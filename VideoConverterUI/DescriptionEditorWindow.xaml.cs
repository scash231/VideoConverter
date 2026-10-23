using VideoConverterUI.Animations;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace VideoConverterUI;

public partial class DescriptionEditorWindow : Window
{
    public string Description { get; private set; } = string.Empty;
    private readonly Rect _originRect;
    private bool _isAnimatingClose;
    public bool IsSaved { get; private set; } = false;

    public DescriptionEditorWindow(string initialText, Rect originRect)
    {
        InitializeComponent();
        _originRect = originRect;
        TxtEditor.Text = initialText;
        TxtEditor.CaretIndex = TxtEditor.Text.Length;
        Loaded += OnLoaded;
    }

    private void WindowCornerGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Services.SettingsService.StartWindowResizeBottomRight(this);
        e.Handled = true; // no drag-move of the window underneath
    }

    private void WindowCornerGrip_MouseEnter(object sender, MouseEventArgs e)
        => Services.SettingsService.HandleGripHover(sender, true);

    private void WindowCornerGrip_MouseLeave(object sender, MouseEventArgs e)
        => Services.SettingsService.HandleGripHover(sender, false);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.SettingsService.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) => Services.SettingsService.SettingsChanged -= OnSettingsChanged;
        Services.SettingsService.ApplyResizeGripVisibility(this);
        FluidMotion.MorphOpen(RootBorder, PopScale, PopTranslate, _originRect, this);
        TxtEditor.Focus();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Services.SettingsService.ApplyResizeGripVisibility(this);

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Apply_Click(object s, RoutedEventArgs e) => CloseWithAnimation(true);
    private void Cancel_Click(object s, RoutedEventArgs e) => CloseWithAnimation(false);
    private void Close_Click(object s, RoutedEventArgs e) => CloseWithAnimation(false);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Services.SettingsService.Current.EnablePerformanceMode)
        {
            base.OnClosing(e);
            return;
        }

        if (!_isAnimatingClose) { e.Cancel = true; CloseWithAnimation(false); }
        base.OnClosing(e);
    }

    private void CloseWithAnimation(bool? result)
    {
        if (_isAnimatingClose) return;
        _isAnimatingClose = true;
        if (result == true) Description = TxtEditor.Text;
        IsSaved = (result == true);
        FluidMotion.MorphClose(RootBorder, PopScale, PopTranslate, this, Close);
    }
}
