using System.Text.Json;

namespace TwelveIsland.Core;

public sealed record ClaudeUsageSample(DateTimeOffset Time, double FiveHour, double SevenDay, string Organization = "");

// The Claude desktop application keeps "plan-usage-history.json" beside its configuration: periodic
// samples of the signed-in account's five-hour ("fh") and seven-day ("sd") utilisation percentages.
// It carries no reset times and no credentials, and is written by the application itself. Each sample names the organization
// ("org") the application was using; organization, when given, keeps only that organization's samples (and those naming none).
public static class ClaudeUsageHistory
{
    public static ClaudeUsageSample? Parse(JsonElement root, string organization = "")
    {
        ClaudeUsageSample? latest = null;
        foreach (var sample in root.Get("samples").Items())
        {
            var usage = sample.Get("u");
            var org = sample.Get("org").Text();
            if (organization.Length > 0 && org.Length > 0 && !org.Equals(organization, StringComparison.OrdinalIgnoreCase)) continue;
            if (sample.Get("t").Date() is not { } time || usage.Get("fh").Number() is not { } fiveHour || usage.Get("sd").Number() is not { } sevenDay) continue;
            if (latest is null || time > latest.Time) latest = new(time, Math.Clamp(fiveHour, 0, 100), Math.Clamp(sevenDay, 0, 100), org);
        }
        return latest;
    }

    public static ClaudeUsageSample? Latest(DataLocations locations, string organization = "")
    {
        ClaudeUsageSample? latest = null;
        foreach (var profile in locations.ClaudeDesktopProfiles())
        {
            try
            {
                var path = Path.Combine(profile, "plan-usage-history.json");
                if (!File.Exists(path)) continue;
                using var doc = SharedFile.Parse(path);
                if (Parse(doc.RootElement, organization) is { } sample && (latest is null || sample.Time > latest.Time)) latest = sample;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return latest;
    }
}
