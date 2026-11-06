using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TwelveIsland;

// Gives memory back while TwelveIsland sits collapsed, unless the user turns it off (DeckSettings.TrimMemoryWhenIdle, on by default).
// Reading the usage logs, building a page and loading covers allocate in bursts; afterwards the garbage collector keeps the room
// it grew into, and Windows keeps the pages in the working set, so an idle panel that had peaked at over 200 MB stayed there. Once every panel has been collapsed and left alone for a while, the heap is
// compacted, the room it no longer needs is returned, and the working set is trimmed. Never while a panel is open or a surface
// is being resized, and at most every few minutes: what is trimmed comes back from memory on the next use, which must not
// fall on an opening panel's first frames.
public partial class App
{
    private static readonly TimeSpan TrimIdle = TimeSpan.FromSeconds(45), TrimEvery = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _trim;
    private DateTime _trimmedAt = DateTime.MinValue;

    // Called after a burst of work (a usage refresh), whenever a panel collapses and when the settings window closes: the trim
    // waits for a quiet stretch after it.
    internal void RequestTrim()
    {
        if (Exiting || !Settings.TrimMemoryWhenIdle) { _trim?.Stop(); return; }
        _trim ??= CreateTrimTimer();
        _trim.Stop();
        var wait = _trimmedAt + TrimEvery - DateTime.UtcNow;
        _trim.Interval = wait > TrimIdle ? wait : TrimIdle;
        _trim.Start();
    }

    private DispatcherTimer CreateTrimTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Exiting || !Settings.TrimMemoryWhenIdle) return;
            // Someone is using a panel: try again after the next quiet stretch.
            if (Decks.Any(deck => deck.IsExpanded || deck.IsResizing)) { RequestTrim(); return; }
            _trimmedAt = DateTime.UtcNow;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Aggressive also returns the memory the heap no longer uses to the system.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        };
        return timer;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
