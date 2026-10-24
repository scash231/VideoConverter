using System;
using System.Linq;
using System.Windows;

namespace VideoConverterSetup;

/// <summary>
/// Command line:
///   /uninstall          remove the installed app (what Windows "Apps" runs)
///   /manifest=&lt;url|path&gt; use another release manifest (testing with a local build)
///   /dir=&lt;path&gt;         preset the install folder
///   /keepmsi            leave an old MSI install (installer/Package.wxs) alone instead of removing it
///   /noupdate           don't swap this setup for the newer one named in the manifest (/updated: already swapped)
/// </summary>
public partial class App : Application
{
    public static string[] Args { get; private set; } = new string[0];
    public static bool SkipSelfUpdate { get; private set; }
    public static bool UninstallMode { get; private set; }
    public static string ManifestLocation { get; private set; } = ReleaseManifest.DefaultUrl;
    public static string? PresetDir { get; private set; }
    public static bool KeepOldMsi { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Start(string.Join(" ", e.Args));
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("unhandled: " + ex.Exception);
            MessageBox.Show(ex.Exception.Message + "\n\nDetails: " + Log.FilePath, "VideoConverter Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        Args = e.Args;
#if DEBUG
        SkipSelfUpdate = true; // a dev build would otherwise swap itself for the released setup
#endif
        foreach (var arg in e.Args)
        {
            var a = arg.TrimStart('/', '-');
            if (a.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) UninstallMode = true;
            else if (a.Equals("updated", StringComparison.OrdinalIgnoreCase) || a.Equals("noupdate", StringComparison.OrdinalIgnoreCase)) SkipSelfUpdate = true;
            else if (a.StartsWith("manifest=", StringComparison.OrdinalIgnoreCase)) ManifestLocation = a.Substring(9).Trim('"');
            else if (a.StartsWith("dir=", StringComparison.OrdinalIgnoreCase)) PresetDir = a.Substring(4).Trim('"');
            else if (a.Equals("keepmsi", StringComparison.OrdinalIgnoreCase)) KeepOldMsi = true;
        }

        base.OnStartup(e);

        if (!Environment.Is64BitOperatingSystem)
        {
            MessageBox.Show("VideoConverter needs 64-bit Windows 10 or 11.", "VideoConverter Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        new MainWindow().Show();
    }
}
