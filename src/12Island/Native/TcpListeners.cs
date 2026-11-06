using System.Net;
using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// IPv4 listening sockets with their owning process, read from the system table without starting netstat.
internal static class TcpListeners
{
    public readonly record struct Listener(IPAddress Address, int Port, int ProcessId);

    public static List<Listener> Read()
    {
        var listeners = new List<Listener>();
        nint table = 0;
        try
        {
            int size = 0;
            GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // IPv4, TCP_TABLE_OWNER_PID_LISTENER
            if (size <= 0 || size > 16_777_216) return listeners;
            table = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return listeners;
            int count = Marshal.ReadInt32(table), rowSize = Marshal.SizeOf<Row>();
            if (count < 0 || count > (size - sizeof(int)) / rowSize) return listeners;
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<Row>(table + sizeof(int) + i * rowSize);
                int port = (int)((row.LocalPort & 255) << 8 | (row.LocalPort >> 8 & 255));
                listeners.Add(new(new IPAddress(row.LocalAddress), port, (int)row.Pid));
            }
            return listeners;
        }
        finally { if (table != 0) Marshal.FreeHGlobal(table); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Row { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);
}
