//! `System.IO.File` / `Directory` with the Windows semantics .NET uses (no overwrite on move,
//! read-only files fail recursive deletes, wildcard matching, error texts).

use std::ffi::{OsStr, OsString};
use std::ptr::null_mut;

use windows_sys::Win32::Foundation::{GetLastError, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateDirectoryW, DeleteFileW, DeleteVolumeMountPointW, FindClose, FindFirstFileW, FindNextFileW,
    GetFileAttributesExW, GetFileExInfoStandard, MoveFileExW, RemoveDirectoryW, FILE_ATTRIBUTE_DIRECTORY,
    FILE_ATTRIBUTE_REPARSE_POINT, MOVEFILE_COPY_ALLOWED, MOVEFILE_REPLACE_EXISTING, WIN32_FILE_ATTRIBUTE_DATA,
    WIN32_FIND_DATAW,
};

use super::ioerr::{
    from_win32, NetError, NetErrorKind, ERROR_ACCESS_DENIED, ERROR_ALREADY_EXISTS, ERROR_DIR_NOT_EMPTY,
    ERROR_FILE_NOT_FOUND, ERROR_NO_MORE_FILES, ERROR_PATH_NOT_FOUND,
};
use super::netstr::{from_wide, wide, wide_nul};
use super::path::{ends_in_separator, get_full_path, get_path_root, is_sep, join, root_length};
use super::text::upper_unit;
use crate::ncat;

const IO_REPARSE_TAG_MOUNT_POINT: u32 = 0xA000_0003;
const IO_REPARSE_TAG_NAME_SURROGATE_BIT: u32 = 0x2000_0000;

fn last_error() -> u32 {
    // SAFETY: trivial FFI call.
    unsafe { GetLastError() }
}

/// `FileSystem.IsPathUnreachableError`.
fn is_unreachable(code: u32) -> bool {
    matches!(code, 2 | 3 | 15 | 21 | 53 | 65 | 67 | 87 | 123 | 161 | 206 | 1231)
}

/// `FileSystem.FillAttributeInfo(path, ref data, returnErrorOnNotFound: true)`.
/// Returns the attributes or the Win32 error code.
fn fill_attribute_info(full_path: &OsStr) -> Result<u32, u32> {
    let mut w = wide(full_path);
    // PathInternal.TrimEndingDirectorySeparator (but never below the root).
    if w.len() > root_length(&w) && w.last().is_some_and(|&u| is_sep(u)) {
        w.pop();
    }
    w.push(0);
    let mut data: WIN32_FILE_ATTRIBUTE_DATA = unsafe { std::mem::zeroed() };
    // SAFETY: `w` is NUL-terminated; `data` is a valid out buffer.
    let ok = unsafe { GetFileAttributesExW(w.as_ptr(), GetFileExInfoStandard, &mut data as *mut _ as *mut _) };
    if ok != 0 {
        return Ok(data.dwFileAttributes);
    }
    let code = last_error();
    if is_unreachable(code) {
        return Err(code);
    }
    let mut fd: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
    // SAFETY: `w` is NUL-terminated; `fd` is a valid out buffer.
    let h = unsafe { FindFirstFileW(w.as_ptr(), &mut fd) };
    if h == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    // SAFETY: handle returned by FindFirstFileW.
    unsafe { FindClose(h) };
    Ok(fd.dwFileAttributes)
}

/// `File.Exists(path)`.
pub fn file_exists(path: impl AsRef<OsStr>) -> bool {
    let path = path.as_ref();
    if path.is_empty() {
        return false;
    }
    let full = match get_full_path(path) {
        Ok(f) => f,
        Err(_) => return false,
    };
    if ends_in_separator(&full) {
        return false;
    }
    matches!(fill_attribute_info(&full), Ok(a) if a & FILE_ATTRIBUTE_DIRECTORY == 0)
}

/// `File.Exists` for an optional path (`File.Exists(null)` is false).
pub fn file_exists_opt(path: Option<&OsStr>) -> bool {
    path.is_some_and(file_exists)
}

/// `Directory.Exists(path)`.
pub fn directory_exists(path: impl AsRef<OsStr>) -> bool {
    let path = path.as_ref();
    if path.is_empty() {
        return false;
    }
    match get_full_path(path) {
        Ok(full) => matches!(fill_attribute_info(&full), Ok(a) if a & FILE_ATTRIBUTE_DIRECTORY != 0),
        Err(_) => false,
    }
}

/// `File.Delete(path)`: a missing file is not an error.
pub fn delete_file(path: impl AsRef<OsStr>) -> Result<(), NetError> {
    let full = get_full_path(path.as_ref())?;
    let w = wide_nul(&full);
    // SAFETY: NUL-terminated path.
    if unsafe { DeleteFileW(w.as_ptr()) } == 0 {
        let code = last_error();
        if code != ERROR_FILE_NOT_FOUND {
            return Err(from_win32(code, Some(&full)));
        }
    }
    Ok(())
}

/// `File.Move(source, dest, overwrite)`: `MoveFileExW` without `MOVEFILE_REPLACE_EXISTING`
/// unless `overwrite` (Rust's `fs::rename` always replaces, so it cannot be used).
pub fn move_file(src: impl AsRef<OsStr>, dst: impl AsRef<OsStr>, overwrite: bool) -> Result<(), NetError> {
    let full_src = get_full_path(src.as_ref())?;
    let full_dst = get_full_path(dst.as_ref())?;
    if !matches!(fill_attribute_info(&full_src), Ok(a) if a & FILE_ATTRIBUTE_DIRECTORY == 0) {
        return Err(NetError::new(NetErrorKind::FileNotFound, ncat!("Could not find file '", &full_src, "'.")));
    }
    let s = wide_nul(&full_src);
    let d = wide_nul(&full_dst);
    let flags = MOVEFILE_COPY_ALLOWED | if overwrite { MOVEFILE_REPLACE_EXISTING } else { 0 };
    // SAFETY: NUL-terminated paths.
    if unsafe { MoveFileExW(s.as_ptr(), d.as_ptr(), flags) } == 0 {
        let code = last_error();
        let p = if code == ERROR_FILE_NOT_FOUND { &full_src } else { &full_dst };
        return Err(from_win32(code, Some(p)));
    }
    Ok(())
}

/// `Directory.CreateDirectory(path)` (`FileSystem.CreateDirectory` on Windows).
pub fn create_directory(path: impl AsRef<OsStr>) -> Result<(), NetError> {
    let full = get_full_path(path.as_ref())?;
    let fw = wide(&full);
    if directory_exists(&full) {
        return Ok(());
    }
    let mut stack: Vec<Vec<u16>> = Vec::new();
    let mut somepath_exists = false;
    let mut length = fw.len();
    if length >= 2 && is_sep(fw[length - 1]) {
        length -= 1;
    }
    let root = root_length(&fw);
    if length > root {
        let mut i = length as isize - 1;
        while i >= root as isize && !somepath_exists {
            let dir = &fw[..(i as usize + 1)];
            if !directory_exists(from_wide(dir)) {
                stack.push(dir.to_vec());
            } else {
                somepath_exists = true;
            }
            while i > root as isize && !is_sep(fw[i as usize]) {
                i -= 1;
            }
            i -= 1;
        }
    }
    let count = stack.len();
    let mut r = true;
    let mut first_error: u32 = 0;
    let mut error_path: OsString = full.clone();
    while let Some(name) = stack.pop() {
        let mut nz = name.clone();
        nz.push(0);
        // SAFETY: NUL-terminated path; default security attributes.
        r = unsafe { CreateDirectoryW(nz.as_ptr(), null_mut()) } != 0;
        if !r && first_error == 0 {
            let current = last_error();
            if current != ERROR_ALREADY_EXISTS {
                first_error = current;
            } else {
                let name_os = from_wide(&name);
                let access_denied_on_check = match fill_attribute_info(&name_os) {
                    Err(code) => code == ERROR_ACCESS_DENIED,
                    Ok(_) => false,
                };
                if file_exists(&name_os) || (!directory_exists(&name_os) && access_denied_on_check) {
                    first_error = current;
                    error_path = name_os;
                }
            }
        }
    }
    if count == 0 && !somepath_exists {
        let root_path = get_path_root(&full);
        if !directory_exists(&root_path) {
            return Err(from_win32(ERROR_PATH_NOT_FOUND, Some(&root_path)));
        }
        return Ok(());
    }
    if !r && first_error != 0 {
        return Err(from_win32(first_error, Some(&error_path)));
    }
    Ok(())
}

struct FindEntry {
    name: Vec<u16>,
    attributes: u32,
    reserved0: u32,
}

/// Lists `dir\*` with `FindFirstFileW`/`FindNextFileW` (file system order, like .NET's
/// `NtQueryDirectoryFile` enumeration). `.` and `..` are skipped.
fn find_entries(dir: &OsStr) -> Result<Vec<FindEntry>, u32> {
    let pattern = wide_nul(&join(dir, "*"));
    let mut fd: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
    // SAFETY: NUL-terminated pattern; valid out buffer.
    let h = unsafe { FindFirstFileW(pattern.as_ptr(), &mut fd) };
    if h == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    let mut out = Vec::new();
    loop {
        let n = fd.cFileName.iter().position(|&u| u == 0).unwrap_or(fd.cFileName.len());
        let name = fd.cFileName[..n].to_vec();
        let is_dot = name == [b'.' as u16] || name == [b'.' as u16, b'.' as u16];
        if !is_dot {
            out.push(FindEntry { name, attributes: fd.dwFileAttributes, reserved0: fd.dwReserved0 });
        }
        // SAFETY: valid handle and buffer.
        if unsafe { FindNextFileW(h, &mut fd) } == 0 {
            let code = last_error();
            // SAFETY: handle from FindFirstFileW.
            unsafe { FindClose(h) };
            if code != ERROR_NO_MORE_FILES {
                return Err(code);
            }
            break;
        }
    }
    Ok(out)
}

/// `Directory.GetFiles(dir)`: full paths built with `Path.Join(dir, name)`.
pub fn get_files(dir: impl AsRef<OsStr>) -> Result<Vec<OsString>, NetError> {
    get_files_where(dir.as_ref(), |_| true)
}

/// `Directory.GetFiles(dir, "{stem}.*")`. The Win32 expression `stem.*` translates to
/// `stem` + DOS_DOT + `*`, i.e. the name equals `stem` or starts with `stem.`
/// (case-insensitive). Only valid for stems without wildcard characters.
pub fn get_files_stem_dot_star(dir: impl AsRef<OsStr>, stem: &str) -> Result<Vec<OsString>, NetError> {
    let s: Vec<u16> = stem.encode_utf16().map(upper_unit).collect();
    get_files_where(dir.as_ref(), |name| {
        if name.len() < s.len() || !name[..s.len()].iter().map(|&u| upper_unit(u)).eq(s.iter().copied()) {
            return false;
        }
        name.len() == s.len() || name[s.len()] == b'.' as u16
    })
}

fn get_files_where(dir: &OsStr, pred: impl Fn(&[u16]) -> bool) -> Result<Vec<OsString>, NetError> {
    let full = get_full_path(dir)?;
    match find_entries(&full) {
        Ok(entries) => Ok(entries
            .into_iter()
            .filter(|e| e.attributes & FILE_ATTRIBUTE_DIRECTORY == 0 && pred(&e.name))
            .map(|e| join(dir, from_wide(&e.name)))
            .collect()),
        Err(code) => {
            let code = if code == ERROR_FILE_NOT_FOUND { ERROR_PATH_NOT_FOUND } else { code };
            Err(from_win32(code, Some(&full)))
        }
    }
}

fn is_name_surrogate_reparse_point(e: &FindEntry) -> bool {
    e.attributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 && e.reserved0 & IO_REPARSE_TAG_NAME_SURROGATE_BIT != 0
}

/// `Directory.Delete(path, recursive: true)`.
pub fn delete_directory_recursive(path: impl AsRef<OsStr>) -> Result<(), NetError> {
    let full = get_full_path(path.as_ref())?;
    remove_directory_recursive(&full, true)
}

fn remove_directory_recursive(full: &OsStr, top_level: bool) -> Result<(), NetError> {
    let entries = find_entries(full).map_err(|code| from_win32(code, Some(full)))?;
    let mut exception: Option<NetError> = None;
    for e in &entries {
        let name = from_wide(&e.name);
        let child = super::path::combine(full, &name);
        if e.attributes & FILE_ATTRIBUTE_DIRECTORY == 0 {
            let w = wide_nul(&child);
            // SAFETY: NUL-terminated path.
            if unsafe { DeleteFileW(w.as_ptr()) } == 0 && exception.is_none() {
                let code = last_error();
                if code != ERROR_FILE_NOT_FOUND {
                    exception = Some(from_win32(code, Some(&name)));
                }
            }
        } else if !is_name_surrogate_reparse_point(e) {
            if let Err(err) = remove_directory_recursive(&child, false) {
                exception.get_or_insert(err);
            }
        } else {
            if e.reserved0 == IO_REPARSE_TAG_MOUNT_POINT {
                let mount = wide_nul(&join(&child, "\\"));
                // SAFETY: NUL-terminated path.
                if unsafe { DeleteVolumeMountPointW(mount.as_ptr()) } == 0 && exception.is_none() {
                    let code = last_error();
                    if code != ERROR_PATH_NOT_FOUND {
                        exception = Some(from_win32(code, Some(&name)));
                    }
                }
            }
            let w = wide_nul(&child);
            // SAFETY: NUL-terminated path.
            if unsafe { RemoveDirectoryW(w.as_ptr()) } == 0 && exception.is_none() {
                let code = last_error();
                if code != ERROR_PATH_NOT_FOUND {
                    exception = Some(from_win32(code, Some(&name)));
                }
            }
        }
    }
    if let Some(e) = exception {
        return Err(e);
    }
    remove_directory_internal(full, top_level, true)
}

fn remove_directory_internal(full: &OsStr, top_level: bool, allow_not_empty: bool) -> Result<(), NetError> {
    let w = wide_nul(full);
    // SAFETY: NUL-terminated path.
    if unsafe { RemoveDirectoryW(w.as_ptr()) } != 0 {
        return Ok(());
    }
    let mut code = last_error();
    if code == ERROR_FILE_NOT_FOUND {
        code = ERROR_PATH_NOT_FOUND;
    }
    match code {
        ERROR_PATH_NOT_FOUND if !top_level => return Ok(()),
        ERROR_DIR_NOT_EMPTY if allow_not_empty => return Ok(()),
        ERROR_ACCESS_DENIED => {
            return Err(NetError::new(NetErrorKind::Io, ncat!("Access to the path '", full, "' is denied.")))
        }
        _ => {}
    }
    Err(from_win32(code, Some(full)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> OsString {
        let d = super::super::path::combine(super::super::path::get_temp_path(), format!("ferrite_test_{name}_{}", std::process::id()));
        let _ = delete_directory_recursive(&d);
        create_directory(&d).unwrap();
        d
    }

    #[test]
    fn stem_wildcard_matches_dotnet() {
        let d = temp_dir("glob");
        for n in ["abc", "abc.mp4", "abcd.mp4", "abc.f1.webm", "ABC.M4A", "abc_fp.mp4"] {
            std::fs::write(super::super::path::combine(&d, n), b"").unwrap();
        }
        let mut names: Vec<String> = get_files_stem_dot_star(&d, "abc")
            .unwrap()
            .into_iter()
            .map(|p| super::super::path::get_file_name(p).into_string().unwrap())
            .collect();
        names.sort_by_key(|a| a.to_lowercase());
        // .NET 10 returned: abc | abc.f1.webm | ABC.M4A | abc.mp4
        assert_eq!(names, vec!["abc", "abc.f1.webm", "ABC.M4A", "abc.mp4"]);
        delete_directory_recursive(&d).unwrap();
        assert!(!directory_exists(&d));
    }

    #[test]
    fn move_does_not_overwrite_and_exists_checks() {
        let d = temp_dir("move");
        let a = super::super::path::combine(&d, "a.txt");
        let b = super::super::path::combine(&d, "b.txt");
        std::fs::write(&a, b"a").unwrap();
        std::fs::write(&b, b"b").unwrap();
        assert!(move_file(&a, &b, false).is_err());
        assert!(file_exists(&a));
        assert!(!file_exists(super::super::path::combine(&d, "")));
        assert!(!file_exists(&d));
        assert!(directory_exists(&d));
        move_file(&a, &b, true).unwrap();
        assert!(!file_exists(&a));
        assert_eq!(std::fs::read(&b).unwrap(), b"a");
        delete_file(&a).unwrap();
        delete_directory_recursive(&d).unwrap();
    }

    #[test]
    fn create_directory_error_texts() {
        let e = create_directory(r"Q:\nodrive_ferrite\x").unwrap_err();
        assert_eq!(e.message, OsString::from(r"Could not find a part of the path 'Q:\nodrive_ferrite\x'."));
    }
}
