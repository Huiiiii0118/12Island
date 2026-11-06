using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TwelveIsland;

// How a rebuilt page takes over from the one it replaces.
internal enum Handover
{
    // At once, as when nothing about the page's form changed (a reading, the clock).
    None,
    // The same page with other figures (a quota page, a filter, a period): as the monitor page changes a reading in place, the
    // old content goes at once and the new comes up from faint, so old and new figures never overlap.
    Dissolve,
    // The page changed its form (a drag crossed a size step, an app was added): the old layout drops away quickly while the new
    // one fades in, so two layouts of different type sizes overlap only briefly.
    Morph,
    // Deeper and back: the old content leaves toward the side it is left for while the new comes in from the other side, as
    // pages of the page switch do (model details, application pages).
    Forward, Back
}

// The usage and media pages are rebuilt rather than updated, so a change of form used to replace everything in one frame. Hold
// takes the page's elements before a rebuild; Release then lets them leave on a layer above the page (LeavingContent, laid out with
// the margin the page had) while the rebuilt content arrives, or drops them at once when the rebuild changed nothing that shows.
// A rebuild during a hand-over that is still under way keeps that hand-over going and drops only its own old elements, so a
// panel dragged across a size step does not cut its hand-over short 25 times a second. The page switch hands over a picture
// of the page it leaves (Capture, Send): the pages keep their own elements, and the monitor page keeps its view across switches.
// Each hand-over leaves on its own layer (a batch in the leaving layer), so a switch to another page while the last one is
// still leaving lets that one finish leaving instead of cutting it off (motion-review F17); the page that was still arriving
// leaves from where it was.
internal sealed class PageHandover
{
    private const double Travel = 16, Faint = .3;
    private readonly Grid _layer;
    private readonly List<UIElement> _held = [];
    private Thickness _margin;
    private int _version;
    // The batch waiting for its page to be laid out before it starts to leave.
    private Grid? _waiting;

    public PageHandover(Grid layer) { _layer = layer; _layer.IsHitTestVisible = false; }

    // Where a page's content lies in the layer, which spans the page's rows: its margins, with the top counted from the top of
    // the first row. Taken before the rebuild or switch lays the rows out again.
    public static Thickness Place(FrameworkElement content)
    {
        var slot = LayoutInformation.GetLayoutSlot(content);
        return new Thickness(content.Margin.Left, slot.Top + content.Margin.Top, content.Margin.Right, content.Margin.Bottom);
    }

    public void Hold(Panel content, UIElement? keep = null)
    {
        _held.Clear();
        _margin = Place(content);
        for (int i = content.Children.Count - 1; i >= 0; i--)
        {
            var child = content.Children[i];
            if (child == keep) continue;
            content.Children.RemoveAt(i);
            _held.Insert(0, child);
        }
    }

    public void Release(FrameworkElement content, Handover motion)
    {
        if (_held.Count == 0 || motion == Handover.None || !Soft.Enabled) { _held.Clear(); return; }
        var leaving = _held.ToList();
        _held.Clear();
        // A rebuild while the page was still arriving (the usage page's own panel is what arrives): its elements leave from the
        // opacity and shift the page had, not from fully in place.
        Begin(leaving, _margin, content, motion, content.Opacity, (content.RenderTransform as TranslateTransform)?.X ?? 0);
    }

    // A picture of a page as it is on screen, to leave in its place; null for a page that is not laid out.
    public static Image? Capture(FrameworkElement page)
    {
        double width = page.ActualWidth, height = page.ActualHeight;
        if (!page.IsVisible || width < 1 || height < 1 || PresentationSource.FromVisual(page) is null) return null;
        // A page still arriving is pictured as it is drawn now: the brush takes the page's own opacity and shift with it
        // (checked: a page at half opacity and moved 10 DIP comes out so), so the picture leaves from where the page was.
        return new Image { Source = Snapshot(page, width, height, VisualTreeHelper.GetDpi(page)), Width = width, Height = height, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    }

    // A frozen picture of an element's own area at the given scale, drawn one to one (Exactly), so its offset in its parent is
    // not part of it. Its own opacity and render transform are: the brush draws the element as it is drawn.
    public static BitmapSource Snapshot(Visual element, double width, double height, DpiScale dpi)
    {
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(Exactly(element, width, height), null, new Rect(0, 0, width, height));
        picture.Render(drawing);
        picture.Freeze();
        return picture;
    }

    // A brush that draws an element's own area one to one, for pictures of it. The view box is the element's area, not the
    // default bounds of what it draws, which a brush stretches to fill: a page drawing less than its area (the media page's one
    // centred line, a short list) left as an enlarged picture of itself. A visual brush draws the element where it lies in its
    // parent, offset included, so the area starts at that offset; from (0, 0) the picture's content came out lower by the
    // page's top (the header row) and leaving pages were seen to drop.
    public static VisualBrush Exactly(Visual element, double width, double height)
    {
        var offset = VisualTreeHelper.GetOffset(element);
        return new(element) { Viewbox = new Rect(offset.X, offset.Y, width, height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    }

    // Lets a captured page leave while content arrives. The picture keeps the place the page had (Place, taken before the
    // switch) and is centred as the page was.
    public void Send(UIElement? picture, Thickness place, FrameworkElement content, Handover motion)
    {
        if (picture is null || motion == Handover.None || !Soft.Enabled) return;
        Begin([picture], new Thickness(0, place.Top, 0, 0), content, motion);
    }

    // The rebuilt content waiting for its arrival to start; it is held out of sight until then.
    private FrameworkElement? _holding;

    // The hand-over starts once the rebuilt page has been laid out and drawn: building it takes a frame or two, and a move
    // started at once lost its first part to them and jumped. Until then the old content stays as it was and the new one out
    // of sight. Timing and curve are the system monitor's (Soft.Follow): what leaves goes in about a fifth of a second, what
    // arrives takes the short swap (motion-review C8; it took the monitor's move, 420 ms).
    private void Begin(List<UIElement> leaving, Thickness margin, FrameworkElement content, Handover motion, double opacity = 1, double x = 0)
    {
        // A page switch lets what is still leaving finish; anything else starts afresh (a rebuild while dragging would pile up).
        if (motion is Handover.Forward or Handover.Back) Settle(); else Drop();
        int version = ++_version;
        var shift = content.RenderTransform as TranslateTransform;
        if (shift is null || shift.IsFrozen) content.RenderTransform = shift = new TranslateTransform();
        if (motion == Handover.Dissolve)
        {
            // Nothing leaves; the rebuilt content brightens from faint once it has been laid out.
            _holding = content; content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Faint, Faint, TimeSpan.Zero));
        }
        else
        {
            var batch = new Grid { Margin = margin, IsHitTestVisible = false, Opacity = opacity, RenderTransform = new TranslateTransform(x, 0) };
            foreach (var child in leaving) batch.Children.Add(child);
            _layer.Children.Add(batch);
            _waiting = batch;
            _holding = content; content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0, TimeSpan.Zero));
        }
        content.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_version != version) return;
            _holding = null;
            if (motion == Handover.Dissolve) { content.BeginAnimation(UIElement.OpacityProperty, Arrival(Faint, 1, Soft.Short)); return; }
            if (_waiting is not { } batch) return;
            _waiting = null;
            int leave = motion == Handover.Morph ? Soft.Hover : 200;
            var away = Soft.Fade(opacity, 0, leave, Soft.Follow);
            away.Completed += (_, _) => Remove(batch);
            batch.BeginAnimation(UIElement.OpacityProperty, away);
            content.BeginAnimation(UIElement.OpacityProperty, Arrival(0, 1, Soft.Short));
            if (motion == Handover.Morph) return;
            double direction = motion == Handover.Back ? -1 : 1;
            batch.RenderTransform.BeginAnimation(TranslateTransform.XProperty, Soft.Fade(x, x - direction * Travel, leave, Soft.Follow));
            shift.BeginAnimation(TranslateTransform.XProperty, Arrival(direction * Travel, 0, Soft.Short));
        });
    }

    // Ends a hand-over at once: the old elements go and the page is in place, also when its arrival had not started yet.
    public void Drop()
    {
        Settle();
        EndLeave();
    }

    // A hand-over that has not started yet is given up: its page shows as it is and its old elements go; what has started
    // to leave carries on.
    private void Settle()
    {
        if (_holding is { } held)
        {
            held.BeginAnimation(UIElement.OpacityProperty, null);
            _holding = null;
        }
        if (_waiting is { } waiting) { Remove(waiting); _waiting = null; }
    }

    // A batch that has gone lets go of its elements too, as the layer did, so none is left with a parent.
    private void Remove(Grid batch)
    {
        batch.Children.Clear();
        _layer.Children.Remove(batch);
    }

    // The old elements have gone; an arrival under way carries on.
    private void EndLeave()
    {
        _version++;
        _waiting = null;
        foreach (var batch in _layer.Children.OfType<Grid>().ToList()) batch.Children.Clear();
        _layer.Children.Clear();
    }

    // Brings a toolbar part that the rebuild showed in with the content: held out of sight with it, and arriving with it once
    // the page has been laid out (Begin).
    public static void Arrive(UIElement element, Handover motion)
    {
        if (motion is not (Handover.Forward or Handover.Back) || element.Visibility != Visibility.Visible) return;
        HoldThenArrive(element, 0, Soft.Choose);
    }

    // Brings one rebuilt part up from faint (a column whose quota page flipped), once the page has been laid out.
    public static void Brighten(UIElement element) => HoldThenArrive(element, Faint, Soft.Short);

    private static void HoldThenArrive(UIElement element, double from, int milliseconds)
    {
        if (!Soft.Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, from, TimeSpan.Zero));
        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => element.BeginAnimation(UIElement.OpacityProperty, Arrival(from, 1, milliseconds)));
    }

    // An arrival returns the property to its value when it ends, so nothing is left animated.
    private static DoubleAnimation Arrival(double from, double to, int milliseconds)
    {
        var animation = Soft.Fade(from, to, milliseconds, Soft.Follow);
        animation.FillBehavior = FillBehavior.Stop;
        return animation;
    }

    // Moves an element that the rebuild keeps (a transport button) to its new place: it glides from where it is now, or is set
    // at once. The property keeps the new value; the glide only covers the way there.
    public static void Glide(UIElement element, DependencyProperty property, double to, bool animate, int milliseconds = Soft.Choose)
    {
        double from = (double)element.GetValue(property);
        element.BeginAnimation(property, null);
        element.SetValue(property, to);
        if (!animate || !double.IsFinite(from) || Math.Abs(from - to) < .5) return;
        element.BeginAnimation(property, Arrival(from, to, milliseconds));
    }
}
