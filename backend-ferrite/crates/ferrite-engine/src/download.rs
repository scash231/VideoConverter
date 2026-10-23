//! Port of `RetryStrategies`, `DownloadVideoWithRetriesAsync`, `RunYtDlpDownloadAsync` and
//! `GetFormatString`, plus the shared yt-dlp error-text derivation.

use std::ffi::{OsStr, OsString};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::Mutex;
use std::time::Duration;

use tokio_util::sync::CancellationToken;

use crate::cookies::resolve_cookies_file;
use crate::dotnet::fmt::fixed;
use crate::dotnet::ioerr::NetError;
use crate::dotnet::path::combine;
use crate::dotnet::text::{contains_ignore_case, eq_ignore_case, is_blank, to_lower_invariant};
use crate::engine::Engine;
use crate::jobs::{emit, emit_status, JobContext, StatusExtra};
use crate::ncat;
use crate::proc::progress::parse_yt_dlp_progress;
use crate::proc::runner::{run_streaming, ProcSpec};
use crate::util::{clean_stem_files, find_downloaded_file};

/// The `PhasePrefix()` local function: `"[{current}/{total}] "` when there is more than one
/// phase, evaluated whenever a message is built.
pub struct Phase {
    current: AtomicI32,
    total: i32,
}

impl Phase {
    pub fn new(total: i32) -> Self {
        Phase { current: AtomicI32::new(1), total }
    }

    pub fn advance(&self) {
        self.current.fetch_add(1, Ordering::SeqCst);
    }

    pub fn prefix(&self) -> String {
        if self.total > 1 {
            format!("[{}/{}] ", self.current.load(Ordering::SeqCst), self.total)
        } else {
            String::new()
        }
    }
}

/// `RetryStrategies`: (format mode, backoff seconds).
pub const RETRY_STRATEGIES: [(&str, u64); 3] = [("best", 0), ("any", 1), ("progressive", 2)];

const HEIGHTS: [&str; 6] = ["2160", "1440", "1080", "720", "480", "360"];

/// `GetFormatString(quality, isAudio, formatMode)`.
pub fn get_format_string(quality: Option<&str>, is_audio: bool, format_mode: &str) -> String {
    if is_audio {
        return "bestaudio[ext=m4a]/bestaudio/best".into();
    }
    let mut q = to_lower_invariant(quality.unwrap_or("best").trim());
    if q.ends_with('p') && q.encode_utf16().count() > 1 {
        q.pop();
    }
    let valid = HEIGHTS.contains(&q.as_str());
    if format_mode == "progressive" {
        if valid {
            return format!("best[height<={q}]/best");
        }
        return "best[height<=1080]/best".into();
    }
    if format_mode == "any" {
        return "bestvideo*+bestaudio/best".into();
    }
    if valid {
        return format!("bestvideo[height<={q}][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<={q}]+bestaudio/best");
    }
    "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best".into()
}

/// The `--audio-format` / `--audio-quality` pair for the video path's audio modes.
pub fn audio_format_for(quality: &str) -> (&'static str, &'static str) {
    if eq_ignore_case(quality, "flac") {
        ("flac", "0")
    } else if eq_ignore_case(quality, "m4a") {
        ("m4a", "256")
    } else {
        ("mp3", "320")
    }
}

/// The yt-dlp download command line (§5.2 of PORTING.md).
#[allow(clippy::too_many_arguments)]
pub fn download_args(
    extra: &str,
    ffmpeg_location: Option<&OsStr>,
    cookies: Option<&str>,
    format_string: &str,
    output_dir: &OsStr,
    stem: &str,
    is_audio: bool,
    quality: &str,
    url: &OsStr,
) -> OsString {
    let mut a = OsString::new();
    if !extra.is_empty() {
        a.push(format!("{extra} "));
    }
    if let Some(dir) = ffmpeg_location {
        a.push(ncat!("--ffmpeg-location \"", dir, "\" "));
    }
    if let Some(c) = cookies {
        a.push(format!("--cookies \"{c}\" "));
    }
    a.push(format!("--newline -f \"{format_string}\" "));
    a.push(ncat!("-o \"", combine(output_dir, format!("{stem}.%(ext)s")), "\" "));
    if !is_audio {
        a.push("--merge-output-format mp4 ");
    }
    if is_audio {
        let (fmt, q) = audio_format_for(quality);
        a.push(format!("--extract-audio --audio-format {fmt} --audio-quality {q} "));
    }
    a.push("--no-playlist ");
    a.push("--retries 10 --fragment-retries 15 --extractor-retries 5 ");
    a.push("--socket-timeout 20 ");
    a.push("--windows-filenames ");
    a.push(ncat!("\"", url, "\""));
    a
}

/// `.AppendLine` + `.ToString()` text builder (lines end with `\r\n`).
#[derive(Default)]
pub struct TextBuilder(Mutex<String>);

impl TextBuilder {
    pub fn append_line(&self, line: &str) {
        let mut g = self.0.lock().unwrap_or_else(|e| e.into_inner());
        g.push_str(line);
        g.push_str("\r\n");
    }

    pub fn text(&self) -> String {
        self.0.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }
}

/// The error text `RunYtDlpDownloadAsync` throws for a non-zero exit code.
pub fn video_error_text(stderr: &str, all_output: &str, exit_code: i32, runner_exe: &OsStr) -> OsString {
    let mut detail = OsString::from(stderr.trim());
    if is_blank(&detail.to_string_lossy()) {
        let out = all_output.trim();
        if !is_blank(out) && (contains_ignore_case(out, "ERROR:") || contains_ignore_case(out, "Traceback")) {
            detail = OsString::from(out);
        } else if exit_code == 9009 {
            detail = ncat!(
                "yt-dlp or ffmpeg command was not found or failed to spawn (exit code 9009). Runner: ",
                runner_exe
            );
        } else {
            detail = OsString::from(format!("yt-dlp exited with error code {exit_code}"));
        }
    }
    let d = detail.to_string_lossy();
    if contains_ignore_case(&d, "Video unavailable")
        || contains_ignore_case(&d, "This video is not available")
        || contains_ignore_case(&d, "Private video")
    {
        return "Video is unavailable, private, or region-restricted.".into();
    }
    if contains_ignore_case(&d, "Sign in to confirm you\u{2019}re not a bot") || contains_ignore_case(&d, "confirm you're not a bot") {
        return "YouTube verification required. For restricted videos, you can configure an optional cookies file in Settings \u{2192} Advanced \u{2192} Backend.".into();
    }
    detail
}

#[allow(clippy::too_many_arguments)]
pub async fn download_video_with_retries(
    engine: &Engine,
    ctx: &JobContext,
    url: &OsStr,
    output_dir: &OsStr,
    stem: &str,
    quality: &str,
    is_audio: bool,
    phase: &Phase,
    ct: &CancellationToken,
) -> Result<Option<OsString>, NetError> {
    let mut last: Option<NetError> = None;
    for attempt in 0..RETRY_STRATEGIES.len() {
        if ct.is_cancelled() {
            return Err(NetError::canceled());
        }
        let (mode, backoff) = RETRY_STRATEGIES[attempt];
        if backoff > 0 {
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_secs(backoff)) => {}
                _ = ct.cancelled() => return Err(NetError::canceled()),
            }
        }
        let format_string = get_format_string(Some(quality), is_audio, mode);
        match run_ytdlp_download(engine, ctx, url, output_dir, stem, &format_string, quality, is_audio, phase, ct).await {
            Ok(file) => return Ok(file),
            Err(e) if e.is_canceled() => return Err(e),
            Err(e) => {
                last = Some(e);
                clean_stem_files(Some(output_dir), Some(stem), None).await;
                if attempt < RETRY_STRATEGIES.len() - 1 {
                    let next = RETRY_STRATEGIES[attempt + 1].0;
                    emit(
                        engine,
                        ctx,
                        "downloading",
                        0.0,
                        format!("{}Retry {}/{} ({next} format)...", phase.prefix(), attempt + 2, RETRY_STRATEGIES.len()),
                    );
                }
            }
        }
    }
    Err(last.unwrap_or_else(|| NetError::generic("All download strategies failed.")))
}

#[allow(clippy::too_many_arguments)]
async fn run_ytdlp_download(
    engine: &Engine,
    ctx: &JobContext,
    url: &OsStr,
    output_dir: &OsStr,
    stem: &str,
    format_string: &str,
    quality: &str,
    is_audio: bool,
    phase: &Phase,
    ct: &CancellationToken,
) -> Result<Option<OsString>, NetError> {
    let bins = engine.bins();
    let (exe, extra) = bins.runner();
    let settings = engine.settings().await;
    let cookies = resolve_cookies_file(&settings);
    let args = download_args(
        &extra,
        bins.ffmpeg_location().as_deref(),
        cookies.as_deref(),
        format_string,
        output_dir,
        stem,
        is_audio,
        quality,
        url,
    );
    let spec = ProcSpec::raw(exe.clone(), args, engine.configured_env());

    let errors = TextBuilder::default();
    let all_output = TextBuilder::default();
    let sub_phase = if is_audio { "Download Audio" } else { "Download Video" };

    let exit_code = run_streaming(
        &spec,
        engine.info(),
        ct,
        &ctx.running,
        |line| {
            if is_blank(&line) {
                return;
            }
            engine.emit_output(&line);
            all_output.append_line(&line);
            if let Some(p) = parse_yt_dlp_progress(&line) {
                let msg = format!("{}{sub_phase}: {}%", phase.prefix(), fixed(p.percent, 1, engine.culture()));
                emit_status(engine, ctx, "downloading", p.percent, msg.into(), StatusExtra { speed: p.speed, eta: p.eta, ..Default::default() });
            }
        },
        |line| {
            if !is_blank(&line) {
                engine.emit_output(format!("[stderr] {}", line.trim()));
                errors.append_line(line.trim());
            }
        },
    )
    .await?;

    if exit_code != 0 {
        return Err(NetError::generic(video_error_text(&errors.text(), &all_output.text(), exit_code, &exe)));
    }
    Ok(find_downloaded_file(output_dir, stem, false))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_strings_match_native() {
        assert_eq!(get_format_string(Some("best"), false, "best"), "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best");
        assert_eq!(
            get_format_string(Some("1080p"), false, "best"),
            "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best"
        );
        assert_eq!(get_format_string(Some(" 720P "), false, "progressive"), "best[height<=720]/best");
        assert_eq!(get_format_string(Some("4k"), false, "progressive"), "best[height<=1080]/best");
        assert_eq!(get_format_string(Some("1080"), false, "any"), "bestvideo*+bestaudio/best");
        assert_eq!(get_format_string(Some("p"), false, "best"), "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best");
        assert_eq!(get_format_string(Some("mp3"), true, "best"), "bestaudio[ext=m4a]/bestaudio/best");
        assert_eq!(get_format_string(None, false, "progressive"), "best[height<=1080]/best");
    }

    #[test]
    fn audio_quality_pairs() {
        assert_eq!(audio_format_for("FLAC"), ("flac", "0"));
        assert_eq!(audio_format_for("m4a"), ("m4a", "256"));
        assert_eq!(audio_format_for("mp3"), ("mp3", "320"));
        assert_eq!(audio_format_for("audio"), ("mp3", "320"));
    }

    #[test]
    fn download_args_match_native() {
        let a = download_args("", Some(OsStr::new(r"C:\ff")), None, "best[height<=1080]/best", OsStr::new(r"C:\Out"), "abc123def456", false, "best", OsStr::new("https://youtu.be/x"));
        assert_eq!(
            a,
            OsString::from(r#"--ffmpeg-location "C:\ff" --newline -f "best[height<=1080]/best" -o "C:\Out\abc123def456.%(ext)s" --merge-output-format mp4 --no-playlist --retries 10 --fragment-retries 15 --extractor-retries 5 --socket-timeout 20 --windows-filenames "https://youtu.be/x""#)
        );
        let b = download_args("-m yt_dlp", None, Some(r"C:\c.txt"), "bestaudio[ext=m4a]/bestaudio/best", OsStr::new(r"C:\Out\"), "s", true, "flac", OsStr::new("u"));
        assert_eq!(
            b,
            OsString::from(r#"-m yt_dlp --cookies "C:\c.txt" --newline -f "bestaudio[ext=m4a]/bestaudio/best" -o "C:\Out\s.%(ext)s" --extract-audio --audio-format flac --audio-quality 0 --no-playlist --retries 10 --fragment-retries 15 --extractor-retries 5 --socket-timeout 20 --windows-filenames "u""#)
        );
    }

    #[test]
    fn error_texts_match_native() {
        let exe = OsStr::new("yt-dlp");
        assert_eq!(video_error_text("ERROR: [youtube] x: Video unavailable\r\n", "", 1, exe), OsString::from("Video is unavailable, private, or region-restricted."));
        assert_eq!(
            video_error_text("ERROR: Sign in to confirm you\u{2019}re not a bot.", "", 1, exe),
            OsString::from("YouTube verification required. For restricted videos, you can configure an optional cookies file in Settings \u{2192} Advanced \u{2192} Backend.")
        );
        assert_eq!(video_error_text("", "", 9009, exe), OsString::from("yt-dlp or ffmpeg command was not found or failed to spawn (exit code 9009). Runner: yt-dlp"));
        assert_eq!(video_error_text("  ", "", 2, exe), OsString::from("yt-dlp exited with error code 2"));
        assert_eq!(video_error_text("", "line\r\nERROR: boom\r\n", 1, exe), OsString::from("line\r\nERROR: boom"));
        assert_eq!(video_error_text("a\r\nb\r\n", "", 1, exe), OsString::from("a\r\nb"));
    }

    #[test]
    fn phase_prefix() {
        let p = Phase::new(1);
        assert_eq!(p.prefix(), "");
        let p = Phase::new(3);
        assert_eq!(p.prefix(), "[1/3] ");
        p.advance();
        assert_eq!(p.prefix(), "[2/3] ");
    }
}
