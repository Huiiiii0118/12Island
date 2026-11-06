using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// What a move starts from (MainWindow.Move.cs), on the screen in DIPs relative to the monitor's top-left corner. Surface is the
// collapsed surface's box. For the notch and the line: the edge they hang from and whether from the screen's edge over a taskbar
// there, what the settings say they hang from (DeckSettings.NotchAnchor, LineAnchor), the length along the edge and the depth away from it (the line's thickness), the notch's depth on the edges that run the
// other way (its height setting: there the content decides once it has landed), its shoulders and bottom corners, and the line's
// distance from the edge. Grab is where the pointer was pressed.
internal sealed record MoveStart(CompactStyle Style, double ScreenWidth, double ScreenHeight, DipRect Work, DipRect Surface,
    ScreenEdge Edge, bool OverTaskbar, EdgeAnchor? SavedAnchor, double Length, double Depth, double OtherDepth, double Shoulder, double Corner, double Gap, Point Grab);

// Where a move ended: the notch's or the line's edge, its saved offset along it and what it hangs from there (null where that edge
// has no taskbar, keeping the saved choice), or the capsule's top-left corner.
internal readonly record struct MovePlacement(ScreenEdge Edge, double Offset, double X, double Y, EdgeAnchor? Anchor);

// The window the collapsed surface moves in while it is dragged: as large as the monitor, above everything, and transparent to the
// pointer, which the panel keeps captured and passes on (Pointer). It lives from the moment the surface lifts until it has settled
// after the drop and faded out over the island shown at its new place.
internal sealed class MoveOverlay : Window
{
    private readonly MoveGhost _ghost;

    public MoveOverlay(Rect bounds, double scale, MoveStart start)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false; Title = "12 island";
        Content = _ghost = new MoveGhost(start);
        Left = bounds.Left / scale; Top = bounds.Top / scale; Width = bounds.Width / scale; Height = bounds.Height / scale;
        // Moved onto the monitor in one step first, which gives the window that monitor's DPI (as MainWindow.Place does), and sized
        // in its DIPs then. The pointer and activation pass through it.
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, ExStyle, GetWindowLongPtr(handle, ExStyle) | Transparent | ToolWindow | NoActivate);
            WindowsHost.MoveTo(this, (int)bounds.Left, (int)bounds.Top);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!(dpi > 0)) dpi = scale;
            Left = bounds.Left / dpi; Top = bounds.Top / dpi; Width = bounds.Width / dpi; Height = bounds.Height / dpi;
        };
    }

    public event Action<MovePlacement>? Settled { add => _ghost.Settled += value; remove => _ghost.Settled -= value; }
    public event Action<ScreenEdge, bool>? DockChanged { add => _ghost.DockChanged += value; remove => _ghost.DockChanged -= value; }
    public void Shape(ScreenEdge edge, bool over, double length, double depth) => _ghost.Shape(edge, over, length, depth);

    // The pointer in DIPs relative to the monitor's top-left corner.
    public void Pointer(Point point) => _ghost.Pointer(point);
    public void Release() => _ghost.Release();

    public void FadeOut()
    {
        if (!Soft.Enabled) { Close(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = Soft.Out };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private const int ExStyle = -20;
    private const nint Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
}

// The surface while it is dragged. It keeps up with the pointer closely, easing only the last few DIPs, so a place that holds it
// (CollapsedMove) draws it in without a jump; a faint guide in the accent color shows what holds it. Moving, it neither stretches
// nor leans.
// The notch and the line move along the edge nearest the pointer. When that edge changes, the surface sinks back into the edge it
// leaves and rises out of the new one under the pointer. The panel lays itself out for the new edge meanwhile (DockChanged) and
// reports the size it takes there (Shape): a notch on a side edge stands its content in a column, so its length and depth change,
// and the surface eases to that size. On an edge with a taskbar it hangs from the screen's edge while the pointer is over the
// taskbar and from the taskbar's inner edge otherwise (CollapsedMove.AnchorAt), sliding between the two.
internal sealed class MoveGhost : FrameworkElement
{
    private sealed class Docked
    {
        public ScreenEdge Edge;
        public bool Over;
        public double Along, Line, Length, Depth;
        public Eased Fill;
    }

    // The time constants with which the surface catches up with the pointer and with a new size; how long it takes to sink into an
    // edge and to rise out of another.
    private const double Follow = .025, Resize = .07, FillSeconds = .2;
    private readonly MoveStart _start;
    private Point _pointer;
    private bool _dropping, _settled, _hooked;
    private TimeSpan _time;
    // The guides: the capsule's vertical and horizontal ones, or the middle of the docked surface's edge; where each was last shown,
    // so it fades out there.
    private Eased _guideA = new(0, Eased.Hover), _guideB = new(0, Eased.Hover);
    private double _guideAAt, _guideBAt;

    // The capsule's top-left corner and where it is heading; the corner relative to the pointer, so it does not jump as it lifts.
    private double _x, _y, _tx, _ty;
    private readonly double _grabX, _grabY;

    // The notch or the line on the edge it hangs from (the last of these), and those still sinking into the edges it left; where it
    // is heading along its edge; the edge the pointer is nearest; what it hangs from there. Its centre relative to the pointer holds
    // on its first edge until it leaves it.
    private readonly List<Docked> _docked = [];
    private double _target;
    private ScreenEdge _pointerEdge;
    private EdgeAnchor? _anchor;
    private readonly double _grabAlong;
    private bool _moved;
    // The size the panel reported for each edge and anchor (Shape).
    private readonly Dictionary<(ScreenEdge, bool), (double Length, double Depth)> _sizes = [];

    public event Action<MovePlacement>? Settled;
    // The surface now hangs from another edge or anchor: the panel lays itself out there and reports the size it takes.
    public event Action<ScreenEdge, bool>? DockChanged;

    public MoveGhost(MoveStart start)
    {
        _start = start; IsHitTestVisible = false;
        var s = start.Surface;
        _pointer = start.Grab;
        if (start.Style == CompactStyle.Capsule)
        {
            _x = _tx = s.X; _y = _ty = s.Y;
            _grabX = s.X - start.Grab.X; _grabY = s.Y - start.Grab.Y;
        }
        else
        {
            _pointerEdge = start.Edge;
            double along = CollapsedMove.Along(start.Edge, s.X + s.Width / 2, s.Y + s.Height / 2);
            _docked.Add(new Docked { Edge = start.Edge, Over = start.OverTaskbar, Along = along, Line = LineOf(start.Edge, start.OverTaskbar), Length = start.Length, Depth = start.Depth, Fill = new Eased(1, FillSeconds) });
            _sizes[(start.Edge, start.OverTaskbar)] = (start.Length, start.Depth);
            _target = along;
            _grabAlong = along - CollapsedMove.Along(start.Edge, start.Grab.X, start.Grab.Y);
        }
        Unloaded += (_, _) => Unhook();
    }

    private bool IsDocked => _start.Style != CompactStyle.Capsule;
    private bool IsLine => _start.Style == CompactStyle.Line;
    private Docked Home => _docked[^1];

    public void Pointer(Point p)
    {
        if (_dropping) return;
        _pointer = p;
        Hook();
    }

    // The size the surface takes on an edge and anchor, from the panel laid out there.
    public void Shape(ScreenEdge edge, bool over, double length, double depth)
    {
        if (!(length > 0) || !(depth > 0)) return;
        _sizes[(edge, over)] = (length, depth);
        Hook();
    }

    // The button is up: the surface settles on exactly the place that will be saved, and then reports it.
    public void Release()
    {
        _dropping = true;
        if (IsDocked) _target = Place(Home.Edge, Home.Over, Home.Length, Wanted(Home.Edge)).Centre;
        Hook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true; _time = TimeSpan.Zero;
        CompositionTarget.Rendering += Frame;
    }
    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= Frame;
    }

    private void Frame(object? sender, EventArgs e)
    {
        var time = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        // WPF may raise Rendering more than once for one frame.
        if (_time != TimeSpan.Zero && time == _time) return;
        double dt = _time == TimeSpan.Zero ? 1 / 120.0 : Math.Clamp((time - _time).TotalSeconds, 0, .1);
        _time = time;
        double now = Eased.Now;
        // Without animations the surface is where the pointer puts it.
        double k = Motion.Enabled ? 1 - Math.Exp(-dt / Follow) : 1, kSize = Motion.Enabled ? 1 - Math.Exp(-dt / Resize) : 1;
        bool moving = false;
        if (!IsDocked)
        {
            if (!_dropping)
            {
                var (x, y, guideX, guideY) = CollapsedMove.Capsule(_pointer.X + _grabX, _pointer.Y + _grabY, _start.Surface.Width, _start.Surface.Height, _start.ScreenHeight, _start.Work);
                _tx = x; _ty = y;
                Guide(ref _guideA, ref _guideAAt, guideX, now);
                Guide(ref _guideB, ref _guideBAt, guideY, now);
            }
            _x = Ease(_x, _tx, k, ref moving);
            _y = Ease(_y, _ty, k, ref moving);
        }
        else DockedFrame(k, kSize, now, ref moving);
        moving |= _guideA.Moving(now) || _guideB.Moving(now);
        InvalidateVisual();
        if (moving) return;
        Unhook();
        if (!_dropping || _settled) return;
        _settled = true;
        if (!IsDocked) { Settled?.Invoke(new MovePlacement(ScreenEdge.Top, 0, _tx, _ty, null)); return; }
        var (from, to) = CollapsedMove.AlongRange(_start.Style, Home.Edge, Home.Over, _start.ScreenWidth, _start.ScreenHeight, _start.Work);
        Settled?.Invoke(new MovePlacement(Home.Edge, CollapsedMove.Offset(_target, from, to), 0, 0, _anchor));
    }

    private static double Ease(double value, double target, double k, ref bool moving)
    {
        if (Math.Abs(target - value) < .3) return target;
        moving = true;
        return value + (target - value) * k;
    }

    // The pointer's place along an edge, less where it held the surface on its first edge.
    private double Wanted(ScreenEdge edge) => CollapsedMove.Along(edge, _pointer.X, _pointer.Y) + (_moved ? 0 : _grabAlong);

    private void DockedFrame(double k, double kSize, double now, ref bool moving)
    {
        if (!_dropping)
        {
            var edge = CollapsedMove.NearestEdge(_pointer.X, _pointer.Y, _start.ScreenWidth, _start.ScreenHeight, _pointerEdge);
            _pointerEdge = edge;
            // Over the taskbar it hangs from the screen's edge, elsewhere from the taskbar's; an edge without one keeps what it had.
            var anchor = CollapsedMove.AnchorAt(edge, _pointer.X, _pointer.Y, _start.ScreenWidth, _start.ScreenHeight, _start.Work);
            bool over = DeckSettings.Anchor(_start.Style, edge, anchor ?? _start.SavedAnchor) == EdgeAnchor.Screen;
            if (anchor is not null) _anchor = anchor;
            if (edge != Home.Edge) MoveTo(edge, over, now);
            else if (over != Home.Over) { Home.Over = over; DockChanged?.Invoke(edge, over); }
            var (centre, held) = Place(edge, Home.Over, Home.Length, Wanted(edge));
            _target = centre;
            var (from, to) = CollapsedMove.AlongRange(_start.Style, edge, Home.Over, _start.ScreenWidth, _start.ScreenHeight, _start.Work);
            Guide(ref _guideA, ref _guideAAt, held ? (from + to) / 2 : null, now);
        }
        var home = Home;
        home.Along = Ease(home.Along, _target, k, ref moving);
        home.Line = Ease(home.Line, LineOf(home.Edge, home.Over), k, ref moving);
        var (length, depth) = SizeOn(home.Edge, home.Over);
        home.Length = Ease(home.Length, length, kSize, ref moving);
        home.Depth = Ease(home.Depth, depth, kSize, ref moving);
        foreach (var docked in _docked) moving |= docked.Fill.Moving(now);
        _docked.RemoveAll(docked => docked != home && !docked.Fill.Moving(now));
    }

    // The surface leaves its edge for the one the pointer is nearest: it sinks into the old edge where it is and rises out of the new
    // one under the pointer, at the size it had there before or, until the panel reports it, the size it would take there.
    private void MoveTo(ScreenEdge edge, bool over, double now)
    {
        var old = Home;
        old.Fill.Set(0, Motion.Enabled, now);
        _moved = true;
        var (length, depth) = SizeOn(edge, over);
        double along = Place(edge, over, length, CollapsedMove.Along(edge, _pointer.X, _pointer.Y)).Centre;
        var next = new Docked { Edge = edge, Over = over, Along = along, Line = LineOf(edge, over), Length = length, Depth = depth, Fill = new Eased(0, FillSeconds) };
        next.Fill.Set(1, Motion.Enabled, now);
        _docked.Add(next);
        DockChanged?.Invoke(edge, over);
    }

    private (double Length, double Depth) SizeOn(ScreenEdge edge, bool over)
    {
        if (_sizes.TryGetValue((edge, over), out var size) || _sizes.TryGetValue((edge, !over), out size)) return size;
        bool across = edge is ScreenEdge.Left or ScreenEdge.Right, startAcross = _start.Edge is ScreenEdge.Left or ScreenEdge.Right;
        return IsLine || across == startAcross ? (_start.Length, _start.Depth) : (_start.Length, _start.OtherDepth);
    }

    private static void Guide(ref Eased eased, ref double at, double? place, double now)
    {
        if (place is { } value) at = value;
        eased.Set(place is null ? 0 : 1, Motion.Enabled, now);
    }

    private double LineOf(ScreenEdge edge, bool over) => CollapsedMove.Line(edge, over, _start.ScreenWidth, _start.ScreenHeight, _start.Work);
    private (double Centre, bool Held) Place(ScreenEdge edge, bool over, double length, double wanted)
    {
        var (from, to) = CollapsedMove.AlongRange(_start.Style, edge, over, _start.ScreenWidth, _start.ScreenHeight, _start.Work);
        return CollapsedMove.AlongIn(wanted, length, from, to);
    }

    // A frame point (x along the edge, y away from the line the surface hangs from) on the screen.
    private static Matrix ToScreen(ScreenEdge edge, double line) => edge switch
    {
        ScreenEdge.Bottom => new Matrix(1, 0, 0, -1, 0, line),
        ScreenEdge.Left => new Matrix(0, 1, 1, 0, line, 0),
        ScreenEdge.Right => new Matrix(0, 1, -1, 0, line, 0),
        _ => new Matrix(1, 0, 0, 1, 0, line)
    };

    protected override void OnRender(DrawingContext drawing)
    {
        double now = Eased.Now;
        if (IsDocked) DrawDocked(drawing, now); else DrawCapsule(drawing, now);
    }

    private void DrawCapsule(DrawingContext drawing, double now)
    {
        double width = _start.Surface.Width, height = _start.Surface.Height;
        double cx = _x + width / 2, cy = _y + height / 2;
        var outline = new RectangleGeometry(new Rect(_x, _y, width, height), height / 2, height / 2);
        MoveLook.Surface(drawing, outline, 1, veil: false);
        MoveLook.Outline(drawing, outline, 1, hangs: false);
        MoveLook.Icon(drawing, new Point(cx, cy), MoveLook.IconSize(height), 1);
        // The guides cross the capsule and reach a little past it: the vertical one where its centre is held on the work area's
        // middle, the horizontal one where its middle or one of its edges is held.
        double a = Lit(_guideA, now), b = Lit(_guideB, now);
        if (a > .01) MoveLook.Guide(drawing, new Rect(_guideAAt - .75, _y - GuideReach, 1.5, height + 2 * GuideReach), a);
        if (b > .01)
        {
            double y = Math.Clamp(_guideBAt, .75, Math.Max(.75, _start.ScreenHeight - .75));
            MoveLook.Guide(drawing, new Rect(_x - GuideReach, y - .75, width + 2 * GuideReach, 1.5), b);
        }
    }

    // How far a guide reaches past the surface it crosses.
    private const double GuideReach = 32;

    // Each surface rises from its edge (or sinks into it) by its fill: it is that much of its depth, and from three quarters of its
    // length to all of it; the line keeps its distance from the edge and grows thicker.
    private void DrawDocked(DrawingContext drawing, double now)
    {
        var home = Home;
        foreach (var docked in _docked)
        {
            double fill = Soft.Out.Ease(Lit(docked.Fill, now));
            if (fill < .01) continue;
            double length = docked.Length * (.75 + .25 * fill);
            drawing.PushTransform(new MatrixTransform(ToScreen(docked.Edge, docked.Line)));
            drawing.PushOpacity(Math.Clamp(fill * 1.5, 0, 1));
            if (IsLine)
            {
                double thickness = Math.Max(.5, docked.Depth * fill);
                var bar = new RectangleGeometry(new Rect(docked.Along - length / 2, _start.Gap + (docked.Depth - thickness) / 2, length, thickness), thickness / 2, thickness / 2);
                MoveLook.Surface(drawing, bar, 1, veil: false);
                MoveLook.Outline(drawing, bar, 1, hangs: false);
                double size = MoveLook.IconSize(28);
                // The line is too thin to hold the icon: it shows just past the line.
                MoveLook.Icon(drawing, new Point(docked.Along, _start.Gap + docked.Depth + 4 + size / 2), size, fill);
            }
            else
            {
                double depth = docked.Depth * fill;
                if (depth >= .5)
                {
                    var outline = IslandBorder.BuildOutline(length, depth, _start.Shoulder, new CornerRadius(0, 0, _start.Corner, _start.Corner), 0);
                    drawing.PushTransform(new TranslateTransform(docked.Along - length / 2, 0));
                    MoveLook.Surface(drawing, outline, 1, veil: false);
                    MoveLook.Outline(drawing, outline, 1, hangs: true);
                    drawing.Pop();
                    MoveLook.Icon(drawing, new Point(docked.Along, depth / 2), MoveLook.IconSize(Math.Min(docked.Length, docked.Depth)), Math.Clamp(fill * 2 - 1, 0, 1));
                }
            }
            drawing.Pop(); drawing.Pop();
        }
        // Held at the middle of its edge: the guide runs from the edge across the surface and a little past it, on every edge alike.
        if (Lit(_guideA, now) is var guide && guide > .01)
        {
            drawing.PushTransform(new MatrixTransform(ToScreen(home.Edge, home.Line)));
            MoveLook.Guide(drawing, new Rect(_guideAAt - .75, 0, 1.5, _start.Gap + home.Depth + GuideReach), guide);
            drawing.Pop();
        }
    }

    private static double Lit(Eased eased, double now) => Math.Clamp(eased.At(now), 0, 1);
}

// How move mode draws the surface, on the island (ResizeGripLayer.Form.Move) and while it is dragged (MoveGhost): the outline lit
// in the accent color as the resize handles light, with a soft glow, and a move icon in the middle.
internal static class MoveLook
{
    public static double IconSize(double surface) => Math.Clamp(surface * .52, 12, 24);

    // The surface: opaque while dragged, or over the island a veil of the surface color that quiets its content under the icon.
    public static void Surface(DrawingContext drawing, Geometry outline, double opacity, bool veil)
    {
        if (opacity < .01) return;
        drawing.PushOpacity((veil ? .86 : 1) * opacity);
        drawing.DrawGeometry(UI.Brush(PanelTheme.Hex("surface")), null, outline);
        drawing.Pop();
    }

    // The lit outline. A notch hangs from the screen edge (hangs): its line along the edge is left dark.
    public static void Outline(DrawingContext drawing, Geometry outline, double opacity, bool hangs)
    {
        if (opacity < .01 || outline.IsEmpty()) return;
        var bounds = outline.Bounds;
        if (hangs) drawing.PushClip(new RectangleGeometry(new Rect(bounds.X - 24, bounds.Y + 1.5, bounds.Width + 48, bounds.Height + 24)));
        string accent = ResizeGripLayer.Accent;
        foreach (var (width, alpha) in new[] { (10.0, .06), (6.0, .11), (3.4, .2) })
        {
            drawing.PushOpacity(alpha * ResizeGripLayer.GlowStrength * opacity);
            drawing.DrawGeometry(null, Pen(accent, width), outline);
            drawing.Pop();
        }
        drawing.PushOpacity(.95 * opacity);
        drawing.DrawGeometry(null, Pen(accent, 1.6), outline);
        drawing.Pop();
        if (hangs) drawing.Pop();
    }

    // Four arrows from the middle, drawn rather than taken from a font, in the accent color.
    public static void Icon(DrawingContext drawing, Point centre, double size, double opacity)
    {
        if (opacity < .01) return;
        double a = size / 2, head = size * .2;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(centre.X - a, centre.Y), false, false); path.LineTo(new Point(centre.X + a, centre.Y), true, false);
            path.BeginFigure(new Point(centre.X, centre.Y - a), false, false); path.LineTo(new Point(centre.X, centre.Y + a), true, false);
            foreach (var (dx, dy) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
            {
                var tip = new Point(centre.X + a * dx, centre.Y + a * dy);
                path.BeginFigure(new Point(tip.X - head * dx - head * dy, tip.Y - head * dy + head * dx), false, false);
                path.LineTo(tip, true, true);
                path.LineTo(new Point(tip.X - head * dx + head * dy, tip.Y - head * dy - head * dx), true, true);
            }
        }
        geometry.Freeze();
        drawing.PushOpacity(opacity);
        drawing.DrawGeometry(null, Pen(ResizeGripLayer.Accent, Math.Max(1.4, Math.Round(size / 11 * 2) / 2), round: true), geometry);
        drawing.Pop();
    }

    // A thin line in the accent color that fades out toward its ends.
    public static void Guide(DrawingContext drawing, Rect line, double opacity)
    {
        if (opacity < .01) return;
        drawing.PushOpacity(.9 * opacity);
        drawing.DrawRectangle(Fade(ResizeGripLayer.Accent, line.Height > line.Width), null, line);
        drawing.Pop();
    }

    private static readonly Dictionary<(string, double, bool), Pen> Pens = [];
    private static Pen Pen(string color, double width, bool round = false)
    {
        if (Pens.TryGetValue((color, width, round), out var pen)) return pen;
        pen = new Pen(UI.Brush(color), width) { LineJoin = PenLineJoin.Round };
        if (round) { pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round; }
        pen.Freeze();
        if (Pens.Count > 64) Pens.Clear();
        return Pens[(color, width, round)] = pen;
    }

    private static readonly Dictionary<(string, bool), Brush> Fades = [];
    private static Brush Fade(string color, bool vertical)
    {
        if (Fades.TryGetValue((color, vertical), out var brush)) return brush;
        var c = (Color)ColorConverter.ConvertFromString(color);
        var clear = Color.FromArgb(0, c.R, c.G, c.B);
        brush = new LinearGradientBrush(new GradientStopCollection { new(clear, 0), new(c, .3), new(c, .7), new(clear, 1) }, new Point(0, 0), vertical ? new Point(0, 1) : new Point(1, 0));
        brush.Freeze();
        if (Fades.Count > 16) Fades.Clear();
        return Fades[(color, vertical)] = brush;
    }
}
