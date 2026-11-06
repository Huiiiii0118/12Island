using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TwelveIsland.Core;

public enum CompactStyle { Notch, Capsule, Line }
// The screen edge the notch hangs from (DeckSettings.NotchEdge); see EdgeFrame.
public enum ScreenEdge { Top, Bottom, Left, Right }
// What the notch or the line hangs from on its edge where a taskbar is: the screen's edge, over the taskbar, or the taskbar's inner
// edge. Without a taskbar on that edge the two are the same line. See DeckSettings.Dock.
public enum EdgeAnchor { Screen, Taskbar }
public enum WindowBehavior { Normal, Line, Hide }
// BackgroundFullscreen: a full-screen window on the panel's monitor that is not the foreground window.
public enum ScreenContext { Desktop, Maximized, Borderless, Exclusive, BackgroundFullscreen }
// The monitors that show the panel. Chosen uses DisplayMonitors and falls back to the primary monitor while none of them is connected.
public enum DisplayPlacement { Primary, Chosen }
// A chosen monitor: its device path, and its name at the time, so that a disconnected monitor can still be named.
public sealed record ChosenDisplay(string Id, string Name);
// One app's own answers to the three window rules, as "app rules": for each of the three contexts the app can be in, the
// panel can follow the global rules (null) or choose one of the behaviors they offer. An app with all three set to Normal
// is the full-screen exception as it began: its covering windows leave the panel alone. Path is the executable when it is
// known; an entry with only a name matches any process of that name.
public sealed record FullscreenRule(string Name = "", string Path = "")
{
    public WindowBehavior? Maximized { get; init; }
    public WindowBehavior? Borderless { get; init; }
    public WindowBehavior? Exclusive { get; init; }
}
// ZCode reads the ZCode desktop application's local request records and its signed-in Coding Plan quota.
// Dsh reads the DeepSeek account or API key that DeepSeek Harness (desktop app or local web app) signed in with; DeepSeek uses a key entered by hand.
// GrokBot reads the weekly allowance of the Grok Bot desktop app; it is a quota source only.
// Kimi reads the plan quota of the account Kimi Code (CLI, desktop app or VS Code extension) signed in with, and its local session records;
// KimiApi reads the same plan quota with a Kimi Code API key entered by hand.
// OpenCode reads the OpenCode Go allowance of the workspace OpenCode (CLI or desktop app) signed in to OpenCode Console with, and its
// local records; OpenCodeGo reads the same allowance with an OpenCode Console API key entered by hand.
// Statistics sources are saved as numbers. 8 and 9 were a short-lived split of Claude and are retired:
// SettingsMigrations maps them back to Claude, and later members keep their saved numbers.
public enum ProviderId { Claude, Codex, Antigravity, Cursor, GlmChina, NewApi, Sub2Api, Custom, ZCode = 10, GlmGlobal = 11, Dsh = 12, DeepSeek = 13, GrokBot = 14, Kimi = 15, KimiApi = 16, OpenCode = 17, OpenCodeGo = 18, OpenRouter = 19, Pi = 20 }
public enum SettingsTheme { System, Light, Dark }
// Pages are saved as numbers, so new members go at the end; their order in the page switch is DeckSettings.PageOrder.
public enum DeckPage { Usage, Music, Monitor, Notify, Clipboard, Convert }
public enum DefaultDeckPage { Last, Usage, Music, Monitor, Notify, Clipboard, Convert }
// What the mouse wheel does over the expanded panel. Saved as numbers, so new members go at the end.
public enum MouseWheelAction { None, Pages, Volume }
// What a double click on an end of the collapsed notch or capsule does: turn its auto width on or off, or start moving it
// (MainWindow.Move.cs); moving is the default. Saved as numbers, so new members go at the end.
public enum GripDoubleClick { AutoWidth, Move }
public enum CompactMusicText { None, Title, Lyrics }
// What the collapsed music hides while the song is paused: the title or lyric, the cover, or both. Saved as numbers, so new
// members go at the end.
public enum PausedMusicHide { None, Text, Cover, Both }
// How the level bars show the sound. Flow: one value for the whole sound that runs from one end of the bars to the other, so a
// drum beat or a voice is seen crossing them. Spectrum: the sound split by pitch, each bar one band, low on the left and high
// on the right. Saved as numbers, so new members go at the end; a file without it, or with a number not listed, is Flow.
public enum EqualizerMode { Flow = 0, Spectrum = 1 }
// The one-shot attention a waiting capsule plays when a new notice arrives. Bounce: the capsule drops and settles, as a dock's
// icon does. Sonic: a ring bursts from the capsule while it punches outward. Saved as numbers; a file without it, or with a
// number not listed, is None.
public enum NotifyAttention { None = 0, Bounce = 1, Sonic = 2 }
// The parts of the collapsed notch and capsule that are put in order, left to right. The cover stays at the left; Music places
// only the title or lyric. Saved as numbers, so new members go at the end.
public enum CompactPart { Rings, Music, Monitor, Equalizer, Clock }
public static class CompactParts
{
    // A file saved before the equalizer and the clock were put in order adds them after its parts, the clock last, where they stood.
    // The quota rings left the collapsed content (they no longer show), so the saved order drops them whatever it holds.
    public static IReadOnlyList<CompactPart> Default { get; } = [CompactPart.Music, CompactPart.Monitor, CompactPart.Equalizer, CompactPart.Clock];
    // Each known part once, in the saved order; a part missing from the file takes its default place after the others.
    public static List<CompactPart> Normalize(IEnumerable<CompactPart>? order) =>
        [.. (order ?? []).Where(part => Enum.IsDefined(part) && part != CompactPart.Rings).Concat(Default).Distinct()];
}
// Saved settings keep the number, so a new style goes at the end.
// Date is the panel's own font with the time and the date on one line; every other style shows the time alone or, in
// Stacked, the date under it.
public enum ClockStyle { Minimal, Stacked, Digits, Dots, Segment, Neon, Serif, Wide, Condensed, Pixel, Rounded, Date }
// Every clock style takes this one color. Gradient spreads ClockGradient across the digits, Custom is ClockCustomColor,
// and Cover follows the color picked from the playing song's cover. Saved as numbers, so new members go at the end.
public enum ClockColor { White, Amber, Cyan, Pink, Lime, Gradient, Cover, Custom }

public static class ClockColors
{
    public const string DefaultCustom = "#B9A8FF";
    // The clock colors a panel theme may give its own shade (see PanelPalette.ClockPreset), and their names in theme files.
    public static readonly IReadOnlyList<ClockColor> Swatches = [ClockColor.Amber, ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime];
    public static string Key(ClockColor color) => color.ToString().ToLowerInvariant();
    // The built-in colors of the amber, cyan, pink and lime swatches; a panel theme can replace them (PanelPalette.ClockPreset).
    public static string Preset(ClockColor color) => color switch
    { ClockColor.Amber => "#FF9F43", ClockColor.Cyan => "#5CE1E6", ClockColor.Pink => "#FF8FB1", ClockColor.Lime => "#E4FF6A", _ => "#F5F5F7" };
    // Every gradient has three colors: start, middle and end.
    public static readonly string[] Sunset = ["#FFB38A", "#FF7EB3", "#B69CFF"];
    public static IReadOnlyList<(string Name, string[] Stops)> Gradients =>
    [
        (Loc.T("日落", "Sunset"), Sunset),
        (Loc.T("海洋", "Ocean"), ["#7CF3FF", "#4FA8FF", "#6A6CFF"]),
        (Loc.T("极光", "Aurora"), ["#8BFFB0", "#5CE1E6", "#B69CFF"]),
        (Loc.T("火焰", "Ember"), ["#FFE27A", "#FF9F43", "#FF5E5E"]),
        (Loc.T("霓虹", "Neon"), ["#FF5CF0", "#A66BFF", "#5CE1E6"])
    ];
    // Under Windows' high contrast the clock is the system's window text (the high contrast palette's clock color, which "white"
    // stands for) whatever color, gradient or cover was chosen, at full strength and with no glow. The choice itself is kept,
    // so the clock is drawn as chosen again when high contrast is turned off.
    public static ClockColor Shown(ClockColor chosen, bool highContrast) => highContrast ? ClockColor.White : chosen;
    public static bool Glows(ClockStyle style, bool highContrast) => !highContrast && style is ClockStyle.Neon or ClockStyle.Segment;
}

// A dashboard column has an identity independent of either data source.
public sealed class AppEntry
{
    private ProviderId? _usageSource;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    [JsonConverter(typeof(QuotaSourceConverter))]
    public ProviderId QuotaSource { get; set; }
    public ProviderId UsageSource { get => _usageSource ?? (ProviderCatalog.IsUsageSource(QuotaSource) ? QuotaSource : ProviderId.Claude); set => _usageSource = value; }
    public string Site { get; set; } = "";
    public string Script { get; set; } = "";
    public Guid SecretRevision { get; set; }
    // Claude and Grok Bot rows only: read the quota from the account API. When off, the row sends no request and shows the
    // quota the desktop app (Claude or Grok Bot) recorded on this PC. Each row has its own choice.
    public bool QuotaOnline { get; set; } = true;
    // New API, Sub2API and custom rows: also send requests, and so the API key, over plain HTTP to any address, such as a
    // gateway on a NAS or reached through Tailscale. Off by default; HTTP to this PC is allowed either way.
    public bool AllowHttp { get; set; }
    // Rows whose source reports an account balance without a total (ProviderCatalog.HasAccountBalance): the balance that counts
    // as 0% used, in the balance's own currency. The account balance is then drawn as (target − balance) / target used, with
    // the warning and critical levels of any quota, and 0% while the balance is above the target. New rows start at 100;
    // null, saved when the field is cleared, shows the bare amount.
    public decimal? BalanceTarget { get; set; } = 100;
    [JsonIgnore] public string ConfigurationKey => ProviderCatalog.ConfigurationKey(this);
    // Shorthand for QuotaSource in code. The "Id" member of old files is converted by SettingsMigrations.
    [JsonIgnore] public ProviderId Id { get => QuotaSource; set => QuotaSource = value; }
    // Empty means the automatically supplied quota-source name.
    public string DisplayName { get; set; } = "";
    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? AppPresets.Name(QuotaSource) : DisplayName;
    public bool Enabled { get; set; } = true;
    public string ThemeColor { get; set; } = "";
    public string WarningColor { get; set; } = "";
    public string CriticalColor { get; set; } = "";
    // The chosen color or the application's preset, before any panel theme; the panel draws PanelTheme.AppTheme and friends.
    [JsonIgnore] public string Theme => ThemeColor.Length > 0 ? ThemeColor : AppPresets.ThemeColor(QuotaSource);
    [JsonIgnore] public string Warning => WarningColor.Length > 0 ? WarningColor : AppPresets.WarningColor;
    [JsonIgnore] public string Critical => CriticalColor.Length > 0 ? CriticalColor : AppPresets.CriticalColor;
    // Daily off-peak periods in local time. Inside one, the panel draws this row's quotas in the off-peak colors: below the
    // warning level in OffPeakColor, and at the warning and critical levels in OffPeakWarningColor and OffPeakCriticalColor.
    // Each one left unchosen keeps the row's usual color for that level. Empty periods mean no off-peak time.
    public List<OffPeakPeriod> OffPeak { get; set; } = [];
    public string OffPeakColor { get; set; } = "";
    public string OffPeakWarningColor { get; set; } = "";
    public string OffPeakCriticalColor { get; set; } = "";
    public bool IsOffPeak(DateTime local) => OffPeak?.Any(period => period.Contains(local)) == true;
    // The next minute at which this row enters or leaves its off-peak time; null without periods, or when they cover the
    // whole day. A boundary inside another period changes nothing, so the first boundary that flips the state is taken.
    public DateTime? NextOffPeakChange(DateTime local)
    {
        if (OffPeak is not { Count: > 0 }) return null;
        bool now = IsOffPeak(local);
        var minute = local.Date.AddMinutes(local.Hour * 60 + local.Minute);
        return OffPeak.SelectMany(period => new[] { period.Start, period.End }).Distinct()
            .Select(boundary => local.Date.AddMinutes(boundary)).Select(at => at <= minute ? at.AddDays(1) : at)
            .Order().Cast<DateTime?>().FirstOrDefault(at => IsOffPeak(at!.Value) != now);
    }
    public AppEntry Copy()
    {
        var copy = (AppEntry)MemberwiseClone();
        copy.UsageSource = UsageSource;
        copy.OffPeak = [.. (OffPeak ?? []).Select(period => period.Copy())];
        return copy;
    }
    public void Normalize()
    {
        UsageSource = ProviderCatalog.IsUsageSource(UsageSource) ? UsageSource : ProviderId.Claude;
        Site = ProviderCatalog.NormalizeSite(Site ?? "", QuotaSource != ProviderId.Custom);
        Script ??= "";
        // A custom row always starts from the template, which is the only place its return format is described.
        if (QuotaSource == ProviderId.Custom && Script.Trim().Length == 0) Script = ProviderScripts.Example;
        DisplayName = Regex.Replace(DisplayName ?? "", @"\s+", " ").Trim();
        if (DisplayName.Length > 64) DisplayName = DisplayName[..64];
        ThemeColor = AppPresets.NormalizeColor(ThemeColor);
        WarningColor = AppPresets.NormalizeColor(WarningColor);
        CriticalColor = AppPresets.NormalizeColor(CriticalColor);
        BalanceTarget = BalanceTarget is { } target && decimal.Round(target, 2, MidpointRounding.AwayFromZero) is > 0 and <= AppPresets.MaximumBalanceTarget and var rounded ? rounded : null;
        OffPeak = [.. (OffPeak ?? []).Where(period => period is not null).Take(AppPresets.MaximumOffPeakPeriods).Select(period => period.Normalized())];
        OffPeakColor = AppPresets.NormalizeColor(OffPeakColor);
        OffPeakWarningColor = AppPresets.NormalizeColor(OffPeakWarningColor);
        OffPeakCriticalColor = AppPresets.NormalizeColor(OffPeakCriticalColor);
    }
}

// One daily off-peak period: Start and End are minutes after local midnight. An End before Start runs past midnight
// into the next day; an End equal to Start covers the whole day, as the settings bar dragged out to both edges gives.
public sealed class OffPeakPeriod
{
    public const int Day = 24 * 60;
    public int Start { get; set; }
    public int End { get; set; } = 8 * 60;
    public OffPeakPeriod Copy() => (OffPeakPeriod)MemberwiseClone();
    public OffPeakPeriod Normalized() => new() { Start = Math.Clamp(Start, 0, Day - 1), End = Math.Clamp(End, 0, Day - 1) };
    public bool Contains(DateTime local)
    {
        int minute = local.Hour * 60 + local.Minute;
        return Start < End ? minute >= Start && minute < End : Start == End || minute >= Start || minute < End;
    }
    public static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
    // Accepts "8:30", "08:30", "0830" and "8"; anything else, or a time past 23:59, is refused.
    public static bool TryParse(string text, out int minutes)
    {
        minutes = 0;
        var match = Regex.Match(text.Trim().Replace('：', ':'), @"^(\d{1,2})(?::?(\d{2}))?$");
        if (!match.Success) return false;
        int hour = int.Parse(match.Groups[1].Value), minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        if (hour > 23 || minute > 59) return false;
        minutes = hour * 60 + minute;
        return true;
    }
}

public static class AppPresets
{
    public const int MaximumApps = 7;
    // A long list would be hard to review, and the exceptions are meant for a few shell-level apps.
    public const int MaximumFullscreenExceptions = 32;
    public const string WarningColor = "#E5C890", CriticalColor = "#E78284";
    public const int MaximumOffPeakPeriods = 6;
    public const decimal MaximumBalanceTarget = 1_000_000_000;
    public const double WarningPercent = 70, CriticalPercent = 90;
    // Each column needs this much width; the panel's minimum width follows the number of enabled applications.
    public const double MinimumColumnWidth = 150;
    public static readonly string[] Palette =
    [
        "#E5A385", "#F0B37E", "#E5C890", "#98DBB0", "#7FD1C4", "#9CB9FF",
        "#B9A8FF", "#E7A0D0", "#E78284", "#D4D5DF", "#8E8E93", "#F5F5F7"
    ];
    public static string Name(ProviderId id) => ProviderCatalog.Name(id);
    // The original preset colors: coral for Claude, mint for Codex, periwinkle for Antigravity, light gray for Cursor.
    public static string ThemeColor(ProviderId id) => id switch
    { ProviderId.Claude => "#E5A385", ProviderId.Codex => "#98DBB0", ProviderId.Antigravity => "#9CB9FF", ProviderId.ZCode => "#3A538D", ProviderId.Dsh => "#6081DA", ProviderId.Kimi => "#68CDEB", ProviderId.OpenCode => "#8E8E93",
      ProviderId.GlmChina or ProviderId.GlmGlobal or ProviderId.DeepSeek or ProviderId.KimiApi or ProviderId.OpenCodeGo or ProviderId.OpenRouter or ProviderId.NewApi or ProviderId.Sub2Api or ProviderId.Custom => ManualColor, _ => "#D4D5DF" };
    // Sources entered by hand share one purple by default.
    public const string ManualColor = "#CC9EED";
    public static int Level(double usedPercent) => usedPercent >= CriticalPercent ? 2 : usedPercent >= WarningPercent ? 1 : 0;
    public static bool IsColor(string value) => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");
    public static string NormalizeColor(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 6 && Regex.IsMatch(text, "^[0-9A-Fa-f]{6}$")) text = "#" + text;
        return IsColor(text) ? "#" + text[1..].ToUpperInvariant() : "";
    }
}

public enum PanelSize { Compact, Standard, Spacious }

// Quick sizes depend on the number of enabled applications at the moment they are chosen; the stored width
// and height do not move afterwards. One to four applications use the values settled with the user; more
// applications extend the same pattern per column, capped at the 1200 DIP maximum width.
public static class PanelSizes
{
    public static (double Width, double Height) For(PanelSize size, int apps)
    {
        int columns = Math.Max(1, apps);
        static double Round(double value) => Math.Ceiling(value / 10) * 10;
        var (width, height) = (size, columns) switch
        {
            (PanelSize.Compact, 1) => (500d, 170d), (PanelSize.Standard, 1) => (550d, 200d), (PanelSize.Spacious, 1) => (630d, 220d),
            (PanelSize.Compact, 2) => (500d, 150d), (PanelSize.Standard, 2) => (520d, 200d), (PanelSize.Spacious, 2) => (760d, 310d),
            (PanelSize.Compact, 3) => (530d, 160d), (PanelSize.Standard, 3) => (730d, 240d), (PanelSize.Spacious, 3) => (860d, 310d),
            (PanelSize.Compact, 4) => (700d, 170d), (PanelSize.Standard, 4) => (760d, 240d), (PanelSize.Spacious, 4) => (1100d, 310d),
            (PanelSize.Compact, _) => (Round(columns * 170 + 12), 170d),
            (PanelSize.Spacious, _) => (Round(columns * 270 + 12), 310d),
            _ => (Round(columns * 187 + 12), 240d)
        };
        return (Math.Clamp(width, 440, 1200), Math.Clamp(height, 140, 400));
    }
    public static string Name(PanelSize size) => size switch { PanelSize.Compact => Loc.T("紧凑", "Compact"), PanelSize.Spacious => Loc.T("宽敞", "Spacious"), _ => Loc.T("标准", "Standard") };
}

// The system monitor page's quick sizes. Unlike the usage page's they do not depend on the applications.
public static class MonitorSizes
{
    public static readonly (double Width, double Height) Default = (620, 220);
    public static (double Width, double Height) For(PanelSize size) => size switch
    { PanelSize.Compact => (480, 160), PanelSize.Spacious => (860, 320), _ => Default };
}

public sealed class DeckSettings
{
    private List<AppEntry>? _apps;
    private List<CompactReading>? _readings;
    private List<CompactPart> _notchOrder = [.. CompactParts.Default], _capsuleOrder = [.. CompactParts.Default];
    private List<DeckPage> _pageOrder = [.. DeckPages.Order];
    private int _equalizerBars = CompactMusicLayout.DefaultBars, _musicEqualizerBars = 5;
    private EqualizerMode _equalizerMode = EqualizerMode.Flow;
    private PausedMusicHide _musicPausedHide;
    // Version of the TwelveIsland that last saved settings.json; see SettingsMigrations. Empty in files saved before 0.1.0.
    public string AppVersion { get; set; } = "";
    public bool LaunchAtStartup { get; set; }
    // Off, the notification area has no TwelveIsland icon. Starting TwelveIsland again then shows the panels and the settings
    // (App.ShowFromRelaunch), and the settings window has its own exit button.
    public bool ShowTrayIcon { get; set; } = true;
    public SettingsTheme Theme { get; set; } = SettingsTheme.System;
    // The panel's color theme (see PanelThemes): "system", "dark", "light", a built-in theme or a theme file's id.
    // The dark default is the original look. An id whose file is missing is kept and shown as the dark theme meanwhile.
    public string PanelTheme { get; set; } = PanelThemes.Dark;
    // How solid every window is drawn: 1 is opaque, .6 the most see-through. The text stays fully opaque (App.ApplyShellOpacity).
    public double ShellOpacity { get; set; } = 1;
    // What the user changed under each panel theme, by theme id (see ThemeChoices); the theme in use keeps its choices in the
    // ordinary settings, and they are saved here when another theme is chosen.
    public Dictionary<string, ThemeChoices> ThemeChoices { get; set; } = [];
    // When a theme with a night look shows its day look, "HH:mm-HH:mm" local time; empty keeps the theme's own hours.
    public string ThemeDaylight { get; set; } = "";
    // A first start replaces the default with the Windows display language (SettingsStore.Load); files saved before
    // English existed are Chinese (SettingsMigrations).
    public string Language { get; set; } = Loc.English;
    public CompactStyle Style { get; set; } = CompactStyle.Capsule;
    // Size and position of each collapsed style in DIPs; limits and defaults are in CollapsedLimits. OffsetX moves the surface
    // from the centre; each monitor narrows the offset to what fits (CollapsedGeometry) without changing the saved value.
    public double NotchWidth { get; set; } = CollapsedLimits.NotchWidthDefault;
    public double NotchHeight { get; set; } = CollapsedLimits.NotchHeightDefault;
    public double NotchOffsetX { get; set; } = CollapsedLimits.OffsetDefault;
    // The edge the notch hangs from. On the other edges it keeps its width (along the edge), height and offset (along the edge
    // too); see EdgeFrame. The capsule always sits below the top edge.
    public ScreenEdge NotchEdge { get; set; } = ScreenEdge.Top;
    // What the notch hangs from on that edge; null until chosen, which is the screen's edge at the top and the taskbar's elsewhere,
    // as before the choice existed.
    public EdgeAnchor? NotchAnchor { get; set; }
    // The edge and the anchor of the line, as the notch's; the line keeps to the taskbar's side by default. A window rule that turns
    // the notch or the capsule into the line shows it where the line's own settings put it.
    public ScreenEdge LineEdge { get; set; } = ScreenEdge.Top;
    public EdgeAnchor? LineAnchor { get; set; }
    // Where a style works from: the edge and whether it hangs from the screen's edge over a taskbar there (EdgeFrame.For). The
    // capsule floats below the top of the screen.
    public (ScreenEdge Edge, bool OverTaskbar) Dock(CompactStyle style) => style switch
    {
        CompactStyle.Notch => (NotchEdge, Anchor(style, NotchEdge, NotchAnchor) == EdgeAnchor.Screen),
        CompactStyle.Line => (LineEdge, Anchor(style, LineEdge, LineAnchor) == EdgeAnchor.Screen),
        _ => (ScreenEdge.Top, true)
    };
    public static EdgeAnchor Anchor(CompactStyle style, ScreenEdge edge, EdgeAnchor? chosen)
        => chosen ?? (style == CompactStyle.Notch && edge == ScreenEdge.Top ? EdgeAnchor.Screen : EdgeAnchor.Taskbar);
    public ScreenEdge CompactEdge(CompactStyle style) => style == CompactStyle.Line ? LineEdge : style == CompactStyle.Notch ? NotchEdge : ScreenEdge.Top;
    public double CapsuleWidth { get; set; } = CollapsedLimits.CapsuleWidthDefault;
    public double CapsuleHeight { get; set; } = CollapsedLimits.CapsuleHeightDefault;
    public double CapsuleOffsetX { get; set; } = CollapsedLimits.OffsetDefault;
    // Distance from the top of the work area. Saved up to 4000; each monitor stops the capsule at the bottom of its work area.
    public double CapsuleTop { get; set; } = CollapsedLimits.CapsuleTopDefault;
    public double LineLength { get; set; } = CollapsedLimits.LineLengthDefault;
    // In half-DIP steps.
    public double LineThickness { get; set; } = CollapsedLimits.LineThicknessDefault;
    public double LineTop { get; set; } = CollapsedLimits.LineTopDefault;
    public double LineOffsetX { get; set; } = CollapsedLimits.OffsetDefault;
    public string HideHotkey { get; set; } = "";
    public string ExpandHotkey { get; set; } = "";
    // The resize shortcut: modifiers with the arrow keys ("Ctrl+Alt+Arrows", HotkeyText.Arrows); unset by default.
    public string ResizeHotkey { get; set; } = "";
    public bool MusicPage { get; set; } = true;
    public double MusicWidth { get; set; } = 440;
    public double MusicHeight { get; set; } = 200;
    // The notch and the capsule each keep their own choices; the indicator only offers the playback progress.
    public bool NotchSummary { get; set; } = true;
    public bool NotchMusic { get; set; } = true;
    public bool CapsuleSummary { get; set; } = true;
    // Writes each application's first letter (or digit) of its name inside its collapsed quota ring, in the notch, the capsule and
    // the side column. Off by default; a file without it reads as off.
    public bool QuotaRingLetters { get; set; }
    public bool CapsuleMusic { get; set; } = true;
    // The notch and the capsule each switch the equalizer on. It shows while a song plays (or is paused), or, with EqualizerAlways,
    // at all times: then it reads the whole system's sound while no song plays.
    public bool NotchEqualizer { get; set; } = true;
    public bool CapsuleEqualizer { get; set; } = true;
    public bool EqualizerAlways { get; set; }
    // How many bars the collapsed equalizer has, which sets its length (CompactMusicLayout.BarsWidth).
    public int EqualizerBars { get => _equalizerBars; set => _equalizerBars = CompactMusicLayout.SafeBars(value); }
    // Kept only so settings written before the equalizer's color pickers were removed still load; the equalizer always takes
    // the panel theme's own text color now (MainWindow.EqualizerColor), and what is saved here is no longer read.
    public ClockColor EqualizerColor { get; set; } = ClockColor.White;
    public string EqualizerCustomColor { get; set; } = ClockColors.DefaultCustom;
    // The notch and the capsule each switch the clock on; its style, color, hour format and seconds are shared.
    public bool NotchClock { get; set; } = true;
    public bool CapsuleClock { get; set; } = true;
    public ClockStyle ClockStyle { get; set; } = ClockStyle.Date;
    public ClockColor ClockColor { get; set; } = ClockColor.White;
    public string ClockCustomColor { get; set; } = ClockColors.DefaultCustom;
    public List<string> ClockGradient { get; set; } = [.. ClockColors.Sunset];
    public bool Clock24Hour { get; set; } = true;
    public bool ClockSeconds { get; set; }
    public CompactMusicText MusicText { get; set; }
    public bool MusicTrackNotice { get; set; } = true;
    // A song taken as instrumental (Lyrics.Instrumental) shows its title in the collapsed lyric's place instead of "♪".
    public bool MusicInstrumentalTitle { get; set; }
    // The surface narrows without what is hidden; a number not listed shows everything.
    public PausedMusicHide MusicPausedHide { get => _musicPausedHide; set => _musicPausedHide = Enum.IsDefined(value) ? value : PausedMusicHide.None; }
    // The album cover at the left of the collapsed row and on the music page; some players give no cover, only a placeholder.
    public bool MusicCompactCover { get; set; } = true;
    public bool MusicPageCover { get; set; } = true;
    public bool MusicIndicatorProgress { get; set; } = true;
    public bool LyricsEnabled { get; set; } = true;
    // How many bars the music page's equalizer has, beside the title; its bars keep their spacing, so more bars make it longer.
    public int MusicEqualizerBars { get => _musicEqualizerBars; set => _musicEqualizerBars = CompactMusicLayout.SafeBars(value); }
    // How the collapsed equalizer and the music page's equalizer show the sound; one choice for both, while each has its own length.
    public EqualizerMode EqualizerMode { get => _equalizerMode; set => _equalizerMode = Enum.IsDefined(value) ? value : EqualizerMode.Flow; }
    // Kept only so settings written before the current lyric's color choice was removed still load; that line always
    // uses the theme's primary text color now (LyricInk).
    public ClockColor MusicLyricColor { get; set; } = ClockColor.Cover;
    public string MusicLyricCustomColor { get; set; } = ClockColors.DefaultCustom;
    // Offers "网易云音乐 · 完整控制" in the source menu, or alone in its place when nothing plays.
    public bool NeteaseFullControlEntry { get; set; }
    // Restarts a player started the normal way with full control, without asking again; switching it on asks once.
    public bool NeteaseAutoFullControl { get; set; }
    // NetEase Cloud Music's full control, which the user confirmed when switching it on: its libcef.dll is kept changed, also after
    // an update replaces it (a running player then restarts once), so every start opens the port. Switching it off restores the file.
    public bool NeteaseFullControl { get; set; }
    // KuGou's full control, which the user confirmed when switching it on: its libcef.dll is kept changed, also after a KuGou
    // update replaces it (a running KuGou then restarts once). Switching it off restores the file.
    public bool KugouFullControl { get; set; }
    // Enables Apple Music's full control when TwelveIsland starts, without the entry and its explanation.
    public bool AppleMusicFullControl { get; set; }
    public DefaultDeckPage DefaultPage { get; set; }
    // A right click on the expanded panel opens the settings window.
    public bool RightClickSettings { get; set; } = true;
    // A notch or capsule wider than every page opens its panel under the pointer when hovered, instead of at its middle.
    public bool ExpandAtPointer { get; set; }
    // What the mouse wheel does while the panel is expanded; scrolling parts of a page keep their wheel.
    public MouseWheelAction MouseWheel { get; set; } = MouseWheelAction.Pages;
    // Read only to carry the switch of a file saved before MouseWheel existed; Normalize moves it across and the member
    // then disappears from the saved file.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MouseWheelPages { get; set; }
    public DeckPage LastPage { get; set; }
    public double Width { get; set; } = 520;
    public double Height { get; set; } = 200;
    // The quick size last chosen in settings; cleared when the width or height is edited by hand.
    public PanelSize? QuickSize { get; set; }
    // TwelveIsland drawn larger, in percent (PanelScales): the collapsed surface and the expanded panel, its pages, header and menus
    // grow together, as Windows' display scaling would for TwelveIsland alone. Sizes stay saved before the scale.
    public int PanelScale { get; set; } = PanelScales.Default;
    public int OpenDelay { get; set; } = 0;
    public int CloseDelay { get; set; } = 0;
    public int AnimationDuration { get; set; } = 360;
    public bool Animations { get; set; } = true;
    public bool ForceTopmost { get; set; }
    public WindowBehavior Maximized { get; set; } = WindowBehavior.Normal;
    public WindowBehavior Borderless { get; set; } = WindowBehavior.Hide;
    public WindowBehavior Exclusive { get; set; } = WindowBehavior.Hide;
    // Whether a window behind the foreground one counts too: maximized or borderless across the panel's monitor, it calls for
    // the same rule as if it were in front. Off, only the foreground window is read, as before the background was looked at.
    public bool BackgroundWindows { get; set; } = true;
    // Applications whose windows are not counted as full screen, so that a desktop beautification tool does not hide the
    // panel. Empty by default: every app follows the rules above.
    public List<FullscreenRule> FullscreenRules { get; set; } = [];
    // Read only to carry the exceptions of a file saved before app rules existed; Normalize moves them across and the
    // member then disappears from the saved file.
    public sealed record LegacyFullscreenException(string Name = "", string Path = "");
    public List<LegacyFullscreenException> FullscreenExceptions { get; set; } = [];
    public DisplayPlacement Display { get; set; }
    // Kept while Display is Primary, so that choosing displays again restores them.
    public List<ChosenDisplay> DisplayMonitors { get; set; } = [];
    public bool UsagePage { get; set; } = true;
    // The notification centre: QQ, WeChat and mailbox notices, kept until they are read.
    public bool NotifyPage { get; set; } = true;
    public double NotifyWidth { get; set; } = 520;
    public double NotifyHeight { get; set; } = 220;
    // The clipboard history page.
    public bool ClipboardPage { get; set; } = true;
    public double ClipboardWidth { get; set; } = 560;
    public double ClipboardHeight { get; set; } = 240;
    // The file conversion page.
    public bool ConvertPage { get; set; } = true;
    public double ConvertWidth { get; set; } = 560;
    public double ConvertHeight { get; set; } = 260;
    // Notification centre. Chat and office programs are watched through their taskbar icons (the shell announces every flash)
    // and through the notifications Windows keeps for them; a mailbox is asked over IMAP, and its password is kept apart
    // (NotifyMail.Secret, protected for the current user).
    public bool NotifyQq { get; set; } = true;
    public bool NotifyWeChat { get; set; } = true;
    // Every other chat and office program the page knows (DingTalk, Feishu, Teams, Slack, Outlook, ...).
    public bool NotifyApps { get; set; } = true;
    public bool NotifyMail { get; set; }
    public string NotifyMailHost { get; set; } = "imap.qq.com";
    public int NotifyMailPort { get; set; } = 993;
    public string NotifyMailUser { get; set; } = "";
    // How often the mailbox is asked, in seconds.
    public int NotifyMailInterval { get; set; } = 60;
    // The one-shot attention the waiting capsule plays when a notice arrives: nothing, a bounce, or a ring burst.
    private NotifyAttention _notifyAttention = NotifyAttention.Bounce;
    public NotifyAttention NotifyAttention { get => _notifyAttention; set => _notifyAttention = Enum.IsDefined(value) ? value : NotifyAttention.None; }
    // The collapsed island keeps showing a waiting message until its program stops flashing; off, it shows it for ten seconds.
    public bool NotifySticky { get; set; } = true;
    // Clipboard history: how many entries are kept, and whether pictures are kept with the text.
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardLimit { get; set; } = 20;
    public bool ClipboardImages { get; set; } = true;
    public string ClipboardHotkey { get; set; } = "";
    // File conversion: the folder results go to (empty: beside each source file), and the tools that are found on their own
    // but may be pointed at by hand.
    public string ConvertOutputFolder { get; set; } = "";
    public string ConvertFfmpegPath { get; set; } = "";
    public string ConvertOfficePath { get; set; } = "";
    // The system monitor page; it reads the system only while it is open on screen.
    public bool MonitorPage { get; set; } = true;
    public double MonitorWidth { get; set; } = MonitorSizes.Default.Width;
    public double MonitorHeight { get; set; } = MonitorSizes.Default.Height;
    public MonitorPalette MonitorPalette { get; set; } = MonitorPalette.Theme;
    // The Custom scheme's colors; kept while another scheme is chosen.
    public MonitorCustomColors MonitorCustomColors { get; set; } = new();
    // The collapsed readings' colors at the warning and critical levels (ReadingLevels), "#RRGGBB", or null for the panel
    // theme's warning and critical colors. The expanded page does not change color by level.
    public string? CompactWarningColor { get; set; }
    public string? CompactCriticalColor { get; set; }
    // The collapsed readings' dials and core columns ease to each reading at this many frames a second, or not at all.
    public CompactSmoothing CompactSmoothing { get; set; } = CompactSmoothing.Ten;
    // Seconds between readings: 1 or 2.
    public int MonitorInterval { get; set; } = 1;
    public bool MonitorProcesses { get; set; } = true;
    public ProcessSort MonitorSort { get; set; }
    public MonitorMain MonitorMain { get; set; }
    // The graphics adapter the monitor shows, by its key (GpuSample.Key), and its name to find it by should its ids change and to
    // name it while it is not connected; null for 自动 (GpuChoice.Auto). Files saved before there was a choice read as 自动.
    public string? MonitorGpu { get; set; }
    public string? MonitorGpuName { get; set; }
    public MonitorProcessView MonitorProcessView { get; set; }
    // The expanded page shows the temperatures the system reports (Temperatures); off, none are read for it. Off by default: asking
    // a driver for a temperature froze one laptop (SensorStops), so a new user turns them on knowingly.
    public bool MonitorTemperatures { get; set; }
    // The expanded page shows the internal drives' space, one line per physical drive (Storage); off, the drives are not read for it.
    public bool MonitorStorage { get; set; } = true;
    // The expanded page shows the whole machine's power draw with its curve, and the machine's own battery when it has one (PowerResolver,
    // BatteryModel); off, neither is read for it. The collapsed row's Power and Charge readings are chosen in its table.
    public bool MonitorPower { get; set; } = true;
    // The expanded page shows a line under memory and network with the frame rate of the program in front, its name and its display's
    // refresh rate (FrameRates). Off by default; read only while the page is open, and only where the user may read Windows' frame
    // events (the Performance Log Users group, or TwelveIsland elevated).
    public bool MonitorFrames { get; set; }
    // The notch and the capsule each switch the system readings on; what they show is shared.
    public bool NotchMonitor { get; set; }
    public bool CapsuleMonitor { get; set; }
    // The order of the quota rings, the title or lyric, the system readings, the equalizer and the clock, for the notch and the capsule each.
    public List<CompactPart> NotchOrder { get => _notchOrder; set => _notchOrder = CompactParts.Normalize(value); }
    public List<CompactPart> CapsuleOrder { get => _capsuleOrder; set => _capsuleOrder = CompactParts.Normalize(value); }
    // The order of the pages in the open panel's page switch, left to right.
    public List<DeckPage> PageOrder { get => _pageOrder; set => _pageOrder = DeckPages.Normalize(value); }
    // Whether the notch and the capsule widen to their content (CollapsedGeometry.Resolve). Resizing one by dragging an end turns
    // its own off, so the width set by hand stays.
    public bool NotchAutoWidth { get; set; } = true;
    // Compacts the heap and trims the working set after the panel has been collapsed and idle for a while (App.Memory.cs). On by
    // default; the memory is read back in when the panel next opens, which can stutter, so it can be turned off.
    public bool TrimMemoryWhenIdle { get; set; } = true;
    public bool CapsuleAutoWidth { get; set; } = true;
    public GripDoubleClick GripDoubleClick { get; set; } = GripDoubleClick.Move;
    // What the collapsed readings showed before their table (CompactReadings). Kept to read older files and, written to match the
    // table (the preset it matches and the readings shown), for older versions.
    public CompactMonitorMode CompactMonitor { get; set; }
    public CompactMonitorItems CompactMonitorItems { get; set; } = CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Download | CompactMonitorItems.Upload;
    // The collapsed readings in their order, each shown or not, with or without its figure, in its own color or the palette's.
    // Files saved before the table have none; they take it from CompactMonitor and CompactMonitorItems.
    public List<CompactReading> CompactReadings
    {
        get => _readings ??= Core.CompactMonitor.Readings(CompactMonitor, CompactMonitorItems);
        set => _readings = value;
    }
    public bool UsageAutoSync { get; set; } = true;
    // The settings window's size when last closed, in DIPs, and whether it was maximized. Empty until it is closed once;
    // it opens at the default size then. The size is fitted to the screen the window opens on.
    public double? SettingsWindowWidth { get; set; }
    public double? SettingsWindowHeight { get; set; }
    public bool SettingsWindowMaximized { get; set; }
    // Shows the compact notice when a quota rises into the warning or critical level.
    public bool QuotaAlerts { get; set; } = true;
    // Also reads the Claude Code and Codex CLI session records inside WSL distributions. Off by default: it starts stopped distributions.
    public bool IncludeWsl { get; set; }
    // The ordered application list; the order is the column order of the expanded panel. Claude and Codex are shown
    // at first; the other built-in applications are listed but hidden.
    public List<AppEntry> Apps
    {
        get => _apps ??= ProviderCatalog.BuiltIns.Select(id => new AppEntry { QuotaSource = id, UsageSource = id, Enabled = id is ProviderId.Claude or ProviderId.Codex }).ToList();
        set => _apps = value;
    }

    // The quota rings are gone from the collapsed content, and with them the collapsed summary of the applications' quotas: the
    // switch that used to show them has no part left to show.
    public bool ShowsSummary(CompactStyle style) => false;
    public bool ShowsMusic(CompactStyle style) => style switch { CompactStyle.Notch => NotchMusic, CompactStyle.Capsule => CapsuleMusic, _ => false };
    // The indicator shows neither the equalizer nor the clock.
    public bool ShowsEqualizer(CompactStyle style) => style switch { CompactStyle.Notch => NotchEqualizer, CompactStyle.Capsule => CapsuleEqualizer, _ => false };
    // The indicator is too thin for text, so it never shows the clock.
    public bool ShowsClock(CompactStyle style) => style switch { CompactStyle.Notch => NotchClock, CompactStyle.Capsule => CapsuleClock, _ => false };
    // The indicator is too thin for readings, as for the clock.
    public bool ShowsMonitor(CompactStyle style) => style switch { CompactStyle.Notch => NotchMonitor, CompactStyle.Capsule => CapsuleMonitor, _ => false };
    public IReadOnlyList<CompactPart> CompactOrder(CompactStyle style) => style == CompactStyle.Capsule ? CapsuleOrder : NotchOrder;
    public void SetCompactOrder(CompactStyle style, IEnumerable<CompactPart> order) { if (style == CompactStyle.Notch) NotchOrder = [.. order]; else if (style == CompactStyle.Capsule) CapsuleOrder = [.. order]; }
    public bool CompactAutoWidth(CompactStyle style) => style switch { CompactStyle.Notch => NotchAutoWidth, CompactStyle.Capsule => CapsuleAutoWidth, _ => false };
    public void SetCompactAutoWidth(CompactStyle style, bool value) { if (style == CompactStyle.Notch) NotchAutoWidth = value; else if (style == CompactStyle.Capsule) CapsuleAutoWidth = value; }
    // The saved width of the notch or the capsule, or the line's length.
    public void SetCompactWidth(CompactStyle style, double width)
    {
        if (style == CompactStyle.Notch) NotchWidth = width; else if (style == CompactStyle.Capsule) CapsuleWidth = width; else LineLength = width;
    }
    // Media is detected for the expanded music page, the chosen style's music or equalizer, or an indicator that shows playback
    // progress; a window rule can turn any style into the indicator.
    [JsonIgnore] public bool MediaWanted => MusicPage || ShowsMusic(Style) || ShowsEqualizer(Style) || MusicIndicatorProgress
        && (Style == CompactStyle.Line || Maximized == WindowBehavior.Line || Borderless == WindowBehavior.Line || Exclusive == WindowBehavior.Line);
    // The switched-on applications. Their quotas are read for the AI usage page or for the rings of the chosen compact
    // style; the two are shown independently, so either one keeps the data current.
    [JsonIgnore] public IReadOnlyList<AppEntry> ConfiguredApps => Apps.Where(app => app.Enabled).ToList();
    [JsonIgnore] public bool QuotaWanted => UsagePage || ShowsSummary(Style);
    [JsonIgnore] public IReadOnlyList<AppEntry> EnabledApps => QuotaWanted ? ConfiguredApps : [];
    [JsonIgnore] public IReadOnlySet<ProviderId> RequiredProviders => EnabledApps.SelectMany(app => new[] { app.QuotaSource, app.UsageSource }).ToHashSet();
    [JsonIgnore] public IReadOnlyList<ProviderId> StatisticsProviders => EnabledApps.Select(app => app.UsageSource).Distinct().ToList();
    [JsonIgnore] public double MinimumWidth => Math.Clamp(Math.Max(440, AppPresets.MinimumColumnWidth * Math.Max(1, ConfiguredApps.Count) + 40), 440, 1200);
    public (double Width, double Height) PresetSize(PanelSize size)
    {
        var (width, height) = PanelSizes.For(size, ConfiguredApps.Count);
        return (Math.Max(width, MinimumWidth), height);
    }
    // The quick size whose width and height equal the current ones, if any.
    [JsonIgnore] public PanelSize? CurrentPreset => Enum.GetValues<PanelSize>().Cast<PanelSize?>().FirstOrDefault(size => PresetSize(size!.Value) == (Width, Height));
    public AppEntry? Entry(ProviderId id) => Apps.FirstOrDefault(app => app.QuotaSource == id);
    public AppEntry? Entry(Guid instanceId) => Apps.FirstOrDefault(app => app.InstanceId == instanceId);
    public bool Enabled(ProviderId id) => RequiredProviders.Contains(id);
    public void SetEnabled(ProviderId id, bool enabled)
    {
        var entries = Apps.Where(app => app.QuotaSource == id || app.UsageSource == id).ToList();
        if (entries.Count == 0) { if (enabled && Apps.Count < AppPresets.MaximumApps) Apps.Add(new AppEntry { QuotaSource = id, UsageSource = id }); }
        else foreach (var entry in entries) entry.Enabled = enabled;
    }
    // The behavior a context calls for: the app's own rule first, where it chooses one for that context, and the global
    // rules everywhere else. The app is the one the context was detected for; a null app, as on the desktop, has no rule.
    // A background full screen has no rules of its own: backgroundKind, the way the window behind the foreground one fills
    // the screen (Maximized or Borderless), picks the rule, the app's own first and the global one after it, as if that
    // window were in front. Without a kind nothing fills the screen, and the panel stays as it is.
    public WindowBehavior Behavior(ScreenContext context, string? processName = null, string? imagePath = null,
        ScreenContext? backgroundKind = null)
    {
        var kind = context == ScreenContext.BackgroundFullscreen ? backgroundKind ?? ScreenContext.Desktop : context;
        if (processName is not null || imagePath is not null)
            foreach (var rule in FullscreenRules)
                if (ScreenPolicy.Matches(rule, processName, imagePath))
                {
                    var own = kind switch
                    {
                        ScreenContext.Maximized => rule.Maximized, ScreenContext.Borderless => rule.Borderless,
                        ScreenContext.Exclusive => rule.Exclusive, _ => null
                    };
                    if (own is { } choice) return choice;
                    break;
                }
        return kind switch
        {
            ScreenContext.Maximized => Maximized, ScreenContext.Borderless => Borderless,
            ScreenContext.Exclusive => Exclusive, _ => WindowBehavior.Normal
        };
    }
    public DeckSettings Copy()
    {
        var copy = (DeckSettings)MemberwiseClone();
        copy._apps = _apps?.Select(app => app.Copy()).ToList();
        copy._readings = _readings?.Select(reading => reading?.Copy()!).ToList();
        copy.MonitorCustomColors = (MonitorCustomColors ?? new()).Copy();
        copy._notchOrder = [.. _notchOrder]; copy._capsuleOrder = [.. _capsuleOrder]; copy._pageOrder = [.. _pageOrder];
        copy.ClockGradient = [.. ClockGradient ?? [.. ClockColors.Sunset]];
        copy.DisplayMonitors = [.. DisplayMonitors ?? []];
        copy.ThemeChoices = (ThemeChoices ?? []).ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        copy.FullscreenRules = FullscreenRules.Select(rule => rule with { }).ToList();
        return copy;
    }
    public void Normalize()
    {
        // Saved per-theme choices: one entry per theme id as the panel names it, each with its lists present.
        ThemeChoices = (ThemeChoices ?? []).Where(pair => pair.Value is not null && pair.Key.Length > 0).GroupBy(pair => PanelThemes.NormalizeId(pair.Key))
            .ToDictionary(group => group.Key, group =>
            {
                var choices = group.Last().Value;
                choices.Apps ??= []; choices.ClockGradient ??= []; choices.ClockCustomColor ??= ClockColors.DefaultCustom;
                return choices;
            });
        var seen = new HashSet<Guid>();
        Apps = Apps.Where(app => app is not null && ProviderCatalog.Sources.Contains(app.QuotaSource)).Take(AppPresets.MaximumApps).ToList();
        foreach (var app in Apps)
        {
            if (app.InstanceId == Guid.Empty || !seen.Add(app.InstanceId))
            {
                do { app.InstanceId = Guid.NewGuid(); } while (!seen.Add(app.InstanceId));
            }
            app.Normalize();
        }
        Width = double.IsFinite(Width) ? Math.Clamp(Width, DeckPages.MinWidth, DeckPages.MaxWidth) : 760;
        Width = Math.Max(Width, MinimumWidth);
        Height = double.IsFinite(Height) ? Math.Clamp(Height, DeckPages.MinHeight, DeckPages.MaxHeight) : 240;
        MusicWidth = double.IsFinite(MusicWidth) ? Math.Clamp(MusicWidth, DeckPages.MinWidth, DeckPages.MaxWidth) : 440;
        MusicHeight = double.IsFinite(MusicHeight) ? Math.Clamp(MusicHeight, DeckPages.MinHeight, DeckPages.MaxHeight) : 200;
        MonitorWidth = double.IsFinite(MonitorWidth) ? Math.Clamp(MonitorWidth, DeckPages.MinWidth, DeckPages.MaxWidth) : MonitorSizes.Default.Width;
        MonitorHeight = double.IsFinite(MonitorHeight) ? Math.Clamp(MonitorHeight, DeckPages.MinHeight, DeckPages.MaxHeight) : MonitorSizes.Default.Height;
        NotifyWidth = double.IsFinite(NotifyWidth) ? Math.Clamp(NotifyWidth, DeckPages.MinWidth, DeckPages.MaxWidth) : 520;
        NotifyHeight = double.IsFinite(NotifyHeight) ? Math.Clamp(NotifyHeight, DeckPages.MinHeight, DeckPages.MaxHeight) : 220;
        ClipboardWidth = double.IsFinite(ClipboardWidth) ? Math.Clamp(ClipboardWidth, DeckPages.MinWidth, DeckPages.MaxWidth) : 560;
        ClipboardHeight = double.IsFinite(ClipboardHeight) ? Math.Clamp(ClipboardHeight, DeckPages.MinHeight, DeckPages.MaxHeight) : 240;
        ConvertWidth = double.IsFinite(ConvertWidth) ? Math.Clamp(ConvertWidth, DeckPages.MinWidth, DeckPages.MaxWidth) : 560;
        ConvertHeight = double.IsFinite(ConvertHeight) ? Math.Clamp(ConvertHeight, DeckPages.MinHeight, DeckPages.MaxHeight) : 260;
        NotifyMailHost = (NotifyMailHost ?? "").Trim();
        NotifyMailUser = (NotifyMailUser ?? "").Trim();
        NotifyMailPort = NotifyMailPort is > 0 and < 65_536 ? NotifyMailPort : 993;
        NotifyMailInterval = Math.Clamp(NotifyMailInterval, 15, 3600);
        ClipboardLimit = Math.Clamp(ClipboardLimit, 5, 100);
        // The AI usage page was removed from the panel; its switch stays only so old settings files still load.
        UsagePage = false;
        ClipboardHotkey ??= "";
        // Results are written beside the file they came from; an output folder from an older settings file is dropped.
        ConvertOutputFolder = "";
        ConvertFfmpegPath = (ConvertFfmpegPath ?? "").Trim();
        ConvertOfficePath = (ConvertOfficePath ?? "").Trim();
        PanelScale = PanelScales.Fit(PanelScale);
        // The readings' colors come from the panel theme alone now, so an older custom scheme comes back as the theme's colors.
        MonitorPalette = MonitorPalette.Theme;
        MonitorCustomColors ??= new();
        MonitorCustomColors.Normalize();
        CompactWarningColor = AppPresets.NormalizeColor(CompactWarningColor) is { Length: > 0 } warning ? warning : null;
        CompactCriticalColor = AppPresets.NormalizeColor(CompactCriticalColor) is { Length: > 0 } critical ? critical : null;
        MonitorInterval = Math.Clamp(MonitorInterval, 1, 2);
        if (!Enum.IsDefined(MonitorSort)) MonitorSort = ProcessSort.Cpu;
        if (!Enum.IsDefined(MonitorMain)) MonitorMain = MonitorMain.Cpu;
        MonitorGpu = string.IsNullOrWhiteSpace(MonitorGpu) ? null : MonitorGpu.Trim();
        MonitorGpuName = MonitorGpu is null || string.IsNullOrWhiteSpace(MonitorGpuName) ? null : MonitorGpuName.Trim();
        if (!Enum.IsDefined(MonitorProcessView)) MonitorProcessView = MonitorProcessView.Auto;
        if (!Enum.IsDefined(CompactSmoothing)) CompactSmoothing = CompactSmoothing.Ten;
        if (!Enum.IsDefined(CompactMonitor)) CompactMonitor = CompactMonitorMode.Full;
        CompactMonitorItems &= CompactMonitorItems.All;
        if (CompactMonitorItems == CompactMonitorItems.None) CompactMonitorItems = CompactMonitorItems.Cpu;
        CompactReadings = Core.CompactMonitor.Normalize(CompactReadings);
        Core.CompactMonitor.MoveColors(this);
        CompactMonitor = Core.CompactMonitor.Preset(CompactReadings) ?? CompactMonitorMode.Custom;
        CompactMonitorItems = Core.CompactMonitor.Shown(CompactReadings);
        if (!Enum.IsDefined(MusicText)) MusicText = CompactMusicText.None;
        if (!Enum.IsDefined(ClockStyle)) ClockStyle = ClockStyle.Minimal;
        if (!Enum.IsDefined(ClockColor)) ClockColor = ClockColor.White;
        ClockCustomColor = AppPresets.NormalizeColor(ClockCustomColor) is { Length: > 0 } custom ? custom : ClockColors.DefaultCustom;
        var stops = (ClockGradient ?? []).Select(AppPresets.NormalizeColor).ToList();
        ClockGradient = stops.Count == 3 && stops.All(stop => stop.Length > 0) ? stops : [.. ClockColors.Sunset];
        if (!Enum.IsDefined(DefaultPage)) DefaultPage = DefaultDeckPage.Last;
        if (!Enum.IsDefined(LastPage)) LastPage = DeckPage.Usage;
        OpenDelay = Math.Clamp(OpenDelay, 0, 2000);
        CloseDelay = Math.Clamp(CloseDelay, 0, 2000);
        AnimationDuration = Math.Clamp(AnimationDuration, 100, 1000);
        if (!Enum.IsDefined(Theme)) Theme = SettingsTheme.System;
        PanelTheme = PanelThemes.NormalizeId(PanelTheme) is { Length: > 0 } panelTheme ? panelTheme : PanelThemes.Dark;
        // Only the app's own three modes remain: a theme file's id from an older settings file comes back as the dark look.
        if (PanelTheme is not (PanelThemes.SystemId or PanelThemes.Dark or PanelThemes.Light)) PanelTheme = PanelThemes.Dark;
        // The settings window is drawn in the same mode as the panel, so the two never disagree.
        Theme = PanelTheme switch
        {
            PanelThemes.Light => SettingsTheme.Light, PanelThemes.Dark => SettingsTheme.Dark, _ => SettingsTheme.System
        };
        if (!double.IsFinite(ShellOpacity)) ShellOpacity = 1;
        ShellOpacity = Math.Clamp(ShellOpacity, .6, 1);
        // Two valid, different times or none: anything else keeps the theme's own hours.
        ThemeDaylight = (ThemeDaylight ?? "").Split('-', 2) is [var day, var night] && PanelThemes.ParseTimeOfDay(day) is { } d && PanelThemes.ParseTimeOfDay(night) is { } n && d != n
            ? PanelThemes.FormatDaylight(d, n) : "";
        Language = Loc.Normalize(Language);
        if (!Enum.IsDefined(Style)) Style = CompactStyle.Notch;
        static double Fit(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        static double Offset(double value) => Fit(value, -CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetLimit, CollapsedLimits.OffsetDefault);
        NotchWidth = Fit(NotchWidth, CollapsedLimits.NotchWidthMin, CollapsedLimits.NotchWidthMax, CollapsedLimits.NotchWidthDefault);
        NotchHeight = Fit(NotchHeight, CollapsedLimits.NotchHeightMin, CollapsedLimits.NotchHeightMax, CollapsedLimits.NotchHeightDefault);
        NotchOffsetX = Offset(NotchOffsetX);
        if (!Enum.IsDefined(NotchEdge)) NotchEdge = ScreenEdge.Top;
        if (!Enum.IsDefined(LineEdge)) LineEdge = ScreenEdge.Top;
        if (NotchAnchor is { } notchAnchor && !Enum.IsDefined(notchAnchor)) NotchAnchor = null;
        if (LineAnchor is { } lineAnchor && !Enum.IsDefined(lineAnchor)) LineAnchor = null;
        if (!Enum.IsDefined(GripDoubleClick)) GripDoubleClick = GripDoubleClick.Move;
        CapsuleWidth = Fit(CapsuleWidth, CollapsedLimits.CapsuleWidthMin, CollapsedLimits.CapsuleWidthMax, CollapsedLimits.CapsuleWidthDefault);
        CapsuleHeight = Fit(CapsuleHeight, CollapsedLimits.CapsuleHeightMin, CollapsedLimits.CapsuleHeightMax, CollapsedLimits.CapsuleHeightDefault);
        CapsuleOffsetX = Offset(CapsuleOffsetX);
        CapsuleTop = Fit(CapsuleTop, CollapsedLimits.CapsuleTopMin, CollapsedLimits.CapsuleTopMax, CollapsedLimits.CapsuleTopDefault);
        LineLength = Fit(LineLength, CollapsedLimits.LineLengthMin, CollapsedLimits.LineLengthMax, CollapsedLimits.LineLengthDefault);
        LineThickness = CollapsedGeometry.RoundLineThickness(LineThickness);
        LineTop = Fit(LineTop, CollapsedLimits.LineTopMin, CollapsedLimits.LineTopMax, CollapsedLimits.LineTopDefault);
        LineOffsetX = Offset(LineOffsetX);
        HideHotkey ??= "";
        ExpandHotkey ??= "";
        ResizeHotkey ??= "";
        if (QuickSize is { } quick && !Enum.IsDefined(quick)) QuickSize = null;
        if (!Enum.IsDefined(Maximized)) Maximized = WindowBehavior.Line;
        if (!Enum.IsDefined(Borderless)) Borderless = WindowBehavior.Hide;
        if (!Enum.IsDefined(Exclusive)) Exclusive = WindowBehavior.Hide;
        if (!Enum.IsDefined(Display)) Display = DisplayPlacement.Primary;
        // A file saved before MouseWheel had the page switch turned off as MouseWheelPages = false.
        if (MouseWheelPages is false) MouseWheel = MouseWheelAction.None;
        MouseWheelPages = null;
        if (!Enum.IsDefined(MouseWheel)) MouseWheel = MouseWheelAction.Pages;
        // A file saved before app rules carried plain exceptions: each becomes a rule that keeps the panel's own style in
        // all three contexts, which is what an exception did.
        if (FullscreenExceptions is { Count: > 0 } legacy)
        {
            var carried = legacy.Where(entry => entry is not null)
                .Select(entry => new FullscreenRule(ScreenPolicy.NormalizeName(entry.Name), ScreenPolicy.NormalizePath(entry.Path))
                    { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal });
            FullscreenRules = carried.Concat(FullscreenRules ?? []).ToList();
            FullscreenExceptions = [];
        }
        // A path-only entry takes its name from the executable, so it can also be matched when the path cannot be read.
        FullscreenRules = (FullscreenRules ?? []).OfType<FullscreenRule>()
            .Select(r => new FullscreenRule(ScreenPolicy.NormalizeName(r.Name), ScreenPolicy.NormalizePath(r.Path))
                { Maximized = Valid(r.Maximized), Borderless = Valid(r.Borderless), Exclusive = Valid(r.Exclusive) })
            .Select(r => r.Name.Length == 0 && r.Path.Length > 0 ? r with { Name = System.IO.Path.GetFileNameWithoutExtension(r.Path) } : r)
            .Where(r => r.Name.Length > 0 || r.Path.Length > 0)
            .DistinctBy(ScreenPolicy.Key, StringComparer.OrdinalIgnoreCase)
            .Take(AppPresets.MaximumFullscreenExceptions).ToList();
        static WindowBehavior? Valid(WindowBehavior? choice) => choice is null || Enum.IsDefined(choice.Value) ? choice : null;
        DisplayMonitors = (DisplayMonitors ?? []).OfType<ChosenDisplay>().Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .Select(d => new ChosenDisplay(d.Id.Trim(), (d.Name ?? "").Trim())).DistinctBy(d => d.Id).ToList();
        if (Display == DisplayPlacement.Chosen && DisplayMonitors.Count == 0) Display = DisplayPlacement.Primary;
    }
}

// appVersion is the running application's version; it defaults to the entry assembly's version.
// settings.json holds the settings of the newest version that saved it, plus "Snapshots": the settings each earlier
// version last saved, keyed by version. Upgrading keeps the older version's settings there; a downgraded TwelveIsland reads
// and saves only its own snapshot, so the newer version's settings stay as they were.
public sealed class SettingsStore(string directory, string? appVersion = null)
{
    private const string SnapshotsKey = "Snapshots";
    private const int KeptSnapshots = 5;
    private readonly string _appVersion = ApplicationVersion.Parse(appVersion ?? ApplicationVersion.Current).ToString(3);
    private JsonObject _snapshots = [];
    // The file as saved by a newer version; saving replaces only this version's snapshot in it.
    private JsonObject? _newerFile;
    // Settings this build does not know, from a file saved by the same version (another build of it, such as one with a feature
    // not merged yet). They are written back as they were, so running this build never deletes them. An older version's file
    // goes through SettingsMigrations instead, and its unknown keys are the ones the migrations retire.
    private JsonObject _unknown = [];
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    // Worded when shown, in the language the loaded settings choose.
    private Func<string>? _loadWarning;
    public string? LoadWarning => _loadWarning?.Invoke();
    // True when the last Load found no settings file, or none it could read, and returned defaults.
    public bool IsFirstStart { get; private set; }
    // Without saved settings the interface follows the Windows display language; saved settings keep their language.
    private DeckSettings FirstStart() { IsFirstStart = true; return new() { Language = Loc.SystemLanguage() }; }
    public DeckSettings Load()
    {
        _snapshots = []; _newerFile = null; _unknown = []; IsFirstStart = false; _loadWarning = null;
        try
        {
            if (!File.Exists(FilePath)) return FirstStart();
            var text = File.ReadAllText(FilePath);
            var root = SettingsMigrations.Parse(text);
            var running = ApplicationVersion.Parse(_appVersion);
            var saved = SettingsMigrations.VersionOf(root);
            var snapshots = root[SnapshotsKey] as JsonObject ?? [];
            root.Remove(SnapshotsKey);
            if (saved > running)
            {
                _newerFile = SettingsMigrations.Parse(text);
                var own = snapshots.Where(pair => pair.Value is JsonObject && ApplicationVersion.Parse(pair.Key) <= running)
                    .OrderByDescending(pair => ApplicationVersion.Parse(pair.Key)).Select(pair => (JsonObject)pair.Value!.DeepClone()).FirstOrDefault();
                // A file from a higher version is read for what this version knows, and the reader is not warned: the version was
                // reset on purpose, and a banner about a version nobody sees any more only gets in the way.
                _loadWarning = null;
                return SettingsMigrations.Read(own ?? root, running, out _);
            }
            _snapshots = snapshots;
            // Keep what an older version saved so that returning to it restores its settings. Files saved before
            // versioning cannot be told apart and are not kept.
            if (saved < running && saved > new Version(0, 0, 0)) _snapshots[saved.ToString(3)] = root.DeepClone();
            if (saved == running)
            {
                var known = JsonSerializer.SerializeToNode(new DeckSettings())!.AsObject();
                _unknown = new JsonObject(root.Where(pair => !known.ContainsKey(pair.Key)).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
            }
            return SettingsMigrations.Read(root, running, out _);
        }
        // Any exception, since a faulty migration step can throw anything. Defaults replace the file at the next save,
        // so the file is copied aside first and can still be restored by hand.
        catch (Exception)
        {
            var kept = KeepUnreadable();
            _loadWarning = kept is null
                ? () => Loc.T("无法读取已保存的设置，本次使用默认值。", "The saved settings could not be read. Default settings are used this time.")
                : () => Loc.T($"无法读取已保存的设置，本次使用默认值。原文件已另存为 {kept}。", $"The saved settings could not be read. Default settings are used this time. The original file is kept as {kept}.");
            return FirstStart();
        }
    }

    // Copies an unreadable settings file next to it and returns the copy's name, or null when there is nothing to copy.
    private string? KeepUnreadable()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var name = $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.Copy(FilePath, Path.Combine(DirectoryPath, name), true);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public void Save(DeckSettings settings) => Write(Serialize(settings));

    // The file's text for these settings. It normalizes them and reads this store's state, so it runs where the settings live;
    // Write may then run on any thread, one call at a time.
    public string Serialize(DeckSettings settings)
    {
        settings.AppVersion = _appVersion;
        settings.Normalize();
        var own = JsonSerializer.SerializeToNode(settings)!.AsObject();
        foreach (var (key, value) in _unknown) if (!own.ContainsKey(key)) own[key] = value?.DeepClone();
        JsonObject file;
        if (_newerFile is not null)
        {
            file = (JsonObject)_newerFile.DeepClone();
            var snapshots = file[SnapshotsKey] as JsonObject ?? [];
            file.Remove(SnapshotsKey);
            snapshots[_appVersion] = own;
            file[SnapshotsKey] = snapshots;
        }
        else
        {
            file = own;
            var running = ApplicationVersion.Parse(_appVersion);
            var kept = _snapshots.Where(pair => pair.Value is JsonObject && ApplicationVersion.Parse(pair.Key) < running)
                .OrderByDescending(pair => ApplicationVersion.Parse(pair.Key)).Take(KeptSnapshots)
                .Select(pair => KeyValuePair.Create(pair.Key, pair.Value!.DeepClone())).ToList();
            if (kept.Count > 0) file[SnapshotsKey] = new JsonObject(kept!);
        }
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Write(string text)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, FilePath, true);
    }
}
