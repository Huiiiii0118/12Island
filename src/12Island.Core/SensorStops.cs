using System.Text.Json;

namespace TwelveIsland.Core;

// The readings that ask a device's driver directly, which may never answer. On one laptop, Intel Rapid Storage Technology never
// answered a drive's temperature query and the whole machine froze. Each such reading is guarded (Native.SensorGuard). Before it is
// asked, a mark is written to disk and taken away once it answers. A mark left at the next start (the machine froze and was
// restarted), or a reading that has not answered for StuckSeconds, stops that reading for that device: a SensorStop, kept until
// the user turns it on again in the settings.
// GpuDriver: an adapter's usage and power asked of its own driver (Native.GpuDrivers).
public enum GuardedReading { DriveTemperature, CpuTemperature, GpuTemperature, PowerMeter, GpuDriver }

// Device tells the device apart across restarts (a drive's model and bus, a graphics adapter's name, the board and its firmware
// version, so a firmware update tries again); Name is what the settings call it.
public sealed record GuardedDevice(GuardedReading Reading, string Device, string Name);

// Method: the way the reading was asked when it stopped (SensorStops.Method). A stop recorded for an older way no longer applies.
public sealed record SensorStop(GuardedReading Reading, string Device, string Name, int Method, DateTimeOffset At);

public static class SensorStops
{
    public const string StopsFile = "sensor-stops.json", PendingFile = "sensor-pending.json";
    // A reading that has not answered for this long is taken for one that never will.
    public const int StuckSeconds = 10;
    // The first readings of a device in a run are marked on disk; the later ones only watched for StuckSeconds. A drive's temperature
    // is asked every 30 seconds and marked every time; the others are asked every second or two, too often to write each time, and
    // a driver that cannot answer them fails on the first ones.
    public const int MarkedReadings = 20;

    // Raised by one whenever a reading is asked in a new way, so the stops recorded for the old way no longer hold and it is tried again.
    // A GPU query can time out once while the machine is busy (a layout rebuild, a resume from sleep) and then answer normally
    // ever after; method 2 lets those old stops lapse instead of hiding the GPU temperature for good.
    public static int Method(GuardedReading reading) => reading switch
    {
        GuardedReading.DriveTemperature => 1,
        GuardedReading.CpuTemperature => 1,
        GuardedReading.GpuTemperature => 2,
        GuardedReading.GpuDriver => 2,
        _ => 1
    };

    public static bool AlwaysMarked(GuardedReading reading) => reading == GuardedReading.DriveTemperature;

    public static bool Applies(SensorStop stop, GuardedReading reading, string device) =>
        stop.Reading == reading && stop.Device == device && stop.Method == Method(reading);

    // The stops with this device's stop added, in place of an earlier one for it.
    public static List<SensorStop> With(IEnumerable<SensorStop> stops, GuardedDevice device, int method, DateTimeOffset at) =>
        [.. stops.Where(stop => stop.Reading != device.Reading || stop.Device != device.Device), new SensorStop(device.Reading, device.Device, device.Name, method, at)];

    // The stops that still apply; one recorded for an older way of asking is dropped. A drive or a power meter that froze the
    // machine stays stopped until the user turns it on again, but a GPU query is given another chance after an hour: these
    // calls answer in milliseconds on a healthy machine, and one timeout must not cost the temperature display forever.
    private static readonly TimeSpan GpuRetry = TimeSpan.FromHours(1);
    private static bool Gpu(GuardedReading reading) => reading is GuardedReading.GpuTemperature or GuardedReading.GpuDriver;
    public static List<SensorStop> Current(IEnumerable<SensorStop> stops) => [.. stops.Where(stop =>
        stop.Method == Method(stop.Reading) && !(Gpu(stop.Reading) && DateTimeOffset.Now - stop.At > GpuRetry))];

    // The line the settings show for a stop.
    public static string Notice(SensorStop stop) => stop.Reading switch
    {
        GuardedReading.CpuTemperature => Loc.T("已停止读取 CPU 温度，上次读取时电脑可能卡住了。",
            "Stopped reading the CPU temperature: the PC may have frozen the last time it was read."),
        GuardedReading.PowerMeter => Loc.T("已停止读取功率计与 CPU 功耗，上次读取时电脑可能卡住了。",
            "Stopped reading the power meter and CPU power: the PC may have frozen the last time they were read."),
        GuardedReading.GpuDriver => Loc.T($"已停止从驱动读取“{stop.Name}”的利用率与功耗，上次读取时电脑可能卡住了。",
            $"Stopped reading the usage and power of “{stop.Name}” from its driver: the PC may have frozen the last time they were read."),
        _ => Loc.T($"已停止读取“{stop.Name}”的温度，上次读取时电脑可能卡住了。",
            $"Stopped reading the temperature of “{stop.Name}”: the PC may have frozen the last time it was read.")
    };

    private sealed record Pending(GuardedReading Reading, string Device, string Name, int Method);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static string Serialize(IEnumerable<SensorStop> stops) => JsonSerializer.Serialize(stops.ToList(), Options);
    public static List<SensorStop> Parse(string json)
    {
        try { return [.. (JsonSerializer.Deserialize<List<SensorStop>>(json, Options) ?? []).Where(stop => stop is { Device: not null, Name: not null })]; }
        catch (JsonException) { return []; }
    }

    // The readings being asked, with the way each is asked, so a stop made from them at the next start names that way.
    public static string SerializePending(IEnumerable<GuardedDevice> asking) =>
        JsonSerializer.Serialize(asking.Select(device => new Pending(device.Reading, device.Device, device.Name, Method(device.Reading))).ToList(), Options);
    // The readings a mark left behind say were never answered, as stops.
    public static List<SensorStop> ParsePending(string json, DateTimeOffset at)
    {
        try
        {
            return [.. (JsonSerializer.Deserialize<List<Pending>>(json, Options) ?? []).Where(pending => pending is { Device: not null, Name: not null })
                .Select(pending => new SensorStop(pending.Reading, pending.Device, pending.Name, pending.Method, at))];
        }
        catch (JsonException) { return []; }
    }
}
