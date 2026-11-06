using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// Moving the collapsed notch, line or capsule by dragging it, when a double click on one of its ends is set to do that
// (DeckSettings.GripDoubleClick). The double click starts move mode: the surface's outline lights in the accent color and a move
// icon covers its content (ResizeGripLayer.Form.Move). Pressing the surface and dragging lifts it into an overlay as large as the
// monitor (MoveOverlay), where it follows the pointer and is held at the places CollapsedMove gives; the notch and the line move to
// the edge nearest the pointer. The island itself is hidden meanwhile; it lays itself out on the edge being dragged to, so the
// overlay shows the size the surface takes there (a notch on a side edge stands its content in a column). Released, the surface settles in the overlay, its place is saved
// like any setting, and the island shows there again under the overlay as that fades.
// Move mode ends with the drop, Esc, a right click, a press anywhere else, or once the pointer has stayed away from the surface for
// MoveIdle; a click that does not drag keeps it. While it lasts the panel does not open, and afterwards it opens only once the
// pointer has left the surface and come back, as after a window drag.
public partial class MainWindow
{
    private bool _moveArmed, _movePressed;
    private CompactStyle _moveStyle;
    private Point _movePress;
    private long _moveAway;
    private MoveOverlay? _moveOverlay;
    private MoveStart? _moveStart;
    private PointerPressObserver? _movePresses;
    private (double Width, double Height, double Shoulder, CornerRadius Corners, Geometry Outline)? _moveOutline;
    // How far the pointer moves with the button down, in device pixels, before the surface lifts; how far from the surface, in
    // DIPs, it still counts as on it; how long, in ms, it may stay farther away before move mode ends.
    private const double MoveThreshold = 4, MoveSlack = 24;
    private const int MoveIdle = 3000;

    private static bool Movable(CompactStyle style) => Enum.IsDefined(style);

    // While the notch or the line is dragged, the edge and anchor it is on: the hidden island lays itself out there (UpdateCollapsedGeometry)
    // and reports its size to the overlay (ReportMoveShape), which shows the surface at the size it takes there.
    private (ScreenEdge Edge, bool OverTaskbar)? _dockOverride;
    private (ScreenEdge Edge, bool OverTaskbar) Dock(CompactStyle style)
    {
        if (_dockOverride is { } dragged && _moveOverlay is not null && style == _moveStyle) return dragged;
        return style == CompactStyle.Capsule && CapsuleBottomDock() is { } bottom ? bottom : Settings.Dock(style);
    }
    private void ReportMoveShape()
    {
        if (_moveOverlay is { } overlay && _dockOverride is { } dock && _frame.Edge == dock.Edge && Collapsed.Style == _moveStyle)
            overlay.Shape(dock.Edge, dock.OverTaskbar, Collapsed.Surface.Width, Collapsed.Surface.Height);
    }

    // Whether a taskbar takes part of this panel's display along an edge (the settings offer what to hang from there).
    internal bool TaskbarOn(ScreenEdge edge)
    {
        var (bounds, work, scale) = WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        var screenWork = new DipRect((work.Left - bounds.Left) / scale, (work.Top - bounds.Top) / scale, work.Width / scale, work.Height / scale);
        return CollapsedMove.HasTaskbar(edge, bounds.Width / scale, bounds.Height / scale, screenWork);
    }

    private void EnterMoveMode()
    {
        _moveArmed = true; _movePressed = false; _moveAway = 0; _moveStyle = EffectiveStyle;
        _hover.Stop(); _pointerInside = false;
        SetHoverEdge(ResizeEdges.None);
        _grips.Cursor = Cursors.SizeAll;
        _movePresses ??= new PointerPressObserver(Dispatcher, MovePressed);
        _escapeDismissal?.SetEnabled(true);
        UpdateGrips();
    }

    private void EndMoveMode()
    {
        if (!_moveArmed) return;
        _moveArmed = false; _movePressed = false; _moveAway = 0;
        _movePresses?.Dispose(); _movePresses = null;
        _escapeDismissal?.SetEnabled(_expanded && !_preview);
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        _grips.Cursor = null;
        _dragHold = true; _cursorInCompact = false;
        UpdateGrips();
    }

    // Leaves move mode at once, the surface back where it was if it was being dragged.
    private void CancelMove()
    {
        var overlay = _moveOverlay;
        _moveOverlay = null;
        // A panel being hidden keeps its island cleared (HideBlank); showing it again brings the island back.
        if (overlay is not null) { overlay.Close(); if (IsVisible && !IsSuppressed) SetImmediate(HoverZone, OpacityProperty, 1d); }
        EndMoveMode();
        // The island goes back to the edge it was on.
        if (_dockOverride is not null) { _dockOverride = null; if (overlay is not null) ApplySettings(); }
    }

    // The panel opened, hid, turned into another form or became a preview: move mode gives way.
    private bool MoveOvertaken => _expanded || _preview || IsSuppressed || !IsVisible || _poweringOff || EffectiveStyle != _moveStyle || _alert is not null && _moveOverlay is null;
    private void CheckMoveOvertaken() { if (_moveArmed && MoveOvertaken) CancelMove(); }

    // The outline the highlight follows: the island's own at rest.
    private Geometry MoveOutline(DipRect box)
    {
        var key = (box.Width, box.Height, Island.ShoulderRadius, Island.CornerRadius);
        if (_moveOutline is { } cached && (cached.Width, cached.Height, cached.Shoulder, cached.Corners) == key) return cached.Outline;
        var outline = IslandBorder.BuildOutline(box.Width, box.Height, Island.ShoulderRadius, Island.CornerRadius, 0);
        _moveOutline = (box.Width, box.Height, Island.ShoulderRadius, Island.CornerRadius, outline);
        return outline;
    }

    // From UpdateGrips: the highlight while armed, nothing while the overlay shows the surface.
    private void ShowMoveGrips()
    {
        if (_moveOverlay is not null) { _grips.Show(ResizeGripLayer.Form.None, default, 0, 0, false); return; }
        var box = IslandBox();
        _grips.ShowMove(box, MoveOutline(box), Collapsed.Style == CompactStyle.Notch, Collapsed.Style == CompactStyle.Line);
    }

    // From TrackPointer while armed: move mode ends once the pointer has stayed away long enough.
    private void TrackMove(Point cursor)
    {
        _pointerTrack.Interval = TimeSpan.FromMilliseconds(TrackNear);
        UpdateGrips();
        if (_movePressed) { _moveAway = 0; return; }
        if (NearMoveSurface(cursor, MoveSlack)) { _moveAway = 0; return; }
        if (_moveAway == 0) _moveAway = Environment.TickCount64;
        else if (Environment.TickCount64 - _moveAway >= MoveIdle) EndMoveMode();
    }

    private bool NearMoveSurface(Point screen, double slack)
    {
        var box = ScreenBounds(Island);
        if (box.IsEmpty) return false;
        box.Inflate(slack * _pixelsPerDip, slack * _pixelsPerDip);
        return box.Contains(screen);
    }

    // A press anywhere but on the surface ends move mode; the press itself goes on to wherever it was made.
    private void MovePressed(Point screen)
    {
        if (!_moveArmed || _movePressed || _moveOverlay is not null || NearMoveSurface(screen, 6)) return;
        EndMoveMode();
    }

    private void BeginMovePress()
    {
        _movePressed = true; _movePress = WindowsHost.Cursor();
        _grips.CaptureMouse();
    }

    private void MoveDrag()
    {
        var cursor = WindowsHost.Cursor();
        if (_moveOverlay is null)
        {
            if (Math.Abs(cursor.X - _movePress.X) < MoveThreshold && Math.Abs(cursor.Y - _movePress.Y) < MoveThreshold) return;
            if (MoveOvertaken) { CancelMove(); return; }
            StartMoveOverlay();
        }
        _moveOverlay?.Pointer(OnMonitor(cursor));
    }

    private void EndMovePress()
    {
        if (!_movePressed) return;
        _movePressed = false;
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        _moveOverlay?.Release();
    }

    // The monitor the move happens on, and a screen point in DIPs from its top-left corner.
    private (Rect Bounds, double Scale) _moveMonitor;
    private Point OnMonitor(Point screen)
    {
        var (bounds, scale) = _moveMonitor;
        return new Point((screen.X - bounds.Left) / scale, (screen.Y - bounds.Top) / scale);
    }

    // The surface lifts: the overlay takes it over from the island where it is, and hides the island and the highlight once it has
    // drawn its first frame, so nothing blinks in between.
    private void StartMoveOverlay()
    {
        var (bounds, work, scale) = WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        _moveMonitor = (bounds, scale);
        var screenWork = new DipRect((work.Left - bounds.Left) / scale, (work.Top - bounds.Top) / scale, work.Width / scale, work.Height / scale);
        var surface = Collapsed.Surface;
        double otherDepth = Collapsed.Style == CompactStyle.Line ? surface.Height : CollapsedGeometry.SurfaceHeight(CompactStyle.Notch, Settings.NotchHeight, MonitorBoundsDip, WorkDip, _scale);
        // The frame starts at the line the surface hangs from, so the line's distance from it is its place in the frame.
        var start = new MoveStart(Collapsed.Style, bounds.Width / scale, bounds.Height / scale, screenWork, _frame.ToScreen(surface), _frame.Edge,
            Settings.Dock(Collapsed.Style).OverTaskbar, Collapsed.Style == CompactStyle.Line ? Settings.LineAnchor : Settings.NotchAnchor, surface.Width, surface.Height, otherDepth, Collapsed.Shoulder, Collapsed.BottomRadius,
            Collapsed.Style == CompactStyle.Line ? surface.Y : 0, OnMonitor(_movePress));
        var overlay = new MoveOverlay(bounds, scale, start);
        _moveStart = start; _moveOverlay = overlay;
        overlay.Settled += place => FinishMove(overlay, place);
        overlay.DockChanged += (edge, over) =>
        {
            if (!ReferenceEquals(_moveOverlay, overlay)) return;
            _dockOverride = (edge, over);
            ApplySettings();
        };
        overlay.ContentRendered += (_, _) =>
        {
            if (!ReferenceEquals(_moveOverlay, overlay)) return;
            SetImmediate(HoverZone, OpacityProperty, 0d);
            UpdateGrips();
        };
        overlay.Show();
    }

    // The surface has settled where it was dropped: that place is saved, and the island takes over from the overlay there.
    private void FinishMove(MoveOverlay overlay, MovePlacement place)
    {
        if (!ReferenceEquals(_moveOverlay, overlay) || _moveStart is not { } start) { overlay.Close(); return; }
        _moveOverlay = null; _dockOverride = null;
        var moved = Settings.Copy();
        if (start.Style == CompactStyle.Capsule) (moved.CapsuleOffsetX, moved.CapsuleTop) = CollapsedMove.CapsulePlace(place.X, place.Y, start.Surface.Width, start.Work);
        else if (start.Style == CompactStyle.Line)
        {
            moved.LineEdge = place.Edge; moved.LineOffsetX = place.Offset;
            if (place.Anchor is { } anchor) moved.LineAnchor = anchor;
        }
        else
        {
            moved.NotchEdge = place.Edge; moved.NotchOffsetX = place.Offset;
            if (place.Anchor is { } anchor) moved.NotchAnchor = anchor;
        }
        EndMoveMode();
        _app.UpdateSettings(moved);
        // A capsule dropped by the bottom or back at the top docks with another edge: the island comes in there once laid out
        // (FadeInOnNewEdge); otherwise it shows at once under the fading overlay.
        if (!_edgeArriving && !_edgeClearing) SetImmediate(HoverZone, OpacityProperty, 1d);
        overlay.FadeOut();
    }

    // A double click on an end of the collapsed surface: move mode, or the auto width turned on or off (the line returns to its
    // usual length). Turned off, the surface keeps the width it shows.
    private void GripDoubleClicked(CompactStyle style)
    {
        if (Settings.GripDoubleClick == GripDoubleClick.Move && Movable(style)) { EnterMoveMode(); return; }
        var fitted = Settings.Copy();
        if (style == CompactStyle.Line) fitted.LineLength = CollapsedLimits.LineLengthDefault;
        else if (fitted.CompactAutoWidth(style)) { fitted.SetCompactWidth(style, Collapsed.Surface.Width / _scale); fitted.SetCompactAutoWidth(style, false); }
        else fitted.SetCompactAutoWidth(style, true);
        _app.UpdateSettings(fitted);
    }
}
