using System.Globalization;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

// The interface language. Each text is written as a pair where it is used: Loc.T("中文", "English").
// Some Chinese labels also serve as identifiers: quota names that are compared, split at "·" and kept with retained
// quotas, and player names that route commands and lyrics. They stay Chinese in the data and are translated where they
// are shown, by Label.
public static class Loc
{
    public const string Chinese = "zh-CN", English = "en-US";
    public static bool IsEnglish { get; private set; }
    public static string Language => IsEnglish ? English : Chinese;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);

    public static void Use(string language) => IsEnglish = Normalize(language) == English;
    public static string Normalize(string? language) => language == Chinese ? Chinese : English;
    public static string T(string chinese, string english) => IsEnglish ? english : chinese;
    // An English count with its noun: "1 item", "2 items".
    public static string Count(long count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    // The Windows display language decides the language of a first start and of "恢复默认". Every Chinese variant,
    // Traditional included, shows Simplified Chinese; any other language shows English.
    public static string SystemLanguage() => SystemLanguage(CultureInfo.CurrentUICulture);
    public static string SystemLanguage(CultureInfo culture) => culture.TwoLetterISOLanguageName == "zh" ? Chinese : English;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["5 小时额度"] = "5-hour limit", ["每周额度"] = "Weekly limit", ["每月额度"] = "Monthly limit", ["主要额度"] = "Primary limit", ["次要额度"] = "Secondary limit",
        ["账期额度"] = "Billing period limit", ["套餐额度"] = "Plan limit",
        ["5 小时"] = "5-hour", ["每日"] = "Daily", ["每周"] = "Weekly", ["每月"] = "Monthly", ["主要"] = "Primary", ["次要"] = "Secondary",
        ["账期"] = "Billing period", ["套餐"] = "Plan", ["其他"] = "Other",
        ["Cursor 模型"] = "Cursor models", ["其他模型"] = "Other models", ["工具调用"] = "Tool calls",
        ["账户余额"] = "Account balance", ["密钥余额"] = "Key balance", ["今日扣费"] = "Charged today", ["本月扣费"] = "Charged this month", ["预算余额"] = "Budget left", ["密钥累计"] = "Key total",
        ["赠送余额"] = "Granted balance", ["累计消费"] = "Total cost", ["加油包"] = "Extra usage", ["本月加油包"] = "Extra usage this month",
        ["次"] = "calls", ["积分"] = "credits", ["令牌"] = "tokens",
        ["网易云音乐"] = "NetEase Cloud Music", ["QQ 音乐"] = "QQ Music", ["酷狗音乐"] = "KuGou Music", ["爱奇艺"] = "iQIYI",
    };

    // Translates a label that the data keeps in Chinese. Labels made of parts joined by "·" are translated part by part;
    // labels this table does not know, such as those a custom script returns, are shown as they are.
    public static string Label(string text)
    {
        if (!IsEnglish || text.Length == 0) return text;
        if (text.Contains('·')) return string.Join(" · ", text.Split('·').Select(part => Label(part.Trim())));
        if (Labels.TryGetValue(text, out var english)) return english;
        if (Regex.Match(text, @"^(\d+) 分钟(额度)?$") is { Success: true } minutes)
            return minutes.Groups[1].Value + "-minute" + (minutes.Groups[2].Success ? " limit" : "");
        if (Regex.Match(text, @"^(\d+) 小时(额度)?$") is { Success: true } hours)
            return hours.Groups[1].Value + "-hour" + (hours.Groups[2].Success ? " limit" : "");
        if (text.EndsWith(" 每周", StringComparison.Ordinal)) return text[..^3] + " weekly";
        return text;
    }
}
