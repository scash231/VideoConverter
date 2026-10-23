# MediaConverter - VideoConverter

Alpha - https://github.com/scash231/VideoConverter/releases/tag/27H1

Modern C# WPF desktop application with a high-performance Python backend for downloading, converting, and auto-uploading media across YouTube Music, YouTube Shorts, Instagram Reels, TikTok, and universal web sources with built-in anti-detection video fingerprinting.

## Install

Download and run **[VideoConverterSetup.exe](https://scash.uk/vc)**. Nothing else is needed: setup downloads the app, the Ferrite engine, the .NET runtime (if missing), FFmpeg and yt-dlp. It installs for your Windows user only (no admin rights, except Windows asks once if .NET has to be installed). Run it again to update; uninstall in Windows Settings → Apps.

The Python (Legacy) engine is no longer supported and is not part of the installer. If you still need it, get [VideoConverter-Legacy-Python.zip](https://scash.uk/vc/VideoConverter-Legacy-Python.zip) and follow its README.

Releasing (maintainer): `.\installer\build_release.ps1 -Upload` builds everything into `dist\vc` and uploads it to scash.uk.

# 26H2 Beta

## New

- **YouTube Music support** — download YT Music tracks up to 320 kbps, with artist/album/year/track count pulled from metadata
- **5 new languages** — Spanish, French, Italian, Japanese, Chinese
- **Phased progress window** — download / fingerprint-bypass / upload, each with its own progress and live speed + ETA
- **Success/failure notifications** with optional sound
- **Window resize grip** — every window is resizable and remembers its last size
- **Designer window redesign** — new Layout and Animations tabs, gradient controls, collapsible color categories

## Improved

- Rounded window corners now clip properly at the OS level (no more square edges under the rounded XAML)
- Blur enabled by default on LogViewer, DevConsole, DescEditor
- Sandstone theme preset fixed for proper glassmorphism
- Popup backgrounds cleaner and more opaque
- Animation system reworked (easing, spring dynamics, staggering)

## Misc

- `mutagen` dependency for YT Music metadata
- New endpoint: `POST /api/ytmusic/metadata`
- 18 new theme settings fields
- Console log filters (System / Backend / FFmpeg)
- Compact mode toggle

---
*Note: build artifacts are not the same as release assets — release assets can be modified after the fact.*
#

- YoutubeMusic support - Lossless/Mp3
- Audio/Video support
- 4k/2k/1080p/720p/480p/Audio Only
- Metadata and Video/Audio Changer Option to avoid automaitc Heuristic Detections from Several Plattoforms.
- Fully Automatic Uploud function to Youtube (You need your API Key for that - Requirments)

26H2 Beta <img width="764" height="562" alt="image" src="https://github.com/user-attachments/assets/372b9f46-8634-4f2d-a26a-5fa01657ee86" />

27H1 Canary<img width="755" height="555" alt="image" src="https://github.com/user-attachments/assets/10c36ef0-e062-47cb-b0df-5c30fe8a91ee" />

