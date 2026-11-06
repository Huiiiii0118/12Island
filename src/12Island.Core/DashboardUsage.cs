namespace TwelveIsland.Core;

// Keep the two original snapshots separate: quota failures must not replace valid usage data.
public sealed record DashboardColumn(AppEntry Entry, ProviderSnapshot Quota, ProviderSnapshot Usage);

public static class DashboardUsage
{
    public static IReadOnlyList<DashboardColumn> Columns(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
        => settings.EnabledApps.Select(entry => new DashboardColumn(entry,
            Quota(entry, snapshots), Find(snapshots, entry.UsageSource))).ToList();

    // Reusing an application on several dashboard columns does not multiply its model totals.
    public static IReadOnlyList<ProviderSnapshot> Statistics(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
        => settings.StatisticsProviders.Select(id => Find(snapshots, id)).ToList();

    // The local Claude snapshot serves only the rows that chose it; statistics and online rows use the main snapshot.
    private static ProviderSnapshot Find(IReadOnlyList<ProviderSnapshot> snapshots, ProviderId id)
        => snapshots.FirstOrDefault(snapshot => snapshot.Id == id && !snapshot.Configurations.Contains(ProviderCatalog.LocalQuota)) ?? new ProviderSnapshot(id);

    public static ProviderSnapshot Quota(AppEntry entry, IReadOnlyList<ProviderSnapshot> snapshots) => WithBalanceTarget(entry, Find(entry, snapshots));

    // The row's target balance becomes the total of its account balance, so the rings, the panel and the level notices
    // read the balance as a used share. Several rows can share one snapshot, so the snapshot itself is left unchanged.
    private static ProviderSnapshot WithBalanceTarget(AppEntry entry, ProviderSnapshot snapshot)
    {
        if (entry.BalanceTarget is not { } target || !ProviderCatalog.HasAccountBalance(entry.QuotaSource) || !snapshot.Metrics.Any(ProviderCatalog.IsAccountBalance)) return snapshot;
        var copy = snapshot.Copy();
        copy.Metrics = [.. snapshot.Metrics.Select(metric => ProviderCatalog.IsAccountBalance(metric) ? metric with { Total = target, TotalIsTarget = true } : metric)];
        return copy;
    }

    private static ProviderSnapshot Find(AppEntry entry, IReadOnlyList<ProviderSnapshot> snapshots)
        => ProviderCatalog.ReadsLocalQuota(entry)
            ? snapshots.FirstOrDefault(snapshot => snapshot.Id == entry.QuotaSource && snapshot.Configurations.Contains(ProviderCatalog.LocalQuota)) ?? new ProviderSnapshot(entry.QuotaSource)
            : ProviderCatalog.IsBuiltIn(entry.QuotaSource) ? Find(snapshots, entry.QuotaSource)
            : snapshots.FirstOrDefault(snapshot => snapshot.Id == entry.QuotaSource && snapshot.Configurations.Contains(entry.ConfigurationKey))
                ?? new ProviderSnapshot(entry.QuotaSource);
}
