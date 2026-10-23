using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public sealed class BackendService : IEngineService
{
    public string EngineName => "Python FastAPI Backend";
    private readonly HttpClient _http;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobStatus> _rejectedJobs = new();
    private static readonly JsonSerializerOptions _jso = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public BackendService(string baseUrl = "http://127.0.0.1:8765")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    public async Task<bool> WaitForHealthAsync(CancellationToken ct, int maxWait = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWait);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r = await _http.GetAsync("/api/health", ct).ConfigureAwait(false);
                if (r.IsSuccessStatusCode) return true;
            }
            catch { /* server not ready */ }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<MetadataResponse?> FetchMetadataAsync(string url, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { url });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/metadata", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MetadataResponse>(stream, _jso, ct).ConfigureAwait(false);
    }

    public async Task<MetadataResponse?> FetchYTMusicMetadataAsync(string url, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { url });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/ytmusic/metadata", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MetadataResponse>(stream, _jso, ct).ConfigureAwait(false);
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
        // The Python backend has no cloud upload. Instead of silently skipping the upload, the job
        // fails with an error the UI shows like any other.
        if (cloudUpload)
        {
            var rejectedId = $"classic-{Guid.NewGuid().ToString("N")[..8]}";
            _rejectedJobs[rejectedId] = new JobStatus
            {
                Id = rejectedId,
                Status = "error",
                Progress = 0,
                Message = "Cloud upload is not available in Legacy mode. Please switch to Native or Ferrite in Settings → Advanced for cloud upload jobs.",
                Error = "Cloud upload requires the Native or Ferrite engine.",
            };
            return rejectedId;
        }

        var payload = new
        {
            url, mode, platform, title, description,
            tags = tags ?? [],
            output_dir = outputDir,
            privacy = privacy ?? "public",
            fingerprint,
            fingerprint_method = fingerprintMethod,
            use_gpu = useGpu,
            quality = quality ?? "best",
        };
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/jobs", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<JobCreateResponse>(stream, _jso, ct).ConfigureAwait(false);
        return result?.JobId ?? throw new Exception("No job_id returned");
    }

    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_rejectedJobs.TryRemove(jobId, out _)) return;
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"/api/jobs/{jobId}/cancel", content, ct).ConfigureAwait(false);
        }
        catch { /* best-effort – backend may already be gone */ }
    }

    public async Task<string> GenerateSubtitlesAsync(
        string videoPath, string modelSize = "base", string? language = null,
        CancellationToken ct = default)
    {
        var payload = new { video_path = videoPath, model_size = modelSize, language };
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/subtitles", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("srt").GetString() ?? string.Empty;
    }

    public async Task<PlaylistInfo?> FetchPlaylistAsync(
        string url, int maxItems = 100,
        CancellationToken ct = default)
    {
        var payload = new { url, max_items = maxItems };
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/playlist", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PlaylistInfo>(json, _jso);
    }

    public async IAsyncEnumerable<JobStatus> StreamJobAsync(
        string jobId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_rejectedJobs.TryRemove(jobId, out var rejected))
        {
            yield return rejected;
            yield break;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/stream");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var json = line[6..];
            var status = JsonSerializer.Deserialize<JobStatus>(json, _jso);
            if (status is not null) yield return status;
            if (status?.Status is "completed" or "error") break;
        }
    }

    public void Dispose() => _http.Dispose();
}
