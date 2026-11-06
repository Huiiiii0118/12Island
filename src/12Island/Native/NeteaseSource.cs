using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TwelveIsland.Core;
using Microsoft.Win32;

namespace TwelveIsland.Native;

internal sealed record NeteaseProcess(int Id, long Started, string Path, int? DebugPort, long StartTick)
{
    public string Identity => $"netease:{Id}:{Started}";
    public bool IsAlive()
    {
        try { using var process = Process.GetProcessById(Id); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == Started; }
        catch { return false; }
    }
    public async Task ExitAsync(CancellationToken cancellationToken)
    {
        if (!IsAlive()) return;
        // The client's single-instance startup handler forwards this command as
        // its native exit message. Unlike WM_CLOSE it bypasses close-to-tray and
        // the close confirmation, without terminating the process or changing settings.
        using var request = Start(Path, "--orpheus-startup=exit-process");
        var deadline = Environment.TickCount64 + 15_000;
        while (IsAlive() || !request.HasExited)
        {
            if (Environment.TickCount64 >= deadline) throw new InvalidOperationException(Loc.T("网易云未响应退出请求，请稍后重试。", "NetEase Cloud Music did not respond to the request to quit. Try again later."));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
    internal static Process Start(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException(Loc.T("无法启动网易云。", "NetEase Cloud Music could not be started."));
        // Shell execution keeps the player's diagnostic streams detached from TwelveIsland.
        return process;
    }
    public static NeteaseProcess? Discover()
    {
        using var current = Process.GetCurrentProcess();
        using var query = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath, SessionId FROM Win32_Process WHERE Name='cloudmusic.exe'");
        using var rows = query.Get();
        var candidates = new List<NeteaseProcess>();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                string? command = row["CommandLine"] as string, path = row["ExecutablePath"] as string;
                if (command is null || path is null || command.Contains("--type=", StringComparison.OrdinalIgnoreCase) || (uint)row["SessionId"] != current.SessionId) continue;
                try
                {
                    using var process = Process.GetProcessById((int)(uint)row["ProcessId"]);
                    if (process.HasExited || !string.Equals(System.IO.Path.GetFileName(path), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var match = Regex.Match(command, @"(?:^|\s)--remote-debugging-port=(\d+)(?:\s|$)");
                    int? port = match.Success && int.TryParse(match.Groups[1].Value, out int parsed) && parsed is > 0 and <= 65535 ? parsed : null;
                    var start = process.StartTime.ToUniversalTime();
                    candidates.Add(new(process.Id, start.Ticks, path, port, Math.Max(0, Environment.TickCount64 - (long)(DateTime.UtcNow - start).TotalMilliseconds - 1000)));
                }
                catch { }
            }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
    // The installer records the player under its uninstall entry; the orpheus:// handler it
    // registers names the same executable and remains when that entry is missing.
    public static string? FindInstalled()
    {
        static string? Checked(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (value.StartsWith('"')) { int end = value.IndexOf('"', 1); value = end > 0 ? value[1..end] : value.Trim('"'); }
            else if (value.LastIndexOf(',') is int comma and > 0 && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..comma];
            return string.Equals(System.IO.Path.GetFileName(value), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(value) ? value : null;
        }
        try
        {
            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64) })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") as string != "网易云音乐") continue;
                    if (Checked(entry.GetValue("DisplayIcon") as string) is { } icon) return icon;
                    if (entry.GetValue("InstallLocation") is string { Length: > 0 } location && Checked(System.IO.Path.Combine(location, "cloudmusic.exe")) is { } located) return located;
                }
            }
            using var handler = Registry.ClassesRoot.OpenSubKey(@"orpheus\shell\open\command");
            return Checked(handler?.GetValue(null) as string);
        }
        catch { return null; }
    }
}

internal sealed class NeteaseSource : IMediaPlayerConnection
{
    internal const int ControlPort = 21631;
    // A failed connection leaves TwelveIsland reading only what plays, but the player keeps the port it was started with.
    internal static string PortStillOpen => Loc.T("本程序已改为只读取播放信息。网易云如果仍在运行，它的调试端口仍然开着；以普通方式重新启动网易云即可关闭。",
        " The app now only reads what is playing. If NetEase Cloud Music is still running, its debugging port is still open; start NetEase Cloud Music again the normal way to close it.");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _supervisor;
    private NeteaseProcess? _process;
    private NeteaseLogReader? _log;
    private NeteaseCdpConnection? _cdp;
    private DesktopMediaSnapshot? _snapshot;
    private bool _cdpFailed;
    private int _restarting;
    private string? _installed;
    // The setting NeteaseAutoFullControl, which the user confirmed when switching it on. Each player started the
    // normal way is restarted once; one that comes back without the port is left as it is.
    private bool _autoFullControl;
    private string? _autoRestarted;
    // The setting NeteaseFullControl: the libcef.dll is kept changed so every start opens the fixed port on its own. While it is
    // on, a running player not yet exposing the port is restarted once (as with the automatic setting), and a file left original
    // by an update is changed again. State is read off the UI thread (MaintainFile); _filePatching guards the one write at a time.
    private volatile bool _fileFullControl, _fileAsked;
    private LibcefPatchState? _fileState;
    private DateTime _fileStamp;
    private long _fileCheckedAt = long.MinValue / 2;
    private int _filePatching;
    private const long FileCheckInterval = 5_000;
    // A player that has only just started is given time to finish starting before it is asked to quit.
    private const long AutoRestartAge = 3_000;
    // Installation rarely changes while the app runs, and a running player already counts as installed.
    private const long InstalledCheckInterval = 600_000;
    private long _installedCheckedAt = -InstalledCheckInterval;
    // Player processes that could not be identified; WMI is asked again only when this set changes.
    private int[] _unidentified = [];
    public DesktopMediaSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    internal string Identity => _process?.Identity ?? "netease";
    public int? ProcessId => _process?.Id;
    public string? Error { get; private set; }
    public bool IsLogMode => _process is not null && _cdp?.Ready != true;
    public bool Restarting => Volatile.Read(ref _restarting) != 0;
    public bool AutoFullControl
    {
        get => Volatile.Read(ref _autoFullControl);
        set { if (Volatile.Read(ref _autoFullControl) == value) return; Volatile.Write(ref _autoFullControl, value); if (value) Signal(); }
    }
    // The file-patch setting (NeteaseFullControl). Turning it on keeps the file changed and connects the running player;
    // turning it off restores the file and leaves the player as it is until its next start.
    public bool FileFullControl
    {
        get => _fileFullControl;
        set { if (_fileFullControl == value) return; _fileFullControl = value; CheckFile(); }
    }
    // The file's patch state as last read, for the settings switch; null until it has been read.
    public LibcefPatchState? FilePatchState => _fileState;
    // Reads the file's state at the next refresh, for the settings page.
    public void CheckFile() { _fileAsked = true; Signal(); }
    // A restart may be due when either full-control setting is on and the running player has no debugging port yet.
    private bool AutoPending => (AutoFullControl || FileFullControl) && (_process is null || _process.DebugPort is null && _autoRestarted != _process.Identity);
    // The file-patched player opens the port a moment after it starts, so a port-less player is looked for every second.
    private bool FilePending => FileFullControl && _process is { DebugPort: null };
    // A running player counts as installed even when neither registry entry is present.
    public bool Installed => _process is not null || Volatile.Read(ref _installed) is not null;
    public event Action? Changed;
    public Task StartAsync() { _supervisor ??= Task.Run(SuperviseAsync); return Task.CompletedTask; }
    private void Signal() { if (_wake.CurrentCount == 0) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }
    private async Task SuperviseAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch { Error = Loc.T("网易云来源暂时不可用。", "The NetEase Cloud Music source is temporarily unavailable."); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(AutoPending || FilePending ? 1 : 5), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task RefreshAsync()
    {
        await _refresh.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - _installedCheckedAt >= InstalledCheckInterval)
            {
                _installedCheckedAt = Environment.TickCount64;
                var installed = NeteaseProcess.FindInstalled();
                if (Interlocked.Exchange(ref _installed, installed) is null != installed is null) Changed?.Invoke();
            }
            MaintainFile();
            if (_process?.IsAlive() != true)
            {
                Clear();
                // Looked for every second while a full-control setting waits for the player: the snapshot is the cheap check.
                if (!ProcessSnapshot.Any("cloudmusic.exe")) { _unidentified = []; return; }
                var processes = Process.GetProcessesByName("cloudmusic");
                int[] ids;
                try { ids = processes.Select(p => p.Id).Order().ToArray(); }
                finally { foreach (var process in processes) process.Dispose(); }
                if (ids.Length == 0) _unidentified = [];
                if (ids.Length == 0 || ids.SequenceEqual(_unidentified)) return;
                _process = NeteaseProcess.Discover();
                _unidentified = _process is null ? ids : [];
                _stop.Token.ThrowIfCancellationRequested();
                if (_process is null) return;
                _cdpFailed = false; Error = null;
            }
            // A file-patched player opens the fixed port without a switch; adopt it once the port is up so no restart is needed.
            if (_process is { DebugPort: null } waiting && NeteaseCdpConnection.OwnsPort(waiting with { DebugPort = NeteasePatch.Port }))
                _process = waiting with { DebugPort = NeteasePatch.Port };
            if (AutoPending && _process is { } normal && !Restarting && Environment.TickCount64 - normal.StartTick >= AutoRestartAge)
            {
                _autoRestarted = normal.Identity;
                _ = Task.Run(() => EnableFullControlAsync());
            }
            if (_process.DebugPort.HasValue && !_cdpFailed && _cdp is null)
            {
                var connection = new NeteaseCdpConnection(_process);
                _cdp = connection; connection.Changed += CdpChanged;
                await connection.StartAsync().ConfigureAwait(false);
            }
            if (_cdp is { Failed: true })
            {
                _cdpFailed = true;
                Error = (_cdp.Error ?? Loc.T("完整控制连接失败。", "The full control connection failed.")) + PortStillOpen;
                _cdp.Changed -= CdpChanged; _cdp.Dispose(); _cdp = null;
            }
            if (_cdp is not null) { PublishCdp(); return; }
            if (_log is null)
            {
                _log = new(_process); _log.Changed += LogChanged; _log.Start();
            }
            PublishLog();
        }
        finally { _refresh.Release(); }
    }
    private void CdpChanged()
    {
        if (_cdp?.Failed == true) Signal();
        PublishCdp();
    }
    private void PublishCdp()
    {
        if (_stop.IsCancellationRequested) return;
        Publish(_cdp?.Snapshot);
    }
    private void LogChanged() => PublishLog();
    private void PublishLog()
    {
        if (_stop.IsCancellationRequested || _process is null) return;
        var state = _log?.State;
        // A session known only by its id is not a media entry. Without a title the
        // route is not created and any system session of the player stands on its own.
        if (state?.Track is not { Title.Length: > 0 } track) { Publish(null); return; }
        Publish(new(track.Title, track.Artist, track.Album, track.HasTimeline ? track.ReportedPosition : null,
            track.Duration > TimeSpan.Zero ? track.Duration : null, track.State, false, false, false, false,
            SongId: track.SongId, CoverUrl: state.CoverUrl, PositionAt: track.PositionAt, TimelinePending: track.TimelinePending,
            Rate: track.Rate, IsNeteaseLog: true, PreviousRestricted: track.PreviousRestricted));
    }
    private void Publish(DesktopMediaSnapshot? snapshot)
    {
        if (Interlocked.Exchange(ref _snapshot, snapshot) != snapshot) Changed?.Invoke();
    }
    private void Clear()
    {
        if (_log is { } log) { _log = null; log.Changed -= LogChanged; log.Dispose(); }
        if (_cdp is { } cdp) { _cdp = null; cdp.Changed -= CdpChanged; cdp.Dispose(); }
        _process = null; Publish(null);
    }
    public Task<DesktopCommandResult> CommandAsync(MediaCommand command, MediaState state) =>
        _cdp?.CommandAsync(command, state) ?? Task.FromResult(DesktopCommandResult.Unavailable);
    public Task<DesktopCommandResult> SetPlaybackModeAsync(MusicPlaybackMode mode) =>
        _cdp?.SetPlaybackModeAsync(mode) ?? Task.FromResult(DesktopCommandResult.Unavailable);
    public Task<DesktopCommandResult> SeekAsync(TimeSpan position) =>
        _cdp?.SeekAsync(position) ?? Task.FromResult(DesktopCommandResult.Unavailable);

    // The UI must obtain the user's consent before calling this operation. A running player,
    // in either mode, is closed and started again; a player that is not running is started.
    // A quiet request leaves no error behind: its caller only shows that it is in progress.
    public async Task<bool> EnableFullControlAsync(bool quiet = false)
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0) return false;
        var owner = _process?.IsAlive() == true ? _process : null;
        var beforeRestart = owner is null ? null : Snapshot;
        Error = null; Changed?.Invoke();
        try
        {
            string path = owner?.Path ?? Volatile.Read(ref _installed) ?? NeteaseProcess.FindInstalled() ?? throw new InvalidOperationException(Loc.T("未找到网易云音乐。", "NetEase Cloud Music was not found."));
            if (owner is not null) await owner.ExitAsync(_stop.Token).ConfigureAwait(false);
            // The user asked for this start, so the player starts the ordinary way and its window appears.
            using var launched = NeteaseProcess.Start(path,
                $"--remote-debugging-port={ControlPort}", "--remote-debugging-address=127.0.0.1");
            Signal();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250, _stop.Token).ConfigureAwait(false);
                if (_cdp?.Ready == true && _process is { } started && started.Started != owner?.Started)
                {
                    // The player is back under full control; the entries are released now,
                    // not after the earlier playback state has been confirmed.
                    Interlocked.Exchange(ref _restarting, 0); Changed?.Invoke();
                    if (beforeRestart?.State is MediaState.Playing or MediaState.Paused)
                        await RestoreStateAsync(beforeRestart.State.Value).ConfigureAwait(false);
                    return true;
                }
            }
            await _refresh.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                _cdpFailed = true;
                if (_cdp is { } failed) { _cdp = null; failed.Changed -= CdpChanged; failed.Dispose(); }
            }
            finally { _refresh.Release(); }
            throw new InvalidOperationException(Loc.T("未能建立完整控制连接。", "The full control connection could not be made.") + PortStillOpen);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { if (!quiet) Error = ex.Message; return false; }
        finally { Interlocked.Exchange(ref _restarting, 0); Signal(); Changed?.Invoke(); }
    }
    // The client applies its own autoplay setting shortly after the page loads, so the
    // state before the restart is confirmed rather than corrected once.
    private async Task RestoreStateAsync(MediaState wanted)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        var settled = DateTimeOffset.UtcNow.AddSeconds(2);
        for (int corrections = 0; DateTimeOffset.UtcNow < deadline;)
        {
            await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            var reported = Snapshot?.State;
            if (reported is not (MediaState.Playing or MediaState.Paused)) continue;
            var state = reported.Value;
            if (state == wanted) { if (DateTimeOffset.UtcNow >= settled) return; continue; }
            if (++corrections > 3) return;
            await CommandAsync(MediaCommand.PlayPause, state).ConfigureAwait(false);
            settled = DateTimeOffset.UtcNow.AddSeconds(2);
        }
    }
    // Reads the libcef.dll's patch state off the UI thread only when it is needed: when the settings page asks (CheckFile), and
    // while the setting is on and the running player has no port, as when an update left the file original, which is then
    // changed again (at most every FileCheckInterval). The 150 MB file is re-read only when it has changed since the last
    // read. One write or read runs at a time.
    private void MaintainFile()
    {
        if (_filePatching != 0) return;
        bool asked = _fileAsked;
        if (!asked && (!FilePending || Environment.TickCount64 - _fileCheckedAt < FileCheckInterval)) return;
        if (Interlocked.CompareExchange(ref _filePatching, 1, 0) != 0) return;
        _fileAsked = false;
        _fileCheckedAt = Environment.TickCount64;
        _ = Task.Run(async () =>
        {
            try
            {
                if (NeteaseInstall.Find() is not { } location) { if (_fileState is not null) { _fileState = null; Changed?.Invoke(); } return; }
                var stamp = File.GetLastWriteTimeUtc(location.Library);
                if (stamp != _fileStamp || _fileState is null)
                {
                    _fileStamp = stamp;
                    var previous = _fileState; _fileState = NeteaseInstall.State(location);
                    if (_fileState != previous) Changed?.Invoke();
                }
                if (FileFullControl && _fileState is LibcefPatchState.Original or LibcefPatchState.Partial)
                {
                    await NeteaseInstall.ChangeFileAsync(true, _stop.Token).ConfigureAwait(false);
                    _fileStamp = File.GetLastWriteTimeUtc(location.Library); _fileState = NeteaseInstall.State(location);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _filePatching, 0); if (!_stop.IsCancellationRequested) { Changed?.Invoke(); Signal(); } }
        });
    }

    // The user confirmed this. Changes the file so every start opens the port, then connects a running player: one without the
    // port started before the change, so it is restarted once. Null when done, otherwise a short message for the user.
    public async Task<string?> EnableFileControlAsync()
    {
        FileFullControl = true;
        try { await Task.Run(() => NeteaseInstall.ChangeFileAsync(true, _stop.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { return ex.Message; }
        _fileState = LibcefPatchState.Patched; _fileCheckedAt = Environment.TickCount64;
        if (_process?.IsAlive() == true && _cdp?.Ready != true && !await EnableFullControlAsync().ConfigureAwait(false))
            return Error ?? Loc.T("未能建立完整控制连接。", "The full control connection could not be made.");
        Signal(); return null;
    }
    // Restores the file; a running player keeps the port until its next start, as its explanation says.
    public async Task<string?> DisableFileControlAsync()
    {
        FileFullControl = false;
        try { await Task.Run(() => NeteaseInstall.ChangeFileAsync(false, _stop.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { return ex.Message; }
        _fileState = LibcefPatchState.Original; _fileCheckedAt = Environment.TickCount64; Signal(); return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Release active transports immediately. Serialize final cleanup with discovery
        // so a WMI query already in flight cannot install a watcher after disposal.
        _log?.Dispose(); _cdp?.Dispose();
        _ = CleanupAsync();
    }
    private async Task CleanupAsync()
    {
        await _refresh.WaitAsync().ConfigureAwait(false);
        try { Clear(); }
        finally { _refresh.Release(); }
    }
}
