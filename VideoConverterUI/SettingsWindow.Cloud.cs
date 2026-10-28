using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VideoConverterUI.Services;

namespace VideoConverterUI;

// Settings → Cloud → Configuration (sub-page PaneCloudConfig): where each service's uploads go, every service in its
// own section under the others. Was CloudSetupDialog. Everything applies at once like the rest of Settings; Cloud →
// Service picks the service in use, marked "In Use" here.
public partial class SettingsWindow
{
    private static readonly HttpClient CloudTestHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private void BindCloudConfig()
    {
        BindText(TxtWebhookUrl, s => s.WebhookUrl ?? "", (s, v) => s.WebhookUrl = v);
        _reloaders.Add(() => PwdDropboxToken.Password = SettingsService.Current.DropboxApiToken ?? "");
        PwdDropboxToken.LostKeyboardFocus += (_, _) => ApplyDropboxToken();
        PwdDropboxToken.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyDropboxToken(); e.Handled = true; } };
        _reloaders.Add(UpdateCloudConfigRows);
    }

    private void ApplyDropboxToken()
    {
        if (_loading) return;
        var token = PwdDropboxToken.Password.Trim();
        if (token == (SettingsService.Current.DropboxApiToken ?? "")) return;
        Commit(s => s.DropboxApiToken = token);
        TxtDropboxTest.Text = L("PrefCloudNotTested");
        UpdateCloudConfigRows();
    }

    /// <summary>Opens Configuration; with a service ("dropbox", "onedrive", "webhook") it scrolls to that one's section.</summary>
    private void OpenCloudConfig(string? provider)
    {
        TxtDropboxTest.Text = TxtWebhookTest.Text = L("PrefCloudNotTested");
        if (_activePane != PaneCloudConfig) OpenSubPage(PaneCloudConfig, PaneCloud, "PrefCloudConfig");
        UpdateCloudConfigRows();

        FrameworkElement? section = provider switch
        {
            "dropbox" => CloudCfgDropbox,
            "onedrive" => CloudCfgOneDrive,
            "webhook" => CloudCfgWebhook,
            _ => null,
        };
        if (section == null) return;
        // After the page has its layout: its header at the top, below the title.
        Dispatcher.BeginInvoke(() =>
        {
            if (_activePane != PaneCloudConfig || !section.IsVisible) return;
            double y = section.TransformToAncestor(PaneCloudConfig).Transform(new Point()).Y;
            TabContentScroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OpenCloudConfig_Click(object sender, RoutedEventArgs e) => OpenCloudConfig(null);

    /// <summary>The folder rows (chosen folder, the one found automatically, or none), "In Use", and the Cloud pane's Configuration value.</summary>
    private void UpdateCloudConfigRows()
    {
        var s = SettingsService.Current;
        var provider = s.CloudProvider ?? "gdrive";
        InUseGDrive.Visibility = provider == "gdrive" ? Visibility.Visible : Visibility.Collapsed;
        InUseDropbox.Visibility = provider == "dropbox" ? Visibility.Visible : Visibility.Collapsed;
        InUseOneDrive.Visibility = provider == "onedrive" ? Visibility.Visible : Visibility.Collapsed;
        InUseWebhook.Visibility = provider == "webhook" ? Visibility.Visible : Visibility.Collapsed;

        // Detection looks through the disk, so it only runs while the page is open.
        bool open = _activePane == PaneCloudConfig;
        ShowFolder(TxtGDriveFolder, BtnClearGDrive, s.GDriveFolderPath, open ? CloudStorageService.DetectGoogleDriveFolder : null);
        ShowFolder(TxtDropboxFolder, BtnClearDropbox, s.DropboxFolderPath, open ? CloudStorageService.DetectDropboxFolder : null);
        ShowFolder(TxtOneDriveFolder, BtnClearOneDrive, s.OneDriveFolderPath, open ? CloudStorageService.DetectOneDriveFolder : null);

        // On the Cloud pane: the service in use, or that it still needs setting up.
        bool ready = provider == "custom" || CloudStorageService.GetProviderStatus(provider, s).ready;
        TxtCloudConfigValue.Text = ready ? CloudStorageService.GetProviderDisplayName(provider) : L("PrefCloudNotSet");
    }

    private static void ShowFolder(TextBlock text, Button clear, string? chosen, Func<string?>? detect)
    {
        bool hasChosen = !string.IsNullOrWhiteSpace(chosen);
        clear.Visibility = hasChosen ? Visibility.Visible : Visibility.Collapsed;
        if (hasChosen) text.Text = chosen!;
        else if (detect == null) return;
        else text.Text = detect() is { } found ? string.Format(L("PrefCloudFolderFound"), found) : L("PrefCloudFolderMissing");
        text.ToolTip = text.Text;
    }

    private void CloudChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var provider = (sender as FrameworkElement)?.Tag as string;
        var s = SettingsService.Current;
        var current = provider switch { "dropbox" => s.DropboxFolderPath, "onedrive" => s.OneDriveFolderPath, _ => s.GDriveFolderPath };
        var dlg = new OpenFolderDialog { Title = CloudStorageService.GetProviderDisplayName(provider ?? "gdrive") };
        if (!string.IsNullOrWhiteSpace(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;
        SetCloudFolder(provider, dlg.FolderName);
    }

    /// <summary>Back to the folder found automatically.</summary>
    private void CloudClearFolder_Click(object sender, RoutedEventArgs e) => SetCloudFolder((sender as FrameworkElement)?.Tag as string, "");

    private void SetCloudFolder(string? provider, string folder)
    {
        Commit(s =>
        {
            switch (provider)
            {
                case "dropbox": s.DropboxFolderPath = folder; break;
                case "onedrive": s.OneDriveFolderPath = folder; break;
                default: s.GDriveFolderPath = folder; break;
            }
        });
        UpdateCloudConfigRows();
    }

    private async void TestDropbox_Click(object sender, RoutedEventArgs e)
    {
        ApplyDropboxToken();
        var token = PwdDropboxToken.Password.Trim();
        if (token.Length == 0) { TxtDropboxTest.Text = L("PrefCloudNeedToken"); return; }

        TxtDropboxTest.Text = L("PrefCloudTesting");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await CloudTestHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                TxtDropboxTest.Text = string.Format(L("PrefCloudTokenBad"), (int)response.StatusCode);
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string name = doc.RootElement.TryGetProperty("name", out var n) && n.TryGetProperty("display_name", out var dn)
                ? dn.GetString() ?? "Dropbox" : "Dropbox";
            TxtDropboxTest.Text = string.Format(L("PrefCloudTokenOk"), name);
        }
        catch (Exception ex)
        {
            TxtDropboxTest.Text = string.Format(L("PrefCloudTestFailed"), ex.Message);
        }
    }

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtWebhookUrl.Text.Trim();
        if (url != (SettingsService.Current.WebhookUrl ?? "")) { Commit(s => s.WebhookUrl = url); UpdateCloudConfigRows(); }
        if (url.Length == 0) { TxtWebhookTest.Text = L("PrefCloudNeedUrl"); return; }

        TxtWebhookTest.Text = L("PrefCloudTesting");
        try
        {
            var payload = JsonSerializer.Serialize(new { content = "🎬 **VideoConverter**: Webhook test successful!" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await CloudTestHttp.PostAsync(url, content);
            TxtWebhookTest.Text = response.IsSuccessStatusCode
                ? L("PrefCloudWebhookOk")
                : string.Format(L("PrefCloudWebhookBad"), (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            TxtWebhookTest.Text = string.Format(L("PrefCloudTestFailed"), ex.Message);
        }
    }
}
