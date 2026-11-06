namespace TwelveIsland.Core;

// Limits and defaults of the three collapsed styles, in DIPs. The defaults reproduce the fixed sizes used before these
// values became settings: notch 238 x 32 at the screen edge, capsule 196 x 32 five DIPs below it, line 56 x 4 four DIPs below it.
public static class CollapsedLimits
{
    // The height at which compact content is drawn at its designed size; Scale is the surface height divided by this.
    public const double BaseHeight = 32;
    // The notch and the capsule may be as wide as the monitor (the work area for the capsule). NotchWidthMax and CapsuleWidthMax
    // are only the limits for saved values; each monitor narrows them when the shape is resolved (WidthRange).
    // The smallest widths are only what the shape itself needs; the content keeps the surface wide enough for what it shows
    // (CompactMusicLayout.RequiredWidth, CollapsedGeometry.Resolve).
    public const double NotchWidthDefault = 238, NotchWidthMin = 72, NotchWidthMax = 8000;
    public const double NotchHeightDefault = 32, NotchHeightMin = 24, NotchHeightMax = 64;
    public const double CapsuleWidthDefault = 196, CapsuleWidthMin = 48, CapsuleWidthMax = 8000;
    public const double CapsuleHeightDefault = 32, CapsuleHeightMin = 24, CapsuleHeightMax = 64;
    // CapsuleTopMax is only the limit for saved values; on a monitor the capsule may go down until its bottom edge meets the
    // bottom of the monitor, over a bottom taskbar (CapsuleTopRange).
    public const double CapsuleTopDefault = 5, CapsuleTopMin = 0, CapsuleTopMax = 4000;
    public const double LineLengthDefault = 56, LineLengthMin = 24, LineLengthMax = 600;
    // The thickness is chosen in half-DIP steps.
    public const double LineThicknessDefault = 4, LineThicknessMin = 2, LineThicknessMax = 8, LineThicknessStep = 0.5;
    public const double LineTopDefault = 4, LineTopMin = 0, LineTopMax = 16;
    // Saved horizontal offsets are kept within this range; each monitor narrows them further when the shape is resolved.
    public const double OffsetDefault = 0, OffsetLimit = 4000;
    // The smallest content scale the height range allows (24 / 32). The largest is 64 / 32 = 2; there is no further cap.
    public const double ContentMinScale = NotchHeightMin / BaseHeight;
    // Horizontal space kept free at each end of the content at scale 1; it grows with the scale.
    public const double NotchInset = 30, CapsuleInset = 16;
    // The notch's bottom corners and the curved shoulders that join it to the screen edge keep these sizes at every
    // width and height.
    public const double NotchCorner = 12, NotchShoulder = 16;
    // A notch on a side edge stands across the screen (EdgeFrame.Across): its content is a column (CompactColumn), with this much
    // free on either side of it at scale 1. The notch is as deep as the column needs, and never less than its height setting.
    public const double ColumnPad = 8;
    // A panel that floats below a lowered capsule (see CollapsedGeometry.Panel) is this much taller than its page: the space
    // is added above the header, so the switch sits 10 DIPs below the top edge instead of 4 and the header's icons 12, close
    // to the 14 DIPs between the last row and the bottom edge. (12 DIPs, with the icons 18 below the edge, looked too open.)
    public const double FloatingHeaderExtra = 6;
    // The floating panel's top edge lies this far above the capsule's vertical centre: the extra space plus half of a 32 DIP
    // header row. A 32 DIP header is then centred on the capsule's centreline, and at the default height (32) the panel's
    // bottom edge lies where the page alone would end below the capsule's top. It does not depend on the page, so switching
    // pages never moves the top edge.
    public const double CapsulePanelReach = FloatingHeaderExtra + 16;
}

// The collapsed surface as it is drawn on one monitor.
// Surface: position and size in DIPs, relative to the monitor's top-left corner.
// Scale: Surface.Height / CollapsedLimits.BaseHeight for the notch and the capsule (0.75 to 2); 1 for the line, which has no content.
// A notch on a side edge, deeper than its height for its column, keeps the scale of its height setting.
// Inset: free space at each end of the content, in DIPs: the style's base inset (30 notch, 16 capsule) times Scale; 0 for the line.
// ContentWidth: width available to the content in DIPs, Surface.Width - 2 * Inset, never below 0. It is not divided by Scale.
// BottomRadius and TopRadius: corner radii; Shoulder: the notch's shoulder radius (0 for the other styles).
public sealed record CollapsedShape(CompactStyle Style, DipRect Surface, double Scale, double Inset, double ContentWidth,
    double BottomRadius, double TopRadius, double Shoulder);

// Where an open panel sits: Top in DIPs from the top of the monitor, and HeaderExtra, the space added above the header of a
// panel that floats below a lowered capsule (0 when the panel hangs from the screen or work-area edge). The panel is the
// page's height plus HeaderExtra tall.
public readonly record struct PanelPlacement(double Top, double HeaderExtra);

// Turns the collapsed-style settings into a shape on a given monitor. The saved values are never changed here: a monitor that
// is too small for them only narrows what is drawn on it.
// monitor: the monitor's bounds in DIPs; only its width and height are used.
// work: the monitor's work area in DIPs, relative to the monitor's top-left corner.
// The notch is placed against the top of the monitor, over a top taskbar as before. The line stays within the work area; so
// does the capsule, except that it may go down past the work area's bottom, over a bottom taskbar. Every result lies on the
// monitor, with a width and height of at least 0, for any input.
public static class CollapsedGeometry
{
    // contentWanted: with auto width, the width the notch's or capsule's content asks for (0 for none); null keeps the saved
    // width. With a value the surface takes the content's width plus its insets, wider or narrower, between the style's
    // smallest width and the monitor.
    // contentRequired: the width of what the content must show (CompactMusicLayout.RequiredWidth: the clock and, while media plays,
    // its cover, equalizer and title; else one quota ring or the first system reading). The surface is always at least wide enough for it, with auto width or a saved width, within
    // the same limits.
    // panelScale: the scale TwelveIsland is drawn at (PanelScales). The saved heights, widths, the line's length and thickness and the notch's
    // corners are multiplied by it, so the content, which follows the height (Scale), grows with them; the offsets and the distances
    // from the top stay as saved, as places on the screen.
    // column: for a notch on a side edge, the width its column of content needs (CompactColumn.Width); null for the top and bottom
    // edges. The monitor and the work area are then those of the edge's frame (EdgeFrame), and the notch's width runs along the
    // edge. Its depth is the column's with ColumnPad on either side, at least its height setting; the height setting still sets the
    // content's scale.
    // lineOverTaskbar: the line hangs from the screen's edge over a taskbar there (DeckSettings.Dock); its offset and distance from
    // the edge are then measured on the monitor's frame rather than within the work area.
    public static CollapsedShape Resolve(DeckSettings settings, CompactStyle style, DipRect monitor, DipRect work, double pixelsPerDip = 1, double? contentWanted = null, double contentRequired = 0,
        double panelScale = 1, double? column = null, bool lineOverTaskbar = false)
    {
        double s = ValidScale(panelScale);
        bool auto = contentWanted is not null;
        double wanted = contentWanted is { } given && double.IsFinite(given) ? Math.Max(0, given) : 0;
        double required = Finite(contentRequired);
        var (screen, area) = Sanitize(monitor, work);
        switch (style)
        {
            case CompactStyle.Capsule:
            {
                double height = Math.Min(Value(settings.CapsuleHeight, CollapsedLimits.CapsuleHeightMin, CollapsedLimits.CapsuleHeightMax, CollapsedLimits.CapsuleHeightDefault) * s, area.Height);
                double saved = Value(settings.CapsuleWidth, CollapsedLimits.CapsuleWidthMin, CollapsedLimits.CapsuleWidthMax, CollapsedLimits.CapsuleWidthDefault) * s;
                double fit = Math.Max(CollapsedLimits.CapsuleWidthMin * s, SurfaceWidth(style, height, wanted));
                double width = Math.Min(Math.Max(auto ? fit : saved, SurfaceWidth(style, height, required)), area.Width);
                var (topMin, topMax) = TopRange(height, screen.Height - area.Y, double.PositiveInfinity);
                double top = Math.Clamp(Value(settings.CapsuleTop, CollapsedLimits.CapsuleTopMin, CollapsedLimits.CapsuleTopMax, CollapsedLimits.CapsuleTopDefault), topMin, topMax);
                double scale = height / CollapsedLimits.BaseHeight, inset = CollapsedLimits.CapsuleInset * scale;
                var surface = new DipRect(area.X + Place(area.Width, width, settings.CapsuleOffsetX), area.Y + top, width, height);
                return new(style, surface, scale, inset, Math.Max(0, width - 2 * inset), height / 2, height / 2, 0);
            }
            case CompactStyle.Line:
            {
                if (lineOverTaskbar) area = screen;
                double length = Math.Min(Value(settings.LineLength, CollapsedLimits.LineLengthMin, CollapsedLimits.LineLengthMax, CollapsedLimits.LineLengthDefault) * s, area.Width);
                double thickness = Math.Min(SnapToDevicePixels(RoundLineThickness(settings.LineThickness) * s, pixelsPerDip), area.Height);
                var (topMin, topMax) = TopRange(thickness, area.Height, CollapsedLimits.LineTopMax);
                double top = Math.Clamp(Value(settings.LineTop, CollapsedLimits.LineTopMin, CollapsedLimits.LineTopMax, CollapsedLimits.LineTopDefault), topMin, topMax);
                var surface = new DipRect(area.X + Place(area.Width, length, settings.LineOffsetX), area.Y + top, length, thickness);
                return new(style, surface, 1, 0, length, thickness / 2, thickness / 2, 0);
            }
            default:
            {
                double height = Math.Min(Value(settings.NotchHeight, CollapsedLimits.NotchHeightMin, CollapsedLimits.NotchHeightMax, CollapsedLimits.NotchHeightDefault) * s, screen.Height);
                double saved = Value(settings.NotchWidth, CollapsedLimits.NotchWidthMin, CollapsedLimits.NotchWidthMax, CollapsedLimits.NotchWidthDefault) * s;
                double fit = Math.Max(CollapsedLimits.NotchWidthMin * s, SurfaceWidth(CompactStyle.Notch, height, wanted));
                double width = Math.Min(Math.Max(auto ? fit : saved, SurfaceWidth(CompactStyle.Notch, height, required)), screen.Width);
                double scale = height / CollapsedLimits.BaseHeight, inset = CollapsedLimits.NotchInset * scale;
                double depth = column is { } across ? Math.Min(screen.Height, Math.Max(height, Finite(across) + 2 * CollapsedLimits.ColumnPad * scale)) : height;
                var surface = new DipRect(Place(screen.Width, width, settings.NotchOffsetX), 0, width, depth);
                return new(CompactStyle.Notch, surface, scale, inset, Math.Max(0, width - 2 * inset), CollapsedLimits.NotchCorner * s, 0, CollapsedLimits.NotchShoulder * s);
            }
        }
    }

    // The height a notch or capsule takes on this monitor for a saved height, as Resolve takes it: within the style's range and
    // no taller than the screen (notch) or the work area (capsule), at the scale. The line's thickness is returned as it is.
    public static double SurfaceHeight(CompactStyle style, double height, DipRect monitor, DipRect work, double scale = 1)
    {
        var (screen, area) = Sanitize(monitor, work);
        double s = ValidScale(scale);
        return style switch
        {
            CompactStyle.Notch => Math.Min(Value(height, CollapsedLimits.NotchHeightMin, CollapsedLimits.NotchHeightMax, CollapsedLimits.NotchHeightDefault) * s, screen.Height),
            CompactStyle.Capsule => Math.Min(Value(height, CollapsedLimits.CapsuleHeightMin, CollapsedLimits.CapsuleHeightMax, CollapsedLimits.CapsuleHeightDefault) * s, area.Height),
            _ => height
        };
    }

    // The surface width whose content area holds content DIPs at this height, up to the style's largest width; 0 for no content
    // and for the line. The content keeps clear of the capsule's inset, and of the notch's inset and, on a low notch where they are
    // wider, its shoulders and corners.
    public static double SurfaceWidth(CompactStyle style, double height, double content)
    {
        double c = Finite(content), h = Finite(height);
        if (c <= 0) return 0;
        return style switch
        {
            CompactStyle.Capsule => Math.Min(CollapsedLimits.CapsuleWidthMax, c + 2 * CollapsedLimits.CapsuleInset * h / CollapsedLimits.BaseHeight),
            CompactStyle.Notch => Math.Min(CollapsedLimits.NotchWidthMax, c + 2 * Math.Max(CollapsedLimits.NotchInset * h / CollapsedLimits.BaseHeight, CollapsedLimits.NotchShoulder + CollapsedLimits.NotchCorner)),
            _ => 0
        };
    }

    // The horizontal offsets that move a surface of this width on this monitor; larger saved offsets act as these limits.
    public static (double Min, double Max) OffsetXRange(CompactStyle style, double width, DipRect monitor, DipRect work)
    {
        double span = Span(style, monitor, work);
        double half = double.IsFinite(width) ? Math.Max(0, (span - Math.Clamp(width, 0, span)) / 2) : 0;
        return (-half, half);
    }

    // The widths that fit the style on this monitor: the setting's range at the scale, narrowed by the monitor or its work area.
    public static (double Min, double Max) WidthRange(CompactStyle style, DipRect monitor, DipRect work, double scale = 1)
    {
        double s = ValidScale(scale);
        var (min, max) = style switch
        {
            CompactStyle.Capsule => (CollapsedLimits.CapsuleWidthMin * s, CollapsedLimits.CapsuleWidthMax * s),
            CompactStyle.Line => (CollapsedLimits.LineLengthMin * s, CollapsedLimits.LineLengthMax * s),
            _ => (CollapsedLimits.NotchWidthMin * s, CollapsedLimits.NotchWidthMax * s)
        };
        double high = Math.Min(max, Span(style, monitor, work));
        return (Math.Min(min, high), high);
    }

    private static double ValidScale(double scale) => scale > 0 && double.IsFinite(scale) ? scale : 1;

    // The distances from the top of the work area at which a capsule of this height stays on the monitor: 0 to
    // monitorH - workY - H, so it may cover a bottom taskbar.
    public static (double Min, double Max) CapsuleTopRange(double height, DipRect monitor, DipRect work)
    {
        var (screen, area) = Sanitize(monitor, work);
        return TopRange(Finite(height), screen.Height - area.Y, double.PositiveInfinity);
    }

    // Where the open panel starts, in DIPs from the top of the monitor (work is relative to the monitor), and how much taller
    // than its page it is (HeaderExtra, added above the header).
    // Notch and line: the screen edge (0). Capsule within the hover bridge (CollapsedHover.HasBridge): the top of the work area,
    // which is the screen edge unless the taskbar is at the top. In these cases the panel hangs from that edge and HeaderExtra is 0.
    // Capsule lowered beyond the bridge: the panel floats, FloatingHeaderExtra taller than its page, with its top edge
    // CapsulePanelLift(height) above the capsule's top (6 at the default height), so the bottom edge lies where the page alone
    // would end below the capsule's top. A floating panel moves up as far as needed to stay inside the work area; when that
    // brings it to the work area's top, it hangs from there as above instead. bottomLimit, when given, is a lower bottom edge for
    // the panel than the work area's (the window keeps room below it).
    public static PanelPlacement Panel(CollapsedShape shape, DipRect work, double pageHeight, double bottomLimit = double.PositiveInfinity)
    {
        if (shape.Style != CompactStyle.Capsule || !double.IsFinite(work.Y) || !double.IsFinite(work.Height)) return new(0, 0);
        double top = Math.Max(0, work.Y), bottom = top + Finite(work.Height);
        if (!double.IsNaN(bottomLimit)) bottom = Math.Min(bottom, bottomLimit);
        if (CollapsedHover.HasBridge(CompactStyle.Capsule, shape.Surface, work.Y)) return new(top, 0);
        double extra = CollapsedLimits.FloatingHeaderExtra;
        double floating = Math.Clamp(shape.Surface.Y - CapsulePanelLift(shape.Surface.Height), top, Math.Max(top, bottom - Finite(pageHeight) - extra));
        return floating > top ? new(floating, extra) : new(top, 0);
    }

    // The top edge alone; see Panel.
    public static double PanelTop(CollapsedShape shape, DipRect work, double pageHeight, double bottomLimit = double.PositiveInfinity)
        => Panel(shape, work, pageHeight, bottomLimit).Top;

    // How far a floating panel starts above the capsule's top: max(0, 22 - H / 2), 6 at the default height.
    public static double CapsulePanelLift(double capsuleHeight) => Math.Max(0, CollapsedLimits.CapsulePanelReach - Finite(capsuleHeight) / 2);

    // The same for the line (at most 16).
    public static (double Min, double Max) LineTopRange(double thickness, DipRect work)
        => TopRange(Finite(thickness), Finite(work.Height), CollapsedLimits.LineTopMax);

    // A thickness rounded to the nearest half DIP within 2 to 8; a value that is not a number gives the default.
    public static double RoundLineThickness(double value)
        => double.IsFinite(value)
            ? Math.Clamp(Math.Round(value / CollapsedLimits.LineThicknessStep, MidpointRounding.AwayFromZero) * CollapsedLimits.LineThicknessStep, CollapsedLimits.LineThicknessMin, CollapsedLimits.LineThicknessMax)
            : CollapsedLimits.LineThicknessDefault;

    // A length in DIPs moved to the nearest whole number of device pixels (at least one), given in DIPs again. Rounding is
    // monotonic: a larger value never gives a smaller result, though neighbouring values may give the same one.
    public static double SnapToDevicePixels(double dip, double pixelsPerDip)
    {
        double scale = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        double pixels = double.IsFinite(dip) ? Math.Max(1, Math.Round(dip * scale, MidpointRounding.AwayFromZero)) : 1;
        return pixels / scale;
    }

    private static (double Min, double Max) TopRange(double height, double areaHeight, double limit)
        => (0, Math.Clamp(areaHeight - Math.Max(0, height), 0, limit));

    // Where a surface of the given width starts within a span: centred, moved by the offset as far as the span allows.
    private static double Place(double span, double width, double offset)
    {
        double half = Math.Max(0, (span - width) / 2);
        double shift = double.IsFinite(offset) ? Math.Clamp(offset, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit) : 0;
        return half + Math.Clamp(shift, -half, half);
    }

    private static double Span(CompactStyle style, DipRect monitor, DipRect work)
    {
        var (screen, area) = Sanitize(monitor, work);
        return style == CompactStyle.Notch ? screen.Width : area.Width;
    }

    private static double Value(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static double Finite(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    // A monitor with sizes that are not numbers or negative counts as empty. The work area is kept within the monitor; one
    // that is missing or empty is replaced by the whole monitor.
    private static (DipRect Screen, DipRect Area) Sanitize(DipRect monitor, DipRect work)
    {
        double width = Finite(monitor.Width), height = Finite(monitor.Height);
        var screen = new DipRect(0, 0, width, height);
        if (!double.IsFinite(work.X) || !double.IsFinite(work.Y) || !double.IsFinite(work.Width) || !double.IsFinite(work.Height)) return (screen, screen);
        double left = Math.Clamp(work.X, 0, width), top = Math.Clamp(work.Y, 0, height);
        double right = Math.Clamp(work.X + Math.Max(0, work.Width), left, width), bottom = Math.Clamp(work.Y + Math.Max(0, work.Height), top, height);
        return right - left <= 0 || bottom - top <= 0 ? (screen, screen) : (screen, new DipRect(left, top, right - left, bottom - top));
    }
}