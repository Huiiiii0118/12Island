using Microsoft.Win32;

namespace TwelveIsland.Core;

// Claude Code and the Codex CLI inside WSL keep their session records in the distribution's own file system, which
// Windows reaches at \\wsl.localhost\<distribution>. Reading those paths starts a stopped distribution, so they are only
// visited when the user has asked for it.
public sealed record WslHome(string Distribution, string Claude, string Codex);

public static class WslLocations
{
    // Installed distributions come from the registry, so nothing has to be started to list them. The distributions that
    // Docker Desktop and Rancher Desktop add are private to them and hold no user sessions.
    public static IReadOnlyList<string> Distributions()
    {
        var names = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows()) return names;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (key is null) return names;
            foreach (var id in key.GetSubKeyNames())
            {
                using var item = key.OpenSubKey(id);
                if (item?.GetValue("DistributionName") is string name && name.Length > 0 &&
                    !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return names;
    }

    public static IReadOnlyList<WslHome> Discover() =>
        Distributions().SelectMany(name => Homes(name, @"\\wsl.localhost\" + name)).ToList();

    // The home folders of one distribution (root and every user under /home) that hold Claude or Codex records.
    public static IReadOnlyList<WslHome> Homes(string distribution, string distributionRoot)
    {
        var result = new List<WslHome>();
        try
        {
            var homes = new List<string> { Path.Combine(distributionRoot, "root") };
            var users = Path.Combine(distributionRoot, "home");
            if (Directory.Exists(users)) homes.AddRange(Directory.EnumerateDirectories(users));
            foreach (var home in homes)
            {
                var claude = Path.Combine(home, ".claude"); var codex = Path.Combine(home, ".codex");
                bool hasClaude = Directory.Exists(Path.Combine(claude, "projects")), hasCodex = Directory.Exists(codex);
                if (hasClaude || hasCodex) result.Add(new(distribution, hasClaude ? claude : "", hasCodex ? codex : ""));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
