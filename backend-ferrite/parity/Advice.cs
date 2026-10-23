using System.IO;
using System.Windows;
using VideoConverterUI.Services;

namespace ParityRunner;

/// <summary>
/// Checks that ErrorAdvisor puts real error texts (Native, Ferrite, Legacy, yt-dlp, FFmpeg, cloud,
/// YouTube, Windows) into the expected category, in every UI language.
/// </summary>
internal static class Advice
{
    private static readonly (string raw, string url, string expected)[] Cases =
    [
        ("Video is unavailable, private, or region-restricted.", "https://youtu.be/x", "unavailable"),
        ("Error: Video is unavailable, private, or region-restricted.", "https://youtu.be/x", "unavailable"),
        ("Music track is unavailable, private, or region-restricted.", "https://youtu.be/x", "unavailable"),
        ("YouTube verification required. For restricted videos, you can configure an optional cookies file in Settings → Advanced → Backend.", "https://youtu.be/x", "bot_check"),
        ("yt-dlp exited with error code 1", "https://youtu.be/x", "ytdlp_failed"),
        ("YT Music download failed (exit code 1)", "https://youtu.be/x", "ytdlp_failed"),
        ("yt-dlp or ffmpeg command was not found or failed to spawn (exit code 9009). Runner: yt-dlp", "https://youtu.be/x", "ytdlp_missing"),
        (@"An error occurred trying to start process 'yt-dlp' with working directory 'C:\x'. Das System kann die angegebene Datei nicht finden.", "https://youtu.be/x", "ytdlp_missing"),
        ("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.", "https://youtu.be/x", "age_restricted"),
        // Real yt-dlp output: a retried HTTP 429 warning and a JS-runtime warning before the age-gate error.
        ("WARNING: [youtube] s1-0lt7b-78: Unable to download webpage: HTTP Error 429: Too Many Requests (caused by <HTTPError 429: Too Many Requests>)\r\n"
            + "WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default; to use another runtime add  --js-runtimes RUNTIME[:PATH]  to your command/config. YouTube extraction without a JS runtime has been deprecated, and some formats may be missing. See  https://github.com/yt-dlp/yt-dlp/wiki/EJS  for details on installing one\r\n"
            + "ERROR: [youtube] s1-0lt7b-78: Sign in to confirm your age. Use --cookies-from-browser or --cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp  for how to manually pass cookies. Also see  https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies  for tips on effectively exporting YouTube cookies",
            "https://www.youtube.com/watch?v=s1-0lt7b-78&list=RDAMVMs1-0lt7b-78", "age_restricted"),
        ("WARNING: [youtube] x: Unable to download webpage: HTTP Error 429: Too Many Requests (caused by <HTTPError 429: Too Many Requests>)\r\nERROR: [youtube] x: Video unavailable. This video is private", "https://youtu.be/x", "unavailable"),
        ("WARNING: [youtube] x: Unable to download webpage: HTTP Error 429: Too Many Requests (caused by <HTTPError 429: Too Many Requests>)\r\nERROR: [youtube] x: Requested format is not available. Use --list-formats for a list of available formats", "https://youtu.be/x", "rate_limited"),
        ("ERROR: [Instagram] Chunk8-jurw: Requested content is not available, rate-limit reached or login required. Use --cookies, --cookies-from-browser, --username and --password", "https://instagram.com/x", "login_required"),
        ("ERROR: Unsupported URL: https://example.com/page", "https://example.com/page", "unsupported_url"),
        ("ERROR: [youtube] x: HTTP Error 429: Too Many Requests", "https://youtu.be/x", "rate_limited"),
        ("ERROR: [youtube] x: Requested format is not available. Use --list-formats for a list of available formats", "https://youtu.be/x", "ytdlp_failed"),
        ("WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default", "https://youtu.be/x", "js_runtime"),
        ("WARNING: [TikTok] The extractor is attempting impersonation, but no impersonate target is available. If you encounter errors, then see  https://github.com/yt-dlp/yt-dlp#impersonation  for information on installing the required dependencies\r\nERROR: [TikTok] 6718335390845095173: Unexpected response from webpage request; please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U", "https://www.tiktok.com/@scout2015/video/6718335390845095173", "ytdlp_impersonate"),
        ("ERROR: Postprocessing: Conversion failed!", "https://youtu.be/x", "ffmpeg_failed"),
        ("ERROR: You have requested merging of multiple formats but ffmpeg is not installed. Aborting due to --abort-on-error", "https://youtu.be/x", "ffmpeg_missing"),
        ("Google Drive folder not found. Please verify Google Drive for Desktop is running or configure a folder in Setup.", "", "cloud_gdrive"),
        ("Microsoft OneDrive folder not found. Please ensure OneDrive is running or configure a folder in Setup.", "", "cloud_onedrive"),
        ("Dropbox API Error (Unauthorized): {\"error_summary\": \"expired_access_token/\"}", "", "cloud_dropbox_auth"),
        ("Dropbox API Error (Conflict): {\"error_summary\": \"path/insufficient_space/\"}", "", "cloud_dropbox"),
        ("Dropbox is not configured. Please enter a Dropbox API token or install the Dropbox desktop app.", "", "cloud_dropbox"),
        ("Webhook URL is empty. Please enter a valid Discord or custom Webhook URL in Cloud Setup.", "", "cloud_webhook_empty"),
        ("Webhook error (RequestEntityTooLarge): {\"message\": \"Request entity too large\"}", "", "cloud_webhook_large"),
        ("Webhook error (NotFound): {\"message\": \"Unknown Webhook\"}", "", "cloud_webhook"),
        ("An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set.", "", "cloud_webhook"),
        ("Custom cloud folder does not exist or is not configured.", "", "cloud_custom"),
        ("Upload cancelled by user", "", "upload_cancelled"),
        ("Cloud upload requires the Native or Ferrite engine.", "", "cloud_classic"),
        ("YouTube upload requires the Ferrite or Legacy engine.", "", "upload_native"),
        (@"YouTube upload: client_secret*.json was not found in C:\repo\backend", "", "upload_credentials"),
        ("client_secret.json does not contain valid JSON. Download the complete OAuth JSON file from the Google Cloud Console.", "", "upload_credentials"),
        ("YouTube API error 403 (quotaExceeded): The request cannot be completed because you have exceeded your quota.", "", "upload_quota"),
        ("Google sign-in failed (invalid_grant): Token has been expired or revoked.", "", "upload_signin"),
        ("YouTube API error 400 (invalidTitle): The request metadata specifies an invalid video title.", "", "upload_api"),
        ("<HttpError 400 when requesting https://youtube.googleapis.com/upload/youtube/v3/videos?part=snippet%2Cstatus&alt=json&uploadType=resumable returned \"The request metadata specifies an invalid video title.\". Details: \"[{'message': 'The request metadata specifies an invalid video title.', 'domain': 'youtube.video', 'reason': 'invalidTitle'}]\">", "", "upload_api"),
        (@"Ferrite backend not found: C:\Program Files\VideoConverter\ferrite.exe", "", "ferrite_missing"),
        ("Ferrite backend process exited unexpectedly (exit code 101).", "", "ferrite_crashed"),
        ("Python wurde nicht gefunden. Bitte Python installieren und zum PATH hinzufügen.", "", "python_missing"),
        (@"Could not find a part of the path 'Q:\Videos\x'.", "https://youtu.be/x", "path_not_found"),
        (@"Access to the path 'C:\Windows\x' is denied.", "https://youtu.be/x", "access_denied"),
        (@"There is not enough space on the disk. : 'C:\x.mp4'", "https://youtu.be/x", "disk_full"),
        (@"The process cannot access the file 'C:\x.mp4' because it is being used by another process.", "https://youtu.be/x", "file_in_use"),
        ("Downloaded media file could not be located.", "https://youtu.be/x", "download_missing"),
        ("Subtitles error: ModuleNotFoundError: No module named 'faster_whisper'", "", "subtitles_whisper"),
        ("Subtitles error: Subtitle generation failed (exit code 1).", "", "subtitles_failed"),
        ("Der angegebene Host ist unbekannt. (www.youtube.com:443)", "https://youtu.be/x", "network"),
        ("ERROR: [youtube] x: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", "https://youtu.be/x", "network"),
        ("Job was cancelled by user.", "", "cancelled"),
        ("GPU encoder failed, retrying on CPU...", "", "gpu_failed"),
        ("Something weird happened 123", "https://youtu.be/x", "unknown"),
    ];

    /// <summary>Cases that depend on the UI text of the active language (resource keys).</summary>
    private static readonly (string key, string url, string expected)[] KeyedCases =
    [
        ("ErrNoUrl", "", "empty_url"),
        ("ErrNoTitle", "https://youtu.be/x", "title_missing"),
        ("ErrNoSavePath", "https://youtu.be/x", "save_path_missing"),
        ("StatusBackendDown", "https://youtu.be/x", "backend_down"),
        ("StatusCancelled", "https://youtu.be/x", "cancelled"),
        ("ErrNoMeta", "dQw4w9WgXcQ cat video", "search_query"),
        ("ErrNoMeta", "youtube.com/watch?v=1", "missing_protocol"),
        ("ErrNoMeta", @"C:\Videos\a.mp4", "local_path"),
        ("ErrNoMeta", "https://www.youtube.com/watch?v=1", "no_meta"),
        ("ErrNoMeta", "", "empty_url"),
    ];

    public static int Run()
    {
        int failures = 0;
        var t = new Thread(() =>
        {
            var app = new Application();
            foreach (var lang in new[] { "en", "de", "ru" })
            {
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VideoConverterUI;component/Assets/Strings.en.xaml") });
                if (lang != "en")
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/VideoConverterUI;component/Assets/Strings.{lang}.xaml") });

                var all = Cases.Concat(KeyedCases.Select(k => ((string)app.Resources[k.key], k.url, k.expected)));
                foreach (var (raw, url, expected) in all)
                {
                    var a = ErrorAdvisor.Analyze(raw, url, backendReady: true);
                    bool ok = a.Id == expected && !string.IsNullOrEmpty(a.Title) && !string.IsNullOrEmpty(a.Summary) && a.Steps.Count > 0
                              && !a.Title.StartsWith("ErrAdv_") && a.Steps.All(s => !s.Contains("{url}") && !s.Contains("{engine}"));
                    if (!ok)
                    {
                        failures++;
                        Console.WriteLine($"[{lang}] FAIL '{raw}' → {a.Id} (expected {expected}); title='{a.Title}' steps={a.Steps.Count}");
                    }
                }
                Console.WriteLine($"[{lang}] {Cases.Length + KeyedCases.Length} cases checked");
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Console.WriteLine(failures == 0 ? "advice: all categories matched" : $"advice: {failures} failures");
        return failures == 0 ? 0 : 1;
    }
}
