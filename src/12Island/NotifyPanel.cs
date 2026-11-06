using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// The notification page: what the chat and office programs announced, newest first, kept until it is read. While something
// waits unread the collapsed island is a capsule instead of its usual row (BuildCompactBadges below).
public partial class MainWindow
{
    private string _notifyBuilt = "";
    private bool _notifyUnreadOnly;
    private NotifyItem? _notice;
    // How long the capsule waits for a click on it before a hover opens the panel instead (MainWindow.PointerChanged).
    private const int CapsuleClickMs = 600;
    // How long one program's icon and count stays on the capsule before the next one takes its place.
    private const long CarouselMs = 2600;

    private void InitializeNotify()
    {
        _app.NoticeArrived += NoticeArrived;
        _app.NoticesChanged += NoticesChanged;
        Closed += (_, _) => { _app.NoticeArrived -= NoticeArrived; _app.NoticesChanged -= NoticesChanged; };
        // The pill is rebuilt whenever a notice arrives, which can happen between the press and the release of a click.
        // Listening on the host that stays for the window's lifetime keeps that click from being lost.
        // The capsule is a button, not a hover surface: a click brings the announcing program up and reads its notices
        // (OpenNoticeApp), a pointer merely resting on it opens nothing (see PointerChanged).
        CompactAlert.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_noticeStyle != CompactStyle.Capsule || _app.Notices is not { Unread: > 0 } store) return;
            e.Handled = true;
            OpenNoticeApp(store);
        };
        SyncNotice();
    }

    private void NoticeArrived(NotifyItem item)
    {
        _notice = item;
        if (!_expanded && !IsSuppressed) SetExpanded(false);
        RenderCompact();
        PlayCapsuleAttention();
        // The reminder keeps playing, one loop after another, until the capsule is clicked or the notices are read (below).
        RefreshCapsuleAttention();
    }

    // A notice read, removed or cleared: the island stops showing it, an open page is drawn again.
    private void NoticesChanged()
    {
        if ((_app.Notices?.Unread ?? 0) == 0) StopCapsulePulse();
        SyncNotice();
        _notifyBuilt = "";
        if (IsNotifyPage && _expanded) RenderNotify();
        RenderCompact();
        // The reminder follows the unread count: it stops once everything is read, and resumes when the panel collapses
        // with notices still waiting.
        RefreshCapsuleAttention();
    }

    // What the collapsed island shows. While any watched program's notices wait unread, the collapsed bar takes the capsule's
    // shape so the waiting programs' icons and counts have a pill to stand in; the change of shape re-lays the window, as
    // choosing a style does.
    private void SyncNotice()
    {
        var store = _app.Notices;
        _notice = store is { Unread: > 0 } kept ? kept.LatestUnread : null;
        var style = store is { Unread: > 0 } ? CompactStyle.Capsule : (CompactStyle?)null;
        if (_noticeStyle == style) return;
        _noticeStyle = style;
        // Before the window has its presentation (the first start) nothing is laid out yet; the Loaded handler lays it out.
        if (PresentationSource.FromVisual(this) is null) return;
        ApplySettings();
    }

    private string NotifyBuildKey() => string.Join("|", _app.Notices?.Count ?? 0, _app.Notices?.Unread ?? 0, _notifyUnreadOnly,
        _app.MailError, _app.MailSeen?.Ticks ?? 0, _app.MailBusy, PanelTheme.Generation, Loc.Language,
        Math.Round(ContentCanvas.Width), Math.Round(ContentCanvas.Height));

    internal void RenderNotify()
    {
        var (width, height) = PreparePage(DeckPage.Notify);
        if (width <= 0) return;
        var content = NotifyContent;
        // Merely looking at the page does not read a notice: the capsule stays until the user clicks it to open the program.
        string key = NotifyBuildKey();
        if (key == _notifyBuilt && content.Children.Count > 0) return;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(NotifyToolbar());
        var list = NotifyRows();
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        var status = UI.Line(NotifyStatus(), 10.5, _app.MailError is { Length: > 0 } ? PanelTheme.Hex("danger") : UI.Tertiary);
        status.Margin = new Thickness(2, 4, 2, 0);
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        content.Children.Clear();
        content.Children.Add(grid);
        _notifyBuilt = key;
    }

    // The page's last line says only that the programs are being watched; a mailbox that refused its sign-in is the one thing
    // worth spelling out, since nothing else on the page would show it.
    private string NotifyStatus() => _app.MailError is { Length: > 0 } error
        ? error + Loc.T(" · 可在设置中修改", " · change it in settings")
        : Loc.T("监听中", "Listening");

    private FrameworkElement NotifyToolbar()
    {
        var row = new Grid { Height = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(UI.Centered(Loc.T("通知", "Notifications"), 12, UI.Secondary, FontWeights.SemiBold, UI.TextFont));
        int count = _app.Notices?.Count ?? 0, unread = _app.Notices?.Unread ?? 0;
        var amount = UI.Centered(unread > 0 ? Loc.T($" {unread} 条未读", Loc.Count(unread, "unread", "unread")) : Loc.T($" {count} 条", Loc.Count(count, "item", "items")),
            10.5, unread > 0 ? PanelTheme.Hex("accent") : UI.Tertiary, font: UI.TextFont);
        amount.Margin = new Thickness(6, 0, 0, 0);
        label.Children.Add(amount);
        row.Children.Add(label);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var all = Loc.T("全部", "All"); var fresh = Loc.T("未读", "Unread");
        right.Children.Add(Segmented(
        [
            new SegmentItem(all, all, !_notifyUnreadOnly, () => { _notifyUnreadOnly = false; _notifyBuilt = ""; RenderNotify(); }),
            new SegmentItem(fresh, fresh, _notifyUnreadOnly, () => { _notifyUnreadOnly = true; _notifyBuilt = ""; RenderNotify(); })
        ], 11, 20, 8, centerText: true));
        if (Settings.NotifyMail)
        {
            var check = IconText(Loc.T("检查邮箱", "Check mail"));
            check.Margin = new Thickness(10, 0, 0, 0);
            check.Click += (_, _) => { _app.CheckMailNow(); _notifyBuilt = ""; RenderNotify(); };
            right.Children.Add(check);
        }
        var read = IconText(Loc.T("全部已读", "Mark all read"));
        read.Margin = new Thickness(10, 0, 0, 0);
        read.Click += (_, _) => { _app.Notices?.MarkAllRead(); _notifyBuilt = ""; RenderNotify(); };
        right.Children.Add(read);
        var clear = IconText(Loc.T("清空", "Clear"));
        clear.Margin = new Thickness(8, 0, 0, 0);
        clear.Click += (_, _) => { _app.Notices?.Clear(); _notifyBuilt = ""; RenderNotify(); };
        right.Children.Add(clear);
        Grid.SetColumn(right, 1);
        Grid.SetColumnSpan(right, 2);
        row.Children.Add(right);
        var host = new Border { Child = row, Margin = new Thickness(2, 0, 2, 6) };
        return host;
    }

    private FrameworkElement NotifyRows()
    {
        var panel = new StackPanel();
        var items = (_app.Notices?.Items ?? []).Where(item => !_notifyUnreadOnly || !item.Read).ToList();
        if (items.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            empty.Children.Add(UI.Fixed(_notifyUnreadOnly ? Loc.T("没有未读通知", "Nothing unread") : Loc.T("还没有通知", "No notifications yet"), 12.5, 20, UI.Secondary));
            empty.Children.Add(UI.Fixed(Loc.T("微信、QQ、邮件或其他聊天软件有新消息时会显示在这里", "Notices from WeChat, QQ, mail or another chat program appear here"), 10.5, 18, UI.Tertiary));
            return empty;
        }
        foreach (var item in items) panel.Children.Add(NotifyRow(item));
        return PageScroll(panel);
    }

    // A mail's subject is shortened to this many characters on the row; the whole notice opens on a click.
    private const int MailPreviewChars = 40;

    // One line for a row: newlines become spaces so a long mail subject or a chat message cannot stretch the row.
    private static string Preview(string text, int limit)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    // The name and the content of a notice. A chat notice names the sender and carries the message; a mail names its
    // sender in Body and its subject in Title, so the two sources swap which field is which.
    private static (string Name, string Content) NoticeText(NotifyItem item) => item.Source == NotifySource.Mail
        ? (item.Body.Length > 0 ? item.Body : item.SourceName, item.Title.Length > 0 ? item.Title : item.Body)
        : (item.Title.Length > 0 ? item.Title : item.SourceName, item.Body.Length > 0 ? item.Body : item.Title);

    // A mail's sender is kept as "Name <address>"; on the collapsed bar the name alone is what fits.
    private static string Sender(string name)
    {
        int bracket = name.IndexOf('<');
        string trimmed = (bracket > 0 ? name[..bracket] : name).Trim();
        return trimmed.Length > 0 ? trimmed : name.Trim();
    }

    // A row in the size of the clipboard's rows: the program's own icon, the sender's name, the notice's content and the
    // time. A mail shows only the first 40 characters of its subject, so a click opens it whole; a chat notice already shows
    // its message in full and is not opened at all. A click never clears the list.
    private FrameworkElement NotifyRow(NotifyItem item)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = SourceBadge(item.Program, item.Source, 15);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.ToolTip = item.SourceName;
        grid.Children.Add(badge);
        var (name, content) = NoticeText(item);
        var shown = item.Source == NotifySource.Mail ? Preview(content, MailPreviewChars) : Preview(content, int.MaxValue);
        var text = new StackPanel { Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        var heading = UI.Line(name, 12.5, item.Read ? UI.Secondary : UI.Primary);
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(heading);
        if (shown.Length > 0 && shown != name)
        {
            var body = UI.Line(shown, 10.5, UI.Tertiary);
            body.Margin = new Thickness(0, 1, 0, 0);
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(body);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var time = UI.Centered(item.Age, 10.5, UI.Tertiary, font: UI.TextFont);
        time.Margin = new Thickness(6, 0, 8, 0);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var row = PageRow(grid, 34);
        row.Tag = item;
        row.ContextMenu = NotifyMenu(item);
        // A mail is worth opening in full, since its subject is cut to forty characters on the row. A chat notice shows its
        // message whole already, so a click on it opens nothing.
        if (item.Source == NotifySource.Mail)
        {
            row.ToolTip = Loc.T("点击查看完整邮件", "Click to read the whole mail");
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => OpenNoticeDetail(item);
        }
        return row;
    }

    // The whole mail, opened from its row: the sender, the full subject and the time. Reading it does not mark it read,
    // clear the list or dismiss the capsule — only a click on the capsule itself opens the program.
    private Window? _noticeDetail;
    private void OpenNoticeDetail(NotifyItem item)
    {
        _noticeDetail?.Close();
        var (name, _) = NoticeText(item);
        var lines = new List<string>();
        if (item.Source == NotifySource.Mail)
        {
            lines.Add($"{Loc.T("发件人", "From")}: {item.Body}");
            lines.Add($"{Loc.T("主题", "Subject")}: {item.Title}");
        }
        else
        {
            if (item.Title.Length > 0) lines.Add($"{Loc.T("来自", "From")}: {item.Title}");
            if (item.Body.Length > 0) lines.Add(item.Body);
        }
        lines.Add($"{Loc.T("时间", "Time")}: {item.At.LocalDateTime:yyyy-MM-dd HH:mm} · {item.SourceName}");
        string full = string.Join("\n\n", lines.Where(line => line.Length > 0));

        var dialog = new Window
        {
            Title = Loc.T("通知内容", "Notice details"), Owner = this,
            Width = 540, Height = 380, MinWidth = 380, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            FontFamily = UI.PanelFont, Background = UI.Brush(PanelTheme.Hex("surface")), Foreground = UI.Brush(UI.Primary)
        };
        // The system caption follows the panel's theme, so the details window does not show a white title bar on a dark skin.
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, PanelTheme.Current.IsDark);
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = UI.Text(name, 16, UI.Primary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 14);
        grid.Children.Add(title);
        var box = new TextBox
        {
            Text = full, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, FontFamily = UI.PanelFont,
            Background = UI.Brush(PanelTheme.Hex("fill")), Foreground = UI.Brush(UI.Primary),
            BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1), Padding = new Thickness(12)
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Content = Loc.T("复制内容", "Copy text"), Padding = new Thickness(14, 6, 14, 6), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => _app.CopyToClipboard(new ClipEntry { Kind = ClipKind.Text, Text = full });
        var close = new Button { Content = Loc.T("关闭", "Close"), Padding = new Thickness(14, 6, 14, 6), MinWidth = 80, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(copy); buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        dialog.Content = grid;
        dialog.Closed += (_, _) => { if (ReferenceEquals(_noticeDetail, dialog)) _noticeDetail = null; };
        _noticeDetail = dialog;
        dialog.Show();
    }

    private ContextMenu NotifyMenu(NotifyItem item)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        void Item(string text, Action action)
        {
            var entry = new MenuItem { Header = text, Style = (Style)FindResource("MediaSourceMenuItem") };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Item(Loc.T("标为已读", "Mark as read"), () => _app.Notices?.MarkRead(item.Id));
        if (item.Body.Length + item.Title.Length > 0)
            Item(Loc.T("复制内容", "Copy the text"), () => _app.CopyToClipboard(new ClipEntry { Kind = ClipKind.Text, Text = (item.Title + " " + item.Body).Trim() }));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
        Item(Loc.T("删除", "Delete"), () => _app.Notices?.Remove(item.Id));
        return menu;
    }

    // Each source keeps its own color, so a glance tells who it came from; the theme's accent stands for the rest.
    private static string SourceColor(NotifySource source) => source switch
    {
        NotifySource.Qq => "#3BA9F0", NotifySource.WeChat => "#22B14C", NotifySource.Mail => "#E8A33D", _ => UI.Accent
    };

    // The icon of the program a notice came from, at the size asked for. Where the program cannot be found (a mailbox read
    // without its client installed) a symbol stands in, in the source's own color.
    private FrameworkElement SourceBadge(string program, NotifySource source, double size)
    {
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        // The window that flashed names the program's running executable; ask for its icon before falling back to the source.
        if ((AppIcons.ForProgram(program, _app.NotifyWindow(program)) ?? AppIcons.For(source)) is { } icon)
        {
            host.Children.Add(new Image
            {
                Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            });
            RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
            return host;
        }
        host.Children.Add(UI.Centered(source == NotifySource.Mail ? "\uE715" : "\uE8BD", size * .9, SourceColor(source), font: UI.IconFont));
        return host;
    }

    // The collapsed island shows an unread notice in place of the rings, the way a quota warning does: the source, then who and
    // what. It stays until the notice is read, and the dot beside it keeps a slow pulse so it is noticed without moving the row.
    private (object? Notice, CollapsedShape Shape)? _noticeRendered;
    private void BuildCompactNotice(CollapsedShape shape)
    {
        if (_notice is not { } notice) return;
        // WeChat, QQ or the mailbox waiting: the bar is a capsule now, and it shows each waiting program's icon and count
        // instead of one notice's text (SyncNotice chose the shape).
        if (_noticeStyle == CompactStyle.Capsule && _app.Notices is { } store) { BuildCompactBadges(shape, store); return; }
        if (_noticeRendered is { } done && Equals(done.Shape, shape) && ReferenceEquals(done.Notice, notice) && CompactAlert.Children.Count > 0) return;
        _noticeRendered = (notice, shape);
        CompactAlert.Children.Clear();
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        double font = Math.Max(1, CompactMusicLayout.Half(10 * scale)), line = Math.Max(1, CompactMusicLayout.Half(12 * scale));
        double room = CompactContentWidth(shape);
        // An iPhone's notification banner, in the width of the island: the program's icon, the sender in bold over the message in
        // a lighter line, and the time at the right, all inside a rounded card so the notice reads as a card rather than a row.
        double size = Math.Max(1, CompactMusicLayout.Whole(16 * scale));
        var badge = SourceBadge(notice.Program, notice.Source, size);
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.Margin = new Thickness(0, 0, CompactMusicLayout.Whole(7 * scale), 0);
        if (Settings.Animations && SystemParameters.ClientAreaAnimation)
        {
            // A few pulses draw the eye to the unread notice, then the animation stops so the render loop goes idle again
            // (a never-ending pulse would keep WPF compositing at the display refresh rate and peg the CPU while the notice waits to be read).
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, .45, TimeSpan.FromSeconds(1.1))
            {
                AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(4), EasingFunction = new System.Windows.Media.Animation.SineEase()
            };
            badge.BeginAnimation(UIElement.OpacityProperty, pulse);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string title = notice.Title.Length > 0 ? notice.Title : notice.SourceName;
        string bodyText = notice.Body.Length > 0 ? notice.Body : notice.Line;
        var heading = UI.Fixed(title, font, line, UI.Primary, FontWeights.SemiBold, UI.TextFont);
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(heading);
        if (bodyText.Length > 0)
        {
            var body = UI.Fixed(bodyText, Math.Max(1, CompactMusicLayout.Half(9.5 * scale)), line, UI.Secondary, FontWeights.Normal, UI.TextFont);
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(body);
        }
        var time = UI.Fixed(notice.Age, Math.Max(1, CompactMusicLayout.Half(9.5 * scale)), line, UI.Tertiary, FontWeights.Normal, UI.TextFont);
        time.VerticalAlignment = VerticalAlignment.Center;
        time.Margin = new Thickness(CompactMusicLayout.Whole(6 * scale), 0, 0, 0);
        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(badge);
        Grid.SetColumn(text, 1); content.Children.Add(text);
        Grid.SetColumn(time, 2); content.Children.Add(time);
        var card = new Border
        {
            CornerRadius = new CornerRadius(CompactMusicLayout.Whole(9 * scale)),
            Background = UI.Brush(PanelTheme.Hex("fill")), Padding = new Thickness(CompactMusicLayout.Whole(6 * scale), CompactMusicLayout.Whole(1 * scale),
                CompactMusicLayout.Whole(7 * scale), CompactMusicLayout.Whole(1 * scale)),
            MaxWidth = Math.Max(10, room), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Tag = AlertRegionTag
        };
        card.Child = content;
        CompactAlert.Children.Add(card);
        _compactRendered = shape;
    }

    // Several programs waiting: the pill shows one at a time and rotates between them, so it stays compact. Its width
    // follows the page on screen — a longer count widens it a little, a short one takes it back — and the island's
    // surface follows that width, so every program gets its turn without the pill ever being cut off or left with air.
    private void BuildCompactBadges(CollapsedShape shape, NotifyStore store)
    {
        StopCapsulePulse();
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        // One page is an icon and its text: the sender with the count, or the code alone. Nothing else is added to its width.
        double size = Math.Max(1, CompactMusicLayout.Whole(14 * scale));
        double font = Math.Max(1, CompactMusicLayout.Half(10 * scale)), line = Math.Max(1, CompactMusicLayout.Half(13 * scale));
        double codeFont = Math.Max(1, CompactMusicLayout.Half(12.5 * scale));
        double senderRoom = CompactMusicLayout.Whole(78 * scale);
        double pad = CompactMusicLayout.Whole(6 * scale);
        bool column = ColumnFor(shape.Style);
        var groups = store.UnreadGroups();
        if (groups.Count == 0) { CompactAlert.Children.Clear(); _compactRendered = shape; return; }

        // One page per program, all stacked in the same place; only the current one is visible.
        var pages = new List<FrameworkElement>(groups.Count);
        double widest = 0;
        foreach (var group in groups)
        {
            var item = new StackPanel { Orientation = column ? Orientation.Vertical : Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var badge = SourceBadge(group.Program, group.Source, size);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.ToolTip = group.App.Length > 0 ? group.App : group.Program;
            item.Children.Add(badge);
            double gap = CompactMusicLayout.Half(4 * scale);
            if (group.Code is { } code)
            {
                // A code is the one thing to be read and typed, so the page is the digits themselves, larger than the
                // sender would be and with nothing beside them to shorten them.
                var digits = UI.Fixed(code, codeFont, line, UI.Primary, FontWeights.SemiBold, UI.TextFont);
                digits.VerticalAlignment = VerticalAlignment.Center;
                digits.Margin = column ? new Thickness(0) : new Thickness(gap, 0, 0, 0);
                digits.ToolTip = Loc.T($"验证码 {code}", $"Code {code}");
                item.Children.Add(digits);
            }
            else
            {
                // Who wrote, then how many are waiting: the two read as one line, the name in full strength.
                var sender = UI.Fixed(Sender(NoticeText(group.Latest).Name), font, line, UI.Primary, FontWeights.SemiBold, UI.TextFont);
                sender.VerticalAlignment = VerticalAlignment.Center;
                sender.TextTrimming = TextTrimming.CharacterEllipsis;
                sender.MaxWidth = senderRoom;
                sender.Margin = column ? new Thickness(0) : new Thickness(gap, 0, 0, 0);
                item.Children.Add(sender);
                var count = UI.Fixed(Loc.T($"通知{group.Count}条", $"{group.Count} new"), font, line, UI.Secondary, FontWeights.Normal, UI.TextFont);
                count.VerticalAlignment = VerticalAlignment.Center;
                count.Margin = column ? new Thickness(0) : new Thickness(CompactMusicLayout.Half(4 * scale), 0, 0, 0);
                item.Children.Add(count);
            }
            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, Math.Ceiling(item.DesiredSize.Width));
            pages.Add(item);
        }
        // The rotation keeps its place when the pill is rebuilt (a new notice, a shape change), so it does not jump back
        // to the first program on every redraw.
        if (_capsuleFrames != groups.Count) { _capsuleFrames = groups.Count; _capsuleSince = Environment.TickCount64; }
        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var page in pages) stage.Children.Add(page);
        int Current() => groups.Count > 1 ? (int)((Environment.TickCount64 - _capsuleSince) / CarouselMs) % groups.Count : 0;
        void Show()
        {
            int current = Current();
            for (int i = 0; i < pages.Count; i++) pages[i].Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
            // What the pill is showing now: a click opens that program and reads that one notice.
            _capsuleNotice = groups[current].Latest;
        }
        Show();

        // Unlimited while measuring, so the requested width is the width of this page, not of the old capsule.
        var pill = new Border
        {
            CornerRadius = new CornerRadius(CompactMusicLayout.Whole(10 * scale)),
            Background = UI.Brush(PanelTheme.Hex("fill")),
            Padding = new Thickness(pad, CompactMusicLayout.Whole(1 * scale), pad, CompactMusicLayout.Whole(1 * scale)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = double.PositiveInfinity,
            Tag = AlertRegionTag, Cursor = Cursors.Hand,
            ToolTip = groups.Count > 1
                ? Loc.T($"共 {groups.Count} 个软件有未读，正在轮换显示；点击打开最新通知的程序",
                    $"{groups.Count} programs are waiting, shown in turn; click to open the newest")
                : Loc.T($"点击打开{store.LatestUnread?.SourceName ?? ""}", "Click to open the program")
        };
        pill.Child = stage;
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = Math.Ceiling(pill.DesiredSize.Width);
        // Every page fits (the widest sets the ceiling), but the pill itself only takes what the page on screen needs.
        pill.MaxWidth = column ? double.PositiveInfinity
            : Math.Max(10, Math.Max(CompactContentWidth(shape), Math.Ceiling(widest + 2 * pad)));
        // A click on the pill opens the program that announced the message; the handler lives on CompactAlert (InitializeNotify)
        // so that rebuilding this pill cannot swallow the release half of a click.
        // The waiting is signalled by the capsule as a whole (PlayCapsuleAttention: the island bounces or bursts a ring),
        // never by the pill's own content: the fill, the icons and the text stay still, so the reminder reads as one
        // surface moving rather than words and pictures twinkling.
        // A new page asks for its own width, so the capsule always fits what it is showing and nothing more.
        if (groups.Count > 1)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                Show();
                pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double wanted = Math.Ceiling(pill.DesiredSize.Width);
                if (column || (!double.IsNaN(_compactWanted) && Math.Abs(wanted - _compactWanted) <= .5)) return;
                _compactWanted = wanted; _compactRequired = wanted;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent);
            };
            timer.Start();
            _capsuleCarousel = timer;
        }
        // The pill asks only for what one page takes, so the width the music row needed must not linger while notices wait.
        bool fit = !column && (double.IsNaN(_compactWanted) || Math.Abs(natural - _compactWanted) > .5);
        _compactWanted = natural; _compactRequired = natural;
        if (fit) Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent);
        CompactAlert.Children.Clear();
        CompactAlert.Children.Add(pill);
        _compactRendered = shape;
    }

    // The pill's rotation between waiting programs keeps a timer busy, so it ends with the waiting it points at.
    private DispatcherTimer? _capsuleCarousel;
    // The notice the pill is showing at this moment: the rotation moves it, and a click on the pill reads this one.
    private NotifyItem? _capsuleNotice;
    private long _capsuleSince;
    private int _capsuleFrames;
    private void StopCapsulePulse()
    {
        if (_capsuleCarousel is not null) { _capsuleCarousel.Stop(); _capsuleCarousel = null; }
        _capsuleNotice = null;
    }

    // The one-shot attention a new notice asks for, as the settings pick: the capsule drops and settles (弹跳), or a ring
    // bursts from it while the capsule punches outward (音爆), the way a dock's icon answers a message. Both play on the
    // surface itself — the pill is rebuilt as notices arrive, the surface is not — and both clean up after, so nothing keeps
    // the render loop busy once the motion ends. A burst of notices plays once, not one animation per message.
    private long _lastAttention;
    private void PlayCapsuleAttention()
    {
        if (_expanded || IsSuppressed || _noticeStyle != CompactStyle.Capsule) return;
        if (!Settings.Animations || !SystemParameters.ClientAreaAnimation || Settings.NotifyAttention == NotifyAttention.None) return;
        long now = Environment.TickCount64;
        if (now - _lastAttention < 900) return;
        _lastAttention = now;
        var surface = Island;
        if (Settings.NotifyAttention == NotifyAttention.Bounce)
        {
            // The capsule lets go, drops a little, and settles with a rebound — one fall, one overshoot, one soft landing.
            var drop = new TranslateTransform();
            surface.RenderTransform = drop;
            ShadowHost.RenderTransform = drop;
            var fall = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            fall.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(9, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
                new System.Windows.Media.Animation.KeySpline(0.5, 0, 1, 1)));
            fall.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(-3, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
                new System.Windows.Media.Animation.KeySpline(0.2, 0.9, 0.4, 1)));
            fall.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(2, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(470)),
                new System.Windows.Media.Animation.KeySpline(0.5, 0, 1, 1)));
            fall.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(630))));
            drop.BeginAnimation(TranslateTransform.YProperty, fall);
            var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(660) };
            done.Tick += (_, _) =>
            {
                done.Stop();
                surface.RenderTransform = null;
                ShadowHost.RenderTransform = null;
            };
            done.Start();
            return;
        }
        // 音爆: the capsule punches outward and two echoes of the capsule itself — the whole silhouette, corners and all,
        // not circles — run from it and thin away, so what radiates is the capsule as one piece.
        surface.RenderTransformOrigin = new Point(0.5, 0.5);
        var punch = new ScaleTransform();
        surface.RenderTransform = punch;
        var outward = new System.Windows.Media.Animation.DoubleAnimation(1, 1.05, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true, EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        punch.BeginAnimation(ScaleTransform.ScaleXProperty, outward);
        punch.BeginAnimation(ScaleTransform.ScaleYProperty, outward);
        var host = HoverZone;
        var flash = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        flash.Freeze();
        var echoes = new List<System.Windows.Controls.Border>();
        for (int i = 0; i < 2; i++)
        {
            var echo = new System.Windows.Controls.Border
            {
                Width = surface.ActualWidth, Height = surface.ActualHeight, Background = flash,
                CornerRadius = Island.CornerRadius,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(surface.Margin.Left, surface.Margin.Top, 0, 0), IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform()
            };
            host.Children.Add(echo);
            echoes.Add(echo);
            var wait = TimeSpan.FromMilliseconds(i * 130);
            var grow = new System.Windows.Media.Animation.DoubleAnimation(1, 2.2, TimeSpan.FromMilliseconds(620))
            {
                BeginTime = wait, EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(620)) { BeginTime = wait, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            var burst = (ScaleTransform)echo.RenderTransform;
            burst.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            burst.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            echo.BeginAnimation(OpacityProperty, fade);
        }
        var clear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            foreach (var echo in echoes) host.Children.Remove(echo);
            surface.RenderTransform = null;
            surface.RenderTransformOrigin = new Point(0, 0);
        };
        clear.Start();
    }

    // The reminder replays on a loop until the capsule is clicked or the waiting ends: each turn plays one attention, and the
    // loop waits long enough for it to settle before the next, so the render loop is idle between turns (a never-ending pulse
    // would peg the CPU while a notice waits to be read).
    private DispatcherTimer? _attentionLoop;
    private void StartCapsuleAttentionLoop()
    {
        _attentionLoop ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(2600) };
        _attentionLoop.Tick -= OnAttentionLoop;
        _attentionLoop.Tick += OnAttentionLoop;
        _attentionLoop.Stop();
        _attentionLoop.Start();
    }
    private void OnAttentionLoop(object? _, EventArgs _e) => PlayCapsuleAttention();
    private void StopCapsuleAttentionLoop() => _attentionLoop?.Stop();
    // Starts or stops the looping reminder to match the current state: it plays only while the island is a capsule showing
    // unread notices, is on screen, animations are on, and the panel is not open. Opening the panel (SetExpanded) or reading
    // the notices ends it; collapsing back with notices still waiting brings it back.
    private void RefreshCapsuleAttention()
    {
        if (_expanded || IsSuppressed || _noticeStyle != CompactStyle.Capsule || _app.Notices?.Unread is not > 0
            || Settings.NotifyAttention == NotifyAttention.None || !Settings.Animations || !SystemParameters.ClientAreaAnimation)
            StopCapsuleAttentionLoop();
        else
            StartCapsuleAttentionLoop();
    }

    // A click on the capsule: the program it is showing comes to the front, and everything that program announced is read
    // with it. The notices stay in the list so their text can still be opened; only their unread state ends, and only for
    // this program — the other programs keep waiting.
    private void OpenNoticeApp(NotifyStore store)
    {
        // The page on screen decides which program a click deals with. A pill rebuilt since can hold a notice that has been
        // read by now, and the newest unread stands in then.
        var notice = _capsuleNotice is { Read: false } shown ? shown : store.LatestUnread;
        if (notice is not { } latest) return;
        _hover.Stop();
        // Marking the whole program read happens on the click itself, whether or not its window came up — the user has
        // answered the capsule either way. Clearing would erase the notification information, which the page keeps until
        // the user chooses "Clear" or deletes a row by hand.
        store.MarkProgramRead(latest.Program);
        _app.OpenNotifyApp(latest.Program);
    }
}
