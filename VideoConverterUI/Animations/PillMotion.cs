using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoConverterUI.Animations;

/// <summary>
/// Shows and hides toolbar items (the header pills, the buttons and dividers inside them, their labels).
/// Animated, an item grows or shrinks sideways through a <see cref="FrameworkElement.LayoutTransform"/>, so the pill
/// around it changes width and its neighbours glide along with every frame; pills also pop up slightly as they appear.
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
}
