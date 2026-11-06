using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TwelveIsland.Native;

// Tells the panel whenever anything is copied: a message-only window that asks Windows for clipboard changes
// (AddClipboardFormatListener) and raises Changed on the thread that created it.
internal sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int HwndMessage = -3;
    private readonly HwndSource _source;
    private bool _disposed;

    public event Action? Changed;

    public ClipboardWatcher()
    {
        var parameters = new HwndSourceParameters("TwelveIsland clipboard")
        {
            ParentWindow = new IntPtr(HwndMessage), WindowStyle = 0, Width = 0, Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
        if (!AddClipboardFormatListener(_source.Handle)) throw new InvalidOperationException("clipboard listener was refused");
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate && !_disposed) Changed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
