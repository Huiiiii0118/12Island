using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 12 island application icon: a near-black squircle with a white capsule across its middle — the island — carrying "12" in
// black at its left and a coral status dot at its right. One shape, two tones, no second outline, as a Mac app's icon is set.
// Composition on a 256-unit canvas: squircle 0–256 with a 58 corner radius (macOS's ratio), the capsule 152 × 60 centred,
// the numeral at 82 % of the capsule's height, the dot 14 across on the capsule's right shoulder.
// Sizes of 24 and below drop the numeral and enlarge the capsule, which would otherwise be a hairline at tray size; 16 and
// below drop the dot as well. The numeral is drawn as a geometry so its ink, not its line box, is what gets centred.
// Usage: dotnet run --project scripts/icon -c Release -- <output.ico> <preview.png>
internal static class Program
{
    // One frame for every size Windows asks for at the common display scales (100 %–300 %), so no size is resampled from another:
    // task bar 24·scale (24, 30, 36, 42, 48, 54, 60, 72), notification area 16·scale (16, 20, 24, 28, 32, 36, 40, 48),
    // Explorer and desktop 32·scale (32, 40, 48, 56, 64, 72, 80, 96), plus 128 and 256 for large views.
    private static readonly int[] Sizes = [16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 54, 56, 60, 64, 72, 80, 96, 128, 256];
    private static readonly Color Ink = Color.FromRgb(0x0A, 0x0B, 0x0E), Paper = Color.FromRgb(0xF5, 0xF6, 0xF8);
    private static readonly Color Dot = Color.FromRgb(0xE5, 0xA3, 0x85);

    [STAThread]
    private static void Main(string[] args)
    {
        string icoPath = args.Length > 0 ? args[0] : "12Island.ico";
        string previewPath = args.Length > 1 ? args[1] : "icon-preview.png";
        var frames = Sizes.Select(size => (Size: size, Png: Encode(Render(size)))).ToList();
        WriteIco(icoPath, frames);
        File.WriteAllBytes(previewPath, Encode(Preview(frames.Select(f => Decode(f.Png)).ToList())));
        Console.WriteLine($"icon: {icoPath} ({new FileInfo(icoPath).Length} bytes); preview: {previewPath}");
    }

    private static RenderTargetBitmap Render(int size)
    {
        bool small = size <= 24;
        // The tray and task bar sizes get a capsule that fills more of the square: at 16 px the 152 × 60 one would be 9.5 × 3.75.
        double width = small ? 180 : 152, height = small ? 92 : 60;
        var capsule = new Rect(128 - width / 2, 128 - height / 2, width, height);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256d, size / 256d));
            var back = new LinearGradientBrush(Color.FromRgb(0x1E, 0x20, 0x26), Color.FromRgb(0x07, 0x08, 0x0B), 90);
            dc.DrawRoundedRectangle(back, null, new Rect(0, 0, 256, 256), 58, 58);
            // A hairline of light along the top edge keeps the dark square from reading as a hole on a dark task bar.
            if (size >= 48)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 2), new Rect(1, 1, 254, 254), 57, 57);

            var face = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE4, 0xE6, 0xEB), 90);
            dc.DrawRoundedRectangle(face, null, capsule, height / 2, height / 2);

            // The numeral sits left of centre so the dot on the right shoulder balances it; at tray sizes the numeral is
            // under a pixel high, so the dot alone carries the shape.
            if (small) dc.DrawEllipse(new SolidColorBrush(Dot), null, new Point(capsule.Right - 30, 128), 12, 12);
            else
            {
                dc.DrawGeometry(new SolidColorBrush(Ink), null, Numeral(height * .82, capsule.Left + 64, 128));
                dc.DrawEllipse(new SolidColorBrush(Dot), null, new Point(capsule.Right - 28, 128), 14, 14);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // "12" as a geometry centred on its ink: a text line box carries the ascender and descender, which would sit the digits
    // low in a capsule this shallow.
    private static Geometry Numeral(double em, double centerX, double centerY)
    {
        var text = new FormattedText("12", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), em,
            Brushes.Black, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var ink = geometry.Bounds;
        // Slightly tighter than the font's default side bearing, so the pair reads as one mark at 32 px.
        var squeeze = new ScaleTransform(.96, 1, ink.X + ink.Width / 2, 0);
        geometry.Transform = new TransformGroup
        {
            Children = { squeeze, new TranslateTransform(centerX - (ink.X + ink.Width / 2), centerY - (ink.Y + ink.Height / 2)) },
        };
        geometry.Freeze();
        return geometry;
    }

    // Three bands: a dark task bar, a light task bar, and the tray overflow panel at 150 % (24 px).
    private static RenderTargetBitmap Preview(IReadOnlyList<BitmapSource> frames)
    {
        const int band = 300, panelHeight = 120;
        int width = 48 + frames.Sum(f => f.PixelWidth + 22);
        int height = band * 2 + panelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x28)), null, new Rect(0, 0, width, band));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF3)), null, new Rect(0, band, width, band));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x46, 0x56, 0x6C)), null, new Rect(0, band * 2, width, panelHeight));
            double x = 24;
            foreach (var frame in frames)
            {
                foreach (int row in new[] { 0, 1 })
                {
                    double y = row * band + (band - frame.PixelHeight) / 2;
                    dc.DrawImage(frame, new Rect(Math.Round(x), Math.Round(y), frame.PixelWidth, frame.PixelHeight));
                }
                x += frame.PixelWidth + 22;
            }
            var tray = frames.First(f => f.PixelWidth == 24);
            for (int i = 0; i < 5; i++)
                dc.DrawImage(tray, new Rect(24 + i * 40 + 8, band * 2 + 48, 24, 24));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Decode(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(png.Length); writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) writer.Write(png);
    }
}
