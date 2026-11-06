using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// What confirming does: restart a player in normal mode, start one that is not running,
// restart one that already has full control so the connection is made again, or switch on
// the setting that restarts every player started the normal way.
internal enum NeteaseControlAction { Restart, Start, Reconnect, Auto }

internal sealed class NeteaseControlWindow : Window
{
    internal static string SecurityNotice => Loc.T("完整控制依赖网易云的调试端口。端口打开期间，这台电脑上的其他程序和浏览器网页有可能借此操控网易云，读取它显示的内容和登录状态。端口会一直开到网易云以普通方式重新启动为止，退出本程序或连接失败都不会关闭它。",
        "Full control relies on the debugging port of NetEase Cloud Music. While the port is open, other programs and web pages on this PC could use it to control NetEase Cloud Music and read what it shows and its sign-in state. The port stays open until NetEase Cloud Music is started again the normal way; quitting this app or a failed connection does not close it.");
    public NeteaseControlWindow(SettingsTheme theme, NeteaseControlAction action = NeteaseControlAction.Restart)
    {
        var palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/NeteaseControlResources.xaml", UriKind.Relative) });
        palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        bool auto = action == NeteaseControlAction.Auto;
        Title = auto ? Loc.T("自动启用网易云完整控制", "Enable full control of NetEase Cloud Music automatically") : Loc.T("启用网易云完整控制", "Enable full control of NetEase Cloud Music"); Width = 480; SizeToContent = SizeToContent.Height;
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
        // Same voice as the settings window: plain sentences, neutral surfaces, the accent only on the primary action.
        body.Children.Add(Text(Title, 20, palette.Primary, true, 28));
        var lead = Text(action switch
        {
            NeteaseControlAction.Start => Loc.T("启用完整控制后，可以在面板里拖动网易云音乐的进度、切换随机与循环播放，进度也会实时同步。",
                "With full control, you can drag the playback position of NetEase Cloud Music in the panel and switch shuffle and repeat, and the position stays in sync."),
            NeteaseControlAction.Auto => Loc.T("打开后，每当网易云音乐以普通方式启动，12 island 会自动将它重启为完整控制。完整控制下，可以在面板里拖动进度、切换随机与循环播放，进度也会实时同步。",
                "Once this is on, whenever NetEase Cloud Music is started the normal way, 12 island restarts it with full control. With full control, you can drag the playback position in the panel and switch shuffle and repeat, and the position stays in sync."),
            NeteaseControlAction.Reconnect => Loc.T("网易云音乐已处于完整控制。继续操作会重新启动网易云音乐，并重新建立连接。",
                "NetEase Cloud Music already has full control. Continuing restarts NetEase Cloud Music and connects to it again."),
            _ => Loc.T("本程序现在只能读取网易云正在播放的内容。启用完整控制后，可以在面板里拖动进度、切换随机与循环播放，进度也会实时同步。",
                "The app can now only read what NetEase Cloud Music is playing. With full control, you can drag the playback position in the panel and switch shuffle and repeat, and the position stays in sync.")
        }, 13, palette.Secondary, lineHeight: 21);
        lead.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(lead);

        bool start = action == NeteaseControlAction.Start;
        string confirm = auto ? Loc.T("打开", "Turn on") : start ? Loc.T("启动并启用", "Start and enable") : Loc.T("重启并启用", "Restart and enable");
        var restartHeading = Text(auto ? Loc.T("网易云每次启动后会重启一次", "NetEase Cloud Music restarts once each time it starts")
            : start ? Loc.T("会启动网易云音乐", "NetEase Cloud Music will start") : Loc.T("会重启一次网易云", "NetEase Cloud Music will restart once"), 13.5, palette.Primary, true); restartHeading.Margin = new Thickness(0, 22, 0, 0); body.Children.Add(restartHeading);
        var restart = Text(auto ? Loc.T("网易云的窗口会关闭再打开，并回到原来的歌曲和播放位置，通常需要几秒钟。网易云如果此刻正以普通方式运行，点击“打开”后会立即重启一次。",
                "Its window closes and opens again and returns to the same song and position, which usually takes a few seconds. If NetEase Cloud Music is running the normal way now, it restarts once as soon as you click \"Turn on\".")
            : start ? Loc.T("点击“启动并启用”后，网易云音乐会以完整控制方式打开。整个过程通常需要几秒钟。",
                "After you click \"Start and enable\", NetEase Cloud Music opens with full control. This usually takes a few seconds.")
            : Loc.T("点击“重启并启用”后，网易云音乐会先关闭再重新打开，然后自动回到当前这首歌和播放位置；如果此刻正在播放，会接着播放。整个过程通常需要几秒钟。",
                "After you click \"Restart and enable\", NetEase Cloud Music closes and opens again, then returns to the current song and position; if it is playing now, it keeps playing. This usually takes a few seconds."), 13, palette.Secondary, lineHeight: 21);
        restart.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(restart);

        var notice = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        var shield = new System.Windows.Shapes.Path { Width = 15, Height = 15, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M8,1.5 L13.5,3.6 V7.4 C13.5,10.7 11.2,13.3 8,14.5 C4.8,13.3 2.5,10.7 2.5,7.4 V3.6 Z M5.6,8 L7.4,9.8 L10.6,6.4"),
            Stroke = UI.Brush(palette.Secondary), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(shield); heading.Children.Add(Text(Loc.T("只在您信任的电脑上启用", "Enable it only on a PC you trust"), 13.5, palette.Primary, true)); notice.Children.Add(heading);
        var warning = Text(auto ? SecurityNotice + Loc.T("打开此开关后，网易云每次启动都会打开调试端口，直到关闭此开关。",
            " With this setting on, the port is opened each time NetEase Cloud Music starts, until the setting is turned off.") : SecurityNotice, 13, palette.Secondary, lineHeight: 21); warning.Margin = new Thickness(0, 6, 0, 0); notice.Children.Add(warning);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 13), Margin = new Thickness(0, 20, 0, 0),
            Background = UI.Brush(palette.Inset), BorderBrush = UI.Brush(palette.RowDivider), BorderThickness = new Thickness(1), Child = notice });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = Loc.T("取消", "Cancel"), Style = (Style)FindResource("ControlDialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var enable = new Button { Content = confirm, Style = (Style)FindResource("ControlDialogPrimaryButton") };
        AutomationProperties.SetName(cancel, Loc.T("取消", "Cancel")); AutomationProperties.SetName(enable, confirm);
        cancel.Click += (_, _) => DialogResult = false; enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(enable); body.Children.Add(buttons);
        Content = new Border { Background = UI.Brush(palette.Surface), Padding = new Thickness(28, 24, 28, 24), Child = body };
        // Keep cancellation as the safe initial keyboard action without drawing
        // the focus indicator that is reserved for Tab navigation.
        Loaded += (_, _) => cancel.Focus();
    }
}
