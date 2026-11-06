using System.Runtime.InteropServices;
using System.Text;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Finds and activates the playing application's top-level window. Activate must run inside the click handler: the
// non-activating island may only hand the foreground to another window while it holds the input that was just clicked.
internal static class MediaAppWindows
{
    public static nint Find(MediaAppOwner owner)
    {
        var windows = new List<MediaAppWindow>();
        var processes = new Dictionary<uint, MediaAppProcess>();
        EnumWindows((handle, _) =>
        {
            // The cheap tests first, so process and property lookups only run for ordinary visible windows.
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != 0 || GetWindowTextLength(handle) == 0) return true;
            long style = (long)GetWindowLongPtr(handle, GwlExStyle);
            if ((style & WsExToolWindow) != 0) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (!processes.TryGetValue(pid, out var process)) processes[pid] = process = AppIdentity.Describe((int)pid);
            DwmGetWindowAttribute(handle, DwmCloaked, out int cloaked, sizeof(int));
            var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
            // A window without an id of its own belongs to the application its process is identified as (QueMusic sets
            // its id for the whole process).
            string appId = AppIdOf(handle) is { Length: > 0 } own ? own : process.AppId;
            windows.Add(new(handle, (int)pid, process.ProcessName, process.PackageFamily, appId, true, IsIconic(handle), false, false, cloaked != 0, title.ToString()));
            return true;
        }, 0);
        return MediaAppTarget.Choose(owner, windows)?.Handle ?? 0;
    }

    public static bool Activate(nint handle)
    {
        if (handle == 0) return false;
        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        return SetForegroundWindow(handle);
    }

    // The AppUserModelID set on the window itself, which is how a packaged application's frame window names its application.
    private static string AppIdOf(nint handle)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(handle, ref iid, out var store) != 0) return "";
        try
        {
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            if (store.GetValue(ref key, out var value) != 0) return "";
            try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private const int GwlExStyle = -20, SwRestore = 9, DwmCloaked = 14;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x80;
    private const ushort VtLpwstr = 31;
    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint handle, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
