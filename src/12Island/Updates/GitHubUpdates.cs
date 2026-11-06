using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TwelveIsland.Core;

namespace TwelveIsland.Updates;

// Checks GitHub Releases for a newer version and applies it. A release carries one Windows executable named
// 12Island-<version>-windows-x64.exe; the tag names the version ("v1.2.0" or "1.2.0"), and the release body is its notes.
//
// An update is applied through PortableUpdate: the download lands in the data folder with a 12Island.portable list that
// names only the executable, and PrepareRestart starts that downloaded copy with the helper arguments — it waits for this
// process to leave, replaces the folder's executable and starts the new version, restoring the old one on any failure.
internal sealed class GitHubUpdates : IAppUpdateBackend
{
    private const string ListFile = "12Island.portable";
    private const string PendingFile = "pending.json";
    private const string ExecutableName = "12Island.exe";
    private const long MaxSize = 1L << 30;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _dataFolder;
    private readonly string _staging;
    private readonly string _appPath;
    private readonly Func<ProcessStartInfo, bool> _launch;
    private Available? _available;

    private sealed record Available(string Version, string Notes, long Size, string Url);
    private sealed record PendingUpdate(string Version, long Size, string Notes);
    private sealed record ReleaseInfo([property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("body")] string? Body, [property: JsonPropertyName("assets")] List<Asset>? Assets);
    private sealed record Asset([property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("browser_download_url")] string? Url);

    public GitHubUpdates(string dataFolder, Func<ProcessStartInfo, bool>? launch = null, string? appPath = null)
    {
        _dataFolder = dataFolder;
        _staging = Path.Combine(dataFolder, "updates", "github");
        _appPath = appPath ?? Environment.ProcessPath ?? "";
        _launch = launch ?? (start => { using var process = Process.Start(start); return process is not null; });
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"12Island/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string CurrentVersion => ApplicationVersion.Current;
    public bool CanInstall => _appPath.Length > 0;
    public string RestartFailureFile => Path.Combine(_staging, PortableUpdate.FailureFile);

    private string ApiLatest => $"https://api.github.com/repos/{App.RepoOwner}/{App.RepoName}/releases/latest";

    // Read once at startup. A staged package that is not newer than the running version was applied or is stale.
    public AppRelease? PendingRelease
    {
        get
        {
            if (Pending() is { } pending) return new AppRelease(pending.Version, ReleaseNotes.Of(pending.Notes), pending.Size);
            DeleteStaging(); return null;
        }
    }

    // Left by the replacement helper when it kept the old version and started it again. Read once, before PendingRelease.
    public RestartFailure? LastRestartFailure
    {
        get
        {
            try
            {
                if (!File.Exists(RestartFailureFile)) return null;
                var code = File.ReadLines(RestartFailureFile).FirstOrDefault();
                File.Delete(RestartFailureFile);
                return code == PortableUpdate.Declined ? RestartFailure.Declined : RestartFailure.Failed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var release = await ReadReleaseAsync(timeout.Token).ConfigureAwait(false);
        // "v1.2.0" and "1.2.0" name the same release; anything else is not a version this copy can compare itself with.
        string version = release.Tag?.TrimStart('v') ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return null;
        // The executable is what this copy downloads and what the helper replaces the folder's copy with, so a release
        // without it offers nothing.
        var asset = release.Assets?.FirstOrDefault(a => a.Name == $"12Island-{version}-windows-x64.exe" && a.Size is > 0 and <= MaxSize);
        if (asset is null) return null;
        if (ApplicationVersion.Compare(version, CurrentVersion) <= 0) return null;
        _available = new Available(version, release.Body ?? "", asset.Size, asset.Url!);
        return new AppRelease(version, ReleaseNotes.Of(_available.Notes), _available.Size);
    }

    private async Task<ReleaseInfo> ReadReleaseAsync(CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(ApiLatest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateCheckException(response.StatusCode, RetryAfter(response.Headers.RetryAfter));
        return JsonSerializer.Deserialize<ReleaseInfo>(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Empty release response.");
    }

    // Retry-After is either a number of seconds or a date.
    private static TimeSpan? RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? value) =>
        value?.Delta ?? (value?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        var available = _available is { } found && found.Version == release.Version ? found
            : throw new InvalidOperationException("Update selection changed.");
        Directory.CreateDirectory(_staging);
        var target = Path.Combine(_staging, ExecutableName);
        var partial = target + ".partial";
        try
        {
            using (var response = await _http.GetAsync(available.Url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != available.Size) throw new InvalidDataException("Package size mismatch.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; long total = 0; int reported = -1, read;
                // The client timeout ends with the response headers; a body that stops arriving fails after 30 seconds without data.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                stall.CancelAfter(StallTimeout);
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    total += read;
                    if (total > available.Size) throw new InvalidDataException("Package size mismatch.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    var percent = (int)(total * 100 / available.Size);
                    if (percent != reported) { reported = percent; progress(percent); }
                }
                if (total != available.Size) throw new InvalidDataException("Package size mismatch.");
            }
            File.Move(partial, target, true);
            // The replacement machinery copies exactly the files this list names, so it replaces the executable and nothing else.
            await File.WriteAllTextAsync(Path.Combine(_staging, ListFile), ExecutableName + Environment.NewLine, cancellation).ConfigureAwait(false);
            var pending = Path.Combine(_staging, PendingFile);
            await File.WriteAllTextAsync(pending + ".tmp", JsonSerializer.Serialize(new PendingUpdate(available.Version, available.Size, available.Notes), JsonOptions), cancellation).ConfigureAwait(false);
            File.Move(pending + ".tmp", pending, true);
        }
        catch { TryDelete(partial); throw; }
    }

    // The downloaded copy is started as the replacement helper: it waits for this process, swaps the executable and starts
    // the new version. The size is checked again here, so a staging folder changed after the download is never used.
    public void PrepareRestart()
    {
        var pending = Pending() ?? throw new InvalidOperationException("No verified update is ready.");
        var path = Path.Combine(_staging, ExecutableName);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = _staging };
        foreach (var argument in PortableUpdate.HelperArguments(Path.GetDirectoryName(_appPath)!, Environment.ProcessId, RestartFailureFile))
            start.ArgumentList.Add(argument);
        if (!_launch(start)) throw new InvalidOperationException("Update did not start.");
    }

    private PendingUpdate? Pending()
    {
        try
        {
            var path = Path.Combine(_staging, PendingFile);
            if (!File.Exists(path)) return null;
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path), JsonOptions);
            if (pending is null || ApplicationVersion.Compare(pending.Version, CurrentVersion) <= 0) return null;
            return new FileInfo(Path.Combine(_staging, ExecutableName)) is { Exists: true } file && file.Length == pending.Size ? pending : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return null; }
    }

    private void DeleteStaging()
    {
        try { if (Directory.Exists(_staging)) Directory.Delete(_staging, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
