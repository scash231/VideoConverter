using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

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

        if (IsLightTheme()) ApplyLightPalette();

        base.OnStartup(e);

        if (!Environment.Is64BitOperatingSystem)
        {
            MessageBox.Show("VideoConverter needs 64-bit Windows 10 or 11.", "VideoConverter Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        new MainWindow().Show();
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>VideoConverter's light mode colors.</summary>
    private void ApplyLightPalette()
    {
        void Set(string key, string hex) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        Set("BgDeep", "#F2F2F7");
        Set("BgCard", "#FFFFFF");
        Set("BgElevated", "#E5E5EA");
        Set("BorderSub", "#D2D2D7");
        Set("WindowBorder", "#1F000000");
        Set("InputBg", "#0D000000");
        Set("HoverBg", "#14000000");
        Set("ActiveBg", "#1F000000");
        Set("Accent", "#007AFF");
        Set("AccentHover", "#3395FF");
        Set("TextPrimary", "#1D1D1F");
        Set("TextSec", "#6E6E73");
        Set("SuccessGreen", "#34C759");
        Set("ErrorRed", "#FF3B30");
        Set("WarnAmber", "#B25000");
        Set("SwitchOff", "#D1D1D6");
        Resources["GlowColor"] = (Color)ColorConverter.ConvertFromString("#40007AFF");
        Resources["GlowColor2"] = (Color)ColorConverter.ConvertFromString("#265AC8FA");
        Resources["AccentGrad"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString("#007AFF"), (Color)ColorConverter.ConvertFromString("#5AC8FA"), 0);
    }
}
