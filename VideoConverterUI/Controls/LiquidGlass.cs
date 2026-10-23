using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VideoConverterUI.Services;

namespace VideoConverterUI.Controls;

/// <summary>
/// Turns an existing surface (a <see cref="Border"/> or <see cref="SquircleBorder"/>, e.g. a popup's or a dialog's
/// root) into Liquid Glass without restructuring its XAML: <c>controls:LiquidGlass.Surface="Popup"</c>.
/// The border's child moves into a <see cref="LiquidGlassPanel"/>; the border keeps its size, transforms,
/// animations and shadow (which the panel then drives) but stops painting itself: its padding becomes the
/// child's margin, its brush the glass outline, its corner radius (also while animated) the glass's.
/// <see cref="BackdropProperty"/> sets what shows through when the panel can't find it by itself (an overlay
/// inside the same window, like an alert sheet); see <see cref="LiquidGlassPanel"/>.
/// </summary>
public static class LiquidGlass
{
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached(
        "Surface", typeof(string), typeof(LiquidGlass), new PropertyMetadata(null, OnSurfaceChanged));

    public static readonly DependencyProperty BackdropProperty = DependencyProperty.RegisterAttached(
        "Backdrop", typeof(Visual), typeof(LiquidGlass), new PropertyMetadata(null, OnBackdropChanged));

    public static string? GetSurface(DependencyObject d) => (string?)d.GetValue(SurfaceProperty);
    public static void SetSurface(DependencyObject d, string? value) => d.SetValue(SurfaceProperty, value);

    public static Visual? GetBackdrop(DependencyObject d) => (Visual?)d.GetValue(BackdropProperty);
    public static void SetBackdrop(DependencyObject d, Visual? value) => d.SetValue(BackdropProperty, value);

    /// <summary>The glass created for a converted surface (null until it is initialized).</summary>
    public static LiquidGlassPanel? GetPanel(Decorator host) => host.Child as LiquidGlassPanel;

    private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Decorator host || e.NewValue is not string surface) return;
        if (GetPanel(host) is { } existing)
        {
            existing.Surface = surface;
            return;
        }
        // Wait for XAML to set the child, padding and brushes.
        if (host.IsInitialized) Convert(host, surface);
        else host.Initialized += (_, _) => Convert(host, GetSurface(host) ?? surface);
    }

    private static void OnBackdropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Decorator host && GetPanel(host) is { } panel)
            panel.BackdropSource = (Visual?)e.NewValue;
    }

    private static void Convert(Decorator host, string surface)
    {
        if (GetPanel(host) != null) return;

        var child = host.Child;
        host.Child = null;
        var panel = new LiquidGlassPanel { Surface = surface, BackdropSource = GetBackdrop(host) };
        if (child != null) panel.Children.Add(child);

        switch (host)
        {
            case Border b:
                if (child is FrameworkElement fe) fe.Margin = Add(fe.Margin, b.Padding);
                b.Padding = new Thickness(0);
                Follow(b, Border.CornerRadiusProperty, panel, () => b.CornerRadius.TopLeft);
                BindOutline(panel, b, Border.BorderBrushProperty);
                b.BorderThickness = new Thickness(0);
                b.Background = Brushes.Transparent;  // still takes clicks; the glass paints
                break;
            case SquircleBorder s:
                Follow(s, SquircleBorder.CornerRadiusProperty, panel, () => s.CornerRadius);
                BindOutline(panel, s, SquircleBorder.BorderBrushProperty);
                s.BorderThickness = new Thickness(0);
                s.Background = Brushes.Transparent;
                break;
        }

        host.Child = panel;
    }

    /// <summary>Keeps the glass's corner radius on the border's, including corner animations.</summary>
    private static void Follow(DependencyObject source, DependencyProperty property, LiquidGlassPanel panel, Func<double> radius)
    {
        panel.CornerRadius = radius();
        DependencyPropertyDescriptor.FromProperty(property, source.GetType())
            ?.AddValueChanged(source, (_, _) => panel.CornerRadius = radius());
    }

    /// <summary>The border's own brush (often a DynamicResource, or red for the error popup) becomes the outline.</summary>
    private static void BindOutline(LiquidGlassPanel panel, DependencyObject source, DependencyProperty brush)
    {
        if (source.GetValue(brush) == null) return;
        BindingOperations.SetBinding(panel, LiquidGlassPanel.OutlineBrushProperty, new Binding { Source = source, Path = new PropertyPath(brush) });
    }

    private static Thickness Add(Thickness a, Thickness b) => new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
}
