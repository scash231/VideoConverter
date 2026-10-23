//! `videos.insert` with the resumable upload protocol (8 MiB chunks, like Legacy's
//! `MediaFileUpload(chunksize=8 << 20)`), plus token handling around it.

use std::ffi::OsStr;
use std::sync::Arc;
use std::time::{Duration, SystemTime};

use bytes::Bytes;
use reqwest::header::{AUTHORIZATION, CONTENT_LENGTH, CONTENT_RANGE, CONTENT_TYPE, LOCATION, RANGE};
use serde_json::Value;
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use tokio_util::sync::CancellationToken;

use super::oauth::{find_client_secret, interactive_sign_in, is_valid, parse_client_secret, read_token, refresh, write_token, StoredToken};
use crate::cloud::copy::{file_length, open_read, throttle};
use crate::dotnet::http::{decode_body, transport_error};
use crate::dotnet::ioerr::{from_io, NetError};
use crate::proc::decode::decode_all;

pub const CHUNK_SIZE: u64 = 8 << 20;
const INSERT_URL: &str = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";

/// Callbacks the pipeline provides.
pub struct UploadCallbacks {
    /// Upload progress in percent (throttled to whole percents by the caller's wrapper).
    pub progress: Arc<dyn Fn(f64) + Send + Sync>,
    /// A one-off status message (e.g. "waiting for sign-in").
    pub status: Arc<dyn Fn(&str) + Send + Sync>,
    /// A console line.
    pub log: Arc<dyn Fn(&str) + Send + Sync>,
}

/// `_get_client()`: a valid access token from token.json, refreshed or freshly signed in.
pub async fn access_token(
    http: &reqwest::Client,
    auth_lock: &tokio::sync::Mutex<()>,
    dir: &OsStr,
    force_refresh: bool,
    cb: &UploadCallbacks,
    ct: &CancellationToken,
) -> Result<String, NetError> {
    // One sign-in at a time; later jobs pick up the token the first one saved.
    let _guard = auth_lock.lock().await;
    let secret_path = find_client_secret(dir)?;
    let secret_text = std::fs::read(&secret_path).map(|b| decode_all(&b)).map_err(|e| from_io(&e, Some(&secret_path)))?;
    let client = parse_client_secret(&secret_text)?;

    let mut creds: Option<StoredToken> = read_token(dir);
    if let Some(t) = creds.as_ref() {
        if !force_refresh && is_valid(t, SystemTime::now()) {
            return Ok(t.token.clone().unwrap_or_default());
        }
    }
    let mut fresh: Option<StoredToken> = None;
    if let Some(mut t) = creds.take() {
        if t.refresh_token.is_some() {
            match refresh(http, &mut t, ct).await {
                Ok(()) => fresh = Some(t),
                Err(e) if e.error.is_canceled() => return Err(e.error),
                // An expired or revoked refresh token needs a new sign-in.
                Err(e) if e.code.as_deref() == Some("invalid_grant") => {}
                Err(e) => return Err(e.error),
            }
        }
    }
    let t = match fresh {
        Some(t) => t,
        None => {
            cb.status("Waiting for Google sign-in in your browser...");
            let log = cb.log.clone();
            interactive_sign_in(http, &client, move |url| log(&format!("Please visit this URL to authorize this application: {url}")), ct).await?
        }
    };
    write_token(dir, &t)?;
    Ok(t.token.unwrap_or_default())
}

trait StatusExt {
    fn status(&self, msg: &str);
}

impl StatusExt for UploadCallbacks {
    fn status(&self, msg: &str) {
        (self.status)(msg)
    }
}

/// `HttpError` text: "YouTube API error {code} ({reason}): {message}".
pub fn api_error(status: u16, body: &str) -> NetError {
    let v: Value = serde_json::from_str(body).unwrap_or(Value::Null);
    let e = &v["error"];
    let message = e.get("message").and_then(Value::as_str).unwrap_or(body).trim().to_owned();
    let reason = e.get("errors").and_then(|x| x.get(0)).and_then(|x| x.get("reason")).and_then(Value::as_str).map(str::to_owned);
    let msg = match reason {
        Some(r) => format!("YouTube API error {status} ({r}): {message}"),
        None => format!("YouTube API error {status}: {message}"),
    };
    NetError::generic(msg)
}

async fn send(ct: &CancellationToken, req: reqwest::RequestBuilder, url: &reqwest::Url) -> Result<reqwest::Response, NetError> {
    tokio::select! {
        r = req.send() => r.map_err(|e| transport_error(&e, url)),
        _ = ct.cancelled() => Err(NetError::canceled()),
    }
}

async fn text(ct: &CancellationToken, resp: reqwest::Response, url: &reqwest::Url) -> Result<String, NetError> {
    tokio::select! {
        b = resp.bytes() => b.map(|b| decode_body(&b)).map_err(|e| transport_error(&e, url)),
        _ = ct.cancelled() => Err(NetError::canceled()),
    }
}

async fn start_session(
    http: &reqwest::Client,
    token: &str,
    resource: &Value,
    total: u64,
    ct: &CancellationToken,
) -> Result<Result<reqwest::Url, (u16, String)>, NetError> {
    let url = reqwest::Url::parse(INSERT_URL).expect("valid url");
    let req = http
        .post(url.clone())
        .header(AUTHORIZATION, format!("Bearer {token}"))
        .header(CONTENT_TYPE, "application/json; charset=UTF-8")
        .header("X-Upload-Content-Type", "video/mp4")
        .header("X-Upload-Content-Length", total)
        .body(resource.to_string());
    let resp = send(ct, req, &url).await?;
    let status = resp.status();
    if status.is_success() {
        if let Some(loc) = resp.headers().get(LOCATION).and_then(|h| h.to_str().ok()) {
            if let Ok(u) = reqwest::Url::parse(loc) {
                return Ok(Ok(u));
            }
        }
        return Err(NetError::generic("YouTube API error: the upload session did not return a location."));
    }
    let body = text(ct, resp, &url).await?;
    Ok(Err((status.as_u16(), body)))
}

/// Next byte to send from a 308 response's `Range: bytes=0-N` header.
pub fn next_offset(range: Option<&str>) -> u64 {
    range
        .and_then(|r| r.strip_prefix("bytes="))
        .and_then(|r| r.split('-').nth(1))
        .and_then(|n| n.parse::<u64>().ok())
        .map_or(0, |n| n + 1)
}

/// Uploads `file` and returns the new video id.
pub async fn upload_video(
    http: &reqwest::Client,
    auth_lock: &tokio::sync::Mutex<()>,
    dir: &OsStr,
    file: &OsStr,
    resource: &Value,
    cb: &UploadCallbacks,
    ct: &CancellationToken,
) -> Result<String, NetError> {
    let total = file_length(file)?;
    let mut token = access_token(http, auth_lock, dir, false, cb, ct).await?;
    let session = match start_session(http, &token, resource, total, ct).await? {
        Ok(u) => u,
        Err((401, _)) => {
            token = access_token(http, auth_lock, dir, true, cb, ct).await?;
            match start_session(http, &token, resource, total, ct).await? {
                Ok(u) => u,
                Err((code, body)) => return Err(api_error(code, &body)),
            }
        }
        Err((code, body)) => return Err(api_error(code, &body)),
    };

    let report = throttle(cb.progress.clone());
    let mut src = open_read(file).await?;
    let mut offset: u64 = 0;
    let mut failures = 0u32;
    loop {
        let len = CHUNK_SIZE.min(total - offset);
        src.seek(std::io::SeekFrom::Start(offset)).await.map_err(|e| from_io(&e, Some(file)))?;
        let mut buf = vec![0u8; len as usize];
        src.read_exact(&mut buf).await.map_err(|e| from_io(&e, Some(file)))?;
        let range = if total == 0 { "bytes */0".to_owned() } else { format!("bytes {}-{}/{}", offset, offset + len - 1, total) };
        let req = http
            .put(session.clone())
            .header(AUTHORIZATION, format!("Bearer {token}"))
            .header(CONTENT_LENGTH, len)
            .header(CONTENT_RANGE, range)
            .body(Bytes::from(buf));
        let resp = match send(ct, req, &session).await {
            Ok(r) => r,
            Err(e) if e.is_canceled() => return Err(e),
            Err(e) => {
                failures += 1;
                if failures > 3 {
                    return Err(e);
                }
                cb.status("Connection problem, resuming YouTube upload...");
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(1 << failures)) => {}
                    _ = ct.cancelled() => return Err(NetError::canceled()),
                }
                offset = query_offset(http, &session, &token, total, ct).await?;
                continue;
            }
        };
        let status = resp.status().as_u16();
        match status {
            200 | 201 => {
                report(100.0);
                let body = text(ct, resp, &session).await?;
                let v: Value = serde_json::from_str(&body).unwrap_or(Value::Null);
                return v
                    .get("id")
                    .and_then(Value::as_str)
                    .map(str::to_owned)
                    .ok_or_else(|| NetError::generic("YouTube API error: the upload finished without a video id."));
            }
            308 => {
                failures = 0;
                offset = next_offset(resp.headers().get(RANGE).and_then(|h| h.to_str().ok()));
                if total > 0 {
                    report((offset as f64 / total as f64 * 100.0).min(100.0));
                }
            }
            500..=599 => {
                failures += 1;
                if failures > 3 {
                    let body = text(ct, resp, &session).await?;
                    return Err(api_error(status, &body));
                }
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(1 << failures)) => {}
                    _ = ct.cancelled() => return Err(NetError::canceled()),
                }
                offset = query_offset(http, &session, &token, total, ct).await?;
            }
            _ => {
                let body = text(ct, resp, &session).await?;
                return Err(api_error(status, &body));
            }
        }
    }
}

/// Asks the session how much it received (`Content-Range: bytes */total`).
async fn query_offset(http: &reqwest::Client, session: &reqwest::Url, token: &str, total: u64, ct: &CancellationToken) -> Result<u64, NetError> {
    let req = http
        .put(session.clone())
        .header(AUTHORIZATION, format!("Bearer {token}"))
        .header(CONTENT_LENGTH, 0)
        .header(CONTENT_RANGE, format!("bytes */{total}"));
    let resp = send(ct, req, session).await?;
    Ok(match resp.status().as_u16() {
        308 => next_offset(resp.headers().get(RANGE).and_then(|h| h.to_str().ok())),
        _ => 0,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ranges_and_errors() {
        assert_eq!(next_offset(Some("bytes=0-8388607")), 8_388_608);
        assert_eq!(next_offset(None), 0);
        let e = api_error(403, r#"{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota.","errors":[{"reason":"quotaExceeded"}]}}"#);
        assert_eq!(
            e.message.to_string_lossy(),
            "YouTube API error 403 (quotaExceeded): The request cannot be completed because you have exceeded your quota."
        );
        assert_eq!(api_error(500, "oops").message.to_string_lossy(), "YouTube API error 500: oops");
    }
}
