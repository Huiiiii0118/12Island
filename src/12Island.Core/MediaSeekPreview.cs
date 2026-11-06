namespace TwelveIsland.Core;

// Dispatch only confirms that a command was sent. Keep its visual target separate
// from native progress until the player reports the new position.
public sealed class MediaSeekPreview
{
    public sealed record Request(MediaTrack Track, TimeSpan Target, DateTimeOffset StartedAt);
    public Request? Pending { get; private set; }

    public Request Begin(MediaTrack track, double fraction, DateTimeOffset now)
        => Pending = new(track, TimeSpan.FromTicks(track.SeekTicks(fraction)), now);

    public bool Cancel(Request request)
    {
        if (!ReferenceEquals(Pending, request)) return false;
        Pending = null;
        return true;
    }

    public void Observe(MediaTrack? track, DateTimeOffset now)
    {
        if (Pending is not { } request) return;
        if (track is null || track.Id != request.Track.Id || !MediaRouting.SameSong(track, request.Track) ||
            !string.IsNullOrEmpty(track.SongId) && !string.IsNullOrEmpty(request.Track.SongId) && track.SongId != request.Track.SongId ||
            !track.HasTimeline || !track.CanSeek || track.State is MediaState.Unknown or MediaState.Stopped ||
            now - request.StartedAt >= TimeSpan.FromSeconds(5))
        {
            Pending = null;
            return;
        }
        // Use an actual new sample, never a position extrapolated by the UI clock.
        if (track.PositionAt < request.StartedAt || track.PositionAt <= request.Track.PositionAt) return;
        double elapsed = track.State == MediaState.Playing
            ? Math.Max(0, (track.PositionAt - request.StartedAt).TotalSeconds) * (double.IsFinite(track.Rate) ? Math.Max(0, track.Rate) : 1) : 0;
        double difference = (track.ReportedPosition - request.Target).TotalSeconds;
        if (difference >= -.75 && difference <= elapsed + .75) Pending = null;
    }

    public TimeSpan Position(MediaTrack track, DateTimeOffset now)
    {
        Observe(track, now);
        return Pending is { } request
            ? TimeSpan.FromSeconds(Math.Clamp((request.Target - track.Start).TotalSeconds, 0, track.Duration.TotalSeconds))
            : track.Position(now);
    }
}
