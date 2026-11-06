namespace TwelveIsland.Core;

// Keeps a ranked list from reshuffling on every sample. Ported from ftop's SteadyRanking by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see ThirdPartyNotices.txt).
//
// Readings of busy processes cross each other all the time, so a list sorted by the current reading never rests. This ranks
// by a level that rises with the reading at once and falls slowly, and lets an entry pass another only when it is clearly ahead.
public sealed class SteadyRanking(double relative, double margin, double fall)
{
    private Dictionary<string, double> _levels = [];
    private List<string> _order = [];

    // An entry passes another when its level is above the other's by this share of it, plus Margin in the readings' unit.
    public double Relative { get; } = relative;
    public double Margin { get; } = margin;
    // Seconds for a level to lose about two thirds of its distance to a lower reading.
    public double Fall { get; } = fall;

    // For processor use in percent of one core.
    public static SteadyRanking Cpu() => new(.12, 1, 4);
    // For memory in bytes.
    public static SteadyRanking Memory() => new(.04, 24 * 1_048_576, 4);

    // The order to show the entries in, elapsed seconds after the last call. With hold nothing already listed changes rank;
    // entries that left are dropped and new ones go last.
    public IReadOnlyList<string> Step(IReadOnlyList<(string Id, double Value)> entries, double elapsed, bool hold = false)
    {
        double keep = Fall > 0 ? Math.Exp(-Math.Max(0, elapsed) / Fall) : 0;
        var next = new Dictionary<string, double>();
        foreach (var (id, value) in entries)
        {
            if (next.ContainsKey(id)) continue;
            next[id] = _levels.TryGetValue(id, out var level) ? Math.Max(value, value + (level - value) * keep) : value;
        }
        _levels = next;

        var result = _order.Where(next.ContainsKey).ToList();
        var seen = result.ToHashSet();
        bool known = seen.Count > 0;
        var arrivals = entries.Select((entry, index) => (entry, index)).Where(item => seen.Add(item.entry.Id))
            .OrderByDescending(item => item.entry.Value).ThenBy(item => item.index).Select(item => item.entry.Id);
        result.AddRange(arrivals);
        // Arrivals are placed even under a hold when there was no list before.
        if (!hold || !known)
        {
            for (int index = 1; index < result.Count; index++)
            {
                double level = next[result[index]];
                int target = result.FindIndex(0, index, other => level > next[other] * (1 + Relative) + Margin);
                if (target < 0) continue;
                var id = result[index]; result.RemoveAt(index); result.Insert(target, id);
            }
        }
        _order = result;
        return result;
    }

    // The samples in the order this ranking settles on.
    public IReadOnlyList<ProcessSample> Arrange(IReadOnlyList<ProcessSample> samples, Func<ProcessSample, double> value, double elapsed, bool hold)
    {
        var byId = new Dictionary<string, ProcessSample>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples) byId.TryAdd(sample.Id, sample);
        return Step(samples.Select(sample => (sample.Id, value(sample))).ToList(), elapsed, hold).Select(id => byId[id]).ToList();
    }
}
