using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// Asks before KuGou's libcef.dll is changed (KugouInstall): what it adds, what happens to KuGou, and the debugging port, in a
// few lines; the details are in the user documentation. Laid out like NeteaseControlWindow.
internal sealed class KugouControlWindow : Window
{
    public KugouControlWindow(SettingsTheme theme)
    {
        var palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/NeteaseControlResources.xaml", UriKind.Relative) });
        palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        Title = Loc.T("启用酷狗完整控制", "Turn on KuGou full control"); Width = 480; SizeToContent = SizeToContent.Height;
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
        TextBlock Add(string value, double top, double size = 13, string? color = null, bool bold = false, Panel? into = null)
        {
            var text = Text(value, size, color ?? palette.Secondary, bold, bold ? 20 : 21); text.Margin = new Thickness(0, top, 0, 0);
            (into ?? body).Children.Add(text); return text;
        }
        body.Children.Add(Text(Title, 20, palette.Primary, true, 28));
        Add(Loc.T("启用后，面板显示酷狗的播放进度，歌词随进度滚动。", "When on, the panel shows KuGou's position and scrolls the lyrics with it."), 10);
        Add(Loc.T("•  酷狗正在运行时会重启一次，正在播放的歌曲会停止", "•  A running KuGou restarts once, and the playing song stops"), 10, color: palette.Primary);
        {
            // How the file is changed, behind an info icon drawn like the settings window's.
            var label = Text(Loc.T("•  会修改酷狗的 libcef.dll，之后酷狗每次启动都是完整控制", "•  KuGou's libcef.dll is changed; KuGou then starts with full control every time"), 13, palette.Primary, lineHeight: 21);
            var info = new Border { Width = 22, Height = 21, Margin = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Help, Focusable = true,
                Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 6.99,1 M 7,6.5 V 10 M 7,4 V 4.1"), Width = 14, Height = 14,
                    Stroke = UI.Brush(palette.Tertiary), StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            string how = Loc.T("只替换文件中 9 处原有的字节（共约 50 字节），让酷狗启动时打开调试端口。不添加内容，文件大小不变；停用时写回原来的字节。",
                "Only 9 existing places in the file are replaced (about 50 bytes in all), so that KuGou opens its debugging port when it starts. Nothing is added and the file keeps its size; turning it off writes the original bytes back.");
            info.ToolTip = new ToolTip { Content = new TextBlock { Text = how, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
            ToolTipService.SetInitialShowDelay(info, 0);
            AutomationProperties.SetName(info, Loc.T("修改方式说明", "How the file is changed")); AutomationProperties.SetHelpText(info, how);
            body.Children.Add(new HelpHeading(label, info) { HorizontalAlignment = HorizontalAlignment.Left });
        }

        var notice = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new System.Windows.Shapes.Path { Width = 15, Height = 15, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M8,1.5 L13.5,3.6 V7.4 C13.5,10.7 11.2,13.3 8,14.5 C4.8,13.3 2.5,10.7 2.5,7.4 V3.6 Z M5.6,8 L7.4,9.8 L10.6,6.4"),
            Stroke = UI.Brush(palette.Secondary), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(Text(Loc.T("只在您信任的电脑上启用", "Turn it on only on a PC you trust"), 13.5, palette.Primary, true)); notice.Children.Add(heading);
        Add(Loc.T("酷狗会打开一个本机调试端口，其他程序可能借此操控酷狗。", "KuGou opens a local debugging port that other programs could use to control it."), 6, into: notice);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 13), Margin = new Thickness(0, 20, 0, 0),
            Background = UI.Brush(palette.Inset), BorderBrush = UI.Brush(palette.RowDivider), BorderThickness = new Thickness(1), Child = notice });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        string confirm = Loc.T("启用", "Turn on");
        var cancel = new Button { Content = Loc.T("取消", "Cancel"), Style = (Style)FindResource("ControlDialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var enable = new Button { Content = confirm, Style = (Style)FindResource("ControlDialogPrimaryButton") };
        AutomationProperties.SetName(cancel, Loc.T("取消", "Cancel")); AutomationProperties.SetName(enable, confirm);
        cancel.Click += (_, _) => DialogResult = false; enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(enable); body.Children.Add(buttons);
        Content = new Border { Background = UI.Brush(palette.Surface), Padding = new Thickness(28, 24, 28, 24), Child = body };
        // Cancelling stays the safe initial keyboard action.
        Loaded += (_, _) => cancel.Focus();
    }
}
