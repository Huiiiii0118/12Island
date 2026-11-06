namespace TwelveIsland.Core;

// Windows' high contrast: the panel shows the system's own colors whatever theme is chosen, as the notices already do. Text is the
// window text on the window color, quiet text the gray text, and everything the theme would tint (the accent, focus, chosen
// dates, the app colors) the highlight with the highlight text on it. Lines and fills are the window text at a low opacity, and
// nothing is drawn over the surface: no gradient, shadow, scan lines, texture, glass or motifs. The rules still hold, so a
// system scheme whose gray text is too faint is moved as any theme's would be.
public static partial class PanelThemes
{
    public const string HighContrastId = "high-contrast";

    public static PanelPalette HighContrast(ThemeColor window, ThemeColor windowText, ThemeColor grayText, ThemeColor highlight, ThemeColor highlightText)
    {
        string W = window.WithAlpha(255).ToString(), T = windowText.WithAlpha(255).ToString(), G = grayText.WithAlpha(255).ToString();
        string H = highlight.WithAlpha(255).ToString(), HT = highlightText.WithAlpha(255).ToString();
        string Line(byte alpha) => windowText.WithAlpha(alpha).ToString();
        var colors = new Dictionary<string, string>
        {
            ["surface"] = W, ["popup"] = W, ["input"] = W, ["tooltip"] = W, ["bubble"] = W, ["shadow"] = W,
            ["textPrimary"] = T, ["textSecondary"] = T, ["textTertiary"] = G, ["menuDetail"] = T, ["clock"] = T, ["danger"] = T,
            ["accent"] = H, ["onAccent"] = HT, ["liked"] = H, ["equalizer"] = H, ["indicator"] = H, ["indicatorProgress"] = HT, ["rangeFill"] = highlight.WithAlpha(0x40).ToString(),
            ["rim"] = T, ["outline"] = T, ["popupBorder"] = T, ["tooltipBorder"] = T, ["bubbleBorder"] = T, ["coverOutline"] = T, ["scrollThumb"] = T,
            ["hairline"] = Line(0x40), ["rowDivider"] = Line(0x40), ["track"] = Line(0x40), ["ringTrack"] = Line(0x50), ["seekRail"] = Line(0x50),
            ["fill"] = Line(0x18), ["switchFill"] = Line(0x18), ["rowHover"] = Line(0x18), ["hover"] = Line(0x20), ["fillSelected"] = Line(0x30),
            ["pressed"] = Line(0x30), ["coverPlaceholder"] = Line(0x18)
        };
        var file = new PanelThemeFile
        {
            FormatVersion = 1, Id = HighContrastId, Base = Dark, Colors = colors,
            Apps = AppKeys.ToDictionary(key => key, _ => H),
            Effects = new PanelThemeEffects
            {
                SurfaceGradient = [], RimGradient = [], RimWidth = 1, ShadowBlur = 0, ShadowDepth = 0, ShadowOpacity = 0, Scanlines = 0, Pixel = 0, Texture = 0
            }
        };
        return Build(HighContrastId, file);
    }
}
