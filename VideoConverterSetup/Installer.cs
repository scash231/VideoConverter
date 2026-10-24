using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VideoConverterSetup;

/// <summary>Where everything goes. Per-user install: no admin rights needed.</summary>
public static class SetupPaths
{
    public const string AppName = "VideoConverter";
    public const string AppExe = "VideoConverterUI.exe";
    public const string SetupExe = "VideoConverterSetup.exe";
    public const string Publisher = "scash";
    public const string AboutUrl = "https://scash.uk/vc";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VideoConverter";

    /// <summary>UpgradeCode of the old WiX MSI (installer/Package.wxs), removed on install.</summary>
    public const string OldMsiUpgradeCode = "{4C37E84B-12F1-94E6-952F-981A468B35A2}";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultInstallDir => Path.Combine(LocalAppData, "Programs", AppName);

    /// <summary>The app's own data folder (tools it downloads); same paths the app looks in.</summary>
    public static string ToolsRoot => Path.Combine(LocalAppData, AppName);
    public static string FfmpegDir => Path.Combine(ToolsRoot, "ffmpeg", "bin");          // FFmpegInstallerService.GetDefaultInstallDir
    public static string YtDlpExe => Path.Combine(ToolsRoot, "bin", "yt-dlp.exe");      // NativeEngineService.EnsureStandaloneYtDlp
    public static string SettingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>Install folder of the old per-machine MSI.</summary>
    public static string OldMsiDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    /// <summary>The setup exe itself (its assembly, so this stays right even when another process hosts it).</summary>
    public static string CurrentExe => typeof(SetupPaths).Assembly.Location;

    /// <summary>A picked folder gets "\VideoConverter" appended unless it already is one, so uninstall never empties a shared folder.</summary>
    public static string NormalizeInstallDir(string folder)
    {
        var full = Path.GetFullPath(folder.Trim().Trim('"')).TrimEnd('\\');
        return string.Equals(Path.GetFileName(full), AppName, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, AppName);
    }

    /// <summary>Null when the folder is usable, otherwise why not.</summary>
    public static string? CheckInstallDir(string dir)
    {
        try
        {
            if (!Path.IsPathRooted(dir) || Path.GetPathRoot(dir)!.TrimEnd('\\').Length >= dir.TrimEnd('\\').Length)
                return "Pick a folder, not a whole drive.";
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (dir.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                return "Setup can't install into the Windows folder.";
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Setup isn't allowed to write there without administrator rights. Pick a folder in your user profile.";
        }
        catch (Exception ex)
        {
            return "This folder can't be used: " + ex.Message;
        }
    }
}

/// <summary>The existing install, from its uninstall entry.</summary>
public sealed class InstalledInfo
{
    public string InstallDir = "";
    public string Version = "";
    public bool DesktopShortcut = true;
    public string[] Files = new string[0];

    public static InstalledInfo? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SetupPaths.UninstallKey);
            if (key?.GetValue("InstallLocation") is not string dir || string.IsNullOrWhiteSpace(dir)) return null;
            return new InstalledInfo
            {
                InstallDir = dir,
                Version = key.GetValue("DisplayVersion") as string ?? "",
                DesktopShortcut = !(key.GetValue("DesktopShortcut") is int d && d == 0),
                Files = key.GetValue("Files") as string[] ?? new string[0],
            };
        }
        catch (Exception ex)
        {
            Log.Write("reading the uninstall entry failed: " + ex.Message);
            return null;
        }
    }
}

public sealed class InstallOptions
{
    public string InstallDir = SetupPaths.DefaultInstallDir;
    public bool DesktopShortcut = true;
}

/// <summary>Plans and runs install / uninstall as a list of <see cref="SetupStep"/>s. Call from the UI thread.</summary>
public sealed class InstallerEngine
{
    private static readonly string[] AppProcessNames = { "VideoConverterUI", "ferrite" };
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "VideoConverterSetup");

    public List<SetupStep> Steps { get; } = new();

    /// <summary>Notes for the finish page (e.g. a tool that will be fetched by the app later).</summary>
    public List<string> Warnings { get; } = new();

    public event Action<double>? OverallProgress;

    private readonly List<Func<CancellationToken, Task>> _actions = new();

    // ── Checks used by the welcome page ────────────────────────────────────

    public static bool HasDesktopRuntime(RuntimeInfo runtime)
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env)) roots.Add(env!);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        foreach (var root in roots)
        {
            var dir = Path.Combine(root, "shared", runtime.Framework);
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                var dash = name.IndexOf('-');
                if (Version.TryParse(dash > 0 ? name.Substring(0, dash) : name, out var v) && v.Major == runtime.Major &&
                    File.Exists(Path.Combine(sub, "PresentationFramework.dll")))
                    return true;
            }
        }
        return false;
    }

    public static bool HasFfmpeg() =>
        File.Exists(Path.Combine(SetupPaths.FfmpegDir, "ffmpeg.exe")) && File.Exists(Path.Combine(SetupPaths.FfmpegDir, "ffprobe.exe"));

    /// <summary>Running app/engine processes started from one of these folders.</summary>
    public static List<Process> FindRunning(params string[] dirs)
    {
        var result = new List<Process>();
        foreach (var name in AppProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path != null && dirs.Any(d => !string.IsNullOrEmpty(d) &&
                        path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    result.Add(p);
                else
                    p.Dispose();
            }
        }
        return result;
    }

    // ── Install ────────────────────────────────────────────────────────────

    public void PlanInstall(ReleaseManifest manifest, InstallOptions options)
    {
        var dir = options.InstallDir;
        var runtime = manifest.EffectiveRuntime;

        if (FindRunning(dir, SetupPaths.OldMsiDir).Count > 0)
            Add(new SetupStep("Close VideoConverter", 1), ct => CloseRunningAsync(dir, SetupPaths.OldMsiDir));

        var oldMsi = App.KeepOldMsi ? new List<string>() : NativeMethods.FindMsiProducts(SetupPaths.OldMsiUpgradeCode);
        if (oldMsi.Count > 0)
            Add(new SetupStep("Remove the old MSI version", 5), ct => RemoveOldMsiAsync(oldMsi));

        if (!HasDesktopRuntime(runtime))
            Add(new SetupStep($".NET {runtime.Major} Desktop Runtime", Mb(runtime.Size)), ct => InstallRuntimeAsync(runtime, ct));

        foreach (var file in manifest.Files)
        {
            var f = file;
            Add(new SetupStep(FriendlyName(f.Name, manifest.Version), Mb(f.Size)), ct => InstallAppFileAsync(manifest, f, dir, ct));
        }

        if (!HasFfmpeg())
            Add(new SetupStep("FFmpeg", 100), ct => InstallFfmpegAsync(manifest.EffectiveFfmpegUrls, ct));

        Add(new SetupStep("yt-dlp", 18), ct => InstallYtDlpAsync(manifest.EffectiveYtDlpUrls, ct));

        Add(new SetupStep("Shortcuts", 1), ct => FinishInstallAsync(manifest, options));
    }

    private static string FriendlyName(string file, string version) => file.ToLowerInvariant() switch
    {
        "videoconverterui.exe" => $"VideoConverter {version}",
        "ferrite.exe" => "Ferrite engine",
        _ => file,
    };

    private static double Mb(long bytes) => Math.Max(1, bytes / (1024.0 * 1024.0));

    private async Task CloseRunningAsync(params string[] dirs)
    {
        var running = FindRunning(dirs);
        foreach (var p in running)
        {
            try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
        }
        await Task.Run(() =>
        {
            foreach (var p in running)
            {
                try
                {
                    if (!p.WaitForExit(4000)) { Log.Write($"killing {p.ProcessName} ({p.Id})"); p.Kill(); p.WaitForExit(3000); }
                }
                catch (Exception ex) { Log.Write($"closing {p.ProcessName} failed: {ex.Message}"); }
                finally { p.Dispose(); }
            }
        });
    }

    private async Task RemoveOldMsiAsync(List<string> productCodes)
    {
        var step = Current!;
        foreach (var code in productCodes)
        {
            step.Detail = "Waiting for administrator permission";
            var exit = await RunElevatedAsync("msiexec.exe", $"/x {code} /passive /norestart");
            Log.Write($"msiexec /x {code} -> {exit}");
            if (exit is 0 or 3010 or 1641 or 1605) continue;
            // the new install works next to the old one, so this is not fatal
            step.State = StepState.Skipped;
            step.Detail = exit == 1223 || exit == 1602 ? "Skipped, the old version stays installed" : $"Failed (code {exit}), the old version stays installed";
            Warnings.Add("The old MSI version of VideoConverter is still installed. You can remove it in Windows Settings → Apps.");
            return;
        }
        step.Detail = "Removed";
    }

    private async Task InstallRuntimeAsync(RuntimeInfo runtime, CancellationToken ct)
    {
        var step = Current!;
        var exe = Path.Combine(TempDir, $"windowsdesktop-runtime-{runtime.Major}-win-x64.exe");
        await DownloadIntoStepAsync(new Uri(runtime.Url), exe, null, runtime.Size, 0.85, ct);

        step.Detail = "Installing (Windows asks for permission)";
        int exit;
        try
        {
            exit = await RunElevatedAsync(exe, "/install /quiet /norestart");
        }
        finally
        {
            Downloader.TryDelete(exe);
        }
        Log.Write($".NET runtime installer -> {exit}");
        if (exit is 1223 or 1602)
            throw new SetupException($"VideoConverter needs the .NET {runtime.Major} Desktop Runtime, and installing it needs administrator permission. Run setup again and allow it.");
        if (exit is not (0 or 3010 or 1641))
            throw new SetupException($"The .NET {runtime.Major} Desktop Runtime could not be installed (code {exit}). You can install it yourself from https://dotnet.microsoft.com/download/dotnet/{runtime.Major}.0 and run setup again.");
        if (!HasDesktopRuntime(runtime))
            throw new SetupException($"The .NET {runtime.Major} Desktop Runtime installer finished, but the runtime was not found. Restart your PC and run setup again.");
        if (exit != 0) Warnings.Add("Windows wants a restart to finish installing .NET.");
        step.Detail = "Installed";
    }

    private Task InstallAppFileAsync(ReleaseManifest manifest, ReleaseFile file, string dir, CancellationToken ct) =>
        DownloadIntoStepAsync(manifest.Resolve(file.Path), Path.Combine(dir, file.Name), file.Sha256, file.Size, 1, ct);

    private async Task InstallFfmpegAsync(string[] urls, CancellationToken ct)
    {
        var step = Current!;
        var zip = Path.Combine(TempDir, "ffmpeg.zip");
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadIntoStepAsync(new Uri(url), zip, null, 100L * 1024 * 1024, 0.9, ct);
                step.Detail = "Unpacking";
                await Task.Run(() => ExtractFfmpeg(zip), ct);
                step.Detail = "";
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Write($"ffmpeg from {url} failed: {ex}");
            }
            finally
            {
                Downloader.TryDelete(zip);
            }
        }
        // VideoConverter offers its own FFmpeg install on first start, so this does not stop setup
        step.State = StepState.Skipped;
        step.Detail = "Couldn't download, the app will offer it later";
        Warnings.Add("FFmpeg couldn't be downloaded (" + (last?.Message ?? "unknown error") + "). VideoConverter will offer to install it when you start it.");
    }

    private static void ExtractFfmpeg(string zip)
    {
        Directory.CreateDirectory(SetupPaths.FfmpegDir);
        using var archive = ZipFile.OpenRead(zip);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if ((entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                && found.Add(entry.Name))
            {
                var target = Path.Combine(SetupPaths.FfmpegDir, entry.Name.ToLowerInvariant());
                entry.ExtractToFile(target + ".partial", overwrite: true);
                Downloader.ReplaceFile(target + ".partial", target);
            }
        }
        if (found.Count < 2) throw new SetupException("The FFmpeg download did not contain ffmpeg.exe and ffprobe.exe.");
    }

    private async Task InstallYtDlpAsync(string[] urls, CancellationToken ct)
    {
        var step = Current!;
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadIntoStepAsync(new Uri(url), SetupPaths.YtDlpExe, null, 18L * 1024 * 1024, 1, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Write($"yt-dlp from {url} failed: {ex}");
            }
        }
        // the app downloads yt-dlp itself when it is missing
        step.State = StepState.Skipped;
        if (File.Exists(SetupPaths.YtDlpExe))
        {
            step.Detail = "Couldn't update, kept the installed copy";
        }
        else
        {
            step.Detail = "Couldn't download, the app will fetch it later";
            Warnings.Add("yt-dlp couldn't be downloaded (" + (last?.Message ?? "unknown error") + "). VideoConverter will download it when you start it.");
        }
    }

    private async Task FinishInstallAsync(ReleaseManifest manifest, InstallOptions options)
    {
        var dir = options.InstallDir;
        await Task.Run(() =>
        {
            // keep a copy of setup in the install folder: it is the uninstaller (and repairs the install)
            var self = SetupPaths.CurrentExe;
            var setupCopy = Path.Combine(dir, SetupPaths.SetupExe);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, setupCopy + ".partial", true);
                Downloader.ReplaceFile(setupCopy + ".partial", setupCopy);
            }

            var appExe = Path.Combine(dir, SetupPaths.AppExe);
            NativeMethods.CreateShortcut(SetupPaths.StartMenuLink, appExe, dir, "Download and convert videos");
            if (options.DesktopShortcut)
                NativeMethods.CreateShortcut(SetupPaths.DesktopLink, appExe, dir, "Download and convert videos");
            else
                Downloader.TryDelete(SetupPaths.DesktopLink);

            var files = manifest.Files.Select(f => f.Name).Concat(new[] { SetupPaths.SetupExe }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            long bytes = files.Sum(f => { try { return new FileInfo(Path.Combine(dir, f)).Length; } catch { return 0L; } });

            using var key = Registry.CurrentUser.CreateSubKey(SetupPaths.UninstallKey);
            key.SetValue("DisplayName", SetupPaths.AppName);
            key.SetValue("DisplayVersion", manifest.Version);
            key.SetValue("Publisher", SetupPaths.Publisher);
            key.SetValue("DisplayIcon", appExe + ",0");
            key.SetValue("InstallLocation", dir);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", $"\"{setupCopy}\" /uninstall");
            key.SetValue("ModifyPath", $"\"{setupCopy}\"");
            key.SetValue("URLInfoAbout", SetupPaths.AboutUrl);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("DesktopShortcut", options.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("Files", files, RegistryValueKind.MultiString);

            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        });
        Log.Write($"installed {manifest.Version} into {dir}");
    }

    // ── Uninstall ──────────────────────────────────────────────────────────

    public void PlanUninstall(InstalledInfo installed, bool removeData)
    {
        var dir = installed.InstallDir;
        if (FindRunning(dir).Count > 0)
            Add(new SetupStep("Close VideoConverter", 1), _ => CloseRunningAsync(dir));
        Add(new SetupStep("Remove app files", 3), _ => Task.Run(() => RemoveAppFiles(installed)));
        Add(new SetupStep("Remove shortcuts", 1), _ => Task.Run(RemoveShortcutsAndEntry));
        if (removeData)
            Add(new SetupStep("Remove settings and downloaded tools", 3), _ => Task.Run(RemoveUserData));
    }

    private static void RemoveAppFiles(InstalledInfo installed)
    {
        var dir = installed.InstallDir;
        if (!Directory.Exists(dir)) return;
        var self = SetupPaths.CurrentExe;
        var names = installed.Files.Concat(new[] { SetupPaths.AppExe, "ferrite.exe", SetupPaths.SetupExe });
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, name);
            if (string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) continue; // deleted after exit
            DeleteFileWithRetry(path);
            Downloader.TryDelete(path + ".partial");
        }
        // folders the app or the user may have added next to the exe (FFmpeg copy, manually installed Legacy backend)
        foreach (var sub in new[] { "ffmpeg_bin", "backend" })
        {
            try { var p = Path.Combine(dir, sub); if (Directory.Exists(p)) Directory.Delete(p, true); }
            catch (Exception ex) { Log.Write($"removing {sub} failed: {ex.Message}"); }
        }
        TryRemoveEmptyDir(dir);
    }

    private static void RemoveShortcutsAndEntry()
    {
        Downloader.TryDelete(SetupPaths.StartMenuLink);
        Downloader.TryDelete(SetupPaths.DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(SetupPaths.UninstallKey, false); } catch (Exception ex) { Log.Write("removing the uninstall entry failed: " + ex.Message); }
        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private static void RemoveUserData()
    {
        foreach (var dir in new[] { SetupPaths.SettingsDir, SetupPaths.ToolsRoot })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Log.Write($"removing {dir} failed: {ex.Message}"); }
        }
        RemoveFromUserPath(SetupPaths.ToolsRoot);
    }

    /// <summary>The app's FFmpeg installer adds its folder to the user PATH; take that out again.</summary>
    private static void RemoveFromUserPath(string prefix)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(path)) return;
            var parts = path!.Split(';');
            var kept = parts.Where(p => !p.Trim().Trim('"').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kept.Length == parts.Length) return;
            Environment.SetEnvironmentVariable("PATH", string.Join(";", kept), EnvironmentVariableTarget.User);
        }
        catch (Exception ex)
        {
            Log.Write("cleaning PATH failed: " + ex.Message);
        }
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (int i = 0; i < 12 && File.Exists(path); i++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Thread.Sleep(250); }
        }
        if (File.Exists(path)) Log.Write("could not delete " + path);
    }

    private static void TryRemoveEmptyDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    /// <summary>
    /// The uninstaller can't delete its own exe while running: a hidden cmd does it a moment after we exit,
    /// together with the then empty install folder.
    /// </summary>
    public static void ScheduleSelfDelete(string installDir)
    {
        var self = SetupPaths.CurrentExe;
        if (!self.StartsWith(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/d /c ping 127.0.0.1 -n 3 >nul & del /f /q \"{self}\" & rmdir \"{installDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Exception ex)
        {
            Log.Write("self delete failed: " + ex.Message);
        }
    }

    // ── Running the plan ───────────────────────────────────────────────────

    private SetupStep? Current;

    private void Add(SetupStep step, Func<CancellationToken, Task> action)
    {
        step.Number = Steps.Count + 1;
        Steps.Add(step);
        _actions.Add(action);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(TempDir);
        for (int i = 0; i < Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = Current = Steps[i];
            step.State = StepState.Active;
            Log.Write("step: " + step.Title);
            try
            {
                await _actions[i](ct);
                if (step.State == StepState.Active) step.State = StepState.Done;
                step.Fraction = 1;
                if (step.State == StepState.Done && step.Detail.EndsWith("MB")) step.Detail = "";
            }
            catch (Exception ex)
            {
                step.State = StepState.Failed;
                step.Detail = ex is OperationCanceledException ? "Cancelled" : "Failed";
                Log.Write($"step {step.Title} failed: {ex}");
                throw;
            }
            finally
            {
                ReportOverall();
            }
        }
        try { Directory.Delete(TempDir, true); } catch { }
    }

    private async Task DownloadIntoStepAsync(Uri uri, string target, string? sha256, long expected, double share, CancellationToken ct)
    {
        var step = Current!;
        var progress = new Progress<(long read, long total)>(p =>
        {
            step.Fraction = share * p.read / Math.Max(1, p.total);
            step.Detail = $"{p.read / 1048576.0:0.0} / {p.total / 1048576.0:0.0} MB";
            ReportOverall();
        });
        await Downloader.DownloadAsync(uri, target, sha256, expected, progress, ct);
    }

    private void ReportOverall()
    {
        double total = 0, done = 0;
        foreach (var s in Steps)
        {
            total += s.Weight;
            done += s.Weight * (s.State is StepState.Done or StepState.Skipped ? 1 : s.Fraction);
        }
        OverallProgress?.Invoke(total <= 0 ? 0 : done / total);
    }

    /// <summary>Starts an exe through UAC and returns its exit code (1223 when the user said no).</summary>
    private static Task<int> RunElevatedAsync(string exe, string args) => Task.Run(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
    });
}
