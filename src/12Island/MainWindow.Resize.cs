using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// Resizing by dragging, one gesture for both forms, as ftop's panel resizes and its strip fits:
// - Open, on any page (AI usage, system monitor, media), the bottom edge changes the page's height, a side edge its width (both
//   sides move, so the panel keeps the centre it opened with; see SetExpanded) and a bottom corner both. Each page keeps its own size
//   and lays itself out for it as it changes.
// - Collapsed, either end of the notch, the capsule or the line changes its width. Pointing at an end shows the resize cursor
//   instead of opening the panel. The content reflows as the width changes: the readings, the rings and the lyric take what
//   fits. A width set this way is kept, so the surface no longer widens to its content; a double click on an end turns that
//   back on or off (the line returns to its usual length), or starts moving the surface, as the settings choose (MainWindow.Move.cs).
// The grips and their hints are a layer over the window (ResizeGripLayer); where they are is ResizeGrips's. The pointer is
// followed both by mouse messages on the layer and by TrackPointer's polling, which also sees it when the messages go to another
// window, and arms the collapsed surface's end before the pointer reaches it.
// The surface trails the pointer on a spring (Spring, in Core): it lags a little while dragging, settles with a slight give when the
// pointer stops, and near a limit the drag meets growing resistance instead of a wall. The size is saved once the spring has come
// to rest after the button is released. The window already has the largest size either form can take (ApplySettings), so each
// frame resizes only the surface; the window itself never changes, and the panel neither opens nor closes.
// The resize shortcut (KeyboardResize) does what a drag does, one step per press of an arrow key, on the same spring and within
// the same limits, so a panel can be sized without the mouse.
public partial class MainWindow
{
    private ResizeGripLayer _grips = null!;
    private ResizeEdges _resizeEdge, _hoverEdge;
    private bool _resizeCompact, _resizeChanged, _resizeSettling, _springHooked;
    // The size is moving for the resize shortcut, and the edges it moves (lit as a drag lights them).
    private bool _resizeByKey;
    private ResizeEdges _keyEdges;
    private Point _resizeStart;
    private (double Width, double Height) _resizeFrom;
    private Spring _springWidth, _springHeight;
    private CompactStyle _resizeStyle;
    private TimeSpan _springTime;
    // How far inside the open panel, in DIPs, its handles start to show as the pointer nears an edge; how far outside its grips a
    // pointer that left the panel still counts as reaching for them, and how long at most the panel keeps waiting while the pointer
    // stays there (after the collapse delay set by the user, checking every 120 ms); how long the panel stays after a resize that
    // ended with the pointer outside it.
    private const double HintReach = 44, GripSlack = 36;
    private const int GripHold = 1500, ResizeLinger = 1200;
    private long _leftAt;

    private bool Dragging => _resizeEdge != ResizeEdges.None;
    // Dragging, or settling after the button was released.
    private bool Resizing => Dragging || _resizeSettling;
    internal bool IsResizing => Resizing;
    // The page being resized, and when its content was last laid out again during the drag.
    private DeckPage _resizePage;
    private long _resizeRendered;
    // The widest a page can be on this monitor, and the limits of the page being resized.
    private double PageMaxWidth => DeckPages.MaxWidthOn(WorkDip.Width, _scale);
    private (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) PageLimits => PageLimitsOf(_resizePage);
    private (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) PageLimitsOf(DeckPage page)
    {
        var (minWidth, _, minHeight, maxHeight) = DeckPages.Limits(Settings, page);
        return (minWidth, Math.Max(minWidth, PageMaxWidth), minHeight, maxHeight);
    }

    private void InitializeResize()
    {
        _grips = new ResizeGripLayer();
        Stage.Children.Add(_grips);
        _grips.MouseMove += GripMove;
        _grips.MouseLeftButtonDown += GripDown;
        _grips.MouseLeftButtonUp += (_, e) => { if (_movePressed) { e.Handled = true; EndMovePress(); } else if (Dragging) { e.Handled = true; EndResize(); } };
        _grips.LostMouseCapture += (_, _) => { if (_movePressed) EndMovePress(); else if (Dragging) EndResize(); };
        // The grips outside the panel are part of it: arriving there from the panel is not leaving it.
        _grips.MouseEnter += (_, _) => { if (_expanded) PointerChanged(true); };
        _grips.MouseLeave += (_, _) =>
        {
            if (Dragging) return;
            SetHoverEdge(ResizeEdges.None);
            if (_expanded && !Island.IsMouseOver) PointerChanged(InsideOpenPanel(WindowsHost.Cursor()));
        };
        Island.SizeChanged += (_, _) => UpdateGrips();
        Closed += (_, _) => StopSpring();
    }

    // Which form the grips take now: the open monitor page, the collapsed surface, or none (another page, a preview, hidden,
    // a quota alert widening the surface, powering off).
    private bool PanelResizable => _expanded && !_preview && IsVisible && !IsSuppressed && !_poweringOff;
    private bool CompactResizable => !_expanded && !_preview && IsVisible && !IsSuppressed && _alert is null && !_poweringOff;

    // The island's box in the window's coordinates, where the grip layer draws.
    private DipRect IslandBox()
    {
        if (PresentationSource.FromVisual(Island) is null || Island.ActualWidth <= 0) return default;
        var origin = Island.TranslatePoint(new Point(), Stage);
        return new DipRect(origin.X, origin.Y, Island.ActualWidth, Island.ActualHeight);
    }

    // Brings the layer up to date with the island; cheap when nothing changed.
    private void UpdateGrips()
    {
        if (_grips is null) return;
        if (_moveArmed) { ShowMoveGrips(); return; }
        var form = PanelResizable ? ResizeGripLayer.Form.Panel : CompactResizable || Resizing && _resizeCompact ? ResizeGripLayer.Form.Compact : ResizeGripLayer.Form.None;
        if (Resizing && !_resizeCompact) form = ResizeGripLayer.Form.Panel;
        _grips.Show(form, IslandBox(), _header.Row * _scale, PanelRadius, EffectiveStyle == CompactStyle.Line);
        if (form == ResizeGripLayer.Form.None && _hoverEdge != ResizeEdges.None && !Dragging) SetHoverEdge(ResizeEdges.None);
    }

    // From TrackPointer, which polls the cursor: arms the collapsed surface's end the pointer is at, and shows the open panel's
    // handles as the pointer nears them. Returns the grip under the pointer.
    private ResizeEdges TrackGrips(Point screen)
    {
        UpdateGrips();
        if (_grips.Shape == ResizeGripLayer.Form.None || PresentationSource.FromVisual(Stage) is null) return ResizeEdges.None;
        var point = Stage.PointFromScreen(screen);
        var edge = _grips.EdgeAt(point);
        if (_grips.Shape == ResizeGripLayer.Form.Compact) { _grips.Arm(edge); return edge; }
        var r = _grips.Rect;
        // The distance to the nearest grip: zero on one, growing inside the panel; nothing above the header.
        double near = 0;
        if (point.Y >= r.Y + _header.Row * _scale && point.X >= r.X - ResizeGrips.PanelOuter && point.X <= r.Right + ResizeGrips.PanelOuter && point.Y <= r.Bottom + ResizeGrips.PanelOuter)
        {
            double distance = Math.Max(0, Math.Min(Math.Min(point.X - r.X, r.Right - point.X), r.Bottom - point.Y) - ResizeGrips.PanelInner);
            near = 1 - Math.Clamp(distance / HintReach, 0, 1);
        }
        _grips.Near(near);
        // Messages reach the layer while it is on top; when another window has them, the polling still lights the edge.
        if (!_grips.IsMouseOver) SetHoverEdge(edge);
        return edge;
    }

    private bool NearGrips(Point screen)
    {
        if (!PanelResizable || PresentationSource.FromVisual(Stage) is null) return false;
        var area = ResizeGrips.PanelArea(IslandBox());
        var point = Stage.PointFromScreen(screen);
        return point.X >= area.X - GripSlack && point.X <= area.Right + GripSlack && point.Y >= area.Y + _header.Row * _scale && point.Y <= area.Bottom + GripSlack;
    }

    private void SetHoverEdge(ResizeEdges edge)
    {
        if (edge == _hoverEdge) return;
        _hoverEdge = edge;
        _grips.Cursor = EdgeCursor(edge);
        if (!Resizing) _grips.Light(edge, false, "");
    }

    // The grips are in the edge's frame (MainWindow.Edge.cs); the cursor points the way the edge moves on the screen.
    private Cursor? EdgeCursor(ResizeEdges edge)
    {
        double fx = edge.HasFlag(ResizeEdges.Left) ? -1 : edge.HasFlag(ResizeEdges.Right) ? 1 : 0, fy = edge.HasFlag(ResizeEdges.Bottom) ? 1 : 0;
        if (fx == 0 && fy == 0) return null;
        var (x, y) = _frame.ToScreen(fx, fy);
        return x == 0 ? Cursors.SizeNS : y == 0 ? Cursors.SizeWE : x * y > 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    private void GripMove(object sender, MouseEventArgs e)
    {
        if (_moveArmed) { if (_movePressed) { e.Handled = true; MoveDrag(); } return; }
        if (!Dragging) { if (!_resizeSettling) SetHoverEdge(_grips.EdgeAt(e.GetPosition(_grips))); return; }
        e.Handled = true;
        if (ResizeOvertaken()) return;
        var cursor = WindowsHost.Cursor();
        double scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1;
        // The grips are in the edge's frame: x along the edge, y away from it (MainWindow.Edge.cs).
        var (dx, dy) = _frame.FromScreen((cursor.X - _resizeStart.X) / scale, (cursor.Y - _resizeStart.Y) / scale);
        // The open panel's size is unscaled: the pointer moves it by its move divided by the panel's scale.
        if (!_resizeCompact) { dx /= _scale; dy /= _scale; }
        // On a side edge the page's height runs along the edge and its width away from it.
        bool across = !_resizeCompact && _frame.Across;
        // Both sides move, so the length along the edge changes by twice the pointer's move.
        double along = (across ? _resizeFrom.Height : _resizeFrom.Width) + (_resizeEdge.HasFlag(ResizeEdges.Right) ? 2 * dx : _resizeEdge.HasFlag(ResizeEdges.Left) ? -2 * dx : 0);
        double away = (across ? _resizeFrom.Width : _resizeFrom.Height) + (_resizeEdge.HasFlag(ResizeEdges.Bottom) ? dy : 0);
        double width = across ? away : along;
        if (_resizeCompact)
        {
            var (min, max) = CompactWidthRange(EffectiveStyle);
            _springWidth.Target = Spring.Resist(width, min, max);
        }
        else
        {
            double height = across ? along : away;
            var (minWidth, maxWidth, minHeight, maxHeight) = PageLimits;
            _springWidth.Target = Spring.Resist(width, minWidth, maxWidth);
            _springHeight.Target = Spring.Resist(height, minHeight, maxHeight);
        }
        StartSpring();
    }

    private void GripDown(object sender, MouseButtonEventArgs e)
    {
        if (_grips.Shape == ResizeGripLayer.Form.Move)
        {
            if (_grips.OverMove(e.GetPosition(_grips))) { e.Handled = true; BeginMovePress(); }
            return;
        }
        var edge = _grips.EdgeAt(e.GetPosition(_grips));
        if (edge == ResizeEdges.None) return;
        e.Handled = true;
        bool compact = _grips.Shape == ResizeGripLayer.Form.Compact;
        var style = EffectiveStyle;
        // A double click on an end does what the settings choose (MainWindow.Move.cs).
        if (compact && e.ClickCount >= 2 && !_resizeSettling) { GripDoubleClicked(style); return; }
        BeginResize(compact);
        _resizeSettling = false; _resizeByKey = false;
        _resizeEdge = edge; _resizeStart = WindowsHost.Cursor();
        SetHoverEdge(edge);
        _grips.Light(edge, true, SizeLabel(_resizeFrom.Width, _resizeFrom.Height));
        _grips.CaptureMouse();
    }

    // Starts resizing the open page or the collapsed surface, for a drag's press or the resize shortcut. A start while the same
    // form still settles picks it up where it is heading; the other form still settling is saved first. A page starts from the
    // size it shows, which a smaller monitor may hold below the saved one. Returns whether it picked up a settling resize.
    private bool BeginResize(bool compact)
    {
        bool settling = SettlingHere(compact);
        if (_resizeSettling && !settling) { StopSpring(); FinishResize(); }
        if (!compact) { _monitorView?.CloseCard(); _detailPopup?.Close(); }
        if (settling) _resizeFrom = (_springWidth.Target, _springHeight.Target);
        else
        {
            var (minWidth, maxWidth, minHeight, maxHeight) = PageLimitsOf(CurrentPage);
            var (width, height) = DeckPages.Size(Settings, CurrentPage);
            _resizeFrom = compact ? (Collapsed.Surface.Width, 0) : (Math.Clamp(width, minWidth, maxWidth), Math.Clamp(height, minHeight, maxHeight));
            _springWidth = new Spring(_resizeFrom.Width); _springHeight = new Spring(_resizeFrom.Height);
            _resizeChanged = false;
        }
        _resizeCompact = compact; _resizeStyle = EffectiveStyle; _resizePage = CurrentPage; Island.SettledClip = false;
        return settling;
    }

    // A resize of this form, and of the page or style still shown, is settling.
    private bool SettlingHere(bool compact) => _resizeSettling && _resizeCompact == compact && (compact ? EffectiveStyle == _resizeStyle : CurrentPage == _resizePage);

    private string SizeLabel(double width, double height) => _resizeCompact ? $"{width:0}" : $"{width:0} × {height:0}";

    private void StartSpring()
    {
        if (_springHooked) return;
        _springHooked = true; _springTime = TimeSpan.Zero;
        CompositionTarget.Rendering += SpringFrame;
    }
    private void StopSpring()
    {
        if (!_springHooked) return;
        _springHooked = false;
        CompositionTarget.Rendering -= SpringFrame;
    }

    // One frame of the spring: the surface takes the spring's size in whole DIPs, and only when that changed. It runs while the
    // spring moves; a pointer held still unhooks it until the next move.
    private void SpringFrame(object? sender, EventArgs e)
    {
        if (ResizeOvertaken()) return;
        var time = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        // WPF may raise Rendering more than once for one frame.
        if (_springTime != TimeSpan.Zero && time == _springTime) return;
        double dt = _springTime == TimeSpan.Zero ? 1 / 120.0 : (time - _springTime).TotalSeconds;
        _springTime = time;
        bool moving = _springWidth.Step(dt);
        if (!_resizeCompact) moving |= _springHeight.Step(dt);
        ApplyResize(Math.Round(_springWidth.Value), Math.Round(_springHeight.Value));
        if (moving) return;
        StopSpring();
        if (_resizeSettling) FinishResize();
    }

    // Something else opened or closed the panel during the drag or while it settled (a hotkey, a full-screen rule), turned the
    // collapsed surface into another form (a maximized window turns it into the line), or switched the page: that wins. The drag
    // ends where it is and the size reached so far is kept; it is never applied to the other form or page.
    private bool ResizeOvertaken()
    {
        if (!Resizing || _resizeCompact != _expanded && (_resizeCompact ? EffectiveStyle == _resizeStyle : CurrentPage == _resizePage)) return false;
        StopSpring();
        _resizeEdge = ResizeEdges.None; _resizeSettling = true;
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        FinishResize();
        return true;
    }

    // The widths a drag can give the collapsed surface: the style's range on this monitor, but never narrower than what the content
    // must show (CompactMusicLayout.RequiredWidth), which the surface keeps whatever width is saved.
    private (double Min, double Max) CompactWidthRange(CompactStyle style)
    {
        var (min, max) = CollapsedGeometry.WidthRange(style, MonitorBoundsDip, WorkDip, _scale);
        return (Math.Min(max, Math.Max(min, CollapsedGeometry.SurfaceWidth(style, Collapsed.Surface.Height, _compactRequired))), max);
    }

    private void ApplyResize(double width, double height)
    {
        if (_resizeCompact)
        {
            var style = EffectiveStyle;
            var (min, max) = CompactWidthRange(style);
            width = Math.Clamp(width, min, max);
            if (width == Collapsed.Surface.Width && !Settings.CompactAutoWidth(style)) return;
            _resizeChanged = true;
            // The surface is drawn at the scale; its width is saved before it.
            Settings.SetCompactWidth(style, width / _scale); Settings.SetCompactAutoWidth(style, false);
            UpdateCollapsedGeometry();
            SetExpanded(false, instant: true);
            _grips.Light(LitEdges, true, SizeLabel(Collapsed.Surface.Width, 0));
            return;
        }
        var (minWidth, maxWidth, minHeight, maxHeight) = PageLimits;
        width = Math.Clamp(width, minWidth, maxWidth);
        height = Math.Clamp(height, minHeight, maxHeight);
        if ((width, height) == DeckPages.Size(Settings, _resizePage)) return;
        _resizeChanged = true;
        DeckPages.SetSize(Settings, _resizePage, width, height);
        _pageWidth = width;
        if (_resizePage == DeckPage.Usage) _usageWidth = width;
        SetExpanded(true, instant: true);
        // The surface follows every frame. The monitor page and the usage page's columns are built again only where they cross a
        // layout step and stretch in between, so they follow too; the model details and the media page are built again whole,
        // about 25 times a second during the drag, and once more when it ends (FinishResize saves the size).
        if (_resizePage == DeckPage.Monitor) RenderMonitor();
        else if (Environment.TickCount64 - _resizeRendered >= 40)
        {
            _resizeRendered = Environment.TickCount64;
            // Columns that stay within their size step stretch; nothing else in the panel changes meanwhile.
            if (_resizePage != DeckPage.Usage || _details || ColumnsBuild() != _usageBuilt) RenderUsage();
        }
        _grips.Light(LitEdges, true, SizeLabel(width, height));
    }

    private ResizeEdges LitEdges => _resizeEdge != ResizeEdges.None ? _resizeEdge : _resizeByKey ? _keyEdges : _hoverEdge;

    // One press (or repeat) of the resize shortcut's arrow keys. Open, Left and Right change the page's width and Up and Down its
    // height; collapsed, Left and Right change the surface's width as dragging an end does, and Up and Down do nothing. Not while
    // a drag is under way, nor where a drag could not start (a preview, hidden, a quota alert, powering off).
    internal void ResizeFromKeyboard(KeyboardResize.Direction direction)
    {
        bool compact = !_expanded;
        // A collapsed surface on a side edge runs down the screen: Up and Down change its length there (MainWindow.Edge.cs).
        bool horizontal = KeyboardResize.Horizontal(direction) != (compact && _frame.Across);
        if (Dragging || (compact ? !CompactResizable || !horizontal : !PanelResizable)) return;
        // A press while the surface still settles moves on from where it is heading, as holding a key repeats.
        bool settling = SettlingHere(compact);
        var limits = PageLimitsOf(CurrentPage);
        var (min, max) = !horizontal ? (limits.MinHeight, limits.MaxHeight)
            : compact ? CollapsedGeometry.WidthRange(EffectiveStyle, MonitorBoundsDip, WorkDip, _scale) : (limits.MinWidth, limits.MaxWidth);
        double current = settling ? horizontal ? _springWidth.Target : _springHeight.Target
            : Math.Clamp(compact ? Collapsed.Surface.Width : horizontal ? DeckPages.Size(Settings, CurrentPage).Width : DeckPages.Size(Settings, CurrentPage).Height, min, max);
        double next = KeyboardResize.Next(current, direction, min, max);
        // At a limit nothing moves and nothing is saved: a key held there costs nothing, and a collapsed surface that widens to
        // its content keeps doing so.
        if (next == current) return;
        // A drag still settling keeps its own ending (KeyboardResize only moves its target on).
        if (!BeginResize(compact)) { _resizeByKey = true; _keyEdges = ResizeEdges.None; }
        _resizeSettling = true;
        _keyEdges |= horizontal ? ResizeEdges.Left | ResizeEdges.Right : ResizeEdges.Bottom;
        if (horizontal) _springWidth.Target = next; else _springHeight.Target = next;
        StartSpring();
    }

    // The button is up: the spring settles where the pointer left it, and then the size is saved.
    private void EndResize()
    {
        var edge = _resizeEdge;
        _resizeEdge = ResizeEdges.None; _resizeSettling = true;
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        _grips.Light(edge, true, "");
        if (_springHooked) return;
        FinishResize();
    }

    private void FinishResize()
    {
        bool compact = _resizeCompact, byKey = _resizeByKey;
        _resizeSettling = false; _resizeCompact = false; _resizeByKey = false; _keyEdges = ResizeEdges.None;
        // Saved like any change from the settings window, which applies it to every panel and fits the window again. A press that
        // moved nothing only fits the window again.
        if (_resizeChanged) _app.UpdateSettings(Settings.Copy()); else ApplySettings();
        _resizeChanged = false;
        // The label leaves; the edge stays lit while the pointer is still on it.
        _hoverEdge = ResizeEdges.None;
        UpdateGrips();
        var cursor = WindowsHost.Cursor();
        SetHoverEdge(TrackGrips(cursor));
        _grips.Light(_hoverEdge, false, "");
        // A panel closed meanwhile (the expand hotkey, overtaking the resize) stays closed, even with the pointer over it.
        if (compact || !_expanded) return;
        if (Island.IsMouseOver || InsideOpenPanel(cursor)) { PointerChanged(true); return; }
        // The resize shortcut used with the pointer outside: the panel closes after the usual delay, as it would have without it
        // (one the expand hotkey opened stays, held by its pin until the pointer has been in it).
        if (byKey) { PointerChanged(false); return; }
        // A drag that made the panel larger usually ends with the pointer ahead of the edge, past the panel: the panel stays a
        // moment for the pointer to come back (TrackPointer takes it as an entry), and closes only if it does not.
        _pointerInside = false; _hover.Stop();
        _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(ResizeLinger); _leave.Start();
    }
}
