using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Jint;

namespace TwelveIsland.Core;

public interface IProviderSecrets
{
    string Read(Guid instanceId);
    void Write(Guid instanceId, string secret);
    void Delete(Guid instanceId);
}

public sealed record ProviderTestResult(ProviderSnapshot Snapshot, double Seconds, IReadOnlyList<string> Logs);

internal sealed class ScriptSession(HttpClient http, AppEntry entry, string secret, DateTimeOffset now, CancellationToken cancellation, string? memory = null)
{
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ConcurrentQueue<int> _statuses = new();
    private readonly SemaphoreSlim _requests = new(4);
    private int _requestCount;
    public DateTimeOffset? RetryAt { get; private set; }
    public IReadOnlyList<string> Logs => _logs.ToArray();
    public int? FailureStatus => _statuses.FirstOrDefault(code => code >= 400) is > 0 and var value ? value : null;
    public string Redact(string text)
    {
        var hidden = Loc.T("[密钥已隐藏]", "[key hidden]");
        if (secret.Length > 0) text = text.Replace(secret, hidden, StringComparison.Ordinal).Replace(Uri.EscapeDataString(secret), hidden, StringComparison.Ordinal);
        return text.Length > 500 ? text[..500] : text;
    }
    public async Task<ProviderResult> RunAsync()
    {
        if (entry.QuotaSource == ProviderId.KimiApi) return await RunKimiAsync();
        if (entry.QuotaSource == ProviderId.OpenCodeGo) return await RunOpenCodeGoAsync();
        using var engine = new Engine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.LimitMemory(32 * 1024 * 1024).MaxStatements(2_000_000).LimitRecursion(200)
                .TimeoutInterval(TimeSpan.FromSeconds(10)).CancellationToken(cancellation);
            options.Constraints.PromiseTimeout = TimeSpan.FromSeconds(10);
            options.Interop.AllowGetType = false; options.Interop.AllowSystemReflection = false; options.Interop.AllowWrite = false;
            options.Host.StringCompilationAllowed = false;
        });
        engine.SetValue("__request", new Func<string, string, Task<string>>(SendAsync));
        engine.SetValue("__log", new Action<string>(value => { if (_logs.Count < 30) _logs.Enqueue(Redact(value)); }));
        // Only primitives and native JS objects cross the boundary, never HTTP responses or other CLR objects.
        using var remembered = memory is null ? null : JsonDocument.Parse(memory);
        var context = JsonSerializer.Serialize(new { site = entry.Site, secrets = new { key = secret }, now = now.ToString("O"), language = Loc.Language, memory = remembered?.RootElement });
        var script = entry.QuotaSource == ProviderId.Custom ? entry.Script : ProviderScripts.For(entry.QuotaSource);
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidDataException(Loc.T("请填写 fetchUsage(ctx) 函数。", "Write a fetchUsage(ctx) function."));
        if (script.Length > 128_000) throw new InvalidDataException(Loc.T("脚本最多为 128,000 个字符。", "A script can have at most 128,000 characters."));
        var missingFunction = Loc.T("脚本必须定义 fetchUsage(ctx)。", "The script must define fetchUsage(ctx).");
        var source = $$$"""
            (async function(send, logger) {
              delete globalThis.__request; delete globalThis.__log;
              const ctx = {{{context}}};
              async function request(method, url, options) {
                const response = JSON.parse(await send(method, JSON.stringify({url, options: options || {}})));
                if (response.error) throw new Error(response.error);
                return {status: response.status, headers: response.headers,
                  text: () => response.body, json: () => JSON.parse(response.body)};
              }
              ctx.http = Object.freeze({get: (url, options) => request("GET", url, options), post: (url, options) => request("POST", url, options)});
              ctx.log = text => logger(String(text));
              Object.freeze(ctx.secrets); Object.freeze(ctx);
              const fetcher = (function() {
                {{{script}}}
                if (typeof fetchUsage !== "function") throw new Error("{{{missingFunction}}}");
                return fetchUsage;
              })();
              return JSON.stringify(await fetcher(ctx));
            })(__request, __log)
            """;
        var result = await engine.EvaluateAsync(source, cancellationToken: cancellation);
        if (!result.IsString()) throw new InvalidDataException(Loc.T("fetchUsage 必须返回数组，每一项是一条配额。", "fetchUsage must return an array in which each item is one quota."));
        var json = result.AsString();
        if (json.Length > 1_000_000) throw new InvalidDataException(Loc.T("返回内容超过 1 MB。", "The returned content exceeds 1 MB."));
        using var doc = JsonDocument.Parse(json);
        var parsed = MetricParser.Parse(doc.RootElement);
        var next = doc.RootElement.Get("memory") is { ValueKind: JsonValueKind.Object } kept ? kept.GetRawText() : null;
        if (next?.Length > 16_000) { parsed.Warnings.Add(Loc.T("memory 超过 16 KB，未保留。", "memory exceeds 16 KB and was not kept.")); next = null; }
        return parsed with { Plan = Redact(parsed.Plan), Details = parsed.Details.ToDictionary(p => Redact(p.Key), p => Redact(p.Value)),
            Metrics = parsed.Metrics.Select(m => m with { Label = Redact(m.Label), Currency = Redact(m.Currency), Unit = Redact(m.Unit), Id = Redact(m.Id),
                Value = Redact(m.Value), Suffix = Redact(m.Suffix), Note = Redact(m.Note), ResetText = Redact(m.ResetText) }).ToList(),
            Warnings = parsed.Warnings.Select(Redact).ToList(), Memory = next };
    }

    private async Task<string> SendAsync(string method, string argument)
    {
        if (Interlocked.Increment(ref _requestCount) > 16) return Error(Loc.T("一次刷新最多请求 16 次。", "A refresh can make at most 16 requests."));
        try
        {
            await _requests.WaitAsync(cancellation);
            try
            {
                using var input = JsonDocument.Parse(argument);
                var root = input.RootElement; var options = root.Get("options");
                var uri = ProviderCatalog.ValidateUrl(root.Get("url").Text(), entry.AllowHttp);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.Get("timeout").Number() ?? 8000, 100, 10000)));
                using var request = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, uri);
                if (method == "POST" && options.Get("body").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    var body = options.Get("body");
                    var text = body.ValueKind == JsonValueKind.String ? body.GetString()! : body.GetRawText();
                    if (Encoding.UTF8.GetByteCount(text) > 1_000_000) return Error(Loc.T("请求正文超过 1 MB。", "The request body exceeds 1 MB."));
                    request.Content = new StringContent(text, Encoding.UTF8, body.ValueKind == JsonValueKind.String ? "text/plain" : "application/json");
                }
                if (options.Get("headers").ValueKind == JsonValueKind.Object)
                    foreach (var header in options.Get("headers").EnumerateObject())
                    {
                        if (header.Value.ValueKind != JsonValueKind.String || header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                            header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) return Error(Loc.T("请求头无效。", "A request header is not valid."));
                        var value = header.Value.GetString()!;
                        if (value.Contains('\r') || value.Contains('\n')) return Error(Loc.T("请求头不能包含换行。", "Request headers cannot contain line breaks."));
                        if (!request.Headers.TryAddWithoutValidation(header.Name, value) && request.Content is not null)
                        { request.Content.Headers.Remove(header.Name); request.Content.Headers.TryAddWithoutValidation(header.Name, value); }
                    }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                _statuses.Enqueue((int)response.StatusCode);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    RetryAt = retry?.Date ?? now.Add(retry?.Delta ?? TimeSpan.FromSeconds(60));
                }
                if ((int)response.StatusCode is >= 300 and < 400)
                    return Error(response.Headers.Location is { IsAbsoluteUri: true } moved
                        ? Loc.T($"地址跳转到了 {moved.GetLeftPart(UriPartial.Authority)}，请填写这个地址。", $"The address redirects to {moved.GetLeftPart(UriPartial.Authority)}. Enter that address instead.")
                        : Loc.T("站点发生重定向，请填写最终站点地址。", "The site redirects. Enter the final site address."));
                // A picked response is reduced here, so the script only ever holds the requested fields.
                var pick = options.Get("pick");
                int limit = pick.ValueKind == JsonValueKind.Object ? 8_000_000 : 1_000_000;
                if (response.Content.Headers.ContentLength > limit) return Error(Loc.T($"响应超过 {limit / 1_000_000} MB。", $"The response exceeds {limit / 1_000_000} MB."));
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream(); var bytes = new byte[8192];
                int length;
                while ((length = await stream.ReadAsync(bytes, timeout.Token)) > 0)
                {
                    if (buffer.Length + length > limit) return Error(Loc.T($"响应超过 {limit / 1_000_000} MB。", $"The response exceeds {limit / 1_000_000} MB."));
                    buffer.Write(bytes, 0, length);
                }
                var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
                var content = pick.ValueKind == JsonValueKind.Object ? Pick(buffer.ToArray(), pick) : Encoding.UTF8.GetString(buffer.ToArray());
                return JsonSerializer.Serialize(new { status = (int)response.StatusCode, headers, body = content });
            }
            finally { _requests.Release(); }
        }
        catch (OperationCanceledException) { return Error(Loc.T("读取超时或已取消。", "Reading timed out or was canceled.")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return Error(ex is InvalidDataException ? ex.Message : ex is HttpRequestException ? Loc.T("无法连接到站点。", "Cannot connect to the site.")
            : Loc.T("无法读取接口，请检查地址、网络和凭据。", "The API cannot be read. Check the address, the network and the credentials.")); }
    }
    // A Kimi Code API key (sk-kimi-…) reads the same GET {base}/usages and {base}/me as the Kimi Code sign-in, and the replies
    // are read by KimiUsage as for that source. kimi.com is asked first; a key it rejects is tried on kimi.ai. The region
    // shown follows /me, otherwise the service that answered; extra usage without a currency takes the region's.
    private async Task<ProviderResult> RunKimiAsync()
    {
        string t(string zh, string en) => Loc.T(zh, en);
        async Task<(int Status, string Body)> Get(string url)
        {
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret, ["Accept"] = "application/json" };
            using var reply = JsonDocument.Parse(await SendAsync("GET", JsonSerializer.Serialize(new { url, options = new { headers } })));
            if (reply.RootElement.Get("error").Text() is { Length: > 0 } error) throw new InvalidDataException(error);
            return ((int)(reply.RootElement.Get("status").Number() ?? 0), reply.RootElement.Get("body").Text());
        }
        static JsonElement Json(string body) { try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); } catch (JsonException) { return default; } }
        var baseUrl = KimiUsage.DefaultBaseUrl;
        var usages = await Get(baseUrl + "/usages");
        if (usages.Status == 401)
        {
            baseUrl = KimiUsage.GlobalBaseUrl;
            usages = await Get(baseUrl + "/usages");
            // Only the second answer describes the key; the first is just the other region's.
            if (usages.Status != 401) _statuses.Clear();
        }
        if (usages.Status == 401) throw new InvalidDataException(t("Kimi Code 拒绝了这个 API 密钥，请确认它是在 Kimi Code 控制台创建的密钥（sk-kimi-…），且未被删除。",
            "Kimi Code rejected this API key. Check that it was created in the Kimi Code console (sk-kimi-…) and has not been deleted."));
        if (usages.Status != 200) throw new HttpRequestException("HTTP " + usages.Status);
        var root = Json(usages.Body);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(t("Kimi Code 返回的额度无法识别。", "The quota Kimi Code returned is not recognized."));
        // The membership only adds the plan name and region; the quota still shows without it.
        JsonElement me = default;
        try { if (await Get(baseUrl + "/me") is { Status: 200 } reply) me = Json(reply.Body); }
        catch (InvalidDataException) { }
        bool global = KimiUsage.GlobalRegion(me) ?? baseUrl == KimiUsage.GlobalBaseUrl;
        var metrics = KimiUsage.Usages(root, global ? "USD" : "CNY");
        if (metrics.Count == 0) throw new InvalidDataException(t("这个密钥所属的账户没有 Kimi Code 会员套餐额度。", "The account of this key has no Kimi Code plan quota."));
        return new(metrics, KimiUsage.Plan(me), "plan", [], []) { Source = KimiUsage.Source(ProviderCatalog.Name(ProviderId.KimiApi), global) };
    }
    // An OpenCode Console API key (oc_sk_…) reads the same Go allowance as OpenCode's own sign-in, read by OpenCodeUsage. A key
    // belongs to one workspace, so none is named. opencode.ai sits behind Cloudflare, which refuses requests without a user agent
    // it accepts; its refusals are told apart from OpenCode's own 403, which means the workspace has no Go plan.
    private async Task<ProviderResult> RunOpenCodeGoAsync()
    {
        string t(string zh, string en) => Loc.T(zh, en);
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret, ["Accept"] = "application/json", ["User-Agent"] = "TwelveIsland/0.1" };
        var url = OpenCodeUsage.UsageAddress(OpenCodeUsage.DefaultServer)!.AbsoluteUri;
        using var reply = JsonDocument.Parse(await SendAsync("GET", JsonSerializer.Serialize(new { url, options = new { headers } })));
        if (reply.RootElement.Get("error").Text() is { Length: > 0 } error) throw new InvalidDataException(error);
        int status = (int)(reply.RootElement.Get("status").Number() ?? 0);
        var body = reply.RootElement.Get("body").Text();
        if (status == 401) throw new InvalidDataException(t("OpenCode 拒绝了这个 API 密钥，请确认它是在 OpenCode Console 创建的密钥（oc_sk_…），且未被撤销或过期。",
            "OpenCode rejected this API key. Check that it was created in OpenCode Console (oc_sk_…) and has not been revoked or expired."));
        if (status == 403 && !body.Contains("\"cloudflare_error\"", StringComparison.Ordinal))
            throw new InvalidDataException(t("这个密钥所属的工作区没有订阅 OpenCode Go，或密钥没有读取用量的权限。", "The workspace of this key has no OpenCode Go plan, or the key may not read usage."));
        if (status != 200) throw new HttpRequestException("HTTP " + status);
        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch (JsonException) { root = default; }
        var quotas = OpenCodeUsage.Allowance(root, out bool limited);
        if (quotas.Count == 0) throw new InvalidDataException(t("OpenCode 返回的 Go 用量无法识别。", "The Go usage OpenCode returned is not recognized."));
        List<string> warnings = limited ? [t("已达到 OpenCode Go 的用量上限，重置前请求会被拒绝", "The OpenCode Go limit is reached; requests are refused until it resets.")] : [];
        return new(quotas.Select(UsageMetric.FromQuota).ToList(), "", "plan", [], warnings) { Source = "OpenCode Go" };
    }
    private static string Error(string error) => JsonSerializer.Serialize(new { error });
    // pick: { path: "data", fields: ["a", "b"] } keeps only those fields of each object in the array at path; anything else becomes null.
    private static string Pick(byte[] body, JsonElement pick)
    {
        var path = pick.Get("path").Text(); var fields = pick.Get("fields");
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is 0 or > 20 || fields.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String))
            throw new InvalidDataException(Loc.T("pick.fields 必须是 1 到 20 个字段名。", "pick.fields must be 1 to 20 field names."));
        var names = fields.EnumerateArray().Select(f => f.GetString()!).ToArray();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var node = doc.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(5)) node = node.Get(segment);
            if (node.ValueKind != JsonValueKind.Array) return "null";
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    writer.WriteStartObject();
                    foreach (var name in names)
                        if (item.TryGetProperty(name, out var value)) { writer.WritePropertyName(name); value.WriteTo(writer); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (JsonException) { return "null"; }
    }
}

public sealed class ConfiguredProviders : IDisposable
{
    private readonly IProviderSecrets? _secrets;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _parallel = new(4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, ProviderSnapshot> _lastGood = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAt = new();
    private readonly ConcurrentDictionary<string, string> _memory = new();
    public ConfiguredProviders(IProviderSecrets? secrets = null, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _secrets = secrets; _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _clock = clock ?? (() => DateTimeOffset.Now);
    }
    public async Task<List<ProviderSnapshot>> RefreshAsync(IEnumerable<AppEntry> entries, CancellationToken ct)
    {
        var configurations = entries.Where(e => !ProviderCatalog.IsBuiltIn(e.QuotaSource)).Select(e => (Entry: e.Copy(), Secret: ReadSecret(e))).ToList();
        var groups = configurations.GroupBy(p => ProviderCatalog.RequestKey(p.Entry, p.Secret)).ToList();
        // State kept for a site or key that is no longer configured is dropped, so edits do not accumulate it.
        var current = groups.Select(g => g.Key).ToHashSet();
        static void Prune<T>(ConcurrentDictionary<string, T> map, HashSet<string> keep) { foreach (var key in map.Keys) if (!keep.Contains(key)) map.TryRemove(key, out _); }
        Prune(_lastGood, current); Prune(_retryAt, current); Prune(_memory, current);
        var tasks = groups.Select(async group =>
        {
            var first = group.First();
            var test = await FetchAsync(first.Entry, first.Secret, group.Key, ct);
            test.Snapshot.Configurations = group.Select(p => p.Entry.ConfigurationKey).ToHashSet();
            return test.Snapshot;
        });
        return (await Task.WhenAll(tasks)).ToList();
    }
    private string ReadSecret(AppEntry entry)
    {
        try { return _secrets?.Read(entry.InstanceId) ?? ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        { return ""; }
    }
    public Task<ProviderTestResult> TestAsync(AppEntry entry, CancellationToken ct = default)
    {
        var secret = ReadSecret(entry);
        return FetchAsync(entry.Copy(), secret, ProviderCatalog.RequestKey(entry, secret), ct);
    }
    private async Task<ProviderTestResult> FetchAsync(AppEntry entry, string secret, string requestKey, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(requestKey, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            await _parallel.WaitAsync(ct);
            try
            {
                var now = _clock(); var timer = Stopwatch.StartNew();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var session = new ScriptSession(_http, entry, secret, now, deadline.Token, _memory.GetValueOrDefault(requestKey));
                ProviderSnapshot snapshot = new(entry.QuotaSource) { Configurations = [entry.ConfigurationKey], Source = ProviderCatalog.Name(entry.QuotaSource) };
                try
                {
                    if (_retryAt.TryGetValue(requestKey, out var retry) && now < retry) throw new ProviderReadException(Loc.T("请求频繁", "Too many requests"), Loc.T($"站点要求在 {retry.LocalDateTime:HH:mm:ss} 后重试。", $"The site asks to retry after {retry.LocalDateTime:HH:mm:ss}."));
                    if (secret.Length == 0 && entry.QuotaSource != ProviderId.Custom) throw new ProviderReadException(Loc.T("未配置", "Not configured"), Loc.T("请在设置中填写此来源的 API 密钥。", "Enter this source's API key in settings."));
                    if (ProviderCatalog.NeedsSite(entry.QuotaSource) && (entry.Site.Length > 0 || entry.QuotaSource != ProviderId.Custom))
                    {
                        // An HTTP address saved by an earlier version, or kept after HTTP was turned off, stops here, before any request.
                        if (Uri.TryCreate(entry.Site, UriKind.Absolute, out var saved) && ProviderCatalog.NeedsHttps(saved, entry.AllowHttp))
                            throw new ProviderReadException(Loc.T("需要 HTTPS", "HTTPS required"), ProviderCatalog.HttpsRequired);
                        var uri = ProviderCatalog.ValidateUrl(entry.Site, entry.AllowHttp); if (uri.Query.Length > 0) throw new InvalidDataException(Loc.T("站点地址只需填写域名或部署子路径，不要包含查询参数。", "Enter only the domain or deployment path in the site address, without query parameters.")); }
                    var result = await Task.Run(session.RunAsync, deadline.Token);
                    snapshot.Metrics = result.Metrics; snapshot.Plan = result.Plan; snapshot.Scope = result.Scope;
                    snapshot.Details = result.Details; snapshot.QuotaTime = now; snapshot.LiveQuota = true;
                    if (result.Source.Length > 0) snapshot.Source = result.Source;
                    // Warnings follow the state after the separator the settings test reads back (SettingsProviders).
                    snapshot.StatusLabel = Loc.T("已连接", "Connected");
                    snapshot.Status = result.Warnings.Count == 0 ? snapshot.StatusLabel : snapshot.StatusLabel + Loc.T("；", "; ") + string.Join(Loc.T("；", "; "), result.Warnings);
                    _lastGood[requestKey] = Clone(snapshot);
                    if (result.Memory is { } kept) _memory[requestKey] = kept; else _memory.TryRemove(requestKey, out _);
                    _retryAt.TryRemove(requestKey, out _);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (_lastGood.TryGetValue(requestKey, out var previous)) snapshot = Clone(previous);
                    snapshot.Configurations = [entry.ConfigurationKey]; snapshot.LiveQuota = false; snapshot.IsStale = snapshot.Metrics.Count > 0;
                    var code = session.FailureStatus;
                    snapshot.StatusLabel = ex is ProviderReadException read ? read.Label : code switch
                    { 401 => Loc.T("密钥无效", "Invalid key"), 403 => Loc.T("访问受限", "Access denied"), 429 => Loc.T("请求频繁", "Too many requests"), >= 400 => Loc.T("读取失败", "Read failed"),
                        _ => deadline.IsCancellationRequested ? Loc.T("读取超时", "Timed out") : entry.QuotaSource == ProviderId.Custom && ex is not InvalidDataException ? Loc.T("脚本出错", "Script error") : Loc.T("数据不可用", "Data unavailable") };
                    // A reason the script wrote itself is shown as is; only bare "HTTP nnn" messages are replaced by the generic wording.
                    var message = ScriptMessage(ex);
                    bool explained = ex is ProviderReadException or InvalidDataException || ex is Jint.Runtime.JavaScriptException or Jint.Runtime.PromiseRejectedException && !System.Text.RegularExpressions.Regex.IsMatch(message, @"^HTTP \d+$");
                    snapshot.Status = explained ? session.Redact(message) : code switch
                    { 401 => Loc.T("站点拒绝了这个密钥，请在设置中更新。", "The site rejected this key. Update it in settings."), 403 => Loc.T("站点拒绝访问，请检查密钥权限。", "The site refused access. Check the key's permissions."),
                        429 => Loc.T("请求频繁，稍后自动重试。", "Too many requests. Retrying automatically later."), _ => deadline.IsCancellationRequested ? Loc.T("读取超时，稍后重试。", "Reading timed out. Retrying later.") : session.Redact(message) };
                    if (snapshot.Status.Length > 200) snapshot.Status = snapshot.Status[..200];
                    if (snapshot.IsStale) snapshot.Status += Loc.T($" 显示 {snapshot.QuotaTime?.LocalDateTime:MM-dd HH:mm} 读取的数据。", $" Showing the data read at {snapshot.QuotaTime?.LocalDateTime:MM-dd HH:mm}.");
                }
                if (session.RetryAt is { } next) _retryAt[requestKey] = next;
                return new(snapshot, timer.Elapsed.TotalSeconds, session.Logs);
            }
            finally { _parallel.Release(); }
        }
        finally { gate.Release(); }
    }
    // An error thrown inside the async script arrives as a rejected promise; its own message is what the user should read.
    private static string ScriptMessage(Exception ex)
    {
        var value = ex switch { Jint.Runtime.PromiseRejectedException rejected => rejected.RejectedValue, Jint.Runtime.JavaScriptException thrown => thrown.Error, _ => null };
        if (value is Jint.Native.Object.ObjectInstance error && error.Get("message") is { } text && text.IsString()) return text.AsString();
        return value is null ? ex.Message : value.ToString();
    }
    private static ProviderSnapshot Clone(ProviderSnapshot source) => new(source.Id)
    { Metrics = source.Metrics.ToList(), Plan = source.Plan, Scope = source.Scope, Details = new(source.Details), QuotaTime = source.QuotaTime,
        LiveQuota = source.LiveQuota, Source = source.Source, Status = source.Status, StatusLabel = source.StatusLabel, Configurations = [.. source.Configurations], IsStale = source.IsStale };
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    private sealed class ProviderReadException(string label, string message) : Exception(message) { public string Label { get; } = label; }
}
