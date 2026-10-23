using VideoConverterUI.Animations;
using VideoConverterUI.Models;
using VideoConverterUI.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoConverterUI;

/// <summary>
/// Appearance pane: Dark/Light, accent color, theme colors (beta), window glass, video colors,
/// layout, toolbar buttons and motion. Theme values live in theme.json (<see cref="ThemeService"/>),
/// the rest in settings.json; both apply and save on every change.
/// </summary>
public partial class SettingsWindow
{
    // Theme colors that can be edited, in display order.
    private static readonly string[] ColorKeys =
        { "BgDeep", "BgSurface", "BgCard", "BgElevated", "BorderSub", "Accent", "AccentAlt", "ButtonGrad", "TextPrimary", "TextSec" };

    // Accent choices: (name key, dark-mode color, light-mode color), like the macOS accent row.
    private static readonly (string Key, string Dark, string Light)[] Accents =
    {
        ("PrefAccentBlue",     "#0A84FF", "#007AFF"),
        ("PrefAccentPurple",   "#BF5AF2", "#AF52DE"),
        ("PrefAccentPink",     "#FF375F", "#FF2D55"),
        ("PrefAccentRed",      "#FF453A", "#FF3B30"),
        ("PrefAccentOrange",   "#FF9F0A", "#FF9500"),
        ("PrefAccentYellow",   "#FFD60A", "#FFCC00"),
        ("PrefAccentGreen",    "#30D158", "#34C759"),
        ("PrefAccentGraphite", "#98989D", "#8E8E93"),
    };

    private static readonly string[] DefaultPaletteColors =
    {
        "#0A84FF", "#64D2FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A",
        "#30D158", "#66D4CF", "#AC8E68", "#98989D",
        "#FFFFFF", "#F2F2F7", "#E5E5EA", "#D1D1D6", "#8E8E93", "#3A3A3C", "#2C2C2E", "#1C1C1E", "#000000",
    };

    private readonly Dictionary<string, (Border Row, Border Swatch, TextBlock Hex)> _colorRows = new();
    private readonly List<(Border Ring, bool Light)> _modeTiles = new();
    private readonly List<(Border Ring, string Dark, string Light)> _accentRings = new();
    private string _activeColorKey = "Accent";
    private bool _syncingEditor;
    private ThemeSettings? _pendingTheme;

    // ═══ Theme apply ═══

    /// <summary>Changes a copy of the current theme, applies it (coalesced per dispatcher pass) and saves it shortly after.</summary>
    private void CommitTheme(Action<ThemeSettings> change)
    {
        if (_loading || !IsLoaded) return; // sliders report coerced values while the window is built
        var theme = ThemeService.Current.Clone();
        change(theme);
        ThemeService.SaveDeferred(theme);
        HasChanges = true;

        bool queued = _pendingTheme != null;
        _pendingTheme = theme;
        if (queued) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var pending = _pendingTheme;
            _pendingTheme = null;
            if (pending != null) ApplyTheme(pending);
        });
    }

    private void ApplyTheme(ThemeSettings theme)
    {
        if (theme.AdaptiveThumbnailTheme && Owner is MainWindow main && main.LastDominantColor is Color dominant)
            ThemeService.Apply(MainWindow.CreateAdaptiveTheme(theme, dominant));
        else
            ThemeService.Apply(theme);
    }

    private void BindThemeSwitch(CheckBox box, Func<ThemeSettings, bool> get, Action<ThemeSettings, bool> set)
    {
        _reloaders.Add(() => box.IsChecked = get(ThemeService.Current));
        RoutedEventHandler handler = (_, _) =>
        {
            if (_loading) return;
            bool value = box.IsChecked == true;
            if (value != get(ThemeService.Current)) CommitTheme(t => set(t, value));
        };
        box.Checked += handler;
        box.Unchecked += handler;
    }

    private void BindThemeCombo(ComboBox combo, Func<ThemeSettings, string> get, Action<ThemeSettings, string> set)
    {
        _reloaders.Add(() => SelectComboByTag(combo, get(ThemeService.Current)));
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || (combo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
            if (tag != get(ThemeService.Current)) CommitTheme(t => set(t, tag));
        };
    }

    private void BindThemeSlider(Slider slider, TextBlock value, Func<double, string> format,
                                 Func<ThemeSettings, double> get, Action<ThemeSettings, double> set)
    {
        _reloaders.Add(() => { slider.Value = get(ThemeService.Current); value.Text = format(slider.Value); });
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (!_loading) CommitTheme(t => set(t, e.NewValue));
        };
    }

    // ═══ Liquid Glass ═══

    /// <summary>Near the middle counts as Default, so the slider can find its way back by hand.</summary>
    private static double SnapToDefault(double v) => Math.Abs(v - 0.5) < 0.04 ? 0.5 : v;

    private string LiquidGlassLabel(double v) => SnapToDefault(v) == 0.5
        ? L("PrefGlassDefault")
        : v < 0.5 ? L("PrefGlassClear") : L("PrefGlassTinted");

    private void OpenLiquidGlass_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneLiquidGlass, PaneAppearance, "PrefLiquidGlass");

    /// <summary>Liquid Glass → Text readability, pushed over the Liquid Glass page.</summary>
    private void OpenGlassReadability_Click(object sender, RoutedEventArgs e)
    {
        string parentTitle = TxtPaneTitle.Text;
        TxtPaneTitle.SetResourceReference(TextBlock.TextProperty, "PrefGlassReadability");
        PushSubPage(PaneGlassReadability, PaneLiquidGlass, parentTitle);
    }

    private string DimStrengthLabel(double v) => SnapToDefault(v) == 0.5
        ? L("PrefGlassDimStandard")
        : v < 0.5 ? L("PrefGlassDimSofter") : L("PrefGlassDimStronger");

    /// <summary>The Text readability row on the Liquid Glass page: Off, or how strong.</summary>
    private void UpdateGlassReadabilityValue() => TxtGlassReadabilityValue.Text =
        ChkGlassAutoDim.IsChecked == true ? DimStrengthLabel(SliderDimStrength.Value) : L("PrefGlassReadabilityOff");

    private void OpenColors_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneColors, PaneAppearance, "PrefColorsHeader");

    private void ResetLiquidGlass_Click(object sender, RoutedEventArgs e) => SliderLiquidGlass.Value = 0.5;

    // ═══ Bindings ═══

    private void BindAppearance()
    {
        _reloaders.Add(UpdateModeTiles);
        _reloaders.Add(UpdateAccentRings);
        _reloaders.Add(UpdateColorRows);
        _reloaders.Add(LoadColorEditor);

        // Glass
        BindThemeSlider(SliderGlassOpacity, TxtGlassOpacityPct, v => $"{v:0}%",
            t => Math.Clamp(AlphaOf(t.BgDeep) / 255.0 * 100.0, 50, 100),
            (t, v) => t.BgDeep = WithAlpha(t.BgDeep, (byte)Math.Round(v / 100.0 * 255.0)));
        BindThemeSwitch(ChkEnableBorders, t => t.EnableBorders, (t, v) => t.EnableBorders = v);

        // Liquid Glass: Clear ↔ Tinted (Appearance row shows the state, the sub-page has the slider)
        BindThemeSlider(SliderLiquidGlass, TxtLiquidGlassValue, LiquidGlassLabel,
            t => t.LiquidGlassTint, (t, v) => t.LiquidGlassTint = SnapToDefault(v));
        _reloaders.Add(() => TxtLiquidGlassState.Text = LiquidGlassLabel(SliderLiquidGlass.Value));
        SliderLiquidGlass.ValueChanged += (_, e) => TxtLiquidGlassState.Text = LiquidGlassLabel(e.NewValue);
        // Liquid Glass → Text readability (the sampling mode is in settings.json, bound with the rest in BindControls)
        BindThemeSwitch(ChkGlassAutoDim, t => t.LiquidGlassAutoDim, (t, v) => t.LiquidGlassAutoDim = v);
        BindThemeSlider(SliderDimStrength, TxtDimStrengthState, DimStrengthLabel,
            t => t.LiquidGlassDimStrength, (t, v) => t.LiquidGlassDimStrength = SnapToDefault(v));
        BindThemeSwitch(ChkGlassDimTextOnly, t => t.LiquidGlassDimTextOnly, (t, v) => t.LiquidGlassDimTextOnly = v);
        _reloaders.Add(UpdateGlassReadabilityValue);
        ChkGlassAutoDim.Checked += (_, _) => UpdateGlassReadabilityValue();
        ChkGlassAutoDim.Unchecked += (_, _) => UpdateGlassReadabilityValue();
        SliderDimStrength.ValueChanged += (_, _) => UpdateGlassReadabilityValue();

        // Video colors
        BindThemeSwitch(ChkAdaptiveTheme, t => t.AdaptiveThumbnailTheme, (t, v) => t.AdaptiveThumbnailTheme = v);
        BindThemeCombo(CmbGradEffectMode, t => t.GradientEffectMode ?? "both", (t, v) => t.GradientEffectMode = v);

        // Layout (settings.json)
        BindSwitch(ChkShowIconLabels, s => s.ShowIconLabels, (s, v) => s.ShowIconLabels = v);
        BindSwitch(ChkHideScrollbars, s => s.HideScrollbars, (s, v) => s.HideScrollbars = v);
        BindSwitch(ChkSmoothScrolling, s => s.SmoothScrolling, (s, v) => s.SmoothScrolling = v);
        BindCombo(CmbAutoProgressMode, s => s.AutoProgressMode ?? "show_progress", (s, v) => s.AutoProgressMode = v);
        BindSwitch(ChkPillHelp, s => s.ShowHelpPill, (s, v) => s.ShowHelpPill = v);
        BindSwitch(ChkPillDev, s => s.ShowDevPill, (s, v) => s.ShowDevPill = v);
        BindSwitch(ChkPillEditor, s => s.ShowEditorPill, (s, v) => s.ShowEditorPill = v);
        BindSwitch(ChkPillLang, s => s.ShowLangPill, (s, v) => s.ShowLangPill = v);

        // Motion
        _reloaders.Add(() => SelectComboByTag(CmbAnimPreset, ThemeService.Current.AnimationPreset ?? Liquid.Name));
        CmbAnimPreset.SelectionChanged += (_, _) =>
        {
            if (_loading || (CmbAnimPreset.SelectedItem as ComboBoxItem)?.Tag is not string preset) return;
            if (preset == ThemeService.Current.AnimationPreset) return;
            CommitTheme(t => ApplyMotionProfile(t, preset));
            ReloadAll(); // the profile changes the rows below
        };
        BindThemeSwitch(ChkHoverMicro, t => t.EnableHoverMicroAnims, (t, v) => t.EnableHoverMicroAnims = v);
        BindThemeSlider(SliderHoverScale, TxtHoverScaleVal, v => $"{v:0.00}×",
            t => t.ButtonHoverScale > 0 ? t.ButtonHoverScale : 1.03, (t, v) => t.ButtonHoverScale = v);
        BindThemeSlider(SliderWinDur, TxtWinDurVal, v => $"{v:0} ms",
            t => t.WindowAnimDuration > 0 ? t.WindowAnimDuration : 400, (t, v) => t.WindowAnimDuration = (int)Math.Round(v));
        BindThemeSwitch(ChkStaggered, t => t.EnableStaggeredAnimations, (t, v) => t.EnableStaggeredAnimations = v);
        BindThemeSwitch(ChkProgPulse, t => t.EnableProgressPulse, (t, v) => t.EnableProgressPulse = v);

        // Color editor inputs
        TxtHexInput.LostKeyboardFocus += (_, _) => ApplyHexInput();
        TxtHexInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyHexInput(); e.Handled = true; } };
        foreach (var box in new[] { TxtR, TxtG, TxtB })
        {
            box.LostKeyboardFocus += (_, _) => ApplyRgbInput();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyRgbInput(); e.Handled = true; } };
        }

        BuildModeTiles();
        BuildAccentRings();
        BuildColorRows();
        BuildPalette();

        // Live previews of the sub-pages (SettingsWindow.Previews.cs); after the bindings above, so they see the saved values.
        BindPreviews();
    }

    /// <summary>The motion profiles set several animation options at once (same values as before).</summary>
    private static void ApplyMotionProfile(ThemeSettings t, string preset)
    {
        t.AnimationPreset = preset;
        t.AnimationLevel = preset;
        (t.WindowAnimDuration, t.ButtonHoverScale, t.ProgressBarAnimSpeed, t.EnableHoverMicroAnims, t.EnableProgressPulse, t.EnableStaggeredAnimations) = preset switch
        {
            "Disabled (Static)" => (100, 1.00, 100, false, false, false),
            "Subtle"            => (200, 1.015, 250, true, false, true),
            "Balanced"          => (300, 1.03, 350, true, true, true),
            Liquid.Name         => (400, 1.04, 350, true, true, true),
            _                   => (380, 1.03, 350, true, true, true),
        };
        // Liquid opens windows its own way; the other profiles go back to Morph.
        if (preset == Liquid.Name)
        {
            t.WindowAnimStyle = Liquid.Name;
            t.WindowAnimEasing = "Apple Spring";
        }
        else if (t.WindowAnimStyle == Liquid.Name)
        {
            t.WindowAnimStyle = "Morph & Scale";
        }
    }

    // ═══ Dark / Light tiles ═══

    private void BuildModeTiles()
    {
        ModeTiles.Children.Clear();
        _modeTiles.Clear();
        ModeTiles.Children.Add(CreateModeTile(ThemeService.LightMode, light: true, "PrefLight"));
        ModeTiles.Children.Add(CreateModeTile(ThemeService.DarkMode, light: false, "PrefDark"));
    }

    private FrameworkElement CreateModeTile(ThemeSettings mode, bool light, string labelKey)
    {
        static Brush Fill(string hex)
        {
            if (!ThemeService.TryParseColor(hex, out var c)) return Brushes.Gray;
            c.A = 255;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // A tiny window in the mode's own colors: sidebar, two cards and an accent pill.
        var window = new Grid();
        window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        window.ColumnDefinitions.Add(new ColumnDefinition());
        window.Children.Add(new Border { Background = Fill(mode.BgSurface), CornerRadius = new CornerRadius(5, 0, 0, 5) });
        var body = new StackPanel { Margin = new Thickness(5, 6, 5, 0) };
        body.Children.Add(new Border { Height = 7, CornerRadius = new CornerRadius(2), Background = Fill(mode.BgCard), Margin = new Thickness(0, 0, 0, 3) });
        body.Children.Add(new Border { Height = 7, CornerRadius = new CornerRadius(2), Background = Fill(mode.BgCard), Margin = new Thickness(0, 0, 0, 5) });
        body.Children.Add(new Border { Height = 5, Width = 20, CornerRadius = new CornerRadius(2.5), Background = Fill(mode.Accent), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(body, 1);
        window.Children.Add(body);

        var preview = new Border
        {
            Width = 72, Height = 46, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = Fill(mode.BgDeep),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0.5),
            Child = window,
        };
        var ring = new Border
        {
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2.5), Padding = new Thickness(1.5),
            BorderBrush = Brushes.Transparent, Child = preview,
        };
        var label = new TextBlock { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var tile = new StackPanel { Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        tile.Children.Add(ring);
        tile.Children.Add(label);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (ThemeService.IsLightTheme(ThemeService.Current) == light) return;
            // Only the colors change; effects, layout and motion stay as they are.
            var withMode = ThemeService.WithMode(ThemeService.Current, light);
            CommitTheme(t => CopyColors(withMode, t));
            ReloadAll();
        };
        _modeTiles.Add((ring, light));
        return tile;
    }

    private void UpdateModeTiles()
    {
        bool light = ThemeService.IsLightTheme(ThemeService.Current);
        foreach (var (ring, isLight) in _modeTiles)
        {
            if (isLight == light) ring.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else ring.BorderBrush = Brushes.Transparent;
        }
    }

    private static void CopyColors(ThemeSettings from, ThemeSettings to)
    {
        to.PresetName = from.PresetName;
        foreach (var key in ColorKeys.Append("SuccessGreen").Append("ErrorRed"))
            SetColor(to, key, GetColor(from, key));
    }

    // ═══ Accent row ═══

    private void BuildAccentRings()
    {
        AccentSwatches.Children.Clear();
        _accentRings.Clear();
        foreach (var (key, dark, light) in Accents)
        {
            ThemeService.TryParseColor(dark, out var c);
            var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(c) };
            var ring = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand,
                Background = Brushes.Transparent, Child = dot,
            };
            ring.SetResourceReference(ToolTipProperty, key);
            ring.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var hex = ThemeService.IsLightTheme(ThemeService.Current) ? light : dark;
                CommitTheme(t => t.Accent = hex);
                ReloadAll();
            };
            _accentRings.Add((ring, dark, light));
            AccentSwatches.Children.Add(ring);
        }
    }

    private void UpdateAccentRings()
    {
        var accent = ThemeService.Current.Accent;
        foreach (var (ring, dark, light) in _accentRings)
        {
            bool active = string.Equals(accent, dark, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(accent, light, StringComparison.OrdinalIgnoreCase);
            if (active) ring.SetResourceReference(Border.BorderBrushProperty, "Tint40");
            else ring.BorderBrush = Brushes.Transparent;
        }
    }

    // ═══ Color rows ═══

    private void BuildColorRows()
    {
        ColorRows.Children.Clear();
        _colorRows.Clear();
        for (int i = 0; i < ColorKeys.Length; i++)
        {
            var key = ColorKeys[i];
            if (i > 0) ColorRows.Children.Add(new Border { Style = (Style)FindResource("Sep") });

            var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Tint26");
            DockPanel.SetDock(swatch, Dock.Right);
            var hex = new TextBlock { Style = (Style)FindResource("Value"), FontSize = 12, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(hex, Dock.Right);
            var label = new TextBlock { Style = (Style)FindResource("Label") };
            label.SetResourceReference(TextBlock.TextProperty, "PrefColor" + key);

            var dock = new DockPanel { MinHeight = 36, Margin = new Thickness(12, 0, 12, 0) };
            dock.Children.Add(swatch);
            dock.Children.Add(hex);
            dock.Children.Add(label);

            // The highlight follows the group's rounded corners on the first and last row.
            var corners = i == 0 ? new CornerRadius(10, 10, 0, 0) : i == ColorKeys.Length - 1 ? new CornerRadius(0, 0, 10, 10) : new CornerRadius(0);
            var row = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dock, CornerRadius = corners };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _activeColorKey = key;
                UpdateColorRows();
                LoadColorEditor();
            };
            _colorRows[key] = (row, swatch, hex);
            ColorRows.Children.Add(row);
        }
    }

    private void UpdateColorRows()
    {
        foreach (var (key, (row, swatch, hexText)) in _colorRows)
        {
            var hex = GetColor(ThemeService.Current, key);
            if (ThemeService.TryParseColor(hex, out var c)) swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            hexText.Text = hex.ToUpperInvariant();
            if (key == _activeColorKey) row.SetResourceReference(Border.BackgroundProperty, "Tint0D");
            else row.Background = Brushes.Transparent;
        }
    }

    // ═══ Color editor ═══

    private void LoadColorEditor()
    {
        var hex = GetColor(ThemeService.Current, _activeColorKey);
        if (!ThemeService.TryParseColor(hex, out var c)) return;
        TxtActiveLabel.SetResourceReference(TextBlock.TextProperty, "PrefColor" + _activeColorKey);
        ShowColorInEditor(c, hex);
    }

    private void ShowColorInEditor(Color c, string hex)
    {
        _syncingEditor = true;
        try
        {
            ActiveSwatch.Background = new SolidColorBrush(c);
            RgbToHsv(c.R, c.G, c.B, out var h, out var s, out var v);
            SliderHue.Value = h;
            SliderSat.Value = s * 100;
            SliderVal.Value = v * 100;
            SliderAlpha.Value = Math.Clamp(c.A / 255.0 * 100, 50, 100);
            TxtHexInput.Text = hex.ToUpperInvariant();
            TxtR.Text = c.R.ToString(CultureInfo.InvariantCulture);
            TxtG.Text = c.G.ToString(CultureInfo.InvariantCulture);
            TxtB.Text = c.B.ToString(CultureInfo.InvariantCulture);
            UpdateEditorLabels();
            UpdateSliderTracks(h, s, v);
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void UpdateEditorLabels()
    {
        TxtHueVal.Text = $"{SliderHue.Value:0}°";
        TxtSatVal.Text = $"{SliderSat.Value:0}%";
        TxtValVal.Text = $"{SliderVal.Value:0}%";
        TxtAlphaVal.Text = $"{SliderAlpha.Value:0}%";
    }

    private void SliderHsv_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return; // Minimum/Maximum coerce the value while the XAML loads
        UpdateEditorLabels();
        if (_syncingEditor || _loading) return;

        double h = SliderHue.Value, s = SliderSat.Value / 100, v = SliderVal.Value / 100;
        HsvToRgb(h, s, v, out var r, out var g, out var b);
        byte a = (byte)Math.Round(Math.Clamp(SliderAlpha.Value, 50, 100) / 100 * 255);
        UpdateSliderTracks(h, s, v);
        SetActiveColor(Color.FromArgb(a, r, g, b), fromEditor: true);
    }

    private void ApplyHexInput()
    {
        if (_loading || !ThemeService.TryParseColor(TxtHexInput.Text.Trim(), out var c)) { LoadColorEditor(); return; }
        SetActiveColor(c, fromEditor: false);
    }

    private void ApplyRgbInput()
    {
        if (_loading) return;
        if (!byte.TryParse(TxtR.Text, out var r) || !byte.TryParse(TxtG.Text, out var g) || !byte.TryParse(TxtB.Text, out var b))
        {
            LoadColorEditor();
            return;
        }
        ThemeService.TryParseColor(GetColor(ThemeService.Current, _activeColorKey), out var current);
        SetActiveColor(Color.FromArgb(current.A, r, g, b), fromEditor: false);
    }

    /// <summary>Writes a color to the selected theme key. Fully opaque colors are stored as #RRGGBB.</summary>
    private void SetActiveColor(Color c, bool fromEditor)
    {
        var hex = c.A < 255 ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var key = _activeColorKey;
        CommitTheme(t => SetColor(t, key, hex));

        UpdateColorRows();
        if (fromEditor)
        {
            // Keep the sliders where the user drags them; only refresh the text fields.
            _syncingEditor = true;
            ActiveSwatch.Background = new SolidColorBrush(c);
            TxtHexInput.Text = hex;
            TxtR.Text = c.R.ToString(CultureInfo.InvariantCulture);
            TxtG.Text = c.G.ToString(CultureInfo.InvariantCulture);
            TxtB.Text = c.B.ToString(CultureInfo.InvariantCulture);
            _syncingEditor = false;
        }
        else
        {
            ShowColorInEditor(c, hex);
        }

        if (key == "BgDeep")
        {
            _loading = true;
            SliderGlassOpacity.Value = Math.Clamp(c.A / 255.0 * 100, 50, 100);
            TxtGlassOpacityPct.Text = $"{SliderGlassOpacity.Value:0}%";
            _loading = false;
        }
        if (key == "Accent") UpdateAccentRings();
    }

    private void UpdateSliderTracks(double h, double s, double v)
    {
        HsvToRgb(h, 0, v, out var r0, out var g0, out var b0);
        HsvToRgb(h, 1, v, out var r1, out var g1, out var b1);
        SliderSat.Background = new LinearGradientBrush(Color.FromRgb(r0, g0, b0), Color.FromRgb(r1, g1, b1), 0);
        HsvToRgb(h, s, 0, out r0, out g0, out b0);
        HsvToRgb(h, s, 1, out r1, out g1, out b1);
        SliderVal.Background = new LinearGradientBrush(Color.FromRgb(r0, g0, b0), Color.FromRgb(r1, g1, b1), 0);
        HsvToRgb(h, s, v, out var r, out var g, out var b);
        SliderAlpha.Background = new LinearGradientBrush(Color.FromArgb(128, r, g, b), Color.FromArgb(255, r, g, b), 0);
    }

    // ═══ Palette ═══

    private void BuildPalette()
    {
        PaletteGridPanel.Children.Clear();
        var saved = SettingsService.Current.CustomPaletteColors ?? new List<string>();
        var colors = saved.Where(h => ThemeService.TryParseColor(h, out _)).Select(h => (Hex: h, Saved: true))
            .Concat(DefaultPaletteColors.Select(h => (Hex: h, Saved: false)))
            .DistinctBy(x => x.Hex.ToUpperInvariant())
            .ToList();

        foreach (var (hex, isSaved) in colors)
        {
            ThemeService.TryParseColor(hex, out var c);
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = isSaved ? string.Format(L("PrefPaletteSavedTip"), hex) : hex,
            };
            dot.SetResourceReference(Border.BorderBrushProperty, isSaved ? "Accent" : "Tint1F");
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ThemeService.TryParseColor(GetColor(ThemeService.Current, _activeColorKey), out var current);
                SetActiveColor(Color.FromArgb(current.A == 0 ? (byte)255 : current.A, c.R, c.G, c.B), fromEditor: false);
            };
            if (isSaved)
            {
                dot.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    Commit(s => s.CustomPaletteColors = (s.CustomPaletteColors ?? new List<string>())
                        .Where(x => !x.Equals(hex, StringComparison.OrdinalIgnoreCase)).ToList());
                    BuildPalette();
                };
            }
            PaletteGridPanel.Children.Add(dot);
        }

        // "+" saves the color being edited to the palette.
        var add = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Child = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Add12, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        add.SetResourceReference(Border.BorderBrushProperty, "Tint26");
        ((Wpf.Ui.Controls.SymbolIcon)add.Child).SetResourceReference(Control.ForegroundProperty, "TextSec");
        add.SetResourceReference(ToolTipProperty, "PrefPaletteAdd");
        add.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var hex = GetColor(ThemeService.Current, _activeColorKey);
            if (!ThemeService.TryParseColor(hex, out var c)) return;
            var rgb = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if ((SettingsService.Current.CustomPaletteColors ?? new List<string>()).Any(x => x.Equals(rgb, StringComparison.OrdinalIgnoreCase))) return;
            Commit(s => s.CustomPaletteColors = new[] { rgb }.Concat(s.CustomPaletteColors ?? new List<string>()).ToList());
            BuildPalette();
        };
        PaletteGridPanel.Children.Add(add);
    }

    private void RestoreColors_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ThemeService.IsLightTheme(ThemeService.Current) ? ThemeService.LightMode : ThemeService.DarkMode;
        CommitTheme(t => CopyColors(defaults, t));
        ReloadAll();
    }

    // ═══ Theme color access ═══

    private static string GetColor(ThemeSettings t, string key) => key switch
    {
        "BgDeep" => t.BgDeep,
        "BgSurface" => t.BgSurface,
        "BgCard" => t.BgCard,
        "BgElevated" => t.BgElevated,
        "BorderSub" => t.BorderSub,
        "Accent" => t.Accent,
        "AccentAlt" => t.AccentAlt,
        "ButtonGrad" => t.ButtonGrad,
        "TextPrimary" => t.TextPrimary,
        "TextSec" => t.TextSec,
        "SuccessGreen" => t.SuccessGreen,
        "ErrorRed" => t.ErrorRed,
        _ => "#000000",
    };

    private static void SetColor(ThemeSettings t, string key, string hex)
    {
        switch (key)
        {
            case "BgDeep": t.BgDeep = hex; break;
            case "BgSurface": t.BgSurface = hex; break;
            case "BgCard": t.BgCard = hex; break;
            case "BgElevated": t.BgElevated = hex; break;
            case "BorderSub": t.BorderSub = hex; break;
            case "Accent": t.Accent = hex; break;
            case "AccentAlt": t.AccentAlt = hex; break;
            case "ButtonGrad": t.ButtonGrad = hex; break;
            case "TextPrimary": t.TextPrimary = hex; break;
            case "TextSec": t.TextSec = hex; break;
            case "SuccessGreen": t.SuccessGreen = hex; break;
            case "ErrorRed": t.ErrorRed = hex; break;
        }
    }

    private static byte AlphaOf(string hex) => ThemeService.TryParseColor(hex, out var c) ? c.A : (byte)255;

    private static string WithAlpha(string hex, byte alpha)
    {
        if (!ThemeService.TryParseColor(hex, out var c)) return hex;
        return alpha >= 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        double r1, g1, b1;

        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        r = (byte)Math.Clamp((r1 + m) * 255 + 0.5, 0, 255);
        g = (byte)Math.Clamp((g1 + m) * 255 + 0.5, 0, 255);
        b = (byte)Math.Clamp((b1 + m) * 255 + 0.5, 0, 255);
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0) h = 0;
        else if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
        else if (max == gd) h = 60 * ((bd - rd) / delta + 2);
        else h = 60 * ((rd - gd) / delta + 4);

        if (h < 0) h += 360;
    }
}
