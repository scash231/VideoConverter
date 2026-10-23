//! YouTube upload for Ferrite.
//!
//! Native has no upload (it rejects upload jobs). Ferrite supports it, compatible with the
//! Legacy engine's files: `client_secret*.json` and `token.json` in the backend folder, the
//! token stored in google-auth's "authorized user" JSON format, and the same request body
//! (title cut to 100 characters, tag rules, category 22, not made for kids).

pub mod oauth;
pub mod time;
pub mod upload;

use std::ffi::OsString;

use crate::dotnet::fs::directory_exists;
use crate::dotnet::path::combine;
use crate::dotnet::win::{get_folder_path, KnownFolder};
use crate::host::HostInfo;
use crate::locate::get_backend_dir;

/// Where `client_secret*.json` and `token.json` live: the backend folder shared with
/// Legacy when it exists, otherwise `%APPDATA%\VideoConverter` (installed builds have no
/// backend folder).
pub fn credentials_dir(info: &HostInfo) -> OsString {
    let backend = get_backend_dir(info);
    if directory_exists(&backend) {
        backend
    } else {
        combine(get_folder_path(KnownFolder::ApplicationData), "VideoConverter")
    }
}

/// `_sanitize_tags`: strip whitespace and leading `#`, drop empty tags and tags longer than
/// 30 characters, stop once the total would exceed 500 characters.
pub fn sanitize_tags(tags: &[String]) -> Vec<String> {
    let mut out = Vec::new();
    let mut total = 0usize;
    for tag in tags {
        let t = tag.trim().trim_start_matches('#');
        let len = t.chars().count();
        if t.is_empty() || len > 30 {
            continue;
        }
        if total + len > 500 {
            break;
        }
        out.push(t.to_owned());
        total += len;
    }
    out
}

/// The description with `#Shorts` prepended when it is missing (Settings → "Auto add
/// #Shorts").
pub fn shorts_description(description: &str, auto_add: bool) -> String {
    if auto_add && !description.to_lowercase().contains("#shorts") {
        format!("#Shorts\n\n{description}")
    } else {
        description.to_owned()
    }
}

/// The `videos.insert` resource body.
pub fn video_resource(title: &str, description: &str, tags: &[String], privacy: &str) -> serde_json::Value {
    let title: String = title.chars().take(100).collect();
    serde_json::json!({
        "snippet": {
            "title": title,
            "description": description,
            "tags": sanitize_tags(tags),
            "categoryId": "22",
        },
        "status": { "privacyStatus": privacy, "selfDeclaredMadeForKids": false },
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tag_rules() {
        let tags: Vec<String> = vec![" #fun ".into(), "".into(), "x".repeat(31), "##two".into()];
        assert_eq!(sanitize_tags(&tags), vec!["fun", "two"]);
        let many: Vec<String> = (0..30).map(|i| format!("{i:0>20}")).collect();
        assert_eq!(sanitize_tags(&many).len(), 25);
    }

    #[test]
    fn shorts_and_body() {
        assert_eq!(shorts_description("hello", true), "#Shorts\n\nhello");
        assert_eq!(shorts_description("x #SHORTS", true), "x #SHORTS");
        assert_eq!(shorts_description("hello", false), "hello");
        let v = video_resource(&"t".repeat(120), "d", &["a".into()], "unlisted");
        assert_eq!(v["snippet"]["title"].as_str().unwrap().len(), 100);
        assert_eq!(v["snippet"]["categoryId"], "22");
        assert_eq!(v["status"]["privacyStatus"], "unlisted");
        assert_eq!(v["status"]["selfDeclaredMadeForKids"], false);
    }
}
