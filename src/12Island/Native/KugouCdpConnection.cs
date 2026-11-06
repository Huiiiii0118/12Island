using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text.Json;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// KuGou's full control: once its libcef.dll is patched (KugouInstall), each KuGou opens its DevTools port at 127.0.0.1:12233
// and this connection reads what plays from one of its pages, through the bridge its pages use to call the client
// (external.SuperCall). 864 returns the playing song, its state and position; 841 plays, pauses and changes songs. The
// pages have no call that seeks, switches the playback mode or adds a liked song (searched in their scripts and in
// kugou.dll), so those stay unavailable; KuGou's window is not operated instead. Without the port there is no snapshot, and
// KuGou's own system media session stands on its own.
internal sealed class KugouCdpConnection : IMediaPlayerConnection
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _supervisor;
    private ClientWebSocket? _socket;
    private DesktopMediaSnapshot? _snapshot;
    private Owner? _owner;
    private int _nextId;
    // Ports of a KuGou process that answered as something other than a DevTools server; they are not asked again.
    private readonly HashSet<(int Id, long Started, int Port)> _rejected = [];
    private sealed record Owner(int Id, long Started, int Port);
    public DesktopMediaSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public int? ProcessId => _owner?.Id;
    internal string Identity => _owner is { } owner ? $"kugou:{owner.Id}:{owner.Started}" : "kugou";
    private volatile bool _bridged;
    public bool Ready => _bridged && _socket is { State: WebSocketState.Open };
    public string? Error => null;
    public event Action? Changed;
    public Task StartAsync() { _supervisor ??= Task.Run(SuperviseAsync); return Task.CompletedTask; }

    // A tick of the play info stays wanted here: KuGou reports no event when the position moves.
    private const int PollMilliseconds = 1000;
    private async Task SuperviseAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (await FindAsync(_stop.Token).ConfigureAwait(false) is { } owner) { _owner = owner; await ConnectAsync(owner, _stop.Token).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch { }
            finally
            {
                _bridged = false; Interlocked.Exchange(ref _socket, null)?.Abort();
                _owner = null; Publish(null);
            }
            try { await Task.Delay(3000, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // A DevTools server of KuGou at the loopback address. Other tools that patch KuGou the same way open the same port, and
    // others open one of their own, so every port a KuGou process alone listens on at 127.0.0.1 is asked, 12233 first.
    // A port that is also open beyond this PC is never used.
    // The ports are read only while KuGou runs: without it every 3 seconds would read the port tables for nothing.
    private async Task<Owner?> FindAsync(CancellationToken token)
    {
        var kugou = ProcessSnapshot.Ids("KuGou.exe");
        if (kugou.Count == 0) return null;
        foreach (var candidate in Candidates(kugou))
        {
            if (_rejected.Contains((candidate.Id, candidate.Started, candidate.Port))) continue;
            if (await AnswersAsDevToolsAsync(candidate.Port, token).ConfigureAwait(false) && Owns(candidate)) return candidate;
            // KuGou's own service opens new ports now and then; the list is started again rather than kept growing.
            if (_rejected.Count >= 256) _rejected.Clear();
            _rejected.Add((candidate.Id, candidate.Started, candidate.Port));
        }
        return null;
    }
    // kugou: the ids of the KuGou processes; only their ports are looked at, so no other program's process is opened.
    private static List<Owner> Candidates(HashSet<int> kugou)
    {
        var found = new List<Owner>();
        try
        {
            var open = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => !IPAddress.IsLoopback(e.Address)).Select(e => e.Port).ToHashSet();
            foreach (var group in TcpListeners.Read().GroupBy(l => l.Port))
            {
                var first = group.First();
                if (!kugou.Contains(first.ProcessId) || open.Contains(group.Key) || group.Any(l => !l.Address.Equals(IPAddress.Loopback) || l.ProcessId != first.ProcessId)) continue;
                try
                {
                    using var process = Process.GetProcessById(first.ProcessId);
                    if (!process.HasExited)
                        found.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, group.Key));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
        catch { }
        return found.OrderBy(o => o.Port == KugouPatch.Port ? 0 : 1).ToList();
    }
    private static bool Owns(Owner owner) => Candidates(ProcessSnapshot.Ids("KuGou.exe")).Contains(owner);
    private static async Task<bool> AnswersAsDevToolsAsync(int port, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 65_536 };
            using var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", timeout.Token).ConfigureAwait(false));
            return Uri.TryCreate(Text(version.RootElement, "webSocketDebuggerUrl"), UriKind.Absolute, out var browser)
                && browser.Scheme == "ws" && browser.Host == "127.0.0.1" && browser.Port == port;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { return false; }
    }

    private async Task ConnectAsync(Owner owner, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 1_048_576 };
        using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{owner.Port}/json", timeout.Token).ConfigureAwait(false));
        if (pages.RootElement.ValueKind != JsonValueKind.Array) return;
        // KuGou's own pages first; every one of them carries the bridge.
        var endpoints = pages.RootElement.EnumerateArray()
            .Where(p => Text(p, "type") == "page" && Uri.TryCreate(Text(p, "webSocketDebuggerUrl"), UriKind.Absolute, out var u) && u.Scheme == "ws"
                && u.Host == "127.0.0.1" && u.Port == owner.Port && u.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal))
            .OrderBy(p => Uri.TryCreate(Text(p, "url"), UriKind.Absolute, out var page) && page.Host.EndsWith(".kugou.com", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(p => new Uri(Text(p, "webSocketDebuggerUrl"))).ToArray();
        foreach (var endpoint in endpoints)
        {
            if (!Owns(owner)) return;
            var socket = new ClientWebSocket(); socket.Options.Proxy = null; socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            try { await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false); }
            catch { socket.Dispose(); continue; }
            _socket = socket;
            if (await EvaluateAsync("typeof external === 'object' && typeof external.SuperCall === 'function'", false, token).ConfigureAwait(false) is not { ValueKind: JsonValueKind.True })
            { Interlocked.Exchange(ref _socket, null)?.Abort(); socket.Dispose(); continue; }
            _bridged = true;
            long checkedAt = Environment.TickCount64;
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                // A reply that is not the play info (a page that went away) ends this page; the supervisor looks again.
                var info = await EvaluateAsync(PlayInfo, true, token).ConfigureAwait(false);
                if (info is not { ValueKind: JsonValueKind.String } text) break;
                if (text.GetString() is { Length: > 0 and <= 65536 } json)
                {
                    using var data = JsonDocument.Parse(json);
                    Publish(ParseSnapshot(data.RootElement, DateTimeOffset.UtcNow));
                }
                else Publish(null);
                if (Environment.TickCount64 - checkedAt > 30_000) { if (!Owns(owner)) break; checkedAt = Environment.TickCount64; }
                try { await _wake.WaitAsync(PollMilliseconds, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            _bridged = false; Interlocked.Exchange(ref _socket, null)?.Abort(); socket.Dispose();
            return;
        }
    }

    private static readonly string PlayInfo = Call(864);
    // The client answers through a named callback, given with the call's own fields; a reply that does not come within
    // 3 seconds counts as none.
    internal static string Call(int number, object? fields = null) => $$"""
        new Promise(function (resolve) {
            var name = "__brimDeckKugou" + Date.now() + Math.random().toString(36).slice(2);
            var timer = setTimeout(function () { window[name] = null; resolve(""); }, 3000);
            window[name] = function (data) { clearTimeout(timer); window[name] = null; resolve(typeof data === "string" ? data : JSON.stringify(data)); };
            try { external.SuperCall({{number}}, JSON.stringify(Object.assign({{JsonSerializer.Serialize(fields ?? new { })}}, { callback: name }))); }
            catch (e) { clearTimeout(timer); window[name] = null; resolve(""); }
        })
        """;
    // Kept between requests, which come one at a time (_exchange); the message is dropped once a large reply has grown it.
    private readonly byte[] _buffer = new byte[16384];
    private MemoryStream _message = new();
    // One request at a time: each waits for its own reply, so the page's other messages need no dispatcher.
    private async Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken token, Action? dispatching = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _exchange.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) return null;
            int id = Interlocked.Increment(ref _nextId);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, awaitPromise } });
            dispatching?.Invoke();
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            var buffer = _buffer;
            var message = _message;
            message.SetLength(0);
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (read.MessageType == WebSocketMessageType.Close) return null;
                if (message.Length + read.Count > 1_048_576) return null;
                message.Write(buffer, 0, read.Count);
                if (!read.EndOfMessage) continue;
                using var reply = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length), new JsonDocumentOptions { MaxDepth = 32 });
                message.SetLength(0);
                var root = reply.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out int number) || number != id) continue;
                return root.TryGetProperty("result", out var result) && !result.TryGetProperty("exceptionDetails", out _)
                    && result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var returned) ? returned.Clone() : null;
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested) { return null; }
        finally
        {
            if (_message.Capacity > 65_536) _message = new();
            _exchange.Release();
        }
    }

    internal static DesktopMediaSnapshot? ParseSnapshot(JsonElement data, DateTimeOffset now)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        // "Artist - Title"; several artists are joined by "、" within the first part.
        string name = Text(data, "filename").Trim();
        int split = name.IndexOf(" - ", StringComparison.Ordinal);
        string artist = split > 0 ? name[..split].Trim() : "", title = split > 0 ? name[(split + 3)..].Trim() : name;
        if (title.Length is 0 or > 4096) return null;
        // The duration is in milliseconds, the position in units of 100 nanoseconds (measured against KuGou's own clock).
        double? duration = Number(data, "duration"), position = Number(data, "progress") / 10_000;
        if (duration is not (> 0 and <= 604_800_000)) duration = null;
        if (position is not >= 0 || duration is null || position > duration) position = null;
        // "pasued" is how KuGou spells a paused song.
        var state = Text(data, "play_status") switch { "playing" => MediaState.Playing, "paused" or "pasued" => MediaState.Paused, "stopped" => MediaState.Stopped, _ => MediaState.Unknown };
        string hash = Text(data, "hash").ToLowerInvariant();
        return new(title, artist, "", position.HasValue ? TimeSpan.FromMilliseconds(position.Value) : null, duration.HasValue ? TimeSpan.FromMilliseconds(duration.Value) : null,
            state, true, true, true, false,
            SongId: hash.Length == 32 && hash.All(char.IsAsciiHexDigit) ? "kugou:" + hash : null,
            CoverUrl: Cover(Text(data, "cover")), PositionAt: now, TimelinePending: position is null);
    }

    // KuGou names its 120-pixel artwork; the same picture is served at 480 pixels. Only KuGou's own image hosts are asked.
    internal static string? Cover(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.EndsWith(".kugou.com", StringComparison.OrdinalIgnoreCase)) return null;
        var secure = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        secure.Path = secure.Path.Replace("/stdmusic/120/", "/stdmusic/480/", StringComparison.Ordinal);
        return secure.Uri.AbsoluteUri;
    }

    public async Task<DesktopCommandResult> CommandAsync(MediaCommand command, MediaState state)
    {
        string? status = command switch
        {
            MediaCommand.PlayPause => state == MediaState.Playing ? "pause" : "play",
            MediaCommand.Next => "next", MediaCommand.Previous => "prev", _ => null
        };
        if (status is null || !Ready) return DesktopCommandResult.Unavailable;
        bool dispatched = false;
        var reply = await EvaluateAsync($"external.SuperCall(841, JSON.stringify({{ status: \"{status}\" }})), true", false, _stop.Token, () => dispatched = true).ConfigureAwait(false);
        // The new state is read at once rather than at the next tick.
        Wake();
        return reply is { ValueKind: JsonValueKind.True } ? DesktopCommandResult.Sent : dispatched ? DesktopCommandResult.Uncertain : DesktopCommandResult.Unavailable;
    }
    public Task<DesktopCommandResult> SeekAsync(TimeSpan position) => Task.FromResult(DesktopCommandResult.Unavailable);
    private void Wake() { if (_wake.CurrentCount == 0) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }

    // A reading that only confirms where the song should be by now keeps the previous one, so a playing song does not
    // rebuild the panel every second.
    private void Publish(DesktopMediaSnapshot? snapshot)
    {
        var previous = Volatile.Read(ref _snapshot);
        if (previous is not null && snapshot is not null && Steady(previous, snapshot)) return;
        Volatile.Write(ref _snapshot, snapshot);
        if (previous != snapshot && !_stop.IsCancellationRequested) Changed?.Invoke();
    }
    internal static bool Steady(DesktopMediaSnapshot previous, DesktopMediaSnapshot next)
    {
        if (previous with { Position = next.Position, PositionAt = next.PositionAt } != next || previous.Position is not { } then || next.Position is not { } now) return false;
        var expected = previous.State == MediaState.Playing ? then + (next.PositionAt!.Value - previous.PositionAt!.Value) : then;
        return Math.Abs((now - expected).TotalMilliseconds) < 1500;
    }
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    // KuGou sends its numbers as strings.
    private static double? Number(JsonElement obj, string key) => double.TryParse(Text(obj, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
    public void Dispose() { _stop.Cancel(); Interlocked.Exchange(ref _socket, null)?.Abort(); }
}
