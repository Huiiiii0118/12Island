using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwelveIsland.Core;

// What a clipboard entry holds. Pictures keep their pixels in a PNG beside the index (ClipEntry.Image).
public enum ClipKind { Text, Image, Files }

// One thing copied to the clipboard. A text entry keeps the text itself; a picture entry keeps the size and the file name its
// pixels were written to; a files entry keeps the paths, one per line.
public sealed class ClipEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    // The PNG in the clipboard folder, for a picture.
    public string? Image { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    // A short fingerprint of a picture's pixels, so the same picture copied twice is one entry however it was copied.
    public string Hash { get; set; } = "";
    // The program in front when it was copied; shown under the text and used to tell apart pictures of the same size.
    public string Source { get; set; } = "";

    [JsonIgnore] public string FirstLine => FirstLineOf(Text);
    [JsonIgnore] public string Preview => Kind switch
    {
        ClipKind.Image => $"{Width} × {Height}",
        ClipKind.Files => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length is var count && count == 1
            ? System.IO.Path.GetFileName(Text.Trim()) : Loc.T($"{count} 个文件", Loc.Count(count, "file", "files")),
        _ => Note.Length > 0 ? Note : RestLines(Text)
    };
    [JsonIgnore] public bool HasNote => Note.Length > 0;

    public ClipEntry Copy() => new()
    {
        Id = Id, Kind = Kind, Text = Text, Note = Note, Favorite = Favorite, At = At, Image = Image, Width = Width, Height = Height, Hash = Hash, Source = Source
    };

    // The key two copies of the same content share: the text itself, the paths, or a picture's fingerprint.
    public string ContentKey => Kind switch
    {
        ClipKind.Image => Hash.Length > 0 ? $"image|{Hash}" : $"image|{Width}x{Height}|{Image}",
        _ => $"{Kind}|{Text}"
    };

    private static string FirstLineOf(string text)
    {
        var line = text.AsSpan();
        int end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        line = line.Trim();
        const int Most = 400;
        return line.Length <= Most ? line.ToString() : line[..Most].ToString() + "…";
    }
    private static string RestLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= 1 ? "" : string.Join(" ", lines.Skip(1).Take(2)).Trim();
    }
}

// The clipboard history: the entries in the order they were copied, newest first, and the pictures they point at. Kept as one
// index file in the app's data folder; written whenever an entry is added, changed or removed.
public sealed class ClipStore
{
    public const string IndexFile = "clipboard.json";
    public const string PictureFolder = "clipboard";
    private readonly string _folder;
    private readonly List<ClipEntry> _items = [];
    private int _limit = 100;

    public ClipStore(string folder, int limit)
    {
        _folder = folder; _limit = Math.Clamp(limit, 10, 1000);
        Directory.CreateDirectory(Pictures);
        Load();
    }

    public string Pictures => Path.Combine(_folder, PictureFolder);
    public IReadOnlyList<ClipEntry> Items => _items;
    public int Count => _items.Count;
    // Raised after any change, so an open page can draw the list again.
    public event Action? Changed;

    // The newest entry kept; null when the history is empty.
    public ClipEntry? Newest => _items.Count > 0 ? _items[0] : null;

    public void SetLimit(int limit)
    {
        _limit = Math.Clamp(limit, 10, 1000);
        if (Trim()) { Save(); Changed?.Invoke(); }
    }

    // Puts an entry at the top; the same content copied again moves up instead of appearing twice. Returns the entry kept.
    public ClipEntry Put(ClipEntry entry)
    {
        var existing = _items.FirstOrDefault(item => item.ContentKey == entry.ContentKey);
        if (existing is not null)
        {
            // A picture copied again may write a new file; the old one goes.
            if (entry.Image is { } fresh && existing.Image is { } old && fresh != old) DeletePicture(old);
            existing.At = entry.At; if (entry.Note.Length == 0) entry.Note = existing.Note;
            if (entry.Source.Length > 0) existing.Source = entry.Source;
            if (entry.Image is not null) existing.Image = entry.Image;
            _items.Remove(existing); _items.Insert(0, existing);
            Trim(); Save(); Changed?.Invoke();
            return existing;
        }
        _items.Insert(0, entry);
        Trim(); Save(); Changed?.Invoke();
        return entry;
    }

    public void Remove(Guid id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } entry) return;
        _items.Remove(entry);
        if (entry.Image is { } picture) DeletePicture(picture);
        Save(); Changed?.Invoke();
    }

    // Clears everything, or only the entries that are not kept as favourites.
    public void Clear(bool favoritesToo)
    {
        foreach (var entry in _items.Where(item => favoritesToo || !item.Favorite).ToList())
            if (entry.Image is { } picture) DeletePicture(picture);
        _items.RemoveAll(item => favoritesToo || !item.Favorite);
        Save(); Changed?.Invoke();
    }

    public void SetFavorite(Guid id, bool favorite)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } entry || entry.Favorite == favorite) return;
        entry.Favorite = favorite;
        Save(); Changed?.Invoke();
    }

    public void SetNote(Guid id, string note)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } entry) return;
        entry.Note = (note ?? "").Trim();
        Save(); Changed?.Invoke();
    }

    // An entry copied back: it moves to the top, as the most recent thing on the clipboard.
    public void Touch(Guid id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } entry) return;
        entry.At = DateTimeOffset.Now;
        _items.Remove(entry); _items.Insert(0, entry);
        Save(); Changed?.Invoke();
    }

    public string? PicturePath(ClipEntry entry) => entry.Image is { Length: > 0 } name ? Path.Combine(Pictures, name) : null;

    // The entries a search word keeps: text with the word anywhere in it, its note, or its source.
    public IEnumerable<ClipEntry> Search(string word, bool favoritesOnly)
    {
        word = word.Trim();
        foreach (var entry in _items)
        {
            if (favoritesOnly && !entry.Favorite) continue;
            if (word.Length == 0 || entry.Text.Contains(word, StringComparison.OrdinalIgnoreCase)
                || entry.Note.Contains(word, StringComparison.OrdinalIgnoreCase) || entry.Source.Contains(word, StringComparison.OrdinalIgnoreCase))
                yield return entry;
        }
    }

    // The entries beyond the limit go, favourites among them; their pictures go too.
    private bool Trim()
    {
        bool trimmed = false;
        while (_items.Count > _limit && _items.Count > 0)
        {
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            if (last.Image is { } picture) DeletePicture(picture);
            trimmed = true;
        }
        return trimmed;
    }

    private void DeletePicture(string name)
    {
        try { File.Delete(Path.Combine(Pictures, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(Path.Combine(_folder, IndexFile))) return;
            var items = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(Path.Combine(_folder, IndexFile)));
            if (items is not null) _items.AddRange(items.Where(item => item is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _items.Clear(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, IndexFile), JsonSerializer.Serialize(_items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
