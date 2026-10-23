//! .NET strings are UTF-16 and may contain unpaired surrogates (for example a file name cut
//! after 80 code units by `SanitizeFilename`). Rust's `OsString` is WTF-8 on Windows and keeps
//! them, so every value that can carry a file name or a path is an `OsString`.

use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::{OsStrExt, OsStringExt};

/// UTF-16 code units of `s` (no terminator).
pub fn wide(s: &OsStr) -> Vec<u16> {
    s.encode_wide().collect()
}

/// UTF-16 code units of `s` followed by a NUL terminator, for Win32 calls.
pub fn wide_nul(s: &OsStr) -> Vec<u16> {
    let mut v: Vec<u16> = s.encode_wide().collect();
    v.push(0);
    v
}

pub fn from_wide(w: &[u16]) -> OsString {
    OsString::from_wide(w)
}

/// Concatenates anything that is `AsRef<OsStr>` (`&str`, `String`, `OsString`, `Path`, ...).
#[macro_export]
macro_rules! ncat {
    ($($e:expr),* $(,)?) => {{
        let mut __s = ::std::ffi::OsString::new();
        $( __s.push($e); )*
        __s
    }};
}

/// Length in UTF-16 code units, which is what .NET's `string.Length` counts.
pub fn utf16_len(s: &OsStr) -> usize {
    s.encode_wide().count()
}
