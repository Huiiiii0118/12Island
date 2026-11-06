namespace TwelveIsland.Core;

// A browser can close its Windows media session and open a new one when its page changes songs. Until the
// new session has been read it has no song, so the player would drop out of the list for a moment and the
// panel would show another player's song and cover. The session on screen that disappears is held briefly;
// a new session of the same app within the hold takes over its route, song and artwork.
// Closing the player for good keeps it on screen for at most the hold.
public sealed class MediaSessionHandover
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.5);
    private readonly List<(string Id, string App, DateTimeOffset Until)> _departed = [];

    public void Depart(string id, string app, DateTimeOffset now)
    {
        _departed.RemoveAll(d => d.Id == id);
        _departed.Add((id, app, now + Hold));
    }
    // The route a new session of this app continues, if one of its sessions departed within the hold.
    public string? Claim(string app, DateTimeOffset now)
    {
        Expire(now);
        int index = _departed.FindLastIndex(d => string.Equals(d.App, app, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var id = _departed[index].Id; _departed.RemoveAt(index);
        return id;
    }
    public bool Holds(string id, DateTimeOffset now) { Expire(now); return _departed.Any(d => d.Id == id); }
    public DateTimeOffset? NextExpiry => _departed.Count == 0 ? null : _departed.Min(d => d.Until);
    private void Expire(DateTimeOffset now) => _departed.RemoveAll(d => d.Until <= now);
}
