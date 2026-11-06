using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;

namespace TwelveIsland;

// Draws one frame of the dot sinking into still water (DotMotion.MergeAt). The figure is the dot and the surface melted into
// one outline (DotMotion.MergeDistance), filled pixel by pixel on the device's own pixels with its edge smoothed across one
// pixel, so it has no seams, no notches and no blurred edge at any scale. Progress is 0 as the dot lands and 1 when it has
// gone; only Progress is animated. The waterline is WaterBelow above the bottom of this element, under the dot's middle.
internal sealed class DropMerge : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(DropMerge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Color Fill { get; set; } = Colors.Black;
    public const double WaterBelow = 4;

    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];

    public DropMerge() { UseLayoutRounding = true; SnapsToDevicePixels = true; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
            _pixels = new uint[width * height];
        }
        var merge = DotMotion.MergeAt(Progress);
        double cx = ActualWidth / 2, waterY = ActualHeight - WaterBelow;
        bool any = false;
        for (int row = 0; row < height; row++)
        {
            double y = (row + .5) / dpi.DpiScaleY - waterY;
            for (int column = 0; column < width; column++)
            {
                double x = (column + .5) / dpi.DpiScaleX - cx;
                // The distance in device pixels gives the share of this pixel the figure covers.
                double cover = Math.Clamp(.5 - DotMotion.MergeDistance(merge, x, y) * dpi.DpiScaleX, 0, 1);
                _pixels[row * width + column] = cover > 0 ? Premultiplied(cover) : 0;
                any |= cover > 0;
            }
        }
        if (!any) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
        drawingContext.DrawImage(_bitmap, new Rect(0, 0, width / dpi.DpiScaleX, height / dpi.DpiScaleY));
    }

    private uint Premultiplied(double cover)
    {
        double alpha = Fill.A / 255d * cover;
        uint a = (uint)Math.Round(alpha * 255), r = (uint)Math.Round(Fill.R * alpha), g = (uint)Math.Round(Fill.G * alpha), b = (uint)Math.Round(Fill.B * alpha);
        return a << 24 | r << 16 | g << 8 | b;
    }
}
