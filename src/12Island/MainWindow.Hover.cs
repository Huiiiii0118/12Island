using System.Windows;
using TwelveIsland.Core;

namespace TwelveIsland;

// The collapsed surface's pointer area (see CollapsedHover), in the two forms the window needs: a screen rectangle in device
// pixels for the cursor checks, and the box of the nearly transparent HoverExtension layer that receives the mouse there.
public partial class MainWindow
{
    // The sensing area in device pixels, comparable with GetCursorPos. Empty while the window has no presentation source.
    private Rect CompactHoverScreenRect()
    {
        if (PresentationSource.FromVisual(Stage) is null) return Rect.Empty;
        var zone = HoverZoneOnStage();
        return new Rect(Stage.PointToScreen(new Point(zone.Left, zone.Top)), Stage.PointToScreen(new Point(zone.Right, zone.Bottom)));
    }

    // The HoverExtension box in DIPs relative to Stage. Collapsed, it is the sensing area: from the screen edge down to the
    // bottom of the surface while bridged, otherwise the surface alone, so the gap above a lowered capsule lets clicks through.
    // Open, the panel uses its own outline as before: no height, and a NaN Left and Width that keep the stretched layout.
    private (double Top, double Height, double Left, double Width) HoverExtensionBox(bool expanded)
    {
        if (expanded) return (0, 0, double.NaN, double.NaN);
        var zone = HoverZoneOnStage();
        return (zone.Top, zone.Height, zone.Left, zone.Width);
    }

    // The collapsed shape is relative to the monitor's top-left corner. Stage starts at the window's corner, which Place puts
    // at the top of the monitor and _windowOffset DIPs to the right of its left edge; the island is drawn with the same offset.
    private Rect HoverZoneOnStage()
    {
        // A capsule docked with the bottom bridges to the line it is docked with (MainWindow.Edge.cs), the frame's edge.
        var zone = CollapsedHover.Zone(Collapsed.Style, Collapsed.Surface, Collapsed.Style == CompactStyle.Capsule && _frame.Edge != ScreenEdge.Top ? 0 : WorkDip.Y);
        return new Rect(zone.X - _windowOffset, zone.Y, Math.Max(0, zone.Width), Math.Max(0, zone.Height));
    }
}
