using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

public sealed class DesktopSources : IDesktopSources
{
    public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => ClaudeDesktopReader.Read(locations);
    public GrokBotSignIn ReadGrokBotDesktop(DataLocations locations) => GrokBotDesktopReader.Read(locations);

    public string? ReadCursorToken(string database)
    {
        if (!File.Exists(database)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, "SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken' LIMIT 1", -1, out statement, IntPtr.Zero) != 0) return null;
            return sqlite3_step(statement) == 100 ? Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) : null;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql)
    {
        if (!File.Exists(database)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero) != 0) return null;
            var rows = new List<string?[]>();
            int columns = sqlite3_column_count(statement), step;
            while ((step = sqlite3_step(statement)) == 100) // SQLITE_ROW
            {
                var row = new string?[columns];
                for (int i = 0; i < columns; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            return step == 101 ? rows : null; // SQLITE_DONE
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    // Discovery asks WMI for the process command line, so its result is kept for as long as that process lives
    // and still listens on the same ports. Each refresh then costs one read of the system's listener table.
    private (int Id, DateTime Started, IReadOnlyList<LocalEndpoint> Endpoints)[] _antigravity = [];

    public async Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
    {
        var listeners = TcpListeners.Read();
        var known = _antigravity.Where(p => Alive(p.Id, p.Started) &&
            p.Endpoints.All(e => listeners.Any(l => l.ProcessId == p.Id && l.Port == e.Port))).ToArray();
        if (known.Length > 0 && known.Length == _antigravity.Length) return known.SelectMany(p => p.Endpoints).ToList();
        // No language server running (Antigravity closed): nothing to ask WMI about.
        if (!ProcessSnapshot.Any(name => name.Contains("language_server", StringComparison.OrdinalIgnoreCase))) { _antigravity = []; return []; }
        // A WMI query has no timeout of its own; a stalled service must not hold the refresh.
        var processes = await Task.Run(FindAntigravityProcesses, cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation);
        var found = new List<(int, DateTime, IReadOnlyList<LocalEndpoint>)>();
        foreach (var (pid, command) in processes)
        {
            var match = Regex.Match(command, "--csrf_token(?:=|\\s+)\"?([^\\s\"]+)");
            if (!match.Success || StartTime(pid) is not { } started) continue;
            var endpoints = listeners.Where(l => l.ProcessId == pid).Select(l => l.Port).Distinct()
                .SelectMany(port => new LocalEndpoint[] { new(port, "https", match.Groups[1].Value), new(port, "http", match.Groups[1].Value) }).ToList();
            if (endpoints.Count > 0) found.Add((pid, started, endpoints));
        }
        _antigravity = [.. found];
        return found.SelectMany(p => p.Item3).ToList();
    }

    private static DateTime? StartTime(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    private static bool Alive(int pid, DateTime started) => StartTime(pid) == started;

    private static List<(int Id, string Command)> FindAntigravityProcesses()
    {
        // WMI reads only the matching local process metadata; credentials never leave this method's result in memory.
        var processes = new List<(int Id, string Command)>();
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (locatorType is null) return processes;
        dynamic locator = Activator.CreateInstance(locatorType)!;
        dynamic? service = null, rows = null;
        try
        {
            service = locator.ConnectServer(".", "root\\cimv2");
            rows = service.ExecQuery("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE '%language_server%'");
            foreach (dynamic row in rows)
            {
                try
                {
                    string path = WmiProperty(row, "ExecutablePath") as string ?? "";
                    string command = WmiProperty(row, "CommandLine") as string ?? "";
                    if (path.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) processes.Add((Convert.ToInt32(WmiProperty(row, "ProcessId")), command));
                }
                finally { Marshal.FinalReleaseComObject(row); }
            }
        }
        finally
        {
            if (rows is not null) Marshal.FinalReleaseComObject(rows);
            if (service is not null) Marshal.FinalReleaseComObject(service);
            Marshal.FinalReleaseComObject(locator);
        }
        return processes;
    }

    private static object? WmiProperty(object row, string name)
    {
        // Use named SWbemPropertySet access. Dynamic property access on SWbemObject
        // failed after the first query in repeated-refresh testing (0x80004005).
        dynamic? properties = null, property = null;
        try
        {
            properties = ((dynamic)row).Properties_;
            property = properties.Item(name);
            return property.Value;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            if (properties is not null) Marshal.FinalReleaseComObject(properties);
        }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
}
