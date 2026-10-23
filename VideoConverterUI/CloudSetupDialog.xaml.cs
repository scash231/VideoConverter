using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VideoConverterUI.Services;

namespace VideoConverterUI;

public partial class CloudSetupDialog : Window
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private bool _isClosing;

    public CloudSetupDialog(string? initialProvider = null)
    {
        InitializeComponent();
        Loaded += (_, _) => OnLoaded(initialProvider);
    }

    private void OnLoaded(string? initialProvider)
    {
        var s = SettingsService.Current;

        TxtGDrivePath.Text = s.GDriveFolderPath ?? "";
        TxtDropboxPath.Text = s.DropboxFolderPath ?? "";
        PwdDropboxToken.Password = s.DropboxApiToken ?? "";
        TxtOneDrivePath.Text = s.OneDriveFolderPath ?? "";
        TxtWebhookUrl.Text = s.WebhookUrl ?? "";
        ChkCloudTempOnly.IsChecked = s.CloudTempDownload;

        UpdateStatuses();

        // Select initial provider tab
        switch (initialProvider?.ToLowerInvariant())
        {
            case "dropbox":
                TabDropbox.IsChecked = true;
                break;
            case "onedrive":
                TabOneDrive.IsChecked = true;
                break;
            case "webhook":
                TabWebhook.IsChecked = true;
                break;
            default:
                TabGDrive.IsChecked = true;
                break;
        }
        UpdateTabVisibility();

        if (TryFindResource("FadeInDialog") is Storyboard sb)
        {
            sb.Begin();
        }
    }

    private void ProviderTab_Click(object sender, RoutedEventArgs e)
    {
        UpdateTabVisibility();
    }

    private void UpdateTabVisibility()
    {
        PanelGDrive.Visibility = TabGDrive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDropbox.Visibility = TabDropbox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelOneDrive.Visibility = TabOneDrive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelWebhook.Visibility = TabWebhook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatuses()
    {
        var s = SettingsService.Current;

        // Google Drive status
        var (_, gdSummary, _) = CloudStorageService.GetProviderStatus("gdrive", s);
        TxtGDriveStatus.Text = $"Status: {gdSummary}";

        // Dropbox status
        var (_, dbSummary, _) = CloudStorageService.GetProviderStatus("dropbox", s);
        TxtDropboxStatus.Text = $"Status: {dbSummary}";

        // OneDrive status
        var (_, odSummary, _) = CloudStorageService.GetProviderStatus("onedrive", s);
        TxtOneDriveStatus.Text = $"Status: {odSummary}";

        // Webhook status
        var (_, whSummary, _) = CloudStorageService.GetProviderStatus("webhook", s);
        TxtWebhookStatus.Text = $"Status: {whSummary}";
    }

    // ── Google Drive Actions ──
    private void AutoDetectGDrive_Click(object sender, RoutedEventArgs e)
    {
        var detected = CloudStorageService.DetectGoogleDriveFolder();
        if (detected != null)
        {
            TxtGDrivePath.Text = detected;
            TxtGDriveStatus.Text = $"Status: Auto-detected ({detected})";
        }
        else
        {
            TxtGDriveStatus.Text = "Status: Google Drive client not detected on system";
        }
    }

    private void BrowseGDrive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Google Drive Folder" };
        if (dlg.ShowDialog() == true)
        {
            TxtGDrivePath.Text = dlg.FolderName;
            TxtGDriveStatus.Text = $"Status: Custom folder selected";
        }
    }

    // ── Dropbox Actions ──
    private async void TestDropbox_Click(object sender, RoutedEventArgs e)
    {
        var token = PwdDropboxToken.Password.Trim();
        if (string.IsNullOrEmpty(token))
        {
            TxtDropboxStatus.Text = "Status: Please enter a token first";
            return;
        }

        TxtDropboxStatus.Text = "Status: Testing API token...";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req);

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                string name = "User";
                if (doc.RootElement.TryGetProperty("name", out var n) && n.TryGetProperty("display_name", out var dn))
                    name = dn.GetString() ?? name;

                TxtDropboxStatus.Text = $"Status: Connected as '{name}'";
            }
            else
            {
                TxtDropboxStatus.Text = $"Status: Invalid Token ({resp.StatusCode})";
            }
        }
        catch (Exception ex)
        {
            TxtDropboxStatus.Text = $"Status: Connection failed ({ex.Message})";
        }
    }

    private void BrowseDropbox_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Dropbox Folder" };
        if (dlg.ShowDialog() == true)
        {
            TxtDropboxPath.Text = dlg.FolderName;
            TxtDropboxStatus.Text = "Status: Custom folder selected";
        }
    }

    // ── OneDrive Actions ──
    private void AutoDetectOneDrive_Click(object sender, RoutedEventArgs e)
    {
        var detected = CloudStorageService.DetectOneDriveFolder();
        if (detected != null)
        {
            TxtOneDrivePath.Text = detected;
            TxtOneDriveStatus.Text = $"Status: Auto-detected ({detected})";
        }
        else
        {
            TxtOneDriveStatus.Text = "Status: OneDrive not detected on system";
        }
    }

    private void BrowseOneDrive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select OneDrive Folder" };
        if (dlg.ShowDialog() == true)
        {
            TxtOneDrivePath.Text = dlg.FolderName;
            TxtOneDriveStatus.Text = "Status: Custom folder selected";
        }
    }

    // ── Webhook Actions ──
    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtWebhookUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            TxtWebhookStatus.Text = "Status: Please enter a Webhook URL first";
            return;
        }

        TxtWebhookStatus.Text = "Status: Sending test ping to Webhook...";
        try
        {
            var payload = JsonSerializer.Serialize(new { content = "🎬 **VideoConverter**: Webhook test successful!" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(url, content);

            if (resp.IsSuccessStatusCode)
            {
                TxtWebhookStatus.Text = "Status: Webhook connection verified successfully!";
            }
            else
            {
                TxtWebhookStatus.Text = $"Status: Webhook returned error ({resp.StatusCode})";
            }
        }
        catch (Exception ex)
        {
            TxtWebhookStatus.Text = $"Status: Webhook error ({ex.Message})";
        }
    }

    // ── Save & Close ──
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;

        s.GDriveFolderPath = TxtGDrivePath.Text.Trim();
        s.DropboxFolderPath = TxtDropboxPath.Text.Trim();
        s.DropboxApiToken = PwdDropboxToken.Password.Trim();
        s.OneDriveFolderPath = TxtOneDrivePath.Text.Trim();
        s.WebhookUrl = TxtWebhookUrl.Text.Trim();
        s.CloudTempDownload = ChkCloudTempOnly.IsChecked == true;

        if (TabGDrive.IsChecked == true) s.CloudProvider = "gdrive";
        else if (TabDropbox.IsChecked == true) s.CloudProvider = "dropbox";
        else if (TabOneDrive.IsChecked == true) s.CloudProvider = "onedrive";
        else if (TabWebhook.IsChecked == true) s.CloudProvider = "webhook";

        SettingsService.Save(s);
        CloseAnimated();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();

    private void CloseAnimated()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (TryFindResource("FadeOutDialog") is Storyboard sb)
        {
            sb.Completed += (_, _) => Close();
            sb.Begin();
        }
        else
        {
            Close();
        }
    }

}
