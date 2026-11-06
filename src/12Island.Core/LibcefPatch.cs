using System.Buffers;
using System.IO;

namespace TwelveIsland.Core;

// Every site holds either its original or its patched bytes, but not all the same, as when a patch was interrupted or
// another tool changed only some of them. Unsupported: a site is missing, is not unique, or is present in both forms.
public enum LibcefPatchState { Original, Patched, Partial, Unsupported }

// A change to a player's browser core (libcef.dll) that makes it open its DevTools port on 127.0.0.1 every time it starts.
// Each site is a short byte window — a fixed Prefix and Suffix bracketing a Core — found by searching the whole file, not by
// a fixed offset, so a build that only moved the code is still found. Only the Core is written; Prefix and Suffix are stable
// context that does not change between the original and the patched file, so a part-patched file still locates each site.
// A window must occur exactly once in the file; a build it no longer matches is reported Unsupported and left untouched.
public sealed class LibcefPatch
{
    // Original and Patched are the two forms of the Core and have the same length; Prefix and Suffix may be empty.
    public sealed record Site(byte[] Prefix, byte[] Original, byte[] Patched, byte[] Suffix)
    {
        public byte[] Window(bool patched) => [.. Prefix, .. (patched ? Patched : Original), .. Suffix];
    }

    private readonly Site[] _sites;
    public int Port { get; }
    public IReadOnlyList<Site> Sites => _sites;
    public LibcefPatch(int port, params Site[] sites) { Port = port; _sites = sites; }

    // The file is searched a chunk at a time (a pooled buffer), as reading the whole 150 MB library at once kept that much memory
    // until the next full collection. Each chunk starts with the last bytes of the one before, so a window across the boundary
    // is found too.
    public const int ChunkSize = 1 << 20;

    // Where each site's Core sits in this file and whether it already holds the patched bytes; null for a file no profile
    // fits (a site absent, found more than once, or present in both forms).
    private List<(Site Site, long Offset, bool Patched)>? Locate(Stream file)
    {
        // Each site's original window, then its patched one: where each was first found, and how often (counted up to 2).
        var windows = _sites.SelectMany(site => new[] { site.Window(false), site.Window(true) }).ToArray();
        var first = new long[windows.Length];
        var seen = new int[windows.Length];
        int overlap = windows.Max(window => window.Length) - 1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            file.Position = 0;
            long start = 0;
            int kept = 0;
            while (true)
            {
                int filled = kept + file.ReadAtLeast(buffer.AsSpan(kept), buffer.Length - kept, throwOnEndOfStream: false);
                var chunk = buffer.AsSpan(0, filled);
                for (int i = 0; i < windows.Length; i++)
                {
                    // A match that lies wholly within the kept bytes was counted in the chunk before.
                    int from = Math.Max(0, kept - windows[i].Length + 1);
                    while (seen[i] < 2 && from <= filled)
                    {
                        int at = chunk[from..].IndexOf(windows[i]);
                        if (at < 0) break;
                        if (seen[i]++ == 0) first[i] = start + from + at;
                        from += at + 1;
                    }
                }
                if (filled < buffer.Length) break;
                kept = overlap;
                chunk[^kept..].CopyTo(buffer);
                start += filled - kept;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        var found = new List<(Site, long, bool)>(_sites.Length);
        for (int i = 0; i < _sites.Length; i++)
        {
            var site = _sites[i];
            bool original = seen[2 * i] == 1, patched = seen[2 * i + 1] == 1;
            if (original && !patched) found.Add((site, first[2 * i] + site.Prefix.Length, false));
            else if (patched && !original) found.Add((site, first[2 * i + 1] + site.Prefix.Length, true));
            else return null;
        }
        return found;
    }

    public LibcefPatchState Classify(Stream file)
    {
        if (Locate(file) is not { } sites) return LibcefPatchState.Unsupported;
        return sites.All(s => s.Patched) ? LibcefPatchState.Patched
            : sites.All(s => !s.Patched) ? LibcefPatchState.Original : LibcefPatchState.Partial;
    }

    // Writes the patched or the original Core where it is not there yet, and confirms the result. A site that already holds
    // the wanted bytes is not written; a file no profile fits is left alone.
    public void Write(Stream file, bool patch)
    {
        if (Locate(file) is not { } sites) throw new InvalidDataException("libcef.dll is not a known build.");
        bool written = false;
        foreach (var (site, offset, patched) in sites)
        {
            if (patched == patch) continue;
            file.Position = offset; file.Write(patch ? site.Patched : site.Original); written = true;
        }
        if (!written) return;
        file.Flush();
        if (Classify(file) != (patch ? LibcefPatchState.Patched : LibcefPatchState.Original))
            throw new IOException("libcef.dll did not keep the written bytes.");
    }
}
