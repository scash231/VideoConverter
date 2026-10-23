using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VideoConverterUI.Helpers.EasterEggs;

public class SpiderEasterEgg
{
    private readonly Canvas _containerCanvas;
    private readonly Random _rand = new();
    private DispatcherTimer? _cycleTimer;
    private bool _isActive;

    private Canvas? _spiderGroup;
    private Line? _webLine;
    private TranslateTransform? _spiderTranslate;
    private ScaleTransform? _spiderScale;

    public SpiderEasterEgg(Canvas containerCanvas)
    {
        _containerCanvas = containerCanvas;
        InitializeSpiderVisuals();
    }

    private void InitializeSpiderVisuals()
    {
        _spiderGroup = new Canvas
        {
            Width = 40,
            Height = 40,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _spiderTranslate = new TranslateTransform(0, -100);
        _spiderScale = new ScaleTransform(1, 1, 20, 20);
        
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_spiderScale);
        transformGroup.Children.Add(_spiderTranslate);
        _spiderGroup.RenderTransform = transformGroup;

        // Web Line extending upward to window top
        _webLine = new Line
        {
            X1 = 20,
            Y1 = -1000,
            X2 = 20,
            Y2 = 20,
            Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }
        };
        _spiderGroup.Children.Add(_webLine);

        // Outer Glow
        var glow = new Ellipse
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(2, 2, 0, 0),
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 0, 0))
        };
        _spiderGroup.Children.Add(glow);

        // Spider Abdomen (Back)
        var abdomen = new Ellipse
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(8, 12, 0, 0),
            Fill = new SolidColorBrush(Color.FromRgb(25, 25, 25))
        };
        _spiderGroup.Children.Add(abdomen);

        // Spider Cephalothorax (Head)
        var head = new Ellipse
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(12, 4, 0, 0),
            Fill = new SolidColorBrush(Color.FromRgb(40, 40, 40))
        };
        _spiderGroup.Children.Add(head);

        // Red Glowing Eyes
        var eyeLeft = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(16, 6, 0, 0), Fill = Brushes.Red };
        var eyeRight = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(21, 6, 0, 0), Fill = Brushes.Red };
        _spiderGroup.Children.Add(eyeLeft);
        _spiderGroup.Children.Add(eyeRight);

        // 8 Curved Legs
        var legBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35));
        double[,] legData = new double[,]
        {
            // Left Legs (X1, Y1, X2, Y2)
            { 14, 10, 2, 2 },
            { 14, 14, 0, 14 },
            { 14, 18, 2, 28 },
            { 14, 22, 6, 36 },
            // Right Legs
            { 26, 10, 38, 2 },
            { 26, 14, 40, 14 },
            { 26, 18, 38, 28 },
            { 26, 22, 34, 36 }
        };

        for (int i = 0; i < 8; i++)
        {
            var leg = new Line
            {
                X1 = legData[i, 0],
                Y1 = legData[i, 1],
                X2 = legData[i, 2],
                Y2 = legData[i, 3],
                Stroke = legBrush,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _spiderGroup.Children.Add(leg);
        }

        _containerCanvas.Children.Add(_spiderGroup);
    }

    public void Start()
    {
        if (_isActive) return;
        _isActive = true;

        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cycleTimer.Tick += (_, _) =>
        {
            _cycleTimer.Stop();
            TriggerSpiderDrop();
        };
        _cycleTimer.Start();
    }

    public void Stop()
    {
        _isActive = false;
        _cycleTimer?.Stop();
        if (_spiderGroup != null)
        {
            _spiderGroup.Visibility = Visibility.Collapsed;
        }
    }

    private void TriggerSpiderDrop()
    {
        if (!_isActive || _spiderGroup == null || _spiderTranslate == null) return;

        double width = Math.Max(_containerCanvas.ActualWidth - 60, 600);
        double randomX = 40 + _rand.NextDouble() * (width - 80);
        Canvas.SetLeft(_spiderGroup, randomX);

        double targetY = 120 + _rand.NextDouble() * 220; // Drop 120px to 340px down

        _spiderGroup.Visibility = Visibility.Visible;

        // Smooth Descent Down
        var dropAnim = new DoubleAnimation(-100, targetY, TimeSpan.FromMilliseconds(1400))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };

        dropAnim.Completed += (_, _) =>
        {
            if (!_isActive) return;

            // Pause brief moment at bottom with leg wiggle
            WiggleLegs();

            int pauseMs = _rand.Next(1500, 3000);
            var pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(pauseMs) };
            pauseTimer.Tick += (_, _) =>
            {
                pauseTimer.Stop();
                if (!_isActive) return;

                // Ascend Back UP Fast
                var retractAnim = new DoubleAnimation(targetY, -100, TimeSpan.FromMilliseconds(600))
                {
                    EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
                };

                retractAnim.Completed += (_, _) =>
                {
                    if (!_isActive) return;
                    _spiderGroup.Visibility = Visibility.Collapsed;

                    // Schedule next drop at random pause
                    int waitMs = _rand.Next(2000, 5000);
                    _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
                    _cycleTimer.Tick += (_, _) =>
                    {
                        _cycleTimer.Stop();
                        TriggerSpiderDrop();
                    };
                    _cycleTimer.Start();
                };

                _spiderTranslate.BeginAnimation(TranslateTransform.YProperty, retractAnim);
            };
            pauseTimer.Start();
        };

        _spiderTranslate.BeginAnimation(TranslateTransform.YProperty, dropAnim);
    }

    private void WiggleLegs()
    {
        if (_spiderScale == null) return;
        var wiggle = new DoubleAnimation(0.9, 1.1, TimeSpan.FromMilliseconds(200))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(4)
        };
        _spiderScale.BeginAnimation(ScaleTransform.ScaleXProperty, wiggle);
    }
}
