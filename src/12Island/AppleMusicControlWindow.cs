using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TwelveIsland.Core;

namespace TwelveIsland;

// Explains Apple Music's full control before it is enabled: what it adds, how it works, and what it does to the
// Apple Music window. Laid out like NeteaseControlWindow.
internal sealed class AppleMusicControlWindow : Window
{
    public AppleMusicControlWindow(SettingsTheme theme)
    {
        var palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/NeteaseControlResources.xaml", UriKind.Relative) });
        palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        Title = Loc.T("启用 Apple Music 完整控制", "Enable full control of Apple Music"); Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        FontFamily = UI.PanelFont; Background = UI.Brush(palette.Surface); Foreground = UI.Brush(palette.Primary);
        Native.SettingsChrome.Apply(this); ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(this, palette.Dark);
        var body = new StackPanel();
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        TextBlock Text(string value, double size, string color, bool bold = false, double lineHeight = 20)
        {
            var text = UI.Line(value, size, color); text.TextWrapping = TextWrapping.Wrap; text.LineHeight = lineHeight;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            if (bold) text.FontWeight = FontWeights.SemiBold;
            return text;
        }
        TextBlock Add(string value, double top, double size = 13, string? color = null, bool bold = false)
        {
            var text = Text(value, size, color ?? palette.Secondary, bold, bold ? 20 : 21); text.Margin = new Thickness(0, top, 0, 0);
            body.Children.Add(text); return text;
        }
        body.Children.Add(Text(Title, 20, palette.Primary, true, 28));
        Add(Loc.T("Apple Music 只向 Windows 提供播放、暂停和切换歌曲的控制。启用完整控制后，本程序会通过 Windows 的界面自动化功能，直接操作 Apple Music 窗口中的按钮和进度条，在面板中增加以下三项功能：",
            "Apple Music only lets Windows play, pause and change songs. With full control, the app uses the UI Automation feature of Windows to operate the buttons and the progress bar in the Apple Music window directly, and adds three functions to the panel:"), 10);
        Add(Loc.T("•  拖动播放进度\n•  开启或关闭随机播放\n•  切换重复模式：关闭、全部重复、单曲重复",
            "•  Drag the playback position\n•  Turn shuffle on or off\n•  Switch the repeat mode: off, repeat all, repeat one"), 8, color: palette.Primary);

        Add(Loc.T("使用前请了解", "Before you enable it"), 22, 13.5, palette.Primary, true);
        Add(Loc.T("•  如果 Apple Music 的主窗口没有最小化，使用这三项功能时，它的窗口会被带到最前面。\n•  这三项功能需要 Apple Music 的主窗口存在。关闭主窗口会退出 Apple Music，功能也随之不可用。\n•  Apple Music 更新后，如果窗口中的按钮发生变化，这三项功能可能暂时无法使用。",
            "•  If the Apple Music main window is not minimized, using these functions brings it to the front.\n•  These functions need the Apple Music main window. Closing it quits Apple Music, and the functions stop working.\n•  If an Apple Music update changes the buttons in its window, these functions may stop working for a while."), 6);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        string confirm = Loc.T("启用", "Enable");
        var cancel = new Button { Content = Loc.T("取消", "Cancel"), Style = (Style)FindResource("ControlDialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var enable = new Button { Content = confirm, Style = (Style)FindResource("ControlDialogPrimaryButton") };
        AutomationProperties.SetName(cancel, Loc.T("取消", "Cancel")); AutomationProperties.SetName(enable, confirm);
        cancel.Click += (_, _) => DialogResult = false; enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(enable); body.Children.Add(buttons);
        Content = new Border { Background = UI.Brush(palette.Surface), Padding = new Thickness(28, 24, 28, 24), Child = body };
        Loaded += (_, _) => cancel.Focus();
    }
}
