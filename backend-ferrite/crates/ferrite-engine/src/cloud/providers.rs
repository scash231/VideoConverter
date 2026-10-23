//! The five providers of `CloudStorageService.UploadAsync`.

use std::ffi::{OsStr, OsString};
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use reqwest::header::{HeaderValue, AUTHORIZATION, CONTENT_LENGTH, CONTENT_TYPE};
use tokio_util::sync::CancellationToken;

use super::copy::{copy_file_with_progress, file_length, open_read, progress_body_stream, unique_target_file_path};
use super::detect::{detect_dropbox_folder, detect_google_drive_folder, detect_onedrive_folder};
use super::{CloudUploadResult, ProgressFn};
use crate::dotnet::fmt::fixed;
use crate::dotnet::fmt::Culture;
use crate::dotnet::fs::{create_directory, directory_exists};
use crate::dotnet::http::{
    decode_body, multipart_content_type, new_boundary, parse_request_uri, status_name, stj_string, transport_error, PartHeaders,
};
use crate::dotnet::ioerr::NetError;
use crate::dotnet::json::JsonDocument;
use crate::dotnet::path::{combine, get_file_name};
use crate::dotnet::text::{contains_ignore_case, is_null_or_whitespace};
use crate::ncat;
use crate::settings::AppSettings;

/// `HttpClient.Timeout` of `CloudStorageService._httpClient`.
const HTTP_TIMEOUT: Duration = Duration::from_secs(600);

/// `{pct:F0}` uses the current culture; for whole numbers only rounding matters, so the
/// invariant culture gives identical text.
fn pct0(p: f64) -> String {
    fixed(p, 0, &Culture::default())
}

/// `x?.Trim()` for an optional setting.
fn trimmed(s: &Option<String>) -> Option<String> {
    s.as_ref().map(|v| v.trim().to_owned())
}

/// `!IsNullOrWhiteSpace(configured) && Directory.Exists(configured) ? configured.Trim() : detect()`.
fn configured_or_detected(configured: &Option<String>, detect: fn() -> Option<OsString>) -> Option<OsString> {
    match configured {
        Some(c) if !is_null_or_whitespace(Some(c)) && directory_exists(c) => Some(OsString::from(c.trim())),
        _ => detect(),
    }
}

#[allow(clippy::too_many_arguments)]
async fn copy_into(
    root: &OsStr,
    sub: Option<&str>,
    local: &OsStr,
    file_name: &OsStr,
    start_msg: &str,
    pct_prefix: &'static str,
    progress: &ProgressFn,
    ct: &CancellationToken,
) -> Result<OsString, NetError> {
    let dir = match sub {
        Some(s) => {
            let d = combine(root, s);
            create_directory(&d)?;
            d
        }
        None => root.to_owned(),
    };
    let dest = unique_target_file_path(&dir, file_name);
    progress(5.0, start_msg.to_owned());
    let p = progress.clone();
    copy_file_with_progress(local, &dest, Arc::new(move |pct| p(pct, format!("{pct_prefix} {}%", pct0(pct)))), ct).await?;
    Ok(dest)
}

pub async fn google_drive(local: &OsStr, file_name: &OsStr, s: &AppSettings, progress: &ProgressFn, ct: &CancellationToken) -> Result<CloudUploadResult, NetError> {
    let root = configured_or_detected(&s.gdrive_folder_path, detect_google_drive_folder);
    let Some(root) = root.filter(|r| !r.is_empty() && directory_exists(r)) else {
        return Ok(CloudUploadResult::fail(
            "Google Drive folder not found. Please verify Google Drive for Desktop is running or configure a folder in Setup.",
        ));
    };
    let dest = copy_into(&root, Some("VideoConverter"), local, file_name, "Uploading to Google Drive...", "Uploading to Google Drive...", progress, ct).await?;
    Ok(CloudUploadResult {
        success: true,
        message: Some(ncat!("Uploaded to Google Drive (", get_file_name(&dest), ")")),
        destination_path: Some(dest),
        ..Default::default()
    })
}

pub async fn onedrive(local: &OsStr, file_name: &OsStr, s: &AppSettings, progress: &ProgressFn, ct: &CancellationToken) -> Result<CloudUploadResult, NetError> {
    let root = configured_or_detected(&s.onedrive_folder_path, detect_onedrive_folder);
    let Some(root) = root.filter(|r| !r.is_empty() && directory_exists(r)) else {
        return Ok(CloudUploadResult::fail(
            "Microsoft OneDrive folder not found. Please ensure OneDrive is running or configure a folder in Setup.",
        ));
    };
    let dest = copy_into(&root, Some("VideoConverter"), local, file_name, "Uploading to OneDrive...", "Uploading to OneDrive...", progress, ct).await?;
    Ok(CloudUploadResult {
        success: true,
        message: Some(ncat!("Uploaded to OneDrive (", get_file_name(&dest), ")")),
        destination_path: Some(dest),
        ..Default::default()
    })
}

pub async fn custom_folder(local: &OsStr, file_name: &OsStr, s: &AppSettings, progress: &ProgressFn, ct: &CancellationToken) -> Result<CloudUploadResult, NetError> {
    let root = trimmed(&s.custom_cloud_folder_path);
    let Some(root) = root.filter(|r| !r.is_empty() && directory_exists(r)) else {
        return Ok(CloudUploadResult::fail("Custom cloud folder does not exist or is not configured."));
    };
    let dest = copy_into(OsStr::new(&root), None, local, file_name, "Copying to custom cloud folder...", "Copying to cloud folder...", progress, ct).await?;
    Ok(CloudUploadResult {
        success: true,
        message: Some(ncat!("Saved to cloud folder (", get_file_name(&dest), ")")),
        destination_path: Some(dest),
        ..Default::default()
    })
}

/// Runs a request future with `HttpClient.Timeout` (timeouts surface as cancellation, like
/// .NET's `TaskCanceledException`) and the caller's token.
async fn with_timeout<T>(ct: &CancellationToken, fut: impl std::future::Future<Output = Result<T, NetError>>) -> Result<T, NetError> {
    tokio::select! {
        r = tokio::time::timeout(HTTP_TIMEOUT, fut) => r.unwrap_or_else(|_| Err(NetError::canceled())),
        _ = ct.cancelled() => Err(NetError::canceled()),
    }
}

async fn read_body(resp: reqwest::Response, url: &reqwest::Url, ct: &CancellationToken) -> Result<String, NetError> {
    tokio::select! {
        b = resp.bytes() => b.map(|b| decode_body(&b)).map_err(|e| transport_error(&e, url)),
        _ = ct.cancelled() => Err(NetError::canceled()),
    }
}

pub async fn dropbox(
    http: &reqwest::Client,
    local: &OsStr,
    file_name: &OsStr,
    s: &AppSettings,
    progress: &ProgressFn,
    ct: &CancellationToken,
) -> Result<CloudUploadResult, NetError> {
    if !is_null_or_whitespace(s.dropbox_api_token.as_deref()) {
        progress(5.0, "Connecting to Dropbox API...".into());
        let token = s.dropbox_api_token.as_deref().unwrap_or_default().trim().to_owned();
        let mut remote_path: OsString = ncat!("/VideoConverter/", file_name);
        let upload_arg = format!(
            "{{\"path\":{},\"mode\":\"add\",\"autorename\":true,\"mute\":false}}",
            stj_string(&remote_path)
        );

        let file = open_read(local).await?;
        let total = file_length(local)?;
        let p = progress.clone();
        let body = progress_body_stream(file, total, 64 * 1024, Bytes::new(), Bytes::new(), Arc::new(move |pct| p(pct, format!("Uploading to Dropbox... {}%", pct0(pct)))));
        let url = reqwest::Url::parse("https://content.dropboxapi.com/2/files/upload").expect("valid url");
        let auth = HeaderValue::from_str(&format!("Bearer {token}")).map_err(|_| NetError::generic(format!("The format of value 'Bearer {token}' is invalid.")))?;
        let resp = with_timeout(ct, async {
            http.post(url.clone())
                .header(AUTHORIZATION, auth.clone())
                .header("Dropbox-API-Arg", upload_arg.as_str())
                .header(CONTENT_TYPE, "application/octet-stream")
                .header(CONTENT_LENGTH, total)
                .body(reqwest::Body::wrap_stream(body))
                .send()
                .await
                .map_err(|e| transport_error(&e, &url))
        })
        .await?;
        let status = resp.status();
        let body_text = read_body(resp, &url, ct).await?;
        if !status.is_success() {
            return Ok(CloudUploadResult::fail(format!("Dropbox API Error ({}): {body_text}", status_name(status.as_u16()))));
        }

        let mut share_url: Option<String> = None;
        let share = async {
            let doc = JsonDocument::parse(&body_text).ok()?;
            if let Some(d) = doc.root().try_get_property("path_display").ok()? {
                if let Some(p) = d.get_string().ok()? {
                    remote_path = OsString::from(p);
                }
            }
            let share_url_req = reqwest::Url::parse("https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings").ok()?;
            let body = format!("{{\"path\":{}}}", stj_string(&remote_path));
            let text = with_timeout(ct, async {
                let r = http
                    .post(share_url_req.clone())
                    .header(AUTHORIZATION, auth.clone())
                    .header(CONTENT_TYPE, "application/json; charset=utf-8")
                    .body(body)
                    .send()
                    .await
                    .map_err(|e| transport_error(&e, &share_url_req))?;
                if !r.status().is_success() {
                    return Ok(None);
                }
                let b = r.bytes().await.map_err(|e| transport_error(&e, &share_url_req))?;
                Ok(Some(decode_body(&b)))
            })
            .await
            .ok()??;
            let sdoc = JsonDocument::parse(&text).ok()?;
            let u = sdoc.root().try_get_property("url").ok()??;
            u.get_string().ok()?
        };
        if let Some(u) = share.await {
            share_url = Some(u);
        }
        return Ok(CloudUploadResult {
            success: true,
            message: Some(ncat!("Uploaded to Dropbox: ", &remote_path)),
            destination_path: Some(remote_path),
            url: share_url,
            ..Default::default()
        });
    }

    let root = configured_or_detected(&s.dropbox_folder_path, detect_dropbox_folder);
    if let Some(root) = root.filter(|r| !r.is_empty() && directory_exists(r)) {
        let dest = copy_into(&root, Some("VideoConverter"), local, file_name, "Syncing to Dropbox folder...", "Syncing to Dropbox...", progress, ct).await?;
        return Ok(CloudUploadResult {
            success: true,
            message: Some(ncat!("Synced to Dropbox (", get_file_name(&dest), ")")),
            destination_path: Some(dest),
            ..Default::default()
        });
    }
    Ok(CloudUploadResult::fail("Dropbox is not configured. Please enter a Dropbox API token or install the Dropbox desktop app."))
}

/// The multipart frame around the file, exactly as `MultipartFormDataContent` writes it.
pub fn webhook_multipart_frame(boundary: &str, is_discord: bool, file_name: &OsStr) -> (String, String) {
    let mut prefix = String::new();
    if is_discord {
        let payload = format!(
            "{{\"content\":{}}}",
            stj_string(&ncat!("\u{1F3AC} Uploaded with **VideoConverter**: `", file_name, "`"))
        );
        prefix.push_str(&format!("--{boundary}\r\n"));
        prefix.push_str(&PartHeaders { content_type: "application/json; charset=utf-8".into(), name: "payload_json".into(), file_name: None }.render());
        prefix.push_str(&payload);
        prefix.push_str("\r\n");
    }
    prefix.push_str(&format!("--{boundary}\r\n"));
    let name = if is_discord { "files[0]" } else { "file" };
    prefix.push_str(&PartHeaders { content_type: "video/mp4".into(), name: name.into(), file_name: Some(file_name.to_owned()) }.render());
    let suffix = format!("\r\n--{boundary}--\r\n");
    (prefix, suffix)
}

pub async fn webhook(
    http: &reqwest::Client,
    local: &OsStr,
    file_name: &OsStr,
    s: &AppSettings,
    progress: &ProgressFn,
    ct: &CancellationToken,
) -> Result<CloudUploadResult, NetError> {
    let webhook_url = trimmed(&s.webhook_url);
    let Some(webhook_url) = webhook_url.filter(|u| !is_null_or_whitespace(Some(u))) else {
        return Ok(CloudUploadResult::fail(
            "Webhook URL is empty. Please enter a valid Discord or custom Webhook URL in Cloud Setup.",
        ));
    };
    progress(5.0, "Preparing Webhook upload...".into());

    let file = open_read(local).await?;
    let total = file_length(local)?;
    let is_discord = contains_ignore_case(&webhook_url, "discord.com/api/webhooks");
    let boundary = new_boundary();
    let (prefix, suffix) = webhook_multipart_frame(&boundary, is_discord, file_name);
    let length = prefix.len() as u64 + total + suffix.len() as u64;
    let url = parse_request_uri(&webhook_url)?;
    let p = progress.clone();
    let body = progress_body_stream(
        file,
        total,
        64 * 1024,
        Bytes::from(prefix),
        Bytes::from(suffix),
        Arc::new(move |pct| p(pct, format!("Sending to Webhook... {}%", pct0(pct)))),
    );
    let (status, body_text) = with_timeout(ct, async {
        let resp = http
            .post(url.clone())
            .header(CONTENT_TYPE, multipart_content_type(&boundary))
            .header(CONTENT_LENGTH, length)
            .body(reqwest::Body::wrap_stream(body))
            .send()
            .await
            .map_err(|e| transport_error(&e, &url))?;
        let status = resp.status();
        let b = resp.bytes().await.map_err(|e| transport_error(&e, &url))?;
        Ok((status, decode_body(&b)))
    })
    .await?;
    if !status.is_success() {
        return Ok(CloudUploadResult::fail(format!("Webhook error ({}): {body_text}", status_name(status.as_u16()))));
    }
    let attachment = (|| -> Option<String> {
        if body_text.trim().is_empty() {
            return None;
        }
        let doc = JsonDocument::parse(&body_text).ok()?;
        let a = doc.root().try_get_property("attachments").ok()??;
        if a.array_length().ok()? > 0 {
            return a.index(0).ok()?.get_property("url").ok()?.get_string().ok()?;
        }
        None
    })();
    Ok(CloudUploadResult {
        success: true,
        url: Some(attachment.unwrap_or(webhook_url)),
        message: Some("Successfully sent to Webhook!".into()),
        ..Default::default()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn webhook_frame_matches_dotnet_layout() {
        let (p, s) = webhook_multipart_frame("b0", true, OsStr::new("clip.mp4"));
        let bs = '\\';
        let expected_payload = format!("{{\"content\":\"{bs}uD83C{bs}uDFAC Uploaded with **VideoConverter**: {bs}u0060clip.mp4{bs}u0060\"}}");
        assert_eq!(
            p,
            format!("--b0\r\nContent-Type: application/json; charset=utf-8\r\nContent-Disposition: form-data; name=payload_json\r\n\r\n{expected_payload}\r\n--b0\r\nContent-Type: video/mp4\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=clip.mp4; filename*=utf-8''clip.mp4\r\n\r\n")
        );
        assert_eq!(s, "\r\n--b0--\r\n");
        let (p2, _) = webhook_multipart_frame("b1", false, OsStr::new("My Clip.mp4"));
        assert_eq!(
            p2,
            "--b1\r\nContent-Type: video/mp4\r\nContent-Disposition: form-data; name=file; filename=\"My Clip.mp4\"; filename*=utf-8''My%20Clip.mp4\r\n\r\n"
        );
    }

    #[tokio::test]
    async fn folder_providers_copy_and_report() {
        use std::sync::Mutex;
        let d = std::env::temp_dir().join(format!("ferrite_prov_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("dest")).unwrap();
        let src = d.join("v.mp4");
        std::fs::write(&src, vec![1u8; 2048]).unwrap();
        let mut s = AppSettings { custom_cloud_folder_path: Some(format!("  {}  ", d.join("dest").display())), ..Default::default() };
        let msgs = Arc::new(Mutex::new(Vec::<(f64, String)>::new()));
        let m2 = msgs.clone();
        let p: ProgressFn = Arc::new(move |pct, m| m2.lock().unwrap().push((pct, m)));
        let r = custom_folder(src.as_os_str(), OsStr::new("v.mp4"), &s, &p, &CancellationToken::new()).await.unwrap();
        assert!(r.success);
        assert_eq!(r.message, Some(OsString::from("Saved to cloud folder (v.mp4)")));
        let m = msgs.lock().unwrap().clone();
        assert_eq!(m[0], (5.0, "Copying to custom cloud folder...".to_owned()));
        assert_eq!(m.last().unwrap(), &(100.0, "Copying to cloud folder... 100%".to_owned()));
        s.custom_cloud_folder_path = Some(r"C:\missing_xyz".into());
        let r = custom_folder(src.as_os_str(), OsStr::new("v.mp4"), &s, &p, &CancellationToken::new()).await.unwrap();
        assert_eq!(r.error_message, Some(OsString::from("Custom cloud folder does not exist or is not configured.")));
        std::fs::remove_dir_all(&d).unwrap();
    }
}
