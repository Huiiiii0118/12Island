using System.Runtime.InteropServices;
using System.Text;

namespace TwelveIsland.Native;

// Finds and activates a window of an app in the process list: the topmost ordinary window whose process runs the executable
// the entry folds by (ProcessSample.AppKey, its file name), so any of an app's processes may own it. A window on the current
// virtual desktop comes first; failing that, one on another desktop, which the system switches to when the window becomes
// foreground. Background processes and apps left with only a tray icon have none, nor do windows cloaked for other reasons.
// Activate must run inside the click handler: the non-activating island may only hand the foreground to another window while
// it holds that click.
internal static class ProcessWindows
{
    private static IVirtualDesktopManager? _desktops;

    public static nint Find(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return 0;
        var names = new Dictionary<uint, string>();
        nint found = 0, elsewhere = 0;
        EnumWindows((handle, _) =>
        {
            // The cheap tests first, so the process is only looked up for ordinary visible windows. Windows come in z-order,
            // so the first match is the app's most recently used window.
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != 0 || GetWindowTextLength(handle) == 0) return true;
            if (((long)GetWindowLongPtr(handle, GwlExStyle) & WsExToolWindow) != 0) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (!names.TryGetValue(pid, out var name)) names[pid] = name = ImageName(pid);
            if (!string.Equals(name, executable, StringComparison.OrdinalIgnoreCase) || IsShell(handle)) return true;
            DwmGetWindowAttribute(handle, DwmCloaked, out int cloaked, sizeof(int));
            if (cloaked == 0) { found = handle; return false; }
            if (elsewhere == 0 && OnOtherDesktop(handle)) elsewhere = handle;
            return true;
        }, 0);
        return found != 0 ? found : elsewhere;
    }

    // The system hides (cloaks) the windows of the desktops not shown; it cloaks windows for other reasons too, such as a
    // suspended app's frame, and those have no desktop to switch to.
    private static bool OnOtherDesktop(nint handle)
    {
        try
        {
            _desktops ??= (IVirtualDesktopManager)new VirtualDesktopManager();
            return _desktops.IsWindowOnCurrentVirtualDesktop(handle, out bool current) == 0 && !current
                && _desktops.GetWindowDesktopId(handle, out var desktop) == 0 && desktop != Guid.Empty;
        }
        catch (COMException) { return false; }
    }

    public static bool Activate(nint handle)
    {
        if (handle == 0) return false;
        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        return SetForegroundWindow(handle);
    }

    // The desktop and the taskbar belong to explorer.exe too; they are not its windows to bring forward.
    private static bool IsShell(nint handle)
    {
        var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static string ImageName(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return "";
        try
        {
            var text = new char[1024]; int size = text.Length;
            return QueryFullProcessImageNameW(process, 0, text, ref size) ? System.IO.Path.GetFileName(new string(text, 0, size)) : "";
        }
        finally { CloseHandle(process); }
    }

    private const int GwlExStyle = -20, SwRestore = 9, DwmCloaked = 14;
    private const uint GwOwner = 4, ProcessQueryLimitedInformation = 0x1000;
    private const long WsExToolWindow = 0x80;
    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")] private class VirtualDesktopManager;
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint window, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(nint window, ref Guid desktop);
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(nint process, int flags, char[] name, ref int size);
}
