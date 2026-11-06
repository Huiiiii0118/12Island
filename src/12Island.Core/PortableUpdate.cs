using System.ComponentModel;
using System.Diagnostics;

namespace TwelveIsland.Core;

// Replaces the files of a portable copy with those of a newer portable package.
//
// A portable copy is a folder with 12Island.portable, which lists the files the package shipped (relative paths, one per
// line). The installer never ships that file, so an installed copy, a build output or a copied folder is never treated
// as portable. Only listed files are replaced or removed; anything else in the folder stays.
//
// The running app extracts the new package and starts its executable with HelperArguments, then exits. The helper waits
// for that process, replaces the files and starts the new version. A folder the user cannot write to, such as one in
// Program Files, is replaced by a second copy of the helper started with administrator rights (ElevatedArgument); the
// first copy waits for it, so the new version starts without them. When the user declines the prompt or a file cannot
// be replaced, the old files are restored, FailureFile records why, and the old version starts again.
public static class PortableUpdate
{
    public const string ListFile = "12Island.portable";
    public const string FailureFile = "restart-failure.txt";
    public const string Declined = "declined";
    public const string Failed = "failed";
    public const string HelperArgument = "--portable-update";
    public const string ElevatedArgument = "--portable-replace";
    private const string BackupPrefix = ".brimdeck-update-";
    private const int Cancelled = 1223;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2);

    public static bool IsPortable(string directory) => File.Exists(Path.Combine(directory, ListFile));

    // The extracted package: the folder itself, or the single folder the zip wraps the files in.
    public static string? FindPackage(string directory)
    {
        if (IsPortable(directory)) return directory;
        var folders = Directory.GetDirectories(directory);
        return folders.Length == 1 && IsPortable(folders[0]) ? folders[0] : null;
    }

    public static string[] HelperArguments(string target, int processId, string failureFile) =>
        [HelperArgument, Path.TrimEndingDirectorySeparator(target), processId.ToString(), failureFile];

    // The files a package lists, always including the list itself. A path that leaves the folder is refused.
    public static IReadOnlyList<string> Files(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(directory, ListFile)))
        {
            var file = line.Trim();
            if (file.Length == 0) continue;
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (Path.IsPathRooted(file) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || file.Split('\\', '/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("The portable file list has an invalid path: " + file);
            files.Add(full[root.Length..]);
        }
        if (!files.Contains(ListFile, StringComparer.OrdinalIgnoreCase)) files.Add(ListFile);
        return files;
    }

    public static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, BackupPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { } return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Moves the old files aside, copies the new ones and only then removes the old ones; on any error the old files return.
    public static void Apply(string source, string target)
    {
        var incoming = Files(source);
        foreach (var file in incoming)
            if (!File.Exists(Path.Combine(source, file))) throw new InvalidDataException("The portable package is missing " + file);
        var current = IsPortable(target) ? Files(target) : [];
        foreach (var leftover in Directory.GetDirectories(target, BackupPrefix + "*")) TryDeleteTree(leftover);
        var backup = Path.Combine(target, BackupPrefix + Guid.NewGuid().ToString("N"));
        var moved = new List<string>(); var copied = new List<string>();
        void MoveAside(string file)
        {
            var path = Path.Combine(target, file);
            if (!File.Exists(path)) return;
            var aside = Path.Combine(backup, file);
            Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
            File.Move(path, aside); moved.Add(file);
        }
        try
        {
            foreach (var file in current) MoveAside(file);
            // A file of the new package that the old list did not name is kept aside too, so a failure can put it back.
            foreach (var file in incoming) if (!moved.Contains(file, StringComparer.OrdinalIgnoreCase)) MoveAside(file);
            foreach (var file in incoming)
            {
                var path = Path.Combine(target, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(source, file), path); copied.Add(file);
            }
        }
        catch
        {
            foreach (var file in copied) TryDelete(Path.Combine(target, file));
            foreach (var file in moved)
                try { File.Move(Path.Combine(backup, file), Path.Combine(target, file), true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            RemoveEmptyFolders(target, copied);
            TryDeleteTree(backup);
            throw;
        }
        TryDeleteTree(backup);
        RemoveEmptyFolders(target, moved);
    }

    // Runs the update step that args ask for. Returns false when they ask for none, so the app starts normally.
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        var source = AppContext.BaseDirectory;
        if (args is [ElevatedArgument, var replace])
        {
            try { Apply(source, replace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { exitCode = 1; }
            return true;
        }
        if (args is not [HelperArgument, var target, var process, var failureFile] || !int.TryParse(process, out var processId)) return false;
        try
        {
            using var running = Process.GetProcessById(processId);
            // The old version is still running: leave it alone; the next start offers the update again.
            if (!running.WaitForExit(ExitTimeout)) { exitCode = 1; return true; }
        }
        catch (ArgumentException) { }
        string? failure = null; Exception? error = null;
        try
        {
            if (CanWrite(target)) Apply(source, target);
            else failure = ReplaceElevated(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception) { failure = Failed; error = ex; }
        if (failure is not null)
        {
            try { File.WriteAllText(failureFile, failure + Environment.NewLine + error); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            exitCode = 1;
        }
        // Started by this helper, which runs without administrator rights, so the app does not get them either.
        Process.Start(new ProcessStartInfo(Path.Combine(target, Path.GetFileName(Environment.ProcessPath!))) { UseShellExecute = true, WorkingDirectory = target })?.Dispose();
        return true;
    }

    private static string? ReplaceElevated(string target)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(ElevatedArgument); start.ArgumentList.Add(target);
        try
        {
            using var elevated = Process.Start(start) ?? throw new Win32Exception();
            elevated.WaitForExit();
            return elevated.ExitCode == 0 ? null : Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled) { return Declined; }
    }

    // Folders that held only replaced files are removed; the app folder itself always stays.
    private static void RemoveEmptyFolders(string target, IEnumerable<string> files)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        foreach (var folder in files.Select(file => Path.GetDirectoryName(Path.Combine(root, file))!).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(folder => folder.Length))
        {
            for (var current = folder; current.Length > root.Length && Directory.Exists(current); current = Path.GetDirectoryName(current)!)
            {
                try { if (Directory.EnumerateFileSystemEntries(current).Any()) break; Directory.Delete(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
