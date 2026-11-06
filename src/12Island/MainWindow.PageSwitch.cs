using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

public partial class MainWindow
{
    // Places the chosen page's fill under its button. While the open panel shows it, it glides there as the monitor page's sort pill
    // does; otherwise it is set in place. A switch whose buttons are not laid out yet places it once they are.
    private void PlacePageCapsule()
    {
        var button = PageButton(CurrentPage);
        if (!LayoutRetry.Laid(button))
        {
            // Retried after a layout pass, never by posting itself again: the switch is collapsed while the usage details show, its
            // buttons then keep an invalid measure and their old width, and a retry that posted itself spun the UI thread at 90%.
            if (!_capsulePending) { _capsulePending = true; LayoutRetry.WhenReady(this, () => LayoutRetry.Laid(PageButton(CurrentPage)), () => { _capsulePending = false; PlacePageCapsule(); }); }
            return;
        }
        // The button's set height, not ActualHeight: a height ApplyHeader just set shows in ActualHeight only after the next layout pass.
        double x = button.TranslatePoint(new Point(), (UIElement)PageCapsule.Parent).X, width = button.ActualWidth, height = button.Height;
        bool glide = _expanded && !_preview && PageSwitch.IsVisible && Soft.Enabled && PageCapsule.Width > 0 && Math.Abs(PageCapsuleShift.X - x) > .5;
        PageCapsule.Height = height;
        PageHandover.Glide(PageCapsule, WidthProperty, width, glide, Soft.Choose);
        double from = PageCapsuleShift.X;
        PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, null);
        PageCapsuleShift.X = x;
        if (glide) PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(from, x, TimeSpan.FromMilliseconds(Soft.Choose))
        { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
    private bool _capsulePending;
    // The page buttons take the header's button height, and the capsule under the chosen one takes it in the same step: PlacePageCapsule,
    // which RefreshStatus runs before ApplyHeader, would otherwise leave it at the size tier left behind until the next refresh.
    private void FitPageButtons(PanelHeader header)
    {
        foreach (var button in PageButtons) button.Height = header.ButtonHeight;
        PageCapsule.Height = header.ButtonHeight;
    }
    // The buttons stand in the order chosen in the settings; the first has no gap before it.
    private void OrderPageButtons()
    {
        if (AIUsageButton.Parent is not Panel buttons) return;
        var order = Settings.PageOrder.Select(PageButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order))
        {
            buttons.Children.Clear();
            // A button may still be parented by the page handover while it glides out; detach it before it joins the switch again.
            foreach (var button in order) { if (button.Parent is Panel p && p != buttons) p.Children.Remove(button); buttons.Children.Add(button); }
        }
        for (int i = 0; i < order.Count; i++) order[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
    }
    private Button PageButton(DeckPage page) => page switch
    {
        DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Notify => NotifyPageButton,
        DeckPage.Clipboard => ClipboardPageButton, DeckPage.Convert => ConvertPageButton, _ => AIUsageButton
    };
    private DeckPage PageOf(Button button) => button switch
    {
        var b when ReferenceEquals(b, MusicPageButton) => DeckPage.Music,
        var b when ReferenceEquals(b, MonitorPageButton) => DeckPage.Monitor,
        var b when ReferenceEquals(b, NotifyPageButton) => DeckPage.Notify,
        var b when ReferenceEquals(b, ClipboardPageButton) => DeckPage.Clipboard,
        var b when ReferenceEquals(b, ConvertPageButton) => DeckPage.Convert,
        _ => DeckPage.Usage
    };
}
