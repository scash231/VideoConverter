using System.Windows;
using System.Windows.Controls;

namespace VideoConverterUI.Controls;

/// <summary>
/// Gives every context menu the app's Liquid Glass look (the global ContextMenu / MenuItem styles in App.xaml).
/// Two kinds need help:
/// <list type="bullet">
/// <item>Text boxes and password boxes don't use their ContextMenu property for Cut / Copy / Paste: WPF's text editor
/// builds its own menu of internal types (TextEditorContextMenu+EditorContextMenu / EditorMenuItem) on every
/// right-click, which WPF-UI styles by type. Those types get the app's styles here.</item>
/// <item>Menus that come with an explicit style of their own: just before one opens, its explicit styles are
/// replaced by the app's.</item>
/// </list>
/// <see cref="Register"/> once at startup (after App.xaml's resources are loaded).
/// </summary>
public static class GlassContextMenus
{
    private const string EditorMenuTypes = "System.Windows.Documents.TextEditorContextMenu+";

    public static void Register()
    {
        StyleEditorMenus();
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnContextMenuOpening), handledEventsToo: true);
    }

    /// <summary>Implicit styles for the text editor's internal menu types, based on the app's ContextMenu / MenuItem ones.</summary>
    private static void StyleEditorMenus()
    {
        if (Application.Current is not { } app) return;
        var framework = typeof(TextBox).Assembly;
        foreach (var (name, baseType) in new[]
                 {
                     ("EditorContextMenu", typeof(ContextMenu)),
                     ("EditorMenuItem", typeof(MenuItem)),
                     ("ReconversionMenuItem", typeof(MenuItem)),  // IME suggestions
                 })
        {
            if (framework.GetType(EditorMenuTypes + name) is { } type && app.TryFindResource(baseType) is Style style)
                app.Resources[type] = new Style(type, style);
        }
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu }) Restyle(menu);
    }

    private static void Restyle(ContextMenu menu)
    {
        if (Application.Current?.TryFindResource(typeof(ContextMenu)) is Style menuStyle && !ReferenceEquals(menu.Style, menuStyle))
            menu.Style = menuStyle;
        RestyleItems(menu);
    }

    private static void RestyleItems(ItemsControl owner)
    {
        // An explicit item style (or container style) wins over the app's implicit one: drop it.
        if (owner.ItemContainerStyle != null) owner.ItemContainerStyle = null;
        foreach (var item in owner.Items)
        {
            switch (item)
            {
                case MenuItem menuItem:
                    if (menuItem.ReadLocalValue(FrameworkElement.StyleProperty) != DependencyProperty.UnsetValue)
                        menuItem.ClearValue(FrameworkElement.StyleProperty);
                    RestyleItems(menuItem);
                    break;
                case Separator separator
                    when Application.Current?.TryFindResource(MenuItem.SeparatorStyleKey) is Style separatorStyle
                         && !ReferenceEquals(separator.Style, separatorStyle):
                    separator.Style = separatorStyle;
                    break;
            }
        }
    }
}
