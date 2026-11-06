namespace TwelveIsland.Core;

// Resize is one setting for four registrations: its modifiers with each arrow key (see Arrows).
public enum HotkeyKind { Hide, Expand, Resize }

// Unset: no combination saved. Active: registered. InUse: another program holds the combination. Invalid: the text is not
// an allowed combination. Duplicate: a combination another hotkey has, which keeps it (see Plan and PlanArrows).
public enum HotkeyStatus { Unset, Active, InUse, Invalid, Duplicate }

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

// The saved text of a global hotkey: modifiers in the fixed order Ctrl+Alt+Shift+Win, then the main key's name (a WPF Key
// name such as "K", "D1" or "F9"), joined by "+". Only F1 to F24 may stand without a modifier. Left and right modifiers are
// not told apart. Whether the key name names a real key is decided by the caller (HotkeyGesture in the app).
public static class HotkeyText
{
    private static readonly (HotkeyModifiers Flag, string Name)[] Order =
        [(HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win")];

    // Accepts the modifiers in any order and letter case, and the aliases Control and Windows; rejects repeated modifiers,
    // a missing or repeated main key and spaces inside a part. An empty text is not a hotkey (see IsUnset).
    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None; key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0 || part.Any(char.IsWhiteSpace)) return false;
            var flag = Modifier(part);
            if (i < parts.Length - 1)
            {
                if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag)) return false;
                modifiers |= flag;
            }
            else
            {
                if (flag != HotkeyModifiers.None) return false;
                key = part;
            }
        }
        return IsAllowed(modifiers, key);
    }

    public static string Format(HotkeyModifiers modifiers, string key)
        => string.Join("+", Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name).Append(key));

    // The canonical form of a saved text, or "" when it is empty or not an allowed combination.
    public static string Normalize(string? text) => TryParse(text, out var modifiers, out var key) ? Format(modifiers, key) : "";

    public static bool IsUnset(string? text) => string.IsNullOrWhiteSpace(text);

    public static bool IsAllowed(HotkeyModifiers modifiers, string key)
        => key.Length > 0 && (modifiers & ~(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win)) == 0
           && (modifiers != HotkeyModifiers.None || IsFunctionKey(key));

    // F1 to F24, which may be used alone.
    public static bool IsFunctionKey(string key)
        => key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, null, out int n)
           && n is >= 1 and <= 24 && key[1] != '0';

    // Whether two saved texts name the same combination, whatever their spelling. Two unset or invalid texts are not the same.
    public static bool Same(string? first, string? second)
        => Normalize(first) is { Length: > 0 } a && string.Equals(a, Normalize(second), StringComparison.OrdinalIgnoreCase);

    // What each hotkey should do before registration: Unset, Invalid, Duplicate (the expand hotkey yields to the hide hotkey
    // when both name the same combination), or null to register its canonical text. isKey checks the main key's name.
    public static (HotkeyStatus? Status, string Text) Plan(HotkeyKind kind, string? hide, string? expand, Func<string, bool> isKey)
    {
        string own = kind == HotkeyKind.Hide ? hide ?? "" : expand ?? "";
        if (IsUnset(own)) return (HotkeyStatus.Unset, "");
        if (!TryParse(own, out var modifiers, out var key) || !isKey(key)) return (HotkeyStatus.Invalid, "");
        string text = Format(modifiers, key);
        if (kind == HotkeyKind.Expand && Normalize(hide) is { Length: > 0 } other && string.Equals(other, text, StringComparison.OrdinalIgnoreCase)
            && TryParse(hide, out _, out var otherKey) && isKey(otherKey))
            return (HotkeyStatus.Duplicate, text);
        return (null, text);
    }

    // The resize shortcut is saved as its modifiers and "Arrows" ("Ctrl+Alt+Arrows"): the four arrow keys with those modifiers,
    // in the order Left, Right, Up, Down. Which modifiers are allowed is ArrowModifiersAllowed's.
    public const string Arrows = "Arrows";
    public static readonly IReadOnlyList<string> ArrowKeys = ["Left", "Right", "Up", "Down"];

    // The modifiers must include Alt and one more: the arrow keys with Shift, Ctrl or both select and move through text in every
    // program, and with Win they arrange windows, so taking those over would break them everywhere.
    public static bool ArrowModifiersAllowed(HotkeyModifiers modifiers)
        => modifiers.HasFlag(HotkeyModifiers.Alt) && modifiers != HotkeyModifiers.Alt;

    public static bool TryParseArrows(string? text, out HotkeyModifiers modifiers)
        => TryParse(text, out modifiers, out var key) && string.Equals(key, Arrows, StringComparison.OrdinalIgnoreCase) && ArrowModifiersAllowed(modifiers);

    // The settings show the resize shortcut as "Alt + [box] + arrow keys": Alt and the arrows are fixed, and the box records
    // only the other modifiers. ArrowExtras is what the box shows ("Ctrl", "Ctrl+Shift"), or "" when the text is not a resize
    // shortcut. FromArrowExtras is the text to save for the recorded modifiers (Alt among them changes nothing), or "" when no
    // other modifier was recorded.
    public static string ArrowExtras(string? text)
        => TryParseArrows(text, out var modifiers) ? FormatModifiers(modifiers & ~HotkeyModifiers.Alt) : "";

    public static string FromArrowExtras(HotkeyModifiers extras)
    {
        var modifiers = (extras & (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Win)) | HotkeyModifiers.Alt;
        return ArrowModifiersAllowed(modifiers) ? Format(modifiers, Arrows) : "";
    }

    // Modifiers alone, in the fixed order ("Ctrl+Shift"), or "" for none.
    public static string FormatModifiers(HotkeyModifiers modifiers)
        => string.Join("+", Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name));

    // The combinations a saved text stands for: the four arrow keys for a resize shortcut, otherwise the text itself.
    public static string[] Combinations(string? text)
        => TryParseArrows(text, out var modifiers) ? [.. ArrowKeys.Select(key => Format(modifiers, key))] : Normalize(text) is { Length: > 0 } one ? [one] : [];

    // Whether two saved texts share a combination (a resize shortcut and Ctrl+Alt+Left do).
    public static bool Overlap(string? first, string? second)
    {
        var others = Combinations(second);
        return Combinations(first).Any(a => others.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    // What the resize shortcut should do before registration, as Plan does for the others: it yields to the other shortcuts
    // (others, such as the hide and expand shortcuts) when one of its four combinations is theirs. Its text is the canonical
    // modifiers with Arrows.
    public static (HotkeyStatus? Status, string Text) PlanArrows(string? resize, Func<string, bool> isKey, params ReadOnlySpan<string?> others)
    {
        if (IsUnset(resize)) return (HotkeyStatus.Unset, "");
        if (!TryParseArrows(resize, out var modifiers)) return (HotkeyStatus.Invalid, "");
        string text = Format(modifiers, Arrows);
        foreach (var other in others)
            if (TryParse(other, out _, out var otherKey) && isKey(otherKey) && Overlap(text, other)) return (HotkeyStatus.Duplicate, text);
        return (null, text);
    }

    private static HotkeyModifiers Modifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None
    };
}
