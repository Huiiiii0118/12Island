using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

internal sealed partial class NeteaseCdpConnection(NeteaseProcess process) : IMediaPlayerConnection
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly string _binding = "__brimDeckMediaPush_" + Guid.NewGuid().ToString("N");
    private ClientWebSocket? _socket;
    private Task? _worker;
    private DesktopMediaSnapshot? _snapshot;
    private int _nextId, _context;
    private volatile bool _ready, _failed;
    public bool Ready => _ready;
    public bool Failed => _failed;
    public DesktopMediaSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public int? ProcessId => process.Id;
    public string? Error { get; private set; }
    public event Action? Changed;
    public Task StartAsync() { _worker ??= Task.Run(SuperviseAsync); return Task.CompletedTask; }

    private async Task SuperviseAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (!_stop.IsCancellationRequested && process.IsAlive())
        {
            try
            {
                await ConnectAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch { Error ??= Loc.T("网易云完整控制连接暂时不可用。", "The full control connection to NetEase Cloud Music is temporarily unavailable."); }
            finally
            {
                bool wasReady = _ready;
                _ready = false; _context = 0;
                Interlocked.Exchange(ref _socket, null)?.Dispose();
                foreach (var request in _requests.Values) request.TrySetException(new IOException("Media connection closed."));
                _requests.Clear();
                var previous = Snapshot;
                Volatile.Write(ref _snapshot, process.IsAlive() && previous is not null ? previous with
                {
                    State = MediaState.Unknown, Position = null, TimelinePending = true,
                    CanToggle = false, CanPrevious = false, CanNext = false, CanSeek = false, CanShuffle = false, CanRepeat = false, CanLike = false
                } : null);
                Notify();
                if (wasReady) deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            }
            if (DateTimeOffset.UtcNow >= deadline) break;
            try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        if (!_stop.IsCancellationRequested) { _failed = true; Notify(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (!OwnsPort(process)) { Error = Loc.T("调试端口未就绪或监听进程不匹配。", "The debugging port is not ready or is held by a different process."); throw new IOException("Debug port is not owned by the player."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 1_048_576 };
        string json = await http.GetStringAsync($"http://127.0.0.1:{process.DebugPort}/json", timeout.Token).ConfigureAwait(false);
        using var pages = JsonDocument.Parse(json);
        if (pages.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        foreach (var page in pages.RootElement.EnumerateArray())
        {
            if (Text(page, "type") != "page" || !Uri.TryCreate(Text(page, "webSocketDebuggerUrl"), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.Port != process.DebugPort ||
                !endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) || !OwnsPort(process)) continue;
            using var socket = new ClientWebSocket(); socket.Options.Proxy = null;
            // Chromium 91's DevTools server rejects PING/PONG frames. The CDP
            // keepalive below checks this connection without protocol-level pings.
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            _context = 0; _socket = socket;
            var receiving = ReceiveAsync(socket, token);
            try
            {
                await RequestAsync("Runtime.enable", new { }, token).ConfigureAwait(false);
                await RequestAsync("Runtime.addBinding", new { name = _binding }, token).ConfigureAwait(false);
                var reply = await EvaluateAsync(BridgeScript.Replace("const binding = '__brimDeckMediaPush';", $"const binding = '{_binding}';", StringComparison.Ordinal), token).ConfigureAwait(false);
                if (!Result(reply, out var result) || !Boolean(result, "ready")) continue;
                if (result.TryGetProperty("snapshot", out var snapshot)) Accept(snapshot);
                _ready = true; Error = null; Notify();
                while (!receiving.IsCompleted)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var delay = Task.Delay(TimeSpan.FromSeconds(30), heartbeat.Token);
                    if (await Task.WhenAny(receiving, delay).ConfigureAwait(false) == receiving) { heartbeat.Cancel(); break; }
                    await delay.ConfigureAwait(false);
                    if (!OwnsPort(process)) throw new IOException("Player generation changed.");
                    var lease = await EvaluateAsync($"window.{BridgeObject} && window.{BridgeObject}.keepalive('{_binding}')", token).ConfigureAwait(false);
                    if (!Result(lease, out var alive) || alive.ValueKind != JsonValueKind.True) throw new IOException("Playback page reloaded.");
                }
                await receiving.ConfigureAwait(false);
                return;
            }
            finally
            {
                // A failed page probe must not leave a receiver behind or keep request tasks alive.
                socket.Abort();
                try { await receiving.ConfigureAwait(false); } catch { }
                if (ReferenceEquals(_socket, socket)) _socket = null;
                foreach (var request in _requests.Values) request.TrySetException(new IOException("Page disconnected."));
                _requests.Clear();
            }
        }
        Error = Loc.T("未找到兼容的网易云播放页面。", "No compatible NetEase Cloud Music player page was found.");
        throw new IOException("No compatible playback page.");
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var bytes = new byte[16384];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            if (read.MessageType == WebSocketMessageType.Close) return;
            if (read.MessageType != WebSocketMessageType.Text || message.Length + read.Count > 1_048_576) throw new InvalidDataException();
            message.Write(bytes, 0, read.Count);
            if (!read.EndOfMessage) continue;
            using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length), new JsonDocumentOptions { MaxDepth = 32 });
            message.SetLength(0);
            var root = json.RootElement;
            if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out int number) && _requests.TryRemove(number, out var request))
                request.TrySetResult(root.Clone());
            else if (Text(root, "method") == "Runtime.bindingCalled" && root.TryGetProperty("params", out var args) && Text(args, "name") == _binding)
            {
                if (args.TryGetProperty("executionContextId", out var context) && context.TryGetInt32(out int contextId))
                {
                    if (_context != 0 && _context != contextId) continue;
                    _context = contextId;
                }
                string payload = Text(args, "payload");
                if (payload.Length > 65536) continue;
                using var snapshot = JsonDocument.Parse(payload);
                Accept(snapshot.RootElement);
            }
            else if (Text(root, "method") == "Runtime.executionContextsCleared" && _ready)
                throw new IOException("Playback page reloaded.");
            else if (Text(root, "method") == "Runtime.executionContextDestroyed" && _ready && root.TryGetProperty("params", out var destroyed) &&
                destroyed.TryGetProperty("executionContextId", out var destroyedId) && destroyedId.TryGetInt32(out int lost) && lost == _context)
                throw new IOException("Playback context destroyed.");
        }
    }
    private Task<JsonElement> EvaluateAsync(string expression, CancellationToken token, Action? dispatching = null) =>
        RequestAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = false }, token, dispatching);

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token, Action? dispatching = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        try
        {
            try
            {
                var socket = _socket;
                if (socket?.State != WebSocketState.Open) throw new IOException("Media socket unavailable.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
                dispatching?.Invoke();
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            }
            finally { _send.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _requests.TryRemove(id, out _); }
    }
    private static bool Result(JsonElement reply, out JsonElement value)
    {
        value = default;
        return !reply.TryGetProperty("error", out _) && reply.TryGetProperty("result", out var response) &&
            !response.TryGetProperty("exceptionDetails", out _) && response.TryGetProperty("result", out var result) && result.TryGetProperty("value", out value);
    }
    private void Accept(JsonElement data)
    {
        var snapshot = ParseSnapshot(data, DateTimeOffset.UtcNow);
        if (snapshot is not null && Interlocked.Exchange(ref _snapshot, snapshot) != snapshot) Notify();
    }
    internal static DesktopMediaSnapshot? ParseSnapshot(JsonElement data, DateTimeOffset now)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        string title = Text(data, "title"), id = Text(data, "songId"), mode = Text(data, "mode");
        if (title.Length == 0 || title.Length > 4096) return null;
        double? duration = Number(data, "durationMs"), position = Number(data, "positionMs");
        if (duration is not (> 0 and <= 604_800_000)) duration = null;
        if (position is not >= 0 || duration is null || position > duration) position = null;
        bool control = Boolean(data, "canControl"), modes = Boolean(data, "canMode");
        return new(title, Text(data, "artist"), Text(data, "album"), position.HasValue ? TimeSpan.FromMilliseconds(position.Value) : null,
            duration.HasValue ? TimeSpan.FromMilliseconds(duration.Value) : null,
            Text(data, "state") switch { "playing" => MediaState.Playing, "paused" => MediaState.Paused, _ => MediaState.Unknown },
            control, control && !Boolean(data, "previousRestricted"), control, position.HasValue && Boolean(data, "canSeek"),
            SongId: id.Length > 0 && id.All(char.IsAsciiDigit) ? "netease:" + id : null,
            CoverUrl: NeteaseLog.SafeCover(Text(data, "coverUrl")), PositionAt: now, TimelinePending: position is null,
            CanShuffle: modes, CanRepeat: modes, Shuffle: mode == "playRandom",
            Repeat: mode switch { "playCycle" => MediaRepeat.List, "playOneCycle" => MediaRepeat.Track, _ => MediaRepeat.None },
            CanLike: Boolean(data, "canLike"), Liked: Boolean(data, "liked"));
    }
    // Liking names the song, so a click that arrives after a skip cannot change the next song.
    public Task<DesktopCommandResult> CommandAsync(MediaCommand command, MediaState state) => command == MediaCommand.Like
        ? Snapshot is { CanLike: true, SongId: { } id } ? SendCommandAsync(new { command = "like", songId = id["netease:".Length..] }) : Task.FromResult(DesktopCommandResult.Unavailable)
        : SendCommandAsync(new
        {
            command = command switch { MediaCommand.PlayPause => "toggle", MediaCommand.Previous => "previous", MediaCommand.Next => "next", MediaCommand.Shuffle => "shuffle", MediaCommand.Repeat => "repeat", _ => "" }
        });
    public Task<DesktopCommandResult> SetPlaybackModeAsync(MusicPlaybackMode mode) => SendCommandAsync(new
    {
        command = "mode", mode = mode switch { MusicPlaybackMode.List => "playCycle", MusicPlaybackMode.Track => "playOneCycle", MusicPlaybackMode.Shuffle => "playRandom", _ => "playOrder" }
    });
    public Task<DesktopCommandResult> SeekAsync(TimeSpan position) => Snapshot is { CanSeek: true, SongId: { } id }
        ? SendCommandAsync(new { command = "seek", seconds = position.TotalSeconds, songId = id["netease:".Length..] })
        : Task.FromResult(DesktopCommandResult.Unavailable);
    private async Task<DesktopCommandResult> SendCommandAsync(object command)
    {
        if (!_ready || _stop.IsCancellationRequested || !OwnsPort(process)) return DesktopCommandResult.Unavailable;
        bool dispatched = false;
        try
        {
            string expression = $"window.{BridgeObject}.command(" + JsonSerializer.Serialize(command) + ")";
            var reply = await EvaluateAsync(expression, _stop.Token, () => dispatched = true).ConfigureAwait(false);
            return Result(reply, out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("dispatched", out var sent)
                ? sent.ValueKind == JsonValueKind.True ? DesktopCommandResult.Sent : DesktopCommandResult.Unavailable
                : DesktopCommandResult.Uncertain;
        }
        catch { return dispatched ? DesktopCommandResult.Uncertain : DesktopCommandResult.Unavailable; }
    }
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double? Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    private static bool Boolean(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private void Notify() { if (!_stop.IsCancellationRequested) Changed?.Invoke(); }
    public void Dispose()
    {
        // The bridge's bounded lease also handles abnormal exits and lost connections.
        _stop.Cancel(); _ready = false; Interlocked.Exchange(ref _socket, null)?.Abort();
    }

    internal static bool OwnsPort(NeteaseProcess owner)
    {
        if (owner.DebugPort is not { } port || !owner.IsAlive()) return false;
        try
        {
            // Reject a non-loopback listener, including an IPv6 wildcard, at this port.
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port && !IPAddress.IsLoopback(e.Address))) return false;
            bool found = false;
            foreach (var listener in TcpListeners.Read().Where(l => l.Port == port))
            {
                if (listener.ProcessId != owner.Id || !listener.Address.Equals(IPAddress.Loopback)) return false;
                found = true;
            }
            return found && owner.IsAlive();
        }
        catch { return false; }
    }
}
