using System.Diagnostics;
using System.IO;
using System.Text;
using TwelveIsland.Core;
using Microsoft.Win32;

namespace TwelveIsland.Native;

// Guards the readings that ask a device's driver directly (SensorStops). Ask marks a reading as being asked: in memory always, and
// on disk for the first readings of a device in a run (every drive temperature), written through to the disk before the driver is
// asked, so the mark survives a machine that freezes and is restarted. Start, at the next run, turns a mark left behind into a stop.
// A reading still unanswered after SensorStops.StuckSeconds is stopped at once; its thread cannot be freed, so the caller is told
// (stuck) and starts another in its place, and the old thread, should the driver ever answer, ends with ReadingAbandoned.
// Clocks are not used for the wait: the watch counts its own ticks, so a machine that sleeps through a reading wakes to one tick.
internal static class SensorGuard
{
    private const int WatchMilliseconds = 2000;
    private sealed class Asking(GuardedDevice device, bool marked, Action? stuck)
    {
        public GuardedDevice Device => device;
        public bool Marked => marked;
        public Action? OnStuck => stuck;
        public int Ticks;
        public volatile bool Stuck;
    }
    private static readonly object Gate = new();
    private static readonly List<Asking> Askings = [];
    private static readonly Dictionary<(GuardedReading, string), int> Counts = [];
    private static readonly Timer Watch = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static List<SensorStop> _stops = [];
    private static string? _folder;
    private static bool _watching;

    // Raised on any thread when a reading is stopped or turned on again.
    public static event Action? Changed;

    public static IReadOnlyList<SensorStop> Stops { get { lock (Gate) return [.. _stops]; } }

    // At startup, before anything is read: the stops kept, and a stop for every reading a mark says was never answered.
    public static void Start(string folder)
    {
        lock (Gate)
        {
            _folder = folder;
            var stops = SensorStops.Current(SensorStops.Parse(ReadText(SensorStops.StopsFile) ?? "[]"));
            var left = SensorStops.ParsePending(ReadText(SensorStops.PendingFile) ?? "[]", DateTimeOffset.Now);
            foreach (var stop in left) stops = SensorStops.With(stops, new GuardedDevice(stop.Reading, stop.Device, stop.Name), stop.Method, stop.At);
            _stops = SensorStops.Current(stops);
            if (left.Count > 0) { SaveStops(); WritePending(); }
        }
    }

    public static bool Stopped(GuardedReading reading, string device)
    {
        lock (Gate) return _stops.Exists(stop => SensorStops.Applies(stop, reading, device));
    }

    // Asks the driver through read. stuck: called once, on another thread, should read not answer in time.
    public static T Ask<T>(GuardedDevice device, Func<T> read, Action? stuck = null)
    {
        var asking = Begin(device, stuck);
        try { return read(); }
        finally
        {
            End(asking);
            // The reading was given up on and its thread replaced: this thread ends here, before it touches anything shared.
            if (asking.Stuck) throw new ReadingAbandoned();
        }
    }

    // The user turns a reading on again.
    public static void Resume(SensorStop stop)
    {
        lock (Gate)
        {
            if (_stops.RemoveAll(kept => kept.Reading == stop.Reading && kept.Device == stop.Device) == 0) return;
            SaveStops();
        }
        Changed?.Invoke();
    }

    // A clean exit: nothing is being asked any longer.
    public static void Finish()
    {
        lock (Gate) { Askings.Clear(); Watch.Change(Timeout.Infinite, Timeout.Infinite); _watching = false; WritePending(); }
    }

    private static Asking Begin(GuardedDevice device, Action? stuck)
    {
        lock (Gate)
        {
            var key = (device.Reading, device.Device);
            int count = Counts.GetValueOrDefault(key);
            bool marked = _folder is not null && (SensorStops.AlwaysMarked(device.Reading) || count < SensorStops.MarkedReadings);
            if (count < SensorStops.MarkedReadings) Counts[key] = count + 1;
            var asking = new Asking(device, marked, stuck);
            Askings.Add(asking);
            if (marked) WritePending();
            if (!_watching) { _watching = true; Watch.Change(WatchMilliseconds, WatchMilliseconds); }
            return asking;
        }
    }

    private static void End(Asking asking)
    {
        lock (Gate)
        {
            if (!Askings.Remove(asking)) return;
            if (asking.Marked) WritePending();
            if (Askings.Count == 0) { _watching = false; Watch.Change(Timeout.Infinite, Timeout.Infinite); }
        }
    }

    private static void Tick()
    {
        var stuck = new List<Asking>();
        lock (Gate)
        {
            foreach (var asking in Askings)
                if (!asking.Stuck && ++asking.Ticks * WatchMilliseconds >= SensorStops.StuckSeconds * 1000) { asking.Stuck = true; stuck.Add(asking); }
            if (stuck.Count == 0) return;
            foreach (var asking in stuck) _stops = SensorStops.With(_stops, asking.Device, SensorStops.Method(asking.Device.Reading), DateTimeOffset.Now);
            // Stopped already: the mark is no longer needed to stop it at the next start.
            Askings.RemoveAll(stuck.Contains);
            SaveStops(); WritePending();
            if (Askings.Count == 0) { _watching = false; Watch.Change(Timeout.Infinite, Timeout.Infinite); }
        }
        foreach (var asking in stuck)
        {
            Debug.WriteLine($"SensorGuard: {asking.Device.Reading} of {asking.Device.Name} did not answer");
            try { asking.OnStuck?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        }
        Changed?.Invoke();
    }

    // The readings being asked that are marked, written through to the disk: a mark still in the cache would be lost with a frozen
    // machine. None left, the file is written empty rather than deleted, as a delete may also be lost and leave the old mark.
    private static void WritePending()
    {
        if (_folder is null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var bytes = Encoding.UTF8.GetBytes(SensorStops.SerializePending(Askings.Where(asking => asking.Marked).Select(asking => asking.Device)));
            using var stream = new FileStream(Path.Combine(_folder, SensorStops.PendingFile), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(bytes); stream.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    private static void SaveStops()
    {
        if (_folder is null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, SensorStops.StopsFile);
            File.WriteAllText(path + ".tmp", SensorStops.Serialize(_stops));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    private static string? ReadText(string name)
    {
        if (_folder is null) return null;
        try { var path = Path.Combine(_folder, name); return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); return null; }
    }

    // The board and its firmware version, from what Windows recorded at boot, without asking any device: the device the processor's
    // temperature and the power meter are read from (both come from the firmware). A firmware update tries them again.
    public static string Board { get; } = ReadBoard();
    private static string ReadBoard()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var parts = new[] { "BaseBoardManufacturer", "BaseBoardProduct", "BIOSVersion" }.Select(name => (key?.GetValue(name) as string)?.Trim() ?? "");
            return string.Join(" | ", parts);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }
}

// Thrown on a thread whose reading was given up on (SensorGuard), once the driver answers after all.
internal sealed class ReadingAbandoned() : Exception("The reading was given up on.");
