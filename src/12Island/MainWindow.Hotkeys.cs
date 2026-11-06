using System.Windows;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// How a panel follows the global hotkeys (see App.Hotkeys.cs).
// Opened by the expand hotkey, the panel stays open until the pointer has been inside it once; after that the usual leave
// rules close it. While the panels are hidden, the expand hotkey still opens this panel; once it closes, it hides again.
public partial class MainWindow
{
    // Shown for the expand hotkey while the panels are hidden.
    private bool _hotkeyOpened;
    // Opened by the expand hotkey and not entered by the pointer yet.
    private bool _hotkeyPinned;

    // Hidden by the hide hotkey; part of IsSuppressed, so it combines with the full-screen rule the same way.
    internal bool HiddenByUser => _app.UserHidden && !_hotkeyOpened;
    internal IntPtr HotkeyMonitor => MonitorHandle;
    internal bool IsOpenForHotkey => _expanded && !_preview;
    internal bool IsHotkeyPinned => _hotkeyPinned;

    internal void OpenFromHotkey()
    {
        if (_expanded) return;
        // A collapsed preview of the settings ends as hovering would end it; a preview of the open panel is left alone.
        if (_preview) { if (_previewExpanded) return; _app.EndPreviewFromDeck(); }
        // The full-screen rule hides the panel; the hotkey does not override it.
        if (Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Hide) return;
        if (_app.UserHidden && !_hotkeyOpened) { _hotkeyOpened = true; ApplySettings(); }
        _hover.Stop(); _leave.Stop();
        _hotkeyPinned = true;
        SetExpanded(true, keyboard: true);
        if (!_expanded) { _hotkeyPinned = false; if (_hotkeyOpened) { _hotkeyOpened = false; ApplySettings(); } }
    }

    internal void CollapseFromHotkey() => SetExpanded(false, keyboard: true);

    // For TrackPointer while open: the pointer inside ends the pin. True while the pin holds the panel open.
    internal bool HotkeyPinned(bool inside)
    {
        if (inside) _hotkeyPinned = false;
        return _hotkeyPinned;
    }

    // For PointerChanged: any entry ends the pin.
    internal void ReleaseHotkeyPin() => _hotkeyPinned = false;

    // For SetExpanded, on the change from open to closed.
    internal void HotkeyPanelCollapsed()
    {
        _hotkeyPinned = false;
        if (!_hotkeyOpened) return;
        _hotkeyOpened = false;
        if (!_app.UserHidden) return;
        // The panels are hidden: closing goes straight back to hidden, as hiding always does (ApplySettings), instead of
        // playing the close into the collapsed island and showing that for a moment first. Not from inside SetExpanded,
        // which ApplySettings calls, but before the next frame is drawn.
        Dispatcher.BeginInvoke(DispatcherPriority.Send, Rehide);
    }

    // Called by App for every panel when the hidden state changes.
    internal void UserHiddenChanged()
    {
        _hotkeyOpened = false; _hotkeyPinned = false;
        if (_app.UserHidden)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
            if (_alert is not null) ClearAlert();
        }
        // Hiding and showing again are immediate (MainWindow.ApplySettings).
        ApplySettings(true);
    }

    // A panel retired meanwhile (its monitor was removed) is closed and left alone.
    private void Rehide()
    {
        if (IsLoaded && HiddenByUser && !_expanded) ApplySettings();
    }
}
