namespace TwelveIsland.Core;

// Apple Music's repeat button shows one glyph for "off" and "all" and another for "one" (measured: U+10029E and
// U+10029F); only its name, in the interface language, tells the first two apart. The button cycles off → all → one
// → off, so the name read just before "one" is "all" and the name read just after it is "off". The Traditional Chinese
// names were read from the app; the names of other languages are learnt once the button has gone round.
public sealed class AppleMusicRepeat
{
    public const string RepeatGlyph = "\U0010029E", RepeatOneGlyph = "\U0010029F";
    private readonly Dictionary<string, MediaRepeat> _names = new(StringComparer.Ordinal)
    { ["不要重複"] = MediaRepeat.None, ["全部重複"] = MediaRepeat.List };
    private string? _lastGlyph, _lastName;

    // The state the button shows, or null when its glyph is unknown or its name has not been learnt yet.
    public MediaRepeat? Observe(string glyph, string name)
    {
        if (glyph == RepeatOneGlyph && _lastGlyph == RepeatGlyph && _lastName is { } before) Learn(before, MediaRepeat.List);
        if (glyph == RepeatGlyph && _lastGlyph == RepeatOneGlyph) Learn(name, MediaRepeat.None);
        _lastGlyph = glyph; _lastName = name;
        if (glyph == RepeatOneGlyph) return MediaRepeat.Track;
        if (glyph != RepeatGlyph) return null;
        if (_names.TryGetValue(name, out var known)) return known;
        // A name that is not one learnt for the same language is the other state, once one of them is known.
        var learnt = _names.Where(pair => pair.Value is MediaRepeat.None or MediaRepeat.List && !Seeded(pair.Key)).Select(pair => pair.Value).Distinct().ToArray();
        return learnt.Length == 1 ? (learnt[0] == MediaRepeat.None ? MediaRepeat.List : MediaRepeat.None) : null;
    }
    private void Learn(string name, MediaRepeat repeat)
    {
        if (Seeded(name)) return;
        // A name only ever means one state; relearning the other state for it replaces the pair of that language.
        foreach (var stale in _names.Where(pair => pair.Value == repeat && !Seeded(pair.Key)).Select(pair => pair.Key).ToArray()) _names.Remove(stale);
        _names[name] = repeat;
    }
    private static bool Seeded(string name) => name is "不要重複" or "全部重複";
}
