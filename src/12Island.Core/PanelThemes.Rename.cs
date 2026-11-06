namespace TwelveIsland.Core;

// Renaming a theme file from settings: only the theme's own name changes, in both languages, and everything else in the file,
// its comments and layout included, stays as it was. The id stays too, so the theme keeps what was saved for it (ThemeChoices).
public static partial class PanelThemes
{
    public static void RenameFile(string path, string name) => File.WriteAllText(path, Renamed(File.ReadAllText(path), name));

    // The text with the top-level "name" and "nameEn" set to name. Only keys directly in the outermost object count: the clock's
    // gradients carry names of their own. A file without a name gets one after its opening brace.
    public static string Renamed(string text, string name)
    {
        var quoted = Quote(name.Trim());
        var replacements = new List<(int Start, int Length)>();
        int depth = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = text.IndexOf('\n', i) is var end and >= 0 ? end : text.Length; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length; continue; }
            if (c is '{' or '[') { depth++; i++; continue; }
            if (c is '}' or ']') { depth--; i++; continue; }
            if (c != '"') { i++; continue; }
            int close = StringEnd(text, i);
            if (depth == 1 && text.AsSpan(i, close - i + 1) is "\"name\"" or "\"nameEn\"")
            {
                // The key's value: past the colon and any space, a string.
                int value = close + 1;
                while (value < text.Length && (char.IsWhiteSpace(text[value]) || text[value] == ':')) value++;
                if (value < text.Length && text[value] == '"')
                {
                    int valueEnd = StringEnd(text, value);
                    replacements.Add((value, valueEnd - value + 1));
                    i = valueEnd + 1;
                    continue;
                }
            }
            i = close + 1;
        }
        if (replacements.Count == 0)
        {
            int brace = text.IndexOf('{');
            return brace < 0 ? text : text.Insert(brace + 1, $"\n  \"name\": {quoted},");
        }
        for (int r = replacements.Count - 1; r >= 0; r--) text = text.Remove(replacements[r].Start, replacements[r].Length).Insert(replacements[r].Start, quoted);
        return text;
    }

    // The index of the quote that closes the string opened at start; past the end for an unclosed string.
    private static int StringEnd(string text, int start)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        return text.Length - 1;
    }
}
