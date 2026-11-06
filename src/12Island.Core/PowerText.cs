namespace TwelveIsland.Core;

// The power tooltip as a table: its column names, its rows (a label and a cell per column), whether the first row is a sum (the whole
// machine ≈ the parts added up), and notes under it.
public sealed record PowerTableRow(string Label, IReadOnlyList<string> Cells);
public sealed record PowerTable(IReadOnlyList<string> Columns, IReadOnlyList<PowerTableRow> Rows, bool Sum, IReadOnlyList<string> Notes);

// The words of the power and battery readings: the power tooltip's table (Table) and the battery tooltip's lines of the form "item  value"
// (two spaces between), in plain words and no implementation names; the few words the battery's line shows (Brief) and the power line's
// mean (Mean). The same readings, at their own length, are the screen reader's name (Spoken). Everything is localized here.
public static class PowerText
{
    public const string Gap = "  ";
    private static string Line(string label, string value) => label + Gap + value;

    public static string Watts(double watts) => PowerFormat.Watts(watts) + " W";
    public static string Percent(double percent) => MonitorFormat.Percent(percent / 100) + "%";

    // "17.0 W (estimated)" / "23.4 W (measured)".
    private static string Quality(PowerSample power) => power.Estimated ? Loc.T("估算", "estimated") : Loc.T("实测", "measured");
    private static string Bracket(string text) => Loc.T("（" + text + "）", " (" + text + ")");

    // Where the whole machine's figure comes from, the tooltip's last line: "电池放电（实测）", or the parts the estimate adds up and what
    // it leaves out: "CPU + GPU 估算，不含屏幕、硬盘、风扇等".
    private static string Source(PowerSample power) => power.Source switch
    {
        PowerSource.SystemMeter => Loc.T("整机功率计", "system meter") + Bracket(Loc.T("实测", "measured")),
        PowerSource.Battery => Loc.T("电池放电", "battery discharge") + Bracket(Loc.T("实测", "measured")),
        _ => string.Join(" + ", power.Parts.Select(part => part.Key switch { "cpu" => "CPU", "gpu" => "GPU", _ => Loc.T("内存", "memory") }).Distinct())
            + Loc.T(" 估算，不含屏幕、硬盘、风扇等", " estimate; display, drives, fans and others not included")
    };
    // The power tooltip with no reading: a dash and the reason.
    public static string NoPower(string reason) => Line(Loc.T("整机功耗", "System power"), "—") + "\n" + Line(Loc.T("原因", "Reason"), reason);

    // The power tooltip as a table, a column for the whole machine and one each for the CPU and the GPU where this machine has them:
    //              整机       CPU       GPU
    //   当前       42.2 W  ≈  34.3 W  +  7.9 W
    //   5 分钟内峰值  75.2 W     52.0 W     23.1 W
    //   5 分钟内均值  46.2 W     38.0 W     8.2 W
    // "≈" and "+" join the current row only where the whole machine is the estimate made of these parts. Notes under it: what could not
    // be read, then where the whole machine's figure comes from. Null without a reading (NoPower then).
    public static PowerTable? Table(Reading<PowerSample> power, PowerSummary? whole, PowerTrack cpu, PowerTrack gpu)
    {
        if (!power.TryGet(out var sample)) return null;
        double? cpuNow = PowerResolver.PartWatts(sample, "cpu"), gpuNow = PowerResolver.PartWatts(sample, "gpu");
        bool hasCpu = cpuNow is not null || cpu.Summary is not null, hasGpu = gpuNow is not null || gpu.Summary is not null || sample.Missing.Count > 0;
        string Cell(double? watts) => watts is { } value ? Watts(value) : "—";
        List<string> Cells(double? all, double? processor, double? graphics)
        {
            var cells = new List<string> { Cell(all) };
            if (hasCpu) cells.Add(Cell(processor));
            if (hasGpu) cells.Add(Cell(graphics));
            return cells;
        }
        var columns = new List<string> { ViewName(PowerView.Whole) };
        if (hasCpu) columns.Add(ViewName(PowerView.Cpu));
        if (hasGpu) columns.Add(ViewName(PowerView.Gpu));
        var rows = new List<PowerTableRow> { new(Loc.T("当前", "Now"), Cells(sample.Watts, cpuNow, gpuNow)) };
        if (whole is { Count: > 0 })
        {
            string span = PowerFormat.Span(whole.Seconds);
            rows.Add(new(Loc.T(span + "内峰值", "Peak, " + span), Cells(whole.Peak, cpu.Summary?.Peak, gpu.Summary?.Peak)));
            rows.Add(new(Loc.T(span + "内均值", "Mean, " + span), Cells(whole.Average, cpu.Summary?.Average, gpu.Summary?.Average)));
        }
        // The whole machine is these parts added up only when it is the estimate and every part it adds is a column.
        bool sum = sample.Source == PowerSource.Components && columns.Count > 1 && sample.Parts.All(part => part.Key is "cpu" or "gpu");
        var notes = new List<string>();
        if (sample.Missing.Count > 0) notes.Add(Line(Loc.T("未读取", "Not read"), string.Join(Loc.T("、", ", "), sample.Missing)));
        notes.Add(Line(Loc.T("来源", "Source"), Source(sample)));
        return new PowerTable(columns, rows, sum, notes);
    }

    // What the power line shows, after its unit: "整机", "CPU", "GPU".
    public static string ViewName(PowerView view) => view switch { PowerView.Cpu => "CPU", PowerView.Gpu => "GPU", _ => Loc.T("整机", "System") };

    // The power line's mean at its right end: "均值 46 W".
    public static string Mean(double watts) => Loc.T("均值 ", "Mean ") + PowerFormat.Compact(watts, 9999) + " W";

    // "放电 23.4 W", "充电 31.0 W", "已接通电源，未在充电", "已充满".
    public static string State(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging => Loc.T("充电", "Charging") + (battery.HasRate ? " " + Watts(battery.Watts) : ""),
        BatteryState.Full => Loc.T("已充满", "Full"),
        BatteryState.PluggedIn => Loc.T("已接通电源，未在充电", "Plugged in, not charging"),
        _ => Loc.T("放电", "Discharging") + (battery.HasRate ? " " + Watts(battery.Watts) : "")
    };

    // After the battery's charge on the page: the time, where there is one ("剩余 1:42", "充满 0:40"); nothing otherwise, since the
    // charge and the battery's drawing (the plug while on mains) already say full, plugged in or on battery.
    public static string Brief(BatterySample battery) => battery.State switch
    {
        BatteryState.Charging when battery.TimeToFull is { } full => Loc.T("充满 ", "Full in ") + PowerFormat.Clock(full),
        BatteryState.Discharging when battery.TimeLeft is { } left => Loc.T("剩余 " + PowerFormat.Clock(left), PowerFormat.Clock(left) + " left"),
        _ => ""
    };

    // The battery tooltip: charge, state, time, power mode, saver, health, number of batteries.
    public static string Battery(Reading<BatterySample> battery)
    {
        if (!battery.TryGet(out var sample))
            return Line(Loc.T("电池", "Battery"), "—") + "\n" + Line(Loc.T("原因", "Reason"), battery.Reason);
        var lines = new List<string> { Line(Loc.T("电池", "Battery"), Percent(sample.Percent) + (sample.RemainingWh is { } wh ? Bracket(Loc.T("储存电能 ", "") + wh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" Wh", " Wh stored")) : "")) };
        lines.Add(Line(Loc.T("状态", "State"), State(sample)));
        if (sample.TimeLeft is { } left) lines.Add(Line(Loc.T("剩余", "Remaining"), PowerFormat.Duration(left)));
        else if (sample.TimeToFull is { } full) lines.Add(Line(Loc.T("充满需", "To full"), PowerFormat.Duration(full)));
        if (BatteryModel.ModeName(sample.Mode) is { Length: > 0 } mode) lines.Add(Line(Loc.T("电源模式", "Power mode"), mode));
        if (sample.BatterySaver) lines.Add(Line(Loc.T("节电模式", "Battery saver"), Loc.T("开启", "on")));
        if (sample.Health is { } health && sample.FullWh is { } fullWh && sample.DesignWh is { } design)
            lines.Add(Line(Loc.T("健康度", "Health"), MonitorFormat.Percent(health) + "%" + Bracket(Loc.T("满充 ", "full ") + fullWh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh / " + Loc.T("设计 ", "design ") + design.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Wh")));
        if (sample.Count > 1) lines.Add(Line(Loc.T("电池数", "Batteries"), sample.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return string.Join("\n", lines);
    }

    // The screen reader's name: the tooltips' first items in one line, "item value" and commas.
    public static string Spoken(Reading<PowerSample> power, Reading<BatterySample> battery, bool batteryShown)
    {
        string text = power.TryGet(out var sample)
            ? Loc.T("整机功耗 ", "System power ") + Watts(sample.Watts) + Loc.T("，", ", ") + Quality(sample)
            : Loc.T("整机功耗 无读数", "System power: no reading");
        if (batteryShown && battery.TryGet(out var cell))
            text += Loc.T("；电池 ", "; battery ") + Percent(cell.Percent) + Loc.T("，", ", ") + State(cell)
                + (BatteryModel.ModeName(cell.Mode) is { Length: > 0 } mode ? Loc.T("；电源模式 ", "; power mode ") + mode : "");
        return text;
    }
}
