//! The parts of `Models/AppSettings.cs` the engine reads. Native reads
//! `SettingsService.Current` at the moment it needs a value, so Ferrite asks the host for a
//! fresh snapshot at the same moments ([`crate::host::Host::get_settings`]).
//!
//! Every field is optional because System.Text.Json assigns `null` when settings.json says so;
//! defaults are the C# property initializers.

use serde_json::Value;

#[derive(Debug, Clone, PartialEq)]
pub struct AppSettings {
    pub cookies_file_path: Option<String>,
    pub cloud_provider: Option<String>,
    pub gdrive_folder_path: Option<String>,
    pub dropbox_folder_path: Option<String>,
    pub dropbox_api_token: Option<String>,
    pub onedrive_folder_path: Option<String>,
    pub webhook_url: Option<String>,
    pub custom_cloud_folder_path: Option<String>,
    pub auto_add_shorts_hashtag: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        AppSettings {
            cookies_file_path: Some(String::new()),
            cloud_provider: Some("gdrive".into()),
            gdrive_folder_path: Some(String::new()),
            dropbox_folder_path: Some(String::new()),
            dropbox_api_token: Some(String::new()),
            onedrive_folder_path: Some(String::new()),
            webhook_url: Some(String::new()),
            custom_cloud_folder_path: Some(String::new()),
            auto_add_shorts_hashtag: true,
        }
    }
}

fn str_field(v: &Value, name: &str, default: Option<String>) -> Option<String> {
    match v.get(name) {
        None => default,
        Some(Value::Null) => None,
        Some(Value::String(s)) => Some(s.clone()),
        Some(_) => default,
    }
}

impl AppSettings {
    /// Reads the JSON the host produces with `JsonSerializer.Serialize(SettingsService.Current)`
    /// (same property names as settings.json; missing keys keep their defaults).
    pub fn from_json(v: &Value) -> AppSettings {
        let d = AppSettings::default();
        AppSettings {
            cookies_file_path: str_field(v, "cookies_file_path", d.cookies_file_path),
            cloud_provider: str_field(v, "cloud_provider", d.cloud_provider),
            gdrive_folder_path: str_field(v, "gdrive_folder_path", d.gdrive_folder_path),
            dropbox_folder_path: str_field(v, "dropbox_folder_path", d.dropbox_folder_path),
            dropbox_api_token: str_field(v, "dropbox_api_token", d.dropbox_api_token),
            onedrive_folder_path: str_field(v, "onedrive_folder_path", d.onedrive_folder_path),
            webhook_url: str_field(v, "webhook_url", d.webhook_url),
            custom_cloud_folder_path: str_field(v, "custom_cloud_folder_path", d.custom_cloud_folder_path),
            auto_add_shorts_hashtag: v
                .get("auto_add_shorts_hashtag")
                .and_then(Value::as_bool)
                .unwrap_or(d.auto_add_shorts_hashtag),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn settings_json_round_trip_and_defaults() {
        let s = AppSettings::from_json(&json!({
            "cookies_file_path": "C:\\c.txt",
            "cloud_provider": null,
            "webhook_url": "https://x",
            "auto_add_shorts_hashtag": false,
            "unrelated_key": 5
        }));
        assert_eq!(s.cookies_file_path.as_deref(), Some("C:\\c.txt"));
        assert_eq!(s.cloud_provider, None);
        assert_eq!(s.gdrive_folder_path.as_deref(), Some(""));
        assert!(!s.auto_add_shorts_hashtag);
        let d = AppSettings::from_json(&json!({}));
        assert_eq!(d, AppSettings::default());
    }
}
