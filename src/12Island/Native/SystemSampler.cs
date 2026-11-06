using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Reads the system for the monitor page on a thread of its own, only while a panel shows the page and for a short while
// after, so the page costs nothing while it is not looked at. The structure follows ftop's SystemSampler by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see ThirdPartyNotices.txt): one provider per module keeps the counters
// it needs for deltas and returns a Reading; nothing here throws; listing processes, the costliest reading, runs every
// other tick. The readings themselves are the Windows ones Task Manager shows.
// A guarded reading (SensorGuard) that never answers holds the thread it was asked on for good; the sampler then starts another
// thread with providers of its own, and the old one ends should the driver ever answer (ReadingAbandoned).
internal sealed class SystemSampler : IDisposable
{
    // Reopening the page within this time finds the sampler still running and the readings current.
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);
    // After a pause the counters are read once and again after this short window, so the page has figures almost at once.
    private const int PrimeMilliseconds = 300;
    // The network readings the sparklines draw, oldest first: a minute at one reading a second.
    public const int HistoryLength = 60;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _linger;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    // Raised when a thread is replaced: only the thread of the current generation reads.
    private int _generation;
    private volatile bool _wanted, _disposed, _listProcesses, _primeProcesses, _primeCounters;
    private volatile int _intervalMilliseconds = 1000;
    // The providers belong to the sampling thread, which creates them: adding the performance counters can take tens of
    // milliseconds, which the panel must not wait for. Only HoldProcesses reaches them from the UI thread. The thermal zones and the
    // power meter have queries of their own (_zonesPdh, _meterPdh), so a stuck collection is known to be theirs.
    private PdhQuery? _pdh, _zonesPdh, _meterPdh;
    private CpuProvider? _cpu;
    private GpuProvider? _gpu;
    private GpuDrivers? _drivers;
    private readonly MonitorBridgeReader _bridges = new();
    // Until when (Now) the processor's temperature counts as coming from its own sensors or a hardware monitor (CpuTemperatureSource),
    // and whether the last reading did: while one does, the zones are not read. The time bridges a reading or two that fails.
    private const double HeatHold = 30;
    private double _processorHeatUntil = double.NegativeInfinity, _driverHeatUntil = double.NegativeInfinity;
    private bool _processorHeat;
    private ThermalZones? _zones;
    private GpuTemperatures? _gpuTemperatures;
    private readonly StorageReader _storage = new();
    // Whether the processor's and the graphics adapter's temperatures are read, the drives, and the drives' temperatures (SetExtras).
    // _powerWanted: the whole machine's power (the Energy Meter counters, NVIDIA's board power); _batteryWanted: the battery alone, which
    // the machine's charge reading needs without any of the rest.
    // _gpuWanted: the graphics adapters' counters, for the page, the row's GPU reading and the power.
    private volatile bool _temperatures, _storageWanted, _driveTemperatures, _powerWanted, _batteryWanted, _gpuWanted;
    // The whole machine's power and the machine's battery (PowerProvider, BatteryProvider), made on the sampling thread.
    private PowerProvider? _power;
    private BatteryProvider? _battery;
    private readonly Queue<double> _powerHistory = new(HistoryLength);
    private readonly PowerStats _powerStats = new(HistoryLength);
    // Where the last whole-machine figure came from: the peak and mean (of the curve's readings) start over when it changes (plugged in
    // or out), so a measurement and an estimate are never counted together. The curve keeps its readings.
    private PowerSource? _powerSource;
    // The processor's and the graphics' own power, read with the whole machine's, for the page's power line when it shows one of them.
    private readonly PowerTrail _cpuPower = new(HistoryLength), _gpuPower = new(HistoryLength);
    // The frame rate of the program in front, while a collapsed row or an open page shows it (SetExtras; FrameRates).
    private readonly FrameRates _frames = new();
    private readonly NetworkProvider _network = new();
    private readonly ProcessProvider _processes = new();
    private Reading<ProcessList> _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting);
    // Listing processes, the costliest reading, runs about every two seconds whatever the interval, and only while the page is
    // open: the notch and the capsule show no processes. A two-second window also steadies the per-process figures.
    private const double ProcessSeconds = 1.5;
    private double _processesAt = double.NegativeInfinity;
    private readonly Queue<(double Down, double Up)> _history = new(HistoryLength);
    private readonly Queue<double> _cpuHistory = new(HistoryLength);
    // Each graphics adapter's usage, by LUID, so the page shows the history of whichever adapter it shows.
    private readonly Dictionary<ulong, Queue<double>> _gpuHistories = [];
    // The saved choice of graphics adapter (DeckSettings.MonitorGpu and MonitorGpuName), and the adapter 自动 showed last, so that
    // it stays (GpuChoice.Auto). The LUID is written on the sampling thread and read on the UI thread by ShowGpuChoice.
    private readonly Func<(string? Key, string? Name)> _gpuChoice;
    private ulong _autoGpu;

    public SystemSampler(Dispatcher dispatcher, Func<(string? Key, string? Name)> gpuChoice)
    {
        _dispatcher = dispatcher; _gpuChoice = gpuChoice;
        _linger = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Linger };
        // Once paused the readings grow old, so they are dropped: a page opened later shows dashes until new ones arrive
        // instead of old figures as if they were current.
        _linger.Tick += (_, _) => { _linger.Stop(); _wanted = false; Latest = null; };
    }

    // The newest readings while the sampler runs; null once it has paused. Raised on the UI thread.
    public MonitorSnapshot? Latest { get; private set; }
    public event Action<MonitorSnapshot>? Updated;
    // The saved choice of graphics adapter was found under an older key (the first) and is to be saved under its key now (the
    // second). Raised on the UI thread.
    public event Action<string, string>? GpuKeyUpgraded;
    // While set, rows already in the process lists keep their rank (the pointer is over a list).
    public bool HoldProcesses { get => _processes.Hold; set => _processes.Hold = value; }

    // Called on the UI thread whenever a panel opens or closes the page or shows or hides the readings in its collapsed row.
    // processes: the expanded page is open, so the process list is read too.
    public void SetDemand(bool wanted, int intervalSeconds, bool processes)
    {
        if (_disposed) return;
        _intervalMilliseconds = Math.Clamp(intervalSeconds, 1, 2) * 1000;
        bool listing = _listProcesses;
        _listProcesses = wanted && processes;
        // The page opened while the collapsed row kept the sampler running: the processes are read at once and again shortly,
        // instead of waiting up to two readings for a list.
        if (_listProcesses && !listing && _wanted) { _primeProcesses = true; _wake.Set(); }
        if (!wanted) { if (_wanted && !_linger.IsEnabled) _linger.Start(); return; }
        _linger.Stop();
        if (_wanted) return;
        _wanted = true;
        if (_thread is null) StartThread(0);
        else _wake.Set();
    }

    private void StartThread(int generation)
    {
        var thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "TwelveIsland system monitor", Priority = ThreadPriority.BelowNormal };
        // The shell's icon lookup (ProcessIcons) needs a single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();
    }

    // A guarded reading on the thread of this generation did not answer (SensorGuard): another thread takes over, and the reading
    // is not asked again. Called on the guard's thread.
    private void Replace(int generation)
    {
        if (_disposed || Interlocked.CompareExchange(ref _generation, generation + 1, generation) != generation) return;
        StartThread(generation + 1);
    }

    // Called on the UI thread with what the panels show: temperatures, the processor's and the graphics adapter's (the expanded page
    // with temperatures on, or the collapsed row's temperature); storage, the drives (the page with storage on, or the collapsed
    // row's drive); driveTemperatures, the drives' temperatures (the page with both on). The drives are first read at once, so the
    // page knows early how many lines it needs for them. frames: a collapsed row or an open page shows the frame rate. gpu: the graphics
    // adapters (the page, the row's GPU reading, the power).
    public void SetExtras(bool temperatures, bool storage, bool driveTemperatures, bool power = false, bool battery = false, bool frames = false, bool gpu = false)
    {
        if (!_disposed) _frames.Want(frames);
        bool newly = power && !_powerWanted || gpu && !_gpuWanted;
        _temperatures = temperatures; _storageWanted = storage; _driveTemperatures = storage && driveTemperatures; _powerWanted = power; _batteryWanted = power || battery;
        _gpuWanted = gpu || power;
        // Power or the adapters asked for while the sampler already runs (the collapsed row's readings kept it going): the counters are read
        // at once and again after the short priming window, so the first figure comes with the next snapshot and not a second reading later.
        if (newly && _wanted && _thread is not null) { _primeCounters = true; _wake.Set(); }
        if (storage && (_storage.Drives == SensorState.Unknown || _driveTemperatures && _storage.Temperatures == SensorState.Unknown)) _storage.Start(_driveTemperatures);
    }
    // Whether the processor's zones, the graphics adapters and the drives have given temperatures, and whether drives were found and
    // how many (-1 until read); the page keeps room for what has turned up or has not been asked yet.
    public SensorState CpuSensor => Now() < Volatile.Read(ref _processorHeatUntil) ? SensorState.Found : _zones?.State ?? SensorState.Unknown;
    // Found while an adapter's driver gives a temperature where the graphics kernel gives none (GpuSample.DriverTemperature).
    public SensorState GpuSensor => Now() < Volatile.Read(ref _driverHeatUntil) ? SensorState.Found : _gpuTemperatures?.State ?? SensorState.Unknown;
    public SensorState DriveSensor => _storage.Temperatures;
    public SensorState StorageState => _storage.Drives;
    public int StorageCount => _storage.Count;
    // Whether the machine gives a power reading and has a battery of its own; Unknown until read.
    public SensorState PowerSensor => _power?.State ?? SensorState.Unknown;
    public SensorState BatterySensor => _battery?.State ?? SensorState.Unknown;
    // Whether frame rates can be read: Found while they are, None without permission, Unknown before they are asked for.
    public SensorState FramesSensor => _frames.State;

    // generation: the thread's own; a thread replaced (Replace) leaves its providers to the thread stuck in them.
    private void Run(int generation)
    {
        // Without performance counters (the library is missing or broken) the processor falls back to the kernel's times
        // and the GPU reads as unavailable; nothing here may end the process.
        static PdhQuery? Query()
        {
            try { return new PdhQuery(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { Debug.WriteLine(ex); return null; }
        }
        Action stuck = () => Replace(generation);
        _pdh = Query(); _zonesPdh = Query(); _meterPdh = Query();
        _cpu = new CpuProvider(_pdh);
        _gpu = new GpuProvider(_pdh);
        _drivers = new GpuDrivers(stuck);
        _zones = new ThermalZones(_zonesPdh, stuck);
        _power = new PowerProvider(_meterPdh, stuck);
        _gpuTemperatures = new GpuTemperatures(stuck);
        _battery = new BatteryProvider();
        bool primed = false, first = false;
        while (!_disposed && Volatile.Read(ref _generation) == generation)
        {
            try
            {
                if (!_wanted) { primed = false; Rest(); _wake.WaitOne(); continue; }
                bool primeCounters = _primeCounters;
                _primeCounters = false;
                if (!primed || _primeProcesses || primeCounters)
                {
                    // The counters are read once to start the deltas; the processes again on the first reading, so their
                    // figures cover the short window and not the whole pause. Power or adapters asked for while the sampler runs are primed the same way:
                    // one collection now, and the next (the first snapshot) has the two samples a rate needs.
                    bool primeProcesses = _primeProcesses;
                    _primeProcesses = false;
                    // Readings starting (the monitor opened or came back): a hardware monitor started meanwhile is looked for again.
                    if (!primed) { _bridges.Rescan(); Collect(listProcesses: true); }
                    else if (primeCounters) Collect(listProcesses: primeProcesses && _listProcesses);
                    else if (_listProcesses) _processes.Sample(Now());
                    primed = true; first = true;
                    _wake.WaitOne(PrimeMilliseconds);
                    continue;
                }
                var snapshot = Collect(listProcesses: first);
                first = false;
                if (_disposed) break;
                // A reading taken just as the sampler paused would bring back figures that then grow old. The adapter shown is
                // resolved again here: a reading taken before the user chose another adapter would otherwise bring the old one back.
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_disposed || !_wanted) return;
                    Latest = WithGpu(snapshot, Volatile.Read(ref _autoGpu), out _);
                    Updated?.Invoke(Latest);
                });
                _wake.WaitOne(_intervalMilliseconds);
            }
            // The driver answered after this thread was replaced: the new thread reads now.
            catch (ReadingAbandoned) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A reading that fails in an unexpected way is skipped; the page keeps its last figures.
                Debug.WriteLine(ex);
                _wake.WaitOne(_intervalMilliseconds);
            }
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Paused: the histories start over, and what only the readings need is let go until they start again: the process table's buffer
    // (a megabyte or more), the last list and the programs' icons, read again with the first list.
    private void Rest()
    {
        _history.Clear(); _cpuHistory.Clear(); _gpuHistories.Clear(); _powerHistory.Clear(); _powerStats.Reset(); _powerSource = null; _cpuPower.Clear(); _gpuPower.Clear();
        _power!.Sample(false, false, 0, [], default, false); _storage.Rest(); _bridges.Rest();
        _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting); _processes.Dispose(); ProcessIcons.Clear();
    }

    private MonitorSnapshot Collect(bool listProcesses)
    {
        double now = Now();
        bool meter = _power!.Prepare(_powerWanted);
        _gpu!.Want(_gpuWanted);
        bool pdh = _pdh?.Collect() == true;
        if (!_listProcesses) { _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting); _processesAt = double.NegativeInfinity; }
        else if (listProcesses || !_lastProcesses.HasValue || now - _processesAt >= ProcessSeconds)
        {
            _lastProcesses = _processes.Sample(now); _processesAt = now;
        }
        var network = _network.Sample(now);
        if (network.TryGet(out var rates))
        {
            if (_history.Count == HistoryLength) _history.Dequeue();
            _history.Enqueue((rates.DownBytesPerSecond, rates.UpBytesPerSecond));
        }
        bool temperatures = _temperatures;
        var cpu = _cpu!.Sample(pdh); var gpus = _gpu.Sample(pdh);
        // Asked on every tick, so the zones' counters leave the query once temperatures are no longer wanted or a better source gives them.
        if (_zones!.Sample(temperatures && !_processorHeat, now) is { } zone) cpu = cpu.Map(sample => sample with { Temperature = zone.Celsius, TemperatureZone = zone.Zone });
        // A hardware monitor the user runs (HWiNFO's shared memory or LibreHardwareMonitor's web server) stands in front of the
        // zones; without one the zones are the processor's only source.
        var bridge = temperatures || _powerWanted ? _bridges.Read(now) : default;
        if (temperatures && bridge.Celsius is { } bridged)
            cpu = cpu.Map(sample => sample with { Temperature = bridged, TemperatureZone = null, TemperatureSource = bridge.Source });
        _processorHeat = temperatures && cpu.OrDefault?.TemperatureSource is not null and not CpuTemperatureSource.Zone;
        if (_processorHeat) Volatile.Write(ref _processorHeatUntil, now + HeatHold);
        IReadOnlyList<StorageDrive> storage = _storageWanted ? _storage.Sample(now, _driveTemperatures) : [];
        Remember(_cpuHistory, cpu.OrDefault?.Usage);
        // The usage each adapter's driver gives, where it gives one (GpuDrivers); the curves follow the figure shown.
        var adapters = gpus.TryGet(out var read) ? Remember(_drivers!.Read(read, now, temperatures)) : [];
        if (!_gpuWanted) _gpuHistories.Clear();
        // The machine's power and battery: read only while something shows them; the history and the peak start over once none does.
        var power = Reading<PowerSample>.Unavailable(Why.Waiting);
        var battery = Reading<BatterySample>.Unavailable(Why.Waiting);
        bool powerWanted = _powerWanted, batteryWanted = _batteryWanted;
        if (batteryWanted)
        {
            // Apart from the rest: a battery or a driver that fails in an unexpected way costs these two readings, never the page's.
            try
            {
                var (batteryInputs, batterySample) = _battery!.Sample();
                battery = batterySample;
                if (powerWanted)
                {
                    power = _power!.Sample(meter, true, now, adapters, BatteryModel.Discharge(batteryInputs, batterySample), _battery.Present,
                        null, null, bridge.Watts);
                    if (power.TryGet(out var drawn))
                    {
                        if (_powerHistory.Count == HistoryLength) _powerHistory.Dequeue();
                        if (_powerSource is { } last && last != drawn.Source) _powerStats.Reset();
                        _powerSource = drawn.Source;
                        _powerHistory.Enqueue(drawn.Watts); _powerStats.Add(drawn.Watts, now);
                        if (PowerResolver.PartWatts(drawn, "cpu") is { } cpuWatts) _cpuPower.Add(cpuWatts, now);
                        if (PowerResolver.PartWatts(drawn, "gpu") is { } gpuWatts) _gpuPower.Add(gpuWatts, now);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine(ex);
                power = Reading<PowerSample>.Unavailable(ex.Message); battery = Reading<BatterySample>.Unavailable(ex.Message);
            }
        }
        // Power no longer wanted (the page left, the row dropped the reading, the display went off): the counters leave the query, the
        // NVIDIA helper ends and the history starts over, at this very reading and not after the sampler's pause.
        if (!powerWanted)
        {
            if (_powerHistory.Count > 0) { _powerHistory.Clear(); _powerStats.Reset(); }
            _powerSource = null;
            _cpuPower.Clear(); _gpuPower.Clear();
            _power!.Sample(false, false, now, [], default, false);
        }
        // Unavailable while nothing shows the frame rate, so one shown again starts from a dash, not an old rate.
        var frames = _frames.Sample(now);
        // The program's icon, read here as the process list's are (the shell's lookup wants this thread's apartment).
        if (frames.TryGet(out var drawing) && drawing.Path is { } program) ProcessIcons.Prepare(program);
        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, Environment.ProcessorCount, cpu, MemoryProvider.Sample(), network,
            Reading<GpuSample>.Unavailable(gpus.Reason), _lastProcesses)
        {
            DownHistory = _history.Select(reading => reading.Down).ToArray(), UpHistory = _history.Select(reading => reading.Up).ToArray(),
            CpuHistory = _cpuHistory.ToArray(), Gpus = adapters, Storage = storage,
            Power = power, Battery = battery, PowerHistory = _powerHistory.ToArray(),
            PowerSummary = _powerStats.Summary(now),
            CpuPower = _cpuPower.Track(now), GpuPower = _gpuPower.Track(now),
            Frames = frames
        };
        var shown = WithGpu(snapshot, _autoGpu, out bool automatic);
        if (automatic && shown.Gpu.TryGet(out var gpu)) Volatile.Write(ref _autoGpu, gpu.Luid);
        return temperatures ? WithTemperature(shown, now) : shown;
    }
    // The shown adapter's temperature (GpuTemperatures.ReadFor): the chosen adapter's own, or, where it has no sensor, that of an
    // adapter in use that has one, named and marked with its kind. Only adapters in use are asked for another's sake.
    private MonitorSnapshot WithTemperature(MonitorSnapshot snapshot, double now)
    {
        if (!snapshot.Gpu.TryGet(out var gpu)) return snapshot;
        var working = snapshot.Gpus.Where(adapter => adapter.EngineUsage > 0).Select(adapter => GpuReadings.LuidText(adapter.Luid)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in snapshot.Gpus) names.TryAdd(GpuReadings.LuidText(adapter.Luid), adapter.Name ?? "GPU");
        if (_gpuTemperatures!.ReadFor(GpuReadings.LuidText(gpu.Luid), working, names, now) is not { } heat)
        {
            // The graphics kernel gives none: a driver's own temperature, the shown adapter's first, then any other
            // adapter's. The auto-chosen adapter may be integrated or virtual, with the sensor on the dedicated one.
            var driven = gpu.DriverTemperature is not null ? gpu : snapshot.Gpus.FirstOrDefault(adapter => adapter.DriverTemperature is not null);
            if (driven?.DriverTemperature is not { } own) return snapshot;
            Volatile.Write(ref _driverHeatUntil, now + HeatHold);
            var source = driven.Luid == gpu.Luid ? null : driven;
            return snapshot with
            {
                Gpu = gpu with
                {
                    Temperature = own, TemperatureFrom = source?.Name,
                    TemperatureKind = source is null ? null : GpuChoice.KindName(source.Kind) is { Length: > 0 } driverKind ? driverKind : Temperatures.AdapterKind(source.DedicatedTotal ?? 0)
                }
            };
        }
        var from = heat.Adapter == GpuReadings.LuidText(gpu.Luid) ? null : snapshot.Gpus.FirstOrDefault(adapter => GpuReadings.LuidText(adapter.Luid) == heat.Adapter);
        return snapshot with
        {
            Gpu = gpu with
            {
                Temperature = heat.Celsius,
                TemperatureFrom = from is null ? null : from.Name ?? "GPU",
                TemperatureKind = from is null ? null : GpuChoice.KindName(from.Kind) is { Length: > 0 } kind ? kind : Temperatures.AdapterKind(from.DedicatedTotal ?? 0)
            }
        };
    }
    // Each adapter with its own history; adapters that went away take theirs with them.
    private List<GpuSample> Remember(IReadOnlyList<GpuSample> gpus)
    {
        var adapters = new List<GpuSample>(gpus.Count);
        foreach (var gpu in gpus)
        {
            if (!_gpuHistories.TryGetValue(gpu.Luid, out var history)) _gpuHistories[gpu.Luid] = history = new Queue<double>(HistoryLength);
            Remember(history, gpu.Usage);
            adapters.Add(gpu with { History = history.ToArray() });
        }
        if (_gpuHistories.Count > adapters.Count)
            foreach (var luid in _gpuHistories.Keys.ToList()) if (!adapters.Exists(gpu => gpu.Luid == luid)) _gpuHistories.Remove(luid);
        return adapters;
    }
    // The snapshot showing the chosen graphics adapter, or why no adapter can be shown.
    // automatic: 自动 chose it, as asked or because the chosen adapter is not there.
    private MonitorSnapshot WithGpu(MonitorSnapshot snapshot, ulong previousAuto, out bool automatic)
    {
        var (key, name) = _gpuChoice();
        if (GpuChoice.Resolve(snapshot.Gpus, key, name, previousAuto, out automatic, out var upgrade) is not { } gpu) return snapshot;
        // A choice found by an older mark (a key saved before adapters had a location) is saved again under the adapter's key.
        if (upgrade is not null) _dispatcher.BeginInvoke(DispatcherPriority.Background, () => GpuKeyUpgraded?.Invoke(key!, upgrade));
        // Only the adapter shown carries a temperature (WithTemperature), not its entry in Gpus: resolved again to the same
        // adapter, the shown one is kept, or the temperature read for it would be lost on the way to the page.
        if (snapshot.Gpu.TryGet(out var shown) && shown.Luid == gpu.Luid) gpu = shown;
        return snapshot with { Gpu = gpu, GpuHistory = gpu.History };
    }
    // Shows the newest readings again for a new choice of graphics adapter, at once instead of with the next reading.
    public void ShowGpuChoice()
    {
        if (_disposed || Latest is not { } latest) return;
        Latest = WithGpu(latest, Volatile.Read(ref _autoGpu), out _);
        Updated?.Invoke(Latest);
    }
    private static void Remember(Queue<double> history, double? usage)
    {
        if (usage is not { } value) return;
        if (history.Count == HistoryLength) history.Dequeue();
        history.Enqueue(Math.Clamp(value, 0, 1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _wanted = false; _linger.Stop();
        _frames.Dispose();
        _wake.Set();
        // The thread may be inside a reading; it ends on its own as a background thread if it does not stop in time.
        if (_thread?.Join(1000) != false) { _pdh?.Dispose(); _zonesPdh?.Dispose(); _meterPdh?.Dispose(); _power?.Dispose(); _processes.Dispose(); _network.Dispose(); _gpuTemperatures?.Dispose(); _wake.Dispose(); }
    }
}

// Per logical processor: usage, and the current frequency against the highest seen so far. Usage is the share of time
// the processor was busy, which Task Manager shows on Windows 11 since 24H2; the processor utility it showed before
// scales that by the frequency and reads well above it while the cores boost. Without performance counters the same
// busy time comes from the kernel.
internal sealed class CpuProvider
{
    private const string BusyPath = @"\Processor Information(*)\% Processor Time";
    private const string PerformancePath = @"\Processor Information(*)\% Processor Performance";
    private const string BasePath = @"\Processor Information(*)\Processor Frequency";
    private readonly PdhQuery? _pdh;
    private readonly bool _busy, _frequency;
    // Whether the busy-time counter gave readings last time; until it does, or once it stops, the kernel's times stand in.
    private bool _busyReads;
    private readonly Dictionary<(int Group, int Number), (int Group, CoreTier Tier)> _kinds = CpuTopology.Read();
    private readonly Dictionary<int, double> _highest = [];
    private (long Idle, long Kernel, long User)[] _previousTimes = [];

    public CpuProvider(PdhQuery? pdh)
    {
        _pdh = pdh;
        _busy = pdh?.Add(BusyPath) == true;
        _frequency = pdh?.Add(PerformancePath) == true & pdh?.Add(BasePath) == true;
    }

    public Reading<CpuSample> Sample(bool pdhCollected)
    {
        // The kernel's times are only the fallback; while the busy-time counter gives readings they are not read at all.
        var times = _busyReads ? null : KernelTimes();
        var timeUsage = new Dictionary<int, double>();
        if (times is not null)
        {
            if (_previousTimes.Length == times.Length)
                for (int i = 0; i < times.Length; i++) if (MonitorMetrics.Usage(_previousTimes[i], times[i]) is { } usage) timeUsage[i] = usage;
            _previousTimes = times;
        }
        var busy = pdhCollected && _busy ? Processors(_pdh!.Read(BusyPath)) : [];
        _busyReads = busy.Count > 0;
        IReadOnlyList<(string, double)> performanceValues = pdhCollected && _frequency ? _pdh!.Read(PerformancePath) : [];
        IReadOnlyList<(string, double)> nominalValues = pdhCollected && _frequency ? _pdh!.Read(BasePath) : [];
        var (performance, nominal) = (Processors(performanceValues), Processors(nominalValues));

        // The processors in their logical order. The kernel's times cover the first processor group only, so the counters
        // decide the list when they are there.
        var processors = busy.Count > 0 ? busy.Keys.OrderBy(key => key).ToList()
            : Enumerable.Range(0, times?.Length ?? 0).Select(index => (Group: 0, Number: index)).ToList();
        if (processors.Count == 0 || busy.Count == 0 && timeUsage.Count == 0) return Reading<CpuSample>.Unavailable(Why.Waiting);
        var counters = new Dictionary<int, int>();
        var cores = new List<CoreSample>(processors.Count);
        for (int logical = 0; logical < processors.Count; logical++)
        {
            var key = processors[logical];
            var (group, tier) = _kinds.TryGetValue(key, out var kind) ? kind : (0, CoreTier.Unknown);
            double usage = busy.TryGetValue(key, out var percent) ? Math.Clamp(percent / 100, 0, 1)
                : timeUsage.TryGetValue(key.Number, out var kernelBusy) && key.Group == 0 ? kernelBusy : 0;
            Reading<double> frequency = Reading<double>.Unavailable(_frequency ? Why.NoFrequency : Why.NoCounters);
            double? top = null;
            if (performance.TryGetValue(key, out var share) && nominal.TryGetValue(key, out var baseMhz) && baseMhz > 0)
            {
                double mhz = baseMhz * share / 100;
                frequency = mhz;
                // Turbo has no reported ceiling; the tick is drawn against the highest frequency this kind of core has reached.
                _highest[group] = Math.Max(_highest.GetValueOrDefault(group, baseMhz), mhz);
                top = _highest[group];
            }
            int number = counters[group] = counters.GetValueOrDefault(group) + 1;
            cores.Add(new CoreSample(logical, group, tier, number, usage, frequency, top));
        }
        cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Id.CompareTo(b.Id));
        var overall = Total(performanceValues) is { } p && Total(nominalValues) is { } n && n > 0 ? Reading<double>.Of(n * p / 100) : Reading<double>.Unavailable(_frequency ? Why.NoFrequency : Why.NoCounters);
        return new CpuSample(cores, overall);
    }

    // "0,3" is processor 3 of group 0; "_Total" and "0,_Total" are sums.
    private static Dictionary<(int Group, int Number), double> Processors(IReadOnlyList<(string Instance, double Value)> values)
    {
        var result = new Dictionary<(int, int), double>();
        foreach (var (instance, value) in values)
        {
            int comma = instance.IndexOf(',');
            if (comma > 0 && int.TryParse(instance.AsSpan(0, comma), out int group) && int.TryParse(instance.AsSpan(comma + 1), out int number))
                result[(group, number)] = value;
        }
        return result;
    }
    private static double? Total(IReadOnlyList<(string Instance, double Value)> values)
        => values.Where(value => value.Instance == "_Total").Select(value => (double?)value.Value).FirstOrDefault();

    // Idle, kernel and user time of each processor of the current group.
    private static (long, long, long)[]? KernelTimes()
    {
        int count = Math.Min(Environment.ProcessorCount, 64);
        const int Entry = 48;
        var buffer = Marshal.AllocHGlobal(count * Entry);
        try
        {
            if (NtQuerySystemInformation(8, buffer, count * Entry, out int length) != 0) return null;
            var times = new (long, long, long)[Math.Min(count, length / Entry)];
            for (int i = 0; i < times.Length; i++)
                times[i] = (Marshal.ReadInt64(buffer, i * Entry), Marshal.ReadInt64(buffer, i * Entry + 8), Marshal.ReadInt64(buffer, i * Entry + 16));
            return times;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);
}

// The kind of each logical processor from its efficiency class: a higher class is a faster core. Group 0 holds the
// fastest kind, as on the page; with one class every core is Unknown.
internal static class CpuTopology
{
    public static Dictionary<(int Group, int Number), (int Group, CoreTier Tier)> Read()
    {
        var classes = new Dictionary<(int, int), int>();
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
            if (length == 0) return [];
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return [];
                for (int offset = 0; offset + 20 <= length;)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    // SYSTEM_CPU_SET_INFORMATION: Size, Type, then CpuSet { Id, Group (WORD), LogicalProcessorIndex, CoreIndex,
                    // LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass, ... }.
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                        classes[(Marshal.ReadInt16(buffer, offset + 12), Marshal.ReadByte(buffer, offset + 14))] = Marshal.ReadByte(buffer, offset + 18);
                    offset += size;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        // Without the topology every core is drawn as one kind; it is never worth failing the page for.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); return []; }
        var ranks = classes.Values.Distinct().OrderByDescending(value => value).ToList();
        return classes.ToDictionary(pair => pair.Key, pair =>
        {
            int group = ranks.IndexOf(pair.Value);
            var tier = ranks.Count < 2 ? CoreTier.Unknown : group == 0 ? CoreTier.Performance : group == ranks.Count - 1 ? CoreTier.Efficiency : CoreTier.Unknown;
            return (group, tier);
        });
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
}

internal static class MemoryProvider
{
    public static Reading<MemorySample> Sample()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return Reading<MemorySample>.Unavailable(Why.NoMemory);
        // The file cache (standby and modified pages) is free for the taking, so Task Manager counts it as available, not in use.
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        ulong cached = GetPerformanceInfo(ref performance, performance.Size) ? (ulong)performance.SystemCache * (ulong)performance.PageSize : 0;
        // TotalPageFile is the commit limit: physical memory plus the page files.
        return new MemorySample(status.TotalPhys, status.TotalPhys - Math.Min(status.TotalPhys, status.AvailPhys),
            status.TotalPageFile - Math.Min(status.TotalPageFile, status.AvailPageFile), status.TotalPageFile, cached);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);
}

// Download and upload over the physical adapters. Virtual adapters (Hyper-V, VPN, loopback) and filter drivers are left out:
// their traffic is already counted on the adapter that carries it. Each adapter is followed on its own, so one that comes or
// goes changes only its own share.
internal sealed class NetworkProvider : IDisposable
{
    // Listing every interface costs about 10 ms with the many filter and virtual interfaces Windows keeps, so the list of
    // physical adapters is taken now and then and each tick reads only those.
    private const int Relist = 30;
    private Dictionary<ulong, (ulong Received, ulong Sent)> _previous = [];
    private List<ulong> _adapters = [];
    private int _sinceList = Relist;
    private double? _time;
    private readonly IntPtr _row = Marshal.AllocHGlobal(RowSize);

    public Reading<NetworkSample> Sample(double now)
    {
        var current = _sinceList++ >= Relist ? List() : Read();
        if (current is null) return Reading<NetworkSample>.Unavailable(Why.NoNetwork);
        // No physical adapter is not the same as no traffic.
        if (current.Count == 0) { _previous = []; _time = null; return Reading<NetworkSample>.Unavailable(Why.NoAdapter); }
        var previous = _previous; var then = _time;
        _previous = current; _time = now;
        if (then is not { } start || now - start <= 0) return Reading<NetworkSample>.Unavailable(Why.Waiting);
        double seconds = now - start, down = 0, up = 0;
        foreach (var (luid, (received, sent)) in current)
        {
            if (!previous.TryGetValue(luid, out var before)) continue;
            down += MonitorMetrics.Rate(before.Received, received, seconds) ?? 0;
            up += MonitorMetrics.Rate(before.Sent, sent, seconds) ?? 0;
        }
        return new NetworkSample(down, up);
    }

    // Every interface, keeping the physical adapters and their counters.
    private Dictionary<ulong, (ulong, ulong)>? List()
    {
        if (GetIfTable2(out var table) != 0) return null;
        var current = new Dictionary<ulong, (ulong, ulong)>();
        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                var row = table + 8 + i * RowSize;
                if (!Physical(row)) continue;
                current[(ulong)Marshal.ReadInt64(row, 0)] = Counters(row);
            }
        }
        finally { FreeMibTable(table); }
        _adapters = [.. current.Keys]; _sinceList = 0;
        return current;
    }

    // The counters of the adapters found by the last listing. An adapter that went away or changed lists them again.
    private Dictionary<ulong, (ulong, ulong)>? Read()
    {
        var current = new Dictionary<ulong, (ulong, ulong)>(_adapters.Count);
        foreach (var luid in _adapters)
        {
            // GetIfEntry2 finds the row by its LUID when the index is zero.
            Marshal.WriteInt64(_row, 0, (long)luid); Marshal.WriteInt32(_row, 8, 0);
            if (GetIfEntry2(_row) != 0 || !Physical(_row)) return List();
            current[luid] = Counters(_row);
        }
        return current;
    }

    // HardwareInterface is bit 0 of the flags, FilterInterface bit 1; type 24 is the software loopback.
    private static bool Physical(IntPtr row)
    {
        byte flags = Marshal.ReadByte(row, 1152);
        return (flags & 1) != 0 && (flags & 2) == 0 && Marshal.ReadInt32(row, 1128) != 24;
    }
    private static (ulong, ulong) Counters(IntPtr row) => ((ulong)Marshal.ReadInt64(row, 1208), (ulong)Marshal.ReadInt64(row, 1280));

    public void Dispose() => Marshal.FreeHGlobal(_row);

    // MIB_IF_ROW2 on 64-bit Windows: InterfaceLuid at 0, Type at 1128, the status flags at 1152, InOctets at 1208 and
    // OutOctets at 1280. The table's rows start 8 bytes in, after NumEntries and its padding.
    private const int RowSize = 1352;
    [DllImport("iphlpapi.dll")] private static extern int GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern int GetIfEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);
}

// Every process from one kernel call, which covers other users' processes without opening them. Processes of one
// executable fold into one entry, the way ftop folds an app's helpers into the app. Names and icons are looked up only
// for the entries that can be shown.
internal sealed class ProcessProvider : IDisposable
{
    // Each list carries a few more rows than the page shows at most, so a row leaving at the bottom has one to replace it.
    private const int Kept = MonitorLayout.MaximumRows * MonitorLayout.MaximumColumns + 2;
    private const uint InfoLengthMismatch = 0xC0000004;
    private IntPtr _buffer;
    private int _capacity;
    private Dictionary<int, (long Created, long Time)> _previous = [];
    private double? _time;
    private readonly SteadyRanking _cpuRanking = SteadyRanking.Cpu(), _memoryRanking = SteadyRanking.Memory();
    // By executable name: its path and the name the page shows, once found.
    private readonly Dictionary<string, (string? Path, string Name)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _unreadable = [];
    public volatile bool Hold;

    public Reading<ProcessList> Sample(double now)
    {
        if (!Read()) return Reading<ProcessList>.Unavailable(Why.NoProcesses);
        var current = new Dictionary<int, (long, long)>();
        var samples = new List<ProcessSample>(512);
        var then = _time;
        double seconds = then is { } start ? now - start : 0;
        for (int offset = 0; ;)
        {
            var entry = _buffer + offset;
            int pid = (int)Marshal.ReadIntPtr(entry, 80);
            if (pid != 0)
            {
                long created = Marshal.ReadInt64(entry, 32), time = Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48);
                ulong memory = (ulong)Math.Max(0, Marshal.ReadInt64(entry, 8));
                int length = Marshal.ReadInt16(entry, 56) & 0xFFFF;
                var image = length > 0 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 64), length / 2) : pid == 4 ? "System" : "";
                current[pid] = (created, time);
                double cpu = _previous.TryGetValue(pid, out var before) && before.Created == created ? MonitorMetrics.CpuPercent(before.Time, time, seconds) ?? 0 : 0;
                if (image.Length > 0) samples.Add(new ProcessSample(pid, image, cpu, memory, image.ToLowerInvariant()));
            }
            int next = Marshal.ReadInt32(entry);
            if (next == 0) break;
            offset += next;
        }
        _previous = current; _time = now;
        _unreadable.IntersectWith(current.Keys);
        if (then is null) return Reading<ProcessList>.Unavailable(Why.Waiting);
        double elapsed = seconds;
        var top = _cpuRanking.Arrange(ProcessSample.Folded(samples, ProcessSort.Cpu), sample => sample.CpuPercent, elapsed, Hold);
        var byMemory = _memoryRanking.Arrange(ProcessSample.Folded(samples, ProcessSort.Memory), sample => sample.MemoryBytes, elapsed, Hold);
        return new ProcessList(Named(top.Take(Kept)), Named(byMemory.Take(Kept)));
    }

    private List<ProcessSample> Named(IEnumerable<ProcessSample> entries) => entries.Select(entry =>
    {
        var (path, name) = App(entry);
        // The icon is read here, off the UI thread: the shell can take long for a program on a slow or network drive.
        ProcessIcons.Prepare(path);
        return entry with { Name = name, Path = path };
    }).ToList();

    // The executable's description (as Task Manager names apps) or its file name without ".exe".
    private (string? Path, string Name) App(ProcessSample entry)
    {
        string image = entry.AppKey ?? entry.Name;
        if (_apps.TryGetValue(image, out var known) && known.Path is not null) return known;
        string plain = entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry.Name[..^4] : entry.Name;
        string? path = _unreadable.Contains(entry.Pid) ? null : ImagePath(entry.Pid);
        if (path is null) { _unreadable.Add(entry.Pid); return _apps[image] = known.Name is { } kept ? (null, kept) : (null, plain); }
        string name = plain;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 40) name = description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return _apps[image] = (path, name);
    }

    private static string? ImagePath(int pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var text = new char[1024]; int size = text.Length;
            return QueryFullProcessImageNameW(process, 0, text, ref size) ? new string(text, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    private bool Read()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (_buffer == IntPtr.Zero) { _capacity = 1 << 20; _buffer = Marshal.AllocHGlobal(_capacity); }
            uint status = (uint)NtQuerySystemInformation(5, _buffer, _capacity, out int needed);
            if (status == 0) return true;
            if (status != InfoLengthMismatch) return false;
            // Processes can start between the calls; leave room for more.
            Marshal.FreeHGlobal(_buffer);
            _capacity = Math.Max(_capacity * 2, needed + (256 << 10)); _buffer = Marshal.AllocHGlobal(_capacity);
        }
        return false;
    }

    public void Dispose() { if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; } }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
