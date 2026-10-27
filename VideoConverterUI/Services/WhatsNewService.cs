using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace VideoConverterUI.Services;

/// <summary>
/// What's New (Settings → About): every release's notes live on the website (https://scash.uk/vc/whatsnew.json,
/// installer\whatsnew.json uploaded by build_release.ps1), not in the app. The site only answers requests with the
/// <c>X-VideoConverter</c> header. The last copy is kept in %LOCALAPPDATA%\VideoConverter\cache for when it can't be reached.
/// </summary>
public static class WhatsNewService
{
    public const string Url = "https://scash.uk/vc/whatsnew.json";

    /// <summary>Opening What's New again within this time shows what was loaded instead of asking again.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoConverter", "cache", "whatsnew.json");

    private static Task<WhatsNewResult>? _loading;
    private static DateTime _loadedAt;

    /// <summary>The last result (null before the first load).</summary>
    public static WhatsNewResult? Last { get; private set; }

    /// <summary>Loads the notes: from the website, or else the saved copy. A load already running is joined.</summary>
    public static Task<WhatsNewResult> LoadAsync(bool force = false)
    {
        if (!force && Last is { FromCache: false } last && DateTime.UtcNow - _loadedAt < Fresh) return Task.FromResult(last);
        return _loading ??= LoadCoreAsync();
    }

    private static async Task<WhatsNewResult> LoadCoreAsync()
    {
        try
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.Add("X-VideoConverter", BuildInfoService.BuildVersion);
                request.Headers.UserAgent.ParseAdd($"VideoConverter/{BuildInfoService.BuildVersion}");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                string text = await response.Content.ReadAsStringAsync();
                var versions = Parse(text);
                _ = SaveCopyAsync(text);
                _loadedAt = DateTime.UtcNow;
                return Last = new WhatsNewResult(versions, FromCache: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WhatsNewService: load failed: {ex.Message}");
                try
                {
                    if (File.Exists(CacheFile))
                        return Last = new WhatsNewResult(Parse(await File.ReadAllTextAsync(CacheFile)), FromCache: true);
                }
                catch (Exception cacheEx) { Debug.WriteLine($"WhatsNewService: saved copy unreadable: {cacheEx.Message}"); }
                return Last = new WhatsNewResult([], FromCache: false, Failed: true);
            }
        }
        finally
        {
            _loading = null;
        }
    }

    private static IReadOnlyList<ChangelogVersion> Parse(string text)
    {
        var file = JsonSerializer.Deserialize<WhatsNewFile>(text, Json);
        if (file?.Versions is not { Count: > 0 } versions) throw new InvalidDataException("no versions");
        return versions;
    }

    private static async Task SaveCopyAsync(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            await File.WriteAllTextAsync(CacheFile, text);
        }
        catch (Exception ex) { Debug.WriteLine($"WhatsNewService: couldn't save a copy: {ex.Message}"); }
    }

    private sealed class WhatsNewFile
    {
        public List<ChangelogVersion>? Versions { get; set; }
    }
}

/// <summary>The big versions (newest first); <see cref="FromCache"/> = the website couldn't be reached and this is the
/// saved copy; <see cref="Failed"/> = neither worked.</summary>
public sealed record WhatsNewResult(IReadOnlyList<ChangelogVersion> Versions, bool FromCache, bool Failed = false);
