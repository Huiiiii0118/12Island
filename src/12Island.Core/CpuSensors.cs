using System.Globalization;
using System.Text;

namespace TwelveIsland.Core;

// The processor's own temperature and power as the sensor service reads them (TwelveIsland.Sensors, through the PawnIO driver), and
// the reply it gives TwelveIsland. This file is compiled into the service as well, so it uses nothing but the base library.

public enum SensorStatus { Ok, NoDriver, NoModule, Unsupported, Failed }

// One reply: Version is the service's protocol, so TwelveIsland can tell an older service to update. Cores holds each core's
// temperature (Intel) or each CCD's (AMD, CoresAreCcds); a figure the processor does not give is null. Watts: the package, its
// cores, its graphics, the memory behind it and the whole platform (Intel's PSYS), as RAPL or AMD's energy counters give them.
public sealed record SensorReport(SensorStatus Status)
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string Processor { get; init; } = "";
    public double? Package { get; init; }
    public IReadOnlyList<double> Cores { get; init; } = [];
    public bool CoresAreCcds { get; init; }
    public double? PackageWatts { get; init; }
    public double? CoresWatts { get; init; }
    public double? GraphicsWatts { get; init; }
    public double? MemoryWatts { get; init; }
    public double? PlatformWatts { get; init; }

    // "key=value" lines, invariant numbers; unknown keys are skipped when read, so either side may add some.
    public string Format()
    {
        var text = new StringBuilder();
        void Line(string key, string? value) { if (value is not null) text.Append(key).Append('=').Append(value).Append('\n'); }
        static string? Number(double? value) => value is { } number && double.IsFinite(number) ? number.ToString("0.###", CultureInfo.InvariantCulture) : null;
        Line("version", Version.ToString(CultureInfo.InvariantCulture));
        Line("status", Status.ToString());
        Line("processor", Processor.Replace('\n', ' '));
        Line("package", Number(Package));
        if (Cores.Count > 0) Line(CoresAreCcds ? "ccds" : "cores", string.Join(",", Cores.Select(core => Number(core))));
        Line("packageWatts", Number(PackageWatts));
        Line("coresWatts", Number(CoresWatts));
        Line("graphicsWatts", Number(GraphicsWatts));
        Line("memoryWatts", Number(MemoryWatts));
        Line("platformWatts", Number(PlatformWatts));
        return text.ToString();
    }

    public static SensorReport? Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
            if (line.IndexOf('=') is var at and > 0) values[line[..at].Trim()] = line[(at + 1)..].Trim();
        if (!values.TryGetValue("status", out var statusText) || !Enum.TryParse<SensorStatus>(statusText, out var status)
            || !values.TryGetValue("version", out var versionText) || !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)) return null;
        double? Number(string key) => values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number) ? number : null;
        bool ccds = values.ContainsKey("ccds");
        var cores = values.TryGetValue(ccds ? "ccds" : "cores", out var list) && list.Length > 0
            ? list.Split(',').Select(item => double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double celsius) ? celsius : double.NaN).Where(double.IsFinite).ToList()
            : [];
        return new SensorReport(status)
        {
            Version = version, Processor = values.GetValueOrDefault("processor") ?? "", Package = Number("package"), Cores = cores, CoresAreCcds = ccds,
            PackageWatts = Number("packageWatts"), CoresWatts = Number("coresWatts"), GraphicsWatts = Number("graphicsWatts"),
            MemoryWatts = Number("memoryWatts"), PlatformWatts = Number("platformWatts")
        };
    }
}

// How the processors' registers turn into degrees and joules: Intel's from its Software Developer's Manual (volume 4), AMD's
// Zen ones as Linux's k10temp and the AMD processor programming references give them.
public static class CpuSensorMath
{
    // Intel: IA32_TEMPERATURE_TARGET (0x1A2) holds TjMax in bits 23:16; IA32_THERM_STATUS (0x19C, per core) and
    // IA32_PACKAGE_THERM_STATUS (0x1B1) the distance below it in bits 22:16, valid when bit 31 is set.
    public static double? IntelTjMax(ulong target) => (target >> 16 & 0xFF) is var tj and >= 50 and <= 150 ? tj : null;
    public static double? IntelTemperature(ulong status, double tjMax) =>
        (status & 0x80000000) == 0 ? null : tjMax - (status >> 16 & 0x7F);

    // RAPL: MSR_RAPL_POWER_UNIT (Intel 0x606, AMD 0xC0010299) holds the energy unit as 1/2^n joules in bits 12:8.
    public static double EnergyUnit(ulong units) => 1.0 / (1UL << (int)(units >> 8 & 0x1F));

    // Joules between two readings of a 32-bit energy counter, which wraps.
    public static double Joules(uint previous, uint current, double unit) => unchecked(current - previous) * unit;

    // AMD Zen: the reported temperature (SMN 0x59800) in eighths of a degree in bits 31:21; 49 degrees lower when bit 19 (range
    // select) is set or bits 17:16 (Tj select) are both set. Tctl, which some early parts report above the die (TctlOffset).
    public const uint ZenTemperatureAddress = 0x00059800;
    public static double ZenTctl(uint register)
    {
        double celsius = (register >> 21) * 0.125;
        return (register & 0x80000) != 0 || (register & 0x30000) == 0x30000 ? celsius - 49 : celsius;
    }

    // Degrees Tctl reads above the die on the parts that report an offset (k10temp's table), by the processor's name.
    public static double TctlOffset(int family, string name)
    {
        if (family != 0x17) return 0;
        foreach (var (part, offset) in TctlOffsets) if (name.Contains(part, StringComparison.Ordinal)) return offset;
        return 0;
    }
    private static readonly (string Part, double Offset)[] TctlOffsets =
    [
        ("AMD Ryzen 5 1600X", 20), ("AMD Ryzen 7 1700X", 20), ("AMD Ryzen 7 1800X", 20), ("AMD Ryzen 7 2700X", 10),
        ("AMD Ryzen Threadripper 19", 27), ("AMD Ryzen Threadripper 29", 27)
    ];

    // Where a Zen part reports each CCD's temperature: an offset from 0x59800 and how many CCDs it may have (k10temp); null
    // for a part without them. A CCD's register is valid when bit 11 is set; its eighths of a degree are bits 10:0, less 49.
    public static (uint Offset, int Count)? ZenCcds(int family, int model) => family switch
    {
        0x17 => model switch
        {
            0x1 or 0x8 or 0x11 or 0x18 => (0x154, 4),
            0x31 or 0x47 or 0x60 or 0x68 or 0x71 => (0x154, 8),
            >= 0xA0 and <= 0xAF => (0x300, 8),
            _ => null
        },
        0x19 => model switch
        {
            0x0 or 0x1 or 0x8 or 0x21 or (>= 0x50 and <= 0x5F) => (0x154, 8),
            >= 0x40 and <= 0x4F => (0x300, 8),
            >= 0x60 and <= 0x7F => (0x308, 8),
            (>= 0x10 and <= 0x1F) or (>= 0xA0 and <= 0xAF) => (0x300, 12),
            _ => null
        },
        0x1A => model switch
        {
            <= 0x1F => (0x1F0, 16),
            >= 0x40 and <= 0x4F => (0x308, 8),
            _ => null
        },
        _ => null
    };
    public static double? ZenCcd(uint register) =>
        // An unreadable CCD answers all ones, which would pass the valid bit.
        register == uint.MaxValue || (register & 0x800) == 0 ? null : (register & 0x7FF) * 0.125 - 49;

    // Family and model from CPUID leaf 1's eax, extended parts included.
    public static (int Family, int Model) FamilyModel(uint eax)
    {
        int family = (int)(eax >> 8 & 0xF), model = (int)(eax >> 4 & 0xF);
        if (family == 0xF) family += (int)(eax >> 20 & 0xFF);
        if (family >= 0x6) model |= (int)(eax >> 16 & 0xF) << 4;
        return (family, model);
    }
}

// Watts from a 32-bit energy counter read now and then: null for the first reading.
public sealed class EnergyMeter
{
    private uint _last;
    private double _lastAt = double.NaN;

    public double? Watts(uint counter, double unit, double now)
    {
        double? watts = double.IsNaN(_lastAt) || now <= _lastAt ? null : CpuSensorMath.Joules(_last, counter, unit) / (now - _lastAt);
        _last = counter; _lastAt = now;
        // A counter reset, or two readings too close together for the counter's resolution, gives no figure.
        return watts is >= 0 and < 2000 ? watts : null;
    }
}
