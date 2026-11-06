using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public enum NeteaseLogKind { Track, Loaded, Play, Pause, Position, Restore, Buffering, Buffered, Mode, End, Exit }
public sealed record NeteaseLogEvent(NeteaseLogKind Kind, long Tick)
{
    public string SongId { get; init; } = "";
    public string PlayId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string? CoverUrl { get; init; }
    public double? Seconds { get; init; }
    public double DurationSeconds { get; init; }
    public bool Paused { get; init; }
    public string Mode { get; init; } = "";
}

// Only selected music fields survive parsing. Neither the raw log nor resource URLs
// (which may contain credentials) are retained by the parser or state machine.
public static partial class NeteaseLog
{
    private static readonly byte[] DecodeTable = Enumerable.Range(0, 256).Select(value =>
    {
        int high = value >> 4, low = value & 15;
        return (byte)((((high ^ ((low + 8) & 15)) & 15) * 16 + (value >> 6) * 4 + (~high & 3)) & 255);
    }).ToArray();

    public static void Decode(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++) bytes[i] = DecodeTable[bytes[i]];
    }

    [GeneratedRegex(@"^\[(\d+):\d+:\d{4}/\d{6}:(\d+):")]
    private static partial Regex Header();

    public static NeteaseLogEvent? Parse(string line, int processId, long nowTick, long minimumTick = 0) =>
        Parse(line, processId, nowTick, minimumTick, false);

    // Song title, artists, album, duration and artwork belong to a song id and do not
    // change between player runs, so metadata is read without the generation filters.
    public static NeteaseLogEvent? ParseTrack(string line) => Parse(line, null, long.MaxValue, 0, true);

    private static NeteaseLogEvent? Parse(string line, int? processId, long nowTick, long minimumTick, bool tracksOnly)
    {
        if (line.Length > 1_048_576 || !line.Contains("【playing】", StringComparison.Ordinal) && !line.Contains("exitApp", StringComparison.Ordinal)) return null;
        var match = Header().Match(line);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, out long tick)) return null;
        if (processId is { } wanted && (!int.TryParse(match.Groups[1].Value, out int pid) || pid != wanted || tick < minimumTick || tick > nowTick)) return null;
        try
        {
            int marker = line.IndexOf("【playing】,", StringComparison.Ordinal);
            if (marker < 0)
            {
                marker = line.IndexOf("【app】,", StringComparison.Ordinal);
                if (marker < 0 || tracksOnly) return null;
                using var app = JsonDocument.Parse(line[(marker + "【app】,".Length)..]);
                return Text(app.RootElement, "actionId") == "exitApp" ? new(NeteaseLogKind.Exit, tick) : null;
            }
            var payload = line[(marker + "【playing】,".Length)..];
            // Reject unrelated events before deserializing their potentially large payloads.
            int nameEnd = payload.IndexOf('"', 1);
            if (!payload.StartsWith('"') || nameEnd < 0) return null;
            string name = payload[1..nameEnd];
            if (tracksOnly && name is not ("playOneTrackInPlayingList" or "nativePlay" or "setPlaying")) return null;
            if (name is not ("playOneTrackInPlayingList" or "nativePlay" or "setPlaying" or
                "native播放资源load完成，开始播放" or "native播放state" or "resume" or "pause" or
                "setPlayingPosition" or "loadingSeek" or "appRestore，播放信息恢复" or "PlayerBuffering" or
                "PlayerBufferingEnd" or "播放模式切换" or "onPlayEnd handle reason" or "stop playId=")) return null;
            using var json = JsonDocument.Parse("[" + payload + "]", new JsonDocumentOptions { MaxDepth = 32 });
            var values = json.RootElement;
            JsonElement Arg(int index) => index < values.GetArrayLength() ? values[index] : default;
            var first = Arg(1);
            NeteaseLogEvent Event(NeteaseLogKind kind, string playId = "") => new(kind, tick)
            { PlayId = playId, SongId = playId.Split('_')[0] };
            switch (name)
            {
                case "playOneTrackInPlayingList": case "nativePlay": case "setPlaying":
                    if (!first.TryObject("track", out var track)) return null;
                    string title = Text(track, "name"), id = Id(track, "id");
                    if (title.Length == 0 || id.Length == 0) return null;
                    track.TryObject("album", out var album);
                    string artist = track.ValueKind == JsonValueKind.Object && track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                        ? string.Join(" / ", artists.EnumerateArray().Take(30).Select(a => Text(a, "name"))) : "";
                    var cover = Text(album, "picUrl");
                    return new(NeteaseLogKind.Track, tick) { SongId = id, Title = title, Artist = artist, Album = Text(album, "name"),
                        DurationSeconds = Number(track, "duration") is { } ms && ms is > 0 and <= 604_800_000 ? ms / 1000 : 0,
                        CoverUrl = SafeCover(cover) };
                case "native播放资源load完成，开始播放":
                    return new(NeteaseLogKind.Loaded, tick) { SongId = Id(first, "songId") };
                case "native播放state":
                    return Number(first) switch { 1 => Event(NeteaseLogKind.Play, Value(Arg(2))), 2 => Event(NeteaseLogKind.Pause, Value(Arg(2))), _ => null };
                case "resume": return Event(NeteaseLogKind.Play, Value(first));
                case "pause": return Event(NeteaseLogKind.Pause, Value(first));
                case "setPlayingPosition": case "loadingSeek":
                    return Number(first) is { } seconds && seconds is >= 0 and <= 604800
                        ? new(NeteaseLogKind.Position, tick) { Seconds = seconds } : null;
                case "appRestore，播放信息恢复":
                    return new(NeteaseLogKind.Restore, tick) { SongId = Id(first, "id"), Seconds = Number(first, "current"),
                        Paused = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("pause", out var pause) && pause.ValueKind == JsonValueKind.True };
                case "PlayerBuffering": return Event(NeteaseLogKind.Buffering, Value(first));
                case "PlayerBufferingEnd": return Event(NeteaseLogKind.Buffered, Value(first));
                case "播放模式切换":
                    string mode = Value(Arg(2));
                    return mode is "playCycle" or "playOneCycle" or "playOrder" or "playRandom" or "playFm"
                        ? new(NeteaseLogKind.Mode, tick) { Mode = mode } : null;
                case "stop playId=": return Event(NeteaseLogKind.End, Value(first));
                case "onPlayEnd handle reason":
                    // An unlabelled 'call' completion commonly belongs to the previous song.
                    if (Text(first, "reason") == "call") return null;
                    return new(NeteaseLogKind.End, tick) { PlayId = Text(first, "playId"), SongId = Id(first, "songId") };
                default: return null;
            }
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool TryObject(this JsonElement value, string key, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result) && result.ValueKind == JsonValueKind.Object;
    }
    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? Value(value)[..Math.Min(Value(value).Length, 4096)] : "";
    private static string Id(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number && value.ToString() is { Length: > 0 and <= 128 } id ? id : "";
    private static double? Number(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? Number(value) : null;
    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    public static string? SafeCover(string value) => value.Length <= 4096 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && (uri.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase) || uri.Host == "music.126.net") ? value : null;
}

// The tail replay keeps the most recent track event per song id so a startup
// restoration, which carries only an id and a position, can be completed.
public sealed class NeteaseTrackCache
{
    private const int Capacity = 200;
    private readonly Dictionary<string, NeteaseLogEvent> _tracks = [];
    private readonly Queue<string> _order = new();
    public int Count => _tracks.Count;
    public void Clear() { _tracks.Clear(); _order.Clear(); }
    public void Remember(NeteaseLogEvent? e)
    {
        if (e is not { Kind: NeteaseLogKind.Track } || e.SongId.Length == 0) return;
        if (!_tracks.ContainsKey(e.SongId)) _order.Enqueue(e.SongId);
        _tracks[e.SongId] = e;
        while (_order.Count > Capacity) _tracks.Remove(_order.Dequeue());
    }
    public NeteaseLogEvent? Find(string songId) => songId.Length > 0 && _tracks.TryGetValue(songId, out var e) ? e : null;
}

public sealed record NeteaseLogState
{
    public MediaTrack? Track { get; init; }
    public string? CoverUrl { get; init; }
    public string PlayId { get; init; } = "";
    public string RetiredPlayId { get; init; } = "";
    public string Mode { get; init; } = "";
    public bool Buffering { get; init; }
    public bool Exited { get; init; }
    public long LastTick { get; init; } = -1;
    public string RestoredId { get; init; } = "";
    public double? RestoredPosition { get; init; }
    public bool RestoredPaused { get; init; }

    public NeteaseLogState Apply(NeteaseLogEvent e, DateTimeOffset now, long nowTick, NeteaseTrackCache? tracks = null)
    {
        if (e.Tick < LastTick || e.Tick > nowTick || Exited) return this;
        var at = now.AddMilliseconds(e.Tick - nowTick);
        var state = this with { LastTick = e.Tick };
        var track = Track;
        if (e.Kind == NeteaseLogKind.Exit) return state with { Track = null, CoverUrl = null, PlayId = "", Exited = true };
        // Shuffle and repeat are not offered in log mode; only private FM is read.
        if (e.Kind == NeteaseLogKind.Mode)
            return state with { Mode = e.Mode, Track = track is null ? null : track with { PreviousRestricted = e.Mode == "playFm" } };
        if (e.Kind == NeteaseLogKind.Track)
        {
            bool restored = RestoredId == e.SongId && RestoredPosition is >= 0;
            return state with { PlayId = "", RetiredPlayId = PlayId.Length > 0 ? PlayId : RetiredPlayId, Buffering = false, CoverUrl = e.CoverUrl, RestoredId = "", RestoredPosition = null,
                Track = Song(e, at, restored ? RestoredPosition!.Value : null, RestoredPaused) };
        }
        if (e.Kind == NeteaseLogKind.Restore)
        {
            state = state with { RestoredId = e.SongId, RestoredPosition = e.Seconds, RestoredPaused = e.Paused };
            if (track?.SongId != "netease:" + e.SongId)
            {
                // A restored session reports only an id and a position. Complete it from the
                // most recent track event for that song; without one there is no media at all.
                if (tracks?.Find(e.SongId) is { } known && e.Seconds is >= 0 and <= 604800)
                    return state with { PlayId = "", Buffering = false, CoverUrl = known.CoverUrl, RestoredId = "", RestoredPosition = null,
                        Track = Song(known, at, e.Seconds.Value, e.Paused) };
                return state with { Track = null, CoverUrl = null, PlayId = "" };
            }
        }
        if (track is null || e.SongId.Length > 0 && track.SongId != "netease:" + e.SongId) return state;
        if (e.PlayId.Length > 0 && (e.PlayId == RetiredPlayId || PlayId.Length > 0 && e.PlayId != PlayId)) return state;
        // Freeze at the event's actual monotonic time, not when the watcher was scheduled.
        var anchored = track with { ReportedPosition = track.Position(at), PositionAt = at };
        switch (e.Kind)
        {
            case NeteaseLogKind.Loaded:
                return state with { Buffering = false, Track = anchored with { ReportedPosition = TimeSpan.Zero, State = MediaState.Playing, Rate = 1, PositionKnown = true, TimelinePending = false } };
            case NeteaseLogKind.Play: case NeteaseLogKind.Pause:
                return state with { PlayId = e.PlayId.Length > 0 ? e.PlayId : PlayId,
                    Track = anchored with { State = e.Kind == NeteaseLogKind.Play ? MediaState.Playing : MediaState.Paused } };
            case NeteaseLogKind.Position: case NeteaseLogKind.Restore:
                if (e.Seconds is not (>= 0 and <= 604800)) return state;
                return state with { Track = anchored with { ReportedPosition = TimeSpan.FromSeconds(Math.Min(e.Seconds.Value, track.Duration.TotalSeconds)),
                    PositionKnown = true, TimelinePending = false, State = e.Kind == NeteaseLogKind.Restore ? e.Paused ? MediaState.Paused : MediaState.Playing : track.State } };
            case NeteaseLogKind.Buffering: case NeteaseLogKind.Buffered:
                return state with { Buffering = e.Kind == NeteaseLogKind.Buffering, Track = anchored with { Rate = e.Kind == NeteaseLogKind.Buffering ? 0 : 1 } };
            case NeteaseLogKind.End:
                if (track.TimelinePending) return state;
                return state with { PlayId = "", Buffering = false, Track = anchored with { State = MediaState.Stopped, Rate = 1 } };
            default: return state;
        }
    }

    // A known position means the song was restored with one; otherwise playback has
    // not reported where it is yet.
    private MediaTrack Song(NeteaseLogEvent e, DateTimeOffset at, double? position, bool paused) => new()
    {
        Source = "网易云音乐", SongId = "netease:" + e.SongId, Title = e.Title, Artist = e.Artist, Album = e.Album,
        End = TimeSpan.FromSeconds(e.DurationSeconds),
        State = position is null ? MediaState.Unknown : paused ? MediaState.Paused : MediaState.Playing,
        PositionKnown = position is not null, TimelinePending = position is null,
        ReportedPosition = TimeSpan.FromSeconds(e.DurationSeconds > 0 ? Math.Min(position ?? 0, e.DurationSeconds) : position ?? 0),
        PositionAt = at, IsNeteaseLog = true, PreviousRestricted = Mode == "playFm"
    };
}
