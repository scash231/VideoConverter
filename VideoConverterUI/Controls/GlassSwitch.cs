using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VideoConverterUI.Animations;

namespace VideoConverterUI.Controls;

/// <summary>
/// glisse's switch (github.com/0kzh/glisse, src/switch.tsx), drawn inside a CheckBox's template. All sizes are glisse's
/// at its 160 x 67 design size times <see cref="Scale"/> (glisse uses 0.375). At rest the thumb is a white capsule, dimmed while off. Held,
/// it lifts into clear glass, a lens that shows the track drawn a little smaller, ringed by the glass and bent at its
/// rim, and it can be dragged across. The CheckBox stays the control: a click toggles it as usual, a drag ends in
/// <see cref="ToggleButton.IsCheckedProperty"/> plus a Click event. The lens exists only while the thumb is lifted, so
/// a resting switch costs no effect. A change of state happens in the lens: the thumb lifts, travels, and goes back to
/// the white capsule only once it has fully arrived.
/// </summary>
public sealed class GlassSwitch : Grid
{
    // glisse's springs (stiffness, damping) as response and damping ratio.
    private static readonly Spring TravelSpring = FromStiffness(1000, 80);
    private static readonly Spring LiftSpring = FromStiffness(2000, 80);
    // Going to a new state: quick, without glisse's overdamped tail, which only kept the thumb creeping in the lens.
    // The main window's Upload / Download thumb travels on this too, so the two take the same time.
    public static readonly Spring SettleSpring = new(0.24, 1);

    private const double RestScale = 0.65, LiftScaleGain = 0.25;
    private const double TravelUnits = 57.9, ThumbLeftUnits = -21.95, ThumbWidthUnits = 146, ThumbHeightUnits = 92;
    private const double TrackWidthUnits = 160, TrackHeightUnits = 67;

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double),
        typeof(GlassSwitch), new PropertyMetadata(0.33, (d, _) => ((GlassSwitch)d).ApplySize()));

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool?),
        typeof(GlassSwitch), new PropertyMetadata(false, (d, _) => ((GlassSwitch)d).Sync()));

    public static readonly DependencyProperty IsHeldProperty = DependencyProperty.Register(nameof(IsHeld), typeof(bool),
        typeof(GlassSwitch), new PropertyMetadata(false, (d, _) => ((GlassSwitch)d).SyncLift()));

    private static readonly DependencyProperty PositionProperty = Paints(nameof(Position));
    private static readonly DependencyProperty ColorProperty = Paints(nameof(Color));
    private static readonly DependencyProperty LiftProperty = Paints(nameof(Lift));

    private static DependencyProperty Paints(string name) => DependencyProperty.Register(name, typeof(double), typeof(GlassSwitch),
        new PropertyMetadata(0.0, (d, _) => ((GlassSwitch)d).Paint()));

    /// <summary>The switch's size: the track is 160 x 67 times this.</summary>
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    /// <summary>Bind to the CheckBox's IsChecked.</summary>
    public bool? IsOn { get => (bool?)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    /// <summary>Bind to the CheckBox's IsPressed.</summary>
    public bool IsHeld { get => (bool)GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }

    private double Position => (double)GetValue(PositionProperty);
    private double Color => (double)GetValue(ColorProperty);
    private double Lift => (double)GetValue(LiftProperty);

    private readonly Grid _bed = new();
    private readonly Border _track = new(), _fill = new();
    private readonly Canvas _canvas = new();
    private readonly Grid _thumb = new() { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _thumbScale = new(RestScale, RestScale);
    private readonly TranslateTransform _thumbMove = new();
    private readonly GlassShadow _shadow = new() { BlurRadius = 24, ShadowDepth = 6, ShadowOpacity = 0.2 };
    private readonly Canvas _lens = new() { Background = Brushes.Transparent, ClipToBounds = true, Visibility = Visibility.Collapsed };
    private readonly Border _insetTrack = new(), _insetFill = new();
    private readonly Border _ring = new() { BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 255, 255, 255)) };
    private readonly Border _gloss = new() { IsHitTestVisible = false };
    private readonly Border _face = new(), _faceOn = new() { Background = Brushes.White };  // the off colour, the white over it as the switch turns on
    private LiquidGlassEffect? _effect;
    private bool _lensTried;

    private CheckBox? _owner;
    private bool _dragging, _moved;
    private bool _travelling;  // on its way to a new state: the thumb stays lifted until it has arrived
    private double _dragStartX, _dragFrom, _dragValue;

    public GlassSwitch()
    {
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

        _track.SetResourceReference(Border.BackgroundProperty, "SwitchTrack");
        _fill.SetResourceReference(Border.BackgroundProperty, "SwitchOn");
        _face.SetResourceReference(Border.BackgroundProperty, "SwitchThumbOff");
        _face.Child = _faceOn;
        _insetTrack.SetResourceReference(Border.BackgroundProperty, "SwitchTrack");
        _insetFill.SetResourceReference(Border.BackgroundProperty, "SwitchOn");
        _bed.Children.Add(_track);
        _bed.Children.Add(_fill);
        Children.Add(_bed);

        var transform = new TransformGroup();
        transform.Children.Add(_thumbScale);
        transform.Children.Add(_thumbMove);
        _thumb.RenderTransform = transform;
        _insetTrack.Child = _insetFill;
        _lens.Children.Add(_insetTrack);
        _thumb.Children.Add(_shadow);
        _thumb.Children.Add(_lens);
        _thumb.Children.Add(_ring);
        _thumb.Children.Add(_gloss);
        _thumb.Children.Add(_face);
        _gloss.Background = new LinearGradientBrush(
            [new GradientStop(System.Windows.Media.Color.FromArgb(0x73, 255, 255, 255), 0), new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0.12)],
            new Point(0, 0), new Point(0, 1));
        _canvas.Children.Add(_thumb);
        Children.Add(_canvas);

        ApplySize();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static Spring FromStiffness(double stiffness, double damping)
    {
        double omega = Math.Sqrt(stiffness);
        return new Spring(2 * Math.PI / omega, damping / (2 * omega));
    }

    private double Travel => TravelUnits * Scale;

    private void ApplySize()
    {
        double s = Scale;
        Width = TrackWidthUnits * s;
        Height = TrackHeightUnits * s;
        var capsule = new CornerRadius(TrackHeightUnits * s / 2);
        _track.CornerRadius = capsule;
        _fill.CornerRadius = capsule;

        double w = ThumbWidthUnits * s, h = ThumbHeightUnits * s;
        var round = new CornerRadius(h / 2);
        _thumb.Width = w;
        _thumb.Height = h;
        Canvas.SetLeft(_thumb, ThumbLeftUnits * s);
        Canvas.SetTop(_thumb, (Height - h) / 2);
        _lens.Clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        _ring.CornerRadius = round;
        _ring.BorderThickness = new Thickness(1.5 * s / 0.375);
        _gloss.CornerRadius = round;
        _face.CornerRadius = round;
        _faceOn.CornerRadius = round;
        _shadow.CornerRadius = h / 2;
        if (_effect != null) ConfigureLens(_effect);
        Paint();
    }

    private void ConfigureLens(LiquidGlassEffect fx)
    {
        // glisse's switch lens: its 2 px band and 6 px dome over the 34.5 px tall thumb, kept as shares of the thumb.
        fx.Size = new Size(_thumb.Width, _thumb.Height);
        fx.CornerRadius = _thumb.Height / 2;
        fx.Inset = 0;
        fx.LensStyle = 1;
        fx.LensStrength = 0.43;
        fx.LensDepth = 2 / 34.5;
        fx.LensDome = 6 / 34.5;
        fx.LensDispersion = 1;
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
        _owner = TemplatedParent as CheckBox;
        if (_owner != null)
        {
            _owner.PreviewMouseLeftButtonDown += OnOwnerDown;
            _owner.PreviewMouseMove += OnOwnerMove;
            _owner.PreviewMouseLeftButtonUp += OnOwnerUp;
            _owner.LostMouseCapture += OnOwnerCaptureLost;
        }
        Snap();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner != null)
        {
            _owner.PreviewMouseLeftButtonDown -= OnOwnerDown;
            _owner.PreviewMouseMove -= OnOwnerMove;
            _owner.PreviewMouseLeftButtonUp -= OnOwnerUp;
            _owner.LostMouseCapture -= OnOwnerCaptureLost;
            _owner = null;
        }
        _dragging = false;
    }

    private bool Target => IsOn == true;

    private void Snap()
    {
        _travelling = false;
        foreach (var dp in new[] { PositionProperty, ColorProperty, LiftProperty }) BeginAnimation(dp, null);
        SetValue(PositionProperty, Target ? 1.0 : 0.0);
        SetValue(ColorProperty, Target ? 1.0 : 0.0);
        SetValue(LiftProperty, 0.0);
        Paint();
    }

    private bool Animates => IsLoaded && IsVisible && !FluidMotion.AnimationsDisabled;

    private void Sync()
    {
        if (_dragging) return;
        if (!Animates) { Snap(); return; }
        double target = Target ? 1 : 0;
        _travelling = Math.Abs(Position - target) > 0.002;
        SyncLift();
        Liquid.To(this, PositionProperty, target, SettleSpring, precision: 0.005, done: () =>
        {
            _travelling = false;
            SyncLift();
        });
        Liquid.To(this, ColorProperty, target, SettleSpring);
    }

    private void SyncLift()
    {
        if (Animates) Liquid.To(this, LiftProperty, IsHeld || _dragging || _travelling ? 1 : 0, LiftSpring);
        else
        {
            BeginAnimation(LiftProperty, null);
            SetValue(LiftProperty, 0.0);
        }
    }

    private void Paint()
    {
        double lift = Math.Clamp(Lift, 0, 1);
        double scale = RestScale + LiftScaleGain * Lift;
        double x = Position * Travel;
        _thumbScale.ScaleX = scale;
        _thumbScale.ScaleY = scale;
        _thumbMove.X = x;
        _fill.Opacity = Math.Clamp(Color, 0, 1);
        _face.Opacity = 1 - 0.85 * lift;
        _faceOn.Opacity = _fill.Opacity;

        bool lifted = Lift > 0.01;
        if (!lifted)
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

        double s = Scale, w = ThumbWidthUnits * s, h = ThumbHeightUnits * s;
        double trackW = TrackWidthUnits * s, trackH = TrackHeightUnits * s;
        double thumbCentreX = ThumbLeftUnits * s + w / 2 + x, centreY = trackH / 2;

        // The real track makes way under the glass; the thumb shows the smaller one instead, where it lies on screen.
        double shownW = w * scale, shownH = h * scale;
        _bed.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, trackW, trackH)),
            new RectangleGeometry(new Rect(thumbCentreX - shownW / 2, centreY - shownH / 2, shownW, shownH), shownH / 2, shownH / 2));

        double insetW = trackW * (0.85 + 0.1 * lift) / scale;
        double insetH = trackH * 0.75 * (0.525 + 0.45 * lift) / scale;
        double insetCentreX = w / 2 + (trackW / 2 - thumbCentreX) / scale;
        _insetTrack.Width = insetW;
        _insetTrack.Height = insetH;
        _insetTrack.CornerRadius = new CornerRadius(insetH / 2);
        _insetFill.CornerRadius = new CornerRadius(insetH / 2);
        _insetFill.Opacity = _fill.Opacity;
        Canvas.SetLeft(_insetTrack, insetCentreX - insetW / 2);
        Canvas.SetTop(_insetTrack, (h - insetH) / 2);
    }

    // ═══ Dragging the thumb ═══
    // The CheckBox has the mouse captured while pressed, so its Preview events are where the moves arrive.

    private void OnOwnerDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseOver || _owner is not { IsEnabled: true }) return;
        _dragging = true;
        _moved = false;
        _dragStartX = e.GetPosition(this).X;
        _dragFrom = Target ? 1 : 0;
        _dragValue = _dragFrom;
    }

    private void OnOwnerMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        double dx = e.GetPosition(this).X - _dragStartX;
        if (!_moved && Math.Abs(dx) < 4) return;
        _moved = true;
        _dragValue = _dragFrom + dx / Travel;
        double overshoot = _dragValue < 0 ? _dragValue : _dragValue > 1 ? _dragValue - 1 : 0;
        Liquid.To(this, PositionProperty, Math.Clamp(_dragValue, 0, 1) + overshoot / 22, TravelSpring);
        Liquid.To(this, ColorProperty, _dragValue > 0.5 ? 1 : 0, TravelSpring);
    }

    private void OnOwnerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (!_moved || _owner is not { } owner) return;  // a tap: the CheckBox's own click

        e.Handled = true;
        _travelling = true;  // no dip in the lift between letting go and the trip to the final state
        owner.ReleaseMouseCapture();
        bool next = _dragValue > 0.5;
        if (next != Target)
        {
            owner.SetCurrentValue(ToggleButton.IsCheckedProperty, next);
            owner.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, owner));
        }
        Sync();
    }

    private void OnOwnerCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Sync();
    }
}
