//! `HttpClient` details that show up in Native's output: status code names in error texts,
//! System.Text.Json escaping of request bodies/headers, `Uri.EscapeDataString`, the exact
//! `MultipartFormDataContent` layout, and `HttpRequestException` messages.

use std::error::Error as _;
use std::ffi::OsStr;

use super::ioerr::{NetError, NetErrorKind};
use super::netstr::wide;
use super::win::format_message;

/// `((HttpStatusCode)code).ToString()` (table generated from .NET 10).
pub fn status_name(code: u16) -> String {
    let name = match code {
        100 => "Continue",
        101 => "SwitchingProtocols",
        102 => "Processing",
        103 => "EarlyHints",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        203 => "NonAuthoritativeInformation",
        204 => "NoContent",
        205 => "ResetContent",
        206 => "PartialContent",
        207 => "MultiStatus",
        208 => "AlreadyReported",
        226 => "IMUsed",
        300 => "MultipleChoices",
        301 => "MovedPermanently",
        302 => "Found",
        303 => "SeeOther",
        304 => "NotModified",
        305 => "UseProxy",
        306 => "Unused",
        307 => "RedirectKeepVerb",
        308 => "PermanentRedirect",
        400 => "BadRequest",
        401 => "Unauthorized",
        402 => "PaymentRequired",
        403 => "Forbidden",
        404 => "NotFound",
        405 => "MethodNotAllowed",
        406 => "NotAcceptable",
        407 => "ProxyAuthenticationRequired",
        408 => "RequestTimeout",
        409 => "Conflict",
        410 => "Gone",
        411 => "LengthRequired",
        412 => "PreconditionFailed",
        413 => "RequestEntityTooLarge",
        414 => "RequestUriTooLong",
        415 => "UnsupportedMediaType",
        416 => "RequestedRangeNotSatisfiable",
        417 => "ExpectationFailed",
        421 => "MisdirectedRequest",
        422 => "UnprocessableEntity",
        423 => "Locked",
        424 => "FailedDependency",
        426 => "UpgradeRequired",
        428 => "PreconditionRequired",
        429 => "TooManyRequests",
        431 => "RequestHeaderFieldsTooLarge",
        451 => "UnavailableForLegalReasons",
        500 => "InternalServerError",
        501 => "NotImplemented",
        502 => "BadGateway",
        503 => "ServiceUnavailable",
        504 => "GatewayTimeout",
        505 => "HttpVersionNotSupported",
        506 => "VariantAlsoNegotiates",
        507 => "InsufficientStorage",
        508 => "LoopDetected",
        510 => "NotExtended",
        511 => "NetworkAuthenticationRequired",
        _ => return code.to_string(),
    };
    name.to_owned()
}

fn push_u_escape(out: &mut String, unit: u16) {
    out.push_str(&format!("\\u{unit:04X}"));
}

/// A JSON string literal as `JsonSerializer.Serialize` writes it with the default
/// `JavaScriptEncoder`: non-ASCII and HTML-sensitive characters (`" & ' + < > \``) become
/// `\uXXXX` (upper-case hex), control characters use the short escapes where JSON has them,
/// and unpaired surrogates are replaced by U+FFFD.
pub fn stj_string(s: &OsStr) -> String {
    let units = wide(s);
    let mut out = String::with_capacity(units.len() + 2);
    out.push('"');
    let mut i = 0;
    while i < units.len() {
        let u = units[i];
        match u {
            0x08 => out.push_str("\\b"),
            0x09 => out.push_str("\\t"),
            0x0A => out.push_str("\\n"),
            0x0C => out.push_str("\\f"),
            0x0D => out.push_str("\\r"),
            0x5C => out.push_str("\\\\"),
            0x22 | 0x26 | 0x27 | 0x2B | 0x3C | 0x3E | 0x60 => push_u_escape(&mut out, u),
            0x20..=0x7E => out.push(u as u8 as char),
            0xD800..=0xDBFF if i + 1 < units.len() && (0xDC00..=0xDFFF).contains(&units[i + 1]) => {
                push_u_escape(&mut out, u);
                push_u_escape(&mut out, units[i + 1]);
                i += 1;
            }
            0xD800..=0xDFFF => push_u_escape(&mut out, 0xFFFD),
            _ => push_u_escape(&mut out, u),
        }
        i += 1;
    }
    out.push('"');
    out
}

/// `Uri.EscapeDataString`: RFC 3986 unreserved characters stay, everything else is
/// percent-encoded from UTF-8 with upper-case hex.
pub fn escape_data_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

/// `HttpRuleParser.IsTokenChar`.
fn is_token_char(c: u16) -> bool {
    (33..127).contains(&c) && !b"()<>@,;:\\\"/[]?={}".contains(&(c as u8))
}

/// `ContentDispositionHeaderValue.EncodeAndQuoteMime`: RFC 2047 for non-ASCII, quotes when the
/// value is not a token.
fn encode_and_quote_mime(value: &str) -> String {
    let units: Vec<u16> = value.encode_utf16().collect();
    if units.iter().any(|&u| u > 0x7F) {
        use base64::Engine as _;
        let b64 = base64::engine::general_purpose::STANDARD.encode(value.as_bytes());
        return format!("\"=?utf-8?B?{b64}?=\"");
    }
    if !units.is_empty() && units.iter().all(|&u| is_token_char(u)) {
        value.to_owned()
    } else {
        format!("\"{value}\"")
    }
}

/// `HeaderUtilities.Encode5987` (`filename*=utf-8''...`): the UTF-8 bytes of the value
/// (an unpaired surrogate becomes U+FFFD), with everything but attr-chars percent-encoded.
fn encode_5987(value: &OsStr) -> String {
    let mut out = String::from("utf-8''");
    for b in value.to_string_lossy().bytes() {
        let u = b as u16;
        if b > 0x7F || !is_token_char(u) || b == b'*' || b == b'\'' || b == b'%' {
            out.push_str(&format!("%{b:02X}"));
        } else {
            out.push(b as char);
        }
    }
    out
}

/// A part header block as `MultipartFormDataContent.Add` writes it.
pub struct PartHeaders {
    pub content_type: String,
    pub name: String,
    pub file_name: Option<std::ffi::OsString>,
}

impl PartHeaders {
    pub fn render(&self) -> String {
        let mut disposition = format!("form-data; name={}", encode_and_quote_mime(&self.name));
        if let Some(f) = &self.file_name {
            // A file name with an unpaired surrogate is turned into U+FFFD by .NET's encoder.
            let lossy = f.to_string_lossy();
            disposition.push_str(&format!("; filename={}; filename*={}", encode_and_quote_mime(&lossy), encode_5987(f)));
        }
        format!("Content-Type: {}\r\nContent-Disposition: {}\r\n\r\n", self.content_type, disposition)
    }
}

/// The boundary .NET generates (`Guid.NewGuid().ToString()`), and the header value it sends.
pub fn new_boundary() -> String {
    uuid::Uuid::new_v4().hyphenated().to_string()
}

pub fn multipart_content_type(boundary: &str) -> String {
    format!("multipart/form-data; boundary=\"{boundary}\"")
}

/// Text decoding of a response body like `HttpContent.ReadAsStringAsync`: the charset from
/// `Content-Type` when it is UTF-8 or absent (with BOM detection), lossy otherwise.
pub fn decode_body(bytes: &[u8]) -> String {
    let b = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    String::from_utf8_lossy(b).into_owned()
}

/// The `HttpRequestException` (or other) message `HttpClient` produces for a transport
/// failure. Socket errors read "{Windows message} ({host}:{port})", exactly like .NET.
pub fn transport_error(e: &reqwest::Error, url: &reqwest::Url) -> NetError {
    if e.is_timeout() {
        return NetError::canceled();
    }
    let host = url.host_str().unwrap_or("").to_owned();
    let port = url.port_or_known_default().unwrap_or(0);
    let mut src: Option<&dyn std::error::Error> = e.source();
    while let Some(s) = src {
        if let Some(io) = s.downcast_ref::<std::io::Error>() {
            if let Some(code) = io.raw_os_error() {
                return NetError::new(
                    NetErrorKind::HttpRequest,
                    format!("{} ({host}:{port})", format_message(code as u32)),
                );
            }
        }
        let text = s.to_string().to_ascii_lowercase();
        if text.contains("dns error") || text.contains("failed to lookup address") {
            return NetError::new(
                NetErrorKind::HttpRequest,
                format!("{} ({host}:{port})", format_message(11001)),
            );
        }
        if text.contains("tls") || text.contains("certificate") || text.contains("handshake") {
            return NetError::new(
                NetErrorKind::HttpRequest,
                "The SSL connection could not be established, see inner exception.",
            );
        }
        src = s.source();
    }
    if e.is_connect() {
        return NetError::new(
            NetErrorKind::HttpRequest,
            format!("{} ({host}:{port})", format_message(10061)),
        );
    }
    if e.is_body() || e.is_request() {
        return NetError::new(NetErrorKind::HttpRequest, "Error while copying content to a stream.");
    }
    NetError::new(NetErrorKind::HttpRequest, "An error occurred while sending the request.")
}

/// `HttpClient`'s validation of a request URI given as a string
/// (`new Uri(s, UriKind.RelativeOrAbsolute)` + "must be absolute" + scheme check).
pub fn parse_request_uri(s: &str) -> Result<reqwest::Url, NetError> {
    let has_scheme = s.find(':').is_some_and(|i| {
        i > 0 && s[..i].chars().next().is_some_and(|c| c.is_ascii_alphabetic())
            && s[..i].chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'))
    });
    if !has_scheme {
        return Err(NetError::new(
            NetErrorKind::InvalidOperation,
            "An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set.",
        ));
    }
    match reqwest::Url::parse(s) {
        Ok(u) => {
            let scheme = u.scheme().to_ascii_lowercase();
            if scheme != "http" && scheme != "https" {
                return Err(NetError::new(
                    NetErrorKind::Generic,
                    format!("The '{scheme}' scheme is not supported."),
                ));
            }
            if u.host_str().is_none_or(str::is_empty) {
                return Err(NetError::new(NetErrorKind::Generic, "Invalid URI: The hostname could not be parsed."));
            }
            Ok(u)
        }
        Err(url::ParseError::EmptyHost) | Err(url::ParseError::InvalidDomainCharacter) => {
            Err(NetError::new(NetErrorKind::Generic, "Invalid URI: The hostname could not be parsed."))
        }
        Err(url::ParseError::InvalidPort) => {
            Err(NetError::new(NetErrorKind::Generic, "Invalid URI: Invalid port specified."))
        }
        Err(_) => Err(NetError::new(NetErrorKind::Generic, "Invalid URI: The format of the URI could not be determined.")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stj_escaping_matches_dotnet() {
        // Verified against JsonSerializer.Serialize on .NET 10.
        let u = |h: &str| format!("{}u{}", '\\', h);
        let expected = format!(
            "\"/VideoConverter/{}{}b{}{}{}{}{}{}{}{} {}x{}.mp4\"",
            u("00C4"),
            u("003C"),
            u("003E"),
            u("0026"),
            u("0027"),
            u("002B"),
            u("0060"),
            u("00E9"),
            u("D83C"),
            u("DFAC"),
            u("0022"),
            u("0022")
        );
        assert_eq!(stj_string(OsStr::new("/VideoConverter/\u{C4}<b>&'+`\u{E9}\u{1F3AC} \"x\".mp4")), expected);
        let expected2 = format!("\"a{0}b{0}n{0}t{1}{2}/{3}\"", '\\', u("0001"), u("007F"), u("20AC"));
        assert_eq!(stj_string(OsStr::new("a\u{8}\n\t\u{1}\u{7f}/\u{20AC}")), expected2);
        let lone = crate::dotnet::netstr::from_wide(&[0x61, 0xD83D, 0x62]);
        assert_eq!(stj_string(&lone), format!("\"a{}b\"", u("FFFD")));
        assert_eq!(stj_string(OsStr::new("a\\b")), r#""a\\b""#);
    }

    #[test]
    fn escape_data_string_matches_dotnet() {
        assert_eq!(
            escape_data_string("https://www.youtube.com/watch?v=a b&x=ü~*'()!"),
            "https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Da%20b%26x%3D%C3%BC~%2A%27%28%29%21"
        );
    }

    #[test]
    fn multipart_headers_match_dotnet() {
        let a = PartHeaders { content_type: "application/json; charset=utf-8".into(), name: "payload_json".into(), file_name: None };
        assert_eq!(a.render(), "Content-Type: application/json; charset=utf-8\r\nContent-Disposition: form-data; name=payload_json\r\n\r\n");
        let b = PartHeaders {
            content_type: "video/mp4".into(),
            name: "files[0]".into(),
            file_name: Some("Mein Vidéo (1).mp4".into()),
        };
        assert_eq!(
            b.render(),
            "Content-Type: video/mp4\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=\"=?utf-8?B?TWVpbiBWaWTDqW8gKDEpLm1wNA==?=\"; filename*=utf-8''Mein%20Vid%C3%A9o%20%281%29.mp4\r\n\r\n"
        );
    }

    #[test]
    fn status_names() {
        assert_eq!(status_name(401), "Unauthorized");
        assert_eq!(status_name(429), "TooManyRequests");
        assert_eq!(status_name(307), "RedirectKeepVerb");
        assert_eq!(status_name(599), "599");
    }

    #[test]
    fn request_uri_validation() {
        assert!(parse_request_uri("https://discord.com/api/webhooks/1/x").is_ok());
        assert_eq!(
            parse_request_uri("discord.com/api/webhooks/1/x").unwrap_err().message,
            std::ffi::OsString::from("An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set.")
        );
        assert_eq!(
            parse_request_uri("ftp://x.org/a").unwrap_err().message,
            std::ffi::OsString::from("The 'ftp' scheme is not supported.")
        );
    }
}
