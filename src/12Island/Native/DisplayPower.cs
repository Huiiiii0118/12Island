using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using TwelveIsland.Core;
using Microsoft.Win32;

namespace TwelveIsland.Native;

// Whether this session's display is off (DisplayWatch). Windows sends the state as WM_POWERBROADCAST to a window that asked for
// it, here a hidden window of its own, and sends the current state at once when asked. GUID_SESSION_DISPLAY_STATUS is the
// display of this session, so a remote desktop session is not taken for off while the machine's own monitor sleeps. Changed is
// raised on the UI thread.
internal static class DisplayPower
{
    private static readonly DisplayWatch Watch = new();
    private static Dispatcher? _dispatcher;
    private static HwndSource? _window;
    private static IntPtr _notification;
    private static DispatcherTimer? _recheck;

    public static bool Off => Watch.Off;
    public static event Action? Changed;

    public static void Install(Dispatcher dispatcher)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        SystemEvents.SessionSwitch += SessionSwitch;
        try
        {
            _window = new HwndSource(new HwndSourceParameters("TwelveIsland display") { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x08000000, Width = 0, Height = 0 });
            _window.AddHook(PowerMessage);
            Register();
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException) { _window?.Dispose(); _window = null; }
    }

    public static void Uninstall()
    {
        if (_dispatcher is null) return;
        SystemEvents.SessionSwitch -= SessionSwitch;
        _recheck?.Stop(); _recheck = null;
        Unregister();
        _window?.Dispose(); _window = null;
        _dispatcher = null; Changed = null;
    }

    // The pointer reached an island: whatever was last reported, the display is on.
    public static void InputSeen() { if (Watch.Seen()) Raise(); }

    private static void Register()
    {
        if (_window is null) return;
        var setting = SessionDisplayStatus;
        _notification = RegisterPowerSettingNotification(_window.Handle, ref setting, 0);
    }
    private static void Unregister()
    {
        if (_notification == IntPtr.Zero) return;
        UnregisterPowerSettingNotification(_notification); _notification = IntPtr.Zero;
    }

    // Back at the session (an unlock, a sign-in, a console or remote connection), the user can see it.
    private static void SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _dispatcher?.BeginInvoke(InputSeen);
    }

    private static IntPtr PowerMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int PowerBroadcast = 0x0218, PowerSettingChange = 0x8013;
        if (message != PowerBroadcast || wParam != PowerSettingChange || lParam == IntPtr.Zero) return IntPtr.Zero;
        // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then the data.
        if (Marshal.PtrToStructure<Guid>(lParam) != SessionDisplayStatus) return IntPtr.Zero;
        if (Watch.Report(Marshal.ReadInt32(lParam, 16), Marshal.ReadInt32(lParam, 20))) Raise();
        return IntPtr.Zero;
    }

    private static void Raise()
    {
        // While it says off, the state is asked again now and then: registering again has Windows send the current state, in
        // case the report that the display came back was missed.
        if (Watch.Off)
        {
            if (_recheck is null)
            {
                _recheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = DisplayWatch.Recheck };
                _recheck.Tick += (_, _) => { Unregister(); Register(); };
            }
            _recheck.Start();
        }
        else _recheck?.Stop();
        Changed?.Invoke();
    }

    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    [DllImport("user32")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
