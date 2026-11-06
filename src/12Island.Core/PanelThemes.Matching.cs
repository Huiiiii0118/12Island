namespace TwelveIsland.Core;

// What choosing a theme changes besides the theme itself: the application columns' colors and the clock.
public static partial class PanelThemes
{
    // The clock color names theme files use. "default" is the theme's clock color, which the settings call White.
    private static ClockColor? ParseClockColor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "default" or "white" => ClockColor.White, "amber" => ClockColor.Amber, "cyan" => ClockColor.Cyan, "pink" => ClockColor.Pink, "lime" => ClockColor.Lime,
        "custom" => ClockColor.Custom, "gradient" => ClockColor.Gradient, "cover" => ClockColor.Cover, _ => null
    };
    private static string ClockColorKey(ClockColor color) => color == ClockColor.White ? "default" : ClockColors.Key(color);

    // The application columns drop their chosen colors and follow the theme's, a preset gradient carries over, and the clock takes
    // the theme's style and color. All of it can be changed again afterwards and stays until the next theme is chosen.
    public static void ApplyMatching(PanelPalette palette, DeckSettings settings, PanelPalette? previous = null)
    {
        foreach (var app in settings.Apps) { app.ThemeColor = ""; app.WarningColor = ""; app.CriticalColor = ""; }
        if (previous is not null) CarryGradient(previous, palette, settings);
        ApplyClock(palette, settings);
    }

    // A preset gradient of the theme being left becomes the next theme's preset in the same place (its "Neon" for the old
    // "Neon"); a gradient the user made stays as it is. Returns whether the gradient changed.
    public static bool CarryGradient(PanelPalette previous, PanelPalette next, DeckSettings settings)
    {
        var current = settings.ClockGradient.Select(ThemeColor.Opaque).ToList();
        int preset = previous.ClockGradients.ToList().FindIndex(gradient => gradient.Stops.SequenceEqual(current, StringComparer.OrdinalIgnoreCase));
        if (preset < 0) return false;
        var stops = next.ClockGradients[Math.Min(preset, next.ClockGradients.Count - 1)].Stops;
        if (stops.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)) return false;
        settings.ClockGradient = [.. stops];
        return true;
    }

    // Switches the clock to the style and color the palette suggests. Every chain ends at the dark theme, which names a clock,
    // so only an empty field leaves that part of the clock as it is. The custom color and gradient count only when used.
    public static void ApplyClock(PanelPalette palette, DeckSettings settings)
    {
        if (palette.Clock is not { } clock) return;
        if (Enum.TryParse<ClockStyle>(clock.Style, true, out var style)) settings.ClockStyle = style;
        var chosen = ParseClockColor(clock.Color);
        if (chosen is { } color) settings.ClockColor = color;
        if (chosen == ClockColor.Custom && clock.Custom.Length > 0) settings.ClockCustomColor = clock.Custom;
        if (chosen == ClockColor.Gradient && clock.Gradient is { Count: 3 } stops) settings.ClockGradient = [.. stops];
    }
}
