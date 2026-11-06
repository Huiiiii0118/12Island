using System.Windows;
using TwelveIsland.Core;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TwelveIsland;

public partial class MainWindow
{
    private string _syncSpin = "";
    // The countdown ring shows how far the automatic cycle has run. It is redrawn once a second, and only while the
    // usage page is on screen: an endlessly rotating icon made the layered panel recompose every frame.
    private DateTime _syncCycleStart = DateTime.UtcNow;
    private PromptTimer? _syncRingTimer;
    private bool _syncCountdown;
    internal bool AutomaticSyncEnabled => Settings.QuotaWanted && Settings.UsageAutoSync;
    // The panel that reads the data; the panels of other monitors show its sync state.
    private MainWindow Lead => _lead ?? this;
    private bool Refreshing => Lead._refreshing;

    private void ApplySyncSchedule()
    {
        if (_lead is null && IsLoaded && AutomaticSyncEnabled) { if (!_refresh.IsEnabled) { _refresh.Start(); _syncCycleStart = DateTime.UtcNow; } }
        else _refresh.Stop();
    }

    // A read that just finished starts the next full cycle, so the ring always runs from empty to a real sync.
    private void RestartSyncCycle()
    {
        if (_refresh.IsEnabled) { _refresh.Stop(); _refresh.Start(); }
        _syncCycleStart = DateTime.UtcNow;
    }

    internal Task RefreshAutomaticallyAsync(bool queueIfBusy = false)
        => AutomaticSyncEnabled ? RefreshAsync(queueIfBusy) : Task.CompletedTask;

    // From another page the switch shows the usage page out of the model details (ShowPage leaves them). The tab is hidden in the
    // details, so on the usage page it has nothing to do.
    private void AIUsage_Click(object sender, RoutedEventArgs e) { if (!IsUsagePage) SelectPage(TwelveIsland.Core.DeckPage.Usage); }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_details)
        {
            // A second click during the same read does not request another pass.
            await RefreshAsync(queueIfBusy: false);
            return;
        }
        var settings = Settings.Copy();
        settings.UsageAutoSync = !settings.UsageAutoSync;
        _app.UpdateSettings(settings);
    }

    private void RenderSyncButton()
    {
        bool paused = !_details && !Settings.UsageAutoSync;
        AIUsageButton.Visibility = Settings.UsagePage && !_details ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Visibility = Settings.UsagePage && IsUsagePage ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Foreground = UI.Brush(paused ? PanelTheme.Hex("danger") : UI.Secondary);
        SyncSlash.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        var tip = _details ? Refreshing ? Loc.T("正在同步用量与模型消耗…", "Syncing usage and model consumption…") : Loc.T("立即同步用量与模型消耗", "Sync usage and model consumption now")
            : Settings.UsageAutoSync ? Loc.T("每 60 秒自动同步一次，点击关闭同步", "Syncs automatically every 60 seconds. Click to turn syncing off.")
            : Loc.T("同步已关闭，点击开启", "Syncing is off. Click to turn it on.");
        if (SyncButton.ToolTip is System.Windows.Controls.ToolTip shown) shown.Content = tip; else SyncButton.ToolTip = UI.LeftwardToolTip(tip);
        AutomationProperties.SetName(SyncButton, _details ? tip : Settings.UsageAutoSync ? Loc.T("关闭自动同步", "Turn off automatic sync") : Loc.T("开启自动同步", "Turn on automatic sync"));

        bool onScreen = _expanded && IsUsagePage && Settings.UsagePage && !IsSuppressed;
        _syncCountdown = !_details && Settings.UsageAutoSync && Lead._refresh.IsEnabled;
        SyncTrack.Visibility = SyncProgress.Visibility = _syncCountdown ? Visibility.Visible : Visibility.Collapsed;
        // Inside the ring the arrows shrink to 13 DIP; the stroke stays 1.24 DIP like the standalone icon. The arrows keep the ring's
        // 24 DIP box and are scaled about its centre: a smaller box centred by layout starts at (24 - 13) / 2 = 5.5 DIP, and layout
        // rounding then moves it up to half a device pixel off the ring's centre (the 18 DIP box too, at some scales such as 150 %).
        double glyph = (_syncCountdown ? 13 : 18) / 24d;
        SyncGlyph.Width = SyncGlyph.Height = 24; SyncGlyph.RenderTransformOrigin = new Point(.5, .5);
        if (SyncGlyph.RenderTransform is ScaleTransform { IsFrozen: false } scale) scale.ScaleX = scale.ScaleY = glyph;
        else SyncGlyph.RenderTransform = new ScaleTransform(glyph, glyph);
        SyncArrows.StrokeThickness = _syncCountdown ? 1.65 * 18 / 13 : 1.65;
        UpdateSyncRing();
        var ringTimer = _syncRingTimer ??= new PromptTimer(Dispatcher, UpdateSyncRing) { Interval = TimeSpan.FromSeconds(1) };
        if (_syncCountdown && onScreen) { if (!ringTimer.IsEnabled) ringTimer.Start(); }
        else ringTimer.Stop();

        // A manual read on the details page spins until it ends; an automatic sync turns the arrows once.
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        string spin = !onScreen || !animations || !Refreshing ? "" : _details ? "details" : _syncCountdown ? "once" : "";
        if (_syncSpin == spin) return;
        _syncSpin = spin;
        SyncRotation.BeginAnimation(RotateTransform.AngleProperty, spin.Length == 0 ? null : new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = spin == "details" ? RepeatBehavior.Forever : new RepeatBehavior(1), FillBehavior = FillBehavior.Stop
        });
    }

    private void UpdateSyncRing()
    {
        if (!_syncCountdown) { SyncProgress.Data = null; return; }
        double fraction = Refreshing ? 1 : Math.Clamp((DateTime.UtcNow - Lead._syncCycleStart).TotalSeconds / Lead._refresh.Interval.TotalSeconds, 0, 1);
        SyncProgress.Data = RingArc(fraction);
    }

    // The arc starts at 12 o'clock and grows counterclockwise, like the compact quota rings.
    private static Geometry? RingArc(double fraction)
    {
        const double center = 12, radius = 10;
        if (fraction <= 0) return null;
        Geometry geometry;
        if (fraction >= .999) geometry = new EllipseGeometry(new Point(center, center), radius, radius);
        else
        {
            double angle = fraction * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(center - radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Counterclockwise, true));
            geometry = new PathGeometry([figure]);
        }
        geometry.Freeze();
        return geometry;
    }
}
