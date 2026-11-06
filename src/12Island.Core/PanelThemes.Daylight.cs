using System.Globalization;

namespace TwelveIsland.Core;

// Themes with a night look (PanelThemeFile.Night): which look is due at a local time, and when the next change comes. The day
// runs from Daylight.Day to Daylight.Night, by the clock; the settings can move both (DeckSettings.ThemeDaylight).
public static partial class PanelThemes
{
    public static readonly TimeSpan DefaultDay = new(6, 30, 0), DefaultNight = new(18, 30, 0);

    // "HH:mm" (or "H:mm") within a day, or null.
    public static TimeSpan? ParseTimeOfDay(string? text)
        => TimeSpan.TryParseExact((text ?? "").Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : null;
    public static string FormatTimeOfDay(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // The day's start and end: the settings' "HH:mm-HH:mm" when it is valid, else the theme's, else 06:30 to 18:30.
    // A day of no length would never end, so it falls back too.
    public static (TimeSpan Day, TimeSpan Night) DaylightHours(PanelThemeFile file, string? setting = null)
    {
        var parts = (setting ?? "").Split('-', 2);
        if (parts.Length == 2 && ParseTimeOfDay(parts[0]) is { } day && ParseTimeOfDay(parts[1]) is { } night && day != night) return (day, night);
        var own = (Day: ParseTimeOfDay(file.Daylight?.Day) ?? DefaultDay, Night: ParseTimeOfDay(file.Daylight?.Night) ?? DefaultNight);
        return own.Day != own.Night ? own : (DefaultDay, DefaultNight);
    }

    // Whether the night look is due at this local time. A day may run past midnight (a night shift's "20:00-08:00").
    public static bool IsNight(PanelThemeFile file, DateTime local, string? setting = null)
    {
        if (file.Night is null) return false;
        var (day, night) = DaylightHours(file, setting);
        var now = local.TimeOfDay;
        bool daytime = day < night ? now >= day && now < night : now >= day || now < night;
        return !daytime;
    }

    // The next local time the look changes, after `local`; null for a theme without a night look.
    public static DateTime? NextDaylightChange(PanelThemeFile file, DateTime local, string? setting = null)
    {
        if (file.Night is null) return null;
        var (day, night) = DaylightHours(file, setting);
        DateTime Next(TimeSpan at) { var t = local.Date + at; return t > local ? t : t.AddDays(1); }
        var a = Next(day); var b = Next(night);
        return a < b ? a : b;
    }

    // "06:30-18:30" for the settings.
    public static string FormatDaylight(TimeSpan day, TimeSpan night) => FormatTimeOfDay(day) + "-" + FormatTimeOfDay(night);
}
