"""FFmpeg fingerprint-bypass transformation - speed-optimized.

Key optimisations vs. the original implementation:
 - Much faster x264 presets  (slow -> veryfast / fast / medium)
 - Higher CRF values         (still excellent for social-media shorts)
 - fast_bilinear scaling      (~2x faster than default bicubic)
 - Audio stream-copy for the 'light' method (no re-encode)
 - Lower AAC bitrate for other methods (128 k - plenty for Reels)
 - CREATE_NO_WINDOW on Windows to avoid console flash
 - Factored-out probe helper for clarity
 - Multi-GPU encoder support: NVENC > Intel QSV > AMD AMF (auto-detected)
 - Hardware-accelerated decode even in CPU-encode mode (D3D11VA on Windows)
"""
from __future__ import annotations

import json, os, subprocess, sys
from functools import lru_cache
from typing import Callable

from downloader import _safe_remove

_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# -- Method presets ------------------------------------------------------------
# (scale_factor, vf_extra, af_filter, crf, preset, copy_audio)
_METHODS: dict[str, tuple] = {
    "light": (
        1.003, None, None,
        23, "veryfast", True,
    ),
    "standard": (
        1.005,
        "eq=brightness=0.006:saturation=1.01",
        "atempo=1.002",
        21, "fast", False,
    ),
    "strong": (
        1.010,
        "eq=brightness=0.012:saturation=1.02:contrast=1.01,hue=h=1.5",
        "atempo=1.005",
        19, "medium", False,
    ),
}

# (encoder) -> (cpu_preset -> hw_preset)
_HW_PRESETS: dict[str, dict[str, str]] = {
    "h264_nvenc": {"veryfast": "p1", "fast": "p4", "medium": "p5"},
    "h264_qsv":   {"veryfast": "veryfast", "fast": "fast", "medium": "medium"},
    "h264_amf":   {"veryfast": "speed", "fast": "balanced", "medium": "quality"},
}

# encoder -> hwaccel flag for decoding
_HW_ACCEL: dict[str, str] = {
    "h264_nvenc": "cuda",
    "h264_qsv":   "qsv",
    "h264_amf":   "d3d11va",
}


# -- Helpers -------------------------------------------------------------------

@lru_cache(maxsize=1)
def _best_hw_encoder() -> tuple[str, str] | None:
    """Return (encoder, hwaccel) for the best *working* HW encoder, or None.

    Priority: NVENC (NVIDIA) > QSV (Intel) > AMF (AMD). Full FFmpeg builds list all three
    encoders even without the matching GPU, so each one is verified with a tiny test encode.
    """
    for enc in ("h264_nvenc", "h264_qsv", "h264_amf"):
        try:
            r = subprocess.run(
                ["ffmpeg", "-hide_banner", "-loglevel", "error",
                 "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
                 "-frames:v", "1", "-c:v", enc, "-f", "null", "-"],
                capture_output=True, timeout=15, creationflags=_NO_WINDOW,
            )
            if r.returncode == 0:
                return enc, _HW_ACCEL[enc]
        except Exception:
            continue
    return None


def _probe_video(path: str) -> tuple[int | None, int | None, float]:
    """Return (width, height, duration) of the first video stream."""
    try:
        r = subprocess.run(
            ["ffprobe", "-v", "quiet", "-print_format", "json",
             "-select_streams", "v:0", "-show_streams", "-show_format", path],
            capture_output=True, encoding="utf-8", errors="replace",
            creationflags=_NO_WINDOW,
        )
    except FileNotFoundError:
        return None, None, 0.0
    try:
        data = json.loads(r.stdout or "{}")
    except (json.JSONDecodeError, TypeError, ValueError):
        return None, None, 0.0

    duration = float(data.get("format", {}).get("duration", 0))
    for s in data.get("streams", []):
        if s.get("codec_type") == "video":
            return int(s["width"]), int(s["height"]), duration
    return None, None, duration


def _run_ffmpeg(
    cmd: list[str],
    duration: float,
    progress_cb: Callable[[float], None] | None,
    cancel_check: Callable[[], bool] | None = None,
) -> int:
    """Run ffmpeg with real-time progress. Returns -1 when cancelled."""
    if not progress_cb or duration <= 0:
        proc = subprocess.Popen(
            cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=_NO_WINDOW,
        )
        while True:
            try:
                return proc.wait(timeout=0.2)
            except subprocess.TimeoutExpired:
                if cancel_check and cancel_check():
                    try:
                        proc.kill()
                    except Exception:
                        pass
                    proc.wait()
                    return -1

    proc = subprocess.Popen(
        cmd + ["-progress", "pipe:1", "-nostats"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        encoding="utf-8", errors="replace", creationflags=_NO_WINDOW,
    )

    cancelled = False
    try:
        for line in proc.stdout:  # type: ignore[union-attr]
            if cancel_check and cancel_check():
                try:
                    proc.kill()
                except Exception:
                    pass
                cancelled = True
                break
            if line.startswith("out_time_us="):
                try:
                    us = int(line.split("=", 1)[1])
                    if us > 0:
                        progress_cb(min(us / 1_000_000 / duration, 1.0))
                except ValueError:
                    pass
    finally:
        if proc.stdout:
            try:
                proc.stdout.close()
            except Exception:
                pass

    rc = proc.wait()
    return -1 if cancelled else rc


# -- Public API ----------------------------------------------------------------

def transform_video(
    input_path: str,
    method: str = "standard",
    progress_cb: Callable[[float], None] | None = None,
    use_gpu: bool = False,
    cancel_check: Callable[[], bool] | None = None,
) -> str:
    """Apply fingerprint-bypass transformation and return the output path."""
    out = os.path.splitext(input_path)[0] + "_yt.mp4"

    # -- Metadata-only: stream copy, strip metadata ----------------------------
    if method == "metadata":
        rc = subprocess.run(
            ["ffmpeg", "-y", "-i", input_path,
             "-c", "copy", "-map_metadata", "-1",
             "-movflags", "+faststart", out],
            capture_output=True, encoding="utf-8", errors="replace",
            creationflags=_NO_WINDOW,
        ).returncode
        if rc != 0 or (cancel_check and cancel_check()):
            _safe_remove(out)
            return input_path
        _safe_remove(input_path)
        return out

    # -- Filter-based methods --------------------------------------------------
    if method not in _METHODS:
        method = "standard"

    w, h, duration = _probe_video(input_path)
    if not (w and h):
        return input_path

    scale_factor, vf_extra, af_filter, crf, preset, copy_audio = _METHODS[method]
    sw = (round(w * scale_factor) // 2) * 2
    sh = (round(h * scale_factor) // 2) * 2

    # fast_bilinear is ~2x faster than default bicubic - sufficient for the
    # tiny up-scale we do here.
    vf = f"scale={sw}:{sh}:flags=fast_bilinear,crop={w}:{h}"
    if vf_extra:
        vf += f",{vf_extra}"

    hw_enc = _best_hw_encoder() if use_gpu else None
    encoder = hw_enc[0] if hw_enc else None
    hwaccel = hw_enc[1] if hw_enc else None

    cmd = ["ffmpeg", "-y"]

    if hwaccel:
        # GPU decode via the matched hwaccel backend; frames are transferred
        # to system memory automatically when CPU-side VF filters are applied.
        cmd += ["-hwaccel", hwaccel]
    elif sys.platform == "win32":
        # Even in CPU-encode mode, use D3D11VA for hardware-accelerated decode.
        # This offloads decode from the CPU so libx264 gets more headroom.
        cmd += ["-hwaccel", "d3d11va"]

    cmd += ["-i", input_path, "-vf", vf]

    if af_filter:
        cmd += ["-af", af_filter]

    # -- Encoder ---------------------------------------------------------------
    if encoder == "h264_nvenc":
        cmd += [
            "-c:v", "h264_nvenc",
            "-rc", "vbr", "-cq", str(crf),
            "-preset", _HW_PRESETS["h264_nvenc"].get(preset, "p4"),
            "-bf", "2", "-b_ref_mode", "middle",
            "-rc-lookahead", "20",
        ]
    elif encoder == "h264_qsv":
        cmd += [
            "-c:v", "h264_qsv",
            "-global_quality", str(crf),
            "-preset", _HW_PRESETS["h264_qsv"].get(preset, "fast"),
        ]
    elif encoder == "h264_amf":
        cmd += [
            "-c:v", "h264_amf",
            "-qp_i", str(crf), "-qp_p", str(crf),
            "-quality", _HW_PRESETS["h264_amf"].get(preset, "balanced"),
        ]
    else:
        cmd += [
            "-c:v", "libx264",
            "-crf", str(crf),
            "-preset", preset,
            "-pix_fmt", "yuv420p",
        ]

    # Audio: stream-copy when untouched, fast AAC otherwise
    if copy_audio:
        cmd += ["-c:a", "copy"]
    else:
        cmd += ["-c:a", "aac", "-b:a", "128k"]

    cmd += [
        "-threads", "0",
        "-map_metadata", "-1",
        "-movflags", "+faststart",
        out,
    ]

    rc = _run_ffmpeg(cmd, duration, progress_cb, cancel_check)
    if rc != 0 and encoder and not (cancel_check and cancel_check()):
        # GPU encode failed (driver/resolution limits) – retry once on the CPU
        _safe_remove(out)
        return transform_video(input_path, method, progress_cb, False, cancel_check)
    if rc != 0 or (cancel_check and cancel_check()):
        _safe_remove(out)
        return input_path

    _safe_remove(input_path)
    return out
