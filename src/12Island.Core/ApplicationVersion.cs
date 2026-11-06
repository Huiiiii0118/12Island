using System.Reflection;

namespace TwelveIsland.Core;

public static class ApplicationVersion
{
    // Version of the running application: the entry assembly's informational version without build metadata
    // ("0.2.0+abc123" -> "0.2.0", "0.3.0-beta.1+abc123" -> "0.3.0-beta.1"). Release builds receive it from the tag through -p:Version.
    public static string Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // Major.minor.patch; a prerelease suffix ("0.2.0-beta") compares as its release. Unreadable text counts as 0.0.0.
    public static Version Parse(string? text)
    {
        var core = (text ?? "").Split('+', '-')[0].Trim();
        return Version.TryParse(core, out var version) && version.Build >= 0 ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 0, 0);
    }

    // True for a version with a prerelease label ("0.3.0-beta.1").
    public static bool IsPrerelease(string? text) => Prerelease(text).Length > 0;

    // Release order for updates. A prerelease comes before its release ("0.3.0-beta.2" < "0.3.0"); prerelease labels
    // compare part by part as in SemVer: numbers by value, text by ordinal, numbers before text, a longer label after its prefix.
    public static int Compare(string? left, string? right)
    {
        var result = Parse(left).CompareTo(Parse(right));
        if (result != 0) return result;
        string[] a = Prerelease(left), b = Prerelease(right);
        if (a.Length == 0 || b.Length == 0) return b.Length.CompareTo(a.Length);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool isNumber = ulong.TryParse(a[i], out var x), otherIsNumber = ulong.TryParse(b[i], out var y);
            result = isNumber && otherIsNumber ? x.CompareTo(y) : isNumber != otherIsNumber ? (isNumber ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return Math.Sign(result);
        }
        return a.Length.CompareTo(b.Length);
    }

    private static string[] Prerelease(string? text)
    {
        var release = (text ?? "").Split('+')[0].Trim();
        var dash = release.IndexOf('-');
        return dash < 0 ? [] : release[(dash + 1)..].Split('.');
    }

    private static string Normalize(string? text)
    {
        var release = (text ?? "").Split('+')[0].Trim();
        var dash = release.IndexOf('-');
        return Parse(release).ToString(3) + (dash < 0 ? "" : release[dash..]);
    }
}
