namespace TwelveIsland.Core;

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go. Owned by the panel, so that it
// outlives the row, which is built again whenever its width changes.
public sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private CompactMonitorItems _chosen = CompactMonitorItems.None, _arriving = CompactMonitorItems.None;

    // Readings that join the row because their first figures arrived (StripAdmission), not because the settings chose them: the
    // next Set brings them in from nothing with the row's own entrance, while the others slide aside.
    public void Arrive(CompactMonitorItems items) => _arriving |= items;

    // Returns whether anything moves. A new choice of readings (the settings) shows at once; motion: whether the row eases at all
    // (the app's animations).
    public bool Set(CompactMonitorItems chosen, CompactMonitorItems fitting, bool motion = true)
    {
        var arriving = _arriving & chosen;
        bool settled = motion && (chosen & ~arriving) == (_chosen & ~arriving) && _items.Count > 0, moving = false;
        _arriving = CompactMonitorItems.None;
        _chosen = chosen;
        foreach (var item in CompactMonitor.Order)
        {
            if ((chosen & item) == 0) { _items.Remove(item); continue; }
            bool fits = (fitting & item) != 0, arrives = (arriving & item) != 0 && !_items.ContainsKey(item);
            var eased = _items.TryGetValue(item, out var known) ? known : new Eased(fits && !(arrives && motion) ? 1 : 0, Eased.Seconds * .8);
            moving |= eased.Set(fits ? 1 : 0, settled || arrives && motion);
            _items[item] = eased;
        }
        return moving;
    }

    public double At(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) ? Math.Clamp(eased.At(now), 0, 1) : 0;
    public bool Moving(double now)
    {
        // Asked on every frame of a transition; the dictionary's own enumerator allocates nothing.
        foreach (var eased in _items.Values) if (eased.Moving(now)) return true;
        return false;
    }
}

// Which parts of the collapsed row wait for their first figures, and when they join: the temperatures of CPU and GPU, and each
// chosen drive letter's space and temperature (CompactFigures, CompactSources). No part shows before its first figure arrives,
// so the row never shows a dash for a sensor still being read, nor builds itself again when the sensor turns out to be missing:
// a machine without one never shows it, and the drives with none of their letters showing are left out. Once a part has shown it
// stays (a figure missing for a moment shows a dash) until its sensor is known to be missing.
public sealed class StripAdmission
{
    public const CompactMonitorItems Later = CompactMonitorItems.Storage | CompactMonitorItems.Power | CompactMonitorItems.Charge | CompactMonitorItems.Frames;
    // A part: a ring's temperature (Heat), a letter's space ("C:"), or a letter's temperature ("C:" + Heat).
    public const string Heat = "°";
    private readonly HashSet<(CompactMonitorItems Item, string Part)> _seen = [], _showing = [];
    // The machine's power draw and its own battery have given a reading, and the sampler has not since found it has none (a battery
    // reading is the machine's own and a desktop never shows one; the power reading stays away on a machine with no source). The frame
    // rate likewise: seen once counted, gone where Windows' frame events cannot be read.
    private bool _powerSeen, _chargeSeen, _framesSeen;
    // The readings left out of the row as it was last built; the drives until their first figures.
    public CompactMonitorItems Hidden { get; private set; } = Later;
    // The parts showing, as text: when it changes, the row is built again for the new widths.
    public string Layout { get; private set; } = "";

    // The parts the chosen figures and letters ask for, and whether each is known to be missing.
    private static IEnumerable<(CompactMonitorItems Item, string Part, bool Missing)> Wanted(IEnumerable<CompactReading> readings, MonitorSnapshot? latest, SensorStates states)
    {
        foreach (var reading in readings)
        {
            if (!CompactFigures.Has(reading.Item)) continue;
            bool usage = CompactFigures.Shows(reading, CompactFigures.Usage), heat = CompactFigures.Shows(reading, CompactFigures.Temperature);
            if (reading.Item == CompactMonitorItems.Storage)
                foreach (var letter in CompactSources.Of(reading))
                {
                    if (usage) yield return (reading.Item, letter, CompactSources.Missing(letter, latest, states));
                    if (heat) yield return (reading.Item, letter + Heat, CompactSources.HeatMissing(letter, latest, states));
                }
            else if (heat)
                // A temperature is text, not the dial's percentage: it is wanted whatever ShowValue says, and one sensor
                // saying it has none must not hide a figure another source (a driver) has already given.
                yield return (reading.Item, Heat, (reading.Item == CompactMonitorItems.Cpu ? states.Cpu : states.Gpu) == SensorState.None
                    && CompactFigures.Heat(reading.Item, latest) is null);
        }
    }
    private static bool Seen(CompactMonitorItems item, string part, MonitorSnapshot? latest) => item == CompactMonitorItems.Storage
        ? part.EndsWith(Heat, StringComparison.Ordinal) ? CompactSources.Heat(part[..^Heat.Length], latest) is not null : CompactSources.Share(part, latest) is not null
        : CompactFigures.Heat(item, latest) is not null;

    // Whether a part of a reading shows: a ring's temperature (Heat), a letter's space or temperature.
    public bool Shows(CompactMonitorItems item, string part) => _showing.Contains((item, part));
    // The drive letters that show, of those chosen, in order: a letter with its space or its temperature showing.
    public IReadOnlyList<string> Letters(CompactReading drives) =>
        [.. CompactSources.Of(drives).Where(letter => (CompactFigures.Shows(drives, CompactFigures.Usage) && Shows(CompactMonitorItems.Storage, letter))
            || (CompactFigures.Shows(drives, CompactFigures.Temperature) && Shows(CompactMonitorItems.Storage, letter + Heat)))];

    // Whether the system must still be read for the row although it does not show the drives yet, only because their first
    // figures have not come. Without this a row showing only the drives would have no width, so no readings would be asked for,
    // so the first figure would never come. False once each part is known to be missing, so a machine without them stops asking.
    public bool Awaits(IReadOnlyList<CompactReading> readings, MonitorSnapshot? latest, SensorStates states)
    {
        var pending = CompactMonitor.Shown(readings) & Hidden;
        if ((pending & CompactMonitorItems.Power) != 0 && states.Power != SensorState.None) return true;
        if ((pending & CompactMonitorItems.Charge) != 0 && states.Charge != SensorState.None) return true;
        if ((pending & CompactMonitorItems.Frames) != 0 && states.Frames != SensorState.None) return true;
        if ((pending & CompactMonitorItems.Storage) == 0) return false;
        return Wanted(readings.Where(reading => reading.Item == CompactMonitorItems.Storage), latest, states).Any(part => !part.Missing);
    }

    // The readings to leave out for this snapshot and the sensors' states, and those that join now (Hidden before, not now).
    public (CompactMonitorItems Hidden, CompactMonitorItems Arriving) Update(MonitorSnapshot? latest, IReadOnlyList<CompactReading> readings, SensorStates states)
    {
        _showing.Clear();
        var hidden = Later;
        var layout = new System.Text.StringBuilder();
        foreach (var (item, part, missing) in Wanted(readings, latest, states))
        {
            if (Seen(item, part, latest)) _seen.Add((item, part));
            if (missing || !_seen.Contains((item, part))) continue;
            _showing.Add((item, part));
            hidden &= ~item;
            layout.Append((int)item).Append(part).Append(' ');
        }
        // A reading of the machine's own: seen once it gives a figure, gone once the sampler knows there is none.
        if (latest?.Power.HasValue == true || states.Power == SensorState.Found) _powerSeen = true; else if (states.Power == SensorState.None) _powerSeen = false;
        if (latest?.Battery.HasValue == true || states.Charge == SensorState.Found) _chargeSeen = true; else if (states.Charge == SensorState.None) _chargeSeen = false;
        if (latest?.Frames.HasValue == true) _framesSeen = true; else if (states.Frames == SensorState.None) _framesSeen = false;
        // Only for readings the row chooses: one it does not show must not rebuild the row when its first figure comes.
        var chosen = CompactMonitor.Shown(readings);
        if (_powerSeen && (chosen & CompactMonitorItems.Power) != 0) { hidden &= ~CompactMonitorItems.Power; layout.Append((int)CompactMonitorItems.Power).Append(' '); }
        if (_chargeSeen && (chosen & CompactMonitorItems.Charge) != 0) { hidden &= ~CompactMonitorItems.Charge; layout.Append((int)CompactMonitorItems.Charge).Append(' '); }
        if (_framesSeen && (chosen & CompactMonitorItems.Frames) != 0) { hidden &= ~CompactMonitorItems.Frames; layout.Append((int)CompactMonitorItems.Frames).Append(' '); }
        var arriving = Hidden & ~hidden;
        Hidden = hidden;
        Layout = layout.ToString();
        return (hidden, arriving);
    }
}
