using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

/// <summary>An engine that can say why it failed to start (shown instead of "Backend not reachable").</summary>
public interface IEngineDiagnostics
{
    string? LastStartError { get; }
}

/// <summary>
/// The Ferrite engine: a Rust port of <see cref="NativeEngineService"/> running as <c>ferrite.exe</c>.
/// Calls, job statuses, console lines and host callbacks travel as JSON lines over stdin/stdout
/// (backend-ferrite/PORTING.md §11.1). Job status channels live here, exactly like Native's.
/// </summary>
public sealed class FerriteBackend : IEngineService, IEngineDiagnostics
{
    public string EngineName => "Ferrite Rust Media Engine";

    public const string ExecutableName = "ferrite.exe";

    private static readonly JsonSerializerOptions _jso = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                       | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private sealed class JobChannel
    {
        public Channel<JobStatus> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<JobStatus>();
        public volatile bool Completed;
    }

    private readonly object _connLock = new();
    private Connection? _conn;
    private Task<Connection>? _starting;
    private volatile bool _disposed;
    private long _nextCallId;
    private readonly ConcurrentDictionary<string, JobChannel> _jobs = new();

    public string? LastStartError { get; private set; }

    public FerriteBackend()
    {
        FFmpegInstallerService.ProcessPathChanged += OnProcessPathChanged;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IEngineService
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<bool> WaitForHealthAsync(CancellationToken ct, int maxWait = 30)
    {
        try
        {
            await CallAsync("wait_for_health", null, ct).ConfigureAwait(false);
            LastStartError = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            EngineProvider.EmitOutput($"[ferrite] {ex.Message}");
            return false;
        }
    }

    public Task<MetadataResponse?> FetchMetadataAsync(string url, CancellationToken ct = default)
        => FetchMetadataCoreAsync("fetch_metadata", url, ct);

    public Task<MetadataResponse?> FetchYTMusicMetadataAsync(string url, CancellationToken ct = default)
        => FetchMetadataCoreAsync("fetch_ytmusic_metadata", url, ct);

    private async Task<MetadataResponse?> FetchMetadataCoreAsync(string method, string url, CancellationToken ct)
    {
        var value = await CallAsync(method, w => WriteNetString(w, "url", url), ct).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.Object ? value.Deserialize<MetadataResponse>(_jso) : null;
    }

    public async Task<string> CreateJobAsync(
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
        // Native ignores the token for job creation; cancelling a job goes through CancelJobAsync.
        var value = await CallAsync("create_job", w =>
        {
            WriteNetString(w, "url", url);
            WriteNetString(w, "mode", mode);
            WriteNetString(w, "platform", platform);
            WriteNetString(w, "title", title);
            WriteNetString(w, "description", description);
            w.WritePropertyName("tags");
            if (tags == null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var t in tags) WriteNetStringValue(w, t);
                w.WriteEndArray();
            }
            WriteNetString(w, "output_dir", outputDir);
            WriteNetString(w, "privacy", privacy);
            w.WriteBoolean("fingerprint", fingerprint);
            WriteNetString(w, "fingerprint_method", fingerprintMethod);
            w.WriteBoolean("use_gpu", useGpu);
            WriteNetString(w, "quality", quality);
            w.WriteBoolean("cloud_upload", cloudUpload);
            WriteNetString(w, "cloud_provider", cloudProvider);
            w.WriteBoolean("cloud_temp_only", cloudTempOnly);
        }, CancellationToken.None).ConfigureAwait(false);

        var jobId = ReadNetString(value) ?? throw new Exception("No job_id returned");
        _jobs.GetOrAdd(jobId, _ => new JobChannel());
        return jobId;
    }

    public async IAsyncEnumerable<JobStatus> StreamJobAsync(
        string jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            yield break;

        var reader = job.Channel.Reader;
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

    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            await CallAsync("cancel_job", w => WriteNetString(w, "job_id", jobId), CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best effort, like Native */ }
    }

    public async Task<string> GenerateSubtitlesAsync(
        string videoPath, string modelSize = "base", string? language = null,
        CancellationToken ct = default)
    {
        var value = await CallAsync("generate_subtitles", w =>
        {
            WriteNetString(w, "video_path", videoPath);
            WriteNetString(w, "model_size", modelSize);
            WriteNetString(w, "language", language);
        }, ct).ConfigureAwait(false);
        return ReadNetString(value) ?? "";
    }

    public async Task<PlaylistInfo?> FetchPlaylistAsync(
        string url, int maxItems = 100,
        CancellationToken ct = default)
    {
        var value = await CallAsync("fetch_playlist", w =>
        {
            WriteNetString(w, "url", url);
            w.WriteNumber("max_items", maxItems);
        }, ct).ConfigureAwait(false);
        return ReadPlaylist(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FFmpegInstallerService.ProcessPathChanged -= OnProcessPathChanged;
        Connection? conn;
        lock (_connLock) conn = _conn;
        conn?.Shutdown();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Calls
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<JsonElement> CallAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextCallId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Pending[id] = tcs;
        if (conn.Dead)
            tcs.TrySetException(new Exception(conn.DeathMessage));

        conn.Send(Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "call");
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            w.WritePropertyName("params");
            w.WriteStartObject();
            writeParams?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }));

        using var reg = ct.CanBeCanceled
            ? ct.Register(() => conn.Send(Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "cancel");
                w.WriteNumber("id", id);
                w.WriteEndObject();
            })))
            : default;

        var res = await tcs.Task.ConfigureAwait(false);
        if (res.GetProperty("ok").GetBoolean())
            return res.TryGetProperty("value", out var v) ? v : default;

        var err = res.GetProperty("error");
        var message = err.TryGetProperty("message", out var m) ? ReadNetString(m) ?? "" : "";
        if (err.TryGetProperty("kind", out var kind) && kind.GetString() == "cancelled")
            throw new OperationCanceledException(ct);
        throw new Exception(message);
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken ct)
    {
        Task<Connection> starting;
        lock (_connLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FerriteBackend));
            if (_conn is { Dead: false }) return _conn;

            if (_conn is { Dead: true } dead && !SettingsService.Current.AutoRestartBackend)
                throw new Exception(dead.DeathMessage);

            if (_starting == null || _starting.IsFaulted || _starting.IsCanceled
                || (_starting.IsCompletedSuccessfully && _starting.Result.Dead))
            {
                _starting = StartConnectionAsync();
            }
            starting = _starting;
        }

        var conn = await starting.WaitAsync(ct).ConfigureAwait(false);
        lock (_connLock)
        {
            if (!conn.Dead) _conn = conn;
        }
        return conn;
    }

    /// <summary>ferrite.exe next to the app, or the cargo build output in a dev checkout.</summary>
    public static string? FindExecutable()
    {
        var sol = MediaTools.FindSolutionDir();
        string[] candidates =
        [
            Path.Combine(MediaTools.AppDir, ExecutableName),
            Path.Combine(sol, "backend-ferrite", "target", "release", ExecutableName),
            Path.Combine(sol, "backend-ferrite", "target", "debug", ExecutableName),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<Connection> StartConnectionAsync()
    {
        var exe = FindExecutable()
            ?? throw new FileNotFoundException($"Ferrite backend not found: {Path.Combine(MediaTools.AppDir, ExecutableName)}");

        var conn = await Task.Run(() => new Connection(this, exe)).ConfigureAwait(false);
        var ready = await Task.WhenAny(conn.Ready, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        if (ready != conn.Ready)
        {
            conn.Kill();
            throw new Exception("Ferrite backend did not start: ferrite.exe did not answer within 15 seconds.");
        }
        await conn.Ready.ConfigureAwait(false);
        return conn;
    }

    private void OnProcessPathChanged()
    {
        Connection? conn;
        lock (_connLock) conn = _conn;
        conn?.SendEnvironment();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Messages from ferrite.exe
    // ═════════════════════════════════════════════════════════════════════════

    private JobChannel ChannelFor(string jobId) => _jobs.GetOrAdd(jobId, _ => new JobChannel());

    private void OnJobStatus(string jobId, JobStatus status)
    {
        ChannelFor(jobId).Channel.Writer.TryWrite(status);
    }

    private void OnJobComplete(string jobId)
    {
        var job = ChannelFor(jobId);
        job.Completed = true;
        job.Channel.Writer.TryComplete();
        _ = ForgetJobLaterAsync(jobId);
    }

    /// <summary>Like Native: a finished job's channel only needs to live until its consumer drained it.</summary>
    private async Task ForgetJobLaterAsync(string jobId)
    {
        await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        _jobs.TryRemove(jobId, out _);
    }

    /// <summary>The sidecar is gone: every unfinished job ends with an error (or "cancelled" after Dispose).</summary>
    private void OnConnectionLost(string message, bool shuttingDown)
    {
        foreach (var (jobId, job) in _jobs)
        {
            if (job.Completed) continue;
            var status = shuttingDown
                ? new JobStatus { Id = jobId, Status = "cancelled", Progress = 0, Message = "Job was cancelled by user." }
                : new JobStatus { Id = jobId, Status = "error", Progress = 0, Message = $"Error: {message}", Error = message };
            job.Channel.Writer.TryWrite(status);
            OnJobComplete(jobId);
        }
    }

    private static async Task<string> HandleHostCallAsync(string method)
    {
        switch (method)
        {
            case "get_settings":
                try
                {
                    return JsonSerializer.Serialize(SettingsService.Current);
                }
                catch
                {
                    return "{}";
                }
            case "ensure_ffmpeg":
                bool installed;
                try
                {
                    installed = await FFmpegInstallerService.PromptAndInstallIfNeededAsync().ConfigureAwait(false);
                }
                catch
                {
                    installed = false;
                }
                return Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("installed", installed);
                    WriteNetString(w, "path", Environment.GetEnvironmentVariable("PATH"));
                    w.WriteEndObject();
                });
            default:
                return "null";
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The sidecar process
    // ═════════════════════════════════════════════════════════════════════════

    private sealed class Connection
    {
        private readonly FerriteBackend _owner;
        private readonly Process _proc;
        private readonly object _writeLock = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _shuttingDown;

        public ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public Task Ready => _ready.Task;
        public volatile bool Dead;
        public string DeathMessage { get; private set; } = "Ferrite backend process is not running.";

        public Connection(FerriteBackend owner, string exe)
        {
            _owner = owner;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    EngineProvider.EmitOutput($"[ferrite] {e.Data}");
            };
            _proc.Start();
            _proc.BeginErrorReadLine();

            new Thread(ReadLoop) { IsBackground = true, Name = "Ferrite reader" }.Start();

            var culture = CultureInfo.CurrentCulture;
            var nf = culture.NumberFormat;
            Send(Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "hello");
                w.WriteNumber("protocol", 1);
                WriteNetString(w, "base_dir", MediaTools.AppDir);
                WriteNetString(w, "process_path", Environment.ProcessPath);
                WriteNetString(w, "cwd", Directory.GetCurrentDirectory());
                w.WritePropertyName("culture");
                w.WriteStartObject();
                w.WriteString("name", culture.Name);
                w.WriteString("decimal_separator", nf.NumberDecimalSeparator);
                w.WriteString("negative_sign", nf.NegativeSign);
                w.WriteString("positive_infinity", nf.PositiveInfinitySymbol);
                w.WriteString("negative_infinity", nf.NegativeInfinitySymbol);
                w.WriteString("nan", nf.NaNSymbol);
                w.WriteEndObject();
                w.WriteEndObject();
            }));
        }

        public void Send(string line)
        {
            if (Dead) return;
            try
            {
                lock (_writeLock)
                {
                    _proc.StandardInput.Write(line);
                    _proc.StandardInput.Write('\n');
                    _proc.StandardInput.Flush();
                }
            }
            catch
            {
                // The reader notices the exit and fails everything that is pending.
            }
        }

        public void SendEnvironment()
        {
            Send(Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "env");
                WriteNetString(w, "path", Environment.GetEnvironmentVariable("PATH"));
                w.WriteEndObject();
            }));
        }

        /// <summary>Dispose semantics: the sidecar cancels its jobs, reports them and exits.</summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            Send("{\"type\":\"shutdown\"}");
            try { lock (_writeLock) _proc.StandardInput.Close(); } catch { }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await _proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    Kill();
                }
            });
        }

        public void Kill()
        {
            try
            {
                if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            }
            catch { }
        }

        private void ReadLoop()
        {
            try
            {
                string? line;
                while ((line = _proc.StandardOutput.ReadLine()) != null)
                {
                    try
                    {
                        OnLine(line);
                    }
                    catch (Exception ex)
                    {
                        EngineProvider.EmitOutput($"[ferrite] Could not process a message: {ex.Message}");
                    }
                }
            }
            catch { }

            int? exitCode = null;
            try
            {
                _proc.WaitForExit(2000);
                if (_proc.HasExited) exitCode = _proc.ExitCode;
            }
            catch { }

            DeathMessage = exitCode.HasValue
                ? $"Ferrite backend process exited unexpectedly (exit code {exitCode.Value})."
                : "Ferrite backend process exited unexpectedly.";
            Dead = true;
            _ready.TrySetException(new Exception(DeathMessage));
            foreach (var (id, tcs) in Pending)
            {
                if (Pending.TryRemove(id, out _))
                    tcs.TrySetException(new Exception(DeathMessage));
            }
            _owner.OnConnectionLost(DeathMessage, _shuttingDown || _owner._disposed);
            if (!_shuttingDown && !_owner._disposed)
                EngineProvider.EmitOutput($"[ferrite] {DeathMessage}");
        }

        private void OnLine(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _ready.TrySetResult();
                    break;

                case "result":
                    var id = root.GetProperty("id").GetInt64();
                    if (Pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(root.Clone());
                    break;

                case "job_status":
                    var jobId = root.GetProperty("job_id").GetString() ?? "";
                    _owner.OnJobStatus(jobId, ReadStatus(root.GetProperty("status")));
                    break;

                case "job_complete":
                    _owner.OnJobComplete(root.GetProperty("job_id").GetString() ?? "");
                    break;

                case "log":
                    EngineProvider.EmitOutput(ReadNetString(root.GetProperty("line")));
                    break;

                case "host_call":
                    var callId = root.GetProperty("id").GetInt64();
                    var method = root.GetProperty("method").GetString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var value = await HandleHostCallAsync(method).ConfigureAwait(false);
                        Send($"{{\"type\":\"host_result\",\"id\":{callId},\"value\":{value}}}");
                    });
                    break;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  JSON helpers (strings with unpaired surrogates travel as {"$utf16":[...]})
    // ═════════════════════════════════════════════════════════════════════════

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool HasUnpairedSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    private static void WriteNetStringValue(Utf8JsonWriter w, string? s)
    {
        if (s == null)
        {
            w.WriteNullValue();
        }
        else if (HasUnpairedSurrogate(s))
        {
            w.WriteStartObject();
            w.WritePropertyName("$utf16");
            w.WriteStartArray();
            foreach (var c in s) w.WriteNumberValue((int)c);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else
        {
            w.WriteStringValue(s);
        }
    }

    private static void WriteNetString(Utf8JsonWriter w, string name, string? s)
    {
        w.WritePropertyName(name);
        WriteNetStringValue(w, s);
    }

    private static string? ReadNetString(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Object when e.TryGetProperty("$utf16", out var units):
                var sb = new StringBuilder(units.GetArrayLength());
                foreach (var u in units.EnumerateArray()) sb.Append((char)u.GetInt32());
                return sb.ToString();
            default:
                return null;
        }
    }

    private static double ReadDouble(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => double.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => 0,
    };

    /// <summary>JSON status → <see cref="JobStatus"/>. Result values become CLR strings/bools, like Native's.</summary>
    private static JobStatus ReadStatus(JsonElement s)
    {
        var status = new JobStatus
        {
            Id = s.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Status = s.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            Progress = s.TryGetProperty("progress", out var p) ? ReadDouble(p) : 0,
            Message = s.TryGetProperty("message", out var m) ? ReadNetString(m) ?? "" : "",
            Error = s.TryGetProperty("error", out var err) ? ReadNetString(err) : null,
            Eta = s.TryGetProperty("eta", out var eta) && eta.ValueKind == JsonValueKind.Number ? eta.GetInt32() : null,
            Speed = s.TryGetProperty("speed", out var sp) ? ReadNetString(sp) : null,
        };
        if (s.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in r.EnumerateObject())
            {
                object? value = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.Null => null,
                    _ => ReadNetString(prop.Value) ?? prop.Value.GetRawText(),
                };
                if (value != null) dict[prop.Name] = value;
            }
            status.Result = dict;
        }
        return status;
    }

    private static PlaylistInfo? ReadPlaylist(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        var info = new PlaylistInfo
        {
            Title = v.TryGetProperty("title", out var t) ? ReadNetString(t)! : null!,
            Count = v.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
        };
        if (v.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            info.Entries = new List<PlaylistEntry>();
            foreach (var e in entries.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    info.Entries.Add(null!);
                    continue;
                }
                info.Entries.Add(new PlaylistEntry
                {
                    Id = e.TryGetProperty("id", out var id) ? ReadNetString(id)! : null!,
                    Title = e.TryGetProperty("title", out var et) ? ReadNetString(et)! : null!,
                    Url = e.TryGetProperty("url", out var u) ? ReadNetString(u)! : null!,
                    Duration = e.TryGetProperty("duration", out var d) ? ReadDouble(d) : 0,
                    Uploader = e.TryGetProperty("uploader", out var up) ? ReadNetString(up)! : null!,
                    Thumbnail = e.TryGetProperty("thumbnail", out var th) ? ReadNetString(th)! : null!,
                });
            }
        }
        else if (v.TryGetProperty("entries", out var ne) && ne.ValueKind == JsonValueKind.Null)
        {
            info.Entries = null!;
        }
        return info;
    }
}
