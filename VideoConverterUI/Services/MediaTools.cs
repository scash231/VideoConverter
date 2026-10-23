using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoConverterUI.Services;

/// <summary>
/// Shared helpers for locating FFmpeg, running external tools and detecting a
/// hardware H.264 encoder that actually works on this machine.
/// </summary>
public static class MediaTools
{
    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs a process to completion while draining stdout and stderr concurrently,
    /// so a chatty tool can never dead-lock on a full pipe buffer.
    /// The process tree is killed when <paramref name="ct"/> is cancelled or the timeout elapses.
    /// </summary>
    public static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct = default, int timeoutMs = Timeout.Infinite)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding ??= Encoding.UTF8;
        psi.StandardErrorEncoding ??= Encoding.UTF8;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs != Timeout.Infinite)
            timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} did not finish within {timeoutMs} ms.");
        }

        return new ProcessResult(proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    // ── FFmpeg location ─────────────────────────────────────────────────────

    /// <summary>
    /// Folder of the real VideoConverterUI.exe (with a trailing separator, like <see cref="AppContext.BaseDirectory"/>).
    /// Use this for files that sit next to the exe (ferrite.exe, ffmpeg_bin, yt-dlp, backend). The single-file publish
    /// extracts itself to %TEMP%\.net\..., so BaseDirectory points there and only fits files bundled into the exe.
    /// </summary>
    public static string AppDir { get; } = GetAppDir();

    private static string GetAppDir()
    {
        var path = Environment.ProcessPath;
        var dir = Path.GetDirectoryName(path);
        // "dotnet VideoConverterUI.dll": the process is dotnet.exe, the app files are in BaseDirectory
        if (string.IsNullOrEmpty(dir) || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return AppContext.BaseDirectory;
        return dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
    }

    /// <summary>Directory that is searched upwards for a dev checkout containing <c>backend/</c>.</summary>
    public static string FindSolutionDir()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null && Directory.Exists(Path.Combine(exeDir, "backend")))
            return exeDir;

        var dir = AppDir;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "backend")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return exeDir ?? AppDir;
    }

    /// <summary>All places an ffmpeg.exe may live, most specific first (PATH entries last).</summary>
    public static List<string> GetFfmpegCandidates()
    {
        var appDir = AppDir;
        var solutionDir = FindSolutionDir();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new List<string>
        {
            Path.Combine(FFmpegInstallerService.GetDefaultInstallDir(), "ffmpeg.exe"),
            Path.Combine(appDir, "backend", "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(solutionDir, "backend", "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(solutionDir, "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(solutionDir, "ffmpeg.exe"),
            Path.Combine(localAppData, "VideoConverter", "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(appData, "VideoConverter", "ffmpeg_bin", "ffmpeg.exe"),
        };
        AddPathCandidates(candidates, "ffmpeg.exe");
        return candidates;
    }

    /// <summary>Full path of the first ffmpeg.exe found, or "ffmpeg" to rely on PATH resolution.</summary>
    public static string FindFfmpeg()
        => GetFfmpegCandidates().FirstOrDefault(File.Exists) ?? "ffmpeg";

    public static void AddPathCandidates(List<string> list, string exeName)
    {
        var targets = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine };
        foreach (var target in targets)
        {
            try
            {
                var pathVal = Environment.GetEnvironmentVariable("PATH", target);
                if (string.IsNullOrEmpty(pathVal)) continue;
                foreach (var part in pathVal.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var clean = part.Trim().Trim('"');
                    if (string.IsNullOrEmpty(clean) || clean.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var full = Path.Combine(clean, exeName);
                        if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
                            list.Add(full);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // ── Hardware encoder detection ──────────────────────────────────────────

    public sealed record HardwareEncoder(string Encoder, string HwAccel);

    private static readonly SemaphoreSlim _hwDetectLock = new(1, 1);
    private static bool _hwDetected;
    private static HardwareEncoder? _hwEncoder;

    /// <summary>
    /// Returns the first H.264 hardware encoder (NVENC → QSV → AMF) that can really encode a
    /// test frame. Listing <c>ffmpeg -encoders</c> is not enough: full FFmpeg builds ship all
    /// three encoders even when the matching GPU is missing. The result is cached.
    /// </summary>
    public static async Task<HardwareEncoder?> GetHardwareEncoderAsync(string? ffmpegPath = null)
    {
        if (_hwDetected) return _hwEncoder;

        await _hwDetectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hwDetected) return _hwEncoder;

            var ffmpeg = ffmpegPath ?? FindFfmpeg();
            var candidates = new[]
            {
                new HardwareEncoder("h264_nvenc", "cuda"),
                new HardwareEncoder("h264_qsv", "qsv"),
                new HardwareEncoder("h264_amf", "d3d11va"),
            };

            foreach (var candidate in candidates)
            {
                if (await CanEncodeAsync(ffmpeg, candidate.Encoder).ConfigureAwait(false))
                {
                    _hwEncoder = candidate;
                    break;
                }
            }

            _hwDetected = true;
            return _hwEncoder;
        }
        finally
        {
            _hwDetectLock.Release();
        }
    }

    private static async Task<bool> CanEncodeAsync(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.1 -frames:v 1 -c:v {encoder} -f null -",
            };
            var result = await RunProcessAsync(psi, CancellationToken.None, timeoutMs: 15000).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
