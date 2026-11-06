using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// The system monitor's choices beyond its colors. All are saved as numbers, so new members go at the end.
// MonitorMain: the gauge the page shows large; the other one shows small beside it, and a click swaps them.
public enum MonitorMain { Cpu, Gpu }
// MonitorProcessView: Auto shows the detailed rows (CPU, memory and the number of processes) where the list is wide enough.
public enum MonitorProcessView { Auto, Compact, Detailed }
// CompactMonitorMode: what the notch or capsule shows of the system, after ftop's menu bar strip and corner badges.
// Full is every reading as a strip; Brief is three ring gauges; Custom is the readings chosen in CompactMonitorItems.
// Since the table of readings (CompactReading) these are its presets: Full and Brief set which readings show and which rings
// carry their figure, and Custom is any other setting.
public enum CompactMonitorMode { Full, Brief, Custom }
// CompactSmoothing: how many frames a second the collapsed readings' dials and core columns are drawn while they ease to a new
// reading; Off shows each reading at once. The values are the frames a second.
public enum CompactSmoothing { Off = 0, Ten = 10, Thirty = 30 }

[Flags]
public enum CompactMonitorItems
{
    None = 0, Columns = 1, Cpu = 2, Memory = 4, Gpu = 8, Download = 16, Upload = 32,
    // The usage and rate readings, as the old Full mode showed them. Storage came later and is not among them (CompactMonitor.Every
    // is every reading, as the Full preset shows them). Temperature was a reading of its own before the temperatures
    // joined CPU, GPU and the drives (CompactFigures); never released, it is dropped from saved tables and its number not reused.
    // The core columns keep their number for older files but are no longer offered: the row draws the volume's rhythm instead.
    All = Cpu | Memory | Gpu | Download | Upload,
    Temperature = 64, Storage = 128,
    // The whole machine's power draw in watts (PowerResolver) and the machine's own battery (BatteryModel): outside the presets like
    // Storage. 256 is left unused.
    Power = 512, Charge = 1024,
    // The frame rate of the program in front (FrameRates): one figure for every panel. Outside the presets too.
    Frames = 2048
}

// One reading of the collapsed strip as set in the settings' table; its place there is its place in the strip, left to right.
// ShowValue: a dial (CPU, memory, GPU) shows its percentage after it, or, off, the dial alone with its letter inside, which is
// narrower; the core columns carry no figure and the rates and the temperature are always text, so it means nothing for them.
// The readings take their colors from the color scheme (CompactMonitor.Roles), as the expanded page does, and at the warning
// and critical levels the collapsed readings' level colors (DeckSettings.CompactWarningColor). Color, ExtraColors,
// WarningColor and CriticalColor are the reading's own colors from before the custom scheme: "#RRGGBB" in place of the
// scheme's in the normal state, for the further channels in the normal state (the core columns' slowest kind of core), and at
// the warning and critical levels. They are kept to read older files, which DeckSettings.Normalize moves into the custom
// scheme and the level colors (CompactMonitor.MoveColors), and are not written.
// Figures: what CPU, GPU and the drives show after their glyph, the share in use and the temperature (CompactFigures), and what the
// frame rate shows of its program, its icon or not (CompactFrames); Sources:
// the drive letters the drives show (CompactSources). Both chosen in the table's menus, null for the default. FiguresInLine:
// both figures side by side in the row's type rather than one above the other in two smaller lines.
public sealed class CompactReading
{
    public CompactMonitorItems Item { get; set; }
    public bool Visible { get; set; } = true;
    public bool ShowValue { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Figures { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FiguresInLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Sources { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string?>? ExtraColors { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WarningColor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CriticalColor { get; set; }
    [JsonIgnore] public bool HasOwnColors => Color is not null || WarningColor is not null || CriticalColor is not null || ExtraColors?.Any(color => color is not null) == true;
    public void ResetColors() { Color = WarningColor = CriticalColor = null; ExtraColors = null; }
    public CompactReading Copy()
    {
        var copy = (CompactReading)MemberwiseClone();
        copy.ExtraColors = ExtraColors?.ToList();
        copy.Figures = Figures?.ToList();
        copy.Sources = Sources?.ToList();
        return copy;
    }
}

// What the collapsed row shows of the system and how it gives way when the row is short: whole readings leave one by one,
// the least telling first, so what stays is never cut through.
public static class CompactMonitor
{
    // Left to right by default; the settings' table can reorder them. The core columns are no longer drawn: the row shows the
    // volume's rhythm after the GPU instead (MonitorStrip.Audio), so the reading is left out of the table and of every preset.
    public static readonly IReadOnlyList<CompactMonitorItems> Order =
        [CompactMonitorItems.Cpu, CompactMonitorItems.Memory, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Upload,
            CompactMonitorItems.Storage, CompactMonitorItems.Power, CompactMonitorItems.Charge, CompactMonitorItems.Frames];
    public const CompactMonitorItems Every = CompactMonitorItems.All | CompactMonitorItems.Storage | CompactMonitorItems.Power | CompactMonitorItems.Charge | CompactMonitorItems.Frames;
    // When the row is too short, readings leave in this order.
    public static readonly IReadOnlyList<CompactMonitorItems> LeaveOrder =
        [CompactMonitorItems.Frames, CompactMonitorItems.Power, CompactMonitorItems.Storage, CompactMonitorItems.Charge, CompactMonitorItems.Upload, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Memory, CompactMonitorItems.Cpu];

    // The readings with a figure that can be hidden.
    public const CompactMonitorItems Rings = CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu;
    public static bool HasValueChoice(CompactMonitorItems item) => item is CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu;
    // The colors a reading has in its normal state: the core columns one for the fastest and one for the slowest kind of core,
    // the others one. Every reading has besides the collapsed readings' warning and critical colors (ReadingLevels).
    public static int Channels(CompactMonitorItems item) => Roles.TryGetValue(item, out var roles) ? roles.Length : 1;
    // The color scheme's color of each channel of a reading in its normal state.
    public static readonly IReadOnlyDictionary<CompactMonitorItems, MonitorRole[]> Roles = new Dictionary<CompactMonitorItems, MonitorRole[]>
    {
        [CompactMonitorItems.Columns] = [MonitorRole.Cpu, MonitorRole.Efficiency], [CompactMonitorItems.Cpu] = [MonitorRole.Cpu],
        [CompactMonitorItems.Memory] = [MonitorRole.Memory], [CompactMonitorItems.Gpu] = [MonitorRole.Gpu],
        [CompactMonitorItems.Download] = [MonitorRole.Download], [CompactMonitorItems.Upload] = [MonitorRole.Upload],
        // The drives take the memory color unless the custom scheme gives them their own, as the page draws its bars. Power and the
        // machine's battery are drawn in the theme's power style (PowerLook, whose curve takes the CPU color) unless the custom scheme
        // gives them their own.
        [CompactMonitorItems.Storage] = [MonitorRole.Storage],
        [CompactMonitorItems.Power] = [MonitorRole.Power], [CompactMonitorItems.Charge] = [MonitorRole.Battery],
        // The frame rate takes the GPU color unless the custom scheme gives it its own.
        [CompactMonitorItems.Frames] = [MonitorRole.Frames]
    };
    public static MonitorRole Role(CompactMonitorItems item, int channel = 0) =>
        Roles.TryGetValue(item, out var roles) ? roles[Math.Clamp(channel, 0, roles.Length - 1)] : MonitorRole.Cpu;

    // Settings saved before the custom scheme gave the collapsed readings their own colors; they leave the readings. Normal-state
    // colors become the custom scheme's over the scheme in use, which the readings followed otherwise: the CPU color is the CPU
    // reading's, else the core columns' fastest. Warning and critical colors become the collapsed readings' level colors: the
    // first a reading sets, in the table's order.
    public static void MoveColors(DeckSettings settings)
    {
        var readings = settings.CompactReadings;
        if (!readings.Any(reading => reading.HasOwnColors)) return;
        var moved = settings.MonitorPalette == MonitorPalette.Custom ? settings.MonitorCustomColors.Copy() : new MonitorCustomColors { Base = settings.MonitorPalette };
        CompactReading? Of(CompactMonitorItems item) => readings.FirstOrDefault(reading => reading.Item == item);
        void Take(MonitorRole role, string? color) { if (color is not null) moved.Set(role, color); }
        Take(MonitorRole.Cpu, Of(CompactMonitorItems.Cpu)?.Color ?? Of(CompactMonitorItems.Columns)?.Color);
        Take(MonitorRole.Efficiency, Of(CompactMonitorItems.Columns)?.ExtraColors?.FirstOrDefault());
        Take(MonitorRole.Memory, Of(CompactMonitorItems.Memory)?.Color);
        Take(MonitorRole.Gpu, Of(CompactMonitorItems.Gpu)?.Color);
        Take(MonitorRole.Download, Of(CompactMonitorItems.Download)?.Color);
        Take(MonitorRole.Upload, Of(CompactMonitorItems.Upload)?.Color);
        if (moved.Changed) { settings.MonitorCustomColors = moved; settings.MonitorPalette = MonitorPalette.Custom; }
        settings.CompactWarningColor ??= readings.Select(reading => reading.WarningColor).FirstOrDefault(color => color is not null);
        settings.CompactCriticalColor ??= readings.Select(reading => reading.CriticalColor).FirstOrDefault(color => color is not null);
        foreach (var reading in readings) reading.ResetColors();
    }

    // What an old mode showed (settings saved before the table; see Readings).
    public static CompactMonitorItems Items(CompactMonitorMode mode, CompactMonitorItems custom) => mode switch
    {
        CompactMonitorMode.Full => CompactMonitorItems.All,
        CompactMonitorMode.Brief => CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu,
        _ => (custom & CompactMonitorItems.All) is var chosen && chosen != CompactMonitorItems.None ? chosen : CompactMonitorItems.Cpu
    };

    // The table for settings saved before it: Full shows every reading with its figure, Brief the three rings alone, Custom the
    // chosen readings with their figures. All in the default order, following the palette.
    public static List<CompactReading> Readings(CompactMonitorMode mode, CompactMonitorItems custom)
    {
        var shown = Items(mode, custom);
        return [.. Order.Select(item => new CompactReading { Item = item, Visible = (shown & item) != 0, ShowValue = mode != CompactMonitorMode.Brief })];
    }

    // Every reading once, in the saved order: unknown and repeated readings are dropped, missing ones join hidden at the end,
    // a color that is not "#RRGGBB" follows the palette, and colors for channels a reading does not have are dropped.
    public static List<CompactReading> Normalize(IEnumerable<CompactReading?>? readings)
    {
        static string? Valid(string? color) => AppPresets.NormalizeColor(color) is { Length: > 0 } valid ? valid : null;
        static List<string?>? Extra(CompactReading reading)
        {
            var extra = Enumerable.Range(0, Channels(reading.Item) - 1).Select(index => Valid(reading.ExtraColors?.ElementAtOrDefault(index))).ToList();
            return extra.Any(color => color is not null) ? extra : null;
        }
        var list = (readings ?? []).OfType<CompactReading>().Where(reading => Order.Contains(reading.Item)).DistinctBy(reading => reading.Item)
            .Select(reading => new CompactReading { Item = reading.Item, Visible = reading.Visible, ShowValue = reading.ShowValue, Color = Valid(reading.Color),
                ExtraColors = Extra(reading), WarningColor = Valid(reading.WarningColor), CriticalColor = Valid(reading.CriticalColor),
                Figures = reading.Item == CompactMonitorItems.Frames ? CompactFrames.Normalize(reading.Figures) : CompactFigures.Normalize(reading.Item, reading.Figures), FiguresInLine = CompactFigures.Has(reading.Item), Sources = CompactSources.Normalize(reading.Item, reading.Sources) }).ToList();
        list.AddRange(Order.Where(item => list.All(reading => reading.Item != item)).Select(item => new CompactReading { Item = item, Visible = false }));
        return list;
    }

    public static CompactMonitorItems Visible(IEnumerable<CompactReading> readings) =>
        readings.Where(reading => reading.Visible).Aggregate(CompactMonitorItems.None, (items, reading) => items | reading.Item) & Every;
    // The rings set to show their figure, shown or not.
    public static CompactMonitorItems Values(IEnumerable<CompactReading> readings) =>
        readings.Where(reading => reading.ShowValue).Aggregate(CompactMonitorItems.None, (items, reading) => items | reading.Item) & Rings;
    // What the strip shows: with every reading switched off, CPU, as the old Custom choice did.
    public static CompactMonitorItems Shown(IEnumerable<CompactReading> readings) => Visible(readings) is var shown && shown != CompactMonitorItems.None ? shown : CompactMonitorItems.Cpu;

    // The preset the table matches exactly: Full is every reading with every figure (the rings' figures, the share and the
    // temperature of CPU, GPU and the drives, the frame rate's program icon), Brief the three rings alone; else none, and the
    // table itself is the custom choice (saved as Custom in the old key).
    public static CompactMonitorMode? Preset(IEnumerable<CompactReading> readings)
    {
        var all = readings.ToList();
        var (shown, values) = (Visible(all), Values(all));
        bool figures = all.All(reading => (!CompactFigures.Has(reading.Item) || CompactFigures.Of(reading).Count == FullFigures.Length)
            && (reading.Item != CompactMonitorItems.Frames || CompactFrames.Shows(reading, CompactFrames.Icon)));
        return shown == Every && values == Rings && figures ? CompactMonitorMode.Full
            : shown == Rings && values == CompactMonitorItems.None ? CompactMonitorMode.Brief : null;
    }
    private static readonly string[] FullFigures = [CompactFigures.Usage, CompactFigures.Temperature];

    // Sets which readings show and which figures they carry as the preset has them: Full shows every reading with every figure,
    // Brief the three rings without figures; the order, the colors and the drive letters stay. Custom (no preset) changes nothing.
    public static void Apply(IEnumerable<CompactReading> readings, CompactMonitorMode preset)
    {
        if (preset == CompactMonitorMode.Custom) return;
        bool full = preset == CompactMonitorMode.Full;
        foreach (var reading in readings)
        {
            bool ring = HasValueChoice(reading.Item);
            reading.Visible = full || ring;
            if (ring) reading.ShowValue = full;
            if (!full) continue;
            if (CompactFigures.Has(reading.Item)) reading.Figures = [.. FullFigures];
            if (reading.Item == CompactMonitorItems.Frames) reading.Figures = null;
        }
    }

    // The width of each reading: a ring with its figure (the ring and the room after it) or alone, the core columns, a rate, the
    // drives, power, the battery, the frame rate. figure: the room after a ring, by ring (a ring showing its temperature too takes more).
    public static Func<CompactMonitorItems, double> Widths(CompactMonitorItems values, double ring, Func<CompactMonitorItems, double> figure, double bareRing, double columns, double rate, double storage = 0, double power = 0, double charge = 0, double frames = 0) => item => item switch
    {
        CompactMonitorItems.Columns => columns,
        CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => (values & item) != 0 ? ring + figure(item) : bareRing,
        CompactMonitorItems.Download or CompactMonitorItems.Upload => rate,
        CompactMonitorItems.Storage => storage,
        CompactMonitorItems.Power => power,
        CompactMonitorItems.Charge => charge,
        CompactMonitorItems.Frames => frames,
        _ => 0
    };
    // Rings alone, as Brief showed them, sit closer together.
    public static bool RingsOnly(CompactMonitorItems shown, CompactMonitorItems values) => shown != CompactMonitorItems.None && (shown & ~Rings) == 0 && (shown & values) == 0;

    // The shown readings left to right in the order given, each with its left edge from the start of the strip. Each takes its
    // width and the gap after it in proportion to how present it is (1 when settled), so the row closes up smoothly as one
    // leaves; Total is the width they take together.
    public static (List<(CompactMonitorItems Item, double X, double Width, double Presence)> Items, double Total) Arrange(IEnumerable<CompactMonitorItems> order,
        CompactMonitorItems shown, Func<CompactMonitorItems, double> width, double gap, Func<CompactMonitorItems, double> presence)
    {
        var items = new List<(CompactMonitorItems, double, double, double)>();
        return (items, Arrange([.. order.Distinct()], shown, width, gap, presence, items));
    }

    // The same into a list the caller keeps (cleared first), returning Total: the strip arranges its readings on every redraw,
    // and allocates nothing for it. The order must name each item once.
    public static double Arrange(IReadOnlyList<CompactMonitorItems> order, CompactMonitorItems shown, Func<CompactMonitorItems, double> width, double gap,
        Func<CompactMonitorItems, double> presence, List<(CompactMonitorItems Item, double X, double Width, double Presence)> items)
    {
        items.Clear();
        double x = 0, last = 0;
        for (int i = 0; i < order.Count; i++)
        {
            var item = order[i];
            if ((shown & item) == 0 || !Order.Contains(item)) continue;
            double p = Math.Clamp(presence(item), 0, 1), w = Math.Max(0, width(item));
            if (p <= 0) continue;
            items.Add((item, x, w, p));
            x += (w + gap) * p; last = p;
        }
        return Math.Max(0, x - gap * last);
    }

    // The width of the items side by side with this gap between them.
    public static double Width(CompactMonitorItems items, Func<CompactMonitorItems, double> width, double gap)
    {
        double total = 0; int count = 0;
        // By index: asked on every reading of a scrolling strip, and an interface's enumerator would be allocated each time.
        for (int i = 0; i < Order.Count; i++)
        {
            var item = Order[i];
            if ((items & item) == 0) continue;
            total += Math.Max(0, width(item)); count++;
        }
        return count == 0 ? 0 : total + (count - 1) * Math.Max(0, gap);
    }

    // The items that fit in the space, dropping whole readings in LeaveOrder; None when not even one fits.
    public static CompactMonitorItems Fit(CompactMonitorItems items, Func<CompactMonitorItems, double> width, double gap, double available)
    {
        if (!double.IsFinite(available) || available <= 0) return CompactMonitorItems.None;
        var shown = items & Every;
        foreach (var item in LeaveOrder)
        {
            if (Width(shown, width, gap) <= available + 1e-9) return shown;
            shown &= ~item;
        }
        return Width(shown, width, gap) <= available + 1e-9 ? shown : CompactMonitorItems.None;
    }

}

// The colors of the collapsed readings at each level (ReadingLevels), from the color scheme. The normal state has a color per
// channel (CompactMonitor.Roles: the core columns one for the fastest and one for the slowest kind of core); the warning and
// critical states the collapsed readings' warning and critical colors.
public sealed class StripColors(MonitorColors palette)
{
    public MonitorColors Palette { get; } = palette;

    public string Normal(CompactMonitorItems item, int channel = 0) => Palette[CompactMonitor.Role(item, channel)];
    public string At(CompactMonitorItems item, MemoryLevel level, int channel = 0) => Palette.Level(level, Normal(item, channel));
    public string Memory(MemoryLevel level) => At(CompactMonitorItems.Memory, level);

    // A core column's color: in the normal state the kinds of core run from the CPU color, for the fastest kind, to the
    // efficiency color in even steps; at the warning and critical levels the scheme's color for that level.
    public string Core(int group, int groups, MemoryLevel level = MemoryLevel.Normal) => Palette.Level(level, Palette.Core(group, groups));
}

// Widths on whole device pixels, for a DIP width and the device pixels per DIP (1.5 at 150 %). The collapsed row rounds its
// regions down (Down); the readings ask for their width rounded up (Up), so that rounding never takes a reading away.
public static class DevicePixels
{
    public static double Down(double value, double dpi) => double.IsFinite(value) && value > 0 && dpi > 0 ? Math.Floor(value * dpi + .001) / dpi : 0;
    public static double Up(double value, double dpi) => !double.IsFinite(value) || value <= 0 ? 0 : double.IsFinite(dpi) && dpi > 0 ? Math.Ceiling(value * dpi - .001) / dpi : value;
}
