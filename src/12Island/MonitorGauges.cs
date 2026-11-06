using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// The system monitor page's gauges. Each draws itself and takes the size its cell gives it, so a new reading redraws it without
// measuring anything; values ease to each reading in a little under half a second (Eased, Motion) and then rest. Hover and press
// feedback fades in and out over Soft.Quick in the same way.

// The frame rate's line under memory and network, laid out as the power line is: the program's icon (or a screen in the frame
// rate's color, for the desktop or a program without one), the frames a
// second and "FPS", the program's name, its readings so far as a curve, and the refresh rate of the display it is on at the right
// end, which is also the curve's top, so it shows how far the frames stay below what the display can show (a game drawing faster
// than its display raises the top to its highest reading). A dash while there is no reading, as without permission to read
// Windows' frame events.
internal sealed class FramesBlock : FrameworkElement
{
    private readonly string _color;
    private readonly double _body, _caption;
    private FrameSample? _frames;
    private readonly DrawingCache<(IReadOnlyList<double>, double, double, double)> _curve = new();

    public FramesBlock(string color, double body, double caption, double row)
    {
        _color = color; _body = body; _caption = caption;
        Height = row; Focusable = false;
        Describe();
    }

    public void Set(MonitorSnapshot snapshot)
    {
        var frames = snapshot.Frames.OrDefault;
        if (ReferenceEquals(frames, _frames)) return;
        // The curve moves with every reading; the tooltip's words only when the figure, the program or the display change.
        string figure = Figure;
        bool same = frames is not null && _frames is not null && frames.Program == _frames.Program && frames.Refresh == _frames.Refresh;
        _frames = frames;
        if (!same || Figure != figure) Describe();
        InvalidateVisual();
    }

    private string Figure => _frames is { } frames && double.IsFinite(frames.PerSecond)
        ? Math.Clamp(Math.Round(frames.PerSecond, MidpointRounding.AwayFromZero), 0, 999).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "—";
    private static string Hertz(double hertz) => Math.Round(hertz).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " Hz";

    private void Describe()
    {
        if (_frames is not { } frames)
        {
            ToolTip = Loc.T("帧率：暂无读数。一直没有读数时，请在设置的“系统监控”页授权读取帧率。",
                "Frame rate: no reading. If none comes, allow reading frame rates on the System monitor page of the settings.");
            AutomationProperties.SetName(this, Loc.T("帧率", "Frame rate"));
            return;
        }
        ToolTip = Loc.T($"{frames.Program}：每秒 {Figure} 帧", $"{frames.Program}: {Figure} frames a second")
            + Loc.T("\n前台程序的帧率；前台程序不在绘制时，显示最近一个在绘制的程序。", "\nThe frame rate of the program in front; while it draws nothing, of the last program that did.")
            + (frames.Refresh is { } hertz ? Loc.T($"\n所在显示器刷新率 {Hertz(hertz)}", $"\nIts display's refresh rate: {Hertz(hertz)}") : "");
        AutomationProperties.SetName(this, Loc.T($"帧率 {Figure} FPS，{frames.Program}", $"Frame rate {Figure} FPS, {frames.Program}"));
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, ppd = MonitorDraw.PixelsPerDip(this);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        // The glyph in the room a dial takes, as the network line's arrows are, so the figures line up with memory's.
        double badge = Math.Round(_body + 5), glyph = Math.Round(badge * .72), baseline = Math.Round(h / 2 + _body * .38), gap = Math.Round(_body * .8);
        if (ProcessIcons.For(_frames?.Path) is { } icon)
        {
            double size = Math.Round(_body + 3);
            drawing.DrawImage(icon, new Rect((badge - size) / 2, Math.Round(h / 2 - size / 2), size, size));
        }
        else MonitorDraw.Arrow(drawing, new Point((badge - glyph) / 2, h / 2 - glyph / 2), glyph, Math.Max(1, glyph * .09), MonitorGlyph.Screen, _frames is null ? UI.Tertiary : _color);
        double x = badge + Math.Round(_body * .5);
        var number = MonitorDraw.Text(Figure, _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, number, x, baseline); x += number.WidthIncludingTrailingWhitespace;
        var unit = MonitorDraw.Text("FPS", _caption, UI.Tertiary, ppd);
        MonitorDraw.Baseline(drawing, unit, x + 3, baseline); x += 3 + unit.WidthIncludingTrailingWhitespace;
        if (_frames is not { } frames) return;
        // The refresh rate at the right end, then the name after the figure, cut to the room between them.
        double right = w;
        if (frames.Refresh is { } hertz)
        {
            var rate = MonitorDraw.Text(Hertz(hertz), _caption, UI.Tertiary, ppd);
            right = w - rate.WidthIncludingTrailingWhitespace;
            MonitorDraw.Baseline(drawing, rate, right, baseline);
            right -= gap;
        }
        double nameRoom = Math.Min(right - x - gap, Math.Max(60, (right - x) * .4));
        if (nameRoom > 24)
        {
            var name = MonitorDraw.Trimmed(frames.Program, _caption, UI.Tertiary, nameRoom, ppd);
            MonitorDraw.Baseline(drawing, name, x + gap, baseline);
            x += gap + Math.Min(nameRoom, name.WidthIncludingTrailingWhitespace);
        }
        // The curve in the room left, as tall as the power line's, scaled to the refresh rate (or the highest reading above it).
        var history = frames.History;
        double start = Math.Max(x + 10, w * .45), areaWidth = right - start, areaHeight = Math.Min(h - 4, Math.Ceiling(_body * 1.5) - 4);
        if (history.Count >= 3 && areaWidth >= 24 && areaHeight >= 6)
        {
            double top = Math.Max(frames.Refresh ?? 60, history.Max());
            var area = new Rect(start, Math.Round((h - areaHeight) / 2), areaWidth, areaHeight);
            MonitorDraw.Place(drawing, area.TopLeft, _curve.Get((history, area.Width, area.Height, top), context =>
                MonitorDraw.Sparkline(context, new Rect(0, 0, area.Width, area.Height), history, top, _color, .16)));
        }
    }
}

// A dial with the reading's letter (MonitorDraw.Dial, as the collapsed strip has it), a figure with its unit, and a detail after
// it, on one baseline: "(C) 47 % 3.41 GHz". Aligned right, the dial still leads and the whole line ends at the right edge. As a
// button it lights its dial under the pointer and takes clicks.
// The main gauge fills the room after its figure (and its detail) with the last minute's trend (Trend), a soft line in its own color.
internal sealed class FigureGauge : FrameworkElement
{
    private readonly string _letter, _color;
    private readonly double _figure, _small;
    private readonly bool _right;
    private bool _button;
    private string? _buttonName;
    private Eased _share, _hover = new(0, Eased.Hover);
    private bool _hasShare, _pressed;
    private string _number = "—", _unit = "", _detail = "", _tip = "", _detailColor = UI.Tertiary;
    private readonly DrawingCache<(string, string, string, string, double, double, double)> _text = new();
    private readonly DrawingCache<(IReadOnlyList<double>, double, double)> _trend = new();
    private IReadOnlyList<double> _history = [];

    // The last minute of readings, 0 to 1, drawn after the figure; empty draws nothing.
    public IReadOnlyList<double> Trend { set { if (!ReferenceEquals(value, _history)) { _history = value; InvalidateVisual(); } } }
    public event Action? Clicked;

    public FigureGauge(string letter, string color, double figure, double small, bool right = false, string? button = null)
    {
        _letter = letter; _color = color; _figure = figure; _small = small; _right = right; _button = button is not null;
        Height = Math.Ceiling(figure * 1.4); Focusable = false;
        Button = button;
    }

    // What a click does, for screen readers, or null for a gauge that takes no clicks; the graphics gauge takes them only while
    // there is another adapter to switch to.
    public string? Button
    {
        set
        {
            if (value == _buttonName && _button == value is not null) return;
            _buttonName = value;
            if (!(_button = value is not null)) { _pressed = false; Hover(false); }
            Cursor = _button ? Cursors.Hand : null;
            AutomationProperties.SetName(this, value ?? "");
        }
    }

    // For screen readers: the reading and what it shows after the figure, "CPU 占用 47% · 62 °C", and on the small gauge what a
    // click does (its Button name, which can change); the tooltip stays the help text.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.ProgressBar, Spoken,
        () => Clicked?.Invoke(), () => _button, () => _hasShare ? _share.Target : null);
    private string Spoken()
    {
        string name = _letter switch { "C" => "CPU", "G" => "GPU", _ => _letter };
        string reading = _hasShare ? Loc.T($"{name} 占用 {_number}{_unit}", $"{name} usage {_number}{_unit}") : name + " · " + DrawnPeer.FirstLine(ToolTip);
        return reading + (_detail.Length > 0 ? " · " + _detail : "") + (_button && !string.IsNullOrEmpty(_buttonName) ? Loc.T("，", ", ") + _buttonName : "");
    }

    // detailColor: the detail's color, the tertiary text color by default (a temperature takes its level's).
    public void Set(double? share, string number, string unit, string detail, string tip, bool motion, string? detailColor = null)
    {
        detailColor ??= UI.Tertiary;
        bool changed = number != _number || unit != _unit || detail != _detail || detailColor != _detailColor || (share is not null) != _hasShare;
        _number = number; _unit = unit; _detail = detail; _detailColor = detailColor; _hasShare = share is not null;
        if (tip != _tip) { _tip = tip; ToolTip = tip.Length > 0 ? tip : null; }
        if (share is { } value && _share.Set(value, motion)) Motion.Start(this, Step);
        else if (changed || share is not null) InvalidateVisual();
    }
    private bool Step(double now) { InvalidateVisual(); return _share.Moving(now) || _hover.Moving(now); }
    // A dial's ring and letter for its size, in the collapsed strip's proportions (a ring an eighth of the dial thinned a little,
    // as the page's dials are larger; a letter seven sixteenths of it).
    public static double DialRing(double size) => Math.Max(1.2, size * .1);
    public static double DialLetter(double size) => Math.Max(1, size * 7 / 16);
    private void Hover(bool on) { if (_hover.Fade(on, Motion.Enabled)) Motion.Start(this, Step); else InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double ppd = MonitorDraw.PixelsPerDip(this), h = ActualHeight, w = ActualWidth, now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double badge = Math.Round(_figure + 3), gap = Math.Round(_figure * .4);
        var number = MonitorDraw.Text(_number, _figure, UI.Primary, ppd, display: true);
        var unit = MonitorDraw.Text(_unit, _small, UI.Secondary, ppd);
        var detail = MonitorDraw.Text(_detail, _small, _detailColor, ppd);
        double width = badge + gap + number.WidthIncludingTrailingWhitespace + (_unit.Length > 0 ? 2 + unit.WidthIncludingTrailingWhitespace : 0)
            + (_detail.Length > 0 ? gap + detail.WidthIncludingTrailingWhitespace : 0);
        double x = _right ? Math.Max(0, w - width) : 0, baseline = Math.Round(h / 2 + number.Baseline - number.Height / 2 + _figure * .05);
        var center = new Point(x + badge / 2, h / 2);
        double hover = _button ? Math.Clamp(_hover.At(now), 0, 1) : 0;
        if (hover > .01)
        {
            drawing.PushOpacity(hover);
            drawing.DrawEllipse(UI.Brush(_pressed ? PanelTheme.Hex("pressed") : PanelTheme.Hex("hover")), null, center, badge / 2 + 2, badge / 2 + 2);
            drawing.Pop();
        }
        MonitorDraw.Dial(drawing, center, badge, DialRing(badge), DialLetter(badge), _hasShare ? _share.At(now) : null, _color, _letter, ppd);
        // The figures change once per reading; while the arc moves they are placed as they were drawn.
        double start = x + badge + gap;
        if (_history.Count >= 3 && !_right)
        {
            // From after the figures (and any detail) to the right edge, half the line's height, centred on it.
            double end = start + number.WidthIncludingTrailingWhitespace + (_unit.Length > 0 ? 2 + unit.WidthIncludingTrailingWhitespace : 0)
                + (_detail.Length > 0 ? gap + detail.WidthIncludingTrailingWhitespace : 0) + gap * 1.5;
            var area = new Rect(end, Math.Round(h * .2), Math.Max(0, w - end - 6), Math.Round(h * .6));
            if (area.Width >= 48)
                MonitorDraw.Place(drawing, area.TopLeft, _trend.Get((_history, area.Width, area.Height), context =>
                    MonitorDraw.Sparkline(context, new Rect(0, 0, area.Width, area.Height), _history, 1, _color, .14, 1.3)));
        }
        drawing.DrawDrawing(_text.Get((_number, _unit, _detail, _detailColor, w, h, ppd), context =>
        {
            double x = start;
            MonitorDraw.Baseline(context, number, x, baseline); x += number.WidthIncludingTrailingWhitespace;
            if (_unit.Length > 0) { MonitorDraw.Baseline(context, unit, x + 2, baseline); x += 2 + unit.WidthIncludingTrailingWhitespace; }
            if (_detail.Length > 0)
            {
                // A long detail is cut to the room left, with an ellipsis.
                double room = w - x - gap;
                if (room > 12) MonitorDraw.Baseline(context, detail.WidthIncludingTrailingWhitespace <= room ? detail : MonitorDraw.Trimmed(_detail, _small, _detailColor, room, ppd), x + gap, baseline);
            }
        }));
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); if (_button) Hover(true); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (_button) { _pressed = false; Hover(false); } }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_button) return;
        _pressed = true; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_button || !_pressed) return;
        _pressed = false; InvalidateVisual(); e.Handled = true; Clicked?.Invoke();
    }
}

// One column of a graph: its value (0..1), an optional tick (0..1, the core's frequency), its color and group (a gap separates
// groups), its tooltip, and under it, where the panel is tall enough, a figure and a smaller line.
internal sealed record GraphColumn(double Value, double? Tick, string Color, int Group, string Tip, string? Label = null, string? Sub = null);

// Columns side by side, ftop's core columns: the fill is the value, the tick the frequency. Used for the processor's cores, the
// graphics adapter's engines, and small for the gauge shown beside the main one. As a button the whole graph takes clicks.
internal sealed class ColumnGraph : FrameworkElement
{
    private const double Rest = 3;
    private readonly double _maxColumn, _label, _gap;
    private readonly bool _labels, _button;
    private IReadOnlyList<GraphColumn> _columns = [];
    private Eased[] _values = [], _ticks = [];
    private bool[] _hasTick = [];
    private readonly ToolTip _tip = new();
    private int _tipIndex = -1;
    // The tracks and the figures under the columns, drawn again only when the figures or the size change (_labelsVersion).
    private readonly DrawingCache<(int, double, double, double)> _fixed = new();
    private int _labelsVersion;
    private readonly string _name, _action;
    public event Action? Clicked;

    // name: what the graph shows, for screen readers ("CPU 各核心占用").
    public ColumnGraph(double maxColumn, double label, bool labels, double gap = 3, string? button = null, string name = "")
    {
        _maxColumn = maxColumn; _label = label; _labels = labels; _gap = gap; _button = button is not null; _name = name; _action = button ?? "";
        ClipToBounds = true; SnapsToDevicePixels = true;
        if (button is not null) { Cursor = Cursors.Hand; AutomationProperties.SetName(this, button); }
        else ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Mouse);
    }

    public void Set(IReadOnlyList<GraphColumn> columns, bool motion)
    {
        bool same = columns.Count == _columns.Count && columns.Select(c => c.Group).SequenceEqual(_columns.Select(c => c.Group));
        if (!same) { _values = new Eased[columns.Count]; _ticks = new Eased[columns.Count]; _hasTick = new bool[columns.Count]; motion = false; }
        if (!same || _labels && Enumerable.Range(0, columns.Count).Any(i => columns[i].Label != _columns[i].Label || columns[i].Sub != _columns[i].Sub)) _labelsVersion++;
        bool moving = false;
        for (int i = 0; i < columns.Count; i++)
        {
            moving |= _values[i].Set(columns[i].Value, motion);
            bool tick = columns[i].Tick is not null;
            if (tick) moving |= _ticks[i].Set(columns[i].Tick!.Value, motion && _hasTick[i]);
            _hasTick[i] = tick;
        }
        _columns = columns;
        if (_tipIndex >= 0 && _tipIndex < columns.Count && _tip.IsOpen) _tip.Content = columns[_tipIndex].Tip;
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
        // Once per reading, not per frame: the columns a screen reader lists are these.
        DrawnPeer.Refresh(this);
    }
    private bool Step(double now) { InvalidateVisual(); return _values.Any(v => v.Moving(now)) || _ticks.Any(t => t.Moving(now)); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    // For screen readers: a list of the columns, each read as its tooltip says ("性能核 3 · 47% · 3.41 GHz"); as a button, the
    // graph's name and what a click does.
    protected override AutomationPeer OnCreateAutomationPeer() => _button
        ? new DrawnPeer(this, AutomationControlType.Button, () => _name + Loc.T("，", ", ") + _action, () => Clicked?.Invoke())
        : new DrawnPeer(this, AutomationControlType.List, () => _name, parts: Parts);
    private IEnumerable<DrawnPart> Parts()
    {
        var (xs, column) = Geometry(ActualWidth);
        for (int i = 0; i < xs.Length; i++)
            yield return new DrawnPart(i.ToString(), _columns[i].Tip.Replace("\n", Loc.T("，", ", ")), new Rect(xs[i], 0, column, ActualHeight));
    }

    // The room under the columns for their figures. With the figure line alone, the line's baseline sits on the graph's bottom
    // edge, level with the foot of the graph beside it (the engines' names and the small cores); a second line needs room for both.
    private double LabelHeight(double ppd) => !_labels ? 0
        : _columns.Any(c => c.Sub is { Length: > 0 }) ? Math.Ceiling(_label * 2.5)
        : 3 + Math.Ceiling(MonitorDraw.Text("3D", _label, UI.Primary, ppd, strong: true).Baseline);

    private (double[] X, double Width) Geometry(double width)
    {
        int count = _columns.Count;
        if (count == 0) return ([], 0);
        double gaps = 0;
        for (int i = 1; i < count; i++) gaps += Gap(i);
        double column = Math.Clamp((width - gaps) / count, 1, _maxColumn);
        var x = new double[count];
        for (int i = 1; i < count; i++) x[i] = x[i - 1] + column + Gap(i);
        return (x, column);
    }
    private double Gap(int index) => _columns[index].Group != _columns[index - 1].Group ? _gap * 2.4 : _columns.Count > 48 ? Math.Min(_gap, 1.5) : _gap;

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), h = ActualHeight - LabelHeight(ppd), now = Eased.Now;
        if (_button) drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var (xs, column) = Geometry(w);
        if (xs.Length == 0 || h <= 4) return;
        double radius = Math.Min(column / 2, 5);
        drawing.DrawDrawing(_fixed.Get((_labelsVersion, w, ActualHeight, ppd), context => Fixed(context, xs, column, h, radius, ppd)));
        var tick = new Pen(UI.Brush(PanelTheme.Current["textPrimary"].WithAlpha(0xB3).ToString()), Math.Max(1, Math.Min(1.6, column / 6)));
        tick.Freeze();
        for (int i = 0; i < xs.Length; i++)
        {
            var c = _columns[i];
            double fill = Math.Max(Rest, Math.Clamp(_values[i].At(now), 0, 1) * h);
            drawing.DrawRoundedRectangle(UI.Brush(c.Color), null, new Rect(xs[i], h - fill, column, fill), radius, radius);
            if (_hasTick[i] && column >= 6)
            {
                double y = Math.Round((1 - Math.Clamp(_ticks[i].At(now), 0, 1)) * (h - 2)) + 1, inset = column * .2;
                drawing.DrawLine(tick, new Point(xs[i] + inset, y), new Point(xs[i] + column - inset, y));
            }
        }
    }

    // The tracks, and under each column, where the column is wide enough to hold them, its figure and a second line.
    private void Fixed(DrawingContext drawing, double[] xs, double column, double h, double radius, double ppd)
    {
        var track = UI.Brush(UI.Fill);
        for (int i = 0; i < xs.Length; i++)
        {
            var c = _columns[i];
            drawing.DrawRoundedRectangle(track, null, new Rect(xs[i], 0, column, h), radius, radius);
            if (!_labels) continue;
            if (c.Label is { Length: > 0 } label && MonitorDraw.Text(label, _label, UI.Primary, ppd, strong: true) is var top && top.Width <= column + 4)
                drawing.DrawText(top, new Point(xs[i] + (column - top.Width) / 2, h + 3));
            if (c.Sub is { Length: > 0 } sub && MonitorDraw.Text(sub, _label - 1.5, UI.Tertiary, ppd) is var bottom && bottom.Width <= column + 6)
                drawing.DrawText(bottom, new Point(xs[i] + (column - bottom.Width) / 2, h + 3 + _label * 1.25));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_button) return;
        var (xs, column) = Geometry(ActualWidth);
        double x = e.GetPosition(this).X;
        int index = Array.FindLastIndex(xs, start => x >= start - _gap / 2);
        if (index >= 0 && x > xs[index] + column + _gap) index = -1;
        if (index == _tipIndex) return;
        _tipIndex = index;
        _tip.Content = index < 0 ? null : _columns[index].Tip;
        ToolTip = index < 0 ? null : _tip;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (_button) { e.Handled = true; Clicked?.Invoke(); } }
}

// One wide column, as ftop draws the GPU beside the cores: the fill is usage, the line its memory in use against its size.
internal sealed class BlockGauge : FrameworkElement
{
    private readonly string _color;
    private Eased _value, _tick, _hover = new(0, Eased.Hover);
    private bool _hasTick, _hasValue;
    private readonly string _action;
    public event Action? Clicked;

    public BlockGauge(string color, string button) { _color = color; _action = button; Cursor = Cursors.Hand; AutomationProperties.SetName(this, button); SnapsToDevicePixels = true; }

    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Button, Spoken, () => Clicked?.Invoke());
    // For screen readers, named as the block it is, with the memory its line shows, so it never reads the same as the small GPU figure beside the main
    // gauge that does the same thing (I-02): "GPU 占用块：占用 12% · 显存 18%，切换为以 GPU 为主".
    // Without a reading it says what the tooltip says (the adapter has none, and why), never a usage of zero.
    private string Spoken() => MonitorSpeech.GpuBlock(_hasValue ? _value.Target : null, _hasTick ? _tick.Target : null, DrawnPeer.FirstLine(ToolTip), _action);

    // value: the adapter's usage, or null while it has no reading (the block then draws empty and reads as having none).
    public void Set(double? value, double? tick, string tip, bool motion)
    {
        ToolTip = tip;
        _hasValue = value is not null;
        bool moving = _value.Set(value ?? 0, motion);
        if (tick is { } t) moving |= _tick.Set(t, motion && _hasTick);
        _hasTick = tick is not null;
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
    }
    private bool Step(double now) { InvalidateVisual(); return _value.Moving(now) || _tick.Moving(now) || _hover.Moving(now); }
    private void Hover(bool on) { if (_hover.Fade(on, Motion.Enabled)) Motion.Start(this, Step); else InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, now = Eased.Now, radius = Math.Min(8, w / 4);
        if (w <= 0 || h <= 0) return;
        var box = new Rect(0, 0, w, h);
        drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, box, radius, radius);
        double hover = Math.Clamp(_hover.At(now), 0, 1);
        if (hover > .01) { drawing.PushOpacity(hover); drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("hover")), null, box, radius, radius); drawing.Pop(); }
        double fill = Math.Max(3, Math.Clamp(_value.At(now), 0, 1) * h);
        drawing.DrawRoundedRectangle(UI.Brush(_color), null, new Rect(0, h - fill, w, fill), radius, radius);
        if (!_hasTick || w < 10) return;
        var pen = new Pen(UI.Brush(PanelTheme.Current["textPrimary"].WithAlpha(0xB3).ToString()), 1.6); pen.Freeze();
        double y = Math.Round((1 - Math.Clamp(_tick.At(now), 0, 1)) * (h - 2)) + 1;
        drawing.DrawLine(pen, new Point(w * .2, y), new Point(w * .8, y));
    }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Hover(true); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Hover(false); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); e.Handled = true; Clicked?.Invoke(); }
}

// Memory: the badge with its arc and the figure, then a bar of the memory in use and, lighter, the file cache, its color the
// level; under it a thin line, the committed memory against its limit. The words and the other figures are in the tooltip.
// Short of room (compact), the figure and the bar share one line.
internal sealed class MemoryBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly bool _compact;
    private readonly double _body, _caption;
    private Eased _used, _cached, _commit;
    private MemorySample? _memory;

    public MemoryBlock(MonitorColors colors, bool compact, double body, double caption)
    {
        _colors = colors; _compact = compact; _body = body; _caption = caption;
        Height = compact ? Math.Ceiling(body * 1.7) : Math.Ceiling(body * 1.7 + 6 + 5 + 5 + 3);
    }

    // For screen readers: "物理内存 63% · 正常", the memory in use as its value.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.ProgressBar, () => DrawnPeer.FirstLine(ToolTip),
        share: () => _memory?.UsedFraction);

    public void Set(Reading<MemorySample> reading, bool motion)
    {
        bool moving = false;
        if (reading.TryGet(out var memory))
        {
            moving |= _used.Set(memory.UsedFraction, motion); moving |= _cached.Set(memory.CachedFraction, motion); moving |= _commit.Set(memory.CommitFraction, motion);
            _memory = memory;
            ToolTip = Loc.T("物理内存 ", "Physical memory ") + MonitorFormat.Percent(memory.UsedFraction) + "% · " + Level(memory.Level) + "\n" + Loc.T("缓存 ", "Cached ") + MonitorFormat.Gigabytes(memory.Cached)
                + " GB\n" + Loc.T("已提交 ", "Committed ") + MonitorFormat.Gigabytes(memory.CommitUsed) + " / " + MonitorFormat.Gigabytes(memory.CommitLimit) + " GB" + Loc.T("（下方细线）", " (the thin line)");
        }
        else { _memory = null; ToolTip = Loc.T("暂无读数：", "No reading: ") + reading.Reason; }
        if (moving) Motion.Start(this, now => { InvalidateVisual(); return _used.Moving(now) || _cached.Moving(now) || _commit.Moving(now); }); else InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    private static string Level(MemoryLevel level) => level switch
    { MemoryLevel.Critical => Loc.T("紧张", "Critical"), MemoryLevel.Elevated => Loc.T("偏高", "Elevated"), _ => Loc.T("正常", "Normal") };

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now, line = Math.Ceiling(_body * 1.7), badge = Math.Round(_body + 5);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        string color = _memory is null ? UI.Tertiary : _colors.Memory;
        MonitorDraw.Dial(drawing, new Point(badge / 2, line / 2), badge, FigureGauge.DialRing(badge), FigureGauge.DialLetter(badge), _memory is null ? null : _used.At(now), color, "M", ppd);
        double x = badge + Math.Round(_body * .5), baseline = Math.Round(line / 2 + _body * .38);
        var figure = MonitorDraw.Text(_memory is { } mem ? MonitorFormat.Gigabytes(mem.Used) : "—", _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace;
        if (_memory is { } total)
        {
            var unit = MonitorDraw.Text("/ " + MonitorFormat.GigabytesCompact(total.Total) + " GB", _caption, UI.Tertiary, ppd);
            MonitorDraw.Baseline(drawing, unit, x + 3, baseline); x += 3 + unit.WidthIncludingTrailingWhitespace;
        }
        if (_compact)
        {
            // One line: the bar fills what the figures leave, with the commit charge as a thin line just under it, so it shows at
            // every size and not only where memory has a block of its own.
            double start = x + 10, end = w;
            if (end - start > 16)
            {
                double top = Math.Round(line / 2 - 4);
                Bar(drawing, new Rect(start, top, end - start, 4), color, now);
                Commit(drawing, new Rect(start, top + 4 + 3, end - start, 2), now);
            }
            return;
        }
        double barTop = line + 6;
        Bar(drawing, new Rect(0, barTop, w, 5), color, now);
        Commit(drawing, new Rect(0, barTop + 5 + 5, w, 2), now);
    }

    // The commit charge as a thin line under the bar, as ftop draws swap.
    private void Commit(DrawingContext drawing, Rect r, double now)
    {
        if (_memory is null) return;
        drawing.DrawRoundedRectangle(UI.Brush(UI.Track), null, r, 1, 1);
        double share = Math.Clamp(_commit.At(now), 0, 1);
        if (share > 0) drawing.DrawRoundedRectangle(UI.Brush(UI.Secondary), null, new Rect(r.X, r.Y, Math.Max(2, r.Width * share), 2), 1, 1);
    }

    // The memory in use, then the cache in the same color, lighter, then the free rest.
    private void Bar(DrawingContext drawing, Rect r, string color, double now)
    {
        double radius = r.Height / 2;
        drawing.DrawRoundedRectangle(UI.Brush(UI.Track), null, r, radius, radius);
        if (_memory is null) return;
        double used = Math.Clamp(_used.At(now), 0, 1) * r.Width, cached = Math.Clamp(_cached.At(now), 0, 1) * r.Width;
        if (cached > 1) drawing.DrawRoundedRectangle(UI.Brush(MonitorDraw.Faint(color, .38)), null, new Rect(r.X, r.Y, Math.Min(r.Width, used + cached), r.Height), radius, radius);
        if (used > 0) drawing.DrawRoundedRectangle(UI.Brush(color), null, new Rect(r.X, r.Y, Math.Max(r.Height, used), r.Height), radius, radius);
    }
}

// Download and upload: a badge with the arrow, the rate, and the last minute as a soft line, each scaled to its own highest
// reading: after the rate on a line (Lines, and Blocks too: a block of its own under the rate took twice the height for a line that
// says little more, and the core columns use it better), or not at all where both share a line with memory (Line).
internal sealed class NetworkBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly MetricsMode _mode;
    private readonly double _body, _caption;
    private NetworkSample? _rates;
    private IReadOnlyList<double> _down = [], _up = [];
    private string _reason = "";

    public NetworkBlock(MonitorColors colors, MetricsMode mode, double body, double caption)
    {
        _colors = colors; _mode = mode == MetricsMode.Blocks ? MetricsMode.Lines : mode; _body = body; _caption = caption;
        Height = Math.Ceiling(body * 1.7);
    }

    // For screen readers: "网络 · 下载 1.2 MB/s · 上传 85 KB/s".
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Text, () => Loc.T("网络 · ", "Network · ") + DrawnPeer.FirstLine(ToolTip));

    public void Set(Reading<NetworkSample> reading, IReadOnlyList<double> down, IReadOnlyList<double> up)
    {
        _rates = reading.OrDefault; _reason = reading.HasValue ? "" : reading.Reason; _down = down; _up = up;
        ToolTip = _rates is { } r ? Loc.T("下载 ", "Download ") + MonitorFormat.Rate(r.DownBytesPerSecond).Text + " · " + Loc.T("上传 ", "Upload ") + MonitorFormat.Rate(r.UpBytesPerSecond).Text
            : Loc.T("暂无读数：", "No reading: ") + _reason;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), line = Math.Ceiling(_body * 1.7), half = Math.Floor((w - 12) / 2);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        Half(drawing, 0, half, line, MonitorGlyph.Down, _rates?.DownBytesPerSecond, _down, _colors.Download, ppd);
        Half(drawing, half + 12, half, line, MonitorGlyph.Up, _rates?.UpBytesPerSecond, _up, _colors.Upload, ppd);
    }

    private void Half(DrawingContext drawing, double left, double width, double line, MonitorGlyph glyph, double? rate, IReadOnlyList<double> history, string color, double ppd)
    {
        double badge = Math.Round(_body + 5), baseline = Math.Round(line / 2 + _body * .38);
        // The arrow in the room a dial takes, as large against it as the collapsed strip draws it, so the figures line up with memory's.
        double arrow = Math.Round(badge * 9 / 16);
        MonitorDraw.Arrow(drawing, new Point(left + (badge - arrow) / 2, line / 2 - arrow / 2), arrow, Math.Max(1, arrow * .155), glyph, color);
        double x = left + badge + Math.Round(_body * .5);
        var q = rate is { } r ? MonitorFormat.Rate(r) : new MonitorFormat.Quantity("—", "");
        var number = MonitorDraw.Text(q.Number, _body + 1, UI.Primary, ppd, strong: true);
        MonitorDraw.Baseline(drawing, number, x, baseline);
        x += number.WidthIncludingTrailingWhitespace;
        if (q.Unit.Length > 0) { var unit = MonitorDraw.Text(q.Unit, _caption, UI.Tertiary, ppd); MonitorDraw.Baseline(drawing, unit, x + 3, baseline); x += 3 + unit.WidthIncludingTrailingWhitespace; }
        if (_mode == MetricsMode.Line || history.Count < 3) return;
        // The sparkline takes the rest of the half after the rate, centred on the line and as tall as the power line's curve (a drive
        // line less 4), so the page's small curves are one size. Measured before a Rect is made: a Rect does not take a negative size,
        // and a narrow half leaves none.
        double start = Math.Max(x + 10, left + width * .45);
        double areaWidth = left + width - start, areaHeight = Math.Min(line - 4, Math.Ceiling(_body * 1.5) - 4), areaTop = Math.Round((line - areaHeight) / 2);
        if (areaWidth >= 24 && areaHeight >= 6) Sparkline(drawing, new Rect(start, areaTop, areaWidth, areaHeight), history, color);
    }

    // The readings as a smooth line over a faint fill, newest at the right; scaled to the highest reading, at least 64 KB/s, so
    // an idle connection draws a flat line at the bottom rather than noise.
    private static void Sparkline(DrawingContext drawing, Rect area, IReadOnlyList<double> history, string color)
        => MonitorDraw.Sparkline(drawing, area, history, Math.Max(64_000, history.Max()), color, .16);


}

// The internal drives' volumes, an entry each (Storage.Rows) in lines tighter than the process list's rows (MonitorLayout.StorageRow),
// filled left to right in one or two columns: a drive glyph, the volume's letter, its space in use
// against its size, a bar of the space in use as memory's bar draws it, and, where its physical drive reports one, that drive's
// temperature at the end in its level's color (volumes on one drive show the same). The parts line up from entry to entry; the
// temperature takes room only while an entry has one, so no entry ends in a gap. With more volumes than places the last entry
// holds the others ("+2"). The bars ease to each reading, and the entries fade in when the first reading arrives.
internal sealed class StorageBlock : FrameworkElement
{
    // Between the columns.
    private const double ColumnGap = 20;
    private readonly MonitorColors _colors;
    private readonly double _body, _caption, _row;
    private readonly int _columns, _room;
    private readonly bool _temperatures;
    private IReadOnlyList<StorageRow> _rows = [];
    private Eased[] _used = [];
    private Eased _shown, _heat;

    // lines and columns: the places the page keeps for the volumes; temperatures: whether entries carry temperatures.
    public StorageBlock(MonitorColors colors, double body, double caption, double row, int lines, int columns, bool temperatures)
    {
        _colors = colors; _body = body; _caption = caption; _row = row; _columns = Math.Max(1, columns);
        _room = Math.Max(1, lines) * _columns; _temperatures = temperatures;
        Height = Math.Max(1, lines) * row;
        SnapsToDevicePixels = true;
    }

    // For screen readers: a list of the volumes' entries, each read as its line shows it; the entries lie as OnRender places them,
    // filled left to right across the columns.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.List, () => Loc.T("硬盘", "Drives"), parts: Parts);
    private IEnumerable<DrawnPart> Parts()
    {
        if (_shown.Target <= 0) yield break;
        double cell = Math.Floor((ActualWidth - ColumnGap * (_columns - 1)) / _columns);
        for (int i = 0; i < _rows.Count; i++)
            yield return new DrawnPart(i + " " + _rows[i].Label, Spoken(_rows[i]),
                new Rect(i % _columns * (cell + ColumnGap), i / _columns * _row, Math.Max(0, cell), _row));
    }
    // An entry as its line shows it: "C: · 246 / 300 GB · 44 °C".
    private static string Spoken(StorageRow row)
    {
        var (used, total, unit) = Storage.Sizes(row.Used, row.Total);
        return $"{row.Label} · {used} / {total} {unit}" + (row.Celsius is { } celsius ? " · " + Temperatures.Detail(celsius) : "");
    }

    public void Set(IReadOnlyList<StorageDrive> drives, bool motion)
    {
        var rows = Storage.Rows(drives, _room, _temperatures);
        // New entries start from an empty bar, so a first reading grows into place as the page's other values do.
        if (rows.Count != _used.Length) _used = new Eased[rows.Count];
        bool moving = false;
        for (int i = 0; i < rows.Count; i++) moving |= _used[i].Set(rows[i].UsedFraction, motion);
        if (rows.Count > 0) _rows = rows;
        moving |= _shown.Set(rows.Count > 0 ? 1 : 0, motion);
        moving |= _heat.Set(rows.Any(row => row.Celsius is not null) ? 1 : 0, motion);
        ToolTip = rows.Count == 0 ? null : string.Join("\n", drives.Select(Describe));
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
        // Once per reading, not per frame: the entries a screen reader lists are these.
        DrawnPeer.Refresh(this);
    }
    private bool Step(double now)
    {
        InvalidateVisual();
        if (_shown.Moving(now) || _heat.Moving(now)) return true;
        foreach (var used in _used) if (used.Moving(now)) return true;
        return false;
    }

    // "C: D: · YMTC YMSS2ED06D25MC · 212 / 477 GB · 44 °C", then each volume of a split drive on a line of its own.
    private static string Describe(StorageDrive drive)
    {
        var (used, total, unit) = Storage.Sizes(drive.Used, drive.Total);
        string text = drive.Letters + (drive.Model.Length > 0 ? " · " + drive.Model : "") + $" · {used} / {total} {unit}";
        if (drive.Temperature is { } heat)
            text += " · " + Temperatures.Detail(heat.Celsius) + (heat.Warning is { } warning ? Loc.T($"（警告 {Temperatures.Format(warning)} °C）", $" (warning at {Temperatures.Format(warning)} °C)") : "");
        if (drive.Volumes.Count > 1)
            foreach (var volume in drive.Volumes)
            {
                var (u, t, n) = Storage.Sizes(volume.Used, volume.Total);
                text += $"\n    {volume.Letter}{(volume.Label.Length > 0 ? " " + volume.Label : "")} · {u} / {t} {n}";
            }
        return text;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double now = Eased.Now, opacity = Math.Clamp(_shown.At(now), 0, 1);
        if (_rows.Count == 0 || opacity <= .001) return;
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), badge = Math.Round(_body + 5), space = Math.Round(_body * .7);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        if (opacity < .999) drawing.PushOpacity(opacity);
        // The columns: the letters, the figures (as wide as the widest size can be), and the temperature, as wide as its widest.
        var texts = _rows.Select(row =>
        {
            var (used, total, unit) = Storage.Sizes(row.Used, row.Total);
            return (Label: MonitorDraw.Text(row.Label, _body, UI.Secondary, ppd), Used: MonitorDraw.Text(used, _body + 1, UI.Primary, ppd, strong: true),
                Size: MonitorDraw.Text($" / {total} {unit}", _caption, UI.Tertiary, ppd), Widest: MonitorDraw.Text(total, _body + 1, UI.Primary, ppd, strong: true));
        }).ToList();
        double labels = texts.Max(t => t.Label.WidthIncludingTrailingWhitespace), figures = texts.Max(t => t.Widest.WidthIncludingTrailingWhitespace + t.Size.WidthIncludingTrailingWhitespace);
        var unitText = MonitorDraw.Text("°C", _caption, UI.Tertiary, ppd);
        double heatWidth = (MonitorDraw.Text("100", _body, UI.Primary, ppd, strong: true).WidthIncludingTrailingWhitespace + 2 + unitText.WidthIncludingTrailingWhitespace + space)
            * Math.Clamp(_heat.At(now), 0, 1);
        double glyph = Math.Round(badge * 9 / 16), labelX = badge + Math.Round(_body * .5), figureX = labelX + labels + space, barX = figureX + figures + space;
        double cell = Math.Floor((w - ColumnGap * (_columns - 1)) / _columns);
        double barEnd = Math.Max(barX, cell - heatWidth);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i]; var t = texts[i];
            double left = i % _columns * (cell + ColumnGap), top = i / _columns * _row, baseline = Math.Round(top + _row / 2 + _body * .36), middle = top + _row / 2;
            MonitorDraw.Arrow(drawing, new Point(left + (badge - glyph) / 2, middle - glyph / 2), glyph, Math.Max(1, glyph * .13), MonitorGlyph.Drive, UI.Secondary);
            MonitorDraw.Baseline(drawing, t.Label, left + labelX, baseline);
            MonitorDraw.Baseline(drawing, t.Used, left + figureX, baseline);
            MonitorDraw.Baseline(drawing, t.Size, left + figureX + t.Used.WidthIncludingTrailingWhitespace, baseline);
            if (barEnd - barX >= 16) Bar(drawing, new Rect(left + barX, Math.Round(middle - 2), barEnd - barX, 4), row, i, now);
            if (row.Celsius is { } celsius && heatWidth > 1)
            {
                var number = MonitorDraw.Text(Temperatures.Format(celsius), _body, _colors.Level(row.Level, UI.Primary), ppd, strong: true);
                double right = left + cell - unitText.WidthIncludingTrailingWhitespace;
                MonitorDraw.Baseline(drawing, unitText, right, baseline);
                MonitorDraw.Baseline(drawing, number, right - 2 - number.WidthIncludingTrailingWhitespace, baseline);
            }
        }
        if (opacity < .999) drawing.Pop();
    }

    // The space in use, as memory's bar is drawn.
    private void Bar(DrawingContext drawing, Rect r, StorageRow row, int index, double now)
    {
        double radius = r.Height / 2;
        drawing.DrawRoundedRectangle(UI.Brush(UI.Track), null, r, radius, radius);
        double used = Math.Clamp(index < _used.Length ? _used[index].At(now) : row.UsedFraction, 0, 1) * r.Width;
        if (used > .5) drawing.DrawRoundedRectangle(UI.Brush(_colors.Storage), null, new Rect(r.X, r.Y, Math.Max(r.Height, used), r.Height), radius, radius);
    }
}
