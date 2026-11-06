namespace TwelveIsland.Core;

public enum MediaState { Stopped, Paused, Playing, Unknown }
public enum MediaRepeat { None, Track, List }
// Like adds the current song to the player's own liked songs, or removes it.
public enum MediaCommand { PlayPause, Previous, Next, Shuffle, Repeat, Like }
public sealed record MediaTrack
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SongId { get; init; } = "";
    public string EmbeddedLyrics { get; init; } = "";
    public string? CurrentLyric { get; init; }
    public TimeSpan? CurrentLyricStart { get; init; }
    public bool IsQqMusic => Source == "QQ 音乐" || SongId.StartsWith("qq:", StringComparison.Ordinal) || SongId.StartsWith("qqid:", StringComparison.Ordinal);
    public bool IsAppleMusic => Source == "Apple Music";
    public bool IsKugou => Source == "酷狗音乐" || SongId.StartsWith("kugou:", StringComparison.Ordinal);
    public MediaState State { get; init; }
    public bool IsNeteaseLog { get; init; }
    public bool PreviousRestricted { get; init; }
    public bool PositionKnown { get; init; } = true;
    public bool TimelinePending { get; init; }
    public bool HasTimeline => PositionKnown && Duration > TimeSpan.Zero;
    public bool CanToggle { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanNext { get; init; }
    public bool CanSeek { get; init; }
    public bool CanShuffle { get; init; }
    public bool CanRepeat { get; init; }
    public bool Shuffle { get; init; }
    public MediaRepeat Repeat { get; init; }
    // Shuffle and repeat are independent switches with a button each (Apple Music), not one cycling playback mode.
    public bool SeparateModes { get; init; }
    // Only a player connection that both reports and changes the liked state offers it; Liked is meaningful only with CanLike.
    public bool CanLike { get; init; }
    public bool Liked { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public TimeSpan MinSeek { get; init; }
    public TimeSpan MaxSeek { get; init; }
    public TimeSpan ReportedPosition { get; init; }
    public DateTimeOffset PositionAt { get; init; } = DateTimeOffset.UtcNow;
    public double Rate { get; init; } = 1;
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    public string SongKey => $"{Title}\n{Artist}\n{Album}\n{Math.Round(Duration.TotalSeconds)}";
    public string Caption => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} · {Artist}";
    public TimeSpan Position(DateTimeOffset now)
    {
        if (!HasTimeline) return TimeSpan.Zero;
        double elapsed = State == MediaState.Playing ? Math.Max(0, (now - PositionAt).TotalSeconds) * (double.IsFinite(Rate) ? Rate : 1) : 0;
        return TimeSpan.FromSeconds(Math.Clamp((ReportedPosition - Start).TotalSeconds + elapsed, 0, Duration.TotalSeconds));
    }
    public long SeekTicks(double fraction)
    {
        var target = Start + TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        var min = MinSeek > Start ? MinSeek : Start;
        var max = MaxSeek > min && MaxSeek <= End ? MaxSeek : End;
        return Math.Clamp(target.Ticks, min.Ticks, Math.Max(min.Ticks, max.Ticks));
    }
}

// The collapsed row, left to right: the cover region, then the quota rings, the lyric or track notice, the system readings, the
// equalizer and the clock in the order of CompactPart chosen for the style. The rings, the lyric and the readings share the
// middle; the equalizer and the clock keep regions of their own. The order only places the parts; their widths are the same in
// any order. The equalizer shows with the media (the cover and the lyric) unless it is chosen apart (equalizer). The row shows what fits of each region whole: the rings and the
// readings that do not fit scroll through it (CompactSpacing.Window), and the surface is never narrower than the cover, the
// equalizer, the clock and the shortest lyric (RequiredWidth). CompactSpacing places the parts with one gap between all of them.
// Sizes follow the height through Scale (1 at the default 32 DIP height) and are whole DIPs; font sizes are
// whole or half DIPs. The width only moves the middle: the side regions keep their size until the middle is empty.
public sealed record CompactMusicLayout(double MiddleWidth, double RingSize, double RingGap, int VisibleRings, double RingWidth, double TextWidth, bool ScrollRings)
{
    // The narrowest scrolling lyric: about one and a half characters. Below it the lyric rests, clipped.
    public const double MinimumText = 18;
    public double Scale { get; init; } = 1;
    public double ContentWidth { get; init; }
    public double RowHeight { get; init; } = 24;
    // Cover region: the cover, then the gap to the middle. The region shrinks the gap first, then cuts the cover's right side.
    public double CoverWidth { get; init; }
    public double CoverSize { get; init; } = 20;
    public double CoverRadius { get; init; } = 4;
    // Equalizer region: the gap from the middle, then the bars; the gap goes first, then the bars' left side. Alone it has no gap.
    public double EqualizerWidth { get; init; }
    public double EqualizerSize { get; init; } = 18;
    public double EqualizerHeight { get; init; } = 14;
    public double EqualizerBar { get; init; } = 3;
    // System readings region (CompactMonitor), with a gap toward its neighbour; alone in the row it is centred.
    public double MonitorWidth { get; init; }
    public double MonitorFace { get; init; }
    public bool MonitorAlone { get; init; }
    // Clock region: the gap, then the face; a lone clock has no gap and is centred.
    public double ClockWidth { get; init; }
    public double ClockFace { get; init; }
    public bool ClockAlone { get; init; }
    // Space between the rings and the lyric, only while both have room.
    public double Between { get; init; }
    public double RingThickness { get; init; } = 2;
    // Fade beside rotating rings and at the lyric's edges.
    public double RingEdge { get; init; } = 3;
    public double TextEdge { get; init; } = 6;
    // The row's one type size: the clock's, which the readings and the title or lyric take too, so the whole row reads alike.
    public const double RowFont = 12.5;
    public double TextFont { get; init; } = RowFont;
    public double TextHeight { get; init; } = 22;
    public bool TextScrolls { get; init; }

    public static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    // The collapsed equalizer's bars (DeckSettings.EqualizerBars): 3 DIP wide, 5 DIP from one bar's left to the next at scale 1,
    // so the eight it starts with take 38 DIP. The row widens the rhythm by taking more bars, never by thickening them.
    public const int DefaultBars = 8, MinimumBars = 3, MaximumBars = 20;
    public static int SafeBars(int bars) => Math.Clamp(bars, MinimumBars, MaximumBars);
    // A bar's width and the step from one bar's left to the next, at scale 1.
    public const double BarWidth = 3, BarStep = 5;
    public static double BarsWidth(int bars, double scale) => Math.Max(BarWidth, Whole((BarWidth + (SafeBars(bars) - 1) * BarStep) * SafeScale(scale)));
    // The bars' height, as tall as the collapsed row has always drawn them.
    public static double RhythmHeight(double scale) => Math.Max(1, Whole(14 * SafeScale(scale)));
    public static double Half(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
    // Any scale is accepted; non-numbers and non-positive values mean the default size.
    public static double SafeScale(double scale) => double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, .25, 8) : 1;
    private static double Safe(double width) => double.IsFinite(width) && width > 0 ? width : 0;
    // The first letter or digit of an application's name, upper case, for the middle of its collapsed quota ring; empty without one.
    public static string RingLetter(string? name)
    {
        // By Unicode scalar (Rune), not UTF-16 unit: a name starting with a supplementary-plane character must give that whole character.
        foreach (var r in (name ?? "").EnumerateRunes()) if (System.Text.Rune.IsLetterOrDigit(r)) return System.Text.Rune.ToUpperInvariant(r).ToString();
        return "";
    }
    // The letter to draw: empty unless the setting is on (DeckSettings.QuotaRingLetters), else the name's (RingLetter).
    public static string RingLetterFor(bool enabled, string? name) => enabled ? RingLetter(name) : "";
    // The box (in whole device pixels) a letter's ink fills inside a ring, so that the box is centred on the ring's centre exactly.
    // A box with whole-pixel edges is symmetric about a centre only when its width has the parity of the cell's: a ring 12 pixels
    // across centres a letter 4 or 6 pixels wide, not 5 (which leaves the ink half a pixel to one side, as the eye sees). So the
    // ink (inkWidth, inkHeight: the glyph's own box in device pixels, stroke included, never the line box with its ascent and
    // descent) takes the nearest size of the cell's parity, never more than limit pixels (the ring's inner diameter less one), and
    // the glyph is scaled to it (about a fifth of the letter's size at most at 6 pixels, less in larger rings).
    // twiceCenter: twice the ring's centre in device pixels from the cell's corner (the cell's width, whole for a ring on whole
    // pixels), which keeps a cell half a pixel off whole centred too. centroidX/centroidY: where the letter's mass lies in its box,
    // 0 to 1 from left/top. The box is centred, then moved toward the mass (a V's is above, a T's near the bar) by half the mass's
    // distance from the centre, to whole pixels: the box and the mass each end up that far off the centre, instead of one of them
    // all of it. A mass within a pixel of the centre moves nothing, so the small letters of a small ring stay exactly centred; larger ones get the move.
    public static (int Left, int Top, int Width, int Height) RingLetterBox(double inkWidth, double inkHeight, int twiceCenter, double centroidX, double centroidY, double limit)
    {
        int Size(double ink, int twice)
        {
            int parity = Math.Abs(twice) & 1, most = Math.Max(parity == 0 ? 2 : 1, (int)Math.Floor(double.IsFinite(limit) ? limit : twice / 2.0));
            if ((most & 1) != parity) most--;
            if (!double.IsFinite(ink) || ink <= 0) return Math.Max(1, parity == 0 ? 2 : 1);
            int near = (int)Math.Round(ink);
            if ((near & 1) != parity) near = Math.Abs(ink - (near - 1)) <= Math.Abs(ink - (near + 1)) ? near - 1 : near + 1;
            return Math.Clamp(near, parity == 0 ? 2 : 1, Math.Max(most, parity == 0 ? 2 : 1));
        }
        int width = Size(inkWidth, twiceCenter), height = Size(inkHeight, twiceCenter);
        static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .5;
        int Move(int size, double fraction) => -(int)Math.Round((Fraction(fraction) - .5) * size / 2, MidpointRounding.ToEven);
        return ((twiceCenter - width) / 2 + Move(width, centroidX), (twiceCenter - height) / 2 + Move(height, centroidY), width, height);
    }
    // The gap between any two neighbours in the row, and between two rings or two readings (CompactSpacing).
    public static double Gap(double scale) => Whole(8 * SafeScale(scale));

    // The narrowest content for what the row shows, so the surface is never narrower (CollapsedGeometry.Resolve) and is no
    // wider than that needs: the clock; while media plays the cover, the equalizer and the shortest lyric; one quota ring when
    // the rings show (ring: its size, 0 without rings) and the narrowest window of the system readings when they show
    // (reading: CompactSpacing.Minimum, 0 without readings), which their carousels then show in turn; all with the gaps between
    // them. A clock alone, or rings alone, so make a short surface. In whole DIPs, rounded up, so rounding never takes a part away.
    public static double RequiredWidth(double scale, bool media, bool text, double clockFace, double ring = 0, double reading = 0, bool? equalizer = null, int bars = DefaultBars, bool cover = true)
    {
        double s = SafeScale(scale), gap = Gap(s), width = 0;
        int parts = 0;
        void Add(double part) { if (part <= 0) return; width += part; parts++; }
        if (media && cover) Add(Math.Max(1, Whole(20 * s)));
        if (equalizer ?? media) Add(BarsWidth(bars, s));
        if (media && text) Add(Whole(MinimumText * s));
        Add(Safe(ring)); Add(Safe(reading)); Add(Safe(clockFace));
        return parts == 0 ? 0 : Math.Ceiling(width + (parts - 1) * gap - 1e-9);
    }

    // The width an auto-width row keeps for the lyric or the title at scale 1. It does not follow the text, so the surface
    // keeps its width from line to line; a longer line scrolls in it.
    public const double TextSlot = 160;

    // The width a row with this content takes without cutting or rotating anything: the cover, every ring, the lyric's fixed
    // slot, the system readings, the equalizer and the clock, with their gaps. An auto-width surface takes it
    // (CollapsedGeometry.Resolve). dpi (device pixels per DIP, 0 for none): the width is rounded up to whole device pixels. The row
    // is laid out in whole device pixels, rounded down (DevicePixels.Down), so a width with a fraction left in it (a clock face
    // that is not a whole number of DIPs) would leave the readings a fraction short of the room they asked for, and they would scroll.
    public static double NaturalWidth(double scale, bool media, int rings, bool text, double clockFace, double monitorFace, double dpi = 0, bool? equalizer = null, int bars = DefaultBars, bool cover = true)
        => dpi > 0 ? DevicePixels.Up(Natural(scale, media, rings, text, clockFace, monitorFace, equalizer ?? media, bars, cover), dpi) : Natural(scale, media, rings, text, clockFace, monitorFace, equalizer ?? media, bars, cover);

    private static double Natural(double scale, bool media, int rings, bool text, double clockFace, double monitorFace, bool equalizer, int count, bool cover)
    {
        double s = SafeScale(scale), face = Safe(clockFace), monitor = Safe(monitorFace), bars = BarsWidth(count, s);
        rings = Math.Max(0, rings);
        // Media without the cover shows only its text and the equalizer, which are counted on their own.
        media &= cover;
        double width = media ? Whole(20 * s) + Whole(8 * s) : 0;
        width += RingsAndText(s, rings, text);
        bool alone = monitor > 0 && !media && !equalizer && rings == 0 && !text && face == 0;
        width += monitor == 0 ? 0 : alone ? monitor : monitor + Whole(8 * s);
        // The bars hold the gap toward the middle, the clock the gap toward whatever is before it.
        width += !equalizer ? 0 : media || rings > 0 || text || monitor > 0 ? bars + Whole(8 * s) : bars;
        width += face == 0 ? 0 : !media && !equalizer && rings == 0 && !text && monitor == 0 ? face : face + Whole(8 * s);
        return width;
    }

    // The width the rings and the lyric's fixed slot take without cutting or rotating the rings.
    private static double RingsAndText(double s, int rings, bool text)
    {
        double ringSpan = rings == 0 ? 0 : rings * Math.Max(1, Whole(12 * s)) + (rings - 1) * Whole(8 * s);
        // Beside a lyric the rings may take at most 40 % of the middle (Calculate), so the middle is wide enough for that too.
        double desired = Whole(TextSlot * s);
        return !text ? ringSpan : rings == 0 ? desired : Math.Max(Math.Ceiling(ringSpan / .4), ringSpan + Whole(8 * s) + desired);
    }

    // contentWidth here excludes the clock, as before custom sizes: the cover, middle and equalizer share it.
    public static CompactMusicLayout Calculate(double contentWidth, bool media, int rings, bool text)
        => Calculate(contentWidth, 1, media, rings, text, 0);

    // contentWidth is the whole row in DIPs; clockFace is the clock's reserved width at this scale, or 0 without a clock;
    // monitorFace is the width all chosen system readings want, or 0 without them; readings wider than their region scroll in it.
    // showCover false leaves the cover out while media plays; the lyric and the equalizer still show with the media.
    public static CompactMusicLayout Calculate(double contentWidth, double scale, bool media, int rings, bool text, double clockFace, double monitorFace = 0, bool? equalizer = null, int barCount = DefaultBars, bool showCover = true)
    {
        double s = SafeScale(scale), width = Safe(contentWidth), face = Safe(clockFace), monitorWanted = Safe(monitorFace);
        rings = Math.Max(0, rings);
        bool bars = equalizer ?? media;
        // From here media means the cover; the lyric counts through text.
        media &= showCover;
        double cover = Math.Max(1, Whole(20 * s)), coverGap = Whole(8 * s);
        double eqW = BarsWidth(barCount, s), eqH = RhythmHeight(s), eqGap = Whole(8 * s), bar = Math.Max(1, Whole(3 * s));
        // Beside system readings or the equalizer the clock is not alone.
        bool clockAlone = face > 0 && !media && !text && !bars && rings == 0 && monitorWanted == 0;
        // The bars hold the gap toward the middle, so without anything there they have none; the clock holds its own.
        bool barsGap = media || rings > 0 || text || monitorWanted > 0;
        double coverRegion = media ? cover + coverGap : 0, eqRegion = !bars ? 0 : barsGap ? eqGap + eqW : eqW;
        double clockRegion = face == 0 ? 0 : clockAlone ? face : face + Whole(8 * s);
        // The middle gives way first. When it is empty, the equalizer, then the cover, then the clock shrink in turn,
        // each to nothing before the next starts, so a 1 DIP change in width changes one region by at most 1 DIP.
        double deficit = Math.Max(0, coverRegion + eqRegion + clockRegion - width);
        double Take(double region) { double cut = Math.Min(region, deficit); deficit -= cut; return region - cut; }
        eqRegion = Take(eqRegion); coverRegion = Take(coverRegion); clockRegion = Take(clockRegion);
        double middle = Math.Max(0, width - coverRegion - eqRegion - clockRegion);
        // A middle too short for everything narrows the readings first, while the rings and the lyric keep their width; once
        // the two halves are even they narrow together. The readings scroll in what they keep, as the rings and the lyric do.
        // One ring and the shortest lyric always stay. Alone, the readings have the row. All are min and max of linear
        // functions of the width, with slopes from 0 to 1, as above.
        bool monitorAlone = monitorWanted > 0 && !media && !bars && rings == 0 && !text && face == 0;
        double monitorNeed = monitorWanted + Whole(8 * s);
        double monitorRegion = monitorWanted == 0 ? 0 : monitorAlone ? Math.Min(monitorWanted, middle)
            : Math.Min(Math.Min(monitorNeed, Math.Max(middle - RingsAndText(s, rings, text), middle / 2)),
                Math.Max(0, middle - (rings > 0 ? Math.Max(1, Whole(12 * s)) : 0) - (text ? Whole(MinimumText * s) : 0)));
        middle = Math.Max(0, middle - monitorRegion);

        // Rings have one size for a given height: the width never shrinks them. The ring region is cut at its edge, through a
        // ring if need be, and rotates while it is narrower than all the rings.
        double size = Math.Max(1, Whole(12 * s)), gap = Whole(8 * s);
        double between = Whole(8 * s), desired = Whole(56 * s), minimum = Whole(MinimumText * s);
        static double Span(int count, double size, double gap) => count == 0 ? 0 : count * size + (count - 1) * gap;
        double all = Span(rings, size, gap);
        double ringWidth, textWidth, used = 0;
        if (!text) { ringWidth = Math.Min(middle, all); textWidth = 0; }
        else if (rings == 0) { ringWidth = 0; textWidth = middle; }
        else
        {
            // Every share is a min or max of linear functions of the middle with slopes from 0 to 1, so one DIP of width moves
            // each region by at most one DIP; no threshold reallocates the row, and no lyric text, ring rotation or clock digit
            // takes part. The lyric fills to its minimum first, then the space between, then the rings: at least one ring, and
            // otherwise 40 % of the middle as long as the lyric keeps its preferred width, never more than all the rings.
            textWidth = Math.Min(middle, minimum);
            used = Math.Min(middle - textWidth, between);
            double rest = middle - textWidth - used;
            ringWidth = Math.Min(all, Math.Max(Math.Min(rest, size), Math.Min(middle * .4, rest - (desired - minimum))));
            textWidth = middle - used - ringWidth;
        }
        textWidth = Math.Max(0, textWidth); ringWidth = Math.Max(0, ringWidth);
        // Whole rings inside the region, for reference; the region itself is not rounded to them.
        int visible = Math.Clamp((int)Math.Floor((ringWidth + gap) / (size + gap) + 1e-9), 0, rings);
        return new(middle, size, gap, visible, ringWidth, textWidth, rings > 0 && ringWidth >= size && ringWidth < all - 1e-9)
        {
            Scale = s, ContentWidth = width, RowHeight = Math.Max(1, Whole(24 * s)),
            CoverWidth = coverRegion, CoverSize = cover, CoverRadius = Whole(4 * s),
            EqualizerWidth = eqRegion, EqualizerSize = eqW, EqualizerHeight = eqH, EqualizerBar = bar,
            ClockWidth = clockRegion, ClockFace = face, ClockAlone = clockAlone,
            MonitorWidth = monitorRegion, MonitorFace = monitorWanted, MonitorAlone = monitorAlone,
            Between = used, RingThickness = Math.Max(.5, Half(2 * s)), RingEdge = Whole(3 * s), TextEdge = Whole(6 * s),
            TextFont = Math.Max(1, Half(RowFont * s)), TextHeight = Math.Max(1, Whole(22 * s)), TextScrolls = text && textWidth >= minimum
        };
    }
}

// The quota notice that replaces the collapsed row: ring, application name, "used" text and reset time, centred.
// Each part has a region that clips; when the row is short, the reset time gives way first, then the name, then the
// ring, and the used text last, each down to nothing before the next starts. The window does not cut the notice this way:
// it measures it with unlimited width and scrolls it when it is longer than the row (Scroll).
public sealed record CompactAlertLayout(double Scale, double RingSize, double RingThickness, double FontSize, double LineHeight, double Gap,
    double RingWidth, double NameWidth, double TextWidth, double ResetWidth)
{
    public double Total => RingWidth + NameWidth + TextWidth + ResetWidth;
    // The name's natural width is capped at 76 DIP at the default height.
    public static CompactAlertLayout Calculate(double contentWidth, double scale, double name, double text, double reset)
    {
        double s = CompactMusicLayout.SafeScale(scale);
        static double Safe(double width) => double.IsFinite(width) && width > 0 ? width : 0;
        double width = Safe(contentWidth);
        double ring = Math.Max(1, CompactMusicLayout.Whole(14 * s)), gap = CompactMusicLayout.Whole(8 * s);
        double nameW = Math.Min(Safe(name), CompactMusicLayout.Whole(76 * s));
        // The ring and the name each hold the gap after them, the reset time the gap before it. A region that shrinks loses
        // its right side first: the gap after the ring or the name, then the content's end.
        double ringR = ring + gap, nameR = nameW > 0 ? nameW + gap : 0, textR = Safe(text), resetR = Safe(reset) > 0 ? gap + Safe(reset) : 0;
        double deficit = Math.Max(0, ringR + nameR + textR + resetR - width);
        double Take(double region) { double cut = Math.Min(region, deficit); deficit -= cut; return region - cut; }
        resetR = Take(resetR); nameR = Take(nameR); ringR = Take(ringR); textR = Take(textR);
        return new(s, ring, 2.2 * s, Math.Max(1, CompactMusicLayout.Half(11.5 * s)), Math.Max(1, CompactMusicLayout.Whole(16 * s)), gap, ringR, nameR, textR, resetR);
    }

    // DIP per second, the collapsed lyric's pace.
    public const double ScrollSpeed = 24;
    // A notice longer than its row scrolls once while it is shown: it rests at its start, glides until its end is in view and
    // rests there. The glide keeps the lyric's pace when that leaves the end its rest, and is faster when it does not, so the
    // whole notice has been seen before it goes. Returns when the glide starts and ends, in seconds from the notice's start.
    public static (double Start, double End) Scroll(double overflow, double shown)
    {
        double time = double.IsFinite(shown) && shown > 0 ? shown : 0;
        double hold = Math.Min(1, time / 5), glide = Math.Max(0, time - 2 * hold);
        double distance = double.IsFinite(overflow) && overflow > 0 ? overflow : 0;
        return (hold, hold + Math.Min(distance / ScrollSpeed, glide));
    }
}

public static class MusicSizes
{
    public static (double Width, double Height) For(PanelSize size) => size switch
    { PanelSize.Compact => (440, 140), PanelSize.Spacious => (760, 300), _ => (440, 200) };
}
