namespace TwelveIsland.Core;

public enum MediaDispatchResult { Unavailable, Sent, Uncertain }

public static class MediaRouting
{
    // Only an operation known not to have been sent may fall through to another transport.
    public static async Task<bool> DispatchAsync(Func<Task<MediaDispatchResult>> dedicated, Func<Task<bool>> system)
        => await dedicated() switch
        {
            MediaDispatchResult.Sent => true,
            MediaDispatchResult.Unavailable => await system(),
            _ => false
        };

    // Players join several artists differently ("A / B" in NetEase's log, "A/B" or "A, B" elsewhere), and
    // NetEase's SMTC session names only the first artist of a song its log lists in full (measured:
    // "<artist>" against "<artist> / <featured>"). Neither the separators nor a list that only adds
    // artists makes a different song; a different artist does.
    public static bool SameSong(MediaTrack a, MediaTrack b) =>
        string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase) && SameArtists(a.Artist, b.Artist);
    private static bool SameArtists(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        if (string.Equals(Joined(a), Joined(b), StringComparison.OrdinalIgnoreCase)) return true;
        var first = Names(a); var second = Names(b);
        return first.Count > 0 && second.Count > 0 && (first.IsSubsetOf(second) || second.IsSubsetOf(first));
    }
    private static readonly char[] ArtistSeparators = ['/', ',', '\u3001', '&', ';', '\uFF0F', '\uFF0C', '\uFF06', '\uFF1B'];
    private static string Joined(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && Array.IndexOf(ArtistSeparators, c) < 0));
    private static HashSet<string> Names(string value) => value.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
        .Select(Joined).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Whether NetEase's log is on the song its SMTC session reports. The two write a title independently,
    // so full-width forms, spacing and a version suffix such as "(Live)" on one side still match.
    public static bool LogMatchesSession(MediaTrack log, MediaTrack system) =>
        !string.IsNullOrWhiteSpace(log.Title) && (SameSong(log, system) || SimilarTitle(log.Title, system.Title) && SameArtists(log.Artist, system.Artist));
    private static bool SimilarTitle(string a, string b)
    {
        string x = Folded(a), y = Folded(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = x.Length < y.Length ? (x, y) : (y, x);
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase) && "([{<-\u3010\u300A\u300C".Contains(longer[shorter.Length]);
    }
    private static string Folded(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));

    // A supplement owns only these three controls. It cannot replace metadata, time,
    // modes, or state, even while the primary is waiting for the next song to load.
    public static MediaTrack Merge(MediaTrack primary, MediaTrack? controls) => primary with
    {
        CanToggle = primary.CanToggle || controls?.CanToggle == true,
        CanPrevious = !primary.PreviousRestricted && (primary.CanPrevious || controls?.CanPrevious == true),
        CanNext = primary.CanNext || controls?.CanNext == true
    };

    // NetEase without full control, with its SMTC switched on: the session reports a skip about 60 ms
    // after it happens (measured in the player's own log), so it supplies the song, its artwork and its
    // state. The log still supplies what the session lacks for the same song: its id, album, timeline and
    // the FM restriction. A session without a title leaves the log in charge, as before.
    public static MediaTrack PreferSystem(MediaTrack log, MediaTrack? system)
    {
        if (system is null || string.IsNullOrWhiteSpace(system.Title)) return Merge(log, system);
        bool same = LogMatchesSession(log, system);
        // The log's position is exact for its song; the session's is used only when the log is on another song.
        var time = same && log.HasTimeline ? log : system.HasTimeline ? system : null;
        return new MediaTrack
        {
            // Once the log is on the session's song it names the song: its artist list is complete.
            Id = log.Id, Source = log.Source, Title = same ? log.Title : system.Title, Artist = same && log.Artist.Length > 0 ? log.Artist : system.Artist,
            Album = system.Album.Length == 0 && same ? log.Album : system.Album,
            SongId = same ? log.SongId : "", EmbeddedLyrics = same ? log.EmbeddedLyrics : "",
            CurrentLyric = same ? log.CurrentLyric : null, CurrentLyricStart = same ? log.CurrentLyricStart : null,
            State = system.State == MediaState.Unknown && same ? log.State : system.State,
            IsNeteaseLog = true, PreviousRestricted = log.PreviousRestricted,
            PositionKnown = time?.PositionKnown ?? false,
            // Until the log reaches the session's song, nothing has reported where that song is.
            TimelinePending = time is null && (!same || log.TimelinePending),
            Start = time?.Start ?? TimeSpan.Zero, End = time?.End ?? TimeSpan.Zero, MinSeek = time?.MinSeek ?? TimeSpan.Zero, MaxSeek = time?.MaxSeek ?? TimeSpan.Zero,
            ReportedPosition = time?.ReportedPosition ?? TimeSpan.Zero, PositionAt = time?.PositionAt ?? system.PositionAt, Rate = time?.Rate ?? 1,
            CanToggle = log.CanToggle || system.CanToggle,
            CanPrevious = !log.PreviousRestricted && (log.CanPrevious || system.CanPrevious),
            CanNext = log.CanNext || system.CanNext
        };
    }

    // Apple Music's session reports "<artist> — <album>" as the artist and leaves the album empty (measured with
    // version 1.1540.23042.0: "伍佰 & China Blue — 泪桥 - Single"). The first dash ends the artist.
    public static (string Artist, string Album) SessionArtistAlbum(string appId, string artist, string album)
    {
        const string separator = " — ";
        if (album.Length > 0 || !IsAppleMusic(appId)) return (artist, album);
        int dash = artist.IndexOf(separator, StringComparison.Ordinal);
        return dash > 0 ? (artist[..dash], artist[(dash + separator.Length)..]) : (artist, album);
    }
    public static bool IsAppleMusic(string appId) => appId.Contains("applemusic", StringComparison.OrdinalIgnoreCase);

    // Apple Music reports its position in whole seconds, stamped with the time of the report, two to four times a
    // second (measured: 2.000 at :57.677, 2.000 at :58.219, 3.000 at :58.433). A report of n seconds at t places the song's
    // start, when it was at zero, after t - n - 1 and no later than t - n. Each report of the same playing song narrows that
    // span (to 0.28 seconds within seven seconds, measured), and the position is taken from its middle; taking each report
    // as it came would leave the position up to a second behind and move the progress bar back and forth. A report outside
    // the span is a seek or a new start, and begins a new span.
    public sealed record WholeSecondStart(MediaTrack Song, DateTimeOffset Earliest, DateTimeOffset Latest);
    public static (MediaTrack Track, WholeSecondStart? Start) FromWholeSeconds(WholeSecondStart? known, MediaTrack read)
    {
        var reported = read.ReportedPosition - read.Start;
        if (read is not { State: MediaState.Playing, HasTimeline: true, Rate: 1 } || reported.Ticks % TimeSpan.TicksPerSecond != 0) return (read, null);
        var latest = read.PositionAt - reported;
        var earliest = latest - TimeSpan.FromSeconds(1);
        if (known is not null && known.Song.Duration == read.Duration && SameSong(known.Song, read) && known.Earliest < latest && earliest < known.Latest)
        {
            if (known.Earliest > earliest) earliest = known.Earliest;
            if (known.Latest < latest) latest = known.Latest;
        }
        var middle = earliest + (latest - earliest) / 2;
        return (read with { ReportedPosition = read.Start + (read.PositionAt - middle) }, new(read, earliest, latest));
    }

    // Apple Music reports a pause right after its position is changed and plays on half a second later (measured: Paused
    // 80 ms after the seek, Playing again 500 ms after that). Within SeekSettle of a seek TwelveIsland made, a pause read while
    // the same song was playing is that pause, and the playing track is kept; a real pause shows once the time is over.
    public static readonly TimeSpan SeekSettle = TimeSpan.FromSeconds(1.5);
    public static bool SettlingAfterSeek(MediaTrack known, MediaTrack read, DateTimeOffset? seekAt, DateTimeOffset now) =>
        seekAt is { } at && now - at >= TimeSpan.Zero && now - at < SeekSettle &&
        known.State == MediaState.Playing && read.State == MediaState.Paused && SameSong(known, read);
}
