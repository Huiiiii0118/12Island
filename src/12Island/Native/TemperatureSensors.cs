using System.Diagnostics;
using System.Runtime.InteropServices;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The temperatures Windows reports to a normal user without a driver: nothing here needs administrator rights or loads anything
// beyond the system's own libraries. Hardware monitors that read the processor's own sensors do it through a kernel driver
// (WinRing0 and the like), which TwelveIsland does not install. Each reader belongs to the sampling thread, as the providers do; the
// sensor states are read from the UI thread.

// The processor from the ACPI thermal zones, through the performance counters the sampler already collects. The zones are the
// firmware's: on some machines one is the processor package, on others the board near it, some report a fixed value (SteadyZones
// leaves those out) and some machines have none (then the page shows no processor temperature). The counters are in a query of
// their own, collected only while temperatures are wanted and guarded (SensorGuard): firmware whose zones never answer stops this
// reading, which then shows nothing, as on a machine without zones. stuck: the sampler gives up its thread for another.
internal sealed class ThermalZones(PdhQuery? pdh, Action stuck)
{
    private const string PrecisePath = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string WholePath = @"\Thermal Zone Information(*)\Temperature";
    private readonly SteadyZones _steady = new();
    private readonly GuardedDevice _device = new(GuardedReading.CpuTemperature, SensorGuard.Board, "CPU");
    private bool _added, _available;
    private double _firstRead = double.NaN;
    private volatile SensorState _state = pdh is null ? SensorState.None : SensorState.Unknown;
    private volatile bool _stopped;
    public SensorState State => _stopped ? SensorState.None : _state;

    // Called on every tick: wanted adds the counters and collects them, not wanted takes them out again.
    public (double Celsius, string Zone)? Sample(bool wanted, double now)
    {
        if (pdh is null) return null;
        _stopped = SensorGuard.Stopped(_device.Reading, _device.Device);
        if (!wanted || _stopped)
        {
            if (_added) { pdh.Remove(PrecisePath); pdh.Remove(WholePath); _added = false; }
            return null;
        }
        bool collected = SensorGuard.Ask(_device, () =>
        {
            if (!_added) { _added = true; _available = pdh.Add(PrecisePath) | pdh.Add(WholePath); }
            return _available && pdh.Collect();
        }, stuck);
        if (!_available) { _state = SensorState.None; return null; }
        if (!collected) return null;
        var zones = Temperatures.Zones(pdh.Read(PrecisePath), pdh.Read(WholePath));
        // Found once a zone gives a temperature (it has moved, SteadyZones); None without any zone, or when no zone has moved for
        // SteadySeconds since the first reading (only fixed values: not a sensor). Until then it stays Unknown, and the page keeps
        // no room for a processor temperature until one shows.
        var cpu = _steady.Choose(zones, now);
        if (double.IsNaN(_firstRead)) _firstRead = now;
        if (cpu is not null) _state = SensorState.Found;
        else if (_state == SensorState.Unknown && (zones.Count == 0 || now - _firstRead >= SteadyZones.SteadySeconds)) _state = SensorState.None;
        return cpu;
    }
}

// Graphics adapters as Task Manager reads their temperature: the adapter's performance data from the graphics kernel (WDDM 2.4
// and later). Each adapter is opened by its LUID and kept open. An adapter whose driver says it does not report the data (a
// software renderer) is not asked again. One that answers with no temperature (an adapter that has just woken reports zero) is
// asked again after 5 seconds; one whose query fails otherwise after a pause that grows from 5 seconds to 5 minutes, which
// starts over once the adapter becomes the one the page shows. Opening and asking an adapter are guarded (SensorGuard), by the
// adapter's name: one whose driver never answered is not asked again, as if it had no sensor. stuck: the sampler gives up its
// thread for another.
internal sealed class GpuTemperatures(Action stuck) : IDisposable
{
    // D3DKMT_ADAPTER_PERFDATA on 64-bit Windows: PhysicalAdapterIndex, then 8-byte figures; Temperature (tenths of a degree
    // Celsius) at 56, 64 bytes in all.
    private const int PerfDataSize = 64, TemperatureOffset = 56, AdapterPerfData = 62;
    private const uint NotSupported = 0xC00000BB, InvalidParameter = 0xC000000D;
    private const double FirstPause = 5, LongestPause = 300, ZeroPause = 5;
    // How long an adapter may answer with zero before it is taken for one without a sensor (one waking up reports a temperature
    // within seconds).
    private const double ZeroLimit = 60;
    private sealed class Adapter { public uint Handle; public bool Never, Stopped; public double RetryAt = double.NegativeInfinity, Pause, ZeroSince = double.NaN; }
    private readonly Dictionary<string, Adapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _data = Marshal.AllocHGlobal(PerfDataSize);
    private string? _last, _shown;
    private bool _disposed;
    private volatile SensorState _state;
    public SensorState State => _state;

    // The temperature for the adapter the page shows: its own where it has one. Otherwise the adapter that last gave one, and
    // then any other, so an adapter without a sensor (most integrated adapters) never takes the temperature away while the
    // busiest adapter changes. Adapter is the key of the one read. working: the keys of the adapters in use (some engine busy
    // above zero), in a fixed order; an idle adapter is never asked for another's sake, so a dedicated adapter powered down in a
    // hybrid laptop stays asleep. The shown adapter is asked, as the page shows its usage anyway. names: each adapter's name by its key.
    public (double Celsius, string Adapter)? ReadFor(string shown, IReadOnlyList<string> working, IReadOnlyDictionary<string, string> names, double now)
    {
        // A newly shown adapter is asked at once, not after a pause it built up while another was shown.
        if (shown != _shown) { _shown = shown; if (_adapters.TryGetValue(shown, out var fresh) && !fresh.Never) { fresh.Pause = 0; fresh.RetryAt = double.NegativeInfinity; } }
        var adapters = working;
        string Name(string key) => names.GetValueOrDefault(key) ?? "GPU";
        if (Read(shown, Name(shown), now) is { } own) return Found(own, shown);
        if (_last is { } last && last != shown && adapters.Contains(last, StringComparer.OrdinalIgnoreCase) && Read(last, Name(last), now) is { } kept) return Found(kept, last);
        foreach (var adapter in adapters)
            if (adapter != shown && adapter != _last && Read(adapter, Name(adapter), now) is { } other) return Found(other, adapter);
        // The sensor may sit on an adapter that has been idle (a laptop's dedicated card) while the shown one is an
        // integrated or virtual adapter. Asked only once the busy ones gave nothing, and never for a sleeping adapter,
        // so an idle card is not woken on every tick for a temperature nobody shows.
        foreach (var adapter in names.Keys)
            if (adapter != shown && adapter != _last && !adapters.Contains(adapter, StringComparer.OrdinalIgnoreCase)
                && Read(adapter, Name(adapter), now) is { } idle) return Found(idle, adapter);
        // It is None once every adapter asked has said it does not report the data, or has answered zero for ZeroLimit seconds:
        // an adapter waking up reports zero for a moment only, and the page keeps room for it meanwhile. One that gives a temperature
        // later makes it Found again.
        if (_state == SensorState.Unknown && _adapters.Count > 0
            && _adapters.Values.All(known => known.Never || known.Stopped || !double.IsNaN(known.ZeroSince) && now - known.ZeroSince >= ZeroLimit)) _state = SensorState.None;
        return null;
    }
    private (double, string) Found(double celsius, string adapter) { _last = adapter; _state = SensorState.Found; return (celsius, adapter); }

    // The adapter by the counters' key ("luid_0x…_0x…") and its name, or null without a temperature for now.
    public double? Read(string key, string name, double now)
    {
        if (_disposed) return null;
        if (!_adapters.TryGetValue(key, out var adapter)) _adapters[key] = adapter = new Adapter();
        var device = new GuardedDevice(GuardedReading.GpuTemperature, name, name);
        adapter.Stopped = SensorGuard.Stopped(device.Reading, device.Device);
        if (adapter.Never || adapter.Stopped || now < adapter.RetryAt) return null;
        if (adapter.Handle == 0 && (adapter.Handle = SensorGuard.Ask(device, () => Open(key), stuck)) == 0) return Wait(adapter, now);
        for (int i = 0; i < PerfDataSize; i += 8) Marshal.WriteInt64(_data, i, 0);
        var query = new QueryAdapterInfo { Adapter = adapter.Handle, Type = AdapterPerfData, Data = _data, Size = PerfDataSize };
        uint status;
        try { status = SensorGuard.Ask(device, () => (uint)D3DKMTQueryAdapterInfo(ref query), stuck); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { Debug.WriteLine(ex); adapter.Never = true; return null; }
        if (status == 0)
        {
            if (Temperatures.FromDeciCelsius((uint)Marshal.ReadInt32(_data, TemperatureOffset)) is { } celsius) { adapter.Pause = 0; adapter.ZeroSince = double.NaN; return celsius; }
            if (double.IsNaN(adapter.ZeroSince)) adapter.ZeroSince = now;
            // An answer without a temperature is no failure: asked again shortly, without growing the pause.
            adapter.RetryAt = now + ZeroPause;
            return null;
        }
        // The driver does not report the data at all: this adapter is left alone from now on. Any other failure (the adapter
        // went away, the driver was busy) opens it again after the pause.
        Close(adapter.Handle); adapter.Handle = 0;
        if (status is NotSupported or InvalidParameter) { adapter.Never = true; return null; }
        return Wait(adapter, now);
    }
    private static double? Wait(Adapter adapter, double now)
    {
        adapter.Pause = Math.Clamp(adapter.Pause * 2, FirstPause, LongestPause);
        adapter.RetryAt = now + adapter.Pause;
        return null;
    }

    private static uint Open(string adapter)
    {
        if (AdapterLuid.Parse(adapter) is not { } luid) return 0;
        var open = new OpenAdapterFromLuid { Low = luid.Low, High = luid.High };
        try { return D3DKMTOpenAdapterFromLuid(ref open) == 0 ? open.Adapter : 0; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { Debug.WriteLine(ex); return 0; }
    }

    private static void Close(uint handle)
    {
        if (handle == 0) return;
        var close = new CloseAdapter { Adapter = handle };
        try { D3DKMTCloseAdapter(ref close); } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var adapter in _adapters.Values) Close(adapter.Handle);
        _adapters.Clear();
        Marshal.FreeHGlobal(_data);
    }

    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapterFromLuid { public uint Low; public int High; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);
}

