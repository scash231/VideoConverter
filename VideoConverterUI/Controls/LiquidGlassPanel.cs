using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VideoConverterUI.Services;

namespace VideoConverterUI.Controls;

/// <summary>
/// Apple Liquid Glass surface. Children added in XAML sit on the glass; the panel clips everything to its squircle.
/// Its look comes from <see cref="GlassTuning"/> under the name in <see cref="Surface"/> (per theme mode) and updates
/// live while the Glass Tuner edits it.
///
/// Layers, bottom to top: a live copy (VisualBrush; cached when it comes from another window, see _picture) of what
/// lies behind on an opaque window-coloured base, through
/// <see cref="LiquidGlassEffect"/> (vibrancy, frosted middle, lensing and light-catching rim), then a thin material
/// tint and a top sheen, the children, and an outline. Without the shader (software rendering, or Liquid off in the
/// tuner) it is classic frosted glass: heavy blur, denser tint and a specular rim.
///
/// What lies behind: <see cref="BackdropSource"/>, or found automatically: in a Popup (a ComboBox drop-down, a
/// flyout) the content of the window the popup belongs to; in a window with an Owner (a dialog, the notification)
/// the owner's content. A panel inside the window it should show needs an explicit BackdropSource that does not
/// contain the panel (a VisualBrush can't show itself). <see cref="BackdropOverlay"/> adds something of the panel's
/// own behind the glass, over that backdrop. <see cref="LiquidGlass"/> turns an existing Border into one.
/// The panel also drives its parent's drop shadow (ShadowBlur/Depth/Opacity), so a parent that clips nothing can
/// carry the shadow the clipped panel can't.
/// </summary>
public class LiquidGlassPanel : Grid
{
    private readonly Border _host = new() { IsHitTestVisible = false };
    private readonly Grid _backdrop = new();
    private readonly Border _base = new();
    private readonly Rectangle _blur = new();  // the backdrop (brush), softened by _blurEffect
    private readonly BlurEffect _blurEffect = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    // Holds _blur. With a backdrop in another window (a popup's, a banner's or a dialog's owner, with all its glass and
    // shadows) rendering that backdrop is the expensive part, so the picture is rendered into a bitmap (BitmapCache)
    // reaching PictureMargin past the blur's overscan, and only again when the backdrop changes: while the panel
    // opens, moves or grows the bitmap is shifted along (AimBrush), and anything else redrawing on the glass reuses it.
    // A backdrop in the panel's own window (the main window's gradient, a page under a sidebar) is cheap to render
    // and isn't cached: off-screen renders of that window (another panel's AdaptiveContrast samples) would have to
    // draw every cached bitmap in software at full size.
    private readonly Border _picture = new();
    private readonly BitmapCache _pictureCache = new();
    private bool _cachePicture;
    private const double PictureMargin = 32;
    private readonly Border _overlayHost = new() { IsHitTestVisible = false };  // BackdropOverlay, at the panel's own bounds
    private readonly Border _tint = new() { IsHitTestVisible = false };
    // Adaptive contrast: a window-coloured wash that fades in only where what lies behind would wash out the content
    // (something bright behind light text in dark mode, something dark behind dark text in light mode). Its strength
    // varies across the panel through a low-resolution opacity mask; two layers crossfade when the mask changes.
    private readonly Border[] _scrims = [new() { IsHitTestVisible = false, Opacity = 0 }, new() { IsHitTestVisible = false, Opacity = 0 }];
    private int _frontScrim;
    private double[]? _shownNeed;  // the mask the front scrim shows, per sample (null = no scrim)
    private double _glassSaturation = 1, _glassBrightness = 1;  // what the shader does to the backdrop's colours
    private readonly DispatcherTimer _sampler = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    // Sampling on changes (Settings → Developer, AppSettings.GlassSamplingMode): a one-shot that a scroll, a resize or
    // the panel moving arms; while changes keep coming it samples about every ChangeDelay.
    private readonly DispatcherTimer _changeSampler = new(DispatcherPriority.Background);
    private bool _watchingChanges;
    private UIElement? _watchedSource;
    private bool _sampledOnce;  // since the backdrop was (re)connected
    private RenderTargetBitmap? _sampleBitmap;
    private List<MediaElement> _videos = new();  // in the backdrop; refreshed now and then (SampleBackdrop)
    private int _sampleCount;
    // The last sample's pixels and the map worked out from them: an unchanged backdrop skips the maths, and the timer
    // backs off (SampleInterval … MaxSampleInterval) while nothing changes.
    private byte[]? _lastPx;
    private double[]? _lastNeed;
    private int _dimVersion, _lastDimVersion = -1;  // bumped whenever what the map depends on (colours, strength) may have changed
    private readonly Dictionary<int, double> _needByColor = new();  // scrim per backdrop colour, for the current _dimVersion
    // Keeping the backdrop aligned: a per-frame hook only while the panel moves (or may be about to), see ArmAlign.
    private bool _alignHooked;
    private int _alignIdleFrames;
    private Window? _myWindow, _sourceWindow;
    private static readonly Brush VideoStandIn = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    private readonly Border _sheen = new() { IsHitTestVisible = false };
    private readonly GradientStop _sheenStop = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0);
    private readonly SquircleBorder _rim = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly SquircleBorder _outline = new() { BorderThickness = new Thickness(0.5), IsHitTestVisible = false };
    // Outline and light-catching edge of an L-shaped surface (ReverseLArms), traced along the cut-out too.
    private readonly Path _shapeOutline = new() { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Path _shapeRim = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Opacity = 0.7 };
    // Soft shadow under everything on the glass (text, icons) so it stays readable over a busy backdrop; shared by all
    // content children, recoloured per mode in Apply.
    private readonly DropShadowEffect _textShadow = new() { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.45, Color = Colors.Black };

    private LiquidGlassEffect? _effect;
    private VisualBrush? _brush;
    private Visual? _source;
    private bool _attached;
    // The brush is only re-aimed (Viewbox) when the panel has moved or grown past the picture's margin; smaller moves
    // and scaling shift the cached picture instead (_pictureShift, see AimBrush).
    private readonly MatrixTransform _pictureShift = new();
    private Rect _brushBase = Rect.Empty;  // the backdrop region the Viewbox was last aimed at
    private Size _brushBaseSize;           // the picture's size then
    private Rect _lastBox = Rect.Empty;    // the region behind the picture at the last check

    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(string), typeof(LiquidGlassPanel),
        new PropertyMetadata("Default", (d, _) => ((LiquidGlassPanel)d).Apply()));

    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(Visual), typeof(LiquidGlassPanel),
        new PropertyMetadata(null, (d, _) => ((LiquidGlassPanel)d).Rebuild()));

    public static readonly DependencyProperty BackdropOverlayProperty = DependencyProperty.Register(
        nameof(BackdropOverlay), typeof(UIElement), typeof(LiquidGlassPanel),
        new PropertyMetadata(null, (d, e) =>
        {
            var panel = (LiquidGlassPanel)d;
            panel._overlayHost.Child = (UIElement?)e.NewValue;
            panel.RequestSample();
        }));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LiquidGlassPanel),
        new PropertyMetadata(double.NaN, (d, _) => ((LiquidGlassPanel)d).ApplyRadius()));

    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(LiquidGlassPanel),
        new PropertyMetadata(null, (d, e) =>
        {
            var outline = ((LiquidGlassPanel)d)._outline;
            if (e.NewValue is Brush brush) outline.BorderBrush = brush;
            else outline.SetResourceReference(SquircleBorder.BorderBrushProperty, "Tint1F");
        }));

    /// <summary>Tuning set in <see cref="GlassTuning"/> (see <see cref="GlassSurfaces"/>).</summary>
    public string Surface
    {
        get => (string)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>What shows through the glass. Optional in popups (found automatically); must not contain the panel.</summary>
    public Visual? BackdropSource
    {
        get => (Visual?)GetValue(BackdropSourceProperty);
        set => SetValue(BackdropSourceProperty, value);
    }

    /// <summary>
    /// Something the glass shows over its backdrop, laid out at the panel's own bounds (e.g. a picture on the glass
    /// mirrored below itself, so the glass around it reflects it the way glass beside a video does). It goes through
    /// the glass like the backdrop: frosted in the middle, lensed at the rim, and counted by the adaptive contrast.
    /// </summary>
    public UIElement? BackdropOverlay
    {
        get => (UIElement?)GetValue(BackdropOverlayProperty);
        set => SetValue(BackdropOverlayProperty, value);
    }

    /// <summary>Corner radius; NaN (default) = the surface's tuned radius. Can be animated (the clip follows).</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty TextShadowProperty = DependencyProperty.Register(
        nameof(TextShadow), typeof(bool), typeof(LiquidGlassPanel),
        new PropertyMetadata(true, (d, _) => ((LiquidGlassPanel)d).ApplyTextShadow()));

    /// <summary>A slight shadow under the content (on by default): dark in dark mode, a soft light halo in light mode.</summary>
    public bool TextShadow
    {
        get => (bool)GetValue(TextShadowProperty);
        set => SetValue(TextShadowProperty, value);
    }

    public static readonly DependencyProperty AdaptiveContrastProperty = DependencyProperty.Register(
        nameof(AdaptiveContrast), typeof(bool), typeof(LiquidGlassPanel),
        new PropertyMetadata(true, (d, _) => ((LiquidGlassPanel)d).Rebuild()));

    /// <summary>Fade in a soft wash when the backdrop gets too bright (dark mode) or too dark (light mode) for the
    /// content to stay readable (on by default; panels too small to hold text never sample).</summary>
    public bool AdaptiveContrast
    {
        get => (bool)GetValue(AdaptiveContrastProperty);
        set => SetValue(AdaptiveContrastProperty, value);
    }

    public static readonly DependencyProperty ReverseLArmsProperty = DependencyProperty.Register(
        nameof(ReverseLArms), typeof(Size), typeof(LiquidGlassPanel),
        new PropertyMetadata(Size.Empty, (d, _) => ((LiquidGlassPanel)d).ApplyRadius()));

    /// <summary>
    /// Turns the surface into one backwards-L platform (⅃): a right-hand arm <c>Width</c> wide and a bottom arm
    /// <c>Height</c> tall, joined at the bottom-right; the top-left rest is cut out (and lets clicks through), with a
    /// rounded inner corner. Empty (default) = the usual rounded rectangle.
    /// </summary>
    public Size ReverseLArms
    {
        get => (Size)GetValue(ReverseLArmsProperty);
        set => SetValue(ReverseLArmsProperty, value);
    }

    private bool IsLShape => !ReverseLArms.IsEmpty && ReverseLArms.Width > 0 && ReverseLArms.Height > 0
                             && ReverseLArms.Width < ActualWidth && ReverseLArms.Height < ActualHeight;

    /// <summary>The ⅃ outline: right arm ∪ bottom arm (each a squircle) plus a concave fillet where they meet.</summary>
    private Geometry BuildLShape(double r)
    {
        double w = ActualWidth, h = ActualHeight, aw = ReverseLArms.Width, ah = ReverseLArms.Height;
        double ix = w - aw, iy = h - ah;  // the inner corner
        r = Math.Min(r, Math.Min(aw, ah) / 2);
        Geometry shape = new CombinedGeometry(GeometryCombineMode.Union,
            SquircleBorder.CreateAppleStyleGeometry(new Rect(ix, 0, aw, h), r),
            SquircleBorder.CreateAppleStyleGeometry(new Rect(0, iy, w, ah), r));
        // The arms overlap only along straight edges; square off the joint, then round the inside corner.
        shape = new CombinedGeometry(GeometryCombineMode.Union, shape, new RectangleGeometry(new Rect(ix, iy, aw, ah)));
        var fillet = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(ix - r, iy - r, r, r)),
            new EllipseGeometry(new Point(ix - r, iy - r), r, r));
        shape = new CombinedGeometry(GeometryCombineMode.Union, shape, fillet).GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
        shape.Freeze();
        return shape;
    }

    /// <summary>Hairline around the glass (defaults to the theme's Tint1F).</summary>
    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public LiquidGlassPanel()
    {
        Background = Brushes.Transparent;  // the whole surface takes clicks, also between children

        _blur.Effect = _blurEffect;
        _picture.Child = _blur;
        _picture.RenderTransform = _pictureShift;
        _backdrop.Children.Add(_base);
        _backdrop.Children.Add(_picture);
        _backdrop.Children.Add(_overlayHost);
        _host.Child = new Border { ClipToBounds = true, Child = _backdrop };  // trims the picture's overflow to the host
        _host.SizeChanged += (_, e) => { if (_effect != null) _effect.Size = e.NewSize; };

        _sheen.Background = new LinearGradientBrush(
            new GradientStopCollection { _sheenStop, new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.6) },
            new Point(0, 0), new Point(0, 1));
        _rim.BorderBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0),
            new(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.35),
            new(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.65),
            new(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 1),
        }, new Point(0.3, 0), new Point(0.7, 1));
        _outline.SetResourceReference(SquircleBorder.BorderBrushProperty, "Tint1F");
        _shapeOutline.SetResourceReference(Shape.StrokeProperty, "Tint1F");
        _shapeRim.Stroke = _rim.BorderBrush;

        // Glass under the children, rim and outline over them, whatever order XAML adds children in.
        int z = 0;
        foreach (var layer in new FrameworkElement[] { _host, _tint, _scrims[0], _scrims[1], _sheen })
        {
            SetZIndex(layer, -1000 + z++);
            Span(layer);
            Children.Add(layer);
        }
        foreach (var layer in new FrameworkElement[] { _rim, _outline, _shapeRim, _shapeOutline })
        {
            SetZIndex(layer, 1000 + z++);
            Span(layer);
            Children.Add(layer);
        }

        _sampler.Tick += (_, _) =>
        {
            bool changed = SampleBackdrop();
            // Back off while the backdrop stays the same (and while the app is in the background); any change, a
            // move or the app coming back brings the timer back to its normal pace (NudgeSampler).
            var next = changed ? SampleInterval : TimeSpan.FromTicks(Math.Min(_sampler.Interval.Ticks * 2, MaxSampleInterval.Ticks));
            if (!AppActive) next = MaxSampleInterval;
            if (_sampler.Interval != next) _sampler.Interval = next;
        };
        _changeSampler.Tick += (_, _) =>
        {
            _changeSampler.Stop();
            if (SampleBackdrop()) NudgeSampler();
        };

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        // Popup content is "loaded" along with the window, before the popup opens and gets its own HWND,
        // and nothing reloads it when it opens; the presentation source changing is the reliable signal.
        PresentationSource.AddSourceChangedHandler(this, (_, _) => { if (_attached) Rebuild(); });
        SizeChanged += (_, _) =>
        {
            UpdateClip();
            RequestSample();
        };
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement removed && ReferenceEquals(removed.Effect, _textShadow)) removed.Effect = null;
        if (visualAdded is UIElement added) ShadowContent(added);
    }

    private bool IsLayer(UIElement e) => e == _host || e == _tint || e == _scrims[0] || e == _scrims[1] || e == _sheen || e == _rim || e == _outline
                                         || e == _shapeRim || e == _shapeOutline;

    // Only content children, and only those without an effect of their own.
    private void ShadowContent(UIElement child)
    {
        if (IsLayer(child)) return;
        if (TextShadow) { if (child.Effect == null) child.Effect = _textShadow; }
        else if (ReferenceEquals(child.Effect, _textShadow)) child.Effect = null;
    }

    private void ApplyTextShadow()
    {
        foreach (UIElement child in Children) ShadowContent(child);
    }

    private static void Span(FrameworkElement e)
    {
        SetRowSpan(e, 100);
        SetColumnSpan(e, 100);
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        GlassTuning.Changed += Apply;
        ThemeService.ThemeApplied += Apply;  // mode, colours and the Clear ↔ Tinted slider
        SettingsService.SettingsChanged += OnSettingsChanged;  // the sampling mode (Developer)
        Rebuild();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        GlassTuning.Changed -= Apply;
        ThemeService.ThemeApplied -= Apply;
        SettingsService.SettingsChanged -= OnSettingsChanged;
        StopTracking();
        _sampler.Stop();
        _changeSampler.Stop();
        UnwatchChanges();
        _brush = null;
        _source = null;
        _blur.Fill = null;
        _brushBase = _lastBox = Rect.Empty;
    }

    /// <summary>(Re)connects the backdrop and applies the tuning.</summary>
    private void Rebuild()
    {
        StopTracking();
        _sampler.Stop();
        _changeSampler.Stop();
        UnwatchChanges();
        _sampledOnce = false;
        _lastPx = null;
        ClearScrim(animate: false);
        _brush = null;
        _source = null;
        _blur.Fill = null;
        _brushBase = _lastBox = Rect.Empty;
        _cachePicture = false;
        _picture.CacheMode = null;

        if (_attached && !SettingsService.Current.EnablePerformanceMode && ResolveBackdrop() is { } source)
        {
            _source = source;
            var mine = PresentationSource.FromVisual(this);
            _cachePicture = mine == null || !ReferenceEquals(mine, PresentationSource.FromVisual(source));
            _picture.CacheMode = _cachePicture ? _pictureCache : null;
            _pictureShift.Matrix = Matrix.Identity;
            _brush = new VisualBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None };
            _blur.Fill = _brush;
            StartTracking();
            if (AutoDims)
            {
                // The first sample (an off-screen render of the backdrop) waits until the window's opening animation is done.
                var first = UsesTimer ? _sampler : _changeSampler;
                first.Interval = FirstSampleDelay;
                first.Start();
            }
        }
        Apply();
    }

    // ── When to sample (Settings → Developer: timer, on changes, or both) ──

    private static string SamplingMode => SettingsService.Current?.GlassSamplingMode ?? "timer";
    private static bool UsesTimer => SamplingMode != "changes";
    private static bool UsesChanges => SamplingMode is "changes" or "both";

    private void OnSettingsChanged(object? sender, EventArgs e)
        => Dispatcher.InvokeAsync(() => { if (_attached) ApplyAutoDim(); });

    /// <summary>Arms a sample shortly after a change; changes while one is pending don't push it back, so a long
    /// scroll still samples about every <see cref="ChangeDelay"/>.</summary>
    private void RequestSample()
    {
        if (_watchingChanges) ScheduleSample();
    }

    /// <summary>A sample shortly, whatever the mode (a pending one stays as it is).</summary>
    private void ScheduleSample()
    {
        if (_changeSampler.IsEnabled) return;
        _changeSampler.Interval = ChangeDelay;
        _changeSampler.Start();
    }

    private void OnBackdropScrolled(object sender, ScrollChangedEventArgs e) => RequestSample();  // also new or removed rows (extent)
    private void OnBackdropResized(object sender, SizeChangedEventArgs e) => RequestSample();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestSample();

    /// <summary>
    /// What counts as a change: anything scrolling (or rows coming and going) inside the backdrop, the backdrop or the
    /// panel resizing, the panel moving over it (<see cref="Align"/>), showing again, and theme changes (<see cref="Apply"/>).
    /// Not seen: content changing in place, such as a thumbnail loading; the timer catches those.
    /// </summary>
    private void WatchChanges()
    {
        if (_watchingChanges) return;
        _watchingChanges = true;
        if (_source is UIElement source)
        {
            _watchedSource = source;
            source.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnBackdropScrolled), true);
            if (source is FrameworkElement fe) fe.SizeChanged += OnBackdropResized;
        }
        IsVisibleChanged += OnVisibilityChanged;
    }

    private void UnwatchChanges()
    {
        if (!_watchingChanges) return;
        _watchingChanges = false;
        if (_watchedSource is { } source)
        {
            source.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnBackdropScrolled));
            if (source is FrameworkElement fe) fe.SizeChanged -= OnBackdropResized;
            _watchedSource = null;
        }
        IsVisibleChanged -= OnVisibilityChanged;
    }

    // ── Adaptive contrast ──
    // A few times a second the area behind the panel is rendered into a tiny bitmap. For every sample the glass colour
    // is worked out (backdrop through the shader's colour shift, under the material tint) and from it the least scrim
    // that gives the content's text colours a readable contrast ratio there. The result is a map, not one number: a
    // bright sidebar item behind one label of a dialog gets a wash right there while the rest stays clear.

    /// <summary>Whether this panel dims: <see cref="AdaptiveContrast"/>, the user's switch (Settings → Appearance →
    /// Liquid Glass, <see cref="Models.ThemeSettings.LiquidGlassAutoDim"/>) and a live backdrop to sample.</summary>
    private bool AutoDims => AdaptiveContrast && _brush != null && (ThemeService.Current?.LiquidGlassAutoDim ?? true);

    /// <summary>Starts or stops sampling for the user's switch and the sampling mode (called from <see cref="Apply"/>
    /// and when settings change).</summary>
    private void ApplyAutoDim()
    {
        _dimVersion++;  // colours, strength or the text-only switch may have changed: the next sample works the map out afresh
        if (!AutoDims)
        {
            _sampler.Stop();
            _changeSampler.Stop();
            UnwatchChanges();
            ClearScrim(animate: true);
            return;
        }

        if (!UsesTimer) _sampler.Stop();
        else if (!_sampler.IsEnabled)
        {
            _sampler.Interval = SampleInterval;
            _sampler.Start();
        }

        if (UsesChanges) WatchChanges();
        else UnwatchChanges();

        // Something changed (theme, strength, mode): re-check soon in every mode, so the strength slider's preview
        // follows while it is dragged. Not before the first sample, which waits for the opening animation.
        if (_sampledOnce) ScheduleSample();
    }

    private const int SampleLong = 24;  // samples along the longer side; the shorter side follows the panel's shape
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxSampleInterval = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan FirstSampleDelay = TimeSpan.FromMilliseconds(1200);
    private const double PrimaryContrast = 4.5;    // WCAG AA for body text (TextPrimary)
    private const double SecondaryContrast = 3.0;  // secondary labels (TextSec)
    private const double MaxScrim = 0.92;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void FindVideos(DependencyObject d, List<MediaElement> into)
    {
        if (d is MediaElement m) { into.Add(m); return; }
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++)
            FindVideos(VisualTreeHelper.GetChild(d, i), into);
    }

    /// <summary>Samples the backdrop and updates the scrim; true when the backdrop looked different from last time.</summary>
    private bool SampleBackdrop()
    {
        if (_source == null || !IsVisible || ActualWidth * ActualHeight < 4000)
        {
            _lastPx = null;
            ClearScrim(animate: true);
            return false;
        }
        // Nobody sees a minimized window, and its off-screen render would be wasted.
        if (_myWindow?.WindowState == WindowState.Minimized || _sourceWindow?.WindowState == WindowState.Minimized) return false;
        try
        {
            if (PresentationSource.FromVisual(_source) == null || PresentationSource.FromVisual(this) == null) return false;
            var a = _source.PointFromScreen(PointToScreen(new Point(0, 0)));
            var b = _source.PointFromScreen(PointToScreen(new Point(ActualWidth, ActualHeight)));
            var box = new Rect(a, b);
            if (box.Width <= 0 || box.Height <= 0) return false;

            _sampledOnce = true;
            if (_sampleCount++ % 12 == 0)
            {
                _videos = new List<MediaElement>();
                FindVideos(_source, _videos);
            }

            // Wide bars get a wide grid, so a thumbnail behind one end of the text isn't averaged into the whole bar.
            int sw = SampleLong, sh = SampleLong;
            if (box.Width >= box.Height) sh = Math.Clamp((int)Math.Round(SampleLong * box.Height / box.Width), 4, SampleLong);
            else sw = Math.Clamp((int)Math.Round(SampleLong * box.Width / box.Height), 4, SampleLong);

            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(_base.Background, null, new Rect(0, 0, sw, sh));
                ctx.DrawRectangle(new VisualBrush(_source) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = InBrushSpace(_source, box), Stretch = Stretch.Fill },
                    null, new Rect(0, 0, sw, sh));
                // Off-screen rendering leaves video black; count a showing video as bright, busy footage instead.
                foreach (var video in _videos)
                {
                    if (!video.IsVisible || !video.HasVideo || video.ActualWidth <= 0) continue;
                    var r = video.TransformToAncestor(_source).TransformBounds(new Rect(0, 0, video.ActualWidth, video.ActualHeight));
                    r.Intersect(box);
                    if (r.IsEmpty) continue;
                    double kx = sw / box.Width, ky = sh / box.Height;
                    ctx.DrawRectangle(VideoStandIn, null, new Rect((r.X - box.X) * kx, (r.Y - box.Y) * ky, r.Width * kx, r.Height * ky));
                }
                if (_overlayHost.Child != null && _overlayHost.IsVisible)
                    ctx.DrawRectangle(new VisualBrush(_overlayHost)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute, Viewbox = InBrushSpace(_overlayHost, new Rect(0, 0, ActualWidth, ActualHeight)),
                        Stretch = Stretch.Fill
                    }, null, new Rect(0, 0, sw, sh));
            }
            if (_sampleBitmap == null || _sampleBitmap.PixelWidth != sw || _sampleBitmap.PixelHeight != sh)
                _sampleBitmap = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            _sampleBitmap.Clear();
            _sampleBitmap.Render(dv);

            var px = new byte[sw * sh * 4];
            _sampleBitmap.CopyPixels(px, sw * 4, 0);
            bool textOnly = ThemeService.Current?.LiquidGlassDimTextOnly == true;

            // Same pixels, same settings: the map is the same too. Only text-only mode still has work to do, since
            // the text on the glass may have moved or changed.
            bool changed = _lastPx == null || !px.AsSpan().SequenceEqual(_lastPx) || _lastDimVersion != _dimVersion;
            if (!changed && !textOnly) return false;
            if (changed || _lastNeed == null)
            {
                _lastNeed = ScrimNeeded(px, sw, sh);
                _lastPx = px;
                _lastDimVersion = _dimVersion;
            }

            var need = _lastNeed;
            if (textOnly)
            {
                need = (double[])need.Clone();
                KeepOnlyBehindText(need, sw, sh);
            }
            ShowScrim(need, sw, sh);
            return changed;
        }
        catch (InvalidOperationException)
        {
            // The backdrop's window went away; keep the last state.
            return false;
        }
    }

    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb From(Color c) => new(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        public Rgb Toward(Rgb o, double t) => new(R + (o.R - R) * t, G + (o.G - G) * t, B + (o.B - B) * t);

        /// <summary>WCAG relative luminance (sRGB in, linear light out).</summary>
        public double Luminance => 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);

        // The sRGB curve from a table (linear between entries, off by < 1e-6): the scrim search evaluates it
        // hundreds of thousands of times per sample, and Math.Pow dominated it.
        private const int LinSteps = 4096;
        private static readonly double[] LinTable = BuildLinTable();

        private static double[] BuildLinTable()
        {
            var t = new double[LinSteps + 2];
            for (int i = 0; i <= LinSteps; i++)
            {
                double c = (double)i / LinSteps;
                t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            t[LinSteps + 1] = t[LinSteps];
            return t;
        }

        private static double Lin(double c)
        {
            double x = Math.Clamp(c, 0, 1) * LinSteps;
            int i = (int)x;
            return LinTable[i] + (LinTable[i + 1] - LinTable[i]) * (x - i);
        }
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private Color ResourceColor(string key, Color fallback) => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    /// <summary>Per sample, the scrim opacity that keeps the content readable over the sampled backdrop (Pbgra32, opaque).</summary>
    private double[] ScrimNeeded(byte[] px, int w, int h)
    {
        int count = w * h;
        bool light = ThemeService.IsLight;
        var bgColor = (_base.Background as SolidColorBrush)?.Color ?? (light ? Colors.White : Colors.Black);
        var tintColor = (_tint.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        var primaryColor = ResourceColor("TextPrimary", light ? Colors.Black : Colors.White);
        var secondaryColor = ResourceColor("TextSec", primaryColor);

        Rgb bg = Rgb.From(bgColor), tint = Rgb.From(tintColor), primary = Rgb.From(primaryColor), secondary = Rgb.From(secondaryColor);
        double tintA = tintColor.A / 255.0, primaryA = primaryColor.A / 255.0, secondaryA = secondaryColor.A / 255.0;

        // The user's strength (Text readability → Strength): standard (0.5) aims for the contrast above. Softer asks for
        // less and washes less deeply; stronger asks for more, so it also dims spots the standard leaves clear, and may
        // wash a little deeper.
        double k = Math.Clamp(ThemeService.Current?.LiquidGlassDimStrength ?? 0.5, 0, 1);
        double targetScale = k < 0.5 ? 0.55 + 0.9 * k : 1 + 1.8 * (k - 0.5);   // 0.55 … 1 … 1.9
        double depth = k < 0.5 ? 0.5 + k : 1;                                  // 0.5 … 1
        double maxScrim = k > 0.5 ? MaxScrim + 0.1 * (k - 0.5) : MaxScrim;     // … 0.97

        // A theme whose text never reaches the target even on the plain window shouldn't pin the scrim at its maximum.
        double bgL = bg.Luminance;
        double primaryTarget = Math.Min(PrimaryContrast * targetScale, 0.9 * Contrast(bgL, bg.Toward(primary, primaryA).Luminance));
        double secondaryTarget = Math.Min(SecondaryContrast * targetScale, 0.9 * Contrast(bgL, bg.Toward(secondary, secondaryA).Luminance));

        // The answer depends only on the sample's colour (everything else is fixed until _dimVersion changes), and a
        // backdrop is mostly a few flat colours, so most samples are a lookup.
        if (_lastDimVersion != _dimVersion || _needByColor.Count > 8192) _needByColor.Clear();

        var need = new double[count];
        for (int i = 0; i < count; i++)
        {
            int key = (px[i * 4 + 2] << 16) | (px[i * 4 + 1] << 8) | px[i * 4];
            if (_needByColor.TryGetValue(key, out var known)) { need[i] = known; continue; }

            var glass = Shade(new Rgb(px[i * 4 + 2] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4] / 255.0)).Toward(tint, tintA);
            double s = 0;
            for (; s < maxScrim; s += 0.04)
            {
                var c = glass.Toward(bg, s);
                double l = c.Luminance;
                // Translucent text colours blend with what is under them.
                if (Contrast(l, c.Toward(primary, primaryA).Luminance) >= primaryTarget
                    && Contrast(l, c.Toward(secondary, secondaryA).Luminance) >= secondaryTarget)
                    break;
            }
            need[i] = _needByColor[key] = Math.Min(s, maxScrim) * depth;
        }

        // The frost spreads a bright spot over its neighbours, so each sample also covers the ones around it.
        var spread = new double[count];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double m = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h) m = Math.Max(m, need[ny * w + nx]);
                    }
                spread[y * w + x] = m;
            }
        return spread;
    }

    // ── Only behind text (ThemeSettings.LiquidGlassDimTextOnly) ──
    // The map keeps its values only in the cells that text on the glass covers; empty glass stays clear. Text is what
    // the content draws as TextBlocks (labels, icon glyphs, a text box's placeholder) and the text boxes themselves.

    private const double TextMargin = 3;  // around each piece of text, so its edges are covered too

    /// <summary>Zeroes every cell of the map (w × h over the panel) that no text on the glass touches.</summary>
    private void KeepOnlyBehindText(double[] need, int w, int h)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var covered = new bool[need.Length];
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        foreach (UIElement child in Children)
            if (!IsLayer(child)) MarkText(child, covered, w, h, area);
        for (int i = 0; i < need.Length; i++)
            if (!covered[i]) need[i] = 0;
    }

    private void MarkText(DependencyObject d, bool[] covered, int w, int h, Rect area)
    {
        if (d is UIElement { IsVisible: false }) return;

        Rect? text = d switch
        {
            TextBlock tb when tb.Text.Length > 0 => TextExtent(tb),
            TextBoxBase or PasswordBox => new Rect(((FrameworkElement)d).RenderSize),
            _ => null,
        };
        if (text is { } local && d is Visual visual)
        {
            try
            {
                var r = visual.TransformToAncestor(this).TransformBounds(local);
                r.Inflate(TextMargin, TextMargin);
                r.Intersect(area);  // scrolled or clipped out of the panel
                if (!r.IsEmpty)
                {
                    double cw = area.Width / w, ch = area.Height / h;
                    int x0 = Math.Clamp((int)(r.Left / cw), 0, w - 1), x1 = Math.Clamp((int)Math.Ceiling(r.Right / cw) - 1, 0, w - 1);
                    int y0 = Math.Clamp((int)(r.Top / ch), 0, h - 1), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom / ch) - 1, 0, h - 1);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            covered[y * w + x] = true;
                }
            }
            catch (InvalidOperationException)
            {
                // Not connected to the panel right now.
            }
            if (d is TextBoxBase or PasswordBox) return;  // the box covers what is inside it
        }

        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++)
            MarkText(VisualTreeHelper.GetChild(d, i), covered, w, h, area);
    }

    /// <summary>Where a TextBlock's text actually is: a stretched label is wider than its words.</summary>
    private static Rect TextExtent(TextBlock tb)
    {
        var size = tb.RenderSize;
        double width = Math.Clamp(tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right, 0, size.Width);
        double x = tb.TextAlignment switch
        {
            TextAlignment.Center => (size.Width - width) / 2,
            TextAlignment.Right => size.Width - width,
            _ => 0,
        };
        return new Rect(x, 0, width, size.Height);
    }

    /// <summary>The shader's saturation and brightness, applied to a backdrop colour (as in LiquidGlassEffect).</summary>
    private Rgb Shade(Rgb c)
    {
        if (_glassSaturation == 1 && _glassBrightness == 1) return c;
        double l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        static double Clamp01(double v) => Math.Clamp(v, 0, 1);
        return new Rgb(Clamp01((l + (c.R - l) * _glassSaturation) * _glassBrightness),
                       Clamp01((l + (c.G - l) * _glassSaturation) * _glassBrightness),
                       Clamp01((l + (c.B - l) * _glassSaturation) * _glassBrightness));
    }

    /// <summary>Shows a new scrim map (w × h samples over the panel), crossfading from the one shown.</summary>
    private void ShowScrim(double[] need, int w, int h)
    {
        if (need.Max() < 0.04) { ClearScrim(animate: true); return; }
        if (_shownNeed is { } shown && shown.Length == need.Length)
        {
            double diff = 0;
            for (int i = 0; i < need.Length; i++) diff = Math.Max(diff, Math.Abs(need[i] - shown[i]));
            if (diff < 0.06) return;
        }
        _shownNeed = need;

        // White at the needed alpha (premultiplied); stretched smoothly over the panel as the scrim's opacity mask.
        var px = new byte[w * h * 4];
        for (int i = 0; i < need.Length; i++)
        {
            var a = (byte)Math.Round(need[i] * 255);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = px[i * 4 + 3] = a;
        }
        var map = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        map.Freeze();
        var mask = new ImageBrush(map) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(mask, BitmapScalingMode.Linear);
        mask.Freeze();

        var back = _scrims[_frontScrim];
        _frontScrim ^= 1;
        var front = _scrims[_frontScrim];
        front.OpacityMask = mask;
        FadeScrim(front, 1, animate: true);
        FadeScrim(back, 0, animate: true);
    }

    private void ClearScrim(bool animate)
    {
        if (_shownNeed == null && animate) return;
        _shownNeed = null;
        FadeScrim(_scrims[0], 0, animate);
        FadeScrim(_scrims[1], 0, animate);
    }

    private static void FadeScrim(Border scrim, double to, bool animate)
    {
        if (!animate || SettingsService.Current.EnablePerformanceMode)
        {
            scrim.BeginAnimation(OpacityProperty, null);
            scrim.Opacity = to;
            return;
        }
        scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private Visual? ResolveBackdrop()
    {
        if (BackdropSource != null) return BackdropSource;

        // Up to the Popup this panel renders in (if any); its owner sits in the window behind. A popup opened from
        // inside another one (a context menu's submenu) is anchored in that popup, so keep climbing until a window.
        DependencyObject? d = this;
        bool inPopup = false;
        Visual? content = null;
        for (int depth = 0; depth < 4 && d != null; depth++)
        {
            while (d != null && d is not Popup)
                d = LogicalTreeHelper.GetParent(d) ?? (d is Visual ? VisualTreeHelper.GetParent(d) : null);
            if (d is not Popup popup) break;
            inPopup = true;
            var anchor = popup.PlacementTarget ?? popup.TemplatedParent ?? LogicalTreeHelper.GetParent(popup);
            content = anchor != null ? Window.GetWindow(anchor)?.Content as Visual : null;
            if (content != null) break;
            d = anchor;
        }
        // Not in a popup: a window over its owner (dialog, notification) shows the owner.
        if (!inPopup)
            content = Window.GetWindow(this)?.Owner is { IsVisible: true } owner ? owner.Content as Visual : null;
        if (content == null) return null;

        // Sampling the window the panel renders in would include the panel itself.
        var mine = PresentationSource.FromVisual(this);
        var theirs = PresentationSource.FromVisual(content);
        return mine != null && theirs != null && !ReferenceEquals(mine, theirs) ? content : null;
    }

    // ── Keeping the backdrop aligned ──
    // The VisualBrush must show exactly what lies behind the panel, so it follows the panel over the backdrop (AimBrush).
    // A per-frame hook would do that, but it also keeps WPF rendering at 60 fps for as long as any glass is open. So
    // the hook runs only while the panel moves (or may start to): a layout pass, input, a window moving or the panel
    // showing arms it, and it unhooks itself after AlignIdleFrames still frames. A slow shared poll catches what
    // arrives without any of those (a render-transform animation started by a timer) within IdlePollInterval.

    private const int AlignIdleFrames = 30;  // ~0.5 s at 60 fps
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly List<LiquidGlassPanel> Tracked = new();
    private static DispatcherTimer? _idlePoll;
    private static bool _staticHooks;

    /// <summary>Whether one of the app's windows has focus (sampling slows down in the background).</summary>
    private static bool AppActive { get; set; } = true;

    private static void EnsureStaticHooks()
    {
        if (_staticHooks) return;
        _staticHooks = true;
        InputManager.Current.PostProcessInput += (_, _) =>
        {
            foreach (var panel in Tracked) panel.ArmAlign();
        };
        if (Application.Current is { } app)
        {
            app.Activated += (_, _) =>
            {
                AppActive = true;
                foreach (var panel in Tracked) panel.NudgeSampler();
            };
            app.Deactivated += (_, _) => AppActive = false;
        }
        _idlePoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdlePollInterval };
        _idlePoll.Tick += (_, _) =>
        {
            foreach (var panel in Tracked.ToArray())
                if (!panel._alignHooked && panel.UpdateViewbox()) panel.ArmAlign();
        };
    }

    private void StartTracking()
    {
        EnsureStaticHooks();
        if (!Tracked.Contains(this)) Tracked.Add(this);
        _idlePoll!.Start();
        LayoutUpdated += OnLayoutUpdated;
        IsVisibleChanged += OnTrackedVisibilityChanged;
        _myWindow = Window.GetWindow(this);
        _sourceWindow = _source is DependencyObject s ? Window.GetWindow(s) : null;
        if (_myWindow != null) _myWindow.LocationChanged += OnWindowMoved;
        if (_sourceWindow != null && _sourceWindow != _myWindow) _sourceWindow.LocationChanged += OnWindowMoved;
        ArmAlign();
    }

    private void StopTracking()
    {
        Tracked.Remove(this);
        if (Tracked.Count == 0) _idlePoll?.Stop();
        LayoutUpdated -= OnLayoutUpdated;
        IsVisibleChanged -= OnTrackedVisibilityChanged;
        if (_myWindow != null) _myWindow.LocationChanged -= OnWindowMoved;
        if (_sourceWindow != null) _sourceWindow.LocationChanged -= OnWindowMoved;
        _myWindow = _sourceWindow = null;
        if (_alignHooked)
        {
            _alignHooked = false;
            CompositionTarget.Rendering -= Align;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => ArmAlign();
    private void OnWindowMoved(object? sender, EventArgs e) => ArmAlign();

    private void OnTrackedVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ArmAlign();
        if (IsVisible) NudgeSampler();
    }

    /// <summary>Follows the panel every frame until it has stood still for a while.</summary>
    private void ArmAlign()
    {
        _alignIdleFrames = 0;
        if (_alignHooked || _brush == null) return;
        _alignHooked = true;
        CompositionTarget.Rendering += Align;
    }

    private void Align(object? sender, EventArgs e)
    {
        if (UpdateViewbox()) _alignIdleFrames = 0;
        else if (++_alignIdleFrames > AlignIdleFrames)
        {
            _alignHooked = false;
            CompositionTarget.Rendering -= Align;
        }
    }

    /// <summary>Points the brush at what lies behind the panel now; true when that moved.</summary>
    private bool UpdateViewbox()
    {
        if (_brush == null || _source == null || _picture.ActualWidth <= 0 || !IsVisible) return false;
        try
        {
            var theirs = PresentationSource.FromVisual(_source);
            var mine = PresentationSource.FromVisual(_backdrop);
            if (theirs == null || mine == null) return false;
            // Where the picture sits before its own shift.
            var size = _picture.RenderSize;
            var at = new Rect((Point)VisualTreeHelper.GetOffset(_picture), size);
            Rect box;
            if (ReferenceEquals(theirs, mine))
                box = _backdrop.TransformToVisual(_source).TransformBounds(at);  // same window: no trip through the screen
            else
                box = new Rect(_source.PointFromScreen(_backdrop.PointToScreen(at.TopLeft)),
                               _source.PointFromScreen(_backdrop.PointToScreen(at.BottomRight)));
            box = InBrushSpace(_source, box);
            if (box == _lastBox) return false;
            _lastBox = box;
            AimBrush(box, size);
            RequestSample();  // the panel moved over its backdrop (only while sampling on changes)
            NudgeSampler();
            return true;
        }
        catch (InvalidOperationException)
        {
            // The backdrop's window went away; the glass keeps its last frame.
            return false;
        }
    }

    /// <summary>
    /// A VisualBrush draws its visual with the visual's own offset (Margin, layout position) and RenderTransform, so an
    /// absolute Viewbox counts in the parent's units, not the visual's: moves <paramref name="local"/> there. Without
    /// it a backdrop with a margin showed shifted by that margin (the Multi Downloader's queue, 12 px to the right).
    /// </summary>
    private static Rect InBrushSpace(Visual visual, Rect local)
    {
        if (VisualTreeHelper.GetTransform(visual) is { } t) local = t.TransformBounds(local);
        local.Offset(VisualTreeHelper.GetOffset(visual));
        return local;
    }

    // ── Aiming the brush ──
    // The cached picture shows the backdrop region in the brush's Viewbox. Aiming it anew means rendering that region
    // again, so while the panel moves, grows or scales (an opening spring, a banner sliding into the stack) the picture
    // is shifted and scaled to where the backdrop lies now, for as long as it still covers the glass and the blur's
    // overscan; past PictureMargin it is aimed afresh. On screen the backdrop keeps its size either way.

    /// <summary>Shows <paramref name="box"/> (the region behind the picture, in the backdrop's units) on the picture.</summary>
    private void AimBrush(Rect box, Size size)
    {
        if (_brush == null || box.Width <= 0 || box.Height <= 0 || size.Width <= 0 || size.Height <= 0) return;
        if (_cachePicture && !_brushBase.IsEmpty && _brushBaseSize == size)
        {
            double kx = size.Width / box.Width, ky = size.Height / box.Height;  // picture units per backdrop unit
            // Maps where a backdrop point is drawn with the brush as aimed onto where it lies behind the panel now.
            var shift = new Matrix(_brushBase.Width / box.Width, 0, 0, _brushBase.Height / box.Height,
                (_brushBase.X - box.X) * kx, (_brushBase.Y - box.Y) * ky);
            var needed = new Rect(PictureMargin, PictureMargin,
                Math.Max(0, size.Width - 2 * PictureMargin), Math.Max(0, size.Height - 2 * PictureMargin));
            if (Rect.Transform(new Rect(size), shift).Contains(needed))
            {
                _pictureShift.Matrix = shift;
                return;
            }
        }

        _brushBase = box;
        _brushBaseSize = size;
        _pictureShift.Matrix = Matrix.Identity;
        _brush.Viewbox = box;
    }

    /// <summary>Brings a backed-off sampling timer back to its normal pace. Only shortens it, so a steady stream of
    /// changes can't keep restarting the countdown.</summary>
    private void NudgeSampler()
    {
        if (_sampler.IsEnabled && _sampler.Interval > SampleInterval) _sampler.Interval = SampleInterval;
    }

    /// <summary>The explicit <see cref="CornerRadius"/> or the tuned one, never more than half the short side.</summary>
    private double EffectiveRadius(GlassParams p)
    {
        var r = double.IsNaN(CornerRadius) ? p.CornerRadius : CornerRadius;
        return ActualWidth > 0 ? Math.Min(r, Math.Min(ActualWidth, ActualHeight) / 2) : r;
    }

    private void UpdateClip() => UpdateClip(EffectiveRadius(GlassTuning.Effective(Surface)));

    private void UpdateClip(double radius)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        bool l = IsLShape;
        if (l)
        {
            var shape = BuildLShape(radius);
            Clip = shape;
            _shapeOutline.Data = shape;   // the clip keeps the inner half of each stroke
            _shapeRim.Data = shape;
        }
        else
        {
            Clip = SquircleBorder.CreateAppleStyleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius);
        }
        _outline.Visibility = l ? Visibility.Collapsed : Visibility.Visible;
        _shapeOutline.Visibility = l ? Visibility.Visible : Visibility.Collapsed;
        // The shader lenses and lights the L itself; the frosted fallback gets a traced rim instead.
        if (_effect != null) _effect.Arms = l ? ReverseLArms : new Size(0, 0);
        _shapeRim.Visibility = l && _host.Effect == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Just the radius-dependent parts, cheap enough for every frame of a corner animation.</summary>
    private void ApplyRadius()
    {
        var r = EffectiveRadius(GlassTuning.Effective(Surface));
        _rim.CornerRadius = r;
        _outline.CornerRadius = r;
        if (_effect != null) _effect.CornerRadius = r;
        UpdateClip(r);
    }

    /// <summary>Applies the surface's current tuning to every layer.</summary>
    private void Apply()
    {
        var p = GlassTuning.Effective(Surface);
        bool light = ThemeService.IsLight;

        ApplyRadius();
        if (_attached) ApplyAutoDim();
        // Dark mode: a dark drop shadow under light text. Light mode: dark text, so a soft white halo instead.
        _textShadow.Color = light ? Colors.White : Colors.Black;
        _textShadow.Opacity = light ? 0.55 : 0.45;
        _textShadow.ShadowDepth = light ? 0 : 1;
        _sheenStop.Color = Color.FromArgb(ToByte(p.SheenAlpha), 0xFF, 0xFF, 0xFF);
        if (TryFindResource("BgDeep") is SolidColorBrush bg)
        {
            _base.Background = new SolidColorBrush(Color.FromRgb(bg.Color.R, bg.Color.G, bg.Color.B));
            _scrims[0].Background = _scrims[1].Background = _base.Background;
        }

        // Style setters freeze their effects, so the parent's shadow is replaced rather than edited.
        if (VisualTreeHelper.GetParent(this) is UIElement { Effect: DropShadowEffect } parent)
            parent.Effect = new DropShadowEffect
            {
                BlurRadius = p.ShadowBlur, ShadowDepth = p.ShadowDepth, Direction = 270,
                Color = Colors.Black, Opacity = p.ShadowOpacity
            };

        if (SettingsService.Current.EnablePerformanceMode)
        {
            _host.Effect = null;
            _host.Margin = new Thickness(0);
            _picture.Visibility = Visibility.Collapsed;
            _overlayHost.Visibility = Visibility.Collapsed;
            _rim.Visibility = Visibility.Collapsed;
            _tint.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            return;
        }
        _picture.Visibility = Visibility.Visible;
        _overlayHost.Visibility = Visibility.Visible;

        if (p.Liquid && _brush != null)
            _effect ??= LiquidGlassEffect.TryCreate();
        bool liquid = p.Liquid && _brush != null && _effect != null;
        _glassSaturation = liquid ? p.Saturation : 1;
        _glassBrightness = liquid ? p.Brightness : 1;

        if (liquid)
        {
            // The host must reach past the furthest point the lens and the reflection sample.
            var inset = Math.Max(p.Inset, Math.Max(p.Refraction, p.Reflection > 0 ? p.ReflectionReach : 0) + 2);
            _effect!.CornerRadius = EffectiveRadius(p);
            _effect.Inset = inset;
            _effect.Saturation = p.Saturation;
            _effect.Brightness = p.Brightness;
            _effect.Frost = p.Frost;
            _effect.Refraction = p.Refraction;
            _effect.Bevel = p.Bevel;
            _effect.Dispersion = p.Dispersion;
            _effect.Highlight = p.Highlight;
            _effect.Reflection = p.Reflection;
            _effect.ReflectionReach = p.ReflectionReach;
            _effect.ReflectionWidth = p.ReflectionWidth;
            _host.Margin = new Thickness(-inset);
            _overlayHost.Margin = new Thickness(inset);  // back on the panel's bounds
            _host.Effect = _effect;
            if (_host.ActualWidth > 0) _effect.Size = new Size(_host.ActualWidth, _host.ActualHeight);
            SetBackdropBlur(p.BackdropBlur);  // just softens; the shader does the frost
            _rim.Visibility = Visibility.Collapsed;
        }
        else
        {
            _host.Margin = new Thickness(0);
            _overlayHost.Margin = new Thickness(0);
            _host.Effect = null;
            SetBackdropBlur(p.FallbackBlur);
            _rim.Visibility = IsLShape ? Visibility.Collapsed : Visibility.Visible;  // an L traces its own rim
        }

        UpdateClip(EffectiveRadius(p));

        // Liquid glass needs only a whisper of tint; the frosted fallback needs a denser one to stay legible.
        var alpha = ToByte(liquid ? p.TintAlpha : p.FallbackTintAlpha);
        _tint.Background = new SolidColorBrush(light
            ? Color.FromArgb(alpha, 0xF8, 0xF8, 0xFA)
            : Color.FromArgb(alpha, 0x1C, 0x1C, 0x20));
    }

    /// <summary>
    /// The blur reaches only as far past the glass as its kernel does (a fixed 40 px overscan made small surfaces
    /// copy and blur several times their own area), and a blur too small to see is no effect at all, which saves
    /// an off-screen pass whenever the backdrop redraws. The picture reaches PictureMargin further, for AimBrush.
    /// </summary>
    private void SetBackdropBlur(double radius)
    {
        double overscan = 0;
        if (radius < 0.5)
            _blur.Effect = null;
        else
        {
            _blurEffect.Radius = radius;
            _blur.Effect = _blurEffect;
            overscan = Math.Ceiling(radius * 1.5 + 2);
        }
        _picture.Margin = new Thickness(-(overscan + (_cachePicture ? PictureMargin : 0)));
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
