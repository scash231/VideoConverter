using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public partial class FFmpegInstallerDialog : Window
{
    public bool InstalledSuccessfully { get; private set; }
    private readonly CancellationTokenSource _cts = new();
    private bool _isClosingAnimated;

    public FFmpegInstallerDialog()
    {
        InitializeComponent();
        Loaded += FFmpegInstallerDialog_Loaded;
    }

    private void FFmpegInstallerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        (Resources["FadeInDialog"] as Storyboard)?.Begin();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && BtnCancel.IsEnabled)
        {
            CloseWithAnimation();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (SettingsService.Current.EnablePerformanceMode)
        {
            base.OnClosing(e);
            return;
        }

        if (!_isClosingAnimated)
        {
            e.Cancel = true;
            CloseWithAnimation();
            return;
        }

        base.OnClosing(e);
    }

    private void CloseWithAnimation()
    {
        if (_isClosingAnimated) return;
        _isClosingAnimated = true;
        _cts.Cancel();

        if (Resources["FadeOutDialog"] is Storyboard sb)
        {
            sb.Completed += (_, _) =>
            {
                Close();
            };
            sb.Begin();
        }
        else
        {
            Close();
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        BtnClose.IsEnabled = false;
        ChkAddToUserPath.IsEnabled = false;
        ChkAddToSystemPath.IsEnabled = false;

        PanelPrompt.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Visible;
        TxtInstallInfo.Text = L("Please keep the application open during download.");

        var progress = new Progress<(double percent, string status)>(update =>
        {
            TxtPercent.Text = $"{update.percent:F0}%";
            TxtStatus.Text = update.status;
            TxtDetail.Text = update.percent >= 80 ? L("FfmpegStatusExtracting") : $"{update.percent:F0}%";

            var maxBarWidth = RootBorder.ActualWidth > 50 ? RootBorder.ActualWidth - 40 : 420;
            ProgressBarFill.Width = Math.Max(0, Math.Min(maxBarWidth, (update.percent / 100.0) * maxBarWidth));
        });

        try
        {
            bool addToUser = ChkAddToUserPath.IsChecked == true;
            bool addToSystem = ChkAddToSystemPath.IsChecked == true;

            var success = await FFmpegInstallerService.InstallFfmpegAsync(
                progress, addToUser, addToSystem, _cts.Token);

            if (success)
            {
                InstalledSuccessfully = true;
                TxtStatus.Text = L("FfmpegStatusReady");
                TxtPercent.Text = "100%";
                TxtDetail.Text = L("FfmpegDetailSuccess");
                var maxBarWidth = RootBorder.ActualWidth > 50 ? RootBorder.ActualWidth - 40 : 420;
                ProgressBarFill.Width = maxBarWidth;

                await Task.Delay(1000);
                CloseWithAnimation();
            }
            else
            {
                TxtStatus.Text = L("FfmpegStatusFailed");
                TxtDetail.Text = L("FfmpegDetailFailed");
                BtnCancel.IsEnabled = true;
                BtnClose.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = L("FfmpegStatusCancelled");
            TxtDetail.Text = L("FfmpegDetailCancelled");
            BtnCancel.IsEnabled = true;
            BtnClose.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = L("FfmpegStatusFailed");
            TxtDetail.Text = ex.Message;
            BtnCancel.IsEnabled = true;
            BtnClose.IsEnabled = true;
            BtnInstall.IsEnabled = true;
            BtnInstall.Content = L("Retry");
        }
    }

    private string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? TryFindResource(key) as string ?? key;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _cts.Dispose();
    }
}
