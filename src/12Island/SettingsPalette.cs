using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TwelveIsland.Core;

namespace TwelveIsland;

// Settings own their palette; the floating usage panel deliberately keeps its existing visual design.
internal sealed record SettingsPalette(bool Dark)
{
    public string Surface => Dark ? "#141416" : "#FCFCFD";
    public string Sidebar => Dark ? "#0D0D0F" : "#F3F3F5";
    public string Primary => Dark ? "#F1F1F4" : "#202027";
    public string Secondary => Dark ? "#A5A5AF" : "#62626F";
    // At least 4.5:1 on the surface and on inset fields, where units and hints in this color sit.
    public string Tertiary => Dark ? "#8E8E9A" : "#6B6B78";
    public string Divider => Dark ? "#2D2D33" : "#E0E0E6";
    public string RowDivider => Dark ? "#242429" : "#EDEDF1";
    public string Inset => Dark ? "#25252B" : "#EFF0F4";
    public string Input => Dark ? "#202026" : "#FFFFFF";
    public string Popup => Dark ? "#222227" : "#FCFCFD";
    public string Border => Dark ? "#42424B" : "#CED1DA";
    // The edge of what takes input (text and number fields, drop-downs, a switch's off track): at least 3:1 against the surface,
    // the inset and the input fill (WCAG 1.4.11, SW-07). Frames and dividers keep the lighter Border.
    public string InputBorder => Dark ? "#6F6F79" : "#848997";
    public string Accent => Dark ? "#88B0EE" : "#347BDF";
    // Text that says something failed or is unavailable.
    public string Negative => Dark ? "#F0A6AA" : "#B12E3A";
    public void Apply(ResourceDictionary resources)
    {
        void Brush(string key, string color) => resources[key] = UI.Brush(color);
        Brush("SettingsTextPrimary", Primary); Brush("SettingsTextSecondary", Secondary);
        Brush("SettingsFillHover", Dark ? "#25252B" : "#E8E9EF");
        Brush("SettingsFillPressed", Dark ? "#363640" : "#DDDFE8");
        Brush("SettingsFillSelected", Dark ? "#41414A" : "#FFFFFF");
        Brush("SettingsNavSelected", Dark ? "#27272D" : "#FFFFFF");
        Brush("SettingsAccent", Dark ? "#348AEB" : "#2374DE");
        // The fill of buttons with white text on the accent: at least 4.5:1 for that text, at rest and under the pointer, in both
        // modes (the dark mode's own accent is too light for it).
        Brush("SettingsAccentFill", "#2374DE");
        Brush("SettingsAccentHover", "#1964C7");
        Brush("SettingsSliderAccent", Accent); Brush("SettingsTrackBrush", Dark ? "#42424A" : "#D3D6DF");
        Brush("SettingsBorder", Border); Brush("SettingsInput", Input); Brush("SettingsPopup", Popup);
        Brush("SettingsSwatchBorder", Dark ? "#65656F" : "#B0B3BE");
        Brush("SettingsScrollThumb", Dark ? "#62626D" : "#C4C7D0");
        Brush("SettingsScrollThumbHover", Dark ? "#858591" : "#A4A9B5");
        Brush("SettingsScrollThumbPressed", Dark ? "#A5A5AF" : "#8C929F");
        resources[SystemParameters.VerticalScrollBarWidthKey] = 12d;
        // WPF Track uses half this metric for its minimum thumb length. Setting
        // Thumb.MinHeight alone makes the thumb draw outside Track's allotted slot.
        resources[SystemParameters.VerticalScrollBarButtonHeightKey] = 56d;
        Brush("SettingsInset", Inset); Brush("SettingsSurface", Surface);
        Brush("SettingsSwitchOff", InputBorder); Brush("SettingsInputBorder", InputBorder);
        Brush("SettingsError", Dark ? "#F0A6AA" : "#B12E3A");
    }
    public static bool IsDark(SettingsTheme theme)
    {
        if (theme != SettingsTheme.System) return theme == SettingsTheme.Dark;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
}
