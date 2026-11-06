using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TwelveIsland;

// Clicking anything that is not a control takes focus off a text field, as elsewhere in Windows. WPF leaves the
// field focused otherwise, since panels and scroll areas do not take focus. The holder is a focusable frame that
// draws no outline and is skipped by Tab. A frame given to FieldBox is a control in this sense: its click puts the focus in its field.
internal static class EmptyClickFocus
{
    public static void Attach(Window window, Func<UIElement> holder)
    {
        window.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            // Any focused control counts: the script editor, for one, keeps focus in its own text area rather than a TextBox.
            if (Keyboard.FocusedElement is not Visual focused || ReferenceEquals(focused, holder()) || !window.IsAncestorOf(focused)) return;
            for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, window);
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is Control { Focusable: true, IsEnabled: true } || FieldBox.Owns(node)) return;
            // The window itself would hand focus straight back to the field, so the frame takes it.
            Keyboard.Focus(holder());
        }), true);
    }
}
