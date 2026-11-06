namespace TwelveIsland.Core;

// The processor's temperature and power taken from a hardware monitor the user already runs, for a machine where Windows gives
// neither and enhanced sensors are not set up: HWiNFO's shared memory, and LibreHardwareMonitor's own web server (Options ›
// Remote Web Server, http://localhost:8085/data.json; its releases no longer publish to WMI). Each lists its readings with the
// device they belong to; these pick the processor's package temperature and package power by the names the monitors give them
// in English (HWiNFO's original labels, LibreHardwareMonitor's sensor names).
public enum BridgeReading { Temperature, Power }

public sealed record BridgeSensor(string Device, string Label, BridgeReading Kind, double Value);

public static class MonitorBridges
{
    // HWiNFO: devices "CPU [#0]: Intel Core i7-12700H: DTS", "CPU [#0]: AMD Ryzen 7 7800X3D: Enhanced"; LibreHardwareMonitor:
    // sensor ids "/intelcpu/0/...", "/amdcpu/0/...". The labels in order of preference.
    private static readonly string[] Temperatures = ["CPU Package", "CPU (Tctl/Tdie)", "Core (Tctl/Tdie)", "CPU Die (average)", "Core (Tctl)", "CPU (Tctl)", "Core Max", "Core Average"];
    private static readonly string[] Powers = ["CPU Package Power", "CPU Package", "Package", "CPU PPT"];

    public static (double? Celsius, double? Watts) Cpu(IEnumerable<BridgeSensor> sensors)
    {
        var cpu = sensors.Where(sensor => IsCpu(sensor.Device) && double.IsFinite(sensor.Value)).ToList();
        double? Pick(BridgeReading kind, string[] labels, Func<double, bool> plausible)
        {
            foreach (var label in labels)
                foreach (var sensor in cpu)
                    if (sensor.Kind == kind && sensor.Label.Equals(label, StringComparison.OrdinalIgnoreCase) && plausible(sensor.Value)) return sensor.Value;
            return null;
        }
        return (Pick(BridgeReading.Temperature, Temperatures, celsius => celsius > 0 && TwelveIsland.Core.Temperatures.Plausible(celsius) is not null),
            Pick(BridgeReading.Power, Powers, PowerResolver.Plausible));
    }

    private static bool IsCpu(string device) => device.StartsWith("CPU [", StringComparison.OrdinalIgnoreCase)
        || device.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase) || device.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase);
}
