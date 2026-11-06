using TwelveIsland.Core;

namespace TwelveIsland.Native;

internal sealed record DesktopMediaSnapshot(string Title, string Artist, string Album,
    TimeSpan? Position, TimeSpan? Duration, MediaState? State,
    bool CanToggle, bool CanPrevious, bool CanNext, bool CanSeek,
    string? SongId = null, byte[]? Cover = null,
    bool CanShuffle = false, bool CanRepeat = false, bool? Shuffle = null, MediaRepeat? Repeat = null,
    string? CoverUrl = null, string? SyncedLyrics = null, DateTimeOffset? PositionAt = null, bool TimelinePending = false,
    double Rate = 1, bool IsNeteaseLog = false, bool PreviousRestricted = false, string? CurrentLyric = null, TimeSpan? CurrentLyricStart = null,
    bool CanLike = false, bool Liked = false);

// Only Unavailable guarantees that dispatch did not occur.
internal enum DesktopCommandResult { Unavailable, Sent, Uncertain }

internal interface IMediaPlayerConnection : IDisposable
{
    event Action? Changed;
    DesktopMediaSnapshot? Snapshot { get; }
    int? ProcessId { get; }
    string? Error { get; }
    Task StartAsync();
    Task<DesktopCommandResult> CommandAsync(MediaCommand command, MediaState state);
    Task<DesktopCommandResult> SetPlaybackModeAsync(MusicPlaybackMode mode) => Task.FromResult(DesktopCommandResult.Unavailable);
    Task<DesktopCommandResult> SeekAsync(TimeSpan position);
}
