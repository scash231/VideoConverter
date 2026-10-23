using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public enum BatchSaveResult
{
    Cancel,
    SaveAndExit,
    ExitWithoutSaving
}

public partial class BatchSavePromptDialog : Window
{
    public BatchSaveResult Result { get; private set; } = BatchSaveResult.Cancel;
    public bool NeverAskAgain => ChkDontAskAgain.IsChecked == true;
    private bool _isClosingAnimated;

    public BatchSavePromptDialog()
    {
        InitializeComponent();
        Loaded += BatchSavePromptDialog_Loaded;
    }

    private void BatchSavePromptDialog_Loaded(object sender, RoutedEventArgs e)
    {
        (Resources["FadeInDialog"] as Storyboard)?.Begin();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Result = BatchSaveResult.Cancel;
            CloseWithAnimation();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Result = BatchSaveResult.SaveAndExit;
        CloseWithAnimation();
    }

    private void BtnDontSave_Click(object sender, RoutedEventArgs e)
    {
        Result = BatchSaveResult.ExitWithoutSaving;
        CloseWithAnimation();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = BatchSaveResult.Cancel;
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
}