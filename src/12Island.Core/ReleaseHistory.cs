using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public sealed record ReleaseEntry(string Version, ReleaseNotes Notes);

// The release notes built into this copy: releases/<version>.md, embedded at build time, so the What's new window works
// offline and matches the installed version. Notes edited on GitHub after a release reach the app with the next version.
public static partial class ReleaseHistory
{
    private const string Prefix = "TwelveIsland.Core.Releases.";
    private static readonly Lazy<IReadOnlyList<ReleaseEntry>> BundledEntries = new(LoadBundled);
    public static IReadOnlyList<ReleaseEntry> Bundled => BundledEntries.Value;

    // The same split as scripts/ReleaseNotes.ps1, which refuses malformed files before a release. A file without markers is
    // notes for every language; text before the first marker belongs to no language and is left out.
    public static ReleaseNotes Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        var markers = LanguageMarker().Matches(text);
        if (markers.Count == 0) return ReleaseNotes.Of(text);
        var entries = new List<(string, string)>();
        for (int i = 0; i < markers.Count; i++)
        {
            int start = markers[i].Index + markers[i].Length, end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            entries.Add((markers[i].Groups[1].Value, text[start..end]));
        }
        return new ReleaseNotes(entries);
    }

    // The versions the window lists, newest first: every release up to this one. Alphas are never listed. Betas are listed
    // only with beta updates on (AppUpdates.Beta), and only those newer than the last release: a release gathers the notes
    // of the betas before it.
    public static IReadOnlyList<ReleaseEntry> Shown(IEnumerable<ReleaseEntry> entries, string current, bool beta)
    {
        var order = Comparer<string>.Create(ApplicationVersion.Compare);
        var known = entries.Where(e => ApplicationVersion.Compare(e.Version, current) <= 0).ToList();
        var lastRelease = known.Where(e => !ApplicationVersion.IsPrerelease(e.Version)).Select(e => e.Version).Max(order);
        return known.Where(e => !ApplicationVersion.IsPrerelease(e.Version) ||
                beta && IsBeta(e.Version) && (lastRelease is null || ApplicationVersion.Compare(e.Version, lastRelease) > 0))
            .OrderByDescending(e => e.Version, order).ToList();
    }

    private static bool IsBeta(string version) => version.Split('+')[0].Contains("-beta.", StringComparison.OrdinalIgnoreCase);

    // Whether a listed version arrived with the update from updatedFrom (AppUpdates.UpdatedFrom). An empty updatedFrom means
    // an earlier version ran but did not record which, so only this version is marked.
    public static bool IsNew(string version, string? updatedFrom, string current) => updatedFrom switch
    {
        null => false,
        "" => ApplicationVersion.Compare(version, current) == 0,
        _ => ApplicationVersion.Compare(version, updatedFrom) > 0 && ApplicationVersion.Compare(version, current) <= 0
    };

    private static IReadOnlyList<ReleaseEntry> LoadBundled()
    {
        var assembly = typeof(ReleaseHistory).Assembly;
        var entries = new List<ReleaseEntry>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            entries.Add(new(name[Prefix.Length..^3], Parse(reader.ReadToEnd())));
        }
        return entries;
    }

    [GeneratedRegex(@"^[ \t]*<!--[ \t]*lang:[ \t]*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)[ \t]*-->[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex LanguageMarker();
}
