using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;

namespace VideoConverterUI.Controls;

/// <summary>
/// glisse's button and input surface (github.com/0kzh/glisse, .lg-glass, .lg-button, .lg-input) for things that sit on glass
/// or in lists: a capsule of the glass colour with a 0.5 px edge and a rim lit towards the top-left and bottom-right. It lights
/// up at once when held and fades on release. Set <see cref="Fill"/> for the filled variants. No shader and no effect: one
/// shape, redrawn only while it changes, so a card or a long list can hold many. Standalone buttons over the window's
/// background use real Liquid Glass instead (see MainWindow's search row).
/// </summary>
public sealed class GlassChrome : Decorator
{
    private static readonly Spring HoverSpring = GlassPress.FromStiffness(420, 41);

    public static readonly DependencyProperty FillProperty = Renders<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty DimProperty = Renders(nameof(Dim), 0.0);
    public static readonly DependencyProperty CornerRadiusProperty = Renders(nameof(CornerRadius), double.NaN);
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(GlassChrome),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty IsHoveredProperty = Watched(nameof(IsHovered));
    public static readonly DependencyProperty IsHeldProperty = Watched(nameof(IsHeld));

    // The glass colours, written per mode by ThemeService.
    private static readonly DependencyProperty GlassFillProperty = Renders<Brush?>("GlassFill", null);
    private static readonly DependencyProperty GlassLitProperty = Renders<Brush?>("GlassLit", null);
    private static readonly DependencyProperty GlassEdgeProperty = Renders<Brush?>("GlassEdge", null);
    private static readonly DependencyProperty LitProperty = Renders("Lit", 0.0);

    private static DependencyProperty Renders<T>(string name, T value) => DependencyProperty.Register(name, typeof(T), typeof(GlassChrome),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Watched(string name) => DependencyProperty.Register(name, typeof(bool), typeof(GlassChrome),
        new PropertyMetadata(false, (d, _) => ((GlassChrome)d).UpdateLit()));

    /// <summary>The fill of a filled button; null = the glass colour.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>How much black lies over the <see cref="Fill"/>, for a fill quieter than its colour.</summary>
    public double Dim { get => (double)GetValue(DimProperty); set => SetValue(DimProperty, value); }
    /// <summary>NaN = a capsule, whatever the height.</summary>
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public bool IsHovered { get => (bool)GetValue(IsHoveredProperty); set => SetValue(IsHoveredProperty, value); }
    public bool IsHeld { get => (bool)GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }

    public GlassChrome()
    {
        SetResourceReference(GlassFillProperty, "GlassFill");
        SetResourceReference(GlassLitProperty, "GlassFillLit");
        SetResourceReference(GlassEdgeProperty, "GlassEdge");
    }

    private void UpdateLit()
    {
        double target = IsHeld ? 1 : IsHovered ? 0.7 : 0;
        if (IsHeld || !IsLoaded || FluidMotion.AnimationsDisabled)
        {
            BeginAnimation(LitProperty, null);  // lights up at once, as on iOS
            SetValue(LitProperty, target);
            return;
        }
        Liquid.To(this, LitProperty, target, HoverSpring, min: 0, max: 1);
    }

    protected override Size MeasureOverride(Size available)
    {
        var p = Padding;
        double px = p.Left + p.Right, py = p.Top + p.Bottom;
        if (Child == null) return new Size(px, py);
        Child.Measure(new Size(Math.Max(0, available.Width - px), Math.Max(0, available.Height - py)));
        return new Size(Child.DesiredSize.Width + px, Child.DesiredSize.Height + py);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top, Math.Max(0, final.Width - p.Left - p.Right), Math.Max(0, final.Height - p.Top - p.Bottom)));
        return final;
    }

    private static readonly Pen ClearPen = new(Brushes.Transparent, 0);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(double.IsNaN(CornerRadius) ? h / 2 : CornerRadius, Math.Min(w, h) / 2);
        var rect = new Rect(0, 0, w, h);
        double lit = (double)GetValue(LitProperty);
        bool light = ThemeService.IsLight;

        // glisse's 0 1px 3px drop, on a light page only: two soft outlines instead of a blur.
        if (light)
        {
            dc.DrawRoundedRectangle(Soft(0.025), null, new Rect(-1, 0, w + 2, h + 2), r + 1, r + 1);
            dc.DrawRoundedRectangle(Soft(0.035), null, new Rect(-0.5, 0.5, w + 1, h + 1), r + 0.5, r + 0.5);
        }

        if (Fill is { } fill)
        {
            dc.DrawRoundedRectangle(fill, null, rect, r, r);
            if (Dim > 0) dc.DrawRoundedRectangle(Soft(Dim), null, rect, r, r);
            // Lighter while hovered, darker while held, as glisse mixes its fill.
            bool held = IsHeld;
            if (lit > 0.001)
                dc.DrawRoundedRectangle(held ? Soft(0.15) : White(0.12 * lit / 0.7), null, rect, r, r);
        }
        else
        {
            dc.DrawRoundedRectangle((Brush?)GetValue(GlassFillProperty), null, rect, r, r);
            if (lit > 0.001)
            {
                dc.PushOpacity(lit);
                dc.DrawRoundedRectangle((Brush?)GetValue(GlassLitProperty), null, rect, r, r);
                dc.Pop();
            }
        }

        var inner = new Rect(0.25, 0.25, Math.Max(0, w - 0.5), Math.Max(0, h - 0.5));
        double ir = Math.Max(0, r - 0.25);
        if (Fill == null && GetValue(GlassEdgeProperty) is Brush edge)
            dc.DrawRoundedRectangle(null, EdgePen(edge), inner, ir, ir);
        dc.DrawRoundedRectangle(null, RimPen(light || Fill != null ? GlassParams.LightRimShine : GlassParams.DarkRimShine), inner, ir, ir);
    }

    private static readonly Dictionary<int, Brush> Blacks = [], Whites = [];
    private static Brush Soft(double alpha) => Tone(Blacks, Colors.Black, alpha);
    private static Brush White(double alpha) => Tone(Whites, Colors.White, alpha);

    private static Brush Tone(Dictionary<int, Brush> cache, Color color, double alpha)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        if (cache.TryGetValue(a, out var b)) return b;
        b = new SolidColorBrush(Color.FromArgb((byte)a, color.R, color.G, color.B));
        b.Freeze();
        return cache[a] = b;
    }

    private Pen? _edgePen;
    private Brush? _edgePenBrush;

    private Pen EdgePen(Brush brush)
    {
        if (_edgePen != null && ReferenceEquals(_edgePenBrush, brush)) return _edgePen;
        _edgePenBrush = brush;
        _edgePen = new Pen(brush, 0.5);
        _edgePen.Freeze();
        return _edgePen;
    }

    private static readonly Dictionary<double, Pen> RimPens = [];

    // Bright towards the top-left and the bottom-right, faint between (see LiquidGlassEffect's rim).
    private static Pen RimPen(double shine)
    {
        if (RimPens.TryGetValue(shine, out var pen)) return pen;
        Color At(double alpha) => Color.FromArgb((byte)(alpha * 255), 255, 255, 255);
        var brush = new LinearGradientBrush([
            new GradientStop(At(shine), 0),
            new GradientStop(At(shine * 0.12), 0.32),
            new GradientStop(At(shine * 0.12), 0.68),
            new GradientStop(At(shine * 0.8), 1),
        ], new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        pen = new Pen(brush, 0.75);
        pen.Freeze();
        return RimPens[shine] = pen;
    }
}
