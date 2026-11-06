using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TwelveIsland.Native;

// Observe presses only while a popup is open. Always pass input through to its
// destination, including windows owned by another process.
internal sealed class PointerPressObserver : IDisposable
{
    private readonly HookProc _callback;
    private nint _hook;
    public PointerPressObserver(Dispatcher dispatcher, Action<Point> pressed)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && message.ToInt32() is 0x0201 or 0x0204 or 0x0207 or 0x020B && !dispatcher.HasShutdownStarted)
            {
                var point = Marshal.PtrToStructure<POINT>(data);
                // A popup may close before this runs. Deliver this already observed
                // press so its owner can still collapse on the same outside click.
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => pressed(new Point(point.X, point.Y)));
            }
            return CallNextHookEx(_hook, code, message, data);
        };
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
    }
    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook); _hook = 0;
    }
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
