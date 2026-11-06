using System.Windows;
using System.Windows.Media.Animation;
using TwelveIsland.Core;

namespace TwelveIsland;

// The island's width and left edge while it opens, closes or changes size, on whole device pixels (TwelveIsland.Core.SurfaceSnap): the
// layout rounds the island's margin, its width and the open page's offset inside it each on its own, which in the tail of a move
// stepped the page and the surface's edges a pixel to and fro. Both follow the curve they are given exactly as the plain
// animations do; only the values they give are the snapped ones.
public sealed class SnappedWidthAnimation : AnimationTimeline
{
    public double To { get; set; }
    // The width of the content centred in the island, which the width keeps whole pixels of room around.
    public double Page { get; set; }
    // Device pixels per DIP.
    public double Scale { get; set; } = 1;
    public IEasingFunction? EasingFunction { get; set; }
    public override Type TargetPropertyType => typeof(double);
    protected override Freezable CreateInstanceCore() => new SnappedWidthAnimation { To = To, Page = Page, Scale = Scale, EasingFunction = EasingFunction };
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double p = animationClock.CurrentProgress ?? 0;
        return SurfaceSnap.Width((double)defaultOriginValue, To, EasingFunction?.Ease(p) ?? p, Scale, Page);
    }
}

// The HoverZone's left margin, which places the island: its edge on whole pixels about the centre the island is travelling to, for
// a width that goes from FromWidth to ToWidth on the same track and curve.
public sealed class SnappedLeftAnimation : AnimationTimeline
{
    public double To { get; set; }
    public double FromWidth { get; set; }
    public double ToWidth { get; set; }
    public double Page { get; set; }
    public double Scale { get; set; } = 1;
    public IEasingFunction? EasingFunction { get; set; }
    public override Type TargetPropertyType => typeof(Thickness);
    protected override Freezable CreateInstanceCore() => new SnappedLeftAnimation { To = To, FromWidth = FromWidth, ToWidth = ToWidth, Page = Page, Scale = Scale, EasingFunction = EasingFunction };
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double p = animationClock.CurrentProgress ?? 0;
        return new Thickness(SurfaceSnap.Left(((Thickness)defaultOriginValue).Left, To, FromWidth, ToWidth, EasingFunction?.Ease(p) ?? p, Scale, Page), 0, 0, 0);
    }
}
