namespace TwelveIsland.Core;

public sealed record MediaStackDecision(string? Current, string? Started, DateTimeOffset? RecheckAt);

// The most recently started source is shown first. Stopping any source, at the top or
// below it, removes it from the playing set; the next most recent playing source takes over.
// A source that stops only briefly (a song change or buffering) keeps its original place,
// so a finishing song in an older player cannot jump above a newer one.
public sealed class MediaPlaybackStack
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private sealed class Entry
    {
        public string Source = "";
        public long Order;
        public bool Playing;
        public DateTimeOffset? StoppedAt;
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private long _next;

    // A pinned route can change identity for the same player, for example when the QQ pipe
    // hands over to the Windows session. A newly appeared route of the same source keeps the
    // pin; otherwise the pinned player has closed and the pin is released.
    public static string? FollowPin(string? pinned, string? source, IReadOnlyList<MediaTrack> tracks, IReadOnlyList<MediaTrack> previous)
    {
        if (pinned is null || tracks.Any(t => t.Id == pinned)) return pinned;
        return tracks.FirstOrDefault(t => t.Source == source && previous.All(p => p.Id != t.Id))?.Id;
    }

    // A source that stopped within the grace period is between songs or buffering, as far as the last Update knows.
    public bool BrieflyStopped(string id, DateTimeOffset now) =>
        _entries.TryGetValue(id, out var entry) && !entry.Playing && entry.StoppedAt is { } stopped && now - stopped <= Grace;

    public MediaStackDecision Update(IReadOnlyList<MediaTrack> tracks, DateTimeOffset now, string? systemCurrent = null, string? previous = null)
    {
        var live = tracks.Select(t => t.Id).ToHashSet();
        var vanished = _entries.Where(e => !live.Contains(e.Key)).ToList();
        foreach (var (id, _) in vanished) _entries.Remove(id);
        string? started = null;
        // Sources already playing when first seen have no known start order. The Windows
        // current session is treated as the most recent, so it is processed last.
        foreach (var track in tracks.OrderBy(t => t.Id == systemCurrent))
        {
            bool playing = track.State == MediaState.Playing;
            if (!_entries.TryGetValue(track.Id, out var entry))
            {
                // A route can change its identity for the same player, for example when the
                // QQ pipe disconnects and the Windows session takes over. It keeps its place.
                int inherited = vanished.FindIndex(e => e.Value.Source == track.Source);
                if (inherited >= 0) { entry = vanished[inherited].Value; vanished.RemoveAt(inherited); }
                else entry = new Entry { Source = track.Source };
                _entries[track.Id] = entry;
            }
            if (playing && !entry.Playing)
            {
                bool resumedWithinGrace = entry.StoppedAt is { } stopped && now - stopped <= Grace;
                if (!resumedWithinGrace) { entry.Order = ++_next; started = track.Id; }
                entry.StoppedAt = null;
            }
            else if (!playing && entry.Playing) entry.StoppedAt = now;
            entry.Playing = playing;
        }

        bool Held(Entry e) => e.Playing || e.StoppedAt is { } stopped && now - stopped <= Grace;
        var active = tracks.Where(t => Held(_entries[t.Id])).OrderByDescending(t => _entries[t.Id].Order).FirstOrDefault();
        // With nothing playing, keep the source that was on screen instead of jumping to
        // an older paused player.
        var current = active?.Id
            ?? tracks.FirstOrDefault(t => t.Id == previous)?.Id
            ?? tracks.OrderByDescending(t => _entries[t.Id].Order).FirstOrDefault()?.Id;
        var recheck = _entries.Values.Where(e => !e.Playing && e.StoppedAt is { } stopped && now - stopped <= Grace)
            .Select(e => (DateTimeOffset?)(e.StoppedAt!.Value + Grace)).Min();
        return new(current, started, recheck);
    }
}
