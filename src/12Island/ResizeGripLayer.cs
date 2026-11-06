using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// The grips for resizing by dragging (MainWindow.Resize.cs), drawn over the whole window. It takes the pointer only on the grips
// (ResizeGrips): the open panel's sides and bottom, or the end of the collapsed surface the pointer is at, so everywhere else
// clicks reach the panel or pass through the window. Where it takes the pointer it paints an almost transparent fill, without
// which Windows would send those clicks to the window beneath.
// It also shows where the grips are: as the pointer nears an edge of the open panel, handles appear on its sides and bottom;
// the edge under the pointer lights in the accent color with a soft glow, its handle grows, and a corner lights its arc. At an end
// of the collapsed surface a glowing bar shows beside it. While dragging, a small label gives the size. All of it eases in and
// out (Motion), and nothing is drawn while the pointer is away.
// In move mode (MainWindow.Move.cs) the whole collapsed surface takes the pointer instead: its outline lights in the accent color
// and a move icon covers its content (MoveLook).
internal sealed class ResizeGripLayer : FrameworkElement
{
    public enum Form { None, Panel, Compact, Move }

    private static readonly Brush Catch = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private Form _form;
    private DipRect _rect;
    private double _header, _radius;
    private bool _line, _dragging;
    private ResizeEdges _armed, _lit;
    private string _label = "";
    private Eased _near = new(0, Eased.Hover), _left = new(0, Eased.Hover), _right = new(0, Eased.Hover), _bottom = new(0, Eased.Hover), _badge = new(0, Eased.Hover);

    public ResizeGripLayer() { Focusable = false; SnapsToDevicePixels = false; }

    public Form Shape => _form;
    public DipRect Rect => _rect;
    // The layer is drawn in the frame of the edge the panel hangs from (MainWindow.Edge.cs); this turns the size label upright.
    private Transform _upright = Transform.Identity;
    public Transform Upright { get => _upright; set { _upright = value; InvalidateVisual(); } }

    // The panel's or the surface's box in this layer's coordinates (the window's), and what it is.
    public void Show(Form form, DipRect rect, double header, double radius, bool line)
    {
        if (form == _form && rect == _rect && header == _header && radius == _radius && line == _line) return;
        bool formChanged = form != _form;
        _form = form; _rect = rect; _header = header; _radius = radius; _line = line;
        if (formChanged) { _armed = ResizeEdges.None; Light(ResizeEdges.None, false, ""); Near(0); }
        InvalidateVisual();
    }

    // Move mode: the surface's box, its outline (at the box's top-left corner), whether it hangs from the edge at the box's top
    // (the notch), whose line along the screen edge is then not lit, and whether it is the line, too thin to hold the icon, which
    // then shows just past it. The highlight eases in.
    private Geometry _moveOutline = Geometry.Empty;
    private bool _moveHangs, _moveThin;
    private Eased _moveShown = new(0, Eased.Hover);
    public void ShowMove(DipRect rect, Geometry outline, bool hangs, bool thin)
    {
        if (_form == Form.Move && rect == _rect && ReferenceEquals(outline, _moveOutline) && hangs == _moveHangs && thin == _moveThin) return;
        if (_form != Form.Move)
        {
            Show(Form.Move, rect, 0, 0, false);
            _moveShown = new(0, Eased.Hover);
            if (_moveShown.Set(1, Motion.Enabled)) Motion.Start(this, Step);
        }
        _rect = rect; _moveOutline = outline; _moveHangs = hangs; _moveThin = thin;
        InvalidateVisual();
    }

    // Where move mode takes the pointer: the surface and a little around it.
    private const double MoveCatch = 6;
    public bool OverMove(Point point) => _form == Form.Move && point.X >= _rect.X - MoveCatch && point.X <= _rect.Right + MoveCatch && point.Y >= _rect.Y - MoveCatch && point.Y <= _rect.Bottom + MoveCatch;

    public ResizeEdges EdgeAt(Point point) => _form switch
    {
        Form.Panel => ResizeGrips.Panel(point.X, point.Y, _rect, _header),
        Form.Compact => ResizeGrips.Compact(point.X, point.Y, _rect, _line),
        _ => ResizeEdges.None
    };

    // Collapsed, only the end the pointer is at takes it (the polling in TrackPointer says which), so the rest of the area
    // around the surface lets clicks through.
    public void Arm(ResizeEdges end)
    {
        if (end == _armed) return;
        _armed = end;
        InvalidateVisual();
    }

    // How close the pointer is to the open panel's grips, 0 to 1: the handles show as it nears.
    public void Near(double value)
    {
        value = Math.Clamp(value, 0, 1);
        // Polled 25 times a second: an unchanged value draws nothing, as every redraw presents the whole window again.
        if (Math.Abs(value - _near.Target) < .02) return;
        if (_near.Set(value, Motion.Enabled)) Motion.Start(this, Step); else InvalidateVisual();
    }

    // The edge or edges under the pointer, or being dragged, and the size to show while dragging.
    public void Light(ResizeEdges edges, bool dragging, string label)
    {
        bool moving = _left.Set(edges.HasFlag(ResizeEdges.Left) ? 1 : 0, Motion.Enabled);
        moving |= _right.Set(edges.HasFlag(ResizeEdges.Right) ? 1 : 0, Motion.Enabled);
        moving |= _bottom.Set(edges.HasFlag(ResizeEdges.Bottom) ? 1 : 0, Motion.Enabled);
        moving |= _badge.Set(dragging && label.Length > 0 ? 1 : 0, Motion.Enabled);
        bool changed = edges != _lit || dragging != _dragging || label != _label;
        _lit = edges; _dragging = dragging; if (label.Length > 0) _label = label;
        if (moving) Motion.Start(this, Step); else if (changed) InvalidateVisual();
    }

    private bool Step(double now)
    {
        InvalidateVisual();
        return _near.Moving(now) || _left.Moving(now) || _right.Moving(now) || _bottom.Moving(now) || _badge.Moving(now) || _moveShown.Moving(now);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
    {
        if (_form == Form.Move) return OverMove(parameters.HitPoint) || IsMouseCaptured ? new PointHitTestResult(this, parameters.HitPoint) : null;
        var edge = EdgeAt(parameters.HitPoint);
        if (edge == ResizeEdges.None) return null;
        if (_form == Form.Compact && edge != _armed && !IsMouseCaptured) return null;
        return new PointHitTestResult(this, parameters.HitPoint);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (_form == Form.None) return;
        double now = Eased.Now;
        if (_form == Form.Move)
        {
            var r = _rect;
            drawing.DrawRectangle(Catch, null, new Rect(r.X - MoveCatch, r.Y - MoveCatch, Math.Max(0, r.Width) + 2 * MoveCatch, Math.Max(0, r.Height) + 2 * MoveCatch));
            double shown = Lit(_moveShown, now);
            drawing.PushTransform(new TranslateTransform(r.X, r.Y));
            MoveLook.Surface(drawing, _moveOutline, shown, veil: true);
            MoveLook.Outline(drawing, _moveOutline, shown, _moveHangs);
            double size = MoveLook.IconSize(_moveThin ? 28 : Math.Min(r.Width, r.Height));
            MoveLook.Icon(drawing, new Point(r.Width / 2, _moveThin ? r.Height + 4 + size / 2 : r.Height / 2), size, shown);
            drawing.Pop();
            return;
        }
        if (_form == Form.Panel)
        {
            foreach (var strip in ResizeGrips.PanelStrips(_rect, _header)) drawing.DrawRectangle(Catch, null, Box(strip));
            PanelHints(drawing, now);
        }
        else
        {
            if (_armed != ResizeEdges.None || IsMouseCaptured)
                foreach (var end in new[] { ResizeEdges.Left, ResizeEdges.Right })
                    if (_armed == end || IsMouseCaptured) drawing.DrawRectangle(Catch, null, Box(ResizeGrips.CompactZone(_rect, _line, end)));
            CompactHints(drawing, now);
        }
        Badge(drawing, now);
    }

    private static Rect Box(DipRect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    private void PanelHints(DrawingContext drawing, double now)
    {
        double near = Math.Clamp(_near.At(now), 0, 1), left = Lit(_left, now), right = Lit(_right, now), bottom = Lit(_bottom, now);
        if (near + left + right + bottom < .01) return;
        var r = _rect;
        double top = r.Y + _header, middle = top + (r.Bottom - _radius - top) / 2;
        // The glow along each lit edge, between the header and the corner, or between the corners.
        if (left > .01) Glow(drawing, new Point(r.X, top + 6), new Point(r.X, r.Bottom - _radius), -1, left);
        if (right > .01) Glow(drawing, new Point(r.Right, top + 6), new Point(r.Right, r.Bottom - _radius), 1, right);
        if (bottom > .01) Glow(drawing, new Point(r.X + _radius, r.Bottom), new Point(r.Right - _radius, r.Bottom), 1, bottom);
        if (Math.Min(bottom, left) > .01) Corner(drawing, true, Math.Min(bottom, left));
        if (Math.Min(bottom, right) > .01) Corner(drawing, false, Math.Min(bottom, right));
        // The handles: one bar for every edge, just inside it on the panel's own surface, which grows and takes the accent color
        // when lit. The sides' and the bottom's are the same bar turned, placed on whole pixels so none of them draws softer.
        Handle(drawing, new Point(r.X + HandleInset, middle), true, near, left);
        Handle(drawing, new Point(r.Right - HandleInset, middle), true, near, right);
        Handle(drawing, new Point(r.X + r.Width / 2, r.Bottom - HandleInset), false, near, bottom);
    }

    private void CompactHints(DrawingContext drawing, double now)
    {
        var r = _rect;
        double height = _line ? 12 : Math.Max(10, r.Height * .62), y = r.Y + r.Height / 2;
        foreach (var (end, eased) in new[] { (ResizeEdges.Left, _left), (ResizeEdges.Right, _right) })
        {
            // An end the pointer is at shows a quiet bar; the one under the pointer or being dragged, a bright one.
            double lit = Lit(eased, now), armed = _armed == end ? .45 : 0, a = Math.Max(lit, armed);
            if (a < .01) continue;
            // The surface is too small to hold a bar, so it stands just outside the end.
            double x = end == ResizeEdges.Left ? r.X - HandleGap : r.Right + HandleGap;
            Bar(drawing, Snap(new Rect(x - HandleWidth / 2, y - height / 2, HandleWidth, height)), a, lit);
        }
    }

    private static double Lit(Eased eased, double now) => Math.Clamp(eased.At(now), 0, 1);

    // Every handle is the same bar: this thick, this long (longer when lit); the panel's this far inside its edge (to the bar's
    // middle), the collapsed surface's this far outside its ends.
    private const double HandleWidth = 4, HandleLength = 34, HandleGrowth = 14, HandleInset = 7, HandleGap = 7;

    // The theme's accent, unless it would hardly show on the panel's surface (a theme whose accent is its surface color);
    // then its primary text color. Light themes glow less, where a soft glow reads as a smudge, and lean on the line instead.
    internal static string Accent => ColorMath.Distance(PanelTheme.Hex("accent"), PanelTheme.Hex("surface")) < 60 ? PanelTheme.Hex("textPrimary") : PanelTheme.Hex("accent");
    internal static double GlowStrength => PanelTheme.Current.IsDark ? 1 : .55;
    // A thin rim of the surface color keeps a bar apart from what is under it: the panel's content, or whatever is behind the
    // window beside the collapsed surface.
    private static Pen Rim => _rim is { } rim && rim.Color == PanelTheme.Hex("surface") ? rim.Pen : (_rim = (PanelTheme.Hex("surface"), Frozen(new Pen(UI.Brush(PanelTheme.Hex("surface")), 1)))).Value.Pen;
    private static (string Color, Pen Pen)? _rim;

    // A bar's ends are round across its thickness, whichever way it lies: the bottom handle is the sides' turned, not an ellipse.
    private static double Round(Rect bar) => Math.Min(bar.Width, bar.Height) / 2;

    // A bar with a soft glow: wider, fainter copies behind it.
    private static void Bar(DrawingContext drawing, Rect bar, double opacity, double lit)
    {
        drawing.PushOpacity(opacity);
        if (lit > .01)
        {
            drawing.PushOpacity(lit);
            for (int i = 3; i >= 1; i--)
            {
                var halo = bar; halo.Inflate(i * 1.6, i * 1.6);
                drawing.PushOpacity(.09 * (4 - i) * GlowStrength);
                drawing.DrawRoundedRectangle(UI.Brush(Accent), null, halo, Round(halo), Round(halo));
                drawing.Pop();
            }
            drawing.Pop();
        }
        var rim = bar; rim.Inflate(.5, .5);
        drawing.PushOpacity(.55);
        drawing.DrawRoundedRectangle(null, Rim, rim, Round(rim), Round(rim));
        drawing.Pop();
        drawing.DrawRoundedRectangle(UI.Brush(UI.Tertiary), null, bar, Round(bar), Round(bar));
        drawing.PushOpacity(lit);
        drawing.DrawRoundedRectangle(UI.Brush(Accent), null, bar, Round(bar), Round(bar));
        drawing.Pop();
        drawing.Pop();
    }

    private void Handle(DrawingContext drawing, Point center, bool vertical, double near, double lit)
    {
        double opacity = Math.Max(near * .6, lit);
        if (opacity < .01) return;
        double length = HandleLength + HandleGrowth * lit;
        var bar = vertical ? new Rect(center.X - HandleWidth / 2, center.Y - length / 2, HandleWidth, length) : new Rect(center.X - length / 2, center.Y - HandleWidth / 2, length, HandleWidth);
        Bar(drawing, Snap(bar), opacity, lit);
    }

    // The bar's edges on whole device pixels, its thickness kept the same in every direction.
    private Rect Snap(Rect bar)
    {
        double ppd = MonitorDraw.PixelsPerDip(this);
        double Px(double dip) => Math.Round(dip * ppd) / ppd;
        double thick = Math.Max(1, Math.Round(HandleWidth * ppd)) / ppd;
        return bar.Width <= bar.Height
            ? new Rect(Px(bar.X + bar.Width / 2 - thick / 2), Px(bar.Y), thick, Px(bar.Height))
            : new Rect(Px(bar.X), Px(bar.Y + bar.Height / 2 - thick / 2), Px(bar.Width), thick);
    }

    // A line along the edge that fades out toward its ends, with a soft glow outside the panel (side: which way is outside).
    private static readonly Dictionary<(string, bool), Brush> Fades = [];
    private static Brush Fade(string color, bool vertical)
    {
        if (Fades.TryGetValue((color, vertical), out var brush)) return brush;
        var c = (Color)ColorConverter.ConvertFromString(color);
        var stops = new GradientStopCollection
        {
            new(Color.FromArgb(0, c.R, c.G, c.B), 0), new(c, .3), new(c, .7), new(Color.FromArgb(0, c.R, c.G, c.B), 1)
        };
        brush = Frozen(new LinearGradientBrush(stops, vertical ? new Point(0, 0) : new Point(0, 0), vertical ? new Point(0, 1) : new Point(1, 0)));
        if (Fades.Count > 32) Fades.Clear();
        Fades[(color, vertical)] = brush;
        return brush;
    }
    private static void Glow(DrawingContext drawing, Point from, Point to, int outside, double lit)
    {
        bool vertical = from.X == to.X;
        double length = vertical ? to.Y - from.Y : to.X - from.X;
        if (length < 8) return;
        var brush = Fade(Accent, vertical);
        drawing.PushOpacity(lit);
        // Inside the edge a crisp line; outside, fainter lines further out.
        for (int i = 0; i <= 4; i++)
        {
            double offset = i == 0 ? -1.5 * outside : (i - .5) * outside, alpha = i == 0 ? .95 : .34 / i * GlowStrength;
            var line = vertical ? new Rect(from.X + offset - (i == 0 ? 1 : .5), from.Y, i == 0 ? 2 : 1, length) : new Rect(from.X, from.Y + offset - (i == 0 ? 1 : .5), length, i == 0 ? 2 : 1);
            drawing.PushOpacity(alpha);
            drawing.DrawRectangle(brush, null, line);
            drawing.Pop();
        }
        drawing.Pop();
    }

    // The bottom corner's arc, just inside the panel's outline.
    private void Corner(DrawingContext drawing, bool left, double lit)
    {
        var r = _rect;
        double radius = Math.Max(4, _radius - 1.5);
        var start = left ? new Point(r.X + 1.5, r.Bottom - _radius) : new Point(r.Right - 1.5, r.Bottom - _radius);
        var end = left ? new Point(r.X + _radius, r.Bottom - 1.5) : new Point(r.Right - _radius, r.Bottom - 1.5);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, false, left ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(UI.Brush(Accent), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawing.PushOpacity(lit);
        drawing.DrawGeometry(null, pen, geometry);
        drawing.Pop();
    }

    // The size while dragging: a chip inside the panel's bottom right corner, clear of the handles, or under the collapsed surface.
    private void Badge(DrawingContext drawing, double now)
    {
        double opacity = Lit(_badge, now);
        if (opacity < .01 || _label.Length == 0) return;
        double ppd = MonitorDraw.PixelsPerDip(this), size = _form == Form.Panel ? 12 : 11;
        var text = MonitorDraw.Text(_label, size, UI.Primary, ppd, strong: true);
        double width = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 16, height = Math.Ceiling(size * 1.7);
        // Turned upright, the chip takes its own width and height on the screen: its place is worked out with them swapped when
        // the frame turns them, and it is then turned about its centre.
        var upright = _upright.Value;
        bool turned = upright.M11 == 0;
        double across = turned ? height : width, deep = turned ? width : height;
        double x = _form == Form.Panel ? _rect.Right - 14 - across : _rect.X + _rect.Width / 2 - across / 2;
        double y = _form == Form.Panel ? _rect.Bottom - 12 - deep : _rect.Bottom + 4;
        var centre = new Point(Math.Round(x) + across / 2, Math.Round(y) + deep / 2);
        var box = new Rect(centre.X - width / 2, centre.Y - height / 2, width, height);
        drawing.PushTransform(new MatrixTransform(upright.M11, upright.M12, upright.M21, upright.M22,
            centre.X - (centre.X * upright.M11 + centre.Y * upright.M21), centre.Y - (centre.X * upright.M12 + centre.Y * upright.M22)));
        drawing.PushOpacity(opacity);
        drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("tooltip")), new Pen(UI.Brush(PanelTheme.Hex("tooltipBorder")), 1), box, height / 2, height / 2);
        drawing.DrawText(text, new Point(box.X + 8, box.Y + (height - text.Height) / 2));
        drawing.Pop(); drawing.Pop();
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
