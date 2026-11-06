using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwelveIsland.Native;

// The non-activating island cannot rely on WPF keyboard focus after a hover.
// Reserve Escape only while it is expanded; release it as soon as it closes.
internal sealed class EscapeDismissal : IDisposable
{
    private const int HotKeyId = 0xBD01;
    private readonly HwndSource _source;
    private readonly Action _dismiss;
    public bool IsEnabled { get; private set; }
    public EscapeDismissal(Window window, Action dismiss)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _dismiss = dismiss; _source.AddHook(HandleMessage);
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled == IsEnabled) return;
        if (enabled) IsEnabled = RegisterHotKey(_source.Handle, HotKeyId, 0x4000, 0x1B);
        else { UnregisterHotKey(_source.Handle, HotKeyId); IsEnabled = false; }
    }
    private IntPtr HandleMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotKeyId && IsEnabled)
        { handled = true; _dismiss(); }
        return IntPtr.Zero;
    }
    public void Dispose() { SetEnabled(false); _source.RemoveHook(HandleMessage); }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
