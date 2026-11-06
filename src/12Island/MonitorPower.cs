using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// What the page needs to build the power lines: the look of the theme, the low-battery threshold and whether this machine has a battery.
// Quick: the battery as the system itself says it at once (BatteryProvider.Quick), shown until the sampler's first reading arrives.
// Animate: the page is being opened for the first time (not rebuilt for a new size), so the rows come in.
internal sealed record PowerOptions(PowerStyle Style, int Low, bool Battery, Reading<BatterySample> Quick = default, bool Animate = false);

// The whole machine's power draw and its own battery, under the drives on the monitor page. As memory and network are drawn, it is one
// element that renders directly: a new reading changes what it draws, never the layout, and the battery's fill eases to each reading.
//
// It is one line, laid out as the page's other lines are (memory, network, the drives): a glyph in the room a dial takes, then the
// figure right-aligned in the room of its widest and its small unit after it, so the unit does not move. The power: a bolt, the watts,
// the name of what they are of ("整机", "CPU", "GPU"; a click on the power part switches), the last minute of it as a curve that fills
// the line, and its mean since the page opened at the curve's end. On a machine with a battery,
// the battery closes the line as one group: the time left or to full where there is one ("剩余 1:42", "充满 0:40"), its drawing (the
// gauge, with a plug at its corner on mains), and the charge, its "%" ending where the drives' "°C" ends; the curve ends before it.
// Where the figure comes from, the parts, the rate, the power mode, Battery saver and the health are in the tooltips, one for the power
// and one for the battery (PowerText), each shown over its part.
// The parts come in as the page opens (PowerOptions.Animate), whether or not a reading is there yet: each rises a few DIP into place as
// it fades in, the battery a beat after the power; the battery's fill eases to its reading and the curve is drawn from left to right.
// The figures show each reading as it comes, as the page's other figures do. Before the first reading they are dashes.
internal sealed class PowerBlock : FrameworkElement
{
    private readonly MonitorColors _colors;
    private readonly PowerOptions _options;
    private readonly double _body, _caption, _row;
    // The rows' entrance, the battery's fill, and how far the curve is drawn.
    private Eased _fill, _appear, _reveal;
    private bool _entered, _hasCurve, _overBattery;
    // Where the battery's group began when last drawn: the power's tooltip is left of it, the battery's from it on.
    private double _batteryLeft = double.PositiveInfinity;
    private Reading<PowerSample> _power;
    private Reading<BatterySample> _battery;
    private IReadOnlyList<double> _history = [];
    private PowerSummary? _summary;
    private PowerTrack _cpu = PowerTrack.None, _gpu = PowerTrack.None;
    // What the line shows: the whole machine, the CPU or the GPU, switched by a click on the power part. Kept for the session, so a page
    // built again (a new size) shows the same.
    private static PowerView _view = PowerView.Whole;
    private string _spoken = "";
    // The watts that reach the top of the curve, worked out once per reading.
    private double _top = 10;

    public PowerBlock(MonitorColors colors, PowerOptions options, double body, double caption, double row)
    {
        _colors = colors; _options = options; _body = body; _caption = caption; _row = row;
        Height = row;
        SnapsToDevicePixels = true;
        _battery = options.Quick;
        ToolTip = _tip;
        ToolTipOpening += (_, _) => Tip(opening: true);
    }

    // For screen readers: the items in one line (PowerText.Spoken).
    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, () => _spoken);

    private PowerStyle Skin => _options.Style;
    private bool Shared => _options.Battery;

    public void Set(MonitorSnapshot snapshot, bool motion)
    {
        _power = snapshot.Power; _history = snapshot.PowerHistory; _summary = snapshot.PowerSummary; _cpu = snapshot.CpuPower; _gpu = snapshot.GpuPower;
        // The sampler's battery reading once it has one, the system's own until then.
        _battery = snapshot.Battery.HasValue || !_options.Quick.HasValue ? snapshot.Battery : _options.Quick;
        _top = Top(Shown().History);
        bool moving = false;
        // The entrance runs as the page is opened, with or without a reading; a page rebuilt for a new size shows its rows at once.
        if (!_entered) { _entered = true; moving |= _appear.Set(1, _options.Animate); }
        // The battery's fill fills up from nothing while the page opens, then eases from reading to reading as the page's gauges do.
        bool ease = motion || _options.Animate;
        if (_battery.TryGet(out var sample)) moving |= _fill.Set(sample.Percent / 100, ease);
        if (!_hasCurve && Shown().History.Count >= 3) { _hasCurve = true; moving |= _reveal.Set(1, ease); }
        Tip();
        string spoken = PowerText.Spoken(_power, _battery, _options.Battery);
        if (spoken != _spoken)
        {
            string old = _spoken; _spoken = spoken;
            // A screen reader hears the new reading: the automation name changed (it is asked for the name only on focus otherwise).
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) && UIElementAutomationPeer.FromElement(this) is { } peer)
                peer.RaisePropertyChangedEvent(System.Windows.Automation.AutomationElementIdentifiers.NameProperty, old, spoken);
        }
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
    }
    // One frame of the entrance, the fill and the curve: drawn again while anything moves, and no callback after.
    private bool Step(double now) { InvalidateVisual(); return _fill.Moving(now) || _appear.Moving(now) || _reveal.Moving(now); }

    // The watts that reach the top of the curve.
    private static double Top(IReadOnlyList<double> history) => history.Count == 0 ? 10 : Math.Max(10, history.Max() * 1.15);

    // What the line shows now: the figure (none where the view's part was not read this time), its last minute, and its peak and mean.
    private (double? Watts, IReadOnlyList<double> History, PowerSummary? Summary) Shown()
    {
        var sample = _power.OrDefault;
        return _view switch
        {
            PowerView.Cpu => (sample is null ? null : PowerResolver.PartWatts(sample, "cpu"), _cpu.History, _cpu.Summary),
            PowerView.Gpu => (sample is null ? null : PowerResolver.PartWatts(sample, "gpu"), _gpu.History, _gpu.Summary),
            _ => (sample?.Watts, _history, _summary)
        };
    }

    // The views this machine has: the whole machine always; the CPU and the GPU once they have been read (a GPU at rest is not read, but
    // is listed as not read, and can be chosen to see why).
    private bool Has(PowerView view) => view switch
    {
        PowerView.Cpu => _cpu.History.Count > 0 || _power.OrDefault is { } s && PowerResolver.PartWatts(s, "cpu") is not null,
        PowerView.Gpu => _gpu.History.Count > 0 || _power.OrDefault is { } g && (PowerResolver.PartWatts(g, "gpu") is not null || g.Missing.Count > 0),
        _ => true
    };
    private IEnumerable<PowerView> Views => Enum.GetValues<PowerView>().Where(Has);

    // One tooltip whose content follows the readings, so an open one shows each new reading: the power's table or the battery's lines,
    // whichever part is under the pointer. Built only as it opens and while it is open, not for every reading nobody looks at.
    private readonly ToolTip _tip = new() { MaxWidth = 640 };
    private void Tip(bool opening = false)
    {
        if (!opening && !_tip.IsOpen) return;
        if (_overBattery && _options.Battery) _tip.Content = PowerText.Battery(_battery);
        else _tip.Content = PowerText.Table(_power, _summary, _cpu, _gpu) is { } table ? TableView(table) : PowerText.NoPower(_power.Reason);
    }

    // The power's table: the column names over the figures, right-aligned in their columns; "≈" and "+" between the current figures
    // where they add up; the notes under it. Labels and signs in the tooltip's color, dimmed.
    private static UIElement TableView(PowerTable table)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        int rows = 1 + table.Rows.Count;
        for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(string text, int row, int column, bool dim, HorizontalAlignment align, Thickness margin, int span = 1)
        {
            var block = new TextBlock { Text = text, HorizontalAlignment = align, Margin = margin, Opacity = dim ? .65 : 1 };
            System.Windows.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            Grid.SetRow(block, row); Grid.SetColumn(block, column); Grid.SetColumnSpan(block, span);
            grid.Children.Add(block);
        }
        // The value of column i sits in grid column 2i + 1, its sign before it in 2i.
        for (int i = 0; i < table.Columns.Count; i++) Put(table.Columns[i], 0, 2 * i + 1, true, HorizontalAlignment.Right, new Thickness(12, 0, 0, 0));
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            Put(row.Label, r + 1, 0, true, HorizontalAlignment.Left, new Thickness(0));
            for (int i = 0; i < row.Cells.Count; i++)
            {
                Put(row.Cells[i], r + 1, 2 * i + 1, false, HorizontalAlignment.Right, new Thickness(12, 0, 0, 0));
                if (r == 0 && table.Sum && i > 0) Put(i == 1 ? "≈" : "+", r + 1, 2 * i, true, HorizontalAlignment.Center, new Thickness(8, 0, 0, 0));
            }
        }
        // Notes must not participate in the table's auto column sizing: a long adapter
        // name used to widen every column and push the GPU beyond the tooltip's edge.
        var content = new StackPanel();
        content.Children.Add(grid);
        for (int n = 0; n < table.Notes.Count; n++)
            content.Children.Add(new TextBlock
            {
                Text = table.Notes[n], TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                HorizontalAlignment = HorizontalAlignment.Left, Opacity = .65,
                Margin = new Thickness(0, n == 0 ? 4 : 0, 0, 0)
            });
        return content;
    }

    private bool OverBattery(Point point) => Shared && point.X >= _batteryLeft - Gap / 2;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = OverBattery(e.GetPosition(this));
        Cursor = over ? null : Cursors.Hand;
        if (over == _overBattery) return;
        _overBattery = over;
        Tip();
    }
    // A click on the power part shows the next view this machine has: whole machine, CPU, GPU, then the whole machine again.
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (OverBattery(e.GetPosition(this))) return;
        e.Handled = true;
        var views = Views.ToList();
        _view = views[(views.IndexOf(_view) + 1) % views.Count];
        _top = Top(Shown().History);
        Tip();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Height);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private double Badge => Math.Round(_body + 5);
    private double Space => Math.Round(_body * .7);
    // Between the power's mean and the battery's group.
    private double Gap => Math.Round(_body * 1.5);

    // The battery's drawing, tall enough that the plug of the mains and the mark of a low battery fit inside it at every scale (PowerGlyphs
    // draws them inside a body at least 8 DIP high, as in Windows' own battery icon) rather than in a tip too small to read.
    private double BatteryHeight(double ppd) => Math.Round(Math.Max(14, _body + 2) * ppd) / ppd;
    private double BatteryWidth(double ppd) => Math.Round(BatteryHeight(ppd) * 2.1);

    // Where the power's figure and curve begin, the room kept for the mean, and the room of a figure and its unit; from the widest thing
    // each can hold, so nothing moves when a figure changes length. Made once per scale.
    private readonly record struct Columns(double Figure, double Middle, double Mean, double Number, double Unit, double Label);
    private (double Ppd, Columns Columns)? _columns;
    private Columns ColumnsAt(double ppd)
    {
        if (_columns is { } known && known.Ppd == ppd) return known.Columns;
        double Width(string text, double size, bool strong = false) => Math.Ceiling(MonitorDraw.Text(text, size, UI.Primary, ppd, strong: strong).WidthIncludingTrailingWhitespace);
        double figure = Badge + Math.Round(_body * .5);
        double number = Width("8888", _body + 1, strong: true);
        double unit = Math.Max(Width("W", _caption), Width("%", _caption));
        // The view's name after the unit, in the room of the widest, so the curve does not move when the view changes.
        double label = figure + number + 3 + unit + Math.Round(_body * .5), names = Enum.GetValues<PowerView>().Max(view => Width(PowerText.ViewName(view), _caption));
        var columns = new Columns(figure, label + names + Space, Width(PowerText.Mean(8888), _caption), number, unit, label);
        _columns = (ppd, columns);
        return columns;
    }

    // The battery's group at the right end of the line, from the right: "%" ending where the drives' "°C" ends, the charge right-aligned
    // against it as the temperatures are, the drawing just before the figure (it moves only when the charge gains or loses a digit),
    // and the time left or to full, where there is one, before the drawing. Left is where the group begins.
    private readonly record struct BatteryPlace(double Left, double Drawing, double NumberRight, double Unit, double Words, string Text);
    private BatteryPlace BatteryPlaces(Rect line, Columns c, double ppd)
    {
        string words = _battery.TryGet(out var battery) ? PowerText.Brief(battery) : "";
        double unit = line.Right - MonitorDraw.Text("%", _caption, UI.Tertiary, ppd).WidthIncludingTrailingWhitespace, numberRight = unit - 2;
        string charge = _battery.OrDefault is { } shown ? MonitorFormat.Percent(shown.Percent / 100) : "—";
        double figure = MonitorDraw.Text(charge, _body + 1, UI.Primary, ppd, strong: true).WidthIncludingTrailingWhitespace;
        double drawing = numberRight - figure - 5 - BatteryWidth(ppd);
        double wordsLeft = words.Length > 0 ? drawing - Space - MonitorDraw.Text(words, _caption, UI.Secondary, ppd).WidthIncludingTrailingWhitespace : drawing;
        return new(wordsLeft, drawing, numberRight, unit, wordsLeft, words);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var c = ColumnsAt(ppd);
        double appear = Math.Clamp(_appear.At(now), 0, 1);
        if (appear <= .001) return;
        // Each part rises into its place as it fades in, the battery a beat after the power.
        double first = Clamp01(appear), second = Clamp01((appear - .22) / .78);
        void Row(double t, Action draw)
        {
            if (t <= .001) return;
            if (t >= .999) { draw(); return; }
            drawing.PushOpacity(t); drawing.PushTransform(new TranslateTransform(0, (1 - t) * 6));
            draw();
            drawing.Pop(); drawing.Pop();
        }
        var line = new Rect(0, 0, w, _row);
        double end = w;
        if (Shared)
        {
            var places = BatteryPlaces(line, c, ppd);
            _batteryLeft = places.Left; end = Math.Max(0, places.Left - Gap);
            Row(second, () => Battery(drawing, line, ppd, places, now));
        }
        Row(first, () => Power(drawing, new Rect(0, 0, end, _row), ppd, c, now));
    }

    private double BaselineOf(Rect line) => Math.Round(line.Y + line.Height / 2 + _body * .38);

    // A figure right-aligned at right, and its unit at unitX: the unit keeps its place whatever the figure's length.
    private void FigureAt(DrawingContext drawing, string figure, string unit, string color, Rect line, double ppd, double right, double unitX)
    {
        double baseline = BaselineOf(line);
        var number = MonitorDraw.Text(figure, _body + 1, color, ppd, strong: true);
        MonitorDraw.Baseline(drawing, number, right - number.WidthIncludingTrailingWhitespace, baseline);
        if (unit.Length > 0) MonitorDraw.Baseline(drawing, MonitorDraw.Text(unit, _caption, UI.Tertiary, ppd), unitX, baseline);
    }

    // A short text whose right edge is the line's.
    private void RightText(DrawingContext drawing, string text, string color, Rect line, double ppd)
    {
        var formatted = MonitorDraw.Text(text, _caption, color, ppd);
        MonitorDraw.Baseline(drawing, formatted, line.Right - formatted.WidthIncludingTrailingWhitespace, BaselineOf(line));
    }

    // The power: the bolt, the watts of the view, its name, its curve and its mean.
    private void Power(DrawingContext drawing, Rect line, double ppd, Columns c, double now)
    {
        bool known = _power.HasValue;
        var (watts, history, summary) = Shown();
        // The bolt in a square (PowerIcons draws in the shorter side) larger than a dial's room: the bolt is narrower than tall.
        double size = Math.Round(Badge * 1.05);
        PowerIcons.Draw(drawing, PowerIcon.Bolt, new Rect(line.X + Math.Round((Badge - size) / 2), Math.Round(line.Y + (line.Height - size) / 2), size, size),
            known ? Skin.Draw : UI.Tertiary, Skin, ppd);
        double right = line.X + c.Figure + c.Number;
        FigureAt(drawing, watts is { } figure ? PowerFormat.Compact(figure, 9999) : "—", watts is null ? "" : "W", UI.Primary, line, ppd, right, right + 3);
        MonitorDraw.Baseline(drawing, MonitorDraw.Text(PowerText.ViewName(_view), _caption, UI.Secondary, ppd), line.X + c.Label, BaselineOf(line));
        if (!known) return;
        double curveLeft = line.X + c.Middle, curveRight = line.Right;
        // The mean keeps its room from the start, so the curve does not shrink when it appears; a line too short for both has the curve only.
        if (curveRight - c.Mean - Space - curveLeft >= 40)
        {
            curveRight -= c.Mean + Space;
            if (summary is { Count: > 2 } kept) RightText(drawing, PowerText.Mean(kept.Average), UI.Tertiary, line, ppd);
        }
        if (curveRight - curveLeft >= 40 && history.Count >= 3)
        {
            // Drawn from the left as the page opens.
            double reveal = Clamp01(_reveal.At(now));
            if (reveal < .999) drawing.PushClip(new RectangleGeometry(new Rect(curveLeft, line.Y, (curveRight - curveLeft) * reveal, line.Height)));
            PowerGlyphs.Curve(drawing, new Rect(curveLeft, line.Y + 2, curveRight - curveLeft, line.Height - 4), history, _top, Skin);
            if (reveal < .999) drawing.Pop();
        }
    }

    // The battery: the time left or to full where there is one, its drawing (the gauge; no bar beside it), with the plug at its corner
    // while it is on mains, the leaf while Battery saver is on and an exclamation mark when low; then the charge.
    private void Battery(DrawingContext drawing, Rect line, double ppd, BatteryPlace place, double now)
    {
        double height = BatteryHeight(ppd), mid = line.Y + line.Height / 2;
        bool known = _battery.TryGet(out var battery);
        var visual = known ? BatteryVisual.Of(battery!, _options.Low) : new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal);
        double share = Clamp01(_fill.At(now));
        if (place.Text.Length > 0) MonitorDraw.Baseline(drawing, MonitorDraw.Text(place.Text, _caption, UI.Secondary, ppd), place.Words, BaselineOf(line));
        PowerGlyphs.Battery(drawing, new Rect(place.Drawing, Math.Round((mid - height / 2) * ppd) / ppd, BatteryWidth(ppd), height), visual, Skin, ppd, share, compact: false, unknown: !known);
        FigureAt(drawing, known ? MonitorFormat.Percent(battery!.Percent / 100) : "—", known ? "%" : "", known ? _colors.Level(visual.Level, UI.Primary) : UI.Primary, line, ppd, place.NumberRight, place.Unit);
    }
}

// What a screen reader gets from a drawn element: one text with the name, since the element draws its readings instead of holding controls.
internal sealed class SpokenPeer : FrameworkElementAutomationPeer
{
    private readonly Func<string> _name;
    public SpokenPeer(FrameworkElement owner, Func<string> name) : base(owner) => _name = name;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override string GetNameCore() => _name();
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
}
