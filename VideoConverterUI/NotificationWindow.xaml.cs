using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public enum NotificationType
{
    Info,
    Warning,
    Error,
    Success,
    Custom
}

public partial class NotificationWindow : Window
{
    // ═══ Stack ═══
    // Banners stack under each other, newest on top: a new one flies in at the top and the older ones slide down to
    // make room; when one goes, the ones below slide up. Each banner is still its own window (so the glass and the
    // placement over its parent work as before); the stack only decides where each card sits.
    // The windows stay where they opened, at the top of the stack, and the cards slide inside them (_stackSlide):
    // moving a window makes WPF wait for the frame being drawn (in any window), so a window sliding down frame by
    // frame dropped everything, the new banner's fly-in included, to about 25 fps. A window only grows at the bottom
    // before its card slides down and shrinks again once it has settled (SetSlideRoom).
    private static readonly List<NotificationWindow> Stack = new(); // newest first
    private const int MaxStacked = 4;
    private const double StackGap = 8;
    private const double SlideOvershoot = 12;  // extra room while sliding, for the spring's overshoot
    private bool _placed;
    private readonly string _message;
    private readonly TranslateTransform _stackSlide = new();  // the card's place in the stack, below the window's top
    private double _slideTarget;
    private double _bottomMargin;  // RootGrid's own, before any slide room

    private static readonly ExponentialEase ExpoEaseInOut = new ExponentialEase
    {
        Exponent = 3.0,
        EasingMode = EasingMode.EaseInOut
    };

    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _autoDismissDuration;
    private readonly bool _isPersistent;
    private bool _isClosing;
    private readonly Window? _parentWindow;
    private readonly Action? _onClick;
    private Brush _normalBorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private Brush _accentProgressBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

    public NotificationWindow(
        string message,
        NotificationType type,
        Window? parentWindow = null,
        Action? onClick = null,
        string? actionText = null,
        bool persistent = false)
    {
        InitializeComponent();
        RootGrid.RenderTransform = _stackSlide;
        _bottomMargin = RootGrid.Margin.Bottom;
        _onClick = onClick;
        _message = message ?? string.Empty;

        var app = Application.Current;
        _parentWindow = parentWindow
            ?? app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not NotificationWindow && w.IsVisible)
            ?? app?.MainWindow;

        if (_parentWindow != null && _parentWindow != this && _parentWindow is not NotificationWindow && _parentWindow.IsLoaded && _parentWindow.IsVisible)
        {
            try { Owner = _parentWindow; } catch { }
        }
        try { Topmost = true; } catch { }

        if (TxtMessage != null)
            TxtMessage.Text = message ?? string.Empty;

        // Configure cursor and tooltip for interactive (clickable) notifications
        if (_onClick != null)
        {
            if (RootBorder != null)
            {
                RootBorder.Cursor = Cursors.Hand;
                var defaultTooltip = app?.TryFindResource("BtnOpen") as string ?? "Click to open";
                RootBorder.ToolTip = !string.IsNullOrWhiteSpace(actionText) ? actionText : defaultTooltip;
            }
        }
        else
        {
            if (RootBorder != null)
            {
                RootBorder.Cursor = Cursors.Arrow;
                RootBorder.ToolTip = null;
            }
        }

        // Setup visual styling (Blue = Regular/Info, Green = Working/Success, Red = Failed/Error)
        SetupStyle(type);

        // Configure auto-dismiss timer duration from settings (0 = stay until dismissed)
        var seconds = persistent ? 0 : GetDurationSeconds(type);
        _isPersistent = seconds <= 0;
        _autoDismissDuration = TimeSpan.FromSeconds(_isPersistent ? 1 : seconds);
        if (_isPersistent && ProgressLine != null)
            ProgressLine.Visibility = Visibility.Collapsed;
        _timer = new DispatcherTimer
        {
            Interval = _autoDismissDuration
        };
        _timer.Tick += (s, e) => Dismiss();

        Loaded += OnLoaded;

        if (RootBorder != null)
        {
            // Hover behavior: pause countdown & highlight clickable notifications
            RootBorder.MouseEnter += (_, _) =>
            {
                _timer.Stop();
                PauseProgressLineAnimation();
                if (!_isClosing)
                {
                    if (_onClick != null)
                    {
                        CardGrid.OutlineBrush = _accentProgressBrush;
                    }
                }
            };
            RootBorder.MouseLeave += (_, _) =>
            {
                if (!_isClosing)
                {
                    if (!_isPersistent)
                    {
                        _timer.Start();
                        ResumeProgressLineAnimation();
                    }
                    if (_onClick != null)
                    {
                        CardGrid.OutlineBrush = _normalBorderBrush;
                    }
                }
            };
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = AnchorLeft();
        Restack(); // takes the top place; the older banners slide down under it
        SetupGlass();
        AnimateIn();
        if (!_isPersistent)
        {
            _timer.Start();
            StartProgressLineAnimation(_autoDismissDuration);
        }
    }

    private static int GetDurationSeconds(NotificationType type)
    {
        var s = SettingsService.Current;
        return type switch
        {
            NotificationType.Success => s.NotificationDurationComplete,
            NotificationType.Error or NotificationType.Warning => s.NotificationDurationError,
            _ => s.NotificationDurationInfo
        };
    }

    // RootGrid's top margin leaves room for the fly-in, so a window starts that much above its card.
    private double AnchorLeft() => _parentWindow is { IsVisible: true } ? _parentWindow.Left + 16 : SystemParameters.WorkArea.Left + 16;
    private double AnchorTop() => _parentWindow is { IsVisible: true } ? _parentWindow.Top + 12 : SystemParameters.WorkArea.Top + 12;

    /// <summary>Moves every banner to its place in the stack: each one sits under the newer ones above it.</summary>
    private static void Restack()
    {
        // A banner that hasn't been laid out yet has no height; it restacks everyone once it has loaded.
        if (Stack.Any(w => !w.IsLoaded)) return;
        double offset = 0;
        foreach (var win in Stack)
        {
            win.MoveTo(offset);
            offset += win.RootBorder.ActualHeight + StackGap;
        }
    }

    /// <summary>Slides the card to <paramref name="offset"/> below the top of the stack on a spring; a new banner
    /// just starts there.</summary>
    private void MoveTo(double offset)
    {
        bool animate = _placed && !SettingsService.Current.EnablePerformanceMode && !FluidMotion.AnimationsDisabled;
        _placed = true;
        _slideTarget = offset;
        // The window only moves if its parent has (then the card jumps along with it).
        double top = AnchorTop();
        if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.5)
        {
            Top = top;
            animate = false;
        }
        if (!animate)
        {
            _stackSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _stackSlide.Y = offset;
            SetSlideRoom(offset);
            return;
        }

        // Room for the whole way first, so the card is never cut off by the window's edge.
        SetSlideRoom(Math.Max(_stackSlide.Y, offset) + SlideOvershoot);
        // A layout pass, so the glass starts following its card right away (a transform alone doesn't tell it).
        CardGrid.InvalidateArrange();
        void Settled()
        {
            if (_slideTarget == offset) SetSlideRoom(offset);
        }
        // No From: a card still moving turns towards its new place from where it is.
        if (Liquid.IsActive)
            Liquid.To(_stackSlide, TranslateTransform.YProperty, offset, Spring.WithBounce(0.5, 0.18), done: Settled);
        else
        {
            var slide = new DoubleAnimation(offset, TimeSpan.FromMilliseconds(560)) { EasingFunction = AppleSpringEase.Interactive };
            slide.Completed += (_, _) => Settled();
            _stackSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    /// <summary>Makes the window reach far enough below its top for a card <paramref name="below"/> further down.</summary>
    private void SetSlideRoom(double below)
    {
        var m = RootGrid.Margin;
        double bottom = _bottomMargin + Math.Max(0, below);
        if (Math.Abs(m.Bottom - bottom) > 0.1) RootGrid.Margin = new Thickness(m.Left, m.Top, m.Right, bottom);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Closed some other way than Dismiss (e.g. with its parent window)
        if (Stack.Remove(this)) Restack();
    }

    private void SetupStyle(NotificationType type)
    {
        var app = Application.Current;
        Color accentColor;
        Wpf.Ui.Controls.SymbolRegular iconSymbol;

        switch (type)
        {
            case NotificationType.Success:
                // Green for working / success
                iconSymbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                accentColor = Color.FromRgb(0x10, 0xB9, 0x81); // Emerald Green
                if (app?.TryFindResource("SuccessGreen") is SolidColorBrush succBrush)
                    accentColor = succBrush.Color;
                break;

            case NotificationType.Error:
            case NotificationType.Warning:
                // Red for error / warning / failed
                iconSymbol = Wpf.Ui.Controls.SymbolRegular.DismissCircle24;
                accentColor = Color.FromRgb(0xEF, 0x44, 0x44); // Crimson Red
                if (app?.TryFindResource("ErrorRed") is SolidColorBrush errBrush)
                    accentColor = errBrush.Color;
                break;

            case NotificationType.Info:
            case NotificationType.Custom:
            default:
                // Blue for regular / info
                iconSymbol = Wpf.Ui.Controls.SymbolRegular.Info24;
                accentColor = Color.FromRgb(0x3B, 0x82, 0xF6); // Electric Blue
                if (app?.TryFindResource("Accent") is SolidColorBrush infoBrush)
                    accentColor = infoBrush.Color;
                break;
        }

        if (IconNotify != null)
        {
            IconNotify.Symbol = iconSymbol;
            IconNotify.Foreground = new SolidColorBrush(accentColor);
        }

        _accentProgressBrush = new SolidColorBrush(accentColor);
        _normalBorderBrush = app?.TryFindResource("Tint1F") as Brush ?? _normalBorderBrush;

        if (ProgressLine != null)
            ProgressLine.Fill = _accentProgressBrush;
    }

    private void StartProgressLineAnimation(TimeSpan duration)
    {
        if (ProgressScale == null) return;
        try
        {
            ProgressScale.ScaleX = 1.0;
            var anim = new DoubleAnimation(1.0, 0.0, duration)
            {
                EasingFunction = ExpoEaseInOut
            };
            anim.Completed += (s, e) => Dismiss();
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        }
        catch { }
    }

    private void PauseProgressLineAnimation()
    {
        if (ProgressScale == null) return;
        try
        {
            var currentScale = (double)ProgressScale.GetValue(ScaleTransform.ScaleXProperty);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = currentScale;
        }
        catch { }
    }

    private void ResumeProgressLineAnimation()
    {
        if (ProgressScale == null) return;
        try
        {
            var currentScale = ProgressScale.ScaleX;
            if (currentScale <= 0)
            {
                Dismiss();
                return;
            }

            var remainingRatio = currentScale;
            var remainingMs = _autoDismissDuration.TotalMilliseconds * remainingRatio;
            if (remainingMs <= 30)
            {
                Dismiss();
                return;
            }

            var anim = new DoubleAnimation(currentScale, 0.0, TimeSpan.FromMilliseconds(remainingMs))
            {
                EasingFunction = ExpoEaseInOut
            };
            anim.Completed += (s, e) => Dismiss();
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        }
        catch { }
    }

    // ═══ Glass ═══
    // The card is a LiquidGlassPanel (surface "Notification", tunable live in the Glass Tuner). This window is
    // transparent and sits over the parent window, so the panel can't find its backdrop by itself: it gets the
    // parent window's content.
    private void SetupGlass()
    {
        if (_parentWindow is { IsVisible: true } && _parentWindow.Content is Visual source)
            CardGrid.BackdropSource = source;
    }

    // ═══ Fly in / out ═══
    // Like iOS banners: the card drops in from above on a spring, growing from smaller and sharpening out of a
    // blur, and leaves the same way up.
    private void AnimateIn()
    {
        if (RootBorder == null || WindowScale == null || WindowTranslate == null) return;
        try
        {
            if (SettingsService.Current.EnablePerformanceMode)
            {
                RootBorder.Opacity = 1;
                return;
            }

            if (Liquid.IsActive)
            {
                // Like the Dynamic Island: a small pill drops in, spreads wide and then pours down to full height,
                // sharpening out of a blur.
                Liquid.Arrive(RootBorder, WindowScale, WindowTranslate, 0.42, 0.3, -34, Spring.Jelly);
                Liquid.To(ContentBlur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 0, new Spring(0.3, 1), from: 10);
                return;
            }

            var spring = new AppleSpringEase(0.62, 0.42);
            var dur = TimeSpan.FromMilliseconds(700);

            RootBorder.Opacity = 0;
            RootBorder.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            WindowTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-40, 0, dur) { EasingFunction = spring });
            WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.72, 1.0, dur) { EasingFunction = spring });
            WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.6, 1.0, dur) { EasingFunction = spring });
            ContentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        catch { }
    }

    public void Dismiss()
    {
        if (_isClosing) return;
        _isClosing = true;
        try { _timer.Stop(); } catch { }

        try
        {
            if (Liquid.IsActive && RootBorder != null && WindowScale != null)
            {
                // Back into a pill and up and away.
                Liquid.Leave(RootBorder, WindowScale, WindowTranslate, 0.55, 0.35, -30, done: () =>
                {
                    try { Close(); } catch { }
                });
                if (ContentBlur != null)
                    Liquid.To(ContentBlur, System.Windows.Media.Effects.BlurEffect.RadiusProperty, 8, Spring.Exit);
                if (Stack.Remove(this)) Restack();
                return;
            }

            var dur = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            var opAnim = new DoubleAnimation(RootBorder != null ? RootBorder.Opacity : 1.0, 0, dur) { EasingFunction = ease };
            opAnim.Completed += (s, e) =>
            {
                try { Close(); } catch { }
            };

            if (RootBorder != null)
                RootBorder.BeginAnimation(UIElement.OpacityProperty, opAnim);
            WindowTranslate?.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(WindowTranslate.Y, -28, dur) { EasingFunction = ease });
            WindowScale?.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(WindowScale.ScaleX, 0.86, dur) { EasingFunction = ease });
            WindowScale?.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(WindowScale.ScaleY, 0.8, dur) { EasingFunction = ease });
            ContentBlur?.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty,
                new DoubleAnimation(0, 8, dur) { EasingFunction = ease });
        }
        catch
        {
            try { Close(); } catch { }
        }

        // Leaves its place right away, so the banners below slide up while it fades.
        if (Stack.Remove(this)) Restack();
    }

    private void Notification_Click(object sender, MouseButtonEventArgs e)
    {
        if (_onClick == null || _isClosing) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var action = _onClick;
            Dismiss();
            try { action(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    public static void Show(
        string message,
        Window? owner = null,
        NotificationType type = NotificationType.Warning,
        Action? onClick = null,
        string? actionText = null,
        bool persistent = false)
    {
        var app = Application.Current;
        if (app == null)
        {
            FallbackToMessageBox(message, new InvalidOperationException("Application.Current is null or shut down."), type);
            return;
        }

        try
        {
            app.Dispatcher.Invoke(() =>
            {
                try
                {
                    // The same message again replaces the earlier banner instead of piling up.
                    foreach (var same in Stack.Where(w => w._message == (message ?? string.Empty)).ToList())
                        same.Dismiss();

                    var win = new NotificationWindow(message, type, owner, onClick, actionText, persistent);
                    Stack.Insert(0, win);
                    // Past the limit, the oldest banners make way.
                    while (Stack.Count > MaxStacked)
                        Stack[^1].Dismiss();
                    win.Show();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing custom notification window: {ex}");
                    FallbackToMessageBox(message, ex, type);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dispatcher error showing notification: {ex}");
            FallbackToMessageBox(message, ex, type);
        }
    }

    private static void FallbackToMessageBox(string originalNotificationMessage, Exception ex, NotificationType type)
    {
        try
        {
            var title = "Notification Window Exception Error";
            var details = $"Failed to display custom notification window!\n\n" +
                          $"[Exception Message]\n{ex.Message}\n\n" +
                          $"[Notification Target]\nType: {type}\nMessage: \"{originalNotificationMessage}\"\n\n" +
                          $"[Exception StackTrace]\n{ex}";

            MessageBox.Show(details, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
