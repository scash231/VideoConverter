//! Port of `Models/Models.cs` (engine DTOs) plus their wire form.
//!
//! JSON names match the C# `JsonPropertyName` attributes. Strings that may contain unpaired
//! surrogates (file names, messages) travel as `{"$utf16":[...]}` because System.Text.Json
//! cannot read or write such strings; [`os_to_json`] / [`json_to_os`] handle both forms.

use std::ffi::{OsStr, OsString};
use std::os::windows::ffi::OsStrExt;

use serde_json::{json, Map, Value};

use crate::dotnet::netstr::from_wide;

/// Wire form of a .NET string.
pub fn os_to_json(s: &OsStr) -> Value {
    match s.to_str() {
        Some(v) => Value::String(v.to_owned()),
        None => json!({ "$utf16": s.encode_wide().collect::<Vec<u16>>() }),
    }
}

pub fn opt_os_to_json(s: Option<&OsStr>) -> Value {
    s.map_or(Value::Null, os_to_json)
}

/// Reads a .NET string from its wire form (`null` → `None`).
pub fn json_to_os(v: &Value) -> Option<OsString> {
    match v {
        Value::String(s) => Some(OsString::from(s)),
        Value::Object(m) => m.get("$utf16").and_then(Value::as_array).map(|units| {
            let w: Vec<u16> = units.iter().filter_map(|u| u.as_u64()).map(|u| u as u16).collect();
            from_wide(&w)
        }),
        _ => None,
    }
}

/// `MetadataResponse`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct MetadataResponse {
    pub title: String,
    pub description: String,
    pub tags: Vec<String>,
    pub thumbnail: String,
    pub duration: f64,
    pub uploader: String,
    pub artist: Option<String>,
    pub album: Option<String>,
    pub is_music: bool,
    pub release_year: Option<String>,
    pub track_count: Option<i32>,
}

/// JSON number for a double; System.Text.Json cannot read NaN/Infinity tokens, so those are
/// sent as the named literals it accepts with `AllowNamedFloatingPointLiterals`.
pub fn f64_to_json(v: f64) -> Value {
    if v.is_finite() {
        json!(v)
    } else if v.is_nan() {
        json!("NaN")
    } else if v > 0.0 {
        json!("Infinity")
    } else {
        json!("-Infinity")
    }
}

impl MetadataResponse {
    pub fn to_json(&self) -> Value {
        json!({
            "title": self.title,
            "description": self.description,
            "tags": self.tags,
            "thumbnail": self.thumbnail,
            "duration": f64_to_json(self.duration),
            "uploader": self.uploader,
            "artist": self.artist,
            "album": self.album,
            "is_music": self.is_music,
            "release_year": self.release_year,
            "track_count": self.track_count,
        })
    }
}

/// A value in `JobStatus.Result` (Native stores `string` and `bool`).
#[derive(Debug, Clone, PartialEq)]
pub enum ResultValue {
    Str(OsString),
    Bool(bool),
}

/// `JobStatus`.
#[derive(Debug, Clone, PartialEq)]
pub struct JobStatus {
    pub id: String,
    pub status: String,
    pub progress: f64,
    pub message: OsString,
    pub error: Option<OsString>,
    pub eta: Option<i32>,
    pub speed: Option<String>,
    /// Insertion-ordered like a .NET `Dictionary` that never had removals.
    pub result: Option<Vec<(String, ResultValue)>>,
}

impl JobStatus {
    pub fn to_json(&self) -> Value {
        let result = self.result.as_ref().map(|r| {
            let mut m = Map::new();
            for (k, v) in r {
                let jv = match v {
                    ResultValue::Str(s) => os_to_json(s),
                    ResultValue::Bool(b) => Value::Bool(*b),
                };
                m.insert(k.clone(), jv);
            }
            Value::Object(m)
        });
        json!({
            "id": self.id,
            "status": self.status,
            "progress": f64_to_json(self.progress),
            "message": os_to_json(&self.message),
            "error": opt_os_to_json(self.error.as_deref()),
            "eta": self.eta,
            "speed": self.speed,
            "result": result,
        })
    }
}

/// `PlaylistEntry`. Strings are nullable because System.Text.Json assigns `null` when the
/// Python fallback returns it.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PlaylistEntry {
    pub id: Option<String>,
    pub title: Option<String>,
    pub url: Option<OsString>,
    pub duration: f64,
    pub uploader: Option<String>,
    pub thumbnail: Option<String>,
}

/// `PlaylistInfo`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PlaylistInfo {
    pub title: Option<String>,
    pub count: i32,
    /// Elements can be `null` when the Python fallback returns them.
    pub entries: Option<Vec<Option<PlaylistEntry>>>,
}

impl PlaylistInfo {
    pub fn to_json(&self) -> Value {
        let entries = self.entries.as_ref().map(|e| {
            e.iter()
                .map(|x| match x {
                    None => Value::Null,
                    Some(x) => json!({
                        "id": x.id,
                        "title": x.title,
                        "url": opt_os_to_json(x.url.as_deref()),
                        "duration": f64_to_json(x.duration),
                        "uploader": x.uploader,
                        "thumbnail": x.thumbnail,
                    }),
                })
                .collect::<Vec<_>>()
        });
        json!({ "title": self.title, "count": self.count, "entries": entries })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn os_strings_round_trip_with_lone_surrogates() {
        let w = [0x61u16, 0xD83D, 0x62];
        let s = from_wide(&w);
        let v = os_to_json(&s);
        assert!(v.is_object());
        assert_eq!(json_to_os(&v).unwrap(), s);
        assert_eq!(os_to_json(OsStr::new("plain")), json!("plain"));
        assert_eq!(json_to_os(&json!("plain")).unwrap(), OsString::from("plain"));
        assert_eq!(json_to_os(&Value::Null), None);
    }

    #[test]
    fn job_status_json_keeps_result_order() {
        let s = JobStatus {
            id: "abcd1234".into(),
            status: "completed".into(),
            progress: 100.0,
            message: "done".into(),
            error: None,
            eta: None,
            speed: None,
            result: Some(vec![
                ("file_path".into(), ResultValue::Str(r"C:\a.mp4".into())),
                ("is_temp".into(), ResultValue::Bool(true)),
            ]),
        };
        let v = s.to_json();
        assert_eq!(v["result"]["is_temp"], json!(true));
        assert_eq!(v["progress"], json!(100.0));
    }
}
