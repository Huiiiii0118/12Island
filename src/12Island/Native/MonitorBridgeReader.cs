using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Reads the hardware monitors MonitorBridges picks from, from the sampling thread, and only while the processor's temperature
// or power is wanted and no better source gives it. HWiNFO's shared memory (Global\HWiNFO_SENS_SM2, its layout as HWiNFO's
// hwisenssm2.h gives it, packed: a header, the sensors, then the readings) is opened under its own mutex and copied at once; it
// exists only while HWiNFO runs with "Shared Memory Support" on. LibreHardwareMonitor answers on its web server only when the
// user turned that on. A monitor is looked for when the readings start (Rescan, as the monitor starts or comes back), and, not
// found, a few times more in the first minutes (Looks: both may start with Windows, in either order); then no more until the
// readings start again. One that was found is read on every tick, and looked for on the same schedule once it stops answering.
internal sealed class MonitorBridgeReader
{
    private static readonly double[] Looks = [30, 120, 300];
    private const string HwInfoMap = @"Global\HWiNFO_SENS_SM2", HwInfoMutex = @"Global\HWiNFO_SM2_MUTEX";
    private static readonly Uri LibreUri = new("http://localhost:8085/data.json");
    // Made when LibreHardwareMonitor is first looked for, so the network stack is not loaded for nothing.
    private static HttpClient? _http;
    private static HttpClient Http => _http ??= new() { Timeout = TimeSpan.FromMilliseconds(400) };

    // One monitor: whether it answered last time, the time of its next look, and how many of Looks are spent.
    private sealed class Bridge(Func<List<BridgeSensor>> read, CpuTemperatureSource source)
    {
        private bool _found;
        private int _looks;
        private double _nextAt = double.NegativeInfinity;
        private double _startedAt = double.NaN;
        public void Rescan() { _found = false; _looks = 0; _nextAt = double.NegativeInfinity; _startedAt = double.NaN; }
        public (double? Celsius, double? Watts, CpuTemperatureSource Source)? Read(double now)
        {
            if (double.IsNaN(_startedAt)) _startedAt = now;
            if (!_found && now < _nextAt) return null;
            var (celsius, watts) = MonitorBridges.Cpu(read());
            if (celsius is not null || watts is not null) { _found = true; return (celsius, watts, source); }
            // Lost after being found: looked for again on the same schedule, from now.
            if (_found) { _found = false; _looks = 0; _startedAt = now; }
            _nextAt = _looks < Looks.Length ? _startedAt + Looks[_looks++] : double.PositiveInfinity;
            return null;
        }
    }
    private readonly Bridge _hwInfo = new(HwInfo, CpuTemperatureSource.HwInfo), _libre = new(Libre, CpuTemperatureSource.LibreHardwareMonitor);

    public void Rescan() { _hwInfo.Rescan(); _libre.Rescan(); }
    // The readings paused: HWiNFO's copy is let go until they start again. On the sampling thread, whose copy it is.
    public void Rest() => _copy = null;

    public (double? Celsius, double? Watts, CpuTemperatureSource Source) Read(double now) =>
        _hwInfo.Read(now) ?? _libre.Read(now) ?? (null, null, CpuTemperatureSource.Zone);

    // The copy of HWiNFO's memory, kept between readings so a second's reading does not allocate it again.
    [ThreadStatic] private static byte[]? _copy;

    private static List<BridgeSensor> HwInfo()
    {
        var sensors = new List<BridgeSensor>();
        byte[] copy;
        int length;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(HwInfoMap, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Mutex? mutex = null;
            try { mutex = Mutex.OpenExisting(HwInfoMutex); } catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
            bool held = false;
            try
            {
                try { held = mutex?.WaitOne(50) ?? false; } catch (AbandonedMutexException) { held = true; }
                // Only as far as the readings end: the header (below) says where that is.
                long end = view.Capacity < 44 ? view.Capacity : Math.Max(44, Math.Max(view.ReadInt32(20) + (long)view.ReadInt32(24) * view.ReadInt32(28),
                    view.ReadInt32(32) + (long)view.ReadInt32(36) * view.ReadInt32(40)));
                length = (int)Math.Clamp(end, 0, Math.Min(view.Capacity, 16 << 20));
                copy = _copy is { } kept && kept.Length >= length ? kept : _copy = new byte[length];
                view.ReadArray(0, copy, 0, length);
            }
            finally { if (held) mutex!.ReleaseMutex(); mutex?.Dispose(); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { _copy = null; return sensors; }
        // The header: "HWiS", version, revision, poll time (8), then offset, size and count of the sensors and of the readings.
        if (length < 44 || Encoding.ASCII.GetString(copy, 0, 4) != "SiWH" && Encoding.ASCII.GetString(copy, 0, 4) != "HWiS") return sensors;
        int sensorAt = BitConverter.ToInt32(copy, 20), sensorSize = BitConverter.ToInt32(copy, 24), sensorCount = BitConverter.ToInt32(copy, 28);
        int readingAt = BitConverter.ToInt32(copy, 32), readingSize = BitConverter.ToInt32(copy, 36), readingCount = BitConverter.ToInt32(copy, 40);
        if (sensorSize < 136 || readingSize < 292 || sensorCount < 0 || readingCount < 0) return sensors;
        var names = new string?[sensorCount];
        // A reading: type, sensor index, reading id, original label (128), the user's label (128), unit (16), then the value. Only the
        // sensors that have a temperature or power reading are named.
        for (int i = 0; i < readingCount && readingAt + (long)(i + 1) * readingSize <= length; i++)
        {
            int at = readingAt + i * readingSize;
            int type = BitConverter.ToInt32(copy, at), sensor = BitConverter.ToInt32(copy, at + 4);
            BridgeReading? kind = type switch { 1 => BridgeReading.Temperature, 5 => BridgeReading.Power, _ => null };
            if (kind is null || sensor < 0 || sensor >= names.Length || sensorAt + (long)(sensor + 1) * sensorSize > length) continue;
            double value = BitConverter.ToDouble(copy, at + 284);
            // HWiNFO gives temperatures in the unit its user chose.
            if (kind == BridgeReading.Temperature && Text(copy, at + 268, 16).EndsWith('F')) value = (value - 32) * 5 / 9;
            sensors.Add(new BridgeSensor(names[sensor] ??= Text(copy, sensorAt + sensor * sensorSize + 8, 128), Text(copy, at + 12, 128), kind.Value, value));
        }
        return sensors;
    }
    private static string Text(byte[] bytes, int at, int length)
    {
        int end = Array.IndexOf(bytes, (byte)0, at, length);
        return Encoding.Latin1.GetString(bytes, at, (end < 0 ? at + length : end) - at).Trim();
    }

    private static List<BridgeSensor> Libre()
    {
        var sensors = new List<BridgeSensor>();
        try
        {
            // Parsed as it arrives, within the same time as the request: the whole document as one string was large enough for the
            // large object heap, every second.
            using var timeout = new CancellationTokenSource(Http.Timeout);
            using var response = Http.GetAsync(LibreUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream(timeout.Token);
            using var document = JsonDocument.ParseAsync(stream, default, timeout.Token).GetAwaiter().GetResult();
            void Walk(JsonElement node)
            {
                if (node.TryGetProperty("SensorId", out var id) && node.TryGetProperty("Type", out var type) && node.TryGetProperty("RawValue", out var raw)
                    && raw.ValueKind == JsonValueKind.Number && node.TryGetProperty("Text", out var text))
                {
                    BridgeReading? kind = type.GetString() switch { "Temperature" => BridgeReading.Temperature, "Power" => BridgeReading.Power, _ => null };
                    if (kind is not null) sensors.Add(new BridgeSensor(id.GetString() ?? "", text.GetString() ?? "", kind.Value, raw.GetDouble()));
                }
                if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var child in children.EnumerateArray()) Walk(child);
            }
            Walk(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException) { Debug.WriteLine(ex.Message); }
        return sensors;
    }
}
