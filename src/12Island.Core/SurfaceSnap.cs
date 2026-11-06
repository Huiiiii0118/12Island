namespace TwelveIsland.Core;

// Where the island's left edge and width are while the panel opens, closes or changes size, kept on whole device pixels.
//
// WPF lays out on the pixel grid (UseLayoutRounding): the island's left margin, its width and the open page's offset inside it (the
// page is centred, so half of what is left over) are each rounded on their own. While the surface moves less than a pixel per frame
// (the opening's tail: the spring's give and its settling) the three cross their half pixels in different frames, so the page, drawn
// at Left + (Width - page) / 2 rounded, stepped one pixel left and right a few times though its place was not changing at all (measured
// offscreen with the real layout: up to four steps, three of them reversals, in the last 30 % of the move at 125 to 175 percent;
// none at 100 percent, where everything is whole already).
//
// The width is taken to the nearest whole pixel of the same parity as the end width, and the left edge to whole pixels about the
// centre the island is travelling to. Then the page's offset (Width - page) / 2 always has the same fraction, Left + that is a
// whole step per step of the width, and no frame rounds the other way: a symmetric opening leaves the page exactly where it will
// stay, and every edge only moves as the curve moves it.
public static class SurfaceSnap
{
    // To the nearest whole number, a half always upwards. Rounding to even at a half (Math.Round's way, WPF's too) would send the
    // half pixels that a width of the page's parity leaves in the left edge up and down by turns, a pixel to and fro. The left edge
    // is whole pixels, so the layout leaves it as it is.
    private static double Nearest(double value) => Math.Floor(value + .5 + 1e-7);

    private static bool Usable(double scale, double a, double b, double c, double d, double e) =>
        double.IsFinite(scale) && scale > 0 && double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d) && double.IsFinite(e);

    // The width at progress e of a move from `from` to `to` (DIPs); scale is device pixels per DIP. page is the width of the content
    // centred in the island (it stays the same through the move): the width keeps the parity that leaves it a whole number of
    // pixels of room on each side.
    public static double Width(double from, double to, double e, double scale, double page)
    {
        if (!Usable(scale, from, to, page, 0, e) || e <= 0) return from;
        // The spring passes its end (e above 1): that is its give, and it is kept; only the end itself (to a billionth) is exact.
        if (Math.Abs(e - 1) < 1e-9) return to;
        return WidthPixels(from, to, e, scale, page) / scale;
    }

    // In whole device pixels. A move of under two pixels is left as it is: snapping it to the page's parity would only add a step.
    private static double WidthPixels(double from, double to, double e, double scale, double page)
    {
        double linear = (from + (to - from) * e) * scale;
        if (Math.Abs(to - from) * scale < 2) return linear;
        return OfParity(linear, PageParity(page, scale));
    }

    // The progress by which the left edge has taken on the end's fraction of a pixel (see Left): from there it is the same for the rest
    // of the move, the spring's give included, so that nothing in the tail can cross a half pixel and come back.
    private const double SettleBy = .5;

    // The page's width in whole pixels as the layout makes it (to even at a half, as WPF rounds), odd or even.
    private static double PageParity(double page, double scale) => Math.Abs(Math.Round(page * scale)) % 2;

    private static double OfParity(double pixels, double parity) => Math.Max(0, 2 * Nearest((pixels - parity) / 2) + parity);

    // The left edge at progress e (DIPs), the width being Width(fromWidth, toWidth, e, scale, page): the island's centre travels from
    // fromLeft + fromWidth / 2 to toLeft + toWidth / 2 in a straight line on the same curve, and the edge is the centre less half the
    // width, on whole pixels. The width taken for that half is the snapped one plus what the start and the end widths have beyond
    // their own snapped widths, so that the edge lands on the place the layout gives the wider of the two states (where the page is
    // drawn) without a step; its fraction of a pixel is the wider state's from half the move on, the spring's give included, and the
    // edge counts its whole pixels from that place. Then the page's place is a whole-pixel step of a straight line in the progress, as the
    // centre is, and changes direction no oftener than the curve does.
    public static double Left(double fromLeft, double toLeft, double fromWidth, double toWidth, double e, double scale, double page)
    {
        if (!Usable(scale, fromLeft, toLeft, fromWidth, toWidth, e) || !double.IsFinite(page) || Math.Abs(e) < 1e-9) return fromLeft;
        if (Math.Abs(e - 1) < 1e-9) return toLeft;
        // The move seen from the wider state: closing is the opening run backwards.
        return fromWidth > toWidth ? Anchored(toLeft, fromLeft, toWidth, fromWidth, 1 - e, scale, page) : Anchored(fromLeft, toLeft, fromWidth, toWidth, e, scale, page);
    }

    // A move that ends at the wider state: e runs to 1 there.
    private static double Anchored(double fromLeft, double toLeft, double fromWidth, double toWidth, double e, double scale, double page)
    {
        double centre = fromLeft + fromWidth / 2 + (toLeft + toWidth / 2 - (fromLeft + fromWidth / 2)) * e;
        double width = WidthPixels(fromWidth, toWidth, e, scale, page), start = 0, end = 0;
        if (Math.Abs(toWidth - fromWidth) * scale >= 2)
        {
            double parity = PageParity(page, scale);
            start = fromWidth * scale - OfParity(fromWidth * scale, parity);
            end = toWidth * scale - OfParity(toWidth * scale, parity);
        }
        // The wider state's own left edge, rounded as the layout does it (to even at a half); every other whole-pixel place is counted from it.
        double anchor = toLeft * scale;
        double at = centre * scale - (width + start + (end - start) * Math.Clamp(e / SettleBy, 0, 1)) / 2;
        return (Math.Round(anchor) + Nearest(at - anchor)) / scale;
    }
}
