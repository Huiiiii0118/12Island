using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// Whether some window on the desktop is being moved or resized by the system's move loop: dragging a title bar or an edge,
// in any process, TwelveIsland's settings window included. Windows reports the start and the end as WinEvents, which arrive on
// the thread that created this object, so it must have a message loop.
internal sealed class WindowDrags : IDisposable
{
    private const uint MoveSizeStart = 0x000A, MoveSizeEnd = 0x000B, OutOfContext = 0;
    // Held in a field so the collector keeps the delegate the hook calls.
    private readonly WinEventProc _callback;
    private nint _hook;
    public bool Active { get; private set; }

    public WindowDrags()
    {
        _callback = (_, kind, _, _, _, _, _) => Active = kind == MoveSizeStart;
        _hook = SetWinEventHook(MoveSizeStart, MoveSizeEnd, 0, _callback, 0, 0, OutOfContext);
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWinEvent(_hook); _hook = 0; Active = false;
    }

    private delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(nint hook);
}
