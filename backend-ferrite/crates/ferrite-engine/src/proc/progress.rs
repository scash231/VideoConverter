//! Progress parsing from `NativeEngineService` (`YtDlpProgressRegex`, `ParseEta`,
//! `ProgressNumberRegex`) and the ffmpeg `-progress pipe:1` reader.

use std::sync::LazyLock;

use regex::Regex;

/// .NET `\w` is `[\p{L}\p{Mn}\p{Nd}\p{Pc}]` (Rust's `\w` is broader).
const NET_W: &str = r"[\p{L}\p{Mn}\p{Nd}\p{Pc}]";

/// `YtDlpProgressRegex` (RegexOptions.IgnoreCase).
pub static YT_DLP_PROGRESS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(&format!(
        r"(?i)\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+{w}+)(?:\s+at\s+([\d.]+{w}+/s))?(?:\s+ETA\s+([\d:]+))?",
        w = NET_W
    ))
    .expect("valid regex")
});

/// `ProgressNumberRegex` (`[\d.,]+\s*%?`).
pub static PROGRESS_NUMBER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[\d.,]+\s*%?").expect("valid regex"));

/// A parsed yt-dlp progress line.
#[derive(Debug, Clone, PartialEq)]
pub struct YtDlpProgress {
    pub percent: f64,
    pub speed: Option<String>,
    pub eta: Option<i32>,
}

/// The `OutputDataReceived` handler's parsing step: regex match plus
/// `double.TryParse(g1, NumberStyles.Any, CultureInfo.InvariantCulture)`.
pub fn parse_yt_dlp_progress(line: &str) -> Option<YtDlpProgress> {
    let caps = YT_DLP_PROGRESS.captures(line)?;
    let percent = parse_double_invariant_any(caps.get(1)?.as_str())?;
    let speed = caps.get(3).map(|m| m.as_str().to_owned());
    let eta = parse_eta(caps.get(4).map(|m| m.as_str()));
    Some(YtDlpProgress { percent, speed, eta })
}

/// `double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture)` for the inputs the
/// regex can produce (`[\d.]+`): ASCII digits with at most one decimal point. Unicode digits
/// matched by `\d` are rejected by .NET's parser as well.
pub fn parse_double_invariant_any(s: &str) -> Option<f64> {
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_digit() || b == b'.') {
        return None;
    }
    if s.bytes().filter(|&b| b == b'.').count() > 1 || !s.bytes().any(|b| b.is_ascii_digit()) {
        return None;
    }
    s.parse::<f64>().ok()
}

/// `int.TryParse(string)`: optional surrounding whitespace (U+0009..U+000D, U+0020), optional
/// sign, ASCII digits, no overflow.
pub fn parse_dotnet_i32(s: &str) -> Option<i32> {
    parse_dotnet_i64(s).and_then(|v| i32::try_from(v).ok())
}

/// `long.TryParse(...)` with `NumberStyles.Integer`.
pub fn parse_dotnet_i64(s: &str) -> Option<i64> {
    let is_ws = |c: char| matches!(c, '\u{9}'..='\u{D}' | ' ');
    let t = s.trim_matches(is_ws);
    let (neg, digits) = match t.as_bytes().first() {
        Some(b'-') => (true, &t[1..]),
        Some(b'+') => (false, &t[1..]),
        _ => (false, t),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let mut v: i128 = 0;
    for b in digits.bytes() {
        v = v * 10 + (b - b'0') as i128;
        if v > i64::MAX as i128 + 1 {
            return None;
        }
    }
    let v = if neg { -v } else { v };
    i64::try_from(v).ok()
}

/// `ParseEta`: `m:s` or `h:m:s` into seconds (C# int arithmetic wraps on overflow).
pub fn parse_eta(eta: Option<&str>) -> Option<i32> {
    let s = eta?;
    if s.chars().all(char::is_whitespace) {
        return None;
    }
    let parts: Vec<&str> = s.split(':').collect();
    match parts.as_slice() {
        [m, sec] => {
            let (m, sec) = (parse_dotnet_i32(m)?, parse_dotnet_i32(sec)?);
            Some(m.wrapping_mul(60).wrapping_add(sec))
        }
        [h, m, sec] => {
            let (h, m, sec) = (parse_dotnet_i32(h)?, parse_dotnet_i32(m)?, parse_dotnet_i32(sec)?);
            Some(h.wrapping_mul(3600).wrapping_add(m.wrapping_mul(60)).wrapping_add(sec))
        }
        _ => None,
    }
}

/// The ffmpeg progress reader: `line.StartsWith("out_time_us=")` followed by `long.TryParse`.
/// Returns the fraction of `duration` when `us > 0 && duration > 0`.
pub fn parse_ffmpeg_out_time(line: &str, duration: f64) -> Option<f64> {
    let rest = line.strip_prefix("out_time_us=")?;
    let us = parse_dotnet_i64(rest)?;
    if us > 0 && duration > 0.0 {
        Some((us as f64 / 1_000_000.0 / duration).min(1.0))
    } else {
        None
    }
}

/// `status + "|" + ProgressNumberRegex.Replace(message, "")`: the key used to log a status
/// line only when the phase changes.
pub fn phase_key(status: &str, message: &str) -> String {
    format!("{status}|{}", PROGRESS_NUMBER.replace_all(message, ""))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yt_dlp_progress_lines() {
        let p = parse_yt_dlp_progress("[download]  45.3% of   12.34MiB at    1.23MiB/s ETA 00:07").unwrap();
        assert_eq!(p, YtDlpProgress { percent: 45.3, speed: Some("1.23MiB/s".into()), eta: Some(7) });
        let p = parse_yt_dlp_progress("[download] 100% of 5.00MiB in 00:01").unwrap();
        assert_eq!(p.percent, 100.0);
        assert_eq!(p.speed, None);
        let p = parse_yt_dlp_progress("[DOWNLOAD]   3.0% of ~5.00MiB at 2.00KiB/s ETA 1:02:03").unwrap();
        assert_eq!(p.eta, Some(3723));
        // "~" followed by padding does not match (same as .NET).
        assert!(parse_yt_dlp_progress("[download]  10.0% of ~  12.34MiB at 1.00MiB/s ETA 00:10").is_none());
        assert!(parse_yt_dlp_progress("[download] Destination: x.mp4").is_none());
        assert!(parse_yt_dlp_progress("[download] 1.2.3% of 5MiB").is_none());
    }

    #[test]
    fn eta_rules() {
        assert_eq!(parse_eta(Some("00:07")), Some(7));
        assert_eq!(parse_eta(Some("1:02:03")), Some(3723));
        assert_eq!(parse_eta(Some("1::2")), None);
        assert_eq!(parse_eta(Some("5")), None);
        assert_eq!(parse_eta(None), None);
    }

    #[test]
    fn ffmpeg_progress() {
        assert_eq!(parse_ffmpeg_out_time("out_time_us=5000000", 10.0), Some(0.5));
        assert_eq!(parse_ffmpeg_out_time("out_time_us=N/A", 10.0), None);
        assert_eq!(parse_ffmpeg_out_time("out_time_us=-9223372036854775807", 10.0), None);
        assert_eq!(parse_ffmpeg_out_time("out_time_us=50000000", 10.0), Some(1.0));
        assert_eq!(parse_ffmpeg_out_time("out_time_us=1", 0.0), None);
        assert_eq!(parse_ffmpeg_out_time("frame=1", 10.0), None);
    }

    #[test]
    fn phase_keys_ignore_numbers() {
        assert_eq!(phase_key("downloading", "[1/2] Download Video: 45,3%"), "downloading|[/] Download Video: ");
        assert_eq!(
            phase_key("downloading", "Download Video: 1,0%"),
            phase_key("downloading", "Download Video: 99,9%")
        );
    }
}
