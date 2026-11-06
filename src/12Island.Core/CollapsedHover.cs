namespace TwelveIsland.Core;

// A rectangle in device-independent pixels, relative to the top-left corner of the monitor.
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

// Where the collapsed surface senses the pointer. Every form opens when the pointer reaches the edge above it, as long as the
// surface stays close to that edge: the strip between them is a target the pointer cannot overshoot. The notch and the line
// bridge to the screen edge. The capsule bridges to the top of the work area, so the strip never covers a taskbar at the top;
// moved further down, it gives the strip up, since it would then cover other applications' title bars and tabs, and only the
// capsule itself opens the panel. The thin indicator bar senses a slightly larger box around it so it stays easy to reach.
// workTop: the top of the work area in DIPs, relative to the monitor (0 unless the taskbar is at the top).
public static class CollapsedHover
{
    // The largest gap between the top of the work area and a capsule that is still bridged. Not a setting.
    public const double BridgeMaxGap = 20;
    public const double LinePadX = 8, LinePadBelow = 2;

    public static bool HasBridge(CompactStyle style, DipRect surface, double workTop) => surface.Y - BridgeTop(style, workTop) <= BridgeMaxGap || style != CompactStyle.Capsule;

    // The sensing area of a collapsed surface, in the same coordinates as the surface.
    public static DipRect Zone(CompactStyle style, DipRect surface, double workTop)
    {
        double padX = style == CompactStyle.Line ? LinePadX : 0, padBelow = style == CompactStyle.Line ? LinePadBelow : 0;
        double top = HasBridge(style, surface, workTop) ? Math.Min(BridgeTop(style, workTop), surface.Y) : surface.Y;
        return new(surface.X - padX, top, surface.Width + padX * 2, surface.Bottom + padBelow - top);
    }

    // The edge a bridge reaches up to: the work area's top for the capsule, the screen edge for the others.
    private static double BridgeTop(CompactStyle style, double workTop)
        => style == CompactStyle.Capsule && double.IsFinite(workTop) ? Math.Max(0, workTop) : 0;
}
