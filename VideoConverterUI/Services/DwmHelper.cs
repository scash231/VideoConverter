using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VideoConverterUI.Services;

/// <summary>
/// DWM attributes for windows with a system frame (not <c>AllowsTransparency</c>), e.g. the Glass Tuner:
/// the dark title bar in dark mode. The app's own windows draw their chrome themselves.
/// </summary>
public static class DwmHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Matches the title bar to the current dark/light mode.</summary>
    public static void UpdateDarkMode(Window window)
    {
        if (window.AllowsTransparency) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = ThemeService.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
