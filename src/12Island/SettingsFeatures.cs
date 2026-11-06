using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// The settings of the three panel pages the app adds: notifications, the clipboard history and file conversion. Each has a page
// of its own in the settings window's navigation.
public sealed partial class SettingsWindow
{
    private void NotifySettings()
    {
        SectionTitle(Loc.T("监听的软件", "Programs watched"), help: () => Loc.T(
            "只看这些软件的图标是否在闪动：任务栏、Dock（如 Nexus）上的图标闪动，就是有新消息；打开该软件、或图标停止闪动，即视为已读。只读取通知本身，不读取聊天记录，也不上传任何内容。",
            "Only the icons of these programs are watched: a flash on the taskbar or on a dock (Nexus and the like) means a message waits; opening the program, or the flashing stopping, counts as reading it. Only the notice is read — never a chat history — and nothing is uploaded."));
        Group(Toggle(Loc.T("QQ", "QQ"), S.NotifyQq, value => Change(s => s.NotifyQq = value)),
            Toggle(Loc.T("微信", "WeChat"), S.NotifyWeChat, value => Change(s => s.NotifyWeChat = value)),
            Toggle(Loc.T("其他聊天与办公软件", "Other chat and office programs"), S.NotifyApps, value => Change(s => s.NotifyApps = value),
                help: Loc.T("钉钉、飞书、企业微信、TIM、Teams、Slack、Discord、Telegram、WhatsApp、Zoom、腾讯会议、Notion 等；Outlook、Foxmail 等邮箱客户端归在下面的邮件里。",
                    "DingTalk, Feishu, WeCom, TIM, Teams, Slack, Discord, Telegram, WhatsApp, Zoom, Tencent Meeting, Notion and the like; Outlook, Foxmail and other mail clients count as mail, below.")),
            Toggle(Loc.T("邮箱新邮件", "New mail"), S.NotifyMail, value => Change(s => s.NotifyMail = value),
                help: Loc.T("直接连接邮箱读取新邮件（IMAP），不依赖邮件客户端是否在运行。需要邮箱开启 IMAP 并生成授权码。",
                    "Reads new mail over IMAP, whether or not a mail client is running. The mailbox needs IMAP turned on and an app password.")));

        SectionTitle(Loc.T("提示方式", "How it shows"), help: () => Loc.T(
            "有未读消息时，收起栏一直保持胶囊，显示各软件的图标和条数；点击胶囊打开该软件后，提示才会消失。",
            "While anything waits unread the collapsed bar stays a capsule with each program's icon and count; it goes away only when you click it to open that program."));
        Group(Row(Loc.T("胶囊条数", "Count on the capsule"), TextLine(Loc.T("每闪动一次计一条，最多显示 4 个软件", "One per flash, up to four programs"), 12, TextSecondary)),
            MusicChoice(Loc.T("提醒动态", "Attention"), S.NotifyAttention,
                new[] { (NotifyAttention.None, Loc.T("无", "None")), (NotifyAttention.Bounce, Loc.T("弹跳", "Bounce")), (NotifyAttention.Sonic, Loc.T("音爆", "Sonic")) },
                v => Change(s => s.NotifyAttention = v),
                help: Loc.T("新消息到达时胶囊的一次性提醒：弹跳让胶囊向下跳动回弹一次；音爆让一圈波纹从胶囊向外扩散，胶囊同时向外一胀。",
                    "A one-shot attention when a notice arrives: Bounce drops the capsule once and lets it settle; Sonic sends a ring bursting from the capsule while it punches outward.")));
        SectionTitle(Loc.T("邮箱接收", "Mailbox"), help: () => Loc.T(
            "填好地址和授权码后，程序每隔一段时间直接连到邮箱服务器查看是否有新邮件。授权码不是登录密码，在邮箱的网页设置里生成。",
            "With the address and app password filled in, the mailbox is checked directly at the chosen interval. The app password is not the login password; generate it in the mailbox's web settings."));
        Group(Row(Loc.T("邮箱地址", "Address"), MailField(S.NotifyMailUser, value => Change(s => s.NotifyMailUser = value), "name@qq.com")),
            Row(Loc.T("授权码", "App password"), MailField(MailSecret.Read(), value => _app.SetMailPassword(value), Loc.T("邮箱设置中生成的授权码", "The password generated in the mailbox's settings"), password: true)),
            Row(Loc.T("邮件服务器", "Mail server"), MailServerField()),
            SliderRow(Loc.T("检查间隔", "Check every"), S.NotifyMailInterval, 15, 600, 15, Loc.T("秒", "s"), value => Change(s => s.NotifyMailInterval = Math.Clamp((int)value, 15, 600))),
            Row(Loc.T("立即检查", "Check now"), ActionButton(Loc.T("检查邮箱", "Check mail"), () => _app.CheckMailNow())));
        SectionTitle(Loc.T("页面尺寸", "Page size"));
        Group(Row(Loc.T("通知页大小", "Notifications page"), TextLine(Loc.T("与剪贴板页共用，在“外观 › 展开面板”中调整", "Shared with the clipboard page; set under Appearance › Expanded panel"), 12, TextSecondary)));
    }

    private void ClipboardSettings()
    {
        SectionTitle(Loc.T("剪贴板历史", "Clipboard history"), help: () => Loc.T(
            "复制过的文字、图片和文件会按时间保存在本机，只有这台电脑能读取。密码等敏感内容复制后不会自动清除，可随时删除单条或清空。",
            "Copied text, pictures and files are kept on this computer in time order, readable only here. Sensitive copies such as passwords are not hidden automatically; an entry can be deleted, or the history cleared, at any time."));
        Group(Toggle(Loc.T("记录剪贴板", "Record the clipboard"), S.ClipboardEnabled, value => Change(s => s.ClipboardEnabled = value),
                help: Loc.T("关闭后不再记录新的复制内容，已经保存的记录仍然保留。", "When off, nothing new is recorded; what is already kept stays.")),
            Toggle(Loc.T("同时记录图片", "Record pictures too"), S.ClipboardImages, value => Change(s => s.ClipboardImages = value),
                help: Loc.T("关闭时只记录文字和文件。", "When off, only text and files are recorded.")),
            SliderRow(Loc.T("保留条数", "Entries kept"), S.ClipboardLimit, 5, 100, 5, Loc.T("条", "entries"), value => Change(s => s.ClipboardLimit = Math.Clamp((int)value, 5, 100)),
                help: Loc.T("超出后按时间删掉最早的记录；同一条内容再次复制只会移到最前，不会多出一条。",
                    "Beyond this the oldest entries go; copying the same thing again moves it to the top instead of adding another.")));
        SectionTitle(Loc.T("清理", "Clean up"));
        Group(Row(Loc.T("清空记录", "Clear the history"), ActionButton(Loc.T("清空（保留收藏）", "Clear (keep starred)"), () => _app.Clips?.Clear(favoritesToo: false)),
                help: () => Loc.T("删掉未收藏的全部记录。", "Removes every entry that is not starred.")),
            Row(Loc.T("全部删除", "Delete everything"), ActionButton(Loc.T("全部删除", "Delete all"), () => _app.Clips?.Clear(favoritesToo: true)),
                help: () => Loc.T("包括收藏的记录，全部删掉。", "Removes every entry, starred ones included.")));
    }

    private void ConvertSettings()
    {
        SectionTitle(Loc.T("转换能力", "What it converts"), help: () => Loc.T(
            "所有格式都在这台电脑上转换，不联网，也不需要安装别的软件。图片、文档、表格、文本、音频、视频，以及转换成 PDF，都由程序自己完成（音视频用的是随程序附带的 FFmpeg）。只有旧版 .doc / .xls 和 PDF 转 Word 需要装了 LibreOffice 才有。",
            "Every format is converted on this machine, with no internet and nothing to install. Pictures, documents, spreadsheets, text, sound, video and PDF are all done by the app itself, using the FFmpeg it carries. Only old .doc/.xls files and PDF to Word need LibreOffice installed."));
        Group(
            Row(Loc.T("FFmpeg（可选，内置）", "FFmpeg (built in, optional)"), PathField(S.ConvertFfmpegPath, value => Change(s => s.ConvertFfmpegPath = value), Loc.T("留空即用内置的", "Leave empty to use the built-in one")),
                help: () => Loc.T("音频、视频转换用它；程序里已经带了一份，填这里的路径则改用你自己装的那份。",
                    "Converts sound and video. The app carries its own; a path here uses the copy you installed instead.")),
            Row(Loc.T("LibreOffice（可选）", "LibreOffice (optional)"), PathField(S.ConvertOfficePath, value => Change(s => s.ConvertOfficePath = value), "soffice.exe"),
                help: () => Loc.T("只有旧版 .doc / .xls 和 PDF 转 Word 需要它，其他格式不用装。",
                    "Only old .doc/.xls and PDF to Word need it; other formats do not.")),
            Row(Loc.T("检查到的程序", "Found"), TextLine(ConverterStatus(), 12, TextSecondary)));
        SectionTitle(Loc.T("输出", "Output"));
        Group(Row(Loc.T("输出位置", "Output folder"), TextLine(Loc.T("在展开的格式转换页里逐个选择", "Chosen for each file on the conversion page"), 12, TextSecondary),
            help: () => Loc.T("转换结果放在哪里可以在转换页里选；原文件不会被改动。",
                "Where the result goes is chosen on the conversion page; the original file is never changed.")));
    }

    // A path to a program, with the system's file chooser beside it; the field writes what it holds when it loses the keyboard.
    private FrameworkElement PathField(string value, Action<string> commit, string placeholder)
    {
        var field = Field(value, placeholder);
        field.Width = 240;
        void Commit() => commit(field.Text.Trim());
        field.LostFocus += (_, _) => Commit();
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var browse = ActionButton(Loc.T("浏览…", "Browse…"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "可执行文件|*.exe", CheckFileExists = true, Title = placeholder };
            if (dialog.ShowDialog(this) == true) { field.Text = dialog.FileName; Commit(); }
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(field); row.Children.Add(browse);
        return row;
    }

    private string ConverterStatus()
    {
        var converter = new FileConverter(S);
        return Loc.T("内置转换 ✓", "Built-in conversion ✓") + (converter.HasOffice ? " · LibreOffice ✓" : "");
    }

    // A text field in the settings' own style, which writes what it holds when it loses the keyboard or Enter is pressed.
    // A password field is a hidden one instead: what is stored is never echoed, the box opens empty, and leaving it empty
    // keeps the stored secret — only a code typed in replaces it.
    private FrameworkElement MailField(string value, Action<string> commit, string placeholder, bool password = false)
    {
        if (!password)
        {
            var field = Field(value, placeholder);
            void Commit() => commit(field.Text.Trim());
            field.LostFocus += (_, _) => Commit();
            field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            return field;
        }
        var secret = new PasswordBox
        {
            Style = (Style)FindResource("SettingsPassword"), Width = 260, MaxLength = 200,
            ToolTip = placeholder, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(secret, placeholder);
        // The hint sits over the box while it is empty; hit-test transparent, so a click reaches the box beneath it.
        var hint = new TextBlock
        {
            Text = MailSecret.Has ? Loc.T("已保存，输入新授权码可更换", "Saved — type a new code to replace it") : placeholder,
            Foreground = (Brush)FindResource("SettingsTextSecondary"), FontSize = 13, Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        var host = new Grid();
        host.Children.Add(secret);
        host.Children.Add(hint);
        secret.PasswordChanged += (_, _) => hint.Visibility = secret.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void CommitSecret()
        {
            string code = secret.Password.Trim();
            if (code.Length == 0) return;
            commit(code);
            secret.Clear();
        }
        secret.LostFocus += (_, _) => CommitSecret();
        secret.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSecret(); e.Handled = true; } };
        return host;
    }

    private TextBox Field(string value, string placeholder)
    {
        var field = new TextBox
        {
            Style = (Style)FindResource("SettingsField"), Text = value, Width = 260, MaxLength = 200, ToolTip = placeholder
        };
        AutomationProperties.SetName(field, placeholder);
        return field;
    }

    private FrameworkElement MailServerField()
    {
        var host = Field(S.NotifyMailHost, "imap.qq.com");
        host.Width = 170;
        host.LostFocus += (_, _) => Change(s => s.NotifyMailHost = host.Text.Trim());
        host.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Change(s => s.NotifyMailHost = host.Text.Trim()); e.Handled = true; } };
        var port = Field(S.NotifyMailPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "993");
        port.Width = 62; port.Margin = new Thickness(8, 0, 0, 0);
        port.LostFocus += (_, _) => Change(s => s.NotifyMailPort = int.TryParse(port.Text.Trim(), out int value) ? Math.Clamp(value, 1, 65535) : 993);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(host); row.Children.Add(port);
        return row;
    }


    // The system's own folder chooser, through the shell.
    private static string? PickFolder()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? folder = shell.BrowseForFolder(0, Loc.T("选择输出文件夹", "Choose the folder results go to"), 0);
            string? path = folder?.Self.Path;
            return path;
        }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException
            or InvalidOperationException or System.Reflection.TargetInvocationException) { return null; }
    }
}
