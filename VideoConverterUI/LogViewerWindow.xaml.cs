using VideoConverterUI.Animations;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace VideoConverterUI;

public partial class LogViewerWindow : Window
{
    private readonly Rect _originRect;
    private bool _isAnimatingClose;

    public LogViewerWindow(string logContent, Rect originRect, string? jobHash = null)
    {
        InitializeComponent();
        _originRect = originRect;
        TxtLog.Text = logContent;
        if (!string.IsNullOrWhiteSpace(jobHash))
        {
            var hashDisplay = jobHash.Trim().Replace("-", "");
            if (hashDisplay.StartsWith("#")) hashDisplay = hashDisplay[1..];
            if (hashDisplay.Length > 8) hashDisplay = hashDisplay[..8];
            TxtJobHash.Text = $"#{hashDisplay}";
            TxtJobHash.Visibility = Visibility.Visible;
        }
        else
        {
            TxtJobHash.Visibility = Visibility.Collapsed;
        }
        Loaded += OnLoaded;
    }

    private void WindowCornerGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Services.SettingsService.StartWindowResizeBottomRight(this);
        e.Handled = true; // no drag-move of the window underneath
    }

    private void WindowCornerGrip_MouseEnter(object sender, MouseEventArgs e)
        => Services.SettingsService.HandleGripHover(sender, true);

    private void WindowCornerGrip_MouseLeave(object sender, MouseEventArgs e)
        => Services.SettingsService.HandleGripHover(sender, false);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.SettingsService.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) => Services.SettingsService.SettingsChanged -= OnSettingsChanged;
        Services.SettingsService.ApplyResizeGripVisibility(this);
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            if (RootBorder != null) RootBorder.CornerRadius = new CornerRadius(0);
        }
        if (RootBorder != null)
            FluidMotion.MorphOpen(RootBorder, WinScale, WinTranslate, _originRect, this);
        TxtLog.ScrollToEnd();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Services.SettingsService.ApplyResizeGripVisibility(this);

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Copy_Click(object s, RoutedEventArgs e)
        => Clipboard.SetText(TxtLog.Text);

    private void Close_Click(object s, RoutedEventArgs e) => CloseWithAnimation();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            base.OnClosing(e);
            return;
        }

        if (!_isAnimatingClose) { e.Cancel = true; CloseWithAnimation(); }
        base.OnClosing(e);
    }

    private void CloseWithAnimation()
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;
        FluidMotion.MorphClose(RootBorder, WinScale, WinTranslate, this,
            () => Close());
    }
}
