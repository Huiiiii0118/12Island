using System.Windows;
using System.Windows.Input;

namespace TwelveIsland;

// Moving a row from the keyboard (Alt+Up/Down) first folds away the rows' open details. A control in the part that folds, the
// one the focus was on, stops being visible, and WPF then takes the keyboard focus away from it; the next Alt+arrow would not
// reach the row's key handler. The focus goes to the row's handle instead, which stays visible and is inside that handler's reach.
internal static class RowFocus
{
    // True when the focus is on a control inside row that is no longer visible.
    public static bool Orphaned(UIElement row, IInputElement? focused) =>
        focused is UIElement element && !element.IsVisible && row.IsAncestorOf(element);

    // Call after the details fold. A focus outside the row, or on a control that is still shown, is left where it is (a name being
    // edited keeps being edited).
    public static void Keep(UIElement row, UIElement handle)
    {
        if (Orphaned(row, Keyboard.FocusedElement)) handle.Focus();
    }
}
