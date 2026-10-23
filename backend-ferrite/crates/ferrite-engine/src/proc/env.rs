//! Child-process environments.

use std::ffi::OsString;

use crate::dotnet::fs::{directory_exists, file_exists};
use crate::dotnet::netstr::wide;
use crate::dotnet::path::{combine, get_directory_name};
use crate::dotnet::text::eq_ignore_case_wide;
use crate::dotnet::win::{get_folder_path, KnownFolder};

/// Which of Native's environment set-ups a process gets.
#[derive(Debug, Clone)]
pub enum EnvMode {
    /// `ConfigureProcessStartInfo`: `PYTHONUTF8=1`, `PYTHONIOENCODING=utf-8` and the given
    /// `PATH` (see [`configured_path`]).
    Configured { path: OsString },
    /// `TestExecutable`: only the two Python variables.
    PythonOnly,
    /// `MediaTools.RunProcessAsync` without configuration (hardware encoder probe).
    Inherit,
}

/// Builds the `PATH` value `ConfigureProcessStartInfo` assigns: the ffmpeg dir (when ffmpeg is
/// an existing file), the yt-dlp runner dir (same rule) and `%USERPROFILE%\.deno\bin` (when it
/// exists), de-duplicated case-insensitively, in front of the current `PATH`.
pub fn configured_path(ffmpeg_path: Option<&OsString>, runner_exe: Option<&OsString>) -> OsString {
    let mut extra: Vec<OsString> = Vec::new();
    let mut add = |dir: Option<OsString>| {
        if let Some(d) = dir {
            let dw = wide(&d);
            if !dw.is_empty() && !extra.iter().any(|e| eq_ignore_case_wide(&wide(e), &dw)) {
                extra.push(d);
            }
        }
    };
    if let Some(f) = ffmpeg_path.filter(|f| !f.is_empty() && file_exists(f)) {
        add(get_directory_name(f));
    }
    if let Some(r) = runner_exe.filter(|r| !r.is_empty() && file_exists(r)) {
        add(get_directory_name(r));
    }
    let deno = combine(get_folder_path(KnownFolder::UserProfile), r".deno\bin");
    if directory_exists(&deno) {
        add(Some(deno));
    }
    let current = std::env::var_os("PATH").unwrap_or_default();
    if extra.is_empty() {
        return current;
    }
    let mut p = OsString::new();
    for (i, e) in extra.iter().enumerate() {
        if i > 0 {
            p.push(";");
        }
        p.push(e);
    }
    p.push(";");
    p.push(current);
    p
}
