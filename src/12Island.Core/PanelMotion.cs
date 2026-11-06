namespace TwelveIsland.Core;

// How long the panel's opening and closing take, and what a move turned back part way does (TwelveIsland.MainWindow.SetExpanded).
// The lengths are milliseconds; 0 means no animation.
public static class PanelMotion
{
    // motion-review C1: the panel closes in three quarters of the time it takes to open, as it draws back into the dot on
    // powering off. C4: a move the keyboard asked for (a hotkey, Escape) takes 0.6 of it and lands without the opening
    // spring's give.
    public const double CloseFactor = .75, KeyFactor = .6;
    public static int Length(int length, bool closing, bool keyboard)
        => length <= 0 ? 0 : (int)Math.Round(length * (keyboard ? KeyFactor : closing ? CloseFactor : 1));

    // The open panel taking a new size in place (another page, a changed setting) always takes this long, the default opening
    // length: the setting is for opening and closing, and a page switch slowed down with it felt like a stall.
    public const int Reshape = 360;

    // Where the closing surface starts, as a fraction of the move: a little after the content has begun to leave; at once
    // when the closing turns back an opening still under way (motion-review F1), rather than stopping where the opening was
    // and starting back from rest after a pause.
    public const double CloseSurfaceDelay = .12;
    public static double CloseSurfaceStart(bool turned) => turned ? 0 : CloseSurfaceDelay;

    // motion-review F2: a move turned back part way only has the way already covered to go back: its length is that share of a
    // whole move (the distance left over the distance between the two ends), never under RedirectMin of it, so a turn just
    // after the start still reads as a move. Each triple is (now, the target being left, the new target); the largest share of
    // the sizes that change counts. Nothing changing at all takes the whole move.
    public const double RedirectMin = .4;
    public static double RemainingShare(params (double Now, double Left, double Target)[] sizes)
    {
        double share = -1;
        foreach (var (now, left, target) in sizes)
        {
            double whole = Math.Abs(left - target);
            if (!double.IsFinite(whole) || !double.IsFinite(now) || whole < 1) continue;
            share = Math.Max(share, Math.Abs(now - target) / whole);
        }
        return share < 0 ? 1 : Math.Clamp(share, RedirectMin, 1);
    }
}

// The panel's last opening or closing, so that the next one can tell whether it turns that one back while it is still under
// way (motion-review F1, F2). Only those moves count: the panel laid out again while it rests open or closed (a page's new
// size, a window coming to the front, a reading) never makes the next closing look turned back, which cut a closing after
// such a lay-out short; and only a move the other way turns one back. Times are milliseconds on one clock
// (Environment.TickCount64).
public sealed class MoveTurn
{
    private long _ends;
    private bool _opening;
    private double _width = double.NaN, _height = double.NaN;

    // An opening (or a closing) of the given length toward the given size starts at now.
    public void Started(long now, double length, bool opening, double width, double height)
    {
        _ends = length > 0 ? now + (long)Math.Ceiling(length) : 0;
        _opening = opening; _width = width; _height = height;
    }

    // Whether an opening (or a closing) starting at now turns back the last move: only one the other way, still under way.
    public bool Turns(long now, bool opening) => now < _ends && opening != _opening;

    // The share of a whole move the turning move takes (PanelMotion.RemainingShare), from the size the surface has now to the
    // new target; 1 when it turns nothing back.
    public double Share(long now, bool opening, double width, double height, double toWidth, double toHeight)
        => Turns(now, opening) ? PanelMotion.RemainingShare((width, _width, toWidth), (height, _height, toHeight)) : 1;

    // A lay-out the same way as the move still under way (the closing surface given a new collapsed width by its content)
    // carries that move on rather than starting a whole one: from where the surface is, at once, over what is left of the move
    // and never under RedirectMin of a whole one (length), so it reads as one move and not as a closing that stops and starts
    // over. 0 when no such move is under way: the lay-out is then a move of its own.
    public int Carried(long now, bool opening, int length)
        => length > 0 && now < _ends && opening == _opening ? (int)Math.Max(_ends - now, Math.Round(length * PanelMotion.RedirectMin)) : 0;
}
