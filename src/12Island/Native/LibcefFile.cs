using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Writing a player's libcef.dll (LibcefPatch) in place, around two obstacles: the player holds the file while it runs, and the
// file may sit in a folder that needs administrator rights. A held file cannot be written but can be renamed, so a changed copy
// takes its place and the next start loads it; the renamed file is deleted once the player no longer holds it. A write without
// rights is retried elevated, which re-launches TwelveIsland with the caller's switch and shows a UAC prompt. Shared by every
// player's install (KugouInstall, NeteaseInstall).
internal static class LibcefFile
{
    // The state last read of each file, with its write time and length then: KuGou's install is checked every 5 to 60 seconds,
    // and the 150 MB file is searched again only once it has changed.
    private static readonly Dictionary<string, (DateTime Written, long Length, LibcefPatchState State)> Known = new(StringComparer.OrdinalIgnoreCase);

    // Reads the file's patch state without changing it; also clears any held files left from an earlier replace.
    public static LibcefPatchState State(string library, LibcefPatch profile)
    {
        CleanUp(library);
        try
        {
            var info = new FileInfo(library);
            var (written, length) = (info.LastWriteTimeUtc, info.Length);
            lock (Known) if (Known.TryGetValue(library, out var known) && known.Written == written && known.Length == length) return known.State;
            using var file = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = profile.Classify(file);
            lock (Known) Known[library] = (written, length, state);
            return state;
        }
        catch (IOException) { return LibcefPatchState.Unsupported; }
        catch (UnauthorizedAccessException) { return LibcefPatchState.Unsupported; }
    }

    // A file the player has only just released can still be locked for a moment; a write without rights is done elevated.
    public static async Task WriteAsync(string library, LibcefPatch profile, bool patch, string elevatedSwitch, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Write(library, profile, patch); return; }
            catch (UnauthorizedAccessException) { await WriteElevatedAsync(library, patch, elevatedSwitch, token).ConfigureAwait(false); return; }
            catch (IOException) when (attempt < 20 && File.Exists(library)) { await Task.Delay(250, token).ConfigureAwait(false); }
        }
    }

    public static void Write(string library, LibcefPatch profile, bool patch, bool elevated = false)
    {
        lock (Known) Known.Remove(library);
        try
        {
            using var file = new FileStream(library, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            profile.Write(file, patch);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == SharingViolation) { Replace(library, profile, patch, elevated); }
    }

    // The player holds the file: a changed copy is made and put in its place, and the held file is renamed until it can go.
    private static void Replace(string library, LibcefPatch profile, bool patch, bool elevated)
    {
        string copy = library + ".brimdeck-new", held = library + ".brimdeck-old-" + Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(library, copy, overwrite: true);
        try
        {
            using (var file = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) profile.Write(file, patch);
            File.Move(library, held);
            File.Move(copy, library);
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
        // Without administrator rights later, the held file in a protected folder would stay; Windows removes it at restart.
        if (elevated) MoveFileEx(held, null, MoveFileDelayUntilReboot);
    }

    public static void CleanUp(string library)
    {
        try
        {
            foreach (var held in Directory.EnumerateFiles(Path.GetDirectoryName(library)!, Path.GetFileName(library) + ".brimdeck-old-*"))
                try { File.Delete(held); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const int SharingViolation = 32;
    private const uint MoveFileDelayUntilReboot = 4;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? replacement, uint flags);

    private static async Task WriteElevatedAsync(string library, bool patch, string elevatedSwitch, CancellationToken token)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(elevatedSwitch); info.ArgumentList.Add(patch ? "apply" : "restore"); info.ArgumentList.Add(library);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException(Loc.T("未取得管理员权限", "Administrator rights were not given")); }
        using (process)
        {
            if (process is null) throw new InvalidOperationException(Loc.T("无法以管理员身份修改文件", "The file could not be changed as administrator"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(Loc.T("修改文件失败，文件保持原样", "The file could not be changed and was left as it was"));
        }
    }

    // The elevated instance: writes the one file named on its command line, which must be a libcef.dll the profile recognizes,
    // and exits with 0 on success.
    public static int RunElevated(LibcefPatch profile, string mode, string library)
    {
        if (mode is not ("apply" or "restore") || !string.Equals(Path.GetFileName(library), "libcef.dll", StringComparison.OrdinalIgnoreCase)) return 2;
        try { Write(library, profile, mode == "apply", elevated: true); return 0; }
        catch { return 1; }
    }
}
