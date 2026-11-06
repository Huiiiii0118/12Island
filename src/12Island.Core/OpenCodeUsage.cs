using System.Globalization;
using System.Text.Json;

namespace TwelveIsland.Core;

// The OpenCode sign-in that reads the Go allowance: an OAuth access token from signing in to OpenCode Console, or an API key;
// when the token expires; and the Console workspace (orgID) and address it signed in to. Held in memory only and never shown.
public sealed record OpenCodeCredential(string Token, DateTimeOffset? ExpiresAt, string Workspace, string Server)
{
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
    public override string ToString() => nameof(OpenCodeCredential);
}

// OpenCode 1.x and 2.x, the CLI and the desktop app alike, keep their data in $XDG_DATA_HOME\opencode, otherwise
// %USERPROFILE%\.local\share\opencode, in one SQLite database: opencode.db, or OPENCODE_DB (a path relative to that folder or
// absolute). The desktop app runs its own copy of the CLI as its server, so both write the same database.
//   - Sign-in: 2.x keeps it in the credential table, one row per connection, value a JSON object: { type: "oauth", access,
//     refresh, expires (ms), metadata: { server, orgID, ... } } or { type: "key", key }. Signing in to OpenCode Console is
//     the active row of integration "opencode". 1.x kept API keys in auth.json ({ "<provider>": { type: "api", key } }).
//   - Records: 2.x writes each model response to session_message (type "assistant", data { model: { id, providerID },
//     cost, tokens: { input, output, reasoning, cache: { read, write } } }); 1.x writes them to message (data with role
//     "assistant", modelID, providerID, and the same cost and tokens). Moving to 2.x leaves the 1.x tables as they were and
//     does not copy them, so both are read. input excludes the cache, and reasoning is billed as output but not counted in
//     output. cost is what OpenCode charges at its own list prices; for Go models it is what the request counted against the
//     allowance, and 0 for free models. A failed response has no tokens. A session also counts the request that names it,
//     which has no record of its own and is not read.
// The token is used as it is and never renewed: OpenCode renews it while in use (it lasts 30 days).
public static class OpenCodeUsage
{
    public const string DefaultServer = "https://opencode.ai/console";
    public static string DataFolder(string home, string? xdgData) =>
        Path.Combine(!string.IsNullOrWhiteSpace(xdgData) && Path.IsPathFullyQualified(xdgData) ? xdgData : Path.Combine(home, ".local", "share"), "opencode");
    public static string Database(string data, string? custom) =>
        string.IsNullOrWhiteSpace(custom) || custom == ":memory:" ? Path.Combine(data, "opencode.db") : Path.GetFullPath(Path.Combine(data, custom));

    // The active OpenCode Console connection, an OAuth sign-in before an API key; otherwise an OpenCode Go or OpenCode API key
    // that 1.x keeps in auth.json. Null when there is none.
    public static OpenCodeCredential? ReadCredential(IDesktopSources desktop, string database, string data)
    {
        var rows = desktop.QueryDatabase(database, "SELECT value FROM credential WHERE active = 1 AND integration_id = 'opencode' ORDER BY time_updated DESC") ?? [];
        var found = rows.Select(row => Parse(row.FirstOrDefault())).Where(c => c is not null).OrderByDescending(c => c!.ExpiresAt is not null).FirstOrDefault();
        if (found is not null) return found;
        try
        {
            using var auth = SharedFile.ReadJson(Path.Combine(data, "auth.json"));
            foreach (var name in new[] { "opencode-go", "opencode" })
            {
                var entry = auth?.RootElement.Get(name) ?? default;
                if (entry.Get("type").Text() == "api" && entry.Get("key").Text().Trim() is { Length: > 0 } key) return new(key, null, "", DefaultServer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    public static OpenCodeCredential? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement; var metadata = root.Get("metadata");
            var server = metadata.Get("server").Text() is { Length: > 0 } text ? text : DefaultServer;
            var workspace = metadata.Get("orgID").Text();
            return root.Get("type").Text() switch
            {
                "oauth" when root.Get("access").Text() is { Length: > 0 } token => new(token, root.Get("expires").Date(), workspace, server),
                "key" when root.Get("key").Text() is { Length: > 0 } key => new(key, null, workspace, server),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    // The Go allowance is read from the inference API, which shares the Console's origin. Sign-ins saved before August 2026
    // name the retired console.opencode.ai, which maps onto opencode.ai. Only an HTTPS address is used.
    public static Uri? UsageAddress(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host.Equals("console.opencode.ai", StringComparison.OrdinalIgnoreCase) ? "opencode.ai" : uri.Host;
        return new UriBuilder(Uri.UriSchemeHttps, host, uri.IsDefaultPort ? -1 : uri.Port, "/zen/go/v1/usage").Uri;
    }

    // GET /zen/go/v1/usage: { usage: { rolling, weekly, monthly: { status: "ok" | "rate-limited", percent, resetsAt } } }.
    // The rolling window is 5 hours; a workspace without Go is refused with HTTP 403. The answer does not say whether the
    // plan is Go or Go Plus: each percentage is of that plan's own limit.
    public static List<Quota> Allowance(JsonElement root, out bool limited)
    {
        var quotas = new List<Quota>(); limited = false;
        var usage = root.Get("usage");
        foreach (var (name, label, minutes) in new[] { ("rolling", "5 小时额度", (int?)300), ("weekly", "每周额度", 7 * 24 * 60), ("monthly", "每月额度", null) })
        {
            var meter = usage.Get(name);
            if (meter.Get("percent").Number() is not { } percent) continue;
            limited |= meter.Get("status").Text() == "rate-limited";
            quotas.Add(new(label, Math.Clamp(percent, 0, 100), meter.Get("resetsAt").Date(), minutes));
        }
        return quotas;
    }

    public static void Read(ProviderSnapshot snapshot, IDesktopSources desktop, string database, DateTimeOffset start, DateTimeOffset now)
    {
        snapshot.UsageAvailable = File.Exists(database);
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T("未找到本机 OpenCode 记录。", "No local OpenCode records were found."); return; }
        var tables = desktop.QueryDatabase(database, "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('session_message', 'message')")?
            .Select(row => row[0]).ToHashSet();
        if (tables is null) { snapshot.UsageComplete = false; snapshot.UsageNote = Loc.T("OpenCode 记录暂时无法读取。", "The OpenCode records cannot be read right now."); return; }
        var since = start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        const string Fields = "id, session_id, time_created, json_extract(data, '$.model.id'), json_extract(data, '$.modelID'), json_extract(data, '$.tokens.input'), " +
            "json_extract(data, '$.tokens.cache.read'), json_extract(data, '$.tokens.cache.write'), json_extract(data, '$.tokens.output'), json_extract(data, '$.tokens.reasoning'), json_extract(data, '$.cost')";
        var rows = new List<string?[]>();
        bool complete = true;
        void Query(string table, string where)
        {
            if (!tables.Contains(table)) return;
            if (desktop.QueryDatabase(database, $"SELECT {Fields} FROM {table} WHERE {where} AND time_created >= {since}") is { } found) rows.AddRange(found);
            else complete = false;
        }
        Query("session_message", "type = 'assistant'");
        Query("message", "json_extract(data, '$.role') = 'assistant'");
        snapshot.Entries = Parse(rows).Where(e => e.Time >= start && e.Time <= now && e.Total > 0).OrderBy(e => e.Time).ToList();
        snapshot.UsageComplete = complete;
        int sessions = rows.Where(row => row.Length > 1 && row[1] is not null).Select(row => row[1]).Distinct().Count();
        snapshot.UsageNote = Loc.T($"本机 OpenCode {start.LocalDateTime:yyyy-MM-dd} 起 · {sessions} 个会话 · {snapshot.Entries.Count} 次模型请求",
            $"Local OpenCode since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(sessions, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
        if (!complete) snapshot.UsageNote += Loc.T(" · 部分记录未能读取", " · Some records could not be read");
    }

    // Rows of: id, session, time (ms), model (2.x), model (1.x), input, cache read, cache write, output, reasoning, cost.
    public static IEnumerable<TokenEntry> Parse(IEnumerable<string?[]> rows)
    {
        foreach (var row in rows)
        {
            if (row.Length < 11 || string.IsNullOrEmpty(row[0]) || !long.TryParse(row[2], out var millis)) continue;
            long Count(int i) => double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (long)Math.Clamp(value, 0, 1e15) : 0;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }
            decimal? cost = decimal.TryParse(row[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? amount : null;
            var model = row[3] is { Length: > 0 } current ? current : row[4] ?? "";
            yield return new TokenEntry("opencode:" + row[0], time, model, Count(5), Count(6), Count(7), 0, Count(8) + Count(9), cost);
        }
    }
}
