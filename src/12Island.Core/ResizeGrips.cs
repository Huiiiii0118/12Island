namespace TwelveIsland.Core;

[Flags] public enum ResizeEdges { None = 0, Left = 1, Right = 2, Bottom = 4 }

// Where a drag resizes the panel or the collapsed surface, in DIPs. The grips reach both inside and outside an edge, so an edge
// is easy to take from either side and a small overshoot past it still holds it, instead of leaving the panel and closing it.
// - The open panel resizes by its sides and its bottom, below its header; a bottom corner takes both, in a larger square.
// - The collapsed surface resizes by its ends, across its height and a little above and below it (more for the thin line).
public static class ResizeGrips
{
    public const double PanelInner = 10, PanelOuter = 12, PanelCorner = 24;
    public const double CompactInner = 14, CompactOuter = 14;
    // The room the window keeps around the panel or the surface for the grips outside them.
    public const double Room = 14;

    public static ResizeEdges Panel(double x, double y, DipRect panel, double header)
    {
        if (panel.Width <= 0 || panel.Height <= 0 || y < panel.Y + header) return ResizeEdges.None;
        if (x < panel.X - PanelOuter || x > panel.Right + PanelOuter || y > panel.Bottom + PanelOuter) return ResizeEdges.None;
        // A narrow panel keeps a middle that is not a grip.
        double inner = Math.Min(PanelInner, panel.Width / 4), corner = Math.Min(PanelCorner, panel.Width / 3);
        bool left = x <= panel.X + inner, right = x >= panel.Right - inner, bottom = y >= panel.Bottom - inner;
        if (y >= panel.Bottom - corner)
        {
            if (x <= panel.X + corner && (left || bottom)) return ResizeEdges.Bottom | ResizeEdges.Left;
            if (x >= panel.Right - corner && (right || bottom)) return ResizeEdges.Bottom | ResizeEdges.Right;
        }
        var edges = ResizeEdges.None;
        if (left) edges |= ResizeEdges.Left; else if (right) edges |= ResizeEdges.Right;
        if (bottom) edges |= ResizeEdges.Bottom;
        return edges;
    }

    // The strips the panel's grips take: the sides below the header, and the bottom.
    public static IEnumerable<DipRect> PanelStrips(DipRect panel, double header)
    {
        double top = panel.Y + header, height = panel.Bottom + PanelOuter - top;
        if (height <= 0 || panel.Width <= 0) yield break;
        double inner = Math.Min(PanelInner, panel.Width / 4);
        yield return new DipRect(panel.X - PanelOuter, top, PanelOuter + inner, height);
        yield return new DipRect(panel.Right - inner, top, PanelOuter + inner, height);
        yield return new DipRect(panel.X - PanelOuter, panel.Bottom - inner, panel.Width + 2 * PanelOuter, PanelOuter + inner);
    }

    // The area around the open panel that counts as the panel, grips included, so leaving by a grip does not close it.
    public static DipRect PanelArea(DipRect panel) => new(panel.X - PanelOuter, panel.Y, panel.Width + 2 * PanelOuter, panel.Height + PanelOuter);

    public static ResizeEdges Compact(double x, double y, DipRect surface, bool line)
    {
        if (surface.Width <= 0) return ResizeEdges.None;
        var zone = CompactZone(surface, line, ResizeEdges.Left);
        if (y < zone.Y || y > zone.Bottom) return ResizeEdges.None;
        if (x >= zone.X && x <= zone.Right) return ResizeEdges.Left;
        zone = CompactZone(surface, line, ResizeEdges.Right);
        return x >= zone.X && x <= zone.Right ? ResizeEdges.Right : ResizeEdges.None;
    }

    // The grip at one end of the collapsed surface. Its inner part is at most a fifth of the surface, so the middle still opens
    // the panel; the line, four DIPs high, gets more room above and below.
    public static DipRect CompactZone(DipRect surface, bool line, ResizeEdges end)
    {
        double inner = Math.Min(CompactInner, surface.Width / 5);
        double top = surface.Y - (line ? 6 : 3), bottom = surface.Bottom + (line ? 10 : 5);
        double left = end == ResizeEdges.Left ? surface.X - CompactOuter : surface.Right - inner;
        return new DipRect(left, top, CompactOuter + inner, bottom - top);
    }
}
