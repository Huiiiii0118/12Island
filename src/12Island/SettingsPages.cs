using System.Windows;
using System.Windows.Controls;
using TwelveIsland.Core;

namespace TwelveIsland;

// The size of a page of the app's own (notifications, clipboard, conversion): the same width and height fields every other page
// has, with the page's own limits. The panel takes the new size at once, as it does for a drag of its edge.
public sealed partial class SettingsWindow
{
    private void PageSizeControls(DeckPage page)
    {
        var (width, height) = DeckPages.Size(S, page);
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        var widthField = Dimension(Loc.T("宽度", "Width"), width, DeckPages.MinWidth, DeckPages.MaxWidth, value => Resize(s => Set(page, s, value, null), page));
        widthField.Margin = new Thickness(0, 0, 20, 0);
        dimensions.Children.Add(widthField);
        var heightField = Dimension(Loc.T("高度", "Height"), height, DeckPages.MinHeight, DeckPages.MaxHeight, value => Resize(s => Set(page, s, null, value), page));
        heightField.Margin = new Thickness(20, 0, 0, 0);
        Grid.SetColumn(heightField, 1);
        dimensions.Children.Add(heightField);
        Target.Children.Add(dimensions);
    }

    private static void Set(DeckPage page, DeckSettings settings, double? width, double? height)
    {
        var (currentWidth, currentHeight) = DeckPages.Size(settings, page);
        DeckPages.SetSize(settings, page, width ?? currentWidth, height ?? currentHeight);
    }
}
