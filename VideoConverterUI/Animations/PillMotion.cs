using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoConverterUI.Controls;

namespace VideoConverterUI.Animations;

/// <summary>
/// Shows and hides toolbar items (the header pills, the buttons and dividers inside them, their labels).
/// Animated, an item grows or shrinks sideways through a <see cref="FrameworkElement.LayoutTransform"/>, so the pill
/// around it changes width and its neighbours glide along with every frame; pills also pop up slightly as they appear.
/// A Liquid Glass pill (a Border with <see cref="LiquidGlass"/>.Surface) would squash its glass that way, so it morphs
/// like a drop instead (<see cref="SetGlassShown"/>).
/// The item's target state is kept on the element, so a call that changes nothing is free and a change mid-animation
/// turns around from where it is. Used by the main window's header and its preview in Settings → Toolbar buttons.
/// </summary>
public static class PillMotion
{
    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(bool?), typeof(PillMotion), new PropertyMetadata(null));

    // A LayoutTransform must stay invertible, so "nothing" is a hair above zero.
    private const double Folded = 0.001;
    private const double PopFrom = 0.55;

    /// <summary>The state <paramref name="el"/> is showing or heading to.</summary>
    public static bool IsShown(FrameworkElement el)
        => el.GetValue(TargetProperty) as bool? ?? el.Visibility == Visibility.Visible;

    /// <param name="pop">Also grow from below full height (pills and buttons); off for labels and inner rows.</param>
    public static void SetShown(FrameworkElement? el, bool show, bool animate, bool pop = true)
    {
        if (el == null) return;
        bool current = IsShown(el);
        el.SetValue(TargetProperty, show);
        if (current == show) return;

        if (el is Border pill && LiquidGlass.GetSurface(pill) != null)
        {
            SetGlassShown(pill, show, animate);
            return;
        }

        if (el.LayoutTransform is not ScaleTransform scale || scale.IsFrozen)
            el.LayoutTransform = scale = new ScaleTransform(1, 1);

        if (!animate || FluidMotion.AnimationsDisabled)
        {
            Settle(el, scale);
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (Liquid.IsActive)
        {
            LiquidSetShown(el, scale, show, pop);
            return;
        }

        if (show)
        {
            bool fromNothing = el.Visibility != Visibility.Visible;
            if (fromNothing)
            {
                scale.ScaleX = Folded;
                scale.ScaleY = pop ? PopFrom : 1;
                el.Opacity = 0;
                el.Visibility = Visibility.Visible;
            }
            // No From: a pill that was still folding away turns around where it is.
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = AppleSpringEase.Interactive });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(480)) { EasingFunction = AppleSpringEase.Bouncy });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = fromNothing ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero,
            });
        }
        else
        {
            var fold = new DoubleAnimation(Folded, TimeSpan.FromMilliseconds(300)) { EasingFunction = AppleSpringEase.Smooth };
            fold.Completed += (_, _) =>
            {
                if (IsShown(el)) return; // shown again meanwhile
                Settle(el, scale);
                el.Visibility = Visibility.Collapsed;
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
            if (pop)
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(PopFrom, TimeSpan.FromMilliseconds(300)) { EasingFunction = AppleSpringEase.Smooth });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        }
    }

    /// <summary>
    /// The Liquid version: the same grow and fold on springs that keep their speed, so a pill that changes its mind
    /// swings round instead of restarting; appearing pills pop up with a small bounce.
    /// </summary>
    private static void LiquidSetShown(FrameworkElement el, ScaleTransform scale, bool show, bool pop)
    {
        if (show)
        {
            bool fromNothing = el.Visibility != Visibility.Visible;
            if (fromNothing)
            {
                Settle(el, scale);
                scale.ScaleX = Folded;
                scale.ScaleY = pop ? PopFrom : 1;
                el.Opacity = 0;
                el.Visibility = Visibility.Visible;
            }
            Liquid.To(scale, ScaleTransform.ScaleXProperty, 1, Spring.WithBounce(0.4, 0.14), min: Folded);
            Liquid.To(scale, ScaleTransform.ScaleYProperty, 1, Spring.WithBounce(0.46, 0.32), min: Folded);
            Liquid.To(el, UIElement.OpacityProperty, 1, Spring.Fade, delayMs: fromNothing ? 50 : 0);
        }
        else
        {
            Liquid.To(scale, ScaleTransform.ScaleXProperty, Folded, Spring.WithBounce(0.3, 0), min: Folded, precision: 0.01, done: () =>
            {
                if (IsShown(el)) return; // shown again meanwhile
                Settle(el, scale);
                el.Visibility = Visibility.Collapsed;
            });
            if (pop) Liquid.To(scale, ScaleTransform.ScaleYProperty, PopFrom, Spring.WithBounce(0.3, 0), min: Folded);
            Liquid.To(el, UIElement.OpacityProperty, 0, new Spring(0.16, 1));
        }
    }

    /// <summary>Stops the animations and leaves the item at full size and opacity (its visibility is the caller's).</summary>
    private static void Settle(FrameworkElement el, ScaleTransform scale)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        el.Opacity = 1;
    }

    // ═══ Glass pills: the drop ═══
    // One number drives the whole shape: Grow, 0 (nothing) … 1 (the pill as its content wants it), past 1 while a
    // bouncy spring overshoots. Every frame it changes, ShapeGlass works the pill's real width out from it (so the glass,
    // its rim and its lens stay a true capsule and the neighbours glide along), the height follows the width while the
    // pill is narrower than tall (a round drop rather than a thin sliver), an overshoot squashes it a little, and the
    // content fades in behind the glass and out ahead of it.

    private static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow", typeof(double), typeof(PillMotion), new PropertyMetadata(1.0, (d, _) => ShapeGlass((Border)d)));

    // While a pill morphs: its margin at rest (it opens up with the pill) and its squash transform.
    private static readonly DependencyProperty MorphProperty = DependencyProperty.RegisterAttached(
        "Morph", typeof(GlassMorph), typeof(PillMotion), new PropertyMetadata(null));

    private sealed record GlassMorph(Thickness RestMargin, ScaleTransform Squash);

    /// <summary>
    /// A glass pill comes and goes like a drop of liquid: it wells up as a small round drop, swells sideways into its
    /// capsule with a jelly overshoot and settles; leaving, it drains back into a drop that shrinks away. Under Liquid
    /// on springs that carry their speed into a change of mind, otherwise on the profile's spring eases.
    /// </summary>
    private static void SetGlassShown(Border pill, bool show, bool animate)
    {
        if (!animate || FluidMotion.AnimationsDisabled)
        {
            SettleGlass(pill);
            pill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (pill.GetValue(MorphProperty) == null)
        {
            if (pill.RenderTransform is not ScaleTransform { IsFrozen: false } squash)
                pill.RenderTransform = squash = new ScaleTransform(1, 1);
            pill.RenderTransformOrigin = new Point(0.5, 0.5);
            pill.SetValue(MorphProperty, new GlassMorph(pill.Margin, squash));
        }
        if (show && pill.Visibility != Visibility.Visible)
        {
            pill.BeginAnimation(GrowProperty, null);
            pill.SetValue(GrowProperty, 0.0);
            pill.Visibility = Visibility.Visible;
        }

        if (Liquid.IsActive)
        {
            if (show)
                Liquid.To(pill, GrowProperty, 1, Spring.WithBounce(0.5, 0.3), min: 0, done: () => SettleGlass(pill));
            else
                Liquid.To(pill, GrowProperty, 0, Spring.WithBounce(0.34, 0), min: 0, precision: 0.01, done: () => GlassGone(pill));
            return;
        }

        var anim = show
            ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(480)) { EasingFunction = AppleSpringEase.Bouncy }
            : new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = AppleSpringEase.Smooth };
        anim.Completed += (_, _) =>
        {
            if (IsShown(pill) != show) return; // turned around meanwhile
            if (show) SettleGlass(pill);
            else GlassGone(pill);
        };
        pill.BeginAnimation(GrowProperty, anim);
    }

    private static void ShapeGlass(Border pill)
    {
        if (pill.GetValue(MorphProperty) is not GlassMorph morph || LiquidGlass.GetContent(pill) is not FrameworkElement content) return;
        double grow = Math.Max(0, (double)pill.GetValue(GrowProperty));

        // The content's own width, unconstrained: buttons growing or folding inside the pill at the same moment
        // (PillMotion on them) are followed frame by frame.
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = content.DesiredSize.Width;
        double height = pill.ActualHeight > 0 ? pill.ActualHeight : content.DesiredSize.Height;
        double width = natural * grow;

        pill.Width = width;
        double open = Math.Min(1, grow);
        var m = morph.RestMargin;
        pill.Margin = new Thickness(m.Left * open, m.Top, m.Right * open, m.Bottom);
        morph.Squash.ScaleY = width < height ? width / height        // a round drop
                            : grow > 1 ? Math.Max(0.84, 1 - 1.6 * (grow - 1))  // overshooting: squashed flatter
                            : 1;
        content.Opacity = Math.Clamp((grow - 0.45) / 0.4, 0, 1);
    }

    /// <summary>Back to rest: no morph, the pill sized by its content again (its visibility is the caller's).</summary>
    private static void SettleGlass(Border pill)
    {
        if (pill.GetValue(MorphProperty) is not GlassMorph morph) return;
        pill.ClearValue(MorphProperty);  // first, so the reset below doesn't shape it again
        pill.BeginAnimation(GrowProperty, null);
        pill.SetValue(GrowProperty, 1.0);
        pill.Width = double.NaN;
        pill.Margin = morph.RestMargin;
        morph.Squash.ScaleY = 1;
        if (LiquidGlass.GetContent(pill) is { } content) content.Opacity = 1;
    }

    private static void GlassGone(Border pill)
    {
        if (IsShown(pill)) return; // shown again meanwhile
        SettleGlass(pill);
        pill.Visibility = Visibility.Collapsed;
    }
}
