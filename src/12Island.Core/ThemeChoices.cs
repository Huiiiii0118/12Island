namespace TwelveIsland.Core;

// What the user changed while using one panel theme: each application's theme, warning and critical colors, and the clock's
// style and color. Every theme keeps its own, as an editor keeps per-theme color overrides: switching away saves the current
// choices under the theme being left, and switching back brings them back. A theme never chosen before starts from its own
// colors and its suggested clock (PanelThemes.ApplyMatching).
public sealed class ThemeChoices
{
    public sealed class AppColors
    {
        public string Theme { get; set; } = "";
        public string Warning { get; set; } = "";
        public string Critical { get; set; } = "";
    }

    // By application instance (AppEntry.InstanceId, as "N").
    public Dictionary<string, AppColors> Apps { get; set; } = [];
    public ClockStyle ClockStyle { get; set; }
    public ClockColor ClockColor { get; set; }
    public string ClockCustomColor { get; set; } = ClockColors.DefaultCustom;
    public List<string> ClockGradient { get; set; } = [];

    public ThemeChoices Copy() => new()
    {
        Apps = Apps.ToDictionary(pair => pair.Key, pair => new AppColors { Theme = pair.Value.Theme, Warning = pair.Value.Warning, Critical = pair.Value.Critical }),
        ClockStyle = ClockStyle, ClockColor = ClockColor, ClockCustomColor = ClockCustomColor, ClockGradient = [.. ClockGradient]
    };

    public static ThemeChoices From(DeckSettings settings) => new()
    {
        Apps = settings.Apps.ToDictionary(app => app.InstanceId.ToString("N"), app => new AppColors { Theme = app.ThemeColor, Warning = app.WarningColor, Critical = app.CriticalColor }),
        ClockStyle = settings.ClockStyle, ClockColor = settings.ClockColor, ClockCustomColor = settings.ClockCustomColor, ClockGradient = [.. settings.ClockGradient]
    };

    // An application added since these were saved takes the theme's own colors.
    public void ApplyTo(DeckSettings settings)
    {
        foreach (var app in settings.Apps)
        {
            var colors = Apps.GetValueOrDefault(app.InstanceId.ToString("N"));
            app.ThemeColor = colors?.Theme ?? ""; app.WarningColor = colors?.Warning ?? ""; app.CriticalColor = colors?.Critical ?? "";
        }
        settings.ClockStyle = ClockStyle; settings.ClockColor = ClockColor; settings.ClockCustomColor = ClockCustomColor;
        if (ClockGradient.Count == 3) settings.ClockGradient = [.. ClockGradient];
    }
}

public static partial class PanelThemes
{
    // Switches the panel theme, keeping what the user changed under each theme (ThemeChoices). from is the theme being left,
    // as chosen (its id, "system" included); previous its palette; to and next the theme chosen and its palette.
    public static void SwitchTheme(DeckSettings settings, string from, PanelPalette? previous, string to, PanelPalette next)
    {
        from = NormalizeId(from); to = NormalizeId(to);
        settings.PanelTheme = to;
        if (from == to) return;
        settings.ThemeChoices[from] = ThemeChoices.From(settings);
        if (settings.ThemeChoices.TryGetValue(to, out var saved)) saved.ApplyTo(settings);
        else ApplyMatching(next, settings, previous);
    }

    // The application list was replaced (the applications page's Restore defaults): each new application takes the colors the
    // other themes saved for the old one with the same quota source, the first new for the first old, and saved colors of
    // applications no longer listed are dropped. The new applications get new instances, so their keys do not carry over.
    public static void CarryAppChoices(DeckSettings settings, IReadOnlyList<AppEntry> previous)
    {
        foreach (var choices in settings.ThemeChoices.Values)
        {
            var left = previous.ToList();
            var apps = new Dictionary<string, ThemeChoices.AppColors>();
            foreach (var app in settings.Apps)
            {
                int at = left.FindIndex(old => old.QuotaSource == app.QuotaSource);
                if (at < 0) continue;
                if (choices.Apps.TryGetValue(left[at].InstanceId.ToString("N"), out var colors)) apps[app.InstanceId.ToString("N")] = colors;
                left.RemoveAt(at);
            }
            choices.Apps = apps;
        }
    }

    // A theme file deleted for good takes its saved choices with it.
    public static void ForgetTheme(DeckSettings settings, string id) => settings.ThemeChoices.Remove(NormalizeId(id));
}
