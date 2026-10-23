using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoConverterUI.Helpers.EasterEggs;

public class EasterEggOrchestrator
{
    private readonly ChristmasTreeEasterEgg? _christmasTree;
    private readonly DiscoGlobeEasterEgg? _discoGlobe;
    private readonly SnowfallEasterEgg? _snowfall;
    private readonly SpiderEasterEgg? _spider;

    public bool IsComicSansActive { get; private set; }

    public EasterEggOrchestrator(
        Canvas? christmasTreeCanvas,
        Canvas? discoGlobeCanvas,
        TranslateTransform? discoLowerTransform,
        Canvas? snowCanvas,
        FrameworkElement? snowBoundary,
        Canvas? rootOverlayCanvas)
    {
        if (christmasTreeCanvas != null)
            _christmasTree = new ChristmasTreeEasterEgg(christmasTreeCanvas);

        if (discoGlobeCanvas != null && discoLowerTransform != null)
            _discoGlobe = new DiscoGlobeEasterEgg(discoGlobeCanvas, discoLowerTransform);

        if (snowCanvas != null && snowBoundary != null)
            _snowfall = new SnowfallEasterEgg(snowCanvas, snowBoundary);

        if (rootOverlayCanvas != null)
            _spider = new SpiderEasterEgg(rootOverlayCanvas);
    }

    public void Toggle(bool enable)
    {
        IsComicSansActive = enable;

        // Custom Fire Sword Cursor across entire application
        try
        {
            Mouse.OverrideCursor = enable ? FireSwordCursorHelper.GetFireSwordCursor() : null;
        }
        catch { }

        // Trigger individual secret scripts
        if (enable)
        {
            _christmasTree?.Start();
            _discoGlobe?.Start();
            _snowfall?.Start();
            _spider?.Start();
        }
        else
        {
            _christmasTree?.Stop();
            _discoGlobe?.Stop();
            _snowfall?.Stop();
            _spider?.Stop();
        }

        // Apply Font Changes across all open windows
        ApplyFont(enable);
    }

    private void ApplyFont(bool enable)
    {
        FontFamily fallbackFont = new FontFamily("Segoe UI");
        if (Application.Current.TryFindResource("AppFont") is FontFamily fontRes)
        {
            fallbackFont = fontRes;
        }

        var font = enable ? new FontFamily("Comic Sans MS") : fallbackFont;

        try
        {
            foreach (Window win in Application.Current.Windows)
            {
                if (win != null)
                {
                    try
                    {
                        System.Windows.Documents.TextElement.SetFontFamily(win, font);
                        win.FontFamily = font;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
