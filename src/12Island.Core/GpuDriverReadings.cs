namespace TwelveIsland.Core;

// An adapter's usage as its driver counts it (Native.GpuDrivers): NVIDIA's NVAPI, AMD's ADL, Intel's control library. The driver
// counts the time its graphics engine is busy, the figure AIDA64, HWiNFO's "GPU Core Load" and nvidia-smi show; Windows counts
// each engine apart and shows the busiest, which reads lower when work spreads over several engines or is the driver's own. The
// engines' own figures stay Windows' (GpuSample.Engines): no driver gives them all. Where no driver answers, the usage stays the
// busiest engine.
public static class GpuDriverReadings
{
    public const uint Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086;

    public static string? SourceName(uint vendor) => vendor switch
    {
        Nvidia => Loc.T("NVIDIA 驱动", "NVIDIA driver"), Amd => Loc.T("AMD 驱动", "AMD driver"), Intel => Loc.T("Intel 驱动", "Intel driver"), _ => null
    };

    // The adapter with the driver's usage (percent), board power (watts) and temperature (°C) where they were read. An integrated
    // adapter's power is inside the processor package already (PowerInputs.Cpu), so it is never taken: it would be counted twice.
    public static GpuSample Apply(GpuSample gpu, double? percent, double? watts, double? celsius = null)
    {
        if (percent is { } usage && double.IsFinite(usage))
            gpu = gpu with { Usage = Math.Clamp(usage / 100, 0, 1), UsageSource = SourceName(gpu.VendorId) };
        if (watts is { } power && PowerResolver.Plausible(power) && gpu.Kind != GpuKind.Integrated) gpu = gpu with { DriverWatts = power };
        // Zero is what a driver gives without a sensor, as the graphics kernel does (GpuTemperatures).
        if (celsius is { } heat && heat > 0 && Temperatures.Plausible(heat) is { } temperature) gpu = gpu with { DriverTemperature = temperature };
        return gpu;
    }

    // A rate from two readings of a counter that only grows (seconds busy, joules), per second of the clock beside it; null for the
    // first reading, a clock that did not move, or a counter that went back (a driver reset).
    public static double? Rate(double previous, double previousAt, double current, double currentAt)
    {
        double seconds = currentAt - previousAt, delta = current - previous;
        return double.IsFinite(seconds) && double.IsFinite(delta) && seconds > 0 && delta >= 0 ? delta / seconds : null;
    }

    // The bus of a location as GpuReadings.Location writes it ("1:0.0" is bus 1).
    public static uint? Bus(string? location) =>
        location is { Length: > 0 } && location.IndexOf(':') is var colon and > 0 && uint.TryParse(location.AsSpan(0, colon), out uint bus) ? bus : null;
}

// Which adapters may be asked: one Windows saw working (an engine above zero) within the last AwakeSeconds. A laptop's discrete
// adapter asleep has no engine at work, and asking its driver would wake it, every second, at a cost to the battery.
public sealed class GpuDriverGate
{
    public const double AwakeSeconds = 8;
    private readonly Dictionary<ulong, double> _busyAt = [];

    public bool Awake(GpuSample gpu, double now)
    {
        if (gpu.NoReadings) return false;
        if (gpu.EngineUsage > 0) _busyAt[gpu.Luid] = now;
        return _busyAt.TryGetValue(gpu.Luid, out var at) && now - at <= AwakeSeconds;
    }

    // Adapters that went away are forgotten.
    public void Keep(IEnumerable<ulong> luids)
    {
        var kept = luids.ToHashSet();
        foreach (var luid in _busyAt.Keys.Where(luid => !kept.Contains(luid)).ToList()) _busyAt.Remove(luid);
    }
}
