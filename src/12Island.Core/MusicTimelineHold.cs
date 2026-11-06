namespace TwelveIsland.Core;

// A player that moves to its next song often reports the song before its timeline: NetEase's log names the song
// before the player has loaded it, QQ Music clears the old time at once, and an SMTC session may read no timeline
// for a moment. The music page gives the progress bar's room to the cover when there is no timeline, so each such
// gap would grow the cover and shrink it back. A player that had a timeline therefore keeps the bar's room while
// it has none, for at most Hold, or PendingHold while it reports that the timeline is still to come.
public sealed class MusicTimelineHold
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2), PendingHold = TimeSpan.FromSeconds(8);
    private MediaTrack? _last;
    private DateTimeOffset? _until;
    // When the page must be laid out again, since no update reports the end of the hold.
    public DateTimeOffset? Until => _until;

    public void Observe(MediaTrack? track, DateTimeOffset now)
    {
        if (track is null || track.HasTimeline || _last?.Id != track.Id) _until = null;
        else if (_last.HasTimeline) _until = now + (track.TimelinePending ? PendingHold : Hold);
        else if (_until is { } until && !track.TimelinePending && until > now + Hold) _until = now + Hold;
        _last = track;
    }
    // Whether the page keeps the progress bar's room for a track that has no timeline.
    public bool Holds(DateTimeOffset now) => _until > now;
}
