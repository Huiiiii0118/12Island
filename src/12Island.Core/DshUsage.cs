using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TwelveIsland.Core;

// What DeepSeek Harness keeps for its DeepSeek connection: the account grant from signing in, and the API key saved on
// its Models page. Either may be empty. Values are held in memory only.
public sealed record DshAccount(string Token, string ApiKey);

// DeepSeek Harness (the desktop app and the local web app started with `npx @deepseek-ai/dsh web`) keeps its data in DSH_HOME,
// otherwise %USERPROFILE%\.dsh. Its credentials are the YAML document .credentials.yaml:
//   version: 1
//   refs:    { DEEPSEEK_API_KEY: <key> }
//   records: { deepseek-account-platform/default: { kind: grant, payload: { version: 1, token, issuer } } }
public static class DshUsage
{
    public const string Platform = "https://platform.deepseek.com";
    public static string Credentials(string home) => Path.Combine(home, ".credentials.yaml");

    public static DshAccount? ReadAccount(string home)
    {
        var path = Credentials(home);
        if (!File.Exists(path)) return null;
        string text;
        using (var stream = SharedFile.Open(path))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
        var root = Yaml.Parse(text);
        if (Yaml.Find(root, "version") != "1") throw new InvalidDataException(Loc.T("DeepSeek Harness 凭据格式无法识别。", "The DeepSeek Harness credential format is not recognized."));
        // DeepSeek Harness discards a grant issued by another origin, so only one from the public platform is used.
        var grant = Yaml.Find(root, "records", "deepseek-account-platform/default", "payload", "token");
        if (Yaml.Find(root, "records", "deepseek-account-platform/default", "kind") != "grant" ||
            Yaml.Find(root, "records", "deepseek-account-platform/default", "payload", "issuer")?.TrimEnd('/') != Platform) grant = null;
        return new((grant ?? "").Trim(), (Yaml.Find(root, "refs", "DEEPSEEK_API_KEY") ?? "").Trim());
    }

    // GET /api/v0/users/get_user_summary with the grant in x-dsh-auth-token, as DeepSeek Harness reads it:
    // {code:0, data:{biz_code:0, biz_data:{normal_wallets:[{currency, balance}], bonus_wallets:[{currency, balance}],
    // total_costs:[{currency, amount}]}}}. Amounts are decimal strings with up to 16 places. The platform's web console
    // shows these as the topped-up balance, the granted balance and the account's total cost ("累计消费金额").
    // The two wallets together are the balance /user/balance reports; each is cut to whole cents first, so the two
    // sources show the same amounts.
    public static List<UsageMetric>? Summary(JsonElement root)
    {
        if (root.Get("code").Number() != 0 || root.Get("data").Get("biz_code").Number() != 0) return null;
        var data = root.Get("data").Get("biz_data");
        if (data.Get("normal_wallets").ValueKind != JsonValueKind.Array || data.Get("bonus_wallets").ValueKind != JsonValueKind.Array) return null;
        Dictionary<string, decimal> Sums(string name, string field)
        {
            var sums = new Dictionary<string, decimal>();
            foreach (var wallet in data.Get(name).Items())
            {
                var currency = wallet.Get("currency").Text();
                if (currency is not ("CNY" or "USD")) continue;
                if (!decimal.TryParse(wallet.Get(field).Text(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) throw new InvalidDataException();
                sums[currency] = sums.GetValueOrDefault(currency) + amount;
            }
            return sums;
        }
        var normal = Sums("normal_wallets", "balance"); var bonus = Sums("bonus_wallets", "balance"); var costs = Sums("total_costs", "amount");
        var metrics = new List<UsageMetric>();
        foreach (var currency in new[] { "CNY", "USD" }.Where(c => normal.ContainsKey(c) || bonus.ContainsKey(c)))
        {
            decimal paid = Cents(normal.GetValueOrDefault(currency)), granted = Cents(bonus.GetValueOrDefault(currency));
            metrics.Add(new(MetricKind.Balance, "账户余额") { Id = "total:" + currency, Amount = paid + granted, Currency = currency });
            metrics.Add(new(MetricKind.Balance, "赠送余额") { Id = "granted:" + currency, Amount = granted, Currency = currency });
            // The total cost comes last, so the first page shows the two balances.
            if (costs.TryGetValue(currency, out var cost)) metrics.Add(new(MetricKind.Spend, "累计消费") { Id = "cost:" + currency, Amount = Math.Max(0, Cents(cost)), Currency = currency });
        }
        return metrics.Count == 0 ? null : metrics;
    }
    private static decimal Cents(decimal value) => decimal.Truncate(value * 100) / 100;
}

// Reads the block mappings of a YAML document into nested dictionaries: plain, single-quoted and double-quoted scalars,
// comments and blank lines. This is the part of YAML that DeepSeek Harness writes in .credentials.yaml; sequences,
// flow collections and block scalars are not needed there and are skipped.
public static class Yaml
{
    public static Dictionary<string, object> Parse(string text)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var stack = new List<(int Indent, Dictionary<string, object> Map)> { (-1, root) };
        int skipBelow = int.MaxValue;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var content = line.TrimStart(' ');
            if (content.Length == 0 || content[0] == '#' || content.StartsWith("---", StringComparison.Ordinal) || content.StartsWith("%", StringComparison.Ordinal)) continue;
            int indent = line.Length - content.Length;
            // Lines inside a sequence or a block scalar are skipped up to the next line at its parent's indentation.
            if (indent > skipBelow) continue;
            skipBelow = int.MaxValue;
            while (stack.Count > 1 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (content[0] == '-' || ReadKey(content, out var key, out var rest) is false) { skipBelow = indent; continue; }
            var map = stack[^1].Map;
            if (rest.Length == 0 || rest[0] == '#')
            {
                var child = new Dictionary<string, object>(StringComparer.Ordinal);
                map[key] = child; stack.Add((indent, child));
            }
            else if (rest[0] is '|' or '>' or '[' or '{' or '&' or '*' or '!') skipBelow = indent;
            else map[key] = Scalar(rest);
        }
        return root;
    }

    public static string? Find(Dictionary<string, object> root, params string[] path)
    {
        object node = root;
        foreach (var segment in path)
            if (node is not Dictionary<string, object> map || !map.TryGetValue(segment, out node!)) return null;
        return node as string;
    }

    // "key: value", where the key may be quoted and contain "/" or ":" inside quotes.
    private static bool ReadKey(string content, out string key, out string rest)
    {
        key = rest = "";
        int end;
        if (content[0] is '"' or '\'')
        {
            end = ClosingQuote(content, 0);
            if (end < 0 || end + 1 >= content.Length || content[end + 1] != ':') return false;
            key = Scalar(content[..(end + 1)]);
            end++;
        }
        else
        {
            end = content.IndexOf(": ", StringComparison.Ordinal);
            if (end < 0 && content.EndsWith(':')) end = content.Length - 1;
            if (end <= 0) return false;
            key = content[..end].Trim();
        }
        rest = content[(end + 1)..].Trim();
        return true;
    }

    private static int ClosingQuote(string text, int start)
    {
        char quote = text[start];
        for (int i = start + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\') { i++; continue; }
            if (text[i] != quote) continue;
            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
            return i;
        }
        return -1;
    }

    private static string Scalar(string value)
    {
        if (value[0] == '\'')
        {
            int end = ClosingQuote(value, 0);
            return (end < 0 ? value[1..] : value[1..end]).Replace("''", "'");
        }
        if (value[0] == '"')
        {
            int end = ClosingQuote(value, 0);
            var inner = end < 0 ? value[1..] : value[1..end];
            var result = new StringBuilder();
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i + 1 >= inner.Length) { result.Append(inner[i]); continue; }
                char next = inner[++i];
                result.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => next });
            }
            return result.ToString();
        }
        // A plain scalar ends at a comment, which YAML starts with " #".
        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment < 0 ? value : value[..comment]).Trim();
    }
}
