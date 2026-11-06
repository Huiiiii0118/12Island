using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// The applications that currently have a visible top-level window, for a list that picks an app by process name. One entry
// per process: several windows of an app describe the same application.
internal static class RunningApps
{
    internal sealed record App(string Name, string Path);

    public static IReadOnlyList<App> WithWindows()
    {
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName;
                if (name.Length == 0 || apps.ContainsKey(name)) return true;
                string path = "";
                try { path = process.MainModule?.FileName ?? ""; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                apps[name] = new App(name, path);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return apps.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
