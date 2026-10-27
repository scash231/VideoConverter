using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public static class ThemeService
{
    public static event Action? ThemeApplied;

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoConverter", "theme.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static ThemeSettings Current { get; private set; } = Load();

    public static ThemeSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return MigrateLegacyPalette(JsonSerializer.Deserialize<ThemeSettings>(
                    File.ReadAllText(_path), _opts) ?? DarkMode);
        }
        catch { }
        return DarkMode;
    }

    /// <summary>
    /// Themes saved with the earlier orange Dark/Light palettes (and not customised since) move to
    /// the current Apple palettes, and the old 92 % default opacity becomes 100 %. Custom colours are left alone.
    /// </summary>
    private static ThemeSettings MigrateLegacyPalette(ThemeSettings theme)
    {
        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (Is(theme.BgDeep, "#E7141416") && Is(theme.Accent, "#FF9812"))
            return WithMode(theme, light: false);
        if (Is(theme.BgDeep, "#EBF2F3F5") && Is(theme.Accent, "#E07B00"))
            return WithMode(theme, light: true);
        // Windows were 92 % opaque by default while they had a blur behind them; without it they open solid.
        if (Is(theme.BgDeep, "#EB1C1C1E")) theme.BgDeep = DarkMode.BgDeep;
        else if (Is(theme.BgDeep, "#EBF2F2F7")) theme.BgDeep = LightMode.BgDeep;
        return theme;
    }

    public static void Save(ThemeSettings theme)
    {
        _saveTimer?.Stop();
        _savePending = false;
        Current = theme;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(theme, _opts));
    }

    private static DispatcherTimer? _saveTimer;
    private static bool _savePending;

    /// <summary>
    /// Makes <paramref name="theme"/> current immediately but writes it to disk only once the
    /// user stops changing values (e.g. while dragging a colour slider), instead of on every tick.
    /// </summary>
    public static void SaveDeferred(ThemeSettings theme)
    {
        Current = theme;
        _savePending = true;
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => FlushPendingSave();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void FlushPendingSave()
    {
        _saveTimer?.Stop();
        if (!_savePending) return;
        try { Save(Current); } catch { }
    }

    /// <summary>True while the applied theme has a light background (Light mode or a light custom theme).</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised after the effective mode flips between dark and light.</summary>
    public static event Action? ModeChanged;

    public static void Apply(ThemeSettings theme)
    {
        var res = Application.Current.Resources;

        if (SettingsService.Current.EnablePerformanceMode)
        {
            SetBrush(res, "BgDeep", "#121212", 255);
            SetBrush(res, "BgSurface", "#1E1E1E", 255);
            SetBrush(res, "BgCard", "#252525", 255);
            SetBrush(res, "BgElevated", "#2C2C2C", 255);
            SetBrush(res, "BorderSub", "#3E3E3E", 255);
            // Grey, not white: accent fills carry white text and switch knobs (selected sidebar item, Start, Update Now),
            // and accent text sits on the dark greys. #7A7A7A is ~4:1 against both.
            SetBrush(res, "Accent", "#7A7A7A", 255);
            SetBrush(res, "AccentPink", "#7A7A7A", 255);
            SetBrush(res, "TextPrimary", "#FFFFFF", 255);
            SetBrush(res, "TextSec", "#A0A0A0", 255);
            SetBrush(res, "SuccessGreen", "#FFFFFF", 255);
            SetBrush(res, "ErrorRed", "#CCCCCC", 255);

            var buttonBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            buttonBrush.Freeze();
            res["AccentGrad"] = buttonBrush;

            SetColor(res, "HoverBg", Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            SetColor(res, "ActiveBg", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SetColor(res, "CardHoverBg", Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PopupBg", Color.FromRgb(0x1E, 0x1E, 0x1E));
            SetColor(res, "PopupBorder", Color.FromRgb(0x3E, 0x3E, 0x3E));
            SetColor(res, "InputBg", Color.FromRgb(0x25, 0x25, 0x25));
            ApplyModeResources(res, light: false);
            ApplyAliases(res);

            ThemeApplied?.Invoke();
            return;
        }

        bool isLight = IsLightTheme(theme);

        // Keep text and borders readable even when a custom theme picks clashing values.
        string primaryHex = theme.TextPrimary;
        string secHex = theme.TextSec;
        string borderSubHex = theme.BorderSub;

        if (!isLight)
        {
            if (Luminance(theme.TextPrimary) is not double p || p < 0.55) primaryHex = DarkMode.TextPrimary;
            if (Luminance(theme.TextSec) is not double s || s < 0.40) secHex = DarkMode.TextSec;
            if (Luminance(theme.BorderSub) is not double b || b > 0.45) borderSubHex = DarkMode.BorderSub;
        }
        else
        {
            if (Luminance(theme.TextPrimary) is not double p || p > 0.35) primaryHex = LightMode.TextPrimary;
            if (Luminance(theme.TextSec) is not double s || s > 0.55) secHex = LightMode.TextSec;
            if (Luminance(theme.BorderSub) is not double b || b > 0.92) borderSubHex = LightMode.BorderSub;
        }

        byte deepAlpha = 255;
        if (TryParseColor(theme.BgDeep, out var deepCol))
        {
            deepAlpha = deepCol.A;
        }

        string effectiveBorderSub = theme.EnableBorders ? borderSubHex : "#00000000";

        SetBrush(res, "BgDeep", theme.BgDeep, deepAlpha);
        SetBrush(res, "BgSurface", theme.BgSurface, deepAlpha);
        SetBrush(res, "BgCard", theme.BgCard, deepAlpha);
        SetBrush(res, "BgElevated", theme.BgElevated, deepAlpha);
        SetBrush(res, "BorderSub", effectiveBorderSub);
        SetBrush(res, "Accent", theme.Accent);
        SetBrush(res, "AccentPink", theme.AccentAlt);
        SetBrush(res, "TextPrimary", primaryHex);
        SetBrush(res, "TextSec", secHex);
        SetBrush(res, "SuccessGreen", theme.SuccessGreen);
        SetBrush(res, "ErrorRed", theme.ErrorRed);

        if (TryParseColor(theme.ButtonGrad, out var gc))
        {
            if (res["AccentGrad"] is LinearGradientBrush lgb && !lgb.IsFrozen)
            {
                lgb.GradientStops[0].Color = gc;
                lgb.GradientStops[1].Color = gc;
            }
            else
            {
                res["AccentGrad"] = new LinearGradientBrush(gc, gc, 0);
            }
        }

        // Helper brushes for hover, active, input and popup states (Apple system fills)
        if (isLight)
        {
            SetColor(res, "HoverBg", Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
            SetColor(res, "ActiveBg", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            SetColor(res, "CardHoverBg", Color.FromArgb(0x08, 0x00, 0x00, 0x00));
            SetColor(res, "PopupBg", Color.FromArgb(0xFA, 0xFB, 0xFB, 0xFD));
            SetColor(res, "PopupBorder", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            SetColor(res, "InputBg", Color.FromArgb(0x14, 0x3C, 0x3C, 0x43));
        }
        else
        {
            SetColor(res, "HoverBg", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            SetColor(res, "ActiveBg", Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
            SetColor(res, "CardHoverBg", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            SetColor(res, "PopupBg", Color.FromArgb(0xFA, 0x2C, 0x2C, 0x2E));
            SetColor(res, "PopupBorder", Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C));
            SetColor(res, "InputBg", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        }

        ApplyModeResources(res, isLight);

        // Must run after InputBg is updated, otherwise ThumbGrad keeps pointing at the previous theme's brush.
        ApplyAliases(res);

        try
        {
            // Ensure all transparent windows maintain a clean transparent window background
            foreach (Window window in Application.Current.Windows)
            {
                if (window.AllowsTransparency && window.Background != Brushes.Transparent)
                {
                    window.Background = Brushes.Transparent;
                }
            }
        }
        catch { }

        ThemeApplied?.Invoke();
    }

    private static double? Luminance(string hex) =>
        TryParseColor(hex, out var c) ? (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 : null;

    private static bool _modeResourcesInitialized;

    /// <summary>
    /// Writes everything that depends only on dark vs. light: the <c>TintXX</c> contrast overlays
    /// (white at alpha XX in dark mode, black at alpha XX in light mode; XAML uses them instead of
    /// hard-coded <c>#XXFFFFFF</c> so hover fills, separators and chips stay visible on light
    /// backgrounds), a few fixed text brushes, the WPF-UI control theme and the DWM dark-mode flag.
    /// </summary>
    private static void ApplyModeResources(ResourceDictionary res, bool light)
    {
        if (_modeResourcesInitialized && light == IsLight) return;
        bool changed = _modeResourcesInitialized;
        _modeResourcesInitialized = true;
        IsLight = light;

        byte rgb = light ? (byte)0x00 : (byte)0xFF;
        for (int a = 1; a <= 0x80; a++)
            SetColor(res, "Tint" + a.ToString("X2"), Color.FromArgb((byte)a, rgb, rgb, rgb));

        // Text drawn on dark media (thumbnails, video) or on accent fills stays white in both modes.
        SetColor(res, "TextOnMedia", Colors.White);
        SetColor(res, "ConsoleText", light ? Color.FromRgb(0x1D, 0x1D, 0x1F) : Color.FromRgb(0xE5, 0xE5, 0xEA));
        // Black text shadows read as smudges behind dark text on light backgrounds.
        res["TextShadowOpacity"] = light ? 0.0 : 0.5;

        SwapControlTheme(res, light);

        try
        {
            foreach (Window window in Application.Current.Windows)
                DwmHelper.UpdateDarkMode(window);
        }
        catch { }

        if (changed) ModeChanged?.Invoke();
    }

    /// <summary>Switches the merged WPF-UI theme dictionary (default control text and fill colours).</summary>
    private static void SwapControlTheme(ResourceDictionary res, bool light)
    {
        var dicts = res.MergedDictionaries;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString;
            if (src == null || !src.Contains("/Resources/Theme/", StringComparison.OrdinalIgnoreCase)) continue;
            if (src.EndsWith(light ? "Light.xaml" : "Dark.xaml", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var uri = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme/" + (light ? "Light.xaml" : "Dark.xaml"), UriKind.Absolute);
                dicts[i] = new ResourceDictionary { Source = uri };
            }
            catch { }
            return;
        }
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex, byte deepAlpha = 255)
    {
        if (!TryParseColor(hex, out var color)) return;

        // Apply glassmorphism transparency to background layers based on deepAlpha
        if (key == "BgDeep")
        {
            color.A = deepAlpha;
        }
        else if (key == "BgCard")
        {
            color.A = (byte)Math.Clamp(Math.Round(deepAlpha * 0.85), 20, 255);
        }
        else if (key == "BgSurface")
        {
            color.A = (byte)Math.Clamp(Math.Round(deepAlpha * 0.90), 25, 255);
        }
        else if (key == "BgElevated")
        {
            color.A = (byte)Math.Clamp(Math.Round(deepAlpha * 0.95), 30, 255);
        }

        SetColor(res, key, color);
    }

    /// <summary>
    /// Updates the colour of a theme brush in place. Replacing a resource forces WPF to re-resolve
    /// every DynamicResource in all open windows, while mutating an (unfrozen) brush only triggers
    /// a repaint – that keeps live theme previews and adaptive thumbnail themes smooth.
    /// </summary>
    private static void SetColor(ResourceDictionary res, string key, Color color)
    {
        if (res[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            if (brush.Color != color)
                brush.Color = color;
        }
        else
        {
            res[key] = new SolidColorBrush(color);
        }
    }

    private static void ApplyAliases(ResourceDictionary res)
    {
        SetAlias(res, "HeaderGrad", "BgSurface");
        SetAlias(res, "BgGrad", "BgDeep");
        SetAlias(res, "ThumbGrad", "InputBg");
    }

    private static void SetAlias(ResourceDictionary res, string alias, string target)
    {
        var value = res[target];
        if (!ReferenceEquals(res[alias], value))
            res[alias] = value;
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    // ── Appearance Modes ──────────────────────────────────────
    // Only two built-in looks remain. Everything else is user-made (Customize / Custom Presets).

    public const string DarkModeName = "Dark";
    public const string LightModeName = "Light";

    // Apple system colours (macOS / iOS dark appearance)
    public static ThemeSettings DarkMode => new()
    {
        PresetName = DarkModeName,
        BgDeep = "#FF1C1C1E", BgSurface = "#232325", BgCard = "#2C2C2E",
        BgElevated = "#3A3A3C", BorderSub = "#38383A",
        Accent = "#0A84FF", AccentAlt = "#64D2FF",
        TextPrimary = "#F5F5F7", TextSec = "#98989D",
        SuccessGreen = "#30D158", ErrorRed = "#FF453A",
        ButtonGrad = "#3A3A3C"
    };

    // Apple system colours (macOS / iOS light appearance)
    public static ThemeSettings LightMode => new()
    {
        PresetName = LightModeName,
        BgDeep = "#FFF2F2F7", BgSurface = "#F9F9FB", BgCard = "#FFFFFF",
        BgElevated = "#E5E5EA", BorderSub = "#D2D2D7",
        Accent = "#007AFF", AccentAlt = "#5AC8FA",
        TextPrimary = "#1D1D1F", TextSec = "#6E6E73",
        SuccessGreen = "#34C759", ErrorRed = "#FF3B30",
        ButtonGrad = "#E5E5EA"
    };

    public static bool IsLightTheme(ThemeSettings theme)
    {
        if (!TryParseColor(theme.BgDeep, out var c)) return false;
        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5;
    }

    /// <summary>
    /// Returns a copy of <paramref name="theme"/> with the colours of the given mode, keeping its
    /// effect, layout and animation settings.
    /// </summary>
    public static ThemeSettings WithMode(ThemeSettings theme, bool light)
    {
        var mode = light ? LightMode : DarkMode;
        var result = theme.Clone();
        result.PresetName = mode.PresetName;
        result.BgDeep = mode.BgDeep;
        result.BgSurface = mode.BgSurface;
        result.BgCard = mode.BgCard;
        result.BgElevated = mode.BgElevated;
        result.BorderSub = mode.BorderSub;
        result.Accent = mode.Accent;
        result.AccentAlt = mode.AccentAlt;
        result.TextPrimary = mode.TextPrimary;
        result.TextSec = mode.TextSec;
        result.SuccessGreen = mode.SuccessGreen;
        result.ErrorRed = mode.ErrorRed;
        result.ButtonGrad = mode.ButtonGrad;
        return result;
    }
}
