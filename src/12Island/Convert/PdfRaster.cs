using System.IO;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TwelveIsland;

// A PDF's pages as pictures, drawn by the PDF renderer Windows itself carries: no library, no tool, nothing downloaded.
internal static class PdfRaster
{
    // 150 dots per inch: sharp on screen and in print, and a page stays a sensible size.
    private const double Dpi = 150;

    // The pages written, in order. One page keeps the name it was given; more than one is numbered after it.
    public static async Task<List<string>> ToImagesAsync(string source, string output, string extension, CancellationToken token)
    {
        var saved = new List<string>();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source));
        var document = await PdfDocument.LoadFromFileAsync(file);
        {
            if (document.PageCount == 0) throw new InvalidOperationException(Loc.T("这个 PDF 没有页面", "This PDF has no pages"));
            bool many = document.PageCount > 1;
            string folder = Path.GetDirectoryName(output) ?? ".";
            string name = Path.GetFileNameWithoutExtension(output);
            for (uint index = 0; index < document.PageCount; index++)
            {
                token.ThrowIfCancellationRequested();
                using var page = document.GetPage(index);
                using var stream = new InMemoryRandomAccessStream();
                var render = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width / 72 * Dpi)) };
                await page.RenderToStreamAsync(stream, render);
                var bytes = await ReadAsync(stream);
                using var input = new MemoryStream(bytes);
                var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                string path = many ? Path.Combine(folder, $"{name}-{index + 1:00}{extension}") : output;
                Save(frame, path, extension);
                saved.Add(path);
            }
        }
        return saved;
    }

    private static async Task<byte[]> ReadAsync(IRandomAccessStream stream)
    {
        uint size = (uint)stream.Size;
        var bytes = new byte[size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // The same encoders the picture page uses, so a PDF's page becomes a PNG or a JPEG like any other picture.
    private static void Save(BitmapSource frame, string path, string extension)
    {
        BitmapEncoder encoder = extension switch
        {
            ".jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        BitmapSource image = frame;
        if (encoder is JpegBitmapEncoder && frame.Format != System.Windows.Media.PixelFormats.Bgr24) image = FileConverter.Flatten(frame);
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var target = File.Create(path);
        encoder.Save(target);
    }
}
