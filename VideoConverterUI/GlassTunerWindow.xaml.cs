using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoConverterUI.Services;

namespace VideoConverterUI;

/// <summary>
/// Developer tool: a slider + number box for every <see cref="GlassParams"/> value of each Liquid Glass surface
/// (<see cref="GlassTuning.Surfaces"/>), per theme mode. Every change goes straight into <see cref="GlassTuning"/>,
/// which every open <see cref="Controls.LiquidGlassPanel"/> re-applies live. New surfaces appear here by themselves.
/// Opened from Settings → Developer or the console command <c>debug glass</c>.
/// </summary>
public partial class GlassTunerWindow : Window
{
    private static GlassTunerWindow? _instance;

    private sealed record Knob(string Name, string Help, double Min, double Max, double Step,
        Func<GlassParams, double> Get, Action<GlassParams, double> Set);

    private static readonly (string Group, Knob[] Knobs)[] Groups =
    [
        ("Liquid Glass shader", [
            new("Saturation", "Colour boost of what lies behind (1 = unchanged, Apple uses about 1.8).", 0, 3, 0.05, p => p.Saturation, (p, v) => p.Saturation = v),
            new("Brightness", "Multiplies the backdrop's brightness.", 0.5, 1.5, 0.01, p => p.Brightness, (p, v) => p.Brightness = v),
            new("Frost", "Blur radius (DIPs) of the flat middle; the rim stays sharp.", 0, 30, 0.5, p => p.Frost, (p, v) => p.Frost = v),
            new("Refraction", "How far (DIPs) beyond the edge the rim pulls the backdrop in from.", 0, 60, 1, p => p.Refraction, (p, v) => p.Refraction = v),
            new("Bevel", "Width (DIPs) of the curved, lensing rim.", 1, 40, 0.5, p => p.Bevel, (p, v) => p.Bevel = v),
            new("Dispersion", "Colour split (prism fringe) along the rim, as a fraction of the refraction.", 0, 1, 0.01, p => p.Dispersion, (p, v) => p.Dispersion = v),
            new("Highlight", "Light caught in the rim: the bright edge line and inner glow.", 0, 2, 0.01, p => p.Highlight, (p, v) => p.Highlight = v),
            new("Inset", "How far (DIPs) the glass layer reaches past the card. Always at least Refraction + 2 (and Reflection reach + 2).", 0, 100, 1, p => p.Inset, (p, v) => p.Inset = v),
            new("Reflection", "Colour beyond the edge mirrored into a soft band inside it (0 = off).", 0, 1, 0.01, p => p.Reflection, (p, v) => p.Reflection = v),
            new("Reflection reach", "How far (DIPs) beyond the edge the reflection looks.", 0, 96, 1, p => p.ReflectionReach, (p, v) => p.ReflectionReach = v),
            new("Reflection width", "Width (DIPs) of the band inside the edge the reflection shows in.", 1, 60, 0.5, p => p.ReflectionWidth, (p, v) => p.ReflectionWidth = v),
        ]),
        ("Layers", [
            new("Backdrop blur", "Gaussian blur under the shader (just softens; the shader does the frost).", 0, 40, 0.5, p => p.BackdropBlur, (p, v) => p.BackdropBlur = v),
            new("Tint alpha", "Material tint over the glass (0-255): white in light mode, graphite in dark mode.", 0, 255, 1, p => p.TintAlpha, (p, v) => p.TintAlpha = v),
            new("Sheen alpha", "White gradient over the top of the card (0-255).", 0, 255, 1, p => p.SheenAlpha, (p, v) => p.SheenAlpha = v),
            new("Corner radius", "Card corner radius (DIPs).", 0, 30, 0.5, p => p.CornerRadius, (p, v) => p.CornerRadius = v),
        ]),
        ("Frosted fallback (shader off / software rendering)", [
            new("Fallback blur", "Gaussian blur radius when the shader is off.", 0, 60, 0.5, p => p.FallbackBlur, (p, v) => p.FallbackBlur = v),
            new("Fallback tint", "Tint alpha (0-255) when the shader is off.", 0, 255, 1, p => p.FallbackTintAlpha, (p, v) => p.FallbackTintAlpha = v),
        ]),
        ("Shadow", [
            new("Shadow blur", "Drop shadow blur radius.", 0, 60, 0.5, p => p.ShadowBlur, (p, v) => p.ShadowBlur = v),
            new("Shadow depth", "Drop shadow offset (downward).", 0, 30, 0.5, p => p.ShadowDepth, (p, v) => p.ShadowDepth = v),
            new("Shadow opacity", "Drop shadow opacity.", 0, 1, 0.01, p => p.ShadowOpacity, (p, v) => p.ShadowOpacity = v),
        ]),
    ];

    private readonly List<(Knob Knob, Slider Slider, TextBox Box)> _rows = [];
    private CheckBox? _chkLiquid;
    private string _surface = GlassSurfaces.Notification;
    private bool _light;
    private bool _syncing;

    private GlassParams Params => GlassTuning.For(_surface, _light);

    public static void ShowTuner(Window? owner)
    {
        if (_instance != null)
        {
            _instance.Activate();
            return;
        }
        _instance = new GlassTunerWindow();
        if (owner is { IsVisible: true })
        {
            _instance.Owner = owner;
            _instance.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
    }

    public GlassTunerWindow()
    {
        InitializeComponent();
        BuildRows();
        foreach (var surface in GlassTuning.Surfaces)
            CmbSurface.Items.Add(surface);
        CmbSurface.SelectedItem = _surface;
        _light = ThemeService.IsLight;
        (_light ? RadLight : RadDark).IsChecked = true;
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.UpdateDarkMode(this);  // dark title bar in dark mode
    }

    private void BuildRows()
    {
        _chkLiquid = new CheckBox
        {
            Content = "Liquid Glass shader (off = frosted fallback)",
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "Turn off to compare with the frosted look software rendering gets."
        };
        _chkLiquid.SetResourceReference(ForegroundProperty, "TextPrimary");
        _chkLiquid.Click += (_, _) =>
        {
            if (_syncing) return;
            Params.Liquid = _chkLiquid.IsChecked == true;
            GlassTuning.NotifyChanged();
        };
        Rows.Children.Add(_chkLiquid);

        foreach (var (group, knobs) in Groups)
        {
            Rows.Children.Add(new TextBlock
            {
                Text = group,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 4)
            });
            foreach (var knob in knobs)
                Rows.Children.Add(BuildRow(knob));
        }
    }

    private FrameworkElement BuildRow(Knob knob)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3), ToolTip = knob.Help };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var label = new TextBlock { Text = knob.Name, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = knob.Min, Maximum = knob.Max, SmallChange = knob.Step, LargeChange = knob.Step * 10,
            TickFrequency = knob.Step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        var box = new TextBox { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 2, 4, 2) };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(box);

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            box.Text = Format(e.NewValue);
            knob.Set(Params, e.NewValue);
            GlassTuning.NotifyChanged();
        };

        // Typed values may go past the slider's range (the slider just pins at its end).
        void Commit()
        {
            if (!TryParse(box.Text, out var v))
            {
                box.Text = Format(knob.Get(Params));
                return;
            }
            v = Math.Max(0, v);
            knob.Set(Params, v);
            _syncing = true;
            slider.Value = Math.Clamp(v, knob.Min, knob.Max);
            _syncing = false;
            box.Text = Format(v);
            GlassTuning.NotifyChanged();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            box.SelectAll();
        };
        // Mouse wheel over the number box nudges by one step.
        box.PreviewMouseWheel += (_, e) =>
        {
            slider.Value = Math.Clamp(knob.Get(Params) + Math.Sign(e.Delta) * knob.Step, knob.Min, knob.Max);
            e.Handled = true;
        };

        _rows.Add((knob, slider, box));
        return grid;
    }

    private static string Format(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out double v)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Loads the edited mode's values into the controls.</summary>
    private void Refresh()
    {
        _syncing = true;
        var p = Params;
        _chkLiquid!.IsChecked = p.Liquid;
        foreach (var (knob, slider, box) in _rows)
        {
            var v = knob.Get(p);
            slider.Value = Math.Clamp(v, knob.Min, knob.Max);
            box.Text = Format(v);
        }
        _syncing = false;

        TxtModeHint.Text = _light == ThemeService.IsLight
            ? "This is the app's current mode, so the previews show these values."
            : $"The app is in {(ThemeService.IsLight ? "light" : "dark")} mode: these values only show once you switch it.";
    }

    private void Surface_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSurface.SelectedItem is not string surface) return;
        _surface = surface;
        TxtSurfaceInfo.Text = GlassTuning.Describe(surface);
        PreviewNotification.Visibility = surface == GlassSurfaces.Notification ? Visibility.Visible : Visibility.Collapsed;
        PreviewDropdown.Visibility = surface == GlassSurfaces.Dropdown ? Visibility.Visible : Visibility.Collapsed;
        PreviewOther.Visibility = surface is GlassSurfaces.Notification or GlassSurfaces.Dropdown ? Visibility.Collapsed : Visibility.Visible;
        if (_rows.Count > 0 && (RadDark.IsChecked == true || RadLight.IsChecked == true)) Refresh();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;  // during InitializeComponent
        _light = RadLight.IsChecked == true;
        Refresh();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        var type = ((CmbType.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Success" => NotificationType.Success,
            "Info" => NotificationType.Info,
            "Warning" => NotificationType.Warning,
            _ => NotificationType.Error
        };
        // Over the main window (not this one): that is where the real notifications appear.
        var main = Application.Current.MainWindow;
        NotificationWindow.Show("Glass preview: the quick brown fox jumps over the lazy dog.",
            main is { IsVisible: true } ? main : null, type, persistent: true);
        TxtStatus.Text = "Test notification shown. It stays until you close it.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        GlassTuning.Reset(_surface, _light);
        Refresh();
        TxtStatus.Text = $"{_surface}, {(_light ? "light" : "dark")} mode: back to the shipped values (not saved yet).";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(GlassTuning.ToCSharp(_surface));
            TxtStatus.Text = $"{_surface}: both modes copied as C# (GlassParams initializers for GlassTuning.Registry).";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GlassTuning.Save();
            TxtStatus.Text = "Saved to %APPDATA%\\VideoConverter\\glass.json. These values now load on every start.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (!GlassTuning.HasSavedFile)
        {
            TxtStatus.Text = "Nothing saved; the app already starts with the shipped values.";
            return;
        }
        GlassTuning.DeleteSaved();
        TxtStatus.Text = "glass.json removed. The next start uses the shipped values; the current values stay until then.";
    }
}
