using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TwelveIsland;

// A tooltip open over a page that is left without the pointer moving (the wheel walks the pages, music starts and the panel
// shows its page) stayed on top of the next page: WPF closes a tooltip when the pointer leaves its owner, and the owner never
// saw the pointer leave. The tooltips WPF makes for text are its own, so they are found where they are shown, in the open
// pop-ups, by the element they were opened for.
internal static class PageToolTips
{
    // Closes every open tooltip whose owner lies within page (or is page); returns how many it closed.
    public static int CloseWithin(DependencyObject page)
    {
        int closed = 0;
        foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>().ToList())
        {
            if (source.RootVisual is not { } root) continue;
            foreach (var tip in Open(root))
            {
                if (tip.PlacementTarget is not DependencyObject owner || !Within(owner, page)) continue;
                tip.IsOpen = false;
                closed++;
            }
        }
        return closed;
    }

    private static IEnumerable<ToolTip> Open(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is ToolTip { IsOpen: true } tip) { yield return tip; continue; }
            if (node is not (Visual or System.Windows.Media.Media3D.Visual3D)) continue;
            for (int i = 0, count = VisualTreeHelper.GetChildrenCount(node); i < count; i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static bool Within(DependencyObject owner, DependencyObject page)
    {
        for (var node = owner; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, page)) return true;
        return false;
    }
}
