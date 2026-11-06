using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace TwelveIsland;

// The tools that travel inside the program. FFmpeg is carried as a compressed archive and unpacked once into the data folder:
// a machine that never had it installed converts sound and video all the same, with no download and no setup.
internal static class BundledTools
{
    private static readonly object Gate = new();
    private static string? _ffmpeg;
    private static bool _unpacked;

    // Whether the program carries its own copy: known without unpacking it, so the page can offer the formats at once.
    public static bool HasFfmpeg => typeof(BundledTools).Assembly.GetManifestResourceInfo("12Island.Assets.ffmpeg.tgz") is not null;

    // The unpacked ffmpeg.exe, or null when it could not be written (a locked or read-only data folder).
    public static string? Ffmpeg
    {
        get
        {
            lock (Gate)
            {
                if (_unpacked) return _ffmpeg;
                _unpacked = true;
                return _ffmpeg = Unpack();
            }
        }
    }

    private static string? Unpack()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island", "tools");
        string executable = Path.Combine(folder, "ffmpeg.exe");
        try
        {
            // A file already there is this program's own copy; a cut-off one is caught by its size and written again.
            if (new FileInfo(executable) is { Exists: true, Length: > 1_000_000 }) return executable;
            Directory.CreateDirectory(folder);
            using var archive = typeof(BundledTools).Assembly.GetManifestResourceStream("12Island.Assets.ffmpeg.tgz");
            if (archive is null) return null;
            using var compressed = new GZipStream(archive, CompressionMode.Decompress);
            using var reader = new TarReader(compressed);
            while (reader.GetNextEntry() is { } entry)
            {
                if (!entry.Name.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || entry.DataStream is null) continue;
                string temporary = executable + ".tmp";
                using (var file = File.Create(temporary)) entry.DataStream.CopyTo(file);
                File.Move(temporary, executable, true);
                return executable;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { }
        return null;
    }
}
