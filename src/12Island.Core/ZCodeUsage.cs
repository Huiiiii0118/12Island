using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwelveIsland.Core;

// The signed-in ZCode account: the region ("bigmodel" or "zai"), its Coding Plan API key, the ZCode token used for
// the MCP and Start Plan allowances, the region's access token, and ZCode's device id. Values are held in memory only.
public sealed record ZCodeAccount(string Family, string ApiKey, string Jwt, string AccessToken, string DeviceMid = "");

// ZCode records one row per model request in cli\db\db.sqlite (table model_usage).
// input_tokens includes the cached part: input_tokens + output_tokens equals computed_total_tokens.
public static class ZCodeUsage
{
    private const string Encrypted = "enc:v1:";
    public static string Database(string home) => Path.Combine(home, "cli", "db", "db.sqlite");
    public static string Credentials(string home) => Path.Combine(home, "v2", "credentials.json");

    // ZCode stores each credential as enc:v1:<iv>.<tag>.<ciphertext> (base64url, AES-256-GCM) with the key
    // SHA-256("zcode-credential-fallback:<platform>:<home directory>:<user name>"), unless ZCODE_CREDENTIAL_SECRET is set.
    public static ZCodeAccount? ReadAccount(string home, string? secret = null)
    {
        var path = Credentials(home);
        if (!File.Exists(path)) return null;
        using var doc = SharedFile.Parse(path);
        var profile = Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } userProfile ? userProfile : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        secret ??= Environment.GetEnvironmentVariable("ZCODE_CREDENTIAL_SECRET") is { Length: > 0 } custom ? custom : $"zcode-credential-fallback:win32:{profile}:{Environment.UserName}";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        string Value(string name) => doc.RootElement.Get(name).Text() is { Length: > 0 } text ? Decrypt(text, key) : "";
        // ZCode signs in to one region at a time: signing in to bigmodel clears the zai session and vice versa.
        // Plan keys are stored separately and a key from the previous region can remain, so only the active region's key is used.
        var family = Value("oauth:active_provider").Trim();
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name)
            .Where(n => n.StartsWith("account-provider:coding-plan:account:", StringComparison.Ordinal) && n.EndsWith(":api-key", StringComparison.Ordinal)).ToList();
        string? KeyFor(string region) => names.FirstOrDefault(n => n.Contains($":{region}-individual-coding-plan:", StringComparison.Ordinal));
        if (family is not ("bigmodel" or "zai")) family = KeyFor("bigmodel") is null && KeyFor("zai") is not null ? "zai" : "bigmodel";
        var apiKeyName = KeyFor(family);
        return new(family, apiKeyName is null ? "" : Value(apiKeyName).Trim(), Value("zcodejwttoken").Trim(), Value($"oauth:{family}:access_token").Trim(), DeviceMid(home));
    }

    // The Start Plan balance requires ZCode's device id, which ZCode keeps in v2	elemetry-state.json.
    private static string DeviceMid(string home)
    {
        try
        {
            var path = Path.Combine(home, "v2", "telemetry-state.json");
            if (!File.Exists(path)) return "";
            using var doc = SharedFile.Parse(path);
            return Guid.TryParse(doc.RootElement.Get("deviceMid").Text(), out var id) ? id.ToString() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    public static string Decrypt(string value, byte[] key)
    {
        if (!value.StartsWith(Encrypted, StringComparison.Ordinal)) return value;
        var parts = value[Encrypted.Length..].Split('.');
        if (parts.Length != 3) throw new InvalidDataException(Loc.T("ZCode 凭据格式无法识别。", "The ZCode credential format is not recognized."));
        byte[] iv = Base64Url(parts[0]), tag = Base64Url(parts[1]), data = Base64Url(parts[2]), plain = new byte[data.Length];
        using var aes = new AesGcm(key, tag.Length);
        try { aes.Decrypt(iv, data, tag, plain); }
        catch (CryptographicException) { throw new InvalidDataException(Loc.T("ZCode 凭据无法解密。", "The ZCode credentials cannot be decrypted.")); }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Base64Url(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4)); }
        catch (FormatException) { throw new InvalidDataException(Loc.T("ZCode 凭据格式无法识别。", "The ZCode credential format is not recognized.")); }
    }

    // ZCode Start Plan (event allowances): data.plans[] name the plans; data.balances[] hold one allowance per entitlement,
    // usually a one-time token grant for a model that ends with the plan. Only active, unexpired plans are shown.
    public static (List<UsageMetric> Metrics, string Plan) StartPlan(JsonElement root, DateTimeOffset now)
    {
        var metrics = new List<UsageMetric>();
        if (root.Get("code").Number() != 0) return (metrics, "");
        var data = root.Get("data");
        var plans = data.Get("plans").Items()
            .Where(p => p.Get("status").Text().Equals("active", StringComparison.OrdinalIgnoreCase) && (p.Get("ends_at").Date() is not { } end || end > now))
            .ToList();
        var periods = plans.SelectMany(p => p.Get("entitlements").Items())
            .GroupBy(e => e.Get("entitlement_id").Text()).ToDictionary(g => g.Key, g => g.First().Get("period").Text());
        var active = plans.Select(p => p.Get("user_plan_id").Text()).ToHashSet();
        foreach (var balance in data.Get("balances").Items())
        {
            if (!active.Contains(balance.Get("user_plan_id").Text()) || balance.Get("total_units").Number() is not { } total || total <= 0) continue;
            var expires = balance.Get("expires_at").Date() ?? balance.Get("period_end").Date();
            if (expires <= now) continue;
            var used = balance.Get("used_units").Number() ?? total - (balance.Get("remaining_units").Number() ?? total);
            var entitlement = balance.Get("entitlement_id").Text();
            var periodEnd = balance.Get("period_end").Date();
            var type = balance.Get("unit_type").Text();
            metrics.Add(new(MetricKind.Count, balance.Get("show_name").Text() is { Length: > 0 } name ? name : entitlement)
            {
                Id = "start-plan:" + entitlement, Used = (decimal)Math.Clamp(used, 0, total), Total = (decimal)total,
                Unit = type == "token" ? "令牌" : type, ExpiresAt = expires,
                // A recurring allowance resets at the end of its period; a one-time grant only expires.
                ResetAt = periods.GetValueOrDefault(entitlement, "") is { Length: > 0 } period && period != "one_time" && periodEnd < expires ? periodEnd : null
            });
        }
        return (metrics, plans.Select(p => p.Get("name").Text()).FirstOrDefault(n => n.Length > 0) ?? "");
    }

    // ZCode's own MCP allowance: {code:0, data:{next_refresh_at (seconds), total_usage:{used, limit}}}. It resets daily.
    public static UsageMetric? McpMetric(JsonElement root)
    {
        if (root.Get("code").Number() != 0) return null;
        var data = root.Get("data"); var usage = data.Get("total_usage");
        if (usage.Get("limit").Number() is not > 0 || usage.Get("used").Number() is not { } used) return null;
        return new(MetricKind.Count, "ZCode MCP")
        { Id = "zcode-mcp", Used = (decimal)Math.Max(0, used), Total = (decimal)usage.Get("limit").Number()!.Value, Unit = "次", ResetAt = data.Get("next_refresh_at").Date() };
    }

    public static void Read(ProviderSnapshot snapshot, IDesktopSources desktop, string home, DateTimeOffset start, DateTimeOffset now)
    {
        var database = Database(home);
        snapshot.UsageAvailable = File.Exists(database);
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T("未找到本机 ZCode 记录。", "No local ZCode records were found."); return; }
        var rows = desktop.QueryDatabase(database,
            "SELECT id, model_id, COALESCE(completed_at, started_at), input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens " +
            "FROM model_usage WHERE status <> 'running' AND started_at >= " + start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (rows is null) { snapshot.UsageComplete = false; snapshot.UsageNote = Loc.T("ZCode 记录暂时无法读取。", "The ZCode records cannot be read right now."); return; }
        snapshot.Entries = Parse(rows).Where(e => e.Time >= start && e.Time <= now && e.Total > 0).OrderBy(e => e.Time).ToList();
        snapshot.UsageNote = Loc.T($"本机 ZCode {start.LocalDateTime:yyyy-MM-dd} 起 · {snapshot.Entries.Count} 次模型请求", $"Local ZCode since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
    }

    public static IEnumerable<TokenEntry> Parse(IEnumerable<string?[]> rows)
    {
        foreach (var row in rows)
        {
            if (row.Length < 7 || string.IsNullOrEmpty(row[0]) || !long.TryParse(row[2], out var millis)) continue;
            long Count(int i) => long.TryParse(row[i], out var value) ? Math.Max(0, value) : 0;
            long input = Count(3), read = Math.Min(input, Count(5)), write = Math.Min(input - read, Count(6));
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }
            yield return new TokenEntry("zcode:" + row[0], time, row[1] ?? "", input - read - write, read, write, 0, Count(4));
        }
    }
}

// ZCode's own service can take tens of seconds to answer. A refresh waits only while nothing has been read yet;
// otherwise it uses the last good response at once and the new one loads in the background. A failed, slow or
// unrecognised response never replaces the last good one, so an allowance does not disappear between refreshes.
// Only a different account discards it.
public sealed class LatestResponse
{
    private readonly object _sync = new();
    private Task? _pending;
    private string _account = "";
    private JsonElement? _last;

    public async Task<JsonElement?> GetAsync(string account, Func<CancellationToken, Task<JsonElement>> load, Func<JsonElement, bool> accept, CancellationToken wait)
    {
        Task pending;
        lock (_sync)
        {
            if (_account != account) { _account = account; _last = null; _pending = null; }
            if (_pending is null || _pending.IsCompleted) _pending = Load(account, load, accept);
            pending = _pending;
            if (_last is { } kept && !pending.IsCompleted) return kept;
        }
        try { await pending.WaitAsync(wait); }
        catch (OperationCanceledException) { }
        lock (_sync) return _last;
    }

    private async Task Load(string account, Func<CancellationToken, Task<JsonElement>> load, Func<JsonElement, bool> accept)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var root = await load(timeout.Token);
            if (accept(root)) lock (_sync) if (account == _account) _last = root;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException) { }
    }
}
