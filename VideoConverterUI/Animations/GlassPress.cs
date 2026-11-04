using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoConverterUI.Animations;

/// <summary>
/// glisse's button press (github.com/0kzh/glisse, src/internal/press.ts, its mouse feel): pressed, the surface grows until
/// its longer side is 6 px bigger, leans towards the pointer and stretches along the pull, and lets go on a softer spring.
/// Works on any element, a glass host included: <c>anim:GlassPress.IsEnabled="True"</c>. <see cref="IsHeldProperty"/>
/// tells a <see cref="Controls.GlassChrome"/> on a non-button (the Start capsule) that it is held.
/// </summary>
public static class GlassPress
{
    private const double Grow = 6, Lean = 8, Stretch = 6;

    // glisse's stiffness and damping as response and damping ratio.
    private static readonly Spring PressSpring = FromStiffness(417.6, 29.2);
    private static readonly Spring ReleaseSpring = FromStiffness(230, 23.7);

    public static Spring FromStiffness(double stiffness, double damping)
    {
        double omega = Math.Sqrt(stiffness);
        return new Spring(2 * Math.PI / omega, damping / (2 * omega));
    }

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GlassPress), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty IsHeldProperty = DependencyProperty.RegisterAttached(
        "IsHeld", typeof(bool), typeof(GlassPress), new PropertyMetadata(false));

    private static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(Point?), typeof(GlassPress), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsHeld(DependencyObject d) => (bool)d.GetValue(IsHeldProperty);
    public static void SetIsHeld(DependencyObject d, bool value) => d.SetValue(IsHeldProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.PreviewMouseLeftButtonDown -= OnDown;
        el.PreviewMouseMove -= OnMove;
        el.PreviewMouseLeftButtonUp -= OnUp;
        el.LostMouseCapture -= OnCaptureLost;
        el.MouseLeave -= OnLeave;
        el.Unloaded -= OnUnloaded;
        if (!(bool)e.NewValue) return;
        el.PreviewMouseLeftButtonDown += OnDown;
        el.PreviewMouseMove += OnMove;
        el.PreviewMouseLeftButtonUp += OnUp;
        el.LostMouseCapture += OnCaptureLost;
        el.MouseLeave += OnLeave;
        el.Unloaded += OnUnloaded;
    }

    private static bool Animates => !FluidMotion.AnimationsDisabled;

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsEnabled: true } el || el.ActualWidth <= 0 || el.ActualHeight <= 0) return;
        el.SetValue(StartProperty, (Point?)e.GetPosition(el));
        SetIsHeld(el, true);
        Track(el, e.GetPosition(el));
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && GetIsHeld(el)) Track(el, e.GetPosition(el));
    }

    private static void OnUp(object sender, MouseButtonEventArgs e) => End(sender);
    private static void OnCaptureLost(object sender, MouseEventArgs e) => End(sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => End(sender);

    // A surface that doesn't capture the mouse (a Border) only hears the release while the pointer is over it.
    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && !el.IsMouseCaptureWithin) End(sender);
    }

    private static void Track(FrameworkElement el, Point at)
    {
        if (!Animates || el.GetValue(StartProperty) is not Point start) return;

        // The layout box doesn't move with the transform, so positions in the element's own space need the transform
        // undone: GetPosition already goes through it, which is close enough for a lean of a few pixels.
        double width = el.ActualWidth, height = el.ActualHeight;
        double grown = 1 + Grow / Math.Max(width, height);
        double aspect = width / height;
        double dx = (at.X - start.X) / aspect, dy = at.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01)
        {
            Target(el, grown, grown, 0, 0, PressSpring);
            return;
        }

        double nx = dx / length, ny = dy / length;
        double rubber = 1 - 1 / (1 + length / height / (5 * aspect));
        double stretch = 1 + Stretch / height * rubber * Math.Abs(Math.Abs(nx) - Math.Abs(ny));
        bool horizontal = Math.Abs(nx) > Math.Abs(ny);
        Target(el, grown * (horizontal ? stretch : 1 / stretch), grown * (horizontal ? 1 / stretch : stretch),
            nx * Lean * rubber, ny * Lean * rubber, PressSpring);
    }

    private static void End(object sender)
    {
        if (sender is not FrameworkElement el || !GetIsHeld(el)) return;
        SetIsHeld(el, false);
        el.ClearValue(StartProperty);
        if (Animates) Target(el, 1, 1, 0, 0, ReleaseSpring);
    }

    private static void Target(FrameworkElement el, double scaleX, double scaleY, double x, double y, Spring spring)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var (scale, move) = Liquid.Rig(el);
        Liquid.To(scale, ScaleTransform.ScaleXProperty, scaleX, spring);
        Liquid.To(scale, ScaleTransform.ScaleYProperty, scaleY, spring);
        Liquid.To(move, TranslateTransform.XProperty, x, spring);
        Liquid.To(move, TranslateTransform.YProperty, y, spring);
    }
}
