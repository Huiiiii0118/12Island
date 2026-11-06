using System.Windows.Input;
using TwelveIsland.Core;

namespace TwelveIsland;

// Reads and writes the saved text of a global hotkey (see HotkeyText) with WPF keys, and turns a combination into the values
// RegisterHotKey takes. The main key's name is the Key enum name, with the friendlier of two names where WPF has both
// (Enter, PageUp, PageDown, CapsLock, PrintScreen).
public static class HotkeyGesture
{
    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None; key = Key.None;
        if (!HotkeyText.TryParse(text, out var flags, out var name) || !TryKey(name, out var parsed) || !IsAllowed(ToWpf(flags), parsed)) return false;
        modifiers = ToWpf(flags); key = parsed;
        return true;
    }

    // The canonical text, or "" for a combination that cannot be a hotkey.
    public static string Format(ModifierKeys modifiers, Key key)
        => IsAllowed(modifiers, key) ? HotkeyText.Format(FromWpf(modifiers), Name(key)) : "";

    // A real, non-modifier key with a virtual-key code and, unless it is F1 to F24, at least one modifier.
    public static bool IsAllowed(ModifierKeys modifiers, Key key)
        => IsMainKey(key) && (modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0
           && HotkeyText.IsAllowed(FromWpf(modifiers), Name(key));

    // RegisterHotKey's modifier flags (with MOD_NOREPEAT, so holding the keys acts once, unless repeat asks for every repeat of a
    // held key, as resizing does) and virtual-key code.
    public static bool ToNative(ModifierKeys modifiers, Key key, out uint fsModifiers, out uint vk, bool repeat = false)
    {
        fsModifiers = 0; vk = 0;
        if (!IsAllowed(modifiers, key)) return false;
        if (modifiers.HasFlag(ModifierKeys.Alt)) fsModifiers |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) fsModifiers |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) fsModifiers |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) fsModifiers |= 0x0008;
        if (!repeat) fsModifiers |= 0x4000; // MOD_NOREPEAT
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    // The key a key event stands for: with Alt held WPF reports Key.System and the key in SystemKey; an input method or a
    // dead key reports the key in ImeProcessedKey or DeadCharProcessedKey.
    public static Key KeyOf(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    // Whether a saved key name names a key that can be the main key.
    public static bool IsKeyName(string name) => TryKey(name, out _);

    private static bool TryKey(string name, out Key key)
    {
        key = Key.None;
        // Enum.TryParse would also take numbers and comma lists.
        if (name.Length == 0 || !char.IsLetter(name[0]) || !name.All(char.IsLetterOrDigit)) return false;
        return Enum.TryParse(name, true, out key) && Enum.IsDefined(key) && IsMainKey(key);
    }

    private static bool IsMainKey(Key key) => key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
        && Enum.IsDefined(key) && KeyInterop.VirtualKeyFromKey(key) != 0;

    private static string Name(Key key) => key switch
    {
        Key.Enter => "Enter", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.CapsLock => "CapsLock", Key.PrintScreen => "PrintScreen",
        _ => key.ToString()
    };

    internal static HotkeyModifiers FromWpf(ModifierKeys modifiers)
        => (modifiers.HasFlag(ModifierKeys.Control) ? HotkeyModifiers.Ctrl : 0) | (modifiers.HasFlag(ModifierKeys.Alt) ? HotkeyModifiers.Alt : 0)
           | (modifiers.HasFlag(ModifierKeys.Shift) ? HotkeyModifiers.Shift : 0) | (modifiers.HasFlag(ModifierKeys.Windows) ? HotkeyModifiers.Win : 0);

    private static ModifierKeys ToWpf(HotkeyModifiers modifiers)
        => (modifiers.HasFlag(HotkeyModifiers.Ctrl) ? ModifierKeys.Control : 0) | (modifiers.HasFlag(HotkeyModifiers.Alt) ? ModifierKeys.Alt : 0)
           | (modifiers.HasFlag(HotkeyModifiers.Shift) ? ModifierKeys.Shift : 0) | (modifiers.HasFlag(HotkeyModifiers.Win) ? ModifierKeys.Windows : 0);
}
