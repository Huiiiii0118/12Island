using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TwelveIsland;

// A label followed by its info icon. The label is centered in the height the panel is given, and the icon's center goes on
// the middle of the label's glyphs. The settings window draws text in Display mode, which snaps the baseline to device
// pixels, so the glyphs sit at a different height in the label's box at each display scale and a fixed offset cannot
// follow them: at 150% a 1 DIP nudge measured at another scale left the icon 1.5 pixels low.
internal sealed class HelpHeading : Panel
{
    // The middle of a capital letter and of an ideograph above the baseline, the same for Segoe UI Variable and
    // Microsoft YaHei UI; measured from rendered glyphs at 11 to 13 DIP and 100% to 200% scale.
    private const double GlyphMiddle = 0.36;
    private readonly TextBlock _label;
    private readonly FrameworkElement _icon;
    private readonly TranslateTransform _shift = new();

    public HelpHeading(TextBlock label, FrameworkElement icon)
    {
        _label = label; _icon = icon;
        label.VerticalAlignment = VerticalAlignment.Top; icon.VerticalAlignment = VerticalAlignment.Top; icon.RenderTransform = _shift;
        Children.Add(label); Children.Add(icon);
    }

    protected override Size MeasureOverride(Size available)
    {
        var free = new Size(double.PositiveInfinity, available.Height);
        _label.Measure(free); _icon.Measure(free);
        return new Size(_label.DesiredSize.Width + _icon.DesiredSize.Width, Math.Max(_label.DesiredSize.Height, _icon.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Rounded as layout rounding would round it, so the baseline below is measured from where the label really is.
        double top = Math.Round((final.Height - _label.DesiredSize.Height) / 2 * scale) / scale;
        _label.Arrange(new Rect(0, top, _label.DesiredSize.Width, _label.DesiredSize.Height));
        var text = new FormattedText(_label.Text, CultureInfo.CurrentUICulture, _label.FlowDirection,
            new Typeface(_label.FontFamily, _label.FontStyle, _label.FontWeight, _label.FontStretch), _label.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(_label), scale);
        // A label with a fixed line height moves its baseline within the taller line; the dialogs' labels set one.
        if (_label.LineStackingStrategy == LineStackingStrategy.BlockLineHeight && !double.IsNaN(_label.LineHeight)) text.LineHeight = _label.LineHeight;
        double middle = top + _label.Margin.Top + _label.Padding.Top + text.Baseline - GlyphMiddle * _label.FontSize;
        _icon.Arrange(new Rect(_label.DesiredSize.Width, middle - _icon.DesiredSize.Height / 2, _icon.DesiredSize.Width, _icon.DesiredSize.Height));
        // Layout rounding moves the icon's box, and again the mark inside it, to whole device pixels; at 150% the two
        // together left the mark half a pixel low. The shift takes the remainder back.
        var mark = (_icon as Decorator)?.Child as FrameworkElement ?? _icon;
        double center = VisualTreeHelper.GetOffset(_icon).Y + mark.TranslatePoint(new Point(0, mark.RenderSize.Height / 2), _icon).Y;
        _shift.Y = middle - center;
        return final;
    }

    // The baseline moves with the display scale even when the label's size in DIPs stays the same.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateArrange();
}
