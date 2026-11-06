using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwelveIsland.Core;

namespace TwelveIsland;

// The collapsed row's sizes for any width and height the user sets. The collapsed shape (CollapsedGeometry) gives the row's
// width in DIPs and a height scale; nothing is scaled by a transform. Sizes and font sizes are computed directly and
// positions fall on whole device pixels, so the text keeps ClearType and the pixel snapping of the default size.
public partial class MainWindow
{
    // The shape the collapsed row was last built for; a new shape rebuilds it. Both hooks run on every shape and alert change
    // and on every open and close, so they only rebuild when something they depend on has changed.
    private CollapsedShape? _compactRendered;
    private bool _compactRendering;
    private (CollapsedShape Shape, object Alert)? _alertRendered;
    // The notice on show and when it appeared (Environment.TickCount64).
    private (object Alert, long Since)? _alertSince;
    private static readonly object AlertRegionTag = new();

    // The row's width. The notch's shoulders and bottom corners keep their sizes at every height while its inset shrinks with
    // the height, so the row also keeps clear of both curves: at least the shoulder plus the corner radius from each end,
    // which is within the default 30 DIP inset at the default height.
    private static double CompactContentWidth(CollapsedShape shape)
    {
        double width = double.IsFinite(shape.ContentWidth) ? Math.Max(0, shape.ContentWidth) : 0;
        if (shape.Style != CompactStyle.Notch) return width;
        double curves = (double.IsFinite(shape.Shoulder) ? Math.Max(0, shape.Shoulder) : 0) + (double.IsFinite(shape.BottomRadius) ? Math.Max(0, shape.BottomRadius) : 0);
        double surface = double.IsFinite(shape.Surface.Width) ? Math.Max(0, shape.Surface.Width) : 0;
        return Math.Min(width, Math.Max(0, surface - 2 * curves));
    }

    // Runs several times for one change; every value is computed from the shape alone, so repeating it changes nothing.
    partial void ApplyCompactGeometry(CollapsedShape shape)
    {
        double scale = CompactMusicLayout.SafeScale(shape.Scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (ColumnFor(shape.Style))
        {
            // A notch on a side edge: the column across it and its length down the edge (MainWindow.CompactColumn.cs). The content is
            // upright (IslandContent), so its width is the notch's depth.
            Compact.Width = SnapDown(ColumnWidth(shape), dpi);
            Compact.Height = SnapDown(CompactContentWidth(shape), dpi);
        }
        else
        {
            Compact.Width = SnapDown(CompactContentWidth(shape), dpi);
            // The content box: the 24 DIP row with a DIP above and below, as at the default height.
            Compact.Height = Math.Max(1, CompactMusicLayout.Whole(26 * scale));
        }
        if (shape.Style == CompactStyle.Line)
        {
            // The progress bar is as thick as the indicator, at least one device pixel, and rounded at its ends. On a side edge it
            // runs down the screen (MainWindow.Edge.cs).
            double thickness = double.IsFinite(shape.Surface.Height) && shape.Surface.Height > 0 ? shape.Surface.Height : 4;
            thickness = Math.Max(1, Math.Round(thickness * dpi, MidpointRounding.AwayFromZero)) / dpi;
            if (_frame.Across) MusicIndicator.Width = thickness; else MusicIndicator.Height = thickness;
            MusicIndicator.CornerRadius = new CornerRadius(thickness / 2);
        }
        // RenderCompact runs these hooks again; by then the shape matches, so this does not repeat.
        if (!Equals(shape, _compactRendered) && !_compactRendering)
        {
            _compactRendered = shape; _compactRendering = true;
            try { RenderCompact(); } finally { _compactRendering = false; }
        }
    }

    // The quota notice replaces the collapsed row: its ring, name, "used" text and reset time side by side, centred. A notice
    // longer than the row scrolls through it once while it is shown (CompactAlertLayout.Scroll).
    partial void LayoutCompactAlert(CollapsedShape shape, bool alertVisible)
    {
        // An unread notification takes the row while no quota warning is on: it stays until the page is read.
        if (alertVisible && _alert is null && _notice is not null) { BuildCompactNotice(shape); return; }
        if (!alertVisible || _alert is not { } alert)
        {
            // A cleared notice leaves none of this file's regions behind.
            if (CompactAlert.Children.Count > 0 && CompactAlert.Children[0] is FrameworkElement { Tag: var own } && ReferenceEquals(own, AlertRegionTag)) CompactAlert.Children.Clear();
            _alertRendered = null; _alertSince = null; return;
        }
        // The scroll follows the time since the notice appeared, so a rebuild (a new shape or theme) carries on where it was.
        if (_alertSince is not { } since || !ReferenceEquals(since.Alert, alert)) _alertSince = (alert, Environment.TickCount64);
        // An unchanged notice that this file built is kept, so opening and closing the panel do not rebuild it.
        if (_alertRendered is { } done && Equals(done.Shape, shape) && ReferenceEquals(done.Alert, alert)
            && CompactAlert.Children.Count > 0 && CompactAlert.Children[0] is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, AlertRegionTag)) return;
        CompactAlert.Children.Clear();
        _alertRendered = (shape, alert); _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        double width = CompactContentWidth(shape);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double font = Math.Max(1, CompactMusicLayout.Half(11.5 * scale)), line = Math.Max(1, CompactMusicLayout.Whole(16 * scale));
        var name = UI.Fixed(alert.Name, font, line, UI.Primary, FontWeights.SemiBold); name.ToolTip = alert.Name;
        // The name keeps its 76 DIP cap with an ellipsis; below that, its region cuts it.
        name.MaxWidth = CompactMusicLayout.Whole(76 * scale);
        var used = UI.Fixed(Loc.T($"{alert.Label}已用 {alert.Used:0}%", $"{alert.Label}{alert.Used:0}% used"), font, line, UI.Secondary);
        string shortReset = UI.ShortReset(alert.ResetAt);
        var reset = shortReset.Length > 0 ? UI.Fixed(Loc.T(shortReset + "重置", shortReset.StartsWith("in ", StringComparison.Ordinal) ? "resets " + shortReset : shortReset), font, line, UI.Tertiary) : null;
        double Natural(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(element.DesiredSize.Width * dpi - .001) / dpi;
        }
        double nameWidth = Natural(name), usedWidth = Natural(used), resetWidth = reset is null ? 0 : Natural(reset);
        var layout = CompactAlertLayout.Calculate(double.MaxValue, scale, nameWidth, usedWidth, resetWidth);
        if (ColumnFor(shape.Style))
        {
            // A notch on a side edge: the ring, then each text on its own line, cut with an ellipsis to the column.
            double column = SnapDown(ColumnWidth(shape), dpi);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = column, Tag = AlertRegionTag };
            var mark = UI.Ring(alert.Used, PanelTheme.QuotaTheme(alert.Entry), layout.RingSize, layout.RingThickness);
            mark.HorizontalAlignment = HorizontalAlignment.Center; mark.Margin = new Thickness(0, 0, 0, layout.Gap / 2);
            stack.Children.Add(mark);
            name.MaxWidth = column;
            foreach (var text in new[] { name, used, reset })
            {
                if (text is null) continue;
                text.ClearValue(WidthProperty); text.MaxWidth = column; text.TextTrimming = TextTrimming.CharacterEllipsis; text.TextAlignment = TextAlignment.Center;
                text.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(text);
            }
            CompactAlert.Children.Add(stack);
            return;
        }
        double row = Math.Max(layout.RingSize, layout.LineHeight);
        var ring = UI.Ring(alert.Used, PanelTheme.QuotaTheme(alert.Entry), layout.RingSize, layout.RingThickness);
        var parts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (layout.RingWidth > 0) parts.Children.Add(CompactRegion(ring, layout.RingWidth, layout.RingSize, layout.RingSize, row, RegionAnchor.Left, dpi));
        if (layout.NameWidth > 0) parts.Children.Add(CompactRegion(name, layout.NameWidth, Math.Min(nameWidth, name.MaxWidth), line, row, RegionAnchor.Left, dpi));
        if (layout.TextWidth > 0) parts.Children.Add(CompactRegion(used, layout.TextWidth, usedWidth, line, row, RegionAnchor.Left, dpi));
        if (reset is not null && layout.ResetWidth > 0) parts.Children.Add(CompactRegion(reset, layout.ResetWidth, resetWidth, line, row, RegionAnchor.Left, dpi, layout.Gap));
        parts.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double span = SnapDown(width, dpi), full = parts.DesiredSize.Width;
        // The allowance keeps rounding noise from starting a one-pixel scroll.
        double overflow = Math.Max(0, Math.Ceiling((full - span) * dpi - .01) / dpi);
        if (overflow <= 0) { CompactAlert.Children.Add(parts); parts.Tag = AlertRegionTag; return; }
        // The row is laid out in a Canvas, which leaves it its full width; inside a clipping panel the layout clip would move
        // with the translation and cut off the end that scrolls in.
        var shift = new TranslateTransform();
        parts.RenderTransform = shift;
        var view = new Canvas { Width = span, Height = row, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Tag = AlertRegionTag };
        Canvas.SetTop(parts, Snap((row - parts.DesiredSize.Height) / 2, dpi));
        view.Children.Add(parts);
        RenderOptions.SetClearTypeHint(parts, ClearTypeHint.Enabled);
        CompactAlert.Children.Add(view);
        double shown = _alertTimer.Interval.TotalSeconds;
        var (start, end) = CompactAlertLayout.Scroll(overflow, shown);
        static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
        var glide = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(shown), FillBehavior = FillBehavior.HoldEnd };
        glide.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(0)));
        glide.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(start)));
        // Without animations the notice steps to its end once, when the glide would have finished.
        if (Settings.Animations && SystemParameters.ClientAreaAnimation) glide.KeyFrames.Add(new SplineDoubleKeyFrame(-overflow, At(end), new KeySpline(.4, 0, .6, 1)));
        else glide.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, At(end)));
        var clock = glide.CreateClock();
        shift.ApplyAnimationClock(TranslateTransform.XProperty, clock);
        double elapsed = Math.Clamp((Environment.TickCount64 - _alertSince!.Value.Since) / 1000d, 0, shown);
        if (elapsed > 0) clock.Controller!.Seek(TimeSpan.FromSeconds(elapsed), TimeSeekOrigin.BeginTime);
    }

    private enum RegionAnchor { Left, Center, Right }
    // Rounded down to whole device pixels, so regions never add up to more than the row.
    private static double SnapDown(double value, double dpi) => DevicePixels.Down(value, dpi);
    private static double Snap(double value, double dpi) => double.IsFinite(value) && dpi > 0 ? Math.Round(value * dpi, MidpointRounding.AwayFromZero) / dpi : 0;

    // A region of the given width holding one element, centred vertically in the row. The element keeps its size and is
    // anchored to one side (after a leading gap, if any); a region narrower than that cuts the other side off.
    // Text below the cut is set to ClearType again: the clip would otherwise turn it grayscale, and it lies over the opaque surface.
    private static FrameworkElement CompactRegion(FrameworkElement child, double width, double childWidth, double childHeight, double row, RegionAnchor anchor, double dpi, double leading = 0)
    {
        double w = SnapDown(width, dpi), cw = Math.Max(0, double.IsFinite(childWidth) ? childWidth : 0), ch = Math.Max(0, double.IsFinite(childHeight) ? childHeight : 0);
        double left = anchor switch { RegionAnchor.Right => w - cw, RegionAnchor.Center => (w - cw) / 2, _ => Math.Min(leading, w) };
        var region = new Canvas { Width = w, Height = row, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        child.Width = cw; child.HorizontalAlignment = HorizontalAlignment.Left; child.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(child, Snap(left, dpi)); Canvas.SetTop(child, Snap((row - ch) / 2, dpi));
        region.ClipToBounds = left < 0 || left + cw > w + .001;
        if (region.ClipToBounds && child is TextBlock) RenderOptions.SetClearTypeHint(child, ClearTypeHint.Enabled);
        region.Children.Add(child);
        return region;
    }
}
