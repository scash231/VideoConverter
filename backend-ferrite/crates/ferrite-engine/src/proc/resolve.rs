//! Executable lookup for bare names (`"yt-dlp"`, `"ffmpeg"`, `"python"`, `"py"`).
//!
//! .NET starts processes with `CreateProcess(NULL, "\"name\" args")`, so Windows searches:
//! the directory of the calling executable, the current directory, the 32-bit system
//! directory, the 16-bit system directory, the Windows directory, then the caller's `PATH`
//! (not the child's). `.exe` is appended when the name has no extension. Ferrite searches the
//! same places with the WPF host's executable directory and current directory.

use std::ffi::{OsStr, OsString};

use crate::dotnet::fs::file_exists;
use crate::dotnet::netstr::wide;
use crate::dotnet::path::{combine, get_directory_name, get_extension, is_sep};
use crate::dotnet::win::{system_directory, windows_directory};
use crate::host::HostInfo;

/// True when `name` contains a directory component (then it is used as given).
pub fn has_directory(name: &OsStr) -> bool {
    wide(name).into_iter().any(|u| is_sep(u) || u == b':' as u16)
}

pub fn resolve(name: &OsStr, host: &HostInfo) -> Option<OsString> {
    if has_directory(name) {
        return Some(name.to_owned());
    }
    let file: OsString = if get_extension(name).is_empty() {
        let mut f = name.to_owned();
        f.push(".exe");
        f
    } else {
        name.to_owned()
    };
    let mut dirs: Vec<OsString> = Vec::new();
    if let Some(dir) = host.process_path.as_deref().and_then(get_directory_name) {
        dirs.push(dir);
    }
    dirs.push(OsString::from(&host.cwd));
    let sys = system_directory();
    dirs.push(sys);
    let win = windows_directory();
    dirs.push(combine(&win, "System"));
    dirs.push(win);
    if let Some(path) = std::env::var_os("PATH") {
        for part in path.to_string_lossy().split(';') {
            if !part.is_empty() {
                dirs.push(OsString::from(part));
            }
        }
    }
    dirs.into_iter().map(|d| combine(&d, &file)).find(|p| file_exists(p))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_system_tools_and_keeps_paths() {
        let host = HostInfo { cwd: std::env::current_dir().unwrap().to_string_lossy().into_owned(), ..Default::default() };
        let cmd = resolve(OsStr::new("cmd"), &host).expect("cmd.exe");
        assert!(cmd.to_string_lossy().to_lowercase().ends_with(r"system32\cmd.exe"));
        assert!(resolve(OsStr::new("definitely-not-a-tool-xyz"), &host).is_none());
        assert_eq!(resolve(OsStr::new(r"C:\x\y.exe"), &host).unwrap(), OsString::from(r"C:\x\y.exe"));
    }
}
