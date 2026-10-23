# All three backends through the app's engine switch (2026-10-10 01:15)

| Engine | Feature | Result | Details |
|---|---|---|---|
| Native | Engine start | ✅ works | Native C# Media Engine, ready=True |
| Native | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 3 tags |
| Native | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Native | Download MP3 | ✅ works | Download and processing completed successfully. → file_path=Zoo.mp3, title=Zoo |
| Native | Download video + fingerprint | ✅ works | Download and processing completed successfully. → file_path=Zoo FP.mp4, title=Zoo FP |
| Native | Cloud upload (custom folder) | ✅ works | Saved to cloud folder (Zoo Cloud.mp4) → file_path=Zoo Cloud.mp4, title=Zoo Cloud, cloud_provider=Custom Cloud Folder, cloud_destination=Zoo Cloud.mp4 |
| Native | YouTube upload | ☑️ clean error | error: YouTube upload requires the Ferrite or Classic engine. |
| Native | Subtitles | ✅ works | 337 characters |
| Ferrite | Engine start | ✅ works | Ferrite Rust Media Engine, ready=True |
| Ferrite | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 3 tags |
| Ferrite | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Ferrite | Download MP3 | ✅ works | Download and processing completed successfully. → file_path=Zoo.mp3, title=Zoo |
| Ferrite | Download video + fingerprint | ✅ works | Download and processing completed successfully. → file_path=Zoo FP.mp4, title=Zoo FP |
| Ferrite | Cloud upload (custom folder) | ✅ works | Saved to cloud folder (Zoo Cloud.mp4) → file_path=Zoo Cloud.mp4, title=Zoo Cloud, cloud_provider=Custom Cloud Folder, cloud_destination=Zoo Cloud.mp4 |
| Ferrite | YouTube upload | ☑️ clean error | error: client_secret.json does not contain valid JSON. Download the complete OAuth JSON file from the Google Cloud Console. |
| Ferrite | Subtitles | ✅ works | 337 characters |
| Classic | Engine start | ✅ works | Python FastAPI Backend, ready=True |
| Classic | Metadata | ✅ works | "Me at the zoo" by jawed, 19s, 0 tags |
| Classic | Playlist | ✅ works | "youtube-dl public playlist", 1 entries |
| Classic | Download MP3 | ✅ works | Erfolgreich abgeschlossen! → file_path=Me at the zoo.mp3 |
| Classic | Download video + fingerprint | ✅ works | Erfolgreich abgeschlossen! → file_path=Me at the zoo_yt.mp4 |
| Classic | Cloud upload (custom folder) | ☑️ clean error | error: Cloud upload requires the Native or Ferrite engine. |
| Classic | YouTube upload | ☑️ clean error | error: ValueError: client_secret.json enthält kein gültiges JSON. Bitte lade die vollständige OAuth-JSON-Datei von der Google Cloud Console herunter. |
| Classic | Subtitles | ✅ works | 324 characters |
| Native → Ferrite | Switch while a job runs | ✅ works | applied immediately=False, engine during job=native, job=completed, engine after job=ferrite |
| Ferrite | ferrite.exe killed during a job | ☑️ clean error | error: Ferrite backend process exited unexpectedly (exit code -1). |
| Ferrite | Next call after the crash (auto-restart) | ✅ works | "Me at the zoo" |
