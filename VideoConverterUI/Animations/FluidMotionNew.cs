using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VideoConverterUI.Models;

namespace VideoConverterUI.Animations;

// Exact CASpringAnimation / SwiftUI .spring(response:dampingFraction:) formula
public sealed class AppleSpringEase : IEasingFunction
{
    private readonly double _zeta;
    private readonly double _omega0;
    private readonly double _settlingTime;

    public AppleSpringEase(double dampingRatio = 0.86, double response = 0.4)
    {
        _zeta = Math.Clamp(dampingRatio, 0.01, 5.0);
        _omega0 = 2.0 * Math.PI / Math.Max(response, 0.001);
        _settlingTime = Math.Log(1000.0) / (_zeta * _omega0);
    }

    public double Ease(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        double realTime = t * _settlingTime;

        if (_zeta < 1.0)
        {
            // Under-damped
            double omegaD = _omega0 * Math.Sqrt(1.0 - _zeta * _zeta);
            double envelope = Math.Exp(-_zeta * _omega0 * realTime);
            double osc = Math.Cos(omegaD * realTime)
                       + (_zeta * _omega0 / omegaD) * Math.Sin(omegaD * realTime);
            return 1.0 - envelope * osc;
        }

        if (Math.Abs(_zeta - 1.0) < 1e-6)
        {
            // Critically damped
            double wt = _omega0 * realTime;
            return 1.0 - (1.0 + wt) * Math.Exp(-wt);
        }

        // Over-damped
        double s = Math.Sqrt(_zeta * _zeta - 1.0);
        double r1 = -_omega0 * (_zeta + s);
        double r2 = -_omega0 * (_zeta - s);
        double c2 = 1.0 / (2.0 * s);
        double c1 = 1.0 - c2;
        return 1.0 - c1 * Math.Exp(r1 * realTime) - c2 * Math.Exp(r2 * realTime);
    }

    public static readonly AppleSpringEase Interactive = new(0.72, 0.50);
    public static readonly AppleSpringEase Gentle      = new(0.80, 0.55);
    public static readonly AppleSpringEase Bouncy      = new(0.65, 0.45);
    public static readonly AppleSpringEase Smooth      = new(1.00, 0.40);
    public static readonly AppleSpringEase Snappy      = new(0.86, 0.35);
}

public static class FluidMotion
{
    public static readonly DependencyProperty OriginOxProperty =
        DependencyProperty.RegisterAttached("OriginOx", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.5));

    public static readonly DependencyProperty OriginOyProperty =
        DependencyProperty.RegisterAttached("OriginOy", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.5));

    public static readonly DependencyProperty OriginSxProperty =
        DependencyProperty.RegisterAttached("OriginSx", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OriginSyProperty =
        DependencyProperty.RegisterAttached("OriginSy", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OriginTxProperty =
        DependencyProperty.RegisterAttached("OriginTx", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OriginTyProperty =
        DependencyProperty.RegisterAttached("OriginTy", typeof(double), typeof(FluidMotion), new PropertyMetadata(0.0));

    private static void GetWindowPosition(Window window, double winW, double winH, out double left, out double top)
    {
        left = window.Left;
        top = window.Top;

        if (double.IsNaN(left) || double.IsNaN(top))
        {
            if (window.Owner != null)
            {
                var ownerW = window.Owner.ActualWidth;
                if (double.IsNaN(ownerW) || ownerW <= 0) ownerW = window.Owner.Width;
                var ownerH = window.Owner.ActualHeight;
                if (double.IsNaN(ownerH) || ownerH <= 0) ownerH = window.Owner.Height;

                var ownerL = window.Owner.Left;
                var ownerT = window.Owner.Top;

                if (double.IsNaN(ownerL)) ownerL = 0;
                if (double.IsNaN(ownerT)) ownerT = 0;

                if (double.IsNaN(left)) left = ownerL + (ownerW - winW) / 2;
                if (double.IsNaN(top)) top = ownerT + (ownerH - winH) / 2;
            }
            else
            {
                if (double.IsNaN(left)) left = (SystemParameters.PrimaryScreenWidth - winW) / 2;
                if (double.IsNaN(top)) top = (SystemParameters.PrimaryScreenHeight - winH) / 2;
            }
        }

        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
    }

    private static ThemeSettings GetThemeSettings()
    {
        try
        {
            return Services.ThemeService.Current ?? new ThemeSettings();
        }
        catch
        {
            return new ThemeSettings();
        }
    }

    private static string GetAnimationLevel()
    {
        try
        {
            if (Services.SettingsService.Current.EnablePerformanceMode) return "Disabled (Static)";
            var theme = GetThemeSettings();
            return theme.AnimationPreset ?? theme.AnimationLevel ?? "Balanced";
        }
        catch
        {
            return "Balanced";
        }
    }

    /// <summary>True when the user picked "Disabled (Static)" (or a legacy equivalent) or runs in performance mode.</summary>
    public static bool AnimationsDisabled => GetMotionMode() == "None";

    /// <summary>The motion profile in use ("Liquid", "Smooth Liquid", "Balanced", ...).</summary>
    internal static string Preset => GetAnimationLevel();

    /// <summary>How much longer the window animations take in a profile.</summary>
    internal static double SpeedFor(string preset) => preset switch
    {
        "Subtle" => 0.7,
        "Smooth Liquid" => 1.35,
        "Hyper Fluid" => 1.65,
        _ => 1.0
    };

    /// <summary>
    /// Maps the animation preset to the three motion modes the helpers below understand:
    /// "None" (no animation), "Reduced" (short fades) or "Full".
    /// The presets are named "Disabled (Static)", "Subtle", "Balanced", ... while these helpers
    /// historically compared against "None"/"Reduced", so disabling animations had no effect.
    /// </summary>
    private static string GetMotionMode() => GetAnimationLevel() switch
    {
        "Disabled (Static)" or "Disabled" or "None" => "None",
        "Subtle" or "Reduced" => "Reduced",
        _ => "Full"
    };

    /// <summary>The easing a window-animation easing name (<see cref="ThemeSettings.WindowAnimEasing"/>) stands for.</summary>
    internal static IEasingFunction? GetEasing(string? easingName)
    {
        return easingName switch
        {
            "Apple Spring (Bouncy)" => AppleSpringEase.Bouncy,
            "Elastic Snap"          => AppleSpringEase.Snappy,
            "Cubic Smooth"          => AppleSpringEase.Smooth,
            "Linear Constant"       => null,
            _                       => AppleSpringEase.Interactive
        };
    }

    public static void MorphOpen(
        FrameworkElement root,
        ScaleTransform scale,
        TranslateTransform translate,
        Rect origin,
        Window window,
        Action? onCompleted = null)
    {
        string preset = GetAnimationLevel();
        if (AnimationsDisabled)
        {
            root.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            translate.X = 0;
            translate.Y = 0;
            onCompleted?.Invoke();
            return;
        }

        var theme = GetThemeSettings();
        double speedMult = SpeedFor(preset);

        int baseDurMs = theme.WindowAnimDuration > 0 ? theme.WindowAnimDuration : 280;
        var springDur = TimeSpan.FromMilliseconds(baseDurMs * speedMult);
        var fadeDur = TimeSpan.FromMilliseconds((baseDurMs * 0.6) * speedMult);
        var easing = GetEasing(theme.WindowAnimEasing);
        var smooth = AppleSpringEase.Smooth;

        string style = theme.WindowAnimStyle ?? "Morph & Scale";

        if (style == Liquid.Name)
        {
            MeasureMorphOrigin(root, origin, window, out double lox, out double loy, out double lsx, out double lsy, out double ltx, out double lty);
            Liquid.OpenWindow(root, scale, translate, lox, loy, lsx, lsy, ltx, lty, Liquid.WindowSpring(theme, speedMult), onCompleted);
            return;
        }

        if (style == "Fade Only")
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            translate.X = 0;
            translate.Y = 0;
            var fade = new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth };
            if (onCompleted != null) fade.Completed += (s, e) => onCompleted();
            root.BeginAnimation(UIElement.OpacityProperty, fade);
            return;
        }

        if (style == "Slide Up & Fade")
        {
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            translate.X = 0;
            translate.Y = 35;
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
            var yAnim = new DoubleAnimation(35, 0, springDur) { EasingFunction = easing };
            if (onCompleted != null) yAnim.Completed += (s, e) => onCompleted();
            translate.BeginAnimation(TranslateTransform.YProperty, yAnim);
            return;
        }

        if (style == "Zoom Elastic")
        {
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            scale.ScaleX = 0.6;
            scale.ScaleY = 0.6;
            translate.X = 0;
            translate.Y = 0;
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1.0, springDur) { EasingFunction = AppleSpringEase.Bouncy });
            var yAnim = new DoubleAnimation(0.6, 1.0, springDur) { EasingFunction = AppleSpringEase.Bouncy };
            if (onCompleted != null) yAnim.Completed += (s, e) => onCompleted();
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, yAnim);
            return;
        }

        // Default: Morph & Scale
        MeasureMorphOrigin(root, origin, window, out double ox, out double oy, out double sx, out double sy, out double tx, out double ty);
        root.RenderTransformOrigin = new Point(ox, oy);

        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, springDur) { EasingFunction = easing });

        var yMorphAnim = new DoubleAnimation(sy, 1, springDur) { EasingFunction = easing };
        if (onCompleted != null) yMorphAnim.Completed += (s, e) => onCompleted();
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, yMorphAnim);

        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx, 0, springDur) { EasingFunction = smooth });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ty, 0, springDur) { EasingFunction = smooth });
    }

    /// <summary>
    /// Where a window grows from when the control that opened it sits at <paramref name="origin"/> (screen units):
    /// the transform origin (ox, oy), the start scale (sx, sy) and the start offset (tx, ty). Kept on
    /// <paramref name="root"/> so <see cref="MorphClose"/> can shrink it back there.
    /// </summary>
    private static void MeasureMorphOrigin(FrameworkElement root, Rect origin, Window window,
        out double ox, out double oy, out double sx, out double sy, out double tx, out double ty)
    {
        var winW = window.ActualWidth;
        if (double.IsNaN(winW) || winW <= 0) winW = window.Width;
        if (double.IsNaN(winW) || winW <= 0) winW = 800;

        var winH = window.ActualHeight;
        if (double.IsNaN(winH) || winH <= 0) winH = window.Height;
        if (double.IsNaN(winH) || winH <= 0) winH = 600;

        GetWindowPosition(window, winW, winH, out double left, out double top);

        var btnCx = origin.X + origin.Width / 2;
        var btnCy = origin.Y + origin.Height / 2;

        ox = Math.Clamp((btnCx - left) / winW, 0.0, 1.0);
        oy = Math.Clamp((btnCy - top) / winH, 0.0, 1.0);

        sx = Math.Clamp(origin.Width / winW, 0.03, 0.45);
        sy = Math.Clamp(origin.Height / winH, 0.03, 0.45);

        tx = btnCx - left - ox * winW;
        ty = btnCy - top - oy * winH;

        root.SetValue(OriginOxProperty, ox);
        root.SetValue(OriginOyProperty, oy);
        root.SetValue(OriginSxProperty, sx);
        root.SetValue(OriginSyProperty, sy);
        root.SetValue(OriginTxProperty, tx);
        root.SetValue(OriginTyProperty, ty);
    }

    public static void MorphClose(
        FrameworkElement root,
        ScaleTransform scale,
        TranslateTransform translate,
        Window window,
        Action onCompleted)
    {
        var theme = GetThemeSettings();
        string preset = GetAnimationLevel();
        if (AnimationsDisabled)
        {
            onCompleted();
            return;
        }

        int baseDurMs = theme.WindowAnimDuration > 0 ? theme.WindowAnimDuration : 280;
        double speedMult = SpeedFor(preset);

        var springDur = TimeSpan.FromMilliseconds((baseDurMs * 0.8) * speedMult);
        var fadeDur = TimeSpan.FromMilliseconds((baseDurMs * 0.55) * speedMult);
        var easing = GetEasing(theme.WindowAnimEasing) ?? AppleSpringEase.Snappy;
        var smooth = AppleSpringEase.Smooth;

        double sx = (double)root.GetValue(OriginSxProperty);
        double sy = (double)root.GetValue(OriginSyProperty);
        double tx = (double)root.GetValue(OriginTxProperty);
        double ty = (double)root.GetValue(OriginTyProperty);
        double ox = (double)root.GetValue(OriginOxProperty);
        double oy = (double)root.GetValue(OriginOyProperty);

        if (theme.WindowAnimStyle == Liquid.Name)
        {
            Liquid.CloseWindow(root, scale, translate, ox, oy, sx, sy, tx, ty, Liquid.WindowSpring(theme, speedMult), onCompleted);
            return;
        }

        if (sx > 0 && sy > 0)
        {
            root.RenderTransformOrigin = new Point(ox, oy);

            var fade = new DoubleAnimation(1, 0, fadeDur) { EasingFunction = smooth };
            fade.Completed += (s, e) => onCompleted();

            root.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, sx, springDur) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, sy, springDur) { EasingFunction = easing });

            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, tx, springDur) { EasingFunction = smooth });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, ty, springDur) { EasingFunction = smooth });
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, fadeDur) { EasingFunction = smooth };
            fade.Completed += (s, e) => onCompleted();

            root.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.88, springDur) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.88, springDur) { EasingFunction = easing });
        }
    }

    // Stagger children with depth-aware spring (travel increases per child)
    public static void StaggerIn(Panel panel, int baseDelayMs = 60, int stepMs = 50)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement fe) continue;
                fe.BeginAnimation(UIElement.OpacityProperty, null);
                fe.Opacity = 1;
                fe.RenderTransform = Transform.Identity;
            }
            return;
        }
        if (mode == "Reduced")
        {
            int idx = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement fe) continue;
                fe.BeginAnimation(UIElement.OpacityProperty, null);
                fe.Opacity = 0;
                fe.RenderTransform = Transform.Identity;
                var delay = TimeSpan.FromMilliseconds(idx * 30);
                fe.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                    { BeginTime = delay, EasingFunction = AppleSpringEase.Smooth });
                idx++;
            }
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.StaggerIn(panel, baseDelayMs, stepMs);
            return;
        }

        var spring = AppleSpringEase.Bouncy;
        var smooth = AppleSpringEase.Gentle;
        int idxOrig = 0;

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe) continue;

            // Clear the last run's held fade, or it shows through the BeginTime delay (flash on re-open).
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            fe.Opacity = 0;
            fe.RenderTransformOrigin = new Point(0.5, 0.0);
            var group = new TransformGroup();
            var st = new ScaleTransform(0.96 - idxOrig * 0.003, 0.96 - idxOrig * 0.003);
            var tt = new TranslateTransform(0, 20 + idxOrig * 3);
            group.Children.Add(st);
            group.Children.Add(tt);
            fe.RenderTransform = group;

            var delay = TimeSpan.FromMilliseconds(baseDelayMs + idxOrig * stepMs);

            fe.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
                { BeginTime = delay, EasingFunction = smooth });

            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20 + idxOrig * 3, 0, TimeSpan.FromMilliseconds(520))
                { BeginTime = delay, EasingFunction = spring });

            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96 - idxOrig * 0.003, 1, TimeSpan.FromMilliseconds(520))
                { BeginTime = delay, EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.96 - idxOrig * 0.003, 1, TimeSpan.FromMilliseconds(520))
                { BeginTime = delay, EasingFunction = spring });

            idxOrig++;
        }
    }

    // Panel show with spring scale + child stagger
    public static void ShowPanel(Border panel, double slideFromX = 0)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 1;
            panel.RenderTransform = Transform.Identity;
            if (panel.Child is StackPanel spNone)
                StaggerIn(spNone);
            return;
        }
        if (mode == "Reduced")
        {
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            panel.RenderTransform = Transform.Identity;
            panel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                { EasingFunction = AppleSpringEase.Smooth });
            if (panel.Child is StackPanel spReduced)
                StaggerIn(spReduced);
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.ShowPanel(panel, slideFromX);
            return;
        }

        panel.Visibility = Visibility.Visible;
        panel.RenderTransformOrigin = new Point(0.5, 0.0);
        var group = new TransformGroup();
        var st = new ScaleTransform(0.94, 0.94);
        var tt = new TranslateTransform(slideFromX, 16);
        group.Children.Add(st);
        group.Children.Add(tt);
        panel.RenderTransform = group;

        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;
        var springDur = TimeSpan.FromMilliseconds(520);

        panel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            { EasingFunction = smooth });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, springDur) { EasingFunction = spring });
        if (slideFromX != 0)
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(slideFromX, 0, springDur) { EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, springDur) { EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, springDur) { EasingFunction = spring });

        if (panel.Child is StackPanel sp)
            StaggerIn(sp, baseDelayMs: 40, stepMs: 35);
    }

    // Which panel of a crossfade is the one meant to be showing (the last call's showPanel).
    private static readonly DependencyProperty CrossfadeShownProperty =
        DependencyProperty.RegisterAttached("CrossfadeShown", typeof(bool), typeof(FluidMotion), new PropertyMetadata(false));

    private static void SetCrossfadeShown(DependencyObject d, bool value) => d.SetValue(CrossfadeShownProperty, value);

    private static void CollapseIfStillHidden(UIElement panel)
    {
        if (!(bool)panel.GetValue(CrossfadeShownProperty)) panel.Visibility = Visibility.Collapsed;
    }

    // iOS 27 stretchy liquid glass crossfade with bouncy swing-tilt landing
    public static void LiquidGlassCrossfade(Border hidePanel, Border showPanel, double direction)
    {
        if (hidePanel == null || showPanel == null) return;
        if (hidePanel == showPanel) return;

        // Idempotency check: if showPanel is already fully visible and hidePanel is collapsed, do nothing!
        // Only for the panel the last switch showed: one that has just started fading out still looks opaque.
        if ((bool)showPanel.GetValue(CrossfadeShownProperty) &&
            showPanel.Visibility == Visibility.Visible && showPanel.Opacity >= 0.95 &&
            (hidePanel.Visibility == Visibility.Collapsed || hidePanel.Opacity <= 0.05))
        {
            return;
        }

        // Switching back before a fade-out has finished: that fade's Completed still fires and must not
        // collapse the panel that is now the one shown.
        SetCrossfadeShown(hidePanel, false);
        SetCrossfadeShown(showPanel, true);

        string mode = GetMotionMode();
        if (mode == "None")
        {
            hidePanel.Opacity = 0;
            hidePanel.Visibility = Visibility.Collapsed;
            showPanel.Visibility = Visibility.Visible;
            showPanel.Opacity = 1;
            showPanel.RenderTransform = Transform.Identity;
            if (showPanel.Child is Panel spNone)
            {
                foreach (UIElement child in spNone.Children)
                {
                    if (child is not FrameworkElement fe) continue;
                    fe.Opacity = 1;
                    fe.RenderTransform = Transform.Identity;
                }
            }
            return;
        }
        if (mode == "Reduced")
        {
            hidePanel.RenderTransform = Transform.Identity;
            var hideOp = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = AppleSpringEase.Smooth };
            hideOp.Completed += (_, _) => CollapseIfStillHidden(hidePanel);
            hidePanel.BeginAnimation(UIElement.OpacityProperty, hideOp);

            showPanel.Visibility = Visibility.Visible;
            showPanel.RenderTransform = Transform.Identity;
            showPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = AppleSpringEase.Smooth });

            if (showPanel.Child is Panel spReduced)
            {
                int idx = 0;
                foreach (UIElement child in spReduced.Children)
                {
                    if (child is not FrameworkElement fe) continue;
                    fe.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                        { BeginTime = TimeSpan.FromMilliseconds(60 + idx * 30), EasingFunction = AppleSpringEase.Smooth });
                    idx++;
                }
            }
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.Crossfade(hidePanel, showPanel, direction);
            return;
        }

        var springX   = new AppleSpringEase(0.65, 0.44); // elastic X response
        var springY   = new AppleSpringEase(0.70, 0.50); // elastic Y response
        var springRot = new AppleSpringEase(0.58, 0.46); // bouncy swing-tilt response
        var smooth    = AppleSpringEase.Gentle;
        var snappy    = AppleSpringEase.Snappy;

        var hideDur   = TimeSpan.FromMilliseconds(160);
        var showDur   = TimeSpan.FromMilliseconds(500);
        var showDelay = TimeSpan.FromMilliseconds(110);   // overlap timing

        // ── Outgoing panel (fade-out with slide + dynamic tilt) ──
        if (hidePanel.Visibility == Visibility.Visible && hidePanel.Opacity > 0.02)
        {
            hidePanel.RenderTransformOrigin = new Point(0.5, 0.5);
            var hideGroup = new TransformGroup();
            var hideSt = new ScaleTransform(1, 1);
            var hideRt = new RotateTransform(0);
            var hideTt = new TranslateTransform(0, 0);
            hideGroup.Children.Add(hideSt);
            hideGroup.Children.Add(hideRt);
            hideGroup.Children.Add(hideTt);
            hidePanel.RenderTransform = hideGroup;

            var hideOpOrig = new DoubleAnimation(0, hideDur) { EasingFunction = snappy };
            hideOpOrig.Completed += (_, _) => CollapseIfStillHidden(hidePanel);
            hidePanel.BeginAnimation(UIElement.OpacityProperty, hideOpOrig);

            hideTt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -direction * 25, hideDur) { EasingFunction = snappy });
            hideSt.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.90, hideDur) { EasingFunction = snappy });
            hideSt.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.90, hideDur) { EasingFunction = snappy });
            hideRt.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, direction * 3.0, hideDur) { EasingFunction = snappy });
        }
        else
        {
            hidePanel.Visibility = Visibility.Collapsed;
            hidePanel.Opacity = 0;
        }

        // ── Incoming panel (stretchy landing + bouncy rotation) ──
        bool isAlreadyShowing = showPanel.Visibility == Visibility.Visible && showPanel.Opacity > 0.5;
        showPanel.Visibility = Visibility.Visible;
        if (!isAlreadyShowing) showPanel.Opacity = 0;

        showPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        var showGroup = new TransformGroup();
        var showSt = new ScaleTransform(0.88, 0.84); // vertical starting stretch
        var showRt = new RotateTransform(-direction * 4.0); // start tilt
        var showTt = new TranslateTransform(direction * 35, 10); // start slide offset
        showGroup.Children.Add(showSt);
        showGroup.Children.Add(showRt);
        showGroup.Children.Add(showTt);
        showPanel.RenderTransform = showGroup;

        showPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))
            { BeginTime = showDelay, EasingFunction = smooth });

        showTt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 35, 0, showDur)
            { BeginTime = showDelay, EasingFunction = springX });
        showTt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, showDur)
            { BeginTime = showDelay, EasingFunction = springY });

        showSt.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.88, 1.0, showDur)
            { BeginTime = showDelay, EasingFunction = springX });
        showSt.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.84, 1.0, showDur)
            { BeginTime = showDelay, EasingFunction = springY });

        showRt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-direction * 4.0, 0, showDur)
            { BeginTime = showDelay, EasingFunction = springRot });

        // ── Apple Glassmorphic Staggered Child Blossom (Flicker-Free) ──
        Panel? sp = showPanel.Child as Panel;
        if (sp != null)
        {
            int idx = 0;
            foreach (UIElement child in sp.Children)
            {
                if (child is not FrameworkElement fe) continue;

                if (!isAlreadyShowing) fe.Opacity = 0;
                fe.RenderTransformOrigin = new Point(0.5, 0.5);
                var childGroup = new TransformGroup();
                var childScale = new ScaleTransform(0.92, 0.92);
                var childTrans = new TranslateTransform(direction * 14, 12 + idx * 2);
                childGroup.Children.Add(childScale);
                childGroup.Children.Add(childTrans);
                fe.RenderTransform = childGroup;

                var childDur = TimeSpan.FromMilliseconds(260 + idx * 35);
                var childSpring = new AppleSpringEase(0.72, 0.44 + idx * 0.02);

                fe.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220 + idx * 25))
                    { EasingFunction = smooth });

                childTrans.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(direction * 14, 0, childDur)
                    { EasingFunction = childSpring });
                childTrans.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(12 + idx * 2, 0, childDur)
                    { EasingFunction = childSpring });

                childScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.92, 1.0, childDur)
                    { EasingFunction = childSpring });
                childScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.92, 1.0, childDur)
                    { EasingFunction = childSpring });

                idx++;
            }
        }
    }

    /// <summary>
    /// iOS navigation push/pop. Push (<paramref name="forward"/>): the new page slides in from the right edge while
    /// the old one drifts a third of the way left (parallax); pop is the mirror image. The pages have no opaque
    /// background, so both also cross-fade, quicker than they move, to keep them from showing through each other.
    /// <paramref name="onCompleted"/> runs when the motion has settled; resetting <paramref name="outgoing"/> is up to the caller.
    /// </summary>
    public static void SlidePage(FrameworkElement outgoing, FrameworkElement incoming, double width, bool forward, Action? onCompleted = null)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Opacity = 1;
            incoming.RenderTransform = Transform.Identity;
            onCompleted?.Invoke();
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.SlidePage(outgoing, incoming, width, forward, onCompleted);
            return;
        }

        bool reduced = mode == "Reduced";
        double far = reduced ? 24 : width;          // the page on top travels the whole width
        double near = reduced ? 12 : width * 0.3;   // the page underneath only a third of it
        var moveDur = TimeSpan.FromMilliseconds(reduced ? 200 : 440);
        var fadeDur = TimeSpan.FromMilliseconds(reduced ? 140 : 260);
        IEasingFunction moveEase = reduced ? new CubicEase { EasingMode = EasingMode.EaseOut } : AppleSpringEase.Smooth;
        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        double inFrom = forward ? far : -near;
        double outTo = forward ? -near : far;

        var inMove = new TranslateTransform(inFrom, 0);
        incoming.RenderTransform = inMove;
        inMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(inFrom, 0, moveDur) { EasingFunction = moveEase });
        incoming.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = fadeEase });

        var outMove = new TranslateTransform(0, 0);
        outgoing.RenderTransform = outMove;
        var outSlide = new DoubleAnimation(0, outTo, moveDur) { EasingFunction = moveEase };
        if (onCompleted != null) outSlide.Completed += (_, _) => onCompleted();
        outMove.BeginAnimation(TranslateTransform.XProperty, outSlide);
        outgoing.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, fadeDur) { EasingFunction = fadeEase });
    }

    // Panel hide with snappy ease
    public static void HidePanel(Border panel, double slideX = 0, Action? onCompleted = null)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            panel.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
            return;
        }
        if (mode == "Reduced")
        {
            panel.RenderTransform = Transform.Identity;
            var opAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = AppleSpringEase.Smooth };
            opAnim.Completed += (_, _) =>
            {
                panel.Visibility = Visibility.Collapsed;
                onCompleted?.Invoke();
            };
            panel.BeginAnimation(UIElement.OpacityProperty, opAnim);
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.HidePanel(panel, slideX, onCompleted);
            return;
        }

        var ease = AppleSpringEase.Snappy;
        var dur = TimeSpan.FromMilliseconds(200);

        panel.RenderTransformOrigin = new Point(0.5, 0.0);
        var group = new TransformGroup();
        var st = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        group.Children.Add(st);
        group.Children.Add(tt);
        panel.RenderTransform = group;

        var opAnimOrig = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        opAnimOrig.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        };

        panel.BeginAnimation(UIElement.OpacityProperty, opAnimOrig);
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, dur) { EasingFunction = ease });
        if (slideX != 0)
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, slideX, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
    }

    // Progress bar spring animation
    public static void AnimateProgressWidth(FrameworkElement fill, double targetWidth)
    {
        if (fill == null) return;

        var theme = GetThemeSettings();
        if (AnimationsDisabled)
        {
            fill.BeginAnimation(FrameworkElement.WidthProperty, null);
            fill.Width = targetWidth;
            return;
        }

        double currentWidth = fill.ActualWidth > 0 ? fill.ActualWidth : (double.IsNaN(fill.Width) ? 0 : fill.Width);

        if (Math.Abs(currentWidth - targetWidth) < 0.5)
        {
            fill.BeginAnimation(FrameworkElement.WidthProperty, null);
            fill.Width = targetWidth;
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.ProgressWidth(fill, targetWidth);
            return;
        }

        int speedMs =theme.ProgressBarAnimSpeed > 0 ? Math.Min(theme.ProgressBarAnimSpeed, 220) : 180;

        var da = new DoubleAnimation
        {
            From = currentWidth,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(speedMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fill.BeginAnimation(FrameworkElement.WidthProperty, da);
    }

    /// <summary>
    /// Starts a continuous, ambient breathing glow pulse animation on a progress bar element.
    /// Uses an attached or created DropShadowEffect with smooth sine breathing.
    /// </summary>
    public static void StartProgressGlowPulse(FrameworkElement target, Color? glowColor = null, double minBlur = 4.0, double maxBlur = 14.0, double minOpacity = 0.20, double maxOpacity = 0.70)
    {
        if (target == null) return;

        DropShadowEffect? glow = target.Effect as DropShadowEffect;
        if (glow == null)
        {
            glow = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = minBlur,
                Opacity = minOpacity,
                Color = glowColor ?? Color.FromArgb(255, 255, 255, 255)
            };
            target.Effect = glow;
        }
        else
        {
            glow.ShadowDepth = 0;
            if (glowColor.HasValue) glow.Color = glowColor.Value;

            // Callers refresh the pulse on every progress update; restarting the forever-animation
            // each time resets it (it never visibly "breathes") and allocates new timelines.
            if (GetPulseRunning(glow)) return;
        }

        SetPulseRunning(glow, true);

        var duration = TimeSpan.FromMilliseconds(1300);

        var blurAnim = new DoubleAnimation
        {
            From = minBlur,
            To = maxBlur,
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var opAnim = new DoubleAnimation
        {
            From = minOpacity,
            To = maxOpacity,
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, opAnim);
    }

    /// <summary>
    /// Stops the progress glow pulse animation and smoothly fades the glow effect to idle state.
    /// </summary>
    public static void StopProgressGlowPulse(FrameworkElement target, double idleBlur = 0, double idleOpacity = 0, Action? onCompleted = null)
    {
        if (target == null) return;
        if (target.Effect is DropShadowEffect glow)
        {
            SetPulseRunning(glow, false);
            var fadeDur = TimeSpan.FromMilliseconds(220);
            var blurAnim = new DoubleAnimation(idleBlur, fadeDur) { EasingFunction = AppleSpringEase.Snappy };
            var opAnim = new DoubleAnimation(idleOpacity, fadeDur) { EasingFunction = AppleSpringEase.Snappy };
            if (onCompleted != null)
            {
                opAnim.Completed += (s, e) => onCompleted();
            }
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, opAnim);
        }
    }

    private static readonly DependencyProperty PulseRunningProperty =
        DependencyProperty.RegisterAttached("PulseRunning", typeof(bool), typeof(FluidMotion), new PropertyMetadata(false));

    private static bool GetPulseRunning(DependencyObject d) => (bool)d.GetValue(PulseRunningProperty);
    private static void SetPulseRunning(DependencyObject d, bool value) => d.SetValue(PulseRunningProperty, value);

    // Phase transition out (progress rows)
    public static void PhaseOut(FrameworkElement[] rows, Action onComplete)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].Opacity = 0;
            }
            onComplete();
            return;
        }
        if (mode == "Reduced")
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                row.RenderTransform = Transform.Identity;
                var delay = TimeSpan.FromMilliseconds(i * 15);
                var opAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = AppleSpringEase.Smooth, BeginTime = delay };
                if (i == rows.Length - 1)
                    opAnim.Completed += (_, _) => onComplete();
                row.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.PhaseOut(rows, onComplete);
            return;
        }

        var ease = AppleSpringEase.Snappy;

        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            EnsureTranslateScale(row);
            var group = (TransformGroup)row.RenderTransform;
            var st = (ScaleTransform)group.Children[0];
            var tt = (TranslateTransform)group.Children[1];
            var delay = TimeSpan.FromMilliseconds(i * 25);
            var dur = TimeSpan.FromMilliseconds(160);

            var opAnim = new DoubleAnimation(1, 0, dur)
            { EasingFunction = ease, BeginTime = delay };
            var yAnim = new DoubleAnimation(0, 5, dur)
            { EasingFunction = ease, BeginTime = delay };
            var sAnim = new DoubleAnimation(1, 0.97, dur)
            { EasingFunction = ease, BeginTime = delay };

            if (i == rows.Length - 1)
                opAnim.Completed += (_, _) => onComplete();

            row.BeginAnimation(UIElement.OpacityProperty, opAnim);
            tt.BeginAnimation(TranslateTransform.YProperty, yAnim);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sAnim);
        }
    }

    // Phase transition in (progress rows)
    public static void PhaseIn(FrameworkElement[] rows, Action? onComplete = null)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].Opacity = 1;
                rows[i].RenderTransform = Transform.Identity;
            }
            onComplete?.Invoke();
            return;
        }
        if (mode == "Reduced")
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                row.RenderTransform = Transform.Identity;
                var delay = TimeSpan.FromMilliseconds(i * 20);
                row.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = AppleSpringEase.Smooth, BeginTime = delay });
            }
            if (onComplete != null)
            {
                var totalMs = (rows.Length - 1) * 20 + 150;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(totalMs) };
                timer.Tick += (_, _) => { timer.Stop(); onComplete(); };
                timer.Start();
            }
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.PhaseIn(rows, onComplete);
            return;
        }

        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;

        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var group = (TransformGroup)row.RenderTransform;
            var st = (ScaleTransform)group.Children[0];
            var tt = (TranslateTransform)group.Children[1];
            var delay = TimeSpan.FromMilliseconds(i * 45);

            row.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                { BeginTime = delay, EasingFunction = smooth });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(440))
                { BeginTime = delay, EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(440))
                { BeginTime = delay, EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(440))
                { BeginTime = delay, EasingFunction = spring });
        }

        if (onComplete != null)
        {
            var totalMs = (rows.Length - 1) * 45 + 440;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(totalMs) };
            timer.Tick += (_, _) => { timer.Stop(); onComplete(); };
            timer.Start();
        }
    }

    // Log panel expand
    public static void ExpandElement(FrameworkElement element, double targetMaxHeight)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.MaxHeight = targetMaxHeight;
            element.Opacity = 1;
            element.Visibility = Visibility.Visible;
            return;
        }
        if (mode == "Reduced")
        {
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = targetMaxHeight;
            element.Opacity = 0;
            element.Visibility = Visibility.Visible;
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = AppleSpringEase.Smooth });
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.Expand(element, targetMaxHeight);
            return;
        }

        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;

        element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        element.MaxHeight = 0;
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;

        element.BeginAnimation(FrameworkElement.MaxHeightProperty,
            new DoubleAnimation(0, targetMaxHeight, TimeSpan.FromMilliseconds(380))
            { EasingFunction = spring });
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            { EasingFunction = smooth });
    }

    // Log panel collapse
    public static void CollapseElement(FrameworkElement element, double currentHeight, Action onCompleted)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            element.Visibility = Visibility.Collapsed;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = currentHeight;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            onCompleted();
            return;
        }
        if (mode == "Reduced")
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = AppleSpringEase.Smooth };
            fade.Completed += (_, _) =>
            {
                element.Visibility = Visibility.Collapsed;
                element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                element.MaxHeight = currentHeight;
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                onCompleted();
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            return;
        }
        if (Liquid.IsActive)
        {
            Liquid.Collapse(element, currentHeight, onCompleted);
            return;
        }

        var ease = AppleSpringEase.Snappy;

        var heightAnim = new DoubleAnimation(currentHeight, 0, TimeSpan.FromMilliseconds(220))
        { EasingFunction = ease };
        heightAnim.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = currentHeight;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            onCompleted();
        };
        element.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnim);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    // ── Animatable CornerRadius for liquid glass shape morphing ──
    // WPF has no built-in CornerRadiusAnimation, so we proxy through an
    // attached double that pushes a uniform CornerRadius on every change.

    public static readonly DependencyProperty CornerRadiusValueProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadiusValue",
            typeof(double),
            typeof(FluidMotion),
            new PropertyMetadata(0.0, OnCornerRadiusValueChanged));

    public static double GetCornerRadiusValue(DependencyObject d) =>
        (double)d.GetValue(CornerRadiusValueProperty);

    public static void SetCornerRadiusValue(DependencyObject d, double value) =>
        d.SetValue(CornerRadiusValueProperty, value);

    private static void OnCornerRadiusValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Border b)
        {
            double r = (double)e.NewValue;
            b.CornerRadius = new CornerRadius(r);
        }
    }

    public static void AnimateCornerRadius(Border border, double to, TimeSpan duration,
        IEasingFunction? ease = null, TimeSpan? beginTime = null)
    {
        string mode = GetMotionMode();
        if (mode == "None")
        {
            border.BeginAnimation(CornerRadiusValueProperty, null);
            SetCornerRadiusImmediate(border, to);
            return;
        }

        var anim = new DoubleAnimation(to, duration);
        if (ease != null) anim.EasingFunction = ease;
        if (beginTime.HasValue) anim.BeginTime = beginTime.Value;
        border.BeginAnimation(CornerRadiusValueProperty, anim);
    }

    public static void SetCornerRadiusImmediate(Border border, double value)
    {
        border.BeginAnimation(CornerRadiusValueProperty, null);
        SetCornerRadiusValue(border, value);
    }

    private static void EnsureTranslateScale(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup tg && tg.Children.Count >= 2) return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new TranslateTransform(0, 0));
        element.RenderTransform = group;
    }
}
