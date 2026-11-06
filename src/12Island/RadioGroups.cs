using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TwelveIsland;

// The settings window's radio groups (segmented controls, theme and style cards, clock tiles and swatches) behave as Windows' radio
// groups (SW-06): the group is one Tab stop, on its chosen option (or its first), and the arrow keys move the focus to the next or
// previous option, wrapping at the ends. Space chooses the focused option, so moving through a group never changes a setting by
// itself. A group is the radio buttons of one panel with one group name. On for everything under an element with Enabled set (the
// window's frame), so the panel's own radio buttons keep their behavior.
internal static class RadioGroups
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RadioGroups), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    static RadioGroups()
    {
        // A new option takes its place at its first layout: WPF raises Loaded only to elements with a handler of their own.
        EventManager.RegisterClassHandler(typeof(RadioButton), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((sender, _) => TabStops((RadioButton)sender)));
        EventManager.RegisterClassHandler(typeof(RadioButton), ToggleButton.CheckedEvent, new RoutedEventHandler((sender, _) => TabStops((RadioButton)sender)));
        // A group whose chosen option is cleared (the monitor's two presets when a setting leaves both) goes back to its first option.
        EventManager.RegisterClassHandler(typeof(RadioButton), ToggleButton.UncheckedEvent, new RoutedEventHandler((sender, _) => TabStops((RadioButton)sender)));
        EventManager.RegisterClassHandler(typeof(RadioButton), UIElement.PreviewKeyDownEvent, new KeyEventHandler(Arrow));
    }
    // Registers the class handlers; called once before the first settings window is built.
    public static void Use() { }

    private static List<RadioButton> Group(RadioButton radio) => VisualTreeHelper.GetParent(radio) is Panel panel
        ? [.. panel.Children.OfType<RadioButton>().Where(option => option.GroupName == radio.GroupName)] : [radio];

    private static void TabStops(RadioButton radio)
    {
        if (!(bool)radio.GetValue(EnabledProperty)) return;
        var group = Group(radio);
        var chosen = group.FirstOrDefault(option => option.IsChecked == true) ?? group[0];
        foreach (var option in group) KeyboardNavigation.SetIsTabStop(option, ReferenceEquals(option, chosen));
    }

    private static void Arrow(object sender, KeyEventArgs e)
    {
        var radio = (RadioButton)sender;
        int step = e.Key is Key.Left or Key.Up ? -1 : e.Key is Key.Right or Key.Down ? 1 : 0;
        if (step == 0 || e.Handled || !ReferenceEquals(e.OriginalSource, radio) || Keyboard.Modifiers != ModifierKeys.None || !(bool)radio.GetValue(EnabledProperty)) return;
        var open = Group(radio).Where(option => option.IsEnabled && option.IsVisible).ToList();
        int at = open.IndexOf(radio);
        if (at < 0 || open.Count < 2) return;
        var next = open[(at + step + open.Count) % open.Count];
        next.Focus(); e.Handled = true;
    }
}
