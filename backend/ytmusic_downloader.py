"""YouTube Music Downloader Module - Specialized for music.youtube.com

Features:
- Handles YouTube Music tracks, albums, and playlists
- Extracts music-specific metadata (Artist, Track Title, Album Name, Release Year, High-Res Cover Art)
- High Quality Audio Extraction (MP3 320kbps, M4A 256kbps, FLAC Lossless)
- Automatic ID3/MP4 Metadata Tagging and Cover Art Embedding into output files
"""
from __future__ import annotations
import os, re, sys, time, uuid
import yt_dlp

# Import shared helpers from downloader (single source of truth)
from downloader import (
    _safe_print, _clean_url, _safe_title, _clean_stem_files,
    _cookie_opts_for, _logger, _UA,
    DownloadCancelled,
    _SilentLogger,
)

_silent_logger = _SilentLogger()


def is_ytmusic_url(url: str) -> bool:
    """Check if the provided URL is a YouTube Music URL.

    Regular YouTube links with a ``list=`` parameter (e.g. a video opened from a playlist)
    are *not* music – treating them as such downloaded audio only.
    """
    return "music.youtube.com" in _clean_url(url).lower()


def fetch_ytmusic_metadata(url: str) -> dict:
    """Extract detailed metadata for a YouTube Music track/album."""
    url = _clean_url(url)
    
    # If watch?v= is present, treat as single track even if list= (e.g. YouTube radio mix) is attached
    is_track = "watch?v=" in url or "/watch?" in url

    opts = {
        "quiet": True,
        "no_warnings": True,
        "skip_download": True,
        "noplaylist": is_track,
        "logger": _silent_logger,
        "http_headers": {"User-Agent": _UA},
        "socket_timeout": 10,
        "extractor_retries": 2,
    }
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False)

    if not info:
        raise ValueError("Could not fetch metadata for YouTube Music URL")

    # If playlist/album info returned
    if info.get("_type") == "playlist" or "entries" in info:
        entries = [e for e in info.get("entries", []) if e]
        first = entries[0] if entries else {}
        title = info.get("title") or first.get("title", "")
        artist = info.get("uploader") or info.get("channel") or first.get("artist") or first.get("uploader", "")
        album = info.get("title") or "YouTube Music Album"
        thumbnail = info.get("thumbnail") or first.get("thumbnail", "")
        return {
            "title": title,
            "artist": artist,
            "album": album,
            "description": info.get("description", "") or f"Album/Playlist with {len(entries)} tracks",
            "thumbnail": thumbnail,
            "duration": sum(e.get("duration", 0) for e in entries),
            "uploader": artist,
            "is_music": True,
            "track_count": len(entries),
            "tags": ["YouTubeMusic", "Audio", "Album"],
        }

    title = info.get("title") or ""
    artist = info.get("artist") or info.get("creator") or info.get("uploader") or info.get("channel") or ""
    album = info.get("album") or info.get("playlist_title") or "Single"
    thumbnail = info.get("thumbnail") or ""
    duration = info.get("duration") or 0
    upload_date = info.get("upload_date") or ""
    release_year = str(info.get("release_year") or upload_date[:4] or "")

    # Enhanced clean track title formatting (e.g. remove " - Topic" or official video tags)
    clean_artist = re.sub(r' - Topic$', '', str(artist)).strip()

    return {
        "title": title,
        "artist": clean_artist,
        "album": album,
        "description": f"Artist: {clean_artist} | Album: {album} | Year: {release_year}",
        "thumbnail": thumbnail,
        "duration": duration,
        "uploader": clean_artist,
        "release_year": release_year,
        "is_music": True,
        "tags": ["YouTubeMusic", "Audio", clean_artist] if clean_artist else ["YouTubeMusic", "Audio"],
    }



def download_ytmusic_audio(
    url: str,
    output_dir: str,
    progress_hook,
    cancel_check=None,
    quality: str = "ytmusic_320k"
) -> str | None:
    """Download YouTube Music track/album audio with high quality & embedded ID3 tags."""
    url = _clean_url(url)
    output_dir = os.path.abspath(os.path.normpath(output_dir))
    os.makedirs(output_dir, exist_ok=True)
    stem = uuid.uuid4().hex[:12]

    _safe_print(f"[ytmusic_downloader] Downloading URL: {url}")
    _safe_print(f"[ytmusic_downloader] Quality mode: {quality}")

    def _hook(d: dict):
        if cancel_check and cancel_check():
            raise DownloadCancelled("Download cancelled by user")
        progress_hook(d)

    # Determine postprocessors & format based on quality profile
    postprocessors = []

    if quality == "ytmusic_flac":
        preferred_codec = "flac"
        preferred_quality = "0"
        file_ext = "flac"
    elif quality in ("ytmusic_m4a", "m4a"):
        preferred_codec = "m4a"
        preferred_quality = "256"
        file_ext = "m4a"
    else:  # ytmusic_320k / default mp3
        preferred_codec = "mp3"
        preferred_quality = "320"
        file_ext = "mp3"

    postprocessors.append({
        "key": "FFmpegExtractAudio",
        "preferredcodec": preferred_codec,
        "preferredquality": preferred_quality,
    })
    postprocessors.append({
        "key": "FFmpegMetadata",
        "add_metadata": True,
    })
    postprocessors.append({
        "key": "EmbedThumbnail",
        "already_have_thumbnail": False,
    })

    is_track = "watch?v=" in url or "/watch?" in url

    local_ffmpeg = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ffmpeg_bin")

    opts = {
        "format": "bestaudio/best",
        "outtmpl": os.path.join(output_dir, f"{stem}.%(ext)s"),
        "noplaylist": is_track,
        "writethumbnail": True,
        "updatetime": False,
        "windowsfilenames": True,
        "progress_hooks": [_hook],
        "logger": _logger,
        "http_headers": {"User-Agent": _UA},
        "retries": 5,
        "fragment_retries": 10,
        "extractor_retries": 3,
        "socket_timeout": 15,
        "postprocessors": postprocessors,
        "postprocessor_args": {
            "ffmpeg": ["-id3v2_version", "3"]
        },
    }
    if os.path.isdir(local_ffmpeg):
        opts["ffmpeg_location"] = local_ffmpeg

    try:
        try:
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info(url, download=True)
        except Exception as first_exc:
            if cancel_check and cancel_check():
                _clean_stem_files(output_dir, stem)
                return None
            _safe_print(f"[ytmusic_downloader] Fast download without cookies failed ({type(first_exc).__name__}), retrying with cookies…")
            opts.update(_cookie_opts_for(url))
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info(url, download=True)

        if not info:
            _clean_stem_files(output_dir, stem)
            return None

        # Find downloaded file
        downloaded = None
        for f in os.listdir(output_dir):
            if f.startswith(stem) and os.path.isfile(os.path.join(output_dir, f)):
                if not f.endswith(('.part', '.ytdl', '.temp', '.jpg', '.webp', '.png', '.aria2')):
                    downloaded = os.path.join(output_dir, f)
                    break

        if not downloaded:
            _clean_stem_files(output_dir, stem)
            return None

        # Rename to clean "Artist - Title.ext"
        track_title = (info or {}).get("title", "")
        artist = (info or {}).get("artist") or (info or {}).get("uploader", "")
        artist = re.sub(r' - Topic$', '', artist).strip()

        if artist and track_title and not track_title.lower().startswith(artist.lower()):
            raw_name = f"{artist} - {track_title}"
        else:
            raw_name = track_title or stem

        safe_stem = _safe_title(raw_name)
        ext = os.path.splitext(downloaded)[1]
        target = os.path.join(output_dir, f"{safe_stem}{ext}")

        n = 1
        while os.path.exists(target):
            target = os.path.join(output_dir, f"{safe_stem} ({n}){ext}")
            n += 1

        try:
            os.rename(downloaded, target)
            _safe_print(f"[ytmusic_downloader] Renamed to: {target}")
            final_path = target
        except OSError:
            final_path = downloaded

        # Clean up any leftover temporary files (including extracted thumbnail images)
        _clean_stem_files(output_dir, stem, exclude_path=final_path)
        return final_path

    except DownloadCancelled:
        _safe_print("[ytmusic_downloader] Download cancelled")
        _clean_stem_files(output_dir, stem)
        return None
    except Exception as exc:
        _safe_print(f"[ytmusic_downloader] Error downloading: {exc}")
        _clean_stem_files(output_dir, stem)
        raise
