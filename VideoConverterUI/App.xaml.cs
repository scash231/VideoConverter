using System.Windows;
using System.Windows.Threading;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Enforce WPF DirectX hardware rendering acceleration
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        // Load (or, the very first time, compile) the Liquid Glass shader in the background, so the first glass
        // window doesn't stall its opening animation for it.
        Controls.LiquidGlassEffect.Warmup();

        ThemeService.Apply(ThemeService.Current);
        SettingsService.ApplyScrollbarVisibility();
        Animations.SmoothScrollBehavior.Register();
        Controls.GlassContextMenus.Register();
        Animations.ScrollBarAutoHide.Register();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Persist a theme change that is still waiting for its deferred save.
        ThemeService.FlushPendingSave();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var msg = e.Exception.ToString();
        if (e.Exception.InnerException is not null)
            msg = e.Exception.InnerException.ToString() + "\n\n" + msg;
        NotificationWindow.Show(msg, null, NotificationType.Error);
        e.Handled = true;
    }
}
