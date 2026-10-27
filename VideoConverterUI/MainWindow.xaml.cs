using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoConverterUI.Animations;
using VideoConverterUI.Controls;
using VideoConverterUI.Models;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public partial class MainWindow : Window
{
    private IEngineService _backend => EngineProvider.Current;
    private static readonly System.Text.RegularExpressions.Regex FfmpegTimeRegex = new(@"time=(\d+):(\d+):(\d+)\.(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private MetadataResponse? _meta;
    private CancellationTokenSource? _cts;
    private string? _currentJobId;
    private ProgressWindow? _progressWin;
    private LogViewerWindow? _logViewer;
    private DevConsoleWindow? _devConsoleWin;
    private string _lastJobLog = string.Empty;
    private string? _lastJobHash;
    private string _lastLogEntry = string.Empty;
    private bool _backendReady;
    private string _currentLang = "de";
    private string? _lastDownloadedFolder;
    private string? _lastDownloadedFile;
    private string? _lastCloudUrl;
    private Color? _lastDominantColor;
    internal Color? LastDominantColor => _lastDominantColor;
    private bool _isLoadingFromDrag;

    // ── Segment pill drag state (iOS 26 interactive tracking) ──
    private bool _pillDragPending;
    private bool _pillDragging;
    private bool _pillSnapFromDrag;
    private double _pillDragAnchor;
    private double _pillLastMoveX;
    private DateTime _pillLastMoveTime;
    private double _pillVelocity; // px/s, smoothed: a flick carries on with it (Liquid)

    private bool _isAnimatingClose;
    private Helpers.EasterEggs.EasterEggOrchestrator? _easterEggOrchestrator;

    public MainWindow()
    {
        InitializeComponent();
        SetupThumbReflection();
        Loaded += OnLoaded;
        Closing += MainWindow_Closing;
        ThemeService.ThemeApplied += OnThemeApplied;
        Deactivated += (_, _) => CloseAllPopupsAnimated();
    }

    private void WindowCornerGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        SettingsService.StartWindowResizeBottomRight(this);
        e.Handled = true; // no drag-move of the window underneath
    }

    private void WindowCornerGrip_MouseEnter(object sender, MouseEventArgs e)
        => SettingsService.HandleGripHover(sender, true);

    private void WindowCornerGrip_MouseLeave(object sender, MouseEventArgs e)
        => SettingsService.HandleGripHover(sender, false);

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ThemeService.ThemeApplied -= OnThemeApplied;
        SettingsService.SettingsChanged -= OnSettingsChanged;
        // The spare Settings window was never shown but counts in Application.Windows: left open, the app would keep
        // running after the main window closes.
        _settingsSpare?.Close();
        _settingsSpare = null;

        if (SettingsService.Current.EnablePerformanceMode)
        {
            _cts?.Cancel();
            _devConsoleWin?.Close();
            EngineProvider.Shutdown();
            return;
        }

        if (!_isAnimatingClose)
        {
            e.Cancel = true;
            CloseWithAnimation();
        }
    }

    private void OnThemeApplied()
    {
        if (AllowsTransparency && Background != Brushes.Transparent)
        {
            Background = Brushes.Transparent;
        }
        RefreshDominantColorVisuals();
        if (ThemeService.Current.DisableThumbnailCard)
            BorderMeta.Visibility = Visibility.Collapsed;
        else if (_meta != null)
            BorderMeta.Visibility = Visibility.Visible;

        if (_isStartContainerInProgress && StartProgressFill != null)
        {
            if (ThemeService.Current.EnableProgressPulse)
            {
                Color glowColor = Color.FromArgb(255, 255, 255, 255);
                if (TryFindResource("Accent") is SolidColorBrush ab) glowColor = ab.Color;
                FluidMotion.StartProgressGlowPulse(StartProgressFill, glowColor, minBlur: 6, maxBlur: 18, minOpacity: 0.25, maxOpacity: 0.70);
            }
            else
            {
                FluidMotion.StopProgressGlowPulse(StartProgressFill, idleBlur: 10, idleOpacity: 0.35);
            }
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        SettingsService.ApplyResizeGripVisibility(this);
        Dispatcher.BeginInvoke(UpdatePerformanceOverlayVisibility);
        Dispatcher.BeginInvoke(UpdateIconLabelsVisibility);
    }

    private void CloseWithAnimation()
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;

        if (SettingsService.Current.EnablePerformanceMode)
        {
            _cts?.Cancel();
            _devConsoleWin?.Close();
            EngineProvider.Shutdown();
            Close();
            return;
        }

        if (Liquid.IsActive)
        {
            Liquid.Leave(RootBorder, WindowScale, null, 0.9, 0.86, 0, done: () =>
            {
                _cts?.Cancel();
                _devConsoleWin?.Close();
                EngineProvider.Shutdown();
                Close();
            });
            return;
        }

        // Play closing scale & opacity fade out animation
        var ease = AppleSpringEase.Snappy;
        var dur = TimeSpan.FromMilliseconds(220);

        var opAnim = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        opAnim.Completed += (s, e) =>
        {
            // 3. Cleanup resources & exit app
            _cts?.Cancel();
            _devConsoleWin?.Close();
            EngineProvider.Shutdown();
            Close();
        };

        RootBorder.BeginAnimation(UIElement.OpacityProperty, opAnim);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.88, dur) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.85, dur) { EasingFunction = ease });
    }

    // ════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ════════════════════════════════════════════════════════════
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        OpenOnceRendering();

        _easterEggOrchestrator = new Helpers.EasterEggs.EasterEggOrchestrator(
            ChristmasTreeContainer,
            DiscoBallContainer,
            DiscoBallLowerTransform,
            SnowCanvas,
            MainContentGrid,
            SpiderOverlayCanvas
        );

        SettingsService.SettingsChanged += OnSettingsChanged;
        SettingsService.ApplyResizeGripVisibility(this);
        UpdatePerformanceOverlayVisibility();
        UpdateIconLabelsVisibility();
        ApplySettings();
        FluidMotion.SetCornerRadiusValue(SegmentIndicator, 50);
        Mode_Changed(sender, e);

        if (SettingsService.Current.EnablePerformanceMode)
        {
            if (RootBorder != null) RootBorder.CornerRadius = 0;
        }

        PrepareSettingsWindow(TimeSpan.FromSeconds(3));

        Activated += (_, _) =>
        {
            if (SettingsService.Current.AutoPasteOnFocus
                && Clipboard.ContainsText()
                && string.IsNullOrWhiteSpace(TxtUrl.Text))
            {
                TxtUrl.Text = Clipboard.GetText().Trim();
            }
        };

        EngineProvider.OutputReceived += DevLog;
        EngineProvider.EngineChanged += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _backendReady = true;
                SetStatus(ReadyStatusText(), true);
                DevLog($"Switched execution engine to: {EngineProvider.Current.EngineName}");
            });
        };

        try
        {
            DevLog($"Initializing media engine ({SettingsService.Current.ExecutionEngine})...");
            _backendReady = await EngineProvider.InitializeAsync(CancellationToken.None);
            DevLog(_backendReady ? $"Engine initialization: OK ({EngineProvider.Current.EngineName})" : "Engine initialization: FAILED");
            SetStatus(_backendReady 
                ? ReadyStatusText() 
                : L("StatusBackendDown"), _backendReady);
        }
        catch (Exception ex)
        {
            ReportError($"{L("StatusBackendErrPrefix")} {ex.Message}");
        }


    }

    /// <summary>
    /// The window's first frames are slow (60–120 ms each while every glass surface sets up its shader and backdrop),
    /// so an opening started at Loaded showed its first pose frozen and then jumped ahead. The window waits in that pose,
    /// all but invisible, until frames come at a steady pace (at most <paramref name="maxWaitMs"/>), and only then opens:
    /// under Liquid it spreads open and settles with a soft wobble, otherwise it plays the FadeInWindow storyboard.
    /// Not quite invisible: at opacity 0 WPF skips drawing it, and the slow frames would come with the first visible one.
    /// </summary>
    private void OpenOnceRendering(double maxWaitMs = 400)
    {
        RootBorder.Opacity = 0.004;
        WindowScale.ScaleX = 0.9;
        WindowScale.ScaleY = 0.86;

        TimeSpan first = TimeSpan.Zero, last = TimeSpan.Zero;
        int steadyFrames = 0;
        EventHandler? tick = null;
        tick = (_, e) =>
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return;  // Rendering can fire more than once per frame
            if (first == TimeSpan.Zero) first = now;
            steadyFrames = last != TimeSpan.Zero && (now - last).TotalMilliseconds < 25 ? steadyFrames + 1 : 0;
            last = now;
            if (steadyFrames < 2 && (now - first).TotalMilliseconds < maxWaitMs) return;

            CompositionTarget.Rendering -= tick;
            if (Liquid.IsActive)
                Liquid.Arrive(RootBorder, WindowScale, null, 0.9, 0.86, 0, Spring.Bouncy.Slower(1.1));
            else
                ((Storyboard)FindResource("FadeInWindow")).Begin(this);
        };
        CompositionTarget.Rendering += tick;
    }

    // ════════════════════════════════════════════════════════════
    //  TITLEBAR & EASTER EGG
    // ════════════════════════════════════════════════════════════
    private int _titleEggClickCount = 0;
    private bool _isComicSansActive = false;

    private void TitleStack_Click(object sender, MouseButtonEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
            return;
        }

        if (_isComicSansActive)
        {
            ToggleComicSansMode(false);
            _titleEggClickCount = 0;
            NotificationWindow.Show("Back to normal!", this, NotificationType.Info);
        }
        else
        {
            _titleEggClickCount++;
            switch (_titleEggClickCount)
            {
                case 1:
                    NotificationWindow.Show("Hey, that's just the title bar.", this, NotificationType.Info);
                    break;
                case 2:
                    NotificationWindow.Show("Seriously? Still clicking?", this, NotificationType.Info);
                    break;
                case 3:
                    NotificationWindow.Show("What are you expecting to happen?", this, NotificationType.Info);
                    break;
                case 4:
                    NotificationWindow.Show("Nothing is going to happen.", this, NotificationType.Info);
                    break;
                case 5:
                    NotificationWindow.Show("Literally nothing.", this, NotificationType.Info);
                    break;
                case 6:
                    NotificationWindow.Show("Still nothing...", this, NotificationType.Info);
                    break;
                case 7:
                    NotificationWindow.Show("You must really be bored.", this, NotificationType.Info);
                    break;
                case 8:
                    NotificationWindow.Show("Okay, fine. Keep going if you want.", this, NotificationType.Info);
                    break;
                case 9:
                    NotificationWindow.Show("Are you not tired yet?", this, NotificationType.Info);
                    break;
                case 10:
                    NotificationWindow.Show("This is getting impressive.", this, NotificationType.Info);
                    break;
                case 11:
                    NotificationWindow.Show("Persistence level 100.", this, NotificationType.Info);
                    break;
                case 12:
                    NotificationWindow.Show("Almost there...", this, NotificationType.Info);
                    break;
                case 13:
                    NotificationWindow.Show("Or are you?", this, NotificationType.Info);
                    break;
                case 14:
                    NotificationWindow.Show("Just one more...", this, NotificationType.Info);
                    break;
                case 15:
                default:
                    ToggleComicSansMode(true);
                    NotificationWindow.Show("Was it worth it?", this, NotificationType.Info);
                    break;
            }
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleComicSansMode(bool enable)
    {
        _isComicSansActive = enable;
        _easterEggOrchestrator?.Toggle(enable);
    }

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        // The pop-ups declared in the title bar (help, errors, Tools menu) route their clicks up to here; they must not move the window.
        if (SettingsService.IsFromPopup(this, e.OriginalSource)) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();
    private void HelpPopup_Closed(object? sender, EventArgs e)
    {
        RefreshHeaderPills();
    }

    private bool _isClosingHelpPopup;
    private DateTime _helpPopupClosedTime = DateTime.MinValue;

    private void Help_Click(object s, RoutedEventArgs e)
    {
        if (HelpPopup == null) return;
        if ((DateTime.UtcNow - _helpPopupClosedTime).TotalMilliseconds < 250) return;

        HelpPopup.PlacementTarget = (UIElement)s;
        if (!HelpPopup.IsOpen && !_isClosingHelpPopup)
        {
            HelpPopup.IsOpen = true;
            AnimatePopupIn(HelpPopupBorder);
            RefreshHeaderPills();
        }
        else
        {
            CloseHelpPopupAnimated();
        }
    }

    private void CloseHelpPopupAnimated()
    {
        if (_isClosingHelpPopup || HelpPopup == null || !HelpPopup.IsOpen) return;
        _isClosingHelpPopup = true;
        _helpPopupClosedTime = DateTime.UtcNow;
        AnimatePopupOut(HelpPopupBorder, () =>
        {
            HelpPopup.IsOpen = false;
            _isClosingHelpPopup = false;
            RefreshHeaderPills();
        });
    }

    // ════════════════════════════════════════════════════════════
    //  MODE TOGGLE
    // ════════════════════════════════════════════════════════════
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        bool toDownload = RbDownload?.IsChecked == true;
        var targetPanel = toDownload ? BorderDownload : BorderUpload;
        var otherPanel = toDownload ? BorderUpload : BorderDownload;

        if (targetPanel.Visibility == Visibility.Visible && targetPanel.Opacity >= 0.95 && otherPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        if (SettingsService.Current.EnablePerformanceMode)
        {
            if (SegmentIndicator != null && RbUpload != null && RbUpload.ActualWidth > 0)
            {
                double targetX = toDownload ? RbUpload.ActualWidth : 0;
                SegmentIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                SegmentIndicator.Opacity = 1.0;
                SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                SegmentScale.ScaleX = 1.0;
                SegmentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                SegmentScale.ScaleY = 1.0;
                SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty, null);
                SegmentTranslateX.X = targetX;
                FluidMotion.SetCornerRadiusValue(SegmentIndicator, 0);
            }
            _pillSnapFromDrag = false;

            var showP = toDownload ? BorderDownload : BorderUpload;
            var hideP = toDownload ? BorderUpload : BorderDownload;

            if (hideP != null)
            {
                hideP.Opacity = 0;
                hideP.Visibility = Visibility.Collapsed;
            }
            if (showP != null)
            {
                showP.Visibility = Visibility.Visible;
                showP.Opacity = 1;
                showP.RenderTransform = Transform.Identity;
            }

            if (toDownload && !string.IsNullOrEmpty(_lastDownloadedFolder))
                ShowOpenFolderBar();
            else
                HideOpenFolderBar();

            return;
        }

        // ── Pill indicator: animated only for taps (drag handles its own pill) ──
        if (_isUploadDisabledForYtMusic)
        {
            _pillSnapFromDrag = false;
            var showP = toDownload ? BorderDownload : BorderUpload;
            var hideP = toDownload ? BorderUpload : BorderDownload;
            double slideD = toDownload ? 1.0 : -1.0;
            FluidMotion.LiquidGlassCrossfade(hideP, showP, slideD);
            if (toDownload && !string.IsNullOrEmpty(_lastDownloadedFolder))
                ShowOpenFolderBar();
            else
                HideOpenFolderBar();
            return;
        }

        if (!_pillSnapFromDrag && SegmentIndicator != null && RbUpload.ActualWidth > 0 && Liquid.IsActive)
        {
            // Glass pill: stretches and clears while it travels, settles solid; a second tap mid-way swings it round.
            Liquid.Glide(SegmentIndicator, SegmentTranslateX, SegmentScale, toDownload ? RbUpload.ActualWidth : 0);
        }
        else if (!_pillSnapFromDrag && SegmentIndicator != null && RbUpload.ActualWidth > 0)
        {
            double targetX = toDownload ? RbUpload.ActualWidth : 0;
            var snappy = AppleSpringEase.Snappy;
            var interactive = AppleSpringEase.Interactive;
            var totalDur = TimeSpan.FromMilliseconds(320);

            // Slightly larger in size while moving, then spring back
            var scaleXAnim = new DoubleAnimationUsingKeyFrames { Duration = totalDur };
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(0)));
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromPercent(0.35)) { EasingFunction = snappy });
            scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(1.0))  { EasingFunction = snappy });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);

            var scaleYAnim = new DoubleAnimationUsingKeyFrames { Duration = totalDur };
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(0)));
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromPercent(0.35)) { EasingFunction = snappy });
            scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(1.0))  { EasingFunction = snappy });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

            // Subtle light translucent glass color (0.38 opacity) while moving, fading in and out quickly
            var opacityAnim = new DoubleAnimationUsingKeyFrames { Duration = totalDur };
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(0)));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.38, KeyTime.FromPercent(0.20)) { EasingFunction = interactive });
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.38, KeyTime.FromPercent(0.65)));
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(1.0))  { EasingFunction = snappy });
            SegmentIndicator.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            // Slide: Apple .snappy spring glide to target
            SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(targetX, totalDur) { EasingFunction = snappy });
        }
        _pillSnapFromDrag = false;

        // ── Content panels: liquid glass crossfade (always runs) ──
        var showPanel = toDownload ? BorderDownload : BorderUpload;
        var hidePanel = toDownload ? BorderUpload : BorderDownload;
        double slideDir = toDownload ? 1.0 : -1.0;

        FluidMotion.LiquidGlassCrossfade(hidePanel, showPanel, slideDir);

        // Show/hide open-folder button with mode switch
        if (toDownload && !string.IsNullOrEmpty(_lastDownloadedFolder))
            ShowOpenFolderBar();
        else
            HideOpenFolderBar();
    }

    // ════════════════════════════════════════════════════════════
    //  SEGMENT PILL DRAG  (Apple iOS clear glass interactive)
    // ════════════════════════════════════════════════════════════
    private void Segment_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isUploadDisabledForYtMusic || SegmentIndicator == null || RbUpload.ActualWidth <= 0) return;
        var grid = (UIElement)SegmentIndicator.Parent;
        double mouseX = e.GetPosition(grid).X;

        _pillDragPending = true;
        _pillDragging = false;
        _pillLastMoveX = mouseX;
        _pillLastMoveTime = DateTime.UtcNow;
        _pillVelocity = 0;
        _pillDragAnchor =(SegmentIndicator.ActualWidth * SegmentScale.ScaleX) / 2.0;
    }

    private void Segment_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isUploadDisabledForYtMusic || !_pillDragPending) return;
        var grid = SegmentIndicator?.Parent as UIElement;
        if (grid == null || SegmentIndicator == null || RbUpload.ActualWidth <= 0) return;
        var pos = e.GetPosition(grid);
        double mouseX = pos.X;

        if (!_pillDragging)
        {
            if (Math.Abs(mouseX - _pillLastMoveX) < 3) return;
            _pillDragging = true;

            // Capture mouse now that we know it's a drag
            ((UIElement)sender).CaptureMouse();

            // Cancel running animations and enter responsive state
            SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty, null);
            SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SegmentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SegmentIndicator.BeginAnimation(UIElement.OpacityProperty, null);

            // Quick fade to 0.38 (light translucent glass color) and expand size
            SegmentIndicator.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.38, TimeSpan.FromMilliseconds(80)) { EasingFunction = AppleSpringEase.Interactive });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(100)) { EasingFunction = AppleSpringEase.Interactive });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(100)) { EasingFunction = AppleSpringEase.Interactive });
        }

        // Velocity for subtle response
        var now = DateTime.UtcNow;
        double dt = Math.Max((now - _pillLastMoveTime).TotalSeconds, 0.001);
        double velocity = (mouseX - _pillLastMoveX) / dt;
        _pillVelocity = _pillVelocity * 0.35 + velocity * 0.65;
        _pillLastMoveX = mouseX;
        _pillLastMoveTime = now;

        double vFactor = Math.Clamp(Math.Abs(velocity) / 1000.0, 0, 0.04);
        double targetScaleX = 1.05 + vFactor;
        double targetScaleY = 1.04;

        // Track pill position with strict containment boundaries
        double colW = RbUpload.ActualWidth;
        double trackW = colW * 2.0;
        double raw = mouseX - _pillDragAnchor;

        // Guarantee the pill NEVER extends past [0, trackW]
        double currentPillW = colW * targetScaleX;
        double maxSafeX = Math.Max(0, trackW - currentPillW);
        double minSafeX = 0;

        double clamped;
        if (raw < minSafeX)
        {
            clamped = minSafeX;
            SegmentScale.ScaleX = Math.Max(0.97, targetScaleX - Math.Abs(raw) / colW * 0.10);
        }
        else if (raw > maxSafeX)
        {
            clamped = maxSafeX;
            SegmentScale.ScaleX = Math.Max(0.97, targetScaleX - (raw - maxSafeX) / colW * 0.10);
        }
        else
        {
            clamped = raw;
            SegmentScale.ScaleX = targetScaleX;
        }

        SegmentScale.ScaleY = targetScaleY;
        SegmentTranslateX.X = clamped;
    }

    private void Segment_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isUploadDisabledForYtMusic)
        {
            _pillDragPending = false;
            _pillDragging = false;
            return;
        }

        bool wasDragging = _pillDragging;
        _pillDragPending = false;
        _pillDragging = false;

        if (!wasDragging) return; // was a tap — let RadioButton handle it

        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;

        // Spring-snap to nearest segment
        double currentX = SegmentTranslateX.X;
        double colW = RbUpload.ActualWidth;
        bool snapToDownload = currentX > colW * 0.45;
        double targetX = snapToDownload ? colW : 0;

        if (Liquid.IsActive)
        {
            // A flick: aim where the pill would coast to, and let it carry on with the pointer's speed.
            // A pointer that had stopped before letting go doesn't flick.
            double v = (DateTime.UtcNow - _pillLastMoveTime).TotalMilliseconds > 80 ? 0 : _pillVelocity;
            snapToDownload = currentX + v * 0.15 > colW * 0.5;
            targetX = snapToDownload ? colW : 0;
            Liquid.Glide(SegmentIndicator, SegmentTranslateX, SegmentScale, targetX, velocity: v, min: 0, max: colW, lifted: true);
        }
        else
        {
            var snappy = AppleSpringEase.Snappy;

            SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(currentX, targetX, TimeSpan.FromMilliseconds(320))
                { EasingFunction = snappy });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(SegmentScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(280))
                { EasingFunction = snappy });
            SegmentScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(SegmentScale.ScaleY, 1.0, TimeSpan.FromMilliseconds(280))
                { EasingFunction = snappy });
            SegmentIndicator.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(SegmentIndicator.Opacity, 1.0, TimeSpan.FromMilliseconds(220))
                { EasingFunction = snappy });
        }

        // Switch content if segment changed
        bool wasDownload = RbDownload.IsChecked == true;
        if (snapToDownload != wasDownload)
        {
            _pillSnapFromDrag = true;
            if (snapToDownload)
                RbDownload.IsChecked = true;
            else
                RbUpload.IsChecked = true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  PASTE & BROWSE
    // ════════════════════════════════════════════════════════════
    private string? _lastPromptedPlaylistUrl;

    private void CheckAndPromptPlaylist(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !SettingsService.Current.PlaylistDetectedNotification) return;
        var trimmed = url.Trim();
        var lower = trimmed.ToLowerInvariant();
        bool isPlaylist = lower.Contains("list=") || lower.Contains("/playlist?") || trimmed.Contains('\n') || trimmed.Contains('\r');
        if (!isPlaylist) return;

        if (_lastPromptedPlaylistUrl == lower) return;
        _lastPromptedPlaylistUrl = lower;

        NotificationWindow.Show(
            L("PlaylistDetectedPrompt"),
            this,
            NotificationType.Info,
            onClick: () =>
            {
                Rect origin = GetBtnRect(TxtUrl);
                OpenBatchQueue(trimmed, origin);
            },
            actionText: L("BtnOpen")
        );
    }

    private void Paste_Click(object s, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText().Trim();
            TxtUrl.Text = text;
            CheckAndPromptPlaylist(text);
        }
    }

    private List<ComboBoxItem>? _videoQualityItems;
    private List<ComboBoxItem>? _ytmusicQualityItems;
    private bool? _isYTMusicActiveState = null;

    private void EnsureQualityItemLists()
    {
        if (_videoQualityItems == null && CmbItemBest != null && CmbItem1080 != null && CmbItem720 != null && CmbItem480 != null && CmbItem360 != null && CmbItemAudio != null)
        {
            _videoQualityItems = new List<ComboBoxItem>
            {
                CmbItemBest, CmbItem1080, CmbItem720, CmbItem480, CmbItem360, CmbItemAudio
            };
        }
        if (_ytmusicQualityItems == null && CmbItemYt320 != null && CmbItemYtM4a != null && CmbItemYtFlac != null)
        {
            _ytmusicQualityItems = new List<ComboBoxItem>
            {
                CmbItemYt320, CmbItemYtM4a, CmbItemYtFlac
            };
        }
    }

    private void UpdateQualityOptionsVisibility(bool isYTMusic)
    {
        if (CmbQuality == null) return;
        EnsureQualityItemLists();

        if (_isYTMusicActiveState == isYTMusic) return;
        _isYTMusicActiveState = isYTMusic;

        CmbQuality.Items.Clear();

        if (isYTMusic && _ytmusicQualityItems != null)
        {
            foreach (var item in _ytmusicQualityItems)
            {
                item.Visibility = Visibility.Visible;
                CmbQuality.Items.Add(item);
            }
            CmbQuality.SelectedItem = CmbItemYt320;
        }
        else if (_videoQualityItems != null)
        {
            foreach (var item in _videoQualityItems)
            {
                item.Visibility = Visibility.Visible;
                CmbQuality.Items.Add(item);
            }
            CmbQuality.SelectedItem = CmbItemBest;
        }
    }

    private bool _isUploadDisabledForYtMusic;

    private void SegmentTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isUploadDisabledForYtMusic && SegmentTrack != null && SegmentTranslateX != null && SegmentScale != null && DownloadTranslateX != null)
        {
            double colW = (SegmentTrack.ActualWidth - 6.0) / 2.0;
            if (colW > 0)
            {
                SegmentTranslateX.X = colW / 2.0;
                SegmentScale.ScaleX = 2.0;
                DownloadTranslateX.X = -colW / 2.0;
            }
        }
    }

    private void SetYouTubeUploadEnabled(bool enabled)
    {
        if (_isUploadDisabledForYtMusic == !enabled) return;
        _isUploadDisabledForYtMusic = !enabled;

        var spring = AppleSpringEase.Interactive;
        var bouncy = AppleSpringEase.Bouncy;
        var gentle = AppleSpringEase.Gentle;

        double colW = (SegmentTrack?.ActualWidth > 0 ? (SegmentTrack.ActualWidth - 6.0) / 2.0 : (RbUpload?.ActualWidth > 0 ? RbUpload.ActualWidth : 150.0));

        if (!enabled)
        {
            // Switch to Download mode
            if (RbUpload != null && RbUpload.IsChecked == true)
            {
                if (RbDownload != null) RbDownload.IsChecked = true;
            }

            if (RbUpload != null) RbUpload.IsEnabled = false;

            // 1. RbUpload scale down & fade out
            var opacityAnim = new DoubleAnimation(RbUpload?.Opacity ?? 1, 0.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = gentle };
            opacityAnim.Completed += (_, _) =>
            {
                if (_isUploadDisabledForYtMusic && RbUpload != null)
                {
                    RbUpload.Visibility = Visibility.Collapsed;
                }
            };
            RbUpload?.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            if (UploadBtnScale != null)
            {
                UploadBtnScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(UploadBtnScale.ScaleX, 0.0, TimeSpan.FromMilliseconds(350))
                    { EasingFunction = bouncy });
                UploadBtnScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(UploadBtnScale.ScaleY, 0.0, TimeSpan.FromMilliseconds(350))
                    { EasingFunction = bouncy });
            }

            // 2. SegmentIndicator spring-morph to 100% full track width smoothly centered (X = colW / 2, ScaleX = 2.0)
            if (SegmentTranslateX != null)
            {
                SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(SegmentTranslateX.X, colW / 2.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = spring });
            }

            if (SegmentScale != null)
            {
                SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(SegmentScale.ScaleX, 2.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = bouncy });
            }

            // 3. RbDownload text glides smoothly to exact track center (-colW / 2)
            if (DownloadTranslateX != null)
            {
                DownloadTranslateX.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(DownloadTranslateX.X, -colW / 2.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = spring });
            }
        }
        else
        {
            if (RbUpload != null)
            {
                RbUpload.Visibility = Visibility.Visible;
                RbUpload.IsEnabled = true;

                RbUpload.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(RbUpload.Opacity, 1.0, TimeSpan.FromMilliseconds(350))
                    { EasingFunction = gentle });
            }

            if (UploadBtnScale != null)
            {
                UploadBtnScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(UploadBtnScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = bouncy });
                UploadBtnScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(UploadBtnScale.ScaleY, 1.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = bouncy });
            }

            // Restore RbDownload position smoothly back to Column 1 (0)
            if (DownloadTranslateX != null)
            {
                DownloadTranslateX.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(DownloadTranslateX.X, 0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = spring });
            }

            // Restore SegmentIndicator scale to 1.0 (50% width) and translate to active segment
            if (SegmentScale != null && SegmentTranslateX != null)
            {
                bool isDownload = RbDownload?.IsChecked == true;
                double targetX = isDownload ? colW : 0;

                SegmentScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(SegmentScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = bouncy });

                SegmentTranslateX.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(SegmentTranslateX.X, targetX, TimeSpan.FromMilliseconds(450))
                    { EasingFunction = spring });
            }
        }
    }

    private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        var rawUrl = TxtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _lastPromptedPlaylistUrl = null;
        }
        else
        {
            CheckAndPromptPlaylist(rawUrl);
        }

        var url = rawUrl.ToLowerInvariant();
        string? platform = null;
        bool isYTMusic = url.Contains("music.youtube.com");

        UpdateQualityOptionsVisibility(isYTMusic);
        SetYouTubeUploadEnabled(!isYTMusic);

        if (isYTMusic && RbDownload != null && RbDownload.IsChecked != true)
        {
            RbDownload.IsChecked = true;
        }

        if (isYTMusic)
            platform = "YouTube Music";
        else if (url.Contains("instagram.com") || url.Contains("instagr.am"))
            platform = "Instagram";
        else if (url.Contains("tiktok.com") || url.Contains("vm.tiktok"))
            platform = "TikTok";
        else if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            platform = "YouTube";
        else if (url.Contains("twitter.com") || url.Contains("x.com"))
            platform = "X / Twitter";
        else if (url.Contains("facebook.com") || url.Contains("fb.watch"))
            platform = "Facebook";
        else if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
            platform = "Other";

        if (platform != null && platform != _lastDetectedPlatform)
            DevLog($"Platform detected: {platform}");
        _lastDetectedPlatform = platform;
    }

    private string? _lastDetectedPlatform;

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Speicherort wählen" };
        if (dlg.ShowDialog() == true)
            TxtOutputDir.Text = dlg.FolderName;
    }

    // ════════════════════════════════════════════════════════════
    //  CLOUD UPLOAD UI HANDLERS
    // ════════════════════════════════════════════════════════════
    private void ChkCloudUpload_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ChkCloudUpload.IsChecked == true;
        SettingsService.Current.CloudUploadEnabled = enabled;
        SettingsService.Save(SettingsService.Current);
        UpdateCloudUiState();
    }

    private void CmbCloudProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbCloudProvider?.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            var provider = item.Tag.ToString() ?? "gdrive";
            SettingsService.Current.CloudProvider = provider;
            SettingsService.Save(SettingsService.Current);
            UpdateCloudUiState();
        }
    }



    private void BtnCloudConfig_Click(object sender, RoutedEventArgs e)
    {
        var provider = (CmbCloudProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gdrive";
        var dlg = new CloudSetupDialog(provider) { Owner = this };
        dlg.Closed += (_, _) => UpdateCloudUiState();
        dlg.ShowDialog();
    }

    private void BtnChangeCloudFolder_Click(object sender, RoutedEventArgs e)
    {
        var provider = (CmbCloudProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gdrive";
        var dlg = new OpenFolderDialog { Title = $"{CloudStorageService.GetProviderDisplayName(provider)} - Select Folder" };
        if (dlg.ShowDialog() == true)
        {
            var path = dlg.FolderName;
            var s = SettingsService.Current;
            switch (provider)
            {
                case "gdrive": s.GDriveFolderPath = path; break;
                case "dropbox": s.DropboxFolderPath = path; break;
                case "onedrive": s.OneDriveFolderPath = path; break;
                case "custom": s.CustomCloudFolderPath = path; break;
            }
            SettingsService.Save(s);
            UpdateCloudUiState();
        }
    }

    private void UpdateCloudUiState()
    {
        if (ExpandLocalSave == null || ExpandCloudSave == null) return;

        var isCloud = ChkCloudUpload.IsChecked == true;
        // Slide between the local path row and the cloud options; the action buttons below
        // resize with the card frame by frame.
        ExpandLocalSave.SetExpanded(!isCloud, animate: IsLoaded);
        ExpandCloudSave.SetExpanded(isCloud, animate: IsLoaded);

        var provider = (CmbCloudProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? SettingsService.Current.CloudProvider;
        if (isCloud && TxtCloudTargetStatus != null)
        {
            var (_, summary, _) = CloudStorageService.GetProviderStatus(provider, SettingsService.Current);
            TxtCloudTargetStatus.Text = summary;
        }

        if (BtnChangeCloudFolder != null)
            BtnChangeCloudFolder.Visibility = provider == "webhook" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCloudLink_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCloudUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _lastCloudUrl, UseShellExecute = true });
            }
            catch
            {
                try { Clipboard.SetText(_lastCloudUrl); } catch { }
            }
        }
    }

    private void EditDescription_Click(object s, RoutedEventArgs e)
    {
        foreach (Window w in OwnedWindows)
        {
            if (w is DescriptionEditorWindow existing)
            {
                existing.Focus();
                return;
            }
        }

        var editor = new DescriptionEditorWindow(TxtDescription.Text, GetBtnRect((UIElement)s)) { Owner = this };
        editor.Closed += (sender, args) =>
        {
            if (editor.IsSaved)
                TxtDescription.Text = editor.Description;
        };
        editor.Show();
    }

    // ════════════════════════════════════════════════════════════
    //  FETCH METADATA
    // ════════════════════════════════════════════════════════════
    private async void Fetch_Click(object s, RoutedEventArgs e)
    {
        await FetchMetadataAsync(TxtUrl.Text.Trim());
    }

    private async System.Threading.Tasks.Task FetchMetadataAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) { Warn(L("ErrNoUrl")); return; }
        if (!_backendReady) { Warn(L("ErrNoBackend")); return; }

        BtnFetch.IsEnabled = false;
        SetStatus(L("StatusLoading"), true);

        // Show wireframe skeleton preview card while fetching
        if (BorderMeta != null && BorderMeta.Visibility == Visibility.Visible)
            AnimatePanel(BorderMeta, false);
        if (BorderMetaSkeleton != null)
            AnimatePanel(BorderMetaSkeleton, true);

        try
        {
            DevLog($"Fetching metadata for: {url}");
            if (url.ToLowerInvariant().Contains("music.youtube.com"))
                _meta = await _backend.FetchYTMusicMetadataAsync(url);
            else
                _meta = await _backend.FetchMetadataAsync(url);

            if (_meta is null)
            {
                if (BorderMetaSkeleton != null) AnimatePanel(BorderMetaSkeleton, false);
                Warn(L("ErrNoMeta"));
                SetStatus(L("ErrNoMeta"), false);
                return;
            }

            TxtMetaTitle.Text = _meta.Title;
            var uploaderName = !string.IsNullOrEmpty(_meta.Artist) ? _meta.Artist : _meta.Uploader;
            TxtMetaUploader.Text = $"👤 {uploaderName}";

            if (_meta.Duration > 0)
            {
                var ts = TimeSpan.FromSeconds(_meta.Duration);
                TxtMetaDuration.Text = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
                BorderDurationBadge.Visibility = Visibility.Visible;
            }
            else
            {
                BorderDurationBadge.Visibility = Visibility.Collapsed;
            }

            if (_meta.Tags != null && _meta.Tags.Count > 0)
            {
                TagsList.ItemsSource = _meta.Tags.Take(8).Select(t => t.StartsWith("#") ? t : $"#{t}").ToList();
                TagsList.Visibility = Visibility.Visible;
            }
            else
            {
                TagsList.ItemsSource = null;
                TagsList.Visibility = Visibility.Collapsed;
            }

            LoadThumbnail(_meta.Thumbnail);

            bool isYTMusic = _meta.IsMusic || url.ToLowerInvariant().Contains("music.youtube.com");
            UpdateQualityOptionsVisibility(isYTMusic);
            SetYouTubeUploadEnabled(!isYTMusic);

            if (isYTMusic)
            {
                if (RbDownload != null && RbDownload.IsChecked != true)
                    RbDownload.IsChecked = true;

                if (!string.IsNullOrEmpty(_meta.Album))
                {
                    TxtMetaAlbum.Text = $"💿 {_meta.Album}";
                    TxtMetaAlbum.Visibility = Visibility.Visible;
                }
                else
                {
                    TxtMetaAlbum.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                TxtMetaAlbum.Visibility = Visibility.Collapsed;
            }

            TxtTitle.Text = _meta.Title;
            TxtTitleDl.Text = _meta.Title;
            TxtDescription.Text = _meta.Description;
            DevLog($"Metadata loaded: \"{_meta.Title}\" by {uploaderName} ({_meta.Duration:F0}s, {_meta.Tags?.Count ?? 0} tags)");

            // Animated morph from skeleton to loaded metadata card
            if (BorderMetaSkeleton != null) AnimatePanel(BorderMetaSkeleton, false);
            AnimatePanel(BorderMeta, true);
            SetStatus(L("StatusMetaLoaded"), true);
        }
        catch (Exception ex)
        {
            if (BorderMetaSkeleton != null) AnimatePanel(BorderMetaSkeleton, false);
            DevLog($"Error fetching metadata: {ex.Message}");
            Warn($"{L("ErrPrefix")} {ex.Message}");
            SetStatus($"{L("StatusLoadErr")}: {ex.Message}", false);
        }
        finally
        {
            BtnFetch.IsEnabled = true;
        }
    }

    private void LoadThumbnail(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ImgThumbnail.Source = null;
            TxtMetaThumbFallback.Visibility = Visibility.Visible;
            ResetThumbBackground();
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.DecodePixelWidth = 1280;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            if (bmp.IsDownloading || bmp.PixelWidth <= 1)
            {
                bmp.DownloadCompleted += (_, _) => {
                    if (bmp.PixelWidth > 1) ApplyDominantColor(bmp);
                };
            }
            else
            {
                ApplyDominantColor(bmp);
            }

            ImgThumbnail.Source = bmp;
            TxtMetaThumbFallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ImgThumbnail.Source = null;
            TxtMetaThumbFallback.Visibility = Visibility.Visible;
            ResetThumbBackground();
        }
    }

    /// <summary>How much longer than the thumbnail its reflection on the preview card's glass is drawn out.</summary>
    private const double ThumbReflectionStretch = 1.6;

    /// <summary>
    /// The thumbnail's surroundings reflect it, the way glass beside a video does in the Editor (and beside the
    /// colourful preview in Settings):
    /// <list type="bullet">
    /// <item>Behind the preview card's glass, the picture continues into its own mirror image right at the
    /// thumbnail's bottom edge, drawn out, blurred and washed out towards the window colour, fading away down the
    /// card. The unmirrored copy above the seam lies hidden under the thumbnail; it keeps the blur from fading
    /// the seam, so the reflection starts exactly where the thumbnail ends.</item>
    /// <item>The window's glass backdrop (GlassBackdrop) gets a copy of the thumbnail where it is on screen, so the
    /// rims of the glass around it (the mode toggle above, the settings card beside it) lens and reflect its colours.</item>
    /// </list>
    /// </summary>
    private void SetupThumbReflection()
    {
        var mirrored = ThumbCopy();
        mirrored.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = ThumbBgBorder });
        mirrored.LayoutTransform = new ScaleTransform(1, -ThumbReflectionStretch);
        var wash = new Border { Opacity = 0.35 };
        wash.SetResourceReference(Border.BackgroundProperty, "BgDeep");
        var above = ThumbCopy();
        above.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = ThumbBgBorder });

        double seam = 1 / (1 + ThumbReflectionStretch);  // where the thumbnail ends, as a share of the whole
        var reflection = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            // Wider than the card: the blur would fade the sides and the card's rims would lens the window instead,
            // so the glass under the thumbnail looked narrower than it. Beyond the card the rims bend this in.
            Margin = new Thickness(-40, 0, -40, 0),
            Children = { above, new Grid { Children = { mirrored, wash } } },
            Effect = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance },
            OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0xFF, 0, 0, 0), 0),
                new(Color.FromArgb(0xB0, 0, 0, 0), seam),
                new(Color.FromArgb(0x48, 0, 0, 0), seam + (1 - seam) * 0.3),
                new(Color.FromArgb(0x00, 0, 0, 0), seam + (1 - seam) * 0.65),
            }, new Point(0.5, 0), new Point(0.5, 1)),
        };
        LiquidGlass.SetBackdropOverlay(BorderMeta, reflection);

        // The copy in the window's backdrop follows the thumbnail (layout passes, and every frame of the card's
        // fade in or out, which also moves it) and is only there while the card is.
        var onBackdrop = ThumbCopy();
        GlassBackdrop.Child = new Canvas { Children = { onBackdrop } };
        void Place()
        {
            if (!BorderMeta.IsVisible || ThumbBgBorder.ActualWidth <= 0)
            {
                onBackdrop.Visibility = Visibility.Collapsed;
                return;
            }
            var at = ThumbBgBorder.TransformToVisual(GlassBackdrop).TransformBounds(new Rect(ThumbBgBorder.RenderSize));
            Canvas.SetLeft(onBackdrop, at.X);
            Canvas.SetTop(onBackdrop, at.Y);
            onBackdrop.Width = at.Width;
            onBackdrop.Height = at.Height;
            onBackdrop.Opacity = BorderMeta.Opacity;
            onBackdrop.Visibility = Visibility.Visible;
        }
        ThumbBgBorder.LayoutUpdated += (_, _) => Place();
        BorderMeta.IsVisibleChanged += (_, _) => Place();
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(Border))
            ?.AddValueChanged(BorderMeta, (_, _) => Place());
    }

    /// <summary>A live copy of what the thumbnail shows: its letterbox colour and the picture, fitted the same way.</summary>
    private Border ThumbCopy()
    {
        var image = new Image { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source)) { Source = ImgThumbnail });
        image.SetBinding(Image.StretchProperty, new Binding(nameof(Image.Stretch)) { Source = ImgThumbnail });
        var copy = new Border { ClipToBounds = true, Child = image };
        copy.SetBinding(Border.BackgroundProperty, new Binding(nameof(Border.Background)) { Source = ThumbBgBorder });
        return copy;
    }

    private void RefreshDominantColorVisuals()
    {
        var mode = ThemeService.Current?.GradientEffectMode ?? "thumbnail_only";
        if (mode == "none")
        {
            ResetThumbBackground();
            return;
        }

        if (_lastDominantColor is Color color)
        {
            var baseCardObj = FindResource("InputBg");
            var baseCard = baseCardObj is SolidColorBrush scb ? scb.Color : Color.FromArgb(0x0A, 255, 255, 255);
            byte cardAlpha = baseCard.A;

            // Gradient: subtle dominant tint at top fading to base InputBg at bottom while preserving glass material transparency
            const double a = 0.25;
            var cardTint = Color.FromArgb(
                cardAlpha,
                (byte)(baseCard.R * (1 - a) + color.R * a),
                (byte)(baseCard.G * (1 - a) + color.G * a),
                (byte)(baseCard.B * (1 - a) + color.B * a));

            DevLog($"Card tint calculated: {cardTint}, Base card: {baseCard}");

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(cardTint, 0.0),
                    new GradientStop(baseCard, 1.0)
                }
            };
            MetaTint.Background = gradient;  // on the card's glass (its own background lies under the glass)

            if (mode == "both")
            {
                // Measure position and update window gradient
                bool wasCollapsed = BorderMeta.Visibility == Visibility.Collapsed;
                if (wasCollapsed)
                {
                    BorderMeta.Visibility = Visibility.Visible;
                }

                RootBorder.UpdateLayout();
                UpdateWindowGradient();

                if (wasCollapsed)
                {
                    BorderMeta.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ResetWindowBackground();
            }

            // Subtle tint for the thumbnail container (visible in letterbox gaps)
            const double b = 0.30;
            var thumbBg = Color.FromArgb(
                (byte)Math.Max((int)cardAlpha, 0x22),
                (byte)(baseCard.R * (1 - b) + color.R * b),
                (byte)(baseCard.G * (1 - b) + color.G * b),
                (byte)(baseCard.B * (1 - b) + color.B * b));

            ThumbBgBorder.Background = new SolidColorBrush(thumbBg);
        }
        else
        {
            ResetThumbBackground();
        }
    }

    private void ApplyDominantColor(BitmapSource bmp)
    {
        try
        {
            DevLog($"ApplyDominantColor: source size = {bmp.PixelWidth}x{bmp.PixelHeight}");
            var color = GetDominantColor(bmp);
            DevLog($"Dominant color extracted: {color}");
            _lastDominantColor = color;

            if (ThemeService.Current.AdaptiveThumbnailTheme)
            {
                ThemeService.Apply(CreateAdaptiveTheme(ThemeService.Current, color));
            }
            else
            {
                ThemeService.Apply(ThemeService.Current);
            }
        }
        catch (Exception ex)
        {
            DevLog($"Dominant color extraction failed: {ex.Message}");
        }
    }

    private void ResetThumbBackground()
    {
        MetaTint.Background = null;
        ThumbBgBorder.SetResourceReference(Border.BackgroundProperty, "BgElevated");

        _lastDominantColor = null;
        UpdateWindowGradient();
    }

    private void UpdateWindowGradient()
    {
        var mode = ThemeService.Current?.GradientEffectMode ?? "thumbnail_only";
        if (mode == "both" && _lastDominantColor is Color color)
        {
            double relativeX = 0.255;
            double relativeY = 0.403;
            try
            {
                if (BorderMeta.Visibility == Visibility.Visible &&
                    ThumbBgBorder.ActualWidth > 0 && ThumbBgBorder.ActualHeight > 0 &&
                    RootBorder.ActualWidth > 0 && RootBorder.ActualHeight > 0)
                {
                    var centerInThumb = new Point(ThumbBgBorder.ActualWidth / 2, ThumbBgBorder.ActualHeight / 2);
                    var centerInRoot = ThumbBgBorder.TransformToAncestor(RootBorder).Transform(centerInThumb);
                    relativeX = centerInRoot.X / RootBorder.ActualWidth;
                    relativeY = centerInRoot.Y / RootBorder.ActualHeight;
                }
            }
            catch (Exception ex)
            {
                DevLog($"Failed to compute relative thumbnail position: {ex.Message}");
            }

            double w = RootBorder.ActualWidth > 0 ? RootBorder.ActualWidth : 760;
            double h = RootBorder.ActualHeight > 0 ? RootBorder.ActualHeight : 560;

            double radiusPixels = Math.Max(w, h) * 0.85;
            double radX = radiusPixels / w;
            double radY = radiusPixels / h;

            var windowBgObj = FindResource("BgDeep");
            var windowBg = windowBgObj is SolidColorBrush scbWin ? scbWin.Color : Color.FromArgb(0xE6, 20, 20, 22);
            byte bgAlpha = windowBg.A;

            const double aWin = 0.20;
            var windowTint = Color.FromArgb(
                bgAlpha,
                (byte)(windowBg.R * (1 - aWin) + color.R * aWin),
                (byte)(windowBg.G * (1 - aWin) + color.G * aWin),
                (byte)(windowBg.B * (1 - aWin) + color.B * aWin));

            var winGradient = new RadialGradientBrush
            {
                Center = new Point(relativeX, relativeY),
                GradientOrigin = new Point(relativeX, relativeY),
                RadiusX = radX,
                RadiusY = radY,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(windowTint, 0.0),
                    new GradientStop(windowBg, 1.0)
                }
            };
            RootBorder.Background = winGradient;
        }
        else
        {
            ResetWindowBackground();
        }
    }

    /// <summary>Back to the theme's own window background, as in the XAML. RootBorder is a SquircleBorder: its
    /// Background is SquircleBorder's own property, so Border.BackgroundProperty would set nothing.</summary>
    private void ResetWindowBackground() => RootBorder.SetResourceReference(SquircleBorder.BackgroundProperty, "BgGrad");

    private static Color Tint(Color baseColor, Color tintColor, double ratio)
    {
        return Color.FromArgb(
            baseColor.A,
            (byte)(baseColor.R * (1 - ratio) + tintColor.R * ratio),
            (byte)(baseColor.G * (1 - ratio) + tintColor.G * ratio),
            (byte)(baseColor.B * (1 - ratio) + tintColor.B * ratio));
    }

    private static Color ParseHex(string hex)
    {
        if (ThemeService.TryParseColor(hex, out var c)) return c;
        return Color.FromRgb(0, 0, 0);
    }

    private static string ToHex(Color c)
    {
        if (c.A < 255)
            return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    internal static ThemeSettings CreateAdaptiveTheme(ThemeSettings baseTheme, Color dominant)
    {
        var adaptive = baseTheme.Clone();
        adaptive.BgDeep = ToHex(Tint(ParseHex(baseTheme.BgDeep), dominant, 0.15));
        adaptive.BgSurface = ToHex(Tint(ParseHex(baseTheme.BgSurface), dominant, 0.18));
        adaptive.BgCard = ToHex(Tint(ParseHex(baseTheme.BgCard), dominant, 0.22));
        adaptive.BgElevated = ToHex(Tint(ParseHex(baseTheme.BgElevated), dominant, 0.26));
        adaptive.BorderSub = ToHex(Tint(ParseHex(baseTheme.BorderSub), dominant, 0.30));
        adaptive.Accent = ToHex(Tint(ParseHex(baseTheme.Accent), dominant, 0.75));
        adaptive.AccentAlt = ToHex(Tint(ParseHex(baseTheme.AccentAlt), dominant, 0.75));
        adaptive.ButtonGrad = ToHex(Tint(ParseHex(baseTheme.ButtonGrad), dominant, 0.65));
        return adaptive;
    }

    private static Color GetDominantColor(BitmapSource bmp)
    {
        var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = formatted.PixelWidth, h = formatted.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        formatted.CopyPixels(pixels, stride, 0);

        // Sample a grid of pixels and bucket by reduced color
        var buckets = new Dictionary<int, (long r, long g, long b, int count)>();
        int stepX = Math.Max(1, w / 30), stepY = Math.Max(1, h / 30);
        for (int y = 0; y < h; y += stepY)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x += stepX)
            {
                int i = rowOff + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];

                // Skip very dark or very light pixels (background / highlight)
                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                if (lum < 25 || lum > 240) continue;

                // Reduce to 5-bit per channel for bucketing
                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                if (buckets.TryGetValue(key, out var v))
                    buckets[key] = (v.r + r, v.g + g, v.b + b, v.count + 1);
                else
                    buckets[key] = (r, g, b, 1);
            }
        }

        if (buckets.Count == 0)
            return Color.FromRgb(30, 30, 34);

        var top = buckets.Values.OrderByDescending(v => v.count).First();
        return Color.FromRgb(
            (byte)(top.r / top.count),
            (byte)(top.g / top.count),
            (byte)(top.b / top.count));
    }

    // ════════════════════════════════════════════════════════════
    //  START JOB
    // ════════════════════════════════════════════════════════════
    private async void Start_Click(object s, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) { Warn(L("ErrNoUrl")); return; }
        if (!_backendReady) { Warn(L("ErrNoBackend")); return; }

        var isUpload = RbUpload.IsChecked == true;
        var isCloud = !isUpload && ChkCloudUpload.IsChecked == true;
        var isTempOnly = isCloud && SettingsService.Current.CloudTempDownload;
        var selectedCloudProvider = (CmbCloudProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? SettingsService.Current.CloudProvider;

        if (isUpload && string.IsNullOrWhiteSpace(TxtTitle.Text))
        { Warn(L("ErrNoTitle")); return; }
        if (!isUpload && !isTempOnly && string.IsNullOrWhiteSpace(TxtOutputDir.Text))
        { Warn(L("ErrNoSavePath")); return; }

        var privacy = (CmbPrivacy.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "public";
        var fingerprint = isUpload ? ChkFingerprint.IsChecked == true
                                   : ChkFingerprintDl.IsChecked == true;
        var fingerprintMethod = isUpload
            ? (CmbFingerprintMethod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard"
            : (CmbFingerprintMethodDl.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        var title = isUpload ? TxtTitle.Text.Trim() : TxtTitleDl.Text.Trim();
        var quality = (CmbQuality.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "best";

        var cts = BeginJobUi();
        _progressWin!.UpdateProgress(0, L("StatusStarting"), L("StatusConnectingBackend"));
        _progressWin.AppendLog(L("StatusConnectingBackend"));

        UpdateMiniProgress(0, L("StatusConnectingBackend"));

        try
        {
            bool isYTMusic = url.ToLowerInvariant().Contains("music.youtube.com") || (_meta?.IsMusic ?? false) || (quality?.StartsWith("ytmusic") ?? false);
            string platformParam = isYTMusic ? "ytmusic" : "auto";

            DevLog($"Creating job: mode={( isUpload ? "upload" : "download" )}, platform={platformParam}, fingerprint={fingerprint} ({fingerprintMethod}), quality={quality}");
            _currentJobId = await _backend.CreateJobAsync(
                url,
                isUpload ? "upload" : "download",
                platformParam,
                title,
                TxtDescription.Text.Trim(),
                _meta?.Tags,
                isUpload ? null : (isTempOnly ? null : TxtOutputDir.Text.Trim()),
                privacy,
                fingerprint,
                fingerprintMethod,
                SettingsService.Current.UseGpu,
                isUpload ? null : quality,
                cts.Token,
                cloudUpload: isCloud,
                cloudProvider: selectedCloudProvider,
                cloudTempOnly: isTempOnly);
            _lastJobHash = _currentJobId;
            var shortHash = !string.IsNullOrEmpty(_currentJobId) ? _currentJobId.Replace("-", "") : "";
            if (shortHash.Length > 8) shortHash = shortHash[..8];

            _progressWin?.AppendLog($"Job gestartet: #{shortHash}");

            await foreach (var status in _backend.StreamJobAsync(_currentJobId, cts.Token))
                {
                    var detail = "";
                    _progressWin?.UpdateProgress(status.Progress, status.Message, detail, status.Eta, status.Speed);
                    UpdateMiniProgress(status.Progress, status.Message);
                    if (!string.IsNullOrEmpty(status.Message))
                    {
                        var entry = $"{status.Progress}% \u2013 {status.Message}";
                        if (entry != _lastLogEntry)
                        {
                            _progressWin?.AppendLog(entry);
                            _lastLogEntry = entry;
                        }
                    }

                    if (status.Status == "completed")
                    {
                        var folderPath = "";
                        var filePath = "";
                        string? cloudUrl = null;
                        string? cloudProviderName = null;
                        bool isTempDeleted = false;

                        if (status.Result != null)
                        {
                            if (status.Result.TryGetValue("file_path", out var fp))
                                filePath = fp?.ToString() ?? "";
                            if (status.Result.TryGetValue("cloud_url", out var cu))
                                cloudUrl = cu?.ToString();
                            if (status.Result.TryGetValue("cloud_provider", out var cp))
                                cloudProviderName = cp?.ToString();
                            if (status.Result.TryGetValue("cloud_destination", out var cd))
                            {
                                var cdStr = cd?.ToString() ?? "";
                                if (string.IsNullOrEmpty(folderPath))
                                {
                                    if (Directory.Exists(cdStr)) folderPath = cdStr;
                                    else if (File.Exists(cdStr)) folderPath = Path.GetDirectoryName(cdStr) ?? "";
                                }
                            }
                            if (status.Result.TryGetValue("is_temp", out var _))
                                isTempDeleted = true;
                        }

                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            folderPath = System.IO.Path.GetDirectoryName(filePath) ?? folderPath;
                        }
                        else if (!isUpload && string.IsNullOrEmpty(folderPath) && !isTempDeleted)
                        {
                            folderPath = TxtOutputDir.Text.Trim();
                        }

                        _lastCloudUrl = cloudUrl;
                        if (!string.IsNullOrEmpty(cloudUrl) && BtnOpenCloudLink != null)
                        {
                            BtnOpenCloudLink.Visibility = Visibility.Visible;
                            if (TxtBtnOpenCloudLink != null)
                                TxtBtnOpenCloudLink.Text = $"Open {cloudProviderName ?? "Cloud"} Link";
                        }
                        else if (BtnOpenCloudLink != null)
                        {
                            BtnOpenCloudLink.Visibility = Visibility.Collapsed;
                        }

                        _progressWin?.MarkDone(true, !string.IsNullOrEmpty(folderPath) ? folderPath : cloudUrl);

                        _lastDownloadedFolder = folderPath;
                        _lastDownloadedFile = isTempDeleted ? null : filePath;
                        if (!string.IsNullOrEmpty(folderPath) || !string.IsNullOrEmpty(cloudUrl))
                            ShowOpenFolderBar();

                        if (!string.IsNullOrEmpty(cloudProviderName))
                        {
                            NotificationWindow.Show($"Uploaded to {cloudProviderName}!", this, NotificationType.Info);
                        }

                        break;
                    }

                if (status.Status == "error")
                {
                    var jobError = !string.IsNullOrWhiteSpace(status.Error) ? status.Error : status.Message;
                    _progressWin?.AppendLog($"[Fehler] {jobError}");
                    ReportError($"{L("StatusErrPrefix")} {jobError}");
                    _progressWin?.MarkDone(false);
                    break;
                }

                if (status.Status == "cancelled")
                {
                    SetStatus(L("StatusCancelled"), false);
                    _progressWin?.MarkDone(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_currentJobId != null)
                await _backend.CancelJobAsync(_currentJobId);
            SetStatus(L("StatusCancelled"), false);
        }
        catch (Exception ex)
        {
            ReportError($"{L("StatusErrPrefix")} {ex.Message}");
            _progressWin?.MarkDone(false);
        }
        finally
        {
            _currentJobId = null;
            BtnFetch.IsEnabled = true;
            ResetStartContainerToIdle();
        }
    }

    /// <summary>
    /// Prepares the UI for a new job and creates its progress window. Every handler only touches the
    /// window and token source it was created with, so closing an old progress window can no longer
    /// cancel (or detach) a job that was started afterwards.
    /// </summary>
    private CancellationTokenSource BeginJobUi()
    {
        _isJobRunning = true;
        BtnFetch.IsEnabled = false;
        HideOpenFolderBar();
        _lastDownloadedFolder = null;
        _lastDownloadedFile = null;

        _lastJobLog = string.Empty;
        _lastLogEntry = string.Empty;
        _logViewer?.Close();
        _progressWin?.Close();

        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var win = new ProgressWindow(cts, GetBtnRect(StartContainer)) { Owner = this };
        _progressWin = win;
        win.OnHiddenInBackground += (_, _) => HighlightMiniProgress(true);
        win.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_progressWin, win)) return;

            _lastJobLog = win.LogContent;
            _progressWin = null;
            RefreshHeaderPills();
            if (_currentJobId != null)
                _ = _backend.CancelJobAsync(_currentJobId);
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };
        RefreshHeaderPills();

        string autoProgressMode = SettingsService.Current.AutoProgressMode ?? (SettingsService.Current.AutoShowProgressWindow ? "show_progress" : "disabled");
        if (autoProgressMode != "disabled")
        {
            win.Show();
            if (autoProgressMode == "show_with_log")
                win.EnsureLogOpen();
        }

        return cts;
    }

    // ════════════════════════════════════════════════════════════
    //  LANGUAGE
    // ════════════════════════════════════════════════════════════
    private static string L(string key)
        => Application.Current.Resources[key] as string ?? key;

    /// <summary>Status bar text once the active engine is ready.</summary>
    private static string ReadyStatusText() => EngineProvider.ActiveKey switch
    {
        EngineProvider.NativeKey => "Ready (Native C#)",
        EngineProvider.FerriteKey => "Ready (Ferrite)",
        _ => L("StatusBackendReady"),
    };

    private bool _isClosingLangPopup;
    private DateTime _langPopupClosedTime = DateTime.MinValue;

    private void LangPopup_Closed(object sender, EventArgs e)
    {
        _langPopupClosedTime = DateTime.UtcNow;
        if (IconLangArrow != null) IconLangArrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
        RefreshHeaderPills();
    }

    private void LangPill_Click(object s, RoutedEventArgs e)
    {
        if (LangPopup == null) return;
        if ((DateTime.UtcNow - _langPopupClosedTime).TotalMilliseconds < 250)
        {
            return;
        }

        LangPopup.PlacementTarget = BtnLangPill;
        if (!LangPopup.IsOpen && !_isClosingLangPopup)
        {
            UpdateLanguageCheckmarks();
            LangPopup.IsOpen = true;
            if (IconLangArrow != null) IconLangArrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
            AnimatePopupIn(LangPopupBorder);
            RefreshHeaderPills();
        }
        else
        {
            CloseLangPopupAnimated();
        }
    }

    private void CloseLangPopupAnimated()
    {
        if (_isClosingLangPopup || LangPopup == null || !LangPopup.IsOpen) return;
        _isClosingLangPopup = true;
        _langPopupClosedTime = DateTime.UtcNow;
        if (IconLangArrow != null) IconLangArrow.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
        AnimatePopupOut(LangPopupBorder, () =>
        {
            LangPopup.IsOpen = false;
            _isClosingLangPopup = false;
            RefreshHeaderPills();
        });
    }

    private void SelectLang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string lang)
        {
            SetLanguage(lang);
            CloseLangPopupAnimated();
        }
    }

    private void UpdateLanguageCheckmarks()
    {
        if (CheckLangDE != null) CheckLangDE.Visibility = _currentLang == "de" ? Visibility.Visible : Visibility.Collapsed;
        if (CheckLangEN != null) CheckLangEN.Visibility = _currentLang == "en" ? Visibility.Visible : Visibility.Collapsed;
        if (CheckLangRU != null) CheckLangRU.Visibility = _currentLang == "ru" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLanguage(string lang)
    {
        _currentLang = lang;

        if (TxtCurrentLang != null)
        {
            TxtCurrentLang.Text = lang switch
            {
                "de" => "DE (Unsupported)",
                "ru" => "RU (Unsupported)",
                _ => "EN"
            };
            if (BtnLangPill != null)
            {
                BtnLangPill.ToolTip = lang == "en" ? "Language: English" : $"Language: {lang.ToUpperInvariant()} (Unsupported)";
            }
        }

        UpdateLanguageCheckmarks();
        LocalizationService.ApplyLanguage(lang);

        // Applied on every startup – only write settings.json when the language really changed.
        if (SettingsService.Current.Language != lang)
        {
            SettingsService.Current.Language = lang;
            SettingsService.Save(SettingsService.Current);
        }
    }

    private void MiniProgress_Click(object sender, MouseButtonEventArgs e)
    {
        if (_progressWin != null)
        {
            HighlightMiniProgress(false);
            if (_progressWin.Visibility != Visibility.Visible)
            {
                _progressWin.ShowWithAnimation();
            }
            else
            {
                if (_progressWin.WindowState == WindowState.Minimized)
                    _progressWin.WindowState = WindowState.Normal;
                _progressWin.Activate();
            }
            _progressWin.EnsureLogOpen();
        }
    }

    private bool _isJobRunning = false;

    private void StartContainer_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isJobRunning)
        {
            if (_progressWin != null)
            {
                if (_progressWin.Visibility != Visibility.Visible)
                    _progressWin.ShowWithAnimation();
                else
                {
                    if (_progressWin.WindowState == WindowState.Minimized)
                        _progressWin.WindowState = WindowState.Normal;
                    _progressWin.Activate();
                }
                _progressWin.EnsureLogOpen();
            }
            return;
        }

        Start_Click(sender, e);
    }

    private int _lastRenderedPercentageInt = -1;
    private string? _lastRenderedStatusMsg = null;
    private string _cachedMinimalStatusLabel = string.Empty;

    private static string GetMinimalStatusLabel(string rawMsg, double percentage)
    {
        if (string.IsNullOrWhiteSpace(rawMsg))
            return $"{percentage:0}%";

        string lower = rawMsg.ToLowerInvariant();

        if (lower.Contains("fingerprint"))
            return $"Fingerprint Bypass   {percentage:0}%";
        if (lower.Contains("upload"))
            return $"YouTube Upload   {percentage:0}%";
        if (lower.Contains("export"))
            return $"Export   {percentage:0}%";
        if (lower.Contains("merg") || lower.Contains("zusammenführ") || lower.Contains("zusammenfüg"))
            return $"Merging   {percentage:0}%";
        if (lower.Contains("music") || lower.Contains("musik") || lower.Contains("ytmusic"))
            return $"Download Music   {percentage:0}%";
        if (lower.Contains("download audio") || (lower.Contains("audio") && lower.Contains("download")))
            return $"Download Audio   {percentage:0}%";
        if (lower.Contains("download video") || lower.Contains("download") || lower.Contains("lade"))
            return $"Download Video   {percentage:0}%";
        if (lower.Contains("verbinde") || lower.Contains("starte"))
            return $"Starte   {percentage:0}%";

        var title = rawMsg;
        if (title.StartsWith('['))
        {
            var idx = title.IndexOf(']');
            if (idx >= 0 && idx + 1 < title.Length)
                title = title.Substring(idx + 1).Trim();
        }
        var colonIdx = title.IndexOf(':');
        if (colonIdx >= 0)
        {
            title = title.Substring(0, colonIdx).Trim();
        }
        title = title.TrimEnd('.', '…');

        return $"{title}   {percentage:0}%";
    }

    private bool _isStartContainerInProgress = false;
    private string? _lastStatusTextPrefix = null;
    private double _targetMiniProgressPercent = 0;
    private double _visualMiniProgressPercent = 0;
    private string? _latestStatusMsg = null;
    private bool _isProgressRenderingAttached = false;

    private void UpdateMiniProgress(double percentage, string? statusMsg = null)
    {
        _targetMiniProgressPercent = Math.Clamp(percentage, 0, 100);
        if (!string.IsNullOrWhiteSpace(statusMsg))
        {
            _latestStatusMsg = statusMsg;
        }

        if (StartContainer != null && StartProgressFill != null)
        {
            if (!_isStartContainerInProgress)
            {
                _isStartContainerInProgress = true;
                _visualMiniProgressPercent = 0;
                AnimateStartContainerToProgress();
            }

            EnsureProgressRenderingLoop();
        }
    }

    private void EnsureProgressRenderingLoop()
    {
        if (!_isProgressRenderingAttached)
        {
            _isProgressRenderingAttached = true;
            System.Windows.Media.CompositionTarget.Rendering += OnProgressRenderingFrame;
        }
    }

    private void StopProgressRenderingLoop()
    {
        if (_isProgressRenderingAttached)
        {
            _isProgressRenderingAttached = false;
            System.Windows.Media.CompositionTarget.Rendering -= OnProgressRenderingFrame;
        }
    }

    private void OnProgressRenderingFrame(object? sender, EventArgs e)
    {
        if (StartContainer == null || StartProgressFill == null) return;

        double diff = _targetMiniProgressPercent - _visualMiniProgressPercent;

        if (Math.Abs(diff) < 0.05)
        {
            _visualMiniProgressPercent = _targetMiniProgressPercent;
        }
        else
        {
            // 60 FPS smooth exponential lerp
            _visualMiniProgressPercent += diff * 0.16;
        }

        double containerWidth = StartContainer.ActualWidth;
        if (containerWidth <= 0) containerWidth = 300.0;
        double maxFillWidth = Math.Max(0, containerWidth - 6.0);

        double targetWidth = 0;
        if (_visualMiniProgressPercent > 0)
        {
            targetWidth = Math.Clamp(maxFillWidth * (_visualMiniProgressPercent / 100.0), 34.0, maxFillWidth);
        }
        else if (_isStartContainerInProgress || _isJobRunning)
        {
            targetWidth = 34.0;
        }

        // This runs every frame while a job is active; only touch the layout when the width changes.
        if (StartProgressFill.HasAnimatedProperties)
            StartProgressFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (Math.Abs(StartProgressFill.Width - targetWidth) > 0.25 || double.IsNaN(StartProgressFill.Width))
            StartProgressFill.Width = targetWidth;

        if (TxtStartLabel != null && !string.IsNullOrWhiteSpace(_latestStatusMsg))
        {
            int currentPctInt = (int)_visualMiniProgressPercent;
            if (currentPctInt != _lastRenderedPercentageInt || _latestStatusMsg != _lastRenderedStatusMsg)
            {
                _lastRenderedPercentageInt = currentPctInt;
                _lastRenderedStatusMsg = _latestStatusMsg;
                _cachedMinimalStatusLabel = GetMinimalStatusLabel(_latestStatusMsg, _visualMiniProgressPercent);

                if (TxtStartLabel.Text != _cachedMinimalStatusLabel)
                {
                    int colonIdx = _latestStatusMsg.IndexOf(':');
                    int bracketIdx = _latestStatusMsg.IndexOf(']');
                    int cutIdx = colonIdx >= 0 ? colonIdx : (bracketIdx >= 0 ? bracketIdx : _latestStatusMsg.Length);
                    string prefix = _latestStatusMsg.Substring(0, cutIdx).Trim();

                    if (_lastStatusTextPrefix != prefix)
                    {
                        _lastStatusTextPrefix = prefix;
                        var fade = new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(160))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        TxtStartLabel.BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                    TxtStartLabel.Text = _cachedMinimalStatusLabel;
                }
            }
        }

        if (!_isJobRunning && Math.Abs(_visualMiniProgressPercent - _targetMiniProgressPercent) < 0.05)
        {
            StopProgressRenderingLoop();
        }
    }

    private void AnimateStartContainerToProgress()
    {
        if (StartIdleBg != null)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, e) => { StartIdleBg.Visibility = Visibility.Collapsed; };
            StartIdleBg.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        if (StartTrackGroove != null)
        {
            StartTrackGroove.Visibility = Visibility.Visible;
            StartTrackGroove.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            StartTrackGroove.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        if (StartProgressFill != null)
        {
            StartProgressFill.Visibility = Visibility.Visible;
            StartProgressFill.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            StartProgressFill.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            if (ThemeService.Current.EnableProgressPulse)
            {
                Color glowColor = Color.FromArgb(255, 255, 255, 255);
                if (TryFindResource("Accent") is SolidColorBrush ab) glowColor = ab.Color;
                FluidMotion.StartProgressGlowPulse(StartProgressFill, glowColor, minBlur: 6, maxBlur: 18, minOpacity: 0.25, maxOpacity: 0.70);
            }
        }
    }

    private void ResetStartContainerToIdle()
    {
        _isJobRunning = false;
        _targetMiniProgressPercent = 0;
        _visualMiniProgressPercent = 0;
        _isStartContainerInProgress = false;
        _lastStatusTextPrefix = null;
        StopProgressRenderingLoop();

        if (StartIdleBg != null)
        {
            StartIdleBg.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            StartIdleBg.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        if (StartTrackGroove != null)
        {
            var fadeOut = new DoubleAnimation(StartTrackGroove.Opacity, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, e) => { StartTrackGroove.Visibility = Visibility.Collapsed; };
            StartTrackGroove.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        if (StartProgressFill != null)
        {
            FluidMotion.StopProgressGlowPulse(StartProgressFill, idleBlur: 10, idleOpacity: 0.35);
            var fadeOut = new DoubleAnimation(StartProgressFill.Opacity, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, e) =>
            {
                StartProgressFill.Visibility = Visibility.Collapsed;
                StartProgressFill.BeginAnimation(FrameworkElement.WidthProperty, null);
                StartProgressFill.Width = 0;
            };
            StartProgressFill.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        if (TxtStartLabel != null)
        {
            TxtStartLabel.SetResourceReference(TextBlock.TextProperty, "BtnStart");
            TxtStartLabel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }
    }

    private void HighlightMiniProgress(bool highlight)
    {
        if (highlight)
        {
            // Set border brush to accent color
            var accentBrush = TryFindResource("Accent") as Brush;
            if (accentBrush != null)
                MiniProgressContainer.BorderBrush = accentBrush;

            // Update glow color to dynamic accent
            if (accentBrush is SolidColorBrush scb)
                MiniProgressGlow.Color = scb.Color;

            // Start glow storyboard
            if (MiniProgressContainer.Resources["GlowPulse"] is Storyboard sb)
            {
                sb.Begin(MiniProgressContainer, true);
            }
        }
        else
        {
            // Reset border brush to standard BorderSub
            MiniProgressContainer.BorderBrush = TryFindResource("BorderSub") as Brush ?? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

            // Stop glow storyboard
            if (MiniProgressContainer.Resources["GlowPulse"] is Storyboard sb)
            {
                sb.Stop(MiniProgressContainer);
            }

            MiniProgressGlow.BlurRadius = 0;
            MiniProgressGlow.Opacity = 0;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  SETTINGS
    // ════════════════════════════════════════════════════════════
    private void Settings_Click(object s, RoutedEventArgs e)
    {
        if (_settingsWin != null && _settingsWin.IsLoaded)
        {
            _settingsWin.Focus();
            return;
        }

        OpenSettingsWindow(GetBtnRect((UIElement)s), null);
    }

    /// <summary>Opens Settings (or focuses it), optionally on a specific pane ("appearance", "advanced", "download", "upload").</summary>
    private void OpenSettingsWindow(Rect originRect, string? initialTab)
    {
        if (_settingsWin != null && _settingsWin.IsLoaded)
        {
            _settingsWin.Focus();
            if (initialTab != null) _settingsWin.SelectPane(initialTab);
            return;
        }

        var win = _settingsSpare ?? new SettingsWindow();
        _settingsSpare = null;
        win.OriginRect = originRect;
        win.InitialTab = initialTab;
        win.Owner = this;
        _settingsWin = win;
        win.Closed += (sender, args) =>
        {
            _settingsWin = null;
            UpdateHeaderToolButtonsVisibility();
            if (win.HasChanges)
                ApplySettings();
            PrepareSettingsWindow(TimeSpan.FromSeconds(2));
        };
        win.Show();
        UpdateHeaderToolButtonsVisibility();
    }

    // Building Settings (its XAML is the biggest) froze the main window for ~75 ms on every click, longest the
    // first time. So the next one is built ahead while the app is idle and a click only shows it. It gets its
    // Owner when it opens, so until then it doesn't count as open (IsWindowOpen).
    private SettingsWindow? _settingsSpare;

    private async void PrepareSettingsWindow(TimeSpan delay)
    {
        await Task.Delay(delay);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_settingsSpare != null || _settingsWin != null || _isAnimatingClose || !IsLoaded) return;
            _settingsSpare = new SettingsWindow();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ════════════════════════════════════════════════════════════
    //  VIDEO EDITOR
    // ════════════════════════════════════════════════════════════
    private void Editor_Click(object s, RoutedEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            NotificationWindow.Show(L("ErrEditorDisabledPerf"), this, NotificationType.Warning);
            return;
        }

        if (Services.SettingsService.Current.EnableDeveloperMode)
        {
            if (_editorWin != null && _editorWin.IsLoaded)
            {
                _editorWin.Focus();
                return;
            }

            var win = new EditorWindow(GetBtnRect((UIElement)s)) { Owner = this };
            _editorWin = win;
            win.Closed += (sender, args) =>
            {
                _editorWin = null;
                UpdateHeaderToolButtonsVisibility();
            };
            win.Show();
            UpdateHeaderToolButtonsVisibility();
        }
        else
        {
            NotificationWindow.Show(L("ErrEditorDisabled"), this, NotificationType.Warning);
        }
    }

    private void ApplySettings()
    {
        var s = SettingsService.Current;
        if (s.Language != _currentLang) SetLanguage(s.Language);
        Topmost = s.AlwaysOnTop;
        SelectComboByTag(CmbPrivacy, s.DefaultPrivacy);
        ChkFingerprint.IsChecked = s.DefaultFingerprintEnabled;
        SelectComboByTag(CmbFingerprintMethod, s.DefaultFingerprintMethod);
        if (!string.IsNullOrEmpty(s.DefaultOutputDir))
            TxtOutputDir.Text = s.DefaultOutputDir;
        ChkFingerprintDl.IsChecked = s.DefaultFingerprintDlEnabled;
        SelectComboByTag(CmbFingerprintMethodDl, s.DefaultFingerprintDlMethod);
        if (ChkCloudUpload != null) ChkCloudUpload.IsChecked = s.CloudUploadEnabled;
        if (CmbCloudProvider != null) SelectComboByTag(CmbCloudProvider, s.CloudProvider);
        UpdateCloudUiState();
        RefreshHeaderPills();
        if (!s.DevConsoleEnabled) _devConsoleWin?.Close();
        SettingsService.ApplyScrollbarVisibility();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Tag?.ToString() == tag) { combo.SelectedItem = item; return; }
    }

    // ════════════════════════════════════════════════════════════
    //  CLEAR FORM
    // ════════════════════════════════════════════════════════════
    private void ClearForm_Click(object s, RoutedEventArgs e)
    {
        TxtUrl.Text = string.Empty;
        TxtTitleDl.Text = string.Empty;
        _meta = null;
        ImgThumbnail.Source = null;
        TxtMetaThumbFallback.Visibility = Visibility.Visible;
        ResetThumbBackground();
        BorderDurationBadge.Visibility = Visibility.Collapsed;
        TxtMetaAlbum.Visibility = Visibility.Collapsed;
        TagsList.ItemsSource = null;
        TagsList.Visibility = Visibility.Collapsed;
        UpdateQualityOptionsVisibility(false);
        AnimatePanel(BorderMeta, false);
        HideOpenFolderBar();
    }

    // ════════════════════════════════════════════════════════════
    //  QUICK ACTIONS
    // ════════════════════════════════════════════════════════════
    private void CopyTitle_Click(object s, RoutedEventArgs e)
    {
        if (_meta is not null && !string.IsNullOrEmpty(_meta.Title))
            Clipboard.SetText(_meta.Title);
    }

    private void CopyUrl_Click(object s, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        if (!string.IsNullOrEmpty(url))
            Clipboard.SetText(url);
    }

    private void OpenInBrowser_Click(object s, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        if (!string.IsNullOrEmpty(url))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void SaveThumbnail_Click(object s, RoutedEventArgs e)
    {
        if (_meta is null || string.IsNullOrWhiteSpace(_meta.Thumbnail)) return;
        var dlg = new SaveFileDialog
        {
            Title = L("SaveThumbnailTitle"),
            Filter = "JPEG|*.jpg|PNG|*.png",
            FileName = "thumbnail.jpg",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_meta.Thumbnail, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            BitmapEncoder encoder = dlg.FilterIndex == 2
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using var fs = System.IO.File.Create(dlg.FileName);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            Warn($"{L("ErrPrefix")} {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ANIMATIONS
    // ════════════════════════════════════════════════════════════
    private static void AnimatePanel(Border? panel, bool show)
    {
        if (panel == null) return;
        if (show)
            FluidMotion.ShowPanel(panel);
        else
            FluidMotion.HidePanel(panel);
    }

    // Action buttons share the card's height: full size normally, smaller when the cloud options
    // make the download card taller, but never huge in a tall window.
    private const double ActionButtonMinHeight = 22;
    private const double ActionButtonMaxHeight = 44;

    private void ApplyCompletedActionsLayout()
    {
        if (GridCompletedActions == null || BorderCompletedActions == null) return;

        var visibleButtons = new[] { BtnOpenCloudLink, BtnOpenFileLocation, BtnOpenInEditor, BtnGenerateSubtitles }
            .Where(b => b != null && b.Visibility == Visibility.Visible)
            .ToList();

        int count = visibleButtons.Count;
        if (count == 0) return;

        // Stacked under each other; four buttons (cloud link + developer editor) would get too
        // thin in one column, so they go 2 × 2.
        int columns = count >= 4 ? 2 : 1;
        int rows = (count + columns - 1) / columns;
        GridCompletedActions.Columns = columns;
        GridCompletedActions.Rows = 0;

        // The card's inset: on glass, the padding lives on the content's host (LiquidGlass moved it there).
        CompletedActionsHost.Margin = new Thickness(10, 6, 10, 6);
        BorderCompletedActions.Margin = new Thickness(0, 8, 0, 0);

        const double spacing = 4;
        double fontSize = count >= 4 ? 11 : 11.5;
        double iconSize = count >= 4 ? 12 : 12.5;

        // Same margin on every button keeps the UniformGrid cells (and so the buttons) equal;
        // the grid's negative margin cancels the outer half-gaps.
        GridCompletedActions.Margin = new Thickness(-spacing / 2);
        foreach (var btn in visibleButtons)
        {
            btn.Margin = new Thickness(spacing / 2);
            btn.Height = double.NaN;
            btn.VerticalAlignment = VerticalAlignment.Stretch;
        }

        if (RowCompletedActions != null)
        {
            double chrome = BorderCompletedActions.Margin.Top
                + CompletedActionsHost.Margin.Top + CompletedActionsHost.Margin.Bottom
                + BorderCompletedActions.BorderThickness.Top + BorderCompletedActions.BorderThickness.Bottom
                + spacing * (rows - 1);
            RowCompletedActions.MinHeight = chrome + rows * ActionButtonMinHeight;
            RowCompletedActions.MaxHeight = chrome + rows * ActionButtonMaxHeight;
        }

        if (TxtBtnOpenFileLocation != null) TxtBtnOpenFileLocation.FontSize = fontSize;
        if (TxtBtnOpenInEditor != null) TxtBtnOpenInEditor.FontSize = fontSize;
        if (TxtBtnGenSubtitles != null) TxtBtnGenSubtitles.FontSize = fontSize;
        if (TxtBtnOpenCloudLink != null) TxtBtnOpenCloudLink.FontSize = fontSize;

        if (IconOpenFileLocation != null) { IconOpenFileLocation.FontSize = iconSize; IconOpenFileLocation.Margin = new Thickness(0, 0, 5, 0); }
        if (IconOpenInEditor != null) { IconOpenInEditor.FontSize = iconSize; IconOpenInEditor.Margin = new Thickness(0, 0, 5, 0); }
        if (IconGenSubtitles != null) { IconGenSubtitles.FontSize = iconSize; IconGenSubtitles.Margin = new Thickness(0, 0, 5, 0); }
        if (IconOpenCloudLink != null) { IconOpenCloudLink.FontSize = iconSize; IconOpenCloudLink.Margin = new Thickness(0, 0, 5, 0); }

        // Full labels
        if (TxtBtnOpenFileLocation != null)
            TxtBtnOpenFileLocation.Text = L("BtnOpenFileLocation");
        if (TxtBtnOpenInEditor != null)
            TxtBtnOpenInEditor.Text = L("BtnOpenInEditor");
        if (TxtBtnGenSubtitles != null && !TxtBtnGenSubtitles.Text.Contains("✓") && !TxtBtnGenSubtitles.Text.Contains("..."))
            TxtBtnGenSubtitles.Text = L("BtnGenSubtitles");
    }

    private void ShowOpenFolderBar()
    {
        if (BorderCompletedActions.Visibility == Visibility.Visible) return;
        if (RbDownload?.IsChecked != true) return;

        bool devMode = SettingsService.Current.EnableDeveloperMode;
        BtnOpenInEditor.Visibility = devMode ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(_lastDownloadedFolder) && !string.IsNullOrEmpty(_lastCloudUrl))
            BtnOpenFileLocation.Visibility = Visibility.Collapsed;
        else
            BtnOpenFileLocation.Visibility = Visibility.Visible;

        if (BtnGenerateSubtitles != null)
        {
            BtnGenerateSubtitles.Visibility = Visibility.Visible;
            BtnGenerateSubtitles.IsEnabled = true;
            if (TxtBtnGenSubtitles != null)
                TxtBtnGenSubtitles.Text = L("BtnGenSubtitles");
            if (IconGenSubtitles != null)
                IconGenSubtitles.Symbol = Wpf.Ui.Controls.SymbolRegular.ClosedCaption24;
        }

        ApplyCompletedActionsLayout();

        BorderCompletedActions.Visibility = Visibility.Visible;
        BorderCompletedActions.Opacity = 0;
        BorderCompletedActions.RenderTransformOrigin = new Point(0.5, 0.5);
        if (Liquid.IsActive)
        {
            // The card rises in; its buttons pop out of it one after another.
            var (cardS, cardM) = Liquid.Rig(BorderCompletedActions);
            Liquid.Arrive(BorderCompletedActions, cardS, cardM, 0.94, 0.9, 12, Spring.Bouncy);
            int n = 0;
            foreach (var b in new[] { BtnOpenCloudLink, BtnOpenFileLocation, BtnOpenInEditor, BtnGenerateSubtitles })
            {
                if (b is not { Visibility: Visibility.Visible }) continue;
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                var (bs, bm) = Liquid.Rig(b);
                Liquid.Arrive(b, bs, bm, 0.86, 0.8, 14, Spring.Jelly, delayMs: 80 + n++ * 55);
            }
            return;
        }
        var cardGroup = new TransformGroup();
        var cardScale = new ScaleTransform(0.95, 0.95);
        var cardTrans = new TranslateTransform(0, 10);
        cardGroup.Children.Add(cardScale);
        cardGroup.Children.Add(cardTrans);
        BorderCompletedActions.RenderTransform = cardGroup;

        var spring = AppleSpringEase.Interactive;
        var smooth = AppleSpringEase.Gentle;

        BorderCompletedActions.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = smooth });
        cardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = spring });
        cardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = spring });
        cardTrans.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = spring });

        // Stagger only the visible buttons inside the card
        var buttons = new List<Button>();
        if (BtnOpenCloudLink != null && BtnOpenCloudLink.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenCloudLink);
        if (BtnOpenFileLocation != null && BtnOpenFileLocation.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenFileLocation);
        if (BtnOpenInEditor != null && BtnOpenInEditor.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenInEditor);
        if (BtnGenerateSubtitles != null && BtnGenerateSubtitles.Visibility == Visibility.Visible)
            buttons.Add(BtnGenerateSubtitles);

        var staggerSpring = AppleSpringEase.Bouncy;
        for (int i = 0; i < buttons.Count; i++)
        {
            var btn = buttons[i];
            if (btn == null) continue;

            btn.Opacity = 0;
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            var btnGroup = new TransformGroup();
            var btnScale = new ScaleTransform(0.88, 0.88);
            var btnTrans = new TranslateTransform(0, 16);
            btnGroup.Children.Add(btnScale);
            btnGroup.Children.Add(btnTrans);
            btn.RenderTransform = btnGroup;

            var delay = TimeSpan.FromMilliseconds(90 + i * 85);

            btn.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
                { BeginTime = delay, EasingFunction = smooth });

            btnScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(420))
                { BeginTime = delay, EasingFunction = staggerSpring });
            btnScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(420))
                { BeginTime = delay, EasingFunction = staggerSpring });

            btnTrans.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420))
                { BeginTime = delay, EasingFunction = staggerSpring });
        }
    }

    private void HideOpenFolderBar()
    {
        if (BorderCompletedActions.Visibility != Visibility.Visible) return;

        if (Liquid.IsActive)
        {
            var (cardS, cardM) = Liquid.Rig(BorderCompletedActions);
            Liquid.Leave(BorderCompletedActions, cardS, cardM, 0.96, 0.94, 8,
                done: () => BorderCompletedActions.Visibility = Visibility.Collapsed);
            return;
        }

        var buttons = new List<Button>();
        if (BtnGenerateSubtitles != null && BtnGenerateSubtitles.Visibility == Visibility.Visible)
            buttons.Add(BtnGenerateSubtitles);
        if (BtnOpenInEditor != null && BtnOpenInEditor.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenInEditor);
        if (BtnOpenFileLocation != null && BtnOpenFileLocation.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenFileLocation);
        if (BtnOpenCloudLink != null && BtnOpenCloudLink.Visibility == Visibility.Visible)
            buttons.Add(BtnOpenCloudLink);

        var ease = AppleSpringEase.Snappy;
        var dur = TimeSpan.FromMilliseconds(160);

        for (int i = 0; i < buttons.Count; i++)
        {
            var btn = buttons[i];
            if (btn == null) continue;

            var delay = TimeSpan.FromMilliseconds(i * 40);
            var group = btn.RenderTransform as TransformGroup;
            var st = group?.Children.OfType<ScaleTransform>().FirstOrDefault();
            var tt = group?.Children.OfType<TranslateTransform>().FirstOrDefault();

            btn.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, dur) { BeginTime = delay, EasingFunction = ease });

            tt?.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 8, dur) { BeginTime = delay, EasingFunction = ease });

            st?.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.94, dur) { BeginTime = delay, EasingFunction = ease });
            st?.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.94, dur) { BeginTime = delay, EasingFunction = ease });
        }

        var totalDur = TimeSpan.FromMilliseconds(160 + buttons.Count * 40);
        var opAnim = new DoubleAnimation(1, 0, totalDur) { EasingFunction = ease };
        opAnim.Completed += (_, _) =>
        {
            BorderCompletedActions.Visibility = Visibility.Collapsed;
        };
        BorderCompletedActions.BeginAnimation(UIElement.OpacityProperty, opAnim);
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastDownloadedFile) && File.Exists(_lastDownloadedFile))
            Process.Start("explorer.exe", $"/select,\"{_lastDownloadedFile}\"");
        else if (!string.IsNullOrEmpty(_lastDownloadedFolder) && Directory.Exists(_lastDownloadedFolder))
            Process.Start("explorer.exe", $"\"{_lastDownloadedFolder}\"");
    }

    private void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (Services.SettingsService.Current.EnableDeveloperMode)
        {
            if (string.IsNullOrEmpty(_lastDownloadedFile) || !System.IO.File.Exists(_lastDownloadedFile))
                return;

            foreach (Window w in OwnedWindows)
            {
                if (w is EditorWindow existing)
                {
                    existing.Focus();
                    existing.LoadVideo(_lastDownloadedFile);
                    return;
                }
            }

            var win = new EditorWindow(GetBtnRect((UIElement)sender), _lastDownloadedFile) { Owner = this };
            win.Show();
        }
        else
        {
            NotificationWindow.Show(L("ErrEditorDisabled"), this, NotificationType.Warning);
        }
    }

    private async void GenerateSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastDownloadedFile) || !System.IO.File.Exists(_lastDownloadedFile))
        {
            NotificationWindow.Show("No downloaded file available", this, NotificationType.Warning);
            return;
        }

        BtnGenerateSubtitles.IsEnabled = false;
        TxtBtnGenSubtitles.Text = L("BtnGenSubtitlesGenerating");
        if (IconGenSubtitles != null)
            IconGenSubtitles.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24;

        try
        {
            var srt = await _backend.GenerateSubtitlesAsync(_lastDownloadedFile, modelSize: "base");
            if (!string.IsNullOrWhiteSpace(srt))
            {
                var srtPath = System.IO.Path.ChangeExtension(_lastDownloadedFile, ".srt");
                await System.IO.File.WriteAllTextAsync(srtPath, srt, System.Text.Encoding.UTF8);
                NotificationWindow.Show(string.Format(L("SubtitlesSaved"), System.IO.Path.GetFileName(srtPath)), this, NotificationType.Info);
                TxtBtnGenSubtitles.Text = L("BtnGenSubtitles");
                if (IconGenSubtitles != null)
                    IconGenSubtitles.Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;
            }
            else
            {
                NotificationWindow.Show("No subtitles generated or speech detected", this, NotificationType.Warning);
                TxtBtnGenSubtitles.Text = L("BtnGenSubtitles");
                if (IconGenSubtitles != null)
                    IconGenSubtitles.Symbol = Wpf.Ui.Controls.SymbolRegular.ClosedCaption24;
            }
        }
        catch (Exception ex)
        {
            ReportError($"Subtitles error: {ex.Message}");
            TxtBtnGenSubtitles.Text = L("BtnGenSubtitles");
            if (IconGenSubtitles != null)
                IconGenSubtitles.Symbol = Wpf.Ui.Controls.SymbolRegular.ClosedCaption24;
        }
        finally
        {
            BtnGenerateSubtitles.IsEnabled = true;
        }
    }

    private static void AnimatePopupIn(Border border)
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

    private static void AnimatePopupOut(Border border, Action onDone)
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

    // ════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════
    private const int MaxDevLogChars = 250_000;
    private readonly System.Text.StringBuilder _devLogBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingDevLog = new();
    private int _devLogFlushScheduled;

    /// <summary>
    /// Thread-safe: engine output arrives on worker threads, often dozens of lines per second.
    /// Lines are queued and written to the console in one batch per dispatcher pass.
    /// </summary>
    private void DevLog(string message)
    {
        var category = "system";
        var lower = message.ToLowerInvariant();
        if (lower.Contains("[backend]") || lower.Contains("[ping]") || lower.Contains("[status]") || lower.Contains("[restart]") || lower.Contains("pinging backend") || lower.Contains("restarting backend"))
        {
            category = "backend";
        }
        else if (lower.Contains("[ffmpeg]") || lower.Contains("[yt-dlp]") || lower.Contains("ffmpeg") || lower.Contains("yt-dlp"))
        {
            category = "ffmpeg";
        }

        var s = SettingsService.Current;
        if (category == "system" && !s.ConsoleShowSystem) return;
        if (category == "backend" && !s.ConsoleShowBackend) return;
        if (category == "ffmpeg" && !s.ConsoleShowFFmpeg) return;

        _pendingDevLog.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        if (Interlocked.Exchange(ref _devLogFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FlushDevLog);
    }

    private void FlushDevLog()
    {
        Interlocked.Exchange(ref _devLogFlushScheduled, 0);
        if (_pendingDevLog.IsEmpty) return;

        var chunk = new System.Text.StringBuilder();
        while (_pendingDevLog.TryDequeue(out var line))
        {
            if (chunk.Length > 0) chunk.AppendLine();
            chunk.Append(line);
        }

        if (_devLogBuffer.Length > 0) _devLogBuffer.AppendLine();
        _devLogBuffer.Append(chunk);
        if (_devLogBuffer.Length > MaxDevLogChars)
            _devLogBuffer.Remove(0, _devLogBuffer.Length - MaxDevLogChars / 2);

        _devConsoleWin?.AppendLog(chunk.ToString());
    }

    // ═══ Header pills ═══
    // RefreshHeaderPills works out the whole toolbar at once (which pills, buttons, dividers and labels show, and whether
    // the tools fold into •••) and hands each item to PillMotion, which grows or shrinks it in place: pills change width
    // and their neighbours glide along frame by frame. Everything that affects the toolbar just calls it again.

    private bool _pillsInitialized;
    private bool _toolsFolded;

    private void RefreshHeaderPills()
    {
        if (HeaderActionStack == null) return;
        var s = SettingsService.Current;
        // The first pass (window opening) only sets the scene.
        bool animate = _pillsInitialized && IsLoaded && IsVisible;

        bool perfOverlay = s.ShowPerformanceOverlay;
        // A button hides while the pop-up or window it opens is up: that one grows out of it.
        bool error = !string.IsNullOrEmpty(_rawErrorDetails) && ErrorPopup?.IsOpen != true;
        bool help = s.ShowHelpPill && HelpPopup?.IsOpen != true;
        bool console = s.ShowDevPill && s.DevConsoleEnabled && _devConsoleWin == null;
        bool log = s.ShowDevPill && !string.IsNullOrEmpty(_lastJobLog) && _logViewer == null;
        bool editor = s.ShowEditorPill && s.EnableDeveloperMode && !s.EnablePerformanceMode
                      && _editorWin?.IsLoaded != true && !IsWindowOpen<EditorWindow>();
        bool batch = _batchQueueWin?.IsLoaded != true && !IsWindowOpen<BatchQueueWindow>();
        bool settings = _settingsWin?.IsLoaded != true && !IsWindowOpen<SettingsWindow>();
        bool lang = s.ShowLangPill && LangPopup?.IsOpen != true;
        bool labels = s.ShowIconLabels;
        bool tools = editor || batch || settings;
        bool fold = tools && ShouldFoldTools(perfOverlay, error, help, console, log, editor, batch, settings, lang, labels);

        PillMotion.SetShown(BorderPerformanceOverlay, perfOverlay, animate);

        PillMotion.SetShown(PillHelpGroup, error || help, animate);
        PillMotion.SetShown(BtnErrorStatus, error, animate);
        PillMotion.SetShown(ErrorPillDivider, error && help, animate, pop: false);
        PillMotion.SetShown(BtnHelp, help, animate);

        PillMotion.SetShown(DevPillGroup, console || log, animate);
        PillMotion.SetShown(BtnHeaderConsole, console, animate);
        PillMotion.SetShown(HeaderDevDivider, console && log, animate, pop: false);
        PillMotion.SetShown(BtnHeaderLog, log, animate);

        PillMotion.SetShown(PillToolsGroup, tools, animate);
        PillMotion.SetShown(StackToolsExpanded, !fold, animate, pop: false);
        PillMotion.SetShown(BtnToolsCollapsed, fold, animate);
        PillMotion.SetShown(BtnVideoEditor, editor, animate);
        PillMotion.SetShown(VideoEditorSep, editor && (batch || settings), animate, pop: false);
        PillMotion.SetShown(BtnBatchQueue, batch, animate);
        PillMotion.SetShown(BatchQueueSep, batch && settings, animate, pop: false);
        PillMotion.SetShown(BtnSettings, settings, animate);

        PillMotion.SetShown(LangPillGlass, lang, animate);

        foreach (var label in new FrameworkElement?[]
                 { TxtErrorStatusLabel, TxtHelpLabel, TxtHeaderConsoleLabel, TxtHeaderLogLabel, TxtToolEditor, TxtToolBatchQueue, TxtToolSettings })
            PillMotion.SetShown(label, labels, animate, pop: false);

        _pillsInitialized = true;
    }

    /// <summary>
    /// Whether the tools pill folds into •••. It is judged on where the toolbar is heading, not on what is still
    /// mid-animation, and unfolds only with some room to spare, so it doesn't flip back and forth at the edge.
    /// </summary>
    private bool ShouldFoldTools(bool perfOverlay, bool error, bool help, bool console, bool log,
                                 bool editor, bool batch, bool settings, bool lang, bool labels)
    {
        double windowWidth = ActualWidth > 0 ? ActualWidth : 760;
        // Title (~185px) + window controls (~85px) + breathing gap (~45px), and the mini progress bar while it shows
        double reserved = 315 + (MiniProgressContainer?.Visibility == Visibility.Visible ? 150 : 0);
        double room = Math.Max(80, windowWidth - reserved);

        if (perfOverlay) room -= 210;
        room -= TwoButtonPillWidth(labels, (error, 76), (help, 71));
        room -= TwoButtonPillWidth(labels, (console, 84), (log, 64));
        if (lang) room -= 56;

        int count = (editor ? 1 : 0) + (batch ? 1 : 0) + (settings ? 1 : 0);
        double toolsWidth = labels
            ? (editor ? 68 : 0) + (batch ? 74 : 0) + (settings ? 78 : 0) + Math.Max(0, count - 1) * 5 + 6
            : count * 34.0 + Math.Max(0, count - 1) * 5.0 + 6.0;

        const double unfoldMargin = 16;
        _toolsFolded = _toolsFolded ? room < toolsWidth + unfoldMargin : room < toolsWidth;
        return _toolsFolded;
    }

    /// <summary>Width of a pill with up to two buttons: each one's width with its label, or 39 without labels.</summary>
    private static double TwoButtonPillWidth(bool labels, (bool on, double labelled) a, (bool on, double labelled) b)
    {
        if (!labels) return (a.on ? 39 : 0) + (b.on ? 39 : 0);
        return (a.on ? a.labelled : 0) + (b.on ? b.labelled : 0) + (a.on && b.on ? 5 : 0);
    }

    private System.Windows.Threading.DispatcherTimer? _perfTimer;

    private void UpdatePerformanceOverlayVisibility()
    {
        if (BorderPerformanceOverlay == null) return;

        if (SettingsService.Current.ShowPerformanceOverlay)
        {
            if (_perfTimer == null)
            {
                _perfTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _perfTimer.Tick += (_, _) => RefreshPerformanceStats();
            }
            _perfTimer.Start();
            RefreshPerformanceStats();
        }
        else
        {
            _perfTimer?.Stop();
        }
        RefreshHeaderPills();
    }

    private EditorWindow? _editorWin;
    private BatchQueueWindow? _batchQueueWin;
    private SettingsWindow? _settingsWin;

    private bool IsWindowOpen<T>() where T : Window
    {
        foreach (Window w in OwnedWindows)
        {
            if (w is T) return true;
        }
        return false;
    }

    private void UpdateHeaderToolButtonsVisibility()
    {
        RefreshHeaderPills();

        if (BorderCompletedActions?.Visibility == Visibility.Visible)
        {
            BtnOpenInEditor.Visibility = SettingsService.Current.EnableDeveloperMode ? Visibility.Visible : Visibility.Collapsed;
            if (BtnGenerateSubtitles != null)
                BtnGenerateSubtitles.Visibility = Visibility.Visible;
            ApplyCompletedActionsLayout();
        }
    }

    private void UpdateIconLabelsVisibility()
    {
        var vis = SettingsService.Current.ShowIconLabels ? Visibility.Visible : Visibility.Collapsed;

        if (TxtQuickCopyTitle != null) TxtQuickCopyTitle.Visibility = vis;
        if (TxtQuickCopyUrl != null) TxtQuickCopyUrl.Visibility = vis;
        if (TxtQuickOpenBrowser != null) TxtQuickOpenBrowser.Visibility = vis;
        if (TxtQuickSaveThumb != null) TxtQuickSaveThumb.Visibility = vis;

        // The header's labels slide in and out with the pills.
        UpdateHeaderToolButtonsVisibility();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshHeaderPills();
        UpdateWindowGradient();
        UpdateThumbnailMaxHeight();
    }

    private void UpdateThumbnailMaxHeight()
    {
        if (ThumbBgBorder == null) return;
        double baseWindowHeight = 570;
        double baseThumbHeight = 185;
        double actualH = ActualHeight > 0 ? ActualHeight : baseWindowHeight;
        double extraHeight = Math.Max(0, actualH - baseWindowHeight);
        double newThumbHeight = baseThumbHeight + (extraHeight * 0.6);

        ThumbBgBorder.Height = newThumbHeight;
        if (ThumbSkeletonBorder != null)
        {
            ThumbSkeletonBorder.Height = newThumbHeight;
        }
    }

    private void ToolsCollapsed_Click(object sender, RoutedEventArgs e)
    {
        if (PopupToolsMenu == null || ToolsPopupBorder == null) return;
        PopupToolsMenu.PlacementTarget = (UIElement)sender;

        if (!PopupToolsMenu.IsOpen)
        {
            PopupToolsMenu.IsOpen = true;
            AnimatePopupIn(ToolsPopupBorder);
        }
        else
        {
            AnimatePopupOut(ToolsPopupBorder, () => PopupToolsMenu.IsOpen = false);
        }
    }

    private void EditorFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PopupToolsMenu != null && ToolsPopupBorder != null)
        {
            AnimatePopupOut(ToolsPopupBorder, () =>
            {
                PopupToolsMenu.IsOpen = false;
                Editor_Click(sender, e);
            });
        }
        else Editor_Click(sender, e);
    }

    private void SettingsFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PopupToolsMenu != null && ToolsPopupBorder != null)
        {
            AnimatePopupOut(ToolsPopupBorder, () =>
            {
                PopupToolsMenu.IsOpen = false;
                Settings_Click(sender, e);
            });
        }
        else Settings_Click(sender, e);
    }

    public void OpenBatchQueue(string? url = null, Rect originRect = default)
    {
        if (_batchQueueWin != null && _batchQueueWin.IsLoaded)
        {
            if (_batchQueueWin.WindowState == WindowState.Minimized)
                _batchQueueWin.WindowState = WindowState.Normal;
            _batchQueueWin.Activate();
            _batchQueueWin.Focus();
            if (!string.IsNullOrWhiteSpace(url))
            {
                _batchQueueWin.TxtPlaylistUrl.Text = url;
                _ = _batchQueueWin.FetchPlaylistOrUrlsAsync(url);
            }
            return;
        }

        Rect origin = (!originRect.IsEmpty && originRect.Width > 0 && originRect.Height > 0)
            ? originRect
            : (BtnBatchQueue != null && BtnBatchQueue.IsVisible ? GetBtnRect(BtnBatchQueue) : GetBtnRect(TxtUrl));

        _batchQueueWin = new BatchQueueWindow(_backend, url, origin) { Owner = this };
        _batchQueueWin.Closed += (_, _) =>
        {
            _batchQueueWin = null;
            UpdateHeaderToolButtonsVisibility();
        };
        _batchQueueWin.Show();
        _batchQueueWin.Activate();
        UpdateHeaderToolButtonsVisibility();
    }

    private void BatchQueue_Click(object sender, RoutedEventArgs e)
    {
        Rect origin = sender is UIElement el ? GetBtnRect(el) : (BtnBatchQueue != null && BtnBatchQueue.IsVisible ? GetBtnRect(BtnBatchQueue) : GetBtnRect(TxtUrl));
        OpenBatchQueue(TxtUrl.Text.Trim(), origin);
    }

    private void BatchQueueFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (PopupToolsMenu != null && ToolsPopupBorder != null)
        {
            AnimatePopupOut(ToolsPopupBorder, () =>
            {
                PopupToolsMenu.IsOpen = false;
                BatchQueue_Click(sender, e);
            });
        }
        else BatchQueue_Click(sender, e);
    }

    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc;

    private void RefreshPerformanceStats()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            long ramMb = proc.WorkingSet64 / (1024 * 1024);

            // CPU usage of this process since the previous sample (all cores = 100 %)
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            double cpuPct = 0;
            if (_lastCpuSampleUtc != default)
            {
                double wallMs = (now - _lastCpuSampleUtc).TotalMilliseconds;
                if (wallMs > 0)
                    cpuPct = (cpuTime - _lastCpuTime).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0;
            }
            _lastCpuTime = cpuTime;
            _lastCpuSampleUtc = now;

            if (TxtPerfCpu != null) TxtPerfCpu.Text = $"CPU: {Math.Clamp(cpuPct, 0, 100):F1}%";
            if (TxtPerfRam != null) TxtPerfRam.Text = $"RAM: {ramMb} MB";

            // UI responsiveness: how long a background-priority dispatcher item waits to run
            var latency = Stopwatch.StartNew();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (TxtPerfFps != null) TxtPerfFps.Text = $"UI: {latency.Elapsed.TotalMilliseconds:F1}ms";
            });
        }
        catch { }
    }

    private void HeaderConsole_Click(object sender, RoutedEventArgs e)
    {
        if (BtnHeaderConsole == null) return;

        if (_devConsoleWin != null)
        {
            _devConsoleWin.Activate();
            return;
        }

        Rect originRect = GetBtnRect(BtnHeaderConsole);
        _devConsoleWin = new DevConsoleWindow(_devLogBuffer.ToString(), originRect)
        {
            Owner = this,
            Title = "Console"
        };

        _devConsoleWin.CommandEntered += (cmd) =>
        {
            HandleConsoleCommand(cmd);
        };

        _devConsoleWin.Closed += (_, _) =>
        {
            _devConsoleWin = null;
            RefreshHeaderPills();
        };

        RefreshHeaderPills(); // the button folds away as its window grows out of it

        _devConsoleWin.Show();
    }

    private void HeaderLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastJobLog) || BtnHeaderLog == null) return;

        if (_logViewer != null)
        {
            _logViewer.Activate();
            return;
        }

        Rect originRect = GetBtnRect(BtnHeaderLog);
        _logViewer = new LogViewerWindow(_lastJobLog, originRect, _lastJobHash) { Owner = this };
        _logViewer.Closed += (_, _) =>
        {
            _logViewer = null;
            RefreshHeaderPills();
        };

        RefreshHeaderPills(); // the button folds away as its window grows out of it

        _logViewer.Show();
    }

    private void HandleConsoleCommand(string cmd)
    {
        var trimCmd = cmd.Trim();
        var lower = trimCmd.ToLowerInvariant();
        
        if (lower == "help")
        {
            DevLog("Available commands:");
            DevLog("  help                 - Show this help list");
            DevLog("  clear / cls          - Clear the console window");
            DevLog("  status               - Show backend connection status");
            DevLog("  ping                 - Ping backend and measure latency");
            DevLog("  restart              - Restart the Python backend service");
            DevLog("  config               - Display current settings config");
            DevLog("  python               - Check Python installation path & version");
            DevLog("  ffmpeg               - Check FFmpeg installation path & version");
            DevLog("  open [downloads|app] - Open downloads or application data folder");
            DevLog("  resize [h]/[w] [h]   - Resize inline console height or detached window size");
            DevLog("  debug help           - Show all developer debug commands & triggers");
            DevLog("  info                 - Show application version information");
            DevLog("  Any other input is sent directly to the backend process stdin.");
            return;
        }

        if (lower == "debug" || lower.StartsWith("debug "))
        {
            var debugArg = lower == "debug" ? "help" : lower.Substring(6).Trim();

            if (debugArg == "help" || debugArg == "list" || debugArg == "?")
            {
                DevLog("=== DEVELOPER DEBUG COMMAND INDEX ===");
                DevLog("  debug help / debug list          - Show all available debug options");
                DevLog("  debug error \"<msg>\"             - Simulate raw error message (triggers ! pill & diagnostic popup)");
                DevLog("  debug error clear               - Clear simulated error diagnostics");
                DevLog("  debug toast \"<msg>\" [type]      - Trigger floating in-app notification toast (info/success/warning/error)");
                DevLog("  debug lang [en|de|es|fr|ru|it]  - Switch app language instantly");
                DevLog("  debug easteregg [on|off|toggle] - Toggle interactive easter egg effects (spider, tree, disco, snow)");
                DevLog("  debug stats                     - Display real-time UI stats (memory MB, threads, pill states)");
                DevLog("  debug window [appearance|settings|editor|log|console|glass] - Open target secondary window");
                DevLog("  debug glass                     - Open the Glass Tuner (live notification glass values)");
                return;
            }

            if (debugArg.StartsWith("error"))
            {
                var rawArg = trimCmd.Length > trimCmd.IndexOf("error", StringComparison.OrdinalIgnoreCase) + 5
                    ? trimCmd.Substring(trimCmd.IndexOf("error", StringComparison.OrdinalIgnoreCase) + 5).Trim()
                    : "";
                if (rawArg.StartsWith("\"") && rawArg.EndsWith("\"") && rawArg.Length >= 2)
                {
                    rawArg = rawArg.Substring(1, rawArg.Length - 2);
                }

                if (string.IsNullOrWhiteSpace(rawArg) || rawArg.Equals("clear", StringComparison.OrdinalIgnoreCase) || rawArg.Equals("none", StringComparison.OrdinalIgnoreCase) || rawArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    ShowErrorDiagnostics(null);
                    DevLog("[debug error] Error diagnostics cleared.");
                }
                else
                {
                    ShowErrorDiagnostics(rawArg);
                    DevLog($"[debug error] Simulated raw error: \"{rawArg}\" -> Main Menu ! Pill updated.");
                }
                return;
            }

            if (debugArg.StartsWith("toast"))
            {
                var toastCmd = trimCmd.Length > trimCmd.IndexOf("toast", StringComparison.OrdinalIgnoreCase) + 5
                    ? trimCmd.Substring(trimCmd.IndexOf("toast", StringComparison.OrdinalIgnoreCase) + 5).Trim()
                    : "";
                string msg = "Test Notification";
                NotificationType type = NotificationType.Info;

                if (!string.IsNullOrWhiteSpace(toastCmd))
                {
                    if (toastCmd.StartsWith("\""))
                    {
                        int lastQuote = toastCmd.LastIndexOf('"');
                        if (lastQuote > 0)
                        {
                            msg = toastCmd.Substring(1, lastQuote - 1);
                            var remaining = toastCmd.Substring(lastQuote + 1).Trim().ToLowerInvariant();
                            if (remaining == "error") type = NotificationType.Error;
                            else if (remaining == "warning") type = NotificationType.Warning;
                            else if (remaining == "info" || remaining == "success") type = NotificationType.Info;
                        }
                        else
                        {
                            msg = toastCmd.Substring(1);
                        }
                    }
                    else
                    {
                        var parts = toastCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        msg = parts[0];
                        if (parts.Length > 1)
                        {
                            var t = parts[1].ToLowerInvariant();
                            if (t == "error") type = NotificationType.Error;
                            else if (t == "warning") type = NotificationType.Warning;
                            else if (t == "info" || t == "success") type = NotificationType.Info;
                        }
                    }
                }

                NotificationWindow.Show(msg, this, type);
                DevLog($"[debug toast] Triggered {type} toast: \"{msg}\"");
                return;
            }

            if (debugArg.StartsWith("lang"))
            {
                var parts = debugArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    var targetLang = parts[1].ToLowerInvariant();
                    SetLanguage(targetLang);
                    DevLog($"[debug lang] Switched language to '{targetLang}'.");
                }
                else
                {
                    DevLog($"[debug lang] Current language: '{_currentLang}'");
                }
                return;
            }

            if (debugArg.StartsWith("easteregg"))
            {
                var parts = debugArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool enable = !_isComicSansActive;
                if (parts.Length > 1)
                {
                    var opt = parts[1].ToLowerInvariant();
                    if (opt == "on" || opt == "true" || opt == "1") enable = true;
                    else if (opt == "off" || opt == "false" || opt == "0") enable = false;
                }
                ToggleComicSansMode(enable);
                DevLog($"[debug easteregg] Easter eggs set to: {(enable ? "ENABLED" : "DISABLED")}");
                return;
            }

            if (debugArg == "stats")
            {
                long memMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                int threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                DevLog($"[debug stats] Memory: {memMb} MB | Active Threads: {threadCount}");
                DevLog($"[debug stats] Lang: {_currentLang} | DevMode: {SettingsService.Current.EnableDeveloperMode} | PerfMode: {SettingsService.Current.EnablePerformanceMode}");
                DevLog($"[debug stats] Error Pill: {(BtnErrorStatus?.Visibility == Visibility.Visible ? "Visible" : "Hidden")}");
                DevLog($"[debug stats] Help Pill: {(BtnHelp?.Visibility == Visibility.Visible ? "Visible" : "Hidden")}");
                DevLog($"[debug stats] Dev Pill Group: {(DevPillGroup?.Visibility == Visibility.Visible ? "Visible" : "Hidden")}");
                DevLog($"[debug stats] Tools Pill Group: {(PillToolsGroup?.Visibility == Visibility.Visible ? "Visible" : "Hidden")}");
                return;
            }

            if (debugArg == "glass")
            {
                DevLog("[glass] Opening Glass Tuner...");
                GlassTunerWindow.ShowTuner(this);
                return;
            }

            if (debugArg.StartsWith("window"))
            {
                var parts = debugArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    var win = parts[1].ToLowerInvariant();
                    if (win == "appearance") OpenSettingsWindow(GetBtnRect(BtnSettings), "appearance");
                    else if (win == "settings") Settings_Click(this, new RoutedEventArgs());
                    else if (win == "editor") Editor_Click(this, new RoutedEventArgs());
                    else if (win == "log") HeaderLog_Click(this, new RoutedEventArgs());
                    else if (win == "console") HeaderConsole_Click(this, new RoutedEventArgs());
                    else if (win == "glass") GlassTunerWindow.ShowTuner(this);
                    else DevLog("Usage: debug window [appearance|settings|editor|log|console|glass]");
                }
                else
                {
                    DevLog("Usage: debug window [appearance|settings|editor|log|console|glass]");
                }
                return;
            }

            DevLog($"Unknown debug command: '{debugArg}'. Type 'debug help' for available debug options.");
            return;
        }

        if (lower.StartsWith("resize"))
        {
            var parts = trimCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && double.TryParse(parts[1], out double w) && double.TryParse(parts[2], out double h))
            {
                if (_devConsoleWin != null)
                {
                    _devConsoleWin.Width = w;
                    _devConsoleWin.Height = h;
                    DevLog($"[resize] Console window resized to {w}x{h}px.");
                }
            }
            else
            {
                DevLog("Usage: resize <width> <height>");
            }
            return;
        }

        switch (lower)
        {
            case "clear":
            case "cls":
                _devLogBuffer.Clear();
                _devConsoleWin?.ClearConsole();
                break;
            case "status":
                DevLog($"[status] Backend Ready: {_backendReady}");
                DevLog($"[status] Backend URL: {SettingsService.Current.BackendUrl}");
                DevLog($"[status] Developer Mode: {(SettingsService.Current.EnableDeveloperMode ? "ENABLED" : "DISABLED")}");
                break;
            case "dev":
            case "devmode":
                DevLog($"[devmode] Developer Mode is {(SettingsService.Current.EnableDeveloperMode ? "ACTIVE (Video Editor & Dev Tools Unlocked)" : "INACTIVE (Video Editor Disabled)")}");
                break;
            case "editor":
                if (SettingsService.Current.EnableDeveloperMode)
                {
                    DevLog("[editor] Launching Video Editor...");
                    Editor_Click(this, new RoutedEventArgs());
                }
                else
                {
                    DevLog("[editor] Video Editor is blocked. Enable Developer Mode in Settings to unlock.");
                    NotificationWindow.Show(L("ErrEditorDisabled"), this, NotificationType.Warning);
                }
                break;
            case "ping":
                DevLog("Pinging backend server...");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await _backend.WaitForHealthAsync(System.Threading.CancellationToken.None, 1);
                    sw.Stop();
                    Dispatcher.Invoke(() => {
                        if (ok) DevLog($"[ping] Pong! Backend responded in {sw.ElapsedMilliseconds} ms.");
                        else DevLog("[ping] Ping failed. Backend is not responding.");
                    });
                });
                break;
            case "restart":
                DevLog("Restarting media engine from console...");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        _backendReady = await EngineProvider.RestartAsync();
                        Dispatcher.Invoke(() => {
                            SetStatus(_backendReady ? ReadyStatusText() : L("StatusBackendDown"), _backendReady);
                            DevLog(_backendReady ? "[restart] Media engine restarted successfully." : "[restart] Media engine restart failed.");
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => {
                            SetStatus($"{L("StatusBackendErrPrefix")} {ex.Message}", false);
                            DevLog($"[restart] Error: {ex.Message}");
                        });
                    }
                });
                break;
            case "config":
                DevLog("Current Configuration Settings:");
                DevLog($"  Backend URL:          {SettingsService.Current.BackendUrl}");
                DevLog($"  Startup Timeout:      {SettingsService.Current.BackendTimeoutSeconds}s");
                DevLog($"  Auto-Paste URL:       {SettingsService.Current.AutoPasteOnFocus}");
                DevLog($"  Default Privacy:      {SettingsService.Current.DefaultPrivacy}");
                DevLog($"  Auto #Shorts Tag:     {SettingsService.Current.AutoAddShortsHashtag}");
                DevLog($"  Bypass Fingerprint:   {SettingsService.Current.DefaultFingerprintEnabled}");
                DevLog($"  Default Save Path:    {SettingsService.Current.DefaultOutputDir}");
                DevLog($"  GPU Acceleration:     {SettingsService.Current.UseGpu}");
                DevLog($"  Always On Top:        {SettingsService.Current.AlwaysOnTop}");
                DevLog($"  Notify On Complete:   {SettingsService.Current.NotifyOnComplete}");
                DevLog($"  Max Concurrent Jobs:  {SettingsService.Current.MaxConcurrentJobs}");
                break;
            case "python":
                DevLog("Checking system Python...");
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var pInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "python",
                            Arguments = "--version",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var p = System.Diagnostics.Process.Start(pInfo);
                        if (p != null)
                        {
                            p.WaitForExit(3000);
                            var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                            Dispatcher.Invoke(() => DevLog($"[python] Version: {output}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => DevLog($"[python] Failed to call 'python --version' on PATH: {ex.Message}"));
                    }
                });
                break;
            case "ffmpeg":
                DevLog("Checking system FFmpeg...");
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var pInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = MediaTools.FindFfmpeg(),
                            Arguments = "-version",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var p = System.Diagnostics.Process.Start(pInfo);
                        if (p != null)
                        {
                            p.WaitForExit(3000);
                            var line = p.StandardOutput.ReadLine();
                            Dispatcher.Invoke(() => DevLog($"[ffmpeg] Version: {(line ?? "Unknown").Trim()}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => DevLog($"[ffmpeg] Failed to call 'ffmpeg -version' on PATH: {ex.Message}"));
                    }
                });
                break;
            case "info":
                DevLog("VideoConverter developer console");
                DevLog($"App version: {BuildInfoService.BuildName} – build {BuildInfoService.BuildNumber}");
                DevLog($"Language: {SettingsService.Current.Language}");
                break;
            case "open":
            case "open downloads":
            case "open appdata":
            case "open app":
                if (lower == "open")
                {
                    DevLog("Open command targets: downloads, appdata");
                }
                else if (lower == "open downloads")
                {
                    var path = SettingsService.Current.DefaultOutputDir;
                    if (string.IsNullOrEmpty(path))
                        path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    DevLog($"Opening downloads folder: {path}");
                    if (System.IO.Directory.Exists(path))
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
                    else
                        DevLog("[open] Directory does not exist.");
                }
                else
                {
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoConverter");
                    DevLog($"Opening app data folder: {path}");
                    if (System.IO.Directory.Exists(path))
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
                    else
                        DevLog("[open] Directory does not exist.");
                }
                break;
            default:
                EngineProvider.SendInput(trimCmd);
                DevLog($"[sent to backend] {trimCmd}");
                break;
        }
    }

    private string _rawErrorDetails = string.Empty;

    public void ShowErrorDiagnostics(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            SetStatus("Ready", true);
        }
        else
        {
            SetStatus(rawError, false);
        }
    }

    private void SetStatus(string text, bool ok)
    {
        DevLog($"Status change: {text} (ok={ok})");
        if (ok)
        {
            _rawErrorDetails = string.Empty;
            RefreshHeaderPills();
            if (ErrorPopup != null && ErrorPopup.IsOpen) ErrorPopup.IsOpen = false;
        }
        else
        {
            _rawErrorDetails = text ?? string.Empty;
            DevLog($"[error diagnostics] Raw: {_rawErrorDetails}");

            ShowErrorAdvice(ErrorAdvisor.Analyze(_rawErrorDetails, TxtUrl?.Text, _backendReady));

            RefreshHeaderPills();

            if (SettingsService.Current.AutoOpenConsoleOnError)
            {
                Dispatcher.InvokeAsync(() => HeaderConsole_Click(this, new RoutedEventArgs()));
            }
        }
    }

    private ErrorAdvice? _errorAdvice;
    private DateTime _errorTime;

    private sealed record FixStep(int Number, string Text);

    /// <summary>Fills the error window: title, what happened, numbered fixes, the raw message and the action.</summary>
    private void ShowErrorAdvice(ErrorAdvice advice)
    {
        _errorAdvice = advice;
        _errorTime = DateTime.Now;

        TxtErrorTitle.Text = string.IsNullOrEmpty(advice.Title) ? L("ErrPopupTitle") : advice.Title;
        TxtErrorMeta.Text = $"{EngineProvider.DisplayName(EngineProvider.ActiveKey)} · {_errorTime:HH:mm:ss}";
        TxtPopupErrorDetails.Text = advice.Summary;
        ErrorFixSteps.ItemsSource = advice.Steps.Select((t, i) => new FixStep(i + 1, t)).ToList();
        TxtErrorRaw.Text = ErrorAdvisor.StripPrefixes(advice.RawError);

        (string? text, Wpf.Ui.Controls.SymbolRegular icon) action = advice.Action switch
        {
            ErrorAction.RestartEngine => (L("ErrPopupRestart"), Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24),
            ErrorAction.OpenAdvancedSettings => (L("ErrPopupOpenSettings"), Wpf.Ui.Controls.SymbolRegular.Settings24),
            ErrorAction.OpenDownloadSettings => (L("ErrPopupOpenSettings"), Wpf.Ui.Controls.SymbolRegular.FolderOpen24),
            ErrorAction.InstallFfmpeg => (L("ErrPopupInstallFfmpeg"), Wpf.Ui.Controls.SymbolRegular.ArrowDownload24),
            ErrorAction.OpenCloudSetup => (L("ErrPopupCloudSetup"), Wpf.Ui.Controls.SymbolRegular.Cloud24),
            _ => (null, Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24),
        };
        BtnErrorAction.Visibility = action.text == null ? Visibility.Collapsed : Visibility.Visible;
        TxtErrorAction.Text = action.text ?? "";
        IconErrorAction.Symbol = action.icon;
        BtnErrorAction.ToolTip = action.text;
    }

    /// <summary>Opens the error window under the header pill (used when a job or call fails).</summary>
    private void OpenErrorPopup()
    {
        if (ErrorPopup == null || ErrorPopup.IsOpen || _isClosingErrorPopup || string.IsNullOrEmpty(_rawErrorDetails)) return;
        // The pop-up is placed under the pill, so it must be laid out (it folds away once the pop-up is open).
        PillMotion.SetShown(PillHelpGroup, true, animate: false);
        PillMotion.SetShown(BtnErrorStatus, true, animate: false);
        PillHelpGroup?.UpdateLayout();
        ErrorPopup.PlacementTarget = PillHelpGroup != null ? PillHelpGroup : this;
        ErrorPopup.IsOpen = true;
        AnimateErrorPopupIn(ErrorPopupBorder);
        RefreshHeaderPills();
    }

    /// <summary>
    /// Shows an error in the header pill. The error window opens right away if that is turned on (Settings → General);
    /// otherwise a toast names the problem and clicking it opens the window.
    /// </summary>
    private void ReportError(string rawError)
    {
        SetStatus(rawError, false);
        if (SettingsService.Current.AutoOpenErrorWindow)
        {
            OpenErrorPopup();
            return;
        }
        var title = string.IsNullOrEmpty(_errorAdvice?.Title) ? L("ErrPopupTitle") : _errorAdvice!.Title;
        NotificationWindow.Show($"{title} – {L("ErrToastDetails")}", this, NotificationType.Error, OpenErrorPopup, L("ErrToastDetails"));
    }

    private void CloseErrorPopup_Click(object sender, RoutedEventArgs e) => CloseErrorPopupAnimated();

    private async void ErrorAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _errorAdvice?.Action ?? ErrorAction.None;
        if (action == ErrorAction.RestartEngine)
        {
            RestartBackend_Click(sender, e);
            return;
        }

        var origin = GetBtnRect((UIElement)sender);
        CloseErrorPopupAnimated();
        switch (action)
        {
            case ErrorAction.OpenAdvancedSettings:
                OpenSettingsWindow(origin, "advanced");
                break;
            case ErrorAction.OpenDownloadSettings:
                OpenSettingsWindow(origin, "download");
                break;
            case ErrorAction.InstallFfmpeg:
                if (await FFmpegInstallerService.ShowInstallerDialogAsync(this))
                    NotificationWindow.Show(L("NotifyFfmpegInstalled"), this, NotificationType.Info);
                break;
            case ErrorAction.OpenCloudSetup:
                BtnCloudConfig_Click(sender, e);
                break;
        }
    }

    private void ErrorPopup_Closed(object? sender, EventArgs e)
    {
        RefreshHeaderPills();
    }

    private bool _isClosingErrorPopup;
    private DateTime _errorPopupClosedTime = DateTime.MinValue;

    private void ErrorStatus_Click(object s, RoutedEventArgs e)
    {
        if (ErrorPopup == null) return;
        if ((DateTime.UtcNow - _errorPopupClosedTime).TotalMilliseconds < 250) return;

        ErrorPopup.PlacementTarget = PillHelpGroup != null ? PillHelpGroup : (UIElement)s;
        if (!ErrorPopup.IsOpen && !_isClosingErrorPopup)
        {
            ErrorPopup.IsOpen = true;
            AnimateErrorPopupIn(ErrorPopupBorder);
            RefreshHeaderPills();
        }
        else
        {
            CloseErrorPopupAnimated();
        }
    }

    private void CloseErrorPopupAnimated()
    {
        if (_isClosingErrorPopup || ErrorPopup == null || !ErrorPopup.IsOpen) return;
        _isClosingErrorPopup = true;
        _errorPopupClosedTime = DateTime.UtcNow;
        AnimateErrorPopupOut(ErrorPopupBorder, () =>
        {
            ErrorPopup.IsOpen = false;
            _isClosingErrorPopup = false;
            RefreshHeaderPills();
        });
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        CheckClosePopupsOnOutsideClick(e);
    }

    private void CheckClosePopupsOnOutsideClick(MouseButtonEventArgs e)
    {
        if (HelpPopup != null && HelpPopup.IsOpen && !_isClosingHelpPopup)
        {
            if (!IsMouseInsideBorder(HelpPopupBorder, e))
            {
                CloseHelpPopupAnimated();
            }
        }

        if (LangPopup != null && LangPopup.IsOpen && !_isClosingLangPopup)
        {
            if (!IsMouseInsideBorder(LangPopupBorder, e))
            {
                CloseLangPopupAnimated();
            }
        }

        if (ErrorPopup != null && ErrorPopup.IsOpen && !_isClosingErrorPopup)
        {
            if (!IsMouseInsideBorder(ErrorPopupBorder, e))
            {
                CloseErrorPopupAnimated();
            }
        }
    }

    private static bool IsMouseInsideBorder(Border border, MouseButtonEventArgs e)
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

    private void CloseAllPopupsAnimated()
    {
        if (HelpPopup != null && HelpPopup.IsOpen) CloseHelpPopupAnimated();
        if (LangPopup != null && LangPopup.IsOpen) CloseLangPopupAnimated();
        if (ErrorPopup != null && ErrorPopup.IsOpen) CloseErrorPopupAnimated();
    }

    private void AnimateErrorPopupIn(Border border)
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

        // Animate Opacity
        border.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = smooth });

        // Animate Scale
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });

        // Animate Y position (slide down slightly)
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(420))
            { EasingFunction = spring });

        // Morph corner radius to normal rounded corner (16) using a bouncy spring!
        FluidMotion.AnimateCornerRadius(border, 16, TimeSpan.FromMilliseconds(550), bouncy);
    }

    private void AnimateErrorPopupOut(Border border, Action onDone)
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

        // Animate corner radius back to bubbly state (55) quickly!
        FluidMotion.AnimateCornerRadius(border, 55, TimeSpan.FromMilliseconds(200), ease);

        var opAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        { EasingFunction = ease };
        opAnim.Completed += (_, _) => onDone();

        border.BeginAnimation(UIElement.OpacityProperty, opAnim);
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(200))
            { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(200))
            { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(200))
            { EasingFunction = ease });
    }

    private void CopyErrorDetails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var advice = _errorAdvice;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{TxtErrorTitle.Text}");
            sb.AppendLine(TxtPopupErrorDetails.Text);
            if (advice != null && advice.Steps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L("ErrPopupFix"));
                for (int i = 0; i < advice.Steps.Count; i++)
                    sb.AppendLine($"  {i + 1}. {advice.Steps[i]}");
            }
            sb.AppendLine();
            sb.AppendLine($"--- {L("ErrPopupRaw")} ---");
            sb.AppendLine(_rawErrorDetails);
            sb.AppendLine();
            sb.AppendLine($"Engine: {BuildInfoService.ExecutionEngine} · Build: {BuildInfoService.BuildNumber} · {_errorTime:yyyy-MM-dd HH:mm:ss} · Category: {advice?.Id ?? "-"}");
            var textToCopy = sb.ToString();
            Clipboard.SetText(textToCopy);
            NotificationWindow.Show(L("ErrPopupCopySuccess"), this, NotificationType.Info);
        }
        catch (Exception ex)
        {
            DevLog($"Failed to copy error details: {ex.Message}");
        }
    }

    private void ShowLogs_Click(object sender, RoutedEventArgs e)
    {
        AnimateErrorPopupOut(ErrorPopupBorder, () => ErrorPopup.IsOpen = false);
        HeaderConsole_Click(sender, e);
    }

    private void RestartBackend_Click(object sender, RoutedEventArgs e)
    {
        AnimateErrorPopupOut(ErrorPopupBorder, () => ErrorPopup.IsOpen = false);
        NotificationWindow.Show(L("ErrPopupRestarting"), this, NotificationType.Info);

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                DevLog("Restarting media engine...");
                _backendReady = await EngineProvider.RestartAsync();
                DevLog(_backendReady ? "Media engine restart: OK" : "Media engine restart: FAILED");
                
                Dispatcher.Invoke(() =>
                {
                    SetStatus(_backendReady ? ReadyStatusText() : L("StatusBackendDown"), _backendReady);
                    if (_backendReady)
                    {
                        NotificationWindow.Show(_currentLang == "de" ? "Media-Engine erfolgreich neu gestartet!" : "Media engine restarted successfully!", this, NotificationType.Info);
                    }
                    else
                    {
                        NotificationWindow.Show(_currentLang == "de" ? "Engine-Start fehlgeschlagen!" : "Engine failed to start!", this, NotificationType.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus($"{L("StatusBackendErrPrefix")} {ex.Message}", false));
            }
        });
    }

    private void Warn(string msg)
        => NotificationWindow.Show(msg, this, NotificationType.Error);

    private static Rect GetBtnRect(UIElement el)
    {
        var pos = el.PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(el);
        if (source?.CompositionTarget != null)
            pos = source.CompositionTarget.TransformFromDevice.Transform(pos);
        var sz = el.RenderSize;
        return new Rect(pos.X, pos.Y, sz.Width, sz.Height);
    }

    // ════════════════════════════════════════════════════════════
    //  DRAG AND DROP
    // ════════════════════════════════════════════════════════════
    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (_isLoadingFromDrag) return;

        if (DropZoneWindow.IsLinkDrag(e))
        {
            DropZoneWindow.ShowZone(this, HandleDroppedUrl);
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (_isLoadingFromDrag) return;

        if (DropZoneWindow.IsLinkDrag(e))
        {
            if (!DropZoneWindow.IsActive)
            {
                DropZoneWindow.ShowZone(this, HandleDroppedUrl);
            }
            else
            {
                DropZoneWindow.RefreshTimerStatic();
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void HandleDroppedUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || _isLoadingFromDrag) return;

        TxtUrl.Text = url;
        _isLoadingFromDrag = true;

        DropZoneWindow.SetLoadingStatic(L("StatusLoading"));

        await System.Threading.Tasks.Task.Delay(100);

        try
        {
            await FetchMetadataAsync(url);
        }
        finally
        {
            _isLoadingFromDrag = false;
            var targetRect = GetBtnRect(TxtUrl);
            DropZoneWindow.MorphAwayStatic(targetRect);
        }
    }

    public async Task RunFfmpegExportJobAsync(string output, string args, string? srtPath, double trimDurationSecs)
    {
        var cts = BeginJobUi();
        _progressWin!.UpdateProgress(0, "Exportieren…", "Starte ffmpeg…");
        _progressWin.AppendLog("Exportiere Video mit ffmpeg…");

        UpdateMiniProgress(0, "Exportieren…");

        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = MediaTools.FindFfmpeg(),
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                
                using var proc = Process.Start(psi);
                if (proc == null) throw new Exception("ffmpeg konnte nicht gestartet werden.");

                using (var registration = cts.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch {} }))
                {
                    string? line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                    {
                        var logLine = line;
                        var match = FfmpegTimeRegex.Match(logLine);
                        if (match.Success)
                        {
                            int hrs = int.Parse(match.Groups[1].Value);
                            int mins = int.Parse(match.Groups[2].Value);
                            int secs = int.Parse(match.Groups[3].Value);
                            double currentSecs = hrs * 3600 + mins * 60 + secs;
                            
                            int progress = 0;
                            if (trimDurationSecs > 0)
                            {
                                progress = (int)Math.Clamp((currentSecs / trimDurationSecs) * 100, 0, 99);
                            }
                            
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                            {
                                if (_progressWin != null)
                                {
                                    _progressWin.AppendLog(logLine);
                                    _progressWin.UpdateProgress(progress, "Exportieren…", $"{progress}% abgeschlossen");
                                    UpdateMiniProgress(progress, "Exportieren…");
                                }
                            });
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                            {
                                _progressWin?.AppendLog(logLine);
                            });
                        }
                    }

                    proc.WaitForExit();
                    if (cts.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cts.Token);
                    }
                    if (proc.ExitCode != 0)
                    {
                        throw new Exception($"ffmpeg beendet mit Code {proc.ExitCode}");
                    }
                }
            });

            _progressWin?.AppendLog("[OK] " + L("EditorExportDone"));
            SetStatus(L("EditorExportDone"), true);
            _progressWin?.MarkDone(true, System.IO.Path.GetDirectoryName(output));
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrEmpty(output) && System.IO.File.Exists(output))
                try { System.IO.File.Delete(output); } catch { }
            SetStatus(L("StatusCancelled"), false);
            _progressWin?.MarkDone(false);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(output) && System.IO.File.Exists(output))
                try { System.IO.File.Delete(output); } catch { }
            SetStatus($"{L("StatusErrPrefix")} {ex.Message}", false);
            _progressWin?.MarkDone(false);
        }
        finally
        {
            if (srtPath != null && System.IO.File.Exists(srtPath))
                try { System.IO.File.Delete(srtPath); } catch { }

            BtnFetch.IsEnabled = true;
            ResetStartContainerToIdle();
        }
    }
}
