using System.Runtime.InteropServices;
using System.Windows;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// A connected monitor. Id is the monitor's device path, which stays the same across restarts and changes of the primary
// monitor; the GDI device name (\\.\DISPLAY1) and the monitor handle may change whenever the display layout changes.
// Refresh: the refresh rate of the monitor's mode in hertz, null where the display configuration does not give one.
public sealed record DisplayInfo(string Id, string Name, string Device, IntPtr Handle, bool Primary, Rect Bounds)
{
    public double? Refresh { get; init; }
}

public static class Displays
{
    // The primary monitor first, then the others from left to right and top to bottom.
    public static IReadOnlyList<DisplayInfo> Current()
    {
        var targets = TargetNames();
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            bool known = targets.TryGetValue(info.Device, out var target);
            string id = target.Path is { Length: > 0 } path ? path : info.Device;
            // A laptop's own panel usually reports no model name.
            string name = target.Name is { Length: > 0 } friendly ? friendly
                : known && target.Technology is 6 or 11 or 13 or unchecked((int)0x80000000) ? Loc.T("内置显示器", "Built-in display")
                : Loc.T("显示器 ", "Display ") + info.Device.Replace(@"\\.\DISPLAY", "");
            var bounds = new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            list.Add(new DisplayInfo(id, name, info.Device, monitor, (info.Flags & 1) != 0, bounds) { Refresh = known ? target.Refresh : null });
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top).ToList();
    }

    // Monitors of the same model are told apart by their GDI number.
    public static string Label(DisplayInfo display, IReadOnlyList<DisplayInfo> all)
    {
        string name = all.Count(d => d.Name == display.Name) > 1 && display.Device.StartsWith(@"\\.\DISPLAY", StringComparison.Ordinal)
            ? $"{display.Name} {display.Device[11..]}" : display.Name;
        return $"{name} ({display.Bounds.Width:0}×{display.Bounds.Height:0})";
    }

    // GDI device name of each active source -> the device path, friendly name, connection and refresh rate of the first monitor it drives.
    private static Dictionary<string, (string Path, string Name, int Technology, double? Refresh)> TargetNames()
    {
        var names = new Dictionary<string, (string Path, string Name, int Technology, double? Refresh)>(StringComparer.OrdinalIgnoreCase);
        const uint ActivePaths = 2;
        if (GetDisplayConfigBufferSizes(ActivePaths, out uint pathCount, out uint modeCount) != 0) return names;
        var paths = new PATH_INFO[pathCount]; var modes = new MODE_INFO[modeCount];
        if (QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return names;
        for (int i = 0; i < pathCount; i++)
        {
            var source = new SOURCE_DEVICE_NAME { Type = 1, Size = Marshal.SizeOf<SOURCE_DEVICE_NAME>(), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId };
            var target = new TARGET_DEVICE_NAME { Type = 2, Size = Marshal.SizeOf<TARGET_DEVICE_NAME>(), Adapter = paths[i].TargetAdapter, Id = paths[i].TargetId };
            if (DisplayConfigGetDeviceInfo(ref source) != 0 || DisplayConfigGetDeviceInfo(ref target) != 0) continue;
            double? refresh = paths[i].RefreshDenominator > 0 && paths[i].RefreshNumerator > 0 ? paths[i].RefreshNumerator / (double)paths[i].RefreshDenominator : null;
            names.TryAdd(source.GdiName, (target.DevicePath ?? "", target.FriendlyName ?? "", target.OutputTechnology, refresh));
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    // DISPLAYCONFIG_PATH_INFO: the source and target infos are flattened; only their adapters and ids are read.
    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public LUID SourceAdapter; public uint SourceId, SourceModeIndex, SourceFlags;
        public LUID TargetAdapter; public uint TargetId, TargetModeIndex;
        public int OutputTechnology, Rotation, Scaling; public uint RefreshNumerator, RefreshDenominator; public int ScanLineOrdering, TargetAvailable;
        public uint TargetFlags, Flags;
    }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct MODE_INFO { public int InfoType; public uint Id; public LUID Adapter; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public int Type, Size; public LUID Adapter; public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_DEVICE_NAME
    {
        public int Type, Size; public LUID Adapter; public uint Id;
        public uint Flags; public int OutputTechnology; public ushort ManufacturerId, ProductCodeId; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref RECT clip, IntPtr data);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc callback, IntPtr data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME request);
    [DllImport("user32")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME request);
}
