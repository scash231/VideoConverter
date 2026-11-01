using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoConverterUI.Controls;

/// <summary>
/// Soft drop shadow drawn as a stack of translucent outlines, under its child if it has one. A DropShadowEffect on a
/// glass surface's parent sends the whole surface through an off-screen blur whenever anything on it changes, which
/// took more than half of the render thread's time per frame; these shapes are only drawn, with no off-screen pass.
/// A <see cref="LiquidGlassPanel"/> directly inside one sets its values from <see cref="Services.GlassTuning"/> and
/// keeps the outline on its own (corner radius, the backwards L).
/// </summary>
public sealed class GlassShadow : Decorator
{
    private const int MaxLayers = 14;
    private const double SigmaPerBlur = 0.31;
    private const double CornerSoftening = 1.0;

    // Solid black at every opacity a layer can use, in thousandths: a morphing pill redraws its layers each frame.
    private static readonly Dictionary<int, Brush> Brushes = new();

    private readonly DrawingGroup _drawing = new();

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassShadow), new PropertyMetadata(24.0, OnShadowChanged));

    public static readonly DependencyProperty ShadowDepthProperty = DependencyProperty.Register(
        nameof(ShadowDepth), typeof(double), typeof(GlassShadow), new PropertyMetadata(6.0, OnShadowChanged));

    public static readonly DependencyProperty ShadowOpacityProperty = DependencyProperty.Register(
        nameof(ShadowOpacity), typeof(double), typeof(GlassShadow), new PropertyMetadata(0.18, OnShadowChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassShadow), new PropertyMetadata(14.0, OnShadowChanged));

    public static readonly DependencyProperty ReverseLArmsProperty = DependencyProperty.Register(
        nameof(ReverseLArms), typeof(Size), typeof(GlassShadow), new PropertyMetadata(Size.Empty, OnShadowChanged));

    /// <summary>Same meaning as <see cref="System.Windows.Media.Effects.DropShadowEffect.BlurRadius"/>.</summary>
    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    /// <summary>How far (DIPs) the shadow falls below the shape.</summary>
    public double ShadowDepth
    {
        get => (double)GetValue(ShadowDepthProperty);
        set => SetValue(ShadowDepthProperty, value);
    }

    public double ShadowOpacity
    {
        get => (double)GetValue(ShadowOpacityProperty);
        set => SetValue(ShadowOpacityProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>See <see cref="LiquidGlassPanel.ReverseLArms"/>; empty = the rounded rectangle.</summary>
    public Size ReverseLArms
    {
        get => (Size)GetValue(ReverseLArmsProperty);
        set => SetValue(ReverseLArmsProperty, value);
    }

    private static void OnShadowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GlassShadow)d).Redraw();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawDrawing(_drawing);

    // The shadow takes no clicks; the child is hit-tested on its own.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;

    /// <summary>
    /// From the outermost layer in: each one covers a bit more of the shape and its surroundings and darkens them just
    /// enough that the layers so far add up to the blur's falloff at that distance. Measured against DropShadowEffect
    /// on the hardware renderer: half the strength at the edge, a Gaussian of <see cref="SigmaPerBlur"/> × BlurRadius.
    /// The shadow lies ShadowDepth lower than the shape, so on the lower side its inside shows too, down to that far in;
    /// further in the shape covers it. The DrawingGroup is rewritten in place, so a pill morphing frame by frame
    /// causes no layout.
    /// </summary>
    private void Redraw()
    {
        using var dc = _drawing.Open();
        double w = RenderSize.Width, h = RenderSize.Height, opacity = ShadowOpacity;
        if (w <= 0 || h <= 0 || opacity <= 0) return;

        double sigma = Math.Max(0.5, BlurRadius * SigmaPerBlur);
        double outer = sigma * 2.2;
        double span = outer + Math.Max(1, ShadowDepth);
        int layers = Math.Clamp((int)Math.Ceiling(span / 2), 2, MaxLayers);
        double step = span / layers;
        double covered = 0;
        for (int i = 0; i < layers; i++)
        {
            double outset = outer - i * step;
            double wanted = opacity * Falloff((outset - step / 2) / sigma);
            double alpha = 1 - (1 - wanted) / (1 - covered);
            covered = wanted;
            if (alpha > 0) DrawLayer(dc, w, h, outset, sigma, BrushFor(alpha));
        }
    }

    // The Gaussian's tail, close enough for a shadow: 0.5 at the edge, falling off outward and rising to 1 inward.
    private static double Falloff(double z) => 1 / (1 + Math.Exp(1.702 * z));

    // A blur thins out around a convex corner: the corners are rounder than the shape's by a share of its spread.
    private void DrawLayer(DrawingContext dc, double w, double h, double outset, double sigma, Brush brush)
    {
        double y = ShadowDepth;
        var arms = ReverseLArms;
        if (arms.IsEmpty || arms.Width <= 0 || arms.Height <= 0 || arms.Width >= w || arms.Height >= h)
        {
            double r = Math.Max(0, Math.Min(CornerRadius, Math.Min(w, h) / 2) + outset + sigma * CornerSoftening);
            dc.DrawRoundedRectangle(brush, null, new Rect(-outset, y - outset, Math.Max(0, w + 2 * outset), Math.Max(0, h + 2 * outset)), r, r);
            return;
        }

        double armRadius = Math.Max(0, Math.Min(CornerRadius, Math.Min(arms.Width, arms.Height) / 2) + outset + sigma * CornerSoftening);
        var l = new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(w - arms.Width - outset, y - outset, Math.Max(0, arms.Width + 2 * outset), Math.Max(0, h + 2 * outset)), armRadius, armRadius),
            new RectangleGeometry(new Rect(-outset, y + h - arms.Height - outset, Math.Max(0, w + 2 * outset), Math.Max(0, arms.Height + 2 * outset)), armRadius, armRadius));
        l.Freeze();
        dc.DrawGeometry(brush, null, l);
    }

    private static Brush BrushFor(double alpha)
    {
        int key = (int)Math.Round(alpha * 1000);
        if (Brushes.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(Colors.Black) { Opacity = key / 1000.0 };
        brush.Freeze();
        Brushes[key] = brush;
        return brush;
    }
}
