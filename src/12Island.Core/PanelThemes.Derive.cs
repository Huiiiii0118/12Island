namespace TwelveIsland.Core;

// The three colors the settings page offers first: the background (surface), the foreground (primary text) and the accent. The
// other tokens follow from them by the proportions of the dark and light themes, so a theme can be made by choosing three colors.
public static partial class PanelThemes
{
    public const string BackgroundKey = "surface", ForegroundKey = "textPrimary", AccentKey = "accent";
    public static readonly IReadOnlyList<string> SeedKeys = [BackgroundKey, ForegroundKey, AccentKey];

    // The foreground drawn over the surface at these opacities, on a dark surface and on a light one, as the dark and light themes
    // draw their text, lines and fills. The light theme draws its text in black; a foreground that is not black needs a little
    // more opacity for the same contrast, so the secondary and tertiary text are a step stronger than there.
    private static readonly (string Key, byte Dark, byte Light)[] ForegroundShades =
    [
        ("textSecondary", 0x9E, 0xB8), ("textTertiary", 0x8C, 0xA0), ("equalizer", 0x9E, 0x8C), ("menuDetail", 0x80, 0x8C),
        ("hairline", 0x17, 0x14), ("track", 0x1F, 0x17), ("ringTrack", 0x29, 0x21), ("fill", 0x12, 0x0D), ("fillSelected", 0x24, 0x1A),
        ("hover", 0x18, 0x12), ("pressed", 0x24, 0x1F), ("outline", 0x24, 0x26), ("popupBorder", 0x24, 0x24), ("coverPlaceholder", 0x0F, 0x0D),
        ("coverOutline", 0x14, 0x1A), ("rim", 0x12, 0x1F), ("switchFill", 0x0F, 0x0A), ("seekRail", 0x2E, 0x24), ("rowDivider", 0x0D, 0x0F),
        ("rowHover", 0x0A, 0x0A), ("tooltipBorder", 0x38, 0x29), ("bubbleBorder", 0x1A, 0x24),
    ];
    // Opaque colors between the background (0) and the foreground (1). A light theme's pop-ups are lighter than its surface
    // instead (negative: toward white).
    private static readonly (string Key, double Dark, double Light)[] BackgroundShades =
    [
        ("popup", .10, -.8), ("input", .06, .045), ("tooltip", .12, -.8), ("bubble", .17, -.8), ("scrollThumb", .33, .30),
        ("indicator", .58, .34), ("indicatorProgress", .35, .85),
    ];
    private static readonly ThemeColor DarkForeground = ThemeColor.Parse("#F5F5F7"), LightForeground = ThemeColor.Parse("#1D1D1F");

    public static bool IsDarkSurface(ThemeColor surface) => surface.Over(ThemeColor.Black).Luminance < .18;

    // The colors to set when one of the three changes to value, the changed one included. A new background or foreground brings
    // new text, lines, fills and pop-ups; a new accent brings the text on it and the date range. A background that turns a dark
    // theme light (or the reverse) also brings a foreground that reads on it, unless the one written already does.
    public static IReadOnlyDictionary<string, ThemeColor> Derive(PanelPalette palette, string seed, ThemeColor value)
    {
        if (!SeedKeys.Contains(seed)) throw new ArgumentException("Not a seed color: " + seed, nameof(seed));
        value = value.WithAlpha(255);
        var background = palette.WrittenColors[BackgroundKey].Over(ThemeColor.Black);
        var foreground = palette.WrittenColors[ForegroundKey].Over(background);
        var accent = palette.WrittenColors[AccentKey].Over(background);
        bool wasDark = IsDarkSurface(background);
        if (seed == BackgroundKey) background = value; else if (seed == ForegroundKey) foreground = value; else accent = value;
        bool dark = IsDarkSurface(background), flipped = dark != wasDark;

        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal) { [seed] = value };
        if (seed == BackgroundKey && flipped && ThemeColor.Contrast(foreground, background) < TextContrast)
            colors[ForegroundKey] = foreground = dark ? DarkForeground : LightForeground;
        if (seed != AccentKey)
        {
            colors["clock"] = foreground;
            foreach (var (key, onDark, onLight) in ForegroundShades) colors[key] = foreground.WithAlpha(dark ? onDark : onLight);
            foreach (var (key, onDark, onLight) in BackgroundShades)
            {
                double amount = dark ? onDark : onLight;
                colors[key] = amount >= 0 ? ThemeColor.Mix(background, foreground, amount) : ThemeColor.Mix(background, ThemeColor.White, -amount);
            }
        }
        if (seed == AccentKey || flipped)
        {
            colors["onAccent"] = ThemeColor.Contrast(ThemeColor.White, accent) >= ThemeColor.Contrast(ThemeColor.Black, accent) ? ThemeColor.White : ThemeColor.Black;
            colors["rangeFill"] = accent.WithAlpha(dark ? (byte)0x38 : (byte)0x2E);
        }
        return colors;
    }
}
