using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VideoConverterUI.Helpers.EasterEggs;

public class SnowfallEasterEgg
{
    private readonly Canvas _snowCanvas;
    private readonly FrameworkElement _boundaryElement;
    private readonly Random _rand = new();
    private bool _isActive;

    public SnowfallEasterEgg(Canvas snowCanvas, FrameworkElement boundaryElement)
    {
        _snowCanvas = snowCanvas;
        _boundaryElement = boundaryElement;
    }

    public void Start()
    {
        if (_isActive) return;
        _isActive = true;

        _snowCanvas.Children.Clear();
        _snowCanvas.Visibility = Visibility.Visible;

        double canvasWidth = Math.Max(_boundaryElement.ActualWidth, 700);
        double canvasHeight = Math.Max(_boundaryElement.ActualHeight, 450);

        string[] snowChars = new[] { "❄", "❅", "❆", "•" };

        for (int i = 0; i < 32; i++)
        {
            var flake = new TextBlock
            {
                Text = snowChars[_rand.Next(snowChars.Length)],
                FontSize = _rand.Next(10, 20),
                Foreground = new SolidColorBrush(Color.FromArgb((byte)_rand.Next(100, 230), 255, 255, 255)),
                IsHitTestVisible = false
            };

            double startX = _rand.NextDouble() * canvasWidth;
            double fallDuration = _rand.NextDouble() * 4.0 + 3.0; // 3 to 7s
            double delay = _rand.NextDouble() * 3.0;

            Canvas.SetLeft(flake, startX);
            Canvas.SetTop(flake, -30);

            var tt = new TranslateTransform();
            flake.RenderTransform = tt;

            _snowCanvas.Children.Add(flake);

            var fallAnim = new DoubleAnimation(-30, canvasHeight + 30, TimeSpan.FromSeconds(fallDuration))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            tt.BeginAnimation(TranslateTransform.YProperty, fallAnim);

            var swayAnim = new DoubleAnimation(-15, 15, TimeSpan.FromSeconds(_rand.NextDouble() * 2.0 + 1.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            tt.BeginAnimation(TranslateTransform.XProperty, swayAnim);
        }
    }

    public void Stop()
    {
        _isActive = false;
        _snowCanvas.Children.Clear();
        _snowCanvas.Visibility = Visibility.Collapsed;
    }
}
