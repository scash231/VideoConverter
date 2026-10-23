//! Port of `MediaTools.FindSolutionDir`, `GetFfmpegCandidates`, `FindFfmpeg`,
//! `AddPathCandidates`, `FFmpegInstallerService.GetDefaultInstallDir` and
//! `NativeEngineService.GetBackendDir`.

use std::ffi::{OsStr, OsString};

use crate::dotnet::fs::{directory_exists, file_exists};
use crate::dotnet::netstr::{from_wide, wide};
use crate::dotnet::path::{combine, combine_all, get_directory_name, get_full_path};
use crate::dotnet::text::{contains_ignore_case, eq_ignore_case_wide, trim_chars_wide, trim_wide};
use crate::dotnet::win::{get_environment_variable, get_folder_path, EnvTarget, KnownFolder};
use crate::host::HostInfo;

/// `Directory.GetParent(dir)?.FullName`.
fn get_parent(dir: &OsStr) -> Option<OsString> {
    let full = get_full_path(dir).ok()?;
    let parent = get_directory_name(&full)?;
    get_full_path(&parent).ok()
}

/// `MediaTools.FindSolutionDir()`.
pub fn find_solution_dir(info: &HostInfo) -> OsString {
    let exe_dir = info.process_path.as_deref().and_then(get_directory_name);
    if let Some(d) = &exe_dir {
        if directory_exists(combine(d, "backend")) {
            return d.clone();
        }
    }
    let mut dir = OsString::from(&info.base_dir);
    for _ in 0..8 {
        if directory_exists(combine(&dir, "backend")) {
            return dir;
        }
        match get_parent(&dir) {
            Some(p) => dir = p,
            None => break,
        }
    }
    exe_dir.unwrap_or_else(|| OsString::from(&info.base_dir))
}

/// `FFmpegInstallerService.GetDefaultInstallDir()`.
pub fn ffmpeg_default_install_dir() -> OsString {
    combine_all(&[
        &get_folder_path(KnownFolder::LocalApplicationData),
        OsStr::new("VideoConverter"),
        OsStr::new("ffmpeg"),
        OsStr::new("bin"),
    ])
}

/// `MediaTools.AddPathCandidates(list, exeName)`: every entry of the Process, User and Machine
/// `PATH` (quotes trimmed, WindowsApps skipped), de-duplicated case-insensitively.
pub fn add_path_candidates(list: &mut Vec<OsString>, exe_name: &str) {
    for target in [EnvTarget::Process, EnvTarget::User, EnvTarget::Machine] {
        let Some(path_val) = get_environment_variable("PATH", target) else { continue };
        if path_val.is_empty() {
            continue;
        }
        let w = wide(&path_val);
        for part in w.split(|&u| u == b';' as u16).filter(|p| !p.is_empty()) {
            let clean = trim_chars_wide(trim_wide(part), &[b'"' as u16]);
            if clean.is_empty() {
                continue;
            }
            let clean_os = from_wide(clean);
            if contains_ignore_case(&clean_os.to_string_lossy(), "WindowsApps") {
                continue;
            }
            let full = combine(&clean_os, exe_name);
            let fw = wide(&full);
            if !list.iter().any(|e| eq_ignore_case_wide(&wide(e), &fw)) {
                list.push(full);
            }
        }
    }
}

/// `MediaTools.GetFfmpegCandidates()`.
pub fn get_ffmpeg_candidates(info: &HostInfo) -> Vec<OsString> {
    let app_dir = OsString::from(&info.base_dir);
    let sol = find_solution_dir(info);
    let local = get_folder_path(KnownFolder::LocalApplicationData);
    let roaming = get_folder_path(KnownFolder::ApplicationData);
    let s = |p: &[&OsStr]| combine_all(p);
    let o = OsStr::new;
    let mut c = vec![
        combine(ffmpeg_default_install_dir(), "ffmpeg.exe"),
        s(&[&app_dir, o("backend"), o("ffmpeg_bin"), o("ffmpeg.exe")]),
        s(&[&sol, o("backend"), o("ffmpeg_bin"), o("ffmpeg.exe")]),
        s(&[&app_dir, o("ffmpeg_bin"), o("ffmpeg.exe")]),
        s(&[&sol, o("ffmpeg_bin"), o("ffmpeg.exe")]),
        s(&[&app_dir, o("ffmpeg.exe")]),
        s(&[&sol, o("ffmpeg.exe")]),
        s(&[&local, o("VideoConverter"), o("ffmpeg_bin"), o("ffmpeg.exe")]),
        s(&[&roaming, o("VideoConverter"), o("ffmpeg_bin"), o("ffmpeg.exe")]),
    ];
    add_path_candidates(&mut c, "ffmpeg.exe");
    c
}

/// `MediaTools.FindFfmpeg()`.
pub fn find_ffmpeg(info: &HostInfo) -> OsString {
    get_ffmpeg_candidates(info).into_iter().find(|p| file_exists(p)).unwrap_or_else(|| "ffmpeg".into())
}

/// `NativeEngineService.GetBackendDir()`.
pub fn get_backend_dir(info: &HostInfo) -> OsString {
    let d = combine(find_solution_dir(info), "backend");
    if directory_exists(&d) {
        d
    } else {
        combine(&info.base_dir, "backend")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_this_repository_backend() {
        // Running from backend-ferrite/target/...: the repo root (with backend/) is a parent.
        let exe = std::env::current_exe().unwrap();
        let base = format!("{}\\", exe.parent().unwrap().display());
        let info = HostInfo { base_dir: base, process_path: Some(exe.display().to_string()), ..Default::default() };
        let sol = find_solution_dir(&info);
        assert!(directory_exists(combine(&sol, "backend")), "{sol:?}");
        assert!(get_backend_dir(&info).to_string_lossy().ends_with("backend"));
        let cands = get_ffmpeg_candidates(&info);
        assert!(cands[0].to_string_lossy().ends_with(r"VideoConverter\ffmpeg\bin\ffmpeg.exe"));
        assert!(cands.len() >= 9);
    }
}
