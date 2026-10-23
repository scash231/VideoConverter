//! Command-line construction like `ProcessStartInfo`.
//!
//! Native passes most arguments as one pre-quoted string (`ProcessStartInfo.Arguments`), which
//! .NET appends verbatim. A few calls use `ArgumentList`, which .NET quotes with
//! `PasteArguments.AppendArgument`. Ferrite reproduces both so the child sees byte-identical
//! arguments.

use std::ffi::{OsStr, OsString};

use super::netstr::{from_wide, wide};
use super::text::is_white_space_unit;

const QUOTE: u16 = b'"' as u16;
const BACKSLASH: u16 = b'\\' as u16;

/// How the arguments of a process are given.
#[derive(Debug, Clone)]
pub enum Args {
    /// `ProcessStartInfo.Arguments`: appended to the command line as-is.
    Raw(OsString),
    /// `ProcessStartInfo.ArgumentList`: quoted per argument.
    List(Vec<OsString>),
}

impl Args {
    /// The text .NET appends after `"FileName" ` (empty when there are no arguments).
    pub fn to_command_tail(&self) -> OsString {
        match self {
            Args::Raw(s) => s.clone(),
            Args::List(list) => paste_arguments(list),
        }
    }
}

fn contains_no_whitespace_or_quotes(s: &[u16]) -> bool {
    s.iter().all(|&c| !is_white_space_unit(c) && c != QUOTE)
}

/// `PasteArguments.AppendArgument` (Windows) for every argument, joined by spaces.
pub fn paste_arguments<S: AsRef<OsStr>>(args: &[S]) -> OsString {
    let mut sb: Vec<u16> = Vec::new();
    for arg in args {
        let a = wide(arg.as_ref());
        if !sb.is_empty() {
            sb.push(b' ' as u16);
        }
        if !a.is_empty() && contains_no_whitespace_or_quotes(&a) {
            sb.extend_from_slice(&a);
            continue;
        }
        sb.push(QUOTE);
        let mut idx = 0;
        while idx < a.len() {
            let c = a[idx];
            idx += 1;
            if c == BACKSLASH {
                let mut n = 1;
                while idx < a.len() && a[idx] == BACKSLASH {
                    idx += 1;
                    n += 1;
                }
                if idx == a.len() {
                    sb.extend(std::iter::repeat_n(BACKSLASH, n * 2));
                } else if a[idx] == QUOTE {
                    sb.extend(std::iter::repeat_n(BACKSLASH, n * 2 + 1));
                    sb.push(QUOTE);
                    idx += 1;
                } else {
                    sb.extend(std::iter::repeat_n(BACKSLASH, n));
                }
                continue;
            }
            if c == QUOTE {
                sb.push(BACKSLASH);
                sb.push(QUOTE);
                continue;
            }
            sb.push(c);
        }
        sb.push(QUOTE);
    }
    from_wide(&sb)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(args: &[&str]) -> String {
        paste_arguments(args).into_string().unwrap()
    }

    #[test]
    fn quoting_matches_paste_arguments() {
        assert_eq!(p(&["-y", "-i", r"C:\My Videos\a.mp4"]), r#"-y -i "C:\My Videos\a.mp4""#);
        assert_eq!(p(&[""]), r#""""#);
        assert_eq!(p(&[r"C:\dir with space\"]), r#""C:\dir with space\\""#);
        assert_eq!(p(&[r#"say "hi""#]), r#""say \"hi\"""#);
        assert_eq!(p(&[r"a\b"]), r"a\b");
        assert_eq!(p(&["scale=1:2:flags=fast_bilinear,crop=3:4"]), "scale=1:2:flags=fast_bilinear,crop=3:4");
        assert_eq!(
            p(&["-c", "import sys; print('x')"]),
            r#"-c "import sys; print('x')""#
        );
    }
}
