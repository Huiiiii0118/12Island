using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// The bolt before the power figures (the monitor page and the collapsed row), on a 16 unit grid, set in the theme's graphic language
// (PowerLook): plain, rounder for the island, straight edges for the HUD look and the industrial plate (IconStyle), and 8 by 8 squares
// for pixel art. A mark drawn dashed (for an estimate) or stroked keeps the treatments for marks added later.
internal enum PowerIcon { Bolt }

internal static class PowerIcons
{
    // Stroked line art on a 16 unit grid.
    private static readonly Dictionary<PowerIcon, Geometry> Lines = new()
    {
        [PowerIcon.Bolt] = Frozen("M9.2,1.5 L3.8,9 H7.6 L7,14.5 L12.2,7 H8.4 Z"),
    };
    // Marks drawn filled rather than stroked.
    private static bool Solid(PowerIcon icon) => icon is PowerIcon.Bolt;

    // The 8 by 8 squares of pixel art, one string a row.
    private static readonly Dictionary<PowerIcon, string[]> Pixels = new()
    {
        [PowerIcon.Bolt] = ["...###..", "..###...", ".###....", "..#####.", "....###.", "...###..", "..##....", "..#....."],
    };

    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // How the theme draws a mark: Plain, Round (the island), Angular (the HUD look) or Pixel.
    private enum Treatment { Plain, Round, Angular, Pixel }
    private static Treatment TreatmentOf(PowerStyle style) => style.Look.Gauge switch
    {
        PowerGauge.Round => Treatment.Round, PowerGauge.Blocks => Treatment.Pixel, PowerGauge.Chamfer => Treatment.Angular, _ => Treatment.Plain
    };

    private static readonly Dictionary<PowerIcon, Geometry> Angles = [];
    private static Geometry Angular(PowerIcon icon) => Angles.TryGetValue(icon, out var known) ? known : Angles[icon] = Flat(Lines[icon]);
    // The drawing with its curves turned into a few straight edges, as the HUD look cuts its corners.
    private static Geometry Flat(Geometry geometry) { var flat = geometry.GetFlattenedPathGeometry(.05, ToleranceType.Absolute); flat.Freeze(); return flat; }

    // Pens by color, thickness (on the 16 unit grid), treatment and dash.
    private static readonly Dictionary<(string Color, double Thickness, Treatment Kind, bool Dashed), Pen> Pens = [];
    private static Pen Stroke(string color, double thickness, Treatment kind, bool dashed)
    {
        if (Pens.TryGetValue((color, thickness, kind, dashed), out var known)) return known;
        bool square = kind == Treatment.Angular;
        var pen = new Pen(UI.Brush(color), thickness)
        {
            StartLineCap = square ? PenLineCap.Square : PenLineCap.Round, EndLineCap = square ? PenLineCap.Square : PenLineCap.Round,
            LineJoin = square ? PenLineJoin.Miter : PenLineJoin.Round, MiterLimit = 3
        };
        if (dashed) pen.DashStyle = new DashStyle([1.7, 1.5], 0);
        pen.Freeze();
        if (Pens.Count > 96) Pens.Clear();
        return Pens[(color, thickness, kind, dashed)] = pen;
    }

    // The mark in the box (its shorter side is its size), in the color; dashed for an estimate. ppd: device pixels per DIP.
    public static void Draw(DrawingContext drawing, PowerIcon icon, Rect box, string color, PowerStyle style, double ppd, bool dashed = false)
    {
        double size = Math.Min(box.Width, box.Height);
        if (size < 3) return;
        var kind = TreatmentOf(style);
        double x = box.X + (box.Width - size) / 2, y = box.Y + (box.Height - size) / 2;
        if (kind == Treatment.Pixel) { Pixel(drawing, icon, x, y, size, color, ppd, dashed); return; }
        double scale = size / 16;
        // The stroke in DIP: a little heavier for the island.
        double dip = kind switch { Treatment.Round => 1.55, Treatment.Angular => 1.25, _ => 1.35 };
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        var geometry = kind == Treatment.Angular ? Angular(icon) : Lines[icon];
        if (Solid(icon)) drawing.DrawGeometry(UI.Brush(color), null, geometry);
        else drawing.DrawGeometry(null, Stroke(color, dip / scale, kind, dashed), geometry);
        drawing.Pop();
    }

    // Squares on whole device pixels (as the island's steps are): a dashed mark leaves every other square out.
    private static void Pixel(DrawingContext drawing, PowerIcon icon, double x, double y, double size, string color, double ppd, bool dashed)
    {
        double cell = Math.Max(1, Math.Round(size / 8 * ppd)) / ppd;
        double left = Math.Round(x * ppd) / ppd, top = Math.Round(y * ppd) / ppd;
        var brush = UI.Brush(color);
        var rows = Pixels[icon];
        for (int row = 0; row < rows.Length; row++)
            for (int column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] != '#') continue;
                if (dashed && (row + column) % 2 == 1) continue;
                drawing.DrawRectangle(brush, null, new Rect(left + column * cell, top + row * cell, cell, cell));
            }
    }
}

// The first time the power rows are shown, the UI thread built the icons' geometry and compiled the rows' code on the spot (about 25 ms, with
// the page's own first layout). Once the app has started and has nothing else to do, a pool thread does both: the geometry (frozen, so any
// thread may use it) and the code of the rows, the tooltip's words and the figures. One pass, a few milliseconds of one pool thread, and
// nothing runs again while the panel is idle.
internal static class PowerWarm
{
    public static void Start(Dispatcher dispatcher) => dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ThreadPool.QueueUserWorkItem(_ => Run()));

    private static void Prepare(MethodBase method)
    {
        if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() is not null) RuntimeHelpers.PrepareMethod(method.MethodHandle);
    }

    private static void Run()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(PowerIcons).TypeHandle);
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in new[] { typeof(PowerBlock), typeof(PowerGlyphs), typeof(PowerIcons), typeof(PowerStyle), typeof(PowerText), typeof(PowerResolver), typeof(BatteryModel), typeof(PowerFormat) })
                foreach (var method in type.GetMethods(All)) Prepare(method);
            // The words and the figures once, with made-up readings.
            var sample = new PowerSample(20, PowerOrigin.Estimated, PowerSource.Components, [new PowerPart("cpu", "CPU", 12, "RAPL")]);
            var battery = BatteryModel.Build(new BatteryInputs([new BatteryCell("warm", 30_000, 60_000, 66_000, -10_000, false, true)], false, false, PowerMode.Balanced));
            PowerText.Table(Reading<PowerSample>.Of(sample), new PowerSummary(30, 20, 3), PowerTrack.None, PowerTrack.None);
            PowerText.Battery(battery);
            PowerText.Spoken(Reading<PowerSample>.Of(sample), battery, true);
            PowerFormat.Clock(TimeSpan.FromMinutes(90)); PowerFormat.Duration(TimeSpan.FromMinutes(90)); PowerFormat.Watts(12.3);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
