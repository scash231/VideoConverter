using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace VideoConverterSetup;

public partial class MainWindow : Window
{
    private ReleaseManifest? _manifest;
    private InstalledInfo? _installed;
    private string _installDir = SetupPaths.DefaultInstallDir;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _confirmClose;
    private bool _confirmCancel;
    private bool _uninstalled;
    private bool _runningConfirmed;
    private FrameworkElement? _page;
    private Func<Task>? _retry;

    // the window opens as a small cube with just the spinner and grows to full size once there is a page to show
    private const double CubeSize = 132;
    private const double CubeRadius = 18;
    private const double FullRadius = 10;
    private static readonly TimeSpan MinCubeTime = TimeSpan.FromMilliseconds(1200);
    private readonly Stopwatch _cubeShown = new();
    private Task? _expand;

    private static readonly DependencyProperty ExpandProperty = DependencyProperty.Register(
        "Expand", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyExpand()));

    public MainWindow()
    {
        InitializeComponent();
        TaskbarItemInfo = new TaskbarItemInfo();
        TitleBar.Opacity = 0;
        PageLoading.Visibility = Visibility.Collapsed;
        ApplyExpand();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cubeShown.Start();
        AnimateWindowIn();
        if (App.UninstallMode)
        {
            await ExpandAsync();
            ShowUninstallPage(fromWelcome: false);
            return;
        }
        await LoadManifestAsync();
    }

    // ── Pages ──────────────────────────────────────────────────────────────

    private void ShowPage(FrameworkElement page)
    {
        if (_page == page) return;
        var old = _page;
        _page = page;
        foreach (var child in Pages.Children)
            if (child is FrameworkElement fe && fe != page && fe != old) fe.Visibility = Visibility.Collapsed;

        if (old != null)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { if (_page != old) old.Visibility = Visibility.Collapsed; };
            old.BeginAnimation(OpacityProperty, fade);
        }

        page.Visibility = Visibility.Visible;
        page.Opacity = 0;
        var shift = new TranslateTransform(0, 14);
        page.RenderTransform = shift;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(old != null ? 90 : 0);
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(340)) { BeginTime = begin, EasingFunction = ease });
    }

    private void AnimateWindowIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(0.8, 0.8);
        Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        Shell.RenderTransform = scale;
        Shell.Opacity = 0;
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    /// <summary>Grows the cube into the full window (once; later calls return the same task).</summary>
    private Task ExpandAsync() => _expand ??= ExpandCoreAsync();

    private async Task ExpandCoreAsync()
    {
        var wait = MinCubeTime - _cubeShown.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        var done = new TaskCompletionSource<bool>();
        var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut } };
        grow.Completed += (_, _) => done.TrySetResult(true);
        BeginAnimation(ExpandProperty, grow);
        Splash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        await done.Task;

        // hand the size back to the layout
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        Shell.CornerRadius = new CornerRadius(FullRadius);
        ShellGrid.Children.Remove(Splash);
        TitleBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>Sizes the shell between the cube (Expand 0) and the full window (Expand 1).</summary>
    private void ApplyExpand()
    {
        var t = (double)GetValue(ExpandProperty);
        double fullW = Width - Shell.Margin.Left - Shell.Margin.Right;
        double fullH = Height - Shell.Margin.Top - Shell.Margin.Bottom;
        Shell.CornerRadius = new CornerRadius(Math.Max(4, CubeRadius + (FullRadius - CubeRadius) * t));
        Shell.Width = CubeSize + (fullW - CubeSize) * t;
        Shell.Height = CubeSize + (fullH - CubeSize) * t;
    }

    private void ShellGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var r = Shell.CornerRadius.TopLeft - 1;
        ShellGrid.Clip = new RectangleGeometry(new Rect(e.NewSize), r, r);
    }

    private void ShowError(string message, Func<Task>? retry, string title = "Setup couldn't finish", string retryText = "Try again")
    {
        _retry = retry;
        TxtErrorTitle.Text = title;
        TxtError.Text = message;
        BtnRetry.Content = retryText;
        BtnRetry.Visibility = retry != null ? Visibility.Visible : Visibility.Collapsed;
        TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error;
        ShowPage(PageError);
    }

    // ── Manifest + welcome page ────────────────────────────────────────────

    private async Task LoadManifestAsync()
    {
        // the first time the cube's spinner is the loading page
        if (_expand != null) ShowPage(PageLoading);
        try
        {
            _manifest = await ReleaseManifest.LoadAsync(App.ManifestLocation, CancellationToken.None);
            Log.Write("manifest: " + _manifest);
        }
        catch (Exception ex)
        {
            Log.Write("manifest failed: " + ex);
            var msg = ex is SetupException ? ex.Message
                : ex is System.Net.Http.HttpRequestException ? "Setup couldn't reach the download server. Check your internet connection and try again."
                : ex.Message;
            await ExpandAsync();
            ShowError(msg, LoadManifestAsync, "Can't reach the download server");
            return;
        }
        if (await SelfUpdate.TryStartNewerAsync(_manifest, CancellationToken.None))
        {
            Close();
            return;
        }
        await ExpandAsync();
        if (_manifest.NeedsNewerSetup)
        {
            ShowError("This setup is too old for the current release. Update it to continue.",
                UpdateSetupAsync, "Setup needs an update", "Update installer");
            return;
        }
        PrepareWelcome();
        ShowPage(PageWelcome);
    }

    /// <summary>"Update installer": starts the newest setup from the manifest, or opens the download page when there is none to fetch.</summary>
    private async Task UpdateSetupAsync()
    {
        BtnRetry.IsEnabled = false;
        BtnRetry.Content = "Updating…";
        if (await SelfUpdate.TryStartNewerAsync(_manifest!, CancellationToken.None, force: true))
        {
            Close();
            return;
        }
        try { Process.Start(new ProcessStartInfo(SetupPaths.AboutUrl) { UseShellExecute = true }); } catch { }
        BtnRetry.IsEnabled = true;
        BtnRetry.Content = "Update installer";
    }

    private void PrepareWelcome()
    {
        var m = _manifest!;
        _installed = InstalledInfo.Read();
        if (_installed != null && Directory.Exists(_installed.InstallDir))
        {
            _installDir = _installed.InstallDir;
            ChkDesktop.IsChecked = _installed.DesktopShortcut;
            BtnChangeDir.Visibility = Visibility.Collapsed;
            BtnUninstallLink.Visibility = Visibility.Visible;
            var same = string.Equals(_installed.Version, m.Version, StringComparison.OrdinalIgnoreCase);
            TxtWelcomeTitle.Text = same ? "Reinstall VideoConverter" : "Update VideoConverter";
            TxtWelcomeSub.Text = same
                ? $"Version {m.Version} is already installed. Reinstalling repairs it and updates yt-dlp."
                : $"Version {_installed.Version} is installed. Setup updates it to {m.Version} and keeps your settings.";
            BtnInstall.Content = same ? "Reinstall" : "Update";
        }
        else
        {
            _installed = null;
            _installDir = App.PresetDir != null ? SetupPaths.NormalizeInstallDir(App.PresetDir) : SetupPaths.DefaultInstallDir;
        }
        TxtDir.Text = _installDir;

        var runtime = m.EffectiveRuntime;
        var needsRuntime = !InstallerEngine.HasDesktopRuntime(runtime);
        var needsFfmpeg = !InstallerEngine.HasFfmpeg();
        long bytes = m.AppDownloadBytes + 18L * 1024 * 1024
                     + (needsRuntime ? runtime.Size : 0)
                     + (needsFfmpeg ? 100L * 1024 * 1024 : 0);

        TxtSpecVersion.Text = m.Version;
        TxtSpecDownload.Text = $"about {Math.Ceiling(bytes / 1048576.0 / 5) * 5:0} MB";
        var extras = new System.Collections.Generic.List<string>();
        if (needsRuntime) extras.Add($".NET {runtime.Major} Runtime");
        if (needsFfmpeg) extras.Add("FFmpeg");
        TxtSpecIncludes.Text = string.Join(", ", extras);
        RowIncludes.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRunningNote();
    }

    private bool UpdateRunningNote()
    {
        var running = InstallerEngine.FindRunning(_installDir, SetupPaths.OldMsiDir);
        var any = running.Count > 0;
        foreach (var p in running) p.Dispose();
        TxtRunningNote.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        BtnUninstallLink.Visibility = !any && _installed != null ? Visibility.Visible : Visibility.Collapsed;
        return any;
    }

    private void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_installDir);
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
        var picked = NativeMethods.PickFolder(new WindowInteropHelper(this).Handle, parent, "Choose where to install VideoConverter");
        if (picked == null) return;
        _installDir = SetupPaths.NormalizeInstallDir(picked);
        TxtDir.Text = _installDir;
        TxtDirError.Visibility = Visibility.Collapsed;
    }

    // ── Install ────────────────────────────────────────────────────────────

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        // first click only warns that the running app will be closed
        if (UpdateRunningNote() && !_runningConfirmed)
        {
            _runningConfirmed = true;
            BtnInstall.Content = "Close it and continue";
            return;
        }

        var problem = SetupPaths.CheckInstallDir(_installDir);
        if (problem != null)
        {
            TxtDirError.Text = problem;
            TxtDirError.Visibility = Visibility.Visible;
            return;
        }
        await RunInstallAsync();
    }

    private Task RunInstallAsync()
    {
        var engine = new InstallerEngine();
        engine.PlanInstall(_manifest!, new InstallOptions { InstallDir = _installDir, DesktopShortcut = ChkDesktop.IsChecked == true });
        TxtProgressTitle.Text = _installed == null ? "Installing VideoConverter" : "Updating VideoConverter";
        TxtProgressSub.Text = "This takes a minute or two, depending on your connection.";
        return RunEngineAsync(engine, uninstall: false);
    }

    private async Task RunEngineAsync(InstallerEngine engine, bool uninstall)
    {
        StepList.ItemsSource = engine.Steps;
        Bar.BeginAnimation(RangeBase.ValueProperty, null);
        Bar.Value = 0;
        TxtPercent.Text = "0 %";
        BtnCancel.Visibility = uninstall ? Visibility.Collapsed : Visibility.Visible;
        BtnCancel.Content = "Cancel";
        _confirmCancel = false;
        TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
        TaskbarItemInfo.ProgressValue = 0;
        engine.OverallProgress += SetProgress;
        ShowPage(PageProgress);

        _cts = new CancellationTokenSource();
        _busy = true;
        try
        {
            await engine.RunAsync(_cts.Token);
            SetProgress(1);
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            if (uninstall) ShowUninstallDone(); else ShowInstallDone(engine);
        }
        catch (OperationCanceledException)
        {
            ShowError("Setup was stopped. Run it again any time to finish the installation.", uninstall ? null : RunInstallAsync, "Setup was cancelled");
        }
        catch (Exception ex)
        {
            Log.Write("failed: " + ex);
            var msg = ex is SetupException ? ex.Message : ex.Message + "\n\n(" + ex.GetType().Name + ")";
            ShowError(msg, uninstall ? null : RunInstallAsync);
        }
        finally
        {
            _busy = false;
            engine.OverallProgress -= SetProgress;
            _cts.Dispose();
            _cts = null;
            if (_confirmClose) Close();
        }
    }

    private void SetProgress(double value)
    {
        value = Math.Max(0, Math.Min(1, value));
        Bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        TxtPercent.Text = $"{Math.Floor(value * 100):0} %";
        TaskbarItemInfo.ProgressValue = value;
    }

    private void ShowInstallDone(InstallerEngine engine)
    {
        TxtDoneTitle.Text = _installed == null ? "VideoConverter is ready" : "VideoConverter is up to date";
        TxtDoneSub.Text = ChkDesktop.IsChecked == true
            ? "You'll find it on your desktop and in the Start menu."
            : "You'll find it in the Start menu.";
        WarningList.ItemsSource = engine.Warnings;
        ChkLaunch.Visibility = Visibility.Visible;
        BtnFinish.Content = "Finish";
        ShowPage(PageDone);
        PopBadge();
    }

    private void PopBadge()
    {
        var scale = (ScaleTransform)DoneBadge.RenderTransform;
        var spring = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = spring };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (!_uninstalled && ChkLaunch.IsChecked == true)
        {
            var exe = Path.Combine(_installDir, SetupPaths.AppExe);
            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _installDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("launch failed: " + ex);
            }
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        if (!_confirmCancel)
        {
            _confirmCancel = true;
            BtnCancel.Content = "Stop setup?";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_cts != null && !_cts.IsCancellationRequested) { _confirmCancel = false; BtnCancel.Content = "Cancel"; }
            };
            timer.Start();
            return;
        }
        BtnCancel.Content = "Stopping…";
        BtnCancel.IsEnabled = false;
        _cts.Cancel();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_retry != null) await _retry();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true }); } catch { }
    }

    // ── Uninstall ──────────────────────────────────────────────────────────

    private bool _uninstallFromWelcome;

    private void ShowUninstallPage(bool fromWelcome)
    {
        _uninstallFromWelcome = fromWelcome;
        _installed ??= InstalledInfo.Read();
        if (_installed == null)
        {
            ShowError("VideoConverter isn't installed for this Windows user, so there is nothing to remove.", null, "Nothing to uninstall");
            return;
        }
        TxtTitleMode.Text = "Uninstall";
        TxtUninstallDir.Text = _installed.InstallDir;
        BtnUninstallCancel.Content = fromWelcome ? "Back" : "Cancel";
        ShowPage(PageUninstall);
    }

    private void UninstallLink_Click(object sender, RoutedEventArgs e) => ShowUninstallPage(fromWelcome: true);

    private void UninstallCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_uninstallFromWelcome) { Close(); return; }
        TxtTitleMode.Text = "Setup";
        ShowPage(PageWelcome);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var engine = new InstallerEngine();
        engine.PlanUninstall(_installed!, ChkRemoveData.IsChecked == true);
        TxtProgressTitle.Text = "Uninstalling VideoConverter";
        TxtProgressSub.Text = "This only takes a moment.";
        await RunEngineAsync(engine, uninstall: true);
    }

    private void ShowUninstallDone()
    {
        _uninstalled = true;
        TxtDoneTitle.Text = "VideoConverter was removed";
        TxtDoneSub.Text = ChkRemoveData.IsChecked == true
            ? "The app, its settings and its tools are gone."
            : "Your settings were kept in case you install it again.";
        WarningList.ItemsSource = null;
        ChkLaunch.Visibility = Visibility.Collapsed;
        BtnFinish.Content = "Close";
        ShowPage(PageDone);
        PopBadge();
    }

    // ── Window ─────────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { Cancel_Click(sender, e); return; }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            // uninstall can't be stopped halfway; install asks first
            e.Cancel = true;
            if (_cts != null && BtnCancel.Visibility == Visibility.Visible)
            {
                _confirmClose = _confirmCancel;
                Cancel_Click(this, new RoutedEventArgs());
            }
            return;
        }
        if (_uninstalled && _installed != null) InstallerEngine.ScheduleSelfDelete(_installed.InstallDir);
        base.OnClosing(e);
    }
}
