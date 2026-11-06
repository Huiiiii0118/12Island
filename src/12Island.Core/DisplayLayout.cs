namespace TwelveIsland.Core;

// Decides which connected monitor each panel window sits on. The main window always has a monitor: the primary one if
// it shows a panel, otherwise the first chosen monitor in the order given. Every other chosen monitor gets a window of its own.
public static class DisplayLayout
{
    public sealed record Plan(string Main, IReadOnlyList<string> Others);

    public static Plan Arrange(DisplayPlacement placement, IReadOnlyCollection<string> chosen, IReadOnlyList<(string Id, bool Primary)> monitors)
    {
        if (monitors.Count == 0) return new Plan("", []);
        string primary = monitors.FirstOrDefault(m => m.Primary).Id ?? monitors[0].Id;
        var shown = placement == DisplayPlacement.Chosen ? monitors.Where(m => chosen.Contains(m.Id)).Select(m => m.Id).Distinct().ToList() : [];
        if (shown.Count == 0) return new Plan(primary, []);
        string main = shown.Contains(primary) ? primary : shown[0];
        return new Plan(main, shown.Where(id => id != main).ToList());
    }
}
