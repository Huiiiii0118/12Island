using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// The busiest apps, drawn as one element: a header with the sort switch, then rows in one or two columns. After ftop, a row
// that changes place by a step or two slides there; one that goes further, or comes new, fades in where it lands; a new sort
// reorders the rows the same way. The rows keep their order while the pointer is over the list (hold). The highlight under the
// pointer glides from row to row and fades in and out, the chosen sort's pill slides, a pressed row gives a little, and a click
// opens its card.
// Compact rows show the sorted figure; detailed rows show CPU and memory. The rest is on the app's card.
internal sealed class ProcessListView : FrameworkElement
{
    private const double ColumnGap = 18;
    private readonly MonitorLayout _layout;
    private ProcessSort _sort;
    private readonly MonitorProcessView _view;
    private readonly Action<ProcessSort> _sortChanged;
    private readonly Action<bool> _hold;
    private readonly Action<ProcessSample, Rect> _open;
    private IReadOnlyList<ProcessSample> _rows = [];
    private int _cores = 1;
    private string _message = "";
    // Per app: its place as a slot number (eased, so a short move slides) and its opacity (a new or far-moved row fades in).
    private readonly Dictionary<string, (Eased Slot, Eased Alpha)> _motion = [];
    private string? _hover, _pressed, _pressedShown;
    private int _hoverSort = -1;
    private Rect[] _sortRects = [];
    private Reading<ProcessList> _reading = Reading<ProcessList>.Unavailable("");
    // The highlight: its place as a slot (it glides between rows of a column) and its opacity; the pills' hover and the chosen
    // pill's place (0 for CPU, 1 for memory); how far a pressed row gives.
    private Eased _highlightSlot = new(0, Eased.Hover * 1.25), _highlight = new(0, Eased.Hover), _chosen, _press = new(0, Eased.Hover);
    private readonly Eased[] _pillHover = [new(0, Eased.Hover), new(0, Eased.Hover)];
    // Each row's drawing, kept while its figures and its width stay the same: during a move the rows are only placed again.
    private readonly Dictionary<string, DrawingCache<(ProcessSample, ProcessSort, bool, double, double, bool, double)>> _rowDrawings = [];

    public ProcessListView(MonitorLayout layout, ProcessSort sort, MonitorProcessView view, Action<ProcessSort> sortChanged, Action<bool> hold, Action<ProcessSample, Rect> open)
    {
        _layout = layout; _sort = sort; _view = view; _sortChanged = sortChanged; _hold = hold; _open = open;
        _chosen = new Eased((int)sort, Eased.Seconds * .75);
        AutomationProperties.SetName(this, Loc.T("进程列表", "Process list"));
        ClipToBounds = true;
    }

    private (int Rows, int Columns) Grid => MonitorLayout.ListGrid(ActualWidth, ActualHeight, _layout.RowHeight);

    // For screen readers: the two sort pills, then each app's row as it shows ("Chrome，CPU 12%，内存 1.2 GB"); invoking a row
    // opens its card, as a click does.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.List, () => Loc.T("进程列表", "Process list"), parts: Parts);
    private IEnumerable<DrawnPart> Parts()
    {
        string[] sorts = [Loc.T("按 CPU 排序", "Sort by CPU"), Loc.T("按内存排序", "Sort by memory")];
        for (int i = 0; i < _sortRects.Length && i < sorts.Length; i++)
        {
            var sort = (ProcessSort)i;
            yield return new DrawnPart("sort " + i, sorts[i] + ((int)_sort == i ? Loc.T("（当前）", " (current)") : ""), _sortRects[i], AutomationControlType.Button,
                () => { if (sort != _sort) _sortChanged(sort); });
        }
        var (rows, columns) = Grid;
        for (int i = 0; i < _rows.Count && i < rows * columns; i++)
        {
            var sample = _rows[i];
            var box = RowRect(i, rows, columns);
            if (box.Bottom > ActualHeight + .5) continue;
            yield return new DrawnPart(sample.Id, sample.Name + Loc.T("，CPU ", ", CPU ") + MonitorFormat.ProcessShare(sample.CpuPercent, _cores) + "%"
                + Loc.T("，内存 ", ", memory ") + MonitorFormat.ProcessMemory(sample.MemoryBytes), box, Invoke: () => _open(sample, box));
        }
    }
    private double ColumnWidth(int columns) => columns <= 1 ? ActualWidth : (ActualWidth - ColumnGap * (columns - 1)) / columns;

    // A new sort from another panel or from this one's pills: the rows reorder as a new reading would, and the pill slides.
    public void SetSort(ProcessSort sort, bool motion)
    {
        if (sort == _sort) return;
        _sort = sort;
        _chosen.Set((int)sort, motion);
        HoverPill(_hoverSort);
        Set(_reading, _cores, motion);
        Motion.Start(this, Step);
    }

    public void Set(Reading<ProcessList> reading, int cores, bool motion)
    {
        _reading = reading;
        _cores = Math.Max(1, cores);
        var (rows, columns) = Grid;
        int capacity = Math.Max(rows * columns, MonitorLayout.MaximumRows * MonitorLayout.MaximumColumns);
        if (!reading.TryGet(out var list)) { _rows = []; _message = Loc.T("正在读取进程…", "Reading processes…"); _motion.Clear(); InvalidateVisual(); DrawnPeer.Refresh(this); return; }
        var ranked = list.Ranked(_sort).Take(capacity).ToList();
        _message = ranked.Count == 0 ? Loc.T("没有进程", "No processes") : "";
        bool moving = false;
        var seen = new HashSet<string>();
        for (int slot = 0; slot < ranked.Count; slot++)
        {
            var id = ranked[slot].Id; seen.Add(id);
            if (_motion.TryGetValue(id, out var state))
            {
                double from = state.Slot.Target;
                // A short move within a column slides; a longer one, or one into another column, turns over in place.
                bool slide = rows > 0 && Math.Abs(slot - from) <= 2 && (int)from / rows == slot / rows;
                if (slide) moving |= state.Slot.Set(slot, motion);
                else if (Math.Abs(slot - from) > 1e-6) { state.Slot = new Eased(slot); state.Alpha = new Eased(0); moving |= state.Alpha.Set(1, motion); }
                _motion[id] = state;
            }
            else
            {
                var fresh = (Slot: new Eased(slot), Alpha: new Eased(_rows.Count == 0 ? 1 : 0));
                moving |= fresh.Alpha.Set(1, motion);
                _motion[id] = fresh;
            }
        }
        foreach (var gone in _motion.Keys.Where(id => !seen.Contains(id)).ToList()) { _motion.Remove(gone); _rowDrawings.Remove(gone); }
        _rows = ranked;
        if (_hover is not null) moving |= Highlight(_hover, motion);
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
        // Once per reading, not per frame: the rows a screen reader lists are these.
        DrawnPeer.Refresh(this);
    }
    private bool Step(double now)
    {
        InvalidateVisual();
        bool moving = _highlightSlot.Moving(now) || _highlight.Moving(now) || _chosen.Moving(now) || _press.Moving(now) || _pillHover[0].Moving(now) || _pillHover[1].Moving(now)
            || _motion.Values.Any(m => m.Slot.Moving(now) || m.Alpha.Moving(now));
        if (!moving && _pressed is null) _pressedShown = null;
        return moving;
    }

    // Moves the highlight to a row: it glides within a column and appears in place in another; with no row it fades out.
    private bool Highlight(string? id, bool motion)
    {
        int slot = -1;
        for (int i = 0; id is not null && i < _rows.Count; i++) if (_rows[i].Id == id) { slot = i; break; }
        if (slot < 0) return _highlight.Fade(false, motion);
        int rows = Grid.Rows;
        bool visible = _highlight.Target > .5 || _highlight.Moving(Eased.Now);
        bool glide = visible && rows > 0 && (int)Math.Round(_highlightSlot.Target) / rows == slot / rows;
        bool moving = _highlightSlot.Set(slot, motion && glide);
        return _highlight.Fade(true, motion) | moving;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    private bool Detailed(double columnWidth) => _view switch { MonitorProcessView.Detailed => columnWidth >= 200, MonitorProcessView.Compact => false, _ => columnWidth >= 300 };

    // A row's box from its slot.
    private Rect RowRect(double slot, int rows, int columns)
    {
        double width = ColumnWidth(columns);
        int column = rows > 0 ? Math.Clamp((int)Math.Floor(slot / rows + 1e-9), 0, columns - 1) : 0;
        double row = slot - column * rows;
        return new Rect(column * (width + ColumnGap), MonitorLayout.ListHead + row * _layout.RowHeight, width, _layout.RowHeight);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        Header(drawing, w, ppd);
        var (rows, columns) = Grid;
        if (_message.Length > 0 || rows == 0)
        {
            var text = MonitorDraw.Text(_message, _layout.Caption, UI.Tertiary, ppd);
            if (_message.Length > 0) drawing.DrawText(text, new Point((w - text.Width) / 2, MonitorLayout.ListHead + 6));
            return;
        }
        double columnWidth = ColumnWidth(columns);
        bool detailed = Detailed(columnWidth);
        double highlight = Math.Clamp(_highlight.At(now), 0, 1);
        if (highlight > .01)
        {
            var r = RowRect(_highlightSlot.At(now), rows, columns);
            drawing.PushOpacity(highlight);
            drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("rowHover")), null, new Rect(r.X - 4, r.Y + 1, r.Width + 8, r.Height - 2), 6, 6);
            drawing.Pop();
        }
        double give = Math.Clamp(_press.At(now), 0, 1) * .03;
        for (int i = 0; i < _rows.Count && i < rows * columns; i++)
        {
            var sample = _rows[i];
            var (slot, alpha) = _motion.TryGetValue(sample.Id, out var m) ? (m.Slot.At(now), m.Alpha.At(now)) : (i, 1d);
            var r = RowRect(slot, rows, columns);
            if (r.Bottom > h + .5) continue;
            if (alpha < .999) drawing.PushOpacity(Math.Clamp(alpha, 0, 1));
            bool pressed = _pressedShown == sample.Id && give > 0;
            if (pressed) drawing.PushTransform(new ScaleTransform(1 - give, 1 - give, r.X + r.Width / 2, r.Y + r.Height / 2));
            if (!_rowDrawings.TryGetValue(sample.Id, out var cache)) _rowDrawings[sample.Id] = cache = new();
            // The sample (a record) stands for its figures; the sort decides which figure leads.
            var key = (sample, _sort, detailed, r.Width, r.Height, ProcessIcons.For(sample.Path) is null, ppd);
            var row = new Rect(0, 0, r.Width, r.Height);
            drawing.PushTransform(new TranslateTransform(r.X, r.Y));
            drawing.DrawDrawing(cache.Get(key, context => Row(context, row, sample, detailed, ppd)));
            drawing.Pop();
            if (pressed) drawing.Pop();
            if (alpha < .999) drawing.Pop();
        }
    }

    private void Header(DrawingContext drawing, double w, double ppd)
    {
        // The sort pills name the list; the rows speak for themselves.
        double line = MonitorLayout.ListHead - 6, baseline = Math.Round(line / 2 + _layout.Caption * .38);
        // Two pills at the right: CPU and memory, the chosen one filled.
        string[] names = ["CPU", Loc.T("内存", "Memory")];
        var texts = names.Select(name => MonitorDraw.Text(name, _layout.Caption, UI.Secondary, ppd)).ToArray();
        double pad = 8, x = w;
        _sortRects = new Rect[2];
        for (int i = 1; i >= 0; i--)
        {
            double width = texts[i].WidthIncludingTrailingWhitespace + pad * 2;
            x -= width;
            _sortRects[i] = new Rect(x, 1, width, line - 2);
            x -= 2;
        }
        var strip = new Rect(_sortRects[0].X - 1, 0, _sortRects[1].Right - _sortRects[0].X + 2, line);
        drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, strip, line / 2, line / 2);
        double now = Eased.Now, radius = (line - 2) / 2;
        for (int i = 0; i < 2; i++)
        {
            double hover = Math.Clamp(_pillHover[i].At(now), 0, 1);
            if (hover <= .01) continue;
            drawing.PushOpacity(hover);
            drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("hover")), null, _sortRects[i], radius, radius);
            drawing.Pop();
        }
        // The chosen pill slides between the two, its width following.
        double at = Math.Clamp(_chosen.At(now), 0, 1);
        var pill = new Rect(_sortRects[0].X + (_sortRects[1].X - _sortRects[0].X) * at, 1, _sortRects[0].Width + (_sortRects[1].Width - _sortRects[0].Width) * at, line - 2);
        drawing.DrawRoundedRectangle(UI.Brush(UI.FillSelected), null, pill, radius, radius);
        for (int i = 0; i < 2; i++)
        {
            bool chosen = (int)_sort == i;
            var text = MonitorDraw.Text(names[i], _layout.Caption, chosen ? UI.Primary : UI.Secondary, ppd, strong: chosen);
            MonitorDraw.Baseline(drawing, text, _sortRects[i].X + pad, baseline);
        }
    }

    private void Row(DrawingContext drawing, Rect r, ProcessSample sample, bool detailed, double ppd)
    {
        double icon = Math.Min(16, r.Height - 5), baseline = Math.Round(r.Y + r.Height / 2 + _layout.Body * .36);
        var iconRect = new Rect(r.X, Math.Round(r.Y + (r.Height - icon) / 2), icon, icon);
        if (ProcessIcons.For(sample.Path) is { } image) drawing.DrawImage(image, iconRect);
        else
        {
            // Without an icon (a process this user cannot open) its first letter stands in, in a faint disc.
            drawing.DrawEllipse(UI.Brush(UI.Fill), null, new Point(iconRect.X + icon / 2, iconRect.Y + icon / 2), icon / 2, icon / 2);
            var letter = MonitorDraw.Text(sample.Name.Length > 0 ? char.ToUpperInvariant(sample.Name[0]).ToString() : "?", Math.Max(8, icon * .55), UI.Secondary, ppd, strong: true);
            drawing.DrawText(letter, new Point(iconRect.X + (icon - letter.Width) / 2, iconRect.Y + (icon - letter.Height) / 2));
        }
        string cpu = MonitorFormat.ProcessShare(sample.CpuPercent, _cores) + "%", memory = MonitorFormat.ProcessMemory(sample.MemoryBytes);
        double right = r.Right;
        void Figure(string text, bool primary, double slot)
        {
            var f = MonitorDraw.Text(text, _layout.Body, primary ? UI.Primary : UI.Tertiary, ppd);
            MonitorDraw.Baseline(drawing, f, right - f.WidthIncludingTrailingWhitespace, baseline);
            right -= slot;
        }
        if (detailed)
        {
            // How many processes an app has is on its card.
            Figure(memory, _sort == ProcessSort.Memory, Math.Ceiling(_layout.Body * 4.3));
            Figure(cpu, _sort == ProcessSort.Cpu, Math.Ceiling(_layout.Body * 4));
        }
        else Figure(_sort == ProcessSort.Memory ? memory : cpu, true, Math.Ceiling(_layout.Body * 4));
        // The name takes what the figures leave; each figure's slot is wider than its text.
        double nameLeft = iconRect.Right + 7;
        MonitorDraw.Baseline(drawing, MonitorDraw.Trimmed(sample.Name, _layout.Body, UI.Primary, Math.Max(10, right - nameLeft - 6), ppd), nameLeft, baseline);
    }

    // Whether the pointer is on an app's row: a click there opens or closes its card (Open), so the page leaves the card to it.
    public bool PointerOnRow => IsMouseOver && At(Mouse.GetPosition(this), out _) is not null;

    private ProcessSample? At(Point point, out Rect box)
    {
        box = Rect.Empty;
        var (rows, columns) = Grid;
        for (int i = 0; i < _rows.Count && i < rows * columns; i++)
        {
            var r = RowRect(i, rows, columns);
            if (r.Contains(point)) { box = r; return _rows[i]; }
        }
        return null;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hold(true); }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _hold(false);
        if (_hover is null && _pressed is null && _hoverSort < 0) return;
        _hover = _pressed = null; Cursor = null;
        HoverPill(-1);
        _highlight.Fade(false, Motion.Enabled); _press.Set(0, Motion.Enabled);
        Motion.Start(this, Step);
    }
    private void HoverPill(int sort)
    {
        for (int i = 0; i < 2; i++) _pillHover[i].Fade(i == sort && i != (int)_sort, Motion.Enabled);
        _hoverSort = sort;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        int sort = Array.FindIndex(_sortRects, r => r.Contains(point));
        string? hover = sort < 0 ? At(point, out _)?.Id : null;
        Cursor = sort >= 0 || hover is not null ? Cursors.Hand : null;
        if (hover == _hover && sort == _hoverSort) return;
        _hover = hover;
        HoverPill(sort);
        Highlight(hover, Motion.Enabled);
        Motion.Start(this, Step);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (Array.FindIndex(_sortRects, r => r.Contains(point)) is var sort and >= 0) { e.Handled = true; if (sort != (int)_sort) _sortChanged((ProcessSort)sort); return; }
        if (At(point, out _) is { } sample) { _pressed = _pressedShown = sample.Id; _press.Set(1, Motion.Enabled); Motion.Start(this, Step); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed is null) return;
        var sample = At(e.GetPosition(this), out var box);
        bool same = sample?.Id == _pressed;
        _pressed = null; _press.Set(0, Motion.Enabled); Motion.Start(this, Step); e.Handled = true;
        if (same && sample is not null) _open(sample, box);
    }
}

// An app's card, opened from its row: its icon and name, where it runs from, its CPU and memory, how many processes it has and
// the busiest of them; for an app with a window, a button that brings it to the front. It grows in from just smaller than
// itself, follows the readings while open, and closes on a click.
internal sealed class ProcessCard : Border
{
    private readonly TextBlock _name, _where, _cpu, _memory, _count;
    private readonly StackPanel _members;
    private readonly Image _icon;
    private readonly Button _switch;
    private string? _executable;
    private const long WindowCheck = 3000;
    private long _windowCheckedAt;
    public string Id { get; private set; } = "";
    // The app's window was brought to the front from the card.
    public event Action? Switched;

    private string _spoken = "";

    // For screen readers: the card's app and its figures; the busiest processes follow as its text shows them.
    protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Group,
        () => Loc.T("进程详情：", "Process details: ") + _spoken);

    public ProcessCard(double body, double caption)
    {
        Width = 248; Padding = new Thickness(14, 12, 14, 12); CornerRadius = new CornerRadius(PanelTheme.Corner(12));
        Background = UI.Brush(PanelTheme.Hex("popup")); BorderBrush = UI.Brush(PanelTheme.Hex("popupBorder")); BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand; AutomationProperties.SetName(this, Loc.T("进程详情", "Process details"));
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        // At the top right, beside the name, which gives up the room it takes.
        _switch = new Button { Content = Loc.T("打开", "Open"), FontFamily = UI.PanelFont, FontSize = caption, Height = 22, Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        _switch.SetResourceReference(StyleProperty, "PanelOutlineButton");
        AutomationProperties.SetName(_switch, Loc.T("打开应用窗口", "Open the app's window"));
        // Looked up again on the click, since the window may have changed since the card last looked; synchronous on purpose:
        // the foreground can only be handed over while this click is being handled.
        _switch.Click += (_, _) => { if (ProcessWindows.Activate(ProcessWindows.Find(_executable))) Switched?.Invoke(); };
        DockPanel.SetDock(_switch, Dock.Right); head.Children.Add(_switch);
        _icon =new Image { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        head.Children.Add(_icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _name = UI.Line("", body + 1, UI.Primary, FontWeights.SemiBold); _name.FontFamily = UI.PanelFont;
        _where = UI.Line("", caption, UI.Tertiary); _where.FontFamily = UI.PanelFont;
        titles.Children.Add(_name); titles.Children.Add(_where); head.Children.Add(titles);
        stack.Children.Add(head);
        var figures = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        (_cpu, _memory, _count) = (Cell(figures, "CPU", body, caption), Cell(figures, Loc.T("内存", "Memory"), body, caption), Cell(figures, Loc.T("进程", "Processes"), body, caption));
        stack.Children.Add(figures);
        _members = new StackPanel();
        stack.Children.Add(_members);
        Child = stack;
        RenderTransformOrigin = new Point(.5, .5);
        RenderTransform = new ScaleTransform(1, 1);
    }

    private static TextBlock Cell(Panel parent, string caption, double body, double small)
    {
        var cell = new StackPanel();
        var label = UI.Line(caption, small, UI.Tertiary); label.FontFamily = UI.PanelFont;
        var value = UI.Centered("", body + 2, UI.Primary, FontWeights.SemiBold, UI.DisplayFont);
        cell.Children.Add(label); cell.Children.Add(value); parent.Children.Add(cell);
        return value;
    }

    public void Show(ProcessSample sample, int cores, double body, double caption)
    {
        Id = sample.Id;
        // The button only for an app that has a window to show: not for background processes or an app left in the tray.
        // Looked for as the card opens and then every few seconds, not with every reading: it lists every window.
        string executable = sample.AppKey ?? System.IO.Path.GetFileName(sample.Path) ?? "";
        if (executable != _executable || Environment.TickCount64 - _windowCheckedAt >= WindowCheck)
        {
            _executable = executable; _windowCheckedAt = Environment.TickCount64;
            _switch.Visibility = ProcessWindows.Find(_executable) != 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _icon.Source = ProcessIcons.For(sample.Path); _icon.Visibility = _icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _name.Text = sample.Name;
        _where.Text = sample.Path ?? (sample.AppKey ?? "") + " · PID " + sample.Pid;
        _where.ToolTip = sample.Path;
        _cpu.Text = MonitorFormat.ProcessShare(sample.CpuPercent, cores) + "%";
        _memory.Text = MonitorFormat.ProcessMemory(sample.MemoryBytes);
        _count.Text = Math.Max(1, sample.Members.Count).ToString();
        _spoken = sample.Name + " · CPU " + _cpu.Text + Loc.T(" · 内存 ", " · memory ") + _memory.Text + Loc.T($" · {_count.Text} 个进程", $" · {_count.Text} processes");
        _members.Children.Clear();
        // The busiest of the app's processes, up to five.
        foreach (var member in sample.Members.Take(5))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var mem = UI.Line(MonitorFormat.ProcessMemory(member.MemoryBytes), caption, UI.Tertiary); mem.FontFamily = UI.PanelFont; mem.Width = 44; mem.TextAlignment = TextAlignment.Right;
            var cpu = UI.Line(MonitorFormat.ProcessShare(member.CpuPercent, cores) + "%", caption, UI.Secondary); cpu.FontFamily = UI.PanelFont; cpu.Width = 44; cpu.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(mem, Dock.Right); DockPanel.SetDock(cpu, Dock.Right);
            row.Children.Add(mem); row.Children.Add(cpu);
            var name = UI.Line(member.Name, caption, UI.Secondary); name.FontFamily = UI.PanelFont; row.Children.Add(name);
            _members.Children.Add(row);
        }
    }

    public void Appear(bool motion)
    {
        if (!motion) return;
        BeginAnimation(OpacityProperty, Soft.Fade(0, 1, Soft.Quick));
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Soft.Fade(.94, 1, Soft.Short));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Soft.Fade(.94, 1, Soft.Short));
    }

    // Leaves the way it came, a little quicker; done is called once it is gone.
    public void Leave(Action done)
    {
        IsHitTestVisible = false;
        var fade = Soft.Fade(Opacity, 0, Soft.Quick, Soft.InOut);
        fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Soft.Fade(scale.ScaleX, .96, Soft.Quick, Soft.InOut));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Soft.Fade(scale.ScaleY, .96, Soft.Quick, Soft.InOut));
    }
}

// The icons of executables, read once per path on the sampler's thread (Prepare) and taken from the cache on the UI thread
// (For), which never waits for the shell. A failed read is remembered as no icon.
internal static class ProcessIcons
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => Cache.Clear();
    public static ImageSource? For(string? path) => !string.IsNullOrEmpty(path) && Cache.TryGetValue(path, out var icon) ? icon : null;

    public static void Prepare(string? path)
    {
        if (string.IsNullOrEmpty(path) || Cache.ContainsKey(path)) return;
        ImageSource? icon = null;
        var info = new ShellFileInfo();
        // The 32 px icon is drawn at 16 DIP, so it stays sharp on scaled displays.
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); icon = source;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { }
            finally { DestroyIcon(info.Icon); }
        }
        // Programs come and go over days; the cache is bounded rather than tracked.
        if (Cache.Count >= 512) Cache.Clear();
        Cache[path] = icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
