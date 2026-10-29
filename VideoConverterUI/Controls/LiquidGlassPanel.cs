using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    // The last sample's pixels and what was worked out from them (the glass colour per sample, the whole-glass map):
    // an unchanged backdrop skips the maths, and the timer backs off (SampleInterval … MaxSampleInterval) while
    // nothing changes.
    private byte[]? _lastPx;
    private Rgb[]? _lastGlass;
    private double[]? _lastNeed;
    private int _dimVersion, _lastDimVersion = -1;  // bumped whenever what the map depends on (colours, strength) may have changed
    private readonly Dictionary<int, double> _needByColor = new();  // scrim per backdrop colour, for the current _dimVersion
    private readonly Dictionary<long, double> _needByText = new();  // scrim per backdrop colour and text colour (only behind text)
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

    /// <summary>Keep the content readable over a backdrop that gets too bright (dark mode) or too dark (light mode):
    /// the user's Auto Dim and Text Color (on by default; panels too small to hold text never sample).</summary>
    public bool AdaptiveContrast
    {
        get => (bool)GetValue(AdaptiveContrastProperty);
        set => SetValue(AdaptiveContrastProperty, value);
    }

    public static readonly DependencyProperty ReadabilityProperty = DependencyProperty.Register(
        nameof(Readability), typeof(GlassReadability), typeof(LiquidGlassPanel),
        new PropertyMetadata(GlassReadability.Settings, (d, _) =>
        {
            var panel = (LiquidGlassPanel)d;
            if (panel._attached) panel.ApplyAutoDim();
        }));

    /// <summary>Which of the user's readability options this panel follows: both (default), or one alone (the
    /// previews on the Auto Dim and Text Color pages each show their own).</summary>
    public GlassReadability Readability
    {
        get => (GlassReadability)GetValue(ReadabilityProperty);
        set => SetValue(ReadabilityProperty, value);
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

    // Only content children, and only those without an effect of their own. A child with text that Text Color turned
    // to the other end of the grey scale goes without for the time being (a dark shadow under dark text blurs it).
    private void ShadowContent(UIElement child)
    {
        if (IsLayer(child)) return;
        if (TextShadow && !_unshadowed.Contains(child)) { if (child.Effect == null) child.Effect = _textShadow; }
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
        RestoreText(animate: false);
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
        RestoreText(animate: false);
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
            if (Adapts)
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
    // is worked out (backdrop through the shader's colour shift, under the material tint), and from it two things the
    // user can turn on separately (Settings → Appearance → Liquid Glass):
    //   Auto Dim: the least scrim that gives the text a readable contrast ratio there. The result is a map, not one
    //     number: a bright sidebar item behind one label of a dialog gets a wash right there while the rest stays clear.
    //   Text Color: grey text that would be hard to read turns to the other end of the grey scale (or full strength).
    // With both on, dimming the whole glass comes first and text colours are chosen over the dimmed glass; dimming only
    // behind text, text changes colour first where that is enough and the glass dims behind the rest.

    /// <summary>Whether this panel dims: <see cref="AdaptiveContrast"/>, the user's switch (Settings → Appearance →
    /// Liquid Glass → Auto Dim, <see cref="Models.ThemeSettings.LiquidGlassAutoDim"/>) and a live backdrop to sample.</summary>
    private bool DimsGlass => AdaptiveContrast && _brush != null && Readability != GlassReadability.TextColorOnly
                              && (ThemeService.Current?.LiquidGlassAutoDim ?? true);

    /// <summary>Whether text on the panel changes colour (Settings → Appearance → Liquid Glass → Text Color,
    /// <see cref="Models.ThemeSettings.LiquidGlassAdaptiveText"/>).</summary>
    private bool AdaptsText => AdaptiveContrast && _brush != null && Readability != GlassReadability.AutoDimOnly
                               && ThemeService.Current?.LiquidGlassAdaptiveText == true;

    /// <summary>Whether this panel samples its backdrop at all.</summary>
    private bool Adapts => DimsGlass || AdaptsText;

    /// <summary>Starts or stops sampling for the user's switches and the sampling mode (called from <see cref="Apply"/>
    /// and when settings change).</summary>
    private void ApplyAutoDim()
    {
        _dimVersion++;  // colours, strength or the text-only switch may have changed: the next sample works the map out afresh
        if (!Adapts)
        {
            _sampler.Stop();
            _changeSampler.Stop();
            UnwatchChanges();
            ClearScrim(animate: true);
            RestoreText(animate: true);
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
    private const double DesignShare = 0.85;       // text is never asked for more than this share of its contrast on the plain window
    private const double MaxScrim = 0.92;

    /// <summary>A strength slider (0 softer, 0.5 standard, 1 stronger) as a factor on the contrast text should reach:
    /// 0.6 … 1 (WCAG AA) … 1.55 (about AAA).</summary>
    private static double StrengthScale(double k)
    {
        k = Math.Clamp(k, 0, 1);
        return k < 0.5 ? 0.6 + 0.8 * k : 1 + 1.1 * (k - 0.5);
    }

    /// <summary>
    /// The contrast text of this colour should reach on the glass: the WCAG goal for body text (4.5:1), or for secondary
    /// text (3:1; text that is weaker even on the plain window), times the strength, but never more than
    /// <see cref="DesignShare"/> of what it has on the plain glass. A theme's grey label can't be asked for more than it
    /// was made to have: without that cap, Stronger turned every grey label white, even on dark glass.
    /// </summary>
    private static double TargetFor(Rgb text, double alpha, Rgb plain, double scale)
    {
        double design = Contrast(plain.Luminance, plain.Toward(text, alpha).Luminance);
        double goal = design >= 7 ? PrimaryContrast : SecondaryContrast;
        return Math.Max(1.2, Math.Min(goal * scale, DesignShare * design));
    }

    /// <summary>The glass over the window's own colour: what the theme's text colours were made for.</summary>
    private Rgb PlainGlass()
    {
        var bgColor = (_base.Background as SolidColorBrush)?.Color ?? (ThemeService.IsLight ? Colors.White : Colors.Black);
        var tintColor = (_tint.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        return Shade(Rgb.From(bgColor)).Toward(Rgb.From(tintColor), tintColor.A / 255.0);
    }

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
            RestoreText(animate: true);
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

            bool changed = _lastPx == null || _lastGlass == null || _lastDimVersion != _dimVersion
                           || !px.AsSpan().SequenceEqual(_lastPx);
            if (changed)
            {
                _lastPx = px;
                _lastDimVersion = _dimVersion;
                _lastGlass = GlassColors(px, sw * sh);
                _lastNeed = null;
            }
            bool dims = DimsGlass, adapts = AdaptsText;
            bool textOnly = dims && ThemeService.Current?.LiquidGlassDimTextOnly == true;
            // Same pixels, same settings: the whole-glass map is the same too. Text may have moved or changed, though.
            if (!changed && !adapts && !textOnly) return false;

            // Dimming the whole glass comes first: text colours are then chosen over the dimmed glass.
            var glass = _lastGlass!;
            double[]? wash = null;
            if (dims && !textOnly)
            {
                wash = _lastNeed ??= ScrimNeeded(px, sw, sh);
                if (adapts) glass = Dimmed(glass, wash);
            }

            // Text changes colour; dimming only behind text, only where that is enough (the wash takes the rest).
            var texts = adapts || textOnly ? FindText(new Rect(0, 0, ActualWidth, ActualHeight)) : null;
            if (adapts) AdaptText(texts!, glass, sw, sh, mustRead: textOnly);
            else RestoreText(animate: true);

            if (!dims) ClearScrim(animate: true);
            else if (textOnly) ShowScrim(WashBehindText(texts!, px, _lastGlass!, sw, sh, out int mw, out int mh), mw, mh);
            else ShowScrim(wash!, sw, sh);
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

    /// <summary>Auto Dim's strength (Settings → Appearance → Liquid Glass → Auto Dim → Strength): standard (0.5) aims
    /// for the contrast in <see cref="TargetFor"/>. Softer asks for less and washes less deeply; stronger asks for more,
    /// so it also dims spots the standard leaves clear, and may wash a little deeper.</summary>
    private static (double Scale, double Depth, double MaxScrim) DimStrength()
    {
        double k = Math.Clamp(ThemeService.Current?.LiquidGlassDimStrength ?? 0.42, 0, 1);
        return (StrengthScale(k), k < 0.5 ? 0.5 + k : 1, k > 0.5 ? MaxScrim + 0.1 * (k - 0.5) : MaxScrim);
    }

    /// <summary>The glass under a wash (per sample).</summary>
    private Rgb[] Dimmed(Rgb[] glass, double[] wash)
    {
        var bg = Rgb.From((_base.Background as SolidColorBrush)?.Color ?? Colors.Black);
        var dimmed = new Rgb[glass.Length];
        for (int i = 0; i < glass.Length; i++) dimmed[i] = glass[i].Toward(bg, wash[i]);
        return dimmed;
    }

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

        // Every spot of the glass may hold either kind of text, so it has to suit both.
        var (scale, depth, maxScrim) = DimStrength();
        var plain = PlainGlass();
        double primaryTarget = TargetFor(primary, primaryA, plain, scale);
        double secondaryTarget = TargetFor(secondary, secondaryA, plain, scale);

        // The answer depends only on the sample's colour (everything else is fixed until _dimVersion changes), and a
        // backdrop is mostly a few flat colours, so most samples are a lookup.
        CheckScrimCaches();

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

    private int _scrimCacheVersion = -1;

    /// <summary>Forgets the cached scrims when what they depend on (colours, strength) may have changed, or when they
    /// have grown too big.</summary>
    private void CheckScrimCaches()
    {
        if (_scrimCacheVersion == _dimVersion && _needByColor.Count <= 8192 && _needByText.Count <= 16384) return;
        _scrimCacheVersion = _dimVersion;
        _needByColor.Clear();
        _needByText.Clear();
    }

    // ── The text on the glass ──
    // What the content draws as TextBlocks (labels, icon glyphs, a text box's placeholder) and the text boxes themselves,
    // on the glass itself: text on a fill of its own (an accent button) or on nested glass doesn't need the glass's help.

    private const double TextMargin = 3;  // around each piece of text, so its edges are covered too

    /// <summary>A piece of text on the glass: where it is (in the panel, with <see cref="TextMargin"/> around it), and
    /// the content child it is in (which carries the text shadow).</summary>
    private readonly record struct GlassText(FrameworkElement Element, Rect Bounds, UIElement Root);

    private List<GlassText> FindText(Rect area)
    {
        var found = new List<GlassText>();
        foreach (UIElement child in Children)
            if (!IsLayer(child)) FindText(child, child, area, found);
        return found;
    }

    private void FindText(DependencyObject d, UIElement root, Rect area, List<GlassText> into)
    {
        if (d is UIElement { IsVisible: false }) return;
        if (HasOwnSurface(d)) return;

        Rect? text = d switch
        {
            TextBlock tb when tb.Text.Length > 0 => TextExtent(tb),
            TextBoxBase or PasswordBox => new Rect(((FrameworkElement)d).RenderSize),
            _ => null,
        };
        if (text is { } local && d is FrameworkElement element)
        {
            try
            {
                var r = element.TransformToAncestor(this).TransformBounds(local);
                r.Inflate(TextMargin, TextMargin);
                r.Intersect(area);  // scrolled or clipped out of the panel
                if (!r.IsEmpty) into.Add(new GlassText(element, r, root));
            }
            catch (InvalidOperationException)
            {
                // Not connected to the panel right now.
            }
            if (d is TextBoxBase or PasswordBox) return;  // the box covers what is inside it
        }

        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++)
            FindText(VisualTreeHelper.GetChild(d, i), root, area, into);
    }

    /// <summary>Whether <paramref name="d"/> draws a surface of its own that its text sits on instead of the glass:
    /// a mostly opaque fill (an accent button), or another glass panel (which looks after its own text).</summary>
    private bool HasOwnSurface(DependencyObject d)
    {
        if (d is LiquidGlassPanel && d != this) return true;
        var fill = d switch
        {
            Panel p => p.Background,
            Border b => b.Background,
            Control c => c.Background,
            _ => null,
        };
        return fill switch
        {
            null => false,
            SolidColorBrush s => s.Color.A * s.Opacity > 150,
            _ => fill.Opacity > 0.6,  // gradients and pictures
        };
    }

    /// <summary>The sample cells (of w × h over the panel) that a rectangle of the panel touches.</summary>
    private (int X0, int Y0, int X1, int Y1) CellsUnder(Rect r, int w, int h)
    {
        double cw = ActualWidth / w, ch = ActualHeight / h;
        return (Math.Clamp((int)(r.Left / cw), 0, w - 1), Math.Clamp((int)(r.Top / ch), 0, h - 1),
                Math.Clamp((int)Math.Ceiling(r.Right / cw) - 1, 0, w - 1), Math.Clamp((int)Math.Ceiling(r.Bottom / ch) - 1, 0, h - 1));
    }

    private static Color? ShownColor(FrameworkElement element) =>
        (element.GetValue(TextElement.ForegroundProperty) as SolidColorBrush)?.Color;

    /// <summary>Whether a colour is at the other end of the grey scale from the theme's text (dark in dark mode).</summary>
    private static bool IsOtherSide(Color c, bool light) => light ? c.R + c.G + c.B > 3 * 128 : c.R + c.G + c.B < 3 * 128;

    // ── Auto Dim only behind text (ThemeSettings.LiquidGlassDimTextOnly) ──
    // Empty glass stays clear. Text that sits together (an icon, a title and its subtitle) gets one soft wash, as deep as
    // its most demanding colour needs over the glass there (a lone title less than a grey line, accent text its own
    // amount), fading out around it. The mask is finer than the samples (MaskDetail per sample along each side), so its
    // edge follows the text instead of the sample grid, which drew blocky patches with stairs at their corners.

    private const int MaskDetail = 4;

    /// <summary>The wash behind the text, as a map of mw × mh over the panel. Text that Text Color turned to the other
    /// end of the grey scale gets none: a wash would only make it harder to read.</summary>
    private double[] WashBehindText(List<GlassText> texts, byte[] px, Rgb[] glass, int w, int h, out int mw, out int mh)
    {
        mw = w * MaskDetail;
        mh = h * MaskDetail;
        var mask = new double[mw * mh];
        if (ActualWidth <= 0 || ActualHeight <= 0) return mask;
        CheckScrimCaches();

        bool light = ThemeService.IsLight;
        var (scale, depth, maxScrim) = DimStrength();
        var plain = PlainGlass();
        var bg = Rgb.From((_base.Background as SolidColorBrush)?.Color ?? (light ? Colors.White : Colors.Black));
        var primary = ResourceColor("TextPrimary", light ? Colors.Black : Colors.White);

        // The text to wash behind, in the colour it shows (or is fading to); other brushes count as body text.
        var items = new List<(Rect Bounds, Rgb Color, double Alpha, double Target, long Key)>();
        foreach (var t in texts)
        {
            _adapted.TryGetValue(t.Element, out var adapted);
            if (adapted != null && IsOtherSide(adapted.Target, light)) continue;
            var color = adapted?.Target ?? ShownColor(t.Element) ?? primary;
            double opacity = t.Element.GetValue(TextElement.ForegroundProperty) is Brush brush ? brush.Opacity : 1;
            double alpha = color.A / 255.0 * opacity;
            if (alpha < 0.15) continue;
            var rgb = Rgb.From(color);
            items.Add((t.Bounds, rgb, alpha, TargetFor(rgb, alpha, plain, scale),
                (long)((uint)ToByte(alpha * 255) << 24 | (uint)(color.R << 16 | color.G << 8 | color.B)) << 24));
        }

        double cw = ActualWidth / w, ch = ActualHeight / h, mcw = ActualWidth / mw, mch = ActualHeight / mh;
        foreach (var group in GroupRects(items.ConvertAll(i => i.Bounds)))
        {
            var box = items[group[0]].Bounds;
            foreach (int i in group) box.Union(items[i].Bounds);
            var colors = group.Select(i => items[i]).DistinctBy(i => i.Key).ToList();

            // The scrim per sample in the box and one ring around it (the frost spreads a bright spot over its
            // neighbours), for the colour there that needs the most; then per sample in the box the most around it.
            var (x0, y0, x1, y1) = CellsUnder(box, w, h);
            int rx0 = Math.Max(0, x0 - 1), ry0 = Math.Max(0, y0 - 1), rx1 = Math.Min(w - 1, x1 + 1), ry1 = Math.Min(h - 1, y1 + 1);
            int rw = rx1 - rx0 + 1;
            var ring = new double[rw * (ry1 - ry0 + 1)];
            for (int y = ry0; y <= ry1; y++)
                for (int x = rx0; x <= rx1; x++)
                {
                    int i = y * w + x;
                    uint backdrop = (uint)((px[i * 4 + 2] << 16) | (px[i * 4 + 1] << 8) | px[i * 4]);
                    double s = 0;
                    foreach (var c in colors)
                    {
                        if (!_needByText.TryGetValue(c.Key | backdrop, out var v))
                            _needByText[c.Key | backdrop] = v = LeastScrim(glass[i], bg, c.Color, c.Alpha, c.Target, maxScrim) * depth;
                        s = Math.Max(s, v);
                    }
                    ring[(y - ry0) * rw + (x - rx0)] = s;
                }
            int nw = x1 - x0 + 1, nh = y1 - y0 + 1;
            var need = new double[nw * nh];
            double most = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double m = 0;
                    for (int ny = Math.Max(ry0, y - 1); ny <= Math.Min(ry1, y + 1); ny++)
                        for (int nx = Math.Max(rx0, x - 1); nx <= Math.Min(rx1, x + 1); nx++)
                            m = Math.Max(m, ring[(ny - ry0) * rw + (nx - rx0)]);
                    need[(y - y0) * nw + (x - x0)] = m;
                    most = Math.Max(most, m);
                }
            if (most < 0.02) continue;

            // Into the mask: in full over the box (between the samples there), fading out within the feather around it.
            double feather = Math.Clamp(Math.Min(box.Width, box.Height) * 0.5, 6, 16);
            int mx0 = Math.Max(0, (int)((box.Left - feather) / mcw)), mx1 = Math.Min(mw - 1, (int)((box.Right + feather) / mcw));
            int my0 = Math.Max(0, (int)((box.Top - feather) / mch)), my1 = Math.Min(mh - 1, (int)((box.Bottom + feather) / mch));
            for (int my = my0; my <= my1; my++)
            {
                double py = (my + 0.5) * mch, dy = Math.Max(Math.Max(box.Top - py, py - box.Bottom), 0);
                double sy = Math.Clamp(py, box.Top, box.Bottom) / ch - 0.5 - y0;
                for (int mx = mx0; mx <= mx1; mx++)
                {
                    double qx = (mx + 0.5) * mcw, dx = Math.Max(Math.Max(box.Left - qx, qx - box.Right), 0);
                    double d = Math.Sqrt(dx * dx + dy * dy) / feather;
                    if (d >= 1) continue;
                    double v = (1 - d * d * (3 - 2 * d)) * Between(need, nw, nh, Math.Clamp(qx, box.Left, box.Right) / cw - 0.5 - x0, sy);
                    int mi = my * mw + mx;
                    if (v > mask[mi]) mask[mi] = v;
                }
            }
        }
        return mask;
    }

    /// <summary>The least scrim (in steps of 0.04, up to <paramref name="max"/>) that lets text of this colour reach the
    /// target on this glass; 0 when it already does, or when a wash wouldn't help it (text on the wash's side).</summary>
    private static double LeastScrim(Rgb glass, Rgb bg, Rgb text, double alpha, double target, double max)
    {
        double Reads(double s)
        {
            var c = glass.Toward(bg, s);
            return Contrast(c.Luminance, c.Toward(text, alpha).Luminance);
        }
        double now = Reads(0);
        if (now >= target || Reads(max) <= now) return 0;
        double scrim = 0.04;
        for (; scrim < max; scrim += 0.04)
            if (Reads(scrim) >= target) break;
        return Math.Min(scrim, max);
    }

    /// <summary>A value between the cells of a small map (nw × nh), at a point in cell units (kept on the map).</summary>
    private static double Between(double[] map, int nw, int nh, double x, double y)
    {
        x = Math.Clamp(x, 0, nw - 1);
        y = Math.Clamp(y, 0, nh - 1);
        int ix = (int)x, iy = (int)y, jx = Math.Min(ix + 1, nw - 1), jy = Math.Min(iy + 1, nh - 1);
        double fx = x - ix, fy = y - iy;
        double top = map[iy * nw + ix] + (map[iy * nw + jx] - map[iy * nw + ix]) * fx;
        double bottom = map[jy * nw + ix] + (map[jy * nw + jx] - map[jy * nw + ix]) * fx;
        return top + (bottom - top) * fy;
    }

    // ── Text Color (ThemeSettings.LiquidGlassAdaptiveText) ──
    // Grey text whose colour would be hard to read where it sits (light text over a bright spot in dark mode, dark text
    // over a dark spot in light mode) changes: to the other end of the grey scale, keeping how strong it is (secondary
    // text stays lighter than primary), or over a mid-grey spot to full strength on its own side, whichever lets all of
    // it be read. Text that sits together (an icon and its label, a title and its subtitle) changes together, so a row
    // never ends up in three colours. Only grey text on the glass itself changes: coloured text and text on a fill of
    // its own keep their colour. The new colour is an animation on the element, so its own value, style or binding is
    // left as it was and comes back when the animation is removed.

    private enum TextScheme { Own, Flipped, Strong }

    private sealed class AdaptedText
    {
        public TextScheme Scheme;
        public Color Target;     // the colour it shows, or fades to
        public UIElement? Root;  // the content child it is in (UpdateTextShadows)
        public int Pass;         // the last AdaptText pass that found this text
    }

    /// <summary>A piece of grey text Text Color may change: its colour in each scheme, the contrast it should reach and
    /// the sample cells under it.</summary>
    private sealed class TextChoice
    {
        public GlassText Text;
        public Color Own, Flipped, Strong;
        public double Alpha, Target;
        public int X0, Y0, X1, Y1;
        public AdaptedText? State;

        public Color In(TextScheme s) => s switch { TextScheme.Flipped => Flipped, TextScheme.Strong => Strong, _ => Own };
    }

    private readonly Dictionary<FrameworkElement, AdaptedText> _adapted = new();
    private readonly HashSet<UIElement> _unshadowed = new();  // content children without the text shadow for now (ShadowContent)
    private int _textPass;
    private const double FlipMargin = 1.2;        // a change must read this much better before text changes (and back)
    private const double GroupGap = 4;            // text this close (past its TextMargin) belongs together…
    private const double MaxGroupHeight = 56;     // … as long as the group stays about two lines tall (not a whole menu)

    /// <summary>Per sample, the colour of the glass (backdrop through the shader's colour shift, under the tint).</summary>
    private Rgb[] GlassColors(byte[] px, int count)
    {
        var tintColor = (_tint.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        Rgb tint = Rgb.From(tintColor);
        double tintA = tintColor.A / 255.0;
        var glass = new Rgb[count];
        for (int i = 0; i < count; i++)
            glass[i] = Shade(new Rgb(px[i * 4 + 2] / 255.0, px[i * 4 + 1] / 255.0, px[i * 4] / 255.0)).Toward(tint, tintA);
        return glass;
    }

    /// <summary>Gives each group of text on the glass the colours that read best over the glass under it (w × h samples).
    /// <paramref name="mustRead"/>: Auto Dim washes behind whatever text keeps its colour, so text only changes when
    /// that makes all of it readable.</summary>
    private void AdaptText(List<GlassText> texts, Rgb[] glass, int w, int h, bool mustRead)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        bool light = ThemeService.IsLight;
        double scale = StrengthScale(ThemeService.Current?.LiquidGlassTextColorStrength ?? 0.5);
        var plain = PlainGlass();
        var primary = ResourceColor("TextPrimary", light ? Color.FromRgb(0x1D, 0x1D, 0x1F) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        int pass = ++_textPass;

        var choices = new List<TextChoice>();
        foreach (var t in texts)
        {
            if (t.Element.GetAnimationBaseValue(TextElement.ForegroundProperty) is not SolidColorBrush own
                || FlippedColor(own.Color, light) is not { } flipped)
                continue;
            _adapted.TryGetValue(t.Element, out var state);
            // Something else animates this text's colour (a hover fade): leave it to that.
            if (state == null && !ReferenceEquals(t.Element.GetValue(TextElement.ForegroundProperty), own)) continue;
            if (state != null)
            {
                state.Pass = pass;
                state.Root = t.Root;
            }
            double alpha = own.Color.A / 255.0 * own.Opacity;
            var (x0, y0, x1, y1) = CellsUnder(t.Bounds, w, h);
            choices.Add(new TextChoice
            {
                Text = t, Own = own.Color, Flipped = flipped, Strong = Color.FromArgb(own.Color.A, primary.R, primary.G, primary.B),
                Alpha = alpha, Target = TargetFor(Rgb.From(own.Color), alpha, plain, scale),
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, State = state,
            });
        }

        foreach (var group in GroupRects(choices.ConvertAll(c => c.Text.Bounds)).Select(g => g.ConvertAll(i => choices[i])))
        {
            var next = ChooseScheme(group, glass, w, mustRead);
            foreach (var c in group)
            {
                if (next != TextScheme.Own) ShowTextColor(c, next, pass);
                else if (c.State != null) UnflipText(c.Text.Element, c.State, c.Own, animate: true);
            }
        }

        // Text that has gone, is hidden or isn't grey any more: back to its own colour.
        foreach (var (element, state) in _adapted.ToArray())
            if (state.Pass != pass) UnflipText(element, state, null, animate: true);
        UpdateTextShadows();
    }

    /// <summary>
    /// The colours for a group: its own while all of it reads (back to them only with <see cref="FlipMargin"/> to
    /// spare), otherwise the scheme with the fewest pieces that miss their target, then the best worst piece. A change
    /// must read clearly better than its own colours, and holds while it does, so text doesn't flicker on the edge.
    /// </summary>
    private static TextScheme ChooseScheme(List<TextChoice> group, Rgb[] glass, int w, bool mustRead)
    {
        (int Misses, double Worst) Score(TextScheme s)
        {
            int misses = 0;
            double worst = double.MaxValue;
            foreach (var c in group)
            {
                double share = Reads(glass, w, c, c.In(s)) / c.Target;
                if (share < 1) misses++;
                worst = Math.Min(worst, share);
            }
            return (misses, worst);
        }
        static bool Better((int Misses, double Worst) a, (int Misses, double Worst) b, double margin)
            => a.Misses < b.Misses || (a.Misses == b.Misses && a.Worst > b.Worst * margin);

        var own = Score(TextScheme.Own);
        var flipped = Score(TextScheme.Flipped);
        // Full strength only changes secondary text; a group without any is the same as its own colours.
        var strong = group.Any(c => c.Strong != c.Own) ? Score(TextScheme.Strong) : own;
        var (best, bestScore) = Better(strong, flipped, 1) ? (TextScheme.Strong, strong) : (TextScheme.Flipped, flipped);
        bool helps = mustRead ? bestScore.Misses == 0 : Better(bestScore, own, FlipMargin);

        // What the group shows now (its pieces may have been found apart before).
        var current = group.GroupBy(c => c.State?.Scheme ?? TextScheme.Own).OrderByDescending(g => g.Count()).First().Key;
        if (current == TextScheme.Own)
            return own.Misses > 0 && helps ? best : TextScheme.Own;

        var now = current == TextScheme.Flipped ? flipped : strong;
        if (own.Misses == 0 && own.Worst >= FlipMargin) return TextScheme.Own;     // reads well again as it is
        if (mustRead ? now.Worst < 1 / FlipMargin : !Better(now, own, 1)) return helps ? best : TextScheme.Own;
        return best != current && helps && Better(bestScore, now, FlipMargin) ? best : current;
    }

    /// <summary>How well a colour reads over the glass under a piece of text: the geometric mean of the contrast there
    /// (the worst spot alone made a label next to a bright edge give up on every colour; a plain mean let a few very
    /// dark spots hide a bright one).</summary>
    private static double Reads(Rgb[] glass, int w, TextChoice c, Color color)
    {
        var text = Rgb.From(color);
        double sum = 0;
        int n = 0;
        for (int y = c.Y0; y <= c.Y1; y++)
            for (int x = c.X0; x <= c.X1; x++)
            {
                var g = glass[y * w + x];
                sum += Math.Log(Contrast(g.Luminance, g.Toward(text, c.Alpha).Luminance));
                n++;
            }
        return Math.Exp(sum / Math.Max(1, n));
    }

    /// <summary>Text that sits together: pieces closer than <see cref="GroupGap"/> (past their margins) join, as long as
    /// the group stays within <see cref="MaxGroupHeight"/>: an icon and its label, a title and its subtitle, a line of
    /// toolbar buttons, but not every row of a menu. The groups hold indices into <paramref name="rects"/>.</summary>
    private static List<List<int>> GroupRects(List<Rect> rects)
    {
        var groups = rects.Select((_, i) => new List<int> { i }).ToList();
        var bounds = new List<Rect>(rects);
        bool Near(List<int> a, List<int> b)
        {
            foreach (int x in a)
            {
                var r = rects[x];
                r.Inflate(GroupGap, GroupGap);
                foreach (int y in b)
                    if (r.IntersectsWith(rects[y])) return true;
            }
            return false;
        }
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < groups.Count; i++)
                for (int j = groups.Count - 1; j > i; j--)
                {
                    var union = Rect.Union(bounds[i], bounds[j]);
                    if (union.Height > MaxGroupHeight || !Near(groups[i], groups[j])) continue;
                    groups[i].AddRange(groups[j]);
                    bounds[i] = union;
                    groups.RemoveAt(j);
                    bounds.RemoveAt(j);
                    merged = true;
                }
        }
        return groups;
    }

    /// <summary>The other end of the grey scale for the theme's text colour, keeping how strong it is (secondary text
    /// stays lighter than primary); null for text that isn't grey or isn't on the theme's side.</summary>
    private static Color? FlippedColor(Color c, bool light)
    {
        if (c.A < 90) return null;
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min > 40) return null;  // accent, error and other coloured text
        int grey = (c.R + c.G + c.B) / 3;
        if (light ? grey > 175 : grey < 90) return null;  // e.g. white text on a picture in light mode
        byte v = (byte)(light ? 255 - grey / 2 : 28 + (255 - grey) / 2);
        return Color.FromArgb(c.A, v, v, v);
    }

    private void ShowTextColor(TextChoice c, TextScheme scheme, int pass)
    {
        var element = c.Text.Element;
        var state = c.State;
        if (state == null)
        {
            state = new AdaptedText { Target = c.Own, Pass = pass, Root = c.Text.Root };
            _adapted[element] = state;
            c.State = state;
        }
        state.Scheme = scheme;
        var target = c.In(scheme);
        if (state.Target == target) return;
        FadeTextColor(element, ShownColor(element) ?? state.Target, target, done: null);
        state.Target = target;
    }

    /// <summary>Fades the text back to <paramref name="own"/> (null = its own colour now) and then removes the
    /// animation; at once when not animated or hidden.</summary>
    private void UnflipText(FrameworkElement element, AdaptedText state, Color? own, bool animate)
    {
        bool returning = state.Scheme == TextScheme.Own;
        state.Scheme = TextScheme.Own;
        void Release()
        {
            if (state.Scheme != TextScheme.Own || !_adapted.TryGetValue(element, out var current) || current != state) return;
            element.BeginAnimation(TextElement.ForegroundProperty, null);
            _adapted.Remove(element);
            UpdateTextShadows();
        }
        var back = own ?? (element.GetAnimationBaseValue(TextElement.ForegroundProperty) as SolidColorBrush)?.Color;
        if (!animate || back is not { } to || !element.IsVisible) { Release(); return; }
        if (returning && state.Target == to) return;  // already on its way back
        FadeTextColor(element, ShownColor(element) ?? state.Target, to, done: Release);
        state.Target = to;
    }

    /// <summary>Every adapted text back to its own colour (Text Color turned off, the panel going away).</summary>
    private void RestoreText(bool animate)
    {
        if (_adapted.Count == 0) return;
        foreach (var (element, state) in _adapted.ToArray())
            UnflipText(element, state, null, animate);
    }

    /// <summary>A content child with text shown at the other end of the grey scale goes without the text shadow (a dark
    /// shadow under dark text blurs it) until that text is back to its own colour.</summary>
    private void UpdateTextShadows()
    {
        bool light = ThemeService.IsLight;
        var other = new HashSet<UIElement>();
        foreach (var (element, state) in _adapted)
            if (state.Root != null && (IsOtherSide(state.Target, light) || ShownColor(element) is { } shown && IsOtherSide(shown, light)))
                other.Add(state.Root);
        if (other.SetEquals(_unshadowed)) return;
        var changed = new HashSet<UIElement>(_unshadowed);
        changed.SymmetricExceptWith(other);
        _unshadowed.Clear();
        _unshadowed.UnionWith(other);
        foreach (var child in changed)
            if (VisualTreeHelper.GetParent(child) == this) ShadowContent(child);
    }

    private const int TextFadeSteps = 16;
    private static readonly TimeSpan TextFadeDuration = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Fades the element's text from <paramref name="from"/> (what it shows now, so a fade that turns around halfway
    /// doesn't jump) to <paramref name="to"/> and holds it there, in its own brush's opacity. An animation freezes the
    /// brushes it holds, so one brush whose colour is animated wouldn't reach the text: the fade is a run of frozen
    /// brushes along the way instead.
    /// </summary>
    private static void FadeTextColor(FrameworkElement element, Color from, Color to, Action? done)
    {
        int steps = SettingsService.Current.EnablePerformanceMode || from == to ? 1 : TextFadeSteps;
        double opacity = (element.GetAnimationBaseValue(TextElement.ForegroundProperty) as Brush)?.Opacity ?? 1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fade = new ObjectAnimationUsingKeyFrames();
        for (int i = 1; i <= steps; i++)
        {
            double t = steps == 1 ? 1 : ease.Ease((double)i / steps);
            var c = Color.FromArgb(Mix(from.A, to.A, t), Mix(from.R, to.R, t), Mix(from.G, to.G, t), Mix(from.B, to.B, t));
            fade.KeyFrames.Add(new DiscreteObjectKeyFrame(Freeze(new SolidColorBrush(c) { Opacity = opacity }),
                KeyTime.FromTimeSpan(steps == 1 ? TimeSpan.Zero : TextFadeDuration * ((double)i / steps))));
        }
        if (done != null) fade.Completed += (_, _) => done();
        element.BeginAnimation(TextElement.ForegroundProperty, fade);
    }

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

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

/// <summary>Which of the user's readability options a <see cref="LiquidGlassPanel"/> follows (<see cref="LiquidGlassPanel.Readability"/>).</summary>
public enum GlassReadability { Settings, AutoDimOnly, TextColorOnly }
