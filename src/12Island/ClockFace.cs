using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TwelveIsland.Core;

namespace TwelveIsland;

// The time on the collapsed notch or capsule. Each digit takes the advance of the widest digit and the hour always has
// room for two, so the face keeps one width through the day and nothing beside it moves when the minute changes.
internal sealed class ClockFace : FrameworkElement
{
    public const double FaceHeight = 24;
    public static readonly Color White = Color.FromRgb(0xF5, 0xF5, 0xF7);
    private static readonly FontFamily Orbitron = Bundled("Orbitron"), InstrumentSerif = Bundled("Instrument Serif"), Unbounded = Bundled("Unbounded"), BebasNeue = Bundled("Bebas Neue"), Fredoka = Bundled("Fredoka");
    private static FontFamily Bundled(string name) => new(new Uri("pack://application:,,,/12Island;component/"), "./Assets/Fonts/#" + name);
    private static readonly Typeface Plain = Face(UI.PanelFont), Bold = Face(UI.PanelFont, FontWeights.SemiBold);
    private static Typeface Face(FontFamily family, FontWeight? weight = null, bool italic = false)
        => new(family, italic ? FontStyles.Italic : FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal);

    private readonly ClockStyle _style;
    private readonly bool _h24, _seconds;
    private readonly Color[] _colors;
    private DateTime _time;
    private double? _reserved;
    // The collapsed row's height scale. The face is laid out at 1 and its outlines are drawn scaled: every glyph is filled as
    // geometry, so a scaled outline is the same as one built at the larger size.
    private readonly double _scale;
    private readonly bool _plain;

    // One color fills the face; more are spread evenly across it from left to right.
    public ClockFace(ClockStyle style, bool h24, bool seconds, Color[] colors, DateTime time, double scale = 1)
    {
        _style = style; _h24 = h24; _seconds = seconds; _colors = colors.Length > 0 ? colors : [ThemeWhite]; _time = time;
        _scale = CompactMusicLayout.SafeScale(scale);
        Height = FaceHeight * _scale; IsHitTestVisible = false;
        // Under high contrast nothing is dimmed or glows (ClockColors.Glows).
        _plain = PanelTheme.HighContrast;
        if (!ClockColors.Glows(style, _plain)) return;
        var glow = _colors[_colors.Length / 2];
        // A glow lights up a dark surface; on a light one it only smears the digits, so it stays a faint halo there.
        bool dark = PanelTheme.Current.IsDark;
        if (style == ClockStyle.Neon) Effect = new DropShadowEffect { Color = glow, ShadowDepth = 0, BlurRadius = (dark ? 9 : 4) * _scale, Opacity = dark ? .85 : .3 };
        else if (style == ClockStyle.Segment) Effect = new DropShadowEffect { Color = glow, ShadowDepth = 0, BlurRadius = (dark ? 5 : 3) * _scale, Opacity = dark ? .6 : .2 };
    }

    public static string Title(ClockStyle style) => style switch
    {
        ClockStyle.Stacked => Loc.T("双行", "Two lines"), ClockStyle.Digits => Loc.T("叠放", "Stacked digits"), ClockStyle.Dots => Loc.T("点阵", "Dot matrix"),
        ClockStyle.Segment => Loc.T("数码管", "Seven segment"), ClockStyle.Neon => Loc.T("霓虹", "Neon"), ClockStyle.Serif => Loc.T("衬线", "Serif"),
        ClockStyle.Wide => Loc.T("宽体", "Wide"), ClockStyle.Condensed => Loc.T("窄体", "Condensed"), ClockStyle.Pixel => Loc.T("像素", "Pixel"),
        ClockStyle.Rounded => Loc.T("圆润", "Rounded"), ClockStyle.Date => Loc.T("日期", "Date"), _ => Loc.T("简约", "Simple")
    };
    // The cover color is used when a song with a cover is playing; otherwise the clock takes the theme's clock color, which
    // "white" stands for. Every color is drawn as the theme can read it on its surface (see PanelTheme.Readable). Under high
    // contrast it is always the theme's clock color, the system's window text (ClockColors.Shown).
    public static Color[] Palette(DeckSettings settings, Color? cover) => (ClockColors.Shown(settings.ClockColor, PanelTheme.HighContrast) switch
    {
        ClockColor.Gradient => settings.ClockGradient.Select(Parse).ToArray(),
        ClockColor.Custom => [Parse(settings.ClockCustomColor)],
        ClockColor.Cover => [cover ?? ThemeWhite],
        var preset => [Preset(preset)]
    }).Select(color => PanelTheme.Readable(color, PanelThemes.TextContrast)).ToArray();
    private static Color ThemeWhite => PanelTheme.ColorOf("clock");
    // Amber, cyan, pink and lime are the panel theme's own versions of those colors; "white" is the theme's clock color.
    public static Color Preset(ClockColor color) => PanelTheme.ToColor(PanelTheme.Current.ClockPreset(color));
    public static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    // A swatch for colors at evenly spaced stops; the diagonal shows every stop on a round swatch.
    public static Brush Fill(IReadOnlyList<Color> colors, bool diagonal = true)
    {
        if (colors.Count == 1) return new SolidColorBrush(colors[0]);
        var stops = new GradientStopCollection(colors.Select((color, i) => new GradientStop(color, (double)i / (colors.Count - 1))));
        return new LinearGradientBrush(stops, new Point(0, 0), diagonal ? new Point(1, 1) : new Point(1, 0));
    }

    public DateTime Time
    {
        get => _time;
        set { if (value == _time) return; _time = value; InvalidateVisual(); }
    }
    // The widest the face gets on any day: a two-digit hour, a two-digit month and day, every weekday, morning and afternoon.
    // Every digit takes the same width, so the seconds need no sample of their own.
    // In DIPs at the face's scale. A scaled face is rounded up to a whole DIP so the regions beside it stay on whole DIPs;
    // the default face keeps its measured width, as in the settings preview.
    public double ReservedWidth => _scale == 1 ? UnitReserved : Math.Ceiling(UnitReserved * _scale - .001);
    private double UnitReserved => _reserved ??= Enumerable.Range(22, 7).SelectMany(day => new[] { 10, 22 }.Select(hour => new DateTime(2025, 12, day, hour, 0, 0)))
        .Max(time => Layout(null, time, 0, Brushes.White));

    protected override Size MeasureOverride(Size availableSize) => new(ReservedWidth, FaceHeight * _scale);

    protected override void OnRender(DrawingContext dc)
    {
        double width = Layout(null, _time, 0, Brushes.White), scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The start is a whole device pixel after scaling, as at the default size.
        double x = Math.Round((ReservedWidth - width * _scale) / 2 * scale) / scale / _scale;
        Brush paint = Fill(_colors);
        if (paint is LinearGradientBrush gradient) { gradient.MappingMode = BrushMappingMode.Absolute; gradient.StartPoint = new Point(x, 0); gradient.EndPoint = new Point(x + width, 0); }
        paint.Freeze();
        if (_scale != 1) dc.PushTransform(new ScaleTransform(_scale, _scale));
        Layout(dc, _time, x, paint);
        if (_scale != 1) dc.Pop();
    }

    // Draws the face from x when dc is set; returns the width either way.
    private double Layout(DrawingContext? dc, DateTime time, double x, Brush paint)
    {
        string hour = _h24 ? time.ToString("HH", CultureInfo.InvariantCulture) : (time.Hour % 12 == 0 ? 12 : time.Hour % 12).ToString(CultureInfo.InvariantCulture);
        string minute = time.ToString("mm", CultureInfo.InvariantCulture), secs = time.ToString("ss", CultureInfo.InvariantCulture);
        string clock = hour + ":" + minute + (_seconds ? ":" + secs : "");
        // Both languages mark the 12-hour clock with AM or PM after the time.
        string? period = _h24 ? null : time.Hour < 12 ? "AM" : "PM";
        switch (_style)
        {
            case ClockStyle.Dots: return Dots(dc, clock, x);
            case ClockStyle.Pixel: return Blocks(dc, clock, x);
            case ClockStyle.Segment: return Segments(dc, clock, x);
            case ClockStyle.Digits:
            {
                // Hours over minutes, both as two digits, the minutes dimmer; the seconds, smaller still, follow at the middle.
                var hours = new Run(hour.PadLeft(2, '0'), Bold, 11, -.02);
                var minutes = hours with { Text = minute, Opacity = .55 };
                double width = Math.Max(RunWidth(hours), RunWidth(minutes));
                double top = (FaceHeight - 23) / 2;
                Draw(dc, x + (width - RunWidth(hours)) / 2, Baseline(hours, top + 5.75), paint, hours);
                Draw(dc, x + (width - RunWidth(minutes)) / 2, Baseline(minutes, top + 17.25), paint, minutes);
                if (!_seconds) return width;
                var seconds = new Run(secs, Bold, 8.5, Opacity: .55);
                return Draw(dc, x + width + 2.5, Baseline(seconds, FaceHeight / 2), paint, seconds) - x;
            }
            case ClockStyle.Stacked:
            {
                // The time over the date and weekday, each line centred on the other.
                var time1 = new Run(clock, Bold, 11.5);
                var label = period is null ? (Run?)null : new Run(period, Plain, 9.5, Opacity: .62, Cells: false);
                var date = new Run(Loc.IsEnglish ? time.ToString("ddd M/d", CultureInfo.GetCultureInfo("en-US"))
                    : $"{time.Month}/{time.Day} 周{"日一二三四五六"[(int)time.DayOfWeek]}", Plain, 9, Opacity: .62, Cells: false);
                const double gap = 3;
                double first = RunWidth(time1) + (label is { } l ? RunWidth(l) + gap : 0), second = RunWidth(date), width = Math.Max(first, second);
                double baseline = Baseline(time1, 6.5);
                double left = Draw(dc, x + (width - first) / 2, baseline, paint, time1);
                if (label is { } after) Draw(dc, left + gap, baseline, paint, after);
                Draw(dc, x + (width - second) / 2, Baseline(date, 18.5), paint, date);
                return width;
            }
            case ClockStyle.Date:
            {
                // The time's font at the time's size, the month and day first then the time on one line: no weekday,
                // so the row stays short and both parts read alike.
                var date = new Run(Loc.IsEnglish ? time.ToString("M/d", CultureInfo.GetCultureInfo("en-US"))
                    : $"{time.Month}/{time.Day}", Bold, 12.5);
                var time1 = new Run(clock, Bold, 12.5);
                var label = period is null ? (Run?)null : new Run(period, Bold, 12.5, Opacity: .62);
                const double gap = 6;
                double baseline = Baseline(time1, FaceHeight / 2), start = x;
                x = Draw(dc, x, baseline, paint, date);
                x = Draw(dc, x + gap, baseline, paint, time1);
                if (label is { } after) x = Draw(dc, x + gap, baseline, paint, after);
                return x - start;
            }
            default:
            {
                var main = _style switch
                {
                    ClockStyle.Neon => new Run(clock, Face(Orbitron, FontWeights.SemiBold), 11.5, .06),
                    ClockStyle.Serif => new Run(clock, Face(InstrumentSerif, italic: true), 19, .01),
                    ClockStyle.Wide => new Run(clock, Face(Unbounded, FontWeights.SemiBold), 11.5, .01),
                    ClockStyle.Condensed => new Run(clock, Face(BebasNeue), 19, .05),
                    // Fredoka SemiBold, round and soft. Its digits are 0.72 em tall like Orbitron's, so it takes the neon clock's size.
                    ClockStyle.Rounded => new Run(clock, Face(Fredoka, FontWeights.SemiBold), 11.5, .02),
                    _ => new Run(clock, Bold, 12.5)
                };
                Run? label = period is null ? null : _style switch
                {
                    ClockStyle.Neon => new Run(period, Face(Orbitron, FontWeights.SemiBold), 8, .04, Cells: false),
                    ClockStyle.Rounded => new Run(period, Face(Fredoka, FontWeights.SemiBold), 8.5, .03, Opacity: .8, Cells: false),
                    ClockStyle.Minimal => new Run(period, Plain, 10, Opacity: .62, Cells: false),
                    _ => new Run(period, Plain, 10, Opacity: .7, Cells: false)
                };
                const double gap = 3;
                double baseline = Baseline(main, FaceHeight / 2), start = x;
                x = Draw(dc, x, baseline, paint, main);
                if (label is { } after) x = Draw(dc, x + gap, baseline, paint, after);
                return x - start;
            }
        }
    }

    // Letter spacing is in ems and falls between characters. Digits in a cell run take the widest digit's advance.
    private readonly record struct Run(string Text, Typeface Face, double Size, double Spacing = 0, double Opacity = 1, bool Cells = true);
    private double RunWidth(Run run) => Draw(null, 0, 0, Brushes.White, run);
    // Returns where the run ends.
    private double Draw(DrawingContext? dc, double x, double baseline, Brush paint, Run run)
    {
        double spacing = run.Spacing * run.Size, cell = run.Cells ? DigitCell(run) : 0;
        bool dim = dc is not null && run.Opacity < 1 && !_plain;
        if (dim) dc!.PushOpacity(run.Opacity);
        if (!run.Cells && run.Spacing == 0)
        {
            var whole = Text(run.Text, run, paint);
            Paint(dc, whole, new Point(x, baseline - whole.Baseline), paint);
            x += whole.WidthIncludingTrailingWhitespace;
        }
        else for (int i = 0; i < run.Text.Length; i++)
        {
            var text = Text(run.Text[i].ToString(), run, paint);
            double advance = text.WidthIncludingTrailingWhitespace, box = run.Cells && char.IsAsciiDigit(run.Text[i]) ? cell : advance;
            Paint(dc, text, new Point(x + (box - advance) / 2, baseline - text.Baseline), paint);
            x += box + (i < run.Text.Length - 1 ? spacing : 0);
        }
        if (dim) dc!.Pop();
        return x;
    }
    // DrawText moves each line to the nearest whole device pixel, which undoes the sub-pixel baseline from Baseline below:
    // at 11.5 DIP the cap height is not a whole number of pixels, so the snapped digits end up to half a pixel off centre.
    // Filling the glyph outlines keeps the exact position the metrics give.
    private static void Paint(DrawingContext? dc, FormattedText text, Point origin, Brush paint)
    {
        if (dc is null) return;
        var outline = text.BuildGeometry(origin); outline.Freeze();
        dc.DrawGeometry(paint, null, outline);
    }
    private static readonly Dictionary<(Typeface, double, double), double> DigitCells = [];
    private double DigitCell(Run run)
    {
        var key = (run.Face, run.Size, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!DigitCells.TryGetValue(key, out var cell))
            DigitCells[key] = cell = "0123456789".Max(digit => Text(digit.ToString(), run, Brushes.White).WidthIncludingTrailingWhitespace);
        return cell;
    }
    private FormattedText Text(string text, Run run, Brush paint)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, run.Face, run.Size, paint, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    // The baseline that centres the ink of the run's digits on the given height.
    private double Baseline(Run run, double middle)
    {
        var zero = Text("0", run, Brushes.White);
        var ink = zero.BuildGeometry(new Point()).Bounds;
        return middle - (ink.Top + ink.Bottom) / 2 + zero.Baseline;
    }

    // Dot matrix: 5 × 7 digits drawn as 1.7 DIP dots on a 2 DIP pitch; only the lit dots are drawn.
    private static readonly Dictionary<char, string[]> DotFont = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"], ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"], ['3'] = ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"], ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"], ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"], ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        [':'] = ["0", "0", "1", "0", "1", "0", "0"]
    };
    private double Dots(DrawingContext? dc, string clock, double x)
    {
        const double pitch = 2, dot = 1.7, gap = 1.8;
        double top = (FaceHeight - (6 * pitch + dot)) / 2, start = x;
        for (int i = 0; i < clock.Length; i++)
        {
            var rows = DotFont[clock[i]];
            if (dc is not null)
            {
                var brush = GlyphBrush(i, clock.Length);
                for (int row = 0; row < rows.Length; row++)
                    for (int column = 0; column < rows[row].Length; column++)
                        if (rows[row][column] == '1') dc.DrawEllipse(brush, null, new Point(x + column * pitch + dot / 2, top + row * pitch + dot / 2), dot / 2, dot / 2);
            }
            x += (rows[0].Length - 1) * pitch + dot + (i < clock.Length - 1 ? gap : 0);
        }
        return x - start;
    }

    // Pixel: the dot matrix's digits as solid square blocks, 2 DIP each with no gap, so each lit block meets its neighbors and
    // the strokes read as one pixel-art glyph. Blocks lie on whole device pixels at every scale: the face starts on a whole
    // device pixel (OnRender), and a block is the device pixels its square covers.
    private double Blocks(DrawingContext? dc, string clock, double x)
    {
        // Every block the same whole number of device pixels (at 125 % a 2 DIP block would alternate between 2 and 3 pixels);
        // a half rounds up, as the island's pixel steps do (PixelStairs.Step).
        double device = _scale * VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Snap(double value) => Math.Round(value * device, MidpointRounding.AwayFromZero) / device;
        double block = PixelStairs.Step(2, device), gap = block;
        double top = Snap((FaceHeight - 7 * block) / 2), start = x;
        for (int i = 0; i < clock.Length; i++)
        {
            var rows = DotFont[clock[i]];
            if (dc is not null)
            {
                var brush = GlyphBrush(i, clock.Length);
                for (int row = 0; row < rows.Length; row++)
                    for (int column = 0; column < rows[row].Length; column++)
                        if (rows[row][column] == '1')
                        {
                            double left = Snap(x + column * block), right = Snap(x + (column + 1) * block);
                            double y = Snap(top + row * block), bottom = Snap(top + (row + 1) * block);
                            dc.DrawRectangle(brush, null, new Rect(left, y, right - left, bottom - y));
                        }
            }
            x += rows[0].Length * block + (i < clock.Length - 1 ? gap : 0);
        }
        return x - start;
    }

    // Seven segments with 1.5 DIP strokes, leaning 7 degrees; only the lit segments are drawn.
    private const double Stroke = 1.5, Segment = 4.6, SegmentGlyph = Segment + 2 * Stroke - .1;
    private static readonly Dictionary<char, Rect> Segments7 = new()
    {
        ['a'] = new(Stroke - .05, 0, Segment, Stroke), ['b'] = new(Segment + Stroke - .1, Stroke - .25, Stroke, Segment),
        ['c'] = new(Segment + Stroke - .1, Segment + Stroke + .05, Stroke, Segment), ['d'] = new(Stroke - .05, 2 * Segment + Stroke - .2, Segment, Stroke),
        ['e'] = new(0, Segment + Stroke + .05, Stroke, Segment), ['f'] = new(0, Stroke - .25, Stroke, Segment),
        ['g'] = new(Stroke - .05, (2 * Segment + Stroke - .2) / 2, Segment, Stroke)
    };
    private static readonly string[] SegmentMap = ["abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg"];
    private double Segments(DrawingContext? dc, string clock, double x)
    {
        const double gap = 1.4, height = 12;
        double width = clock.Sum(ch => ch == ':' ? Stroke : SegmentGlyph) + gap * (clock.Length - 1), top = (FaceHeight - height) / 2;
        if (dc is null) return width;
        dc.PushTransform(new SkewTransform(-7, 0, x + width / 2, FaceHeight / 2));
        for (int i = 0; i < clock.Length; i++)
        {
            var brush = GlyphBrush(i, clock.Length);
            if (clock[i] == ':')
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(x, top + 3.3, Stroke, Stroke), .6, .6);
                dc.DrawRoundedRectangle(brush, null, new Rect(x, top + 7.6, Stroke, Stroke), .6, .6);
                x += Stroke + gap; continue;
            }
            foreach (var key in SegmentMap[clock[i] - '0'])
            {
                var rect = Segments7[key]; rect.Offset(x, top);
                dc.DrawRoundedRectangle(brush, null, rect, .6, .6);
            }
            x += SegmentGlyph + gap;
        }
        dc.Pop();
        return width;
    }

    // Drawn glyphs take whole colors: the gradient moves from glyph to glyph, left to right.
    private Brush GlyphBrush(int index, int count)
    {
        if (_colors.Length == 1) return UI.Brush(_colors[0].ToString());
        double t = (count > 1 ? (double)index / (count - 1) : 0) * (_colors.Length - 1);
        int segment = Math.Min((int)t, _colors.Length - 2);
        var (from, to, f) = (_colors[segment], _colors[segment + 1], t - segment);
        byte Mix(byte a, byte b) => (byte)Math.Round(a * (1 - f) + b * f);
        return UI.Brush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)).ToString());
    }
}
