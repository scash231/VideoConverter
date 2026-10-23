#!/usr/bin/env python3
"""
VideoConverter Backend - FastAPI Server (v3)
Supports Instagram, TikTok and YouTube downloads + optional YouTube upload.

Improvements:
 - Modern lifespan handler (replaces deprecated on_event)
 - Dedicated ThreadPoolExecutor for job execution
 - Synchronous job runner (avoids async/executor ping-pong overhead)
 - Faster SSE polling interval (0.15 s)
 - Version 27H1 Canary
"""
from __future__ import annotations
import subprocess, sys

if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

_DEPS = {
    "fastapi": "fastapi",
    "uvicorn": "uvicorn[standard]",
    "yt_dlp":  "yt-dlp",
    "mutagen": "mutagen",
}
for _mod, _pkg in _DEPS.items():
    try:
        __import__(_mod.split(".")[0])
    except ImportError:
        subprocess.check_call([sys.executable, "-m", "pip", "install", _pkg, "-q"])

import asyncio, json, os, shutil, tempfile, threading, time, traceback, uuid
from contextlib import asynccontextmanager
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Optional
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field
import uvicorn

from ffmpeg_manager import ensure_ffmpeg
from downloader    import download_video, fetch_metadata, fetch_playlist_entries
from ytmusic_downloader import download_ytmusic_audio, fetch_ytmusic_metadata, is_ytmusic_url
from transformer   import transform_video
from uploader      import upload_to_youtube
from subtitler     import generate_subtitles

TEMP_ROOT = os.path.join(tempfile.gettempdir(), "VideoConverter")


def _cleanup_stale_temp():
    """Clean up any leftover temporary files or job folders from previous runs."""
    try:
        jobs_dir = os.path.join(TEMP_ROOT, "jobs")
        if os.path.isdir(jobs_dir):
            shutil.rmtree(jobs_dir, ignore_errors=True)
        temp_dir = os.path.join(TEMP_ROOT, "temp")
        if os.path.isdir(temp_dir):
            for item in os.listdir(temp_dir):
                p = os.path.join(temp_dir, item)
                try:
                    if os.path.isfile(p) or os.path.islink(p):
                        os.remove(p)
                    elif os.path.isdir(p):
                        shutil.rmtree(p, ignore_errors=True)
                except Exception:
                    pass
    except Exception as exc:
        print(f"[backend] Stale temp cleanup: {exc}", flush=True)


_MAX_WORKERS = min(8, (os.cpu_count() or 4) + 2)
_pool = ThreadPoolExecutor(max_workers=_MAX_WORKERS)

_jobs: dict[str, dict[str, Any]] = {}
_cancel_events: dict[str, threading.Event] = {}
_JOB_MAX_AGE = 3600  # seconds — purge completed jobs older than 1 hour


def _purge_stale_jobs() -> None:
    """Remove completed/errored jobs older than _JOB_MAX_AGE to prevent unbounded memory growth."""
    now = time.time()
    stale = [
        jid for jid, job in _jobs.items()
        if job.get("status") in ("completed", "error")
        and now - job.get("_finished_at", now) > _JOB_MAX_AGE
    ]
    for jid in stale:
        _jobs.pop(jid, None)
        _cancel_events.pop(jid, None)


# -- Lifespan -----------------------------------------------------------------

@asynccontextmanager
async def _lifespan(app: FastAPI):
    print(f"[backend] Python {sys.version}", flush=True)
    print(f"[backend] executable: {sys.executable}", flush=True)
    import yt_dlp
    print(f"[backend] yt-dlp {yt_dlp.version.__version__}", flush=True)
    print(f"[backend] ffmpeg on PATH: {shutil.which('ffmpeg') is not None}", flush=True)
    print(f"[backend] node on PATH: {shutil.which('node') is not None}", flush=True)
    _cleanup_stale_temp()
    await asyncio.to_thread(ensure_ffmpeg)
    print(f"[backend] ffmpeg ready: {shutil.which('ffmpeg') is not None}", flush=True)
    yield
    _pool.shutdown(wait=False)
    _cleanup_stale_temp()


app = FastAPI(title="VideoConverter API", version="27H1 Canary", lifespan=_lifespan)
app.add_middleware(
    CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"],
)


# -- Models --------------------------------------------------------------------

class MetadataReq(BaseModel):
    url: str

class PlaylistReq(BaseModel):
    url: str
    max_items: int = 100

class SubtitleReq(BaseModel):
    video_path:  str
    model_size:  str = "base"
    language:    Optional[str] = None

class JobReq(BaseModel):
    url:                str
    mode:               str   # "upload" | "download"
    platform:           str   # "instagram" | "tiktok" | "youtube"
    title:              Optional[str] = None
    description:        Optional[str] = None
    tags:               Optional[list[str]] = Field(default_factory=list)
    output_dir:         Optional[str] = None
    privacy:            Optional[str] = "public"
    fingerprint:        Optional[bool] = True
    fingerprint_method: Optional[str] = "standard"
    use_gpu:            Optional[bool] = False
    quality:            Optional[str] = "best"


# -- Endpoints -----------------------------------------------------------------

@app.get("/api/health")
async def health():
    import yt_dlp
    return {
        "status": "ok",
        "version": "27H1 Canary",
        "python": sys.version.split()[0],
        "yt_dlp": yt_dlp.version.__version__,
        "ffmpeg": shutil.which("ffmpeg") is not None,
        "node": shutil.which("node") is not None,
        "deno": shutil.which("deno") is not None,
    }

@app.post("/api/metadata")
async def api_metadata(req: MetadataReq):
    try:
        if is_ytmusic_url(req.url):
            return await asyncio.to_thread(fetch_ytmusic_metadata, req.url)
        return await asyncio.to_thread(fetch_metadata, req.url)
    except Exception as exc:
        raise HTTPException(400, str(exc))

@app.post("/api/ytmusic/metadata")
async def api_ytmusic_metadata(req: MetadataReq):
    try:
        return await asyncio.to_thread(fetch_ytmusic_metadata, req.url)
    except Exception as exc:
        raise HTTPException(400, str(exc))

@app.post("/api/playlist")
async def api_playlist(req: PlaylistReq):
    try:
        return await asyncio.to_thread(fetch_playlist_entries, req.url, req.max_items)
    except Exception as exc:
        raise HTTPException(400, str(exc))

@app.post("/api/subtitles")
async def api_subtitles(req: SubtitleReq):
    try:
        srt = await asyncio.to_thread(
            generate_subtitles, req.video_path, req.model_size, req.language
        )
        return {"srt": srt}
    except Exception as exc:
        raise HTTPException(500, str(exc))

@app.post("/api/jobs")
async def create_job(req: JobReq):
    _purge_stale_jobs()
    jid = str(uuid.uuid4())
    _jobs[jid] = dict(
        id=jid, status="pending", progress=0,
        message="Warte auf Start...", result=None, error=None, eta=None,
    )
    # Create the cancel event before queueing, so a cancel request that arrives while the job
    # still waits for a free worker is not lost.
    _cancel_events[jid] = threading.Event()
    _pool.submit(_run_job_sync, jid, req)
    return {"job_id": jid}

@app.get("/api/jobs/{jid}")
async def get_job(jid: str):
    if jid not in _jobs:
        raise HTTPException(404)
    return _jobs[jid]

@app.post("/api/jobs/{jid}/cancel")
async def cancel_job(jid: str):
    if jid not in _jobs:
        raise HTTPException(404)
    job = _jobs[jid]
    if job["status"] in ("completed", "error"):
        return {"status": job["status"]}
    evt = _cancel_events.get(jid)
    if evt:
        evt.set()
    return {"status": "cancelling"}

@app.get("/api/jobs/{jid}/stream")
async def stream_job(jid: str):
    if jid not in _jobs:
        raise HTTPException(404)

    async def _gen():
        while True:
            job = _jobs.get(jid, {})
            yield f"data: {json.dumps(job)}\n\n"
            if job.get("status") in ("completed", "error"):
                break
            await asyncio.sleep(0.15)

    return StreamingResponse(
        _gen(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


# -- Synchronous job runner (runs in thread pool) ------------------------------

_AUDIO_QUALITIES = ("audio", "mp3", "m4a", "flac")


def _run_job_sync(jid: str, req: JobReq):
    cancel_evt = _cancel_events.setdefault(jid, threading.Event())

    def _cancelled() -> bool:
        return cancel_evt.is_set()

    def up(msg: str, pct: float = -1.0):
        _jobs[jid]["message"] = msg
        if pct >= 0:
            _jobs[jid]["progress"] = pct

    job_temp_dir: Optional[str] = None
    path: Optional[str] = None
    is_upload = req.mode == "upload"

    try:
        _jobs[jid]["status"] = "running"
        is_ytmusic = (
            is_ytmusic_url(req.url) or 
            req.platform.lower() == "ytmusic" or 
            (req.quality or "").startswith("ytmusic")
        )
        is_audio_mode = (req.quality in _AUDIO_QUALITIES) or is_ytmusic
        has_fp = bool(req.fingerprint) and not is_audio_mode

        # Count total active phases for clear step-by-step progress messaging
        total_phases = 1
        if is_upload:
            total_phases += 1
        if has_fp:
            total_phases += 1

        current_phase = 1
        phase_prefix = f"[{current_phase}/{total_phases}] " if total_phases > 1 else ""

        if is_ytmusic:
            initial_msg = "Lade Musik herunter..."
        elif is_audio_mode:
            initial_msg = "Lade Audio herunter..."
        else:
            initial_msg = "Lade Video herunter..."
        up(f"{phase_prefix}{initial_msg}", 0)

        if is_upload:
            job_temp_dir = os.path.join(TEMP_ROOT, "jobs", jid)
            os.makedirs(job_temp_dir, exist_ok=True)
            dl_dir = job_temp_dir
        else:
            user_dir = (req.output_dir.strip()
                        if req.output_dir and req.output_dir.strip()
                        else os.path.join(TEMP_ROOT, "downloads"))
            dl_dir = os.path.abspath(os.path.normpath(user_dir))
            os.makedirs(dl_dir, exist_ok=True)

        print(f"[job {jid[:8]}] url={req.url!r}  dl_dir={dl_dir!r}  python={sys.version.split()[0]}", flush=True)

        current_download_index = [1]
        last_filename = [None]

        def _hook(d: dict):
            try:
                fn = d.get("filename") or d.get("tmpfilename")
                if fn and last_filename[0] and fn != last_filename[0]:
                    current_download_index[0] += 1
                if fn:
                    last_filename[0] = fn

                status = d.get("status")
                if status == "downloading":
                    dl  = d.get("downloaded_bytes") or 0
                    tot = d.get("total_bytes") or d.get("total_bytes_estimate") or 1
                    pct = round(min(100.0, (dl / max(1, tot)) * 100.0), 1)
                    raw_eta = d.get("eta")
                    eta = None
                    if raw_eta is not None:
                        try:
                            eta = int(round(float(raw_eta)))
                        except (ValueError, TypeError):
                            eta = None
                    speed = d.get("speed")

                    speed_str = None
                    if speed and speed > 0:
                        speed_mb = speed / 1_048_576.0
                        if speed_mb >= 1.0:
                            speed_str = f"{speed_mb:.1f} MB/s"
                        else:
                            speed_kb = speed / 1024.0
                            speed_str = f"{speed_kb:.0f} KB/s"

                    idx = current_download_index[0]
                    if is_ytmusic:
                        sub_phase = "Download Music"
                    elif is_audio_mode:
                        sub_phase = "Download Audio"
                    elif idx == 1:
                        sub_phase = "Download Video"
                    elif idx == 2:
                        sub_phase = "Download Audio"
                    else:
                        sub_phase = "Merging"

                    _jobs[jid]["eta"] = eta
                    _jobs[jid]["speed"] = speed_str
                    up(f"{phase_prefix}{sub_phase}: {dl / 1_048_576:.1f} / {tot / 1_048_576:.1f} MB", pct)
                elif status == "finished":
                    _jobs[jid]["eta"] = None
                    _jobs[jid]["speed"] = None
                    if not is_audio_mode and current_download_index[0] >= 2:
                        up(f"{phase_prefix}Merging", 100)
            except Exception as hook_exc:
                # Never let a progress-hook crash kill the download
                print(f"[backend] _hook error (ignored): {hook_exc}", flush=True)

        if is_ytmusic:
            path = download_ytmusic_audio(
                req.url, dl_dir, _hook, _cancelled,
                quality=req.quality or "ytmusic_320k"
            )
        else:
            path = download_video(req.url, dl_dir, _hook, _cancelled,
                                 quality=req.quality or "best")
        if _cancelled():
            raise RuntimeError("Abgebrochen")
        if not path:
            raise RuntimeError("Keine Datei nach Download gefunden.")

        # -- Fingerprint bypass (Video only) -----------------------------------
        if has_fp:
            current_phase += 1
            phase_prefix = f"[{current_phase}/{total_phases}] " if total_phases > 1 else ""
            up(f"{phase_prefix}Fingerprint-Bypass wird angewendet...", 0)

            def _fp_progress(frac: float):
                pct = min(100, int(frac * 100))
                up(f"{phase_prefix}Fingerprint-Bypass: {pct}%", pct)

            path = transform_video(
                path,
                req.fingerprint_method or "standard",
                _fp_progress,
                req.use_gpu or False,
                _cancelled,
            )
            if _cancelled():
                raise RuntimeError("Abgebrochen")
            up(f"{phase_prefix}Fingerprint-Bypass abgeschlossen.", 100)

        # -- YouTube upload ----------------------------------------------------
        if is_upload:
            current_phase += 1
            phase_prefix = f"[{current_phase}/{total_phases}] " if total_phases > 1 else ""
            up(f"{phase_prefix}YouTube-Upload laeuft...", 0)

            def _up_cb(p: int):
                pct = min(100, int(p))
                up(f"{phase_prefix}YouTube Upload: {pct}%", pct)

            vid = upload_to_youtube(
                path,
                req.title or "Video",
                req.description or "",
                req.tags or [],
                req.privacy or "public",
                _up_cb,
            )
            if os.path.exists(path):
                try:
                    os.remove(path)
                except Exception:
                    pass
            _jobs[jid]["result"] = {
                "video_id": vid,
                "url": f"https://youtube.com/shorts/{vid}",
            }
        else:
            _jobs[jid]["result"] = {"file_path": path}

        _jobs[jid].update(
            status="completed", progress=100,
            message="Erfolgreich abgeschlossen!",
            _finished_at=time.time(),
        )
    except Exception as exc:
        if cancel_evt.is_set():
            # _finished_at is required, otherwise cancelled jobs are never purged
            _jobs[jid].update(status="error", error="Abgebrochen",
                              message="Abgebrochen", _finished_at=time.time())
        else:
            tb = traceback.format_exc()
            print(tb, flush=True)
            detail = f"{type(exc).__name__}: {exc}"
            _jobs[jid].update(status="error", error=detail,
                              message=f"Fehler: {detail}",
                              traceback=tb,
                              _finished_at=time.time())
    finally:
        _cancel_events.pop(jid, None)
        # Clean up job sandbox if in upload mode
        if job_temp_dir and os.path.isdir(job_temp_dir):
            for _ in range(5):
                try:
                    shutil.rmtree(job_temp_dir, ignore_errors=False)
                    break
                except Exception:
                    time.sleep(0.05)
        # For download mode, if job was cancelled or errored, ensure no partial output file remains
        if not is_upload and (cancel_evt.is_set() or _jobs[jid].get("status") == "error"):
            if path and os.path.isfile(path):
                for _ in range(5):
                    try:
                        os.remove(path)
                        break
                    except Exception:
                        time.sleep(0.05)


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8765, log_level="warning")
