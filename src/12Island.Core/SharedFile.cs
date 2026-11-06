using System.Text.Json;

namespace TwelveIsland.Core;

// Files owned by other applications are read while those applications may be rewriting them.
// Allowing concurrent writes and replacement avoids a sharing violation that would read as a failed refresh.
public static class SharedFile
{
    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static JsonDocument Parse(string path)
    {
        using var stream = Open(path);
        return JsonDocument.Parse(stream);
    }
    public static JsonDocument? ReadJson(string path) => File.Exists(path) ? Parse(path) : null;
}
