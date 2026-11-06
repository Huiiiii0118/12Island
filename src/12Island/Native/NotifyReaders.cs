using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TwelveIsland.Native;

// One notice as a source found it, before the panel keeps it. Program is the key the notice's program is known by
// (NotifyPrograms), so its icon and its window can be found again.
internal sealed record NotifyArrival(Core.NotifySource Source, string Program, string Title, string Body, string App);

// Reads the notices Windows keeps, for the three programs this page shows. A notification that an application posts is
// stored, with the text it showed, in the notification database; the database is copied aside and read with SQLite because the
// service holds it open.
internal sealed class ToastReader
{
    private const string Database = "wpndatabase.db";
    private readonly string _source;
    private readonly string _copy = Path.Combine(Path.GetTempPath(), "12Island", "toasts");
    private long _lastArrival;
    private bool _seeded;
    private long _lastStamp;

    public ToastReader()
    {
        _source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
        try { Directory.CreateDirectory(_copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // The notifications that arrived since the last call, newest last. The first call only notes where the store is now, so
    // history is not replayed as new.
    public IReadOnlyList<NotifyArrival> Poll()
    {
        var found = new List<NotifyArrival>();
        string source = Path.Combine(_source, Database);
        if (!File.Exists(source)) return found;
        // A database that has not been written since the last look holds nothing new: reading it again would only cost time.
        long stamp = 0;
        foreach (var path in new[] { source, source + "-wal" })
            try { if (File.Exists(path)) stamp += File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { }
        if (stamp != 0 && stamp == _lastStamp) return found;
        _lastStamp = stamp;
        string target = Path.Combine(_copy, Database);
        try
        {
            // A copy keeps the service's own file free. The database itself has to come along; without it there is nothing to read.
            File.Copy(source, target, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }
        // The write-ahead log holds the newest notifications. The service keeps -shm memory-mapped and a copy of either
        // side file can fail at any moment; one that could not be refreshed is removed rather than paired with a newer
        // database copy, and the read goes on with what arrived.
        foreach (var suffix in new[] { "-wal", "-shm" })
            try
            {
                if (File.Exists(source + suffix)) File.Copy(source + suffix, target + suffix, true);
                else if (File.Exists(target + suffix)) File.Delete(target + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(target + suffix)) File.Delete(target + suffix); } catch { }
            }
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select n.Payload, h.PrimaryId, n.ArrivalTime from Notification n "
                + "join NotificationHandler h on h.RecordId = n.HandlerId where n.ArrivalTime > $since order by n.ArrivalTime";
            command.Parameters.AddWithValue("$since", _lastArrival);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long arrival = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                if (arrival > _lastArrival) _lastArrival = arrival;
                if (!_seeded) continue;
                string app = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string payload = reader.IsDBNull(0) ? "" : Encoding.UTF8.GetString((byte[])reader.GetValue(0));
                if (NotifyPrograms.FromToast(app) is not { } program) continue;
                var (title, body) = ToastText.Parse(payload);
                if (title.Length == 0 && body.Length == 0) continue;
                found.Add(new NotifyArrival(program.Source, program.Key, title, body, program.Name));
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidCastException) { }
        _seeded = true;
        return found;
    }

}

// The title and the body of a notification's payload: the first two text elements of its layout.
internal static class ToastText
{
    public static (string Title, string Body) Parse(string payload)
    {
        if (payload.Length == 0) return ("", "");
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(payload);
            var texts = document.Descendants().Where(element => element.Name.LocalName == "text")
                .Select(element => element.Value.Trim()).Where(text => text.Length > 0).ToList();
            return texts.Count switch
            {
                0 => ("", ""),
                1 => (texts[0], ""),
                _ => (texts[0], string.Join(" ", texts.Skip(1).Take(2)))
            };
        }
        catch (System.Xml.XmlException) { return ("", ""); }
    }
}

// Watches the windows of the chat and office programs. A message that is not in front makes the program put up its own small
// window with the sender's name on it; a new one of those is a new notice. The text inside is read where the program offers it
// to the accessibility tree, and the sender's name serves as the title where it does not.
internal sealed class WindowReader
{
    private static readonly string[] MainTitles = ["微信", "weixin", "wechat", "qq", "腾讯qq", "钉钉", "飞书", "企业微信"];
    // Windows a program keeps for itself: message-only helpers, the embedded browser, input methods. A message window is one of
    // the program's own small windows instead.
    private static readonly string[] IgnoredClasses =
    [
        "Chrome_WidgetWin", "SystemMessageWindow", "TrayIcon", "PowerMessage", "libusb", "DisplayICC", "IME", "MSCTFIME",
        "Intermediate D3D Window", "TabletPenServiceHelperClass"
    ];
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<int, NotifyProgram> _names = [];
    private bool _seeded;

    public IReadOnlyList<NotifyArrival> Poll()
    {
        var found = new List<NotifyArrival>();
        var windows = new List<(IntPtr Handle, int Pid, string Class, string Title, int Width, int Height, bool Topmost)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            int length = GetWindowTextLength(handle);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (title.Length == 0) return true;
            string name = ClassName(handle);
            if (IgnoredClasses.Any(ignored => name.Contains(ignored, StringComparison.OrdinalIgnoreCase))) return true;
            GetWindowThreadProcessId(handle, out int pid);
            var rect = new Rect(); if (!GetWindowRect(handle, ref rect)) return true;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            // A message window is small; anything near a full window is a window the user opened.
            if (width is < 140 or > 620 || height is < 50 or > 460) return true;
            bool topmost = (GetWindowLong(handle, -20) & 0x8) != 0;
            windows.Add((handle, (int)pid, name, title.ToString().Trim(), width, height, topmost));
            return true;
        }, IntPtr.Zero);
        var live = new HashSet<string>();
        foreach (var window in windows)
        {
            if (ProgramOf(window.Pid) is not { } program) continue;
            string title = window.Title;
            if (title.Length == 0 || MainTitles.Contains(title.ToLowerInvariant())) continue;
            // A page or a file opened in the program's own browser is not a message.
            if (title.StartsWith("http", StringComparison.OrdinalIgnoreCase) || title.Contains("://", StringComparison.Ordinal)) continue;
            // A message window is either kept above everything or small enough to be a corner notice.
            if (!window.Topmost && (window.Width > 460 || window.Height > 280)) continue;
            string key = $"{program.Key}|{title}";
            live.Add(key);
            if (_seen.Contains(key)) continue;
            _seen.Add(key);
            if (!_seeded) continue;
            string body = ReadText(window.Handle, title);
            found.Add(new NotifyArrival(program.Source, program.Key, title, body, program.Name));
        }
        // A window that has gone may come back with the same name when the next message arrives.
        _seen.IntersectWith(live);
        _seeded = true;
        return found;
    }

    // Only the programs the page listens to count; a small window of any other app is not a notice.
    private NotifyProgram? ProgramOf(int pid)
    {
        if (_names.TryGetValue(pid, out var known)) return known;
        string name = "";
        try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        var program = NotifyPrograms.FromProcess(name);
        if (_names.Count > 400) _names.Clear();
        if (program is not null) _names[pid] = program;
        return program;
    }

    // The text a message window shows, where the program offers it: the sender's name is already the title, so the rest of the
    // window's text is the message.
    private static string ReadText(IntPtr hwnd, string title)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null) return "";
            var texts = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<System.Windows.Automation.AutomationElement>()
                .Select(element => element.Current.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => name.Length > 1 && name != title && !Chrome(name))
                .Distinct().Take(4).ToList();
            // The longest name is usually the message; the window's own chrome is short.
            return texts.OrderByDescending(text => text.Length).FirstOrDefault() ?? "";
        }
        catch (Exception ex) when (ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException
            or COMException or NotSupportedException or ArgumentException) { return ""; }
    }

    // Names a window's frame gives itself rather than the message it shows.
    private static bool Chrome(string name) =>
        name.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSCTF", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Default IME", StringComparison.OrdinalIgnoreCase) || name is "最小化" or "最大化" or "关闭" or "还原"
        || name.Contains("Legacy Window", StringComparison.OrdinalIgnoreCase);

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, ref Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
}
