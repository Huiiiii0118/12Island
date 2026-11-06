using System.Windows.Threading;

namespace TwelveIsland;

// A repeating timer for anything the user watches or waits on, used like DispatcherTimer. DispatcherTimer rides on
// WM_TIMER, which Windows generates only when the message queue is otherwise empty; while the cursor moved, its ticks
// here arrived up to 3.5 s late. This timer counts on the thread pool and delivers each tick as an ordinary posted
// dispatcher operation. Low-frequency work that can run a little late stays on DispatcherTimer.
internal sealed class PromptTimer
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _tick;
    private Timer? _timer;
    private TimeSpan _interval;
    private int _generation, _pending;

    public PromptTimer(Dispatcher dispatcher, Action tick) { _dispatcher = dispatcher; _tick = tick; }
    public bool IsEnabled { get; private set; }
    // Changing the interval of a running timer keeps it running; the new interval counts from now.
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (_interval == value) return;
            _interval = value;
            if (IsEnabled) _timer?.Change(Period, Period);
        }
    }
    private TimeSpan Period => _interval > TimeSpan.Zero ? _interval : TimeSpan.FromMilliseconds(1);

    public void Start()
    {
        Stop();
        int generation = _generation; IsEnabled = true;
        _timer = new Timer(_ =>
        {
            // At most one tick waits on the dispatcher; a busy UI thread gets one catch-up tick, not a backlog.
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _pending = 0;
                // A later Start or Stop supersedes this tick even if it was already posted.
                if (generation == _generation && IsEnabled) _tick();
            });
        }, null, Period, Period);
    }

    public void Stop()
    {
        _generation++; IsEnabled = false;
        _timer?.Dispose(); _timer = null;
    }
}
