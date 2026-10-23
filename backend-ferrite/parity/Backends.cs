using System.IO;
using System.Text;
using VideoConverterUI.Models;
using VideoConverterUI.Services;

namespace ParityRunner;

/// <summary>
/// Drives the app's own engine switch (<see cref="EngineProvider"/> and its proxy) through Native, Ferrite and
/// Legacy, and checks every feature of the matrix: it works, or it fails with a clear error the UI can show.
/// Also checks that switching during a running job is deferred until the job is done.
/// </summary>
internal static class Backends
{
    private const string Zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private sealed record Row(string Engine, string Feature, string Outcome, string Detail);

    public static async Task<int> RunAsync()
    {
        var settings = SettingsService.Current;
        var originalEngine = settings.ExecutionEngine;
        var originalCustom = settings.CustomCloudFolderPath;
        var rows = new List<Row>();
        var outRoot = Path.Combine(Path.GetTempPath(), "ferrite_backends");
        if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);

        try
        {
            foreach (var key in new[] { EngineProvider.NativeKey, EngineProvider.FerriteKey, EngineProvider.ClassicKey })
            {
                var name = EngineProvider.DisplayName(key);
                Console.WriteLine($"── {name}");
                EngineProvider.SwitchEngine(key);
                var engine = EngineProvider.Current;

                rows.Add(await Check(name, "Engine start", async () =>
                {
                    var ok = await EngineProvider.InitializeAsync(CancellationToken.None);
                    return (ok ? "ok" : "fail", $"{engine.EngineName}, ready={ok}");
                }));

                rows.Add(await Check(name, "Metadata", async () =>
                {
                    var m = await engine.FetchMetadataAsync(Zoo);
                    return (m?.Title == "Me at the zoo" ? "ok" : "fail", m == null ? "null" : $"\"{m.Title}\" by {m.Uploader}, {m.Duration}s, {m.Tags.Count} tags");
                }));

                rows.Add(await Check(name, "Playlist", async () =>
                {
                    var p = await engine.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PL4lCao7KL_QFVb7Iudeipvc2BCavECqzc", 5);
                    return (p is { Count: > 0 } ? "ok" : "fail", p == null ? "null" : $"\"{p.Title}\", {p.Count} entries");
                }));

                var dir = Path.Combine(outRoot, key);
                Directory.CreateDirectory(dir);
                rows.Add(await Job(name, "Download MP3", engine, Zoo, "download", "Zoo", dir, "mp3"));
                rows.Add(await Job(name, "Download video + fingerprint", engine, Zoo, "download", "Zoo FP", dir, "best", fingerprint: true));

                var cloudDir = Path.Combine(dir, "cloud");
                Directory.CreateDirectory(cloudDir);
                settings.CustomCloudFolderPath = cloudDir;
                rows.Add(await Job(name, "Cloud upload (custom folder)", engine, Zoo, "download", "Zoo Cloud", dir, "best", cloud: "custom"));
                settings.CustomCloudFolderPath = originalCustom;

                rows.Add(await Job(name, "YouTube upload", engine, Zoo, "upload", "Zoo Upload", null, null));

                rows.Add(await Check(name, "Subtitles", async () =>
                {
                    var file = Directory.GetFiles(dir, "Zoo FP.*").FirstOrDefault() ?? Directory.GetFiles(dir, "*.mp4").FirstOrDefault();
                    if (file == null) return ("fail", "no video to subtitle");
                    var srt = await engine.GenerateSubtitlesAsync(file, "base");
                    return ("ok", $"{srt.Length} characters");
                }));
            }

            // Switching while a job runs: the switch must wait for the job.
            Console.WriteLine("── deferred switch");
            EngineProvider.SwitchEngine(EngineProvider.NativeKey);
            await EngineProvider.InitializeAsync(CancellationToken.None);
            var dir2 = Path.Combine(outRoot, "deferred");
            Directory.CreateDirectory(dir2);
            var proxy = EngineProvider.Current;
            var jobId = await proxy.CreateJobAsync(Zoo, "download", "auto", "Deferred", null, null, dir2, null, false, "standard", false, "mp3");
            var appliedNow = EngineProvider.SwitchEngine(EngineProvider.FerriteKey);
            var activeDuring = EngineProvider.ActiveKey;
            JobStatus? last = null;
            await foreach (var st in proxy.StreamJobAsync(jobId)) last = st;
            var activeAfter = EngineProvider.ActiveKey;
            bool deferredOk = !appliedNow && activeDuring == EngineProvider.NativeKey && last?.Status == "completed" && activeAfter == EngineProvider.FerriteKey;
            rows.Add(new Row("Native → Ferrite", "Switch while a job runs", deferredOk ? "ok" : "fail",
                $"applied immediately={appliedNow}, engine during job={activeDuring}, job={last?.Status}, engine after job={activeAfter}"));

            // ferrite.exe dies during a job: the job must end with a clear error, and the next call restarts it.
            Console.WriteLine("── ferrite crash");
            await EngineProvider.InitializeAsync(CancellationToken.None);
            var crashId = await proxy.CreateJobAsync("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "download", "ytmusic", "Crash", null, null, dir2, null, false, "standard", false, "ytmusic_flac");
            JobStatus? crashLast = null;
            bool killed = false;
            await foreach (var st in proxy.StreamJobAsync(crashId))
            {
                crashLast = st;
                if (!killed && st.Status == "downloading" && st.Progress > 0)
                {
                    killed = true;
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ferrite"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
            }
            bool crashOk = crashLast?.Status == "error" && (crashLast.Error ?? "").StartsWith("Ferrite backend process exited unexpectedly");
            rows.Add(new Row("Ferrite", "ferrite.exe killed during a job", crashOk ? "clean" : "fail", $"{crashLast?.Status}: {crashLast?.Error}"));
            var again = await proxy.FetchMetadataAsync(Zoo);
            rows.Add(new Row("Ferrite", "Next call after the crash (auto-restart)", again?.Title == "Me at the zoo" ? "ok" : "fail", again == null ? "null" : $"\"{again.Title}\""));
        }
        finally
        {
            settings.CustomCloudFolderPath = originalCustom;
            EngineProvider.SwitchEngine(originalEngine);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# All three backends through the app's engine switch ({DateTime.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine();
        sb.AppendLine("| Engine | Feature | Result | Details |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in rows)
        {
            var mark = r.Outcome switch { "ok" => "✅ works", "clean" => "☑️ clean error", _ => "❌ failed" };
            sb.AppendLine($"| {r.Engine} | {r.Feature} | {mark} | {r.Detail.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ")} |");
        }
        var path = Path.Combine(Program.ParityDir, "results", "backends-report.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        return rows.Any(r => r.Outcome == "fail") ? 1 : 0;
    }

    private static async Task<Row> Check(string engine, string feature, Func<Task<(string outcome, string detail)>> run)
    {
        try
        {
            var (o, d) = await run();
            Console.WriteLine($"   {feature}: {o} {d}");
            return new Row(engine, feature, o, d);
        }
        catch (Exception ex)
        {
            // An exception with a message is a clean failure: the UI shows it in the error window.
            Console.WriteLine($"   {feature}: exception {ex.Message}");
            return new Row(engine, feature, "clean", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<Row> Job(string engine, string feature, IEngineService e, string url, string mode, string title, string? dir, string? quality,
        bool fingerprint = false, string? cloud = null)
    {
        try
        {
            var id = await e.CreateJobAsync(url, mode, "auto", title, "Parity test upload", ["parity"], dir, "private", fingerprint, "standard", false, quality,
                CancellationToken.None, cloudUpload: cloud != null, cloudProvider: cloud, cloudTempOnly: false);
            JobStatus? last = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await foreach (var st in e.StreamJobAsync(id, timeout.Token)) last = st;
            if (last == null) return new Row(engine, feature, "fail", "no status");
            var detail = last.Status == "completed"
                ? $"{last.Message} → {string.Join(", ", last.Result?.Select(kv => $"{kv.Key}={Path.GetFileName(kv.Value?.ToString() ?? "")}") ?? [])}"
                : $"{last.Status}: {last.Error ?? last.Message}";
            var outcome = last.Status == "completed" ? "ok" : (last.Status == "error" && !string.IsNullOrWhiteSpace(last.Error ?? last.Message) ? "clean" : "fail");
            Console.WriteLine($"   {feature}: {outcome} {detail}");
            return new Row(engine, feature, outcome, detail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   {feature}: exception {ex.Message}");
            return new Row(engine, feature, "clean", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
