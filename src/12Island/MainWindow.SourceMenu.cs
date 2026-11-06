using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

public partial class MainWindow
{
    private PromptTimer _sourceMenuWatch = null!;
    private PointerPressObserver? _sourceMenuPress;
    private DateTimeOffset? _sourceMenuOutsideAt;
    private Rect _sourceMenuBounds = Rect.Empty;

    private void ResizeSourceMenu(ContextMenu menu)
    {
        var compact = MusicSizes.For(PanelSize.Compact);
        var standard = MusicSizes.For(PanelSize.Standard);
        var spacious = MusicSizes.For(PanelSize.Spacious);
        // The presets are interpolation anchors, not buckets. The narrower
        // dimension limits typography for wide/short and narrow/tall panels.
        // The compact and standard music presets share their width; a width up to it counts as standard.
        static double Step(double value, double small, double middle, double large) => Math.Clamp(
            value <= middle ? (middle > small ? (value - small) / (middle - small) : 1) : 1 + (value - middle) / (large - middle), 0, 3);
        double widthStep = Step(ContentCanvas.Width, compact.Width, standard.Width, spacious.Width);
        double heightStep = Step(ContentCanvas.Height, compact.Height, standard.Height, spacious.Height);
        double density = Math.Min(widthStep, heightStep), font = 10.5 + density, gap = 3 + 1.5 * density;
        double maxWidth = Math.Min(172 + 28 * Math.Min(1, widthStep) + 32 * Math.Max(0, widthStep - 1), Math.Max(120, ContentCanvas.Width - 40));
        menu.Padding = new Thickness(2 + Math.Min(1, density));
        double anchorBottom = MediaSourceButton.TranslatePoint(new Point(0, MediaSourceButton.ActualHeight), ExpandedContent).Y;
        menu.MaxHeight = Math.Clamp(ContentCanvas.Height - anchorBottom - menu.VerticalOffset / _scale - 8, 18 + 2 * density + 2 * menu.Padding.Top + 2, 400);
        foreach (var separator in menu.Items.OfType<Separator>())
            separator.Margin = new Thickness(4 + density, 2 + density, 6 + density, 2 + density);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Height = 18 + 2 * density; item.Padding = new Thickness(4 + density, 0, 6 + density, 0);
            if (item.Header is TextBlock action) { action.FontSize = font; continue; }
            var row = (Grid)item.Header;
            var source = (TextBlock)row.Children[0]; var title = (MusicMarquee)row.Children[1]; var status = (TextBlock)row.Children[2];
            source.FontSize = font; source.MaxWidth = font * 8; source.Margin = new Thickness(0, 0, gap, 0);
            title.FontSize = font - .5; title.Height = item.Height; title.Margin = new Thickness(0, 0, gap * 2, 0); status.FontSize = font - 1.5;
        }
        menu.Width = maxWidth;
        menu.HorizontalOffset = MediaSourceButton.ActualWidth - menu.Width;
    }

    private void TickSourceMenu()
    {
        var now = DateTimeOffset.UtcNow;
        CheckSourceMenuPointer(WindowsHost.Cursor(), now: now);
        UpdateSourceMenuTitles(Settings.Animations && SystemParameters.ClientAreaAnimation);
    }
    private void UpdateSourceMenuTitles(bool animations)
    {
        if (MediaSourceButton.ContextMenu is not { IsOpen: true } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Header is Grid row && row.Children[1] is MusicMarquee title) title.SetScrolling(animations);
    }

    private void WatchSourceMenu(ContextMenu menu)
    {
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
        SizeChangedEventHandler resized = (_, _) => { if (menu.IsOpen) ResizeSourceMenu(menu); };
        menu.Opened += (_, _) =>
        {
            if (!ReferenceEquals(MediaSourceButton.ContextMenu, menu)) { menu.IsOpen = false; return; }
            _hover.Stop(); _leave.Stop(); _sourceMenuOutsideAt = null;
            UpdateSourceMenuTitles(Settings.Animations && SystemParameters.ClientAreaAnimation);
            ContentCanvas.SizeChanged += resized;
            menu.UpdateLayout(); _sourceMenuBounds = ScreenBounds(menu);
            _sourceMenuPress?.Dispose();
            _sourceMenuPress = new PointerPressObserver(Dispatcher, point =>
            {
                if (!_app.Exiting && ReferenceEquals(MediaSourceButton.ContextMenu, menu)) CheckSourceMenuPointer(point, pressed: true);
            });
            _sourceMenuWatch.Start();
        };
        menu.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, new MouseButtonEventHandler((_, _) =>
            CheckSourceMenuPointer(WindowsHost.Cursor(), pressed: true)), true);
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseSourceMenu(); SetExpanded(false, keyboard: true); e.Handled = true; }
        };
        menu.Closed += (_, _) =>
        {
            ContentCanvas.SizeChanged -= resized;
            if (!ReferenceEquals(MediaSourceButton.ContextMenu, menu)) return;
            _sourceMenuWatch.Stop(); _sourceMenuPress?.Dispose(); _sourceMenuPress = null; _sourceMenuOutsideAt = null;
            if (!_expanded || _app.Exiting) return;
            _pointerInside = ScreenBounds(Island).Contains(WindowsHost.Cursor());
            _leave.Stop();
            // After selecting a source, allow the pointer to return from the old
            // menu bounds to the panel even when the normal close delay is zero.
            if (!_pointerInside && !_preview)
            { _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(250, Settings.CloseDelay)); _leave.Start(); }
        };
    }

    private static Rect ScreenBounds(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null || element.ActualWidth <= 0 || element.ActualHeight <= 0) return Rect.Empty;
        return new Rect(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    internal void CheckSourceMenuPointer(Point screenPoint, bool pressed = false, DateTimeOffset? now = null)
    {
        if (MediaSourceButton.ContextMenu is not { } menu || !menu.IsOpen && !pressed) return;
        if (!_expanded || !IsVisible || !IsMusicPage) { CloseSourceMenu(); return; }
        var bounds = ScreenBounds(menu);
        if (!bounds.IsEmpty) _sourceMenuBounds = bounds;
        var anchor = ScreenBounds(MediaSourceButton);
        bool inPanel = ScreenBounds(Island).Contains(screenPoint);
        bool inMenu = _sourceMenuBounds.Contains(screenPoint), inAnchor = anchor.Contains(screenPoint);
        if (pressed)
        {
            if (!inMenu && !inAnchor)
            {
                CloseSourceMenu();
                if (!inPanel) { _preview = false; _pointerInside = false; SetExpanded(false); }
            }
            return;
        }
        // The menu is a separate HWND. Use physical screen bounds instead of
        // MouseLeave, which also fires while crossing between these two windows.
        Rect bridge = Rect.Empty;
        if (!anchor.IsEmpty && !_sourceMenuBounds.IsEmpty)
        {
            double top = Math.Min(anchor.Bottom, _sourceMenuBounds.Top), bottom = Math.Max(anchor.Bottom, _sourceMenuBounds.Top);
            if (_sourceMenuBounds.Bottom <= anchor.Top) { top = _sourceMenuBounds.Bottom; bottom = anchor.Top; }
            bridge = new Rect(new Point(Math.Min(anchor.Left, _sourceMenuBounds.Left), top),
                new Point(Math.Max(anchor.Right, _sourceMenuBounds.Right), bottom));
            bridge.Inflate(4, 4);
        }
        if (inMenu || inAnchor || bridge.Contains(screenPoint))
        { _sourceMenuOutsideAt = null; _leave.Stop(); return; }
        var time = now ?? DateTimeOffset.UtcNow;
        _sourceMenuOutsideAt ??= time;
        if ((time - _sourceMenuOutsideAt.Value).TotalMilliseconds < Math.Max(250, Settings.CloseDelay)) return;
        CloseSourceMenu(); _pointerInside = inPanel;
        if (!inPanel && !_preview) SetExpanded(false);
    }

    private void CloseSourceMenu()
    {
        if (MediaSourceButton.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        _sourceMenuWatch.Stop(); _sourceMenuPress?.Dispose(); _sourceMenuPress = null; _sourceMenuOutsideAt = null;
    }
}
