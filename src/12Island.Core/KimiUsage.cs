using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TwelveIsland.Core;

// The Kimi Code sign-in: the OAuth access token, when it expires, and the account it names (the user_id claim of the token,
// used only to tell accounts apart through a one-way hash). The token is held in memory only and never shown.
public sealed record KimiCredential(string AccessToken, DateTimeOffset? ExpiresAt, string Account = "")
{
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
    public override string ToString() => nameof(KimiCredential);
}

// The environment variables Kimi Code reads for its service: KIMI_CODE_BASE_URL (the coding API), KIMI_CODE_OAUTH_HOST or
// KIMI_OAUTH_HOST (the sign-in host) and KIMI_CODE_REGION_MARKER ("off" ignores the region file). Null when unset.
public sealed record KimiEnvironment(string? BaseUrl = null, string? OAuthHost = null, string? RegionMarker = null);

// The service Kimi Code is signed in to: the coding API address, whether it is the international service (kimi.ai) rather
// than the one in mainland China (kimi.com), and the name of the sign-in file under credentials.
public sealed record KimiService(string BaseUrl, bool Global, string CredentialName);

// Kimi Code (the CLI, the desktop app and the VS Code extension) keeps its data in KIMI_CODE_HOME, otherwise
// %USERPROFILE%\.kimi-code. Signing in asks for kimi.com or kimi.ai, and the choice is kept in config.toml: the
// [providers."managed:kimi-code"] table holds base_url (the region's coding API), and its oauth table holds key (which
// sign-in file is used) and, for kimi.ai, oauth_host. The sign-in is credentials\<name>.json, where <name> is key without
// its "oauth/" prefix: kimi-code.json for kimi.com, kimi-code-env-<hash>.json for kimi.ai. It holds { access_token,
// refresh_token, expires_at (Unix seconds), scope, token_type, expires_in }. Only access_token and expires_at are read;
// the token is never refreshed and no Kimi Code file is written.
public static class KimiUsage
{
    public const string DefaultBaseUrl = "https://api.kimi.com/coding/v1";
    public const string GlobalBaseUrl = "https://api.kimi.ai/coding/v1";
    public const string DefaultOAuthHost = "https://auth.kimi.com", GlobalOAuthHost = "https://auth.kimi.ai";
    private const string DefaultCredential = "kimi-code";
    // The provider Kimi Code signs in with.
    private const string ManagedProvider = "managed:kimi-code";
    public static string Credentials(string home, string name = DefaultCredential) => Path.Combine(home, "credentials", name + ".json");
    public static string Config(string home) => Path.Combine(home, "config.toml");

    // The region and the sign-in file, resolved as Kimi Code resolves them:
    //   - the coding API is KIMI_CODE_BASE_URL, otherwise base_url in config.toml, otherwise the region's default;
    //   - the region is the one of that address when it is a Kimi service (api.kimi.ai or api.kimi.com); for another
    //     address, the sign-in host (KIMI_CODE_OAUTH_HOST, otherwise oauth_host in config.toml), then the kimi.com sign-in
    //     key, then the region file Kimi Code may keep in its folder ("mainland-cn" or "global"), otherwise kimi.com;
    //   - the sign-in file follows the oauth key in config.toml; with KIMI_CODE_BASE_URL or KIMI_CODE_OAUTH_HOST set, or
    //     without a key, it is the name Kimi Code derives from the sign-in host and the configured coding API.
    public static KimiService Service(string home, KimiEnvironment environment)
    {
        var config = ReadConfig(home);
        string? envBase = Usable(environment.BaseUrl), envHost = Blank(environment.OAuthHost);
        var configBase = Usable(config.BaseUrl);
        bool? hostRegion = RegionOfHost(envHost) ?? RegionOfHost(config.OAuthHost);
        bool signalGlobal = hostRegion ?? (config.OAuthKey is "oauth/kimi-code" or "kimi-code" ? false
            : !string.Equals(environment.RegionMarker?.Trim(), "off", StringComparison.OrdinalIgnoreCase) && RegionMarker(home) is { } marker ? marker : false);
        var baseUrl = envBase ?? configBase ?? (signalGlobal ? GlobalBaseUrl : DefaultBaseUrl);
        bool global = RegionOfAddress(baseUrl) ?? signalGlobal;
        string? name;
        if (Blank(environment.BaseUrl) is not null || envHost is not null) name = CredentialKey(envHost, envBase ?? configBase ?? DefaultBaseUrl);
        else if (config.OAuthKey is { } key) name = CredentialName(key);
        else name = CredentialKey(config.OAuthHost, configBase ?? DefaultBaseUrl);
        return new(baseUrl, global, name ?? "");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // True for a kimi.ai host, false for a kimi.com one, null for any other or none.
    private static bool? RegionOfHost(string? address)
    {
        if (address is null || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host == "kimi.ai" || host.EndsWith(".kimi.ai", StringComparison.Ordinal) ? true
            : host == "kimi.com" || host.EndsWith(".kimi.com", StringComparison.Ordinal) ? false : null;
    }
    private static bool? RegionOfAddress(string address) => RegionOfHost(address);

    // The region file in Kimi Code's folder: true for "global", false for "mainland-cn", null when absent or unreadable.
    private static bool? RegionMarker(string home)
    {
        try
        {
            var path = Path.Combine(home, "region");
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim() switch { "global" => true, "mainland-cn" => false, _ => null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // The sign-in file name for an oauth key: "oauth/kimi-code" is kimi-code, "oauth/<name>" is <name>, a bare name is
    // itself. Null for anything that is not a plain file name.
    public static string? CredentialName(string key)
    {
        var name = key.Trim();
        if (name is "kimi-code" or "oauth/kimi-code") return DefaultCredential;
        if (name.StartsWith("oauth/", StringComparison.Ordinal)) name = name[6..];
        return name.Length is > 0 and <= 120 && !name.StartsWith('.') && name.IndexOfAny(['/', '\\', ':']) < 0 &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : null;
    }

    // The sign-in file name Kimi Code derives from a sign-in host and a coding API: kimi-code for kimi.com's own pair,
    // otherwise "kimi-code-env-" and the first 16 hex digits of the SHA-256 of {"oauthHost":…,"baseUrl":…}.
    public static string CredentialKey(string? oauthHost, string baseUrl)
    {
        var host = (oauthHost ?? DefaultOAuthHost).Trim().TrimEnd('/');
        var address = baseUrl.Trim().TrimEnd('/');
        if (host == DefaultOAuthHost && address == DefaultBaseUrl) return DefaultCredential;
        var json = JsonSerializer.Serialize(new { oauthHost = host, baseUrl = address }, KeyJson);
        return "kimi-code-env-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }
    private static readonly JsonSerializerOptions KeyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static KimiCredential? ReadCredential(string home, string name = DefaultCredential)
    {
        if (name.Length == 0) return null;
        var path = Credentials(home, name);
        if (!File.Exists(path)) return null;
        using var doc = SharedFile.Parse(path);
        var token = doc.RootElement.Get("access_token").Text().Trim();
        return token.Length == 0 ? null : new(token, doc.RootElement.Get("expires_at").Date(), Account(token));
    }

    // The account a token names: the user_id (else sub) claim of its payload, read without checking the signature, since it
    // only decides whose kept quota may be shown. "" when the token is not a JWT.
    public static string Account(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length is 0 or > 8192) return "";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Get("user_id").Text() is { Length: > 0 } user ? user : doc.RootElement.Get("sub").Text();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }

    // The coding API alone (see Service). A value that is not an HTTPS address (HTTP only for this PC) is passed over, so
    // the token is never sent in plain text.
    public static string BaseUrl(string home, string? environment) => Service(home, new(environment)).BaseUrl;

    private sealed record ConfigValues(string? BaseUrl, string? OAuthKey, string? OAuthHost);
    private static ConfigValues ReadConfig(string home)
    {
        try
        {
            var path = Config(home);
            if (!File.Exists(path)) return new(null, null, null);
            string text;
            using (var stream = SharedFile.Open(path))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            return new(ConfiguredBaseUrl(text), ConfiguredValue(text, "oauth", "key"), ConfiguredValue(text, "oauth", "oauth_host") ?? ConfiguredValue(text, "oauth", "oauthHost"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, null, null); }
    }

    private static string? Usable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { ProviderCatalog.ValidateUrl(value.Trim()); }
        catch (InvalidDataException) { return null; }
        return value.Trim().TrimEnd('/');
    }

    // Reads base_url from the [providers."managed:kimi-code"] table, or another key from one of its subtables. Only what
    // this needs of TOML is read: table headers with bare or quoted keys, comments, and a basic or literal string value.
    // Other lines are skipped.
    public static string? ConfiguredBaseUrl(string toml) => ConfiguredValue(toml, null, "base_url");

    public static string? ConfiguredValue(string toml, string? table, string name)
    {
        bool inside = false;
        foreach (var raw in toml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                inside = !line.StartsWith("[[", StringComparison.Ordinal) && TableKeys(line) is { } keys &&
                    keys.Count == (table is null ? 2 : 3) && keys[0] == "providers" && keys[1] == ManagedProvider && (table is null || keys[2] == table);
                continue;
            }
            if (!inside) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim().Trim('"', '\'');
            if (key != name) continue;
            return StringValue(line[(equals + 1)..].Trim());
        }
        return null;
    }

    // "[providers."managed:kimi-code"]" → ["providers", "managed:kimi-code"]; null when the header cannot be read.
    private static List<string>? TableKeys(string line)
    {
        var keys = new List<string>();
        var current = new StringBuilder();
        bool quoted = false; char quote = '\0'; bool closed = false;
        for (int i = 1; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == quote) quoted = false;
                else if (c == '\\' && quote == '"' && i + 1 < line.Length) current.Append(line[++i]);
                else current.Append(c);
            }
            else if (c is '"' or '\'') { quoted = true; quote = c; }
            else if (c == '.') { keys.Add(current.ToString().Trim()); current.Clear(); }
            else if (c == ']') { closed = true; break; }
            else current.Append(c);
        }
        if (!closed || quoted) return null;
        keys.Add(current.ToString().Trim());
        return keys;
    }

    private static string? StringValue(string value)
    {
        if (value.Length < 2 || value[0] is not ('"' or '\'')) return null;
        char quote = value[0];
        int end = value.IndexOf(quote, 1);
        return end < 0 ? null : value[1..end];
    }

    // GET {base}/usages. The current plans report each window's used share in usages: limit_5h, limit_7d (older plans
    // only) and limit_month_total, each { used_ratio (0–1), reset_time }. limit_month_code is the coding share of the
    // monthly allowance and is not shown on its own. Without usages, the earlier shape gives absolute counts: usage (the
    // weekly allowance) and limits[] { window: { duration, timeUnit }, detail: { limit, used, remaining, resetTime } },
    // where the numbers arrive as strings. An optional boosterWallet holds the extra-usage balance.
    // Returns no metrics when the account has no Kimi Code plan.
    public static List<UsageMetric> Usages(JsonElement root, string currency = "CNY")
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var metrics = new List<UsageMetric>();
        var usages = root.Get("usages");
        if (usages.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, label, minutes) in new[] { ("limit_5h", "5 小时额度", 300), ("limit_7d", "每周额度", 10080), ("limit_month_total", "每月额度", 43200) })
            {
                var window = usages.Get(key);
                if (window.Get("used_ratio").Number() is not { } ratio) continue;
                metrics.Add(Percent(label, minutes, ratio * 100, window.Get("reset_time").Date()));
            }
        }
        else
        {
            foreach (var limit in root.Get("limits").Items())
            {
                var minutes = Minutes(limit.Get("window"));
                if (Share(limit.Get("detail")) is not { } used || minutes is not > 0) continue;
                var label = minutes switch { 300 => "5 小时额度", 10080 => "每周额度", 43200 or 44640 => "每月额度",
                    _ => $"{minutes} 分钟额度" };
                if (metrics.Any(m => m.Label == label)) continue;
                metrics.Add(Percent(label, minutes.Value, used, ResetTime(limit.Get("detail"))));
            }
            if (Share(root.Get("usage")) is { } weekly && metrics.All(m => m.Label != "每周额度"))
                metrics.Add(Percent("每周额度", 10080, weekly, ResetTime(root.Get("usage"))));
        }
        metrics = [.. metrics.OrderBy(m => m.Window)];
        // Extra usage is shown only alongside a plan's windows.
        if (metrics.Count > 0) metrics.AddRange(Booster(root.Get("boosterWallet"), currency));
        return metrics;
    }

    private static UsageMetric Percent(string label, int minutes, double used, DateTimeOffset? reset) =>
        new(MetricKind.Percent, label) { Id = label, Percent = Math.Clamp(used, 0, 100), ResetAt = reset, Window = minutes };

    private static int? Minutes(JsonElement window)
    {
        if (window.Get("duration").Number() is not { } duration || duration <= 0) return null;
        double minutes = window.Get("timeUnit").Text() switch
        {
            "TIME_UNIT_SECOND" => duration / 60, "TIME_UNIT_MINUTE" => duration, "TIME_UNIT_HOUR" => duration * 60, "TIME_UNIT_DAY" => duration * 1440,
            _ => double.NaN
        };
        return double.IsFinite(minutes) && minutes is >= 1 and < 1_000_000 ? (int)Math.Round(minutes) : null;
    }

    // The used share of an absolute allowance: used / limit, or (limit − remaining) / limit.
    private static double? Share(JsonElement detail)
    {
        if (detail.Get("limit").Number() is not { } limit || limit <= 0) return null;
        var used = detail.Get("used").Number() ?? (detail.Get("remaining").Number() is { } remaining ? limit - remaining : null);
        return used is { } value ? value / limit * 100 : null;
    }

    private static DateTimeOffset? ResetTime(JsonElement detail) =>
        detail.Get("resetTime").Date() ?? detail.Get("reset_time").Date() ?? detail.Get("resetAt").Date() ?? detail.Get("reset_at").Date();

    // boosterWallet: { balance: { type: "BOOSTER", amount, amountLeft }, monthlyChargeLimitEnabled, monthlyChargeLimit:
    // { currency, priceInCents }, monthlyUsed: { currency, priceInCents } }. amount and amountLeft are cents in fixed point
    // (one cent is 1,000,000). A wallet that was never topped up is not shown. Without a currency in the reply, the
    // region's is used: CNY for kimi.com, USD for kimi.ai.
    public static IEnumerable<UsageMetric> Booster(JsonElement wallet, string fallbackCurrency = "CNY")
    {
        var balance = wallet.Get("balance");
        if (balance.Get("type").Text() != "BOOSTER" || Fixed(balance.Get("amount")) is not { } total || total <= 0) yield break;
        var limit = wallet.Get("monthlyChargeLimit"); var used = wallet.Get("monthlyUsed");
        var currency = new[] { limit.Get("currency").Text(), used.Get("currency").Text() }.FirstOrDefault(c => c.Length > 0) ?? fallbackCurrency;
        yield return new(MetricKind.Balance, "加油包") { Id = "booster", Amount = Math.Max(0, Fixed(balance.Get("amountLeft")) ?? 0), Total = total, Currency = currency };
        if (wallet.Get("monthlyChargeLimitEnabled").ValueKind == JsonValueKind.True && Cents(limit) is { } cap && cap > 0)
            yield return new(MetricKind.Spend, "本月加油包") { Id = "booster-month", Amount = Math.Max(0, Cents(used) ?? 0), Total = cap, Currency = currency };
    }

    private static decimal? Fixed(JsonElement value) => Integer(value) is { } raw ? decimal.Truncate(raw / 1_000_000m) / 100m : null;
    private static decimal? Cents(JsonElement money) => Integer(money.Get("priceInCents")) is { } cents ? cents / 100m : null;
    private static decimal? Integer(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.Text();
        return decimal.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    // GET {base}/me names the membership level (user_level_name, such as "Plus"). Only that name is read; the other fields
    // describe the person and are not kept.
    // GET {base}/me names the account's region: REGION_CN for kimi.com; another region is taken as kimi.ai. Null when absent.
    public static bool? GlobalRegion(JsonElement me) => me.Get("region").Text().Trim() switch
    { "" => null, "REGION_CN" => false, var region => region.StartsWith("REGION_", StringComparison.Ordinal) ? true : null };
    // The source text naming the service, as ZCode names Z.ai and Zhipu accounts.
    public static string Source(string product, bool global) =>
        product + (global ? Loc.T(" · kimi.ai 账户", " · kimi.ai account") : Loc.T(" · kimi.com 账户", " · kimi.com account"));

    public static string Plan(JsonElement root) => root.Get("user_level_name").Text().Trim() is { Length: > 0 and <= 40 } name ? name : "";

    // Kimi Code names a model "<provider>/<model>" ("kimi-code/k3.1-pro"); its own models are listed under [models] in
    // config.toml and by {base}/models. The name shown, which is also the name prices are looked up by, is the model's
    // Moonshot API ID when Moonshot publishes that model under it; otherwise Kimi Code's own ID is kept, so it is
    // never priced as another model:
    //   - k3, k3.1-pro and k3.1-pro-highspeed are Kimi K3, K3.1 Pro and K3.1 Pro Highspeed, which the Moonshot API
    //     prices as kimi-k3, kimi-k3.1-pro and kimi-k3.1-pro-highspeed. Highspeed is priced on its own.
    //   - kimi-for-coding and kimi-for-coding-highspeed are aliases that Kimi Code points at different models over
    //     time (the display name says which one now), and k3-256k has no API price of its own; these stay as they are,
    //     and k3-256k is priced as K3 (see PriceModel).
    // Any other model keeps its ID without the provider, in lower case.
    public static string ModelName(string model)
    {
        var id = model.Trim();
        id = id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return id switch
        {
            "k3" or "kimi-k3" => "kimi-k3",
            "k3.1-pro" or "kimi-k3.1-pro" => "kimi-k3.1-pro",
            "k3.1-pro-highspeed" or "kimi-k3.1-pro-highspeed" => "kimi-k3.1-pro-highspeed",
            _ => id
        };
    }

    // The model whose price a Kimi Code model takes when its own name has none (no catalog or manual price). K3-256k has
    // no published price of its own and is costed at K3's, including K3's price when it is edited by hand.
    public static string? PriceModel(string name) => name == "k3-256k" ? "kimi-k3" : null;
}
