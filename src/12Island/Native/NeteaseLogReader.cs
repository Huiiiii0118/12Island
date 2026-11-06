using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using TwelveIsland.Core;
using Microsoft.Win32.SafeHandles;

namespace TwelveIsland.Native;

// One reader belongs to one main-process generation. The owner disposes it as soon
// as the process exits or CDP takes over. No timers or file handles survive disposal.
// The player appends through a handle it keeps open. Windows reports the growth of such a file to
// directory watchers only when some process opens it, which made changes arrive up to 5 s late (measured).
// The length read through a newly opened handle is always current, so the reader opens the file briefly
// at every poll. It never keeps it open: a handle held between polls makes replacing the file fail,
// which would stop the player from rotating its log. The watcher only reports renames and deletions early.
internal sealed class NeteaseLogReader : IDisposable
{
    // While a song plays or loads a track change reaches the panel within this interval; paused or idle it may wait longer.
    private const int ActivePoll = 250, IdlePoll = 750;
    private const long AliveInterval = 2000;
    private const long TailBytes = 2 * 1024 * 1024;
    // A song that has played or sat paused for a long time can have its start record
    // further back than the tail. Startup widens the replay for it, but never past this.
    private const long MaxReplayBytes = 32 * 1024 * 1024;
    private readonly NeteaseProcess _process;
    private readonly string _path;
    private readonly long _tailBytes, _maxReplayBytes;
    private string _lastSongId = "";
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly NeteaseTrackCache _tracks = new();
    private readonly object _watchGate = new();
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private long _aliveAt;
    private long _offset = -1;
    private (uint Volume, uint High, uint Low)? _file;
    private bool _discardLine;
    private NeteaseLogState _state = new();
    public NeteaseLogState State => Volatile.Read(ref _state);
    public event Action? Changed;
    internal bool Watching => _watcher is not null && !_stop.IsCancellationRequested;
    internal long Offset => Interlocked.Read(ref _offset);

    internal long ReplayStart { get; private set; } = -1;
    public NeteaseLogReader(NeteaseProcess process, string? path = null, long tailBytes = TailBytes, long maxReplayBytes = MaxReplayBytes)
    {
        _process = process; _tailBytes = tailBytes; _maxReplayBytes = Math.Max(tailBytes, maxReplayBytes);
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEase", "CloudMusic", "cloudmusic.elog");
    }
    public void Start() => _worker ??= Task.Run(RunAsync);
    private void Wake(object sender, FileSystemEventArgs args) => _wake.Writer.TryWrite(true);
    private void Watch()
    {
        lock (_watchGate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_watcher is not null || !Directory.Exists(Path.GetDirectoryName(_path))) return;
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
            { NotifyFilter = NotifyFilters.FileName };
            watcher.Created += Wake; watcher.Renamed += Wake; watcher.Deleted += Wake;
            watcher.Error += (_, _) => _wake.Writer.TryWrite(true);
            _watcher = watcher; watcher.EnableRaisingEvents = true;
        }
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && Alive())
            {
                try { Watch(); await ReadAsync(_stop.Token).ConfigureAwait(false); }
                catch (IOException) { Invalidate(); }
                catch (UnauthorizedAccessException) { Invalidate(); }
                using var check = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                check.CancelAfter(State.Track?.State is MediaState.Playing or MediaState.Unknown || State.Buffering ? ActivePoll : IdlePoll);
                try { await _wake.Reader.ReadAsync(check.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_watchGate) { _watcher?.Dispose(); _watcher = null; } }
    }
    // Asking for the process at every poll would open it four times a second.
    private bool Alive()
    {
        long now = Environment.TickCount64;
        if (now - _aliveAt < AliveInterval) return true;
        _aliveAt = now; return _process.IsAlive();
    }
    private async Task ReadAsync(CancellationToken token)
    {
        // Open for this poll only; rotation and the player's own cleanup stay possible.
        if (!File.Exists(_path)) { Invalidate(); return; }
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous);
        var identity = GetFileInformationByHandle(stream.SafeFileHandle, out var info) ? (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow) : ((uint, uint, uint)?)null;
        long length = stream.Length;
        if (length == _offset && identity == _file) return;
        var original = State;
        var state = original;
        if (_offset < 0 || length < _offset || identity != _file)
        {
            _file = identity;
            // This player's playback events without the start record of their song leave no
            // media. Double the replay window until that record is inside it, the file start
            // is reached or the window reaches its maximum.
            for (long window = _tailBytes; ; window = Math.Min(window * 2, _maxReplayBytes))
            {
                long start = Math.Max(0, length - window);
                _offset = start; ReplayStart = start; _lastSongId = "";
                _decoder.Reset(); _line.Clear(); _tracks.Clear(); _discardLine = start > 0;
                state = await ReadLinesAsync(stream, new(), length, token).ConfigureAwait(false);
                if (state.Track is not null || state.Exited || _lastSongId.Length == 0 || start == 0 || window >= _maxReplayBytes) break;
            }
        }
        else state = await ReadLinesAsync(stream, state, length, token).ConfigureAwait(false);
        if (!_stop.IsCancellationRequested && state != original && _process.IsAlive())
        { Volatile.Write(ref _state, state); Changed?.Invoke(); }
    }
    private async Task<NeteaseLogState> ReadLinesAsync(FileStream stream, NeteaseLogState state, long length, CancellationToken token)
    {
        stream.Position = _offset;
        var bytes = new byte[16384]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        // Capture the observed length. Further writes are found by the next poll.
        while (_offset < length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - _offset)), token).ConfigureAwait(false);
            if (count == 0) break;
            _offset += count;
            NeteaseLog.Decode(bytes.AsSpan(0, count));
            int used = _decoder.GetChars(bytes, 0, count, chars, 0, false);
            for (int i = 0; i < used; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    if (!_discardLine)
                    {
                        long tick = Environment.TickCount64;
                        string text = _line.ToString().TrimEnd('\r');
                        var parsed = NeteaseLog.Parse(text, _process.Id, tick, _process.StartTick);
                        // Earlier generations still supply the metadata a restored session needs.
                        _tracks.Remember(parsed ?? NeteaseLog.ParseTrack(text));
                        if (parsed is not null)
                        {
                            if (parsed.SongId.Length > 0) _lastSongId = parsed.SongId;
                            state = state.Apply(parsed, DateTimeOffset.UtcNow, tick, _tracks);
                        }
                    }
                    _line.Clear(); _discardLine = false;
                }
                else if (!_discardLine)
                {
                    if (_line.Length >= 1_048_576) { _line.Clear(); _discardLine = true; }
                    else _line.Append(c);
                }
            }
        }
        return state;
    }
    private void Invalidate()
    {
        var current = State;
        if (_stop.IsCancellationRequested || current.Track is not { PositionKnown: true } track) return;
        Volatile.Write(ref _state, current with { Track = track with { PositionKnown = false, State = MediaState.Unknown, TimelinePending = true } });
        // Rebuild from the tail once readable again; elapsed time during a missing
        // file must not become a fabricated playback position.
        _offset = -1;
        Changed?.Invoke();
    }
    public void Dispose() { _stop.Cancel(); lock (_watchGate) { _watcher?.Dispose(); _watcher = null; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
