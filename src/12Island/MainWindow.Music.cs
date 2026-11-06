using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TwelveIsland.Core;
using TwelveIsland.Native;
using MediaState = TwelveIsland.Core.MediaState;

namespace TwelveIsland;

public partial class MainWindow
{
    private static readonly DependencyProperty SuppressMusicToolTipsProperty = DependencyProperty.RegisterAttached(
        "SuppressMusicToolTips", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    static MainWindow()
    {
        // ToolTipOpening is a direct event. A class handler covers nested template elements as well as their controls.
        static void Suppress(object sender, ToolTipEventArgs e)
        {
            if (sender is DependencyObject element && (bool)element.GetValue(SuppressMusicToolTipsProperty)) e.Handled = true;
        }
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
    }
    private DeckPage _page;
    private double _pageWidth = 760, _usageWidth = 760, _widestPage = 760;
    private MediaSessions? _media => _app.Media;
    private MediaTrack? _mediaTrack;
    private IReadOnlyList<MediaTrack> _mediaTracks = [];
    private string? _pinnedMedia;
    private BitmapSource? _mediaCover;
    private Brush _musicAccent = MusicVisuals.Accent(null);
    // The color the cover gave, if it gave one; _musicAccent is the theme's equalizer color otherwise.
    private Color? _coverAccent;
    private Lyrics _lyrics = Lyrics.Empty;
    private CancellationTokenSource? _lyricsRequest;
    // Completed when Apple Music writes a lyric file while the current Apple Music song has no lyrics of Apple's.
    private TaskCompletionSource? _appleLyricsWritten;
    private string _lyricsKey = "", _musicRenderKey = "", _compactLyric = "";
    // Drives the equalizer, which must keep moving while the cursor moves; see PromptTimer.
    private PromptTimer _musicTick = null!;
    private DateTimeOffset _lastMusicTick = DateTimeOffset.UtcNow, _positionTick, _trackNoticeUntil;
    private readonly TrackNoticeGate _trackNotice = new();
    private bool _musicStarted, _seeking, _updatingSeek, _commandBusy;
    private MediaTrack? _seekTrack;
    private readonly MediaSeekPreview _seekPreview = new();
    // Keeps the progress bar's room while the player moves to its next song; the timer lays the page out again when it ends.
    private readonly MusicTimelineHold _timelineHold = new();
    private DispatcherTimer? _timelineHoldEnd;
    private Slider? _musicSeek;
    private readonly ScaleTransform _musicCoverScale = new(1, 1);
    internal MusicLayout? CurrentMusicLayout { get; private set; }
    private TextBlock? _musicElapsed, _musicDuration, _lyricPrevious, _lyricNext, _musicMessage;
    // The current line scrolls when it is wider than the column; the lines around it are cut off with an ellipsis.
    private MusicMarquee? _lyricCurrent, _musicTitle;
    // Without the cover the lyrics column shows more lines around the current one (MusicLayout.Columns).
    private TextBlock[] _lyricsAbove = [], _lyricsBelow = [];
    private Grid? _musicInfo;
    private Canvas? _musicControls;
    private Button? _musicFullControl;
    private TextBlock? _musicSmtcHint;
    private readonly Dictionary<MediaCommand, (MusicControlButton Button, string Icon)> _musicControlButtons = new();
    private MusicEqualizer? _musicEqualizer, _compactEqualizer;
    private readonly double[] _flowLevels = new double[CompactMusicLayout.MaximumBars];
    private MusicMarquee? _compactMarquee;
    // The collapsed title that has scrolled its laps (MusicMarquee.Laps): a row built again for it keeps it resting, until the
    // text changes or the pointer comes over the island.
    private string? _compactTitleRested;
    private ClockFace? _compactClock;
    private QuotaCarousel? _quotaCarousel;
    private TimeSpan _quotaElapsed;
    // The readings' carousel while they are wider than their region, and its place in the cycle across rebuilds.
    private ReadingsCarousel? _stripCarousel;
    // The row's readings, a strip each (RenderCompactMusic), which take every new reading (UpdateCompactStrips).
    private readonly List<MonitorStrip> _rowStrips = [];
    private TimeSpan _stripElapsed;
    // The page on screen: the chosen one while it is shown, otherwise the one DeckPages falls back to.
    internal DeckPage CurrentPage => DeckPages.Resolve(Settings, _page);
    internal bool IsMusicPage => CurrentPage == DeckPage.Music;
    internal bool IsMonitorPage => CurrentPage == DeckPage.Monitor;
    internal bool IsUsagePage => CurrentPage == DeckPage.Usage;
    internal bool IsNotifyPage => CurrentPage == DeckPage.Notify;
    internal bool IsClipboardPage => CurrentPage == DeckPage.Clipboard;
    internal bool IsConvertPage => CurrentPage == DeckPage.Convert;
    internal CompactMusicLayout? CurrentCompactLayout { get; private set; }

    private void InitializeMusic()
    {
        _page = Settings.LastPage;
        _sourceMenuWatch = new(Dispatcher, TickSourceMenu) { Interval = TimeSpan.FromMilliseconds(40) };
        _musicTick = new(Dispatcher, TickMusic) { Interval = TimeSpan.FromMilliseconds(33) };
        IsVisibleChanged += (_, _) => UpdateMusicTimer();
        _app.MediaChanged += MediaChanged;
        _app.Lyrics.AppleLyricsWritten += AppleLyricsWritten;
        DisplayPower.Changed += DisplayPowerChanged;
    }
    private void StartMusic()
    {
        _musicStarted = true;
        ApplyMusicSettings();
        // A panel opened while music plays takes the current song without announcing it as a new one.
        if (_media is { } media) { _pinnedMedia = media.Pinned; ApplyMediaUpdate(media.Current, media.Tracks, media.Cover, notice: false); }
    }
    private void ApplyMusicSettings()
    {
        if (!Settings.MusicPage) CloseSourceMenu();
        _page = CurrentPage;
        if (!Settings.UsagePage || !IsUsagePage) _details = false;
        if (_musicStarted) _app.StartMedia();
        if (!Settings.MediaWanted) { _mediaTrack = null; _mediaCover = null; _mediaTracks = []; _seekPreview.Observe(null, DateTimeOffset.UtcNow); }
        UpdateLyrics();
        _musicRenderKey = "";
    }
    private void ApplyDefaultMusicPage()
    {
        var wanted = DeckPages.Opening(Settings.DefaultPage) ?? _page;
        if (wanted != _page && DeckPages.Shown(Settings, wanted)) SelectPage(wanted, remember: false);
    }
    // The page switch moves as one: the page left behind leaves as a picture toward the side it is left for, while the next
    // page is built at once and comes in from the other side and the panel takes its size (PageHandover).
    internal void SelectPage(DeckPage page, bool remember = true)
    {
        if (!DeckPages.Shown(Settings, page)) return;
        bool changed = CurrentPage != page;
        if (changed) { CloseSourceMenu(); PageToolTips.CloseWithin(PageContent(CurrentPage)); }
        if (changed && _expanded && !_preview)
        {
            // Start before resizing: the shrinking surface can immediately fire MouseLeave.
            _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _pageSwitchHold.Start();
        }
        if (remember) { Settings.LastPage = page; _app.QueueSave(); }
        bool animate = changed && _expanded && !_preview && Soft.Enabled;
        var before = CurrentPage;
        var from = PageContent(before);
        var picture = animate ? PageHandover.Capture(from) : null;
        var place = PageHandover.Place(from);
        ShowPage(page, animatePage: changed && _expanded && !_preview);
        bool back = Settings.PageOrder.IndexOf(page) < Settings.PageOrder.IndexOf(before);
        _handover.Send(picture, place, PageContent(page), back ? Handover.Back : Handover.Forward);
    }
    private FrameworkElement PageContent(DeckPage page) => page switch
    {
        DeckPage.Music => MusicContent, DeckPage.Monitor => MonitorContent, DeckPage.Notify => NotifyContent,
        DeckPage.Clipboard => ClipboardContent, DeckPage.Convert => ConvertContent, _ => UsageContent
    };
    // The page buttons in the switch's own order; every page has one.
    private Button[] PageButtons => [AIUsageButton, MusicPageButton, MonitorPageButton, NotifyPageButton, ClipboardPageButton, ConvertPageButton];
    // Shows one page's content and hides the others, so a page only ever reads back its own.
    private void ShowPageContent(DeckPage page)
    {
        foreach (var content in new[] { UsageContent, MusicContent, MonitorContent, NotifyContent, ClipboardContent, ConvertContent })
            content.Visibility = ReferenceEquals(content, PageContent(page)) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowPage(DeckPage page, bool animatePage)
    {
        _detailPopup?.Close(); _details = false; _page = page; _musicRenderKey = ""; _handover.Drop();
        ApplySettings(animatePage);
    }
    // The wheel walks the shown pages (down moves along the switch, up moves back) or steps the system volume. A wheel that
    // a scrolling part of a page uses (the lyrics, the model details) never arrives, as those mark it handled on the way up;
    // an open menu or the detail popup keeps the wheel to itself as well.
    private void PanelWheel(object sender, MouseWheelEventArgs e)
    {
        if (Settings.MouseWheel == MouseWheelAction.None || !_expanded || _preview || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) return;
        if (Settings.MouseWheel == MouseWheelAction.Volume) { WheelVolume(e.Delta); e.Handled = true; return; }
        var shown = DeckPages.Visible(Settings);
        if (shown.Count < 2) return;
        int at = shown.ToList().IndexOf(CurrentPage);
        if (at < 0) return;
        int next = e.Delta > 0 ? at - 1 : at + 1;
        if (next < 0 || next >= shown.Count) return;
        SelectPage(shown[next]);
        e.Handled = true;
    }
    // A precision touchpad sends parts of a notch; they add up until a whole notch (120) presses the volume key once.
    private int _wheelVolumeRest;
    private void WheelVolume(int delta)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelVolumeRest)) _wheelVolumeRest = 0;
        _wheelVolumeRest += delta;
        int notches = _wheelVolumeRest / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) return;
        _wheelVolumeRest -= notches * Mouse.MouseWheelDeltaForOneLine;
        Native.VolumeKeys.Press(notches > 0, Math.Abs(notches));
    }

    private void MusicPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Music);
    private void MonitorPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Monitor);
    private void NotifyPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Notify);
    private void ClipboardPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Clipboard);
    private void ConvertPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Convert);
    // Where the usage page's application tabs start: after the page switch, which holds one 28 DIP button per shown page.
    private double ApplicationPagesLeft(double side)
    {
        int pages = DeckPages.Visible(Settings).Count;
        // The switch: 2 DIP of padding either side, one 28 DIP button per shown page and 1 DIP between neighbours.
        return side + Math.Max(36, 4 + 28 * pages + Math.Max(0, pages - 1)) + 8;
    }
    // The header shared by both pages (see PanelHeader). Its row holds the page's top margin, so the clock, the switch and the
    // right group are centred on one line, and the switch and the gear line up with the page's content edges. Toolbar's own
    // height replaces its binding to the row: the music page takes the usage page's controls (PanelHeader.WithControlsOf), so
    // its band can be shorter than its row, with the rest below the controls; the row may also be one DIP taller, and the
    // controls are centred in the even part. Toolbar lies outside ContentCanvas's rows, so a band taller than the row is not
    // clipped. Only layout values change, and setting an unchanged value does nothing.
    private PanelHeader _header = PanelHeader.For(36, 20);
    private void ApplyHeader(PanelHeader header)
    {
        _header = header;
        ToolbarRow.Height = new GridLength(header.Row);
        Toolbar.Height = header.Height;
        PageSwitch.Margin = new Thickness(header.Side, 0, 0, 0);
        FitPageButtons(header);
        foreach (var button in PageButtons)
        {
            // A local corner for these two buttons only; PanelIconButton keeps its 5 DIP corner elsewhere.
            button.ApplyTemplate();
            if (button.Template?.FindName("Chrome", button) is Border chrome) chrome.CornerRadius = new CornerRadius(PanelTheme.Corner(PanelHeader.ButtonRadius));
        }
        PageCapsule.CornerRadius = new CornerRadius(PanelTheme.Corner(PanelHeader.ButtonRadius));
        SettingsButton.Height = header.SettingsHeight;
        if (SettingsButton.Parent is FrameworkElement right) right.Margin = new Thickness(0, 0, header.RightMargin, 0);
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(header.Side), 0, 0, 0);
    }
    private void RenderPageSwitch()
    {
        SetValue(SuppressMusicToolTipsProperty, IsMusicPage);
        foreach (var control in PageButtons.Append<FrameworkElement>(MediaSourceButton).Append(ClockLabel))
            ToolTipService.SetIsEnabled(control, !IsMusicPage);
        PageSwitch.Visibility = _details && IsUsagePage ? Visibility.Collapsed : Visibility.Visible;
        OrderPageButtons();
        foreach (var button in PageButtons)
        {
            var page = PageOf(button);
            bool current = CurrentPage == page;
            button.Visibility = DeckPages.Shown(Settings, page) ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = UI.Brush(current ? UI.Primary : UI.Secondary);
        }
        PlacePageCapsule();
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(_header.Side), 0, 0, 0);
        // With the entry switched on the source menu has one more row. Without a source that row stands alone in the button.
        bool entry = FullControlEntryShown, alone = entry && _mediaTrack is null;
        MediaSourceButton.Visibility = IsMusicPage && (_mediaTrack is not null || entry) ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceButton.IsEnabled = !alone || _media?.RestartingNetease != true;
        // An open menu keeps its rows; only the entry's availability follows the restart.
        if (MediaSourceButton.ContextMenu is { IsOpen: true } open)
            foreach (var item in open.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "action"))) item.IsEnabled = _media?.RestartingNetease != true;
        AutomationProperties.SetName(MediaSourceButton, alone ? FullControlButtonLabel : Loc.T("切换播放来源", "Switch playback source"));
        MediaSourceLabel.Text = _mediaTrack is { } playing ? Loc.Label(playing.Source) : alone ? FullControlButtonLabel : "";
        MediaSourcePin.Visibility = _pinnedMedia is not null && _pinnedMedia == _mediaTrack?.Id ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceChevron.Visibility = _mediaTrack is not null && (_mediaTracks.Count > 1 || entry) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MediaChanged()
    {
        if (_media is null) return;
        _pinnedMedia = _media.Pinned;
        ApplyMediaUpdate(_media.Current, _media.Tracks, _media.Cover);
    }
    private void ApplyMediaUpdate(MediaTrack? track, IReadOnlyList<MediaTrack> tracks, BitmapSource? cover, bool notice = true)
    {
        bool changedSong = _mediaTrack?.Id != track?.Id || _mediaTrack is not null && track is not null && !SameMediaSong(_mediaTrack, track);
        bool lyricsChanged = changedSong || _mediaTrack?.SongKey != track?.SongKey || _mediaTrack?.SongId != track?.SongId || _mediaTrack?.EmbeddedLyrics != track?.EmbeddedLyrics;
        var old = _mediaTrack;
        _mediaTrack = track; _mediaTracks = tracks;
        var now = DateTimeOffset.UtcNow;
        _seekPreview.Observe(track, now);
        _timelineHold.Observe(track, now); ScheduleTimelineHoldEnd(now);
        bool coverChanged = _mediaCover != cover;
        if (coverChanged) { _mediaCover = cover; _musicAccent = MusicVisuals.Accent(_mediaCover, out _coverAccent); }
        // A new song scrolls its title again, even one of the same name.
        if (changedSong) _compactTitleRested = null;
        if (changedSong)
        {
            _seeking = false; _seekTrack = null;
            // A notice still on screen switches to the new title instead of giving way to the summary until playback starts.
            if (_mediaTrack is not null && Settings.MusicTrackNotice && notice) _trackNotice.Arm(now); else _trackNotice.Disarm();
        }
        if (_trackNotice.Observe(_mediaTrack, now)) _trackNoticeUntil = now.AddSeconds(4);
        if (lyricsChanged) UpdateLyrics();
        if (changedSong || lyricsChanged || coverChanged || old?.State != _mediaTrack?.State || old?.HasTimeline != _mediaTrack?.HasTimeline) RenderCompact();
        if (IsMusicPage) RenderMusic();
        // Native lyric events must update compact text even while playback is paused.
        UpdateMusicPosition();
        UpdateMusicTimer();
    }
    private void ScheduleTimelineHoldEnd(DateTimeOffset now)
    {
        _timelineHoldEnd?.Stop();
        if (_timelineHold.Until is not { } until || until <= now) return;
        if (_timelineHoldEnd is null)
        {
            var timer = _timelineHoldEnd = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            timer.Tick += (_, _) => { timer.Stop(); if (IsMusicPage) RenderMusic(); };
        }
        // A little after the end, so the page laid out then no longer holds.
        _timelineHoldEnd.Interval = until - now + TimeSpan.FromMilliseconds(50); _timelineHoldEnd.Start();
    }
    // The expanded page and the compact text choose lyrics independently; either one is enough to look them up.
    private bool CompactLyrics => Settings.ShowsMusic(Settings.Style) && Settings.MusicText == CompactMusicText.Lyrics;
    private bool NeedsLyrics => Settings.MusicPage && Settings.LyricsEnabled || CompactLyrics;
    // While the song is paused the collapsed title or lyric, the cover, or both give way (DeckSettings.MusicPausedHide); the state
    // change builds the row again.
    private bool PausedHides(PausedMusicHide part) => _mediaTrack?.State == MediaState.Paused && (Settings.MusicPausedHide == part || Settings.MusicPausedHide == PausedMusicHide.Both);
    private string LyricsKey(MediaTrack? track) => NeedsLyrics && track is not null
        ? string.Join("\n", track.Id, track.SongKey, track.SongId, track.EmbeddedLyrics) : "";
    private async void UpdateLyrics()
    {
        string key = LyricsKey(_mediaTrack);
        if (_lyricsKey == key) return;
        _lyricsKey = key; _lyricsRequest?.Cancel(); _lyricsRequest?.Dispose(); _lyricsRequest = null;
        _lyrics = Lyrics.Empty; _compactLyric = ""; _musicRenderKey = "";
        if (key.Length == 0) return;
        var requested = _mediaTrack!;
        if (!requested.IsQqMusic && !string.IsNullOrWhiteSpace(requested.EmbeddedLyrics))
        {
            _lyrics = Lyrics.Parse(requested.EmbeddedLyrics, null, requested.Duration.TotalSeconds, requested.Source);
            if (IsMusicPage) RenderMusic(); RenderCompact(); return;
        }
        var request = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token); _lyricsRequest = request;
        try
        {
            // Apple Music writes a song's lyrics to its cache as the song starts, which can be after the song changes here, or later
            // still: they are looked for again 3 and 15 seconds later and whenever Apple Music writes a lyric file, keeping any
            // lyrics already shown.
            for (int attempt = 0; ; attempt++)
            {
                var lyrics = await _app.Lyrics.FindAsync(requested, true, request.Token);
                if (request.IsCancellationRequested || _lyricsKey != key) return;
                if (lyrics.Lines.Count > 0 || lyrics.Instrumental || attempt == 0)
                {
                    _lyrics = lyrics; _musicRenderKey = "";
                    if (IsMusicPage) RenderMusic(); RenderCompact();
                }
                if (!requested.IsAppleMusic || lyrics.Source == LyricsService.AppleSource) break;
                var written = (_appleLyricsWritten = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                await (attempt < 2 ? Task.WhenAny(written, Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 15), request.Token)) : written.WaitAsync(request.Token));
                request.Token.ThrowIfCancellationRequested();
                // The file is read once Apple Music has finished writing it.
                if (written.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Lyrics are optional; provider failures keep the no-lyrics layout. */ }
    }
    private void AppleLyricsWritten() => Dispatcher.BeginInvoke(() => _appleLyricsWritten?.TrySetResult());
    private void ToggleSourcePin(ContextMenu menu, MenuItem item, string id)
    {
        bool pin = !Equals(item.Tag, "pinned");
        _pinnedMedia = pin ? id : null;
        foreach (var other in menu.Items.OfType<MenuItem>().Where(other => !Equals(other.Tag, "action")))
        {
            other.Tag = pin && other == item ? "pinned" : null;
            if (pin) other.IsChecked = other == item;
        }
        _media?.Pin(_pinnedMedia);
        RenderPageSwitch();
    }
    // The heart of a liked song, in the theme's color for it (NetEase Cloud Music's red in the dark theme).
    private static string LikedColor => PanelTheme.Hex("liked");
    internal static string FullControlEntryLabel => Loc.T("网易云音乐 · 完整控制", "NetEase Cloud Music · Full control");
    // Alone in the toolbar the entry has at most 150 DIP; the full English name would lose "Full control" to the ellipsis.
    internal static string FullControlButtonLabel => Loc.T("网易云音乐 · 完整控制", "NetEase full control");
    private bool FullControlEntryShown => Settings.NeteaseFullControlEntry && _media?.NeteaseInstalled == true;
    // Started from the source menu or the lone button. Only the disabled entry shows that it is under way;
    // neither progress nor a failure is reported, and a later click is ignored until it ends.
    private async Task StartNeteaseFullControl()
    {
        if (_media is not { RestartingNetease: false } media) return;
        var action = !media.NeteaseRunning ? NeteaseControlAction.Start : media.NeteaseFullControl ? NeteaseControlAction.Reconnect : NeteaseControlAction.Restart;
        var dialog = new NeteaseControlWindow(Settings.Theme, action) { Owner = this };
        if (dialog.ShowDialog() != true || _media != media || media.RestartingNetease) return;
        var pending = media.EnableFullControlAsync(quiet: true);
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
        await pending;
        if (_media != media) return;
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
    }
    private void MediaSource_Click(object sender, RoutedEventArgs e)
    {
        if (MediaSourceButton.ContextMenu?.IsOpen == true) { CloseSourceMenu(); return; }
        if (_mediaTrack is null) { if (FullControlEntryShown) _ = StartNeteaseFullControl(); return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        menu.SetValue(SuppressMusicToolTipsProperty, true);
        foreach (var track in _mediaTracks)
        {
            var state = track.State switch { MediaState.Playing => Loc.T("播放中", "Playing"), MediaState.Paused => Loc.T("已暂停", "Paused"),
                MediaState.Stopped => Loc.T("已停止", "Stopped"), _ => Loc.T("状态未知", "Unknown state") };
            var label = $"{Loc.Label(track.Source)} · {track.Title} · {state}";
            // The menu's viewport clip would turn the row grayscale; the menu's background beneath it is opaque.
            var row = new Grid(); RenderOptions.SetClearTypeHint(row, ClearTypeHint.Enabled);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = new TextBlock { Text = Loc.Label(track.Source), FontSize = 12.5, Foreground = UI.Brush(UI.Primary), MaxWidth = 100,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
            var title = new MusicMarquee { Text = track.Title, FontSize = 12, Foreground = UI.Brush(PanelTheme.Hex("menuDetail")),
                CenterWhenFits = false, IsHitTestVisible = false, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
            var status = new TextBlock { Text = state, FontSize = 11, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.NoWrap };
            foreach (var text in new FrameworkElement[] { source, title, status }) text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1); Grid.SetColumn(status, 2); row.Children.Add(source); row.Children.Add(title); row.Children.Add(status);
            var item = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = row, IsCheckable = true, IsChecked = track.Id == _mediaTrack?.Id };
            AutomationProperties.SetName(item, label);
            item.Tag = track.Id == _pinnedMedia ? "pinned" : _mediaTracks.Count < 2 ? "single" : null;
            // The pin button marks its click handled, so MenuItem neither selects the row nor closes the menu.
            item.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) => { e.Handled = true; ToggleSourcePin(menu, item, track.Id); }));
            item.Click += (_, _) => _media?.Select(track.Id); menu.Items.Add(item);
        }
        if (FullControlEntryShown)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
            var label = new TextBlock { Text = FullControlEntryLabel, FontSize = 12.5, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var entry = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = label, Tag = "action", IsEnabled = _media?.RestartingNetease != true };
            AutomationProperties.SetName(entry, FullControlEntryLabel);
            entry.Click += (_, _) => { CloseSourceMenu(); _ = StartNeteaseFullControl(); }; menu.Items.Add(entry);
        }
        WatchSourceMenu(menu);
        MediaSourceButton.ContextMenu = menu; menu.PlacementTarget = MediaSourceButton; menu.Placement = PlacementMode.Bottom;
        // A pop-up is not part of the panel's visual tree, so it takes the panel's scale itself.
        UI.Scale(menu, _scale); menu.VerticalOffset = 6 * _scale; ResizeSourceMenu(menu); menu.IsOpen = true;
    }
    // Guidance shown in place of the transport when Netease runs without SMTC. Both sentences stay at every size:
    // what is required and where to switch it on. Only the type size and the entry's label adapt to the row.
    internal static string SmtcHintWhy => Loc.T("开启网易云的 SMTC 后，才能在这里控制播放", "Turn on SMTC in NetEase to control playback here");
    // The English hint names the menu items as NetEase Cloud Music's Chinese interface shows them.
    internal static string SmtcHintHow => Loc.T("位置：网易云音乐 › 设置 › 系统 › 开启 SMTC", "Where: NetEase Cloud Music › 设置 › 系统 › 开启 SMTC");
    // The song and the form (PageHandover) the media page was last built with: a new song swaps in, a new form (the lyric lines
    // the room holds, the title merged with the artist, the message or the entry shown) morphs in, as when a drag crosses a size step.
    private string _musicSong = "", _musicForm = "";
    private bool _musicHadLyrics;
    private long _songArrived;
    private BitmapSource? _musicCoverShown;
    // The titles of a new song rise into place. A rebuild during the change continues it from where it is.
    private void RiseIn(FrameworkElement titles)
    {
        if (!Soft.Enabled) return;
        double done = Math.Clamp((Environment.TickCount64 - _songArrived) / (double)Soft.Move, 0, 1), start = Curves.Follow(done);
        int left = (int)Math.Max(1, Soft.Move * (1 - done));
        var shift = new TranslateTransform();
        titles.RenderTransform = shift;
        var rise = Soft.Fade(8 * (1 - start), 0, left, Soft.Follow); rise.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        var fade = Soft.Fade(start, 1, left, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
        titles.BeginAnimation(OpacityProperty, fade);
    }
    private void RenderMusic()
    {
        // Only a page that was on screen hands over; one that arrives by the page switch or with the panel opening comes in whole.
        bool shown = MusicContent.IsVisible && _expanded && !_preview;
        RefreshStatus();
        ShowPageContent(DeckPage.Music);
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double h = ContentCanvas.Height, w = ContentCanvas.Width;
        var track = _mediaTrack;
        if (_seeking && _seekTrack is { } seeking && track is not null && SameMediaSong(seeking, track))
        { UpdateMusicPosition(); return; }
        bool playerLyrics = track?.IsQqMusic == true && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental;
        bool hasPlayerLyric = playerLyrics && Settings.LyricsEnabled && !string.IsNullOrWhiteSpace(track?.CurrentLyric) && track?.State != MediaState.Stopped;
        bool hasLyrics = playerLyrics ? hasPlayerLyric : (_lyrics.Lines.Count > 0 || _lyrics.Instrumental) && Settings.LyricsEnabled;
        bool logMode = track?.IsNeteaseLog == true;
        bool appleEntry = track is { IsAppleMusic: true } && _media?.AppleMusicFullControl == false;
        bool kugouEntry = track is { IsKugou: true } && (_media?.CanEnableKugouFullControl == true || _media?.RestartingKugou == true);
        bool showSmtcHint = logMode && track is { CanToggle: false, CanPrevious: false, CanNext: false }
            && _media?.RestartingNetease != true && _media?.Error is null;
        string message = _media?.RestartingNetease == true ? Loc.T("正在重启网易云…", "Restarting NetEase Cloud Music…")
            : _media?.RestartingKugou == true ? Loc.T("正在重启酷狗…", "Restarting KuGou Music…")
            : _media?.Error ?? (track is { IsKugou: true } && _media?.KugouError is { } kugouError ? Loc.T("未能启用酷狗完整控制：", "KuGou full control could not be enabled: ") + kugouError : "");
        var modes = track is null ? [] : MusicPlaybackModes.Supported(track);
        // While the timeline is held (MusicTimelineHold) the bar stays in its place, empty and without times.
        bool timeline = track?.HasTimeline == true || track is not null && _timelineHold.Holds(DateTimeOffset.UtcNow);
        var layout = MusicLayout.Calculate(w, h, hasLyrics, timeline, message.Length > 0, logMode || appleEntry || kugouEntry, transport: !showSmtcHint,
            lyricsExpected: Settings.LyricsEnabled);
        CurrentMusicLayout = layout;
        var c = layout.Metrics;
        // With the usage page on, the controls are the usage page's, as it would lay them out now; see PanelHeader.WithControlsOf.
        ApplyHeader(Settings.UsagePage ? PanelHeader.Music(c).WithControlsOf(UsageHeader(_usageWidth, UsagePageHeight)) : PanelHeader.Music(c));
        bool animate = Settings.Animations && SystemParameters.ClientAreaAnimation;
        // Without the cover the lyrics, or a message, take a column of their own; with neither the titles have the width.
        bool lyricColumn = !Settings.MusicPageCover && (hasLyrics || message.Length > 0);
        string key = $"{track?.Id}|{track?.SongKey}|{logMode}|{appleEntry}|{kugouEntry}|{_media?.RestartingNetease}|{_media?.RestartingKugou}|{track?.State}|{track?.CanToggle}|{track?.CanPrevious}|{track?.CanNext}|{track?.CanSeek}|{track?.CanRepeat}|{track?.CanShuffle}|{track?.Shuffle}|{track?.Repeat}|{track?.CanLike}|{track?.Liked}|{track?.HasTimeline}|{timeline}|{playerLyrics}|{hasPlayerLyric}|{w}|{h}|{_lyrics.GetHashCode()}|{_mediaCover?.GetHashCode()}|{_media?.Error}|{_media?.KugouError}|{Settings.LyricsEnabled}|{Settings.MusicPageCover}|{Settings.EqualizerColor}|{Settings.EqualizerCustomColor}|{animate}";
        if (_musicRenderKey == key) { UpdateMusicPosition(); return; }
        _musicRenderKey = key;
        string song = track is null ? "" : $"{track.Id}|{track.SongKey}|{track.Title}|{track.Artist}";
        // Lyrics that arrive after the song, or come and go with the player's line, only fade in their own lines (lyricsArrive):
        // the form counts the lyric lines the room holds, not whether lyrics are there.
        string form = $"{track is null}|{layout.LyricRoom}|{layout.MergeTitle}|{layout.ShowFullControl}|{layout.ShowMessage}|{Settings.LyricsEnabled}|{Settings.MusicPageCover}|{lyricColumn}|{showSmtcHint}|{timeline}";
        bool lyricsArrive = shown && hasLyrics && !_musicHadLyrics && Soft.Enabled;
        _musicHadLyrics = hasLyrics;
        // A new song changes the titles and perhaps the cover; the rest of the page is the same and stays still. The titles
        // rise into place (RiseIn) and a new cover fades in. A player announces a song in a few updates (title, artist, and a
        // timeline that may go and come back); the first starts the change and the rest carry it on instead of starting it
        // again, and a form that changes during it is taken at once rather than morphed twice.
        bool newCover = shown && !ReferenceEquals(_mediaCover, _musicCoverShown);
        _musicCoverShown = _mediaCover;
        if (shown && song != _musicSong && Environment.TickCount64 - _songArrived >= Soft.Move) _songArrived = Environment.TickCount64;
        long sinceSong = Environment.TickCount64 - _songArrived;
        bool songChanging = sinceSong < Soft.Move;
        // A slow player may bring the timeline back a little after the move: the form is not morphed for twice its length.
        var motion = shown && form != _musicForm && sinceSong >= 2 * Soft.Move ? Handover.Morph : Handover.None;
        _musicSong = song; _musicForm = form;
        // Transport buttons that stay glide to their new places. While the panel is dragged they follow it frame by frame like
        // the rest of the page, and glide only when its form changes.
        bool glide = shown && animate && (!Resizing || motion != Handover.None);
        // Keep the transport layer attached: replacing a hovered button restarts
        // hit testing and its fade-in on every metadata, artwork or lyric update.
        _handover.Hold(MusicContent, keep: _musicControls);
        _musicSeek = null; _musicElapsed = null; _musicDuration = null; _musicEqualizer = null; _musicTitle = null; _lyricCurrent = null; _lyricPrevious = null; _lyricNext = null; _lyricsAbove = []; _lyricsBelow = []; _musicMessage = null; _musicInfo = null;
        MusicContent.Margin = new Thickness(c.Side, 0, c.Side, c.Bottom);
        if (track is null)
        {
            MusicContent.Children.Clear(); _musicControls = null; _musicFullControl = null; _musicSmtcHint = null; _musicControlButtons.Clear();
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(200, w - 60) };
            var icon = MusicVisuals.Icon("music", 26); icon.Margin = new Thickness(0, 0, 0, 8); empty.Children.Add(icon);
            empty.Children.Add(UI.Fixed(Loc.T("没有正在播放的媒体", "Nothing is playing"), 14, 22, UI.Secondary));
            if (_media?.Error is { } error)
            {
                var hint = UI.Line(error, 11.5, UI.Tertiary); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            }
            MusicContent.Children.Add(empty); _handover.Release(empty, motion); return;
        }
        var body = new Grid();
        bool synchronized = playerLyrics || _lyrics.CanSynchronize(track);
        if (!Settings.MusicPageCover) MusicColumns(body, layout, track, lyricColumn, hasLyrics, synchronized, playerLyrics, message, shown && songChanging, lyricsArrive);
        else
        {
            var upper = new Grid { Height = layout.Cover, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.CoverY, 0, 0) };
            upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Cover) });
            upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Gap) });
            upper.ColumnDefinitions.Add(new ColumnDefinition());
            body.Children.Add(upper);
            var art = MusicVisuals.Cover(_mediaCover, layout.Cover, layout.CoverRadius, outlined: true);
            art.RenderTransformOrigin = new Point(.5, .5); art.RenderTransform = _musicCoverScale;
            MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleXProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
            MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleYProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
            AutomationProperties.SetName(art, Loc.T("专辑封面", "Album cover")); upper.Children.Add(art);
            AttachCoverFocus(art);
            if (newCover && animate) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; art.BeginAnimation(OpacityProperty, fade); }
            var right = new Grid(); Grid.SetColumn(right, 2); upper.Children.Add(right);
            int lyricLines = playerLyrics ? Math.Min(1, layout.LyricLines) : layout.LyricLines;
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.HeaderHeight) });
            right.RowDefinitions.Add(new RowDefinition());
            double equalizerWidth = MusicEqualizerWidth(c);
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(equalizerWidth + MusicTitleFit.EqualizerGap) });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            // The title shares its line with the equalizer, so its room shrinks as the bars grow (MusicTitleFit); it rests when it
            // fits and scrolls when it does not. The merged title of a short page carries the artist as a second run.
            var title = PageTitle(track, c, center: false, merge: layout.MergeTitle); titles.Children.Add(title);
            if (!layout.MergeTitle)
            {
                string artist = string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => s.Length > 0));
                var line = UI.Fixed(artist, c.Artist, c.ArtistLine, UI.Secondary); titles.Children.Add(line);
                ShowCutText(line, artist);
            }
            header.Children.Add(titles);
            if (shown && songChanging) RiseIn(titles);
            _musicEqualizer = new MusicEqualizer { Bars = Settings.MusicEqualizerBars, Width = equalizerWidth, Height = c.Equalizer, Color = EqualizerColor, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, (c.TitleLine - c.Equalizer) / 2, 0, 0) };
            Grid.SetColumn(_musicEqualizer, 1); header.Children.Add(_musicEqualizer); right.Children.Add(header);

            var middle = new Grid(); Grid.SetRow(middle, 1); right.Children.Add(middle); _musicInfo = middle;
            if (hasLyrics && lyricLines > 0 && synchronized)
            {
                var lyrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                // All lines share one size; only its color, the theme's primary text, marks the current one.
                if (lyricLines == 3) { _lyricPrevious = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricPrevious); }
                _lyricCurrent = new MusicMarquee { FontSize = c.Lyric, Height = c.LyricLine, CenterWhenFits = false, Foreground = LyricInk,
                    Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
                lyrics.Children.Add(_lyricCurrent); UpdateLyricScrolling();
                if (lyricLines >= 2) { _lyricNext = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricNext); }
                lyrics.Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible; middle.Children.Add(lyrics);
            }
            else if (hasLyrics && lyricLines > 0)
            {
                var text = UI.Line(_lyrics.Instrumental && _lyrics.Lines.Count == 0 ? "♪" : string.Join("\n", _lyrics.Lines.Select(line => line.Text)), c.Lyric, UI.Secondary);
                text.TextWrapping = TextWrapping.Wrap; text.LineHeight = c.LyricLine; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                var lyrics = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalAlignment = VerticalAlignment.Center, Height = lyricLines * c.LyricLine, CanContentScroll = false, Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible };
                // The viewport clip would turn the lyrics grayscale; they lie over the island's opaque surface.
                RenderOptions.SetClearTypeHint(text, ClearTypeHint.Enabled);
                AutomationProperties.SetName(lyrics, Loc.T("歌词", "Lyrics")); middle.Children.Add(lyrics);
            }
            if (lyricsArrive) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; middle.BeginAnimation(OpacityProperty, fade); }
            if (layout.MiddleHeight >= c.MiddleGap + c.LyricLine)
            {
                // One line; a longer message (a failed full control connection) is read whole in its tooltip, like a cut title.
                _musicMessage = UI.Fixed(message, c.Lyric, c.LyricLine, UI.Tertiary); ShowCutText(_musicMessage, message);
                _musicMessage.Visibility = layout.ShowMessage ? Visibility.Visible : Visibility.Collapsed; middle.Children.Add(_musicMessage);
            }
        }
        if (timeline)
        {
            var times = new Grid { Height = c.TimeLine, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.TimesY, 0, 0), IsHitTestVisible = false };
            _musicElapsed = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); times.Children.Add(_musicElapsed);
            _musicDuration = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); _musicDuration.HorizontalAlignment = HorizontalAlignment.Right; times.Children.Add(_musicDuration);
            foreach (var time in new[] { _musicElapsed, _musicDuration }) Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            body.Children.Add(times);
            _musicSeek = new MusicSeekSlider { Style = (Style)FindResource("MusicSeek"), RestHeight = c.Bar, HoverHeight = c.BarHover, BarHeight = c.Bar,
                Accent = ((SolidColorBrush)_musicAccent).Color, DurationSeconds = track.Duration.TotalSeconds, Height = c.SeekRow, AnimationsEnabled = animate, IsEnabled = track.CanSeek && track.HasTimeline,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.ProgressY, 0, 0) };
            AutomationProperties.SetName(_musicSeek, Loc.T("播放进度", "Playback position"));
            // Slider handles move-to-point before instance handlers; observe that handled event too.
            _musicSeek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, _) =>
            {
                var seek = (Slider)sender; BeginSeek(seek);
                var sliderTrack = seek.Template.FindName("PART_Track", seek) as System.Windows.Controls.Primitives.Track;
                // Track clicks do not capture in WPF. Keep the release even if it is outside the bar.
                if (_seeking && Mouse.LeftButton == MouseButtonState.Pressed && sliderTrack?.Thumb.IsMouseOver != true) seek.CaptureMouse();
            }), true);
            _musicSeek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((sender, _) => BeginSeek((Slider)sender)), true);
            _musicSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, e) =>
            {
                if (e.Canceled) { _seeking = false; _seekTrack = null; if (_musicSeek is MusicSeekSlider slider) slider.Seeking = false; UpdateMusicPosition(); }
                else await CommitSeek();
            }), true);
            _musicSeek.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitSeek()), true);
            _musicSeek.LostMouseCapture += async (_, _) => { if (_seeking && _musicSeek?.IsMouseCaptureWithin == false) await CommitSeek(); };
            _musicSeek.ValueChanged += (_, _) => { if (!_updatingSeek && _seeking) UpdateSeekLabels(_musicSeek.Value); };
            _musicSeek.PreviewKeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp) { _seekTrack = _mediaTrack; _seeking = true; await CommitSeek(); } };
            body.Children.Add(_musicSeek);
        }
        if (_musicControls is null)
        {
            _musicControls = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            MusicContent.Children.Add(_musicControls);
        }
        var controls = _musicControls;
        controls.Width = layout.BodyWidth; controls.Height = c.Play;
        controls.Margin = new Thickness(0, layout.ControlsY, 0, 0);
        var visibleControls = new HashSet<MediaCommand>();
        void Add(MediaCommand id, string name, string glyph, double size, double x, bool enabled, Func<Task> command, bool mode = false, bool single = false, string? color = null)
        {
            visibleControls.Add(id);
            if (!_musicControlButtons.TryGetValue(id, out var slot))
            {
                var created = new MusicControlButton { Style = (Style)FindResource("MusicControl") };
                created.Click += async (_, _) => { if (created.Tag is Func<Task> execute) await execute(); };
                slot = (created, ""); controls.Children.Add(created);
            }
            var button = slot.Button;
            string iconKey = $"{glyph}|{size}|{mode}|{single}|{color}|{PanelTheme.Generation}";
            if (slot.Icon != iconKey)
            {
                UIElement icon = MusicVisuals.Icon(glyph, size, UI.Brush(color ?? (mode ? UI.Secondary : UI.Primary)));
                if (single)
                {
                    var container = new Grid { Width = size, Height = size }; container.Children.Add(icon);
                    var singleLabel = UI.CenteredSymbol("1", c.Mode <= 14 ? 7 : c.Mode >= 22 ? 10 : 9, UI.Secondary);
                    singleLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    container.Children.Add(singleLabel); icon = container;
                }
                // A changed glyph (play and pause, the playback mode, the heart) grows into place instead of switching.
                if (glide && button.IsVisible && slot.Icon.Length > 0 && icon is FrameworkElement grown)
                {
                    var scale = new ScaleTransform(.6, .6);
                    grown.RenderTransformOrigin = new Point(.5, .5); grown.RenderTransform = scale;
                    var grow = Soft.Fade(.6, 1, Soft.Choose, Soft.Follow); grow.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    var fade = Soft.Fade(0, 1, Soft.Choose, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    scale.ScaleX = scale.ScaleY = 1;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    grown.BeginAnimation(OpacityProperty, fade);
                }
                button.Content = icon;
            }
            _musicControlButtons[id] = (button, iconKey);
            button.Width = size + 16; button.Height = size + 16;
            button.IsEnabled = enabled; button.AnimationsEnabled = animate; button.Tag = command;
            bool moves = glide && button.IsVisible;
            PageHandover.Glide(button, Canvas.LeftProperty, x - 8, moves);
            PageHandover.Glide(button, Canvas.TopProperty, (c.Play - size) / 2 - 8, moves);
            AutomationProperties.SetName(button, name);
        }
        if (track is { SeparateModes: true })
        {
            // Two switches instead of the mode button: shuffle in its place left of previous, repeat in the heart's place
            // right of next. A switch that is on is drawn in the primary color, one that is off in the tertiary.
            string On(bool on) => on ? UI.Primary : UI.Tertiary;
            Add(MediaCommand.Shuffle, Loc.T("随机播放 · ", "Shuffle · ") + (track.Shuffle ? Loc.T("开", "on") : Loc.T("关", "off")), "shuffle",
                c.Mode, layout.ModeX, track.CanShuffle, () => RunMediaCommand(track, MediaCommand.Shuffle), true, color: On(track.Shuffle));
            Add(MediaCommand.Repeat, Loc.T("重复 · ", "Repeat · ") + (track.Repeat switch
                { MediaRepeat.List => Loc.T("全部", "all"), MediaRepeat.Track => Loc.T("单曲", "one"), _ => Loc.T("关", "off") }), "repeat",
                c.Mode, layout.TripletX + layout.TripletWidth + (layout.TripletX - layout.ModeX - c.Mode), track.CanRepeat,
                () => RunMediaCommand(track, MediaCommand.Repeat), true, track.Repeat == MediaRepeat.Track, On(track.Repeat != MediaRepeat.None));
        }
        else if (modes.Count > 0)
        {
            var mode = MusicPlaybackModes.Current(track);
            Add(MediaCommand.Repeat, Loc.T("播放模式 · ", "Playback mode · ") + MusicPlaybackModes.Label(mode), mode == MusicPlaybackMode.Shuffle ? "shuffle" : mode == MusicPlaybackMode.Order ? "sequential" : "repeat",
                c.Mode, layout.ModeX, true, () => CyclePlaybackMode(track), true, mode == MusicPlaybackMode.Track);
        }
        // The full-control entry never moves the transport. It shortens its label step by step to the room it is given.
        double EntryWidth(string label)
        {
            var probe = new TextBlock { Text = label, FontFamily = UI.TextFont, FontSize = c.Time };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(probe.DesiredSize.Width) + MusicLayout.FullControlChrome;
        }
        string? entryLabel = null; double entryWidth = 0;
        if (showSmtcHint)
        {
            // Without SMTC the transport cannot do anything, so its row carries the explanation instead of three dead glyphs.
            // The wording is fixed; the row is fitted by trying the full entry label first, then smaller type, then the short label.
            if (_musicSmtcHint is null)
            {
                _musicSmtcHint = new TextBlock { FontFamily = UI.TextFont, TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                AutomationProperties.SetName(_musicSmtcHint, Loc.T("SMTC 提示", "SMTC hint")); controls.Children.Add(_musicSmtcHint);
            }
            var hint = _musicSmtcHint; hint.Visibility = Visibility.Visible; hint.Inlines.Clear();
            hint.Inlines.Add(new Run(SmtcHintWhy) { Foreground = UI.Brush(UI.Tertiary) });
            hint.Inlines.Add(new LineBreak());
            hint.Inlines.Add(new Run(SmtcHintHow) { Foreground = UI.Brush(UI.Secondary) });
            var sizes = Enumerable.Range(0, 4).Select(step => c.Time - step * .5).Where(size => size >= 9.5);
            bool fitted = false;
            foreach (var label in layout.ShowFullControl ? MusicLayout.FullControlLabels : [""])
            {
                double reserve = label.Length > 0 ? EntryWidth(label) : 0;
                foreach (double size in sizes)
                {
                    double width = layout.BodyWidth - (reserve > 0 ? reserve + MusicLayout.FullControlGap : 0);
                    hint.FontSize = size; hint.LineHeight = Math.Max(12, Math.Round(size * 1.25)); hint.Width = width;
                    hint.Measure(new Size(width, double.PositiveInfinity));
                    if (hint.DesiredSize.Height <= c.Play + 2) { fitted = true; break; }
                }
                if (label.Length > 0) { entryLabel = label; entryWidth = reserve; }
                if (fitted) break;
            }
            Canvas.SetLeft(hint, 0); Canvas.SetTop(hint, Math.Floor((c.Play - hint.DesiredSize.Height) / 2));
        }
        else
        {
            if (_musicSmtcHint is not null) _musicSmtcHint.Visibility = Visibility.Collapsed;
            double gx = layout.TripletX;
            Add(MediaCommand.Previous, Loc.T("上一首", "Previous"), "previous", c.Skip, gx, track.CanPrevious, () => RunMediaCommand(track, MediaCommand.Previous)); gx += c.Skip + layout.ControlGap;
            Add(MediaCommand.PlayPause, track.State == MediaState.Playing ? Loc.T("暂停", "Pause") : Loc.T("播放", "Play"), track.State == MediaState.Playing ? "pause" : "play", c.Play, gx, track.CanToggle, () => RunMediaCommand(track, MediaCommand.PlayPause)); gx += c.Play + layout.ControlGap;
            Add(MediaCommand.Next, Loc.T("下一首", "Next"), "next", c.Skip, gx, track.CanNext, () => RunMediaCommand(track, MediaCommand.Next));
            // The heart mirrors the mode button: right of next, as far from it as the mode button is from previous. Only
            // full control offers it, and the full-control entry, which shares this side, appears only without full control.
            if (track.CanLike)
                Add(MediaCommand.Like, track.Liked ? Loc.T("取消喜欢", "Unlike") : Loc.T("喜欢", "Like"), track.Liked ? "liked" : "like", c.Mode,
                    layout.TripletX + layout.TripletWidth + (layout.TripletX - layout.ModeX - c.Mode), true, () => RunMediaCommand(track, MediaCommand.Like), mode: true, color: track.Liked ? LikedColor : null);
            if (layout.ShowFullControl)
                foreach (var label in MusicLayout.FullControlLabels)
                    if ((entryWidth = EntryWidth(label)) <= layout.FullControlRoom) { entryLabel = label; break; }
        }
        foreach (var (id, slot) in _musicControlButtons)
            slot.Button.Visibility = visibleControls.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        if (entryLabel is not null)
        {
            if (_musicFullControl is null)
            {
                _musicFullControl = new Button { Style = (Style)FindResource("PanelTextButton"), FontFamily = UI.TextFont, Padding = new Thickness(6, 2, 6, 2),
                    Height = 26, HorizontalContentAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(_musicFullControl, MusicLayout.FullControlLabels[0]);
                _musicFullControl.Click += async (_, _) =>
                {
                    if (_mediaTrack?.IsAppleMusic == true) EnableAppleMusicControl();
                    else if (_mediaTrack?.IsKugou == true) await EnableKugouControl();
                    else await EnableNeteaseControl();
                };
                controls.Children.Add(_musicFullControl);
            }
            var enable = _musicFullControl; enable.IsEnabled = _media?.RestartingNetease != true && _media?.RestartingKugou != true;
            // The app-wide TextBlock style wraps; pixel snapping must never push the last character to a second line.
            enable.Width = entryWidth;
            enable.Content = new TextBlock { Text = entryLabel, FontFamily = UI.TextFont, FontSize = c.Time, TextWrapping = TextWrapping.NoWrap };
            Canvas.SetLeft(enable, layout.BodyWidth - entryWidth); Canvas.SetTop(enable, (c.Play - 26) / 2);
        }
        if (_musicFullControl is not null) _musicFullControl.Visibility = entryLabel is not null ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.Children.Insert(0, body); _handover.Release(body, motion); UpdateMusicPosition();
    }
    private double MusicEqualizerWidth(MusicMetrics c) => MusicTitleFit.EqualizerWidth(c.Equalizer, Settings.MusicEqualizerBars);
    // The open page's title: one MusicMarquee, the collapsed row's, so a title that does not fit its room scrolls the same way. Its
    // name for screen readers is the whole title; a tooltip shows it whole while it overhangs.
    private MusicMarquee PageTitle(MediaTrack track, MusicMetrics c, bool center, bool merge)
    {
        string tail = merge && track.Artist.Length > 0 ? " · " + track.Artist : "";
        var title = new MusicMarquee { Text = track.Title, Tail = tail, AccessibleName = MusicTitleFit.ReadAs(track.Title, tail), TailFontSize = c.Artist, TailForeground = UI.Brush(UI.Secondary), FontSize = c.Title,
            FontWeight = FontWeights.SemiBold, Height = c.TitleLine, CenterWhenFits = center, EdgeWidth = Math.Max(MusicMarquee.Edge, Math.Round(c.Title * .6)), Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
        AutomationProperties.SetName(title, MusicTitleFit.ReadAs(track.Title, tail));
        ShowCutText(title, track.Title + tail);
        _musicTitle = title; UpdateLyricScrolling();
        return title;
    }
    // The upper part without the cover (MusicLayout.Columns): the titles, with the equalizer below them, centred in the left
    // column, and the lyrics or the message right of a divider. The current lyric is larger and in its own color, the
    // others dim, so the lyrics do not read as more titles. Without a lyrics column the titles are centred across the width,
    // and a message that comes later (ShowMusicError) appears below them.
    private void MusicColumns(Grid body, MusicLayout layout, MediaTrack track, bool lyricColumn, bool hasLyrics, bool synchronized, bool playerLyrics,
        string message, bool rise, bool lyricsArrive)
    {
        var c = layout.Metrics;
        var columns = layout.Columns(lyricColumn);
        var upper = new Grid { Height = columns.Height, VerticalAlignment = VerticalAlignment.Top };
        body.Children.Add(upper);
        var info = new StackPanel { Width = columns.InfoWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        // Centred while it fits, scrolling when it does not: the column is as wide as the info column, whatever the bars.
        info.Children.Add(PageTitle(track, c, center: true, merge: false));
        var details = columns.ShowAlbum ? new[] { (Text: track.Artist, Color: UI.Secondary), (Text: track.Album, Color: UI.Tertiary) }
            : [(Text: string.Join(" · ", new[] { track.Artist, track.Album }.Where(text => text.Length > 0)), Color: UI.Secondary)];
        foreach (var (text, color) in details.Where(detail => detail.Text.Length > 0))
        {
            var line = UI.Fixed(text, c.Artist, c.ArtistLine, color); line.TextAlignment = TextAlignment.Center; ShowCutText(line, text); info.Children.Add(line);
        }
        if (columns.ShowEqualizer)
        {
            _musicEqualizer = new MusicEqualizer { Bars = Settings.MusicEqualizerBars, Width = MusicEqualizerWidth(c), Height = c.Equalizer, Color = EqualizerColor,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, columns.EqualizerGap, 0, 0) };
            info.Children.Add(_musicEqualizer);
        }
        upper.Children.Add(info);
        if (rise) RiseIn(info);
        var middle = new Grid { VerticalAlignment = VerticalAlignment.Center }; _musicInfo = middle;
        _musicMessage = UI.Fixed(message, c.Lyric, c.LyricLine, UI.Tertiary); ShowCutText(_musicMessage, message);
        _musicMessage.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!lyricColumn)
        {
            _musicMessage.TextAlignment = TextAlignment.Center; middle.Margin = new Thickness(0, c.MiddleGap, 0, 0);
            middle.Children.Add(_musicMessage); info.Children.Add(middle); return;
        }
        double inset = Math.Min(c.MiddleGap, columns.Height / 4);
        upper.Children.Add(new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(columns.InfoWidth + columns.DividerGap, inset, 0, inset),
            Background = UI.Brush(PanelTheme.Hex("rowDivider")), IsHitTestVisible = false });
        middle.HorizontalAlignment = HorizontalAlignment.Left; middle.Width = columns.LyricsWidth; middle.Margin = new Thickness(columns.LyricsX, 0, 0, 0);
        upper.Children.Add(middle);
        var lyricsVisibility = message.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (hasLyrics && synchronized)
        {
            int above = playerLyrics ? 0 : columns.Before, below = playerLyrics ? 0 : columns.After;
            var lyrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Visibility = lyricsVisibility };
            TextBlock Dim() { var line = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(line); return line; }
            _lyricsAbove = [.. Enumerable.Range(0, above).Select(_ => Dim())];
            _lyricCurrent = new MusicMarquee { FontSize = columns.CurrentLyric, Height = columns.CurrentLine, CenterWhenFits = false, Foreground = LyricInk,
                Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
            lyrics.Children.Add(_lyricCurrent); UpdateLyricScrolling();
            _lyricsBelow = [.. Enumerable.Range(0, below).Select(_ => Dim())];
            middle.Children.Add(lyrics);
        }
        else if (hasLyrics)
        {
            var text = UI.Line(_lyrics.Instrumental && _lyrics.Lines.Count == 0 ? "♪" : string.Join("\n", _lyrics.Lines.Select(line => line.Text)), c.Lyric, UI.Secondary);
            text.TextWrapping = TextWrapping.Wrap; text.LineHeight = c.LyricLine; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var lyrics = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center, Height = Math.Max(1, Math.Floor(columns.Height / c.LyricLine)) * c.LyricLine, CanContentScroll = false, Visibility = lyricsVisibility };
            // The viewport clip would turn the lyrics grayscale; they lie over the island's opaque surface.
            RenderOptions.SetClearTypeHint(text, ClearTypeHint.Enabled);
            AutomationProperties.SetName(lyrics, Loc.T("歌词", "Lyrics")); middle.Children.Add(lyrics);
        }
        middle.Children.Add(_musicMessage);
        if (lyricsArrive) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; middle.BeginAnimation(OpacityProperty, fade); }
    }
    // The page suppresses tooltips. A line cut off by its ellipsis (an artist, a long message) still shows its whole
    // text on hover. A later text goes in ToolTip alone: whether it is cut is checked when the tooltip would open.
    // The scrolling title does the same while it overhangs its room.
    private static void ShowCutText(MusicMarquee title, string text)
    {
        title.SetValue(SuppressMusicToolTipsProperty, false);
        title.ToolTip = text.Length > 0 ? text : null;
        title.ToolTipOpening += (_, e) => { if (!title.Overflows) e.Handled = true; };
    }
    private static void ShowCutText(TextBlock block, string text)
    {
        block.SetValue(SuppressMusicToolTipsProperty, false);
        block.ToolTip = text.Length > 0 ? text : null;
        block.ToolTipOpening += (_, e) => { if (!UI.IsCut(block)) e.Handled = true; };
    }
    private async Task CyclePlaybackMode(MediaTrack track)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id || _media is null) return;
        _commandBusy = true;
        try
        {
            var next = MusicPlaybackModes.Next(_mediaTrack);
            if (!await _media.SetPlaybackModeAsync(track.Id, next)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player."));
            await _media.RefreshAsync();
        }
        finally { _commandBusy = false; }
    }
    private void EnableAppleMusicControl()
    {
        if (_media is not { AppleMusicFullControl: false } media) return;
        var dialog = new AppleMusicControlWindow(Settings.Theme) { Owner = this };
        if (dialog.ShowDialog() != true || _media != media) return;
        media.EnableAppleMusicFullControl();
        _musicRenderKey = ""; RenderMusic();
    }
    // The button beside the controls does what the settings' switch does: after the explanation, the switch is on and KuGou
    // restarts with full control. A failure turns the switch off again and stays in the music page's message line.
    private async Task EnableKugouControl()
    {
        if (_media is not { RestartingKugou: false } media || new KugouControlWindow(Settings.Theme) { Owner = this }.ShowDialog() != true
            || _media != media || media.RestartingKugou) return;
        var pending = media.EnableKugouFullControlAsync();
        _musicRenderKey = ""; RenderMusic();
        if (await pending is null) SetKugouFullControl(true);
        if (_media != media) return;
        await media.RefreshAsync(); _musicRenderKey = ""; RenderMusic();
    }
    private void SetKugouFullControl(bool on)
    {
        if (Settings.KugouFullControl == on) return;
        var settings = Settings.Copy(); settings.KugouFullControl = on; _app.UpdateSettings(settings);
    }
    private async Task EnableNeteaseControl()
    {
        if (_media?.CanEnableFullControl != true || _media.RestartingNetease) return;
        var dialog = new NeteaseControlWindow(Settings.Theme) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var media = _media;
        var pending = media.EnableFullControlAsync();
        _musicRenderKey = ""; RenderMusic();
        bool succeeded = await pending;
        if (_media != media) return;
        await media.RefreshAsync(); _musicRenderKey = ""; RenderMusic();
        if (!succeeded) ShowMusicError(media.Error ?? Loc.T("未能启用完整控制，请重试。", "Full control could not be enabled. Try again."));
    }

    private async Task RunMediaCommand(MediaTrack track, MediaCommand command)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id) return;
        _commandBusy = true;
        try { if (_media is not null && !await _media.CommandAsync(track.Id, command)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player.")); }
        finally { _commandBusy = false; }
    }
    private void BeginSeek(Slider seek)
    {
        if (seek != _musicSeek || _mediaTrack is not { CanSeek: true, HasTimeline: true }) return;
        if (!_seeking) { _seeking = true; _seekTrack = _mediaTrack; }
        if (seek is MusicSeekSlider musicSeek) musicSeek.Seeking = true;
        UpdateSeekLabels(seek.Value);
    }
    private async Task CommitSeek()
    {
        if (!_seeking || _musicSeek is null) return;
        var track = _seekTrack; double value = _musicSeek.Value; _seeking = false; _seekTrack = null;
        if (_musicSeek is MusicSeekSlider musicSeek) musicSeek.Seeking = false;
        if (_musicSeek.IsMouseCaptured) _musicSeek.ReleaseMouseCapture();
        if (track is null || track.Id != _mediaTrack?.Id || !SameMediaSong(track, _mediaTrack)) return;
        if (_media is not null)
        {
            var request = _seekPreview.Begin(track, value, DateTimeOffset.UtcNow);
            UpdateMusicPosition();
            if (!await _media.SeekAsync(track, value) && _seekPreview.Cancel(request))
            {
                UpdateMusicPosition();
                ShowMusicError(Loc.T("播放器未接受进度调整。", "The player did not accept the new position."));
            }
        }
        RenderMusic();
    }
    private static bool SameMediaSong(MediaTrack first, MediaTrack second) => MediaRouting.SameSong(first, second) &&
        (string.IsNullOrEmpty(first.SongId) || string.IsNullOrEmpty(second.SongId) || first.SongId == second.SongId);
    private void ShowMusicError(string error)
    {
        if (_musicMessage is null) return;
        _musicMessage.Text = error; _musicMessage.ToolTip = error; _musicMessage.Visibility = Visibility.Visible;
        if (_musicInfo is not null) foreach (UIElement child in _musicInfo.Children) child.Visibility = ReferenceEquals(child, _musicMessage) ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string MusicTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private void UpdateSeekLabels(double fraction)
    {
        if (_mediaTrack is not { HasTimeline: true } track || _musicElapsed is null || _musicDuration is null) return;
        var position = TimeSpan.FromSeconds(track.Duration.TotalSeconds * fraction);
        _musicElapsed.Text = MusicTime(position);
        _musicDuration.Text = MusicTime(track.Duration);
        AutomationProperties.SetName(_musicElapsed, Loc.T("已播放 ", "Elapsed ") + _musicElapsed.Text);
        AutomationProperties.SetName(_musicDuration, Loc.T("总时长 ", "Duration ") + _musicDuration.Text);
    }
    private void UpdateMusicPosition()
    {
        if (_mediaTrack is not { } track || track.State == MediaState.Stopped) { MusicIndicator.Visibility = Visibility.Collapsed; return; }
        var position = _seekPreview.Position(track, DateTimeOffset.UtcNow);
        // The expanded page's controls stay in the tree while collapsed; they are brought up to date when the panel opens.
        bool page = _expanded && IsMusicPage;
        if (page && !_seeking && _musicSeek is not null) { _updatingSeek = true; _musicSeek.Value = track.Duration > TimeSpan.Zero ? position.TotalSeconds / track.Duration.TotalSeconds : 0; _updatingSeek = false; UpdateSeekLabels(_musicSeek.Value); }
        var line = CurrentLyrics(track);
        if (page && _lyricCurrent is not null) _lyricCurrent.Text = line.Current;
        if (page && _lyricPrevious is not null) _lyricPrevious.Text = line.Previous;
        if (page && _lyricNext is not null) _lyricNext.Text = line.Next;
        if (page && _lyricsAbove.Length + _lyricsBelow.Length > 0)
        {
            // The lines above are filled from the bottom, so the line just sung stays next to the current one.
            var window = CurrentLyricWindow(track, _lyricsAbove.Length, _lyricsBelow.Length);
            for (int i = 0; i < _lyricsAbove.Length; i++)
            {
                int from = window.Before.Count - _lyricsAbove.Length + i;
                _lyricsAbove[i].Text = from >= 0 ? window.Before[from] : "";
            }
            for (int i = 0; i < _lyricsBelow.Length; i++) _lyricsBelow[i].Text = i < window.After.Count ? window.After[i] : "";
        }
        if (CompactLyrics && !PausedHides(PausedMusicHide.Text) && _compactLyric != line.Current)
        {
            _compactLyric = line.Current;
            if (_trackNoticeUntil <= DateTimeOffset.UtcNow)
            {
                // A new line scrolls again, also a line that comes back later in the song.
                _compactTitleRested = null;
                if (_compactMarquee is not null) _compactMarquee.Text = CompactLyricText(track, line.Current);
                else RenderCompact();
            }
        }
        MusicIndicator.Visibility = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && track.State != MediaState.Stopped && track.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        // Hidden, the bar keeps its last length: setting it four times a second laid the island out again for nothing.
        if (MusicIndicator.Visibility != Visibility.Visible) return;
        // The progress runs along the indicator's own length, whatever length is set.
        double length = double.IsFinite(Collapsed.Surface.Width) ? Math.Max(0, Collapsed.Surface.Width) : 0;
        double progress = track.HasTimeline && track.Duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / track.Duration.TotalSeconds, 0, 1) : 0;
        if (_frame.Across) MusicIndicator.Height = length * progress; else MusicIndicator.Width = length * progress;
    }
    private (string Previous, string Current, string Next) CurrentLyrics(MediaTrack track)
    {
        if (!NeedsLyrics || track.State == MediaState.Stopped) return ("", "", "");
        if (track.IsQqMusic && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental) return ("", track.CurrentLyric ?? "", "");
        return _lyrics.At(track, DateTimeOffset.UtcNow);
    }
    // The collapsed lyric: the line, or the title without one, and for an instrumental when chosen (DeckSettings.MusicInstrumentalTitle).
    private string CompactLyricText(MediaTrack track, string line)
        => line.Length == 0 || Settings.MusicInstrumentalTitle && _lyrics.Instrumental ? track.Caption : line;
    private LyricWindow CurrentLyricWindow(MediaTrack track, int before, int after)
    {
        if (!NeedsLyrics || track.State == MediaState.Stopped) return LyricWindow.Empty;
        if (track.IsQqMusic && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental) return LyricWindow.Only(track.CurrentLyric ?? "");
        return _lyrics.Around(track, DateTimeOffset.UtcNow, before, after);
    }
    // The color taken from the playing song's cover; null without one, or when the cover gave no color.
    internal Color? CoverColor => _mediaCover is not null && _mediaTrack is { State: not MediaState.Stopped } ? _coverAccent : null;
    // The collapsed title: it scrolls its laps once per text (MarqueeCycle.Laps) and, while it glides, lends the equalizer its
    // frames (MusicGliding).
    private MusicMarquee CompactTitle(string text, double width, CompactMusicLayout layout, bool against = false)
    {
        var title = new MusicMarquee { Text = text, Width = width, Height = layout.TextHeight, FontSize = layout.TextFont, EdgeWidth = layout.TextEdge,
            ScrollAllowed = layout.TextScrolls, Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay,
            CenterWhenFits = !against, RightWhenFits = against, Laps = true, Rested = text.Length > 0 && text == _compactTitleRested };
        title.Finished += () => { if (ReferenceEquals(_compactMarquee, title)) _compactTitleRested = title.Text; };
        title.Gliding += gliding => { if (ReferenceEquals(_compactMarquee, title)) MusicGliding(gliding); };
        return title;
    }
    // The panel closed: the collapsed title scrolls its laps again, as the row comes back into view. (Reaching the island
    // opens the panel, so the collapsed title is not seen while the pointer is on it.)
    private void ReplayCompactTitle() { _compactTitleRested = null; _compactMarquee?.Replay(); }
    // The collapsed row: the middle block — the cover, the title or lyric, and the volume's rhythm read as one line — takes the
    // surface's centre for the rhythm itself, and the sides' difference flows into the text's room, so both end margins read
    // alike and the rhythm never strays from the middle while the row still fits what each side holds. The system readings
    // are split around the block, the first ones on the left, at the reading that keeps the two sides as even as whole
    // readings allow; the clock keeps the right. Each reading is a strip of its own, placed
    // whole, so a side too narrow for one leaves it out rather than cutting it. Without a block (no rhythm, no text) the
    // parts stand centred as a whole.
    private void RenderCompactMusic()
    {
        // The collapsed shape gives the row's width in DIPs and the height scale; every size below follows from them.
        var shape = Collapsed; _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale), dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double contentWidth = CompactContentWidth(shape);
        bool media = Settings.ShowsMusic(EffectiveStyle) && _mediaTrack is { State: not MediaState.Stopped };
        bool showCover = Settings.MusicCompactCover && !PausedHides(PausedMusicHide.Cover), hideText = PausedHides(PausedMusicHide.Text);
        bool equalizer = Settings.ShowsEqualizer(EffectiveStyle);
        bool notice = media && !hideText && _trackNoticeUntil > DateTimeOffset.UtcNow;
        string text = "";
        if (media && !hideText)
        {
            if (notice || Settings.MusicText == CompactMusicText.Title) text = _mediaTrack!.Caption;
            else if (Settings.MusicText == CompactMusicText.Lyrics)
            {
                _compactLyric = CurrentLyrics(_mediaTrack!).Current;
                text = CompactLyricText(_mediaTrack!, _compactLyric);
            }
        }
        var apps = Settings.ShowsSummary(EffectiveStyle) && !notice ? Settings.EnabledApps : [];
        // The clock keeps its own width; shown alone, it is centred.
        var clock = Settings.ShowsClock(EffectiveStyle) ? new ClockFace(Settings.ClockStyle, Settings.Clock24Hour, Settings.ClockSeconds, ClockFace.Palette(Settings, CoverColor), DateTime.Now, scale) : null;
        _stripLayout = _stripAdmission.Layout;
        var layout = CompactMusicLayout.Calculate(contentWidth, scale, media, apps.Count, text.Length > 0, clock?.ReservedWidth ?? 0, 0, equalizer, Settings.EqualizerBars, showCover);
        CurrentCompactLayout = layout;
        double row = layout.RowHeight, unit = CompactMusicLayout.Gap(scale);
        bool hasCover = media && showCover && _mediaCover is not null, hasText = text.Length > 0;
        double coverW = hasCover ? layout.CoverSize : 0, clockW = clock?.ReservedWidth ?? 0;
        // One strip per reading, each placed whole: the block can then stand between any two of them.
        var strips = new List<MonitorStrip>();
        var widths = new List<double>();
        // One gap everywhere in the row: between two readings, between a reading and the middle block, between the text and
        // the rhythm, and before the clock. A strip's own gap belongs inside a strip that holds several readings.
        double stripGap = unit;
        _rowStrips.Clear();
        if (Settings.ShowsMonitor(EffectiveStyle) && Settings.CompactReadings.Count > 0)
        {
            var colors = MonitorThemeColors.Strip(Settings);
            _stripUnavailable = _stripAdmission.Hidden;
            foreach (var reading in Settings.CompactReadings)
            {
                if (!reading.Visible) continue;
                var strip = new MonitorStrip([reading], colors, scale, false, PresenceOf(reading.Item), dpi, _stripUnavailable, _stripAdmission);
                double width = strip.PreferredWidth;
                if (!(width > 0)) continue;
                strips.Add(strip); widths.Add(width);
            }
            _rowStrips.AddRange(strips);
        }
        _compactStrip = strips.FirstOrDefault();
        // The middle block: the title or lyric and the rhythm read as one line, the text right beside the rhythm.
        // The scrolling text's visible window is ten characters wide (滚动显示 10 字符): a whole CJK glyph is one em, so the
        // window is ten ems of the row's type size; every slot floor below takes it, and the surface's own least width
        // follows through `required`.
        double shortest = CompactMusicLayout.Whole(CompactMusicLayout.RowFont * 10 * scale);
        double eqW = equalizer ? layout.EqualizerSize : 0;
        // The room the text may take at most: the length a long title or lyric scrolls in, no more than the row can spare it.
        double slotW = hasText ? Math.Max(shortest, Math.Min(CompactMusicLayout.Whole(CompactMusicLayout.TextSlot * scale), Math.Max(shortest, contentWidth * .4))) : 0;
        double innerGap = hasText && eqW > 0 ? unit : 0;
        int n = widths.Count;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + widths[i];
        double Span(int from, int count) => count <= 0 ? 0 : prefix[from + count] - prefix[from] + (count - 1) * stripGap;
        // What stands on each side of the block, by the order of the settings' rows: the readings start on the side chosen for
        // them, the clock goes to its side, and the cover keeps the left edge. Readings that do not fit the side they start on
        // carry over to the other, so both sides still carry as much alike as whole readings allow.
        var order = Settings.CompactOrder(EffectiveStyle);
        int At(CompactPart part) { for (int i = 0; i < order.Count; i++) if (order[i] == part) return i; return int.MaxValue; }
        // The block takes the rows from the music's to the rhythm's, so a reading dragged anywhere before the rhythm starts on
        // the left, and the clock goes left only above the music.
        int blockStart = Math.Min(At(CompactPart.Music), At(CompactPart.Equalizer)), blockEnd = Math.Max(At(CompactPart.Music), At(CompactPart.Equalizer));
        bool readingsLeft = At(CompactPart.Monitor) < blockEnd, clockLeft = clock is not null && At(CompactPart.Clock) < blockStart;
        double LeftSpan(int count) => readingsLeft ? Span(0, count) : Span(count, n - count);
        double RightSpan(int count) => readingsLeft ? Span(count, n - count) : Span(0, count);
        // The width one side takes with count readings on the side the order starts them: the readings and the clock, one
        // gap between them. The cover takes no lane any more — it stands inside the middle block, at the rhythm's left.
        double Lane(int count, bool left)
        {
            double total = 0; int blocks = 0;
            double readings = left ? LeftSpan(count) : RightSpan(count);
            if (readings > 0) { total += readings; blocks++; }
            if (clock is not null && clockLeft == left) { total += clockW; blocks++; }
            return blocks == 0 ? 0 : total + (blocks - 1) * unit;
        }
        // Where the readings part: the first go to the side they start on while it stays the narrower one, so both sides carry as
        // much alike as whole readings allow and the surface grows by less than one reading to centre the block.
        int split = 0;
        double bestMax = double.MaxValue, bestAhead = double.MaxValue;
        for (int k = 0; k <= n; k++)
        {
            double left = Lane(k, true), right = Lane(k, false);
            double most = Math.Max(left, right), ahead = Math.Abs(left - right);
            if (most > bestMax + .001 || (most > bestMax - .001 && ahead >= bestAhead)) continue;
            bestMax = most; bestAhead = ahead; split = k;
        }
        double leftNeed = Lane(split, true), rightNeed = Lane(split, false);
        // The sides' difference decides the text's room: the slot takes it, so the rhythm lands on the bar's exact centre
        // while both end margins stay alike. More surplus, more room to scroll in; without enough of it the text keeps its
        // least room and the block leans by what is left. The text stands on the side of the rhythm that balances the row —
        // to its left as a rule, to its right when the left side runs wider. A clock or reading that changes one side's width
        // thus changes the row's length whichever side it stands on.
        double balance = rightNeed - leftNeed;
        bool textRight = hasText && eqW > 0 && balance < 0;
        double slot = !hasText ? 0 : eqW <= 0 ? slotW
            : Math.Max(shortest, Math.Min(slotW, Math.Max(shortest, Math.Abs(balance) - innerGap)));
        double blockW = (hasCover ? coverW + unit : 0) + slot + innerGap + eqW;
        bool hasBlock = blockW > 0;
        // The block on the centre takes twice the wider side: neither side gives way to it, and it stands in the middle.
        double wanted = hasBlock ? 2 * Math.Max(leftNeed, rightNeed) + blockW + 2 * unit : leftNeed + rightNeed;
        double required = blockW + 2 * clockW + 2 * unit;
        // The waiting programs' pill is the widest thing the row shows, so it keeps the width it asked for until it goes; the
        // music's own width is taken again once the notices are read (NotifyPanel.SyncNotice drops the capsule's shape).
        if (_noticeStyle is null && (double.IsNaN(_compactWanted) || Math.Abs(wanted - _compactWanted) > .5 || Math.Abs(required - _compactRequired) > .5))
        { _compactWanted = wanted; _compactRequired = required; Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent); }
        double w = SnapDown(contentWidth, dpi);
        var grid = new Grid { Width = w, Height = row };
        void Place(FrameworkElement element, double x)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left; element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(x, 0, 0, 0);
            grid.Children.Add(element);
        }
        void PlaceStrip(int index, double x) { strips[index].Width = widths[index]; Place(strips[index], x); }
        // The readings on one side from start, up to limit; one that does not fit is left out whole.
        double PlaceReadings(int start, int count, double x, double limit)
        {
            for (int i = start; i < start + count; i++)
            {
                if (i > start) x += stripGap;
                if (x + widths[i] > limit + .5) break;
                PlaceStrip(i, x); x += widths[i];
            }
            return x;
        }
        double clockX = Math.Max(0, w - clockW);
        if (hasBlock)
        {
            // The block's centre sits at the surface's centre shifted by half the sides' difference: both end margins then
            // come out exactly alike (右端与左端一致), every neighbour keeps the same gap, and — while the sides' difference
            // leaves the text room — the rhythm lands on the bar's exact centre (律动条在收起栏正中). Any surplus splits half
            // to each edge.
            double blockLeft = Math.Round(w / 2 + (leftNeed - rightNeed) / 2 - blockW / 2), blockRight = blockLeft + blockW;
            // The cover opens the block, at the rhythm's left (封面在律动条左边); the text keeps the side that balances the row.
            double afterCover = hasCover ? coverW + unit : 0;
            double eqLeft = textRight ? blockLeft + afterCover : blockLeft + afterCover + slot + innerGap;
            double textLeft = textRight ? blockLeft + afterCover + eqW + innerGap : blockLeft + afterCover;
            if (hasCover)
            {
                var cover = MusicVisuals.Cover(_mediaCover, layout.CoverSize, layout.CoverRadius);
                AutomationProperties.SetName(cover, _mediaTrack!.Caption);
                Place(CompactRegion(cover, coverW, coverW, coverW, row, RegionAnchor.Left, dpi), blockLeft);
            }
            if (hasText)
                Place(CompactRegion(_compactMarquee = CompactTitle(text, slot, layout, against: !textRight), slot, slot, layout.TextHeight, row, RegionAnchor.Left, dpi), textLeft);
            else _compactMarquee = null;
            if (eqW > 0)
            {
                _compactEqualizer = new MusicEqualizer { Bars = Settings.EqualizerBars, Width = eqW, Height = layout.EqualizerHeight, BarWidth = layout.EqualizerBar, Color = EqualizerColor };
                Place(CompactRegion(_compactEqualizer, eqW, eqW, layout.EqualizerHeight, row, RegionAnchor.Left, dpi), eqLeft);
            }
            else _compactEqualizer = null;
            // The left side ends at the block: the readings, and the clock when it is chosen there. Every neighbour keeps
            // the same gap, so the clock follows the readings instead of sitting at the window's edge: a side narrower than
            // the other leaves its spare room at the edge, not between the readings and the clock.
            double x = blockLeft - unit - leftNeed;
            double leftStart = x;
            x = PlaceReadings(readingsLeft ? 0 : split, readingsLeft ? split : n - split, x, blockLeft - unit);
            if (clock is not null && clockLeft) clockX = x + (x > leftStart ? unit : 0);
            double rightStart = blockRight + unit;
            x = PlaceReadings(readingsLeft ? split : 0, readingsLeft ? n - split : split, rightStart,
                clock is null || clockLeft ? w : w - clockW - unit);
            if (clock is not null && !clockLeft) clockX = Math.Min(x + (x > rightStart ? unit : 0), w - clockW);
        }
        else
        {
            _compactMarquee = null; _compactEqualizer = null;
            // No block: the parts stand centred as a whole, the cover first and the clock last.
            double x = Math.Max(0, (w - ((hasCover ? coverW + unit : 0) + Span(0, n) + (clock is null ? 0 : (n > 0 ? unit + clockW : clockW)))) / 2);
            if (hasCover)
            {
                var cover = MusicVisuals.Cover(_mediaCover, layout.CoverSize, layout.CoverRadius);
                AutomationProperties.SetName(cover, _mediaTrack!.Caption);
                Place(CompactRegion(cover, coverW, coverW, coverW, row, RegionAnchor.Left, dpi), x); x += coverW + unit;
            }
            x = PlaceReadings(0, n, x, w);
            if (clock is not null) clockX = Math.Min(Math.Max(0, x + (n > 0 ? unit : 0)), w - clockW);
        }
        if (clock is not null) { Place(CompactRegion(clock, clockW, clockW, clock.Height, row, RegionAnchor.Left, dpi), clockX); _compactClock = clock; }
        else _compactClock = null;
        CompactRings.Children.Add(grid); UpdateMusicTimer();
        // The readings were built with the row; they start or stop the sampler and take the newest reading.
        _stripShowing = false; UpdateMonitorDemand();
    }
    // One ring per application: the highest used share among its quotas, with every quota in the tooltip, and the application's
    // letter inside when the setting asks for it, so the rings are told apart by more than their color.
    private (double Used, string Color, string Tip, string Letter)[] QuotaRings(IReadOnlyList<AppEntry> apps) => apps.Select(entry =>
    {
        var snapshot = DashboardUsage.Quota(entry, Snapshots);
        var percentages = snapshot.Metrics.Where(m => m.UsedPercent is not null).Select(m => Math.Clamp(m.UsedPercent!.Value, 0, 100)).ToArray();
        double used = percentages.Length > 0 ? percentages.Max() : snapshot.Metrics.Count > 0 ? 100 : 0;
        string tip = entry.Name + " · " + (percentages.Length > 0 ? Loc.T($"已用 {used:0.#}%", $"{used:0.#}% used")
            : snapshot.Metrics.Count > 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(m => m.ValueText.Length > 0 ? m.ValueText + " " + m.ValueSuffix : Loc.Label(m.Label))) : snapshot.StatusLabel);
        // The display name (AppEntry.Name: the name the user gave the application, else the source's own), so a renamed application's letter follows.
        return (used, PanelTheme.QuotaTheme(entry), tip, CompactMusicLayout.RingLetterFor(Settings.QuotaRingLetters, entry.Name));
    }).ToArray();
    // The collapsed equalizer and the music page's share one color: the panel theme's primary text, whatever the settings
    // once chose (the equalizer's own color pickers were removed; the saved choices are only read so old files load).
    private Brush EqualizerColor => UI.Brush(PanelTheme.Hex("textPrimary"));
    // The current lyric keeps the theme's primary text now that its color choice is gone.
    private Brush LyricInk => UI.Brush(PanelTheme.Hex("textPrimary"));
    // A color chosen among the clock's swatches, drawn as the theme can show it on its surface. The default swatch follows the
    // system; under high contrast everything is the system's own window text, which the theme's text color stands for.
    private Brush MusicColor(ClockColor chosen, string custom, string theme)
    {
        Brush Readable(Color color)
        {
            var brush = new SolidColorBrush(PanelTheme.Readable(color, PanelThemes.GraphicContrast)); brush.Freeze();
            return brush;
        }
        return ClockColors.Shown(chosen, PanelTheme.HighContrast) switch
        {
            ClockColor.White => PanelTheme.HighContrast ? UI.Brush(PanelTheme.Hex(theme)) : Readable(SystemInk.Ink),
            ClockColor.Cover => _musicAccent,
            ClockColor.Custom => Readable(ClockFace.Parse(custom)),
            var preset when ClockColors.Swatches.Contains(preset) => Readable(ClockFace.Preset(preset)),
            _ => UI.Brush(PanelTheme.Hex(theme))
        };
    }
    // The collapsed bars are shown whenever the chosen style asks for them, exactly as the row's layout and the audio
    // capture do: with no song playing they follow the system's sound, as the setting says, rather than disappearing.
    private bool CompactEqualizerShown => !_expanded && _alert is null && !ColumnFor(EffectiveStyle) && Settings.ShowsEqualizer(EffectiveStyle);
    // The row's rhythm, a part of the middle block of its own: the "律动条" switch alone turns it on.
    private bool CompactAudioShown => !_expanded && _alert is null && !ColumnFor(EffectiveStyle) && Settings.ShowsEqualizer(EffectiveStyle);
    private bool TitleGliding => !_expanded && _alert is null && _compactEqualizer is not null && _compactMarquee?.IsGliding == true;
    private void MusicGliding(bool gliding) => UpdateMusicTimer();
    // A display frame while the collapsed title glides (Motion): the bars move once at least a timer's interval has passed,
    // and the timer takes over again once the glide has let go.
    private double _musicFrameAt;
    private bool MusicFrame(double now)
    {
        if (!TitleGliding || !_musicTick.Interval.Equals(TimeSpan.FromMilliseconds(33)))
        {
            UpdateMusicTimer();
            return false;
        }
        if (now - _musicFrameAt >= .033 - .004) { _musicFrameAt = now; TickMusic(); }
        return true;
    }
    private void UpdateMusicTimer()
    {
        bool musicVisible = MusicPresentationVisible, systemVisible = SystemLevelsVisible;
        _app.PresentMedia(this, PlayerLevelsVisible, systemVisible);
        bool progressVisible = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && _mediaTrack is { HasTimeline: true, State: not MediaState.Stopped };
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        // A paused song rests at the start of its line; an endless scroll would keep the window composing for hours. So does a
        // display that is off: the line rests until it is back.
        _compactMarquee?.SetScrolling(animations && MusicAudioActive && IsVisible && !IsSuppressed && !DisplayPower.Off && !_expanded && _alert is null && EffectiveStyle != CompactStyle.Line);
        UpdateLyricScrolling();
        UpdateQuotaCarousel();
        bool needed = IsVisible && !IsSuppressed && !DisplayPower.Off && (MusicAudioActive && (musicVisible || progressVisible) || animations && systemVisible || _trackNoticeUntil > DateTimeOffset.UtcNow);
        // Frame-rate ticks only drive visible equalizer bars; the line indicator's progress moves a pixel every few seconds.
        bool bars = animations && (MusicAudioActive && PlayerLevelsVisible || systemVisible);
        _musicTick.Interval = TimeSpan.FromMilliseconds(bars ? 33 : 250);
        // While the collapsed title glides, WPF draws a frame every refresh for it; the bars move on those frames (MusicFrame),
        // so the timer's own frames, which fell between them, are not drawn as well.
        bool frames = needed && bars && TitleGliding;
        if (frames) { _musicTick.Stop(); Motion.Start(_compactEqualizer!, MusicFrame); }
        else if (needed) { if (!_musicTick.IsEnabled) { _lastMusicTick = DateTimeOffset.UtcNow; _musicTick.Start(); } }
        else _musicTick.Stop();
        if (_expanded || EffectiveStyle != CompactStyle.Line || !Settings.MusicIndicatorProgress || _mediaTrack is not { HasTimeline: true, State: not MediaState.Stopped }) MusicIndicator.Visibility = Visibility.Collapsed;
    }
    // The open page: the current lyric scrolls only while a song plays, the panel is open on this page and animation is on. The title
    // is the song's name, not its audio, so it scrolls whenever the page is open and the title does not fit; paused, it would otherwise
    // rest cut at its start with no way to read the rest.
    private void UpdateLyricScrolling()
    {
        bool open = Settings.Animations && SystemParameters.ClientAreaAnimation
            && IsVisible && !IsSuppressed && !DisplayPower.Off && _expanded && IsMusicPage;
        _lyricCurrent?.SetScrolling(open && MusicAudioActive); _musicTitle?.SetScrolling(open);
    }
    // The carousel runs its own timer and WPF animation; it only needs to know when it may move.
    private void UpdateQuotaCarousel()
    {
        bool running = IsVisible && !IsSuppressed && !DisplayPower.Off && !_expanded && _alert is null;
        _quotaCarousel?.SetRunning(running, _pointerInside);
        _stripCarousel?.SetRunning(running, _pointerInside);
        _columnCarousel?.SetRunning(running, _pointerInside);
        foreach (var reading in _columnReadings) reading.SetRunning(running, _pointerInside);
    }
    // With the display off (and the session unlocked), nothing of the music is seen: the bars stop and the audio levels are not read.
    private bool MusicPresentationVisible => IsVisible && !IsSuppressed && !DisplayPower.Off && _mediaTrack is { State: not MediaState.Stopped }
        && (_expanded ? IsMusicPage : (Settings.ShowsMusic(EffectiveStyle) || CompactAudioShown) && _alert is null);
    // The bars seen with a song: the music page's, or the row's rhythm.
    private bool PlayerLevelsVisible => MusicPresentationVisible && (_expanded || CompactAudioShown);
    // The row's rhythm reads the whole system's sound while no song plays.
    private bool SystemLevelsVisible => IsVisible && !IsSuppressed && !DisplayPower.Off && !_expanded && _alert is null
        && CompactAudioShown && !MusicAudioActive;
    // The display went off or came back on (DisplayPower): what runs only to be seen stops or starts again.
    // A track notice that ran out while the display was off (the music tick that ends it was stopped) is cleared first, so the
    // display comes back to the row as it is now.
    private void DisplayPowerChanged()
    {
        if (!DisplayPower.Off && _trackNoticeUntil != default && DateTimeOffset.UtcNow >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        UpdateMusicTimer(); UpdateMonitorDemand();
    }
    private bool MusicAudioActive => _mediaTrack is { State: MediaState.Playing };
    private DateTimeOffset _eqDiagAt;
    private void TickMusic()
    {
        var now = DateTimeOffset.UtcNow; double seconds = Math.Min(.5, (now - _lastMusicTick).TotalSeconds); _lastMusicTick = now;
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        bool playerVisible = PlayerLevelsVisible, systemVisible = SystemLevelsVisible;
        bool playing = animations && MusicAudioActive, compactPlaying = playing || animations && systemVisible;
        _app.PresentMedia(this, playerVisible, systemVisible);
        bool levels = playerVisible || systemVisible;
        float peak = levels ? _media?.Peak() ?? 0 : 0;
        // Captured audio drives the bars, as many values as the bars on show have (at whatever length they are set to), flowing
        // along them or one frequency band for each bar, as the equalizer setting chooses; the session peak is the fallback.
        var shown = _expanded ? IsMusicPage ? _musicEqualizer : null : _alert is null ? _compactEqualizer : null;
        int bands = shown?.Bars ?? (CompactAudioShown ? Settings.EqualizerBars : 0);
        var bandLevels = bands <= 0 ? default : _flowLevels.AsSpan(0, Math.Clamp(bands, 1, _flowLevels.Length));
        bool flowing = levels && bands > 0 && _media?.FlowLevels(bandLevels, Settings.EqualizerMode) == true;
        // Optional diagnostics (TWELVE_EQ_DIAG=1): one line a second while the bars are drawn, so a bar that never moves
        // can be traced to its analysis, its capture or the mode it was asked to show. Off in normal use.
        if (Environment.GetEnvironmentVariable("TWELVE_EQ_DIAG") == "1" && now - _eqDiagAt > TimeSpan.FromSeconds(1))
        {
            _eqDiagAt = now;
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island");
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "eq.log"),
                    $"{DateTime.Now:HH:mm:ss} mode={Settings.EqualizerMode} shown={(shown is null ? "null" : shown.Bars.ToString())} bands={bands} levels={levels} flowing={flowing} capture={_media?.Capturing} peak={peak:F3} anim={animations} play={compactPlaying} eq={CompactEqualizerShown} audio={CompactAudioShown}\n");
            }
            catch { }
        }
        if (_expanded && IsMusicPage) { if (flowing) _musicEqualizer?.Show(bandLevels, playing); else _musicEqualizer?.Tick(peak, playing, seconds); }
        if (!_expanded && _alert is null)
        {
            if (flowing) _compactEqualizer?.Show(bandLevels, compactPlaying); else _compactEqualizer?.Tick(peak, compactPlaying, seconds);
        }
        if (_trackNoticeUntil != default && now >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        if ((now - _positionTick).TotalMilliseconds >= 250) { _positionTick = now; UpdateMusicPosition(); }
    }
    private void DisposeMusic() { CloseSourceMenu(); _musicTick.Stop(); _lyricsRequest?.Cancel(); _app.MediaChanged -= MediaChanged; _app.Lyrics.AppleLyricsWritten -= AppleLyricsWritten; DisplayPower.Changed -= DisplayPowerChanged; _app.PresentMedia(this, false, false); }
}
