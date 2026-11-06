using System.Globalization;

namespace TwelveIsland.Core;

// A color as the panel themes write it: "#RRGGBB" or "#AARRGGBB". Contrast follows WCAG 2.x: a translucent color is first
// laid over the opaque background it is drawn on, and the ratio is taken between the two relative luminances.
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static readonly ThemeColor Black = new(255, 0, 0, 0), White = new(255, 255, 255, 255);
    // Steps of the lightness scan in EnsureContrast.
    private const int LightnessSteps = 256;
    // The linear value of each sRGB channel byte, as WCAG defines it; the contrast search reads it thousands of times.
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(value =>
    {
        double c = value / 255d;
        return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
    }).ToArray();

    public bool IsOpaque => A == 255;

    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        var value = (text ?? "").Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length is not (6 or 8) || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)) return false;
        if (value.Length == 6) number |= 0xFF000000;
        color = new((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number);
        return true;
    }
    public static ThemeColor Parse(string text) => TryParse(text, out var color) ? color : throw new FormatException("Not a theme color: " + text);
    // Opaque colors keep the short form, so a palette written by hand reads back the same.
    public override string ToString() => IsOpaque ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    public ThemeColor WithAlpha(byte alpha) => this with { A = alpha };
    // The same color in the form the settings store it: opaque "#RRGGBB".
    public static string Opaque(string text) => Parse(text).WithAlpha(255).ToString();

    // This color drawn over an opaque background.
    public ThemeColor Over(ThemeColor background)
    {
        if (IsOpaque) return this;
        double a = A / 255d;
        byte Blend(byte top, byte bottom) => (byte)Math.Round(top * a + bottom * (1 - a));
        return new(255, Blend(R, background.R), Blend(G, background.G), Blend(B, background.B));
    }
    // Linear interpolation of every channel, alpha included.
    public static ThemeColor Mix(ThemeColor from, ThemeColor to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return new(Lerp(from.A, to.A), Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));
    }

    public double Luminance => .2126 * Linear[R] + .7152 * Linear[G] + .0722 * Linear[B];
    public static double Contrast(ThemeColor foreground, ThemeColor background)
    {
        var bottom = background.Over(Black);
        double a = foreground.Over(bottom).Luminance, b = bottom.Luminance;
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    // The lowest contrast against any of the backgrounds; a gradient surface passes only where every stop passes.
    public double MinimumContrast(IReadOnlyList<ThemeColor> backgrounds)
    {
        if (backgrounds.Count == 0) return 21;
        double lowest = double.PositiveInfinity;
        for (int i = 0; i < backgrounds.Count; i++) lowest = Math.Min(lowest, Contrast(this, backgrounds[i]));
        return lowest;
    }
    // How well this color meets every need: the lowest ratio of its contrast to the minimum asked for, so 1 or more means all
    // pass. A plain loop, as the lightness search calls it some hundreds of times.
    public double ContrastScore(IReadOnlyList<ContrastNeed> needs)
    {
        double lowest = double.PositiveInfinity;
        for (int i = 0; i < needs.Count; i++) lowest = Math.Min(lowest, Contrast(this, needs[i].Background) / needs[i].Minimum);
        return lowest;
    }

    // The nearest color of the same hue and saturation that meets every need. A color that already does is returned unchanged,
    // so a theme that passes looks exactly as designed. Lightness moves in whichever direction needs the smaller step; the step
    // is found on a fine scan and then refined. When no lightness meets every need (backgrounds both light and dark), the
    // lightness with the best worst case is used, and never one that scores worse than the color as written. A translucent color
    // is settled over the first background and comes back opaque once it has to move.
    public ThemeColor EnsureContrast(IReadOnlyList<ContrastNeed> needs)
    {
        if (needs.Count == 0 || ContrastScore(needs) >= 1) return this;
        var (h, s, l) = ToHsl(Over(needs[0].Background.Over(Black)));
        bool Passes(double lightness) => FromHsl(h, s, lightness).ContrastScore(needs) >= 1;
        double? best = null;
        foreach (int direction in new[] { 1, -1 })
        {
            for (int i = 1; i <= LightnessSteps; i++)
            {
                double at = Math.Clamp(l + direction * i / (double)LightnessSteps, 0, 1);
                if (Passes(at))
                {
                    // Refine between the last failing step and this one.
                    double fail = l + direction * (i - 1) / (double)LightnessSteps, pass = at;
                    for (int k = 0; k < 12; k++)
                    {
                        double mid = (fail + pass) / 2;
                        if (Passes(mid)) pass = mid; else fail = mid;
                    }
                    if (best is null || Math.Abs(pass - l) < Math.Abs(best.Value - l)) best = pass;
                    break;
                }
                if (at is 0 or 1) break;
            }
        }
        if (best is { } found) return FromHsl(h, s, found);
        double top = ContrastScore(needs), lightness = double.NaN;
        for (int i = 0; i <= LightnessSteps; i++)
        {
            double score = FromHsl(h, s, i / (double)LightnessSteps).ContrastScore(needs);
            if (score > top) { top = score; lightness = i / (double)LightnessSteps; }
        }
        return double.IsNaN(lightness) ? this : FromHsl(h, s, lightness);
    }
    // One minimum against several backgrounds, such as a surface and its gradient stops.
    public ThemeColor EnsureContrast(IReadOnlyList<ThemeColor> backgrounds, double minimum) => EnsureContrast(backgrounds.Select(background => new ContrastNeed(background, minimum)).ToList());
    // The single-background form, for callers outside a palette.
    public ThemeColor EnsureContrast(ThemeColor background, double minimum) => EnsureContrast([new ContrastNeed(background, minimum)]);

    public static (double H, double S, double L) ToHsl(ThemeColor color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, l = (max + min) / 2;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return ((h * 60 + 360) % 360, Math.Clamp(s, 0, 1), l);
    }
    public static ThemeColor FromHsl(double h, double s, double l, byte alpha = 255)
    {
        l = Math.Clamp(l, 0, 1); s = Math.Clamp(s, 0, 1); h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return new(alpha, Byte(r + m), Byte(g + m), Byte(b + m));
    }
}

// A background a color is drawn on and the contrast it must keep there.
public readonly record struct ContrastNeed(ThemeColor Background, double Minimum);
