using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

public partial class MainWindow
{
    internal void ShowModelDetails(bool details)
    {
        if (!IsUsagePage && Settings.UsagePage) SelectPage(DeckPage.Usage, remember: false);
        bool wanted = details && Settings.UsagePage;
        if (wanted != _details) _usageHandover = wanted ? Handover.Forward : Handover.Back;
        _details = wanted; RenderUsage();
    }

    private const double ColumnTopInset = 2, EdgeInset = 6;

    // Grand is for a panel dragged tall: the same page with larger figures and more air, so the room is used rather than left
    // empty above and below the quotas.
    private enum Tier { Grand, Roomy, Default, Dense, Compact, Tiny }
    internal sealed record SegmentItem(string Text, string Name, bool Active, Action Select, string? Tip = null);
    // Slots is the most quotas any page of this app shows, so switching pages keeps the same layout.
    private sealed record QuotaView(List<UsageMetric> Quotas, int Pages, int Page, int Slots);

    // Every size is a whole DIP so text stays crisp; the panel steps between tiers instead of scaling continuously.
    private sealed record Layout(Tier Tier, double Name, double Number, double Label, double Reset, double Foot, double Plan, double Segment,
        double Head, double LabelRow, double QuotaGap, double RowGap, double Track, double ColumnPad, double Toolbar, double FootTop, double FootBottom, double Bottom, bool Footer, bool UsedWord)
    {
        // Small panels prioritize quotas; the bottom inset keeps the final row off the edge.
        public static Layout For(Tier tier) => tier switch
        {
            Tier.Grand => new(tier, 20, 40, 15, 13, 14.5, 12.5, 12, 32, 20, 26, 8, 8, 24, 40, 10, 8, 20, true, true),
            Tier.Roomy => new(tier, 18, 30, 14, 12.5, 14, 12, 12, 28, 18, 16, 6, 6, 20, 40, 8, 7, 18, true, true),
            Tier.Default => new(tier, 15, 20, 12, 11, 12, 10.5, 10.5, 24, 16, 10, 4, 4, 14, 36, 8, 7, 16, true, true),
            Tier.Dense => new(tier, 14, 18, 11.5, 10.5, 11, 10.5, 10.5, 22, 15, 8, 3, 4, 10, 36, 6, 5, 14, true, true),
            Tier.Compact => new(tier, 13, 16, 11, 10, 11, 9.5, 9.5, 20, 13, 4, 2, 3, 10, 32, 0, 0, 12, false, true),
            _ => new(tier, 12, 15, 10.5, 10, 10, 9.5, 9.5, 18, 13, 4, 2, 3, 8, 28, 0, 0, 10, false, false)
        };
        // Totals row: a fixed caption column keeps the token figures of both rows on one vertical line.
        // Wide enough for "今日" and "七日", or for "Today" and "Week".
        public double CaptionWidth => Math.Ceiling(Foot * (Loc.IsEnglish ? 3.7 : 2.7));
        // A single column wider than 480 DIP lays its quotas side by side with larger numbers.
        public Layout Wide() => this with
        {
            Name = Math.Max(Name, 17), Number = Math.Max(Number, 30), Label = Math.Max(Label, 13), Reset = Math.Max(Reset, 12),
            Foot = Math.Max(Foot, 13), LabelRow = Math.Max(LabelRow, 18), RowGap = Math.Max(RowGap, 6)
        };
        public double NumberRow => Math.Ceiling(Number * 1.2);
        public double FootRow => Math.Ceiling(Foot * 1.5);
        public double QuotaHeight => LabelRow + RowGap + NumberRow + RowGap + Track;
        public double FooterHeight(bool inline) => Footer ? FootTop + 1 + FootBottom + (inline ? 1 : 2) * FootRow : 0;
        public double Required(int slots, bool inline) => Head + slots * QuotaHeight + Math.Max(0, slots - 1) * QuotaGap + FooterHeight(inline);
    }

    private static double ToolbarFor(double panelHeight) => panelHeight < 150 ? 28 : panelHeight >= 290 ? 40 : 36;

    // Until a layout is fitted (or without one: no apps, model details) the header uses the inset the content will most likely get.
    private PanelHeader UnfittedHeader(double panelHeight) =>
        PanelHeader.For(ToolbarFor(panelHeight), _details ? (panelHeight < 170 ? 14 : 20) : EdgeInset + Layout.For(panelHeight < 150 ? Tier.Tiny : Tier.Default).ColumnPad);

    // The header the usage page gives a page of this size with the current apps and data, as RenderUsage applies it. The music
    // page takes its controls from it (PanelHeader.WithControlsOf), so they stay in place when the page changes.
    private PanelHeader UsageHeader(double width, double height)
    {
        if (_details || !Settings.UsagePage || Settings.EnabledApps.Count == 0) return UnfittedHeader(height);
        var columns = DashboardUsage.Columns(Settings, Snapshots);
        if (columns.Count == 0) return UnfittedHeader(height);
        var fit = FitColumns(columns, width, height);
        return PanelHeader.For(fit.Layout.Toolbar, EdgeInset + fit.Layout.ColumnPad);
    }

    // The layout is fitted to everything the panel can show, not to the page on screen, so switching application pages or
    // quota pages never changes column widths, sizes or spacing. Each page contains at most two quotas, regardless of the
    // panel's height.
    private (Layout Layout, bool Inline, bool Wide, int Count, double ColumnWidth, List<QuotaView> Views) FitColumns(IReadOnlyList<DashboardColumn> columns, double width, double height)
    {
        int pageSize = Math.Max(1, (int)((width - 40) / AppPresets.MinimumColumnWidth));
        int count = Math.Min(columns.Count, pageSize);
        double columnWidth = (width - EdgeInset * 2) / count;
        var views = columns.Select(x => Quotas(x.Entry, x.Quota)).ToList();
        var (layout, inline, wide) = Fit(count, views.Max(view => view.Slots), columnWidth, height);
        return (layout, inline, wide, count, columnWidth, views);
    }

    private static (Layout Layout, bool Inline, bool Wide) Fit(int count, int slots, double columnWidth, double panelHeight)
    {
        bool wide = count == 1 && columnWidth >= 480 && slots <= 2;
        // Width limits the tier first; extra height only enlarges columns that are wide enough for it.
        var byWidth = columnWidth >= 220 ? Tier.Default : columnWidth >= 170 ? Tier.Dense : Tier.Compact;
        var start = panelHeight < 150 ? Tier.Tiny : panelHeight >= 350 && columnWidth >= 340 ? Tier.Grand : panelHeight >= 290 && columnWidth >= 260 ? Tier.Roomy : byWidth;
        for (var tier = start; tier <= Tier.Tiny; tier++)
        {
            var layout = Layout.For(tier); if (wide) layout = layout.Wide();
            // Totals sit side by side only when a column is wide enough for both groups at that tier's size.
            bool inline = columnWidth >= (tier == Tier.Grand ? 440 : tier == Tier.Roomy ? 400 : tier == Tier.Default ? 340 : 300) && count <= 2;
            double available = panelHeight - layout.Toolbar - layout.Bottom - ColumnTopInset;
            // Narrow columns pick a small tier for their width alone; they keep the totals whenever the height still has room.
            var candidates = panelHeight < 200 ? [layout with { Footer = false }]
                : layout.Footer ? new[] { layout } : [layout with { Footer = true, FootTop = 4, FootBottom = 3 }, layout];
            foreach (var candidate in candidates)
                if (candidate.Required(wide ? 1 : slots, inline) <= available) return (candidate, inline, wide);
        }
        return (Layout.For(Tier.Tiny), false, wide);
    }

    private static List<TokenEntry> Entries(ProviderSnapshot snapshot, int days)
    {
        var start = DateTime.Now.Date.AddDays(1 - days);
        return snapshot.Entries.Where(x => x.Time.LocalDateTime >= start).ToList();
    }

    // The cheap part of a render: clock, gear tooltip and the synchronization button. Run on every expansion.
    internal void RefreshStatus()
    {
        RenderSyncButton();
        RenderPageSwitch();
        ClockLabel.Text = DateTime.Now.ToString("HH:mm");
        // The gear shows a tooltip only while it carries the update notice.
        var updates = _app.Updates;
        SettingsButton.ToolTip = updates.NoticeVersion is { } version ? Loc.T($"新版本 {version} 可用", $"Version {version} is available")
            : updates.UpdatedFrom is not null ? Loc.T($"已更新到 {updates.CurrentVersion}，点击查看更新内容", $"Updated to {updates.CurrentVersion}. Click to see what's new") : null;
    }

    // How the next rebuild of the usage page takes over (PageHandover); the action that changes the page's form sets it.
    private Handover _usageHandover;
    // The form the usage page was last laid out in: its size tier and arrangement, model details or the empty page. A rebuild
    // that changes it while the page is on screen (a drag across a size step, an app added) morphs into the new form.
    private string _usageForm = "";
    // The column whose quota page was just flipped, which alone comes up from faint after the rebuild.
    private Guid? _flippedColumn;
    // What the quota columns were last built from: their form and the data, page and settings they show, not the panel's exact
    // size. As the monitor page is built once per layout step and stretches within it, a render with the same key keeps the
    // page instead of building it again: a drag that stays within a size step, a return from another page, the sync button.
    // The screen's and the panel's scales are part of it, as the title rows are placed on the screen's pixels (TitlePlaces).
    private UsageBuild? _usageBuilt;
    private PanelHeader _usageHeaderBuilt = PanelHeader.For(36, 20);
    private sealed record UsageBuild(string Form, object Snapshots, object Settings, int ApplicationPage, string QuotaPages, int Theme, string Language, long Minute, double Dpi, double Scale);

    private UsageBuild? ColumnsBuild()
    {
        if (_details || !Settings.UsagePage || Settings.EnabledApps.Count == 0) return null;
        var columns = DashboardUsage.Columns(Settings, Snapshots);
        if (columns.Count == 0) return null;
        var fit = FitColumns(columns, ContentCanvas.Width, ContentCanvas.Height);
        return new UsageBuild($"{fit.Layout.Tier}|{fit.Layout.Footer}|{fit.Inline}|{fit.Wide}|{fit.Count}", Snapshots, Settings, _applicationPage,
            string.Join(",", _quotaPages.OrderBy(page => page.Key).Select(page => $"{page.Key:N}:{page.Value}")), PanelTheme.Generation, Loc.Language,
            DateTime.Now.Ticks / TimeSpan.TicksPerMinute, VisualTreeHelper.GetDpi(this).DpiScaleY, _scale);
    }

    public void RenderUsage()
    {
        if (UsageContent is null) return;
        if (IsMusicPage) { RenderMusic(); return; }
        if (IsMonitorPage) { RenderMonitor(); return; }
        if (IsNotifyPage) { RenderNotify(); return; }
        if (IsClipboardPage) { RenderClipboard(); return; }
        if (IsConvertPage) { RenderConvert(); return; }
        // Only a page that was on screen hands over; one that arrives by the page switch or with the panel opening comes in whole.
        bool shown = UsageContent.IsVisible && _expanded && !_preview;
        ShowPageContent(DeckPage.Usage);
        if (!Settings.UsagePage) _details = false;
        RefreshStatus();
        if (_detailPopup is not null) { _flippedColumn = null; return; }
        var build = ColumnsBuild();
        if (build is not null && build == _usageBuilt && _usageHandover == Handover.None && UsageContent.Children.Count > 0)
        {
            // The page as it was built; another page may have taken the header meanwhile.
            ApplyHeader(_usageHeaderBuilt);
            ClockLabel.Visibility = Visibility.Visible; ModelToolbar.Visibility = Visibility.Collapsed;
            ApplicationPages.Visibility = ApplicationPages.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var motion = _usageHandover; _usageHandover = Handover.None;
        string form = _usageForm;
        _handover.Hold(UsageContent);
        RenderUsagePage();
        _usageBuilt = build; _usageHeaderBuilt = _header;
        if (motion == Handover.None && form.Length > 0 && form != _usageForm) motion = Handover.Morph;
        if (!shown) motion = Handover.None;
        _handover.Release(UsageContent, motion);
        if (_flippedColumn is { } flipped && shown && motion == Handover.None)
            foreach (var column in UsageContent.Children.OfType<Panel>().SelectMany(grid => grid.Children.OfType<Border>()).Where(border => Equals(border.Tag, flipped)))
                PageHandover.Brighten(column);
        _flippedColumn = null;
        // The application pager is left out: it is what was clicked, under the pointer, when application pages flip.
        foreach (var part in new UIElement[] { ModelToolbar, PageSwitch, ClockLabel }) PageHandover.Arrive(part, motion);
    }

    private void RenderUsagePage()
    {
        if (_modelScroll is { IsLoaded: true }) _modelScrollOffset = _modelScroll.VerticalOffset;
        _modelScroll = null;
        ModelToolbar.Children.Clear(); UsageContent.Children.Clear();
        ApplicationPages.Children.Clear(); ApplicationPages.Visibility = Visibility.Collapsed;
        double panelHeight = ContentCanvas.Height;
        ApplyHeader(UnfittedHeader(panelHeight));
        ClockLabel.Visibility = _details ? Visibility.Collapsed : Visibility.Visible;
        ModelToolbar.Visibility = _details ? Visibility.Visible : Visibility.Collapsed;

        // The compact rings may keep the data current while the AI usage page itself is switched off.
        IReadOnlyList<AppEntry> apps = Settings.UsagePage ? Settings.EnabledApps : [];
        IReadOnlyList<ProviderId> statistics = Settings.UsagePage ? Settings.StatisticsProviders : [];
        SynchronizeModelSelection(statistics);
        if (_details) RenderModelToolbar(statistics);
        if (apps.Count == 0)
        {
            _usageForm = "empty";
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(UI.Fixed(Settings.UsagePage ? Loc.T("未选择应用", "No apps selected") : Loc.T("AI 用量已关闭", "AI usage is off"), 14, 20, UI.Secondary));
            var openText = Loc.T("打开设置", "Open settings");
            var open = Segmented([new(openText, openText, false, _app.OpenSettings)], 12, 22, 12, centerText: true);
            open.Margin = new Thickness(0, 12, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(open); UsageContent.Children.Add(empty);
            return;
        }
        if (_details) { _usageForm = "details"; RenderModels(DashboardUsage.Statistics(Settings, Snapshots)); }
        else RenderColumns(DashboardUsage.Columns(Settings, Snapshots));
    }

    // A joined switch: one rounded strip, the selected segment filled. Buttons carry accessible names. With a glide key the fill
    // is one piece under the strip that glides to the chosen segment, as the monitor page's sort pill does; the strip is built
    // again on every choice, so where the fill was is kept by the key and the new strip's fill starts there.
    internal static Border Segmented(IReadOnlyList<SegmentItem> items, double fontSize, double lineHeight, double padX, bool centerText = false, string? glide = null)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            var text = centerText ? UI.Centered(item.Text, fontSize, UI.Secondary) : UI.Fixed(item.Text, fontSize, lineHeight, UI.Secondary);
            text.ClearValue(TextBlock.ForegroundProperty); text.ClearValue(TextBlock.FontWeightProperty);
            var button = new Button { Style = (Style)Application.Current.FindResource("SegmentButton"), Content = text, Height = lineHeight, FontSize = fontSize,
                Padding = new Thickness(padX, 0, padX, 0), Tag = item.Active ? glide is null ? "Selected" : "Chosen" : null, ToolTip = item.Tip };
            button.Click += (_, _) =>
            {
                // The strip that replaces this one glides from here (see below).
                if (glide is not null && SegmentFills.TryGetValue(glide, out var at)) SegmentFills[glide] = at with { At = Environment.TickCount64 };
                item.Select();
            };
            AutomationProperties.SetName(button, item.Name);
            strip.Children.Add(button);
        }
        if (glide is null) return new Border { Child = strip, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(1) };
        var shift = new TranslateTransform();
        var fill = new Border { Background = UI.Brush(UI.FillSelected), CornerRadius = new CornerRadius(PanelTheme.Corner(5)), HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false, RenderTransform = shift, Visibility = Visibility.Hidden };
        var host = new Grid { Children = { fill, strip } };
        int chosen = items.ToList().FindIndex(item => item.Active);
        strip.Loaded += (_, _) =>
        {
            if (chosen < 0 || chosen >= strip.Children.Count || strip.Children[chosen] is not FrameworkElement button || button.ActualWidth <= 0) return;
            double x = button.TranslatePoint(new Point(), host).X, width = button.ActualWidth;
            fill.Height = button.ActualHeight; fill.Visibility = Visibility.Visible;
            // Only a strip that replaces one just chosen in glides; one shown afresh (model details opened again) starts in place.
            bool slide = SegmentFills.TryGetValue(glide, out var was) && Environment.TickCount64 - was.At < 1500 && Soft.Enabled && strip.IsVisible && Math.Abs(was.X - x) > .5;
            SegmentFills[glide] = (x, width, 0);
            shift.X = x; fill.Width = width;
            if (!slide) return;
            shift.BeginAnimation(TranslateTransform.XProperty, Soft.Fade(was.X, x, Soft.Choose, Soft.Follow));
            fill.BeginAnimation(FrameworkElement.WidthProperty, Soft.Fade(was.Width, width, Soft.Choose, Soft.Follow));
        };
        return new Border { Child = host, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(1) };
    }
    // Where each gliding strip's fill was last, by its key.
    private static readonly Dictionary<string, (double X, double Width, long At)> SegmentFills = [];

    private QuotaView Quotas(AppEntry entry, ProviderSnapshot snapshot)
    {
        const int capacity = 2;
        var quotas = snapshot.Metrics.OrderBy(x => x.Window is > 0 ? x.Window.Value : int.MaxValue).ToList();
        // Antigravity labels its quotas "Gemini · 5 小时", "Claude · 每周" and so on. Each group starts its own page, Gemini first,
        // so the first page shows rows 1 and 2 (Gemini) and a click on the column shows rows 3 and 4 (Claude), the same rows and
        // click-to-page as any other app with more than two quotas. Labels without a group come last.
        // Claude groups by organization when an account reads several ("Pro · 5 小时额度", "Team · 每周额度"): each organization
        // starts its own page, in the order the service gave them, so Pro's 5-hour and weekly quotas come first, then its Fable
        // quota, then Team's.
        string Group(UsageMetric metric) => UI.QuotaGroup(new Quota(metric.Label, 0, null));
        var order = snapshot.Metrics.Select(Group).Distinct().ToList();
        var sets = snapshot.Id switch
        {
            ProviderId.Antigravity => quotas.GroupBy(Group).OrderBy(x => x.Key == "Gemini" ? 0 : x.Key.Length > 0 ? 1 : 2).Select(x => x.ToList()).ToList(),
            ProviderId.Claude => quotas.GroupBy(Group).OrderBy(x => order.IndexOf(x.Key)).Select(x => x.ToList()).ToList(),
            _ => [quotas]
        };
        var pages = sets.SelectMany(set => set.Chunk(capacity)).Select(x => x.ToList()).ToList();
        if (pages.Count == 0) pages.Add([]);
        // Sized for the fullest page the column can switch to.
        int slots = Awaiting(snapshot) ? capacity : Math.Clamp(pages.Max(x => x.Count), 1, capacity);
        int page = Math.Clamp(_quotaPages.GetValueOrDefault(entry.InstanceId), 0, pages.Count - 1);
        return new QuotaView(pages[page], pages.Count, page, slots);
    }

    private void RenderColumns(IReadOnlyList<DashboardColumn> columns)
    {
        int pageSize = Math.Max(1, (int)((ContentCanvas.Width - 40) / AppPresets.MinimumColumnWidth));
        int pages = Math.Max(1, (columns.Count + pageSize - 1) / pageSize);
        _applicationPage = Math.Clamp(_applicationPage, 0, pages - 1);
        if (pages > 1)
        {
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            Button PageButton(string label, string name, int step)
            {
                var button = ModelAction(label, name, () => { _applicationPage += step; _usageHandover = step > 0 ? Handover.Forward : Handover.Back; RenderUsage(); }, toolTip: false);
                button.IsEnabled = step < 0 ? _applicationPage > 0 : _applicationPage + 1 < pages;
                button.Padding = new Thickness(6, 0, 6, 0); return button;
            }
            controls.Children.Add(PageButton("‹", Loc.T("上一页应用", "Previous apps"), -1));
            var page = UI.Centered($"{_applicationPage + 1}/{pages}", 11, UI.Secondary); page.Margin = new Thickness(4, 0, 4, 0);
            page.ToolTip = Loc.T($"共 {columns.Count} 项应用", $"{columns.Count} apps in total"); controls.Children.Add(page);
            controls.Children.Add(PageButton("›", Loc.T("下一页应用", "Next apps"), 1));
            ApplicationPages.Children.Add(controls); ApplicationPages.Visibility = Visibility.Visible;
        }
        // See FitColumns. A short last page leaves its remaining columns empty.
        var (layout, inline, wide, count, columnWidth, views) = FitColumns(columns, ContentCanvas.Width, ContentCanvas.Height);
        _usageForm = $"{layout.Tier}|{layout.Footer}|{inline}|{wide}|{count}";
        int first = _applicationPage * pageSize;
        columns = columns.Skip(first).Take(pageSize).ToList(); views = views.Skip(first).Take(pageSize).ToList();
        // The quotas start EdgeInset + ColumnPad from the panel edge; the header lines up with them.
        ApplyHeader(PanelHeader.For(layout.Toolbar, EdgeInset + layout.ColumnPad));
        UsageContent.Margin = new Thickness(EdgeInset, 0, EdgeInset, layout.Bottom);
        // Spare height is always split evenly above and below the quotas, so resizing moves them smoothly
        // instead of pinning them under the title until a threshold is crossed.
        const bool center = true;
        var grid = new UniformGrid { Columns = count, Rows = 1 };
        for (int i = 0; i < columns.Count; i++)
            grid.Children.Add(Column(columns[i].Entry, columns[i].Quota, columns[i].Usage, views[i], layout, inline, wide, center, i > 0, columnWidth - layout.ColumnPad * 2 - (i > 0 ? 1 : 0)));
        UsageContent.Children.Add(grid);
    }

    // Before its first reading a quota has its defaults: nothing read, no time, and the label and the detail alike.
    // Only until the first read has ended, whatever it brought: a read that failed or found nothing shows its state, not the skeleton.
    // And only while a read is under way or will come by itself: with the automatic sync off nothing reads at the start, and the
    // skeleton would wait forever, so the column shows its state and the way to sync now.
    private bool ReadComing => (_lead ?? this)._refreshing || Settings.UsageAutoSync;
    private bool Awaiting(ProviderSnapshot snapshot) => !_firstReadEnded && ReadComing && snapshot.Metrics.Count == 0 && !snapshot.LiveQuota && snapshot.QuotaTime is null && snapshot.Status == snapshot.StatusLabel;

    private FrameworkElement Column(AppEntry entry, ProviderSnapshot snapshot, ProviderSnapshot usage, QuotaView view, Layout layout, bool inline, bool wide, bool center, bool separator, double innerWidth)
    {
        // Waiting for its first reading, the column shows its quotas' places in the same layout (SkeletonBlock), so the readings
        // fill in where they will stay instead of the whole column changing when they arrive.
        bool awaiting = Awaiting(snapshot);
        int blocks = awaiting ? view.Slots : view.Quotas.Count;
        FrameworkElement Block(int i) => awaiting ? SkeletonBlock(entry, layout, i) : QuotaBlock(entry, view.Quotas[i], layout, i);
        bool off = blocks == 0;
        // Only this column changes: it comes up from faint while the others stay as they are.
        void NextQuotaPage() { _quotaPages[entry.InstanceId] = (view.Page + 1) % view.Pages; _flippedColumn = entry.InstanceId; RenderUsage(); }
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Head) });
        column.RowDefinitions.Add(new RowDefinition());
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title row: the plan sits right after the name, and the name gives way first.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var (nameTop, chipTop, chipLift) = TitlePlaces(layout, VisualTreeHelper.GetDpi(this).DpiScaleY, _scale);
        var name = UI.Centered(entry.Name, layout.Name, off ? UI.Secondary : UI.Primary, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        name.VerticalAlignment = VerticalAlignment.Top; name.Margin = new Thickness(0, nameTop, 0, 0);
        name.ToolTip = QuotaTooltip(snapshot);
        AutomationProperties.SetAutomationId(name, "quota-title-" + entry.InstanceId.ToString("N"));
        head.Children.Add(name);
        double occupied = 0;
        if (snapshot.Plan.Length > 0)
        {
            var chip = PlanChip(snapshot, layout, chipTop, chipLift);
            Grid.SetColumn(chip, 1); head.Children.Add(chip);
            chip.Measure(new Size(double.PositiveInfinity, layout.Head)); occupied += chip.DesiredSize.Width;
        }
        name.MaxWidth = Math.Max(20, innerWidth - occupied);
        // The page stretches within its size step instead of being built again (RenderUsage), so the name gives way to the plan
        // as the column's width changes.
        head.SizeChanged += (_, e) => name.MaxWidth = Math.Max(20, e.NewSize.Width - occupied);
        column.Children.Add(head);

        FrameworkElement body;
        if (off)
        {
            // No readings: what the state is, why, and a way to sync now, centered in the column. Left-aligned, the button's text sat
            // inside its outline and padding, so it looked further right than the lines above it.
            var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = UI.Fixed(snapshot.StatusLabel, Math.Max(11, layout.Label + 1), Math.Ceiling(layout.Label * 1.5), UI.Secondary, FontWeights.SemiBold);
            label.TextAlignment = TextAlignment.Center; label.ToolTip = snapshot.Status;
            status.Children.Add(label);
            // The detail is dropped when it only repeats the label, and when the column is too short for it (the smallest panel):
            // the label and the button stay, the full text is in the label's tip.
            TextBlock? detail = null;
            if (snapshot.Status != snapshot.StatusLabel)
            {
                detail = UI.Text(snapshot.Status, Math.Max(10, layout.Reset), UI.Tertiary);
                detail.FontFamily = UI.PanelFont; detail.Margin = new Thickness(0, 3, 0, 0); detail.TextWrapping = TextWrapping.Wrap;
                detail.TextAlignment = TextAlignment.Center;
                status.Children.Add(detail);
            }
            var sync = new Button { Style = (Style)FindResource("PanelOutlineButton"), FontFamily = UI.TextFont, Padding = new Thickness(10, 2, 10, 2), Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Content = Loc.T("立即同步", "Sync now") };
            AutomationProperties.SetName(sync, Loc.T("立即同步用量", "Sync usage now"));
            sync.Click += (_, _) => _ = RefreshAsync();
            status.Children.Add(sync);
            var holder = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
            holder.Children.Add(status);
            if (detail is not null)
                holder.SizeChanged += (_, _) =>
                {
                    detail.Visibility = Visibility.Visible;
                    status.Measure(new Size(Math.Max(1, holder.ActualWidth), double.PositiveInfinity));
                    detail.Visibility = status.DesiredSize.Height > holder.ActualHeight + .5 ? Visibility.Collapsed : Visibility.Visible;
                };
            body = holder;
        }
        else if (wide)
        {
            // A page with fewer quotas keeps the width of a full page's quotas instead of widening them, and is centered.
            int cells = Math.Max(blocks, view.Slots);
            if (blocks < cells)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 0; i < blocks; i++)
                {
                    var block = Block(i);
                    block.Width = Math.Max(0, innerWidth / cells - 18);
                    row.Children.Add(block);
                }
                // As the column stretches, the blocks keep a full page's share of it.
                column.SizeChanged += (_, e) =>
                {
                    // The column lies inside its surface's padding already.
                    double width = Math.Max(0, e.NewSize.Width / cells - 18);
                    foreach (FrameworkElement block in row.Children) block.Width = width;
                };
                body = row;
            }
            else
            {
                var row = new UniformGrid { Rows = 1, Columns = cells, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 0; i < blocks; i++)
                {
                    var block = Block(i);
                    block.Margin = new Thickness(i > 0 ? 18 : 0, 0, i < cells - 1 ? 18 : 0, 0);
                    row.Children.Add(block);
                }
                body = row;
            }
        }
        else
        {
            // A page with fewer quotas is centered like a column that has only one quota; type sizes and spacing do not change.
            var stack = new StackPanel { VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top };
            for (int i = 0; i < blocks; i++)
            {
                var block = Block(i);
                if (i > 0) block.Margin = new Thickness(0, layout.QuotaGap, 0, 0);
                stack.Children.Add(block);
            }
            body = stack;
        }
        Grid.SetRow(body, 1); column.Children.Add(body);

        if (layout.Footer)
        {
            var footer = new StackPanel();
            footer.Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, layout.FootTop, 0, layout.FootBottom) });
            string todayLabel = Loc.T("今日", "Today"), weekLabel = Loc.T("七日", "Week");
            // Both captions take the wider one's width, so they start and end on the same vertical lines.
            double captionWidth = Math.Max(JustifiedText.NaturalWidth(todayLabel, layout.Foot), JustifiedText.NaturalWidth(weekLabel, layout.Foot));
            var today = TotalRow(entry, usage, todayLabel, 1, layout, captionWidth, inline); var week = TotalRow(entry, usage, weekLabel, 7, layout, captionWidth, inline);
            if (inline)
            {
                // Side by side, each period is one tight group: today at the left edge, the week at the right edge.
                var rows = new Grid();
                today.HorizontalAlignment = HorizontalAlignment.Left; week.HorizontalAlignment = HorizontalAlignment.Right;
                rows.Children.Add(today); rows.Children.Add(week); footer.Children.Add(rows);
            }
            else
            {
                var rows = new StackPanel(); rows.Children.Add(today); rows.Children.Add(week); footer.Children.Add(rows);
            }
            Grid.SetRow(footer, 2); column.Children.Add(footer);
        }
        var surface = new Border
        {
            Child = column, Padding = new Thickness(layout.ColumnPad, ColumnTopInset, layout.ColumnPad, 0),
            BorderThickness = new Thickness(separator ? 1 : 0, 0, 0, 0), BorderBrush = UI.Brush(UI.Hairline), Tag = entry.InstanceId
        };
        if (view.Pages > 1)
        {
            surface.Background = Brushes.Transparent; surface.Cursor = Cursors.Hand;
            AutomationProperties.SetHelpText(surface, Loc.T($"点击此列切换配额，第 {view.Page + 1} 页，共 {view.Pages} 页。", $"Click this column to switch quotas. Page {view.Page + 1} of {view.Pages}."));
            // Buttons handle their own mouse events, so statistics links act only once.
            surface.MouseLeftButtonUp += (_, e) => { e.Handled = true; NextQuotaPage(); };
        }
        return surface;
    }

    // Where the name and the plan chip go in the title row, from its top. Centered on their own line boxes and rounded to pixels
    // apart, the smaller plan ended up as much as two pixels above the name; instead the chip goes where its capitals' middle
    // comes closest to the name's as drawn. Positions fall on the layout's pixels (1 / dpi), text baselines on the screen's
    // (the panel's scale applied), and the border is a whole number of pixels thick. At 150 % a layout pixel is one and a half
    // screen pixels, too coarse to reach the name's line: the chip's text then also moves by whole screen pixels (Lift), which
    // keeps it sharp. At 100 % it never moves.
    private static (double Name, double Chip, double Lift) TitlePlaces(Layout layout, double dpi, double scale)
    {
        double pixel = 1 / dpi, screen = dpi * scale;
        double Snap(double value) => Math.Round(value / pixel) * pixel;
        double Capitals(double top, double size) => Math.Round((top + UI.Baseline(size)) * screen) / screen - UI.CapHeight(size) / 2;
        double name = Snap((layout.Head - UI.LineBox(layout.Name)) / 2), border = Snap(1);
        double target = Capitals(name, layout.Name), ideal = target - border - UI.Baseline(layout.Plan) + UI.CapHeight(layout.Plan) / 2;
        // Of equally close places, the one nearest the ideal.
        double Miss(double top) => Math.Abs(Capitals(top + border, layout.Plan) - target) + 1e-6 * Math.Abs(top - ideal);
        var chip = Enumerable.Range(-3, 7).Select(step => Snap(ideal) + step * pixel).MinBy(Miss);
        double lift = Math.Round((target - Capitals(chip + border, layout.Plan)) * screen) / screen;
        return (name, chip, lift);
    }

    // top and lift: from TitlePlaces. The text sits right below the border: centered, it moved by a pixel where the rounded
    // border leaves the chip half a pixel short of or over the text.
    private static Border PlanChip(ProviderSnapshot snapshot, Layout layout, double top, double lift)
    {
        var text = UI.Centered(UI.DisplayPlan(snapshot.Plan), layout.Plan, UI.Tertiary);
        text.VerticalAlignment = VerticalAlignment.Top;
        if (lift != 0) text.RenderTransform = new TranslateTransform(0, lift);
        return new Border
        {
            Child = text, BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(PanelTheme.Corner(4)),
            Padding = new Thickness(layout.Tier >= Tier.Dense ? 4 : 5, 0, layout.Tier >= Tier.Dense ? 4 : 5, 0),
            Margin = new Thickness(6, top, 0, 0), VerticalAlignment = VerticalAlignment.Top
        };
    }

    // A quota's place before its first reading, in QuotaBlock's three rows: the first says it is being read and the others hold
    // a neutral bar where the period's name goes, the share is a dash, and the track is empty. The track is the quota's own bar
    // (same key), so the first reading eases it up from nothing.
    private static Grid SkeletonBlock(AppEntry entry, Layout layout, int slot)
    {
        var block = new Grid();
        foreach (double height in new[] { layout.LabelRow, layout.RowGap, layout.NumberRow, layout.RowGap, layout.Track })
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        FrameworkElement label = slot == 0 ? UI.Fixed(Loc.T("正在读取…", "Reading…"), layout.Label, layout.LabelRow, UI.Tertiary)
            : new Border { Width = Math.Round(layout.Label * 3.5), Height = Math.Max(4, Math.Round(layout.Label * .7)), CornerRadius = new CornerRadius(PanelTheme.Corner(3)),
                Background = UI.Brush(UI.Fill), VerticalAlignment = VerticalAlignment.Center };
        label.HorizontalAlignment = HorizontalAlignment.Left;
        block.Children.Add(label);
        var number = new TextBlock { FontFamily = UI.DisplayFont, LineHeight = layout.NumberRow, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Height = layout.NumberRow,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        number.Inlines.Add(new Run("—%") { FontSize = layout.Number, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(UI.Tertiary) });
        Grid.SetRow(number, 2); block.Children.Add(number);
        var track = new QuotaBar($"{entry.InstanceId:N}:{slot}", 0, PanelTheme.AppTheme(entry)); Grid.SetRow(track, 4);
        block.Children.Add(track);
        AutomationProperties.SetName(block, Loc.T("正在读取配额", "Reading the quota"));
        return block;
    }

    // Three rows per quota: the period name, the used share with its reset countdown, and the track.
    // slot is the quota's place in its column: a bar keeps its place's last share (QuotaBar), so a new reading eases the same bar
    // and a quota page flip eases each bar from the quota it replaces, as the monitor page's values move.
    private static Grid QuotaBlock(AppEntry entry, UsageMetric quota, Layout layout, int slot)
    {
        double used = Math.Clamp(quota.UsedPercent ?? 100, 0, 100);
        string color = quota.UsedPercent is null ? PanelTheme.QuotaTheme(entry) : PanelTheme.QuotaColor(entry, used);
        int level = quota.UsedPercent is null ? 0 : AppPresets.Level(used);
        var block = new Grid();
        foreach (double height in new[] { layout.LabelRow, layout.RowGap, layout.NumberRow, layout.RowGap, layout.Track })
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var labelRow = new Grid(); labelRow.ColumnDefinitions.Add(new ColumnDefinition()); labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = UI.Fixed(MetricLabel(quota), layout.Label, layout.LabelRow, UI.Secondary); label.ToolTip = Loc.Label(quota.Label); label.HorizontalAlignment = HorizontalAlignment.Left;
        labelRow.Children.Add(label); block.Children.Add(labelRow);
        // The number always shows in full; the countdown at the right is what gives way in a narrow column.
        var numbers = new Grid(); numbers.ColumnDefinitions.Add(new ColumnDefinition()); numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(numbers, 2);
        var number = new TextBlock { FontFamily = UI.DisplayFont, LineHeight = layout.NumberRow, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Height = layout.NumberRow,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        // A custom item without a value leaves the row empty; the row keeps its height so blocks stay aligned.
        if (quota.ValueText.Length > 0) number.Inlines.Add(new Run(quota.ValueText) { FontSize = layout.Number, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(level > 0 ? color : UI.Primary) });
        if (layout.UsedWord && quota.ValueSuffix.Length > 0) number.Inlines.Add(new Run(" " + quota.ValueSuffix) { FontSize = 11, FontFamily = UI.PanelFont, Foreground = UI.Brush(UI.Tertiary) });
        number.ToolTip = quota.Kind switch
        {
            MetricKind.Balance => Loc.T("可用 " + quota.ValueText, quota.ValueText + " available")
                + (quota.Total is { } total ? quota.TotalIsTarget
                    ? Loc.T(" · 目标余额 " + UsageMetric.Money(total, quota.Currency), " · target " + UsageMetric.Money(total, quota.Currency))
                    : Loc.T(" · 总额 " + UsageMetric.Money(total, quota.Currency), " · " + UsageMetric.Money(total, quota.Currency) + " total") : ""),
            MetricKind.Spend => Loc.T("已用 " + quota.ValueText, quota.ValueText + " used") + (quota.Unlimited ? Loc.T(" · 无上限", " · no limit") : ""),
            MetricKind.Count => CountTooltip(quota),
            _ => MetricPercentTooltip(quota)
        };
        numbers.Children.Add(number);
        var resetText = UI.ShortReset(quota.ResetAt);
        var expiryText = quota.ExpiresAt is { } expiry ? (expiry <= DateTimeOffset.Now ? Loc.T("已到期", "Expired") : Loc.T(UI.ShortReset(expiry) + "到期", "expires " + UI.ShortReset(expiry))) : "";
        var countText = CountLabel(quota);
        var sideText = countText.Length > 0 ? countText : quota.Kind == MetricKind.Balance && quota.UsedPercent is { } percent ? Loc.T($"{Math.Max(0, percent):0.#}% 已用", $"{Math.Max(0, percent):0.#}% used") : quota.Unlimited ? Loc.T("无上限", "No limit") : "";
        // Times sit beside the number, next to the track; shares, counts and "无上限" sit at the label's right.
        // A quota with both a reset and an expiry keeps the reset below and moves the expiry up.
        bool expiryAbove = resetText.Length > 0 && expiryText.Length > 0;
        // A custom item places its own strings in the same two spots.
        bool text = quota.Kind == MetricKind.Text;
        // An account balance in a row with off-peak time takes both spots: peak or off-peak now above, the time to the next
        // switch below. The panel is drawn again each minute, so the countdown stays current.
        var clock = DateTime.Now;
        bool offPeak = quota.Kind == MetricKind.Balance && quota.Label == "账户余额" && entry.OffPeak.Count > 0, offPeakNow = offPeak && entry.IsOffPeak(clock);
        var change = offPeak ? entry.NextOffPeakChange(clock) : null;
        var upperText = text ? quota.Note : offPeak ? (offPeakNow ? Loc.T("低峰时段", "Off-peak") : Loc.T("高峰时段", "Peak")) : expiryAbove ? expiryText : sideText;
        if (upperText.Length > 0)
        {
            var reset = UI.Fixed(upperText, layout.Reset, layout.LabelRow, UI.Tertiary);
            reset.HorizontalAlignment = HorizontalAlignment.Right; reset.Margin = new Thickness(6, 0, 0, 0);
            reset.ToolTip = text ? null : offPeak ? OffPeakTooltip(entry) : expiryAbove ? quota.ExpiresAt!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : countText.Length > 0 ? CountTooltip(quota) : MetricPercentTooltip(quota);
            Grid.SetColumn(reset, 1); labelRow.Children.Add(reset);
        }
        var rightText = text ? quota.ResetText : offPeak ? (change is { } next ? Loc.T(UI.ShortReset(next) + "切换", "switches " + UI.ShortReset(next)) : "")
            : resetText.Length > 0 ? resetText : expiryText;
        if (rightText.Length > 0)
        {
            var right = UI.Line(rightText, layout.Reset, UI.Tertiary);
            right.FontFamily = UI.PanelFont;
            right.ToolTip = text ? null : offPeak ? Loc.T($"{change:yyyy-MM-dd HH:mm} 转为" + (offPeakNow ? "高峰时段" : "低峰时段"), (offPeakNow ? "Peak" : "Off-peak") + $" from {change:yyyy-MM-dd HH:mm}")
                : (quota.ResetAt ?? quota.ExpiresAt)!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            right.VerticalAlignment = VerticalAlignment.Bottom; right.HorizontalAlignment = HorizontalAlignment.Right;
            right.Margin = new Thickness(6, 0, 0, 1); Grid.SetColumn(right, 1); numbers.Children.Add(right);
        }
        block.Children.Add(numbers);
        var track = new QuotaBar($"{entry.InstanceId:N}:{slot}", used / 100, color); Grid.SetRow(track, 4);
        block.Children.Add(track);
        return block;
    }

    // The row's off-peak periods, written as the settings write them.
    private static string OffPeakTooltip(AppEntry entry) => Loc.T("低峰时间：", "Off-peak time: ") + string.Join(Loc.T("、", ", "), entry.OffPeak.Select(period =>
        OffPeakPeriod.Format(period.Start) + " – " + (period.End <= period.Start ? Loc.T("次日 ", "next day ") : "") + OffPeakPeriod.Format(period.End)));

    // A rounded share hides small usage of a large grant, so counts show the exact amounts.
    private static string? CountTooltip(UsageMetric metric)
    {
        if (metric is not { Used: { } used, Total: { } total }) return MetricPercentTooltip(metric);
        var unit = metric.Unit.Length > 0 ? " " + Loc.Label(metric.Unit) : "";
        return Loc.T($"已用 {used:N0}{unit} · 剩余 {Math.Max(0, total - used):N0}{unit}\n总额 {total:N0}{unit}",
            $"{used:N0}{unit} used · {Math.Max(0, total - used):N0}{unit} left\n{total:N0}{unit} total");
    }

    // Counts in the hundreds of thousands, such as token grants, are abbreviated so the title keeps its room.
    private static string CountLabel(UsageMetric metric) => metric is { Kind: MetricKind.Count, Used: { } used, Total: { } total }
        ? total >= 100_000 ? $"{UI.Number((long)used)} / {UI.Number((long)total)}" : metric.CountText : "";

    private static string? MetricPercentTooltip(UsageMetric metric) => metric.UsedPercent is { } percent
        ? Loc.T($"已用 {Math.Max(0, percent):0.#}% · 剩余 {Math.Max(0, 100 - percent):0.#}%", $"{Math.Max(0, percent):0.#}% used · {Math.Max(0, 100 - percent):0.#}% left") : null;

    private static string? QuotaTooltip(ProviderSnapshot snapshot)
    {
        var lines = new List<string>();
        // Quota values, windows and the plan already appear in the column.
        if (snapshot.QuotaTime is { } time) lines.Add(Loc.T($"更新时间：{time.LocalDateTime:MM-dd HH:mm:ss}", $"Updated: {time.LocalDateTime:MM-dd HH:mm:ss}"));
        if ((!snapshot.LiveQuota || snapshot.IsStale) && snapshot.Status.Length > 0 && snapshot.Status != Loc.T("已连接", "Connected")) lines.Add(snapshot.Status);
        return lines.Count > 0 ? string.Join("\n", lines) : snapshot.LiveQuota || snapshot.Metrics.Count > 0 ? null : Loc.T("尚未获取配额", "Quota not read yet");
    }
    private static string MetricLabel(UsageMetric metric) => metric.Kind == MetricKind.Text ? metric.Label : UI.QuotaLabel(new Quota(metric.Label, 0, null, metric.Window));

    // Stacked: caption | tokens ... cost, so both rows line up. Grouped (side by side): caption, tokens and cost close together.
    private FrameworkElement TotalRow(AppEntry entry, ProviderSnapshot snapshot, string label, int days, Layout layout, double captionWidth, bool grouped = false)
    {
        var entries = Entries(snapshot, days);
        var row = new Grid { Height = layout.FootRow };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.CaptionWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = grouped ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        // Captions end on one vertical line, so "今日" and "七日" stack cleanly and both figures start at the same distance.
        var caption = new JustifiedText(label, layout.Foot, UI.Tertiary, captionWidth)
        { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        row.Children.Add(caption);
        // Each row prices its records once; the figure and its tooltip share the result.
        long total = entries.Sum(x => x.Total);
        var cost = snapshot.UsageAvailable ? _app.Prices.Summarize(entries) : null;
        FrameworkElement tokens = snapshot.UsageAvailable ? UI.Centered(UI.Number(total), layout.Foot, UI.Secondary)
            : UI.CenteredSymbol("—", layout.Foot, UI.Secondary);
        tokens.HorizontalAlignment = HorizontalAlignment.Left;
        tokens.ToolTip = ProviderCatalog.UsageTip(snapshot.Id) + "\n" + (snapshot.UsageAvailable ? Loc.T($"{label} Token · {total:N0}", $"{label} tokens · {total:N0}") : snapshot.UsageNote); Grid.SetColumn(tokens, 1); row.Children.Add(tokens);
        FrameworkElement price = snapshot.UsageAvailable ? UI.Centered(UI.Amount(cost!), layout.Foot, UI.Primary, FontWeights.SemiBold)
            : UI.CenteredSymbol("—", layout.Foot, UI.Tertiary, FontWeights.SemiBold);
        price.Margin = new Thickness(grouped ? 12 : 8, 0, 0, 0); price.ToolTip = snapshot.UsageAvailable ? UI.CostNote(cost!, _app.Prices) : snapshot.UsageNote; Grid.SetColumn(price, 2); row.Children.Add(price);
        // The "+" for unpriced records hangs in the arrow column, so the amounts of both rows still end on one vertical line.
        if (cost is { Unpriced: > 0 })
        {
            var plus = UI.Centered("+", layout.Foot, UI.Primary, FontWeights.SemiBold);
            plus.HorizontalAlignment = HorizontalAlignment.Left; plus.ToolTip = price.ToolTip; Grid.SetColumn(plus, 3); row.Children.Add(plus);
        }
        var arrow = UI.CenteredSymbol("›", layout.Foot + 1, UI.Tertiary); arrow.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(arrow, 3); row.Children.Add(arrow);
        var link = DetailLink(row, Loc.T($"查看 {entry.Name} {label}明细", $"Show {entry.Name} {label.ToLowerInvariant()} details"), () => OpenModelDetails(snapshot.Id, days));
        // Extend the highlight into the column inset without shifting the aligned totals.
        link.Padding = new Thickness(6, 0, 6, 0);
        link.Margin = new Thickness(-6, 0, -6, 0);
        AutomationProperties.SetAutomationId(link, $"usage-{days}-{entry.InstanceId:N}");
        return link;
    }

}
