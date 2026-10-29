using System.Text.Json.Serialization;

namespace VideoConverterUI.Models;

public sealed class ThemeSettings
{
    [JsonPropertyName("bg_deep")]
    public string BgDeep { get; set; } = "#FF1C1C1E";

    [JsonPropertyName("bg_surface")]
    public string BgSurface { get; set; } = "#232325";

    [JsonPropertyName("bg_card")]
    public string BgCard { get; set; } = "#2C2C2E";

    [JsonPropertyName("bg_elevated")]
    public string BgElevated { get; set; } = "#3A3A3C";

    [JsonPropertyName("border_sub")]
    public string BorderSub { get; set; } = "#38383A";

    [JsonPropertyName("accent")]
    public string Accent { get; set; } = "#0A84FF";

    [JsonPropertyName("accent_alt")]
    public string AccentAlt { get; set; } = "#64D2FF";

    [JsonPropertyName("text_primary")]
    public string TextPrimary { get; set; } = "#F5F5F7";

    [JsonPropertyName("text_sec")]
    public string TextSec { get; set; } = "#98989D";

    [JsonPropertyName("success")]
    public string SuccessGreen { get; set; } = "#30D158";

    [JsonPropertyName("error")]
    public string ErrorRed { get; set; } = "#FF453A";

    [JsonPropertyName("button_grad")]
    public string ButtonGrad { get; set; } = "#3A3A3C";

    [JsonPropertyName("adaptive_thumbnail_theme")]
    public bool AdaptiveThumbnailTheme { get; set; } = false;

    [JsonPropertyName("animation_level")]
    public string AnimationLevel { get; set; } = "Liquid";

    [JsonPropertyName("preset_name")]
    public string PresetName { get; set; } = "Dark";

    [JsonPropertyName("enable_borders")]
    public bool EnableBorders { get; set; } = true;

    [JsonPropertyName("gradient_effect_mode")]
    public string GradientEffectMode { get; set; } = "none";

    [JsonPropertyName("bg_gradient_strength")]
    public double BgGradientStrength { get; set; } = 1.0;

    [JsonPropertyName("enable_bg_gradient")]
    public bool EnableBgGradient { get; set; } = true;

    [JsonPropertyName("enable_thumbnail_gradient")]
    public bool EnableThumbnailGradient { get; set; } = true;

    [JsonPropertyName("thumbnail_gradient_only")]
    public bool ThumbnailGradientOnly { get; set; } = false;

    [JsonPropertyName("disable_thumbnail_card")]
    public bool DisableThumbnailCard { get; set; } = false;

    /// <summary>Motion profile (Settings → Animations): "Liquid" (Animations/LiquidMotion.cs), "Smooth Liquid" (shown as
    /// Fluid), "Balanced", "Subtle" or "Disabled (Static)".</summary>
    [JsonPropertyName("animation_preset")]
    public string AnimationPreset { get; set; } = "Liquid";

    /// <summary>Window opening speed in ms; for the Liquid style, the spring's response.</summary>
    [JsonPropertyName("window_anim_duration")]
    public int WindowAnimDuration { get; set; } = 301;

    [JsonPropertyName("window_anim_easing")]
    public string WindowAnimEasing { get; set; } = "Apple Spring";

    [JsonPropertyName("window_anim_style")]
    public string WindowAnimStyle { get; set; } = "Liquid";

    [JsonPropertyName("button_hover_scale")]
    public double ButtonHoverScale { get; set; } = 1.04;

    [JsonPropertyName("enable_hover_micro_anims")]
    public bool EnableHoverMicroAnims { get; set; } = false;

    [JsonPropertyName("progress_bar_anim_speed")]
    public int ProgressBarAnimSpeed { get; set; } = 350;

    [JsonPropertyName("enable_progress_pulse")]
    public bool EnableProgressPulse { get; set; } = true;

    [JsonPropertyName("dropdown_anim_style")]
    public string DropdownAnimStyle { get; set; } = "Scale & Fade";

    [JsonPropertyName("tab_switch_transition")]
    public string TabSwitchTransition { get; set; } = "Slide & Fade";

    [JsonPropertyName("enable_staggered_animations")]
    public bool EnableStaggeredAnimations { get; set; } = true;

    /// <summary>Liquid Glass look, Settings → Appearance → Liquid Glass: 0 = clear, 0.5 = as tuned (the default), 1 = tinted.</summary>
    [JsonPropertyName("liquid_glass_tint")]
    public double LiquidGlassTint { get; set; } = 0.5;

    /// <summary>Liquid Glass dims (or, in light mode, lightens) itself just where a bright or busy backdrop would make
    /// the text on it hard to read (LiquidGlassPanel.AdaptiveContrast). Settings → Appearance → Liquid Glass.</summary>
    [JsonPropertyName("liquid_glass_auto_dim")]
    public bool LiquidGlassAutoDim { get; set; } = true;

    /// <summary>How strongly the glass dims behind text: 0 = softer, 0.5 = standard, 1 = stronger (also dims spots the
    /// standard leaves clear). Settings → Appearance → Liquid Glass → Text readability.</summary>
    [JsonPropertyName("liquid_glass_dim_strength")]
    public double LiquidGlassDimStrength { get; set; } = 0.42;

    /// <summary>Dim only the spots of the glass where text sits on it (and leave empty parts of the glass clear),
    /// instead of everywhere the background is too bright or busy. Settings → Appearance → Liquid Glass → Text readability.</summary>
    [JsonPropertyName("liquid_glass_dim_text_only")]
    public bool LiquidGlassDimTextOnly { get; set; } = true;

    /// <summary>Instead of dimming the glass, text on it turns dark over a bright backdrop (light over a dark one in
    /// light mode) wherever its usual colour would be hard to read. Settings → Appearance → Liquid Glass → Text
    /// readability → Text Color.</summary>
    [JsonPropertyName("liquid_glass_adaptive_text")]
    public bool LiquidGlassAdaptiveText { get; set; }

    public ThemeSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(json) ?? new ThemeSettings();
    }
}
