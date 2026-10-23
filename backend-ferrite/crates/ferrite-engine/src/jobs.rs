//! Port of `JobContext`, `_jobs`, `EmitStatus`, `CancelJobAsync`, `Dispose` and
//! `ForgetJobLaterAsync`. The job channel itself lives in the host (the C# side keeps a
//! `Channel<JobStatus>` per job); this side reports every status and the completion.

use std::ffi::{OsStr, OsString};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tokio_util::sync::CancellationToken;

use crate::dotnet::text::to_upper_invariant;
use crate::engine::{Engine, JobRequest};
use crate::models::{JobStatus, ResultValue};
use crate::ncat;
use crate::proc::progress::phase_key;
use crate::proc::runner::RunningSlot;
use crate::util::guid_prefix;

pub struct JobContext {
    pub job_id: String,
    pub cts: CancellationToken,
    pub running: RunningSlot,
    last_logged_phase: Mutex<Option<String>>,
}

impl JobContext {
    fn new(job_id: String) -> Self {
        JobContext {
            job_id,
            cts: CancellationToken::new(),
            running: RunningSlot::default(),
            last_logged_phase: Mutex::new(None),
        }
    }
}

/// Optional parts of an `EmitStatus` call.
#[derive(Default)]
pub struct StatusExtra {
    pub speed: Option<String>,
    pub eta: Option<i32>,
    pub result: Option<Vec<(String, ResultValue)>>,
    pub error: Option<OsString>,
}

/// `EmitStatus(ctx, status, progress, message, speed, eta, result, error)`.
pub fn emit_status(engine: &Engine, ctx: &JobContext, status: &str, progress: f64, message: OsString, extra: StatusExtra) {
    let s = JobStatus {
        id: ctx.job_id.clone(),
        status: status.to_owned(),
        progress: progress.clamp(0.0, 100.0),
        message: message.clone(),
        error: extra.error,
        eta: extra.eta,
        speed: extra.speed,
        result: extra.result,
    };
    engine.host().job_status(&s);

    let phase = phase_key(status, &message.to_string_lossy());
    let mut last = ctx.last_logged_phase.lock().unwrap_or_else(|e| e.into_inner());
    if last.as_deref() != Some(phase.as_str()) {
        *last = Some(phase);
        drop(last);
        engine.emit_output(ncat!("[", to_upper_invariant(status), "] ", &message));
    }
}

/// Shorthand for statuses without speed/eta/result/error.
pub fn emit(engine: &Engine, ctx: &JobContext, status: &str, progress: f64, message: impl AsRef<OsStr>) {
    emit_status(engine, ctx, status, progress, message.as_ref().to_owned(), StatusExtra::default());
}

/// `ctx.Channel.Writer.TryComplete()`.
pub fn complete(engine: &Engine, ctx: &JobContext) {
    engine.host().job_complete(&ctx.job_id);
}

pub fn create_job(engine: &Engine, req: JobRequest, registered: impl FnOnce(&str)) -> String {
    let job_id = guid_prefix(8);
    let ctx = Arc::new(JobContext::new(job_id.clone()));
    engine.0.jobs.lock().unwrap_or_else(|e| e.into_inner()).insert(job_id.clone(), ctx.clone());
    registered(&job_id);
    let e = engine.clone();
    engine.0.active_pipelines.fetch_add(1, Ordering::SeqCst);
    tokio::spawn(async move {
        crate::pipeline::execute_job_pipeline(&e, &ctx, req).await;
        e.0.active_pipelines.fetch_sub(1, Ordering::SeqCst);
        // ForgetJobLaterAsync
        let id = ctx.job_id.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_secs(120)).await;
            e.0.jobs.lock().unwrap_or_else(|p| p.into_inner()).remove(&id);
        });
    });
    job_id
}

fn get(engine: &Engine, job_id: &str) -> Option<Arc<JobContext>> {
    engine.0.jobs.lock().unwrap_or_else(|e| e.into_inner()).get(job_id).cloned()
}

pub fn cancel_job(engine: &Engine, job_id: &str) {
    if let Some(ctx) = get(engine, job_id) {
        ctx.cts.cancel();
        ctx.running.kill();
    }
}

pub fn dispose(engine: &Engine) {
    let all: Vec<Arc<JobContext>> = {
        let mut g = engine.0.jobs.lock().unwrap_or_else(|e| e.into_inner());
        let v = g.values().cloned().collect();
        g.clear();
        v
    };
    for ctx in all {
        ctx.cts.cancel();
        ctx.running.kill();
    }
}

pub fn running_jobs(engine: &Engine) -> usize {
    engine.0.active_pipelines.load(Ordering::SeqCst)
}
