//! Port of `DownloadYtMusicAsync`.

use std::ffi::{OsStr, OsString};
use std::time::Duration;

use tokio_util::sync::CancellationToken;

use crate::cookies::resolve_cookies_file;
use crate::download::{Phase, TextBuilder};
use crate::dotnet::fmt::fixed;
use crate::dotnet::ioerr::NetError;
use crate::dotnet::path::combine;
use crate::dotnet::text::{contains_ignore_case, is_blank};
use crate::engine::Engine;
use crate::jobs::{emit, emit_status, JobContext, StatusExtra};
use crate::ncat;
use crate::proc::progress::parse_yt_dlp_progress;
use crate::proc::runner::{run_streaming, ProcSpec};
use crate::util::{clean_stem_files, find_downloaded_file, is_track_url};

/// Audio format/quality from the quality profile (exact, case-sensitive matches).
pub fn ytmusic_audio_format(quality: &str) -> (&'static str, &'static str) {
    if quality == "ytmusic_flac" {
        ("flac", "0")
    } else if quality == "ytmusic_m4a" || quality == "m4a" {
        ("m4a", "256")
    } else {
        ("mp3", "320")
    }
}

/// `BuildYtMusicArgs()` (§5.3 of PORTING.md).
#[allow(clippy::too_many_arguments)]
pub fn ytmusic_args(
    extra: &str,
    ffmpeg_location: Option<&OsStr>,
    cookies: Option<&str>,
    audio_format: &str,
    audio_quality: &str,
    output_dir: &OsStr,
    stem: &str,
    is_track: bool,
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
    a.push("-f \"bestaudio/best\" ");
    a.push(format!("--extract-audio --audio-format {audio_format} --audio-quality {audio_quality} "));
    a.push("--embed-thumbnail --add-metadata --write-thumbnail ");
    a.push("--postprocessor-args \"ffmpeg:-id3v2_version 3\" ");
    a.push(ncat!("-o \"", combine(output_dir, format!("{stem}.%(ext)s")), "\" "));
    if is_track {
        a.push("--no-playlist ");
    }
    a.push("--retries 5 --fragment-retries 10 --extractor-retries 3 --socket-timeout 15 ");
    a.push("--windows-filenames ");
    a.push("--newline ");
    a.push(ncat!("\"", url, "\""));
    a
}

/// The error text after the second failed attempt.
pub fn ytmusic_error_text(stderr: &str, exit_code: i32) -> String {
    let mut err = stderr.trim().to_owned();
    if is_blank(&err) {
        err = format!("YT Music download failed (exit code {exit_code})");
    }
    if contains_ignore_case(&err, "Video unavailable")
        || contains_ignore_case(&err, "This video is not available")
        || contains_ignore_case(&err, "Private video")
    {
        return "Music track is unavailable, private, or region-restricted.".into();
    }
    if contains_ignore_case(&err, "Sign in to confirm you\u{2019}re not a bot") || contains_ignore_case(&err, "confirm you're not a bot") {
        return "YouTube verification required. For restricted tracks, you can configure an optional cookies file in Settings \u{2192} Advanced \u{2192} Backend.".into();
    }
    err
}

#[allow(clippy::too_many_arguments)]
pub async fn download_ytmusic(
    engine: &Engine,
    ctx: &JobContext,
    url: &OsStr,
    output_dir: &OsStr,
    stem: &str,
    quality: &str,
    phase: &Phase,
    ct: &CancellationToken,
) -> Result<Option<OsString>, NetError> {
    let (audio_format, audio_quality) = ytmusic_audio_format(quality);
    let is_track = is_track_url(url);
    let (exe, extra) = engine.bins().runner();
    let music_errors = TextBuilder::default();

    let run = |args: OsString| {
        let spec = ProcSpec::raw(exe.clone(), args, engine.configured_env());
        let music_errors = &music_errors;
        async move {
            run_streaming(
                &spec,
                engine.info(),
                ct,
                &ctx.running,
                |line| {
                    if is_blank(&line) {
                        return;
                    }
                    engine.emit_output(&line);
                    if let Some(p) = parse_yt_dlp_progress(&line) {
                        let msg = format!("{}Download Music: {}%", phase.prefix(), fixed(p.percent, 1, engine.culture()));
                        emit_status(engine, ctx, "downloading", p.percent, msg.into(), StatusExtra { speed: p.speed, eta: p.eta, ..Default::default() });
                    }
                },
                |line| {
                    if !is_blank(&line) {
                        engine.emit_output(format!("[stderr] {}", line.trim()));
                        music_errors.append_line(line.trim());
                    }
                },
            )
            .await
        }
    };
    let build_args = || async {
        let settings = engine.settings().await;
        let cookies = resolve_cookies_file(&settings);
        ytmusic_args(
            &extra,
            engine.bins().ffmpeg_location().as_deref(),
            cookies.as_deref(),
            audio_format,
            audio_quality,
            output_dir,
            stem,
            is_track,
            url,
        )
    };

    let mut exit_code = run(build_args().await).await?;
    if exit_code != 0 {
        clean_stem_files(Some(output_dir), Some(stem), None).await;
        if ct.is_cancelled() {
            return Err(NetError::canceled());
        }
        emit(engine, ctx, "downloading", 0.0, format!("{}Retrying music download...", phase.prefix()));
        tokio::select! {
            _ = tokio::time::sleep(Duration::from_millis(1500)) => {}
            _ = ct.cancelled() => return Err(NetError::canceled()),
        }
        exit_code = run(build_args().await).await?;
        if exit_code != 0 {
            return Err(NetError::generic(ytmusic_error_text(&music_errors.text(), exit_code)));
        }
    }
    Ok(find_downloaded_file(output_dir, stem, true))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quality_profiles() {
        assert_eq!(ytmusic_audio_format("ytmusic_flac"), ("flac", "0"));
        assert_eq!(ytmusic_audio_format("ytmusic_m4a"), ("m4a", "256"));
        assert_eq!(ytmusic_audio_format("m4a"), ("m4a", "256"));
        assert_eq!(ytmusic_audio_format("ytmusic_320k"), ("mp3", "320"));
        // Native quirks: these all become MP3 320 on the YT Music path.
        assert_eq!(ytmusic_audio_format("flac"), ("mp3", "320"));
        assert_eq!(ytmusic_audio_format("YTMUSIC_FLAC"), ("mp3", "320"));
        assert_eq!(ytmusic_audio_format("best"), ("mp3", "320"));
    }

    #[test]
    fn args_match_native() {
        let a = ytmusic_args("", None, None, "mp3", "320", OsStr::new(r"C:\M"), "st", true, OsStr::new("https://music.youtube.com/watch?v=1"));
        assert_eq!(
            a,
            OsString::from(r#"-f "bestaudio/best" --extract-audio --audio-format mp3 --audio-quality 320 --embed-thumbnail --add-metadata --write-thumbnail --postprocessor-args "ffmpeg:-id3v2_version 3" -o "C:\M\st.%(ext)s" --no-playlist --retries 5 --fragment-retries 10 --extractor-retries 3 --socket-timeout 15 --windows-filenames --newline "https://music.youtube.com/watch?v=1""#)
        );
    }

    #[test]
    fn error_texts() {
        assert_eq!(ytmusic_error_text("", 1), "YT Music download failed (exit code 1)");
        assert_eq!(ytmusic_error_text("ERROR: Private video", 1), "Music track is unavailable, private, or region-restricted.");
        assert_eq!(ytmusic_error_text("first\r\nsecond\r\n", 1), "first\r\nsecond");
    }
}
