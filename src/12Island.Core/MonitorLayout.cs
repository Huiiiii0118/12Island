using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// The system monitor page's color schemes: ftop's three palettes by Nongfsq (https://github.com/Nongfsq/ftop, MIT License;
// see ThirdPartyNotices.txt), Theme, which takes the colors of the panel theme in use (the app resolves it, as only it knows
// the theme), and Custom, a scheme of the user's own (MonitorCustomColors). Saved as numbers, so new members go at the end.
public enum MonitorPalette { Sea, Graphite, Warm, Theme, Custom }

// The colors of a scheme, in the order the settings list them: Cpu the CPU readings and the fastest cores, Efficiency the
// slowest cores (the kinds between take the colors between), then memory, GPU, download and upload, then the drives, the whole
// machine's power and its own battery, and the collapsed row's frame rate. The collapsed readings and the expanded page take the
// same ones. Saved by name in the custom scheme; new members go at the end.
public enum MonitorRole { Cpu, Efficiency, Memory, Gpu, Download, Upload, Storage, Power, Battery, Frames }

// The colors of one of ftop's palettes on a dark or a light panel theme. Theme and Custom have no fixed colors and give Sea's
// here. ftop's palettes have three main colors; memory and upload take the efficiency color, download the performance color.
// Warning and Critical are not part of a scheme: they are the collapsed readings' colors at those levels (ReadingLevels),
// which the app takes from the settings or the panel theme; For gives fixed ones.
public sealed record MonitorColors(string Performance, string Efficiency, string Memory, string Gpu, string Download, string Upload, string Warning, string Critical)
{
    public static MonitorColors For(MonitorPalette palette, bool dark = true)
    {
        var (performance, efficiency, gpu) = (palette, dark) switch
        {
            (MonitorPalette.Graphite, true) => ("#F3F4F6", "#9AA1AD", "#C9B98F"),
            (MonitorPalette.Graphite, false) => ("#1F2937", "#6B7280", "#8A6D2F"),
            (MonitorPalette.Warm, true) => ("#FF8468", "#FFC857", "#FF8FC4"),
            (MonitorPalette.Warm, false) => ("#DC4A2B", "#B9770A", "#B83280"),
            (_, true) => ("#5B9BFF", "#45D1BD", "#B294FF"),
            _ => ("#1F5FE0", "#0B8F7E", "#7A4FD6")
        };
        var (warning, critical) = dark ? ("#F0B54A", "#FF6B5E") : ("#B26A00", "#D23F31");
        return new(performance, efficiency, efficiency, gpu, performance, efficiency, warning, critical);
    }

    // The Theme scheme from a theme's colors (opaque "#RRGGBB"): the accent for the fastest cores, and for the slowest cores and
    // the GPU the first of the theme's other colors (cyan, pink, lime, amber in that order) that is clearly not a color already
    // taken, so a theme whose accent is its cyan still tells the kinds of core apart. Memory, download and upload as in For.
    public static MonitorColors FromTheme(string accent, IReadOnlyList<string> others, string warning, string critical)
    {
        const double Apart = 64;
        string Pick(params string[] taken) => others.FirstOrDefault(color => taken.All(other => ColorMath.Distance(color, other) >= Apart))
            ?? others.Where(color => !taken.Contains(color, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty(taken[^1]).First();
        string efficiency = Pick(accent), gpu = Pick(accent, efficiency);
        return new(accent, efficiency, efficiency, gpu, accent, efficiency, warning, critical);
    }

    // The drives', the power curve's, the machine battery's and the frame rate's own colors, which only a custom scheme gives; null
    // otherwise. Without one the drives' bars take the memory color (both are space in use), power and the battery the panel theme's
    // power style (the app resolves it, as only it knows the theme), and the frame rate the GPU color.
    public string? Drives { get; init; }
    public string? Power { get; init; }
    public string? Battery { get; init; }
    public string? FrameRate { get; init; }
    public string Storage => Drives ?? Memory;

    // Power and the battery fall back on the CPU color here; the app draws them by the theme's power style, whose curve takes the
    // CPU color too.
    public string this[MonitorRole role] => role switch
    {
        MonitorRole.Efficiency => Efficiency, MonitorRole.Memory => Memory, MonitorRole.Gpu => Gpu, MonitorRole.Download => Download,
        MonitorRole.Upload => Upload, MonitorRole.Storage => Storage, MonitorRole.Power => Power ?? Performance, MonitorRole.Battery => Battery ?? Performance,
        MonitorRole.Frames => FrameRate ?? Gpu, _ => Performance
    };
    public MonitorColors With(MonitorRole role, string color) => role switch
    {
        MonitorRole.Efficiency => this with { Efficiency = color }, MonitorRole.Memory => this with { Memory = color }, MonitorRole.Gpu => this with { Gpu = color },
        MonitorRole.Download => this with { Download = color }, MonitorRole.Upload => this with { Upload = color }, MonitorRole.Storage => this with { Drives = color },
        MonitorRole.Power => this with { Power = color }, MonitorRole.Battery => this with { Battery = color }, MonitorRole.Frames => this with { FrameRate = color },
        _ => this with { Performance = color }
    };
    // Every color as drawn (the app moves them to read on the panel theme).
    public MonitorColors Map(Func<string, string> drawn) =>
        new(drawn(Performance), drawn(Efficiency), drawn(Memory), drawn(Gpu), drawn(Download), drawn(Upload), drawn(Warning), drawn(Critical))
        {
            Drives = Drives is null ? null : drawn(Drives), Power = Power is null ? null : drawn(Power), Battery = Battery is null ? null : drawn(Battery),
            FrameRate = FrameRate is null ? null : drawn(FrameRate)
        };

    // Core groups run from the performance color to the efficiency color in even steps, so a processor with any number of
    // kinds of core has a color for each.
    public string Core(int group, int groups) => ColorMath.Mix(Performance, Efficiency, groups > 1 ? Math.Clamp(group, 0, groups - 1) / (double)(groups - 1) : 0);
    // A collapsed reading's color at a level: its own color in the normal state, the warning or critical color above it.
    public string Level(MemoryLevel level, string normal) => level switch { MemoryLevel.Critical => Critical, MemoryLevel.Elevated => Warning, _ => normal };

    public static string Name(MonitorPalette palette) => palette switch
    {
        MonitorPalette.Graphite => Loc.T("石墨", "Graphite"), MonitorPalette.Warm => Loc.T("暖色", "Warm"), MonitorPalette.Theme => Loc.T("跟随主题", "Theme"),
        MonitorPalette.Custom => Loc.T("自定义", "Custom"), _ => Loc.T("海洋", "Sea")
    };
}

// The Custom scheme: the preset it starts from (Base, never Custom) and the colors changed from it, "#RRGGBB" each, or null to
// follow the base. The colors not changed keep following the base, so they still suit a light or a dark panel theme.
public sealed class MonitorCustomColors
{
    public MonitorPalette Base { get; set; } = MonitorPalette.Theme;
    public string? Cpu { get; set; }
    public string? Efficiency { get; set; }
    public string? Memory { get; set; }
    public string? Gpu { get; set; }
    public string? Download { get; set; }
    public string? Upload { get; set; }
    public string? Storage { get; set; }
    public string? Power { get; set; }
    public string? Battery { get; set; }
    public string? Frames { get; set; }

    public string? Get(MonitorRole role) => role switch
    {
        MonitorRole.Efficiency => Efficiency, MonitorRole.Memory => Memory, MonitorRole.Gpu => Gpu, MonitorRole.Download => Download,
        MonitorRole.Upload => Upload, MonitorRole.Storage => Storage, MonitorRole.Power => Power, MonitorRole.Battery => Battery,
        MonitorRole.Frames => Frames, _ => Cpu
    };
    public void Set(MonitorRole role, string? color)
    {
        switch (role)
        {
            case MonitorRole.Efficiency: Efficiency = color; break;
            case MonitorRole.Memory: Memory = color; break;
            case MonitorRole.Gpu: Gpu = color; break;
            case MonitorRole.Download: Download = color; break;
            case MonitorRole.Upload: Upload = color; break;
            case MonitorRole.Storage: Storage = color; break;
            case MonitorRole.Power: Power = color; break;
            case MonitorRole.Battery: Battery = color; break;
            case MonitorRole.Frames: Frames = color; break;
            default: Cpu = color; break;
        }
    }
    // Whether any color differs from the base's.
    [JsonIgnore] public bool Changed => Enum.GetValues<MonitorRole>().Any(role => Get(role) is not null);
    // The base's colors with the changed ones over them, each as drawn (by default as given).
    public MonitorColors Over(MonitorColors colors, Func<string, string>? drawn = null) =>
        Enum.GetValues<MonitorRole>().Aggregate(colors, (result, role) => Get(role) is { } own ? result.With(role, drawn is null ? own : drawn(own)) : result);
    // What the expanded page is built from (part of its key).
    [JsonIgnore] public string Key => string.Join(",", Enum.GetValues<MonitorRole>().Select(Get).Prepend(Base.ToString()));
    public MonitorCustomColors Copy() => (MonitorCustomColors)MemberwiseClone();
    // The base is a fixed scheme or Theme; a color that is not "#RRGGBB" follows the base.
    public void Normalize()
    {
        if (!Enum.IsDefined(Base) || Base == MonitorPalette.Custom) Base = MonitorPalette.Theme;
        foreach (var role in Enum.GetValues<MonitorRole>())
            Set(role, AppPresets.NormalizeColor(Get(role)) is { Length: > 0 } valid ? valid : null);
    }
}

public enum ListPlacement { None, Side, Below }
// How memory and network are shown: both on one line, one line each, or memory as a block with its bar and commit line under the
// figure while network keeps its one line.
public enum MetricsMode { Line, Lines, Blocks }

// How the system monitor page fills the space it has, after ftop's layout ladder: a small page keeps the essentials, a
// large one shows more rather than bigger type. Sizes are DIPs of the page's content area, below its header.
// The ladder has steps only: within a step the page stretches (the list's share, its rows and columns, the graph's height), so
// resizing the panel by dragging rebuilds the page only where it crosses a step.
// Metrics: how much room memory and network take (MetricsMode). The core columns carry no figures at any size: pointing at one
// gives them, so the page stays light to read.
public sealed record MonitorLayout(double Figure, double Body, double Caption, MetricsMode Metrics, ListPlacement List, double RowHeight)
{
    public bool CompactMetrics => Metrics == MetricsMode.Line;
    public bool Processes => List != ListPlacement.None;
    // The most rows one column of the list shows, however tall the page, and the most columns a wide list has.
    public const int MaximumRows = 14, MaximumColumns = 2;
    // Fewer rows than this are not worth a list.
    public const int MinimumRows = 2;
    // The list's own header: its title and the sort switch.
    public const double ListHead = 26;
    // The readings keep this much height when the list sits below them.
    public const double StackedReadings = 170;
    // A side list takes this share of the width, within these bounds; a list this wide or wider has two columns.
    public const double ListShare = .42, ListMinimum = 220, ListMaximum = 560, TwoColumns = 470;

    // The drives' lines (Storage): an entry per volume, in two columns where the readings are StorageTwoColumns wide, else one;
    // as many lines as the entries fill, at most three, while the core columns keep MinimumGraph of height under what memory and
    // network take at this size. The rest share the last entry, and a page too short for one line has none. Memory and network
    // keep the room they have without drives, so their sparklines stay as wide as before.
    public int StorageLines { get; init; }
    public int StorageColumns { get; init; }
    // Two columns of 215 DIP and the gap between them: the letter, the figures and the temperature, with a bar of about 45.
    public const double MinimumGraph = 40, StorageTwoColumns = 450;
    public const int MaximumStorageLines = 3, MaximumStorageColumns = 2;
    // A drive line's height: tighter than the list's rows, so the lines sit close together.
    public double StorageRow => StorageRowFor(Body);
    private static double StorageRowFor(double body) => Math.Ceiling(body * 1.5);
    public static int StorageRoom(double readings, MetricsMode metrics, double figure, double body)
    {
        var (head, margins, rest, storageMargin) = Fixed(metrics, figure, body);
        return Math.Clamp((int)Math.Floor((readings - head - margins - rest - storageMargin - MinimumGraph) / StorageRowFor(body)), 0, MaximumStorageLines);
    }

    // The least height for memory and network on a line each: 150, or what a drive's line under them needs.
    private static double LinesFrom(double figure, double body)
    {
        var (head, margins, rest, storageMargin) = Fixed(MetricsMode.Lines, figure, body);
        return Math.Max(150, head + margins + rest + storageMargin + MinimumGraph + StorageRowFor(body));
    }

    // The lines under the drives for the whole machine's power draw and its own battery (PowerBlock): 2 lines when both have one, 1 when
    // only power has, or when the page has room for one line only (power and battery then share it). The first line comes before the
    // drives' lines, the second after them, with the core columns keeping MinimumGraph; a page without room for a line has none, and a
    // page that asks for no power is laid out exactly as before.
    public int PowerLines { get; init; }
    // The line under memory and network for the frame rate of the program in front (FramesBlock): before the drives,
    // which give up room for it, and after the first power line; a page without room for it has none, and a page that asks for no
    // frame rates is laid out exactly as before.
    public bool FrameLine { get; init; }
    public static int PowerRoom(double readings, MetricsMode metrics, double figure, double body, int storageLines)
    {
        var (head, margins, rest, storageMargin) = Fixed(metrics, figure, body);
        double row = StorageRowFor(body);
        double drives = storageLines > 0 ? storageMargin + storageLines * row : 0;
        return Math.Clamp((int)Math.Floor((readings - head - margins - rest - drives - storageMargin - MinimumGraph) / row), 0, 2);
    }

    // The heights MonitorView gives the readings' parts: the main gauge's line, the graph's margins, memory and network, and the
    // drives' top margin. The graph takes what is left.
    private static (double Head, double Margins, double Metrics, double StorageMargin) Fixed(MetricsMode metrics, double figure, double body)
    {
        double line = Math.Ceiling(body * 1.7);
        return (Math.Ceiling(figure * 1.4), metrics == MetricsMode.Line ? 10 : 16, metrics switch
        {
            MetricsMode.Line => line,
            MetricsMode.Lines => 2 * line + 6,
            // Memory's block (the figure, the bar and the commit line), then network on one line (NetworkBlock).
            _ => Math.Ceiling(body * 1.7 + 19) + Math.Ceiling(body * 1.7) + 10
        }, metrics switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 });
    }

    // The readings' parts in a column this tall (foot: the room left under them, FootAlignment): the main gauge's line, the graph,
    // memory and network together, the drives' lines, the power lines, and last the frame rate's line, which sits between memory and
    // network and the drives; each (Top, Height), as MonitorView lays them out.
    public IReadOnlyList<(double Top, double Height)> Parts(double readings, double foot = 0)
    {
        var (head, margins, metrics, storageMargin) = Fixed(Metrics, Figure, Body);
        double frames = FrameLine ? storageMargin + StorageRow : 0;
        double storage = StorageLines > 0 ? storageMargin + StorageLines * StorageRow : 0;
        double power = PowerLines > 0 ? storageMargin + PowerLines * StorageRow : 0;
        double graph = Math.Max(16, readings - foot - head - margins - metrics - frames - storage - power);
        double graphTop = head + (Metrics == MetricsMode.Line ? 4 : 6);
        double metricsTop = head + margins + graph, framesTop = metricsTop + metrics;
        return [(0, head), (graphTop, graph), (metricsTop, metrics), (framesTop + frames, storage), (framesTop + frames + storage, power), (framesTop, frames)];
    }

    // How far past a step a resize must go before the page takes the next step, so a size held at a step does not flip back and
    // forth between two pages. Within the band the page keeps the step it has and stretches, as it does within any step.
    public const double Hysteresis = 12;

    // The layout for this size, keeping previous (the step the page shows) while the size is within Hysteresis of it.
    public static MonitorLayout For(double width, double height, bool processes, int storage, MonitorLayout? previous, int power = 0, bool frames = false)
    {
        var next = For(width, height, processes, storage, power, frames);
        if (previous is null || next == previous) return next;
        foreach (double dw in new[] { 0, -Hysteresis, Hysteresis })
            foreach (double dh in new[] { 0, -Hysteresis, Hysteresis })
                if (For(width + dw, height + dh, processes, storage, power, frames) == previous) return previous;
        return next;
    }

    // storage: the volumes the page would show an entry each. power: the lines the page would show for power (0 to 2). frames:
    // whether the page shows the frame rate's line.
    public static MonitorLayout For(double width, double height, bool processes, int storage = 0, int power = 0, bool frames = false)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        double figure = height < 120 ? 16 : height < 200 ? 19 : 23;
        double body = height < 120 ? 11 : 12, caption = height < 120 ? 10 : 10.5;
        double row = height < 160 ? 19 : 22;
        var list = !processes ? ListPlacement.None
            : width >= 560 && height - ListHead >= MinimumRows * row ? ListPlacement.Side
            : height >= StackedReadings + ListHead + MinimumRows * row ? ListPlacement.Below : ListPlacement.None;
        // The core columns need height to read; memory and network give theirs first. Readings above a list have its fixed height.
        double readings = list == ListPlacement.Below ? StackedReadings : height;
        // Memory and network take a line each only where a drive's line still fits under them, so a page growing past that step
        // does not lose its drives for a few DIPs.
        var metrics = readings < LinesFrom(figure, body) ? MetricsMode.Line : readings < 250 ? MetricsMode.Lines : MetricsMode.Blocks;
        // The readings beside a side list are the page less the list and the 22 DIP between them.
        double readingsWidth = list == ListPlacement.Side ? width - 22 - SideListWidth(width) : width;
        // The first line of power (with the battery, sharing it) comes before the drives' lines, which give up room for it: a page the
        // user set to show power must show it at the size it has, however many drives there are. The battery's own line comes after the drives.
        bool firstLine = power > 0 && PowerRoom(readings, metrics, figure, body, 0) >= 1;
        double line = Fixed(metrics, figure, body).StorageMargin + StorageRowFor(body);
        double reserve = firstLine ? line : 0;
        // The frame rate's line comes next, where a line still fits with the first power line's room kept.
        bool frameLine = frames && PowerRoom(readings - reserve, metrics, figure, body, 0) >= 1;
        double frameRoom = frameLine ? line : 0;
        int columns = storage <= 0 ? 0 : readingsWidth >= StorageTwoColumns ? Math.Min(storage, MaximumStorageColumns) : 1;
        int lines = storage <= 0 ? 0 : Math.Min((storage + columns - 1) / columns, StorageRoom(readings - reserve - frameRoom, metrics, figure, body));
        int powerLines = power <= 0 ? 0 : Math.Min(Math.Min(power, 2), Math.Max(PowerRoom(readings - frameRoom, metrics, figure, body, lines), firstLine ? 1 : 0));
        return new(figure, body, caption, metrics, list, row) { StorageLines = lines, StorageColumns = lines > 0 ? columns : 0, PowerLines = powerLines, FrameLine = frameLine };
    }

    // The rows and columns a list of this size shows.
    public static (int Rows, int Columns) ListGrid(double width, double height, double rowHeight)
    {
        if (!(rowHeight > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return (0, 0);
        int rows = Math.Clamp((int)Math.Floor((height - ListHead) / rowHeight), 0, MaximumRows);
        if (rows < MinimumRows) return (0, 0);
        return (rows, width >= TwoColumns ? MaximumColumns : 1);
    }

    // The side list's width in a page of this width: its share, within its bounds, leaving the readings at least 260 DIP.
    public static double SideListWidth(double width) => Math.Clamp(Math.Round(width * ListShare), ListMinimum, Math.Min(ListMaximum, Math.Max(ListMinimum, width - 260)));
}

// Where the readings beside the process list end: level with the list's last row, which steps by a row as the page grows or
// shrinks. While the page is being resized the foot stays where it is, so the readings stretch with the drag as they did before
// the list set their foot, instead of jumping a row's height each time the list gains or loses one; once the size has held for
// Settle seconds the foot eases to the list's. The first size is taken at once.
public sealed class FootAlignment
{
    public const double Settle = .16;
    private Eased _foot;
    private bool _placed;
    private double _target, _resizedAt = double.NegativeInfinity;

    public void Resize(double foot, double now)
    {
        _target = double.IsFinite(foot) ? Math.Max(0, foot) : 0;
        if (!_placed) { _placed = true; _foot = new Eased(_target); return; }
        _resizedAt = now;
    }

    // Called on each frame while it returns true: whether the foot moves or still waits for the size to settle.
    public bool Step(double now, bool motion)
    {
        if (now - _resizedAt < Settle) return true;
        if (Math.Abs(_foot.Target - _target) > 1e-9) return _foot.Set(_target, motion, now);
        return _foot.Moving(now);
    }

    public double At(double now) => Math.Max(0, _foot.At(now));
}
