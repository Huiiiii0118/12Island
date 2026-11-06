using System.Diagnostics;
using System.Runtime.InteropServices;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Each adapter's usage asked of its own driver (GpuDriverReadings says why), the way PresentMon and LibreHardwareMonitor read it:
// NVIDIA through NVAPI (nvapi64.dll), AMD through ADL (atiadlxx.dll), Intel through its control library (ControlLib.dll), each
// from the system folder where the driver puts it and loaded only once an adapter of that make is working. AMD's and Intel's
// discrete adapters also give their board power here; NVIDIA's comes from nvidia-smi (PowerSensors). An adapter is asked only
// while Windows sees it working (GpuDriverGate), and every call is guarded (SensorGuard) by the adapter's name: a driver that
// never answers is not asked again, and the usage stays the busiest engine. Belongs to the sampling thread. stuck: the sampler
// gives up its thread for another.
internal sealed class GpuDrivers(Action stuck)
{
    private readonly GpuDriverGate _gate = new();
    private readonly NvidiaDriver _nvidia = new();
    private readonly AmdDriver _amd = new();
    private readonly IntelDriver _intel = new();

    // temperatures: whether anything shows a temperature; only NVIDIA's costs a call of its own, made only then.
    public List<GpuSample> Read(IReadOnlyList<GpuSample> gpus, double now, bool temperatures)
    {
        _gate.Keep(gpus.Select(gpu => gpu.Luid));
        _nvidia.Temperatures = temperatures;
        var read = new List<GpuSample>(gpus.Count);
        foreach (var gpu in gpus)
        {
            IGpuDriver? driver = gpu.VendorId switch
            {
                GpuDriverReadings.Nvidia => _nvidia, GpuDriverReadings.Amd => _amd, GpuDriverReadings.Intel => _intel, _ => null
            };
            string name = gpu.Name ?? "GPU";
            var device = new GuardedDevice(GuardedReading.GpuDriver, name, name);
            // A temperature that is being shown is worth one light driver query even while the adapter has been idle:
            // the shown adapter may be an integrated or virtual one with no sensor of its own. A sleeping adapter
            // (no engine readings at all) is still left alone.
            bool awake = _gate.Awake(gpu, now) || temperatures && !gpu.NoReadings;
            if (driver is null || !awake || SensorGuard.Stopped(device.Reading, device.Device)) { read.Add(gpu); continue; }
            var (percent, watts, celsius) = SensorGuard.Ask(device, () => driver.Read(gpu, now), stuck);
            read.Add(GpuDriverReadings.Apply(gpu, percent, watts, celsius));
        }
        return read;
    }
}

internal interface IGpuDriver
{
    // The adapter's usage in percent, board power in watts and temperature in °C, each null where the driver gives none.
    (double? Percent, double? Watts, double? Celsius) Read(GpuSample gpu, double now);
}

// Loads a driver's library once; a library that is missing or does not start is not tried again in this run. The adapters are
// listed again when one is not found or a reading fails (a driver update or reset changes them), after a pause that grows from
// FirstPause to LongestPause while the adapter stays missing (its driver may simply not list it) and starts over once it is found.
internal abstract class DriverLibrary : IGpuDriver
{
    private const double FirstPause = 5, LongestPause = 600;
    private bool _tried, _ready;
    private double _listedAt = double.NegativeInfinity, _pause = FirstPause;
    protected IntPtr Library { get; private set; }

    public (double? Percent, double? Watts, double? Celsius) Read(GpuSample gpu, double now)
    {
        try
        {
            if (!_tried)
            {
                _tried = true;
                _ready = NativeLibrary.TryLoad(FileName, typeof(DriverLibrary).Assembly, DllImportSearchPath.System32, out var library) && Start(Library = library);
            }
            if (!_ready) return default;
            if (!Listed(gpu) && now - _listedAt >= _pause)
            {
                _listedAt = now; List();
                _pause = Listed(gpu) ? FirstPause : Math.Min(LongestPause, _pause * 2);
            }
            return Listed(gpu) ? Sample(gpu, now) : default;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or SEHException)
        {
            Debug.WriteLine(ex); _ready = false; return default;
        }
    }

    // A reading that failed: the adapters are listed again at the next chance.
    protected void Forget() => Clear();

    protected abstract string FileName { get; }
    protected abstract bool Start(IntPtr library);
    protected abstract void List();
    protected abstract void Clear();
    protected abstract bool Listed(GpuSample gpu);
    protected abstract (double? Percent, double? Watts, double? Celsius) Sample(GpuSample gpu, double now);

    protected T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
    protected T? TryExport<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(Library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
}

// NVAPI: the graphics domain of NvAPI_GPU_GetDynamicPstatesInfoEx (NV_GPU_DYNAMIC_PSTATES_INFO_EX, version 1: version, flags,
// then eight utilization domains of a present bit and a percentage; the graphics domain is the first). Its functions are found
// by their ids through nvapi_QueryInterface; each adapter by its PCI bus (NvAPI_GPU_GetBusId). The temperature is the GPU
// sensor of NvAPI_GPU_GetThermalSettings (NV_GPU_THERMAL_SETTINGS, version 2: version, count, then three sensors of controller,
// lowest, highest, current and target, all sensors asked for at once with NVAPI_THERMAL_TARGET_ALL, 15; target 1 is the GPU).
internal sealed class NvidiaDriver : DriverLibrary
{
    private const uint Initialize = 0x0150E828, EnumPhysicalGpus = 0xE5AC921F, GetBusId = 0x1BE0B8E5, GetDynamicPstatesInfoEx = 0x60DED2ED,
        GetThermalSettings = 0xE3640A56;
    private const int PstatesInts = 18, PstatesVersion = PstatesInts * 4 | 1 << 16, MaxGpus = 64;
    private const int ThermalInts = 17, ThermalVersion = ThermalInts * 4 | 2 << 16, AllSensors = 15, GpuTarget = 1;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFunction([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BusIdFunction(IntPtr gpu, out uint bus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PstatesFunction(IntPtr gpu, [In, Out] int[] info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ThermalFunction(IntPtr gpu, int sensor, [In, Out] int[] settings);
    private ThermalFunction? _thermal;
    private EnumFunction? _enumerate;
    private BusIdFunction? _busId;
    private PstatesFunction? _pstates;
    private readonly Dictionary<uint, IntPtr> _gpus = [];
    private readonly int[] _info = new int[PstatesInts], _thermalSettings = new int[ThermalInts];
    public bool Temperatures;

    protected override string FileName => "nvapi64.dll";

    protected override bool Start(IntPtr library)
    {
        var query = Export<QueryInterface>("nvapi_QueryInterface");
        T? Function<T>(uint id) where T : Delegate => query(id) is var address && address != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
        if (Function<InitializeFunction>(Initialize) is not { } initialize || initialize() != 0) return false;
        _enumerate = Function<EnumFunction>(EnumPhysicalGpus); _busId = Function<BusIdFunction>(GetBusId); _pstates = Function<PstatesFunction>(GetDynamicPstatesInfoEx);
        _thermal = Function<ThermalFunction>(GetThermalSettings);
        return _enumerate is not null && _busId is not null && _pstates is not null;
    }

    protected override void List()
    {
        _gpus.Clear();
        var handles = new IntPtr[MaxGpus];
        if (_enumerate!(handles, out int count) != 0) return;
        for (int i = 0; i < Math.Min(count, MaxGpus); i++)
            if (_busId!(handles[i], out uint bus) == 0) _gpus.TryAdd(bus, handles[i]);
    }
    protected override void Clear() => _gpus.Clear();

    // By its bus; an adapter whose location Windows does not give is found only when the driver lists a single one.
    private IntPtr? Handle(GpuSample gpu) => GpuDriverReadings.Bus(gpu.Location) is { } bus
        ? _gpus.TryGetValue(bus, out var handle) ? handle : null
        : _gpus.Count == 1 ? _gpus.Values.First() : null;
    protected override bool Listed(GpuSample gpu) => Handle(gpu) is not null;

    protected override (double? Percent, double? Watts, double? Celsius) Sample(GpuSample gpu, double now)
    {
        var info = _info;
        Array.Clear(info); info[0] = PstatesVersion;
        var handle = Handle(gpu)!.Value;
        if (_pstates!(handle, info) != 0) { Forget(); return default; }
        double? celsius = null;
        var thermal = _thermalSettings;
        Array.Clear(thermal); thermal[0] = ThermalVersion;
        if (Temperatures && _thermal is not null && _thermal(handle, AllSensors, thermal) == 0)
            for (int i = 0; i < Math.Min(thermal[1], 3); i++)
                if (thermal[2 + i * 5 + 4] == GpuTarget) { celsius = thermal[2 + i * 5 + 3]; break; }
        return ((info[2] & 1) != 0 ? (uint)info[3] : null, null, celsius);
    }
}

// ADL: the driver's log (ADL2_New_QueryPMLogData_Get, ADLPMLogDataOutput: a size, then 256 sensors of a supported flag and a value)
// for the graphics activity (PMLOG_INFO_ACTIVITY_GFX, 19) and the board power (PMLOG_BOARD_POWER, 73, else PMLOG_ASIC_POWER, 23, in
// watts); older adapters, without them in the log, through Overdrive 5's activity (ADLPMActivity, the percentage its fifth int)
// and Overdrive 6's power (in 1/256 W). The temperature is the log's edge sensor (PMLOG_TEMPERATURE_EDGE, 8), else its graphics one
// (PMLOG_TEMPERATURE_GFX, 28). Each adapter by its PCI location (AdapterInfo, 1572 bytes on Windows: size, index, UDID[256],
// bus, device, function, vendor, ...); the driver lists one entry per output, the first of a location is taken.
internal sealed class AmdDriver : DriverLibrary
{
    private const int InfoSize = 1572, IndexAt = 4, BusAt = 264, DeviceAt = 268, FunctionAt = 272, VendorAt = 276;
    private const int Sensors = 256, ActivityGfx = 19, AsicPower = 23, BoardPower = 73, EdgeTemperature = 8, GfxTemperature = 28;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr Allocate(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateFunction(Allocate allocate, int connected, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFunction(IntPtr context, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InfoFunction(IntPtr context, IntPtr info, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LogFunction(IntPtr context, int adapter, [In, Out] int[] output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ActivityFunction(IntPtr context, int adapter, [In, Out] int[] activity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerFunction(IntPtr context, int adapter, int type, out int power);
    // The driver keeps the allocator it was given: it must outlive every context.
    private static readonly Allocate Allocator = size => Marshal.AllocHGlobal(size);
    private IntPtr _context;
    private CountFunction? _count;
    private InfoFunction? _info;
    private LogFunction? _log;
    private ActivityFunction? _activity;
    private PowerFunction? _power;
    private readonly Dictionary<string, int> _adapters = [];

    protected override string FileName => "atiadlxx.dll";

    protected override bool Start(IntPtr library)
    {
        if (Export<CreateFunction>("ADL2_Main_Control_Create")(Allocator, 1, out _context) != 0 || _context == IntPtr.Zero) return false;
        _count = Export<CountFunction>("ADL2_Adapter_NumberOfAdapters_Get"); _info = Export<InfoFunction>("ADL2_Adapter_AdapterInfo_Get");
        _log = TryExport<LogFunction>("ADL2_New_QueryPMLogData_Get"); _activity = TryExport<ActivityFunction>("ADL2_Overdrive5_CurrentActivity_Get");
        _power = TryExport<PowerFunction>("ADL2_Overdrive6_CurrentPower_Get");
        return true;
    }

    protected override void List()
    {
        _adapters.Clear();
        if (_count!(_context, out int count) != 0 || count <= 0 || count > 256) return;
        var buffer = Marshal.AllocHGlobal(count * InfoSize);
        try
        {
            for (int i = 0; i < count * InfoSize; i += 4) Marshal.WriteInt32(buffer, i, 0);
            if (_info!(_context, buffer, count * InfoSize) != 0) return;
            for (int i = 0; i < count; i++)
            {
                var entry = buffer + i * InfoSize;
                // The vendor is written in hexadecimal digits read as decimal by some drivers (1002), as the PCI id by others.
                if (Marshal.ReadInt32(entry, VendorAt) is not (0x1002 or 1002)) continue;
                if (GpuReadings.Location((uint)Marshal.ReadInt32(entry, BusAt), (uint)Marshal.ReadInt32(entry, DeviceAt), (uint)Marshal.ReadInt32(entry, FunctionAt)) is { } location)
                    _adapters.TryAdd(location, Marshal.ReadInt32(entry, IndexAt));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    protected override void Clear() => _adapters.Clear();

    private int? Index(GpuSample gpu) => gpu.Location is { } location
        ? _adapters.TryGetValue(location, out int index) ? index : null
        : _adapters.Count == 1 ? _adapters.Values.First() : null;
    protected override bool Listed(GpuSample gpu) => Index(gpu) is not null;

    // Kept between readings, as NVIDIA's are.
    private readonly int[] _output = new int[1 + Sensors * 2], _activityInfo = new int[10];

    protected override (double? Percent, double? Watts, double? Celsius) Sample(GpuSample gpu, double now)
    {
        int adapter = Index(gpu)!.Value;
        double? percent = null, watts = null, celsius = null;
        if (_log is not null)
        {
            var output = _output;
            Array.Clear(output); output[0] = output.Length * 4;
            if (_log(_context, adapter, output) == 0)
            {
                int? Sensor(int sensor) => output[1 + sensor * 2] != 0 ? output[2 + sensor * 2] : null;
                percent = Sensor(ActivityGfx);
                watts = Sensor(BoardPower) is > 0 and var board ? board : Sensor(AsicPower);
                celsius = Sensor(EdgeTemperature) is > 0 and var edge ? edge : Sensor(GfxTemperature);
            }
        }
        if (percent is null && _activity is not null)
        {
            var activity = _activityInfo;
            Array.Clear(activity); activity[0] = activity.Length * 4;
            if (_activity(_context, adapter, activity) == 0) percent = activity[4];
        }
        if (watts is null && _power is not null && _power(_context, adapter, 0, out int power) == 0) watts = power / 256.0;
        if (percent is null && watts is null) Forget();
        return (percent, watts, celsius);
    }
}

// Intel's control library: ctlPowerTelemetryGet (ctl_power_telemetry_t, version 1, 1024 bytes), whose counters only grow; the usage
// is the global activity counter (seconds any engine was busy) over the time stamp, the power the card's energy counter, else
// the chip's (joules), over the same time; the temperature is the chip's hottest sensor (gpuCurrentTemperature, °C). Each counter
// is an item of 24 bytes: a supported flag, its units, its type (9 is a double) and the value at 16. Each adapter by its LUID,
// which ctlGetDeviceProperties writes where pDeviceID points.
internal sealed class IntelDriver : DriverLibrary
{
    private const int PropertiesSize = 320, TelemetrySize = 1024, DoubleType = 9;
    private const int TimeAt = 8, ChipEnergyAt = 32, TemperatureAt = 104, ActivityAt = 128, CardEnergyAt = 384;
    [StructLayout(LayoutKind.Sequential)]
    private struct InitArgs { public uint Size; public byte Version; public uint AppVersion, Flags, SupportedVersion; public Guid ApplicationUid; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFunction(ref InitArgs args, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumerateFunction(IntPtr api, ref uint count, [Out] IntPtr[]? devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PropertiesFunction(IntPtr device, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TelemetryFunction(IntPtr device, [In, Out] byte[] telemetry);
    private IntPtr _api;
    private EnumerateFunction? _enumerate;
    private PropertiesFunction? _properties;
    private TelemetryFunction? _telemetry;
    private readonly Dictionary<ulong, IntPtr> _devices = [];
    private readonly Dictionary<ulong, (double At, double Busy, double Card, double Chip)> _last = [];
    private readonly byte[] _telemetryData = new byte[TelemetrySize];

    protected override string FileName => "ControlLib.dll";

    protected override bool Start(IntPtr library)
    {
        // Version 1.1 of the interface, with Level Zero, which telemetry needs (CTL_INIT_FLAG_USE_LEVEL_ZERO).
        var args = new InitArgs { Size = (uint)Marshal.SizeOf<InitArgs>(), AppVersion = 1u << 16 | 1, Flags = 1 };
        if (Export<InitFunction>("ctlInit")(ref args, out _api) != 0 || _api == IntPtr.Zero) return false;
        _enumerate = Export<EnumerateFunction>("ctlEnumerateDevices"); _properties = Export<PropertiesFunction>("ctlGetDeviceProperties");
        _telemetry = Export<TelemetryFunction>("ctlPowerTelemetryGet");
        return true;
    }

    protected override void List()
    {
        _devices.Clear();
        uint count = 0;
        if (_enumerate!(_api, ref count, null) != 0 || count == 0 || count > 64) return;
        var devices = new IntPtr[count];
        if (_enumerate(_api, ref count, devices) != 0) return;
        IntPtr properties = Marshal.AllocHGlobal(PropertiesSize), luid = Marshal.AllocHGlobal(8);
        try
        {
            foreach (var device in devices.Take((int)count))
            {
                for (int i = 0; i < PropertiesSize; i += 4) Marshal.WriteInt32(properties, i, 0);
                Marshal.WriteInt64(luid, 0);
                // ctl_device_adapter_properties_t: Size, Version (2), pDeviceID at 8, device_id_size at 16, device_type at 20 (1 is graphics).
                Marshal.WriteInt32(properties, 0, PropertiesSize); Marshal.WriteByte(properties, 4, 2);
                Marshal.WriteIntPtr(properties, 8, luid); Marshal.WriteInt32(properties, 16, 8);
                if (_properties!(device, properties) != 0 || Marshal.ReadInt32(properties, 20) != 1) continue;
                ulong id = (ulong)(uint)Marshal.ReadInt32(luid, 4) << 32 | (uint)Marshal.ReadInt32(luid, 0);
                if (id != 0) _devices.TryAdd(id, device);
            }
        }
        finally { Marshal.FreeHGlobal(properties); Marshal.FreeHGlobal(luid); }
    }
    protected override void Clear() { _devices.Clear(); _last.Clear(); }
    protected override bool Listed(GpuSample gpu) => _devices.ContainsKey(gpu.Luid);

    protected override (double? Percent, double? Watts, double? Celsius) Sample(GpuSample gpu, double now)
    {
        var telemetry = _telemetryData;
        Array.Clear(telemetry);
        BitConverter.TryWriteBytes(telemetry.AsSpan(0), TelemetrySize);
        telemetry[4] = 1;
        if (_telemetry!(_devices[gpu.Luid], telemetry) != 0) { Forget(); return default; }
        double Item(int at) => telemetry[at] != 0 && BitConverter.ToInt32(telemetry, at + 8) == DoubleType ? BitConverter.ToDouble(telemetry, at + 16) : double.NaN;
        var current = (At: Item(TimeAt), Busy: Item(ActivityAt), Card: Item(CardEnergyAt), Chip: Item(ChipEnergyAt));
        bool had = _last.TryGetValue(gpu.Luid, out var last);
        _last[gpu.Luid] = current;
        double? celsius = Item(TemperatureAt) is var heat && !double.IsNaN(heat) ? heat : null;
        if (!had || double.IsNaN(current.At)) return (null, null, celsius);
        double? Rate(double before, double after) => double.IsNaN(before) || double.IsNaN(after) ? null : GpuDriverReadings.Rate(before, last.At, after, current.At);
        return (Rate(last.Busy, current.Busy) * 100, Rate(last.Card, current.Card) ?? Rate(last.Chip, current.Chip), celsius);
    }
}
