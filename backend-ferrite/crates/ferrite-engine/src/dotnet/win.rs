//! Thin Win32 wrappers for the values .NET reads from the OS.

use std::ffi::{OsStr, OsString};
use std::ptr::{null, null_mut};

use windows_sys::core::GUID;
use windows_sys::Win32::Foundation::{ERROR_SUCCESS, MAX_PATH};
use windows_sys::Win32::System::Com::CoTaskMemFree;
use windows_sys::Win32::System::Diagnostics::Debug::{
    FormatMessageW, FORMAT_MESSAGE_ARGUMENT_ARRAY, FORMAT_MESSAGE_FROM_SYSTEM, FORMAT_MESSAGE_IGNORE_INSERTS,
};
use windows_sys::Win32::System::Environment::ExpandEnvironmentStringsW;
use windows_sys::Win32::System::Registry::{
    RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, REG_EXPAND_SZ,
    REG_SZ,
};
use windows_sys::Win32::System::SystemInformation::{GetSystemDirectoryW, GetWindowsDirectoryW};
use windows_sys::Win32::UI::Shell::{
    SHGetKnownFolderPath, ShellExecuteW, FOLDERID_LocalAppData, FOLDERID_Profile, FOLDERID_RoamingAppData,
};
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

use super::netstr::{from_wide, wide_nul};

/// `Interop.Kernel32.GetMessage(errorCode)` as used by `Win32Exception` / `IOException`:
/// the system message (in the user's UI language) with trailing whitespace and control
/// characters removed, or `Unknown error (0x..)`.
pub fn format_message(code: u32) -> String {
    let mut buf = vec![0u16; 4096];
    // SAFETY: the buffer is valid for `buf.len()` UTF-16 units; inserts are ignored.
    let len = unsafe {
        FormatMessageW(
            FORMAT_MESSAGE_IGNORE_INSERTS | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_ARGUMENT_ARRAY,
            null(),
            code,
            0,
            buf.as_mut_ptr(),
            buf.len() as u32,
            null(),
        )
    } as usize;
    if len == 0 {
        return format!("Unknown error (0x{code:x})");
    }
    let mut end = len;
    while end > 0 && buf[end - 1] <= 32 {
        end -= 1;
    }
    String::from_utf16_lossy(&buf[..end])
}

#[derive(Debug, Clone, Copy)]
pub enum KnownFolder {
    /// `Environment.SpecialFolder.UserProfile`
    UserProfile,
    /// `Environment.SpecialFolder.LocalApplicationData`
    LocalApplicationData,
    /// `Environment.SpecialFolder.ApplicationData`
    ApplicationData,
}

/// `Environment.GetFolderPath(folder)` (SpecialFolderOption.None): `SHGetKnownFolderPath`
/// with no flags, or an empty string on failure.
pub fn get_folder_path(folder: KnownFolder) -> OsString {
    let id: &GUID = match folder {
        KnownFolder::UserProfile => &FOLDERID_Profile,
        KnownFolder::LocalApplicationData => &FOLDERID_LocalAppData,
        KnownFolder::ApplicationData => &FOLDERID_RoamingAppData,
    };
    let mut p: *mut u16 = null_mut();
    // SAFETY: `p` receives a CoTaskMem-allocated, NUL-terminated string that we free below.
    let hr = unsafe { SHGetKnownFolderPath(id, 0, null_mut(), &mut p) };
    if hr < 0 || p.is_null() {
        if !p.is_null() {
            // SAFETY: allocated by SHGetKnownFolderPath.
            unsafe { CoTaskMemFree(p as _) };
        }
        return OsString::new();
    }
    // SAFETY: `p` is NUL-terminated.
    let len = unsafe { (0..).take_while(|&i| *p.add(i) != 0).count() };
    // SAFETY: `len` units are readable.
    let s = from_wide(unsafe { std::slice::from_raw_parts(p, len) });
    // SAFETY: allocated by SHGetKnownFolderPath.
    unsafe { CoTaskMemFree(p as _) };
    s
}

/// `GetSystemDirectoryW`.
pub fn system_directory() -> OsString {
    let mut buf = vec![0u16; MAX_PATH as usize + 1];
    // SAFETY: buffer is valid for its length.
    let n = unsafe { GetSystemDirectoryW(buf.as_mut_ptr(), buf.len() as u32) } as usize;
    from_wide(&buf[..n.min(buf.len())])
}

/// `GetWindowsDirectoryW`.
pub fn windows_directory() -> OsString {
    let mut buf = vec![0u16; MAX_PATH as usize + 1];
    // SAFETY: buffer is valid for its length.
    let n = unsafe { GetWindowsDirectoryW(buf.as_mut_ptr(), buf.len() as u32) } as usize;
    from_wide(&buf[..n.min(buf.len())])
}

/// `Environment.ExpandEnvironmentVariables`.
pub fn expand_environment_strings(s: &OsStr) -> OsString {
    let src = wide_nul(s);
    let mut buf = vec![0u16; 1024];
    loop {
        // SAFETY: `src` is NUL-terminated and `buf` is valid for its length.
        let n = unsafe { ExpandEnvironmentStringsW(src.as_ptr(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
        if n == 0 {
            return s.to_owned();
        }
        if n <= buf.len() {
            return from_wide(&buf[..n.saturating_sub(1)]);
        }
        buf.resize(n, 0);
    }
}

/// Where `Environment.GetEnvironmentVariable(name, target)` reads from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvTarget {
    Process,
    User,
    Machine,
}

/// `Environment.GetEnvironmentVariable(name, target)`. User/Machine values come from the
/// registry; `REG_EXPAND_SZ` values are expanded like `RegistryKey.GetValue` does by default.
pub fn get_environment_variable(name: &str, target: EnvTarget) -> Option<OsString> {
    match target {
        EnvTarget::Process => std::env::var_os(name),
        EnvTarget::User => read_env_registry(HKEY_CURRENT_USER, "Environment", name),
        EnvTarget::Machine => read_env_registry(
            HKEY_LOCAL_MACHINE,
            r"System\CurrentControlSet\Control\Session Manager\Environment",
            name,
        ),
    }
}

fn read_env_registry(root: HKEY, subkey: &str, name: &str) -> Option<OsString> {
    let sub = wide_nul(OsStr::new(subkey));
    let val = wide_nul(OsStr::new(name));
    let mut key: HKEY = null_mut();
    // SAFETY: valid NUL-terminated strings and out pointer.
    if unsafe { RegOpenKeyExW(root, sub.as_ptr(), 0, KEY_READ, &mut key) } != ERROR_SUCCESS {
        return None;
    }
    let mut ty = 0u32;
    let mut size = 0u32;
    // SAFETY: querying the size only.
    let rc = unsafe { RegQueryValueExW(key, val.as_ptr(), null(), &mut ty, null_mut(), &mut size) };
    let result = if rc == ERROR_SUCCESS && (ty == REG_SZ || ty == REG_EXPAND_SZ) {
        let mut buf = vec![0u16; (size as usize).div_ceil(2) + 1];
        let mut bytes = (buf.len() * 2) as u32;
        // SAFETY: `buf` holds `bytes` bytes.
        let rc2 = unsafe {
            RegQueryValueExW(key, val.as_ptr(), null(), &mut ty, buf.as_mut_ptr() as *mut u8, &mut bytes)
        };
        if rc2 == ERROR_SUCCESS {
            let mut n = (bytes as usize) / 2;
            while n > 0 && buf[n - 1] == 0 {
                n -= 1;
            }
            let s = from_wide(&buf[..n]);
            Some(if ty == REG_EXPAND_SZ { expand_environment_strings(&s) } else { s })
        } else {
            None
        }
    } else {
        None
    };
    // SAFETY: `key` was opened above.
    unsafe { RegCloseKey(key) };
    result
}

/// Opens a URL with the default browser (`ShellExecute "open"`).
pub fn shell_open(target: &OsStr) -> bool {
    let verb = wide_nul(OsStr::new("open"));
    let file = wide_nul(target);
    // SAFETY: valid NUL-terminated strings; other parameters may be null.
    let h = unsafe { ShellExecuteW(null_mut(), verb.as_ptr(), file.as_ptr(), null(), null(), SW_SHOWNORMAL) };
    (h as isize) > 32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folders_and_messages() {
        assert!(!get_folder_path(KnownFolder::LocalApplicationData).is_empty());
        assert!(!system_directory().is_empty());
        assert!(!format_message(2).is_empty());
        assert!(format_message(0x7FFF_FFF0).starts_with("Unknown error (0x"));
    }
}
