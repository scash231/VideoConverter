// Parity runner for Native (C#) vs. Ferrite (Rust).
//
//   dotnet run --project backend-ferrite/parity -- golden            writes parity/fixtures/golden.json from the C# code
//   dotnet run --project backend-ferrite/parity -- e2e [names...]    runs scenarios through both engines and compares
//
// The e2e report (Markdown) is written to parity/results/e2e-report.md and printed.

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VideoConverterUI.Models;
using VideoConverterUI.Services;

namespace ParityRunner;

internal static class Program
{
    public static readonly string ParityDir = FindParityDir();

    private static string FindParityDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "ParityRunner.csproj"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new InvalidOperationException("parity directory not found");
    }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "e2e";
        try
        {
            return mode switch
            {
                "golden" => await Golden.RunAsync(Path.Combine(ParityDir, "fixtures", "golden.json")),
                "e2e" => await E2E.RunAsync(args.Skip(1).ToArray()),
                "advice" => Advice.Run(),
                "backends" => await Backends.RunAsync(),
                _ => Usage(),
            };
        }
        finally
        {
            EngineProvider.Shutdown();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: ParityRunner golden | e2e [scenario...] | advice | backends");
        return 2;
    }

    public static JsonArray Utf16(string s) => new(s.Select(c => (JsonNode)JsonValue.Create((int)c)).ToArray());
}

/// <summary>Golden fixtures computed by the real C# code (private members via reflection).</summary>
internal static class Golden
{
    private static readonly Type Native = typeof(NativeEngineService);
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    private static object? Call(string method, params object?[] args) => Native.GetMethod(method, Priv)!.Invoke(null, args);

    /// <summary>Doubles as JSON; infinity/NaN become strings (the DTO converters cannot write them).</summary>
    private static JsonNode? Num(double d) => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));

    private static JsonObject MetaNode(MetadataResponse m) => new()
    {
        ["title"] = m.Title, ["description"] = m.Description, ["tags"] = new JsonArray(m.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        ["thumbnail"] = m.Thumbnail, ["duration"] = Num(m.Duration), ["uploader"] = m.Uploader, ["artist"] = m.Artist, ["album"] = m.Album,
        ["is_music"] = m.IsMusic, ["release_year"] = m.ReleaseYear, ["track_count"] = m.TrackCount,
    };

    private static JsonObject PlaylistNode(PlaylistInfo p) => new()
    {
        ["title"] = p.Title, ["count"] = p.Count,
        ["entries"] = p.Entries == null ? null : new JsonArray(p.Entries.Select(e => (JsonNode?)(e == null ? null : new JsonObject
        {
            ["id"] = e.Id, ["title"] = e.Title, ["url"] = e.Url, ["duration"] = Num(e.Duration), ["uploader"] = e.Uploader, ["thumbnail"] = e.Thumbnail,
        })).ToArray()),
    };

    public static async Task<int> RunAsync(string outPath)
    {
        var root = new JsonObject();
        var c = CultureInfo.CurrentCulture;
        var nf = c.NumberFormat;
        root["culture"] = new JsonObject
        {
            ["name"] = c.Name,
            ["decimal_separator"] = nf.NumberDecimalSeparator,
            ["negative_sign"] = nf.NegativeSign,
            ["positive_infinity"] = nf.PositiveInfinitySymbol,
            ["negative_infinity"] = nf.NegativeInfinitySymbol,
            ["nan"] = nf.NaNSymbol,
        };

        // GetFormatString
        var formats = new JsonArray();
        string?[] qualities = [null, "best", "1080p", "1080", "720P", " 480p ", "2160", "1440", "360", "4k", "p", "", "audio", "mp3", "ytmusic_flac"];
        foreach (var q in qualities)
            foreach (var audio in new[] { false, true })
                foreach (var m in new[] { "best", "any", "progressive" })
                    formats.Add(new JsonObject { ["quality"] = q, ["is_audio"] = audio, ["mode"] = m, ["out"] = (string)Call("GetFormatString", q, audio, m)! });
        root["format_strings"] = formats;

        // SanitizeFilename (UTF-16 in/out)
        var sanitize = new JsonArray();
        string[] names =
        [
            "Me at the zoo", "a<b>:c\"d/e\\f|g?h*", "  ..My Title.. ", "con", "COM1", "COM10", "LPT9", "nul.txt", "...", "",
            "Tab\tNew\nLine", new string('x', 79) + "\U0001F3AC", new string('y', 100), "Emoji 🎬 Clip", "Ünïcödé — „Test“",
            "a" + (char)0xD83D + "b",
        ];
        foreach (var n in names)
            sanitize.Add(new JsonObject { ["in"] = Program.Utf16(n), ["out"] = Program.Utf16((string)Call("SanitizeFilename", n)!) });
        root["sanitize"] = sanitize;

        // ParseEta
        var eta = new JsonArray();
        foreach (var e in new[] { null, "", "00:07", "1:02:03", "59:59", "1::2", "5", "99999999999:00", " 1:2", "1:2:3:4" })
            eta.Add(new JsonObject { ["in"] = e, ["out"] = (int?)Call("ParseEta", e) });
        root["eta"] = eta;

        // CleanUrl / IsYtMusicUrl
        var urls = new JsonArray();
        string[] rawUrls =
        [
            " https://youtu.be/x ", "\u200Bhttps://www.youtube.com/watch?v=abc\uFEFF", "https://MUSIC.youtube.com/watch?v=1", "\t\u00AD\u2066https://x\u2069 ",
            "https://www.youtube.com/watch?v=1&list=RD1", "   ",
        ];
        foreach (var u in rawUrls)
        {
            var clean = (string)Call("CleanUrl", u)!;
            urls.Add(new JsonObject { ["in"] = Program.Utf16(u), ["clean"] = Program.Utf16(clean), ["is_music"] = (bool)Call("IsYtMusicUrl", clean)! });
        }
        root["urls"] = urls;

        // yt-dlp progress lines → (pct, speed, eta, message with the current culture)
        var progressRegex = (Regex)Native.GetField("YtDlpProgressRegex", Priv)!.GetValue(null)!;
        var progress = new JsonArray();
        string[] lines =
        [
            "[download]  45.3% of   12.34MiB at    1.23MiB/s ETA 00:07", "[download] 100% of 5.00MiB in 00:01", "[download] 100.0% of    5.00MiB at  18.47MiB/s ETA 00:00",
            "[DOWNLOAD]   3.0% of ~5.00MiB at 2.00KiB/s ETA 1:02:03", "[download]  10.0% of ~  12.34MiB at 1.00MiB/s ETA 00:10", "[download] Destination: x.mp4",
            "[download] 1.2.3% of 5MiB", "[download]   0.5% of  808.48KiB at  501.71KiB/s ETA 00:01", "[download]  99.95% of 1GiB at 1MiB/s ETA 0:01",
            "[download] 12.25% of 3.5MiB at Unknown speed ETA Unknown ETA",
        ];
        foreach (var line in lines)
        {
            var match = progressRegex.Match(line);
            var o = new JsonObject { ["line"] = line, ["match"] = false };
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
            {
                o["match"] = true;
                o["pct"] = pct;
                o["speed"] = match.Groups[3].Success ? match.Groups[3].Value : null;
                o["eta"] = (int?)Call("ParseEta", match.Groups[4].Success ? match.Groups[4].Value : null);
                o["message"] = $"Download Video: {pct:F1}%";
            }
            progress.Add(o);
        }
        root["progress"] = progress;

        // Phase keys for console de-duplication
        var numberRegex = (Regex)Native.GetField("ProgressNumberRegex", Priv)!.GetValue(null)!;
        var phases = new JsonArray();
        foreach (var msg in new[] { "[1/2] Download Video: 45,3%", "Encoding video: 100%", "Uploading to Google Drive... 7%", "Retry 2/3 (any format)...", "v1.2,3 % x" })
            phases.Add(new JsonObject { ["status"] = "downloading", ["message"] = msg, ["key"] = "downloading|" + numberRegex.Replace(msg, "") });
        root["phase_keys"] = phases;

        // F formatting with the current culture
        var fixedArr = new JsonArray();
        foreach (var v in new[] { 0.0, 0.05, 0.5, 1.25, 2.25, 12.5, 13.5, 45.3, 99.95, 99.96, 100.0, 1.005, -0.0, 7.123456, double.PositiveInfinity })
            foreach (var d in new[] { 0, 1 })
                fixedArr.Add(new JsonObject { ["value"] = v.ToString("R", CultureInfo.InvariantCulture), ["decimals"] = d, ["out"] = v.ToString("F" + d) });
        root["fixed"] = fixedArr;

        // System.Text.Json string escaping (Dropbox-API-Arg / Discord payload)
        var stj = new JsonArray();
        foreach (var s in new[] { "/VideoConverter/clip.mp4", "/VideoConverter/Ä<b>&'+`é🎬 \"x\".mp4", "a\\b\n\t\u0001\u007f/€", "a" + (char)0xD83D + "b", "🎬 Uploaded with **VideoConverter**: `x.mp4`" })
            stj.Add(new JsonObject { ["in"] = Program.Utf16(s), ["out"] = JsonSerializer.Serialize(s) });
        root["stj_strings"] = stj;

        // Uri.EscapeDataString
        var esc = new JsonArray();
        foreach (var s in new[] { "https://www.youtube.com/watch?v=a b&x=ü~*'()!", "https://youtu.be/jNQXAC9IVRw", "AZaz09-_.~" })
            esc.Add(new JsonObject { ["in"] = s, ["out"] = Uri.EscapeDataString(s) });
        root["escape_data_string"] = esc;

        // MultipartFormDataContent frames (webhook upload)
        var multipart = new JsonArray();
        foreach (var (discord, file) in new[] { (true, "clip.mp4"), (false, "My Clip (1).mp4"), (true, "Mein Vidéo.mp4"), (false, "Emoji 🎬.mp3") })
        {
            using var form = new MultipartFormDataContent("b0");
            if (discord)
            {
                var payload = JsonSerializer.Serialize(new { content = $"🎬 Uploaded with **VideoConverter**: `{file}`" });
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            }
            var sc = new ByteArrayContent(Encoding.ASCII.GetBytes("DATA"));
            sc.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(sc, discord ? "files[0]" : "file", file);
            multipart.Add(new JsonObject
            {
                ["discord"] = discord,
                ["file_name"] = Program.Utf16(file),
                ["content_type"] = form.Headers.ContentType!.ToString(),
                ["body"] = await form.ReadAsStringAsync(),
            });
        }
        root["multipart"] = multipart;

        // HttpStatusCode names
        var statusNames = new JsonObject();
        for (int i = 100; i < 600; i++) statusNames[i.ToString()] = ((HttpStatusCode)i).ToString();
        root["status_names"] = statusNames;

        // Metadata and playlist mapping: run Native with yt-dlp replaced by `type <fixture>`.
        var engine = new NativeEngineService();
        await engine.WaitForHealthAsync(CancellationToken.None);
        var runnerField = Native.GetField("_ytDlpRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tmp = Path.Combine(Path.GetTempPath(), "ferrite_golden");
        Directory.CreateDirectory(tmp);

        var metaFixtures = MetadataFixtures(await RealYtDlpJson());
        var meta = new JsonArray();
        int idx = 0;
        foreach (var (url, music, json) in metaFixtures)
        {
            var file = Path.Combine(tmp, $"meta{idx++}.json");
            await File.WriteAllTextAsync(file, json, new UTF8Encoding(false));
            runnerField.SetValue(engine, ((string, string)?)("cmd.exe", $"/c type \"{file}\" & rem"));
            var r = music ? await engine.FetchYTMusicMetadataAsync(url) : await engine.FetchMetadataAsync(url);
            meta.Add(new JsonObject { ["url"] = url, ["force_music"] = music, ["json"] = json, ["out"] = r == null ? null : MetaNode(r) });
        }
        root["metadata"] = meta;

        var playlists = new JsonArray();
        foreach (var json in PlaylistFixtures())
        {
            var file = Path.Combine(tmp, $"pl{idx++}.json");
            await File.WriteAllTextAsync(file, json, new UTF8Encoding(false));
            runnerField.SetValue(engine, ((string, string)?)("cmd.exe", $"/c type \"{file}\" & rem"));
            var r = await engine.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PLgolden", 50);
            playlists.Add(new JsonObject { ["json"] = json, ["out"] = r == null ? null : PlaylistNode(r) });
        }
        root["playlist"] = playlists;
        engine.Dispose();

        // .NET IO exception texts
        var io = new JsonArray();
        void Capture(string op, Action a)
        {
            try { a(); io.Add(new JsonObject { ["op"] = op, ["message"] = null }); }
            catch (Exception e) { io.Add(new JsonObject { ["op"] = op, ["type"] = e.GetType().Name, ["message"] = e.Message }); }
        }
        Capture(@"create_directory Q:\nodrive_ferrite\x", () => Directory.CreateDirectory(@"Q:\nodrive_ferrite\x"));
        Capture("get_full_path empty", () => Path.GetFullPath(""));
        Capture(@"delete_file C:\nope_dir_xyz\a.txt", () => File.Delete(@"C:\nope_dir_xyz\a.txt"));
        root["io_errors"] = io;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        Console.WriteLine($"golden fixtures written: {outPath}");
        return 0;
    }

    /// <summary>Real `yt-dlp -J` output for a short public video (falls back to a small sample offline).</summary>
    private static async Task<string?> RealYtDlpJson()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("yt-dlp", "-J --skip-download --no-warnings \"https://www.youtube.com/watch?v=jNQXAC9IVRw\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var r = await MediaTools.RunProcessAsync(psi, CancellationToken.None, 120_000);
            return r.ExitCode == 0 ? r.StdOut.Trim() : null;
        }
        catch { return null; }
    }

    private static List<(string url, bool music, string json)> MetadataFixtures(string? real)
    {
        var list = new List<(string, bool, string)>
        {
            ("https://vimeo.com/1", false, """{"title":"Song","description":"nice #tag1 and #tag_2!","uploader":"Artist - Topic","duration":187.5,"thumbnail":"https://i/x.jpg","release_year":2019,"album":"LP"}"""),
            ("https://music.youtube.com/watch?v=x", true, """{"title":"T","artist":"A - TOPIC","channel":"C","duration":200,"release_year":2019.0,"tags":["a"," ","b"]}"""),
            ("https://vimeo.com/2", false, """{"title":"x","duration":null}"""),
            ("https://vimeo.com/3", false, """{"title":"x","tags":null,"description":"#a #b c#d #ü_1"}"""),
            ("https://vimeo.com/4", false, """{"title":"x","tags":[1]}"""),
            ("https://vimeo.com/5", false, """{"title":"  "}"""),
            ("https://vimeo.com/6", false, """{"title":"x","artist":null,"channel":"C","uploader":null}"""),
            ("https://vimeo.com/7", false, """{"title":"x","release_year":null,"album":null,"thumbnail":null,"description":null}"""),
            ("https://vimeo.com/8", false, """[1,2]"""),
            ("https://vimeo.com/9", false, """{"title":"x","duration":"12"}"""),
            ("https://vimeo.com/10", false, """{"title":"dup","title":"last","duration":1e400}"""),
            ("https://vimeo.com/11", false, "not json"),
            ("https://vimeo.com/12", false, """{"title":"x\ud83dy"}"""),
            ("https://vimeo.com/13", false, """{"title":"ok","description":"x\ud83dy"}"""),
        };
        if (real != null) list.Add(("https://www.youtube.com/watch?v=jNQXAC9IVRw", false, real));
        return list;
    }

    private static List<string> PlaylistFixtures() =>
    [
        """[youtube:tab] junk {"title":"PL","entries":[{"id":"a1","url":"https://www.youtube.com/watch?v=a1","title":"One","duration":61.0,"channel":"Ch"},{"id":"b2","url":"b2","title":null,"uploader":"Up","thumbnails":[{"url":"t1"},{"url":"t2"},{"x":1}]},{"id":"","url":""},{"id":"c3","thumbnail":5}]} trailing""",
        """{"entries":[{"id":"z","url":"HTTPS://x.org/v","thumbnail":"https://t"}]}""",
        """{"title":null,"entries":[{"id":"q","title":"Q","duration":3}]}""",
    ];
}
