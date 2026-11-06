using System.Globalization;
using System.Text.Json;

namespace TwelveIsland.Core;

// Changes made to a theme on the settings page and not yet saved: token colors, app colors and effects by their keys in the file.
// The settings page lays them over the theme's file to show what saving would give (PanelThemes.ApplyDraft); nothing reaches the
// disk, and nothing changes on the panel, until the user saves (PanelThemes.WriteDraft). A change back to the file's own value is
// dropped.
public sealed class PanelThemeDraft(string themeId)
{
    public string ThemeId { get; } = themeId;
    public Dictionary<string, string> Colors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Apps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Effects { get; } = new(StringComparer.Ordinal);
    // Counts the changes, so a palette built from the draft can tell that it is out of date.
    public int Version { get; private set; }
    public bool IsEmpty => Colors.Count == 0 && Apps.Count == 0 && Effects.Count == 0;
    public int Count => Colors.Count + Apps.Count + Effects.Count;

    // null takes the change back.
    public void SetColor(PanelThemeFile file, string key, ThemeColor? value) { Set(Colors, Own(file.Colors, key), key, value); Version++; }
    public void SetApp(PanelThemeFile file, string key, ThemeColor? value) { Set(Apps, Own(file.Apps, key), key, value); Version++; }
    public void SetEffect(PanelThemeFile file, string key, double? value)
    {
        if (value is not { } number || PanelThemes.Effect(file.Effects, key) is { } own && Math.Abs(own - number) < 1e-9) Effects.Remove(key);
        else Effects[key] = number;
        Version++;
    }

    private static string? Own(Dictionary<string, string>? values, string key)
        => values?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    private static void Set(Dictionary<string, string> changes, string? own, string key, ThemeColor? value)
    {
        if (value is not { } color || ThemeColor.TryParse(own, out var written) && written == color) changes.Remove(key);
        else changes[key] = color.ToString();
    }
}

// Theme drafts: laid over a file, and written into a file's text.
public static partial class PanelThemes
{
    // A copy of the file with the draft's changes in it.
    public static PanelThemeFile ApplyDraft(PanelThemeFile file, PanelThemeDraft? draft)
    {
        var copy = Parse(JsonSerializer.Serialize(file, Json));
        if (draft is null || draft.IsEmpty) return copy;
        copy.Colors = Overlay(copy.Colors, draft.Colors);
        copy.Apps = Overlay(copy.Apps, draft.Apps);
        if (draft.Effects.Count > 0)
        {
            copy.Effects ??= new PanelThemeEffects();
            foreach (var (key, value) in draft.Effects) SetEffect(copy.Effects, key, value);
        }
        return copy;

        // Keys are read without regard to case, so a key the file writes in another case is replaced, not kept beside.
        static Dictionary<string, string>? Overlay(Dictionary<string, string>? values, Dictionary<string, string> changes)
        {
            if (changes.Count == 0) return values;
            var merged = new Dictionary<string, string>(values ?? [], StringComparer.Ordinal);
            foreach (var (key, value) in changes)
            {
                foreach (var same in merged.Keys.Where(other => string.Equals(other, key, StringComparison.OrdinalIgnoreCase)).ToList()) merged.Remove(same);
                merged[key] = value;
            }
            return merged;
        }
    }

    // The text of a theme file with the draft's changes written into it. Only the values that changed are touched: comments,
    // layout and everything else stay as they were. A key the file does not have yet goes first in its object.
    public static string WriteDraft(string text, PanelThemeDraft draft)
    {
        foreach (var (key, value) in draft.Colors) text = SetMember(text, ["colors", key], Quote(value));
        foreach (var (key, value) in draft.Apps) text = SetMember(text, ["apps", key], Quote(value));
        foreach (var (key, value) in draft.Effects) text = SetMember(text, ["effects", key], value.ToString("0.###", CultureInfo.InvariantCulture));
        // A result that cannot be read back is never written.
        Parse(text);
        return text;
    }

    // Saves a theme's draft as a new theme in the folder (a built-in theme cannot change, and a theme of the user's can be saved
    // either way): the theme as it was, written out in full (CreateFromTemplate), with the draft written over it. Returns the new
    // file's path.
    public static string SaveDraftAsNew(string folder, PanelThemeInfo theme, PanelThemeDraft draft)
    {
        // The day look, with the night look and its hours carried along (the draft edits the day look).
        var path = CreateFromTemplate(folder, Resolve(theme), theme.BuiltIn ? theme.Id : null, theme.File);
        try { File.WriteAllText(path, WriteDraft(File.ReadAllText(path), draft)); }
        catch { File.Delete(path); throw; }
        return path;
    }

    // Sets the member at path (a key in each nested object, from the outermost) to value, the value's JSON text. A missing object
    // on the way is made; a member that holds something else than an object where the path goes on is replaced by one.
    private static string SetMember(string text, IReadOnlyList<string> path, string value)
    {
        int open = SkipSpace(text, 0);
        if (open >= text.Length || text[open] != '{') throw new JsonException(Loc.T("主题文件不是 JSON 对象", "The theme file is not a JSON object"));
        for (int depth = 0; ; depth++)
        {
            var (members, close) = Members(text, open);
            bool last = depth == path.Count - 1;
            int at = members.FindIndex(member => string.Equals(member.Key, path[depth], StringComparison.OrdinalIgnoreCase));
            if (at < 0) return InsertMember(text, open, members, close, $"{Quote(path[depth])}: {Nested(path, depth + 1, value)}");
            var (_, _, start, end) = members[at];
            if (last) return text[..start] + value + text[end..];
            if (text[start] != '{') return text[..start] + Nested(path, depth + 1, value) + text[end..];
            open = start;
        }

        static string Nested(IReadOnlyList<string> path, int from, string value)
            => from == path.Count ? value : $"{{ {Quote(path[from])}: {Nested(path, from + 1, value)} }}";
    }

    // A member put first in the object at open: on a line of its own when the first member has one, else on the same line.
    private static string InsertMember(string text, int open, List<(string Key, int KeyStart, int ValueStart, int ValueEnd)> members, int close, string member)
    {
        if (members.Count == 0) return text.Insert(open + 1, " " + member + (close == open + 1 ? " " : ""));
        int first = members[0].KeyStart;
        int lineStart = text.LastIndexOf('\n', first - 1) + 1;
        var indent = text[lineStart..first];
        return indent.All(char.IsWhiteSpace) && lineStart > open
            ? text.Insert(first, member + "," + (text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + indent)
            : text.Insert(first, member + ", ");
    }

    // The members of the object whose opening brace is at open, each with where its key starts and its value's span, and the
    // index of the object's closing brace. Comments and trailing commas are allowed, as the theme reader allows them.
    private static (List<(string Key, int KeyStart, int ValueStart, int ValueEnd)> Members, int Close) Members(string text, int open)
    {
        var members = new List<(string, int, int, int)>();
        int i = SkipSpace(text, open + 1);
        while (i < text.Length && text[i] != '}')
        {
            if (text[i] == ',') { i = SkipSpace(text, i + 1); continue; }
            if (text[i] != '"') throw new JsonException(Loc.T("主题文件的格式有误", "The theme file is not well formed"));
            int keyEnd = StringEnd(text, i);
            int colon = SkipSpace(text, keyEnd + 1);
            if (colon >= text.Length || text[colon] != ':') throw new JsonException(Loc.T("主题文件的格式有误", "The theme file is not well formed"));
            int value = SkipSpace(text, colon + 1);
            if (value >= text.Length) throw new JsonException(Loc.T("主题文件的格式有误", "The theme file is not well formed"));
            int end = ValueEnd(text, value);
            members.Add((text[(i + 1)..keyEnd], i, value, end));
            i = SkipSpace(text, end);
        }
        if (i >= text.Length) throw new JsonException(Loc.T("主题文件的格式有误", "The theme file is not well formed"));
        return (members, i);
    }

    // The index just past the value that starts at i.
    private static int ValueEnd(string text, int i)
    {
        if (text[i] == '"') return StringEnd(text, i) + 1;
        if (text[i] is '{' or '[')
        {
            int depth = 0;
            while (true)
            {
                i = SkipSpace(text, i);
                if (i >= text.Length) return text.Length;
                char c = text[i];
                if (c == '"') { i = StringEnd(text, i) + 1; continue; }
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']' && --depth == 0) return i + 1;
                i++;
            }
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or '}' or ']' or '/')) i++;
        return i;
    }

    // The index of the first character from i that is neither white space nor inside a comment.
    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') { int end = text.IndexOf('\n', i); i = end < 0 ? text.Length : end + 1; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; continue; }
            break;
        }
        return i;
    }
}
