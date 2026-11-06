namespace TwelveIsland.Core;

// The graphics adapters of a machine with more than one, such as a laptop's integrated and discrete GPUs (issue #80). Every
// adapter is read on its own as Task Manager reads it; which one the monitor shows is the user's choice (DeckSettings.MonitorGpu),
// or by default the discrete one, so the reading does not jump between adapters as their load changes.

// Integrated adapters share the system's memory; discrete ones have their own. Unknown where the system does not say.
public enum GpuKind { Unknown, Integrated, Discrete }

// A graphics adapter as DXGI describes it. Its LUID names it in the performance counters until the next restart or driver reset;
// Key names it for good (GpuReadings.Keyed). Location is where it sits on the bus ("1:0.0", PCI bus, device and function), which
// tells two cards of one model apart and stays the same across restarts; null where the system does not give it. LegacyKey is
// the key saved before there was a location, to find a choice saved then. Software adapters (the Basic Render Driver) are listed
// so their counters are left out.
public sealed record GpuAdapter(ulong Luid, string Name, uint VendorId, uint DeviceId, uint SubSystemId, ulong DedicatedTotal, GpuKind Kind, bool Software = false)
{
    public string Key { get; init; } = "";
    public string? Location { get; init; }
    public string LegacyKey { get; init; } = "";
}

// One adapter's counters: by engine type ("3D", "Compute_0", ...) the busiest engine of that type, every process's share of it
// summed, in percent (as Task Manager, which reads each engine on its own: two copy engines at 60 % are 60 %, not 120 %); and its
// memory in use in bytes, null where the counter does not list it.
public sealed class GpuTotals
{
    public Dictionary<string, double> Engines { get; } = [];
    public double? Dedicated { get; set; }
    public double? Shared { get; set; }
    // As Task Manager: the busiest engine, in percent.
    public double Usage
    {
        get
        {
            double most = 0;
            foreach (var value in Engines.Values) most = Math.Max(most, value);
            return most;
        }
    }
}

// Sums the graphics counters per engine (adapter, physical adapter of a linked group, engine number) and keeps each type's busiest
// engine. The engine types are kept between readings, so a reading makes no string per counter: the sampler reads several hundred
// engine instances every second.
public sealed class GpuCounters
{
    private readonly HashSet<string> _types = [];
    private readonly Dictionary<(ulong Luid, int Physical, int Engine), (string Type, double Value)> _engines = [];

    public Dictionary<ulong, GpuTotals> Sum(IReadOnlyList<(string Instance, double Value)> engines,
        IReadOnlyList<(string Instance, double Value)> dedicated, IReadOnlyList<(string Instance, double Value)> shared)
    {
        var totals = new Dictionary<ulong, GpuTotals>();
        var types = _types.GetAlternateLookup<ReadOnlySpan<char>>();
        _engines.Clear();
        for (int i = 0; i < engines.Count; i++)
        {
            var (instance, value) = engines[i];
            if (GpuReadings.Luid(instance) is not { } luid) continue;
            int at = instance.IndexOf("engtype_", StringComparison.Ordinal);
            var span = at < 0 ? ReadOnlySpan<char>.Empty : instance.AsSpan(at + 8);
            if (!types.TryGetValue(span, out var type)) { type = span.ToString(); _types.Add(type); }
            var key = (luid, Number(instance, "_phys_"), Number(instance, "_eng_"));
            _engines[key] = (type, (_engines.TryGetValue(key, out var engine) ? engine.Value : 0) + value);
        }
        foreach (var ((luid, _, _), (type, value)) in _engines)
        {
            var adapter = Totals(totals, luid);
            adapter.Engines[type] = Math.Max(adapter.Engines.GetValueOrDefault(type), value);
        }
        // An adapter's memory is listed per physical adapter of a linked group; the group's memory is their sum.
        for (int i = 0; i < dedicated.Count; i++)
            if (GpuReadings.Luid(dedicated[i].Instance) is { } luid && totals.TryGetValue(luid, out var adapter)) adapter.Dedicated = (adapter.Dedicated ?? 0) + dedicated[i].Value;
        for (int i = 0; i < shared.Count; i++)
            if (GpuReadings.Luid(shared[i].Instance) is { } luid && totals.TryGetValue(luid, out var adapter)) adapter.Shared = (adapter.Shared ?? 0) + shared[i].Value;
        return totals;
    }
    private static GpuTotals Totals(Dictionary<ulong, GpuTotals> totals, ulong luid)
    {
        if (!totals.TryGetValue(luid, out var adapter)) totals[luid] = adapter = new GpuTotals();
        return adapter;
    }
    // The number after a part of an instance name ("_eng_" in "..._phys_0_eng_3_engtype_Copy" is 3); -1 without one.
    private static int Number(string instance, string part)
    {
        int at = instance.IndexOf(part, StringComparison.Ordinal);
        if (at < 0) return -1;
        var rest = instance.AsSpan(at + part.Length);
        int end = rest.IndexOf('_');
        return int.TryParse(end < 0 ? rest : rest[..end], out int number) ? number : -1;
    }
}

public static class GpuReadings
{
    // Below this much dedicated memory an adapter is taken as integrated where the system cannot say (before Windows 10 2004):
    // integrated adapters report a small carve-out (Intel 128 MB, AMD 512 MB by default), discrete ones their own memory.
    public const ulong IntegratedBelow = 1UL << 30;

    // The LUID in an engine or adapter memory instance name: "pid_1234_luid_0x00000000_0x0000C5E2_phys_0_eng_0_engtype_3D" or
    // "luid_0x00000000_0x0000C5E2_phys_0". The high part comes first.
    public static ulong? Luid(ReadOnlySpan<char> instance)
    {
        int at = instance.IndexOf("luid_0x", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = instance[(at + 7)..];
        int split = rest.IndexOf("_0x", StringComparison.Ordinal);
        if (split <= 0) return null;
        var lowText = rest[(split + 3)..];
        int end = lowText.IndexOf('_');
        if (end >= 0) lowText = lowText[..end];
        return uint.TryParse(rest[..split], System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint high)
            && uint.TryParse(lowText, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint low) ? (ulong)high << 32 | low : null;
    }
    // As the counters name it.
    public static string LuidText(ulong luid) => $"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}";

    // The system's word where it gives one (DXCore), else the memory carve-out.
    public static GpuKind KindOf(bool? integrated, ulong dedicated) => integrated switch
    {
        true => GpuKind.Integrated, false => GpuKind.Discrete, _ => dedicated < IntegratedBelow ? GpuKind.Integrated : GpuKind.Discrete
    };

    // Gives each adapter a key that outlives its LUID: its vendor, device and subsystem ids (or its name where it has no ids) and
    // where it sits on the bus, "10DE:2684:16F310DE@1:0.0", so two identical cards keep their own keys across restarts, whatever
    // order they are listed in. Without a location (the system does not give one) identical cards are told apart by their order
    // ("#2"), as before; that order is the LUIDs', which can change with a restart. LegacyKey is that older key for every adapter.
    public static IReadOnlyList<GpuAdapter> Keyed(IEnumerable<GpuAdapter> adapters)
    {
        var counts = new Dictionary<string, int>();
        var keyed = new List<GpuAdapter>();
        foreach (var adapter in adapters.OrderBy(adapter => adapter.Luid))
        {
            string identity = Identity(adapter);
            int count = counts[identity] = counts.GetValueOrDefault(identity) + 1;
            string legacy = count == 1 ? identity : identity + "#" + count;
            keyed.Add(adapter with { Key = adapter.Location is { Length: > 0 } location ? identity + "@" + location : legacy, LegacyKey = legacy });
        }
        return keyed;
    }
    private static string Identity(GpuAdapter adapter) => adapter.VendorId != 0 ? $"{adapter.VendorId:X4}:{adapter.DeviceId:X4}:{adapter.SubSystemId:X8}"
        : adapter.Name.Length > 0 ? adapter.Name : LuidText(adapter.Luid);
    // A PCI location as Windows gives it (bus, device, function); null for one that is not on PCI (all ones, a software adapter).
    public static string? Location(uint bus, uint device, uint function) =>
        bus > 0xFFFF || device > 31 || function > 7 ? null : $"{bus}:{device}.{function}";

    // One sample per adapter. With adapters described (DXGI listed any), the hardware adapters it lists are kept, those without
    // engine counters at the moment too (NoReadings), so a connected adapter never drops out of the list while it has nothing to
    // read; the software rasterizer has engines of its own, and NPUs and other compute-only devices have counters but are not
    // graphics adapters. Without a description every adapter with counters is kept, unnamed. Discrete adapters come first, then
    // integrated ones, each by name, so the order is the same after a restart.
    public static List<GpuSample> Samples(Dictionary<ulong, GpuTotals> totals, IReadOnlyDictionary<ulong, GpuAdapter> adapters, bool described, string noMemory)
    {
        var samples = new List<GpuSample>(totals.Count);
        foreach (var (luid, total) in totals)
        {
            GpuAdapter? adapter = adapters.TryGetValue(luid, out var known) ? known : null;
            if (described && adapter is null || adapter?.Software == true) continue;
            var engines = new List<GpuEngine>(total.Engines.Count);
            foreach (var (type, value) in total.Engines) if (type.Length > 0) engines.Add(new GpuEngine(type, value / 100));
            double usage = Math.Clamp(total.Usage / 100, 0, 1);
            samples.Add(new GpuSample(usage, Bytes(total.Dedicated, noMemory))
            {
                EngineUsage = usage, VendorId = adapter?.VendorId ?? 0, Location = adapter?.Location,
                Engines = GpuEngines.Order(engines), SharedBytes = Bytes(total.Shared, noMemory),
                Name = adapter?.Name is { Length: > 0 } name ? name : null, DedicatedTotal = adapter?.DedicatedTotal is > 0 and var size ? size : null,
                Luid = luid, Key = adapter?.Key is { Length: > 0 } key ? key : LuidText(luid), LegacyKey = adapter?.LegacyKey ?? "", Kind = adapter?.Kind ?? GpuKind.Unknown
            });
        }
        if (described)
            foreach (var (luid, adapter) in adapters)
                if (!adapter.Software && !totals.ContainsKey(luid))
                    samples.Add(new GpuSample(0, Reading<ulong>.Unavailable(noMemory))
                    {
                        NoReadings = true, VendorId = adapter.VendorId, Location = adapter.Location,
                        SharedBytes = Reading<ulong>.Unavailable(noMemory), Name = adapter.Name.Length > 0 ? adapter.Name : null,
                        DedicatedTotal = adapter.DedicatedTotal > 0 ? adapter.DedicatedTotal : null, Luid = luid, Key = adapter.Key.Length > 0 ? adapter.Key : LuidText(luid),
                        LegacyKey = adapter.LegacyKey, Kind = adapter.Kind
                    });
        samples.Sort(static (a, b) => Rank(a.Kind) != Rank(b.Kind) ? Rank(a.Kind).CompareTo(Rank(b.Kind))
            : string.CompareOrdinal(a.Name ?? "", b.Name ?? "") is var byName and not 0 ? byName : a.Luid.CompareTo(b.Luid));
        return samples;
    }
    // Discrete adapters first, then integrated ones, then those of unknown kind.
    public static int Rank(GpuKind kind) => kind switch { GpuKind.Discrete => 0, GpuKind.Integrated => 1, _ => 2 };
    private static Reading<ulong> Bytes(double? bytes, string reason) => bytes is { } used ? (ulong)Math.Max(0, used) : Reading<ulong>.Unavailable(reason);
}

// Which adapter the monitor shows.
public static class GpuChoice
{
    // 自动: the discrete adapter with the most dedicated memory, the one games and renders run on. Without one, the adapter shown
    // before while it is there, else the busiest at the time. It never moves to another adapter because that one became busier:
    // on a laptop the integrated adapter draws the desktop and is busy whenever the discrete one rests, so following the load
    // would bring back the jumping between adapters this choice is for. The other adapters are a click away on the page.
    // Adapters with readings come first; one without (NoReadings) is taken only when no adapter has readings.
    public static GpuSample? Auto(IReadOnlyList<GpuSample> gpus, ulong previous)
    {
        GpuSample? discrete = null, before = null, busiest = null;
        foreach (var gpu in gpus)
        {
            if (gpu.NoReadings) continue;
            if (gpu.Kind == GpuKind.Discrete && (discrete is null || (gpu.DedicatedTotal ?? 0) > (discrete.DedicatedTotal ?? 0))) discrete = gpu;
            if (gpu.Luid == previous && previous != 0) before = gpu;
            if (busiest is null || gpu.Usage > busiest.Usage) busiest = gpu;
        }
        return discrete ?? before ?? busiest ?? (gpus.Count > 0 ? gpus[0] : null);
    }

    // The saved adapter: by its key; then by the key saved before adapters had a location (LegacyKey), for a choice saved then;
    // then by the LUID it was saved under, for an adapter with no ids; then by its name, for an adapter whose ids changed with its
    // driver, when no other adapter has that name. Null while it is not there (an external GPU unplugged); the choice is kept for
    // when it returns. A choice found any other way than by its key is saved again under the adapter's key (Resolve's upgrade).
    public static GpuSample? Saved(IReadOnlyList<GpuSample> gpus, string key, string? name) =>
        Find(gpus, gpu => (gpu.Key, gpu.LegacyKey, gpu.Name, gpu.Luid), key, name) is var at and >= 0 ? gpus[at] : null;

    // Where the saved adapter is among items described by of (Saved's rules), or -1: the settings page finds the choice it shows
    // as the monitor does, so it never shows another card of the same model as chosen when the chosen one is not there.
    public static int Find<T>(IReadOnlyList<T> items, Func<T, (string Key, string LegacyKey, string? Name, ulong Luid)> of, string key, string? name)
    {
        for (int i = 0; i < items.Count; i++) if (of(items[i]).Key == key) return i;
        // A key with a location names one card: another card of the model is not it, though it has the same ids and name (the
        // chosen card may be the one removed). By name only an adapter whose ids changed (a new driver) is found.
        if (key.IndexOf('@') is var at and > 0)
        {
            string identity = key[..at];
            return Named(items, of, name) is var named and >= 0 && of(items[named]).Key.Split('@')[0] != identity ? named : -1;
        }
        for (int i = 0; i < items.Count; i++) if (of(items[i]).LegacyKey is { Length: > 0 } legacy && legacy == key) return i;
        for (int i = 0; i < items.Count; i++) if (of(items[i]).Luid != 0 && GpuReadings.LuidText(of(items[i]).Luid) == key) return i;
        return Named(items, of, name);
    }
    // Two cards of one model have one name: only an adapter that is the only one with the name is taken by it.
    private static int Named<T>(IReadOnlyList<T> items, Func<T, (string Key, string LegacyKey, string? Name, ulong Luid)> of, string? name)
    {
        if (name is not { Length: > 0 }) return -1;
        int named = -1, count = 0;
        for (int i = 0; i < items.Count; i++) if (of(items[i]).Name == name) { named = i; count++; }
        return count == 1 ? named : -1;
    }

    // The adapter to show for a saved choice (null for 自动), falling back to 自动 while the chosen one is not there. automatic:
    // 自动 chose it, so it is the adapter to keep (previousAuto) next time. upgrade: the key to save the choice under now, when it
    // was found by an older or weaker mark than its key; null otherwise.
    public static GpuSample? Resolve(IReadOnlyList<GpuSample> gpus, string? key, string? name, ulong previousAuto, out bool automatic, out string? upgrade)
    {
        var saved = key is null ? null : Saved(gpus, key, name);
        automatic = saved is null;
        upgrade = saved is not null && saved.Key != key && saved.Key.Length > 0 ? saved.Key : null;
        return saved ?? Auto(gpus, previousAuto);
    }
    public static GpuSample? Resolve(IReadOnlyList<GpuSample> gpus, string? key, string? name, ulong previousAuto, out bool automatic)
        => Resolve(gpus, key, name, previousAuto, out automatic, out _);

    // The adapter after this one, in the list's order, round to the first.
    public static GpuSample? Next(IReadOnlyList<GpuSample> gpus, GpuSample current)
    {
        for (int i = 0; i < gpus.Count; i++) if (gpus[i].Luid == current.Luid) return gpus[(i + 1) % gpus.Count];
        return gpus.Count > 0 ? gpus[0] : null;
    }

    public static string KindName(GpuKind kind) => kind switch
    {
        GpuKind.Discrete => Loc.T("独显", "Discrete"), GpuKind.Integrated => Loc.T("核显", "Integrated"), _ => ""
    };
    // "NVIDIA GeForce RTX 4060 Laptop GPU（独显）", for lists and tooltips.
    public static string Describe(string? name, GpuKind kind)
    {
        string shown = name is { Length: > 0 } ? name : "GPU", word = KindName(kind);
        return word.Length == 0 ? shown : Loc.T($"{shown}（{word}）", $"{shown} ({word})");
    }
}

// The adapters' descriptions (GpuAdapters.Read), read again when the counters name an adapter not described. A read that fails,
// or that misses an adapter (one failed to describe itself), never shuts an adapter out for long: the descriptions it had are kept,
// and an adapter still without one is asked about again after a pause that grows from FirstPause to LongestPause, so one that
// failed once returns within seconds, while a device DXGI never lists (an NPU, which has counters but is no graphics adapter)
// costs a read now and then only. An adapter described again starts over.
public sealed class GpuDescriptions
{
    public const double FirstPause = 5, LongestPause = 600;
    private readonly Func<(IReadOnlyDictionary<ulong, GpuAdapter> Adapters, bool Described, bool Complete)> _read;
    private IReadOnlyDictionary<ulong, GpuAdapter> _adapters = new Dictionary<ulong, GpuAdapter>();
    private readonly Dictionary<ulong, (double At, double Pause)> _waiting = [];
    private double _retryAt = double.NegativeInfinity, _pause;

    public GpuDescriptions(Func<(IReadOnlyDictionary<ulong, GpuAdapter> Adapters, bool Described, bool Complete)> read, double now)
    {
        _read = read;
        Read([], now);
    }

    // Whether any read described an adapter; until then the samples are kept unnamed (GpuReadings.Samples).
    public bool Described { get; private set; }
    // How many times the adapters were read (for the tests).
    public int Reads { get; private set; }
    public IReadOnlyDictionary<ulong, GpuAdapter> Adapters => _adapters;

    // The descriptions for the adapters the counters list now (luids), read again when one is not described and its pause is over.
    public IReadOnlyDictionary<ulong, GpuAdapter> For(IReadOnlyCollection<ulong> luids, double now)
    {
        bool due = !Described && now >= _retryAt;
        foreach (var luid in luids)
            if (!_adapters.ContainsKey(luid) && (!_waiting.TryGetValue(luid, out var wait) || now >= wait.At)) due = true;
        if (due) Read(luids, now);
        // Adapters that left the counters are forgotten, so one that comes back is asked about at once.
        if (_waiting.Count > 0) foreach (var luid in _waiting.Keys.Where(luid => !luids.Contains(luid)).ToList()) _waiting.Remove(luid);
        return _adapters;
    }

    private void Read(IReadOnlyCollection<ulong> luids, double now)
    {
        Reads++;
        var (adapters, described, complete) = _read();
        if (described)
        {
            // An adapter this read missed keeps the description it had.
            if (!complete && _adapters.Count > 0)
            {
                var merged = new Dictionary<ulong, GpuAdapter>(adapters);
                foreach (var (luid, adapter) in _adapters) merged.TryAdd(luid, adapter);
                adapters = merged;
            }
            _adapters = adapters; Described = true; _pause = 0;
        }
        else if (!Described) { _pause = Math.Clamp(_pause * 2, FirstPause, LongestPause); _retryAt = now + _pause; }
        foreach (var luid in luids)
        {
            if (_adapters.ContainsKey(luid)) { _waiting.Remove(luid); continue; }
            var pause = Math.Clamp((_waiting.TryGetValue(luid, out var wait) ? wait.Pause : 0) * 2, FirstPause, LongestPause);
            _waiting[luid] = (now + pause, pause);
        }
    }
}
