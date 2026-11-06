using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The icon of the program a notice came from. The window that flashed is the surest way to that program: its process names the
// executable that is actually running, wherever it was installed, and the executable carries the icon the taskbar shows. Where
// the window is gone (a notice kept from an earlier run) the running processes, the install folders on every fixed drive and
// Windows' own icon for the file are tried in turn. A failure is never cached, so a program started or installed later shows
// its icon on the next drawing instead of staying a symbol forever.
internal static class AppIcons
{
    // The executable each source is looked for, in the order they are tried, when no program of that source names one.
    private static readonly Dictionary<NotifySource, string[]> Programs = new()
    {
        [NotifySource.WeChat] = ["Weixin.exe", "WeChat.exe"],
        [NotifySource.Qq] = ["QQ.exe", "QQNT.exe"],
        [NotifySource.Mail] = ["outlook.exe", "Foxmail.exe", "QQMail.exe"]
    };

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<NotifySource, BitmapSource?> BySource = [];
    private static readonly Dictionary<string, BitmapSource?> ByProgram = new(StringComparer.OrdinalIgnoreCase);

    // The icon for a source, or null where none of its programs can be found.
    public static BitmapSource? For(NotifySource source)
    {
        if (BySource.TryGetValue(source, out var known) && known is not null) return known;
        BitmapSource? icon = null;
        // Only the executables named for this source: one program's icon must never stand in for another program of the same
        // kind (a DingTalk notice is not a WeCom notice). ForProgram finds each program's own icon by its name.
        if (Programs.TryGetValue(source, out var names))
            foreach (var name in names)
                if (PathOf(name) is { } path && FromFile(path) is { } found) { icon = found; break; }
        if (icon is not null) BySource[source] = icon;
        return icon;
    }

    // The icon of one program (NotifyPrograms). The window that announced its notice, when there is one, points straight at
    // the copy in use: a program installed on another drive, or under a name that changed, is still found this way.
    public static BitmapSource? ForProgram(string program, IntPtr window = default)
    {
        if (program.Length == 0) return null;
        if (ByProgram.TryGetValue(program, out var known) && known is not null) return known;
        BitmapSource? icon = null;
        if (window != IntPtr.Zero && PathOfWindow(window) is { } running) icon = FromFile(running);
        // The icon the window itself shows (WM_GETICON, then the window class): programs that set their taskbar icon at run
        // time, or whose executable has no icon group, are still drawn with the right mark.
        if (icon is null && window != IntPtr.Zero) icon = FromWindow(window);
        if (icon is null)
            foreach (var name in NotifyPrograms.Processes(program))
            {
                string file = name + ".exe";
                if (PathOf(file) is { } path && FromFile(path) is { } found) { icon = found; break; }
            }
        if (icon is not null) ByProgram[program] = icon;
        return icon;
    }

    // The executable of the process that owns a window. Asking the kernel for its full image name works for processes whose
    // MainModule cannot be read (a different bitness, a protected process, one that has just exited).
    private static string? PathOfWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return null;
        try
        {
            if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0) return null;
            IntPtr process = OpenProcess(QueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var text = new StringBuilder(512);
                int size = text.Capacity;
                if (!QueryFullProcessImageName(process, 0, text, ref size)) return null;
                string path = text.ToString();
                return File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
            }
            finally { CloseHandle(process); }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or EntryPointNotFoundException) { return null; }
    }

    // Where an executable of this name lives: the running process first, then the usual install folders on every drive, not
    // only the system drive (a program installed on D: is just as likely to be the one sending notices).
    private static string? PathOf(string name)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                try { if (PathOfProcess((uint)process.Id) is { } running) return running; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                finally { process.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                folders.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
                folders.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string[] relative = name switch
        {
            "Weixin.exe" => [@"Tencent\Weixin\Weixin.exe", @"Weixin\Weixin.exe", @"Tencent\WeChat\WeChat.exe", @"WeChat\WeChat.exe"],
            "WeChat.exe" => [@"Tencent\WeChat\WeChat.exe", @"WeChat\WeChat.exe", @"Tencent\Weixin\Weixin.exe"],
            "QQ.exe" => [@"Tencent\QQNT\QQ.exe", @"Tencent\QQ\Bin\QQ.exe", @"QQNT\QQ.exe"],
            "outlook.exe" => [@"Microsoft Office\root\Office16\OUTLOOK.EXE"],
            _ => [name]
        };
        foreach (var folder in folders.Where(folder => folder.Length > 0 && Directory.Exists(folder)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var tail in relative)
            {
                string path = Path.Combine(folder, tail);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    private static string? PathOfProcess(uint pid)
    {
        IntPtr process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var text = new StringBuilder(512);
            int size = text.Capacity;
            if (!QueryFullProcessImageName(process, 0, text, ref size)) return null;
            string path = text.ToString();
            return File.Exists(path) ? path : null;
        }
        finally { CloseHandle(process); }
    }

    // The icon inside an executable, as a picture the panel can draw at any size. Windows' own icon for the file stands in
    // when ExtractIconEx finds no icon group at index 0 (some installers keep theirs elsewhere).
    public static BitmapSource? FromFile(string path)
    {
        if (Cache.TryGetValue(path, out var known) && known is not null) return known;
        BitmapSource? icon = null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            int count = ExtractIconEx(path, 0, ref large, ref small, 1);
            IntPtr handle = large != IntPtr.Zero ? large : small;
            if (count > 0 && handle != IntPtr.Zero) icon = FromHandle(handle, owned: false);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException) { }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
        icon ??= FromShell(path);
        if (icon is not null) Cache[path] = icon;
        return icon;
    }

    // The icon a window shows on the taskbar: one a program set with WM_SETICON, or the icon registered by its window class.
    // The handles belong to the window, so a private copy is made before it is turned into a bitmap.
    public static BitmapSource? FromWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return null;
        try
        {
            foreach (int which in new[] { 1, 0, 2 })
            {
                if (SendMessageTimeout(window, WmGetIcon, (IntPtr)which, IntPtr.Zero, SmtoAbortIfHung, 200, out IntPtr icon) != IntPtr.Zero
                    && icon != IntPtr.Zero && FromHandle(icon, owned: false) is { } shown) return shown;
            }
            foreach (int index in new[] { GclpIcon, GclpSmallIcon })
                if (FromHandle(GetClassLongPtr(window, index), owned: false) is { } registered) return registered;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException) { }
        return null;
    }

    private static BitmapSource? FromHandle(IntPtr handle, bool owned)
    {
        if (handle == IntPtr.Zero) return null;
        IntPtr copy = owned ? handle : CopyIcon(handle);
        if (copy == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(copy, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        finally { DestroyIcon(copy); }
    }

    // The icon Windows keeps for a file; it also answers for executables whose icon group ExtractIconEx could not read.
    private static BitmapSource? FromShell(string path)
    {
        try
        {
            var info = new ShellFileInfo();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero) return null;
            return FromHandle(info.Icon, owned: true);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    private const uint WmGetIcon = 0x007F, SmtoAbortIfHung = 0x0002, QueryLimitedInformation = 0x1000;
    private const uint ShgfiIcon = 0x0100, ShgfiLargeIcon = 0x0000;
    private const int GclpIcon = -14, GclpSmallIcon = -34;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, ref IntPtr large, ref IntPtr small, int count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref int size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
