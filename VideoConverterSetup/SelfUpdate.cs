using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoConverterSetup;

/// <summary>
/// Keeps setup itself current: when the manifest names a different VideoConverterSetup.exe than the one running
/// (an older download, or the copy in the install folder), that one is downloaded and started instead, with the same
/// arguments. It then copies itself into the install folder, so the uninstaller is updated as well.
/// </summary>
public static class SelfUpdate
{
    /// <summary>Marks the started setup, so it never updates itself in a loop.</summary>
    public const string RelaunchedSwitch = "/updated";

    private static string TargetExe => Path.Combine(Path.GetTempPath(), "VideoConverterSetup-update", SetupPaths.SetupExe);

    /// <summary>True when the newer setup was started and this one should close. Never throws: on any problem this setup simply carries on.</summary>
    public static async Task<bool> TryStartNewerAsync(ReleaseManifest manifest, CancellationToken ct)
    {
        var setup = manifest.Setup;
        if (App.SkipSelfUpdate || setup == null) return false;
        try
        {
            var self = SetupPaths.CurrentExe;
            if (string.Equals(Downloader.HashFile(self), setup.Sha256, StringComparison.OrdinalIgnoreCase)) return false;

            Log.Write($"newer setup in the manifest ({setup.Sha256}), updating {self}");
            await Downloader.DownloadAsync(manifest.Resolve(setup.Path), TargetExe, setup.Sha256, setup.Size, null, ct);
            var args = string.Join(" ", App.Args.Select(Quote).Concat(new[] { RelaunchedSwitch }));
            Process.Start(new ProcessStartInfo(TargetExe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(TargetExe) });
            Log.Write($"started {TargetExe} {args}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("self-update failed, carrying on with this setup: " + ex);
            return false;
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + (arg.EndsWith("\\") ? "\\" : "") + "\"";
}
