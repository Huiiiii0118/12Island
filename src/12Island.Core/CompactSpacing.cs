namespace TwelveIsland.Core;

// Where the collapsed row's parts stand once CompactMusicLayout has given each its share. Every gap between two parts is the
// same; the quota rings and the system readings keep their own closer spacing inside their part (CompactMusicLayout.RingGap,
// MonitorStrip), which never widens. The gap starts at one unit (CompactMusicLayout.Gap), and the width the parts leave over is
// handed out in this order:
//   1. the title, up to the width it wants but no more than TitleMax;
//   2. every gap between two parts alike, up to MaxGap units, so the row widens evenly instead of opening one hole;
//   3. the title again, up to the width it wants;
//   4. what is still left stands equally at both ends: the content is centred.
// Positions are worked out in device pixels: the unit, every part's width (rounded up, so nothing in it is cut), the gap and
// the start are whole pixels, so every gap between two parts is exactly the same.
public static class CompactSpacing
{
    // The widest a gap grows to, in units.
    public const double MaxGap = 2;
    // The room a title fits in before the gaps widen, at the default height.
    public const double TitleMax = 160;

    // Width: the part's width, with any spacing inside it.
    public readonly record struct Part(double Width);

    // Left and Room are in DIPs, one per part. Gap is every gap between two parts.
    public sealed record Placement(double[] Left, double[] Room, double Gap);

    // parts: left to right. title: the index of the title, whose room this works out (-1 for none); titleText: the width it
    // wants (the collapsed row gives its share of the row, CompactMusicLayout.TextWidth, never its text's width, so the room does
    // not change with the line), and titleShortest the least room it keeps while there is any. scale: the height scale.
    public static Placement Arrange(IReadOnlyList<Part> parts, int title, double titleText, double titleShortest, double width, double unit, double scale, double dpi)
    {
        dpi = double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        int n = parts?.Count ?? 0;
        var left = new double[n]; var room = new double[n];
        double Px(double dips) => double.IsFinite(dips) && dips > 0 ? dips * dpi : 0;
        double available = Math.Floor(Px(width) + 1e-6), u = Math.Round(Px(unit));
        bool hasTitle = title >= 0 && title < n;
        double fixedRoom = 0, gaps = Math.Max(0, n - 1);
        for (int i = 0; i < n; i++)
            if (i != title) fixedRoom += room[i] = Math.Ceiling(Px(parts![i].Width) - 1e-6);
        // The title keeps its shortest room (or its text, if shorter); parts rounded to whole pixels may come to a pixel or two
        // more than the row, and the gaps give that up, alike.
        double want = Math.Ceiling(Px(titleText) - 1e-6), shortest = hasTitle ? Math.Min(want, Math.Round(Px(titleShortest))) : 0;
        if (available - fixedRoom - shortest - u * gaps < 0 && gaps > 0) u = Math.Max(0, Math.Floor((available - fixedRoom - shortest) / gaps));
        double free = Math.Max(0, available - fixedRoom - u * gaps);
        double maxExtra = Math.Floor(u * (MaxGap - 1));
        double Extra(double rest) => gaps > 0 ? Math.Max(0, Math.Min(maxExtra, Math.Floor(rest / gaps + 1e-9))) : 0;
        // The title's room: up to TitleMax first, then, once the gaps are at their widest, up to its text.
        double titleRoom = 0;
        if (hasTitle)
        {
            double cap = Math.Round(Px(TitleMax * CompactMusicLayout.SafeScale(scale)));
            titleRoom = Math.Floor(Math.Min(free, Math.Max(shortest, Math.Min(want, cap))));
            double rest = free - titleRoom;
            rest -= Extra(rest) * gaps;
            if (want > titleRoom) titleRoom += Math.Floor(Math.Max(0, Math.Min(rest, want - titleRoom)));
            room[title] = titleRoom;
            free -= titleRoom;
        }
        double extra = Extra(free);
        free -= extra * gaps;
        double gap = u + extra;
        double x = Math.Floor(Math.Max(0, free) / 2);
        for (int i = 0; i < n; i++)
        {
            left[i] = x / dpi;
            x += room[i] + gap;
            room[i] /= dpi;
        }
        return new(left, room, gap / dpi);
    }

    // A carousel's window over items side by side with this gap: the most whole items from the first that fit in the span, and
    // how far the strip moves for its other rest. Both rests start on an item's left edge, and at both no item crosses the
    // window's edges: what shows is whole, and what is past the right edge is hidden. At the second rest the last item stands
    // whole in the window (Travel); the items between pass through while it glides. Count is how many items the window holds
    // at the start; 0 when no window in the span can show both the first item and the last whole, and nothing shows (the
    // narrowest that can is Minimum). The window is as wide as those items, or as the last item when that is wider and fits
    // before the next item would show; Gaps is how many gaps it holds: Count - 1, or 0 for the last item's width. Travel is 0
    // when the strip rests.
    public static (int Count, double Window, int Gaps, double Travel) Window(IReadOnlyList<double> widths, double gap, double span)
    {
        int n = widths?.Count ?? 0;
        if (n == 0 || !double.IsFinite(span) || span <= 0) return (0, 0, 0, 0);
        gap = Safe(gap);
        int count = 0; double window = 0;
        for (int i = 0; i < n; i++)
        {
            double next = window + (i > 0 ? gap : 0) + Safe(widths![i]);
            if (next > span + 1e-6) break;
            window = next; count++;
        }
        if (count == 0 || count == n) return (count, window, Math.Max(0, count - 1), 0);
        double last = Safe(widths![n - 1]);
        // Widened to the last item, the window still ends before the next of the first items begins.
        if (last > window && last <= Math.Min(span, window + gap) + 1e-6) return (count, last, 0, Travel(widths, gap, last));
        // A window narrower than the last item would leave it out of both rests, and the strip would never show it.
        return last <= window + 1e-6 ? (count, window, count - 1, Travel(widths, gap, window)) : (0, 0, 0, 0);
    }

    // The narrowest window a carousel of these items can take (Window): 0 without items.
    public static double Minimum(IReadOnlyList<double> widths, double gap)
    {
        int n = widths?.Count ?? 0;
        double prefix = 0;
        for (int i = 0; i < n; i++)
        {
            prefix += (i > 0 ? Safe(gap) : 0) + Safe(widths![i]);
            // The last item's own width, when it is wider than the first ones but fits before the next would show.
            double last = Safe(widths[n - 1]);
            if (last > prefix && Window(widths, gap, last) is { Count: > 0 } own) return own.Window;
            if (Window(widths, gap, prefix) is { Count: > 0 } fit) return fit.Window;
        }
        return 0;
    }

    // Two carousels in one row (the quota rings and the system readings) share the room left for them, which holds neither
    // their gap to each other nor to their neighbours. Each first takes its narrowest window (Minimum), so both always show;
    // then they widen by turns, a step of whole items at a time, the one showing fewer items first, until the next step does
    // not fit. The order of the steps does not depend on the room, and a step that does not fit is not passed over for a later
    // one: a wider room only takes more of the same steps, so neither ever shows fewer items as the room grows. (Passing over a
    // reading's wide step for a ring's narrow one gave four rings, then three once the reading's step fitted, then four
    // again.) A row too short for both shows the first alone at its narrowest if it fits, else the second. Each result is its
    // Window.
    public static ((int Count, double Window, int Gaps, double Travel) First, (int Count, double Window, int Gaps, double Travel) Second)
        Share(IReadOnlyList<double> first, double firstGap, IReadOnlyList<double> second, double secondGap, double room)
    {
        room = double.IsFinite(room) ? Math.Max(0, room) : 0;
        var a = Steps(first, firstGap); var b = Steps(second, secondGap);
        int ia = -1, ib = -1;
        if (a.Count > 0 && b.Count > 0 && a[0] + b[0] <= room + 1e-6) { ia = 0; ib = 0; }
        else if (a.Count > 0 && a[0] <= room + 1e-6) ia = 0;
        else if (b.Count > 0 && b[0] <= room + 1e-6) ib = 0;
        double Width(List<double> steps, int i) => i < 0 ? 0 : steps[i];
        int Shown(IReadOnlyList<double> widths, double gap, List<double> steps, int i) => i < 0 ? 0 : Window(widths, gap, steps[i]).Count;
        // Only while both show, or one has nothing to show at all: one shown alone for want of room keeps its narrowest window,
        // which it keeps when the other joins it.
        bool together = (ia >= 0 || a.Count == 0) && (ib >= 0 || b.Count == 0);
        while (together)
        {
            bool canA = ia >= 0 && ia + 1 < a.Count, canB = ib >= 0 && ib + 1 < b.Count;
            if (!canA && !canB) break;
            bool takeA = canA && (!canB || Shown(first, firstGap, a, ia) <= Shown(second, secondGap, b, ib));
            double used = Width(a, ia) + Width(b, ib);
            if ((takeA ? used - a[ia] + a[ia + 1] : used - b[ib] + b[ib + 1]) > room + 1e-6) break;
            if (takeA) ia++; else ib++;
        }
        return (ia < 0 ? (0, 0, 0, 0) : Window(first, firstGap, a[ia]), ib < 0 ? (0, 0, 0, 0) : Window(second, secondGap, b[ib]));
    }

    // Share while the surface is being dragged wider or narrower. Share steps by whole items, so as the room changes a window
    // jumps by an item, and the gaps and the centring (Arrange) take the difference back: the row jumps with every item, which
    // read as a jerk against the continuous drag of 1.2.1. Here the windows take the room as it comes: each region first takes its
    // narrowest window, the second after the first, and then the regions widen by turns in the same order Share steps them, but
    // the one whose turn it is takes whatever room is left, up to its next step, instead of waiting for all of it. The windows
    // grow one for one with the room, never jump, and are Share's own at every room Share steps at (the sum of the windows
    // Share would take); in between they are wider than Share's, by less than one step, with an item cut at the right edge
    // (CutFade). Count is the whole items in the window; there is no Travel, nothing glides while the surface moves.
    public static ((int Count, double Window, int Gaps, double Travel) First, (int Count, double Window, int Gaps, double Travel) Second)
        ShareFlowing(IReadOnlyList<double> first, double firstGap, IReadOnlyList<double> second, double secondGap, double room)
    {
        room = double.IsFinite(room) ? Math.Max(0, room) : 0;
        var a = Steps(first, firstGap); var b = Steps(second, secondGap);
        double left = room, wa = 0, wb = 0;
        int ia = -1, ib = -1;
        bool partial = false;
        double Take(ref double window, List<double> steps, int index, ref int at)
        {
            double wanted = steps[index] - (index == 0 ? 0 : steps[index - 1]);
            double take = Math.Min(left, wanted);
            window += take; left -= take;
            if (take >= wanted - 1e-9) at = index; else partial = true;
            return take;
        }
        if (a.Count > 0) { Take(ref wa, a, 0, ref ia); }
        if (!partial && b.Count > 0) { Take(ref wb, b, 0, ref ib); }
        bool together = !partial && (ia >= 0 || a.Count == 0) && (ib >= 0 || b.Count == 0);
        while (together && !partial)
        {
            bool canA = ia >= 0 && ia + 1 < a.Count, canB = ib >= 0 && ib + 1 < b.Count;
            if (!canA && !canB) break;
            bool takeA = canA && (!canB || Window(first, firstGap, a[ia]).Count <= Window(second, secondGap, b[ib]).Count);
            if (takeA) Take(ref wa, a, ia + 1, ref ia); else Take(ref wb, b, ib + 1, ref ib);
        }
        return ((Whole(first, firstGap, wa), wa, 0, 0), (Whole(second, secondGap, wb), wb, 0, 0));
    }

    // How many items stand whole in a window this wide, from the first.
    public static int Whole(IReadOnlyList<double> widths, double gap, double window)
    {
        int count = 0; double used = 0;
        for (int i = 0; i < (widths?.Count ?? 0); i++)
        {
            double next = used + (i > 0 ? Safe(gap) : 0) + Safe(widths![i]);
            if (next > window + 1e-6) break;
            used = next; count++;
        }
        return count;
    }

    // The width of the fade that softens the item a window cuts at its right edge, inside the window (up to edge): the cut item shows
    // its first part and the rest is hidden; the fade is as wide as the smaller of the part shown, the part hidden and edge, so it
    // is nothing when the window ends in a gap or at an item's edge, where it is not cutting anything, and grows from there, and
    // the window changing continuously never shows a fade that appears or goes in one step.
    public static double CutFade(IReadOnlyList<double> widths, double gap, double window, double edge)
    {
        double x = 0;
        for (int i = 0; i < (widths?.Count ?? 0); i++)
        {
            double start = x, end = x + Safe(widths![i]);
            if (window > start + 1e-6 && window < end - 1e-6) return Math.Max(0, Math.Min(edge, Math.Min(window - start, end - window)));
            x = end + Safe(gap);
        }
        return 0;
    }

    // The windows a carousel can take, narrowest first, each one more step of whole items than the one before; the last holds
    // every item and rests.
    private static List<double> Steps(IReadOnlyList<double> widths, double gap)
    {
        var steps = new List<double>();
        int n = widths?.Count ?? 0;
        double prefix = 0, minimum = Minimum(widths!, gap);
        if (minimum > 0) steps.Add(minimum);
        for (int i = 0; i < n; i++)
        {
            prefix += (i > 0 ? Safe(gap) : 0) + Safe(widths![i]);
            if (prefix > minimum + 1e-6 && Window(widths, gap, prefix) is { Count: > 0 } fit && fit.Window > steps[^1] + 1e-6) steps.Add(fit.Window);
        }
        return steps;
    }

    // How far a strip of these items moves for its second rest in a window this wide: to the left edge of an item from which no
    // item crosses the window's right edge, the one that shows the furthest item whole, and of those the one that shows the most;
    // 0 when the first rest already shows as far.
    public static double Travel(IReadOnlyList<double> widths, double gap, double window)
    {
        int n = widths?.Count ?? 0;
        gap = Safe(gap);
        var lefts = new double[n];
        for (int i = 1; i < n; i++) lefts[i] = lefts[i - 1] + Safe(widths![i - 1]) + gap;
        int furthest = -1; double best = 0;
        for (int j = 0; j < n; j++)
        {
            double edge = lefts[j] + window;
            int k = j - 1; bool clean = true;
            for (int i = j; i < n && clean; i++)
            {
                double right = lefts[i] + Safe(widths![i]);
                if (right <= edge + 1e-6) k = i;
                else clean = lefts[i] >= edge - 1e-6;
            }
            if (clean && k > furthest) { furthest = k; best = lefts[j]; }
        }
        return best;
    }

    private static double Safe(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
