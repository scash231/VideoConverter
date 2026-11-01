using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VideoConverterUI.Services;

/// <summary>
/// WPF's render thread can die (UCEERR_RENDERTHREADFAILURE: graphics driver, GPU memory). Its channel can't be
/// reopened, so from then on every window message and every new window throws: the only way out is a new process.
/// A failure soon after such a restart restarts with software rendering, and one more gives up.
/// </summary>
public static class RenderRecovery
{
    private const int UceerrRenderThreadFailure = unchecked((int)0x88980406);
    private const string LevelArg = "--render-recovery=";
    // A failure later than this is a new incident, not the same one coming back.
    private static readonly TimeSpan SameIncident = TimeSpan.FromMinutes(5);

    private static readonly DateTime StartedAt = DateTime.UtcNow;
    private static readonly int Level = ReadLevel();
    private static bool _recovering;

    public static bool UseSoftwareRendering => Level >= 2;

    /// <summary>Handles <paramref name="ex"/> if the render thread died; false means it was something else.</summary>
    public static bool TryRecover(Exception ex)
    {
        if (_recovering) return true;
        if (!IsRenderFailure(ex)) return false;
        _recovering = true;

        Log(ex);
        var next = DateTime.UtcNow - StartedAt < SameIncident ? Level + 1 : 1;
        try { ThemeService.FlushPendingSave(); } catch { }
        // Don't leave yt-dlp or ffmpeg running, but a stuck engine must not hold up the restart.
        try { Task.Run(EngineProvider.Shutdown).Wait(TimeSpan.FromSeconds(3)); } catch { }

        if (next <= 2 && Environment.ProcessPath is { } exe)
        {
            try { Process.Start(new ProcessStartInfo(exe, LevelArg + next) { UseShellExecute = false }); }
            catch (Win32Exception) { }
        }
        else
        {
            // A plain Win32 dialog: WPF can't draw any more.
            MessageBox.Show("VideoConverter could not recover from a graphics error and has to close.\n\nUpdating your graphics driver may help.",
                "VideoConverter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Environment.Exit(1);
        return true;
    }

    /// <summary>After a restart from <see cref="TryRecover"/>, says why the window just reopened.</summary>
    public static void AnnounceRestart()
    {
        if (Level == 0) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var app = Application.Current;
            if (app?.TryFindResource("RenderRestarted") is string text)
                NotificationWindow.Show(text, app.MainWindow, NotificationType.Warning);
        };
        timer.Start();
    }

    private static bool IsRenderFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is COMException && e.HResult == UceerrRenderThreadFailure) return true;
        return false;
    }

    private static int ReadLevel()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith(LevelArg, StringComparison.Ordinal) && int.TryParse(arg[LevelArg.Length..], out var level))
                return level;
        return 0;
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoConverter");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "render-failures.log"),
                $"{DateTime.Now:s} {BuildInfoService.BuildVersion} level {Level}, up {DateTime.UtcNow - StartedAt:g}\n{ex}\n\n");
        }
        catch { }
    }
}
