//! String helpers with .NET semantics (`string.IsNullOrWhiteSpace`, `Trim`,
//! `ToLowerInvariant`, `StringComparison.OrdinalIgnoreCase`).

use std::ffi::{OsStr, OsString};

use super::netstr::{from_wide, wide};

/// `char.IsWhiteSpace`. Rust's `char::is_whitespace` uses the Unicode `White_Space` property,
/// which is the same set .NET uses (Zs/Zl/Zp, U+0009..U+000D, U+0085, U+00A0).
pub fn is_white_space(c: char) -> bool {
    c.is_whitespace()
}

/// `char.IsWhiteSpace` for a single UTF-16 code unit (surrogate halves are never whitespace).
pub fn is_white_space_unit(u: u16) -> bool {
    char::from_u32(u as u32).is_some_and(is_white_space)
}

/// `string.IsNullOrWhiteSpace` for an optional string.
pub fn is_null_or_whitespace(s: Option<&str>) -> bool {
    match s {
        None => true,
        Some(s) => s.chars().all(is_white_space),
    }
}

pub fn is_blank(s: &str) -> bool {
    s.chars().all(is_white_space)
}

/// `string.IsNullOrWhiteSpace` for UTF-16 data.
pub fn is_blank_os(s: &OsStr) -> bool {
    wide(s).into_iter().all(is_white_space_unit)
}

/// `string.Trim()` on UTF-16 code units.
pub fn trim_wide(w: &[u16]) -> &[u16] {
    let mut start = 0;
    let mut end = w.len();
    while start < end && is_white_space_unit(w[start]) {
        start += 1;
    }
    while end > start && is_white_space_unit(w[end - 1]) {
        end -= 1;
    }
    &w[start..end]
}

pub fn trim_os(s: &OsStr) -> OsString {
    from_wide(trim_wide(&wide(s)))
}

/// `string.Trim(params char[])` on UTF-16 code units.
pub fn trim_chars_wide<'a>(w: &'a [u16], chars: &[u16]) -> &'a [u16] {
    let mut start = 0;
    let mut end = w.len();
    while start < end && chars.contains(&w[start]) {
        start += 1;
    }
    while end > start && chars.contains(&w[end - 1]) {
        end -= 1;
    }
    &w[start..end]
}

/// `char.ToLowerInvariant`: simple (1:1) case mapping. .NET leaves U+0130 (capital I with dot)
/// unchanged in invariant mode, while Rust's full mapping would turn it into two characters.
pub fn to_lower_invariant_char(c: char) -> char {
    if c == '\u{0130}' {
        return c;
    }
    let mut it = c.to_lowercase();
    match (it.next(), it.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

/// `char.ToUpperInvariant`: simple (1:1) case mapping.
pub fn to_upper_invariant_char(c: char) -> char {
    if c == '\u{0131}' {
        return c;
    }
    let mut it = c.to_uppercase();
    match (it.next(), it.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// `string.ToLowerInvariant()`.
pub fn to_lower_invariant(s: &str) -> String {
    s.chars().map(to_lower_invariant_char).collect()
}

/// `string.ToUpperInvariant()`.
pub fn to_upper_invariant(s: &str) -> String {
    s.chars().map(to_upper_invariant_char).collect()
}

/// Upper-casing used by `StringComparison.OrdinalIgnoreCase`. .NET does not map the dotless i
/// (U+0131) or the long s (U+017F) onto ASCII letters.
fn ordinal_upper(c: char) -> char {
    if c == '\u{0131}' || c == '\u{017F}' {
        return c;
    }
    to_upper_invariant_char(c)
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    let mut ia = a.chars();
    let mut ib = b.chars();
    loop {
        match (ia.next(), ib.next()) {
            (None, None) => return true,
            (Some(x), Some(y)) if ordinal_upper(x) == ordinal_upper(y) => continue,
            _ => return false,
        }
    }
}

/// `Equals(a, b, OrdinalIgnoreCase)` for nullable strings (`string.Equals(null, x, ...)`).
pub fn eq_ignore_case_opt(a: Option<&str>, b: &str) -> bool {
    a.is_some_and(|a| eq_ignore_case(a, b))
}

fn fold(s: &str) -> Vec<char> {
    s.chars().map(ordinal_upper).collect()
}

/// `string.Contains(value, StringComparison.OrdinalIgnoreCase)`.
pub fn contains_ignore_case(hay: &str, needle: &str) -> bool {
    let h = fold(hay);
    let n = fold(needle);
    if n.is_empty() {
        return true;
    }
    h.windows(n.len()).any(|w| w == n.as_slice())
}

/// `string.StartsWith(value, StringComparison.OrdinalIgnoreCase)`.
pub fn starts_with_ignore_case(s: &str, prefix: &str) -> bool {
    let h = fold(s);
    let p = fold(prefix);
    h.len() >= p.len() && h[..p.len()] == p[..]
}

/// `string.StartsWith(value, OrdinalIgnoreCase)` on UTF-16 data with an ASCII-only prefix
/// (job stems are hex), so case folding can be done per code unit.
pub fn starts_with_ignore_case_wide(s: &[u16], prefix_ascii: &str) -> bool {
    let p: Vec<u16> = prefix_ascii.encode_utf16().collect();
    if s.len() < p.len() {
        return false;
    }
    s.iter().zip(p.iter()).all(|(&a, &b)| upper_unit(a) == upper_unit(b))
}

/// Ordinal upper-casing of a UTF-16 code unit (surrogate halves map to themselves).
pub fn upper_unit(u: u16) -> u16 {
    match char::from_u32(u as u32) {
        Some(c) => {
            let up = ordinal_upper(c) as u32;
            if up <= 0xFFFF {
                up as u16
            } else {
                u
            }
        }
        None => u,
    }
}

/// `string.Equals(a, b, OrdinalIgnoreCase)` on UTF-16 data.
pub fn eq_ignore_case_wide(a: &[u16], b: &[u16]) -> bool {
    a.len() == b.len() && a.iter().zip(b).all(|(&x, &y)| upper_unit(x) == upper_unit(y))
}

/// `string.Contains(string)` (ordinal) on UTF-16 data.
pub fn contains_wide(hay: &[u16], needle: &str) -> bool {
    let n: Vec<u16> = needle.encode_utf16().collect();
    if n.is_empty() {
        return true;
    }
    hay.windows(n.len()).any(|w| w == n.as_slice())
}

/// `ToLowerInvariant().Contains(needle)` where `needle` is ASCII lower case.
pub fn lower_contains_wide(hay: &[u16], needle_ascii_lower: &str) -> bool {
    let lowered: Vec<u16> = hay
        .iter()
        .map(|&u| match char::from_u32(u as u32) {
            Some(c) => {
                let l = to_lower_invariant_char(c) as u32;
                if l <= 0xFFFF {
                    l as u16
                } else {
                    u
                }
            }
            None => u,
        })
        .collect();
    contains_wide(&lowered, needle_ascii_lower)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lower_invariant_keeps_dotted_capital_i() {
        assert_eq!(to_lower_invariant("İSTANBUL"), "İstanbul");
        assert_eq!(to_lower_invariant("1080P"), "1080p");
    }

    #[test]
    fn ignore_case_comparisons() {
        assert!(eq_ignore_case("UPLOAD", "upload"));
        assert!(!eq_ignore_case("upload", "uploads"));
        assert!(contains_ignore_case("ERROR: Private Video here", "private video"));
        assert!(starts_with_ignore_case("YTMusic_flac", "ytmusic"));
        assert!(!eq_ignore_case("ı", "I"));
    }

    #[test]
    fn whitespace_rules() {
        assert!(is_null_or_whitespace(None));
        assert!(is_null_or_whitespace(Some(" \u{00A0}\t\u{2003}")));
        assert!(!is_null_or_whitespace(Some(" x ")));
        let w: Vec<u16> = "  a b \u{3000}".encode_utf16().collect();
        assert_eq!(String::from_utf16(trim_wide(&w)).unwrap(), "a b");
    }
}
