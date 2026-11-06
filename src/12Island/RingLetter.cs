using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

// The letter inside a collapsed quota ring, drawn as the glyph's outline and centred on that outline's own ink box. A TextBlock
// centres its line box, which holds the font's ascent and descent, so a capital sat a device pixel or two below the ring's centre.
// Here the outline (unhinted, stroked a little to give back the weight hinting would) is scaled to a box of whole device pixels
// whose width and height have the parity of the ring's, so that the box is centred on the ring's centre exactly
// (CompactMusicLayout.RingLetterBox); at 12 pixels across, a letter 5 pixels wide could only be half a pixel off.
internal static class RingLetter
{
    private static readonly Dictionary<(string Letter, double Size, double Cell, double Inner, double Ppd, string Font), (Geometry Glyph, double Stroke)> Made = [];

    // The letter's path, to put at the ring's top left corner (cell: the ring's size, inner: its inside diameter); null when the font
    // draws nothing for it. Sizes in DIP; the ring is expected on whole device pixels.
    public static Path? Create(string letter, double fontSize, double cell, double inner, double pixelsPerDip, FontFamily font, Brush ink)
    {
        if (letter.Length == 0) return null;
        double ppd = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        var key = (letter, fontSize, cell, inner, ppd, font.Source ?? "");
        if (!Made.TryGetValue(key, out var made))
        {
            var text = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, ink, ppd);
            var outline = text.BuildGeometry(new Point());
            var bounds = outline.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            double pixels = fontSize * ppd, stroke = Math.Min(.06 * pixels, .17 * Math.Sqrt(pixels)), strokeDip = stroke / ppd;
            var (cx, cy) = Mass(outline, bounds);
            // The box counts the stroke, which reaches half its width past the outline on every side.
            var box = CompactMusicLayout.RingLetterBox(bounds.Width * ppd + stroke, bounds.Height * ppd + stroke, (int)Math.Round(cell * ppd), cx, cy, Math.Round(inner * ppd) - 1);
            double sx = (box.Width - stroke) / (bounds.Width * ppd), sy = (box.Height - stroke) / (bounds.Height * ppd);
            // Outline to its origin, scaled to the box less the stroke, moved to the box's corner plus half the stroke; all in DIP.
            var place = new Matrix(sx, 0, 0, sy, (box.Left + stroke / 2) / ppd - bounds.X * sx, (box.Top + stroke / 2) / ppd - bounds.Y * sy);
            outline.Transform = new MatrixTransform(place);
            outline.Freeze();
            if (Made.Count > 64) Made.Clear();
            Made[key] = made = (outline, strokeDip);
        }
        return new Path { Data = made.Glyph, Fill = ink, Stroke = ink, StrokeThickness = made.Stroke, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    }

    // Where the glyph's mass lies in its box, 0 to 1 from the left and from the top: the centroid of the outline's polygon (the
    // counters, wound the other way, take their area off), so an A's is below the middle and a V's above it.
    private static (double X, double Y) Mass(Geometry outline, Rect bounds)
    {
        double area = 0, sumX = 0, sumY = 0;
        foreach (var figure in outline.GetFlattenedPathGeometry(.01, ToleranceType.Absolute).Figures)
        {
            var from = figure.StartPoint;
            var points = new List<Point> { from };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment line) points.AddRange(line.Points);
                else if (segment is LineSegment single) points.Add(single.Point);
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                area += cross; sumX += (a.X + b.X) * cross; sumY += (a.Y + b.Y) * cross;
            }
        }
        if (Math.Abs(area) < 1e-9) return (.5, .5);
        return (Math.Clamp((sumX / (3 * area) - bounds.X) / bounds.Width, 0, 1), Math.Clamp((sumY / (3 * area) - bounds.Y) / bounds.Height, 0, 1));
    }
}
