namespace TwelveIsland.Core;

// Decides when a track change briefly shows the new title in the compact view: when the new song starts playing.
public sealed class TrackNoticeGate
{
    // QQ Music reports the next song while it is stopped and starts playing it up to 1.5 s later (measured
    // through its pipe). A song that starts within this window still shows its title; one started by hand later does not.
    public static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(5);
    private DateTimeOffset? _until;
    private bool _awaitingState;
    public bool Armed => _until is not null;

    public void Arm(DateTimeOffset now) { _until = now + StartWindow; _awaitingState = false; }
    public void Disarm() { _until = null; _awaitingState = false; }
    // True when this update shows the notice. A song read from NetEase's log has no state until the player
    // reports that it has loaded, and that report can arrive in a later read; the window then starts when
    // the state becomes known, so a late report cannot lose the notice.
    public bool Observe(MediaTrack? track, DateTimeOffset now)
    {
        if (_until is not { } until || track is null) return false;
        if (track.State == MediaState.Unknown) { if (track.IsNeteaseLog) _awaitingState = true; return false; }
        if (_awaitingState) { _awaitingState = false; _until = until = now + StartWindow; }
        if (track.State != MediaState.Playing) return false;
        _until = null;
        return now <= until;
    }
}
