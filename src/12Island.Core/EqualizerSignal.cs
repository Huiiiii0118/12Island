namespace TwelveIsland.Core;

// Turns a player's audio into the levels of the level bars, in one of two ways (EqualizerMode, asked for by Fill):
//
//   Flow (the original, EqualizerSignal.Flow.cs): the sound is made into one value, and the bars show it at increasing delays, left
//   to right, so a drum beat or a voice is seen flowing along them.
//   Spectrum (EqualizerSignal.Spectrum.cs): the sound is split by pitch, and each bar is one band of it, low on the left.
//
// Both read the same samples. Only the way being shown is analysed, so Flow costs what it always did, and the Spectrum's work
// (a longer FFT, 60 narrow bands and their histories) is not done, nor is its memory taken, until it is asked for. Changing the way
// while the sound plays carries the bars over: until the new way has made its first analysis the bars are the old way's, and the
// new way then starts from where they stood.
public sealed partial class EqualizerSignal
{
    // The samples kept: the Spectrum analyses all of them, the Flow the newest FlowWindow.
    public const int WindowSize = 4096;
    private const double RangeSeconds = 3, RiseSmoothing = .08, Attack = .03, Release = .2, IdleSeconds = .15, StaleSeconds = .3;
    // What the analysis last ran as (Advance); None before the first.
    private const int None = -1;

    private readonly int _rate;
    private readonly float[] _samples = new float[WindowSize];
    // The FFT's working space, taken when an analysis first needs it.
    private double[]? _real, _imaginary;
    private readonly object _gate = new();
    private int _write, _running = None;
    // What Fill last asked for, read by the analysis thread: the way and the number of bars.
    private int _mode, _requested;
    private double _last = double.NaN, _lastInput = double.NaN;

    public EqualizerSignal(int sampleRate)
    {
        _rate = sampleRate;
        Array.Fill(_flowSmoothed, double.NaN);
        Array.Fill(_smoothed, double.NaN);
    }

    // Interleaved samples scaled to -1..1; channels are averaged.
    public void Push(ReadOnlySpan<float> interleaved, int channels, double now)
    {
        for (int i = 0; i + channels <= interleaved.Length; i += channels)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i + c];
            _samples[_write] = sum / channels; _write = (_write + 1) % WindowSize;
        }
        if (interleaved.Length > 0) _lastInput = now;
    }

    // Analyse the most recent window and move the bars to time now (seconds), in the way Fill last asked for.
    public void Advance(double now)
    {
        double dt = double.IsNaN(_last) ? 1d / 60 : Math.Clamp(now - _last, .001, .1);
        _last = now;
        if ((EqualizerMode)Volatile.Read(ref _mode) == EqualizerMode.Spectrum) AdvanceSpectrum(now, dt); else AdvanceFlow(now, dt);
    }

    // The bars' levels, 0 to 1 and leftmost first, for levels.Length bars, in the given way. The analysis then goes on making that
    // way, for that many bars. Flow is the default, the way before there was a choice.
    public void Fill(Span<double> levels, double now, EqualizerMode mode = EqualizerMode.Flow)
    {
        Volatile.Write(ref _requested, Math.Min(levels.Length, MaxBars)); Volatile.Write(ref _mode, (int)mode);
        if (mode == EqualizerMode.Spectrum) FillSpectrum(levels, now); else FillFlow(levels, now);
    }

    public void Reset()
    {
        Array.Clear(_samples); _write = 0; _last = _lastInput = double.NaN;
        foreach (var range in _flowRanges) range.Clear();
        foreach (var ordered in _flowOrdered) ordered.Clear();
        Array.Fill(_flowSmoothed, double.NaN); Array.Clear(_flowRises);
        _frameOldest = _frameCount = 0;
        _bars = 0;
        Array.Fill(_smoothed, double.NaN); Array.Clear(_state);
        lock (_gate) { _running = None; _head = 0; _history.Clear(); _publishedBars = 0; _publishedAt = double.NaN; Array.Clear(_published); }
    }

    private void EnsureFft(int size)
    {
        if (_real is not null && _real.Length >= size) return;
        _real = new double[size]; _imaginary = new double[size];
    }

    // An in-place FFT of the first n points; cos and sin are the twiddle factors e^(-2πik/n) for k below n/2, taken once instead of
    // carried by a recurrence in every butterfly.
    private static void Fft(double[] re, double[] im, int n, double[] cos, double[] sin)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            // The stage's twiddle for j is the window's for j * (n / length).
            int stride = n / length;
            for (int i = 0; i < n; i += length)
            {
                for (int j = 0; j < length / 2; j++)
                {
                    int a = i + j, b = a + length / 2;
                    double cr = cos[j * stride], ci = sin[j * stride];
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                }
            }
        }
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];
    private static void Insert(List<double> sorted, double value)
    {
        int at = sorted.BinarySearch(value);
        sorted.Insert(at < 0 ? ~at : at, value);
    }
    private static void Remove(List<double> sorted, double value)
    {
        int at = sorted.BinarySearch(value);
        if (at >= 0) sorted.RemoveAt(at);
    }
}
