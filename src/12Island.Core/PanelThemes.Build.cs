using System.Runtime.CompilerServices;

namespace TwelveIsland.Core;

// From a theme file to a palette: the base chain is merged, the effects are fitted to their ranges, and every color with a
// contrast minimum is brought up to it.
public static partial class PanelThemes
{
    // Built-in palettes never change. A user file is resolved once per reading: an edited file is read again into a new entry.
    // A theme with a night look resolves to two palettes, kept side by side.
    private static readonly Dictionary<(string, bool), PanelPalette> ResolvedBuiltIns = [];
    private static readonly ConditionalWeakTable<PanelThemeInfo, PanelPalette> ResolvedFiles = new(), ResolvedNights = new();
    // The day look. The one-argument form stays so the method group still fits a Select (themes.Select(PanelThemes.Resolve)).
    public static PanelPalette Resolve(PanelThemeInfo theme) => Resolve(theme, false);
    public static PanelPalette Resolve(PanelThemeInfo theme, bool night)
    {
        night &= theme.File.Night is not null;
        var files = night ? ResolvedNights : ResolvedFiles;
        if (theme.BuiltIn ? ResolvedBuiltIns.TryGetValue((theme.Id, night), out var cached) : files.TryGetValue(theme, out cached)) return cached;
        var palette = Build(theme.Id, theme.File, night);
        if (theme.BuiltIn) ResolvedBuiltIns[(theme.Id, night)] = palette; else files.AddOrUpdate(theme, palette);
        return palette;
    }

    // night lays the file's night look over it, when it has one.
    public static PanelPalette Build(string id, PanelThemeFile file, bool night = false)
    {
        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var apps = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var effects = new PanelThemeEffects();
        PanelThemeClock? clock = null;
        var chain = BaseChain(file);
        night &= file.Night is not null;
        if (night) chain.Add(file.Night!);
        foreach (var layer in chain)
        {
            foreach (var (key, value) in layer.Colors ?? [])
                if (Tokens.FirstOrDefault(token => string.Equals(token.Key, key, StringComparison.OrdinalIgnoreCase)) is { } token && ThemeColor.TryParse(value, out var color)) colors[token.Key] = color;
            foreach (var (key, value) in layer.Apps ?? [])
                if (AppKeys.FirstOrDefault(app => string.Equals(app, key, StringComparison.OrdinalIgnoreCase)) is { } app && ThemeColor.TryParse(value, out var color)) apps[app] = color;
            if (layer.Clock is { } layerClock && ClockProblem(layerClock) is null) clock = MergeClock(clock, layerClock);
            if (layer.Effects is { } layerEffects) MergeEffects(effects, layerEffects);
        }
        // The dark theme sets every token, so a missing one only happens if the embedded file is broken.
        foreach (var token in Tokens) if (!colors.ContainsKey(token.Key)) colors[token.Key] = token.Key == "surface" ? ThemeColor.Black : ThemeColor.White;
        var writtenColors = new Dictionary<string, ThemeColor>(colors, StringComparer.Ordinal);
        var writtenApps = new Dictionary<string, ThemeColor>(apps, StringComparer.Ordinal);
        // The surface is drawn opaque, as a translucent one would let the desktop through behind the text. A tooltip is a window of
        // its own and the seek bubble floats over lyrics and times, so both are drawn as they look over the surface.
        var surface = colors["surface"] = colors["surface"].Over(ThemeColor.Black);
        colors["tooltip"] = colors["tooltip"].Over(surface);
        colors["bubble"] = colors["bubble"].Over(surface);
        var values = EffectValues(effects);
        List<ThemeColor> surfaces = [surface, .. values.SurfaceGradient.Select(stop => stop.Over(surface)), .. TextureStops(values, colors, surface), .. DecoratedStops(values, colors, surface)];

        var adjustments = new List<PanelThemeAdjustment>();
        var popup = colors["popup"].Over(surface);
        foreach (var token in Tokens.Where(token => token.Minimum > 0))
        {
            List<ThemeColor> against = token.Against switch
            {
                PanelThemeBackdrop.Surface => surfaces,
                PanelThemeBackdrop.SurfaceAndPopup => [.. surfaces, popup],
                // The chosen date sits on the accent in a pop-up, a notice's main button on the accent over the surface.
                PanelThemeBackdrop.Accent => [colors["accent"].Over(popup), colors["accent"].Over(surface)],
                PanelThemeBackdrop.Indicator => [colors["indicator"].Over(ThemeColor.Black)],
                _ => []
            };
            var needs = against.Select(background => new ContrastNeed(background, token.Minimum)).ToList();
            // Text also stands on fills: the primary text on the selected segment, a pressed button, an input field, the date range,
            // tooltips and the seek time bubble; the secondary text on the segment strip and on hover. Those need the body-text minimum.
            List<ThemeColor> fills = token.Key switch
            {
                "textPrimary" => [colors["fillSelected"].Over(surface), colors["pressed"].Over(surface), colors["input"].Over(popup), colors["rangeFill"].Over(popup),
                    colors["tooltip"], colors["bubble"]],
                "textSecondary" => [colors["fill"].Over(surface), colors["hover"].Over(surface)],
                _ => []
            };
            needs.AddRange(fills.Select(fill => new ContrastNeed(fill, TextContrast)));
            Ensure(colors, token.Key, needs, adjustments);
        }
        // App colors are opaque, as the color picker and the settings store them, and read on every surface stop. The warning and
        // critical colors are also text ("quota nearly used up"), so they are held to the body-text minimum.
        var graphic = surfaces.Select(background => new ContrastNeed(background, GraphicContrast)).ToList();
        var text = surfaces.Select(background => new ContrastNeed(background, LevelContrast)).ToList();
        foreach (var key in apps.Keys.ToList())
        {
            apps[key] = apps[key].Over(surface);
            Ensure(apps, key, key is "warning" or "critical" ? text : graphic, adjustments);
        }
        // A theme's own picker colors are offered as the panel draws them, like the ones derived from its palette.
        var swatches = file.Swatches?.Select(value => ThemeColor.TryParse(value, out var color) ? color.Over(surface).EnsureContrast(graphic) : (ThemeColor?)null)
            .OfType<ThemeColor>().Take(12).ToList();
        return new PanelPalette(id, colors, apps, values, adjustments, clock, swatches, writtenColors, writtenApps) { IsNight = night };
    }

    // A texture's lightest and darkest squares (PanelTheme.Texture draws them in the primary text color and in black, at most the
    // texture's opacity), which text must read on as on the surface.
    internal static IEnumerable<ThemeColor> TextureStops(PanelThemeEffectValues values, IReadOnlyDictionary<string, ThemeColor> colors, ThemeColor surface)
    {
        if (values.Texture <= 0) yield break;
        var light = colors["textPrimary"]; var alpha = (byte)Math.Round(255 * values.Texture);
        yield return new ThemeColor(alpha, light.R, light.G, light.B).Over(surface);
        yield return new ThemeColor(alpha, 0, 0, 0).Over(surface);
    }

    // The motifs' colors, and the texture's squares over each of them: the texture is drawn after the motifs, over them.
    internal static IEnumerable<ThemeColor> DecoratedStops(PanelThemeEffectValues values, IReadOnlyDictionary<string, ThemeColor> colors, ThemeColor surface)
    {
        foreach (var stop in DecorStops(values, surface))
        {
            yield return stop;
            foreach (var square in TextureStops(values, colors, stop)) yield return square;
        }
    }

    // What the motifs lay under the content: the sea (the surface where the horizon puts it, from its color at the horizon to its
    // deep color at the bottom) and the line of light along its top, and each motif over what it is drawn on.
    internal static IEnumerable<ThemeColor> DecorStops(PanelThemeEffectValues values, ThemeColor surface)
    {
        if (values.Decor is not { } decor) yield break;
        // The sea lies over the gradient's lower part, so it is measured over every stop of it.
        var surfaces = new List<ThemeColor> { surface };
        surfaces.AddRange(values.SurfaceGradient.Select(stop => stop.Over(surface)));
        var grounds = new List<ThemeColor>(surfaces);
        if (decor.Horizon is { } horizon)
            foreach (var ground in surfaces)
            {
                foreach (var water in new[] { horizon.Sea, horizon.Deep }) { var sea = water.Over(ground); grounds.Add(sea); yield return sea; }
                if (horizon.Line is { } line)
                    foreach (var lit in new[] { line.Over(ground), line.Over(horizon.Sea.Over(ground)) }) { grounds.Add(lit); yield return lit; }
            }
        // The caustics over everything from the horizon down: the sea, and the sky where the sea's top is still fading in
        // (over the whole surface without a horizon). The palm over the sky above the horizon, where it is clipped.
        if (decor.Caustics is { } caustics)
            foreach (var ground in grounds) yield return caustics.Over(ground);
        if (decor.Palm is { } palm)
            foreach (var ground in surfaces)
            {
                yield return palm.Over(ground);
                // Without a horizon the caustics' light lies on the same surface, so the shade falls over it too.
                if (decor.Horizon is null && decor.Caustics is { } light) yield return palm.Over(light.Over(ground));
            }
    }

    // The file and its base themes, from the dark theme up to the file. The length bound guards against a cycle.
    private static List<PanelThemeFile> BaseChain(PanelThemeFile file)
    {
        var chain = new List<PanelThemeFile> { file };
        var baseId = NormalizeId(file.Base);
        while (chain.Count <= 8)
        {
            if (baseId.Length == 0) baseId = Dark;
            if (BuiltIn(baseId) is not { } parent || chain.Contains(parent.File)) break;
            chain.Add(parent.File);
            if (baseId == Dark) break;
            baseId = NormalizeId(parent.File.Base);
        }
        chain.Reverse();
        return chain;
    }

    // Field by field, like the colors: a file that names only a style keeps its base's clock color. Colors are kept opaque, in the
    // "#RRGGBB" form the settings store, so they can be compared with what the settings hold.
    private static PanelThemeClock MergeClock(PanelThemeClock? inherited, PanelThemeClock layer) => new()
    {
        Style = layer.Style.Length > 0 ? layer.Style : inherited?.Style ?? "",
        Color = layer.Color.Length > 0 ? layer.Color : inherited?.Color ?? "",
        Custom = layer.Custom.Length > 0 ? ThemeColor.Opaque(layer.Custom) : inherited?.Custom ?? "",
        Gradient = layer.Gradient is { } own ? [.. own.Select(ThemeColor.Opaque)] : inherited?.Gradient,
        Palette = layer.Palette is null ? inherited?.Palette : new Dictionary<string, string>((inherited?.Palette ?? []).Concat(layer.Palette.Select(pair => KeyValuePair.Create(pair.Key, ThemeColor.Opaque(pair.Value))))
            .GroupBy(pair => pair.Key).Select(group => group.Last()), StringComparer.OrdinalIgnoreCase),
        Gradients = layer.Gradients?.Select(g => new PanelThemeGradient { Name = g.Name, NameEn = g.NameEn, Stops = [.. g.Stops.Select(ThemeColor.Opaque)] }).ToList() ?? inherited?.Gradients
    };

    private static void MergeEffects(PanelThemeEffects merged, PanelThemeEffects layer)
    {
        if (layer.SurfaceGradient is not null) merged.SurfaceGradient = layer.SurfaceGradient;
        if (layer.RimGradient is not null) merged.RimGradient = layer.RimGradient;
        merged.RimWidth = layer.RimWidth ?? merged.RimWidth;
        merged.ShadowBlur = layer.ShadowBlur ?? merged.ShadowBlur;
        merged.ShadowDepth = layer.ShadowDepth ?? merged.ShadowDepth;
        merged.ShadowOpacity = layer.ShadowOpacity ?? merged.ShadowOpacity;
        merged.Scanlines = layer.Scanlines ?? merged.Scanlines;
        merged.Pixel = layer.Pixel ?? merged.Pixel;
        merged.Texture = layer.Texture ?? merged.Texture;
        if (layer.Decor is { } decor)
        {
            // Field by field, into new parts, so a layer that moves only the horizon keeps the sea it inherits and the base's own
            // parts are never changed.
            var below = merged.Decor;
            merged.Decor = new PanelThemeDecor
            {
                Horizon = decor.Horizon is { } h ? new PanelThemeHorizon { At = h.At ?? below?.Horizon?.At, Sea = h.Sea ?? below?.Horizon?.Sea, Line = h.Line ?? below?.Horizon?.Line } : below?.Horizon,
                Caustics = decor.Caustics is { } c ? new PanelThemeMotif { Color = c.Color ?? below?.Caustics?.Color } : below?.Caustics,
                Palm = decor.Palm is { } p ? new PanelThemeMotif { Color = p.Color ?? below?.Palm?.Color } : below?.Palm
            };
        }
    }

    // The effects in their ranges; a gradient needs two stops and keeps at most eight.
    private static PanelThemeEffectValues EffectValues(PanelThemeEffects effects)
    {
        var surfaceGradient = Stops(effects.SurfaceGradient);
        var rimGradient = Stops(effects.RimGradient);
        double Value(string key) { var (min, max, fallback) = EffectRange(key); return Fit(Effect(effects, key), min, max, fallback); }
        // The pixel size is whole DIP, a half rounding up as the device pixels do (PixelStairs.Step). The texture is part of
        // pixel art: without blocks there is nothing to mottle.
        double pixel = Math.Round(Value("pixel"), MidpointRounding.AwayFromZero);
        return new(surfaceGradient.Count >= 2 ? surfaceGradient : [], rimGradient.Count >= 2 ? rimGradient : [],
            Value("rimWidth"), Value("shadowBlur"), Value("shadowDepth"), Value("shadowOpacity"), Value("scanlines"), pixel, pixel > 0 ? Value("texture") : 0,
            DecorValues(effects.Decor));
    }

    // The motifs in their ranges; a part without a color it can use is left out. The caustics and the palm stay faint (their
    // alpha at most 0.2 and 0.12), so they read as light and shade on the surface, never as pictures over the content.
    private static PanelDecor? DecorValues(PanelThemeDecor? decor)
    {
        if (decor is null) return null;
        static ThemeColor? Color(string? text, double maxAlpha = 1)
        {
            if (text is null || !ThemeColor.TryParse(text, out var color) || color.A == 0) return null;
            return color with { A = (byte)Math.Min(color.A, Math.Round(255 * maxAlpha)) };
        }
        var horizon = decor.Horizon is { Sea: var seaText } h && Color(seaText) is { } sea ? new PanelHorizon(Fit(h.At, .3, .9, .62), sea, Color(h.Line)) : null;
        var values = new PanelDecor(horizon, Color(decor.Caustics?.Color, .2), Color(decor.Palm?.Color, .12));
        return values.Any ? values : null;
    }

    // The numeric effects by their names in the file, with their ranges and the values used when no theme sets them.
    public static readonly IReadOnlyList<string> EffectKeys = ["rimWidth", "shadowBlur", "shadowDepth", "shadowOpacity", "scanlines", "pixel", "texture"];
    public static (double Min, double Max, double Fallback) EffectRange(string key) => key switch
    {
        "rimWidth" => (0, 3, 1), "shadowBlur" => (0, 40, 14), "shadowDepth" => (0, 12, 4), "shadowOpacity" => (0, 1, .28), "scanlines" => (0, .12, 0),
        "pixel" => (0, 4, 0), "texture" => (0, .12, 0),
        _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    public static double? Effect(PanelThemeEffects? effects, string key) => key switch
    {
        "rimWidth" => effects?.RimWidth, "shadowBlur" => effects?.ShadowBlur, "shadowDepth" => effects?.ShadowDepth,
        "shadowOpacity" => effects?.ShadowOpacity, "scanlines" => effects?.Scanlines, "pixel" => effects?.Pixel, "texture" => effects?.Texture, _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    public static double Effect(PanelThemeEffectValues values, string key) => key switch
    {
        "rimWidth" => values.RimWidth, "shadowBlur" => values.ShadowBlur, "shadowDepth" => values.ShadowDepth,
        "shadowOpacity" => values.ShadowOpacity, "scanlines" => values.Scanlines, "pixel" => values.Pixel, "texture" => values.Texture, _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    private static void SetEffect(PanelThemeEffects effects, string key, double value)
    {
        switch (key)
        {
            case "rimWidth": effects.RimWidth = value; break;
            case "shadowBlur": effects.ShadowBlur = value; break;
            case "shadowDepth": effects.ShadowDepth = value; break;
            case "shadowOpacity": effects.ShadowOpacity = value; break;
            case "scanlines": effects.Scanlines = value; break;
            case "pixel": effects.Pixel = value; break;
            case "texture": effects.Texture = value; break;
            default: throw new ArgumentException("Unknown effect: " + key, nameof(key));
        }
    }
    private static List<ThemeColor> Stops(List<string>? values) => values is null ? [] : values.Select(value => ThemeColor.TryParse(value, out var c) ? c : (ThemeColor?)null).OfType<ThemeColor>().Take(8).ToList();
    private static double Fit(double? value, double min, double max, double fallback) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    // Brings one color up to every need and records the change, with the ratio of the need it fell furthest short of.
    private static void Ensure(Dictionary<string, ThemeColor> colors, string key, List<ContrastNeed> needs, List<PanelThemeAdjustment> adjustments)
    {
        var before = colors[key];
        var after = before.EnsureContrast(needs);
        if (after == before) return;
        var worst = needs.MinBy(need => ThemeColor.Contrast(before, need.Background) / need.Minimum);
        adjustments.Add(new(key, before.ToString(), after.ToString(), ThemeColor.Contrast(before, worst.Background), worst.Minimum, after.ContrastScore(needs) >= 1));
        colors[key] = after;
    }
}
