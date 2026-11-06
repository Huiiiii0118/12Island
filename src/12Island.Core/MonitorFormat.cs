using System.Globalization;

namespace TwelveIsland.Core;

// Number formatting for the system monitor page, after ftop's Format by Nongfsq (https://github.com/Nongfsq/ftop,
// MIT License; see ThirdPartyNotices.txt). Numbers and units come apart because the page draws units smaller and
// lighter than their numbers.
public static class MonitorFormat
{
    public readonly record struct Quantity(string Number, string Unit)
    {
        public string Text => Number + " " + Unit;
    }

    // Whole percent of a 0..1 share: "37".
    public static string Percent(double fraction) => WholePercent(fraction).ToString(CultureInfo.InvariantCulture);
    // The same as a number from 0 to 100, for a figure kept ready for each value (MonitorStrip).
    public static int WholePercent(double fraction) => (int)Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * 100);

    // A process's processor use as a share of the whole machine: percentOfOneCore (250 is two and a half cores) over the
    // logical processors. One decimal below 10, so a small busy process does not read "0"; whole from there, at most "100".
    public static string ProcessShare(double percentOfOneCore, int cores)
    {
        double value = Math.Clamp(double.IsFinite(percentOfOneCore) ? percentOfOneCore / Math.Max(cores, 1) : 0, 0, 100);
        return value < 9.95 ? Decimal(value) : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
    }

    // Bytes per second as KB/s below 1 MB/s, then MB/s, then GB/s (decimal units, as Task Manager's network speeds).
    public static Quantity Rate(double bytesPerSecond)
    {
        double value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        if (value >= 999_500_000) return new(Decimal(value / 1_000_000_000), "GB/s");
        if (value >= 999_500) return new(Decimal(value / 1_000_000), "MB/s");
        return new(((int)Math.Round(value / 1000)).ToString(CultureInfo.InvariantCulture), "KB/s");
    }

    // A rate in three significant digits and one letter, for the collapsed row: "5.12" K, "84.2" K, "512" K, "1.24" M, "120" M.
    // Every figure is about as wide as the widest ("99.9M"), so a small rate leaves no wide gap after it.
    public static Quantity CompactRate(double bytesPerSecond)
    {
        double value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        static string Short(double v) => v < 9.995 ? v.ToString("0.00", CultureInfo.InvariantCulture)
            : v < 99.95 ? Decimal(v) : ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        if (value >= 999_500_000) return new(Short(value / 1_000_000_000), "G");
        if (value >= 999_500) return new(Short(value / 1_000_000), "M");
        return new(Short(value / 1000), "K");
    }

    // Memory in binary gigabytes with one decimal: "12.4".
    public static string Gigabytes(ulong bytes) => Decimal(bytes / 1_073_741_824d);

    // Whole gigabytes when exact, otherwise one decimal: "32", "15.8".
    public static string GigabytesCompact(ulong bytes)
    {
        double value = bytes / 1_073_741_824d;
        return Math.Abs(value - Math.Round(value)) < .05 ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) : Decimal(value);
    }

    // Process memory: "512M" below 1000 MB, "4.1G" from there.
    public static string ProcessMemory(ulong bytes)
    {
        double megabytes = bytes / 1_048_576d;
        return megabytes >= 999.5 ? Decimal(megabytes / 1024) + "G" : ((int)Math.Round(megabytes)).ToString(CultureInfo.InvariantCulture) + "M";
    }

    public static string Gigahertz(double megahertz) => (megahertz / 1000).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Decimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
