//! Port of `NativeEngineService.ResolveBinaries`, `TestExecutable`,
//! `FindWorkingPythonWithYtDlp`, `EnsureStandaloneYtDlp` and `EnsureFfmpegReadyAsync`.

use std::ffi::{OsStr, OsString};
use std::sync::OnceLock;
use std::time::Duration;

use crate::dotnet::fs::{create_directory, file_exists, move_file};
use crate::dotnet::path::{combine, combine_all, get_directory_name};
use crate::dotnet::win::{get_folder_path, KnownFolder};
use crate::host::HostInfo;
use crate::locate::{add_path_candidates, find_solution_dir, get_ffmpeg_candidates};
use crate::proc::runner::test_executable;

/// `_ffmpegPath`, `_ffprobePath`, `_ytDlpRunner`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Binaries {
    pub ffmpeg_path: Option<OsString>,
    pub ffprobe_path: Option<OsString>,
    /// (executable, extra arguments: `""` or `"-m yt_dlp"`).
    pub ytdlp_runner: Option<(OsString, String)>,
}

impl Binaries {
    /// `_ytDlpRunner ?? ("yt-dlp", "")`.
    pub fn runner(&self) -> (OsString, String) {
        self.ytdlp_runner.clone().unwrap_or_else(|| (OsString::from("yt-dlp"), String::new()))
    }

    /// `_ffmpegPath ?? "ffmpeg"`.
    pub fn ffmpeg(&self) -> OsString {
        self.ffmpeg_path.clone().unwrap_or_else(|| "ffmpeg".into())
    }

    /// `_ffprobePath ?? "ffprobe"`.
    pub fn ffprobe(&self) -> OsString {
        self.ffprobe_path.clone().unwrap_or_else(|| "ffprobe".into())
    }

    /// The directory for `--ffmpeg-location`, when `_ffmpegPath` is an existing file.
    pub fn ffmpeg_location(&self) -> Option<OsString> {
        let f = self.ffmpeg_path.as_ref().filter(|f| !f.is_empty() && file_exists(f))?;
        get_directory_name(f).filter(|d| !d.is_empty())
    }

    /// The runner executable when it is an existing file (used for the `PATH` prepend).
    pub fn runner_exe_file(&self) -> Option<OsString> {
        self.ytdlp_runner.as_ref().map(|(e, _)| e.clone()).filter(|e| !e.is_empty())
    }
}

/// `ResolveBinaries()`. Blocking (starts probe processes, may download yt-dlp).
pub fn resolve_binaries(info: &HostInfo) -> Binaries {
    let app_dir = OsString::from(&info.base_dir);
    let sol = find_solution_dir(info);
    let local = get_folder_path(KnownFolder::LocalApplicationData);
    let roaming = get_folder_path(KnownFolder::ApplicationData);
    let o = OsStr::new;

    // 1. FFmpeg & FFprobe
    let mut ffmpeg: Option<OsString> = None;
    let mut ffprobe: Option<OsString> = None;
    for p in get_ffmpeg_candidates(info) {
        if file_exists(&p) {
            let dir = get_directory_name(&p).unwrap_or_default();
            let probe = combine(&dir, "ffprobe.exe");
            if file_exists(&probe) {
                ffprobe = Some(probe);
            }
            ffmpeg = Some(p);
            break;
        }
    }
    if ffprobe.is_none() {
        if let Some(f) = &ffmpeg {
            if let Some(dir) = get_directory_name(f).filter(|d| !d.is_empty()) {
                let probe = combine(&dir, "ffprobe.exe");
                if file_exists(&probe) {
                    ffprobe = Some(probe);
                }
            }
        }
    }
    let ffmpeg = Some(ffmpeg.unwrap_or_else(|| "ffmpeg".into()));
    let ffprobe = Some(ffprobe.unwrap_or_else(|| "ffprobe".into()));

    // 2. yt-dlp binary candidates (standalone exe or virtualenv exe)
    let c = |parts: &[&OsStr]| combine_all(parts);
    let mut candidates = vec![
        c(&[&sol, o("backend"), o("venv"), o("Scripts"), o("yt-dlp.exe")]),
        c(&[&app_dir, o("backend"), o("venv"), o("Scripts"), o("yt-dlp.exe")]),
        c(&[&app_dir, o("venv"), o("Scripts"), o("yt-dlp.exe")]),
        c(&[&sol, o("venv"), o("Scripts"), o("yt-dlp.exe")]),
        c(&[&app_dir, o("yt-dlp.exe")]),
        c(&[&app_dir, o("backend"), o("yt-dlp.exe")]),
        c(&[&sol, o("backend"), o("yt-dlp.exe")]),
        c(&[&sol, o("yt-dlp.exe")]),
        c(&[&local, o("VideoConverter"), o("bin"), o("yt-dlp.exe")]),
        c(&[&roaming, o("VideoConverter"), o("bin"), o("yt-dlp.exe")]),
        combine(&local, r"Programs\Python\Python312\Scripts\yt-dlp.exe"),
        combine(&local, r"Programs\Python\Python311\Scripts\yt-dlp.exe"),
        combine(&local, r"Programs\Python\Python310\Scripts\yt-dlp.exe"),
        combine(&roaming, r"Python\Python312\Scripts\yt-dlp.exe"),
        combine(&roaming, r"Python\Python311\Scripts\yt-dlp.exe"),
        combine(&roaming, r"Python\Python310\Scripts\yt-dlp.exe"),
    ];
    add_path_candidates(&mut candidates, "yt-dlp.exe");

    let mut runner: Option<(OsString, String)> = None;
    for p in candidates {
        if file_exists(&p) && test_executable(&p, "--version", info) {
            runner = Some((p, String::new()));
            break;
        }
    }

    // 3. Fallback: Python module (python -m yt_dlp)
    if runner.is_none() {
        if let Some(py) = python_with_ytdlp(info) {
            runner = Some((py, "-m yt_dlp".into()));
        }
    }

    // 4. Self-healing fallback: auto-download standalone yt-dlp.exe
    if runner.is_none() {
        runner = Some(match ensure_standalone_ytdlp(info) {
            Some(exe) => (exe, String::new()),
            None => ("yt-dlp".into(), String::new()),
        });
    }

    Binaries { ffmpeg_path: ffmpeg, ffprobe_path: ffprobe, ytdlp_runner: runner }
}

static PYTHON_WITH_YTDLP: OnceLock<Option<OsString>> = OnceLock::new();

/// `_pythonWithYtDlp.Value` (a process-wide `Lazy`). Blocking.
pub fn python_with_ytdlp(info: &HostInfo) -> Option<OsString> {
    PYTHON_WITH_YTDLP.get_or_init(|| find_working_python_with_ytdlp(info)).clone()
}

/// `FindWorkingPythonWithYtDlp()`.
pub fn find_working_python_with_ytdlp(info: &HostInfo) -> Option<OsString> {
    let sol = find_solution_dir(info);
    let app_dir = OsString::from(&info.base_dir);
    let local = get_folder_path(KnownFolder::LocalApplicationData);
    let roaming = get_folder_path(KnownFolder::ApplicationData);
    let o = OsStr::new;
    let c = |parts: &[&OsStr]| combine_all(parts);
    let mut candidates = vec![
        c(&[&sol, o("backend"), o("venv"), o("Scripts"), o("python.exe")]),
        c(&[&app_dir, o("backend"), o("venv"), o("Scripts"), o("python.exe")]),
        c(&[&sol, o("venv"), o("Scripts"), o("python.exe")]),
        c(&[&app_dir, o("venv"), o("Scripts"), o("python.exe")]),
        combine(&local, r"Programs\Python\Python312\python.exe"),
        combine(&local, r"Programs\Python\Python311\python.exe"),
        combine(&local, r"Programs\Python\Python310\python.exe"),
        combine(&roaming, r"Programs\Python\Python312\python.exe"),
        combine(&roaming, r"Programs\Python\Python311\python.exe"),
        combine(&roaming, r"Programs\Python\Python310\python.exe"),
    ];
    add_path_candidates(&mut candidates, "python.exe");
    for py in candidates {
        if file_exists(&py) && test_executable(&py, "-m yt_dlp --version", info) {
            return Some(py);
        }
    }
    for v in ["-3.12", "-3.11", "-3.10"] {
        if test_executable(OsStr::new("py"), &format!("{v} -m yt_dlp --version"), info) {
            return Some("py".into());
        }
    }
    if test_executable(OsStr::new("python"), "-m yt_dlp --version", info) {
        return Some("python".into());
    }
    None
}

/// `EnsureStandaloneYtDlp()`. Blocking; must run on a blocking thread inside the runtime.
pub fn ensure_standalone_ytdlp(info: &HostInfo) -> Option<OsString> {
    let target_dir = combine_all(&[
        &get_folder_path(KnownFolder::LocalApplicationData),
        OsStr::new("VideoConverter"),
        OsStr::new("bin"),
    ]);
    create_directory(&target_dir).ok()?;
    let target = combine(&target_dir, "yt-dlp.exe");
    if file_exists(&target) && test_executable(&target, "--version", info) {
        return Some(target);
    }
    let mut temp = target.clone();
    temp.push(".tmp");

    let handle = tokio::runtime::Handle::try_current().ok()?;
    let bytes: Option<Vec<u8>> = handle.block_on(async {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
            .http1_only()
            .build()
            .ok()?;
        let resp = client
            .get("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe")
            .send()
            .await
            .ok()?;
        if !resp.status().is_success() {
            return None;
        }
        resp.bytes().await.ok().map(|b| b.to_vec())
    });
    let bytes = bytes?;
    std::fs::write(&temp, &bytes).ok()?;
    move_file(&temp, &target, true).ok()?;
    if test_executable(&target, "--version", info) {
        Some(target)
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runner_and_locations() {
        let b = Binaries::default();
        assert_eq!(b.runner(), (OsString::from("yt-dlp"), String::new()));
        assert_eq!(b.ffmpeg(), OsString::from("ffmpeg"));
        assert!(b.ffmpeg_location().is_none());
        let b = Binaries { ffmpeg_path: Some(r"C:\Windows\System32\cmd.exe".into()), ..Default::default() };
        assert_eq!(b.ffmpeg_location().unwrap().to_string_lossy().to_lowercase(), r"c:\windows\system32");
    }
}
