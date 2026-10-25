using System.Runtime.InteropServices;

namespace VideoConverterUI.Services;

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

public sealed record ChangelogRelease(string Version, string Date, IReadOnlyList<ChangelogSection> Sections)
{
    public string Header => $"{Version}  ·  {Date}";
}

/// <summary>A big version (27H1, 26H2, …) and its releases, newest first. <see cref="Channel"/> is shown under the name.</summary>
public sealed record ChangelogVersion(string Name, string Channel, IReadOnlyList<ChangelogRelease> Releases)
{
    public string LatestDate => Releases.Count > 0 ? Releases[0].Date : "";
    public string FirstDate => Releases.Count > 0 ? Releases[^1].Date : "";
}

public static class BuildInfoService
{
    public static string BuildName => "27H1 Canary";

    public static string BuildVersion => "27H1.26.2";

    /// <summary>Source hash this build was stamped with.</summary>
    public static string CommitSha => "108cf6ad03b3fb6052047d908d55f310a8f60dbd";

    public static string ShortSha => CommitSha.Length >= 8 ? CommitSha[..8] : CommitSha;

    public static string BuildNumber => $"{BuildVersion} ({ShortSha})";

    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public static string ExecutionEngine => EngineProvider.ActiveKey switch
    {
        EngineProvider.ClassicKey => "Legacy Python Backend",
        EngineProvider.FerriteKey => "Ferrite Rust Engine",
        _ => "Native C# Engine",
    };

    /// <summary>"27H1.20 (sha) · Ferrite": the build number with the active backend.</summary>
    public static string BuildNumberWithEngine => $"{BuildNumber} · {EngineProvider.DisplayName(EngineProvider.ActiveKey)}";

    public static string MediaPipeline => "FFmpeg & yt-dlp Core";

    /// <summary>Release notes shown under "What's New" in Settings → About: big versions, each with its releases (newest first).</summary>
    public static IReadOnlyList<ChangelogVersion> Versions { get; } =
    [
        new("27H1", "Canary",
        [
            new("27H1.26.2", "2026-10-25",
            [
                new("Performance",
                [
                    "Menus, pop-ups, dialogs and notifications no longer draw the window behind them again on every frame: their glass keeps a picture of it and only redraws it when something back there changes. They open more smoothly, and a notification's countdown line costs a fraction of what it did.",
                    "When a new notification arrives, the ones below slide down inside their own windows instead of moving the windows, which made the whole app stutter for a moment.",
                ]),
            ]),
            new("27H1.26.1", "2026-10-25",
            [
                new("New",
                [
                    "The main window is Liquid Glass throughout: header pills, the link field, the Upload / Download switch, the cards and Start. Header pills come and go like drops.",
                    "The preview card reflects the thumbnail: its glass shows a soft, drawn-out mirror image right below it, and the glass around it (the switch above, the settings card beside it) picks up its colours at the edges.",
                    "Settings → Notifications: a switch for the \"Playlist detected\" banner.",
                ]),
                new("Changed",
                [
                    "The Upload / Download switch's thumb takes on the window's tint.",
                    "The main window waits until it can draw smoothly before it opens, instead of starting frozen and then jumping.",
                    "Liquid Glass edges bend what lies beyond them smoothly to the very edge, instead of ending in streaks and a hard line.",
                ]),
                new("Fixed",
                [
                    "The thumbnail fills the preview card's corners.",
                    "The quick actions under the preview (Title, URL, Browser, Save) are readable again.",
                ]),
            ]),
            new("27H1.26", "2026-10-23",
            [
                new("New",
                [
                    "Liquid, a new motion style (Settings → Animations → Motion): everything moves on real springs that keep their momentum when you change your mind, so nothing jumps or stops dead. New installs start with it.",
                    "With Liquid, windows grow out of the button that opened them, spreading wide first and then to full height, and pour back into it when they close (Customize → Opening → Liquid).",
                    "The Upload / Download switch stretches and turns clear while it slides; flick it and it carries on with your speed.",
                    "Notifications bloom in like the Dynamic Island and fly back up into a pill; the banners below glide out of the way.",
                    "Drop-down lists animate closed when you pick something, click elsewhere or press Esc, instead of vanishing.",
                ]),
                new("Changed",
                [
                    "Help and the other header bubbles, and drop-down lists, keep their bubble opening with Liquid too.",
                ]),
                new("Fixed",
                [
                    "Switching quickly between Upload and Download no longer leaves both panels hidden.",
                    "Opening a window with animations turned off no longer throws an error.",
                ]),
            ]),
            new("27H1.25.6", "2026-10-17",
            [
                new("Fixed",
                [
                    "Video Editor: videos Windows can't play itself (such as VP9 WebM) no longer leave the preview blank. The editor makes a copy for the preview and shows how far along it is; export still uses the original file.",
                    "Video Editor: a video that can't be opened at all says so and shows the Open Video button again.",
                ]),
            ]),
            new("27H1.25.5", "2026-10-15",
            [
                new("Performance",
                [
                    "Liquid Glass no longer keeps the app redrawing 60 times a second: it follows what lies behind it only while something moves, so open menus, dialogs and Settings cost next to nothing at rest.",
                    "Auto Dim checks less often while nothing behind the glass changes, pauses for minimized windows and slows down while the app is in the background.",
                    "Auto Dim's contrast maths is much cheaper, and small glass surfaces blur only the area they need.",
                ]),
            ]),
            new("27H1.25.4", "2026-10-14",
            [
                new("New",
                [
                    "Auto Dim: \"Only right behind text\" darkens just the spots where text sits and leaves the rest of the glass clear.",
                ]),
                new("Changed",
                [
                    "Text readability is now called Auto Dim and is marked as beta.",
                ]),
            ]),
            new("27H1.25.3", "2026-10-14",
            [
                new("New",
                [
                    "Settings → Appearance → Liquid Glass → Text readability: a page of its own with a live preview, the on/off switch, a strength slider (softer, standard, stronger) and how quickly the glass adapts.",
                    "Stronger also darkens spots that the standard setting leaves clear; softer darkens less.",
                ]),
                new("Changed",
                [
                    "\"Glass readability check\" moved from Developer to the Text readability page, in plainer words: Regularly, When something moves, or Both.",
                ]),
            ]),
            new("27H1.25.2", "2026-10-14",
            [
                new("New",
                [
                    "Settings → Developer: \"Glass readability check\" picks when Liquid Glass checks whether text on it needs dimming: on a timer, after changes (scrolling, resizing, moving), or both.",
                ]),
            ]),
            new("27H1.25.1", "2026-10-13",
            [
                new("New",
                [
                    "Settings → Appearance → Liquid Glass: \"Dim behind text automatically\" turns the glass's automatic dimming on or off.",
                ]),
                new("Changed",
                [
                    "The Liquid Glass page is shorter: the explanation under the Clear ↔ Tinted slider is gone.",
                ]),
            ]),
            new("27H1.25", "2026-10-13",
            [
                new("New",
                [
                    "Multi Downloader: \"Add links\" sits in the title bar as a glass pill. Click it and it slides open into the link field; while the links load it shrinks to a loading capsule, then folds away.",
                    "Right-click menus are Liquid Glass, also the Cut / Copy / Paste menu of every text box.",
                ]),
                new("Changed",
                [
                    "Multi Downloader: the title-bar buttons are ordered Add links, Options, Save & Exit, and their labels follow \"Show icon labels\". The capsules next to the Fetch button catch its blue.",
                ]),
                new("Fixed",
                [
                    "Text on Liquid Glass stays readable over busy or bright backgrounds: the glass now dims just the spots that would wash the text out.",
                ]),
            ]),
            new("27H1.24.1", "2026-10-12",
            [
                new("Fixed",
                [
                    "Settings: the shade behind the page title fades in or out after switching pages, instead of cutting in.",
                ]),
            ]),
            new("27H1.24", "2026-10-12",
            [
                new("New",
                [
                    "What's New is sorted by version: 27H1, 26H2, v3, v2 and v1, each with its releases, back to the first console app.",
                    "Multi Downloader: one circle selects all or none, the footer shows the total length and roughly how much the downloads take at the chosen quality, and the storage bin sits next to Cloud.",
                ]),
                new("Changed",
                [
                    "Multi Downloader: tidier toolbar, smaller cards with a soft shadow, and the overall progress bar only shows while downloading.",
                    "New installs start with the Smooth Liquid animations.",
                ]),
                new("Fixed",
                [
                    "The Multi Downloader no longer lags: progress bars ran an endless animation that kept the window redrawing, even when idle.",
                    "Scrolling a long queue is smoother.",
                ]),
            ]),
            new("27H1.23", "2026-10-12",
            [
                new("New",
                [
                    "Video Editor: after resizing, the window snaps to the video's shape, so there are no black bars.",
                    "Settings: the page title gets a soft shade behind it while you scroll, so it stays readable.",
                    "Windows are fully opaque by default (Glass & Layout → Window glass).",
                ]),
                new("Changed",
                [
                    "Removed the blur behind windows: it didn't work reliably on Windows 11.",
                ]),
                new("Fixed",
                [
                    "The Video Editor and Log Viewer borders are visible all the way around again.",
                ]),
            ]),
            new("27H1.22", "2026-10-12",
            [
                new("New",
                [
                    "Video Editor redesign in the Liquid Glass style of Settings: the inspector and the timeline form one glass platform around the preview.",
                    "Play, stop and mute sit under the timeline, so nothing covers the video. \"Remove audio\" moved to the Audio tab. Space plays or pauses, Esc closes.",
                    "The editor window takes the shape of the video, so 16:9 and vertical videos show without black bars.",
                    "Multi Downloader: the link bar, toolbar, footer and title-bar buttons are Liquid Glass.",
                    "Glass stays readable on bright backgrounds: a soft shade fades in under the text, and text on glass has a slight shadow.",
                ]),
                new("Fixed",
                [
                    "The first Liquid Glass window no longer hangs for a second while opening: the effect is prepared at startup and kept for later launches.",
                ]),
            ]),
            new("27H1.21", "2026-10-11",
            [
                new("New",
                [
                    "New installer: VideoConverterSetup.exe from scash.uk/vc sets up everything the app needs (.NET, FFmpeg, yt-dlp, Ferrite). Run it again to update.",
                    "The Legacy (Python) engine is no longer supported or installed; it stays available as a manual download.",
                ]),
                new("Fixed",
                [
                    "\"Ferrite engine not found\" after installing: the app looked for ferrite.exe in a temporary folder.",
                ]),
            ]),
            new("27H1.20", "2026-10-11",
            [
                new("New",
                [
                    "Liquid Glass: notifications and menus are made of glass like Apple's, frosted in the middle and bending the content behind them at the edges.",
                    "All drop-down menus open as Liquid Glass, everywhere in the app.",
                    "Pop-ups and dialogs are Liquid Glass too: the Tools menu, help, error details, language picker, Queue Options, Cloud Upload, the FFmpeg installer, \"Save batch?\" and the confirmation sheets.",
                    "Settings → Appearance → Liquid Glass: choose from Clear to Tinted, with a live preview. Sub-pages in Settings have a back button.",
                    "Glass Tuner (developer tool): adjust the glass live per surface and theme mode, then save the values or copy them as code. Open it in Settings → Developer or with \"debug glass\".",
                    "Without hardware graphics acceleration, the glass falls back to classic frosted glass.",
                ]),
            ]),
            new("27H1.19", "2026-10-10",
            [
                new("New",
                [
                    "Ferrite: a new media engine written in Rust. Same results as Native, but it runs as its own process, so a crash can't take the window down. Choose it in Settings → Advanced → Engine.",
                    "Ferrite can upload to YouTube, using the same client_secret.json and token.json as the Legacy engine.",
                    "Three engines: Native (download and cloud upload), Ferrite (everything, in its own process) and Legacy (download and YouTube upload). The help bubble explains each one.",
                    "Settings → Info shows the active engine next to the build number.",
                    "New error window: what went wrong, why, the steps to fix it and the real error message, plus a button for the most likely fix.",
                    "When a job or the engine fails, a notification names the problem; click it (or the red header pill) for details, or let the error window open automatically (Settings → General). Errors are understood in English, German and Russian.",
                    "Multi Downloader: failed items show what went wrong and the first thing to try; hover for all steps.",
                    "Slim scroll bars that only appear while you scroll or hover over a list.",
                ]),
                new("Fixed",
                [
                    "Switching the engine during a download or upload no longer cancels it; the switch waits until it is done.",
                    "The Multi Downloader keeps working after the engine was switched.",
                    "Legacy engine: cloud upload jobs fail with a clear message instead of silently not uploading.",
                    "Native engine: the upload message points to Ferrite or Legacy.",
                    "\"Error loading metadata\" keeps the actual reason, and an empty result shows the error window.",
                    "Errors are judged by the line where yt-dlp actually stopped, not by earlier warnings (an age-restricted video no longer shows an upload error).",
                ]),
            ]),
            new("27H1.18", "2026-10-08",
            [
                new("New",
                [
                    "Smooth scrolling: the mouse wheel glides instead of jumping (Theme Designer → Layout).",
                    "Multi Downloader: middle-click a list to autoscroll like in a browser.",
                    "Expandable sections in Settings and the Theme Designer share one design and slide open smoothly.",
                    "The Theme Designer uses the same glass toggle switches as Settings.",
                    "Download mode: the buttons shown after a download fill their card and shrink smoothly when the cloud options open.",
                ]),
                new("Fixed",
                [
                    "Jobs no longer freeze when FFmpeg or yt-dlp print a lot of output.",
                    "GPU acceleration tests the encoder first (NVENC → QSV → AMF) and falls back to the CPU, so it works on AMD and Intel graphics.",
                    "Stopping a Multi Downloader batch really stops the running downloads.",
                    "Closing an old progress window no longer cancels a newer job.",
                    "Subtitle (.srt) files no longer contain log lines.",
                    "Saving Settings keeps the Theme Designer's custom colours, and a cancelled language change is reverted.",
                    "The \"Disabled (Static)\" preset and Performance Mode really turn animations off.",
                    "The drop zone's Cancel button closes it again.",
                    "Switching Settings tabs while scrolled down no longer jumps.",
                    "Fixed a crash when resizing with a monitor left of or above the main display.",
                    "Fixed a crash in the developer console when restarting the engine.",
                    "Legacy engine: \"&list=\" links are no longer treated as YouTube Music, M4A/FLAC work and queued jobs can be cancelled.",
                ]),
                new("Performance",
                [
                    "The media engine starts in the background instead of blocking the window.",
                    "Theme Designer: colour sliders no longer save on every step, and theme changes no longer rebuild every window.",
                    "Logs are written in batches and stay size-limited, so they don't slow down over time.",
                    "Cloud uploads report progress per percent; cloud folder detection only runs when needed and is cached.",
                    "Multi Downloader: single links skip the playlist check, and metadata loads three links at a time.",
                    "The performance overlay shows real CPU usage and UI latency.",
                ]),
                new("Cleanup",
                [
                    "Removed about 1,200 lines of unused code, styles and translation strings.",
                ]),
            ]),
            new("27H1.17", "2026-09-27",
            [
                new("New",
                [
                    "Multi Downloader: a batch queue for playlists and managed downloading.",
                    "Up to 30 simultaneous downloads, depending on your system.",
                    "Cloud upload.",
                    "Build Info tab in Settings.",
                    "Cookies file option for private or members-only videos and to avoid platform limits.",
                ]),
                new("Improved",
                [
                    "Better organised Advanced tab.",
                    "Overhauled notifications.",
                    "Overhauled presets.",
                    "Stability and performance improvements.",
                ]),
            ]),
            new("27H1.16", "2026-09-06",
            [
                new("New",
                [
                    "Native C# engine (YouTube upload still needs the Legacy engine).",
                    "Performance Mode.",
                    "Dynamic layout.",
                    "New theme manager.",
                ]),
                new("Improved",
                [
                    "Better design language.",
                    "Loading bar.",
                    "Download and upload fields.",
                    "The thumbnail preview scales properly when resizing.",
                    "The Customize tab works as it should.",
                ]),
                new("Misc",
                [
                    "FFmpeg installs automatically the first time the Native engine is used.",
                    "FFmpeg is added to PATH if you allow admin access (optional).",
                    "Upgraded to .NET 10.",
                    "Upgraded WPF-UI.",
                ]),
            ]),
        ]),
        new("26H2", "Beta",
        [
            new("26H2", "2026-08-10",
            [
                new("New",
                [
                    "YouTube Music: download tracks at up to 320 kbps, with artist, album, year and track number from the metadata.",
                    "Five new languages: Spanish, French, Italian, Japanese and Chinese.",
                    "The progress window shows each phase (download, fingerprint, upload) with its own progress, speed and time left.",
                    "Notifications when a job succeeds or fails, with an optional sound.",
                    "Every window can be resized with a corner grip.",
                    "Redesigned Designer window with Layout and Animations tabs, gradient controls and collapsible colour groups.",
                ]),
                new("Improved",
                [
                    "Rounded window corners are clipped properly, without square edges behind them.",
                    "Blur is on by default for the Log Viewer, Developer Console and Description Editor.",
                    "Fixed the Sandstone theme preset; pop-ups are cleaner and more opaque.",
                    "Reworked animations: easing, springs and staggering.",
                ]),
                new("Misc",
                [
                    "Console log filters (System, Backend, FFmpeg) and a compact mode.",
                ]),
            ]),
        ]),
        new("v3", "ReelsConverter",
        [
            new("v3.1", "2026-06-27",
            [
                new("New",
                [
                    "Choose whether the progress window opens by itself or only a bar shows at the top.",
                    "Scroll bars can be turned off.",
                    "Animation level: None, Reduced or Standard.",
                    "An \"+ Add New\" preset card and an inline save overlay.",
                ]),
                new("Fixed",
                [
                    "Windows no longer flicker or stretch when they open.",
                    "Better contrast in the light theme, and a subtler Beta tag next to the Video Editor.",
                ]),
            ]),
            new("v3", "2026-04-15",
            [
                new("New",
                [
                    "Video Editor with Whisper subtitles built in.",
                    "Design Editor and a better Settings window.",
                    "GPU acceleration for NVIDIA graphics cards.",
                    "Better logging, and a console to help when something goes wrong.",
                    "Video or audio in 4K, 2K, 1080p, 720p, 480p or audio only.",
                    "Metadata and video/audio changes to avoid automatic duplicate detection.",
                    "Automatic YouTube upload (needs your own API key).",
                ]),
                new("Improved",
                [
                    "Design overhaul and better performance.",
                ]),
            ]),
        ]),
        new("v2", "ReelsConverter",
        [
            new("v2.1", "2026-03-26",
            [
                new("New",
                [
                    "Settings window: language, privacy defaults, fingerprint bypass, backend address and timeout are kept between sessions.",
                    "Quick actions once the metadata is loaded: copy title, description or tags, open in the browser, reset the form.",
                    "DE / EN switch in the title bar; the workflow guide moved to the ? button.",
                ]),
                new("Improved",
                [
                    "Clean log output, a smooth opening animation for Settings and a left panel that scrolls when it is full.",
                    "Shorter German button labels that fit.",
                ]),
            ]),
            new("v2.0", "2026-03-25",
            [
                new("New",
                [
                    "Standalone app (.NET 8, no install) with a Python backend, set up once with pip.",
                    "FFmpeg downloads by itself on the first start.",
                    "YouTube upload with a client_secret.json from Google Cloud.",
                    "Fingerprint bypass methods: metadata, light, standard and strong.",
                ]),
            ]),
        ]),
        new("v1", "Console app",
        [
            new("v1.0", "2026-03-24",
            [
                new("New",
                [
                    "Where it all began: Python console scripts that download an Instagram Reel and upload it to YouTube as a Short by themselves.",
                    "Fingerprint bypass: a slight zoom, brightness, saturation and audio-tempo change plus a re-encode, invisible to viewers.",
                    "AI titles: a frame from the middle of the video goes to OpenAI, which writes the YouTube title.",
                    "The Reel's caption and hashtags become the description and tags; #Shorts is added automatically.",
                    "A recurring mode, and a compilation script that joins many Reels into one video.",
                    "yt-dlp, the Python libraries and FFmpeg install themselves on the first start.",
                ]),
            ]),
        ]),
    ];
}
