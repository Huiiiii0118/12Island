using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

// The system monitor's own settings page ("系统监控 › 显示与读数"), as AI usage and music have theirs: how the expanded page looks
// and what the collapsed notch or capsule shows. The page's size stays with the other pages' sizes under "展开外观", and the
// switches that show the readings in the notch or the capsule stay under "收起外观 › 显示内容", next to the quota rings.
public sealed partial class SettingsWindow
{
    // The colors come first on their own, as they color both the collapsed readings and the expanded page: a color per reading,
    // with ftop's schemes and the panel theme's colors under them as quick presets. Then what the collapsed row shows and how the
    // expanded page looks, under the music page's "收起时" and "展开时".
    private void MonitorSettings()
    {
        // The colors follow the panel theme, and the panel theme follows the appearance mode: there is nothing here to pick.
        // The options stay available while the page is off, so they can be set before it is shown.
        SectionTitle(Loc.T("数据来源", "Data source"), help: () => Loc.T(
            "显卡占用和温度由下面的方式读取；选“自动”时，程序会挑能读到数据的那一种。",
            "GPU usage and temperature come from the reader chosen here; on Auto the app picks the one that gives a reading."));
        Group(GpuChoiceRow());
        CompactMonitorOptions();
        SectionTitle(Loc.T("展开时", "When expanded"));
        // The choices that show themselves on the monitor page keep its preview open, so switching between them does not collapse it first.
        Group(MusicChoice(Loc.T("主视图", "Large gauge"), S.MonitorMain, [(MonitorMain.Cpu, "CPU"), (MonitorMain.Gpu, "GPU")], v => Resize(s => s.MonitorMain = v, DeckPage.Monitor), keepPreview: true),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            MusicChoice(Loc.T("进程行", "Process rows"), S.MonitorProcessView,
                [(MonitorProcessView.Auto, Loc.T("自动", "Auto")), (MonitorProcessView.Compact, Loc.T("简略", "Compact")), (MonitorProcessView.Detailed, Loc.T("详细", "Detailed"))],
                v => Resize(s => s.MonitorProcessView = v, DeckPage.Monitor), keepPreview: true),
            Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用。", "Lists the apps using the most CPU or memory."),
                keepPreview: true),
            Toggle(Loc.T("显示硬盘", "Show drives"), S.MonitorStorage, v => Resize(s => s.MonitorStorage = v, DeckPage.Monitor),
                help:
                Loc.T("不含 U 盘、移动硬盘、网络盘和光驱。", "USB, external, network and optical drives are not included."),
                keepPreview: true),
            Toggle(Loc.T("显示功耗与电池", "Show power and battery"), S.MonitorPower, v => Resize(s => s.MonitorPower = v, DeckPage.Monitor),
                help:
                Loc.T("在硬盘下方显示整机功耗和本机电池。只在面板展开到系统监控页时读取。",
                    "Shows system power and this PC's battery under the drives. Read only while the panel is open on the system monitor page."),
                keepPreview: true),
            WithFrameAccess(Toggle(Loc.T("显示帧率", "Show the frame rate"), S.MonitorFrames, v => { Resize(s => s.MonitorFrames = v, DeckPage.Monitor); if (v) AskFrameAccess(); else FrameAccessChanged?.Invoke(); },
                help:
                Loc.T("在网速下方显示前台程序的帧率、程序名称和所在显示器的刷新率；前台程序不在绘制时，显示最近一个在绘制的程序。只在面板展开到系统监控页时读取。首次开启时需要授权。",
                    "Shows the frame rate of the program in front under the network rates, with its name and its display's refresh rate; while it draws nothing, the last program that did. Read only while the panel is open on the system monitor page. Needs permission the first time."),
                keepPreview: true), () => S.MonitorFrames && !Reading(S, CompactMonitorItems.Frames).Visible),
            WithSensorStops(Toggle(Loc.T("显示温度", "Show temperatures"), S.MonitorTemperatures, v => Resize(s => s.MonitorTemperatures = v, DeckPage.Monitor),
                help:
                Loc.T("读不到的温度不显示。CPU 温度由主板固件（ACPI 温区）提供，不少台式机没有这项数据；这类电脑上可以运行 HWiNFO（打开共享内存）或 LibreHardwareMonitor（打开远程网页服务），程序会从其中一个读取。",
                    "Temperatures that cannot be read are not shown. The CPU temperature comes from the motherboard firmware (ACPI thermal zones), which many desktops do not provide; on those, running HWiNFO (shared memory on) or LibreHardwareMonitor (web server on) gives the app a reading."),
                keepPreview: true)));
    }

    // Under the last row of "展开时": a line in amber for every reading stopped because its driver did not answer (SensorGuard),
    // with a button that turns it on again. Shown whatever the switches say, until the reading is turned on again; nothing without
    // a stop. Filled again when a reading stops or is turned on (SensorStopsChanged).
    private Action? _refreshStops;
    private FrameworkElement WithSensorStops(FrameworkElement row)
    {
        var lines = new StackPanel();
        void Fill()
        {
            lines.Children.Clear();
            foreach (var stop in Native.SensorGuard.Stops)
            {
                var line = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var notice = TextLine(SensorStops.Notice(stop), 12.5, CautionColor);
                notice.TextWrapping = TextWrapping.Wrap; notice.VerticalAlignment = VerticalAlignment.Center; notice.Margin = new Thickness(0, 0, 12, 0);
                var resume = ActionButton(Loc.T("重新开启", "Turn on again"), () => Native.SensorGuard.Resume(stop));
                resume.ToolTip = Loc.T("重新开启后如果电脑再次卡住，12 island 会再次自动停止读取。", "Should the PC freeze again, 12 island stops the reading again by itself.");
                AutomationProperties.SetName(resume, Loc.T("重新开启：", "Turn on again: ") + SensorStops.Notice(stop));
                resume.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(resume, 1);
                line.Children.Add(notice); line.Children.Add(resume);
                lines.Children.Add(line);
            }
        }
        Fill();
        _refreshStops = Fill;
        var panel = new StackPanel();
        panel.Children.Add(row); panel.Children.Add(lines);
        return panel;
    }
    private void SensorStopsChanged()
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (IsLoaded) _refreshStops?.Invoke(); }); }

    // Under a frame rate switch while it is on and this user may not read frame events (FramePermission): a line in amber with the
    // button that grants it, or, once granted, that it works from the next sign-in. Nothing once allowed or while the switch is off.
    // One line on the page: with both switches on, only under the collapsed readings' table, which comes first.
    private event Action? FrameAccessChanged;
    private bool _grantingFrames;
    private string? _framesGrantFailure;
    private FrameworkElement WithFrameAccess(FrameworkElement row, Func<bool> wanted)
    {
        var line = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = TextLine("", 12.5, CautionColor);
        note.TextWrapping = TextWrapping.Wrap; note.VerticalAlignment = VerticalAlignment.Center; note.Margin = new Thickness(0, 0, 12, 0);
        var grant = ActionButton(Loc.T("授权", "Allow"), () => _ = GrantFrameAccessAsync());
        AutomationProperties.SetName(grant, Loc.T("授权读取帧率", "Allow reading frame rates"));
        grant.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(grant, 1);
        line.Children.Add(note); line.Children.Add(grant);
        void Update()
        {
            var access = Native.FramePermission.Current();
            line.Visibility = wanted() && access != Native.FrameAccess.Allowed ? Visibility.Visible : Visibility.Collapsed;
            bool missing = access == Native.FrameAccess.Missing;
            note.Text = _grantingFrames ? Loc.T("正在授权…", "Allowing…")
                : !missing ? Loc.T("已授权，重新登录 Windows 后显示帧率", "Allowed; the frame rate shows after you sign in to Windows again")
                : _framesGrantFailure is { } failure ? Loc.T("授权失败：", "Could not allow: ") + failure
                : Loc.T("读取帧率需要授权", "Reading the frame rate needs permission");
            note.Foreground = UI.Brush(_framesGrantFailure is not null && missing ? _palette.Negative : !missing ? TextTertiary : CautionColor);
            grant.Visibility = missing && !_grantingFrames ? Visibility.Visible : Visibility.Collapsed;
        }
        Update();
        var panel = new StackPanel();
        panel.Children.Add(row); panel.Children.Add(line);
        panel.Loaded += (_, _) => { FrameAccessChanged -= Update; FrameAccessChanged += Update; Update(); };
        panel.Unloaded += (_, _) => FrameAccessChanged -= Update;
        return panel;
    }
    // A frame rate switched on without permission asks for it at once; declined, the switch stays on and its line offers it again.
    private void AskFrameAccess()
    {
        if (!_grantingFrames && Native.FramePermission.Current() == Native.FrameAccess.Missing
            && Confirm(Loc.T("授权读取帧率", "Allow reading frame rates"),
                Loc.T("读取帧率需要把当前用户加入 Windows 的“Performance Log Users”组。授权时 Windows 会请求管理员确认，重新登录 Windows 后生效。",
                    "Reading frame rates needs the user in Windows' Performance Log Users group. Windows asks an administrator to confirm, and it works from the next sign-in."),
                Loc.T("授权", "Allow"), danger: false))
            _ = GrantFrameAccessAsync();
        else FrameAccessChanged?.Invoke();
    }
    private async Task GrantFrameAccessAsync()
    {
        if (_grantingFrames) return;
        _grantingFrames = true; _framesGrantFailure = null; FrameAccessChanged?.Invoke();
        try { await Native.FramePermission.GrantAsync(); }
        catch (OperationCanceledException) { _framesGrantFailure = Loc.T("管理员确认超时", "The administrator prompt timed out"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { _framesGrantFailure = ex.Message; }
        finally { _grantingFrames = false; FrameAccessChanged?.Invoke(); }
    }

    // The graphics adapter the monitor shows, on the page and in the collapsed readings alike: 自动, or one of the adapters found,
    // named with its kind. The adapters are the ones the monitor reads while it runs, else the ones DXGI lists. A chosen adapter
    // that is not connected stays in the list, marked, so the choice can be seen and changed.
    private Grid GpuChoiceRow()
    {
        var found = _app.Monitor?.Latest?.Gpus is { Count: > 0 } read ? read.Select(gpu => (gpu.Key, gpu.LegacyKey, gpu.Name, gpu.Luid, gpu.Kind, gpu.NoReadings)).ToList()
            : Native.GpuAdapters.Read().Adapters.Values.Where(adapter => !adapter.Software).OrderBy(adapter => GpuReadings.Rank(adapter.Kind))
                .ThenBy(adapter => adapter.Name, StringComparer.Ordinal).Select(adapter => (adapter.Key, adapter.LegacyKey, Name: (string?)adapter.Name, adapter.Luid, adapter.Kind, NoReadings: false)).ToList();
        var options = new List<(string? Key, string? Name, string Label)> { (null, null, Loc.T("自动", "Auto")) };
        // An adapter without readings at the moment stays in the list, marked.
        options.AddRange(found.Select(gpu => ((string?)gpu.Key, gpu.Name, GpuChoice.Describe(gpu.Name, gpu.Kind) + (gpu.NoReadings ? Loc.T("（暂无读数）", " (no reading)") : ""))));
        // The saved card is found as the monitor finds it (GpuChoice.Find): while it is not connected, another card of the same model
        // is never shown as chosen; the saved one is listed as not connected instead, and the monitor shows 自动 meanwhile.
        int selected = S.MonitorGpu is not { } saved ? 0 : GpuChoice.Find(found, gpu => (gpu.Key, gpu.LegacyKey, gpu.Name, gpu.Luid), saved, S.MonitorGpuName) is var at and >= 0 ? at + 1 : -1;
        if (selected < 0)
        {
            options.Add((S.MonitorGpu, S.MonitorGpuName, Loc.T($"{S.MonitorGpuName ?? "GPU"}（未连接，暂按自动显示）", $"{S.MonitorGpuName ?? "GPU"} (not connected; showing Auto meanwhile)")));
            selected = options.Count - 1;
        }
        var choice = new ComboBox { Width = 292, Style = (Style)FindResource("SettingsCombo"), ItemsSource = options.Select(option => option.Label).ToList(), SelectedIndex = selected };
        AutomationProperties.SetName(choice, Loc.T("显卡", "GPU")); PopupMotion.Attach(choice);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            var (key, name, _) = options[choice.SelectedIndex];
            if (key == S.MonitorGpu) return;
            Change(s => { s.MonitorGpu = key; s.MonitorGpuName = name; });
            // The open page and the collapsed readings show the new adapter at once.
            _app.Monitor?.ShowGpuChoice();
        };
        return Row(Loc.T("显卡", "GPU"), choice, help: () => Loc.T(
            "自动：优先显示独显，有多块独显时显示显存最大的一块；没有独显时显示占用最高的一块。选定后不随占用切换。以 GPU 为主视图时，点击 GPU 读数可切换显卡。",
            "Auto: prefers the discrete GPU, and with several, the one with the most memory; without one, the busiest GPU. It does not switch as the load changes. Click the GPU reading while the GPU is the large gauge to switch GPUs."));
    }

    // A color circle with its name beside it. refresh builds the circle again, so it shows the color the settings hold. The note
    // shows on hover over the cell, or, with info, by an info icon after the name, for a note the user needs to find.
    private StackPanel SwatchCell(string name, string? help, Func<Button> swatch, List<Action> refresh, bool info = false)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 3), ToolTip = info ? null : help };
        Button? shown = null;
        refresh.Add(() =>
        {
            var next = swatch();
            // A panel will not take a new child at an index still holding one, so the old circle leaves first.
            if (shown is not null) cell.Children.Remove(shown);
            cell.Children.Insert(0, next);
            shown = next;
        });
        var label = TextLine(name, 12.5, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(8, 0, 0, 0);
        cell.Children.Add(info && help is not null ? new HelpHeading(label, HelpIcon(name, () => help, 12)) : label);
        return cell;
    }

    // Changing a color of a preset makes the colors the user's own, starting from that preset; null returns a color to the
    // preset's, and with none changed the preset is lit again.
    private static void SetSchemeColor(DeckSettings settings, MonitorRole role, string? color)
    {
        if (settings.MonitorPalette != MonitorPalette.Custom) settings.MonitorCustomColors = new() { Base = settings.MonitorPalette };
        settings.MonitorCustomColors.Set(role, color);
        settings.MonitorPalette = settings.MonitorCustomColors.Changed ? MonitorPalette.Custom : settings.MonitorCustomColors.Base;
    }

    // A reading's color as the panel draws it. The picker's "默认" returns it to the preset's.
    private Button RoleSwatch(MonitorRole role, Action refreshed)
    {
        string? own = S.MonitorPalette == MonitorPalette.Custom ? S.MonitorCustomColors.Get(role) : null;
        string color = RoleColor(MonitorThemeColors.For(S), role), name = RoleName(role);
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(color) };
        AutomationProperties.SetName(button, Loc.T($"{name}颜色", $"{name} color"));
        AutomationProperties.SetHelpText(button, color + (own is null ? Loc.T("（默认）", " (default)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(own ?? color, own is null, value =>
        {
            popup.IsOpen = false;
            Resize(s => SetSchemeColor(s, role, value.Length > 0 ? value : null), DeckPage.Monitor);
            refreshed();
        });
        TogglePopup(button, popup);
        return button;
    }

    // The collapsed readings' color at the warning or critical level, as the panel draws it; "默认" returns it to the panel theme's.
    private Button LevelSwatch(MemoryLevel level, Action refreshed)
    {
        bool critical = level == MemoryLevel.Critical;
        string? own = critical ? S.CompactCriticalColor : S.CompactWarningColor;
        string color = critical ? MonitorThemeColors.Critical(S) : MonitorThemeColors.Warning(S), name = LevelName(level);
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(color) };
        AutomationProperties.SetName(button, Loc.T($"收起时{name}颜色", $"Collapsed {name} color"));
        AutomationProperties.SetHelpText(button, color + (own is null ? Loc.T("（默认）", " (default)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(own ?? (critical ? PanelTheme.CriticalDefault : PanelTheme.WarningDefault), own is null, value =>
        {
            popup.IsOpen = false;
            string? chosen = value.Length > 0 ? value : null;
            Change(s => { if (critical) s.CompactCriticalColor = chosen; else s.CompactWarningColor = chosen; });
            refreshed();
        });
        TogglePopup(button, popup);
        return button;
    }

    // A role's color as drawn: power and the battery by the theme's power style, which the custom scheme's own colors replace.
    private static string RoleColor(MonitorColors colors, MonitorRole role) => role switch
    {
        MonitorRole.Power => PowerStyle.For(colors).Draw, MonitorRole.Battery => PowerStyle.For(colors).Normal, _ => colors[role]
    };

    private static string RoleName(MonitorRole role) => role switch
    {
        MonitorRole.Efficiency => Loc.T("能效核", "Efficiency cores"), MonitorRole.Memory => Loc.T("内存", "Memory"), MonitorRole.Gpu => "GPU",
        MonitorRole.Download => Loc.T("下载", "Download"), MonitorRole.Upload => Loc.T("上传", "Upload"), MonitorRole.Storage => Loc.T("硬盘", "Drives"),
        MonitorRole.Power => Loc.T("整机功耗", "System power"), MonitorRole.Battery => Loc.T("本机电池", "Battery"), MonitorRole.Frames => Loc.T("帧率", "Frame rate"), _ => "CPU"
    };
    private static string LevelName(MemoryLevel level) => level == MemoryLevel.Critical ? Loc.T("告警", "Critical") : Loc.T("预警", "Warning");

    // Only CPU and the efficiency cores say where they show, as both color the core columns; the others' names say it.
    private static string? RoleHelp(MonitorRole role) => role switch
    {
        MonitorRole.Cpu => Loc.T("CPU 读数与核心柱中性能核的颜色。", "The color of the CPU readings and of the performance cores in the core columns."),
        MonitorRole.Efficiency => Loc.T("核心柱中能效核的颜色。", "The color of the efficiency cores in the core columns."),
        _ => null
    };

    // What the collapsed notch or capsule shows when its "系统监控" switch is on: a table of the readings, laid out as the app list
    // is (drag handle, the reading's colors from the scheme, switches), CPU, GPU and the drives each with a menu of what they
    // show, with Full and Brief under it as quick presets, then the readings' warning and critical colors; and whether each
    // surface widens to its content.
    private Action? _refreshReadings;
    private const double ReadingRowHeight = 53;
    private void CompactMonitorOptions()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        // Shows what the settings hold in place, without saving anything: the preset the table matches, the switches, the colors.
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } }
        _refreshReadings = Refresh;
        void Edit(Action<DeckSettings> edit) { if (syncing) return; Change(edit); Refresh(); }
        SectionTitle(Loc.T("收起时", "When collapsed"));

        var rows = new StackPanel();
        foreach (var reading in S.CompactReadings) rows.Children.Add(ReadingRow(reading.Item, rows, refresh, Edit));
        UpdateDividers(rows);
        var headings = ReadingColumns(); headings.Height = 34;
        AddHeadings(headings, new[]
        {
            (Loc.T("监控项", "Item"), 2, ""),
            (Loc.T("显示数值", "Value"), 3, ""),
            (Loc.T("显示", "Show"), 4, "")
        });
        // The names in the rows start at the left of their column, so their heading does too rather than floating in its middle.
        var nameHeading = headings.Children.OfType<FrameworkElement>().First(heading => Grid.GetColumn(heading) == 2);
        nameHeading.HorizontalAlignment = HorizontalAlignment.Left; nameHeading.Margin = new Thickness(6, 0, 6, 0);
        var table = new StackPanel();
        table.Children.Add(headings);
        table.Children.Add(new Border { Child = rows, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        // Quick presets under the table, as the quick sizes: one sets which readings show and which rings carry their figure, never
        // the order or the colors. The one the table matches exactly is lit, else none; the table itself is the custom choice.
        // Each segment keeps the width of a quick size's.
        var presets = new UniformGrid { Columns = 2, Width = 196 };
        foreach (var (mode, label, help) in new[]
        {
            (CompactMonitorMode.Full, Loc.T("完整", "Full"), Loc.T("显示全部监控项和全部数值。不改变硬盘的盘符、顺序和颜色。", "Shows every reading with every figure. The drive letters, the order and the colors stay.")),
            (CompactMonitorMode.Brief, Loc.T("简洁", "Brief"), Loc.T("只显示 CPU、内存和 GPU 的圆环，不带数值，隐藏硬盘。不改变顺序和颜色。", "Shows only the CPU, memory and GPU rings, without figures, and hides the drives. The order and the colors stay."))
        })
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = label, GroupName = "CompactMonitorPreset" };
            AutomationProperties.SetName(choice, Loc.T("快捷预设 ", "Preset ") + label); AutomationProperties.SetHelpText(choice, help);
            choice.Checked += (_, _) => { if (!syncing) Edit(s => CompactMonitor.Apply(s.CompactReadings, mode)); };
            refresh.Add(() => choice.IsChecked = CompactMonitor.Preset(S.CompactReadings) == mode);
            presets.Children.Add(choice);
        }
        var quick = QuickRow(Loc.T("快捷预设", "Presets"), presets);
        // How the dials move to a new reading; the figures always take it at once.
        var smoothing = MusicChoice(Loc.T("平滑过渡", "Smoothing"), S.CompactSmoothing,
            [(CompactSmoothing.Off, Loc.T("不平滑", "Off")), (CompactSmoothing.Ten, Loc.T("10 帧", "10 fps")), (CompactSmoothing.Thirty, Loc.T("30 帧", "30 fps"))],
            v => Change(s => s.CompactSmoothing = v), help: Loc.T("圆环在两次读数之间渐变到新数值；关掉则直接跳到新数值。数值始终立即更新。",
                "The dials ease to each new figure between readings; off, they jump to it. The figures themselves always update at once."));
        Refresh();
        Group(WithFrameAccess(table, () => Reading(S, CompactMonitorItems.Frames).Visible), quick, smoothing);
    }

    private static Grid ReadingColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        // The handle, the reading's color dots (one per channel, CompactMonitor.Channels), the name, the two switches.
        foreach (var width in new[] { new GridLength(40), new GridLength(ReadingDot * CompactMonitor.Order.Max(CompactMonitor.Channels) + 3 + 6),
            new GridLength(1, GridUnitType.Star), new GridLength(Loc.IsEnglish ? 76 : 84), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    // The note of a level's color column, from the thresholds the levels are read with (ReadingLevels, MemorySample.Level).
    private static string LevelHelp(MemoryLevel level)
    {
        bool critical = level == MemoryLevel.Critical;
        double share = critical ? ReadingLevels.CriticalShare : ReadingLevels.ElevatedShare,
            commit = critical ? MemorySample.CriticalCommit : MemorySample.ElevatedCommit,
            rate = (critical ? ReadingLevels.CriticalRate : ReadingLevels.ElevatedRate) / 1_000_000;
        // The memory commit charge is always named: by its own threshold where it differs from the shares', else as part of memory.
        bool apart = commit != share;
        double cpu = critical ? Temperatures.CpuCritical : Temperatures.CpuWarning, gpu = critical ? Temperatures.GpuCritical : Temperatures.GpuWarning;
        return Loc.T($"占用达到 {share * 100:0}%{(apart ? $"（内存提交量为 {commit * 100:0}%）" : "（内存包括内存提交量）")}、网速达到 {rate:0} MB/s，或 CPU 温度达到 {cpu:0} °C、GPU 温度达到 {gpu:0} °C 时的颜色；硬盘按其自身报告的{(critical ? "临界" : "警告")}温度。",
            $"The color when usage reaches {share * 100:0}%{(apart ? $" ({commit * 100:0}% for the memory commit charge)" : " (for memory, including the commit charge)")}, the network rate reaches {rate:0} MB/s, or the CPU temperature reaches {cpu:0} °C and the GPU's {gpu:0} °C; drives by the {(critical ? "critical" : "warning")} temperature they report.");
    }

    private const double ReadingDot = 10;

    private static string ReadingName(CompactMonitorItems item) => item switch
    {
        CompactMonitorItems.Columns => Loc.T("核心柱", "Core columns"), CompactMonitorItems.Memory => Loc.T("内存", "Memory"), CompactMonitorItems.Gpu => "GPU",
        CompactMonitorItems.Download => Loc.T("下载", "Download"), CompactMonitorItems.Upload => Loc.T("上传", "Upload"),
        CompactMonitorItems.Storage => Loc.T("硬盘", "Drives"), CompactMonitorItems.Power => Loc.T("整机功耗", "System power"),
        CompactMonitorItems.Charge => Loc.T("本机电池", "This PC's battery"), CompactMonitorItems.Frames => Loc.T("帧率", "Frame rate"), _ => "CPU"
    };
    private static CompactReading Reading(DeckSettings settings, CompactMonitorItems item) => settings.CompactReadings.First(reading => reading.Item == item);

    // One row of the readings' table; its switches follow the settings when a preset changes them (refresh).
    private Border ReadingRow(CompactMonitorItems item, StackPanel rows, List<Action> refresh, Action<Action<DeckSettings>> edit)
    {
        string name = ReadingName(item);
        var row = ReadingColumns();
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = TextLine(name, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(label, 2); row.Children.Add(label);
        // CPU, GPU and the drives choose what they show in a menu at the right of their name.
        if (CompactFigures.Has(item))
        {
            var menu = FiguresMenu(item, refresh, edit);
            Grid.SetColumn(menu, 2); row.Children.Add(menu);
        }
        // The frame rate chooses what it shows of its program in a menu at the same place.
        if (item == CompactMonitorItems.Frames)
        {
            var menu = FramesPartsMenu(refresh, edit);
            Grid.SetColumn(menu, 2); row.Children.Add(menu);
        }
        // The reading's colors in the normal state as the scheme gives them, one per channel (the core columns: one per kind of
        // core). They are set under "各项颜色", so they only show here.
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            ToolTip = Loc.T("颜色在上方“各项颜色”中设置。", "Set under Reading colors above.") };
        var fills = Enumerable.Range(0, CompactMonitor.Channels(item)).Select(channel =>
            new Ellipse { Width = ReadingDot, Height = ReadingDot, Margin = new Thickness(channel == 0 ? 0 : 3, 0, 0, 0) }).ToArray();
        foreach (var fill in fills) dots.Children.Add(fill);
        refresh.Add(() =>
        {
            var colors = MonitorThemeColors.For(S);
            for (int channel = 0; channel < fills.Length; channel++) fills[channel].Fill = UI.Brush(RoleColor(colors, CompactMonitor.Role(item, channel)));
        });
        Grid.SetColumn(dots, 1); row.Children.Add(dots);

        // The figure switch is for the rings only; the core columns (no figure) and the rates (always text) show it off and dimmed.
        bool choice = CompactMonitor.HasValueChoice(item);
        var value = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = choice, IsChecked = item is CompactMonitorItems.Download or CompactMonitorItems.Upload or CompactMonitorItems.Storage or CompactMonitorItems.Power or CompactMonitorItems.Charge or CompactMonitorItems.Frames };
        AutomationProperties.SetName(value, Loc.T("显示数值 ", "Show figure ") + name);
        if (!choice)
        {
            value.ToolTip = item switch
            {
                CompactMonitorItems.Columns => Loc.T("核心柱没有数值。", "The core columns have no figure."),
                CompactMonitorItems.Storage => Loc.T("硬盘始终显示数值，在左侧的菜单中选择显示什么。", "The drives always show figures; choose which in the menu at the left."),
                CompactMonitorItems.Power => Loc.T("整机功耗，单位 W；是否为估算见悬停提示。无读数的电脑不显示。", "System power in W; the tooltip says whether it is an estimate. Hidden on PCs with no reading."),
                CompactMonitorItems.Charge => Loc.T("本机电池电量，以电池图形显示。无电池的电脑不显示。", "This PC's battery charge, shown as a battery icon. Hidden on PCs without a battery."),
                CompactMonitorItems.Frames => Loc.T("前台程序每秒绘制的帧数；前台程序不在绘制时，显示最近一个在绘制的程序。首次开启时需要授权，未授权时不显示。",
                    "The frames the program in front draws each second; while it draws nothing, the last program that did. Needs permission the first time; hidden until then."),
                _ => Loc.T("下载和上传始终显示速率。", "Download and upload always show their rate.")
            };
            ToolTipService.SetShowOnDisabled(value, true);
        }
        else
        {
            value.Checked += (_, _) => edit(s => Reading(s, item).ShowValue = true); value.Unchecked += (_, _) => edit(s => Reading(s, item).ShowValue = false);
            refresh.Add(() => value.IsChecked = Reading(S, item).ShowValue);
        }
        Grid.SetColumn(value, 3); row.Children.Add(value);

        var shown = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(shown, Loc.T("显示 ", "Show ") + name);
        shown.Checked += (_, _) =>
        {
            // Only a frame rate the user turns on asks for permission, not one the table shows as on.
            bool was = Reading(S, item).Visible;
            edit(s => Reading(s, item).Visible = true);
            if (item == CompactMonitorItems.Frames && !was) AskFrameAccess();
        };
        shown.Unchecked += (_, _) => { edit(s => Reading(s, item).Visible = false); if (item == CompactMonitorItems.Frames) FrameAccessChanged?.Invoke(); };
        refresh.Add(() => shown.IsChecked = Reading(S, item).Visible);
        Grid.SetColumn(shown, 4); row.Children.Add(shown);

        // Dropped in a new place, the strip takes the new order at once.
        AttachDrag(handle, name, () => rows, ReadingRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactMonitorItems).Select(b => (CompactMonitorItems)b.Tag).ToArray();
            Change(s => s.CompactReadings = [.. order.Select(id => Reading(s, id))]);
        });
        return new Border { Child = row, Height = ReadingRowHeight, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = item, RenderTransform = new TranslateTransform() };
    }

    // The menu of what CPU, GPU or the drives show after their glyph: the share in use, the temperature or both, at least one, and
    // whether both show one above the other (the default) or side by side; for the drives also which lettered volumes, at least one. A chosen letter not found now (a drive taken
    // out) is listed as not connected. The drives come from the sampler's last reading, or, when it has none, are read once as the
    // menu opens. The menu is as wide as its button.
    private Button FiguresMenu(CompactMonitorItems item, List<Action> refresh, Action<Action<DeckSettings>> edit)
    {
        bool drives = item == CompactMonitorItems.Storage;
        string name = ReadingName(item), role = Loc.T($"{name}显示的数值", $"Figures shown for {name}");
        const double Wide = 150;
        var select = ActionButton("", () => { });
        var content = new DockPanel();
        var chevron = Chevron(); chevron.Foreground = UI.Brush(TextSecondary); chevron.Margin = new Thickness(8, ChevronDrop.Top, 0, ChevronDrop.Bottom); DockPanel.SetDock(chevron, Dock.Right); content.Children.Add(chevron);
        var summary = TextLine("", 12.5, TextPrimary); summary.VerticalAlignment = VerticalAlignment.Center; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(summary);
        select.Content = content; select.Height = 30; select.Width = Wide;
        select.HorizontalAlignment = HorizontalAlignment.Right; select.HorizontalContentAlignment = HorizontalAlignment.Stretch; select.VerticalAlignment = VerticalAlignment.Center;
        select.Padding = new Thickness(9, 0, 9, 0); select.Margin = new Thickness(6, 0, 6, 0);
        select.Background = UI.Brush(_palette.Input); select.BorderBrush = UI.Brush(_palette.InputBorder);
        ToolTipService.SetShowOnDisabled(select, true);
        AutomationProperties.SetName(select, role);

        static string FigureName(string figure) => figure == CompactFigures.Usage ? Loc.T("占用", "Usage") : Loc.T("温度", "Temperature");
        refresh.Add(() =>
        {
            var reading = Reading(S, item);
            var figures = string.Join(Loc.T("、", ", "), CompactFigures.Of(reading).Select(FigureName));
            summary.Text = drives ? string.Join(" ", CompactSources.Of(reading)) + " · " + figures : figures;
            // A ring set to show no figure shows none of these either.
            select.IsEnabled = drives || reading.ShowValue;
            select.ToolTip = select.IsEnabled ? role + Loc.T("：", ": ") + summary.Text : Loc.T("先打开“显示数值”。", "Switch Value on first.");
        });

        IReadOnlyList<StorageDrive>? listed = _app.Monitor?.Latest?.Storage is { Count: > 0 } latest ? latest : null;
        List<(string Letter, string Label)> Letters()
        {
            var chosen = CompactSources.Of(Reading(S, item));
            var options = new List<(string, string)>();
            foreach (var volume in (listed ?? []).SelectMany(drive => drive.Volumes))
                options.Add((volume.Letter.ToUpperInvariant(), volume.Label.Length > 0 ? volume.Letter + " " + volume.Label : volume.Letter));
            foreach (var letter in chosen.Where(letter => options.All(option => option.Item1 != letter)))
                options.Add((letter, listed is null ? letter : letter + Loc.T("（未连接）", " (not connected)")));
            return [.. options.OrderBy(option => CompactSources.Rank(option.Item1))];
        }

        var menu = new Popup { PlacementTarget = select, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var items = new StackPanel();
        var list = new ScrollViewer
        {
            Style = (Style)FindResource("SettingsScrollViewer"), Content = items, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var surface = new Border
        {
            Child = list, Width = Wide, Background = UI.Brush(_palette.Popup), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4)
        };
        menu.Child = surface;
        // One line: a check when chosen, then the label. The last one chosen of its group stays; blocked: why it cannot be chosen now.
        void Entry(string label, bool on, bool last, string? help, Action toggle, string? blocked = null)
        {
            var line = new DockPanel();
            line.Children.Add(new TextBlock
            {
                Text = on ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Width = 20,
                Foreground = UI.Brush(TextPrimary), VerticalAlignment = VerticalAlignment.Center
            });
            var text = TextLine(label, 12.5, TextPrimary); text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(text);
            var entry = ActionButton("", () => { }); entry.Content = line;
            entry.HorizontalContentAlignment = HorizontalAlignment.Stretch; entry.Padding = new Thickness(7, 3, 7, 3);
            entry.IsEnabled = !last && blocked is null;
            entry.ToolTip = last ? Loc.T("至少保留一项。", "At least one stays.") : blocked ?? help;
            ToolTipService.SetShowOnDisabled(entry, true);
            AutomationProperties.SetName(entry, label); AutomationProperties.SetHelpText(entry, on ? Loc.T("已选", "Chosen") : Loc.T("未选", "Not chosen"));
            entry.Click += (_, _) => { toggle(); Fill(); };
            items.Children.Add(entry);
        }
        void Header(string text)
        {
            if (items.Children.Count > 0) items.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(4, 3, 4, 2) });
            var header = TextLine(text, 10.5, TextTertiary); header.Margin = new Thickness(8, 2, 8, 1); items.Children.Add(header);
        }
        void Fill()
        {
            items.Children.Clear();
            var reading = Reading(S, item);
            var figures = CompactFigures.Of(reading);
            Header(Loc.T("数值", "Figures"));
            foreach (var figure in new[] { CompactFigures.Usage, CompactFigures.Temperature })
            {
                bool on = figures.Contains(figure);
                string? help = figure == CompactFigures.Temperature
                    ? drives ? Loc.T("固态硬盘的温度，显示在已用空间下方；机械硬盘不显示。", "A solid-state drive's temperature, under its space in use; not shown for hard disks.")
                        : Loc.T("显示在占用下方；读不到温度的电脑上不显示。", "Shown under the usage; not shown on a PC whose temperature cannot be read.")
                    : null;
                Entry(FigureName(figure), on, on && figures.Count == 1, help, () =>
                {
                    var next = CompactFigures.Of(Reading(S, item)).ToList();
                    if (!next.Remove(figure)) next.Add(figure);
                    edit(s => Reading(s, item).Figures = CompactFigures.Normalize(item, next));
                });
            }
            if (!drives) return;
            Header(Loc.T("盘符", "Letters"));
            var chosen = CompactSources.Of(reading);
            foreach (var (letter, label) in Letters())
            {
                bool on = chosen.Contains(letter);
                Entry(label, on, on && chosen.Count == 1, null, () =>
                {
                    var next = CompactSources.Of(Reading(S, item)).ToList();
                    if (!next.Remove(letter)) next.Add(letter);
                    edit(s => Reading(s, item).Sources = CompactSources.Normalize(item, next));
                });
            }
            if (listed is null)
            {
                var note = TextLine(Loc.T("正在读取硬盘…", "Reading the drives…"), 11.5, TextTertiary); note.Margin = new Thickness(8, 4, 8, 4);
                items.Children.Add(note);
            }
        }
        // The menu is filled before it opens: filled as it opened, it grew while dropping into place (Soft.Enter), and WPF placed
        // it again with the drop's offset, six DIPs below its button.
        if (drives && listed is null)
        {
            // Nothing read the drives yet: they are read once here, off the UI thread, while the page is open.
            var reader = new Native.StorageReader();
            reader.Start(false);
            reader.Pending.ContinueWith(_ => Dispatcher.BeginInvoke(() => { listed = reader.Listed; if (menu.IsOpen) Fill(); }));
        }
        // The menu sits right against its button rather than the app list's four DIPs below it.
        select.Click += (_, _) =>
        {
            if (menu.IsOpen) return;
            Fill();
            FitMenuToScreen(select, menu, surface);
            menu.VerticalOffset = 0;
        };
        TogglePopup(select, menu);
        return select;
    }

    // Whether the collapsed frame rate shows the icon of the program it counts (CompactFrames): the icon (the default), or the screen
    // glyph. As wide as the other readings' menus.
    private ComboBox FramesPartsMenu(List<Action> refresh, Action<Action<DeckSettings>> edit)
    {
        var options = new (string[] Parts, string Label)[]
        {
            ([CompactFrames.Icon], Loc.T("显示程序图标", "Program icon")), ([], Loc.T("不显示图标", "No program icon"))
        };
        var choice = new ComboBox { Width = 150, Height = 30, Style = (Style)FindResource("SettingsCombo"), ItemsSource = options.Select(option => option.Label).ToList(),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
            ToolTip = Loc.T("在帧率前显示所统计程序的图标；不显示时用显示器图标。程序名称在展开页显示。",
                "Shows the icon of the program counted before the frame rate; without it, a screen. The program's name shows on the expanded page.") };
        AutomationProperties.SetName(choice, Loc.T("帧率前的程序图标", "Program icon before the frame rate")); PopupMotion.Attach(choice);
        bool syncing = false;
        refresh.Add(() =>
        {
            syncing = true;
            try { var parts = CompactFrames.Of(Reading(S, CompactMonitorItems.Frames)); choice.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Parts.SequenceEqual(parts))); }
            finally { syncing = false; }
        });
        choice.SelectionChanged += (_, _) =>
        {
            if (syncing || choice.SelectedIndex < 0) return;
            var parts = options[choice.SelectedIndex].Parts;
            if (parts.SequenceEqual(CompactFrames.Of(Reading(S, CompactMonitorItems.Frames)))) return;
            edit(s => Reading(s, CompactMonitorItems.Frames).Figures = [.. parts]);
        };
        return choice;
    }

    private void MonitorSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MonitorWidth, 440, 1200, value => Resize(s => s.MonitorWidth = value, DeckPage.Monitor)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MonitorHeight, 140, 400, value => Resize(s => s.MonitorHeight = value, DeckPage.Monitor)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MonitorSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MonitorSize", IsChecked = (S.MonitorWidth, S.MonitorHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("系统监控快捷尺寸 ", "System monitor quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.MonitorWidth = w; s.MonitorHeight = h; }, DeckPage.Monitor); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
