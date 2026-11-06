using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public sealed record LyricLine(double Seconds, string Text);
// The lines shown around the one being sung: Before ends with the line just sung, After starts with the next one.
public sealed record LyricWindow(IReadOnlyList<string> Before, string Current, IReadOnlyList<string> After)
{
    public static LyricWindow Empty { get; } = new([], "", []);
    public static LyricWindow Only(string current) => new([], current, []);
}
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool Synced, string Source, bool Instrumental = false)
{
    public static Lyrics Empty { get; } = new([], true, "");
    // Apple Music's lines are shown this many seconds before they are sung, so that a line can be read as it starts.
    public const double AppleLead = 0.2;
    public bool QqPreview { get; init; }
    public bool CanSynchronize(MediaTrack track) => Synced && track.HasTimeline;
    public (string Previous, string Current, string Next) At(MediaTrack track, DateTimeOffset now) => Nearest(Around(track, now, 1, 1));
    public (string Previous, string Current, string Next) At(double seconds) => Nearest(Around(seconds, 1, 1));
    private static (string Previous, string Current, string Next) Nearest(LyricWindow window)
        => (window.Before.Count > 0 ? window.Before[^1] : "", window.Current, window.After.Count > 0 ? window.After[0] : "");
    // The current line with up to before lines above it and after lines below it, in the order they are sung.
    public LyricWindow Around(MediaTrack track, DateTimeOffset now, int before, int after)
    {
        if (!CanSynchronize(track)) return LyricWindow.Empty;
        if (!QqPreview || !track.IsQqMusic) return Around(track.Position(now).TotalSeconds + (track.IsAppleMusic ? AppleLead : 0), before, after);
        // A preview can start at zero or inside the song. QQ's actual current line and
        // full-song karaoke timestamp locate it without guessing from the clip metadata.
        string text = track.CurrentLyric ?? "";
        string key = NormalizeLine(text);
        if (key.Length == 0) return LyricWindow.Only(text.Length > 0 ? text : track.CurrentLyric is null ? "" : "♪");
        var matches = Lines.Select((line, index) => (Line: line, Index: index))
            .Where(row => NormalizeLine(row.Line.Text) == key).ToArray();
        if (matches.Length == 1) return Around(matches[0].Line.Seconds, before, after);
        if (matches.Length > 1 && track.CurrentLyricStart is { } start)
        {
            var nearest = matches.OrderBy(row => Math.Abs(row.Line.Seconds - start.TotalSeconds)).First();
            if (Math.Abs(nearest.Line.Seconds - start.TotalSeconds) <= 2) return Around(nearest.Line.Seconds, before, after);
        }
        // An unmatched line or ambiguous repeated chorus still has QQ's single-line fallback.
        return LyricWindow.Only(text);
    }
    private static string NormalizeLine(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    public LyricWindow Around(double seconds, int before, int after)
    {
        before = Math.Max(0, before); after = Math.Max(0, after);
        if (Lines.Count == 0) return LyricWindow.Only(Instrumental ? "♪" : "");
        if (!Synced) return LyricWindow.Empty;
        int index = -1;
        for (int i = 0; i < Lines.Count && Lines[i].Seconds <= seconds; i++) index = i;
        string[] Texts(int from, int count) => [.. Lines.Skip(from).Take(count).Select(line => line.Text)];
        if (index < 0) return new([], "♪", Texts(0, after));
        // LRC timestamps start a line; they do not specify how long it is sung.
        // Only an explicit blank line marks a gap before the next timestamp.
        string text = string.IsNullOrWhiteSpace(Lines[index].Text) ? "♪" : Lines[index].Text;
        int first = Math.Max(0, index - before);
        return new(Texts(first, index - first), text, Texts(index + 1, after));
    }
    public static Lyrics Parse(string? lrc, string? plain, double duration, string source, bool instrumental = false)
    {
        var result = new List<LyricLine>();
        double offset = 0;
        var offsetMatch = Regex.Match(lrc ?? "", @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var ms)) offset = ms / 1000;
        foreach (var row in (lrc ?? "").Split('\n'))
        {
            var stamps = Regex.Matches(row, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0) continue;
            // Word-timed LRC puts a <mm:ss.xx> tag before each word; the panel shows whole lines, so the tags are dropped.
            string text = Regex.Replace(row[(stamps[^1].Index + stamps[^1].Length)..], @"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", "").Trim();
            foreach (Match stamp in stamps)
            {
                double time = int.Parse(stamp.Groups[1].Value) * 60 + int.Parse(stamp.Groups[2].Value);
                if (stamp.Groups[3].Success) time += double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new(Math.Max(0, time + offset), text));
            }
        }
        if (result.Count > 0)
            return InstrumentalNotice(result.Select(l => l.Text)) ? new([], true, source, true) : new(result.OrderBy(l => l.Seconds).Distinct().ToList(), true, source, instrumental);
        var lines = (plain ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return new([], true, source, instrumental);
        if (InstrumentalNotice(lines)) return new([], true, source, true);
        // Plain lyrics have no timing information; retain their order for manual scrolling.
        return new(lines.Select(line => new LyricLine(0, line)).ToArray(), false, source, instrumental);
    }
    // QQ Music, NetEase and KuGou give a song without words a lyric of only credits ("作曲 : Yiruma") and a notice such as
    // "纯音乐，请欣赏" or "此歌曲为没有填词的纯音乐，请您欣赏"; such a lyric is the song's mark as instrumental. A notice worded
    // otherwise is not recognised, and its lines are shown as a lyric.
    private static bool InstrumentalNotice(IEnumerable<string> lines)
    {
        var texts = lines.Select(text => text.Trim()).Where(text => text.Length > 0).ToArray();
        static bool Notice(string text) => text.Contains("纯音乐", StringComparison.Ordinal) && text.Contains("欣赏", StringComparison.Ordinal);
        return texts.Any(Notice) && texts.All(text => Notice(text) || Regex.IsMatch(text, @"\A[^:：]{1,16}[:：]"));
    }
}

public sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, Lyrics> _cache = new();
    private readonly LinkedList<string> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    // A lyric reply is a few kilobytes; a larger one is given up rather than read into memory.
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 2;
    // appleLyricsFile keeps which Apple Music lyrics each song was matched to; none keeps it only in memory.
    public LyricsService(HttpClient? http = null, Func<IEnumerable<string>>? appleCacheRoots = null, string? appleLyricsFile = null)
    {
        _appleCacheRoots = appleCacheRoots ?? AppleCacheRoots;
        _appleLyricsFile = appleLyricsFile; _appleLyricsIds = LoadAppleLyricsIds(appleLyricsFile);
        _ownsHttp = http is null;
        // Redirects are followed by Get, only to HTTPS on the same host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    public async Task<Lyrics> FindAsync(MediaTrack track, bool enabled, CancellationToken token)
    {
        if (!enabled || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return Lyrics.Empty;
        if (!track.IsQqMusic && !string.IsNullOrWhiteSpace(track.EmbeddedLyrics))
            return Lyrics.Parse(track.EmbeddedLyrics, null, track.Duration.TotalSeconds, track.Source);
        await _gate.WaitAsync(token);
        try
        {
            string cacheKey = string.Join("\n", track.Source, track.SongId, track.SongKey);
            // A third-party result for Apple Music is kept only until Apple Music caches its own lyrics.
            bool hit = _cache.TryGetValue(cacheKey, out var cached);
            if (hit && (!track.IsAppleMusic || cached!.Source == AppleSource))
            { _order.Remove(cacheKey); _order.AddLast(cacheKey); return cached!; }
            var lyrics = Lyrics.Empty;
            // QQ tracks must stay within QQ, including when lookup fails or metadata is incomplete.
            if (track.IsQqMusic) lyrics = await FindQq(track, token);
            else if (track.IsAppleMusic)
            {
                lyrics = await FindApple(track, token);
                if (!lyrics.Synced || lyrics.Lines.Count == 0) lyrics = hit ? cached! : await FindAppleFallback(track, lyrics, token);
            }
            else
            {
                if (track.SongId.StartsWith("netease:", StringComparison.Ordinal))
                    lyrics = await FindNeteaseId(track.SongId[8..], track.Duration.TotalSeconds, token);
                // KuGou's songs ask KuGou first; for the others it is the last provider asked.
                if (track.IsKugou) lyrics = await FindKugou(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindLrclib(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindNetease(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental && !track.IsKugou) lyrics = await FindKugou(track, token);
            }
            token.ThrowIfCancellationRequested();
            // An empty result can be a temporary provider failure. Do not keep it for the entire app session.
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental)
            {
                _cache[cacheKey] = lyrics; _order.Remove(cacheKey); _order.AddLast(cacheKey);
                while (_order.Count > 80) { _cache.Remove(_order.First!.Value); _order.RemoveFirst(); }
            }
            return lyrics;
        }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement?> Get(string url, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var address = new Uri(url);
                for (int redirects = 0; ; redirects++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, address);
                    request.Headers.UserAgent.ParseAdd("TwelveIsland/0.1.0");
                    if (address.Host == "music.163.com") request.Headers.Referrer = new Uri("https://music.163.com/");
                    if (address.Host is "u.y.qq.com" or "c.y.qq.com") request.Headers.Referrer = new Uri("https://y.qq.com/");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        // Only to HTTPS on the same host, and only a few times: a lookup never ends up somewhere else.
                        if (redirects >= MaxRedirects || response.Headers.Location is not { } location) return null;
                        var next = location.IsAbsoluteUri ? location : new Uri(address, location);
                        if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, address.Host, StringComparison.OrdinalIgnoreCase)) return null;
                        address = next;
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var body = await ReadLimitedAsync(stream, timeout.Token);
                    if (body is null) return null;
                    // As a stream, which skips a byte order mark the way reading the response directly did.
                    using var json = JsonDocument.Parse(new MemoryStream(body, writable: false));
                    return json.RootElement.Clone();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                bool temporary = ex is JsonException or OperationCanceledException || ex is HttpRequestException http &&
                    (http.StatusCode is null or HttpStatusCode.RequestTimeout || (int)http.StatusCode >= 500);
                if (attempt != 0 || !temporary) return null;
                await Task.Delay(200, token);
            }
        }
        return null;
    }
    // The reply, or null once it is larger than MaxResponseBytes, whatever its declared length said.
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    private static string Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Num(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    private static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}\p{S}]", "").ToUpperInvariant();
    // Apple Music shows names in the storefront's script (李耀陽); the other providers may use another (李耀阳).
    private static string MatchText(MediaTrack track, string value) => Normalize(track.IsAppleMusic ? SimplifyAppleText(value) : value);
    private static bool Matches(MediaTrack track, string title, string artist, double seconds)
        => MatchText(track, track.Title) == MatchText(track, title) && Regex.Split(track.Artist, @"\s*[/、,，&;；]\s*").Where(a => a.Length > 0).All(a => MatchText(track, artist).Contains(MatchText(track, a), StringComparison.Ordinal))
            && (track.Duration.TotalSeconds <= 0 || Math.Abs(seconds - track.Duration.TotalSeconds) <= 2);
    private async Task<Lyrics> FindNeteaseId(string id, double duration, CancellationToken token)
    {
        if (!Regex.IsMatch(id, @"\A[0-9]{1,16}\z")) return Lyrics.Empty;
        // lv, kv and tv name the lyric versions the caller already holds; the reply leaves out a version that is
        // not newer. lv=1 therefore received an empty lyric for every song still at its first version (measured).
        var data = await Get($"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1", token);
        if (data is not { ValueKind: JsonValueKind.Object } value) return Lyrics.Empty;
        // NetEase marks a song without words; its lyric then holds only credits and a notice.
        if (value.TryGetProperty("pureMusic", out var pure) && pure.ValueKind == JsonValueKind.True) return new([], true, "网易云音乐", true);
        return value.TryGetProperty("lrc", out var lrc) ? Lyrics.Parse(Str(lrc, "lyric"), null, duration, "网易云音乐") : Lyrics.Empty;
    }
    private async Task<Lyrics> FindLrclib(MediaTrack track, CancellationToken token)
    {
        string query = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
        string duration = track.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var exact = await Get($"https://lrclib.net/api/get?{query}&album_name={Uri.EscapeDataString(track.Album)}&duration={duration}", token);
        Lyrics Parse(JsonElement value) => Lyrics.Parse(Str(value, "syncedLyrics"), Str(value, "plainLyrics"), track.Duration.TotalSeconds, "LRCLIB",
            value.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True);
        if (exact is { ValueKind: JsonValueKind.Object } row && Matches(track, Str(row, "trackName"), Str(row, "artistName"), Num(row, "duration")))
        { var lyrics = Parse(row); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        var search = await Get("https://lrclib.net/api/search?" + query, token);
        if (search is { ValueKind: JsonValueKind.Array } list)
            foreach (var item in list.EnumerateArray().Where(v => Matches(track, Str(v, "trackName"), Str(v, "artistName"), Num(v, "duration"))).OrderByDescending(v => Str(v, "syncedLyrics").Length > 0))
            { var lyrics = Parse(item); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        return Lyrics.Empty;
    }
    private async Task<Lyrics> FindNetease(MediaTrack track, CancellationToken token)
    {
        var search = await Get("https://music.163.com/api/search/get/web?type=1&limit=8&s=" + Uri.EscapeDataString(track.Title + " " + track.Artist), token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        foreach (var song in songs.EnumerateArray())
        {
            string artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array ? string.Join(" / ", artists.EnumerateArray().Select(a => Str(a, "name"))) : "";
            if (!Matches(track, Str(song, "name"), artist, Num(song, "duration") / 1000) || !song.TryGetProperty("id", out var id)) continue;
            var lyrics = await FindNeteaseId(id.ToString(), track.Duration.TotalSeconds, token);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var watcher in _appleWatchers.Values) watcher.Dispose();
    }
}
