using System.Text.Json;

namespace TwelveIsland.Core;

// Which local sign-in a Claude token comes from, and where a Claude plan was read. A reading kept on disk stores these rather
// than their texts, so after a change of language it still names its sources in the language shown now (ClaudeSources).
public enum ClaudeSignIn { Code, Desktop }
public enum ClaudePlanOrigin { None, Account, SignIn, LocalRecord }

public static class ClaudeSources
{
    public static string Name(ClaudeSignIn signIn) => signIn == ClaudeSignIn.Code ? "Claude Code" : Loc.T("Claude 桌面版", "Claude desktop app");
    public static string Quota(ClaudeSignIn signIn) => Name(signIn) + Loc.T(" · 账户配额", " · account quota");
    public static string Plan(ClaudePlanOrigin origin, ClaudeSignIn signIn) => origin switch
    {
        ClaudePlanOrigin.Account => Loc.T("Claude 账户", "Claude account"),
        ClaudePlanOrigin.SignIn => Name(signIn) + Loc.T(" 登录记录", " sign-in record"),
        ClaudePlanOrigin.LocalRecord => Loc.T("Claude 本地账户记录", "Claude local account record"),
        _ => ""
    };
}

// Deliberately a class: generated record formatting must never include an access token.
// Account is the id of the account the token belongs to, read from the same local records without a request; "" when they
// do not name it. Organization is the id of the organization the token acts for: one account can belong to several, such as
// a personal Pro or Max subscription and a Team, and each has its own quotas. "" when the records do not name it.
public sealed class ClaudeCredential(string token, string plan, ClaudeSignIn signIn, string account = "", string organization = "")
{
    public string Token { get; } = token;
    public string Plan { get; } = plan;
    public ClaudeSignIn SignIn { get; } = signIn;
    public string Source => ClaudeSources.Name(SignIn);
    public string Account { get; } = account;
    public string Organization { get; } = organization;
}

public sealed record ClaudeDesktopState(bool Detected, IReadOnlyList<ClaudeCredential> Credentials, bool ReadFailed = false);

public static class ClaudeDesktopCache
{
    public static IReadOnlyList<ClaudeCredential> Parse(JsonElement cache, string account, DateTimeOffset now)
    {
        if (cache.ValueKind != JsonValueKind.Object) return [];
        var credentials = new List<(ClaudeCredential Credential, int ScopeCount)>();
        foreach (var item in cache.EnumerateObject())
        {
            if (item.Name.StartsWith("acct:", StringComparison.Ordinal) &&
                (account.Length == 0 || !item.Name.StartsWith("acct:" + account + "|", StringComparison.Ordinal))) continue;
            const string host = ":https://api.anthropic.com:";
            var index = item.Name.IndexOf(host, StringComparison.Ordinal);
            if (index < 0) continue;
            var scopes = item.Name[(index + host.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "acct:<account>|<client>:<organization>": the desktop app keeps a token for each organization it signed in to.
            var owner = item.Name[..index];
            var parts = owner[(owner.IndexOf('|') + 1)..].Split(':');
            var organization = parts.Length >= 2 ? parts[^1] : "";
            if (!scopes.Contains("user:profile", StringComparer.Ordinal)) continue;
            var token = item.Value.Get("token").Text();
            var expires = item.Value.Get("expiresAt").Number();
            if (token.Length == 0 || expires is null || expires <= now.ToUnixTimeMilliseconds()) continue;
            credentials.Add((new(token, PlanParser.ClaudeTier(item.Value.Get("rateLimitTier").Text(),
                item.Value.Get("subscriptionType").Text()), ClaudeSignIn.Desktop, account, organization), scopes.Length));
        }
        // Prefer the existing profile-only credential when the desktop has several scopes cached.
        return credentials.OrderBy(item => item.ScopeCount).Select(item => item.Credential)
            .DistinctBy(item => item.Token).ToList();
    }
}
