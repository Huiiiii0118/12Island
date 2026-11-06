using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Microsoft.Win32;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow : Window
{
    private SettingsPalette _palette = new(false);
    private string Surface => _palette.Surface;
    private string Sidebar => _palette.Sidebar;
    private string Divider => _palette.Divider;
    private string Inset => _palette.Inset;
    private string TextPrimary => _palette.Primary;
    private string TextSecondary => _palette.Secondary;
    private string TextTertiary => _palette.Tertiary;
    private readonly App _app;
    private readonly StackPanel _navigation = new();
    private readonly StackPanel _content = new();
    private readonly TextBlock _status = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private ScrollViewer _scroll = null!;
    // The pages of the settings window, in the order of their number: the number is the page, as the switch below reads it.
    private const int NotifySettingsPage = 3, ClipboardSettingsPage = 4, ConvertSettingsPage = 5, MusicSettingsPage = 6, AboutPage = 9;
    private static string[] _pages => [Loc.T("基础设置", "Basics"), Loc.T("收起外观", "Collapsed view"), Loc.T("展开外观", "Expanded view"),
        Loc.T("通知", "Notifications"), Loc.T("剪贴板", "Clipboard"), Loc.T("格式转换", "File conversion"), Loc.T("音乐", "Music"),
        Loc.T("显示与读数", "Display and readings"), Loc.T("面板主题", "Panel theme"), Loc.T("关于", "About")];
    // The order of the pages in the navigation; the theme page (ThemePage) is listed with the appearance pages, and the system
    // monitor's page (MonitorSettingsPage) has its own group after music, before About.
    private static readonly int[] PageOrder = [0, 2, 1, ThemePage, 3, 4, 5, 6, MonitorSettingsPage, AboutPage];
    private int _page;
    private bool _preview;
    private StackPanel? _appRows;
    public FrameworkElement RootVisual { get; private set; } = null!;
    // The whole page, including the part scrolled out of view.
    private DeckSettings S => _app.Settings;
    private event Action? PaletteChanged;

    public static string StyleName(CompactStyle style) => style switch { CompactStyle.Notch => Loc.T("刘海", "Notch"), CompactStyle.Capsule => Loc.T("胶囊", "Capsule"), _ => Loc.T("指示条", "Indicator") };
    public static string BehaviorName(WindowBehavior behavior) => behavior switch { WindowBehavior.Normal => Loc.T("保持原有样式", "Keep style"), WindowBehavior.Line => Loc.T("显示为指示条", "As indicator"), _ => Loc.T("隐藏", "Hide") };

    private const double DefaultWidth = 960, DefaultHeight = 780;
    // The minimum size is 840 x 640, reduced where the screen's work area (less a 24 margin) is smaller. Returns the
    // largest size that fits.
    private (double Width, double Height) FitMinimumSize(Rect work, double scale)
    {
        double fitWidth = work.Width / scale - 24, fitHeight = work.Height / scale - 24;
        MinWidth = Math.Min(840, fitWidth); MinHeight = Math.Min(640, fitHeight);
        return (fitWidth, fitHeight);
    }
    // Keeps the size to open with next time: the normal size, also when the window is maximized or minimized.
    internal void RememberSize()
    {
        var size = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
        if (size.IsEmpty || size.Width <= 0 || size.Height <= 0) return;
        bool maximized = WindowState == WindowState.Maximized;
        if (S.SettingsWindowWidth == size.Width && S.SettingsWindowHeight == size.Height && S.SettingsWindowMaximized == maximized) return;
        Change(s => { s.SettingsWindowWidth = size.Width; s.SettingsWindowHeight = size.Height; s.SettingsWindowMaximized = maximized; });
    }
    public SettingsWindow(App app)
    {
        _app = app;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/SettingsResources.xaml", UriKind.Relative) });
        Style = (Style)FindResource(typeof(Window));
        // Sizes are DIPs, so the window looks the same at any scale factor. The default fits the page layout (the content
        // column is at most 858 wide); the last closed size replaces it. Either is fitted to the work area of the screen
        // the window opens on, which CenterScreen takes as the one under the mouse pointer.
        var (_, work, scale) = Native.WindowsHost.CursorScreen();
        var (fitWidth, fitHeight) = FitMinimumSize(work, scale);
        Width = Math.Clamp(S.SettingsWindowWidth ?? DefaultWidth, MinWidth, fitWidth);
        Height = Math.Clamp(S.SettingsWindowHeight ?? DefaultHeight, MinHeight, fitHeight);
        // Dragged to a smaller screen, such as a portrait one, the window must still be able to shrink to fit it.
        IntPtr monitor = IntPtr.Zero;
        void ScreenMaybeChanged()
        {
            if (PresentationSource.FromVisual(this) is null) return;
            var screen = Native.WindowsHost.WindowScreen(this);
            if (screen.Monitor == monitor) return;
            monitor = screen.Monitor; FitMinimumSize(screen.Work, screen.Scale);
        }
        SourceInitialized += (_, _) => ScreenMaybeChanged();
        LocationChanged += (_, _) => ScreenMaybeChanged();
        // WPF also raises DpiChanged with equal old and new values after a move; only a real change needs the sizes again.
        DpiChanged += (_, e) => { if (e.OldDpi.DpiScaleX == e.NewDpi.DpiScaleX) return; monitor = IntPtr.Zero; ScreenMaybeChanged(); };
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (S.SettingsWindowMaximized) WindowState = WindowState.Maximized;
        Closing += (_, _) => { if (!_app.Exiting) RememberSize(); };
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Native.SettingsChrome.Apply(this);
        SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(this, _palette.Dark);
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Deactivated += (_, _) => { EndAppDrag(false); QueuePreviewDismissal(); };
        // Back from PawnIO's installer or uninstaller: the enhanced sensors page shows what changed.
        PreviewMouseDown += (_, e) => DismissPreviewOutsideControl(e.OriginalSource);
        DismissPreviewOnCaptionPress();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _appDrag is not null) { EndAppDrag(false); e.Handled = true; } };
        ReleaseFocusOnEmptyClick(this, () => RootVisual);
        // Moving focus scrolls the control just into view; a little more keeps its focus outline visible at the edges.
        bool widening = false;
        _content.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            if (widening || !e.TargetRect.IsEmpty || e.TargetObject is not FrameworkElement target || !_content.IsAncestorOf(target)) return;
            e.Handled = true; widening = true;
            try { target.BringIntoView(new Rect(-6, -6, target.ActualWidth + 12, target.ActualHeight + 12)); }
            finally { widening = false; }
        }));
        _app.Prices.Changed += PricesChanged;
        _app.Updates.Changed += UpdatesChanged;
        Native.SensorGuard.Changed += SensorStopsChanged;
        SizeChanged += (_, _) => { if (_priceTable is not null) _priceTable.Height = Math.Max(220, ActualHeight - PriceTableReserve); };
        Closed += (_, _) => { EndAppDrag(false, false); _updateOpenGeneration++; _app.Updates.Changed -= UpdatesChanged; Native.SensorGuard.Changed -= SensorStopsChanged; SystemEvents.UserPreferenceChanged -= SystemThemeChanged; _app.Prices.Changed -= PricesChanged; _exceptionsReservedHeight = 0; _exceptionsOpen = false; _exceptionsHost = null; _scroll.ScrollChanged -= ExceptionsScrollChanged; if (!_app.Exiting) { _app.Deck.Preview(false); _app.FlushSettings(); } };
        StateChanged += (_, _) =>
        {
            UpdateCaption();
            if (WindowState == WindowState.Minimized) { _preview = false; _app.Deck.Preview(false); }
        };
        RebuildTheme();
    }

    private Button? _maximize;
    private void UpdateCaption()
    {
        if (_maximize is null) return;
        _maximize.Content = CaptionIcon(WindowState == WindowState.Maximized ? "restore" : "maximize");
        _maximize.ToolTip = WindowState == WindowState.Maximized ? Loc.T("还原", "Restore") : Loc.T("最大化", "Maximize");
        AutomationProperties.SetName(_maximize, (string)_maximize.ToolTip);
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        { if (IsLoaded && S.Theme == SettingsTheme.System && SettingsPalette.IsDark(S.Theme) != _palette.Dark) RebuildTheme(); });
    }
    private void PricesChanged()
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (IsLoaded) RefreshPriceRows(); }); }
    internal void RebuildTheme()
    {
        RememberFocus();
        EndAppDrag(false, false);
        _keptOffset = _scroll?.VerticalOffset ?? 0;
        // A language change rebuilds the window the same way.
        Title = Loc.T("设置", "Settings");
        _palette = new(SettingsPalette.IsDark(S.Theme)); _palette.Apply(Resources);
        Background = UI.Brush(Surface); Foreground = UI.Brush(TextPrimary); FontFamily = UI.PanelFont;
        // Detach reused panels before rebuilding the window's theme-specific surfaces.
        _navigation.Children.Clear(); _content.Children.Clear();
        if (_navigation.Parent is ScrollViewer oldNavigation) oldNavigation.Content = null;
        if (_scroll is not null) _scroll.Content = null;
        if (_status.Parent is Panel oldStatus) oldStatus.Children.Remove(_status);
        var outer = new Grid { Background = UI.Brush(Surface) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); outer.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid { Background = UI.Brush(Sidebar) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) }); sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.Children.Add(Brand(24, new Thickness(24, 0, 22, 0)));
        // The list of pages scrolls once it is taller than the window, so the last pages (About) stay reachable in a short window.
        // Like the page, it always keeps the scroll bar's 16 DIP column, here in place of its right margin, so the buttons and the
        // selected one's background keep their width when the bar appears. The scroll area clips at its edges, so the list keeps a
        // 4 DIP inset at the top, left and right for the keyboard focus outline, which reaches 2 DIP past a button; the scroll
        // area's own margin gives the top and left back, and the buttons stay where they were.
        _navigation.Margin = new Thickness(8, 4, 4, 12);
        var navigationScroll = new ScrollViewer { Style = (Style)FindResource("SettingsPageScrollViewer"), Content = _navigation, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(4, -4, 0, 0) };
        Grid.SetRow(navigationScroll, 1); sidebar.Children.Add(navigationScroll);
        outer.Children.Add(sidebar); outer.Children.Add(new Border { Width = 1, Background = UI.Brush(Divider), HorizontalAlignment = HorizontalAlignment.Right });
        // The scroll area clips at its edges, so the content keeps a 4 DIP inset inside it for keyboard focus outlines
        // that extend past a control; the outer margins give those 4 DIP back and the page stays where it was.
        var main = new DockPanel { Margin = new Thickness(32, 56, 32, 22), MaxWidth = 858 }; Grid.SetColumn(main, 1); outer.Children.Add(main);
        _status.Margin = new Thickness(4, 4, 4, 14); _status.FontSize = 12; DockPanel.SetDock(_status, Dock.Top); main.Children.Add(_status);
        _content.Margin = new Thickness(4);
        // Only the controls inside take focus; a focusable scroll area would draw the system outline around the whole page.
        // The page's scroll bar always keeps its 16 DIP column, taken from the right margin, so no page changes width when it starts scrolling.
        _scroll = new ScrollViewer { Style = (Style)FindResource("SettingsPageScrollViewer"), Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,4,0), Margin = new Thickness(0,0,-16,0), Focusable = false }; main.Children.Add(_scroll);
        var caption = new DockPanel { Height = 40, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 6, 0), LastChildFill = false }; Grid.SetColumn(caption, 1); outer.Children.Add(caption);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); caption.Children.Add(buttons);
        buttons.Children.Add(WindowButton("minimize", Loc.T("最小化", "Minimize"), () => SystemCommands.MinimizeWindow(this)));
        _maximize = WindowButton("maximize", Loc.T("最大化", "Maximize"), () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }); buttons.Children.Add(_maximize);
        buttons.Children.Add(WindowButton("close", Loc.T("关闭设置", "Close settings"), Close, true));
        // Focusable only to hold focus after a click on empty space; Tab never stops on it and it draws no outline.
        var frame = new Border { Child = outer, BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1), Focusable = true, FocusVisualStyle = null };
        KeyboardNavigation.SetIsTabStop(frame, false);
        RadioGroups.Use(); RadioGroups.SetEnabled(frame, true);
        Content = frame; RootVisual = frame; UpdateCaption(); Native.SettingsChrome.SetTheme(this, _palette.Dark); ShowPage(_page);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsError"); PaletteChanged?.Invoke();
    }
    // Clicking anything that is not a control takes focus off a text field, as elsewhere in Windows (EmptyClickFocus).
    private static void ReleaseFocusOnEmptyClick(Window window, Func<UIElement> holder) => EmptyClickFocus.Attach(window, holder);
    // The wordmark alone: no mark, no rule, one colour, as a Mac app's name is set. The sidebar's brand and the About page's
    // heading are the same thing at two sizes.
    // The mark and the name together, as a Mac app's About box sets them: the icon, then the name in two weights.
    private StackPanel Brand(double size, Thickness margin)
    {
        var brand = new StackPanel { Margin = margin, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        brand.Children.Add(new Image
        {
            Source = Mark, Width = Math.Round(size * 1.18), Height = Math.Round(size * 1.18),
            Margin = new Thickness(0, 0, Math.Round(size * .4), 0), VerticalAlignment = VerticalAlignment.Center,
        });
        brand.Children.Add(Wordmark(size));
        return brand;
    }
    // "12" set heavier than "island", both in the text colour: the contrast carries the name, not a second colour.
    private TextBlock Wordmark(double size)
    {
        var name = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = UI.Brush(TextPrimary), VerticalAlignment = VerticalAlignment.Center };
        name.Inlines.Add(new Run("12") { FontWeight = FontWeights.SemiBold });
        // A thin space carries the gap the name needs, so the two words read as one mark.
        name.Inlines.Add(new Run("\u2009island") { FontWeight = FontWeights.Light });
        AutomationProperties.SetName(name, "12 island"); return name;
    }
    // The icon's mark at any size: the dark squircle, the white capsule and the coral dot. The capsule takes the tray-size
    // proportions (see scripts/icon), since these marks are never wider than about 40 px and the numeral would be under a pixel.
    private static readonly ImageSource Mark = BuildMark();
    private static ImageSource BuildMark()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0x1E, 0x20, 0x26), Color.FromRgb(0x07, 0x08, 0x0B), 90), null, new Rect(0, 0, 256, 256), 58, 58);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE4, 0xE6, 0xEB), 90), null, new Rect(38, 82, 180, 92), 46, 46);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0xA3, 0x85)), null, new Point(188, 128), 12, 12);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    private TextBlock TextLine(string text, double size = 13, string color = "#F1F1F4", FontWeight? weight = null)
    {
        var block = UI.Line(text, size, color, weight); block.FontFamily = UI.PanelFont;
        string? resource = color == TextPrimary ? "SettingsTextPrimary" : color == TextSecondary ? "SettingsTextSecondary" :
            color is "#F0A6AA" or "#B12E3A" ? "SettingsError" : null;
        if (resource is not null) block.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return block;
    }
    public void SaveStatus(string message, bool error = false)
    { _status.Text = error ? message : ""; _status.Foreground = UI.Brush(_palette.Dark ? "#F0A6AA" : "#B12E3A"); _status.Visibility = error ? Visibility.Visible : Visibility.Collapsed; }

    private Button ActionButton(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(primary ? "SettingsPrimaryButton" : "SettingsButton") };
        button.Click += (_, _) => action(); return button;
    }

    private Button WindowButton(string glyph, string label, Action action, bool close = false)
    {
        var button = ActionButton("", action); button.Content = CaptionIcon(glyph); button.Width = 42; button.Height = 32;
        button.Padding = new Thickness(0); button.Margin = new Thickness(1, 4, 0, 0); button.ToolTip = label;
        if (close) button.Style = (Style)FindResource("SettingsCloseButton");
        WindowChrome.SetIsHitTestVisibleInChrome(button, true); AutomationProperties.SetName(button, label); return button;
    }
    private Path CaptionIcon(string kind)
    {
        var data = kind switch { "minimize" => "M 1,6 L 11,6", "maximize" => "M 1,1 L 11,1 L 11,11 L 1,11 Z",
            "restore" => "M 4,1 L 11,1 L 11,8 M 1,4 L 8,4 L 8,11 L 1,11 Z", _ => "M 1,1 L 11,11 M 11,1 L 1,11" };
        var path = Stroke(data,12,12,TextSecondary);
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return path;
    }

    // Small vector icons drawn with strokes, independent of icon font availability.
    private static Path Stroke(string data, double width, double height, string color) => new()
    {
        Data = Geometry.Parse(data), Width = width, Height = height, Stroke = UI.Brush(color), StrokeThickness = 1.4,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    // The drag handle: two columns of three dots.
    private static UniformGrid Grip(string color)
    {
        var grip = new UniformGrid { Columns = 2, Rows = 3, Width = 9, Height = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 6; i++) grip.Children.Add(new Ellipse { Width = 2.4, Height = 2.4, Fill = UI.Brush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grip;
    }

    private void Change(Action<DeckSettings> edit) { var settings = S.Copy(); edit(settings); _app.UpdateSettings(settings); }

    // Many choices rebuild the page. The control that had keyboard focus is replaced by an identical one, so focus
    // moves to it by name (and position among controls of the same name) instead of being dropped.
    private (string Key, int Index)? _focusToRestore;
    private static string FocusKey(DependencyObject element) =>
        AutomationProperties.GetAutomationId(element) is { Length: > 0 } id ? "#" + id : AutomationProperties.GetName(element);
    private IEnumerable<FrameworkElement> FocusCandidates(string key) =>
        new DependencyObject[] { _navigation, _content }.SelectMany(Descendants).OfType<FrameworkElement>().Where(e => e.Focusable && FocusKey(e) == key);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
    private void RememberFocus()
    {
        if (_focusToRestore is not null || Keyboard.FocusedElement is not FrameworkElement focused) return;
        if (!_navigation.IsAncestorOf(focused) && !_content.IsAncestorOf(focused)) return;
        var key = FocusKey(focused);
        if (key.Length == 0) return;
        int index = FocusCandidates(key).ToList().IndexOf(focused);
        if (index >= 0) _focusToRestore = (key, index);
    }
    private void RestoreFocus()
    {
        if (_focusToRestore is not { } target) return;
        _focusToRestore = null;
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is FrameworkElement current && IsAncestorOf(current)) return;
            FocusCandidates(target.Key).Where(e => e.IsVisible && e.IsEnabled).ElementAtOrDefault(target.Index)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // A top-level heading in the sidebar: smaller and quieter than the pages it holds, always in view.
    private void AddSection(string title)
    {
        var head = TextLine(title, 11.5, TextTertiary, FontWeights.SemiBold);
        head.Margin = new Thickness(12, 18, 0, 6);
        _navigation.Children.Add(head);
    }

    public void ShowPage(int page)
    {
        if (page < 0 || page >= _pages.Length) return;
        RememberFocus();
        if (page != _page) { _expandedSource = null; _openSizes.Clear(); }
        _sourceTestCancellation?.Cancel();
        EndAppDrag(false, false);
        _appRowMotionVersions.Clear();
        _sourceRows.Clear();
        _appRows = null;
        if (page != _page && _preview) { _preview = false; _app.Deck.Preview(false); }
        // Rebuilding the same page after a change keeps the reader where they were; only switching pages starts at the top.
        double keep = page == _page ? Math.Max(_scroll.VerticalOffset, _keptOffset) : 0; _keptOffset = 0;
        // The exception list's reserved height only lasts for this visit of the page.
        _exceptionsReservedHeight = 0; _exceptionsOpen = false; _exceptionsHost = null;
        _scroll.ScrollChanged -= ExceptionsScrollChanged;
        _page = page; _navigation.Children.Clear(); _content.Children.Clear(); _aboutUpdateView = null; _scroll.ScrollToTop();
        foreach (int i in PageOrder)
        {
            // Five entries stand at the top. 基础设置 and 关于 are pages of their own, the other three head a group. Nothing folds
            // away: every page keeps its place in view, and the group headings are set a step smaller than their pages.
            if (i is 2 or 3 or MonitorSettingsPage) AddSection(i switch
            {
                2 => Loc.T("外观", "Appearance"), 3 => Loc.T("功能设置", "Features"), _ => Loc.T("系统", "System")
            });
            bool selected = page == i;
            if (i is 0 or AboutPage)
            {
                // A page with no pages under it: its own name is the heading, and pressing it opens the page.
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(TextLine(_pages[i], 11.5, selected ? TextPrimary : TextTertiary, FontWeights.SemiBold));
                if (i == AboutPage) { _updateBadge = UpdateBadge(); title.Children.Add(_updateBadge); }
                var entry = new Button
                {
                    Style = (Style)FindResource("SettingsNav"), Content = title, Height = 30, Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(0, i == 0 ? 0 : 20, 0, 3), Cursor = Cursors.Hand, Tag = selected ? "Selected" : null
                };
                entry.Click += (_, _) => ShowPage(i);
                AutomationProperties.SetName(entry, _pages[i]);
                _navigation.Children.Add(entry);
                if (selected) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (entry.IsLoaded) entry.BringIntoView(); });
                continue;
            }
            int target = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = NavigationIcon(i, selected ? TextPrimary : TextSecondary); icon.Margin = new Thickness(0, 0, 9, 0);
            row.Children.Add(icon);
            row.Children.Add(TextLine(_pages[i], 12.5, selected ? TextPrimary : TextSecondary, selected ? FontWeights.Medium : FontWeights.Normal));
            var button = new Button
            {
                Style = (Style)FindResource("SettingsNav"), Content = row, Height = 32, Padding = new Thickness(22, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 1), Tag = selected ? "Selected" : null
            };
            button.Click+=(_,_)=>ShowPage(target); AutomationProperties.SetName(button,_pages[i]); _navigation.Children.Add(button);
            // The rebuilt list keeps its scroll position; a page opened from elsewhere (the tray, a link) is brought into view.
            if (selected) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (button.IsLoaded) button.BringIntoView(); });
        }
        // The About page is titled by the wordmark itself, so the name does not appear twice.
        var head = new DockPanel { Margin = new Thickness(0,0,0,page == AboutPage ? 10 : 20) };
        if (page is 0 or 1 or 2 or MusicSettingsPage or MonitorSettingsPage or ThemePage)
        {
            // Resets every setting on the page, so it asks first (SW-09, S-03); it keeps the look of the other "restore defaults" links.
            var reset=ActionButton(Loc.T("恢复默认", "Restore defaults"),()=>{ if(Confirm(Loc.T("恢复默认", "Restore defaults"),ResetConfirmText(page),Loc.T("恢复默认", "Restore defaults")))ResetPage(); }); reset.FontSize=11; DockPanel.SetDock(reset,Dock.Right); head.Children.Add(reset);
        }
        head.Children.Add(page == AboutPage ? Brand(30, new Thickness(0)) : TextLine(_pages[page],24,TextPrimary,FontWeights.SemiBold)); _content.Children.Add(head);
        switch(page)
        {
            case 0: General(); break; case 1: Compact(); break; case 2: Expanded(); break;
            case NotifySettingsPage: NotifySettings(); break; case ClipboardSettingsPage: ClipboardSettings(); break;
            case ConvertSettingsPage: ConvertSettings(); break; case MusicSettingsPage: MusicSettings(); break;
            case MonitorSettingsPage: MonitorSettings(); break; case ThemePage: ThemesPage(); break;
            case AboutPage: About(); break;
        }
        if (_content.Children[^1] is Border lastGroup) lastGroup.Margin = new Thickness(lastGroup.Margin.Left, lastGroup.Margin.Top, lastGroup.Margin.Right, 0);
        // Apply the offset before the frame is drawn, so the rebuilt page never shows its top for a frame.
        if (keep > 0) { _scroll.UpdateLayout(); _scroll.ScrollToVerticalOffset(keep); _scroll.UpdateLayout(); }
        RestoreFocus();
    }
    // The scroll position of the page being rebuilt by a theme change, whose scroll viewer is replaced.
    private double _keptOffset;
    private static Path NavigationIcon(int page, string color) => Stroke(page switch
    {
        0 => "M 1,4 H 15 M 1,12 H 15 M 5,1 V 7 M 11,9 V 15",
        1 => "M 1,3 H 15 V 13 H 1 Z M 5,3 V 6 H 11 V 3",
        2 => "M 1,6 V 1 H 6 M 10,1 H 15 V 6 M 15,10 V 15 H 10 M 6,15 H 1 V 10",
        MonitorSettingsPage => "M 1,9 H 4 L 6,4 L 9,13 L 11,7 L 12,9 H 15",
        ThemePage => "M 8,1 C 4,1 1,4 1,8 C 1,12 4,15 8,15 C 9.4,15 9.6,13.6 8.8,12.8 C 8,12 8.6,10.5 10,10.5 H 12 C 13.7,10.5 15,9.2 15,7.5 C 15,4 12,1 8,1 Z M 4.6,8 V 8.1 M 6,4.8 V 4.9 M 9.6,4.4 V 4.5 M 12,6.8 V 6.9",
        3 => "M 8,2 C 5.2,2 3,4.2 3,7 V 10.5 L 1.8,12.6 H 14.2 L 13,10.5 V 7 C 13,4.2 10.8,2 8,2 Z M 6.2,14 A 1.9,1.9 0 0 0 9.8,14",
        4 => "M 3,2 H 13 V 13 H 3 Z M 5.5,2 V 1 M 10.5,2 V 1 M 5.5,12 V 11 M 10.5,12 V 11 M 6,5.5 H 10 M 6,8 H 10",
        5 => "M 2,5 H 12 L 9,2 M 14,11 H 4 L 7,14",
        6 => "M 6,12.5 V 3 L 14,1.5 V 11 M 6,12.5 A 2,2 0 1 1 2,12.5 A 2,2 0 1 1 6,12.5 M 14,11 A 2,2 0 1 1 10,11 A 2,2 0 1 1 14,11",
        _ => "M 8,1 A 7,7 0 1 1 7.99,1 M 8,7 V 12 M 8,4 V 4.2"
    },16,16,color);
    private void General()
    {
        SectionTitle(Loc.T("启动与资源占用", "Startup and resources")); Group(Toggle(Loc.T("开机自启动", "Start with Windows"),S.LaunchAtStartup,value =>
        {
            try { Native.StartupRegistration.SetEnabled(value); Change(s=>s.LaunchAtStartup=value); }
            catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { SaveStatus(Loc.T("无法设置自启动：", "Could not change the startup setting: ")+ex.Message,true); ShowPage(0); }
        }),
            Toggle(Loc.T("显示托盘图标", "Show tray icon"), S.ShowTrayIcon, value => Change(s => s.ShowTrayIcon = value),
                help: Loc.T("关闭后，再次打开 12 island 即可显示隐藏的面板并打开设置。",
                    "When off, open 12 island again to show hidden panels and open the settings.")),
            Toggle(Loc.T("空闲时释放内存", "Free memory when idle"), S.TrimMemoryWhenIdle, value => Change(s => s.TrimMemoryWhenIdle = value),
                help: Loc.T("面板收起 45 秒后释放暂时不用的内存，最多每 5 分钟一次。开启后内存占用更低，但下次展开时可能稍有卡顿。",
                    "45 seconds after the panel collapses, memory not in use is released, at most once every 5 minutes. Memory use is lower, but the next time the panel opens it may stutter slightly.")),
            Row(Loc.T("退出程序", "Exit the app"), ActionButton(Loc.T("退出", "Exit"), _app.ExitApplication)));
        SectionTitle(Loc.T("语言与缩放", "Language and scale"));
        // The window's own light or dark follows the panel's mode, chosen once under "面板主题": there is no second switch here.
        // Each language is named in itself. Choosing one rebuilds the window in it, as a theme change does.
        (string Value, string Name)[] languages = [(Loc.Chinese, "简体中文"), (Loc.English, "English")];
        var language = new ComboBox { Width=150,Style=(Style)FindResource("SettingsCombo"),ItemsSource=languages.Select(l=>l.Name).ToList(),
            SelectedIndex=Math.Max(0,Array.FindIndex(languages,l=>l.Value==S.Language)) };
        AutomationProperties.SetName(language,Loc.T("语言", "Language")); AutomationProperties.SetAutomationId(language,"language"); PopupMotion.Attach(language);
        language.SelectionChanged+=(_,_)=>
        {
            if(language.SelectedIndex<0||languages[language.SelectedIndex].Value==S.Language) return;
            var value=languages[language.SelectedIndex].Value; Change(s=>s.Language=value); RebuildTheme();
        };
        // The scale TwelveIsland is drawn at, collapsed and expanded alike (PanelScales); sizes set elsewhere are before it.
        var scales = PanelScales.Choices;
        var scale = new ComboBox { Width=150,Style=(Style)FindResource("SettingsCombo"),ItemsSource=scales.Select(value=>value+"%").ToList(),
            SelectedIndex=Math.Max(0,scales.ToList().IndexOf(PanelScales.Fit(S.PanelScale))) };
        AutomationProperties.SetName(scale,Loc.T("缩放", "Scale")); PopupMotion.Attach(scale);
        scale.SelectionChanged+=(_,_)=> { if(scale.SelectedIndex>=0) { var value=scales[scale.SelectedIndex]; Change(s=>s.PanelScale=value); } };
        Group(Row(Loc.T("语言", "Language"),language),Row(Loc.T("缩放", "Scale"),scale));
        DisplayChoice(); Interaction(); WindowRules();
    }
    // Either the primary display, or any set of displays, each with its own switch. A chosen display that is not
    // connected keeps its switch, marked, so that it can be removed. The last switch that is on cannot be turned off.
    private void DisplayChoice()
    {
        SectionTitle(Loc.T("显示位置", "Display"));
        var displays = Native.Displays.Current();
        var modes = new UniformGrid { Columns = 2, Width = 240 };
        foreach (var (placement, name) in new[] { (DisplayPlacement.Primary, Loc.T("跟随主显示器", "Primary display")), (DisplayPlacement.Chosen, Loc.T("自选显示器", "Choose displays")) })
        {
            var option = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = name, IsChecked = S.Display == placement, GroupName = "Display" };
            AutomationProperties.SetName(option, Loc.T("显示在 · ", "Show on · ") + name);
            option.Checked += (_, _) =>
            {
                if (S.Display == placement) return;
                // Choosing displays for the first time starts from the one TwelveIsland is on now.
                var start = displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
                Change(s =>
                {
                    s.Display = placement;
                    if (placement == DisplayPlacement.Chosen && s.DisplayMonitors.Count == 0 && start is not null) s.DisplayMonitors.Add(new ChosenDisplay(start.Id, start.Name));
                });
                ShowPage(0);
            };
            modes.Children.Add(option);
        }
        var rows = new List<FrameworkElement> { Row(Loc.T("显示在", "Show on"), SegmentBorder(modes)) };
        if (S.Display == DisplayPlacement.Chosen)
        {
            var entries = displays.Select(d => (d.Id, d.Name, Label: Native.Displays.Label(d, displays) + (d.Primary ? Loc.T(" · 主显示器", " · primary") : ""))).ToList();
            entries.AddRange(S.DisplayMonitors.Where(c => displays.All(d => d.Id != c.Id))
                .Select(c => (c.Id, c.Name, Label: (c.Name.Length > 0 ? c.Name : Loc.T("显示器", "Display")) + Loc.T("（未连接）", " (not connected)"))));
            var switches = new List<(CheckBox Box, string Id)>();
            void UpdateSwitches() { foreach (var (box, id) in switches) box.IsEnabled = !(box.IsChecked == true && S.DisplayMonitors.Count == 1); }
            foreach (var (id, name, label) in entries)
            {
                var row = Toggle(label, S.DisplayMonitors.Any(c => c.Id == id), on =>
                {
                    Change(s =>
                    {
                        s.DisplayMonitors.RemoveAll(c => c.Id == id);
                        if (on) s.DisplayMonitors.Add(new ChosenDisplay(id, name));
                    });
                    UpdateSwitches();
                });
                // The displays are items under "Show on", so their names are not set as headings.
                if (row is Border { Child: CheckBox { Content: TextBlock text } box }) { text.FontWeight = FontWeights.Normal; switches.Add((box, id)); }
                rows.Add(row);
            }
            UpdateSwitches();
        }
        Group([.. rows]);
    }
    // Monitors were connected, removed or rearranged; the list of displays is built again.
    // A taskbar that appeared, moved or went away also shows or hides the collapsed group's anchor row.
    internal void DisplaysChanged() { if (!IsLoaded) return; if (_page is 0 or 1) ShowPage(_page); }
    private void Compact()
    {
        HideHotkeySection();
        SectionTitle(Loc.T("收起样式", "Collapsed style"));
        // The grid clips at its own bounds, so each card keeps 3 DIP or more of its cell free on every side of the
        // keyboard focus ring: 6 DIP left and right (the grid overhangs the page by the same amount) and 3 DIP above.
        var styles=new UniformGrid { Columns=3,Margin=new Thickness(-6,0,-6,24) };
        _styleCards.Clear();
        foreach(var style in Enum.GetValues<CompactStyle>())
        {
            var button=new RadioButton { Style=(Style)FindResource("SettingsChoice"),Content=CompactStylePreview.Create(style,_palette.Dark,S.ShowsClock(style)),Margin=new Thickness(6,3,6,0),Tag=StyleName(style),GroupName="CompactStyle",IsChecked=S.Style==style };
            button.Checked+=(_,_)=>{ var previous=S.Style; Change(s=>s.Style=style); ShowStyleOptions(style,previous); }; AutomationProperties.SetName(button,StyleName(style)); styles.Children.Add(button);
            _styleCards[style]=button;
        }
        _styleOptions=new Grid();
        _content.Children.Add(styles); _content.Children.Add(_styleOptions); ShowStyleOptions(S.Style,null);
    }
    // One row per expanded page: its switch at the right, and its sizes under the row while the row is open, set in
    // from the row's title. A row opens and closes only by its own arrow or a click on it, whether or not its page is shown.
    private readonly HashSet<DeckPage> _openSizes = [];
    private void Expanded()
    {
        // The last viewed page is always offered, followed by each page that is shown. A choice whose page is switched off
        // reads as the last viewed page, which is what the panel does then, and returns when the page is shown again.
        // Switching a page on or off refills the list in place, so the page is not rebuilt and the switch keeps its animation.
        var start = new ComboBox { Width = 150, Style = (Style)FindResource("SettingsCombo") };
        AutomationProperties.SetName(start, Loc.T("每次打开时显示", "Page shown on opening")); PopupMotion.Attach(start);
        List<(DefaultDeckPage Value, string Label)> starts = [];
        bool filling = false;
        void FillStarts()
        {
            starts = [(DefaultDeckPage.Last, Loc.T("上次停留的页面", "Last viewed page"))];
            foreach (var page in DeckPages.Visible(S))
                starts.Add(page switch
                {
                    DeckPage.Music => (DefaultDeckPage.Music, Loc.T("音乐", "Music")),
                    DeckPage.Monitor => (DefaultDeckPage.Monitor, Loc.T("系统监控", "System monitor")),
                    DeckPage.Notify => (DefaultDeckPage.Notify, Loc.T("通知", "Notifications")),
                    DeckPage.Clipboard => (DefaultDeckPage.Clipboard, Loc.T("剪贴板", "Clipboard")),
                    _ => (DefaultDeckPage.Convert, Loc.T("格式转换", "Convert"))
                });
            filling = true;
            start.ItemsSource = starts.Select(option => option.Label).ToList();
            start.SelectedIndex = Math.Max(0, starts.FindIndex(option => option.Value == S.DefaultPage));
            filling = false;
        }
        FillStarts();
        start.SelectionChanged += (_, _) => { if (!filling && start.SelectedIndex >= 0) Change(s => s.DefaultPage = starts[start.SelectedIndex].Value); };
        SectionTitle(Loc.T("显示的页面", "Pages shown"), help: () => Loc.T("除了在下面设置宽度与高度，也可以把鼠标移到展开面板的左右两边、底边或底角，直接拖动调整大小。",
            "Besides setting the width and height below, you can point at the expanded panel's sides, bottom edge or bottom corners and drag them to resize it."));
        // The rows are in the order of the page switch; dragging one by its handle moves its page there at once.
        var pages = new StackPanel();
        _pageRows.Clear();
        foreach (var page in S.PageOrder)
            pages.Children.Add(page switch
            {
                DeckPage.Music => SizeRow(Loc.T("显示音乐", "Show music"), Loc.T("音乐", "music"), page, S.MusicPage, value => { Change(s => s.MusicPage = value); FillStarts(); }, Collect(MusicSizeControls), pages, FillStarts),
                DeckPage.Monitor => SizeRow(Loc.T("显示系统监控", "Show system monitor"), Loc.T("系统监控", "system monitor"), page, S.MonitorPage, value => { Change(s => s.MonitorPage = value); FillStarts(); }, Collect(MonitorSizeControls), pages, FillStarts),
                DeckPage.Notify => SizeRow(Loc.T("显示通知", "Show notifications"), Loc.T("通知", "notifications"), page, S.NotifyPage, value => { Change(s => s.NotifyPage = value); FillStarts(); }, Collect(() => PageSizeControls(DeckPage.Notify)), pages, FillStarts),
                DeckPage.Clipboard => SizeRow(Loc.T("显示剪贴板", "Show clipboard"), Loc.T("剪贴板", "clipboard"), page, S.ClipboardPage, value => { Change(s => s.ClipboardPage = value); FillStarts(); }, Collect(() => PageSizeControls(DeckPage.Clipboard)), pages, FillStarts),
                _ => SizeRow(Loc.T("显示格式转换", "Show conversion"), Loc.T("格式转换", "conversion"), page, S.ConvertPage, value => { Change(s => s.ConvertPage = value); FillStarts(); }, Collect(() => PageSizeControls(DeckPage.Convert)), pages, FillStarts)
            });
        UpdateDividers(pages);
        Group(pages);
        // Settings that apply across the expanded pages rather than to one of them.
        SectionTitle(Loc.T("通用", "General"));
        Group(Row(Loc.T("每次打开时显示", "Page shown on opening"), start),
            Toggle(Loc.T("右键点击打开设置", "Right-click opens settings"), S.RightClickSettings, value => Change(s => s.RightClickSettings = value),
                help: Loc.T("面板展开时，在面板内任意位置单击右键即可打开设置。", "When the panel is expanded, right-click anywhere inside it to open settings.")),
            MouseWheelRow(),
            ExpandPlaceRow(),
            HotkeyRow(HotkeyKind.Expand),
            HotkeyRow(HotkeyKind.Resize));
    }
    private const double PageRowHeight = 44;
    private readonly List<(Border Row, StackPanel Sizes, Button Arrow)> _pageRows = [];
    // A page's row: the handle, then the switch with its sizes underneath. A closed row is one line high, as the drag needs.
    private Border SizeRow(string title, string name, DeckPage page, bool shown, Action<bool> show, StackPanel sizes, StackPanel rows, Action reordered)
    {
        bool open = _openSizes.Contains(page);
        Button arrow = null!;
        Border item = null!;
        void Flip()
        {
            bool now = sizes.Visibility != Visibility.Visible;
            if (now) _openSizes.Add(page); else _openSizes.Remove(page);
            sizes.Visibility = now ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, now);
            item.Height = now ? double.NaN : PageRowHeight;
        }
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => show(true);
        toggle.Unchecked += (_, _) => show(false);
        arrow = ChevronButton(Loc.T("展开" + (name.Length > 0 && name[0] < 128 ? " " : "") + name + "尺寸", "Show " + name + " sizes"), () => Flip());
        arrow.Margin = new Thickness(12, 0, 0, 0);
        PointArrow(arrow, open);
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; controls.Children.Add(toggle); controls.Children.Add(arrow);
        var header = Row(title, controls); header.Background = Brushes.Transparent;
        // Clicking anywhere else on the row opens or closes it, as the application rows do.
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        sizes.Visibility = open ? Visibility.Visible : Visibility.Collapsed; sizes.Margin = new Thickness(20, 4, 0, 8);
        var body = new StackPanel(); body.Children.Add(header); body.Children.Add(sizes); Grid.SetColumn(body, 1);
        var handle = new GripHandle { Width = 22, Height = PageRowHeight, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(handle); row.Children.Add(body);
        item = new Border { Child = row, Height = open ? double.NaN : PageRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = page, RenderTransform = new TranslateTransform() };
        _pageRows.Add((item, sizes, arrow));
        // The sizes close when a drag begins, so every row is one line high while they move.
        AttachDrag(handle, title, () => rows, PageRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is DeckPage).Select(b => (DeckPage)b.Tag).ToArray();
            Change(s => s.PageOrder = [.. order]);
            reordered();
        }, ClosePageSizes);
        return item;
    }
    private void ClosePageSizes()
    {
        _openSizes.Clear();
        foreach (var (row, sizes, arrow) in _pageRows) { sizes.Visibility = Visibility.Collapsed; PointArrow(arrow, false); row.Height = PageRowHeight; }
    }
    private void QuickSizeRow(UniformGrid sizes)
    {
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(0, 20, 0, 0) });
        Target.Children.Add(QuickRow(Loc.T("快捷尺寸", "Quick sizes"), sizes));
    }
    // A row of quick choices, as "快捷尺寸": labels inside an open row are set lighter than the row's own title.
    private Grid QuickRow(string title, UniformGrid choices)
    {
        var row = Row(title, SegmentBorder(choices));
        if (row.Children[0] is StackPanel { Children: [TextBlock label, ..] }) label.FontWeight = FontWeights.Normal;
        return row;
    }
    private void UsageSizes()
    {
        var dimensions=new Grid { Margin=new Thickness(0,8,0,0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width=Dimension(Loc.T("宽度", "Width"),S.Width,S.MinimumWidth,1200,value=>Resize(s=>{s.Width=value;s.QuickSize=null;})); width.Margin=new Thickness(0,0,20,0); dimensions.Children.Add(width);
        var height=Dimension(Loc.T("高度", "Height"),S.Height,140,400,value=>Resize(s=>{s.Height=value;s.QuickSize=null;})); height.Margin=new Thickness(20,0,0,0); Grid.SetColumn(height,1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        // Quick sizes are computed from the enabled application count so each lands on one of the panel's typography tiers;
        // the segment that matches the current width and height is shown as selected.
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (presetWidth, presetHeight) = S.PresetSize(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "PanelSize",
                IsChecked = (S.QuickSize ?? S.CurrentPreset) == size };
            AutomationProperties.SetName(choice, Loc.T("快捷尺寸 ", "Quick size ") + PanelSizes.Name(size));
            // The chosen size is written once; removing or adding applications later leaves it untouched.
            choice.Checked += (_, _) => { Resize(s => { s.Width = presetWidth; s.Height = presetHeight; s.QuickSize = size; }); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
    // What the "restore defaults" confirmation says. The quota page also puts the application list back (ResetPage, case 3), which removes the applications
    // the user added and their quota sources, the largest loss of any page, so it says so.
    private string ResetConfirmText(int page) => Loc.T($"将把“{_pages[page]}”页的全部设置恢复为默认值。", $"This puts every setting on the {_pages[page]} page back to its default.")
        + (page == 3 ? Loc.T("\n\n应用列表也会换回默认：你添加的应用、各应用的额度来源与自定义设置都会消失。", "\n\nThe application list also goes back to the default: the applications you added, and each one's quota source and custom settings, are removed.") : "");
    private void ResetPage()
    {
        var defaults=new DeckSettings();
        if(_page==0)
        {
            try { Native.StartupRegistration.SetEnabled(false); }
            catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {SaveStatus(Loc.T("无法恢复自启动设置：", "Could not restore the startup setting: ")+ex.Message,true);return;}
        }
        Change(s=>
        {
            switch(_page)
            {
                case 0:s.LaunchAtStartup=false;s.ShowTrayIcon=defaults.ShowTrayIcon;s.TrimMemoryWhenIdle=defaults.TrimMemoryWhenIdle;s.Theme=defaults.Theme;s.Language=Loc.SystemLanguage();s.OpenDelay=defaults.OpenDelay;s.CloseDelay=defaults.CloseDelay;s.Animations=defaults.Animations;s.AnimationDuration=defaults.AnimationDuration;s.ForceTopmost=defaults.ForceTopmost;s.Maximized=defaults.Maximized;s.Borderless=defaults.Borderless;s.Exclusive=defaults.Exclusive;s.FullscreenRules=[];s.Display=defaults.Display;s.DisplayMonitors=[];s.PanelScale=defaults.PanelScale;break;
                case 1:s.Style=defaults.Style;s.NotchSummary=defaults.NotchSummary;s.QuotaRingLetters=defaults.QuotaRingLetters;s.NotchMusic=defaults.NotchMusic;s.CapsuleSummary=defaults.CapsuleSummary;s.CapsuleMusic=defaults.CapsuleMusic;s.MusicIndicatorProgress=defaults.MusicIndicatorProgress;
                    s.NotchEqualizer=defaults.NotchEqualizer;s.CapsuleEqualizer=defaults.CapsuleEqualizer;s.NotchClock=defaults.NotchClock;s.CapsuleClock=defaults.CapsuleClock;s.ClockStyle=defaults.ClockStyle;s.ClockColor=defaults.ClockColor;s.ClockCustomColor=defaults.ClockCustomColor;s.ClockGradient=[.. PanelTheme.Current.ClockGradients[0].Stops];s.Clock24Hour=defaults.Clock24Hour;s.ClockSeconds=defaults.ClockSeconds;CopyCollapsedSize(s,defaults);s.GripDoubleClick=defaults.GripDoubleClick;s.HideHotkey=defaults.HideHotkey;
                    s.NotchMonitor=defaults.NotchMonitor;s.CapsuleMonitor=defaults.CapsuleMonitor;s.NotchOrder=defaults.NotchOrder;s.CapsuleOrder=defaults.CapsuleOrder;
                    // The clock goes back to the one the panel theme suggests, as choosing the theme gives.
                    PanelThemes.ApplyClock(PanelTheme.Current,s);break;
                case 2:s.UsagePage=false;s.MusicPage=defaults.MusicPage;s.DefaultPage=defaults.DefaultPage;s.RightClickSettings=defaults.RightClickSettings;s.Width=defaults.Width;s.Height=defaults.Height;s.QuickSize=null;s.MusicWidth=defaults.MusicWidth;s.MusicHeight=defaults.MusicHeight;s.ExpandHotkey=defaults.ExpandHotkey;s.ResizeHotkey=defaults.ResizeHotkey;s.MouseWheel=defaults.MouseWheel;s.ExpandAtPointer=defaults.ExpandAtPointer;
                    s.MonitorPage=defaults.MonitorPage;s.MonitorWidth=defaults.MonitorWidth;s.MonitorHeight=defaults.MonitorHeight;s.PageOrder=defaults.PageOrder;break;
                case MonitorSettingsPage:s.MonitorPalette=defaults.MonitorPalette;s.MonitorCustomColors=defaults.MonitorCustomColors;s.CompactWarningColor=null;s.CompactCriticalColor=null;s.CompactSmoothing=defaults.CompactSmoothing;s.MonitorInterval=defaults.MonitorInterval;s.MonitorProcesses=defaults.MonitorProcesses;s.MonitorSort=defaults.MonitorSort;s.MonitorMain=defaults.MonitorMain;s.MonitorProcessView=defaults.MonitorProcessView;
                    s.CompactMonitor=defaults.CompactMonitor;s.CompactMonitorItems=defaults.CompactMonitorItems;s.CompactReadings=defaults.CompactReadings;s.NotchAutoWidth=defaults.NotchAutoWidth;s.CapsuleAutoWidth=defaults.CapsuleAutoWidth;s.MonitorTemperatures=defaults.MonitorTemperatures;s.MonitorStorage=defaults.MonitorStorage;s.MonitorPower=defaults.MonitorPower;break;
                case MusicSettingsPage:s.EqualizerAlways=defaults.EqualizerAlways;s.EqualizerBars=defaults.EqualizerBars;s.EqualizerMode=defaults.EqualizerMode;s.EqualizerColor=defaults.EqualizerColor;s.EqualizerCustomColor=defaults.EqualizerCustomColor;
                    s.MusicText=defaults.MusicText;s.MusicEqualizerBars=defaults.MusicEqualizerBars;s.MusicTrackNotice=defaults.MusicTrackNotice;s.MusicPausedHide=defaults.MusicPausedHide;s.MusicInstrumentalTitle=defaults.MusicInstrumentalTitle;s.MusicCompactCover=defaults.MusicCompactCover;s.MusicPageCover=defaults.MusicPageCover;s.LyricsEnabled=defaults.LyricsEnabled;
                    s.MusicLyricColor=defaults.MusicLyricColor;s.MusicLyricCustomColor=defaults.MusicLyricCustomColor;
                    s.NeteaseFullControlEntry=defaults.NeteaseFullControlEntry;s.NeteaseAutoFullControl=defaults.NeteaseAutoFullControl;s.AppleMusicFullControl=defaults.AppleMusicFullControl;s.KugouFullControl=defaults.KugouFullControl;break;
                // The default theme, with its own colors and suggested clock, the clock reset as the collapsed page resets it; the
                // other themes keep what was saved for them.
                case ThemePage:{s.PanelTheme=PanelThemes.NormalizeId(defaults.PanelTheme);var palette=PanelThemes.Resolve(PanelTheme.Find(s.PanelTheme));
                    s.ClockStyle=defaults.ClockStyle;s.ClockColor=defaults.ClockColor;s.ClockCustomColor=defaults.ClockCustomColor;s.ClockGradient=[.. palette.ClockGradients[0].Stops];
                    PanelThemes.ApplyMatching(palette,s);break;}
            }
        });
        if(_page==0)RebuildTheme();else ShowPage(_page);
    }

    // A page that is not shown cannot be previewed, so its sizes are only saved.
    private void Resize(Action<DeckSettings> edit, DeckPage page = DeckPage.Usage)
    { bool shown = DeckPages.Shown(S, page); Change(edit); if (!shown) return; _preview = true; _previewPage = page; _app.Deck.Preview(true); _app.Deck.SelectPage(page, remember: false); }

    private void Interaction()
    {
        SectionTitle(Loc.T("悬停与动画", "Hover and animation"));
        var duration = SliderRow(Loc.T("展开与收起动画时长", "Animation duration"), S.AnimationDuration, 100, 1000, 20, Loc.T("毫秒", "ms"), value => Change(s => s.AnimationDuration = (int)value));
        void EnableDuration(bool enabled)
        {
            foreach (var control in duration.Children.OfType<FrameworkElement>().Where(control => Grid.GetColumn(control) > 0)) control.IsEnabled = enabled;
            // The whole row reads as off (SW-12): the title and the number box fade as the slider does (its own 0.35).
            foreach (var part in duration.Children.OfType<FrameworkElement>().Where(control => control is not Slider)) part.Opacity = enabled ? 1 : .4;
        }
        EnableDuration(S.Animations);
        Group(SliderRow(Loc.T("展开延迟", "Expand delay"), S.OpenDelay, 0, 2000, 50, Loc.T("毫秒", "ms"), value => Change(s => s.OpenDelay = (int)value),
                Loc.T("鼠标悬停在 12 island 的收起面板或指示条上多久后，开始展开。0 毫秒表示立即开始展开。",
                    "How long the pointer rests on 12 island's collapsed panel or indicator before it starts to expand. 0 ms expands immediately.")),
            SliderRow(Loc.T("收起延迟", "Collapse delay"), S.CloseDelay, 0, 2000, 50, Loc.T("毫秒", "ms"), value => Change(s => s.CloseDelay = (int)value),
                Loc.T("鼠标离开 12 island 的展开面板范围多久后，开始收起。0 毫秒表示立即开始收起。",
                    "How long after the pointer leaves 12 island's expanded panel it starts to collapse. 0 ms collapses immediately.")),
            Toggle(Loc.T("启用展开与收起动画", "Animate expanding and collapsing"), S.Animations, value => { Change(s => s.Animations = value); EnableDuration(value); }), duration);
    }

    private void WindowRules()
    {
        SectionTitle(Loc.T("窗口行为", "Window behavior"));
        Group(BehaviorRow(Loc.T("有窗口最大化时", "When a window is maximized"), S.Maximized, value => Change(s => s.Maximized = value)),
            BehaviorRow(Loc.T("有应用处于无边框全屏时", "When an app is in borderless full screen"), S.Borderless, value => Change(s => s.Borderless = value)),
            BehaviorRow(Loc.T("有应用处于独占全屏时", "When an app is in exclusive full screen"), S.Exclusive, value => Change(s => s.Exclusive = value)),
            Toggle(Loc.T("同时检测后方窗口", "Also check windows behind"), S.BackgroundWindows, value => Change(s => s.BackgroundWindows = value),
                help: Loc.T("开启后，正在使用的窗口后方有窗口最大化或无边框全屏时，也按上面对应的规则处理。关闭后只检测正在使用的窗口。",
                    "When on, a maximized or borderless full-screen window behind the window you are using also follows the matching rule above. When off, only the window you are using is checked.")),
            ExceptionsRow());
        SectionTitle(Loc.T("兼容性", "Compatibility"));
        var raise = ActionButton(Loc.T("立即置顶", "Raise now"), () =>
        {
            foreach (var deck in _app.Decks.ToList()) Native.WindowsHost.RaiseOnce(deck);
        });
        raise.BorderBrush = UI.Brush(_palette.Border);
        raise.Background = UI.Brush(_palette.Input);
        Group(Toggle(Loc.T("持续置顶", "Keep on top"), S.ForceTopmost,
            value => Change(s => s.ForceTopmost = value), help:
            Loc.T("开启后，面板被其他置顶窗口遮挡时会立即恢复到上方，其他应用打开的菜单除外。仍遵循上面的显示与隐藏规则。若其他应用反复置顶自己，面板会暂停恢复几秒。",
                "Restores the panel above other always-on-top windows as soon as it is covered, except for menus other apps open. Display and hide rules above still apply. If an app keeps raising itself, the panel pauses for a few seconds.")),
            Row(Loc.T("单次置顶", "Raise once"), raise, help: () => Loc.T("每次点击仅置顶一次。若面板再次被其他窗口遮挡，需要重新点击。",
                "Each click raises the panel once. If another window covers it again, click again.")));
    }

    // Each row independently chooses its quota source, statistics source and dashboard name.
    private void Pages()
    {
        SectionTitle(Loc.T("提示", "Notifications"));
        Group(Toggle(Loc.T("预警和告警提示", "Warning and critical notices"), S.QuotaAlerts, v => Change(s => s.QuotaAlerts = v), help:
            Loc.T($"开启后，某个应用的已用比例升到预警（{AppPresets.WarningPercent:0}%）或告警（{AppPresets.CriticalPercent:0}%）时，收起的刘海或胶囊会短暂显示该应用的名称、已用比例和重置时间，约 5 秒后恢复。",
                $"When on, and an app's used share rises to the warning ({AppPresets.WarningPercent:0}%) or critical ({AppPresets.CriticalPercent:0}%) level, the collapsed notch or capsule briefly shows the app's name, used share and reset time, then returns after about 5 seconds.")));
        SectionTitle(Loc.T("数据来源", "Data sources"));
        Group(Toggle(Loc.T("同步 WSL 中的用量统计", "Include usage from WSL"), S.IncludeWsl, v => Change(s => s.IncludeWsl = v), help:
            Loc.T("开启后，会读取已安装的 WSL 子系统中 Claude Code 和 Codex CLI 的会话记录，并计入 Token 用量与费用统计。读取时会唤醒尚未运行的 WSL 子系统，默认关闭。",
                "When on, the Claude Code and Codex CLI session records inside installed WSL distributions are read and counted in token usage and cost. Reading wakes a distribution that is not running, so this is off by default.")));
        SectionTitle(Loc.T("应用", "Apps"));
        _appRows = new StackPanel();
        foreach (var entry in S.Apps) _appRows.Children.Add(AppRow(entry));
        UpdateDividers();
        var list = new StackPanel();
        var headings = AppColumns(); headings.Height = 34;
        AddHeadings(headings, new[]
        {
            (Loc.T("配额来源", "Quota source"), 1, Loc.T("决定本列额度、余额、进度条和重置时间的数据来源。", "Where this column's quota, balance, progress bar and reset time come from.")),
            (Loc.T("统计应用", "Usage app"), 2, Loc.T("决定本列 Token 用量、费用统计及模型明细来自哪个应用。", "The app whose token usage, cost and model details this column shows.")),
            (Loc.T("显示名称", "Display name"), 3, Loc.T("显示在看板上的名称。默认使用配额来源名称。", "The name shown on the panel. By default, the quota source's name.")),
            (Loc.T("主题", "Theme"), 4, Loc.T("本列进度条和配额环的颜色。配额环始终使用这个颜色。", "The color of this column's progress bars and quota ring. The quota ring always uses this color.")),
            (Loc.T("预警", "Warning"), 5, Loc.T("已用达到 70% 时，进度条和数字改用这个颜色。", "At 70% used, progress bars and numbers change to this color.")),
            (Loc.T("告警", "Critical"), 6, Loc.T("已用达到 90% 时，进度条和数字改用这个颜色。", "At 90% used, progress bars and numbers change to this color.")),
            (Loc.T("显示", "Show"), 7, "")
        });
        list.Children.Add(headings);
        list.Children.Add(new Border { Child = _appRows, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 1) });
        bool atLimit = S.Apps.Count >= AppPresets.MaximumApps;
        var add = ActionButton("", () =>
        {
            if (S.Apps.Count >= AppPresets.MaximumApps) return;
            Change(s => s.Apps.Add(new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Claude }));
            ShowPage(3);
        });
        add.IsEnabled = !atLimit; add.Opacity = atLimit ? .45 : 1;
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        addContent.Children.Add(Stroke("M 5,0 L 5,10 M 0,5 L 10,5", 10, 10, TextSecondary));
        var addLabel = TextLine(Loc.T("添加应用", "Add app"), 12.5, TextSecondary); addLabel.Margin = new Thickness(8, 0, 0, 0); addLabel.VerticalAlignment = VerticalAlignment.Center; addContent.Children.Add(addLabel);
        add.Content = addContent; add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(10, 6, 0, 6); add.Padding = new Thickness(10, 6, 12, 6);
        add.ToolTip = atLimit ? Loc.T($"最多配置 {AppPresets.MaximumApps} 个应用；移除一行后可以继续添加", $"Up to {AppPresets.MaximumApps} apps. Remove a row to add another.")
            : Loc.T($"最多配置 {AppPresets.MaximumApps} 个应用；同一配额来源或统计应用可以重复选择", $"Up to {AppPresets.MaximumApps} apps. The same quota source or usage app can be chosen more than once.");
        ToolTipService.SetShowOnDisabled(add, true);
        AutomationProperties.SetName(add, Loc.T("添加应用", "Add app"));
        list.Children.Add(add);
        _content.Children.Add(list);
    }

    // Column headings, each with its help mark when it has a note, as the app list has them.
    private void AddHeadings(Grid headings, IEnumerable<(string Title, int Column, string Help)> columns)
    {
        foreach (var (title, column, help) in columns)
        {
            var label = TextLine(title, 11, TextSecondary);
            FrameworkElement heading = label;
            if (help.Length > 0)
            {
                // The mark sits close to its own label so that, in the narrow color columns, each label and mark read as one pair.
                // The 24 DIP pointer box of SmallHelp keeps its negative margin, so the mark sits 2.5 DIP after the label.
                var info = SmallHelp(title, () => help); info.Margin = new Thickness(2.5 - 6, -6, -6, -6);
                heading = new HelpHeading(label, info);
            }
            heading.VerticalAlignment = VerticalAlignment.Center; heading.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(heading, column); headings.Children.Add(heading);
        }
    }

    // The color columns fit their headings and help marks: "Warning" and "Critical" need more room than "预警" and "告警".
    private static GridLength SwatchColumn => new(Loc.IsEnglish ? 60 : 50);

    private static Grid AppColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        var swatch = SwatchColumn;
        foreach (var width in new[] { new GridLength(40), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
            new GridLength(1.12, GridUnitType.Star), swatch, swatch, swatch, new GridLength(50), new GridLength(32) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    private Border AppRow(AppEntry entry)
    {
        var row = AppColumns();
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        string name = entry.Name;
        var quota = SourceSelector(entry, true); Grid.SetColumn(quota, 1); row.Children.Add(quota);
        var usage = SourceSelector(entry, false); Grid.SetColumn(usage, 2); row.Children.Add(usage);
        var nameField = AppNameField(entry);
        Grid.SetColumn(nameField, 3); row.Children.Add(nameField);

        // An unchosen color shows the panel theme's default for this column, which is what the panel draws.
        var theme = ColorSwatch(name, Loc.T("主题", "theme"), entry.ThemeColor.Length > 0 ? entry.ThemeColor : PanelTheme.AppDefault(entry.QuotaSource), entry.ThemeColor.Length == 0, value => Change(s => s.Entry(entry.InstanceId)!.ThemeColor = value), owner: entry.InstanceId);
        var warning = ColorSwatch(name, Loc.T("预警", "warning"), entry.WarningColor.Length > 0 ? entry.WarningColor : PanelTheme.WarningDefault, entry.WarningColor.Length == 0, value => Change(s => s.Entry(entry.InstanceId)!.WarningColor = value), AppPresets.WarningPercent, entry.InstanceId);
        var critical = ColorSwatch(name, Loc.T("告警", "critical"), entry.CriticalColor.Length > 0 ? entry.CriticalColor : PanelTheme.CriticalDefault, entry.CriticalColor.Length == 0, value => Change(s => s.Entry(entry.InstanceId)!.CriticalColor = value), AppPresets.CriticalPercent, entry.InstanceId);
        Grid.SetColumn(theme, 4); row.Children.Add(theme); Grid.SetColumn(warning, 5); row.Children.Add(warning); Grid.SetColumn(critical, 6); row.Children.Add(critical);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.Enabled, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, Loc.T("显示 ", "Show ") + name); AutomationProperties.SetAutomationId(toggle, "enabled-" + entry.InstanceId.ToString("N"));
        toggle.Checked += (_, _) => Change(s => s.Entry(entry.InstanceId)!.Enabled = true); toggle.Unchecked += (_, _) => Change(s => s.Entry(entry.InstanceId)!.Enabled = false);
        Grid.SetColumn(toggle, 7); row.Children.Add(toggle);
        AttachDrag(handle, name);
        // Removing drops the app's colors and sources with it, so it asks first (SW-09).
        var remove = ActionButton("", () =>
        {
            if (!Confirm(Loc.T("移除应用", "Remove app"), Loc.T($"将从看板移除“{name}”，连同它的颜色和来源设置。", $"This removes {name} from the panel, with its colors and sources."), Loc.T("移除", "Remove"))) return;
            Change(s => s.Apps.RemoveAll(app => app.InstanceId == entry.InstanceId));
            ShowPage(3);
        });
        var cross = Stroke("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, TextSecondary);
        cross.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        remove.Style = (Style)FindResource("SettingsGlyphButton"); remove.Content = cross; remove.Width = 24; remove.Height = 26; remove.Padding = new Thickness(0);
        AutomationProperties.SetName(remove, Loc.T("移除 ", "Remove ") + name); AutomationProperties.SetAutomationId(remove, "remove-" + entry.InstanceId.ToString("N"));
        // At the column's right edge, at least 12 DIP from the switch beside it.
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(remove, 8); row.Children.Add(remove);
        var border = new Border { Child = row, Height = AppRowHeight, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = entry,
            RenderTransform = new TranslateTransform() };
        AttachSourceDetails(entry, row, border);
        return border;
    }

    private Button SourceSelector(AppEntry entry, bool quota)
    {
        string role = quota ? Loc.T("配额来源", "Quota source") : Loc.T("统计应用", "Usage app");
        ProviderId selected = quota ? entry.QuotaSource : entry.UsageSource;
        string Label(ProviderId id) => AppPresets.Name(id);
        var select = new DropDownButton { Style = (Style)FindResource("SettingsButton"), Value = () => Label(selected) };
        var selectContent = new DockPanel();
        var chevron = Chevron(); chevron.Foreground = UI.Brush(TextSecondary); chevron.Margin = new Thickness(8, ChevronDrop.Top, 0, ChevronDrop.Bottom); DockPanel.SetDock(chevron, Dock.Right); selectContent.Children.Add(chevron);
        var selectLabel = TextLine(Label(selected), 12.5, TextPrimary, FontWeights.SemiBold);
        selectLabel.VerticalAlignment = VerticalAlignment.Center; selectLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        selectContent.Children.Add(selectLabel);
        select.Content = selectContent; select.Height = 34;
        select.ToolTip = quota ? ProviderCatalog.QuotaTip(selected) : ProviderCatalog.UsageTip(selected);
        select.HorizontalContentAlignment = HorizontalAlignment.Stretch; select.VerticalAlignment = VerticalAlignment.Center;
        select.Padding = new Thickness(9, 0, 9, 0); select.Margin = new Thickness(6, 0, 6, 0);
        select.Background = UI.Brush(_palette.Input); select.BorderBrush = UI.Brush(_palette.InputBorder);
        AutomationProperties.SetName(select, role + " " + entry.Name);
        AutomationProperties.SetAutomationId(select, (quota ? "quota-" : "usage-") + entry.InstanceId.ToString("N"));
        // The application menu is a styled popup list: dark surface, rounded corners, no icon column.
        var menu = new Popup { PlacementTarget = select, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var items = new StackPanel { Width = 208 };
        Button? current = null;
        string? group = null;
        foreach (var id in quota ? ProviderCatalog.Sources : ProviderCatalog.UsageSources)
        {
            if (quota && ProviderCatalog.Group(id) != group)
            {
                group = ProviderCatalog.Group(id);
                if (items.Children.Count > 0) items.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(4, 5, 4, 3) });
                var header = TextLine(group, 10.5, TextTertiary); header.Margin = new Thickness(10, 4, 10, 3); items.Children.Add(header);
            }
            var item = MenuEntry(Label(id), "", true, id == selected);
            // Each option carries the same note as the selector shows once it is chosen.
            item.ToolTip = quota ? ProviderCatalog.QuotaTip(id) : ProviderCatalog.UsageTip(id);
            if (id == selected) current = item;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                PopupMotion.AfterClose(Choose);
            };
            void Choose()
            {
                if (id != selected) Change(s =>
                {
                    var current = s.Entry(entry.InstanceId)!;
                    if (quota)
                    {
                        current.QuotaSource = id; current.Site = ""; current.Script = id == ProviderId.Custom ? ProviderScripts.Example : ""; current.SecretRevision = Guid.Empty;
                    }
                    else current.UsageSource = id;
                });
                if (quota && !ProviderCatalog.IsBuiltIn(id)) { CollapseSourceRows(); _expandedSource = entry.InstanceId; }
                RefreshAppRow(entry.InstanceId);
            }
            AutomationProperties.SetName(item, Loc.T("选择 ", "Select ") + Label(id)); items.Children.Add(item);
        }
        // The list grows as sources are added; past the room on screen it scrolls instead of running off the screen.
        var list = new ScrollViewer
        {
            Style = (Style)FindResource("SettingsScrollViewer"), Content = items, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var surface = new Border
        {
            Child = list, Background = UI.Brush(_palette.Popup), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(5)
        };
        menu.Child = surface;
        // A long list opens at the selected source.
        menu.Opened += (_, _) =>
        {
            list.ScrollToTop();
            if (current is { } chosen) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => chosen.BringIntoView());
        };
        select.Tag = menu;
        select.Menu = menu;
        // At most MenuRows sources show at once, with the next one cut through the middle so the list reads as one that scrolls.
        // Headers and the divider above them count toward the height but not the rows. A shorter list shows whole.
        double MenuLimit()
        {
            items.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double height = surface.Padding.Top + surface.Padding.Bottom + surface.BorderThickness.Top + surface.BorderThickness.Bottom;
            int shown = 0;
            foreach (UIElement row in items.Children)
            {
                bool source = row is Button;
                if (source && shown == MenuRows) return height + row.DesiredSize.Height / 2;
                height += row.DesiredSize.Height;
                if (source) shown++;
            }
            return double.PositiveInfinity;
        }
        select.Click += (_, _) => { if (!menu.IsOpen) FitMenuToScreen(select, menu, surface, MenuLimit()); };
        TogglePopup(select, menu);
        return select;
    }
    private const int MenuRows = 10;

    // Opens the menu on the side of the button that has room for it inside the monitor's work area, so it never covers
    // the taskbar, and limits its height to that room. The room depends on the screen resolution, the Windows scale and
    // where the button is, so it is measured again each time the menu opens. limit caps the height even where there is more room.
    private static void FitMenuToScreen(FrameworkElement anchor, Popup menu, FrameworkElement surface, double limit = double.PositiveInfinity)
    {
        const double Gap = 4, Edge = 8;
        if (PresentationSource.FromVisual(anchor)?.CompositionTarget is not { } target) return;
        var fromDevice = target.TransformFromDevice;
        var top = anchor.PointToScreen(new Point(0, 0));
        var bottom = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var work = Native.WindowsHost.WorkAreaAt(new Point((top.X + bottom.X) / 2, (top.Y + bottom.Y) / 2));
        double below = fromDevice.Transform(new Vector(0, work.Bottom - bottom.Y)).Y - Gap - Edge;
        double above = fromDevice.Transform(new Vector(0, top.Y - work.Top)).Y - Gap - Edge;
        surface.MaxHeight = double.PositiveInfinity;
        surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool down = Math.Min(surface.DesiredSize.Height, limit) <= below || below >= above;
        menu.Placement = down ? PlacementMode.Bottom : PlacementMode.Top;
        menu.VerticalOffset = down ? Gap : -Gap;
        surface.MaxHeight = Math.Max(0, Math.Min(limit, down ? below : above));
    }

    // A popup that closes when the mouse goes down outside it also closes when its own button is pressed.
    // While the popup is open the button ignores the mouse, so that press only closes the popup and does not open it again.
    private static void TogglePopup(ButtonBase button, Popup popup)
    {
        popup.Opened += (_, _) => button.IsHitTestVisible = false;
        PopupMotion.Attach(popup);
        popup.Closed += (_, _) => button.IsHitTestVisible = true;
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
    }

    // One line of the application menu: the name at the left, an optional note at the right.
    private Button MenuEntry(string name, string note, bool enabled, bool current)
    {
        var content = new DockPanel();
        if (note.Length > 0)
        {
            var hint = TextLine(note, 10.5, TextTertiary); hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(hint, Dock.Right); content.Children.Add(hint);
        }
        var label = TextLine(name, 12.5, enabled ? TextPrimary : TextSecondary, current ? FontWeights.SemiBold : FontWeights.Normal); label.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(label);
        var item = ActionButton("", () => { }); item.Content = content; item.IsEnabled = enabled;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch; item.Padding = new Thickness(9, 6, 9, 6); item.Margin = new Thickness(0, 1, 0, 1);
        if (current) item.Background = UI.Brush(Inset);
        return item;
    }

    private Button ColorSwatch(string app, string label, string color, bool isDefault, Action<string> apply, double? threshold = null, Guid? owner = null)
    {
        // The swatch shows the color as the panel draws it (see PanelTheme.Readable); the picker starts from the color as chosen.
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(PanelTheme.Readable(color)) };
        // The column heading explains each color; the swatch itself carries no hover note.
        AutomationProperties.SetName(button, Loc.T($"{app} {label}颜色", $"{app} {label} color")); AutomationProperties.SetHelpText(button, color + (isDefault ? Loc.T("（默认）", " (default)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(color, isDefault, value => { popup.IsOpen = false; PopupMotion.AfterClose(() => { apply(value); if (owner is { } id) RefreshAppRow(id); else ShowPage(3); }); });
        TogglePopup(button, popup);
        return button;
    }

    // A saturation/value square with a hue strip, preset swatches and a hexadecimal field. Dragging or typing changes one pending
    // color shown by every part without closing the popup; "确定" or Enter applies it. A swatch applies its color at once, "默认"
    // restores the preset value, and a click outside closes the popup without changes.
    // choose receives the new color, or "" for the default; the caller closes the popup.
    private Border ColorPicker(string current, bool isDefault, Action<string> choose, string? resetLabel = null)
    {
        const double width = 224;
        static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
        static string Hex(Color color) => ColorMath.ToHex(color.R, color.G, color.B);
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        string pending = AppPresets.NormalizeColor(current) is { Length: > 0 } valid ? valid : "#FFFFFF";
        var panel = new StackPanel { Width = width };
        var area = new HsvColorPicker(width, 132, _palette.Border) { Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(area, Loc.T("颜色选择器", "Color picker"));
        panel.Children.Add(area);
        var palette = new UniformGrid { Columns = 6 };
        panel.Children.Add(palette);

        var entry = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var ok = ActionButton(Loc.T("确定", "OK"), () => { }, primary: true); ok.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(ok, Dock.Right);
        var reset = ActionButton(resetLabel ?? Loc.T("默认", "Default"), () => choose("")); reset.IsEnabled = !isDefault; reset.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(reset, Dock.Right);
        entry.Children.Add(ok); entry.Children.Add(reset);
        var dot = new Ellipse { Width = 20, Height = 20, Stroke = UI.Brush(_palette.Border), StrokeThickness = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left); entry.Children.Add(dot);
        var field = new StackPanel { Orientation = Orientation.Horizontal };
        var hash = TextLine("#", 12, TextSecondary); hash.VerticalAlignment = VerticalAlignment.Center; hash.Margin = new Thickness(0, 0, 4, 0); field.Children.Add(hash);
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), Width = 60, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, MaxLength = 6 };
        AutomationProperties.SetName(input, Loc.T("颜色值", "Color value")); field.Children.Add(input);
        var chip = new Border { Child = field, Height = 28, CornerRadius = new CornerRadius(6), Background = UI.Brush(Inset), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(9, 0, 9, 0) };
        FieldFocus(chip, input, () => Brushes.Transparent);
        entry.Children.Add(chip); panel.Children.Add(entry);

        var rings = new List<(Ellipse Ring, string Color)>();
        bool typing = false;
        void Select(string color, object? source)
        {
            pending = color;
            if (source != area) area.SetColor(Parse(color));
            if (source != input) { typing = true; input.Text = color.TrimStart('#'); typing = false; }
            dot.Fill = UI.Brush(color);
            foreach (var (ring, preset) in rings) { bool selected = Same(preset, color); ring.Stroke = UI.Brush(selected ? TextPrimary : _palette.Border); ring.StrokeThickness = selected ? 2 : 1; }
            ok.IsEnabled = !Same(pending, current);
        }
        void Commit() { if (!Same(pending, current)) choose(pending); }
        // The current panel theme's colors, so a pick from here suits the panel it is shown on.
        foreach (var color in PanelTheme.Current.Swatches.Select(swatch => swatch.ToString()))
        {
            var swatch = ActionButton("", () => choose(color)); swatch.Padding = new Thickness(0); swatch.Width = 31; swatch.Height = 31;
            var ring = new Ellipse { Width = 20, Height = 20, Fill = UI.Brush(color) }; swatch.Content = ring; rings.Add((ring, color));
            swatch.ToolTip = color; AutomationProperties.SetName(swatch, Loc.T("颜色 ", "Color ") + color); palette.Children.Add(swatch);
        }
        area.Changed += color => Select(Hex(color), area);
        input.TextChanged += (_, _) => { if (!typing && input.Text.Length == 6 && AppPresets.NormalizeColor(input.Text) is { Length: > 0 } value) Select(value, input); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (AppPresets.NormalizeColor(input.Text) is { Length: > 0 } value) { Select(value, input); Commit(); }
            e.Handled = true;
        };
        ok.Click += (_, _) => Commit();
        Select(pending, null);
        var surface = new Border
        {
            Child = panel, Background = UI.Brush(_palette.Popup), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12)
        };
        return surface;
    }

    // Replaces one application row in place, so the rest of the page neither rebuilds nor flickers.
    private void RefreshAppRow(Guid id)
    {
        var old = _appRows?.Children.OfType<Border>().FirstOrDefault(b => b.Tag is AppEntry e && e.InstanceId == id);
        if (_appRows is null || old is null || S.Entry(id) is not { } entry) { ShowPage(3); return; }
        int index = _appRows.Children.IndexOf(old);
        _sourceRows.Remove(id);
        _appRows.Children.RemoveAt(index); _appRows.Children.Insert(index, AppRow(entry));
        UpdateDividers();
    }

    private void UpdateDividers() { if (_appRows is not null) UpdateDividers(_appRows); }
    private static void UpdateDividers(Panel rows)
    {
        for (int i = 0; i < rows.Children.Count; i++)
            if (rows.Children[i] is Border border) border.BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
    }

    private void About()
    {
        var description=TextLine(Loc.T("Windows 顶部悬浮面板。", "A floating panel at the top of the Windows screen."),13,TextSecondary);description.Margin=new Thickness(0,0,0,30);_content.Children.Add(description);
        SectionTitle(Loc.T("软件更新", "Software update"));
        _aboutUpdateView = CreateUpdateView(true); Group(_aboutUpdateView.Root);
        SectionTitle(Loc.T("项目信息", "Project"));
        // What's new sits beside the version number, where someone looking for this version's changes looks first.
        var version = new StackPanel { Orientation = Orientation.Horizontal };
        var current = TextLine(_app.Updates.CurrentVersion,12,TextSecondary); current.VerticalAlignment = VerticalAlignment.Center; version.Children.Add(current);
        var notes = ActionButton(Loc.T("更新内容", "What's new"), ShowReleaseNotes); notes.Style = (Style)FindResource("SettingsLink"); notes.Margin = new Thickness(14, 0, 0, 0);
        version.Children.Add(notes);
        Group(Row(Loc.T("作者", "Author"), TextLine("12", 12, TextSecondary)),
            Row(Loc.T("当前版本", "Current version"),version),
            Row(Loc.T("许可证", "License"), TextLine("MIT License", 12, TextSecondary)));
    }

    // External links are plain text, so their right edge lines up with the values in the rows above.
    private Button Link(string text, string url)
    {
        var link = ActionButton(text, () => UI.OpenUrl(url)); link.Style = (Style)FindResource("SettingsLink"); link.ToolTip = url; return link;
    }

    // Sections and groups are added to the page, or to the panel being collected while one is.
    private Panel? _target;
    private Panel Target => _target ?? _content;
    private StackPanel Collect(Action build)
    {
        var panel = new StackPanel(); var previous = _target; _target = panel;
        try { build(); } finally { _target = previous; }
        return panel;
    }

    private void SectionTitle(string title, FrameworkElement? action = null, Func<string>? help = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); action.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(action); }
        var label = TextLine(title, 11, TextSecondary); label.VerticalAlignment = VerticalAlignment.Center;
        if (help is null) row.Children.Add(label);
        else
        {
            // An info icon right after the title, as the settings rows carry theirs.
            row.Children.Add(new HelpHeading(label, HelpIcon(title, help, 13)) { HorizontalAlignment = HorizontalAlignment.Left });
        }
        Target.Children.Add(row);
    }

    private void Group(params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            panel.Children.Add(row);
        }
        Target.Children.Add(new Border { Child = panel, Background = Brushes.Transparent, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0,1,0,0), Margin = new Thickness(0, 0, 0, 28) });
    }

    // One line per setting: the label at the left, the control at the right, an optional description underneath the label.
    private Grid Row(string title, FrameworkElement control, TextBlock? subtitle = null, Func<string>? help = null)
    {
        var row = new Grid { MinHeight = 44, Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, subtitle is null ? 0 : 10, 12, subtitle is null ? 0 : 10) };
        labels.Children.Add(SettingLabel(title, help));
        if (subtitle is not null) labels.Children.Add(subtitle);
        row.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    // keepPreview marks the switch alone as a preview control, so a press on the rest of the row still ends the preview.
    private FrameworkElement Toggle(string title, bool value, Action<bool> changed, bool enabled = true, string? help = null, bool keepPreview = false)
    {
        var toggle = new CheckBox { Content = TextLine(title, 13, TextPrimary, FontWeights.SemiBold), Style = (Style)FindResource("SettingsToggle"), IsChecked = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        if (keepPreview) KeepPreview(toggle);
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        if (help is not null)
        {
            toggle.Content = null; toggle.Padding = new Thickness(0);
            return Row(title, toggle, help: () => help);
        }
        return new Border { Child = toggle, Padding = new Thickness(0), Height = 44 };
    }

    private Grid SliderRow(string title, double value, double min, double max, double tick, string unit, Action<double> changed, string? help = null)
    {
        var row=new Grid { Height=44 };
        row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(174)});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        row.Children.Add(SettingLabel(title, help is null ? null : () => help));
        var controls=NumericControls(title,value,min,max,tick,unit,changed);controls.Slider.Margin=new Thickness(0,0,18,0);Grid.SetColumn(controls.Slider,1);row.Children.Add(controls.Slider);Grid.SetColumn(controls.Number,2);controls.Number.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(controls.Number);
        return row;
    }

    private FrameworkElement MouseWheelRow()
    {
        var title = Loc.T("鼠标滚轮", "Mouse wheel");
        var options = new UniformGrid { Columns = 3, Width = 324 };
        foreach (var (action, label) in new[] { (MouseWheelAction.None, Loc.T("不做任何事", "Nothing")),
            (MouseWheelAction.Pages, Loc.T("切换页面", "Switch pages")), (MouseWheelAction.Volume, Loc.T("调整音量", "Volume")) })
        {
            var option = new RadioButton { Content = label, Style = (Style)FindResource("SettingsSegment"), GroupName = "MouseWheel", IsChecked = S.MouseWheel == action };
            AutomationProperties.SetName(option, title + " · " + label); option.Checked += (_, _) => Change(s => s.MouseWheel = action); options.Children.Add(option);
        }
        return Row(title, SegmentBorder(options), help: () => Loc.T("仅在展开的面板区域内滚动时生效；歌词、明细等会滚动的区域不受影响。",
            "Works only when the wheel is rolled over the expanded panel; areas that scroll, such as the lyrics and the details, are not affected."));
    }

    private FrameworkElement ExpandPlaceRow()
    {
        var title = Loc.T("展开位置", "Expand at");
        var options = new UniformGrid { Columns = 2, Width = 216 };
        foreach (var (atPointer, label) in new[] { (false, Loc.T("正中间", "Middle")), (true, Loc.T("跟随鼠标", "Pointer")) })
        {
            var option = new RadioButton { Content = label, Style = (Style)FindResource("SettingsSegment"), GroupName = "ExpandPlace", IsChecked = S.ExpandAtPointer == atPointer };
            AutomationProperties.SetName(option, title + " · " + label); option.Checked += (_, _) => Change(s => s.ExpandAtPointer = atPointer); options.Children.Add(option);
        }
        return Row(title, SegmentBorder(options), help: () => Loc.T("刘海或胶囊比展开面板更宽时，悬停展开的面板所在的位置。",
            "Where the panel opens when hovered, if the notch or capsule is wider than the expanded panel."));
    }

    private FrameworkElement BehaviorRow(string title, WindowBehavior selected, Action<WindowBehavior> changed)
    {
        var options = new UniformGrid { Columns = 3, Width = 324 };
        foreach (var behavior in Enum.GetValues<WindowBehavior>())
        {
            var option = new RadioButton { Content = BehaviorName(behavior), Style = (Style)FindResource("SettingsSegment"), GroupName = "Behavior" + title, IsChecked = selected == behavior };
            AutomationProperties.SetName(option, title + " · " + BehaviorName(behavior)); option.Checked += (_, _) => changed(behavior); options.Children.Add(option);
        }
        return Row(title, SegmentBorder(options));
    }
}
