using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VideoConverterUI.Services;

namespace VideoConverterUI.Controls;

/// <summary>
/// Floating frosted-glass panel (macOS material). Children added in XAML sit on top of the material.
/// WPF has no backdrop blur for elements, so when <see cref="BackdropSource"/> is set the panel paints a
/// blurred live copy (VisualBrush) of that element, lined up with the part that sits behind the panel,
/// over an opaque base so the sharp content underneath doesn't show through. Without a source it is a
/// translucent material over the window's own acrylic backdrop.
/// </summary>
public class FrostedPanel : Grid
{
    private readonly Border _base = new() { IsHitTestVisible = false };
    private readonly Border _blurClip = new() { IsHitTestVisible = false };
    private readonly Rectangle _blurFill = new() { Margin = new Thickness(-30), IsHitTestVisible = false };
    private readonly Border _surface = new() { IsHitTestVisible = false };
    private readonly Border _tint = new() { IsHitTestVisible = false };
    private readonly Border _outline = new() { BorderThickness = new Thickness(0.5), IsHitTestVisible = false };
    private VisualBrush? _brush;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(FrostedPanel),
        new PropertyMetadata(new CornerRadius(14), (d, _) => ((FrostedPanel)d).ApplyCornerRadius()));

    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(UIElement), typeof(FrostedPanel),
        new PropertyMetadata(null, (d, _) => ((FrostedPanel)d).RebuildBackdrop()));

    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity), typeof(double), typeof(FrostedPanel),
        new PropertyMetadata(0.62, (d, e) => ((FrostedPanel)d)._tint.Opacity = (double)e.NewValue));

    public static readonly DependencyProperty ShadowOpacityProperty = DependencyProperty.Register(
        nameof(ShadowOpacity), typeof(double), typeof(FrostedPanel),
        new PropertyMetadata(0.15, (d, _) => ((FrostedPanel)d).ApplyShadow()));

    public static readonly DependencyProperty OpaqueBaseProperty = DependencyProperty.Register(
        nameof(OpaqueBase), typeof(bool), typeof(FrostedPanel),
        new PropertyMetadata(true, (d, _) => ((FrostedPanel)d).RebuildBackdrop()));

    /// <summary>
    /// Opaque window-colored layer under the blur that hides the sharp content behind the panel. Turn it off
    /// when the host clips that content away itself, so the panel shows the window's own translucent backdrop.
    /// </summary>
    public bool OpaqueBase
    {
        get => (bool)GetValue(OpaqueBaseProperty);
        set => SetValue(OpaqueBaseProperty, value);
    }

    /// <summary>Rounded corners of the panel.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>The element behind the panel whose content shows through, blurred. Must not contain the panel.</summary>
    public UIElement? BackdropSource
    {
        get => (UIElement?)GetValue(BackdropSourceProperty);
        set => SetValue(BackdropSourceProperty, value);
    }

    /// <summary>Strength of the material tint over the blur (0 = clear glass, 1 = solid).</summary>
    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    public double ShadowOpacity
    {
        get => (double)GetValue(ShadowOpacityProperty);
        set => SetValue(ShadowOpacityProperty, value);
    }

    public FrostedPanel()
    {
        _blurFill.Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        var blurHost = new Grid { IsHitTestVisible = false };
        blurHost.Children.Add(_blurFill);
        _blurClip.Child = blurHost;
        ClipHelper.SetCornerRadiusClip(_blurClip, true);

        _surface.SetResourceReference(Border.BackgroundProperty, "Tint06");
        _tint.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        _tint.Opacity = TintOpacity;
        _outline.SetResourceReference(Border.BorderBrushProperty, "Tint1A");

        // Layers go first so the children declared in XAML render on top of them.
        Children.Add(_base);
        Children.Add(_blurClip);
        Children.Add(_surface);
        Children.Add(_tint);
        Children.Add(_outline);
        foreach (UIElement layer in new UIElement[] { _base, _blurClip, _surface, _tint, _outline })
        {
            SetRowSpan(layer, 100);
            SetColumnSpan(layer, 100);
        }

        ApplyCornerRadius();
        ApplyShadow();
        RebuildBackdrop();

        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            ThemeService.ThemeApplied += UpdateBase;
            UpdateBase();
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            ThemeService.ThemeApplied -= UpdateBase;
        };
    }

    private void ApplyCornerRadius()
    {
        var r = CornerRadius;
        _base.CornerRadius = r;
        _blurClip.CornerRadius = r;
        _surface.CornerRadius = r;
        _tint.CornerRadius = r;
        _outline.CornerRadius = r;
    }

    private void ApplyShadow()
    {
        var shadow = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Color = Colors.Black, Opacity = ShadowOpacity };
        // The shadow follows the opaque base when there is one, otherwise the translucent tint.
        bool onBase = BackdropSource != null && OpaqueBase;
        _base.Effect = onBase ? shadow : null;
        _tint.Effect = onBase ? null : shadow;
    }

    private void RebuildBackdrop()
    {
        var source = BackdropSource;
        bool blur = source != null && !SettingsService.Current.EnablePerformanceMode;
        _brush = blur ? new VisualBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None } : null;
        _blurFill.Fill = _brush;
        _base.Visibility = source != null && OpaqueBase ? Visibility.Visible : Visibility.Collapsed;
        _blurClip.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;
        ApplyShadow();
        UpdateBase();
    }

    // Opaque copy of the window colour: hides the sharp content behind the panel.
    private void UpdateBase()
    {
        if (BackdropSource == null || !OpaqueBase) return;
        if (TryFindResource("BgDeep") is SolidColorBrush bg)
        {
            var opaque = new SolidColorBrush(Color.FromRgb(bg.Color.R, bg.Color.G, bg.Color.B));
            opaque.Freeze();
            _base.Background = opaque;
        }
    }

    // Keeps the blurred copy lined up with what is actually behind the panel (scrolling, resizing, layout changes).
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_brush == null || BackdropSource is not Visual source || _blurFill.ActualWidth <= 0) return;
        try
        {
            var area = _blurFill.TransformToVisual(source)
                .TransformBounds(new Rect(0, 0, _blurFill.ActualWidth, _blurFill.ActualHeight));
            if (_brush.Viewbox != area)
                _brush.Viewbox = area;
        }
        catch (InvalidOperationException)
        {
            // Not in the same visual tree (yet); try again on the next layout pass.
        }
    }
}
