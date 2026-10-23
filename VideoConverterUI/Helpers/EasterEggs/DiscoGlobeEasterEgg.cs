using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoConverterUI.Helpers.EasterEggs;

public class DiscoGlobeEasterEgg
{
    private readonly Canvas _discoContainer;
    private readonly TranslateTransform _lowerTransform;
    private bool _isActive;

    public DiscoGlobeEasterEgg(Canvas discoContainer, TranslateTransform lowerTransform)
    {
        _discoContainer = discoContainer;
        _lowerTransform = lowerTransform;
    }

    public void Start()
    {
        if (_isActive || _discoContainer == null || _lowerTransform == null) return;
        _isActive = true;

        _discoContainer.Visibility = Visibility.Visible;
        var raysAnim = _discoContainer.Resources["DiscoRaysRotate"] as Storyboard;
        raysAnim?.Begin();
        var glintAnim = _discoContainer.Resources["DiscoGlint"] as Storyboard;
        glintAnim?.Begin();

        // Slowly lower down 120px into view from top
        _lowerTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 120, TimeSpan.FromMilliseconds(1600))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            });
    }

    public void Stop()
    {
        if (!_isActive || _discoContainer == null || _lowerTransform == null) return;
        _isActive = false;

        var retractAnim = new DoubleAnimation(_lowerTransform.Y, 0, TimeSpan.FromMilliseconds(1200))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        retractAnim.Completed += (_, _) => _discoContainer.Visibility = Visibility.Collapsed;
        _lowerTransform.BeginAnimation(TranslateTransform.YProperty, retractAnim);
    }
}
