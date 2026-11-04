using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VideoConverterUI.Animations;
using VideoConverterUI.Services;
using Wpf.Ui.Controls;

namespace VideoConverterUI.Controls;

/// <summary>A row of a <see cref="GlassMenu"/>: glisse's menu item, an icon and a label.</summary>
public sealed class GlassMenuItem : Border
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(GlassMenuItem),
        new PropertyMetadata(SymbolRegular.Empty, (d, e) =>
        {
            var item = (GlassMenuItem)d;
            item._icon.Symbol = (SymbolRegular)e.NewValue;
            item._icon.Visibility = (SymbolRegular)e.NewValue == SymbolRegular.Empty ? Visibility.Collapsed : Visibility.Visible;
        }));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(GlassMenuItem),
        new PropertyMetadata(string.Empty, (d, e) => ((GlassMenuItem)d)._label.Text = (string)e.NewValue));

    public static readonly DependencyProperty IsDestructiveProperty = DependencyProperty.Register(nameof(IsDestructive), typeof(bool), typeof(GlassMenuItem),
        new PropertyMetadata(false, (d, _) => ((GlassMenuItem)d).ApplyColour()));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(GlassMenuItem),
        new PropertyMetadata(string.Empty, (d, e) =>
        {
            var item = (GlassMenuItem)d;
            item._detail.Text = (string)e.NewValue;
            item._detail.Visibility = string.IsNullOrEmpty((string)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(GlassMenuItem),
        new PropertyMetadata(false, (d, e) => ((GlassMenuItem)d)._check.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsDestructive { get => (bool)GetValue(IsDestructiveProperty); set => SetValue(IsDestructiveProperty, value); }

    /// <summary>A quieter note after the label.</summary>
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    /// <summary>A checkmark at the row's end, for a menu that picks one of several.</summary>
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    /// <summary>Raised once the menu has started closing.</summary>
    public event RoutedEventHandler? Click;

    private readonly SymbolIcon _icon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
    private readonly System.Windows.Controls.TextBlock _label = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.TextBlock _detail = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly SymbolIcon _check = new() { Symbol = SymbolRegular.Checkmark24, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed };

    public GlassMenuItem()
    {
        Background = Brushes.Transparent;
        Padding = new Thickness(12, 0, 12, 0);
        MinHeight = 32;
        Cursor = Cursors.Hand;
        DockPanel.SetDock(_check, Dock.Right);
        Child = new DockPanel
        {
            LastChildFill = true,
            Children = { _check, new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { _icon, _label, _detail } } },
        };
        _detail.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSec");
        ApplyColour();
    }

    private void ApplyColour()
    {
        string key = IsDestructive ? "ErrorRed" : "TextPrimary";
        _icon.SetResourceReference(SymbolIcon.ForegroundProperty, key);
        _label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, key);
        _check.SetResourceReference(SymbolIcon.ForegroundProperty, key);
    }

    internal void RaiseClick() => Click?.Invoke(this, new RoutedEventArgs());
}

/// <summary>
/// glisse's menu (github.com/0kzh/glisse, src/menu.tsx). Opened from a round trigger, the glass grows out of it, lined up
/// with its top-right corner, while the trigger's icon swells and blurs away and the rows scale up from its centre and come
/// into focus. Closing runs it back. A plate slides between rows, for a hovering mouse and for a drag alike; press and drag
/// across the rows to choose one on release, the card leaning after the pointer. Arrow keys, Home, End, Enter, Space, Esc and
/// Tab work while it is open. Closes on any click elsewhere and when the window loses focus. It is a Popup with a still,
/// see-through window: all the motion happens inside, at the card's size plus room for the shadow.
/// </summary>
[ContentProperty(nameof(Items))]
public sealed class GlassMenu : Popup
{
    private enum Phase { Closed, Open, Leaving }

    private const double MinCardWidth = 190, CardRadius = 20, ContentPadding = 6;
    private const double RoomSide = 44, RoomTop = 16, RoomBottom = 60;

    private static readonly Spring OpenSpring = GlassPress.FromStiffness(600, 46);
    private static readonly Spring CloseSpring = GlassPress.FromStiffness(900, 57);
    private static readonly Spring PressSpring = GlassPress.FromStiffness(650, 36);
    private static readonly Spring LeanSpring = GlassPress.FromStiffness(600, 30);
    private static readonly Spring PlateSpring = Spring.WithBounce(0.16, 0);

    private static readonly DependencyProperty MorphProperty = DependencyProperty.Register("Morph", typeof(double), typeof(GlassMenu),
        new PropertyMetadata(0.0, (d, e) => ((GlassMenu)d).Paint((double)e.NewValue)));

    /// <summary>The trigger's icon, which the opening glass carries away.</summary>
    public SymbolRegular Glyph
    {
        get => _glyph.Symbol;
        set => _glyph.Symbol = value;
    }

    /// <summary>For a trigger that shows text rather than an icon: the text, carried away in the icon's place.</summary>
    public string? GlyphText
    {
        get => _glyphText.Text;
        set
        {
            _glyphText.Text = value ?? string.Empty;
            bool text = !string.IsNullOrEmpty(value);
            _glyphText.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            _glyph.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public Collection<GlassMenuItem> Items { get; } = [];

    private readonly Grid _root = new() { Background = Brushes.Transparent };
    private readonly Grid _card = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _glassHost = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly LiquidGlassPanel _glass = new() { Surface = GlassSurfaces.Menu, AdaptiveContrast = false, TextShadow = false };
    private readonly Border _pressedLight = new() { CornerRadius = new CornerRadius(CardRadius), IsHitTestVisible = false, Opacity = 0 };
    private readonly SymbolIcon _glyph = new() { Symbol = SymbolRegular.MoreHorizontal24, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.TextBlock _glyphText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Grid _glyphHost = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly ScaleTransform _glyphScale = new();
    private readonly TranslateTransform _glyphMove = new();
    private readonly BlurEffect _glyphBlur = new() { RenderingBias = RenderingBias.Performance };
    private readonly Grid _content = new() { Margin = new Thickness(ContentPadding) };
    private readonly Grid _contentHost = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScaleTransform _contentScale = new();
    private readonly BlurEffect _contentBlur = new() { RenderingBias = RenderingBias.Performance };
    private readonly StackPanel _rows = new();
    private readonly Border _plate = new() { CornerRadius = new CornerRadius(14), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TranslateTransform _plateMove = new();

    private Phase _phase = Phase.Closed;
    private FrameworkElement? _trigger;
    private UIElement? _fades;
    private Window? _window;
    private Size _from, _to;
    private bool _built;

    private bool _gesture, _pressMoved;
    private Point _gestureStart;
    private GlassMenuItem? _current;

    public GlassMenu()
    {
        AllowsTransparency = true;
        StaysOpen = true;  // the card takes the mouse for its drag; outside clicks are handled here
        PopupAnimation = PopupAnimation.None;
        Placement = PlacementMode.Relative;

        _plate.SetResourceReference(Border.BackgroundProperty, "GlassHighlight");
        _plate.RenderTransform = _plateMove;
        _glyph.SetResourceReference(SymbolIcon.ForegroundProperty, "TextPrimary");
        _pressedLight.Background = new LinearGradientBrush(Color.FromArgb(0x1C, 255, 255, 255), Color.FromArgb(0x06, 255, 255, 255), 45);
        _glyphText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");
        _glyphHost.Children.Add(_glyph);
        _glyphHost.Children.Add(_glyphText);
        _glyphHost.RenderTransformOrigin = new Point(0.5, 0.5);
        var glyphTransform = new TransformGroup();
        glyphTransform.Children.Add(_glyphScale);
        glyphTransform.Children.Add(_glyphMove);
        _glyphHost.RenderTransform = glyphTransform;
        _contentHost.RenderTransform = _contentScale;

        _glassHost.Child = new GlassShadow { Child = _glass };
        _content.Children.Add(_plate);
        _content.Children.Add(_rows);
        _contentHost.Children.Add(_content);
        _card.Children.Add(_glassHost);
        _card.Children.Add(_pressedLight);
        _card.Children.Add(_glyphHost);
        _card.Children.Add(_contentHost);
        _root.Children.Add(_card);
        Child = _root;

        _card.MouseMove += OnCardMove;
        _card.MouseLeave += (_, _) => { if (!_gesture) Highlight(null); };
        _card.PreviewMouseLeftButtonDown += OnCardDown;
        _card.PreviewMouseLeftButtonUp += OnCardUp;
        _card.LostMouseCapture += (_, _) => { if (_gesture) EndGesture(null); };
    }

    /// <summary>Opens it from <paramref name="trigger"/>, or closes it. <paramref name="fades"/> goes with the trigger (its glass pill).</summary>
    public void Toggle(FrameworkElement trigger, UIElement? fades = null)
    {
        if (_phase == Phase.Open) Close();
        else Open(trigger, fades);
    }

    private void Build()
    {
        if (_built) return;
        _built = true;
        foreach (var item in Items) _rows.Children.Add(item);
    }

    private void Open(FrameworkElement trigger, UIElement? fades)
    {
        Build();
        bool wasClosed = _phase == Phase.Closed;
        _phase = Phase.Open;
        _trigger = trigger;
        _fades = fades;

        if (wasClosed)
        {
            _from = new Size(trigger.ActualWidth, trigger.ActualHeight);
            // Measured free of the last opening's size: the labels may have changed language since.
            _contentHost.ClearValue(FrameworkElement.WidthProperty);
            _contentHost.ClearValue(FrameworkElement.HeightProperty);
            _contentHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = Math.Max(MinCardWidth, Math.Ceiling(_contentHost.DesiredSize.Width));
            _to = new Size(width, Math.Ceiling(_contentHost.DesiredSize.Height));

            _card.Width = _to.Width;
            _card.Height = _to.Height;
            _card.Margin = new Thickness(RoomSide, RoomTop, RoomSide, RoomBottom);
            _contentHost.Width = _to.Width;
            _contentHost.Height = _to.Height;
            _glyphHost.Width = _from.Width;
            _glyphHost.Height = _from.Height;

            PlacementTarget = trigger;
            AimAtTrigger();
            VerticalOffset = -RoomTop;

            _plate.Opacity = 0;
            _current = null;
            _glass.CornerRadius = CardRadius;
            trigger.BeginAnimation(UIElement.OpacityProperty, null);  // the header's motion leaves its finished fade holding this
            fades?.BeginAnimation(UIElement.OpacityProperty, null);
            trigger.IsVisibleChanged += OnTriggerVisibleChanged;
            trigger.SizeChanged += OnTriggerSizeChanged;
            _window = Window.GetWindow(trigger);
            if (_window != null)
            {
                _window.PreviewMouseDown += OnWindowMouseDown;
                _window.PreviewKeyDown += OnWindowKeyDown;
                _window.Deactivated += OnWindowDeactivated;
            }

            BeginAnimation(MorphProperty, null);
            SetValue(MorphProperty, 0.0);
            Paint(0);
            IsOpen = true;
        }

        if (FluidMotion.AnimationsDisabled)
        {
            BeginAnimation(MorphProperty, null);
            SetValue(MorphProperty, 1.0);
            return;
        }
        Liquid.To(this, MorphProperty, 1, OpenSpring);
    }

    /// <summary>Runs the opening back. Safe to call when it is already closed or closing.</summary>
    public void Close()
    {
        if (_phase != Phase.Open) return;
        _phase = Phase.Leaving;
        Highlight(null);
        if (_gesture) EndGesture(null);
        if (FluidMotion.AnimationsDisabled)
        {
            Finish();
            return;
        }
        Liquid.To(this, MorphProperty, 0, CloseSpring, precision: 0.003, done: Finish);
    }

    private void Finish()
    {
        if (_phase != Phase.Leaving) return;
        _phase = Phase.Closed;
        IsOpen = false;
        BeginAnimation(MorphProperty, null);
        SetValue(MorphProperty, 0.0);
        _contentHost.Effect = null;
        _glyphHost.Effect = null;
        _trigger?.ClearValue(UIElement.OpacityProperty);
        _fades?.ClearValue(UIElement.OpacityProperty);
        if (_trigger != null)
        {
            _trigger.IsVisibleChanged -= OnTriggerVisibleChanged;
            _trigger.SizeChanged -= OnTriggerSizeChanged;
        }
        if (_window != null)
        {
            _window.PreviewMouseDown -= OnWindowMouseDown;
            _window.PreviewKeyDown -= OnWindowKeyDown;
            _window.Deactivated -= OnWindowDeactivated;
        }
        _trigger = null;
        _fades = null;
        _window = null;
    }

    private void OnTriggerVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Close();  // the header unfolded its tools
    }

    // The card's top-right corner on the trigger's.
    private void AimAtTrigger() => HorizontalOffset = _from.Width - (_to.Width + RoomSide * 2) + RoomSide;

    // A row can change the trigger itself (a new language's name): the closing glass heads for what it is now.
    private void OnTriggerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _from = e.NewSize;
        _glyphHost.Width = _from.Width;
        _glyphHost.Height = _from.Height;
        AimAtTrigger();
        Paint((double)GetValue(MorphProperty));
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Close();

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_trigger is { IsMouseOver: true }) return;  // its click toggles
        if (_card.IsMouseOver) return;  // a Popup's mouse events tunnel through the window that declares it, on their way to the card
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_phase != Phase.Open) return;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Tab:
                Close();
                break;
            case Key.Down:
            case Key.Up:
            case Key.Home:
            case Key.End:
            {
                int count = Items.Count, index = _current == null ? -1 : Items.IndexOf(_current);
                int next = e.Key switch
                {
                    Key.Home => 0,
                    Key.End => count - 1,
                    Key.Down => (index + 1) % count,
                    _ => index < 0 ? count - 1 : (index - 1 + count) % count,
                };
                Highlight(Items[next]);
                e.Handled = true;
                break;
            }
            case Key.Enter:
            case Key.Space:
                if (_current is { } chosen)
                {
                    Choose(chosen);
                    e.Handled = true;
                }
                break;
        }
    }

    private void Choose(GlassMenuItem item)
    {
        Close();
        item.RaiseClick();
    }

    // ═══ The morph ═══

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private void Paint(double value)
    {
        if (_to.Width <= 0) return;
        double t = Clamp(value);
        bool settled = t >= 0.9995;
        double width = _from.Width + (_to.Width - _from.Width) * t;
        double height = _from.Height + (_to.Height - _from.Height) * t;
        double scale = 0.5 + 0.5 * t;
        double originX = _to.Width - _from.Width / 2, originY = _from.Height / 2;

        // The glass, its corner a capsule's until the card has outgrown that.
        _glassHost.Width = width;
        _glassHost.Height = height;
        _glassHost.Opacity = Clamp(t / 0.2);
        _glass.CornerRadius = Math.Min(CardRadius, Math.Min(width, height) / 2);
        _pressedLight.Width = width;
        _pressedLight.Height = height;
        _pressedLight.HorizontalAlignment = HorizontalAlignment.Right;
        _pressedLight.VerticalAlignment = VerticalAlignment.Top;

        double triggerOpacity = 1 - Clamp(t / 0.2);
        if (t <= 0)
        {
            _trigger?.ClearValue(UIElement.OpacityProperty);
            _fades?.ClearValue(UIElement.OpacityProperty);
        }
        else
        {
            if (_trigger != null) _trigger.Opacity = triggerOpacity;
            if (_fades != null) _fades.Opacity = triggerOpacity;
        }

        // The trigger's icon rides the glass towards its centre, swelling and blurring away as the rows arrive.
        double drift = 0.3 * t;
        _glyphMove.X = (_to.Width - width / 2 - originX) * drift;
        _glyphMove.Y = (height / 2 - originY) * drift;
        _glyphScale.ScaleX = _glyphScale.ScaleY = 1 + 0.6 * t;
        double glyphOpacity = 1 - Clamp(t / 0.45);
        _glyphHost.Opacity = glyphOpacity;
        _glyphHost.Visibility = glyphOpacity <= 0 ? Visibility.Collapsed : Visibility.Visible;
        _glyphBlur.Radius = 8 * t;
        _glyphHost.Effect = glyphOpacity > 0 ? _glyphBlur : null;

        // The rows stay inside the glass: clip them to its rect, mapped back through their own scale.
        bool leaving = _phase == Phase.Leaving;
        _contentHost.Opacity = leaving ? Clamp((t - 0.55) / 0.45) : Clamp((t - 0.15) / 0.6);
        _contentHost.IsHitTestVisible = _phase == Phase.Open && t > 0.5;
        if (settled)
        {
            _contentScale.ScaleX = _contentScale.ScaleY = 1;
            _contentHost.Clip = null;
            _contentHost.Effect = null;
            return;
        }
        _contentScale.CenterX = originX;
        _contentScale.CenterY = originY;
        _contentScale.ScaleX = _contentScale.ScaleY = scale;
        double Local(double edge, double centre) => centre + (edge - centre) / scale;
        double left = Local(_to.Width - width, originX), right = Local(_to.Width, originX);
        double top = Local(0, originY), bottom = Local(height, originY);
        _contentHost.Clip = new RectangleGeometry(new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)), CardRadius / scale, CardRadius / scale);
        _contentBlur.Radius = 6 * (1 - t);
        _contentHost.Effect = _contentBlur;
    }

    // ═══ The plate and the press ═══

    private GlassMenuItem? RowAt(Point inRows)
    {
        foreach (var item in Items)
        {
            var at = item.TranslatePoint(new Point(0, 0), _rows);
            if (inRows.X >= at.X && inRows.X <= at.X + item.ActualWidth && inRows.Y >= at.Y && inRows.Y <= at.Y + item.ActualHeight)
                return item;
        }
        return null;
    }

    // The first row lights up in place and at once; later rows slide to it.
    private void Highlight(GlassMenuItem? row)
    {
        if (ReferenceEquals(_current, row)) return;
        bool first = _current == null;
        _current = row;
        if (row == null)
        {
            _plate.Opacity = 0;
            return;
        }
        double y = row.TranslatePoint(new Point(0, 0), _rows).Y;
        _plate.Height = row.ActualHeight;
        if (first || FluidMotion.AnimationsDisabled)
        {
            _plateMove.BeginAnimation(TranslateTransform.YProperty, null);
            _plateMove.Y = y;
        }
        else
            Liquid.To(_plateMove, TranslateTransform.YProperty, y, PlateSpring);
        _plate.Opacity = 1;
    }

    private void OnCardMove(object sender, MouseEventArgs e)
    {
        var row = RowAt(e.GetPosition(_rows));
        if (!_gesture)
        {
            // Crossing a gap keeps the plate on the last row.
            if (row != null) Highlight(row);
            return;
        }
        Highlight(row);
        var p = e.GetPosition(_root);
        Lean(p.X - _gestureStart.X, p.Y - _gestureStart.Y);
        _pressMoved = true;
    }

    private void OnCardDown(object sender, MouseButtonEventArgs e)
    {
        if (_phase != Phase.Open) return;
        e.Handled = true;
        _gesture = true;
        _pressMoved = false;
        _gestureStart = e.GetPosition(_root);
        _card.CaptureMouse();
        _pressedLight.Opacity = 1;
        Highlight(RowAt(e.GetPosition(_rows)));
        if (!FluidMotion.AnimationsDisabled)
        {
            var (scale, _) = Rig();
            double grown = 1 + 5 / Math.Max(Math.Max(_to.Width, _to.Height), 200);
            Liquid.To(scale, ScaleTransform.ScaleXProperty, grown, PressSpring);
            Liquid.To(scale, ScaleTransform.ScaleYProperty, grown, PressSpring);
        }
    }

    private void OnCardUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gesture) return;
        e.Handled = true;
        EndGesture(RowAt(e.GetPosition(_rows)));
    }

    private void EndGesture(GlassMenuItem? chosen)
    {
        _gesture = false;
        _pressedLight.Opacity = 0;
        if (_card.IsMouseCaptured) _card.ReleaseMouseCapture();
        if (!FluidMotion.AnimationsDisabled)
        {
            var (scale, _) = Rig();
            Liquid.To(scale, ScaleTransform.ScaleXProperty, 1, PressSpring);
            Liquid.To(scale, ScaleTransform.ScaleYProperty, 1, PressSpring);
            Lean(0, 0);
        }
        if (chosen != null && _phase == Phase.Open) Choose(chosen);
    }

    private (ScaleTransform Scale, TranslateTransform Move) Rig()
    {
        var rig = Liquid.Rig(_card);
        // Pressed, it grows around the trigger's corner.
        rig.Scale.CenterX = _to.Width - CardRadius;
        rig.Scale.CenterY = CardRadius;
        return rig;
    }

    private void Lean(double dx, double dy)
    {
        if (FluidMotion.AnimationsDisabled) return;
        static double Resist(double d) => Math.Sign(d) * 7 * (1 - Math.Exp(-Math.Abs(d) * 0.12 / 7));
        var (_, move) = Rig();
        Liquid.To(move, TranslateTransform.XProperty, Resist(dx), LeanSpring);
        Liquid.To(move, TranslateTransform.YProperty, Resist(dy), LeanSpring);
    }
}
