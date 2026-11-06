using System.Diagnostics;
using System.Runtime.InteropServices;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The graphics engines as Task Manager reads them, for every adapter: per adapter and kind of engine, every process's share
// summed; an adapter's usage is its busiest kind. Each adapter has its engines, its dedicated and shared memory, and its name,
// dedicated memory size and kind from DXGI and DXCore (GpuReadings.Samples). Which one the page shows is decided by the sampler
// (GpuChoice).
internal sealed class GpuProvider
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string MemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string SharedPath = @"\GPU Adapter Memory(*)\Shared Usage";
    private readonly PdhQuery? _pdh;
    private bool _wanted, _engines, _memory, _shared;
    private readonly GpuCounters _counters = new();
    // By LUID. Read once, and again when the counters name an adapter not described: the LUIDs change when a driver restarts.
    // A read that fails or misses an adapter never shuts it out for long (GpuDescriptions): it is asked about again after a pause
    // that grows from seconds to minutes, so an adapter that failed once returns, and a device DXGI never lists (an NPU) costs a
    // read now and then only.
    private readonly GpuDescriptions _descriptions;

    public GpuProvider(PdhQuery? pdh)
    {
        _pdh = pdh;
        _descriptions = new GpuDescriptions(GpuAdapters.Read, Now());
    }

    // The counters are in the query only while something shows an adapter: the engine counter has an instance per process and engine,
    // often thousands, and is the costliest part of a collection. Called on every tick, before the collection.
    public void Want(bool wanted)
    {
        if (wanted == _wanted || _pdh is null) return;
        _wanted = wanted;
        if (wanted) { _engines = _pdh.Add(EnginePath); _memory = _pdh.Add(MemoryPath); _shared = _pdh.Add(SharedPath); }
        else { _pdh.Remove(EnginePath); _pdh.Remove(MemoryPath); _pdh.Remove(SharedPath); }
    }
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Every adapter with counters, or why there is none.
    public Reading<IReadOnlyList<GpuSample>> Sample(bool collected)
    {
        if (!_wanted && _pdh is not null) return Reading<IReadOnlyList<GpuSample>>.Unavailable(Why.Waiting);
        if (!_engines) return Reading<IReadOnlyList<GpuSample>>.Unavailable(_pdh?.IsOpen == true ? Why.NoGpu : Why.NoCounters);
        if (!collected) return Reading<IReadOnlyList<GpuSample>>.Unavailable(Why.NoCounters);
        var engines = _pdh!.Read(EnginePath);
        if (engines.Count == 0) return Reading<IReadOnlyList<GpuSample>>.Unavailable(Why.Waiting);
        var totals = _counters.Sum(engines, _memory ? _pdh.Read(MemoryPath) : [], _shared ? _pdh.Read(SharedPath) : []);
        var adapters = _descriptions.For(totals.Keys, Now());
        var samples = GpuReadings.Samples(totals, adapters, _descriptions.Described, Why.NoGpuMemory);
        return samples.Count > 0 ? samples : Reading<IReadOnlyList<GpuSample>>.Unavailable(Why.NoGpu);
    }
}

// The graphics adapters from DXGI by LUID: name, ids, dedicated memory, whether each is integrated (DXCore, Windows 10 2004
// and later; before it the memory carve-out tells, GpuReadings.KindOf), and where it sits on the bus (the graphics kernel's adapter
// address). Described is false when DXGI listed nothing (a machine without it, or a failure on the way); the page then shows "GPU"
// and no memory size. Complete is false when an adapter DXGI listed could not describe itself, so the caller keeps what it knew.
internal static class GpuAdapters
{
    public static (IReadOnlyDictionary<ulong, GpuAdapter> Adapters, bool Described, bool Complete) Read()
    {
        var adapters = new List<GpuAdapter>();
        bool complete = true;
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        IntPtr factory = IntPtr.Zero, description = Marshal.AllocHGlobal(512);
        try
        {
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return (new Dictionary<ulong, GpuAdapter>(), false, false);
            // IDXGIFactory1::EnumAdapters1 is the 13th method (index 12); IDXGIAdapter1::GetDesc1 the 11th (index 10).
            var enumerate = Method<EnumAdapters1>(factory, 12);
            for (uint index = 0; index < 16 && enumerate(factory, index, out var adapter) >= 0 && adapter != IntPtr.Zero; index++)
            {
                try
                {
                    if (Method<GetDesc1>(adapter, 10)(adapter, description) < 0) { complete = false; continue; }
                    // DXGI_ADAPTER_DESC1: Description[128] WCHAR, four UINT ids, three SIZE_T memory sizes, the LUID, Flags.
                    string name = Marshal.PtrToStringUni(description, 128).TrimEnd('\0').Trim();
                    uint vendor = (uint)Marshal.ReadInt32(description, 256), device = (uint)Marshal.ReadInt32(description, 260), subsystem = (uint)Marshal.ReadInt32(description, 264);
                    ulong dedicated = (ulong)Marshal.ReadInt64(description, 272);
                    ulong luid = (ulong)(uint)Marshal.ReadInt32(description, 300) << 32 | (uint)Marshal.ReadInt32(description, 296);
                    // Remote (1) and software (2) adapters: the Basic Render Driver has engine counters of its own.
                    uint flags = (uint)Marshal.ReadInt32(description, 304);
                    var (integrated, hardware) = DxCore(luid);
                    adapters.Add(new GpuAdapter(luid, name, vendor, device, subsystem, dedicated, GpuReadings.KindOf(integrated, dedicated), (flags & 3) != 0 || hardware == false)
                    {
                        Location = Address(luid)
                    });
                }
                finally { Method<Release>(adapter, 2)(adapter); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { Debug.WriteLine(ex); complete = false; }
        finally
        {
            if (factory != IntPtr.Zero) Method<Release>(factory, 2)(factory);
            Marshal.FreeHGlobal(description);
        }
        return (GpuReadings.Keyed(adapters).ToDictionary(adapter => adapter.Luid), adapters.Count > 0, complete);
    }

    // Where the adapter sits on the PCI bus (D3DKMT_ADAPTERADDRESS: bus, device and function, KMTQAITYPE_ADAPTERADDRESS 6), which
    // stays the same across restarts and tells two cards of one model apart; null where the graphics kernel does not give it.
    private static string? Address(ulong luid)
    {
        var open = new OpenAdapterFromLuid { Low = (uint)luid, High = (int)(luid >> 32) };
        var data = Marshal.AllocHGlobal(12);
        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;
            try
            {
                for (int i = 0; i < 12; i += 4) Marshal.WriteInt32(data, i, -1);
                var query = new QueryAdapterInfo { Adapter = open.Adapter, Type = 6, Data = data, Size = 12 };
                return D3DKMTQueryAdapterInfo(ref query) == 0
                    ? GpuReadings.Location((uint)Marshal.ReadInt32(data), (uint)Marshal.ReadInt32(data, 4), (uint)Marshal.ReadInt32(data, 8)) : null;
            }
            finally { var close = new CloseAdapter { Adapter = open.Adapter }; D3DKMTCloseAdapter(ref close); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { Debug.WriteLine(ex); return null; }
        finally { Marshal.FreeHGlobal(data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapterFromLuid { public uint Low; public int High; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    // Whether DXCore takes the adapter as integrated and as hardware; null where it does not say (no DXCore before Windows 10 2004).
    private static (bool? Integrated, bool? Hardware) DxCore(ulong luid)
    {
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            var factoryId = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
            if (DXCoreCreateAdapterFactory(ref factoryId, out factory) < 0 || factory == IntPtr.Zero) return (null, null);
            // IDXCoreAdapterFactory::GetAdapterByLuid is its 5th method (index 4); IDXCoreAdapter::GetProperty the 7th (index 6),
            // with DXCoreAdapterProperty IsHardware 11 and IsIntegrated 12, each a one-byte bool.
            var adapterId = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
            if (Method<GetAdapterByLuid>(factory, 4)(factory, ref luid, ref adapterId, out adapter) < 0 || adapter == IntPtr.Zero) return (null, null);
            var property = Method<GetProperty>(adapter, 6);
            byte integrated = 0, hardware = 0;
            return (property(adapter, 12, 1, ref integrated) >= 0 ? integrated != 0 : null, property(adapter, 11, 1, ref hardware) >= 0 ? hardware != 0 : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { Debug.WriteLine(ex); return (null, null); }
        finally
        {
            if (adapter != IntPtr.Zero) Method<Release>(adapter, 2)(adapter);
            if (factory != IntPtr.Zero) Method<Release>(factory, 2)(factory);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1(IntPtr adapter, IntPtr description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Release(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterByLuid(IntPtr factory, ref ulong luid, ref Guid riid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetProperty(IntPtr adapter, uint property, nuint size, ref byte value);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [DllImport("dxcore.dll")] private static extern int DXCoreCreateAdapterFactory(ref Guid riid, out IntPtr factory);
}
