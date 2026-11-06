using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// NetEase Cloud Music's installation and the change to its libcef.dll that opens the DevTools port (NeteasePatch). Once the file
// is changed, every normal start of NetEase Cloud Music opens the port, so TwelveIsland connects to it without passing a switch. The
// file write itself (in place, around a held file, elevated when needed) is LibcefFile; this class finds the installation and,
// when enabling, closes and starts NetEase Cloud Music so the change takes effect at once.
internal static class NeteaseInstall
{
    internal sealed record Location(string Executable, string Library);
    internal const string ElevatedSwitch = "--netease-patch";

    public static Location? Find()
    {
        foreach (var path in RunningPaths())
        {
            string library = Path.Combine(Path.GetDirectoryName(path)!, "libcef.dll");
            if (File.Exists(library)) return new(path, library);
        }
        if (NeteaseProcess.FindInstalled() is { } executable)
        {
            string library = Path.Combine(Path.GetDirectoryName(executable)!, "libcef.dll");
            if (File.Exists(library)) return new(executable, library);
        }
        return null;
    }

    // Whether full control can be switched on or off here: the installation's state, null when NetEase Cloud Music is not installed.
    public static LibcefPatchState? State() => Find() is { } location ? State(location) : null;
    public static LibcefPatchState? State(Location location) => LibcefFile.State(location.Library, NeteasePatch.Profile);

    // Changes or restores the file and leaves NetEase Cloud Music as it is; a running player takes the change at its next start.
    public static async Task ChangeFileAsync(bool patch, CancellationToken token) =>
        await LibcefFile.WriteAsync(Changeable().Library, NeteasePatch.Profile, patch, ElevatedSwitch, token).ConfigureAwait(false);
    // Enables full control: closes NetEase Cloud Music, changes the file and starts it again when it was running, or when start asks.
    public static async Task EnableAsync(bool start, CancellationToken token)
    {
        var location = Changeable();
        bool running = RunningPaths().Any();
        await CloseAsync(location, token).ConfigureAwait(false);
        await LibcefFile.WriteAsync(location.Library, NeteasePatch.Profile, true, ElevatedSwitch, token).ConfigureAwait(false);
        if (start || running) NeteaseProcess.Start(location.Executable).Dispose();
    }
    private static Location Changeable()
    {
        var location = Find() ?? throw new InvalidOperationException(Loc.T("未找到网易云音乐", "NetEase Cloud Music was not found"));
        return State(location) is LibcefPatchState.Original or LibcefPatchState.Patched or LibcefPatchState.Partial ? location
            : throw new InvalidOperationException(Loc.T("不支持这个版本的网易云音乐", "This version of NetEase Cloud Music is not supported"));
    }

    public static bool Running() => RunningPaths().Any();

    // NetEase Cloud Music quits on its own exit command, which bypasses close-to-tray without changing its settings.
    private static async Task CloseAsync(Location location, CancellationToken token)
    {
        if (!Running()) return;
        using (NeteaseProcess.Start(location.Executable, "--orpheus-startup=exit-process")) { }
        var deadline = Environment.TickCount64 + 15_000;
        while (Running())
        {
            if (Environment.TickCount64 >= deadline) throw new InvalidOperationException(Loc.T("无法关闭网易云音乐，请退出后重试", "NetEase Cloud Music could not be closed; quit it and try again"));
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    // The elevated instance started for a write without rights (LibcefFile), dispatched from Program.cs by ElevatedSwitch.
    public static int RunElevated(string mode, string library) => LibcefFile.RunElevated(NeteasePatch.Profile, mode, library);

    private static IEnumerable<string> RunningPaths()
    {
        // Looked for in a snapshot first: GetProcessesByName reads every process's threads.
        if (!ProcessSnapshot.Any("cloudmusic.exe")) return [];
        var processes = Process.GetProcessesByName("cloudmusic");
        try { return processes.Select(PathOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static string? PathOf(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
