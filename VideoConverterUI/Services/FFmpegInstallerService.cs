using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;

namespace VideoConverterUI.Services;

public static class FFmpegInstallerService
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static FFmpegInstallerService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 VideoConverter/2.0");
    }

    private static readonly string[] DownloadUrls =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/GyanD/codexffmpeg/releases/latest/download/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
    ];

    /// <summary>Raised after this process's PATH changed (install/uninstall), so engines running in
    /// their own process (Ferrite) can pick up the same PATH.</summary>
    public static event Action? ProcessPathChanged;

    public static string GetDefaultInstallDir()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "VideoConverter", "ffmpeg", "bin");
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint HWND_BROADCAST = 0xffff;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static bool IsFfmpegInstalled(out string? ffmpegPath, out string? ffprobePath)
    {
        ffmpegPath = null;
        ffprobePath = null;

        var defaultBin = GetDefaultInstallDir();
        var defaultFfmpeg = Path.Combine(defaultBin, "ffmpeg.exe");
        var defaultFfprobe = Path.Combine(defaultBin, "ffprobe.exe");
        if (File.Exists(defaultFfmpeg))
        {
            ffmpegPath = defaultFfmpeg;
            if (File.Exists(defaultFfprobe))
                ffprobePath = defaultFfprobe;
            return true;
        }

        var appDir = MediaTools.AppDir;
        var candidates = new List<string>
        {
            Path.Combine(appDir, "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "backend", "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoConverter", "ffmpeg_bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoConverter", "ffmpeg_bin", "ffmpeg.exe"),
        };

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = dir.Trim().Trim('"');
            if (Directory.Exists(clean))
            {
                candidates.Add(Path.Combine(clean, "ffmpeg.exe"));
            }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                ffmpegPath = c;
                var pDir = Path.GetDirectoryName(c);
                if (!string.IsNullOrEmpty(pDir))
                {
                    var probeCandidate = Path.Combine(pDir, "ffprobe.exe");
                    if (File.Exists(probeCandidate))
                        ffprobePath = probeCandidate;
                }
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> InstallFfmpegAsync(
        IProgress<(double percent, string status)>? progress = null,
        bool addToUserPath = true,
        bool addToSystemPath = false,
        CancellationToken ct = default)
    {
        var targetDir = GetDefaultInstallDir();
        Directory.CreateDirectory(targetDir);

        var tempZip = Path.Combine(Path.GetTempPath(), $"ffmpeg_dl_{Guid.NewGuid():N}.zip");

        try
        {
            Exception? lastEx = null;
            bool downloaded = false;

            foreach (var url in DownloadUrls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report((0, "Connecting..."));

                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 32 * 1024 * 1024; // estimate ~32MB if header missing
                    var readBytes = 0L;

                    await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                    await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int bytesRead;
                        var lastReportTime = DateTime.UtcNow;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            readBytes += bytesRead;

                            if ((DateTime.UtcNow - lastReportTime).TotalMilliseconds > 100 || readBytes == totalBytes)
                            {
                                lastReportTime = DateTime.UtcNow;
                                double pct = Math.Min(80.0, (readBytes / (double)totalBytes) * 80.0);
                                double mbRead = readBytes / (1024.0 * 1024.0);
                                double mbTotal = totalBytes / (1024.0 * 1024.0);
                                progress?.Report((pct, $"Downloading... ({mbRead:F1} MB / {mbTotal:F1} MB)"));
                            }
                        }
                    }

                    downloaded = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (File.Exists(tempZip))
                    {
                        try { File.Delete(tempZip); } catch { }
                    }
                }
            }

            if (!downloaded)
            {
                throw lastEx ?? new Exception("Failed to download FFmpeg.");
            }

            // Extract binaries
            progress?.Report((82.0, "Installing files..."));
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempZip);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var destinationPath = Path.Combine(targetDir, entry.Name);
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                }
            }, ct);

            progress?.Report((90.0, "Configuring settings..."));

            // Add to User PATH
            if (addToUserPath)
            {
                AddDirectoryToPath(targetDir, EnvironmentVariableTarget.User);
            }

            // Always add to current process PATH so app immediately works without restart
            AddDirectoryToPath(targetDir, EnvironmentVariableTarget.Process);
            ProcessPathChanged?.Invoke();

            // Add to System PATH (requires admin elevation)
            if (addToSystemPath)
            {
                progress?.Report((95.0, "Requesting administrator permission..."));
                await SetSystemPathElevatedAsync(targetDir);
            }

            // Broadcast environment changes to all open Windows Explorer & terminal windows
            BroadcastEnvironmentChange();

            progress?.Report((100.0, "FFmpeg installation completed!"));
            return true;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    private static void AddDirectoryToPath(string targetDir, EnvironmentVariableTarget target)
    {
        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", target) ?? "";
            var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            var cleanTarget = targetDir.TrimEnd('\\', '/');
            if (!parts.Any(p => string.Equals(p.TrimEnd('\\', '/'), cleanTarget, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Insert(0, targetDir);
                Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), target);
            }
        }
        catch { }
    }

    private static async Task SetSystemPathElevatedAsync(string targetDir)
    {
        try
        {
            var script = $"[Environment]::SetEnvironmentVariable('PATH', '{targetDir};' + [Environment]::GetEnvironmentVariable('PATH', 'Machine'), 'Machine')";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }
        }
        catch { }
    }

    public static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_SETTINGCHANGE,
                UIntPtr.Zero,
                "Environment",
                SMTO_ABORTIFHUNG,
                2000,
                out _);
        }
        catch { }
    }

    public static bool UninstallFfmpeg(bool removeFromUserPath = true)
    {
        bool success = true;
        try
        {
            var installDir = GetDefaultInstallDir();
            var ffmpegDir = Directory.GetParent(installDir)?.FullName;

            if (Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, true); } catch { success = false; }
            }

            if (!string.IsNullOrEmpty(ffmpegDir) && Directory.Exists(ffmpegDir))
            {
                try { Directory.Delete(ffmpegDir, true); } catch { }
            }

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = Path.Combine(localApp, "VideoConverter", "ffmpeg_bin");
            if (Directory.Exists(oldDir))
            {
                try { Directory.Delete(oldDir, true); } catch { }
            }

            if (removeFromUserPath)
            {
                var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
                if (!string.IsNullOrEmpty(userPath))
                {
                    var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !(p.Contains("VideoConverter", StringComparison.OrdinalIgnoreCase) && p.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.User);
                }
            }

            // Clean Process PATH
            var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrEmpty(processPath))
            {
                var parts = processPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !(p.Contains("VideoConverter", StringComparison.OrdinalIgnoreCase) && p.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.Process);
                ProcessPathChanged?.Invoke();
            }

            BroadcastEnvironmentChange();
        }
        catch
        {
            success = false;
        }
        return success;
    }

    public static Task<bool> ShowInstallerDialogAsync(Window? owner = null)
    {
        var tcs = new TaskCompletionSource<bool>();

        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var dialog = new FFmpegInstallerDialog
                {
                    Owner = owner ?? Application.Current.MainWindow
                };

                dialog.Closed += (_, _) =>
                {
                    tcs.TrySetResult(dialog.InstalledSuccessfully);
                };

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    public static Task<bool> PromptAndInstallIfNeededAsync(Window? owner = null)
    {
        if (IsFfmpegInstalled(out _, out _))
            return Task.FromResult(true);

        return ShowInstallerDialogAsync(owner);
    }
}
