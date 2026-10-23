using VideoConverterUI.Animations;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoConverterUI;

public partial class ProgressWindow : Window
{
    private readonly CancellationTokenSource _cts;
    private readonly Rect _originRect;
    private bool _done;
    private bool _isAnimatingClose;
    private DispatcherTimer? _countdownTimer;
    private int _countdownRemaining;
    private string? _folderPath;
    private DateTime? _jobStartTime;
    private string _lastPhaseKey = string.Empty;
    private bool _isPhaseAnimating;
    private (double pct, string message, string detail, int? eta, string? speed) _pending;
    private double? _smoothedEta;
    private const double EtaAlpha = 0.15;
    private const int MaxLogChars = 200_000;

    private bool _hasNotifiedCompletion;
    private int _logLength;
    public string LogContent => TxtConsole.Text;

    public event EventHandler? OnHiddenInBackground;

    public ProgressWindow(CancellationTokenSource cts, Rect originRect)
    {
        InitializeComponent();
        _cts = cts;
        _originRect = originRect;
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
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            if (RootBorder != null)
            {
                RootBorder.CornerRadius = new CornerRadius(0);
                RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgDeep");
            }
        }

        Services.SettingsService.SettingsChanged += OnSettingsChanged;
        Services.ThemeService.ThemeApplied += OnThemeApplied;
        Closed += (_, _) =>
        {
            Services.SettingsService.SettingsChanged -= OnSettingsChanged;
            Services.ThemeService.ThemeApplied -= OnThemeApplied;
        };
        Services.SettingsService.ApplyResizeGripVisibility(this);
        if (RootBorder != null)
            FluidMotion.MorphOpen(RootBorder, WinScale, WinTranslate, _originRect, this);
        UpdateGlowPulse();
    }

    private void OnThemeApplied() => UpdateGlowPulse();

    private void UpdateGlowPulse()
    {
        if (ProgressFill == null) return;
        if (!_done && Services.ThemeService.Current.EnableProgressPulse)
        {
            Color glowColor = Color.FromArgb(255, 255, 255, 255);
            if (TryFindResource("Accent") is SolidColorBrush ab) glowColor = ab.Color;
            FluidMotion.StartProgressGlowPulse(ProgressFill, glowColor, minBlur: 4, maxBlur: 14, minOpacity: 0.20, maxOpacity: 0.70);
        }
        else
        {
            FluidMotion.StopProgressGlowPulse(ProgressFill);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Services.SettingsService.ApplyResizeGripVisibility(this);

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    public void UpdateProgress(double pct, string message, string detail, int? eta = null, string? speed = null)
    {
        if (_done) return;

        var phaseKey = GetPhaseKey(message);
        var isPhaseChange = !_isPhaseAnimating
                            && _lastPhaseKey.Length > 0
                            && phaseKey.Length > 0
                            && phaseKey != _lastPhaseKey;

        if (phaseKey.Length > 0 && phaseKey != _lastPhaseKey)
        {
            _lastPhaseKey = phaseKey;
            _smoothedEta = null;
            _jobStartTime = DateTime.UtcNow;
        }

        if (_jobStartTime == null && pct > 0)
            _jobStartTime = DateTime.UtcNow;

        int? effectiveEta = eta is > 0 ? eta : null;
        if (effectiveEta == null && _jobStartTime.HasValue && pct >= 3 && pct < 100)
        {
            var elapsed = (DateTime.UtcNow - _jobStartTime.Value).TotalSeconds;
            if (elapsed > 0.8)
            {
                effectiveEta = (int)Math.Max(1, Math.Round(elapsed * (100 - pct) / pct));
            }
        }

        if (effectiveEta is > 0)
        {
            _smoothedEta = _smoothedEta == null
                ? effectiveEta.Value
                : EtaAlpha * effectiveEta.Value + (1.0 - EtaAlpha) * _smoothedEta.Value;
            effectiveEta = (int)Math.Max(1, Math.Round(_smoothedEta.Value));
        }

        _pending = (pct, message, detail, effectiveEta, speed);

        if (isPhaseChange)
            AnimatePhaseTransition();
        else if (!_isPhaseAnimating)
            ApplyProgressValues(pct, message, detail, effectiveEta, speed);
    }

    private static string GetPhaseKey(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        return message.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                      .TrimEnd('.', '…', ',', ':')
                      .ToLowerInvariant();
    }

    private void ApplyProgressValues(double pct, string message, string detail, int? effectiveEta, string? speed = null)
    {
        TxtProgressMsg.Text = message;
        TxtProgressPct.Text = $"{pct:0}%";
        TxtProgressDetail.Text = detail;

        var etaStr = string.Empty;
        if (effectiveEta is > 0 && pct < 100)
        {
            var ts = TimeSpan.FromSeconds(effectiveEta.Value);
            etaStr = ts.TotalHours >= 1
                ? $"ETA {ts:hh\\:mm\\:ss}"
                : $"ETA {ts:mm\\:ss}";
        }

        if (!string.IsNullOrWhiteSpace(speed) && !string.IsNullOrWhiteSpace(etaStr))
        {
            TxtEta.Text = $"{speed}  •  {etaStr}";
        }
        else if (!string.IsNullOrWhiteSpace(speed))
        {
            TxtEta.Text = speed;
        }
        else if (!string.IsNullOrWhiteSpace(etaStr))
        {
            TxtEta.Text = etaStr;
        }
        else
        {
            TxtEta.Text = string.Empty;
        }

        var totalWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 388;
        FluidMotion.AnimateProgressWidth(ProgressFill, totalWidth * pct / 100.0);
        UpdateGlowPulse();
    }

    private void AnimatePhaseTransition()
    {
        _isPhaseAnimating = true;
        FrameworkElement[] rows = [RowMsgPct, ProgressTrack, RowDetail];
        FluidMotion.PhaseOut(rows, () =>
        {
            ApplyProgressValues(_pending.pct, _pending.message, _pending.detail, _pending.eta, _pending.speed);
            AnimatePhaseIn(rows);
        });
    }

    private void AnimatePhaseIn(FrameworkElement[] rows)
    {
        FluidMotion.PhaseIn(rows, () =>
        {
            _isPhaseAnimating = false;
            ApplyProgressValues(_pending.pct, _pending.message, _pending.detail, _pending.eta, _pending.speed);
        });
    }

    public void MarkDone(bool success, string? folderPath = null)
    {
        _done = true;
        _folderPath = folderPath;
        TxtEta.Text = string.Empty;
        BtnCancel.Content = "Schlie\u00dfen";
        UpdateGlowPulse();

        if (success)
            ApplyProgressValues(100, _pending.message, "", null);

        if (!string.IsNullOrEmpty(_folderPath))
            BtnOpenFolder.Visibility = Visibility.Visible;

        if (success)
        {
            _countdownRemaining = 4;
            TxtCountdown.Text = $"Schließt in {_countdownRemaining} s…";
            TxtCountdown.Visibility = Visibility.Visible;
            StartCountdown();
        }

        if (Services.SettingsService.Current.NotifyOnComplete && !_hasNotifiedCompletion)
        {
            _hasNotifiedCompletion = true;
            try
            {
                bool playSound = Services.SettingsService.Current.EnableNotificationSound;
                if (success)
                {
                    if (playSound) try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    NotificationWindow.Show(L("NotificationJobComplete"), Owner ?? this, NotificationType.Info);
                }
                else
                {
                    if (playSound) try { System.Media.SystemSounds.Hand.Play(); } catch { }
                    NotificationWindow.Show(L("NotificationJobFailed"), Owner ?? this, NotificationType.Error);
                }
            }
            catch { }
        }
    }

    private void StartCountdown()
    {
        _countdownTimer?.Stop();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            _countdownRemaining--;
            if (_countdownRemaining <= 0)
            {
                _countdownTimer.Stop();
                CloseWithAnimation();
            }
            else
            {
                TxtCountdown.Text = $"Schlie\u00dft in {_countdownRemaining} s\u2026";
            }
        };
        _countdownTimer.Start();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        if (!_done) _cts.Cancel();
        CloseWithAnimation();
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_folderPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _folderPath,
                    UseShellExecute = true,
                });
            }
            catch { /* ignore if folder no longer exists */ }
        }
    }

    public void AppendLog(string message)
    {
        // TextBox.Text rebuilds the whole string on every read, so the length is tracked here
        // instead – otherwise each appended line gets slower as the log grows.
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
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

    public void ToggleLog()
    {
        var opening = TxtConsole.Visibility != Visibility.Visible;
        BtnToggleLog.Content = opening ? "\u25b4 Log" : "\u25be Log";

        if (opening)
        {
            FluidMotion.ExpandElement(TxtConsole, 140);
            _countdownTimer?.Stop();
        }
        else
        {
            var fromHeight = Math.Min(TxtConsole.ActualHeight, 140);
            FluidMotion.CollapseElement(TxtConsole, fromHeight, () =>
            {
                if (_done && _countdownRemaining > 0)
                    StartCountdown();
            });
        }
    }

    public void EnsureLogOpen()
    {
        if (TxtConsole.Visibility != Visibility.Visible)
        {
            ToggleLog();
        }
    }

    private void ToggleLog_Click(object s, RoutedEventArgs e) => ToggleLog();

    public void ShowWithAnimation()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _isAnimatingClose = false;
        FluidMotion.MorphOpen(RootBorder, WinScale, WinTranslate, _originRect, this);
    }

    private void CloseWindow_Click(object s, RoutedEventArgs e)
    {
        CloseWithAnimation(realClose: false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            _countdownRemaining = 0;
            _countdownTimer?.Stop();
            base.OnClosing(e);
            return;
        }

        if (!_isAnimatingClose) 
        { 
            e.Cancel = true; 
            CloseWithAnimation(realClose: _done); 
        }
        base.OnClosing(e);
    }

    private void CloseWithAnimation() => CloseWithAnimation(realClose: true);

    private void CloseWithAnimation(bool realClose)
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;
        _countdownRemaining = 0;
        _countdownTimer?.Stop();
        FluidMotion.MorphClose(RootBorder, WinScale, WinTranslate, this,
            () =>
            {
                if (realClose)
                {
                    Close();
                }
                else
                {
                    _isAnimatingClose = false;
                    Hide();
                    OnHiddenInBackground?.Invoke(this, EventArgs.Empty);
                }
            });
    }

    private static string L(string key)
        => Application.Current.Resources[key] as string ?? key;
}
