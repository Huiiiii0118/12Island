using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

internal static class UI
{
    // The scale the expanded panel is drawn at (DeckSettings.PanelScale), inherited by everything in it and set on its menus.
    // Device pixels per DIP of their own layout are the monitor's times this, so what is drawn on whole device pixels still meets
    // them; a new scale draws them again.
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached("Scale", typeof(double), typeof(UI),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject element, double scale) => element.SetValue(ScaleProperty, scale);
    public static double PixelsPerDip(Visual visual) => VisualTreeHelper.GetDpi(visual).PixelsPerDip * GetScale(visual);
    // Draws element at scale (none at 1), as the expanded panel and its menus are.
    public static void Scale(FrameworkElement element, double scale)
    {
        element.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        SetScale(element, scale);
    }
    public static readonly FontFamily TextFont = new("Microsoft YaHei");
    // Windows' symbol font, used where a picture stands for a program or an action.
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    // The panel sets Latin text and digits in Segoe UI Variable and falls back to YaHei for Chinese.
    public static readonly FontFamily PanelFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    // Text and structure on the panel's surface, from the panel theme (see PanelTheme): three label levels, hairlines and fills.
    public static string Accent => PanelTheme.Hex("accent");
    public static string Primary => PanelTheme.Hex("textPrimary");
    public static string Secondary => PanelTheme.Hex("textSecondary");
    public static string Tertiary => PanelTheme.Hex("textTertiary");
    public static string Hairline => PanelTheme.Hex("hairline");
    public static string Track => PanelTheme.Hex("track");
    public static string Fill => PanelTheme.Hex("fill");
    public static string FillSelected => PanelTheme.Hex("fillSelected");
    public static string Outline => PanelTheme.Hex("outline");
    // Panels are rebuilt on every refresh with hundreds of brushes; one frozen brush per color is shared instead.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    public static Brush Brush(string color) => Brushes.GetOrAdd(color, static text =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)); brush.Freeze(); return brush;
    });
    public static string ColorFor(ProviderId id) => PanelTheme.DefaultAppColor(id);
    public static TextBlock Text(string text, double size = 13, string color = "#E6E8EF", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Line(string text, double size = 13, string color = "#E6E6E9", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    // Keep the font's natural line box so a parent can center it within a taller row or button.
    public static TextBlock Centered(string text, double size, string color, FontWeight? weight = null, FontFamily? font = null)
    {
        var block = Line(text, size, color, weight);
        block.FontFamily = font ?? PanelFont;
        block.VerticalAlignment = VerticalAlignment.Center;
        // Equal-width digits keep figures in neighbouring rows on the same vertical lines.
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }
    // A Centered line's own height, its baseline below its top, and the height of its capitals.
    public static double LineBox(double size) => PanelFont.LineSpacing * size;
    public static double Baseline(double size) => PanelFont.Baseline * size;
    public static double CapHeight(double size) => new Typeface(PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight * size;
    // Standalone symbols center their visible ink, without the font's baseline whitespace.
    public static Path CenteredSymbol(string text, double size, string color, FontWeight? weight = null)
    {
        var brush = Brush(color);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(PanelFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, 1);
        var geometry = formatted.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        geometry.Transform = new TranslateTransform(0, -bounds.Top);
        return new Path { Data = geometry, Fill = brush, Width = formatted.WidthIncludingTrailingWhitespace,
            Height = bounds.Height, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = false };
    }
    // WPF keeps a hover tooltip open while the pointer stays in the convex hull of the owner and the tooltip.
    // A tooltip that hangs to the right of its owner therefore stays open over the neighbour on that side.
    // This one is right-aligned under its owner and extends to the left, so the hull leaves out the right-hand neighbour.
    private const double ToolTipGap = 4;
    private static readonly CustomPopupPlacementCallback Leftward = (popup, target, _) =>
        [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + ToolTipGap), PopupPrimaryAxis.Horizontal)];
    public static ToolTip LeftwardToolTip(object content) => new() { Content = content, Placement = PlacementMode.Custom, CustomPopupPlacementCallback = Leftward };
    // A single line whose height is exactly the given value, independent of the font's own line box.
    public static TextBlock Fixed(string text, double size, double lineHeight, string color, FontWeight? weight = null, FontFamily? font = null)
    {
        var block = Centered(text, size, color, weight, font);
        block.LineHeight = lineHeight; block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.Height = lineHeight;
        return block;
    }
    // Whether a single-line text is cut off by its ellipsis at its current width, checked when needed so that resizing and
    // reused rows are always measured afresh.
    public static bool IsCut(TextBlock block)
    {
        double shown = block.ActualWidth;
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool cut = block.DesiredSize.Width > shown + .5;
        block.InvalidateMeasure();
        return cut;
    }
    // A progress ring: a faint track with a colored arc that starts at the top and runs counterclockwise.
    public static Grid Ring(double percent, string color, double size, double thickness, string? track = null)
    {
        var ring = new Grid { Width = size, Height = size };
        ring.Children.Add(new Ellipse { Stroke = Brush(track ?? PanelTheme.Hex("ringTrack")), StrokeThickness = thickness });
        double fraction = Math.Clamp(percent / 100, 0, 1);
        if (fraction >= .999) ring.Children.Add(new Ellipse { Stroke = Brush(color), StrokeThickness = thickness });
        else if (fraction > 0)
        {
            double radius = size / 2 - thickness / 2, center = size / 2;
            double angle = fraction * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(center - radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            ring.Children.Add(new Path { Data = geometry, Stroke = Brush(color), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        return ring;
    }
    public static string Number(long number) => number switch
    { >= 1_000_000_000 => (number / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B", >= 1_000_000 => (number / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M", >= 1000 => (number / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K", _ => number.ToString("N0") };
    public static string Cost(IEnumerable<TokenEntry> entries, Pricing pricing) => Cost(pricing.Summarize(entries));
    public static string Cost(CostSummary cost) => Amount(cost) + (cost.Unpriced > 0 ? " +" : "");
    public static string Amount(CostSummary cost) => "$" + cost.Amount.ToString("N2", CultureInfo.InvariantCulture);
    public static string? CostNote(IEnumerable<TokenEntry> entries, Pricing pricing) => CostNote(pricing.Summarize(entries), pricing);
    // Null when there is nothing to add to the amount itself, so the cost shows no tooltip.
    public static string? CostNote(CostSummary cost, Pricing pricing)
    {
        if (!cost.HasValue && cost.Unpriced == 0) return Loc.T("无用量记录", "No usage records");
        if (!cost.HasValue && cost.Unpriced > 0) return pricing.LastError ?? Loc.T("未匹配到完整价格", "Prices could not be matched for every record");
        var lines = new List<string>();
        if (cost.IsEstimate && pricing.UpdatedAt is { } time)
            lines.Add(Loc.T($"价格更新 {time.LocalDateTime:MM-dd HH:mm}", $"Prices updated {time.LocalDateTime:MM-dd HH:mm}") +
                (pricing.IsStale || pricing.LastError is not null ? Loc.T(" · 缓存", " · cached") : ""));
        if (cost.Unpriced > 0) lines.Add(Loc.T($"{cost.Unpriced} 条记录未计价", Loc.Count(cost.Unpriced, "record has", "records have") + " no price"));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
    public static string Reset(DateTimeOffset? date)
    {
        if (date is null) return Loc.T("重置时间未知", "Reset time unknown");
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return Loc.T("已到重置时间，等待更新", "Reset time reached; waiting for an update");
        if (remaining.TotalDays >= 1) return Loc.T($"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时后重置", $"Resets in {(int)remaining.TotalDays} d {remaining.Hours} h");
        if (remaining.TotalHours >= 1) return Loc.T($"{remaining.Hours} 小时 {remaining.Minutes} 分后重置", $"Resets in {remaining.Hours} h {remaining.Minutes} min");
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Loc.T($"{minutes} 分钟后重置", $"Resets in {minutes} min");
    }
    public static string ShortReset(DateTimeOffset? date)
    {
        if (date is null) return "";
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return Loc.T("待重置", "Reset due");
        if (remaining.TotalDays >= 1) return Loc.T($"{(int)remaining.TotalDays} 天 {remaining.Hours} 时后", $"in {(int)remaining.TotalDays}d {remaining.Hours}h");
        if (remaining.TotalHours >= 1) return Loc.T($"{remaining.Hours} 时 {remaining.Minutes} 分后", $"in {remaining.Hours}h {remaining.Minutes}m");
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Loc.T($"{minutes} 分后", $"in {minutes}m");
    }
    // "Gemini · 5 小时" → group "Gemini", which orders the quota pages. The group stays in the data's words.
    public static string QuotaGroup(Quota quota) => quota.Label.Contains('·') ? quota.Label.Split('·')[0].Trim() : "";
    // "5 小时额度" → "5 小时"; "Gemini · 5 小时" → "Gemini 5 小时" and "Claude · 每周" → "Claude 每周", written like
    // Claude's "Opus 每周", so a grouped quota names its group in its own row. The shown label is translated.
    public static string QuotaLabel(Quota quota)
    {
        var period = (quota.Label.Contains('·') ? quota.Label.Split('·')[^1] : quota.Label).Replace("额度", "", StringComparison.Ordinal).Trim();
        var group = QuotaGroup(quota);
        if (group.Length == 0) return Loc.Label(period);
        return period == "每周" ? Loc.Label(group + " 每周") : group + " " + Loc.Label(period);
    }
    public static string DisplayPlan(string plan) => plan.Trim().ToLowerInvariant() switch
    { "prolite" => "Pro Lite", "pro" or "google ai pro" => "Pro", "google ai ultra" => "Ultra", "plus" => "Plus", "free" => "Free", "max" => "Max", "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", _ => plan.Trim() };
    public static void OpenUrl(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}

// One line of text set to a given width: the space between its characters grows evenly to fill it, and the glyphs keep their
// shapes. Captions stacked in a column, such as "Today" over "Week", then start and end on the same vertical lines.
// Text that already has the width, such as "今日" and "七日", is drawn as it is.
internal sealed class JustifiedText(string text, double size, string color, double width) : FrameworkElement
{
    private static FormattedText Format(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, pixelsPerDip);
    public static double NaturalWidth(string text, double size) => Format(text, size, Brushes.White, 1).WidthIncludingTrailingWhitespace;

    protected override Size MeasureOverride(Size availableSize) => new(width, Format(text, size, Brushes.White, 1).Height);
    protected override void OnRender(DrawingContext drawing)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var brush = UI.Brush(color);
        var whole = Format(text, size, brush, pixelsPerDip);
        double extra = text.Length > 1 ? (width - whole.WidthIncludingTrailingWhitespace) / (text.Length - 1) : 0;
        if (extra <= 0) { drawing.DrawText(whole, new Point()); return; }
        // Each character keeps its natural place, the width of the text before it, plus the added spacing.
        for (int i = 0; i < text.Length; i++)
        {
            double x = (i == 0 ? 0 : Format(text[..i], size, brush, pixelsPerDip).WidthIncludingTrailingWhitespace) + i * extra;
            drawing.DrawText(Format(text[i].ToString(), size, brush, pixelsPerDip), new Point(x, 0));
        }
    }
}
