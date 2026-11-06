using System.Diagnostics;
using TwelveIsland.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TwelveIsland.Native;

// Audio is a visualization input, never a discoverable media source. Every COM
// object is created, read, and released on this single worker thread.
// The level bars prefer the player's captured audio; when process capture is not
// available they fall back to the session peak meter. Bars shown at all times read the
// whole system's sound (every process but TwelveIsland) and fall back to the devices' peak meters.
internal sealed class AudioLevelMeter : IDisposable
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly List<MMDevice> _devices = [];
    private Thread? _thread;
    private MediaAppOwner? _requested;
    private int _revision;
    private bool _disposed;
    private float _peak;
    private volatile ProcessLoopbackCapture? _capture;
    // The target that stands for the whole system; no application has this id or process name.
    private static readonly MediaAppOwner Everything = new(null, "\0", "\0");
    public float Peak => Volatile.Read(ref _peak);
    // Whether the loopback capture is running: without it the bars have no analysed sound to show in either mode.
    public bool Capturing => _capture is not null;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public void SetSource(MediaAppOwner? owner, bool enabled, bool system = false)
    {
        var target = enabled ? owner : system ? Everything : null;
        lock (_gate)
        {
            if (_disposed || _requested == target) return;
            _requested = target; _revision++;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "TwelveIsland audio level" };
                _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
            }
            _wake.Set();
        }
    }
    // Fills the bars' levels from the captured audio, in the given way: flowing along the bars, or one band of pitch for each of
    // the levels.Length bars.
    public bool TryFill(Span<double> levels, EqualizerMode mode)
    {
        if (_capture is not { } capture) return false;
        capture.Signal.Fill(levels, Now, mode);
        return true;
    }
    private void Run()
    {
        int revision = -1;
        MediaAppOwner? target = null;
        bool retried = false;
        double nextPeak = 0;
        // A player may create its audio session after it reports playback; look again with growing pauses.
        int searchDelay = 1000;
        WaitHandle[]? waits = null;
        try
        {
            while (true)
            {
                try
                {
                    bool changed;
                    lock (_gate)
                    {
                        if (_disposed) break;
                        changed = revision != _revision;
                        if (changed) { revision = _revision; target = _requested; }
                    }
                    // Starting a capture waits for the system; do it outside the lock SetSource takes on the UI thread.
                    if (changed) { Clear(); if (target is not null) Enumerate(target); retried = false; searchDelay = 1000; }
                    var capture = _capture;
                    double now = Now;
                    if (capture is not null && !capture.Drain(now)) { StopCapture(); capture = null; }
                    // Captured audio drives the bars whenever a capture runs, so session peaks are only read without one.
                    if (capture is null && now >= nextPeak)
                    {
                        nextPeak = now + .033;
                        float peak = 0;
                        try
                        {
                            if (target == Everything) foreach (var device in _devices) peak = Math.Max(peak, device.AudioMeterInformation.MasterPeakValue);
                            else foreach (var session in _sessions) peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
                        }
                        catch
                        {
                            Clear();
                            if (!retried && target is not null) { Enumerate(target); retried = true; }
                        }
                        Volatile.Write(ref _peak, peak);
                    }
                    if (capture is not null)
                    {
                        if (waits?[1] != capture.Ready) waits = [_wake, capture.Ready];
                        WaitHandle.WaitAny(waits, 20);
                    }
                    else if (_sessions.Count > 0 || target == Everything && _devices.Count > 0) _wake.WaitOne(33);
                    else if (target is null) _wake.WaitOne();
                    else if (!_wake.WaitOne(searchDelay))
                    {
                        searchDelay = Math.Min(searchDelay * 2, 30_000);
                        Clear(); Enumerate(target);
                    }
                }
                catch (Exception)
                {
                    // An unhandled exception on a background thread ends the whole process. A COM object, a capture
                    // or a device can fail between two calls, so it is contained here and the next pass starts over.
                    try { Clear(); } catch { }
                    waits = null;
                    try { _wake.WaitOne(50); } catch (ObjectDisposedException) { }
                }
            }
        }
        finally { try { Clear(); } catch { } Volatile.Write(ref _peak, 0); }
    }
    private void Enumerate(MediaAppOwner target)
    {
        if (target == Everything) { EnumerateEverything(); return; }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                var device = devices[d]; _devices.Add(device);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i]; bool keep = false;
                    try
                    {
                        if (MediaAppTarget.Plays(target, AppIdentity.Describe((int)session.GetProcessID))) { _sessions.Add(session); keep = true; }
                    }
                    catch { }
                    finally { if (!keep) session.Dispose(); }
                }
            }
        }
        catch { Clear(); return; }
        // Capture the process that owns the playing session; its child processes are included.
        var playing = _sessions.FirstOrDefault(s => s.State == AudioSessionState.AudioSessionStateActive) ?? _sessions.FirstOrDefault();
        if (playing is not null) _capture = ProcessLoopbackCapture.TryStart(playing.GetProcessID);
    }
    // Every render device, for their peak meters, and a capture of every process but this one.
    private void EnumerateEverything()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++) _devices.Add(devices[d]);
        }
        catch { Clear(); return; }
        _capture = ProcessLoopbackCapture.TryStart((uint)Environment.ProcessId, exclude: true);
    }
    private void StopCapture()
    {
        var capture = _capture; _capture = null;
        capture?.Dispose();
    }
    private void Clear()
    {
        StopCapture();
        foreach (var session in _sessions) { try { session.Dispose(); } catch { } }
        _sessions.Clear();
        foreach (var device in _devices) { try { device.AudioSessionManager.Dispose(); device.Dispose(); } catch { } }
        _devices.Clear();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // The wake handle is kept for the process lifetime. Disposing it while a worker is still winding down
            // made a later SetSource throw ObjectDisposedException on the UI thread (a crash on every re-render).
            _wake.Set();
        }
    }
}
