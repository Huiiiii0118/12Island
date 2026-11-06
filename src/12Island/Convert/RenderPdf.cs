using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;

namespace TwelveIsland;

// A document or a sheet as a PDF, laid out and drawn by Windows' own text engine with the fonts on this machine, then written
// page by page as pictures. Nothing else is installed and nothing is sent anywhere.
internal static class RenderPdf
{
    // A4 at 96 dots per inch, the unit WPF lays pages out in.
    private const double PageWidth = 794, PageHeight = 1123;
    private const double RenderScale = 2;

    public static string FromDocx(string source, string output, CancellationToken token) => OnSta(() =>
    {
        var document = Flow();
        foreach (var block in FileConverter.DocxBlocks(source))
        {
            token.ThrowIfCancellationRequested();
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
            if (block.Heading) { paragraph.FontSize = 17; paragraph.FontWeight = FontWeights.SemiBold; }
            foreach (var (text, bold, italic, size) in block.Runs)
            {
                var run = new Run(text)
                {
                    FontWeight = bold || block.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal
                };
                if (size > 0) run.FontSize = size;
                paragraph.Inlines.Add(run);
            }
            document.Blocks.Add(paragraph);
        }
        return Write(document, output, token);
    });

    public static string FromText(string text, string output, CancellationToken token) => OnSta(() =>
    {
        var document = Flow();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            string value = line.TrimEnd();
            if (value.Length == 0) { document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 4) }); continue; }
            // A line that opens with # is a heading, as it is in the Markdown this text may have come from.
            bool heading = value.StartsWith('#');
            var paragraph = new Paragraph(new Run(heading ? value.TrimStart('#', ' ') : value)) { Margin = new Thickness(0, 0, 0, 4) };
            if (heading) { paragraph.FontSize = 17; paragraph.FontWeight = FontWeights.SemiBold; }
            document.Blocks.Add(paragraph);
        }
        return Write(document, output, token);
    });

    public static string FromSheet(string source, string output, CancellationToken token) => OnSta(() => Sheet(FileConverter.SheetRows(source), output, token));

    private static FlowDocument Flow() => new()
    {
        PageWidth = PageWidth, PageHeight = PageHeight, ColumnWidth = PageWidth, PagePadding = new Thickness(56),
        Background = Brushes.White, FontFamily = new FontFamily("Microsoft YaHei"), FontSize = 12.5
    };

    // The document's pages as pictures, then those pictures into one PDF.
    private static string Write(FlowDocument document, string output, CancellationToken token)
    {
        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(PageWidth, PageHeight);
        paginator.ComputePageCount();
        var pages = new List<PdfWriter.Page>();
        for (int index = 0; index < paginator.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            using var page = paginator.GetPage(index);
            pages.Add(Draw(page.Visual));
        }
        PdfWriter.Write(output, pages);
        return output;
    }

    // A sheet's rows as a grid, split over pages that repeat the first row as a header.
    private static string Sheet(List<List<string>> rows, string output, CancellationToken token)
    {
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (columns == 0) throw new InvalidOperationException(Loc.T("这个表格没有内容", "This spreadsheet is empty"));
        // Columns take the width their widest cell asks for, within the page's width.
        var widths = new double[columns];
        for (int column = 0; column < columns; column++)
        {
            double widest = 48;
            foreach (var row in rows.Take(400))
                if (column < row.Count) widest = Math.Max(widest, Label(row[column], 11).WidthIncludingTrailingWhitespace + 16);
            widths[column] = Math.Min(widest, 240);
        }
        double total = widths.Sum(), limit = PageWidth - 72;
        if (total > limit) for (int i = 0; i < columns; i++) widths[i] *= limit / total;
        const double rowHeight = 22;
        int perPage = Math.Max(1, (int)((PageHeight - 72) / rowHeight) - 1);
        var pages = new List<PdfWriter.Page>();
        // The first row is the header, repeated on every page; the body starts under it.
        for (int first = 1; first < rows.Count; first += perPage)
        {
            token.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
                double y = 36;
                DrawRow(context, rows[0], widths, y, rowHeight, header: true);
                y += rowHeight;
                for (int i = 0; i < perPage && first + i < rows.Count; i++, y += rowHeight)
                    DrawRow(context, rows[first + i], widths, y, rowHeight, header: false);
            }
            pages.Add(Draw(visual));
        }
        // A sheet of one row is its header alone.
        if (pages.Count == 0)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
                DrawRow(context, rows[0], widths, 36, rowHeight, header: true);
            }
            pages.Add(Draw(visual));
        }
        PdfWriter.Write(output, pages);
        return output;
    }

    private static void DrawRow(DrawingContext context, List<string> cells, double[] widths, double y, double height, bool header)
    {
        double x = 36;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xDA)), 0.7);
        for (int column = 0; column < widths.Length; column++)
        {
            var box = new Rect(x, y, widths[column], height);
            context.DrawRectangle(header ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)) : Brushes.White, pen, box);
            string text = column < cells.Count ? cells[column] : "";
            if (text.Length > 0)
            {
                var label = Label(text, 11, header ? FontWeights.SemiBold : FontWeights.Normal);
                label.MaxTextWidth = widths[column] - 10;
                label.MaxTextHeight = height - 4;
                label.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(label, new Point(x + 5, y + (height - label.Height) / 2));
            }
            x += widths[column];
        }
    }

    private static FormattedText Label(string text, double size, FontWeight? weight = null) => new(
        text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size, Brushes.Black, 1.0);

    // One page drawn as a JPEG at twice the layout size, so the text stays sharp.
    private static PdfWriter.Page Draw(Visual visual)
    {
        var target = new RenderTargetBitmap((int)Math.Round(PageWidth * RenderScale), (int)Math.Round(PageHeight * RenderScale), 96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        var page = new ContainerVisual();
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
        page.Children.Add(background);
        page.Children.Add(visual);
        target.Render(page);
        var jpeg = new MemoryStream();
        new JpegBitmapEncoder { QualityLevel = 90, Frames = { BitmapFrame.Create(target) } }.Save(jpeg);
        return new PdfWriter.Page(jpeg.ToArray(), target.PixelWidth, target.PixelHeight);
    }

    // WPF lays out and draws on a single-threaded apartment; the conversion runs on a worker thread, so the drawing gets one.
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
