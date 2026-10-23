using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

/// <summary>
/// The <see cref="IEngineService"/> the UI holds. It forwards to the engine that is active when a call
/// starts and keeps that engine pinned until the call ends; a job stays pinned from
/// <see cref="CreateJobAsync"/> until its status stream ends, and its stream and cancel calls always go to
/// the engine that created it.
/// </summary>
internal sealed class EngineProxy : IEngineService
{
    private readonly ConcurrentDictionary<string, IEngineService> _jobEngines = new();

    public string EngineName => EngineProvider.Peek().EngineName;

    private static async Task<T> Pinned<T>(Func<IEngineService, Task<T>> call)
    {
        var engine = EngineProvider.Acquire();
        try
        {
            return await call(engine).ConfigureAwait(false);
        }
        finally
        {
            EngineProvider.Release(engine);
        }
    }

    public Task<bool> WaitForHealthAsync(CancellationToken ct, int maxWait = 30)
        => Pinned(e => e.WaitForHealthAsync(ct, maxWait));

    public Task<MetadataResponse?> FetchMetadataAsync(string url, CancellationToken ct = default)
        => Pinned(e => e.FetchMetadataAsync(url, ct));

    public Task<MetadataResponse?> FetchYTMusicMetadataAsync(string url, CancellationToken ct = default)
        => Pinned(e => e.FetchYTMusicMetadataAsync(url, ct));

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
        var engine = EngineProvider.Acquire();
        try
        {
            var jobId = await engine.CreateJobAsync(
                url, mode, platform, title, description, tags, outputDir, privacy, fingerprint,
                fingerprintMethod, useGpu, quality, ct, cloudUpload, cloudProvider, cloudTempOnly).ConfigureAwait(false);
            if (_jobEngines.TryAdd(jobId, engine))
                return jobId; // stays pinned until the stream ends
            EngineProvider.Release(engine);
            return jobId;
        }
        catch
        {
            EngineProvider.Release(engine);
            throw;
        }
    }

    public async IAsyncEnumerable<JobStatus> StreamJobAsync(string jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var owned = _jobEngines.TryGetValue(jobId, out var engine);
        engine ??= EngineProvider.Peek();
        try
        {
            await foreach (var status in engine.StreamJobAsync(jobId, ct).ConfigureAwait(false))
                yield return status;
        }
        finally
        {
            if (owned && _jobEngines.TryRemove(jobId, out var pinned))
                EngineProvider.Release(pinned);
        }
    }

    public Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        var engine = _jobEngines.TryGetValue(jobId, out var e) ? e : EngineProvider.Peek();
        return engine.CancelJobAsync(jobId, ct);
    }

    public Task<string> GenerateSubtitlesAsync(string videoPath, string modelSize = "base", string? language = null, CancellationToken ct = default)
        => Pinned(e => e.GenerateSubtitlesAsync(videoPath, modelSize, language, ct));

    public Task<PlaylistInfo?> FetchPlaylistAsync(string url, int maxItems = 100, CancellationToken ct = default)
        => Pinned(e => e.FetchPlaylistAsync(url, maxItems, ct));

    /// <summary>The engines are owned by <see cref="EngineProvider"/>; disposing the proxy does nothing.</summary>
    public void Dispose() { }
}
