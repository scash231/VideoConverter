//! OAuth for YouTube uploads, compatible with Legacy's google-auth files.
//!
//! * `client_secret*.json`: Google's "installed" (or "web") client JSON.
//! * `token.json`: google-auth `Credentials.to_json()` ("authorized user" format), so a token
//!   created by Legacy works in Ferrite and the other way round.
//! * A missing or unusable token starts the installed-app flow: the default browser opens
//!   Google's consent page, a loopback listener on `http://localhost:{port}/` receives the
//!   code, and PKCE (S256) protects the exchange.

use std::ffi::{OsStr, OsString};
use std::time::{Duration, SystemTime};

use base64::Engine as _;
use serde_json::Value;
use sha2::{Digest, Sha256};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio_util::sync::CancellationToken;

use super::time::{format_expiry, parse_expiry};
use crate::dotnet::fs::{create_directory, file_exists, get_files};
use crate::dotnet::http::{decode_body, escape_data_string, transport_error};
use crate::dotnet::ioerr::NetError;
use crate::dotnet::path::{combine, get_file_name};
use crate::dotnet::win::shell_open;
use crate::ncat;

pub const SCOPE: &str = "https://www.googleapis.com/auth/youtube.upload";
const DEFAULT_AUTH_URI: &str = "https://accounts.google.com/o/oauth2/auth";
const DEFAULT_TOKEN_URI: &str = "https://oauth2.googleapis.com/token";
/// google-auth's `REFRESH_THRESHOLD` (3 min 45 s).
const REFRESH_THRESHOLD: Duration = Duration::from_secs(225);
pub const SUCCESS_PAGE: &str = "The authentication flow has completed. You may close this window.";

#[derive(Debug, Clone, PartialEq)]
pub struct ClientSecret {
    pub client_id: String,
    pub client_secret: String,
    pub auth_uri: String,
    pub token_uri: String,
}

/// The fields of google-auth's authorized-user JSON.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StoredToken {
    pub token: Option<String>,
    pub refresh_token: Option<String>,
    pub token_uri: Option<String>,
    pub client_id: Option<String>,
    pub client_secret: Option<String>,
    pub scopes: Option<Vec<String>>,
    pub rapt_token: Option<String>,
    pub universe_domain: Option<String>,
    pub account: Option<String>,
    pub expiry: Option<SystemTime>,
}

/// The first file named `client_secret*.json` in `dir` (directory order, like `os.listdir`).
pub fn find_client_secret(dir: &OsStr) -> Result<OsString, NetError> {
    let files = get_files(dir).unwrap_or_default();
    files
        .into_iter()
        .find(|f| {
            let n = get_file_name(f).to_string_lossy().into_owned();
            n.starts_with("client_secret") && n.ends_with(".json")
        })
        .ok_or_else(|| NetError::generic(ncat!("YouTube upload: client_secret*.json was not found in ", dir)))
}

pub fn parse_client_secret(text: &str) -> Result<ClientSecret, NetError> {
    let v: Value = serde_json::from_str(text).map_err(|_| {
        NetError::generic("client_secret.json does not contain valid JSON. Download the complete OAuth JSON file from the Google Cloud Console.")
    })?;
    let c = v.get("installed").or_else(|| v.get("web")).ok_or_else(|| {
        NetError::generic("client_secret.json is invalid. Download the complete OAuth JSON file from the Google Cloud Console (APIs & Services \u{2192} Credentials \u{2192} OAuth 2.0 Client \u{2192} Download JSON).")
    })?;
    let s = |k: &str| c.get(k).and_then(Value::as_str).map(str::to_owned);
    match (s("client_id"), s("client_secret")) {
        (Some(id), Some(secret)) => Ok(ClientSecret {
            client_id: id,
            client_secret: secret,
            auth_uri: s("auth_uri").unwrap_or_else(|| DEFAULT_AUTH_URI.into()),
            token_uri: s("token_uri").unwrap_or_else(|| DEFAULT_TOKEN_URI.into()),
        }),
        _ => Err(NetError::generic("client_secret.json is missing client_id or client_secret. Download the complete OAuth JSON file from the Google Cloud Console.")),
    }
}

/// `Credentials.from_authorized_user_info`: needs refresh_token, client_id and client_secret.
pub fn parse_token(text: &str) -> Option<StoredToken> {
    let v: Value = serde_json::from_str(text).ok()?;
    let s = |k: &str| v.get(k).and_then(Value::as_str).map(str::to_owned);
    let t = StoredToken {
        token: s("token"),
        refresh_token: s("refresh_token"),
        token_uri: s("token_uri"),
        client_id: s("client_id"),
        client_secret: s("client_secret"),
        scopes: v.get("scopes").and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(str::to_owned).collect()),
        rapt_token: s("rapt_token"),
        universe_domain: s("universe_domain"),
        account: s("account"),
        expiry: s("expiry").and_then(|e| parse_expiry(&e)),
    };
    if t.refresh_token.is_none() || t.client_id.is_none() || t.client_secret.is_none() {
        return None;
    }
    Some(t)
}

/// `json.dumps` string escaping (ensure_ascii=True, lower-case hex).
fn py_str(s: &str) -> String {
    let mut out = String::from("\"");
    for u in s.encode_utf16() {
        match u {
            0x22 => out.push_str("\\\""),
            0x5C => out.push_str("\\\\"),
            0x0A => out.push_str("\\n"),
            0x0D => out.push_str("\\r"),
            0x09 => out.push_str("\\t"),
            0x08 => out.push_str("\\b"),
            0x0C => out.push_str("\\f"),
            0x20..=0x7E => out.push(u as u8 as char),
            _ => out.push_str(&format!("\\u{u:04x}")),
        }
    }
    out.push('"');
    out
}

/// `Credentials.to_json()`: fixed key order, `None` values removed, Python's separators.
pub fn token_to_json(t: &StoredToken) -> String {
    let mut parts: Vec<String> = Vec::new();
    let mut add = |k: &str, v: Option<String>| {
        if let Some(v) = v {
            parts.push(format!("{}: {v}", py_str(k)));
        }
    };
    add("token", t.token.as_deref().map(py_str));
    add("refresh_token", t.refresh_token.as_deref().map(py_str));
    add("token_uri", t.token_uri.as_deref().map(py_str));
    add("client_id", t.client_id.as_deref().map(py_str));
    add("client_secret", t.client_secret.as_deref().map(py_str));
    add("scopes", t.scopes.as_ref().map(|s| format!("[{}]", s.iter().map(|x| py_str(x)).collect::<Vec<_>>().join(", "))));
    add("rapt_token", t.rapt_token.as_deref().map(py_str));
    add("universe_domain", t.universe_domain.as_deref().map(py_str));
    add("account", t.account.as_deref().map(py_str));
    add("expiry", t.expiry.map(|e| py_str(&format_expiry(e))));
    format!("{{{}}}", parts.join(", "))
}

/// `Credentials.valid`: a token that is not within the refresh threshold of its expiry.
pub fn is_valid(t: &StoredToken, now: SystemTime) -> bool {
    if t.token.is_none() {
        return false;
    }
    match t.expiry {
        None => true,
        Some(exp) => match exp.checked_sub(REFRESH_THRESHOLD) {
            Some(skewed) => now < skewed,
            None => false,
        },
    }
}

fn form(pairs: &[(&str, &str)]) -> String {
    pairs.iter().map(|(k, v)| format!("{k}={}", escape_data_string(v))).collect::<Vec<_>>().join("&")
}

/// Token endpoint failure: (OAuth error code, human-readable message).
#[derive(Debug)]
pub struct TokenError {
    pub code: Option<String>,
    pub error: NetError,
}

async fn token_request(http: &reqwest::Client, token_uri: &str, body: String, ct: &CancellationToken) -> Result<Value, TokenError> {
    let url = reqwest::Url::parse(token_uri).map_err(|_| TokenError { code: None, error: NetError::generic(format!("Invalid token_uri: {token_uri}")) })?;
    let req = http
        .post(url.clone())
        .header(reqwest::header::CONTENT_TYPE, "application/x-www-form-urlencoded")
        .body(body)
        .send();
    let resp = tokio::select! {
        r = req => r.map_err(|e| TokenError { code: None, error: transport_error(&e, &url) })?,
        _ = ct.cancelled() => return Err(TokenError { code: None, error: NetError::canceled() }),
    };
    let status = resp.status();
    let text = resp.bytes().await.map(|b| decode_body(&b)).map_err(|e| TokenError { code: None, error: transport_error(&e, &url) })?;
    let v: Value = serde_json::from_str(&text).unwrap_or(Value::Null);
    if !status.is_success() {
        let code = v.get("error").and_then(Value::as_str).map(str::to_owned);
        let desc = v.get("error_description").and_then(Value::as_str).unwrap_or("");
        let msg = match &code {
            Some(c) if !desc.is_empty() => format!("Google sign-in failed ({c}): {desc}"),
            Some(c) => format!("Google sign-in failed ({c})."),
            None => format!("Google sign-in failed (HTTP {}): {text}", status.as_u16()),
        };
        return Err(TokenError { code, error: NetError::generic(msg) });
    }
    Ok(v)
}

fn apply_token_response(t: &mut StoredToken, v: &Value) {
    if let Some(a) = v.get("access_token").and_then(Value::as_str) {
        t.token = Some(a.to_owned());
    }
    if let Some(r) = v.get("refresh_token").and_then(Value::as_str) {
        t.refresh_token = Some(r.to_owned());
    }
    t.expiry = v
        .get("expires_in")
        .and_then(Value::as_u64)
        .map(|secs| SystemTime::now() + Duration::from_secs(secs));
    if let Some(s) = v.get("scope").and_then(Value::as_str) {
        t.scopes = Some(s.split(' ').filter(|x| !x.is_empty()).map(str::to_owned).collect());
    }
}

/// `creds.refresh(Request())`.
pub async fn refresh(http: &reqwest::Client, t: &mut StoredToken, ct: &CancellationToken) -> Result<(), TokenError> {
    let uri = t.token_uri.clone().unwrap_or_else(|| DEFAULT_TOKEN_URI.into());
    let body = form(&[
        ("grant_type", "refresh_token"),
        ("client_id", t.client_id.as_deref().unwrap_or_default()),
        ("client_secret", t.client_secret.as_deref().unwrap_or_default()),
        ("refresh_token", t.refresh_token.as_deref().unwrap_or_default()),
    ]);
    let v = token_request(http, &uri, body, ct).await?;
    apply_token_response(t, &v);
    Ok(())
}

fn random_string(len: usize) -> String {
    let mut bytes = Vec::with_capacity(len);
    while bytes.len() < len {
        bytes.extend_from_slice(uuid::Uuid::new_v4().as_bytes());
    }
    let b64 = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(&bytes);
    b64.chars().take(len).collect()
}

pub fn pkce_challenge(verifier: &str) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()))
}

pub fn authorization_url(client: &ClientSecret, redirect_uri: &str, state: &str, challenge: &str) -> String {
    format!(
        "{}?{}",
        client.auth_uri,
        form(&[
            ("response_type", "code"),
            ("client_id", &client.client_id),
            ("redirect_uri", redirect_uri),
            ("scope", SCOPE),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("access_type", "offline"),
            ("prompt", "consent"),
        ])
    )
}

/// Query parameters of the request line `GET /?a=b&c=d HTTP/1.1`.
pub fn parse_callback(request: &str) -> Option<(String, Vec<(String, String)>)> {
    let line = request.lines().next()?;
    let mut it = line.split(' ');
    let _method = it.next()?;
    let target = it.next()?;
    let (path, query) = target.split_once('?').unwrap_or((target, ""));
    let params = query
        .split('&')
        .filter(|p| !p.is_empty())
        .map(|p| {
            let (k, v) = p.split_once('=').unwrap_or((p, ""));
            (pct_decode(k), pct_decode(v))
        })
        .collect();
    Some((path.to_owned(), params))
}

fn pct_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    let hex = |c: u8| (c as char).to_digit(16).map(|d| d as u8);
    while i < b.len() {
        match b[i] {
            b'+' => out.push(b' '),
            b'%' if i + 2 < b.len() => match (hex(b[i + 1]), hex(b[i + 2])) {
                (Some(h), Some(l)) => {
                    out.push(h * 16 + l);
                    i += 2;
                }
                _ => out.push(b'%'),
            },
            c => out.push(c),
        }
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

async fn respond(stream: &mut tokio::net::TcpStream, status: &str, body: &str) {
    let resp = format!(
        "HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    );
    let _ = stream.write_all(resp.as_bytes()).await;
    let _ = stream.shutdown().await;
}

/// Runs the browser sign-in and returns the authorization code.
async fn wait_for_code(listeners: Vec<TcpListener>, state: &str, ct: &CancellationToken) -> Result<String, NetError> {
    loop {
        let accept = futures_util::future::select_all(listeners.iter().map(|l| Box::pin(l.accept())));
        let (res, _, _) = tokio::select! {
            r = accept => r,
            _ = ct.cancelled() => return Err(NetError::canceled()),
        };
        let Ok((mut stream, _)) = res else { continue };
        let mut buf = vec![0u8; 8192];
        let n = tokio::select! {
            r = stream.read(&mut buf) => r.unwrap_or(0),
            _ = ct.cancelled() => return Err(NetError::canceled()),
        };
        let req = String::from_utf8_lossy(&buf[..n]).into_owned();
        let Some((path, params)) = parse_callback(&req) else { continue };
        if path != "/" {
            respond(&mut stream, "404 Not Found", "Not found").await;
            continue;
        }
        let get = |k: &str| params.iter().find(|(a, _)| a == k).map(|(_, v)| v.clone());
        if let Some(err) = get("error") {
            respond(&mut stream, "200 OK", "Google sign-in was cancelled. You may close this window.").await;
            return Err(NetError::generic(format!("Google sign-in was cancelled or denied ({err}).")));
        }
        if get("state").as_deref() != Some(state) {
            respond(&mut stream, "400 Bad Request", "Invalid state.").await;
            return Err(NetError::generic("Google sign-in failed: the response did not match the request (state mismatch)."));
        }
        match get("code") {
            Some(code) => {
                respond(&mut stream, "200 OK", SUCCESS_PAGE).await;
                return Ok(code);
            }
            None => respond(&mut stream, "400 Bad Request", "Missing code.").await,
        }
    }
}

/// `InstalledAppFlow.run_local_server(port=0, open_browser=True)`.
pub async fn interactive_sign_in(
    http: &reqwest::Client,
    client: &ClientSecret,
    on_url: impl Fn(&str),
    ct: &CancellationToken,
) -> Result<StoredToken, NetError> {
    let v4 = TcpListener::bind("127.0.0.1:0").await.map_err(|e| NetError::generic(format!("Could not start the local sign-in listener: {e}")))?;
    let port = v4.local_addr().map_err(|e| NetError::generic(e.to_string()))?.port();
    let mut listeners = vec![v4];
    if let Ok(v6) = TcpListener::bind(("::1", port)).await {
        listeners.push(v6);
    }
    let redirect_uri = format!("http://localhost:{port}/");
    let verifier = random_string(64);
    let state = random_string(30);
    let url = authorization_url(client, &redirect_uri, &state, &pkce_challenge(&verifier));
    on_url(&url);
    shell_open(OsStr::new(&url));
    let code = wait_for_code(listeners, &state, ct).await?;
    let body = form(&[
        ("grant_type", "authorization_code"),
        ("code", &code),
        ("client_id", &client.client_id),
        ("client_secret", &client.client_secret),
        ("redirect_uri", &redirect_uri),
        ("code_verifier", &verifier),
    ]);
    let v = token_request(http, &client.token_uri, body, ct).await.map_err(|e| e.error)?;
    let mut t = StoredToken {
        token_uri: Some(client.token_uri.clone()),
        client_id: Some(client.client_id.clone()),
        client_secret: Some(client.client_secret.clone()),
        scopes: Some(vec![SCOPE.into()]),
        universe_domain: Some("googleapis.com".into()),
        account: Some(String::new()),
        ..Default::default()
    };
    apply_token_response(&mut t, &v);
    if t.token.is_none() {
        return Err(NetError::generic("Google sign-in failed: the token response did not contain an access token."));
    }
    Ok(t)
}

pub fn token_path(dir: &OsStr) -> OsString {
    combine(dir, "token.json")
}

pub fn read_token(dir: &OsStr) -> Option<StoredToken> {
    let p = token_path(dir);
    if !file_exists(&p) {
        return None;
    }
    std::fs::read(&p).ok().and_then(|b| parse_token(&crate::proc::decode::decode_all(&b)))
}

pub fn write_token(dir: &OsStr, t: &StoredToken) -> Result<(), NetError> {
    create_directory(dir)?;
    let p = token_path(dir);
    std::fs::write(&p, token_to_json(t)).map_err(|e| crate::dotnet::ioerr::from_io(&e, Some(&p)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::UNIX_EPOCH;

    #[test]
    fn token_json_round_trip_in_google_auth_format() {
        let t = StoredToken {
            token: Some("ya29.a".into()),
            refresh_token: Some("1//r".into()),
            token_uri: Some(DEFAULT_TOKEN_URI.into()),
            client_id: Some("id.apps.googleusercontent.com".into()),
            client_secret: Some("sec".into()),
            scopes: Some(vec![SCOPE.into()]),
            rapt_token: None,
            universe_domain: Some("googleapis.com".into()),
            account: Some(String::new()),
            expiry: Some(UNIX_EPOCH + Duration::from_secs(1_791_547_200) + Duration::from_micros(5)),
        };
        let j = token_to_json(&t);
        assert_eq!(
            j,
            r#"{"token": "ya29.a", "refresh_token": "1//r", "token_uri": "https://oauth2.googleapis.com/token", "client_id": "id.apps.googleusercontent.com", "client_secret": "sec", "scopes": ["https://www.googleapis.com/auth/youtube.upload"], "universe_domain": "googleapis.com", "account": "", "expiry": "2026-10-09T12:00:00.000005Z"}"#
        );
        let back = parse_token(&j).unwrap();
        assert_eq!(back.expiry, Some(UNIX_EPOCH + Duration::from_secs(1_791_547_200)));
        assert_eq!(back.scopes, t.scopes);
        assert!(parse_token(r#"{"token":"x"}"#).is_none());
        assert!(parse_token("PLACEHOLDER").is_none());
    }

    #[test]
    fn validity_uses_refresh_threshold() {
        let now = UNIX_EPOCH + Duration::from_secs(1_000_000);
        let mut t = StoredToken { token: Some("a".into()), ..Default::default() };
        assert!(is_valid(&t, now));
        t.expiry = Some(now + Duration::from_secs(226));
        assert!(is_valid(&t, now));
        t.expiry = Some(now + Duration::from_secs(225));
        assert!(!is_valid(&t, now));
        t.token = None;
        assert!(!is_valid(&t, now));
    }

    #[test]
    fn client_secret_parsing() {
        let c = parse_client_secret(r#"{"installed":{"client_id":"a","client_secret":"b","token_uri":"https://t"}}"#).unwrap();
        assert_eq!(c.auth_uri, DEFAULT_AUTH_URI);
        assert_eq!(c.token_uri, "https://t");
        assert!(parse_client_secret("PLACEHOLDER").is_err());
        assert!(parse_client_secret(r#"{"other":{}}"#).is_err());
    }

    #[test]
    fn callback_and_url() {
        let (p, q) = parse_callback("GET /?state=s1&code=4%2F0Ab&scope=x+y HTTP/1.1\r\nHost: localhost\r\n").unwrap();
        assert_eq!(p, "/");
        assert_eq!(q, vec![("state".into(), "s1".into()), ("code".into(), "4/0Ab".into()), ("scope".into(), "x y".into())]);
        let c = ClientSecret { client_id: "id".into(), client_secret: "s".into(), auth_uri: DEFAULT_AUTH_URI.into(), token_uri: DEFAULT_TOKEN_URI.into() };
        let u = authorization_url(&c, "http://localhost:1234/", "st", "ch");
        assert!(u.starts_with("https://accounts.google.com/o/oauth2/auth?response_type=code&client_id=id&redirect_uri=http%3A%2F%2Flocalhost%3A1234%2F&scope="));
        assert!(u.contains("&code_challenge_method=S256&access_type=offline&prompt=consent"));
        assert_eq!(pkce_challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }
}
