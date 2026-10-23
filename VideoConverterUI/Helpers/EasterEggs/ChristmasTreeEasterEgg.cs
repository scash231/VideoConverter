using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoConverterUI.Animations;

namespace VideoConverterUI.Helpers.EasterEggs;

public class ChristmasTreeEasterEgg
{
    private readonly Canvas _treeContainer;
    private bool _isActive;

    public ChristmasTreeEasterEgg(Canvas treeContainer)
    {
        _treeContainer = treeContainer;
    }

    public void Start()
    {
        if (_isActive || _treeContainer == null) return;
        _isActive = true;

        _treeContainer.Visibility = Visibility.Visible;
        var starAnim = _treeContainer.Resources["StarTwinkle"] as Storyboard;
        starAnim?.Begin();
        var floatAnim = _treeContainer.Resources["TreeFloat"] as Storyboard;
        floatAnim?.Begin();

        _treeContainer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(400)));
        
        if (_treeContainer.RenderTransform is TransformGroup group && group.Children[0] is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(500)) { EasingFunction = AppleSpringEase.Bouncy });
            st.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(500)) { EasingFunction = AppleSpringEase.Bouncy });
        }
    }

    public void Stop()
    {
        if (!_isActive || _treeContainer == null) return;
        _isActive = false;

        var fadeOut = new DoubleAnimation(_treeContainer.Opacity, 0, TimeSpan.FromMilliseconds(250));
        fadeOut.Completed += (_, _) => _treeContainer.Visibility = Visibility.Collapsed;
        _treeContainer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
}
