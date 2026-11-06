using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

// The custom-theme template and new theme files made from it.
public static partial class PanelThemes
{
    // The placeholders of the template that a new file replaces.
    private const string TemplateId = "my-theme", TemplateName = "我的主题", TemplateNameEn = "My theme",
        TemplateDescription = "一句话描述这个主题。", TemplateDescriptionEn = "One sentence about this theme.";
    private static string? _template;
    // The template a new custom theme starts from: every token and effect, each with its meaning.
    public static string Template => _template ??= Resource(typeof(PanelThemes).Assembly.GetManifestResourceNames()
        .Single(name => name.EndsWith(".Templates.theme-template.json", StringComparison.Ordinal)));

    // Writes a new custom theme from the template into the folder and returns its path; the file name does not overwrite another.
    // With a starting palette the template's values are that palette's, so the new theme starts out looking like it.
    // source: the theme the start palette comes from; its night look and hours go into the new file too (FillTemplate).
    public static string CreateFromTemplate(string folder, PanelPalette? start = null, string? baseId = null, PanelThemeFile? source = null)
    {
        Directory.CreateDirectory(folder);
        // Neither the file nor the id may already exist: a renamed file can still carry the id "my-theme".
        var existing = LoadFolder(folder);
        var ids = existing.Select(theme => theme.Id).Concat(existing.Select(theme => NormalizeId(theme.File.Id))).ToHashSet(StringComparer.Ordinal);
        string path; int n = 1;
        do { path = Path.Combine(folder, n == 1 ? TemplateId + ".json" : $"{TemplateId}-{n}.json"); n++; }
        while (File.Exists(path) || Directory.Exists(Path.ChangeExtension(path, null)) || ids.Contains(Path.GetFileNameWithoutExtension(path)));
        File.WriteAllText(path, FillTemplate(Path.GetFileNameWithoutExtension(path), start, baseId, source));
        // Themes are listed in the order they were added (InAddedOrder). Windows gives a file made under the name of one deleted
        // moments before that file's creation time, so the time is set to now.
        File.SetCreationTimeUtc(path, DateTime.UtcNow);
        return path;
    }

    // The template with its id, name and base set, and, with a starting palette, every value taken from it. The replacements rely
    // on the template's layout: one "key": "#color" per match, and the palette, gradients and effect arrays as the template writes
    // them. The test "A theme made from another starts out identical" guards this.
    public static string FillTemplate(string id, PanelPalette? start = null, string? baseId = null, PanelThemeFile? source = null)
    {
        // "my-theme-2" is named "我的主题 2", so several new themes can be told apart on their cards; the descriptions start empty.
        string number = Regex.Match(id, @"-(\d+)$") is { Success: true } suffix ? " " + suffix.Groups[1].Value : "";
        var text = Template.Replace($"\"id\": \"{TemplateId}\"", $"\"id\": \"{id}\"", StringComparison.Ordinal)
            .Replace($"\"name\": \"{TemplateName}\"", $"\"name\": \"{TemplateName}{number}\"", StringComparison.Ordinal)
            .Replace($"\"nameEn\": \"{TemplateNameEn}\"", $"\"nameEn\": \"{TemplateNameEn}{number}\"", StringComparison.Ordinal)
            .Replace($"\"description\": \"{TemplateDescription}\"", "\"description\": \"\"", StringComparison.Ordinal)
            .Replace($"\"descriptionEn\": \"{TemplateDescriptionEn}\"", "\"descriptionEn\": \"\"", StringComparison.Ordinal);
        if (baseId is { Length: > 0 } && IsBuiltIn(baseId)) text = text.Replace($"\"base\": \"{Dark}\"", $"\"base\": \"{baseId}\"", StringComparison.Ordinal);
        if (start is null) return text;
        text = Regex.Replace(text, @"""(\w+)"": ""#[0-9A-Fa-f]{6,8}""", match => Tokens.Any(token => token.Key == match.Groups[1].Value) || AppKeys.Contains(match.Groups[1].Value)
            ? $"\"{match.Groups[1].Value}\": \"{TemplateColor(start, match.Groups[1].Value)}\"" : match.Value);
        if (start.Clock is { } clock) text = FillClock(text, start, clock);
        // A theme with its own picker colors writes them out; the template has the line commented out.
        if (start.HasOwnSwatches) text = Regex.Replace(text, @"// ""swatches"": \[[^\]]*\]", "\"swatches\": " + ColorList(start.Swatches));
        var effects = start.Effects;
        text = Regex.Replace(text, @"""surfaceGradient"": \[\]", "\"surfaceGradient\": " + ColorList(effects.SurfaceGradient));
        text = Regex.Replace(text, @"""rimGradient"": \[\]", "\"rimGradient\": " + ColorList(effects.RimGradient));
        // Every numeric effect, those the settings page cannot edit (pixel, texture) included, so a copy keeps them.
        foreach (var key in EffectKeys)
            text = Regex.Replace(text, $"\"{key}\": [0-9.]+", $"\"{key}\": {Effect(effects, key).ToString("0.###", CultureInfo.InvariantCulture)}");
        // The motifs follow the last numeric effect.
        if (effects.Decor is { } decor)
            text = new Regex(@"(""texture"": [0-9.]+)(,?)").Replace(text, match => match.Groups[1].Value + ", \"decor\": " + DecorJson(decor) + match.Groups[2].Value, 1);
        // A theme with a night look keeps it, and its hours, after everything the template lists (start is its day look).
        return source?.Night is { } night ? AppendNight(text, night, source.Daylight) : text;
    }

    // The night look as the theme file writes it: only what it sets, as "daylight" and "night" members at the end of the file.
    private static string AppendNight(string text, PanelThemeFile night, PanelThemeDaylight? daylight)
    {
        var layer = new PanelThemeFile { Colors = night.Colors, Apps = night.Apps, Effects = night.Effects, Clock = night.Clock };
        if (JsonSerializer.SerializeToNode(layer, Json) is not JsonObject json) return text;
        // Only what the layer sets: no format, empty strings (an id, a name, an unset clock style) or names made for display.
        json.Remove("formatVersion");
        static void Trim(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Trim(item); return; }
            if (node is not JsonObject members) return;
            foreach (var key in members.Where(pair => pair.Key == "displayName" || pair.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0)
                .Select(pair => pair.Key).ToList()) members.Remove(key);
            foreach (var (_, value) in members) Trim(value);
        }
        Trim(json);
        static string Indent(string member) => member.ReplaceLineEndings("\n  ");
        var members = new List<string>();
        if (daylight is { } hours) members.Add($"\"daylight\": {{ \"day\": {Quote(hours.Day)}, \"night\": {Quote(hours.Night)} }}");
        members.Add("\"night\": " + Indent(json.ToJsonString(Json)));
        int end = text.LastIndexOf('}');
        return end < 0 ? text : text[..end].TrimEnd() + ",\n  " + string.Join(",\n  ", members) + "\n}\n";
    }

    private static string DecorJson(PanelDecor decor)
    {
        var file = new PanelThemeDecor
        {
            Horizon = decor.Horizon is { } h ? new PanelThemeHorizon { At = h.At, Sea = h.Sea.ToString(), Line = h.Line?.ToString() } : null,
            Caustics = decor.Caustics is { } caustics ? new PanelThemeMotif { Color = caustics.ToString() } : null,
            Palm = decor.Palm is { } palm ? new PanelThemeMotif { Color = palm.ToString() } : null
        };
        // On one line, where the template has it.
        return Regex.Replace(JsonSerializer.Serialize(file, Json), @"\s*\n\s*", " ");
    }

    // Every palette has every token and, from the dark theme it is built on, every app color.
    private static string TemplateColor(PanelPalette start, string key) => start.Colors.TryGetValue(key, out var color) ? color.ToString() : start.Apps[key].ToString();

    private static string FillClock(string text, PanelPalette start, PanelThemeClock clock)
    {
        if (Enum.TryParse<ClockStyle>(clock.Style, true, out var style)) text = Regex.Replace(text, @"""style"": ""\w+""", $"\"style\": \"{style.ToString().ToLowerInvariant()}\"");
        if (ParseClockColor(clock.Color) is { } color) text = Regex.Replace(text, @"""color"": ""\w+""", $"\"color\": \"{ClockColorKey(color)}\"");
        if (clock.Custom.Length > 0) text = Regex.Replace(text, @"""custom"": ""#[0-9A-Fa-f]{6}""", $"\"custom\": \"{clock.Custom}\"");
        if (clock.Gradient is { Count: 3 } gradient) text = Regex.Replace(text, @"""gradient"": \[[^\]]*\]", "\"gradient\": " + ColorList(gradient));
        // The swatches and preset gradients, written out in full as the template lays them out.
        var swatches = string.Join(", ", ClockColors.Swatches.Select(swatch => $"\"{ClockColors.Key(swatch)}\": \"{start.ClockPreset(swatch)}\""));
        text = Regex.Replace(text, @"""palette"": \{[^}]*\}", "\"palette\": { " + swatches + " }");
        var presets = string.Join(",\n", start.ClockGradients.Select(g => $"      {{ \"name\": {Quote(g.Name)}, \"nameEn\": {Quote(g.NameEn)}, \"stops\": {ColorList(g.Stops)} }}"));
        return Regex.Replace(text, @"""gradients"": \[\s*\n(?:.*\n)*?    \]", "\"gradients\": [\n" + presets + "\n    ]");
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Json);
    private static string ColorList<T>(IEnumerable<T> colors) => "[" + string.Join(", ", colors.Select(color => $"\"{color}\"")) + "]";
}
