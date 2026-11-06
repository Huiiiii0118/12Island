using System.Globalization;
using System.Windows;
using TwelveIsland.Core;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TwelveIsland;

internal static class MusicVisuals
{
    internal static FrameworkElement Icon(string name, double size, Brush? brush = null)
    {
        var data = name switch
        {
            "play" => "M 7,3.5 Q 5,2.5 5,5 V 19 Q 5,21.5 7,20.5 L 20,13 Q 22,12 20,11 Z",
            "pause" => "M 5,3 H 9 Q 10,3 10,4 V 20 Q 10,21 9,21 H 5 Q 4,21 4,20 V 4 Q 4,3 5,3 Z M 15,3 H 19 Q 20,3 20,4 V 20 Q 20,21 19,21 H 15 Q 14,21 14,20 V 4 Q 14,3 15,3 Z",
            "previous" => "M 3,4 H 5 V 20 H 3 Z M 20,4 Q 21,3.4 21,5 V 19 Q 21,20.6 20,20 L 7,13 Q 5.5,12 7,11 Z",
            "next" => "M 19,4 H 21 V 20 H 19 Z M 4,4 Q 3,3.4 3,5 V 19 Q 3,20.6 4,20 L 17,13 Q 18.5,12 17,11 Z",
            "shuffle" => "M 3,6 H 6 C 10,6 14,18 18,18 H 21 M 17,14 L 21,18 L 17,22 M 3,18 H 6 C 10,18 14,6 18,6 H 21 M 17,2 L 21,6 L 17,10",
            "repeat" => "M 4,10 V 8 Q 4,5 7,5 H 20 M 16,1 L 20,5 L 16,9 M 20,14 V 16 Q 20,19 17,19 H 4 M 8,15 L 4,19 L 8,23",
            "sequential" => "M 3,5.1 H 16 V 3 L 21,6 L 16,9 V 6.9 H 3 Z M 3,17.1 H 16 V 15 L 21,18 L 16,21 V 18.9 H 3 Z",
            "like" or "liked" => "M 12,20.5 C 12,20.5 3,15 3,8.8 C 3,6 5.2,3.8 7.9,3.8 C 9.6,3.8 11.1,4.7 12,6.1 C 12.9,4.7 14.4,3.8 16.1,3.8 C 18.8,3.8 21,6 21,8.8 C 21,15 12,20.5 12,20.5 Z",
            _ => "M 9,18 V 5.5 L 20,3.3 V 16 M 9,18 A 3,3 0 1 1 3,18 A 3,3 0 1 1 9,18 M 20,16 A 3,3 0 1 1 14,16 A 3,3 0 1 1 20,16"
        };
        bool filled = name is "play" or "pause" or "previous" or "next" or "sequential" or "liked";
        var path = new Path { Data = Geometry.Parse(data), Fill = filled ? brush ?? UI.Brush(UI.Primary) : null,
            Stroke = filled ? null : brush ?? UI.Brush(UI.Secondary), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
    internal static Border Cover(BitmapSource? image, double size, double radius, bool outlined = false)
    {
        double corner = PanelTheme.Corner(radius);
        var content = new Grid { Background = UI.Brush(PanelTheme.Hex("coverPlaceholder")) };
        if (image is not null) content.Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill });
        else content.Children.Add(Icon("music", size * .34));
        if (outlined) content.Children.Add(new Rectangle { Stroke = UI.Brush(PanelTheme.Hex("coverOutline")), StrokeThickness = 1, RadiusX = corner, RadiusY = corner, Margin = new Thickness(.5), IsHitTestVisible = false });
        content.Clip = new RectangleGeometry(new Rect(0, 0, size, size), corner, corner);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(corner), Child = content };
    }
    // The color taken from the cover, or the theme's equalizer color when there is no cover or it gives none.
    internal static Brush Accent(BitmapSource? source) => Accent(source, out _);
    internal static Brush Accent(BitmapSource? source, out Color? cover)
    {
        cover = null;
        if (source is null) return UI.Brush(PanelTheme.Hex("equalizer"));
        try
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(16d / source.PixelWidth, 16d / source.PixelHeight));
            var bitmap = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[16 * 16 * 4]; bitmap.CopyPixels(pixels, 64, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                double min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                double w = Math.Max(.12, (max - min) / 255d) * pixels[i + 3] / 255d;
                b += pixels[i] * w; g += pixels[i + 1] * w; r += pixels[i + 2] * w; weight += w;
            }
            if (weight == 0) return UI.Brush(PanelTheme.Hex("equalizer"));
            r /= weight * 255; g /= weight * 255; b /= weight * 255;
            double maxChannel = Math.Max(r, Math.Max(g, b)), minChannel = Math.Min(r, Math.Min(g, b));
            double light = (maxChannel + minChannel) / 2, delta = maxChannel - minChannel;
            double saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * light - 1));
            double hue = delta == 0 ? 0 : maxChannel == r ? ((g - b) / delta + (g < b ? 6 : 0)) / 6
                : maxChannel == g ? ((b - r) / delta + 2) / 6 : ((r - g) / delta + 4) / 6;
            bool dark = PanelTheme.Current.IsDark;
            saturation = Math.Max(.35, saturation); light = dark ? Math.Clamp(light, .62, .78) : Math.Clamp(light, .30, .42);
            double chroma = (1 - Math.Abs(2 * light - 1)) * saturation, x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1)), m = light - chroma / 2;
            (r, g, b) = (hue * 6) switch
            { < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x), < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x) };
            r += m; g += m; b += m;
            var accent = PanelTheme.Readable(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
            var brush = new SolidColorBrush(accent); brush.Freeze(); cover = accent; return brush;
        }
        catch (Exception) { return UI.Brush(PanelTheme.Hex("equalizer")); }
    }
    // The state of a move's clock as the scrolling regions plan their turns with it (CycleTurn).
    internal static MoveClock MoveClockOf(Clock clock) => clock.CurrentState switch
    {
        ClockState.Active => MoveClock.Active,
        ClockState.Filling => MoveClock.Filling,
        _ => clock.CurrentTime is null ? MoveClock.Waiting : MoveClock.None
    };
    // Coordinates from the element's own width. A relative brush spans everything the element draws,
    // including rings scrolled out of view, so its fades would follow the content instead of the edges.
    internal static Brush Fade(double width, double edge)
        => new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, Math.Min(.5, edge / Math.Max(1, width))), new(Colors.White, Math.Max(.5, 1 - edge / Math.Max(1, width))), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(width, 0))
        { MappingMode = BrushMappingMode.Absolute };
}

// The bars change every frame. They are redrawn into a retained drawing, so a frame costs
// only rendering; InvalidateVisual would also run layout and re-hit-test the whole window.
internal sealed class MusicEqualizer : FrameworkElement
{
    public Brush Color { get; set; } = UI.Brush(UI.Secondary);
    public bool Playing { get; set; }
    public float Peak { get; set; }
    public int Bars { get; init; } = 5;
    // Bar width in DIPs; the collapsed row scales it with its height.
    public double BarWidth { get; init; } = 3;
    private readonly (double Time, double Peak)[] _history = new (double, double)[64];
    private readonly double[] _levels = new double[CompactMusicLayout.MaximumBars];
    private readonly DrawingGroup _drawing = new();
    private Size _drawnSize;
    private int _next, _count;
    private double _time;
    public void Tick(float peak, bool playing, double seconds = 1d / 30)
    {
        Peak = float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0; Playing = playing;
        if (!playing)
        {
            Array.Clear(_levels); _next = _count = 0; _time = 0;
            Redraw(); return;
        }
        seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, .001, .5) : 1d / 30;
        _time += seconds;
        _history[_next] = (_time, Peak); _next = (_next + 1) % _history.Length;
        _count = Math.Min(_count + 1, _history.Length);
        double low = Peak, high = Peak;
        for (int i = 0; i < _count; i++)
        {
            var sample = Previous(i);
            if (_time - sample.Time > 1.6) break;
            low = Math.Min(low, sample.Peak); high = Math.Max(high, sample.Peak);
        }
        // Expand the recent musical dynamics instead of compressing absolute
        // volume with a square root. A minimum range prevents noise amplification.
        double range = Math.Max(.0025, Math.Max(high * .25, high - low));
        for (int i = 0; i < _levels.Length; i++)
        {
            double sample = SampleAt(_time - i * .075);
            double contrast = Math.Clamp((sample - low) / range, 0, 1);
            double target = Math.Clamp(sample / .0015, 0, 1) * (.1 + .9 * Math.Pow(contrast, .8));
            double response = 1 - Math.Exp(-seconds / (target > _levels[i] ? .035 : .085));
            _levels[i] += (target - _levels[i]) * response;
        }
        Redraw();
    }
    // Bar values already computed from captured audio, leftmost first.
    public void Show(ReadOnlySpan<double> levels, bool playing)
    {
        Playing = playing;
        for (int i = 0; i < _levels.Length; i++) _levels[i] = playing && i < levels.Length ? Math.Clamp(levels[i], 0, 1) : 0;
        Redraw();
    }
    private (double Time, double Peak) Previous(int offset) => _history[(_next - 1 - offset + _history.Length) % _history.Length];
    private double SampleAt(double time)
    {
        var newer = Previous(0);
        for (int i = 0; i < _count; i++)
        {
            var older = Previous(i);
            if (older.Time <= time)
                return newer.Time == older.Time ? older.Peak : older.Peak + (newer.Peak - older.Peak) * (time - older.Time) / (newer.Time - older.Time);
            newer = older;
        }
        return 0;
    }
    protected override void OnRender(DrawingContext dc) { Redraw(force: true); dc.DrawDrawing(_drawing); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); _bars = null; InvalidateVisual(); }
    // The bars stand on half device pixels (EqualizerLayout): a level that moves none of them draws nothing new, and one that
    // does only moves its own rectangle in the retained drawing.
    private RectangleGeometry[]? _bars;
    private Brush? _barsColor;
    private void Redraw(bool force = false)
    {
        var size = RenderSize;
        int count = Math.Min(Bars, _levels.Length);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip, radius = BarWidth / 2;
        bool rebuild = force || _bars is null || size != _drawnSize || !ReferenceEquals(_barsColor, Color);
        if (rebuild)
        {
            _bars = new RectangleGeometry[count]; _barsColor = Color; _drawnSize = size;
            using var dc = _drawing.Open();
            for (int i = 0; i < count; i++)
            {
                var (x, y, w, h) = EqualizerLayout.Bar(i, Bars, _levels[i], size.Width, size.Height, BarWidth, dpi);
                _bars[i] = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
                dc.DrawGeometry(Color, null, _bars[i]);
            }
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var (x, y, w, h) = EqualizerLayout.Bar(i, Bars, _levels[i], size.Width, size.Height, BarWidth, dpi);
            var rect = new Rect(x, y, w, h);
            if (_bars![i].Rect != rect) _bars[i].Rect = rect;
        }
    }
}

internal sealed class MusicMarquee : FrameworkElement
{
    private static readonly Typeface Plain = new(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _typeface = Plain;
    // Width of the edge fade, and the scroll speed in DIP per second.
    internal const double Edge = 6, Speed = 24;
    private readonly TranslateTransform _shift = new();
    // The ink fade's brush moves against the text, so its fades stay at the element's edges while the text glides.
    private readonly TranslateTransform _unshift = new();
    // An edge fades only while text is hidden beyond it: none on the left before the text moves,
    // none on the right once its end is in view. The first and last characters rest unfaded.
    private readonly GradientStop _left = new(Colors.White, 0), _right = new(Colors.White, 1);
    // The same fade for the tail's own color (InkFade): its stops follow the text's clocks and offsets.
    private readonly GradientStop _tailLeft = new(Colors.White, 0), _tailRight = new(Colors.White, 1);
    private string _text = "";
    private double _fontSize = 11.5, _overflow, _fadeWidth = -1;
    private FormattedText? _formatted;
    // The scroll's cycle (MarqueeCycle), null until the text first scrolls. The rests are waited out on a timer (_turn) and a
    // clock runs only while the text glides (_glide): one clock repeating the whole cycle ticked at 60 frames a second through
    // the rests too, for as long as a song played. _since is when the scroll last went on (Eased.Now), NaN while it is held;
    // _elapsed is how long the cycle had then run in all, not wrapped, so its laps can be counted (MarqueeCycle.Done).
    private MarqueeCycle? _scroll;
    private PromptTimer? _turn;
    private ClockGroup? _glide;
    private CycleStretch? _glideMove;
    private double _since = double.NaN, _elapsed;
    private bool _scrolling, _running, _hovered, _rested;
    // With Laps set (the collapsed row's title), the text scrolls that many whole cycles and then rests at its start until the
    // text changes or Replay is called; Rested starts it there. Without (the open page's lyric), it scrolls for as long as it may.
    public bool Laps { get; init; }
    public bool Rested { get => _rested; init => _rested = value; }
    // Raised once the laps are done, so the window can keep the text resting when the row is built again.
    public event Action? Finished;
    // Raised when the glide's clock starts and when it lets go: while it runs, WPF draws a frame every refresh anyway.
    public event Action<bool>? Gliding;
    public bool IsGliding => _glide is not null;
    public MusicMarquee()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => { _hovered = false; ResetAnimation(); };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        MouseEnter += (_, _) => { _hovered = true; UpdateAnimation(); };
        MouseLeave += (_, _) => { _hovered = false; UpdateAnimation(); };
    }
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; ResetText(); AutomationProperties.SetName(this, value); }
    }
    // For screen readers: the whole line as text, however much of it shows. The open page's title reads with its own name
    // ("Title ..." and the artist merged into the line); every other line reads as it is drawn.
    public string AccessibleName { get; init; } = "";
    internal string Reading => AccessibleName.Length > 0 ? AccessibleName : _text + Tail;
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => Reading);
    public double FontSize
    {
        get => _fontSize;
        set { if (_fontSize == value) return; _fontSize = value; ResetText(); }
    }
    // The open page's title is semibold; the collapsed row and the lyrics are regular.
    public FontWeight FontWeight { init => _typeface = new Typeface(UI.PanelFont, FontStyles.Normal, value, FontStretches.Normal); }
    // A second run after the text in its own size and color: the merged title of a short page ("title · artist"). It scrolls with
    // the text as one line. Over a gradient or scan lines (InkFade) it fades at its ends like the text, in its own color, so it
    // is the same color whether the line fits or scrolls.
    public string Tail { get; init; } = "";
    public double TailFontSize { get; init; }
    public Brush? TailForeground { get; init; }
    // Whether the text overhangs the room it was last drawn in: the window shows the whole title in a tooltip then.
    public bool Overflows => _overflow > 0;
    public bool CenterWhenFits { get; init; } = true;
    // The text that fits is set against the box's right edge: the collapsed row's title or lyric stands right before the
    // rhythm, the two read as one.
    public bool RightWhenFits { get; init; }
    // The edge fade's width; the collapsed row scales it with its height. The scroll speed does not change.
    public double EdgeWidth { get; init; } = Edge;
    // False when the region is too narrow for a meaningful stretch of text: the text then rests at its start, cut at the edge.
    public bool ScrollAllowed { get; init; } = true;
    public Brush Foreground { get; init; } = UI.Brush(UI.Primary);
    // With a backdrop, the text is drawn by a child that re-enables ClearType below this element's clip, and the edge fades are
    // drawn over the text in the backdrop color instead of an opacity mask. A clip or a mask on the drawing element forces grayscale.
    public Color? Backdrop
    {
        get => _backdrop;
        init
        {
            _backdrop = value;
            if (value is not { } color) return;
            _left.Color = _right.Color = Color.FromArgb(0, color.R, color.G, color.B);
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, ClearTypeHint.Enabled);
            AddVisualChild(_glyphs); ClipToBounds = true;
        }
    }
    private readonly Color? _backdrop;
    // Over a surface with a gradient or scan lines no single color can paint the fades, so the text itself fades: its brush is
    // the foreground with transparent ends. The text is still drawn by the child, with no opacity mask and no intermediate
    // layer, which a mask would need on every frame of the scroll.
    private readonly bool _inkFade;
    public bool InkFade
    {
        get => _inkFade;
        init
        {
            if (!value || _glyphs is not null) return;
            _inkFade = true;
            _glyphs = new Glyphs(this); RenderOptions.SetClearTypeHint(_glyphs, ClearTypeHint.Enabled);
            AddVisualChild(_glyphs); ClipToBounds = true;
        }
    }
    private Brush? _ink;
    private readonly Glyphs? _glyphs;
    private Brush? _fade;
    private UIElement Painter => _glyphs ?? (UIElement)this;
    private sealed class Glyphs(MusicMarquee owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.Draw(dc, RenderSize);
    }
    protected override int VisualChildrenCount => _glyphs is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _glyphs ?? throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size availableSize) { _glyphs?.Measure(availableSize); return default; }
    protected override Size ArrangeOverride(Size finalSize) { _glyphs?.Arrange(new Rect(finalSize)); return finalSize; }
    private Brush? _tailInk;
    private FormattedText Formatted => _formatted ??= Format();
    private FormattedText Format()
    {
        var text = new FormattedText(Text + Tail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, FontSize, _ink ?? Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (Tail.Length > 0)
        {
            text.SetFontWeight(FontWeights.Normal, Text.Length, Tail.Length);
            if (TailFontSize > 0) text.SetFontSize(TailFontSize, Text.Length, Tail.Length);
            if (_ink is not null && _tailInk is not null) text.SetForegroundBrush(_tailInk, Text.Length, Tail.Length);
            else if (_ink is null && TailForeground is not null) text.SetForegroundBrush(TailForeground, Text.Length, Tail.Length);
        }
        return text;
    }
    public void SetScrolling(bool enabled) { _scrolling = enabled; UpdateAnimation(); }
    private void ResetText() { _formatted = null; _rested = false; ResetAnimation(); Painter.InvalidateVisual(); }
    // Scrolls the laps again from the start (the panel closed and the row shows again).
    public void Replay()
    {
        if (!_rested) return;
        _rested = false; ResetAnimation(); UpdateAnimation();
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); ResetText(); }
    private double EdgeStop => _fadeWidth > 0 ? Math.Min(.5, EdgeWidth / _fadeWidth) : 0;
    private void ResetAnimation()
    {
        _turn?.Stop(); ReleaseGlide(); ShowGliding();
        _scroll = null; _running = false; _since = double.NaN; _elapsed = 0;
        _shift.X = 0; _unshift.X = 0; _left.Offset = _tailLeft.Offset = 0; _right.Offset = _tailRight.Offset = 1 - EdgeStop;
    }
    private void UpdateAnimation()
    {
        bool running = _scrolling && ScrollAllowed && IsLoaded && IsVisible && !_hovered && !_rested && _overflow > 0 && _formatted is not null;
        // Rest, glide to the end, rest, start again (MarqueeCycle).
        if (_scroll is null && running) { _scroll = new MarqueeCycle(_overflow, Speed, EdgeWidth, EdgeStop); _elapsed = 0; }
        if (_scroll is not null && _running != running)
        {
            // Held, the text stays where the cycle stood, with no clock or timer left behind.
            double now = Eased.Now;
            if (running) _since = now;
            else { _elapsed = Position(now); _since = double.NaN; }
            _running = running;
            Turn();
        }
        // Scrolling text takes the animated quality, which follows fractional offsets smoothly; otherwise WPF chooses, so resting
        // text gets the static quality and the island's scaling still counts as motion. The mode is a composition flag and does not
        // draw the text again.
        TextOptions.SetTextHintingMode(Painter, _running ? TextHintingMode.Animated : TextHintingMode.Auto);
    }

    // How long the cycle has run at a time, in seconds from its first start (not wrapped).
    private double At(double now) => _scroll is null ? 0 : double.IsNaN(_since) ? _elapsed : _elapsed + now - _since;
    // The same, taken from the glide's clock while it runs: that is where the text is on screen (CycleTurn.Where).
    private double Position(double now)
    {
        double at = At(now);
        if (_scroll is not { } cycle || _glide is null || _glideMove is not { } move) return at;
        double within = cycle.Wrap(at);
        return at - within + CycleTurn.Where(within, move, MusicVisuals.MoveClockOf(_glide), _glide.CurrentTime?.TotalSeconds ?? double.NaN);
    }

    // Shows where the cycle stands and, while it runs, waits for its next turn: a rest shows its still values with no clock
    // running, and just before it ends the glide starts on a clock of its own (CycleTurn.Lead), which lets go at the next turn.
    private void Turn() { TurnClock(); ShowGliding(); }
    private void TurnClock()
    {
        if (_scroll is not { } cycle) { _turn?.Stop(); return; }
        // The time counted here follows the glide's clock, so the two never drift apart.
        double now = Eased.Now, played = Position(now), at = cycle.Wrap(played);
        _elapsed = played; if (_running) _since = now;
        _turn ??= new PromptTimer(Dispatcher, Turn);
        if (_running && _glide is not null && _glideMove is { } gliding && CycleTurn.Holds(MusicVisuals.MoveClockOf(_glide), at, gliding))
        {
            // Woken while the clock still glides: it finishes on its own, and the turn comes back for its end.
            _turn.Interval = TimeSpan.FromSeconds(gliding.End - at + .005); _turn.Start();
            return;
        }
        ReleaseGlide();
        // Its laps done, the text rests at its start with no clock or timer left.
        bool done = Laps && _running && cycle.Done(played);
        if (done) at = cycle.Played(played);
        _shift.X = cycle.Shift(at); _unshift.X = cycle.Unshift(at); _left.Offset = _tailLeft.Offset = cycle.Left(at); _right.Offset = _tailRight.Offset = cycle.Right(at);
        if (done)
        {
            _rested = true; _running = false; _since = double.NaN; _elapsed = 0; _turn.Stop();
            TextOptions.SetTextHintingMode(Painter, TextHintingMode.Auto);
            Finished?.Invoke();
            return;
        }
        if (!_running) { _turn?.Stop(); return; }
        var stretch = cycle.StretchAt(at);
        var turn = CycleTurn.Plan(stretch, cycle.StretchAt(stretch.End), at);
        if (turn.Move is { } move) { Glide(cycle, -turn.Into); _glideMove = move; }
        // A hair past the turn; a timer that still lands short of it only waits out what is left.
        _turn.Interval = TimeSpan.FromSeconds(turn.Wait + .005);
        _turn.Start();
    }

    // The glide from the start to the end of the text, beginning in `delay` seconds (negative: that far into it), on WPF's render
    // cadence: a background DispatcherTimer can be delayed by input and must not drive text motion. Each track holds its last
    // value until the next turn takes it off; the keyframes are the cycle's own between its two rests.
    private void Glide(MarqueeCycle cycle, double delay)
    {
        double travel = cycle.Travel, ramp = cycle.Ramp, edge = cycle.EdgeStop;
        static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        var duration = TimeSpan.FromSeconds(travel);
        DoubleAnimationUsingKeyFrames Track(params DoubleKeyFrame[] frames)
        { var track = new DoubleAnimationUsingKeyFrames { Duration = duration }; foreach (var frame in frames) track.KeyFrames.Add(frame); return track; }
        var glide = new ParallelTimeline { Duration = duration, BeginTime = TimeSpan.FromSeconds(delay), FillBehavior = FillBehavior.HoldEnd };
        glide.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new LinearDoubleKeyFrame(-cycle.Overflow, At(travel))));
        glide.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new LinearDoubleKeyFrame(edge, At(ramp)), new DiscreteDoubleKeyFrame(edge, At(travel))));
        glide.Children.Add(Track(new DiscreteDoubleKeyFrame(1 - edge, At(0)), new DiscreteDoubleKeyFrame(1 - edge, At(travel - ramp)), new LinearDoubleKeyFrame(1, At(travel))));
        glide.Children.Add(Track(new DiscreteDoubleKeyFrame(0, At(0)), new LinearDoubleKeyFrame(cycle.Overflow, At(travel))));
        _glide = (ClockGroup)glide.CreateClock(true);
        _shift.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)_glide.Children[0]);
        _left.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_glide.Children[1]);
        _right.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_glide.Children[2]);
        // The tail's fade shares the clocks (a clock may drive several stops).
        _tailLeft.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_glide.Children[1]);
        _tailRight.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_glide.Children[2]);
        _unshift.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)_glide.Children[3]);
    }
    private void ReleaseGlide()
    {
        if (_glide is null) return;
        _glide.Controller?.Remove(); _glide = null; _glideMove = null;
        _shift.ApplyAnimationClock(TranslateTransform.XProperty, null);
        _unshift.ApplyAnimationClock(TranslateTransform.XProperty, null);
        _left.ApplyAnimationClock(GradientStop.OffsetProperty, null);
        _right.ApplyAnimationClock(GradientStop.OffsetProperty, null);
        _tailLeft.ApplyAnimationClock(GradientStop.OffsetProperty, null);
        _tailRight.ApplyAnimationClock(GradientStop.OffsetProperty, null);
    }
    // Gliding is raised once a turn has settled, and only when whether a clock glides has changed: a turn that lets one clock go
    // and starts the next (a new room for the text) raised it twice, and the bars' timer was stopped and started again for it.
    private bool _glidingShown;
    private void ShowGliding()
    {
        bool gliding = _glide is not null;
        if (gliding == _glidingShown) return;
        _glidingShown = gliding;
        Gliding?.Invoke(gliding);
    }
    protected override void OnRender(DrawingContext dc) { if (_glyphs is null) Draw(dc, RenderSize); }
    // The size comes from the element that draws: the child renders during this element's arrange, before its own size is set.
    private void Draw(DrawingContext dc, Size size)
    {
        if (size.Width <= 0) return;
        var text = Formatted;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Snap(double value) => Math.Round(value * scale) / scale;
        // Whole device pixels for the scroll distance, the centred start and the baseline, so resting glyphs are not split across
        // pixels at 150 %. The small allowance keeps rounding noise from starting a one-pixel scroll.
        double overflow = MusicTitleFit.Overflow(text.Width, size.Width, scale);
        double fadeWidth = overflow > 0 ? size.Width : 0;
        if (_fadeWidth != fadeWidth)
        {
            _fadeWidth = fadeWidth;
            if (_inkFade)
            {
                // The text's own brush fades at the ends (see InkFade).
                var ink = (Foreground as SolidColorBrush)?.Color ?? Colors.White;
                _left.Color = _right.Color = ink;
                var clear = Color.FromArgb(0, ink.R, ink.G, ink.B);
                _ink = fadeWidth > 0 ? new LinearGradientBrush(new GradientStopCollection { new(clear, 0), _left, _right, new(clear, 1) },
                    new Point(0, 0), new Point(fadeWidth, 0)) { MappingMode = BrushMappingMode.Absolute, Transform = _unshift } : null;
                // The tail fades in its own color (TailForeground, as it is drawn when the line fits); its stops take the same offsets.
                var tailColor = (TailForeground as SolidColorBrush)?.Color ?? ink;
                _tailLeft.Color = _tailRight.Color = tailColor;
                var tailClear = Color.FromArgb(0, tailColor.R, tailColor.G, tailColor.B);
                _tailInk = fadeWidth > 0 && Tail.Length > 0 ? new LinearGradientBrush(new GradientStopCollection { new(tailClear, 0), _tailLeft, _tailRight, new(tailClear, 1) },
                    new Point(0, 0), new Point(fadeWidth, 0)) { MappingMode = BrushMappingMode.Absolute, Transform = _unshift } : null;
                _formatted = null; text = Formatted;
            }
            else
            {
                // Coordinates from this element's own width, like MusicVisuals.Fade.
                var edge = _backdrop ?? Colors.Transparent;
                var fade = fadeWidth > 0 ? new LinearGradientBrush(new GradientStopCollection { new(edge, 0), _left, _right, new(edge, 1) },
                    new Point(0, 0), new Point(fadeWidth, 0)) { MappingMode = BrushMappingMode.Absolute } : null;
                if (_glyphs is null) OpacityMask = fade; else _fade = fade;
            }
        }
        if (_overflow != overflow) { _overflow = overflow; ResetAnimation(); }
        // Cover the whole box, as the carousel's background does, so the mask layer's edge is not the text's edge.
        if (overflow > 0) dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (_glyphs is null) dc.PushClip(new RectangleGeometry(new Rect(size)));
        dc.PushTransform(_shift);
        double x = overflow > 0 ? 0 : RightWhenFits ? Snap(size.Width - text.Width) : CenterWhenFits ? Snap((size.Width - text.Width) / 2) : 0;
        dc.DrawText(text, new Point(x, Snap((size.Height - text.Height) / 2 + text.Baseline) - text.Baseline));
        dc.Pop();
        if (_glyphs is null) dc.Pop(); else if (_fade is not null) dc.DrawRectangle(_fade, null, new Rect(size));
        UpdateAnimation();
    }
}

// A region whose content is wider than it scrolls like the lyric marquee: rest at the start, glide until the end is in
// view, rest there, then fade back to the start and begin again. The quota rings and the system readings use it.
internal abstract class GlideCarousel : Grid
{
    internal const double StartHold = GlideCycle.StartHold, EndHold = GlideCycle.EndHold, Speed = GlideCycle.Speed, Fade = GlideCycle.Fade, FadeEdge = 3;
    private FrameworkElement? _strip;
    // The cycle (GlideCycle), null while there is no travel. The rests are waited out on a timer (_turn) and an animation clock
    // runs only while the strip moves (_move: the glide, or the fade back to the start): a storyboard repeating forever kept
    // WPF's animation clock ticking at 60 frames a second through the rests too, and so did its discrete keyframes with
    // animations off. _since is when the cycle last went on (Eased.Now), NaN while it is held; _elapsed is where it stood then.
    private GlideCycle? _cycle;
    private PromptTimer? _turn;
    private ClockGroup? _move;
    private CycleStretch? _moving;
    private bool _discrete, _enabled, _paused;
    private double _since = double.NaN;
    private TimeSpan _elapsed;
    public bool Rotates { get; protected set; }
    public double Travel { get; private set; }
    // Position within the cycle; the window hands it to the next carousel so a rebuild continues where this one was.
    public TimeSpan Elapsed => _cycle is not null ? TimeSpan.FromSeconds(Position(Eased.Now)) : _elapsed;

    // The direction the strip moves in: left, or up for the collapsed column (ColumnCarousel).
    private DependencyProperty _axis = TranslateTransform.XProperty;

    // Starts the cycle that moves the strip (with a TranslateTransform) left by travel and back; up when vertical.
    protected void Glide(FrameworkElement strip, double travel, TimeSpan elapsed, bool animate, bool vertical = false)
    {
        _strip = strip; _discrete = !animate; _elapsed = elapsed;
        _axis = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Update();
        IsVisibleChanged += (_, _) => Update();
        Retarget(travel);
    }

    // Sets the travel; a cycle already running carries on from the same time. No travel leaves it at rest.
    protected void Retarget(double travel)
    {
        if (_cycle is not null)
        {
            // The new cycle carries on from the same time (where the strip is on screen); a running one counts on from now. A move
            // under way is taken up again with the new travel.
            double now = Eased.Now;
            _elapsed = TimeSpan.FromSeconds(Position(now));
            if (!double.IsNaN(_since)) _since = now;
            Release();
        }
        Travel = Math.Max(0, travel);
        if (Travel <= 0)
        {
            _cycle = null; _turn?.Stop(); _since = double.NaN;
            if (_strip?.RenderTransform is TranslateTransform shift) shift.SetValue(_axis, 0d);
            if (_strip is not null) _strip.Opacity = 1;
            return;
        }
        var cycle = new GlideCycle(Travel, _discrete);
        _cycle = cycle;
        _elapsed = TimeSpan.FromSeconds(cycle.Wrap(_elapsed.TotalSeconds));
        bool running = !double.IsNaN(_since);
        Update();
        // Update turns only when it starts or holds the cycle; one that runs on takes the new travel here.
        if (running && !double.IsNaN(_since)) Turn();
    }
    public void SetRunning(bool enabled, bool paused) { _enabled = enabled; _paused = paused; Update(); }

    // Where the cycle stands at a time, in seconds from its start.
    private double At(double now) => _cycle is { } cycle ? cycle.Wrap(double.IsNaN(_since) ? _elapsed.TotalSeconds : _elapsed.TotalSeconds + now - _since) : 0;
    // The same, taken from the move's clock while it runs: that is where the strip is on screen (CycleTurn.Where).
    private double Position(double now)
    {
        double at = At(now);
        if (_cycle is not { } cycle || _move is null || _moving is not { } move) return at;
        return cycle.Wrap(CycleTurn.Where(at, move, MusicVisuals.MoveClockOf(_move), _move.CurrentTime?.TotalSeconds ?? double.NaN));
    }

    // Runs while it is loaded, visible, enabled and not paused; held, it still shows where the cycle stands (a carousel built in
    // place of one that was past its glide), with no clock or timer left behind.
    private void Update()
    {
        if (_cycle is null) return;
        bool run = IsLoaded && IsVisible && _enabled && !_paused;
        if (run == !double.IsNaN(_since)) { if (!run) Turn(); return; }
        double now = Eased.Now;
        if (run) _since = now;
        else { _elapsed = TimeSpan.FromSeconds(Position(now)); _since = double.NaN; }
        Turn();
    }

    // Shows where the cycle stands and, while it runs, waits for its next turn: a rest shows its still values with no clock
    // running, and just before it ends the next move (the glide, or the fade back to the start) starts on a clock of its own
    // (CycleTurn.Lead), which lets go at the next turn. Without animations every stretch is a rest, and the strip only jumps.
    private void Turn()
    {
        if (_cycle is not { } cycle || _strip?.RenderTransform is not TranslateTransform shift) { _turn?.Stop(); return; }
        // The time counted here follows the move's clock, so the two never drift apart.
        double now = Eased.Now, at = Position(now);
        bool running = !double.IsNaN(_since);
        _elapsed = TimeSpan.FromSeconds(at); if (running) _since = now;
        _turn ??= new PromptTimer(Dispatcher, Turn);
        if (running && _move is not null && _moving is { } moving && CycleTurn.Holds(MusicVisuals.MoveClockOf(_move), at, moving))
        {
            // Woken while the clock still moves the strip: it finishes on its own, and the turn comes back for its end.
            _turn.Interval = TimeSpan.FromSeconds(moving.End - at + .005); _turn.Start();
            return;
        }
        Release();
        shift.SetValue(_axis, cycle.Shift(at)); _strip.Opacity = cycle.Opacity(at);
        if (!running) { _turn.Stop(); return; }
        var stretch = cycle.StretchAt(at);
        var turn = CycleTurn.Plan(stretch, cycle.StretchAt(stretch.End), at);
        if (turn.Move is { } move) Move(cycle, shift, move, -turn.Into);
        // A hair past the turn; a timer that still lands short of it only waits out what is left.
        _turn.Interval = TimeSpan.FromSeconds(turn.Wait + .005);
        _turn.Start();
    }

    // One move of the cycle, beginning in `delay` seconds (negative: that far into it), with the keyframes the cycle had there.
    // The glide eases from the start to the end; the reset fades out, jumps back to the start and fades in, so it never reads as
    // a backwards scroll. Each holds its last value until the next turn takes it off.
    private void Move(GlideCycle cycle, TranslateTransform shift, CycleStretch move, double delay)
    {
        static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        var length = TimeSpan.FromSeconds(move.End - move.Start);
        var group = new ParallelTimeline { BeginTime = TimeSpan.FromSeconds(delay), Duration = length, FillBehavior = FillBehavior.HoldEnd };
        bool glide = move.Start < cycle.GlideEnd;
        if (glide)
        {
            var slide = new DoubleAnimationUsingKeyFrames { Duration = length };
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(0)));
            slide.KeyFrames.Add(new SplineDoubleKeyFrame(-Travel, At(move.End - move.Start),
                new KeySpline(GlideCycle.CurveX1, GlideCycle.CurveY1, GlideCycle.CurveX2, GlideCycle.CurveY2)));
            group.Children.Add(slide);
        }
        else
        {
            var back = new DoubleAnimationUsingKeyFrames { Duration = length };
            back.KeyFrames.Add(new DiscreteDoubleKeyFrame(-Travel, At(0)));
            back.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(Fade)));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = length };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, At(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, At(Fade)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, At(2 * Fade)));
            group.Children.Add(back); group.Children.Add(fade);
        }
        _move = (ClockGroup)group.CreateClock(true); _moving = move;
        shift.ApplyAnimationClock(_axis, (AnimationClock)_move.Children[0]);
        if (!glide) _strip!.ApplyAnimationClock(OpacityProperty, (AnimationClock)_move.Children[1]);
    }

    // Takes the move's clock off the strip; the strip keeps the still values set after it.
    private void Release()
    {
        if (_move is null) return;
        _move.Controller?.Remove(); _move = null; _moving = null;
        if (_strip?.RenderTransform is TranslateTransform shift) shift.ApplyAnimationClock(_axis, null);
        _strip?.ApplyAnimationClock(OpacityProperty, null);
    }
}

// Rings that do not all fit scroll through a window of whole rings, a ring step at a time.
internal sealed class QuotaCarousel : GlideCarousel
{
    private readonly Canvas _strip;
    // For screen readers: its name, every ring's tooltip line, rotating or not.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => AutomationProperties.GetName(this).Replace("\n", Loc.T("；", "; ")));
    // One ring with its application's letter inside, at the size the system readings' dials carry theirs for this height, in the secondary text color.
    internal static Grid Ring((double Used, string Color, string Tip, string Letter) item, double size, double thickness, double scale)
    {
        var ring = UI.Ring(item.Used, item.Color, size, thickness);
        // Empty without the setting (DeckSettings.QuotaRingLetters): the ring is then as it was without letters.
        if (item.Letter.Length > 0
            && RingLetter.Create(item.Letter, Math.Max(1, TwelveIsland.Core.CompactMusicLayout.Half(7 * scale)), size, Math.Max(1, size - 2 * thickness),
                Application.Current?.MainWindow is { } window ? VisualTreeHelper.GetDpi(window).PixelsPerDip : 1, UI.PanelFont, UI.Brush(UI.Secondary)) is { } letter)
            ring.Children.Add(letter);
        return ring;
    }

    // rowScale: the row's scale, which sizes the letter inside each ring (as the system readings' dials size theirs).
    // count: the whole rings the window holds (CompactSpacing.Window); gap: the row's gap, which the rings keep between them too.
    // flowing: the window as wide as the room gives it, not whole rings (the surface is being dragged; CompactSpacing.ShareFlowing),
    // with the ring it cuts faded over cut DIPs; nothing glides then.
    public QuotaCarousel(IReadOnlyList<(double Used, string Color, string Tip, string Letter)> items, double size, double thickness, double rowScale, double gap, int count, double edge,
        TimeSpan elapsed = default, bool animate = true, double? flowing = null, double cut = 0)
    {
        // Ring positions, the window and the travel all use whole device pixels. Layout rounding would otherwise widen each gap
        // at 150 % and carry the last ring into the edge fade.
        double scale = VisualTreeHelper.GetDpi(Application.Current?.MainWindow ?? (Visual)this).DpiScaleX;
        double Snap(double value) => Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
        double step = Snap(size + gap);
        int shown = Math.Clamp(count, 0, items.Count);
        double window = flowing is { } flow ? Math.Min(Math.Max(0, flow), items.Count * step - Snap(gap)) : shown == 0 ? 0 : (shown - 1) * step + size;
        Rotates = flowing is null && shown >= 1 && shown < items.Count;
        bool cutting = flowing is not null && cut > 0;
        // The fades lie outside the window, over the gaps beside it, so every ring in the window is whole at rest.
        edge = Rotates || cutting ? Math.Min(Snap(edge), Snap(gap)) : 0;
        Width = edge + window + edge; Height = size; ClipToBounds = true;
        _strip = new Canvas { Width = items.Count * step, Height = size, RenderTransform = new TranslateTransform() };
        for (int i = 0; i < items.Count; i++)
        {
            var ring = Ring(items[i], size, thickness, rowScale);
            Canvas.SetLeft(ring, edge + i * step); _strip.Children.Add(ring);
        }
        // A Canvas measures the strip at its full width. Inside the Grid the strip would be
        // layout-clipped to the visible width, and that clip moves with the translation,
        // so rings scrolled in from the right would render blank.
        var host = new Canvas(); host.Children.Add(_strip); Children.Add(host);
        ToolTip = string.Join("\n", items.Select(i => i.Tip));
        AutomationProperties.SetName(this, Loc.T("配额环 ", "Quota rings ") + ToolTip);
        if (!Rotates && !cutting) return;
        Margin = new Thickness(-edge, 0, -edge, 0);
        OpacityMask = Rotates ? EdgeFades(Width, edge) : CutFades(Width, edge, window, Math.Min(Snap(cut), Snap(gap)));
        // The masked content is composited from a layer the size of what is drawn. A transparent
        // background extends that layer to the box, so its resampled edge column falls on empty
        // space instead of repeating a ring's outermost pixels.
        Background = Brushes.Transparent;
        if (!Rotates) return;
        // The glide moves by whole ring steps, so at the end the last rings stand whole in the window, as the first did.
        Glide(_strip, (items.Count - shown) * step, elapsed, animate);
    }

    // The window cuts an item (the surface is being dragged): the box's left fade lies over the gap before the window and the
    // right one inside it, over the last cut DIPs, where the item it cuts fades out; nothing shows past the window.
    internal static LinearGradientBrush CutFades(double width, double edge, double window, double cut) => new(new GradientStopCollection { new(Colors.Transparent, 0),
        new(Colors.White, edge / width), new(Colors.White, Math.Max(edge, edge + window - cut) / width), new(Colors.Transparent, (edge + window) / width), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(width, 0))
    { MappingMode = BrushMappingMode.Absolute };

    // Transparent at both ends of the box, opaque over the window between the fades.
    internal static LinearGradientBrush EdgeFades(double width, double edge) => new(new GradientStopCollection { new(Colors.Transparent, 0),
        new(Colors.White, edge / width), new(Colors.White, (width - edge) / width), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(width, 0))
    { MappingMode = BrushMappingMode.Absolute };
}

// System readings that do not all fit scroll through a window of whole readings (CompactSpacing.Window): it rests with the
// first readings whole in it, glides until the last ones stand whole from its left edge, and rests there. The readings keep the
// width of their widest figures (MonitorStrip), so the window and the travel do not move with the figures. The fades lie
// outside the window, over the gaps beside it, so nothing at rest is cut or faded.
internal sealed class ReadingsCarousel : GlideCarousel
{
    // cut: while the surface is dragged the window is not whole readings and nothing glides (travel 0); the reading it cuts fades over
    // this many DIPs inside it (CompactSpacing.CutFade). A window narrower than the readings that cuts none still clips them.
    public ReadingsCarousel(MonitorStrip strip, double window, double travel, double edge, TimeSpan elapsed, bool animate, double cut = 0)
    {
        window = Math.Max(0, window); travel = Math.Max(0, travel); cut = Math.Max(0, cut);
        Rotates = travel > 0;
        edge = Rotates || cut > 0 ? Math.Max(0, edge) : 0;
        Width = edge + window + edge; Height = strip.Height; ClipToBounds = true;
        strip.Width = strip.PreferredWidth; strip.HorizontalAlignment = HorizontalAlignment.Left; strip.VerticalAlignment = VerticalAlignment.Top; strip.Margin = new Thickness(0);
        strip.RenderTransform = new TranslateTransform();
        Canvas.SetLeft(strip, edge);
        // A Canvas measures the strip at its full width, as for the rings.
        var host = new Canvas(); host.Children.Add(strip); Children.Add(host);
        if (Rotates || cut > 0)
        {
            Margin = new Thickness(-edge, 0, -edge, 0);
            OpacityMask = Rotates ? QuotaCarousel.EdgeFades(Width, edge) : QuotaCarousel.CutFades(Width, edge, window, cut);
            Background = Brushes.Transparent;
        }
        Glide(strip, travel, elapsed, animate);
    }
}

// The collapsed column on a side edge (MainWindow.CompactColumn.cs) when it is longer than the notch: it rests with its start in
// view, glides up until its end is, rests there and fades back, as the readings do sideways. The fades lie over the window's top
// and bottom edges, outside it (over the notch's ends), so nothing at rest is faded.
internal sealed class ColumnCarousel : GlideCarousel
{
    public ColumnCarousel(FrameworkElement column, double width, double window, double travel, double edge, TimeSpan elapsed, bool animate)
    {
        window = Math.Max(0, window); travel = Math.Max(0, travel);
        Rotates = travel > 0;
        edge = Rotates ? Math.Max(0, edge) : 0;
        Width = width; Height = edge + window + edge; ClipToBounds = true;
        column.VerticalAlignment = VerticalAlignment.Top; column.RenderTransform = new TranslateTransform();
        Canvas.SetTop(column, edge);
        // A Canvas measures the column at its full length, as for the rings.
        var host = new Canvas(); host.Children.Add(column); Children.Add(host);
        if (Rotates && edge > 0)
        {
            Margin = new Thickness(0, -edge, 0, -edge);
            OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, edge / Height),
                new(Colors.White, (Height - edge) / Height), new(Colors.Transparent, 1) }, new Point(0, 0), new Point(0, Height)) { MappingMode = BrushMappingMode.Absolute };
            Background = Brushes.Transparent;
        }
        Glide(column, travel, elapsed, animate, vertical: true);
    }
}