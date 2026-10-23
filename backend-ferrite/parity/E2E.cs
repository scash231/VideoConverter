using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoConverterUI.Models;
using VideoConverterUI.Services;

namespace ParityRunner;

/// <summary>Runs the same scenario through Native and Ferrite and compares what the UI would see.</summary>
internal static class E2E
{
    private sealed record Scenario(string Name, string Kind, Dictionary<string, object?> P);

    private sealed class Capture
    {
        public List<JobStatus> Statuses { get; } = new();
        public List<string> Console { get; } = new();
        public string? Error;
        public object? Value;
        public List<string> Files { get; } = new();
        public string? RequestBody;
        public TimeSpan Elapsed;
    }

    private const string Zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
    private const string Rick = "https://music.youtube.com/watch?v=dQw4w9WgXcQ";

    private static List<Scenario> All() =>
    [
        new("metadata_youtube", "metadata", new() { ["url"] = Zoo }),
        new("metadata_ytmusic", "ytmusic_metadata", new() { ["url"] = Rick }),
        new("metadata_unsupported", "metadata", new() { ["url"] = "https://example.com/not-a-video" }),
        new("playlist_youtube", "playlist", new() { ["url"] = "https://www.youtube.com/playlist?list=PL4lCao7KL_QFVb7Iudeipvc2BCavECqzc", ["max"] = 10 }),
        new("playlist_single_video", "playlist", new() { ["url"] = Zoo, ["max"] = 10 }),
        new("video_best_title", "job", new() { ["url"] = Zoo, ["title"] = "Zoo Clip", ["quality"] = "best" }),
        new("video_720_no_title", "job", new() { ["url"] = Zoo, ["quality"] = "720p" }),
        new("audio_mp3", "job", new() { ["url"] = Zoo, ["title"] = "Zoo Audio", ["quality"] = "mp3" }),
        new("audio_m4a", "job", new() { ["url"] = Zoo, ["title"] = "Zoo Audio", ["quality"] = "m4a" }),
        new("audio_flac", "job", new() { ["url"] = Zoo, ["title"] = "Zoo Audio", ["quality"] = "flac" }),
        new("fp_light", "job", new() { ["url"] = Zoo, ["title"] = "FP", ["fingerprint"] = true, ["method"] = "light" }),
        new("fp_standard", "job", new() { ["url"] = Zoo, ["title"] = "FP", ["fingerprint"] = true, ["method"] = "standard" }),
        new("fp_strong", "job", new() { ["url"] = Zoo, ["title"] = "FP", ["fingerprint"] = true, ["method"] = "strong" }),
        new("fp_metadata", "job", new() { ["url"] = Zoo, ["title"] = "FP", ["fingerprint"] = true, ["method"] = "metadata" }),
        new("fp_standard_gpu", "job", new() { ["url"] = Zoo, ["title"] = "FP", ["fingerprint"] = true, ["method"] = "standard", ["gpu"] = true }),
        new("ytmusic_320k", "job", new() { ["url"] = Rick, ["title"] = "Rick", ["platform"] = "ytmusic", ["quality"] = "ytmusic_320k" }),
        new("ytmusic_m4a", "job", new() { ["url"] = Rick, ["title"] = "Rick", ["platform"] = "ytmusic", ["quality"] = "ytmusic_m4a" }),
        new("ytmusic_flac", "job", new() { ["url"] = Rick, ["title"] = "Rick", ["platform"] = "ytmusic", ["quality"] = "ytmusic_flac" }),
        new("tiktok", "job", new() { ["url"] = "https://www.tiktok.com/@scout2015/video/6718335390845095173", ["title"] = "TikTok" }),
        new("instagram", "job", new() { ["url"] = "https://www.instagram.com/reel/Chunk8-jurw/", ["title"] = "Insta" }),
        new("unavailable_video", "job", new() { ["url"] = "https://www.youtube.com/watch?v=xxxxxxxxxxx", ["title"] = "Nope" }),
        new("cancel_during_download", "job", new() { ["url"] = Rick, ["title"] = "Cancel", ["platform"] = "ytmusic", ["quality"] = "ytmusic_flac", ["cancel_on_progress"] = true }),
        new("cloud_custom_folder", "job", new() { ["url"] = Zoo, ["title"] = "Cloud", ["cloud"] = "custom" }),
        new("cloud_custom_temp_only", "job", new() { ["url"] = Zoo, ["title"] = "CloudTemp", ["cloud"] = "custom", ["temp_only"] = true }),
        new("cloud_webhook", "job", new() { ["url"] = Zoo, ["title"] = "Hook", ["cloud"] = "webhook" }),
        new("cloud_gdrive_missing", "job", new() { ["url"] = Zoo, ["title"] = "GD", ["cloud"] = "gdrive" }),
        new("subtitles", "subtitles", new() { ["url"] = Zoo }),
    ];

    public static async Task<int> RunAsync(string[] names)
    {
        var scenarios = All().Where(s => names.Length == 0 || names.Contains(s.Name)).ToList();
        var outRoot = Path.Combine(Path.GetTempPath(), "ferrite_parity");
        var report = new StringBuilder();
        report.AppendLine($"# Native vs. Ferrite end-to-end run ({DateTime.Now:yyyy-MM-dd HH:mm})");
        report.AppendLine();
        report.AppendLine("| Scenario | Result | Details |");
        report.AppendLine("|---|---|---|");
        int failures = 0;

        EngineProvider.OutputReceived += line => _console?.Add(line);
        foreach (var s in scenarios)
        {
            Console.WriteLine($"── {s.Name}");
            var native = await RunAsync(s, "native", Path.Combine(outRoot, s.Name, "native"));
            var ferrite = await RunAsync(s, "ferrite", Path.Combine(outRoot, s.Name, "ferrite"));
            var diffs = Compare(s, native, ferrite);
            var summary = Summary(s, native);
            if (diffs.Count == 0)
            {
                report.AppendLine($"| {s.Name} | ✅ identical | {summary} |");
                Console.WriteLine($"   identical ({summary})");
            }
            else
            {
                failures++;
                report.AppendLine($"| {s.Name} | ❌ differs | {string.Join("<br>", diffs.Select(Escape))} |");
                foreach (var d in diffs) Console.WriteLine($"   DIFF {d}");
            }
        }

        var path = Path.Combine(Program.ParityDir, "results", "e2e-report.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static List<string>? _console;

    private static IEngineService Create(string engine) => engine == "native" ? new NativeEngineService() : new FerriteBackend();

    private static async Task<Capture> RunAsync(Scenario s, string engineName, string outDir)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var cap = new Capture();
        _console = cap.Console;
        using var engine = Create(engineName);
        var sw = Stopwatch.StartNew();
        var settings = SettingsService.Current;
        var savedCustom = settings.CustomCloudFolderPath;
        var savedHook = settings.WebhookUrl;
        var savedGDrive = settings.GDriveFolderPath;
        HttpListener? listener = null;
        Task<string?>? bodyTask = null;
        try
        {
            await engine.WaitForHealthAsync(CancellationToken.None);
            switch (s.Kind)
            {
                case "metadata":
                    cap.Value = await engine.FetchMetadataAsync((string)s.P["url"]!);
                    break;
                case "ytmusic_metadata":
                    cap.Value = await engine.FetchYTMusicMetadataAsync((string)s.P["url"]!);
                    break;
                case "playlist":
                    cap.Value = await engine.FetchPlaylistAsync((string)s.P["url"]!, (int)s.P["max"]!);
                    break;
                case "subtitles":
                {
                    var src = Path.Combine(Path.GetTempPath(), "ferrite_parity", "zoo_for_subs.mp4");
                    if (!File.Exists(src)) await DownloadOnceAsync(src);
                    cap.Value = await engine.GenerateSubtitlesAsync(src, "base");
                    break;
                }
                case "job":
                {
                    string? cloud = s.P.GetValueOrDefault("cloud") as string;
                    bool tempOnly = s.P.GetValueOrDefault("temp_only") as bool? ?? false;
                    if (cloud == "custom")
                    {
                        var dest = Path.Combine(outDir, "cloud_dest");
                        Directory.CreateDirectory(dest);
                        settings.CustomCloudFolderPath = dest;
                    }
                    if (cloud == "gdrive")
                        settings.GDriveFolderPath = @"Q:\no_drive_here";
                    if (cloud == "webhook")
                    {
                        var port = FreePort();
                        listener = new HttpListener();
                        listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
                        listener.Start();
                        settings.WebhookUrl = $"http://127.0.0.1:{port}/hook/";
                        bodyTask = ReceiveOnceAsync(listener);
                    }

                    var jobId = await engine.CreateJobAsync(
                        (string)s.P["url"]!, "download", s.P.GetValueOrDefault("platform") as string ?? "auto",
                        s.P.GetValueOrDefault("title") as string, null, null, outDir, null,
                        s.P.GetValueOrDefault("fingerprint") as bool? ?? false,
                        s.P.GetValueOrDefault("method") as string ?? "standard",
                        s.P.GetValueOrDefault("gpu") as bool? ?? false,
                        s.P.GetValueOrDefault("quality") as string ?? "best",
                        CancellationToken.None,
                        cloudUpload: cloud != null, cloudProvider: cloud, cloudTempOnly: tempOnly);

                    // Cancel at the first real download progress, so both engines are cancelled in the same phase.
                    bool cancelOnProgress = s.P.GetValueOrDefault("cancel_on_progress") as bool? ?? false;
                    bool cancelSent = false;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    await foreach (var st in engine.StreamJobAsync(jobId, timeout.Token))
                    {
                        cap.Statuses.Add(st);
                        if (cancelOnProgress && !cancelSent && st.Status == "downloading" && st.Progress > 0)
                        {
                            cancelSent = true;
                            await engine.CancelJobAsync(jobId);
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            cap.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            settings.CustomCloudFolderPath = savedCustom;
            settings.WebhookUrl = savedHook;
            settings.GDriveFolderPath = savedGDrive;
        }

        if (bodyTask != null)
        {
            cap.RequestBody = await Task.WhenAny(bodyTask, Task.Delay(5000)) == bodyTask ? await bodyTask : null;
            listener?.Stop();
        }

        // Give a job's cleanup (temp dirs, stem files) a moment, then list what is left on disk.
        await Task.Delay(500);
        cap.Elapsed = sw.Elapsed;
        foreach (var f in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(outDir, f);
            var bytes = await File.ReadAllBytesAsync(f);
            cap.Files.Add($"{Normalize(rel, outDir)} ({bytes.Length} bytes, sha256 {Convert.ToHexString(SHA256.HashData(bytes))[..16]})");
        }
        _console = null;
        return cap;
    }

    private static async Task DownloadOnceAsync(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var engine = new NativeEngineService();
        var dir = Path.GetDirectoryName(target)!;
        var id = await engine.CreateJobAsync(Zoo, "download", "auto", "zoo_for_subs", null, null, dir, null, false, "standard", false, "best");
        await foreach (var _ in engine.StreamJobAsync(id)) { }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string?> ReceiveOnceAsync(HttpListener listener)
    {
        var ctx = await listener.GetContextAsync();
        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms);
        var head = $"Content-Type: {ctx.Request.ContentType}\nContent-Length: {ctx.Request.ContentLength64}\n";
        var body = Encoding.UTF8.GetString(ms.ToArray());
        var json = Encoding.UTF8.GetBytes("{\"attachments\":[{\"url\":\"https://cdn.example/x.mp4\"}]}");
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(json);
        ctx.Response.Close();
        return head + body;
    }

    private static readonly Regex Hex12 = new(@"\b[0-9a-f]{12}\b");
    private static readonly Regex JobDir = new(@"ReelsConverter\\jobs\\[0-9a-f]{8}");
    private static readonly Regex Boundary = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
    private static readonly Regex Numbers = new(@"[\d.,]+\s*%?");

    private static string Normalize(string? s, string outDir)
    {
        if (s == null) return "<null>";
        s = s.Replace(outDir, "{OUT}", StringComparison.OrdinalIgnoreCase);
        s = s.Replace(outDir.Replace("\\native", "\\ferrite"), "{OUT}", StringComparison.OrdinalIgnoreCase);
        s = s.Replace(outDir.Replace("\\ferrite", "\\native"), "{OUT}", StringComparison.OrdinalIgnoreCase);
        s = JobDir.Replace(s, @"ReelsConverter\jobs\{JOB}");
        s = Boundary.Replace(s, "{BOUNDARY}");
        s = Hex12.Replace(s, "{STEM}");
        return s;
    }

    private static string Phase(JobStatus st, string outDir) => $"{st.Status}|{Numbers.Replace(Normalize(st.Message, outDir), "")}";

    private static List<string> Phases(Capture c, string outDir)
    {
        var list = new List<string>();
        foreach (var st in c.Statuses)
        {
            var p = Phase(st, outDir);
            if (list.Count == 0 || list[^1] != p) list.Add(p);
        }
        return list;
    }

    private static string Describe(JobStatus st, string outDir)
    {
        var result = st.Result == null
            ? "null"
            : string.Join(", ", st.Result.Select(kv => $"{kv.Key}={Normalize(kv.Value?.ToString(), outDir)} ({kv.Value?.GetType().Name})"));
        return $"{st.Status} {st.Progress} '{Normalize(st.Message, outDir)}' error='{Normalize(st.Error, outDir)}' result=[{result}]";
    }

    private static List<string> Compare(Scenario s, Capture n, Capture f)
    {
        var diffs = new List<string>();
        var nd = Path.Combine(Path.GetTempPath(), "ferrite_parity", s.Name, "native");
        var fd = Path.Combine(Path.GetTempPath(), "ferrite_parity", s.Name, "ferrite");

        if (Normalize(n.Error, nd) != Normalize(f.Error, fd))
            diffs.Add($"exception: native '{n.Error}' / ferrite '{f.Error}'");

        var nv = JsonSerializer.Serialize(n.Value);
        var fv = JsonSerializer.Serialize(f.Value);
        if (s.Kind is "playlist")
        {
            // Stage-3 single-video playlists get a random 8-hex id.
            nv = Regex.Replace(nv, "\"id\":\"[0-9a-f]{8}\"", "\"id\":\"{ID}\"");
            fv = Regex.Replace(fv, "\"id\":\"[0-9a-f]{8}\"", "\"id\":\"{ID}\"");
        }
        if (Normalize(nv, nd) != Normalize(fv, fd))
            diffs.Add($"value: native {Trim(nv)} / ferrite {Trim(fv)}");

        if (s.Kind == "job")
        {
            var np = Phases(n, nd);
            var fp = Phases(f, fd);
            if (!np.SequenceEqual(fp))
                diffs.Add($"phases: native [{string.Join(" → ", np)}] / ferrite [{string.Join(" → ", fp)}]");

            var nl = n.Statuses.LastOrDefault();
            var fl = f.Statuses.LastOrDefault();
            var nls = nl == null ? "<none>" : Describe(nl, nd);
            var fls = fl == null ? "<none>" : Describe(fl, fd);
            if (nls != fls)
                diffs.Add($"final status: native {nls} / ferrite {fls}");

            if (n.Statuses.Any(x => x.Progress is < 0 or > 100) || f.Statuses.Any(x => x.Progress is < 0 or > 100))
                diffs.Add("progress outside 0..100");

            var nc = n.Console.Where(l => Regex.IsMatch(l, @"^\[(DOWNLOADING|PROCESSING|UPLOADING|COMPLETED|ERROR|CANCELLED)\]")).Select(l => Numbers.Replace(Normalize(l, nd), "")).Distinct().ToList();
            var fc = f.Console.Where(l => Regex.IsMatch(l, @"^\[(DOWNLOADING|PROCESSING|UPLOADING|COMPLETED|ERROR|CANCELLED)\]")).Select(l => Numbers.Replace(Normalize(l, fd), "")).Distinct().ToList();
            if (!nc.SequenceEqual(fc))
                diffs.Add($"console phases: native [{string.Join(" | ", nc)}] / ferrite [{string.Join(" | ", fc)}]");

            if (!n.Files.SequenceEqual(f.Files))
                diffs.Add($"files: native [{string.Join("; ", n.Files)}] / ferrite [{string.Join("; ", f.Files)}]");

            if (Normalize(n.RequestBody, nd) != Normalize(f.RequestBody, fd))
                diffs.Add($"webhook request differs: native {Trim(n.RequestBody)} / ferrite {Trim(f.RequestBody)}");
        }
        return diffs;
    }

    private static string Trim(string? s) => s == null ? "null" : (s.Length > 300 ? s[..300] + "…" : s);

    private static string Summary(Scenario s, Capture n)
    {
        var nd = Path.Combine(Path.GetTempPath(), "ferrite_parity", s.Name, "native");
        if (s.Kind != "job")
            return n.Error != null ? $"both threw: {Escape(n.Error)}" : $"value {Escape(Trim(JsonSerializer.Serialize(n.Value)).Replace("{OUT}", ""))}";
        var last = n.Statuses.LastOrDefault();
        var files = n.Files.Count == 0 ? "no files" : string.Join("; ", n.Files);
        var body = n.RequestBody != null ? "; webhook body identical" : "";
        return last == null ? "no status" : $"{last.Status}: {Escape(Normalize(last.Error ?? last.Message, nd))}; {Phases(n, nd).Count} phases; {Escape(files)}{body}";
    }
}
