//! Golden tests: every fixture in `parity/fixtures/golden.json` was produced by the real C# code
//! (`dotnet run --project backend-ferrite/parity -- golden`). Ferrite must give the same answers.

use std::ffi::{OsStr, OsString};

use serde_json::Value;

use ferrite_engine::cloud::providers::webhook_multipart_frame;
use ferrite_engine::dotnet::fmt::{fixed, Culture};
use ferrite_engine::dotnet::fs::{create_directory, delete_file};
use ferrite_engine::dotnet::http::{escape_data_string, multipart_content_type, status_name, stj_string};
use ferrite_engine::dotnet::netstr::{from_wide, wide};
use ferrite_engine::dotnet::path::get_full_path;
use ferrite_engine::download::get_format_string;
use ferrite_engine::metadata::parse_metadata;
use ferrite_engine::playlist::parse_flat_playlist;
use ferrite_engine::proc::progress::{parse_eta, parse_yt_dlp_progress, phase_key};
use ferrite_engine::util::{clean_url, is_yt_music_url, sanitize_filename};

fn golden() -> Value {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../parity/fixtures/golden.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).expect("valid golden.json")
}

fn utf16(v: &Value) -> OsString {
    let units: Vec<u16> = v.as_array().expect("utf16 array").iter().map(|u| u.as_u64().unwrap() as u16).collect();
    from_wide(&units)
}

fn culture(g: &Value) -> Culture {
    let c = &g["culture"];
    let s = |k: &str| c[k].as_str().unwrap().to_owned();
    Culture {
        name: s("name"),
        decimal_separator: s("decimal_separator"),
        negative_sign: s("negative_sign"),
        positive_infinity: s("positive_infinity"),
        negative_infinity: s("negative_infinity"),
        nan: s("nan"),
    }
}

fn f64_of(v: &Value) -> f64 {
    match v {
        Value::Number(n) => n.as_f64().unwrap(),
        Value::String(s) => s.parse::<f64>().unwrap(),
        other => panic!("not a number: {other}"),
    }
}

#[test]
fn format_strings() {
    let g = golden();
    for f in g["format_strings"].as_array().unwrap() {
        let q = f["quality"].as_str();
        let got = get_format_string(q, f["is_audio"].as_bool().unwrap(), f["mode"].as_str().unwrap());
        assert_eq!(got, f["out"].as_str().unwrap(), "{f}");
    }
}

#[test]
fn sanitize_filenames() {
    let g = golden();
    for f in g["sanitize"].as_array().unwrap() {
        let got = sanitize_filename(&utf16(&f["in"]));
        assert_eq!(wide(&got), wide(&utf16(&f["out"])), "{f}");
    }
}

#[test]
fn eta() {
    let g = golden();
    for f in g["eta"].as_array().unwrap() {
        let expected = f["out"].as_i64().map(|v| v as i32);
        assert_eq!(parse_eta(f["in"].as_str()), expected, "{f}");
    }
}

#[test]
fn urls() {
    let g = golden();
    for f in g["urls"].as_array().unwrap() {
        let raw = utf16(&f["in"]);
        let clean = clean_url(Some(&raw));
        assert_eq!(wide(&clean), wide(&utf16(&f["clean"])), "{f}");
        assert_eq!(is_yt_music_url(&clean), f["is_music"].as_bool().unwrap(), "{f}");
    }
}

#[test]
fn progress_lines_and_messages() {
    let g = golden();
    let c = culture(&g);
    for f in g["progress"].as_array().unwrap() {
        let p = parse_yt_dlp_progress(f["line"].as_str().unwrap());
        if !f["match"].as_bool().unwrap() {
            assert!(p.is_none(), "{f}");
            continue;
        }
        let p = p.unwrap_or_else(|| panic!("no match: {f}"));
        assert_eq!(p.percent, f["pct"].as_f64().unwrap(), "{f}");
        assert_eq!(p.speed.as_deref(), f["speed"].as_str(), "{f}");
        assert_eq!(p.eta, f["eta"].as_i64().map(|v| v as i32), "{f}");
        assert_eq!(format!("Download Video: {}%", fixed(p.percent, 1, &c)), f["message"].as_str().unwrap(), "{f}");
    }
}

#[test]
fn phase_keys() {
    let g = golden();
    for f in g["phase_keys"].as_array().unwrap() {
        assert_eq!(phase_key(f["status"].as_str().unwrap(), f["message"].as_str().unwrap()), f["key"].as_str().unwrap(), "{f}");
    }
}

#[test]
fn fixed_formatting() {
    let g = golden();
    let c = culture(&g);
    for f in g["fixed"].as_array().unwrap() {
        let v: f64 = f["value"].as_str().unwrap().parse().unwrap();
        let d = f["decimals"].as_u64().unwrap() as usize;
        assert_eq!(fixed(v, d, &c), f["out"].as_str().unwrap(), "{f}");
    }
}

#[test]
fn json_escaping_and_uri_escaping() {
    let g = golden();
    for f in g["stj_strings"].as_array().unwrap() {
        assert_eq!(stj_string(&utf16(&f["in"])), f["out"].as_str().unwrap(), "{f}");
    }
    for f in g["escape_data_string"].as_array().unwrap() {
        assert_eq!(escape_data_string(f["in"].as_str().unwrap()), f["out"].as_str().unwrap(), "{f}");
    }
}

#[test]
fn webhook_multipart_bodies() {
    let g = golden();
    for f in g["multipart"].as_array().unwrap() {
        let (prefix, suffix) = webhook_multipart_frame("b0", f["discord"].as_bool().unwrap(), &utf16(&f["file_name"]));
        assert_eq!(format!("{prefix}DATA{suffix}"), f["body"].as_str().unwrap(), "{f}");
        assert_eq!(multipart_content_type("b0"), f["content_type"].as_str().unwrap());
    }
}

#[test]
fn http_status_names() {
    let g = golden();
    for (code, name) in g["status_names"].as_object().unwrap() {
        assert_eq!(status_name(code.parse().unwrap()), name.as_str().unwrap(), "{code}");
    }
}

#[test]
fn metadata_mapping() {
    let g = golden();
    for f in g["metadata"].as_array().unwrap() {
        let url = OsString::from(f["url"].as_str().unwrap());
        let got = parse_metadata(f["json"].as_str().unwrap().trim(), &url, f["force_music"].as_bool().unwrap()).ok().flatten();
        let out = &f["out"];
        if out.is_null() {
            assert!(got.is_none(), "expected no metadata for {}", f["json"]);
            continue;
        }
        let m = got.unwrap_or_else(|| panic!("expected metadata for {}", f["json"]));
        assert_eq!(m.title, out["title"].as_str().unwrap());
        assert_eq!(m.description, out["description"].as_str().unwrap());
        assert_eq!(m.uploader, out["uploader"].as_str().unwrap());
        assert_eq!(m.thumbnail, out["thumbnail"].as_str().unwrap());
        assert_eq!(m.duration, f64_of(&out["duration"]));
        assert_eq!(m.artist.as_deref(), out["artist"].as_str());
        assert_eq!(m.album.as_deref(), out["album"].as_str());
        assert_eq!(m.release_year.as_deref(), out["release_year"].as_str());
        assert_eq!(m.is_music, out["is_music"].as_bool().unwrap());
        let tags: Vec<&str> = out["tags"].as_array().unwrap().iter().map(|t| t.as_str().unwrap()).collect();
        assert_eq!(m.tags, tags);
    }
}

#[test]
fn flat_playlist_mapping() {
    let g = golden();
    for f in g["playlist"].as_array().unwrap() {
        let got = parse_flat_playlist(f["json"].as_str().unwrap()).ok().flatten();
        let out = &f["out"];
        let p = got.unwrap_or_else(|| panic!("expected playlist for {}", f["json"]));
        assert_eq!(p.title.as_deref(), out["title"].as_str());
        assert_eq!(p.count as i64, out["count"].as_i64().unwrap());
        let entries = p.entries.unwrap();
        let expected = out["entries"].as_array().unwrap();
        assert_eq!(entries.len(), expected.len());
        for (e, x) in entries.iter().zip(expected) {
            let e = e.as_ref().unwrap();
            assert_eq!(e.id.as_deref(), x["id"].as_str());
            assert_eq!(e.title.as_deref(), x["title"].as_str());
            assert_eq!(e.url.as_deref(), x["url"].as_str().map(OsStr::new));
            assert_eq!(e.duration, f64_of(&x["duration"]));
            assert_eq!(e.uploader.as_deref(), x["uploader"].as_str());
            assert_eq!(e.thumbnail.as_deref(), x["thumbnail"].as_str());
        }
    }
}

#[test]
fn io_error_texts() {
    let g = golden();
    for f in g["io_errors"].as_array().unwrap() {
        let op = f["op"].as_str().unwrap();
        let expected = f["message"].as_str().map(OsString::from);
        let got = if let Some(p) = op.strip_prefix("create_directory ") {
            create_directory(p).err().map(|e| e.message)
        } else if op == "get_full_path empty" {
            get_full_path("").err().map(|e| e.message)
        } else if let Some(p) = op.strip_prefix("delete_file ") {
            delete_file(p).err().map(|e| e.message)
        } else {
            panic!("unknown op {op}")
        };
        assert_eq!(got, expected, "{op}");
    }
}
