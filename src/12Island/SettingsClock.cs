using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private readonly List<(RadioButton Tile, ClockStyle Style)> _clockTiles = [];
    private readonly Dictionary<CompactStyle, RadioButton> _styleCards = [];

    // The clock's row among the collapsed parts; the switch only turns the clock on or off.
    private Border ClockRow(CompactStyle style, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        return OptionsPartRow(style, CompactPart.Clock, rows, Loc.T("时间", "Clock"), Loc.T("时间设置", "Clock settings"), S.ShowsClock(style), on =>
        {
            Change(s => { if (notch) s.NotchClock = on; else s.CapsuleClock = on; });
            if (_styleCards.TryGetValue(style, out var card)) card.Content = CompactStylePreview.Create(style, _palette.Dark, on);
        }, ClockOptions(style), new Thickness(16, 14, 16, 0));
    }

    private FrameworkElement ClockOptions(CompactStyle compact)
    {
        var panel = new StackPanel();
        var heading = TextLine(Loc.T("时间样式", "Clock style"), 11.5, TextSecondary); heading.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(heading);
        // Each card shows only the time; its name is read by screen readers.
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(-5, -5, -5, 9) };
        _clockTiles.Clear();
        foreach (var style in Enum.GetValues<ClockStyle>())
        {
            var tile = new RadioButton { Style = (Style)FindResource("SettingsClockTile"), GroupName = "ClockStyle" + compact, IsChecked = S.ClockStyle == style,
                Margin = new Thickness(5), Content = ClockPreview(style) };
            AutomationProperties.SetName(tile, Loc.T("时间样式 ", "Clock style ") + ClockFace.Title(style));
            tile.Checked += (_, _) => Change(s => s.ClockStyle = style);
            tiles.Children.Add(tile); _clockTiles.Add((tile, style));
        }
        panel.Children.Add(tiles);
        // Shown only while the gradient is chosen, under the color row.
        var gradient = new StackPanel();
        RadioButton? gradientSwatch = null;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        ClockColor[] order = [ClockColor.White, ClockColor.Amber, ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Custom, ClockColor.Gradient, ClockColor.Cover];
        foreach (var color in order)
        {
            string name = ColorName(color);
            var swatch = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = "ClockColor" + compact, IsChecked = S.ClockColor == color,
                Background = ColorSwatchFill(color), ToolTip = name, Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 6, 0, 0, 0) };
            AutomationProperties.SetName(swatch, Loc.T("时间颜色 ", "Clock color ") + name);
            swatch.Checked += (_, _) =>
            {
                Change(s => s.ClockColor = color); RefreshClockTiles();
                gradient.Visibility = color == ClockColor.Gradient ? Visibility.Visible : Visibility.Collapsed;
            };
            if (color == ClockColor.Cover) swatch.Content = MusicVisuals.Icon("music", 13, UI.Brush(SwatchInk(SampleCover)));
            if (color == ClockColor.Gradient) gradientSwatch = swatch;
            if (color == ClockColor.Custom)
            {
                // Choosing this swatch also opens the picker; each later click opens it again.
                swatch.Content = Pencil(ClockFace.Parse(S.ClockCustomColor));
                AttachClockPicker(swatch, () => S.ClockCustomColor, ClockColors.DefaultCustom, value =>
                {
                    Change(s => { s.ClockCustomColor = value; s.ClockColor = ClockColor.Custom; });
                    swatch.Background = ColorSwatchFill(ClockColor.Custom); swatch.Content = Pencil(ClockFace.Parse(value)); RefreshClockTiles();
                });
            }
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider) });
        panel.Children.Add(Row(Loc.T("颜色", "Color"), swatches));
        GradientOptions(gradient, compact, () => { if (gradientSwatch is not null) gradientSwatch.Background = ColorSwatchFill(ClockColor.Gradient); });
        gradient.Visibility = S.ClockColor == ClockColor.Gradient ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(gradient);
        panel.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider) });
        panel.Children.Add(Toggle(Loc.T("24 小时制", "24-hour clock"), S.Clock24Hour, value => { Change(s => s.Clock24Hour = value); RefreshClockTiles(); }));
        panel.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider) });
        panel.Children.Add(Toggle(Loc.T("显示秒数", "Show seconds"), S.ClockSeconds, value => { Change(s => s.ClockSeconds = value); RefreshClockTiles(); }));
        return panel;
    }

    // Every card shows 14:32:08 today in the chosen color, hour format and seconds, at twice the size of the collapsed surface.
    private FrameworkElement ClockPreview(ClockStyle style)
        => new ClockFace(style, S.Clock24Hour, S.ClockSeconds, ClockFace.Palette(S, _app.Deck.CoverColor), DateTime.Today.AddHours(14).AddMinutes(32).AddSeconds(8)) { LayoutTransform = new ScaleTransform(2, 2) };
    private void RefreshClockTiles()
    {
        foreach (var (tile, style) in _clockTiles) tile.Content = ClockPreview(style);
    }
    private static string ColorName(ClockColor color) => color switch
    {
        ClockColor.Amber => Loc.T("琥珀", "Amber"), ClockColor.Cyan => Loc.T("青色", "Cyan"), ClockColor.Pink => Loc.T("粉色", "Pink"),
        ClockColor.Lime => Loc.T("柠檬绿", "Lime"), ClockColor.Gradient => Loc.T("渐变", "Gradient"), ClockColor.Cover => Loc.T("跟随封面", "Cover color"),
        ClockColor.Custom => Loc.T("自定义颜色", "Custom color"), _ => Loc.T("主题默认色", "Theme default")
    };
    // The cover swatch shows a sample cover color under its note.
    private Brush ColorSwatchFill(ClockColor color) => color switch
    {
        ClockColor.Gradient => ClockFace.Fill(S.ClockGradient.Select(ClockFace.Parse).ToList()),
        ClockColor.Custom => ClockFace.Fill([ClockFace.Parse(S.ClockCustomColor)]),
        ClockColor.Cover => ClockFace.Fill([SampleCover]),
        // As the panel draws them on the current theme's surface.
        _ => ClockFace.Fill([PanelTheme.Readable(ClockFace.Preset(color), PanelThemes.TextContrast)])
    };
    private static readonly Color SampleCover = Color.FromRgb(0xE0, 0xA4, 0x8C);
    // A mark over a swatch (the cover's note, the custom color's pencil): dark on light colors and light on dark ones.
    private static string SwatchInk(Color under) => .2126 * under.R + .7152 * under.G + .0722 * under.B > 150 ? "#B3202027" : "#E6FFFFFF";
    // A pencil over the custom color marks it as editable.
    private static FrameworkElement Pencil(Color under)
    {
        var path = new Path { Data = Geometry.Parse("M 4,20 L 5,15.5 L 15.5,5 L 19,8.5 L 8.5,19 Z M 13.5,7 L 17,10.5"), Stroke = UI.Brush(SwatchInk(under)),
            StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = 12, Height = 12, Child = canvas, IsHitTestVisible = false };
    }
    // Opens the color picker under the button with the color as it is when clicked; "默认" restores the fallback.
    private void AttachClockPicker(ButtonBase target, Func<string> current, string fallback, Action<string> apply)
    {
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Opened += (_, _) => target.IsHitTestVisible = false;
        PopupMotion.Attach(popup);
        popup.Closed += (_, _) => target.IsHitTestVisible = true;
        target.Click += (_, _) =>
        {
            var color = current();
            popup.Child = ColorPicker(color, string.Equals(color, fallback, StringComparison.OrdinalIgnoreCase), value => { popup.IsOpen = false; PopupMotion.AfterClose(() => apply(value.Length > 0 ? value : fallback)); });
            popup.IsOpen = true;
        };
    }
    // The gradient row: preset gradients, then the three colors of the current gradient, each opening the picker.
    private void GradientOptions(StackPanel host, CompactStyle compact, Action changed)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var presets = new List<(RadioButton Button, string[] Stops)>();
        var stops = new List<Button>();
        bool updating = false;
        void Refresh()
        {
            updating = true;
            foreach (var (button, colors) in presets) button.IsChecked = colors.SequenceEqual(S.ClockGradient, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stops.Count; i++) stops[i].Foreground = UI.Brush(S.ClockGradient[i]);
            updating = false;
            changed(); RefreshClockTiles();
        }
        // The current panel theme's presets; switching themes carries a chosen preset over to the new theme's (ApplyMatching).
        foreach (var themed in PanelTheme.Current.ClockGradients)
        {
            string name = themed.DisplayName; string[] colors = [.. themed.Stops];
            var preset = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = "ClockGradient" + compact, ToolTip = name,
                Background = ClockFace.Fill(colors.Select(ClockFace.Parse).ToList()), Margin = new Thickness(line.Children.Count == 0 ? 0 : 6, 0, 0, 0),
                IsChecked = colors.SequenceEqual(S.ClockGradient, StringComparer.OrdinalIgnoreCase) };
            AutomationProperties.SetName(preset, Loc.T("预设渐变 ", "Preset gradient ") + name);
            preset.Checked += (_, _) => { if (updating) return; Change(s => s.ClockGradient = [.. colors]); Refresh(); };
            presets.Add((preset, colors)); line.Children.Add(preset);
        }
        line.Children.Add(new Border { Width = 1, Height = 20, Background = UI.Brush(Divider), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        string[] names = [Loc.T("起始颜色", "Start color"), Loc.T("中间颜色", "Middle color"), Loc.T("结束颜色", "End color")];
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var stop = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(S.ClockGradient[i]), ToolTip = names[i], Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            AutomationProperties.SetName(stop, Loc.T("渐变", "Gradient ") + names[i]);
            // "默认" restores this stop of the theme's first preset gradient.
            var fallback = PanelTheme.Current.ClockGradients[0].Stops[index];
            AttachClockPicker(stop, () => S.ClockGradient[index], fallback, value => { Change(s => s.ClockGradient[index] = value); Refresh(); });
            stops.Add(stop); line.Children.Add(stop);
        }
        host.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider) });
        host.Children.Add(Row(Loc.T("渐变", "Gradient"), line));
    }
}
