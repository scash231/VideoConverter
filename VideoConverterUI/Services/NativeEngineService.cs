using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public sealed class NativeEngineService : IEngineService
{
    public string EngineName => "Native C# Media Engine";

    private static readonly JsonSerializerOptions _jso = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                       | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly Regex YtDlpProgressRegex = new(
        @"\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+\w+)(?:\s+at\s+([\d.]+\w+/s))?(?:\s+ETA\s+([\d:]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InvisibleCharsRegex = new(
        @"[\u200b\u200c\u200d\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2060\u2066\u2067\u2068\u2069\ufeff\u00ad\u034f\u061c\u115f\u1160\u17b4\u17b5\u180e\ufff0-\ufff8\ufffa-\ufffd]",
        RegexOptions.Compiled);

    // ── Job context ──────────────────────────────────────────────────────────

    private sealed class JobContext
    {
        public string JobId { get; set; } = "";
        public Channel<JobStatus> Channel { get; set; } = System.Threading.Channels.Channel.CreateUnbounded<JobStatus>();
        public CancellationTokenSource Cts { get; set; } = new();
        public Process? RunningProcess { get; set; }
        public JobStatus LastStatus { get; set; } = new();
        public string? LastLoggedPhase { get; set; }
    }

    private readonly ConcurrentDictionary<string, JobContext> _jobs = new();
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private (string exe, string extraArgs)? _ytDlpRunner;

    private readonly object _initLock = new();
    private Task? _initTask;

    private static readonly Lazy<string?> _pythonWithYtDlp = new(FindWorkingPythonWithYtDlp);

    // ── Initialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Locating yt-dlp/ffmpeg spawns several probe processes (and may even download yt-dlp),
    /// so it runs once on a worker thread instead of blocking the UI thread in the constructor.
    /// </summary>
    private Task EnsureInitializedAsync()
    {
        lock (_initLock)
            return _initTask ??= Task.Run(ResolveBinaries);
    }

    public async Task<bool> WaitForHealthAsync(CancellationToken ct, int maxWait = 30)
    {
        await EnsureInitializedAsync().WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Binary resolution
    // ═════════════════════════════════════════════════════════════════════════

    private void ResolveBinaries()
    {
        var appDir = MediaTools.AppDir;
        var solutionDir = MediaTools.FindSolutionDir();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 1. FFmpeg & FFprobe
        _ffmpegPath = null;
        _ffprobePath = null;
        foreach (var p in MediaTools.GetFfmpegCandidates())
        {
            if (File.Exists(p))
            {
                _ffmpegPath = p;
                var dir = Path.GetDirectoryName(p)!;
                var probe = Path.Combine(dir, "ffprobe.exe");
                if (File.Exists(probe))
                    _ffprobePath = probe;
                break;
            }
        }

        if (_ffprobePath == null && _ffmpegPath != null)
        {
            var dir = Path.GetDirectoryName(_ffmpegPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var probe = Path.Combine(dir, "ffprobe.exe");
                if (File.Exists(probe)) _ffprobePath = probe;
            }
        }

        _ffmpegPath ??= "ffmpeg";
        _ffprobePath ??= "ffprobe";

        // 2. yt-dlp binary candidates (standalone exe or virtualenv exe)
        var ytDlpCandidates = new List<string>
        {
            Path.Combine(solutionDir, "backend", "venv", "Scripts", "yt-dlp.exe"),
            Path.Combine(appDir, "backend", "venv", "Scripts", "yt-dlp.exe"),
            Path.Combine(appDir, "venv", "Scripts", "yt-dlp.exe"),
            Path.Combine(solutionDir, "venv", "Scripts", "yt-dlp.exe"),
            Path.Combine(appDir, "yt-dlp.exe"),
            Path.Combine(appDir, "backend", "yt-dlp.exe"),
            Path.Combine(solutionDir, "backend", "yt-dlp.exe"),
            Path.Combine(solutionDir, "yt-dlp.exe"),
            Path.Combine(localAppData, "VideoConverter", "bin", "yt-dlp.exe"),
            Path.Combine(appData, "VideoConverter", "bin", "yt-dlp.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python312\Scripts\yt-dlp.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python311\Scripts\yt-dlp.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python310\Scripts\yt-dlp.exe"),
            Path.Combine(appData, @"Python\Python312\Scripts\yt-dlp.exe"),
            Path.Combine(appData, @"Python\Python311\Scripts\yt-dlp.exe"),
            Path.Combine(appData, @"Python\Python310\Scripts\yt-dlp.exe"),
        };

        MediaTools.AddPathCandidates(ytDlpCandidates, "yt-dlp.exe");

        _ytDlpRunner = null;
        foreach (var p in ytDlpCandidates)
        {
            if (File.Exists(p) && TestExecutable(p, "--version"))
            {
                _ytDlpRunner = (p, "");
                break;
            }
        }

        // 3. Fallback: Python module (python -m yt_dlp)
        if (_ytDlpRunner == null)
        {
            var py = _pythonWithYtDlp.Value;
            if (py != null)
            {
                _ytDlpRunner = (py, "-m yt_dlp");
            }
        }

        // 4. Self-healing fallback: Auto-download standalone yt-dlp.exe if still not found
        if (_ytDlpRunner == null)
        {
            var autoYtDlp = EnsureStandaloneYtDlp();
            if (autoYtDlp != null)
            {
                _ytDlpRunner = (autoYtDlp, "");
            }
            else
            {
                _ytDlpRunner = ("yt-dlp", "");
            }
        }
    }

    private static bool TestExecutable(string exe, string testArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = testArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            using var p = Process.Start(psi);
            if (p != null)
            {
                if (!p.WaitForExit(3500))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                return p.ExitCode == 0;
            }
        }
        catch { }
        return false;
    }

    private static string? FindWorkingPythonWithYtDlp()
    {
        var sol = MediaTools.FindSolutionDir();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var pythonCandidates = new List<string>
        {
            Path.Combine(sol, "backend", "venv", "Scripts", "python.exe"),
            Path.Combine(MediaTools.AppDir, "backend", "venv", "Scripts", "python.exe"),
            Path.Combine(sol, "venv", "Scripts", "python.exe"),
            Path.Combine(MediaTools.AppDir, "venv", "Scripts", "python.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python312\python.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python311\python.exe"),
            Path.Combine(localAppData, @"Programs\Python\Python310\python.exe"),
            Path.Combine(appData, @"Programs\Python\Python312\python.exe"),
            Path.Combine(appData, @"Programs\Python\Python311\python.exe"),
            Path.Combine(appData, @"Programs\Python\Python310\python.exe"),
        };

        MediaTools.AddPathCandidates(pythonCandidates, "python.exe");

        foreach (var py in pythonCandidates)
        {
            if (File.Exists(py) && TestExecutable(py, "-m yt_dlp --version"))
                return py;
        }

        if (TestExecutable("py", "-3.12 -m yt_dlp --version")) return "py";
        if (TestExecutable("py", "-3.11 -m yt_dlp --version")) return "py";
        if (TestExecutable("py", "-3.10 -m yt_dlp --version")) return "py";
        if (TestExecutable("python", "-m yt_dlp --version")) return "python";

        return null;
    }

    private static string? EnsureStandaloneYtDlp()
    {
        try
        {
            var targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoConverter", "bin");
            Directory.CreateDirectory(targetDir);
            var targetExe = Path.Combine(targetDir, "yt-dlp.exe");

            if (File.Exists(targetExe) && TestExecutable(targetExe, "--version"))
                return targetExe;

            var tempExe = targetExe + ".tmp";
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var response = client.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                using (var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                }
                File.Move(tempExe, targetExe, true);
                if (TestExecutable(targetExe, "--version"))
                    return targetExe;
            }
        }
        catch { }
        return null;
    }

    public async Task<bool> EnsureFfmpegReadyAsync()
    {
        if (File.Exists(_ffmpegPath) && !string.IsNullOrEmpty(_ffprobePath) && File.Exists(_ffprobePath))
            return true;

        if (TestExecutable(_ffmpegPath ?? "ffmpeg", "-version"))
            return true;

        var installed = await FFmpegInstallerService.PromptAndInstallIfNeededAsync();
        if (installed)
        {
            ResolveBinaries();
            return true;
        }

        return false;
    }

    private void ConfigureProcessStartInfo(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var extraPaths = new List<string>();

        if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(_ffmpegPath);
            if (!string.IsNullOrEmpty(ffmpegDir) && !extraPaths.Contains(ffmpegDir, StringComparer.OrdinalIgnoreCase))
                extraPaths.Add(ffmpegDir);
        }

        if (_ytDlpRunner.HasValue && !string.IsNullOrEmpty(_ytDlpRunner.Value.exe) && File.Exists(_ytDlpRunner.Value.exe))
        {
            var runnerDir = Path.GetDirectoryName(_ytDlpRunner.Value.exe);
            if (!string.IsNullOrEmpty(runnerDir) && !extraPaths.Contains(runnerDir, StringComparer.OrdinalIgnoreCase))
                extraPaths.Add(runnerDir);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var denoBin = Path.Combine(userProfile, ".deno", "bin");
        if (Directory.Exists(denoBin) && !extraPaths.Contains(denoBin, StringComparer.OrdinalIgnoreCase))
            extraPaths.Add(denoBin);

        if (extraPaths.Count > 0)
        {
            var currentPath = psi.EnvironmentVariables.ContainsKey("PATH")
                ? psi.EnvironmentVariables["PATH"]
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.EnvironmentVariables["PATH"] = string.Join(";", extraPaths) + ";" + currentPath;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Cookies configuration (configured explicitly via Settings -> Advanced)
    // ═════════════════════════════════════════════════════════════════════════

    public static string? ResolveCookiesFile()
    {
        var custom = SettingsService.Current.CookiesFilePath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Metadata fetching (via yt-dlp -J with oEmbed fallback)
    // ═════════════════════════════════════════════════════════════════════════

    private static readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<MetadataResponse?> FetchMetadataAsync(string url, CancellationToken ct = default)
    {
        var cleanUrl = CleanUrl(url);
        var meta = await ExtractMetadataViaYtDlpAsync(cleanUrl, false, ct);
        if (meta != null && !string.IsNullOrWhiteSpace(meta.Title))
            return meta;

        // Fallback: oEmbed
        return await FetchFallbackMetadataAsync(cleanUrl, ct);
    }

    public async Task<MetadataResponse?> FetchYTMusicMetadataAsync(string url, CancellationToken ct = default)
    {
        var cleanUrl = CleanUrl(url);
        var meta = await ExtractMetadataViaYtDlpAsync(cleanUrl, true, ct);
        if (meta != null && !string.IsNullOrWhiteSpace(meta.Title))
            return meta;

        // yt-dlp already failed for this URL; running it again in "regular" mode would only
        // repeat the same (slow) request, so go straight to the oEmbed fallback.
        var fallback = await FetchFallbackMetadataAsync(cleanUrl, ct);
        if (fallback != null)
            fallback.IsMusic = true;
        return fallback;
    }

    private async Task<MetadataResponse?> ExtractMetadataViaYtDlpAsync(string cleanUrl, bool forceMusic, CancellationToken ct)
    {
        try
        {
            await EnsureInitializedAsync().WaitAsync(ct);

            var (exe, extra) = _ytDlpRunner ?? ("yt-dlp", "");
            var args = new StringBuilder();
            if (!string.IsNullOrEmpty(extra)) args.Append($"{extra} ");

            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
            {
                var ffmpegDir = Path.GetDirectoryName(_ffmpegPath);
                if (!string.IsNullOrEmpty(ffmpegDir))
                    args.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
            }

            bool isTrack = cleanUrl.Contains("watch?v=") || cleanUrl.Contains("/watch?");
            if (isTrack) args.Append("--no-playlist ");

            var cookieFile = ResolveCookiesFile();
            if (!string.IsNullOrEmpty(cookieFile))
                args.Append($"--cookies \"{cookieFile}\" ");

            args.Append("-J --skip-download --no-warnings ");
            args.Append($"\"{cleanUrl}\"");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString(),
            };
            ConfigureProcessStartInfo(psi);

            var stdout = (await MediaTools.RunProcessAsync(psi, ct, timeoutMs: 90_000)).StdOut;
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;

            bool isMusic = forceMusic || IsYtMusicUrl(cleanUrl);

            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var uploader = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : (root.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "");
            var thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "";
            double duration = root.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out var durVal) ? durVal : 0;
            var artist = root.TryGetProperty("artist", out var art) ? art.GetString() : (isMusic ? uploader : null);
            var album = root.TryGetProperty("album", out var alb) ? alb.GetString() : null;
            var releaseYear = root.TryGetProperty("release_year", out var ry) ? ry.ToString() : null;

            if (artist != null)
                artist = Regex.Replace(artist, @" - Topic$", "", RegexOptions.IgnoreCase).Trim();

            var resp = new MetadataResponse
            {
                Title = title,
                Description = desc,
                Uploader = artist ?? uploader,
                Duration = duration,
                Thumbnail = thumbnail,
                Artist = artist,
                Album = album,
                ReleaseYear = releaseYear,
                IsMusic = isMusic,
            };

            // Extract tags
            if (root.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsElem.EnumerateArray())
                {
                    var tagStr = tag.GetString();
                    if (!string.IsNullOrWhiteSpace(tagStr))
                        resp.Tags.Add(tagStr);
                }
            }
            else if (!string.IsNullOrWhiteSpace(desc))
            {
                var hashTags = Regex.Matches(desc, @"#(\w+)");
                foreach (Match match in hashTags)
                {
                    if (match.Groups[1].Success)
                        resp.Tags.Add(match.Groups[1].Value);
                }
            }

            return !string.IsNullOrWhiteSpace(resp.Title) ? resp : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    private async Task<MetadataResponse?> FetchFallbackMetadataAsync(string url, CancellationToken ct)
    {
        try
        {
            if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            {
                var oembedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
                var json = await _httpClient.GetStringAsync(oembedUrl, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new MetadataResponse
                {
                    Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Uploader = root.TryGetProperty("author_name", out var a) ? a.GetString() ?? "" : "",
                    Thumbnail = root.TryGetProperty("thumbnail_url", out var th) ? th.GetString() ?? "" : "",
                    Description = root.TryGetProperty("title", out var d) ? d.GetString() ?? "" : "",
                };
            }
        }
        catch { }
        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Job creation & pipeline
    // ═════════════════════════════════════════════════════════════════════════

    public Task<string> CreateJobAsync(
        string url, string mode, string platform,
        string? title, string? description, List<string>? tags,
        string? outputDir, string? privacy, bool fingerprint,
        string fingerprintMethod = "standard",
        bool useGpu = false,
        string? quality = null,
        CancellationToken ct = default,
        bool cloudUpload = false,
        string? cloudProvider = null,
        bool cloudTempOnly = true)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var ctx = new JobContext
        {
            JobId = jobId,
            LastStatus = new JobStatus
            {
                Id = jobId,
                Status = "pending",
                Progress = 0,
                Message = "Starting job..."
            }
        };

        _jobs[jobId] = ctx;

        _ = Task.Run(async () =>
        {
            // description/tags/privacy only matter for YouTube uploads, which the native engine rejects.
            await ExecuteJobPipelineAsync(
                ctx, url, mode, platform, title,
                outputDir, fingerprint, fingerprintMethod, useGpu, quality,
                cloudUpload, cloudProvider, cloudTempOnly);
        });

        return Task.FromResult(jobId);
    }

    private async Task ExecuteJobPipelineAsync(
        JobContext ctx, string url, string mode, string platform,
        string? title, string? outputDir, bool fingerprint,
        string fingerprintMethod, bool useGpu, string? quality,
        bool cloudUpload, string? cloudProvider, bool cloudTempOnly)
    {
        var ct = ctx.Cts.Token;
        string? downloadedFile = null;
        string? rawStem = null;
        string? downloadDir = null;

        try
        {
            var cleanUrl = CleanUrl(url);
            bool isUpload = string.Equals(mode, "upload", StringComparison.OrdinalIgnoreCase);
            bool isYtMusic = IsYtMusicUrl(cleanUrl)
                          || string.Equals(platform, "ytmusic", StringComparison.OrdinalIgnoreCase)
                          || (quality ?? "").StartsWith("ytmusic", StringComparison.OrdinalIgnoreCase);
            bool isAudioMode = string.Equals(quality, "audio", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(quality, "mp3", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(quality, "m4a", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(quality, "flac", StringComparison.OrdinalIgnoreCase)
                            || isYtMusic;
            bool hasFp = fingerprint && !isAudioMode;

            // ── Upload mode: not supported in native engine ──────────────
            if (isUpload)
            {
                EmitStatus(ctx, "error", 0,
                    "YouTube Upload is not available in Native mode. Please switch to Ferrite or Legacy in Settings → Advanced for upload jobs.",
                    error: "YouTube upload requires the Ferrite or Legacy engine.");
                ctx.Channel.Writer.TryComplete();
                return;
            }

            // ── Compute total phases ─────────────────────────────────────
            int totalPhases = 1; // download
            if (hasFp) totalPhases++;
            if (cloudUpload) totalPhases++;
            int currentPhase = 1;

            string PhasePrefix() => totalPhases > 1 ? $"[{currentPhase}/{totalPhases}] " : "";

            // ── Ensure FFmpeg binaries are ready (auto-prompt if missing) ──
            await EnsureInitializedAsync().WaitAsync(ct);
            await EnsureFfmpegReadyAsync();

            // ── Determine download directory ─────────────────────────────
            if (cloudUpload && cloudTempOnly)
            {
                downloadDir = Path.Combine(Path.GetTempPath(), "ReelsConverter", "jobs", ctx.JobId);
            }
            else
            {
                downloadDir = string.IsNullOrWhiteSpace(outputDir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                    : Path.GetFullPath(outputDir);
            }
            Directory.CreateDirectory(downloadDir);

            rawStem = Guid.NewGuid().ToString("N")[..12];

            // ── Build yt-dlp arguments ───────────────────────────────────
            if (isYtMusic)
            {
                EmitStatus(ctx, "downloading", 0, $"{PhasePrefix()}Downloading music...", null);
                downloadedFile = await DownloadYtMusicAsync(ctx, cleanUrl, downloadDir, rawStem, quality ?? "ytmusic_320k", PhasePrefix, ct);
            }
            else
            {
                string initialMsg = isAudioMode ? "Downloading audio..." : "Downloading video...";
                EmitStatus(ctx, "downloading", 0, $"{PhasePrefix()}{initialMsg}", null);
                downloadedFile = await DownloadVideoWithRetriesAsync(ctx, cleanUrl, downloadDir, rawStem, quality ?? "best", isAudioMode, PhasePrefix, ct);
            }

            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            if (downloadedFile == null || !File.Exists(downloadedFile))
                throw new FileNotFoundException("Downloaded media file could not be located.");

            string finalFile = downloadedFile;

            // ── Fingerprint bypass ───────────────────────────────────────
            if (hasFp)
            {
                currentPhase++;
                EmitStatus(ctx, "processing", 50, $"{PhasePrefix()}Applying fingerprint bypass...", null);

                finalFile = await ApplyFingerprintAsync(ctx, downloadedFile, fingerprintMethod, useGpu, PhasePrefix, ct);

                if (finalFile != downloadedFile && File.Exists(finalFile))
                {
                    SafeDelete(downloadedFile);
                }
            }

            // ── Rename to safe title ─────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(title))
            {
                var safeName = SanitizeFilename(title) + Path.GetExtension(finalFile);
                var targetPath = Path.Combine(downloadDir, safeName);

                // Handle existing file with same name
                int n = 1;
                while (File.Exists(targetPath) && targetPath != finalFile)
                {
                    safeName = SanitizeFilename(title) + $" ({n})" + Path.GetExtension(finalFile);
                    targetPath = Path.Combine(downloadDir, safeName);
                    n++;
                }

                try
                {
                    File.Move(finalFile, targetPath);
                    finalFile = targetPath;
                }
                catch { }
            }

            // Clean up leftover temp artifacts
            CleanStemFiles(downloadDir, rawStem, finalFile);

            var resultDict = new Dictionary<string, object>
            {
                ["file_path"] = finalFile,
                ["title"] = title ?? Path.GetFileNameWithoutExtension(finalFile),
            };

            // ── Cloud Upload ─────────────────────────────────────────────
            if (cloudUpload)
            {
                currentPhase++;
                var pKey = cloudProvider ?? SettingsService.Current.CloudProvider;
                var pDisplayName = CloudStorageService.GetProviderDisplayName(pKey);
                EmitStatus(ctx, "uploading", 0, $"{PhasePrefix()}Uploading to {pDisplayName}...", null);

                var uploadResult = await CloudStorageService.UploadAsync(
                    finalFile,
                    pKey,
                    SettingsService.Current,
                    (pct, msg) =>
                    {
                        EmitStatus(ctx, "uploading", pct, $"{PhasePrefix()}{msg}", null);
                    },
                    ct);

                if (!uploadResult.Success)
                {
                    throw new Exception(uploadResult.ErrorMessage ?? $"Failed to upload to {pDisplayName}");
                }

                resultDict["cloud_provider"] = pDisplayName;
                resultDict["cloud_destination"] = uploadResult.DestinationPath ?? uploadResult.Url ?? pDisplayName;
                if (!string.IsNullOrEmpty(uploadResult.Url))
                {
                    resultDict["cloud_url"] = uploadResult.Url;
                }

                if (cloudTempOnly)
                {
                    resultDict["is_temp"] = true;
                    try
                    {
                        if (File.Exists(finalFile)) File.Delete(finalFile);
                        if (!string.IsNullOrEmpty(downloadDir) && downloadDir.Contains("ReelsConverter") && Directory.Exists(downloadDir))
                        {
                            Directory.Delete(downloadDir, true);
                        }
                    }
                    catch { }
                }

                EmitStatus(ctx, "completed", 100, uploadResult.Message ?? $"Successfully uploaded to {pDisplayName}!", result: resultDict);
                ctx.Channel.Writer.TryComplete();
                return;
            }

            EmitStatus(ctx, "completed", 100, "Download and processing completed successfully.", result: resultDict);
            ctx.Channel.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            EmitStatus(ctx, "cancelled", 0, "Job was cancelled by user.");
            ctx.Channel.Writer.TryComplete();

            // Cleanup on cancel
            if (downloadDir != null && rawStem != null)
                CleanStemFiles(downloadDir, rawStem);
        }
        catch (Exception ex)
        {
            EmitStatus(ctx, "error", 0, $"Error: {ex.Message}", error: ex.Message);
            ctx.Channel.Writer.TryComplete();

            // Cleanup on error
            if (downloadDir != null && rawStem != null)
                CleanStemFiles(downloadDir, rawStem);
        }
        finally
        {
            ctx.RunningProcess?.Dispose();
            ctx.RunningProcess = null;
            _ = ForgetJobLaterAsync(ctx.JobId);
        }
    }

    /// <summary>
    /// Finished jobs only need to stay around long enough for a consumer to drain the
    /// remaining status updates; afterwards they are dropped so the dictionary cannot grow forever.
    /// </summary>
    private async Task ForgetJobLaterAsync(string jobId)
    {
        await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        _jobs.TryRemove(jobId, out _);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Video download with retry strategies (mirrors downloader.py)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Retry strategies: (format_mode, backoff_seconds)
    ///   best        = bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best
    ///   any         = bestvideo*+bestaudio/best
    ///   progressive = best[height<=1080]/best (single progressive stream)
    /// </summary>
    private static readonly (string formatMode, int backoffSec)[] RetryStrategies =
    [
        ("best",        0),
        ("any",         1),
        ("progressive", 2),
    ];

    private async Task<string?> DownloadVideoWithRetriesAsync(
        JobContext ctx, string url, string outputDir, string stem,
        string quality, bool isAudio, Func<string> phasePrefix, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < RetryStrategies.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (formatMode, backoff) = RetryStrategies[attempt];

            if (backoff > 0)
                await Task.Delay(backoff * 1000, ct);

            var formatString = GetFormatString(quality, isAudio, formatMode);

            try
            {
                var file = await RunYtDlpDownloadAsync(
                    ctx, url, outputDir, stem, formatString, quality, isAudio,
                    phasePrefix, ct);
                return file;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastException = ex;
                // Clean up partial files before retrying
                CleanStemFiles(outputDir, stem);

                if (attempt < RetryStrategies.Length - 1)
                {
                    var nextMode = RetryStrategies[attempt + 1].formatMode;
                    EmitStatus(ctx, "downloading", 0,
                        $"{phasePrefix()}Retry {attempt + 2}/{RetryStrategies.Length} ({nextMode} format)...", null);
                }
            }
        }

        throw lastException ?? new Exception("All download strategies failed.");
    }

    private async Task<string?> RunYtDlpDownloadAsync(
        JobContext ctx, string url, string outputDir, string stem,
        string formatString, string quality, bool isAudio,
        Func<string> phasePrefix, CancellationToken ct)
    {
        var (exe, extra) = _ytDlpRunner ?? ("yt-dlp", "");
        var args = new StringBuilder();
        if (!string.IsNullOrEmpty(extra)) args.Append($"{extra} ");

        // FFmpeg location
        if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(_ffmpegPath);
            if (!string.IsNullOrEmpty(ffmpegDir))
                args.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
        }

        // Optional custom cookie file from Settings (never browser scraping)
        var cookieFile = ResolveCookiesFile();
        if (!string.IsNullOrEmpty(cookieFile))
        {
            args.Append($"--cookies \"{cookieFile}\" ");
        }

        // Format
        args.Append($"--newline -f \"{formatString}\" ");

        // Output template
        args.Append($"-o \"{Path.Combine(outputDir, $"{stem}.%(ext)s")}\" ");

        // Merge format for video
        if (!isAudio)
            args.Append("--merge-output-format mp4 ");

        // Audio postprocessing
        if (isAudio)
        {
            var audioFormat = "mp3";
            var audioQuality = "320";
            if (string.Equals(quality, "flac", StringComparison.OrdinalIgnoreCase))
            {
                audioFormat = "flac";
                audioQuality = "0";
            }
            else if (string.Equals(quality, "m4a", StringComparison.OrdinalIgnoreCase))
            {
                audioFormat = "m4a";
                audioQuality = "256";
            }
            args.Append($"--extract-audio --audio-format {audioFormat} --audio-quality {audioQuality} ");
        }

        // Misc flags
        args.Append("--no-playlist ");
        args.Append("--retries 10 --fragment-retries 15 --extractor-retries 5 ");
        args.Append("--socket-timeout 20 ");
        args.Append("--windows-filenames ");

        args.Append($"\"{url}\"");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
        };
        ConfigureProcessStartInfo(psi);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        ctx.RunningProcess = proc;
        var errorBuilder = new StringBuilder();
        var allOutputBuilder = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            EngineProvider.EmitOutput(e.Data);
            allOutputBuilder.AppendLine(e.Data);
            var match = YtDlpProgressRegex.Match(e.Data);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
            {
                var speed = match.Groups[3].Success ? match.Groups[3].Value : null;
                var etaStr = match.Groups[4].Success ? match.Groups[4].Value : null;
                int? eta = ParseEta(etaStr);

                string subPhase = isAudio ? "Download Audio" : "Download Video";
                EmitStatus(ctx, "downloading", pct, $"{phasePrefix()}{subPhase}: {pct:F1}%", speed, eta);
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                EngineProvider.EmitOutput($"[stderr] {e.Data.Trim()}");
                errorBuilder.AppendLine(e.Data.Trim());
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await WaitForExitOrKillAsync(proc, ct);

        if (proc.ExitCode != 0)
        {
            var errDetail = errorBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(errDetail))
            {
                var outStr = allOutputBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(outStr) && (outStr.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) || outStr.Contains("Traceback", StringComparison.OrdinalIgnoreCase)))
                {
                    errDetail = outStr;
                }
                else if (proc.ExitCode == 9009)
                {
                    errDetail = $"yt-dlp or ffmpeg command was not found or failed to spawn (exit code 9009). Runner: {exe}";
                }
                else
                {
                    errDetail = $"yt-dlp exited with error code {proc.ExitCode}";
                }
            }
            if (errDetail.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
                errDetail.Contains("This video is not available", StringComparison.OrdinalIgnoreCase) ||
                errDetail.Contains("Private video", StringComparison.OrdinalIgnoreCase))
            {
                errDetail = "Video is unavailable, private, or region-restricted.";
            }
            else if (errDetail.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase) ||
                     errDetail.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase))
            {
                errDetail = "YouTube verification required. For restricted videos, you can configure an optional cookies file in Settings → Advanced → Backend.";
            }
            throw new Exception(errDetail);
        }

        // Locate downloaded file
        return FindDownloadedFile(outputDir, stem);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  YT Music download  (mirrors ytmusic_downloader.py)
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<string?> DownloadYtMusicAsync(
        JobContext ctx, string url, string outputDir, string stem,
        string quality, Func<string> phasePrefix, CancellationToken ct)
    {
        // Determine audio format from quality profile
        string audioFormat, audioQuality;
        if (quality == "ytmusic_flac")
        {
            audioFormat = "flac";
            audioQuality = "0"; // lossless
        }
        else if (quality is "ytmusic_m4a" or "m4a")
        {
            audioFormat = "m4a";
            audioQuality = "256";
        }
        else // ytmusic_320k / default
        {
            audioFormat = "mp3";
            audioQuality = "320";
        }

        bool isTrack = url.Contains("watch?v=") || url.Contains("/watch?");

        var (exe, extra) = _ytDlpRunner ?? ("yt-dlp", "");

        string BuildYtMusicArgs()
        {
            var a = new StringBuilder();
            if (!string.IsNullOrEmpty(extra)) a.Append($"{extra} ");

            // FFmpeg location
            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
            {
                var ffmpegDir = Path.GetDirectoryName(_ffmpegPath);
                if (!string.IsNullOrEmpty(ffmpegDir))
                    a.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
            }

            // Optional custom cookie file from Settings
            var cookieFile = ResolveCookiesFile();
            if (!string.IsNullOrEmpty(cookieFile))
                a.Append($"--cookies \"{cookieFile}\" ");

            // Format & audio extraction
            a.Append("-f \"bestaudio/best\" ");
            a.Append($"--extract-audio --audio-format {audioFormat} --audio-quality {audioQuality} ");

            // Metadata & thumbnail embedding
            a.Append("--embed-thumbnail --add-metadata --write-thumbnail ");
            a.Append("--postprocessor-args \"ffmpeg:-id3v2_version 3\" ");

            // Output template
            a.Append($"-o \"{Path.Combine(outputDir, $"{stem}.%(ext)s")}\" ");

            // Single track vs playlist
            if (isTrack) a.Append("--no-playlist ");

            // Retry settings
            a.Append("--retries 5 --fragment-retries 10 --extractor-retries 3 --socket-timeout 15 ");
            a.Append("--windows-filenames ");

            a.Append("--newline ");
            a.Append($"\"{url}\"");
            return a.ToString();
        }

        var musicErrorBuilder = new StringBuilder();
        async Task<int> RunYtMusicProcess(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
            };
            ConfigureProcessStartInfo(psi);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ctx.RunningProcess = proc;

            proc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                EngineProvider.EmitOutput(e.Data);
                var match = YtDlpProgressRegex.Match(e.Data);
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
                {
                    var speed = match.Groups[3].Success ? match.Groups[3].Value : null;
                    var etaStr = match.Groups[4].Success ? match.Groups[4].Value : null;
                    int? eta = ParseEta(etaStr);
                    EmitStatus(ctx, "downloading", pct, $"{phasePrefix()}Download Music: {pct:F1}%", speed, eta);
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    EngineProvider.EmitOutput($"[stderr] {e.Data.Trim()}");
                    musicErrorBuilder.AppendLine(e.Data.Trim());
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await WaitForExitOrKillAsync(proc, ct);
            return proc.ExitCode;
        }

        // Primary attempt
        var exitCode = await RunYtMusicProcess(BuildYtMusicArgs());

        // If failed, retry once with backoff
        if (exitCode != 0)
        {
            CleanStemFiles(outputDir, stem);
            ct.ThrowIfCancellationRequested();

            EmitStatus(ctx, "downloading", 0, $"{phasePrefix()}Retrying music download...", null);
            await Task.Delay(1500, ct);

            exitCode = await RunYtMusicProcess(BuildYtMusicArgs());

            if (exitCode != 0)
            {
                var err = musicErrorBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(err))
                    err = $"YT Music download failed (exit code {exitCode})";

                if (err.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("This video is not available", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("Private video", StringComparison.OrdinalIgnoreCase))
                {
                    err = "Music track is unavailable, private, or region-restricted.";
                }
                else if (err.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase) ||
                         err.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase))
                {
                    err = "YouTube verification required. For restricted tracks, you can configure an optional cookies file in Settings → Advanced → Backend.";
                }
                throw new Exception(err);
            }
        }

        // Find downloaded file (skip thumbnail images)
        return FindDownloadedFile(outputDir, stem, skipImages: true);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fingerprint bypass  (mirrors transformer.py exactly)
    // ═════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, (double scale, string? vfExtra, string? afFilter, int crf, string preset, bool copyAudio)> FpMethods = new()
    {
        ["light"]    = (1.003, null, null, 23, "veryfast", true),
        ["standard"] = (1.005, "eq=brightness=0.006:saturation=1.01", "atempo=1.002", 21, "fast", false),
        ["strong"]   = (1.010, "eq=brightness=0.012:saturation=1.02:contrast=1.01,hue=h=1.5", "atempo=1.005", 19, "medium", false),
    };

    private static readonly Dictionary<string, Dictionary<string, string>> HwPresets = new()
    {
        ["h264_nvenc"] = new() { ["veryfast"] = "p1", ["fast"] = "p4", ["medium"] = "p5" },
        ["h264_qsv"]   = new() { ["veryfast"] = "veryfast", ["fast"] = "fast", ["medium"] = "medium" },
        ["h264_amf"]   = new() { ["veryfast"] = "speed", ["fast"] = "balanced", ["medium"] = "quality" },
    };

    private async Task<string> ApplyFingerprintAsync(
        JobContext ctx, string inputFile, string method,
        bool useGpu, Func<string> phasePrefix, CancellationToken ct)
    {
        var outFile = Path.Combine(
            Path.GetDirectoryName(inputFile)!,
            Path.GetFileNameWithoutExtension(inputFile) + "_fp.mp4");

        // ── Metadata-only method: stream copy, strip metadata ────────────
        if (method == "metadata")
        {
            var metaPsi = new ProcessStartInfo
            {
                FileName = _ffmpegPath ?? "ffmpeg",
                Arguments = $"-y -hide_banner -loglevel error -i \"{inputFile}\" -c copy -map_metadata -1 -movflags +faststart \"{outFile}\"",
            };
            ConfigureProcessStartInfo(metaPsi);

            var metaResult = await MediaTools.RunProcessAsync(metaPsi, ct);
            if (metaResult.ExitCode == 0 && File.Exists(outFile))
                return outFile;

            SafeDelete(outFile);
            return inputFile; // fallback: return original
        }

        // ── Filter-based methods ─────────────────────────────────────────
        if (!FpMethods.ContainsKey(method)) method = "standard";
        var (scaleFactor, vfExtra, afFilter, crf, preset, copyAudio) = FpMethods[method];

        // Probe video dimensions and duration
        var (w, h, duration) = await ProbeVideoAsync(inputFile, ct);
        if (w == null || h == null || w == 0 || h == 0)
            return inputFile;

        int sw = ((int)Math.Round(w.Value * scaleFactor) / 2) * 2;
        int sh = ((int)Math.Round(h.Value * scaleFactor) / 2) * 2;

        // Build video filter: fast_bilinear scale + crop back to original
        var vf = $"scale={sw}:{sh}:flags=fast_bilinear,crop={w}:{h}";
        if (!string.IsNullOrEmpty(vfExtra))
            vf += $",{vfExtra}";

        async Task<int> EncodeAsync(MediaTools.HardwareEncoder? hw)
        {
            var cmdParts = new List<string> { "-y" };

            // HW accel decode (even in CPU mode D3D11VA offloads decoding on Windows)
            cmdParts.Add("-hwaccel");
            cmdParts.Add(hw?.HwAccel ?? "d3d11va");

            cmdParts.Add("-i");
            cmdParts.Add(inputFile);
            cmdParts.Add("-vf");
            cmdParts.Add(vf);

            if (!string.IsNullOrEmpty(afFilter))
            {
                cmdParts.Add("-af");
                cmdParts.Add(afFilter);
            }

            switch (hw?.Encoder)
            {
                case "h264_nvenc":
                    cmdParts.AddRange(["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", crf.ToString(), "-preset", HwPresets["h264_nvenc"].GetValueOrDefault(preset, "p4"), "-bf", "2", "-b_ref_mode", "middle", "-rc-lookahead", "20"]);
                    break;
                case "h264_qsv":
                    cmdParts.AddRange(["-c:v", "h264_qsv", "-global_quality", crf.ToString(), "-preset", HwPresets["h264_qsv"].GetValueOrDefault(preset, "fast")]);
                    break;
                case "h264_amf":
                    cmdParts.AddRange(["-c:v", "h264_amf", "-qp_i", crf.ToString(), "-qp_p", crf.ToString(), "-quality", HwPresets["h264_amf"].GetValueOrDefault(preset, "balanced")]);
                    break;
                default:
                    cmdParts.AddRange(["-c:v", "libx264", "-crf", crf.ToString(), "-preset", preset, "-pix_fmt", "yuv420p"]);
                    break;
            }

            if (copyAudio)
                cmdParts.AddRange(["-c:a", "copy"]);
            else
                cmdParts.AddRange(["-c:a", "aac", "-b:a", "128k"]);

            cmdParts.AddRange(["-threads", "0", "-map_metadata", "-1", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-loglevel", "error"]);
            cmdParts.Add(outFile);

            var psi = new ProcessStartInfo { FileName = _ffmpegPath ?? "ffmpeg" };
            foreach (var part in cmdParts)
                psi.ArgumentList.Add(part);
            ConfigureProcessStartInfo(psi);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ctx.RunningProcess = proc;
            proc.Start();

            // Parse -progress pipe:1 output for precise progress
            var progressTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                    {
                        if (!line.StartsWith("out_time_us=")) continue;
                        if (long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0 && duration > 0)
                        {
                            var frac = Math.Min(us / 1_000_000.0 / duration, 1.0);
                            EmitStatus(ctx, "processing", 50.0 + frac * 50.0, $"{phasePrefix()}Encoding video: {frac * 100:F0}%", null);
                        }
                    }
                }
                catch { }
            }, CancellationToken.None);

            // Drain stderr to prevent deadlock
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            await WaitForExitOrKillAsync(proc, ct);
            await Task.WhenAll(progressTask, stderrTask);
            return proc.ExitCode;
        }

        var hwEncoder = useGpu ? await MediaTools.GetHardwareEncoderAsync(_ffmpegPath) : null;
        int exitCode = await EncodeAsync(hwEncoder);

        if (exitCode != 0 && hwEncoder != null && !ct.IsCancellationRequested)
        {
            // GPU encode failed (driver issue, unsupported resolution, ...) – retry on the CPU
            SafeDelete(outFile);
            EmitStatus(ctx, "processing", 50, $"{phasePrefix()}GPU encoder failed, retrying on CPU...", null);
            exitCode = await EncodeAsync(null);
        }

        if (exitCode != 0 || ct.IsCancellationRequested)
        {
            SafeDelete(outFile);
            return inputFile;
        }

        return File.Exists(outFile) ? outFile : inputFile;
    }

    private async Task<(int? width, int? height, double duration)> ProbeVideoAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobePath ?? "ffprobe",
                Arguments = $"-v quiet -print_format json -select_streams v:0 -show_streams -show_format \"{path}\"",
            };
            ConfigureProcessStartInfo(psi);

            var output = (await MediaTools.RunProcessAsync(psi, ct, timeoutMs: 60_000)).StdOut;

            if (string.IsNullOrWhiteSpace(output)) return (null, null, 0);

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var durEl))
            {
                if (durEl.ValueKind == JsonValueKind.String)
                    double.TryParse(durEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out duration);
                else if (durEl.ValueKind == JsonValueKind.Number)
                    durEl.TryGetDouble(out duration);
            }

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == "video")
                    {
                        int? w = stream.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : null;
                        int? h = stream.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : null;
                        return (w, h, duration);
                    }
                }
            }

            return (null, null, duration);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return (null, null, 0);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Subtitle generation (shells out to Python faster-whisper)
    // ═════════════════════════════════════════════════════════════════════════

    private const string SubtitleMarker = "__SRT_BEGIN__";

    public async Task<string> GenerateSubtitlesAsync(
        string videoPath, string modelSize = "base", string? language = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync().WaitAsync(ct);

        var py = _pythonWithYtDlp.Value ?? "python";
        var backendDir = GetBackendDir();

        // Paths are handed over as argv entries (ArgumentList does the quoting), and the SRT is
        // printed after a marker so log lines on stdout can never end up inside the subtitle file.
        const string script =
            "import sys; sys.path.insert(0, sys.argv[1]); from subtitler import generate_subtitles; " +
            "srt = generate_subtitles(sys.argv[2], sys.argv[3], None if sys.argv[4] == 'auto' else sys.argv[4]); " +
            "print('" + SubtitleMarker + "'); print(srt)";

        var psi = new ProcessStartInfo { FileName = py };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(backendDir);
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add(modelSize);
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(language) ? "auto" : language);
        ConfigureProcessStartInfo(psi);

        var result = await MediaTools.RunProcessAsync(psi, ct);
        EngineProvider.EmitOutput(result.StdErr.Trim());

        var idx = result.StdOut.IndexOf(SubtitleMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            if (result.ExitCode != 0)
                throw new Exception(LastLine(result.StdErr) ?? $"Subtitle generation failed (exit code {result.ExitCode}).");
            return "";
        }

        return result.StdOut[(idx + SubtitleMarker.Length)..].Trim();
    }

    private static string GetBackendDir()
    {
        var backendDir = Path.Combine(MediaTools.FindSolutionDir(), "backend");
        return Directory.Exists(backendDir)
            ? backendDir
            : Path.Combine(MediaTools.AppDir, "backend");
    }

    private static string? LastLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    public async Task<PlaylistInfo?> FetchPlaylistAsync(
        string url, int maxItems = 100,
        CancellationToken ct = default)
    {
        var cleanUrl = CleanUrl(url);
        if (string.IsNullOrWhiteSpace(cleanUrl)) return null;

        await EnsureInitializedAsync().WaitAsync(ct);

        // 1. Native yt-dlp flat-playlist extraction (independent of Python)
        try
        {
            var (exe, extra) = _ytDlpRunner ?? ("yt-dlp", "");
            var args = new StringBuilder();
            if (!string.IsNullOrEmpty(extra)) args.Append($"{extra} ");

            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
            {
                var ffmpegDir = Path.GetDirectoryName(_ffmpegPath);
                if (!string.IsNullOrEmpty(ffmpegDir))
                    args.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
            }

            args.Append($"--flat-playlist --playlist-end {maxItems} -J --skip-download --no-warnings ");
            args.Append("--user-agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36\" ");
            args.Append($"\"{cleanUrl}\"");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString(),
            };
            ConfigureProcessStartInfo(psi);

            var stdout = (await MediaTools.RunProcessAsync(psi, ct, timeoutMs: 120_000)).StdOut;
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                var firstBrace = stdout.IndexOf('{');
                var lastBrace = stdout.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    var jsonStr = stdout.Substring(firstBrace, lastBrace - firstBrace + 1);
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    var playlistTitle = root.TryGetProperty("title", out var pt) ? pt.GetString() ?? "Playlist" : "Playlist";
                    var result = new PlaylistInfo
                    {
                        Title = playlistTitle,
                        Count = 0,
                        Entries = new List<PlaylistEntry>()
                    };

                    if (root.TryGetProperty("entries", out var entriesElem) && entriesElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in entriesElem.EnumerateArray())
                        {
                            var vidId = item.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? "" : "";
                            var itemUrl = item.TryGetProperty("url", out var urlElem) ? urlElem.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(itemUrl) && !string.IsNullOrEmpty(vidId))
                                itemUrl = $"https://www.youtube.com/watch?v={vidId}";
                            else if (!string.IsNullOrEmpty(itemUrl) && !itemUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                itemUrl = $"https://www.youtube.com/watch?v={vidId}";

                            if (string.IsNullOrEmpty(itemUrl)) continue;

                            var itemTitle = item.TryGetProperty("title", out var te) ? te.GetString() ?? "Untitled Video" : "Untitled Video";
                            var itemUploader = item.TryGetProperty("uploader", out var ue) ? ue.GetString() ?? "" : (item.TryGetProperty("channel", out var ce) ? ce.GetString() ?? "" : "");
                            double itemDur = item.TryGetProperty("duration", out var de) && de.TryGetDouble(out var dv) ? dv : 0;
                            string thumb = "";
                            if (item.TryGetProperty("thumbnail", out var the) && the.ValueKind == JsonValueKind.String)
                            {
                                thumb = the.GetString() ?? "";
                            }
                            if (string.IsNullOrEmpty(thumb) && item.TryGetProperty("thumbnails", out var thArr) && thArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tElem in thArr.EnumerateArray())
                                {
                                    if (tElem.TryGetProperty("url", out var uElem) && uElem.ValueKind == JsonValueKind.String)
                                    {
                                        var u = uElem.GetString();
                                        if (!string.IsNullOrEmpty(u)) thumb = u;
                                    }
                                }
                            }
                            if (string.IsNullOrEmpty(thumb) && !string.IsNullOrEmpty(vidId))
                            {
                                thumb = $"https://i.ytimg.com/vi/{vidId}/hqdefault.jpg";
                            }

                            result.Entries.Add(new PlaylistEntry
                            {
                                Id = vidId,
                                Title = itemTitle,
                                Url = itemUrl,
                                Duration = itemDur,
                                Uploader = itemUploader,
                                Thumbnail = thumb
                            });
                        }
                    }

                    if (result.Entries.Count > 0)
                    {
                        result.Count = result.Entries.Count;
                        return result;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        // 2. Fallback: python downloader script (only useful when a Python with yt-dlp exists)
        var py = _pythonWithYtDlp.Value;
        if (py != null)
        {
            try
            {
                const string marker = "__PL_JSON__";
                const string script =
                    "import sys, json; sys.path.insert(0, sys.argv[1]); from downloader import fetch_playlist_entries; " +
                    "print('" + marker + "' + json.dumps(fetch_playlist_entries(sys.argv[2], int(sys.argv[3]))))";

                var psi = new ProcessStartInfo { FileName = py };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(GetBackendDir());
                psi.ArgumentList.Add(cleanUrl);
                psi.ArgumentList.Add(maxItems.ToString(CultureInfo.InvariantCulture));
                ConfigureProcessStartInfo(psi);

                var stdout = (await MediaTools.RunProcessAsync(psi, ct, timeoutMs: 120_000)).StdOut;
                var idx = stdout.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var json = stdout[(idx + marker.Length)..].Trim();
                    return JsonSerializer.Deserialize<PlaylistInfo>(json, _jso);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        // 3. Fallback: single video metadata extraction
        try
        {
            var meta = await FetchMetadataAsync(cleanUrl, ct);
            if (meta != null && !string.IsNullOrWhiteSpace(meta.Title))
            {
                return new PlaylistInfo
                {
                    Title = meta.Title,
                    Count = 1,
                    Entries = new List<PlaylistEntry>
                    {
                        new()
                        {
                            Id = Guid.NewGuid().ToString("N")[..8],
                            Title = meta.Title,
                            Url = cleanUrl,
                            Duration = meta.Duration,
                            Uploader = meta.Uploader,
                            Thumbnail = meta.Thumbnail
                        }
                    }
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Streaming & cancellation
    // ═════════════════════════════════════════════════════════════════════════

    public async IAsyncEnumerable<JobStatus> StreamJobAsync(
        string jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var ctx))
            yield break;

        var reader = ctx.Channel.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                yield return item;
                if (item.Status is "completed" or "error" or "cancelled")
                    yield break;
            }
        }
    }

    public Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var ctx))
        {
            try
            {
                ctx.Cts.Cancel();
                if (ctx.RunningProcess is { HasExited: false })
                {
                    ctx.RunningProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var ctx in _jobs.Values)
        {
            try
            {
                ctx.Cts.Cancel();
                ctx.RunningProcess?.Kill(entireProcessTree: true);
                ctx.RunningProcess?.Dispose();
            }
            catch { }
        }
        _jobs.Clear();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static async Task WaitForExitOrKillAsync(Process proc, CancellationToken ct)
    {
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private void EmitStatus(
        JobContext ctx, string status, double progress, string message,
        string? speed = null, int? eta = null,
        Dictionary<string, object>? result = null, string? error = null)
    {
        var s = new JobStatus
        {
            Id = ctx.JobId,
            Status = status,
            Progress = Math.Clamp(progress, 0, 100),
            Message = message,
            Speed = speed,
            Eta = eta,
            Result = result,
            Error = error,
        };

        ctx.LastStatus = s;
        ctx.Channel.Writer.TryWrite(s);

        // Progress updates arrive many times per second (and yt-dlp already echoes its raw
        // progress lines), so only log a status line to the console when the phase changes.
        var phase = status + "|" + ProgressNumberRegex.Replace(message, "");
        if (phase != ctx.LastLoggedPhase)
        {
            ctx.LastLoggedPhase = phase;
            EngineProvider.EmitOutput($"[{status.ToUpperInvariant()}] {message}");
        }
    }

    private static readonly Regex ProgressNumberRegex = new(@"[\d.,]+\s*%?", RegexOptions.Compiled);

    /// <summary>
    /// Build yt-dlp format string matching Python downloader.py logic.
    /// Handles both "1080p" and "1080" quality formats.
    /// </summary>
    private static string GetFormatString(string? quality, bool isAudio, string formatMode)
    {
        if (isAudio)
            return "bestaudio[ext=m4a]/bestaudio/best";

        // Normalize quality: strip trailing "p" if present
        var q = (quality ?? "best").Trim().ToLowerInvariant();
        if (q.EndsWith('p') && q.Length > 1)
            q = q[..^1]; // "1080p" -> "1080"

        if (formatMode == "progressive")
        {
            if (q is "2160" or "1440" or "1080" or "720" or "480" or "360")
                return $"best[height<={q}]/best";
            return "best[height<=1080]/best";
        }

        if (formatMode == "any")
            return "bestvideo*+bestaudio/best";

        // formatMode == "best"
        if (q is "2160" or "1440" or "1080" or "720" or "480" or "360")
            return $"bestvideo[height<={q}][ext=mp4]+bestaudio[ext=m4a]/"
                 + $"bestvideo[height<={q}]+bestaudio/best";

        return "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best";
    }

    private static bool IsYtMusicUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.Contains("music.youtube.com");
    }

    private static string CleanUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return InvisibleCharsRegex.Replace(raw, "").Trim();
    }

    private static readonly Regex InvalidFileNameCharsRegex = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);

    private static string SanitizeFilename(string name)
    {
        var clean = InvalidFileNameCharsRegex.Replace(name, "_").Trim(' ', '.');
        // Check Windows reserved names
        if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase))
            clean = $"_{clean}";
        return clean.Length > 80 ? clean[..80] : (clean.Length > 0 ? clean : "video");
    }

    private static int? ParseEta(string? etaStr)
    {
        if (string.IsNullOrWhiteSpace(etaStr)) return null;
        var parts = etaStr.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
            return m * 60 + s;
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var mm) && int.TryParse(parts[2], out var ss))
            return h * 3600 + mm * 60 + ss;
        return null;
    }

    /// <summary>
    /// Find a downloaded file by its stem, skipping partial/temp files.
    /// </summary>
    private static string? FindDownloadedFile(string dir, string stem, bool skipImages = false)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, $"{stem}.*"))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".part" or ".ytdl" or ".temp" or ".aria2")
                    continue;
                if (skipImages && ext is ".jpg" or ".jpeg" or ".webp" or ".png")
                    continue;
                if (File.Exists(f))
                    return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Clean up all temp/intermediate files matching a stem prefix (mirrors _clean_stem_files).
    /// </summary>
    private static void CleanStemFiles(string dir, string stem, string? excludePath = null)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
            return;

        var excludeAbs = excludePath != null ? Path.GetFullPath(excludePath) : null;

        try
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                if (!Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (excludeAbs != null && string.Equals(Path.GetFullPath(f), excludeAbs, StringComparison.OrdinalIgnoreCase))
                    continue;

                SafeDelete(f);
            }
        }
        catch { }
    }

    private static void SafeDelete(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }
}
