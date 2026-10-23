using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoConverterUI.Animations;

/// <summary>
/// Overlay-style scroll bars for the whole app: every ScrollBar is invisible until its content scrolls or the mouse
/// enters the scroll area, then fades in and fades out again shortly after. It stays visible while the mouse is on
/// it or the thumb is being dragged. The look itself (slim pill) is the ScrollBar style in App.xaml.
/// </summary>
public static class ScrollBarAutoHide
{
    private sealed class State(DispatcherTimer timer)
    {
        public DispatcherTimer Timer { get; } = timer;
        public bool Shown;
    }

    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1100);
    private static readonly ConditionalWeakTable<ScrollBar, State> _states = new();

    /// <summary>Set by the global ScrollBar style in App.xaml: the bar starts hidden and is managed here.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ScrollBarAutoHide), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>Hooks scrolling and hovering of every ScrollViewer in the app. Call once at startup.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseEnterEvent, new MouseEventHandler(OnViewerMouseEnter));
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar || e.NewValue is not true || _states.TryGetValue(bar, out _)) return;

        var timer = new DispatcherTimer(DispatcherPriority.Background, bar.Dispatcher) { Interval = HideDelay };
        var state = new State(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (bar.IsMouseOver || bar.Track?.Thumb?.IsDragging == true)
            {
                timer.Start();
                return;
            }
            Fade(bar, state, false);
        };
        _states.Add(bar, state);

        bar.BeginAnimation(UIElement.OpacityProperty, null);
        bar.Opacity = 0;
        bar.MouseEnter += (_, _) => Flash(bar);
        bar.MouseLeave += (_, _) => Flash(bar);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The event bubbles through outer ScrollViewers too; only the one that scrolled counts.
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not ScrollViewer viewer) return;
        if (e.VerticalChange != 0) Flash(Part(viewer, "PART_VerticalScrollBar"));
        if (e.HorizontalChange != 0) Flash(Part(viewer, "PART_HorizontalScrollBar"));
    }

    private static void OnViewerMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible) Flash(Part(viewer, "PART_VerticalScrollBar"));
        if (viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible) Flash(Part(viewer, "PART_HorizontalScrollBar"));
    }

    private static ScrollBar? Part(ScrollViewer viewer, string name) => viewer.Template?.FindName(name, viewer) as ScrollBar;

    /// <summary>Shows the bar and (re)starts the countdown that hides it again.</summary>
    private static void Flash(ScrollBar? bar)
    {
        if (bar == null || !_states.TryGetValue(bar, out var state)) return;
        Fade(bar, state, true);
        state.Timer.Stop();
        state.Timer.Start();
    }

    private static void Fade(ScrollBar bar, State state, bool show)
    {
        if (state.Shown == show) return;
        state.Shown = show;
        double to = show ? 1 : 0;

        if (FluidMotion.AnimationsDisabled)
        {
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = to;
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(show ? 140 : 420))
        {
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseInOut },
        };
        bar.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
