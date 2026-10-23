//! Port of `NativeEngineService.FetchPlaylistAsync` (yt-dlp flat playlist → Python helper →
//! single-video metadata), including System.Text.Json's deserialization rules for the
//! Python helper's output.

use std::ffi::{OsStr, OsString};

use tokio_util::sync::CancellationToken;

use crate::binaries::python_with_ytdlp;
use crate::dotnet::ioerr::NetError;
use crate::dotnet::json::{string_or, JResult, JsonDocument, JsonElement, JsonError, Kind};
use crate::dotnet::text::{eq_ignore_case, is_blank, is_blank_os, starts_with_ignore_case};
use crate::engine::Engine;
use crate::locate::get_backend_dir;
use crate::models::{PlaylistEntry, PlaylistInfo};
use crate::ncat;
use crate::proc::runner::{run_to_end, ProcSpec};
use crate::util::{clean_url, guid_prefix};

const USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

const PL_MARKER: &str = "__PL_JSON__";

/// The yt-dlp flat-playlist command line (§5.4 of PORTING.md).
pub fn playlist_args(extra: &str, ffmpeg_location: Option<&OsStr>, max_items: i32, clean_url: &OsStr) -> OsString {
    let mut a = OsString::new();
    if !extra.is_empty() {
        a.push(format!("{extra} "));
    }
    if let Some(dir) = ffmpeg_location {
        a.push(ncat!("--ffmpeg-location \"", dir, "\" "));
    }
    a.push(format!("--flat-playlist --playlist-end {max_items} -J --skip-download --no-warnings "));
    a.push(format!("--user-agent \"{USER_AGENT}\" "));
    a.push(ncat!("\"", clean_url, "\""));
    a
}

/// The Python helper script (marker + `json.dumps(fetch_playlist_entries(...))`).
pub fn python_playlist_script() -> String {
    format!(
        "import sys, json; sys.path.insert(0, sys.argv[1]); from downloader import fetch_playlist_entries; print('{PL_MARKER}' + json.dumps(fetch_playlist_entries(sys.argv[2], int(sys.argv[3]))))"
    )
}

pub async fn fetch_playlist(engine: &Engine, url: Option<&OsStr>, max_items: i32, ct: &CancellationToken) -> Result<Option<PlaylistInfo>, NetError> {
    let clean = clean_url(url);
    if is_blank_os(&clean) {
        return Ok(None);
    }
    engine.ensure_initialized(Some(ct)).await?;

    // 1. Native yt-dlp flat-playlist extraction
    let stage1 = async {
        let bins = engine.bins();
        let (exe, extra) = bins.runner();
        let args = playlist_args(&extra, bins.ffmpeg_location().as_deref(), max_items, &clean);
        let spec = ProcSpec::raw(exe, args, engine.configured_env());
        let stdout = run_to_end(&spec, engine.info(), ct, Some(120_000)).await?.stdout;
        Ok::<_, NetError>(parse_flat_playlist(&stdout).ok().flatten())
    };
    match stage1.await {
        Ok(Some(p)) => return Ok(Some(p)),
        Ok(None) => {}
        Err(e) if e.is_canceled() && ct.is_cancelled() => return Err(e),
        Err(_) => {}
    }

    // 2. Fallback: Python downloader script
    let info = engine.info().clone();
    let py = tokio::task::spawn_blocking(move || python_with_ytdlp(&info)).await.ok().flatten();
    if let Some(py) = py {
        let stage2 = async {
            let args = vec![
                OsString::from("-c"),
                OsString::from(python_playlist_script()),
                get_backend_dir(engine.info()),
                clean.clone(),
                OsString::from(max_items.to_string()),
            ];
            let spec = ProcSpec::list(py, args, engine.configured_env());
            let stdout = run_to_end(&spec, engine.info(), ct, Some(120_000)).await?.stdout;
            Ok::<_, NetError>(stdout.find(PL_MARKER).map(|idx| stj_deserialize_playlist(stdout[idx + PL_MARKER.len()..].trim())))
        };
        match stage2.await {
            Ok(Some(Ok(p))) => return Ok(p),
            Ok(_) => {}
            Err(e) if e.is_canceled() && ct.is_cancelled() => return Err(e),
            Err(_) => {}
        }
    }

    // 3. Fallback: single video metadata extraction
    match crate::metadata::fetch_metadata(engine, Some(&clean), ct).await {
        Ok(Some(meta)) if !is_blank(&meta.title) => Ok(Some(PlaylistInfo {
            title: Some(meta.title.clone()),
            count: 1,
            entries: Some(vec![Some(PlaylistEntry {
                id: Some(guid_prefix(8)),
                title: Some(meta.title),
                url: Some(clean.clone()),
                duration: meta.duration,
                uploader: Some(meta.uploader),
                thumbnail: Some(meta.thumbnail),
            })]),
        })),
        Err(e) if e.is_canceled() && ct.is_cancelled() => Err(e),
        _ => Ok(None),
    }
}

/// Stage 1 parsing. `Ok(None)` = nothing usable, `Err` = exception (both continue).
pub fn parse_flat_playlist(stdout: &str) -> JResult<Option<PlaylistInfo>> {
    if is_blank(stdout) {
        return Ok(None);
    }
    let (Some(first), Some(last)) = (stdout.find('{'), stdout.rfind('}')) else { return Ok(None) };
    if last <= first {
        return Ok(None);
    }
    let doc = JsonDocument::parse(&stdout[first..=last])?;
    let root = doc.root();
    let title = string_or(&root, "title", "Playlist")?;
    let mut entries: Vec<Option<PlaylistEntry>> = Vec::new();
    if let Some(arr) = root.try_get_property("entries")? {
        if arr.kind() == Kind::Array {
            for item in arr.array()? {
                let vid_id = string_or(&item, "id", "")?;
                let mut item_url = string_or(&item, "url", "")?;
                // Native has two branches with the same body: no url but an id, or a non-http url.
                if (item_url.is_empty() && !vid_id.is_empty()) || (!item_url.is_empty() && !starts_with_ignore_case(&item_url, "http")) {
                    item_url = format!("https://www.youtube.com/watch?v={vid_id}");
                }
                if item_url.is_empty() {
                    continue;
                }
                let item_title = string_or(&item, "title", "Untitled Video")?;
                let uploader = match item.try_get_property("uploader")? {
                    Some(u) => u.get_string()?.unwrap_or_default(),
                    None => string_or(&item, "channel", "")?,
                };
                let duration = match item.try_get_property("duration")? {
                    Some(d) => d.try_get_double()?.unwrap_or(0.0),
                    None => 0.0,
                };
                let mut thumb = String::new();
                if let Some(t) = item.try_get_property("thumbnail")? {
                    if t.kind() == Kind::String {
                        thumb = t.get_string()?.unwrap_or_default();
                    }
                }
                if thumb.is_empty() {
                    if let Some(arr) = item.try_get_property("thumbnails")? {
                        if arr.kind() == Kind::Array {
                            for t in arr.array()? {
                                if let Some(u) = t.try_get_property("url")? {
                                    if u.kind() == Kind::String {
                                        if let Some(s) = u.get_string()? {
                                            if !s.is_empty() {
                                                thumb = s;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if thumb.is_empty() && !vid_id.is_empty() {
                    thumb = format!("https://i.ytimg.com/vi/{vid_id}/hqdefault.jpg");
                }
                entries.push(Some(PlaylistEntry {
                    id: Some(vid_id),
                    title: Some(item_title),
                    url: Some(OsString::from(item_url)),
                    duration,
                    uploader: Some(uploader),
                    thumbnail: Some(thumb),
                }));
            }
        }
    }
    if entries.is_empty() {
        return Ok(None);
    }
    Ok(Some(PlaylistInfo { title: Some(title), count: entries.len() as i32, entries: Some(entries) }))
}

fn json_err<T>(msg: &str) -> JResult<T> {
    Err(JsonError(msg.into()))
}

/// System.Text.Json string property: string or null, anything else throws.
fn stj_string(v: &JsonElement<'_>) -> JResult<Option<String>> {
    match v.kind() {
        Kind::String | Kind::Null => v.get_string(),
        _ => json_err("The JSON value could not be converted to System.String."),
    }
}

/// `double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture)`.
pub fn parse_double_any_invariant(s: &str) -> Option<f64> {
    let is_ws = |c: char| matches!(c, '\u{9}'..='\u{D}' | ' ');
    let mut t = s.trim_matches(is_ws).to_owned();
    let mut negate = false;
    if t.starts_with('(') && t.ends_with(')') && t.len() >= 2 {
        negate = true;
        t = t[1..t.len() - 1].to_owned();
    }
    t = t.replace('\u{A4}', "");
    let lower = t.to_ascii_lowercase();
    let special = match lower.as_str() {
        "infinity" | "+infinity" => Some(f64::INFINITY),
        "-infinity" => Some(f64::NEG_INFINITY),
        "nan" | "+nan" | "-nan" => Some(f64::NAN),
        _ => None,
    };
    if let Some(v) = special {
        return Some(if negate { -v } else { v });
    }
    let (mant, exp) = match lower.find('e') {
        Some(i) => (&t[..i], &t[i..]),
        None => (t.as_str(), ""),
    };
    let (int_part, frac_part) = match mant.find('.') {
        Some(i) => (&mant[..i], &mant[i..]),
        None => (mant, ""),
    };
    if frac_part.contains(',') {
        return None;
    }
    let cleaned = format!("{}{}{}", int_part.replace(',', ""), frac_part, exp);
    if cleaned.is_empty() || !cleaned.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'.' | b'e' | b'E' | b'+' | b'-')) {
        return None;
    }
    let v = cleaned.parse::<f64>().ok()?;
    Some(if negate { -v } else { v })
}

/// `FlexibleDoubleConverter.Read`.
fn flexible_double(v: &JsonElement<'_>) -> JResult<f64> {
    match v.kind() {
        Kind::Null => Ok(0.0),
        Kind::Number => Ok(v.try_get_double()?.unwrap_or(0.0)),
        Kind::String => Ok(v.get_string()?.and_then(|s| parse_double_any_invariant(&s)).unwrap_or(0.0)),
        Kind::True | Kind::False => Ok(0.0),
        Kind::Object | Kind::Array => json_err("The converter read too much or not enough."),
    }
}

/// `int` with `JsonNumberHandling.AllowReadingFromString`.
fn stj_int(v: &JsonElement<'_>) -> JResult<i32> {
    match v.kind() {
        Kind::Number => v.get_int32(),
        Kind::String => {
            let s = v.get_string()?.unwrap_or_default();
            let digits = s.strip_prefix(['-', '+']).unwrap_or(&s);
            if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
                return json_err("The JSON value could not be converted to System.Int32.");
            }
            s.parse::<i32>().map_err(|_| JsonError("The JSON value could not be converted to System.Int32.".into()))
        }
        _ => json_err("The JSON value could not be converted to System.Int32."),
    }
}

fn stj_entry(v: &JsonElement<'_>) -> JResult<Option<PlaylistEntry>> {
    match v.kind() {
        Kind::Null => return Ok(None),
        Kind::Object => {}
        _ => return json_err("The JSON value could not be converted to PlaylistEntry."),
    }
    let mut e = PlaylistEntry { id: Some(String::new()), title: Some(String::new()), url: Some(OsString::new()), duration: 0.0, uploader: Some(String::new()), thumbnail: Some(String::new()) };
    for (name, val) in v.properties()? {
        if eq_ignore_case(&name, "id") {
            e.id = stj_string(&val)?;
        } else if eq_ignore_case(&name, "title") {
            e.title = stj_string(&val)?;
        } else if eq_ignore_case(&name, "url") {
            e.url = stj_string(&val)?.map(OsString::from);
        } else if eq_ignore_case(&name, "duration") {
            e.duration = flexible_double(&val)?;
        } else if eq_ignore_case(&name, "uploader") {
            e.uploader = stj_string(&val)?;
        } else if eq_ignore_case(&name, "thumbnail") {
            e.thumbnail = stj_string(&val)?;
        }
    }
    Ok(Some(e))
}

/// `JsonSerializer.Deserialize<PlaylistInfo>(json, _jso)` (case-insensitive names, numbers
/// from strings, `FlexibleDoubleConverter` for durations). `Ok(None)` for a JSON `null`.
pub fn stj_deserialize_playlist(json: &str) -> JResult<Option<PlaylistInfo>> {
    let doc = JsonDocument::parse(json)?;
    let root = doc.root();
    match root.kind() {
        Kind::Null => return Ok(None),
        Kind::Object => {}
        _ => return json_err("The JSON value could not be converted to PlaylistInfo."),
    }
    let mut info = PlaylistInfo { title: Some(String::new()), count: 0, entries: Some(Vec::new()) };
    for (name, val) in root.properties()? {
        if eq_ignore_case(&name, "title") {
            info.title = stj_string(&val)?;
        } else if eq_ignore_case(&name, "count") {
            info.count = stj_int(&val)?;
        } else if eq_ignore_case(&name, "entries") {
            info.entries = match val.kind() {
                Kind::Null => None,
                Kind::Array => {
                    let mut list = Vec::new();
                    for item in val.array()? {
                        list.push(stj_entry(&item)?);
                    }
                    Some(list)
                }
                _ => return json_err("The JSON value could not be converted to List<PlaylistEntry>."),
            };
        }
    }
    Ok(Some(info))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flat_playlist_mapping() {
        let out = r#"[youtube:tab] junk before {"title":"PL","entries":[
            {"id":"a1","url":"https://www.youtube.com/watch?v=a1","title":"One","duration":61.0,"channel":"Ch"},
            {"id":"b2","url":"b2","title":null,"uploader":"Up","thumbnails":[{"url":"t1"},{"url":"t2"},{"x":1}]},
            {"id":"","url":""},
            {"id":"c3","thumbnail":5}
        ]} trailing"#;
        let p = parse_flat_playlist(out).unwrap().unwrap();
        assert_eq!(p.title.as_deref(), Some("PL"));
        assert_eq!(p.count, 3);
        let e = p.entries.unwrap();
        let e0 = e[0].as_ref().unwrap();
        assert_eq!((e0.uploader.as_deref(), e0.duration), (Some("Ch"), 61.0));
        assert_eq!(e0.thumbnail.as_deref(), Some("https://i.ytimg.com/vi/a1/hqdefault.jpg"));
        let e1 = e[1].as_ref().unwrap();
        assert_eq!(e1.url.as_deref(), Some(OsStr::new("https://www.youtube.com/watch?v=b2")));
        assert_eq!(e1.title.as_deref(), Some("Untitled Video"));
        assert_eq!(e1.thumbnail.as_deref(), Some("t2"));
        assert_eq!(e[2].as_ref().unwrap().url.as_deref(), Some(OsStr::new("https://www.youtube.com/watch?v=c3")));
    }

    #[test]
    fn null_duration_fails_stage_one_like_native() {
        assert!(parse_flat_playlist(r#"{"entries":[{"id":"a","duration":null}]}"#).is_err());
        assert_eq!(parse_flat_playlist("no json here").unwrap(), None);
        assert_eq!(parse_flat_playlist(r#"{"title":"x","entries":[]}"#).unwrap(), None);
    }

    #[test]
    fn stj_deserialization_rules() {
        let p = stj_deserialize_playlist(r#"{"Title":"T","COUNT":"2","entries":[{"id":"x","Duration":"1,234.5"},null]}"#).unwrap().unwrap();
        assert_eq!(p.title.as_deref(), Some("T"));
        assert_eq!(p.count, 2);
        let e = p.entries.unwrap();
        assert_eq!(e[0].as_ref().unwrap().duration, 1234.5);
        assert!(e[1].is_none());
        assert_eq!(stj_deserialize_playlist("null").unwrap(), None);
        assert!(stj_deserialize_playlist(r#"{"count":null}"#).is_err());
        assert!(stj_deserialize_playlist(r#"{"count":1.5}"#).is_err());
        assert!(stj_deserialize_playlist(r#"{"title":5}"#).is_err());
        assert!(stj_deserialize_playlist(r#"{"entries":[{"duration":{}}]}"#).is_err());
        let q = stj_deserialize_playlist(r#"{"entries":[{"duration":true}]}"#).unwrap().unwrap();
        assert_eq!(q.entries.unwrap()[0].as_ref().unwrap().duration, 0.0);
    }

    #[test]
    fn playlist_args_match_native() {
        let a = playlist_args("", Some(OsStr::new(r"C:\f")), 150, OsStr::new("https://youtube.com/playlist?list=PL"));
        assert_eq!(
            a,
            OsString::from(format!(
                r#"--ffmpeg-location "C:\f" --flat-playlist --playlist-end 150 -J --skip-download --no-warnings --user-agent "{USER_AGENT}" "https://youtube.com/playlist?list=PL""#
            ))
        );
    }

    #[test]
    fn number_styles_any() {
        assert_eq!(parse_double_any_invariant(" 12.5 "), Some(12.5));
        assert_eq!(parse_double_any_invariant("(3)"), Some(-3.0));
        assert_eq!(parse_double_any_invariant("1e3"), Some(1000.0));
        assert_eq!(parse_double_any_invariant("Infinity"), Some(f64::INFINITY));
        assert_eq!(parse_double_any_invariant("inf"), None);
        assert_eq!(parse_double_any_invariant("abc"), None);
    }
}
