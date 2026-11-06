using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

public sealed class WindowsHost : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly Func<IntPtr> _monitor;
    // The foreground window's app, as the detection reads it: the empty pair on the desktop.
    private string _appName = "", _appPath = "";
    // The app the answer resolves for: the window whose context was detected. On a background full screen that is the
    // window behind the foreground one, which is the window the user is looking at, so its own rule has to answer.
    private string _resolvedName = "", _resolvedPath = "";
    public (string Name, string Path) App => (_resolvedName, _resolvedPath);
    // How that background window fills the screen — Maximized or Borderless — so that the app's own rule can answer for
    // the context it is in; Desktop when the context is not a background full screen.
    private ScreenContext _backgroundKind = ScreenContext.Desktop;
    public ScreenContext BackgroundKind => _backgroundKind;
    // The background window's app, kept apart from the foreground one: another app taking the screen behind the
    // foreground window changes which rule speaks, but it is not the foreground app changing.
    private string _backgroundName = "", _backgroundPath = "";
    // The foreground window changes rarely but is polled once a second, so its process is read once per window.
    private IntPtr _identityWindow;
    private uint _identityPid;
    private string _identityName = "", _identityPath = "";
    // The background window gets a reading of its own, so that reading one window does not throw the other away.
    private IntPtr _backgroundWindow;
    private uint _backgroundPid;
    private string _backgroundIdentityName = "", _backgroundIdentityPath = "";
    public event Action<ScreenContext>? ContextChanged;
    // The app the answer was resolved for changed while the context stayed, carrying that app and the way its window
    // fills the screen, as they were for the last answer. The app rules may now answer differently for the same context,
    // but nothing about the panel's own state needs to change — the panel does not fold away for an app switch, only for
    // a context switch.
    public event Action<(string Name, string Path, ScreenContext Kind)>? AppChanged;
    public ScreenContext Current { get; private set; }
    // monitor gives the monitor whose foreground window decides the context. The context is reported with the app it was
    // detected for, and a change of either is a change: two apps can read as the same context with different app rules.
    // backgroundWindows says whether a full-screen window behind the foreground one counts; off, only the foreground is read.
    public WindowsHost(Func<IntPtr> monitor, Func<IntPtr> window, Func<Rect?> island, Func<bool>? forceTopmost = null,
        Func<bool>? backgroundWindows = null)
    {
        _monitor = monitor; _window = window; _island = island; _forceTopmost = forceTopmost; _backgroundWindows = backgroundWindows;
        _timer.Tick += (_, _) => Poll();
        // The WinEvent hooks (foreground / show / reorder) react to changes immediately; the poll is only a safety net for
        // what the events miss, so it runs at a calm one-per-second pace rather than several times a second.
        _orderEvent = OnOrderEvent;
        _hooks = [Hook(0x0003), Hook(0x8002), Hook(0x8004)]; // EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_SHOW, EVENT_OBJECT_REORDER
        _timer.Start();
    }
    // Polling alone leaves the panel covered for up to a poll, longer while this thread is busy, each time another window
    // raises itself. A z-order change among top-level windows is reported as a reorder of the desktop window's children,
    // and a window coming up as a show or a foreground event, so each of these asks for a check of the order at once; the
    // poll stays for whatever the events miss. The hooks are out of context and skip this process: the events arrive as
    // messages on this thread, and a burst of them runs one check.
    private readonly WinEventProc _orderEvent;
    private readonly IntPtr[] _hooks;
    private bool _orderCheckPending;
    private static readonly IntPtr DesktopWindow = GetDesktopWindow();
    private IntPtr Hook(uint winEvent) => SetWinEventHook(winEvent, winEvent, IntPtr.Zero, _orderEvent, 0, 0, 0x0002); // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
    private void OnOrderEvent(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        // Reorders of the desktop's children only, which are the top-level windows; other events for windows only, not
        // for carets or cursors.
        if (winEvent == 0x8004 ? window != DesktopWindow : idObject != 0 || idChild != 0) return;
        if (_orderCheckPending) return;
        _orderCheckPending = true;
        _timer.Dispatcher.BeginInvoke(() => { _orderCheckPending = false; WatchOrder(_window()); });
    }
    private readonly Func<IntPtr> _window;
    private readonly Func<bool>? _forceTopmost;
    private readonly Func<bool>? _backgroundWindows;
    // The strip the island actually draws in, in screen physical pixels, as of the last layout; null before the first.
    private readonly Func<Rect?> _island;
    private string _raisedName = "", _raisedPath = "";
    private ScreenContext _raisedKind = ScreenContext.Desktop;
    private void Poll()
    {
        var context = DetectNow();
        var change = ScreenPolicy.WhatChanged(context, _resolvedName, _resolvedPath, Current, _raisedName, _raisedPath);
        if (change == ScreenPolicy.ForegroundChange.None) { WatchOrder(_window()); }
        else
        {
            var previous = (_raisedName, _raisedPath, _raisedKind);
            Current = context; _raisedName = _resolvedName; _raisedPath = _resolvedPath; _raisedKind = _backgroundKind;
            if (change == ScreenPolicy.ForegroundChange.Context) ContextChanged?.Invoke(context);
            else AppChanged?.Invoke(previous);
            WatchOrder(_window());
        }
    }

    // The panel is a topmost overlay: nothing belongs over it but another topmost window, and nothing ordinary may
    // cover it. A show-desktop sweep, or an app that moves itself about in the z-order, can leave the panel beneath
    // ordinary windows — and then no window rule can be seen to act, for the panel is simply covered. The watch looks
    // every poll, and distinguishes what it finds:
    //
    //  - the topmost flag lost, or an ordinary window above the panel: a real anomaly, and the panel is put back at
    //    once, since no ordinary window has any business over a topmost overlay;
    //  - another topmost window covering the island: often deliberate, a pinned video, a screenshot mask, another
    //    overlay. Leave it above the panel unless the user enabled ForceTopmost. The taskbar is always recovered from.
    //    A menu open over the island is always left alone, and so is an app that keeps raising itself (see Yield).
    //    Recovery keeps keyboard focus unchanged and only runs when an overlapping window is found.
    private void WatchOrder(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return;
        var island = _island();
        string? cause = null;
        // The window the cause names, if any; its process is looked up only for the log.
        var culprit = IntPtr.Zero;
        bool recover = false;
        if ((GetWindowLongPtr(window, -20).ToInt64() & 0x8) == 0) { cause = "topmost flag lost"; recover = true; }
        else
        {
            // The windows above the panel in z-order, nearest first: the first one that is shown, not cloaked and not
            // this app's own decides.
            int walked = 0;
            for (var above = GetWindow(window, 3); above != IntPtr.Zero && walked < 32; above = GetWindow(above, 3), walked++)
            {
                if (!IsWindowVisible(above) || IsIconic(above)) continue;
                if (DwmGetWindowAttribute(above, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                GetWindowThreadProcessId(above, out var pid);
                if (pid == Environment.ProcessId) continue;
                bool over = (GetWindowLongPtr(above, -20).ToInt64() & 0x8) != 0;
                if (!over) { cause = "under"; culprit = above; recover = true; break; }
                if (island is { } strip && Covers(above, strip))
                {
                    // The taskbar puts itself on top whenever it is used, and would then hide a capsule moved down over it
                    // until the next use of the panel. Other topmost windows require the explicit setting.
                    if (IsTaskbar(above)) { cause = "covered by topmost taskbar"; recover = true; }
                    else if (IsMenu(above)) { cause = "covered by a menu of"; culprit = above; }
                    else
                    {
                        cause = "covered by topmost"; culprit = above;
                        recover = _forceTopmost?.Invoke() == true;
                        if (recover && Yield()) { cause = "repeatedly covered by topmost"; recover = false; }
                    }
                    break;
                }
            }
        }
        if (cause is null) { _orderCulprit = null; return; }
        // Log repeated obstruction at most every ten seconds, including whether recovery was requested.
        if (recover) SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        if (_orderCulprit != (cause, culprit) || Environment.TickCount64 - _orderLoggedAt > 10000)
        {
            _orderCulprit = (cause, culprit); _orderLoggedAt = Environment.TickCount64;
            var named = culprit == IntPtr.Zero ? cause : cause + " " + ProcessName(culprit);
            NoteOrder($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} panel {named}" + (recover ? "; topmost restored" : "; left as is"));
        }
    }
    private (string Cause, IntPtr Window)? _orderCulprit;
    private long _orderLoggedAt;
    // An app that raises itself whenever it is covered would trade places with the panel without end, the two flickering
    // in turn. Past ten recoveries from topmost windows within two seconds, the panel leaves them above it for five.
    private long _raisesFrom, _yieldUntil;
    private int _raises;
    private bool Yield()
    {
        var now = Environment.TickCount64;
        if (now < _yieldUntil) return true;
        if (now - _raisesFrom > 2000) { _raisesFrom = now; _raises = 0; }
        if (++_raises <= 10) return false;
        _yieldUntil = now + 5000;
        return true;
    }
    // A menu open over the island: the user is at work in that app, and the panel stays beneath it. Besides the system's
    // own menus, a narrow window owned by a shown one, such as a dropdown under a menu bar. A bar across the screen is
    // not a menu even when owned, and neither is a window whose owner is hidden, which is only how some frameworks keep a
    // window off the taskbar.
    private static bool IsMenu(IntPtr window)
    {
        if (ClassName(window, stackalloc char[32]) is "#32768") return true;
        var owner = GetWindow(window, 4); // GW_OWNER
        if (owner == IntPtr.Zero || !IsWindowVisible(owner) || !GetWindowRect(window, out var r)) return false;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref info) && r.Right - r.Left < (info.Monitor.Right - info.Monitor.Left) / 2;
    }
    // Whether the window above reaches into the island's drawn area, given in screen physical pixels.
    private static bool Covers(IntPtr above, Rect island)
    {
        if (!(island.Width > 0) || !GetWindowRect(above, out var r)) return false;
        return r.Left < island.Right && r.Right > island.Left && r.Top < island.Bottom && r.Bottom > island.Top;
    }
    private static bool IsTaskbar(IntPtr window) => ClassName(window, stackalloc char[32]) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    // The desktop and the taskbars.
    private static bool IsShell(IntPtr window) => ClassName(window, stackalloc char[32]) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    // The window's class name, read into the caller's buffer: the windows are polled once a second.
    private static ReadOnlySpan<char> ClassName(IntPtr window, Span<char> buffer) =>
        buffer[..Math.Max(0, GetClassName(window, ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(buffer)), buffer.Length))];
    private static string ProcessName(IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return "pid " + pid_of(window); }
    }
    private static uint pid_of(IntPtr window) { GetWindowThreadProcessId(window, out var pid); return pid; }
    // Notes of buried panels, in the data folder. The current file is kept and a full one moved aside, so earlier
    // notes survive a rotation: zorder.log, then zorder.1.log, zorder.2.log, up to three.
    private const long OrderLogLimit = 262144;
    private const int OrderLogKept = 3;
    private static void NoteOrder(string line)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island", "zorder.log");
            var folder = Path.GetDirectoryName(file);
            if (folder is { Length: > 0 }) Directory.CreateDirectory(folder);
            if (File.Exists(file) && new FileInfo(file).Length >= OrderLogLimit)
                for (int i = OrderLogKept - 1; i >= 1; i--)
                {
                    var from = $"{file}.{i}";
                    if (File.Exists(from)) File.Move(from, $"{file}.{i + 1}", true);
                }
            if (File.Exists(file) && new FileInfo(file).Length >= OrderLogLimit) File.Move(file, file + ".1", true);
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // The context right now, without raising ContextChanged; a caller applies it itself, for instance at startup.
    public ScreenContext DetectNow()
    {
        var context = Detect(_monitor());
        // A background full screen is answered for by the window behind the foreground one; every other context by the
        // foreground app the detection has just read.
        (_resolvedName, _resolvedPath) = context == ScreenContext.BackgroundFullscreen
            ? (_backgroundName, _backgroundPath) : (_appName, _appPath);
        return context;
    }
    public static IntPtr PrimaryMonitor() => MonitorFromPoint(new POINT(), 1);
    public static (Rect Bounds, Rect Work, double Scale) PrimaryScreen() => Screen(PrimaryMonitor());
    // Null when the handle no longer names a connected monitor.
    public static (Rect Bounds, Rect Work, double Scale)? MonitorScreen(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? Screen(monitor) : null;
    }
    // Moves the window without resizing it, in one step and in device pixels.
    public static void MoveTo(Window window, int x, int y)
        => SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    // Raises a visible panel once, preserving focus and display/hide rules.
    public static void RaiseOnce(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle)) return;
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    // Puts a window directly beneath another, without moving, sizing or activating it.
    public static void PlaceBelow(IntPtr window, IntPtr above)
        => SetWindowPos(window, above, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
    // The monitor under the mouse pointer, where a window opened with CenterScreen appears.
    public static (Rect Bounds, Rect Work, double Scale) CursorScreen()
    { GetCursorPos(out var point); return Screen(MonitorFromPoint(point, 2)); }
    // The work area of the monitor nearest to a point given in device pixels.
    public static Rect WorkAreaAt(Point point) => Screen(MonitorFromPoint(new POINT { X = (int)point.X, Y = (int)point.Y }, 2)).Work;
    // The monitor that holds most of the window; the handle tells whether the window has moved to another monitor.
    public static (IntPtr Monitor, Rect Bounds, Rect Work, double Scale) WindowScreen(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var (bounds, work, scale) = Screen(monitor);
        return (monitor, bounds, work, scale);
    }
    private static (Rect Bounds, Rect Work, double Scale) Screen(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        return (info.Monitor.ToRect(), info.Work.ToRect(), dpi > 0 ? dpi / 96.0 : 1);
    }
    public static Point Cursor()
    { GetCursorPos(out var point); return new Point(point.X, point.Y); }
    // Only a foreground window on the given monitor counts; each panel follows the windows on its own monitor.
    private ScreenContext Detect(IntPtr monitor)
    {
        var foreground = GetForegroundWindow();
        _backgroundName = ""; _backgroundPath = ""; _backgroundKind = ScreenContext.Desktop;
        // Nothing but the desktop: a full-screen window elsewhere on this monitor still owns the screen, so the answer is
        // BackgroundFullscreen rather than Desktop. Looked up once, only where it can matter, and only while the settings ask
        // for windows behind the foreground one to count.
        ScreenContext? background = null;
        ScreenContext Desktop()
        {
            if (background is null)
            {
                var filled = _backgroundWindows?.Invoke() == false ? IntPtr.Zero : BackgroundFilled(monitor, foreground);
                background = filled == IntPtr.Zero ? ScreenContext.Desktop : ScreenContext.BackgroundFullscreen;
                if (filled != IntPtr.Zero)
                {
                    // The rules answer for the app that fills the screen, so its identity and the way it fills the screen
                    // travel with the answer: an app that asked to keep the panel's style is then left alone here too.
                    GetWindowThreadProcessId(filled, out var filledPid);
                    DescribeBackground(filled, filledPid, out _backgroundName, out _backgroundPath);
                    _backgroundKind = KindOf(filled, monitor);
                }
            }
            return background.Value;
        }
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) { _appName = ""; _appPath = ""; return Desktop(); }
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId) { _appName = ""; _appPath = ""; return Desktop(); }
        if (MonitorFromWindow(foreground, 2) != monitor) { _appName = ""; _appPath = ""; return Desktop(); }
        if (IsShell(foreground)) { _appName = ""; _appPath = ""; return Desktop(); }
        // The window's app travels with its context, so the app rules can answer for it; the read is cached per window.
        Describe(foreground, pid, out _appName, out _appPath);
        if (SHQueryUserNotificationState(out var state) == 0 && state == 3) return ScreenContext.Exclusive;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        if (DwmGetWindowAttribute(foreground, 9, out RECT rect, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(foreground, out rect);
        var style = GetWindowLongPtr(foreground, -16).ToInt64();
        bool caption = (style & 0x00C00000) != 0;
        bool covers = rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2 && rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
        var context = ScreenPolicy.Classify(false, covers, caption, IsZoomed(foreground));
        return context == ScreenContext.Desktop ? Desktop() : context;
    }
    // A window on this monitor that fills the screen — maximised, or full screen without a frame (browser F11, a
    // borderless game). It counts even while another app holds the foreground: the island is a topmost strip, so the
    // filled window is partly hidden behind it, which is what the user opened it for. Excluded: the desktop shell, tool
    // windows, our own windows, minimised windows and the ones the window manager keeps cloaked (the windows of other
    // virtual desktops are still "visible" to the API).
    private static IntPtr BackgroundFilled(IntPtr monitor, IntPtr foreground)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (window == foreground) return true;
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            if ((GetWindowLongPtr(window, -20).ToInt64() & 0x00000080) != 0) return true;              // WS_EX_TOOLWINDOW
            if (MonitorFromWindow(window, 2) != monitor) return true;
            // A maximised window counts here too. The island is a topmost strip at the top of the screen, so a window that
            // fills the screen is partly hidden behind it even when that window is not in front — the case reported for a
            // browser left maximised behind another app. Only the window the user is actually working in is excluded, by
            // being the foreground window itself.
            //
            // A maximised window fills the work area, not the monitor: with a taskbar on screen its rectangle stops above
            // the taskbar, so testing it against the monitor rectangle would never match. IsZoomed answers it directly.
            if (DwmGetWindowAttribute(window, 9, out RECT rect, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(window, out rect);
            var coversMonitor = rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2
                             && rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
            var caption = (GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000) != 0;
            // Maximised, or frameless across the whole monitor (browser F11, a borderless game, a full-screen video).
            if (!IsZoomed(window) && !(coversMonitor && !caption)) return true;
            // Asked only of a window that fills the screen, which few do.
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == Environment.ProcessId || IsShell(window)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    // The way a background window fills the screen, in the same terms the foreground context is read in, so that the
    // app's own rule can answer for it: a frameless window across the whole monitor is Borderless, a maximised one is
    // Maximized. The two tests that accept the window above are the ones deciding here, so an accepted window never
    // reads as Desktop.
    private static ScreenContext KindOf(IntPtr window, IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return ScreenContext.Desktop;
        if (DwmGetWindowAttribute(window, 9, out RECT rect, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(window, out rect);
        var coversMonitor = rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2
                         && rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
        var caption = (GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000) != 0;
        return ScreenPolicy.Classify(false, coversMonitor, caption, IsZoomed(window));
    }
    // A window's process name and executable, read once per window. Both reads can fail when the process exited, runs as
    // administrator or has another bitness; the name alone still matches a saved exception. The foreground window and the
    // one that fills the screen are both read every poll, so each keeps a reading of its own.
    private void Describe(IntPtr window, uint pid, out string processName, out string imagePath)
        => Describe(ref _identityWindow, ref _identityPid, ref _identityName, ref _identityPath, window, pid, out processName, out imagePath);
    private void DescribeBackground(IntPtr window, uint pid, out string processName, out string imagePath)
        => Describe(ref _backgroundWindow, ref _backgroundPid, ref _backgroundIdentityName, ref _backgroundIdentityPath, window, pid, out processName, out imagePath);
    private static void Describe(ref IntPtr cachedWindow, ref uint cachedPid, ref string cachedName, ref string cachedPath,
        IntPtr window, uint pid, out string processName, out string imagePath)
    {
        if (window != cachedWindow || pid != cachedPid)
        {
            cachedWindow = window; cachedPid = pid; cachedName = ""; cachedPath = "";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                cachedName = process.ProcessName;
                try { cachedPath = process.MainModule?.FileName ?? ""; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        processName = cachedName; imagePath = cachedPath;
    }
    public static void ConfigureOverlay(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // WS_EX_NOACTIVATE makes WGC CreateForWindow fail with E_INVALIDARG. Keep the
        // window capturable and reject mouse activation without swallowing the click.
        // ShowActivated=False separately prevents activation when the window is shown.
        HwndSource.FromHwnd(handle)?.AddHook(OverlayMessages);
    }
    private static IntPtr OverlayMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0021) return IntPtr.Zero; // WM_MOUSEACTIVATE
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE: deliver the click without taking focus.
    }
    public void Dispose()
    {
        _timer.Stop();
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT
    { public int Left, Top, Right, Bottom; public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    private delegate void WinEventProc(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    // ushort: a char is not blittable to DllImport, which would copy a single ANSI byte.
    [DllImport("user32", EntryPoint = "GetClassNameW")] private static extern int GetClassName(IntPtr window, ref ushort name, int max);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("shell32")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT rect, int size);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);}
