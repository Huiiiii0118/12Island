using System.Text.Json;

namespace TwelveIsland.Core;

// Theme files in the user's folder, and the checks that decide whether a file can be used.
public static partial class PanelThemes
{
    private const int MaximumFileBytes = 256 * 1024;

    // A theme's name on disk: the file name without ".json", or the folder's name for a theme.json in a subfolder.
    public static string FileName(string path) => Path.GetFileName(path) == "theme.json" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "" : Path.GetFileNameWithoutExtension(path);
    // The same as it is shown in a list: "folder/theme.json" or "name.json".
    public static string DisplayFileName(string path) => Path.GetFileName(path) == "theme.json" ? FileName(path) + "/theme.json" : Path.GetFileName(path);

    // Themes in the user's folder: "*.json" in the folder itself and "theme.json" in each subfolder, like an editor's
    // theme extensions. A file that cannot be used is still listed, with the reason, so the user can see why.
    public static IReadOnlyList<PanelThemeInfo> LoadFolder(string folder)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(folder)) return [];
            files = Directory.EnumerateFiles(folder, "*.json").Concat(Directory.EnumerateDirectories(folder).Select(dir => Path.Combine(dir, "theme.json")).Where(File.Exists))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        // Every file is read and checked before any id is handed out.
        var read = new List<(string Path, string Name, PanelThemeFile File, string Id, string? Error)>();
        foreach (var path in files)
        {
            string name = NormalizeId(FileName(path));
            try
            {
                if (new FileInfo(path).Length > MaximumFileBytes)
                    throw new InvalidDataException(Loc.T($"文件超过 {MaximumFileBytes / 1024} KB", $"The file is larger than {MaximumFileBytes / 1024} KB"));
                var file = Parse(File.ReadAllText(path));
                var id = NormalizeId(file.Id.Length > 0 ? file.Id : name);
                read.Add((path, name, file, id, FileProblem(file, id)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                read.Add((path, name, new PanelThemeFile { Name = Path.GetFileName(path) }, name, ex.Message));
            }
        }
        // A usable file takes its id; a file named after its id claims it before any other, so a copy made in Explorer
        // ("my-theme - Copy.json") never takes the id from the original. A later file with the same id is refused.
        var taken = BuiltInIndex.Select(entry => entry.Id).Append(SystemId).ToHashSet(StringComparer.Ordinal);
        // A file named after its id keeps that id while it cannot be used (being saved, say), so a copy does not take its place.
        foreach (var entry in read) if (entry.Error is not null && entry.Name == entry.Id && entry.Id.Length > 0) taken.Add(entry.Id);
        var owner = new bool[read.Count];
        foreach (int i in Enumerable.Range(0, read.Count).OrderBy(i => read[i].Name == read[i].Id ? 0 : 1).ThenBy(i => i))
            owner[i] = read[i].Error is null && taken.Add(read[i].Id);
        var found = new List<PanelThemeInfo>();
        for (int i = 0; i < read.Count; i++)
        {
            var (path, name, file, id, error) = read[i];
            if (error is null && !owner[i]) error = Loc.T($"主题 ID“{id}”与已有主题重复", $"The theme id \"{id}\" is already used by another theme");
            // An unusable entry is listed under an id no theme can have, unique to its file.
            found.Add(new PanelThemeInfo(error is null ? id : "file:" + (id.Length > 0 ? id : name) + ":" + i, PanelThemeKind.Custom, file, path, error));
        }
        return found;
    }

    // Themes of the user's in the order they were added: by the time their files were created, then by name. A file that cannot
    // be read for its time goes last.
    public static IEnumerable<PanelThemeInfo> InAddedOrder(IEnumerable<PanelThemeInfo> themes)
    {
        static DateTime Created(string? path)
        {
            try { return path is not null && File.Exists(path) ? File.GetCreationTimeUtc(path) : DateTime.MaxValue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.MaxValue; }
        }
        return themes.Select(theme => (Theme: theme, Created: Created(theme.Path))).OrderBy(entry => entry.Created)
            .ThenBy(entry => entry.Theme.Path, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Theme);
    }

    // Why a theme file cannot be used, or null. Colors that only need adjusting are not problems (see Build).
    private static string? FileProblem(PanelThemeFile file, string id)
    {
        // Letters outside a-z (Chinese, say) would vanish from the id, or two ids would become one; the file says so instead.
        if (file.Id.Any(ch => ch > 127))
            return Loc.T($"主题 ID“{file.Id}”只能使用小写字母 a–z、数字和 - _ .（名称可以用中文，写在 name 中）",
                $"The theme id \"{file.Id}\" may only use the letters a–z, digits, - _ and . (a Chinese name goes in \"name\")");
        if (id.Length == 0) return Loc.T("缺少主题 ID", "The theme has no id");
        if (file.FormatVersion > 1) return Loc.T("需要更新版本的 12 island", "This theme needs a newer 12 island");
        var baseId = NormalizeId(file.Base);
        if (baseId.Length > 0 && !IsBuiltIn(baseId)) return Loc.T($"基础主题“{file.Base}”不存在", $"The base theme \"{file.Base}\" does not exist");
        if (LayerProblem(file) is { } problem) return problem;
        if (file.Night is { } night && LayerProblem(night) is { } nightProblem) return Loc.T("夜间外观：", "Night look: ") + nightProblem;
        foreach (var time in new[] { file.Daylight?.Day, file.Daylight?.Night })
            if (time is { Length: > 0 } && ParseTimeOfDay(time) is null) return Loc.T($"时间“{time}”不是 HH:mm", $"The time \"{time}\" is not HH:mm");
        return null;
    }

    // What makes the colors, effects or clock of a file, or of its night look, unusable.
    private static string? LayerProblem(PanelThemeFile file)
    {
        foreach (var (key, value) in file.Colors ?? [])
        {
            if (Tokens.All(token => !string.Equals(token.Key, key, StringComparison.OrdinalIgnoreCase))) return Loc.T($"未知的颜色项“{key}”", $"Unknown color \"{key}\"");
            if (!ThemeColor.TryParse(value, out _)) return Loc.T($"颜色项“{key}”的值“{value}”不是 #RRGGBB 或 #AARRGGBB", $"The color \"{key}\" has the value \"{value}\", which is not #RRGGBB or #AARRGGBB");
        }
        foreach (var (key, value) in file.Apps ?? [])
        {
            if (AppKeys.All(app => !string.Equals(app, key, StringComparison.OrdinalIgnoreCase))) return Loc.T($"未知的应用颜色“{key}”", $"Unknown app color \"{key}\"");
            if (!ThemeColor.TryParse(value, out _)) return Loc.T($"应用颜色“{key}”的值“{value}”无效", $"The app color \"{key}\" has the invalid value \"{value}\"");
        }
        if (file.Swatches is { } swatches && (swatches.Count > 12 || swatches.Any(value => !ThemeColor.TryParse(value, out _))))
            return Loc.T("颜色选择器预设色最多十二个，且必须是有效颜色", "The picker swatches must be at most twelve valid colors");
        var decor = file.Effects?.Decor;
        var decorColors = new[] { decor?.Horizon?.Sea, decor?.Horizon?.Line, decor?.Caustics?.Color, decor?.Palm?.Color }.OfType<string>();
        foreach (var value in (file.Effects?.SurfaceGradient ?? []).Concat(file.Effects?.RimGradient ?? []).Concat(decorColors))
            if (!ThemeColor.TryParse(value, out _)) return Loc.T($"效果中的颜色“{value}”无效", $"The effect color \"{value}\" is invalid");
        return file.Clock is { } clock ? ClockProblem(clock) : null;
    }

    private static string? ClockProblem(PanelThemeClock clock)
    {
        if (clock.Style.Length > 0 && !Enum.TryParse<ClockStyle>(clock.Style, true, out _)) return Loc.T($"未知的时间样式“{clock.Style}”", $"Unknown clock style \"{clock.Style}\"");
        if (clock.Color.Length > 0 && ParseClockColor(clock.Color) is null) return Loc.T($"未知的时间颜色“{clock.Color}”", $"Unknown clock color \"{clock.Color}\"");
        if (clock.Custom.Length > 0 && !ThemeColor.TryParse(clock.Custom, out _)) return Loc.T($"时间自定义颜色“{clock.Custom}”无效", $"The custom clock color \"{clock.Custom}\" is invalid");
        if (clock.Gradient is { } stops && (stops.Count != 3 || stops.Any(stop => !ThemeColor.TryParse(stop, out _))))
            return Loc.T("时间渐变必须正好是三个有效颜色", "The clock gradient must be exactly three valid colors");
        foreach (var (key, value) in clock.Palette ?? [])
        {
            if (ClockColors.Swatches.All(color => ClockColors.Key(color) != key)) return Loc.T($"未知的时间预设色“{key}”", $"Unknown clock swatch \"{key}\"");
            if (!ThemeColor.TryParse(value, out _)) return Loc.T($"时间预设色“{key}”的值“{value}”无效", $"The clock swatch \"{key}\" has the invalid value \"{value}\"");
        }
        if (clock.Gradients is { } gradients && (gradients.Count is 0 or > 8 || gradients.Any(g => g.Stops is not { Count: 3 } || g.Stops.Any(stop => !ThemeColor.TryParse(stop, out _)))))
            return Loc.T("预设渐变需要一到八组，每组正好三个有效颜色", "The preset gradients must be one to eight, each exactly three valid colors");
        return null;
    }
}
