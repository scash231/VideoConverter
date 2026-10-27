using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoConverterSetup;

/// <summary>
/// manifest.json on the website (written by installer/build_release.ps1). Relative paths are resolved against the
/// manifest's own address, so the whole release can move to another host by changing only <see cref="DefaultUrl"/>.
/// </summary>
[DataContract]
public sealed class ReleaseManifest
{
    public const string DefaultUrl = "https://scash.uk/vc/manifest.json";

    /// <summary>Bump when a manifest needs features an older setup does not understand.</summary>
    /// 1: the first web setup. 2: black cube, updates itself (<see cref="SelfUpdate"/>). 3: the redesign.
    /// 4: navy sidebar + cards layout (the older look is retired: minSetup 4 makes those setups update or stop).
    /// Setups below the manifest's minSetup update themselves (2+) or are told to download the newest one (1).
    public const int SetupProtocol = 4;

    [DataMember(Name = "name")] public string Name { get; set; } = "VideoConverter";
    [DataMember(Name = "version")] public string Version { get; set; } = "";
    [DataMember(Name = "published")] public string? Published { get; set; }
    [DataMember(Name = "minSetup")] public int MinSetup { get; set; }
    [DataMember(Name = "files")] public ReleaseFile[] Files { get; set; } = new ReleaseFile[0];
    /// <summary>The current VideoConverterSetup.exe; a setup that differs from it starts this one instead (<see cref="SelfUpdate"/>).</summary>
    [DataMember(Name = "setup")] public ReleaseFile? Setup { get; set; }
    [DataMember(Name = "runtime")] public RuntimeInfo? Runtime { get; set; }
    [DataMember(Name = "ffmpeg")] public string[]? FfmpegUrls { get; set; }
    [DataMember(Name = "ytdlp")] public string[]? YtDlpUrls { get; set; }

    [IgnoreDataMember] public Uri BaseUri { get; private set; } = new(DefaultUrl);

    public RuntimeInfo EffectiveRuntime => Runtime ?? new RuntimeInfo();

    /// <summary>Checked only after <see cref="SelfUpdate"/> had its chance, so a too-old setup that can update itself does.</summary>
    public bool NeedsNewerSetup => MinSetup > SetupProtocol;

    // DataContractJsonSerializer skips constructors and initializers: set the defaults for missing members here.
    [OnDeserializing]
    private void OnDeserializing(StreamingContext _)
    {
        Name = "VideoConverter";
        Version = "";
        Files = new ReleaseFile[0];
        BaseUri = new Uri(DefaultUrl);
    }

    public string[] EffectiveFfmpegUrls => FfmpegUrls is { Length: > 0 } ? FfmpegUrls : new[]
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/GyanD/codexffmpeg/releases/latest/download/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    };

    public string[] EffectiveYtDlpUrls => YtDlpUrls is { Length: > 0 } ? YtDlpUrls : new[]
    {
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
    };

    public long AppDownloadBytes
    {
        get { long n = 0; foreach (var f in Files) n += f.Size; return n; }
    }

    public Uri Resolve(string pathOrUrl) => new(BaseUri, pathOrUrl);

    /// <summary>Loads the manifest from an http(s) address or, for testing, a local file.</summary>
    public static async Task<ReleaseManifest> LoadAsync(string location, CancellationToken ct)
    {
        Uri uri = Uri.TryCreate(location, UriKind.Absolute, out var u) ? u : new Uri(Path.GetFullPath(location));
        byte[] data;
        if (uri.IsFile)
        {
            data = File.ReadAllBytes(uri.LocalPath);
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var res = await Downloader.Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                throw new SetupException($"The release information could not be loaded ({(int)res.StatusCode} {res.ReasonPhrase}).\n{uri}");
            data = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        ReleaseManifest? m;
        try
        {
            using var ms = new MemoryStream(data);
            m = (ReleaseManifest?)new DataContractJsonSerializer(typeof(ReleaseManifest)).ReadObject(ms);
        }
        catch (Exception ex)
        {
            throw new SetupException("The release information is damaged: " + ex.Message);
        }
        if (m == null || string.IsNullOrWhiteSpace(m.Version) || m.Files == null || m.Files.Length == 0)
            throw new SetupException("The release information is incomplete (no version or no files).");
        foreach (var f in m.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Name) || f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new SetupException($"The release information names an invalid file: \"{f.Name}\".");
            if (string.IsNullOrWhiteSpace(f.Path))
                throw new SetupException($"The release information has no address for {f.Name}.");
            if (string.IsNullOrWhiteSpace(f.Sha256) || f.Sha256.Length != 64)
                throw new SetupException($"The release information has no checksum for {f.Name}.");
        }
        // a broken setup entry only means no self-update, never a failed install
        if (m.Setup != null && (string.IsNullOrWhiteSpace(m.Setup.Path) || m.Setup.Sha256?.Length != 64)) m.Setup = null;
        m.BaseUri = uri;
        return m;
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"{Name} {Version} from {BaseUri}");
        foreach (var f in Files) sb.Append($"\n  {f.Name} {f.Size} bytes sha256 {f.Sha256}");
        return sb.ToString();
    }
}

[DataContract]
public sealed class ReleaseFile
{
    /// <summary>File name inside the install folder.</summary>
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    /// <summary>Address of the file, relative to the manifest (or absolute).</summary>
    [DataMember(Name = "path")] public string Path { get; set; } = "";
    [DataMember(Name = "size")] public long Size { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; } = "";

    [OnDeserializing]
    private void OnDeserializing(StreamingContext _) { Name = ""; Path = ""; Sha256 = ""; }
}

[DataContract]
public sealed class RuntimeInfo
{
    /// <summary>Shared framework the app needs (framework-dependent publish).</summary>
    [DataMember(Name = "framework")] public string Framework { get; set; } = "Microsoft.WindowsDesktop.App";
    [DataMember(Name = "major")] public int Major { get; set; } = 10;
    [DataMember(Name = "url")] public string Url { get; set; } = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    [DataMember(Name = "size")] public long Size { get; set; } = DefaultSize;

    private const long DefaultSize = 60L * 1024 * 1024;

    [OnDeserializing]
    private void OnDeserializing(StreamingContext _)
    {
        var d = new RuntimeInfo();
        Framework = d.Framework; Major = d.Major; Url = d.Url; Size = d.Size;
    }
}

/// <summary>An error whose message is meant for the user as is.</summary>
public sealed class SetupException : Exception
{
    public SetupException(string message, Exception? inner = null) : base(message, inner) { }
}
