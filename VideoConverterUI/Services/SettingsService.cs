using System.IO;
using System.Text.Json;
using VideoConverterUI.Models;

namespace VideoConverterUI.Services;

public static class SettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoConverter",
        "settings.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json, _opts) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public static event EventHandler? SettingsChanged;

    public static void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, _opts));
        ApplyScrollbarVisibility();
        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    // ═══ Resize handle ═══
    // The handle in a window's bottom-right corner (an element named "WindowCornerGrip"). Settings → Glass & Layout →
    // Resize handle: shown or not, and if so always or only while the pointer is on it. It is the only way to resize a
    // window (they are all ResizeMode="NoResize", so no edges or snap resizing), so dragging it resizes the window here,
    // by hand. Sizes are not kept: every window opens at its normal size (the one in its XAML), and ResetWindowSizes
    // puts open ones back.

    private const double GripIdleOpacity = 1.0; // the glyph (CornerResizeGlyph) sets its own tones
    private const double GripHoverOpacity = 1.0;

    public static void ApplyResizeGripVisibility(System.Windows.Window window)
    {
        if (window?.FindName("WindowCornerGrip") is not System.Windows.FrameworkElement grip) return;
        RecordNormalSize(window);
        if (!Current.ShowWindowResizerGrip)
        {
            grip.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }
        grip.Visibility = System.Windows.Visibility.Visible;
        FadeGrip(grip, grip.IsMouseOver || grip.IsMouseCaptured);
    }

    public static void HandleGripHover(object sender, bool isHovered)
    {
        if (sender is System.Windows.FrameworkElement grip && Current.ShowWindowResizerGrip)
            FadeGrip(grip, isHovered || grip.IsMouseCaptured);
    }

    private static void FadeGrip(System.Windows.FrameworkElement grip, bool pointedAt)
    {
        double target = pointedAt ? GripHoverOpacity : Current.ResizerGripOnlyOnHover ? 0.0 : GripIdleOpacity;
        if (Animations.FluidMotion.AnimationsDisabled)
        {
            grip.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            grip.Opacity = target;
            return;
        }
        grip.BeginAnimation(System.Windows.UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(pointedAt ? 120 : 260)));
    }

    /// <summary>
    /// Resizes <paramref name="window"/> from its bottom-right corner while the left button stays down on its
    /// resize handle. The top-left corner stays put, so the pointer's position inside the window gives the new size.
    /// <paramref name="onDone"/> runs when the drag ends, with how far width and height changed (relative, e.g. 0.2 = +20 %).
    /// </summary>
    public static void StartWindowResizeBottomRight(System.Windows.Window window, Action<double, double>? onDone = null)
    {
        if (window == null || window.WindowState != System.Windows.WindowState.Normal) return;
        if (window.FindName("WindowCornerGrip") is not System.Windows.FrameworkElement grip) return;

        var (minW, minH) = MinimumSize(window);
        var work = System.Windows.SystemParameters.WorkArea;
        var start = System.Windows.Input.Mouse.GetPosition(window);
        double startW = window.ActualWidth, startH = window.ActualHeight;

        void Move(object? s, System.Windows.Input.MouseEventArgs e)
        {
            var p = e.GetPosition(window);
            double maxW = Math.Max(minW, work.Right - window.Left), maxH = Math.Max(minH, work.Bottom - window.Top);
            window.Width = Math.Clamp(startW + p.X - start.X, minW, maxW);
            window.Height = Math.Clamp(startH + p.Y - start.Y, minH, maxH);
        }
        void Up(object? s, System.Windows.Input.MouseButtonEventArgs e) => grip.ReleaseMouseCapture();
        void Done(object? s, System.Windows.Input.MouseEventArgs e)
        {
            grip.MouseMove -= Move;
            grip.MouseLeftButtonUp -= Up;
            grip.LostMouseCapture -= Done;
            FadeGrip(grip, grip.IsMouseOver);
            onDone?.Invoke(window.ActualWidth / startW - 1, window.ActualHeight / startH - 1);
        }

        grip.MouseMove += Move;
        grip.MouseLeftButtonUp += Up;
        grip.LostMouseCapture += Done;
        if (!grip.CaptureMouse()) Done(null, null!);
    }

    /// <summary>The smallest size a window may be resized to: its own MinWidth/MinHeight, or what its layout needs.</summary>
    private static (double width, double height) MinimumSize(System.Windows.Window window)
    {
        var (w, h) = window switch
        {
            MainWindow => (500.0, 400.0),
            EditorWindow => (600.0, 400.0),
            SettingsWindow => (500.0, 400.0),
            DescriptionEditorWindow => (400.0, 300.0),
            DevConsoleWindow or LogViewerWindow => (350.0, 200.0),
            _ => (360.0, 240.0),
        };
        return (Math.Max(w, window.MinWidth), Math.Max(h, window.MinHeight));
    }

    // The size each window opened at, recorded the first time its resize handle is set up (ApplyResizeGripVisibility,
    // which every window with a handle calls when it loads).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Window, Tuple<double, double>> NormalSizes = new();

    private static void RecordNormalSize(System.Windows.Window window)
    {
        if (NormalSizes.TryGetValue(window, out _)) return;
        double w = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double h = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        if (w > 0 && h > 0) NormalSizes.Add(window, Tuple.Create(w, h));
    }

    /// <summary>Puts every open window back to the size it opened at.</summary>
    public static void ResetWindowSizes()
    {
        foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
        {
            if (window.WindowState != System.Windows.WindowState.Normal || !NormalSizes.TryGetValue(window, out var size)) continue;
            window.Width = size.Item1;
            window.Height = size.Item2;
        }
    }

    /// <summary>
    /// True when a mouse event came from a pop-up (its own HWND) and not from <paramref name="window"/> itself.
    /// A Popup declared inside a title bar routes its clicks up to the title bar, where they must not start DragMove.
    /// </summary>
    public static bool IsFromPopup(System.Windows.Window window, object? source)
    {
        var d = source as System.Windows.DependencyObject;
        while (d is not null and not System.Windows.Media.Visual and not System.Windows.Media.Media3D.Visual3D)
            d = System.Windows.LogicalTreeHelper.GetParent(d);
        return d != null && System.Windows.PresentationSource.FromDependencyObject(d)?.RootVisual is { } root && root != window;
    }

    public static void ApplyScrollbarVisibility()
    {
        var s = Current;
        var width = s.HideScrollbars ? 0.0 : 6.0;
        var height = s.HideScrollbars ? 0.0 : 6.0;

        if (System.Windows.Application.Current != null)
        {
            var res = System.Windows.Application.Current.Resources;
            res["GlobalScrollBarWidth"] = width;
            res["GlobalScrollBarHeight"] = height;
        }
    }

}
