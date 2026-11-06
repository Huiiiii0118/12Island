using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;
using TwelveIsland.Native;
using Microsoft.Win32;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    // Whether the exception list is open. Kept while the page is rebuilt, so adding or removing an app leaves it as it was.
    private bool _exceptionsOpen;
    // Holds the row, so that adding or removing an app rebuilds the row alone; rebuilding the page makes the window flash.
    private Border? _exceptionsHost;
    // Tallest height the open list has reached on this visit. Removing an app or closing the row keeps this space so the
    // page does not jump; it is cleared when the settings page is left, the window closes, or the row scrolls out of view.
    private double _exceptionsReservedHeight;
    // Executable descriptions read for the list and the menu, by path; the row is rebuilt on every change.
    private static readonly Dictionary<string, string> AppDescriptions = new(StringComparer.OrdinalIgnoreCase);

    // The apps that keep the panel's style while they cover the screen, as the last row of the window rules. The row opens
    // like the clock row: a click on the row or its arrow shows the apps, set in under the title. With no apps there is
    // nothing to open, so the arrow is left out. Adding from the running apps is the shortest path: the app that causes the
    // problem is usually in front of the user, and its executable name is not obvious.
    private FrameworkElement ExceptionsRow()
    {
        _exceptionsHost = new Border { Child = ExceptionsContent() };
        _scroll.ScrollChanged -= ExceptionsScrollChanged;
        _scroll.ScrollChanged += ExceptionsScrollChanged;
        return _exceptionsHost;
    }

    private void RefreshExceptions()
    {
        if (_exceptionsHost is not null) _exceptionsHost.Child = ExceptionsContent();
    }

    // Leaves the reserved height when the row is fully above or below the page's scroll viewport.
    private void ExceptionsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_exceptionsReservedHeight <= 0 || _exceptionsHost is not { IsLoaded: true } host) return;
        try
        {
            var top = host.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
            var bottom = top + host.ActualHeight;
            if (bottom < 0 || top > _scroll.ViewportHeight) ClearExceptionsReservation();
        }
        catch (InvalidOperationException) { }
    }

    private void ClearExceptionsReservation()
    {
        if (_exceptionsReservedHeight <= 0 && !_exceptionsOpen) return;
        _exceptionsReservedHeight = 0;
        _exceptionsOpen = false;
        _scroll.ScrollChanged -= ExceptionsScrollChanged;
        RefreshExceptions();
    }

    private FrameworkElement ExceptionsContent()
    {
        string title = Loc.T("应用程序规则", "App rules");
        var exceptions = S.FullscreenRules;
        // With no apps there is nothing open: a leftover open flag would expand the list the moment the first app is added.
        // A reserved height from earlier on this visit is kept so the page does not jump.
        if (exceptions.Count == 0) _exceptionsOpen = false;
        var list = new StackPanel { Margin = new Thickness(20, 0, 0, 6) };
        foreach (var exception in exceptions)
        {
            list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            list.Children.Add(ExceptionRow(exception));
        }
        // The list's slot: its minimum height is the reserved height, so removing an app or closing the row leaves empty
        // space instead of pulling the rest of the page up.
        var space = new Border { Child = list, MinHeight = _exceptionsReservedHeight };
        var label = TextLine(title, 13, TextPrimary, FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center;
        var info = HelpIcon(title, () => Loc.T("为单个应用分别指定三种情境下面板的行为；选“跟随全局”的情境沿用上面的三项设置。",
            "Chooses what the panel does in each of the three contexts for one app; a context left on Follow follows the three settings above."), 14);
        // Pointing at the icon shows the description; a click on it should not also open the row.
        info.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new HelpHeading(label, info));
        Button? arrow = null;
        void CaptureHeight()
        {
            if (list.Visibility != Visibility.Visible) return;
            space.UpdateLayout();
            if (space.ActualHeight > _exceptionsReservedHeight) _exceptionsReservedHeight = space.ActualHeight;
            space.MinHeight = _exceptionsReservedHeight;
        }
        void Show(bool open)
        {
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            space.MinHeight = _exceptionsReservedHeight;
            if (arrow is not null) PointArrow(arrow, open);
            if (open) Dispatcher.BeginInvoke(CaptureHeight, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        void Flip()
        {
            if (_exceptionsOpen) CaptureHeight();
            _exceptionsOpen = !_exceptionsOpen;
            Show(_exceptionsOpen);
        }
        if (exceptions.Count > 0)
        {
            arrow = ChevronButton(Loc.T("展开应用程序规则", "Show app rules"), Flip);
            arrow.Margin = new Thickness(2, 0, 0, 0);
            heading.Children.Add(arrow);
        }
        int n = exceptions.Count;
        var count = TextLine(n == 0 ? Loc.T("未添加", "None") : Loc.T($"{n} 个应用", n == 1 ? "1 app" : $"{n} apps"), 12, TextTertiary);
        count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0, 0, 12, 0);
        var add = new Button { Style = (Style)FindResource("SettingsOutlineButton"), VerticalAlignment = VerticalAlignment.Center };
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        addContent.Children.Add(new TextBlock { Text = Loc.T("添加应用", "Add app"), VerticalAlignment = VerticalAlignment.Center });
        var addChevron = Chevron(); addChevron.Margin = new Thickness(6, 1, 0, -1); addContent.Children.Add(addChevron);
        add.Content = addContent;
        AutomationProperties.SetName(add, Loc.T("添加应用程序规则", "Add an app rule"));
        add.Click += (_, _) => ExceptionMenu(add);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(count); controls.Children.Add(add);
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading); Grid.SetColumn(controls, 1); header.Children.Add(controls);
        // The buttons handle their own clicks; a click anywhere else on the row opens or closes it, as the clock row does.
        header.MouseLeftButtonDown += (_, e) => { if (arrow is not null) Flip(); e.Handled = true; };
        Show(_exceptionsOpen && n > 0);
        var row = new StackPanel(); row.Children.Add(header); row.Children.Add(space); return row;
    }

    // One app under the row: its icon, the name its executable gives, the file name, and under them one behaviour pick
    // per context, each offering the global rules' three behaviours and a fourth choice that follows them.
    private FrameworkElement ExceptionRow(FullscreenRule rule)
    {
        string name = AppName(rule.Name, rule.Path);
        string key = ScreenPolicy.Key(rule);
        int At(DeckSettings settings) => settings.FullscreenRules.FindIndex(entry => string.Equals(ScreenPolicy.Key(entry), key, StringComparison.OrdinalIgnoreCase));
        var column = new StackPanel();
        var row = new Grid { MinHeight = 40, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (rule.Path.Length > 0) row.ToolTip = rule.Path;
        row.Children.Add(AppIcon(rule.Path));
        var labels = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); labels.ColumnDefinitions.Add(new ColumnDefinition());
        var title = TextLine(name, 13, TextPrimary); title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var file = TextLine(FileName(rule.Name, rule.Path), 11.5, TextTertiary);
        file.VerticalAlignment = VerticalAlignment.Center; file.Margin = new Thickness(8, 1, 0, 0); file.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(file, 1);
        labels.Children.Add(title); labels.Children.Add(file);
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        var remove = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Width = 24, Height = 26, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        var cross = Stroke("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, TextSecondary);
        cross.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        remove.Content = cross;
        AutomationProperties.SetName(remove, Loc.T("移除 ", "Remove ") + name);
        remove.Click += (_, _) => { Change(s => s.FullscreenRules.RemoveAll(entry => string.Equals(ScreenPolicy.Key(entry), key, StringComparison.OrdinalIgnoreCase))); RefreshExceptions(); };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        column.Children.Add(row);
        var picks = new StackPanel { Margin = new Thickness(26, 0, 0, 8) };
        picks.Children.Add(RulePick(name, Loc.T("窗口最大化时", "Maximized"), rule.Maximized,
            choice => Change(s => { var i = At(s); if (i >= 0) s.FullscreenRules[i] = s.FullscreenRules[i] with { Maximized = choice }; })));
        picks.Children.Add(RulePick(name, Loc.T("无边框全屏时", "Borderless full screen"), rule.Borderless,
            choice => Change(s => { var i = At(s); if (i >= 0) s.FullscreenRules[i] = s.FullscreenRules[i] with { Borderless = choice }; })));
        picks.Children.Add(RulePick(name, Loc.T("独占全屏时", "Exclusive full screen"), rule.Exclusive,
            choice => Change(s => { var i = At(s); if (i >= 0) s.FullscreenRules[i] = s.FullscreenRules[i] with { Exclusive = choice }; })));
        column.Children.Add(picks);
        return column;
    }

    // One context's pick: the label, then a four-way segment — Follow and the three behaviours the global rules offer.
    private FrameworkElement RulePick(string app, string title, WindowBehavior? selected, Action<WindowBehavior?> changed)
    {
        var options = new UniformGrid { Columns = 4, Width = 428 };
        foreach (var choice in new (WindowBehavior? Value, string Label)[]
        {
            (null, Loc.T("跟随全局", "Follow")), (WindowBehavior.Normal, BehaviorName(WindowBehavior.Normal)),
            (WindowBehavior.Line, BehaviorName(WindowBehavior.Line)), (WindowBehavior.Hide, BehaviorName(WindowBehavior.Hide))
        })
        {
            var option = new RadioButton { Content = choice.Label, Style = (Style)FindResource("SettingsSegment"), GroupName = "Rule" + app + title, IsChecked = selected == choice.Value };
            AutomationProperties.SetName(option, app + " · " + title + " · " + choice.Label);
            WindowBehavior? picked = choice.Value;
            option.Checked += (_, _) => changed(picked);
            options.Children.Add(option);
        }
        return Row(title, SegmentBorder(options));
    }

    // Runs as the button's menu, so the menu is anchored to the button that opened it.
    private void ExceptionMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        // A menu opened from code is not in the window's tree and would take the application's dark menu colors; it takes
        // the settings palette instead, as the settings' drop-down lists do.
        menu.Resources["MenuSurface"] = UI.Brush(_palette.Popup);
        menu.Resources["MenuInk"] = UI.Brush(_palette.Primary);
        menu.Resources["MenuEdge"] = UI.Brush(_palette.Border);
        menu.Resources["MenuHover"] = UI.Brush(_palette.Dark ? "#25252B" : "#E8E9EF");
        menu.Resources["MenuRule"] = UI.Brush(_palette.Divider);
        var apps = RunningApps.WithWindows()
            .Where(app => !S.FullscreenRules.Any(exception => ScreenPolicy.Matches(exception, app.Name, app.Path)))
            .Select(app => (App: app, Name: AppName(app.Name, app.Path)))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var (app, name) in apps)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(AppIcon(app.Path));
            line.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = FileName(app.Name, app.Path), Opacity = .55, FontSize = 11.5, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = line, ToolTip = app.Path.Length > 0 ? app.Path : null };
            AutomationProperties.SetName(item, name);
            item.Click += (_, _) => AddException(new FullscreenRule(app.Name, app.Path)
                { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal });
            menu.Items.Add(item);
        }
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("没有可添加的应用", "No apps to add"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = Loc.T("浏览可执行文件…", "Browse for an executable…") };
        browse.Click += (_, _) => BrowseException();
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    private void BrowseException()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("选择要豁免的应用", "Choose an app to exempt"),
            Filter = Loc.T("应用程序 (*.exe)|*.exe", "Applications (*.exe)|*.exe"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        AddException(new FullscreenRule(System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName)
            { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal });
    }

    // The list stays open or closed as it was; the count beside the button shows the app was added.
    private void AddException(FullscreenRule rule)
    {
        Change(s => s.FullscreenRules.Add(rule));
        RefreshExceptions();
    }

    // The executable's icon at 16 DIP; an app whose path is unknown keeps the space, so the names stay in line.
    private static FrameworkElement AppIcon(string path)
    {
        ProcessIcons.Prepare(path);
        return new Image { Source = ProcessIcons.For(path), Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    // The executable's description, as Task Manager names apps, or the process name when it has none.
    private static string AppName(string processName, string path)
    {
        if (path.Length == 0) return processName;
        if (AppDescriptions.TryGetValue(path, out var known)) return known.Length > 0 ? known : processName;
        string description = "";
        try
        {
            var text = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length <= 40) description = text;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { }
        AppDescriptions[path] = description;
        return description.Length > 0 ? description : processName;
    }

    private static string FileName(string processName, string path)
        => path.Length > 0 ? System.IO.Path.GetFileName(path) : ScreenPolicy.NormalizeName(processName) + ".exe";
}
