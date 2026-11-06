using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TwelveIsland.Native;

// Watches the shell for the attention signal of WeChat, QQ and the mailbox. None of them reliably posts a notice this app can
// read — WeChat's own popup window, the system's toast store and the window's accessibility tree have all proven empty on a
// current build — but each flashes its taskbar button when a message waits, and a dock (Winstep Nexus, RocketDock and the rest)
// shows the same flash on its own icon.
//
// The flash is not a state any interface exposes — the accessibility trees carry no "flashing" flag, and the taskbar's own
// elements do not reach a client through UI Automation. What the system does announce is the flash itself: a window that
// registers with RegisterShellHookWindow receives a SHELLHOOK message with the code HSHELL_REDRAW and the flashing window's
// handle each time a top-level window is flashed. That is exactly the message the docks live on — which is why their icons
// flash in step with the taskbar — so one hook covers the taskbar and every dock at once, with no sampling and no searching.
//
// A flash that stops is the message having been read: the program is in front of the user, which the foreground hook announces
// at once, or it stopped flashing on its own, which a few quiet seconds announce.
internal sealed class TaskbarWatch : IDisposable
{
    // The flashing began for a program: a message waits. The second value is what the window that announced it could say
    // about the message (a notice window's title, the sender's name), or empty when the flash said nothing at all.
    public event Action<NotifyProgram, string>? Attention;
    // The flashing stopped, or the program itself came to the front: what waited has been looked at.
    public event Action<NotifyProgram>? Settled;

    private const int HSHELL_WINDOWCREATED = 1, HSHELL_REDRAW = 6, HSHELL_FLASH = 0x8006;
    private const int EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    // A window flashes a few times a second; this long without one means the program stopped flashing.
    private const long QuietMs = 4000;
    // A flash announces itself with HSHELL_FLASH and HSHELL_REDRAW on every pulse; a title change alone sends one redraw.
    // So two redraws close together are a flash where the system sends the redraw alone.
    private const long PulseMs = 2500;

    // What one program last showed: when it flashed, the window that flashed, and whether it is still flashing. Redraws counts
    // the redraws that came without a flash code, since the last one that was more than PulseMs ago; Windows is added on the
    // first and counts when the second comes, so a single title change never reads as a message.
    private sealed class Signal { public long Last, FirstRedraw; public int Redraws; public IntPtr Window; public bool Active; }
    private readonly Dictionary<string, Signal> _signals = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _window, _foregroundHook;
    private uint _message;
    private Timer? _quiet;
    private readonly WndProcDelegate _proc;
    private readonly WinEventDelegate _foreground;

    public TaskbarWatch()
    {
        _proc = Procedure;
        _foreground = Foreground;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "TaskbarWatch" };
    }

    public void Start() { if (!_thread.IsAlive) _thread.Start(); }

    public void Dispose()
    {
        _stop.Cancel();
        try { if (_window != IntPtr.Zero) PostMessage(_window, 0x0010, IntPtr.Zero, IntPtr.Zero); } catch { } // WM_CLOSE ends the message loop
        try { _thread.Join(1500); } catch { }
    }

    // The window that last flashed for a program, so the capsule can bring it up where it is.
    public IntPtr WindowOf(string program)
    {
        lock (_gate) return _signals.TryGetValue(program, out var signal) ? signal.Window : IntPtr.Zero;
    }

    // The hook lives on its own thread with a hidden window of its own, so the panels never carry the messages.
    private void Run()
    {
        // This runs off the UI thread: an exception that escapes it ends the whole process. Several programs flashing
        // at once is exactly when the shell sends the most messages, so nothing here may be allowed to escape.
        try
        {
            const string className = "12IslandShellHook";
            // The instance handle is not used by a class that draws nothing of its own, and a single-file build has none to give.
            var identity = IntPtr.Zero;
            var class_ = new WndClass { Name = className, Procedure = Marshal.GetFunctionPointerForDelegate(_proc), Instance = identity, Cursor = LoadCursor(IntPtr.Zero, 32512) };
            RegisterClass(ref class_);
            _window = CreateWindowEx(0, className, className, 0x80000000u, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, identity, IntPtr.Zero); // WS_POPUP, never shown
            if (_window == IntPtr.Zero) return;
            _message = RegisterWindowMessage("SHELLHOOK");
            bool hooked = RegisterShellHookWindow(_window);
            Trace($"watch 0x{_window.ToInt64():X} message 0x{_message:X} hook={hooked}");
            // The program in front is the one being read, whatever its taskbar button was doing.
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foreground, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _quiet = new Timer(_ => CheckQuiet(), null, 1000, 1000);
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (_stop.IsCancellationRequested) break;
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception) { /* The watch stops; the app keeps running. */ }
        finally
        {
            try { _quiet?.Dispose(); } catch { }
            _quiet = null;
            try
            {
                if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
                if (_window != IntPtr.Zero) { DeregisterShellHookWindow(_window); DestroyWindow(_window); }
            }
            catch (Exception) { }
            _window = IntPtr.Zero;
        }
    }

    private IntPtr Procedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if ((uint)message == _message)
        {
            try { Handle((int)wParam, lParam); } catch { }
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    // The shell hook's own message: wParam is the code and lParam the window it is about (not the other way round, as the
    // older RegisterShellHook API had it). A flash arrives as HSHELL_FLASH with HSHELL_REDRAW beside it on every pulse; a
    // program that puts up a new window instead of flashing announces that as HSHELL_WINDOWCREATED.
    private void Handle(int code, IntPtr handle)
    {
        if (handle == IntPtr.Zero || _stop.IsCancellationRequested) return;
        // The first codes of a session are kept in the log, so a message that never arrives leaves a trace of the ones that do.
        if (++_seen < 40) Trace($"code=0x{code:X} hwnd=0x{handle.ToInt64():X} program={ProgramOf(handle)?.Key ?? "-"}");
        switch (code)
        {
            case HSHELL_FLASH: if (ProgramOf(handle) is { } flashing) Pulse(flashing, handle, sure: true); break;
            case HSHELL_REDRAW: if (ProgramOf(handle) is { } redrawing) Pulse(redrawing, handle, sure: false); break;
            case HSHELL_WINDOWCREATED: if (ProgramOf(handle) is { } opening) Appeared(opening, handle); break;
        }
    }

    // A window the shell redrew: a pulse of the flash, or a title change. Only a window the user is not looking at asks for
    // attention.
    private void Pulse(NotifyProgram program, IntPtr handle, bool sure)
    {
        if (GetForegroundWindow() == handle) { Settle(program, handle); return; }
        bool started = false;
        int redraws;
        lock (_gate)
        {
            if (!_signals.TryGetValue(program.Key, out var signal)) _signals[program.Key] = signal = new Signal();
            long now = Environment.TickCount64;
            if (now - signal.FirstRedraw > PulseMs) { signal.FirstRedraw = now; signal.Redraws = 0; }
            signal.Redraws++;
            redraws = signal.Redraws;
            signal.Last = now;
            signal.Window = handle;
            // A flash code is the shell saying so; a redraw on its own only once it repeats as a flash's pulses do, and a
            // window that renames itself twice in a row is not a message.
            if (!signal.Active && (sure || redraws > 2)) { signal.Active = true; started = true; }
        }
        if (started) { Note(program, "attention", handle); RaiseAttention(program, ""); }
    }

    // A window of a watched program appeared while the program was not in front: its own notice window, or the message
    // window it opens for a chat. Windows that a program keeps for itself have no title, and its main window is the one the
    // title names, so neither counts.
    private void Appeared(NotifyProgram program, IntPtr handle)
    {
        if (GetForegroundWindow() is var front && front != IntPtr.Zero)
        {
            GetWindowThreadProcessId(front, out uint pid);
            if (pid != 0 && pid == PidOf(handle)) return; // one of the program's own windows is already in front
        }
        if (!IsWindowVisible(handle)) return;
        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        string text = title.ToString().Trim();
        if (text.Length == 0 || text.Equals(program.Name, StringComparison.OrdinalIgnoreCase)
            || NotifyPrograms.FromTitle(text) is { } named && named.Key == program.Key) return;
        bool started = false;
        lock (_gate)
        {
            if (!_signals.TryGetValue(program.Key, out var signal)) _signals[program.Key] = signal = new Signal();
            if (signal.Active) { signal.Last = Environment.TickCount64; signal.Window = handle; return; }
            signal.Active = true; signal.Last = Environment.TickCount64; signal.Window = handle;
            started = true;
        }
        if (started) { Note(program, "window", handle); RaiseAttention(program, text); }
    }

    // The foreground hook: the program the user just brought up has been read. This is the only ending a mailbox has — its
    // notices come over IMAP and nothing flashes for them.
    private void Foreground(IntPtr hook, int @event, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (window == IntPtr.Zero || _stop.IsCancellationRequested) return;
        try { if (ProgramOf(window) is { } program) Settle(program, window); } catch { }
    }

    // What a program announced is read: its flashing is over, and the window that holds it is now the user's to look at.
    private void Settle(NotifyProgram program, IntPtr window)
    {
        bool was = false;
        lock (_gate)
        {
            if (!_signals.TryGetValue(program.Key, out var signal)) _signals[program.Key] = signal = new Signal();
            was = signal.Active;
            signal.Window = window;
            signal.Active = false;
            signal.Redraws = 0;
        }
        if (was) Note(program, "read", window);
        Raise(Settled, program);
    }

    // No flash for a while: the program stopped flashing, so what it announced has been read.
    private void CheckQuiet()
    {
        // A Timer callback is a background thread too: an escaping exception would end the process.
        try
        {
            if (_stop.IsCancellationRequested) return;
            List<NotifyProgram>? settled = null;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                foreach (var (key, signal) in _signals)
                {
                    // A redraw that never repeated was a title change, not a flash: the count starts again.
                    if (now - signal.FirstRedraw > PulseMs) signal.Redraws = 0;
                    if (!signal.Active || now - signal.Last < QuietMs) continue;
                    signal.Active = false;
                    if (NotifyPrograms.All.FirstOrDefault(program => program.Key == key) is { } program) (settled ??= []).Add(program);
                }
            }
            if (settled is null) return;
            foreach (var program in settled) { Note(program, "quiet", IntPtr.Zero); Raise(Settled, program); }
        }
        catch (Exception) { }
    }

    // What the watcher did, for the log beside the settings (a message that did not arrive leaves this as its only trace).
    private static void Note(NotifyProgram program, string what, IntPtr window)
        => Trace($"{what} {program.Key} {program.Name} 0x{(long)window:X}");

    private int _seen;
    private static readonly object Gate = new();
    private static void Trace(string line)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "notify.log");
            lock (Gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 128 * 1024) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private void RaiseAttention(NotifyProgram program, string hint)
    {
        try { Attention?.Invoke(program, hint); } catch { }
    }

    private static void Raise(Action<NotifyProgram>? handler, NotifyProgram program)
    {
        try { handler?.Invoke(program); } catch { }
    }

    // Which of the watched programs a window belongs to, by its process first and by its title where the name was changed.
    private static NotifyProgram? ProgramOf(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0 || pid == CurrentPid) return null;
        string process = "";
        try { process = Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        if (NotifyPrograms.FromProcess(process) is { } known) return known;
        // The process name decides, and a window title that names the program stands in for it: a renamed launcher whose
        // window is flashed still reads as the program it holds.
        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        return NotifyPrograms.FromTitle(title.ToString());
    }

    private static readonly uint CurrentPid = (uint)Environment.ProcessId;
    private static uint PidOf(IntPtr handle) { GetWindowThreadProcessId(handle, out uint pid); return pid; }
    private delegate IntPtr WndProcDelegate(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hook, int @event, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ExtraA, ExtraB;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu;   // lpszMenuName comes before lpszClassName in WNDCLASS
        public string Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WndClass @class);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint style, string className, string name, uint style2, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message payload, IntPtr window, int min, int max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message payload);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message payload);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(int min, int max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public int Id;
        public IntPtr wParam;
        public IntPtr lParam;
        public int Time;
        public POINT Point;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
