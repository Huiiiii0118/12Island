namespace TwelveIsland.Core;

// The Spectrum way: the bars, left to right, are low to high pitch, so a kick drum lifts the left bars, a cymbal the right ones,
// and a single tone only the bars around its pitch.
//
// One analysis serves any number of bars from 1 to MaxBars (the collapsed and the expanded equalizer are set to different
// lengths). A window of SpectrumWindow samples goes through one FFT; its power is gathered into Bands narrow bands spaced evenly on a
// log axis between LowHz and the top of the audible range; and the bars are made from those narrow bands, as many bars as the
// panel draws, each an equal share of the log axis (BarRange). The narrow bands of the last RangeSeconds are kept, so the bars can
// be made again for another count without starting over: a bar's history, and so its scale, is rebuilt from them (Rebuild).
//
// Each bar is scaled against its own last RangeSeconds (so the treble is not always smaller than the bass, and a wide bar does not
// sit higher than a narrow one just for holding more bins) and against the loudest bar of the moment (so a steady tone keeps
// standing instead of being scaled down to nothing), with the sharpness of a recent rise added on top.
public sealed partial class EqualizerSignal
{
    // 4096 samples are 93 ms at 44.1 kHz and 10.8 Hz to a bin: the lowest bars of a long equalizer (20 bars: 40 to 54 Hz, 54 to
    // 72 Hz) lie in a bin or two. 2048 samples put several of them into the same bin.
    private const int SpectrumWindow = WindowSize;
    public const int Bands = 60;
    public const int MaxBars = Bands;
    public const int DefaultBars = 5;
    public const double LowHz = 40, HighHz = 16000;
    // A full-scale sine puts about 60 dB in its bin with this window. A bar whose share of the narrow bands averages less than
    // this (a paused player's dither, a band with nothing in it) is silent and does not move, whatever it was scaled against.
    private const double SpectrumSilenceDb = -11;
    // Samples all below this (one least significant bit of 16-bit audio) are digital silence or dither: no analysis is made.
    private const double QuietPeak = 4e-5;
    // How far below the loudest bar a bar still counts for the absolute part of its level, in dB.
    private const double AbsoluteRange = 60;
    private const int HistoryCapacity = 200;

    // Made when the Spectrum is first used.
    private static class SpectrumTables
    {
        public static readonly double[] Hann = Enumerable.Range(0, SpectrumWindow).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (SpectrumWindow - 1))).ToArray();
        public static readonly double[] Cos = Enumerable.Range(0, SpectrumWindow / 2).Select(k => Math.Cos(-2 * Math.PI * k / SpectrumWindow)).ToArray();
        public static readonly double[] Sin = Enumerable.Range(0, SpectrumWindow / 2).Select(k => Math.Sin(-2 * Math.PI * k / SpectrumWindow)).ToArray();
    }

    // Which bins make each narrow band, and how much of each bin's cell lies in it; made when the Spectrum first runs.
    private readonly int[] _first = new int[Bands], _start = new int[Bands + 1];
    private float[]? _weights;
    private readonly float[] _power = new float[Bands];
    // The narrow bands' power of the last RangeSeconds, newest last, with each analysis's time and the time since the one before.
    private readonly float[][] _frames = new float[HistoryCapacity][];
    private readonly double[] _frameTime = new double[HistoryCapacity], _frameStep = new double[HistoryCapacity];
    private int _frameOldest, _frameCount;
    // The bars as made for _bars of them: each bar's last RangeSeconds of levels in order and in time, kept as levels come and go
    // for its percentiles (sorting them all again on every analysis was most of the cost), and the levels the bars stand at.
    private readonly List<double>?[] _ordered = new List<double>?[MaxBars];
    private readonly Queue<(double Time, double Db)>?[] _ranges = new Queue<(double, double)>?[MaxBars];
    private readonly double[] _db = new double[MaxBars], _smoothed = new double[MaxBars], _state = new double[MaxBars];
    private readonly double[] _published = new double[MaxBars];
    private int _bars, _publishedBars;
    private double _publishedAt = double.NaN;

    private double SpectrumRate => Math.Max(8000, _rate);

    // The highest frequency the bars show: 16 kHz, or less when the sample rate leaves no room above.
    public static double TopHz(int sampleRate) => Math.Min(HighHz, Math.Max(sampleRate, 8000) * .45);

    private static double Edge(double top, int index, int count) => LowHz * Math.Pow(top / LowHz, index / (double)count);

    // The frequencies bar index of bars covers, in Hz: the bars split LowHz to TopHz into equal shares of the log axis, so each
    // bar starts where the one before ends, whatever the count.
    public static (double Low, double High) BarRange(int bars, int index, int sampleRate)
    {
        bars = Math.Clamp(bars, 1, MaxBars); index = Math.Clamp(index, 0, bars - 1);
        double top = TopHz(sampleRate);
        return (Edge(top, index, bars), Edge(top, index + 1, bars));
    }

    private void EnsureBands()
    {
        if (_weights is not null) return;
        double top = TopHz(_rate), delta = SpectrumRate / SpectrumWindow;
        var weights = new List<float>();
        for (int band = 0; band < Bands; band++)
        {
            // In bin units; bin k stands for the frequencies from k - .5 to k + .5.
            double low = Edge(top, band, Bands) / delta, high = Edge(top, band + 1, Bands) / delta;
            int first = Math.Max(1, (int)Math.Floor(low + .5)), last = Math.Min(SpectrumWindow / 2 - 1, (int)Math.Floor(high + .5));
            _first[band] = first; _start[band] = weights.Count;
            for (int k = first; k <= last; k++) weights.Add((float)Math.Max(0, Math.Min(high, k + .5) - Math.Max(low, k - .5)));
        }
        _start[Bands] = weights.Count; _weights = [.. weights];
    }

    // Moves the bars to time now, for as many bars as Fill last asked for.
    private void AdvanceSpectrum(double now, double dt)
    {
        EnsureBands();
        int requested = Volatile.Read(ref _requested), bars = requested <= 0 ? DefaultBars : Math.Min(requested, MaxBars);
        // Starting, or starting again after Flow: the bars are made afresh from the narrow bands kept, and when the bars were showing
        // the flow they start from what they showed, so they go on from that picture instead of from rest.
        bool entering = _running != (int)EqualizerMode.Spectrum, fromFlow = _running == (int)EqualizerMode.Flow;
        if (entering) _bars = 0;
        if (bars != _bars) Rebuild(bars, now);
        if (fromFlow) for (int i = 0; i < _bars; i++) _state[i] = Sample(now - i * BarDelay);
        // Without input for a while the player has gone quiet, and nothing but silence needs no analysis at all: let the bars fall.
        bool heard = !double.IsNaN(_lastInput) && now - _lastInput < IdleSeconds && Analyse(now, dt);
        if (!heard) Fall(dt);
        lock (_gate)
        {
            Array.Copy(_state, _published, _bars); _publishedBars = _bars; _publishedAt = now; _running = (int)EqualizerMode.Spectrum;
        }
    }

    // A count that has just changed is answered from the bars as they stood until the next analysis, so no frame shows them at rest;
    // so is the first look after changing from Flow, from the flow's bars. Levels from an analysis older than a moment are not shown.
    private void FillSpectrum(Span<double> levels, double now)
    {
        lock (_gate)
        {
            if (_running == (int)EqualizerMode.Flow)
            {
                levels.Clear();
                for (int i = 0, count = Math.Min(levels.Length, MaxBars); i < count; i++) levels[i] = Sample(now - i * BarDelay);
                return;
            }
            FillPublished(levels, now);
        }
    }

    // The levels Advance last made, as many as asked for; to be called with the gate held.
    private void FillPublished(Span<double> levels, double now)
    {
        int count = Math.Min(levels.Length, MaxBars);
        levels.Clear();
        if (count == 0 || _publishedBars == 0 || double.IsNaN(_publishedAt) || now - _publishedAt > StaleSeconds) return;
        if (_publishedBars == count) _published.AsSpan(0, count).CopyTo(levels);
        else Resample(_published.AsSpan(0, _publishedBars), levels[..count]);
    }

    // The levels of from stretched or squeezed along the pitch axis to fill to.
    public static void Resample(ReadOnlySpan<double> from, Span<double> to)
    {
        if (from.Length == 0) { to.Clear(); return; }
        for (int i = 0; i < to.Length; i++)
        {
            double at = Math.Clamp((i + .5) * from.Length / to.Length - .5, 0, from.Length - 1);
            int low = (int)at, high = Math.Min(from.Length - 1, low + 1);
            to[i] = from[low] + (from[high] - from[low]) * (at - low);
        }
    }

    // Makes the bars for a new count the way they would be had they been that many all along: from the narrow bands kept.
    // Only frames still within RangeSeconds are used, and when the newest is older than IdleSeconds (the player has gone quiet) the bars
    // start at rest, as Fall had left them: a count that changes in silence must not bring back the level of the last sound.
    private void Rebuild(int bars, double now)
    {
        _bars = bars;
        for (int i = 0; i < bars; i++)
        {
            (_ordered[i] ??= new List<double>(128)).Clear(); (_ranges[i] ??= new Queue<(double, double)>()).Clear();
            _smoothed[i] = double.NaN; _state[i] = 0;
        }
        for (int n = 0; n < _frameCount; n++)
        {
            int slot = (_frameOldest + n) % HistoryCapacity;
            if (now - _frameTime[slot] > RangeSeconds) continue;
            Step(_frames[slot], _frameTime[slot], _frameStep[slot]);
        }
        if (_frameCount == 0 || now - _frameTime[(_frameOldest + _frameCount - 1) % HistoryCapacity] >= IdleSeconds) Array.Clear(_state, 0, bars);
    }

    // False when the window holds nothing to analyse.
    private bool Analyse(double now, double dt)
    {
        int tail = WindowSize - _write;
        bool loud = false;
        for (int i = 0; i < WindowSize; i++) if (Math.Abs(_samples[i]) >= QuietPeak) { loud = true; break; }
        if (!loud) return false;
        EnsureFft(SpectrumWindow);
        var real = _real!; var imaginary = _imaginary!; var hann = SpectrumTables.Hann; var weights = _weights!;
        // The oldest sample first, the window applied as it is copied.
        for (int i = 0; i < tail; i++) { real[i] = _samples[_write + i] * hann[i]; imaginary[i] = 0; }
        for (int i = tail; i < WindowSize; i++) { real[i] = _samples[i - tail] * hann[i]; imaginary[i] = 0; }
        Fft(real, imaginary, SpectrumWindow, SpectrumTables.Cos, SpectrumTables.Sin);
        for (int band = 0; band < Bands; band++)
        {
            double power = 0;
            int weight = _start[band], bin = _first[band];
            for (int end = _start[band + 1]; weight < end; weight++, bin++) power += weights[weight] * (real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            _power[band] = (float)power;
        }
        // Frames of the last RangeSeconds are kept for making the bars again.
        while (_frameCount > 0 && (now - _frameTime[_frameOldest] > RangeSeconds || _frameCount == HistoryCapacity)) { _frameOldest = (_frameOldest + 1) % HistoryCapacity; _frameCount--; }
        int slot = (_frameOldest + _frameCount) % HistoryCapacity;
        _frames[slot] ??= new float[Bands];
        _power.CopyTo(_frames[slot], 0); _frameTime[slot] = now; _frameStep[slot] = dt; _frameCount++;
        Step(_frames[slot], now, dt);
        return true;
    }

    // One analysis's narrow bands (power by band) moved into the bars.
    private void Step(float[] power, double now, double dt)
    {
        int bars = _bars;
        double share = Bands / (double)bars, loudest = double.MinValue;
        for (int i = 0; i < bars; i++)
        {
            // The bar takes the part of each narrow band it overlaps, and its mean power per narrow band is what is measured, so a
            // bar of three narrow bands and one of twenty are held to the same silence.
            double start = i * share, end = start + share, sum = 0;
            for (int band = (int)start; band < Bands && band < end; band++) sum += power[band] * (Math.Min(end, band + 1) - Math.Max(start, band));
            _db[i] = Math.Max(SpectrumSilenceDb, 10 * Math.Log10(1e-12 + sum / share));
            loudest = Math.Max(loudest, _db[i]);
        }
        for (int i = 0; i < bars; i++)
        {
            double db = _db[i];
            var range = _ranges[i]!; var ordered = _ordered[i]!;
            range.Enqueue((now, db)); Insert(ordered, db);
            while (now - range.Peek().Time > RangeSeconds) Remove(ordered, range.Dequeue().Db);
            double floor = Percentile(ordered, .25), ceiling = Math.Max(Percentile(ordered, .98), floor + 15);
            double relative = Math.Pow(Math.Clamp((db - floor) / (ceiling - floor), 0, 1), 1.5);
            double absolute = Math.Clamp((db - (loudest - AbsoluteRange)) / AbsoluteRange, 0, 1);
            double level = Math.Max(relative, .9 * absolute * absolute);
            if (double.IsNaN(_smoothed[i])) _smoothed[i] = db;
            double rise = Math.Clamp((db - _smoothed[i]) / 6, 0, 1);
            _smoothed[i] += (db - _smoothed[i]) * (1 - Math.Exp(-dt / RiseSmoothing));
            double target = db <= SpectrumSilenceDb ? 0 : Math.Clamp(.05 + .6 * level + .35 * rise, 0, 1);
            _state[i] += (target - _state[i]) * (1 - Math.Exp(-dt / (target > _state[i] ? Attack : Release)));
            if (_state[i] < .002 && target == 0) _state[i] = 0;
        }
    }

    private void Fall(double dt)
    {
        double keep = Math.Exp(-dt / Release);
        for (int i = 0; i < _bars; i++) { _state[i] = _state[i] < .002 ? 0 : _state[i] * keep; _smoothed[i] = double.NaN; }
    }
}
