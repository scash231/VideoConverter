using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public sealed class CloudUploadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DestinationPath { get; set; }
    public string? Url { get; set; }
    public string? Message { get; set; }
}

public static class CloudStorageService
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string GetProviderDisplayName(string providerKey) => providerKey?.ToLowerInvariant() switch
    {
        "gdrive" => "Google Drive",
        "dropbox" => "Dropbox",
        "onedrive" => "Microsoft OneDrive",
        "webhook" => "Discord / Webhook",
        "custom" => "Custom Cloud Folder",
        _ => "Cloud Storage"
    };

    // ═════════════════════════════════════════════════════════════════════════
    //  DETECTION
    // ═════════════════════════════════════════════════════════════════════════

    // Scanning every drive (IsReady / VolumeLabel) can take seconds on slow or optical drives and the
    // status line is refreshed on every UI toggle, so detection results are cached for a short while.
    private static readonly TimeSpan DetectionCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, (DateTime at, string? path)> _detectionCache = new();

    private static string? CachedDetect(string key, Func<string?> detect)
    {
        lock (_detectionCache)
        {
            if (_detectionCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < DetectionCacheDuration)
                return hit.path;
        }

        var path = detect();
        lock (_detectionCache)
            _detectionCache[key] = (DateTime.UtcNow, path);
        return path;
    }

    public static string? DetectGoogleDriveFolder() => CachedDetect("gdrive", DetectGoogleDriveFolderCore);

    public static string? DetectDropboxFolder() => CachedDetect("dropbox", DetectDropboxFolderCore);

    public static string? DetectOneDriveFolder() => CachedDetect("onedrive", DetectOneDriveFolderCore);

    private static string? DetectGoogleDriveFolderCore()
    {
        try
        {
            // 1. Check virtual drives mounted by Google Drive for Desktop
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Optical drives may spin up for seconds just to answer IsReady.
                if (drive.DriveType == DriveType.CDRom || !drive.IsReady) continue;

                // Check volume label
                try
                {
                    if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    {
                        var myDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                        if (Directory.Exists(myDrive)) return myDrive;

                        var meineAblage = Path.Combine(drive.RootDirectory.FullName, "Meine Ablage");
                        if (Directory.Exists(meineAblage)) return meineAblage;

                        return drive.RootDirectory.FullName;
                    }
                }
                catch { }

                // Check typical folder names in drive root
                var testMy = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                if (Directory.Exists(testMy)) return testMy;

                var testAbl = Path.Combine(drive.RootDirectory.FullName, "Meine Ablage");
                if (Directory.Exists(testAbl)) return testAbl;
            }

            // 2. Check standard user profile path
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var gdrivePath = Path.Combine(userProfile, "Google Drive");
            if (Directory.Exists(gdrivePath))
            {
                var myDrive = Path.Combine(gdrivePath, "My Drive");
                if (Directory.Exists(myDrive)) return myDrive;
                return gdrivePath;
            }
        }
        catch { }

        return null;
    }

    private static string? DetectDropboxFolderCore()
    {
        try
        {
            // 1. Check Dropbox info.json in AppData / LocalAppData
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] infoPaths =
            [
                Path.Combine(appData, "Dropbox", "info.json"),
                Path.Combine(localAppData, "Dropbox", "info.json")
            ];

            foreach (var infoPath in infoPaths)
            {
                if (File.Exists(infoPath))
                {
                    try
                    {
                        var json = File.ReadAllText(infoPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("personal", out var personal) &&
                            personal.TryGetProperty("path", out var pPath))
                        {
                            var path = pPath.GetString();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                return path;
                        }

                        if (doc.RootElement.TryGetProperty("business", out var business) &&
                            business.TryGetProperty("path", out var bPath))
                        {
                            var path = bPath.GetString();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                return path;
                        }
                    }
                    catch { }
                }
            }

            // 2. Fallback to standard UserProfile\Dropbox
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dropboxUserPath = Path.Combine(userProfile, "Dropbox");
            if (Directory.Exists(dropboxUserPath)) return dropboxUserPath;
        }
        catch { }

        return null;
    }

    private static string? DetectOneDriveFolderCore()
    {
        try
        {
            var envOneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(envOneDrive) && Directory.Exists(envOneDrive))
                return envOneDrive;

            var envConsumer = Environment.GetEnvironmentVariable("OneDriveConsumer");
            if (!string.IsNullOrEmpty(envConsumer) && Directory.Exists(envConsumer))
                return envConsumer;

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var oneDriveUserPath = Path.Combine(userProfile, "OneDrive");
            if (Directory.Exists(oneDriveUserPath)) return oneDriveUserPath;
        }
        catch { }

        return null;
    }

    public static (bool ready, string summary, string? pathOrTarget) GetProviderStatus(string providerKey, AppSettings s)
    {
        switch (providerKey?.ToLowerInvariant())
        {
            case "gdrive":
            {
                var configured = s.GDriveFolderPath?.Trim();
                if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
                    return (true, $"Ready: {configured}", configured);

                var detected = DetectGoogleDriveFolder();
                if (detected != null)
                    return (true, $"Auto-detected: {detected}", detected);

                return (false, "Google Drive desktop client not found", null);
            }

            case "dropbox":
            {
                if (!string.IsNullOrWhiteSpace(s.DropboxApiToken))
                    return (true, "Direct API Connected (Token set)", null);

                var configured = s.DropboxFolderPath?.Trim();
                if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
                    return (true, $"Ready: {configured}", configured);

                var detected = DetectDropboxFolder();
                if (detected != null)
                    return (true, $"Auto-detected: {detected}", detected);

                return (false, "API Token or Dropbox desktop required", null);
            }

            case "onedrive":
            {
                var configured = s.OneDriveFolderPath?.Trim();
                if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
                    return (true, $"Ready: {configured}", configured);

                var detected = DetectOneDriveFolder();
                if (detected != null)
                    return (true, $"Auto-detected: {detected}", detected);

                return (false, "OneDrive not found", null);
            }

            case "webhook":
            {
                var url = s.WebhookUrl?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                    return (false, "Webhook URL required", null);

                var isDiscord = url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase);
                return (true, isDiscord ? "Discord Webhook Ready" : "Custom Webhook Ready", url);
            }

            case "custom":
            {
                var custom = s.CustomCloudFolderPath?.Trim();
                if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom))
                    return (true, $"Folder: {custom}", custom);

                return (false, "Select a destination folder", null);
            }

            default:
                return (false, "Unknown provider", null);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  UPLOAD PIPELINE
    // ═════════════════════════════════════════════════════════════════════════

    public static async Task<CloudUploadResult> UploadAsync(
        string localFilePath,
        string providerKey,
        AppSettings settings,
        Action<double, string>? progressCb = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(localFilePath))
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = $"Local file does not exist: {localFilePath}"
            };
        }

        var fileName = Path.GetFileName(localFilePath);
        providerKey = providerKey?.ToLowerInvariant() ?? "gdrive";

        try
        {
            switch (providerKey)
            {
                case "gdrive":
                    return await UploadToGoogleDriveAsync(localFilePath, fileName, settings, progressCb, ct);

                case "dropbox":
                    return await UploadToDropboxAsync(localFilePath, fileName, settings, progressCb, ct);

                case "onedrive":
                    return await UploadToOneDriveAsync(localFilePath, fileName, settings, progressCb, ct);

                case "webhook":
                    return await UploadToWebhookAsync(localFilePath, fileName, settings, progressCb, ct);

                case "custom":
                    return await UploadToCustomFolderAsync(localFilePath, fileName, settings, progressCb, ct);

                default:
                    return new CloudUploadResult
                    {
                        Success = false,
                        ErrorMessage = $"Unsupported cloud provider: {providerKey}"
                    };
            }
        }
        catch (OperationCanceledException)
        {
            return new CloudUploadResult { Success = false, ErrorMessage = "Upload cancelled by user" };
        }
        catch (Exception ex)
        {
            return new CloudUploadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── Google Drive ─────────────────────────────────────────────────────────
    private static async Task<CloudUploadResult> UploadToGoogleDriveAsync(
        string localFilePath, string fileName, AppSettings settings,
        Action<double, string>? progressCb, CancellationToken ct)
    {
        var targetRoot = !string.IsNullOrWhiteSpace(settings.GDriveFolderPath) && Directory.Exists(settings.GDriveFolderPath)
            ? settings.GDriveFolderPath.Trim()
            : DetectGoogleDriveFolder();

        if (string.IsNullOrEmpty(targetRoot) || !Directory.Exists(targetRoot))
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = "Google Drive folder not found. Please verify Google Drive for Desktop is running or configure a folder in Setup."
            };
        }

        var destinationDir = Path.Combine(targetRoot, "VideoConverter");
        Directory.CreateDirectory(destinationDir);

        var destinationFile = GetUniqueTargetFilePath(destinationDir, fileName);
        progressCb?.Invoke(5, "Uploading to Google Drive...");

        await CopyFileWithProgressAsync(localFilePath, destinationFile, pct =>
        {
            progressCb?.Invoke(pct, $"Uploading to Google Drive... {pct:F0}%");
        }, ct);

        return new CloudUploadResult
        {
            Success = true,
            DestinationPath = destinationFile,
            Message = $"Uploaded to Google Drive ({Path.GetFileName(destinationFile)})"
        };
    }

    // ── Dropbox ──────────────────────────────────────────────────────────────
    private static async Task<CloudUploadResult> UploadToDropboxAsync(
        string localFilePath, string fileName, AppSettings settings,
        Action<double, string>? progressCb, CancellationToken ct)
    {
        // 1. Direct API upload if token is available
        if (!string.IsNullOrWhiteSpace(settings.DropboxApiToken))
        {
            progressCb?.Invoke(5, "Connecting to Dropbox API...");
            var token = settings.DropboxApiToken.Trim();

            var remotePath = $"/VideoConverter/{fileName}";
            var uploadArg = JsonSerializer.Serialize(new
            {
                path = remotePath,
                mode = "add",
                autorename = true,
                mute = false
            });

            using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var content = new ProgressableStreamContent(fileStream, 64 * 1024, pct =>
            {
                progressCb?.Invoke(pct, $"Uploading to Dropbox... {pct:F0}%");
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload")
            {
                Content = content
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Dropbox-API-Arg", uploadArg);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return new CloudUploadResult
                {
                    Success = false,
                    ErrorMessage = $"Dropbox API Error ({resp.StatusCode}): {respBody}"
                };
            }

            // Try to create a shareable link
            string? shareUrl = null;
            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("path_display", out var displayPath))
                {
                    remotePath = displayPath.GetString() ?? remotePath;
                }

                using var shareReq = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { path = remotePath }), Encoding.UTF8, "application/json")
                };
                shareReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var shareResp = await _httpClient.SendAsync(shareReq, ct);
                if (shareResp.IsSuccessStatusCode)
                {
                    var shareBody = await shareResp.Content.ReadAsStringAsync(ct);
                    using var shareDoc = JsonDocument.Parse(shareBody);
                    if (shareDoc.RootElement.TryGetProperty("url", out var urlProp))
                        shareUrl = urlProp.GetString();
                }
            }
            catch { }

            return new CloudUploadResult
            {
                Success = true,
                DestinationPath = remotePath,
                Url = shareUrl,
                Message = $"Uploaded to Dropbox: {remotePath}"
            };
        }

        // 2. Fallback to desktop sync folder
        var targetRoot = !string.IsNullOrWhiteSpace(settings.DropboxFolderPath) && Directory.Exists(settings.DropboxFolderPath)
            ? settings.DropboxFolderPath.Trim()
            : DetectDropboxFolder();

        if (!string.IsNullOrEmpty(targetRoot) && Directory.Exists(targetRoot))
        {
            var destinationDir = Path.Combine(targetRoot, "VideoConverter");
            Directory.CreateDirectory(destinationDir);

            var destinationFile = GetUniqueTargetFilePath(destinationDir, fileName);
            progressCb?.Invoke(5, "Syncing to Dropbox folder...");

            await CopyFileWithProgressAsync(localFilePath, destinationFile, pct =>
            {
                progressCb?.Invoke(pct, $"Syncing to Dropbox... {pct:F0}%");
            }, ct);

            return new CloudUploadResult
            {
                Success = true,
                DestinationPath = destinationFile,
                Message = $"Synced to Dropbox ({Path.GetFileName(destinationFile)})"
            };
        }

        return new CloudUploadResult
        {
            Success = false,
            ErrorMessage = "Dropbox is not configured. Please enter a Dropbox API token or install the Dropbox desktop app."
        };
    }

    // ── OneDrive ─────────────────────────────────────────────────────────────
    private static async Task<CloudUploadResult> UploadToOneDriveAsync(
        string localFilePath, string fileName, AppSettings settings,
        Action<double, string>? progressCb, CancellationToken ct)
    {
        var targetRoot = !string.IsNullOrWhiteSpace(settings.OneDriveFolderPath) && Directory.Exists(settings.OneDriveFolderPath)
            ? settings.OneDriveFolderPath.Trim()
            : DetectOneDriveFolder();

        if (string.IsNullOrEmpty(targetRoot) || !Directory.Exists(targetRoot))
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = "Microsoft OneDrive folder not found. Please ensure OneDrive is running or configure a folder in Setup."
            };
        }

        var destinationDir = Path.Combine(targetRoot, "VideoConverter");
        Directory.CreateDirectory(destinationDir);

        var destinationFile = GetUniqueTargetFilePath(destinationDir, fileName);
        progressCb?.Invoke(5, "Uploading to OneDrive...");

        await CopyFileWithProgressAsync(localFilePath, destinationFile, pct =>
        {
            progressCb?.Invoke(pct, $"Uploading to OneDrive... {pct:F0}%");
        }, ct);

        return new CloudUploadResult
        {
            Success = true,
            DestinationPath = destinationFile,
            Message = $"Uploaded to OneDrive ({Path.GetFileName(destinationFile)})"
        };
    }

    // ── Discord / Custom Webhook ─────────────────────────────────────────────
    private static async Task<CloudUploadResult> UploadToWebhookAsync(
        string localFilePath, string fileName, AppSettings settings,
        Action<double, string>? progressCb, CancellationToken ct)
    {
        var webhookUrl = settings.WebhookUrl?.Trim();
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = "Webhook URL is empty. Please enter a valid Discord or custom Webhook URL in Cloud Setup."
            };
        }

        progressCb?.Invoke(5, "Preparing Webhook upload...");

        using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var streamContent = new ProgressableStreamContent(fileStream, 64 * 1024, pct =>
        {
            progressCb?.Invoke(pct, $"Sending to Webhook... {pct:F0}%");
        });
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        using var form = new MultipartFormDataContent();

        bool isDiscord = webhookUrl.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase);
        if (isDiscord)
        {
            var payload = JsonSerializer.Serialize(new
            {
                content = $"🎬 Uploaded with **VideoConverter**: `{fileName}`"
            });
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            form.Add(streamContent, "files[0]", fileName);
        }
        else
        {
            form.Add(streamContent, "file", fileName);
        }

        using var resp = await _httpClient.PostAsync(webhookUrl, form, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = $"Webhook error ({resp.StatusCode}): {respBody}"
            };
        }

        string? attachmentUrl = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(respBody))
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("attachments", out var attachments) &&
                    attachments.GetArrayLength() > 0)
                {
                    attachmentUrl = attachments[0].GetProperty("url").GetString();
                }
            }
        }
        catch { }

        return new CloudUploadResult
        {
            Success = true,
            Url = attachmentUrl ?? webhookUrl,
            Message = "Successfully sent to Webhook!"
        };
    }

    // ── Custom Cloud Folder ──────────────────────────────────────────────────
    private static async Task<CloudUploadResult> UploadToCustomFolderAsync(
        string localFilePath, string fileName, AppSettings settings,
        Action<double, string>? progressCb, CancellationToken ct)
    {
        var targetRoot = settings.CustomCloudFolderPath?.Trim();
        if (string.IsNullOrEmpty(targetRoot) || !Directory.Exists(targetRoot))
        {
            return new CloudUploadResult
            {
                Success = false,
                ErrorMessage = "Custom cloud folder does not exist or is not configured."
            };
        }

        var destinationFile = GetUniqueTargetFilePath(targetRoot, fileName);
        progressCb?.Invoke(5, "Copying to custom cloud folder...");

        await CopyFileWithProgressAsync(localFilePath, destinationFile, pct =>
        {
            progressCb?.Invoke(pct, $"Copying to cloud folder... {pct:F0}%");
        }, ct);

        return new CloudUploadResult
        {
            Success = true,
            DestinationPath = destinationFile,
            Message = $"Saved to cloud folder ({Path.GetFileName(destinationFile)})"
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private static string GetUniqueTargetFilePath(string directory, string originalFileName)
    {
        var target = Path.Combine(directory, originalFileName);
        if (!File.Exists(target)) return target;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);

        int counter = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(directory, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        }

        return target;
    }

    public static async Task CopyFileWithProgressAsync(
        string sourcePath, string targetPath,
        Action<double>? progress, CancellationToken ct)
    {
        long totalBytes = new FileInfo(sourcePath).Length;
        var report = ThrottleProgress(progress);

        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            using var dest = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

            var buffer = new byte[1024 * 1024];
            long copiedBytes = 0;
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                copiedBytes += bytesRead;
                if (totalBytes > 0)
                    report?.Invoke(Math.Min(100.0, (double)copiedBytes / totalBytes * 100.0));
            }
        }
        catch
        {
            // Never leave a half-written file behind in the user's cloud folder.
            try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Wraps a progress callback so it only fires when the whole-number percentage changes.
    /// Every callback becomes a UI/status update, and per-buffer reporting floods the dispatcher.
    /// </summary>
    internal static Action<double>? ThrottleProgress(Action<double>? progress)
    {
        if (progress == null) return null;
        int last = -1;
        return pct =>
        {
            int whole = (int)pct;
            if (whole == last) return;
            last = whole;
            progress(pct);
        };
    }
}

/// <summary>
/// Custom HttpContent wrapper that reports real-time upload progress.
/// </summary>
public sealed class ProgressableStreamContent : HttpContent
{
    private readonly Stream _content;
    private readonly int _bufferSize;
    private readonly Action<double>? _progress;
    private readonly long _totalBytes;

    public ProgressableStreamContent(Stream content, int bufferSize, Action<double>? progress)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _bufferSize = bufferSize > 0 ? bufferSize : 64 * 1024;
        _progress = CloudStorageService.ThrottleProgress(progress);
        _totalBytes = content.Length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[_bufferSize];
        long totalUploaded = 0;
        int bytesRead;

        while ((bytesRead = await _content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalUploaded += bytesRead;
            if (_totalBytes > 0)
                _progress?.Invoke(Math.Min(100.0, (double)totalUploaded / _totalBytes * 100.0));
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _totalBytes;
        return true;
    }
}
