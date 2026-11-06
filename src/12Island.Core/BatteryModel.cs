namespace TwelveIsland.Core;

// The machine's own battery (a laptop's or tablet's), not a wireless device's: one reading for
// all the machine's batteries together. The probe (SystemSampler) hands over each battery's report and the system's power state in
// BatteryInputs; BatteryModel.Build works out the rest, so every rule here is tested with made-up reports.

// What the batteries are doing as a whole. PluggedIn: on mains and not charging, below full (a charge limit or a battery held
// at a threshold, as some laptops do to spare the cell).
public enum BatteryState { Discharging, Charging, Full, PluggedIn }

// Windows' power mode (the overlay scheme the Settings app calls Power mode).
public enum PowerMode { Unknown, BestEfficiency, Balanced, BestPerformance, BetterPerformance }

// One battery's report. Milliwatt-hours and milliwatts as Windows gives them; null where the battery does not say. RateMilliwatts
// is positive while charging and negative while discharging. Charging, Discharging: the battery's own status (both false: idle).
public sealed record BatteryCell(string Id, double? RemainingMwh, double? FullMwh, double? DesignMwh, double? RateMilliwatts, bool Charging = false, bool Discharging = false);

// What the system says besides the batteries. AcOnline null: unknown. BatterySaver: Windows' Battery saver is on. WindowsPercent and
// WindowsRemaining are the system's own figures (GetSystemPowerStatus, PowerManager), used where the reports lack capacities or a rate.
public sealed record BatteryInputs(IReadOnlyList<BatteryCell> Cells, bool? AcOnline = null, bool BatterySaver = false, PowerMode Mode = PowerMode.Unknown,
    double? WindowsPercent = null, TimeSpan? WindowsRemaining = null);

// The batteries as the page shows them. Percent is 0 to 100; Watts the size of the charge or discharge rate (0 when idle or unknown, see
// HasRate). TimeLeft is set while discharging and TimeToFull while charging, each only when it can be told.
public sealed record BatterySample(double Percent, BatteryState State, int Count)
{
    public double Watts { get; init; }
    public bool HasRate { get; init; }
    public TimeSpan? TimeLeft { get; init; }
    public TimeSpan? TimeToFull { get; init; }
    public double? RemainingWh { get; init; }
    public double? FullWh { get; init; }
    public double? DesignWh { get; init; }
    // The full-charge capacity against the design capacity, 0 to 1; null without both.
    public double? Health { get; init; }
    public bool BatterySaver { get; init; }
    public bool AcOnline { get; init; }
    public PowerMode Mode { get; init; }
    public bool Charging => State == BatteryState.Charging;
}

public static class BatteryModel
{
    // A rate under this is rounding noise in a battery that is idle; a time computed from a smaller one would run to days.
    public const double RateFloorMilliwatts = 500;
    // Times past this are not worth saying ("12 天"): the page leaves them out.
    public static readonly TimeSpan LongestTime = TimeSpan.FromHours(48);
    // A battery at or above this share on mains is full (laptops stop charging a little short of 100).
    public const double FullPercent = 97;

    public static Reading<BatterySample> Build(BatteryInputs inputs)
    {
        var cells = inputs.Cells;
        if (cells.Count == 0) return Reading<BatterySample>.Unavailable(NoBattery);

        // Capacities add up over the batteries; the share is the sum of what remains over the sum of what fits, so a
        // large battery weighs more than a small one (two batteries at 50 % and 100 % of 30 and 60 Wh are 83 %, not 75 %).
        // A total stands for the machine only when every battery gives its part: a sum missing one battery's capacity or rate would
        // be shown as the whole (a health of 100 %, a time twice too long, half the discharge called measured), so it is left out.
        double remaining = 0, full = 0, design = 0, rate = 0;
        bool hasRemaining = true, hasFull = true, hasDesign = true, hasRate = true, charging = false, discharging = false, sharable = true;
        double shareRemaining = 0, shareFull = 0;
        foreach (var cell in cells)
        {
            if (cell.RemainingMwh is { } r && Plausible(r)) remaining += r; else hasRemaining = false;
            if (cell.FullMwh is { } f && Plausible(f) && f > 0) full += f; else hasFull = false;
            if (cell.DesignMwh is { } d && Plausible(d) && d > 0) design += d; else hasDesign = false;
            if (cell.RateMilliwatts is { } w && double.IsFinite(w)) rate += w; else hasRate = false;
            charging |= cell.Charging; discharging |= cell.Discharging;
            if (cell.RemainingMwh is { } cr && Plausible(cr) && cell.FullMwh is { } cf && Plausible(cf) && cf > 0) { shareRemaining += cr; shareFull += cf; } else sharable = false;
        }
        double? percent = null;
        if (shareFull > 0 && (sharable || !inputs.WindowsPercent.HasValue)) percent = shareRemaining / shareFull * 100;
        else if (inputs.WindowsPercent is { } system && double.IsFinite(system)) percent = system;
        if (percent is not { } share) return Reading<BatterySample>.Unavailable(Loc.T("电池无电量数据", "battery reports no charge"));
        share = Math.Clamp(share, 0, 100);

        // What the batteries do. The reports' own statuses first; the sign of the rate when they say neither; mains alone
        // otherwise. A rate within the floor is idle.
        bool moving = hasRate && Math.Abs(rate) >= RateFloorMilliwatts;
        BatteryState state;
        if (charging && !discharging || !discharging && moving && rate > 0) state = BatteryState.Charging;
        else if (discharging && !charging || !charging && moving && rate < 0) state = BatteryState.Discharging;
        else if (inputs.AcOnline == false) state = BatteryState.Discharging;
        else state = share >= FullPercent ? BatteryState.Full : BatteryState.PluggedIn;
        // A mains connection that says it is there outweighs a battery claiming to discharge by a hair: it is held at its threshold.
        if (state == BatteryState.Discharging && inputs.AcOnline == true && !moving) state = share >= FullPercent ? BatteryState.Full : BatteryState.PluggedIn;

        double watts = hasRate ? Math.Abs(rate) / 1000 : 0;
        TimeSpan? left = null, toFull = null;
        if (state == BatteryState.Discharging)
        {
            if (inputs.WindowsRemaining is { } known && known > TimeSpan.Zero && known <= LongestTime) left = known;
            else if (hasRemaining && moving && rate < 0) left = Bounded(TimeSpan.FromHours(remaining / -rate));
        }
        else if (state == BatteryState.Charging && hasRemaining && hasFull && moving && rate > 0)
            toFull = Bounded(TimeSpan.FromHours(Math.Max(0, full - remaining) / rate));

        double? health = hasFull && hasDesign ? Math.Clamp(full / design, 0, 1) : null;
        return new BatterySample(share, state, cells.Count)
        {
            Watts = watts, HasRate = hasRate && moving, TimeLeft = left, TimeToFull = toFull,
            RemainingWh = hasRemaining ? remaining / 1000 : null, FullWh = hasFull ? full / 1000 : null, DesignWh = hasDesign ? design / 1000 : null,
            Health = health, BatterySaver = inputs.BatterySaver, AcOnline = inputs.AcOnline == true || state is BatteryState.Charging or BatteryState.Full or BatteryState.PluggedIn,
            Mode = inputs.Mode
        };
    }

    public static string NoBattery => Loc.T("无电池", "no battery");

    private static bool Plausible(double mwh) => double.IsFinite(mwh) && mwh >= 0 && mwh < 1e9;
    private static TimeSpan? Bounded(TimeSpan time) => time > TimeSpan.Zero && time <= LongestTime ? time : null;

    // The power the battery supplies, for the whole-machine reading (PowerResolver): the discharge rate, only while discharging.
    public static Reading<double> Discharge(BatteryInputs inputs, Reading<BatterySample> battery)
        // On mains (a charger weaker than the load) the battery covers only the shortfall, which is not what the machine draws.
        => battery.TryGet(out var sample) && sample.State == BatteryState.Discharging && sample.HasRate && inputs.AcOnline != true ? sample.Watts : Reading<double>.Unavailable(
            Loc.T("未使用电池供电", "not on battery"));

    // The charge at or under which the battery is low (critical), and elevated within ten points above it.
    public const int LowPercent = 15;
    // Low is at or under the threshold, elevated within ten points above it; a battery on mains is never low. Battery saver is a
    // state of its own (Saver) and does not change the level.
    public static MemoryLevel Level(BatterySample? battery, int low)
    {
        if (battery is null || battery.State != BatteryState.Discharging) return MemoryLevel.Normal;
        int percent = (int)Math.Round(battery.Percent);
        return percent <= low ? MemoryLevel.Critical : percent <= low + 10 ? MemoryLevel.Elevated : MemoryLevel.Normal;
    }

    public static string ModeName(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => Loc.T("最佳能效", "Best power efficiency"),
        PowerMode.Balanced => Loc.T("平衡", "Balanced"),
        PowerMode.BestPerformance => Loc.T("最佳性能", "Best performance"),
        PowerMode.BetterPerformance => Loc.T("更佳性能", "Better performance"),
        _ => ""
    };

    // Windows' overlay scheme GUIDs (powrprof.h). Anything else (a vendor's own scheme) is unknown.
    public static PowerMode ModeOf(Guid overlay) => overlay.ToString().ToLowerInvariant() switch
    {
        "961cc777-2547-4f9d-8174-7d86181b8a7a" => PowerMode.BestEfficiency,
        "00000000-0000-0000-0000-000000000000" => PowerMode.Balanced,
        "ded574b5-45a0-4f42-8737-46345c09c238" => PowerMode.BestPerformance,
        "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => PowerMode.BetterPerformance,
        _ => PowerMode.Unknown
    };
}
