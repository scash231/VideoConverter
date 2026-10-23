//! Text decoding of redirected output like .NET's `StreamReader` with
//! `StandardOutputEncoding = UTF8`: a byte order mark at the start selects (and is removed
//! from) the encoding, invalid UTF-8 becomes U+FFFD per maximal subpart, and lines end at
//! `\n`, `\r` or `\r\n`.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Enc {
    Undetected,
    Utf8,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,
}

/// Incremental decoder producing text.
pub struct Decoder {
    enc: Enc,
    pending: Vec<u8>,
}

impl Default for Decoder {
    fn default() -> Self {
        Self::new()
    }
}

impl Decoder {
    pub fn new() -> Self {
        Decoder { enc: Enc::Undetected, pending: Vec::new() }
    }

    fn detect(&mut self, eof: bool) -> bool {
        if self.enc != Enc::Undetected {
            return true;
        }
        let p = &self.pending;
        if p.len() < 4 && !eof {
            // Wait for enough bytes to recognise every BOM (StreamReader does the same).
            let prefixes: [&[u8]; 5] = [&[0xEF, 0xBB, 0xBF], &[0xFE, 0xFF], &[0xFF, 0xFE], &[0x00, 0x00, 0xFE, 0xFF], &[0xFF, 0xFE, 0x00, 0x00]];
            if prefixes.iter().any(|b| b.starts_with(p) || p.starts_with(b)) {
                return false;
            }
        }
        let (enc, skip) = if p.starts_with(&[0xFF, 0xFE, 0x00, 0x00]) {
            (Enc::Utf32Le, 4)
        } else if p.starts_with(&[0x00, 0x00, 0xFE, 0xFF]) {
            (Enc::Utf32Be, 4)
        } else if p.starts_with(&[0xEF, 0xBB, 0xBF]) {
            (Enc::Utf8, 3)
        } else if p.starts_with(&[0xFE, 0xFF]) {
            (Enc::Utf16Be, 2)
        } else if p.starts_with(&[0xFF, 0xFE]) {
            (Enc::Utf16Le, 2)
        } else {
            (Enc::Utf8, 0)
        };
        self.enc = enc;
        self.pending.drain(..skip);
        true
    }

    /// Decodes `bytes`, appending text to `out`. Incomplete sequences wait for more input.
    pub fn push(&mut self, bytes: &[u8], out: &mut String) {
        self.pending.extend_from_slice(bytes);
        if !self.detect(false) {
            return;
        }
        self.drain(out, false);
    }

    /// Flushes everything at end of stream.
    pub fn finish(&mut self, out: &mut String) {
        self.detect(true);
        self.drain(out, true);
    }

    fn drain(&mut self, out: &mut String, eof: bool) {
        match self.enc {
            Enc::Utf8 | Enc::Undetected => self.drain_utf8(out, eof),
            Enc::Utf16Le | Enc::Utf16Be => self.drain_utf16(out, eof),
            Enc::Utf32Le | Enc::Utf32Be => self.drain_utf32(out, eof),
        }
    }

    fn drain_utf8(&mut self, out: &mut String, eof: bool) {
        let buf = std::mem::take(&mut self.pending);
        let mut pos = 0;
        loop {
            match std::str::from_utf8(&buf[pos..]) {
                Ok(s) => {
                    out.push_str(s);
                    return;
                }
                Err(e) => {
                    let valid = e.valid_up_to();
                    out.push_str(std::str::from_utf8(&buf[pos..pos + valid]).unwrap_or_default());
                    pos += valid;
                    match e.error_len() {
                        Some(n) => {
                            out.push('\u{FFFD}');
                            pos += n;
                        }
                        None => {
                            if eof {
                                out.push('\u{FFFD}');
                            } else {
                                self.pending = buf[pos..].to_vec();
                            }
                            return;
                        }
                    }
                }
            }
        }
    }

    fn drain_utf16(&mut self, out: &mut String, eof: bool) {
        let usable = self.pending.len() / 2 * 2;
        let units: Vec<u16> = self.pending[..usable]
            .chunks_exact(2)
            .map(|c| if self.enc == Enc::Utf16Le { u16::from_le_bytes([c[0], c[1]]) } else { u16::from_be_bytes([c[0], c[1]]) })
            .collect();
        let mut keep_from = units.len();
        if !eof && units.last().is_some_and(|&u| (0xD800..=0xDBFF).contains(&u)) {
            keep_from -= 1;
        }
        out.push_str(&String::from_utf16_lossy(&units[..keep_from]));
        let rest: Vec<u8> = self.pending[keep_from * 2..].to_vec();
        self.pending = rest;
        if eof && !self.pending.is_empty() {
            out.push('\u{FFFD}');
            self.pending.clear();
        }
    }

    fn drain_utf32(&mut self, out: &mut String, eof: bool) {
        let usable = self.pending.len() / 4 * 4;
        for c in self.pending[..usable].chunks_exact(4) {
            let v = if self.enc == Enc::Utf32Le {
                u32::from_le_bytes([c[0], c[1], c[2], c[3]])
            } else {
                u32::from_be_bytes([c[0], c[1], c[2], c[3]])
            };
            out.push(char::from_u32(v).unwrap_or('\u{FFFD}'));
        }
        self.pending.drain(..usable);
        if eof && !self.pending.is_empty() {
            out.push('\u{FFFD}');
            self.pending.clear();
        }
    }
}

/// Splits decoded text into lines like `StreamReader.ReadLine` / `OutputDataReceived`.
#[derive(Default)]
pub struct LineSplitter {
    buf: String,
    last_cr: bool,
}

impl LineSplitter {
    /// Feeds text and returns the completed lines.
    pub fn push(&mut self, text: &str) -> Vec<String> {
        let mut lines = Vec::new();
        for ch in text.chars() {
            if self.last_cr {
                self.last_cr = false;
                if ch == '\n' {
                    continue;
                }
            }
            match ch {
                '\r' => {
                    lines.push(std::mem::take(&mut self.buf));
                    self.last_cr = true;
                }
                '\n' => lines.push(std::mem::take(&mut self.buf)),
                c => self.buf.push(c),
            }
        }
        lines
    }

    /// The final unterminated line, if any.
    pub fn finish(&mut self) -> Option<String> {
        if self.buf.is_empty() {
            None
        } else {
            Some(std::mem::take(&mut self.buf))
        }
    }
}

/// Decodes a complete byte buffer (`StreamReader.ReadToEnd`).
pub fn decode_all(bytes: &[u8]) -> String {
    let mut d = Decoder::new();
    let mut s = String::new();
    d.push(bytes, &mut s);
    d.finish(&mut s);
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bom_and_invalid_bytes() {
        assert_eq!(decode_all(b"\xEF\xBB\xBFhello"), "hello");
        assert_eq!(decode_all(b"a\xFFb"), "a\u{FFFD}b");
        assert_eq!(decode_all(b"\xE2\x82"), "\u{FFFD}");
        assert_eq!(decode_all(&[0xFF, 0xFE, b'h', 0, b'i', 0]), "hi");
    }

    #[test]
    fn split_utf8_across_chunks() {
        let mut d = Decoder::new();
        let mut s = String::new();
        let bytes = "größe".as_bytes();
        for b in bytes {
            d.push(&[*b], &mut s);
        }
        d.finish(&mut s);
        assert_eq!(s, "größe");
    }

    #[test]
    fn line_endings_like_readline() {
        let mut l = LineSplitter::default();
        let mut out = l.push("a\r\nb\rc\n\nd\r");
        out.extend(l.push("\ne"));
        out.extend(l.finish());
        assert_eq!(out, vec!["a", "b", "c", "", "d", "e"]);
    }
}
