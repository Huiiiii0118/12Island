using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// A file of a release that the app downloads: its plain name, size and SHA-256.
public sealed record UpdatePackage(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

// update.json is published with every release and names the installer that belongs to it. "portable" names the
// portable zip; versions before it ignore the field, so file, size and sha256 keep describing the installer.
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("notes")] ReleaseNotes Notes,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("portable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UpdatePackage? Portable = null)
{
    public const string FileName = "update.json";
    private const long MaxSize = 1L << 30;

    public static UpdateManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new InvalidDataException("Empty update manifest.");
        return manifest.IsValid ? manifest with { Notes = manifest.Notes ?? ReleaseNotes.Of("") } : throw new InvalidDataException("Invalid update manifest.");
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    // The package a copy downloads: the installer, or for a portable copy the zip (null when the release has none).
    public UpdatePackage? Package(UpdateMode mode) => mode == UpdateMode.Portable ? Portable : new(File, Size, Sha256);

    // Only plain file names are accepted, so a manifest cannot direct the download outside the updates folder.
    private bool IsValid => IsVersion(Version) && IsPackage(new(File, Size, Sha256), ".exe") && (Portable is null || IsPackage(Portable, ".zip"));

    private static bool IsPackage(UpdatePackage package, string extension) => package.Size is > 0 and <= MaxSize &&
        !string.IsNullOrEmpty(package.File) && package.File.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
        package.File.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        package.Sha256 is { Length: 64 } && package.Sha256.All(char.IsAsciiHexDigit);

    // Major.minor.patch with an optional prerelease label, as in the release tags ("0.3.0", "0.3.0-beta.1").
    // The version also names the tag the installer is downloaded from, so nothing else is accepted.
    private static bool IsVersion(string? value) => value is not null && Regex.IsMatch(value, @"^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?\z");
}

// How this copy updates itself: through the installer that registered it, by replacing the files of a portable copy, or
// not at all (a build output or a copied folder).
public enum UpdateMode { None, Installer, Portable }

// Why the last attempt to replace a portable copy kept the old version.
public enum RestartFailure { Declined, Failed }

// Downloads the package of a newer release, verifies its size and SHA-256, and applies it after the app exits.
// Installer: runs it silently; it waits for the running instance, installs into the existing folder and starts TwelveIsland again.
// Portable: extracts the zip and starts its own executable as the helper in PortableUpdate, which replaces the files.
public sealed class InstallerUpdateBackend : IAppUpdateBackend
{
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH";
    private const string PendingFile = "pending.json";
    private const string PortableFolder = "portable";
    private readonly HttpClient _http;
    private readonly Uri _manifest;
    private readonly Uri? _betaManifest;
    private readonly Func<UpdateManifest, UpdatePackage, Uri> _package;
    private readonly string _directory;
    private readonly string _appPath;
    private readonly Func<ProcessStartInfo, bool> _launch;
    private UpdateManifest? _available;

    // appPath is the running executable; a portable copy replaces the folder it is in. betaManifest is the newest beta's
    // update.json, read only when betas are asked for.
    public InstallerUpdateBackend(HttpClient http, Uri manifest, Func<UpdateManifest, UpdatePackage, Uri> package, string currentVersion,
        string directory, UpdateMode mode, Func<ProcessStartInfo, bool>? launch = null, string? appPath = null, Uri? betaManifest = null)
    {
        _http = http; _manifest = manifest; _betaManifest = betaManifest; _package = package; _directory = directory; Mode = mode;
        CurrentVersion = currentVersion;
        _appPath = appPath ?? Environment.ProcessPath ?? "";
        _launch = launch ?? (start => { using var process = Process.Start(start); return process is not null; });
    }

    public string CurrentVersion { get; }
    public UpdateMode Mode { get; }
    public bool CanInstall => Mode != UpdateMode.None;
    public string RestartFailureFile => Path.Combine(_directory, PortableUpdate.FailureFile);

    // Read once at startup. A pending package that is not newer than the running version was applied or is stale.
    public AppRelease? PendingRelease
    {
        get
        {
            if (Pending() is { } manifest) return Describe(manifest);
            DeleteDownloads(); return null;
        }
    }

    // Left by the portable helper when it kept the old version and started it again. Read once, before PendingRelease.
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

    public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => CheckAsync(false, cancellation);

    // With beta, the newer of the release and the newest beta is offered. The beta channel exists only once a beta has been
    // published, so a beta manifest that cannot be read leaves the result of the release check.
    public async Task<AppRelease?> CheckAsync(bool beta, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var manifest = await ReadManifestAsync(_manifest, timeout.Token).ConfigureAwait(false);
        if (beta && _betaManifest is not null)
        {
            try
            {
                var newest = await ReadManifestAsync(_betaManifest, timeout.Token).ConfigureAwait(false);
                if (ApplicationVersion.Compare(newest.Version, manifest.Version) > 0) manifest = newest;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException ||
                ex is OperationCanceledException && !cancellation.IsCancellationRequested) { }
        }
        _available = IsNewer(manifest) ? manifest : null;
        return _available is null ? null : Describe(_available);
    }

    private async Task<UpdateManifest> ReadManifestAsync(Uri uri, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateCheckException(response.StatusCode, RetryAfter(response.Headers.RetryAfter));
        return UpdateManifest.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    // Retry-After is either a number of seconds or a date.
    private static TimeSpan? RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? value) =>
        value?.Delta ?? (value?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        var manifest = _available is { } available && available.Version == release.Version ? available
            : throw new InvalidOperationException("Update selection changed.");
        // A release published before portable packages existed has nothing a portable copy can apply.
        var package = manifest.Package(Mode) ?? throw new InvalidDataException("The release has no package for this copy.");
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, package.File); var partial = target + ".partial";
        try
        {
            using (var response = await _http.GetAsync(_package(manifest, package), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != package.Size) throw new InvalidDataException("Package size mismatch.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int reported = -1, read;
                // The client timeout ends with the response headers; a body that stops arriving fails after 30 seconds without data.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                stall.CancelAfter(StallTimeout);
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    total += read;
                    if (total > package.Size) throw new InvalidDataException("Package size mismatch.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    var percent = (int)(total * 100 / package.Size);
                    if (percent != reported) { reported = percent; progress(percent); }
                }
                if (total != package.Size || !Matches(hash.GetHashAndReset(), package.Sha256)) throw new InvalidDataException("Package checksum mismatch.");
            }
            File.Move(partial, target, true);
            var pending = Path.Combine(_directory, PendingFile);
            File.WriteAllText(pending + ".tmp", manifest.ToJson());
            File.Move(pending + ".tmp", pending, true);
        }
        catch { TryDelete(partial); throw; }
    }

    // The file is hashed again right before it is run or extracted, so a download changed after verification is never used.
    public void PrepareRestart()
    {
        var manifest = Pending() ?? throw new InvalidOperationException("No verified update is ready.");
        var package = manifest.Package(Mode)!;
        var path = Path.Combine(_directory, package.File);
        bool valid;
        using (var stream = File.OpenRead(path)) valid = stream.Length == package.Size && Matches(SHA256.HashData(stream), package.Sha256);
        if (!valid) { DeleteDownloads(); throw new InvalidDataException("Package checksum mismatch."); }
        var start = Mode == UpdateMode.Portable ? PortableHelper(path)
            : new ProcessStartInfo(path, InstallerArguments) { UseShellExecute = true, WorkingDirectory = _directory };
        if (!_launch(start)) throw new InvalidOperationException("Update did not start.");
    }

    // The new version's own executable replaces the files once this process has exited, then starts the new version.
    private ProcessStartInfo PortableHelper(string zip)
    {
        var staging = Path.Combine(_directory, PortableFolder);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        var source = PortableUpdate.FindPackage(staging) ?? throw new InvalidDataException("The portable package has no file list.");
        var helper = Path.Combine(source, Path.GetFileName(_appPath));
        if (!File.Exists(helper)) throw new InvalidDataException("The portable package has no " + Path.GetFileName(_appPath) + ".");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = source };
        foreach (var argument in PortableUpdate.HelperArguments(Path.GetDirectoryName(_appPath)!, Environment.ProcessId, RestartFailureFile))
            start.ArgumentList.Add(argument);
        return start;
    }

    private UpdateManifest? Pending()
    {
        try
        {
            var path = Path.Combine(_directory, PendingFile);
            if (!File.Exists(path)) return null;
            var manifest = UpdateManifest.Parse(File.ReadAllText(path));
            if (manifest.Package(Mode) is not { } package) return null;
            var file = new FileInfo(Path.Combine(_directory, package.File));
            return IsNewer(manifest) && file.Exists && file.Length == package.Size ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return null; }
    }

    private bool IsNewer(UpdateManifest manifest) => ApplicationVersion.Compare(manifest.Version, CurrentVersion) > 0;
    // The size shown is that of the package this copy downloads.
    private AppRelease Describe(UpdateManifest manifest) => new(manifest.Version, manifest.Notes, manifest.Package(Mode)?.Size ?? manifest.Size);
    private static bool Matches(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private void DeleteDownloads()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
