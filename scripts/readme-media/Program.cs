using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TwelveIsland;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace ReadmeMedia;

// Renders the README images with TwelveIsland's own panel code and invented data. Nothing is read from this computer:
// every snapshot, song and cover below is made up, the usage service points at an empty folder, the network is
// replaced by a handler that always fails, and media detection is never started. The panel window stays transparent
// and off screen; each image is taken from its visual tree with RenderTargetBitmap.
//
// Usage: ReadmeMedia <output folder> [zh-CN|en-US] [stills,themes,monitor,hero,settings]  (settings only when named)
internal static class Program
{
    internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "readme-media");
        var language = args.Length > 1 ? args[1] : Loc.Chinese;
        Directory.CreateDirectory(output);
        int exit = 0;
        var app = new App();
        app.InitializeComponent();
        // App raises Startup as soon as the dispatcher runs, even without Application.Run. TwelveIsland's OnStartup would
        // start a real instance with real data, or ask the running one to open its settings. OnStartup calls the base
        // method first, so an exception from the Startup event ends it before anything else happens.
        app.Startup += (_, _) => throw new StartupSkipped();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { if (e.Exception is StartupSkipped) e.Handled = true; };
        // The one setting OnStartup applies before anything else: the narrow scroll bars of the panel and settings.
        app.Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await new Renderer(app, output, language, args.Length > 2 ? args[2].Split(',') : []).RunAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exit;
    }

    internal static object? Field(object target, string name) => target.GetType().GetField(name, Any)!.GetValue(target);
    internal static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Any)!.SetValue(target, value);
    internal static object? Call(object target, string name, Type[] types, params object?[] args)
        => (target.GetType().GetMethod(name, Any, types) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, args);
    internal static void SetProperty(object target, string name, object? value) => target.GetType().GetProperty(name, Any)!.SetValue(target, value);
}

internal sealed class StartupSkipped : Exception;

internal sealed class Offline : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("The README renderer works offline.");
}

internal sealed class NoDesktop : IDesktopSources
{
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

internal sealed record Frame(BitmapSource Image, double Time);

internal sealed class Renderer(App app, string output, string language, string[] scenes)
{
    private bool Wants(string scene) => scenes.Length == 0 || scenes.Contains(scene);
    private MainWindow _deck = null!;
    private FrameworkElement _stage = null!;
    private DeckSettings _settings = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;
    private BitmapSource _cover = null!;
    private MediaTrack _track = null!, _other = null!;
    private readonly List<Frame> _frames = [];
    // Called with the clip's time before each frame of a recording is taken.
    private Action<double>? _onFrame;
    private readonly Dictionary<string, object> _marks = [];
    private double _clock;
    private bool _english;

    public async Task RunAsync()
    {
        Loc.Use(language); _english = Loc.IsEnglish;
        var folder = Path.Combine(Path.GetTempPath(), "TwelveIsland.ReadmeMedia." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var offline = new HttpClient(new Offline());
        _settings = BuildSettings();
        Program.SetProperty(app, "Settings", _settings);
        Program.SetProperty(app, "Store", new SettingsStore(folder));
        Program.SetProperty(app, "Prices", new Pricing(folder, offline));
        Program.SetProperty(app, "Secrets", new ProviderSecrets(folder));
        Program.SetProperty(app, "Usage", new UsageService(new NoDesktop(), DataLocations.Resolve(folder, folder, folder, folder, folder, folder), offline));
        // The panel subscribes to the lyrics as it is made; this one finds none offline and reads no Apple Music cache.
        Program.SetProperty(app, "Lyrics", new LyricsService(offline, () => [], Path.Combine(folder, "apple-music-lyrics.json")));
        // The toolbar reads the update notice on every render.
        var backend = Type.GetType("TwelveIsland.Updates.GitHubUpdates, 12Island")!.GetMethod("Create", [typeof(string)])!.Invoke(null, [folder]);
        Program.SetProperty(app, "Updates", new AppUpdates((IAppUpdateBackend)backend!, folder));

        _deck = new MainWindow(app) { Opacity = 0, IsHitTestVisible = false };
        Program.SetProperty(app, "Deck", _deck);
        // The window's own Loaded handler starts media detection, pointer tracking and the first read of real data;
        // none of that may run here. Loaded may also never come while the displays are off, so nothing waits for it.
        var store = typeof(UIElement).GetProperty("EventHandlersStore", Program.Any)!.GetValue(_deck);
        var handlers = (RoutedEventHandlerInfo[]?)store?.GetType().GetMethod("GetRoutedEventHandlers", Program.Any)!.Invoke(store, [FrameworkElement.LoadedEvent]);
        foreach (var handler in handlers ?? []) _deck.RemoveHandler(FrameworkElement.LoadedEvent, handler.Handler);
        // Foreground-window changes would re-apply the settings in the middle of a capture.
        Program.SetField(Program.Field(_deck, "_host")!, "ContextChanged", null);
        // Keep the transparent window off screen; ApplySettings centres it on the primary screen each time.
        _deck.LocationChanged += (_, _) => { if (_deck.Left > -10000) _deck.Left = -20000; };
        // The one-minute read would overwrite the invented state. The timer itself still runs for the countdown ring.
        Program.SetField(Program.Field(_deck, "_refresh")!, "Tick", null);
        _deck.ApplySettings();
        await Settle();
        _stage = (FrameworkElement)_deck.Content;

        _cover = Cover();
        (_track, _other) = Tracks();
        _deck.SetSnapshots(Snapshots());
        Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], _track, new List<MediaTrack> { _track, _other }, _cover, false);
        Program.SetField(_deck, "_trackNoticeUntil", default(DateTimeOffset));
        _deck.ApplySettings();
        await Settle();

        if (Wants("stills")) await Stills();
        if (Wants("themes")) await Themes();
        if (Wants("monitor")) await Monitor();
        if (Wants("hero")) await Hero();
        // The "配额与统计" page: one row per application with its sources, name and colours.
        // Not used by the README at present; rendered only when asked for by name.
        if (scenes.Contains("settings")) await SettingsPage("settings", 3);
        // The music page at its presets and at the ends of its size range; for checking layout changes, not for the README.
        if (scenes.Contains("music-sizes")) await MusicSizes();
        Directory.Delete(folder, true);
    }

    private DeckSettings BuildSettings()
    {
        var settings = new DeckSettings
        {
            Language = language, Style = CompactStyle.Notch, UsagePage = true, MusicPage = true, Width = 760, Height = 240,
            MusicWidth = 440, MusicHeight = 200, OpenDelay = 0, CloseDelay = 3_600_000, AnimationDuration = 360, Animations = true,
            Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal,
            MusicTrackNotice = false, QuotaAlerts = true, UsageAutoSync = true, MusicText = CompactMusicText.None,
            Apps =
            [
                new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Claude },
                new AppEntry { QuotaSource = ProviderId.Codex, UsageSource = ProviderId.Codex },
                new AppEntry { QuotaSource = ProviderId.Cursor, UsageSource = ProviderId.Cursor },
                new AppEntry { QuotaSource = ProviderId.Antigravity, UsageSource = ProviderId.Antigravity },
            ]
        };
        return settings;
    }

    // ---------- Invented data ----------

    // Five models in all, so the model details list fits the panel without a scroll bar.
    private List<ProviderSnapshot> Snapshots()
    {
        DateTimeOffset In(double days, double hours, double minutes) => _now.AddDays(days).AddHours(hours).AddMinutes(minutes).AddSeconds(30);
        ProviderSnapshot Snapshot(ProviderId id, string plan, params Quota[] quotas) => new(id)
        {
            Plan = plan, Quotas = quotas.ToList(), LiveQuota = true, QuotaTime = _now, UsageAvailable = true,
            StatusLabel = Loc.T("已连接", "Connected"), Status = Loc.T("已连接", "Connected")
        };
        var claude = Snapshot(ProviderId.Claude, "max",
            new Quota("5 小时额度", 34, In(0, 2, 13), 300), new Quota("每周额度", 58, In(3, 5, 0), 10080));
        claude.Entries = Entries(1, ["claude-opus-5-5", "claude-sonnet-5"], [.65, .35], 26_400_000, 17.84m);
        var codex = Snapshot(ProviderId.Codex, "plus",
            new Quota("5 小时额度", 22, In(0, 3, 41), 300), new Quota("每周额度", 41, In(4, 2, 0), 10080));
        codex.Entries = Entries(2, ["gpt-5.5-codex"], [1], 11_200_000, 6.37m);
        var cursor = Snapshot(ProviderId.Cursor, "pro",
            new Quota("Cursor 模型", 76, In(12, 6, 0)), new Quota("其他模型", 18, In(12, 6, 0)));
        cursor.Entries = Entries(3, ["auto"], [1], 3_900_000, 2.41m);
        var antigravity = Snapshot(ProviderId.Antigravity, "Google AI Pro",
            new Quota("Gemini · 5 小时", 12, In(0, 4, 5), 300), new Quota("Gemini · 每周", 29, In(5, 1, 0), 10080),
            new Quota("Claude · 5 小时", 47, In(0, 1, 26), 300), new Quota("Claude · 每周", 63, In(5, 1, 0), 10080));
        antigravity.Entries = Entries(4, ["gemini-3-pro"], [1], 5_600_000, 3.12m);
        return [claude, codex, cursor, antigravity];
    }

    // Today's records add up to the given totals; the six days before hold between 3.5 and 6.5 times as much in all.
    private List<TokenEntry> Entries(int seed, string[] models, double[] shares, long todayTokens, decimal todayCost)
    {
        var random = new Random(seed);
        var result = new List<TokenEntry>();
        var today = DateTime.Today;
        double todayHours = Math.Max(.5, (DateTime.Now - today).TotalHours - .1);
        for (int day = 0; day < 7; day++)
        {
            double factor = day == 0 ? 1 : .6 + random.NextDouble() * .5;
            long dayTokens = (long)(todayTokens * factor);
            decimal dayCost = Math.Round(todayCost * (decimal)factor, 2);
            for (int m = 0; m < models.Length; m++)
            {
                const int pieces = 6;
                for (int i = 0; i < pieces; i++)
                {
                    long total = (long)(dayTokens * shares[m] / pieces);
                    long output = total / 40, input = total / 12, cacheWrite = total / 20, cacheRead = total - output - input - cacheWrite;
                    var time = day == 0 ? today.AddHours(todayHours * (i + .5) / pieces) : today.AddDays(-day).AddHours(9 + i * 2 + random.NextDouble());
                    result.Add(new TokenEntry($"{seed}:{day}:{m}:{i}", new DateTimeOffset(time), models[m], input, cacheRead, cacheWrite, 0, output,
                        Math.Round(dayCost * (decimal)shares[m] / pieces, 4)));
                }
            }
        }
        return result;
    }

    private (MediaTrack Playing, MediaTrack Paused) Tracks()
    {
        var utc = DateTimeOffset.UtcNow;
        string lyrics = _english ? EnglishLyrics : ChineseLyrics;
        var playing = new MediaTrack
        {
            Id = "demo:netease", Source = "网易云音乐", Title = Loc.T("晚风邮局", "Evening Post"), Artist = Loc.T("纸灯乐队", "Paper Lanterns"),
            Album = Loc.T("海边来信", "Letters from the Shore"), EmbeddedLyrics = lyrics, State = MediaState.Playing,
            CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true, CanShuffle = true, CanRepeat = true, Repeat = MediaRepeat.List, CanLike = true, Liked = true,
            Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(232), MaxSeek = TimeSpan.FromSeconds(232),
            ReportedPosition = TimeSpan.FromSeconds(83), PositionAt = utc, Rate = 1
        };
        var paused = new MediaTrack
        {
            Id = "demo:qq", Source = "QQ 音乐", Title = Loc.T("慢车", "Slow Train"), Artist = Loc.T("白日梦", "Daydreamers"),
            State = MediaState.Paused, CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true,
            Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(201), ReportedPosition = TimeSpan.FromSeconds(40), PositionAt = utc
        };
        return (playing, paused);
    }

    private const string ChineseLyrics = """
        [00:00.00]晚风邮局 - 纸灯乐队
        [00:18.00]黄昏把街道慢慢折起
        [00:26.00]路灯一盏一盏替我数着
        [00:34.00]信封里装着整个夏天
        [00:42.00]还有没说完的那句话
        [00:52.00]把今天的晚风寄给你
        [01:00.00]寄到你窗前的那棵树
        [01:08.00]如果它轻轻敲你的窗
        [01:16.00]那是我在说晚安
        [01:24.00]海浪替我盖上邮戳
        [01:32.00]星星在信纸上排成一行
        [01:40.00]不必急着拆开回信
        [01:48.00]等潮水退了再读
        [01:58.00]把今天的晚风寄给你
        [02:06.00]寄到很远很远的地方
        [02:14.00]如果你刚好也在想念
        [02:22.00]就把灯留给我
        """;

    private const string EnglishLyrics = """
        [00:00.00]Evening Post - Paper Lanterns
        [00:18.00]Dusk folds the street up slowly
        [00:26.00]The lamps count the hours for me
        [00:34.00]A whole summer in one envelope
        [00:42.00]And the words I never said
        [00:52.00]I'm mailing you tonight's breeze
        [01:00.00]To the tree outside your window
        [01:08.00]If it taps upon the glass
        [01:16.00]That's me saying goodnight
        [01:24.00]The waves press on the postmark
        [01:32.00]Stars line up across the page
        [01:40.00]No need to write back yet
        [01:48.00]Read it when the tide goes out
        [01:58.00]I'm mailing you tonight's breeze
        [02:06.00]To somewhere far away
        [02:14.00]If you're missing me as well
        [02:22.00]Leave a light on for me
        """;

    // A made-up album cover: an evening sky over the sea.
    private static BitmapSource Cover()
    {
        const int size = 512;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x2A, 0x2D, 0x52), 0), new(Color.FromRgb(0x7C, 0x4E, 0x78), .45), new(Color.FromRgb(0xE8, 0x8F, 0x6E), .72), new(Color.FromRgb(0xF4, 0xC0, 0x8A), .78)
            }, new Point(0, 0), new Point(0, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
            var glow = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xD9, 0xA8), Color.FromArgb(0, 0xFF, 0xD9, 0xA8)) { RadiusX = .5, RadiusY = .5 };
            dc.DrawEllipse(glow, null, new Point(size * .5, size * .66), 190, 150);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xB6)), null, new Point(size * .5, size * .66), 70, 70);
            var sea = new LinearGradientBrush(Color.FromRgb(0x3A, 0x3F, 0x6E), Color.FromRgb(0x18, 0x1B, 0x33), 90);
            dc.DrawRectangle(sea, null, new Rect(0, size * .7, size, size * .3));
            var shine = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xD2, 0xA0));
            double[] widths = [110, 84, 60, 40, 26, 14];
            for (int i = 0; i < widths.Length; i++)
            {
                double y = size * .72 + i * 14;
                dc.DrawRoundedRectangle(shine, null, new Rect(size * .5 - widths[i] / 2, y, widths[i], 4), 2, 2);
            }
            var hill = new StreamGeometry();
            using (var g = hill.Open())
            {
                g.BeginFigure(new Point(0, size * .7), true, true);
                g.BezierTo(new Point(size * .08, size * .6), new Point(size * .2, size * .58), new Point(size * .3, size * .7), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x24, 0x22, 0x40)), null, hill);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    // ---------- Scenes ----------

    private async Task Stills()
    {
        const double scale = 2;
        _deck.SetExpanded(true, true);
        await Settle(); SetSyncRing(.4);
        await Save("usage", scale);

        Program.Call(_deck, "OpenModelDetails", [typeof(ProviderId), typeof(int)], ProviderId.Claude, 7);
        foreach (var id in new[] { ProviderId.Codex, ProviderId.Cursor, ProviderId.Antigravity })
            Program.Call(_deck, "ToggleModelApp", [typeof(ProviderId)], id);
        await Settle();
        await Save("details", scale);
        Program.Call(_deck, "ShowModelDetails", [typeof(bool)], false);

        SelectPage(DeckPage.Music);
        await Settle();
        await Save("music", scale, 1.3);
        SelectPage(DeckPage.Usage);

        _deck.SetExpanded(false, true);
        await Settle();
        await Save("compact-notch", scale, .7);

        _settings.MusicText = CompactMusicText.Lyrics; Refresh();
        await Settle();
        await Save("compact-notch-lyrics", scale, .9);
        _settings.MusicText = CompactMusicText.None;

        _settings.Style = CompactStyle.Capsule; Refresh();
        await Settle();
        await Save("compact-capsule", scale, 1.1);

        _settings.Style = CompactStyle.Line; Refresh();
        await Settle();
        await Save("compact-line", scale);

        _settings.Style = CompactStyle.Notch; Refresh();
        await QuotaAlert(scale);
        await ClockStyles();
    }

    // The quota notice replaces the collapsed row and is cut off when the row is too short (the reset time goes first), so it
    // is shown on a notch just wide enough for the whole notice plus a 12 DIP margin, with the same invented data.
    private async Task QuotaAlert(double scale)
    {
        var claude = _settings.Apps[0];
        var alert = (System.Windows.Controls.Panel)_deck.FindName("CompactAlert")!;
        async Task Show(double width)
        {
            _settings.NotchWidth = width; Refresh();
            Program.Call(_deck, "ShowAlert", [typeof(AppEntry), typeof(string), typeof(double), typeof(DateTimeOffset?)],
                claude, Loc.T("5 小时", "5-hour"), 91.0, (DateTimeOffset?)_now.AddHours(2).AddMinutes(13).AddSeconds(30));
            await Settle();
        }
        double fit = 0;
        for (double width = CollapsedLimits.NotchWidthDefault; width <= CollapsedLimits.NotchWidthMax && fit == 0; width += 2)
        {
            await Show(width);
            var regions = alert.Children.OfType<FrameworkElement>().ToList();
            if (regions.Count == 4 && regions.All(region => !region.ClipToBounds)) fit = width;
        }
        if (fit == 0) throw new InvalidOperationException("No notch width shows the whole quota notice.");
        await Show(fit + 12);
        await Save("compact-alert", scale);
        Program.Call(_deck, "ClearAlert", []);
        _settings.NotchWidth = CollapsedLimits.NotchWidthDefault; Refresh();
        await Settle();
    }

    // Compact → usage page → music page → system monitor page → compact, with the configured animations. The panel's own animations run
    // slower while they are captured; frame times are recorded on the normal time scale.
    private async Task Hero()
    {
        const double scale = 1.5;
        const int slow = 6;
        _frames.Clear(); _clock = 0;
        _settings.AnimationDuration = 360 * slow;
        _deck.SetExpanded(false, true);
        await Settle(); SetSyncRing(.3);

        await Record(1.6, 1, scale);
        _marks["expand"] = _clock;
        _deck.SetExpanded(true);
        await Record(.5, slow, scale);
        await Record(2.4, 1, scale, fps: 4);

        _marks["musicButton"] = Point(_deck.FindName("MusicPageButton"));
        _marks["switch"] = _clock;
        SelectPage(DeckPage.Music);
        await Record(.5, slow, scale);
        _marks["musicPanel"] = Bounds();
        await Record(2.8, 1, scale);

        // As in Monitor(): App.Exiting keeps the real sampler from starting. The page gets the invented reading as soon as it
        // is built and draws it without easing, as a page that opens on the latest reading does.
        Program.SetProperty(app, "Exiting", true);
        _settings.MonitorWidth = MonitorSizes.Default.Width; _settings.MonitorHeight = MonitorSizes.Default.Height;
        _marks["monitorButton"] = Point(_deck.FindName("MonitorPageButton"));
        _marks["monitorSwitch"] = _clock;
        SelectPage(DeckPage.Monitor);
        Program.Call(Program.Field(_deck, "_monitorView")!, "Update", [typeof(MonitorSnapshot), typeof(bool)], MonitorSample(), false);
        // A new reading every second of the clip, handed over as the sampler would, so the page eases to it and the list reorders.
        double opened = _clock; int fed = 0;
        _onFrame = time =>
        {
            int tick = (int)Math.Floor(time - opened);
            if (tick > fed) { fed = tick; Program.Call(_deck, "MonitorUpdated", [typeof(MonitorSnapshot)], MonitorSample(tick)); }
        };
        await Record(.5, slow, scale);
        _marks["monitorPanel"] = Bounds();
        await Record(2.8, 1, scale);
        _onFrame = null;

        _marks["collapse"] = _clock;
        _deck.SetExpanded(false);
        await Record(.6, slow, scale);
        await Record(1.2, 1, scale);
        _marks["end"] = _clock;
        SelectPage(DeckPage.Usage);
        Program.SetProperty(app, "Exiting", false);

        var folder = Path.Combine(output, "hero");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var list = new List<object>();
        for (int i = 0; i < _frames.Count; i++)
        {
            var name = $"{i:0000}.png";
            Write(_frames[i].Image, Path.Combine(folder, name));
            list.Add(new { file = name, time = Math.Round(_frames[i].Time, 4) });
        }
        _marks["scale"] = scale;
        _marks["stage"] = new { width = _stage.ActualWidth, height = _stage.ActualHeight };
        _marks["usagePanel"] = new { width = _settings.Width, height = _settings.Height };
        File.WriteAllText(Path.Combine(folder, "frames.json"), JsonSerializer.Serialize(new { frames = list, marks = _marks }, new JsonSerializerOptions { WriteIndented = true }));
        _settings.AnimationDuration = 360;
    }

    // Collapsed notches showing the clock at a fixed invented date and time (2026-09-27 15:25): three styles in different
    // preset colors beside the quota rings, and one shown alone, centred. compose.py arranges them in a labelled grid as
    // compact-clock.png.
    private async Task ClockStyles()
    {
        const double scale = 2;
        var face = Type.GetType("TwelveIsland.ClockFace, 12Island")!;
        var time = new DateTime(2026, 9, 27, 15, 25, 0);
        (ClockStyle Style, ClockColor Color, bool Alone)[] samples =
        [
            (ClockStyle.Minimal, ClockColor.White, false), (ClockStyle.Segment, ClockColor.Lime, false),
            (ClockStyle.Serif, ClockColor.Amber, false), (ClockStyle.Neon, ClockColor.Pink, true)
        ];
        _deck.SetExpanded(false, true);
        _settings.Style = CompactStyle.Notch; _settings.NotchClock = true; _settings.NotchMusic = false;
        var list = new List<object>();
        for (int i = 0; i < samples.Length; i++)
        {
            var (style, color, alone) = samples[i];
            // Alone means no quota rings and no music, so the clock is centred.
            _settings.ClockStyle = style; _settings.ClockColor = color; _settings.NotchSummary = !alone; Refresh();
            await Settle();
            if (Program.Field(_deck, "_compactClock") is { } clock) Program.SetProperty(clock, "Time", time);
            await Settle();
            var name = $"clock-{i}";
            Write(Capture(scale), Path.Combine(output, name + ".png"));
            string title = alone ? Loc.T("单独显示", "Shown alone") : (string)face.GetMethod("Title")!.Invoke(null, [style])!;
            list.Add(new { file = name + ".png", style = style.ToString(), alone, title });
        }
        File.WriteAllText(Path.Combine(output, "clocks.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("compact-clock");
        _settings.NotchClock = false; _settings.NotchMusic = true; _settings.NotchSummary = true;
        _settings.ClockStyle = default; _settings.ClockColor = ClockColor.White; Refresh();
        await Settle();
    }

    // The usage page in built-in panel themes that look clearly different, and in an invented custom theme. compose.py
    // arranges them in a labelled grid as themes.png. The theme folder is never read: PanelTheme is marked as loaded and its
    // custom themes are the one below, written to a folder of this run.
    private const string CustomTheme = """
        {
          "id": "warm-paper",
          "name": "暖纸",
          "nameEn": "Warm paper",
          "base": "light",
          "colors": { "surface": "#F3EBDC", "accent": "#B4532A", "clock": "#8A4B2A" }
        }
        """;

    private async Task Themes()
    {
        const double scale = 2;
        var folder = Path.Combine(Path.GetTempPath(), "TwelveIsland.ReadmeThemes." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "warm-paper.json"), CustomTheme);
        string[] ids = [PanelThemes.Dark, PanelThemes.Light, "night-owl", "dracula", PanelThemes.Demo, "warm-paper"];
        var theme = Type.GetType("TwelveIsland.PanelTheme, 12Island")!;
        theme.GetField("_loaded", Program.Any)!.SetValue(null, true);
        theme.GetProperty("UserThemes", Program.Any)!.SetValue(null, PanelThemes.LoadFolder(folder));
        var use = theme.GetMethod("Use", Program.Any)!;
        _settings.Width = 620; _settings.Height = 200;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(true, true);
        var list = new List<object>();
        for (int i = 0; i < ids.Length; i++)
        {
            _settings.PanelTheme = ids[i];
            use.Invoke(null, [_settings, false, false]); Refresh();
            await Settle(); SetSyncRing(.4);
            var name = $"theme-{i}";
            await Save(name, scale);
            string title = PanelThemes.BuiltIn(ids[i])?.DisplayName ?? Loc.T("自定义主题示例：暖纸", "Custom theme example: Warm paper");
            list.Add(new { file = name + ".png", title });
        }
        File.WriteAllText(Path.Combine(output, "themes.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Delete(folder, true);
        _settings.PanelTheme = PanelThemes.Dark;
        use.Invoke(null, [_settings, false, false]);
        _settings.Width = 760; _settings.Height = 240;
        _deck.SetExpanded(false, true); Refresh();
        await Settle();
    }

    // The system monitor page and the readings in the notch, from one invented reading. App.Exiting keeps PresentMonitor from
    // starting the real sampler; the reading is handed to the panel as the sampler would.
    private async Task Monitor()
    {
        const double scale = 2;
        Program.SetProperty(app, "Exiting", true);
        // The page eases new readings in; without animations the first reading is drawn as it is.
        _settings.Animations = false;
        var snapshot = MonitorSample();
        void Feed() => Program.Call(_deck, "MonitorUpdated", [typeof(MonitorSnapshot)], snapshot);

        // The usage page's size, so the two images show at the same scale in the README.
        _settings.MonitorWidth = 760; _settings.MonitorHeight = 240;
        SelectPage(DeckPage.Monitor);
        _deck.SetExpanded(true, true);
        await Settle(); Feed();
        await Save("monitor", scale);
        SelectPage(DeckPage.Usage);

        _settings.NotchMonitor = true; _settings.NotchMusic = false;
        _deck.SetExpanded(false, true); Refresh();
        await Settle(); Feed();
        await Save("compact-monitor", scale);
        _settings.NotchMonitor = false; _settings.NotchMusic = true; Refresh();
        Program.SetProperty(app, "Exiting", false);
        _settings.Animations = true;
        await Settle();
    }

    // A 14-core processor (6 performance cores with two threads each, 8 efficiency cores), 32 GB of memory and a graphics card,
    // read once a second: tick counts the seconds, and tick 0 is the reading of the still image. Every figure moves around its
    // value at tick 0 from one second to the next, and the minute of history is the same figures for the 59 seconds before, so
    // the lines scroll on as the readings come in.
    private static MonitorSnapshot MonitorSample(int tick = 0)
    {
        // A slow wander between -2 and 2 and a jump between -1 and 1 that changes every second; both are 0 at tick 0.
        static double Drift(double t, int k)
        {
            static double Wave(double t, int k) => Math.Sin(t * (.4 + k % 7 * .09) + k * 1.7) * .6 + Math.Sin(t * (1.1 + k % 5 * .13) + k * .9) * .4;
            return Wave(t, k) - Wave(0, k);
        }
        static double Jump(int t, int k) => t == 0 ? 0 : new Random(t * 7919 + k * 104729).NextDouble() * 2 - 1;
        double[] performance = [.62, .48, .71, .35, .54, .27, .44, .19, .58, .31, .23, .12];
        double[] efficiency = [.22, .09, .15, .31, .06, .12, .18, .04];
        int count = performance.Length + efficiency.Length;
        double Core(int i, int t) => Math.Clamp((i < performance.Length ? performance[i] : efficiency[i - performance.Length]) + .05 * Drift(t, i) + .1 * Jump(t, i) + .06 * Drift(t, 98) + .1 * Jump(t, 99), .02, 1);
        double Cpu(int t) => Enumerable.Range(0, count).Average(i => Core(i, t));
        double Gpu(int t) => Math.Clamp(.36 + .05 * Drift(t, 40) + .08 * Jump(t, 40), .02, 1);
        double Down(int t) => Math.Max(60_000, 3_400_000 * (1 + .2 * Drift(t, 50) + .35 * Jump(t, 50)));
        double Up(int t) => Math.Max(20_000, 420_000 * (1 + .2 * Drift(t, 51) + .35 * Jump(t, 51)));

        var cores = new List<CoreSample>();
        for (int i = 0; i < count; i++)
        {
            bool fast = i < performance.Length;
            double usage = Core(i, tick);
            cores.Add(new CoreSample(i, fast ? 0 : 1, fast ? CoreTier.Performance : CoreTier.Efficiency, fast ? i + 1 : i - performance.Length + 1, usage,
                fast ? 3600 + usage * 1700 : 2800 + usage * 1500, fast ? 5400 : 4200));
        }
        var cpu = new CpuSample(cores, 4300);
        const ulong GB = 1UL << 30, MB = 1UL << 20;
        var memory = new MemorySample(32 * GB, (ulong)((18.4 + .1 * Jump(tick, 60)) * GB), (ulong)(24.6 * GB), (ulong)(37.8 * GB), (ulong)(8.9 * GB));
        var network = new NetworkSample(Down(tick), Up(tick));
        double gpuUsage = Gpu(tick);
        var gpu = new GpuSample(gpuUsage, (ulong)(5.2 * GB))
        {
            Name = "NVIDIA GeForce RTX 4070", DedicatedTotal = 12 * GB, SharedBytes = (ulong)(0.4 * GB),
            Engines = [new("3D", gpuUsage), new("Copy", Math.Max(0, .07 + .04 * Jump(tick, 41))), new("VideoDecode", Math.Max(0, .21 + .08 * Jump(tick, 42))),
                new("VideoEncode", 0), new("Compute", Math.Max(0, .03 + .02 * Jump(tick, 43)))]
        };
        // The icons are the only thing taken from this computer: each app's icon is read from its executable where it is
        // installed, as TwelveIsland does. An app that is not installed shows its initial instead.
        var prepare = Type.GetType("TwelveIsland.ProcessIcons, 12Island")!.GetMethod("Prepare", Program.Any)!;
        // Each app's processor use over five seconds, repeated. The figures change every second, and once in the clip (at tick 2)
        // Chrome overtakes Visual Studio Code, so the list reorders once. Tick 0 is the still image.
        ProcessSample App(int pid, string name, string exe, double[] cpuPercents, double memoryMb, int members = 1, params string[] places)
        {
            var path = Installed(exe, places);
            if (path is not null) prepare.Invoke(null, [path]);
            double cpuPercent = cpuPercents[tick % cpuPercents.Length];
            return new(pid, name, cpuPercent, (ulong)(memoryMb * MB), name.ToLowerInvariant(), path) { Members = Folded(name, cpuPercent, memoryMb, members) };
        }
        IReadOnlyList<ProcessMember> Folded(string name, double cpuPercent, double memoryMb, int members) =>
            members > 1 ? [.. Enumerable.Range(0, members).Select(i => new ProcessMember(name, cpuPercent / members, (ulong)(memoryMb / members * MB)))] : [];
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows), system = Environment.SystemDirectory;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<ProcessSample> apps =
        [
            App(4120, "Visual Studio Code", "Code.exe", [182, 170, 120, 115, 118], 1460, 9, Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe")),
            App(6388, "Google Chrome", "chrome.exe", [126, 135, 168, 172, 165], 2380, 14),
            App(9012, "Cursor", "Cursor.exe", [74, 78, 80, 76, 79], 890, 6, Path.Combine(local, @"Programs\cursor\Cursor.exe")),
            App(1876, Loc.T("桌面窗口管理器", "Desktop Window Manager"), "dwm.exe", [41, 44, 39, 42, 40], 210, 1, Path.Combine(system, "dwm.exe")),
            App(7740, Loc.T("Windows 资源管理器", "Windows Explorer"), "explorer.exe", [18, 16, 19, 17, 18], 330, 1, Path.Combine(windows, "explorer.exe")),
            App(5320, Loc.T("网易云音乐", "NetEase Cloud Music"), "cloudmusic.exe", [12, 13, 12, 14, 13], 410, 4),
            App(3304, "Windows PowerShell", "powershell.exe", [9, 8, 9, 8, 9], 160, 2, Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe")),
            App(2210, "Microsoft Edge", "msedge.exe", [6, 7, 6, 7, 6], 720, 5)
        ];
        var processes = new ProcessList(ProcessSample.Folded(apps, ProcessSort.Cpu), ProcessSample.Folded(apps, ProcessSort.Memory));
        double[] History(Func<int, double> reading) => [.. Enumerable.Range(tick - 59, 60).Select(reading)];
        return new MonitorSnapshot(DateTimeOffset.Now, count, cpu, memory, network, gpu, processes)
        {
            CpuHistory = History(Cpu), GpuHistory = History(Gpu), DownHistory = History(Down), UpHistory = History(Up)
        };
    }

    // Where an executable is installed: the places given, then the App Paths that installers register.
    private static string? Installed(string exe, string[] places)
    {
        foreach (var place in places) if (File.Exists(place)) return place;
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    private async Task MusicSizes()
    {
        (double Width, double Height)[] sizes = [(440, 140), (440, 170), (440, 200), (520, 200), (540, 250), (600, 240), (760, 300), (900, 350), (1200, 400), (440, 400), (1200, 140)];
        SelectPage(DeckPage.Music);
        _deck.SetExpanded(true, true);
        foreach (var (width, height) in sizes)
        {
            _settings.MusicWidth = width; _settings.MusicHeight = height; Refresh();
            await Save($"music-{width}x{height}", 1.5);
        }
        // NetEase read from its log, with and without SMTC, and a song without lyrics or a timeline.
        var log = _track with { Id = "demo:log", IsNeteaseLog = true, CanShuffle = false, CanRepeat = false, CanSeek = false, CanLike = false, Liked = false };
        var variants = new (string Name, MediaTrack Track)[]
        {
            ("entry", log), ("hint", log with { CanToggle = false, CanPrevious = false, CanNext = false }),
            ("plain", _track with { Id = "demo:plain", EmbeddedLyrics = "", PositionKnown = false })
        };
        foreach (var (name, track) in variants)
        {
            Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], track, new List<MediaTrack> { track }, _cover, false);
            foreach (var (width, height) in new[] { (440d, 140d), (760d, 300d) })
            {
                _settings.MusicWidth = width; _settings.MusicHeight = height; Refresh();
                await Save($"music-{name}-{width}x{height}", 1.5);
            }
        }
        Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], _track, new List<MediaTrack> { _track, _other }, _cover, false);
        _settings.MusicWidth = 440; _settings.MusicHeight = 200; Refresh();
        SelectPage(DeckPage.Usage);
    }

    // The settings window is built without being shown: its content is taken out, laid out at the default size and
    // captured with the window's resources.
    private async Task SettingsPage(string name, int page)
    {
        const double scale = 1.5, width = 960, height = 640;
        _settings.Theme = SettingsTheme.Dark;
        var window = new SettingsWindow(app);
        window.ShowPage(page);
        var root = window.RootVisual;
        var resources = window.Resources;
        window.Content = null;
        root.Resources = resources;
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height));
        await Settle();
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze();
        Write(bitmap, Path.Combine(output, name + ".png"));
        Console.WriteLine(name);
        window.Close();
    }

    private object Point(object? element)
    {
        var target = (FrameworkElement)element!;
        var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), _stage);
        return new { x = point.X, y = point.Y };
    }

    private object Bounds()
    {
        var island = (FrameworkElement)_deck.FindName("Island")!;
        var point = island.TranslatePoint(new Point(0, 0), _stage);
        return new { x = point.X, y = point.Y, width = island.ActualWidth, height = island.ActualHeight };
    }

    // Captures for the given length of normal time; slow is how much slower the panel's animations run meanwhile.
    private async Task Record(double seconds, int slow, double scale, double fps = 30)
    {
        var watch = Stopwatch.StartNew();
        double start = _clock, last = double.NegativeInfinity;
        while (true)
        {
            double time = watch.Elapsed.TotalSeconds / slow;
            if (time > seconds) break;
            if (time - last >= 1 / fps / 3)
            {
                Equalizer(start + time);
                _onFrame?.Invoke(start + time);
                _frames.Add(new Frame(Capture(scale), start + time)); last = time;
            }
            await Task.Delay(8);
        }
        _clock = start + seconds;
    }

    private void Refresh() { _deck.ApplySettings(); Program.Call(_deck, "RenderCompact", []); }
    private void SelectPage(DeckPage page) => Program.Call(_deck, "SelectPage", [typeof(DeckPage), typeof(bool)], page, false);

    private void SetSyncRing(double fraction)
    {
        // The window never loads here, so ApplySettings stops the timer; the ring is shown only while it runs.
        ((DispatcherTimer)Program.Field(_deck, "_refresh")!).Start();
        Program.SetField(_deck, "_syncCycleStart", DateTime.UtcNow.AddSeconds(-60 * fraction));
        Program.Call(_deck, "RenderSyncButton", []);
    }

    // Equalizer bars follow an invented signal that flows from left to right, 75 ms per bar, like the real ones.
    private void Equalizer(double time)
    {
        static double Signal(double t) => Math.Clamp(.52 + .22 * Math.Sin(t * 7.1) + .16 * Math.Sin(t * 12.7 + 1.3) + .12 * Math.Sin(t * 3.3 + .4)
            + .25 * Math.Pow(Math.Max(0, Math.Sin(t * Math.PI * 2 * 1.05)), 12), .08, 1);
        foreach (var name in new[] { "_musicEqualizer", "_compactEqualizer" })
        {
            if (Program.Field(_deck, name) is not { } equalizer) continue;
            var levels = (double[])Program.Field(equalizer, "_levels")!;
            for (int i = 0; i < levels.Length; i++) levels[i] = Signal(time - i * .075);
            Program.SetProperty(equalizer, "Playing", true);
            Program.Call(equalizer, "Redraw", [typeof(bool)], true);
        }
    }

    private BitmapSource Capture(double scale)
    {
        _stage.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_stage.ActualWidth * scale), (int)Math.Ceiling(_stage.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(_stage); bitmap.Freeze();
        return bitmap;
    }

    // The equalizer is set just before the capture; the panel's own tick would otherwise bring the bars to rest.
    private async Task Save(string name, double scale, double equalizer = 1.3)
    {
        await Settle();
        Equalizer(equalizer);
        Write(Capture(scale), Path.Combine(output, name + ".png"));
        Console.WriteLine(name);
    }

    private static void Write(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // Lets layout, bindings and the first render pass finish.
    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
