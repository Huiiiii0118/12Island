using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TwelveIsland;

public partial class MainWindow
{
    private const string GearGlyph = "\uE713";

    // A newer version that was not skipped, or an update whose notes were not opened yet, adds a small blue dot to the settings
    // gear. Without either the button shows the plain glyph. Rebuilt only when the update state changes, never per frame.
    internal void UpdateNotice()
    {
        RefreshStatus();
        SettingsButton.Content = _app.Updates.NoticeVersion is null && _app.Updates.UpdatedFrom is null ? GearGlyph : NoticeGear();
    }

    // The 16 x 16 glyph box sits at (6, 4) in the 28 x 24 button, so the dot's center (20, 6), on the top-right tooth tip, is (14, 2)
    // here. The gear is drawn as its glyph outline with a circle of radius 3.5 removed around the 4 DIP dot: 1 DIP of clear space at
    // the dot itself, and wide enough that no cut-off piece of the tooth is left behind (radius 3 leaves a sliver). The rest of the
    // gear is unchanged.
    private FrameworkElement NoticeGear()
    {
        const double box = 16, x = 14, y = 2, dot = 4, clear = 3.5;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(GearGlyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(SettingsButton.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), SettingsButton.FontSize, Brushes.White, dpi);
        var gear = new CombinedGeometry(GeometryCombineMode.Exclude, text.BuildGeometry(new Point((box - text.WidthIncludingTrailingWhitespace) / 2, (box - text.Height) / 2)),
            new EllipseGeometry(new Point(x, y), clear, clear));
        gear.Freeze();
        var shape = new System.Windows.Shapes.Path { Data = gear, SnapsToDevicePixels = true };
        shape.SetBinding(Shape.FillProperty, new Binding(nameof(Foreground)) { Source = SettingsButton });
        var mark = new Ellipse
        {
            Width = dot, Height = dot, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x - dot / 2, y - dot / 2, 0, 0)
        };
        mark.SetResourceReference(Shape.FillProperty, "PanelAccent");
        var drawing = new Grid { Width = box, Height = box, UseLayoutRounding = true, IsHitTestVisible = false };
        drawing.Children.Add(shape); drawing.Children.Add(mark);
        return drawing;
    }
}
