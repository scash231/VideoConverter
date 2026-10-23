using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoConverterUI.Services;

namespace VideoConverterUI.Animations;

/// <summary>
/// App-wide smooth mouse-wheel scrolling. Instead of jumping a fixed step per wheel notch,
/// every ScrollViewer glides towards a target offset; further notches extend the target, so
/// fast spinning stays fluid. Controlled by Settings → Appearance → Smooth scrolling and
/// automatically off when animations are disabled or Performance Mode is on.
/// </summary>
public static class SmoothScrollBehavior
{
    private sealed class ScrollState
    {
        public double Target;
        public double LastSet;
    }

    // Time constant of the exponential glide; ~95 % of the distance is covered after 3τ (~250 ms).
    private const double GlideTimeConstant = 0.085;

    private static readonly Dictionary<ScrollViewer, ScrollState> _active = new();
    private static bool _renderingHooked;
    private static TimeSpan _lastRenderTime;

    /// <summary>Hooks the wheel handling for every ScrollViewer in the app. Call once at startup.</summary>
    public static void Register()
    {
        // A class handler on ScrollViewer runs before ScrollViewer's own (stepping) wheel handling,
        // and the innermost ScrollViewer under the mouse sees the bubbling event first.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnMouseWheel));
    }

    private static bool IsEnabled => SettingsService.Current.SmoothScrolling && !FluidMotion.AnimationsDisabled;

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || !IsEnabled || sender is not ScrollViewer viewer) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return; // keep Ctrl/Shift+wheel behaviour of controls

        // Item-based scrolling (e.g. default ListBox/ComboBox lists) can only stop on whole items,
        // so those keep the regular stepping.
        if (!SupportsPixelScrolling(viewer)) return;

        var target = viewer.ScrollableHeight > 0 ? viewer : FindScrollableAncestor(viewer);
        if (target == null) return;

        // Like the system setting: WheelScrollLines × 16 px per notch (120 delta units)
        double step = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0 * e.Delta / 120.0;
        bool atStart = target.VerticalOffset <= 0.5 && step > 0;
        bool atEnd = target.VerticalOffset >= target.ScrollableHeight - 0.5 && step < 0;
        if ((atStart || atEnd) && !_active.ContainsKey(target))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (!_active.TryGetValue(target, out var state))
        {
            state = new ScrollState { Target = target.VerticalOffset, LastSet = target.VerticalOffset };
            _active[target] = state;
        }

        state.Target = Math.Clamp(state.Target - step, 0, target.ScrollableHeight);
        EnsureRendering();
    }

    /// <summary>Cancels a running glide, e.g. when something else takes over scrolling.</summary>
    internal static void Stop(ScrollViewer viewer) => _active.Remove(viewer);

    internal static bool SupportsPixelScrolling(ScrollViewer viewer)
    {
        if (!viewer.CanContentScroll) return true;
        return viewer.TemplatedParent is ItemsControl owner
            && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel;
    }

    /// <summary>
    /// A nested ScrollViewer with nothing to scroll (e.g. a short text box) would swallow the wheel;
    /// hand it to the nearest scrollable parent instead.
    /// </summary>
    private static ScrollViewer? FindScrollableAncestor(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is ScrollViewer sv && sv.ScrollableHeight > 0 && SupportsPixelScrolling(sv))
                return sv;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void EnsureRendering()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var renderTime = ((RenderingEventArgs)e).RenderingTime;
        if (renderTime == _lastRenderTime) return; // Rendering can fire more than once per frame
        double dt = _lastRenderTime == TimeSpan.Zero ? 1.0 / 60 : (renderTime - _lastRenderTime).TotalSeconds;
        _lastRenderTime = renderTime;

        double blend = 1.0 - Math.Exp(-Math.Clamp(dt, 0.001, 0.1) / GlideTimeConstant);

        foreach (var (viewer, state) in _active.ToList())
        {
            double current = viewer.VerticalOffset;

            // Window closed, or the user grabbed the scrollbar / used the keyboard meanwhile.
            if (!viewer.IsLoaded || Math.Abs(current - state.LastSet) > 2.0)
            {
                _active.Remove(viewer);
                continue;
            }

            double target = Math.Clamp(state.Target, 0, viewer.ScrollableHeight);
            double diff = target - current;
            double next = Math.Abs(diff) < 0.5 ? target : current + diff * blend;

            viewer.ScrollToVerticalOffset(next);
            state.LastSet = next;

            if (next == target)
                _active.Remove(viewer);
        }

        if (_active.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }
}
