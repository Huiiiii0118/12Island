using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// Where a notice came from. QQ, WeChat and the mailbox are named apart for their colour and their symbol; every other chat or
// office program the page listens to is App, and is told apart by its own name.
public enum NotifySource { Qq, WeChat, Mail, App }

// One program's waiting notices, as the collapsed capsule counts them: its newest one (whose sender names the page) and the
// newest one carrying a one-time code, which the capsule shows instead of sender and count.
public sealed record NotifyGroup(string Program, NotifySource Source, string App, int Count, NotifyItem Latest, string? Code);

// One notice kept by the notification page. It stays unread, and the island keeps showing it, until the page is opened.
public sealed class NotifyItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NotifySource Source { get; set; }
    // The program that announced it: its process name (NotifyPrograms), so its icon and its window can be found again.
    public string Program { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string App { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public bool Read { get; set; }
    // What makes two notices the same one, so a re-read of the same message does not pile up: source, app, title and body.
    public string Key { get; set; } = "";

    [JsonIgnore] public string SourceName => Source switch
    {
        NotifySource.Qq => "QQ", NotifySource.WeChat => Loc.T("微信", "WeChat"), NotifySource.Mail => Loc.T("邮箱", "Mail"),
        _ => App.Length > 0 ? App : Loc.T("程序", "Program")
    };
    [JsonIgnore] public string Line => Body.Length > 0 ? Body : Title;
    [JsonIgnore] public string Age => At.LocalDateTime.Date == DateTime.Today ? At.ToString("HH:mm") : At.ToString("MM-dd HH:mm");
}

// The notices the panel keeps: the newest first, unread ones counted, the whole list capped. Saved beside the settings.
public sealed class NotifyStore
{
    public const string FileName = "notifications.json";
    public const int Limit = 200;
    private readonly string _path;
    private readonly List<NotifyItem> _items = [];

    public NotifyStore(string folder) { _path = Path.Combine(folder, FileName); Load(); }

    public IReadOnlyList<NotifyItem> Items => _items;
    public int Count => _items.Count;
    public int Unread => _items.Count(item => !item.Read);
    public NotifyItem? LatestUnread => _items.FirstOrDefault(item => !item.Read);
    // The unread notices of one program, and of the programs in the order their newest notice arrived: the collapsed capsule
    // counts by program.
    public int UnreadOf(string program) => _items.Count(item => !item.Read && item.Program == program);
    public IReadOnlyList<NotifyGroup> UnreadGroups()
    {
        var groups = new List<NotifyGroup>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            if (item.Read) continue;
            string? code = VerifyCodes.Of(item.Title, item.Body);
            if (at.TryGetValue(item.Program, out int known))
            {
                // A code is what the reader needs from that program, even when a later message pushed it down the list.
                if (code is not null && groups[known].Code is null) groups[known] = groups[known] with { Code = code };
                continue;
            }
            at[item.Program] = groups.Count;
            groups.Add(new NotifyGroup(item.Program, item.Source, item.App, UnreadOf(item.Program), item, code));
        }
        return groups;
    }
    // Raised when a notice arrives or is read, so the panel and the island draw again.
    public event Action? Changed;

    // Adds a notice unless the same one is already kept. Returns the item added, or null when it was a repeat. fresh: this
    // occurrence is a new message even when it reads exactly as one already kept (a second flash of the same program says
    // "another message" and nothing more, so its text is always the same).
    public NotifyItem? Add(NotifySource source, string program, string title, string body, string app = "", DateTimeOffset? at = null, bool fresh = false)
    {
        title = (title ?? "").Trim(); body = (body ?? "").Trim(); app = (app ?? "").Trim(); program = (program ?? "").Trim();
        if (title.Length == 0 && body.Length == 0) return null;
        string key = Key(source, app, title, body);
        // The same message arriving again within a few minutes (a re-read of a mailbox, a window renamed twice) is one notice.
        if (!fresh && _items.FirstOrDefault(item => item.Key == key && DateTimeOffset.Now - item.At < TimeSpan.FromMinutes(10)) is { } known)
        {
            if (!known.Read) return null;
            known.Read = false; known.At = at ?? DateTimeOffset.Now; known.Program = program;
            Save(); Changed?.Invoke();
            return known;
        }
        var item = new NotifyItem { Source = source, Program = program, Title = title, Body = body, App = app, At = at ?? DateTimeOffset.Now, Key = key };
        _items.Insert(0, item);
        while (_items.Count > Limit) _items.RemoveAt(_items.Count - 1);
        Save(); Changed?.Invoke();
        return item;
    }

    public void MarkRead(Guid id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } item || item.Read) return;
        item.Read = true; Save(); Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        bool any = false;
        foreach (var item in _items) { if (!item.Read) { item.Read = true; any = true; } }
        if (!any) return;
        Save(); Changed?.Invoke();
    }

    // Everything one program announced is read: its icon stopped flashing, or its window was brought up.
    public void MarkProgramRead(string program)
    {
        bool any = false;
        foreach (var item in _items) { if (!item.Read && item.Program == program) { item.Read = true; any = true; } }
        if (!any) return;
        Save(); Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        if (_items.RemoveAll(item => item.Id == id) == 0) return;
        Save(); Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear(); Save(); Changed?.Invoke();
    }

    public static string Key(NotifySource source, string app, string title, string body) => $"{source}|{app}|{title}|{body}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<NotifyItem>>(File.ReadAllText(_path));
            // Only notices that name the program that announced them are kept: a list written before programs were watched
            // loses the rest.
            if (items is not null) _items.AddRange(items.Where(item => item is not null && item.Program.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _items.Clear(); }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_items)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
