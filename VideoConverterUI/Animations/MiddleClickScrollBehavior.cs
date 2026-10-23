using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoConverterUI.Animations;

/// <summary>
/// Browser-style middle-click autoscroll. Middle-click a scrollable area to drop an anchor, then move
/// the mouse away from it: the further away, the faster the list scrolls. A short click keeps
/// autoscroll running until the next click / key / wheel; holding the middle button and dragging
/// stops as soon as the button is released.
/// Enable it on a window (or any container) with <c>anim:MiddleClickScrollBehavior.IsEnabled="True"</c>.
/// </summary>
public static class MiddleClickScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(MiddleClickScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // Radius around the anchor in which nothing scrolls (DIPs).
    private const double DeadZone = 12;
    private const double MaxSpeed = 8000; // px per second

    private static Session? _session;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement root) return;
        root.RemoveHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
        if ((bool)e.NewValue)
            root.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Any click while autoscrolling only ends it (like in browsers).
        if (_session != null)
        {
            _session.Stop();
            e.Handled = true;
            return;
        }

        if (e.Handled || e.ChangedButton != MouseButton.Middle || sender is not UIElement root) return;

        var viewer = FindScrollableViewer(e.OriginalSource as DependencyObject);
        if (viewer == null || !root.IsAncestorOf(viewer)) return;

        var session = new Session(root, viewer, e.GetPosition(viewer));
        if (session.Start())
        {
            _session = session;
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollableViewer(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0)
                && SmoothScrollBehavior.SupportsPixelScrolling(sv))
                return sv;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Scroll speed in px/s for a pointer that is <paramref name="distance"/> DIPs away from the anchor.</summary>
    private static double SpeedFor(double distance)
    {
        double excess = Math.Abs(distance) - DeadZone;
        if (excess <= 0) return 0;
        return Math.Sign(distance) * Math.Min(MaxSpeed, excess * 8 + excess * excess * 0.04);
    }

    private sealed class Session
    {
        private readonly UIElement _root;
        private readonly ScrollViewer _viewer;
        private readonly Point _anchor;
        private readonly bool _vertical;
        private readonly bool _horizontal;
        private readonly Window? _window;
        private AnchorAdorner? _adorner;
        private AdornerLayer? _layer;
        private Cursor? _previousCursor;
        private double _x, _y;
        private TimeSpan _lastRenderTime;
        private bool _leftDeadZoneWhileHeld;
        private bool _stopped;

        public Session(UIElement root, ScrollViewer viewer, Point anchor)
        {
            _root = root;
            _viewer = viewer;
            _anchor = anchor;
            _vertical = viewer.ScrollableHeight > 0;
            _horizontal = viewer.ScrollableWidth > 0;
            _window = Window.GetWindow(viewer);
        }

        public bool Start()
        {
            if (!_viewer.CaptureMouse()) return false;

            SmoothScrollBehavior.Stop(_viewer); // a running wheel glide would fight the autoscroll
            _x = _viewer.HorizontalOffset;
            _y = _viewer.VerticalOffset;

            _layer = AdornerLayer.GetAdornerLayer(_viewer);
            if (_layer != null)
            {
                _adorner = new AnchorAdorner(_viewer, _anchor, _vertical, _horizontal);
                _layer.Add(_adorner);
            }

            _previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = CursorFor(0, 0);

            _viewer.PreviewMouseUp += OnPreviewMouseUp;
            _viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            _viewer.LostMouseCapture += OnLostMouseCapture;
            _viewer.Unloaded += OnUnloaded;
            _root.PreviewKeyDown += OnPreviewKeyDown;
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
            CompositionTarget.Rendering += OnRendering;
            return true;
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            if (_session == this) _session = null;

            CompositionTarget.Rendering -= OnRendering;
            _viewer.PreviewMouseUp -= OnPreviewMouseUp;
            _viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            _viewer.LostMouseCapture -= OnLostMouseCapture;
            _viewer.Unloaded -= OnUnloaded;
            _root.PreviewKeyDown -= OnPreviewKeyDown;
            if (_window != null) _window.Deactivated -= OnWindowDeactivated;

            if (_adorner != null) _layer?.Remove(_adorner);
            Mouse.OverrideCursor = _previousCursor;
            if (_viewer.IsMouseCaptured) _viewer.ReleaseMouseCapture();
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;

            // Held and dragged → releasing ends it. A plain click keeps autoscroll running.
            var p = e.GetPosition(_viewer);
            if (_leftDeadZoneWhileHeld || Math.Abs(p.X - _anchor.X) > DeadZone || Math.Abs(p.Y - _anchor.Y) > DeadZone)
                Stop();
        }

        // Let the wheel scroll normally again.
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) => Stop();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) e.Handled = true;
            Stop();
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e) => Stop();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
        private void OnWindowDeactivated(object? sender, EventArgs e) => Stop();

        private void OnRendering(object? sender, EventArgs e)
        {
            var renderTime = ((RenderingEventArgs)e).RenderingTime;
            if (renderTime == _lastRenderTime) return; // Rendering can fire more than once per frame
            double dt = _lastRenderTime == TimeSpan.Zero ? 1.0 / 60 : (renderTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = renderTime;
            dt = Math.Clamp(dt, 0.001, 0.1);

            var p = Mouse.GetPosition(_viewer);
            double dx = _horizontal ? p.X - _anchor.X : 0;
            double dy = _vertical ? p.Y - _anchor.Y : 0;
            double vx = SpeedFor(dx);
            double vy = SpeedFor(dy);

            if ((vx != 0 || vy != 0) && Mouse.MiddleButton == MouseButtonState.Pressed)
                _leftDeadZoneWhileHeld = true;

            // Offsets changed by someone else (items added/removed) → continue from there.
            if (Math.Abs(_viewer.VerticalOffset - _y) > 2) _y = _viewer.VerticalOffset;
            if (Math.Abs(_viewer.HorizontalOffset - _x) > 2) _x = _viewer.HorizontalOffset;

            if (vy != 0)
            {
                _y = Math.Clamp(_y + vy * dt, 0, _viewer.ScrollableHeight);
                _viewer.ScrollToVerticalOffset(_y);
            }
            if (vx != 0)
            {
                _x = Math.Clamp(_x + vx * dt, 0, _viewer.ScrollableWidth);
                _viewer.ScrollToHorizontalOffset(_x);
            }

            int sx = Math.Sign(vx), sy = Math.Sign(vy);
            var cursor = CursorFor(sx, sy);
            if (Mouse.OverrideCursor != cursor) Mouse.OverrideCursor = cursor;
            _adorner?.SetDirection(sx, sy);
        }

        private Cursor CursorFor(int sx, int sy) => (sx, sy) switch
        {
            (0, -1) => Cursors.ScrollN,
            (0, 1) => Cursors.ScrollS,
            (-1, 0) => Cursors.ScrollW,
            (1, 0) => Cursors.ScrollE,
            (-1, -1) => Cursors.ScrollNW,
            (1, -1) => Cursors.ScrollNE,
            (-1, 1) => Cursors.ScrollSW,
            (1, 1) => Cursors.ScrollSE,
            _ => _vertical && _horizontal ? Cursors.ScrollAll : _vertical ? Cursors.ScrollNS : Cursors.ScrollWE,
        };
    }

    /// <summary>The round anchor marker at the click position; the arrow of the current direction lights up.</summary>
    private sealed class AnchorAdorner : Adorner
    {
        private const double Radius = 14;
        private readonly Point _center;
        private readonly bool _vertical;
        private readonly bool _horizontal;
        private int _sx, _sy;

        public AnchorAdorner(UIElement adorned, Point center, bool vertical, bool horizontal) : base(adorned)
        {
            _center = center;
            _vertical = vertical;
            _horizontal = horizontal;
            IsHitTestVisible = false;
        }

        public void SetDirection(int sx, int sy)
        {
            if (sx == _sx && sy == _sy) return;
            _sx = sx;
            _sy = sy;
            InvalidateVisual();
        }

        private Brush Resource(string key, Brush fallback) =>
            (AdornedElement as FrameworkElement)?.TryFindResource(key) as Brush ?? fallback;

        protected override void OnRender(DrawingContext dc)
        {
            var fill = Resource("BgCard", Brushes.Black);
            var border = Resource("BorderSub", Brushes.Gray);
            var idle = Resource("TextSec", Brushes.LightGray);
            var active = Resource("Accent", Brushes.White);

            // Card colour is translucent with blur themes; keep the marker solid so list text doesn't show through.
            if (fill is SolidColorBrush { Color.A: < 255 } translucent)
                fill = new SolidColorBrush(Color.FromRgb(translucent.Color.R, translucent.Color.G, translucent.Color.B));

            dc.DrawEllipse(fill, new Pen(border, 1), _center, Radius, Radius);
            dc.DrawEllipse(idle, null, _center, 2, 2);

            if (_vertical)
            {
                DrawArrow(dc, 0, -1, _sy < 0 ? active : idle);
                DrawArrow(dc, 0, 1, _sy > 0 ? active : idle);
            }
            if (_horizontal)
            {
                DrawArrow(dc, -1, 0, _sx < 0 ? active : idle);
                DrawArrow(dc, 1, 0, _sx > 0 ? active : idle);
            }
        }

        /// <summary>Small triangle pointing in direction (x, y), between 6 and 11 DIPs from the centre.</summary>
        private void DrawArrow(DrawingContext dc, int x, int y, Brush brush)
        {
            Point At(double along, double across) =>
                new(_center.X + x * along - y * across, _center.Y + y * along + x * across);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(At(11, 0), isFilled: true, isClosed: true);
                ctx.LineTo(At(6, -4), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(At(6, 4), isStroked: false, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(brush, null, geometry);
        }
    }
}
