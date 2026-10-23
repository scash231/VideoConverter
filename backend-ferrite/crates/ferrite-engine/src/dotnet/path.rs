//! `System.IO.Path` (Windows implementation), operating on UTF-16 like .NET.

use std::ffi::{OsStr, OsString};

use windows_sys::Win32::Storage::FileSystem::GetFullPathNameW;

use super::ioerr::{argument, from_win32, NetError};
use super::netstr::{from_wide, wide, wide_nul};

const BS: u16 = b'\\' as u16;
const FS: u16 = b'/' as u16;
const COLON: u16 = b':' as u16;
const DOT: u16 = b'.' as u16;
const SPACE: u16 = b' ' as u16;
const QM: u16 = b'?' as u16;

pub fn is_sep(u: u16) -> bool {
    u == BS || u == FS
}

fn is_valid_drive_char(u: u16) -> bool {
    (u >= b'A' as u16 && u <= b'Z' as u16) || (u >= b'a' as u16 && u <= b'z' as u16)
}

fn is_extended(p: &[u16]) -> bool {
    p.len() >= 4 && p[0] == BS && (p[1] == BS || p[1] == QM) && p[2] == QM && p[3] == BS
}

fn is_device(p: &[u16]) -> bool {
    is_extended(p) || (p.len() >= 4 && is_sep(p[0]) && is_sep(p[1]) && (p[2] == DOT || p[2] == QM) && is_sep(p[3]))
}

fn is_device_unc(p: &[u16]) -> bool {
    p.len() >= 8
        && is_device(p)
        && is_sep(p[7])
        && p[4] == b'U' as u16
        && p[5] == b'N' as u16
        && p[6] == b'C' as u16
}

/// `PathInternal.GetRootLength`.
pub fn root_length(p: &[u16]) -> usize {
    let len = p.len();
    let mut i = 0;
    let device = is_device(p);
    let device_unc = device && is_device_unc(p);
    if (!device || device_unc) && len > 0 && is_sep(p[0]) {
        if device_unc || (len > 1 && is_sep(p[1])) {
            i = if device_unc { 8 } else { 2 };
            let mut n = 2;
            while i < len {
                if is_sep(p[i]) {
                    n -= 1;
                    if n == 0 {
                        break;
                    }
                }
                i += 1;
            }
        } else {
            i = 1;
        }
    } else if device {
        i = 4;
        while i < len && !is_sep(p[i]) {
            i += 1;
        }
        if i < len && i > 4 && is_sep(p[i]) {
            i += 1;
        }
    } else if len >= 2 && p[1] == COLON && is_valid_drive_char(p[0]) {
        i = 2;
        if len > 2 && is_sep(p[2]) {
            i += 1;
        }
    }
    i
}

/// `Path.IsPathRooted`.
pub fn is_path_rooted(p: &[u16]) -> bool {
    (!p.is_empty() && is_sep(p[0])) || (p.len() >= 2 && is_valid_drive_char(p[0]) && p[1] == COLON)
}

/// `Path.Combine(a, b)`.
pub fn combine(a: impl AsRef<OsStr>, b: impl AsRef<OsStr>) -> OsString {
    let a = wide(a.as_ref());
    let b = wide(b.as_ref());
    from_wide(&combine_w(&a, &b))
}

/// `Path.Combine(a, b, c, ...)` (left to right).
pub fn combine_all(parts: &[&OsStr]) -> OsString {
    let mut acc: Vec<u16> = Vec::new();
    for p in parts {
        acc = combine_w(&acc, &wide(p));
    }
    from_wide(&acc)
}

fn combine_w(a: &[u16], b: &[u16]) -> Vec<u16> {
    if a.is_empty() {
        return b.to_vec();
    }
    if b.is_empty() {
        return a.to_vec();
    }
    if is_path_rooted(b) {
        return b.to_vec();
    }
    join_w(a, b)
}

fn join_w(a: &[u16], b: &[u16]) -> Vec<u16> {
    if a.is_empty() {
        return b.to_vec();
    }
    if b.is_empty() {
        return a.to_vec();
    }
    let has_sep = is_sep(a[a.len() - 1]) || is_sep(b[0]);
    let mut v = Vec::with_capacity(a.len() + b.len() + 1);
    v.extend_from_slice(a);
    if !has_sep {
        v.push(BS);
    }
    v.extend_from_slice(b);
    v
}

/// `Path.Join(a, b)`.
pub fn join(a: impl AsRef<OsStr>, b: impl AsRef<OsStr>) -> OsString {
    from_wide(&join_w(&wide(a.as_ref()), &wide(b.as_ref())))
}

fn is_effectively_empty(p: &[u16]) -> bool {
    p.iter().all(|&u| u == SPACE)
}

/// `PathInternal.NormalizeDirectorySeparators`.
fn normalize_separators(p: &[u16]) -> Vec<u16> {
    if p.is_empty() {
        return Vec::new();
    }
    let needs = p.iter().enumerate().any(|(i, &c)| {
        is_sep(c) && (c != BS || (i > 0 && i + 1 < p.len() && is_sep(p[i + 1])))
    });
    if !needs {
        return p.to_vec();
    }
    let mut out = Vec::with_capacity(p.len());
    let mut start = 0;
    if is_sep(p[0]) {
        start = 1;
        out.push(BS);
    }
    let mut i = start;
    while i < p.len() {
        let mut c = p[i];
        if is_sep(c) {
            if i + 1 < p.len() && is_sep(p[i + 1]) {
                i += 1;
                continue;
            }
            c = BS;
        }
        out.push(c);
        i += 1;
    }
    out
}

/// `Path.GetDirectoryName`.
pub fn get_directory_name(path: impl AsRef<OsStr>) -> Option<OsString> {
    let p = wide(path.as_ref());
    if p.is_empty() || is_effectively_empty(&p) {
        return None;
    }
    let root = root_length(&p);
    let mut end = p.len();
    if end <= root {
        return None;
    }
    loop {
        end -= 1;
        if end <= root || is_sep(p[end]) {
            break;
        }
    }
    while end > root && is_sep(p[end - 1]) {
        end -= 1;
    }
    Some(from_wide(&normalize_separators(&p[..end])))
}

/// `Path.GetFileName`.
pub fn get_file_name(path: impl AsRef<OsStr>) -> OsString {
    let p = wide(path.as_ref());
    from_wide(file_name_w(&p))
}

fn file_name_w(p: &[u16]) -> &[u16] {
    let root = root_length(p);
    let mut i = p.len();
    while i > 0 {
        i -= 1;
        if i < root || is_sep(p[i]) {
            return &p[i + 1..];
        }
    }
    p
}

/// `Path.GetExtension`.
pub fn get_extension(path: impl AsRef<OsStr>) -> OsString {
    let p = wide(path.as_ref());
    let len = p.len();
    let mut i = len;
    while i > 0 {
        i -= 1;
        let c = p[i];
        if c == DOT {
            return if i != len - 1 { from_wide(&p[i..]) } else { OsString::new() };
        }
        if is_sep(c) {
            break;
        }
    }
    OsString::new()
}

/// `Path.GetFileNameWithoutExtension`.
pub fn get_file_name_without_extension(path: impl AsRef<OsStr>) -> OsString {
    let p = wide(path.as_ref());
    let name = file_name_w(&p);
    match name.iter().rposition(|&u| u == DOT) {
        Some(i) => from_wide(&name[..i]),
        None => from_wide(name),
    }
}

/// `Path.GetFullPath`.
pub fn get_full_path(path: impl AsRef<OsStr>) -> Result<OsString, NetError> {
    let p = wide(path.as_ref());
    if p.contains(&0) {
        return Err(argument("Null character in path.", "path"));
    }
    if p.is_empty() || is_effectively_empty(&p) {
        return Err(argument("The path is empty.", "path"));
    }
    if is_extended(&p) {
        return Ok(from_wide(&p));
    }
    let src = wide_nul(path.as_ref());
    let mut buf = vec![0u16; 512];
    loop {
        // SAFETY: `src` is NUL-terminated; `buf` is valid for its length.
        let n = unsafe { GetFullPathNameW(src.as_ptr(), buf.len() as u32, buf.as_mut_ptr(), std::ptr::null_mut()) }
            as usize;
        if n == 0 {
            let code = std::io::Error::last_os_error().raw_os_error().unwrap_or(0) as u32;
            return Err(from_win32(code, Some(path.as_ref())));
        }
        if n < buf.len() {
            return Ok(from_wide(&buf[..n]));
        }
        buf.resize(n + 1, 0);
    }
}

/// `Path.GetPathRoot` length helper for callers that need the root of a full path.
pub fn get_path_root(path: impl AsRef<OsStr>) -> OsString {
    let p = wide(path.as_ref());
    from_wide(&normalize_separators(&p[..root_length(&p)]))
}

/// `PathInternal.EndsInDirectorySeparator`.
pub fn ends_in_separator(path: impl AsRef<OsStr>) -> bool {
    wide(path.as_ref()).last().is_some_and(|&u| is_sep(u))
}

/// `Path.GetTempPath()` (uses `GetTempPath2W` where available, with a trailing separator).
pub fn get_temp_path() -> OsString {
    std::env::temp_dir().into_os_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(x: OsString) -> String {
        x.into_string().unwrap()
    }

    #[test]
    fn combine_and_join() {
        assert_eq!(s(combine(r"C:\a", "b")), r"C:\a\b");
        assert_eq!(s(combine(r"C:\a\", "b")), r"C:\a\b");
        assert_eq!(s(combine(r"C:\a", r"D:\x")), r"D:\x");
        assert_eq!(s(combine("", "b")), "b");
        assert_eq!(s(combine(r"C:\out", "x.%(ext)s")), r"C:\out\x.%(ext)s");
    }

    #[test]
    fn directory_name_like_dotnet() {
        assert_eq!(s(get_directory_name(r"C:\a\b\ffmpeg.exe").unwrap()), r"C:\a\b");
        assert_eq!(s(get_directory_name(r"C:\a\b\").unwrap()), r"C:\a\b");
        assert_eq!(s(get_directory_name(r"C:\a").unwrap()), r"C:\");
        assert!(get_directory_name(r"C:\").is_none());
        assert_eq!(s(get_directory_name(r"C:/a//b/c").unwrap()), r"C:\a\b");
        assert!(get_directory_name("ffmpeg").is_some_and(|d| d.is_empty()));
    }

    #[test]
    fn names_and_extensions() {
        assert_eq!(s(get_extension(r"C:\x\a.f137.mp4")), ".mp4");
        assert_eq!(s(get_extension(r"C:\x\a.")), "");
        assert_eq!(s(get_extension(r"C:\x.y\a")), "");
        assert_eq!(s(get_extension(".mp4")), ".mp4");
        assert_eq!(s(get_file_name_without_extension(r"C:\x\.mp4")), "");
        assert_eq!(s(get_file_name_without_extension(r"C:\x\abc.tar.gz")), "abc.tar");
        assert_eq!(s(get_file_name(r"C:\x\y.mp4")), "y.mp4");
        assert_eq!(s(get_file_name("C:y.mp4")), "y.mp4");
    }

    #[test]
    fn full_path_like_dotnet() {
        assert_eq!(s(get_full_path(r"C:\a\b\..\c\.\d\").unwrap()), r"C:\a\c\d\");
        assert_eq!(s(get_full_path(r"C:\a\b. ").unwrap()), r"C:\a\b");
        assert_eq!(
            get_full_path("").unwrap_err().message,
            OsString::from("The path is empty. (Parameter 'path')")
        );
    }
}
