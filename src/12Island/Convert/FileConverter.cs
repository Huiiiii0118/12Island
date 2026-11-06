using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using TwelveIsland.Core;

namespace TwelveIsland;

// Turns a file into another format. Everything is done on this machine: pictures, documents, spreadsheets and text in the panel
// itself, sound and video through the FFmpeg carried inside the program. Nothing is uploaded and nothing has to be installed.
internal sealed class FileConverter
{
    // Text files are written without a byte-order mark: an editor, a script or an importer reads them as plain UTF-8.
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    public string? Ffmpeg { get; private set; }
    public string? Office { get; private set; }

    // FFmpeg is part of the program, so sound, video and WebP are always offered: the copy inside is unpacked on first use.
    public bool HasFfmpeg => Ffmpeg is not null || BundledTools.HasFfmpeg;
    public bool HasOffice => Office is not null;

    public FileConverter(DeckSettings settings) => Detect(settings);

    public void Detect(DeckSettings settings)
    {
        Ffmpeg = Existing(settings.ConvertFfmpegPath) ?? OnPath("ffmpeg.exe") ?? FirstExisting(
            @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe", @"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
        Office = Existing(settings.ConvertOfficePath) ?? OnPath("soffice.exe") ?? FirstExisting(
            @"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe");
    }

    private static string? Existing(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    // A tool found through the PATH: "where" lists every match and its first line is the one a shell would run.
    private static string? OnPath(string name)
    {
        try
        {
            var start = new ProcessStartInfo("where", name)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    // The target's path for a source: the chosen folder or the source's own, and a name that does not overwrite anything.
    public static string OutputPath(string source, string extension, string? folder)
    {
        string directory = string.IsNullOrWhiteSpace(folder) ? Path.GetDirectoryName(source) ?? "." : folder!;
        string name = Path.GetFileNameWithoutExtension(source) + extension;
        string path = Path.Combine(directory, name);
        for (int i = 2; File.Exists(path) && i < 1000; i++) path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(source)} ({i}){extension}");
        return path;
    }

    // Converts one file. progress reports a line of text; the work runs on the calling thread except for the tools, which are
    // separate processes.
    public async Task<string> ConvertAsync(string source, ConvertTarget target, string? folder, Action<string>? progress, CancellationToken token)
    {
        string output = OutputPath(source, target.Extension, folder);
        string family = ConvertFormats.FamilyOf(source)?.ToString() ?? "";
        bool image = family == nameof(ConvertFamily.Image);
        if (image && target.Extension is ".png" or ".jpg" or ".bmp" or ".gif" or ".tif" or ".ico") { ConvertImage(source, output, target.Extension); return output; }
        if (image && target.Extension == ".pdf") { ImageToPdf(source, output); return output; }
        if (family == nameof(ConvertFamily.Pdf) && target.Extension is ".png" or ".jpg" or ".bmp" or ".tif")
        {
            progress?.Invoke(Loc.T("正在渲染页面", "Rendering the pages"));
            var pages = await PdfRaster.ToImagesAsync(source, output, target.Extension, token);
            return pages.Count > 0 ? pages[0] : output;
        }
        if (target.Engine == ConvertEngine.Ffmpeg)
        {
            // The program's own copy first: an installed one is used only when this build carries none.
            string tool = Ffmpeg ?? BundledTools.Ffmpeg
                ?? throw new InvalidOperationException(Loc.T("无法解开内置的转换组件，请检查数据目录是否可写", "The built-in converter could not be unpacked; check that the data folder is writable"));
            progress?.Invoke(Loc.T("正在转换", "Converting"));
            await RunAsync(tool, ["-hide_banner", "-loglevel", "error", "-y", "-i", source, .. FfmpegOptions(target.Extension), output], token);
            return output;
        }
        if (target.Engine == ConvertEngine.Office)
        {
            if (Office is null) throw new InvalidOperationException(Loc.T("未找到 LibreOffice", "LibreOffice was not found"));
            await RunOfficeAsync(source, output, target.Extension, token);
            return output;
        }
        if (target.Extension == ".pdf")
        {
            // Pages drawn here, except for the old binary Office formats, which only an office suite can open.
            if (ConvertFormats.Extension(source) is ".doc" or ".xls")
            {
                if (Office is null) throw new InvalidOperationException(Loc.T("旧版 Office 格式请先另存为 .docx / .xlsx", "Save the old Office file as .docx or .xlsx first"));
                await RunOfficeAsync(source, output, ".pdf", token);
                return output;
            }
            progress?.Invoke(Loc.T("正在排版", "Laying out the pages"));
            return family switch
            {
                nameof(ConvertFamily.Document) => RenderPdf.FromDocx(source, output, token),
                nameof(ConvertFamily.Sheet) => RenderPdf.FromSheet(source, output, token),
                _ => RenderPdf.FromText(ExtractText(source), output, token)
            };
        }
        progress?.Invoke(Loc.T("正在转换", "Converting"));
        string text = ExtractText(source);
        WriteText(output, target.Extension, text, source);
        return output;
    }

    private static string[] FfmpegOptions(string extension) => extension switch
    {
        ".mp4" => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
        ".mkv" => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "aac"],
        ".webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus"],
        ".mov" => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-c:a", "aac"],
        ".avi" => ["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame"],
        ".gif" => ["-vf", "fps=12,scale=480:-1:flags=lanczos", "-loop", "0"],
        ".mp3" => ["-vn", "-c:a", "libmp3lame", "-b:a", "192k"],
        ".wav" => ["-vn", "-c:a", "pcm_s16le"],
        ".flac" => ["-vn", "-c:a", "flac", "-compression_level", "5"],
        ".m4a" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
        ".aac" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
        ".ogg" => ["-vn", "-c:a", "libvorbis", "-q:a", "5"],
        ".webp" => ["-c:v", "libwebp", "-quality", "90"],
        _ => ["-q:v", "2"]
    };

    // LibreOffice converts in a process of its own, headless; it names the result after the source, so the file is moved to
    // the name chosen here.
    private async Task RunOfficeAsync(string source, string output, string extension, CancellationToken token)
    {
        string filter = extension switch
        {
            ".pdf" => "pdf", ".docx" => "docx:MS Word 2007 XML", ".xlsx" => "xlsx:Calc MS Excel 2007 XML", _ => extension.TrimStart('.')
        };
        await RunAsync(Office!, ["--headless", "--norestore", "--convert-to", filter, "--outdir", Path.GetDirectoryName(output)!, source], token);
        string produced = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(source) + extension);
        if (produced != output && File.Exists(produced)) File.Move(produced, output, true);
        if (!File.Exists(output)) throw new IOException(Loc.T("转换没有生成文件", "The conversion produced no file"));
    }

    private static async Task RunAsync(string tool, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the tool could not start");
        string error = await process.StandardError.ReadToEndAsync(token);
        string output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim().Split('\n').Last().Trim());
    }

    // ---- pictures ---------------------------------------------------------------------------------------------------

    private static void ConvertImage(string source, string output, string extension)
    {
        using var input = File.OpenRead(source);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapEncoder encoder = extension switch
        {
            ".jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        // A JPEG has no transparency: the picture is laid on white first, so it does not turn black.
        BitmapSource image = encoder is JpegBitmapEncoder && frame.Format != System.Windows.Media.PixelFormats.Bgr24
            ? Flatten(frame) : frame;
        encoder.Frames.Add(BitmapFrame.Create(image));
        if (extension == ".ico") { WriteIcon(image, output); return; }
        using var target = File.Create(output);
        encoder.Save(target);
    }

    internal static BitmapSource Flatten(BitmapSource frame)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            context.DrawImage(frame, new System.Windows.Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
        }
        var rendered = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var converted = new FormatConvertedBitmap(rendered, System.Windows.Media.PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    // An .ico holds one PNG image, with the size in its directory entry (0 stands for 256).
    private static void WriteIcon(BitmapSource image, string output)
    {
        var png = new MemoryStream();
        new PngBitmapEncoder { Frames = { BitmapFrame.Create(image) } }.Save(png);
        var bytes = png.ToArray();
        using var file = File.Create(output);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)(image.PixelWidth >= 256 ? 0 : image.PixelWidth));
        writer.Write((byte)(image.PixelHeight >= 256 ? 0 : image.PixelHeight));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(bytes.Length); writer.Write(22);
        writer.Write(bytes);
    }

    // A single-page PDF holding the picture: no tool needed, and the picture keeps its JPEG quality.
    private static void ImageToPdf(string source, string output)
    {
        using var input = File.OpenRead(source);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var jpeg = new MemoryStream();
        new JpegBitmapEncoder { QualityLevel = 92, Frames = { BitmapFrame.Create(Flatten(frame)) } }.Save(jpeg);
        var data = jpeg.ToArray();
        double width = frame.PixelWidth, height = frame.PixelHeight;
        // The page keeps the picture's proportions at 72 points per inch of its own pixels, in portrait or landscape.
        double pageWidth = width > height ? 842 : 595, pageHeight = width > height ? 595 : 842;
        double scale = Math.Min(pageWidth / width, pageHeight / height);
        double drawWidth = width * scale, drawHeight = height * scale;
        double x = (pageWidth - drawWidth) / 2, y = (pageHeight - drawHeight) / 2;
        var objects = new List<string>();
        var content = $"q {drawWidth:0.##} 0 0 {drawHeight:0.##} {x:0.##} {y:0.##} cm /Im0 Do Q";
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:0.##} {pageHeight:0.##}] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>");
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var file = File.Create(output);
        using var writer = new BinaryWriter(file, Encoding.ASCII);
        var offsets = new List<long>();
        void Line(string text) => writer.Write(Encoding.ASCII.GetBytes(text + "\n"));
        Line("%PDF-1.4");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Line($"{i + 1} 0 obj"); Line(objects[i]); Line("endobj");
        }
        // The picture is the fifth object; its stream is binary, so it is written by hand.
        offsets.Add(file.Position);
        Line("5 0 obj");
        Line($"<< /Type /XObject /Subtype /Image /Width {frame.PixelWidth} /Height {frame.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {data.Length} >>");
        Line("stream"); writer.Write(data); writer.Flush(); Line(""); Line("endstream"); Line("endobj");
        long xref = file.Position;
        Line("xref"); Line($"0 {offsets.Count + 1}"); Line("0000000000 65535 f ");
        foreach (var offset in offsets) Line($"{offset:0000000000} 00000 n ");
        Line("trailer"); Line($"<< /Size {offsets.Count + 1} /Root 1 0 R >>"); Line("startxref"); Line(xref.ToString(CultureInfo.InvariantCulture)); Line("%%EOF");
    }

    // ---- text and documents ------------------------------------------------------------------------------------------

    // Everything a file says in plain text: documents, spreadsheets, slides, markup, data.
    public static string ExtractText(string path) => ConvertFormats.FamilyOf(path) switch
    {
        ConvertFamily.Document => ConvertFormats.Extension(path) switch
        {
            ".docx" => Docx(path), ".txt" => File.ReadAllText(path),
            ".html" or ".htm" => HtmlToText(File.ReadAllText(path)),
            _ => throw new InvalidOperationException(Loc.T("这个文档格式需要 LibreOffice", "This document format needs LibreOffice"))
        },
        ConvertFamily.Sheet => ConvertFormats.Extension(path) switch
        {
            ".xlsx" => XlsxToCsv(path), ".csv" => File.ReadAllText(path),
            _ => throw new InvalidOperationException(Loc.T("这个表格格式需要 LibreOffice", "This spreadsheet format needs LibreOffice"))
        },
        ConvertFamily.Text => ConvertFormats.Extension(path) switch
        {
            ".html" or ".htm" => HtmlToText(File.ReadAllText(path)), _ => File.ReadAllText(path)
        },
        ConvertFamily.Pdf => throw new InvalidOperationException(Loc.T("PDF 的文字需要 LibreOffice 提取", "Reading a PDF's text needs LibreOffice")),
        _ => throw new InvalidOperationException(Loc.T("这个文件没有文字可以提取", "This file holds no text"))
    };

    private static void WriteText(string output, string extension, string text, string source)
    {
        switch (extension)
        {
            case ".txt": File.WriteAllText(output, text, Utf8); break;
            case ".md": File.WriteAllText(output, ToMarkdown(text, source), Utf8); break;
            case ".html": File.WriteAllText(output, ToHtml(ToMarkdown(text, source), Path.GetFileNameWithoutExtension(source)), Utf8); break;
            case ".json":
                // A table becomes a list of rows keyed by the first row; anything else a list of its lines.
                File.WriteAllText(output, ToJson(text), Utf8); break;
            case ".csv": File.WriteAllText(output, ToCsv(text), Utf8); break;
            case ".tsv": File.WriteAllText(output, ToCsv(text).Replace(',', '\t'), Utf8); break;
            default: File.WriteAllText(output, text, Utf8); break;
        }
    }

    // A paragraph of a document: its runs carry the weight, the slant and the size each piece of text is written in.
    public readonly record struct Block(bool Heading, List<(string Text, bool Bold, bool Italic, double Size)> Runs);

    // A .docx as paragraphs of runs, which the PDF writer lays out; w:pPr/w:pStyle names a heading.
    public static List<Block> DocxBlocks(string path)
    {
        // The other word-processor formats open here too, so a document becomes a PDF without any office suite.
        switch (ConvertFormats.Extension(path))
        {
            case ".doc": throw new InvalidOperationException(Loc.T("旧版 .doc 请先用 Word 另存为 .docx", "Save an old .doc as .docx in Word first"));
            case ".rtf": return RtfBlocks(File.ReadAllText(path));
            case ".odt": return OdtBlocks(path);
        }
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException(Loc.T("这不是有效的 Word 文件", "This is not a valid Word file"));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = entry.Open();
        var blocks = new List<Block>();
        foreach (var paragraph in XDocument.Load(stream).Descendants(w + "p"))
        {
            string style = paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
            bool heading = style.Contains("Heading", StringComparison.OrdinalIgnoreCase) || style.Contains("标题") || style is "Title" or "Subtitle";
            var runs = new List<(string, bool, bool, double)>();
            foreach (var run in paragraph.Descendants(w + "r"))
            {
                string text = string.Concat(run.Elements().Where(node => node.Name == w + "t" || node.Name == w + "tab" || node.Name == w + "br")
                    .Select(node => node.Name == w + "t" ? node.Value : node.Name == w + "tab" ? "\t" : "\n"));
                if (text.Length == 0) continue;
                var properties = run.Element(w + "rPr");
                bool bold = properties?.Element(w + "b") is { } b && b.Attribute(w + "val")?.Value is not "0" and not "false";
                bool italic = properties?.Element(w + "i") is { } i && i.Attribute(w + "val")?.Value is not "0" and not "false";
                double size = double.TryParse(properties?.Element(w + "sz")?.Attribute(w + "val")?.Value, out double half) ? half / 2.0 : 0;
                runs.Add((text, bold, italic, size));
            }
            blocks.Add(new Block(heading, runs));
        }
        return blocks;
    }

    // An .rtf as paragraphs: the control words are read off and the text kept, with \par and \line starting a new one.
    private static List<Block> RtfBlocks(string rtf)
    {
        var blocks = new List<Block>();
        var builder = new StringBuilder();
        var runs = new List<(string, bool, bool, double)>();
        void Finish()
        {
            if (builder.Length > 0) runs.Add((builder.ToString(), false, false, 0));
            builder.Clear();
            if (runs.Count > 0) blocks.Add(new Block(false, runs));
            runs = [];
        }
        for (int index = 0; index < rtf.Length; index++)
        {
            char character = rtf[index];
            if (character == '\\')
            {
                int start = index + 1;
                if (start >= rtf.Length) break;
                if (rtf[start] is '\\' or '{' or '}') { builder.Append(rtf[start]); index = start; continue; }
                // A control word: a name, an optional number, and one space that belongs to it.
                int end = start;
                while (end < rtf.Length && char.IsLetter(rtf[end])) end++;
                string word = rtf[start..end];
                while (end < rtf.Length && (char.IsDigit(rtf[end]) || rtf[end] == '-')) end++;
                index = end;
                if (word is "par" or "line") { runs.Add((builder.ToString(), false, false, 0)); builder.Clear(); }
                else if (word == "tab") builder.Append('\t');
                else if (word == "u" && end < rtf.Length)
                {
                    // \uN: a character by code, followed by a stand-in character that is skipped.
                    int stop = end + 1;
                    if (stop < rtf.Length && rtf[stop] == ' ') stop++;
                    if (int.TryParse(rtf[end..stop].Trim(), out int code)) builder.Append((char)code);
                    index = stop;
                }
                else if (end < rtf.Length && rtf[end] == ' ') index = end;
                continue;
            }
            if (character is '{' or '}' or '\r' or '\n') continue;
            builder.Append(character);
        }
        Finish();
        return blocks;
    }

    // An .odt as paragraphs: content.xml holds them as text:p, with text:s standing for a space.
    private static List<Block> OdtBlocks(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml")
            ?? throw new InvalidOperationException(Loc.T("这不是有效的 OpenDocument 文件", "This is not a valid OpenDocument file"));
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        using var stream = entry.Open();
        var blocks = new List<Block>();
        foreach (var paragraph in XDocument.Load(stream).Descendants(text + "p"))
        {
            string value = string.Concat(paragraph.Descendants(text + "s").Select(_ => " "))
                + string.Concat(paragraph.DescendantNodes().OfType<XText>().Select(node => node.Value));
            if (value.Length == 0) { blocks.Add(new Block(false, [])); continue; }
            string style = paragraph.Attribute(text + "style-name")?.Value ?? "";
            bool heading = style.Contains("Heading", StringComparison.OrdinalIgnoreCase) || style.StartsWith("Title", StringComparison.OrdinalIgnoreCase);
            blocks.Add(new Block(heading, [(value, false, false, 0)]));
        }
        return blocks;
    }

    // The rows of a spreadsheet or of a comma-separated file, cell by cell, for the PDF writer's grid.
    public static List<List<string>> SheetRows(string path)
    {
        string extension = ConvertFormats.Extension(path);
        if (extension is ".csv" or ".tsv")
        {
            char separator = extension == ".tsv" ? '\t' : ',';
            var rows = new List<List<string>>();
            var cell = new StringBuilder();
            var row = new List<string>();
            bool quoted = false;
            string text = File.ReadAllText(path);
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (quoted)
                {
                    if (character == '"' && index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; }
                    else if (character == '"') quoted = false;
                    else cell.Append(character);
                    continue;
                }
                if (character == '"') { quoted = true; continue; }
                if (character == separator) { row.Add(cell.ToString()); cell.Clear(); continue; }
                if (character is '\n' or '\r')
                {
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = [];
                    continue;
                }
                cell.Append(character);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
        return XlsxRows(path);
    }

    public static List<List<string>> XlsxRows(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var stream = sharedEntry.Open();
            foreach (var item in XDocument.Load(stream).Descendants(s + "si"))
                shared.Add(string.Concat(item.Descendants(s + "t").Select(text => text.Value)));
        }
        var sheet = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName).FirstOrDefault()
            ?? throw new InvalidOperationException(Loc.T("这个表格没有工作表", "This spreadsheet holds no sheet"));
        using var sheetStream = sheet.Open();
        var rows = new List<List<string>>();
        foreach (var row in XDocument.Load(sheetStream).Descendants(s + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            int next = 0;
            foreach (var cell in row.Elements(s + "c"))
            {
                int column = Column(cell.Attribute("r")?.Value) ?? next;
                next = column + 1;
                string type = cell.Attribute("t")?.Value ?? "n";
                cells[column] = type switch
                {
                    "s" => int.TryParse(cell.Element(s + "v")?.Value, out int index) && index < shared.Count ? shared[index] : "",
                    "inlineStr" => string.Concat(cell.Descendants(s + "t").Select(text => text.Value)),
                    _ => cell.Element(s + "v")?.Value ?? ""
                };
            }
            var values = new List<string>();
            for (int i = 0; i < (cells.Count == 0 ? 0 : cells.Keys.Max() + 1); i++) values.Add(cells.TryGetValue(i, out var value) ? value : "");
            rows.Add(values);
        }
        return rows;
    }

    // The paragraphs of a .docx: word/document.xml holds them as w:p, with w:tab and w:br inside.
    public static string Docx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException(Loc.T("这不是有效的 Word 文件", "This is not a valid Word file"));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            var text = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == w + "t") text.Append(node.Value);
                else if (node.Name == w + "tab") text.Append('\t');
                else if (node.Name == w + "br") text.Append('\n');
            }
            builder.AppendLine(text.ToString());
        }
        return builder.ToString().TrimEnd() + "\n";
    }

    // The first sheet of a .xlsx as comma-separated values: shared strings and inline strings both read.
    public static string XlsxToCsv(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var stream = sharedEntry.Open();
            foreach (var item in XDocument.Load(stream).Descendants(s + "si"))
                shared.Add(string.Concat(item.Descendants(s + "t").Select(text => text.Value)));
        }
        var sheet = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName).FirstOrDefault()
            ?? throw new InvalidOperationException(Loc.T("这个表格没有工作表", "This spreadsheet holds no sheet"));
        using var sheetStream = sheet.Open();
        var rows = new List<string>();
        foreach (var row in XDocument.Load(sheetStream).Descendants(s + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            int next = 0;
            foreach (var cell in row.Elements(s + "c"))
            {
                int column = Column(cell.Attribute("r")?.Value) ?? next;
                next = column + 1;
                string type = cell.Attribute("t")?.Value ?? "n";
                string value = type switch
                {
                    "s" => int.TryParse(cell.Element(s + "v")?.Value, out int index) && index < shared.Count ? shared[index] : "",
                    "inlineStr" => string.Concat(cell.Descendants(s + "t").Select(text => text.Value)),
                    _ => cell.Element(s + "v")?.Value ?? ""
                };
                cells[column] = value;
            }
            if (cells.Count == 0) { rows.Add(""); continue; }
            var line = new List<string>();
            for (int i = 0; i <= cells.Keys.Max(); i++) line.Add(cells.TryGetValue(i, out var value) ? Escape(value) : "");
            rows.Add(string.Join(",", line));
        }
        return string.Join("\n", rows) + "\n";
    }

    // "BC12" → 54 (the column's place, from zero).
    private static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int column = 0, letters = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character)) break;
            column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1); letters++;
        }
        return letters == 0 ? null : column - 1;
    }

    private static string Escape(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // A page's words without its markup: tags dropped, entities decoded, lines kept.
    public static string HtmlToText(string html)
    {
        html = System.Text.RegularExpressions.Regex.Replace(html, "(?is)<(script|style|head).*?</\\1>", " ");
        html = System.Text.RegularExpressions.Regex.Replace(html, "(?i)<br\\s*/?>|</p>|</div>|</li>|</h[1-6]>", "\n");
        html = System.Text.RegularExpressions.Regex.Replace(html, "(?s)<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = System.Text.RegularExpressions.Regex.Replace(html, "[ \\t\\x0B\\f\\r]+", " ");
        html = System.Text.RegularExpressions.Regex.Replace(html, "\\n\\s*\\n+", "\n\n");
        return html.Trim() + "\n";
    }

    // Markdown from plain text: headings where a line is short and followed by a blank one, paragraphs otherwise.
    private static string ToMarkdown(string text, string source)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        var builder = new StringBuilder($"# {name}\n\n");
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()).ToList();
            if (lines.Count == 0) continue;
            // A table (commas in every line) becomes a Markdown table.
            if (lines.Count > 1 && lines.All(line => line.Contains(',')))
            {
                var cells = lines.Select(line => line.Split(',')).ToList();
                int columns = cells.Max(row => row.Length);
                for (int i = 0; i < cells.Count; i++)
                {
                    builder.AppendLine("| " + string.Join(" | ", cells[i].Concat(new string[columns - cells[i].Length])) + " |");
                    if (i == 0) builder.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |");
                }
                builder.AppendLine();
                continue;
            }
            if (lines.Count == 1 && lines[0].Length is > 0 and <= 60) builder.AppendLine("## " + lines[0]).AppendLine();
            else builder.AppendLine(string.Join("  \n", lines)).AppendLine();
        }
        return builder.ToString().TrimEnd() + "\n";
    }

    // A Markdown file as a small, self-contained page: headings, lists, code, quotes and paragraphs.
    public static string ToHtml(string markdown, string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
        builder.AppendLine("<style>body{max-width:820px;margin:40px auto;padding:0 20px;font:16px/1.7 -apple-system,'Segoe UI','Microsoft YaHei',sans-serif;color:#1c1c1e}"
            + "h1,h2,h3{line-height:1.3;margin:1.6em 0 .6em}pre{background:#f5f5f7;padding:12px 14px;border-radius:8px;overflow:auto}"
            + "code{background:#f5f5f7;padding:2px 5px;border-radius:4px;font-size:.92em}blockquote{margin:1em 0;padding:.2em 1em;border-left:3px solid #d0d0d5;color:#555}"
            + "table{border-collapse:collapse}td,th{border:1px solid #ddd;padding:6px 10px}ul,ol{padding-left:1.5em}</style></head><body>");
        bool code = false, list = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                builder.AppendLine(code ? "</code></pre>" : "<pre><code>");
                code = !code; continue;
            }
            if (code) { builder.AppendLine(System.Net.WebUtility.HtmlEncode(raw)); continue; }
            if (line.Length == 0) { if (list) { builder.AppendLine("</ul>"); list = false; } continue; }
            int level = line.TakeWhile(character => character == '#').Count();
            if (level is > 0 and <= 6 && line.Length > level && line[level] == ' ')
            {
                builder.AppendLine($"<h{level}>{Inline(line[(level + 1)..])}</h{level}>"); continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!list) { builder.AppendLine("<ul>"); list = true; }
                builder.AppendLine("<li>" + Inline(line[2..]) + "</li>"); continue;
            }
            if (list) { builder.AppendLine("</ul>"); list = false; }
            if (line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith('|'))
            {
                // A table is written as its rows; the separator row is dropped.
                if (line.Contains("---")) continue;
                var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
                builder.AppendLine("<table><tr>" + string.Concat(cells.Select(cell => $"<td>{Inline(cell)}</td>")) + "</tr></table>");
                continue;
            }
            if (line.StartsWith('>')) { builder.AppendLine("<blockquote>" + Inline(line.TrimStart('>', ' ')) + "</blockquote>"); continue; }
            builder.AppendLine("<p>" + Inline(line) + "</p>");
        }
        if (list) builder.AppendLine("</ul>");
        if (code) builder.AppendLine("</code></pre>");
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    // Bold, italics, inline code and links inside one line.
    private static string Inline(string text)
    {
        string encoded = System.Net.WebUtility.HtmlEncode(text);
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, "`([^`]+)`", "<code>$1</code>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, "\\*\\*([^*]+)\\*\\*", "<strong>$1</strong>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, "\\[([^\\]]+)\\]\\(([^)]+)\\)", "<a href=\"$2\">$1</a>");
        return encoded;
    }

    // Rows of a table as JSON objects, or the lines as a list when the text is not a table.
    private static string ToJson(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2 && lines.All(line => line.Contains(',')))
        {
            var header = lines[0].Split(',').Select(cell => cell.Trim().Trim('"')).ToArray();
            var rows = new List<Dictionary<string, string>>();
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                var row = new Dictionary<string, string>();
                for (int i = 0; i < header.Length; i++) row[header[i]] = i < cells.Length ? cells[i].Trim().Trim('"') : "";
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        }
        return JsonSerializer.Serialize(lines, new JsonSerializerOptions { WriteIndented = true });
    }

    // The rows of a JSON list of objects as comma-separated values; anything else is written out line by line.
    private static string ToCsv(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array
                    && document.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
                {
                    var columns = first.EnumerateObject().Select(property => property.Name).ToList();
                    var builder = new StringBuilder();
                    builder.AppendLine(string.Join(",", columns.Select(Escape)));
                    foreach (var row in document.RootElement.EnumerateArray())
                    {
                        builder.AppendLine(string.Join(",", columns.Select(column =>
                            Escape(row.TryGetProperty(column, out var value) ? value.ToString() : ""))));
                    }
                    return builder.ToString();
                }
            }
            catch (JsonException) { }
        }
        return text;
    }
}
