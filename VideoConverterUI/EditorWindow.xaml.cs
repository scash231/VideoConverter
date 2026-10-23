using VideoConverterUI.Animations;
using VideoConverterUI.Services;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoConverterUI;

public partial class EditorWindow : Window
{
    private readonly Rect _originRect;
    private bool _isAnimatingClose;
    private string? _filePath;
    private TimeSpan _duration;
    private TimeSpan _trimStart;
    private TimeSpan _trimEnd;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _isDraggingSlider;
    private string? _srtContent;
    private IEngineService _backend => EngineProvider.Current;
    private readonly DispatcherTimer _positionTimer;
    private int _rotationAngle = 0;
    private bool _flipHorizontal = false;
    private DateTime _lastSeekTime = DateTime.MinValue;
    private string _selectedCropRatio = "Original";
    private double _selectedSpeed = 1.0;
    private double _volumeFactor = 1.0;
    private int _currentTabIndex = 0;

    // Windows can't decode every file (e.g. VP9/Opus WebM without the Store codecs). The player then shows an H.264
    // copy of it (_previewPath), while subtitles and export keep using _filePath.
    private string? _previewPath;
    private bool _playingPreviewCopy;
    private CancellationTokenSource? _previewCts;
    private int _loadId;   // bumped per LoadVideo and on close, so a preview copy for an earlier file is dropped
    private readonly DispatcherTimer _openTimeout;   // the player sometimes neither opens nor fails

    public EditorWindow(Rect originRect, string? initialFile = null)
    {
        InitializeComponent();
        _originRect = originRect;
        _filePath = initialFile;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        _openTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _openTimeout.Tick += (_, _) => OnOpenFailed(null);

        Loaded += OnLoaded;
    }

    private void WindowCornerGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        SettingsService.StartWindowResizeBottomRight(this, SnapWindowToVideo);
        e.Handled = true; // no drag-move of the window underneath
    }

    private void WindowCornerGrip_MouseEnter(object sender, MouseEventArgs e)
        => SettingsService.HandleGripHover(sender, true);

    private void WindowCornerGrip_MouseLeave(object sender, MouseEventArgs e)
        => SettingsService.HandleGripHover(sender, false);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsService.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) => SettingsService.SettingsChanged -= OnSettingsChanged;
        Closed += (_, _) =>
        {
            _openTimeout.Stop();
            _filePath = null;
            _loadId++;
            VideoPlayer.Close();
            DiscardPreviewCopy();
        };
        SettingsService.ApplyResizeGripVisibility(this);
        FluidMotion.MorphOpen(RootBorder, WindowScale, WindowTranslate, _originRect, this);

        if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
            LoadVideo(_filePath);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => SettingsService.ApplyResizeGripVisibility(this);

    // ════════════════════════════════════════════════════════════
    //  TITLEBAR
    // ════════════════════════════════════════════════════════════
    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    // Space plays/pauses (unless typing), Escape closes
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWithAnimation();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && BtnPlay.IsEnabled && Keyboard.FocusedElement is not TextBox)
        {
            Play_Click(this, e);
            e.Handled = true;
        }
    }

    private void Close_Click(object s, RoutedEventArgs e)
        => CloseWithAnimation();

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
        _positionTimer.Stop();
        VideoPlayer.Stop();
        VideoPlayer.Close();
        FluidMotion.MorphClose(RootBorder, WindowScale, WindowTranslate, this, Close);
    }

    // ════════════════════════════════════════════════════════════
    //  FILE LOADING
    // ════════════════════════════════════════════════════════════
    private void BrowseVideo_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L("EditorBrowseTitle"),
            Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadVideo(dlg.FileName);
    }


    public void LoadVideo(string path)
    {
        _filePath = path;
        _loadId++;
        TxtFileName.Text = Path.GetFileName(path);
        TxtFileName.ToolTip = path;

        _rotationAngle = 0;
        _flipHorizontal = false;
        BtnFlipH.IsChecked = false;
        UpdatePreviewTransform();

        _positionTimer.Stop();
        SetEditingEnabled(false);
        ShowPreviewMessage(L("EditorLoadingVideo"), null, showOpenButton: false);
        DiscardPreviewCopy();
        OpenInPlayer(path, previewCopy: false);
    }

    private void OpenInPlayer(string source, bool previewCopy)
    {
        _playingPreviewCopy = previewCopy;
        VideoPlayer.Source = new Uri(source, UriKind.Absolute);
        VideoPlayer.Play();
        VideoPlayer.Pause();
        SetPlaying(false);
        _openTimeout.Stop();
        _openTimeout.Start();
    }

    /// <summary>The text in the middle of the preview while there is no picture (loading, preparing, failed).</summary>
    private void ShowPreviewMessage(string text, string? detail, bool showOpenButton)
    {
        TxtNoVideo.Text = text;
        TxtLoadDetail.Text = detail ?? "";
        TxtLoadDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        BtnEmptyOpen.Visibility = showOpenButton ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void VideoPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        => OnOpenFailed(e.ErrorException?.Message);

    /// <summary>
    /// The player couldn't open the file (no decoder in Windows, or no answer within the timeout): play an H.264 copy
    /// instead. If the copy fails too, show why and offer to open another video.
    /// </summary>
    private async void OnOpenFailed(string? reason)
    {
        _openTimeout.Stop();
        if (_filePath is not { } source || _isAnimatingClose) return;
        if (_playingPreviewCopy)
        {
            ShowPreviewMessage(L("EditorOpenFailed"), reason, showOpenButton: true);
            return;
        }
        if (_previewCts != null) return;   // already converting (the timeout came first)

        int loadId = _loadId;
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        ShowPreviewMessage(string.Format(L("EditorPreparingPreview"), 0), L("EditorPreviewHint"), showOpenButton: false);
        try
        {
            var copy = await BuildPreviewCopyAsync(source, percent => Dispatcher.BeginInvoke(() =>
            {
                if (loadId == _loadId) TxtNoVideo.Text = string.Format(L("EditorPreparingPreview"), percent);
            }), cts.Token);

            if (loadId != _loadId)
            {
                _ = DeleteWhenReleasedAsync(copy);
                return;
            }
            _previewPath = copy;
            OpenInPlayer(copy, previewCopy: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (loadId == _loadId)
                ShowPreviewMessage(L("EditorOpenFailed"), ex.Message, showOpenButton: true);
        }
        finally
        {
            if (_previewCts == cts) _previewCts = null;
            cts.Dispose();
        }
    }

    private static readonly Regex FfmpegDurationRegex = new(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex FfmpegTimeRegex = new(@"time=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// Converts the video into an H.264/AAC MP4 in %TEMP% that the player can always show. It keeps the pixel size
    /// (crop and the window fit read the player's video size) and gets a keyframe every 30 frames for smooth scrubbing.
    /// Reports 0–100 from ffmpeg's progress; throws with ffmpeg's last error line when it fails.
    /// </summary>
    private static async Task<string> BuildPreviewCopyAsync(string source, Action<int> onProgress, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), $"vc_preview_{Guid.NewGuid():N}.mp4");
        var psi = new ProcessStartInfo
        {
            FileName = MediaTools.FindFfmpeg(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-i", source,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-pix_fmt", "yuv420p",   // yuv420p needs an even size
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-g", "30",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart", output,
        })
            psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            double totalSecs = 0;
            int lastPercent = -1;
            string lastError = "";
            string? line;
            // ffmpeg redraws its "time=" line with \r, which ReadLine also treats as a line end
            while ((line = await proc.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
            {
                if (line.Contains("time="))
                {
                    if (totalSecs <= 0 || FfmpegTimeRegex.Match(line) is not { Success: true } t) continue;
                    int percent = (int)Math.Clamp(ToSeconds(t) / totalSecs * 100, 0, 99);
                    if (percent != lastPercent) onProgress(lastPercent = percent);
                }
                else if (totalSecs <= 0 && FfmpegDurationRegex.Match(line) is { Success: true } d)
                    totalSecs = ToSeconds(d);
                else if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("Conversion failed"))
                    lastError = line.Trim();
            }
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(lastError) ? $"ffmpeg exit code {proc.ExitCode}" : lastError);
            return output;
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }

        static double ToSeconds(Match m)
            => int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
               + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Stops a running preview conversion and deletes the current preview copy.</summary>
    private void DiscardPreviewCopy()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        if (_previewPath is { } old) _ = DeleteWhenReleasedAsync(old);
        _previewPath = null;
    }

    // The player lets go of a file a moment after its source changes or it closes.
    private static async Task DeleteWhenReleasedAsync(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(1000);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  MEDIA EVENTS
    // ════════════════════════════════════════════════════════════
    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _openTimeout.Stop();
        if (!VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            OnOpenFailed(null);   // nothing to trim against (e.g. a WebM without a duration); the copy has one
            return;
        }
        if (!_playingPreviewCopy) DiscardPreviewCopy();   // opened after all, later than the timeout
        EmptyState.Visibility = Visibility.Collapsed;

        _duration = VideoPlayer.NaturalDuration.TimeSpan;
        _trimStart = TimeSpan.Zero;
        _trimEnd = _duration;

        SliderTimeline.Maximum = _duration.TotalSeconds;
        SliderTimeline.SelectionStart = 0;
        SliderTimeline.SelectionEnd = _duration.TotalSeconds;
        SliderTimeline.IsEnabled = true;
        TxtDuration.Text = FormatTime(_duration);
        TxtTrimStart.Text = FormatTime(_trimStart);
        TxtTrimEnd.Text = FormatTime(_trimEnd);
        UpdateHudTime(TimeSpan.Zero);

        SetEditingEnabled(true);
        UpdateCropOverlayBounds();
        Dispatcher.BeginInvoke(FitWindowToVideo, DispatcherPriority.Loaded);
    }

    /// <summary>Every playback, trim, inspector and export control: on once a video is open, off while one loads.</summary>
    private void SetEditingEnabled(bool on)
    {
        SliderTimeline.IsEnabled = on;
        BtnPlay.IsEnabled = on;
        BtnStop.IsEnabled = on;
        BtnMuteToggle.IsEnabled = on;
        TxtTrimStart.IsEnabled = on;
        TxtTrimEnd.IsEnabled = on;
        BtnSetStart.IsEnabled = on;
        BtnSetEnd.IsEnabled = on;
        BtnResetTrim.IsEnabled = on;
        ChkMuteAudio.IsEnabled = on;
        BtnGenSubs.IsEnabled = on;
        BtnExport.IsEnabled = on;

        BtnRotateCCW.IsEnabled = on;
        BtnRotateCW.IsEnabled = on;
        BtnFlipH.IsEnabled = on;

        RadCropOriginal.IsEnabled = on;
        RadCrop916.IsEnabled = on;
        RadCrop169.IsEnabled = on;
        RadCrop11.IsEnabled = on;

        RadSpeed05.IsEnabled = on;
        RadSpeed10.IsEnabled = on;
        RadSpeed15.IsEnabled = on;
        RadSpeed20.IsEnabled = on;

        SliderVolume.IsEnabled = on;
        CmbResolution.IsEnabled = on;
        CmbFormat.IsEnabled = on;
    }

    // Smallest window that still fits the trim row; tallest it grows to on its own (beyond that it gets narrower).
    private const double MinFitWidth = 640, MinFitHeight = 480, MaxFitHeight = 760;

    /// <summary>
    /// Resizes the window so the preview takes the video's shape (no black bars): first the height to the video at the
    /// preview's current width (growing to at most MaxFitHeight, or the current height if taller), then, for tall
    /// videos, the width down to that height. Stays within the work area and keeps the window centred where it was.
    /// </summary>
    private void FitWindowToVideo()
    {
        double vw = VideoPlayer.NaturalVideoWidth, vh = VideoPlayer.NaturalVideoHeight;
        double cw = PreviewCard.ActualWidth, ch = PreviewCard.ActualHeight;
        if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) return;
        if (_rotationAngle is 90 or 270) (vw, vh) = (vh, vw);
        double aspect = vw / vh;

        var area = SystemParameters.WorkArea;
        double chromeW = ActualWidth - cw, chromeH = ActualHeight - ch;   // everything around the preview

        double maxH = Math.Min(area.Height - 40, Math.Max(ActualHeight, MaxFitHeight));
        double cardH = Math.Clamp(cw / aspect, MinFitHeight - chromeH, Math.Max(MinFitHeight, maxH) - chromeH);
        double cardW = Math.Clamp(Math.Min(cw, cardH * aspect), MinFitWidth - chromeW, Math.Max(MinFitWidth, area.Width - 40) - chromeW);
        double newW = Math.Round(cardW + chromeW), newH = Math.Round(cardH + chromeH);
        if (Math.Abs(newW - ActualWidth) < 2 && Math.Abs(newH - ActualHeight) < 2) return;

        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        Width = newW;
        Height = newH;
        Left = Math.Clamp(cx - newW / 2, area.Left, area.Right - newW);
        Top = Math.Clamp(cy - newH / 2, area.Top, area.Bottom - newH);
    }

    /// <summary>
    /// After a resize with the corner handle: keeps the side that was dragged more and snaps the other one so the
    /// preview has the video's shape again (no bars). Falls back to the other side when that would leave the screen or
    /// go below the minimum size. The top-left corner stays put, as during the drag.
    /// </summary>
    private void SnapWindowToVideo(double widthChange, double heightChange)
    {
        UpdateLayout();
        double vw = VideoPlayer.NaturalVideoWidth, vh = VideoPlayer.NaturalVideoHeight;
        double cw = PreviewCard.ActualWidth, ch = PreviewCard.ActualHeight;
        if (vw <= 0 || vh <= 0 || cw <= 0 || ch <= 0) return;
        if (_rotationAngle is 90 or 270) (vw, vh) = (vh, vw);
        double aspect = vw / vh;

        var area = SystemParameters.WorkArea;
        double chromeW = ActualWidth - cw, chromeH = ActualHeight - ch;
        double minW = Math.Max(MinFitWidth, MinWidth), minH = Math.Max(MinFitHeight, MinHeight);
        double maxW = Math.Max(minW, area.Right - Left), maxH = Math.Max(minH, area.Bottom - Top);

        double newW = ActualWidth, newH = ActualHeight;
        bool keepWidth = Math.Abs(widthChange) >= Math.Abs(heightChange);
        if (keepWidth)
        {
            newH = chromeH + cw / aspect;
            if (newH > maxH || newH < minH)
            {
                newH = Math.Clamp(newH, minH, maxH);
                newW = Math.Clamp(chromeW + (newH - chromeH) * aspect, minW, maxW);
            }
        }
        else
        {
            newW = chromeW + ch * aspect;
            if (newW > maxW || newW < minW)
            {
                newW = Math.Clamp(newW, minW, maxW);
                newH = Math.Clamp(chromeH + (newW - chromeW) / aspect, minH, maxH);
            }
        }
        newW = Math.Round(newW);
        newH = Math.Round(newH);
        if (Math.Abs(newW - ActualWidth) < 2 && Math.Abs(newH - ActualHeight) < 2) return;
        AnimateWindowSize(newW, newH);
    }

    private EventHandler? _sizeTween;

    /// <summary>Glides the window to a new size (the top-left stays put); instant in performance mode.</summary>
    private void AnimateWindowSize(double width, double height)
    {
        if (_sizeTween != null) CompositionTarget.Rendering -= _sizeTween;
        _sizeTween = null;
        if (SettingsService.Current.EnablePerformanceMode)
        {
            Width = width;
            Height = height;
            return;
        }

        double fromW = ActualWidth, fromH = ActualHeight;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const double durationMs = 220;
        _sizeTween = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
            double k = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            Width = Math.Round(fromW + (width - fromW) * k);
            Height = Math.Round(fromH + (height - fromH) * k);
            if (t >= 1 && _sizeTween != null)
            {
                CompositionTarget.Rendering -= _sizeTween;
                _sizeTween = null;
            }
        };
        CompositionTarget.Rendering += _sizeTween;
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        SetPlaying(false);
        _positionTimer.Stop();
        VideoPlayer.Position = _trimStart;
        SliderTimeline.Value = _trimStart.TotalSeconds;
        UpdateHudTime(_trimStart);
    }

    // ════════════════════════════════════════════════════════════
    //  PLAYBACK CONTROLS
    // ════════════════════════════════════════════════════════════
    private void Play_Click(object s, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            VideoPlayer.Pause();
            _positionTimer.Stop();
            SetPlaying(false);
        }
        else
        {
            VideoPlayer.Play();
            _positionTimer.Start();
            SetPlaying(true);
        }
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayIcon.Symbol = playing ? Wpf.Ui.Controls.SymbolRegular.Pause24 : Wpf.Ui.Controls.SymbolRegular.Play24;
    }

    private void UpdateHudTime(TimeSpan pos) => TxtCurrentTime.Text = FormatTime(pos);

    private void Stop_Click(object s, RoutedEventArgs e)
    {
        VideoPlayer.Pause();
        VideoPlayer.Position = _trimStart;
        _positionTimer.Stop();
        SetPlaying(false);
        SliderTimeline.Value = _trimStart.TotalSeconds;
        UpdateHudTime(_trimStart);
    }

    private void MuteToggle_Click(object s, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        VideoPlayer.IsMuted = _isMuted;
        MuteIcon.Symbol = _isMuted ? Wpf.Ui.Controls.SymbolRegular.SpeakerMute24 : Wpf.Ui.Controls.SymbolRegular.Speaker224;
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingSlider) return;
        var pos = VideoPlayer.Position;
        if (pos >= _trimEnd)
        {
            VideoPlayer_MediaEnded(this, new RoutedEventArgs());
            return;
        }
        SliderTimeline.Value = pos.TotalSeconds;
        UpdateHudTime(pos);
    }

    private void SliderTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingSlider)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSeekTime).TotalMilliseconds >= 50)
            {
                _lastSeekTime = now;
                VideoPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
                UpdateHudTime(VideoPlayer.Position);
            }
        }
    }

    private void SliderTimeline_DragStarted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void SliderTimeline_DragCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        VideoPlayer.Position = TimeSpan.FromSeconds(SliderTimeline.Value);
        UpdateHudTime(VideoPlayer.Position);
    }

    // ════════════════════════════════════════════════════════════
    //  TRIM CONTROLS
    // ════════════════════════════════════════════════════════════
    private void SetStart_Click(object s, RoutedEventArgs e)
    {
        _trimStart = VideoPlayer.Position;
        TxtTrimStart.Text = FormatTime(_trimStart);
        SliderTimeline.SelectionStart = _trimStart.TotalSeconds;
    }

    private void SetEnd_Click(object s, RoutedEventArgs e)
    {
        _trimEnd = VideoPlayer.Position;
        TxtTrimEnd.Text = FormatTime(_trimEnd);
        SliderTimeline.SelectionEnd = _trimEnd.TotalSeconds;
    }

    private void ResetTrim_Click(object s, RoutedEventArgs e)
    {
        _trimStart = TimeSpan.Zero;
        _trimEnd = _duration;
        TxtTrimStart.Text = FormatTime(_trimStart);
        TxtTrimEnd.Text = FormatTime(_trimEnd);
        SliderTimeline.SelectionStart = 0;
        SliderTimeline.SelectionEnd = _duration.TotalSeconds;
        
        _rotationAngle = 0;
        _flipHorizontal = false;
        BtnFlipH.IsChecked = false;
        UpdatePreviewTransform();
    }

    // ════════════════════════════════════════════════════════════
    //  SUBTITLES
    // ════════════════════════════════════════════════════════════
    private async void GenSubs_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        BtnGenSubs.IsEnabled = false;
        BtnGenSubs.Content = L("EditorGenSubsRunning");
        PbSubs.Visibility = Visibility.Visible;
        TxtSubStatus.Text = L("EditorSubStatusGenerating");
        TxtSubStatus.Visibility = Visibility.Visible;

        try
        {
            var model = (CmbSubModel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "base";
            var lang = (CmbSubLang.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

            _srtContent = await _backend.GenerateSubtitlesAsync(_filePath, model, lang);

            if (string.IsNullOrWhiteSpace(_srtContent))
            {
                TxtSubStatus.Text = L("EditorSubStatusEmpty");
                return;
            }

            TxtSubPreview.Text = _srtContent;
            TxtSubPreview.Visibility = Visibility.Visible;
            ChkBurnSubs.IsEnabled = true;
            ChkBurnSubs.IsChecked = true;
            TxtSubStatus.Text = L("EditorSubStatusDone");
        }
        catch (Exception ex)
        {
            var advice = ErrorAdvisor.Analyze($"Subtitles error: {ex.Message}", null, true);
            TxtSubStatus.Text = advice.Steps.Count > 0
                ? $"{L("ErrPrefix")} {ex.Message}{Environment.NewLine}→ {advice.Steps[0]}"
                : $"{L("ErrPrefix")} {ex.Message}";
            TxtSubStatus.ToolTip = string.Join(Environment.NewLine, advice.Steps.Select((t, i) => $"{i + 1}. {t}"));
        }
        finally
        {
            PbSubs.Visibility = Visibility.Collapsed;
            BtnGenSubs.IsEnabled = true;
            BtnGenSubs.Content = L("EditorGenSubs");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  EXPORT
    // ════════════════════════════════════════════════════════════
    private async void Export_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        var formatItem = CmbFormat.SelectedItem as ComboBoxItem;
        var formatTag = formatItem?.Tag?.ToString() ?? "mp4";
        
        string filter = "MP4 Video|*.mp4";
        string defaultExt = "mp4";
        if (formatTag == "mkv")
        {
            filter = "MKV Video|*.mkv";
            defaultExt = "mkv";
        }
        else if (formatTag == "webm")
        {
            filter = "WebM Video|*.webm";
            defaultExt = "webm";
        }

        var dlg = new SaveFileDialog
        {
            Title = L("EditorExportTitle"),
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = Path.GetFileNameWithoutExtension(_filePath) + "_edited." + defaultExt
        };
        if (dlg.ShowDialog() != true) return;

        // Prepare subtitle temp file if needed
        string? srtPath = null;
        bool burnSubs = ChkBurnSubs.IsChecked == true && !string.IsNullOrEmpty(_srtContent);
        if (burnSubs)
        {
            srtPath = Path.Combine(Path.GetTempPath(), $"reels_subs_{Guid.NewGuid():N}.srt");
            await File.WriteAllTextAsync(srtPath, TxtSubPreview.Text);
        }

        BtnExport.IsEnabled = false;
        BtnExport.Content = L("EditorExporting");

        try
        {
            // The export runs (and reports progress) in the main window, which always owns the editor.
            var mainWin = Owner as MainWindow ?? Application.Current.MainWindow as MainWindow
                ?? throw new InvalidOperationException("Main window is not available.");

            var hwEncoder = SettingsService.Current.UseGpu && formatTag != "webm"
                ? await MediaTools.GetHardwareEncoderAsync()
                : null;

            var output = dlg.FileName;
            var args = BuildFfmpegArgs(_filePath, output, srtPath, hwEncoder?.Encoder);
            var trimDurationSecs = (_trimEnd - _trimStart).TotalSeconds;

            _ = mainWin.RunFfmpegExportJobAsync(output, args, srtPath, trimDurationSecs);
            // Close EditorWindow since the progress is shown on MainWindow
            Close();
        }
        catch (Exception ex)
        {
            NotificationWindow.Show($"{L("ErrPrefix")} {ex.Message}", this, NotificationType.Error);
            if (srtPath != null && File.Exists(srtPath))
                try { File.Delete(srtPath); } catch { }
            BtnExport.IsEnabled = true;
            BtnExport.Content = L("EditorExport");
        }
    }

    private void RotateCCW_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 270) % 360;
        UpdatePreviewTransform();
    }

    private void RotateCW_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        UpdatePreviewTransform();
    }

    private void FlipH_Click(object sender, RoutedEventArgs e)
    {
        _flipHorizontal = BtnFlipH.IsChecked == true;
        UpdatePreviewTransform();
    }

    private void UpdatePreviewTransform()
    {
        var group = new TransformGroup();
        if (_flipHorizontal)
        {
            group.Children.Add(new ScaleTransform(-1, 1));
        }
        if (_rotationAngle != 0)
        {
            group.Children.Add(new RotateTransform(_rotationAngle));
        }
        VideoPlayer.RenderTransformOrigin = new Point(0.5, 0.5);
        VideoPlayer.RenderTransform = group;
        TxtRotation.Text = $"{_rotationAngle}°";
        UpdateCropOverlayBounds();
    }

    private void InspectorTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb)
        {
            var tabName = rb.Name;
            int newIndex = tabName switch
            {
                "TabVideo" => 0,
                "TabAudio" => 1,
                "TabSubs" => 2,
                "TabExport" => 3,
                _ => 0
            };

            Border? targetBorder = tabName switch
            {
                "TabVideo" => InspectorVideo,
                "TabAudio" => InspectorAudio,
                "TabSubs" => InspectorSubs,
                "TabExport" => InspectorExport,
                _ => null
            };

            if (targetBorder == null) return;

            Border? currentBorder = _currentTabIndex switch
            {
                0 => InspectorVideo,
                1 => InspectorAudio,
                2 => InspectorSubs,
                3 => InspectorExport,
                _ => null
            };

            if (targetBorder == currentBorder || (targetBorder.Visibility == Visibility.Visible && targetBorder.Opacity >= 0.95))
            {
                _currentTabIndex = newIndex;
                return;
            }

            if (!this.IsLoaded)
            {
                if (InspectorVideo != null) InspectorVideo.Visibility = Visibility.Collapsed;
                if (InspectorAudio != null) InspectorAudio.Visibility = Visibility.Collapsed;
                if (InspectorSubs != null) InspectorSubs.Visibility = Visibility.Collapsed;
                if (InspectorExport != null) InspectorExport.Visibility = Visibility.Collapsed;

                targetBorder.Visibility = Visibility.Visible;
                _currentTabIndex = newIndex;
                return;
            }

            // Collapse other panels to prevent rapid clicking from showing multiple overlapping panels
            if (InspectorVideo != null && InspectorVideo != targetBorder && InspectorVideo != currentBorder) InspectorVideo.Visibility = Visibility.Collapsed;
            if (InspectorAudio != null && InspectorAudio != targetBorder && InspectorAudio != currentBorder) InspectorAudio.Visibility = Visibility.Collapsed;
            if (InspectorSubs != null && InspectorSubs != targetBorder && InspectorSubs != currentBorder) InspectorSubs.Visibility = Visibility.Collapsed;
            if (InspectorExport != null && InspectorExport != targetBorder && InspectorExport != currentBorder) InspectorExport.Visibility = Visibility.Collapsed;

            if (currentBorder != null && currentBorder != targetBorder)
            {
                double direction = (newIndex > _currentTabIndex) ? 1.0 : -1.0;
                FluidMotion.LiquidGlassCrossfade(currentBorder, targetBorder, direction);
            }
            else
            {
                targetBorder.Visibility = Visibility.Visible;
            }

            _currentTabIndex = newIndex;
        }
    }

    private void CropRatio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb)
        {
            _selectedCropRatio = rb.Tag?.ToString() ?? "Original";
            UpdateCropOverlayBounds();
        }
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb)
        {
            var speedText = rb.Content.ToString()?.Replace("x", "") ?? "1.0";
            if (double.TryParse(speedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                _selectedSpeed = val;
                VideoPlayer.SpeedRatio = _selectedSpeed;
                TxtSpeedBadge.Text = $"{_selectedSpeed:F1}x";
            }
        }
    }

    private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volumeFactor = e.NewValue;
        if (VideoPlayer != null)
        {
            VideoPlayer.Volume = Math.Min(1.0, _volumeFactor);
        }
        if (TxtVolumeVal != null)
        {
            TxtVolumeVal.Text = $"{(int)(_volumeFactor * 100)}%";
        }
        if (TxtVolumeBadge != null)
        {
            TxtVolumeBadge.Text = $"{(int)(_volumeFactor * 100)}%";
        }
    }

    private void VideoPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCropOverlayBounds();
    }

    private void UpdateCropOverlayBounds()
    {
        if (VideoPlayer == null || CropOverlay == null) return;
        if (!VideoPlayer.NaturalDuration.HasTimeSpan || VideoPlayer.NaturalVideoWidth == 0 || VideoPlayer.NaturalVideoHeight == 0)
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_selectedCropRatio == "Original")
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double natW = VideoPlayer.NaturalVideoWidth;
        double natH = VideoPlayer.NaturalVideoHeight;

        if (_rotationAngle == 90 || _rotationAngle == 270)
        {
            natW = VideoPlayer.NaturalVideoHeight;
            natH = VideoPlayer.NaturalVideoWidth;
        }

        double playerW = VideoPlayer.ActualWidth;
        double playerH = VideoPlayer.ActualHeight;

        if (playerW == 0 || playerH == 0)
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double rVideo = natW / natH;
        double rPlayer = playerW / playerH;

        double renderW, renderH;
        double renderX = 0;
        double renderY = 0;

        if (rVideo > rPlayer)
        {
            renderW = playerW;
            renderH = playerW / rVideo;
            renderY = (playerH - renderH) / 2;
        }
        else
        {
            renderW = playerH * rVideo;
            renderH = playerH;
            renderX = (playerW - renderW) / 2;
        }

        double rTarget = 1.0;
        if (_selectedCropRatio == "9:16") rTarget = 9.0 / 16.0;
        else if (_selectedCropRatio == "16:9") rTarget = 16.0 / 9.0;
        else if (_selectedCropRatio == "1:1") rTarget = 1.0;

        double cropW, cropH;
        if (rVideo > rTarget)
        {
            cropH = renderH;
            cropW = renderH * rTarget;
        }
        else
        {
            cropW = renderW;
            cropH = renderW / rTarget;
        }

        double cropX = (renderW - cropW) / 2 + renderX;
        double cropY = (renderH - cropH) / 2 + renderY;

        CropOverlay.Width = cropW;
        CropOverlay.Height = cropH;
        CropOverlay.Margin = new Thickness(cropX, cropY, 0, 0);
        CropOverlay.Visibility = Visibility.Visible;
    }

    private string BuildFfmpegArgs(string input, string output, string? srtPath, string? hwEncoder)
    {
        var parts = new List<string> { "-y" };

        if (_trimStart > TimeSpan.Zero)
            parts.AddRange(["-ss", _trimStart.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);

        parts.AddRange(["-i", $"\"{input}\""]);

        var trimDuration = _trimEnd - _trimStart;
        if (trimDuration < _duration)
            parts.AddRange(["-t", trimDuration.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);

        var resItem = CmbResolution.SelectedItem as ComboBoxItem;
        var resTag = resItem?.Tag?.ToString() ?? "original";

        var formatItem = CmbFormat.SelectedItem as ComboBoxItem;
        var formatTag = formatItem?.Tag?.ToString() ?? "mp4";

        bool hasFilters = (_rotationAngle != 0) || 
                          _flipHorizontal || 
                          !string.IsNullOrEmpty(srtPath) || 
                          (_selectedCropRatio != "Original") || 
                          (_selectedSpeed != 1.0) || 
                          (_volumeFactor != 1.0) || 
                          (resTag != "original") ||
                          (formatTag == "webm");

        if (!hasFilters)
        {
            if (ChkMuteAudio.IsChecked == true)
            {
                parts.AddRange(["-c:v", "copy", "-an"]);
            }
            else
            {
                parts.AddRange(["-c:v", "copy", "-c:a", "copy"]);
            }
        }
        else
        {
            var vf = new List<string>();

            if (_rotationAngle == 90) vf.Add("transpose=1");
            else if (_rotationAngle == 180) vf.Add("transpose=1,transpose=1");
            else if (_rotationAngle == 270) vf.Add("transpose=2");

            if (_flipHorizontal) vf.Add("hflip");

            if (_selectedCropRatio != "Original")
            {
                double transW = VideoPlayer.NaturalVideoWidth;
                double transH = VideoPlayer.NaturalVideoHeight;
                if (_rotationAngle == 90 || _rotationAngle == 270)
                {
                    transW = VideoPlayer.NaturalVideoHeight;
                    transH = VideoPlayer.NaturalVideoWidth;
                }

                double rTrans = transW / transH;
                double rTarget = 1.0;
                if (_selectedCropRatio == "9:16") rTarget = 9.0 / 16.0;
                else if (_selectedCropRatio == "16:9") rTarget = 16.0 / 9.0;
                else if (_selectedCropRatio == "1:1") rTarget = 1.0;

                double cropW, cropH;
                if (rTrans > rTarget)
                {
                    cropH = transH;
                    cropW = transH * rTarget;
                }
                else
                {
                    cropW = transW;
                    cropH = transW / rTarget;
                }

                int finalW = ((int)Math.Round(cropW)) & ~1;
                int finalH = ((int)Math.Round(cropH)) & ~1;
                int finalX = ((int)Math.Round((transW - cropW) / 2)) & ~1;
                int finalY = ((int)Math.Round((transH - cropH) / 2)) & ~1;

                vf.Add($"crop={finalW}:{finalH}:{finalX}:{finalY}");
            }

            if (!string.IsNullOrEmpty(srtPath))
            {
                var escaped = srtPath.Replace("\\", "/").Replace(":", "\\:");
                vf.Add($"subtitles='{escaped}'");
            }

            if (resTag != "original")
            {
                int targetHeight = 1080;
                if (resTag == "720p") targetHeight = 720;
                else if (resTag == "480p") targetHeight = 480;

                vf.Add($"scale=-2:{targetHeight}");
            }

            if (_selectedSpeed != 1.0)
            {
                vf.Add($"setpts=PTS/{_selectedSpeed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (vf.Count > 0)
                parts.AddRange(["-vf", $"\"{string.Join(",", vf)}\""]);

            if (ChkMuteAudio.IsChecked == true)
            {
                parts.Add("-an");
            }
            else
            {
                var af = new List<string>();

                if (_selectedSpeed != 1.0)
                {
                    af.Add($"atempo={_selectedSpeed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                if (_volumeFactor != 1.0)
                {
                    af.Add($"volume={_volumeFactor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                if (af.Count > 0)
                {
                    parts.AddRange(["-af", $"\"{string.Join(",", af)}\""]);
                }

                if (formatTag == "webm")
                {
                    parts.AddRange(["-c:a", "libopus", "-b:a", "96k"]);
                }
                else
                {
                    parts.AddRange(["-c:a", "aac", "-b:a", "128k"]);
                }
            }

            if (formatTag == "webm")
            {
                parts.AddRange(["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "realtime"]);
            }
            else
            {
                // Only use a GPU encoder that was verified to work on this machine
                // (previously NVENC was hard-coded, so exports failed on AMD/Intel GPUs).
                switch (hwEncoder)
                {
                    case "h264_nvenc":
                        parts.AddRange(["-c:v", "h264_nvenc", "-cq", "20", "-preset", "p4"]);
                        break;
                    case "h264_qsv":
                        parts.AddRange(["-c:v", "h264_qsv", "-global_quality", "20", "-preset", "fast"]);
                        break;
                    case "h264_amf":
                        parts.AddRange(["-c:v", "h264_amf", "-qp_i", "20", "-qp_p", "20", "-quality", "balanced"]);
                        break;
                    default:
                        parts.AddRange(["-c:v", "libx264", "-crf", "18", "-preset", "fast"]);
                        break;
                }
            }
        }

        parts.AddRange(["-movflags", "+faststart"]);
        parts.Add($"\"{output}\"");

        return string.Join(" ", parts);
    }

    // ════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════
    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"m\:ss");

    private static string L(string key)
        => Application.Current.Resources[key] as string ?? key;

}
