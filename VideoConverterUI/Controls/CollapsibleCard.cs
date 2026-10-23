using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoConverterUI.Animations;

namespace VideoConverterUI.Controls;

/// <summary>
/// The app's one collapsible card: a clickable header (title, optional description, optional summary
/// text and a chevron) above a body that smoothly grows open and shrinks closed.
/// Its look lives in App.xaml (implicit style); windows only adjust the card surface to match their
/// other cards.
/// </summary>
public class CollapsibleCard : HeaderedContentControl
{
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CollapsibleCard),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsExpandedChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(CollapsibleCard), new PropertyMetadata(null));

    /// <summary>Short status text shown next to the chevron, e.g. "3 Active".</summary>
    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(string), typeof(CollapsibleCard), new PropertyMetadata(null));

    /// <summary>Optional colour marker shown left of the title.</summary>
    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(CollapsibleCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner(typeof(CollapsibleCard));

    public static readonly RoutedEvent ExpandedEvent =
        EventManager.RegisterRoutedEvent(nameof(Expanded), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CollapsibleCard));

    public static readonly RoutedEvent CollapsedEvent =
        EventManager.RegisterRoutedEvent(nameof(Collapsed), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CollapsibleCard));

    private ExpandPresenter? _body;
    private RotateTransform? _chevronRotation;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Summary
    {
        get => (string?)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public event RoutedEventHandler Expanded
    {
        add => AddHandler(ExpandedEvent, value);
        remove => RemoveHandler(ExpandedEvent, value);
    }

    public event RoutedEventHandler Collapsed
    {
        add => AddHandler(CollapsedEvent, value);
        remove => RemoveHandler(CollapsedEvent, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _body = GetTemplateChild("PART_Body") as ExpandPresenter;

        // Transforms declared in a template are frozen; the chevron needs its own to animate.
        _chevronRotation = null;
        if (GetTemplateChild("PART_Chevron") is UIElement chevron)
            chevron.RenderTransform = _chevronRotation = new RotateTransform();

        UpdateState(animate: false);
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (CollapsibleCard)d;
        card.UpdateState(animate: card.IsLoaded);
        card.RaiseEvent(new RoutedEventArgs((bool)e.NewValue ? ExpandedEvent : CollapsedEvent, card));
    }

    private void UpdateState(bool animate)
    {
        animate &= !FluidMotion.AnimationsDisabled;
        _body?.SetExpanded(IsExpanded, animate);

        if (_chevronRotation == null) return;
        double angle = IsExpanded ? 180 : 0;
        if (animate)
        {
            _chevronRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(angle, ExpandPresenter.DurationFor(IsExpanded)) { EasingFunction = AppleSpringEase.Interactive });
        }
        else
        {
            _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _chevronRotation.Angle = angle;
        }
    }
}

/// <summary>
/// Shows its child at <see cref="Progress"/> × its natural height (clipped), fading and sliding it in,
/// so the content below moves smoothly while a <see cref="CollapsibleCard"/> opens or closes.
/// </summary>
public sealed class ExpandPresenter : Decorator
{
    private const double SlideDistance = 8;

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ExpandPresenter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnProgressChanged));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public ExpandPresenter() => ApplyProgress(); // the change callback doesn't run for the default 0

    /// <summary>Opening takes a little longer than closing.</summary>
    public static TimeSpan DurationFor(bool expanding) => TimeSpan.FromMilliseconds(expanding ? 380 : 260);

    /// <summary>Opens or closes the content, animated from wherever a running animation currently is.</summary>
    public void SetExpanded(bool expanded, bool animate)
    {
        double target = expanded ? 1 : 0;
        if (!animate || FluidMotion.AnimationsDisabled)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = target;
            return;
        }
        if (Progress == target && !HasAnimatedProperties) return;
        BeginAnimation(ProgressProperty,
            new DoubleAnimation(target, DurationFor(expanded)) { EasingFunction = AppleSpringEase.Smooth });
    }

    private double ClampedProgress => Math.Clamp(Progress, 0, 1);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpandPresenter)d).ApplyProgress();

    private void ApplyProgress()
    {
        double p = ClampedProgress;

        // Fully closed content is collapsed so it can't be tabbed into and costs no layout.
        Visibility = p <= 0 ? Visibility.Collapsed : Visibility.Visible;
        // Fade faster than the height changes: closing content is gone before it gets squeezed,
        // opening content appears once there is room for it.
        Opacity = p * p;
        // Clip only while moving, so hover effects inside an open card aren't cut off.
        ClipToBounds = p < 1;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return default;
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var desired = Child.DesiredSize;
        return new Size(desired.Width, desired.Height * ClampedProgress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child != null)
        {
            double height = Math.Max(arrangeSize.Height, Child.DesiredSize.Height);
            Child.Arrange(new Rect(0, -(1 - ClampedProgress) * SlideDistance, arrangeSize.Width, height));
        }
        return arrangeSize;
    }
}
