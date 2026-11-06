using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// The last Kimi Code quota read online: its windows, the extra-usage metrics, the membership level and when it was read.
public sealed record KimiQuotaReading(DateTimeOffset Time, string Plan, List<UsageMetric> Metrics);

// The last Kimi Code quota read online, kept on disk. Kimi Code's sign-in lasts about a quarter of an hour and is renewed
// only while Kimi Code is in use, so most refreshes find it expired and send nothing; the reading from before then stands
// in, after a restart too. It holds the metrics, the membership level and when they were read, never a token or anything
// else /me returns. As with ClaudeQuotaCache, the account is recorded only as a one-way hash, and the file is used only
// for the same account; a reading older than MaxAge is not used, and a window that has reset since is left out.
public static class KimiQuotaCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const string FileName = "kimi-quota.json";
    // How old the kept reading may grow before a refresh that read the same values writes the file again.
    public static readonly TimeSpan RewriteAfter = TimeSpan.FromMinutes(10);

    private sealed record StoredMetric(MetricKind Kind, string Label, string? Id, int? Window, double? Percent, decimal? Amount, decimal? Total, string? Currency, DateTimeOffset? ResetAt);
    private sealed record Stored(string? Account, DateTimeOffset Time, string? Plan, List<StoredMetric?>? Metrics);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    // A one-way hash of the service address and the account id, enough to tell two accounts apart without naming either.
    // "" when the account is unknown.
    public static string AccountHash(string baseUrl, string account) => string.IsNullOrWhiteSpace(account) ? ""
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TwelveIsland Kimi account " + baseUrl.Trim().ToLowerInvariant() + "\n" + account.Trim())))[..32];

    public static string Serialize(KimiQuotaReading reading, string accountHash) => JsonSerializer.Serialize(new Stored(accountHash, reading.Time, reading.Plan,
        reading.Metrics.Select(m => (StoredMetric?)new StoredMetric(m.Kind, m.Label, m.Id, m.Window, m.Percent, m.Amount, m.Total, m.Currency, m.ResetAt)).ToList()), Options);

    public static KimiQuotaReading? Parse(string json, DateTimeOffset now, string accountHash)
    {
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json, Options); }
        catch (JsonException) { return null; }
        if (stored?.Metrics is null || accountHash.Length == 0 || stored.Account != accountHash) return null;
        var metrics = stored.Metrics.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Label) && m.Kind is MetricKind.Percent or MetricKind.Balance or MetricKind.Spend
                && (m.Kind != MetricKind.Percent || m.Percent is { } p && double.IsFinite(p)) && (m.Kind == MetricKind.Percent || m.Amount is not null))
            .Select(m => new UsageMetric(m!.Kind, m.Label) { Id = m.Id ?? m.Label, Window = m.Window, Percent = m.Percent, Amount = m.Amount, Total = m.Total, Currency = m.Currency ?? "", ResetAt = m.ResetAt })
            .ToList();
        return Usable(new(stored.Time, stored.Plan ?? "", metrics), now);
    }

    // A reading from the future (a clock set back since) or too old is not the account's quota, and a window that has reset
    // since it was read is left out: its share no longer says anything. Null when no window is left.
    public static KimiQuotaReading? Usable(KimiQuotaReading reading, DateTimeOffset now)
    {
        if (reading.Time > now.AddMinutes(5) || now - reading.Time > MaxAge) return null;
        var metrics = reading.Metrics.Where(m => m.ResetAt is not { } reset || reset > now).ToList();
        return metrics.Any(m => m.Kind == MetricKind.Percent) ? reading with { Metrics = metrics } : null;
    }

    public static KimiQuotaReading? Load(string path, DateTimeOffset now, string accountHash)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), now, accountHash) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Written whole and then moved into place, so an exit midway never leaves half a file. A failed write keeps the previous file.
    public static void Save(string path, KimiQuotaReading reading, string accountHash)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(reading, accountHash));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
