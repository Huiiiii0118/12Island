using System.Windows;
using System.Windows.Controls;
using TwelveIsland.Core;

namespace TwelveIsland;

// What every page of the app's own draws first: its content shown, the header the other pages use (so the switch, the clock and
// the settings button stay in place), and the page's margins.
public partial class MainWindow
{
    private (double Width, double Height) PreparePage(DeckPage page, double? bottom = null)
    {
        RefreshStatus();
        ShowPageContent(page);
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return (0, 0);
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        PageContent(page).Margin = new Thickness(_header.Side, 0, _header.Side, bottom ?? (height < 170 ? 10 : 16));
        Motion.Enabled = Settings.Animations && SystemParameters.ClientAreaAnimation;
        return (width, height);
    }

    // A scrolling list that keeps the panel's wheel to itself: the wheel scrolls the list instead of turning the page.
    internal static ScrollViewer PageScroll(UIElement content)
    {
        var scroll = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 4, 0)
        };
        scroll.PreviewMouseWheel += (_, e) => { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; };
        return scroll;
    }

    // A row of a list: its content, a hover fill in the theme's color, and the radius the panel uses for rows.
    internal static System.Windows.Controls.Border PageRow(UIElement content, double height)
    {
        var row = new System.Windows.Controls.Border
        {
            Child = content, Height = height, Background = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(4, 0, 2, 0)
        };
        row.MouseEnter += (_, _) => row.Background = UI.Brush(UI.Fill);
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        return row;
    }
}
