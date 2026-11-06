using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// Booting and powering off. Every form (notch, capsule, line) starts and ends as the same round dot, DotMotion.Size across,
// resting on a floor at the bottom of its place; DotMotion gives how it moves. Booting, the dot fades in, jumps twice (up, then
// down onto that floor) and swells into the collapsed shape once the second landing has sprung back. Powering off, the surface
// draws back into the dot, which jumps twice and at the second landing merges into the water: a neck joins it to the plane,
// the free drop is taken in leaving a mound, and the mound settles flat.
// A panel boots only when it first appears (TwelveIsland starts, a monitor is connected); showing it again after a hide (the tray,
// a full-screen rule) is immediate. It powers off only when TwelveIsland exits; hiding is immediate. Without animations it appears
// and goes at once.
public partial class MainWindow
{
    // _booting asks the next SetExpanded for the boot's tracks; _poweringOff holds the panel still while it goes.
    // _bootDot keeps the surface as the round jumping circle: ApplySettings and music updates call SetExpanded while the
    // jumps play, and without this they would grow the island to its collapsed size under the same lift, so the whole
    // capsule or line would jump (the notch sits at the top and is clipped, so the same fault is easy to miss there).
    private bool _booting, _poweringOff, _bootWaiting, _bootDot;
    // Pointing at the surface does not open the panel while it waits out of sight for its boot, or until the boot has played
    // (TrackPointer): a pointer already resting there would open an invisible panel, or cut the swell short.
    private long _bootEnds;
    private bool BootHoldsPointer => _bootWaiting || Environment.TickCount64 < _bootEnds;
    // The swell from the dot into the shape (SetExpanded), and the surface drawing back into the dot, as multiples of the
    // duration set for the panel: these change the panel's shape as opening and closing do. The dot's own motion keeps its
    // pace (DotMotion).
    private const double BootSwell = 1.0, PowerOffShrink = PanelMotion.CloseFactor;
    // How long TwelveIsland's exit waits at most for the panels to power off (App.ExitApplication).
    internal static int PowerOffLength(int duration) => (int)(duration * PowerOffShrink + DotMotion.PowerOffLength);

    // The dot: at the middle of the surface, resting on a floor at the bottom of that place, and low enough that its jumps stay
    // inside the window (the line and a low notch are near the top edge, where the jumps were cut off).
    private double DotLeft(CollapsedShape shape) => shape.Surface.X + (shape.Surface.Width - DotMotion.Size) / 2 - _windowOffset;
    private static double DotTop(CollapsedShape shape) => Math.Max(DotMotion.Headroom, shape.Surface.Y + Math.Max(0, shape.Surface.Height - DotMotion.Size));

    // The dot's pose moves the island: its lift above the floor (up the screen) and its squash about the middle of that floor.
    // Outside the boot and the power-off the island has no lift and a scale of 1.
    private ScaleTransform? _dotSquash;
    private TranslateTransform? _dotLift;
    private void ResetDotPose()
    {
        if (_dotSquash is null || _dotLift is null) return;
        SetImmediate(_dotSquash, ScaleTransform.ScaleXProperty, 1d); SetImmediate(_dotSquash, ScaleTransform.ScaleYProperty, 1d);
        SetImmediate(_dotLift, TranslateTransform.YProperty, 0d);
    }
    // Moves the dot by pose (its time in milliseconds) for length ms, starting after delay ms; completed runs at the end. The
    // pose's opacity is the panel's, through HoverZone. Lift is above the floor: WPF's Y grows down, so the translate is -Lift.
    private void PoseDot(Func<double, DotPose> pose, double length, double delay = 0, Action? completed = null)
    {
        if (_dotSquash is null || _dotLift is null)
        {
            _dotSquash = new ScaleTransform(); _dotLift = new TranslateTransform();
            Island.RenderTransformOrigin = new Point(.5, 1);
            Island.RenderTransform = new TransformGroup { Children = { _dotSquash, _dotLift } };
        }
        PoseAnimation Part(Func<DotPose, double> part) => new(t => part(pose(t)), length) { BeginTime = TimeSpan.FromMilliseconds(delay) };
        var lift = Part(p => -p.Lift);
        if (completed is not null) lift.Completed += (_, _) => completed();
        _dotLift.BeginAnimation(TranslateTransform.YProperty, lift, HandoffBehavior.SnapshotAndReplace);
        _dotSquash.BeginAnimation(ScaleTransform.ScaleXProperty, Part(p => p.ScaleX), HandoffBehavior.SnapshotAndReplace);
        _dotSquash.BeginAnimation(ScaleTransform.ScaleYProperty, Part(p => p.ScaleY), HandoffBehavior.SnapshotAndReplace);
        HoverZone.BeginAnimation(OpacityProperty, Part(p => p.Opacity), HandoffBehavior.SnapshotAndReplace);
    }

    // Whether the island can pass a size of width x height at left and gap (window coordinates) by the opening spring's give
    // without leaving the window, which would cut it off for those frames. Width and Height are the values on screen now, in the
    // edge's frame (MainWindow.Edge.cs).
    private bool HasRoomToBounce(double width, double height, double left, double gap)
    {
        double give = Curves.BounceOvershoot;
        double grow = Math.Max(0, width - Island.Width) * give, rise = Math.Max(0, height - Island.Height) * give;
        return double.IsFinite(grow) && double.IsFinite(rise) && left - grow / 2 >= 0 && left + width + grow / 2 <= FrameWidth && gap + height + rise <= FrameHeight;
    }

    private bool CanAnimateLifecycle => IsVisible && Soft.Enabled && Settings.AnimationDuration > 0 && !_preview;

    // Starts the boot. The panel stays out of sight until the dispatcher is idle: on TwelveIsland's start the first frames come
    // late while the panel is built and the data read, which ate the dot and the first half of the swell. A panel that is busy
    // longer than BootWait boots anyway. The panel is out of sight from the start (the constructor), so the window is never
    // seen before it has its place on the monitor.
    private void Boot()
    {
        if (_poweringOff || _app.Exiting || _retiring) return;
        if (_expanded || IsSuppressed || !CanAnimateLifecycle) { SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        SetImmediate(HoverZone, OpacityProperty, 0d);
        _bootWaiting = true;
        int boot = ++_boot;
        var wait = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(BootWait) };
        void Start() { wait.Stop(); if (boot == _boot) BeginBoot(); }
        wait.Tick += (_, _) => Start();
        wait.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Start);
    }
    private int _boot;
    private const int BootWait = 700;

    // The dot fades in and jumps twice; once the second landing has sprung back and the dot is a still circle again, the lift
    // and squash are cleared so only the round dot has jumped, then the surface swells from that resting circle into the collapsed shape on the opening
    // spring (SetExpanded's boot tracks). The compact content arrives last.
    private void BeginBoot()
    {
        int boot = ++_boot; _bootWaiting = false;
        if (_poweringOff || _app.Exiting || _retiring) return;
        if (_expanded || IsSuppressed || !CanAnimateLifecycle) { SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        var shape = Collapsed = ShapeFor(EffectiveStyle);
        PlaceDot(shape);
        _bootDot = true; Island.SettledClip = false;
        SetImmediate(ShadowHost, OpacityProperty, 0d);
        if (Compact.Visibility == Visibility.Visible)
        {
            SetImmediate(Compact, OpacityProperty, 0d);
            SetImmediate(CompactScale, ScaleTransform.ScaleXProperty, Soft.Shrunk); SetImmediate(CompactScale, ScaleTransform.ScaleYProperty, Soft.Shrunk);
        }
        const double swell = DotMotion.SwellStart;
        _bootEnds = Environment.TickCount64 + (long)(swell + Settings.AnimationDuration * BootSwell);
        SetImmediate(HoverZone, OpacityProperty, 0d);
        PoseDot(DotMotion.Boot, swell, completed: () =>
        {
            if (boot != _boot || _poweringOff || _app.Exiting || _retiring) return;
            // Clear the pose before the swell: continuing the landing on Island would lift the whole shape (capsule and line
            // especially), not only the circle that jumped.
            _bootDot = false;
            ResetDotPose();
            SetImmediate(HoverZone, OpacityProperty, 1d);
            // Opened meanwhile (a hotkey), the panel is already on its way; otherwise the dot swells into the shape.
            if (_expanded) return;
            _booting = true;
            SetExpanded(false);
        });
    }

    // Cuts a boot short when the panel opens before it has played (a hotkey): the panel opens from wherever the surface is.
    private void EndBoot()
    {
        if (!BootHoldsPointer) return;
        ++_boot; _bootWaiting = false; _bootEnds = 0; _bootDot = false;
        ResetDotPose();
        SetImmediate(HoverZone, OpacityProperty, 1d);
    }

    // While the boot circle is jumping, keep it a circle: refresh layout may call SetExpanded, which would otherwise size the
    // island to the collapsed form under the lift transform. Must not call ResetDotPose or PlaceDot here: those stop the jump
    // animation clock, so its Completed never runs and the swell never starts (the circle stays on screen).
    private bool KeepBootDot(CollapsedShape shape)
    {
        if (!_bootDot) return false;
        double dot = DotMotion.Size;
        SetImmediate(HoverZone, MarginProperty, new Thickness(DotLeft(shape), 0, 0, 0));
        SetImmediate(Island, WidthProperty, dot);
        SetImmediate(Island, HeightProperty, dot);
        SetImmediate(Island, MarginProperty, new Thickness(0, DotTop(shape), 0, 0));
        SetImmediate(Island, Border.CornerRadiusProperty, new CornerRadius(dot / 2));
        SetImmediate(Island, IslandBorder.ShoulderRadiusProperty, 0d);
        SetImmediate(Compact, OpacityProperty, 0d);
        SetImmediate(MusicIndicator, OpacityProperty, 0d);
        SetImmediate(ShadowHost, OpacityProperty, 0d);
        return true;
    }

    // The surface as the dot, at once: round, without shoulders, in the surface's own color and material for every form.
    private void PlaceDot(CollapsedShape shape)
    {
        double dot = DotMotion.Size;
        SetImmediate(HoverZone, MarginProperty, new Thickness(DotLeft(shape), 0, 0, 0));
        SetImmediate(Island, WidthProperty, dot); SetImmediate(Island, HeightProperty, dot);
        SetImmediate(Island, MarginProperty, new Thickness(0, DotTop(shape), 0, 0));
        SetImmediate(Island, Border.CornerRadiusProperty, new CornerRadius(dot / 2));
        SetImmediate(Island, IslandBorder.ShoulderRadiusProperty, 0d);
        SetImmediate(Island, IslandBorder.MaterialOpacityProperty, 1d);
        SetImmediate(Island, OpacityProperty, 1d);
        SetImmediate(_surface, SolidColorBrush.ColorProperty, PanelTheme.ColorOf("surface"));
        // The line's playback progress lies inside the surface: it is not part of the dot (SetExpanded brings it back).
        SetImmediate(MusicIndicator, OpacityProperty, 0d);
        ResetDotPose();
    }

    // Takes the panel off the screen on exit: an open panel closes at once, the compact content leaves first, then the surface
    // draws back into the dot on the settling curve, without the give an opening has; the dot then jumps twice and merges into
    // the water. gone runs once it has, or at once when nothing animates. Nothing opens it meanwhile; SetExpanded waits for it.
    internal void PowerOff(Action gone)
    {
        if (_poweringOff) { _whenGone += gone; return; }
        if (!CanAnimateLifecycle) { gone(); return; }
        int boot = ++_boot; _bootWaiting = false; _bootEnds = 0; _bootDot = false;
        if (_expanded) SetExpanded(false, instant: true);
        _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
        _poweringOff = true; _whenGone = gone; Island.SettledClip = false;
        UpdateGrips();
        ResetDotPose();
        var shape = Collapsed;
        double dot = DotMotion.Size;
        int shrink = (int)(Settings.AnimationDuration * PowerOffShrink);
        // The shoulders and the flat top go early, so the surface is round well before it is small. The line takes the
        // surface's color and material on the way, so it ends as the same dot as the other forms.
        Track compact = new(0, .35), surface = new(.1, 1), corners = new(.1, .55), shadow = new(0, .45), tone = new(0, .5);
        // The content leaves as every exit does (Soft.Out): it starts at once and slows into nothing.
        Animate(Compact, OpacityProperty, 0, shrink, compact, Soft.Out);
        // The line's playback progress leaves with the content: kept, it showed as a grey bar inside the shrinking dot.
        Animate(MusicIndicator, OpacityProperty, 0, shrink, compact, Soft.Out);
        Animate(CompactScale, ScaleTransform.ScaleXProperty, Soft.Shrunk, shrink, compact, Soft.Out);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, Soft.Shrunk, shrink, compact, Soft.Out);
        Animate(ShadowHost, OpacityProperty, 0, shrink, shadow, Soft.Out);
        Animate(Island, IslandBorder.MaterialOpacityProperty, 1, shrink, tone, Soft.Out);
        _surface.BeginAnimation(SolidColorBrush.ColorProperty, Timed(new ColorAnimation { To = PanelTheme.ColorOf("surface"), EasingFunction = Soft.InOut }, shrink, tone), HandoffBehavior.SnapshotAndReplace);
        Animate(Island, IslandBorder.ShoulderRadiusProperty, 0, shrink, corners, Soft.InOut);
        Island.BeginAnimation(Border.CornerRadiusProperty, Timed(new CornerRadiusAnimation { To = new CornerRadius(dot / 2), EasingFunction = Soft.Out }, shrink, corners), HandoffBehavior.SnapshotAndReplace);
        Island.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(0, DotTop(shape), 0, 0), EasingFunction = Soft.Settle }, shrink, surface), HandoffBehavior.SnapshotAndReplace);
        HoverZone.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(DotLeft(shape), 0, 0, 0), EasingFunction = Soft.Settle }, shrink, surface), HandoffBehavior.SnapshotAndReplace);
        Animate(Island, HeightProperty, dot, shrink, surface, Soft.Settle);
        Animate(Island, WidthProperty, dot, shrink, surface, Soft.Settle);
        Sink(shape, boot, shrink);
    }
    private Action? _whenGone;
    private DropMerge? _merge;

    // Once the surface is the dot (after `after` ms), it jumps twice; then the island hands off to the water-merge drawing.
    private void Sink(CollapsedShape shape, int boot, int after)
    {
        PoseDot(DotMotion.PowerOff, DotMotion.Touchdown, after, () =>
        {
            if (boot != _boot || !_poweringOff) return;
            MergeIntoWater(shape, boot);
        });
    }

    // Hides the round island and draws the dot sinking into the water in its place (DropMerge).
    private void MergeIntoWater(CollapsedShape shape, int boot)
    {
        ResetDotPose();
        SetImmediate(Island, OpacityProperty, 0d);
        SetImmediate(HoverZone, OpacityProperty, 1d);
        if (_merge is null) { _merge = new DropMerge { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }; Stage.Children.Add(_merge); }
        // Room for the drop above the waterline and for the line on either side; the waterline is on the floor the dot landed
        // on. The box may start above the window: only its empty top is cut off there.
        double width = DotMotion.Size * 4, height = DotMotion.Size * 2 + DropMerge.WaterBelow, floor = DotTop(shape) + DotMotion.Size;
        _merge.Width = width; _merge.Height = height;
        _merge.Margin = new Thickness(DotLeft(shape) + DotMotion.Size / 2 - width / 2, floor - (height - DropMerge.WaterBelow), 0, 0);
        _merge.Fill = _surface.Color;
        _merge.Visibility = Visibility.Visible;
        SetImmediate(_merge, DropMerge.ProgressProperty, 0d);
        var progress = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(DotMotion.SinkLength)) { EasingFunction = null };
        progress.Completed += (_, _) =>
        {
            if (boot != _boot || !_poweringOff) return;
            HideMerge();
            _poweringOff = false;
            var whenGone = _whenGone; _whenGone = null;
            whenGone?.Invoke();
        };
        _merge.BeginAnimation(DropMerge.ProgressProperty, progress);
    }

    private void HideMerge()
    {
        if (_merge is null) return;
        SetImmediate(_merge, DropMerge.ProgressProperty, 0d);
        _merge.Visibility = Visibility.Collapsed;
    }

    // A new collapsed style from the settings (notch, capsule or line): the surface changes into the new form instead of
    // switching (see SetExpanded). The panel stays as it is when it is open or hidden.
    private bool _morphingShape;
    internal void MorphShape()
    {
        // A preview from the settings window morphs too: the style chips sit on the page that previews the collapsed size.
        _morphingShape = !_expanded && !_poweringOff && IsVisible && Soft.Enabled && Settings.AnimationDuration > 0 && !_bootWaiting;
        ApplySettings(true);
        _morphingShape = false;
    }
}

// One part of the dot's pose (DotMotion) as an animation: its value at each moment is read from the pose, by the time in
// milliseconds since the animation began.
internal sealed class PoseAnimation : DoubleAnimationBase
{
    private readonly Func<double, double> _at;
    public PoseAnimation(Func<double, double> at, double length) { _at = at; Duration = TimeSpan.FromMilliseconds(Math.Max(1, length)); }
    protected override Freezable CreateInstanceCore() => new PoseAnimation(_at, 1);
    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
        => _at((animationClock.CurrentTime ?? TimeSpan.Zero).TotalMilliseconds);
}
