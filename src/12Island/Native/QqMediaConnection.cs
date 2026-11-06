using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TwelveIsland.Core;
using Microsoft.Win32.SafeHandles;

namespace TwelveIsland.Native;

// QQ Music's own transport publishes media changes. No window scanning, DLL injection,
// player launch, account access, keyboard input, or memory offsets are needed for the pipe.
internal sealed class QqMediaConnection : IMediaPlayerConnection
{
    private const string PipeName = @"y.qq.com\QQMusicTransportServer";
    private const int MaximumMessageBytes = 65_536;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly object _snapshotGate = new();
    private NamedPipeClientStream? _pipe;
    private Task? _supervisor;
    private DesktopMediaSnapshot? _snapshot;
    private int _pid, _disposing;
    private long _processStart;
    private volatile bool _ready;
    private TaskCompletionSource? _handshake;
    private readonly byte[] _login = Encoding.UTF8.GetBytes("{\"cmd\":\"login\",\"params\":{\"appName\":\"TwelveIsland\",\"appKey\":\"TwelveIsland\"}}\n");

    public bool Ready => _ready;
    public DesktopMediaSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    internal string Identity => $"qq:{_pid}:{_processStart}";
    public int? ProcessId => Volatile.Read(ref _pid) is > 0 and var id ? id : null;
    public string? Error { get; private set; }
    public event Action? Changed;

    public Task StartAsync()
    {
        lock (_lifecycle)
            if (_disposing == 0) _supervisor ??= Task.Run(SuperviseAsync);
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await ConnectAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Error = ex.Message; }
            finally
            {
                _ready = false;
                Interlocked.Exchange(ref _pipe, null)?.Dispose();
                _handshake?.TrySetCanceled();
                var previous = Snapshot;
                if (previous is not null)
                {
                    // Keep the song during a transport interruption, but never retain live
                    // progress or enabled commands after the transport has disconnected.
                    bool alive = ProcessStillMatches();
                    Volatile.Write(ref _snapshot, alive ? previous with { State = MediaState.Unknown, Position = null, PositionAt = null,
                        TimelinePending = false, CurrentLyric = null, CurrentLyricStart = null,
                        CanToggle = false, CanPrevious = false, CanNext = false, CanSeek = false, CanShuffle = false, CanRepeat = false } : null);
                    if (!alive) Volatile.Write(ref _pid, 0);
                    Notify();
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync()
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        connecting.CancelAfter(1500);
        // One attempt only: without a timeout the runtime spins on a missing pipe until cancellation.
        // The supervisor already retries every five seconds while QQ Music is closed.
        await pipe.ConnectAsync(0, connecting.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue)
            throw new InvalidDataException("QQ Music pipe has no identifiable owner.");
        using (var process = Process.GetProcessById((int)pid))
        {
            if (process.HasExited || !string.Equals(process.ProcessName, "QQMusic", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(process.MainModule?.FileName), "QQMusic.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected QQ Music pipe owner.");
            long started = process.StartTime.ToUniversalTime().Ticks;
            if (_pid != (int)pid || _processStart != started) Volatile.Write(ref _snapshot, null);
            _processStart = started;
            Volatile.Write(ref _pid, (int)pid);
        }
        _pipe = pipe;
        _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = ReceiveAsync(pipe);
        try
        {
            await pipe.WriteAsync(_login, connecting.Token).ConfigureAwait(false);
            await _handshake.Task.WaitAsync(connecting.Token).ConfigureAwait(false);
            Error = null;
            await receiving.ConfigureAwait(false);
        }
        finally
        {
            lock (_snapshotGate)
            {
                _ready = false;
            }
            pipe.Dispose();
            try { await receiving.ConfigureAwait(false); } catch { }
        }
    }

    private async Task ReceiveAsync(NamedPipeClientStream pipe)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!_stop.IsCancellationRequested)
        {
            int count = await pipe.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
            if (count == 0) return;
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                Append(buffer.AsSpan(start, i - start));
                if (message.Length > 0)
                {
                    using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length),
                        new JsonDocumentOptions { MaxDepth = 16 });
                    Accept(json.RootElement);
                }
                message.SetLength(0);
                start = i + 1;
            }
            Append(buffer.AsSpan(start, count - start));
        }

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (message.Length + bytes.Length > MaximumMessageBytes)
                throw new InvalidDataException("QQ Music media message exceeds its limit.");
            message.Write(bytes);
        }
    }

    private void Accept(JsonElement root)
    {
        lock (_snapshotGate) AcceptCore(root);
    }

    private void AcceptCore(JsonElement root)
    {
        if (Text(root, "reply") == "login")
        {
            if (!Integer(root, "code", out int code) || code != 0)
                throw new InvalidDataException("QQ Music did not accept the media subscription.");
            _ready = true;
            _handshake?.TrySetResult();
            return;
        }
        if (!_ready || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var previous = Snapshot;
        var next = (previous ?? new("", "", "", null, null, null, true, true, true, false))
            with { CanToggle = true, CanPrevious = true, CanNext = true };
        switch (Text(root, "notify"))
        {
            case "song":
                string title = Text(data, "title"), artist = Text(data, "singer"), album = Text(data, "album");
                string? cover = SafeCoverUrl(Text(data, "picurl"));
                string? songId = ReadSongId(data);
                bool changed = title != next.Title || artist != next.Artist || album != next.Album ||
                    songId is not null && next.SongId is not null && songId != next.SongId;
                next = next with
                {
                    Title = title, Artist = artist, Album = album, CoverUrl = cover ?? (changed ? null : next.CoverUrl),
                    Position = changed ? null : next.Position, Duration = changed ? null : next.Duration,
                    PositionAt = changed ? null : next.PositionAt, CanSeek = !changed && next.CanSeek,
                    TimelinePending = changed || next.TimelinePending,
                    SongId = songId, SyncedLyrics = changed ? null : next.SyncedLyrics,
                    CurrentLyric = changed ? null : next.CurrentLyric,
                    CurrentLyricStart = changed ? null : next.CurrentLyricStart
                };
                break;
            case "status":
                if (!Integer(data, "value", out int status)) return;
                // These are the Windows MediaPlaybackStatus values, also used by QQ's SMTC publisher.
                MediaState state = status switch { 2 => MediaState.Stopped, 3 => MediaState.Playing, 4 => MediaState.Paused, _ => MediaState.Unknown };
                var now = DateTimeOffset.UtcNow;
                // Freeze the last advancing sample immediately on pause; the subsequent native
                // progress event supplies its exact position, without changing the song identity.
                var position = next.Position;
                if (position.HasValue && next.State == MediaState.Playing && next.PositionAt.HasValue && next.Duration.HasValue)
                    position = TimeSpan.FromMilliseconds(Math.Clamp(position.Value.TotalMilliseconds +
                        (now - next.PositionAt.Value).TotalMilliseconds, 0, next.Duration.Value.TotalMilliseconds));
                next = next with { State = state, Position = position, PositionAt = now,
                    CurrentLyric = state is MediaState.Stopped or MediaState.Unknown ? null : next.CurrentLyric,
                    CurrentLyricStart = state is MediaState.Stopped or MediaState.Unknown ? null : next.CurrentLyricStart };
                break;
            case "lyric":
                // QQ publishes the current line, not a complete LRC. Keep it as a
                // QQ-only fallback while the complete lyric request is unavailable.
                if (previous is null || !data.TryGetProperty("text", out var lyric) || lyric.ValueKind != JsonValueKind.String) return;
                string text = lyric.GetString()!.Trim();
                next = next with { CurrentLyric = text,
                    CurrentLyricStart = ReadLyricStart(data, text) ?? (text == next.CurrentLyric ? next.CurrentLyricStart : null) };
                break;
            case "progress":
                if (!Integer(data, "duration", out int duration) || !Integer(data, "playtime", out int played) ||
                    duration < 0 || duration > 604_800_000 || played < 0 || played > duration) return;
                next = next with
                {
                    Duration = duration > 0 ? TimeSpan.FromMilliseconds(duration) : null,
                    Position = duration > 0 ? TimeSpan.FromMilliseconds(played) : null,
                    PositionAt = DateTimeOffset.UtcNow, CanSeek = duration > 0, TimelinePending = duration <= 0
                };
                break;
            default:
                return;
        }
        if (next.Title.Length == 0) return;
        if (next != previous) { Volatile.Write(ref _snapshot, next); Notify(); }
    }

    public Task<DesktopCommandResult> CommandAsync(MediaCommand command, MediaState state)
    {
        string? name = command switch
        {
            MediaCommand.PlayPause => "playorpause", MediaCommand.Previous => "prev", MediaCommand.Next => "next", _ => null
        };
        return name is null ? Task.FromResult(DesktopCommandResult.Unavailable) : SendAsync(name, null);
    }

    public Task<DesktopCommandResult> SeekAsync(TimeSpan position)
    {
        var snapshot = Snapshot;
        if (snapshot is not { CanSeek: true, Duration: { } duration } || duration.TotalMilliseconds < 1 ||
            position < TimeSpan.Zero) return Task.FromResult(DesktopCommandResult.Unavailable);
        int milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, duration.TotalMilliseconds - 1);
        return SendAsync("seek", milliseconds);
    }

    private async Task<DesktopCommandResult> SendAsync(string command, int? value)
    {
        if (!_ready || _stop.IsCancellationRequested || !OwnerStillMatches()) return DesktopCommandResult.Unavailable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(1000);
        bool entered = false, dispatched = false;
        try
        {
            await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var pipe = _pipe;
            if (!_ready || pipe is not { IsConnected: true } || !OwnerStillMatches()) return DesktopCommandResult.Unavailable;
            string json = value.HasValue
                ? JsonSerializer.Serialize(new { cmd = command, @params = new { value = value.Value } })
                : JsonSerializer.Serialize(new { cmd = command, @params = new { } });
            byte[] packet = Encoding.UTF8.GetBytes(json + "\n");
            dispatched = true;
            await pipe.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
            return DesktopCommandResult.Sent;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _ready = false;
            _pipe?.Dispose();
            return dispatched ? DesktopCommandResult.Uncertain : DesktopCommandResult.Unavailable;
        }
        finally { if (entered) _send.Release(); }
    }

    private bool OwnerStillMatches()
    {
        try
        {
            var pipe = _pipe;
            if (pipe is not { IsConnected: true } || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint owner) || owner != (uint)_pid) return false;
            return ProcessStillMatches();
        }
        catch { return false; }
    }
    private bool ProcessStillMatches()
    {
        try { using var process = Process.GetProcessById(_pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == _processStart; }
        catch { return false; }
    }

    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Integer(JsonElement obj, string key, out int number)
    {
        number = 0;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    internal static string? ReadSongId(JsonElement data)
    {
        foreach (string key in new[] { "songmid", "mid" })
        {
            string mid = Text(data, key);
            if (mid.Length is >= 8 and <= 32 && mid.All(char.IsAsciiLetterOrDigit)) return "qq:" + mid;
        }
        foreach (string key in new[] { "songid", "id" })
            if (data.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                value.ToString() is { Length: > 0 and <= 16 } id && id.All(char.IsAsciiDigit) && id[0] != '0') return "qqid:" + id;
        return null;
    }

    private static TimeSpan? ReadLyricStart(JsonElement data, string text)
    {
        if (text.Length == 0 || !data.TryGetProperty("karaoke", out var words) || words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0) return null;
        // Only a complete word list establishes the line start. A single active word
        // must not move the anchor forward on every karaoke update.
        string fullText = string.Concat(words.EnumerateArray().Select(word => Text(word, "text")));
        if (string.Concat(fullText.Where(char.IsLetterOrDigit)) != string.Concat(text.Where(char.IsLetterOrDigit)) ||
            !Integer(words[0], "start", out int start) || start < 0 || start > 604_800_000) return null;
        return TimeSpan.FromMilliseconds(start);
    }

    private static string? SafeCoverUrl(string value) => value.Length <= 4096 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        (uri.Host.Equals("y.gtimg.cn", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".gtimg.cn", StringComparison.OrdinalIgnoreCase))
        ? value : null;

    private void Notify() { try { Changed?.Invoke(); } catch { } }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
            _ready = false;
            _stop.Cancel();
            Interlocked.Exchange(ref _pipe, null)?.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
