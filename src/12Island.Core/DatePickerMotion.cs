namespace TwelveIsland.Core;

// The custom date range picker's motion (TwelveIsland.ModelDatePicker) as plain functions, so it can be checked without WPF. The
// picker is built once and changes in place, as the system monitor page does: the start and end markers glide between days as
// the monitor's sort pill glides, the range band eases in and out cell by cell, and a month change slides the grid in the
// direction of travel as the panel's page switch does, the old month leaving before the new one arrives so two sets of figures
// never fade over each other.

// The durations in milliseconds: the panel's own feedback timings, which do not follow the animation length chosen in settings
// (the panel's hover and press feedback do not either). A glide is the monitor's sort pill (Soft.Choose), a band, a message or a
// changed date the short swap (Soft.Short), a month leaves in about a seventh of a second and arrives in the monitor's move
// (Soft.Move), and an enabled state eases in the panel's hover time (Soft.Hover).
// Glide: a marker or the active field's outline moving. Band: a cell of the range band. Leave and Arrive: a month change.
// Message: the validation message opening or closing. Enable: a button becoming available or not. Dissolve: a changed date.
public readonly record struct DatePickerTiming(int Glide, int Band, int Leave, int Arrive, int Message, int Enable, int Dissolve)
{
    // Soft.Short.
    private const int Short = 240;

    // What the picker uses.
    public static readonly DatePickerTiming Fixed = new(Glide: (int)(Eased.Seconds * 750), Band: Short, Leave: 144, Arrive: (int)(Eased.Seconds * 1000),
        Message: Short, Enable: (int)(Eased.Hover * 1000), Dissolve: Short);

    // The new month starts to arrive once the old one has nearly gone (DatePickerMotion.HandOver).
    public int ArriveDelay => (int)Math.Round(Leave * DatePickerMotion.HandOver);
    public int MonthLength => ArriveDelay + Arrive;
}

// How a marker reaches a newly chosen day.
public enum MarkerMove
{
    // Where it was.
    Stay,
    // Across the grid from the day it was on, as the sort pill does.
    Glide,
    // In place: it was not shown, or the month changed and the grid slides in with it.
    Appear,
    // Its day is not in the month shown.
    Hide
}

// The part of a day's cell the range band covers: the whole cell between the ends, the half toward the range at each end
// (beneath the end's marker, so the band runs into it), or none.
public enum BandPart { None, Full, Leading, Trailing }

// One frame of a month change: where the old and new grids (and their headers) are, as offsets in DIPs and opacities.
public readonly record struct MonthSlideFrame(double OutX, double OutOpacity, double InX, double InOpacity, bool Done);

public static class DatePickerMotion
{
    public const int Columns = 7, Rows = 6, Cells = Columns * Rows;
    // How far a month travels as it leaves or arrives: the page switch's.
    public const double Travel = 16;
    // The share of its leaving after which the old month is faint enough (under a tenth) for the new one to start.
    public const double HandOver = .75;

    // The first day the grid of a month shows: the Monday on or before its first day.
    public static DateTime FirstShown(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
    }

    // The cell a day takes in a month's grid, -1 when the grid does not show it.
    public static int CellOf(DateTime month, DateTime day)
    {
        double offset = (day.Date - FirstShown(month)).TotalDays;
        return offset is >= 0 and < Cells ? (int)offset : -1;
    }

    public static int Column(int cell) => cell % Columns;
    public static int Row(int cell) => cell / Columns;

    // The way a month change travels: 1 toward later months, -1 toward earlier ones, 0 for the same month.
    public static int Direction(DateTime from, DateTime to) => Math.Sign(to.Year * 12 + to.Month - (from.Year * 12 + from.Month));

    // How a marker that was on cell from (-1: not shown) reaches cell to. Within one month it glides; a month change moves the
    // whole grid, so the marker comes with it in place.
    public static MarkerMove Plan(int from, int to, bool sameMonth)
    {
        if (to < 0) return MarkerMove.Hide;
        if (from < 0 || !sameMonth) return MarkerMove.Appear;
        return from == to ? MarkerMove.Stay : MarkerMove.Glide;
    }

    public static BandPart Band(DateTime day, DateTime start, DateTime end)
    {
        day = day.Date; start = start.Date; end = end.Date;
        if (start >= end || day < start || day > end) return BandPart.None;
        return day == start ? BandPart.Leading : day == end ? BandPart.Trailing : BandPart.Full;
    }

    // The validation message shows while the start lies after the end.
    public static bool ShowsError(DateTime start, DateTime end) => start.Date > end.Date;

    // A month change at elapsed milliseconds: the old month leaves against the direction of travel while it fades, and once it
    // has nearly gone (ArriveDelay) the new one comes in from the other side on the monitor's move (Curves.Follow).
    public static MonthSlideFrame Slide(double elapsed, int direction, DatePickerTiming timing)
    {
        double sign = direction < 0 ? -1 : 1;
        double leaving = Curves.Follow(elapsed / Math.Max(1, timing.Leave));
        double arriving = Curves.Follow((elapsed - timing.ArriveDelay) / Math.Max(1, timing.Arrive));
        return new(-sign * Travel * leaving, 1 - leaving, sign * Travel * (1 - arriving), arriving, elapsed >= timing.MonthLength);
    }
}
