using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

public partial class MainWindow : Window
{
    private readonly App _app;
    // Hover timing runs on PromptTimer rather than DispatcherTimer; see PromptTimer for why.
    private readonly PromptTimer _hover, _leave, _pointerTrack, _pageSwitchHold;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(60) };
    // Fires just after each minute boundary, when the clock text and the reset countdowns change.
    private readonly DispatcherTimer _clock = new();
    // Fires just after each second boundary while the collapsed clock shows seconds; it redraws only the clock.
    private readonly DispatcherTimer _seconds = new();
    private readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // Compact hover follows the cursor position rather than window mouse messages, which reach
    // this per-pixel transparent window unreliably over the nearly invisible strip around the surface.
    private bool _cursorInCompact, _trackPointer;
    // Set while a window is dragged and kept until the pointer leaves the hover area after the drag (see TrackPointer).
    private bool _dragHold;
    // Checks run every 40 ms near the surface. Farther away the interval grows with the distance, assuming the cursor
    // approaches at no more than 10 physical pixels per millisecond, so it still arrives within one short interval.
    private const double TrackNear = 40, TrackFar = 160, ApproachSpeed = 10;
    private readonly WindowsHost _host;
    // Null for the main panel. The panels of other monitors take their usage data from it instead of reading their own.
    private readonly MainWindow? _lead;
    private bool _retiring;
    // The monitor this panel sits on; App assigns it. Without one, or while it is not connected, the primary monitor.
    internal DisplayInfo? Display { get; set; }
    private IntPtr MonitorHandle => Display is { } display && WindowsHost.MonitorScreen(display.Handle) is not null ? display.Handle : WindowsHost.PrimaryMonitor();
    // Animated in place so the indicator style can fade its surface in and out instead of switching brushes.
    private readonly SolidColorBrush _surface = new(Colors.Black);
    // The collapsed surface on this window's monitor, for the effective style (the line while a window rule switches to it).
    // Surface is in DIPs relative to the monitor's top-left corner. See CollapsedGeometry.
    // Until the constructor reads the monitor, the default notch on a 1920 x 1080 monitor stands in, so no early call meets null.
    internal CollapsedShape Collapsed { get; private set; } = CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Notch, FallbackMonitor, FallbackWork);
    internal WindowsHost WinHost => _host;
    private static readonly DipRect FallbackMonitor = new(0, 0, 1920, 1080), FallbackWork = new(0, 0, 1920, 1032);
    // This window's monitor in DIPs (its bounds; the work area relative to the monitor's top-left corner).
    internal DipRect MonitorBoundsDip { get; private set; } = FallbackMonitor;
    internal DipRect WorkDip { get; private set; } = FallbackWork;
    private double _pixelsPerDip = 1;
    // The window's left edge in DIPs from the monitor's left edge. The window follows the island, so the island and the open
    // panel are placed relative to this.
    private double _windowOffset;
    // The panel theme generation this window's surface and cached content were built for; see ApplyTheme.
    private int _themeGeneration = -1;
    private double _themeDpi;
    private VirtualDesktopPresence? _virtualDesktops;
    private EscapeDismissal? _escapeDismissal;
    private bool _expanded, _refreshing, _preview, _refreshRequested, _pointerInside;
    // Held open by the panel's own "悬停" button: neither the five-second timer nor the pointer leaving closes it until "收起".
    private bool _pinned;
    // An expanded panel closes on its own five seconds after opening (unless held, pinned by the hotkey, or a settings preview).
    private DispatcherTimer? _autoClose;
    private const int AutoCollapseMs = 5000;
    // Whether the five-second auto close is running: while it is, it alone ends the stay — the pointer leaving, or the cursor's
    // exit check, must not close the panel before its five seconds are up (a triggered panel stays five seconds, then collapses).
    private bool AutoClosing => _autoClose is { IsEnabled: true };
    // A preview from the settings window either holds the panel open or keeps it collapsed while a collapsed size is adjusted.
    private bool _previewExpanded;
    private Rect? _popupReturnArea;
    private int _days = 1;
    private bool _details;
    // Lets the old content of a rebuilt usage or media page leave while the new one arrives.
    private PageHandover _handover = null!;
    private int _applicationPage;
    private readonly Dictionary<Guid, int> _quotaPages = new();
    private DateTimeOffset? _updatedAt;
    private ScreenContext _context;
    // Threshold levels seen per quota; a rise after the first reading briefly widens the compact island.
    private readonly Dictionary<string, int> _levels = new();
    private bool _levelsSeeded;
    private CompactAlertInfo? _alert;
    private DeckSettings Settings => _app.Settings;
    public bool IsExpanded => _expanded;
    public bool IsSuppressed => !_preview && (Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Hide || HiddenByUser);
    public IReadOnlyList<ProviderSnapshot> Snapshots { get; private set; } = [];
    // Set when the first read has ended, with data or without (RefreshAsync); until then the usage page shows its skeleton.
    private bool _firstReadEnded;
    // The page built while it waited is not reused (RenderUsage compares its build key, which a failed read does not change).
    internal void FirstReadEnded() { if (_firstReadEnded) return; _firstReadEnded = true; _usageBuilt = null; }
    public string? LastRefreshError { get; private set; }

    private sealed record CompactAlertInfo(AppEntry Entry, string Name, string Label, double Used, DateTimeOffset? ResetAt);

    public MainWindow(App app, MainWindow? lead = null)
    {
        _app = app; _lead = lead;
        _host = new WindowsHost(() => MonitorHandle, () => new WindowInteropHelper(this).Handle, IslandStrip,
            () => Settings.ForceTopmost, () => Settings.BackgroundWindows);
        Rect? IslandStrip()
        {
            // The island border is what is drawn, in every style and whether the panel is collapsed or open — its size
            // and place are the layout's, so PointToScreen carries the window offset, the shape's own offset, the DPI,
            // the edge's frame and the open panel's own extent all at once. Opposite corners, as the frame may turn the island.
            if (!IsVisible || PresentationSource.FromVisual(this) is null || PresentationSource.FromVisual(Island) is null) return null;
            if (!(Island.ActualWidth > 0) || !(Island.ActualHeight > 0)) return null;
            return new Rect(Island.PointToScreen(new Point(0, 0)), Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight)));
        }
        UpdateCollapsedGeometry();
        _hover = new(Dispatcher, () =>
        {
            _hover!.Stop();
            if (IsSuppressed || !_pointerInside) return;
            // Opened by hovering: the panel may centre on the pointer (see SetExpanded).
            if (Settings.ExpandAtPointer && PresentationSource.FromVisual(Stage) is not null) _openCentre = Stage.PointFromScreen(WindowsHost.Cursor()).X + _windowOffset;
            SetExpanded(true);
        });
        _leave = new(Dispatcher, () => CheckPointerExit(WindowsHost.Cursor()));
        _pointerTrack = new(Dispatcher, TrackPointer) { Interval = TimeSpan.FromMilliseconds(TrackNear) };
        _pageSwitchHold = new(Dispatcher, () =>
        {
            _pageSwitchHold!.Stop();
            // Enter/leave events keep the current pointer state up to date during the hold.
            // Only now resume the normal exit delay for the newly sized page.
            if (_expanded && !_preview) PointerChanged(_pointerInside);
        }) { Interval = TimeSpan.FromSeconds(1) };
        InitializeComponent();
        // Out of sight until the boot (or, without animations, until it has its place): the window shows before it is placed.
        HoverZone.Opacity = 0;
        _handover = new PageHandover(LeavingContent);
        InitializeMusic();
        InitializeMonitor();
        InitializeClipboard();
        InitializeNotify();
        InitializeConvert();
        InitializeResize();
        InitializeScale();
        InitializeShadow();
        InitializeThemeFade();
        Island.Background = _surface;
        _refresh.Tick += async (_, _) => await RefreshAutomaticallyAsync();
        _clock.Tick += (_, _) => { UpdateClock(); ScheduleClock(); if (_expanded) RenderUsage(); else if (OffPeakChanged()) RenderCompact(); if (_lead is null) _app.CheckUpdatesIfDue(); };
        _seconds.Tick += (_, _) => { if (_compactClock is not null) _compactClock.Time = DateTime.Now; _seconds.Interval = SecondDelay(DateTime.Now); };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); _alert = null; RenderCompact(); if (!_expanded) SetExpanded(false); };
        // A new foreground context closes the panel directly (ApplySettings lays it out collapsed), so SetExpanded never sees the
        // panel turn from open to closed: the collapsed title is set to scroll its laps again here, as SetExpanded does.
        _host.ContextChanged += context =>
        {
            _context = context;
            if (!_preview && _detailPopup is null && _popupReturnArea is null && MediaSourceButton.ContextMenu?.IsOpen != true)
            {
                if (_expanded) ReplayCompactTitle();
                _expanded = false;
            }
            ApplySettings(true);
        };
        // The app under the same context changed. Only the app rules' answer can differ, so only a changed answer is
        // re-applied: collapsed, the shape crosses to it with the same animation a context change uses; expanded, it is
        // applied at once, so switching windows does not replay the expand animation. An unchanged answer re-applies
        // nothing — most switches are between apps with the same rules.
        _host.AppChanged += previous =>
        {
            var context = _host.Current;
            if (context == ScreenContext.Desktop) return;
            var before = Settings.Behavior(context, previous.Name, previous.Path, previous.Kind);
            var after = Settings.Behavior(context, _host.App.Name, _host.App.Path, _host.BackgroundKind);
            if (before == after) return;
            _context = context;
            ApplySettings(!_expanded);
        };
        // Esc also leaves move mode (MainWindow.Move.cs).
        SourceInitialized += (_, _) => { WindowsHost.ConfigureOverlay(this); _escapeDismissal = new EscapeDismissal(this, () => { if (_moveArmed) CancelMove(); else SetExpanded(false, keyboard: true); }); };
        Loaded += async (_, _) =>
        {
            _virtualDesktops ??= new VirtualDesktopPresence(this);
            _context = _host.DetectNow();
            if (_lead is { } lead) { Snapshots = lead.Snapshots; _updatedAt = lead._updatedAt; DetectThresholds(); UpdateNotice(); }
            ApplySettings();
            Boot();
            _trackPointer = true; UpdatePointerTracking();
            ScheduleClock(); _clock.Start(); StartMusic();
            if (_lead is null) { _app.AssignDisplays(); await RefreshAutomaticallyAsync(); }
        };
        Closing += (_, e) => { if (!_app.Exiting && !_retiring) { e.Cancel = true; SetExpanded(false); } };
        Closed += (_, _) => { CancelMove(); _escapeDismissal?.Dispose(); _virtualDesktops?.Dispose(); _host.Dispose(); _hover.Stop(); _leave.Stop(); _pointerTrack.Stop(); _pageSwitchHold.Stop(); _clock.Stop(); _seconds.Stop(); _refresh.Stop(); _alertTimer.Stop(); _clipSettle?.Stop(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; SetExpanded(false); } };
        // The collapsed island opens on hover, so a right click there does nothing, except leave move mode.
        MouseWheel += PanelWheel;
        MouseRightButtonUp += (_, e) => { if (_moveArmed) { CancelMove(); e.Handled = true; return; } if (!_expanded || !Settings.RightClickSettings) return; _app.OpenSettings(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetExpanded(false, keyboard: true); e.Handled = true; } };
        // A scale change moves the window to a rectangle Windows suggests; the island is centred again.
        // DpiChanged is a bubbling event: every Image raises it when its own DPI changes. Once the window has moved to a
        // monitor with another scale, each image the panel rebuilds while it is laid out reports a change, and laying out
        // again on those would never stop. Only the window's own change counts.
        // The wanted width is rounded up to whole device pixels of the monitor it was measured on, so a new scale drops it and the
        // next layout writes it again; the half-DIP threshold of the cache could keep a width rounded for the old scale.
        DpiChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, this) || e.OldDpi.DpiScaleX == e.NewDpi.DpiScaleX) return;
            _compactWanted = double.NaN;
            Dispatcher.BeginInvoke(ApplySettings);
        };
        // WM_DISPLAYCHANGE reaches the window after Windows has moved it into the new layout; SystemEvents reports the change
        // before that, when the window still has its old position. A taskbar change (auto-hide, size) alters the work area
        // without a display change.
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
        {
            const int DisplayChange = 0x007E, SettingChange = 0x001A, SetWorkArea = 0x002F;
            if (message == DisplayChange || message == SettingChange && wParam == SetWorkArea) _app.DisplaysChanged();
            return IntPtr.Zero;
        });
        Closed += (_, _) => DisposeMusic();
    }

    // Closes a panel whose monitor no longer shows one.
    internal void Retire() { _retiring = true; SetExpanded(false, true); Close(); }
    private void ScheduleClock() => _clock.Interval = ClockDelay(DateTime.Now);
    // The rings take the off-peak color, so the collapsed island is drawn again when an application enters or leaves an off-peak period.
    private string _offPeakState = "";
    private bool OffPeakChanged()
    {
        var now = DateTime.Now;
        string state = string.Concat(Settings.EnabledApps.Select(app => app.IsOffPeak(now) ? '1' : '0'));
        if (state == _offPeakState) return false;
        _offPeakState = state;
        return true;
    }
    internal static TimeSpan ClockDelay(DateTime now) => TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 50);
    private static TimeSpan SecondDelay(DateTime now) => TimeSpan.FromMilliseconds(1000 - now.Millisecond + 20);
    private void ApplySecondClock()
    {
        bool run = Settings.ClockSeconds && Settings.ShowsClock(EffectiveStyle);
        if (run && !_seconds.IsEnabled) { _seconds.Interval = SecondDelay(DateTime.Now); _seconds.Start(); }
        else if (!run && _seconds.IsEnabled) _seconds.Stop();
    }
    // Hidden or held open by a preview, the surface cannot be hovered, so the checks stop instead of returning early 25 times a
    // second. A collapsed preview keeps them running: hovering the island ends the preview (see TrackPointer).
    private void UpdatePointerTracking()
    {
        bool run = _trackPointer && !IsSuppressed && !(_preview && _previewExpanded);
        if (run && !_pointerTrack.IsEnabled) { _pointerTrack.Interval = TimeSpan.FromMilliseconds(TrackNear); _pointerTrack.Start(); }
        else if (!run && _pointerTrack.IsEnabled) _pointerTrack.Stop();
    }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockLabel.Text = now.ToString("HH:mm");
        if (_compactClock is not null) _compactClock.Time = now;
    }
    public void ApplySettings() => ApplySettings(false);
    // animatePage also animates the collapsed shape, which is how a screen-context change (maximized window, full screen)
    // moves between the notch, the capsule and the indicator.
    private void ApplySettings(bool animatePage)
    {
        // Powering off (exit) holds the panel as it is.
        if (_poweringOff) return;
        // An exception added or removed in settings changes the context before the next poll; read it again so that the
        // panel hides or returns in this pass.
        _context = _host.DetectNow();
        // A new edge waits for the island to be cleared before the window moves (MainWindow.Edge.cs).
        if (ClearBeforeMoving(animatePage)) return;
        bool wasVisible = IsVisible;
        ApplyLanguage();
        ApplyTheme();
        if (!Settings.QuotaAlerts && _alert is not null) ClearAlert();
        ApplySyncSchedule();
        ApplyMusicSettings();
        ApplySecondClock();
        ApplyScale();
        var (bounds, work, scale) = WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();
        UpdateCollapsedGeometry(bounds, work, scale);
        // A new edge lays the panel out there at once (MainWindow.Edge.cs); the row is built again for it.
        bool newEdge = ApplyFrame();
        if (newEdge) Collapsed = ShapeFor(EffectiveStyle);
        // Page widths are unscaled; drawn at the panel's scale they fit the work area less 12 DIP each side.
        double Fit(double width) => Math.Max(240, Math.Min(width, (work.Width / scale - 24) / _scale));
        _pageWidth = Fit(DeckPages.Size(Settings, CurrentPage).Width); _usageWidth = Fit(DeckPages.Size(Settings, DeckPage.Usage).Width);
        // The widest shown page as it fits this monitor, measured as _pageWidth is (SetExpanded centres on it). A drag resizes
        // only the page that shows, which SetExpanded takes from _pageWidth; this is set again when the size is saved (ApplySettings).
        _widestPage = Fit(DeckPages.Largest(Settings).Width);
        // The layered HWND keeps the largest size anything it shows can take, so that it is not resized or moved while the panel
        // is used: Windows shows a layered window's last picture at its new size and place before WPF draws it again, so every
        // resize flashed the panel cut off or shifted for a frame (it did on each press and release of a resize drag). That is
        // every page at the largest size a drag can give it, and the collapsed surface at the widest any form can be on this
        // monitor, with room on either side for the resize grips just outside them (ResizeGrips). The notch and the capsule may be
        // as wide as the monitor, so the window is as wide as the monitor too. Only Island animates.
        var largest = DeckPages.Largest(Settings);
        largest = (Math.Max(largest.Width, PageMaxWidth), Math.Max(largest.Height, DeckPages.MaxHeight));
        double widestSurface = Enum.GetValues<CompactStyle>().Max(style => CollapsedGeometry.WidthRange(style, MonitorBoundsDip, WorkDip, _scale).Max);
        // In the edge's frame: on a side edge a page's height runs along the edge and its width away from it.
        // The window starts at the monitor top (the frame's edge). A capsule's panel can start lower (at the top of a top taskbar's work area, or
        // just above a lowered capsule; see CollapsedGeometry.Panel), so the window reaches the panel's bottom; it always holds the collapsed surface and its shadow, and never passes the
        // bottom of the monitor. Its transparent parts let clicks through.
        var hostPanel = PanelFor(largest.Height);
        double panelTop = hostPanel.Top;
        double hostAlong = _frame.Across ? largest.Height * _scale + hostPanel.HeaderExtra : Fit(largest.Width) * _scale;
        double hostDepth = _frame.Across ? Fit(largest.Width) * _scale : largest.Height * _scale + (panelTop > 0 ? 0 : hostPanel.HeaderExtra);
        FrameWidth = Math.Min(MonitorBoundsDip.Width, Math.Max(Math.Max(hostAlong, widestSurface), Collapsed.Surface.Width) + 2 * ResizeGrips.Room);
        double panelSpace = panelTop > 0 ? panelTop + hostPanel.HeaderExtra + hostDepth + 12 : Math.Min(hostDepth + 16, WorkDip.Bottom - 16);
        FrameHeight = Math.Max(1, Math.Min(MonitorBoundsDip.Height, Math.Max(Math.Max(140, panelSpace), Collapsed.Surface.Bottom + 24)));
        Place(bounds, scale);
        UpdateClock();
        RenderCompact();
        // Hiding and showing again (the tray, a full-screen rule) are immediate. The boot plays only when the panel first
        // appears (Loaded); the power-off only when TwelveIsland exits (App.ExitApplication).
        // The window's own opacity is no longer animated; a build that faded it may have left it lowered.
        if (Opacity < 1) { BeginAnimation(OpacityProperty, null); Opacity = 1; }
        if (IsSuppressed)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false;
            // A boot cut short here never reaches its own reset: the dot's lift and squash would hold their last frame and
            // squash the whole surface once it shows again (without a boot).
            ++_boot; _bootWaiting = false; _bootEnds = 0; _bootDot = false;
            ResetDotPose();
            SetExpanded(false, true); HideBlank();
            _edgeCleared = false;
        }
        else
        {
            bool appearing = !IsVisible || _hiding;
            _hiding = false;
            if (!IsVisible) Show();
            if (appearing) SetImmediate(HoverZone, OpacityProperty, 1d);
            SetExpanded(_expanded, !animatePage || !wasVisible || newEdge);
            bool cleared = _edgeCleared;
            _edgeCleared = false;
            if ((newEdge || cleared) && wasVisible && !appearing) FadeInOnNewEdge(cleared);
        }
        UpdatePointerTracking();
        RenderUsage();
        CheckMoveOvertaken();
        ReportMoveShape();
    }
    // Windows keeps a layered window's last picture while it is hidden and puts it on screen the moment the window is shown,
    // before WPF draws again. Hidden straight away, the panel came back showing the frame it was hidden on: after the expand
    // hotkey opened it while the panels were hidden, the next press flashed the whole open panel before it opened again from
    // the collapsed surface. The surface is cleared first and the window hidden once that empty frame has been drawn.
    private bool _hiding;
    private void HideBlank()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        SetImmediate(HoverZone, OpacityProperty, 0d);
        int frames = 0;
        void Rendered(object? sender, EventArgs e)
        {
            // Shown again meanwhile (ApplySettings clears _hiding), or closed.
            if (!_hiding || PresentationSource.FromVisual(this) is null) { CompositionTarget.Rendering -= Rendered; return; }
            // Rendering comes before each frame: by the third, the empty frame has been drawn.
            if (++frames < 3) return;
            CompositionTarget.Rendering -= Rendered;
            _hiding = false;
            Hide();
        }
        CompositionTarget.Rendering += Rendered;
    }
    // Sizes are DIPs as they will be on the target monitor, and WPF turns Left and Top into pixels with the DPI the window
    // has now. A window on a monitor with another scale is therefore first moved onto the target in one step, which gives
    // it the target's DPI; setting Left and then Top could pass it over a third monitor on the way.
    // The window is centred on the collapsed surface and kept on the monitor. It is at least as wide as the surface and the
    // pages (as wide as the monitor, see ApplySettings), so the island and the open panel, wherever it is centred, always lie
    // inside the window. Only the surface and the nearly
    // transparent hover strip are drawn; the rest of the layered window stays fully transparent and lets clicks through.
    // _windowOffset and the centring are along the edge, in the edge's frame; the frame gives the window's place on the screen.
    private void Place(Rect bounds, double scale)
    {
        double centre = Collapsed.Surface.X + Collapsed.Surface.Width / 2;
        _windowOffset = Math.Clamp(centre - FrameWidth / 2, 0, Math.Max(0, MonitorBoundsDip.Width - FrameWidth));
        var place = _frame.ToScreen(new DipRect(_windowOffset, 0, FrameWidth, FrameHeight));
        if (PresentationSource.FromVisual(this) is not null && VisualTreeHelper.GetDpi(this).DpiScaleX is var old && old != scale)
            WindowsHost.MoveTo(this, (int)(bounds.Left + place.X * scale), (int)(bounds.Top + place.Y * scale));
        double current = PresentationSource.FromVisual(this) is null ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = bounds.Left / current + place.X;
        Top = bounds.Top / current + place.Y;
    }
    // Reads this window's monitor and resolves the collapsed shape for it. Runs with every ApplySettings, which DPI, display
    // and work-area changes call (see the window hook in the constructor and App.DisplaysChanged).
    private void UpdateCollapsedGeometry()
    {
        var (bounds, work, scale) = WindowsHost.MonitorScreen(MonitorHandle) ?? WindowsHost.PrimaryScreen();
        UpdateCollapsedGeometry(bounds, work, scale);
    }
    private void UpdateCollapsedGeometry(Rect bounds, Rect work, double scale)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        _pixelsPerDip = scale;
        // Both in the frame of the edge the panel hangs from (MainWindow.Edge.cs); for the top edge, the monitor's own.
        var screenWork = new DipRect((work.Left - bounds.Left) / scale, (work.Top - bounds.Top) / scale, work.Width / scale, work.Height / scale);
        _screenMonitor = new DipRect(0, 0, bounds.Width / scale, bounds.Height / scale); _screenWork = screenWork;
        // The style shown decides the edge: a window rule that turns the notch or the capsule into the line shows it where the line's
        // own settings put it.
        var (edge, overTaskbar) = Dock(EffectiveStyle);
        _frame = EdgeFrame.For(edge, bounds.Width / scale, bounds.Height / scale, screenWork, overTaskbar);
        _framedDock = (edge, overTaskbar);
        var frame = _frame.Monitor;
        MonitorBoundsDip = new DipRect(bounds.Left / scale, bounds.Top / scale, frame.Width, frame.Height);
        WorkDip = _frame.FromScreen(screenWork);
        Collapsed = ShapeFor(EffectiveStyle);
    }
    private CollapsedShape ShapeFor(CompactStyle style)
    {
        double? wanted = Settings.CompactAutoWidth(style) && !double.IsNaN(_compactWanted) ? _compactWanted : null;
        // The capsule keeps its place on the screen; docked with the bottom (MainWindow.Edge.cs) it is only seen from there.
        if (style == CompactStyle.Capsule && _frame.Edge != ScreenEdge.Top)
        {
            var onScreen = CollapsedGeometry.Resolve(Settings, style, _screenMonitor, _screenWork, _pixelsPerDip, wanted, _compactRequired, _scale);
            return onScreen with { Surface = _frame.FromScreen(onScreen.Surface) };
        }
        return CollapsedGeometry.Resolve(Settings, style, MonitorBoundsDip, WorkDip, _pixelsPerDip, wanted, _compactRequired, _scale,
            ColumnFor(style) ? double.IsNaN(_compactColumn) ? 0 : _compactColumn : null, style == CompactStyle.Line && Dock(style).OverTaskbar);
    }
    // The width the collapsed column asks for (CompactColumn.Width); NaN until it is first built.
    private double _compactColumn = double.NaN;
    // The width the collapsed content asks for (see CollapsedGeometry); NaN until the row is first built, which keeps the saved width.
    private double _compactWanted = double.NaN;
    // The width of what the collapsed content must show (CompactMusicLayout.RequiredWidth), which the surface always keeps.
    private double _compactRequired;
    // Where the pointer was when hovering opened the panel, in DIPs from the monitor's left edge; null when the panel was opened
    // another way (the hotkey) or is closed.
    private double? _openCentre;
    // The content changed what it asks for: the collapsed surface moves to the new width with its usual animation, and the window
    // grows first when the surface would be wider than it.
    private void FitCompactToContent()
    {
        var before = Collapsed;
        UpdateCollapsedGeometry();
        if (Equals(Collapsed.Surface, before.Surface)) return;
        ReportMoveShape();
        _app.CollapsedChanged();
        if (_expanded) return;
        // A surface that no longer lies within the window (wider, or moved along a screen edge) places the window again.
        // Arriving on a new edge, still cleared, the surface takes its size at once (MainWindow.Edge.cs).
        if (Collapsed.Surface.X < _windowOffset || Collapsed.Surface.X + Collapsed.Surface.Width > _windowOffset + FrameWidth || Collapsed.Surface.Bottom + 24 > FrameHeight) ApplySettings(); else SetExpanded(false, _edgeArriving);
    }
    // The limits the settings window offers for a collapsed style on this window's monitor, whatever values are saved.
    // OffsetMax is the largest offset either way (OffsetMin = -OffsetMax) for a surface of the given width. TopMin and TopMax
    // are distances from the top of the work area for a capsule of the given height or a line of the given thickness; 0 and 0
    // for the notch, which always touches the screen edge. WidthMin and WidthMax are the style's width (line length) range,
    // narrowed by what the monitor or its work area can hold; WidthMin is never below what the content must show.
    // Widths and heights are as saved, before the scale (PanelScales); the offsets and tops are places on the screen, for the
    // surface as drawn at the scale.
    public (double OffsetMin, double OffsetMax, double TopMin, double TopMax, double WidthMin, double WidthMax) CurrentCollapsedRanges(CompactStyle style, double width, double height)
    {
        // The capsule's settings are places on the screen, whichever edge it is docked with.
        var (monitor, work) = style == CompactStyle.Capsule ? (_screenMonitor, _screenWork) : (MonitorBoundsDip, WorkDip);
        var (widthMin, widthMax) = CollapsedGeometry.WidthRange(style, monitor, work, _scale);
        // At the height the surface takes, which a short screen or work area can lower below the saved one (as Resolve does).
        double surfaceHeight = CollapsedGeometry.SurfaceHeight(style, height, monitor, work, _scale);
        widthMin = Math.Min(widthMax, Math.Max(widthMin, CollapsedGeometry.SurfaceWidth(style, surfaceHeight, _compactRequired)));
        var (_, offsetMax) = CollapsedGeometry.OffsetXRange(style, width * _scale, monitor, work);
        var (topMin, topMax) = style switch
        {
            CompactStyle.Capsule => CollapsedGeometry.CapsuleTopRange(surfaceHeight, monitor, work),
            CompactStyle.Line => CollapsedGeometry.LineTopRange(CollapsedGeometry.SnapToDevicePixels(CollapsedGeometry.RoundLineThickness(height) * _scale, _pixelsPerDip), work),
            _ => (0d, 0d)
        };
        return (-offsetMax, offsetMax, topMin, topMax, widthMin / _scale, widthMax / _scale);
    }
    // Where an open panel for a page of the given height starts, in DIPs from the top of the monitor, and the space a floating
    // panel adds above its header; see CollapsedGeometry.Panel.
    // The height a page that wants this height gets in the window. A panel hanging from the screen edge keeps 12 DIPs of the
    // window below it. One that starts lower has no shadow to keep room for, so it may reach the bottom of the window, which
    // can be the bottom of the monitor.
    // Heights are unscaled; the window's room is divided by the panel's scale.
    // On a side edge the page's height runs along the edge, within the window's length there.
    private double PageHeight(double wanted, PanelPlacement panel)
    {
        if (_frame.Across) return Math.Max(0, Math.Min(wanted, (FrameWidth - 2 * ResizeGrips.Room - panel.HeaderExtra) / _scale));
        double height = Math.Max(0, Math.Min(wanted, (FrameHeight - (panel.Top > 0 ? 0 : 12) - panel.Top - panel.HeaderExtra) / _scale));
        return panel.Top > 0 ? Math.Max(0, Math.Min(height, (PanelBottom - panel.Top - panel.HeaderExtra) / _scale)) : height;
    }
    // The usage page's height in the current window, while the music page is shown too.
    private double UsagePageHeight => PageHeight(Settings.Height, PanelFor(Settings.Height));
    // pageHeight is unscaled; the panel's place depends on its height as drawn.
    // On the bottom, left and right edges the panel's top is a free side, rounded as a floating capsule panel's is, so it keeps the
    // same space above its header (CollapsedLimits.FloatingHeaderExtra) rather than the 4 DIPs under the screen edge.
    // A capsule docked with the bottom hangs its panel from there, as a notch on that edge does.
    private PanelPlacement PanelFor(double pageHeight)
    {
        var panel = Collapsed.Style == CompactStyle.Capsule && _frame.Edge != ScreenEdge.Top ? new PanelPlacement(0, 0) : CollapsedGeometry.Panel(Collapsed, WorkDip, pageHeight * _scale, PanelBottom);
        return _frame.Edge == ScreenEdge.Top ? panel : panel with { HeaderExtra = panel.HeaderExtra + CollapsedLimits.FloatingHeaderExtra };
    }
    // The lowest bottom edge of a panel below a lowered capsule: the work area's bottom, even when that is the monitor's bottom
    // (no taskbar there), so a capsule pulled all the way down is still covered by its panel; a capsule moved over a bottom
    // taskbar stays below the panel. The window may then end at the monitor's bottom with no room below the panel; SetExpanded
    // does not reserve any for a panel that starts below the top.
    private double PanelBottom => WorkDip.Bottom;
    // While open, the pointer is inside over the panel or over the collapsed surface's hover area, which can be wider than the
    // panel (a wide notch or capsule) or reach the screen edge above it.
    // The open monitor page's grips reach a little outside it (ResizeGrips.PanelArea); they count as the panel.
    private bool InsideOpenPanel(Point screenPoint) => ScreenBounds(Island).Contains(screenPoint) || CompactHoverScreenRect().Contains(screenPoint)
        || PanelResizable && PresentationSource.FromVisual(Stage) is not null && Contains(ResizeGrips.PanelArea(IslandBox()), Stage.PointFromScreen(screenPoint));
    private static bool Contains(DipRect r, Point p) => p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom;
    // Implemented by the compact layout in its own file, if present. Both run on every shape change and every alert change,
    // so they must be safe to call repeatedly with the same values.
    partial void ApplyCompactGeometry(CollapsedShape shape);
    partial void LayoutCompactAlert(CollapsedShape shape, bool alertVisible);
    private void ApplyCollapsedLayout()
    {
        // While the content fades out into the indicator it keeps the size it had, instead of being squeezed by the new shape.
        if (Collapsed.Style == CompactStyle.Line && Compact.Visibility == Visibility.Visible && Compact.Opacity > 0 && !_expanded && IsVisible) return;
        // Compact content keeps its final width while the surface moves, so nothing drifts with the shrinking edges.
        Compact.Width = Collapsed.ContentWidth;
        Compact.Height = Math.Max(0, 26 * Collapsed.Scale);
        ApplyCompactGeometry(Collapsed);
        LayoutCompactAlert(Collapsed, _alert is not null || _notice is not null);
    }
    // Brings the surface, its layers, the shadow and every cached piece of content up to the current panel theme. XAML styles
    // follow the theme by themselves (DynamicResource); what code built with the old colors is built again by the renders that
    // ApplySettings runs next. Pop-ups are closed rather than repainted.
    private void ApplyTheme()
    {
        double dpi = PresentationSource.FromVisual(this) is null ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_themeGeneration == PanelTheme.Generation && _themeDpi == dpi) return;
        // A new scale alone only redraws the scan lines or texture, which are laid out in device pixels.
        bool sameTheme = _themeGeneration == PanelTheme.Generation;
        _themeGeneration = PanelTheme.Generation; _themeDpi = dpi;
        // The resize hints take the theme's colors when they are drawn.
        _grips?.InvalidateVisual();
        if (sameTheme) { Island.Pattern = PanelTheme.Texture(dpiScale: dpi) ?? PanelTheme.Scanlines(dpiScale: dpi); FitShadowCache(dpi); return; }
        Foreground = UI.Brush(UI.Primary);
        PanelTheme.Dress(Island, dpiScale: dpi);
        if (IslandShadow.Effect is System.Windows.Media.Effects.DropShadowEffect shadow) PanelTheme.Dress(shadow);
        // Pixel art draws the island's text unsmoothed, as on a pixel screen, and casts a stepped shadow.
        PanelTheme.Text(Island);
        if (IslandShadow.Children.OfType<IslandBorder>().FirstOrDefault() is { } caster) caster.Pixel = PanelTheme.Current.Effects.Pixel;
        FitShadowCache(dpi);
        MusicIndicator.Background = UI.Brush(PanelTheme.Hex("indicatorProgress"));
        SetImmediate(_surface, SolidColorBrush.ColorProperty, PanelTheme.ColorOf(!_expanded && Collapsed.Style == CompactStyle.Line ? "indicator" : "surface"));
        _musicAccent = MusicVisuals.Accent(_mediaCover, out _coverAccent);
        _musicRenderKey = ""; _compactRendered = null; _alertRendered = null;
        if (_detailPopup is { } popup) Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_detailPopup, popup)) popup.Close(); });
        if (MediaSourceButton.ContextMenu?.IsOpen == true) Dispatcher.BeginInvoke(CloseSourceMenu);
    }
    // The toolbar's fixed buttons are declared in XAML; their names follow the interface language.
    private void ApplyLanguage()
    {
        System.Windows.Automation.AutomationProperties.SetName(AIUsageButton, Loc.T("AI 用量", "AI usage"));
        System.Windows.Automation.AutomationProperties.SetName(MonitorPageButton, Loc.T("系统监控", "System monitor"));
        System.Windows.Automation.AutomationProperties.SetName(MusicPageButton, Loc.T("音乐", "Music"));
        System.Windows.Automation.AutomationProperties.SetName(NotifyPageButton, Loc.T("通知", "Notifications"));
        System.Windows.Automation.AutomationProperties.SetName(ClipboardPageButton, Loc.T("剪贴板", "Clipboard"));
        System.Windows.Automation.AutomationProperties.SetName(ConvertPageButton, Loc.T("格式转换", "Convert"));
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, Loc.T("设置", "Settings"));
    }
    public void Preview(bool enabled, bool expanded = true)
    { _preview = enabled; _previewExpanded = enabled && expanded; if (enabled) _pageSwitchHold.Stop(); ApplySettings(); SetExpanded(enabled && expanded, true); }
    internal void ResumeAfterPreview()
    {
        if (!_preview) return;
        _preview = false; _previewExpanded = false; _pageSwitchHold.Stop();
        ApplySettings();
        PointerChanged(_pointerInside);
    }
    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        Snapshots = snapshots; _updatedAt = DateTimeOffset.Now; FirstReadEnded();
        DetectThresholds();
        RenderCompact(); RenderUsage();
    }

    // The compact island shows one ring per enabled application: the highest used share among its quotas.
    internal void RenderCompact()
    {
        var fade = RowFadeStart();
        CompactRings.Children.Clear(); CompactAlert.Children.Clear();
        CompactRings.HorizontalAlignment = HorizontalAlignment.Center;
        if (_quotaCarousel is not null) { _quotaElapsed = _quotaCarousel.Elapsed; _quotaCarousel.SetRunning(false, false); }
        if (_stripCarousel is not null) { _stripElapsed = _stripCarousel.Elapsed; _stripCarousel.SetRunning(false, false); }
        if (_columnCarousel is not null) { _columnElapsed = _columnCarousel.Elapsed; _columnCarousel.SetRunning(false, false); }
        if (_columnReadings.Count > 0) _stripElapsed = _columnReadings[0].Elapsed;
        foreach (var reading in _columnReadings) reading.SetRunning(false, false);
        _compactEqualizer = null; _compactMarquee = null; _quotaCarousel = null; _stripCarousel = null; _compactClock = null; _compactStrip = null;
        _columnCarousel = null; _columnReadings.Clear(); _columnStrips.Clear(); _rowStrips.Clear();
        if (_alert is not null || _notice is not null)
        {
            CompactAlert.Visibility = Visibility.Visible; CompactRings.Visibility = Visibility.Collapsed;
            // LayoutCompactAlert (MainWindow.CompactLayout.cs) builds the row from the warning or the notice, sized for the shape.
            ApplyCollapsedLayout();
            // The notice hides the system readings, which then need none.
            UpdateMonitorDemand();
            return;
        }
        CompactAlert.Visibility = Visibility.Collapsed; CompactRings.Visibility = Visibility.Visible;
        // A notch on a side edge stands its content in a column (MainWindow.CompactColumn.cs).
        if (CompactColumnMode) RenderCompactColumn(); else RenderCompactMusic();
        ApplyCollapsedLayout();
        RowFadeRun(fade);
    }

    private void DetectThresholds()
    {
        CompactAlertInfo? risen = null;
        foreach (var entry in Settings.EnabledApps)
        {
            var snapshot = DashboardUsage.Quota(entry, Snapshots);
            if (snapshot.IsStale || !snapshot.LiveQuota) continue;
            foreach (var quota in snapshot.Metrics)
            {
                if (quota.UsedPercent is not { } used) continue;
                var key = $"{entry.InstanceId}:{entry.ConfigurationKey}:{quota.Id}";
                int level = AppPresets.Level(used);
                bool rose = _levelsSeeded && _levels.TryGetValue(key, out var previous) && level > previous;
                _levels[key] = level;
                if (rose && level > 0 && (risen is null || used > risen.Used))
                    risen = new CompactAlertInfo(entry, entry.Name, MetricLabel(quota) + " ", Math.Clamp(used, 0, 100), quota.ResetAt);
            }
        }
        _levelsSeeded = true;
        // Levels are still tracked while notices are off, so switching them back on does not replay an earlier rise.
        if (risen is not null && Settings.QuotaAlerts && !IsSuppressed) ShowAlert(risen);
    }

    internal void ShowAlert(AppEntry entry, string label, double used, DateTimeOffset? resetAt)
        => ShowAlert(new CompactAlertInfo(entry, entry.Name, label + " ", used, resetAt));
    internal void ClearAlert() { _alertTimer.Stop(); _alert = null; RenderCompact(); }
    // No alert while the panel is suppressed (full-screen rule or hidden by the user): it would not be seen, and it must not
    // appear later when the panel shows again.
    private void ShowAlert(CompactAlertInfo alert)
    {
        if (IsSuppressed) return;
        _alert = alert; _alertTimer.Stop(); _alertTimer.Start();
        RenderCompact();
        if (!_expanded && !IsSuppressed) SetExpanded(false);
    }

    // While WeChat's, QQ's or the mailbox's notices wait unread, the collapsed bar takes the capsule's shape, whatever the
    // setting or a window rule chose, so the waiting programs' icons and counts have a pill to stand in (SyncNotice).
    private CompactStyle? _noticeStyle;
    private CompactStyle EffectiveStyle => !_preview && _noticeStyle is null
        && Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Line ? CompactStyle.Line : _noticeStyle ?? Settings.Style;
    // One animated track: where it starts and ends, as fractions of the configured duration.
    private readonly record struct Track(double Start, double End);
    // The collapsed island at rest clips its content to a rectangle (IslandBorder.SettledClip): the content keeps clear of its
    // corners and shoulders, and a rounded clip made every frame of the title and the bars cost a quarter more. The rounded clip
    // comes back before anything changes the surface; the rectangle only once the closing has had its whole length.
    private DispatcherTimer? _clipSettle;
    private void SettleClip(int after)
    {
        if (_clipSettle is null)
        {
            _clipSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _clipSettle.Tick += (_, _) => { _clipSettle.Stop(); if (!_expanded && !_poweringOff && !_booting && !Resizing && !_bootWaiting) Island.SettledClip = true; };
        }
        _clipSettle.Stop();
        _clipSettle.Interval = TimeSpan.FromMilliseconds(Math.Max(1, after) + 100);
        _clipSettle.Start();
    }

    // The panel's last opening or closing, so that one turned back part way can tell (motion-review F1, F2).
    private readonly MoveTurn _turn = new();
    // keyboard: a hotkey or Escape asked for it (motion-review C4), so the move is quicker and has no give.
    public void SetExpanded(bool expanded, bool instant = false, bool keyboard = false)
    {
        if (expanded && IsSuppressed || _poweringOff) return;
        if (expanded && !_expanded) EndBoot();
        // Only the panel under the pointer is open; the others close at once and give up the Escape key.
        if (expanded && !_preview)
            foreach (var other in _app.Decks.Where(deck => deck != this && deck._expanded && !deck._preview).ToList()) other.SetExpanded(false);
        if (expanded && !_expanded && !_preview) ApplyDefaultMusicPage();
        // A hand-over cut short by closing the panel leaves nothing behind for the next opening.
        if (expanded && !_expanded) _handover.Drop();
        bool wasExpanded = _expanded;
        _expanded = expanded;
        // Opening a page does not read its notices: the capsule waits for a click on it.
        _escapeDismissal?.SetEnabled(expanded && !_preview || _moveArmed);
        if (!expanded) { CloseSourceMenu(); _popupReturnArea = null; _leave.Stop(); _pageSwitchHold.Stop(); _detailPopup?.Close(); _pinned = false; }
        if (wasExpanded && !expanded) { HotkeyPanelCollapsed(); ReplayCompactTitle(); _app.RequestTrim(); }
        Collapsed = ShapeFor(EffectiveStyle);
        var shape = Collapsed;
        var style = shape.Style;
        bool line = style == CompactStyle.Line, indicator = !expanded && line;
        double wantedHeight = DeckPages.Size(Settings, CurrentPage).Height;
        var panel = PanelFor(wantedHeight);
        double panelTop = panel.Top, headerExtra = panel.HeaderExtra;
        double pageHeight = PageHeight(wantedHeight, panel);
        // The page as drawn, at the panel's scale; the content is laid out at its unscaled size (ContentCanvas below).
        // Width and height below are the island's in the edge's frame: along the edge and away from it. On a side edge the page's
        // height runs along the edge (MainWindow.Edge.cs).
        bool across = _frame.Across;
        // A floating panel, and one on another edge than the top, is taller than its page by headerExtra, the space above its header;
        // on a side edge that runs along the edge.
        double pageWidth = across ? pageHeight * _scale + headerExtra : _pageWidth * _scale, widestPage = across ? pageWidth : _widestPage * _scale;
        double width = expanded ? pageWidth : shape.Surface.Width;
        double height = expanded ? (across ? _pageWidth * _scale : pageHeight * _scale + headerExtra) : shape.Surface.Height;
        // Every compact style opens into the same panel, hanging from the screen edge or, below a lowered capsule, rounded all round.
        double panelRadius = PanelRadius;
        var radius = expanded ? panelTop > 0 ? new CornerRadius(panelRadius) : new CornerRadius(0, 0, panelRadius, panelRadius)
            : new CornerRadius(shape.TopRadius, shape.TopRadius, shape.BottomRadius, shape.BottomRadius);
        double gap = expanded ? panelTop : shape.Surface.Y;
        // The island's left edge in window coordinates: the surface, or the open panel centred on it and kept on the monitor. A
        // panel opened by hovering a surface wider than every shown page centres, with ExpandAtPointer on, on where the pointer
        // was instead, as far as the surface's ends allow, and keeps that centre while open (page switches, resizing) without
        // following the pointer. The choice is the same for every page: decided per page, a capsule that the music widens past the narrow pages but not the
        // wide ones put some pages at the pointer and others at the middle, so switching pages moved the panel sideways.
        if (!expanded) _openCentre = null;
        double panelCentre = shape.Surface.X + shape.Surface.Width / 2;
        if (_openCentre is { } pointer && double.IsFinite(pointer) && shape.Surface.Width > Math.Max(pageWidth, widestPage))
            panelCentre = Math.Clamp(pointer, shape.Surface.X + pageWidth / 2, shape.Surface.Right - pageWidth / 2);
        double panelLeft = Math.Clamp(panelCentre - pageWidth / 2, 0, Math.Max(0, MonitorBoundsDip.Width - pageWidth));
        double left = (expanded ? panelLeft : shape.Surface.X) - _windowOffset;
        ApplyCollapsedLayout();
        // The boot circle is still jumping: do not grow the island to the collapsed size under its lift.
        if (!expanded && KeepBootDot(shape)) return;
        Island.BorderThickness = new Thickness(0);
        // On a side edge the page keeps its width as well (MainWindow.Edge.cs).
        if (across) ExpandedContent.Width = _pageWidth;
        ExpandedContent.Height = pageHeight;
        // The page and its header start headerExtra below the panel's top edge. The header row keeps its own height, so its
        // controls keep their size tier. Collapsing keeps the offset the content had, so it fades out without moving.
        if (expanded) ExpandedContent.Margin = new Thickness(0, headerExtra, 0, 0);
        // Lay out in device-independent pixels. Column typography adapts to its available space.
        ContentCanvas.Width = _pageWidth;
        ContentCanvas.Height = pageHeight;
        // motion-review C1, C4: closing takes three quarters of the set time; a move the keyboard asked for takes 0.6 of it. The
        // open panel changing size in place (a page switch) keeps its own length whatever the setting (PanelMotion.Reshape).
        bool turning = wasExpanded != expanded;
        int duration = !instant && Settings.Animations && SystemParameters.ClientAreaAnimation
            ? expanded && !turning ? PanelMotion.Reshape : PanelMotion.Length(Settings.AnimationDuration, closing: turning && !expanded, keyboard: keyboard && turning) : 0;
        // motion-review F1, F2: turned back while the last opening or closing is still under way (the pointer left an opening
        // panel, or came back to a closing one), the surface starts back from where it is at once, in the share of the move it
        // has to undo. The panel laid out again in place never counts.
        long now = Environment.TickCount64;
        bool redirect = turning && duration > 0 && _turn.Turns(now, expanded);
        if (redirect) duration = (int)Math.Round(duration * _turn.Share(now, expanded, Island.Width, Island.Height, width, height));
        // The collapsed surface laid out again while it is still closing (its content asked for a new width) carries the closing
        // on, on the same drawer curve, rather than holding for the closing's delay and starting a whole new move.
        int carried = !turning && !expanded ? _turn.Carried(now, false, duration) : 0;
        if (carried > 0) duration = carried;
        ExpandedContent.IsHitTestVisible = expanded;
        if (expanded) ExpandedContent.Visibility = Visibility.Visible;
        // The indicator has no content: it fades out with the other tracks and is hidden once gone.
        if (!line) Compact.Visibility = Visibility.Visible;
        else if (duration == 0) Compact.Visibility = Visibility.Hidden;
        // Collapsed, the hover strip covers the box MainWindow.Hover.cs gives (Stage coordinates; HoverZone starts at the island's
        // left edge). Open, it stretches across the island and shrinks to nothing, as the NaN box asks.
        var box = HoverExtensionBox(expanded);
        bool stretch = double.IsNaN(box.Left) || double.IsNaN(box.Width);
        HoverExtension.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        HoverExtension.Width = stretch ? double.NaN : Math.Max(0, box.Width);
        // Collapsed, hovering is detected by polling the cursor (TrackPointer), so the strip is not drawn: its nearly transparent
        // pixels would still catch clicks for this layered window (Windows only lets clicks through fully transparent pixels).
        HoverExtension.IsHitTestVisible = expanded;
        HoverExtension.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        double boxTop = double.IsFinite(box.Top) ? box.Top : 0;
        // Collapsing runs in three stages: the content leaves first, the surface follows a moment later on the drawer's curve
        // (leaving at once, landing softly), the compact content lands last.
        // Expanding must feel immediate: the surface grows from the first frame on the opening spring, the compact content vanishes
        // quickly, and the content fades in once the surface is about two thirds open.
        // Open already (a page switch, a new page size), the surface reshapes over the whole of the move, easing in and out, so
        // the change of size reads as one soft move rather than a jump (motion-review C8: no longer 1.25 times as long).
        // Booting (MainWindow.Lifecycle.cs), after the round dot's jumps the surface swells from that resting circle into the
        // collapsed shape on the opening spring, the compact content arriving once the shape has nearly settled.
        // A new collapsed style chosen in the settings (MorphShape), the surface changes from one form into the other over the
        // whole of a longer move on the monitor's curve; the compact content steps out at once and comes back in its new layout
        // once the form has nearly settled.
        bool reshaping = expanded && wasExpanded, booting = _booting && !expanded && duration > 0, morphing = _morphingShape && !expanded && duration > 0;
        _booting = false; _morphingShape = false;
        if (booting) duration = (int)(duration * BootSwell);
        if (morphing) duration = (int)(duration * 1.3);
        if (turning || carried > 0) _turn.Started(now, duration, expanded, width, height);
        Island.SettledClip = false;
        if (!expanded) SettleClip(duration);
        // The opening spring is most of the way there within a fifth of the move, so the content follows soon after it.
        Track content = expanded ? new(.12, .8) : new(0, .30);
        Track surface = reshaping || morphing || booting || expanded ? new(0, 1) : new(PanelMotion.CloseSurfaceStart(redirect || carried > 0), line ? .92 : 1);
        // Between forms the corners turn early: a notch becoming a capsule rounds its flat top long before it has its new size,
        // rather than showing a square top over round bottom corners for most of the move. The outline draws no top corners
        // while it has shoulders (IslandBorder), so the two take turns: leaving the notch, the shoulders draw in first and the
        // top rounds after them; becoming the notch, the top squares first and the shoulders flare after it. On one track the
        // top stayed square until the shoulders were gone and then rounded in a single frame.
        bool intoShoulders = morphing && shape.Shoulder > 0, outOfShoulders = morphing && !intoShoulders && Island.ShoulderRadius > 0;
        Track cornerTrack = outOfShoulders ? new(.3, .7) : morphing ? new(0, .4) : surface;
        Track shoulderTrack = intoShoulders ? new(.4, 1) : outOfShoulders ? new(0, .3) : surface;
        Track compact = booting ? new(.5, 1) : expanded ? new(0, .15) : line ? new(0, morphing ? .25 : .35) : morphing ? new(.55, 1) : new(.62, 1);
        Track shadow = booting ? new(.35, 1) : expanded ? new(0, .3) : morphing ? new(.2, .9) : new(.5, 1);
        Track rim = expanded ? new(.2, .5) : morphing ? new(.2, .7) : new(.5, .8);
        // The indicator's color only ever shows at the line's size, never as a larger block: into the indicator it comes once the
        // surface has nearly narrowed to the line, and out of it (a style morph from the line) it goes at once.
        Track tint = expanded ? new(0, .12) : indicator ? morphing ? new(.65, 1) : new(.85, 1) : morphing ? new(0, .15) : new(.68, 1);
        if (morphing && !line && Compact.Visibility == Visibility.Visible && Compact.Opacity > .5)
        {
            SetImmediate(Compact, OpacityProperty, 0d);
            SetImmediate(CompactScale, ScaleTransform.ScaleXProperty, Soft.Shrunk); SetImmediate(CompactScale, ScaleTransform.ScaleYProperty, Soft.Shrunk);
        }
        // Opening, the size and place spring past their end by a third of a percent of the move and settle back (softened
        // from 1.7 percent, so the bottom edge's rebound is felt rather than seen, in every theme alike); the corners never
        // bounce (a radius would pass zero). A panel that has no room in the window for that give opens on the plain curve.
        // Booting does not use that give: Bounce on Margin and Height would bob the whole capsule or line after the round
        // dot has already finished its jumps (the notch sits at the top edge, so the same overshoot is barely seen).
        // A move the keyboard asked for lands without the give (motion-review C4).
        bool bounce = !booting && expanded && !reshaping && !keyboard && duration > 0 && HasRoomToBounce(width, height, left, gap);
        IEasingFunction easeOut = Soft.Out, easeInOut = Soft.InOut, corners = morphing ? Soft.Follow : reshaping ? Soft.InOut : expanded || booting ? Soft.Out : Soft.Drawer;
        IEasingFunction settle = bounce ? Soft.Bounce : corners;
        Animate(ExpandedContent, OpacityProperty, expanded ? 1 : 0, duration, content, easeOut, () => { if (!_expanded) ExpandedContent.Visibility = Visibility.Hidden; });
        // The content only fades, in and out on the same track and curve (motion-review F13: what leaves eases out as what arrives
        // does). It used to scale between .96 and 1 as well; text under a scale that is not exactly 1 is drawn unhinted at fractional
        // places, and snapped onto the pixel grid on the frame the scale reached 1.
        // The island's width and left edge move on whole device pixels (SnappedAnimations.cs): rounded each on its own by the layout,
        // they stepped the page and the surface's edges a pixel to and fro in the tail of the move at display scales above 100 percent.
        double dpi = VisualTreeHelper.GetDpi(Island).DpiScaleX, fromWidth = Island.Width;
        if (duration == 0) SetImmediate(Island, WidthProperty, width);
        else Island.BeginAnimation(WidthProperty, Timed(new SnappedWidthAnimation { To = width, Page = pageWidth, Scale = dpi, EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
        Animate(Island, HeightProperty, height, duration, surface, settle);
        Animate(Island, IslandBorder.ShoulderRadiusProperty, expanded ? 0 : shape.Shoulder, duration, shoulderTrack, corners);
        // Compact notch and capsule hover over their full bounding box from the screen edge; the open panel uses its own outline.
        Animate(HoverExtension, HeightProperty, double.IsFinite(box.Height) ? Math.Max(0, box.Height) : 0, duration, surface, settle);
        if (duration == 0)
        {
            SetImmediate(HoverZone, MarginProperty, new Thickness(left, 0, 0, 0));
            SetImmediate(HoverExtension, MarginProperty, new Thickness(stretch ? 0 : box.Left - left, boxTop, 0, 0));
            SetImmediate(Island, MarginProperty, new Thickness(0, gap, 0, 0));
            SetImmediate(Island, Border.CornerRadiusProperty, radius);
        }
        else
        {
            // HoverZone moves the island; the collapsed strip moves the opposite way on the same track and curve, so it stays
            // at the box (where TrackPointer senses) for the whole animation. Open, it stretches across the island and moves with it.
            if (stretch) SetImmediate(HoverExtension, MarginProperty, new Thickness(0, boxTop, 0, 0));
            else
            {
                // Starting from the current zone position also covers the delay before the surface track begins.
                var from = new Thickness(box.Left - HoverZone.Margin.Left, boxTop, 0, 0);
                SetImmediate(HoverExtension, MarginProperty, from);
                HoverExtension.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { From = from, To = new Thickness(box.Left - left, boxTop, 0, 0), EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
            }
            HoverZone.BeginAnimation(MarginProperty, Timed(new SnappedLeftAnimation { To = left, FromWidth = fromWidth, ToWidth = width, Page = pageWidth, Scale = dpi, EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
            Island.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(0, gap, 0, 0), EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
            Island.BeginAnimation(Border.CornerRadiusProperty, Timed(new CornerRadiusAnimation { To = radius, EasingFunction = morphing ? Soft.Out : corners }, duration, cornerTrack), HandoffBehavior.SnapshotAndReplace);
        }
        bool compactGone = expanded || line;
        Animate(Compact, OpacityProperty, compactGone ? 0 : 1, duration, compact, easeOut, () => { if (_expanded || ShapeFor(EffectiveStyle).Style == CompactStyle.Line) Compact.Visibility = Visibility.Hidden; });
        Animate(CompactScale, ScaleTransform.ScaleXProperty, compactGone ? Soft.Shrunk : 1, duration, compact, easeOut);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, compactGone ? Soft.Shrunk : 1, duration, compact, easeOut);
        // The line's playback progress, hidden while the surface was the boot's dot, comes back with the compact content.
        Animate(MusicIndicator, OpacityProperty, 1, duration, compact, easeOut);
        Animate(ShadowHost, OpacityProperty, !expanded && !line ? 1 : 0, duration, shadow, easeOut);
        // The surface stays opaque throughout; it loses its rim and turns grey only once it has the indicator's shape.
        Animate(Island, IslandBorder.MaterialOpacityProperty, indicator ? 0 : 1, duration, rim, easeOut);
        var tone = PanelTheme.ColorOf(indicator ? "indicator" : "surface");
        // Opening from the indicator, the surface takes its own color while it is still the thin line: the opening spring is
        // half way open within a few frames, and a tint still under way showed for those frames as a large block in the
        // indicator's color.
        if (duration == 0 || expanded && line && !wasExpanded) SetImmediate(_surface, SolidColorBrush.ColorProperty, tone);
        else _surface.BeginAnimation(SolidColorBrush.ColorProperty, Timed(new ColorAnimation { To = tone, EasingFunction = easeInOut }, duration, tint), HandoffBehavior.SnapshotAndReplace);
        // The panel is rebuilt when data or settings change, never here: a rebuild on the first frame reads as a stall.
        if (expanded) { RefreshStatus(); UpdateMusicPosition(); }
        else RenderSyncButton();
        UpdateMusicTimer();
        UpdateMonitorDemand();
        UpdateGrips();
        // A newly opened panel waits ten seconds, then closes on its own; a page switch while already open does not reset it, and
        // a held or preview panel never starts it. Closing clears the hold and brings the capsule reminder back if notices remain.
        RefreshCapsuleAttention();
        UpdatePinControls();
        if (_expanded && !wasExpanded)
        {
            if (!_preview && !IsHotkeyPinned && !_pinned) StartAutoCollapse();
            else StopAutoCollapse();
        }
        else if (!_expanded)
        {
            StopAutoCollapse();
        }
    }
    // The panel closes itself ten seconds after opening, unless held, pinned by the hotkey, or a settings preview.
    private void StartAutoCollapse()
    {
        _autoClose ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(AutoCollapseMs) };
        _autoClose.Tick -= OnAutoCollapse;
        _autoClose.Tick += OnAutoCollapse;
        _autoClose.Stop();
        _autoClose.Start();
    }
    private void OnAutoCollapse(object? _, EventArgs _e)
    {
        if (_pinned || IsHotkeyPinned || _preview) { StopAutoCollapse(); return; }
        SetExpanded(false);
    }
    private void StopAutoCollapse() => _autoClose?.Stop();

    // The panel's own hold and collapse controls (PanelPinBar in the XAML). "悬停" toggles a hold that keeps the panel open past
    // the ten-second timer and past the pointer leaving it; "收起" collapses at once and releases the hold.
    private void PinKeep_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        if (_pinned) StopAutoCollapse();
        else if (_expanded && !_preview && !IsHotkeyPinned) StartAutoCollapse();
        UpdatePinControls();
    }
    private void PinCollapse_Click(object sender, RoutedEventArgs e)
    {
        _pinned = false;
        SetExpanded(false);
    }
    private void UpdatePinControls()
    {
        if (PanelPinBar is null) return;
        PanelPinBar.Visibility = (_expanded && !_preview) ? Visibility.Visible : Visibility.Collapsed;
        if (PinKeepButton is { } keep)
        {
            keep.Foreground = UI.Brush(_pinned ? PanelTheme.Hex("accent") : UI.Secondary);
            keep.Background = _pinned ? UI.Brush(PanelTheme.Hex("fillSelected")) : Brushes.Transparent;
        }
    }

    private static T Timed<T>(T animation, int duration, Track track) where T : AnimationTimeline
    {
        animation.BeginTime = TimeSpan.FromMilliseconds(duration * track.Start);
        animation.Duration = TimeSpan.FromMilliseconds(duration * (track.End - track.Start));
        return animation;
    }
    private static void Animate(IAnimatable target, DependencyProperty property, double value, int duration, Track track, IEasingFunction easing, Action? completed = null)
    {
        if (duration == 0) { SetImmediate(target, property, value); completed?.Invoke(); return; }
        var animation = Timed(new DoubleAnimation { To = value, EasingFunction = easing }, duration, track);
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void SetImmediate(IAnimatable target, DependencyProperty property, object value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }
    // While compact, TrackPointer alone decides; an extra enter or leave message would restart the open delay.
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    { if (_expanded) PointerChanged(true); }
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    { if (_expanded) PointerChanged(false); }
    internal void TrackPointer()
    {
        CheckMoveOvertaken();
        if (_preview && _previewExpanded || IsSuppressed || !IsVisible || PresentationSource.FromVisual(Stage) is null || Resizing || BootHoldsPointer) return;
        var cursor = WindowsHost.Cursor();
        // Move mode: the panel does not open (MainWindow.Move.cs).
        if (_moveArmed) { TrackMove(cursor); return; }
        var area = CompactHoverScreenRect();
        if (area.IsEmpty) return;
        // The surface's ends resize it (MainWindow.Resize.cs); pointing there does not open the panel.
        var grip = TrackGrips(cursor);
        bool inCompact = area.Contains(cursor) && grip == ResizeEdges.None;
        double dx = Math.Max(0, Math.Max(area.Left - cursor.X, cursor.X - area.Right)), dy = Math.Max(0, Math.Max(area.Top - cursor.Y, cursor.Y - area.Bottom));
        // Whole 20 ms steps, so small movements far away do not reschedule the timer on every check.
        double wait = _expanded ? TrackNear : Math.Clamp(Math.Floor(Math.Sqrt(dx * dx + dy * dy) / ApproachSpeed / 20) * 20, TrackNear, TrackFar);
        _pointerTrack.Interval = TimeSpan.FromMilliseconds(wait);
        if (_preview)
        {
            // A collapsed preview: reaching the island ends it. The hover state starts afresh, so the next check counts as an
            // entry and the panel opens after the usual delay.
            _cursorInCompact = false; _pointerInside = false; _hover.Stop();
            if (inCompact) _app.EndPreviewFromDeck();
            return;
        }
        if (_expanded)
        {
            // Kept current so that collapsing with the cursor still on the surface does not count as a new entry.
            _cursorInCompact = inCompact;
            // A jump straight out of the panel can leave without any mouse message reaching the window.
            // Inside means over the panel itself or the collapsed hover area (InsideOpenPanel); HoverZone's bounds would also
            // count its top margin above a lowered panel.
            bool inside = Island.IsMouseOver || InsideOpenPanel(cursor);
            // The expand hotkey's pin holds the panel open until the pointer is inside once. If that first entry is seen here
            // rather than by Island_MouseEnter (for example in the hover area beside the panel), record it, so leaving later closes
            // the panel as usual. An entry by MouseEnter already went through PointerChanged, which released the pin.
            bool wasPinned = IsHotkeyPinned;
            if (HotkeyPinned(inside)) return;
            if (wasPinned && inside && !_pointerInside) { PointerChanged(true); return; }
            if (_pointerInside && !inside) PointerChanged(false);
            else if (!_pointerInside && inside && _leave.IsEnabled) PointerChanged(true);
            return;
        }
        // A window dragged to the top of the screen for Snap Layouts passes the surface. Neither the drag nor the release
        // there counts as an entry: the panel opens only once the pointer has left the area and come back.
        _dragHold = _app.WindowDrags.Active || _dragHold && inCompact;
        if (_dragHold)
        {
            _cursorInCompact = inCompact;
            if (_pointerInside) PointerChanged(false);
            return;
        }
        if (inCompact != _cursorInCompact) { _cursorInCompact = inCompact; PointerChanged(inCompact); }
        else if (!inCompact && _pointerInside) PointerChanged(false);
    }
    internal void PointerChanged(bool inside)
    {
        // Dragging an edge may take the pointer outside; the panel stays open until the drag ends.
        if (!inside && Resizing) return;
        // A pointer on the island means the display is on, whatever Windows last reported (DisplayPower).
        if (inside) DisplayPower.InputSeen();
        _pointerInside = inside; UpdateQuotaCarousel();
        if (inside) ReleaseHotkeyPin();
        if (inside)
        {
            _popupReturnArea = null;
            _leave.Stop(); if (_expanded || IsSuppressed) return;
            // The capsule is a button, not a hover surface: a pointer resting on it opens nothing, and a click brings the
            // announcing program up (OpenNoticeApp) rather than opening the panel. Every other collapsed style keeps
            // opening on hover.
            if (_noticeStyle == CompactStyle.Capsule) return;
            int delay = Settings.OpenDelay;
            _hover.Stop(); _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)); _hover.Start();
        }
        else
        {
            _hover.Stop(); if (!_expanded || _preview || _pageSwitchHold.IsEnabled || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) return;
            // Held open by the panel's own "悬停" button: the pointer leaving does not close it; only "收起" does.
            if (_pinned) { _leave.Stop(); return; }
            // Within the ten-second stay the auto close owns the ending: the pointer leaving does not close the panel early.
            if (AutoClosing) { _leave.Stop(); return; }
            int delay = Math.Max(_popupReturnArea is null ? 1 : 50, Settings.CloseDelay);
            _leftAt = Environment.TickCount64;
            _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(delay); _leave.Start();
        }
    }
    internal void CheckPointerExit(Point screenPoint)
    {
        // The ten-second stay outranks the exit check: while it runs, only its timer (or "收起") ends the panel.
        if (IsHotkeyPinned || Resizing || _pinned || AutoClosing) { _leave.Stop(); return; }
        if (_pointerInside || _preview || _pageSwitchHold.IsEnabled || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) { _leave.Stop(); return; }
        // Closing a calendar with either button must leave a path back to the results.
        // Keep checking until the cursor leaves that area; entering the island restores normal hover behavior.
        if (_popupReturnArea is { } area && area.Contains(screenPoint)) return;
        // A mouse leave from the panel into the part of the hover area beside or above it is not an exit.
        if (_expanded && InsideOpenPanel(screenPoint)) { PointerChanged(true); return; }
        // Still just past a resize grip: the pointer is most likely coming back to it. The panel waits, but not for long.
        if (_expanded && NearGrips(screenPoint) && Environment.TickCount64 - _leftAt < GripHold) { _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(120); _leave.Start(); return; }
        _leave.Stop(); SetExpanded(false);
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Updates.NoticeVersion is not null) _app.OpenUpdateSettings();
        else if (_app.Updates.UpdatedFrom is not null) _app.OpenReleaseNotes();
        else _app.OpenSettings();
    }
    // The panel shows music, the system readings and the pages this build adds; no AI tool is read any more, so there is
    // nothing to collect and no prices to fetch. The signature stays, so the pages that ask for a read keep working unchanged.
    private Task<IReadOnlyList<ProviderSnapshot>> LoadSnapshotsAsync(DateTime? historyStart)
        => Task.FromResult<IReadOnlyList<ProviderSnapshot>>([]);
    // Only the main panel reads; every panel shows the result. The history goes back as far as any panel's model details need.
    public async Task RefreshAsync(bool queueIfBusy = true)
    {
        if (_lead is { } lead) { await lead.RefreshAsync(queueIfBusy); return; }
        // Timer requests reuse the current read. Changed settings or a wider
        // date range still require one follow-up, and automatic requests must not clear it.
        if (_refreshing) { if (queueIfBusy) _refreshRequested = true; return; }
        _refreshing = true; LastRefreshError = null;
        var decks = _app.Decks.ToList();
        foreach (var deck in decks) deck.RenderSyncButton();
        try
        {
            var result = await LoadSnapshotsAsync(decks.Min(deck => deck._historyStart));
            foreach (var deck in _app.Decks) deck.SetSnapshots(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastRefreshError = ex.GetType().Name + "\n" + ex.StackTrace; }
        finally
        {
            _refreshing = false; foreach (var deck in _app.Decks) deck.FirstReadEnded();
            RestartSyncCycle(); _app.RequestTrim();
            foreach (var deck in _app.Decks) deck.RenderUsage();
            if (_refreshRequested)
            {
                _refreshRequested = false; _ = RefreshAsync();
            }
        }
    }

}

public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public CornerRadius To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }
    public override Type TargetPropertyType => typeof(CornerRadius);
    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation { To = To, EasingFunction = EasingFunction };
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = (CornerRadius)defaultOriginValue;
        var p = animationClock.CurrentProgress ?? 0;
        p = EasingFunction?.Ease(p) ?? p;
        double L(double a, double b) => a + (b - a) * p;
        return new CornerRadius(L(from.TopLeft, To.TopLeft), L(from.TopRight, To.TopRight), L(from.BottomRight, To.BottomRight), L(from.BottomLeft, To.BottomLeft));
    }
}
