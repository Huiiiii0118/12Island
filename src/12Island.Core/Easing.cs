using System.Diagnostics;

namespace TwelveIsland.Core;

// A value that eases to each new target over a short move, ftop's columns' "follow" made cheap: a move takes a little under half
// a second and the panel rests between readings. The move is a cubic Hermite curve that starts gently and lands softly; a new
// target that arrives mid-move keeps the speed the value already has toward it, so the motion bends instead of jolting. The
// starting speed stays within what never passes the target, so a bar never overshoots and comes back.
public struct Eased
{
    // Hover: a hover's feedback arriving; HoverOut: leaving, a little quicker (seven tenths), so what the pointer left never trails it.
    public const double Seconds = .42, Hover = .16, HoverOut = Hover * .7;
    // A move's starting speed as a multiple of its average: 0 is a smoothstep, 3 an ease-out cubic, the most that never overshoots.
    // A change too small to see (a third of a percent; the values are shares, slots or opacities) is taken at once, so an idle
    // core does not keep its graph redrawing.
    private const double Launch = 1.5, Fastest = 3, Unseen = .0035;
    private double _from, _to, _start, _speed, _seconds;
    public readonly double Target => _to;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public Eased(double value, double seconds = Seconds) { _from = _to = value; _start = _speed = 0; _seconds = seconds; }

    private readonly double Duration => _seconds > 0 ? _seconds : Seconds;

    // Returns whether the value moves.
    public bool Set(double target, bool motion) => Set(target, motion, Now);
    public bool Set(double target, bool motion, double now) => Set(target, motion, now, _seconds);
    // A hover's feedback (0 to 1): in over Hover, out over HoverOut.
    public bool Fade(bool on, bool motion) => Set(on ? 1 : 0, motion, Now, on ? Hover : HoverOut);
    // The move takes seconds from here on; one under way goes on from where it is, at its speed.
    public bool Set(double target, bool motion, double now, double seconds)
    {
        if (!double.IsFinite(target)) target = 0;
        bool moving = Moving(now);
        if (Math.Abs(target - _to) < 1e-6 && !moving) return false;
        if (!motion) { _from = _to = target; _start = _speed = 0; return false; }
        if (Math.Abs(target - _to) < 1e-6) return true;
        double current = At(now), distance = target - current, speed = moving ? Speed(now) : 0;
        _seconds = seconds;
        double d = Duration;
        if (Math.Abs(distance) < Unseen && !moving) { _from = _to = target; _start = _speed = 0; return false; }
        double carried = moving ? speed * d / distance : 0;
        _speed = Math.Clamp(Math.Max(Launch, carried), 0, Fastest) * distance / d;
        _from = current; _to = target; _start = now;
        return true;
    }

    public readonly double At(double now)
    {
        if (_start <= 0) return _to;
        double s = (now - _start) / Duration;
        if (s >= 1) return _to;
        s = Math.Max(0, s);
        double s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * _from + (s3 - 2 * s2 + s) * Duration * _speed + (3 * s2 - 2 * s3) * _to;
    }
    public readonly double Speed(double now)
    {
        if (!Moving(now)) return 0;
        double s = Math.Max(0, (now - _start) / Duration), s2 = s * s, d = Duration;
        return (6 * s2 - 6 * s) * _from / d + (3 * s2 - 4 * s + 1) * _speed + (6 * s - 6 * s2) * _to / d;
    }
    public readonly bool Moving(double now) => _start > 0 && now - _start < Duration;
}

// A spring that trails its target, for what follows the pointer: slightly under-damped, so a fast drag that stops overshoots a
// little and settles back, and slow enough (a period of about 0.4 s) that the motion reads as weight rather than lag.
public struct Spring
{
    private const double Frequency = 16, DampingRatio = .7;
    public double Value, Velocity, Target;

    public Spring(double value) { Value = Target = value; Velocity = 0; }

    // Advances by dt seconds, in steps short enough to stay stable after a stalled frame. Returns whether it still moves.
    public bool Step(double dt)
    {
        dt = Math.Clamp(dt, 0, .1);
        while (dt > 0)
        {
            double h = Math.Min(dt, 1 / 240.0);
            Velocity += (-Frequency * Frequency * (Value - Target) - 2 * DampingRatio * Frequency * Velocity) * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Math.Abs(Value - Target) < .25 && Math.Abs(Velocity) < 4) { Value = Target; Velocity = 0; return false; }
        return true;
    }

    // Near a limit the target gives way more and more, as a stretched band does, and never passes it.
    public static double Resist(double raw, double min, double max, double zone = 36)
    {
        if (max <= min) return min;
        zone = Math.Max(1e-3, Math.Min(zone, (max - min) / 3));
        if (raw > max - zone) return max - zone + zone * (1 - Math.Exp(-(raw - (max - zone)) / zone));
        if (raw < min + zone) return min + zone - zone * (1 - Math.Exp(-(min + zone - raw) / zone));
        return raw;
    }
}

// The curves behind the panel's timed animations (TwelveIsland.Soft), as plain functions of the elapsed fraction of a move.
public static class Curves
{
    // A CSS cubic-bezier(x1, y1, x2, y2): the progress at the elapsed fraction x. x(t) is monotonic while x1 and x2 lie in [0, 1].
    public static double Bezier(double x1, double y1, double x2, double y2, double x)
    {
        x = Math.Clamp(x, 0, 1);
        double t = x;
        // Newton steps on the x polynomial, then bisection for what they leave (a flat slope near an end).
        for (int i = 0; i < 8; i++)
        {
            double slope = Slope(t, x1, x2);
            if (Math.Abs(slope) < 1e-6) break;
            t = Math.Clamp(t - (Sample(t, x1, x2) - x) / slope, 0, 1);
        }
        if (Math.Abs(Sample(t, x1, x2) - x) > 1e-5)
        {
            double low = 0, high = 1;
            for (int i = 0; i < 30; i++) { t = (low + high) / 2; if (Sample(t, x1, x2) < x) low = t; else high = t; }
        }
        return Sample(t, y1, y2);
    }
    private static double Sample(double t, double a1, double a2) => ((1 - 3 * a2 + 3 * a1) * t + (3 * a2 - 6 * a1)) * t * t + 3 * a1 * t;
    private static double Slope(double t, double a1, double a2) => 3 * (1 - 3 * a2 + 3 * a1) * t * t + 2 * (3 * a2 - 6 * a1) * t + 3 * a1;

    // The system monitor's own move (Eased) from rest to rest: it leaves at one and a half times its average speed and lands
    // with no speed left, never passing its end. The panel's content, its selections and its hover feedback follow it, so the
    // whole panel moves as the monitor page does.
    public static double Follow(double x) => x <= 0 ? 0 : x >= 1 ? 1 : 1.5 * x - .5 * x * x * x;

    // A spring thrown toward its target, fitted into the move: it leaves at speed (half way there after a tenth of the move),
    // passes the target by about a third of a percent of the move a little after its two thirds, and has settled by the end.
    // This is the island's own gesture: a quick attack and a soft landing with a slight give that is felt more than seen, for
    // what opens or appears. Thrown with the speed that cancels the sine term, the spring's path is 1 - e^(-decay x) cos(omega x).
    // The damping is what sets the give: at .85 the path passed its target by 1.7 percent, which at the bottom edge of a
    // tall panel was a visible bounce of several DIPs; at .91 it passes by .32 percent (about one DIP on the tallest move, half a
    // DIP on a usual one) and the way back is under a third as fast, so the landing no longer reads as a rebound. The attack stays
    // (half way at .105 of the move instead of .100, nine tenths at .31 instead of .27).
    public const double BounceDamping = .91;
    // The envelope e^(-decay x) is down to 0.2 percent at the end.
    private const double BounceDecay = 6.2;
    private static double BounceOmega => BounceDecay / BounceDamping * Math.Sqrt(1 - BounceDamping * BounceDamping);
    private static double BouncePath(double x) => 1 - Math.Exp(-BounceDecay * x) * Math.Cos(BounceOmega * x);
    // The path ends at 1.002, not 1: it is divided by that end so that it lands exactly where it is going and the surface does not
    // jump a fraction of a DIP on the last frame (as SpringEase does for the glass's spring).
    private static readonly double BounceEnd = BouncePath(1);
    public static double Bounce(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return BouncePath(x) / BounceEnd;
    }
    // How far Bounce passes its target, as a fraction of the move.
    public static double BounceOvershoot
    {
        get
        {
            // The peak, where the path's slope e^(-decay x) (decay cos + omega sin) is zero.
            // (The end division moves the height of the peak, not its place.)
            double x = (Math.PI - Math.Atan(BounceDecay / BounceOmega)) / BounceOmega;
            return Bounce(x) - 1;
        }
    }
}
