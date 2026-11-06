using System.Globalization;

namespace TwelveIsland.Core;

// A solid-state drive's temperature as the drive reports it, in degrees Celsius, with the drive's own warning and critical
// temperatures where it gives them. Name is the drive's model, Number its number (\\.\PhysicalDriveN), -1 where unknown.
public sealed record DiskTemperature(string Name, double Celsius, double? Warning = null, double? Critical = null)
{
    public int Number { get; init; } = -1;
}

// The temperatures the system monitor shows, from what Windows reports without a driver or administrator rights: the processor
// from the ACPI thermal zones, which the firmware defines and which may be the board's rather than the processor's own; the
// graphics adapter as Task Manager reads it; solid-state drives as they report themselves. A sensor that gives nothing, or a
// value no working sensor gives, is no reading: the page hides it rather than show a zero.
public static class Temperatures
{
    // Readings outside this range, in degrees Celsius, are taken for a missing or broken sensor: thermal zones report 0 K when
    // the firmware has nothing, and some drives report 0 °C or −273 °C.
    public const double Lowest = 1, Highest = 125;
    // The levels (ReadingLevels): a laptop processor runs in the high eighties under load and throttles near 100 °C; graphics
    // adapters aim for the low eighties; drives without thresholds of their own take ones solid-state drives keep well under.
    public const double CpuWarning = 85, CpuCritical = 95, GpuWarning = 80, GpuCritical = 90, DiskWarning = 60, DiskCritical = 70;

    public static double? Plausible(double celsius) => double.IsFinite(celsius) && celsius >= Lowest && celsius <= Highest ? celsius : null;
    public static double? FromKelvin(double kelvin) => Plausible(kelvin - 273.15);
    // The thermal zones' high precision counter, in tenths of a kelvin.
    public static double? FromDeciKelvin(double deciKelvin) => Plausible(deciKelvin / 10 - 273.15);
    // The graphics adapter's reading, in tenths of a degree; zero is an adapter without a sensor.
    public static double? FromDeciCelsius(uint deciCelsius) => deciCelsius == 0 ? null : Plausible(deciCelsius / 10.0);

    // The processor's temperature from the thermal zones' counters, by zone ("\_TZ.TZ00"): the high precision counter (tenths of
    // a kelvin) where a zone has it, else the whole kelvin one. A zone named for the processor wins; otherwise the hottest zone,
    // as the processor is usually what heats a zone most. Null without a zone giving a plausible reading.
    public static (double Celsius, string Zone)? Cpu(IEnumerable<(string Instance, double Value)> precise, IEnumerable<(string Instance, double Value)> whole)
        => Pick(Zones(precise, whole));

    // Each zone's plausible reading in degrees Celsius, the high precision counter where the zone has a plausible one.
    //
    // The two counters are not reported in one unit on every machine: "High Precision Temperature" is documented as tenths of a
    // kelvin and "Temperature" as whole kelvin, but several firmwares report the latter in tenths as well (a zone reading 3000
    // rather than 300). Each value is therefore read in its documented unit first and in the other when that gives nothing
    // plausible, so a machine whose firmware disagrees still shows a temperature instead of none.
    public static Dictionary<string, double> Zones(IEnumerable<(string Instance, double Value)> precise, IEnumerable<(string Instance, double Value)> whole)
    {
        var zones = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zone, value) in whole) if ((FromKelvin(value) ?? FromDeciKelvin(value)) is { } celsius) zones[zone] = celsius;
        foreach (var (zone, value) in precise) if ((FromDeciKelvin(value) ?? FromKelvin(value)) is { } celsius) zones[zone] = celsius;
        return zones;
    }

    public static (double Celsius, string Zone)? Pick(IEnumerable<KeyValuePair<string, double>> zones)
    {
        static bool Processor(string zone) => zone.Contains("CPU", StringComparison.OrdinalIgnoreCase) || zone.Contains("PROC", StringComparison.OrdinalIgnoreCase);
        return zones.OrderByDescending(pair => Processor(pair.Key)).ThenByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ((double Celsius, string Zone)?)(pair.Value, pair.Key)).FirstOrDefault();
    }

    // A drive's reading, or null when it is not plausible. Thresholds the drive gives are kept only when they are plausible and
    // in order.
    public static DiskTemperature? Disk(string name, double celsius, double warning, double critical)
    {
        if (Plausible(celsius) is not { } value) return null;
        double? own(double threshold) => Plausible(threshold);
        var (w, c) = (own(warning), own(critical));
        if (w is { } low && c is { } high && high < low) (w, c) = (null, null);
        return new DiskTemperature(name.Trim(), value, w, c);
    }

    public static MemoryLevel Level(double celsius, double warning, double critical) => celsius >= critical ? MemoryLevel.Critical : celsius >= warning ? MemoryLevel.Elevated : MemoryLevel.Normal;
    public static MemoryLevel OfCpu(double celsius) => Level(celsius, CpuWarning, CpuCritical);
    public static MemoryLevel OfGpu(double celsius) => Level(celsius, GpuWarning, GpuCritical);
    // A drive by its own thresholds where it gives them (NVMe drives give both); one alone sets the other five or ten degrees apart.
    public static MemoryLevel OfDisk(DiskTemperature disk)
    {
        double warning = disk.Warning ?? (disk.Critical is { } c ? c - 5 : DiskWarning);
        double critical = disk.Critical ?? (disk.Warning is { } w ? w + 10 : DiskCritical);
        return Level(disk.Celsius, warning, critical);
    }

    // A gauge's temperature after its figure, "62 °C", or nothing, so a missing sensor leaves no gap or dash.
    public static string Detail(double? celsius) => celsius is { } value ? Format(value) + " °C" : "";

    // An adapter's kind by its dedicated memory, as Task Manager tells them apart: integrated adapters have little or none of
    // their own. Shown before a temperature read from an adapter other than the one shown.
    public static string AdapterKind(ulong dedicated) => dedicated >= 512UL << 20 ? Loc.T("独显", "dGPU") : Loc.T("核显", "iGPU");

    // Whole degrees: "47".
    public static string Format(double celsius) => ((int)Math.Round(celsius, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
}

// Whether a kind of sensor (the processor's zones, the graphics adapters, the drives) has given a reading since the sampler
// started: Unknown until it is first asked, then Found or None. The page keeps room for a kind that is Unknown or Found, so it
// is built again only for a kind that turns out to have none, or one that turns up later.
public enum SensorState { Unknown, Found, None }

// Many desktop boards report a thermal zone that never moves (27.8 °C, 300 K and the like): a value the firmware fills in, not
// a sensor. A zone shows only once its value has changed since it was first read, so a fixed one never shows at all; a real
// sensor moves by a tenth of a kelvin (or a kelvin) within a minute or so. One that then stays the same for SteadySeconds is
// left out again until it changes.
public sealed class SteadyZones
{
    public const double SteadySeconds = 600;
    private readonly Dictionary<string, (double Value, double Since, bool Moved)> _zones = new(StringComparer.OrdinalIgnoreCase);

    // The processor's temperature (Temperatures.Pick) from the zones that have moved and are not steady, at now (seconds).
    public (double Celsius, string Zone)? Choose(IReadOnlyDictionary<string, double> zones, double now)
    {
        foreach (var zone in _zones.Keys.Where(zone => !zones.ContainsKey(zone)).ToList()) _zones.Remove(zone);
        var moving = new List<KeyValuePair<string, double>>(zones.Count);
        foreach (var pair in zones)
        {
            if (!_zones.TryGetValue(pair.Key, out var known)) _zones[pair.Key] = known = (pair.Value, now, false);
            else if (known.Value != pair.Value) _zones[pair.Key] = known = (pair.Value, now, true);
            if (known.Moved && now - known.Since < SteadySeconds) moving.Add(pair);
        }
        return Temperatures.Pick(moving);
    }
}

// The adapter's locally unique identifier from the key the performance counters and DXGI name adapters by,
// "luid_0x00000000_0x0000C5E2" (the high part, then the low part).
public static class AdapterLuid
{
    public static (uint Low, int High)? Parse(string? key)
    {
        if (key is null || !key.StartsWith("luid_0x", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = key.Split('_');
        if (parts.Length < 3 || !Hex(parts[1], out uint high) || !Hex(parts[2], out uint low)) return null;
        return (low, unchecked((int)high));
    }
    private static bool Hex(string text, out uint value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2
            && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
