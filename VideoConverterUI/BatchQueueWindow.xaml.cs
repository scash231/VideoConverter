using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public partial class BatchQueueWindow : Window
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" } }
    };
    private static readonly ConcurrentDictionary<string, ImageSource> _thumbnailCache = new();
    private static readonly ConcurrentDictionary<string, Color> _colorCache = new();
    private static Brush SkeletonCardBrush => Application.Current?.TryFindResource("Tint12") as Brush ?? Brushes.Transparent;

    private static readonly SemaphoreSlim _thumbnailThrottler = new(4, 4);
    private CancellationTokenSource _fetchCts = new();
    private readonly IEngineService _backend;
    private readonly ObservableCollection<BatchQueueItemViewModel> _items = [];
    private readonly ObservableCollection<BatchQueueItemViewModel> _storedItems = [];
    private readonly Rect _originRect;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;
    private bool _isAnimatingClose;
    private bool _isClosingBatchOptions;
    private DateTime _batchOptionsClosedTime = DateTime.MinValue;
    private int _logLineCount;
    private int _liveLogLength;
    private int _logFlushScheduled;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private const int MaxLiveLogChars = 60_000;
    private int _maxConcurrent = 2;
    private bool _isUiInitialized;
    private bool _isSplitStorageActive;

    public BatchQueueWindow(IEngineService? backend = null, string? initialUrl = null, Rect originRect = default)
    {
        InitializeComponent();
        _backend = backend ?? EngineProvider.Current;
        _originRect = originRect;
        QueueItemsList.ItemsSource = _items;
        if (StoredItemsList != null) StoredItemsList.ItemsSource = _storedItems;
        _items.CollectionChanged += OnItemsCollectionChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;

        // Default save path
        var defaultDir = SettingsService.Current.DefaultOutputDir;
        if (string.IsNullOrWhiteSpace(defaultDir) || !Directory.Exists(defaultDir))
            defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        TxtBatchOutputDir.Text = defaultDir;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            TxtPlaylistUrl.Text = initialUrl;
            Loaded += async (_, _) => await AddUrlsWithSkeletonPreviewAsync(initialUrl);
        }

        UpdateBadge();
        LogMessage("Multi Downloader initialized. Ready to process video queues.", "SYSTEM");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FluidMotion.MorphOpen(RootBorder, WindowScale, WindowTranslate, _originRect, this);
        SettingsService.ApplyResizeGripVisibility(this);
        EngineProvider.OutputReceived += OnEngineOutput;

        var s = SettingsService.Current;

        // Initialize batch quality
        var qualityTag = s.BatchQuality ?? "best";
        foreach (ComboBoxItem item in CmbBatchQuality.Items)
        {
            if (string.Equals(item.Tag?.ToString(), qualityTag, StringComparison.OrdinalIgnoreCase))
            {
                CmbBatchQuality.SelectedItem = item;
                break;
            }
        }

        // Initialize concurrency
        _maxConcurrent = Math.Clamp(s.BatchMaxConcurrency, 1, 30);
        foreach (ComboBoxItem item in CmbMaxConcurrency.Items)
        {
            if (string.Equals(item.Tag?.ToString(), _maxConcurrent.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbMaxConcurrency.SelectedItem = item;
                break;
            }
        }

        // Initialize auto-retry, auto-remove, and per-video quality
        ChkAutoRetry.IsChecked = s.BatchAutoRetry;
        ChkAutoRemoveDone.IsChecked = s.BatchAutoRemoveDone;
        ChkShowPerVideoQuality.IsChecked = s.ShowPerVideoQuality;

        // Initialize cloud toggle and provider from settings
        var cloudEnabled = s.CloudUploadEnabled;
        ChkBatchCloudUpload.IsChecked = cloudEnabled;
        ChkPopupCloudUpload.IsChecked = cloudEnabled;
        PanelPopupCloudSettings.Visibility = cloudEnabled ? Visibility.Visible : Visibility.Collapsed;
        ChkPopupCloudTempOnly.IsChecked = s.CloudTempDownload;

        var providerTag = s.CloudProvider ?? "gdrive";
        foreach (ComboBoxItem item in CmbPopupCloudProvider.Items)
        {
            if (string.Equals(item.Tag?.ToString(), providerTag, StringComparison.OrdinalIgnoreCase))
            {
                CmbPopupCloudProvider.SelectedItem = item;
                break;
            }
        }

        UpdateCloudToolbarUi();
        UpdateOptionsTextVisibility();
        InitFrostedBars();
        TitleBackdrop.SizeChanged += (_, _) => FitLinkField();
        LayoutUpdated += (_, _) => UpdateFetchReflection();
        SettingsService.SettingsChanged += OnSettingsChanged;
        _isUiInitialized = true;
        RestoreBatchSession();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _fetchCts.Cancel();
            _fetchCts.Dispose();
        }
        catch { }
        EngineProvider.OutputReceived -= OnEngineOutput;
        SettingsService.SettingsChanged -= OnSettingsChanged;
    }

    // ════════════════════════════════════════════════════════════
    //  FLOATING BARS (header: link bar + toolbar, footer: progress + actions)
    //  The glass comes from LiquidGlassPanel (surface "Toolbar"); this keeps the queue content
    //  spaced and clipped so it only ever shows underneath the glass.
    // ════════════════════════════════════════════════════════════

    private Thickness _listInsets = new(-1);

    private void InitFrostedBars()
    {
        // Only sizes move the bars or the lists (not after every layout pass: live progress causes plenty of those).
        foreach (var element in new FrameworkElement[] { HeaderHost, FooterHost, QueueSection, QueueItemsList, StoredItemsList })
            element.SizeChanged += (_, _) => UpdateBarInsets();
        QueueItemsList.IsVisibleChanged += (_, _) => UpdateBarInsets();
        StoredItemsList.IsVisibleChanged += (_, _) => UpdateBarInsets();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateBarInsets);
    }

    private void UpdateBarInsets()
    {
        if (!IsLoaded || FooterHost.ActualHeight <= 0 || HeaderHost.ActualHeight <= 0) return;

        // Cards may only show under the glass, not in the thin gaps above the header / below the footer.
        ClipBetweenBars(QueueItemsList, clipTop: true);
        ClipBetweenBars(StoredItemsList, clipTop: false);

        // Space the content so the first card starts below the header and the last one can scroll above the footer.
        double top = Math.Round(HeaderHost.Margin.Top + HeaderHost.ActualHeight + 2);
        double bottom = Math.Round(FooterHost.ActualHeight + 14);
        var insets = new Thickness(0, top, 0, bottom);
        if (insets == _listInsets) return;
        _listInsets = insets;

        SetListInsets(QueueItemsList, top, bottom);
        SetListInsets(StoredItemsList, 0, bottom);
        StoredPanelContainer.Padding = new Thickness(6, top, 6, 6);
        EmptyQueueState.Margin = new Thickness(0, top, 0, bottom);
        EmptyStoredState.Margin = new Thickness(10, 0, 10, bottom);
        DropOverlay.Margin = new Thickness(0, top - 2, 0, bottom - 6);
    }

    private void ClipBetweenBars(ListBox list, bool clipTop)
    {
        if (!list.IsVisible || list.ActualWidth <= 0) return;
        double top = clipTop ? HeaderHost.TransformToVisual(list).Transform(new Point(0, 0)).Y : 0;
        double bottom = FooterHost.TransformToVisual(list).Transform(new Point(0, FooterHost.ActualHeight)).Y;
        var rect = new Rect(0, Math.Max(0, top), list.ActualWidth, Math.Max(0, bottom - Math.Max(0, top)));
        if (list.Clip is RectangleGeometry g && g.Rect == rect) return;
        list.Clip = new RectangleGeometry(rect, 14, 14);
    }

    private static void SetListInsets(ListBox list, double top, double bottom)
    {
        list.ApplyTemplate();
        if (list.Template?.FindName("ItemsHost", list) is ItemsPresenter presenter)
            presenter.Margin = new Thickness(0, top, 0, bottom);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            SettingsService.ApplyResizeGripVisibility(this);
            UpdateOptionsTextVisibility();
            UpdateCloudToolbarUi();
        });
    }

    /// <summary>The title-bar pills' labels follow "Show icon labels"; the add-links one also hides while its field is open.</summary>
    private void UpdateOptionsTextVisibility()
    {
        bool show = SettingsService.Current.ShowIconLabels;
        bool animate = _isUiInitialized;
        PillMotion.SetShown(TxtBatchOptionsLabel, show, animate, pop: false);
        PillMotion.SetShown(TxtSaveAndExitLabel, show, animate, pop: false);
        PillMotion.SetShown(TxtAddLinksLabel, show && _linkField == LinkField.Closed, animate, pop: false);
    }

    private void OnEngineOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogMessage(line.Trim(), "ENGINE");
    }

    /// <summary>
    /// Thread-safe. With several parallel downloads the engine produces dozens of lines per second,
    /// so lines are queued and appended in one batch per dispatcher pass.
    /// </summary>
    public void LogMessage(string message, string level = "INFO")
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FlushLog);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        if (TxtLiveLog == null || _pendingLog.IsEmpty) return;

        var chunk = new System.Text.StringBuilder();
        int lines = 0;
        while (_pendingLog.TryDequeue(out var line))
        {
            chunk.Append(line).Append(Environment.NewLine);
            lines++;
        }

        // TextBox.Text copies the whole log, so only read it when the log actually needs trimming.
        if (_liveLogLength + chunk.Length > MaxLiveLogChars)
        {
            var current = TxtLiveLog.Text;
            var cutIndex = current.IndexOf('\n', Math.Min(current.Length, Math.Max(15000, current.Length + chunk.Length - MaxLiveLogChars)));
            TxtLiveLog.Text = cutIndex > 0 ? current[(cutIndex + 1)..] : string.Empty;
            _liveLogLength = TxtLiveLog.Text.Length;
        }

        TxtLiveLog.AppendText(chunk.ToString());
        _liveLogLength += chunk.Length;
        _logLineCount += lines;
        if (TxtLogLineCount != null) TxtLogLineCount.Text = $"{_logLineCount} lines";
        TxtLiveLog.ScrollToEnd();
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void WindowCornerGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            SettingsService.StartWindowResizeBottomRight(this);
            e.Handled = true; // no drag-move of the window underneath
        }
    }

    private void WindowCornerGrip_MouseEnter(object sender, MouseEventArgs e)
    {
        SettingsService.HandleGripHover(sender, true);
    }

    private void WindowCornerGrip_MouseLeave(object sender, MouseEventArgs e)
    {
        SettingsService.HandleGripHover(sender, false);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private bool _hasExplicitlySavedOrClosed;

    private static string SessionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoConverter",
        "batch_session.json"
    );

    private void SaveAndExit_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimatingClose) return;

        if (_isDownloading)
        {
            _downloadCts?.Cancel();
        }

        var mainWin = Application.Current?.MainWindow;
        if (SaveBatchSession())
        {
            NotificationWindow.Show("Batch progress saved successfully!", mainWin, NotificationType.Success);
        }
        _hasExplicitlySavedOrClosed = true;
        CloseWithAnimation();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimatingClose) return;

        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            NotificationWindow.Show("Batch download cancelled.", this, NotificationType.Warning);
        }
        CloseWithAnimation();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            base.OnClosing(e);
            return;
        }

        if (!_isAnimatingClose)
        {
            e.Cancel = true;
            CloseWithAnimation();
            return;
        }
        base.OnClosing(e);
    }

    private bool PromptAndSaveIfUnsaved()
    {
        if (_hasExplicitlySavedOrClosed) return true;

        var totalItems = _items.Count + _storedItems.Count;
        if (totalItems == 0)
        {
            ClearBatchSessionFile();
            return true;
        }

        if (!SettingsService.Current.PromptSaveBatchOnExit)
        {
            ClearBatchSessionFile();
            _hasExplicitlySavedOrClosed = true;
            return true;
        }

        var dialog = new BatchSavePromptDialog
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.NeverAskAgain)
        {
            SettingsService.Current.PromptSaveBatchOnExit = false;
            SettingsService.Save(SettingsService.Current);
        }

        if (dialog.Result == BatchSaveResult.SaveAndExit)
        {
            var mainWin = Application.Current?.MainWindow;
            SaveBatchSession();
            NotificationWindow.Show("Batch progress saved successfully!", mainWin, NotificationType.Success);
            _hasExplicitlySavedOrClosed = true;
            return true;
        }
        else if (dialog.Result == BatchSaveResult.ExitWithoutSaving)
        {
            ClearBatchSessionFile();
            _hasExplicitlySavedOrClosed = true;
            return true;
        }
        else
        {
            return false;
        }
    }

    private void CloseWithAnimation()
    {
        if (_isAnimatingClose) return;

        if (!PromptAndSaveIfUnsaved())
        {
            return;
        }

        _isAnimatingClose = true;

        FluidMotion.MorphClose(RootBorder, WindowScale, WindowTranslate, this, () =>
        {
            try { Close(); } catch { }
        });
    }

    public bool SaveBatchSession()
    {
        try
        {
            var session = new BatchSessionData
            {
                SavedAt = DateTime.Now,
                OutputDirectory = TxtBatchOutputDir?.Text ?? "",
                Quality = (CmbBatchQuality?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "best",
                MaxConcurrency = _maxConcurrent,
                AutoRetry = ChkAutoRetry?.IsChecked ?? true,
                AutoRemoveDone = ChkAutoRemoveDone?.IsChecked ?? false,
                CloudUploadEnabled = ChkBatchCloudUpload?.IsChecked ?? false,
                Items = _items.Select(ToDto).ToList(),
                StoredItems = _storedItems.Select(ToDto).ToList()
            };

            var dir = Path.GetDirectoryName(SessionFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionFilePath, json);

            LogMessage($"Saved batch queue session to disk ({session.Items.Count + session.StoredItems.Count} items).", "SYSTEM");
            return true;
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to save batch session: {ex.Message}", "ERROR");
            return false;
        }
    }

    private void RestoreBatchSession()
    {
        try
        {
            if (!File.Exists(SessionFilePath)) return;

            var json = File.ReadAllText(SessionFilePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var session = System.Text.Json.JsonSerializer.Deserialize<BatchSessionData>(json);
            if (session == null) return;

            if (session.Items != null && session.Items.Count > 0)
            {
                foreach (var dto in session.Items)
                {
                    var vm = FromDto(dto);
                    _items.Add(vm);
                    if (!string.IsNullOrWhiteSpace(dto.ThumbnailUrl))
                    {
                        LoadThumbnailAsync(vm, dto.ThumbnailUrl);
                    }
                }
            }

            if (session.StoredItems != null && session.StoredItems.Count > 0)
            {
                foreach (var dto in session.StoredItems)
                {
                    var vm = FromDto(dto);
                    _storedItems.Add(vm);
                    if (!string.IsNullOrWhiteSpace(dto.ThumbnailUrl))
                    {
                        LoadThumbnailAsync(vm, dto.ThumbnailUrl);
                    }
                }
            }

            var total = (session.Items?.Count ?? 0) + (session.StoredItems?.Count ?? 0);
            if (total > 0)
            {
                UpdateBadge();
                LogMessage($"Restored previous batch session ({total} items restored).", "SYSTEM");
                NotificationWindow.Show($"Restored {total} saved batch queue items.", this, NotificationType.Info);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to restore batch session: {ex.Message}", "ERROR");
        }
    }

    private static void ClearBatchSessionFile()
    {
        try
        {
            if (File.Exists(SessionFilePath))
                File.Delete(SessionFilePath);
        }
        catch { }
    }

    private static BatchItemDto ToDto(BatchQueueItemViewModel vm)
    {
        return new BatchItemDto
        {
            Id = vm.Id ?? "",
            Url = vm.Url ?? "",
            Title = vm.Title ?? "",
            Uploader = vm.Uploader ?? "",
            ThumbnailUrl = vm.ThumbnailUrl ?? "",
            Duration = vm.Duration,
            IsSelected = vm.IsSelected,
            IsCompleted = vm.IsCompleted,
            HasError = vm.HasError,
            ErrorMessage = vm.ErrorMessage ?? "",
            Progress = vm.Progress,
            StatusText = vm.StatusText ?? "Pending",
            IsStored = vm.IsStored,
            SelectedQuality = vm.SelectedQuality ?? ""
        };
    }

    private static BatchQueueItemViewModel FromDto(BatchItemDto dto)
    {
        var vm = new BatchQueueItemViewModel
        {
            Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
            Url = dto.Url ?? "",
            Title = string.IsNullOrWhiteSpace(dto.Title) ? (dto.Url ?? "Video Item") : dto.Title,
            Uploader = dto.Uploader ?? "",
            ThumbnailUrl = dto.ThumbnailUrl ?? "",
            Duration = dto.Duration,
            IsSelected = dto.IsSelected,
            IsCompleted = dto.IsCompleted,
            HasError = dto.HasError,
            ErrorMessage = dto.ErrorMessage ?? "",
            Progress = dto.Progress,
            StatusText = dto.StatusText ?? "Pending",
            IsStored = dto.IsStored,
            SelectedQuality = dto.SelectedQuality ?? ""
        };

        if (vm.IsCompleted)
        {
            vm.RefreshState();
        }
        else if (vm.HasError)
        {
            vm.RefreshState();
        }

        return vm;
    }

    // ════════════════════════════════════════════════════════════
    //  DRAG & DROP SUPPORT WITH INSTANT SKELETON PREVIEWS
    // ════════════════════════════════════════════════════════════

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (IsLinkOrFileDrag(e))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            ShowDropOverlay(true);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (IsLinkOrFileDrag(e))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // DragLeave also fires when the pointer crosses between child elements; only hide once it has left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            ShowDropOverlay(false);
    }

    private void ShowDropOverlay(bool show)
    {
        if (DropOverlay == null) return;
        if (show) DropOverlay.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (!show)
            fade.Completed += (_, _) => { if (DropOverlay.Opacity == 0) DropOverlay.Visibility = Visibility.Collapsed; };
        DropOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ShowDropOverlay(false);
        if (!IsLinkOrFileDrag(e)) return;

        var text = ExtractDropText(e);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await AddUrlsWithSkeletonPreviewAsync(text);
        }
    }

    private static bool IsLinkOrFileDrag(DragEventArgs e)
    {
        return e.Data.GetDataPresent(DataFormats.UnicodeText) ||
               e.Data.GetDataPresent(DataFormats.Text) ||
               e.Data.GetDataPresent(DataFormats.StringFormat) ||
               e.Data.GetDataPresent(DataFormats.FileDrop);
    }

    private static string ExtractDropText(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            return (e.Data.GetData(DataFormats.UnicodeText) as string) ?? string.Empty;
        if (e.Data.GetDataPresent(DataFormats.Text))
            return (e.Data.GetData(DataFormats.Text) as string) ?? string.Empty;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                if (File.Exists(file) && (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        sb.AppendLine(File.ReadAllText(file));
                    }
                    catch { }
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    // Ctrl+V adds links, Ctrl+A selects everything, Ctrl+Enter starts, Esc closes the open panel.
    // Plain Ctrl+V / Ctrl+A keep their normal meaning while typing in a text box.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool inTextBox = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.V && !inTextBox)
        {
            Paste_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A && !inTextBox)
        {
            SelectAll_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Enter)
        {
            if (BtnStartBatchDownload.IsEnabled && !_isDownloading)
                StartBatchDownload_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (BatchOptionsPopup.IsOpen) { CloseBatchOptionsAnimated(); e.Handled = true; }
            else if (_isLiveLogOpen) { SetLiveLogVisibility(false); e.Handled = true; }
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText().Trim();
            TxtPlaylistUrl.Text = text;
            LogMessage($"Pasted input: {text}", "INPUT");
            _ = AddUrlsWithSkeletonPreviewAsync(text);
        }
    }

    private async void TxtPlaylistUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddUrlsWithSkeletonPreviewAsync(TxtPlaylistUrl.Text.Trim());
        }
        else if (e.Key == Key.Escape && _linkField == LinkField.Open)
        {
            TxtPlaylistUrl.Clear();
            SetLinkField(LinkField.Closed);
            e.Handled = true;
        }
    }

    private async void FetchPlaylist_Click(object sender, RoutedEventArgs e)
    {
        await AddUrlsWithSkeletonPreviewAsync(TxtPlaylistUrl.Text.Trim());
    }

    public Task FetchPlaylistOrUrlsAsync(string input) => AddUrlsWithSkeletonPreviewAsync(input);

    public async Task AddUrlsWithSkeletonPreviewAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var lines = input.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     l.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                     l.Contains("youtube.com") || l.Contains("youtu.be") || l.Contains("instagram.com") || l.Contains("tiktok.com"))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        if (lines.Count == 0)
        {
            var trimmed = input.Trim();
            if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("youtube.com") || trimmed.Contains("youtu.be"))
            {
                lines.Add("https://" + trimmed.TrimStart('/'));
            }
            else
            {
                LogMessage($"No valid video URLs found in input: {input}", "WARN");
                NotificationWindow.Show("No valid video URLs found.", this, NotificationType.Warning);
                return;
            }
        }

        LogMessage($"Adding {lines.Count} item(s) to queue...", "FETCH");

        bool currentCloud = ChkBatchCloudUpload?.IsChecked == true;
        var currentProvider = GetCurrentProviderKey();

        // 1. Immediately create greyed-out skeleton placeholder items in queue for instant visual feedback
        var skeletons = new List<(string Url, BatchQueueItemViewModel Item)>();
        foreach (var url in lines)
        {
            if (_items.Any(i => i.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                continue;

            var skeleton = new BatchQueueItemViewModel
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Title = "Loading video details...",
                Uploader = url,
                Url = url,
                IsLoading = true,
                StatusText = "Fetching...",
                StatusColor = (Brush)FindResource("TextSec"),
                CardBackground = SkeletonCardBrush
            };
            skeleton.UpdateCloudConfig(currentCloud, currentProvider);
            _items.Add(skeleton);
            skeletons.Add((url, skeleton));
        }

        UpdateBadge();
        if (skeletons.Count == 0)
        {
            // Everything was already queued: nothing to load, so the field folds back straight away.
            if (_linkFetches == 0)
            {
                TxtPlaylistUrl.Clear();
                SetLinkField(LinkField.Closed);
            }
            return;
        }

        // The pill shrinks to its loading capsule (also for pasted or dropped links), then folds away.
        _linkFetches++;
        SetLinkField(LinkField.Loading);

        var ct = _fetchCts.Token;
        using var gate = new SemaphoreSlim(3);

        async Task LoadOneAsync(string url, BatchQueueItemViewModel skeleton)
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                // Playlist probing costs an extra yt-dlp run, so skip it for plain video links.
                var playlist = LooksLikeSingleVideo(url) ? null : await _backend.FetchPlaylistAsync(url, 150, ct);
                if (playlist != null && playlist.Entries.Count > 1)
                {
                    int index = _items.IndexOf(skeleton);
                    if (index >= 0) _items.RemoveAt(index);
                    else index = _items.Count;

                    int added = 0;
                    foreach (var entry in playlist.Entries)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (_items.Any(i => i.Url.Equals(entry.Url, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var item = new BatchQueueItemViewModel
                        {
                            Id = entry.Id,
                            Title = entry.Title,
                            Uploader = entry.Uploader,
                            Url = entry.Url,
                            Duration = entry.Duration,
                            StatusText = "Pending",
                            StatusColor = (Brush)FindResource("TextSec"),
                            IsLoading = false,
                            CardBackground = SkeletonCardBrush
                        };
                        item.UpdateCloudConfig(currentCloud, currentProvider);
                        _items.Insert(index++, item);
                        LoadThumbnailAsync(item, entry.Thumbnail);
                        added++;
                    }
                    LogMessage($"Loaded playlist '{playlist.Title}' with {added} videos.", "FETCH");
                }
                else
                {
                    // Single video metadata
                    var meta = await _backend.FetchMetadataAsync(url, ct);
                    if (meta != null)
                    {
                        skeleton.Title = meta.Title;
                        skeleton.Uploader = meta.Uploader;
                        skeleton.Duration = meta.Duration;
                        skeleton.StatusText = "Pending";
                        skeleton.StatusColor = (Brush)FindResource("TextSec");
                        skeleton.IsLoading = false;
                        skeleton.UpdateCloudConfig(currentCloud, currentProvider);
                        LoadThumbnailAsync(skeleton, meta.Thumbnail);
                        LogMessage($"Loaded video metadata: {meta.Title}", "FETCH");
                    }
                    else if (playlist != null && playlist.Entries.Count == 1)
                    {
                        var entry = playlist.Entries[0];
                        skeleton.Id = entry.Id;
                        skeleton.Title = entry.Title;
                        skeleton.Uploader = entry.Uploader;
                        skeleton.Duration = entry.Duration;
                        skeleton.StatusText = "Pending";
                        skeleton.StatusColor = (Brush)FindResource("TextSec");
                        skeleton.IsLoading = false;
                        skeleton.UpdateCloudConfig(currentCloud, currentProvider);
                        LoadThumbnailAsync(skeleton, entry.Thumbnail);
                        LogMessage($"Loaded single video: {entry.Title}", "FETCH");
                    }
                    else
                    {
                        skeleton.Title = url;
                        skeleton.Uploader = "Metadata unavailable";
                        skeleton.StatusText = "Ready";
                        skeleton.StatusColor = (Brush)FindResource("TextSec");
                        skeleton.IsLoading = false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to fetch metadata for {url}: {ex.Message}", "WARN");
                skeleton.Title = url;
                skeleton.Uploader = "Fetch error";
                skeleton.StatusText = "Ready";
                skeleton.StatusColor = (Brush)FindResource("TextSec");
                skeleton.IsLoading = false;
            }
            finally
            {
                gate.Release();
                UpdateBadge();
            }
        }

        try
        {
            // A few metadata lookups run in parallel instead of strictly one after another.
            await Task.WhenAll(skeletons.Select(p => LoadOneAsync(p.Url, p.Item)));
        }
        finally
        {
            _linkFetches--;
            if (_linkFetches == 0)
            {
                TxtPlaylistUrl.Clear();
                SetLinkField(LinkField.Closed);
            }
            UpdateBadge();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  ADD LINKS PILL (title bar)
    //  A glass pill beside Options and Save. Clicked, it grows to the left into the link field. While links load it
    //  shrinks to just the "Loading..." capsule, and folds away completely once they are in. Esc, or leaving the
    //  field empty, folds it back too.
    // ════════════════════════════════════════════════════════════

    private enum LinkField { Closed, Open, Loading }

    private LinkField _linkField;
    private int _linkFetches;  // AddUrlsWithSkeletonPreviewAsync runs in progress
    private const double LinkFieldMin = 150, LinkFieldMax = 440;

    private void AddLinks_Click(object sender, RoutedEventArgs e)
    {
        switch (_linkField)
        {
            case LinkField.Closed: SetLinkField(LinkField.Open); break;
            case LinkField.Open when string.IsNullOrWhiteSpace(TxtPlaylistUrl.Text): SetLinkField(LinkField.Closed); break;
            case LinkField.Open: TxtPlaylistUrl.Focus(); break;
        }
    }

    private void TxtPlaylistUrl_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseLinkFieldIfIdle();

    /// <summary>Folds the field back unless links are loading or something has been typed.</summary>
    private void CloseLinkFieldIfIdle()
    {
        if (_linkField == LinkField.Open && string.IsNullOrWhiteSpace(TxtPlaylistUrl.Text))
            SetLinkField(LinkField.Closed);
    }

    /// <summary>The field's width: what the title bar has left beside the title and the other capsules.</summary>
    private double LinkFieldWidth()
    {
        const double titleRoom = 190;  // keeps the window title readable
        const double iconRoom = 32;    // the pill's link button without its label, plus padding
        double others = TitleActions.ActualWidth - AddLinksGlass.ActualWidth;
        double room = TitleBackdrop.ActualWidth - titleRoom - TitleActions.Margin.Right - others - iconRoom;
        return Math.Clamp(room, LinkFieldMin, LinkFieldMax);
    }

    private void SetLinkField(LinkField state, bool focus = true)
    {
        if (state == _linkField)
        {
            if (state == LinkField.Open && focus) TxtPlaylistUrl.Focus();
            return;
        }
        _linkField = state;
        bool animate = !FluidMotion.AnimationsDisabled && IsLoaded;
        PillMotion.SetShown(TxtAddLinksLabel, state == LinkField.Closed && SettingsService.Current.ShowIconLabels, animate, pop: false);

        bool fromClosed = LinkFieldHost.Visibility != Visibility.Visible;
        double to;
        switch (state)
        {
            case LinkField.Open:
                ShowFetchLoading(false);
                to = LinkFieldWidth();
                LinkFieldContent.Width = to;
                break;
            case LinkField.Loading:
                // Only the Fetch button stays: paste goes (its room passes to the text box, so nothing moves), and the
                // window narrows to the button, which is pinned to its right edge.
                if (double.IsNaN(LinkFieldContent.Width) || fromClosed) LinkFieldContent.Width = LinkFieldWidth();
                ShowFetchLoading(true);
                if (TxtPlaylistUrl.IsKeyboardFocusWithin) Keyboard.ClearFocus();
                BtnFetchPlaylist.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                to = BtnFetchPlaylist.DesiredSize.Width + BtnFetchPlaylist.Margin.Left + BtnFetchPlaylist.Margin.Right;
                break;
            default:
                to = 0;  // the loading look stays while it folds and is reset once it is gone
                break;
        }
        LinkFieldHost.Visibility = Visibility.Visible;

        void Settle()
        {
            LinkFieldHost.BeginAnimation(WidthProperty, null);
            LinkFieldHost.Width = to;
            LinkFieldContent.BeginAnimation(OpacityProperty, null);
            LinkFieldContent.Opacity = state == LinkField.Closed ? 0 : 1;
            if (state != LinkField.Closed)
            {
                if (LinkFieldHost.OpacityMask is LinearGradientBrush edge) FadeOutSoftEdge(edge);
                return;
            }
            LinkFieldHost.OpacityMask = null;
            LinkFieldHost.Visibility = Visibility.Collapsed;
            ShowFetchLoading(false);
        }

        if (!animate)
        {
            Settle();
        }
        else
        {
            // No From: a field still moving turns around where it is. Opening eases out with only a hint of spring;
            // narrowing and folding don't overshoot (that would clip the loading capsule).
            var anim = state == LinkField.Open
                ? new DoubleAnimation(to, TimeSpan.FromMilliseconds(400)) { EasingFunction = AppleSpringEase.Snappy }
                : new DoubleAnimation(to, TimeSpan.FromMilliseconds(state == LinkField.Loading ? 380 : 340)) { EasingFunction = AppleSpringEase.Smooth };
            anim.Completed += (_, _) =>
            {
                if (_linkField == state) Settle();  // otherwise it turned around meanwhile
            };

            // Opening and folding: the moving edge fades softly instead of cutting through the content, and the content
            // fades with it (folding: gone well before the field is, so nothing is left as a hard sliver at the end).
            // Narrowing to the loading capsule keeps everything as it is: the button it ends on must stay crisp.
            // (Pasted or dropped links open it straight into the loading capsule, which then fades in like opening.)
            if (state != LinkField.Loading || fromClosed)
            {
                LinkFieldHost.OpacityMask = CreateSoftEdge();
                LinkFieldContent.BeginAnimation(OpacityProperty, state != LinkField.Closed
                    ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
                    : new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            }
            LinkFieldHost.BeginAnimation(WidthProperty, anim);
        }

        if (state == LinkField.Open && focus)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => TxtPlaylistUrl.Focus());
    }

    private const double SoftEdgeWidth = 18;

    /// <summary>An opacity mask for the field's moving (left) edge: transparent there, opaque from 18 px in.</summary>
    private static LinearGradientBrush CreateSoftEdge() => new(new GradientStopCollection
    {
        new(Colors.Transparent, 0),
        new(Colors.Black, 1),
    }, new Point(0, 0), new Point(SoftEdgeWidth, 0)) { MappingMode = BrushMappingMode.Absolute };

    /// <summary>Once the field has arrived, its soft edge melts away (so the text box's start isn't left faded).</summary>
    private void FadeOutSoftEdge(LinearGradientBrush edge)
    {
        var fade = new ColorAnimation(Colors.Black, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(LinkFieldHost.OpacityMask, edge)) LinkFieldHost.OpacityMask = null;
        };
        edge.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, fade);
    }

    /// <summary>Switches the Fetch button between "Fetch Videos" and "Loading..." with a spinning icon.</summary>
    private void ShowFetchLoading(bool loading)
    {
        BtnPasteLinks.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        BtnFetchPlaylist.IsHitTestVisible = !loading;  // not disabled: that would dim the capsule
        TxtBtnFetch.Text = loading ? "Loading..." : (string)FindResource("BtnLoadPlaylist");
        IconFetchSpinner.Symbol = loading ? Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24 : Wpf.Ui.Controls.SymbolRegular.Add24;

        if (IconFetchSpinner.RenderTransform is not RotateTransform spin || spin.IsFrozen)
            IconFetchSpinner.RenderTransform = spin = new RotateTransform();
        if (loading && !FluidMotion.AnimationsDisabled)
        {
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, null);
            spin.Angle = 0;
        }
    }

    /// <summary>
    /// Keeps the accent stand-in in the title bar's backdrop under the visible part of the Fetch button, so the
    /// neighbouring glass reflects its blue. Runs after every layout pass, so it follows the field's animations.
    /// </summary>
    private void UpdateFetchReflection()
    {
        Rect r = Rect.Empty;
        if (LinkFieldHost.IsVisible && LinkFieldHost.ActualWidth >= 1 && BtnFetchPlaylist.IsVisible)
        {
            try
            {
                var field = LinkFieldHost.TransformToVisual(TitleReflections).TransformBounds(new Rect(LinkFieldHost.RenderSize));
                r = BtnFetchPlaylist.TransformToVisual(TitleReflections).TransformBounds(new Rect(BtnFetchPlaylist.RenderSize));
                r.Intersect(field);  // the field clips the button while it narrows or folds
            }
            catch (InvalidOperationException)
            {
                r = Rect.Empty;  // not connected (yet)
            }
        }

        if (r.IsEmpty || r.Width < 1)
        {
            if (FetchReflection.Visibility != Visibility.Collapsed) FetchReflection.Visibility = Visibility.Collapsed;
            return;
        }
        if (Canvas.GetLeft(FetchReflection) != r.X) Canvas.SetLeft(FetchReflection, r.X);
        if (Canvas.GetTop(FetchReflection) != r.Y) Canvas.SetTop(FetchReflection, r.Y);
        if (FetchReflection.Width != r.Width) FetchReflection.Width = r.Width;
        if (FetchReflection.Height != r.Height) FetchReflection.Height = r.Height;
        FetchReflection.CornerRadius = new CornerRadius(Math.Min(r.Height, r.Width) / 2);
        if (FetchReflection.Visibility != Visibility.Visible) FetchReflection.Visibility = Visibility.Visible;
    }

    /// <summary>Keeps an open field fitted to the title bar while the window is resized.</summary>
    private void FitLinkField()
    {
        if (_linkField != LinkField.Open || LinkFieldHost.HasAnimatedProperties) return;
        LinkFieldHost.Width = LinkFieldContent.Width = LinkFieldWidth();
    }

    private static bool LooksLikeSingleVideo(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("list=") || u.Contains("/playlist") || u.Contains("/sets/"))
            return false;
        return u.Contains("watch?v=") || u.Contains("youtu.be/") || u.Contains("/shorts/")
            || u.Contains("/reel/") || u.Contains("/reels/") || u.Contains("/p/")
            || u.Contains("/video/") || u.Contains("/status/");
    }

    // ════════════════════════════════════════════════════════════
    //  DOMINANT COLOR EXTRACTION & AMBIENT CARD GRADIENT
    // ════════════════════════════════════════════════════════════

    private void LoadThumbnailAsync(BatchQueueItemViewModel item, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        item.ThumbnailUrl = url;

        if (_thumbnailCache.TryGetValue(url, out var cached))
        {
            item.Thumbnail = cached;
            if (_colorCache.TryGetValue(url, out var color))
            {
                item.CardBackground = CreateDominantGradient(color);
            }
            return;
        }

        var ct = _fetchCts.Token;

        _ = Task.Run(async () =>
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                await _thumbnailThrottler.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (ct.IsCancellationRequested) return;

                byte[] bytes;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = await _httpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                }
                else if (File.Exists(url))
                {
                    bytes = await File.ReadAllBytesAsync(url, ct).ConfigureAwait(false);
                }
                else return;

                if (ct.IsCancellationRequested) return;

                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 160;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }

                _thumbnailCache[url] = bmp;
                var domColor = GetDominantColor(bmp);
                _colorCache[url] = domColor;
                var ambientGrad = CreateDominantGradient(domColor);

                if (ct.IsCancellationRequested) return;

                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || !_items.Contains(item)) return;
                    item.Thumbnail = bmp;
                    item.CardBackground = ambientGrad;
                });
            }
            catch (OperationCanceledException) { }
            catch
            {
                if ((url.Contains("ytimg.com") || url.Contains("youtube.com")) && !ct.IsCancellationRequested)
                {
                    _ = Task.Run(async () =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            var uri = new Uri(url);
                            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            if (segs.Length >= 2 && segs[0] == "vi")
                            {
                                var vid = segs[1];
                                var fallbacks = new[] { $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg", $"https://i.ytimg.com/vi/{vid}/default.jpg" };
                                foreach (var fb in fallbacks)
                                {
                                    if (fb == url || ct.IsCancellationRequested) continue;
                                    try
                                    {
                                        var fbBytes = await _httpClient.GetByteArrayAsync(fb, ct).ConfigureAwait(false);
                                        if (ct.IsCancellationRequested) return;

                                        var bmp = new BitmapImage();
                                        using (var ms = new MemoryStream(fbBytes))
                                        {
                                            bmp.BeginInit();
                                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                                            bmp.DecodePixelWidth = 160;
                                            bmp.StreamSource = ms;
                                            bmp.EndInit();
                                            bmp.Freeze();
                                        }
                                        _thumbnailCache[url] = bmp;
                                        var domColor = GetDominantColor(bmp);
                                        _colorCache[url] = domColor;
                                        var ambientGrad = CreateDominantGradient(domColor);

                                        if (ct.IsCancellationRequested) return;

                                        Application.Current?.Dispatcher.InvokeAsync(() =>
                                        {
                                            if (ct.IsCancellationRequested || !_items.Contains(item)) return;
                                            item.Thumbnail = bmp;
                                            item.CardBackground = ambientGrad;
                                        });
                                        break;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }, ct);
                }
            }
            finally
            {
                _thumbnailThrottler.Release();
            }
        }, ct);
    }

    private static Color GetDominantColor(BitmapSource bmp)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = formatted.PixelWidth, h = formatted.PixelHeight;
            if (w <= 0 || h <= 0) return Color.FromRgb(30, 30, 34);

            int stride = w * 4;
            var pixels = new byte[stride * h];
            formatted.CopyPixels(pixels, stride, 0);

            var buckets = new Dictionary<int, (long r, long g, long b, int count)>();
            int stepX = Math.Max(1, w / 16), stepY = Math.Max(1, h / 16);
            for (int y = 0; y < h; y += stepY)
            {
                int rowOff = y * stride;
                for (int x = 0; x < w; x += stepX)
                {
                    int i = rowOff + x * 4;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];

                    int lum = (r * 299 + g * 587 + b * 114) / 1000;
                    if (lum < 25 || lum > 240) continue;

                    int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    if (buckets.TryGetValue(key, out var v))
                        buckets[key] = (v.r + r, v.g + g, v.b + b, v.count + 1);
                    else
                        buckets[key] = (r, g, b, 1);
                }
            }

            if (buckets.Count == 0)
                return Color.FromRgb(35, 35, 40);

            var top = buckets.Values.OrderByDescending(v => v.count).First();
            return Color.FromRgb(
                (byte)(top.r / top.count),
                (byte)(top.g / top.count),
                (byte)(top.b / top.count));
        }
        catch
        {
            return Color.FromRgb(35, 35, 40);
        }
    }

    private static LinearGradientBrush CreateDominantGradient(Color dom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(45, dom.R, dom.G, dom.B), 0.0),
                new GradientStop(Color.FromArgb(18, dom.R, dom.G, dom.B), 0.45),
                new GradientStop(Color.FromArgb(14, 255, 255, 255), 1.0)
            }
        };
        brush.Freeze();
        return brush;
    }

    // ════════════════════════════════════════════════════════════
    //  TOOLBAR SELECTION & UTILITIES
    // ════════════════════════════════════════════════════════════

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = true;
        LogMessage("Selected all queue items.", "UI");
    }

    /// <summary>The toolbar circle: selects everything unless everything is selected already, then nothing.</summary>
    private void SelectAllToggle_Click(object sender, RoutedEventArgs e)
    {
        bool all = _items.Where(i => i.IsLoaded).All(i => i.IsSelected);
        foreach (var item in _items) item.IsSelected = !all;
        LogMessage(all ? "Deselected all queue items." : "Selected all queue items.", "UI");
        UpdateCounts();
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _items.Count == 0) return;
        try
        {
            _fetchCts.Cancel();
            _fetchCts.Dispose();
            _fetchCts = new CancellationTokenSource();
        }
        catch { }

        // Items still fetching metadata can't be brought back (their fetch was just cancelled).
        var removed = _items.Where(i => !i.IsLoading).ToList();
        foreach (var vm in _items) vm.PropertyChanged -= OnItemPropertyChanged;
        _items.Clear();
        UpdateBadge();
        LogMessage("Cleared queue items.", "UI");

        if (removed.Count == 0) return;
        NotificationWindow.Show(
            removed.Count == 1 ? "Removed 1 video from the queue. Click to undo." : $"Removed {removed.Count} videos from the queue. Click to undo.",
            this, NotificationType.Info,
            onClick: () =>
            {
                if (_isDownloading) return;
                foreach (var vm in removed)
                    if (!_items.Any(i => i.Url.Equals(vm.Url, StringComparison.OrdinalIgnoreCase)))
                        _items.Add(vm);
                UpdateBadge();
                LogMessage($"Restored {removed.Count} cleared item(s).", "UI");
            },
            actionText: "Undo");
    }

    private void ContextRemove_Click(object sender, RoutedEventArgs e) => RemoveItem_Click(sender, e);

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } vm)
        {
            _items.Remove(vm);
            UpdateBadge();
            LogMessage($"Removed from queue: {vm.Title}", "UI");
        }
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select save folder for batch downloads",
            InitialDirectory = Directory.Exists(TxtBatchOutputDir.Text) ? TxtBatchOutputDir.Text : string.Empty
        };
        if (dlg.ShowDialog() == true)
        {
            TxtBatchOutputDir.Text = dlg.FolderName;
            var s = SettingsService.Current;
            s.DefaultOutputDir = dlg.FolderName;
            SettingsService.Save(s);
            LogMessage($"Output directory updated: {dlg.FolderName}", "CONFIG");
        }
    }

    // The toolbar shows just the folder name; the full path lives in the tooltip.
    private void TxtBatchOutputDir_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtOutputFolderName == null || BtnBrowseOutputDir == null) return;
        var path = TxtBatchOutputDir.Text.Trim();
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        TxtOutputFolderName.Text = string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(path) ? "Choose folder" : path) : name;
        BtnBrowseOutputDir.ToolTip = string.IsNullOrEmpty(path) ? "Choose where videos are saved" : $"Saving to {path}\nClick to change";
    }

    private void OpenOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtBatchOutputDir.Text.Trim();
        if (!Directory.Exists(path))
        {
            NotificationWindow.Show("That save folder doesn't exist anymore. Click the folder name to pick another one.", this, NotificationType.Info);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogMessage($"Could not open folder: {ex.Message}", "ERROR");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  OPTIONS POPUP (APPLE FLUID SPRING MORPH ANIMATION)
    // ════════════════════════════════════════════════════════════

    private int _savedConcurrency = 2;
    private bool _savedCloudEnabled;
    private string _savedCloudProvider = "gdrive";
    private bool _savedCloudTempOnly;
    private bool _savedAutoRetry;
    private bool _savedAutoRemoveDone;
    private bool _savedShowPerVideoQuality;

    private void CacheOptionsState()
    {
        var s = SettingsService.Current;
        _savedConcurrency = s.BatchMaxConcurrency;
        _savedCloudEnabled = s.CloudUploadEnabled;
        _savedCloudProvider = s.CloudProvider ?? "gdrive";
        _savedCloudTempOnly = s.CloudTempDownload;
        _savedAutoRetry = s.BatchAutoRetry;
        _savedAutoRemoveDone = s.BatchAutoRemoveDone;
        _savedShowPerVideoQuality = s.ShowPerVideoQuality;
    }

    private void RestoreCachedOptionsState()
    {
        // Restore Concurrency UI
        _maxConcurrent = Math.Clamp(_savedConcurrency, 1, 30);
        foreach (ComboBoxItem item in CmbMaxConcurrency.Items)
        {
            if (string.Equals(item.Tag?.ToString(), _maxConcurrent.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                CmbMaxConcurrency.SelectedItem = item;
                break;
            }
        }

        // Restore Cloud Upload UI
        ChkPopupCloudUpload.IsChecked = _savedCloudEnabled;
        ChkBatchCloudUpload.IsChecked = _savedCloudEnabled;
        PanelPopupCloudSettings.Visibility = _savedCloudEnabled ? Visibility.Visible : Visibility.Collapsed;

        foreach (ComboBoxItem item in CmbPopupCloudProvider.Items)
        {
            if (string.Equals(item.Tag?.ToString(), _savedCloudProvider, StringComparison.OrdinalIgnoreCase))
            {
                CmbPopupCloudProvider.SelectedItem = item;
                break;
            }
        }

        ChkPopupCloudTempOnly.IsChecked = _savedCloudTempOnly;

        // Restore Auto behavior UI
        ChkAutoRetry.IsChecked = _savedAutoRetry;
        ChkAutoRemoveDone.IsChecked = _savedAutoRemoveDone;
        ChkShowPerVideoQuality.IsChecked = _savedShowPerVideoQuality;

        UpdateCloudToolbarUi();
    }

    private void BatchOptions_Click(object sender, RoutedEventArgs e)
    {
        if (BatchOptionsPopup == null) return;
        if ((DateTime.UtcNow - _batchOptionsClosedTime).TotalMilliseconds < 250) return;

        if (!BatchOptionsPopup.IsOpen && !_isClosingBatchOptions)
        {
            CacheOptionsState();
            BatchOptionsPopup.IsOpen = true;
            AnimateOptionsPillOpen();
            AnimatePopupIn(BatchOptionsPopupBorder);
        }
        else
        {
            CloseBatchOptionsAnimated();
        }
    }

    private void CloseBatchOptions_Click(object sender, RoutedEventArgs e)
    {
        CloseBatchOptionsAnimated();
    }

    private void SaveBatchOptions_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;
        s.BatchMaxConcurrency = _maxConcurrent;
        s.CloudUploadEnabled = ChkPopupCloudUpload.IsChecked == true;
        if (CmbPopupCloudProvider.SelectedItem is ComboBoxItem item)
            s.CloudProvider = item.Tag?.ToString() ?? "gdrive";
        s.CloudTempDownload = ChkPopupCloudTempOnly.IsChecked == true;
        s.BatchAutoRetry = ChkAutoRetry.IsChecked == true;
        s.BatchAutoRemoveDone = ChkAutoRemoveDone.IsChecked == true;
        s.ShowPerVideoQuality = ChkShowPerVideoQuality.IsChecked == true;

        SettingsService.Save(s);
        foreach (var qItem in _items) qItem.NotifyQualityVisibilityChanged();
        foreach (var stItem in _storedItems) stItem.NotifyQualityVisibilityChanged();
        LogMessage("Queue options saved successfully.", "CONFIG");
        CloseBatchOptionsAnimated();
    }

    private void CancelBatchOptions_Click(object sender, RoutedEventArgs e)
    {
        RestoreCachedOptionsState();
        CloseBatchOptionsAnimated();
    }

    private void CloseBatchOptionsAnimated()
    {
        if (_isClosingBatchOptions || BatchOptionsPopup == null || !BatchOptionsPopup.IsOpen) return;
        _isClosingBatchOptions = true;
        _batchOptionsClosedTime = DateTime.UtcNow;
        AnimateOptionsPillClose();
        AnimatePopupOut(BatchOptionsPopupBorder, () =>
        {
            BatchOptionsPopup.IsOpen = false;
            _isClosingBatchOptions = false;
        });
    }

    private void AnimateOptionsPillOpen()
    {
        if (OptionsGlass == null) return;
        var group = OptionsGlass.RenderTransform as TransformGroup;
        if (group == null)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform(1, 1));
            group.Children.Add(new TranslateTransform(0, 0));
            OptionsGlass.RenderTransformOrigin = new Point(0.5, 0.5);
            OptionsGlass.RenderTransform = group;
        }

        if (group.Children.Count >= 2 && group.Children[0] is ScaleTransform st && group.Children[1] is TranslateTransform tt)
        {
            var spring = AppleSpringEase.Interactive;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(280)) { EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(280)) { EasingFunction = spring });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 4, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
        }
    }

    private void AnimateOptionsPillClose()
    {
        if (OptionsGlass == null) return;
        var group = OptionsGlass.RenderTransform as TransformGroup;
        if (group != null && group.Children.Count >= 2 && group.Children[0] is ScaleTransform st && group.Children[1] is TranslateTransform tt)
        {
            var spring = AppleSpringEase.Gentle;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, 1.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, 1.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = spring });
        }
    }

    private void BatchOptionsPopup_Closed(object? sender, EventArgs e)
    {
        _batchOptionsClosedTime = DateTime.UtcNow;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        // A click anywhere else folds an empty link field back (not every click there moves keyboard focus).
        if (_linkField == LinkField.Open && !IsMouseInsideElement(AddLinksGlass, e)) CloseLinkFieldIfIdle();
        if (BatchOptionsPopup != null && BatchOptionsPopup.IsOpen && !_isClosingBatchOptions)
        {
            if (!IsMouseInsideBorder(BatchOptionsPopupBorder, e) && !IsMouseInsideElement(BtnBatchOptions, e))
            {
                CloseBatchOptionsAnimated();
            }
        }
    }

    private static bool IsMouseInsideBorder(Border border, MouseButtonEventArgs e)
    {
        if (border == null || !border.IsVisible) return false;
        var pos = e.GetPosition(border);
        return pos.X >= 0 && pos.X <= border.ActualWidth &&
               pos.Y >= 0 && pos.Y <= border.ActualHeight;
    }

    private static bool IsMouseInsideElement(FrameworkElement elem, MouseButtonEventArgs e)
    {
        if (elem == null || !elem.IsVisible) return false;
        var pos = e.GetPosition(elem);
        return pos.X >= 0 && pos.X <= elem.ActualWidth &&
               pos.Y >= 0 && pos.Y <= elem.ActualHeight;
    }

    private static void AnimatePopupIn(Border border)
    {
        if (border == null) return;
        if (SettingsService.Current.EnablePerformanceMode)
        {
            border.Opacity = 1;
            border.RenderTransform = Transform.Identity;
            FluidMotion.SetCornerRadiusImmediate(border, 0);
            return;
        }
        var spring = AppleSpringEase.Interactive;
        var bouncy = AppleSpringEase.Bouncy;
        var smooth = AppleSpringEase.Gentle;
        var group = border.RenderTransform as TransformGroup ?? new TransformGroup();
        ScaleTransform st;
        TranslateTransform tt;

        if (group.Children.Count >= 2 && group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t)
        {
            st = s;
            tt = t;
        }
        else
        {
            st = new ScaleTransform(0.96, 0.96);
            tt = new TranslateTransform(0, -4);
            group.Children.Clear();
            group.Children.Add(st);
            group.Children.Add(tt);
            border.RenderTransform = group;
        }

        // Reset corner radius to bubbly morph state
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

        // Morph corner radius to normal rounded corner (14) using spring
        FluidMotion.AnimateCornerRadius(border, 14, TimeSpan.FromMilliseconds(550), bouncy);
    }

    private static void AnimatePopupOut(Border border, Action onDone)
    {
        if (border == null)
        {
            onDone();
            return;
        }
        if (SettingsService.Current.EnablePerformanceMode)
        {
            border.Opacity = 0;
            border.RenderTransform = Transform.Identity;
            onDone();
            return;
        }
        var ease = AppleSpringEase.Snappy;
        var group = border.RenderTransform as TransformGroup ?? new TransformGroup();
        ScaleTransform st;
        TranslateTransform tt;

        if (group.Children.Count >= 2 && group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t)
        {
            st = s;
            tt = t;
        }
        else
        {
            st = new ScaleTransform(1, 1);
            tt = new TranslateTransform(0, 0);
            group.Children.Clear();
            group.Children.Add(st);
            group.Children.Add(tt);
            border.RenderTransform = group;
        }

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

    private void CmbBatchQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        if (CmbBatchQuality?.SelectedItem is ComboBoxItem item)
        {
            var q = item.Tag?.ToString() ?? "best";
            var s = SettingsService.Current;
            s.BatchQuality = q;
            SettingsService.Save(s);
            LogMessage($"Batch quality profile set to: {item.Content}", "CONFIG");
            QueueCountsUpdate(); // the size estimate in the footer depends on it
        }
    }

    private void CmbMaxConcurrency_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        if (CmbMaxConcurrency?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int val))
        {
            _maxConcurrent = Math.Clamp(val, 1, 30);
            var s = SettingsService.Current;
            s.BatchMaxConcurrency = _maxConcurrent;
            SettingsService.Save(s);
            LogMessage($"Parallel downloads concurrency set to: {_maxConcurrent}", "CONFIG");
        }
    }

    private void ChkAutoRetry_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        var s = SettingsService.Current;
        s.BatchAutoRetry = ChkAutoRetry.IsChecked == true;
        SettingsService.Save(s);
        LogMessage($"Auto-retry on error {(s.BatchAutoRetry ? "enabled" : "disabled")}.", "CONFIG");
    }

    private void ChkAutoRemoveDone_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        var s = SettingsService.Current;
        s.BatchAutoRemoveDone = ChkAutoRemoveDone.IsChecked == true;
        SettingsService.Save(s);
        LogMessage($"Auto-remove completed {(s.BatchAutoRemoveDone ? "enabled" : "disabled")}.", "CONFIG");
    }

    private void ChkShowPerVideoQuality_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        var s = SettingsService.Current;
        s.ShowPerVideoQuality = ChkShowPerVideoQuality.IsChecked == true;
        SettingsService.Save(s);
        foreach (var item in _items) item.NotifyQualityVisibilityChanged();
        foreach (var item in _storedItems) item.NotifyQualityVisibilityChanged();
        LogMessage($"Show per-video quality selector {(s.ShowPerVideoQuality ? "enabled" : "disabled")}.", "CONFIG");
    }

    private void ChkBatchCloudUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        bool isChecked = ChkBatchCloudUpload.IsChecked == true;
        ChkPopupCloudUpload.IsChecked = isChecked;
        PanelPopupCloudSettings.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdateCloudToolbarUi();

        var s = SettingsService.Current;
        s.CloudUploadEnabled = isChecked;
        SettingsService.Save(s);

        LogMessage($"Batch cloud upload {(isChecked ? "enabled" : "disabled")}.", "CONFIG");
    }

    private void ChkPopupCloudUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        bool isChecked = ChkPopupCloudUpload.IsChecked == true;
        ChkBatchCloudUpload.IsChecked = isChecked;
        PanelPopupCloudSettings.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdateCloudToolbarUi();

        var s = SettingsService.Current;
        s.CloudUploadEnabled = isChecked;
        SettingsService.Save(s);

        LogMessage($"Batch cloud upload {(isChecked ? "enabled" : "disabled")}.", "CONFIG");
    }

    private void CmbPopupCloudProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized) return;
        if (CmbPopupCloudProvider?.SelectedItem is ComboBoxItem item)
        {
            var provider = item.Tag?.ToString() ?? "gdrive";
            var s = SettingsService.Current;
            s.CloudProvider = provider;
            SettingsService.Save(s);

            UpdateCloudToolbarUi();
            LogMessage($"Cloud provider selected: {CloudStorageService.GetProviderDisplayName(provider)}", "CONFIG");
        }
    }

    private void BtnBatchCloudConfig_Click(object sender, RoutedEventArgs e) => OpenCloudSetup((UIElement)sender);

    private void BtnPopupCloudConfig_Click(object sender, RoutedEventArgs e) => OpenCloudSetup((UIElement)sender);

    /// <summary>Settings → Cloud → Configuration (the main window opens it); the toolbar follows through OnSettingsChanged.</summary>
    private void OpenCloudSetup(UIElement origin)
    {
        var provider = (CmbPopupCloudProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? SettingsService.Current.CloudProvider ?? "gdrive";
        (Owner as MainWindow ?? Application.Current.MainWindow as MainWindow)?.OpenCloudConfiguration(origin, provider);
    }

    private void ChkPopupCloudTempOnly_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;
        bool isTempOnly = ChkPopupCloudTempOnly.IsChecked == true;
        var s = SettingsService.Current;
        s.CloudTempDownload = isTempOnly;
        SettingsService.Save(s);
        LogMessage($"Cloud temporary local file only: {isTempOnly}", "CONFIG");
    }

    private string GetCurrentProviderKey()
    {
        return (CmbPopupCloudProvider?.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? SettingsService.Current.CloudProvider
            ?? "gdrive";
    }

    private void UpdateCloudToolbarUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(UpdateCloudToolbarUi);
            return;
        }

        bool isCloudOn = ChkBatchCloudUpload?.IsChecked == true;
        var providerKey = GetCurrentProviderKey();

        if (PillBatchCloud != null)
        {
            // Resource references (not copies) so the pill follows Dark/Light switches.
            PillBatchCloud.SetResourceReference(Border.BorderBrushProperty, isCloudOn ? "Accent" : "Tint12");
            TxtBatchCloudLabel?.SetResourceReference(TextBlock.ForegroundProperty, isCloudOn ? "Accent" : "TextSec");
        }

        foreach (var item in _items)
        {
            item.UpdateCloudConfig(isCloudOn, providerKey);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  CONTEXT MENU & SINGLE ITEM ACTIONS
    // ════════════════════════════════════════════════════════════

    private async void ContextRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            await DownloadSingleItemAsync(item);
        }
    }

    private void ContextCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            try
            {
                Clipboard.SetText(item.Url);
                LogMessage($"Copied URL: {item.Url}", "CLIPBOARD");
                NotificationWindow.Show("Video URL copied to clipboard.", this, NotificationType.Info);
            }
            catch { }
        }
    }

    private void ContextOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Url))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Url)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to open browser: {ex.Message}", "ERROR");
                NotificationWindow.Show($"Failed to open browser: {ex.Message}", this, NotificationType.Error);
            }
        }
    }



    private async void RetrySingleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BatchQueueItemViewModel item)
        {
            await DownloadSingleItemAsync(item);
        }
    }

    private async Task DownloadSingleItemAsync(BatchQueueItemViewModel item)
    {
        if (_isDownloading)
        {
            NotificationWindow.Show("A batch download is currently running. Please wait or stop the batch first.", this, NotificationType.Warning);
            return;
        }

        _isDownloading = true;
        OverallProgressBar.Visibility = Visibility.Visible;
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        TxtBtnBatchStart.Text = "Stop Download";
        IconBatchStart.Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss24;
        TxtOverallStatus.Text = $"Downloading: {item.Title}";

        var qualityTag = (CmbBatchQuality.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "best";
        var outDir = TxtBatchOutputDir.Text.Trim();
        bool cloudUpload = ChkBatchCloudUpload.IsChecked == true;
        var cloudProvider = GetCurrentProviderKey();
        bool cloudTempOnly = ChkPopupCloudTempOnly.IsChecked == true;
        bool autoRetry = ChkAutoRetry.IsChecked == true;

        try
        {
            await ProcessQueueItemAsync(item, outDir, qualityTag, cloudUpload, cloudProvider, cloudTempOnly, autoRetry, ct);
        }
        finally
        {
            _isDownloading = false;
            TxtBtnBatchStart.Text = (string)FindResource("BtnStartBatch");
            IconBatchStart.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
            TxtOverallStatus.Text = item.IsCompleted ? (cloudUpload ? "Completed & synced to Cloud" : "Completed") : "Ready";
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  LIVE LOG CONSOLE
    // ════════════════════════════════════════════════════════════

    private bool _isLiveLogOpen;
    private double _normalWidth = 920;
    private bool _isAnimatingLiveLog;

    private void ToggleLiveLog_Click(object sender, RoutedEventArgs e)
    {
        SetLiveLogVisibility(!_isLiveLogOpen);
    }

    private void CloseLiveLog_Click(object sender, RoutedEventArgs e)
    {
        SetLiveLogVisibility(false);
    }

    private void SetLiveLogVisibility(bool open)
    {
        if (_isLiveLogOpen == open && !_isAnimatingLiveLog) return;

        _isLiveLogOpen = open;
        _isAnimatingLiveLog = true;

        if (open)
        {
            if (Width < 1000)
            {
                _normalWidth = Width;
            }
            else if (_normalWidth <= 0)
            {
                _normalWidth = 920;
            }

            double targetWindowWidth = _normalWidth + 360;

            LiveLogSidePanel.Visibility = Visibility.Visible;
            TxtLiveLog.ScrollToEnd();

            MinWidth = Math.Max(740, _normalWidth);

            var panelAnim = new DoubleAnimation
            {
                From = LiveLogSidePanel.ActualWidth > 0 && LiveLogSidePanel.ActualWidth < 360 ? LiveLogSidePanel.ActualWidth : 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var windowAnim = new DoubleAnimation
            {
                From = Width,
                To = targetWindowWidth,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            windowAnim.Completed += (s, e) =>
            {
                BeginAnimation(Window.WidthProperty, null);
                LiveLogSidePanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                Width = targetWindowWidth;
                LiveLogSidePanel.Width = 360;
                MinWidth = _normalWidth + 360;
                _isAnimatingLiveLog = false;
            };

            LiveLogSidePanel.BeginAnimation(FrameworkElement.WidthProperty, panelAnim);
            BeginAnimation(Window.WidthProperty, windowAnim);
        }
        else
        {
            double targetWindowWidth = Math.Max(740, _normalWidth);
            MinWidth = 740;

            var panelAnim = new DoubleAnimation
            {
                From = LiveLogSidePanel.ActualWidth > 0 ? LiveLogSidePanel.ActualWidth : 360,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var windowAnim = new DoubleAnimation
            {
                From = Width,
                To = targetWindowWidth,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            windowAnim.Completed += (s, e) =>
            {
                BeginAnimation(Window.WidthProperty, null);
                LiveLogSidePanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                LiveLogSidePanel.Visibility = Visibility.Collapsed;
                LiveLogSidePanel.Width = 360;
                Width = targetWindowWidth;
                MinWidth = 740;
                _isAnimatingLiveLog = false;
            };

            LiveLogSidePanel.BeginAnimation(FrameworkElement.WidthProperty, panelAnim);
            BeginAnimation(Window.WidthProperty, windowAnim);
        }
    }

    private void CopyLiveLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtLiveLog.Text))
        {
            try
            {
                Clipboard.SetText(TxtLiveLog.Text);
                LogMessage("Live log contents copied to clipboard.", "CLIPBOARD");
                NotificationWindow.Show("Live log copied to clipboard.", this, NotificationType.Info);
            }
            catch { }
        }
    }

    private void ClearLiveLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLiveLog.Clear();
        _liveLogLength = 0;
        _logLineCount = 0;
        TxtLogLineCount.Text = "0 lines";
    }

    private void UpdateBadge() => UpdateCounts();

    private void UpdateCounts()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(UpdateCounts);
            return;
        }

        int totalActive = _items.Count;
        int loadedActive = _items.Count(i => i.IsLoaded);
        int totalStored = _storedItems.Count;

        if (EmptyQueueState != null)
            EmptyQueueState.Visibility = totalActive == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (QueueItemsList != null)
            QueueItemsList.Visibility = totalActive > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (EmptyStoredState != null)
            EmptyStoredState.Visibility = totalStored == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (StoredItemsList != null)
            StoredItemsList.Visibility = totalStored > 0 ? Visibility.Visible : Visibility.Collapsed;

        int loading = totalActive - loadedActive;
        int selected = _items.Count(i => i.IsSelected && i.IsLoaded);
        int failed = _items.Count(i => i.HasError && i.IsLoaded);
        int ready = GetDownloadTargets().Count;

        // Title-bar summary: "12 videos · 2 loading · 1 failed" (the selection is in the toolbar)
        if (TxtLoadedCount != null)
        {
            if (totalActive == 0)
            {
                TxtLoadedCount.Text = totalStored > 0 ? $"Queue is empty · {totalStored} in storage" : "Queue is empty";
            }
            else
            {
                var parts = new List<string> { totalActive == 1 ? "1 video" : $"{totalActive} videos" };
                if (loading > 0) parts.Add($"{loading} loading");
                if (failed > 0) parts.Add($"{failed} failed");
                if (totalStored > 0) parts.Add($"{totalStored} in storage");
                TxtLoadedCount.Text = string.Join(" · ", parts);
            }
        }

        if (BtnToggleSplitStorage?.Template?.FindName("TxtBadgeStoredCount", BtnToggleSplitStorage) is TextBlock txtBadge)
            txtBadge.Text = totalStored.ToString();
        if (BtnToggleSplitStorage?.Template?.FindName("BadgeStored", BtnToggleSplitStorage) is Border badgeStored)
            badgeStored.Visibility = totalStored > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (BtnClearQueue != null)
            BtnClearQueue.IsEnabled = !_isDownloading && totalActive > 0;

        // Select-all circle: filled when all are included, a dash when some are
        if (ChkSelectAll != null)
        {
            ChkSelectAll.IsEnabled = !_isDownloading && loadedActive > 0;
            ChkSelectAll.IsChecked = loadedActive == 0 || selected == 0 ? false : selected == loadedActive ? true : null;
            TxtSelectionCount.Text = loadedActive == 0 ? "None selected"
                : selected == loadedActive ? (loadedActive == 1 ? "1 selected" : $"All {loadedActive} selected")
                : $"{selected} of {loadedActive} selected";
        }

        if (BtnRetryFailed != null)
        {
            BtnRetryFailed.Visibility = !_isDownloading && failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (TxtRetryFailed != null) TxtRetryFailed.Text = failed == 1 ? "Retry failed" : $"Retry {failed} failed";
        }

        if (_isDownloading) return;

        // Primary button: "Download 5" when there is something to do, disabled otherwise.
        BtnStartBatchDownload.Tag = null;
        BtnStartBatchDownload.IsEnabled = ready > 0;
        IconBatchStart.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
        TxtBtnBatchStart.Text = ready > 0 ? $"Download {ready}" : "Download";

        // Keep the last run's summary until the queue itself changes.
        if (!_showingRunSummary && TxtOverallStatus != null)
        {
            if (TxtOverallProgressCount != null)
                TxtOverallProgressCount.Text = "";
            // The button already says how many; the status adds their total length and roughly how much that
            // downloads at the chosen quality (per-video picks included), so it changes with the quality.
            var targets = GetDownloadTargets();
            double seconds = targets.Sum(i => Math.Max(0, i.Duration));
            double bytes = targets.Sum(i => Math.Max(0, i.Duration) * BytesPerSecond(EffectiveQuality(i)));
            string estimate = bytes > 0 ? $" · about {FormatSize(bytes)}" : "";
            TxtOverallStatus.Text = totalActive == 0 ? "Add links to get started"
                : loading > 0 ? "Loading video details…"
                : ready > 0 ? (seconds > 0 ? $"Ready · {FormatTotalDuration(seconds)}{estimate}" : "Ready")
                : "Select videos to download";
            // The overall bar only means something during and right after a run.
            OverallProgressBar.Visibility = Visibility.Collapsed;
            OverallProgressBar?.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            if (OverallProgressBar != null) OverallProgressBar.Value = 0;
        }
    }

    private string EffectiveQuality(BatchQueueItemViewModel item) =>
        string.IsNullOrEmpty(item.SelectedQuality) || item.SelectedQuality == "default"
            ? (CmbBatchQuality?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "best"
            : item.SelectedQuality;

    /// <summary>Typical download rate per second of media for a quality (rough: YouTube's usual bitrates).</summary>
    private static double BytesPerSecond(string quality) => quality switch
    {
        "audio" => 320_000 / 8.0,          // MP3 320k
        "m4a" => 256_000 / 8.0,            // M4A 256k
        "flac" => 900_000 / 8.0,           // lossless from ~160k sources, typically ~0.9 Mbit/s
        "2160" => 20_000_000 / 8.0,
        "1440" => 10_000_000 / 8.0,
        "1080" => 4_500_000 / 8.0,
        "720" => 2_500_000 / 8.0,
        "480" => 1_200_000 / 8.0,
        "360" => 700_000 / 8.0,
        _ => 6_000_000 / 8.0,              // best available: usually 1080p-1440p
    };

    private static string FormatSize(double bytes) =>
        bytes >= 1e9 ? $"{bytes / 1e9:0.#} GB" : bytes >= 1e6 ? $"{bytes / 1e6:0} MB" : $"{Math.Max(1, bytes / 1e3):0} KB";

    private static string FormatTotalDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)Math.Round(t.TotalMinutes)} min" : $"{t.Seconds} s";
    }

    /// <summary>Included, loaded videos that still need downloading.</summary>
    private List<BatchQueueItemViewModel> GetDownloadTargets() =>
        _items.Where(i => i.IsSelected && !i.IsLoading && !i.IsCompleted).ToList();

    private bool _showingRunSummary;
    private bool _countsUpdateQueued;

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (BatchQueueItemViewModel vm in e.NewItems) vm.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems != null)
            foreach (BatchQueueItemViewModel vm in e.OldItems) vm.PropertyChanged -= OnItemPropertyChanged;

        if (!_isDownloading) _showingRunSummary = false;
        QueueCountsUpdate();
    }

    // Ticking a video, a finished fetch or a failure changes the counts and the Download button.
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchQueueItemViewModel.IsSelected) or nameof(BatchQueueItemViewModel.IsLoading)
            or nameof(BatchQueueItemViewModel.HasError) or nameof(BatchQueueItemViewModel.IsCompleted)
            or nameof(BatchQueueItemViewModel.SelectedQuality))
        {
            QueueCountsUpdate();
        }
    }

    // Coalesces bursts (Select All on 200 videos) into one refresh.
    private void QueueCountsUpdate()
    {
        if (_countsUpdateQueued) return;
        _countsUpdateQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _countsUpdateQueued = false;
            UpdateCounts();
        });
    }

    // ════════════════════════════════════════════════════════════
    //  SPLIT STORAGE BIN & ITEM STORE / RESTORE
    // ════════════════════════════════════════════════════════════

    private void BtnToggleSplitStorage_Click(object sender, RoutedEventArgs e)
    {
        _isSplitStorageActive = BtnToggleSplitStorage.IsChecked == true;
        ApplySplitStorageLayout(_isSplitStorageActive);
    }

    private void ApplySplitStorageLayout(bool active)
    {
        if (ColActiveQueue == null || ColSplitter == null || ColStoredQueue == null) return;

        if (active)
        {
            ColActiveQueue.Width = new GridLength(1, GridUnitType.Star);
            ColSplitter.Width = GridLength.Auto;
            ColStoredQueue.Width = new GridLength(1, GridUnitType.Star);

            if (StorageSplitter != null) StorageSplitter.Visibility = Visibility.Visible;
            if (StoredPanelContainer != null) StoredPanelContainer.Visibility = Visibility.Visible;
            LogMessage("Split storage view activated.", "UI");
        }
        else
        {
            ColActiveQueue.Width = new GridLength(1, GridUnitType.Star);
            ColSplitter.Width = new GridLength(0);
            ColStoredQueue.Width = new GridLength(0);

            if (StorageSplitter != null) StorageSplitter.Visibility = Visibility.Collapsed;
            if (StoredPanelContainer != null) StoredPanelContainer.Visibility = Visibility.Collapsed;
            LogMessage("Split storage view deactivated.", "UI");
        }
        UpdateCounts();
    }

    public void StoreItem(BatchQueueItemViewModel item)
    {
        if (item == null) return;
        if (_items.Contains(item))
        {
            _items.Remove(item);
            item.IsStored = true;
            _storedItems.Add(item);

            if (!_isSplitStorageActive)
            {
                _isSplitStorageActive = true;
                if (BtnToggleSplitStorage != null) BtnToggleSplitStorage.IsChecked = true;
                ApplySplitStorageLayout(true);
            }
            else
            {
                UpdateCounts();
            }
            LogMessage($"Stored item for later: {item.Title}", "QUEUE");
        }
    }

    public void RestoreItem(BatchQueueItemViewModel item)
    {
        if (item == null) return;
        if (_storedItems.Contains(item))
        {
            _storedItems.Remove(item);
            item.IsStored = false;
            _items.Add(item);
            UpdateCounts();
            LogMessage($"Restored item to queue: {item.Title}", "QUEUE");
        }
    }

    private void StoreSingleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            StoreItem(item);
        }
    }

    private void RestoreSingleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            RestoreItem(item);
        }
    }

    private void ContextStore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && (mi.DataContext as BatchQueueItemViewModel ?? mi.Tag as BatchQueueItemViewModel) is { } item)
        {
            StoreItem(item);
        }
    }

    private void ContextRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && (mi.DataContext as BatchQueueItemViewModel ?? mi.Tag as BatchQueueItemViewModel) is { } item)
        {
            RestoreItem(item);
        }
    }

    private void RemoveStoredItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && (fe.DataContext as BatchQueueItemViewModel ?? fe.Tag as BatchQueueItemViewModel) is { } item)
        {
            _storedItems.Remove(item);
            UpdateCounts();
            LogMessage($"Removed from stored bin: {item.Title}", "UI");
        }
    }


    // ════════════════════════════════════════════════════════════
    //  BATCH DOWNLOAD WITH MULTI-DOWNLOAD CONCURRENCY
    // ════════════════════════════════════════════════════════════

    private async void StartBatchDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            TxtOverallStatus.Text = "Stopping…";
            LogMessage("Batch download cancelled by user.", "CANCEL");
            return;
        }

        var targets = GetDownloadTargets();
        if (targets.Count == 0)
        {
            NotificationWindow.Show("No selected items to download.", this, NotificationType.Info);
            return;
        }

        await RunBatchAsync(targets);
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        var failed = _items.Where(i => i.HasError && !i.IsLoading).ToList();
        if (failed.Count == 0) return;
        foreach (var item in failed) item.IsSelected = true;
        LogMessage($"Retrying {failed.Count} failed item(s).", "BATCH");
        await RunBatchAsync(failed);
    }

    private async Task RunBatchAsync(List<BatchQueueItemViewModel> targets)
    {
        _isDownloading = true;
        _showingRunSummary = false;
        OverallProgressBar.Visibility = Visibility.Visible;
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        TxtBtnBatchStart.Text = "Stop";
        IconBatchStart.Symbol = Wpf.Ui.Controls.SymbolRegular.Stop24;
        BtnStartBatchDownload.Tag = "stop";
        BtnStartBatchDownload.IsEnabled = true;
        UpdateCounts();

        // The bar follows the average progress of every video in the run instead of jumping per finished file.
        var progressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progressTimer.Tick += (_, _) =>
        {
            if (OverallProgressBar == null || targets.Count == 0) return;
            double sum = targets.Sum(t => t.IsCompleted ? 100 : t.HasError ? 0 : t.Progress);
            OverallProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(sum / targets.Count, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        progressTimer.Start();

        var qualityTag = (CmbBatchQuality.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "best";
        var outDir = TxtBatchOutputDir.Text.Trim();

        bool cloudUpload = ChkBatchCloudUpload.IsChecked == true;
        var cloudProvider = GetCurrentProviderKey();
        bool cloudTempOnly = ChkPopupCloudTempOnly.IsChecked == true;
        bool autoRetry = ChkAutoRetry.IsChecked == true;
        bool autoRemoveDone = ChkAutoRemoveDone.IsChecked == true;

        int total = targets.Count;
        int completed = 0;
        int active = 0;

        LogMessage($"Started batch download of {total} items. Max parallel: {_maxConcurrent}. Quality: {qualityTag}. Destination: {outDir}. Cloud: {(cloudUpload ? cloudProvider : "Disabled")}", "BATCH");

        using var semaphore = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        var tasks = new List<Task>();

        void UpdateOverallProgress()
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtOverallProgressCount.Text = $"{completed} of {total} done";
                bool hasCloudActive = _items.Any(i => i.IsCloudUploading);
                if (ct.IsCancellationRequested)
                    TxtOverallStatus.Text = "Stopping…";
                else if (hasCloudActive)
                    TxtOverallStatus.Text = $"Uploading to the cloud · {active} active";
                else
                    TxtOverallStatus.Text = active == 1 ? "Downloading 1 video…" : $"Downloading {active} videos at once…";
            });
        }

        try
        {
            foreach (var item in targets)
            {
                if (ct.IsCancellationRequested) break;

                try { await semaphore.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }

                tasks.Add(Task.Run(async () =>
                {
                    Interlocked.Increment(ref active);
                    UpdateOverallProgress();

                    try
                    {
                        bool success = await ProcessQueueItemAsync(item, outDir, qualityTag, cloudUpload, cloudProvider, cloudTempOnly, autoRetry, ct);
                        if (success)
                        {
                            Interlocked.Increment(ref completed);
                            if (autoRemoveDone)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    _items.Remove(item);
                                    UpdateBadge();
                                });
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                        semaphore.Release();
                        UpdateOverallProgress();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            int failedCount = targets.Count(t => t.HasError);
            TxtOverallStatus.Text = ct.IsCancellationRequested ? $"Stopped · {completed} of {total} downloaded"
                : failedCount > 0 ? $"Finished with problems · {completed} downloaded, {failedCount} failed"
                : cloudUpload ? $"All done · {completed} downloaded & uploaded"
                : completed == 1 ? "All done · 1 video downloaded" : $"All done · {completed} videos downloaded";
            LogMessage($"Batch run finished: {completed}/{total} successfully downloaded.", "BATCH");
        }
        catch (OperationCanceledException)
        {
            TxtOverallStatus.Text = $"Stopped · {completed} of {total} downloaded";
            LogMessage("Batch download cancelled.", "CANCEL");
        }
        catch (Exception ex)
        {
            LogMessage($"Batch error: {ex.Message}", "ERROR");
        }
        finally
        {
            // Never leave downloads running in the background once the batch has ended.
            try { await Task.WhenAll(tasks); } catch { }

            progressTimer.Stop();
            _isDownloading = false;
            _showingRunSummary = true;
            TxtOverallProgressCount.Text = $"{completed} of {total} done";
            _downloadCts?.Dispose();
            _downloadCts = null;
            UpdateBadge();
        }
    }

    private void ContextSetQuality_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag && (mi.DataContext as BatchQueueItemViewModel ?? mi.CommandParameter as BatchQueueItemViewModel) is { } item)
        {
            item.SelectedQuality = tag;
            LogMessage($"Quality set for '{item.Title}': {item.SelectedQualityDisplayName}", "CONFIG");
            NotificationWindow.Show($"Quality set to {item.SelectedQualityDisplayName}", this, NotificationType.Info);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ProgressNumberRegex =
        new(@"[\d.,]+\s*%?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<bool> ProcessQueueItemAsync(
        BatchQueueItemViewModel item,
        string outDir,
        string qualityTag,
        bool cloudUpload,
        string cloudProvider,
        bool cloudTempOnly,
        bool autoRetry,
        CancellationToken ct)
    {
        var targetQuality = (!string.IsNullOrWhiteSpace(item.SelectedQuality) && item.SelectedQuality != "default")
            ? item.SelectedQuality
            : qualityTag;

        int attempts = autoRetry ? 2 : 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            await Dispatcher.InvokeAsync(() =>
            {
                item.IsDownloading = true;
                item.IsCloudUploading = false;
                item.IsCompleted = false;
                item.StatusText = attempt > 1 ? "Retrying..." : "Starting...";
                item.ProgressVisibility = Visibility.Visible;
                item.Progress = 0;
                item.ErrorMessage = "";
                item.HasError = false;
                item.CloudStatusMessage = "";
                item.RefreshState();
            });

            LogMessage($"Starting download: {item.Title} [{targetQuality}] (Attempt {attempt})", "DOWNLOAD");

            try
            {
                var titleToPass = (item.Title is "Loading video details..." or "Failed to load info" || item.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ? null : item.Title;

                var finalOutDir = string.IsNullOrWhiteSpace(outDir) ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : outDir;
                if (!Directory.Exists(finalOutDir))
                {
                    try { Directory.CreateDirectory(finalOutDir); } catch { }
                }

                var jobId = await _backend.CreateJobAsync(
                    url: item.Url,
                    mode: "download",
                    platform: "youtube",
                    title: titleToPass,
                    description: null,
                    tags: null,
                    outputDir: finalOutDir,
                    privacy: null,
                    fingerprint: false,
                    quality: targetQuality,
                    ct: ct,
                    cloudUpload: cloudUpload,
                    cloudProvider: cloudProvider,
                    cloudTempOnly: cloudTempOnly);

                // Stopping the batch only cancels the token; the engine job (yt-dlp/ffmpeg) must be told explicitly.
                using var cancelRegistration = ct.Register(() => _ = _backend.CancelJobAsync(jobId));

                bool isCompleted = false;
                string? lastLoggedPhase = null;

                await foreach (var status in _backend.StreamJobAsync(jobId, ct))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (status.Status == "uploading")
                        {
                            item.IsDownloading = false;
                            item.IsCloudUploading = true;
                            item.Progress = Math.Clamp(status.Progress, 0, 100);
                            item.StatusText = status.Progress > 0 ? $"Cloud {Math.Round(status.Progress)}%" : "Uploading...";
                            item.CloudStatusMessage = !string.IsNullOrWhiteSpace(status.Message)
                                ? status.Message
                                : $"Uploading to {CloudStorageService.GetProviderDisplayName(cloudProvider)}...";
                        }
                        else
                        {
                            item.IsDownloading = true;
                            item.IsCloudUploading = false;
                            item.Progress = Math.Clamp(status.Progress, 0, 100);
                            item.StatusText = $"{Math.Round(item.Progress)}%";
                            if (!string.IsNullOrWhiteSpace(status.Speed))
                                item.Speed = status.Speed;
                            if (status.Eta.HasValue && status.Eta.Value > 0)
                            {
                                var ts = TimeSpan.FromSeconds(status.Eta.Value);
                                item.Eta = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
                            }
                        }
                    });

                    // Log phase changes only – every percentage step would flood the live log.
                    if (!string.IsNullOrWhiteSpace(status.Message))
                    {
                        var phase = status.Status + "|" + ProgressNumberRegex.Replace(status.Message, "");
                        if (phase != lastLoggedPhase)
                        {
                            lastLoggedPhase = phase;
                            LogMessage($"[{item.Title}] {status.Message}", "PROGRESS");
                        }
                    }

                    if (status.Status == "completed")
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (status.Result != null && status.Result.TryGetValue("title", out var titleObj) && titleObj is string realTitle && !string.IsNullOrWhiteSpace(realTitle))
                            {
                                if (item.Title is "Loading video details..." or "Failed to load info" || item.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                {
                                    item.Title = realTitle;
                                }
                            }

                            item.IsDownloading = false;
                            item.IsCloudUploading = false;
                            item.IsCompleted = true;
                            item.StatusText = cloudUpload ? (cloudTempOnly ? "Uploaded" : "Done + Cloud") : "Completed";
                            item.ProgressVisibility = Visibility.Collapsed;
                            item.Speed = "";
                            item.Eta = "";
                            item.HasError = false;
                            item.ErrorMessage = "";
                            item.CloudStatusMessage = cloudUpload ? $"Uploaded to {CloudStorageService.GetProviderDisplayName(cloudProvider)}" : "";
                            item.RefreshState();
                        });
                        LogMessage($"[COMPLETED] {item.Title}", "SUCCESS");
                        isCompleted = true;
                        break;
                    }
                    else if (status.Status == "error")
                    {
                        var err = !string.IsNullOrWhiteSpace(status.Error) ? status.Error : status.Message;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            item.IsDownloading = false;
                            item.IsCloudUploading = false;
                            item.IsCompleted = false;
                            item.StatusText = "Failed";
                            item.ErrorMessage = err;
                            item.HasError = true;
                            item.ProgressVisibility = Visibility.Collapsed;
                            item.Speed = "";
                            item.Eta = "";
                            item.RefreshState();
                        });
                        LogMessage($"[FAILED] {item.Title}: {err}", "ERROR");
                        break;
                    }
                }

                if (isCompleted) return true;
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    item.IsDownloading = false;
                    item.IsCloudUploading = false;
                    item.StatusText = "Cancelled";
                    item.ProgressVisibility = Visibility.Collapsed;
                    item.Speed = "";
                    item.Eta = "";
                    item.RefreshState();
                });
                LogMessage($"[CANCELLED] {item.Title}", "CANCEL");
                return false;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    item.IsDownloading = false;
                    item.IsCloudUploading = false;
                    item.StatusText = "Failed";
                    item.ErrorMessage = ex.Message;
                    item.HasError = true;
                    item.ProgressVisibility = Visibility.Collapsed;
                    item.Speed = "";
                    item.Eta = "";
                    item.RefreshState();
                });
                LogMessage($"[ERROR] {item.Title}: {ex.Message}", "ERROR");
            }

            if (attempt < attempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(1500, ct);
            }
        }

        return false;
    }

}

public sealed class BatchQueueItemViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush BrushCardBorderDownloading = new(Color.FromArgb(0x48, 0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush BrushCardBorderCloud = new(Color.FromArgb(0x48, 0x81, 0x8C, 0xF8));
    private static readonly SolidColorBrush BrushCardBorderSuccess = new(Color.FromArgb(0x40, 0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush BrushCardBorderError = new(Color.FromArgb(0x48, 0xEF, 0x44, 0x44));

    // Text and neutral borders come from the live theme brushes so they stay readable in light mode.
    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Brush BrushCardBorderPending => ThemeBrush("Tint28");
    private static Brush BrushTextPrimary => ThemeBrush("TextPrimary");
    private static Brush BrushTextSec => ThemeBrush("TextSec");
    private static Brush BrushAccentBright => ThemeBrush("Accent");
    private static Brush BrushSuccess => ThemeBrush("SuccessGreen");
    private static Brush BrushError => ThemeBrush("ErrorRed");

    static BatchQueueItemViewModel()
    {
        BrushCardBorderDownloading.Freeze();
        BrushCardBorderCloud.Freeze();
        BrushCardBorderSuccess.Freeze();
        BrushCardBorderError.Freeze();
    }

    private string _id = "";
    private string _title = "";
    private string _uploader = "";
    private string _url = "";
    private double _duration;
    private bool _isSelected = true;
    private bool _isLoading;
    private bool _isDownloading;
    private bool _isCloudUploading;
    private bool _isCloudActive;
    private bool _isCompleted;
    private string _cloudProvider = "gdrive";
    private string _cloudStatusMessage = "";
    private string _statusText = "Pending";
    private Brush? _statusColor;
    private Brush? _cardBackground;
    private double _progress;
    private Visibility _progressVisibility = Visibility.Collapsed;
    private ImageSource? _thumbnail;
    private string _speed = "";
    private string _eta = "";
    private string _errorMessage = "";
    private bool _hasError;

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    public string Uploader
    {
        get => _uploader;
        set { _uploader = value; OnPropertyChanged(); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(DurationTextVisibility));
        }
    }

    public string DurationText
    {
        get
        {
            if (_duration <= 0) return "";
            var ts = TimeSpan.FromSeconds(_duration);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
        }
    }

    public Visibility DurationTextVisibility => _duration > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(LoadedVisibility));
            OnPropertyChanged(nameof(ContentOpacity));
            OnPropertyChanged(nameof(CloudBadgeVisibility));
            OnPropertyChanged(nameof(StoreButtonVisibility));
        }
    }

    private bool _isStored;
    public bool IsStored
    {
        get => _isStored;
        set
        {
            _isStored = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StoreButtonVisibility));
        }
    }

    public Visibility StoreButtonVisibility => (!_isStored && IsLoaded) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsLoaded => !_isLoading;
    public Visibility LoadingVisibility => _isLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadedVisibility => (!_isLoading && _thumbnail != null) ? Visibility.Visible : Visibility.Collapsed;
    public double ContentOpacity
    {
        get
        {
            if (_isLoading) return 0.55;
            if (!_isSelected) return 0.38;
            return 1.0;
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
            RefreshState();
        }
    }

    public bool IsCloudUploading
    {
        get => _isCloudUploading;
        set
        {
            if (_isCloudUploading == value) return;
            _isCloudUploading = value;
            OnPropertyChanged();
            RefreshState();
        }
    }

    public bool IsCloudActive
    {
        get => _isCloudActive;
        set
        {
            _isCloudActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloudBadgeVisibility));
            OnPropertyChanged(nameof(CloudBadgeText));
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value) return;
            _isCompleted = value;
            OnPropertyChanged();
            RefreshState();
        }
    }

    public string CloudProvider
    {
        get => _cloudProvider;
        set
        {
            _cloudProvider = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloudBadgeText));
        }
    }

    public string CloudStatusMessage
    {
        get => _cloudStatusMessage;
        set
        {
            _cloudStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloudStatusVisibility));
        }
    }

    public Visibility CloudStatusVisibility => !string.IsNullOrWhiteSpace(_cloudStatusMessage) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CloudBadgeVisibility
    {
        get
        {
            if (_isLoading) return Visibility.Collapsed;
            return (_isCloudActive || _isCloudUploading || (_isCompleted && _isCloudActive)) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public string CloudBadgeText
    {
        get
        {
            if (_isCloudUploading) return "Uploading...";
            if (_isCompleted && _isCloudActive) return "Uploaded";
            return _cloudProvider?.ToLowerInvariant() switch
            {
                "gdrive" or "googledrive" => "GDrive",
                "dropbox" => "Dropbox",
                "onedrive" => "OneDrive",
                "webhook" or "discord" => "Discord",
                "nextcloud" => "Nextcloud",
                "sftp" or "ftp" => "SFTP",
                "custom" => "Cloud",
                _ => "Cloud"
            };
        }
    }


    public Visibility ActivePillVisibility => (_isDownloading || _isCloudUploading) ? Visibility.Visible : Visibility.Collapsed;
    public Wpf.Ui.Controls.SymbolRegular ActivePillSymbol => _isCloudUploading ? Wpf.Ui.Controls.SymbolRegular.CloudArrowUp24 : Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
    public string ActivePillText => _isCloudUploading ? "CLOUD" : "DL";
    public Brush ActivePillForeground => _isCloudUploading ? BrushAccentBright : BrushTextPrimary;

    public Brush? CardBackground
    {
        get => _isSelected ? _cardBackground : null;
        set { _cardBackground = value; OnPropertyChanged(); }
    }

    private string _thumbnailUrl = "";
    public string ThumbnailUrl
    {
        get => _thumbnailUrl;
        set { _thumbnailUrl = value; OnPropertyChanged(); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoadedVisibility));
        }
    }

    private string _selectedQuality = "default";
    public string SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            _selectedQuality = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedQualityDisplayName));
        }
    }

    public string SelectedQualityDisplayName => _selectedQuality switch
    {
        "best" => "Best Available",
        "2160" => "4K UHD (2160p)",
        "1440" => "2K QHD (1440p)",
        "1080" => "1080p Full HD",
        "720" => "720p HD",
        "480" => "480p SD",
        "360" => "360p Low",
        "audio" => "Audio MP3 (320k)",
        "m4a" => "Audio M4A (256k)",
        "flac" => "Audio FLAC",
        _ => "Default (Batch)"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ContentOpacity));
            OnPropertyChanged(nameof(CardBackground));
            RefreshState();
        }
    }

    public Visibility PerVideoQualityVisibility => SettingsService.Current.ShowPerVideoQuality ? Visibility.Visible : Visibility.Collapsed;

    public void NotifyQualityVisibilityChanged()
    {
        OnPropertyChanged(nameof(PerVideoQualityVisibility));
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public Brush? StatusColor
    {
        get
        {
            if (_statusColor != null) return _statusColor;
            if (_hasError) return BrushError;
            if (_isCloudUploading) return BrushAccentBright;
            if (_isDownloading) return BrushTextPrimary;
            if (_isCompleted) return BrushSuccess;
            return BrushTextSec;
        }
        set { _statusColor = value; OnPropertyChanged(); }
    }

    private double _targetProgress;

    // Progress glides towards each new value. One shared ticker serves every moving bar (30 parallel downloads used to
    // mean 30 timers at 60 Hz), and 30 Hz is plenty for a bar this thin.
    private static readonly List<BatchQueueItemViewModel> Gliding = [];
    private static System.Windows.Threading.DispatcherTimer? _glideTimer;

    public double Progress
    {
        get => _progress;
        set
        {
            _targetProgress = Math.Clamp(value, 0, 100);
            if (Math.Abs(_progress - _targetProgress) < 0.2)
            {
                _progress = _targetProgress;
                Gliding.Remove(this);
                OnPropertyChanged();
                return;
            }

            if (!Gliding.Contains(this)) Gliding.Add(this);
            if (_glideTimer == null)
            {
                _glideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _glideTimer.Tick += (_, _) => GlideStep();
            }
            if (!_glideTimer.IsEnabled) _glideTimer.Start();
        }
    }

    private static void GlideStep()
    {
        for (int i = Gliding.Count - 1; i >= 0; i--)
        {
            var vm = Gliding[i];
            double diff = vm._targetProgress - vm._progress;
            if (Math.Abs(diff) < 0.2)
            {
                vm._progress = vm._targetProgress;
                Gliding.RemoveAt(i);
            }
            else vm._progress += diff * 0.45;
            vm.OnPropertyChanged(nameof(Progress));
        }
        if (Gliding.Count == 0) _glideTimer?.Stop();
    }

    public Visibility ProgressVisibility
    {
        get => _progressVisibility;
        set { _progressVisibility = value; OnPropertyChanged(); }
    }

    public string Speed
    {
        get => _speed;
        set { _speed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedVisibility)); }
    }
    public Visibility SpeedVisibility => !string.IsNullOrWhiteSpace(_speed) ? Visibility.Visible : Visibility.Collapsed;

    public string Eta
    {
        get => _eta;
        set { _eta = value; OnPropertyChanged(); OnPropertyChanged(nameof(EtaVisibility)); }
    }
    public Visibility EtaVisibility => !string.IsNullOrWhiteSpace(_eta) ? Visibility.Visible : Visibility.Collapsed;

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            _errorAdvice = string.IsNullOrWhiteSpace(value) ? null : ErrorAdvisor.Analyze(value, Url, true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorVisibility));
            OnPropertyChanged(nameof(ErrorTitle));
            OnPropertyChanged(nameof(ErrorHint));
            OnPropertyChanged(nameof(ErrorTooltip));
            RefreshState();
        }
    }

    private ErrorAdvice? _errorAdvice;

    /// <summary>What went wrong, in words (e.g. "Video unavailable").</summary>
    public string ErrorTitle => _errorAdvice?.Title ?? "";

    /// <summary>The first thing to try.</summary>
    public string ErrorHint => _errorAdvice is { Steps.Count: > 0 } a ? a.Steps[0] : "";

    /// <summary>Explanation, every fix step and the original message.</summary>
    public string ErrorTooltip
    {
        get
        {
            if (_errorAdvice == null) return _errorMessage;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_errorAdvice.Summary);
            for (int i = 0; i < _errorAdvice.Steps.Count; i++)
                sb.AppendLine($"{i + 1}. {_errorAdvice.Steps[i]}");
            sb.AppendLine();
            sb.Append(ErrorAdvisor.StripPrefixes(_errorMessage));
            return sb.ToString();
        }
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            _hasError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ErrorVisibility));
            RefreshState();
        }
    }
    public Visibility ErrorVisibility => _hasError ? Visibility.Visible : Visibility.Collapsed;


    public void UpdateCloudConfig(bool isCloudActive, string provider)
    {
        _isCloudActive = isCloudActive;
        _cloudProvider = provider;
        OnPropertyChanged(nameof(IsCloudActive));
        OnPropertyChanged(nameof(CloudProvider));
        OnPropertyChanged(nameof(CloudBadgeVisibility));
        OnPropertyChanged(nameof(CloudBadgeText));
    }

    public Brush CardBorderBrush
    {
        get
        {
            if (_hasError) return BrushCardBorderError;
            if (_isCloudUploading) return BrushCardBorderCloud;
            if (_isDownloading) return BrushCardBorderDownloading;
            if (_isCompleted) return BrushCardBorderSuccess;
            return BrushCardBorderPending;
        }
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(ActivePillVisibility));
        OnPropertyChanged(nameof(ActivePillSymbol));
        OnPropertyChanged(nameof(ActivePillText));
        OnPropertyChanged(nameof(ActivePillForeground));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(CloudBadgeVisibility));
        OnPropertyChanged(nameof(CloudBadgeText));
        OnPropertyChanged(nameof(CloudStatusVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BatchSessionData
{
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string OutputDirectory { get; set; } = "";
    public string Quality { get; set; } = "best";
    public int MaxConcurrency { get; set; } = 2;
    public bool AutoRetry { get; set; }
    public bool AutoRemoveDone { get; set; }
    public bool CloudUploadEnabled { get; set; }
    public List<BatchItemDto> Items { get; set; } = new();
    public List<BatchItemDto> StoredItems { get; set; } = new();
}

public class BatchItemDto
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Uploader { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public double Duration { get; set; }
    public bool IsSelected { get; set; } = true;
    public bool IsCompleted { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "";
    public double Progress { get; set; }
    public string StatusText { get; set; } = "Pending";
    public bool IsStored { get; set; }
    public string SelectedQuality { get; set; } = "";
}
