using System.Globalization;
using System.Text.Json;

namespace TwelveIsland.Core;

// Text is the custom-script format: the script supplies every displayed string itself.
public enum MetricKind { Percent, Balance, Spend, Count, Text }
public sealed record UsageMetric(MetricKind Kind, string Label)
{
    public string Id { get; init; } = "";
    public int? Window { get; init; }
    public double? Percent { get; init; }
    public decimal? Amount { get; init; }
    public decimal? Total { get; init; }
    public decimal? Used { get; init; }
    public string Currency { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool Unlimited { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    // The amount is a lower bound because the source could not see every record.
    public bool AtLeast { get; init; }
    // The total is the row's target balance (AppEntry.BalanceTarget), not an amount the source reported.
    public bool TotalIsTarget { get; init; }
    // Text metrics only: the big value, the grey word after it, the label row's right side and the value row's right side.
    public string Value { get; init; } = "";
    public string Suffix { get; init; } = "";
    public string Note { get; init; } = "";
    public string ResetText { get; init; } = "";
    public double? UsedPercent => Kind switch
    {
        MetricKind.Percent or MetricKind.Text => Percent,
        MetricKind.Balance when Total is > 0 && Amount is { } amount => (double)((Total.Value - amount) / Total.Value * 100),
        // An exhausted or overdrawn balance counts as fully used even when the source gives no total.
        MetricKind.Balance when Total is null or 0 && Amount <= 0 => 100,
        MetricKind.Count when Total is > 0 && Used is { } used => (double)(used / Total.Value * 100),
        _ => null
    };
    public string ValueText => Kind == MetricKind.Text ? Value : Kind is MetricKind.Balance or MetricKind.Spend ? (AtLeast ? "≥" : "") + Money(Amount!.Value, Currency) : UsedPercent is { } p ? $"{p:0.#}%" : "—";
    public string ValueSuffix => Kind switch { MetricKind.Text => Value.Length > 0 ? Suffix : "", MetricKind.Balance => Loc.T("可用", "left"), _ => Loc.T("已用", "used") };
    public string CountText => Kind == MetricKind.Count ? $"{Used:N0} / {Total:N0}" : "";
    public string Description => string.Join("\n", new[] { Kind == MetricKind.Text ? Label : Loc.Label(Label), (ValueText + " " + ValueSuffix).Trim(), Note, ResetText,
        Total is { } total ? (TotalIsTarget ? Loc.T("目标余额：", "Target balance: ") : Loc.T("总额：", "Total: ")) + (Kind == MetricKind.Count ? total.ToString("N0") + " " + Loc.Label(Unit) : Money(total, Currency)) : "",
        CountText, Unlimited ? Loc.T("无上限", "No limit") : "", ResetAt is { } reset ? Loc.T("重置时间：", "Resets: ") + $"{reset.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : "",
        ExpiresAt is { } expiry ? Loc.T("到期时间：", "Expires: ") + $"{expiry.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : "" }.Where(x => x.Length > 0));
    public static string Money(decimal amount, string currency) => (amount < 0 ? "−" : "") + (currency switch { "USD" => "$", "CNY" => "¥", "" => "", _ => currency + " " }) + Math.Abs(amount).ToString("N2", CultureInfo.InvariantCulture);
    public static UsageMetric FromQuota(Quota quota) => new(MetricKind.Percent, quota.Label)
    { Id = quota.Label, Percent = quota.UsedPercent, ResetAt = quota.ResetAt, Window = quota.Minutes };
}

public sealed record ProviderResult(List<UsageMetric> Metrics, string Plan, string Scope, Dictionary<string, string> Details, List<string> Warnings)
{
    // JSON the script asked to receive again as ctx.memory on its next run for the same connection.
    public string? Memory { get; init; }
    // The source text to show instead of the source's name, for a built-in reader that knows which service answered.
    public string Source { get; init; } = "";
}

public static class MetricParser
{
    public static ProviderResult Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return ParseText(root);
        // Objects with metrics are the typed format the built-in services still use.
        if (root.Get("metrics").ValueKind != JsonValueKind.Array) throw new InvalidDataException(Loc.T("fetchUsage 必须返回数组，每一项是一条配额。", "fetchUsage must return an array in which each item is one quota."));
        var metrics = new List<UsageMetric>(); var warnings = new List<string>();
        int index = 0;
        foreach (var item in root.Get("metrics").EnumerateArray())
        {
            if (index >= 100) { warnings.Add(Loc.T("最多显示 100 项指标。", "At most 100 metrics are shown.")); break; }
            try { metrics.Add(ParseMetric(item, index)); }
            catch (InvalidDataException ex) { warnings.Add($"metrics[{index}].{ex.Message}"); }
            index++;
        }
        if (metrics.Count == 0) throw new InvalidDataException(warnings.FirstOrDefault() ?? Loc.T("metrics 为空，未返回可显示的额度。", "metrics is empty; no quota to show was returned."));
        var details = new Dictionary<string, string>();
        if (root.Get("details").ValueKind == JsonValueKind.Object)
            foreach (var field in root.Get("details").EnumerateObject().Take(30)) details[Short(field.Name)] = Short(field.Value.ToString());
        if (root.Get("warnings").ValueKind == JsonValueKind.Array)
            warnings.AddRange(root.Get("warnings").EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Take(10).Select(w => Short(w.GetString()!)));
        return new(metrics, Short(root.Get("plan").Text()), root.Get("scope").Text(), details, warnings);
    }
    // The custom format: an array whose items are shown as they are, in order, two per page.
    private static ProviderResult ParseText(JsonElement root)
    {
        if (root.GetArrayLength() == 0) throw new InvalidDataException(Loc.T("返回的数组为空，至少需要一项。", "The returned array is empty; at least one item is needed."));
        var metrics = new List<UsageMetric>(); var warnings = new List<string>();
        int index = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (index >= 100) { warnings.Add(Loc.T("最多显示 100 项。", "At most 100 items are shown.")); break; }
            try { metrics.Add(ParseTextItem(item, index)); }
            catch (InvalidDataException ex) { warnings.Add(Loc.T($"第 {index + 1} 项：{ex.Message}", $"Item {index + 1}: {ex.Message}")); }
            index++;
        }
        if (metrics.Count == 0) throw new InvalidDataException(warnings[0]);
        return new(metrics, "", "", [], warnings);
    }
    private static UsageMetric ParseTextItem(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException(Loc.T("必须是对象。", "It must be an object."));
        string Text(string key, bool required = false)
        {
            var value = item.Get(key);
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return required ? throw new InvalidDataException(key + Loc.T(" 为必填项。", " is required.")) : "";
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException(key + Loc.T(" 必须是字符串。", " must be a string."));
            var text = value.GetString()!.Trim();
            if (required && text.Length == 0) throw new InvalidDataException(key + Loc.T(" 为必填项，不能为空。", " is required and cannot be empty."));
            return Short(text);
        }
        var title = Text("title", true);
        double? percent = null;
        var number = item.Get("percent");
        if (number.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new InvalidDataException(Loc.T("percent 必须是数字。", "percent must be a number."));
            if (value is < 0 or > 100) throw new InvalidDataException(Loc.T("percent 必须在 0 到 100 之间。", "percent must be between 0 and 100."));
            percent = value;
        }
        return new(MetricKind.Text, title) { Id = index.ToString(CultureInfo.InvariantCulture), Percent = percent,
            Value = Text("value"), Suffix = Text("suffix"), Note = Text("note"), ResetText = Text("resetAt") };
    }
    private static string Short(string text) => text.Length > 500 ? text[..500] : text;
    private static UsageMetric ParseMetric(JsonElement item, int index)
    {
        var kind = item.Get("kind").Text() switch
        { "percent" => MetricKind.Percent, "balance" => MetricKind.Balance, "spend" => MetricKind.Spend, "count" => MetricKind.Count, _ => throw new InvalidDataException(Loc.T("kind 必须是 percent、balance、spend 或 count。", "kind must be percent, balance, spend or count.")) };
        var label = item.Get("label").Text();
        if (string.IsNullOrWhiteSpace(label)) throw new InvalidDataException(Loc.T("label 缺失。", "label is missing."));
        decimal? Number(string key, bool required = false)
        {
            var value = item.Get(key);
            if (!required && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || Math.Abs(number) > 1_000_000_000_000_000_000m)
                throw new InvalidDataException(key + Loc.T(" 缺失或不是有效数字。", " is missing or not a valid number."));
            return number;
        }
        DateTimeOffset? Date(string key)
        {
            var value = item.Get(key);
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var millis))
            { try { return DateTimeOffset.FromUnixTimeMilliseconds(millis); } catch (ArgumentOutOfRangeException) { } }
            else if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
            throw new InvalidDataException(key + Loc.T(" 必须是 ISO 时间或毫秒时间戳。", " must be an ISO time or a timestamp in milliseconds."));
        }
        bool Flag(string key)
        {
            var value = item.Get(key);
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.True) return true;
            throw new InvalidDataException(key + Loc.T(" 必须是布尔值。", " must be a Boolean value."));
        }
        var percent = Number("percent", kind == MetricKind.Percent);
        var amount = Number("amount", kind is MetricKind.Balance or MetricKind.Spend);
        var total = Number("total", kind == MetricKind.Count); var used = Number("used", kind == MetricKind.Count);
        var window = Number("window");
        if (window is { } w && (w <= 0 || w > 5256000 || decimal.Truncate(w) != w)) throw new InvalidDataException(Loc.T("window 必须是正整数分钟。", "window must be a positive whole number of minutes."));
        if (percent < 0 || total < 0 || used < 0 || (kind == MetricKind.Spend && amount < 0)) throw new InvalidDataException(Loc.T("额度或已用量不能为负数。", "Quotas and used amounts cannot be negative."));
        if (kind == MetricKind.Count && total == 0) throw new InvalidDataException(Loc.T("total 必须大于 0。", "total must be greater than 0."));
        return new(kind, Short(label)) { Id = item.Get("id").Text() is { Length: > 0 } id ? Short(id) : index.ToString(CultureInfo.InvariantCulture),
            Window = window is { } win ? (int)win : null, Percent = (double?)percent, Amount = amount, Total = total, Used = used,
            Currency = Short(item.Get("currency").Text()), Unit = Short(item.Get("unit").Text()), Unlimited = Flag("unlimited"), AtLeast = Flag("atLeast"), ResetAt = Date("resetAt"), ExpiresAt = Date("expiresAt") };
    }
}
