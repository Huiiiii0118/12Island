using System.Globalization;
using System.Text.Json;

namespace TwelveIsland.Core;

public sealed record Quota(string Label, double UsedPercent, DateTimeOffset? ResetAt, int? Minutes = null)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
}

// Each input category is disjoint. Reasoning tokens are already included in Output.
public sealed record TokenEntry(string Key, DateTimeOffset Time, string Model,
    long Input, long CacheRead, long CacheWrite, long CacheWriteHour, long Output, decimal? ReportedCostUsd = null)
{
    public long Total => Input + CacheRead + CacheWrite + CacheWriteHour + Output;
}

// The most recent account quota values; Local marks values taken from the desktop application's own usage history.
// Claude and Grok Bot each keep one. ScopedTime is when the per-model quotas (Fable and the like) were read, where a local
// sample newer than them carries them over from an earlier online reading.
public sealed record ClaudeQuotaSnapshot(DateTimeOffset Time, string Plan, string PlanSource, string Source, List<Quota> Quotas, bool Local = false, DateTimeOffset? ScopedTime = null);

public sealed class ProviderSnapshot(ProviderId id)
{
    public ProviderId Id { get; } = id;
    public string Name => AppPresets.Name(Id);
    public HashSet<string> Configurations { get; set; } = [];
    public string Scope { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = [];
    public bool IsStale { get; set; }
    private List<UsageMetric>? _metrics;
    public List<UsageMetric> Metrics { get => _metrics ?? Quotas.Select(UsageMetric.FromQuota).ToList(); set => _metrics = value; }
    public string Plan { get; set; } = "";
    public string PlanSource { get; set; } = "";
    public string Status { get; set; } = Loc.T("等待更新", "Waiting for update");
    public string StatusLabel { get; set; } = Loc.T("等待更新", "Waiting for update");
    public string Source { get; set; } = "";
    public string UsageNote { get; set; } = "";
    public DateTimeOffset? QuotaTime { get; set; }
    public bool LiveQuota { get; set; }
    public bool UsageAvailable { get; set; }
    public bool UsageComplete { get; set; } = true;
    public DateTime? UsageStart { get; set; }
    public List<Quota> Quotas { get; set; } = [];
    public List<TokenEntry> Entries { get; set; } = [];
    // A shallow copy: setting a property of the copy leaves this snapshot as it was, but the two share their lists.
    public ProviderSnapshot Copy() => (ProviderSnapshot)MemberwiseClone();
}

public static class JsonValue
{
    public static JsonElement Get(this JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;
    public static string Text(this JsonElement obj) => obj.ValueKind == JsonValueKind.String ? obj.GetString() ?? "" : "";
    public static double? Number(this JsonElement obj)
    {
        if (obj.ValueKind == JsonValueKind.Number && obj.TryGetDouble(out var value) && double.IsFinite(value)) return value;
        if (obj.ValueKind == JsonValueKind.String && double.TryParse(obj.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return value;
        return null;
    }
    public static long Count(this JsonElement obj) => (long)Math.Clamp(obj.Number() ?? 0, 0, 1e15);
    public static DateTimeOffset? Date(this JsonElement obj)
    {
        if (obj.Number() is { } epoch)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch > 100_000_000_000 ? epoch : epoch * 1000)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(obj.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }
    public static IEnumerable<JsonElement> Items(this JsonElement obj) => obj.ValueKind == JsonValueKind.Array ? obj.EnumerateArray() : [];
}
