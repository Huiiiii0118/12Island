using System.Windows;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, CPU,
// memory and GPU each as a dial with its letter inside, with its figure after it or alone (narrower), download and upload, and,
// when chosen, the drives' space with a drive glyph, in the order and the colors set in the settings' table (CompactReading);
// the rings alone sit closer together. CPU, GPU and the drives show their share in use, their temperature, or both one above the
// other in two smaller lines, or side by side when so chosen (CompactFigures); the drives show each chosen letter after one glyph, each after its letter when
// there are several (CompactSources); the frame rate shows the program in front's frames a second after its icon or a screen glyph
// (CompactFrames). It asks for the width of everything chosen (PreferredWidth); when its region is narrower,
// the row draws it at that width and scrolls it through a window of whole readings (ReadingsCarousel, CompactSpacing.Window).
// Each reading keeps the width of the widest figures it can show, so the window and the readings in it never move with the
// figures; the readings keep their own close spacing (Gap), which the row does not widen. It draws itself once per reading; the
// collapsed row stays still between readings. Given a narrower width directly, it shows what fits, dropping whole readings
// (CompactMonitor.Fit) so it never cuts through a figure: a reading that no longer fits fades and shrinks out of its place while
// the others slide together. The panel keeps those transitions (StripPresence) across rebuilds of the row, which happen on every
// change of its width.
internal sealed class MonitorStrip : FrameworkElement
{
    // The parts of the drive reading a strip draws: the glyph and the letters as in the row, or the letters alone.
    internal enum DriveParts { All, Letters }
    private readonly DriveParts _driveParts;
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _shown, _values;
    private readonly IReadOnlyList<CompactMonitorItems> _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _small, _dial, _gap, _row, _dpi;
    private readonly Func<CompactMonitorItems, double> _width;
    private MonitorSnapshot? _snapshot;
    // The look of the power and charge readings on the theme in use.
    private readonly PowerStyle _powerStyle;
    // Made once, as the strip measures and draws on every reading: the readings in order, each once, the delegates it hands to
    // CompactMonitor (a method group or a lambda passed each time would be allocated each time), and the list they are
    // arranged into.
    private readonly CompactMonitorItems[] _orderOnce;
    private readonly Func<CompactMonitorItems, double> _presenceNow, _ringFigure;
    private readonly List<(CompactMonitorItems Item, double X, double Width, double Presence)> _arranged = [];
    private double _now;
    // The rings showing their share in use and their temperature after them; the drive letters showing, the tag before each
    // (empty when one shows), and whether each shows its space and its temperature. _inline: the readings showing both figures
    // side by side rather than one above the other.
    private readonly CompactMonitorItems _usage, _heat, _inline;
    private readonly string[] _letters, _tags;
    private readonly bool[] _letterUsage, _letterHeat;
    // Whether the frame rate shows its program's icon in place of the glyph (CompactFrames).
    private readonly bool _framesIcon;

    // unavailable: readings left out (StripAdmission.Hidden): the drives until their first figures, and for good on a machine
    // without them, so the row never shows a dash for them. admission: which temperatures and letters have given their first
    // figures (StripAdmission.Shows); without it every chosen one shows.
    // parts: what the drive reading draws. The collapsed column (MainWindow.CompactColumn.cs) gives each drive a line without the
    // glyph, its letter shown even when it is the only one.
    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi,
        CompactMonitorItems unavailable = CompactMonitorItems.None, StripAdmission? admission = null, DriveParts parts = DriveParts.All)
    {
        _driveParts = parts;
        _easing = new PromptTimer(Dispatcher, EaseStep);
        _presence = presence; _dpi = dpi; _colors = colors; _centered = centered; _powerStyle = PowerStyle.For(colors.Palette);
        _order = [.. readings.Select(reading => reading.Item)];
        _orderOnce = [.. _order.Distinct()];
        _presenceNow = item => _presence.At(item, _now); _ringFigure = RingFigure;
        bool shows(CompactMonitorItems item, string part) => admission?.Shows(item, part) ?? true;
        _usage = CompactMonitorItems.Memory;
        foreach (var reading in readings)
        {
            if (reading.Item is not (CompactMonitorItems.Cpu or CompactMonitorItems.Gpu)) continue;
            if (CompactFigures.Shows(reading, CompactFigures.Usage)) _usage |= reading.Item;
            if (CompactFigures.Shows(reading, CompactFigures.Temperature) && shows(reading.Item, StripAdmission.Heat)) _heat |= reading.Item;
            if (reading.FiguresInLine) _inline |= reading.Item;
        }
        var drives = readings.FirstOrDefault(reading => reading.Item == CompactMonitorItems.Storage) ?? new CompactReading { Item = CompactMonitorItems.Storage };
        bool driveUsage = CompactFigures.Shows(drives, CompactFigures.Usage), driveHeat = CompactFigures.Shows(drives, CompactFigures.Temperature);
        if (drives.FiguresInLine) _inline |= CompactMonitorItems.Storage;
        _letters = [.. admission?.Letters(drives) ?? CompactSources.Of(drives)];
        _tags = [.. _letters.Select(letter => _letters.Length > 1 || parts == DriveParts.Letters ? CompactSources.Tag(letter) : "")];
        _letterUsage = [.. _letters.Select(letter => driveUsage && shows(CompactMonitorItems.Storage, letter))];
        _letterHeat = [.. _letters.Select(letter => driveHeat && shows(CompactMonitorItems.Storage, letter + StripAdmission.Heat))];
        if (_letters.Length == 0) unavailable |= CompactMonitorItems.Storage;
        var frames = readings.FirstOrDefault(reading => reading.Item == CompactMonitorItems.Frames) ?? new CompactReading { Item = CompactMonitorItems.Frames };
        _framesIcon = CompactFrames.Shows(frames, CompactFrames.Icon);
        _shown = CompactMonitor.Shown(readings) & ~unavailable;
        // A ring set to show only its temperature, on a machine that has none, shows its dial alone.
        _values = CompactMonitor.Values(readings) & (_usage | _heat);
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(CompactMusicLayout.RowFont * _scale));
        // Two figures stack in smaller type, so both fit the row's height.
        _small = Math.Max(1, CompactMusicLayout.Half(8.5 * _scale));
        // Each ring is a dial carrying its letter, with its figure after it or alone.
        _dial = Math.Max(4, CompactMusicLayout.Whole(16 * _scale));
        // Readings with figures sit closer than the dials alone: a figure narrower than the widest it can show already leaves room after it.
        _gap = CompactMusicLayout.Whole((CompactMonitor.RingsOnly(_shown, _values) ? 7 : 6) * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
        // Fixed per item, from the widest figure each can show, so the row's layout never moves with the readings. A rate has
        // three digits and a letter, with a point below 100 (MonitorFormat.CompactRate): "99.9M" or "99.9G" at the widest.
        double text(string sample) => MonitorDraw.Text(sample, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace;
        _width = CompactMonitor.Widths(_values, _dial, _ringFigure, _dial, ColumnsWidth(Environment.ProcessorCount),
            Math.Round(9 * _scale) + Math.Round(3 * _scale) + Math.Ceiling(Math.Max(text("99.9M"), text("99.9G"))), DrivesWidth(),
            Math.Round(9 * _scale) + Math.Round(3 * _scale) + FigureWidth("999W"), BatteryWidth + Math.Round(3 * _scale) + FigureWidth(WidestPercent),
            FramesSlot + Math.Round(3 * _scale) + FigureWidth(WidestFrames) + Math.Round(2 * _scale) + SmallWidth(FramesUnit));
    }

    private const string WidestDegrees = "125℃", WidestPercent = "100%";
    // Monitors run up to 500 Hz and more, so three digits; the unit follows in the smaller type.
    private const string WidestFrames = "999", FramesUnit = "FPS";
    private double FramesSlot => _framesIcon ? Math.Round(14 * _scale) : Glyph;
    // The figures after a ring or a drive letter: the share alone, the temperature alone, or both stacked in the smaller type or
    // side by side in the row's type, at the widest each can show.
    private double Column(CompactMonitorItems item, bool usage, bool heat)
    {
        if (usage && heat)
            return (_inline & item) != 0 ? FigureWidth(WidestPercent) + InlineGap + FigureWidth(WidestDegrees)
                : Math.Max(SmallWidth(WidestPercent), SmallWidth(WidestDegrees));
        return usage ? FigureWidth(WidestPercent) : heat ? FigureWidth(WidestDegrees) : 0;
    }
    private double InlineGap => Math.Round(3 * _scale);
    private double RingFigure(CompactMonitorItems item) => Math.Round(4 * _scale) + Column(item, (_usage & item) != 0, (_heat & item) != 0);

    // The drives: a glyph, then each letter, its tag (if any) and its figures.
    private double Glyph => Math.Round(11 * _scale);
    private double TagGap => Math.Round(2 * _scale);
    private double EntryGap => Math.Round(5 * _scale);
    private double DrivesWidth()
    {
        double width = _driveParts == DriveParts.Letters ? 0 : Glyph + Math.Round(3 * _scale);
        for (int i = 0; i < _letters.Length; i++)
            width += (i > 0 ? EntryGap : 0) + (_tags[i].Length > 0 ? FigureWidth(_tags[i]) + TagGap : 0) + Column(CompactMonitorItems.Storage, _letterUsage[i], _letterHeat[i]);
        return width;
    }

    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private CompactMonitorItems Shown => _shown;
    // Rounded up to whole device pixels: the row rounds the region it gives down to them, which must not drop a reading.
    public double PreferredWidth => DevicePixels.Up(CompactMonitor.Width(Shown, _width, _gap), _dpi);

    // The dials and the core columns ease to each new reading as the page's do (Eased); the figures and the levels' colors take the
    // reading at once. A dial whose reading is missing shows empty, and one that arrives (the first reading of a row built again,
    // too) takes its value at once.
    private struct Smooth
    {
        private Eased _value;
        private bool _has;
        public bool Set(double? target, bool motion, double now)
        {
            if (target is not { } value) { _has = false; return false; }
            bool moving = _value.Set(value, motion && _has, now);
            _has = true;
            return moving;
        }
        public readonly double? At(double now) => _has ? _value.At(now) : null;
        public readonly bool Moving(double now) => _has && _value.Moving(now);
    }
    private Smooth[] _cores = [];
    // The values ease on a timer of their own at the frames a second chosen (CompactSmoothing), not on Motion: Motion hooks the
    // render loop, which then draws the whole window on every display frame, and the readings move after every reading, once a
    // second. The timer stops once they rest, after drawing their final values.
    private readonly PromptTimer _easing;
    private void EaseStep()
    {
        InvalidateVisual();
        if (!ValuesMoving(Eased.Now)) _easing.Stop();
    }
    private Smooth _cpu, _memory, _gpu;

    // The gap between two readings.
    public double Gap => _gap;
    // The width of each reading shown, left to right, for the window the row scrolls them through (CompactSpacing.Window). The
    // upload right after the download is one item with it, with the gap between them: the carousel never stops between them.
    // Each keeps the room of its widest figure while the readings scroll too, so a new download figure never moves the upload.
    public List<double> ItemWidths()
    {
        var widths = new List<double>();
        var previous = CompactMonitorItems.None;
        foreach (var item in _orderOnce)
        {
            if ((Shown & item) == 0 || !CompactMonitor.Order.Contains(item)) continue;
            double width = Math.Max(0, _width(item));
            if (item == CompactMonitorItems.Upload && previous == CompactMonitorItems.Download) widths[^1] += _gap + width;
            else widths.Add(width);
            previous = item;
        }
        return widths;
    }

    // frames: the frames a second the dials and the core columns are drawn at while they ease (CompactSmoothing).
    public void Update(MonitorSnapshot? snapshot, bool motion, int frames)
    {
        _snapshot = snapshot;
        _easing.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, frames));
        var s = snapshot;
        double now = Eased.Now;
        bool moving = false;
        var cpu = s?.Cpu.OrDefault;
        if ((Shown & CompactMonitorItems.Columns) != 0)
        {
            int count = cpu?.Cores.Count ?? 0;
            if (count != _cores.Length) _cores = new Smooth[count];
            for (int i = 0; i < count; i++) moving |= _cores[i].Set(cpu!.Cores[i].Usage, motion, now);
        }
        if ((Shown & CompactMonitorItems.Cpu) != 0) moving |= _cpu.Set(cpu?.Usage, motion, now);
        if ((Shown & CompactMonitorItems.Memory) != 0) moving |= _memory.Set(s?.Memory.OrDefault?.UsedFraction, motion, now);
        if ((Shown & CompactMonitorItems.Gpu) != 0) moving |= _gpu.Set(s?.Gpu.OrDefault is { NoReadings: false } gpu ? gpu.Usage : null, motion, now);
        if (moving && !_easing.IsEnabled) _easing.Start();
        InvalidateVisual();
    }
    private bool ValuesMoving(double now)
    {
        for (int i = 0; i < _cores.Length; i++) if (_cores[i].Moving(now)) return true;
        return _cpu.Moving(now) || _memory.Moving(now) || _gpu.Moving(now);
    }

    private double FigureWidth(string figure) => Math.Ceiling(MonitorDraw.Text(figure, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace);
    private double SmallWidth(string figure) => Math.Ceiling(MonitorDraw.Text(figure, _small, UI.Primary, 1).WidthIncludingTrailingWhitespace);

    // Every whole percent's figure, made once: the gauges show one on every reading.
    private static readonly string[] Percents = [.. Enumerable.Range(0, 101).Select(percent => MonitorFormat.Percent(percent / 100d) + "%")];
    private static string GaugeFigure(double? share) => share is { } v ? Percents[MonitorFormat.WholePercent(v)] : "—";
    private static string RateFigure(double? rate)
    {
        var q = rate is { } r ? MonitorFormat.CompactRate(r) : new MonitorFormat.Quantity("—", "");
        return q.Number + q.Unit;
    }
    // Whole degrees with the Celsius sign, the number followed by ℃: "62℃". Made once for every degree a reading can have.
    private static readonly string[] Degrees = [.. Enumerable.Range(0, (int)Temperatures.Highest + 1).Select(degrees => Temperatures.Format(degrees) + "℃")];
    private static string TemperatureFigure(double? celsius) => celsius is { } c ? Degrees[(int)Math.Clamp(Math.Round(c, MidpointRounding.AwayFromZero), 0, Temperatures.Highest)] : "—";

    // The readings that fit the width the row gives; a change starts their transitions. A row built again in the middle of one
    // carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var fitted = CompactMonitor.Fit(Shown, _width, _gap, Math.Max(0, finalSize.Width));
        if (_presence.Set(Shown, fitted, Motion.Enabled) | _presence.Moving(Eased.Now)) Motion.Start(this, Step);
        return finalSize;
    }
    private bool Step(double now) { InvalidateVisual(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2, now = Eased.Now;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        _now = now;
        double total = CompactMonitor.Arrange(_orderOnce, Shown, _width, _gap, _presenceNow, _arranged);
        if (total <= 0) return;
        double start = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        foreach (var (item, at, width, presence) in _arranged)
        {
            double x = start + at;
            // A reading on its way in or out is drawn smaller and fainter, centred in the room it has.
            bool partial = presence < .999;
            double offset = (width * presence - width) / 2;
            if (partial)
            {
                double zoom = .82 + .18 * presence;
                drawing.PushOpacity(presence * presence);
                drawing.PushTransform(new ScaleTransform(zoom, zoom, x + width * presence / 2, center));
                drawing.PushTransform(new TranslateTransform(offset, 0));
            }
            double left = Math.Round(x);
            // Each reading in the color of its level (ReadingLevels); the core columns take theirs core by core.
            var level = item == CompactMonitorItems.Charge ? BatteryModel.Level(s?.Battery.OrDefault, BatteryModel.LowPercent) : ReadingLevels.Of(item, s);
            string color = _colors.At(item, level);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, width, center, s?.Cpu.OrDefault, now); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, item, _cpu.At(now), s?.Cpu.OrDefault?.Usage, color, level, "C", s, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, item, _memory.At(now), memory?.UsedFraction, memory is null ? UI.Tertiary : color, level, "M", s, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, item, _gpu.At(now), s?.Gpu.OrDefault is { NoReadings: false } gpu ? gpu.Usage : null, color, level, "G", s, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Storage: Drives(drawing, left, center, s, ppd); break;
                case CompactMonitorItems.Power: Power(drawing, left, center, s?.Power.OrDefault, ppd); break;
                case CompactMonitorItems.Charge: Charge(drawing, left, center, s?.Battery.OrDefault, level, ppd); break;
                case CompactMonitorItems.Frames: Frames(drawing, left, center, FrameOf(s), color, ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
        }
    }

    // The cores as thin columns, fastest kind first, in their colors; their tops show the load at a glance, and a busy core
    // takes the warning or critical color by its own load.
    private void Columns(DrawingContext drawing, double x, double width, double center, CpuSample? cpu, double now)
    {
        double height = Math.Round(14 * _scale), top = Math.Round(center - height / 2);
        if (cpu is null || cpu.Cores.Count == 0) { drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, new Rect(x, top, width, height), 2, 2); return; }
        int count = cpu.Cores.Count, groups = Math.Max(1, cpu.GroupCount);
        double gap = Math.Max(.5, _scale), column = Math.Max(1, (width - (count - 1) * gap) / count);
        for (int i = 0; i < count; i++)
        {
            var core = cpu.Cores[i];
            double usage = i < _cores.Length ? _cores[i].At(now) ?? core.Usage : core.Usage;
            double cx = x + i * (column + gap), fill = Math.Max(1.5, usage * height);
            drawing.DrawRectangle(UI.Brush(UI.Fill), null, new Rect(cx, top, column, height));
            drawing.DrawRectangle(UI.Brush(CoreColor(core.Group, groups, ReadingLevels.OfShare(core.Usage))), null, new Rect(cx, top + height - fill, column, fill));
        }
    }
    // A core's color is mixed from the strip's colors, which it keeps: each is mixed once, not for every core on every reading.
    private readonly Dictionary<(int Group, int Groups, MemoryLevel Level), string> _coreColors = [];
    private string CoreColor(int group, int groups, MemoryLevel level)
    {
        if (!_coreColors.TryGetValue((group, groups, level), out var color)) _coreColors[(group, groups, level)] = color = _colors.Core(group, groups, level);
        return color;
    }

    // A dial open at the bottom with the reading's letter inside, and its figures after it when it shows them: the gap tells it
    // from the full quota rings beside it, and a letter stays legible at this size where a glyph does not.
    // dial: the share the dial shows as it eases; share: the reading's, which the figure shows.
    private void Gauge(DrawingContext drawing, double x, double center, CompactMonitorItems item, double? dial, double? share, string color, MemoryLevel level, string letter, MonitorSnapshot? s, double ppd)
    {
        // As thick as a quota ring and on the same track, so its whole outline shows even at a low share.
        MonitorDraw.Dial(drawing, new Point(x + _dial / 2, center), _dial, Math.Max(1.2, CompactMusicLayout.Half(2 * _scale)), Math.Max(1, 7 * _scale), dial, color, letter, ppd);
        if ((_values & item) == 0) return;
        var heat = (_heat & item) != 0 ? CompactFigures.Heat(item, s) : null;
        Figures(drawing, x + _dial + Math.Round(4 * _scale), center, item, (_usage & item) != 0 ? (share, level) : null, (_heat & item) != 0, heat, ppd);
    }

    // The share in use and the temperature at x: one in the row's type, or both stacked in the smaller type, the share above, or
    // side by side in the row's type, the share first.
    // Each keeps the text color while normal and takes its own level's color at the warning and critical levels.
    private void Figures(DrawingContext drawing, double x, double center, CompactMonitorItems item, (double? Share, MemoryLevel Level)? usage, bool heat,
        (double Celsius, MemoryLevel Level)? degrees, double ppd)
    {
        var heatLevel = degrees?.Level ?? MemoryLevel.Normal;
        string? shareText = usage is { } u ? GaugeFigure(u.Share) : null, heatText = heat ? TemperatureFigure(degrees?.Celsius) : null;
        string ShareColor() => FigureColor(usage!.Value.Level, _colors.At(item, usage.Value.Level));
        string HeatColor() => FigureColor(heatLevel, _colors.At(item, heatLevel));
        double baseline = Math.Round(center + _font * .36);
        if (shareText is not null && heatText is not null && (_inline & item) != 0)
        {
            var first = MonitorDraw.Text(shareText, _font, ShareColor(), ppd);
            MonitorDraw.Baseline(drawing, first, x, baseline);
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(heatText, _font, HeatColor(), ppd), x + Math.Ceiling(first.WidthIncludingTrailingWhitespace) + InlineGap, baseline);
            return;
        }
        if (shareText is not null && heatText is not null)
        {
            // The baselines sit far enough apart for a gap of about four DIPs between the lines' digits, within the row's height.
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(shareText, _small, ShareColor(), ppd), x, Math.Round(center - 2.5 * _scale));
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(heatText, _small, HeatColor(), ppd), x, Math.Round(center + 8 * _scale));
            return;
        }
        if (shareText is not null) MonitorDraw.Baseline(drawing, MonitorDraw.Text(shareText, _font, ShareColor(), ppd), x, baseline);
        else if (heatText is not null) MonitorDraw.Baseline(drawing, MonitorDraw.Text(heatText, _font, HeatColor(), ppd), x, baseline);
    }

    // A figure keeps the text color while its reading is normal and takes the level's color at the warning and critical
    // levels, as the quota bars' numbers do.
    private static string FigureColor(MemoryLevel level, string color) => level == MemoryLevel.Normal ? UI.Primary : color;

    // An arrow and the rate in three significant digits and a letter: "84.2K", "1.24M".
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, MemoryLevel level, double ppd)
    {
        double size = Math.Round(9 * _scale);
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.4 * _scale), glyph, color);
        var text = MonitorDraw.Text(RateFigure(rate), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + size + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }

    // A drive glyph in the color of the highest level among the figures shown, then each letter's tag, in the tertiary text
    // color, and its figures: "C 45% D 60%", or each share above its drive's temperature. A figure missing for the moment shows
    // a dash.
    private void Drives(DrawingContext drawing, double x, double center, MonitorSnapshot? s, double ppd)
    {
        const CompactMonitorItems item = CompactMonitorItems.Storage;
        var level = MemoryLevel.Normal;
        bool any = false;
        for (int i = 0; i < _letters.Length; i++)
        {
            if (_letterUsage[i] && CompactSources.Share(_letters[i], s) is { } share) { any = true; if (share.Level > level) level = share.Level; }
            if (_letterHeat[i] && CompactSources.Heat(_letters[i], s) is { } heat) { any = true; if (heat.Level > level) level = heat.Level; }
        }
        double size = Glyph, baseline = Math.Round(center + _font * .36);
        if (_driveParts != DriveParts.Letters)
            MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.3 * _scale), MonitorGlyph.Drive, any ? _colors.At(item, level) : UI.Tertiary);
        double at = _driveParts == DriveParts.Letters ? x : x + size + Math.Round(3 * _scale);
        for (int i = 0; i < _letters.Length; i++)
        {
            if (i > 0) at += EntryGap;
            if (_tags[i].Length > 0)
            {
                MonitorDraw.Baseline(drawing, MonitorDraw.Text(_tags[i], _font, UI.Tertiary, ppd), at, baseline);
                at += FigureWidth(_tags[i]) + TagGap;
            }
            var share = _letterUsage[i] ? CompactSources.Share(_letters[i], s) : null;
            var heat = _letterHeat[i] ? CompactSources.Heat(_letters[i], s) : null;
            Figures(drawing, at, center, item, _letterUsage[i] ? (share?.Share, share?.Level ?? MemoryLevel.Normal) : null, _letterHeat[i], heat, ppd);
            // Each letter keeps the room of the widest figures it can show.
            at += Column(item, _letterUsage[i], _letterHeat[i]);
        }
    }

    // The figure of a reading that is a share: "83%", one size, weight and baseline for every reading that has one (the dials' figures,
    // the drives', the battery's).
    private void Figure(DrawingContext drawing, double x, double center, double? share, string color, double ppd)
        => MonitorDraw.Baseline(drawing, MonitorDraw.Text(GaugeFigure(share), _font, color, ppd), x, Math.Round(center + _font * .36));

    // The whole machine's power: a bolt in the theme's curve color, then the draw in watts, "38W"; a dash
    // while there is no reading. The room is kept for the widest figure ("999W"; the font's 1 is narrower than its other digits).
    private void Power(DrawingContext drawing, double x, double center, PowerSample? power, double ppd)
    {
        // The bolt is drawn in a square (PowerIcons draws in the shorter side) over the room kept for it; it is narrower than tall.
        double size = Math.Round(14 * _scale), slot = Math.Round(9 * _scale);
        PowerIcons.Draw(drawing, PowerIcon.Bolt, new Rect(x + Math.Round((slot - size) / 2), Math.Round(center - size / 2), size, size), power is null ? UI.Tertiary : _powerStyle.Draw, _powerStyle, ppd);
        string figure = power is { } p ? PowerFormat.Compact(p.Watts) + "W" : "—";
        MonitorDraw.Baseline(drawing, MonitorDraw.Text(figure, _font, UI.Primary, ppd), x + slot + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }

    // The frame rate: the program's icon (when chosen and it has one) or a screen glyph in the reading's color, then the whole frames
    // a second and "FPS" in the smaller type, "60 FPS"; a dash while there is no reading.
    private void Frames(DrawingContext drawing, double x, double center, FrameSample? frames, string color, double ppd)
    {
        double slot = FramesSlot, baseline = Math.Round(center + _font * .36);
        if (_framesIcon && ProcessIcons.For(frames?.Path) is { } icon)
            drawing.DrawImage(icon, new Rect(x, Math.Round(center - slot / 2), slot, slot));
        else
        {
            double size = Glyph;
            MonitorDraw.Arrow(drawing, new Point(x + (slot - size) / 2, center - size / 2), size, Math.Max(1, 1.3 * _scale), MonitorGlyph.Screen, frames is null ? UI.Tertiary : color);
        }
        double at = x + slot + Math.Round(3 * _scale);
        var figure = MonitorDraw.Text(FramesFigure(frames?.PerSecond), _font, UI.Primary, ppd);
        MonitorDraw.Baseline(drawing, figure, at, baseline);
        MonitorDraw.Baseline(drawing, MonitorDraw.Text(FramesUnit, _small, UI.Tertiary, ppd), at + Math.Ceiling(figure.WidthIncludingTrailingWhitespace) + Math.Round(2 * _scale), baseline);
    }
    private static FrameSample? FrameOf(MonitorSnapshot? s) => s?.Frames.OrDefault;
    private static readonly string[] FrameFigures = [.. Enumerable.Range(0, 1000).Select(count => count.ToString(System.Globalization.CultureInfo.InvariantCulture))];
    private static string FramesFigure(double? perSecond) => perSecond is { } rate && double.IsFinite(rate) ? FrameFigures[(int)Math.Clamp(Math.Round(rate, MidpointRounding.AwayFromZero), 0, 999)] : "—";

    private double BatteryWidth => Math.Round(17 * _scale);
    // The machine's own battery in the shape of the theme's look (PowerGlyphs.Battery), filled to the charge, with the plug in its tip
    // while it is on mains, the leaf while Battery saver is on and an exclamation mark when low; then the figure: "96%".
    private void Charge(DrawingContext drawing, double x, double center, BatterySample? battery, MemoryLevel level, double ppd)
    {
        double height = Math.Round(9 * _scale * ppd) / ppd;
        var visual = battery is null ? new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal) : BatteryVisual.Of(battery, BatteryModel.LowPercent);
        PowerGlyphs.Battery(drawing, new Rect(x, Math.Round((center - height / 2) * ppd) / ppd, BatteryWidth, height), visual, _powerStyle, ppd,
            battery is null ? 0 : battery.Percent / 100, compact: true, unknown: battery is null);
        string color = battery is null ? UI.Tertiary : _powerStyle.Fill(level, battery.State);
        Figure(drawing, x + BatteryWidth + Math.Round(3 * _scale), center, battery is null ? null : battery.Percent / 100, FigureColor(level, color), ppd);
    }

    // For screen readers (the row's readings are drawn, not controls): the readings the row shows, named for what they are.
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, Spoken);
    private string Spoken()
    {
        var parts = new List<string>();
        var s = _snapshot;
        foreach (var item in _orderOnce)
        {
            if ((Shown & item) == 0 || !CompactMonitor.Order.Contains(item)) continue;
            switch (item)
            {
                case CompactMonitorItems.Cpu when s?.Cpu.OrDefault is { } cpu: parts.Add("CPU " + MonitorFormat.Percent(cpu.Usage) + "%"); break;
                case CompactMonitorItems.Memory when s?.Memory.OrDefault is { } memory: parts.Add(Loc.T("内存 ", "Memory ") + MonitorFormat.Percent(memory.UsedFraction) + "%"); break;
                case CompactMonitorItems.Gpu when s?.Gpu.OrDefault is { NoReadings: false } gpu: parts.Add("GPU " + MonitorFormat.Percent(gpu.Usage) + "%"); break;
                case CompactMonitorItems.Download when s?.Network.OrDefault is { } down: parts.Add(Loc.T("下载 ", "Download ") + MonitorFormat.Rate(down.DownBytesPerSecond).Text); break;
                case CompactMonitorItems.Upload when s?.Network.OrDefault is { } up: parts.Add(Loc.T("上传 ", "Upload ") + MonitorFormat.Rate(up.UpBytesPerSecond).Text); break;
                case CompactMonitorItems.Power when s?.Power.OrDefault is { } power:
                    parts.Add(Loc.T("整机功耗 ", "System power ") + PowerText.Watts(power.Watts) + Loc.T("，", ", ") + (power.Estimated ? Loc.T("估算", "estimated") : Loc.T("实测", "measured"))); break;
                case CompactMonitorItems.Frames when FrameOf(s) is { } frames: parts.Add(Loc.T("帧率 ", "Frame rate ") + FramesFigure(frames.PerSecond) + " FPS" + Loc.T("，", ", ") + frames.Program); break;
                case CompactMonitorItems.Charge when s?.Battery.OrDefault is { } own:
                    parts.Add(Loc.T("本机电池 ", "This PC's battery ") + PowerText.Percent(own.Percent) + Loc.T("，", ", ") + PowerText.State(own) + LevelWords(BatteryModel.Level(own, BatteryModel.LowPercent))); break;
            }
        }
        return string.Join(Loc.T("；", "; "), parts);
    }
    private static string LevelWords(MemoryLevel level) => level switch
    { MemoryLevel.Critical => Loc.T("，低电量", ", low"), MemoryLevel.Elevated => Loc.T("，电量偏低", ", getting low"), _ => "" };
}
