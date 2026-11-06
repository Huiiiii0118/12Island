namespace TwelveIsland.Core;

public sealed record DataLocations(string ClaudeHome, string CodexHome, string CursorDatabase, string Roaming, string Local)
{
    // ZCode resolves its data directory as ZCODE_HOME, otherwise %USERPROFILE%\.zcode.
    public string ZCodeHome { get; init; } = "";
    // DeepSeek Harness resolves its data directory as DSH_HOME, otherwise %USERPROFILE%\.dsh; the desktop app and the local web app share it.
    public string DshHome { get; init; } = "";
    // Kimi Code resolves its data directory as KIMI_CODE_HOME, otherwise %USERPROFILE%\.kimi-code; its CLI, desktop app and VS Code extension share it.
    public string KimiHome { get; init; } = "";
    // Pi's session root.
    public string PiSessions { get; init; } = "";
    // The environment variables that change which Kimi Code service is used (KIMI_CODE_BASE_URL, KIMI_CODE_OAUTH_HOST and
    // the others in KimiEnvironment); all null when unset.
    public KimiEnvironment KimiEnvironment { get; init; } = new();
    // Conversation databases of Antigravity 2.x and the Antigravity CLI.
    public IReadOnlyList<string> AntigravityConversations { get; init; } = [];
    // OpenCode's data folder ($XDG_DATA_HOME\opencode, otherwise %USERPROFILE%\.local\share\opencode) and its database
    // (OPENCODE_DB, otherwise opencode.db there); its CLI and desktop app share both.
    public string OpenCodeData { get; init; } = "";
    public string OpenCodeDatabase { get; init; } = "";

    public static DataLocations Detect() => (Resolve(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetEnvironmentVariable("CODEX_HOME"),
        Environment.GetEnvironmentVariable("ZCODE_HOME"), Environment.GetEnvironmentVariable("DSH_HOME"), Environment.GetEnvironmentVariable("KIMI_CODE_HOME"),
        Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"), Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR"))
    with { KimiEnvironment = new(Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL"),
        Environment.GetEnvironmentVariable("KIMI_CODE_OAUTH_HOST") ?? Environment.GetEnvironmentVariable("KIMI_OAUTH_HOST"),
        Environment.GetEnvironmentVariable("KIMI_CODE_REGION_MARKER")) })
    .WithOpenCode(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("OPENCODE_DB"));

    public DataLocations WithOpenCode(string home, string? xdgData, string? database)
    {
        var data = OpenCodeUsage.DataFolder(home, xdgData);
        return this with { OpenCodeData = data, OpenCodeDatabase = OpenCodeUsage.Database(data, database) };
    }

    public static DataLocations Resolve(string home, string roaming, string local, string? claudeOverride, string? codexOverride, string? zcodeOverride = null, string? dshOverride = null, string? kimiOverride = null, string? piOverride = null, string? piSessionOverride = null) => new(
        ConfigDirectory(claudeOverride, Path.Combine(home, ".claude")),
        ConfigDirectory(codexOverride, Path.Combine(home, ".codex")),
        Path.Combine(roaming, "Cursor", "User", "globalStorage", "state.vscdb"), roaming, local)
    { ZCodeHome = ConfigDirectory(zcodeOverride, Path.Combine(home, ".zcode")), DshHome = ConfigDirectory(dshOverride, Path.Combine(home, ".dsh")), KimiHome = ConfigDirectory(kimiOverride, Path.Combine(home, ".kimi-code")), AntigravityConversations = AntigravityUsage.Folders(home),
      PiSessions = PiSessionDirectory(home, piOverride, piSessionOverride), OpenCodeData = OpenCodeUsage.DataFolder(home, null), OpenCodeDatabase = OpenCodeUsage.Database(OpenCodeUsage.DataFolder(home, null), null) };

    private static string ConfigDirectory(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : fallback;

    // Pi accepts relative paths and expands ~ itself, unlike the other applications' directory overrides.
    // Resolve only the selected override: a session override takes precedence even if the agent override is invalid.
    private static string PiSessionDirectory(string home, string? agent, string? sessions) =>
        !string.IsNullOrEmpty(sessions) ? PiDirectory(sessions, home) :
        Path.Combine(!string.IsNullOrEmpty(agent) ? PiDirectory(agent, home) : Path.Combine(home, ".pi", "agent"), "sessions");

    private static string PiDirectory(string value, string home)
    {
        // Match Pi's Windows normalization for Git Bash/MSYS/Cygwin/WSL drive paths.
        if (OperatingSystem.IsWindows() && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\'))
        {
            var drive = System.Text.RegularExpressions.Regex.Match(value, @"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (drive.Success) value = char.ToUpperInvariant(drive.Groups[1].Value[0]) + @":\" + drive.Groups[2].Value.Replace('/', '\\');
        }
        if (value == "~") value = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && value.StartsWith(@"~\", StringComparison.Ordinal)))
            value = Path.Join(home, value[2..]);
        else if (value.StartsWith("file://", StringComparison.Ordinal)) value = new Uri(value).LocalPath;
        // Do not trim meaningful spaces, expand %variables%, or silently substitute the default for an explicit path.
        return Path.GetFullPath(value);
    }

    public IReadOnlyList<string> ClaudeDesktopProfiles()
    {
        var paths = new List<string> { Path.Combine(Roaming, "Claude") };
        void Add(string parent, string pattern, Func<IEnumerable<string>, IEnumerable<string>> profiles)
        {
            try
            {
                if (Directory.Exists(parent)) paths.AddRange(profiles(Directory.EnumerateDirectories(parent, pattern)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Add(Path.Combine(Local, "Packages"), "Claude_*", packages => packages.Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude")));
        Add(Path.Combine(Local, "Packages"), "AnthropicPBC.Claude_*", packages => packages.Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude")));
        // Newer desktop versions move their data to %LOCALAPPDATA%\Claude-Data\<install>, which also holds a README; a folder
        // there is a profile once the app has written its config.json.
        Add(Path.Combine(Local, "Claude-Data"), "*", folders => folders.Where(HasConfig));
        return paths.Where(path => Directory.Exists(path) && (HasConfig(path) || !Left(path))).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "config.json"))).ToList();
    }

    private static bool HasConfig(string profile) => File.Exists(Path.Combine(profile, "config.json"));

    // After moving to Claude-Data the desktop app leaves a ".user-data-left.<install>" marker in each old folder. The marker names
    // the install that left, and another install may still use the folder, so a folder that kept its config.json stays a profile.
    private static bool Left(string profile)
    {
        try { return Directory.EnumerateFiles(profile, ".user-data-left.*").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
