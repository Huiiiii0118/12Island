namespace TwelveIsland.Core;

// The cycles of the collapsed row's scrolling regions as plain functions of the time within them (seconds from the cycle's
// start). The window waits out their rests on a timer and runs an animation clock only while something moves: a cycle that
// repeated forever on one clock kept WPF's animation clock ticking at 60 frames a second through every rest too, for as long
// as a song played or the rings overflowed, and so did its discrete keyframes with animations off. The values are those of
// the keyframes they replace (IdleCostTests compares them point by point).

// A stretch of a cycle: whether its values move in it, and when it starts and ends.
public readonly record struct CycleStretch(bool Moving, double Start, double End);

// What a running cycle does at a time within it: the move to hand to an animation clock now, if any (Into is how far into it
// the cycle already is; negative, it starts that much later), and how long to wait before looking again.
public readonly record struct CycleTurn(CycleStretch? Move, double Into, double Wait)
{
    // A move is handed to the clock this long before it starts, so a timer that wakes a little late never delays it; the clock
    // then starts it on time.
    public const double Lead = .05;

    // now is the stretch at the time, next the one that follows it (taken at now.End, so both count from the same start).
    public static CycleTurn Plan(CycleStretch now, CycleStretch next, double at, double lead = Lead)
    {
        if (now.Moving) return new(now, at - now.Start, Math.Max(0, now.End - at));
        double left = Math.Max(0, now.End - at);
        if (!next.Moving) return new(null, 0, left);
        if (left <= lead) return new(next, at - next.Start, Math.Max(0, next.End - at));
        return new(null, 0, left - lead);
    }

    // Where the cycle stands, from the time the window counts (planned) and the clock running a move: what the clock shows is
    // what is on screen, so while it runs it is the truth (the move's start plus the clock's time), and once it has finished the
    // cycle is at least at the move's end. A cycle already past the move (wrapped to its start) or a clock not yet begun leaves
    // the planned time.
    public static double Where(double planned, CycleStretch move, MoveClock clock, double clockSeconds) => clock switch
    {
        MoveClock.Active when double.IsFinite(clockSeconds) => move.Start + Math.Clamp(clockSeconds, 0, move.End - move.Start),
        MoveClock.Filling when planned >= move.Start && planned < move.End => move.End,
        _ => planned
    };

    // Whether a running move is left to finish on its clock: the timer woke while the clock still runs (it counts a little
    // behind the window's time), so the move is not cut short; the turn waits for its end instead.
    public static bool Holds(MoveClock clock, double at, CycleStretch move) => clock == MoveClock.Active && at < move.End;
}

// The state of the clock that runs a move (WPF's ClockState, with Waiting for one whose BeginTime has not come).
public enum MoveClock { None, Waiting, Active, Filling }

// The quota rings and the system readings (GlideCarousel): rest at the start for StartHold, glide by Travel on the (.4, 0, .6, 1)
// spline at Speed DIP a second, rest at the end for EndHold, then fade out over Fade, jump back to the start and fade in over
// Fade. Without animations (Discrete) the strip jumps to the end when the glide would have ended and back when the last rest
// does, with no fade.
public readonly record struct GlideCycle(double Travel, bool Discrete = false)
{
    public const double StartHold = 2.5, EndHold = 2, Speed = 12, Fade = .25;
    public const double CurveX1 = .4, CurveY1 = 0, CurveX2 = .6, CurveY2 = 1;

    public double GlideEnd => StartHold + Travel / Speed;
    public double ResetStart => GlideEnd + EndHold;
    public double Period => ResetStart + (Discrete ? 0 : 2 * Fade);

    // A time in seconds from any start, within one cycle.
    public double Wrap(double seconds) => Period > 0 && double.IsFinite(seconds) ? (seconds % Period + Period) % Period : 0;

    // The strip's shift (0 at the start, -Travel at the end) and its opacity at a time within the cycle.
    public double Shift(double at) => at < StartHold ? 0
        : at < GlideEnd ? Discrete ? 0 : -Travel * Curves.Bezier(CurveX1, CurveY1, CurveX2, CurveY2, (at - StartHold) / (GlideEnd - StartHold))
        : at < ResetStart + (Discrete ? 0 : Fade) ? -Travel : 0;
    public double Opacity(double at) => Discrete || at < ResetStart ? 1
        : at < ResetStart + Fade ? 1 - (at - ResetStart) / Fade : Math.Min(1, (at - ResetStart - Fade) / Fade);

    public CycleStretch StretchAt(double at)
    {
        if (Discrete) return at < GlideEnd ? new(false, 0, GlideEnd) : new(false, GlideEnd, Period);
        return at < StartHold ? new(false, 0, StartHold) : at < GlideEnd ? new(true, StartHold, GlideEnd)
            : at < ResetStart ? new(false, GlideEnd, ResetStart) : new(true, ResetStart, Period);
    }
}

// The scrolling title or lyric (MusicMarquee): rest for Rest, glide by Overflow at Speed DIP a second, rest again, then start
// over at once. Each edge fade grows or shrinks over the first or last EdgeWidth of travel (to EdgeStop, the fade's share of the
// region), so it follows how much of the text is hidden on that side.
// The collapsed row's title scrolls Laps whole cycles and then rests at its start (Done, Played): a song plays for minutes,
// and the glide's frames were most of what the row cost while it played. A new title, or the pointer coming over the island,
// lets it scroll again.
public readonly record struct MarqueeCycle(double Overflow, double Speed, double EdgeWidth, double EdgeStop)
{
    public const double Rest = 1.5;
    public const int Laps = 3;

    // Whether the cycle has run its laps after played seconds in all (not wrapped), and where it then stands within one cycle:
    // at the start of its first rest once the laps are done.
    public bool Done(double played) => Period > 0 && double.IsFinite(played) && played >= Laps * Period - 1e-9;
    public double Played(double played) => Done(played) ? 0 : Wrap(played);

    public double Travel => Overflow / Speed;
    public double Ramp => Math.Min(Travel, EdgeWidth / Speed);
    public double GlideEnd => Rest + Travel;
    public double Period => 2 * Rest + Travel;

    public double Wrap(double seconds) => Period > 0 && double.IsFinite(seconds) ? (seconds % Period + Period) % Period : 0;

    // The text's shift, the left and right fade stops and the fade brush's counter-shift at a time within the cycle.
    public double Shift(double at) => at < Rest ? 0 : at < GlideEnd ? -Overflow * ((at - Rest) / Travel) : -Overflow;
    public double Unshift(double at) => at < Rest ? 0 : at < GlideEnd ? Overflow * ((at - Rest) / Travel) : Overflow;
    public double Left(double at) => at < Rest ? 0 : at < Rest + Ramp ? EdgeStop * ((at - Rest) / Ramp) : EdgeStop;
    public double Right(double at) => at < GlideEnd - Ramp ? 1 - EdgeStop : at < GlideEnd ? 1 - EdgeStop + EdgeStop * ((at - (GlideEnd - Ramp)) / Ramp) : 1;

    public CycleStretch StretchAt(double at) => at < Rest ? new(false, 0, Rest) : at < GlideEnd ? new(true, Rest, GlideEnd) : new(false, GlideEnd, Period);
}
