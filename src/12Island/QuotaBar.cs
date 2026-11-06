using System.Windows;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// A quota's track on the usage page, drawn as the system monitor's bars are: the share eases to each new reading (Eased, Motion)
// instead of jumping. The page is built again when its data changes, so the share each quota last showed is kept by its key
// (the app and the quota) and a new bar starts from there.
internal sealed class QuotaBar : FrameworkElement
{
    // Shares last shown, by quota. Bounded: a quota that no longer exists is forgotten once the table fills.
    private static readonly Dictionary<string, double> Shown = [];
    private readonly string _key, _color;
    private Eased _share;

    public QuotaBar(string key, double share, string color)
    {
        _key = key; _color = color;
        share = Math.Clamp(double.IsFinite(share) ? share : 0, 0, 1);
        _share = new Eased(Shown.TryGetValue(key, out var last) ? last : share);
        if (Shown.Count > 256) Shown.Clear();
        Shown[key] = share;
        SnapsToDevicePixels = true;
        if (_share.Set(share, Soft.Enabled)) Loaded += (_, _) => Motion.Start(this, Step);
    }

    private bool Step(double now) { InvalidateVisual(); return _share.Moving(now); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, radius = h / 2;
        if (w <= 0 || h <= 0) return;
        drawing.DrawRoundedRectangle(UI.Brush(UI.Track), null, new Rect(0, 0, w, h), radius, radius);
        double share = Math.Clamp(_share.At(Eased.Now), 0, 1);
        if (share <= 0) return;
        // A sliver keeps its rounded ends, as the old bar's star column did.
        drawing.DrawRoundedRectangle(UI.Brush(_color), null, new Rect(0, 0, Math.Max(Math.Min(h, w), w * share), h), radius, radius);
    }
}
