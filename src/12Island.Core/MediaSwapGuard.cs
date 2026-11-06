namespace TwelveIsland.Core;

// One SMTC session can stand for several media sources of its app. Chrome publishes a single session and
// fills it with whichever tab it considers active, so while a playing tab moves to its next song the session
// briefly carries another tab's title and artwork (a paused video, for instance) before the new song.
// A playing session therefore keeps its song on screen while it reports a different song that is not a
// fresh start (empty, not playing, or playing from well inside its timeline), for at most Hold. A new song
// that starts playing from its beginning, which is what a skip or the end of a song produces, is taken at
// once; anything else that lasts beyond the hold is taken then, so a real switch is only delayed.
public sealed class MediaSwapGuard
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.5);
    // How far into its song a newly reported track may be and still count as a song that has just started.
    public static readonly TimeSpan FreshStart = TimeSpan.FromSeconds(3);
    private DateTimeOffset? _since;
    // When a held change must be looked at again, since no event reports the end of the hold.
    public DateTimeOffset? RecheckAt => _since + Hold;

    // Whether the session's newly read song is held back in favour of the one on screen.
    public bool Holds(MediaTrack? shown, MediaTrack read, DateTimeOffset now)
    {
        if (shown is not { State: MediaState.Playing } || string.IsNullOrWhiteSpace(shown.Title) ||
            !string.IsNullOrWhiteSpace(read.Title) && (MediaRouting.SameSong(shown, read) || Fresh(read, now)))
        { _since = null; return false; }
        _since ??= now;
        if (now - _since < Hold) return true;
        _since = null; return false;
    }
    private static bool Fresh(MediaTrack read, DateTimeOffset now) =>
        read.State == MediaState.Playing && (!read.HasTimeline || read.Position(now) <= FreshStart);
}