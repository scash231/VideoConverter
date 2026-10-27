using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace VideoConverterUI.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>A newer release on the website: what Settings → Software Update shows and installs.</summary>
public sealed record UpdateInfo(
    string Version,
    DateTime? Published,
    long DownloadBytes,
    Uri SetupUrl,
    long SetupSize,
    string SetupSha256,
    IReadOnlyList<ChangelogSection> Notes);

/// <summary>
/// Software Update: compares this build with the release manifest the web installer reads
/// (https://scash.uk/vc/manifest.json, written by installer/build_release.ps1) and installs a newer release by
/// starting its VideoConverterSetup.exe with <c>/update</c> (it installs without asking, then opens the app again)
/// and closing the app. Runs on the UI thread; <see cref="Changed"/> fires there.
/// </summary>
public static class UpdateService
{
    public const string ManifestUrl = "https://scash.uk/vc/manifest.json";

    /// <summary>How often a running app looks again (Automatic updates on).</summary>
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static DispatcherTimer? _autoTimer;
    private static Task? _checking;

    public static UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>The newer release, while <see cref="State"/> is Available, Downloading or Installing.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>The newest release on the website at the last check (also when it isn't newer than this build).</summary>
    public static string? LatestVersion { get; private set; }

    /// <summary>What went wrong (Failed), for the user.</summary>
    public static string? Error { get; private set; }

    /// <summary>0–1 while the installer downloads.</summary>
    public static double Progress { get; private set; }

    public static DateTime? LastChecked => SettingsService.Current.LastUpdateCheck?.ToLocalTime();

    public static event Action? Changed;

    /// <summary>Checks once a little after startup and then every few hours, while Automatic updates is on.
    /// <paramref name="onFound"/> runs (once per version) when a check finds a newer release.</summary>
    public static void StartAutomaticChecks(Action<UpdateInfo> onFound)
    {
        _autoTimer?.Stop();
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoTimer.Tick += async (_, _) =>
        {
            _autoTimer.Interval = AutoCheckInterval;
            if (!SettingsService.Current.AutoCheckUpdates || State is UpdateState.Downloading or UpdateState.Installing) return;
            await CheckAsync();
            if (Available is { } update && SettingsService.Current.NotifiedUpdateVersion != update.Version)
            {
                var s = SettingsService.Current.Clone();
                s.NotifiedUpdateVersion = update.Version;
                SettingsService.Save(s);
                onFound(update);
            }
        };
        _autoTimer.Start();
    }

    /// <summary>Looks for a newer release. A check already running is joined, not repeated.</summary>
    public static Task CheckAsync() => _checking ??= CheckCoreAsync();

    private static async Task CheckCoreAsync()
    {
        try
        {
            if (State is UpdateState.Downloading or UpdateState.Installing) return;
            Set(UpdateState.Checking);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var manifest = JsonSerializer.Deserialize<Manifest>(await response.Content.ReadAsStringAsync())
                               ?? throw new InvalidDataException("empty manifest");
                if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Setup is not { Path.Length: > 0, Sha256.Length: 64 })
                    throw new InvalidDataException("the release information is incomplete");

                var s = SettingsService.Current.Clone();
                s.LastUpdateCheck = DateTime.UtcNow;
                SettingsService.Save(s);

                LatestVersion = manifest.Version;
                if (CompareVersions(manifest.Version, BuildInfoService.BuildVersion) > 0)
                {
                    Available = new UpdateInfo(
                        manifest.Version,
                        DateTime.TryParse(manifest.Published, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var published) ? published.ToLocalTime() : null,
                        manifest.Files?.Sum(f => f.Size) ?? 0,
                        new Uri(new Uri(ManifestUrl), manifest.Setup.Path),
                        manifest.Setup.Size,
                        manifest.Setup.Sha256.ToLowerInvariant(),
                        manifest.Notes?.Where(n => n.Items is { Count: > 0 })
                            .Select(n => new ChangelogSection(n.Title ?? "", n.Items!)).ToList() ?? []);
                    Set(UpdateState.Available);
                }
                else
                {
                    Available = null;
                    Set(UpdateState.UpToDate);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateService: check failed: {ex}");
                Error = ex is HttpRequestException or TaskCanceledException ? null : ex.Message;
                Set(UpdateState.Failed);
            }
        }
        finally
        {
            _checking = null;
        }
    }

    /// <summary>
    /// Downloads the release's setup (checked against the manifest's SHA-256), starts it with /update and closes
    /// the app; the setup closes anything still running, installs, and opens the new version.
    /// </summary>
    public static async Task InstallAsync()
    {
        if (Available is not { } update || State is UpdateState.Downloading or UpdateState.Installing) return;
        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "VideoConverterUpdate");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "VideoConverterSetup.exe");

            using (var response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? update.SetupSize;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                    {
                        Progress = Math.Min(1, (double)done / total);
                        Changed?.Invoke();
                    }
                }
            }

            string hash;
            await using (var stream = File.OpenRead(file))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (hash != update.SetupSha256)
                throw new InvalidDataException("the downloaded installer doesn't match the release (checksum)");

            Set(UpdateState.Installing);
            Process.Start(new ProcessStartInfo(file, "/update") { UseShellExecute = true, WorkingDirectory = dir });

            // Give the "Installing…" state a moment on screen, then close like the close button does.
            await Task.Delay(600);
            Application.Current.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService: install failed: {ex}");
            Error = ex is HttpRequestException or TaskCanceledException ? null : ex.Message;
            Set(UpdateState.Failed);
        }
    }

    private static void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Orders release names like "27H1.26", "27H1.26.4", "27H1.27", "27H2": part by part (a missing part counts as 0),
    /// each part by its numbers in turn ("27H1" = 27, 1), then by its text.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Trim().Split('.');
        var pb = b.Trim().Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            string x = i < pa.Length ? pa[i] : "0", y = i < pb.Length ? pb[i] : "0";
            var nx = Regex.Matches(x, @"\d+");
            var ny = Regex.Matches(y, @"\d+");
            for (int k = 0; k < Math.Min(nx.Count, ny.Count); k++)
            {
                int c = long.Parse(nx[k].Value).CompareTo(long.Parse(ny[k].Value));
                if (c != 0) return c;
            }
            if (nx.Count != ny.Count) return nx.Count.CompareTo(ny.Count);
            int t = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (t != 0) return t;
        }
        return 0;
    }

    // ═══ manifest.json (only what the app needs; installer/build_release.ps1 writes it) ═══

    private sealed class Manifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("published")] public string? Published { get; set; }
        [JsonPropertyName("files")] public List<ManifestFile>? Files { get; set; }
        [JsonPropertyName("setup")] public ManifestFile? Setup { get; set; }
        [JsonPropertyName("notes")] public List<ManifestNotes>? Notes { get; set; }
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    private sealed class ManifestNotes
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("items")] public List<string>? Items { get; set; }
    }
}
