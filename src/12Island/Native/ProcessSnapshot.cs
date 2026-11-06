using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// The running processes from one Toolhelp snapshot: their ids, parents and executable file names, without opening any of them.
// Far cheaper than Process.GetProcessesByName, which reads every process's threads too, so a player that is not running costs
// little to look for. The file names are read in place: looking for a player makes no string per process.
internal static class ProcessSnapshot
{
    public delegate bool NameMatch(ReadOnlySpan<char> file);

    // Each process's parent and executable file name.
    public static Dictionary<int, (int Parent, string File)> Read()
    {
        var processes = new Dictionary<int, (int, string)>();
        Walk((in PROCESSENTRY32W entry) => { processes[entry.th32ProcessID] = (entry.th32ParentProcessID, Name(entry).ToString()); return true; });
        return processes;
    }

    // The ids of the processes of this executable.
    public static HashSet<int> Ids(string file)
    {
        var ids = new HashSet<int>();
        Walk((in PROCESSENTRY32W entry) => { if (Name(entry).Equals(file, StringComparison.OrdinalIgnoreCase)) ids.Add(entry.th32ProcessID); return true; });
        return ids;
    }

    // Whether a process of this executable ("cloudmusic.exe") runs.
    public static bool Any(string file) => Any(name => name.Equals(file, StringComparison.OrdinalIgnoreCase));
    // Whether a process runs whose executable's file name matches.
    public static bool Any(NameMatch match)
    {
        bool found = false;
        Walk((in PROCESSENTRY32W entry) => !(found = match(Name(entry))));
        return found;
    }

    private static ReadOnlySpan<char> Name(in PROCESSENTRY32W entry)
    {
        ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)entry.szExeFile);
        int end = name.IndexOf('\0');
        return end < 0 ? name : name[..end];
    }

    private delegate bool Each(in PROCESSENTRY32W entry);

    // Calls each until it returns false.
    private static void Walk(Each each)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == new IntPtr(-1)) return;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = Unsafe.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more && each(entry); more = Process32NextW(snapshot, ref entry)) { }
        }
        finally { CloseHandle(snapshot); }
    }

    // The name's UTF-16 units as ushort: a char is not blittable to DllImport, which would copy it as one ANSI byte.
    [InlineArray(260)]
    private struct ExeFile { private ushort _first; }

    // Blittable, so each call fills it in place without marshalling the name into a string.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32W
    {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        public ExeFile szExeFile;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll")] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
