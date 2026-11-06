using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TwelveIsland.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;

namespace TwelveIsland.Native;

// What Windows reports about power to a normal user: nothing here needs administrator rights or installs a driver. Processor power
// comes from the Energy Meter counters (Windows exposes the processor's RAPL registers through its own Energy Meter Interface
// driver); a graphics adapter's board power from the driver's nvidia-smi (NvidiaSmi), and AMD's and Intel's discrete adapters' from
// their drivers with their usage (GpuDrivers); the battery from Windows.Devices.Power; the power mode from the power API. Each
// reader belongs to the sampling thread, as the providers in SystemSampler do; the sensor states are read from the UI thread.

// The whole machine's power (PowerResolver chooses between the sources, PowerInputs hands them over). The counters are in the query
// only while power is wanted. A graphics adapter is asked only while it is doing work (Awake seconds after its last use): asking
// an idle hybrid-graphics laptop's discrete adapter would wake it from sleep and cost the battery more than the figure is worth.
// The meter counters are in a query of their own, guarded (SensorGuard): firmware whose meter never answers stops the meter, and
// the power then comes from the other sources. stuck: the sampler gives up its thread for another.
internal sealed class PowerProvider(PdhQuery? pdh, Action stuck) : IDisposable
{
    private const string EnergyPath = @"\Energy Meter(*)\Power", MeterPath = @"\Power Meter(*)\Power";
    private const double Awake = 8, Busy = .03;
    // Ticks without any source before the machine is taken for one without (a battery machine keeps trying: unplugged it has one).
    private const int GiveUpAfter = 5;
    private readonly GuardedDevice _device = new(GuardedReading.PowerMeter, SensorGuard.Board, Loc.T("功率计", "Power meter"));
    private bool _added, _available, _stopped;
    private int _empty;
    private bool _everFound;
    private volatile SensorState _state = SensorState.Unknown;
    private readonly Dictionary<string, double> _busyAt = new(StringComparer.OrdinalIgnoreCase);
    private NvidiaSmi? _smi;
    private double _smiStartedAt;
    public SensorState State => _state;

    // Called where the sampler collects its own query: while power is wanted the Energy Meter counters are added and collected, so the
    // first collection after the page opens already holds a sample of them and the next (the first snapshot, 300 ms later) a second: a
    // rate needs two. Taken out again once it is not wanted, or once the meter is stopped. Whether they were collected.
    public bool Prepare(bool wanted)
    {
        if (pdh is null) return false;
        _stopped = SensorGuard.Stopped(_device.Reading, _device.Device);
        if (!wanted || _stopped)
        {
            if (_added) { pdh.Remove(EnergyPath); pdh.Remove(MeterPath); _added = false; }
            return false;
        }
        return SensorGuard.Ask(_device, () =>
        {
            if (!_added) { _added = true; _available = pdh.Add(EnergyPath) | pdh.Add(MeterPath); }
            return _available && pdh.Collect();
        }, stuck);
    }

    // gpus: the adapters read this tick (usage tells whether one is awake); discharge: the battery's supply (BatteryModel.Discharge);
    // hasBattery: the machine runs on a battery, so a missing source may only be for the moment.
    // collected: what Prepare returned this tick.
    // processorWatts, processorMemory: the package's and the memory's power from the sensor service (enhanced sensors), which come
    // first; bridgeWatts: the package's from a hardware monitor the user runs, taken only where Windows gives none.
    public Reading<PowerSample> Sample(bool collected, bool wanted, double now, IReadOnlyList<GpuSample> gpus, Reading<double> discharge, bool hasBattery,
        double? processorWatts = null, double? processorMemory = null, double? bridgeWatts = null)
    {
        if (!wanted)
        {
            if (_added && pdh is not null) { pdh.Remove(EnergyPath); pdh.Remove(MeterPath); _added = false; }
            _smi?.Dispose(); _smi = null; _busyAt.Clear();
            return Reading<PowerSample>.Unavailable(Why.Waiting);
        }
        Reading<double> system = Reading<double>.Unavailable(""), cpu = system, memory = system;
        if (_available && collected && pdh is not null)
            (system, cpu, memory) = PowerResolver.FromCounters(pdh.Read(EnergyPath), pdh.Read(MeterPath));
        if (processorWatts is { } package) cpu = package;
        else if (!cpu.HasValue && bridgeWatts is { } bridged) cpu = bridged;
        if (!memory.HasValue && processorMemory is { } dram) memory = dram;
        var inputs = new PowerInputs { SystemMeter = system, BatteryDischarge = discharge, Cpu = cpu, Memory = memory, Gpus = ReadGpus(gpus, now) };
        var power = PowerResolver.Resolve(inputs);
        if (power.HasValue) { _empty = 0; _everFound = true; _state = SensorState.Found; }
        // None only for a machine that has never given a reading and has no NVIDIA adapter that may yet be busy; a laptop that gives
        // none on mains may give one on battery, which the panel asks again for (MainWindow.StripSensors). Once found it stays found:
        // a figure missing for a moment (an idle adapter) is a dash, not a sensor that has gone.
        else if (!_everFound && (collected || pdh is null || _stopped) && ++_empty >= GiveUpAfter && !gpus.Any(gpu => gpu.Name?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true
            || gpu.Kind != GpuKind.Integrated && gpu.VendorId is GpuDriverReadings.Amd or GpuDriverReadings.Intel))
            _state = SensorState.None;
        return power;
    }

    private List<GpuPowerInput> ReadGpus(IReadOnlyList<GpuSample> gpus, double now)
    {
        var read = new List<GpuPowerInput>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpu in gpus)
        {
            string name = gpu.Name ?? "GPU";
            // AMD's and Intel's discrete adapters gave their power with their usage (GpuDrivers), while they work.
            if (gpu.DriverWatts is { } watts)
            {
                read.Add(new GpuPowerInput(name, watts, GpuDriverReadings.SourceName(gpu.VendorId)));
                continue;
            }
            // NVIDIA's through nvidia-smi, and only an adapter that is working.
            if (gpu.VendorId != GpuDriverReadings.Nvidia && !(gpu.VendorId == 0 && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) continue;
            // Identical cards are told apart by their bus, else by their order (the nth of this name).
            int ordinal = seen.GetValueOrDefault(name); seen[name] = ordinal + 1;
            string key = name + "#" + ordinal;
            // Idle counts as under a few percent of Windows' figure (the desktop's own drawing): the helper takes about 25 MB while it
            // runs, and starting it for an adapter that only draws the desktop is not worth it.
            if (gpu.EngineUsage >= Busy) _busyAt[key] = now;
            if (!_busyAt.TryGetValue(key, out var last) || now - last > Awake)
            {
                read.Add(new GpuPowerInput(name, Reading<double>.Unavailable(Loc.T("空闲", "idle"))));
                continue;
            }
            if (_smi is not null && PowerResolver.RestartDue(_smi.Ended, now, _smiStartedAt)) { _smi.Dispose(); _smi = null; }
            if (_smi is null) { _smi = new NvidiaSmi(); _smiStartedAt = now; }
            read.Add(new GpuPowerInput(name, _smi.Read(GpuDriverReadings.Bus(gpu.Location), name, ordinal)));
        }
        // Every adapter idle for good: the helper ends, and its memory goes with it.
        if (_smi is not null && _busyAt.Values.All(at => now - at > Awake)) { _smi.Dispose(); _smi = null; }
        return read;
    }

    public void Dispose() { _smi?.Dispose(); _smi = null; }
}

// NVIDIA's board power, read by the driver's own nvidia-smi.exe (in the system folder) running in loop mode in a process of its own.
// Its management library cannot be taken out of this process once loaded: nvml.dll and the driver's copy of it pin themselves, so
// closing it returned none of its 20 MB (measured). In a helper the memory leaves with the process: it is
// started when an adapter works and ended once it has been idle for a while or power is no longer wanted. A job object ends it with
// this process, so a crash leaves none behind. One line a second, "bus id, name, watts", for every NVIDIA adapter in the driver's
// order. Current drivers put the tool in the system folder; older ones in Program Files (NVIDIA Corporation\NVSMI).
internal sealed class NvidiaSmi : IDisposable
{
    private readonly Process? _process;
    private readonly IntPtr _job;
    private readonly object _lock = new();
    private List<(uint? Bus, string Name, double? Watts)> _ready = [], _batch = [];
    private long _lastLine, _readyAt;
    private readonly string? _failure;

    public NvidiaSmi()
    {
        string? path = new[] { Environment.SystemDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI") }
            .Select(folder => Path.Combine(folder, "nvidia-smi.exe")).FirstOrDefault(File.Exists);
        if (path is null) { _failure = Loc.T("无 NVIDIA 驱动工具", "no NVIDIA driver tool"); return; }
        try
        {
            var start = new ProcessStartInfo(path, "--query-gpu=pci.bus_id,name,power.draw --format=csv,noheader,nounits -l 1")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(path)!
            };
            _process = Process.Start(start);
            if (_process is null) { _failure = Loc.T("无法启动", "did not start"); return; }
            _job = CreateJobObjectW(IntPtr.Zero, null);
            if (_job != IntPtr.Zero)
            {
                // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: LimitFlags (KILL_ON_JOB_CLOSE, 0x2000) at offset 16 of the basic information.
                int size = IntPtr.Size == 8 ? 144 : 112;
                var info = Marshal.AllocHGlobal(size);
                try
                {
                    for (int i = 0; i < size; i += 8) Marshal.WriteInt64(info, i, 0);
                    Marshal.WriteInt32(info, 16, 0x2000);
                    if (SetInformationJobObject(_job, 9, info, (uint)size)) AssignProcessToJobObject(_job, _process.Handle);
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            _process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Line(line); };
            _process.BeginOutputReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine(ex); _failure = Loc.T("无法启动", "did not start");
        }
    }

    // One line, on a thread of the pool: the adapters' lines of one second arrive together; a pause starts the next batch.
    private void Line(string line)
    {
        int first = line.IndexOf(','), comma = line.LastIndexOf(',');
        if (first <= 0 || comma <= first) return;
        uint? bus = NvidiaSmiBus(line[..first].Trim());
        string name = line[(first + 1)..comma].Trim();
        double? watts = double.TryParse(line[(comma + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_batch.Count > 0 && (now - _lastLine) * 1000 / Stopwatch.Frequency > 400) { _ready = _batch; _readyAt = _lastLine; _batch = []; }
            _batch.Add((bus, name, watts)); _lastLine = now;
            // Until the first batch is complete the lines so far stand in for it.
            if (_ready.Count == 0 || _readyAt == 0) { _ready = [.. _batch]; _readyAt = now; }
        }
    }

    // Whether the helper is not running: it did not start, or it has ended.
    public bool Ended => _failure is not null || _process is { HasExited: true };

    // The adapter on this PCI bus; where Windows gives no bus, or the driver lists none on it, the nth adapter of this name in the
    // driver's order (identical cards are told apart by it). The adapter's name is DXGI's, the driver's alike.
    public Reading<double> Read(uint? bus, string adapter, int ordinal)
    {
        if (_failure is not null) return Reading<double>.Unavailable(_failure);
        if (_process is { HasExited: true }) return Reading<double>.Unavailable(Loc.T("读取已停止", "stopped"));
        List<(uint? Bus, string Name, double? Watts)> ready; long at;
        lock (_lock) { ready = _ready; at = _readyAt; }
        if (ready.Count == 0) return Reading<double>.Unavailable(Loc.T("启动中", "starting"));
        if ((Stopwatch.GetTimestamp() - at) * 1000 / Stopwatch.Frequency > 4000) return Reading<double>.Unavailable(Loc.T("无新读数", "stale"));
        if (bus is not null && ready.FindIndex(line => line.Bus == bus) is var on and >= 0)
            return ready[on].Watts is { } watts && PowerResolver.Plausible(watts) ? watts : Reading<double>.Unavailable(Loc.T("无功率数据", "no power data"));
        return PowerResolver.PickGpu(ready.ConvertAll(line => (line.Name, line.Watts)), adapter, ordinal);
    }

    // The bus of nvidia-smi's PCI bus id, "00000000:01:00.0" (domain, bus, device.function, in hexadecimal).
    private static uint? NvidiaSmiBus(string id)
    {
        var parts = id.Split(':');
        return parts.Length == 3 && uint.TryParse(parts[1], System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint bus) ? bus : null;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) { _process.Kill(); _process.WaitForExit(1000); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { Debug.WriteLine(ex); }
        _process?.Dispose();
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

// The machine's own batteries: each one's report, the mains connection, Battery saver and the power mode (BatteryInputs). The
// batteries are listed now and then and read on every tick; a machine without one is known after the first listing (SensorState.None)
// and asked again only every minute (a docking battery can appear).
internal sealed class BatteryProvider
{
    private const int Relist = 60;
    private readonly List<Windows.Devices.Power.Battery> _batteries = [];
    private int _sinceList = Relist, _unusable;
    private volatile SensorState _state = SensorState.Unknown;
    public SensorState State => _state;
    public bool Present => _state == SensorState.Found;

    // Whether the system says it has a battery, asked of the system directly (no reading of the batteries): the page needs it to
    // know how many lines to keep before the sampler has run. 128 is "no system battery", 255 "unknown".
    public static bool SystemHasBattery => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);
    // The battery as the system itself reports it, at once and without the battery listing (a charge, the mains, Battery saver, the time Windows
    // estimates, the power mode): shown by the page until the sampler's first reading arrives. Unavailable on a machine without a battery.
    public static Reading<BatterySample> Quick()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag is 128 or 255 || status.BatteryLifePercent > 100) return Reading<BatterySample>.Unavailable(BatteryModel.NoBattery);
        bool? ac = status.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        var cell = new BatteryCell("system", null, null, null, null, (status.BatteryFlag & 8) != 0, ac == false);
        return BatteryModel.Build(new BatteryInputs([cell], ac, (status.SystemStatusFlag & 1) != 0, BatteryModel.ModeOf(Overlay()), status.BatteryLifePercent,
            status.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null));
    }

    // Whether the machine is on mains now (a laptop that gives no power reading there may give one on battery).
    public static bool OnMains => GetSystemPowerStatus(out var status) && status.ACLineStatus == 1;

    public (BatteryInputs Inputs, Reading<BatterySample> Sample) Sample()
    {
        if (_sinceList++ >= Relist) List();
        var status = SystemStatus();
        var cells = new List<BatteryCell>(_batteries.Count);
        foreach (var battery in _batteries)
        {
            try
            {
                var report = battery.GetReport();
                if (report.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                cells.Add(new BatteryCell(battery.DeviceId ?? "", report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours,
                    report.DesignCapacityInMilliwattHours, report.ChargeRateInMilliwatts, report.Status == Windows.System.Power.BatteryStatus.Charging,
                    report.Status == Windows.System.Power.BatteryStatus.Discharging));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        }
        // The system's own view where the batteries give no report (a driver that lists none): it knows a battery is there and its share.
        bool systemBattery = status.BatteryFlag is not (128 or 255);
        var inputs = new BatteryInputs(cells, status.AcOnline, status.Saver, BatteryModel.ModeOf(Overlay()), status.Percent, status.Remaining);
        if (cells.Count == 0 && systemBattery && status.Percent is not null)
            inputs = inputs with { Cells = [new BatteryCell("system", null, null, null, null, (status.BatteryFlag & 8) != 0, status.AcOnline == false)] };
        var sample = BatteryModel.Build(inputs);
        if (sample.HasValue) { _unusable = 0; _state = SensorState.Found; }
        else if (cells.Count == 0 && !systemBattery) _state = SensorState.None;
        // A battery whose reports never give a share is taken for none after a few reads, so the row stops waiting for it.
        else if (_state != SensorState.Found && ++_unusable >= 5) _state = SensorState.None;
        return (inputs, sample);
    }

    private void List()
    {
        _sinceList = 0;
        try
        {
            var found = DeviceInformation.FindAllAsync(Windows.Devices.Power.Battery.GetDeviceSelector()).AsTask().GetAwaiter().GetResult();
            var batteries = new List<Windows.Devices.Power.Battery>();
            foreach (var info in found)
                if (Windows.Devices.Power.Battery.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult() is { } battery) batteries.Add(battery);
            _batteries.Clear(); _batteries.AddRange(batteries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex);
        }
    }

    // GetSystemPowerStatus: the mains connection, the share Windows shows, the time left it estimates, Battery saver.
    private static (bool? AcOnline, byte BatteryFlag, double? Percent, TimeSpan? Remaining, bool Saver) SystemStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (null, 255, null, null, false);
        return (status.ACLineStatus switch { 0 => false, 1 => true, _ => null }, status.BatteryFlag, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null,
            status.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null, (status.SystemStatusFlag & 1) != 0);
    }

    // The effective power mode; the zero GUID (Balanced) when the call is not there. Asking for it waits on the power service, 130 ms and
    // more on some machines, so it is asked once and again only after Windows reports that the effective power mode changed (the power
    // slider or Battery saver, version 1 of the notifications; Windows calls back once at once on registering). Without the notifications
    // it is asked every time, as before.
    private static readonly Lock OverlayGate = new();
    private static readonly ModeChanged OnModeChanged = (_, _) => Interlocked.Increment(ref _modeChanges);
    private static int _modeChanges;
    private static bool _modeWatched, _modeTried;
    private static (Guid Scheme, int At)? _overlay;
    private static Guid Overlay()
    {
        lock (OverlayGate)
        {
            try
            {
                if (!_modeTried) { _modeTried = true; _modeWatched = PowerRegisterForEffectivePowerModeNotifications(1, OnModeChanged, IntPtr.Zero, out _) == 0; }
                int changes = Volatile.Read(ref _modeChanges);
                if (_modeWatched && _overlay is { } known && known.At == changes) return known.Scheme;
                var scheme = PowerGetEffectiveOverlayScheme(out var read) == 0 ? read : Guid.Empty;
                _overlay = (scheme, changes);
                return scheme;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return Guid.Empty; }
        }
    }
    private delegate void ModeChanged(int mode, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, ModeChanged callback, IntPtr context, out IntPtr registration);
}
