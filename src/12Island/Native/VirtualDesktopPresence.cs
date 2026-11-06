using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TwelveIsland.Native;

// Pin the overlay's shell view, not its application ID: settings windows remain ordinary windows.
internal sealed class VirtualDesktopPresence : IDisposable
{
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private IApplicationViewCollection? _views;
    private IVirtualDesktopPinnedApps? _pins;
    private bool _disposed;
    // The wait after a failed check: it doubles while the shell keeps refusing (an interface changed in an update, say), so a failure
    // that will not go away costs a check a minute, not one every two seconds.
    private double _failPause = FirstPause;
    private const double FirstPause = 2, LongestPause = 60;

    public VirtualDesktopPresence(Window window)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(Hook);
        window.IsVisibleChanged += VisibilityChanged;
        _retry.Tick += Retry;
        _retry.Start();
        EnsurePinned();
    }

    private void EnsurePinned()
    {
        if (_disposed) return;
        IntPtr view = IntPtr.Zero;
        try
        {
            Connect();
            Marshal.ThrowExceptionForHR(_views!.GetViewForHwnd(_handle, out view));
            if (view == IntPtr.Zero) throw new COMException("The shell view is not registered yet.", unchecked((int)0x80070490));
            Marshal.ThrowExceptionForHR(_pins!.IsViewPinned(view, out bool pinned));
            if (!pinned)
            {
                Marshal.ThrowExceptionForHR(_pins.PinView(view));
                Marshal.ThrowExceptionForHR(_pins.IsViewPinned(view, out pinned));
            }
            // Once pinned, an Explorer restart or a visibility change triggers the next check; the timer is only a fallback.
            _failPause = FirstPause;
            _retry.Interval = TimeSpan.FromSeconds(pinned ? LongestPause : FirstPause);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Disconnect();
            _retry.Interval = TimeSpan.FromSeconds(_failPause);
            _failPause = Math.Min(LongestPause, _failPause * 2);
        }
        finally { if (view != IntPtr.Zero) Marshal.Release(view); }
    }

    private void Connect()
    {
        if (_views is not null && _pins is not null) return;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239"), true)!)!;
            var services = (IShellServices)shell;
            var viewId = typeof(IApplicationViewCollection).GUID;
            var pinId = typeof(IVirtualDesktopPinnedApps).GUID;
            var pinService = new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
            Marshal.ThrowExceptionForHR(services.QueryService(ref viewId, ref viewId, out var views));
            _views = (IApplicationViewCollection)views;
            Marshal.ThrowExceptionForHR(services.QueryService(ref pinService, ref pinId, out var pins));
            _pins = (IVirtualDesktopPinnedApps)pins;
        }
        finally { if (shell is not null) Marshal.ReleaseComObject(shell); }
    }

    private void Retry(object? sender, EventArgs args)
    {
        if (_window.IsVisible) EnsurePinned();
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_window.IsVisible) _window.Dispatcher.BeginInvoke(() => EnsurePinned(), DispatcherPriority.Loaded);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated)
        {
            // Explorer owns these COM services. Its replacement needs fresh proxies and a fresh view.
            Reconnect();
        }
        return IntPtr.Zero;
    }

    private void Reconnect()
    {
        if (_disposed) return;
        _failPause = FirstPause;
        Disconnect();
        _window.Dispatcher.BeginInvoke(() => EnsurePinned(), DispatcherPriority.Loaded);
    }

    private void Disconnect()
    {
        if (_pins is not null) { Marshal.ReleaseComObject(_pins); _pins = null; }
        if (_views is not null) { Marshal.ReleaseComObject(_views); _views = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Stop(); _retry.Tick -= Retry;
        _window.IsVisibleChanged -= VisibilityChanged;
        _source.RemoveHook(Hook);
        Disconnect();
        // A window pin ends with its shell view; no persistent application-wide pin is written.
    }

    // Shell ABI declarations verified against Microsoft's TypeAgent virtual desktop integration.
    // Only the required vtable prefixes are declared; the opaque view pointer is released after use.
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServices
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IntPtr views);
        [PreserveSig] int GetViewsByZOrder(out IntPtr views);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr views);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int UnpinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int IsViewPinned(IntPtr view, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinView(IntPtr view);
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
