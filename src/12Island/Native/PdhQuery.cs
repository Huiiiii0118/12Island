using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// A Performance Data Helper query of wildcard counters, read the way Task Manager reads processor time, processor
// frequency and the graphics engines. Rate counters need two collections, so the first reading of each is unavailable.
// One thread uses a query; it is not thread safe.
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = [];
    private IntPtr _buffer;
    private uint _capacity;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    // False when the performance counters could not be opened at all (the service is disabled or its data is damaged).
    public bool IsOpen => _query != IntPtr.Zero;

    // Adds an English counter path such as \Processor Information(*)\% Processor Time. Returns false when the counter
    // does not exist on this machine (graphics counters need a WDDM 2 driver).
    public bool Add(string path)
    {
        if (_query == IntPtr.Zero) return false;
        if (_counters.ContainsKey(path)) return true;
        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != 0) return false;
        _counters[path] = counter;
        return true;
    }

    // Takes a counter out of the query, so the collections no longer read it.
    public void Remove(string path)
    {
        if (!_counters.Remove(path, out var counter)) return;
        PdhRemoveCounter(counter);
    }

    // Takes a reading of every counter. False when the query failed as a whole.
    public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    // The instances of a counter with valid values from the last two collections, by instance name. Empty when the counter
    // has no reading yet (its first collection) or is not in the query.
    public IReadOnlyList<(string Instance, double Value)> Read(string path)
    {
        if (!_counters.TryGetValue(path, out var counter)) return [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _capacity;
            uint status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out uint count, _buffer);
            if (status == MoreData)
            {
                // Instances can appear between the two calls; leave room for a few more.
                Grow(size + 4096);
                continue;
            }
            if (status != 0) return [];
            if (_names.Count > 2 * count + 256) _names.Clear();
            var values = new List<(string, double)>((int)count);
            for (int i = 0; i < count; i++)
            {
                var item = _buffer + i * ItemSize;
                uint itemStatus = (uint)Marshal.ReadInt32(item, 8);
                if (itemStatus is not (ValidData or NewData)) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (!double.IsFinite(value)) continue;
                values.Add((Name(Marshal.ReadIntPtr(item)), value));
            }
            return values;
        }
        return [];
    }

    // Instance names kept between readings: the GPU engine counter alone has hundreds of instances, read every second, and a name
    // seen before is not made again.
    private readonly Dictionary<string, string> _names = [];
    private char[] _name = new char[128];
    private string Name(IntPtr text)
    {
        if (text == IntPtr.Zero) return "";
        int length = lstrlenW(text);
        if (length > _name.Length) _name = new char[length];
        Marshal.Copy(text, _name, 0, length);
        var span = _name.AsSpan(0, length);
        if (_names.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(span, out var known)) return known;
        var name = span.ToString();
        return _names[name] = name;
    }

    private void Grow(uint size)
    {
        if (size <= _capacity) return;
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal((int)size); _capacity = size;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _capacity = 0; }
        _counters.Clear(); _names.Clear();
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: the name pointer, then PDH_FMT_COUNTERVALUE (status, padding, the double).
    private const int ItemSize = 24;
    private const uint FormatDouble = 0x00000200, FormatNoCap100 = 0x00008000, MoreData = 0x800007D2, ValidData = 0, NewData = 1;
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] private static extern int lstrlenW(IntPtr text);
    [DllImport("pdh.dll")] private static extern uint PdhRemoveCounter(IntPtr counter);
}
