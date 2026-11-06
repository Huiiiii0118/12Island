namespace TwelveIsland.Core;

// The dot's pose at one moment. Lift: how far above its resting floor, in DIPs (up the screen). ScaleX and ScaleY: squash and
// stretch about the middle of that floor. Opacity: 0 to 1.
public readonly record struct DotPose(double Lift, double ScaleX, double ScaleY, double Opacity)
{
    public static readonly DotPose Rest = new(0, 1, 1, 1);
}

// One frame of the dot sinking into still water (DropMerge draws it), in DIPs about the point where the dot goes in. Depth is
// the dot's centre below the waterline (negative while it is above) and Radius its radius. The surface is a hairline that is
// thickest in the middle and thins to a point at each end: LineHalf is how far it reaches either side, LineThickness its half
// thickness in the middle. Blend is how far the dot and the surface melt into each other, as two drops of the same liquid
// do: the surface rises to meet the dot as it nears, and after the dot has gone under it leaves a swell that sinks back.
public readonly record struct WaterMerge(double Depth, double Radius, double LineHalf, double LineThickness, double Blend);

// How the round dot moves that every collapsed form starts from and ends in (MainWindow.Lifecycle.cs). The floor is below the
// resting place: the dot pops up and lands down twice with the same force. Each flight follows a smooth arc that slows at the
// top and turns without a kink. Booting, it fades in and jumps twice; once the second landing has sprung back the pose is at
// rest, so the swell into the collapsed shape starts from a still circle without a snap in size. Powering off, once the surface has drawn back into it, it jumps twice; at the second landing it meets the
// water: a hairline surface opens under it, the dot melts into it and is drawn under, and the surface closes into
// the point where it went in. Times are in milliseconds and do not follow the duration chosen for the panel.
public static class DotMotion
{
    // The dot's diameter for every form, so the notch, the capsule and the line all start and end as the same circle.
    public const double Size = 16;
    // Both jumps share the same height and length: a deliberate pop, not a bounce that dies away. Kept moderate so the
    // motion reads as a lift, not a hard knock.
    private const double JumpHeight = 14, Jump = 360;
    private const double Appear = 260, Crouch = 60, Landing = 60, LastLanding = 55, Sink = 380;
    private const double Jumps = Crouch + Jump + Landing + Jump;
    private const double CrouchDepth = .14, LandingDepth = .22, LastLandingDepth = .18;
    private const double Stretch = .1, StretchFade = 3, AppearScale = .72;

    // How far above its resting top the dot reaches at the top of a jump, stretched: its resting top must be at least this
    // far below the window's top edge, or the jump is cut off there.
    public const double Headroom = JumpHeight + Size * Stretch;
    public const double BootLength = Appear + Jumps + LastLanding, SwellStart = BootLength;
    public const double Touchdown = Jumps, SinkLength = Sink, PowerOffLength = Jumps + Sink;

    public static DotPose Boot(double t)
    {
        t = Math.Max(0, t);
        if (t < Appear)
        {
            double u = t / Appear, scale = AppearScale + (1 - AppearScale) * Out(u);
            return new(0, scale, scale, Smooth(u));
        }
        t -= Appear;
        if (t < Jumps) return JumpPose(t);
        t -= Jumps;
        return t < LastLanding ? Squash(t / LastLanding, LastLandingDepth) : DotPose.Rest;
    }

    // The jumps alone; the merge into water is MergeAt, drawn separately once the second landing is reached.
    public static DotPose PowerOff(double t)
    {
        t = Math.Max(0, t);
        return t < Jumps ? JumpPose(t) : DotPose.Rest;
    }

    // The dot sinking into still water, u from the moment it lands on the surface (0) to the end (1). It starts as the plain
    // dot where the jumps left it, so the hand-over from the jumping dot does not move or reshape it. A hairline surface opens
    // under it and melts into its base; the dot is drawn under, slowly for a moment and then quickly, the surface swelling
    // after it and sinking back flat; the surface then closes into the point where the dot went in. Nothing fades: every part
    // ends by shrinking to nothing, so the close stays sharp.
    public static WaterMerge MergeAt(double u)
    {
        u = Math.Clamp(u, 0, 1);
        const double r = Size / 2, blend = 3.2, thickness = .8;
        double open = Out(Math.Min(1, u / .3));
        double sink = Curves.Bezier(.55, 0, .25, 1, Math.Clamp((u - .04) / .5, 0, 1));
        // Under by more than the blend, the dot no longer raises the surface at all.
        double depth = -r + (2 * r + blend + 1) * sink;
        // The surface starts closing as the dot's top goes under, while the swell still sinks back, so it never lies still.
        double close = Curves.Bezier(.45, 0, .5, 1, Math.Clamp((u - .3) / .66, 0, 1));
        double lineHalf = r * (.5 + 1.3 * open) * (1 - close);
        double lineThickness = thickness * Smooth(Math.Min(1, u / .18)) * (1 - .5 * close);
        return new(depth, r, lineHalf, lineThickness, blend * Smooth(Math.Min(1, u / .18)));
    }

    // The signed distance from (x, y) to the edge of the frame's figure, negative inside: x across from the point where the dot
    // goes in, y down from the waterline. The dot and the surface are joined by a smooth minimum, so they flow into each other
    // with no seam or notch; only what is above the bottom of the surface shows, the rest being under the water. That bottom
    // follows the surface as it thins toward its ends, so the dot's lower part never shows as a flat bar under the line.
    public static double MergeDistance(WaterMerge merge, double x, double y)
    {
        double dot = Math.Sqrt(x * x + (y - merge.Depth) * (y - merge.Depth)) - merge.Radius;
        if (merge.LineHalf < 1e-3 || merge.LineThickness < 1e-3) return Math.Max(dot, y);
        double across = Math.Abs(x) / merge.LineHalf;
        double half = across < 1 ? merge.LineThickness * Math.Sqrt(1 - across * across) : 0;
        double line = across < 1 ? Math.Abs(y) - half : Math.Sqrt((Math.Abs(x) - merge.LineHalf) * (Math.Abs(x) - merge.LineHalf) + y * y);
        double joined = SmoothMin(dot, line, merge.Blend);
        return Math.Max(joined, y - half);
    }

    private static double SmoothMin(double a, double b, double k)
    {
        if (k <= 0) return Math.Min(a, b);
        double h = Math.Clamp(.5 + .5 * (b - a) / k, 0, 1);
        return b + (a - b) * h - k * h * (1 - h);
    }

    private static DotPose JumpPose(double t)
    {
        if (t < Crouch) return Squash(t / Crouch, CrouchDepth);
        t -= Crouch;
        if (t < Jump) return Flight(t / Jump);
        t -= Jump;
        if (t < Landing) return Squash(t / Landing, LandingDepth);
        t -= Landing;
        return t < Jump ? Flight(t / Jump) : DotPose.Rest;
    }

    private static DotPose Flight(double u)
    {
        double lift = JumpHeight * Math.Sin(Math.PI * Math.Clamp(u, 0, 1));
        double stretch = 1 + Stretch * Math.Abs(Math.Cos(Math.PI * u)) * Math.Min(1, lift / StretchFade);
        return new(lift, 1 / Math.Sqrt(stretch), stretch, 1);
    }

    private static DotPose Squash(double u, double depth)
    {
        double height = 1 - depth * Math.Sin(Math.PI * Math.Clamp(u, 0, 1));
        return new(0, 1 / Math.Sqrt(height), height, 1);
    }

    private static double Out(double u) => Curves.Bezier(.22, .8, .28, 1, u);
    private static double Smooth(double u) => u * u * (3 - 2 * u);
}
