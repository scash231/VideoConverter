using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoConverterUI.Services;

public class SquircleBorder : Decorator
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty =
        DependencyProperty.Register(
            nameof(BorderBrush),
            typeof(Brush),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(
            nameof(BorderThickness),
            typeof(Thickness),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                new Thickness(0),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush BorderBrush
    {
        get => (Brush)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double border = Math.Max(0, BorderThickness.Left);

        Rect rect = new Rect(
            border / 2,
            border / 2,
            Math.Max(0, ActualWidth - border),
            Math.Max(0, ActualHeight - border));

        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        double radius = Math.Max(
            0,
            Math.Min(
                CornerRadius,
                Math.Min(rect.Width, rect.Height) / 2));

        Pen? pen = null;

        if (BorderBrush != null && border > 0)
            pen = new Pen(BorderBrush, border);

        // No radius = normal rectangle
        if (radius <= 0.01)
        {
            dc.DrawRectangle(Background, pen, rect);
            return;
        }

        StreamGeometry geometry = CreateAppleStyleGeometry(rect, radius);

        dc.DrawGeometry(
            Background,
            pen,
            geometry);
    }

    internal static StreamGeometry CreateAppleStyleGeometry(
        Rect rect,
        double radius)
    {
        // Squircle corners, except when fully rounded: a circle or capsule like Apple's, not a rounded square.
        // LiquidGlassEffect's sdSquircle follows the same rule.
        double exponent = radius >= Math.Min(rect.Width, rect.Height) / 2 - 0.5 ? 2.0 : 4.0;
        const int segments = 32;

        StreamGeometry geometry = new StreamGeometry();

        using (StreamGeometryContext ctx = geometry.Open())
        {
            // Start at the top-left corner's horizontal tangent.
            Point start = new Point(
                rect.Left + radius,
                rect.Top);

            ctx.BeginFigure(
                start,
                isFilled: true,
                isClosed: true);

            // TOP EDGE
            ctx.LineTo(
                new Point(rect.Right - radius, rect.Top),
                true,
                false);

            // TOP-RIGHT CORNER
            AddSuperellipseCorner(
                ctx,
                rect.Right - radius,
                rect.Top + radius,
                radius,
                270,
                360,
                exponent,
                segments);

            // RIGHT EDGE
            ctx.LineTo(
                new Point(rect.Right, rect.Bottom - radius),
                true,
                false);

            // BOTTOM-RIGHT CORNER
            AddSuperellipseCorner(
                ctx,
                rect.Right - radius,
                rect.Bottom - radius,
                radius,
                0,
                90,
                exponent,
                segments);

            // BOTTOM EDGE
            ctx.LineTo(
                new Point(rect.Left + radius, rect.Bottom),
                true,
                false);

            // BOTTOM-LEFT CORNER
            AddSuperellipseCorner(
                ctx,
                rect.Left + radius,
                rect.Bottom - radius,
                radius,
                90,
                180,
                exponent,
                segments);

            // LEFT EDGE
            ctx.LineTo(
                new Point(rect.Left, rect.Top + radius),
                true,
                false);

            // TOP-LEFT CORNER
            AddSuperellipseCorner(
                ctx,
                rect.Left + radius,
                rect.Top + radius,
                radius,
                180,
                270,
                exponent,
                segments);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void AddSuperellipseCorner(
        StreamGeometryContext ctx,
        double centerX,
        double centerY,
        double radius,
        double startAngle,
        double endAngle,
        double exponent,
        int segments)
    {
        for (int i = 1; i <= segments; i++)
        {
            double t = i / (double)segments;

            double angle =
                startAngle +
                (endAngle - startAngle) * t;

            double radians = angle * Math.PI / 180.0;

            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // Superellipse:
            //
            // |x|^n + |y|^n = r^n
            //
            // n = 4 gives the characteristic "squircle"
            // shape rather than a circular rounded corner.

            double x =
                Math.Sign(cos) *
                Math.Pow(Math.Abs(cos), 2.0 / exponent) *
                radius;

            double y =
                Math.Sign(sin) *
                Math.Pow(Math.Abs(sin), 2.0 / exponent) *
                radius;

            ctx.LineTo(
                new Point(
                    centerX + x,
                    centerY + y),
                true,
                false);
        }
    }
}