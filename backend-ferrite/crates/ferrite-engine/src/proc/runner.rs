//! Starting, streaming, waiting for and killing processes like Native does.
//!
//! * [`run_to_end`] = `MediaTools.RunProcessAsync` (read both streams to the end, optional
//!   timeout, kill the tree on cancel/timeout).
//! * [`run_streaming`] = the `OutputDataReceived` / `ErrorDataReceived` + `WaitForExitOrKillAsync`
//!   pattern used for yt-dlp and ffmpeg encodes.
//! * [`test_executable`] = `NativeEngineService.TestExecutable` (3.5 s, output not read).

use std::ffi::{OsStr, OsString};
use std::os::windows::io::AsRawHandle;
use std::os::windows::process::CommandExt;
use std::process::Stdio;
use std::sync::Mutex;
use std::time::Duration;

use tokio::io::AsyncReadExt;
use tokio_util::sync::CancellationToken;
use windows_sys::Win32::Foundation::WAIT_OBJECT_0;
use windows_sys::Win32::System::Threading::WaitForSingleObject;

use super::decode::{decode_all, Decoder, LineSplitter};
use super::env::EnvMode;
use super::killtree::kill_tree;
use super::resolve::resolve;
use crate::dotnet::cmdline::Args;
use crate::dotnet::ioerr::{process_start_failed, NetError, NetErrorKind};
use crate::dotnet::path::get_file_name;
use crate::host::HostInfo;
use crate::ncat;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// A process to start: `ProcessStartInfo.FileName` plus arguments and environment.
#[derive(Debug, Clone)]
pub struct ProcSpec {
    pub file_name: OsString,
    pub args: Args,
    pub env: EnvMode,
}

impl ProcSpec {
    pub fn raw(file_name: impl Into<OsString>, args: impl Into<OsString>, env: EnvMode) -> Self {
        ProcSpec { file_name: file_name.into(), args: Args::Raw(args.into()), env }
    }

    pub fn list(file_name: impl Into<OsString>, args: Vec<OsString>, env: EnvMode) -> Self {
        ProcSpec { file_name: file_name.into(), args: Args::List(args), env }
    }

    /// The command line .NET would build: `"FileName" args`.
    pub fn command_line(&self) -> OsString {
        let tail = self.args.to_command_tail();
        if tail.is_empty() {
            ncat!("\"", &self.file_name, "\"")
        } else {
            ncat!("\"", &self.file_name, "\" ", tail)
        }
    }
}

/// `ExitCode` / `StandardOutput.ReadToEnd()` / `StandardError.ReadToEnd()`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

/// `JobContext.RunningProcess`: the pid of the process a job is currently running, so
/// `CancelJobAsync` / `Dispose` can kill it.
#[derive(Debug, Default)]
pub struct RunningSlot(Mutex<Option<u32>>);

impl RunningSlot {
    pub fn set(&self, pid: Option<u32>) {
        *self.0.lock().unwrap_or_else(|e| e.into_inner()) = pid;
    }

    /// Kills the running process tree, if any (errors ignored like Native's `try { } catch { }`).
    pub fn kill(&self) {
        let pid = *self.0.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(pid) = pid {
            kill_tree(pid);
        }
    }
}

fn apply_env_std(cmd: &mut std::process::Command, env: &EnvMode) {
    match env {
        EnvMode::Configured { path } => {
            cmd.env("PYTHONUTF8", "1");
            cmd.env("PYTHONIOENCODING", "utf-8");
            cmd.env("PATH", path);
        }
        EnvMode::PythonOnly => {
            cmd.env("PYTHONUTF8", "1");
            cmd.env("PYTHONIOENCODING", "utf-8");
        }
        EnvMode::Inherit => {}
    }
}

fn build_command(spec: &ProcSpec, host: &HostInfo) -> Result<std::process::Command, NetError> {
    let exe = resolve(&spec.file_name, host)
        .ok_or_else(|| process_start_failed(&spec.file_name, OsStr::new(&host.cwd), 2))?;
    let mut cmd = std::process::Command::new(exe);
    let tail = spec.args.to_command_tail();
    if !tail.is_empty() {
        cmd.raw_arg(tail);
    }
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    apply_env_std(&mut cmd, &spec.env);
    Ok(cmd)
}

fn start_error(spec: &ProcSpec, host: &HostInfo, e: &std::io::Error) -> NetError {
    let code = e.raw_os_error().map(|c| c as u32).unwrap_or(2);
    process_start_failed(&spec.file_name, OsStr::new(&host.cwd), code)
}

fn start(spec: &ProcSpec, host: &HostInfo) -> Result<tokio::process::Child, NetError> {
    let cmd = build_command(spec, host)?;
    let mut cmd = tokio::process::Command::from(cmd);
    cmd.kill_on_drop(false);
    cmd.spawn().map_err(|e| start_error(spec, host, &e))
}

/// `MediaTools.RunProcessAsync(psi, ct, timeoutMs)`.
pub async fn run_to_end(
    spec: &ProcSpec,
    host: &HostInfo,
    ct: &CancellationToken,
    timeout_ms: Option<u64>,
) -> Result<ProcessResult, NetError> {
    let mut child = start(spec, host)?;
    let pid = child.id().unwrap_or(0);
    let mut out = child.stdout.take();
    let mut err = child.stderr.take();
    let out_task = tokio::spawn(async move {
        let mut buf = Vec::new();
        if let Some(o) = out.as_mut() {
            let _ = o.read_to_end(&mut buf).await;
        }
        decode_all(&buf)
    });
    let err_task = tokio::spawn(async move {
        let mut buf = Vec::new();
        if let Some(e) = err.as_mut() {
            let _ = e.read_to_end(&mut buf).await;
        }
        decode_all(&buf)
    });

    let timeout = async {
        match timeout_ms {
            Some(ms) => tokio::time::sleep(Duration::from_millis(ms)).await,
            None => std::future::pending::<()>().await,
        }
    };
    let status = tokio::select! {
        s = child.wait() => s,
        _ = ct.cancelled() => {
            kill_tree(pid);
            return Err(NetError::canceled());
        }
        _ = timeout => {
            kill_tree(pid);
            if ct.is_cancelled() {
                return Err(NetError::canceled());
            }
            let name = get_file_name(&spec.file_name);
            return Err(NetError::new(
                NetErrorKind::Timeout,
                ncat!(name, format!(" did not finish within {} ms.", timeout_ms.unwrap_or(0))),
            ));
        }
    };
    let exit_code = status.ok().and_then(|s| s.code()).unwrap_or(-1);
    let stdout = out_task.await.unwrap_or_default();
    let stderr = err_task.await.unwrap_or_default();
    Ok(ProcessResult { exit_code, stdout, stderr })
}

async fn pump_lines<R: tokio::io::AsyncRead + Unpin>(reader: Option<R>, mut on_line: impl FnMut(String)) {
    let Some(mut r) = reader else { return };
    let mut dec = Decoder::new();
    let mut split = LineSplitter::default();
    let mut buf = vec![0u8; 4096];
    let mut text = String::new();
    loop {
        match r.read(&mut buf).await {
            Ok(0) | Err(_) => break,
            Ok(n) => {
                text.clear();
                dec.push(&buf[..n], &mut text);
                for line in split.push(&text) {
                    on_line(line);
                }
            }
        }
    }
    text.clear();
    dec.finish(&mut text);
    for line in split.push(&text) {
        on_line(line);
    }
    if let Some(last) = split.finish() {
        on_line(last);
    }
}

/// Starts a process, feeds every stdout / stderr line to the callbacks (in order per stream)
/// and waits for exit and end of both streams. On cancellation the process tree is killed and
/// `OperationCanceledException` is returned (`WaitForExitOrKillAsync`).
pub async fn run_streaming(
    spec: &ProcSpec,
    host: &HostInfo,
    ct: &CancellationToken,
    slot: &RunningSlot,
    on_stdout: impl FnMut(String),
    on_stderr: impl FnMut(String),
) -> Result<i32, NetError> {
    let mut child = start(spec, host)?;
    let pid = child.id().unwrap_or(0);
    slot.set(Some(pid));
    let out = child.stdout.take();
    let err = child.stderr.take();
    let work = async {
        let (_, _, status) = tokio::join!(pump_lines(out, on_stdout), pump_lines(err, on_stderr), child.wait());
        status
    };
    let result = tokio::select! {
        status = work => Ok(status.ok().and_then(|s| s.code()).unwrap_or(-1)),
        _ = ct.cancelled() => {
            kill_tree(pid);
            Err(NetError::canceled())
        }
    };
    slot.set(None);
    result
}

/// `TestExecutable(exe, args)`: exit code 0 within 3.5 s; the redirected output is not read.
/// Blocking; call from a blocking context.
pub fn test_executable(exe: &OsStr, args: &str, host: &HostInfo) -> bool {
    let spec = ProcSpec::raw(exe, args, EnvMode::PythonOnly);
    let mut cmd = match build_command(&spec, host) {
        Ok(c) => c,
        Err(_) => return false,
    };
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(_) => return false,
    };
    // SAFETY: the child's process handle stays valid while `child` is alive.
    let rc = unsafe { WaitForSingleObject(child.as_raw_handle() as _, 3500) };
    if rc != WAIT_OBJECT_0 {
        kill_tree(child.id());
        let _ = child.wait();
        return false;
    }
    matches!(child.wait().ok().and_then(|s| s.code()), Some(0))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn host() -> HostInfo {
        HostInfo { cwd: std::env::current_dir().unwrap().to_string_lossy().into_owned(), ..Default::default() }
    }

    #[tokio::test]
    async fn runs_and_reads_output() {
        let spec = ProcSpec::raw("cmd", "/c echo hello& echo err 1>&2& exit 3", EnvMode::Inherit);
        let r = run_to_end(&spec, &host(), &CancellationToken::new(), Some(10_000)).await.unwrap();
        assert_eq!(r.exit_code, 3);
        assert_eq!(r.stdout.trim(), "hello");
        assert_eq!(r.stderr.trim(), "err");
    }

    #[tokio::test]
    async fn start_failure_message_matches_dotnet_format() {
        let spec = ProcSpec::raw("does-not-exist-xyz", "--x", EnvMode::Inherit);
        let e = run_to_end(&spec, &host(), &CancellationToken::new(), None).await.unwrap_err();
        let msg = e.message.to_string_lossy().into_owned();
        assert!(msg.starts_with("An error occurred trying to start process 'does-not-exist-xyz' with working directory '"), "{msg}");
    }

    #[tokio::test]
    async fn timeout_kills_and_reports() {
        let spec = ProcSpec::raw("cmd", "/c ping -n 30 127.0.0.1 >nul", EnvMode::Inherit);
        let e = run_to_end(&spec, &host(), &CancellationToken::new(), Some(300)).await.unwrap_err();
        assert_eq!(e.message, OsString::from("cmd did not finish within 300 ms."));
    }

    #[tokio::test]
    async fn streaming_lines_and_cancel() {
        let slot = RunningSlot::default();
        let mut lines = Vec::new();
        let spec = ProcSpec::raw("cmd", "/c echo one& echo two", EnvMode::Inherit);
        let code = run_streaming(&spec, &host(), &CancellationToken::new(), &slot, |l| lines.push(l), |_| {}).await.unwrap();
        assert_eq!(code, 0);
        assert_eq!(lines, vec!["one", "two"]);

        let ct = CancellationToken::new();
        let c2 = ct.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(200)).await;
            c2.cancel();
        });
        let spec = ProcSpec::raw("cmd", "/c ping -n 30 127.0.0.1", EnvMode::Inherit);
        let e = run_streaming(&spec, &host(), &ct, &slot, |_| {}, |_| {}).await.unwrap_err();
        assert!(e.is_canceled());
    }

    #[test]
    fn test_executable_works() {
        let h = host();
        assert!(test_executable(OsStr::new("cmd"), "/c exit 0", &h));
        assert!(!test_executable(OsStr::new("cmd"), "/c exit 1", &h));
        assert!(!test_executable(OsStr::new("nope-not-here"), "--version", &h));
    }

    #[test]
    fn command_line_like_dotnet() {
        let s = ProcSpec::list("ffmpeg", vec!["-i".into(), r"C:\a b.mp4".into()], EnvMode::Inherit);
        assert_eq!(s.command_line(), OsString::from(r#""ffmpeg" -i "C:\a b.mp4""#));
        let r = ProcSpec::raw(r"C:\x\yt-dlp.exe", "", EnvMode::Inherit);
        assert_eq!(r.command_line(), OsString::from(r#""C:\x\yt-dlp.exe""#));
    }
}
