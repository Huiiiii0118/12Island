namespace TwelveIsland.Core;

// Calendar dates are local to the user. The last date includes the whole day;
// comparing local dates also handles changes in UTC offset across the range.
public sealed record UsageDateRange
{
    public DateTime Start { get; }
    public DateTime End { get; }
    public UsageDateRange(DateTime start, DateTime end)
    {
        if (start.Date > end.Date) throw new ArgumentException(Loc.T("开始日期不能晚于结束日期。", "The start date cannot be later than the end date."));
        Start = start.Date; End = end.Date;
    }
    public bool Contains(DateTimeOffset time) => time.LocalDateTime.Date >= Start && time.LocalDateTime.Date <= End;
    public static UsageDateRange Recent(int days, DateTime today)
    {
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        return new(today.Date.AddDays(1 - days), today.Date);
    }
}

public sealed record ModelUsageRow(ProviderId Provider, string Model, IReadOnlyList<TokenEntry> Entries);
public sealed record ModelUsageResult(IReadOnlyList<ModelUsageRow> Rows, IReadOnlyList<TokenEntry> Entries, IReadOnlyList<ProviderId> IncompleteProviders);

public static class ModelUsage
{
    public static ModelUsageResult Filter(IEnumerable<ProviderSnapshot> snapshots, IReadOnlySet<ProviderId> selected, UsageDateRange range)
    {
        var providers = snapshots.Where(s => selected.Contains(s.Id)).ToList();
        var rows = providers.SelectMany(s => s.Entries.Where(t => range.Contains(t.Time)).Select(t => (s.Id, Token: t)))
            .GroupBy(item => (item.Id, item.Token.Model))
            .Select(group => new ModelUsageRow(group.Key.Id, group.Key.Model, group.Select(item => item.Token).ToArray()))
            .OrderByDescending(row => row.Entries.Sum(t => t.Total)).ThenBy(row => row.Provider).ThenBy(row => row.Model, StringComparer.Ordinal)
            .ToArray();
        // Keep records from different applications, even if their message keys or model names coincide.
        return new(rows, rows.SelectMany(row => row.Entries).ToArray(), providers.Where(s => !s.UsageAvailable || !s.UsageComplete ||
            s.UsageStart is { } start && start.Date > range.Start).Select(s => s.Id).Distinct().ToArray());
    }
}
