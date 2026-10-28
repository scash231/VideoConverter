using System.Runtime.InteropServices;

namespace VideoConverterUI.Services;

// What's New: the notes live on the website (installer\whatsnew.json → https://scash.uk/vc/whatsnew.json),
// loaded by WhatsNewService; these records are its shape.

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

public sealed record ChangelogRelease(string Version, string Date, IReadOnlyList<ChangelogSection> Sections)
{
    public string Header => $"{Version}  ·  {Date}";
}

/// <summary>A big version (27H1, 26H2, …) and its releases, newest first. <see cref="Channel"/> is shown under the name.</summary>
public sealed record ChangelogVersion(string Name, string Channel, IReadOnlyList<ChangelogRelease> Releases)
{
    public string LatestDate => Releases.Count > 0 ? Releases[0].Date : "";
    public string FirstDate => Releases.Count > 0 ? Releases[^1].Date : "";
}

public static class BuildInfoService
{
    public static string BuildName => $"{BuildBigVersion} {BuildChannel}";

    /// <summary>The big version this build belongs to and its channel (Settings → Software Update → Update channel).</summary>
    public static string BuildBigVersion => "27H1";

    public static string BuildChannel => "Canary";

    public static string BuildVersion => "27H1.27.3";

    /// <summary>Source hash this build was stamped with.</summary>
    public static string CommitSha => "108cf6ad03b3fb6052047d908d55f310a8f60dbd";

    public static string ShortSha => CommitSha.Length >= 8 ? CommitSha[..8] : CommitSha;

    public static string BuildNumber => $"{BuildVersion} ({ShortSha})";

    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public static string ExecutionEngine => EngineProvider.ActiveKey switch
    {
        EngineProvider.ClassicKey => "Legacy Python Backend",
        EngineProvider.FerriteKey => "Ferrite Rust Engine",
        _ => "Native C# Engine",
    };

    /// <summary>"27H1.20 (sha) · Ferrite": the build number with the active backend.</summary>
    public static string BuildNumberWithEngine => $"{BuildNumber} · {EngineProvider.DisplayName(EngineProvider.ActiveKey)}";

    public static string MediaPipeline => "FFmpeg & yt-dlp Core";
}
