//! `ferrite.exe`: exposes the Ferrite engine to the WPF host over newline-delimited JSON on
//! stdin/stdout (protocol v1, see backend-ferrite/PORTING.md §11.1).

#![cfg(windows)]

mod protocol;

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::{mpsc, oneshot};
use tokio_util::sync::CancellationToken;
use windows_sys::Win32::Foundation::{SetHandleInformation, HANDLE_FLAG_INHERIT};
use windows_sys::Win32::System::Console::{GetStdHandle, STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE};

use ferrite_engine::dotnet::fmt::Culture;
use ferrite_engine::host::{Host, HostFuture};
use ferrite_engine::models::{os_to_json, JobStatus};
use ferrite_engine::settings::AppSettings;
use ferrite_engine::{Engine, HostInfo};

/// State shared by the reader loop, the writer task and the host callbacks.
struct Shared {
    out: mpsc::UnboundedSender<String>,
    pending_host: Mutex<HashMap<u64, oneshot::Sender<Value>>>,
    next_host_id: AtomicU64,
    info: OnceLock<HostInfo>,
}

impl Shared {
    fn send(&self, v: Value) {
        let _ = self.out.send(v.to_string());
    }

    async fn host_call(&self, method: &str) -> Value {
        let id = self.next_host_id.fetch_add(1, Ordering::SeqCst);
        let (tx, rx) = oneshot::channel();
        self.pending_host.lock().unwrap_or_else(|e| e.into_inner()).insert(id, tx);
        self.send(json!({ "type": "host_call", "id": id, "method": method }));
        rx.await.unwrap_or(Value::Null)
    }
}

struct SidecarHost(Arc<Shared>);

impl Host for SidecarHost {
    fn info(&self) -> &HostInfo {
        self.0.info.get().expect("hello received before the engine exists")
    }

    fn emit_output(&self, line: &std::ffi::OsStr) {
        self.0.send(json!({ "type": "log", "line": os_to_json(line) }));
    }

    fn job_status(&self, status: &JobStatus) {
        self.0.send(json!({ "type": "job_status", "job_id": status.id, "status": status.to_json() }));
    }

    fn job_complete(&self, job_id: &str) {
        self.0.send(json!({ "type": "job_complete", "job_id": job_id }));
    }

    fn get_settings(&self) -> HostFuture<'_, AppSettings> {
        Box::pin(async move { AppSettings::from_json(&self.0.host_call("get_settings").await) })
    }

    fn ensure_ffmpeg(&self) -> HostFuture<'_, bool> {
        Box::pin(async move {
            let v = self.0.host_call("ensure_ffmpeg").await;
            if let Some(path) = v.get("path").and_then(Value::as_str) {
                std::env::set_var("PATH", path);
            }
            v.get("installed").and_then(Value::as_bool).unwrap_or(false)
        })
    }
}

/// The pipes to the host must not leak into yt-dlp/ffmpeg (they would keep them open).
fn make_std_handles_non_inheritable() {
    for id in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
        // SAFETY: plain FFI calls on this process's standard handles.
        unsafe {
            let h = GetStdHandle(id);
            if !h.is_null() {
                SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
            }
        }
    }
}

fn host_info_from_hello(v: &Value) -> HostInfo {
    let s = |k: &str| v.get(k).and_then(Value::as_str).map(str::to_owned);
    let c = &v["culture"];
    let cs = |k: &str, d: &str| c.get(k).and_then(Value::as_str).unwrap_or(d).to_owned();
    HostInfo {
        base_dir: s("base_dir").unwrap_or_default(),
        process_path: s("process_path"),
        cwd: s("cwd").unwrap_or_default(),
        culture: Culture {
            name: cs("name", ""),
            decimal_separator: cs("decimal_separator", "."),
            negative_sign: cs("negative_sign", "-"),
            positive_infinity: cs("positive_infinity", "Infinity"),
            negative_infinity: cs("negative_infinity", "-Infinity"),
            nan: cs("nan", "NaN"),
        },
    }
}

fn main() {
    make_std_handles_non_inheritable();
    let rt = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(rt) => rt,
        Err(e) => {
            eprintln!("ferrite: could not start the async runtime: {e}");
            std::process::exit(2);
        }
    };
    rt.block_on(run());
    rt.shutdown_timeout(Duration::from_secs(1));
}

async fn run() {
    let (tx, mut rx) = mpsc::unbounded_channel::<String>();
    let writer = tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(first) = rx.recv().await {
            let mut batch = first;
            batch.push('\n');
            while let Ok(more) = rx.try_recv() {
                batch.push_str(&more);
                batch.push('\n');
            }
            if stdout.write_all(batch.as_bytes()).await.is_err() || stdout.flush().await.is_err() {
                break;
            }
        }
    });

    let shared = Arc::new(Shared {
        out: tx,
        pending_host: Mutex::new(HashMap::new()),
        next_host_id: AtomicU64::new(1),
        info: OnceLock::new(),
    });
    let mut engine: Option<Engine> = None;
    let calls: Arc<Mutex<HashMap<u64, CancellationToken>>> = Arc::new(Mutex::new(HashMap::new()));

    let mut lines = BufReader::new(tokio::io::stdin()).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        let Ok(msg) = serde_json::from_str::<Value>(&line) else { continue };
        match msg.get("type").and_then(Value::as_str).unwrap_or("") {
            "hello" => {
                let info = host_info_from_hello(&msg);
                if !info.cwd.is_empty() {
                    let _ = std::env::set_current_dir(&info.cwd);
                }
                let _ = shared.info.set(info);
                engine = Some(Engine::new(Arc::new(SidecarHost(shared.clone()))));
                shared.send(json!({ "type": "ready", "protocol": 1, "version": env!("CARGO_PKG_VERSION") }));
            }
            "call" => {
                let Some(e) = engine.clone() else { continue };
                let id = msg.get("id").and_then(Value::as_u64).unwrap_or(0);
                let method = msg.get("method").and_then(Value::as_str).unwrap_or("").to_owned();
                let params = msg.get("params").cloned().unwrap_or(Value::Null);
                let ct = CancellationToken::new();
                calls.lock().unwrap_or_else(|p| p.into_inner()).insert(id, ct.clone());
                let (sh, calls2) = (shared.clone(), calls.clone());
                tokio::spawn(async move {
                    let sh2 = sh.clone();
                    let task = tokio::spawn(async move { protocol::dispatch(&e, &sh2.out, id, &method, &params, &ct).await });
                    let result = match task.await {
                        Ok(r) => r,
                        Err(join) => Err(ferrite_engine::dotnet::ioerr::NetError::generic(format!(
                            "Ferrite internal error: {}",
                            panic_text(join)
                        ))),
                    };
                    calls2.lock().unwrap_or_else(|p| p.into_inner()).remove(&id);
                    if let Some(v) = protocol::result_message(id, result) {
                        sh.send(v);
                    }
                });
            }
            "cancel" => {
                let id = msg.get("id").and_then(Value::as_u64).unwrap_or(0);
                if let Some(ct) = calls.lock().unwrap_or_else(|p| p.into_inner()).get(&id) {
                    ct.cancel();
                }
            }
            "host_result" => {
                let id = msg.get("id").and_then(Value::as_u64).unwrap_or(0);
                let tx = shared.pending_host.lock().unwrap_or_else(|e| e.into_inner()).remove(&id);
                if let Some(tx) = tx {
                    let _ = tx.send(msg.get("value").cloned().unwrap_or(Value::Null));
                }
            }
            "env" => {
                if let Some(path) = msg.get("path").and_then(Value::as_str) {
                    std::env::set_var("PATH", path);
                }
            }
            "shutdown" => break,
            _ => {}
        }
    }

    // Dispose(): cancel every job and kill its processes, then give the pipelines a moment to
    // report "cancelled" before the process exits.
    for ct in calls.lock().unwrap_or_else(|p| p.into_inner()).values() {
        ct.cancel();
    }
    if let Some(e) = engine.as_ref() {
        e.dispose();
        for _ in 0..50 {
            if e.running_jobs() == 0 {
                break;
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }
    drop(engine);
    // Unblock host calls that will never be answered.
    shared.pending_host.lock().unwrap_or_else(|e| e.into_inner()).clear();
    drop(shared);
    let _ = tokio::time::timeout(Duration::from_secs(2), writer).await;
}

fn panic_text(e: tokio::task::JoinError) -> String {
    if e.is_cancelled() {
        return "the call was aborted".into();
    }
    match e.try_into_panic() {
        Ok(p) => p
            .downcast_ref::<&str>()
            .map(|s| (*s).to_owned())
            .or_else(|| p.downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "panic".into()),
        Err(_) => "unknown failure".into(),
    }
}
