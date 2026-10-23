"""yt-dlp download helpers — stability & performance optimised.

Key features:
 - Adaptive format fallback: DASH → any-codec DASH → progressive-only
 - Exponential backoff between retry attempts (avoids YouTube rate-limits)
 - Modern User-Agent (Chrome 137)
 - Automatic aria2c external downloader when available
 - Robust cookie probing with caching
 - URL sanitisation (invisible Unicode chars from social-media copy-paste)
 - Clean temp-file management with Windows retry logic
"""
from __future__ import annotations

import os
import re
import shutil
import sys
import time
import uuid

import yt_dlp

if sys.platform == "win32":
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


# ---------------------------------------------------------------------------
# Shared helpers (also imported by ytmusic_downloader, subtitler)
# ---------------------------------------------------------------------------

def _safe_print(msg: str) -> None:
    """Print message safely without crashing on unmappable Unicode, directed to stderr so stdout remains clean."""
    try:
        print(msg, file=sys.stderr, flush=True)
    except UnicodeEncodeError:
        try:
            safe = msg.encode("utf-8", errors="replace").decode("utf-8", errors="replace")
            sys.stderr.write(safe + "\n")
            sys.stderr.flush()
        except Exception:
            try:
                sys.stderr.buffer.write((msg + "\n").encode("utf-8", errors="replace"))
                sys.stderr.buffer.flush()
            except Exception:
                pass


class DownloadCancelled(Exception):
    """Raised when a download is cancelled by the user."""


_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/137.0.0.0 Safari/537.36"
)

# Invisible / zero-width Unicode codepoints that browsers and social-media
# apps inject when the user copies a URL.
_INVISIBLE_RE = re.compile(
    "[\u200b\u200c\u200d\u200e\u200f"
    "\u202a\u202b\u202c\u202d\u202e"
    "\u2060\u2066\u2067\u2068\u2069"
    "\ufeff\u00ad\u034f\u061c\u115f\u1160"
    "\u17b4\u17b5\u180e\ufff0-\ufff8\ufffa-\ufffd]"
)


def _clean_url(raw: str) -> str:
    """Strip invisible Unicode chars and whitespace from a pasted URL."""
    return _INVISIBLE_RE.sub("", raw).strip()


def _safe_title(raw: str) -> str:
    """Sanitise a video title into a Windows-safe filename stem."""
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', raw).strip('. ')
    if re.match(r'^(CON|PRN|AUX|NUL|COM\d|LPT\d)$', name, re.IGNORECASE):
        name = f"_{name}"
    return name[:80] or "video"


def _safe_remove(path: str | None) -> None:
    """Safely remove a file with retry for brief Windows file locks."""
    if not path or not os.path.isfile(path):
        return
    for _ in range(5):
        try:
            os.remove(path)
            return
        except OSError:
            time.sleep(0.05)


def _clean_stem_files(output_dir: str, stem: str, exclude_path: str | None = None) -> None:
    """Clean up all temporary/intermediate files starting with stem in output_dir."""
    if not output_dir or not stem or not os.path.isdir(output_dir):
        return
    exclude_abs = os.path.abspath(exclude_path) if exclude_path else None
    try:
        for f in os.listdir(output_dir):
            if f.startswith(stem):
                full_path = os.path.join(output_dir, f)
                if exclude_abs and os.path.abspath(full_path) == exclude_abs:
                    continue
                if os.path.isfile(full_path) or os.path.islink(full_path):
                    for _ in range(3):
                        try:
                            os.remove(full_path)
                            break
                        except OSError:
                            time.sleep(0.05)
                elif os.path.isdir(full_path):
                    shutil.rmtree(full_path, ignore_errors=True)
    except Exception:
        pass


# ---------------------------------------------------------------------------
# Cookie handling
# ---------------------------------------------------------------------------

_COOKIE_BROWSERS = ["firefox", "edge", "chrome", "brave", "opera"]
_COOKIE_FILE_ENV = "YTDLP_COOKIES_FILE"

_working_cookie_opts: dict | None = None
_cookie_probe_done = False


class _SilentLogger:
    def debug(self, msg): pass
    def info(self, msg): pass
    def warning(self, msg): pass
    def error(self, msg): pass

_silent_logger = _SilentLogger()


def _cookie_candidates() -> list[dict]:
    """Ordered list of cookie opts to try. Always ends with {} (no cookies)."""
    candidates: list[dict] = []
    cookie_file = os.environ.get(_COOKIE_FILE_ENV)
    if cookie_file and os.path.isfile(cookie_file):
        candidates.append({"cookiefile": cookie_file})
    if os.environ.get("YTDLP_NO_COOKIES") != "1":
        forced = os.environ.get("YTDLP_COOKIES_BROWSER")
        browsers = [forced] if forced else _COOKIE_BROWSERS
        for browser in browsers:
            candidates.append({"cookiesfrombrowser": (browser, None, None, None)})
    candidates.append({})
    return candidates


def _describe_cookie_opts(opts: dict) -> str:
    if "cookiefile" in opts:
        return f"cookie file {opts['cookiefile']}"
    if "cookiesfrombrowser" in opts:
        return f"{opts['cookiesfrombrowser'][0]} browser cookies"
    return "no cookies"


def _probe_cookie_opts(url: str) -> dict:
    """Try each cookie candidate against a lightweight metadata fetch.
    Result is cached for the rest of the process."""
    global _working_cookie_opts, _cookie_probe_done
    if _cookie_probe_done:
        return _working_cookie_opts if _working_cookie_opts is not None else {}

    for opts in _cookie_candidates():
        probe_opts = {
            **opts,
            "quiet": True,
            "no_warnings": True,
            "skip_download": True,
            "simulate": True,
            "logger": _silent_logger,
            "http_headers": {"User-Agent": _UA},
            "socket_timeout": 15,
            "extractor_retries": 1,
        }
        try:
            with yt_dlp.YoutubeDL(probe_opts) as ydl:
                ydl.extract_info(url, download=False)
            _safe_print(f"[downloader] cookies: using {_describe_cookie_opts(opts)}")
            _working_cookie_opts = opts
            _cookie_probe_done = True
            return opts
        except Exception as exc:
            _safe_print(f"[downloader] cookies: {_describe_cookie_opts(opts)} failed "
                        f"({type(exc).__name__}), trying next…")
            continue

    # Nothing worked — cache empty result so we don't re-probe every call
    _working_cookie_opts = {}
    _cookie_probe_done = True
    return {}


def _cookie_opts_for(url: str) -> dict:
    return _probe_cookie_opts(url)


# ---------------------------------------------------------------------------
# yt-dlp logger
# ---------------------------------------------------------------------------

class _YtdlpLogger:
    """Forward yt-dlp log messages to stdout."""
    def debug(self, msg: str) -> None:
        if msg.startswith('[debug] '):
            return
        _safe_print(f"[yt-dlp] {msg}")
    def info(self, msg: str) -> None:
        _safe_print(f"[yt-dlp] {msg}")
    def warning(self, msg: str) -> None:
        _safe_print(f"[yt-dlp] WARNING: {msg}")
    def error(self, msg: str) -> None:
        _safe_print(f"[yt-dlp] ERROR: {msg}")

_logger = _YtdlpLogger()


# ---------------------------------------------------------------------------
# aria2c detection
# ---------------------------------------------------------------------------

def _has_aria2c() -> bool:
    return shutil.which("aria2c") is not None


# ---------------------------------------------------------------------------
# Metadata
# ---------------------------------------------------------------------------

def fetch_metadata(url: str) -> dict:
    url = _clean_url(url)
    opts = {
        "quiet": False,
        "no_warnings": False,
        "skip_download": True,
        "logger": _logger,
        "http_headers": {"User-Agent": _UA},
        "socket_timeout": 15,
        "extractor_retries": 3,
        **_cookie_opts_for(url),
    }
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False)

    desc = (info.get("description") or "").strip()
    tags = list(dict.fromkeys(t.lstrip("#") for t in re.findall(r"#\w+", desc)))
    return {
        "title":       info.get("title", ""),
        "description": desc,
        "tags":        tags,
        "thumbnail":   info.get("thumbnail", ""),
        "duration":    info.get("duration", 0),
        "uploader":    info.get("uploader", ""),
    }


# ---------------------------------------------------------------------------
# Download
# ---------------------------------------------------------------------------

_HEIGHTS = ("2160", "1440", "1080", "720", "480", "360")

# Retry strategies: (use_aria, fast_fragments, format_mode, use_cookies, backoff_secs)
#   format_mode: "best"       = best DASH mp4+m4a
#                "any"        = any codec DASH
#                "progressive"= single progressive stream (most compatible)
_RETRY_STRATEGIES = [
    (True,  True,  "best",        True,  0),
    (False, False, "any",         True,  1),
    (False, False, "progressive", True,  3),
    (False, False, "progressive", False, 2),
]


def download_video(url: str, output_dir: str, progress_hook,
                   cancel_check=None, quality: str = "best") -> str | None:
    url = _clean_url(url)
    quality = (quality or "best").strip().lower().removesuffix("p")
    output_dir = os.path.abspath(os.path.normpath(output_dir))
    os.makedirs(output_dir, exist_ok=True)
    stem = uuid.uuid4().hex[:12]
    _safe_print(f"[downloader] yt-dlp {yt_dlp.version.__version__}")
    _safe_print(f"[downloader] URL: {url[:120]}")
    _safe_print(f"[downloader] output_dir: {output_dir}")
    _safe_print(f"[downloader] quality: {quality}")
    is_audio = quality in ("audio", "mp3", "m4a", "flac")

    def _hook(d: dict):
        if cancel_check and cancel_check():
            raise DownloadCancelled("Download abgebrochen")
        progress_hook(d)

    _was_cancelled = [False]

    def _hook_wrap(d: dict):
        try:
            _hook(d)
        except DownloadCancelled:
            _was_cancelled[0] = True
            raise

    def _format_string(mode: str) -> str:
        if is_audio:
            return "bestaudio[ext=m4a]/bestaudio/best"
        if mode == "progressive":
            if quality in _HEIGHTS:
                return f"best[height<={quality}]/best"
            return "best[height<=1080]/best"
        if mode == "any":
            return "bestvideo*+bestaudio/best"
        # mode == "best"
        if quality in _HEIGHTS:
            return (f"bestvideo[height<={quality}][ext=mp4]+bestaudio[ext=m4a]/"
                    f"bestvideo[height<={quality}]+bestaudio/best")
        return "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best"

    def _build_opts(*, use_aria: bool, fast_fragments: bool,
                    format_mode: str, cookie_opts: dict | None) -> dict:
        fmt = _format_string(format_mode)
        o = {
            "format": fmt,
            "outtmpl": os.path.join(output_dir, f"{stem}.%(ext)s"),
            "noplaylist": True,
            "updatetime": False,
            "windowsfilenames": True,
            "noprogress": False,
            "progress_hooks": [_hook_wrap],
            "logger": _logger,
            "http_headers": {"User-Agent": _UA},
            "retries": 10,
            "fragment_retries": 15,
            "extractor_retries": 5,
            "socket_timeout": 20,
            "buffersize": 1024 * 64,
        }
        if is_audio:
            codec, codec_quality = {
                "m4a": ("m4a", "256"),
                "flac": ("flac", "0"),
            }.get(quality, ("mp3", "320"))
            o["postprocessors"] = [{
                "key": "FFmpegExtractAudio",
                "preferredcodec": codec,
                "preferredquality": codec_quality,
            }]
        else:
            o["merge_output_format"] = "mp4"
            if format_mode != "progressive":
                o["postprocessors"] = [{"key": "FFmpegVideoConvertor", "preferedformat": "mp4"}]
        if fast_fragments:
            o["concurrent_fragment_downloads"] = 8
        if use_aria and _has_aria2c():
            o["external_downloader"] = "aria2c"
            o["external_downloader_args"] = {
                "default": [
                    "--min-split-size=1M",
                    "--max-connection-per-server=16",
                    "--split=16",
                    "--max-concurrent-downloads=16",
                ]
            }
        if cookie_opts:
            o.update(cookie_opts)
        local_ffmpeg = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ffmpeg_bin")
        if os.path.isdir(local_ffmpeg):
            o["ffmpeg_location"] = local_ffmpeg
        return o

    # Probe once for a working cookie source, then reuse across retries.
    good_cookie_opts = _cookie_opts_for(url)

    info = None
    last_exc = None
    num_strategies = len(_RETRY_STRATEGIES)

    for attempt, (aria, frag, fmt_mode, use_cookies, backoff) in enumerate(_RETRY_STRATEGIES):
        if backoff > 0:
            _safe_print(f"[downloader] waiting {backoff}s before retry…")
            time.sleep(backoff)

        _was_cancelled[0] = False
        opts = _build_opts(
            use_aria=aria, fast_fragments=frag, format_mode=fmt_mode,
            cookie_opts=good_cookie_opts if use_cookies else None,
        )
        try:
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info(url, download=True)
            break  # success
        except DownloadCancelled:
            _clean_stem_files(output_dir, stem)
            return None
        except Exception as exc:
            if _was_cancelled[0] or (cancel_check and cancel_check()):
                _clean_stem_files(output_dir, stem)
                return None
            last_exc = exc
            if attempt < num_strategies - 1:
                import traceback as _tb
                _safe_print(f"[downloader] attempt {attempt + 1} failed ({type(exc).__name__}), "
                            f"retrying with {_RETRY_STRATEGIES[attempt + 1][2]} format…")
                _safe_print(_tb.format_exc())
                _clean_stem_files(output_dir, stem)
                continue
            _clean_stem_files(output_dir, stem)
            raise

    # Locate the downloaded file
    downloaded = None
    for f in os.listdir(output_dir):
        if f.startswith(stem) and os.path.isfile(os.path.join(output_dir, f)):
            if not f.endswith(('.part', '.ytdl', '.temp', '.aria2')):
                downloaded = os.path.join(output_dir, f)
                break
    if not downloaded:
        _clean_stem_files(output_dir, stem)
        return None

    # Rename to human-readable title
    title = (info or {}).get("title", "")
    safe = _safe_title(title) if title else stem
    ext = os.path.splitext(downloaded)[1]
    target = os.path.join(output_dir, f"{safe}{ext}")
    n = 1
    while os.path.exists(target):
        target = os.path.join(output_dir, f"{safe} ({n}){ext}")
        n += 1
    try:
        os.rename(downloaded, target)
        final_path = target
    except OSError:
        final_path = downloaded

    # Clean up leftover temp artifacts
    _clean_stem_files(output_dir, stem, exclude_path=final_path)
    return final_path


def fetch_playlist_entries(url: str, max_items: int = 100) -> dict:
    """Extract flat playlist entries or list of videos from a URL quickly without downloading."""
    url = _clean_url(url)
    opts = {
        "quiet": True,
        "no_warnings": True,
        "skip_download": True,
        "extract_flat": True,
        "logger": _silent_logger,
        "http_headers": {"User-Agent": _UA},
        "socket_timeout": 15,
        "extractor_retries": 2,
    }
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False)

    if not info:
        return {"title": "Playlist", "count": 0, "entries": []}

    playlist_title = info.get("title") or "Playlist"
    raw_entries = info.get("entries")
    
    entries = []
    if raw_entries:
        for item in raw_entries:
            if not item:
                continue
            if len(entries) >= max_items:
                break
            
            vid_id = item.get("id", "")
            item_url = item.get("url") or (f"https://www.youtube.com/watch?v={vid_id}" if vid_id else "")
            if not item_url and not vid_id:
                continue
            if item_url and not item_url.startswith("http"):
                item_url = f"https://www.youtube.com/watch?v={vid_id}"
                
            thumb = item.get("thumbnail")
            if not thumb and item.get("thumbnails"):
                th_list = item.get("thumbnails")
                if isinstance(th_list, list) and len(th_list) > 0:
                    thumb = th_list[-1].get("url") if isinstance(th_list[-1], dict) else None
            if not thumb and vid_id:
                thumb = f"https://i.ytimg.com/vi/{vid_id}/hqdefault.jpg"

            entries.append({
                "id": vid_id,
                "title": item.get("title") or "Untitled Video",
                "url": item_url,
                "duration": item.get("duration") or 0,
                "uploader": item.get("uploader") or item.get("channel") or "",
                "thumbnail": thumb or "",
            })
    else:
        # Single video treated as a 1-item list
        vid_id = info.get("id", "")
        entries.append({
            "id": vid_id,
            "title": info.get("title") or "Untitled Video",
            "url": url,
            "duration": info.get("duration") or 0,
            "uploader": info.get("uploader") or "",
            "thumbnail": info.get("thumbnail") or "",
        })

    return {
        "title": playlist_title,
        "count": len(entries),
        "entries": entries,
    }