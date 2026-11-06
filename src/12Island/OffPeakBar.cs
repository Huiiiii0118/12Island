using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TwelveIsland;

// One off-peak period on a day drawn from noon to noon, midnight in the middle: the period is the stretch between two
// handles. Dragging moves the nearer handle in 15-minute steps; with the bar focused, Left and Right move the active
// handle and Up and Down switch handles. Positions are minutes after noon, 0 to 1440; Start and End are minutes after
// midnight, as OffPeakPeriod keeps them.
internal sealed class OffPeakBar : FrameworkElement
{
    public const int Step = 15;
    private const int Day = 24 * 60;
    private const double Knob = 14, Line = 4, Middle = 9;
    private int _start, _end, _active;
    private bool _dragging;
    public Brush Track { get; init; } = Brushes.Gray;
    public Brush Range { get; init; } = Brushes.Teal;
    public Brush Knobs { get; init; } = Brushes.White;
    public Brush Outline { get; init; } = Brushes.Gray;
    public Brush FocusRing { get; init; } = Brushes.DodgerBlue;
    public Brush Scale { get; init; } = Brushes.Gray;
    // Moved reports each step while dragging; Committed reports the period once the handle is let go.
    public event Action? Moved;
    public event Action? Committed;
    public int Start => Time(_start);
    public int End => Time(_end);

    public OffPeakBar(int start, int end)
    {
        _start = Position(start); _end = Position(end);
        // An end at noon is the right edge, and a period that covers the whole day runs from edge to edge.
        if (_end == 0 && _start > 0) _end = Day;
        else if (_start == _end) { _start = 0; _end = Day; }
        Height = Middle + Knob / 2 + 15; MinWidth = 220; Focusable = true; Cursor = Cursors.Hand; FocusVisualStyle = null;
    }
    private static int Position(int time) => (time + Day / 2) % Day;
    private static int Time(int position) => (position + Day / 2) % Day;
    // A period from before noon to after it (saved from text entry) shows as two pieces until a handle is moved.
    private bool Wrapped => _end < _start;
    private double X(int position) => Knob / 2 + position / (double)Day * Math.Max(0, ActualWidth - Knob);
    private int Snap(double x)
    {
        double share = (x - Knob / 2) / Math.Max(1, ActualWidth - Knob);
        return Math.Clamp((int)Math.Round(share * Day / Step) * Step, 0, Day);
    }
    // A handle stays at least one step from the other, so a period never shrinks to nothing.
    private void Move(int handle, int position)
    {
        if (handle == 0) position = Math.Clamp(position, 0, (Wrapped ? Day : _end) - Step);
        else position = Math.Clamp(position, Wrapped ? Step : _start + Step, Day);
        if (handle == 0 && Wrapped) _end = Day;
        if (handle == 1 && Wrapped) _start = 0;
        if (handle == 0) _start = position; else _end = position;
        InvalidateVisual(); Moved?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        double x = e.GetPosition(this).X;
        double toStart = Math.Abs(x - X(_start)), toEnd = Math.Abs(x - X(_end));
        _active = toEnd < toStart || toEnd == toStart && x > X(_end) ? 1 : 0;
        _dragging = CaptureMouse();
        Move(_active, Snap(x));
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Move(_active, Snap(e.GetPosition(this).X));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false; ReleaseMouseCapture(); Committed?.Invoke();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) { _dragging = false; Committed?.Invoke(); }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Move(_active, (_active == 0 ? _start : _end) - Step); Committed?.Invoke(); break;
            case Key.Right: Move(_active, (_active == 0 ? _start : _end) + Step); Committed?.Invoke(); break;
            case Key.Up or Key.Down: _active = 1 - _active; InvalidateVisual(); break;
            default: return;
        }
        e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        // A transparent fill makes the whole bar answer the mouse, not just the drawn parts.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));
        double left = X(0), right = X(Day);
        dc.DrawRoundedRectangle(Track, null, new Rect(left, Middle - Line / 2, right - left, Line), Line / 2, Line / 2);
        void Piece(int from, int to) => dc.DrawRectangle(Range, null, new Rect(X(from), Middle - Line / 2, Math.Max(0, X(to) - X(from)), Line));
        if (Wrapped) { Piece(0, _end); Piece(_start, Day); } else Piece(_start, _end);

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (var (position, label) in new[] { (0, "12:00"), (Day / 4, "18:00"), (Day / 2, "00:00"), (Day * 3 / 4, "06:00"), (Day, "12:00") })
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, Scale, dip);
            double x = position == 0 ? 0 : position == Day ? width - text.Width : X(position) - text.Width / 2;
            dc.DrawText(text, new Point(x, Middle + Knob / 2 + 1));
        }
        // Midnight is marked on the track, where the day changes.
        dc.DrawRectangle(Scale, null, new Rect(X(Day / 2) - .5, Middle - Line / 2 - 3, 1, Line + 6));

        var pen = new Pen(Outline, 1);
        for (int handle = 0; handle < 2; handle++)
        {
            var center = new Point(X(handle == 0 ? _start : _end), Middle);
            if (IsKeyboardFocused && handle == _active) dc.DrawEllipse(null, new Pen(FocusRing, 2), center, Knob / 2 + 2, Knob / 2 + 2);
            dc.DrawEllipse(Knobs, pen, center, Knob / 2 - .5, Knob / 2 - .5);
        }
    }
}
