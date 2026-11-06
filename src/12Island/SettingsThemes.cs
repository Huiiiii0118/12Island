using System.Windows;
using System.Windows.Controls;
using TwelveIsland.Core;

namespace TwelveIsland;

// The "面板主题" page: one look in three modes — follow Windows, dark, light — and one opacity for the whole app. The colors
// themselves have no pickers: the palette is the app's own, and the mode decides which of its two looks is shown, so the panel
// keeps one appearance instead of a set of themes to manage.
public sealed partial class SettingsWindow
{
    internal const int ThemePage = 8, MonitorSettingsPage = 7;

    private void ThemesPage()
    {
        SectionTitle(Loc.T("外观模式", "Appearance"),
            help: () => Loc.T("跟随系统：随 Windows 的“应用模式”在深色与浅色之间切换；深色与浅色：固定使用其中一种。",
                "System follows the Windows app mode between dark and light; Dark and Light keep one of them."));
        Group(MusicChoice(Loc.T("模式", "Mode"), ModeOf(S.PanelTheme),
            [(AppearanceMode.System, Loc.T("跟随系统", "System")), (AppearanceMode.Dark, Loc.T("深色", "Dark")), (AppearanceMode.Light, Loc.T("浅色", "Light"))],
            value =>
            {
                Change(s => s.PanelTheme = value switch
                {
                    AppearanceMode.Dark => PanelThemes.Dark, AppearanceMode.Light => PanelThemes.Light, _ => PanelThemes.SystemId
                });
                _app.ApplyPanelTheme(reload: true);
            }));
        SectionTitle(Loc.T("透明度", "Opacity"));
        Group(SliderRow(Loc.T("界面透明度", "Window opacity"), Math.Round(S.ShellOpacity * 100), 60, 100, 5, "%",
            value => { Change(s => s.ShellOpacity = Math.Clamp(value / 100, .6, 1)); _app.ApplyShellOpacity(); },
            help: Loc.T("面板、设置窗口与所有小窗口一起变淡，文字保持清晰。", "Fades the panel, the settings window and every small window together; the text stays readable.")));
    }

    // The panel theme changed outside this page (Windows switched its mode): the page shows the mode now in force, and the
    // window's own colors are taken again, without rebuilding the whole window.
    internal void PanelThemeChanged(bool onlyThemePage = false)
    {
        if (onlyThemePage && _page != ThemePage) return;
        RebuildTheme();
        if (_page == ThemePage) ShowPage(ThemePage);
    }

    // A line of warning text: the amber a notice or a stopped reading is written in.
    private string CautionColor => _palette.Dark ? "#E8B04B" : "#B7791F";

    private enum AppearanceMode { System, Dark, Light }

    private static AppearanceMode ModeOf(string theme) => PanelThemes.NormalizeId(theme) switch
    {
        PanelThemes.Light => AppearanceMode.Light,
        PanelThemes.Dark => AppearanceMode.Dark,
        _ => AppearanceMode.System
    };
}
