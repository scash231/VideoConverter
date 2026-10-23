using System.Windows;

namespace VideoConverterUI.Services;

/// <summary>What the error window's main button does for an error.</summary>
public enum ErrorAction
{
    None,
    RestartEngine,
    OpenAdvancedSettings,
    OpenDownloadSettings,
    InstallFfmpeg,
    OpenCloudSetup,
}

/// <summary>An error explained for the user: what happened, how to fix it, and the original message.</summary>
public sealed record ErrorAdvice(string Id, string Title, string Summary, IReadOnlyList<string> Steps, ErrorAction Action, string RawError);

/// <summary>
/// Turns raw error texts from all three engines (Native, Ferrite, Legacy), yt-dlp, FFmpeg, the cloud
/// providers, YouTube and Windows into a category with a short explanation and concrete fix steps.
/// Texts come from the <c>ErrAdv_{id}_*</c> string resources, so they follow the UI language.
/// </summary>
public static class ErrorAdvisor
{
    private sealed record Rule(string Id, ErrorAction Action, Func<Probe, bool> Matches);

    /// <summary>The error being analysed, with helpers for matching.</summary>
    private sealed class Probe(string raw, string url, bool backendReady)
    {
        public string Raw { get; } = raw;
        public string Lower { get; } = raw.ToLowerInvariant();
        public string Url { get; } = url;
        public bool BackendReady { get; } = backendReady;

        public bool Has(params string[] needles) => needles.Any(n => Lower.Contains(n, StringComparison.Ordinal));
        public bool Is(string resourceKey) => string.Equals(Raw.Trim(), Res(resourceKey).Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Errors that are about the link itself (metadata could not be read).</summary>
        public bool IsLinkProblem => Is("ErrNoUrl") || Is("ErrNoMeta") || Is("StatusLoadErr") || Has("unsupported url", "is not a valid url");
        public bool HasProtocol => Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    // Order matters: specific messages first, broad ones (network, generic yt-dlp) last.
    private static readonly Rule[] Rules =
    [
        new("ferrite_missing", ErrorAction.OpenAdvancedSettings, p => p.Has("ferrite backend not found")),
        new("ferrite_crashed", ErrorAction.RestartEngine, p => p.Has("ferrite backend process exited", "ferrite backend did not start", "ferrite internal error", "ferrite backend process is not running")),
        new("python_missing", ErrorAction.OpenAdvancedSettings, p => p.Has("python wurde nicht gefunden", "python was not found", "could not start python backend")),
        new("classic_server_missing", ErrorAction.OpenAdvancedSettings, p => p.Has("backend not found:") && p.Has("server.py")),
        new("upload_cancelled", ErrorAction.None, p => p.Has("upload cancelled by user")),
        new("empty_url", ErrorAction.None, p => p.Is("ErrNoUrl") || (p.IsLinkProblem && string.IsNullOrWhiteSpace(p.Url))),
        new("title_missing", ErrorAction.None, p => p.Is("ErrNoTitle")),
        new("save_path_missing", ErrorAction.OpenDownloadSettings, p => p.Is("ErrNoSavePath")),
        new("local_path", ErrorAction.None, p => p.IsLinkProblem && (p.Url.Contains(":\\") || p.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))),
        new("search_query", ErrorAction.None, p => p.IsLinkProblem && !p.HasProtocol && !p.Url.Contains('.') && !p.Url.Contains('/')),
        new("missing_protocol", ErrorAction.None, p => p.IsLinkProblem && !p.HasProtocol),
        new("backend_down", ErrorAction.RestartEngine, p => p.Is("StatusBackendDown") || p.Is("ErrNoBackend")
            || p.Has("127.0.0.1:8765", "actively refused", "verweigerte", "no connection could be made", "backend not reachable", "backend nicht erreichbar")),
        new("cloud_classic", ErrorAction.OpenAdvancedSettings, p => p.Has("cloud upload requires the native or ferrite engine", "cloud upload is not available in legacy mode", "cloud upload is not available in classic mode")),
        new("upload_native", ErrorAction.OpenAdvancedSettings, p => p.Has("youtube upload requires the ferrite or legacy engine", "youtube upload requires the ferrite or classic engine", "upload is not available in native mode", "upload requires legacy")),
        new("upload_credentials", ErrorAction.None, p => p.Has("client_secret")),
        new("upload_quota", ErrorAction.None, p => p.Has("quotaexceeded", "uploadlimitexceeded", "exceeded your quota", "exceeded the number of videos")),
        new("upload_signin", ErrorAction.None, p => p.Has("google sign-in", "invalid_grant", "refresherror", "token has been expired", "token has been revoked")),
        // Legacy's googleapiclient text is "<HttpError 400 when requesting https://youtube.googleapis.com/…>". yt-dlp also prints
        // "<HTTPError 429: …>" in its warnings, so a bare "httperror 4" must not count as an upload error.
        new("upload_api", ErrorAction.None, p => p.Has("youtube api error") || (p.Has("httperror 4", "httperror 5") && p.Has("when requesting", "googleapis.com"))),
        new("cloud_gdrive", ErrorAction.OpenCloudSetup, p => p.Has("google drive folder not found")),
        new("cloud_onedrive", ErrorAction.OpenCloudSetup, p => p.Has("onedrive folder not found")),
        new("cloud_dropbox_auth", ErrorAction.OpenCloudSetup, p => p.Has("dropbox api error (unauthorized)", "expired_access_token", "invalid_access_token")),
        new("cloud_dropbox", ErrorAction.OpenCloudSetup, p => p.Has("dropbox api error", "dropbox is not configured")),
        new("cloud_webhook_empty", ErrorAction.OpenCloudSetup, p => p.Has("webhook url is empty")),
        new("cloud_webhook_large", ErrorAction.None, p => p.Has("webhook error (requestentitytoolarge)")),
        new("cloud_webhook", ErrorAction.OpenCloudSetup, p => p.Has("webhook error", "invalid request uri", "invalid uri", "scheme is not supported")),
        new("cloud_custom", ErrorAction.OpenCloudSetup, p => p.Has("custom cloud folder does not exist")),
        new("age_restricted", ErrorAction.OpenAdvancedSettings, p => p.Has("age-restricted", "age restricted", "confirm your age", "inappropriate for some users")),
        new("bot_check", ErrorAction.OpenAdvancedSettings, p => p.Has("youtube verification required", "confirm you’re not a bot", "confirm you're not a bot")),
        new("members_only", ErrorAction.OpenAdvancedSettings, p => p.Has("members-only", "members only", "join this channel", "available to this channel's members")),
        new("login_required", ErrorAction.OpenAdvancedSettings, p => p.Has("login required", "requires login", "log in to", "rate-limit reached or login required", "use --cookies", "--cookies-from-browser", "cookies for the authentication")),
        new("unavailable", ErrorAction.None, p => p.Has("video is unavailable", "music track is unavailable", "video unavailable", "private video", "this video is not available",
            "has been removed", "been terminated", "does not exist", "http error 404")),
        new("geo_blocked", ErrorAction.None, p => p.Has("not available in your country", "geo restriction", "geo-restricted", "geo restricted")),
        new("live", ErrorAction.None, p => p.Has("live event will begin", "premieres in", "this live event", "is a live stream", "is currently live", "premiere will begin")),
        new("rate_limited", ErrorAction.None, p => p.Has("http error 429", "too many requests", "toomanyrequests")),
        new("subtitles_whisper", ErrorAction.None, p => p.Has("faster_whisper", "faster-whisper", "no module named 'whisper'")),
        new("subtitles_failed", ErrorAction.None, p => p.Has("subtitle generation failed", "subtitles error")),
        new("ffmpeg_missing", ErrorAction.InstallFfmpeg, p => p.Has("ffmpeg not found", "ffprobe and ffmpeg not found", "start process 'ffmpeg'", "start process 'ffprobe'",
            "ffmpeg is not installed", "ffmpeg was not found", "ffmpeg-location") && !p.Has("ffmpeg exited")),
        new("ytdlp_missing", ErrorAction.RestartEngine, p => p.Has("yt-dlp or ffmpeg command was not found", "start process 'yt-dlp'", "no module named yt_dlp", "exit code 9009")),
        new("js_runtime", ErrorAction.None, p => p.Has("no supported javascript runtime", "javascript runtime", "js runtime")),
        new("ytdlp_impersonate", ErrorAction.RestartEngine, p => p.Has("impersonat") && p.Has("error")),
        new("cookies_invalid", ErrorAction.OpenAdvancedSettings, p => p.Has("cookie") && p.Has("invalid", "netscape", "does not look like", "could not load", "loadcookie")),
        new("gpu_failed", ErrorAction.OpenAdvancedSettings, p => p.Has("nvenc", "cuda", "h264_qsv", "h264_amf", "gpu encoder", "hardware acceleration", "d3d11va")),
        new("disk_full", ErrorAction.OpenDownloadSettings, p => p.Has("not enough space", "no space left", "nicht genügend speicherplatz", "disk full", "errno 28", "enospc")),
        new("file_in_use", ErrorAction.None, p => p.Has("being used by another process", "von einem anderen prozess verwendet")),
        new("access_denied", ErrorAction.OpenDownloadSettings, p => p.Has("access to the path", "is denied", "permission denied", "zugriff verweigert", "errno 13", "access is denied")),
        new("path_not_found", ErrorAction.OpenDownloadSettings, p => p.Has("could not find a part of the path", "the path is empty", "could not find file", "directory not found", "cannot find the path")),
        new("download_missing", ErrorAction.None, p => p.Has("downloaded media file could not be located", "keine datei nach download gefunden")),
        new("ffmpeg_failed", ErrorAction.None, p => p.Has("postprocessing", "conversion failed", "error opening input", "invalid data found", "ffmpeg exited", "merging formats")),
        new("unsupported_url", ErrorAction.None, p => p.Has("unsupported url", "no video formats found", "is not a valid url")),
        new("no_meta", ErrorAction.None, p => p.Is("ErrNoMeta") || p.Is("StatusLoadErr")
            || (Res("StatusLoadErr").Length > 0 && p.Raw.StartsWith(Res("StatusLoadErr"), StringComparison.OrdinalIgnoreCase))),
        new("network", ErrorAction.None, p => p.Has("timed out", "timeout", "getaddrinfo", "name or service not known", "host ist unbekannt", "no such host", "unable to download webpage",
            "connection reset", "network is unreachable", "ssl", "certificate", "temporary failure in name resolution", "remote end closed", "connection aborted",
            "failed to resolve", "nameresolutionerror", "could not be established", "httprequestexception", "an error occurred while sending the request")),
        new("ytdlp_failed", ErrorAction.None, p => p.Has("http error 403", "signature extraction failed", "nsig extraction failed", "unable to extract", "precondition check failed",
            "requested format is not available", "yt-dlp exited with error code", "yt music download failed", "error: [", "please report this issue")),
        new("cancelled", ErrorAction.None, p => p.Is("StatusCancelled") || p.Has("cancelled by user", "abgebrochen", "was canceled", "was cancelled")),
    ];

    private static string Res(string key) => Application.Current?.TryFindResource(key) as string ?? "";

    /// <summary>Removes "Error:" / "Backend error:" style prefixes the UI adds in front of engine messages.</summary>
    public static string StripPrefixes(string raw)
    {
        var s = raw.Trim();
        string[] prefixes = [Res("StatusBackendErrPrefix"), Res("StatusErrPrefix"), Res("ErrPrefix"), "Backend error:", "Error:", "Fehler:"];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in prefixes)
            {
                if (p.Length > 0 && s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[p.Length..].TrimStart();
                    changed = true;
                }
            }
        }
        return s;
    }

    /// <summary>Rules that only say "yt-dlp failed" or "network problem"; a more specific hint elsewhere in the text wins.</summary>
    private static readonly HashSet<string> GenericRules = ["ytdlp_failed", "network"];

    /// <summary>Explains <paramref name="rawError"/>. <paramref name="url"/> is the URL field's text.</summary>
    public static ErrorAdvice Analyze(string? rawError, string? url, bool backendReady)
    {
        var raw = (rawError ?? "").Trim();
        var text = StripPrefixes(raw);
        var link = (url ?? "").Trim();

        // yt-dlp prints warnings (retried HTTP errors, missing JS runtime, …) before the error that actually stopped it.
        // The "ERROR:" lines decide; the whole text is only used when they don't match anything specific.
        Rule? rule = null;
        var errorLines = ErrorLines(text);
        if (errorLines != null)
            rule = Match(new Probe(errorLines, link, backendReady));
        if (rule == null || GenericRules.Contains(rule.Id))
            rule = Match(new Probe(text, link, backendReady)) ?? rule;

        var id = rule?.Id ?? (!backendReady ? "backend_down" : "unknown");
        var action = rule?.Action ?? (!backendReady ? ErrorAction.RestartEngine : ErrorAction.None);
        return Build(id, action, raw, link);
    }

    private static Rule? Match(Probe probe) => Rules.FirstOrDefault(r => r.Matches(probe));

    /// <summary>The lines yt-dlp marks with "ERROR:", or null if there are none.</summary>
    private static string? ErrorLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static ErrorAdvice Build(string id, ErrorAction action, string raw, string url)
    {
        var engine = EngineProvider.DisplayName(EngineProvider.ActiveKey);
        string Fill(string s) => s.Replace("{url}", url).Replace("{engine}", engine);

        var steps = new List<string>();
        for (int i = 1; i <= 5; i++)
        {
            var step = Res($"ErrAdv_{id}_Fix{i}");
            if (string.IsNullOrEmpty(step)) break;
            steps.Add(Fill(step));
        }
        return new ErrorAdvice(id, Fill(Res($"ErrAdv_{id}_Title")), Fill(Res($"ErrAdv_{id}_Summary")), steps, action, raw);
    }
}
