using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TwelveIsland.Core;

namespace TwelveIsland;

// Size and position of the collapsed notch, capsule and indicator. The group sits under the chosen style's content
// options and slides with them when another style card is chosen. The panel at the top of the screen is the preview:
// while a value is adjusted it stays collapsed in the chosen style, even where window rules would show the indicator.
public sealed partial class SettingsWindow
{
    // Updates the offset and top limits of the group on screen; replaced each time the group is built.
    private Action? _refreshCollapsedRanges;
    // Set while limits are applied, so a slider that only shows the clamped position does not save it.
    private bool _updatingCollapsedRanges;

    private sealed class SizeControl
    {
        public required Grid Row;
        public required Slider Slider;
        public required TextBlock Note;
        public Action Refresh = () => { };
    }

    // The notch or the line on a side edge runs down the screen: its width is a length there and its offset vertical.
    private bool Across(CompactStyle style) => style != CompactStyle.Capsule && S.CompactEdge(style) is ScreenEdge.Left or ScreenEdge.Right;

    // The edge the notch or the line hangs from. A new edge builds the page again, for the names of the rows and the anchor row.
    private FrameworkElement EdgeRow(CompactStyle style) => MusicChoice(Loc.T("贴靠位置", "Edge"), S.CompactEdge(style),
        [(ScreenEdge.Top, Loc.T("顶部", "Top")), (ScreenEdge.Bottom, Loc.T("底部", "Bottom")), (ScreenEdge.Left, Loc.T("左侧", "Left")), (ScreenEdge.Right, Loc.T("右侧", "Right"))],
        value =>
        {
            if (S.CompactEdge(style) == value) return;
            Change(s => { if (style == CompactStyle.Line) s.LineEdge = value; else s.NotchEdge = value; });
            ShowPage(1);
        });

    // Whether the notch or the line covers a taskbar on its edge or hangs from its inner edge; shown only where the edge has a
    // taskbar on the main panel's display, as elsewhere the two are the same.
    private FrameworkElement? AnchorRow(CompactStyle style)
    {
        var edge = S.CompactEdge(style);
        if (!_app.Deck.TaskbarOn(edge)) return null;
        var chosen = DeckSettings.Anchor(style, edge, style == CompactStyle.Line ? S.LineAnchor : S.NotchAnchor);
        return MusicChoice(Loc.T("贴在", "Hang from"), chosen, [(EdgeAnchor.Screen, Loc.T("屏幕边缘", "Screen edge")), (EdgeAnchor.Taskbar, Loc.T("任务栏边缘", "Taskbar edge"))],
            value =>
            {
                _builtDock = (style, S.CompactEdge(style), value);
                ChangeCollapsed(s => { if (style == CompactStyle.Line) s.LineAnchor = value; else s.NotchAnchor = value; });
            });
    }

    private void CollapsedSizeOptions(CompactStyle style)
    {
        var reset = KeepPreview(ActionButton(Loc.T("恢复默认尺寸", "Reset to default size"), () => ResetCollapsedSize(style))); reset.FontSize = 11;
        AutomationProperties.SetName(reset, Loc.T("恢复" + StyleName(style) + "的默认尺寸", "Reset " + StyleName(style) + " to default size"));
        SectionTitle(Loc.T("尺寸与位置 · ", "Size and position · ") + StyleName(style), reset);
        switch (style)
        {
            case CompactStyle.Notch:
            {
                bool across = Across(style);
                var edge = EdgeRow(style);
                var auto = AutoWidthRow(style);
                var width = SizeRow(across ? Loc.T("长度", "Length") : Loc.T("宽度", "Width"), S.NotchWidth, CollapsedLimits.NotchWidthMin, CollapsedLimits.NotchWidthMax, 1, v => ChangeCollapsed(s => { s.NotchWidth = v; s.NotchAutoWidth = false; }));
                var height = SizeRow(across ? Loc.T("内容大小", "Content size") : Loc.T("高度", "Height"), S.NotchHeight, CollapsedLimits.NotchHeightMin, CollapsedLimits.NotchHeightMax, 1, v => ChangeCollapsed(s => s.NotchHeight = v),
                    help: across ? Loc.T("贴靠左侧或右侧时，此项决定内容的大小；刘海的宽度随内容自动调整。", "At the left or right edge this sets the size of the content; the notch's width follows the content.") : null);
                var offset = SizeRow(across ? Loc.T("垂直偏移", "Vertical offset") : Loc.T("水平偏移", "Horizontal offset"), S.NotchOffsetX, 0, 0, 1, v => ChangeCollapsed(s => s.NotchOffsetX = v), centered: true, vertical: across);
                _refreshCollapsedRanges = () =>
                {
                    double shown = ShownWidth(style);
                    var limits = _app.Deck.CurrentCollapsedRanges(style, shown, S.NotchHeight);
                    SetCollapsedRange(width, limits.WidthMin, limits.WidthMax, shown);
                    SetAutoWidth(auto, width, S.NotchAutoWidth);
                    SetCollapsedRange(offset, -Whole(limits.OffsetMax), Whole(limits.OffsetMax), S.NotchOffsetX);
                };
                Group(Rows(edge, AnchorRow(style), auto, width.Row, height.Row, offset.Row));
                _builtDock = (style, S.NotchEdge, S.NotchAnchor);
                break;
            }
            case CompactStyle.Capsule:
            {
                var auto = AutoWidthRow(style);
                var width = SizeRow(Loc.T("宽度", "Width"), S.CapsuleWidth, CollapsedLimits.CapsuleWidthMin, CollapsedLimits.CapsuleWidthMax, 1, v => ChangeCollapsed(s => { s.CapsuleWidth = v; s.CapsuleAutoWidth = false; }));
                var height = SizeRow(Loc.T("高度", "Height"), S.CapsuleHeight, CollapsedLimits.CapsuleHeightMin, CollapsedLimits.CapsuleHeightMax, 1, v => ChangeCollapsed(s => s.CapsuleHeight = v));
                var top = SizeRow(Loc.T("垂直偏移", "Vertical offset"), S.CapsuleTop, CollapsedLimits.CapsuleTopMin, Math.Max(CollapsedLimits.CapsuleTopMin, S.CapsuleTop), 1, v => ChangeCollapsed(s => s.CapsuleTop = v),
                    help: Loc.T("胶囊距屏幕顶部的距离。不超过 20 DIP 时，展开后的面板将会吸附在屏幕顶端。", "Distance between the capsule and the top of the screen. At 20 DIP or less, the expanded panel snaps to the top of the screen."));
                var offset = SizeRow(Loc.T("水平偏移", "Horizontal offset"), S.CapsuleOffsetX, 0, 0, 1, v => ChangeCollapsed(s => s.CapsuleOffsetX = v), centered: true);
                _refreshCollapsedRanges = () =>
                {
                    double shown = ShownWidth(style);
                    var limits = _app.Deck.CurrentCollapsedRanges(style, shown, S.CapsuleHeight);
                    SetCollapsedRange(width, limits.WidthMin, limits.WidthMax, shown);
                    SetAutoWidth(auto, width, S.CapsuleAutoWidth);
                    // The capsule can move down to the bottom of the monitor, over a bottom taskbar; the limit follows its height and the display.
                    SetCollapsedRange(top, CollapsedLimits.CapsuleTopMin, Whole(limits.TopMax), S.CapsuleTop);
                    SetCollapsedRange(offset, -Whole(limits.OffsetMax), Whole(limits.OffsetMax), S.CapsuleOffsetX);
                };
                Group(auto, width.Row, height.Row, top.Row, offset.Row);
                _builtDock = null;
                break;
            }
            default:
            {
                var length = SizeRow(Loc.T("长度", "Length"), S.LineLength, CollapsedLimits.LineLengthMin, CollapsedLimits.LineLengthMax, 1, v => ChangeCollapsed(s => s.LineLength = v));
                var thickness = SizeRow(Loc.T("粗细", "Thickness"), S.LineThickness, CollapsedLimits.LineThicknessMin, CollapsedLimits.LineThicknessMax, CollapsedLimits.LineThicknessStep, v => ChangeCollapsed(s => s.LineThickness = CollapsedGeometry.RoundLineThickness(v)));
                bool across = Across(style);
                var edge = EdgeRow(style);
                var top = SizeRow(Loc.T("离边距离", "Distance from edge"), S.LineTop, CollapsedLimits.LineTopMin, CollapsedLimits.LineTopMax, 1, v => ChangeCollapsed(s => s.LineTop = v));
                var offset = SizeRow(across ? Loc.T("垂直偏移", "Vertical offset") : Loc.T("水平偏移", "Horizontal offset"), S.LineOffsetX, 0, 0, 1, v => ChangeCollapsed(s => s.LineOffsetX = v), centered: true, vertical: across);
                _refreshCollapsedRanges = () =>
                {
                    // The thickness is passed as the height, so a thicker line leaves less room below the top.
                    var limits = _app.Deck.CurrentCollapsedRanges(style, S.LineLength, S.LineThickness);
                    SetCollapsedRange(length, limits.WidthMin, limits.WidthMax, S.LineLength);
                    SetCollapsedRange(top, CollapsedLimits.LineTopMin, Math.Min(CollapsedLimits.LineTopMax, Whole(limits.TopMax)), S.LineTop);
                    SetCollapsedRange(offset, -Whole(limits.OffsetMax), Whole(limits.OffsetMax), S.LineOffsetX);
                };
                Group(Rows(edge, AnchorRow(style), length.Row, thickness.Row, top.Row, offset.Row));
                _builtDock = (style, S.LineEdge, S.LineAnchor);
                break;
            }
        }
        _refreshCollapsedRanges();
    }

    // Limits come from the main panel's display (MainWindow.CurrentCollapsedRanges). Offsets and tops are taken in whole
    // DIPs so zero stays on the slider's ticks and the thumb never shows a place the panel cannot reach.
    private static double Whole(double limit) => double.IsFinite(limit) ? Math.Max(0, Math.Floor(limit)) : 0;

    private void SetCollapsedRange(SizeControl control, double min, double max, double stored)
    {
        _updatingCollapsedRanges = true;
        try
        {
            max = Math.Max(min, max);
            // Widening first and narrowing last keeps the minimum from passing the maximum in between.
            if (min <= control.Slider.Maximum) { control.Slider.Minimum = min; control.Slider.Maximum = max; }
            else { control.Slider.Maximum = max; control.Slider.Minimum = min; }
            // The slider shows where the panel actually is; the saved value changes only when the user moves it.
            control.Slider.Value = Math.Clamp(stored, min, max);
            control.Slider.IsEnabled = max > min;
        }
        finally { _updatingCollapsedRanges = false; }
        control.Refresh();
    }

    // The auto width switch of the notch or the capsule. While it is on the content sets the width and the width row only
    // shows it; turning it off keeps the width the surface has at that moment.
    private FrameworkElement AutoWidthRow(CompactStyle style)
    {
        // On a side edge the notch's width runs down the screen, where the rows call it its length.
        string title = Across(style) ? Loc.T("自适应长度", "Fit length to content") : Loc.T("自适应宽度", "Fit width to content");
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.CompactAutoWidth(style), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => ChangeCollapsed(s => s.SetCompactAutoWidth(style, true));
        toggle.Unchecked += (_, _) => { double shown = ShownWidth(style); ChangeCollapsed(s => { s.SetCompactWidth(style, shown); s.SetCompactAutoWidth(style, false); }); };
        var row = Row(title, toggle);
        row.Tag = toggle;
        return row;
    }

    private void SetAutoWidth(FrameworkElement auto, SizeControl width, bool on)
    {
        _updatingCollapsedRanges = true;
        try { if (auto.Tag is CheckBox toggle) toggle.IsChecked = on; }
        finally { _updatingCollapsedRanges = false; }
        width.Row.IsEnabled = !on;
    }

    // The width the panel shows for the style: with auto width the content's, otherwise the saved one.
    private double ShownWidth(CompactStyle style)
    {
        var shape = _app.Deck.Collapsed;
        // The surface is drawn at the scale (PanelScales); the setting is the width before it.
        return S.CompactAutoWidth(style) && shape.Style == style ? shape.Surface.Width / _app.Deck.Scale : style == CompactStyle.Capsule ? S.CapsuleWidth : S.NotchWidth;
    }

    // The panel changed the collapsed size or switch from outside this window (a drag, a double click, or content that
    // changed the auto width).
    // A notch or line moved to another edge or anchor on the panel builds the page again, for its choices and the names of its rows.
    internal void CollapsedChanged()
    {
        if (!IsLoaded || _page != 1) return;
        if (_builtDock is { } built && (built.Edge != S.CompactEdge(built.Style) || built.Anchor != (built.Style == CompactStyle.Line ? S.LineAnchor : S.NotchAnchor))) ShowPage(1);
        else _refreshCollapsedRanges?.Invoke();
    }
    // The edge and the anchor the shown notch or line group was built for; null for the capsule.
    private (CompactStyle Style, ScreenEdge Edge, EdgeAnchor? Anchor)? _builtDock;
    private static FrameworkElement[] Rows(params FrameworkElement?[] rows) => [.. rows.OfType<FrameworkElement>()];

    // What a double click on the resize handle at an end of the collapsed panel does (MainWindow.Move.cs), for every style.
    private FrameworkElement GripDoubleClickRow() => MusicChoice(Loc.T("双击调整把手", "Double-click a resize handle"), S.GripDoubleClick,
        [(GripDoubleClick.AutoWidth, Loc.T("切换自适应宽度", "Toggle auto width")), (GripDoubleClick.Move, Loc.T("移动位置", "Move"))],
        value => Change(s => s.GripDoubleClick = value),
        help: Loc.T("鼠标移到刘海、胶囊或指示条的两端时，旁边会出现调整把手。移动位置：双击把手，边缘亮起后，可拖动整个区域调整位置。切换自适应宽度：双击把手，宽度在“跟随内容”和“固定”之间切换。",
            "Pointing at either end of the notch, capsule or indicator shows a resize handle beside it. Move: double-click the handle, and once the edge lights up, drag the whole area to a new place. Toggle auto width: double-click the handle to switch the width between following the content and staying fixed."));

    private void ChangeCollapsed(Action<DeckSettings> edit)
    {
        if (_updatingCollapsedRanges) return;
        Change(edit);
        StartCollapsedPreview();
        _refreshCollapsedRanges?.Invoke();
    }

    private void StartCollapsedPreview()
    {
        if (_preview) return;
        _preview = true;
        _app.Deck.Preview(true, expanded: false);
    }

    private void ResetCollapsedSize(CompactStyle style)
    {
        var defaults = new DeckSettings();
        Change(s => CopyCollapsedSize(s, defaults, style));
        StartCollapsedPreview();
        ShowPage(1);
    }

    // Copies one style's size and position, or every style's when none is named; Restore defaults uses the latter.
    private static void CopyCollapsedSize(DeckSettings target, DeckSettings source, CompactStyle? style = null)
    {
        if (style is null or CompactStyle.Notch)
        { target.NotchWidth = source.NotchWidth; target.NotchHeight = source.NotchHeight; target.NotchOffsetX = source.NotchOffsetX; target.NotchAutoWidth = source.NotchAutoWidth; }
        if (style is null or CompactStyle.Capsule)
        { target.CapsuleWidth = source.CapsuleWidth; target.CapsuleHeight = source.CapsuleHeight; target.CapsuleOffsetX = source.CapsuleOffsetX; target.CapsuleTop = source.CapsuleTop; target.CapsuleAutoWidth = source.CapsuleAutoWidth; }
        if (style is null or CompactStyle.Line)
        { target.LineLength = source.LineLength; target.LineThickness = source.LineThickness; target.LineTop = source.LineTop; target.LineOffsetX = source.LineOffsetX; }
    }

    // A slider with a number box and steppers, like SliderRow, with its own step (the indicator's thickness moves by
    // 0.5 DIP) and limits that can change while it is shown. A centered slider fills from zero toward the thumb.
    // vertical: the offset of a notch on a side edge, which moves it up and down.
    private SizeControl SizeRow(string title, double value, double min, double max, double step, Action<double> changed,
        string? help = null, bool centered = false, string? hint = null, bool vertical = false)
    {
        string format = step < 1 ? "0.#" : "0";
        string Text(double number) => number.ToString(format, CultureInfo.InvariantCulture);
        var slider = KeepPreview(new Slider
        {
            Style = (Style)FindResource(centered ? "SettingsCenteredSlider" : "SettingsSlider"), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            SmallChange = step, LargeChange = step * (centered ? 10 : 2), TickFrequency = step, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0)
        });
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), Width = 44, Text = Text(slider.Value), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, title); AutomationProperties.SetName(input, Loc.T(title + "数值", title + " value"));
        var line = new StackPanel { Orientation = Orientation.Horizontal }; line.Children.Add(input);
        var units = TextLine("DIP", 10, TextTertiary); units.Margin = new Thickness(4, 0, 6, 0); units.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(units);
        var minus = new RepeatButton { Style = (Style)FindResource("SettingsStepper"), Content = "−" };
        var plus = new RepeatButton { Style = (Style)FindResource("SettingsStepper"), Content = "+" };
        AutomationProperties.SetName(minus, Loc.T("减少" + title, "Decrease " + title)); AutomationProperties.SetName(plus, Loc.T("增加" + title, "Increase " + title));
        line.Children.Add(new Border { Width = 1, Background = UI.Brush(Divider), Margin = new Thickness(2, 5, 3, 5) }); line.Children.Add(minus); line.Children.Add(plus);
        var chip = KeepPreview(new Border
        {
            Child = line, Height = 30, CornerRadius = new CornerRadius(6), Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder),
            BorderThickness = new Thickness(1), Padding = new Thickness(7, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center
        });
        FieldFocus(chip, input, () => UI.Brush(_palette.InputBorder));

        var row = new Grid { MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(SettingLabel(title, help is null ? null : () => help));
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        Grid.SetColumn(chip, 2); row.Children.Add(chip);
        var note = TextLine(hint ?? "", 10.5, hint is null ? TextSecondary : TextTertiary);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(8, -6, 18, 10); note.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        Grid.SetRow(note, 1); Grid.SetColumn(note, 1); row.Children.Add(note);
        var control = new SizeControl { Row = row, Slider = slider, Note = note };

        FrameworkElement? fill = null;
        void UpdateFill()
        {
            fill ??= slider.Template?.FindName("CenterFill", slider) as FrameworkElement;
            if (fill is null) return;
            double track = slider.ActualWidth - 16, span = slider.Maximum - slider.Minimum;
            if (track <= 0 || span <= 0) { fill.Width = 0; return; }
            double X(double number) => 8 + track * (number - slider.Minimum) / span;
            double zero = X(0), thumb = X(slider.Value);
            fill.Margin = new Thickness(Math.Min(zero, thumb), 0, 0, 0); fill.Width = Math.Abs(thumb - zero);
        }
        void UpdateEdge()
        {
            if (!centered) return;
            bool left = slider.Maximum > 0 && slider.Value <= slider.Minimum, right = slider.Maximum > 0 && slider.Value >= slider.Maximum;
            note.Text = vertical
                ? left ? Loc.T("已到达屏幕上边缘，不能再向上移动。", "At the top edge of the screen; it cannot move further up.")
                    : right ? Loc.T("已到达屏幕下边缘，不能再向下移动。", "At the bottom edge of the screen; it cannot move further down.") : ""
                : left ? Loc.T("已到达屏幕左边缘，不能再向左移动。", "At the left edge of the screen; it cannot move further left.")
                    : right ? Loc.T("已到达屏幕右边缘，不能再向右移动。", "At the right edge of the screen; it cannot move further right.") : "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        control.Refresh = () => { input.Text = Text(slider.Value); UpdateFill(); UpdateEdge(); };
        if (centered) { slider.Loaded += (_, _) => UpdateFill(); slider.SizeChanged += (_, _) => UpdateFill(); }

        void Set(double number)
        {
            var next = Math.Clamp(Math.Round(number / step) * step, slider.Minimum, slider.Maximum);
            if (Math.Abs(slider.Value - next) < step / 100) { input.Text = Text(next); return; }
            slider.Value = next;
        }
        slider.ValueChanged += (_, _) =>
        {
            input.Text = Text(slider.Value); UpdateFill(); UpdateEdge();
            if (!_updatingCollapsedRanges) changed(slider.Value);
        };
        void Commit()
        {
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) Set(number);
            input.Text = Text(slider.Value);
        }
        minus.Click += (_, _) => Set(slider.Value - step); plus.Click += (_, _) => Set(slider.Value + step);
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { input.Text = Text(slider.Value); e.Handled = true; }
        };
        UpdateEdge();
        return control;
    }
}
