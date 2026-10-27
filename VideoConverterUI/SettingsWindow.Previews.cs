using VideoConverterUI.Animations;
using VideoConverterUI.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VideoConverterUI;

/// <summary>
/// Sub-pages with live previews, in the style of Appearance → Liquid Glass: Appearance → Video colors,
/// Glass &amp; Layout → Window glass and → Toolbar buttons, Animations → Customize, plus the sample banners
/// in Notifications. Each preview follows its controls as they change; the values themselves are bound
/// in <see cref="BindAppearance"/> like every other row.
/// </summary>
public partial class SettingsWindow
{
    // ═══ Opening the sub-pages ═══

    private void OpenVideoColors_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneVideoColors, PaneAppearance, "PrefVideoColorsHeader");

    private void OpenWindowGlass_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneWindowGlass, PaneGlassLayout, "PrefWindowGlass");

    private void OpenToolbar_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneToolbar, PaneGlassLayout, "PrefToolbarHeader");

    private void OpenMotion_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneMotion, PaneAnimations, "PrefMotionCustomize");

    private void OpenWindowSizes_Click(object sender, RoutedEventArgs e)
        => OpenSubPage(PaneWindowSizes, PaneGlassLayout, "PrefWindowSizes");

    /// <summary>Called by <see cref="ShowPane"/> once <paramref name="pane"/> is the visible one.</summary>
    private void OnPaneShown(StackPanel pane, bool sliding)
    {
        if (pane == PaneAnimations || pane == PaneMotion)
        {
            MoveMotionPreview(pane == PaneMotion ? MotionPreviewHostDetail : MotionPreviewHostMain);
            // Wait for the page slide to finish, so the animation isn't half over before it can be seen.
            QueueMotionReplay(sliding ? 460 : 200);
        }
        else
        {
            _motionReplayTimer.Stop();
            MpProgress.Effect = null; // the glow pulse runs forever
        }

        if (pane == PaneVideoColors) UpdateVideoColorsPreview();
        if (pane == PaneUpdate) OnUpdatePaneShown();
        // Developer Mode (Advanced) decides whether the Editor button can show.
        if (pane == PaneToolbar || pane == PaneGlassLayout) UpdateToolbarPreview(animate: false);
        if (pane == PaneWindowGlass) UpdateWindowGlassPreview();
    }

    private void BindPreviews()
    {
        // Video colors
        _reloaders.Add(UpdateVideoColorsPreview);
        ChkAdaptiveTheme.Checked += (_, _) => UpdateVideoColorsPreview();
        ChkAdaptiveTheme.Unchecked += (_, _) => UpdateVideoColorsPreview();
        CmbGradEffectMode.SelectionChanged += (_, _) => UpdateVideoColorsPreview();
        BuildVideoSamples();

        // Window glass
        _reloaders.Add(UpdateWindowGlassPreview);
        SliderGlassOpacity.ValueChanged += (_, _) => UpdateWindowGlassPreview();

        // Toolbar
        _reloaders.Add(() => UpdateToolbarPreview(animate: false));
        foreach (var box in new[] { ChkPillHelp, ChkPillDev, ChkPillEditor, ChkPillLang, ChkShowIconLabels })
        {
            box.Checked += (_, _) => UpdateToolbarPreview(animate: !_loading);
            box.Unchecked += (_, _) => UpdateToolbarPreview(animate: !_loading);
        }

        // Resize handle
        _reloaders.Add(UpdateResizeHandlePreview);
        foreach (var box in new[] { ChkShowWindowResizerGrip, ChkResizerGripOnlyOnHover })
        {
            box.Checked += (_, _) => UpdateResizeHandlePreview();
            box.Unchecked += (_, _) => UpdateResizeHandlePreview();
        }

        // Motion: anything that changes how it moves plays the preview again
        BindThemeCombo(CmbWindowAnimStyle, t => KnownOr(t.WindowAnimStyle, WindowStyles, "Morph & Scale"), (t, v) => t.WindowAnimStyle = v);
        BindThemeCombo(CmbWindowAnimEasing, t => KnownOr(t.WindowAnimEasing, Easings, "Apple Spring"), (t, v) => t.WindowAnimEasing = v);
        _reloaders.Add(() => TxtMotionValue.Text = WindowStyleLabel(ThemeService.Current.WindowAnimStyle));
        CmbWindowAnimStyle.SelectionChanged += (_, _) => TxtMotionValue.Text = WindowStyleLabel(ThemeService.Current.WindowAnimStyle);
        foreach (var combo in new[] { CmbAnimPreset, CmbWindowAnimStyle, CmbWindowAnimEasing })
            combo.SelectionChanged += (_, _) => QueueMotionReplay();
        foreach (var box in new[] { ChkStaggered, ChkProgPulse })
        {
            box.Checked += (_, _) => QueueMotionReplay();
            box.Unchecked += (_, _) => QueueMotionReplay();
        }
        SliderWinDur.ValueChanged += (_, _) => QueueMotionReplay(350);
        _motionReplayTimer.Tick += (_, _) =>
        {
            _motionReplayTimer.Stop();
            PlayMotionPreview();
        };
    }

    /// <summary>The previews draw some colors from the theme's values instead of resources; follow it.</summary>
    private void OnPreviewThemeApplied() => UpdateVideoColorsPreview();

    // ═══ Appearance → Video colors ═══

    // Dominant colors of the sample videos: sunset, ocean, forest, neon, dusk.
    private static readonly Color[] VideoSampleColors =
    {
        Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0x24, 0x8B, 0xF5), Color.FromRgb(0x2F, 0xB8, 0x6A),
        Color.FromRgb(0xF2, 0x3D, 0x8E), Color.FromRgb(0x8E, 0x5C, 0xFF),
    };
    private int _videoSample;
    private readonly List<Border> _videoSampleRings = new();

    private void BuildVideoSamples()
    {
        VcSamples.Children.Clear();
        _videoSampleRings.Clear();
        for (int i = 0; i < VideoSampleColors.Length; i++)
        {
            int index = i;
            var ring = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2),
                Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Child = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Frozen(VideoSampleColors[i]) },
            };
            ring.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _videoSample = index;
                UpdateVideoColorsPreview();
            };
            _videoSampleRings.Add(ring);
            VcSamples.Children.Add(ring);
        }
    }

    /// <summary>
    /// Tints the preview card the way <c>MainWindow.RefreshDominantColorVisuals</c> / <c>UpdateWindowGradient</c> tint the
    /// real one for the selected sample video, with the same mixing ratios, and through
    /// <see cref="MainWindow.CreateAdaptiveTheme"/> when the app takes on the video's colors.
    /// </summary>
    private void UpdateVideoColorsPreview()
    {
        if (VcCard == null) return;
        var theme = ThemeService.Current;
        var dominant = VideoSampleColors[_videoSample];
        var t = theme.AdaptiveThumbnailTheme ? MainWindow.CreateAdaptiveTheme(theme, dominant) : theme;
        var mode = theme.GradientEffectMode ?? "both";
        bool light = ThemeService.IsLightTheme(theme);

        var windowBg = Opaque(ParseOr(t.BgDeep, Colors.Black));
        VcWindowBg.Background = mode == "both"
            ? new RadialGradientBrush
            {
                Center = new Point(0.3, 0.55), GradientOrigin = new Point(0.3, 0.55), RadiusX = 0.85, RadiusY = 1.6,
                GradientStops = { new GradientStop(Mix(windowBg, dominant, 0.20), 0), new GradientStop(windowBg, 1) },
            }
            : Frozen(windowBg);

        // InputBg, as ThemeService writes it for each mode
        var baseCard = light ? Color.FromArgb(0x14, 0x3C, 0x3C, 0x43) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
        VcCard.Background = mode == "none"
            ? Frozen(baseCard)
            : new LinearGradientBrush(Mix(baseCard, dominant, 0.25), baseCard, 90);
        VcCard.BorderBrush = theme.EnableBorders ? Frozen(ParseOr(t.BorderSub, Colors.Gray)) : Brushes.Transparent;
        VcThumbBg.Background = mode == "none"
            ? Frozen(ParseOr(t.BgElevated, Colors.Gray))
            : Frozen(Mix(Color.FromArgb(Math.Max(baseCard.A, (byte)0x22), baseCard.R, baseCard.G, baseCard.B), dominant, 0.30));
        VcStart.Background = Frozen(Opaque(ParseOr(t.Accent, Colors.DodgerBlue)));

        VcThumb.Children.Clear();
        VcThumb.Children.Add(SampleThumbnail(dominant));
        for (int i = 0; i < _videoSampleRings.Count; i++)
        {
            if (i == _videoSample) _videoSampleRings[i].SetResourceReference(Border.BorderBrushProperty, "TextPrimary");
            else _videoSampleRings[i].BorderBrush = Brushes.Transparent;
        }

        TxtVideoColorsValue.Text = L(mode switch
        {
            "none" => "PrefGradientOff",
            "thumbnail_only" => "PrefGradientCard",
            _ => "PrefGradientBoth",
        });
    }

    /// <summary>A stand-in video frame in the sample's colors: a sky, a sun and a hill.</summary>
    private static FrameworkElement SampleThumbnail(Color c)
    {
        var frame = new Grid
        {
            Background = new LinearGradientBrush(Mix(c, Colors.White, 0.35), Mix(c, Colors.Black, 0.45), 70),
        };
        frame.Children.Add(new Ellipse
        {
            Width = 26, Height = 26, Fill = Frozen(Mix(c, Colors.White, 0.75)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 8, 0),
        });
        frame.Children.Add(new Ellipse
        {
            Width = 150, Height = 70, Fill = Frozen(Mix(c, Colors.Black, 0.6)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-40, 0, -40, -34),
        });
        return frame;
    }

    // ═══ Glass & Layout → Window glass ═══

    /// <summary>The window body uses BgDeep and BorderSub, so opacity and borders follow by themselves.</summary>
    private void UpdateWindowGlassPreview()
    {
        if (TxtWindowGlassValue == null) return;
        TxtWindowGlassValue.Text = $"{SliderGlassOpacity.Value:0}%";
    }

    // ═══ Glass & Layout → Toolbar buttons ═══

    /// <summary>
    /// Mirrors MainWindow.RefreshHeaderPills for the switches on this page, with the same <see cref="PillMotion"/>
    /// animation. Like there, the Editor button only shows with Developer Mode on (and not in performance mode).
    /// </summary>
    private void UpdateToolbarPreview(bool animate)
    {
        if (TbHelp == null) return;
        var s = SettingsService.Current;
        bool help = ChkPillHelp.IsChecked == true, dev = ChkPillDev.IsChecked == true;
        bool editor = ChkPillEditor.IsChecked == true, lang = ChkPillLang.IsChecked == true;
        bool editorAvailable = s.EnableDeveloperMode && !s.EnablePerformanceMode;
        bool labels = ChkShowIconLabels.IsChecked == true;
        animate &= IsLoaded;

        PillMotion.SetShown(TbHelp, help, animate);
        PillMotion.SetShown(TbDev, dev, animate);
        PillMotion.SetShown(TbEditor, editor && editorAvailable, animate);
        PillMotion.SetShown(TbLang, lang, animate);
        foreach (var label in new[] { TbHelpLabel, TbConsoleLabel, TbLogLabel, TbEditorLabel, TbBatchLabel, TbSettingsLabel })
            PillMotion.SetShown(label, labels, animate, pop: false);
        TbLangCode.Text = (s.Language ?? "en").ToUpperInvariant();
        TxtPillEditorNote.Visibility = editorAvailable ? Visibility.Collapsed : Visibility.Visible;

        int shown = new[] { help, dev, editor, lang }.Count(on => on);
        TxtToolbarValue.Text = string.Format(L("PrefToolbarCount"), shown);
    }

    // ═══ Glass & Layout → Window sizes (resize handle) ═══
    // The preview's handle is shown and faded by the same rules as the real ones (SettingsService.HandleGripHover)
    // and resizes the little window when dragged, like the real handle resizes its window.

    private Point _rhDragStart;
    private Size _rhStartSize;

    private void UpdateResizeHandlePreview()
    {
        if (RhGrip == null) return;
        bool show = ChkShowWindowResizerGrip.IsChecked == true, hoverOnly = ChkResizerGripOnlyOnHover.IsChecked == true;
        RhGrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) SettingsService.HandleGripHover(RhGrip, RhGrip.IsMouseOver);
        RhHint.Text = L(!show ? "PrefHandleOffHint" : hoverOnly ? "PrefHandleHoverHint" : "PrefHandleTry");
        TxtResizeHandleValue.Text = L(!show ? "PrefHandleOff" : hoverOnly ? "PrefHandleOnHover" : "PrefHandleAlways");
    }

    private void RhGrip_MouseEnter(object sender, MouseEventArgs e) => SettingsService.HandleGripHover(RhGrip, true);
    private void RhGrip_MouseLeave(object sender, MouseEventArgs e) => SettingsService.HandleGripHover(RhGrip, false);

    private void RhGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _rhDragStart = e.GetPosition(RhPreview);
        _rhStartSize = new Size(RhWindow.ActualWidth, RhWindow.ActualHeight);
        RhGrip.CaptureMouse();
        e.Handled = true;
    }

    private void RhGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!RhGrip.IsMouseCaptured) return;
        var p = e.GetPosition(RhPreview);
        double maxW = RhPreview.ActualWidth - RhWindow.Margin.Left - 14, maxH = RhPreview.ActualHeight - RhWindow.Margin.Top - 14;
        RhWindow.Width = Math.Clamp(_rhStartSize.Width + p.X - _rhDragStart.X, 150, Math.Max(150, maxW));
        RhWindow.Height = Math.Clamp(_rhStartSize.Height + p.Y - _rhDragStart.Y, 96, Math.Max(96, maxH));
    }

    private void RhGrip_MouseUp(object sender, MouseButtonEventArgs e) => RhGrip.ReleaseMouseCapture();

    private void RhGrip_LostMouseCapture(object sender, MouseEventArgs e) => SettingsService.HandleGripHover(RhGrip, RhGrip.IsMouseOver);

    /// <summary>Window sizes → Reset: every open window back to the size it opened at.</summary>
    private void ResetWindowSizes_Click(object sender, RoutedEventArgs e) => SettingsService.ResetWindowSizes();

    // ═══ Animations → motion preview ═══

    private static readonly string[] WindowStyles = { Liquid.Name, "Morph & Scale", "Slide Up & Fade", "Zoom Elastic", "Fade Only" };
    private static readonly string[] Easings = { "Apple Spring (Bouncy)", "Apple Spring", "Elastic Snap", "Cubic Smooth", "Linear Constant" };

    private readonly DispatcherTimer _motionReplayTimer = new();
    private int _motionVersion;

    /// <summary>Values the menus don't list behave like the fallback (FluidMotion treats them that way too).</summary>
    private static string KnownOr(string? value, string[] known, string fallback)
        => value != null && known.Contains(value) ? value : fallback;

    private string WindowStyleLabel(string? style) => L(KnownOr(style, WindowStyles, "Morph & Scale") switch
    {
        Liquid.Name => "PrefWindowStyleLiquid",
        "Slide Up & Fade" => "PrefWindowStyleSlide",
        "Zoom Elastic" => "PrefWindowStyleZoom",
        "Fade Only" => "PrefWindowStyleFade",
        _ => "PrefWindowStyleMorph",
    });

    /// <summary>The preview lives in one host at a time: the Animations pane or its Customize sub-page.</summary>
    private void MoveMotionPreview(Border host)
    {
        if (MotionPreview.Parent == host) return;
        if (MotionPreview.Parent is Border oldHost) oldHost.Child = null;
        host.Child = MotionPreview;
    }

    private void QueueMotionReplay() => QueueMotionReplay(220);

    private void QueueMotionReplay(int delayMs)
    {
        if (!IsLoaded || _loading) return;
        if (_activePane != PaneAnimations && _activePane != PaneMotion) return;
        _motionReplayTimer.Stop();
        _motionReplayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _motionReplayTimer.Start();
    }

    private void ReplayMotion_Click(object sender, RoutedEventArgs e) => PlayMotionPreview();

    /// <summary>
    /// Opens the mini window the way <see cref="FluidMotion.MorphOpen"/> opens real ones (same style, easing, duration
    /// and speed per profile; Morph grows it out of the button like a window out of the button that opened it), then
    /// cascades its rows and fills the progress bar, with the glow if that is on.
    /// </summary>
    private void PlayMotionPreview()
    {
        var theme = ThemeService.Current;
        int version = ++_motionVersion;

        // Back to rest
        MpWindow.BeginAnimation(OpacityProperty, null);
        MpScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MpScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        MpTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        MpTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        MpWindow.Opacity = 1;
        MpScale.ScaleX = MpScale.ScaleY = 1;
        MpTranslate.X = MpTranslate.Y = 0;
        MpWindow.RenderTransformOrigin = new Point(0.5, 0.5);
        foreach (FrameworkElement row in MpRows.Children)
        {
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 1;
            row.RenderTransform = Transform.Identity;
        }
        MpProgress.BeginAnimation(WidthProperty, null);
        MpProgress.Effect = null;
        double progressTo = Math.Max(0, MpTrack.ActualWidth * 0.72);

        if (FluidMotion.AnimationsDisabled)
        {
            MpProgress.Width = progressTo;
            return;
        }

        double speed = FluidMotion.SpeedFor(theme.AnimationPreset ?? "");
        int baseMs = theme.WindowAnimDuration > 0 ? theme.WindowAnimDuration : 280;
        var springDur = TimeSpan.FromMilliseconds(baseMs * speed);
        var fadeDur = TimeSpan.FromMilliseconds(baseMs * 0.6 * speed);
        var easing = FluidMotion.GetEasing(theme.WindowAnimEasing);
        var smooth = AppleSpringEase.Smooth;

        MpWindow.Opacity = 0;
        MpWindow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = smooth });
        string style = KnownOr(theme.WindowAnimStyle, WindowStyles, "Morph & Scale");
        switch (style)
        {
            case "Fade Only":
                break;
            case "Slide Up & Fade":
                MpTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(35, 0, springDur) { EasingFunction = easing });
                break;
            case "Zoom Elastic":
                MpScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, springDur) { EasingFunction = AppleSpringEase.Bouncy });
                MpScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, springDur) { EasingFunction = AppleSpringEase.Bouncy });
                break;
            default:
            {
                // Grow out of the button, with MorphOpen's math (window = MpWindow, origin rect = the button).
                var origin = MpTryButton.TranslatePoint(new Point(MpTryButton.ActualWidth / 2, MpTryButton.ActualHeight / 2), MotionPreview);
                var topLeft = MpWindow.TranslatePoint(new Point(0, 0), MotionPreview);
                double w = Math.Max(1, MpWindow.ActualWidth), h = Math.Max(1, MpWindow.ActualHeight);
                double ox = Math.Clamp((origin.X - topLeft.X) / w, 0, 1), oy = Math.Clamp((origin.Y - topLeft.Y) / h, 0, 1);
                double sx = Math.Clamp(MpTryButton.ActualWidth / w, 0.03, 0.45), sy = Math.Clamp(MpTryButton.ActualHeight / h, 0.03, 0.45);
                double tx = origin.X - topLeft.X - ox * w, ty = origin.Y - topLeft.Y - oy * h;
                if (style == Liquid.Name)
                {
                    Liquid.OpenWindow(MpWindow, MpScale, MpTranslate, ox, oy, sx, sy, tx, ty, Liquid.WindowSpring(theme, speed));
                    break;
                }
                MpWindow.RenderTransformOrigin = new Point(ox, oy);
                MpScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, 1, springDur) { EasingFunction = easing });
                MpScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, 1, springDur) { EasingFunction = easing });
                MpTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx, 0, springDur) { EasingFunction = smooth });
                MpTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ty, 0, springDur) { EasingFunction = smooth });
                break;
            }
        }

        if (theme.EnableStaggeredAnimations)
            FluidMotion.StaggerIn(MpRows, baseDelayMs: (int)(springDur.TotalMilliseconds * 0.35), stepMs: 60);

        var fill = new DoubleAnimation(0, progressTo, TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromMilliseconds(springDur.TotalMilliseconds * 0.6 + 150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fill.Completed += (_, _) =>
        {
            if (version != _motionVersion || !theme.EnableProgressPulse) return;
            var accent = (TryFindResource("Accent") as SolidColorBrush)?.Color;
            FluidMotion.StartProgressGlowPulse(MpProgress, accent);
        };
        MpProgress.BeginAnimation(WidthProperty, fill);
    }

    // ═══ Notifications → sample banners ═══

    private void PreviewBanner_Click(object sender, RoutedEventArgs e)
    {
        var (type, message) = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "error" => (NotificationType.Error, "PrefBannerSampleErrorMsg"),
            "done" => (NotificationType.Success, "PrefBannerSampleDoneMsg"),
            _ => (NotificationType.Info, "PrefBannerSampleInfoMsg"),
        };
        NotificationWindow.Show(L(message), this, type);
    }

    // ═══ Color helpers ═══

    private static Color ParseOr(string hex, Color fallback) => ThemeService.TryParseColor(hex, out var c) ? c : fallback;

    private static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>Moves <paramref name="c"/> towards <paramref name="to"/> by <paramref name="amount"/>, keeping its alpha.</summary>
    private static Color Mix(Color c, Color to, double amount) => Color.FromArgb(c.A,
        (byte)(c.R * (1 - amount) + to.R * amount),
        (byte)(c.G * (1 - amount) + to.G * amount),
        (byte)(c.B * (1 - amount) + to.B * amount));

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
