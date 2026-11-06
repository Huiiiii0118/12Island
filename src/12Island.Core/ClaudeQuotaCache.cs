using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwelveIsland.Core;

// The last Claude quota read online, kept on disk. Only the account API reports the per-model quotas (Fable and the like) and
// the reset times; the Claude desktop app's local records carry the 5-hour and weekly shares alone. A start while the API is
// rate limited therefore showed two quotas and no Fable page until a request got through. With this file the reading from
// before the restart stands in, as the last reading of the same run already did (UsageService.Freshest).
// It holds the quotas, the plan and when they were read, never a token. A reading older than MaxAge is not used, and a quota
// whose window has reset since is left out: its share no longer says anything.
// It also says which account it was read for, as a hash of the account id (AccountHash), never the id itself. It is used only
// for the same account: after a switch of account while TwelveIsland was closed, the other account's quotas and plan would
// otherwise stand in for the new one's. A file that names no account (one written before the hash was kept) or a sign-in
// whose account cannot be told is not used either.
// One account can read several organizations (a personal subscription and a Team, say), each with quotas of its own. The file
// keeps one reading for each, named by a hash of the organization id ("" when the sign-in did not name one); a file of earlier
// versions holds a single reading, which counts as the one of the organization "".
public static class ClaudeQuotaCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const string FileName = "claude-quota.json";
    // How old the kept reading may grow before a refresh that read the same values writes the file again (UsageService.KeepOnline).
    public static readonly TimeSpan RewriteAfter = TimeSpan.FromMinutes(10);

    // The sources are kept as which sign-in and which plan origin, not as text: a text written in the language of the day would
    // still show in that language after a change of language and a restart. They are named again when the file is read.
    // Quota labels are already language-neutral (Loc.Label translates them where they are shown).
    private sealed record Stored(string? Account, DateTimeOffset Time, string? Plan, ClaudePlanOrigin PlanFrom, ClaudeSignIn SignIn, List<Quota>? Quotas, string? Organization = null);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    // A one-way hash of the account id, enough to tell two accounts apart without naming either. "" when the id is unknown.
    public static string AccountHash(string account) => Hash("TwelveIsland Claude account ", account);
    public static string OrganizationHash(string organization) => Hash("TwelveIsland Claude organization ", organization);
    private static string Hash(string purpose, string id) => string.IsNullOrWhiteSpace(id) ? ""
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + id.Trim().ToLowerInvariant())))[..32];

    public static string Serialize(ClaudeQuotaSnapshot snapshot, string account, ClaudeSignIn signIn, ClaudePlanOrigin planFrom, string organization = "") =>
        JsonSerializer.Serialize(Entry(snapshot, account, signIn, planFrom, organization), Options);

    private static Stored Entry(ClaudeQuotaSnapshot snapshot, string account, ClaudeSignIn signIn, ClaudePlanOrigin planFrom, string organization) =>
        new(AccountHash(account), snapshot.Time, snapshot.Plan, planFrom, signIn, snapshot.Quotas, OrganizationHash(organization));

    // The readings a file holds: a list, or the single reading earlier versions wrote. A damaged file holds none.
    private static List<Stored> Entries(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.Deserialize<List<Stored?>>(Options)?.OfType<Stored>().ToList() ?? [],
                JsonValueKind.Object => doc.RootElement.Deserialize<Stored>(Options) is { } single ? [single] : [],
                _ => []
            };
        }
        catch (JsonException) { return []; }
    }

    // account is the id of the account signed in now, organization the id of the organization whose reading is wanted.
    public static ClaudeQuotaSnapshot? Parse(string json, DateTimeOffset now, string account, string organization = "")
    {
        var expected = AccountHash(account);
        var wanted = OrganizationHash(organization);
        var stored = Entries(json).FirstOrDefault(entry => (entry.Organization ?? "") == wanted);
        if (stored is null || expected.Length == 0 || stored.Account != expected) return null;
        // A reading from the future (a clock set back since) or too old is not shown as the account's quota.
        if (stored.Quotas is null || stored.Time > now.AddMinutes(5) || now - stored.Time > MaxAge) return null;
        var quotas = stored.Quotas.Where(quota => quota is not null && !string.IsNullOrWhiteSpace(quota.Label)
            && double.IsFinite(quota.UsedPercent) && (quota.ResetAt is not { } reset || reset > now)).ToList();
        if (quotas.Count == 0) return null;
        var plan = stored.Plan ?? "";
        return new(stored.Time, plan, plan.Length > 0 ? ClaudeSources.Plan(stored.PlanFrom, stored.SignIn) : "", ClaudeSources.Quota(stored.SignIn), quotas);
    }

    public static ClaudeQuotaSnapshot? Load(string path, DateTimeOffset now, string account, string organization = "")
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), now, account, organization) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Replaces the reading of this organization and keeps those of the account's other organizations; readings of another
    // account and readings past MaxAge are dropped. Written whole and then moved into place, so a reading cut short by an exit
    // never leaves half a file. A failed write keeps the previous file; the reading is still kept in memory for this run.
    public static void Save(string path, ClaudeQuotaSnapshot snapshot, string account, ClaudeSignIn signIn, ClaudePlanOrigin planFrom, string organization = "")
    {
        try
        {
            var entry = Entry(snapshot, account, signIn, planFrom, organization);
            var entries = File.Exists(path) ? Entries(File.ReadAllText(path)) : [];
            entries = [.. entries.Where(old => old.Account == entry.Account && (old.Organization ?? "") != entry.Organization && snapshot.Time - old.Time <= MaxAge), entry];
            Write(path, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(string path, List<Stored> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options));
        File.Move(temporary, path, true);
    }

    // A signed-out or rejected organization keeps no quota of its own, on disk either; null forgets every organization.
    public static void Delete(string path, string? organization = null)
    {
        try
        {
            var entries = organization is null || !File.Exists(path) ? [] : Entries(File.ReadAllText(path));
            var hash = OrganizationHash(organization ?? "");
            var kept = entries.Where(entry => (entry.Organization ?? "") != hash).ToList();
            if (kept.Count == 0) File.Delete(path);
            else if (kept.Count < entries.Count) Write(path, kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
