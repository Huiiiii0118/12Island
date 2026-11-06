using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;

namespace TwelveIsland;

// A reading switched on or off in the settings changes how many readings the collapsed row holds, and the row built for the new
// choice appeared at once, readings and all, over a surface that was still moving to its new width. The row as it was is kept as a
// picture and fades out over the new one, which fades in: the readings that stay do not move and the ones that come or go dissolve
// (the same of a style's readings turned on or off altogether, and of the panel's scale, which draws the whole row at another size).
// Nothing else rebuilds the row with a different choice of readings:
// a resize, a clock tick or a reading's own figures keep the same key and build the row as before.
public partial class MainWindow
{
    private string? _rowReadings;
    private const int RowFadeMs = 160;

    // The choice the row is built for: the panel's scale (the whole row is drawn again at another size), which readings are shown, and
    // whether the row shows any.
    private string ReadingsChoice() => Settings.PanelScale + ":" + (!Settings.ShowsMonitor(EffectiveStyle) ? "" : string.Join(",", Settings.CompactReadings.Where(r => r.Visible).Select(r => (int)r.Item)));

    // Called as the collapsed row is about to be built again: a picture of the row on show if its choice of readings has changed.
    private Image? RowFadeStart()
    {
        string key = ReadingsChoice();
        bool changed = _rowReadings is not null && key != _rowReadings;
        _rowReadings = key;
        if (!changed || !Soft.Enabled || _expanded || !IsVisible || _alert is not null || Compact.Visibility != Visibility.Visible || Collapsed.Style == CompactStyle.Line) return null;
        double width = CompactRings.ActualWidth, height = CompactRings.ActualHeight;
        if (!(width > 1) || !(height > 1) || PresentationSource.FromVisual(CompactRings) is null) return null;
        double dpi = VisualTreeHelper.GetDpi(CompactRings).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(CompactRings);
        bitmap.Freeze();
        return new Image { Source = bitmap, Width = width, Height = height, IsHitTestVisible = false, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    }

    // The row is built: the picture of the old one dissolves over it.
    private void RowFadeRun(Image? ghost)
    {
        if (ghost is null) return;
        Compact.Children.Add(ghost);
        var length = TimeSpan.FromMilliseconds(RowFadeMs);
        var leave = new DoubleAnimation(1, 0, length) { EasingFunction = Soft.Out };
        leave.Completed += (_, _) => Compact.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, leave);
        CompactRings.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, length) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
    }
}
