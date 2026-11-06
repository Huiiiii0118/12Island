namespace TwelveIsland.Core;

// Resizing with the resize shortcut (HotkeyKind.Resize): each press of an arrow key moves the size the panel is heading for by
// one step, within the same limits as dragging an edge. Left and Right change the width (of the open page, or of the collapsed
// notch, capsule or line, as dragging an end does), Up and Down the open page's height. Holding a key repeats it.
public static class KeyboardResize
{
    // One step, in DIPs: the width changes by it as a whole (both sides move, as when dragging), the height at the bottom.
    public const double Step = 24;

    public enum Direction { Narrower, Wider, Shorter, Taller }

    // The arrow key's place in HotkeyText.ArrowKeys is its direction.
    public static Direction Of(int arrow) => (Direction)Math.Clamp(arrow, 0, 3);

    public static bool Horizontal(Direction direction) => direction is Direction.Narrower or Direction.Wider;

    // The size one step on from where the panel is heading, kept within its limits and in whole DIPs. A size already past a
    // limit (a smaller monitor) comes back inside it.
    public static double Next(double target, Direction direction, double min, double max)
    {
        double sign = direction is Direction.Wider or Direction.Taller ? 1 : -1;
        return Math.Round(Math.Clamp(target + sign * Step, min, Math.Max(min, max)));
    }
}
