# Native vs. Ferrite end-to-end run (2026-10-10 01:10)

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
