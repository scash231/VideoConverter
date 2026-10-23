//! Call dispatch: maps protocol calls onto `IEngineService` members.

use std::ffi::OsString;

use serde_json::{json, Value};
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use ferrite_engine::dotnet::ioerr::NetError;
use ferrite_engine::models::{json_to_os, os_to_json};
use ferrite_engine::{Engine, JobRequest};

/// A call's outcome: `Ok(Some(value))`, `Ok(None)` when the result was already sent
/// (create_job answers before its pipeline starts), or an exception.
pub type CallResult = Result<Option<Value>, NetError>;

fn os(p: &Value, k: &str) -> Option<OsString> {
    p.get(k).and_then(json_to_os)
}

fn string(p: &Value, k: &str) -> Option<String> {
    p.get(k).and_then(Value::as_str).map(str::to_owned)
}

fn boolean(p: &Value, k: &str, default: bool) -> bool {
    p.get(k).and_then(Value::as_bool).unwrap_or(default)
}

pub fn ok_message(id: u64, value: Value) -> Value {
    json!({ "type": "result", "id": id, "ok": true, "value": value })
}

pub fn result_message(id: u64, r: CallResult) -> Option<Value> {
    match r {
        Ok(Some(v)) => Some(ok_message(id, v)),
        Ok(None) => None,
        Err(e) if e.is_canceled() => Some(json!({
            "type": "result", "id": id, "ok": false,
            "error": { "kind": "cancelled", "message": os_to_json(&e.message) }
        })),
        Err(e) => Some(json!({
            "type": "result", "id": id, "ok": false,
            "error": { "kind": "error", "message": os_to_json(&e.message) }
        })),
    }
}

pub fn job_request(p: &Value) -> JobRequest {
    JobRequest {
        url: os(p, "url"),
        mode: string(p, "mode"),
        platform: string(p, "platform"),
        title: os(p, "title"),
        description: os(p, "description"),
        tags: p.get("tags").and_then(Value::as_array).map(|a| a.iter().filter_map(json_to_os).collect()),
        output_dir: os(p, "output_dir"),
        privacy: string(p, "privacy"),
        fingerprint: boolean(p, "fingerprint", false),
        fingerprint_method: string(p, "fingerprint_method"),
        use_gpu: boolean(p, "use_gpu", false),
        quality: string(p, "quality"),
        cloud_upload: boolean(p, "cloud_upload", false),
        cloud_provider: string(p, "cloud_provider"),
        cloud_temp_only: boolean(p, "cloud_temp_only", true),
    }
}

pub async fn dispatch(e: &Engine, out: &mpsc::UnboundedSender<String>, id: u64, method: &str, p: &Value, ct: &CancellationToken) -> CallResult {
    match method {
        "wait_for_health" => e.wait_for_health(ct).await.map(|b| Some(json!(b))),
        "fetch_metadata" => e.fetch_metadata(os(p, "url"), ct).await.map(|m| Some(m.map_or(Value::Null, |m| m.to_json()))),
        "fetch_ytmusic_metadata" => e.fetch_ytmusic_metadata(os(p, "url"), ct).await.map(|m| Some(m.map_or(Value::Null, |m| m.to_json()))),
        "fetch_playlist" => {
            let max = p.get("max_items").and_then(Value::as_i64).unwrap_or(100) as i32;
            e.fetch_playlist(os(p, "url"), max, ct).await.map(|pl| Some(pl.map_or(Value::Null, |pl| pl.to_json())))
        }
        "generate_subtitles" => e
            .generate_subtitles(os(p, "video_path"), os(p, "model_size"), os(p, "language"), ct)
            .await
            .map(|s| Some(os_to_json(&s))),
        "create_job" => {
            let out = out.clone();
            e.create_job_with(job_request(p), move |job_id| {
                let _ = out.send(ok_message(id, json!(job_id)).to_string());
            });
            Ok(None)
        }
        "cancel_job" => {
            if let Some(job) = string(p, "job_id") {
                e.cancel_job(&job);
            }
            Ok(Some(Value::Null))
        }
        other => Err(NetError::generic(format!("Unknown Ferrite call '{other}'."))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn job_request_defaults() {
        let r = job_request(&json!({ "url": "u", "mode": "download", "tags": ["a", {"$utf16": [98]}] }));
        assert_eq!(r.url, Some("u".into()));
        assert_eq!(r.tags, Some(vec!["a".into(), "b".into()]));
        assert!(r.cloud_temp_only);
        assert!(!r.fingerprint);
    }

    #[test]
    fn result_messages() {
        let m = result_message(3, Err(NetError::canceled())).unwrap();
        assert_eq!(m["error"]["kind"], "cancelled");
        let m = result_message(4, Err(NetError::generic("boom"))).unwrap();
        assert_eq!(m["error"]["message"], "boom");
        assert!(result_message(5, Ok(None)).is_none());
        assert_eq!(result_message(6, Ok(Some(json!(true)))).unwrap()["value"], true);
    }
}
