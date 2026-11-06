using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private Border AppNameField(AppEntry? entry)
    {
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left,
            FontSize = 12.5, MaxLength = 64, VerticalAlignment = VerticalAlignment.Center, IsEnabled = entry is not null };
        AutomationProperties.SetAutomationId(input, "display-name-" + entry?.InstanceId.ToString("N"));
        string help = Loc.T("显示在看板上的名称；浅色文字为默认名称，点击后可直接输入，留空恢复配额来源名称",
            "The name shown on the panel. Light text is the default name; click to type a new one, or leave it empty to use the quota source's name");
        AutomationProperties.SetHelpText(input, help);
        var field = new Border { Child = input, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1) };
        bool showingDefault = false;
        void Refresh()
        {
            var current = entry is null ? null : S.Entry(entry.InstanceId);
            showingDefault = !input.IsKeyboardFocused && string.IsNullOrWhiteSpace(current?.DisplayName);
            input.Text = input.IsKeyboardFocused ? current?.DisplayName ?? "" : current?.Name ?? "";
            input.Foreground = UI.Brush(showingDefault ? TextTertiary : TextPrimary);
            AutomationProperties.SetName(input, Loc.T("显示名称 ", "Display name ") + (current?.Name ?? Loc.T("未配置应用", "unconfigured app")));
        }
        void Commit()
        {
            if (entry is not null && S.Entry(entry.InstanceId) is { } current)
            {
                var value = showingDefault ? "" : input.Text.Trim();
                if (value != current.DisplayName) Change(s => s.Entry(entry.InstanceId)!.DisplayName = value);
            }
            Refresh();
        }
        FieldFocus(field, input, () => UI.Brush(_palette.InputBorder));
        input.GotKeyboardFocus += (_, _) => Refresh();
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Refresh(); e.Handled = true; }
        };
        // Padding belongs to the input's click target as well.
        field.MouseLeftButtonDown += (_, e) => { input.Focus(); e.Handled = true; };
        Refresh();
        return field;
    }

    private const double AppRowHeight = 69;
    private AppDrag? _appDrag;
    private readonly Dictionary<Border, long> _appRowMotionVersions = [];
    private long _appRowMotionVersion;
    // A drag in a list of rows of one height (the app list, the collapsed readings); Reordered saves the rows' new order.
    private sealed class AppDrag(Panel list, double height, Action<Panel> reordered, Border[] rows, Border row, Border handle, double grabOffset)
    {
        public Panel List { get; } = list;
        public double Height { get; } = height;
        public Action<Panel> Reordered { get; } = reordered;
        public Border[] Rows { get; } = rows;
        public Border Row { get; } = row;
        public Border Handle { get; } = handle;
        public double GrabOffset { get; } = grabOffset;
        public int From { get; } = Array.IndexOf(rows, row);
        public int Target { get; set; } = Array.IndexOf(rows, row);
    }

    private static TranslateTransform RowTransform(Border row) => (TranslateTransform)row.RenderTransform;
    private void SetRowOffset(Border row, double offset)
    {
        _appRowMotionVersions[row] = ++_appRowMotionVersion;
        var transform = RowTransform(row);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = offset;
    }

    private void AnimateRow(Border row, double target, Action? completed = null, bool animate = true)
    {
        var transform = RowTransform(row);
        double start = transform.Y;
        if (!animate || !S.Animations || !SystemParameters.ClientAreaAnimation || Math.Abs(start - target) < .1)
        {
            SetRowOffset(row, target);
            completed?.Invoke();
            return;
        }
        // A new WPF animation clock starts on the next render tick. Keep the
        // current position as the base value so neither that tick nor a rapid
        // direction change can briefly expose the destination position.
        SetRowOffset(row, start);
        long version = _appRowMotionVersions[row];
        // The monitor's glide between choices, whatever the panel's animation length (motion-review C5).
        var motion = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(Soft.Choose))
        {
            EasingFunction = Soft.Out
        };
        motion.Completed += (_, _) =>
        {
            // Replaced clocks can still complete; they must not move a row
            // that has since been dragged, reversed or returned on cancellation.
            if (_appRowMotionVersions.GetValueOrDefault(row) != version) return;
            SetRowOffset(row, target); completed?.Invoke();
        };
        transform.BeginAnimation(TranslateTransform.YProperty, motion);
    }

    private static void ResetDragAppearance(Border row)
    {
        row.Background = Brushes.Transparent;
        Panel.SetZIndex(row, 0);
    }

    private void AttachDrag(Border handle, string name) => AttachDrag(handle, name, () => _appRows, AppRowHeight, rows =>
    {
        var order = rows.Children.OfType<Border>().Where(b => b.Tag is AppEntry).Select(b => ((AppEntry)b.Tag).InstanceId).ToArray();
        Change(s => s.Apps = order.Select(id => s.Entry(id)!).ToList());
    }, CollapseSourceRows);

    // A row's drag handle (⠿). A Border has no automation peer; this one reads as a thumb named for its row and how to move it.
    private sealed class GripHandle : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Thumb, () => AutomationProperties.GetName(this));
    }

    // The rows are the list's children with a Tag, each a Border holding a Grid that holds the handle. name: the row's, for the
    // handle's spoken name.
    private void AttachDrag(Border handle, string name, Func<Panel?> list, double height, Action<Panel> reordered, Action? starting = null)
    {
        AutomationProperties.SetName(handle, Loc.T($"{name}：拖动，或在本行按 Alt+↑/↓ 调整顺序", $"{name}: drag, or press Alt+Up/Down in its row, to reorder"));
        // Focusable so that pressing it commits a name being edited. Tab skips it: the keyboard moves a row from any control in it.
        handle.Focusable = true; KeyboardNavigation.SetIsTabStop(handle, false); handle.FocusVisualStyle = null;
        // WCAG 2.5.7, the keyboard's way to reorder (SW-03): with the focus anywhere in the row, Alt+Up or Alt+Down moves the row one
        // place, the two rows gliding as after a drag, and a screen reader hears where it went.
        void Move(int delta)
        {
            if (_appDrag is not null || list() is not { } rows || handle.Parent is not Grid { Parent: Border row }) return;
            var items = rows.Children.OfType<Border>().Where(b => b.Tag is not null).ToArray();
            int from = Array.IndexOf(items, row), to = from + delta;
            if (from < 0) return;
            if (to < 0 || to >= items.Length) { Announce(to < 0 ? Loc.T("已在最前", "Already first") : Loc.T("已在最后", "Already last")); return; }
            starting?.Invoke();
            RowFocus.Keep(row, handle);
            var other = items[to];
            rows.Children.Remove(row);
            rows.Children.Insert(rows.Children.IndexOf(other) + (delta > 0 ? 1 : 0), row);
            UpdateDividers(rows);
            SetRowOffset(row, -delta * height); AnimateRow(row, 0);
            SetRowOffset(other, delta * height); AnimateRow(other, 0);
            reordered(rows);
            Announce(Loc.T($"已移到第 {to + 1} 位，共 {items.Length} 项", $"Moved to position {to + 1} of {items.Length}"));
        }
        void Announce(string text)
        {
            if (Keyboard.FocusedElement is not UIElement focused) focused = handle;
            (UIElementAutomationPeer.FromElement(focused) ?? UIElementAutomationPeer.CreatePeerForElement(focused))?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "TwelveIsland.Reorder");
        }
        bool listening = false;
        handle.Loaded += (_, _) =>
        {
            if (listening || handle.Parent is not Grid line) return;
            listening = true;
            line.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down)) return;
                Move(e.SystemKey == Key.Up ? -1 : 1); e.Handled = true;
            };
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (list() is not { } rows || handle.Parent is not Grid { Parent: Border row }) return;
            handle.Focus(); // Commits any name being edited before beginning the drag.
            StartAppDrag(rows, height, reordered, starting, row, handle, e.GetPosition(rows).Y);
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (_appDrag?.Handle != handle || !handle.IsMouseCaptured) return;
            UpdateAppDrag(e.GetPosition(_appDrag.List).Y);
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_appDrag?.Handle != handle) return;
            EndAppDrag(true); e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) => { if (_appDrag?.Handle == handle) EndAppDrag(false); };
    }

    private void StartAppDrag(Panel list, double height, Action<Panel> reordered, Action? starting, Border row, Border handle, double pointerY)
    {
        EndAppDrag(false, false);
        starting?.Invoke();
        var rows = list.Children.OfType<Border>().Where(b => b.Tag is not null).ToArray();
        foreach (var item in rows) ResetDragAppearance(item);
        int from = Array.IndexOf(rows, row);
        if (from < 0) return;
        _appDrag = new AppDrag(list, height, reordered, rows, row, handle, pointerY - from * height - RowTransform(row).Y);
        // Keep the visual tree and mouse capture intact until release. Only transforms move during a drag.
        SetRowOffset(row, RowTransform(row).Y);
        row.Background = UI.Brush(Inset); row.CornerRadius = new CornerRadius(6); Panel.SetZIndex(row, 2);
        if (!handle.CaptureMouse()) EndAppDrag(false, false);
    }

    internal void UpdateAppDrag(double pointerY)
    {
        if (_appDrag is not { } drag) return;
        double top = Math.Clamp(pointerY - drag.GrabOffset, 0, (drag.Rows.Length - 1) * drag.Height);
        SetRowOffset(drag.Row, top - drag.From * drag.Height);
        int target = Math.Clamp((int)Math.Floor(top / drag.Height + .5), 0, drag.Rows.Length - 1);
        if (target == drag.Target) return;
        drag.Target = target;
        for (int i = 0; i < drag.Rows.Length; i++)
        {
            if (i == drag.From) continue;
            int slot = i;
            if (i > drag.From && i <= target) slot--;
            else if (i < drag.From && i >= target) slot++;
            AnimateRow(drag.Rows[i], (slot - i) * drag.Height);
        }
    }

    internal void EndAppDrag(bool commit, bool animate = true)
    {
        if (_appDrag is not { } drag) return;
        _appDrag = null;
        if (drag.Handle.IsMouseCaptured) drag.Handle.ReleaseMouseCapture();
        var list = drag.List;
        var positions = drag.Rows.Select((row, i) => (row, y: i * drag.Height + RowTransform(row).Y)).ToArray();
        bool changed = commit && drag.From != drag.Target;
        if (changed)
        {
            list.Children.Remove(drag.Row);
            list.Children.Insert(drag.Target, drag.Row);
            UpdateDividers(list);
        }
        foreach (var (row, y) in positions)
        {
            SetRowOffset(row, y - list.Children.IndexOf(row) * drag.Height);
            AnimateRow(row, 0, () => { if (_appDrag?.Row != row) ResetDragAppearance(row); }, animate);
        }
        if (changed) drag.Reordered(list);
    }
}
