using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public sealed partial class LyricsService
{
    private static bool QqSucceeded(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int number) && number == 0;

    private async Task<Lyrics> FindQq(MediaTrack track, CancellationToken token)
    {
        string? mid = null;
        long? id = null;
        bool requiresPreview = false;
        if (track.SongId.StartsWith("qq:", StringComparison.Ordinal))
        {
            mid = track.SongId[3..];
            if (!Regex.IsMatch(mid, @"\A[a-zA-Z0-9]{8,32}\z")) return Lyrics.Empty;
        }
        else if (track.SongId.StartsWith("qqid:", StringComparison.Ordinal))
        {
            string value = track.SongId[5..];
            if (!Regex.IsMatch(value, @"\A[1-9][0-9]{0,15}\z") || !long.TryParse(value, out long number)) return Lyrics.Empty;
            id = number;
        }
        else
        {
            // The desktop transport currently has no song ID. Wait for its duration before
            // matching; otherwise an identically named live recording or cover can be selected.
            if (track.Duration <= TimeSpan.Zero) return Lyrics.Empty;
            var search = await Get("https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?format=json&p=1&n=20&w=" +
                Uri.EscapeDataString(track.Title + " " + track.Artist), token);
            if (search is not { } root || !QqSucceeded(root) ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object ||
                !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
            var candidates = new List<(string Mid, double Seconds, bool ExactArtists)>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("singer", out var singers) ||
                    singers.ValueKind != JsonValueKind.Array) continue;
                string artist = string.Join(" / ", singers.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(s => Str(s, "name")));
                string candidateMid = Str(item, "songmid");
                if (!Regex.IsMatch(candidateMid, @"\A[a-zA-Z0-9]{8,32}\z") ||
                    !Matches(track, WebUtility.HtmlDecode(Str(item, "songname")), WebUtility.HtmlDecode(artist), track.Duration.TotalSeconds) ||
                    track.IsQqMusic && !string.IsNullOrWhiteSpace(track.Album) && Normalize(track.Album) != Normalize(WebUtility.HtmlDecode(Str(item, "albumname")))) continue;
                var expectedArtists = Regex.Split(track.Artist, @"\s*[/、,，&;；]\s*").Select(Normalize).Where(a => a.Length > 0).ToHashSet();
                var actualArtists = singers.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object)
                    .Select(s => Normalize(WebUtility.HtmlDecode(Str(s, "name")))).Where(a => a.Length > 0);
                candidates.Add((candidateMid, Num(item, "interval"), expectedArtists.SetEquals(actualArtists)));
            }
            mid = candidates.Where(c => Math.Abs(c.Seconds - track.Duration.TotalSeconds) <= 2)
                .OrderBy(c => Math.Abs(c.Seconds - track.Duration.TotalSeconds)).Select(c => c.Mid).FirstOrDefault();
            if (mid is null && track.IsQqMusic && !string.IsNullOrWhiteSpace(track.Album))
            {
                // A VIP preview reports the clip duration. Require an unambiguous recording
                // and verify its preview length with QQ's song detail before accepting it.
                var previews = candidates.Where(c => c.ExactArtists && c.Seconds > track.Duration.TotalSeconds + 2)
                    .Select(c => c.Mid).Distinct().ToArray();
                if (previews.Length == 1) { mid = previews[0]; requiresPreview = true; }
            }
            if (mid is null) return Lyrics.Empty;
        }

        var parameter = new Dictionary<string, object>();
        if (mid is not null) parameter["songMID"] = mid;
        else parameter["songID"] = id!.Value;
        var detailParameter = new Dictionary<string, object> { ["song_type"] = 0 };
        if (mid is not null) detailParameter["song_mid"] = mid;
        else detailParameter["song_id"] = id!.Value;
        string payload = JsonSerializer.Serialize(new
        {
            comm = new { ct = 24, cv = 0 },
            lyric = new { module = "music.musichallSong.PlayLyricInfo", method = "GetPlayLyricInfo", param = parameter },
            info = new { module = "music.pf_song_detail_svr", method = "get_song_detail_yqq", param = detailParameter }
        });
        var response = await Get("https://u.y.qq.com/cgi-bin/musicu.fcg?data=" + Uri.EscapeDataString(payload), token);
        bool preview = IsQqPreview(response, track, mid, id);
        if (requiresPreview && !preview) return Lyrics.Empty;
        if (response is { } result && QqSucceeded(result) && result.TryGetProperty("lyric", out var lyric) && QqSucceeded(lyric) &&
            lyric.TryGetProperty("data", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            try
            {
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(Str(content, "lyric")));
                var parsed = ParseQq(text, track);
                if (parsed.Lines.Count > 0 || parsed.Instrumental) return parsed with { QqPreview = preview };
            }
            catch (FormatException) { }
        }
        // The legacy endpoint uses the same QQ song identity and catalog, never another provider.
        if (mid is null) return Lyrics.Empty;
        var legacy = await Get($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={mid}&format=json&nobase64=1", token);
        return legacy is { } old && QqSucceeded(old) ? ParseQq(Str(old, "lyric"), track) with { QqPreview = preview } : Lyrics.Empty;
    }

    private static bool IsQqPreview(JsonElement? response, MediaTrack track, string? mid, long? id)
    {
        if (track.Duration <= TimeSpan.Zero || response is not { } root || !QqSucceeded(root) ||
            !root.TryGetProperty("info", out var info) || !QqSucceeded(info) ||
            !info.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("track_info", out var song) || song.ValueKind != JsonValueKind.Object ||
            (mid is not null ? Str(song, "mid") != mid : Num(song, "id") != id) ||
            Num(song, "interval") <= track.Duration.TotalSeconds + 2 ||
            !song.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object) return false;
        bool MatchesClip(string begin, string end)
        {
            double start = Num(file, begin), finish = Num(file, end);
            return start >= 0 && finish > start && finish <= Num(song, "interval") * 1000 + 2000 &&
                Math.Abs((finish - start) / 1000 - track.Duration.TotalSeconds) <= 2;
        }
        // These describe available clips, not necessarily the desktop player's active offset.
        return MatchesClip("b_30s", "e_30s") || MatchesClip("try_begin", "try_end");
    }

    private static Lyrics ParseQq(string text, MediaTrack track)
    {
        var lyrics = Lyrics.Parse(WebUtility.HtmlDecode(text), null, track.Duration.TotalSeconds, "QQ 音乐");
        // Empty timestamp placeholders are not a usable lyric result and must not be cached.
        return lyrics.Instrumental || lyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)) ? lyrics : Lyrics.Empty;
    }
}
