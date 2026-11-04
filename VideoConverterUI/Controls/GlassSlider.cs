using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoConverterUI.Animations;

namespace VideoConverterUI.Controls;

/// <summary>
/// glisse's slider (github.com/0kzh/glisse, src/slider.tsx), everything inside a Slider's template. All sizes are glisse's
/// times <see cref="Scale"/>. A thin rail with the fill ending under the middle of the thumb; at rest the thumb is a white
/// capsule. Held, it lifts into clear glass, a lens that shows the rail thicker, as if magnified, bent at its rim. The
/// Slider stays the control (Value, Minimum, Maximum, ticks, keyboard, ValueChanged); the mouse is handled here, so a press
/// anywhere on the rail moves the thumb and drags it right away. A Slider with a Background of its own (the colour editor's
/// gradients) shows that as its rail and has no fill. The lens exists only while lifted, so a resting slider costs no effect.
/// </summary>
public sealed class GlassSlider : Grid
{
    private static readonly Spring LiftSpring = GlassPress.FromStiffness(2000, 80);

    private const double RestScale = 0.6, ThumbWidthUnits = 60, ThumbHeightUnits = 40, HitWidthUnits = 36;
    private const double RailUnits = 6, LensLineUnits = 8;

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double),
        typeof(GlassSlider), new PropertyMetadata(0.72, (d, _) => ((GlassSlider)d).ApplySize()));

    public static readonly DependencyProperty RailBrushProperty = DependencyProperty.Register(nameof(RailBrush), typeof(Brush),
        typeof(GlassSlider), new PropertyMetadata(null, (d, _) => ((GlassSlider)d).ApplyBrushes()));

    private static readonly DependencyProperty LiftProperty = DependencyProperty.Register("Lift", typeof(double), typeof(GlassSlider),
        new PropertyMetadata(0.0, (d, _) => ((GlassSlider)d).Paint()));

    /// <summary>The slider's size: its thumb is 60 x 40 lifted, times this.</summary>
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    /// <summary>Bind to the Slider's Background: a brush of its own replaces the rail and the fill.</summary>
    public Brush? RailBrush { get => (Brush?)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }

    private double Lift => (double)GetValue(LiftProperty);

    private readonly Grid _bed = new();
    private readonly Border _rail = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fill = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Canvas _canvas = new();
    private readonly Grid _thumb = new() { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _thumbScale = new(RestScale, RestScale);
    private readonly TranslateTransform _thumbMove = new();
    private readonly GlassShadow _shadow = new() { BlurRadius = 24, ShadowDepth = 6, ShadowOpacity = 0.2 };
    private readonly Canvas _lens = new() { Background = Brushes.Transparent, ClipToBounds = true, Visibility = Visibility.Collapsed };
    private readonly Border _insetRail = new(), _insetFill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _ring = new() { BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)) };
    private readonly Border _face = new() { Background = Brushes.White };
    private LiquidGlassEffect? _effect;
    private bool _lensTried, _dragging;
    private Slider? _owner;

    public GlassSlider()
    {
        Background = Brushes.Transparent;
        VerticalAlignment = VerticalAlignment.Stretch;

        _bed.Children.Add(_rail);
        _bed.Children.Add(_fill);
        Children.Add(_bed);

        var transform = new TransformGroup();
        transform.Children.Add(_thumbScale);
        transform.Children.Add(_thumbMove);
        _thumb.RenderTransform = transform;
        _insetRail.Child = _insetFill;
        _lens.Children.Add(_insetRail);
        _thumb.Children.Add(_shadow);
        _thumb.Children.Add(_lens);
        _thumb.Children.Add(_ring);
        _thumb.Children.Add(_face);
        _canvas.Children.Add(_thumb);
        Children.Add(_canvas);

        ApplyBrushes();
        ApplySize();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Paint();
    }

    private void ApplyBrushes()
    {
        if (RailBrush is { } own)
        {
            _rail.Background = own;
            _insetRail.Background = own;
            _fill.Visibility = _insetFill.Visibility = Visibility.Collapsed;
            return;
        }
        _rail.SetResourceReference(Border.BackgroundProperty, "SliderRail");
        _insetRail.SetResourceReference(Border.BackgroundProperty, "SliderRail");
        _fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        _insetFill.SetResourceReference(Border.BackgroundProperty, "Accent");
        _fill.Visibility = _insetFill.Visibility = Visibility.Visible;
    }

    private double HitWidth => HitWidthUnits * Scale;

    private void ApplySize()
    {
        double s = Scale, w = ThumbWidthUnits * s, h = ThumbHeightUnits * s, rail = RailUnits * s;
        MinHeight = h * RestScale + 4;
        _rail.Height = _fill.Height = rail;
        _rail.CornerRadius = _fill.CornerRadius = new CornerRadius(rail / 2);
        _thumb.Width = w;
        _thumb.Height = h;
        _lens.Clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        _ring.CornerRadius = _face.CornerRadius = new CornerRadius(h / 2);
        _ring.BorderThickness = new Thickness(1.5 * s);
        _shadow.CornerRadius = h / 2;
        if (_effect != null) ConfigureLens(_effect);
        Paint();
    }

    private void ConfigureLens(LiquidGlassEffect fx)
    {
        // glisse's slider lens: strength 0.75, dispersion 0.65, its 2 px band and 5 px dome over the 40 px tall thumb.
        fx.Size = new Size(_thumb.Width, _thumb.Height);
        fx.CornerRadius = _thumb.Height / 2;
        fx.Inset = 0;
        fx.LensStyle = 1;
        fx.LensStrength = 0.75;
        fx.LensDepth = 2 / 40.0;
        fx.LensDome = 5 / 40.0;
        fx.LensDispersion = 0.65;
        fx.LensBlur = 0.5;
        fx.RimShine = 0;
        fx.RimEdge = 0;
        fx.Saturation = 1.5;
        fx.Brightness = 1;
        fx.Frost = 0.5;
        fx.Reflection = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = TemplatedParent as Slider;
        if (_owner != null)
        {
            _owner.ValueChanged += OnOwnerValueChanged;
            _owner.PreviewMouseLeftButtonDown += OnOwnerDown;
            _owner.PreviewMouseMove += OnOwnerMove;
            _owner.PreviewMouseLeftButtonUp += OnOwnerUp;
            _owner.LostMouseCapture += OnOwnerCaptureLost;
            DependencyPropertyDescriptorHelper.Watch(_owner, RangeBaseMinimum, OnOwnerRangeChanged);
            DependencyPropertyDescriptorHelper.Watch(_owner, RangeBaseMaximum, OnOwnerRangeChanged);
        }
        Paint();
    }

    private static readonly DependencyProperty RangeBaseMinimum = System.Windows.Controls.Primitives.RangeBase.MinimumProperty;
    private static readonly DependencyProperty RangeBaseMaximum = System.Windows.Controls.Primitives.RangeBase.MaximumProperty;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner != null)
        {
            _owner.ValueChanged -= OnOwnerValueChanged;
            _owner.PreviewMouseLeftButtonDown -= OnOwnerDown;
            _owner.PreviewMouseMove -= OnOwnerMove;
            _owner.PreviewMouseLeftButtonUp -= OnOwnerUp;
            _owner.LostMouseCapture -= OnOwnerCaptureLost;
            DependencyPropertyDescriptorHelper.Unwatch(_owner, RangeBaseMinimum, OnOwnerRangeChanged);
            DependencyPropertyDescriptorHelper.Unwatch(_owner, RangeBaseMaximum, OnOwnerRangeChanged);
            _owner = null;
        }
        _dragging = false;
    }

    private void OnOwnerValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Paint();
    private void OnOwnerRangeChanged(object? sender, EventArgs e) => Paint();

    private double Progress
    {
        get
        {
            if (_owner is not { } o || o.Maximum <= o.Minimum) return 0;
            return Math.Clamp((o.Value - o.Minimum) / (o.Maximum - o.Minimum), 0, 1);
        }
    }

    private void Paint()
    {
        double width = ActualWidth, height = ActualHeight, s = Scale;
        if (width <= 0) return;

        double lift = Math.Clamp(Lift, 0, 1), scale = RestScale + (1 - RestScale) * Lift;
        double w = ThumbWidthUnits * s, h = ThumbHeightUnits * s, hit = HitWidth;
        double p = Progress;
        double centre = (width - hit) * p + hit / 2;

        Canvas.SetLeft(_thumb, centre - w / 2);
        Canvas.SetTop(_thumb, (height - h) / 2);
        _thumbScale.ScaleX = _thumbScale.ScaleY = scale;
        _fill.Width = centre;
        _face.Opacity = 1 - 0.85 * lift;

        if (Lift <= 0.01)
        {
            if (_lens.Visibility == Visibility.Visible)
            {
                _lens.Effect = null;
                _lens.Visibility = Visibility.Collapsed;
                _bed.Clip = null;
            }
            return;
        }

        if (_lens.Visibility != Visibility.Visible)
        {
            _lens.Visibility = Visibility.Visible;
            if (!_lensTried)
            {
                _lensTried = true;
                _effect = LiquidGlassEffect.TryCreate();
                if (_effect != null) ConfigureLens(_effect);
            }
            _lens.Effect = _effect;
        }

        // The real rail makes way under the glass; the thumb shows the magnified one instead, where it lies on screen.
        double shownW = w * scale, shownH = h * scale, midY = height / 2;
        _bed.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(new Rect(centre - shownW / 2, midY - shownH / 2, shownW, shownH), shownH / 2, shownH / 2));

        double line = LensLineUnits * s * (0.6 + 0.4 * lift) / scale;
        _insetRail.Width = width / scale;
        _insetRail.Height = line;
        _insetRail.CornerRadius = _insetFill.CornerRadius = new CornerRadius(line / 2);
        Canvas.SetLeft(_insetRail, w / 2 - centre / scale);
        Canvas.SetTop(_insetRail, (h - line) / 2);

        // Within a few pixels of either end the fill gives way from the middle of the thumb to the end of the rail,
        // so through the thumb an empty slider shows no fill and a full one no bare rail.
        double travel = (width - hit) * p, rest = width - hit - travel, half = hit / 2;
        _insetFill.Width = (travel + Math.Min(half, travel * 4) + Math.Max(0, half - rest * 4)) / scale;
    }

    // ═══ The mouse ═══

    private void OnOwnerDown(object sender, MouseButtonEventArgs e)
    {
        if (_owner is not { IsEnabled: true } owner) return;
        _dragging = true;
        owner.Focus();
        owner.CaptureMouse();
        e.Handled = true;
        SetFromPointer(e.GetPosition(this).X);
        LiftTo(1);
    }

    private void OnOwnerMove(object sender, MouseEventArgs e)
    {
        if (_dragging) SetFromPointer(e.GetPosition(this).X);
    }

    private void OnOwnerUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) EndDrag();
    }

    private void OnOwnerCaptureLost(object sender, MouseEventArgs e)
    {
        if (_dragging) EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        _owner?.ReleaseMouseCapture();
        LiftTo(0);
    }

    private void LiftTo(double target)
    {
        if (FluidMotion.AnimationsDisabled)
        {
            BeginAnimation(LiftProperty, null);
            SetValue(LiftProperty, 0.0);
            return;
        }
        Liquid.To(this, LiftProperty, target, LiftSpring);
    }

    private void SetFromPointer(double x)
    {
        if (_owner is not { } o || ActualWidth <= HitWidth) return;
        double p = Math.Clamp((x - HitWidth / 2) / (ActualWidth - HitWidth), 0, 1);
        double value = o.Minimum + p * (o.Maximum - o.Minimum);
        if (o.IsSnapToTickEnabled && o.TickFrequency > 0)
            value = o.Minimum + Math.Round((value - o.Minimum) / o.TickFrequency) * o.TickFrequency;
        o.Value = Math.Clamp(value, o.Minimum, o.Maximum);
    }
}

internal static class DependencyPropertyDescriptorHelper
{
    public static void Watch(DependencyObject target, DependencyProperty property, EventHandler handler)
        => System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, target.GetType())?.AddValueChanged(target, handler);

    public static void Unwatch(DependencyObject target, DependencyProperty property, EventHandler handler)
        => System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, target.GetType())?.RemoveValueChanged(target, handler);
}
