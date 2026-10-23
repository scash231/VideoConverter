# Ferrite — porting the Native C# engine to Rust

Status: **Phase 2 done (build 27H1.19).**
- Ferrite is implemented in `backend-ferrite/`, wired into the app, and selectable next to Native and Legacy.
- It was verified three ways against the real C# code:
  - golden fixtures produced by the C# code itself
  - side-by-side runs of identical jobs through Native and Ferrite
  - a run of every feature through all three engines via the app's own engine switch

Ferrite was ported from the Native C# engine only, with one exception you approved: YouTube upload, which Native does not
have. Its behaviour and file formats follow the Legacy engine (`backend/uploader.py`, `backend/server.py`), so `token.json`
stays shared.

Contents

0. [Summary](#0-summary)
1. [Decisions](#1-decisions)
2. [How the frontend talks to the backends](#2-how-the-frontend-talks-to-the-backends)
3. [Engine API contract](#3-engine-api-contract)
4. [Native job pipeline, step by step](#4-native-job-pipeline-step-by-step)
5. [External process calls](#5-external-process-calls)
6. [Output and progress parsing](#6-output-and-progress-parsing)
7. [Files and network endpoints](#7-files-and-network-endpoints)
8. [Error messages and edge cases](#8-error-messages-and-edge-cases)
9. [Ferrite-only behaviour](#9-ferrite-only-behaviour)
10. [.NET and Win32 semantics reproduced in Rust](#10-net-and-win32-semantics-reproduced-in-rust)
11. [File map: Native C# → Rust modules](#11-file-map-native-c--rust-modules)
12. [Interop: sidecar protocol](#12-interop-sidecar-protocol)
13. [App changes (C#)](#13-app-changes-c)
14. [Feature matrix](#14-feature-matrix)
15. [Parity checklist](#15-parity-checklist)
16. [Verification results](#16-verification-results)
17. [Known differences](#17-known-differences)
18. [Build, test, release](#18-build-test-release)

---

## 0. Summary

| | |
|---|---|
| Rust workspace | `backend-ferrite/`: `crates/ferrite-engine` (library, all logic) and `crates/ferrite-sidecar` (`ferrite.exe`) |
| Interop | Sidecar process, newline-delimited JSON over stdin/stdout (§12) |
| App integration | `FerriteBackend : IEngineService`. `EngineProvider` hands out a stable proxy; engine switches wait for running work |
| Setting | `execution_engine` = `native` (default) \| `ferrite` \| `python` (shown as "Legacy") |
| Gates | `cargo build --release`, `cargo clippy --workspace --all-targets -- -D warnings`, `cargo test --workspace` and `dotnet build`: all pass |
| Parity | Golden tests: 13/13 groups. Side-by-side: 27/27 scenarios identical, including file hashes for downloads and re-encodes (§16.3). All three engines through the app: every feature works or fails cleanly (§16.4). Error advice: 192/192 (§16.5) |
| Default engine | Stays **Native**. Some checklist items need accounts this machine does not have (§15); switch the default when you're satisfied |

---

## 1. Decisions

| # | Topic | Your answer | What was done |
|---|---|---|---|
| D1 | YouTube upload + OAuth | "it should support it" | Ferrite uploads to YouTube (§9). `client_secret*.json` / `token.json` stay compatible with Legacy. Native still rejects upload jobs. |
| D2 | Browser cookie probing | (FYI) | Ferrite does what Native does: only the cookies file from Settings → Advanced → Backend, no browser scraping. |
| D3 | Subtitles | (FYI) | Same `python -c` call into `backend/subtitler.py` as Native. |
| D4 | Deno | (FYI) | `%USERPROFILE%\.deno\bin` is put on the child `PATH` like Native. The error window explains how to install Deno when yt-dlp asks for it. |
| D5 | M4A | (FYI) | `--audio-format m4a --audio-quality 256`, identical to Native. |
| D6 | Shared interface | "yes" | `IEngineService` stays the shared interface. `FerriteBackend` implements it, and `BackendService` stays the Legacy adapter. |
| D7 | Settings key | "yes" | The existing `execution_engine` key, with the new value `ferrite`. Old files load unchanged, and an old build reading `ferrite` falls back to Native. |
| D8 | Languages | (FYI) | All new texts exist in en/de/ru (the three languages the app has). |
| D9 | Version | "27H1.19" | `BuildInfoService` bumped, changelog written, and Settings → Info shows `27H1.19 (sha) · Ferrite`. |
| D10 | Switching while a job runs | "a" | The switch waits until the running engine is idle and a toast says so (§2.2). |
| D11 | "Legacy mode" wording | "what fits best", then "Legacy" | The Python engine is labelled "Legacy". The help bubble describes each engine by what it can do. Native's upload message says *"…switch to Ferrite or Legacy…"* and its error field says *"YouTube upload requires the Ferrite or Legacy engine."* |
| D12 | Legacy + cloud upload | "what you think is best" | The job fails with *"Cloud upload requires the Native or Ferrite engine."* (shown in the error window). |
| D13 | Ferrite-only failures | — | *"Ferrite backend not found: {path}"*, *"Ferrite backend process exited unexpectedly (exit code N)."* and *"Ferrite backend did not start: …"* all reach the error window. A crashed sidecar is restarted on the next call when "Auto-restart backend" is on. |
| D14–D17 | Unavoidable differences | "do what's best" | Implemented as recommended; listed in §17. |
| — | Error messages | "show real solutions and the actual error, overhaul the error message window" | New error window, `ErrorAdvisor` with 53 categories × 3 languages, a fix hint on Multi Downloader items and an Editor hint (§13.3). |

---

## 2. How the frontend talks to the backends

### 2.1 Layers

```
MainWindow / BatchQueueWindow / EditorWindow
        │  IEngineService  (always EngineProvider.Current = EngineProxy)
        ▼
EngineProxy ── pins the engine a call/job started on, routes stream/cancel by job id
        ▼
EngineProvider (static) ── settings.execution_engine ──┬─ "native"  → NativeEngineService (in-process)
                                                       ├─ "ferrite" → FerriteBackend ──JSON lines──► ferrite.exe (Rust)
                                                       └─ "python"  → BackendService ──HTTP :8765──► backend/server.py
```

### 2.2 Switching engines

- `SwitchEngine(key)` saves the key. When the active engine is idle, the switch happens at once (old engine disposed,
  Python started or stopped, `EngineChanged` raised).
- While any call or job is pinned to the active engine, the switch is **deferred** and runs when the last lease is
  released. Settings shows *"The backend switches to Ferrite as soon as the current download is finished."*
- Switching back to the active engine cancels a pending switch.
- `BatchQueueWindow` keeps working after a switch: the proxy it holds always resolves the active engine.
- `RestartAsync` (Restart Backend button, console `restart`) still disposes and recreates the engine immediately.

### 2.3 UI call sites (unchanged)

Every window still calls only `IEngineService` (`FetchMetadataAsync`, `FetchYTMusicMetadataAsync`,
`FetchPlaylistAsync`, `CreateJobAsync` + `StreamJobAsync` + `CancelJobAsync`, `GenerateSubtitlesAsync`,
`WaitForHealthAsync`). The only UI changes are the engine selector, the version text, the status texts that
name the engine, and the error-window overhaul.

---

## 3. Engine API contract

This is the contract Ferrite must satisfy from the UI's point of view. "Native:" describes the
exact current behavior.

### 3.1 Members

| Member | Signature | Native behavior |
|---|---|---|
| `EngineName` | `string` | `"Native C# Media Engine"`; Legacy `"Python FastAPI Backend"`; Ferrite `"Ferrite Rust Media Engine"` |
| `WaitForHealthAsync` | `Task<bool>(CancellationToken ct, int maxWait = 30)` | Awaits one-time binary resolution (`ResolveBinaries` on a worker thread), then returns `true`. **Ignores `maxWait`.** Throws OCE if `ct` is cancelled. |
| `FetchMetadataAsync` | `Task<MetadataResponse?>(string url, CancellationToken ct = default)` | §4.2. Returns `null` on any failure. Throws OCE only when `ct` is cancelled during the yt-dlp phase. |
| `FetchYTMusicMetadataAsync` | same | Forces `IsMusic`; the oEmbed fallback also sets `IsMusic = true` |
| `CreateJobAsync` | `Task<string>(url, mode, platform, title, description, tags, outputDir, privacy, fingerprint, fingerprintMethod = "standard", useGpu = false, quality = null, ct = default, cloudUpload = false, cloudProvider = null, cloudTempOnly = true)` | Returns an 8-char lowercase hex id at once (completed task) and starts the pipeline on the thread pool. **`ct` is ignored.** `description`, `tags` and `privacy` are unused. |
| `StreamJobAsync` | `IAsyncEnumerable<JobStatus>(string jobId, CancellationToken ct = default)` | Unknown id → ends at once. Otherwise reads the job channel and stops after yielding `completed`, `error` or `cancelled`, or when the channel completes. `ct` cancels the wait (OCE). Single consumer: the channel buffers everything from job start on. |
| `CancelJobAsync` | `Task(string jobId, CancellationToken ct = default)` | Synchronous: cancel the job CTS, then kill the running process tree; swallows errors. `ct` is ignored. |
| `GenerateSubtitlesAsync` | `Task<string>(videoPath, modelSize = "base", language = null, ct = default)` | §4.5 |
| `FetchPlaylistAsync` | `Task<PlaylistInfo?>(url, maxItems = 100, ct = default)` | §4.3 |
| `Dispose` | `void` | For each job: cancel the CTS, kill the process tree, dispose the process. Clear the job table. |

### 3.2 DTOs (`Models/Models.cs`; JSON names are the wire contract)

- `MetadataResponse`: `title, description, tags[], thumbnail, duration (double, lenient), uploader, artist?, album?, is_music, release_year?, track_count? (lenient int)`
- `JobStatus`: `id, status, progress (double, lenient), message, error?, eta? (int, lenient), speed?, result? (Dictionary<string, object>)`
- `PlaylistInfo`: `title, count, entries[]`; `PlaylistEntry`: `id, title, url, duration, uploader, thumbnail`
- `JobCreateResponse`: `job_id` (Legacy only)

### 3.3 Job status stream

| `status` | `progress` | Emitted when |
|---|---|---|
| `pending` | 0 | **Never emitted.** It is only the initial `LastStatus`. |
| `downloading` | 0–100 (yt-dlp %) | start, every progress line, retries |
| `processing` | 50–100 | fingerprinting |
| `uploading` | 0–100 | cloud upload |
| `completed` | 100 | end; `result` set |
| `error` | 0 | failure; `error` = raw message, `message` = `"Error: " + message` (except the upload rejection, §8) |
| `cancelled` | 0 | the job CTS was cancelled |

- `progress` is always `Math.Clamp(p, 0, 100)`.
- After the terminal status the channel is completed. The job entry is dropped 2 minutes later.
- `result` keys and CLR types: `file_path` (string), `title` (string: the passed title, or the final file name without extension), and when cloud upload is on: `cloud_provider` (string, display name), `cloud_destination` (string), `cloud_url` (string, only if non-empty) and `is_temp` (bool `true`, only for temp-only).
- Phase prefix: `"[{current}/{total}] "` when total > 1; total = 1 + fingerprint (video only) + cloud. It is evaluated each time a message is built.
- Every status is also logged to the console as `"[{STATUS}] {message}"`, but only when `status + "|" + message-with-numbers-removed` changes (regex `[\d.,]+\s*%?`).

Every message text Native emits (`{p}` = phase prefix; numbers are formatted with the **current culture**, so `45,3` on this de-DE machine):

```
downloading  {p}Downloading music...                        0
downloading  {p}Downloading audio... | {p}Downloading video...   0
downloading  {p}Download Video: {pct:F1}% | {p}Download Audio: {pct:F1}% | {p}Download Music: {pct:F1}%   (+speed, eta)
downloading  {p}Retry {n}/3 ({best|any|progressive} format)...   0
downloading  {p}Retrying music download...                  0
processing   {p}Applying fingerprint bypass...              50
processing   {p}Encoding video: {frac*100:F0}%              50 + frac*50
processing   {p}GPU encoder failed, retrying on CPU...      50
uploading    {p}Uploading to {ProviderDisplayName}...       0
uploading    {p}{provider progress message}                 pct (§4.4)
completed    Download and processing completed successfully.      100
completed    {upload result message}                        100
cancelled    Job was cancelled by user.                     0
error        Error: {ex.Message}                            0
error        YouTube Upload is not available in Native mode. Please switch to Ferrite or Legacy in Settings → Advanced for upload jobs.   (error: "YouTube upload requires the Ferrite or Legacy engine.", changed in 27H1.19, D11)
```

### 3.4 Events, cancellation, threading

- **Console events**: `EngineProvider.EmitOutput` is called from the threads that read process output (thread pool). It receives yt-dlp stdout lines (download and YT Music only), `"[stderr] " + trimmed stderr line`, status phase lines, and for subtitles the whole trimmed stderr block. Metadata, playlist, ffprobe and ffmpeg calls log nothing.
- **Cancellation**: each job has its own CTS, triggered only by `CancelJobAsync` or `Dispose`. Each async step checks the token. Running processes are killed with `Kill(entireProcessTree: true)`, both by `CancelJobAsync` and by `WaitForExitOrKillAsync`. Calls outside jobs (metadata, playlist, subtitles, health) use the caller's `ct`.
- **Threading**: pipeline on the thread pool; status writes are thread-safe channel writes. Consumers (`await foreach` in windows) resume on their own synchronization context. The FFmpeg install dialog is shown with `Dispatcher.Invoke` from the pipeline thread, which **blocks that pipeline** until the dialog closes.

---

## 4. Native job pipeline, step by step

### 4.1 Binary resolution (`ResolveBinaries`, once per engine instance, worker thread)

1. **ffmpeg/ffprobe**: the first existing candidate wins (`MediaTools.GetFfmpegCandidates`):
   `%LOCALAPPDATA%\VideoConverter\ffmpeg\bin\ffmpeg.exe`, `{appDir}\backend\ffmpeg_bin\`, `{sol}\backend\ffmpeg_bin\`, `{appDir}\ffmpeg_bin\`, `{sol}\ffmpeg_bin\`, `{appDir}\ffmpeg.exe`, `{sol}\ffmpeg.exe`, `%LOCALAPPDATA%\VideoConverter\ffmpeg_bin\`, `%APPDATA%\VideoConverter\ffmpeg_bin\`, then `ffmpeg.exe` in every dir of the Process, User and Machine `PATH` (in that order, deduplicated case-insensitively, WindowsApps skipped, quotes trimmed). ffprobe is used only if `ffprobe.exe` sits next to ffmpeg. Fallbacks are the bare names `ffmpeg` / `ffprobe`.
   `{appDir}` = `MediaTools.AppDir`, the folder of the real exe (with a trailing `\`; not `AppDomain.BaseDirectory`, which is the `%TEMP%\.net\...` extraction folder in the single-file publish). `{sol}` = `FindSolutionDir()`: the exe dir if it contains `backend\`, else up to 8 parents of `{appDir}` that contain `backend\`, else the exe dir.
2. **yt-dlp exe**: the first candidate that exists **and** runs `--version` with exit code 0 within 3.5 s:
   `{sol}\backend\venv\Scripts\yt-dlp.exe`, `{appDir}\backend\venv\Scripts\`, `{appDir}\venv\Scripts\`, `{sol}\venv\Scripts\`, `{appDir}\yt-dlp.exe`, `{appDir}\backend\`, `{sol}\backend\`, `{sol}\`, `%LOCALAPPDATA%\VideoConverter\bin\`, `%APPDATA%\VideoConverter\bin\`, `%LOCALAPPDATA%\Programs\Python\Python31{2,1,0}\Scripts\`, `%APPDATA%\Python\Python31{2,1,0}\Scripts\`, then `yt-dlp.exe` in each `PATH` dir as above.
3. **python -m yt_dlp** (static `Lazy`, once per process): the first of `{sol}\backend\venv\Scripts\python.exe`, `{appDir}\backend\venv\…`, `{sol}\venv\…`, `{appDir}\venv\…`, `%LOCALAPPDATA%\Programs\Python\Python31{2,1,0}\python.exe`, `%APPDATA%\Programs\Python\Python31{2,1,0}\python.exe`, then `python.exe` in each `PATH` dir, that passes `-m yt_dlp --version`. After that come `py -3.12|-3.11|-3.10 -m yt_dlp --version` (each returns plain `"py"`; the version flag is **dropped** later, see §8) and `python -m yt_dlp --version`. Runner = `(python, "-m yt_dlp")`.
4. **Self-healing download**: `%LOCALAPPDATA%\VideoConverter\bin\yt-dlp.exe`. If it exists and passes `--version`, use it. Otherwise `GET https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe` (UA `Mozilla/5.0 (Windows NT 10.0; Win64; x64)`, 30 s for the whole response), write `…yt-dlp.exe.tmp`, move it over the target, and test again. Final fallback is the bare name `yt-dlp`.
- `TestExecutable` sets `PYTHONUTF8=1` and `PYTHONIOENCODING=utf-8` but **not** the PATH tweaks. stdout/stderr are redirected but not read. On timeout it kills the tree and returns false.

**`EnsureFfmpegReadyAsync`** (called at the start of every job; its result is **ignored**):
returns true if both ffmpeg and ffprobe exist as files; else true if `{ffmpeg} -version` exits 0;
else calls `FFmpegInstallerService.PromptAndInstallIfNeededAsync()` (a WPF dialog unless
`IsFfmpegInstalled` finds an ffmpeg first). On `true` it runs `ResolveBinaries()` again,
synchronously.

**Child process environment** (`ConfigureProcessStartInfo`): UTF-8 stdout/stderr, `PYTHONUTF8=1`,
`PYTHONIOENCODING=utf-8`, `PATH = join(";", [ffmpegDir if ffmpeg is a file, runnerDir if the
yt-dlp runner exe is a file, %USERPROFILE%\.deno\bin if it exists]) + ";" + PATH`. Used for yt-dlp,
ffmpeg, ffprobe and the python `-c` calls. **Not** used for `TestExecutable` or the hardware-encoder probe.

**Cookies**: `settings.cookies_file_path` if it is non-blank and the file exists, else none. It is read at the moment each command line is built.

### 4.2 Metadata

1. `CleanUrl`: remove the invisible characters listed in §6.4, then `Trim()`.
2. yt-dlp `-J` (§5.1, 90 s timeout). Parse `stdout.Trim()` as JSON:
   - `title`, `description`, `thumbnail`: string or `""`.
   - `uploader` if present, else `channel`, else `""`.
   - `duration`: double if present, else 0.
   - `artist`: the property value if present (JSON null stays null), else `uploader` when it is a music URL, else null. A trailing `" - Topic"` is removed (case-insensitive), then trimmed.
   - `album`: string or null. `release_year`: `JsonElement.ToString()`: raw text for numbers (`2019`, `2019.0`), `""` for null, `True`/`False` for booleans.
   - `Uploader = artist ?? uploader`; `IsMusic = forceMusic || url contains music.youtube.com`.
   - `tags`: if `tags` is an array, every non-blank string element. Otherwise, if the description is non-blank, every `#(\w+)` match.
   - The result is `null` if the title is blank. **Any exception → `null`**, including wrong JSON kinds; see §10.
3. Fallback: only for URLs containing `youtube.com` or `youtu.be`. `GET https://www.youtube.com/oembed?url={EscapeDataString(url)}&format=json` (8 s). Maps `title`, `author_name` → Uploader, `thumbnail_url`, and `title` → Description (sic). Any error, **including cancellation**, gives `null`.

### 4.3 Playlist (three stages)

1. yt-dlp `--flat-playlist` (§5.4, 120 s, **no cookies**). Take the substring from the first `{` to the last `}` and parse it:
   - playlist title: string, default `"Playlist"`.
   - Per entry:
     - `id` and `url` (strings).
     - If `url` is empty and an id exists, `url = https://www.youtube.com/watch?v={id}`. If `url` does not start with `http` (case-insensitive), also `https://www.youtube.com/watch?v={id}`, **even if the id is empty**. Entries whose url is still empty are skipped.
     - `title` defaults to `"Untitled Video"`. `uploader` falls back to `channel`, then `""`. `duration` is a double, else 0.
     - `thumbnail`: the string value. If that is empty, the **last** string `url` in `thumbnails[]`. If still empty and an id exists, `https://i.ytimg.com/vi/{id}/hqdefault.jpg`.
   - Returned if there is at least 1 entry; `count = entries.Count`.
2. Only if a Python with yt-dlp was found: `python -c` with `downloader.fetch_playlist_entries` (§5.8). Take stdout after `__PL_JSON__`, trim it and deserialize to `PlaylistInfo` (case-insensitive, numbers from strings).
3. `FetchMetadataAsync(cleanUrl)`. If it has a title, return a 1-entry playlist (`id` = random 8 hex, `count` = 1).
- Blank URL → `null`. Each stage swallows its exceptions except OCE while `ct` is cancelled. Result is `null` if every stage fails.

### 4.4 Job pipeline (`ExecuteJobPipelineAsync`)

```
cleanUrl = CleanUrl(url)
isUpload = mode == "upload" (case-insensitive)                → emit error (§3.3), complete, return
isYtMusic = cleanUrl contains music.youtube.com || platform == "ytmusic" (ci) || quality starts with "ytmusic" (ci)
isAudio = quality ∈ {audio, mp3, m4a, flac} (ci) || isYtMusic
hasFp = fingerprint && !isAudio
await EnsureInitialized; await EnsureFfmpegReady (result ignored)
downloadDir = cloudUpload && cloudTempOnly ? %TEMP%\ReelsConverter\jobs\{jobId}
            : outputDir blank ? %USERPROFILE%\Downloads : Path.GetFullPath(outputDir)
CreateDirectory(downloadDir); rawStem = random 12 hex
isYtMusic → DownloadYtMusic(quality ?? "ytmusic_320k")  else DownloadVideoWithRetries(quality ?? "best", isAudio)
cancelled? → OCE;  file missing → FileNotFoundException("Downloaded media file could not be located.")
hasFp → emit processing 50 "Applying fingerprint bypass..."; finalFile = ApplyFingerprint(); if changed and exists, SafeDelete(original)
title non-blank → rename to SanitizeFilename(title) + ext; on collision use " (n)", n = 1..; File.Move errors swallowed
CleanStemFiles(downloadDir, rawStem, exclude: finalFile)
result = {file_path, title ?? name-without-ext}
cloudUpload → provider = cloudProvider ?? settings.cloud_provider
              emit uploading 0 "Uploading to {display}..."
              r = CloudStorageService.UploadAsync(finalFile, provider, settings, (pct,msg) → emit uploading pct "{p}{msg}", ct)
              !r.Success → throw Exception(r.ErrorMessage ?? "Failed to upload to {display}")
              result += cloud_provider, cloud_destination = r.DestinationPath ?? r.Url ?? display, cloud_url (if non-empty)
              cloudTempOnly → is_temp = true; delete finalFile; delete downloadDir recursively if the path contains "ReelsConverter"
              emit completed 100 (r.Message ?? "Successfully uploaded to {display}!")
else          emit completed 100 "Download and processing completed successfully."
catch OCE       → emit cancelled; CleanStemFiles(downloadDir, rawStem)
catch Exception → emit error "Error: {msg}" (error = msg); CleanStemFiles(downloadDir, rawStem)
finally         → dispose process; forget the job after 2 min
```

**Video download** (`DownloadVideoWithRetriesAsync`): strategies `best` (0 s), `any` (+1 s), `progressive`
(+2 s). Before each attempt: `ct.ThrowIfCancellationRequested()`, then the backoff delay.
On failure (not OCE): `CleanStemFiles`, then emit `"{p}Retry {n+2}/3 ({next} format)..."` unless it was the last attempt.
After 3 failures the last exception is rethrown.

Format strings (`GetFormatString`): audio → `bestaudio[ext=m4a]/bestaudio/best`. Otherwise
`q = quality.Trim().ToLowerInvariant()` with one trailing `p` stripped (if length > 1), and H = q if q is one of
{2160, 1440, 1080, 720, 480, 360}:

| mode | H valid | otherwise |
|---|---|---|
| best | `bestvideo[height<=H][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=H]+bestaudio/best` | `bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best` |
| any | `bestvideo*+bestaudio/best` | same |
| progressive | `best[height<=H]/best` | `best[height<=1080]/best` |

Audio post-processing: `flac` → `flac 0`, `m4a` → `m4a 256`, anything else (`audio`, `mp3`) → `mp3 320`.

**YT Music** (`DownloadYtMusicAsync`): `ytmusic_flac` → `flac 0`; `ytmusic_m4a` or `m4a` → `m4a 256`;
anything else → `mp3 320`. Note that `flac`, `mp3` and `best` with a YT Music URL become **mp3 320**.
Two attempts: if the first exits ≠ 0, `CleanStemFiles`, `ct.ThrowIfCancellationRequested()`, emit
`"{p}Retrying music download..."`, wait 1.5 s, retry. The stderr buffer is **not cleared** between attempts.
The result file is found with images skipped.

**Fingerprint** (`ApplyFingerprintAsync`): output is `{dir}\{nameWithoutExt}_fp.mp4`.

- `metadata`: stream copy (§5.6). Exit 0 and the file exists → output; else delete the output and return the **input**.
- Unknown method → `standard`. Presets:

| method | scale | vf extra | af | crf | preset | audio |
|---|---|---|---|---|---|---|
| light | 1.003 | – | – | 23 | veryfast | copy |
| standard | 1.005 | `eq=brightness=0.006:saturation=1.01` | `atempo=1.002` | 21 | fast | aac 128k |
| strong | 1.010 | `eq=brightness=0.012:saturation=1.02:contrast=1.01,hue=h=1.5` | `atempo=1.005` | 19 | medium | aac 128k |

- ffprobe (§5.5). Width or height null or 0 → return the input unchanged.
- `sw = ((int)Math.Round(w*scale) / 2) * 2`, same for `sh`. `Math.Round` is **banker's rounding**.
- `vf = "scale={sw}:{sh}:flags=fast_bilinear,crop={w}:{h}"` plus `",{extra}"`.
- With `useGpu`, call `MediaTools.GetHardwareEncoderAsync` (NVENC/cuda → QSV/qsv → AMF/d3d11va; first one that encodes a test frame; cached per process).
- Encode (§5.7). On exit ≠ 0 with a hardware encoder and not cancelled: delete the output, emit `"{p}GPU encoder failed, retrying on CPU..."` (50), and re-encode on the CPU.
- Final exit ≠ 0 or cancelled → delete the output and return the **input**, otherwise the output if it exists, else the input.
  Cancelling during an encode throws (`WaitForExitOrKillAsync` kills ffmpeg) and the job ends `cancelled`. The post-check only
  matters when ffmpeg exited just before the cancel was seen: then the input is kept and the job continues to `completed`.

**Cloud providers** (`CloudStorageService.UploadAsync`): a missing local file gives
`"Local file does not exist: {path}"`. Provider key lower-cased (null → `gdrive`). OCE →
`"Upload cancelled by user"`; any other exception → `ex.Message` (both as `Success=false`).

| provider | target | progress messages (start at 5) | success message |
|---|---|---|---|
| gdrive | `{configured (if exists, trimmed) or detected}\VideoConverter\{unique name}` | `Uploading to Google Drive...` / `Uploading to Google Drive... {pct:F0}%` | `Uploaded to Google Drive ({name})` |
| dropbox, token set | `POST content.dropboxapi.com/2/files/upload`, path `/VideoConverter/{name}`, mode add, autorename; then try `create_shared_link_with_settings` | `Connecting to Dropbox API...` / `Uploading to Dropbox... {pct:F0}%` | `Uploaded to Dropbox: {path_display}` (Url = share link or null) |
| dropbox, folder | `{configured or detected}\VideoConverter\…` | `Syncing to Dropbox folder...` / `Syncing to Dropbox... {pct:F0}%` | `Synced to Dropbox ({name})` |
| onedrive | `{configured or %OneDrive% / %OneDriveConsumer% / %USERPROFILE%\OneDrive}\VideoConverter\…` | `Uploading to OneDrive...` / `Uploading to OneDrive... {pct:F0}%` | `Uploaded to OneDrive ({name})` |
| webhook | multipart `POST` to the URL (Discord: `payload_json` + `files[0]`; other: `file`) | `Preparing Webhook upload...` / `Sending to Webhook... {pct:F0}%` | `Successfully sent to Webhook!` (Url = first attachment URL or the webhook URL) |
| custom | `{custom}\{unique name}` (no subfolder) | `Copying to custom cloud folder...` / `Copying to cloud folder... {pct:F0}%` | `Saved to cloud folder ({name})` |

Progress is throttled so it fires only when the integer percent changes. Copy uses a 1 MiB buffer;
on failure the half-written target is deleted. HTTP streaming uses a 64 KiB buffer. Unique name:
`{name}`, then `{stem} (1){ext}`, `(2)`, …

Folder detection (cached 30 s):
- Google Drive: for each drive that is not a CD-ROM and is ready, `{root}\My Drive` or `{root}\Meine Ablage`, or the drive root if its volume label contains "Google Drive". Then `%USERPROFILE%\Google Drive[\My Drive]`.
- Dropbox: `info.json` (`personal.path`, then `business.path`) in `%APPDATA%\Dropbox`, then `%LOCALAPPDATA%\Dropbox`, then `%USERPROFILE%\Dropbox`.

### 4.5 Subtitles

`python -c <script> {backendDir} {videoPath} {modelSize} {language or "auto"}` (§5.9), no timeout.
- `python` = the Python-with-yt-dlp from §4.1 (or `python`).
- `backendDir` = `{sol}\backend` if it exists, else `{appDir}\backend`.
- The whole trimmed stderr is logged.
- If stdout contains `__SRT_BEGIN__`, return the trimmed text after it.
- Otherwise: exit ≠ 0 → throw the last non-empty trimmed stderr line, or `"Subtitle generation failed (exit code N)."` Exit 0 → `""`.

---

## 5. External process calls

Command lines are shown exactly as Native builds them. `{X}` = substitution, `[...]` = conditional.
Native builds most of them as **one raw string** (`ProcessStartInfo.Arguments`), quoting by hand with
`"`. Calls marked *(ArgumentList)* use .NET's standard argv quoting (`PasteArguments`). The runner is
`{exe} = yt-dlp.exe path | python path | bare name`, and `{extra}` = `-m yt_dlp` for the python runner.

### 5.1 Metadata (90 s)
```
{exe} [{extra} ][--ffmpeg-location "{ffmpegDir}" ][--no-playlist ][--cookies "{cookies}" ]-J --skip-download --no-warnings "{cleanUrl}"
```
`--ffmpeg-location` only when the ffmpeg path is an existing file. `--no-playlist` when the URL contains `watch?v=` or `/watch?`.

### 5.2 Video / audio download (no timeout; killed on cancel)
```
{exe} [{extra} ][--ffmpeg-location "{ffmpegDir}" ][--cookies "{cookies}" ]--newline -f "{format}" -o "{dir}\{stem}.%(ext)s" [--merge-output-format mp4 ][--extract-audio --audio-format {mp3|m4a|flac} --audio-quality {320|256|0} ]--no-playlist --retries 10 --fragment-retries 15 --extractor-retries 5 --socket-timeout 20 --windows-filenames "{url}"
```

### 5.3 YT Music download
```
{exe} [{extra} ][--ffmpeg-location "{ffmpegDir}" ][--cookies "{cookies}" ]-f "bestaudio/best" --extract-audio --audio-format {fmt} --audio-quality {q} --embed-thumbnail --add-metadata --write-thumbnail --postprocessor-args "ffmpeg:-id3v2_version 3" -o "{dir}\{stem}.%(ext)s" [--no-playlist ]--retries 5 --fragment-retries 10 --extractor-retries 3 --socket-timeout 15 --windows-filenames --newline "{url}"
```

### 5.4 Playlist (120 s)
```
{exe} [{extra} ][--ffmpeg-location "{ffmpegDir}" ]--flat-playlist --playlist-end {maxItems} -J --skip-download --no-warnings --user-agent "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36" "{cleanUrl}"
```

### 5.5 ffprobe (60 s)
```
{ffprobe} -v quiet -print_format json -select_streams v:0 -show_streams -show_format "{path}"
```
`format.duration` comes as a string or a number. Width and height come from the first stream with `codec_type == "video"`.

### 5.6 ffmpeg, metadata-only fingerprint (no timeout)
```
{ffmpeg} -y -hide_banner -loglevel error -i "{in}" -c copy -map_metadata -1 -movflags +faststart "{out}"
```

### 5.7 ffmpeg, filter fingerprint *(ArgumentList)*
```
-y -hwaccel {cuda|qsv|d3d11va} -i {in} -vf {vf} [-af {af}] {encoder} {audio} -threads 0 -map_metadata -1 -movflags +faststart -progress pipe:1 -nostats -loglevel error {out}
```

| encoder | arguments |
|---|---|
| h264_nvenc (hwaccel cuda) | `-c:v h264_nvenc -rc vbr -cq {crf} -preset {p1\|p4\|p5} -bf 2 -b_ref_mode middle -rc-lookahead 20` |
| h264_qsv (hwaccel qsv) | `-c:v h264_qsv -global_quality {crf} -preset {veryfast\|fast\|medium}` |
| h264_amf (hwaccel d3d11va) | `-c:v h264_amf -qp_i {crf} -qp_p {crf} -quality {speed\|balanced\|quality}` |
| CPU (hwaccel d3d11va) | `-c:v libx264 -crf {crf} -preset {preset} -pix_fmt yuv420p` |

Audio: `-c:a copy` (light) or `-c:a aac -b:a 128k`.

### 5.8 Hardware encoder probe (15 s; no PATH tweak, no PYTHON env)
```
{ffmpeg} -hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.1 -frames:v 1 -c:v {h264_nvenc|h264_qsv|h264_amf} -f null -
```

### 5.9 Python helpers *(ArgumentList)*
```
{py} -c "import sys; sys.path.insert(0, sys.argv[1]); from subtitler import generate_subtitles; srt = generate_subtitles(sys.argv[2], sys.argv[3], None if sys.argv[4] == 'auto' else sys.argv[4]); print('__SRT_BEGIN__'); print(srt)" {backendDir} {videoPath} {modelSize} {lang|auto}
{py} -c "import sys, json; sys.path.insert(0, sys.argv[1]); from downloader import fetch_playlist_entries; print('__PL_JSON__' + json.dumps(fetch_playlist_entries(sys.argv[2], int(sys.argv[3]))))" {backendDir} {cleanUrl} {maxItems}     (120 s)
```

### 5.10 Probes (`TestExecutable`, 3.5 s)
`{yt-dlp.exe} --version`, `{python} -m yt_dlp --version`, `py -3.12|-3.11|-3.10 -m yt_dlp --version`,
`python -m yt_dlp --version`, `{ffmpeg} -version`.

### 5.11 Deno
Never invoked directly; see D4. yt-dlp finds it through the `PATH` prepend.

---

## 6. Output and progress parsing

### 6.1 yt-dlp progress (stdout, `--newline`)
Regex (case-insensitive): `\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+\w+)(?:\s+at\s+([\d.]+\w+/s))?(?:\s+ETA\s+([\d:]+))?`
- pct = `double.TryParse(g1, NumberStyles.Any, InvariantCulture)`; no match or parse failure → no event.
- speed = g3 or null; eta = `ParseEta(g4)`: `m:s` → m·60+s, `h:m:s` → h·3600+m·60+s, otherwise null (`int.TryParse` per part).
- Message `"{p}Download {Video|Audio|Music}: {pct:F1}%"` (current culture).
- Lines are .NET `ReadLine` lines (split at `\r`, `\n`, `\r\n`); whitespace-only lines are ignored. Each non-blank stdout line is also echoed to the console.

### 6.2 yt-dlp failure text
1. stderr: all non-blank lines, each trimmed, joined with `\r\n` (`AppendLine`), then trimmed.
2. If that is empty: all stdout if it contains `ERROR:` or `Traceback` (case-insensitive); else exit 9009 → `"yt-dlp or ffmpeg command was not found or failed to spawn (exit code 9009). Runner: {exe}"`; else `"yt-dlp exited with error code {code}"`.
3. The result is replaced if it contains (case-insensitive) `Video unavailable` / `This video is not available` / `Private video` → `"Video is unavailable, private, or region-restricted."`, or contains `Sign in to confirm you’re not a bot` (curly apostrophe) / `confirm you're not a bot` → `"YouTube verification required. For restricted videos, you can configure an optional cookies file in Settings → Advanced → Backend."`
4. YT Music: if stderr is empty → `"YT Music download failed (exit code {code})"`. Same mapping but with `"Music track is unavailable, private, or region-restricted."` and `"…For restricted tracks, you can configure…"`.

### 6.3 ffmpeg `-progress pipe:1`
Only lines starting with `out_time_us=`. `long.TryParse`, value > 0 and duration > 0 →
`frac = min(us / 1e6 / duration, 1)`, emit processing `50 + frac*50`, `"{p}Encoding video: {frac*100:F0}%"`.
stderr is drained and discarded.

### 6.4 Text helpers
- `CleanUrl`: remove `[​-‏‪-‮⁠⁦-⁩﻿­͏؜ᅟᅠ឴឵᠎￰-￸￺-�]`, then `Trim()`.
- `SanitizeFilename`: `[<>:"/\\|?*\x00-\x1f]` → `_`; `Trim(' ', '.')`; reserved names `^(CON|PRN|AUX|NUL|COM\d|LPT\d)$` (case-insensitive) get a `_` prefix; truncated to **80 UTF-16 code units**; empty → `"video"`.
- `FindDownloadedFile(dir, stem)`: `Directory.GetFiles(dir, "{stem}.*")` in enumeration order. Skip `.part .ytdl .temp .aria2` (and `.jpg .jpeg .webp .png` for music). Return the first one that exists; errors → null.
- `CleanStemFiles(dir, stem, exclude)`: every file in `dir` whose name starts with `stem` (ordinal, case-insensitive), except `GetFullPath(exclude)`. Deleted with `SafeDelete`: 5 tries with 50 ms sleeps, errors ignored.

---

## 7. Files and network endpoints

### 7.1 Read
| What | Path / format |
|---|---|
| Settings | `SettingsService.Current` (in memory; persisted as `%APPDATA%\VideoConverter\settings.json`, snake_case JSON). Keys the engine reads: `cookies_file_path`, `cloud_provider`, `gdrive_folder_path`, `dropbox_folder_path`, `dropbox_api_token`, `onedrive_folder_path`, `webhook_url`, `custom_cloud_folder_path` |
| Cookies | Netscape cookie file, passed to yt-dlp (**yt-dlp also writes it back**) |
| Dropbox client | `%APPDATA%\Dropbox\info.json`, `%LOCALAPPDATA%\Dropbox\info.json`: `{"personal":{"path":…},"business":{"path":…}}` |
| Python helpers | `{backend}\subtitler.py`, `{backend}\downloader.py` (executed, not parsed) |
| Registry | User/Machine `PATH` (via `Environment.GetEnvironmentVariable(…, User/Machine)`, REG_EXPAND_SZ expanded) |

### 7.2 Written
| What | Path |
|---|---|
| Downloads | `{downloadDir}\{stem12}.{ext}` plus yt-dlp intermediates (`.part`, `.ytdl`, `.f{id}.{ext}`, `.temp`, thumbnails for YT Music) |
| Fingerprint | `{dir}\{stem}_fp.mp4` (the original is deleted on success) |
| Final file | `{downloadDir}\{SanitizeFilename(title)}[ (n)].{ext}`, or the stem name when there is no title |
| Temp job dir | `%TEMP%\ReelsConverter\jobs\{jobId}\` (old product name kept on purpose; deleted after a temp-only upload) |
| yt-dlp self-heal | `%LOCALAPPDATA%\VideoConverter\bin\yt-dlp.exe` (+`.tmp`) |
| Cloud targets | `{gdrive|dropbox|onedrive root}\VideoConverter\{name}`, `{custom}\{name}`, Dropbox API `/VideoConverter/{name}` |

`client_secret*.json` and `token.json` are **not** touched by Native. Legacy and Ferrite read `client_secret*.json` and
read/write `token.json` in the same format (§9.1).

### 7.3 Network
| Call | Timeout | Notes |
|---|---|---|
| YouTube oEmbed `GET` | 8 s | static `HttpClient` |
| yt-dlp self-download `GET` | 30 s (whole body) | UA set |
| Dropbox upload `POST` | 10 min | `Authorization: Bearer`, `Dropbox-API-Arg` (System.Text.Json default escaping), `application/octet-stream`, explicit length |
| Dropbox share link `POST` | 10 min | JSON `{path}` |
| Webhook multipart `POST` | 10 min | file part `video/mp4` even for audio |

All of these use `HttpClient` defaults: HTTP/1.1, system proxy, SChannel TLS, no `Accept-Encoding`, no User-Agent (except the yt-dlp download).

---

## 8. Error messages and edge cases

**Texts the UI can show from Native** (job `error`, unless noted otherwise):

- `YouTube upload requires the Ferrite or Legacy engine.` with the message in §3.3 (Native only; was "Upload requires Legacy (Python) engine." before 27H1.19)
- `Downloaded media file could not be located.`
- yt-dlp texts from §6.2 (raw stderr, 9009 text, exit-code text, the three mapped texts, the YT Music variants)
- Process start failure (`Win32Exception`): `An error occurred trying to start process '{FileName}' with working directory '{cwd}'. {localized system message}`. Exact format to be verified against .NET 10 in the golden tests.
- `TimeoutException`: `{exe name} did not finish within {ms} ms.` (only reachable from subtitles and the metadata-only fingerprint, which have no timeout, so effectively unreachable)
- Cloud: `Local file does not exist: {path}` · `Unsupported cloud provider: {key}` · `Upload cancelled by user` · `Google Drive folder not found. Please verify Google Drive for Desktop is running or configure a folder in Setup.` · `Dropbox API Error ({HttpStatusCode name}): {body}` · `Dropbox is not configured. Please enter a Dropbox API token or install the Dropbox desktop app.` · `Microsoft OneDrive folder not found. Please ensure OneDrive is running or configure a folder in Setup.` · `Webhook URL is empty. Please enter a valid Discord or custom Webhook URL in Cloud Setup.` · `Webhook error ({HttpStatusCode name}): {body}` · `Custom cloud folder does not exist or is not configured.` · `Failed to upload to {display}` · .NET IO/HTTP exception messages
- Directory or path errors from `GetFullPath` / `CreateDirectory` (.NET exception messages)
- Subtitles (exception): the last stderr line, or `Subtitle generation failed (exit code N).`

**Edge cases Ferrite must reproduce**

1. **Cancel during cloud upload** → status `error`, `"Error: Upload cancelled by user"`, **not** `cancelled`. The HttpClient 10 min timeout surfaces the same way, because it is an OCE.
2. A JSON field of the wrong kind in yt-dlp output throws. For example `"duration": null` makes `TryGetDouble` throw; a numeric `title` makes `GetString` throw. In **metadata** this makes the whole result `null`, so the oEmbed fallback runs. In the **playlist** it fails stage 1 entirely, so stages 2 and 3 run.
3. `tags: null` → hashtag extraction from the description.
4. Playlist entry with a non-http `url` and an empty id → `https://www.youtube.com/watch?v=`.
5. `py` launcher: found via `py -3.12 -m yt_dlp`, but later run as `py -c …` / `py -m yt_dlp …` **without** `-3.12`.
6. Subtitles use the Python that has yt-dlp, which does not necessarily have faster-whisper.
7. YT Music with `quality` `flac`/`mp3`/`best` → mp3 320.
8. YT Music error text contains the stderr of **both** attempts.
9. The video path retries a process start failure 3 times (it is caught). YT Music does not, because its start failure propagates.
10. ffprobe missing or failing → fingerprint silently skipped, original file kept.
11. GPU encode failure → CPU retry. A CPU failure keeps the original, and the job still **completes**.
12. Fingerprint is never applied to audio jobs.
13. Rename: `File.Move` without overwrite; a collision loop with ` (n)`; rename errors swallowed.
14. `EnsureFfmpegReadyAsync` result ignored; the job continues without ffmpeg.
15. A job id passed to `StreamJobAsync` after 2 minutes, or an unknown one, gives an empty stream.
16. `CreateJobAsync` ignores an already-cancelled `ct`.
17. Metadata/oEmbed: cancelling during the oEmbed request returns `null` instead of throwing.
18. Temp-dir deletion only if the path contains `ReelsConverter`.
19. A sanitized title can end in an **unpaired UTF-16 surrogate** (80-code-unit cut through an emoji). The file name and `file_path` carry it.
20. Native's upload-mode check runs before `EnsureInitialized`, so the rejection is instant. (Ferrite runs the upload pipeline instead, §9.1.)

---

## 9. Ferrite-only behaviour

### 9.1 YouTube upload (new in Ferrite; Native rejects upload jobs)

Pipeline for `mode = "upload"`:
1. Download into `%TEMP%\ReelsConverter\jobs\{jobId}` with the normal download rules (quality `best` when none is given).
2. Fingerprint when enabled.
3. Upload.
4. Delete the temporary file and folder.

Phases are counted like Native's: download, +fingerprint, +upload.

| Step | Behaviour |
|---|---|
| Credentials folder | `GetBackendDir()` (shared with Legacy) when it exists, otherwise `%APPDATA%\VideoConverter` (installed builds have no backend folder) |
| Client file | First `client_secret*.json` in that folder (directory order, like Legacy's `os.listdir`); must contain `installed` or `web` |
| Token | `token.json` in google-auth's authorized-user format (`token, refresh_token, token_uri, client_id, client_secret, scopes, universe_domain, account, expiry`; `expiry` as `datetime.isoformat()+"Z"`). Read and written compatibly with Legacy (round-trip unit test) |
| Validity | Valid while `now < expiry − 3 min 45 s` (google-auth's refresh threshold) |
| Refresh | `grant_type=refresh_token`. `invalid_grant` starts a new sign-in instead of failing |
| Sign-in | Installed-app flow: loopback listener on `localhost` (IPv4 + IPv6), browser opened with `ShellExecute`, PKCE S256, `access_type=offline`, `prompt=consent` (guarantees a refresh token). The URL is also written to the console. The status shows *"Waiting for Google sign-in in your browser..."*. Concurrent uploads share one sign-in |
| Request | Resumable `videos.insert` with `part=snippet,status`, 8 MiB chunks; title cut to 100 characters, Legacy's tag rules (strip `#`, ≤ 30 characters each, ≤ 500 total), `categoryId 22`, `selfDeclaredMadeForKids false`, privacy from the UI (default `public`) |
| `#Shorts` | Prepended when missing **if** Settings → "Auto add #Shorts" is on (Legacy always prepends; the setting defaults to on, so the default behaviour matches) |
| Progress | `uploading`, *"[n/m] Uploading to YouTube... {pct}%"*, throttled to whole percent |
| Robustness | 401 on session start → refresh once and retry; 5xx or connection loss on a chunk → resume from the server's offset, up to 3 times (1/2/4 s) |
| Result | `video_id`, `url` (`https://youtube.com/shorts/{id}`, like Legacy), `title`, plus `cloud_provider = "YouTube"`, `cloud_destination`, `cloud_url` so the existing "Open … Link" button and notification work |
| Errors | `YouTube upload: client_secret*.json was not found in {dir}`, *client_secret.json does not contain valid JSON …*, `Google sign-in failed ({code}): {description}`, `YouTube API error {status} ({reason}): {message}`. Each has its own category in the error window |

### 9.2 Sidecar lifecycle

- `ferrite.exe` starts lazily on the first call, inherits the app's environment and current directory, and gets the
  host facts in `hello` (§12).
- Calls cancel through the caller's `CancellationToken`, and jobs through `cancel_job`, exactly like Native.
- **Crash**: unfinished calls fail with *"Ferrite backend process exited unexpectedly (exit code N)."*, and unfinished jobs
  end with `error` and the same text. The next call starts a new sidecar when "Auto-restart backend" is on.
- **Dispose / app exit**: `shutdown` (or stdin EOF when the app dies) runs Native's `Dispose` semantics: cancel every
  job, kill its process tree, let the pipelines report `cancelled`, then exit. After 8 s the app kills the sidecar.
- The sidecar's standard handles are made non-inheritable, so yt-dlp and ffmpeg never hold the protocol pipes.

---

## 10. .NET and Win32 semantics reproduced in Rust

| Behaviour | Rust (`ferrite-engine`) | Verified by |
|---|---|---|
| `{x:F1}` / `{x:F0}` with the current culture (`45,3`), ties to even | `dotnet::fmt::fixed` + culture sent in `hello` | golden `fixed` (30 cases), e2e messages |
| `Math.Round` (banker's) for scale sizes | `fingerprint::scaled_even` (`round_ties_even`) | unit tests, byte-identical fingerprint output |
| `JsonDocument` strictness and `JsonElement` throw rules (`GetString` on numbers, `TryGetDouble` on null, last duplicate wins, lazy surrogate errors, ±∞) | `dotnet::json` | unit tests, golden `metadata` (15 fixtures incl. real yt-dlp JSON), `playlist` |
| System.Text.Json deserialization of the Python playlist fallback | `playlist::stj_deserialize_playlist` | unit tests, e2e `playlist_youtube` (fallback stage) |
| `Path.*` (Combine, GetDirectoryName, GetExtension, GetFullPath …) | `dotnet::path` (UTF-16) | unit tests |
| `File.Exists`/`Directory.Exists`, `File.Move` without overwrite, `File.Delete`, `Directory.CreateDirectory`, recursive delete, `GetFiles("stem.*")` wildcard | `dotnet::fs` (Win32 APIs) | unit tests, golden `io_errors`, e2e file lists |
| .NET IO exception texts (`Win32Marshal`) and `FormatMessage` | `dotnet::ioerr`, `dotnet::win` | golden `io_errors` |
| `ProcessStartInfo.Arguments` / `ArgumentList` quoting (`PasteArguments`) | `dotnet::cmdline` + `raw_arg` | unit tests, byte-identical encodes (same ffmpeg arguments) |
| `Process.Start` failure text | `ioerr::process_start_failed` | unit test (format verified against .NET 10) |
| `CreateProcess` executable search for bare names | `proc::resolve` | unit test |
| `StreamReader` decoding (BOM, invalid UTF-8, `\r`/`\n`/`\r\n`) | `proc::decode` | unit tests |
| `Process.Kill(entireProcessTree: true)` | `proc::killtree` | e2e cancel |
| `Environment.GetFolderPath`, registry `PATH` (REG_EXPAND_SZ) | `dotnet::win` | unit tests |
| `HttpStatusCode` names, STJ escaping, `Uri.EscapeDataString`, `MultipartFormDataContent` layout | `dotnet::http` | golden (500 status codes, 5 strings, 4 multipart bodies), e2e webhook body identical |
| `HttpClient` timeouts (10 min up to headers = cancellation) | `cloud::providers::with_timeout` | code review against `CloudStorageService` |
| UTF-16 strings with unpaired surrogates (80-unit title cut) | `OsString` + `{"$utf16":[…]}` on the wire | golden `sanitize`, unit tests |

---

## 11. File map: Native C# → Rust modules

`backend-ferrite/crates/ferrite-engine/src/` (library) and `crates/ferrite-sidecar/src/` (`ferrite.exe`):

| Native C# | Rust |
|---|---|
| `NativeEngineService` fields, `JobContext`, `EmitStatus`, `CancelJobAsync`, `Dispose`, `ForgetJobLaterAsync` | `jobs.rs`, `engine.rs` |
| `EnsureInitializedAsync`, `ResolveBinaries`, `TestExecutable`, `FindWorkingPythonWithYtDlp`, `EnsureStandaloneYtDlp` | `binaries.rs`, `engine.rs` |
| `EnsureFfmpegReadyAsync` | `pipeline.rs::ensure_ffmpeg_ready` (dialog via host call) |
| `ConfigureProcessStartInfo`, `WaitForExitOrKillAsync`, `MediaTools.RunProcessAsync` | `proc/env.rs`, `proc/runner.rs` |
| `ResolveCookiesFile` | `cookies.rs` |
| `FetchMetadataAsync`, `FetchYTMusicMetadataAsync`, `ExtractMetadataViaYtDlpAsync`, `FetchFallbackMetadataAsync` | `metadata.rs` |
| `FetchPlaylistAsync` | `playlist.rs` |
| `ExecuteJobPipelineAsync` | `pipeline.rs` |
| `RetryStrategies`, `DownloadVideoWithRetriesAsync`, `RunYtDlpDownloadAsync`, `GetFormatString` | `download.rs` |
| `DownloadYtMusicAsync` | `ytmusic.rs` |
| `FpMethods`, `HwPresets`, `ApplyFingerprintAsync`, `ProbeVideoAsync` | `fingerprint.rs` |
| `MediaTools.GetHardwareEncoderAsync`, `CanEncodeAsync` | `hwenc.rs` |
| `GenerateSubtitlesAsync`, `GetBackendDir`, `LastLine` | `subtitles.rs`, `locate.rs`, `util.rs` |
| `YtDlpProgressRegex`, `ParseEta`, `ProgressNumberRegex` | `proc/progress.rs` |
| `CleanUrl`, `IsYtMusicUrl`, `SanitizeFilename`, `FindDownloadedFile`, `CleanStemFiles`, `SafeDelete` | `util.rs` |
| `MediaTools.FindSolutionDir`, `GetFfmpegCandidates`, `FindFfmpeg`, `AddPathCandidates`; `FFmpegInstallerService.GetDefaultInstallDir` | `locate.rs` |
| `CloudStorageService` (upload half, detection, copy, throttling, progress body) | `cloud/{mod, detect, copy, providers}.rs` |
| `Models.cs` DTOs | `models.rs` |
| `AppSettings` (fields the engine reads) | `settings.rs` |
| `IEngineService` / `EngineProvider.EmitOutput` | `host.rs` + sidecar `main.rs`, `protocol.rs` |
| — (new) YouTube upload | `youtube/{mod, oauth, upload, time}.rs` |
| — .NET/Win32 compatibility | `dotnet/{cmdline, fmt, fs, http, ioerr, json, netstr, path, text, win}.rs`, `proc/{decode, killtree, resolve}.rs` |

Stays in C#: `FFmpegInstallerService` (dialog and installer, reached through the `ensure_ffmpeg` host call), `CloudStorageService.GetProviderStatus` (UI only), `MediaTools.FindFfmpeg`/`GetHardwareEncoderAsync` for the Editor's export.

---

## 12. Interop: sidecar protocol

Option B as proposed: `ferrite.exe` exchanges newline-delimited UTF-8 JSON with the app; stdout carries only protocol
messages. Strings that contain unpaired UTF-16 surrogates travel as `{"$utf16":[…code units…]}` in both directions
(System.Text.Json cannot read or write them).

| Direction | Message | Purpose |
|---|---|---|
| app → F | `{"type":"hello","protocol":1,"base_dir","process_path","cwd","culture":{"name","decimal_separator","negative_sign","positive_infinity","negative_infinity","nan"}}` | What Native read from its own process |
| F → app | `{"type":"ready","protocol":1,"version"}` | |
| app → F | `{"type":"call","id","method","params"}` | `wait_for_health`, `fetch_metadata`, `fetch_ytmusic_metadata`, `fetch_playlist`, `generate_subtitles`, `create_job`, `cancel_job` |
| app → F | `{"type":"cancel","id"}` | Cancels call `id` (the caller's `CancellationToken`) |
| F → app | `{"type":"result","id","ok":true,"value"}` / `{"ok":false,"error":{"kind":"cancelled"\|"error","message"}}` | → return value, `OperationCanceledException` or `Exception(message)` |
| F → app | `{"type":"job_status","job_id","status":{JobStatus}}` | One per `EmitStatus`; result keys keep insertion order |
| F → app | `{"type":"job_complete","job_id"}` | The channel's `TryComplete()` |
| F → app | `{"type":"log","line"}` | `EngineProvider.EmitOutput` |
| F → app | `{"type":"host_call","id","method":"get_settings"\|"ensure_ffmpeg"}` | `SettingsService.Current` at that moment; FFmpeg dialog (answer includes the app's new `PATH`) |
| app → F | `{"type":"host_result","id","value"}` | |
| app → F | `{"type":"env","path"}` | The app's `PATH` changed (FFmpeg install/uninstall) |
| app → F | `{"type":"shutdown"}` | `Dispose`; stdin EOF does the same |

`create_job` answers before its pipeline starts, so a job's first status never arrives before its id.

---

## 13. App changes (C#)

### 13.1 Engines
| File | Change |
|---|---|
| `Services/FerriteBackend.cs` (new) | `IEngineService` over the sidecar; per-job channels (Native semantics); host calls; crash and restart handling; `IEngineDiagnostics.LastStartError` |
| `Services/EngineProxy.cs` (new) | The stable `IEngineService` the UI holds; pins engines per call/job |
| `Services/EngineProvider.cs` | Three engines, keys/display names, deferred switching, `EngineSwitchDeferred`; start errors surface as exceptions |
| `Services/BackendService.cs` (Legacy) | Cloud upload jobs fail cleanly (D12). Nothing else changed |
| `Services/NativeEngineService.cs` | Only the upload-rejection text (D11) |
| `Services/FFmpegInstallerService.cs` | `ProcessPathChanged` event so Ferrite mirrors `PATH` |
| `Services/BuildInfoService.cs` | 27H1.19, changelog, three-way engine name, `BuildNumberWithEngine` |
| `VideoConverterUI.csproj` | Builds `ferrite.exe` with cargo (skippable with `/p:SkipFerriteBuild=true`) and copies it to the build/publish output |
| `installer/Package.wxs` | Installs `ferrite.exe` |

### 13.2 Selector
`SettingsWindow`: third option in the Execution Engine combo (Native, Ferrite, Legacy), a Ferrite entry in the help bubble, a toast after
switching (applied or deferred), the build number with the engine (`27H1.19 (108cf6ad) · Ferrite`), and an `InitialTab` so other windows can
open a specific tab. Strings: `EngineFerrite*`, `EngineSwitch*` and the renamed `EnginePython` (= "Legacy"), in en/de/ru.

### 13.3 Error window overhaul
- `Services/ErrorAdvisor.cs` (new): 54 categories that cover errors from Native, Ferrite, Legacy, yt-dlp, FFmpeg, the cloud
  providers, YouTube and Windows. Each category has a title, an explanation and 1–3 concrete fix steps in en/de/ru
  (`ErrAdv_*` strings), plus a main action.
- `MainWindow` error popup redesigned. It shows:
  - the category title and the engine with the time
  - what happened
  - numbered fixes
  - the actual error message (selectable, monospace)
  - a context button (Restart Backend / Open Settings on the right tab / Install FFmpeg / Cloud Setup), Copy and Logs

  It opens by itself when a job, a subtitle run or the engine start fails. "Copy" includes steps, raw message, engine, build and category.
  Metadata failures keep the real reason.
- Multi Downloader: failed items show the error title and the first fix; the tooltip has all steps and the full message.
- Editor: subtitle errors show the first fix under the message.
- `parity -- advice` checks 60 real error texts × 3 languages against the expected categories.

---

## 14. Feature matrix

✅ works · ☑️ fails cleanly (clear message in the error window) · ❌ not available

| Feature | Legacy | Native | Ferrite |
|---|---|---|---|
| Engine start / health | ✅ | ✅ | ✅ |
| Metadata (yt-dlp `-J` + oEmbed fallback) | ✅ | ✅ | ✅ identical to Native |
| YT Music metadata | ✅ | ✅ | ✅ identical |
| Playlist (3 stages) | ✅ | ✅ | ✅ identical |
| YouTube / TikTok / Instagram / generic download | ✅ | ✅ | ✅ identical |
| Quality best / 2160 … 360 | ✅ | ✅ | ✅ identical |
| Audio MP3 320 / M4A / FLAC | ✅ | ✅ | ✅ identical (same file hashes) |
| YT Music 320k / m4a / flac with tags + cover | ✅ | ✅ | ✅ identical (same file hashes) |
| Fingerprint light / standard / strong / metadata | ✅ | ✅ | ✅ identical (same file hashes) |
| GPU encode with CPU fallback | ✅ | ✅ | ✅ identical |
| Cancel | ✅ | ✅ | ✅ identical |
| Cloud: custom folder, temp-only | ☑️ D12 | ✅ | ✅ identical |
| Cloud: Google Drive / OneDrive / Dropbox (folder) | ☑️ D12 | ✅ | ✅ same code path as custom folder; "folder not found" identical |
| Cloud: Dropbox API | ☑️ D12 | ✅ | ✅ request format verified (golden), not run (no token) |
| Cloud: webhook (Discord / custom) | ☑️ D12 | ✅ | ✅ identical request body |
| YouTube upload (OAuth) | ✅ | ☑️ D11 | ✅ new (§9.1); files compatible with Legacy |
| Subtitles (faster-whisper) | ✅ | ✅ | ✅ identical |
| Cookies file | ✅ | ✅ | ✅ |
| Browser cookie probing | ❌ | ❌ | ❌ (by design) |
| Deno for yt-dlp | inherits `PATH` | ✅ | ✅ |
| yt-dlp self-download | ❌ | ✅ | ✅ |
| FFmpeg install prompt | auto-download | ✅ | ✅ (host call) |
| Dev console output | ✅ | ✅ | ✅ same lines |

---

## 15. Parity checklist

Legend: **[x]** verified (how: *unit* = Rust unit test, *golden* = C# fixture test, *e2e* = identical side-by-side run, *backends* = run through the app's engine switch); **[ ]** not verifiable on this machine (reason given).

### API
- [x] `EngineName` ("Ferrite Rust Media Engine") — *backends*
- [x] `WaitForHealthAsync` (waits for init, ignores `maxWait`, OCE on `ct`) — *backends*, *e2e* (every scenario starts with it)
- [x] `FetchMetadataAsync` — *golden* (15 fixtures), *e2e* `metadata_youtube`, `metadata_unsupported`
- [x] `FetchYTMusicMetadataAsync` — *e2e* `metadata_ytmusic`
- [x] `CreateJobAsync` (8-hex id, returns before the pipeline, ignores `ct`) — *e2e* all job scenarios, sidecar protocol test
- [x] `StreamJobAsync` (buffering, terminal statuses, unknown id) — *e2e*
- [x] `CancelJobAsync` (cancel + kill tree) — *e2e* `cancel_during_download`, driver test (cancelled in < 10 ms)
- [x] `GenerateSubtitlesAsync` — *e2e* `subtitles` (identical SRT)
- [x] `FetchPlaylistAsync` — *golden*, *e2e* `playlist_youtube` (Python fallback stage), `playlist_single_video`
- [x] `Dispose` (cancels jobs, `cancelled` reported) — driver test (shutdown during a job → `cancelled`)
- [x] Console output (`EmitOutput`): yt-dlp lines, `[stderr]` lines, phase lines with de-duplication — *e2e* console comparison
- [x] Host callback: FFmpeg prompt + re-resolve — code review against `EnsureFfmpegReadyAsync`; FFmpeg is installed here, so the dialog path was not triggered

### Core, config, process runner
- [x] Settings read at the same moments as Native (`get_settings`) — *e2e* cloud scenarios (in-memory settings changes seen by both engines)
- [x] `FindSolutionDir`, ffmpeg/ffprobe candidate order — *unit*
- [x] yt-dlp candidate order + `--version` test (3.5 s) — *unit* (`test_executable`), *e2e* (same runner found)
- [x] Python-with-yt-dlp lookup — *e2e* `subtitles`, `playlist_youtube` (same Python used)
- [x] yt-dlp self-download — code review (path, UA, 30 s, `.tmp` + move); not triggered (yt-dlp present)
- [x] Child environment (UTF-8, `PYTHONUTF8`, `PYTHONIOENCODING`, `PATH` prepend incl. Deno) — *unit*, *e2e*
- [x] Command-line building (raw + `PasteArguments`) — *unit*, *e2e* (byte-identical encodes prove identical ffmpeg arguments)
- [x] Exe search for bare names — *unit*
- [x] Output decoding and line splitting — *unit*
- [x] `RunProcessAsync` timeouts and timeout text — *unit*
- [x] Kill process tree — *unit*, *e2e*
- [x] Culture-formatted numbers — *golden* `fixed`, `progress`

### Progress parsing
- [x] yt-dlp progress regex, pct/speed/ETA — *golden* (10 real lines)
- [x] `ParseEta` — *golden*
- [x] ffmpeg `out_time_us` → 50…100 — *unit*, *e2e* fingerprint phases
- [x] Phase prefix `[n/m]` — *unit*, *e2e*
- [x] Clamp 0…100 — *e2e* (no value outside 0..100)

### Cookies
- [x] Cookies file resolution (blank / missing / directory / present) and placement in the three yt-dlp command lines — *unit*

### Download and quality
- [x] Upload-mode handling (Native rejects; Ferrite uploads) — *backends*
- [x] `isYtMusic` / `isAudio` / `hasFp` — *unit*, *e2e*
- [x] Download dir (temp `ReelsConverter` path / Downloads / `GetFullPath`) — *e2e* `cloud_custom_temp_only`
- [x] Format strings for every quality × mode — *golden* (90 combinations)
- [x] Retry strategies, backoff, retry messages — *unit*, *e2e* `unavailable_video`, `tiktok` (all three strategies, same messages)
- [x] Audio args mp3 320 / m4a 256 / flac 0 — *unit*, *e2e* (identical files)
- [x] YT Music args, 2 attempts, 1.5 s delay, accumulated stderr — *unit*, *e2e* (identical files)
- [x] yt-dlp error texts (video + music) — *unit*, *e2e* `unavailable_video`, `tiktok`
- [x] `FindDownloadedFile` (wildcard semantics, skipped extensions, images) — *unit* (matches .NET's result), *e2e*
- [x] `CleanStemFiles` / `SafeDelete` — *e2e* (identical leftover file lists)
- [x] Rename to sanitized title, ` (n)` collisions, no-overwrite move, UTF-16 cut — *golden* `sanitize`, *e2e*
- [x] Result dictionary keys, order and CLR types — *e2e* (string/bool, insertion order)
- [x] Completion / cancelled / error statuses + cleanup — *e2e*
- [x] Job forgotten after 2 min — code review (both sides)
- [x] TikTok, Instagram, YouTube, YouTube Music — *e2e* (TikTok fails identically: yt-dlp lacks its impersonation helper here)

### Subtitles
- [x] `python -c` call, marker parsing, empty result, error texts, stderr logging — *unit*, *e2e*

### Transform (fingerprint)
- [x] Metadata-only method — *e2e* `fp_metadata` (identical hash)
- [x] Presets and unknown-method fallback — *unit*, *e2e*
- [x] ffprobe call and parsing — *unit*, *e2e*
- [x] Scale math and `-vf` — *unit*, *e2e* (identical hashes)
- [x] Hardware encoder detection order and cache — *e2e* `fp_standard_gpu` (identical hash)
- [x] Encoder argument sets (NVENC / QSV / AMF / CPU) — *unit*, *e2e* (GPU + CPU)
- [x] GPU → CPU fallback message and retry — *unit* (code path); the GPU here encodes fine, so the fallback was not triggered live
- [x] Failure / cancel → original kept — *unit*

### Upload (cloud)
- [x] Provider display names — *unit*
- [x] Google Drive missing folder text — *e2e* `cloud_gdrive_missing`
- [x] Custom folder copy, progress messages, unique names — *e2e* `cloud_custom_folder` (identical hashes in the destination)
- [x] Temp-only cleanup (`is_temp`, directory guard) — *e2e* `cloud_custom_temp_only`
- [x] Webhook (Discord / custom) multipart body + attachment URL — *golden* (4 bodies), *e2e* `cloud_webhook` (identical request)
- [x] Error texts incl. `HttpStatusCode` names and IO texts — *golden*
- [x] Cancel/timeout during upload → `error` "Upload cancelled by user" — *unit* (`upload` maps cancellation)
- [ ] Google Drive / OneDrive / Dropbox desktop folders, live — not installed on this machine (same copy code path as the verified custom folder)
- [ ] Dropbox API upload + share link, live — needs a Dropbox token (headers and JSON body verified by *golden*)

### YouTube upload (Ferrite, new)
- [x] token.json google-auth format round trip, refresh threshold, client_secret parsing, PKCE, callback parsing — *unit*
- [x] Missing / invalid client_secret → clear error — *backends* (placeholder file here)
- [ ] Live sign-in and upload — needs real OAuth credentials (the files in `backend/` are placeholders)

### Interop and app
- [x] Sidecar launch, `hello`/`ready`, shutdown on `Dispose` / stdin EOF — driver tests, *e2e*
- [x] Sidecar crash → clean job errors, restart on the next call — *backends* (`ferrite.exe` killed during a job)
- [x] `PATH` mirroring — code review (`ProcessPathChanged`)
- [x] Backend selector (Native / Ferrite / Legacy) with existing controls and styles — app run with each engine (§16.4)
- [x] Strings in en / de / ru — `parity -- advice` (all categories resolve in all three languages)
- [x] Persisted in `execution_engine`; old settings files still load — default `native` unchanged
- [x] Switch rule while jobs run (deferred) — *backends*
- [x] Active backend shown next to the version — Settings → Info
- [x] Legacy reports unsupported cloud upload cleanly — *backends*
- [x] All three backends run every feature in §14 (or fail cleanly) — *backends*

---

## 16. Verification results

### 16.1 Build gates (2026-10-10)

| Gate | Result |
|---|---|
| `cargo build --release` | ✅ `ferrite.exe` 4.8 MB |
| `cargo clippy --workspace --all-targets -- -D warnings` | ✅ no warnings |
| `cargo test --workspace` | ✅ 90 unit tests (engine), 13 golden test groups, 2 sidecar tests |
| `dotnet build VideoConverter.slnx` | ✅ 0 warnings, 0 errors (includes the cargo build) |

### 16.2 Golden tests (C# fixtures → Rust)

`parity/fixtures/golden.json` is generated by `dotnet run --project backend-ferrite/parity -- golden` from the real C# code
(private members via reflection; metadata and playlist mapping by running `NativeEngineService` with yt-dlp replaced by
`type <fixture>`). `crates/ferrite-engine/tests/golden.rs` checks Ferrite against it. **13/13 groups pass**:

| Group | Cases |
|---|---|
| `GetFormatString` | 90 (15 qualities × audio/video × 3 modes) |
| `SanitizeFilename` | 16 (reserved names, control characters, 80-unit cut through an emoji, unpaired surrogate) |
| `ParseEta` | 10 |
| `CleanUrl` / `IsYtMusicUrl` | 6 |
| yt-dlp progress lines + culture message | 10 |
| Console phase keys | 5 |
| `F0`/`F1` formatting (de-DE) | 30 |
| System.Text.Json escaping | 5 |
| `Uri.EscapeDataString` | 3 |
| Webhook multipart bodies | 4 (byte-identical) |
| `HttpStatusCode` names | 500 |
| Metadata JSON → `MetadataResponse` | 15, incl. the real `yt-dlp -J` output, `duration: null` / `"12"` / `1e400`, duplicate keys, lone surrogates |
| Flat playlist JSON → `PlaylistInfo` | 3 |
| .NET IO exception texts | 3 |

### 16.3 Native vs. Ferrite, identical inputs (`parity -- e2e`)

Each scenario runs once through `NativeEngineService` and once through `FerriteBackend` (the real `ferrite.exe`), in the same
process, against the live services. Compared: the returned value, every phase of the status stream, the final status
(text, error, result keys/order/types), the console phase lines, all files left on disk (name, size, SHA-256), and the
webhook HTTP request. Paths, job ids, stems and multipart boundaries are normalised.

**27/27 identical.** (The first run found two real differences, both fixed: result keys were sorted instead of
insertion-ordered, which `serde_json/preserve_order` fixed; and the cancel test cancelled at a fixed delay, which raced with a
fast download, so it now cancels at the first progress event in both engines.)

| Scenario | Result | Details |
|---|---|---|
| metadata_youtube | ✅ identical | value {"title":"Me at the zoo","description":"Microplastics are accumulating in human brains at an alarming rate\nhttps://www.youtube.com/watch?v=0PT5c1z3LL8\n\n\u201CNanoplastics and Human Health\u201D with Matthew J Campen, PhD, MSPH\nhttps://www.youtube.com/watch?v=RRBN_4L09Mg\n\n00:00 Intro\n00:05 The… |
| metadata_ytmusic | ✅ identical | value {"title":"Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)","description":"The official video for \u201CNever Gonna Give You Up\u201D by Rick Astley. \n\nNever: The Autobiography \uD83D\uDCDA OUT NOW! \nFollow this link to get your copy and listen to Rick\u2019s \u2018Never\u2019… |
| metadata_unsupported | ✅ identical | value null |
| playlist_youtube | ✅ identical | value {"title":"youtube-dl public playlist","count":1,"entries":[{"id":"PL4lCao7KL_QFVb7Iudeipvc2BCavECqzc","title":"youtube-dl public playlist","url":"https://www.youtube.com/playlist?list=PL4lCao7KL_QFVb7Iudeipvc2BCavECqzc","duration":0,"uploader":"Sergey M.","thumbnail":""}]} |
| playlist_single_video | ✅ identical | value {"title":"Me at the zoo","count":1,"entries":[{"id":"jNQXAC9IVRw","title":"Me at the zoo","url":"https://www.youtube.com/watch?v=jNQXAC9IVRw","duration":19,"uploader":"jawed","thumbnail":"https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg?sqp=-oaymwEmCOADEOgC8quKqQMa8AEB-AG-AoAC8AGKAgwIABABGFUgWShlMA8… |
| video_best_title | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Zoo Clip.mp4 (533931 bytes, sha256 3FE549390A27A9C6) |
| video_720_no_title | ✅ identical | completed: Download and processing completed successfully.; 3 phases; {STEM}.mp4 (533931 bytes, sha256 3FE549390A27A9C6) |
| audio_mp3 | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Zoo Audio.mp3 (765007 bytes, sha256 83036A58B208D4D8) |
| audio_m4a | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Zoo Audio.m4a (309156 bytes, sha256 8790EE0C3E3E7072) |
| audio_flac | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Zoo Audio.flac (1239538 bytes, sha256 0AD553F660BEEEAA) |
| fp_light | ✅ identical | completed: Download and processing completed successfully.; 5 phases; FP.mp4 (678701 bytes, sha256 5DED280C03779108) |
| fp_standard | ✅ identical | completed: Download and processing completed successfully.; 5 phases; FP.mp4 (809189 bytes, sha256 2FA70D5B39354EB9) |
| fp_strong | ✅ identical | completed: Download and processing completed successfully.; 5 phases; FP.mp4 (922029 bytes, sha256 546EAAB8AE4CA598) |
| fp_metadata | ✅ identical | completed: Download and processing completed successfully.; 4 phases; FP.mp4 (533879 bytes, sha256 A442B03D67C484BB) |
| fp_standard_gpu | ✅ identical | completed: Download and processing completed successfully.; 5 phases; FP.mp4 (1046964 bytes, sha256 50C68DD26DAA4391) |
| ytmusic_320k | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Rick.mp3 (8896139 bytes, sha256 DC4724EC961FADD2) |
| ytmusic_m4a | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Rick.m4a (7334916 bytes, sha256 9CBAACAE8E182998) |
| ytmusic_flac | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Rick.flac (47916914 bytes, sha256 A87FD6AF790C6E77) |
| tiktok | ✅ identical | error: WARNING: [TikTok] The extractor is attempting impersonation, but no impersonate target is available. If you encounter errors, then see  https://github.com/yt-dlp/yt-dlp#impersonation  for information on installing the required dependencies ERROR: [TikTok] 6718335390845095173: Unexpected response from webpage request; please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U; 4 phases; no files |
| instagram | ✅ identical | completed: Download and processing completed successfully.; 3 phases; Insta.mp4 (1949801 bytes, sha256 64C7D81D2251172E) |
| unavailable_video | ✅ identical | error: WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default; to use another runtime add  --js-runtimes RUNTIME[:PATH]  to your command/config. YouTube extraction without a JS runtime has been deprecated, and some formats may be missing. See  https://github.com/yt-dlp/yt-dlp/wiki/EJS  for details on installing one ERROR: [youtube] xxxxxxxxxxx: This video is unavailable; 4 phases; no files |
| cancel_during_download | ✅ identical | cancelled: Job was cancelled by user.; 3 phases; no files |
| cloud_custom_folder | ✅ identical | completed: Saved to cloud folder (Cloud.mp4); 6 phases; Cloud.mp4 (533931 bytes, sha256 3FE549390A27A9C6); cloud_dest\Cloud.mp4 (533931 bytes, sha256 3FE549390A27A9C6) |
| cloud_custom_temp_only | ✅ identical | completed: Saved to cloud folder (CloudTemp.mp4); 6 phases; cloud_dest\CloudTemp.mp4 (533931 bytes, sha256 3FE549390A27A9C6) |
| cloud_webhook | ✅ identical | completed: Successfully sent to Webhook!; 6 phases; Hook.mp4 (533931 bytes, sha256 3FE549390A27A9C6); webhook body identical |
| cloud_gdrive_missing | ✅ identical | error: Google Drive folder not found. Please verify Google Drive for Desktop is running or configure a folder in Setup.; 4 phases; GD.mp4 (533931 bytes, sha256 3FE549390A27A9C6) |
| subtitles | ✅ identical | value "1\r\n00:00:00,000 --\u003E 00:00:04,000\r\nAlright, so here we are, one of the elephants.\r\n\r\n2\r\n00:00:04,000 --\u003E 00:00:12,000\r\nThe cool thing about these guys is that they have really, really, really long hunts.\r\n\r\n3\r\n00:00:12,000 --\u003E 00:00:14,000\r\nAnd that\u0027s cool.\r\… |

### 16.4 All three engines through the app (`parity -- backends`)

This uses the app's real `EngineProvider` / `EngineProxy`: switch, initialise, then exercise each feature. It also covers the
deferred switch and a `ferrite.exe` crash.

| Engine | Feature | Result | Details |
|---|---|---|---|
| Native | Engine start | ✅ works | Native C# Media Engine, ready=True |
| Native | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 3 tags |
| Native | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Native | Download MP3 | ✅ works | Download and processing completed successfully. → file_path=Zoo.mp3, title=Zoo |
| Native | Download video + fingerprint | ✅ works | Download and processing completed successfully. → file_path=Zoo FP.mp4, title=Zoo FP |
| Native | Cloud upload (custom folder) | ✅ works | Saved to cloud folder (Zoo Cloud.mp4) → file_path=Zoo Cloud.mp4, title=Zoo Cloud, cloud_provider=Custom Cloud Folder, cloud_destination=Zoo Cloud.mp4 |
| Native | YouTube upload | ☑️ clean error | error: YouTube upload requires the Ferrite or Legacy engine. |
| Native | Subtitles | ✅ works | 337 characters |
| Ferrite | Engine start | ✅ works | Ferrite Rust Media Engine, ready=True |
| Ferrite | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 3 tags |
| Ferrite | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Ferrite | Download MP3 | ✅ works | Download and processing completed successfully. → file_path=Zoo.mp3, title=Zoo |
| Ferrite | Download video + fingerprint | ✅ works | Download and processing completed successfully. → file_path=Zoo FP.mp4, title=Zoo FP |
| Ferrite | Cloud upload (custom folder) | ✅ works | Saved to cloud folder (Zoo Cloud.mp4) → file_path=Zoo Cloud.mp4, title=Zoo Cloud, cloud_provider=Custom Cloud Folder, cloud_destination=Zoo Cloud.mp4 |
| Ferrite | YouTube upload | ☑️ clean error | error: client_secret.json does not contain valid JSON. Download the complete OAuth JSON file from the Google Cloud Console. |
| Ferrite | Subtitles | ✅ works | 337 characters |
| Legacy | Engine start | ✅ works | Python FastAPI Backend, ready=True |
| Legacy | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 0 tags |
| Legacy | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Legacy | Download MP3 | ✅ works | Erfolgreich abgeschlossen! → file_path=Me at the zoo.mp3 |
| Legacy | Download video + fingerprint | ✅ works | Erfolgreich abgeschlossen! → file_path=Me at the zoo_yt.mp4 |
| Legacy | Cloud upload (custom folder) | ☑️ clean error | error: Cloud upload requires the Native or Ferrite engine. |
| Legacy | YouTube upload | ☑️ clean error | error: ValueError: client_secret.json enthält kein gültiges JSON. Bitte lade die vollständige OAuth-JSON-Datei von der Google Cloud Console herunter. |
| Legacy | Subtitles | ✅ works | 324 characters |
| Native → Ferrite | Switch while a job runs | ✅ works | applied immediately=False, engine during job=native, job=completed, engine after job=ferrite |
| Ferrite | ferrite.exe killed during a job | ☑️ clean error | error: Ferrite backend process exited unexpectedly (exit code -1). |
| Ferrite | Next call after the crash (auto-restart) | ✅ works | "Me at the zoo" |

The real app (VideoConverterUI.exe, Debug build) was also started with each engine selected and driven through
UI Automation:
- **All three engines**: the app starts. Settings → Advanced shows Native, Ferrite and Legacy in the existing combo, and the help bubble has a badge for each. Settings → Info shows `27H1.19 (108cf6ad) · Native` / `· Ferrite` / `· Legacy` and the engine names.
- **Ferrite with `ferrite.exe` removed**: the new error window opened by itself. It showed "Ferrite engine not found", the explanation, three numbered fixes, the real message (`Ferrite backend not found: …\ferrite.exe`) and the Open Settings / Copy Details / Show Logs buttons.

### 16.5 Error advice (`parity -- advice`)

64 real error texts (from Native, Ferrite, Legacy, yt-dlp, FFmpeg, Google Drive/OneDrive/Dropbox/webhook, YouTube and
Windows, plus the UI's own messages) are classified in en, de and ru. Every text lands in the expected category, and every
category has a title, an explanation and at least one fix in all three languages. **192/192 pass.**

---

## 17. Known differences

None of these change outputs, progress or error texts that the UI shows for normal use.

| # | Difference | Why |
|---|---|---|
| D14 | Network-layer failures (DNS, refused connection, TLS) in cloud uploads produce .NET-style texts, e.g. `"{Windows message} ({host}:{port})"`, built from the OS error code. Rare transport errors without an OS code use .NET's generic texts | `reqwest` errors are not .NET exceptions; socket texts are reproduced exactly, the rest by category |
| D15 | Process-wide caches (hardware encoder, Python lookup, cloud folder detection) live in `ferrite.exe`, not shared with the app's Editor | Separate process; results are identical, at most one extra probe after an engine switch |
| D16 | When a tool is started by bare name (`py`, `python`, a missing `yt-dlp`), Ferrite searches like `CreateProcess` and passes the full path, so the child's argv[0] is a full path | Rust's `Command` cannot keep a bare argv[0] with a custom search order; none of the tools reads argv[0] |
| D17 | If the app is killed, `ferrite.exe` cancels its jobs and exits (stdin closed). Native's children kept running as orphans | Otherwise the sidecar would keep running forever |
| — | `reqwest` always sends `Accept: */*`; .NET's `HttpClient` sends no `Accept` header | `reqwest` adds it unconditionally; no service used here reacts to it |
| — | Ferrite honours Settings → "Auto add #Shorts" for YouTube uploads; Legacy always adds `#Shorts` | The setting defaults to on, so the default behaviour is the same |
| — | Engine start failures, sidecar crashes and YouTube upload errors are Ferrite-only texts (D13, §9.1) | Native has no equivalent |

---

## 18. Build, test, release

```bash
cd backend-ferrite
cargo build --release                                      # target/release/ferrite.exe
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace                                     # unit + golden + sidecar tests

dotnet build VideoConverter.slnx                           # also runs cargo and copies ferrite.exe next to the app
dotnet publish VideoConverterUI -c Release                 # publish output includes ferrite.exe
installer/build_msi.bat                                    # Package.wxs installs ferrite.exe

dotnet run --project backend-ferrite/parity -- golden      # after changing Native: regenerate fixtures, rerun cargo test
dotnet run --project backend-ferrite/parity -- e2e         # side by side (downloads ~150 MB)
dotnet run --project backend-ferrite/parity -- backends    # all engines through EngineProvider (switches settings, restores them)
dotnet run --project backend-ferrite/parity -- advice      # error categories, en/de/ru
```

Reports are written to `backend-ferrite/parity/results/`. To make Ferrite the default for new installs, change the
`ExecutionEngine` initializer in `Models/AppSettings.cs` to `"ferrite"`; existing settings files keep their choice.
