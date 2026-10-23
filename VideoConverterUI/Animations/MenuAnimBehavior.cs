using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoConverterUI.Animations;

/// <summary>
/// Open animation for context menus and their submenus (the drop-downs' "Scale &amp; Fade", grown from the corner the
/// menu opens from). Set on <see cref="ContextMenu"/> (animates its template's <c>MenuSurface</c>) and on
/// <see cref="MenuItem"/> (animates its submenu's <c>SubMenuSurface</c>); both are set by the global styles in App.xaml.
/// </summary>
public static class MenuAnimBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(MenuAnimBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        bool on = (bool)e.NewValue;
        switch (d)
        {
            case ContextMenu menu:
                if (on) menu.Opened += OnMenuOpened;
                else menu.Opened -= OnMenuOpened;
                break;
            case MenuItem item:
                if (on) item.SubmenuOpened += OnSubmenuOpened;
                else item.SubmenuOpened -= OnSubmenuOpened;
                break;
        }
    }

    private static void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && ReferenceEquals(e.OriginalSource, menu)
            && menu.Template?.FindName("MenuSurface", menu) is FrameworkElement surface)
            Play(surface);
    }

    private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        // SubmenuOpened bubbles: only the item whose own submenu opened.
        if (sender is MenuItem item && ReferenceEquals(e.OriginalSource, item)
            && item.Template?.FindName("SubMenuSurface", item) is FrameworkElement surface)
            Play(surface);
    }

    private static void Play(FrameworkElement surface)
    {
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        if (FluidMotion.AnimationsDisabled)
        {
            surface.Opacity = 1;
            surface.RenderTransform = Transform.Identity;
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.OpenMenu(surface);
            return;
        }

        // Menus open down and to the right of where they were asked for, so they grow from their top-left corner.
        surface.RenderTransformOrigin = new Point(0, 0);
        var scale = new ScaleTransform(0.94, 0.94);
        surface.RenderTransform = scale;
        var grow = TimeSpan.FromMilliseconds(320);
        surface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = AppleSpringEase.Gentle });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, grow) { EasingFunction = AppleSpringEase.Snappy });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, grow) { EasingFunction = AppleSpringEase.Snappy });
    }
}
