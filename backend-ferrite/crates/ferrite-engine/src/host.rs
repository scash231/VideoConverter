//! What the engine needs from the process that hosts it. In-process, Native read these from
//! .NET directly (`MediaTools.AppDir`, `SettingsService.Current`, the FFmpeg dialog, ...);
//! Ferrite gets them from the WPF host over the sidecar protocol.

use std::ffi::OsStr;
use std::future::Future;
use std::pin::Pin;

use crate::dotnet::fmt::Culture;
use crate::models::JobStatus;
use crate::settings::AppSettings;

/// Facts about the WPF host process that Native read from its own process.
#[derive(Debug, Clone, Default)]
pub struct HostInfo {
    /// `MediaTools.AppDir`: folder of the real VideoConverterUI.exe (with trailing separator).
    pub base_dir: String,
    /// `Environment.ProcessPath`.
    pub process_path: Option<String>,
    /// `Directory.GetCurrentDirectory()`, used in process start error texts.
    pub cwd: String,
    /// `CultureInfo.CurrentCulture` number format.
    pub culture: Culture,
}

pub type HostFuture<'a, T> = Pin<Box<dyn Future<Output = T> + Send + 'a>>;

pub trait Host: Send + Sync + 'static {
    fn info(&self) -> &HostInfo;

    /// `EngineProvider.EmitOutput` (callers already filter blank lines).
    fn emit_output(&self, line: &OsStr);

    /// One `JobStatus` written to the job's channel.
    fn job_status(&self, status: &JobStatus);

    /// `ctx.Channel.Writer.TryComplete()`.
    fn job_complete(&self, job_id: &str);

    /// `SettingsService.Current` at this moment.
    fn get_settings(&self) -> HostFuture<'_, AppSettings>;

    /// `FFmpegInstallerService.PromptAndInstallIfNeededAsync()`.
    fn ensure_ffmpeg(&self) -> HostFuture<'_, bool>;
}
