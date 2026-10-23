//! Port of `CloudStorageService` (the upload half; `GetProviderStatus` stays in the UI).

pub mod copy;
pub mod detect;
pub mod providers;

use std::ffi::{OsStr, OsString};
use std::sync::Arc;

use tokio_util::sync::CancellationToken;

use crate::dotnet::fs::file_exists;
use crate::dotnet::path::get_file_name;
use crate::dotnet::text::to_lower_invariant;
use crate::ncat;
use crate::settings::AppSettings;

/// `Action<double, string>` progress callback.
pub type ProgressFn = Arc<dyn Fn(f64, String) + Send + Sync>;

/// `CloudUploadResult`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct CloudUploadResult {
    pub success: bool,
    pub error_message: Option<OsString>,
    pub destination_path: Option<OsString>,
    pub url: Option<String>,
    pub message: Option<OsString>,
}

impl CloudUploadResult {
    pub fn fail(msg: impl AsRef<OsStr>) -> Self {
        CloudUploadResult { success: false, error_message: Some(msg.as_ref().to_owned()), ..Default::default() }
    }
}

/// `GetProviderDisplayName(providerKey)`.
pub fn provider_display_name(key: Option<&str>) -> &'static str {
    match key.map(to_lower_invariant).as_deref() {
        Some("gdrive") => "Google Drive",
        Some("dropbox") => "Dropbox",
        Some("onedrive") => "Microsoft OneDrive",
        Some("webhook") => "Discord / Webhook",
        Some("custom") => "Custom Cloud Folder",
        _ => "Cloud Storage",
    }
}

/// `UploadAsync(localFilePath, providerKey, settings, progressCb, ct)`.
pub async fn upload(
    http: &reqwest::Client,
    local: &OsStr,
    provider_key: Option<&str>,
    settings: &AppSettings,
    progress: ProgressFn,
    ct: &CancellationToken,
) -> CloudUploadResult {
    if !file_exists(local) {
        return CloudUploadResult::fail(ncat!("Local file does not exist: ", local));
    }
    let file_name = get_file_name(local);
    let key = provider_key.map(to_lower_invariant).unwrap_or_else(|| "gdrive".into());
    let r = match key.as_str() {
        "gdrive" => providers::google_drive(local, &file_name, settings, &progress, ct).await,
        "dropbox" => providers::dropbox(http, local, &file_name, settings, &progress, ct).await,
        "onedrive" => providers::onedrive(local, &file_name, settings, &progress, ct).await,
        "webhook" => providers::webhook(http, local, &file_name, settings, &progress, ct).await,
        "custom" => providers::custom_folder(local, &file_name, settings, &progress, ct).await,
        other => return CloudUploadResult::fail(format!("Unsupported cloud provider: {other}")),
    };
    match r {
        Ok(res) => res,
        Err(e) if e.is_canceled() => CloudUploadResult::fail("Upload cancelled by user"),
        Err(e) => CloudUploadResult::fail(e.message),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn display_names() {
        assert_eq!(provider_display_name(Some("GDrive")), "Google Drive");
        assert_eq!(provider_display_name(Some("webhook")), "Discord / Webhook");
        assert_eq!(provider_display_name(None), "Cloud Storage");
        assert_eq!(provider_display_name(Some("s3")), "Cloud Storage");
    }

    /// CloudStorageService turns cancellation into a failed result, so the job ends with `error`.
    #[tokio::test]
    async fn cancelled_upload_reports_cancelled_by_user() {
        let d = std::env::temp_dir().join(format!("ferrite_cloud_cancel_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("dest")).unwrap();
        let f = d.join("v.mp4");
        std::fs::write(&f, vec![0u8; 4 * 1024 * 1024]).unwrap();
        let s = AppSettings { custom_cloud_folder_path: Some(d.join("dest").display().to_string()), ..Default::default() };
        let ct = CancellationToken::new();
        ct.cancel();
        let p: ProgressFn = Arc::new(|_, _| {});
        let r = upload(&reqwest::Client::new(), f.as_os_str(), Some("custom"), &s, p, &ct).await;
        assert!(!r.success);
        assert_eq!(r.error_message, Some(OsString::from("Upload cancelled by user")));
        assert!(!d.join("dest").join("v.mp4").exists(), "half-written copy must be removed");
        std::fs::remove_dir_all(&d).unwrap();
    }

    #[tokio::test]
    async fn missing_file_and_unknown_provider() {
        let http = reqwest::Client::new();
        let p: ProgressFn = Arc::new(|_, _| {});
        let ct = CancellationToken::new();
        let r = upload(&http, OsStr::new(r"C:\nope\x.mp4"), Some("gdrive"), &AppSettings::default(), p.clone(), &ct).await;
        assert_eq!(r.error_message, Some(OsString::from(r"Local file does not exist: C:\nope\x.mp4")));
        let f = std::env::temp_dir().join(format!("ferrite_cloud_{}.bin", std::process::id()));
        std::fs::write(&f, b"x").unwrap();
        let r = upload(&http, f.as_os_str(), Some("S3"), &AppSettings::default(), p, &ct).await;
        assert_eq!(r.error_message, Some(OsString::from("Unsupported cloud provider: s3")));
        std::fs::remove_file(&f).unwrap();
    }
}
