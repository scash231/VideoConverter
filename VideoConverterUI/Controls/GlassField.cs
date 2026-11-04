using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;

namespace VideoConverterUI.Controls;

/// <summary>
/// What glisse's search field (github.com/0kzh/glisse, src/input.tsx) adds to a text box in the <c>GlassSearchField</c> style:
/// <see cref="IconProperty"/>, the glyph before the text, <see cref="InsetProperty"/>, and <see cref="ClearButtonProperty"/>, which makes a button in the
/// template empty the box and keep the focus in it.
/// </summary>
public static class GlassField
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(SymbolRegular), typeof(GlassField), new PropertyMetadata(SymbolRegular.Search24));

    public static readonly DependencyProperty InsetProperty = DependencyProperty.RegisterAttached(
        "Inset", typeof(Thickness), typeof(GlassField), new PropertyMetadata(new Thickness(14, 0, 6, 0)));

    public static readonly DependencyProperty ClearButtonProperty = DependencyProperty.RegisterAttached(
        "ClearButton", typeof(bool), typeof(GlassField), new PropertyMetadata(false, OnClearButtonChanged));

    public static SymbolRegular GetIcon(DependencyObject d) => (SymbolRegular)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, SymbolRegular value) => d.SetValue(IconProperty, value);
    /// <summary>How far the glyph and the clear button sit in from the ends. Not the box's Padding: a text box also gives that to its text.</summary>
    public static Thickness GetInset(DependencyObject d) => (Thickness)d.GetValue(InsetProperty);
    public static void SetInset(DependencyObject d, Thickness value) => d.SetValue(InsetProperty, value);
    public static bool GetClearButton(DependencyObject d) => (bool)d.GetValue(ClearButtonProperty);
    public static void SetClearButton(DependencyObject d, bool value) => d.SetValue(ClearButtonProperty, value);

    private static void OnClearButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClear;
        if ((bool)e.NewValue) button.Click += OnClear;
    }

    private static void OnClear(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { TemplatedParent: System.Windows.Controls.TextBox box }) return;
        box.Clear();
        box.Focus();
    }
}
