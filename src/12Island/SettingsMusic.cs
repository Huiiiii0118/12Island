using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private DeckPage _previewPage;
    // keepPreview marks the segments alone as preview controls, so a press on the rest of the row still ends the preview.
    private FrameworkElement MusicChoice<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true, bool keepPreview = false, string? help = null) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = 292, IsEnabled = enabled };
        if (keepPreview) KeepPreview(strip);
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip), help: help is null ? null : () => help); row.IsEnabled = enabled; return row;
    }
    // The options under the style cards belong to the chosen style. Choosing another card slides the current options
    // out and the chosen style's options in, moving the way the chosen card lies from the previous one.
    private Grid? _styleOptions;
    private void ShowStyleOptions(CompactStyle style, CompactStyle? previous)
    {
        if (_styleOptions is not { } host) return;
        var incoming = Collect(() => { StyleOptions(style); CollapsedSizeOptions(style); });
        var outgoing = host.Children.OfType<FrameworkElement>().LastOrDefault();
        double width = host.ActualWidth;
        if (outgoing is null || previous is not { } from || from == style || width <= 0 || !SystemParameters.ClientAreaAnimation)
        { host.Children.Clear(); host.Children.Add(incoming); return; }
        // A panel still leaving from an earlier choice is dropped; the one arriving becomes the one leaving.
        foreach (var leaving in host.Children.OfType<FrameworkElement>().Where(child => child != outgoing).ToList()) host.Children.Remove(leaving);
        double direction = style > from ? 1 : -1;
        host.ClipToBounds = true; outgoing.IsHitTestVisible = false; host.Children.Add(incoming);
        var leave = outgoing.RenderTransform as TranslateTransform ?? new TranslateTransform(); outgoing.RenderTransform = leave;
        var enter = new TranslateTransform(direction * width, 0); incoming.RenderTransform = enter;
        DoubleAnimation Slide(double to) => new(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = Soft.Out };
        var leaveMotion = Slide(-direction * width); leaveMotion.Completed += (_, _) => host.Children.Remove(outgoing);
        var enterMotion = Slide(0); enterMotion.Completed += (_, _) => { if (host.Children.Count == 1) host.ClipToBounds = false; };
        leave.BeginAnimation(TranslateTransform.XProperty, leaveMotion); enter.BeginAnimation(TranslateTransform.XProperty, enterMotion);
    }
    private void StyleOptions(CompactStyle style)
    {
        SectionTitle(Loc.T("显示内容 · ", "Content · ") + StyleName(style), help: () => Loc.T(
            "律动条和中间文字固定在收起栏正中，文字紧接在律动条的左边。上下拖动这几项，决定其余各项排在正中两侧的哪一边：系统读数排在律动条之前时从左边开始，排到后面时从右边开始，一侧放不下就顺延到另一侧；时钟排在最前面时在左边。",
            "The rhythm and the middle text stand in the centre of the collapsed bar, the text right before the rhythm. Dragging these rows moves the rest to one side or the other: the readings start on the left when they come before the rhythm and on the right when they come after, carrying over to the other side when one is full; the clock goes left when it comes first."));
        if (style == CompactStyle.Line)
        {
            Group(Toggle(Loc.T("显示播放进度", "Show playback progress"), S.MusicIndicatorProgress, v => Change(s => s.MusicIndicatorProgress = v)));
            return;
        }
        // The rows are in the order the collapsed row shows the parts, left to right; only the cover keeps its place at the left.
        var rows = new StackPanel();
        _partOptions.Clear();
        foreach (var part in S.CompactOrder(style))
            rows.Children.Add(part == CompactPart.Clock ? ClockRow(style, rows) : PartRow(style, part, rows));
        UpdateDividers(rows);
        Group(rows);
    }

    private const double PartRowHeight = 44, PartHandleWidth = 40;
    // The parts whose options are open (the clock, the quota rings), kept while the page is rebuilt or the collapsed style changes.
    private readonly HashSet<CompactPart> _openParts = [];
    private readonly List<(Border Row, Border Options, Button Arrow)> _partOptions = [];
    // One draggable row of the collapsed content: the handle, the name and the switch.
    private Border PartRow(CompactStyle style, CompactPart part, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        var (title, shown, help) = part switch
        {
            CompactPart.Equalizer => (Loc.T("律动条", "Equalizer"), S.ShowsEqualizer(style),
                Loc.T("显示在收起栏的正中，中间文字紧接在它的左边。颜色和根数在“音乐”页设置。",
                    "Stands in the centre of the collapsed bar, with the middle text right before it. Its color and bar count are set on the Music page.")),
            CompactPart.Music => (Loc.T("正在播放的音乐", "Music now playing"), S.ShowsMusic(style),
                Loc.T("歌名或歌词紧接在律动条的左边，和它一起固定在正中。封面始终在最左侧。",
                    "The title or lyric stands right before the rhythm, the two centred together. The cover stays at the left edge.")),
            _ => (Loc.T("系统监控", "System monitor"), S.ShowsMonitor(style),
                Loc.T("排在律动条之前时从正中左侧开始，排在之后时从右侧开始；一侧放不下的自动排到另一侧。",
                    "Starts left of the centre when it comes before the rhythm and right of it when it comes after; what does not fit one side carries over to the other."))
        };
        void Show(bool on) => Change(s =>
        {
            switch (part)
            {
                case CompactPart.Music: if (notch) s.NotchMusic = on; else s.CapsuleMusic = on; break;
                case CompactPart.Equalizer: if (notch) s.NotchEqualizer = on; else s.CapsuleEqualizer = on; break;
                default: if (notch) s.NotchMonitor = on; else s.CapsuleMonitor = on; break;
            }
        });
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = SettingLabel(title, help is null ? null : () => help); Grid.SetColumn(label, 1); row.Children.Add(label);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => Show(true); toggle.Unchecked += (_, _) => Show(false);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        AttachPartDrag(handle, title, style, rows);
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
    }
    // Dropped in a new place, the collapsed row takes the new order at once. The open options close when a drag begins, so every
    // row is one line high while they move.
    private void AttachPartDrag(Border handle, string name, CompactStyle style, StackPanel rows) => AttachDrag(handle, name, () => rows, PartRowHeight, list =>
    {
        var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactPart).Select(b => (CompactPart)b.Tag).ToArray();
        Change(s => s.SetCompactOrder(style, order));
    }, ClosePartOptions);
    private void ClosePartOptions()
    {
        _openParts.Clear();
        foreach (var (row, options, arrow) in _partOptions) { options.Visibility = Visibility.Collapsed; PointArrow(arrow, false); row.Height = PartRowHeight; }
    }
    // A part with options under it: the handle, the title with its arrow, and the switch, which only turns the part on or off.
    // The title and its arrow open and close the options; the options are dimmed while the part is off. Without padding they lie
    // under the title, a little indented, as the page rows' sizes do; with it, on a card across the row.
    private Border OptionsPartRow(CompactStyle style, CompactPart part, StackPanel rows, string title, string arrowName, bool shown, Action<bool> turn, FrameworkElement content, Thickness? padding = null)
    {
        var options = padding is { } card ? new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(10), Padding = card, Margin = new Thickness(0, 0, 0, 12), Child = content }
            : new Border { Margin = new Thickness(20, 0, 0, 8), Child = content };
        void Enable(bool on) { options.IsEnabled = on; options.Opacity = on ? 1 : .45; }
        Enable(shown);
        Button arrow = null!;
        Border item = null!;
        void Show(bool open)
        {
            if (open) _openParts.Add(part); else _openParts.Remove(part);
            options.Visibility = open ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, open);
            item.Height = open ? double.NaN : PartRowHeight;
        }
        arrow = ChevronButton(arrowName, () => Show(!_openParts.Contains(part)));
        arrow.Margin = new Thickness(2, 0, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(SettingLabel(title)); heading.Children.Add(arrow);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => { turn(true); Enable(true); }; toggle.Unchecked += (_, _) => { turn(false); Enable(false); };
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PartRowHeight) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        Grid.SetColumn(heading, 1); row.Children.Add(heading);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        Grid.SetRow(options, 1); if (padding is null) Grid.SetColumn(options, 1); Grid.SetColumnSpan(options, padding is null ? 2 : 3); row.Children.Add(options);
        // The handle, the switch and the arrow take their own clicks; a click anywhere else on the title line opens or closes the
        // options, as the size rows do.
        row.MouseLeftButtonDown += (_, e) => { if (e.GetPosition(row).Y >= PartRowHeight) return; Show(!_openParts.Contains(part)); e.Handled = true; };
        AttachPartDrag(handle, title, style, rows);
        item = new Border { Child = row, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
        _partOptions.Add((item, options, arrow));
        Show(_openParts.Contains(part));
        return item;
    }
    // The clock's swatches but the gradient: the theme's color (theme), the presets, a custom color and the cover's. Used for the
    // equalizer and for the music page's current lyric; save gets the chosen color and the custom one.
    private StackPanel ColorSwatches(string group, string label, ClockColor selected, string custom, Brush theme, Action<ClockColor, string> save,
        string? firstName = null)
    {
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        ClockColor[] order = [ClockColor.White, ClockColor.Amber, ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Custom, ClockColor.Cover];
        foreach (var color in order)
        {
            string name = color == ClockColor.White ? firstName ?? ColorName(color) : ColorName(color);
            var swatch = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = group, IsChecked = selected == color,
                Background = SwatchFill(color), ToolTip = name, Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 6, 0, 0, 0) };
            AutomationProperties.SetName(swatch, label + name);
            swatch.Checked += (_, _) => save(color, custom);
            if (color == ClockColor.Cover) swatch.Content = MusicVisuals.Icon("music", 13, UI.Brush(SwatchInk(SampleCover)));
            if (color == ClockColor.Custom)
            {
                // Choosing this swatch also opens the picker; each later click opens it again.
                swatch.Content = Pencil(ClockFace.Parse(custom));
                AttachClockPicker(swatch, () => custom, ClockColors.DefaultCustom, value =>
                {
                    custom = value; save(ClockColor.Custom, value);
                    swatch.Background = SwatchFill(ClockColor.Custom); swatch.Content = Pencil(ClockFace.Parse(value));
                });
            }
            swatches.Children.Add(swatch);
        }
        return swatches;
        Brush SwatchFill(ClockColor color) => color switch
        {
            ClockColor.Custom => ClockFace.Fill([ClockFace.Parse(custom)]),
            ClockColor.Cover => ClockFace.Fill([SampleCover]),
            ClockColor.White => theme,
            _ => ClockFace.Fill([PanelTheme.Readable(ClockFace.Preset(color))])
        };
    }
    // Labels inside an open row are set lighter than the row's own title, as "快捷尺寸" is (QuickRow).
    private static FrameworkElement Lighter(FrameworkElement row, string title)
    {
        static IEnumerable<TextBlock> Texts(DependencyObject parent) => LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()
            .SelectMany(child => child is TextBlock text ? new[] { text } : Texts(child));
        if (Texts(row).FirstOrDefault(text => text.Text == title) is { } label) label.FontWeight = FontWeights.Normal;
        return row;
    }
    // Settings every player shares: the equalizer, colors, cover and text.
    // The music page of the settings: how the panel draws the player, then how much control it takes of each player.
    private void MusicSettings()
    {
        MusicStyle();
        FullControl();
    }

    private void MusicStyle()
    {
        // The equalizer's way and color serve the collapsed and the expanded equalizer alike; each keeps its own length, and the
        // collapsed one's switch stays with the collapsed content (PartRow).
        SectionTitle(Loc.T("律动条", "Equalizer"), help: () => Loc.T(
            "律动条显示正在播放的声音：有两处，收起栏里的一处和展开后音乐页的一处，长度各自设置。没有播放音乐时，收起栏的律动条跟随系统声音。",
            "The equalizer shows the sound being played, in the collapsed bar and on the expanded music page, each with its own length. With no music playing, the collapsed one follows the system's sound."));
        var pageBars = SliderRow(Loc.T("展开时长度", "Expanded length"), S.MusicEqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, Loc.T("根", "bars"),
            v => Change(s => s.MusicEqualizerBars = (int)v), help: Loc.T("音乐页里律动条的根数。", "How many bars the music page shows."));
        pageBars.IsEnabled = S.MusicPage;
        Group(MusicChoice(Loc.T("律动方式", "Style"), S.EqualizerMode, [(EqualizerMode.Flow, Loc.T("流动", "Flow")), (EqualizerMode.Spectrum, Loc.T("频谱", "Spectrum"))],
                v => Change(s => s.EqualizerMode = v), help: Loc.T("流动：声音的起伏从左流到右。频谱：每根条显示一段音高，左低右高。",
                    "Flow: the rise and fall of the sound runs from left to right. Spectrum: each bar shows a range of pitch, low on the left.")),
            SliderRow(Loc.T("收起时长度", "Collapsed length"), S.EqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, Loc.T("根", "bars"),
                v => Change(s => s.EqualizerBars = (int)v), help: Loc.T("收起栏里律动条的根数：每根 3 DIP 宽、隔 2 DIP，条数越多越宽。",
                    "How many bars the collapsed bar shows: 3 DIP wide with 2 DIP between them, so more bars make it wider.")),
            pageBars);
        SectionTitle(Loc.T("收起时", "When collapsed"));
        // The instrumental choice belongs to the lyric, so it shows only while the middle text is the lyric.
        var text = new List<FrameworkElement> { MusicChoice(Loc.T("中间文字", "Middle text"), S.MusicText, new[] { (CompactMusicText.None, Loc.T("无", "None")), (CompactMusicText.Title, Loc.T("歌名", "Title")), (CompactMusicText.Lyrics, Loc.T("歌词", "Lyrics")) },
            v => { Change(s => s.MusicText = v); ShowPage(_page); },
            help: Loc.T("收起栏中间的这行字显示在律动条的左侧。", "This line sits in the collapsed bar, to the left of the equalizer.")) };
        if (S.MusicText == CompactMusicText.Lyrics)
            text.Add(Toggle(Loc.T("纯音乐时显示歌名", "Show the title for instrumentals"), S.MusicInstrumentalTitle, v => Change(s => s.MusicInstrumentalTitle = v),
                help: Loc.T("歌曲本身没有可靠的纯音乐标识，只能根据歌词内容推断，因此可能判断错误。",
                    "Songs carry no reliable instrumental mark, so this is inferred from the lyrics and can be wrong.")));
        Group([.. text,
            MusicChoice(Loc.T("暂停时隐藏", "Hide while paused"), S.MusicPausedHide, new[] { (PausedMusicHide.None, Loc.T("不隐藏", "Nothing")), (PausedMusicHide.Text, Loc.T("文字", "Text")),
                (PausedMusicHide.Cover, Loc.T("封面", "Cover")), (PausedMusicHide.Both, Loc.T("全部", "Both")) }, v => Change(s => s.MusicPausedHide = v),
                help: Loc.T("音乐暂停时，收起状态下隐藏所选内容，恢复播放后重新显示。“文字”指按“中间文字”显示的歌名或歌词；“全部”指文字和封面，不包括律动条。",
                    "While music is paused, the chosen part is hidden when collapsed and comes back when playback resumes. \"Text\" is the title or lyric shown by \"Middle text\"; \"Both\" is the text and the cover, not the equalizer.")),
            Toggle(Loc.T("切歌时短暂显示歌名", "Briefly show the title on a track change"), S.MusicTrackNotice, v => Change(s => s.MusicTrackNotice = v),
                help: Loc.T("换歌时先显示歌名几秒，之后回到“中间文字”的选择。", "On a track change the title shows for a few seconds, then the \"Middle text\" choice returns.")),
            Toggle(Loc.T("显示封面", "Show cover"), S.MusicCompactCover, v => Change(s => s.MusicCompactCover = v))]);
        SectionTitle(Loc.T("展开时", "When expanded"));
        Group(Toggle(Loc.T("显示封面", "Show cover"), S.MusicPageCover, v => Change(s => s.MusicPageCover = v), S.MusicPage),
            Toggle(Loc.T("显示歌词", "Show lyrics"), S.LyricsEnabled, v => Change(s => s.LyricsEnabled = v), S.MusicPage));
    }
    // Full control of every player behind one switch: on turns on what each player needs (the changed libcef.dll of NetEase
    // and KuGou, Apple Music enabled at start), off restores all of them.
    private void FullControl()
    {
        var app = (App)Application.Current;
        var neteaseFile = app.Media?.NeteasePatchState ?? Native.NeteaseInstall.State();
        var kugouFile = app.Media?.KugouState ?? Native.KugouInstall.State();
        bool on = S.NeteaseFullControl || S.NeteaseAutoFullControl || S.KugouFullControl || S.AppleMusicFullControl
            || neteaseFile == LibcefPatchState.Patched || kugouFile == LibcefPatchState.Patched;

        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, Loc.T("完整控制", "Full control"));
        var note = TextLine("", 11, TextTertiary); note.TextWrapping = TextWrapping.Wrap;
        bool busy = false, quiet = false;
        string? failure = null;

        async Task Switch(bool turnOn)
        {
            if (quiet) return;
            if (turnOn && !Confirm(Loc.T("启用完整控制", "Turn on full control"), Loc.T(
                "会修改网易云音乐与酷狗音乐的 libcef.dll，使它们每次启动都是完整控制模式；Apple Music 也会自动启用完整控制。正在运行的音乐软件会重启一次。可以随时关闭，文件会复原。",
                "Changes the libcef.dll of NetEase Cloud Music and KuGou Music so they start with full control every time, and enables full control of Apple Music. A running player restarts once. It can be turned off at any time, which restores the files."),
                Loc.T("启用", "Turn on"), danger: false)) { Set(false); return; }
            busy = true; failure = null; Update();
            var problems = new List<string>();
            try
            {
                if (turnOn)
                {
                    if (app.Media is { } media)
                    {
                        if (await media.EnableNeteaseFileControlAsync() is { } neteaseError) problems.Add(Loc.T("网易云：", "NetEase: ") + neteaseError);
                        if (await media.EnableKugouFullControlAsync(start: false) is { } kugouError) problems.Add(Loc.T("酷狗：", "KuGou: ") + kugouError);
                        media.EnableAppleMusicFullControl();
                    }
                    else
                    {
                        await Task.Run(async () =>
                        {
                            await Native.NeteaseInstall.EnableAsync(false, CancellationToken.None);
                            await Native.KugouInstall.EnableAsync(false, CancellationToken.None);
                        });
                    }
                    Change(s => { s.NeteaseFullControl = true; s.NeteaseAutoFullControl = true; s.KugouFullControl = true; s.AppleMusicFullControl = true; });
                }
                else
                {
                    Change(s => { s.NeteaseFullControl = false; s.NeteaseAutoFullControl = false; s.KugouFullControl = false; s.AppleMusicFullControl = false; });
                    if (app.Media is { } media)
                    {
                        if (await media.DisableNeteaseFileControlAsync() is { } neteaseError) problems.Add(Loc.T("网易云：", "NetEase: ") + neteaseError);
                        await Task.Run(() => Native.KugouInstall.ChangeFileAsync(false, CancellationToken.None));
                    }
                    else
                    {
                        await Task.Run(() => Native.NeteaseInstall.ChangeFileAsync(false, CancellationToken.None));
                    }
                }
            }
            catch (Exception ex) { failure = ex.Message; if (turnOn) Set(false); }
            finally
            {
                busy = false;
                if (problems.Count > 0) failure = string.Join("；", problems);
                app.Media?.CheckKugouInstall(now: true);
                app.Media?.CheckNeteaseFile();
                Update();
            }
        }
        // Moves the switch without acting on it.
        void Set(bool value) { quiet = true; toggle.IsChecked = value; quiet = false; }
        toggle.Checked += async (_, _) => await Switch(true);
        toggle.Unchecked += async (_, _) => await Switch(false);
        void Update()
        {
            var media = app.Media;
            bool working = busy || media is { RestartingKugou: true } || media is { RestartingNetease: true };
            toggle.IsEnabled = !working && S.MusicPage;
            note.Text = working ? Loc.T("正在处理…", "Working…")
                : failure is not null ? Loc.T("启用失败：", "Could not turn on: ") + failure
                : Loc.T("网易云音乐 · 酷狗音乐 · Apple Music", "NetEase Cloud Music · KuGou Music · Apple Music");
            note.Foreground = UI.Brush(failure is not null && !working ? _palette.Negative : TextTertiary);
            note.Visibility = Visibility.Visible;
        }
        Update();
        var row = Row(Loc.T("完整控制", "Full control"), toggle, note,
            () => Loc.T("开启后，三个播放器都显示真实进度，可拖动进度条、切换播放模式，歌词随进度滚动。开启时会修改网易云与酷狗的 libcef.dll，之后每次启动都是完整控制模式。",
                "With this on, all three players show the real position, allow the position to be dragged and the playback mode switched, and scroll the lyrics with the song. Turning it on changes the libcef.dll of NetEase and KuGou, so they start in full control mode every time."));
        row.Loaded += (_, _) => app.MediaChanged += Changed;
        row.Unloaded += (_, _) => app.MediaChanged -= Changed;
        return;

        void Changed() => Dispatcher.BeginInvoke(Update);
    }

    private void MusicSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MusicWidth, 440, 1200, value => Resize(s => s.MusicWidth = value, DeckPage.Music)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MusicHeight, 140, 400, value => Resize(s => s.MusicHeight = value, DeckPage.Music)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MusicSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MusicSize", IsChecked = (S.MusicWidth, S.MusicHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("音乐快捷尺寸 ", "Music quick size ") + PanelSizes.Name(size)); choice.Checked += (_, _) => { Resize(s => { s.MusicWidth = w; s.MusicHeight = h; }, DeckPage.Music); ShowPage(2); }; sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
