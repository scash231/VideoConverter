using System.Text.Json.Serialization;

namespace VideoConverterUI.Models;

public sealed class AppSettings
{
    // General
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("auto_paste_on_focus")]
    public bool AutoPasteOnFocus { get; set; } = false;

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; } = false;

    [JsonPropertyName("auto_fetch_metadata")]
    public bool AutoFetchMetadata { get; set; } = false;

    /// <summary>Open the error window as soon as something fails. Off: a toast and the header's error pill lead to it.</summary>
    [JsonPropertyName("auto_open_error_window")]
    public bool AutoOpenErrorWindow { get; set; } = false;

    [JsonPropertyName("completion_notification_mode")]
    public string CompletionNotificationMode { get; set; } = "notification_only";

    /// <summary>The "Playlist detected" banner that offers to open a playlist link in the Multi Downloader.</summary>
    [JsonPropertyName("playlist_detected_notification")]
    public bool PlaylistDetectedNotification { get; set; } = true;

    // Software Update
    /// <summary>Look for a new release at startup and every few hours, and say so when there is one.</summary>
    [JsonPropertyName("auto_check_updates")]
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>When the last update check succeeded (UTC), shown as "Last checked".</summary>
    [JsonPropertyName("last_update_check")]
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>The release the "update available" banner was last shown for, so it appears once per release.</summary>
    [JsonPropertyName("notified_update_version")]
    public string? NotifiedUpdateVersion { get; set; }

    [JsonIgnore]
    public bool NotifyOnComplete => CompletionNotificationMode != "off";

    [JsonIgnore]
    public bool EnableNotificationSound => CompletionNotificationMode == "sound_and_notification";

    // Notification display duration in seconds (0 = stay until dismissed)
    [JsonPropertyName("notification_duration_info")]
    public int NotificationDurationInfo { get; set; } = 3;

    [JsonPropertyName("notification_duration_error")]
    public int NotificationDurationError { get; set; } = 4;

    [JsonPropertyName("notification_duration_complete")]
    public int NotificationDurationComplete { get; set; } = 2;

    // Upload defaults
    [JsonPropertyName("default_privacy")]
    public string DefaultPrivacy { get; set; } = "unlisted";

    [JsonPropertyName("auto_add_shorts_hashtag")]
    public bool AutoAddShortsHashtag { get; set; } = true;

    [JsonPropertyName("default_fingerprint_enabled")]
    public bool DefaultFingerprintEnabled { get; set; } = true;

    [JsonPropertyName("default_fingerprint_method")]
    public string DefaultFingerprintMethod { get; set; } = "standard";

    // Download defaults
    [JsonPropertyName("default_output_dir")]
    public string DefaultOutputDir { get; set; } = "";

    [JsonPropertyName("default_video_quality")]
    public string DefaultVideoQuality { get; set; } = "best";

    [JsonPropertyName("default_fingerprint_dl_enabled")]
    public bool DefaultFingerprintDlEnabled { get; set; } = false;

    [JsonPropertyName("default_fingerprint_dl_method")]
    public string DefaultFingerprintDlMethod { get; set; } = "metadata";

    [JsonPropertyName("cookies_file_path")]
    public string CookiesFilePath { get; set; } = "";

    // Batch Queue Settings
    [JsonPropertyName("batch_max_concurrency")]
    public int BatchMaxConcurrency { get; set; } = 5;

    [JsonPropertyName("batch_auto_retry")]
    public bool BatchAutoRetry { get; set; } = true;

    [JsonPropertyName("batch_auto_remove_done")]
    public bool BatchAutoRemoveDone { get; set; } = false;

    [JsonPropertyName("batch_quality")]
    public string BatchQuality { get; set; } = "best";

    [JsonPropertyName("prompt_save_batch_on_exit")]
    public bool PromptSaveBatchOnExit { get; set; } = true;

    // Cloud Upload Settings
    [JsonPropertyName("cloud_upload_enabled")]
    public bool CloudUploadEnabled { get; set; } = false;

    [JsonPropertyName("cloud_provider")]
    public string CloudProvider { get; set; } = "gdrive"; // "gdrive" | "dropbox" | "onedrive" | "webhook" | "custom"

    [JsonPropertyName("cloud_temp_download")]
    public bool CloudTempDownload { get; set; } = true;

    [JsonPropertyName("cloud_keep_local_copy")]
    public bool CloudKeepLocalCopy { get; set; } = false;

    [JsonPropertyName("gdrive_folder_path")]
    public string GDriveFolderPath { get; set; } = "";

    [JsonPropertyName("dropbox_folder_path")]
    public string DropboxFolderPath { get; set; } = "";

    [JsonPropertyName("dropbox_api_token")]
    public string DropboxApiToken { get; set; } = "";

    [JsonPropertyName("onedrive_folder_path")]
    public string OneDriveFolderPath { get; set; } = "";

    [JsonPropertyName("webhook_url")]
    public string WebhookUrl { get; set; } = "";

    [JsonPropertyName("custom_cloud_folder_path")]
    public string CustomCloudFolderPath { get; set; } = "";

    // Performance & Execution Engine
    [JsonPropertyName("execution_engine")]
    public string ExecutionEngine { get; set; } = "ferrite"; // "native" | "ferrite" | "python"

    [JsonPropertyName("use_gpu")]
    public bool UseGpu { get; set; } = true;

    [JsonPropertyName("max_concurrent_jobs")]
    public int MaxConcurrentJobs { get; set; } = 4;

    // Backend
    [JsonPropertyName("backend_url")]
    public string BackendUrl { get; set; } = "http://127.0.0.1:8765";

    [JsonPropertyName("backend_timeout_seconds")]
    public int BackendTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("auto_restart_backend")]
    public bool AutoRestartBackend { get; set; } = true;

    [JsonPropertyName("backend_log_level")]
    public string BackendLogLevel { get; set; } = "info";

    // Developer
    [JsonPropertyName("enable_developer_mode")]
    public bool EnableDeveloperMode { get; set; } = true;

    /// <summary>When Liquid Glass checks whether the text on it needs dimming (Settings → Developer):
    /// "timer" (every 0.4 s), "changes" (after scrolling, resizing or moving) or "both".</summary>
    [JsonPropertyName("glass_sampling_mode")]
    public string GlassSamplingMode { get; set; } = "timer";

    [JsonPropertyName("dev_console_enabled")]
    public bool DevConsoleEnabled { get; set; } = true;

    [JsonPropertyName("auto_open_console_on_error")]
    public bool AutoOpenConsoleOnError { get; set; } = false;

    [JsonPropertyName("verbose_logging")]
    public bool VerboseLogging { get; set; } = true;

    [JsonPropertyName("bypass_file_restrictions")]
    public bool BypassFileRestrictions { get; set; } = true;

    [JsonPropertyName("show_performance_overlay")]
    public bool ShowPerformanceOverlay { get; set; } = false;

    [JsonPropertyName("enable_performance_mode")]
    public bool EnablePerformanceMode { get; set; } = false;

    [JsonPropertyName("auto_progress_mode")]
    public string AutoProgressMode { get; set; } = "disabled";

    [JsonIgnore]
    public bool AutoShowProgressWindow
    {
        get => AutoProgressMode != "disabled";
        set
        {
            if (!value)
                AutoProgressMode = "disabled";
            else if (AutoProgressMode == "disabled")
                AutoProgressMode = "show_progress";
        }
    }

    [JsonPropertyName("hide_scrollbars")]
    public bool HideScrollbars { get; set; } = false;

    [JsonPropertyName("smooth_scrolling")]
    public bool SmoothScrolling { get; set; } = true;

    [JsonPropertyName("show_icon_labels")]
    public bool ShowIconLabels { get; set; } = true;

    [JsonPropertyName("show_help_pill")]
    public bool ShowHelpPill { get; set; } = true;

    [JsonPropertyName("show_dev_pill")]
    public bool ShowDevPill { get; set; } = true;

    [JsonPropertyName("show_editor_pill")]
    public bool ShowEditorPill { get; set; } = true;

    [JsonPropertyName("show_lang_pill")]
    public bool ShowLangPill { get; set; } = false;

    [JsonPropertyName("show_per_video_quality")]
    public bool ShowPerVideoQuality { get; set; } = true;

    [JsonPropertyName("custom_palette_colors")]
    public List<string> CustomPaletteColors { get; set; } = [];

    // Console Logging Filters
    [JsonPropertyName("console_show_system")]
    public bool ConsoleShowSystem { get; set; } = true;

    [JsonPropertyName("console_show_backend")]
    public bool ConsoleShowBackend { get; set; } = true;

    [JsonPropertyName("console_show_ffmpeg")]
    public bool ConsoleShowFFmpeg { get; set; } = true;

    [JsonPropertyName("compact_mode")]
    public bool CompactMode { get; set; } = false;

    [JsonPropertyName("show_window_resizer_grip")]
    public bool ShowWindowResizerGrip { get; set; } = true;

    [JsonPropertyName("resizer_grip_only_on_hover")]
    public bool ResizerGripOnlyOnHover { get; set; } = false;

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
}
