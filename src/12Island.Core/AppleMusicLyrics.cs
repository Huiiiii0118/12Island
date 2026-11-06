using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TwelveIsland.Core;

// Lyrics for the Apple Music desktop app, from the lyrics it downloads to its web cache as each song starts.
public sealed partial class LyricsService
{
    public const string AppleSource = "Apple Music";
    // How long before the song's start its lyric file may have been written.
    private static readonly TimeSpan AppleCacheWindow = TimeSpan.FromSeconds(20);
    // A shorter pause between lines (most are under half a second, measured) keeps the line shown rather than a note.
    private const double AppleGap = 5;
    private const int AppleLyricsIdLimit = 1000;
    private readonly Func<IEnumerable<string>> _appleCacheRoots;
    // The lyric ID each song was matched to, latest match last, kept in _appleLyricsFile for songs played again after a restart.
    private readonly string? _appleLyricsFile;
    private readonly OrderedDictionary<string, string> _appleLyricsIds;
    private sealed record AppleCacheEntry(string LyricsId, Lyrics Lyrics, double? Length, double Offset);
    private readonly Dictionary<string, FileSystemWatcher> _appleWatchers = new();
    // Raised on a worker thread when Apple Music writes a lyric file, which may be late for the song playing.
    public event Action? AppleLyricsWritten;

    // Apple Music writes ttmlLyrics*.json as a song starts, whether or not its lyrics are shown (measured: mostly 1 to 2 seconds
    // before the song's audio, once some minutes into the song), but not when the song is played again (measured). The lyric ID
    // in the file does not always name the song (a reissued song keeps an older one), so a song played for the first time takes
    // the file written nearest its start whose TTML is as long as the song (measured within 0.05 seconds). Untimed lyrics give no
    // length (measured); their file is taken only when written after the song started, so that the file of a song skipped a
    // moment ago is not. Many songs share a length to the second, so a file written long before the song started is taken only
    // when the song was first seen midway and that file is the only one of its length.
    private async Task<Lyrics> FindApple(MediaTrack track, CancellationToken token)
    {
        if (track.Duration <= TimeSpan.Zero) return Lyrics.Empty;
        var now = DateTimeOffset.UtcNow;
        var position = track.Position(now);
        var started = (now - position).UtcDateTime;
        var entries = new List<AppleCacheEntry>();
        WatchAppleCache();
        foreach (var file in AppleCacheFiles())
        {
            try
            {
                if (await ReadAppleCacheFile(file.FullName, token) is not { } bytes) continue;
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Str(root, "status") != "success") continue;
                var (lyrics, length) = ReadAppleTtml(Str(root, "ttml"));
                // Seconds from the song's start to the file's creation or last write, whichever is nearer.
                double offset = new[] { file.CreationTimeUtc, file.LastWriteTimeUtc }.Select(t => (t - started).TotalSeconds).MinBy(Math.Abs);
                if (lyrics.Lines.Count > 0) entries.Add(new(Str(root, "lyricsId"), lyrics, length, offset));
            }
            // A file Apple Music has not finished writing.
            catch (Exception ex) when (ex is JsonException or XmlException) { }
        }
        double window = AppleCacheWindow.TotalSeconds;
        bool SameLength(AppleCacheEntry entry) => entry.Length is { } seconds && Math.Abs(seconds - track.Duration.TotalSeconds) <= 1;
        // A song played before has no new file; its lyrics are the ones it was matched to, while Apple Music keeps them.
        var found = (_appleLyricsIds.TryGetValue(track.SongKey, out var id) ? entries.Where(e => e.LyricsId == id && (e.Length is null || SameLength(e))).MinBy(e => Math.Abs(e.Offset)) : null)
            // The song's start is known to about a second.
            ?? entries.Where(e => e.Length is null ? e.Offset >= -3 : SameLength(e) && e.Offset >= -window).MinBy(e => Math.Abs(e.Offset));
        // Where the song was when its track was reported, not now: lookups repeated later in the song keep the start seen then.
        // Seen midway, as when TwelveIsland starts, the song may have started earlier than estimated after a pause. A file as long
        // as the song is then taken however long before, but not one matched to another song, and not when two such files
        // differ: the song played before keeps its own file in the cache beside the other song's of the same length.
        if (found is null && track.Position(track.PositionAt).TotalSeconds > window)
        {
            var earlier = entries.Where(e => SameLength(e) && e.Offset < -window && !AppleLyricsOfAnotherSong(e.LyricsId, track)).ToArray();
            if (earlier.Select(e => e.LyricsId).Distinct().Count() == 1) found = earlier.MinBy(e => Math.Abs(e.Offset));
        }
        if (found is null) return Lyrics.Empty;
        RememberAppleLyrics(track.SongKey, found.LyricsId);
        return found.Lyrics;
    }

    // Each cache folder is watched from the first Apple Music lookup until the service is disposed.
    private void WatchAppleCache()
    {
        foreach (string root in _appleCacheRoots())
        {
            if (_appleWatchers.ContainsKey(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root, "ttmlLyrics*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                watcher.Created += (_, _) => AppleLyricsWritten?.Invoke();
                watcher.Changed += (_, _) => AppleLyricsWritten?.Invoke();
                watcher.EnableRaisingEvents = true;
                _appleWatchers[root] = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
    }

    private void RememberAppleLyrics(string song, string lyricsId)
    {
        if (lyricsId.Length == 0 || _appleLyricsIds.TryGetValue(song, out var known) && known == lyricsId) return;
        _appleLyricsIds.Remove(song); _appleLyricsIds.Add(song, lyricsId);
        while (_appleLyricsIds.Count > AppleLyricsIdLimit) _appleLyricsIds.RemoveAt(0);
        if (_appleLyricsFile is null) return;
        // Written whole and then moved into place; a failed write keeps the previous file.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_appleLyricsFile)!);
            File.WriteAllBytes(_appleLyricsFile + ".tmp", JsonSerializer.SerializeToUtf8Bytes(_appleLyricsIds));
            File.Move(_appleLyricsFile + ".tmp", _appleLyricsFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // A reissue of the song keeps the same title; a song with another title is another song.
    private static string AppleSongTitle(string song) => song.Split('\n')[0];
    private bool AppleLyricsOfAnotherSong(string lyricsId, MediaTrack track)
        => _appleLyricsIds.Any(pair => pair.Value == lyricsId && AppleSongTitle(pair.Key) != track.Title);
    private static OrderedDictionary<string, string> LoadAppleLyricsIds(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<OrderedDictionary<string, string>>(File.ReadAllBytes(path)) is { } ids)
            {
                // Earlier versions could match a song to another song's file of the same length. Lyrics shared by songs of
                // different titles are dropped, so that those songs are matched again.
                foreach (var song in ids.GroupBy(pair => pair.Value).Where(group => group.Select(pair => AppleSongTitle(pair.Key)).Distinct().Count() > 1)
                    .SelectMany(group => group.Select(pair => pair.Key)).ToArray())
                    ids.Remove(song);
                return ids;
            }
        }
        // A damaged file is started over.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    // LRCLIB, QQ Music and NetEase in turn until one has synced lyrics; plain lyrics are used only when none has.
    private async Task<Lyrics> FindAppleFallback(MediaTrack track, Lyrics plain, CancellationToken token)
    {
        foreach (var find in new Func<MediaTrack, CancellationToken, Task<Lyrics>>[] { FindLrclib, FindQq, FindNetease })
        {
            var lyrics = await find(track, token);
            if (lyrics.Synced && lyrics.Lines.Count > 0) return lyrics;
            if (plain.Lines.Count == 0 && !plain.Instrumental && (lyrics.Lines.Count > 0 || lyrics.Instrumental)) plain = lyrics;
        }
        return plain;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
    private const uint SimplifiedChinese = 0x02000000;
    private static string SimplifyAppleText(string value)
    {
        if (value.Length == 0) return value;
        var result = new StringBuilder(value.Length + 1);
        return LCMapStringEx("zh-CN", SimplifiedChinese, value, -1, result, result.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0 ? result.ToString() : value;
    }

    private static IEnumerable<string> AppleCacheRoots()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return [];
        return Directory.GetDirectories(packages, "AppleInc.AppleMusicWin_*")
            .Select(p => Path.Combine(p, "AC", "INetCache")).Where(Directory.Exists).ToArray();
    }

    private FileInfo[] AppleCacheFiles()
    {
        // The cache's folders are hidden system folders, which enumeration skips by default.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return _appleCacheRoots().SelectMany(root => new DirectoryInfo(root).EnumerateFiles("ttmlLyrics*.json", options)).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    private static async Task<byte[]?> ReadAppleCacheFile(string path, CancellationToken token)
    {
        // Apple Music may be writing or removing the file.
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return await ReadLimitedAsync(stream, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static Lyrics ParseAppleTtml(string ttml) => ReadAppleTtml(ttml).Lyrics;
    // The lyrics, and the song's length from the body's dur.
    private static (Lyrics Lyrics, double? Length) ReadAppleTtml(string ttml)
    {
        using var reader = XmlReader.Create(new StringReader(ttml), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseBytes });
        var xml = XDocument.Load(reader);
        XNamespace ns = "http://www.w3.org/ns/ttml";
        var body = xml.Root?.Element(ns + "body");
        if (body is null) return (Lyrics.Empty, null);
        var length = AppleTime((string?)body.Attribute("dur"));
        string Text(XElement row) => string.Concat(row.DescendantNodes().Select(n => n is XText t ? t.Value :
            n is XElement e && e.Name == ns + "br" ? "\n" : "")).Trim();
        var rows = body.Descendants(ns + "p").Where(row => Text(row).Length > 0).ToArray();
        if (rows.Length == 0) return (Lyrics.Empty, length);
        double? Start(XElement row) => AppleTime((string?)row.Attribute("begin")) ??
            row.Descendants(ns + "span").Select(e => AppleTime((string?)e.Attribute("begin"))).FirstOrDefault(t => t.HasValue);
        // Lines without timing are shown as plain lyrics rather than given guessed times.
        if (rows.Any(row => Start(row) is null))
            return (new(rows.Select(row => new LyricLine(0, Text(row))).ToArray(), false, AppleSource), length);
        var timed = rows.Select(row => (Start: Start(row)!.Value, End: AppleTime((string?)row.Attribute("end")), Text: Text(row)))
            .OrderBy(row => row.Start).ToArray();
        var lines = new List<LyricLine>();
        for (int i = 0; i < timed.Length; i++)
        {
            var row = timed[i]; lines.Add(new(row.Start, row.Text));
            // A line that ends AppleGap or more before the next one starts leaves a gap, shown as a blank line.
            if (row.End is { } end && end > row.Start && (i == timed.Length - 1 || timed[i + 1].Start - end >= AppleGap))
                lines.Add(new(end, ""));
        }
        return (new(lines, true, AppleSource), length);
    }

    // TTML clock times: "1:02.5", "62.5s", "62500ms".
    private static double? AppleTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        double seconds = 0;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)) return null;
            seconds /= 1000;
        }
        else
        {
            if (value.EndsWith('s')) value = value[..^1];
            var parts = value.Split(':');
            if (parts.Length > 3) return null;
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return null;
                seconds = seconds * 60 + number;
            }
        }
        return seconds;
    }
}
