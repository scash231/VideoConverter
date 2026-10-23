//! Port of `ExecuteJobPipelineAsync`, plus Ferrite's YouTube upload mode.

use std::ffi::{OsStr, OsString};
use std::sync::Arc;

use crate::cloud::{self, provider_display_name, ProgressFn};
use crate::download::{download_video_with_retries, Phase};
use crate::dotnet::fmt::fixed;
use crate::dotnet::fs::{create_directory, delete_directory_recursive, delete_file, directory_exists, file_exists, move_file};
use crate::dotnet::ioerr::NetError;
use crate::dotnet::netstr::wide;
use crate::dotnet::path::{combine, combine_all, get_extension, get_file_name_without_extension, get_full_path, get_temp_path};
use crate::dotnet::text::{contains_wide, eq_ignore_case_opt, is_blank_os, starts_with_ignore_case};
use crate::dotnet::win::{get_folder_path, KnownFolder};
use crate::engine::{Engine, JobRequest};
use crate::fingerprint::apply_fingerprint;
use crate::jobs::{complete, emit, emit_status, JobContext, StatusExtra};
use crate::models::ResultValue;
use crate::ncat;
use crate::util::{clean_stem_files, clean_url, guid_prefix, is_yt_music_url, safe_delete, sanitize_filename};
use crate::youtube::{credentials_dir, shorts_description, upload::upload_video, upload::UploadCallbacks, video_resource};

fn is_audio_quality(q: Option<&str>) -> bool {
    ["audio", "mp3", "m4a", "flac"].iter().any(|a| eq_ignore_case_opt(q, a))
}

/// `%TEMP%\ReelsConverter\jobs\{jobId}` (Native keeps the old product name here).
fn temp_job_dir(job_id: &str) -> OsString {
    combine_all(&[&get_temp_path(), OsStr::new("ReelsConverter"), OsStr::new("jobs"), OsStr::new(job_id)])
}

pub async fn execute_job_pipeline(engine: &Engine, ctx: &Arc<JobContext>, req: JobRequest) {
    let ct = ctx.cts.clone();
    let mut download_dir: Option<OsString> = None;
    let mut raw_stem: Option<String> = None;

    let outcome = run(engine, ctx, &req, &ct, &mut download_dir, &mut raw_stem).await;
    match outcome {
        Ok(()) => {}
        Err(e) if e.is_canceled() => {
            emit(engine, ctx, "cancelled", 0.0, "Job was cancelled by user.");
            complete(engine, ctx);
            clean_stem_files(download_dir.as_deref(), raw_stem.as_deref(), None).await;
        }
        Err(e) => {
            emit_status(
                engine,
                ctx,
                "error",
                0.0,
                ncat!("Error: ", &e.message),
                StatusExtra { error: Some(e.message.clone()), ..Default::default() },
            );
            complete(engine, ctx);
            clean_stem_files(download_dir.as_deref(), raw_stem.as_deref(), None).await;
        }
    }
    ctx.running.set(None);
}

async fn run(
    engine: &Engine,
    ctx: &Arc<JobContext>,
    req: &JobRequest,
    ct: &tokio_util::sync::CancellationToken,
    download_dir_out: &mut Option<OsString>,
    raw_stem_out: &mut Option<String>,
) -> Result<(), NetError> {
    let clean = clean_url(req.url.as_deref());
    let is_upload = eq_ignore_case_opt(req.mode.as_deref(), "upload");
    let quality = req.quality.as_deref();
    let is_yt_music = is_yt_music_url(&clean)
        || eq_ignore_case_opt(req.platform.as_deref(), "ytmusic")
        || starts_with_ignore_case(quality.unwrap_or(""), "ytmusic");
    let is_audio_mode = is_audio_quality(quality) || is_yt_music;
    let has_fp = req.fingerprint && !is_audio_mode;

    // ── Compute total phases ─────────────────────────────────────────
    let mut total = 1;
    if has_fp {
        total += 1;
    }
    if req.cloud_upload || is_upload {
        total += 1;
    }
    let phase = Phase::new(total);

    // ── Ensure FFmpeg binaries are ready (auto-prompt if missing) ───
    engine.ensure_initialized(Some(ct)).await?;
    ensure_ffmpeg_ready(engine).await;

    // ── Determine download directory ────────────────────────────────
    let download_dir: OsString = if is_upload || (req.cloud_upload && req.cloud_temp_only) {
        temp_job_dir(&ctx.job_id)
    } else {
        match req.output_dir.as_deref().filter(|d| !is_blank_os(d)) {
            None => combine(get_folder_path(KnownFolder::UserProfile), "Downloads"),
            Some(d) => get_full_path(d)?,
        }
    };
    *download_dir_out = Some(download_dir.clone());
    create_directory(&download_dir)?;

    let raw_stem = guid_prefix(12);
    *raw_stem_out = Some(raw_stem.clone());

    // ── Download ────────────────────────────────────────────────────
    let downloaded = if is_yt_music {
        emit(engine, ctx, "downloading", 0.0, format!("{}Downloading music...", phase.prefix()));
        crate::ytmusic::download_ytmusic(engine, ctx, &clean, &download_dir, &raw_stem, quality.unwrap_or("ytmusic_320k"), &phase, ct).await?
    } else {
        let initial = if is_audio_mode { "Downloading audio..." } else { "Downloading video..." };
        emit(engine, ctx, "downloading", 0.0, format!("{}{initial}", phase.prefix()));
        download_video_with_retries(engine, ctx, &clean, &download_dir, &raw_stem, quality.unwrap_or("best"), is_audio_mode, &phase, ct).await?
    };

    if ct.is_cancelled() {
        return Err(NetError::canceled());
    }
    let downloaded = match downloaded {
        Some(f) if file_exists(&f) => f,
        _ => return Err(NetError::new(crate::dotnet::ioerr::NetErrorKind::FileNotFound, "Downloaded media file could not be located.")),
    };
    let mut final_file = downloaded.clone();

    // ── Fingerprint bypass ──────────────────────────────────────────
    if has_fp {
        phase.advance();
        emit(engine, ctx, "processing", 50.0, format!("{}Applying fingerprint bypass...", phase.prefix()));
        final_file = apply_fingerprint(engine, ctx, &downloaded, req.fingerprint_method.as_deref(), req.use_gpu, &phase, ct).await?;
        if final_file != downloaded && file_exists(&final_file) {
            safe_delete(Some(&downloaded)).await;
        }
    }

    if is_upload {
        return youtube_upload(engine, ctx, req, &phase, &download_dir, &raw_stem, &final_file, ct).await;
    }

    // ── Rename to safe title ────────────────────────────────────────
    if let Some(title) = req.title.as_deref().filter(|t| !is_blank_os(t)) {
        let ext = get_extension(&final_file);
        let mut safe = ncat!(sanitize_filename(title), &ext);
        let mut target = combine(&download_dir, &safe);
        let mut n = 1;
        while file_exists(&target) && wide(&target) != wide(&final_file) {
            safe = ncat!(sanitize_filename(title), format!(" ({n})"), &ext);
            target = combine(&download_dir, &safe);
            n += 1;
        }
        if move_file(&final_file, &target, false).is_ok() {
            final_file = target;
        }
    }

    // Clean up leftover temp artifacts
    clean_stem_files(Some(&download_dir), Some(&raw_stem), Some(&final_file)).await;

    let title_value = match req.title.as_ref() {
        Some(t) => t.clone(),
        None => get_file_name_without_extension(&final_file),
    };
    let mut result: Vec<(String, ResultValue)> = vec![
        ("file_path".into(), ResultValue::Str(final_file.clone())),
        ("title".into(), ResultValue::Str(title_value)),
    ];

    // ── Cloud Upload ────────────────────────────────────────────────
    if req.cloud_upload {
        phase.advance();
        let settings = engine.settings().await;
        let key: Option<String> = req.cloud_provider.clone().or_else(|| settings.cloud_provider.clone());
        let display = provider_display_name(key.as_deref());
        emit(engine, ctx, "uploading", 0.0, format!("{}Uploading to {display}...", phase.prefix()));

        let (e2, c2, p2) = (engine.clone(), ctx.clone(), phase.prefix());
        let progress: ProgressFn = Arc::new(move |pct: f64, msg: String| {
            emit(&e2, &c2, "uploading", pct, format!("{p2}{msg}"));
        });
        let upload = cloud::upload(&engine.0.cloud_http, &final_file, key.as_deref(), &settings, progress, ct).await;

        if !upload.success {
            let msg = upload.error_message.unwrap_or_else(|| format!("Failed to upload to {display}").into());
            return Err(NetError::generic(msg));
        }

        result.push(("cloud_provider".into(), ResultValue::Str(display.into())));
        let destination: OsString = upload
            .destination_path
            .clone()
            .or_else(|| upload.url.clone().map(OsString::from))
            .unwrap_or_else(|| display.into());
        result.push(("cloud_destination".into(), ResultValue::Str(destination)));
        if let Some(u) = upload.url.as_ref().filter(|u| !u.is_empty()) {
            result.push(("cloud_url".into(), ResultValue::Str(u.into())));
        }

        if req.cloud_temp_only {
            result.push(("is_temp".into(), ResultValue::Bool(true)));
            // One try block in Native: a failed file delete skips the directory delete.
            let file_deleted = !file_exists(&final_file) || delete_file(&final_file).is_ok();
            if file_deleted && contains_wide(&wide(&download_dir), "ReelsConverter") && directory_exists(&download_dir) {
                let _ = delete_directory_recursive(&download_dir);
            }
        }

        let message = upload.message.unwrap_or_else(|| format!("Successfully uploaded to {display}!").into());
        emit_status(engine, ctx, "completed", 100.0, message, StatusExtra { result: Some(result), ..Default::default() });
        complete(engine, ctx);
        return Ok(());
    }

    emit_status(
        engine,
        ctx,
        "completed",
        100.0,
        "Download and processing completed successfully.".into(),
        StatusExtra { result: Some(result), ..Default::default() },
    );
    complete(engine, ctx);
    Ok(())
}

/// `EnsureFfmpegReadyAsync()`; the result is ignored by the pipeline, like in Native.
async fn ensure_ffmpeg_ready(engine: &Engine) -> bool {
    let b = engine.bins();
    let ffmpeg_file = b.ffmpeg_path.as_ref().is_some_and(file_exists);
    let ffprobe_file = b.ffprobe_path.as_ref().is_some_and(|p| !p.is_empty() && file_exists(p));
    if ffmpeg_file && ffprobe_file {
        return true;
    }
    let exe = b.ffmpeg();
    let info = engine.info().clone();
    let works = tokio::task::spawn_blocking(move || crate::proc::runner::test_executable(&exe, "-version", &info)).await.unwrap_or(false);
    if works {
        return true;
    }
    if engine.host().ensure_ffmpeg().await {
        engine.re_resolve_binaries().await;
        return true;
    }
    false
}

/// Ferrite's upload step: sign in if needed, upload, delete the temporary download.
#[allow(clippy::too_many_arguments)]
async fn youtube_upload(
    engine: &Engine,
    ctx: &Arc<JobContext>,
    req: &JobRequest,
    phase: &Phase,
    download_dir: &OsStr,
    raw_stem: &str,
    final_file: &OsStr,
    ct: &tokio_util::sync::CancellationToken,
) -> Result<(), NetError> {
    phase.advance();
    let prefix = phase.prefix();
    emit(engine, ctx, "uploading", 0.0, format!("{prefix}Uploading to YouTube..."));

    let settings = engine.settings().await;
    let title = req
        .title
        .as_ref()
        .map(|t| t.to_string_lossy().into_owned())
        .filter(|t| !t.is_empty())
        .unwrap_or_else(|| "Video".into());
    let description = req.description.as_ref().map(|d| d.to_string_lossy().into_owned()).unwrap_or_default();
    let tags: Vec<String> = req.tags.as_ref().map(|t| t.iter().map(|x| x.to_string_lossy().into_owned()).collect()).unwrap_or_default();
    let privacy = req.privacy.clone().filter(|p| !p.is_empty()).unwrap_or_else(|| "public".into());
    let resource = video_resource(&title, &shorts_description(&description, settings.auto_add_shorts_hashtag), &tags, &privacy);

    let (e1, c1, p1) = (engine.clone(), ctx.clone(), prefix.clone());
    let (e2, c2, p2) = (engine.clone(), ctx.clone(), prefix.clone());
    let e3 = engine.clone();
    let culture = engine.culture().clone();
    let callbacks = UploadCallbacks {
        progress: Arc::new(move |pct| {
            emit(&e1, &c1, "uploading", pct, format!("{p1}Uploading to YouTube... {}%", fixed(pct, 0, &culture)));
        }),
        status: Arc::new(move |msg| emit(&e2, &c2, "uploading", 0.0, format!("{p2}{msg}"))),
        log: Arc::new(move |line| e3.emit_output(line)),
    };
    let dir = credentials_dir(engine.info());
    let video_id = upload_video(&engine.0.cloud_http, &engine.0.youtube_auth, &dir, final_file, &resource, &callbacks, ct).await?;

    let url = format!("https://youtube.com/shorts/{video_id}");
    if file_exists(final_file) {
        let _ = delete_file(final_file);
    }
    clean_stem_files(Some(download_dir), Some(raw_stem), None).await;
    if contains_wide(&wide(download_dir), "ReelsConverter") && directory_exists(download_dir) {
        let _ = delete_directory_recursive(download_dir);
    }
    let result = vec![
        ("video_id".into(), ResultValue::Str(video_id.clone().into())),
        ("url".into(), ResultValue::Str(url.clone().into())),
        ("title".into(), ResultValue::Str(title.into())),
        ("cloud_provider".into(), ResultValue::Str("YouTube".into())),
        ("cloud_destination".into(), ResultValue::Str(url.clone().into())),
        ("cloud_url".into(), ResultValue::Str(url.clone().into())),
    ];
    emit_status(
        engine,
        ctx,
        "completed",
        100.0,
        format!("Uploaded to YouTube: {url}").into(),
        StatusExtra { result: Some(result), ..Default::default() },
    );
    complete(engine, ctx);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn audio_modes() {
        assert!(is_audio_quality(Some("MP3")));
        assert!(is_audio_quality(Some("flac")));
        assert!(!is_audio_quality(Some("1080p")));
        assert!(!is_audio_quality(None));
        assert!(temp_job_dir("abcd1234").to_string_lossy().ends_with(r"ReelsConverter\jobs\abcd1234"));
    }
}
