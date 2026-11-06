using System.Text;
using System.Text.Json;

namespace TwelveIsland.Core;

// One reading of the Grok Bot weekly allowance. UsedPercent is null when the account draws on a pooled enterprise
// allowance, which has no share of its own to show. ReadAt is when the desktop app took a cached reading.
public sealed record GrokBotReading(double? UsedPercent, DateTimeOffset? ResetAt, string Plan, DateTimeOffset? ReadAt = null)
{
    public List<Quota> Quotas => UsedPercent is { } used ? [new Quota("每周额度", Math.Clamp(used, 0, 100), ResetAt, 10080)] : [];
}

// The Grok Bot desktop app's own sign-in. A class rather than a record, so the token never appears in ToString.
public sealed class GrokBotSignIn(bool detected, string token, bool readFailed = false)
{
    public bool Detected { get; } = detected;
    public string Token { get; } = token;
    public bool ReadFailed { get; } = readFailed;
}

// The Grok Bot desktop app keeps its data in %APPDATA%\Grok Bot. Its weekly allowance is cached as plain JSON in
// sand-client-persistence, one file per slice, named by the lowercase unpadded base32 of the slice key
// (sand.client.slice.account.<account>.weekly-usage.cache). Online, the same figure comes from GetSandUsageStatus of
// the account service. Only the weekly allowance is read; the account's other quotas belong to the Cursor source.
public static class GrokBotUsage
{
    public const string StatusUri = "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus";
    private const string CacheSuffix = ".weekly-usage.cache";
    public static string Folder(string roaming) => Path.Combine(roaming, "Grok Bot");

    // usagePercent is the share used, 0-100, as the desktop app shows it.
    public static GrokBotReading? Status(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        return Reading(root, "usagePercent", root.Get("nextResetTimestampUtc").Date(), root.Get("grokPlanLabel"), null);
    }

    // Only a present value with a usage object is a reading; other kinds and unknown layouts are no reading.
    public static GrokBotReading? Cache(JsonElement root)
    {
        var value = root.Get("value");
        if (value.Get("kind").Text() != "present") return null;
        var reading = value.Get("reading");
        var usage = reading.Get("usage");
        if (usage.ValueKind != JsonValueKind.Object) return null;
        return Reading(usage, "percentUsed", usage.Get("nextResetMs").Date(), usage.Get("grokPlanLabel"), reading.Get("readAtMs").Date());
    }

    private static GrokBotReading? Reading(JsonElement usage, string percentKey, DateTimeOffset? reset, JsonElement plan, DateTimeOffset? readAt)
    {
        bool pooled = usage.Get("usesPooledEnterpriseAllowance").ValueKind == JsonValueKind.True;
        var percent = usage.Get(percentKey).Number();
        if (!pooled && percent is null) return null;
        return new(pooled ? null : percent, reset, plan.Text().Trim(), readAt);
    }

    // The newest cached weekly allowance; more than one account may have signed in on this PC. A file that cannot be
    // read or parsed is skipped.
    public static GrokBotReading? ReadCache(string folder)
    {
        var directory = Path.Combine(folder, "sand-client-persistence");
        GrokBotReading? newest = null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            foreach (var file in Directory.EnumerateFiles(directory, "*.blob"))
            {
                if (SliceName(Path.GetFileNameWithoutExtension(file)) is not { } name || !name.EndsWith(CacheSuffix, StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = SharedFile.Parse(file);
                    if (Cache(doc.RootElement) is not { } reading) continue;
                    if (reading.ReadAt is null) reading = reading with { ReadAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) };
                    if (newest is null || reading.ReadAt > newest.ReadAt) newest = reading;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return newest;
    }

    // Decodes a file name written as lowercase base32 without padding; null when it is not one.
    public static string? SliceName(string encoded)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var bytes = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in encoded)
        {
            int index = alphabet.IndexOf(c);
            if (index < 0) return null;
            buffer = (buffer << 5) | index; bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1; }
        }
        try { return new UTF8Encoding(false, true).GetString([.. bytes]); }
        catch (DecoderFallbackException) { return null; }
    }
}
