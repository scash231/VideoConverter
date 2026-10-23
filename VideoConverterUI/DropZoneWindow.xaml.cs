using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VideoConverterUI.Animations;

namespace VideoConverterUI;

public partial class DropZoneWindow : Window
{
    private static DropZoneWindow? _activeZone;
    private readonly Window? _parentWindow;
    private readonly Action<string>? _onUrlDropped;
    private readonly DispatcherTimer _inactivityTimer;
    private bool _isClosing;

    public static new bool IsActive => _activeZone != null && !_activeZone._isClosing;

    public DropZoneWindow(Window? parentWindow, Action<string>? onUrlDropped)
    {
        InitializeComponent();
        _parentWindow = parentWindow ?? Application.Current.MainWindow;
        _onUrlDropped = onUrlDropped;

        if (_parentWindow != null && _parentWindow.IsLoaded)
        {
            try { Owner = _parentWindow; } catch { }
        }

        Topmost = true;

        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _inactivityTimer.Tick += (_, _) =>
        {
            _inactivityTimer.Stop();
            HideZone();
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePosition();
        AnimateIn();
        RefreshTimer();
    }

    public void RefreshTimer()
    {
        if (_isClosing) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    public void UpdatePosition()
    {
        if (_parentWindow != null && _parentWindow.IsVisible)
        {
            double parentCx = _parentWindow.Left + (_parentWindow.ActualWidth / 2);
            double targetW = ActualWidth > 0 ? ActualWidth : Width;
            Left = parentCx - (targetW / 2);
            Top = _parentWindow.Top + 65;
        }
        else
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2) - (Width / 2);
            Top = SystemParameters.WorkArea.Top + 100;
        }
    }

    private void AnimateIn()
    {
        if (Liquid.IsActive)
        {
            Liquid.Arrive(this, WindowScale, WindowTranslate, 0.82, 0.7, -20, Spring.Jelly);
            return;
        }

        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;

        Opacity = 0;

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            { EasingFunction = smooth });

        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });

        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });

        WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });
    }

    public void HideZone()
    {
        if (_isClosing) return;
        _isClosing = true;
        _inactivityTimer.Stop();

        if (Liquid.IsActive)
        {
            Liquid.Leave(this, WindowScale, WindowTranslate, 0.88, 0.8, -18, done: Close);
            if (_activeZone == this) _activeZone = null;
            return;
        }

        var ease = AppleSpringEase.Snappy;
        var dur = TimeSpan.FromMilliseconds(180);

        var opAnim = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        opAnim.Completed += (_, _) => Close();

        BeginAnimation(OpacityProperty, opAnim);

        WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -18, dur) { EasingFunction = ease });

        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.88, dur) { EasingFunction = ease });

        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.88, dur) { EasingFunction = ease });

        if (_activeZone == this)
        {
            _activeZone = null;
        }
    }

    public void MorphAway(Rect? targetRect = null)
    {
        if (_isClosing) return;
        _isClosing = true;
        _inactivityTimer.Stop();

        var spring = AppleSpringEase.Interactive;
        var dur = TimeSpan.FromMilliseconds(280);

        double targetScaleX = 0.35;
        double targetScaleY = 0.35;
        double targetTranslateX = 0;
        double targetTranslateY = 50;

        if (targetRect.HasValue && _parentWindow != null)
        {
            try
            {
                var curPos = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
                var targetPos = new Point(targetRect.Value.Left + targetRect.Value.Width / 2, targetRect.Value.Top + targetRect.Value.Height / 2);
                targetTranslateX = targetPos.X - curPos.X;
                targetTranslateY = targetPos.Y - curPos.Y;
                if (ActualWidth > 0 && ActualHeight > 0)
                {
                    targetScaleX = Math.Max(0.2, targetRect.Value.Width / ActualWidth);
                    targetScaleY = Math.Max(0.2, targetRect.Value.Height / ActualHeight);
                }
            }
            catch { }
        }

        if (Liquid.IsActive)
        {
            // Pours into whatever it dropped its link into.
            Liquid.Leave(this, WindowScale, WindowTranslate, targetScaleX, targetScaleY, targetTranslateY, toX: targetTranslateX,
                done: Close, spring: Spring.WithBounce(0.34, 0));
            if (_activeZone == this) _activeZone = null;
            return;
        }

        var opAnim = new DoubleAnimation(1, 0, dur) { EasingFunction = AppleSpringEase.Gentle };
        opAnim.Completed += (_, _) => Close();

        BeginAnimation(OpacityProperty, opAnim);

        WindowTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(WindowTranslate.X, targetTranslateX, dur) { EasingFunction = spring });

        WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(WindowTranslate.Y, targetTranslateY, dur) { EasingFunction = spring });

        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(WindowScale.ScaleX, targetScaleX, dur) { EasingFunction = spring });

        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(WindowScale.ScaleY, targetScaleY, dur) { EasingFunction = spring });

        if (_activeZone == this)
        {
            _activeZone = null;
        }
    }

    public void SetLoading(string loadingText)
    {
        _inactivityTimer.Stop();
        GridLoadingBar.Visibility = Visibility.Visible;
        TxtTitle.Text = loadingText;

        // Smooth pulse animation on the icon
        var pulseAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 1.18,
            Duration = TimeSpan.FromMilliseconds(650),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AppleSpringEase.Interactive
        };
        IconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
        IconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);

        // Smooth continuous sliding shimmer animation for liquid glass loading bar
        var shimmerAnimation = new DoubleAnimation
        {
            From = -120,
            To = 200,
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        LoadingBarTranslate.BeginAnimation(TranslateTransform.XProperty, shimmerAnimation);
    }

    private void Cancel_Click(object? sender, EventArgs? e)
    {
        // HideZone() itself sets _isClosing; setting it here first made HideZone return
        // immediately, so the drop zone never closed and stayed on screen.
        HideZone();
        if (e is RoutedEventArgs re) re.Handled = true;
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        RefreshTimer();
        if (IsLinkDrag(e))
        {
            e.Effects = DragDropEffects.Copy;
            AnimateHoverState(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        RefreshTimer();
        if (IsLinkDrag(e))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        AnimateHoverState(false);
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        _inactivityTimer.Stop();
        if (IsLinkDrag(e))
        {
            var url = GetDroppedLink(e);
            if (!string.IsNullOrEmpty(url))
            {
                _onUrlDropped?.Invoke(url);
                return;
            }
        }
        HideZone();
        e.Handled = true;
    }

    private void AnimateHoverState(bool isHovered)
    {
        var spring = AppleSpringEase.Interactive;
        double targetScale = isHovered ? 1.03 : 1.0;

        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(WindowScale.ScaleX, targetScale, TimeSpan.FromMilliseconds(220))
            { EasingFunction = spring });

        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(WindowScale.ScaleY, targetScale, TimeSpan.FromMilliseconds(220))
            { EasingFunction = spring });
    }

    public static bool IsLinkDrag(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.Text) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = GetDragText(e);
            if (!string.IsNullOrEmpty(text))
            {
                var lower = text.Trim().ToLowerInvariant();
                return lower.StartsWith("http://") ||
                       lower.StartsWith("https://") ||
                       lower.Contains("youtube.com") ||
                       lower.Contains("youtu.be") ||
                       lower.Contains("instagram.com") ||
                       lower.Contains("tiktok.com");
            }
        }
        return false;
    }

    public static string GetDragText(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                return e.Data.GetData(DataFormats.UnicodeText) as string ?? "";
            if (e.Data.GetDataPresent(DataFormats.Text))
                return e.Data.GetData(DataFormats.Text) as string ?? "";
        }
        catch { }
        return "";
    }

    public static string GetDroppedLink(DragEventArgs e)
    {
        return GetDragText(e).Trim();
    }

    public static void ShowZone(Window parentWindow, Action<string> onUrlDropped)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_activeZone != null && !_activeZone._isClosing)
            {
                _activeZone.UpdatePosition();
                _activeZone.RefreshTimer();
                return;
            }

            var zone = new DropZoneWindow(parentWindow, onUrlDropped);
            _activeZone = zone;
            zone.Show();
        });
    }

    public static void RefreshTimerStatic()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _activeZone?.RefreshTimer();
        });
    }

    public static void MorphAwayStatic(Rect? targetRect = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_activeZone != null && !_activeZone._isClosing)
            {
                _activeZone.MorphAway(targetRect);
            }
            else
            {
                _activeZone?.HideZone();
            }
        });
    }

    public static void SetLoadingStatic(string loadingText)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _activeZone?.SetLoading(loadingText);
        });
    }
}
