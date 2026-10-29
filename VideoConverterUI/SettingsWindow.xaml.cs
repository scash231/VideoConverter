using Microsoft.Win32;
using VideoConverterUI.Animations;
using VideoConverterUI.Models;
using VideoConverterUI.Services;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoConverterUI;

/// <summary>
/// Settings, organised like macOS System Settings: a sidebar of panes and grouped rows.
/// Every control applies and saves its value the moment it changes; there is no Save or Cancel.
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _isAnimatingClose;
    private StackPanel? _activePane;
    private readonly Dictionary<RadioButton, StackPanel> _panes = new();
    private readonly Dictionary<StackPanel, string> _paneTitles = new();

    // Re-read every control from SettingsService.Current (open, Reset All, changes made elsewhere).
    private readonly List<Action> _reloaders = new();
    private bool _loading;
    private bool _committing;

    /// <summary>True once any setting was changed while the window was open.</summary>
    public bool HasChanges { get; private set; }

    /// <summary>Pane to show when the window opens: "appearance", "advanced", "download", "upload" or null for General.</summary>
    public string? InitialTab { get; set; }

    /// <summary>Screen rect (of the button that opened it) the window morphs open from.</summary>
    public Rect OriginRect { get; set; }

    /// <summary>
    /// Builds the whole window (~75 ms for its XAML and bindings). MainWindow does that ahead while the app is idle
    /// and sets <see cref="OriginRect"/>, <see cref="InitialTab"/> and the Owner when it shows it; everything that
    /// depends on the moment it opens (current values, subscriptions) happens in OnLoaded.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        // ScrollViewers mark mouse-down as handled (to take focus), so the drag handler also listens to handled events.
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(Window_DragFromBackground), handledEventsToo: true);
        TabContentScroll.ScrollChanged += (_, _) => UpdateHeaderScrim();

        _panes[NavGeneral] = PaneGeneral;
        _panes[NavAppearance] = PaneAppearance;
        _panes[NavGlassLayout] = PaneGlassLayout;
        _panes[NavAnimations] = PaneAnimations;
        _panes[NavNotifications] = PaneNotifications;
        _panes[NavDownloads] = PaneDownloads;
        _panes[NavMulti] = PaneMulti;
        _panes[NavYouTube] = PaneYouTube;
        _panes[NavCloud] = PaneCloud;
        _panes[NavAdvanced] = PaneAdvanced;
        _panes[NavDeveloper] = PaneDeveloper;
        _panes[NavAbout] = PaneAbout;
        // The title over the content names the pane, like the sidebar does.
        _paneTitles[PaneGeneral] = "PrefGeneral";
        _paneTitles[PaneAppearance] = "PrefAppearance";
        _paneTitles[PaneGlassLayout] = "PrefGlassLayout";
        _paneTitles[PaneAnimations] = "PrefAnimations";
        _paneTitles[PaneNotifications] = "PrefNotifications";
        _paneTitles[PaneDownloads] = "PrefDownloads";
        _paneTitles[PaneMulti] = "PrefMulti";
        _paneTitles[PaneYouTube] = "PrefYouTube";
        _paneTitles[PaneCloud] = "PrefCloud";
        _paneTitles[PaneAdvanced] = "PrefAdvanced";
        _paneTitles[PaneDeveloper] = "PrefDeveloper";
        _paneTitles[PaneAbout] = "PrefAbout";
        // Clicking the (already selected) sidebar item of a sub-page's parent goes back to it.
        foreach (var (nav, pane) in _panes)
            nav.Click += (_, _) => { if (SubPageRoot == pane) ShowPane(pane); };

        BindControls();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FluidMotion.MorphOpen(RootBorder, WindowScale, WindowTranslate, OriginRect, this);

        ReloadAll();
        _activePane = PaneGeneral;

        if (InitialTab != null) SelectPane(InitialTab);

        TxtInfoBuildName.Text = BuildInfoService.BuildName;
        TxtInfoDotNet.Text = BuildInfoService.DotNetVersion;
        TxtInfoMediaPipeline.Text = BuildInfoService.MediaPipeline;
        BindWhatsNew();
        _reloaders.Add(BindWhatsNew); // the row texts follow the language
        UpdateEngineInfo();
        UpdateFfmpegStatusUi();

        SettingsService.SettingsChanged += OnSettingsChanged;
        ThemeService.ThemeApplied += OnPreviewThemeApplied;
        UpdateService.Changed += UpdateUpdateUi;
        Closed += (_, _) =>
        {
            SettingsService.SettingsChanged -= OnSettingsChanged;
            ThemeService.ThemeApplied -= OnPreviewThemeApplied;
            UpdateService.Changed -= UpdateUpdateUi;
            SetUpdateSpinner(false);
            SetWhatsNewSpinner(false);
            _motionReplayTimer.Stop();
        };
        SettingsService.ApplyResizeGripVisibility(this);

        ContentHost.SizeChanged += (_, _) => ClipContentAroundClose();
        ClipContentAroundClose();
    }

    /// <summary>
    /// Cuts a round hole in the scroll area behind the close button (and the back button on a sub-page),
    /// so only its glass copy shows there.
    /// </summary>
    private void ClipContentAroundClose()
    {
        if (ContentHost.ActualWidth <= 0 || CloseGlass.ActualWidth <= 0) return;
        Geometry clip = new RectangleGeometry(new Rect(0, 0, ContentHost.ActualWidth, ContentHost.ActualHeight));
        foreach (var button in new FrameworkElement[] { CloseGlass, BackGlass })
        {
            if (button.Visibility != Visibility.Visible || button.ActualWidth <= 0) continue;
            var center = button.TransformToVisual(ContentHost).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            double r = button.ActualWidth / 2;
            clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new EllipseGeometry(center, r, r));
        }
        ContentHost.Clip = clip;
    }

    // ═══ Sub-pages ═══
    // A pane opened from a row of another pane (Appearance → Liquid Glass), like a pushed page in iOS Settings:
    // the back button (BackGlass, the close button's twin on the left) appears and the title shows the sub-page's name.
    // Pushing and popping slides the pages sideways (FluidMotion.SlidePage); going back restores the parent's scroll position.
    // A sub-page can open a further one (About → 27H1 → 27H1.23): the outer levels wait in _subPageTrail.
    private StackPanel? _subPageParent;
    private double _subPageParentOffset;
    private readonly Stack<(StackPanel Parent, double Offset, string Title)> _subPageTrail = new();
    private int _slideVersion;

    /// <summary>The top-level pane the current sub-page chain started from.</summary>
    private StackPanel? SubPageRoot => _subPageTrail.Count > 0 ? _subPageTrail.Last().Parent : _subPageParent;

    private void OpenSubPage(StackPanel page, StackPanel parent, string titleKey)
    {
        TxtPaneTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
        PushSubPage(page, parent);
    }

    private void PushSubPage(StackPanel page, StackPanel parent, string? parentTitle = null)
    {
        // Opened from another sub-page: remember how to get back out of that one too.
        bool nested = _subPageParent != null && _activePane == parent;
        if (nested) _subPageTrail.Push((_subPageParent!, _subPageParentOffset, parentTitle ?? TxtPaneTitle.Text));
        else _subPageTrail.Clear();

        _subPageParent = parent;
        _subPageParentOffset = TabContentScroll.VerticalOffset;
        ShowPane(page);
        if (!nested) SetSubPageChrome(true);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_subPageParent is not { } parent) return;
        if (_subPageTrail.Count == 0) { ShowPane(parent); return; }

        // Back to a sub-page: it gets its own parent, scroll position and title back.
        var (outer, offset, title) = _subPageTrail.Pop();
        ShowPane(parent);
        _subPageParent = outer;
        _subPageParentOffset = offset;
        TxtPaneTitle.Text = title;
    }

    private void SetSubPageChrome(bool visible)
    {
        if (!visible) { _subPageParent = null; _subPageTrail.Clear(); }
        var chrome = new FrameworkElement[] { BackGlass };
        bool animate = !FluidMotion.AnimationsDisabled && IsLoaded;

        if (visible)
        {
            foreach (var element in chrome)
            {
                element.Visibility = Visibility.Visible;
                element.BeginAnimation(OpacityProperty, animate ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) : null);
            }
            BackGlass.UpdateLayout();
            ClipContentAroundClose();
            return;
        }

        void Hide()
        {
            if (_subPageParent != null) return; // a sub-page opened again meanwhile
            foreach (var element in chrome)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Visibility = Visibility.Collapsed;
            }
            ClipContentAroundClose();
        }

        if (!animate) { Hide(); return; }
        foreach (var element in chrome)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            if (element == BackGlass) fade.Completed += (_, _) => Hide();
            element.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ═══ What's New ═══

    // About → What's New (PaneWhatsNew) lists the big versions loaded from the website (WhatsNewService); one opens its
    // releases (PaneVersion), a release its notes (PaneRelease).

    private sealed record WhatsNewVersionRow(ChangelogVersion Version, string Detail);

    private sealed record WhatsNewReleaseRow(ChangelogRelease Release, string Preview, bool IsCurrent);

    private ChangelogVersion? _openVersion;

    /// <summary>Shows <see cref="WhatsNewService.Last"/>: loading (nothing yet), couldn't load, or the list (from the saved copy: noted).</summary>
    private void BindWhatsNew()
    {
        var result = WhatsNewService.Last;
        bool loading = result == null;
        bool failed = result is { Failed: true };
        WhatsNewLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SetWhatsNewSpinner(loading && _activePane == PaneWhatsNew);
        WhatsNewFailed.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        TxtWhatsNewOffline.Visibility = result is { FromCache: true } ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewList.Visibility = loading || failed ? Visibility.Collapsed : Visibility.Visible;
        VersionList.ItemsSource = result?.Versions
            .Select(v => new WhatsNewVersionRow(v, ReleaseCount(v.Releases.Count)))
            .ToList();
        if (_openVersion != null) FillVersionPage(_openVersion);
    }

    private void OpenWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        OpenSubPage(PaneWhatsNew, PaneAbout, "PrefWhatsNew");
        _ = LoadWhatsNewAsync(force: false);
    }

    private void WhatsNewRetry_Click(object sender, RoutedEventArgs e) => _ = LoadWhatsNewAsync(force: true);

    private async Task LoadWhatsNewAsync(bool force)
    {
        // Something loaded already stays up while it looks again; only a first load or a retry shows the spinner.
        if (WhatsNewService.Last is null or { Failed: true })
        {
            WhatsNewLoading.Visibility = Visibility.Visible;
            WhatsNewFailed.Visibility = Visibility.Collapsed;
            SetWhatsNewSpinner(true);
        }
        await WhatsNewService.LoadAsync(force);
        if (IsLoaded) BindWhatsNew();
    }

    /// <summary>The arrow turns only while the page loads (no endless animation left behind on a hidden page).</summary>
    private void SetWhatsNewSpinner(bool spinning)
    {
        if (!spinning || FluidMotion.AnimationsDisabled)
        {
            WhatsNewSpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        if (WhatsNewSpinnerTurn.HasAnimatedProperties) return;
        WhatsNewSpinnerTurn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>"8 releases" in the UI language (Russian has a separate form for 2–4).</summary>
    private static string ReleaseCount(int n)
    {
        int mod10 = n % 10, mod100 = n % 100;
        string key = n == 1 ? "PrefReleaseCountOne"
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "PrefReleaseCountFew"
            : "PrefReleaseCount";
        return string.Format(L(key), n);
    }

    private void FillVersionPage(ChangelogVersion version)
    {
        string dates = version.FirstDate == version.LatestDate ? version.LatestDate : $"{version.FirstDate} – {version.LatestDate}";
        TxtVersionInfo.Text = $"{version.Channel}  ·  {dates}";
        VersionReleases.ItemsSource = version.Releases
            .Select(r => new WhatsNewReleaseRow(r, string.Join("  ·  ", r.Sections.Select(sec => sec.Title)),
                r.Version == BuildInfoService.BuildVersion))
            .ToList();
    }

    private void OpenVersion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WhatsNewVersionRow row) return;
        _openVersion = row.Version;
        FillVersionPage(row.Version);
        string whatsNewTitle = TxtPaneTitle.Text; // what Back puts back
        TxtPaneTitle.Text = row.Version.Name;
        PushSubPage(PaneVersion, PaneWhatsNew, whatsNewTitle);
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WhatsNewReleaseRow row) return;
        var release = row.Release;
        TxtReleaseDate.Text = release.Date;
        TxtReleaseDate.Visibility = string.IsNullOrEmpty(release.Date) ? Visibility.Collapsed : Visibility.Visible;
        ReleaseSections.ItemsSource = release.Sections;
        string versionTitle = TxtPaneTitle.Text;
        TxtPaneTitle.Text = release.Version;
        PushSubPage(PaneRelease, PaneVersion, versionTitle);
    }

    /// <summary>
    /// Freezes what the scroll area shows right now into <see cref="PageSnapshot"/>, laid under it, so the old page
    /// can slide out while the real one has already been swapped and scrolled.
    /// </summary>
    private Image? SnapshotContent()
    {
        double w = TabContentScroll.ActualWidth, h = TabContentScroll.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        var dpi = VisualTreeHelper.GetDpi(this);
        var brush = new VisualBrush(TabContentScroll)
        {
            Viewbox = new Rect(0, 0, w, h),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        PageSnapshot.Source = bitmap;
        PageSnapshot.Width = w;
        PageSnapshot.Height = h;
        PageSnapshot.Visibility = Visibility.Visible;
        return PageSnapshot;
    }

    private void ClearSnapshot()
    {
        PageSnapshot.BeginAnimation(OpacityProperty, null);
        PageSnapshot.RenderTransform = Transform.Identity;
        PageSnapshot.Visibility = Visibility.Collapsed;
        PageSnapshot.Source = null;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        SettingsService.ApplyResizeGripVisibility(this);
        // Another window changed settings (e.g. the Multi Downloader toolbar): show the new values.
        if (!_committing) ReloadAll();
    }

    // ═══ Instant apply ═══

    /// <summary>Applies a change to a copy of the current settings and saves it right away.</summary>
    private void Commit(Action<AppSettings> change)
    {
        if (_loading || !IsLoaded) return;
        var s = SettingsService.Current.Clone();
        change(s);
        _committing = true;
        try { SettingsService.Save(s); }
        finally { _committing = false; }
        HasChanges = true;
    }

    private void BindSwitch(CheckBox box, Func<AppSettings, bool> get, Action<AppSettings, bool> set, Action<bool>? applied = null)
    {
        _reloaders.Add(() => box.IsChecked = get(SettingsService.Current));
        RoutedEventHandler handler = (_, _) =>
        {
            if (_loading) return;
            bool value = box.IsChecked == true;
            if (value == get(SettingsService.Current)) return;
            Commit(s => set(s, value));
            applied?.Invoke(value);
        };
        box.Checked += handler;
        box.Unchecked += handler;
    }

    private void BindCombo(ComboBox combo, Func<AppSettings, string> get, Action<AppSettings, string> set, Action<string>? applied = null)
    {
        _reloaders.Add(() => SelectComboByTag(combo, get(SettingsService.Current)));
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var tag = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (tag == null || tag == get(SettingsService.Current)) return;
            Commit(s => set(s, tag));
            applied?.Invoke(tag);
        };
    }

    private void BindIntCombo(ComboBox combo, Func<AppSettings, int> get, Action<AppSettings, int> set) =>
        BindCombo(combo, s => get(s).ToString(CultureInfo.InvariantCulture),
            (s, v) => set(s, int.Parse(v, CultureInfo.InvariantCulture)));

    private void BindDurationCombo(ComboBox combo, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        _reloaders.Add(() => PopulateDurationCombo(combo, get(SettingsService.Current)));
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (!int.TryParse((combo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;
            if (v == get(SettingsService.Current)) return;
            Commit(s => set(s, Math.Max(0, v)));
        };
    }

    /// <summary>Text fields save when you press Enter or leave the field.</summary>
    private void BindText(TextBox box, Func<AppSettings, string> get, Action<AppSettings, string> set)
    {
        _reloaders.Add(() => box.Text = get(SettingsService.Current));
        void Apply()
        {
            if (_loading) return;
            var text = box.Text.Trim();
            if (text == get(SettingsService.Current)) return;
            Commit(s => set(s, text));
            box.Text = get(SettingsService.Current);
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
    }

    private void BindControls()
    {
        // General
        BindCombo(CmbLanguage, s => s.Language, (s, v) => s.Language = v, v =>
        {
            LocalizationService.ApplyLanguage(v);
            ReloadAll(); // texts built in code (durations, folder rows) follow the new language
            UpdateFfmpegStatusUi();
        });
        BindSwitch(ChkAlwaysOnTop, s => s.AlwaysOnTop, (s, v) => s.AlwaysOnTop = v, v => { if (Owner != null) Owner.Topmost = v; });
        BindSwitch(ChkAutoPaste, s => s.AutoPasteOnFocus, (s, v) => s.AutoPasteOnFocus = v);
        BindSwitch(ChkAutoFetch, s => s.AutoFetchMetadata, (s, v) => s.AutoFetchMetadata = v);
        BindSwitch(ChkAutoOpenErrorWindow, s => s.AutoOpenErrorWindow, (s, v) => s.AutoOpenErrorWindow = v);

        // Notifications
        BindCombo(CmbCompletionNotificationMode, s => s.CompletionNotificationMode, (s, v) => s.CompletionNotificationMode = v);
        BindSwitch(ChkPlaylistDetectedNotification, s => s.PlaylistDetectedNotification, (s, v) => s.PlaylistDetectedNotification = v);
        BindDurationCombo(CmbNotifyDurationInfo, s => s.NotificationDurationInfo, (s, v) => s.NotificationDurationInfo = v);
        BindDurationCombo(CmbNotifyDurationError, s => s.NotificationDurationError, (s, v) => s.NotificationDurationError = v);
        BindDurationCombo(CmbNotifyDurationComplete, s => s.NotificationDurationComplete, (s, v) => s.NotificationDurationComplete = v);

        // Downloads
        _reloaders.Add(UpdateFolderRows);
        BindCombo(CmbVideoQuality, s => s.DefaultVideoQuality, (s, v) => s.DefaultVideoQuality = v);
        BindSwitch(ChkDefaultFpDl, s => s.DefaultFingerprintDlEnabled, (s, v) => s.DefaultFingerprintDlEnabled = v);
        BindCombo(CmbDefaultFpMethodDl, s => s.DefaultFingerprintDlMethod, (s, v) => s.DefaultFingerprintDlMethod = v);

        // Multi Downloader
        BindCombo(CmbBatchQuality, s => s.BatchQuality, (s, v) => s.BatchQuality = v);
        BindSwitch(ChkShowPerVideoQuality, s => s.ShowPerVideoQuality, (s, v) => s.ShowPerVideoQuality = v);
        BindIntCombo(CmbBatchMaxConcurrency, s => s.BatchMaxConcurrency, (s, v) => s.BatchMaxConcurrency = v);
        BindSwitch(ChkBatchAutoRetry, s => s.BatchAutoRetry, (s, v) => s.BatchAutoRetry = v);
        BindSwitch(ChkBatchAutoRemoveDone, s => s.BatchAutoRemoveDone, (s, v) => s.BatchAutoRemoveDone = v);
        BindSwitch(ChkPromptSaveBatchOnExit, s => s.PromptSaveBatchOnExit, (s, v) => s.PromptSaveBatchOnExit = v);

        // YouTube
        BindCombo(CmbDefaultPrivacy, s => s.DefaultPrivacy, (s, v) => s.DefaultPrivacy = v);
        BindSwitch(ChkAutoShorts, s => s.AutoAddShortsHashtag, (s, v) => s.AutoAddShortsHashtag = v);
        BindSwitch(ChkDefaultFp, s => s.DefaultFingerprintEnabled, (s, v) => s.DefaultFingerprintEnabled = v);
        BindCombo(CmbDefaultFpMethod, s => s.DefaultFingerprintMethod, (s, v) => s.DefaultFingerprintMethod = v);

        // Cloud
        BindSwitch(ChkCloudUpload, s => s.CloudUploadEnabled, (s, v) => s.CloudUploadEnabled = v);
        BindCombo(CmbCloudProvider, s => s.CloudProvider, (s, v) => s.CloudProvider = v, _ => UpdateCloudConfigRows());
        BindSwitch(ChkCloudTemp, s => s.CloudTempDownload, (s, v) => s.CloudTempDownload = v);

        // Resize handle (the preview follows in SettingsWindow.Previews.cs)
        BindSwitch(ChkShowWindowResizerGrip, s => s.ShowWindowResizerGrip, (s, v) => s.ShowWindowResizerGrip = v);
        BindSwitch(ChkResizerGripOnlyOnHover, s => s.ResizerGripOnlyOnHover, (s, v) => s.ResizerGripOnlyOnHover = v);

        // Advanced
        BindCombo(CmbExecutionEngine, s => s.ExecutionEngine, (s, v) => s.ExecutionEngine = v, ApplyEngineSelection);
        BindSwitch(ChkUseGpu, s => s.UseGpu, (s, v) => s.UseGpu = v);
        BindIntCombo(CmbMaxJobs, s => s.MaxConcurrentJobs, (s, v) => s.MaxConcurrentJobs = v);
        _reloaders.Add(() => ChkPerformanceMode.IsChecked = SettingsService.Current.EnablePerformanceMode);
        BindSwitch(ChkDeveloperMode, s => s.EnableDeveloperMode, (s, v) => s.EnableDeveloperMode = v, _ => UpdateDeveloperNav());
        _reloaders.Add(UpdateDeveloperNav);

        // Developer
        BindSwitch(ChkAutoOpenConsoleOnError, s => s.AutoOpenConsoleOnError, (s, v) => s.AutoOpenConsoleOnError = v);
        BindSwitch(ChkVerboseLogging, s => s.VerboseLogging, (s, v) => s.VerboseLogging = v);
        BindSwitch(ChkBypassFileRestrictions, s => s.BypassFileRestrictions, (s, v) => s.BypassFileRestrictions = v);
        BindSwitch(ChkShowPerformanceOverlay, s => s.ShowPerformanceOverlay, (s, v) => s.ShowPerformanceOverlay = v);
        BindSwitch(ChkDevConsole, s => s.DevConsoleEnabled, (s, v) => s.DevConsoleEnabled = v);
        BindSwitch(ChkConsoleShowSystem, s => s.ConsoleShowSystem, (s, v) => s.ConsoleShowSystem = v);
        BindSwitch(ChkConsoleShowBackend, s => s.ConsoleShowBackend, (s, v) => s.ConsoleShowBackend = v);
        BindSwitch(ChkConsoleShowFFmpeg, s => s.ConsoleShowFFmpeg, (s, v) => s.ConsoleShowFFmpeg = v);
        BindSwitch(ChkAutoRestartBackend, s => s.AutoRestartBackend, (s, v) => s.AutoRestartBackend = v);
        BindText(TxtBackendUrl, s => s.BackendUrl,
            (s, v) => s.BackendUrl = string.IsNullOrWhiteSpace(v) ? "http://127.0.0.1:8765" : v);
        BindText(TxtBackendTimeout, s => s.BackendTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            (s, v) => { if (int.TryParse(v, out var t) && t > 0) s.BackendTimeoutSeconds = t; });
        BindCombo(CmbBackendLogLevel, s => s.BackendLogLevel, (s, v) => s.BackendLogLevel = v);
        BindCombo(CmbGlassSampling, s => s.GlassSamplingMode ?? "timer", (s, v) => s.GlassSamplingMode = v);

        BindUpdate();
        BindCloudConfig();
        BindAppearance();
    }

    private void ReloadAll()
    {
        _loading = true;
        try
        {
            foreach (var reload in _reloaders) reload();
        }
        finally
        {
            _loading = false;
        }
    }

    // ═══ Window chrome ═══

    /// <summary>
    /// There is no title bar: the window moves when dragged by any background, i.e. the sidebar or empty space in
    /// a pane. Clicks on controls, clickable tiles and swatches, settings rows and the resize grip are left alone.
    /// </summary>
    private void Window_DragFromBackground(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || e.ButtonState != MouseButtonState.Pressed) return;
        if (Sheet.Visibility == Visibility.Visible || !IsBackground(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private bool IsBackground(DependencyObject? element)
    {
        var groupStyle = (Style)FindResource("Group");
        var d = element;
        for (; d != null && d != this;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase
                  or ComboBox or Slider or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb)
                return false;
            if (d is FrameworkElement fe && (fe.Cursor != null && fe.Cursor != Cursors.Arrow || fe.Style == groupStyle))
                return false;
        }
        // Clicks inside pop-ups (menu items) never reach the window in the visual tree.
        return d == this;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithAnimation();

    /// <summary>The gradient behind the title fades in over the first 32 px of scrolling.</summary>
    private void UpdateHeaderScrim()
    {
        if (_scrimHeld) return;
        double target = Math.Clamp(TabContentScroll.VerticalOffset / 32, 0, 1);
        if (Math.Abs(target - _scrimTarget) < 0.001) return;
        bool jump = Math.Abs(target - _scrimTarget) > 0.25;  // a pane switch or a restored scroll position, not a scroll step
        _scrimTarget = target;

        if (SettingsService.Current.EnablePerformanceMode || FluidMotion.AnimationsDisabled || !(jump || _scrimFading))
        {
            HeaderScrim.BeginAnimation(OpacityProperty, null);
            HeaderScrim.Opacity = target;
            return;
        }

        // Fades from wherever it is now; a scroll during the fade just retargets it.
        _scrimFading = true;
        var fade = new DoubleAnimation(target, TimeSpan.FromMilliseconds(jump ? 240 : 120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (Math.Abs(_scrimTarget - target) > 0.001) return;  // superseded by a newer fade
            _scrimFading = false;
            HeaderScrim.BeginAnimation(OpacityProperty, null);
            HeaderScrim.Opacity = target;
        };
        HeaderScrim.BeginAnimation(OpacityProperty, fade);
    }

    private double _scrimTarget;
    private bool _scrimFading;
    private bool _scrimHeld;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Sheet.Visibility == Visibility.Visible) SheetCancel_Click(sender, e);
        else if (_subPageParent != null) Back_Click(sender, e);
        else CloseWithAnimation();
        e.Handled = true;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        // A window built ahead and never shown (MainWindow's spare) just closes.
        if (!_isAnimatingClose && IsLoaded)
        {
            e.Cancel = true;
            CloseWithAnimation();
        }
        base.OnClosing(e);
    }

    private void CloseWithAnimation()
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;

        ThemeService.FlushPendingSave();

        if (SettingsService.Current.EnablePerformanceMode) { Close(); return; }
        FluidMotion.MorphClose(RootBorder, WindowScale, WindowTranslate, this, Close);
    }

    // ═══ Panes ═══

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav || !_panes.TryGetValue(nav, out var pane)) return;
        if (IsLoaded) ShowPane(pane);
    }

    private void ShowPane(StackPanel target)
    {
        var old = _activePane;
        if (old == target) return;
        _activePane = target;

        // Into a sub-page from its parent, or back out of it: slide sideways like iOS navigation.
        bool push = old != null && IsSubPage(target) && old == _subPageParent;
        bool pop = old != null && IsSubPage(old) && target == _subPageParent;
        double restoreOffset = pop ? _subPageParentOffset : 0;
        if (_subPageParent != null && !IsSubPage(target)) SetSubPageChrome(false);

        // Sub-pages set their own title before they are shown (OpenSubPage / OpenRelease_Click).
        if (_paneTitles.TryGetValue(target, out var titleKey))
            TxtPaneTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
        TxtPaneTitle.BeginAnimation(OpacityProperty, IsLoaded && !FluidMotion.AnimationsDisabled
            ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) : null);

        _slideVersion++;
        int version = _slideVersion;
        ClearSnapshot();
        // The title's shade keeps its state while the pages switch and fades to the new one once they have.
        _scrimHeld = IsLoaded;
        bool slide = (push || pop) && IsLoaded && !SettingsService.Current.EnablePerformanceMode && !FluidMotion.AnimationsDisabled;
        var snapshot = slide ? SnapshotContent() : null;

        // Every pane opens at the top, except a parent being returned to.
        SmoothScrollBehavior.Stop(TabContentScroll);
        if (old != null)
        {
            old.BeginAnimation(OpacityProperty, null);
            old.Visibility = Visibility.Collapsed;
        }
        target.Visibility = Visibility.Visible;
        if (restoreOffset > 0)
        {
            TabContentScroll.UpdateLayout();
            TabContentScroll.ScrollToVerticalOffset(restoreOffset);
        }
        else TabContentScroll.ScrollToTop();
        OnPaneShown(target, sliding: snapshot != null);

        if (SettingsService.Current.EnablePerformanceMode || !IsLoaded)
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = 1;
            target.RenderTransform = Transform.Identity;
            ReleaseHeaderScrim(version);
            return;
        }

        if (snapshot != null)
        {
            FluidMotion.SlidePage(snapshot, target, TabContentScroll.ActualWidth, forward: push,
                () => { if (version == _slideVersion) ClearSnapshot(); ReleaseHeaderScrim(version); });
            return;
        }

        // A short fade and settle, like switching panes in System Settings.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var move = new TranslateTransform(0, 6);
        target.RenderTransform = move;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        var settle = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        settle.Completed += (_, _) => ReleaseHeaderScrim(version);
        move.BeginAnimation(TranslateTransform.YProperty, settle);
    }

    /// <summary>The page switch <paramref name="version"/> is done: the title's shade may follow the new page now.</summary>
    private void ReleaseHeaderScrim(int version)
    {
        if (version != _slideVersion) return;  // a newer switch holds it
        _scrimHeld = false;
        UpdateHeaderScrim();
    }

    private bool IsSubPage(StackPanel pane) =>
        pane == PaneLiquidGlass || pane == PaneGlassReadability || pane == PaneGlassTextColor || pane == PaneColors || pane == PaneVideoColors || pane == PaneWindowGlass
        || pane == PaneToolbar || pane == PaneWindowSizes || pane == PaneMotion || pane == PaneCloudConfig || pane == PaneWhatsNew || pane == PaneVersion || pane == PaneRelease
        || pane == PaneUpdate;

    /// <summary>Switches to a pane by name: "update" (General → Software Update), "cloud-config" or "cloud-config:dropbox" (Cloud → Configuration on a service), "appearance", "glass", "animations", "advanced", "download", "upload" or "cloud".</summary>
    public void SelectPane(string name)
    {
        if (name.StartsWith("cloud-config", StringComparison.Ordinal))
        {
            NavCloud.IsChecked = true;
            OpenCloudConfig(name.Length > 13 ? name[13..] : null);
            return;
        }
        if (name == "update")
        {
            NavGeneral.IsChecked = true;
            if (_activePane != PaneUpdate) OpenSubPage(PaneUpdate, PaneGeneral, "PrefSoftwareUpdate");
            return;
        }
        var nav = name switch
        {
            "appearance" => NavAppearance,
            "glass" => NavGlassLayout,
            "animations" => NavAnimations,
            "advanced" => NavAdvanced,
            "download" => NavDownloads,
            "upload" => NavYouTube,
            "cloud" => NavCloud,
            _ => null,
        };
        if (nav != null) nav.IsChecked = true;
    }

    /// <summary>The Developer pane is listed only while Developer Mode is on.</summary>
    private void UpdateDeveloperNav()
    {
        bool show = SettingsService.Current.EnableDeveloperMode;
        NavDeveloper.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show && NavDeveloper.IsChecked == true)
            NavAdvanced.IsChecked = true;
    }

    // ═══ Downloads ═══

    private void UpdateFolderRows()
    {
        var s = SettingsService.Current;
        // Without a saved folder downloads go to the Videos folder.
        TxtDefaultOutputDir.Text = string.IsNullOrWhiteSpace(s.DefaultOutputDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            : s.DefaultOutputDir;
        TxtDefaultOutputDir.ToolTip = TxtDefaultOutputDir.Text;

        bool hasCookies = !string.IsNullOrWhiteSpace(s.CookiesFilePath);
        TxtCookiesFilePath.Text = hasCookies ? System.IO.Path.GetFileName(s.CookiesFilePath) : L("PrefCookiesNone");
        TxtCookiesFilePath.ToolTip = hasCookies ? s.CookiesFilePath : null;
        BtnClearCookies.Visibility = hasCookies ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenGlassTuner_Click(object s, RoutedEventArgs e) => GlassTunerWindow.ShowTuner(Owner ?? this);

    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L("PrefSaveTo") };
        if (!string.IsNullOrWhiteSpace(SettingsService.Current.DefaultOutputDir))
            dlg.InitialDirectory = SettingsService.Current.DefaultOutputDir;
        if (dlg.ShowDialog(this) != true) return;
        Commit(st => st.DefaultOutputDir = dlg.FolderName);
        UpdateFolderRows();
    }

    private void BrowseCookiesFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L("PrefCookies"),
            Filter = "cookies.txt (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        Commit(s => s.CookiesFilePath = dlg.FileName);
        UpdateFolderRows();
    }

    private void ClearCookiesFile_Click(object sender, RoutedEventArgs e)
    {
        Commit(s => s.CookiesFilePath = "");
        UpdateFolderRows();
    }

    // ═══ Engine ═══

    /// <summary>
    /// Switches the media engine. While a download or upload is running the switch waits until it is
    /// finished; a toast says which of the two happened.
    /// </summary>
    private void ApplyEngineSelection(string engine)
    {
        var name = EngineProvider.DisplayName(engine);
        bool applied = EngineProvider.SwitchEngine(engine);
        NotificationWindow.Show(string.Format(L(applied ? "EngineSwitchApplied" : "EngineSwitchDeferred"), name), Owner ?? this, NotificationType.Info);
        UpdateEngineInfo();
    }

    private void UpdateEngineInfo()
    {
        TxtInfoBuildSha.Text = BuildInfoService.BuildNumberWithEngine;
        TxtInfoEngine.Text = BuildInfoService.ExecutionEngine;
    }

    // ═══ Alert sheet ═══

    private Action? _sheetConfirm;
    private Action? _sheetCancel;

    private void ShowSheet(string title, string message, string confirmText, bool destructive, Action onConfirm, Action? onCancel = null)
    {
        SheetTitle.Text = title;
        SheetMessage.Text = message;
        SheetConfirm.Content = confirmText;
        if (destructive) SheetConfirm.SetResourceReference(BackgroundProperty, "ErrorRed");
        else SheetConfirm.SetResourceReference(BackgroundProperty, "Accent");
        _sheetConfirm = onConfirm;
        _sheetCancel = onCancel;

        Sheet.Visibility = Visibility.Visible;
        if (SettingsService.Current.EnablePerformanceMode) return;
        var scale = (ScaleTransform)SheetCard.RenderTransform;
        if (Liquid.IsActive)
        {
            // The card blooms open over the dimmed page.
            Liquid.To(Sheet, OpacityProperty, 1, new Spring(0.2, 1), from: 0);
            Liquid.To(scale, ScaleTransform.ScaleXProperty, 1, Spring.Bouncy, from: 0.86);
            Liquid.To(scale, ScaleTransform.ScaleYProperty, 1, Spring.Bouncy.Slower(1.08), from: 0.82, delayMs: 16);
            return;
        }
        var spring = AppleSpringEase.Interactive;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
        Sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void HideSheet()
    {
        Sheet.Visibility = Visibility.Collapsed;
        _sheetConfirm = null;
        _sheetCancel = null;
    }

    private void SheetConfirm_Click(object sender, RoutedEventArgs e)
    {
        var action = _sheetConfirm;
        HideSheet();
        action?.Invoke();
    }

    private void SheetCancel_Click(object sender, RoutedEventArgs e)
    {
        var action = _sheetCancel;
        HideSheet();
        action?.Invoke();
    }

    private void SheetBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => SheetCancel_Click(sender, e);

    // ═══ Reduce visual effects (needs a restart) ═══

    private void ChkPerformanceMode_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = ChkPerformanceMode.IsChecked == true;
        if (wanted == SettingsService.Current.EnablePerformanceMode) return;

        ShowSheet(L("PrefRestartTitle"), L("PrefRestartMessage"), L("PrefRestart"), false,
            onConfirm: () =>
            {
                Commit(s => s.EnablePerformanceMode = wanted);
                RestartApp();
            },
            onCancel: () => ChkPerformanceMode.IsChecked = SettingsService.Current.EnablePerformanceMode);
    }

    private void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            NotificationWindow.Show(string.Format(L("PrefRestartFailed"), ex.Message), this, NotificationType.Error);
        }
    }

    // ═══ Reset All ═══

    private void ResetAll_Click(object s, RoutedEventArgs e)
    {
        ShowSheet(L("PrefResetTitle"), L("PrefResetMessage"), L("PrefReset"), true, () =>
        {
            var before = SettingsService.Current;
            var defaults = new AppSettings();
            _committing = true;
            try { SettingsService.Save(defaults); }
            finally { _committing = false; }
            HasChanges = true;

            if (defaults.Language != before.Language) LocalizationService.ApplyLanguage(defaults.Language);
            if (defaults.ExecutionEngine != before.ExecutionEngine) ApplyEngineSelection(defaults.ExecutionEngine);
            if (Owner != null) Owner.Topmost = defaults.AlwaysOnTop;
            ReloadAll();
        });
    }

    // ═══ Developer ═══

    private void UpdateFfmpegStatusUi()
    {
        bool installed = FFmpegInstallerService.IsFfmpegInstalled(out var ffmpegPath, out _) && !string.IsNullOrEmpty(ffmpegPath);
        TxtFfmpegStatusTitle.Text = L(installed ? "PrefFfmpegInstalled" : "PrefFfmpegMissing");
        TxtFfmpegStatusPath.Text = installed ? ffmpegPath : FFmpegInstallerService.GetDefaultInstallDir();
        TxtFfmpegStatusPath.ToolTip = TxtFfmpegStatusPath.Text;
        BtnUninstallFfmpeg.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        BtnInstallFfmpeg.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UninstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        ShowSheet(L("PrefFfmpegUninstallTitle"), L("PrefFfmpegUninstallMessage"), L("PrefUninstallConfirm"), true, () =>
        {
            bool ok = FFmpegInstallerService.UninstallFfmpeg(true);
            UpdateFfmpegStatusUi();
            NotificationWindow.Show(L(ok ? "NotifyFfmpegUninstalled" : "PrefFfmpegUninstallFailed"), this,
                ok ? NotificationType.Info : NotificationType.Warning);
        });
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        bool installed = await FFmpegInstallerService.ShowInstallerDialogAsync(this);
        UpdateFfmpegStatusUi();
        if (installed)
            NotificationWindow.Show(L("NotifyFfmpegInstalled"), this, NotificationType.Info);
    }

    private void ClearDevCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VideoConverter");
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, true);
            NotificationWindow.Show(L("PrefTempCleared"), this, NotificationType.Info);
        }
        catch (Exception ex)
        {
            NotificationWindow.Show(string.Format(L("PrefTempClearFailed"), ex.Message), this, NotificationType.Error);
        }
    }

    private void CopyDevDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;
        try
        {
            var summary = "VideoConverter diagnostics\n" +
                          $"Version: {BuildInfoService.BuildNumberWithEngine}\n" +
                          $"OS: {Environment.OSVersion}\n" +
                          $"64-bit process: {Environment.Is64BitProcess}\n" +
                          $"CLR: {Environment.Version}\n" +
                          $"Developer mode: {s.EnableDeveloperMode}\n" +
                          $"Console: {s.DevConsoleEnabled}\n" +
                          $"Legacy engine address: {s.BackendUrl}\n" +
                          $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            Clipboard.SetText(summary);
            NotificationWindow.Show(L("PrefDiagCopied"), this, NotificationType.Info);
        }
        catch (Exception ex)
        {
            NotificationWindow.Show(string.Format(L("PrefDiagCopyFailed"), ex.Message), this, NotificationType.Error);
        }
    }

    private async void TestBackendConnection_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = SettingsService.Current.BackendUrl.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://127.0.0.1:8765";
        var healthUrl = baseUrl.EndsWith("/api/health") ? baseUrl : $"{baseUrl}/api/health";

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var response = await client.GetAsync(healthUrl);
            if (response.IsSuccessStatusCode)
                NotificationWindow.Show(L("PrefServiceOnline"), this, NotificationType.Info);
            else
                NotificationWindow.Show(string.Format(L("PrefServiceStatus"), (int)response.StatusCode), this, NotificationType.Warning);
        }
        catch
        {
            NotificationWindow.Show(string.Format(L("PrefServiceOffline"), baseUrl), this, NotificationType.Error);
        }
    }

    // ═══ Helpers ═══

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Tag?.ToString() == tag) { combo.SelectedItem = item; return; }
    }

    private static readonly int[] NotificationDurationOptions = { 2, 3, 4, 5, 6, 8, 10, 15, 30, 0 };

    // Fills a banner duration menu (seconds; 0 = until dismissed) and selects the current value.
    private static void PopulateDurationCombo(ComboBox combo, int selectedSeconds)
    {
        combo.Items.Clear();
        var options = NotificationDurationOptions.Contains(selectedSeconds)
            ? NotificationDurationOptions
            : NotificationDurationOptions.Append(selectedSeconds).OrderBy(v => v == 0 ? int.MaxValue : v).ToArray();
        foreach (var sec in options)
        {
            var item = new ComboBoxItem { Tag = sec.ToString(CultureInfo.InvariantCulture) };
            if (sec == 0)
                item.SetResourceReference(ContentControl.ContentProperty, "SettingsNotifyDurationUntilDismissed");
            else
                item.Content = string.Format(L("PrefSeconds"), sec);
            combo.Items.Add(item);
        }
        SelectComboByTag(combo, selectedSeconds.ToString(CultureInfo.InvariantCulture));
    }

    private static string L(string key)
        => Application.Current.Resources[key] as string ?? key;
}
