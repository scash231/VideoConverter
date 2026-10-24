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
/// </summary>
public partial class App : Application
{
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

        foreach (var arg in e.Args)
        {
            var a = arg.TrimStart('/', '-');
            if (a.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) UninstallMode = true;
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
