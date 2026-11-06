namespace TwelveIsland.Core;

// The sensors behind the collapsed row's temperatures and drives, as the sampler knows them: the processor's thermal zones, the
// graphics adapters, the drives listed, and the drives' temperatures.
public readonly record struct SensorStates(SensorState Cpu, SensorState Gpu, SensorState Storage, SensorState DiskTemperatures)
{
    // Whether the machine gives a power reading (PowerResolver), and whether it has a battery of its own (BatteryModel).
    public SensorState Power { get; init; }
    public SensorState Charge { get; init; }
    // Whether frame rates can be read (FrameRates): by event tracing, or else by capturing the screen.
    public SensorState Frames { get; init; }
    public static SensorStates Unknown => default;
}

// What CPU, GPU and the drives show after their glyph in the collapsed row (CompactReading.Figures): the share in use, the
// temperature, or both, the share above the temperature in two smaller lines or, when chosen (CompactReading.FiguresInLine),
// side by side in the row's type. A temperature the machine cannot read is left out,
// so a reading set to show only that shows its glyph alone.
public static class CompactFigures
{
    public const string Usage = "Usage", Temperature = "Temperature";
    public static bool Has(CompactMonitorItems item) => item is CompactMonitorItems.Cpu or CompactMonitorItems.Gpu or CompactMonitorItems.Storage;

    // The share in use without a choice.
    public static IReadOnlyList<string> Of(CompactReading reading) => reading.Figures is { Count: > 0 } figures ? figures : [Usage];
    public static bool Shows(CompactReading reading, string figure) => Of(reading).Contains(figure);

    // A choice in its order (the share, then the temperature), each once; an empty or unknown one is the default (null).
    public static List<string>? Normalize(CompactMonitorItems item, IEnumerable<string?>? figures)
    {
        if (!Has(item) || figures is null) return null;
        var kept = new[] { Usage, Temperature }.Where(figure => figures.Any(chosen => string.Equals(chosen?.Trim(), figure, StringComparison.OrdinalIgnoreCase))).ToList();
        return kept.Count > 0 ? kept : null;
    }

    // A ring's temperature in this snapshot and its level; null while it has none.
    public static (double Celsius, MemoryLevel Level)? Heat(CompactMonitorItems item, MonitorSnapshot? snapshot) => item switch
    {
        CompactMonitorItems.Cpu when snapshot?.Cpu.OrDefault?.Temperature is { } cpu => (cpu, Temperatures.OfCpu(cpu)),
        CompactMonitorItems.Gpu when GpuHeat(snapshot) is { } gpu => (gpu, Temperatures.OfGpu(gpu)),
        _ => null
    };

    // The adapter shown need not be the one carrying the sensor: an integrated or virtual adapter can be the choice while
    // a dedicated card reports the only temperature. Each adapter keeps its own reading (WithTemperature) and the one its
    // driver gave with the usage (GpuDrivers), so a temperature anywhere on the machine counts for the GPU ring.
    private static double? GpuHeat(MonitorSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        if (snapshot.Gpu.OrDefault?.Temperature is { } own) return own;
        foreach (var adapter in snapshot.Gpus)
        {
            if (adapter.Temperature is { } reported) return reported;
            if (adapter.DriverTemperature is { } driven) return driven;
        }
        return null;
    }
}

// Whether the collapsed frame rate shows the icon of the program it counts in place of the screen glyph (CompactReading.Figures of
// the frame rate: the icon, or an empty choice for the glyph). Without a choice, the icon. The desktop and a program without an
// icon show the glyph. Its name shows on the page only: a name of any length in the row would need a room of fixed width, and leave
// a gap after a short one.
public static class CompactFrames
{
    public const string Icon = "Icon";
    public static IReadOnlyList<string> Of(CompactReading reading) => reading.Figures ?? [Icon];
    public static bool Shows(CompactReading reading, string part) => Of(reading).Contains(part);

    // The icon or nothing; null stays the default, and an empty choice stays empty.
    public static List<string>? Normalize(IEnumerable<string?>? parts) =>
        parts is null ? null : [.. new[] { Icon }.Where(part => parts.Any(chosen => string.Equals(chosen?.Trim(), part, StringComparison.OrdinalIgnoreCase)))];
}

// The drive letters the collapsed row's drive reading shows (CompactReading.Sources): each lettered volume's space in use and,
// when chosen, the temperature of the solid-state drive holding it. Several letters show side by side after one glyph, each
// after its letter: "C 45% D 60%".
public static class CompactSources
{
    // Without a choice, the volume holding Windows.
    public static IReadOnlyList<string> Default => [Storage.WindowsLetter?.ToUpperInvariant() ?? "C:"];
    public static IReadOnlyList<string> Of(CompactReading reading) => reading.Sources is { Count: > 0 } sources ? sources : Default;

    // A choice in letter order, each once; anything but a letter is dropped, and an empty choice is the default (null).
    public static List<string>? Normalize(CompactMonitorItems item, IEnumerable<string?>? sources)
    {
        if (item != CompactMonitorItems.Storage || sources is null) return null;
        var kept = sources.Select(source => source?.Trim().ToUpperInvariant())
            .Where(text => text is { Length: 2 } && text[0] is >= 'A' and <= 'Z' && text[1] == ':').OfType<string>().Distinct().OrderBy(Rank).ToList();
        return kept.Count > 0 ? kept : null;
    }
    public static int Rank(string letter) => letter[0];
    // The letter before a volume's figures when the reading shows more than one: "D".
    public static string Tag(string letter) => letter[..1];

    // A volume's share in use and its level, and the temperature of the drive holding it and its level; null while there is none.
    public static (double Share, MemoryLevel Level)? Share(string letter, MonitorSnapshot? snapshot) =>
        snapshot is not null && Storage.Find(snapshot.Storage, letter) is { } found ? (found.Volume.UsedFraction, ReadingLevels.OfShare(found.Volume.UsedFraction)) : null;
    public static (double Celsius, MemoryLevel Level)? Heat(string letter, MonitorSnapshot? snapshot) =>
        snapshot is not null && Storage.Find(snapshot.Storage, letter)?.Drive.Temperature is { } disk ? (disk.Celsius, Temperatures.OfDisk(disk)) : null;

    // Whether a letter is known to have nothing to show: no drive is listed at all, or, once the drives are read, none holds it (a
    // drive taken out). Its temperature also when the drive holding it is not solid-state, or once no drive gives one.
    public static bool Missing(string letter, MonitorSnapshot? snapshot, SensorStates states)
    {
        if (states.Storage == SensorState.None) return true;
        return states.Storage == SensorState.Found && snapshot is { Storage.Count: > 0 } && Storage.Find(snapshot.Storage, letter) is null;
    }
    public static bool HeatMissing(string letter, MonitorSnapshot? snapshot, SensorStates states)
    {
        if (Missing(letter, snapshot, states) || states.DiskTemperatures == SensorState.None) return true;
        return states.Storage == SensorState.Found && snapshot is { Storage.Count: > 0 } && Storage.Find(snapshot.Storage, letter) is { Drive.SolidState: false };
    }
}
