using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoConverterUI.Services;

namespace VideoConverterUI.Animations;

public static class MicroAnimationBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(MicroAnimationBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        if ((bool)e.NewValue)
        {
            el.MouseEnter += OnMouseEnter;
            el.MouseLeave += OnMouseLeave;
            el.PreviewMouseLeftButtonDown += OnMouseDown;
            el.PreviewMouseLeftButtonUp += OnMouseUp;
        }
        else
        {
            el.MouseEnter -= OnMouseEnter;
            el.MouseLeave -= OnMouseLeave;
            el.PreviewMouseLeftButtonDown -= OnMouseDown;
            el.PreviewMouseLeftButtonUp -= OnMouseUp;
        }
    }

    private static readonly Point CenterPoint = new(0.5, 0.5);

    private static void EnsureTransform(FrameworkElement el, out ScaleTransform st)
    {
        if (el.RenderTransformOrigin != CenterPoint)
            el.RenderTransformOrigin = CenterPoint;

        if (el.RenderTransform is ScaleTransform scale)
        {
            st = scale;
        }
        else
        {
            st = new ScaleTransform(1.0, 1.0);
            el.RenderTransform = st;
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var theme = ThemeService.Current;
        if (theme == null || !theme.EnableHoverMicroAnims || FluidMotion.AnimationsDisabled) return;

        EnsureTransform(el, out var st);
        double targetScale = theme.ButtonHoverScale > 0 ? theme.ButtonHoverScale : 1.03;
        if (Liquid.IsActive)
        {
            Liquid.Hover(st, targetScale);
            return;
        }
        var anim = new DoubleAnimation(st.ScaleX, targetScale, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = AppleSpringEase.Interactive
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        if (SettingsService.Current.EnablePerformanceMode) return;
        EnsureTransform(el, out var st);
        if (Math.Abs(st.ScaleX - 1.0) < 0.001 && Math.Abs(st.ScaleY - 1.0) < 0.001) return;
        if (Liquid.IsActive)
        {
            Liquid.Unhover(st);
            return;
        }
        var anim = new DoubleAnimation(st.ScaleX, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = AppleSpringEase.Smooth
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var theme = ThemeService.Current;
        if (theme == null || !theme.EnableHoverMicroAnims || FluidMotion.AnimationsDisabled) return;

        EnsureTransform(el, out var st);
        if (Liquid.IsActive)
        {
            Liquid.Press(st);
            return;
        }
        var anim = new DoubleAnimation(st.ScaleX, 0.96, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = AppleSpringEase.Snappy
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var theme = ThemeService.Current;
        if (theme == null || !theme.EnableHoverMicroAnims || FluidMotion.AnimationsDisabled) return;

        EnsureTransform(el, out var st);
        double targetScale = el.IsMouseOver ? (theme.ButtonHoverScale > 0 ? theme.ButtonHoverScale : 1.03) : 1.0;
        if (Liquid.IsActive)
        {
            Liquid.Release(st, targetScale);
            return;
        }
        var anim = new DoubleAnimation(st.ScaleX, targetScale, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = AppleSpringEase.Interactive
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
