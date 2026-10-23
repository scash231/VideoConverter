//! A `System.Text.Json.JsonDocument` look-alike.
//!
//! Native parses yt-dlp / ffprobe / Dropbox output with `JsonDocument` and reads it through
//! `JsonElement`. Several of those accessors **throw** when the value has an unexpected kind
//! (`GetString` on a number, `TryGetDouble` on `null`, ...), and Native's `catch` blocks turn
//! that into "no metadata" / "try the next playlist stage". To reproduce that exactly, this
//! module parses with the same strictness as `JsonDocument.Parse` (no comments, no trailing
//! commas, max depth 64) and exposes accessors with the same throw rules. Strings stay escaped
//! until they are read, like in System.Text.Json, so an invalid surrogate only fails the
//! property that is actually accessed.

use std::fmt;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JsonError(pub String);

impl fmt::Display for JsonError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for JsonError {}

pub type JResult<T> = Result<T, JsonError>;

fn err<T>(msg: impl Into<String>) -> JResult<T> {
    Err(JsonError(msg.into()))
}

/// `JsonValueKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

#[derive(Debug, Clone)]
enum Node {
    /// Properties as (raw key span, value).
    Object(Vec<((usize, usize), NodeRef)>),
    Array(Vec<NodeRef>),
    /// Raw (still escaped) content span between the quotes.
    String(usize, usize),
    Number,
    True,
    False,
    Null,
}

#[derive(Debug, Clone)]
struct NodeRef {
    node: Box<Node>,
    start: usize,
    end: usize,
}

/// `JsonDocument`.
#[derive(Debug, Clone)]
pub struct JsonDocument {
    src: String,
    root: NodeRef,
}

const MAX_DEPTH: usize = 64;

impl JsonDocument {
    /// `JsonDocument.Parse(string)` with default options.
    pub fn parse(text: &str) -> JResult<JsonDocument> {
        let mut p = Parser { s: text.as_bytes(), i: 0, depth: 0 };
        p.skip_ws();
        if p.i >= p.s.len() {
            return err("The input does not contain any JSON tokens.");
        }
        let root = p.value()?;
        p.skip_ws();
        if p.i != p.s.len() {
            return err("'value' is invalid after a single JSON value.");
        }
        Ok(JsonDocument { src: text.to_owned(), root })
    }

    /// `JsonDocument.RootElement`.
    pub fn root(&self) -> JsonElement<'_> {
        JsonElement { doc: self, node: &self.root }
    }
}

struct Parser<'a> {
    s: &'a [u8],
    i: usize,
    depth: usize,
}

impl Parser<'_> {
    fn skip_ws(&mut self) {
        while self.i < self.s.len() && matches!(self.s[self.i], b' ' | b'\t' | b'\n' | b'\r') {
            self.i += 1;
        }
    }

    fn value(&mut self) -> JResult<NodeRef> {
        self.skip_ws();
        let start = self.i;
        let c = match self.s.get(self.i) {
            Some(&c) => c,
            None => return err("Expected a value but reached the end of data."),
        };
        let node = match c {
            b'{' => self.object()?,
            b'[' => self.array()?,
            b'"' => {
                let (a, b) = self.string()?;
                Node::String(a, b)
            }
            b't' => self.literal(b"true", Node::True)?,
            b'f' => self.literal(b"false", Node::False)?,
            b'n' => self.literal(b"null", Node::Null)?,
            b'-' | b'0'..=b'9' => {
                self.number()?;
                Node::Number
            }
            _ => return err(format!("'{}' is an invalid start of a value.", c as char)),
        };
        Ok(NodeRef { node: Box::new(node), start, end: self.i })
    }

    fn literal(&mut self, word: &[u8], node: Node) -> JResult<Node> {
        if self.s.len() - self.i >= word.len() && &self.s[self.i..self.i + word.len()] == word {
            self.i += word.len();
            Ok(node)
        } else {
            err("Invalid literal.")
        }
    }

    fn enter(&mut self) -> JResult<()> {
        self.depth += 1;
        if self.depth > MAX_DEPTH {
            return err("The maximum configured depth of 64 has been exceeded.");
        }
        Ok(())
    }

    fn object(&mut self) -> JResult<Node> {
        self.enter()?;
        self.i += 1; // '{'
        let mut props = Vec::new();
        self.skip_ws();
        if self.s.get(self.i) == Some(&b'}') {
            self.i += 1;
            self.depth -= 1;
            return Ok(Node::Object(props));
        }
        loop {
            self.skip_ws();
            if self.s.get(self.i) != Some(&b'"') {
                return err("Expected a property name.");
            }
            let key = self.string()?;
            self.skip_ws();
            if self.s.get(self.i) != Some(&b':') {
                return err("Expected ':' after a property name.");
            }
            self.i += 1;
            let v = self.value()?;
            props.push((key, v));
            self.skip_ws();
            match self.s.get(self.i) {
                Some(b',') => {
                    self.i += 1;
                    self.skip_ws();
                    if self.s.get(self.i) == Some(&b'}') {
                        return err("Trailing commas are not allowed.");
                    }
                }
                Some(b'}') => {
                    self.i += 1;
                    break;
                }
                _ => return err("Expected ',' or '}'."),
            }
        }
        self.depth -= 1;
        Ok(Node::Object(props))
    }

    fn array(&mut self) -> JResult<Node> {
        self.enter()?;
        self.i += 1; // '['
        let mut items = Vec::new();
        self.skip_ws();
        if self.s.get(self.i) == Some(&b']') {
            self.i += 1;
            self.depth -= 1;
            return Ok(Node::Array(items));
        }
        loop {
            items.push(self.value()?);
            self.skip_ws();
            match self.s.get(self.i) {
                Some(b',') => {
                    self.i += 1;
                    self.skip_ws();
                    if self.s.get(self.i) == Some(&b']') {
                        return err("Trailing commas are not allowed.");
                    }
                }
                Some(b']') => {
                    self.i += 1;
                    break;
                }
                _ => return err("Expected ',' or ']'."),
            }
        }
        self.depth -= 1;
        Ok(Node::Array(items))
    }

    /// Validates a string token; returns the raw content span (without quotes).
    fn string(&mut self) -> JResult<(usize, usize)> {
        self.i += 1; // opening quote
        let start = self.i;
        loop {
            let c = match self.s.get(self.i) {
                Some(&c) => c,
                None => return err("Expected end of string, but instead reached end of data."),
            };
            match c {
                b'"' => {
                    let end = self.i;
                    self.i += 1;
                    return Ok((start, end));
                }
                b'\\' => {
                    let e = match self.s.get(self.i + 1) {
                        Some(&e) => e,
                        None => return err("Expected end of string."),
                    };
                    match e {
                        b'"' | b'\\' | b'/' | b'b' | b'f' | b'n' | b'r' | b't' => self.i += 2,
                        b'u' => {
                            if self.s.len() < self.i + 6
                                || !self.s[self.i + 2..self.i + 6].iter().all(u8::is_ascii_hexdigit)
                            {
                                return err("Invalid unicode escape.");
                            }
                            self.i += 6;
                        }
                        _ => return err("Invalid escape sequence."),
                    }
                }
                0x00..=0x1F => return err("Invalid control character within a string."),
                _ => self.i += 1,
            }
        }
    }

    fn number(&mut self) -> JResult<()> {
        let s = self.s;
        if s.get(self.i) == Some(&b'-') {
            self.i += 1;
        }
        match s.get(self.i) {
            Some(b'0') => self.i += 1,
            Some(b'1'..=b'9') => {
                while matches!(s.get(self.i), Some(b'0'..=b'9')) {
                    self.i += 1;
                }
            }
            _ => return err("Invalid number."),
        }
        if s.get(self.i) == Some(&b'.') {
            self.i += 1;
            if !matches!(s.get(self.i), Some(b'0'..=b'9')) {
                return err("Invalid number.");
            }
            while matches!(s.get(self.i), Some(b'0'..=b'9')) {
                self.i += 1;
            }
        }
        if matches!(s.get(self.i), Some(b'e' | b'E')) {
            self.i += 1;
            if matches!(s.get(self.i), Some(b'+' | b'-')) {
                self.i += 1;
            }
            if !matches!(s.get(self.i), Some(b'0'..=b'9')) {
                return err("Invalid number.");
            }
            while matches!(s.get(self.i), Some(b'0'..=b'9')) {
                self.i += 1;
            }
        }
        // A number must be followed by a delimiter (e.g. "01" or "1x" are invalid).
        match s.get(self.i) {
            None | Some(b',' | b'}' | b']' | b' ' | b'\t' | b'\n' | b'\r') => Ok(()),
            _ => err("Invalid number."),
        }
    }
}

/// Decodes a raw JSON string body into UTF-16 code units (unpaired surrogates preserved).
fn unescape_utf16(raw: &str) -> Vec<u16> {
    let b = raw.as_bytes();
    let mut out: Vec<u16> = Vec::with_capacity(raw.len());
    let mut i = 0;
    let mut literal_start = 0;
    while i < b.len() {
        if b[i] == b'\\' {
            out.extend(raw[literal_start..i].encode_utf16());
            let e = b[i + 1];
            let unit = match e {
                b'"' => Some(0x22),
                b'\\' => Some(0x5C),
                b'/' => Some(0x2F),
                b'b' => Some(0x08),
                b'f' => Some(0x0C),
                b'n' => Some(0x0A),
                b'r' => Some(0x0D),
                b't' => Some(0x09),
                _ => None,
            };
            match unit {
                Some(u) => {
                    out.push(u);
                    i += 2;
                }
                None => {
                    // Validated by the parser: 'u' followed by four hex digits.
                    let hex = &raw[i + 2..i + 6];
                    out.push(u16::from_str_radix(hex, 16).unwrap_or(0xFFFD));
                    i += 6;
                }
            }
            literal_start = i;
        } else {
            i += 1;
        }
    }
    out.extend(raw[literal_start..].encode_utf16());
    out
}

/// `JsonElement`.
#[derive(Clone, Copy)]
pub struct JsonElement<'a> {
    doc: &'a JsonDocument,
    node: &'a NodeRef,
}

impl<'a> JsonElement<'a> {
    /// `ValueKind`.
    pub fn kind(&self) -> Kind {
        match &*self.node.node {
            Node::Object(_) => Kind::Object,
            Node::Array(_) => Kind::Array,
            Node::String(..) => Kind::String,
            Node::Number => Kind::Number,
            Node::True => Kind::True,
            Node::False => Kind::False,
            Node::Null => Kind::Null,
        }
    }

    /// `GetRawText()`.
    pub fn raw_text(&self) -> &'a str {
        &self.doc.src[self.node.start..self.node.end]
    }

    fn wrong_kind<T>(&self, expected: &str) -> JResult<T> {
        err(format!(
            "The requested operation requires an element of type '{expected}', but the target element has type '{:?}'.",
            self.kind()
        ))
    }

    /// The string as UTF-16, without validating surrogates (used for protocol data only).
    pub fn get_utf16(&self) -> JResult<Option<Vec<u16>>> {
        match &*self.node.node {
            Node::String(a, b) => Ok(Some(unescape_utf16(&self.doc.src[*a..*b]))),
            Node::Null => Ok(None),
            _ => self.wrong_kind("String"),
        }
    }

    /// `GetString()`: `null` for JSON null, throws for other non-string kinds and for strings
    /// containing an unpaired surrogate escape.
    pub fn get_string(&self) -> JResult<Option<String>> {
        match self.get_utf16()? {
            None => Ok(None),
            Some(units) => match String::from_utf16(&units) {
                Ok(s) => Ok(Some(s)),
                Err(_) => err("Cannot read incomplete UTF-16 JSON text as string with missing low surrogate."),
            },
        }
    }

    /// `TryGetDouble(out double)`: throws unless the element is a number. Out-of-range values
    /// become ±infinity, like .NET Core 3.0+.
    pub fn try_get_double(&self) -> JResult<Option<f64>> {
        match &*self.node.node {
            Node::Number => Ok(self.raw_text().parse::<f64>().ok()),
            _ => self.wrong_kind("Number"),
        }
    }

    /// `TryGetInt32(out int)`: throws unless the element is a number; false for fractions,
    /// exponents and out-of-range values.
    pub fn try_get_int32(&self) -> JResult<Option<i32>> {
        match &*self.node.node {
            Node::Number => {
                let t = self.raw_text();
                let digits = t.strip_prefix('-').unwrap_or(t);
                if digits.is_empty() || !digits.bytes().all(|c| c.is_ascii_digit()) {
                    return Ok(None);
                }
                Ok(t.parse::<i32>().ok())
            }
            _ => self.wrong_kind("Number"),
        }
    }

    /// `GetInt32()`: throws when `TryGetInt32` would return false.
    pub fn get_int32(&self) -> JResult<i32> {
        match self.try_get_int32()? {
            Some(v) => Ok(v),
            None => err("One of the identified items was in an invalid format."),
        }
    }

    /// `GetBoolean()`.
    pub fn get_boolean(&self) -> JResult<bool> {
        match &*self.node.node {
            Node::True => Ok(true),
            Node::False => Ok(false),
            _ => self.wrong_kind("Boolean"),
        }
    }

    /// `TryGetProperty(name, out value)`: throws unless the element is an object. With duplicate
    /// names the last one wins, like `JsonDocument`.
    pub fn try_get_property(&self, name: &str) -> JResult<Option<JsonElement<'a>>> {
        match &*self.node.node {
            Node::Object(props) => {
                for ((a, b), v) in props.iter().rev() {
                    let raw = &self.doc.src[*a..*b];
                    let matches = if raw.contains('\\') {
                        String::from_utf16(&unescape_utf16(raw)).is_ok_and(|k| k == name)
                    } else {
                        raw == name
                    };
                    if matches {
                        return Ok(Some(JsonElement { doc: self.doc, node: v }));
                    }
                }
                Ok(None)
            }
            _ => self.wrong_kind("Object"),
        }
    }

    /// `GetProperty(name)`: throws when missing.
    pub fn get_property(&self, name: &str) -> JResult<JsonElement<'a>> {
        match self.try_get_property(name)? {
            Some(v) => Ok(v),
            None => err("The given key was not present in the dictionary."),
        }
    }

    /// Object properties in document order as (unescaped name, value); used for
    /// System.Text.Json-style deserialization.
    pub fn properties(&self) -> JResult<Vec<(String, JsonElement<'a>)>> {
        match &*self.node.node {
            Node::Object(props) => Ok(props
                .iter()
                .map(|((a, b), v)| {
                    let raw = &self.doc.src[*a..*b];
                    let name = String::from_utf16_lossy(&unescape_utf16(raw));
                    (name, JsonElement { doc: self.doc, node: v })
                })
                .collect()),
            _ => self.wrong_kind("Object"),
        }
    }

    /// `EnumerateArray()`: throws unless the element is an array.
    pub fn array(&self) -> JResult<Vec<JsonElement<'a>>> {
        match &*self.node.node {
            Node::Array(items) => Ok(items.iter().map(|n| JsonElement { doc: self.doc, node: n }).collect()),
            _ => self.wrong_kind("Array"),
        }
    }

    /// `GetArrayLength()`.
    pub fn array_length(&self) -> JResult<usize> {
        match &*self.node.node {
            Node::Array(items) => Ok(items.len()),
            _ => self.wrong_kind("Array"),
        }
    }

    /// `this[int index]`.
    pub fn index(&self, i: usize) -> JResult<JsonElement<'a>> {
        match &*self.node.node {
            Node::Array(items) => match items.get(i) {
                Some(n) => Ok(JsonElement { doc: self.doc, node: n }),
                None => err("Index was outside the bounds of the array."),
            },
            _ => self.wrong_kind("Array"),
        }
    }

    /// `JsonElement.ToString()`: the string value, raw text for numbers / objects / arrays,
    /// `True`/`False`, and an empty string for null.
    pub fn to_dotnet_string(&self) -> JResult<String> {
        match &*self.node.node {
            Node::String(..) => Ok(self.get_string()?.unwrap_or_default()),
            Node::Null => Ok(String::new()),
            Node::True => Ok("True".into()),
            Node::False => Ok("False".into()),
            Node::Number | Node::Object(_) | Node::Array(_) => Ok(self.raw_text().to_owned()),
        }
    }
}

/// `prop.GetString() ?? fallback`, after `TryGetProperty`: the common Native idiom
/// `root.TryGetProperty("x", out var t) ? t.GetString() ?? "" : ""`.
pub fn string_or(elem: &JsonElement<'_>, name: &str, fallback: &str) -> JResult<String> {
    Ok(match elem.try_get_property(name)? {
        Some(v) => v.get_string()?.unwrap_or_else(|| fallback.to_owned()),
        None => fallback.to_owned(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_reads_like_system_text_json() {
        let d = JsonDocument::parse(r#"{"a":1,"a":2,"s":"xéy","n":null,"big":1e400,"y":2019.0,"t":true}"#).unwrap();
        let r = d.root();
        assert_eq!(r.get_property("a").unwrap().raw_text(), "2");
        assert_eq!(r.get_property("s").unwrap().get_string().unwrap().unwrap(), "xéy");
        assert_eq!(r.get_property("n").unwrap().get_string().unwrap(), None);
        assert_eq!(r.get_property("big").unwrap().try_get_double().unwrap(), Some(f64::INFINITY));
        assert_eq!(r.get_property("y").unwrap().to_dotnet_string().unwrap(), "2019.0");
        assert_eq!(r.get_property("t").unwrap().to_dotnet_string().unwrap(), "True");
        assert_eq!(r.get_property("n").unwrap().to_dotnet_string().unwrap(), "");
    }

    #[test]
    fn escapes_are_decoded_on_access() {
        let text = format!("{{\"s\":\"x{0}u00e9y{0}n{0}{0}\",\"k{0}u0065y\":1}}", '\\');
        let d = JsonDocument::parse(&text).unwrap();
        let r = d.root();
        assert_eq!(r.get_property("s").unwrap().get_string().unwrap().unwrap(), "x\u{e9}y\n\\");
        assert_eq!(r.get_property("key").unwrap().raw_text(), "1");
    }

    #[test]
    fn wrong_kinds_throw() {
        let d = JsonDocument::parse(r#"{"duration":null,"title":5,"arr":[1]}"#).unwrap();
        let r = d.root();
        assert!(r.get_property("duration").unwrap().try_get_double().is_err());
        assert!(r.get_property("title").unwrap().get_string().is_err());
        assert!(r.get_property("arr").unwrap().try_get_property("x").is_err());
        assert!(r.get_property("missing").is_err());
    }

    #[test]
    fn lone_surrogate_fails_only_on_access() {
        let d = JsonDocument::parse(r#"{"t":"x\ud83dy","ok":"fine"}"#).unwrap();
        let r = d.root();
        assert!(r.get_property("t").unwrap().get_string().is_err());
        assert_eq!(r.get_property("ok").unwrap().get_string().unwrap().unwrap(), "fine");
        assert_eq!(r.get_property("t").unwrap().get_utf16().unwrap().unwrap(), vec![0x78, 0xD83D, 0x79]);
    }

    #[test]
    fn strictness_matches_jsondocument_defaults() {
        assert!(JsonDocument::parse("").is_err());
        assert!(JsonDocument::parse("{\"a\":1,}").is_err());
        assert!(JsonDocument::parse("[1,]").is_err());
        assert!(JsonDocument::parse("{} {}").is_err());
        assert!(JsonDocument::parse("01").is_err());
        assert!(JsonDocument::parse("// c\n{}").is_err());
        assert!(JsonDocument::parse(" {\"a\" : [ 1 , 2.5e-3 , -0 ] } ").is_ok());
        let deep_ok = "[".repeat(64) + &"]".repeat(64);
        assert!(JsonDocument::parse(&deep_ok).is_ok());
        let deep_bad = "[".repeat(65) + &"]".repeat(65);
        assert!(JsonDocument::parse(&deep_bad).is_err());
    }

    #[test]
    fn int32_rules() {
        let d = JsonDocument::parse(r#"[1920, 1920.0, 1e3, -5, 3000000000]"#).unwrap();
        let a = d.root().array().unwrap();
        assert_eq!(a[0].get_int32().unwrap(), 1920);
        assert!(a[1].get_int32().is_err());
        assert!(a[2].get_int32().is_err());
        assert_eq!(a[3].get_int32().unwrap(), -5);
        assert!(a[4].get_int32().is_err());
    }
}
