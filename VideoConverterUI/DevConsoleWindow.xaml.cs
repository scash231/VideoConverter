using VideoConverterUI.Animations;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoConverterUI;

public partial class DevConsoleWindow : Window
{
    private readonly Rect _originRect;
    private bool _isAnimatingClose;
    private readonly List<string> _cmdHistory = new();
    private int _historyIndex = -1;
    private int _logLength;
    private const int MaxLogChars = 250_000;

    /// <summary>Fired when the user enters a command. The handler should process it.</summary>
    public event Action<string>? CommandEntered;

    public DevConsoleWindow(string existingLog, Rect originRect)
    {
        InitializeComponent();
        _originRect = originRect;
        TxtConsole.Text = existingLog;
        _logLength = existingLog.Length;
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
        FluidMotion.MorphOpen(RootBorder, WinScale, WinTranslate, _originRect, this);
        TxtConsole.ScrollToEnd();
        TxtInput.Focus();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Services.SettingsService.ApplyResizeGripVisibility(this);

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        // The help pop-up sits in the title bar and routes its clicks up to here.
        if (Services.SettingsService.IsFromPopup(this, e.OriginalSource)) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    public void AppendLog(string line)
    {
        // Track the length ourselves: reading TextBox.Text copies the whole log on every call.
        if (_logLength > MaxLogChars)
        {
            var text = TxtConsole.Text;
            var cut = text.IndexOf('\n', text.Length / 2);
            TxtConsole.Text = cut >= 0 ? text[(cut + 1)..] : string.Empty;
            _logLength = TxtConsole.Text.Length;
        }

        var chunk = _logLength == 0 ? line : Environment.NewLine + line;
        TxtConsole.AppendText(chunk);
        _logLength += chunk.Length;
        TxtConsole.ScrollToEnd();
    }

    public void ClearConsole()
    {
        TxtConsole.Clear();
        _logLength = 0;
    }

    private void Clear_Click(object s, RoutedEventArgs e) => ClearConsole();

    private bool _isClosingConsoleHelpPopup;
    private DateTime _consoleHelpPopupClosedTime = DateTime.MinValue;

    private void Help_Click(object s, RoutedEventArgs e)
    {
        if (ConsoleHelpPopup == null) return;
        if ((DateTime.UtcNow - _consoleHelpPopupClosedTime).TotalMilliseconds < 250) return;

        ConsoleHelpPopup.PlacementTarget = (UIElement)s;
        if (!ConsoleHelpPopup.IsOpen && !_isClosingConsoleHelpPopup)
        {
            ConsoleHelpPopup.IsOpen = true;
            AnimatePopupIn(HelpPopupBorder);
        }
        else
        {
            CloseConsoleHelpPopupAnimated();
        }
    }

    private void CloseConsoleHelpPopupAnimated()
    {
        if (_isClosingConsoleHelpPopup || ConsoleHelpPopup == null || !ConsoleHelpPopup.IsOpen) return;
        _isClosingConsoleHelpPopup = true;
        _consoleHelpPopupClosedTime = DateTime.UtcNow;
        AnimatePopupOut(HelpPopupBorder, () =>
        {
            ConsoleHelpPopup.IsOpen = false;
            _isClosingConsoleHelpPopup = false;
        });
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (ConsoleHelpPopup != null && ConsoleHelpPopup.IsOpen && !_isClosingConsoleHelpPopup)
        {
            if (!IsMouseInsideBorder(HelpPopupBorder, e))
            {
                CloseConsoleHelpPopupAnimated();
            }
        }
    }

    private static bool IsMouseInsideBorder(System.Windows.Controls.Border border, MouseButtonEventArgs e)
    {
        if (border == null || !border.IsVisible) return false;
        try
        {
            Point p = e.GetPosition(border);
            return p.X >= 0 && p.X <= border.ActualWidth && p.Y >= 0 && p.Y <= border.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private static void AnimatePopupIn(System.Windows.Controls.Border border)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            border.Opacity = 1;
            border.RenderTransform = Transform.Identity;
            FluidMotion.SetCornerRadiusImmediate(border, 0);
            return;
        }
        var spring = AppleSpringEase.Interactive;
        var bouncy = AppleSpringEase.Bouncy;
        var smooth = AppleSpringEase.Gentle;
        var group = (TransformGroup)border.RenderTransform;
        var st = (ScaleTransform)group.Children[0];
        var tt = (TranslateTransform)group.Children[1];

        // Reset corner radius to bubbly state
        FluidMotion.SetCornerRadiusImmediate(border, 55);

        border.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = smooth });
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });

        // Morph corner radius to normal rounded corner (12) using a spring
        FluidMotion.AnimateCornerRadius(border, 12, TimeSpan.FromMilliseconds(550), bouncy);
    }

    private static void AnimatePopupOut(System.Windows.Controls.Border border, Action onDone)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            border.Opacity = 0;
            border.RenderTransform = Transform.Identity;
            onDone();
            return;
        }
        var ease = AppleSpringEase.Snappy;
        var group = (TransformGroup)border.RenderTransform;
        var st = (ScaleTransform)group.Children[0];
        var tt = (TranslateTransform)group.Children[1];

        // Morph corner radius back to bubbly state (55) quickly
        FluidMotion.AnimateCornerRadius(border, 55, TimeSpan.FromMilliseconds(160), ease);

        var opAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160))
        { EasingFunction = ease };
        opAnim.Completed += (_, _) => onDone();

        border.BeginAnimation(UIElement.OpacityProperty, opAnim);
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(160))
            { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(160))
            { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(160))
            { EasingFunction = ease });
    }

    private void Minimize_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Reattach_Click(object s, RoutedEventArgs e) => CloseWithAnimation();

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

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var cmd = TxtInput.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            _cmdHistory.Add(cmd);
            _historyIndex = _cmdHistory.Count;
            AppendLog($"> {cmd}");
            CommandEntered?.Invoke(cmd);
            TxtInput.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (_cmdHistory.Count > 0 && _historyIndex > 0)
            {
                _historyIndex--;
                TxtInput.Text = _cmdHistory[_historyIndex];
                TxtInput.CaretIndex = TxtInput.Text.Length;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (_historyIndex < _cmdHistory.Count - 1)
            {
                _historyIndex++;
                TxtInput.Text = _cmdHistory[_historyIndex];
                TxtInput.CaretIndex = TxtInput.Text.Length;
            }
            else
            {
                _historyIndex = _cmdHistory.Count;
                TxtInput.Clear();
            }
            e.Handled = true;
        }
    }

    private void CloseWithAnimation()
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;
        FluidMotion.MorphClose(RootBorder, WinScale, WinTranslate, this,
            () => Close());
    }
}
