//! The engine instance: the state `NativeEngineService` keeps in fields, plus the public
//! entry points that implement `IEngineService`.

use std::collections::HashMap;
use std::ffi::{OsStr, OsString};
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

use tokio::sync::watch;
use tokio_util::sync::CancellationToken;

use crate::binaries::{resolve_binaries, Binaries};
use crate::dotnet::fmt::Culture;
use crate::dotnet::ioerr::NetError;
use crate::dotnet::text::is_blank_os;
use crate::host::{Host, HostInfo};
use crate::jobs::JobContext;
use crate::models::{MetadataResponse, PlaylistInfo};
use crate::proc::env::configured_path;
use crate::proc::env::EnvMode;
use crate::settings::AppSettings;

/// Arguments of `IEngineService.CreateJobAsync` (the `CancellationToken` is ignored by Native).
#[derive(Debug, Clone, Default)]
pub struct JobRequest {
    pub url: Option<OsString>,
    pub mode: Option<String>,
    pub platform: Option<String>,
    pub title: Option<OsString>,
    pub description: Option<OsString>,
    pub tags: Option<Vec<OsString>>,
    pub output_dir: Option<OsString>,
    pub privacy: Option<String>,
    pub fingerprint: bool,
    pub fingerprint_method: Option<String>,
    pub use_gpu: bool,
    pub quality: Option<String>,
    pub cloud_upload: bool,
    pub cloud_provider: Option<String>,
    pub cloud_temp_only: bool,
}

/// An error returned to the host: cancellation or an exception message.
pub type CallError = NetError;

pub struct EngineInner {
    pub host: Arc<dyn Host>,
    bins: RwLock<Binaries>,
    init: Mutex<Option<watch::Receiver<bool>>>,
    pub(crate) jobs: Mutex<HashMap<String, Arc<JobContext>>>,
    /// `NativeEngineService._httpClient` (8 s timeout, used for oEmbed).
    pub(crate) oembed_http: reqwest::Client,
    /// `CloudStorageService._httpClient` (10 min timeout, applied per call like .NET).
    pub(crate) cloud_http: reqwest::Client,
    /// Serialises interactive YouTube sign-ins.
    pub(crate) youtube_auth: tokio::sync::Mutex<()>,
    /// Pipelines that have not finished yet (including ones removed by `Dispose`).
    pub(crate) active_pipelines: std::sync::atomic::AtomicUsize,
}

/// Cheap to clone; all clones share one engine.
#[derive(Clone)]
pub struct Engine(pub(crate) Arc<EngineInner>);

fn http_client(timeout: Option<Duration>) -> reqwest::Client {
    let mut b = reqwest::Client::builder().http1_only();
    if let Some(t) = timeout {
        b = b.timeout(t);
    }
    b.build().unwrap_or_else(|_| reqwest::Client::new())
}

impl Engine {
    pub fn new(host: Arc<dyn Host>) -> Engine {
        Engine(Arc::new(EngineInner {
            host,
            bins: RwLock::new(Binaries::default()),
            init: Mutex::new(None),
            jobs: Mutex::new(HashMap::new()),
            oembed_http: http_client(Some(Duration::from_secs(8))),
            cloud_http: http_client(None),
            youtube_auth: tokio::sync::Mutex::new(()),
            active_pipelines: std::sync::atomic::AtomicUsize::new(0),
        }))
    }

    pub fn host(&self) -> &Arc<dyn Host> {
        &self.0.host
    }

    pub fn info(&self) -> &HostInfo {
        self.0.host.info()
    }

    pub fn culture(&self) -> &Culture {
        &self.0.host.info().culture
    }

    /// A snapshot of `_ffmpegPath` / `_ffprobePath` / `_ytDlpRunner`.
    pub fn bins(&self) -> Binaries {
        self.0.bins.read().unwrap_or_else(|e| e.into_inner()).clone()
    }

    pub(crate) fn set_bins(&self, b: Binaries) {
        *self.0.bins.write().unwrap_or_else(|e| e.into_inner()) = b;
    }

    /// `EngineProvider.EmitOutput(line)` (blank lines are dropped).
    pub fn emit_output(&self, line: impl AsRef<OsStr>) {
        let line = line.as_ref();
        if !is_blank_os(line) {
            self.0.host.emit_output(line);
        }
    }

    /// `SettingsService.Current`, read now.
    pub async fn settings(&self) -> AppSettings {
        self.0.host.get_settings().await
    }

    /// The `ConfigureProcessStartInfo` environment for the current binaries.
    pub fn configured_env(&self) -> EnvMode {
        let b = self.bins();
        EnvMode::Configured { path: configured_path(b.ffmpeg_path.as_ref(), b.runner_exe_file().as_ref()) }
    }

    fn init_receiver(&self) -> watch::Receiver<bool> {
        let mut g = self.0.init.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(rx) = g.as_ref() {
            return rx.clone();
        }
        let (tx, rx) = watch::channel(false);
        let me = self.clone();
        tokio::task::spawn_blocking(move || {
            let b = resolve_binaries(me.info());
            me.set_bins(b);
            let _ = tx.send(true);
        });
        *g = Some(rx.clone());
        rx
    }

    /// `EnsureInitializedAsync().WaitAsync(ct)`: binary resolution runs once on a worker
    /// thread; cancelling only stops waiting.
    pub async fn ensure_initialized(&self, ct: Option<&CancellationToken>) -> Result<(), NetError> {
        let mut rx = self.init_receiver();
        let wait = async move {
            let _ = rx.wait_for(|done| *done).await;
        };
        match ct {
            Some(ct) => tokio::select! {
                _ = wait => Ok(()),
                _ = ct.cancelled() => Err(NetError::canceled()),
            },
            None => {
                wait.await;
                Ok(())
            }
        }
    }

    /// Re-runs `ResolveBinaries()` synchronously (after FFmpeg was installed).
    pub(crate) async fn re_resolve_binaries(&self) {
        let me = self.clone();
        let b = tokio::task::spawn_blocking(move || resolve_binaries(me.info())).await;
        if let Ok(b) = b {
            self.set_bins(b);
        }
    }

    /// `WaitForHealthAsync(ct, maxWait)`: waits for initialization (`maxWait` is ignored).
    pub async fn wait_for_health(&self, ct: &CancellationToken) -> Result<bool, CallError> {
        self.ensure_initialized(Some(ct)).await?;
        Ok(true)
    }

    /// `FetchMetadataAsync(url, ct)`.
    pub async fn fetch_metadata(&self, url: Option<OsString>, ct: &CancellationToken) -> Result<Option<MetadataResponse>, CallError> {
        crate::metadata::fetch_metadata(self, url.as_deref(), ct).await
    }

    /// `FetchYTMusicMetadataAsync(url, ct)`.
    pub async fn fetch_ytmusic_metadata(&self, url: Option<OsString>, ct: &CancellationToken) -> Result<Option<MetadataResponse>, CallError> {
        crate::metadata::fetch_ytmusic_metadata(self, url.as_deref(), ct).await
    }

    /// `FetchPlaylistAsync(url, maxItems, ct)`.
    pub async fn fetch_playlist(&self, url: Option<OsString>, max_items: i32, ct: &CancellationToken) -> Result<Option<PlaylistInfo>, CallError> {
        crate::playlist::fetch_playlist(self, url.as_deref(), max_items, ct).await
    }

    /// `GenerateSubtitlesAsync(videoPath, modelSize, language, ct)`.
    pub async fn generate_subtitles(
        &self,
        video_path: Option<OsString>,
        model_size: Option<OsString>,
        language: Option<OsString>,
        ct: &CancellationToken,
    ) -> Result<OsString, CallError> {
        crate::subtitles::generate_subtitles(self, video_path, model_size, language, ct).await
    }

    /// `CreateJobAsync(...)`: registers the job, starts the pipeline and returns the id.
    pub fn create_job(&self, req: JobRequest) -> String {
        crate::jobs::create_job(self, req, |_| {})
    }

    /// Like [`Engine::create_job`], calling `registered` with the id before the pipeline
    /// starts (so the sidecar can answer the call before the first status is sent).
    pub fn create_job_with(&self, req: JobRequest, registered: impl FnOnce(&str)) -> String {
        crate::jobs::create_job(self, req, registered)
    }

    /// `CancelJobAsync(jobId)`.
    pub fn cancel_job(&self, job_id: &str) {
        crate::jobs::cancel_job(self, job_id)
    }

    /// `Dispose()`.
    pub fn dispose(&self) {
        crate::jobs::dispose(self)
    }

    /// Number of jobs whose pipeline is still running (used by the sidecar to let
    /// cancelled pipelines report before the process exits).
    pub fn running_jobs(&self) -> usize {
        crate::jobs::running_jobs(self)
    }
}
