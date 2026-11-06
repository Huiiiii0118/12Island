using System.Globalization;

namespace TwelveIsland.Core;

// A color that missed its minimum and the color used in its place. Reached is false when no color of that hue could meet every
// minimum (backgrounds both light and dark); the color shown is then the one with the best worst case.
public sealed record PanelThemeAdjustment(string Key, string Before, string After, double Ratio, double Minimum, bool Reached = true)
{
    // One line for the settings page: the color, its contrast and what is shown instead.
    public string Describe()
    {
        var token = PanelThemes.Tokens.FirstOrDefault(token => token.Key == Key);
        string name = token is null ? Loc.T("应用颜色", "App color") : Loc.T(token.Chinese, token.English);
        string ratio = Ratio.ToString("0.0", CultureInfo.InvariantCulture), minimum = Minimum.ToString("0.#", CultureInfo.InvariantCulture);
        return Reached
            ? Loc.T($"{name}（{Key}）对比度 {ratio}:1，低于 {minimum}:1，已由 {Before} 调整为 {After}",
                $"{name} ({Key}) has a contrast of {ratio}:1, below {minimum}:1; {Before} is shown as {After}")
            : Loc.T($"{name}（{Key}）对比度 {ratio}:1，低于 {minimum}:1，且背景明暗不一、无法全部达标，已改用最接近的 {After}；请调整相关底色",
                $"{name} ({Key}) has a contrast of {ratio}:1, below {minimum}:1. Its backgrounds are both light and dark, so no color meets every minimum; the closest, {After}, is shown. Adjust the backgrounds");
    }
}

public sealed record PanelThemeEffectValues(IReadOnlyList<ThemeColor> SurfaceGradient, IReadOnlyList<ThemeColor> RimGradient, double RimWidth,
    double ShadowBlur, double ShadowDepth, double ShadowOpacity, double Scanlines, double Pixel = 0, double Texture = 0, PanelDecor? Decor = null);

// A theme's motifs (PanelThemeDecor, fitted); null parts are not drawn.
public sealed record PanelDecor(PanelHorizon? Horizon, ThemeColor? Caustics, ThemeColor? Palm)
{
    public bool Any => Horizon is not null || Caustics is not null || Palm is not null;
}
public sealed record PanelHorizon(double At, ThemeColor Sea, ThemeColor? Line)
{
    // The sea deepens from Sea at the horizon to this at the bottom, as open water does away from the light.
    public ThemeColor Deep => Sea with { R = (byte)(Sea.R * .9), G = (byte)(Sea.G * .92), B = (byte)(Sea.B * .95) };
}

// A theme ready to draw: every token has a color, every effect a value, and the contrast rules hold (see PanelThemes.Build).
public sealed class PanelPalette
{
    private readonly Dictionary<string, ThemeColor> _colors;
    private readonly Dictionary<string, ThemeColor> _apps;
    private readonly IReadOnlyList<ThemeColor>? _swatches;
    private IReadOnlyList<ThemeColor>? _derivedSwatches;
    private IReadOnlyList<PanelThemeGradient>? _clockGradients;

    internal PanelPalette(string id, Dictionary<string, ThemeColor> colors, Dictionary<string, ThemeColor> apps, PanelThemeEffectValues effects,
        IReadOnlyList<PanelThemeAdjustment> adjustments, PanelThemeClock? clock, IReadOnlyList<ThemeColor>? swatches,
        IReadOnlyDictionary<string, ThemeColor> writtenColors, IReadOnlyDictionary<string, ThemeColor> writtenApps)
    {
        Id = id; _colors = colors; _apps = apps; Effects = effects; Adjustments = adjustments; Clock = clock; _swatches = swatches;
        WrittenColors = writtenColors; WrittenApps = writtenApps;
        List<ThemeColor> plain = [Surface, .. effects.SurfaceGradient.Select(stop => stop.Over(Surface)), .. PanelThemes.TextureStops(effects, colors, Surface)];
        SurfaceStops = [.. plain, .. PanelThemes.DecoratedStops(effects, colors, Surface)];
        // Dark or light by the surface itself, its gradient and its texture; the motifs drawn into it do not change which.
        IsDark = plain.Average(stop => stop.Luminance) < .18;
    }

    public string Id { get; }

    public IReadOnlyDictionary<string, ThemeColor> Colors => _colors;
    public IReadOnlyDictionary<string, ThemeColor> Apps => _apps;
    // Every token and app color as the theme and its base themes write them, before any is made readable: what the theme
    // editor in settings shows and starts from.
    public IReadOnlyDictionary<string, ThemeColor> WrittenColors { get; }
    public IReadOnlyDictionary<string, ThemeColor> WrittenApps { get; }
    public ThemeColor this[string key] => _colors[key];
    public ThemeColor Surface => _colors["surface"];
    // The surface, every gradient stop and a texture's lightest and darkest squares: text must read on each of them.
    public IReadOnlyList<ThemeColor> SurfaceStops { get; }
    // The opacity the scroll bar's handle rests at while the pointer is elsewhere (App.xaml): faint, at ThumbRest, where the
    // thumb still reaches its 2:1 on every surface stop there, else just as much more as it takes.
    public double ThumbRestOpacity => _thumbRest ??= PanelThemes.ThumbRestOpacity(this["scrollThumb"], SurfaceStops);
    private double? _thumbRest;
    public bool IsDark { get; }

    public PanelThemeEffectValues Effects { get; }
    // A textured or decorated surface, like a gradient, has no single color to fade text into.
    public bool HasOverlay => Effects.SurfaceGradient.Count > 0 || Effects.Scanlines > 0 || Effects.Texture > 0 || Effects.Decor is not null;
    // Whether this is a theme's night look (PanelThemes.Resolve).
    public bool IsNight { get; init; }
    public IReadOnlyList<PanelThemeAdjustment> Adjustments { get; }

    // The theme's default color for an application column. The dark theme names every source, and every theme builds on it.
    public ThemeColor? AppDefault(ProviderId id) => _apps.TryGetValue(PanelThemes.AppKey(id), out var color) ? color : _apps.TryGetValue("other", out var other) ? other : null;
    // The color a column of this source has by default, as the panel draws it.
    public ThemeColor AppColor(ProviderId id) => Readable(AppDefault(id) ?? ThemeColor.Parse(AppPresets.ThemeColor(id)));
    public ThemeColor? WarningDefault => _apps.TryGetValue("warning", out var color) ? color : null;
    public ThemeColor? CriticalDefault => _apps.TryGetValue("critical", out var color) ? color : null;
    public ThemeColor WarningColor => Readable(WarningDefault ?? ThemeColor.Parse(AppPresets.WarningColor));

    // The clock the theme suggests, from the theme or its base themes, field by field. Its colors are opaque "#RRGGBB".
    public PanelThemeClock? Clock { get; }
    // A clock swatch's color in this theme: the theme's own amber, cyan, pink and lime, or the built-in ones. "White" is the
    // theme's clock color. Not yet made readable; ClockFace does that with the rest of the clock's colors.
    public ThemeColor ClockPreset(ClockColor color) => ClockColors.Swatches.Contains(color)
        ? Clock?.Palette?.TryGetValue(ClockColors.Key(color), out var hex) == true && ThemeColor.TryParse(hex, out var themed) ? themed : ThemeColor.Parse(ClockColors.Preset(color))
        : this["clock"];
    // The preset gradients of this theme, or the built-in ones.
    public IReadOnlyList<PanelThemeGradient> ClockGradients => _clockGradients ??= Clock?.Gradients is { Count: > 0 } own ? own
        : ClockColors.Gradients.Select(gradient => new PanelThemeGradient { Name = gradient.Name, Stops = [.. gradient.Stops] }).ToList();

    // The twelve colors the color picker offers, as the panel draws them: the theme's own list, or else colors from the theme.
    public IReadOnlyList<ThemeColor> Swatches => _swatches is { Count: > 0 } own ? own : _derivedSwatches ??= DeriveSwatches();
    public bool HasOwnSwatches => _swatches is { Count: > 0 };
    // The picker's order: the app colors, warning and critical, the clock swatches, the gradients, then the theme's other colors.
    private static readonly string[] SwatchApps = ["claude", "codex", "antigravity", "zcode", "dsh", "manual", "warning", "critical", "cursor"];
    private List<ThemeColor> DeriveSwatches()
    {
        var colors = SwatchApps.Select(key => _apps.TryGetValue(key, out var color) ? color : (ThemeColor?)null).OfType<ThemeColor>()
            .Concat(ClockColors.Swatches.Select(ClockPreset))
            .Concat(ClockGradients.SelectMany(gradient => gradient.Stops).Select(ThemeColor.Parse))
            .Concat(new[] { "accent", "liked", "equalizer", "textSecondary", "textPrimary" }.Select(key => this[key]))
            .Select(color => Readable(color.Over(Surface))).Distinct().Take(12).ToList();
        // A small palette is filled with the colors halfway between neighbors, which stay within the theme's range.
        for (int i = 0; colors.Count < 12 && i < colors.Count * 2; i++)
        {
            var between = Readable(ThemeColor.Mix(colors[i % colors.Count], colors[(i + 1) % colors.Count], .5));
            if (!colors.Contains(between)) colors.Add(between);
        }
        return colors;
    }

    // A color chosen elsewhere (an application's color, a clock color, the cover color) as the panel draws it: unchanged when it
    // reads on this surface, otherwise moved just far enough to reach the minimum.
    public ThemeColor Readable(ThemeColor color, double minimum = PanelThemes.GraphicContrast) => color.EnsureContrast(SurfaceStops, minimum);
}
