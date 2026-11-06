using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// The edge the panel hangs from (DeckSettings.Dock, for the style shown). Everything is laid out in the edge's frame (EdgeFrame), as if the panel
// hung from the top: MonitorBoundsDip and WorkDip are the frame's, the collapsed shape, the hover area, the open panel and the
// resize grips are computed in it, and Stage draws them through the frame's mapping (its layout transform). The island's content is
// turned back upright (IslandContent), so text reads as usual on every edge and its pop-ups, tooltips and screen points need no
// change. FrameWidth runs along the edge and FrameHeight away from it; on the left and right edges they are the window's height
// and width.
public partial class MainWindow
{
    private EdgeFrame _frame = EdgeFrame.For(ScreenEdge.Top, 1920, 1080, new DipRect(0, 0, 1920, 1032));
    // The edge the transforms were last set for; null before the first time.
    private ScreenEdge? _framedEdge;

    private double FrameWidth
    {
        get => _frame.Across ? Height : Width;
        set { if (_frame.Across) Height = value; else Width = value; }
    }
    private double FrameHeight
    {
        get => _frame.Across ? Width : Height;
        set { if (_frame.Across) Width = value; else Height = value; }
    }

    // The collapsed content stands as a column: the notch on a side edge (CompactColumn).
    private bool ColumnFor(CompactStyle style) => _frame.Across && style == CompactStyle.Notch;
    private bool CompactColumnMode => ColumnFor(Collapsed.Style);

    // Sets the transforms for the frame's edge. Returns whether the edge changed since the last time; the first time does not count.
    private bool ApplyFrame()
    {
        var edge = _frame.Edge;
        if (_framedEdge == edge) return false;
        bool changed = _framedEdge is not null;
        // Between the top and the bottom, or the left and the right, the row stays as it was built and keeps the width it asked for.
        bool turned = _framedEdge is not { } before || (before is ScreenEdge.Left or ScreenEdge.Right) != _frame.Across;
        _framedEdge = edge;
        Stage.LayoutTransform = Turn(_frame.Matrix);
        IslandContent.LayoutTransform = Turn(_frame.Upright);
        // On the top and bottom edges the open page follows the island's width (InitializeScale). On a side edge it keeps the page's
        // size (SetExpanded), centred along the edge, so the header and the page stay together while the island changes its length.
        // It hangs from the edge, so it is anchored there and is not scaled as the panel opens or closes (it only fades).
        if (_frame.Across) BindingOperations.ClearBinding(ExpandedContent, WidthProperty);
        else if (BindingOperations.GetBindingExpression(ExpandedContent, WidthProperty) is null)
            ExpandedContent.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = Island, Converter = _unscaled });
        (ExpandedContent.HorizontalAlignment, ExpandedContent.VerticalAlignment) = edge switch
        {
            ScreenEdge.Bottom => (HorizontalAlignment.Center, VerticalAlignment.Bottom),
            ScreenEdge.Left => (HorizontalAlignment.Left, VerticalAlignment.Center),
            ScreenEdge.Right => (HorizontalAlignment.Right, VerticalAlignment.Center),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Top)
        };
        _grips.Upright = Turn(_frame.Upright);
        // The line's playback progress runs along the edge; its length and thickness are set again for it.
        MusicIndicator.ClearValue(WidthProperty); MusicIndicator.ClearValue(HeightProperty);
        MusicIndicator.HorizontalAlignment = _frame.Across ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        MusicIndicator.VerticalAlignment = _frame.Across ? VerticalAlignment.Top : VerticalAlignment.Center;
        _compactRendered = null; _alertRendered = null;
        // The row was built for the other orientation.
        if (turned) { _compactWanted = double.NaN; _compactColumn = double.NaN; }
        return changed;
    }

    private static Transform Turn((double M11, double M12, double M21, double M22) m)
    {
        if (m == (1, 0, 0, 1)) return Transform.Identity;
        var transform = new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, 0, 0));
        transform.Freeze();
        return transform;
    }

    // The capsule floats below the top of the screen, but sitting by the bottom it is docked with it, as the notch on the bottom edge
    // is: within CollapsedHover.BridgeMaxGap above the work area's bottom it hangs its panel from there, and below it, over a bottom
    // taskbar, from the screen's bottom edge; the panel's corners on that edge are square, as they are under a capsule near the top.
    // Its place stays a place on the screen (ShapeFor). _screenMonitor and _screenWork are this display's, relative to its corner.
    private DipRect _screenMonitor = FallbackMonitor, _screenWork = FallbackWork;
    private (ScreenEdge Edge, bool OverTaskbar)? CapsuleBottomDock()
    {
        var surface = CollapsedGeometry.Resolve(Settings, CompactStyle.Capsule, _screenMonitor, _screenWork, _pixelsPerDip, null, 0, _scale).Surface;
        if (surface.Y - _screenWork.Y <= CollapsedHover.BridgeMaxGap) return null;
        if (surface.Bottom > _screenWork.Bottom + .5) return (ScreenEdge.Bottom, true);
        return _screenWork.Bottom - surface.Bottom <= CollapsedHover.BridgeMaxGap ? (ScreenEdge.Bottom, false) : null;
    }

    // Moving to another edge or anchor moves and resizes the window, and Windows shows a layered window's last picture at its new
    // place and size before WPF draws again: the island flashed where the old picture put it (at the other end of the screen,
    // or in its middle for the side edges). As HideBlank does, the island is cleared first, and the window moves once that empty
    // frame has been drawn (ApplySettings runs again then). Returns whether ApplySettings waits for it. While the surface is dragged
    // the island is hidden already (MainWindow.Move.cs).
    private (ScreenEdge Edge, bool OverTaskbar)? _framedDock;
    private bool _edgeClearing, _edgeCleared, _edgeArriving;
    private bool ClearBeforeMoving(bool animatePage)
    {
        if (_edgeClearing) return true;
        if (_framedDock is not { } framed || Dock(EffectiveStyle) == framed || !IsVisible || IsSuppressed || _hiding || _moveOverlay is not null || !(HoverZone.Opacity > 0)) return false;
        _edgeClearing = true;
        SetImmediate(HoverZone, OpacityProperty, 0d);
        int frames = 0;
        void Rendered(object? sender, EventArgs e)
        {
            if (PresentationSource.FromVisual(this) is null) { CompositionTarget.Rendering -= Rendered; _edgeClearing = false; return; }
            // Rendering comes before each frame: by the third, the empty frame has been drawn.
            if (++frames < 3) return;
            CompositionTarget.Rendering -= Rendered;
            _edgeClearing = false; _edgeCleared = true;
            ApplySettings(animatePage);
        }
        CompositionTarget.Rendering += Rendered;
        return true;
    }

    // The panel moved to another edge while it was on show: it comes in there with a short fade rather than jumping, once its
    // content has been laid out for the edge (FitCompactToContent, queued at the same priority, runs first and resizes the surface
    // at once meanwhile), so it arrives at the size it keeps. cleared: the island was cleared before the move and shows again here.
    private void FadeInOnNewEdge(bool cleared)
    {
        // While the surface is dragged the island stays hidden; it is laid out on the edge being dragged to (MainWindow.Move.cs).
        if (_moveOverlay is not null) return;
        if (_bootWaiting || _bootDot || _poweringOff || !Soft.Enabled || Settings.AnimationDuration <= 0) { if (cleared) SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        SetImmediate(HoverZone, OpacityProperty, 0d);
        _edgeArriving = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _edgeArriving = false;
            // Hidden meanwhile: showing again brings the island back (ApplySettings).
            if (!IsVisible || _hiding || _moveOverlay is not null) return;
            SetImmediate(HoverZone, OpacityProperty, 1d);
            HoverZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Max(120, Settings.AnimationDuration * .6))) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        });
    }
}
