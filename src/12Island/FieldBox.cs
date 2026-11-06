using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TwelveIsland;

// A text field in a frame that is drawn around it, such as a number box with its unit and steppers: a click anywhere in the frame
// that is not a control of its own (the padding, the unit, the divider) puts the focus in the field and selects its text, as a
// click on the field's own text would put it there. The window's "click on empty space releases the focus" rule leaves such a
// frame alone, or it would take the focus back on the same click.
internal static class FieldBox
{
    private static readonly DependencyProperty FieldProperty = DependencyProperty.RegisterAttached("Field", typeof(TextBox), typeof(FieldBox));

    public static void Attach(Border frame, TextBox field)
    {
        frame.SetValue(FieldProperty, field);
        // A stepper inside the frame handles its own press, so only the rest of the frame arrives here.
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            field.Focus(); field.SelectAll(); e.Handled = true;
        };
    }

    // Whether the element is a frame Attach was given.
    public static bool Owns(DependencyObject element) => element.GetValue(FieldProperty) is not null;
}

// The framed number box of the settings window (SettingsWindow.NumericControls builds it through Create): the number, its unit, a
// divider and two steppers. The whole box is the number's click target, not only the 16 DIP line of text in it (SW-08); the
// steppers take their own clicks.
internal static class NumberBox
{
    public static Border Create(TextBox input, UIElement unit, UIElement minus, UIElement plus, Brush divider, Brush background, Brush border)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(input); line.Children.Add(unit);
        line.Children.Add(new Border { Width = 1, Background = divider, Margin = new Thickness(2, 5, 3, 5) }); line.Children.Add(minus); line.Children.Add(plus);
        var chip = new Border { Child = line, Height = 30, CornerRadius = new CornerRadius(6), Background = background, BorderBrush = border,
            BorderThickness = new Thickness(1), Padding = new Thickness(7, 0, 2, 0) };
        FieldBox.Attach(chip, input);
        return chip;
    }
}
