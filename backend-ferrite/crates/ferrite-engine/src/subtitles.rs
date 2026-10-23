//! Port of `GenerateSubtitlesAsync`: Native shells out to `backend/subtitler.py`
//! (faster-whisper) with the Python that has yt-dlp, and so does Ferrite.

use std::ffi::OsString;

use tokio_util::sync::CancellationToken;

use crate::binaries::python_with_ytdlp;
use crate::dotnet::ioerr::NetError;
use crate::dotnet::text::is_blank_os;
use crate::engine::Engine;
use crate::locate::get_backend_dir;
use crate::proc::runner::{run_to_end, ProcSpec};
use crate::util::last_line;

pub const SUBTITLE_MARKER: &str = "__SRT_BEGIN__";

pub fn subtitle_script() -> String {
    format!(
        "import sys; sys.path.insert(0, sys.argv[1]); from subtitler import generate_subtitles; srt = generate_subtitles(sys.argv[2], sys.argv[3], None if sys.argv[4] == 'auto' else sys.argv[4]); print('{SUBTITLE_MARKER}'); print(srt)"
    )
}

/// The text after the marker, the "no subtitles" empty string, or the thrown message.
pub fn subtitle_result(stdout: &str, stderr: &str, exit_code: i32) -> Result<String, NetError> {
    match stdout.find(SUBTITLE_MARKER) {
        Some(idx) => Ok(stdout[idx + SUBTITLE_MARKER.len()..].trim().to_owned()),
        None if exit_code != 0 => Err(NetError::generic(
            last_line(stderr).unwrap_or_else(|| format!("Subtitle generation failed (exit code {exit_code}).")),
        )),
        None => Ok(String::new()),
    }
}

pub async fn generate_subtitles(
    engine: &Engine,
    video_path: Option<OsString>,
    model_size: Option<OsString>,
    language: Option<OsString>,
    ct: &CancellationToken,
) -> Result<OsString, NetError> {
    engine.ensure_initialized(Some(ct)).await?;
    let info = engine.info().clone();
    let py = tokio::task::spawn_blocking(move || python_with_ytdlp(&info))
        .await
        .ok()
        .flatten()
        .unwrap_or_else(|| "python".into());
    let backend_dir = get_backend_dir(engine.info());

    // ArgumentList.Add(null) makes Process.Start throw a NullReferenceException.
    let (Some(video_path), Some(model_size)) = (video_path, model_size) else {
        return Err(NetError::generic("Object reference not set to an instance of an object."));
    };
    let lang = match language {
        Some(l) if !is_blank_os(&l) => l,
        _ => "auto".into(),
    };
    let args = vec![OsString::from("-c"), OsString::from(subtitle_script()), backend_dir, video_path, model_size, lang];
    let spec = ProcSpec::list(py, args, engine.configured_env());
    let result = run_to_end(&spec, engine.info(), ct, None).await?;
    engine.emit_output(result.stderr.trim());
    subtitle_result(&result.stdout, &result.stderr, result.exit_code).map(OsString::from)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn marker_handling() {
        assert_eq!(subtitle_result("log\n__SRT_BEGIN__\n1\n00:00 --> 00:01\nHi\n\n", "", 0).unwrap(), "1\n00:00 --> 00:01\nHi");
        assert_eq!(subtitle_result("nothing", "", 0).unwrap(), "");
        assert_eq!(
            subtitle_result("", "Traceback\nModuleNotFoundError: No module named 'faster_whisper'\n", 1).unwrap_err().message,
            OsString::from("ModuleNotFoundError: No module named 'faster_whisper'")
        );
        assert_eq!(subtitle_result("", "  \n", 2).unwrap_err().message, OsString::from("Subtitle generation failed (exit code 2)."));
    }

    #[test]
    fn script_text() {
        assert!(subtitle_script().contains("print('__SRT_BEGIN__'); print(srt)"));
    }
}
