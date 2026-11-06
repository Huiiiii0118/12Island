using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public sealed partial class LyricsService
{
    // KuGou's lyric search answers with candidates and the key to download each. A song KuGou itself plays is found by its
    // hash; any other song by "Artist - Title", and only a candidate with the same title, artists and length is taken.
    private async Task<Lyrics> FindKugou(MediaTrack track, CancellationToken token)
    {
        string hash = track.SongId.StartsWith("kugou:", StringComparison.Ordinal) ? track.SongId[6..] : "";
        if (hash.Length > 0 && !Regex.IsMatch(hash, @"\A[0-9a-f]{32}\z")) return Lyrics.Empty;
        string keyword = hash.Length > 0 ? "" : track.Artist + " - " + track.Title;
        string duration = track.Duration > TimeSpan.Zero ? ((long)track.Duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var search = await Get($"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={Uri.EscapeDataString(keyword)}&duration={duration}&hash={hash}", token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        foreach (var candidate in candidates.EnumerateArray())
        {
            double seconds = Num(candidate, "duration") / 1000;
            bool fits = hash.Length > 0 ? track.Duration.TotalSeconds <= 0 || Math.Abs(seconds - track.Duration.TotalSeconds) <= 2
                : Matches(track, Str(candidate, "song"), Str(candidate, "singer"), seconds);
            string id = Str(candidate, "id"), key = Str(candidate, "accesskey");
            if (!fits || !Regex.IsMatch(id, @"\A[0-9]{1,16}\z") || !Regex.IsMatch(key, @"\A[0-9A-Fa-f]{16,64}\z")) continue;
            var lyrics = await DownloadKugou(id, key, track.Duration.TotalSeconds, token);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }

    private async Task<Lyrics> DownloadKugou(string id, string key, double duration, CancellationToken token)
    {
        var reply = await Get($"https://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={key}&fmt=lrc&charset=utf8", token);
        if (reply is not { ValueKind: JsonValueKind.Object } value || Str(value, "content") is not { Length: > 0 } content) return Lyrics.Empty;
        try { return Lyrics.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(content)), null, duration, "酷狗音乐"); }
        catch (FormatException) { return Lyrics.Empty; }
    }
}
