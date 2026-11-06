using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TwelveIsland.Core;
using MediaState = TwelveIsland.Core.MediaState;

namespace TwelveIsland;

// The collapsed content of a notch on a side edge (MainWindow.Edge.cs): the parts the row shows side by side (RenderCompactMusic)
// stand one below the other, each on a line of its own across the column, in the same order, with the row's gap between them
// (CompactColumn). Text stays upright. The quota rings stand side by side as far as the column allows; each system reading has a
// line, and one wider than the column glides sideways in it, as the title does. A column longer than the notch glides down the
// edge and back (ColumnCarousel). An auto-width notch takes the column's length; the notch is as deep as the column is wide.
public partial class MainWindow
{
    private ColumnCarousel? _columnCarousel;
    private TimeSpan _columnElapsed;
    // One strip per reading; the first is also _compactStrip, which the sampler's demand looks at (MonitorPanel). All of them take
    // each reading (UpdateCompactStrips).
    private readonly List<MonitorStrip> _columnStrips = [];
    private readonly List<ReadingsCarousel> _columnReadings = [];

    // The column across the notch: its depth less ColumnPad on either side.
    private static double ColumnWidth(CollapsedShape shape)
        => Math.Max(0, (double.IsFinite(shape.Surface.Height) ? shape.Surface.Height : 0) - 2 * CollapsedLimits.ColumnPad * CompactMusicLayout.SafeScale(shape.Scale));

    private void RenderCompactColumn()
    {
        var shape = Collapsed; _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double column = SnapDown(ColumnWidth(shape), dpi), room = SnapDown(CompactContentWidth(shape), dpi);
        bool media = Settings.ShowsMusic(EffectiveStyle) && _mediaTrack is { State: not MediaState.Stopped };
        bool cover = media && !PausedHides(PausedMusicHide.Cover), hideText = PausedHides(PausedMusicHide.Text);
        bool equalizer = CompactEqualizerShown;
        bool notice = media && !hideText && _trackNoticeUntil > DateTimeOffset.UtcNow;
        string text = "";
        if (media && !hideText)
        {
            if (notice || Settings.MusicText == CompactMusicText.Title) text = _mediaTrack!.Caption;
            else if (Settings.MusicText == CompactMusicText.Lyrics)
            {
                _compactLyric = CurrentLyrics(_mediaTrack!).Current;
                text = CompactLyricText(_mediaTrack!, _compactLyric);
            }
        }
        var apps = Settings.ShowsSummary(EffectiveStyle) && !notice ? Settings.EnabledApps : [];
        var clock = Settings.ShowsClock(EffectiveStyle) ? new ClockFace(Settings.ClockStyle, Settings.Clock24Hour, Settings.ClockSeconds, ClockFace.Palette(Settings, CoverColor), DateTime.Now, scale) : null;
        var layout = CompactMusicLayout.Calculate(10000, scale, media, apps.Count, text.Length > 0, clock?.ReservedWidth ?? 0, 0, equalizer, Settings.EqualizerBars); CurrentCompactLayout = layout;
        // One strip per reading, each alone on its line and centred in it. The drives show no glyph here: each drive has a line with its
        // letter and its figures, stacked or side by side as chosen in the settings, so any number of drives stand one below the other.
        var strips = new List<MonitorStrip>();
        if (Settings.ShowsMonitor(EffectiveStyle))
        {
            var colors = MonitorThemeColors.Strip(Settings);
            _stripUnavailable = _stripAdmission.Hidden;
            void Line(CompactReading reading, MonitorStrip.DriveParts drives = MonitorStrip.DriveParts.All)
            {
                var strip = new MonitorStrip([reading], colors, scale, true, PresenceOf(reading.Item), dpi, _stripUnavailable, _stripAdmission, drives);
                if (strip.PreferredWidth > 0) strips.Add(strip);
            }
            foreach (var reading in Settings.CompactReadings)
            {
                if (reading.Item != CompactMonitorItems.Storage) { Line(reading); continue; }
                foreach (var letter in CompactSources.Of(reading))
                {
                    var drive = reading.Copy(); drive.Sources = [letter];
                    Line(drive, MonitorStrip.DriveParts.Letters);
                }
            }
        }
        _stripLayout = _stripAdmission.Layout;
        bool animate = Settings.Animations && SystemParameters.ClientAreaAnimation;
        double gap = CompactMusicLayout.Gap(scale), ringGap = CompactMusicLayout.Whole(CompactColumn.RingGap * scale);
        double edge = Math.Min(Snap(layout.TextEdge, dpi), CompactMusicLayout.Whole(CollapsedLimits.ColumnPad * scale));

        var lines = new List<(FrameworkElement Element, double Height)>();
        void Add(FrameworkElement element, double height)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center; element.VerticalAlignment = VerticalAlignment.Center;
            var line = new Grid { Width = column, Height = height };
            line.Children.Add(element);
            lines.Add((line, height));
        }
        if (cover)
        {
            var image = MusicVisuals.Cover(_mediaCover, layout.CoverSize, layout.CoverRadius);
            System.Windows.Automation.AutomationProperties.SetName(image, _mediaTrack!.Caption);
            Add(image, layout.CoverSize);
        }
        foreach (var part in Settings.CompactOrder(EffectiveStyle))
        {
            switch (part)
            {
                case CompactPart.Rings when apps.Count > 0:
                    Add(ColumnRings(apps, layout, ringGap, column, out double ringsHeight), ringsHeight); break;
                case CompactPart.Music when text.Length > 0:
                    Add(_compactMarquee = CompactTitle(text, column, layout), layout.TextHeight); break;
                case CompactPart.Monitor:
                    foreach (var strip in strips)
                    {
                        strip.Margin = new Thickness(0);
                        if (strip.PreferredWidth > column + .5)
                        {
                            var carousel = new ReadingsCarousel(strip, column, strip.PreferredWidth - column, edge, _stripElapsed, animate);
                            _columnReadings.Add(carousel);
                            Add(carousel, strip.Height);
                        }
                        else { strip.Width = column; Add(strip, strip.Height); }
                    }
                    break;
                case CompactPart.Equalizer when equalizer:
                    _compactEqualizer = new MusicEqualizer { Bars = Settings.EqualizerBars, Width = layout.EqualizerSize, Height = layout.EqualizerHeight, BarWidth = layout.EqualizerBar, Color = EqualizerColor };
                    Add(_compactEqualizer, layout.EqualizerHeight); break;
                case CompactPart.Clock when clock is not null:
                    clock.Width = clock.ReservedWidth; Add(clock, clock.Height); _compactClock = clock; break;
            }
        }
        _columnStrips.AddRange(strips);
        _compactStrip = strips.FirstOrDefault();

        // The column's width and length for the shape (CollapsedGeometry.Resolve): the widest fixed part, the readings up to the cap,
        // and every line with the gaps; the surface never shortens below its tallest line. Like the row, the track notice is left out.
        var stack = CompactColumn.Stack([.. lines.Select(line => line.Height)], gap);
        double wantedColumn = CompactColumn.Width(scale, [layout.CoverSize * (cover ? 1 : 0), equalizer ? layout.EqualizerSize : 0, clock?.ReservedWidth ?? 0, apps.Count > 0 ? layout.RingSize : 0],
            strips.Select(strip => strip.PreferredWidth));
        double wanted = notice ? (double.IsNaN(_compactWanted) ? stack.Length : _compactWanted) : DevicePixels.Up(stack.Length, dpi);
        double required = lines.Count == 0 ? 0 : Math.Ceiling(lines.Max(line => line.Height));
        if (double.IsNaN(_compactWanted) || Math.Abs(wanted - _compactWanted) > .5 || Math.Abs(required - _compactRequired) > .5 || double.IsNaN(_compactColumn) || Math.Abs(wantedColumn - _compactColumn) > .5)
        { _compactWanted = wanted; _compactRequired = required; _compactColumn = wantedColumn; Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent); }

        var panel = new StackPanel { Width = column };
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) lines[i].Element.Margin = new Thickness(0, gap, 0, 0);
            panel.Children.Add(lines[i].Element);
        }
        var (_, travel) = CompactColumn.Place(stack.Length, room);
        FrameworkElement content = panel;
        if (travel > 0) content = _columnCarousel = new ColumnCarousel(panel, column, room, travel, edge, _columnElapsed, animate);
        else panel.VerticalAlignment = VerticalAlignment.Center;
        var host = new Grid { Width = column, Height = room };
        host.Children.Add(content);
        CompactRings.Children.Add(host); UpdateMusicTimer();
        _stripShowing = false; UpdateMonitorDemand();
    }

    // The quota rings in lines across the column, each line centred; the tooltip lists every quota.
    private FrameworkElement ColumnRings(IReadOnlyList<AppEntry> apps, CompactMusicLayout layout, double gap, double column, out double height)
    {
        var rings = QuotaRings(apps);
        var (perLine, count) = CompactColumn.Rings(rings.Length, layout.RingSize, gap, column);
        height = count * layout.RingSize + Math.Max(0, count - 1) * gap;
        var canvas = new Canvas { Width = column, Height = height, ToolTip = string.Join("\n", rings.Select(ring => ring.Tip)) };
        for (int i = 0; i < rings.Length; i++)
        {
            int line = i / perLine, inLine = Math.Min(perLine, rings.Length - line * perLine);
            double start = (column - (inLine * layout.RingSize + (inLine - 1) * gap)) / 2;
            var ring = QuotaCarousel.Ring(rings[i], layout.RingSize, layout.RingThickness, layout.Scale);
            Canvas.SetLeft(ring, Math.Round(start + (i % perLine) * (layout.RingSize + gap)));
            Canvas.SetTop(ring, line * (layout.RingSize + gap));
            canvas.Children.Add(ring);
        }
        System.Windows.Automation.AutomationProperties.SetName(canvas, Loc.T("配额环 ", "Quota rings ") + canvas.ToolTip);
        return canvas;
    }

    // Every strip of the collapsed readings takes the new reading: the row's (one per reading), or the column's.
    private void UpdateCompactStrips(MonitorSnapshot? snapshot, bool motion)
    {
        _compactStrip?.Update(snapshot, motion, (int)Settings.CompactSmoothing);
        foreach (var strip in _rowStrips) if (!ReferenceEquals(strip, _compactStrip)) strip.Update(snapshot, motion, (int)Settings.CompactSmoothing);
        foreach (var strip in _columnStrips) if (!ReferenceEquals(strip, _compactStrip)) strip.Update(snapshot, motion, (int)Settings.CompactSmoothing);
    }
}
