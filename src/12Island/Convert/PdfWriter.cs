using System.Globalization;
using System.IO;
using System.Text;
using TwelveIsland.Core;

namespace TwelveIsland;

// Writes a PDF whose pages are pictures: each page holds one JPEG, scaled to fit A4 and centred. Everything is written by hand,
// so the file is made on this machine with no library and no office suite.
internal static class PdfWriter
{
    // A4 at 72 points per inch.
    public const double PageWidth = 595, PageHeight = 842;

    public sealed record Page(byte[] Jpeg, int Width, int Height);

    public static void Write(string output, IReadOnlyList<Page> pages)
    {
        if (pages.Count == 0) throw new IOException(Loc.T("转换没有生成页面", "The conversion produced no page"));
        const double landscapeWidth = PageHeight, landscapeHeight = PageWidth;
        // Objects: 1 catalog, 2 the page tree, then three per page (page, contents, picture). The picture holding binary data
        // is written by hand, so its offset is recorded around it.
        var objects = new List<byte[]>();
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{3 + index * 3} 0 R"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>"));
        for (int index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            double width = page.Width > page.Height ? landscapeWidth : PageWidth;
            double height = page.Width > page.Height ? landscapeHeight : PageHeight;
            double scale = Math.Min(width / page.Width, height / page.Height);
            double drawWidth = page.Width * scale, drawHeight = page.Height * scale;
            double x = (width - drawWidth) / 2, y = (height - drawHeight) / 2;
            int image = 3 + index * 3 + 2;
            string content = $"q {drawWidth:0.##} 0 0 {drawHeight:0.##} {x:0.##} {y:0.##} cm /Im0 Do Q";
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width:0.##} {height:0.##}] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {3 + index * 3 + 1} 0 R >>"));
            objects.Add(Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
            objects.Add([]);
        }
        using var file = File.Create(output);
        var offsets = new List<long>();
        void Line(string text) => file.Write(Encoding.ASCII.GetBytes(text + "\n"));
        Line("%PDF-1.4");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Line($"{i + 1} 0 obj");
            if (objects[i].Length == 0)
            {
                var page = pages[(i - 2) / 3];
                Line($"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>");
                Line("stream"); file.Write(page.Jpeg); Line(""); Line("endstream");
            }
            else file.Write(objects[i]);
            Line("endobj");
        }
        long start = file.Position;
        Line("xref"); Line($"0 {offsets.Count + 1}"); Line("0000000000 65535 f ");
        foreach (var offset in offsets) Line($"{offset:0000000000} 00000 n ");
        Line("trailer"); Line($"<< /Size {offsets.Count + 1} /Root 1 0 R >>"); Line("startxref");
        Line(start.ToString(CultureInfo.InvariantCulture)); Line("%%EOF");
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
