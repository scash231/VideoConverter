"""Automatic FFmpeg installer / locator."""
from __future__ import annotations
import os, shutil, urllib.request, zipfile

_URL = (
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/"
    "ffmpeg-master-latest-win64-gpl.zip"
)


def ensure_ffmpeg() -> None:
    here = os.path.dirname(os.path.abspath(__file__))
    local = os.path.join(here, "ffmpeg_bin")
    # Directory used by the app's built-in FFmpeg installer (FFmpegInstallerService)
    app_install = os.path.join(os.environ.get("LOCALAPPDATA", ""), "VideoConverter", "ffmpeg", "bin")

    for d in (local, app_install):
        if (os.path.isfile(os.path.join(d, "ffmpeg.exe")) and
                os.path.isfile(os.path.join(d, "ffprobe.exe"))):
            _add(d)
            return

    if shutil.which("ffmpeg") and shutil.which("ffprobe"):
        return

    print("Downloading FFmpeg ...")
    os.makedirs(local, exist_ok=True)
    zp = os.path.join(local, "_tmp.zip")
    try:
        with urllib.request.urlopen(_URL) as r, open(zp, "wb") as f:
            while chunk := r.read(1 << 16):
                f.write(chunk)
        targets = {
            "ffmpeg.exe": os.path.join(local, "ffmpeg.exe"),
            "ffprobe.exe": os.path.join(local, "ffprobe.exe"),
        }
        with zipfile.ZipFile(zp) as zf:
            found: dict[str, bool] = {}
            for m in zf.namelist():
                name = m.rsplit("/", 1)[-1]
                if name in targets and name not in found:
                    with zf.open(m) as src, open(targets[name], "wb") as dst:
                        dst.write(src.read())
                    found[name] = True
                if len(found) == len(targets):
                    break
    finally:
        if os.path.isfile(zp):
            os.remove(zp)
    _add(local)
    print("FFmpeg installed.")


def _add(d: str) -> None:
    os.environ["PATH"] = d + os.pathsep + os.environ.get("PATH", "")