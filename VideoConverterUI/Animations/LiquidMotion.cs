using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoConverterUI.Models;

namespace VideoConverterUI.Animations;

/// <summary>
/// A physical spring (mass 1), described like SwiftUI's <c>Spring(response:dampingRatio:)</c>: <see cref="Response"/>
/// is about how long the motion takes to feel done, in seconds; <see cref="Damping"/> 1 = no overshoot, lower = bouncier.
/// Unlike <see cref="AppleSpringEase"/> it isn't squeezed into a fixed duration: a <see cref="SpringAnimation"/> runs as
/// long as the spring needs to settle, so the same spring feels the same over any distance.
/// </summary>
public readonly struct Spring
{
    public double Response { get; }
    public double Damping { get; }

    public Spring(double response, double damping)
    {
        Response = Math.Max(0.02, response);
        Damping = Math.Clamp(damping, 0.05, 4.0);
    }

    /// <summary>SwiftUI's <c>Spring(duration:bounce:)</c>: bounce 0 = smooth, 0.15 = snappy, 0.3 = bouncy, below 0 = sluggish.</summary>
    public static Spring WithBounce(double duration, double bounce)
        => new(duration, bounce >= 0 ? 1 - bounce : 1 / (1 + bounce));

    /// <summary>The same spring, <paramref name="factor"/> times as long.</summary>
    public Spring Slower(double factor) => new(Response * factor, Damping);

    /// <summary>The same timing without overshoot.</summary>
    public Spring Smooth => new(Response, 1);

    // ═══ The Liquid motion's vocabulary ═══

    /// <summary>Fades, and anything else that must not overshoot.</summary>
    public static readonly Spring Fade = WithBounce(0.22, 0);
    /// <summary>Things leaving: quick and without bounce.</summary>
    public static readonly Spring Exit = WithBounce(0.26, 0);
    /// <summary>Long travels (pages, progress bars): smooth.</summary>
    public static readonly Spring Glide = WithBounce(0.44, 0);
    /// <summary>Small controls answering the pointer.</summary>
    public static readonly Spring Snappy = WithBounce(0.32, 0.15);
    /// <summary>Things arriving.</summary>
    public static readonly Spring Bouncy = WithBounce(0.46, 0.28);
    /// <summary>Glass blooming open (menus, banners, popups): the softest overshoot.</summary>
    public static readonly Spring Jelly = WithBounce(0.52, 0.36);
    /// <summary>A press going down.</summary>
    public static readonly Spring Press = WithBounce(0.14, 0);
}

/// <summary>Closed-form motion of a damped spring from offset <c>d0</c> (moving at <c>v0</c>) towards 0.</summary>
internal readonly struct SpringCurve
{
    private readonly double _w0, _zeta, _wd, _a, _b, _r1, _r2;

    public SpringCurve(Spring spring, double offset, double velocity)
    {
        _w0 = 2 * Math.PI / spring.Response;
        _zeta = spring.Damping;
        _wd = _r1 = _r2 = 0;
        if (_zeta < 1 - 1e-6)
        {
            // Under-damped: d = e^(-ζω0·t) · (a·cos ωd·t + b·sin ωd·t)
            _wd = _w0 * Math.Sqrt(1 - _zeta * _zeta);
            _a = offset;
            _b = (velocity + _zeta * _w0 * offset) / _wd;
        }
        else if (_zeta > 1 + 1e-6)
        {
            // Over-damped: d = a·e^(r1·t) + b·e^(r2·t)
            double s = _w0 * Math.Sqrt(_zeta * _zeta - 1);
            _r1 = -_zeta * _w0 + s;
            _r2 = -_zeta * _w0 - s;
            _a = (velocity - _r2 * offset) / (_r1 - _r2);
            _b = offset - _a;
        }
        else
        {
            // Critically damped: d = (a + b·t) · e^(-ω0·t)
            _a = offset;
            _b = velocity + _w0 * offset;
        }
    }

    public double Offset(double t)
    {
        if (_wd > 0) return Math.Exp(-_zeta * _w0 * t) * (_a * Math.Cos(_wd * t) + _b * Math.Sin(_wd * t));
        if (_r1 != 0) return _a * Math.Exp(_r1 * t) + _b * Math.Exp(_r2 * t);
        return (_a + _b * t) * Math.Exp(-_w0 * t);
    }

    public double Velocity(double t)
    {
        if (_wd > 0)
        {
            double k = _zeta * _w0;
            return Math.Exp(-k * t) * ((_b * _wd - k * _a) * Math.Cos(_wd * t) - (_a * _wd + k * _b) * Math.Sin(_wd * t));
        }
        if (_r1 != 0) return _a * _r1 * Math.Exp(_r1 * t) + _b * _r2 * Math.Exp(_r2 * t);
        return Math.Exp(-_w0 * t) * (_b - _w0 * (_a + _b * t));
    }

    /// <summary>Seconds until the spring stays within <paramref name="tolerance"/> of rest (and has about stopped).</summary>
    public double SettleTime(double tolerance)
    {
        const double step = 1 / 120.0, limit = 4.0;
        double last = 0;
        // An oscillating spring stays under its envelope: once that is small enough, nothing later can be bigger.
        double k = _zeta * _w0;
        double envelope = _wd > 0
            ? Math.Max(Math.Sqrt(_a * _a + _b * _b),
                Math.Sqrt(Math.Pow(_b * _wd - k * _a, 2) + Math.Pow(_a * _wd + k * _b, 2)) / _w0)
            : 0;
        for (double t = step; t <= limit; t += step)
        {
            if (_wd > 0 && envelope * Math.Exp(-k * t) < tolerance) break;
            if (Math.Abs(Offset(t)) > tolerance || Math.Abs(Velocity(t)) / _w0 > tolerance) last = t;
        }
        return Math.Min(limit, last + step);
    }
}

/// <summary>What a <see cref="SpringAnimation"/> puts out.</summary>
public enum SpringOutput
{
    /// <summary>The spring's position, From → To.</summary>
    Value,
    /// <summary><see cref="SpringAnimation.Rest"/> + <see cref="SpringAnimation.Gain"/> × the spring's speed (relative to
    /// <see cref="SpringAnimation.FullSpeed"/>, at most 1): for things that follow a motion, like squash and stretch.</summary>
    Speed,
}

/// <summary>
/// A physics spring as a WPF animation: its duration is however long the spring takes to settle, and it can start
/// moving (<see cref="Velocity"/>), which is how <see cref="Liquid.To"/> hands one animation's momentum to the next.
/// </summary>
public sealed class SpringAnimation : DoubleAnimationBase
{
    public static readonly DependencyProperty FromProperty = Register(nameof(From), 0.0);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), 0.0);
    public static readonly DependencyProperty VelocityProperty = Register(nameof(Velocity), 0.0);
    public static readonly DependencyProperty ResponseProperty = Register(nameof(Response), 0.4);
    public static readonly DependencyProperty DampingProperty = Register(nameof(Damping), 1.0);
    public static readonly DependencyProperty PrecisionProperty = Register(nameof(Precision), 0.001);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), double.NegativeInfinity);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), double.PositiveInfinity);
    public static readonly DependencyProperty RestProperty = Register(nameof(Rest), 1.0);
    public static readonly DependencyProperty GainProperty = Register(nameof(Gain), 0.0);
    public static readonly DependencyProperty FullSpeedProperty = Register(nameof(FullSpeed), 1000.0);
    public static readonly DependencyProperty OutputProperty = DependencyProperty.Register(nameof(Output), typeof(SpringOutput),
        typeof(SpringAnimation), new PropertyMetadata(SpringOutput.Value, (d, _) => ((SpringAnimation)d)._curve = null));

    private static DependencyProperty Register(string name, double fallback) =>
        DependencyProperty.Register(name, typeof(double), typeof(SpringAnimation),
            new PropertyMetadata(fallback, (d, _) => ((SpringAnimation)d)._curve = null));

    public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    /// <summary>How fast the value is already moving when the animation starts, in units per second.</summary>
    public double Velocity { get => (double)GetValue(VelocityProperty); set => SetValue(VelocityProperty, value); }
    public double Response { get => (double)GetValue(ResponseProperty); set => SetValue(ResponseProperty, value); }
    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
    /// <summary>How close counts as arrived, as a part of the distance (0.001 = a pixel in a thousand).</summary>
    public double Precision { get => (double)GetValue(PrecisionProperty); set => SetValue(PrecisionProperty, value); }
    /// <summary>Clamps the value, e.g. for a LayoutTransform that must stay invertible.</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public SpringOutput Output { get => (SpringOutput)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }
    public double Rest { get => (double)GetValue(RestProperty); set => SetValue(RestProperty, value); }
    public double Gain { get => (double)GetValue(GainProperty); set => SetValue(GainProperty, value); }
    public double FullSpeed { get => (double)GetValue(FullSpeedProperty); set => SetValue(FullSpeedProperty, value); }

    // Computed on first use; frozen copies (what clocks run) compute their own.
    private SpringCurve? _curve;
    internal SpringCurve Curve => _curve ??= new SpringCurve(new Spring(Response, Damping), From - To, Velocity);

    private double Tolerance
    {
        get
        {
            // A spring that only has momentum still travels about v / ω0.
            double travel = Math.Max(Math.Abs(From - To), Math.Abs(Velocity) * Response / (2 * Math.PI));
            return Math.Max(travel, 1e-3) * Precision;
        }
    }

    internal double ValueAt(double seconds)
    {
        if (Output == SpringOutput.Speed)
            return Rest + Gain * Math.Min(1, Math.Abs(Curve.Velocity(seconds)) / Math.Max(1e-6, FullSpeed));
        return Math.Max(Minimum, Math.Min(Maximum, To + Curve.Offset(seconds)));
    }

    protected override Duration GetNaturalDurationCore(Clock clock)
        => new(TimeSpan.FromSeconds(Curve.SettleTime(Tolerance)));

    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock.CurrentProgress is not double progress) return defaultOriginValue;
        if (progress >= 1)
            return Output == SpringOutput.Speed ? Rest : Math.Max(Minimum, Math.Min(Maximum, To));
        return ValueAt(animationClock.CurrentTime?.TotalSeconds ?? 0);
    }

    protected override Freezable CreateInstanceCore() => new SpringAnimation();
}

/// <summary>
/// "Liquid", the app's own motion (Settings → Animations → Motion → Liquid; also the window opening style of that
/// name). Everything moves on physical springs (<see cref="Spring"/>), and an animation that is interrupted hands its
/// speed to the next one (<see cref="To"/>), so nothing ever jumps or stops dead when it changes its mind, the way
/// UIKit and SwiftUI animate. Glass behaves like a drop of liquid: it blooms open wide first and then tall
/// (<see cref="Arrive"/>), stretches and clears while it moves fast (<see cref="Glide"/>), and a flick carries on
/// with the pointer's speed. <see cref="FluidMotion"/>, the animation behaviors, the notification banners and the
/// windows' popups hand their work to the motions here while <see cref="IsActive"/>.
/// </summary>
public static class Liquid
{
    /// <summary>The motion profile's tag (<see cref="ThemeSettings.AnimationPreset"/>) and the window opening style's.</summary>
    public const string Name = "Liquid";

    /// <summary>True while the Liquid profile drives the animations (and animations aren't off).</summary>
    public static bool IsActive => FluidMotion.Preset == Name && !FluidMotion.AnimationsDisabled;

    // ═══ Engine ═══

    private sealed class Track(AnimationClock clock, SpringAnimation animation)
    {
        public AnimationClock Clock { get; } = clock;
        public SpringAnimation Animation { get; } = animation;
    }

    // The spring currently driving each animated property, so the next animation on it can pick up its speed.
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Track>> Tracks = new();

    /// <summary>
    /// Springs <paramref name="dp"/> of <paramref name="target"/> to <paramref name="to"/>. Without
    /// <paramref name="from"/> it starts where the property is and as fast as it is moving (an interrupted Liquid
    /// animation carries on into the new one); with it, from rest unless <paramref name="velocity"/> says otherwise.
    /// <paramref name="done"/> runs once it has settled, unless another animation took the property over first.
    /// </summary>
    public static AnimationClock To(IAnimatable target, DependencyProperty dp, double to, Spring spring,
        double? from = null, double? velocity = null, double delayMs = 0, Action? done = null,
        double precision = 0.001, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        var obj = (DependencyObject)target;
        return Run(target, dp, new SpringAnimation
        {
            From = from ?? ValueOf(obj, dp),
            To = to,
            Velocity = velocity ?? (from == null ? VelocityOf(obj, dp) : 0),
            Response = spring.Response,
            Damping = spring.Damping,
            Precision = precision,
            Minimum = min,
            Maximum = max,
        }, delayMs, done);
    }

    /// <summary>
    /// Makes <paramref name="dp"/> follow the speed of a spring moving from <paramref name="from"/> to
    /// <paramref name="to"/>: <paramref name="rest"/> while still, up to <paramref name="rest"/> + <paramref name="gain"/>
    /// at <paramref name="fullSpeed"/> (units of the motion per second). Run it next to the motion's own <see cref="To"/>
    /// with the same spring and start speed, and it stretches, squashes or clears in step with it.
    /// </summary>
    public static AnimationClock Follow(IAnimatable target, DependencyProperty dp, double from, double to, double velocity,
        Spring spring, double rest, double gain, double fullSpeed, double delayMs = 0)
        => Run(target, dp, new SpringAnimation
        {
            Output = SpringOutput.Speed,
            From = from,
            To = to,
            Velocity = velocity,
            Response = spring.Response,
            Damping = spring.Damping,
            Rest = rest,
            Gain = gain,
            FullSpeed = fullSpeed,
        }, delayMs, null);

    /// <summary>How fast a Liquid spring is moving <paramref name="dp"/> right now (0 when none is).</summary>
    public static double VelocityOf(DependencyObject obj, DependencyProperty dp)
    {
        if (!Tracks.TryGetValue(obj, out var map) || !map.TryGetValue(dp, out var track)) return 0;
        var anim = track.Animation;
        if (anim.Output != SpringOutput.Value || track.Clock.CurrentState != ClockState.Active
            || track.Clock.CurrentTime is not TimeSpan time) return 0;
        double t = time.TotalSeconds;
        // Something else may have taken the property over since (BeginAnimation, a plain set): only trust a
        // spring that is still where it should be.
        if (Math.Abs(ValueOf(obj, dp) - anim.ValueAt(t)) > Math.Max(1e-3, Math.Abs(anim.From - anim.To) * 0.05)) return 0;
        return anim.Curve.Velocity(t);
    }

    /// <summary>True while a Liquid spring is still moving <paramref name="dp"/> (or waiting to start).</summary>
    public static bool IsMoving(DependencyObject obj, DependencyProperty dp)
        => Tracks.TryGetValue(obj, out var map) && map.TryGetValue(dp, out var track)
           && track.Clock.CurrentState != ClockState.Filling;

    private static double ValueOf(DependencyObject obj, DependencyProperty dp)
        => obj.GetValue(dp) is double d && !double.IsNaN(d) ? d : 0;

    private static AnimationClock Run(IAnimatable target, DependencyProperty dp, SpringAnimation anim, double delayMs, Action? done)
    {
        if (delayMs > 0) anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        anim.Freeze(); // the clock then runs this instance, so its curve is worked out once
        var clock = anim.CreateClock();
        var map = Tracks.GetOrCreateValue((DependencyObject)target);
        map.TryGetValue(dp, out var previous);
        var track = new Track(clock, anim);
        map[dp] = track;
        clock.Completed += (_, _) =>
        {
            if (!map.TryGetValue(dp, out var current) || !ReferenceEquals(current, track)) return; // taken over meanwhile
            map.Remove(dp);
            done?.Invoke();
        };
        target.ApplyAnimationClock(dp, clock, HandoffBehavior.SnapshotAndReplace);
        // The replaced spring is no longer applied; stop it ticking.
        previous?.Clock.Controller?.Stop();
        return clock;
    }

    // ═══ Shapes ═══

    /// <summary>
    /// The element's RenderTransform as scale + move, reusing the pair a previous animation left there (the
    /// same shape <see cref="FluidMotion"/> uses), so a new animation carries on from it.
    /// </summary>
    public static (ScaleTransform Scale, TranslateTransform Move) Rig(UIElement el)
    {
        if (el.RenderTransform is TransformGroup { IsFrozen: false } g && g.Children.Count == 2
            && g.Children[0] is ScaleTransform { IsFrozen: false } s && g.Children[1] is TranslateTransform { IsFrozen: false } t)
            return (s, t);
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(move);
        el.RenderTransform = group;
        return (scale, move);
    }

    /// <summary>
    /// Brings an element in from smaller, offset and transparent, the Liquid way: it blooms open wide first and then
    /// tall, like a drop spreading out. With <paramref name="resume"/> an element that is still on its way (in or out)
    /// turns around from where it is instead of starting over. <paramref name="done"/> runs once it has settled.
    /// </summary>
    public static void Arrive(UIElement el, ScaleTransform scale, TranslateTransform? move, double fromScaleX, double fromScaleY,
        double fromY, Spring spring, double delayMs = 0, double fromX = 0, bool resume = false, Action? done = null)
    {
        bool carry = resume && IsMoving(el, UIElement.OpacityProperty);
        double? From(double v) => carry ? null : v;
        if (!carry)
        {
            // Drop the previous run's held value, or it shows through the delay.
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
        }
        var height = spring.Slower(1.08);
        To(el, UIElement.OpacityProperty, 1, new Spring(Math.Min(0.24, spring.Response * 0.5), 1), from: From(0), delayMs: delayMs);
        To(scale, ScaleTransform.ScaleXProperty, 1, spring, from: From(fromScaleX), delayMs: delayMs);
        To(scale, ScaleTransform.ScaleYProperty, 1, height, from: From(fromScaleY), delayMs: delayMs + (carry ? 0 : 16),
            done: move == null ? done : null);
        if (move == null) return;
        To(move, TranslateTransform.XProperty, 0, spring, from: From(fromX), delayMs: delayMs);
        To(move, TranslateTransform.YProperty, 0, height, from: From(fromY), delayMs: delayMs, done: done, precision: 0.01);
    }

    /// <summary>Sends an element away (towards the given scale and offset, fading); <paramref name="done"/> runs once it is gone.</summary>
    public static void Leave(UIElement el, ScaleTransform scale, TranslateTransform? move, double toScaleX, double toScaleY,
        double toY, Action? done = null, double delayMs = 0, double toX = 0, Spring? spring = null)
    {
        var exit = spring ?? Spring.Exit;
        To(scale, ScaleTransform.ScaleXProperty, toScaleX, exit, delayMs: delayMs);
        To(scale, ScaleTransform.ScaleYProperty, toScaleY, exit, delayMs: delayMs);
        if (move != null)
        {
            To(move, TranslateTransform.XProperty, toX, exit, delayMs: delayMs);
            To(move, TranslateTransform.YProperty, toY, exit, delayMs: delayMs);
        }
        To(el, UIElement.OpacityProperty, 0, exit.Slower(0.75), delayMs: delayMs, done: done, precision: 0.02);
    }

    /// <summary>
    /// Slides a glass element (a selection pill) to <paramref name="toX"/>: it stretches along the way and turns
    /// clearer the faster it goes, then settles solid. <paramref name="velocity"/> is the pointer's speed when it was
    /// flicked; without it the pill carries on from its current speed. <paramref name="lifted"/>: it was being dragged
    /// (held larger and clear), so it settles back from that instead.
    /// </summary>
    public static void Glide(UIElement el, TranslateTransform move, ScaleTransform scale, double toX, double? velocity = null,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool lifted = false)
    {
        var spring = Spring.WithBounce(0.4, 0.2);
        double from = move.X;
        double v = velocity ?? VelocityOf(move, TranslateTransform.XProperty);
        To(move, TranslateTransform.XProperty, toX, spring, from: from, velocity: v, min: min, max: max);
        if (lifted)
        {
            To(scale, ScaleTransform.ScaleXProperty, 1, Spring.WithBounce(0.36, 0.3));
            To(scale, ScaleTransform.ScaleYProperty, 1, Spring.WithBounce(0.4, 0.3));
            To(el, UIElement.OpacityProperty, 1, new Spring(0.3, 1));
            return;
        }
        double fullSpeed = Math.Max(400, Math.Abs(toX - from) * 6);
        Follow(scale, ScaleTransform.ScaleXProperty, from, toX, v, spring, rest: 1, gain: 0.12, fullSpeed);
        Follow(scale, ScaleTransform.ScaleYProperty, from, toX, v, spring, rest: 1, gain: 0.05, fullSpeed);
        Follow(el, UIElement.OpacityProperty, from, toX, v, spring, rest: 1, gain: -0.55, fullSpeed);
    }

    // ═══ Windows ═══

    /// <summary>The window spring from Customize: Opening speed is its response, Spring its bounce.</summary>
    public static Spring WindowSpring(ThemeSettings theme, double speed = 1)
    {
        double response = (theme.WindowAnimDuration > 0 ? theme.WindowAnimDuration : 400) / 1000.0 * speed;
        return theme.WindowAnimEasing switch
        {
            "Apple Spring (Bouncy)" => Spring.WithBounce(response, 0.32),
            "Elastic Snap" => Spring.WithBounce(response * 0.8, 0.14),
            "Cubic Smooth" or "Linear Constant" => Spring.WithBounce(response, 0),
            _ => Spring.WithBounce(response, 0.2),
        };
    }

    /// <summary>
    /// Window opening: grows out of the control that opened it (origin <paramref name="ox"/>/<paramref name="oy"/>,
    /// start scale <paramref name="sx"/>/<paramref name="sy"/>, start offset <paramref name="tx"/>/<paramref name="ty"/>),
    /// spreading wide first and then pouring down to its full height, then settles with a soft wobble.
    /// </summary>
    public static void OpenWindow(FrameworkElement root, ScaleTransform scale, TranslateTransform translate,
        double ox, double oy, double sx, double sy, double tx, double ty, Spring spring, Action? done = null)
    {
        root.RenderTransformOrigin = new Point(ox, oy);
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 0;
        To(root, UIElement.OpacityProperty, 1, new Spring(spring.Response * 0.45, 1), from: 0);
        To(scale, ScaleTransform.ScaleXProperty, 1, spring, from: sx, done: done);
        To(scale, ScaleTransform.ScaleYProperty, 1, new Spring(spring.Response * 1.12, spring.Damping * 0.92), from: sy, delayMs: 18);
        // The way there is smooth; only the shape wobbles.
        To(translate, TranslateTransform.XProperty, 0, spring.Smooth, from: tx);
        To(translate, TranslateTransform.YProperty, 0, spring.Slower(1.06).Smooth, from: ty);
    }

    /// <summary>Window closing: pours back into the control it came from (<paramref name="sx"/> 0 = none known: it just shrinks).</summary>
    public static void CloseWindow(FrameworkElement root, ScaleTransform scale, TranslateTransform translate,
        double ox, double oy, double sx, double sy, double tx, double ty, Spring spring, Action done)
    {
        var exit = new Spring(spring.Response * 0.62, 1);
        if (sx > 0 && sy > 0)
        {
            root.RenderTransformOrigin = new Point(ox, oy);
            To(scale, ScaleTransform.ScaleXProperty, sx, exit.Slower(1.1));
            To(scale, ScaleTransform.ScaleYProperty, sy, exit);
            To(translate, TranslateTransform.XProperty, tx, exit);
            To(translate, TranslateTransform.YProperty, ty, exit);
        }
        else
        {
            To(scale, ScaleTransform.ScaleXProperty, 0.9, exit);
            To(scale, ScaleTransform.ScaleYProperty, 0.88, exit);
        }
        To(root, UIElement.OpacityProperty, 0, exit.Slower(0.8), delayMs: exit.Response * 250, done: done, precision: 0.02);
    }

    // ═══ Panels and pages ═══

    /// <summary>Children rise into place one after another (capped, so long lists don't drag on).</summary>
    public static void StaggerIn(Panel panel, int baseDelayMs, int stepMs)
    {
        int i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            fe.RenderTransformOrigin = new Point(0.5, 0);
            var (scale, move) = Rig(fe);
            int n = Math.Min(i, 10);
            Arrive(fe, scale, move, 0.97, 0.95, 14 + n * 1.5, Spring.Bouncy, delayMs: baseDelayMs + n * stepMs * 0.8);
            i++;
        }
    }

    public static void ShowPanel(Border panel, double slideFromX)
    {
        // Still on its way out: turn around where it is.
        bool resume = panel.Visibility == Visibility.Visible && IsMoving(panel, UIElement.OpacityProperty);
        panel.Visibility = Visibility.Visible;
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var (scale, move) = Rig(panel);
        Arrive(panel, scale, move, 0.95, 0.92, 14, Spring.Bouncy, fromX: slideFromX, resume: resume);
        if (!resume && FluidMotion.ContentRows(panel) is StackPanel sp) StaggerIn(sp, 40, 35);
    }

    public static void HidePanel(Border panel, double slideX, Action? onCompleted)
    {
        panel.RenderTransformOrigin = new Point(0.5, 0);
        var (scale, move) = Rig(panel);
        Leave(panel, scale, move, 0.97, 0.95, 6, toX: slideX, done: () =>
        {
            panel.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        });
    }

    /// <summary>
    /// Mode switch: the old panel steps back and fades, the new one springs in from the side it is on; its rows
    /// follow a beat later. Switching back mid-way turns both around where they are.
    /// </summary>
    public static void Crossfade(Border hidePanel, Border showPanel, double direction)
    {
        if (hidePanel.Visibility == Visibility.Visible && hidePanel.Opacity > 0.02)
        {
            hidePanel.RenderTransformOrigin = new Point(0.5, 0.5);
            var (hs, hm) = Rig(hidePanel);
            Leave(hidePanel, hs, hm, 0.96, 0.96, 0, toX: -direction * 28, done: () => hidePanel.Visibility = Visibility.Collapsed);
        }
        else
        {
            hidePanel.BeginAnimation(UIElement.OpacityProperty, null);
            hidePanel.Opacity = 0;
            hidePanel.Visibility = Visibility.Collapsed;
        }

        bool resume = showPanel.Visibility == Visibility.Visible && showPanel.Opacity > 0.02;
        showPanel.Visibility = Visibility.Visible;
        showPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        var (ss, sm) = Rig(showPanel);
        Arrive(showPanel, ss, sm, 0.96, 0.95, 6, Spring.Bouncy, delayMs: resume ? 0 : 60, fromX: direction * 40, resume: resume);

        if (resume || FluidMotion.ContentRows(showPanel) is not Panel rows) return;
        int i = 0;
        foreach (UIElement child in rows.Children)
        {
            if (child is not FrameworkElement fe) continue;
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            var (cs, cm) = Rig(fe);
            Arrive(fe, cs, cm, 0.96, 0.96, 10, Spring.Snappy.Slower(1.3), delayMs: 70 + Math.Min(i, 8) * 30, fromX: direction * 14);
            i++;
        }
    }

    /// <summary>iOS push/pop on one smooth spring; the pages cross-fade quicker than they move (they have no background).</summary>
    public static void SlidePage(FrameworkElement outgoing, FrameworkElement incoming, double width, bool forward, Action? onCompleted)
    {
        double inFrom = forward ? width : -width * 0.3;
        double outTo = forward ? -width * 0.3 : width;

        var inMove = new TranslateTransform(inFrom, 0);
        incoming.RenderTransform = inMove;
        To(inMove, TranslateTransform.XProperty, 0, Spring.Glide, from: inFrom);
        To(incoming, UIElement.OpacityProperty, 1, new Spring(0.26, 1), from: 0);

        var outMove = new TranslateTransform();
        outgoing.RenderTransform = outMove;
        To(outMove, TranslateTransform.XProperty, outTo, Spring.Glide, from: 0, done: onCompleted, precision: 0.004);
        To(outgoing, UIElement.OpacityProperty, 0, new Spring(0.2, 1), from: 1);
    }

    /// <summary>Progress glides: every update carries on with the bar's current speed, so it never stutters.</summary>
    public static void ProgressWidth(FrameworkElement fill, double targetWidth)
    {
        double? from = double.IsNaN(fill.Width) ? fill.ActualWidth : null;
        To(fill, FrameworkElement.WidthProperty, targetWidth, new Spring(0.36, 1), from: from, min: 0);
    }

    public static void PhaseOut(FrameworkElement[] rows, Action onComplete)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            var (scale, move) = Rig(rows[i]);
            Leave(rows[i], scale, move, 0.97, 0.97, 5, delayMs: i * 22, done: i == rows.Length - 1 ? onComplete : null);
        }
    }

    public static void PhaseIn(FrameworkElement[] rows, Action? onComplete)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            var (scale, move) = Rig(rows[i]);
            Arrive(rows[i], scale, move, 0.97, 0.96, 14, Spring.Bouncy, delayMs: i * 40, done: i == rows.Length - 1 ? onComplete : null);
        }
    }

    public static void Expand(FrameworkElement element, double targetMaxHeight)
    {
        bool resume = element.Visibility == Visibility.Visible && IsMoving(element, FrameworkElement.MaxHeightProperty);
        if (!resume)
        {
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.MaxHeight = 0;
            element.Opacity = 0;
        }
        element.Visibility = Visibility.Visible;
        To(element, FrameworkElement.MaxHeightProperty, targetMaxHeight, Spring.WithBounce(0.4, 0.1), from: resume ? null : 0, min: 0);
        To(element, UIElement.OpacityProperty, 1, Spring.Fade);
    }

    public static void Collapse(FrameworkElement element, double currentHeight, Action onCompleted)
    {
        double from = Math.Min(currentHeight, element.ActualHeight > 0 ? element.ActualHeight : currentHeight);
        To(element, FrameworkElement.MaxHeightProperty, 0, Spring.Exit, from: IsMoving(element, FrameworkElement.MaxHeightProperty) ? null : from,
            min: 0, precision: 0.01, done: () =>
            {
                element.Visibility = Visibility.Collapsed;
                element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                element.MaxHeight = currentHeight;
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                onCompleted();
            });
        To(element, UIElement.OpacityProperty, 0, Spring.Exit.Slower(0.8));
    }

    // ═══ Menus ═══
    // Header popups and drop-downs keep the help bubble's opening from before Liquid (the windows' AnimatePopupIn,
    // DropdownAnimBehavior.PlayBubble).

    /// <summary>A context menu or submenu blooms out of the corner it opens from: wide first, then down.</summary>
    public static void OpenMenu(FrameworkElement surface)
    {
        surface.RenderTransformOrigin = new Point(0, 0);
        var scale = new ScaleTransform(0.8, 0.6);
        surface.RenderTransform = scale;
        To(surface, UIElement.OpacityProperty, 1, new Spring(0.14, 1), from: 0);
        To(scale, ScaleTransform.ScaleXProperty, 1, Spring.WithBounce(0.34, 0.16), from: 0.8);
        To(scale, ScaleTransform.ScaleYProperty, 1, Spring.WithBounce(0.42, 0.26), from: 0.6, delayMs: 12);
    }

    // ═══ Pointer ═══

    /// <summary>Hover lifts, press sinks, release pops back with a small bounce; each carries on from the last.</summary>
    public static void Hover(ScaleTransform scale, double target) => ScaleTo(scale, target, Spring.WithBounce(0.3, 0.2));
    public static void Unhover(ScaleTransform scale) => ScaleTo(scale, 1, Spring.WithBounce(0.36, 0));
    public static void Press(ScaleTransform scale) => ScaleTo(scale, 0.95, Spring.Press);
    public static void Release(ScaleTransform scale, double target) => ScaleTo(scale, target, Spring.WithBounce(0.34, 0.35));

    private static void ScaleTo(ScaleTransform scale, double target, Spring spring)
    {
        To(scale, ScaleTransform.ScaleXProperty, target, spring);
        To(scale, ScaleTransform.ScaleYProperty, target, spring);
    }
}
