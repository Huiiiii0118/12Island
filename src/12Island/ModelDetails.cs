using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

public partial class MainWindow
{
    private readonly HashSet<ProviderId> _modelApps = [];
    private readonly HashSet<ProviderId> _knownModelApps = [];
    private UsageDateRange? _customRange;
    private DateTime? _historyStart;
    private ScrollViewer? _modelScroll;
    private double _modelScrollOffset;
    private Window? _detailPopup;
    internal UsageDateRange ModelRange => _customRange ?? UsageDateRange.Recent(_days, DateTime.Today);

    private Button DetailLink(UIElement content, string name, Action click)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("DetailLinkButton") };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private void SynchronizeModelSelection(IReadOnlyList<ProviderId> apps)
    {
        bool all = _knownModelApps.SetEquals(_modelApps);
        var enabled = apps.ToHashSet();
        _modelApps.IntersectWith(enabled);
        if (all || _modelApps.Count == 0) _modelApps.UnionWith(enabled);
        _knownModelApps.Clear(); _knownModelApps.UnionWith(enabled);
    }

    internal void OpenModelDetails(ProviderId provider, int days)
    {
        if (!IsUsagePage && Settings.UsagePage) SelectPage(DeckPage.Usage, remember: false);
        _modelApps.Clear(); _modelApps.Add(provider);
        _knownModelApps.Clear(); _knownModelApps.UnionWith(Settings.StatisticsProviders);
        _usageHandover = _details ? Handover.Dissolve : Handover.Forward;
        _customRange = null; _historyStart = null; _days = days; _details = true;
        ResetModelScroll(); RenderUsage();
    }

    private void ResetModelScroll() { _modelScroll?.ScrollToTop(); _modelScroll = null; _modelScrollOffset = 0; }
    internal void ToggleModelApp(ProviderId provider)
    {
        if (!_modelApps.Contains(provider)) _modelApps.Add(provider);
        else if (_modelApps.Count > 1) _modelApps.Remove(provider);
        _usageHandover = Handover.Dissolve; ResetModelScroll(); RenderUsage();
    }
    internal void SelectModelPeriod(int days)
    {
        _ = UsageDateRange.Recent(days, DateTime.Today);
        _days = days; _customRange = null; _historyStart = null; _usageHandover = Handover.Dissolve; ResetModelScroll(); RenderUsage();
    }
    internal void SelectModelRange(UsageDateRange range)
    {
        if (range.End > DateTime.Today) throw new ArgumentException(Loc.T("结束日期不能晚于今天。", "The end date cannot be later than today."));
        _customRange = range; _usageHandover = Handover.Dissolve; ResetModelScroll();
        // The logs are read back only as far as the range shown needs: a later range lets the next refresh drop what is before it.
        var earliest = _historyStart ?? DateTime.Today.AddDays(-29);
        _historyStart = range.Start < DateTime.Today.AddDays(-29) ? range.Start : null;
        if (range.Start < earliest) _ = RefreshAsync();
        RenderUsage();
    }

    private string ModelPeriodLabel => _customRange is { } range ? $"{range.Start:MM-dd}–{range.End:MM-dd}" : PeriodName(_days);
    private static string PeriodName(int days) => days == 1 ? Loc.T("今日", "Today") : Loc.T($"{days} 天", $"{days} days");

    private System.Windows.Controls.ToolTip? _backToolTip;
    // content wraps the label when the button shows more than its text; toolTip false leaves only the accessible name.
    private Button ModelAction(string text, string name, Action click, bool active = false, bool icon = false,
        string style = "ToolbarActionButton", double height = 24, bool toolTip = true, Func<TextBlock, UIElement>? content = null)
    {
        var label = UI.Centered(text, icon ? 14 : 12, UI.Secondary, font: icon ? new FontFamily("Segoe MDL2 Assets") : null);
        label.ClearValue(TextBlock.ForegroundProperty); label.ClearValue(TextBlock.FontWeightProperty);
        var button = new Button { Content = content is null ? label : content(label), Height = height, Padding = new Thickness(icon ? 6 : 9, 0, icon ? 6 : 9, 0),
            Style = (Style)FindResource(style), Tag = active ? "Selected" : null, ToolTip = toolTip ? name : null };
        // The toolbar look's 6 DIP corner comes from its style's own resources, which a theme does not replace: square in a pixel theme.
        if (style == "ToolbarActionButton") button.Resources["SegmentCorner"] = new CornerRadius(PanelTheme.Corner(6));
        AutomationProperties.SetName(button, name); button.Click += (_, _) => click(); return button;
    }

    private string StatisticsTheme(ProviderId id) => Settings.EnabledApps.FirstOrDefault(app => app.UsageSource == id) is { } app ? PanelTheme.AppTheme(app) : UI.ColorFor(id);

    private FrameworkElement ModelApplicationFilter(IReadOnlyList<ProviderId> apps, int stage)
    {
        if (stage == 4)
            return ModelAction(Loc.T($"应用 · {_modelApps.Count} ▾", $"Apps · {_modelApps.Count} ▾"), Loc.T("筛选应用", "Filter apps"), () => OpenModelApplications(apps), toolTip: false);
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var app in apps)
        {
            var name = AppPresets.Name(app);
            var label = stage == 0 ? name : stage == 1 ? name.Length > 3 ? name[..3] + "…" : name : name[..1];
            UIElement Tab(TextBlock shown)
            {
                // A hidden SemiBold copy reserves the selected width, so selecting a tab does not resize it.
                var reserve = UI.Centered(label, 12, UI.Secondary, FontWeights.SemiBold); reserve.Visibility = Visibility.Hidden;
                var text = new Grid { Children = { reserve, shown } };
                if (stage < 2) return text;
                var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(StatisticsTheme(app)), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                return new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } };
            }
            var button = ModelAction(label, Loc.T("筛选 ", "Filter ") + name, () => ToggleModelApp(app), _modelApps.Contains(app),
                style: "SegmentButton", height: 22, toolTip: false, content: Tab);
            button.Padding = new Thickness(stage >= 2 ? 5 : 8, 0, stage >= 2 ? 5 : 8, 0);
            strip.Children.Add(button);
        }
        return new Border { Child = strip, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(1) };
    }

    private Button ModelDateButton(bool iconOnly)
    {
        Button button = null!;
        button = ModelAction(iconOnly ? "\uE787" : ModelPeriodLabel, Loc.T("按时间", "By date"), () => OpenModelDates(button), _customRange is not null, iconOnly, toolTip: false);
        if (!iconOnly)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = UI.Centered("\uE787", 12, UI.Secondary, font: new FontFamily("Segoe MDL2 Assets"));
            // The calendar glyph sits slightly above the label; move the glyph, not the label. The top margin centers it
            // 2/3 DIP lower (one device pixel at 150%), and layout rounding keeps it on whole pixels at every scale.
            icon.Margin = new Thickness(0, 4.0 / 3, 0, 0); content.Children.Add(icon);
            var label = UI.Centered(_customRange is null ? Loc.T("按时间", "By date") : ModelPeriodLabel, 12, _customRange is null ? UI.Secondary : UI.Primary);
            label.Margin = new Thickness(6, 0, 0, 0); content.Children.Add(label); button.Content = content;
        }
        return button;
    }

    private void RenderModelToolbar(IReadOnlyList<ProviderId> apps)
    {
        ModelToolbar.ColumnDefinitions.Clear();
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ModelToolbar.ColumnDefinitions.Add(new ColumnDefinition());
        var back = ModelAction("\uE76B", Loc.T("返回用量", "Back to usage"), () => ShowModelDetails(false), icon: true);
        _backToolTip ??= UI.LeftwardToolTip(null!);
        _backToolTip.Content = back.ToolTip; back.ToolTip = _backToolTip;
        ModelToolbar.Children.Add(back);
        var filters = new Grid { Name = "ModelFilters", HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 5; i++) filters.ColumnDefinitions.Add(new ColumnDefinition
        { Width = i % 2 == 0 ? GridLength.Auto : new GridLength(8) });
        Border Periods(double padding) => Segmented(new[] { 1, 7, 30 }.Select(days => new SegmentItem(PeriodName(days), PeriodName(days),
            _customRange is null && days == _days, () => SelectModelPeriod(days))).ToList(), 12, 22, padding, centerText: true, glide: "periods");
        var periods = Periods(7);
        FrameworkElement applications = null!; Button dates = null!;
        double available = Math.Max(0, ContentCanvas.Width - ModelToolbar.Margin.Left - ModelToolbar.Margin.Right - 36);
        for (int stage = 0; stage <= 4; stage++)
        {
            applications = ModelApplicationFilter(apps, stage);
            // From stage 3 the date button shows only the calendar icon, with or without a custom range; a selected range keeps its highlight.
            dates = ModelDateButton(stage >= 3);
            if (stage == 4) periods = Periods(3);
            foreach (var element in new[] { applications, periods, dates }) element.Measure(new Size(double.PositiveInfinity, 28));
            if (applications.DesiredSize.Width + periods.DesiredSize.Width + dates.DesiredSize.Width + 16 <= available) break;
            if (stage == 4)
                applications = ModelAction(Loc.T("应用 ▾", "Apps ▾"), Loc.T("筛选应用", "Filter apps"), () => OpenModelApplications(apps), toolTip: false);
        }
        Grid.SetColumn(periods, 2); Grid.SetColumn(dates, 4);
        filters.Children.Add(applications); filters.Children.Add(periods); filters.Children.Add(dates);
        Grid.SetColumn(filters, 2); ModelToolbar.Children.Add(filters);
    }

    private void OpenModelDates(FrameworkElement anchor)
    {
        var range = ModelRange;
        var popup = new ModelDateWindow(range);
        void PreserveReturnArea() => _popupReturnArea = new Rect(popup.PointToScreen(new Point()), popup.PointToScreen(new Point(popup.ActualWidth, popup.ActualHeight)));
        popup.Applied += selected =>
        {
            PreserveReturnArea();
            SelectModelRange(selected);
        };
        popup.Cancelled += PreserveReturnArea;
        ShowModelPopup(popup, anchor);
    }

    private void OpenModelApplications(IReadOnlyList<ProviderId> apps)
    {
        var stack = new StackPanel();
        var popup = new Window { Title = Loc.T("筛选应用", "Filter apps"), Width = 210, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true }; PanelTheme.Text(popup);
        foreach (var app in apps)
        {
            Button button = null!;
            button = ModelAction(AppPresets.Name(app), Loc.T("筛选 ", "Filter ") + AppPresets.Name(app), () =>
            { ToggleModelApp(app); button.Tag = _modelApps.Contains(app) ? "Selected" : null; }, _modelApps.Contains(app), toolTip: false);
            button.Margin = new Thickness(0, 3, 0, 3); stack.Children.Add(button);
        }
        popup.Content = new Border { Child = stack, Background = UI.Brush(PanelTheme.Hex("popup")), CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Padding = new Thickness(8), BorderBrush = UI.Brush(PanelTheme.Hex("popupBorder")), BorderThickness = new Thickness(1) };
        ShowModelPopup(popup, ModelToolbar);
    }

    private void ShowModelPopup(Window popup, FrameworkElement anchor)
    {
        if (_detailPopup is not null) { _detailPopup.Activate(); return; }
        _leave.Stop(); _popupReturnArea = null; _detailPopup = popup; popup.Owner = this;
        // Drawn at the panel's scale, as the panel it opens from.
        if (popup.Content is FrameworkElement scaled) UI.Scale(scaled, _scale);
        if (double.IsFinite(popup.Width)) popup.Width *= _scale;
        // A layered window renders grayscale text unless told otherwise; its rounded background is opaque, so ClearType is safe.
        RenderOptions.SetClearTypeHint(popup, ClearTypeHint.Enabled);
        var point = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        point = transform.Transform(point);
        // The work area of the panel's monitor in this window's DIPs, the same units as the point above. SystemParameters.WorkArea
        // is the primary monitor's, at the scale the session started with.
        var (_, panelWork, _) = Native.WindowsHost.MonitorScreen(MonitorHandle) ?? Native.WindowsHost.PrimaryScreen();
        var work = new Rect(transform.Transform(panelWork.TopLeft), transform.Transform(panelWork.BottomRight));
        popup.Left = Math.Clamp(point.X - popup.Width, work.Left, Math.Max(work.Left, work.Right - popup.Width)); popup.Top = point.Y + 3;
        popup.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { popup.Close(); SetExpanded(false, keyboard: true); e.Handled = true; } };
        // Closing an active owned window also raises Deactivated. Do not close it
        // again from that nested native activation message.
        // A closing pop-up leaves as the monitor page's card does (Soft.Leave), and the panel takes it as closed from the start of
        // that: the result it was closed for shows at once, and another pop-up may open meanwhile.
        bool closing = false, left = false;
        popup.Closing += (_, e) =>
        {
            closing = true; if (ReferenceEquals(_detailPopup, popup)) _detailPopup = null;
            if (left || _app.Exiting || popup.Content is not UIElement content || !Soft.Enabled) return;
            e.Cancel = true; left = true; popup.IsHitTestVisible = false;
            Soft.Leave(content, () => popup.Close());
            // The leave is the only thing that closes it now, and an animation replaced on the same property never completes,
            // so the pop-up closes on its own a little after the leave should have ended.
            var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Soft.Quick + 250) };
            fallback.Tick += (_, _) => { fallback.Stop(); if (popup.IsLoaded) popup.Close(); };
            fallback.Start();
            RenderUsage();
        };
        popup.Deactivated += (_, _) => { if (!closing && popup.IsVisible) popup.Close(); };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_detailPopup, popup)) _detailPopup = null;
            // A pop-up that faded out had the page brought up to date when it began to leave.
            if (!left) RenderUsage();
            if (_expanded && !IsMouseOver) PointerChanged(false);
        };
        // The pop-up enters once its first frame has been drawn: a layered window takes a frame or two to come up, and an
        // entrance started before Show lost its beginning to them and popped in. Until then its content is held out of sight.
        if (Soft.Enabled && popup.Content is UIElement entering)
        {
            entering.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0, TimeSpan.Zero));
            // Once only, and not when it was closed before its first frame: the entrance would replace the leave's fade, whose
            // end is what closes it.
            EventHandler? enter = null;
            enter = (_, _) => { popup.ContentRendered -= enter; if (!left && popup.IsVisible) Soft.Enter(entering); };
            popup.ContentRendered += enter;
        }
        popup.Show(); popup.Activate();
    }

    private static string TokenBreakdown(IEnumerable<TokenEntry> source)
    {
        var entries = source.ToList();
        return Loc.T($"输入 {entries.Sum(t => t.Input):N0}\n缓存写入 {entries.Sum(t => t.CacheWrite + t.CacheWriteHour):N0}\n缓存读取 {entries.Sum(t => t.CacheRead):N0}\n输出 {entries.Sum(t => t.Output):N0}\nToken 合计 {entries.Sum(t => t.Total):N0}",
            $"Input {entries.Sum(t => t.Input):N0}\nCache write {entries.Sum(t => t.CacheWrite + t.CacheWriteHour):N0}\nCache read {entries.Sum(t => t.CacheRead):N0}\nOutput {entries.Sum(t => t.Output):N0}\nTotal tokens {entries.Sum(t => t.Total):N0}");
    }

    // Every record in a row shares one model name and application, so the first record finds the row's prices.
    // A price table: one column per prompt-size tier, so standard and long-context prices sit side by side.
    private FrameworkElement PriceBreakdown(ModelUsageRow record)
    {
        var grid = new Grid();
        void Cell(string text, int row, int column, string? color = null, bool right = false, int span = 1, Thickness margin = default)
        {
            var block = new TextBlock { Text = text, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = margin };
            if (color is not null) block.Foreground = UI.Brush(color);
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(block, row); Grid.SetColumn(block, column); Grid.SetColumnSpan(block, span); grid.Children.Add(block);
        }
        // When every cost comes from the API, only the prices recovered from those amounts are the ones actually charged;
        // catalog prices would describe a cost that was never calculated.
        var recovery = record.Entries.All(t => t.ReportedCostUsd is not null) ? Pricing.FromReported(record.Entries) : null;
        var derived = recovery?.Prices;
        var model = recovery is null ? _app.Prices.Match(record.Entries[0]) : null;
        var tiers = model?.Tiers.Where(t => t.MinimumPromptTokens > 0).ToArray() ?? [];
        var columns = derived is not null ? [derived] : model is null ? [] : tiers.Select(t => t.Prices).Prepend(model.Tiers.LastOrDefault(t => t.MinimumPromptTokens <= 0)?.Prices ?? model.Prices)
            .Select(prices => _app.Prices.Effective(model, prices)).ToArray();
        for (int i = 0; i <= columns.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int span = columns.Length + 1, line = 0;
        Cell($"{AppPresets.Name(record.Provider)} · {record.Model}", line++, 0, span: span, margin: new Thickness(0, 0, 0, 3));
        if (tiers.Length > 0)
            for (int i = 0; i < columns.Length; i++)
                Cell(i == 0 ? $"< {UI.Number(tiers[0].MinimumPromptTokens)}" : i < tiers.Length ? $"{UI.Number(tiers[i - 1].MinimumPromptTokens)}–{UI.Number(tiers[i].MinimumPromptTokens)}"
                    : $"≥ {UI.Number(tiers[i - 1].MinimumPromptTokens)}", line, i + 1, UI.Secondary, true, margin: new Thickness(16, 0, 0, 0));
        if (tiers.Length > 0) line++;
        (string Label, Func<TokenPrices, decimal?> Price)[] kinds =
        [
            (Loc.T("输入", "Input"), p => p.Input), (Loc.T("输出", "Output"), p => p.Output), (Loc.T("缓存写入", "Cache write"), p => p.CacheWrite),
            (Loc.T("缓存写入（1 小时）", "Cache write (1 hour)"), p => p.CacheWriteHour == p.CacheWrite ? null : p.CacheWriteHour), (Loc.T("缓存读取", "Cache read"), p => p.CacheRead),
        ];
        foreach (var (label, price) in kinds.Where(kind => columns.Any(prices => kind.Price(prices) is not null)))
        {
            Cell(label, line, 0, UI.Secondary);
            for (int i = 0; i < columns.Length; i++)
                Cell(price(columns[i]) is { } value ? "$" + (value * 1_000_000m).ToString("0.00####", System.Globalization.CultureInfo.InvariantCulture) : "—",
                    line, i + 1, right: true, margin: new Thickness(16, 0, 0, 0));
            line++;
        }
        string note = recovery switch
        {
            { Status: RecoveryStatus.Recovered } => Loc.T("USD / 百万 Token · 根据接口金额推算", "USD per million tokens · worked out from the API amounts"),
            { Status: RecoveryStatus.TooFewRecords, Records: var count } => Loc.T($"所选时段只有 {count} 条记录，不足以推算单价",
                $"The selected period has only {Loc.Count(count, "record", "records")}, too few to work out the prices"),
            { Status: RecoveryStatus.Undetermined } => Loc.T("所选时段的记录不足以确定各项单价", "The records in the selected period cannot determine each price"),
            { Status: RecoveryStatus.Inconsistent } => Loc.T("各条记录的金额不符合固定单价，无法推算单价", "The amounts of the records do not fit fixed prices, so the prices cannot be worked out"),
            _ => model is null ? Loc.T("未找到该模型的单价", "No unit price was found for this model")
                : tiers.Length > 0 ? Loc.T("USD / 百万 Token · 按单次请求的输入量分档", "USD per million tokens · tiered by the input of each request") : Loc.T("USD / 百万 Token", "USD per million tokens"),
        };
        Cell(note, line, 0, UI.Secondary, span: span, margin: new Thickness(0, columns.Length == 0 ? 0 : 3, 0, 0));
        return grid;
    }

    private void RenderModels(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        var result = ModelUsage.Filter(snapshots, _modelApps, ModelRange);
        bool expanded = ContentCanvas.Width >= 600, compact = ContentCanvas.Width < 640 || ContentCanvas.Height < 170, tiny = ContentCanvas.Height < 170;
        double font = tiny ? 11.5 : 12.5, rowHeight = tiny ? 22 : 27, headerHeight = tiny ? 18 : 22, totalHeight = tiny ? 24 : 29;
        double inset = tiny ? 14 : 20, gap = expanded ? 8 : 12;
        UsageContent.Margin = new Thickness(inset, 0, inset, tiny ? 8 : 10);
        string[] Values(IEnumerable<TokenEntry> source)
        {
            var entries = source.ToList();
            return expanded ? [UI.Number(entries.Sum(t => t.Input)), UI.Number(entries.Sum(t => t.CacheWrite + t.CacheWriteHour)),
                UI.Number(entries.Sum(t => t.CacheRead)), UI.Number(entries.Sum(t => t.Output)), UI.Number(entries.Sum(t => t.Total)), UI.Cost(entries, _app.Prices)]
                : [UI.Number(entries.Sum(t => t.Total)), UI.Cost(entries, _app.Prices)];
        }
        // A figure cell's tooltip gives that cell's exact value. The narrow table has no breakdown columns, so its total keeps the breakdown.
        // Only the total's cost has a tooltip, with the price update time.
        string?[] Tips(IEnumerable<TokenEntry> source, bool total)
        {
            var entries = source.ToList();
            var cost = total ? UI.CostNote(entries, _app.Prices) : null;
            return expanded ? [entries.Sum(t => t.Input).ToString("N0"), entries.Sum(t => t.CacheWrite + t.CacheWriteHour).ToString("N0"),
                entries.Sum(t => t.CacheRead).ToString("N0"), entries.Sum(t => t.Output).ToString("N0"), entries.Sum(t => t.Total).ToString("N0"), cost]
                : [TokenBreakdown(entries), cost];
        }
        var totals = Values(result.Entries);
        bool incomplete = result.IncompleteProviders.Count > 0;
        if (incomplete && result.Entries.Count == 0) totals = totals.Select(_ => "—").ToArray();
        double[] widths = expanded ? [compact ? 72 : 80, 0, ..Enumerable.Repeat(compact ? 52d : 56d, 4), compact ? 58 : 62, compact ? 64 : 78] : [84, 0, 84, 84];
        var values = result.Rows.Select(row => Values(row.Entries)).ToArray();
        var headings = expanded
            ? new[] { Loc.T("输入", "Input"), Loc.T("缓存写入", "Cache write"), Loc.T("缓存读取", "Cache read"), Loc.T("输出", "Output"), Loc.T("Token 合计", "Total tokens"), Loc.T("费用 · USD", "Cost · USD") }
            : [Loc.T("Token 合计", "Total tokens"), Loc.T("费用 · USD", "Cost · USD")];
        for (int n = 0; n < totals.Length; n++)
        {
            // English headings are longer than the numbers under them; a column also fits its heading.
            var heading = UI.Centered(headings[n], tiny ? 10 : 11, UI.Tertiary); heading.Measure(new Size(double.PositiveInfinity, headerHeight));
            widths[n + 2] = Math.Max(widths[n + 2], Math.Ceiling(heading.DesiredSize.Width));
            // Amounts such as $1,234.56 + give up model-name space, never numeric digits.
            foreach (var value in values.Select(row => row[n]).Append(totals[n]))
            {
                var text = UI.Centered(value, font, UI.Primary, FontWeights.SemiBold); text.Measure(new Size(double.PositiveInfinity, rowHeight));
                widths[n + 2] = Math.Max(widths[n + 2], Math.Ceiling(text.DesiredSize.Width));
            }
        }
        var panel = new Grid { Name = "ModelTable" };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight + 1) });
        panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(totalHeight + 1) });
        Grid Row(string app, string model, string[] numbers, bool header, bool total, ProviderId? provider = null)
        {
            double height = header ? headerHeight : total ? totalHeight : rowHeight;
            var row = new Grid { Height = height, Name = header ? "ModelHeader" : total ? "ModelTotal" : "ModelDataRow" };
            for (int i = 0; i < widths.Length; i++)
            {
                if (i > 0) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
            }
            string[] labels = [app, model, ..numbers];
            for (int i = 0; i < labels.Length; i++)
            {
                var text = UI.Centered(labels[i], header ? tiny ? 10 : 11 : font,
                    header || total && i < 2 ? UI.Tertiary : i == 1 || i >= 2 && i < widths.Length - 2 && !total ? UI.Secondary : UI.Primary,
                    !header && (total && i >= 2 || !total && i == 0 || i == widths.Length - 1) ? FontWeights.SemiBold : FontWeights.Normal);
                text.TextAlignment = i >= 2 ? TextAlignment.Right : TextAlignment.Left;
                FrameworkElement cell = text;
                if (i == 0 && provider is { } id)
                {
                    var box = new Grid(); box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) }); box.ColumnDefinitions.Add(new ColumnDefinition());
                    // Match the visible Latin glyphs, whose center sits just below the font's line-box center.
                    box.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(StatisticsTheme(id)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                        RenderTransform = new TranslateTransform(0, 1) });
                    Grid.SetColumn(text, 1); box.Children.Add(text); cell = box;
                }
                Grid.SetColumn(cell, i * 2); row.Children.Add(cell);
            }
            return row;
        }
        panel.Children.Add(new Border { Child = Row(Loc.T("应用", "App"), Loc.T("模型", "Model"), headings, true, false), BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) });
        var list = new StackPanel();
        for (int i = 0; i < result.Rows.Count; i++)
        {
            var record = result.Rows[i];
            var row = Row(AppPresets.Name(record.Provider), record.Model, values[i], false, false, record.Provider);
            var tips = Tips(record.Entries, false);
            // The model name shows the unit prices; the app name shows no tooltip.
            foreach (var text in row.Children.OfType<TextBlock>())
            {
                int column = Grid.GetColumn(text) / 2;
                text.ToolTip = column == 1 ? PriceBreakdown(record) : column >= 2 ? tips[column - 2] : (object?)null;
            }
            var item = new Border { Child = row, Background = Brushes.Transparent, BorderBrush = UI.Brush(PanelTheme.Hex("rowDivider")), BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0) };
            item.MouseEnter += (_, _) => item.Background = UI.Brush(PanelTheme.Hex("rowHover")); item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            list.Children.Add(item);
        }
        // The viewport clip would turn the rows grayscale; they lie over the island's opaque surface.
        RenderOptions.SetClearTypeHint(list, ClearTypeHint.Enabled);
        // Focusable so the arrow keys scroll the list; an outline around the whole list would only add noise.
        var scroll = new ScrollViewer { Content = list, Style = (Style)FindResource("ModelScrollViewer"), Focusable = true, FocusVisualStyle = null };
        _modelScroll = scroll;
        var offset = _modelScrollOffset;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        if (result.Rows.Count == 0)
        {
            var message = UI.Centered(incomplete ? Refreshing ? Loc.T("正在读取所选时段…", "Reading the selected period…") : Loc.T("所选应用的用量明细暂不可用", "Usage details for the selected apps are not available yet")
                : Loc.T("所选时段暂无用量记录", "No usage records in the selected period"), 12, UI.Tertiary);
            message.HorizontalAlignment = HorizontalAlignment.Center; message.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(message, 1); panel.Children.Add(message);
        }
        string selected = _modelApps.SetEquals(Settings.StatisticsProviders) ? Loc.T("全部", "All") : _modelApps.Count == 1 ? AppPresets.Name(_modelApps.Single()) : Loc.T($"{_modelApps.Count} 个应用", Loc.Count(_modelApps.Count, "app", "apps"));
        string summary = $"{selected} · {ModelPeriodLabel} · " + Loc.T($"{result.Rows.Count} 项", Loc.Count(result.Rows.Count, "item", "items"));
        var totalRow = Row(incomplete ? Loc.T("已知合计", "Known total") : Loc.T("合计", "Total"), summary, totals, false, true);
        string missing = incomplete ? Loc.T("部分明细尚未读取完整：", "Some details are not fully read yet: ") + string.Join(Loc.T("、", ", "), result.IncompleteProviders.Select(AppPresets.Name)) : "";
        // The label cells explain only an incomplete total; otherwise the visible summary says it all.
        string? totalTip = incomplete ? missing : null;
        // Figures shown as "—" are not known yet, so their tooltips say why instead of giving a count of zero.
        var totalTips = incomplete && result.Entries.Count == 0 ? totals.Select(_ => (string?)missing).ToArray() : Tips(result.Entries, true);
        if (incomplete && result.Entries.Count > 0) totalTips[^1] = totalTips[^1] is { } note ? note + "\n" + missing : missing;
        totalRow.ToolTip = totalTip;
        foreach (var cell in totalRow.Children.OfType<FrameworkElement>())
        {
            int column = Grid.GetColumn(cell) / 2;
            cell.ToolTip = column >= 2 ? totalTips[column - 2] : totalTip;
        }
        var footer = new Border { Child = totalRow, BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footer, 2); panel.Children.Add(footer); UsageContent.Children.Add(panel);
    }
}
