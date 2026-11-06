namespace TwelveIsland.Core;

// The Flow way: the single value that flows across the level bars.
// The value combines how loud each of five frequency bands is relative to its own
// last three seconds with how sharply any band just rose, so a piano note counts as
// much as a kick drum. The bars show it at increasing delays, left to right.
// This is the analysis the equalizer had before the Spectrum was added, as it was.
public sealed partial class EqualizerSignal
{
    // The newest this many samples of the window are analysed.
    private const int FlowWindow = 2048;
    public const double BarDelay = .075;
    private const double HistorySeconds = 2;
    // A full-scale sine concentrates about 54 dB in its band with this window; anything 60 dB
    // below that is treated as silence, so a paused player's dither never gets scaled up.
    private const double FlowSilenceDb = -6;
    // The history reaches back past the last of 20 bars (19 BarDelay), so every bar has a value.
    private static readonly double[] Edges = [50, 150, 500, 1500, 4500, 14000];

    // Made when Flow is first used.
    private static class FlowTables
    {
        public static readonly double[] Hann = Enumerable.Range(0, FlowWindow).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (FlowWindow - 1))).ToArray();
        public static readonly double[] Cos = Enumerable.Range(0, FlowWindow / 2).Select(k => Math.Cos(-2 * Math.PI * k / FlowWindow)).ToArray();
        public static readonly double[] Sin = Enumerable.Range(0, FlowWindow / 2).Select(k => Math.Sin(-2 * Math.PI * k / FlowWindow)).ToArray();
    }

    private readonly Queue<(double Time, double Db)>[] _flowRanges = Enumerable.Range(0, Edges.Length - 1).Select(_ => new Queue<(double, double)>()).ToArray();
    private readonly double[] _flowSmoothed = new double[Edges.Length - 1], _flowRises = new double[Edges.Length - 1];
    private readonly List<(double Time, double Value)> _history = [];
    // Each band's last RangeSeconds of levels in order, kept as levels come and go, for its percentiles: sorting them all again
    // on every analysis was most of its cost.
    private readonly List<double>[] _flowOrdered = Enumerable.Range(0, Edges.Length - 1).Select(_ => new List<double>(128)).ToArray();
    private double _head;

    public double Head { get { lock (_gate) return _head; } }

    private void AdvanceFlow(double now, double dt)
    {
        if (_running != (int)EqualizerMode.Flow) EnterFlow(now);
        // Without input for a while, the player has gone quiet: let the bars fall.
        double target = !double.IsNaN(_lastInput) && now - _lastInput < IdleSeconds ? FlowTarget(now, dt) : 0;
        lock (_gate)
        {
            _head += (target - _head) * (1 - Math.Exp(-dt / (target > _head ? Attack : Release)));
            _history.Add((now, _head));
            int stale = 0;
            while (stale < _history.Count && now - _history[stale].Time > HistorySeconds) stale++;
            if (stale > 0) _history.RemoveRange(0, stale);
            _running = (int)EqualizerMode.Flow;
        }
    }

    // Flow starts, or starts again after the Spectrum. When the bars were showing the Spectrum, the flow is made to stand where they
    // stood: the first bar's level is the flow's value now and the later bars' levels are what the flow had at the delays they read
    // it at, so the bars go on from the picture they had instead of from rest.
    private void EnterFlow(double now)
    {
        lock (_gate)
        {
            if (_running != (int)EqualizerMode.Spectrum) return;
            _history.Clear(); _head = 0;
            if (_publishedBars > 0 && now - _publishedAt <= StaleSeconds)
            {
                _head = _published[0];
                for (int i = _publishedBars - 1; i >= 1; i--) _history.Add((now - i * BarDelay, _published[i]));
            }
        }
        // What the bands last did is out of date; a rise measured against it would be a jump.
        Array.Fill(_flowSmoothed, double.NaN); Array.Clear(_flowRises);
    }

    // The flowing value at an earlier analysis time, interpolated between analyses.
    public double Sample(double at)
    {
        lock (_gate)
        {
            if (_history.Count == 0 || at < _history[0].Time) return 0;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Time > at) continue;
                if (i + 1 == _history.Count) return _history[i].Value;
                var (t0, v0) = _history[i]; var (t1, v1) = _history[i + 1];
                return t1 == t0 ? v0 : v0 + (v1 - v0) * (at - t0) / (t1 - t0);
            }
            return 0;
        }
    }

    private void FillFlow(Span<double> levels, double now)
    {
        // Just changed from the Spectrum, before the flow's first analysis: the bars as the Spectrum had them.
        if (Volatile.Read(ref _running) == (int)EqualizerMode.Spectrum)
        {
            lock (_gate)
            {
                if (_running == (int)EqualizerMode.Spectrum) { FillPublished(levels, now); return; }
            }
        }
        for (int i = 0; i < levels.Length; i++) levels[i] = Sample(now - i * BarDelay);
    }

    private double FlowTarget(double now, double dt)
    {
        EnsureFft(FlowWindow);
        var real = _real!; var imaginary = _imaginary!;
        for (int i = 0; i < FlowWindow; i++) { real[i] = _samples[(_write + (WindowSize - FlowWindow) + i) % WindowSize] * FlowTables.Hann[i]; imaginary[i] = 0; }
        Fft(real, imaginary, FlowWindow, FlowTables.Cos, FlowTables.Sin);
        double level = 0;
        for (int b = 0; b < Edges.Length - 1; b++)
        {
            int k0 = (int)(Edges[b] * FlowWindow / _rate), k1 = Math.Min(FlowWindow / 2, Math.Max(k0 + 1, (int)(Edges[b + 1] * FlowWindow / _rate)));
            double power = 0;
            for (int k = k0; k < k1; k++) power += real[k] * real[k] + imaginary[k] * imaginary[k];
            double db = Math.Max(FlowSilenceDb, 10 * Math.Log10(1e-12 + power));
            var range = _flowRanges[b]; var ordered = _flowOrdered[b];
            range.Enqueue((now, db)); Insert(ordered, db);
            while (now - range.Peek().Time > RangeSeconds) Remove(ordered, range.Dequeue().Db);
            double floor = Percentile(ordered, .25), ceiling = Math.Max(Percentile(ordered, .98), floor + 15);
            level += Math.Pow(Math.Clamp((db - floor) / (ceiling - floor), 0, 1), 1.5) / (Edges.Length - 1);
            if (double.IsNaN(_flowSmoothed[b])) _flowSmoothed[b] = db;
            _flowRises[b] = Math.Clamp((db - _flowSmoothed[b]) / 6, 0, 1);
            _flowSmoothed[b] += (db - _flowSmoothed[b]) * (1 - Math.Exp(-dt / RiseSmoothing));
        }
        // The two clearest onsets, so one noisy band cannot drive the bars alone.
        double first = 0, second = 0;
        foreach (var rise in _flowRises) { if (rise > first) { second = first; first = rise; } else if (rise > second) second = rise; }
        return Math.Clamp(.05 + .45 * level + .45 * (first + second) / 2, 0, 1);
    }
}
