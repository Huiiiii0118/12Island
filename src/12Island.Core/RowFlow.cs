namespace TwelveIsland.Core;

// How much the collapsed row follows the room while its width is being changed continuously (CompactSpacing.ShareFlowing), 0 to 1:
// 0 is the rest, whole items in a window; 1 is the windows taking the room as it comes, an item cut at the edge. It is 1 while
//   - the surface is dragged (the pointer, or the resize shortcut, until the spring has come to rest), or
//   - the settings change the width again and again (a slider dragged, a stepper held): the first change is a layout like any
//     other, one that follows within Hold seconds of the one before is a motion, and the flow is held until the changes have been
//     quiet for Hold seconds;
// it rises over In seconds when that begins, so the row does not jump to it, and falls over Out seconds when it ends. Without
// animations it is at its target at once. Eased is the flow as the row blends by, smoothstepped so the windows start and stop gently.
public sealed class RowFlow
{
    public const double In = .3, Out = .25, Hold = .25;

    private double _at, _changedAt = double.NegativeInfinity;
    private bool _holding;

    public double Value { get; private set; }
    public double Eased => Value * Value * (3 - 2 * Value);
    public bool Holding => _holding;

    // The flow wants to be at this, with the surface dragged or not.
    public double Target(bool dragging) => dragging || _holding ? 1 : 0;
    public bool Moving(bool dragging) => Value != Target(dragging);

    // The window the row shows at this flow: from the window of the rest (never wider than the room gives, flowing) to the one the room
    // gives as it comes. The rest is the window the row had before the flow began while the flow rises, so a step of the whole items
    // as the width crosses it never shows through; while it falls, the width no longer moves and it is the whole items' own.
    public static double Window(double rest, double flowing, double eased)
    {
        double from = Math.Min(rest, flowing);
        return from + (flowing - from) * eased;
    }

    // Starts counting time from now (the flow was idle, so the time since it last moved is nothing).
    public void Restart(double now) => _at = now;

    // Moves the flow toward its target by the time since it was last moved.
    public void Advance(double now, bool dragging, bool animate)
    {
        double dt = Math.Clamp(now - _at, 0, .1);
        _at = now;
        if (_holding && now - _changedAt >= Hold) _holding = false;
        double target = Target(dragging);
        if (!animate) Value = target;
        else if (target > Value) Value = Math.Min(target, Value + dt / In);
        else if (target < Value) Value = Math.Max(target, Value - dt / Out);
    }

    // The width was changed from the settings. Returns whether the flow is held from now on.
    public bool NoteChange(double now)
    {
        bool again = now - _changedAt < Hold;
        _changedAt = now;
        if (again) _holding = true;
        return _holding;
    }
}
