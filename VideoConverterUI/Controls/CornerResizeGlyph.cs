using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;

namespace VideoConverterUI.Controls;

/// <summary>
/// The iPadOS-style corner resize indicator inside a window's resize handle (<c>WindowCornerGrip</c>): a short,
/// thick arc with round caps, tucked into the window's rounded corner and following its curve. Pointing at the
/// handle brings up a soft round lens behind the arc; while the handle is dragged the lens swells a little and the
/// arc thickens. The handle (its parent) does the hit testing and resizing; this only draws and follows its state.
/// </summary>
public class CornerResizeGlyph : Grid
{
    /// <summary>The window's corner radius the arc follows. NaN (default) reads it from the window's RootBorder.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(CornerResizeGlyph),
        new PropertyMetadata(double.NaN, (d, _) => ((CornerResizeGlyph)d).Rebuild()));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private const double ArcInset = 5.5;     // gap between the window's edge and the arc
    private const double ArcFrom = 14, ArcTo = 76; // degrees of the corner's quarter circle the arc covers
    private const double ArcIdleThickness = 3.2, ArcPressedThickness = 4.2;

    private readonly Path _arc = new()
    {
        StrokeThickness = ArcIdleThickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Opacity = 0.55,
    };
    private readonly Ellipse _lens = new() { StrokeThickness = 0.5, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _lensScale = new(0.4, 0.4);
    private FrameworkElement? _handle;

    public CornerResizeGlyph()
    {
        IsHitTestVisible = false;
        _arc.SetResourceReference(Shape.StrokeProperty, "TextPrimary");
        _lens.SetResourceReference(Shape.FillProperty, "Tint14");
        _lens.SetResourceReference(Shape.StrokeProperty, "Tint1F");
        _lens.RenderTransform = _lensScale;
        foreach (var shape in new Shape[] { _lens, _arc })
        {
            shape.HorizontalAlignment = HorizontalAlignment.Left;
            shape.VerticalAlignment = VerticalAlignment.Top;
            Children.Add(shape);
        }

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => Rebuild();
    }

    private void Attach()
    {
        Detach();
        _handle = Parent as FrameworkElement;
        if (_handle != null)
        {
            _handle.MouseEnter += OnHandleChanged;
            _handle.MouseLeave += OnHandleChanged;
            _handle.GotMouseCapture += OnHandleChanged;
            _handle.LostMouseCapture += OnHandleChanged;
        }
        Rebuild();
        UpdateState(animate: false);
    }

    private void Detach()
    {
        if (_handle == null) return;
        _handle.MouseEnter -= OnHandleChanged;
        _handle.MouseLeave -= OnHandleChanged;
        _handle.GotMouseCapture -= OnHandleChanged;
        _handle.LostMouseCapture -= OnHandleChanged;
        _handle = null;
    }

    private void OnHandleChanged(object sender, RoutedEventArgs e) => UpdateState(animate: true);

    /// <summary>The corner radius to follow: the property, else the window's RootBorder (SquircleBorder or Border).</summary>
    private double ResolveRadius()
    {
        if (!double.IsNaN(CornerRadius)) return CornerRadius;
        return Window.GetWindow(this)?.FindName("RootBorder") switch
        {
            SquircleBorder squircle => squircle.CornerRadius,
            Border border => border.CornerRadius.BottomRight,
            _ => 16,
        };
    }

    /// <summary>
    /// Lays the arc and lens out around the centre of the window's corner circle, which sits one radius in from the
    /// handle's bottom-right corner (the handle sits in the window's bottom-right corner).
    /// </summary>
    private void Rebuild()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double radius = Math.Max(8, ResolveRadius());
        var centre = new Point(w - radius, h - radius);
        double r = Math.Max(4, radius - ArcInset);

        Point At(double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new Point(centre.X + r * Math.Cos(a), centre.Y + r * Math.Sin(a));
        }
        var figure = new PathFigure { StartPoint = At(ArcFrom), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(At(ArcTo), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();
        _arc.Data = geometry;

        // The lens stays inside the rounded corner, so the window's rounded edge never cuts it.
        double d = Math.Min(2 * (radius - 1.5), 34);
        _lens.Width = _lens.Height = d;
        _lens.Margin = new Thickness(centre.X - d / 2, centre.Y - d / 2, 0, 0);
    }

    private void UpdateState(bool animate)
    {
        bool pressed = _handle?.IsMouseCaptured == true;
        bool pointed = pressed || _handle?.IsMouseOver == true;

        double lensScale = pressed ? 1.08 : pointed ? 1 : 0.4;
        double lensOpacity = pointed ? 1 : 0;
        double arcOpacity = pressed ? 1 : pointed ? 0.85 : 0.55;
        double arcThickness = pressed ? ArcPressedThickness : ArcIdleThickness;

        if (!animate || FluidMotion.AnimationsDisabled || SettingsService.Current.EnablePerformanceMode)
        {
            _lensScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _lensScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _lens.BeginAnimation(OpacityProperty, null);
            _arc.BeginAnimation(OpacityProperty, null);
            _arc.BeginAnimation(Shape.StrokeThicknessProperty, null);
            _lensScale.ScaleX = _lensScale.ScaleY = lensScale;
            _lens.Opacity = lensOpacity;
            _arc.Opacity = arcOpacity;
            _arc.StrokeThickness = arcThickness;
            return;
        }

        var spring = pointed ? AppleSpringEase.Bouncy : AppleSpringEase.Smooth;
        var duration = TimeSpan.FromMilliseconds(pointed ? 420 : 260);
        _lensScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(lensScale, duration) { EasingFunction = spring });
        _lensScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(lensScale, duration) { EasingFunction = spring });
        _lens.BeginAnimation(OpacityProperty, new DoubleAnimation(lensOpacity, TimeSpan.FromMilliseconds(pointed ? 140 : 220)));
        _arc.BeginAnimation(OpacityProperty, new DoubleAnimation(arcOpacity, TimeSpan.FromMilliseconds(160)));
        _arc.BeginAnimation(Shape.StrokeThicknessProperty,
            new DoubleAnimation(arcThickness, TimeSpan.FromMilliseconds(220)) { EasingFunction = AppleSpringEase.Interactive });
    }
}
