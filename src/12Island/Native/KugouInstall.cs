using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TwelveIsland.Core;
using Microsoft.Win32;

namespace TwelveIsland.Native;

// KuGou's installation and the change to its libcef.dll that opens the DevTools port (KugouPatch). Changing or restoring the
// file alone (the settings) does not touch KuGou: a file KuGou has loaded cannot be written, but it can be renamed, so the
// changed copy takes its place and the next start loads it; the renamed file is deleted once KuGou no longer holds it.
// Enabling full control (the panel's entries and the automatic setting) also restarts KuGou, so the change works at once. Under Program Files the file
// needs administrator rights; TwelveIsland then starts itself elevated for the write alone, which shows a UAC prompt.
internal static class KugouInstall
{
    // Launcher: KGMusic\KuGou.exe, which starts the KuGou.exe of the version folder; Library: that folder's libcef.dll.
    internal sealed record Location(string Launcher, string Library);
    internal const string ElevatedSwitch = "--kugou-patch";

    public static Location? Find()
    {
        foreach (var path in RunningPaths())
        {
            string folder = Path.GetDirectoryName(path)!;
            string library = Path.Combine(folder, "libcef.dll");
            if (!File.Exists(library)) continue;
            string launcher = Path.Combine(Path.GetDirectoryName(folder)!, "KuGou.exe");
            return new(File.Exists(launcher) ? launcher : path, library);
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\KuGou");
            if (key?.GetValue("AppPath") is string { Length: > 0 } appPath && Installed(Path.Combine(appPath, "KuGou.exe"),
                key.GetValue("KuGou8") as string) is { } registered) return registered;
            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64) })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") as string != "酷狗音乐") continue;
                    if (entry.GetValue("DisplayIcon") is string { Length: > 0 } icon && Installed(icon.Trim('"'), null) is { } listed) return listed;
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // The version folder named by KuGou's own record, else the one named by the launcher's version, else the highest one.
    private static Location? Installed(string launcher, string? folder)
    {
        if (!File.Exists(launcher)) return null;
        if (folder is { Length: > 0 } && File.Exists(Path.Combine(folder, "libcef.dll"))) return new(launcher, Path.Combine(folder, "libcef.dll"));
        string root = Path.GetDirectoryName(launcher)!;
        string? version = FileVersionInfo.GetVersionInfo(launcher).FileVersion;
        if (version is { Length: > 0 } && File.Exists(Path.Combine(root, version, "libcef.dll"))) return new(launcher, Path.Combine(root, version, "libcef.dll"));
        var highest = Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "libcef.dll")))
            .Select(d => (Folder: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null)).Where(d => d.Version is not null)
            .OrderByDescending(d => d.Version).FirstOrDefault();
        return highest.Folder is null ? null : new(launcher, Path.Combine(highest.Folder, "libcef.dll"));
    }

    // Whether full control can be switched on or off here: the installation's state, null when KuGou is not installed.
    public static LibcefPatchState? State() => Find() is { } location ? State(location) ?? LibcefPatchState.Unsupported : null;

    public static LibcefPatchState? State(Location location) => LibcefFile.State(location.Library, KugouPatch.Profile);

    // Changes or restores the file and leaves KuGou as it is; a running KuGou takes the change at its next start.
    // Throws with a short message for the user, as EnableAsync does.
    public static async Task ChangeFileAsync(bool patch, CancellationToken token) =>
        await LibcefFile.WriteAsync(Changeable().Library, KugouPatch.Profile, patch, ElevatedSwitch, token).ConfigureAwait(false);
    // Enables full control: closes KuGou, changes the file and starts KuGou again when it was running, or when start asks.
    public static async Task EnableAsync(bool start, CancellationToken token)
    {
        var location = Changeable();
        bool running = RunningPaths().Any();
        await CloseAsync(location, token).ConfigureAwait(false);
        await LibcefFile.WriteAsync(location.Library, KugouPatch.Profile, true, ElevatedSwitch, token).ConfigureAwait(false);
        if (start || running) Start(location);
    }
    private static Location Changeable()
    {
        var location = Find() ?? throw new InvalidOperationException(Loc.T("未找到酷狗音乐", "KuGou Music was not found"));
        return State(location) is LibcefPatchState.Original or LibcefPatchState.Patched or LibcefPatchState.Partial ? location
            : throw new InvalidOperationException(Loc.T("不支持这个版本的酷狗音乐", "This version of KuGou Music is not supported"));
    }

    public static bool Running() => RunningPaths().Any();
    // The running KuGou processes, to tell one start of KuGou from the next, and when the first of them started; "" and null
    // while KuGou is not running.
    public static (string Key, DateTime? Started) Processes()
    {
        // Looked for in a snapshot first: GetProcessesByName reads every process's threads, which is the most of a check while
        // KuGou is not running.
        if (!ProcessSnapshot.Any("KuGou.exe")) return ("", null);
        var processes = Process.GetProcessesByName("KuGou");
        try
        {
            DateTime? started = null;
            foreach (var process in processes)
                try { var at = process.StartTime.ToUniversalTime(); if (started is null || at < started) started = at; }
                catch (InvalidOperationException) { } catch (Win32Exception) { }
            return (string.Join(",", processes.Select(p => p.Id).Order()), started);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void Start(Location location)
    {
        var info = new ProcessStartInfo(location.Launcher) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(location.Launcher)! };
        using var process = Process.Start(info);
    }

    // KuGou has no command that quits it; closing its window only hides it in the tray. Its processes are ended, as the
    // patching tools for it do, which leaves its song list and settings as they were saved.
    private static async Task CloseAsync(Location location, CancellationToken token)
    {
        string root = Path.GetDirectoryName(location.Launcher)!;
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            var processes = Process.GetProcessesByName("KuGou");
            bool any = false;
            try
            {
                foreach (var process in processes)
                {
                    string? path = PathOf(process);
                    if (path is null || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            if (!any) return;
            if (Environment.TickCount64 >= deadline) throw new InvalidOperationException(Loc.T("无法关闭酷狗音乐，请退出后重试", "KuGou could not be closed; quit it and try again"));
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    // The elevated instance started for a write without rights (LibcefFile), dispatched from Program.cs by ElevatedSwitch.
    public static int RunElevated(string mode, string library) => LibcefFile.RunElevated(KugouPatch.Profile, mode, library);

    private static IEnumerable<string> RunningPaths()
    {
        if (!ProcessSnapshot.Any("KuGou.exe")) return [];
        var processes = Process.GetProcessesByName("KuGou");
        try { return processes.Select(PathOf).OfType<string>().ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static string? PathOf(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
