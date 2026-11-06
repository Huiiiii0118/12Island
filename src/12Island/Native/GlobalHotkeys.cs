using System.Runtime.InteropServices;
using System.Windows.Interop;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Global hotkeys registered on a message-only window that the app owns for its whole run. The panels are closed and opened
// again when monitors change, so their windows cannot hold the registrations; this window has no monitor, no position and
// no DPI, and Explorer restarting does not affect it. A hotkey may hold several combinations, told apart by their slot (the
// resize shortcut's four arrow keys).
internal sealed class GlobalHotkeys : IDisposable
{
    // Slots per hotkey in the registration ids: room for more combinations than the four arrow keys need.
    private const int WmHotkey = 0x0312, ErrorHotkeyAlreadyRegistered = 1409, Slots = 8;
    private readonly HwndSource _source;
    private readonly HashSet<(HotkeyKind Kind, int Slot)> _registered = [];
    public event Action<HotkeyKind, int>? Pressed;

    public GlobalHotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("TwelveIsland.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 }); // HWND_MESSAGE
        _source.AddHook(Hook);
    }

    // Registers a combination given as RegisterHotKey's values; Active, InUse when another program holds it, Invalid otherwise.
    public HotkeyStatus Register(HotkeyKind kind, uint fsModifiers, uint vk, int slot = 0)
    {
        Unregister(kind, slot);
        if (RegisterHotKey(_source.Handle, Id(kind, slot), fsModifiers, vk)) { _registered.Add((kind, slot)); return HotkeyStatus.Active; }
        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered ? HotkeyStatus.InUse : HotkeyStatus.Invalid;
    }

    // Every combination of the hotkey.
    public void Unregister(HotkeyKind kind)
    {
        foreach (var (registered, slot) in _registered.Where(entry => entry.Kind == kind).ToList()) Unregister(registered, slot);
    }

    private void Unregister(HotkeyKind kind, int slot)
    {
        if (_registered.Remove((kind, slot))) UnregisterHotKey(_source.Handle, Id(kind, slot));
    }

    // The monitor under the mouse pointer, or the nearest one.
    public static IntPtr CursorMonitor() { GetCursorPos(out var point); return MonitorFromPoint(point, 2); }

    public void Dispose()
    {
        foreach (var (kind, slot) in _registered.ToList()) Unregister(kind, slot);
        _source.RemoveHook(Hook); _source.Dispose();
    }

    private static int Id(HotkeyKind kind, int slot) => 0xB100 + (int)kind * Slots + slot;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        foreach (var (kind, slot) in _registered)
            if (wParam.ToInt32() == Id(kind, slot)) { handled = true; Pressed?.Invoke(kind, slot); break; }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
}
