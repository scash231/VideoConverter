//! `DetectGoogleDriveFolder`, `DetectDropboxFolder`, `DetectOneDriveFolder` with the 30 s cache.

use std::collections::HashMap;
use std::ffi::{OsStr, OsString};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

use windows_sys::Win32::Storage::FileSystem::{GetDriveTypeW, GetLogicalDrives, GetVolumeInformationW};

use crate::dotnet::fs::directory_exists;
use crate::dotnet::json::JsonDocument;
use crate::dotnet::netstr::wide_nul;
use crate::dotnet::path::{combine, combine_all};
use crate::dotnet::text::contains_ignore_case;
use crate::dotnet::win::{get_folder_path, KnownFolder};
use crate::proc::decode::decode_all;

const DRIVE_CDROM: u32 = 5;
const CACHE_FOR: Duration = Duration::from_secs(30);

type DetectionCache = HashMap<&'static str, (Instant, Option<OsString>)>;

static CACHE: LazyLock<Mutex<DetectionCache>> = LazyLock::new(|| Mutex::new(HashMap::new()));

fn cached(key: &'static str, detect: impl FnOnce() -> Option<OsString>) -> Option<OsString> {
    if let Some((at, path)) = CACHE.lock().unwrap_or_else(|e| e.into_inner()).get(key) {
        if at.elapsed() < CACHE_FOR {
            return path.clone();
        }
    }
    let path = detect();
    CACHE.lock().unwrap_or_else(|e| e.into_inner()).insert(key, (Instant::now(), path.clone()));
    path
}

pub fn detect_google_drive_folder() -> Option<OsString> {
    cached("gdrive", detect_google_drive_core)
}

pub fn detect_dropbox_folder() -> Option<OsString> {
    cached("dropbox", detect_dropbox_core)
}

pub fn detect_onedrive_folder() -> Option<OsString> {
    cached("onedrive", detect_onedrive_core)
}

/// `DriveInfo.GetDrives()` roots ("C:\").
fn drive_roots() -> Vec<OsString> {
    // SAFETY: plain FFI call.
    let mask = unsafe { GetLogicalDrives() };
    (0..26u32).filter(|i| mask & (1 << i) != 0).map(|i| OsString::from(format!("{}:\\", (b'A' + i as u8) as char))).collect()
}

fn drive_type(root: &OsStr) -> u32 {
    let w = wide_nul(root);
    // SAFETY: NUL-terminated root path.
    unsafe { GetDriveTypeW(w.as_ptr()) }
}

/// `DriveInfo.VolumeLabel` (throws → `None`).
fn volume_label(root: &OsStr) -> Option<String> {
    let w = wide_nul(root);
    let mut name = [0u16; 261];
    // SAFETY: valid buffers; optional outputs are null.
    let ok = unsafe {
        GetVolumeInformationW(
            w.as_ptr(),
            name.as_mut_ptr(),
            name.len() as u32,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            0,
        )
    };
    if ok == 0 {
        return None;
    }
    let n = name.iter().position(|&u| u == 0).unwrap_or(name.len());
    Some(String::from_utf16_lossy(&name[..n]))
}

fn detect_google_drive_core() -> Option<OsString> {
    for root in drive_roots() {
        // Optical drives may spin up for seconds just to answer IsReady.
        if drive_type(&root) == DRIVE_CDROM || !directory_exists(&root) {
            continue;
        }
        if let Some(label) = volume_label(&root) {
            if contains_ignore_case(&label, "Google Drive") {
                let my = combine(&root, "My Drive");
                if directory_exists(&my) {
                    return Some(my);
                }
                let ablage = combine(&root, "Meine Ablage");
                if directory_exists(&ablage) {
                    return Some(ablage);
                }
                return Some(root);
            }
        }
        let my = combine(&root, "My Drive");
        if directory_exists(&my) {
            return Some(my);
        }
        let ablage = combine(&root, "Meine Ablage");
        if directory_exists(&ablage) {
            return Some(ablage);
        }
    }
    let gdrive = combine(get_folder_path(KnownFolder::UserProfile), "Google Drive");
    if directory_exists(&gdrive) {
        let my = combine(&gdrive, "My Drive");
        if directory_exists(&my) {
            return Some(my);
        }
        return Some(gdrive);
    }
    None
}

/// Reads `personal.path` / `business.path` from a Dropbox `info.json`.
pub fn dropbox_path_from_info(json: &str) -> Option<OsString> {
    let doc = JsonDocument::parse(json).ok()?;
    let root = doc.root();
    for account in ["personal", "business"] {
        let found = (|| -> Result<Option<String>, crate::dotnet::json::JsonError> {
            if let Some(acc) = root.try_get_property(account)? {
                if let Some(p) = acc.try_get_property("path")? {
                    return p.get_string();
                }
            }
            Ok(None)
        })();
        match found {
            Ok(Some(p)) if !p.is_empty() && directory_exists(&p) => return Some(OsString::from(p)),
            Ok(_) => {}
            Err(_) => return None,
        }
    }
    None
}

fn detect_dropbox_core() -> Option<OsString> {
    let infos = [
        combine_all(&[&get_folder_path(KnownFolder::ApplicationData), OsStr::new("Dropbox"), OsStr::new("info.json")]),
        combine_all(&[&get_folder_path(KnownFolder::LocalApplicationData), OsStr::new("Dropbox"), OsStr::new("info.json")]),
    ];
    for info in infos {
        if crate::dotnet::fs::file_exists(&info) {
            if let Ok(bytes) = std::fs::read(&info) {
                if let Some(p) = dropbox_path_from_info(&decode_all(&bytes)) {
                    return Some(p);
                }
            }
        }
    }
    let user = combine(get_folder_path(KnownFolder::UserProfile), "Dropbox");
    if directory_exists(&user) {
        return Some(user);
    }
    None
}

fn detect_onedrive_core() -> Option<OsString> {
    for var in ["OneDrive", "OneDriveConsumer"] {
        if let Some(v) = std::env::var_os(var) {
            if !v.is_empty() && directory_exists(&v) {
                return Some(v);
            }
        }
    }
    let user = combine(get_folder_path(KnownFolder::UserProfile), "OneDrive");
    if directory_exists(&user) {
        return Some(user);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dropbox_info_parsing() {
        let tmp = std::env::temp_dir();
        let t = tmp.display().to_string().replace('\\', "\\\\");
        assert_eq!(dropbox_path_from_info(&format!(r#"{{"personal":{{"path":"{t}"}}}}"#)), Some(tmp.into_os_string()));
        assert_eq!(dropbox_path_from_info(r#"{"personal":{"path":"C:\\missing_dir_xyz"}}"#), None);
        assert_eq!(dropbox_path_from_info("not json"), None);
        assert_eq!(dropbox_path_from_info(r#"{"personal":{"path":5}}"#), None);
    }

    #[test]
    fn detection_is_cached_and_does_not_panic() {
        let a = detect_onedrive_folder();
        let b = detect_onedrive_folder();
        assert_eq!(a, b);
        let _ = detect_google_drive_folder();
        let _ = detect_dropbox_folder();
    }
}
