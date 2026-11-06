using System.Windows;

namespace TwelveIsland;

// Something that needs an element laid out and cannot be done yet is tried again after the next layout pass, not by posting the
// try to the dispatcher again: an element under a collapsed parent keeps an invalid measure (and its old size) for as long as the
// parent stays collapsed, and a try that posts itself on each failure then spins the UI thread at full speed until the parent shows.
internal static class LayoutRetry
{
    // Shown in a window and measured since it last changed (an element under a collapsed parent keeps its old ActualWidth).
    public static bool Laid(FrameworkElement element) =>
        element.IsVisible && element.IsMeasureValid && element.ActualWidth > 0 && PresentationSource.FromVisual(element) is not null;
    // Runs `run` once, after the first layout pass of `host` at which `ready` holds.
    public static void WhenReady(UIElement host, Func<bool> ready, Action run)
    {
        EventHandler? retry = null;
        retry = (_, _) =>
        {
            if (!ready()) return;
            host.LayoutUpdated -= retry;
            run();
        };
        host.LayoutUpdated += retry;
    }
}
