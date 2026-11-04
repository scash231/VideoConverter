using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

        (border.GetValue(RigProperty) as MorphRig)?.Release();

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
            PlayMorph(combo, popup, border);
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

    // ═══ glisse's opening and closing (the Liquid motion; github.com/0kzh/glisse, src/menu.tsx) ═══
    // The glass grows out of the box while the rows scale up from the box's centre and come into focus; the box fades
    // away under it. Closing runs it back on a quicker spring. One value, 0 closed to 1 open, drives all of it.

    // glisse's springs: stiffness 600, damping 46 to open; 900, 57 to close.
    private static readonly Spring MorphOpen = new(0.2565, 0.939);
    private static readonly Spring MorphClose = new(0.2094, 0.95);

    private static readonly DependencyProperty MorphProperty =
        DependencyProperty.RegisterAttached("Morph", typeof(double), typeof(DropdownAnimBehavior),
            new PropertyMetadata(0.0, (d, e) => (d.GetValue(RigProperty) as MorphRig)?.Paint((double)e.NewValue)));

    private static readonly DependencyProperty RigProperty =
        DependencyProperty.RegisterAttached("Rig", typeof(object), typeof(DropdownAnimBehavior), new PropertyMetadata(null));

    /// <summary>
    /// Holds a menu in pieces while it morphs: the surface keeps its full size (so the popup window stays still), the
    /// glass inside it takes the morphing rectangle, and the rows keep their size and place, cut by the glass's clip.
    /// </summary>
    private sealed class MorphRig
    {
        private readonly Border _surface;
        private readonly FrameworkElement _shell, _content;
        private readonly UIElement? _box;
        private readonly Rect _from;
        private readonly Size _to;
        private readonly Point _contentAt;
        private readonly ScaleTransform _scale;
        private readonly BlurEffect _blur = new();
        private readonly Effect? _contentEffect;

        public bool Leaving { get; set; }

        private MorphRig(ComboBox combo, Popup popup, Border surface, FrameworkElement shell, FrameworkElement content)
        {
            _surface = surface;
            _shell = shell;
            _content = content;
            _box = BoxOf(combo);
            _to = new Size(surface.ActualWidth, surface.ActualHeight);
            _contentAt = content.TranslatePoint(new Point(0, 0), surface);

            // Where the box lies inside the surface (the popup's offsets are relative to the box).
            var inHost = (Point)VisualTreeHelper.GetOffset(surface);
            var box = new Rect(-(popup.HorizontalOffset + inHost.X), -(popup.VerticalOffset + inHost.Y), combo.ActualWidth, combo.ActualHeight);
            box.Intersect(new Rect(_to));
            _from = box.IsEmpty ? new Rect(_to.Width / 2, _to.Height / 2, 0, 0) : box;

            double width = content.ActualWidth, height = content.ActualHeight;
            _scale = new ScaleTransform(1, 1, _from.X + _from.Width / 2 - _contentAt.X, _from.Y + _from.Height / 2 - _contentAt.Y);

            surface.Width = _to.Width;
            surface.Height = _to.Height;
            shell.HorizontalAlignment = HorizontalAlignment.Left;
            shell.VerticalAlignment = VerticalAlignment.Top;
            content.Width = width;
            content.Height = height;
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;
            content.RenderTransform = _scale;
            _contentEffect = content.Effect;
            content.Effect = _blur;
            surface.SetValue(RigProperty, this);
        }

        public static MorphRig? Create(ComboBox combo, Popup popup, Border surface)
        {
            if (FindDescendant<Controls.LiquidGlassPanel>(surface) is not { } glass) return null;
            if (glass.Children.OfType<Controls.UnclippedDecorator>().FirstOrDefault() is not { } content) return null;
            return new MorphRig(combo, popup, surface, glass.Parent as FrameworkElement ?? glass, content);
        }

        public void Paint(double value)
        {
            double t = Math.Clamp(value, 0, 1);
            double x = _from.X * (1 - t), y = _from.Y * (1 - t);
            double fade = Math.Clamp(t / 0.2, 0, 1);

            _shell.Width = _from.Width + (_to.Width - _from.Width) * t;
            _shell.Height = _from.Height + (_to.Height - _from.Height) * t;
            _shell.Margin = new Thickness(x, y, 0, 0);
            _surface.Opacity = fade;
            if (_box != null) _box.Opacity = 1 - fade;

            double scale = 0.5 + 0.5 * t;
            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
            _content.Margin = new Thickness(_contentAt.X - x, _contentAt.Y - y, 0, 0);
            _content.Opacity = Math.Clamp(Leaving ? (t - 0.55) / 0.45 : (t - 0.15) / 0.6, 0, 1);
            _blur.Radius = 12 * (1 - t);
        }

        /// <summary>Puts the menu back together at its full size.</summary>
        public void Release()
        {
            _surface.ClearValue(RigProperty);
            _surface.ClearValue(FrameworkElement.WidthProperty);
            _surface.ClearValue(FrameworkElement.HeightProperty);
            _surface.Opacity = 1;
            foreach (var dp in new[] { FrameworkElement.WidthProperty, FrameworkElement.HeightProperty, FrameworkElement.MarginProperty,
                         FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty })
            {
                _shell.ClearValue(dp);
                _content.ClearValue(dp);
            }
            _content.ClearValue(UIElement.RenderTransformProperty);
            _content.ClearValue(UIElement.OpacityProperty);
            _content.Effect = _contentEffect;
        }
    }

    /// <summary>The closed box, what fades under the menu: the template's root (a popup's content isn't under it).</summary>
    private static UIElement? BoxOf(ComboBox combo) =>
        VisualTreeHelper.GetChildrenCount(combo) > 0 ? VisualTreeHelper.GetChild(combo, 0) as UIElement : null;

    private static void PlayMorph(ComboBox combo, Popup popup, Border surface)
    {
        surface.RenderTransform = Transform.Identity;
        surface.UpdateLayout();  // PositionOverCombo has just changed its MinWidth
        if (MorphRig.Create(combo, popup, surface) is not { } rig)
        {
            surface.Opacity = 1;
            return;
        }
        rig.Paint((double)surface.GetValue(MorphProperty));
        Liquid.To(surface, MorphProperty, 1, MorphOpen, done: () =>
        {
            if (!ReferenceEquals(surface.GetValue(RigProperty), rig)) return;
            rig.Release();
            surface.BeginAnimation(MorphProperty, null);
            surface.SetValue(MorphProperty, 1.0);
        });
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
    /// Closing (an item picked, a click elsewhere, Esc): with the Liquid motion the opening runs back into the box;
    /// otherwise the menu fades, shrinks to 96 % towards the box and lifts 4 px while its corners round off. Only then
    /// does the popup close.
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
            if (GetMenuSurface(popup) is Border surface)
            {
                (surface.GetValue(RigProperty) as MorphRig)?.Release();
                surface.BeginAnimation(MorphProperty, null);
                surface.SetValue(MorphProperty, 0.0);
            }
            BoxOf(combo)?.ClearValue(UIElement.OpacityProperty);
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

        if (Liquid.IsActive)
        {
            double morph = (double)border.GetValue(MorphProperty);
            var rig = border.GetValue(RigProperty) as MorphRig;
            if (morph <= 0)
            {
                Finish();
                return;
            }
            rig ??= MorphRig.Create(combo, popup, border);
            if (rig != null)
            {
                rig.Leaving = true;
                rig.Paint(morph);
                Liquid.To(border, MorphProperty, 0, MorphClose, done: Finish);
                return;
            }
        }

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
