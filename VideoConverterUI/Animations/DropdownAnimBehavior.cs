using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoConverterUI.Animations;

public static class DropdownAnimBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(DropdownAnimBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;

        if ((bool)e.NewValue)
        {
            combo.DropDownOpened += OnDropDownOpened;
            // Not DropDownClosed: ComboBox raises that from its popup's Closed event, and the popup now stays
            // up until the close animation is over (HoldOpen).
            combo.SetBinding(IsDropDownOpenMirrorProperty,
                new Binding(nameof(ComboBox.IsDropDownOpen)) { RelativeSource = RelativeSource.Self, Mode = BindingMode.OneWay });
        }
        else
        {
            combo.DropDownOpened -= OnDropDownOpened;
            BindingOperations.ClearBinding(combo, IsDropDownOpenMirrorProperty);
        }
    }

    private static readonly DependencyProperty IsDropDownOpenMirrorProperty =
        DependencyProperty.RegisterAttached("IsDropDownOpenMirror", typeof(bool), typeof(DropdownAnimBehavior),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is ComboBox combo && !(bool)e.NewValue) BeginClose(combo);
            }));

    // ═══ Holding the popup open while it animates closed ═══
    // The templates bind PART_Popup.IsOpen to IsDropDownOpen, so the menu would vanish the moment the list closes.
    // On its first open the popup is re-bound to PopupShown instead, which goes false only once the close animation
    // has played (BeginClose); OpenCount tells a close that finishes after the list reopened to leave it be.

    private static readonly DependencyProperty PopupShownProperty =
        DependencyProperty.RegisterAttached("PopupShown", typeof(bool), typeof(DropdownAnimBehavior), new PropertyMetadata(false));

    private static readonly DependencyProperty HeldProperty =
        DependencyProperty.RegisterAttached("Held", typeof(bool), typeof(DropdownAnimBehavior), new PropertyMetadata(false));

    private static readonly DependencyProperty OpenCountProperty =
        DependencyProperty.RegisterAttached("OpenCount", typeof(int), typeof(DropdownAnimBehavior), new PropertyMetadata(0));

    private static void HoldOpen(ComboBox combo, Popup popup)
    {
        combo.SetValue(OpenCountProperty, (int)combo.GetValue(OpenCountProperty) + 1);
        combo.SetValue(PopupShownProperty, true); // first, so the new binding finds it open
        if ((bool)popup.GetValue(HeldProperty)) return; // a new template gets a new popup
        popup.SetBinding(Popup.IsOpenProperty, new Binding { Path = new PropertyPath(PopupShownProperty), Source = combo });
        popup.SetValue(HeldProperty, true);
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;

        var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
        if (popup == null || GetMenuSurface(popup) is not Border border) return;

        int selIdx = Math.Max(combo.SelectedIndex, 0);
        popup.Placement = PlacementMode.Relative;

        // Hidden until it has been positioned: on the first open the rows are not generated or
        // measured yet, and showing it early made the menu flash at the wrong spot.
        border.BeginAnimation(UIElement.OpacityProperty, null);
        border.Opacity = 0;

        HoldOpen(combo, popup);

        // Block immediate click-through: the selected item lands under the cursor,
        // so the mouse-up from the opening click would instantly select & close.
        border.IsHitTestVisible = false;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        guard.Tick += (_, _) => { guard.Stop(); border.IsHitTestVisible = true; };
        guard.Start();

        border.UpdateLayout();
        if (IsMeasured(combo, border, selIdx))
        {
            Present(combo, popup, border, selIdx);
        }
        else
        {
            combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (combo.IsDropDownOpen) Present(combo, popup, border, selIdx);
            });
        }
    }

    private static bool IsMeasured(ComboBox combo, Border surface, int selIdx) =>
        surface.ActualWidth > 0 &&
        (combo.Items.Count == 0 ||
         combo.ItemContainerGenerator.ContainerFromIndex(selIdx) is FrameworkElement { ActualHeight: > 0 });

    /// <summary>Positions the measured menu over the box and plays the open animation.</summary>
    private static void Present(ComboBox combo, Popup popup, Border border, int selIdx)
    {
        double selectedCenter = PositionOverCombo(combo, popup, border, selIdx);
        double totalH = border.ActualHeight;

        // --- Scale origin at selected item's relative position ---
        double originY = totalH > 0 ? Math.Clamp(selectedCenter / totalH, 0.05, 0.95) : 0.5;

        border.RenderTransformOrigin = new Point(0.5, originY);
        var theme = Services.ThemeService.Current ?? new Models.ThemeSettings();
        if (FluidMotion.AnimationsDisabled)
        {
            border.Opacity = 1;
            border.RenderTransform = Transform.Identity;
            return;
        }
        if (Liquid.IsActive)
        {
            PlayBubble(border);
            return;
        }

        string style = theme.DropdownAnimStyle ?? "Scale & Fade";
        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;
        var springDur = TimeSpan.FromMilliseconds(380);
        var fadeDur = TimeSpan.FromMilliseconds(160);

        if (style == "Fade Only")
        {
            border.RenderTransform = Transform.Identity;
            border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
        }
        else if (style == "Pop Out")
        {
            var st = new ScaleTransform(0.75, 0.75);
            border.RenderTransform = st;
            border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.75, 1, springDur) { EasingFunction = AppleSpringEase.Bouncy });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.75, 1, springDur) { EasingFunction = AppleSpringEase.Bouncy });
        }
        else if (style == "Accordion Slide")
        {
            border.RenderTransformOrigin = new Point(0.5, 0);
            var st = new ScaleTransform(1.0, 0.0);
            border.RenderTransform = st;
            border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.0, 1.0, springDur) { EasingFunction = smooth });
        }
        else // Scale & Fade (Default)
        {
            var st = new ScaleTransform(0.92, 0.92);
            border.RenderTransform = st;
            border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, springDur) { EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, springDur) { EasingFunction = spring });
        }

        if (theme.EnableStaggeredAnimations)
        {
            StaggerDropdownItems(combo, selIdx);
        }
    }

    /// <summary>
    /// With the Liquid motion, drop-downs open like the help bubble did before it (MainWindow.AnimatePopupIn): fade in,
    /// grow from 96 % around the selected row (so it stays on the box) and settle 4 px down, while the glass firms up
    /// from round corners into its own.
    /// </summary>
    private static void PlayBubble(Border surface)
    {
        var scale = new ScaleTransform(0.96, 0.96);
        var move = new TranslateTransform(0, -4);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(move);
        surface.RenderTransform = group;

        var spring = AppleSpringEase.Interactive;
        var dur = TimeSpan.FromMilliseconds(420);
        surface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = AppleSpringEase.Gentle });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = spring });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, dur) { EasingFunction = spring });

        // The glass is a LiquidGlassPanel inside the surface; its radius is the tuned one when not animated (NaN).
        if (surface.Child is Controls.LiquidGlassPanel glass)
        {
            double radius = Services.GlassTuning.Effective(glass.Surface).CornerRadius;
            var corners = new DoubleAnimation(55, radius, TimeSpan.FromMilliseconds(550)) { EasingFunction = AppleSpringEase.Bouncy };
            // Back to following the tuning (Glass Tuner, Liquid Glass look) once it has landed; not if the
            // list is already closing again (that animation owns the corners now).
            corners.Completed += (_, _) =>
            {
                if (Math.Abs(glass.CornerRadius - radius) < 0.01)
                    glass.BeginAnimation(Controls.LiquidGlassPanel.CornerRadiusProperty, null);
            };
            glass.BeginAnimation(Controls.LiquidGlassPanel.CornerRadiusProperty, corners);
        }
    }

    /// <summary>
    /// The animated menu surface. Templates wrap it in a host panel because WPF places the popup
    /// window from its child's transformed bounds: scaling the direct child would shift the window.
    /// </summary>
    private static Border? GetMenuSurface(Popup popup) => popup.Child switch
    {
        Border b => b,
        Panel p => p.Children.OfType<Border>().FirstOrDefault(),
        _ => null
    };

    /// <summary>
    /// Sizes the menu so it fully covers the box, scrolls the selected row into view and offsets the
    /// popup so that row's text sits on the box's text. Positions are measured relative to the popup's
    /// child (what Placement=Relative aligns to the box), so the surface's shadow margin and the
    /// checkmark gutter are accounted for without hard-coding either.
    /// Returns the selected row's vertical centre inside the surface (used as the scale origin).
    /// </summary>
    private static double PositionOverCombo(ComboBox combo, Popup popup, Border surface, int selIdx)
    {
        var host = popup.Child as FrameworkElement ?? surface;
        var site = combo.Template.FindName("ContentSite", combo) as FrameworkElement;
        double siteX = site?.TranslatePoint(new Point(0, 0), combo).X ?? 8;

        surface.MinWidth = 0;
        surface.UpdateLayout();

        double itemH = 26.0;
        double selectedCenter;
        double textX = 0;
        if (combo.ItemContainerGenerator.ContainerFromIndex(selIdx) is FrameworkElement sel && sel.ActualHeight > 0)
        {
            sel.BringIntoView();
            surface.UpdateLayout();
            itemH = sel.ActualHeight;
            selectedCenter = sel.TranslatePoint(new Point(0, sel.ActualHeight / 2.0), surface).Y;
            if (sel is Control c && c.Template?.FindName("ItemContent", c) is FrameworkElement content)
                textX = content.TranslatePoint(new Point(0, 0), surface).X;
        }
        else
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && first.ActualHeight > 0)
                itemH = first.ActualHeight;
            // Rows start where the scroll viewer does (inside the glass panel's margin).
            double topPad = surface.Padding.Top + surface.BorderThickness.Top;
            if (FindDescendant<ScrollViewer>(surface) is { } rows)
                topPad = rows.TranslatePoint(new Point(0, 0), surface).Y;
            selectedCenter = topPad + selIdx * itemH + itemH / 2.0;
        }

        // Surface origin inside the host (its shadow margin when wrapped, 0 when it is the child itself).
        // Layout offset, not TranslatePoint: the open animation may already be scaling the surface.
        var surfaceInHost = ReferenceEquals(host, surface) ? new Point(0, 0) : (Point)VisualTreeHelper.GetOffset(surface);

        // Fallback when the row template has no ItemContent part: surface edge on the box edge.
        double hOffset = textX > 0 ? siteX - (surfaceInHost.X + textX) : -surfaceInHost.X;
        double comboH = combo.ActualHeight > 0 ? combo.ActualHeight : itemH;

        // The surface starts left of the box (checkmark gutter); make it reach past the box's right edge.
        surface.MinWidth = Math.Max(0, combo.ActualWidth - (hOffset + surfaceInHost.X) + 6);

        popup.HorizontalOffset = hOffset;
        popup.VerticalOffset = comboH / 2.0 - (surfaceInHost.Y + selectedCenter);
        return selectedCenter;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// Closing (an item picked, a click elsewhere, Esc): the menu leaves like the help bubble does
    /// (MainWindow.AnimatePopupOut): fades, shrinks to 96 % towards the box and lifts 4 px while its corners round
    /// off, and only then does the popup close.
    /// </summary>
    private static void BeginClose(ComboBox combo)
    {
        var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
        if (popup == null) return;

        int openCount = (int)combo.GetValue(OpenCountProperty);
        void Finish()
        {
            if ((int)combo.GetValue(OpenCountProperty) != openCount) return; // reopened meanwhile
            combo.SetValue(PopupShownProperty, false);
            popup.Placement = PlacementMode.Bottom;
            popup.VerticalOffset = 0;
            popup.HorizontalOffset = 0;

            if (GetMenuSurface(popup) is FrameworkElement elem)
            {
                elem.BeginAnimation(UIElement.OpacityProperty, null);
                elem.Opacity = 1;
                elem.RenderTransform = Transform.Identity;
                if (elem is Border { Child: Controls.LiquidGlassPanel glass })
                    glass.BeginAnimation(Controls.LiquidGlassPanel.CornerRadiusProperty, null);
            }
        }

        if (!popup.IsOpen || GetMenuSurface(popup) is not Border border || FluidMotion.AnimationsDisabled)
        {
            Finish();
            return;
        }

        // Clicks pass nothing on while it fades.
        border.IsHitTestVisible = false;

        // From wherever the opening got to.
        if (border.RenderTransform is not TransformGroup { IsFrozen: false } group || group.Children.Count != 2
            || group.Children[0] is not ScaleTransform scale || group.Children[1] is not TranslateTransform move)
        {
            double s = border.RenderTransform is ScaleTransform st ? st.ScaleX : 1;
            scale = new ScaleTransform(s, s);
            move = new TranslateTransform();
            group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(move);
            border.RenderTransform = group;
        }

        var ease = AppleSpringEase.Snappy;
        var dur = TimeSpan.FromMilliseconds(160);
        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Finish();
        border.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, dur) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, dur) { EasingFunction = ease });
        if (border.Child is Controls.LiquidGlassPanel glass)
        {
            double radius = double.IsNaN(glass.CornerRadius) ? Services.GlassTuning.Effective(glass.Surface).CornerRadius : glass.CornerRadius;
            glass.BeginAnimation(Controls.LiquidGlassPanel.CornerRadiusProperty, new DoubleAnimation(radius, 55, dur) { EasingFunction = ease });
        }
    }

    private static void StaggerDropdownItems(ComboBox combo, int selIdx)
    {
        var spring = AppleSpringEase.Bouncy;
        var smooth = AppleSpringEase.Gentle;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not ComboBoxItem item)
                continue;

            int dist = Math.Abs(i - selIdx);
            double direction = i < selIdx ? -1 : (i > selIdx ? 1 : 0);
            double travel = direction * (6 + dist * 2);

            // Drop the previous open's fade first: it still holds opacity at 1, and during the new
            // fade's BeginTime delay that held value would show, so the row flashed on every re-open.
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 0;
            item.RenderTransformOrigin = new Point(0.5, 0.5);
            var tt = new TranslateTransform(0, travel);
            item.RenderTransform = tt;

            var delay = TimeSpan.FromMilliseconds(20 + dist * 30);

            item.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                { BeginTime = delay, EasingFunction = smooth });

            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(travel, 0, TimeSpan.FromMilliseconds(380))
                { BeginTime = delay, EasingFunction = spring });
        }
    }
}
