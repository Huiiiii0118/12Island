using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// The global hotkeys and the hidden state the hide hotkey toggles. All are unset by default and are taken from the settings
// (HideHotkey, ExpandHotkey, ResizeHotkey). The registrations live on a message-only window of the app (GlobalHotkeys), so
// panels closed and opened again for display changes do not lose them.
public partial class App
{
    private GlobalHotkeys? _hotkeys;
    private readonly Dictionary<HotkeyKind, HotkeyStatus> _hotkeyStatus = new() { [HotkeyKind.Hide] = HotkeyStatus.Unset, [HotkeyKind.Expand] = HotkeyStatus.Unset, [HotkeyKind.Resize] = HotkeyStatus.Unset };
    private string _hideHotkey = "", _expandHotkey = "", _resizeHotkey = "";
    private int _hotkeySuspensions;

    // Raised on the UI thread after the hotkeys are registered again, with any status change.
    public event EventHandler? HotkeysChanged;
    // Raised when the panels are hidden or shown again by the hide hotkey or the tray menu.
    public event EventHandler? UserHiddenChanged;

    // Every panel is hidden: nothing is drawn, hovering does nothing and no click is caught. Not saved; a restart shows them.
    public bool UserHidden { get; private set; }

    // The status from the last registration. While SuspendHotkeys is in effect nothing is registered and this keeps the
    // status the combinations will have again.
    public HotkeyStatus StatusOf(HotkeyKind kind) => _hotkeyStatus[kind];

    // Creates the registrar and registers the saved hotkeys. Call once the main panel exists.
    internal void InitializeHotkeys()
    {
        if (_hotkeys is not null) return;
        _hotkeys = new GlobalHotkeys();
        _hotkeys.Pressed += HotkeyPressed;
        ApplyHotkeys();
    }

    // Registers the hotkeys saved in the settings. Unchanged texts that are already active are left alone; any other one is
    // tried again, so a combination another program has since released becomes active on the next save.
    public void ApplyHotkeys() => ApplyHotkeys(Settings.HideHotkey, Settings.ExpandHotkey, Settings.ResizeHotkey);
    internal void ApplyHotkeys(string? hide, string? expand, string? resize)
    {
        hide ??= ""; expand ??= ""; resize ??= "";
        bool unchanged = hide == _hideHotkey && expand == _expandHotkey && resize == _resizeHotkey
            && _hotkeyStatus.Values.All(status => status is HotkeyStatus.Active or HotkeyStatus.Unset);
        _hideHotkey = hide; _expandHotkey = expand; _resizeHotkey = resize;
        if (unchanged && _hotkeySuspensions == 0 && _hotkeys is not null) return;
        RegisterHotkeys();
    }

    // Releases the hotkeys, for example while the settings window records a new combination. Calls nest; each must be
    // matched by ResumeHotkeys, and the hotkeys come back with the last one.
    public void SuspendHotkeys()
    {
        if (_hotkeySuspensions++ > 0 || _hotkeys is null) return;
        foreach (var kind in Enum.GetValues<HotkeyKind>()) _hotkeys.Unregister(kind);
    }

    public void ResumeHotkeys()
    {
        if (_hotkeySuspensions == 0) return;
        if (--_hotkeySuspensions == 0) RegisterHotkeys();
    }

    internal void DisposeHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Pressed -= HotkeyPressed; _hotkeys.Dispose(); _hotkeys = null;
    }

    // Hides every panel, or shows them again. An open panel closes first.
    public void ToggleHidden() => SetUserHidden(!UserHidden);
    public void SetUserHidden(bool hidden)
    {
        if (UserHidden == hidden) return;
        UserHidden = hidden;
        foreach (var deck in Decks.ToList()) deck.UserHiddenChanged();
        UserHiddenChanged?.Invoke(this, EventArgs.Empty);
    }

    // Opens the panel on the monitor under the pointer, or closes the one that is open. A monitor without a panel uses the
    // main panel.
    public void ToggleExpandFromHotkey()
    {
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey) is { } open) { open.CollapseFromHotkey(); return; }
        var monitor = GlobalHotkeys.CursorMonitor();
        (Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck).OpenFromHotkey();
    }

    // Resizes the open panel, or the collapsed surface on the monitor under the pointer (KeyboardResize).
    public void ResizeFromHotkey(int arrow)
    {
        var target = Decks.FirstOrDefault(deck => deck.IsOpenForHotkey);
        if (target is null) { var monitor = GlobalHotkeys.CursorMonitor(); target = Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck; }
        target.ResizeFromKeyboard(KeyboardResize.Of(arrow));
    }

    private void RegisterHotkeys()
    {
        // Everything is released first, so a combination one hotkey gives up is free for another this time round.
        foreach (var kind in Enum.GetValues<HotkeyKind>()) _hotkeys?.Unregister(kind);
        foreach (var kind in new[] { HotkeyKind.Hide, HotkeyKind.Expand })
        {
            var (status, text) = HotkeyText.Plan(kind, _hideHotkey, _expandHotkey, HotkeyGesture.IsKeyName);
            if (status is null)
            {
                HotkeyGesture.TryParse(text, out var modifiers, out var key);
                status = !HotkeyGesture.ToNative(modifiers, key, out uint fs, out uint vk) ? HotkeyStatus.Invalid
                    : _hotkeys is null || _hotkeySuspensions > 0 ? HotkeyStatus.Active : _hotkeys.Register(kind, fs, vk);
            }
            _hotkeyStatus[kind] = status.Value;
        }
        RegisterResizeHotkey();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    // The resize shortcut's four combinations, each repeating while its key is held. It is active only when all four are; one
    // another program holds leaves none registered, so the arrows never work in some directions only.
    private void RegisterResizeHotkey()
    {
        const HotkeyKind kind = HotkeyKind.Resize;
        var (status, text) = HotkeyText.PlanArrows(_resizeHotkey, HotkeyGesture.IsKeyName, _hideHotkey, _expandHotkey);
        if (status is null && HotkeyText.TryParseArrows(text, out var modifiers))
        {
            status = HotkeyStatus.Active;
            for (int arrow = 0; arrow < HotkeyText.ArrowKeys.Count && status == HotkeyStatus.Active; arrow++)
            {
                HotkeyGesture.TryParse(HotkeyText.Format(modifiers, HotkeyText.ArrowKeys[arrow]), out var wpfModifiers, out var key);
                status = !HotkeyGesture.ToNative(wpfModifiers, key, out uint fs, out uint vk, repeat: true) ? HotkeyStatus.Invalid
                    : _hotkeys is null || _hotkeySuspensions > 0 ? HotkeyStatus.Active : _hotkeys.Register(kind, fs, vk, arrow);
            }
            if (status != HotkeyStatus.Active) _hotkeys?.Unregister(kind);
        }
        _hotkeyStatus[kind] = status ?? HotkeyStatus.Invalid;
    }

    private void HotkeyPressed(HotkeyKind kind, int slot)
    {
        if (Exiting) return;
        if (kind == HotkeyKind.Hide) ToggleHidden();
        else if (kind == HotkeyKind.Resize) ResizeFromHotkey(slot);
        else ToggleExpandFromHotkey();
    }
}
