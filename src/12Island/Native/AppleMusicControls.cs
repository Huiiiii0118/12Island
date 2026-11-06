using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Shuffle, repeat and seeking for Apple Music, whose media session reports all three as unavailable. They go through the
// shuffle button, the repeat button and the progress bar of its main window, found by their automation ids, so they work
// only while that window exists and may stop working when Apple Music changes its interface. The bar counts whole
// seconds; writing it moves playback (measured: 157 to 167). UI Automation calls into another process and can take a
// while, so every call runs on the thread pool, one at a time. All three sit in the window's transport bar (17 elements
// against about 450 for the whole window), which is looked up once and kept, so the rest of the window is not walked
// again on every reading; while it is not found it is looked for again at most every 10 seconds.
internal sealed class AppleMusicControls
{
    public sealed record State(bool Shuffle, MediaRepeat Repeat, bool CanSeek);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppleMusicRepeat _repeatNames = new();
    private static readonly TimeSpan SearchEvery = TimeSpan.FromSeconds(10);
    private AutomationElement? _bar, _shuffle, _repeat, _scrubber;
    private DateTimeOffset _readAt, _searchedAt;
    private int _reading;
    // The window's last reading; null while no Apple Music window with these controls was found.
    public State? Latest { get; private set; }
    // Raised on a pool thread when a reading differs from the previous one.
    public event Action? Changed;

    // Reads the window again, at most once a second unless now is set.
    public void Refresh(bool now = false)
    {
        var at = DateTimeOffset.UtcNow;
        if (!now && at - _readAt < TimeSpan.FromSeconds(1) || Interlocked.Exchange(ref _reading, 1) == 1) return;
        _readAt = at;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                var state = Read();
                if (state != Latest) { Latest = state; Changed?.Invoke(); }
            }
            finally { _gate.Release(); Volatile.Write(ref _reading, 0); }
        });
    }

    public Task<bool> ToggleShuffleAsync() => RunAsync(() => ((TogglePattern)_shuffle!.GetCurrentPattern(TogglePattern.Pattern)).Toggle());
    public Task<bool> CycleRepeatAsync() => RunAsync(() => ((InvokePattern)_repeat!.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    public Task<bool> SeekAsync(TimeSpan position) => RunAsync(() =>
    {
        if (_scrubber is null) throw new InvalidOperationException();
        var range = (RangeValuePattern)_scrubber.GetCurrentPattern(RangeValuePattern.Pattern);
        range.SetValue(Math.Clamp(Math.Round(position.TotalSeconds), range.Current.Minimum, range.Current.Maximum));
    });

    private async Task<bool> RunAsync(Action action)
    {
        await _gate.WaitAsync();
        try
        {
            // A control that is gone (its window was rebuilt) is looked up again and the action tried once more.
            return await Task.Run(() =>
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { if (!Find(now: true)) return false; action(); return true; }
                    catch (Exception ex) when (Lost(ex)) { Forget(); }
                }
                return false;
            });
        }
        finally
        {
            _gate.Release();
            // Apple Music redraws the button a moment after the click (about half a second in testing).
            _ = Task.Delay(400).ContinueWith(_ => Refresh(now: true), TaskScheduler.Default);
        }
    }
    private State? Read()
    {
        try
        {
            if (!Find()) return null;
            bool shuffle = ((TogglePattern)_shuffle!.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
            string glyph = TreeWalker.RawViewWalker.GetFirstChild(_repeat!)?.Current.Name ?? "";
            var repeat = _repeatNames.Observe(glyph, _repeat!.Current.Name) ?? MediaRepeat.None;
            return new(shuffle, repeat, _scrubber is not null);
        }
        catch (Exception ex) when (Lost(ex)) { Forget(); return null; }
    }
    // The controls in the transport bar of the window that has one; elements of a closed window throw on use and are looked
    // up again. Without now, a bar not found is looked for again only after SearchEvery. A progress bar not found yet is
    // looked for again within the bar: the window has none while no song is loaded.
    private bool Find(bool now = false)
    {
        if (_bar is null || _shuffle is null || _repeat is null)
        {
            var at = DateTimeOffset.UtcNow;
            if (!now && at - _searchedAt < SearchEvery) return false;
            _searchedAt = at;
            if (TransportBar() is not { } bar || ById(bar, "ShuffleButton") is not { } shuffle || ById(bar, "RepeatButton") is not { } repeat) return false;
            _bar = bar; _shuffle = shuffle; _repeat = repeat;
        }
        _scrubber ??= ById(_bar, "LCDScrubber");
        return true;
    }
    private static AutomationElement? TransportBar()
    {
        foreach (var process in Process.GetProcessesByName("AppleMusic"))
            using (process)
                foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)))
                    if (ById(window, "TransportBar") is { } bar) return bar;
        return null;
    }
    private static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    // A lost element is looked up again at once; only a lookup that finds nothing waits for SearchEvery.
    private void Forget() { _bar = _shuffle = _repeat = _scrubber = null; _searchedAt = default; }
    private static bool Lost(Exception ex) => ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException;
}
