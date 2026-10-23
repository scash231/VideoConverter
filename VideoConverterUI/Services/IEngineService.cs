using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public interface IEngineService : System.IDisposable
{
    string EngineName { get; }
    Task<bool> WaitForHealthAsync(CancellationToken ct, int maxWait = 30);
    Task<MetadataResponse?> FetchMetadataAsync(string url, CancellationToken ct = default);
    Task<MetadataResponse?> FetchYTMusicMetadataAsync(string url, CancellationToken ct = default);
    Task<string> CreateJobAsync(
        string url, string mode, string platform,
        string? title, string? description, List<string>? tags,
        string? outputDir, string? privacy, bool fingerprint,
        string fingerprintMethod = "standard",
        bool useGpu = false,
        string? quality = null,
        CancellationToken ct = default,
        bool cloudUpload = false,
        string? cloudProvider = null,
        bool cloudTempOnly = true);
    IAsyncEnumerable<JobStatus> StreamJobAsync(string jobId, CancellationToken ct = default);
    Task CancelJobAsync(string jobId, CancellationToken ct = default);
    Task<string> GenerateSubtitlesAsync(
        string videoPath, string modelSize = "base", string? language = null,
        CancellationToken ct = default);
    Task<PlaylistInfo?> FetchPlaylistAsync(
        string url, int maxItems = 100,
        CancellationToken ct = default);
}
