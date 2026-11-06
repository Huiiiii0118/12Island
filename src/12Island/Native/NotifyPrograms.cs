using TwelveIsland.Core;

namespace TwelveIsland.Native;

// One program the notification page listens to.
internal sealed record NotifyProgram(string Key, string Name, NotifySource Source);

// The chat and office programs whose waiting messages this island shows. What is watched is the flash of their taskbar or dock
// button — the shell announces every flash — so a program that is listed here is covered wherever its icon sits, and a program
// that is not listed is shown by nothing. Each entry names the programs (their process names) that count as the same one.
internal static class NotifyPrograms
{
    private sealed record Entry(string Key, string Name, NotifySource Source, string[] Processes);

    private static readonly Entry[] Entries =
    [
        new("weixin", "微信", NotifySource.WeChat, ["weixin", "wechat"]),
        new("qq", "QQ", NotifySource.Qq, ["qq", "qqnt"]),
        new("tim", "TIM", NotifySource.Qq, ["tim"]),
        new("wxwork", "企业微信", NotifySource.App, ["wxwork", "wework", "wecom"]),
        new("dingtalk", "钉钉", NotifySource.App, ["dingtalk"]),
        new("feishu", "飞书", NotifySource.App, ["feishu", "lark"]),
        new("teams", "Teams", NotifySource.App, ["teams", "ms-teams"]),
        new("slack", "Slack", NotifySource.App, ["slack"]),
        new("discord", "Discord", NotifySource.App, ["discord"]),
        new("telegram", "Telegram", NotifySource.App, ["telegram"]),
        new("whatsapp", "WhatsApp", NotifySource.App, ["whatsapp"]),
        new("line", "LINE", NotifySource.App, ["line"]),
        new("skype", "Skype", NotifySource.App, ["skype"]),
        new("zoom", "Zoom", NotifySource.App, ["zoom"]),
        new("wemeet", "腾讯会议", NotifySource.App, ["wemeetapp", "tencentmeeting"]),
        new("notion", "Notion", NotifySource.App, ["notion"]),
        new("outlook", "Outlook", NotifySource.Mail, ["outlook"]),
        new("foxmail", "Foxmail", NotifySource.Mail, ["foxmail"]),
        new("thunderbird", "Thunderbird", NotifySource.Mail, ["thunderbird"]),
        new("mailmaster", "网易邮箱大师", NotifySource.Mail, ["mailmaster"]),
        new("qqmail", "QQ 邮箱", NotifySource.Mail, ["qqmail"])
    ];

    private static readonly Dictionary<string, NotifyProgram> ByProcess = Build();
    private static readonly NotifyProgram[] Programs = [.. Entries.Select(entry => new NotifyProgram(entry.Key, entry.Name, entry.Source))];

    public static IReadOnlyList<NotifyProgram> All => Programs;

    // The key a notice found over IMAP carries: it names the mailbox itself, since no program had to show anything.
    public const string MailKey = "mail";
    public static readonly NotifyProgram Mailbox = new(MailKey, "邮箱", NotifySource.Mail);

    private static Dictionary<string, NotifyProgram> Build()
    {
        var map = new Dictionary<string, NotifyProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var program = new NotifyProgram(entry.Key, entry.Name, entry.Source);
            foreach (var process in entry.Processes) map[process] = program;
        }
        return map;
    }

    // A program's helper process (WeChatAppEx, QQProtect) keeps its program's name at the front. A short key must not claim
    // an unrelated process that merely begins the same way: "lineage.exe" is a game, not LINE.
    private static readonly string[] HelperSuffixes =
        ["app", "protect", "helper", "nt", "web", "browser", "update", "service", "plugin", "crash", "render", "tray", "exe"];

    // The program whose process this is, by its name: only the programs listed above count.
    public static NotifyProgram? FromProcess(string process)
    {
        string name = process.ToLowerInvariant();
        if (name.Length == 0) return null;
        if (ByProcess.TryGetValue(name, out var known)) return known;
        foreach (var (key, program) in ByProcess)
            if (name.StartsWith(key, StringComparison.Ordinal) && name.Length > key.Length
                && (key.Length >= 5 || HelperSuffixes.Any(suffix => name[key.Length..].StartsWith(suffix, StringComparison.Ordinal))))
                return program;
        return null;
    }

    // A name counts only as a whole word: "梦幻西游 ONLINE" contains "line" but is not LINE, and "outline" is not LINE either.
    // Chinese names have no word boundaries, so those are found as substrings as they always were.
    private static bool ContainsWord(string text, string word)
    {
        if (word.Any(character => character > 127)) return text.Contains(word, StringComparison.Ordinal);
        int start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + word.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1]))
                && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            start = end;
        }
        return false;
    }

    // The program a window's title names, where its process could not say: a launcher whose window is flashed still reads as
    // the program it holds.
    public static NotifyProgram? FromTitle(string title)
    {
        string id = title.Trim().ToLowerInvariant();
        if (id.Length == 0) return null;
        foreach (var (key, program) in ByProcess)
            if (ContainsWord(id, key)) return program;
        foreach (var (text, key) in Titles)
            if (ContainsWord(id, text)) return ByProcess[key];
        return null;
    }

    // The program a Windows notification belongs to, by the identifier the notification service keeps for it.
    public static NotifyProgram? FromAppId(string app)
    {
        string id = app.ToLowerInvariant();
        if (id.Length == 0) return null;
        foreach (var (key, program) in ByProcess)
            if (ContainsWord(id, key)) return program;
        foreach (var (text, key) in Titles)
            if (ContainsWord(id, text)) return ByProcess[key];
        return null;
    }

    // Chinese and other names a program goes by, for titles and notification identifiers that do not carry its process name.
    private static readonly (string Text, string Key)[] Titles =
    [
        ("微信", "weixin"), ("wechat", "weixin"), ("腾讯qq", "qq"), ("钉钉", "dingtalk"), ("飞书", "feishu"),
        ("企业微信", "wxwork"), ("邮箱", "outlook"), ("foxmail", "foxmail"), ("网易邮箱大师", "mailmaster"),
        ("腾讯会议", "wemeet"), ("邮件", "outlook")
    ];

    // The program a notification in Windows' own store belongs to. Only the chat and email programs the page was asked to
    // watch are recognised here: a notification from any other program, or from Windows and its components, is ignored, so the
    // page never reports on software it was not told to listen to. Nothing is made up from an unfamiliar identifier — that is
    // what kept every other app and the system itself from showing up.
    public static NotifyProgram? FromToast(string app) => FromAppId(app);

    // The process names a program runs under, for finding its icon and bringing its window up. A program known only by its
    // notification identifier is looked for under that same name, so its icon is still the one it shows on the taskbar.
    public static string[] Processes(string key) => Entries.FirstOrDefault(entry => entry.Key == key)?.Processes ?? [key];
}
