namespace TwelveIsland.Core;

// The collapsed content of a notch on a side edge (EdgeFrame.Across): the parts the row would show side by side stand one below
// the other, centred across the column, with the row's gap between them. Text stays upright. The column is as wide as its
// widest fixed part (the clock, the cover, the bars, a ring), and at least MinimumWidth; the system readings widen it up to
// ReadingCap, which holds a drive's letter with its space and temperature side by side (when so chosen), and a reading wider than that glides
// sideways within it, as the title does. A column longer than the notch glides
// up and back (a GlideCarousel turned upright), so what the row would scroll sideways scrolls along the edge here.
// Sizes are DIPs at the content's scale.
public static class CompactColumn
{
    public const double MinimumWidth = 36, ReadingCap = 84;
    // The rings stand in lines across the column, this far apart.
    public const double RingGap = 6;

    // The width the column needs: the widest fixed part and the readings up to the cap, never below the minimum. In whole DIPs,
    // rounded up.
    public static double Width(double scale, IEnumerable<double> fixedParts, IEnumerable<double> readings)
    {
        double s = CompactMusicLayout.SafeScale(scale);
        double widest = Math.Max(MinimumWidth * s, fixedParts.Where(double.IsFinite).DefaultIfEmpty(0).Max());
        double reading = Math.Min(ReadingCap * s, readings.Where(double.IsFinite).DefaultIfEmpty(0).Max());
        return Math.Ceiling(Math.Max(widest, reading) - 1e-9);
    }

    // How many rings of this size stand side by side in a column of this width (at least one), and how many lines they take.
    public static (int PerLine, int Lines) Rings(int count, double size, double gap, double width)
    {
        if (count <= 0) return (0, 0);
        double step = Math.Max(1e-9, size + gap);
        int perLine = Math.Clamp((int)Math.Floor((Math.Max(0, width) + gap) / step + 1e-9), 1, count);
        return (perLine, (count + perLine - 1) / perLine);
    }

    // The tops of parts of these heights, stacked with the gap between them, and the column's length.
    public static (double[] Tops, double Length) Stack(IReadOnlyList<double> heights, double gap)
    {
        var tops = new double[heights.Count];
        double y = 0;
        for (int i = 0; i < heights.Count; i++)
        {
            if (i > 0) y += gap;
            tops[i] = y;
            y += double.IsFinite(heights[i]) ? Math.Max(0, heights[i]) : 0;
        }
        return (tops, y);
    }

    // Where a column of this length rests in the notch's room, and how far it glides: centred when it fits, otherwise from the
    // top down by what does not fit.
    public static (double Top, double Travel) Place(double length, double room)
    {
        double l = double.IsFinite(length) ? Math.Max(0, length) : 0, r = double.IsFinite(room) ? Math.Max(0, room) : 0;
        return l <= r + 1e-9 ? ((r - l) / 2, 0) : (0, l - r);
    }
}
