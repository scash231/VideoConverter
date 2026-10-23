//! Port of `FetchMetadataAsync`, `FetchYTMusicMetadataAsync`, `ExtractMetadataViaYtDlpAsync`
//! and `FetchFallbackMetadataAsync`.

use std::ffi::{OsStr, OsString};
use std::sync::LazyLock;

use regex::Regex;
use tokio_util::sync::CancellationToken;

use crate::cookies::resolve_cookies_file;
use crate::dotnet::http::{decode_body, escape_data_string};
use crate::dotnet::ioerr::NetError;
use crate::dotnet::json::{string_or, JResult, JsonDocument, Kind};
use crate::dotnet::text::{contains_wide, is_blank};
use crate::dotnet::netstr::wide;
use crate::engine::Engine;
use crate::models::MetadataResponse;
use crate::ncat;
use crate::proc::runner::{run_to_end, ProcSpec};
use crate::util::{clean_url, is_track_url, is_yt_music_url};

/// `#(\w+)` with .NET's definition of `\w`.
static HASHTAG: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"#([\p{L}\p{Mn}\p{Nd}\p{Pc}]+)").expect("valid regex"));

/// `" - Topic$"` (IgnoreCase); .NET's `$` also matches before a final `\n`, which the
/// following `Trim()` removes anyway.
static TOPIC: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i) - Topic\n?\z").expect("valid regex"));

pub async fn fetch_metadata(engine: &Engine, url: Option<&OsStr>, ct: &CancellationToken) -> Result<Option<MetadataResponse>, NetError> {
    let clean = clean_url(url);
    let meta = extract_metadata_via_ytdlp(engine, &clean, false, ct).await?;
    if let Some(m) = meta {
        if !is_blank(&m.title) {
            return Ok(Some(m));
        }
    }
    Ok(fetch_fallback_metadata(engine, &clean, ct).await)
}

pub async fn fetch_ytmusic_metadata(engine: &Engine, url: Option<&OsStr>, ct: &CancellationToken) -> Result<Option<MetadataResponse>, NetError> {
    let clean = clean_url(url);
    let meta = extract_metadata_via_ytdlp(engine, &clean, true, ct).await?;
    if let Some(m) = meta {
        if !is_blank(&m.title) {
            return Ok(Some(m));
        }
    }
    let mut fallback = fetch_fallback_metadata(engine, &clean, ct).await;
    if let Some(f) = fallback.as_mut() {
        f.is_music = true;
    }
    Ok(fallback)
}

/// The yt-dlp `-J` command line (§5.1 of PORTING.md).
pub fn metadata_args(extra: &str, ffmpeg_location: Option<&OsStr>, clean_url: &OsStr, cookies: Option<&str>) -> OsString {
    let mut a = OsString::new();
    if !extra.is_empty() {
        a.push(format!("{extra} "));
    }
    if let Some(dir) = ffmpeg_location {
        a.push(ncat!("--ffmpeg-location \"", dir, "\" "));
    }
    if is_track_url(clean_url) {
        a.push("--no-playlist ");
    }
    if let Some(c) = cookies {
        a.push(format!("--cookies \"{c}\" "));
    }
    a.push("-J --skip-download --no-warnings ");
    a.push(ncat!("\"", clean_url, "\""));
    a
}

async fn extract_metadata_via_ytdlp(
    engine: &Engine,
    clean_url: &OsStr,
    force_music: bool,
    ct: &CancellationToken,
) -> Result<Option<MetadataResponse>, NetError> {
    let attempt = async {
        engine.ensure_initialized(Some(ct)).await?;
        let bins = engine.bins();
        let (exe, extra) = bins.runner();
        let settings = engine.settings().await;
        let cookies = resolve_cookies_file(&settings);
        let args = metadata_args(&extra, bins.ffmpeg_location().as_deref(), clean_url, cookies.as_deref());
        let spec = ProcSpec::raw(exe, args, engine.configured_env());
        let stdout = run_to_end(&spec, engine.info(), ct, Some(90_000)).await?.stdout;
        if is_blank(&stdout) {
            return Ok::<_, NetError>(None);
        }
        Ok(parse_metadata(stdout.trim(), clean_url, force_music).ok().flatten())
    };
    match attempt.await {
        Ok(v) => Ok(v),
        Err(e) if e.is_canceled() && ct.is_cancelled() => Err(e),
        Err(_) => Ok(None),
    }
}

/// The JSON-to-`MetadataResponse` mapping. `Err` = an exception Native's catch-all swallows.
pub fn parse_metadata(json: &str, clean_url: &OsStr, force_music: bool) -> JResult<Option<MetadataResponse>> {
    let doc = JsonDocument::parse(json)?;
    let root = doc.root();
    let is_music = force_music || is_yt_music_url(clean_url);

    let title = string_or(&root, "title", "")?;
    let desc = string_or(&root, "description", "")?;
    let uploader = match root.try_get_property("uploader")? {
        Some(u) => u.get_string()?.unwrap_or_default(),
        None => string_or(&root, "channel", "")?,
    };
    let thumbnail = string_or(&root, "thumbnail", "")?;
    let duration = match root.try_get_property("duration")? {
        Some(d) => d.try_get_double()?.unwrap_or(0.0),
        None => 0.0,
    };
    let mut artist = match root.try_get_property("artist")? {
        Some(a) => a.get_string()?,
        None => is_music.then(|| uploader.clone()),
    };
    let album = match root.try_get_property("album")? {
        Some(a) => a.get_string()?,
        None => None,
    };
    let release_year = match root.try_get_property("release_year")? {
        Some(r) => Some(r.to_dotnet_string()?),
        None => None,
    };
    if let Some(a) = artist.as_mut() {
        *a = TOPIC.replace_all(a, "").trim().to_owned();
    }

    let mut resp = MetadataResponse {
        title,
        description: desc.clone(),
        uploader: artist.clone().unwrap_or_else(|| uploader.clone()),
        duration,
        thumbnail,
        artist,
        album,
        release_year,
        is_music,
        ..Default::default()
    };

    match root.try_get_property("tags")? {
        Some(t) if t.kind() == Kind::Array => {
            for tag in t.array()? {
                if let Some(s) = tag.get_string()? {
                    if !is_blank(&s) {
                        resp.tags.push(s);
                    }
                }
            }
        }
        _ => {
            if !is_blank(&desc) {
                for c in HASHTAG.captures_iter(&desc) {
                    if let Some(m) = c.get(1) {
                        resp.tags.push(m.as_str().to_owned());
                    }
                }
            }
        }
    }

    Ok(if !is_blank(&resp.title) { Some(resp) } else { None })
}

/// `FetchFallbackMetadataAsync`: YouTube oEmbed; every failure (including cancellation)
/// returns `None`.
async fn fetch_fallback_metadata(engine: &Engine, url: &OsStr, ct: &CancellationToken) -> Option<MetadataResponse> {
    let w = wide(url);
    if !(contains_wide(&w, "youtube.com") || contains_wide(&w, "youtu.be")) {
        return None;
    }
    let url_str = url.to_str()?;
    let oembed = format!("https://www.youtube.com/oembed?url={}&format=json", escape_data_string(url_str));
    let request = async {
        let resp = engine.0.oembed_http.get(&oembed).send().await.ok()?;
        if !resp.status().is_success() {
            return None;
        }
        let bytes = resp.bytes().await.ok()?;
        Some(decode_body(&bytes))
    };
    let json = tokio::select! {
        r = request => r?,
        _ = ct.cancelled() => return None,
    };
    parse_oembed(&json).ok()
}

pub fn parse_oembed(json: &str) -> JResult<MetadataResponse> {
    let doc = JsonDocument::parse(json)?;
    let root = doc.root();
    Ok(MetadataResponse {
        title: string_or(&root, "title", "")?,
        uploader: string_or(&root, "author_name", "")?,
        thumbnail: string_or(&root, "thumbnail_url", "")?,
        description: string_or(&root, "title", "")?,
        ..Default::default()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_ytdlp_json_like_native() {
        let json = r#"{"title":"Song","description":"nice #tag1 and #tag_2!","uploader":"Artist - Topic","duration":187.5,"thumbnail":"https://i/x.jpg","release_year":2019,"album":"LP"}"#;
        let m = parse_metadata(json, OsStr::new("https://music.youtube.com/watch?v=1"), false).unwrap().unwrap();
        assert_eq!(m.title, "Song");
        assert!(m.is_music);
        assert_eq!(m.artist.as_deref(), Some("Artist"));
        assert_eq!(m.uploader, "Artist");
        assert_eq!(m.duration, 187.5);
        assert_eq!(m.release_year.as_deref(), Some("2019"));
        assert_eq!(m.tags, vec!["tag1", "tag_2"]);
        assert_eq!(m.album.as_deref(), Some("LP"));
    }

    #[test]
    fn json_kind_errors_fall_back_like_native() {
        // `duration: null` makes TryGetDouble throw → whole result is discarded.
        assert!(parse_metadata(r#"{"title":"x","duration":null}"#, OsStr::new("u"), false).is_err());
        // `tags: null` → hashtags from the description.
        let m = parse_metadata(r##"{"title":"x","tags":null,"description":"#a #b"}"##, OsStr::new("u"), false).unwrap().unwrap();
        assert_eq!(m.tags, vec!["a", "b"]);
        // Non-string tag → exception.
        assert!(parse_metadata(r#"{"title":"x","tags":[1]}"#, OsStr::new("u"), false).is_err());
        // Blank title → no metadata.
        assert_eq!(parse_metadata(r#"{"title":"  "}"#, OsStr::new("u"), false).unwrap(), None);
        // `artist: null` stays null even for music.
        let m = parse_metadata(r#"{"title":"x","artist":null,"channel":"C"}"#, OsStr::new("https://music.youtube.com/x"), false).unwrap().unwrap();
        assert_eq!(m.artist, None);
        assert_eq!(m.uploader, "C");
    }

    #[test]
    fn args_match_native() {
        let a = metadata_args("-m yt_dlp", Some(OsStr::new(r"C:\ff\bin")), OsStr::new("https://www.youtube.com/watch?v=abc"), Some(r"C:\c.txt"));
        assert_eq!(
            a,
            OsString::from(r#"-m yt_dlp --ffmpeg-location "C:\ff\bin" --no-playlist --cookies "C:\c.txt" -J --skip-download --no-warnings "https://www.youtube.com/watch?v=abc""#)
        );
        let b = metadata_args("", None, OsStr::new("https://www.tiktok.com/@u/video/1"), None);
        assert_eq!(b, OsString::from(r#"-J --skip-download --no-warnings "https://www.tiktok.com/@u/video/1""#));
    }

    #[test]
    fn oembed_mapping() {
        let m = parse_oembed(r#"{"title":"T","author_name":"A","thumbnail_url":"https://t"}"#).unwrap();
        assert_eq!((m.title.as_str(), m.uploader.as_str(), m.description.as_str()), ("T", "A", "T"));
    }
}
