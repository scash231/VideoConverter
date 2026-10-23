//! Port of Native's small helpers: `CleanUrl`, `IsYtMusicUrl`, `SanitizeFilename`,
//! `FindDownloadedFile`, `CleanStemFiles`, `SafeDelete`, `LastLine`, and the id formats.

use std::ffi::{OsStr, OsString};
use std::sync::LazyLock;
use std::time::Duration;

use regex::Regex;

use crate::dotnet::fs::{delete_file, file_exists, get_files, get_files_stem_dot_star};
use crate::dotnet::netstr::{from_wide, wide};
use crate::dotnet::path::{get_extension, get_file_name, get_full_path};
use crate::dotnet::text::{
    eq_ignore_case_wide, is_blank_os, lower_contains_wide, starts_with_ignore_case_wide, to_lower_invariant,
    trim_chars_wide, trim_wide,
};

/// `Guid.NewGuid().ToString("N")[..len]`.
pub fn guid_prefix(len: usize) -> String {
    uuid::Uuid::new_v4().simple().to_string()[..len].to_owned()
}

/// `InvisibleCharsRegex` set (all BMP, so it can be applied per UTF-16 code unit).
fn is_invisible(u: u16) -> bool {
    matches!(u,
        0x200B | 0x200C | 0x200D | 0x200E | 0x200F | 0x202A | 0x202B | 0x202C | 0x202D | 0x202E
        | 0x2060 | 0x2066 | 0x2067 | 0x2068 | 0x2069 | 0xFEFF | 0x00AD | 0x034F | 0x061C | 0x115F
        | 0x1160 | 0x17B4 | 0x17B5 | 0x180E | 0xFFF0..=0xFFF8 | 0xFFFA..=0xFFFD)
}

/// `CleanUrl(raw)`.
pub fn clean_url(raw: Option<&OsStr>) -> OsString {
    let Some(raw) = raw else { return OsString::new() };
    if is_blank_os(raw) {
        return OsString::new();
    }
    let kept: Vec<u16> = wide(raw).into_iter().filter(|&u| !is_invisible(u)).collect();
    from_wide(trim_wide(&kept))
}

/// `IsYtMusicUrl(url)`: `url.ToLowerInvariant().Contains("music.youtube.com")`.
pub fn is_yt_music_url(url: &OsStr) -> bool {
    lower_contains_wide(&wide(url), "music.youtube.com")
}

/// `url.Contains("watch?v=") || url.Contains("/watch?")`.
pub fn is_track_url(url: &OsStr) -> bool {
    let w = wide(url);
    crate::dotnet::text::contains_wide(&w, "watch?v=") || crate::dotnet::text::contains_wide(&w, "/watch?")
}

static RESERVED: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^(CON|PRN|AUX|NUL|COM\p{Nd}|LPT\p{Nd})$").expect("valid regex"));

/// `SanitizeFilename(name)` on UTF-16 (the 80-unit cut can split a surrogate pair, exactly
/// like the C# substring).
pub fn sanitize_filename(name: &OsStr) -> OsString {
    let replaced: Vec<u16> = wide(name)
        .into_iter()
        .map(|u| match u {
            0x00..=0x1F | 0x3C | 0x3E | 0x3A | 0x22 | 0x2F | 0x5C | 0x7C | 0x3F | 0x2A => b'_' as u16,
            _ => u,
        })
        .collect();
    let mut clean: Vec<u16> = trim_chars_wide(&replaced, &[b' ' as u16, b'.' as u16]).to_vec();
    if let Ok(s) = String::from_utf16(&clean) {
        if RESERVED.is_match(&s) {
            clean.insert(0, b'_' as u16);
        }
    }
    if clean.len() > 80 {
        clean.truncate(80);
    } else if clean.is_empty() {
        return OsString::from("video");
    }
    from_wide(&clean)
}

/// `FindDownloadedFile(dir, stem, skipImages)`.
pub fn find_downloaded_file(dir: &OsStr, stem: &str, skip_images: bool) -> Option<OsString> {
    let files = get_files_stem_dot_star(dir, stem).ok()?;
    for f in files {
        let ext = to_lower_invariant(&get_extension(&f).to_string_lossy());
        if matches!(ext.as_str(), ".part" | ".ytdl" | ".temp" | ".aria2") {
            continue;
        }
        if skip_images && matches!(ext.as_str(), ".jpg" | ".jpeg" | ".webp" | ".png") {
            continue;
        }
        if file_exists(&f) {
            return Some(f);
        }
    }
    None
}

/// `CleanStemFiles(dir, stem, excludePath)`.
pub async fn clean_stem_files(dir: Option<&OsStr>, stem: Option<&str>, exclude: Option<&OsStr>) {
    let (Some(dir), Some(stem)) = (dir, stem) else { return };
    if dir.is_empty() || stem.is_empty() || !crate::dotnet::fs::directory_exists(dir) {
        return;
    }
    let exclude_abs = match exclude {
        Some(e) => match get_full_path(e) {
            Ok(p) => Some(wide(&p)),
            Err(_) => return,
        },
        None => None,
    };
    let Ok(files) = get_files(dir) else { return };
    for f in files {
        if !starts_with_ignore_case_wide(&wide(&get_file_name(&f)), stem) {
            continue;
        }
        if let Some(ex) = &exclude_abs {
            if let Ok(full) = get_full_path(&f) {
                if eq_ignore_case_wide(&wide(&full), ex) {
                    continue;
                }
            }
        }
        safe_delete(Some(&f)).await;
    }
}

/// `SafeDelete(path)`: up to 5 attempts, 50 ms apart.
pub async fn safe_delete(path: Option<&OsStr>) {
    let Some(path) = path else { return };
    if path.is_empty() || !file_exists(path) {
        return;
    }
    for _ in 0..5 {
        if delete_file(path).is_ok() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

/// `LastLine(text)`: the last non-empty line after trimming each line.
pub fn last_line(text: &str) -> Option<String> {
    text.split('\n').map(str::trim).rfind(|l| !l.is_empty()).map(str::to_owned)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clean_url_removes_invisible_characters() {
        let raw = OsString::from(" \u{200B}https://youtu.be/x\u{FEFF}\u{00AD} ");
        assert_eq!(clean_url(Some(&raw)), OsString::from("https://youtu.be/x"));
        assert_eq!(clean_url(None), OsString::new());
        assert_eq!(clean_url(Some(OsStr::new("   "))), OsString::new());
    }

    #[test]
    fn url_kinds() {
        assert!(is_yt_music_url(OsStr::new("https://MUSIC.youtube.com/watch?v=1")));
        assert!(!is_yt_music_url(OsStr::new("https://www.youtube.com/watch?v=1&list=x")));
        assert!(is_track_url(OsStr::new("https://music.youtube.com/watch?v=abc")));
        assert!(!is_track_url(OsStr::new("https://music.youtube.com/playlist?list=abc")));
    }

    #[test]
    fn sanitize_like_csharp() {
        assert_eq!(sanitize_filename(OsStr::new("a<b>:c\"d/e\\f|g?h*")), OsString::from("a_b__c_d_e_f_g_h_"));
        assert_eq!(sanitize_filename(OsStr::new("  ..My Title.. ")), OsString::from("My Title"));
        assert_eq!(sanitize_filename(OsStr::new("con")), OsString::from("_con"));
        assert_eq!(sanitize_filename(OsStr::new("COM1")), OsString::from("_COM1"));
        assert_eq!(sanitize_filename(OsStr::new("COM10")), OsString::from("COM10"));
        assert_eq!(sanitize_filename(OsStr::new("...")), OsString::from("video"));
        let long = "x".repeat(79) + "\u{1F3AC}";
        let s = sanitize_filename(OsStr::new(&long));
        let w = wide(&s);
        assert_eq!(w.len(), 80);
        assert_eq!(w[79], 0xD83C); // high surrogate left alone, like C#'s substring
    }

    #[test]
    fn last_line_rules() {
        assert_eq!(last_line("a\n  b  \n\n"), Some("b".into()));
        assert_eq!(last_line(" \n "), None);
    }
}
