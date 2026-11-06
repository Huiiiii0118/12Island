namespace TwelveIsland.Core;

// What a file is, for the conversion page: which family it belongs to and what it may become.
public enum ConvertFamily { Image, Document, Sheet, Text, Audio, Video, Pdf }

// Who does the work: the panel itself, FFmpeg for sound and video, or an office suite (LibreOffice/Office) for documents.
public enum ConvertEngine { Builtin, Ffmpeg, Office }

public sealed record ConvertTarget(string Extension, ConvertEngine Engine);

// The formats the conversion page knows, and what each one may become. A family converts within itself, documents, sheets and
// text become PDF as well: all of it is done on this machine, by the panel itself or by the FFmpeg it carries. An office suite,
// when one is installed, only adds the formats the panel cannot read on its own (.doc, .xls) and PDF to Word.
public static class ConvertFormats
{
    // extension → family, lower case with the dot.
    private static readonly Dictionary<string, ConvertFamily> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ConvertFamily.Image, [".jpg"] = ConvertFamily.Image, [".jpeg"] = ConvertFamily.Image, [".bmp"] = ConvertFamily.Image,
        [".gif"] = ConvertFamily.Image, [".tif"] = ConvertFamily.Image, [".tiff"] = ConvertFamily.Image, [".ico"] = ConvertFamily.Image,
        [".webp"] = ConvertFamily.Image, [".heic"] = ConvertFamily.Image,
        [".docx"] = ConvertFamily.Document, [".doc"] = ConvertFamily.Document, [".rtf"] = ConvertFamily.Document,
        [".odt"] = ConvertFamily.Document, [".txt"] = ConvertFamily.Text, [".md"] = ConvertFamily.Text, [".markdown"] = ConvertFamily.Text,
        [".html"] = ConvertFamily.Text, [".htm"] = ConvertFamily.Text, [".json"] = ConvertFamily.Text, [".xml"] = ConvertFamily.Text,
        [".xlsx"] = ConvertFamily.Sheet, [".xls"] = ConvertFamily.Sheet, [".csv"] = ConvertFamily.Sheet, [".tsv"] = ConvertFamily.Sheet,
        [".pdf"] = ConvertFamily.Pdf,
        [".mp3"] = ConvertFamily.Audio, [".wav"] = ConvertFamily.Audio, [".flac"] = ConvertFamily.Audio, [".aac"] = ConvertFamily.Audio,
        [".m4a"] = ConvertFamily.Audio, [".ogg"] = ConvertFamily.Audio, [".opus"] = ConvertFamily.Audio, [".wma"] = ConvertFamily.Audio,
        [".mp4"] = ConvertFamily.Video, [".mkv"] = ConvertFamily.Video, [".mov"] = ConvertFamily.Video, [".avi"] = ConvertFamily.Video,
        [".webm"] = ConvertFamily.Video, [".flv"] = ConvertFamily.Video, [".m4v"] = ConvertFamily.Video, [".ts"] = ConvertFamily.Video
    };

    // The formats each family may become, with who converts them.
    private static readonly Dictionary<ConvertFamily, IReadOnlyList<(string Extension, ConvertEngine Engine)>> Targets = new()
    {
        [ConvertFamily.Image] = [(".png", ConvertEngine.Builtin), (".jpg", ConvertEngine.Builtin), (".bmp", ConvertEngine.Builtin),
            (".gif", ConvertEngine.Builtin), (".tif", ConvertEngine.Builtin), (".ico", ConvertEngine.Builtin), (".webp", ConvertEngine.Ffmpeg)],
        [ConvertFamily.Document] = [(".docx", ConvertEngine.Office), (".pdf", ConvertEngine.Builtin), (".txt", ConvertEngine.Builtin),
            (".md", ConvertEngine.Builtin), (".html", ConvertEngine.Builtin)],
        [ConvertFamily.Sheet] = [(".csv", ConvertEngine.Builtin), (".tsv", ConvertEngine.Builtin), (".json", ConvertEngine.Builtin),
            (".xlsx", ConvertEngine.Office), (".pdf", ConvertEngine.Builtin)],
        [ConvertFamily.Text] = [(".txt", ConvertEngine.Builtin), (".md", ConvertEngine.Builtin), (".html", ConvertEngine.Builtin),
            (".json", ConvertEngine.Builtin), (".csv", ConvertEngine.Builtin), (".pdf", ConvertEngine.Builtin)],
        [ConvertFamily.Audio] = [(".mp3", ConvertEngine.Ffmpeg), (".wav", ConvertEngine.Ffmpeg), (".flac", ConvertEngine.Ffmpeg),
            (".m4a", ConvertEngine.Ffmpeg), (".ogg", ConvertEngine.Ffmpeg), (".aac", ConvertEngine.Ffmpeg)],
        [ConvertFamily.Video] = [(".mp4", ConvertEngine.Ffmpeg), (".mkv", ConvertEngine.Ffmpeg), (".webm", ConvertEngine.Ffmpeg),
            (".mov", ConvertEngine.Ffmpeg), (".avi", ConvertEngine.Ffmpeg), (".gif", ConvertEngine.Ffmpeg), (".mp3", ConvertEngine.Ffmpeg)],
        [ConvertFamily.Pdf] = [(".png", ConvertEngine.Builtin), (".jpg", ConvertEngine.Builtin), (".bmp", ConvertEngine.Builtin),
            (".tif", ConvertEngine.Builtin), (".docx", ConvertEngine.Office)]
    };

    public static string Extension(string path) => Path.GetExtension(path).ToLowerInvariant();
    public static ConvertFamily? FamilyOf(string path) => Families.TryGetValue(Extension(path), out var family) ? family : null;
    public static bool Known(string path) => FamilyOf(path) is not null;
    public static string FamilyName(ConvertFamily family) => family switch
    {
        ConvertFamily.Image => "图片", ConvertFamily.Document => "文档", ConvertFamily.Sheet => "表格", ConvertFamily.Text => "文本",
        ConvertFamily.Audio => "音频", ConvertFamily.Video => "视频", _ => "PDF"
    };
    public static string FamilyNameOf(string path) => FamilyOf(path) is { } family ? Loc.T(FamilyName(family), family.ToString()) : Loc.T("未知", "Unknown");

    // The targets a source may become now, in the order they are shown. A target that needs a tool the machine does not have
    // is left out rather than offered and then failing.
    public static IReadOnlyList<ConvertTarget> TargetsFor(string path, bool ffmpeg, bool office)
    {
        if (FamilyOf(path) is not { } family) return [];
        string source = Extension(path);
        var list = new List<ConvertTarget>();
        foreach (var (extension, engine) in Targets[family])
        {
            if (extension == source) continue;
            if (engine == ConvertEngine.Ffmpeg && !ffmpeg) continue;
            if (engine == ConvertEngine.Office && !office) continue;
            list.Add(new ConvertTarget(extension, engine));
        }
        // A picture also becomes a PDF without any tool, through the print path every Windows machine has.
        if (family == ConvertFamily.Image && !list.Any(target => target.Extension == ".pdf"))
            list.Add(new ConvertTarget(".pdf", ConvertEngine.Builtin));
        return list;
    }

    // Whether a target for this source needs FFmpeg or an office suite (so the page can say what is missing).
    public static (bool NeedsFfmpeg, bool NeedsOffice) Missing(string path)
    {
        if (FamilyOf(path) is not { } family) return (false, false);
        var targets = Targets[family];
        return (targets.Any(target => target.Engine == ConvertEngine.Ffmpeg), targets.Any(target => target.Engine == ConvertEngine.Office));
    }

    public static string Filter => "所有支持的文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp;*.docx;*.doc;*.rtf;*.odt;*.pdf;*.xlsx;*.xls;*.csv;*.tsv;*.txt;*.md;*.html;*.htm;*.json;*.xml;*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.m4v;*.ts|所有文件|*.*";
}
