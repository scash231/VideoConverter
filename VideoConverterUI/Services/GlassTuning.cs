using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VideoConverterUI.Services;

/// <summary>
/// Every value that shapes one Liquid Glass surface in one theme mode. Property defaults are the generic dark look;
/// each surface in <see cref="GlassTuning"/> overrides what it needs.
/// </summary>
public sealed class GlassParams
{
    /// <summary>Liquid Glass shader on; off = classic frosted fallback (what software rendering gets).</summary>
    public bool Liquid { get; set; } = true;
    public double CornerRadius { get; set; } = 14;

    // LiquidGlassEffect
    public double Saturation { get; set; } = 1.7;
    public double Brightness { get; set; } = 1.0;
    public double Frost { get; set; } = 10;
    public double Refraction { get; set; } = 20;
    public double Bevel { get; set; } = 10;
    public double Dispersion { get; set; } = 0;
    public double Highlight { get; set; } = 0.55;
    public double Inset { get; set; } = 34;
    /// <summary>Colour beyond the edge mirrored into a soft band inside it (0 = off).</summary>
    public double Reflection { get; set; } = 0;
    public double ReflectionReach { get; set; } = 40;
    public double ReflectionWidth { get; set; } = 24;

    // Layers around the shader
    public double BackdropBlur { get; set; } = 4;
    public double TintAlpha { get; set; } = 0x1F;
    public double SheenAlpha { get; set; } = 0x14;

    // Frosted fallback
    public double FallbackBlur { get; set; } = 30;
    public double FallbackTintAlpha { get; set; } = 0xA0;

    // Drop shadow of the surface's parent
    public double ShadowBlur { get; set; } = 24;
    public double ShadowDepth { get; set; } = 6;
    public double ShadowOpacity { get; set; } = 0.18;

    public GlassParams Clone() => (GlassParams)MemberwiseClone();
}

/// <summary>Names of the glass surfaces (<see cref="Controls.LiquidGlassPanel.Surface"/>).</summary>
public static class GlassSurfaces
{
    public const string Notification = "Notification";
    public const string Dropdown = "Dropdown";
    /// <summary>Flyouts in popups: Tools menu, help, error details, language picker, Queue Options.</summary>
    public const string Popup = "Popup";
    /// <summary>Dialog windows over their owner (Cloud Setup, FFmpeg installer, …) and alert sheets.</summary>
    public const string Dialog = "Dialog";
    /// <summary>The Settings sidebar: glass over the window, catching the light of the content beside it.</summary>
    public const string Sidebar = "Sidebar";
    /// <summary>Small round buttons floating over content (Settings close and back, Editor close, Multi Downloader title bar).</summary>
    public const string Button = "Button";
    /// <summary>The Multi Downloader's title-bar capsules beside the add-links pill: Button with a strong reflection
    /// that catches the blue Fetch button next to them.</summary>
    public const string TitleButton = "TitleButton";
    /// <summary>The Video Editor's inspector (the Settings sidebar's twin, beside the video) and its timeline bar.</summary>
    public const string Inspector = "Inspector";
    /// <summary>The Multi Downloader's floating link bar/toolbar and its footer, over the queue.</summary>
    public const string Toolbar = "Toolbar";
    /// <summary>The main window's header pills (help, console/log, tools, language, performance overlay).</summary>
    public const string HeaderPill = "HeaderPill";
    /// <summary>The main window's link field and its mode toggle's track.</summary>
    public const string UrlBar = "UrlBar";
    /// <summary>The main window's cards (preview, upload/download settings, finished actions).</summary>
    public const string Card = "Card";
    /// <summary>The main window's Start button (also the progress capsule while a job runs).</summary>
    public const string StartButton = "StartButton";
}

/// <summary>
/// The live Liquid Glass values, per surface and per theme mode. Defaults (<see cref="Registry"/>) are the shipped
/// look; the Glass Tuner (developer tool) edits them, every <see cref="Controls.LiquidGlassPanel"/> re-applies them on
/// <see cref="Changed"/>, and <see cref="Save"/> keeps them in <c>%APPDATA%\VideoConverter\glass.json</c>.
/// To give something new the glass: wrap it in a LiquidGlassPanel with a new Surface name and add defaults here.
/// A name without an entry still works (generic defaults) and shows up in the tuner once used.
/// </summary>
public static class GlassTuning
{
    private sealed record SurfaceDefaults(string Description, Func<GlassParams> Dark, Func<GlassParams> Light);

    private static readonly Dictionary<string, SurfaceDefaults> Registry = new()
    {
        // Every surface shares the sidebar's look: no prism split (it turns white text beside the rim into red ghosts),
        // a thin rim with a soft highlight, a moderate lens, and a soft reflection of the colour around it. Sizes scale
        // with the surface; the tint grows with how much small text it holds.
        [GlassSurfaces.Notification] = new("Toast notifications and the drop zone",
            () => new GlassParams
            {
                CornerRadius = 21,
                Frost = 10,
                Refraction = 14,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.5,
                Reflection = 0.35,
                ReflectionReach = 28,
                ReflectionWidth = 12,
                ShadowBlur = 18,
                ShadowDepth = 4,
                ShadowOpacity = 0.3,
            },
            () => new GlassParams
            {
                CornerRadius = 21,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 10,
                Refraction = 14,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.35,
                Reflection = 0.3,
                ReflectionReach = 28,
                ReflectionWidth = 12,
                TintAlpha = 0x4D,
                FallbackTintAlpha = 0x99,
            }),
        // Menus hold rows of small text, so they frost and tint more than the toast.
        [GlassSurfaces.Dropdown] = new("Selector menus (ComboBox drop-downs)",
            () => new GlassParams
            {
                CornerRadius = 10,
                Frost = 14,
                Refraction = 14,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.5,
                Reflection = 0.3,
                ReflectionReach = 32,
                ReflectionWidth = 14,
                BackdropBlur = 6,
                TintAlpha = 0x48,
                FallbackTintAlpha = 0xD8,
                ShadowOpacity = 0.28,
            },
            () => new GlassParams
            {
                CornerRadius = 10,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 14,
                Refraction = 14,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.35,
                Reflection = 0.25,
                ReflectionReach = 32,
                ReflectionWidth = 14,
                BackdropBlur = 6,
                TintAlpha = 0x80,
                FallbackTintAlpha = 0xE0,
                ShadowOpacity = 0.2,
            }),
        [GlassSurfaces.Popup] = new("Flyouts (Tools menu, help, error details, language, Queue Options)",
            () => new GlassParams
            {
                CornerRadius = 12,
                Frost = 14,
                Refraction = 16,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.5,
                Reflection = 0.3,
                ReflectionReach = 36,
                ReflectionWidth = 16,
                BackdropBlur = 6,
                TintAlpha = 0x50,
                FallbackTintAlpha = 0xD8,
                ShadowBlur = 22,
                ShadowDepth = 2,
                ShadowOpacity = 0.3,
            },
            () => new GlassParams
            {
                CornerRadius = 12,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 14,
                Refraction = 16,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.35,
                Reflection = 0.25,
                ReflectionReach = 36,
                ReflectionWidth = 16,
                BackdropBlur = 6,
                TintAlpha = 0x88,
                FallbackTintAlpha = 0xE0,
                ShadowBlur = 22,
                ShadowDepth = 2,
                ShadowOpacity = 0.2,
            }),
        // Dialogs are large and full of controls: the most frost and tint.
        [GlassSurfaces.Dialog] = new("Dialogs and alert sheets",
            () => new GlassParams
            {
                CornerRadius = 16,
                Frost = 18,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.55,
                Reflection = 0.3,
                ReflectionReach = 48,
                ReflectionWidth = 22,
                BackdropBlur = 8,
                TintAlpha = 0x70,
                FallbackTintAlpha = 0xE6,
                ShadowBlur = 30,
                ShadowDepth = 8,
                ShadowOpacity = 0.3,
            },
            () => new GlassParams
            {
                CornerRadius = 16,
                Saturation = 1.5,
                Brightness = 1.04,
                Frost = 18,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.35,
                Reflection = 0.25,
                ReflectionReach = 48,
                ReflectionWidth = 22,
                BackdropBlur = 8,
                TintAlpha = 0x99,
                FallbackTintAlpha = 0xEB,
                ShadowBlur = 30,
                ShadowDepth = 8,
                ShadowOpacity = 0.2,
            }),
        // Nothing lies directly behind the sidebar but the window, so its backdrop is the content pane beside it:
        // the lensing rim stays thin (10 px); the reflection mirrors the cards and buttons beside it (up to 64 px past the edge)
        // into a soft band, strong enough that a coloured control next to it (Reset All…) visibly tints the rim.
        // No dispersion here or on the buttons: they sit right next to white text, which the colour split turns into red ghosts.
        [GlassSurfaces.Sidebar] = new("Settings sidebar",
            () => new GlassParams
            {
                CornerRadius = 14,
                Frost = 16,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.55,
                Inset = 66,
                Reflection = 0.55,
                ReflectionReach = 64,
                ReflectionWidth = 22,
                BackdropBlur = 6,
                TintAlpha = 0x38,
                FallbackTintAlpha = 0xB0,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.14,
            },
            () => new GlassParams
            {
                CornerRadius = 14,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 16,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.35,
                Inset = 66,
                Reflection = 0.5,
                ReflectionReach = 64,
                ReflectionWidth = 22,
                BackdropBlur = 6,
                TintAlpha = 0x70,
                FallbackTintAlpha = 0xD0,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.1,
            }),
        // A 28 px circle: a thin, soft rim (a wide or bright one reads as a thick ring at this size), and like the sidebar
        // a reflection of the colour around it (up to 20 px out), since it often floats just above content.
        [GlassSurfaces.Button] = new("Round floating buttons (Settings close/back, Editor close, Multi Downloader title bar)",
            () => new GlassParams
            {
                CornerRadius = 14,
                Frost = 6,
                Refraction = 10,
                Bevel = 4,
                Dispersion = 0,
                Highlight = 0.4,
                Inset = 24,
                Reflection = 0.4,
                ReflectionReach = 20,
                ReflectionWidth = 9,
                BackdropBlur = 3,
                TintAlpha = 0x30,
                FallbackTintAlpha = 0xA0,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.18,
            },
            () => new GlassParams
            {
                CornerRadius = 14,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 6,
                Refraction = 10,
                Bevel = 4,
                Dispersion = 0,
                Highlight = 0.3,
                Inset = 24,
                Reflection = 0.3,
                ReflectionReach = 20,
                ReflectionWidth = 9,
                BackdropBlur = 3,
                TintAlpha = 0x60,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.12,
            }),
        // The round buttons' glass, but its reflection reaches further and lies deeper inside the rim, so the accent Fetch
        // button sliding out beside the capsules (a stand-in for it sits in their backdrop) visibly tints them blue.
        [GlassSurfaces.TitleButton] = new("Multi Downloader title-bar capsules (Options, Save & Exit, window controls)",
            () => new GlassParams
            {
                CornerRadius = 14,
                Frost = 6,
                Refraction = 12,
                Bevel = 5,
                Dispersion = 0,
                Highlight = 0.4,
                Inset = 48,
                Reflection = 0.9,
                ReflectionReach = 44,
                ReflectionWidth = 15,
                BackdropBlur = 3,
                TintAlpha = 0x30,
                FallbackTintAlpha = 0xA0,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.18,
            },
            () => new GlassParams
            {
                CornerRadius = 14,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 6,
                Refraction = 12,
                Bevel = 5,
                Dispersion = 0,
                Highlight = 0.3,
                Inset = 48,
                Reflection = 0.85,
                ReflectionReach = 44,
                ReflectionWidth = 15,
                BackdropBlur = 3,
                TintAlpha = 0x60,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.12,
            }),
        // The Settings sidebar's frost, lens and reflection with less tint; the rim catches the preview beside it.
        [GlassSurfaces.Inspector] = new("Video Editor inspector and timeline",
            () => new GlassParams
            {
                CornerRadius = 14,
                Frost = 16,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.55,
                Inset = 66,
                Reflection = 0.55,
                ReflectionReach = 64,
                ReflectionWidth = 22,
                BackdropBlur = 6,
                TintAlpha = 0x22,
                FallbackTintAlpha = 0x98,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.14,
            },
            () => new GlassParams
            {
                CornerRadius = 14,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 16,
                Refraction = 20,
                Bevel = 10,
                Dispersion = 0,
                Highlight = 0.35,
                Inset = 66,
                Reflection = 0.5,
                ReflectionReach = 64,
                ReflectionWidth = 22,
                BackdropBlur = 6,
                TintAlpha = 0x4C,
                FallbackTintAlpha = 0xB8,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.1,
            }),
        // Wide bars holding inputs and small labels, with queue items scrolling underneath: light frost, a little tint.
        [GlassSurfaces.Toolbar] = new("Multi Downloader link bar, toolbar and footer",
            () => new GlassParams
            {
                CornerRadius = 16,
                Frost = 7,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.5,
                Inset = 44,
                Reflection = 0.3,
                ReflectionReach = 40,
                ReflectionWidth = 16,
                BackdropBlur = 3,
                TintAlpha = 0x30,
                FallbackTintAlpha = 0xB4,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.22,
            },
            () => new GlassParams
            {
                CornerRadius = 16,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 7,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.35,
                Inset = 44,
                Reflection = 0.25,
                ReflectionReach = 40,
                ReflectionWidth = 16,
                BackdropBlur = 3,
                TintAlpha = 0x62,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 24,
                ShadowDepth = 4,
                ShadowOpacity = 0.14,
            }),
        // The main window's glass lies on the window's own background (gradient included), so there is little to
        // refract: the look comes from the rim, the lens bending the gradient and the sheen. 34 px capsules: the round
        // buttons' thin rim, a little more tint for the small labels.
        [GlassSurfaces.HeaderPill] = new("Main window header pills (help, console/log, tools, language)",
            () => new GlassParams
            {
                CornerRadius = 17,
                Frost = 6,
                Refraction = 10,
                Bevel = 4,
                Dispersion = 0,
                Highlight = 0.45,
                Inset = 24,
                Reflection = 0.4,
                ReflectionReach = 20,
                ReflectionWidth = 9,
                BackdropBlur = 3,
                TintAlpha = 0x34,
                FallbackTintAlpha = 0xA8,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.18,
            },
            () => new GlassParams
            {
                CornerRadius = 17,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 6,
                Refraction = 10,
                Bevel = 4,
                Dispersion = 0,
                Highlight = 0.3,
                Inset = 24,
                Reflection = 0.3,
                ReflectionReach = 20,
                ReflectionWidth = 9,
                BackdropBlur = 3,
                TintAlpha = 0x66,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 10,
                ShadowDepth = 2,
                ShadowOpacity = 0.12,
            }),
        // A wide capsule holding the link text: the Multi Downloader's link bar. The mode toggle's track shares it.
        [GlassSurfaces.UrlBar] = new("Main window link field and mode toggle",
            () => new GlassParams
            {
                CornerRadius = 25,
                Frost = 7,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.5,
                Inset = 44,
                Reflection = 0.3,
                ReflectionReach = 40,
                ReflectionWidth = 16,
                BackdropBlur = 3,
                TintAlpha = 0x30,
                FallbackTintAlpha = 0xB4,
                ShadowBlur = 18,
                ShadowDepth = 3,
                ShadowOpacity = 0.2,
            },
            () => new GlassParams
            {
                CornerRadius = 25,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 7,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.35,
                Inset = 44,
                Reflection = 0.25,
                ReflectionReach = 40,
                ReflectionWidth = 16,
                BackdropBlur = 3,
                TintAlpha = 0x62,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 18,
                ShadowDepth = 3,
                ShadowOpacity = 0.12,
            }),
        // Large panels full of fields and small labels: the sidebar's lens and rim with a calmer reflection and more
        // tint, so the text boxes and drop-downs on them stay easy to read. No shadow (their Borders carry none: a
        // shadow effect over a card with text boxes redraws it on every caret blink).
        [GlassSurfaces.Card] = new("Main window cards (preview, upload/download settings, finished actions)",
            () => new GlassParams
            {
                CornerRadius = 16,
                Frost = 12,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.5,
                Inset = 52,
                Reflection = 0.3,
                ReflectionReach = 44,
                ReflectionWidth = 18,
                BackdropBlur = 5,
                TintAlpha = 0x3C,
                FallbackTintAlpha = 0xB8,
                ShadowBlur = 0,
                ShadowDepth = 0,
                ShadowOpacity = 0,
            },
            () => new GlassParams
            {
                CornerRadius = 16,
                Saturation = 1.6,
                Brightness = 1.04,
                Frost = 12,
                Refraction = 18,
                Bevel = 9,
                Dispersion = 0,
                Highlight = 0.35,
                Inset = 52,
                Reflection = 0.25,
                ReflectionReach = 44,
                ReflectionWidth = 18,
                BackdropBlur = 5,
                TintAlpha = 0x70,
                FallbackTintAlpha = 0xD0,
                ShadowBlur = 0,
                ShadowDepth = 0,
                ShadowOpacity = 0,
            }),
        // The main action: a brighter rim and sheen than the link field, so it reads as a button, not a field.
        [GlassSurfaces.StartButton] = new("Main window Start button and its progress capsule",
            () => new GlassParams
            {
                CornerRadius = 21,
                Frost = 7,
                Refraction = 16,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.65,
                Inset = 40,
                Reflection = 0.35,
                ReflectionReach = 36,
                ReflectionWidth = 14,
                BackdropBlur = 3,
                TintAlpha = 0x3C,
                SheenAlpha = 0x22,
                FallbackTintAlpha = 0xB4,
                ShadowBlur = 18,
                ShadowDepth = 3,
                ShadowOpacity = 0.24,
            },
            () => new GlassParams
            {
                CornerRadius = 21,
                Saturation = 1.6,
                Brightness = 1.05,
                Frost = 7,
                Refraction = 16,
                Bevel = 8,
                Dispersion = 0,
                Highlight = 0.45,
                Inset = 40,
                Reflection = 0.3,
                ReflectionReach = 36,
                ReflectionWidth = 14,
                BackdropBlur = 3,
                TintAlpha = 0x70,
                SheenAlpha = 0x22,
                FallbackTintAlpha = 0xC8,
                ShadowBlur = 18,
                ShadowDepth = 3,
                ShadowOpacity = 0.14,
            }),
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoConverter", "glass.json");

    private sealed class ModeSet
    {
        public GlassParams? Dark { get; set; }
        public GlassParams? Light { get; set; }
    }

    private static readonly Dictionary<string, ModeSet> Values = new();

    /// <summary>Raised after any value changes (on the UI thread that changed it).</summary>
    public static event Action? Changed;

    static GlassTuning() => Load();

    /// <summary>Known surfaces: the registered ones first, then any other name that has been used.</summary>
    public static IReadOnlyList<string> Surfaces => Registry.Keys.Concat(Values.Keys.Except(Registry.Keys)).ToList();

    public static string Describe(string surface) => Registry.TryGetValue(surface, out var d) ? d.Description : "Custom surface";

    public static GlassParams For(string surface, bool light)
    {
        if (!Values.TryGetValue(surface, out var set))
            Values[surface] = set = new ModeSet();
        return light
            ? set.Light ??= Default(surface, true)
            : set.Dark ??= Default(surface, false);
    }

    public static GlassParams Current(string surface) => For(surface, ThemeService.IsLight);

    /// <summary>
    /// What a surface actually renders with: its tuned values (<see cref="Current"/>) shifted by the user's
    /// Clear ↔ Tinted slider (<see cref="Models.ThemeSettings.LiquidGlassTint"/>; 0.5 leaves them as tuned).
    /// Clear thins the tint and frost so more of the backdrop shows; Tinted thickens them for contrast.
    /// </summary>
    public static GlassParams Effective(string surface)
    {
        var p = Current(surface).Clone();
        var t = Math.Clamp(ThemeService.Current?.LiquidGlassTint ?? 0.5, 0, 1);
        if (t < 0.5)
        {
            var k = (0.5 - t) * 2;
            p.TintAlpha *= 1 - 0.75 * k;
            p.FallbackTintAlpha *= 1 - 0.3 * k;
            p.Frost *= 1 - 0.5 * k;
            p.BackdropBlur *= 1 - 0.6 * k;
        }
        else if (t > 0.5)
        {
            var k = (t - 0.5) * 2;
            p.TintAlpha += Math.Max(0, 0xD8 - p.TintAlpha) * 0.85 * k;
            p.FallbackTintAlpha += Math.Max(0, 0xF5 - p.FallbackTintAlpha) * k;
            p.Frost *= 1 + 0.6 * k;
            p.Saturation += (1.2 - p.Saturation) * 0.5 * k;
        }
        return p;
    }

    private static GlassParams Default(string surface, bool light)
        => Registry.TryGetValue(surface, out var d) ? (light ? d.Light() : d.Dark()) : new GlassParams();

    public static void NotifyChanged() => Changed?.Invoke();

    public static bool HasSavedFile => File.Exists(FilePath);

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            using var doc = JsonDocument.Parse(json);
            // 27H1.20 builds before the tuner went universal saved only the notification: { "Dark": …, "Light": … }.
            if (doc.RootElement.TryGetProperty("Dark", out _) || doc.RootElement.TryGetProperty("Light", out _))
            {
                Values[GlassSurfaces.Notification] = JsonSerializer.Deserialize<ModeSet>(json) ?? new ModeSet();
                return;
            }
            foreach (var (surface, set) in JsonSerializer.Deserialize<Dictionary<string, ModeSet>>(json) ?? [])
                Values[surface] = set;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GlassTuning: could not read {FilePath}: {ex.Message}");
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Back to the shipped look for one surface and mode (in memory; <see cref="Save"/> or <see cref="DeleteSaved"/> to persist).</summary>
    public static void Reset(string surface, bool light)
    {
        var set = Values.TryGetValue(surface, out var s) ? s : Values[surface] = new ModeSet();
        if (light) set.Light = Default(surface, true);
        else set.Dark = Default(surface, false);
        NotifyChanged();
    }

    /// <summary>Removes the saved file, so the next start uses the shipped defaults.</summary>
    public static void DeleteSaved()
    {
        try { File.Delete(FilePath); } catch { }
    }

    /// <summary>One surface's two modes as C# initializers, ready to paste over its defaults in <see cref="Registry"/>.</summary>
    public static string ToCSharp(string surface)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {surface}");
        Append(sb, "Dark", For(surface, false), Default(surface, false));
        Append(sb, "Light", For(surface, true), Default(surface, true));
        return sb.ToString();
    }

    /// <summary>Only the values that differ from <see cref="GlassParams"/>'s own defaults, like the entries in Registry.</summary>
    private static void Append(StringBuilder sb, string mode, GlassParams p, GlassParams shipped)
    {
        var plain = new GlassParams();
        sb.AppendLine($"() => new GlassParams  // {mode}");
        sb.AppendLine("{");
        foreach (var prop in typeof(GlassParams).GetProperties())
        {
            var v = prop.GetValue(p);
            if (Equals(v, prop.GetValue(plain))) continue;
            var text = v switch
            {
                bool b => b ? "true" : "false",
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                _ => v?.ToString() ?? "null"
            };
            var changed = Equals(v, prop.GetValue(shipped)) ? "" : "  // tuned";
            sb.AppendLine($"    {prop.Name} = {text},{changed}");
        }
        sb.AppendLine("},");
    }
}
