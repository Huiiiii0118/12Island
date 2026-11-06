using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// Drop-downs, lists and pickers open and close as the panel's cards do (Soft.Enter, Soft.Leave). WPF closes a pop-up at
// once, also when it closes itself on a click outside, so its closing is shown by a picture of its last frame, which leaves
// while the real one is already gone and the click has done what it was meant for. The picture is shown in a pop-up of its
// own that is opened as soon as the real one has been laid out: a pop-up created at the moment of closing came up some 50 ms
// later, after a blank. It lies right beneath the real one and is kept up to date while that is open, so it already shows
// when WPF takes the real one away: given its picture only on closing, it was drawn a frame late and the list blinked out
// and back before it left. It is first taken once the list has entered, and then again only when the list can have changed
// (motion-review F7, TwelveIsland.Core.PopupPicture): the pointer moved onto another element, a button or key went down or up, or
// the list scrolled; once right after (the change's own frame) and once more when a hover's fade has settled. A change while it
// entered is taken the same way right after the first picture. Taken 20 times a second while open, it kept the list rendering in
// software.
internal static class PopupMotion
{
    private static readonly ConditionalWeakTable<object, Placed> Places = new();
    // The waiting picture's pop-up for the opening under way, null between openings.
    private sealed class Placed { public Popup? Ghost; public Image? Picture; public Window? Window; public DispatcherTimer? Refresh; public Action? Unhook; }

    public static void Attach(Popup popup)
    {
        if (Places.TryGetValue(popup, out _)) return;
        var placed = new Placed();
        Places.Add(popup, placed);
        popup.Opened += (_, _) =>
        {
            Soft.Enter(popup.Child);
            Prepare(placed, () => popup.IsOpen ? popup.Child : null, () => Anchor(popup));
        };
        popup.Closed += (_, _) => Leave(popup.Child as FrameworkElement, placed);
    }

    // What a pop-up is placed against: its target, or, as a template's pop-up has none (a combo box's list), the element it lies in.
    private static UIElement? Anchor(Popup popup) => popup.PlacementTarget ?? popup.Parent as UIElement ?? popup.TemplatedParent as UIElement;

    // A combo box's list: its pop-up is in its template.
    public static void Attach(ComboBox combo)
    {
        void Hook() { if (combo.Template?.FindName("PART_Popup", combo) is Popup popup) Attach(popup); }
        if (combo.IsLoaded) Hook(); else combo.Loaded += (_, _) => Hook();
    }

    // A context menu enters by its surface (chrome), so its own bounds stay put for the pointer checks of its owner. Windows
    // fades a menu out itself as it closes, so it needs no picture leaving.
    public static void Attach(ContextMenu menu, Func<UIElement?> chrome) => menu.Opened += (_, _) => Soft.Enter(chrome());

    // Once the content is laid out, a pop-up for its picture is opened beneath it, placed against the content's window rather
    // than its anchor: choosing an option often rebuilds the page and takes the anchor away in the same click. The place is the
    // content's own offset in the real pop-up's root, which does not move while the content drops into place (Soft.Enter); the
    // root's corner alone put the picture a margin too high. The picture is first taken once the content has entered, so
    // nothing shows beneath it while it fades in.
    private static void Prepare(Placed placed, Func<UIElement?> content, Func<UIElement?> anchor)
    {
        Close(placed);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!Soft.Enabled || placed.Ghost is not null || content() is not FrameworkElement element || anchor() is not { } source || Window.GetWindow(source) is not { } window) return;
            var root = VisualTreeHelper.GetParent(element) as Visual ?? element;
            if (PresentationSource.FromVisual(root) is not HwndSource real || PresentationSource.FromVisual(window) is null) return;
            var offset = root == element ? new Vector() : VisualTreeHelper.GetOffset(element);
            var at = window.PointFromScreen(root.PointToScreen(new Point(offset.X, offset.Y)));
            var picture = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            placed.Picture = picture; placed.Window = window;
            var ghost = placed.Ghost = new Popup { PlacementTarget = window, Placement = PlacementMode.Relative, HorizontalOffset = at.X, VerticalOffset = at.Y,
                AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Focusable = false, Child = picture, IsOpen = true };
            if (PresentationSource.FromVisual(picture) is HwndSource own) Native.WindowsHost.PlaceBelow(own.Handle, real.Handle);
            // First once the content has entered, then again only after something that can change it. After a change it is taken
            // again once a hover's fade (Soft.Hover, Feedback) has had time to settle.
            var schedule = new PopupPicture();
            var settled = TimeSpan.FromMilliseconds(PopupPicture.SettleMilliseconds(Soft.Hover));
            var refresh = placed.Refresh = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Soft.Short + 80) };
            bool queued = false;
            object? over = null;
            refresh.Tick += (_, _) =>
            {
                refresh.Stop();
                // The first picture; a change while the list entered (the pointer onto an option) may still be fading its hover
                // in, so it is taken once more when that has settled. This one is that frame's picture already.
                bool missed = schedule.Enter();
                if (placed.Ghost == ghost && element.IsVisible) Take(element, picture);
                if (missed) { refresh.Interval = settled; refresh.Start(); }
            };
            refresh.Start();
            void Changed()
            {
                // Before it has entered, the first picture is still to come: taken now, it would lie opaque under the list
                // fading in, and the timer would not wait for the entrance.
                if (placed.Ghost != ghost || !schedule.Changed()) return;
                // The change's own frame, once its layout is done; a burst of changes queues it once.
                if (!queued)
                {
                    queued = true;
                    element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { queued = false; if (placed.Ghost == ghost && element.IsVisible) Take(element, picture); });
                }
                refresh.Stop(); refresh.Interval = settled; refresh.Start();
            }
            MouseEventHandler moved = (_, _) => { if (!ReferenceEquals(Mouse.DirectlyOver, over)) { over = Mouse.DirectlyOver; Changed(); } };
            MouseEventHandler left = (_, _) => { over = null; Changed(); };
            MouseButtonEventHandler button = (_, _) => Changed();
            KeyEventHandler key = (_, _) => Changed();
            ScrollChangedEventHandler scrolled = (_, _) => Changed();
            element.PreviewMouseMove += moved; element.MouseLeave += left;
            element.PreviewMouseDown += button; element.PreviewMouseUp += button;
            element.PreviewKeyDown += key; element.PreviewKeyUp += key;
            element.AddHandler(ScrollViewer.ScrollChangedEvent, scrolled);
            placed.Unhook = () =>
            {
                element.PreviewMouseMove -= moved; element.MouseLeave -= left;
                element.PreviewMouseDown -= button; element.PreviewMouseUp -= button;
                element.PreviewKeyDown -= key; element.PreviewKeyUp -= key;
                element.RemoveHandler(ScrollViewer.ScrollChangedEvent, scrolled);
            };
        });
    }

    // The content as it is drawn now (PageHandover.Snapshot).
    private static void Take(FrameworkElement content, Image image)
    {
        double width = content.ActualWidth, height = content.ActualHeight;
        if (width < 1 || height < 1 || PresentationSource.FromVisual(image) is null) return;
        image.Width = width; image.Height = height; image.Source = PageHandover.Snapshot(content, width, height, VisualTreeHelper.GetDpi(image));
    }

    // Applies a choice made in a pop-up once the pop-up's leaving has been drawn: a choice that rebuilds the page holds the
    // window for a moment, and the closed pop-up then left a blank where its picture should already be.
    public static void AfterClose(Action change) => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, change);

    private static void Close(Placed placed)
    {
        placed.Refresh?.Stop(); placed.Unhook?.Invoke();
        if (placed.Ghost is { } ghost) ghost.IsOpen = false;
        placed.Ghost = null; placed.Picture = null; placed.Window = null; placed.Refresh = null; placed.Unhook = null;
    }

    private static void Leave(FrameworkElement? content, Placed placed)
    {
        var ghost = placed.Ghost; var image = placed.Picture; var window = placed.Window;
        placed.Refresh?.Stop(); placed.Unhook?.Invoke();
        placed.Ghost = null; placed.Picture = null; placed.Window = null; placed.Refresh = null; placed.Unhook = null;
        if (ghost is null || image is null) return;
        // A pop-up closed with its window (the settings window closed or hidden) goes with it, without a picture left behind.
        if (!Soft.Enabled || content is null || window is not { IsVisible: true } || PresentationSource.FromVisual(image) is null) { ghost.IsOpen = false; return; }
        // The picture taken while it was open is the one beneath it already: taken now, the hovered option has lost its mark.
        // Closed before it had entered, it has none yet and is taken now.
        if (image.Source is null) Take(content, image);
        if (image.Source is null) { ghost.IsOpen = false; return; }
        DependencyPropertyChangedEventHandler hidden = (_, _) => ghost.IsOpen = false;
        window.IsVisibleChanged += hidden;
        Soft.Leave(image, () => { window.IsVisibleChanged -= hidden; ghost.IsOpen = false; });
    }
}
