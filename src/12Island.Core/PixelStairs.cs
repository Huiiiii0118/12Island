namespace TwelveIsland.Core;

// Pixel art's outline (a theme's "pixel" effect, IslandBorder.Pixel): a smooth outline with its curves turned into steps of whole
// device pixels. Plain numbers, so the tests can check the steps without WPF; IslandBorder flattens the WPF outline into edges.
public static class PixelStairs
{
    // A pixel size in DIP as whole device pixels, at least one, or 0 for none. A half rounds up, so 2 DIP at 125 % is 3 pixels
    // everywhere it is used (the island's steps, its bevel, the texture and the pixel clock's blocks).
    public static double Step(double pixel, double pixelsPerDip)
    {
        if (!(pixel > 0) || !double.IsFinite(pixel)) return 0;
        if (!(pixelsPerDip > 0) || !double.IsFinite(pixelsPerDip)) pixelsPerDip = 1;
        return Math.Max(1, Math.Round(pixel * pixelsPerDip, MidpointRounding.AwayFromZero)) / pixelsPerDip;
    }

    // The outline whose straight edges are given, within its bounds, with its curves turned into steps: every column keeps the
    // rows whose centres lie inside the outline. The island's outline meets every vertical line in one run (the shoulders flare
    // out at the top, the corners round off the bottom), so a column is a top and a bottom. curve is the largest corner or
    // shoulder radius: a corner takes about four blocks whatever its size, each a whole number of steps, so it reads as blocks
    // rather than a fine staircase. Columns are counted inward from both sides and rows from the top and from the bottom, so the
    // left and right corners, and the top and bottom ones, mirror each other whatever the size. Returns the corners of the
    // stepped outline, clockwise from the top left; every edge is horizontal or vertical.
    public static IReadOnlyList<(double X, double Y)> Build(IReadOnlyList<((double X, double Y) A, (double X, double Y) B)> edges,
        double left, double top, double right, double bottom, double curve, double step)
    {
        if (edges.Count == 0 || !(step > 0) || !(right > left) || !(bottom > top)) return [];
        step *= Math.Max(1, Math.Round(curve / (4 * step), MidpointRounding.AwayFromZero));
        double middle = (left + right) / 2;
        // Column edges: inward from the left and from the right; whatever is left in the middle is one column.
        var cuts = new SortedSet<double> { left, right };
        for (double x = left + step; x < middle; x += step) cuts.Add(x);
        for (double x = right - step; x > middle; x -= step) cuts.Add(x);
        var columns = cuts.ToList();
        var tops = new double[columns.Count - 1]; var bottoms = new double[columns.Count - 1];
        for (int i = 0; i + 1 < columns.Count; i++)
        {
            double xc = (columns[i] + columns[i + 1]) / 2, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var (a, b) in edges)
            {
                if (a.X <= xc == b.X <= xc) continue;
                double y = a.Y + (b.Y - a.Y) * (xc - a.X) / (b.X - a.X);
                lo = Math.Min(lo, y); hi = Math.Max(hi, y);
            }
            if (double.IsInfinity(lo)) { lo = top; hi = bottom; }
            tops[i] = lo <= top + 1e-6 ? top : Math.Min(bottom, top + step * Math.Ceiling((lo - top) / step - .5));
            bottoms[i] = hi >= bottom - 1e-6 ? bottom : Math.Max(top, bottom - step * Math.Ceiling((bottom - hi) / step - .5));
        }
        // A notch's shoulders flare thinner than a block at the screen edge: columns left empty at either end are dropped, so the
        // pixel notch meets the edge square.
        int first = 0, last = tops.Length - 1;
        while (first < last && bottoms[first] - tops[first] < 1e-6) first++;
        while (last > first && bottoms[last] - tops[last] < 1e-6) last--;
        var outline = new List<(double X, double Y)>();
        void Add(double x, double y)
        {
            if (outline.Count > 0 && Math.Abs(outline[^1].X - x) < 1e-6 && Math.Abs(outline[^1].Y - y) < 1e-6) return;
            // Three points on one line: the middle one goes.
            if (outline.Count > 1 && (Math.Abs(outline[^2].X - outline[^1].X) < 1e-9 && Math.Abs(outline[^1].X - x) < 1e-9
                || Math.Abs(outline[^2].Y - outline[^1].Y) < 1e-9 && Math.Abs(outline[^1].Y - y) < 1e-9)) outline[^1] = (x, y);
            else outline.Add((x, y));
        }
        for (int i = first; i <= last; i++) { Add(columns[i], tops[i]); Add(columns[i + 1], tops[i]); }
        for (int i = last; i >= first; i--) { Add(columns[i + 1], bottoms[i]); Add(columns[i], bottoms[i]); }
        return outline;
    }
}
