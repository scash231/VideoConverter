using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;

namespace VideoConverterUI;

// Settings → General → Software Update (sub-page PaneUpdate), like System Settings: Automatic updates and the channel
// on top; below either the state (checking, up to date, couldn't check) centred under the app's icon, or the update
// as a card with More Info… (its notes from the manifest, as a sub-page) and Update Now. UpdateService does the work.
public partial class SettingsWindow
{
    private static readonly TimeSpan UpdateRecheckAfter = TimeSpan.FromMinutes(5);

    private void BindUpdate()
    {
        BindSwitch(ChkAutoCheckUpdates, s => s.AutoCheckUpdates, (s, v) => s.AutoCheckUpdates = v);
        TxtUpdateChannel.Text = BuildInfoService.BuildChannel;
        _reloaders.Add(UpdateUpdateUi); // texts set here follow the language
    }

    /// <summary>Opening the pane looks again, as System Settings does, unless it just did.</summary>
    private void OnUpdatePaneShown()
    {
        var state = UpdateService.State;
        if (state is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        if (state is UpdateState.Idle or UpdateState.Failed
            || UpdateService.LastChecked is not { } last || DateTime.Now - last > UpdateRecheckAfter)
            _ = UpdateService.CheckAsync();
    }

    private void UpdateUpdateUi()
    {
        var state = UpdateService.State;
        var update = UpdateService.Available;
        bool available = update != null && state is UpdateState.Available or UpdateState.Downloading or UpdateState.Installing;
        NavGeneralBadge.Visibility = UpdateRowBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        TxtUpdateRowValue.Text = available ? update!.Version : BuildInfoService.BuildVersion;
        UpdateAvailablePanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus.Visibility = available ? Visibility.Collapsed : Visibility.Visible;

        if (available)
        {
            SetUpdateSpinner(false);
            TxtUpdateVersion.Text = $"VideoConverter {update!.Version}";
            var meta = new List<string>();
            if (update.DownloadBytes > 0) meta.Add(string.Format(UiCulture, "{0:0.0} MB", update.DownloadBytes / 1048576.0));
            if (update.Published is { } published) meta.Add(published.ToString("d", UiCulture));
            TxtUpdateMeta.Text = string.Join("  ·  ", meta);
            TxtUpdateMeta.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdateMoreInfo.Visibility = update.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool busy = state is UpdateState.Downloading or UpdateState.Installing;
            BtnUpdateNow.IsEnabled = !busy;
            UpdateProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgressScale.ScaleX = state == UpdateState.Installing ? 1 : UpdateService.Progress;
            TxtUpdateProgress.Text = L(state == UpdateState.Installing ? "PrefUpdateStarting" : "PrefUpdateDownloading");
            return;
        }

        bool checking = state == UpdateState.Checking;
        SetUpdateSpinner(checking);
        UpdateChecking.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        BtnCheckNow.Visibility = checking ? Visibility.Collapsed : Visibility.Visible;
        BtnCheckNow.SetResourceReference(ContentProperty, state == UpdateState.Failed ? "PrefUpdateTryAgain" : "PrefUpdateCheckNow");

        string thisVersion = $"VideoConverter {BuildInfoService.BuildVersion}";
        switch (state)
        {
            case UpdateState.Failed:
                TxtUpdateTitle.Text = L("PrefUpdateFailedTitle");
                TxtUpdateDetail.Text = UpdateService.Error is { } error
                    ? string.Format(L("PrefUpdateFailedDetail"), error)
                    : L("PrefUpdateFailedOffline");
                SetUpdateBadge("#FF9500", Wpf.Ui.Controls.SymbolRegular.Important24);
                break;
            case UpdateState.UpToDate:
                TxtUpdateTitle.Text = L("PrefUpdateUpToDate");
                TxtUpdateDetail.Text = thisVersion + "\n" + LastCheckedText();
                SetUpdateBadge("#34C759", Wpf.Ui.Controls.SymbolRegular.Checkmark24);
                break;
            default: // checking, or not checked yet
                TxtUpdateTitle.Text = thisVersion;
                TxtUpdateDetail.Text = LastCheckedText();
                UpdateStatusBadge.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void SetUpdateBadge(string color, Wpf.Ui.Controls.SymbolRegular glyph)
    {
        UpdateStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        UpdateStatusGlyph.Symbol = glyph;
        UpdateStatusBadge.Visibility = Visibility.Visible;
    }

    /// <summary>The arrow turns only while a check runs (no endless animation left behind on a hidden page).</summary>
    private void SetUpdateSpinner(bool spinning)
    {
        if (!spinning || FluidMotion.AnimationsDisabled)
        {
            UpdateSpinnerTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            return;
        }
        if (UpdateSpinnerTurn.HasAnimatedProperties) return;
        UpdateSpinnerTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>"Last checked: today at 14:05" in the app's language.</summary>
    private static string LastCheckedText()
    {
        if (UpdateService.LastChecked is not { } t) return L("PrefUpdateNeverChecked");
        string when = t.Date == DateTime.Today
            ? string.Format(L("PrefUpdateTodayAt"), t.ToString("t", UiCulture))
            : t.ToString("g", UiCulture);
        return string.Format(L("PrefUpdateLastChecked"), when);
    }

    private static CultureInfo UiCulture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(L("LangCode")); }
            catch (CultureNotFoundException) { return CultureInfo.CurrentCulture; }
        }
    }

    private void OpenUpdate_Click(object sender, RoutedEventArgs e) => OpenSubPage(PaneUpdate, PaneGeneral, "PrefSoftwareUpdate");

    private void CheckNow_Click(object sender, RoutedEventArgs e) => _ = UpdateService.CheckAsync();

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } update) return;
        ShowSheet(string.Format(L("PrefUpdateConfirmTitle"), update.Version),
            L(EngineProvider.IsBusy ? "PrefUpdateConfirmBusy" : "PrefUpdateConfirmMessage"),
            L("PrefUpdateInstall"), false,
            onConfirm: () =>
            {
                ThemeService.FlushPendingSave();
                _ = UpdateService.InstallAsync();
            });
    }

    /// <summary>More Info…: the update's notes (from the manifest) on the same page What's New uses.</summary>
    private void UpdateMoreInfo_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } update) return;
        TxtReleaseDate.Text = update.Published?.ToString("d", UiCulture) ?? "";
        TxtReleaseDate.Visibility = update.Published != null ? Visibility.Visible : Visibility.Collapsed;
        ReleaseSections.ItemsSource = update.Notes;
        string updateTitle = TxtPaneTitle.Text; // what Back puts back
        TxtPaneTitle.Text = update.Version;
        PushSubPage(PaneRelease, PaneUpdate, updateTitle);
    }
}
