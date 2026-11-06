using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TwelveIsland;

// A change of theme while the island is on screen dissolves from the old look into the new one instead of snapping. Two
// pictures of the island as it was lie over it while the new colors are already drawn underneath:
//   the material alone (surface, gradient, rim), under the content, fading slowly for a theme's day and night looks
//   (DaylightFadeMs), as dusk does, and in about a hover's time for any other change;
//   the whole island, content too, over everything, fading over ThemeFadeMs whatever the change. Readings and titles go on
//   changing under it, and two different figures must never stand over each other for long (the monitor's rule), so the old
//   content leaves quickly even while the old surface lingers.
// Nothing moves with animations off; each picture is taken once, so the fade costs two bitmaps and two opacity animations. A
// change during a fade lets the pictures already fading go on fading under the new ones, so nothing that was on screen
// vanishes at once; a change of the island's shape fades them out quickly (LeaveMs), as they no longer lie on it.
public partial class MainWindow
{
    private const int DaylightFadeMs = 1600, ThemeFadeMs = 320, LeaveMs = 120;
    private Image? _themeFade, _materialFade;

    private void InitializeThemeFade()
    {
        PanelTheme.Changing += BeginThemeFade;
        Closed += (_, _) => PanelTheme.Changing -= BeginThemeFade;
        // The pictures lie on the island's present shape only.
        IsVisibleChanged += (_, _) => { if (!IsVisible) EndThemeFade(); };
        Island.SizeChanged += (_, _) => EndThemeFade(quickly: true);
    }

    // PanelTheme.Changing: the old look is still on screen, and PanelTheme.Current is still the old palette. The new one is
    // drawn on the frames that follow.
    private void BeginThemeFade(bool daylight)
    {
        // Pictures still fading are left to finish on their own, under the new ones.
        _themeFade = null; _materialFade = null;
        if (!IsVisible || !Soft.Enabled || _preview || Island.ActualWidth < 1 || Island.ActualHeight < 1 || PresentationSource.FromVisual(Island) is null) return;
        if (Island.Child is not Panel content) return;
        double width = Island.ActualWidth, height = Island.ActualHeight;
        // The material: a copy of the island's own shape, dressed as it is now, without its content.
        var material = new IslandBorder
        {
            Width = width, Height = height, CornerRadius = Island.CornerRadius, ShoulderRadius = Island.ShoulderRadius, MaterialOpacity = Island.MaterialOpacity,
            // The brush's opacity too, whatever it is, so the copy looks exactly as the surface does.
            Background = new SolidColorBrush(_surface.Color) { Opacity = _surface.Opacity }
        };
        PanelTheme.Dress(material, dpiScale: VisualTreeHelper.GetDpi(Island).DpiScaleX);
        _materialFade = Fade(Picture(material, width, height), daylight ? DaylightFadeMs : ThemeFadeMs);
        // Under the content, above the island's own drawing.
        content.Children.Insert(0, _materialFade);
        // The whole island, drawn through a brush so its place in the window does not offset the picture.
        _themeFade = Fade(Picture(new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = new VisualBrush(Island) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } },
            width, height), ThemeFadeMs);
        var at = Island.TranslatePoint(new Point(), HoverZone);
        _themeFade.Margin = new Thickness(at.X, at.Y, 0, 0);
        Panel.SetZIndex(_themeFade, int.MaxValue);
        HoverZone.Children.Add(_themeFade);
    }

    private BitmapSource Picture(FrameworkElement element, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(Island);
        // The copy is laid out at the island's scale, so its edges fall on the same device pixels.
        VisualTreeHelper.SetRootDpi(element, dpi);
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height));
        var picture = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        picture.Render(element);
        picture.Freeze();
        return picture;
    }

    private Image Fade(BitmapSource picture, int milliseconds)
    {
        var image = new Image
        {
            Source = picture, Width = picture.Width, Height = picture.Height, Stretch = Stretch.Fill, IsHitTestVisible = false, Tag = "fade",
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var fade = Soft.Fade(1, 0, milliseconds, Soft.InOut);
        fade.Completed += (_, _) => Remove(image);
        image.BeginAnimation(OpacityProperty, fade);
        return image;
    }

    // When the island hides, at once; when it changes shape, the pictures (which would no longer lie on it) fade out from where
    // they are over LeaveMs, also any left from an earlier change.
    private void EndThemeFade(bool quickly = false)
    {
        foreach (var image in Fading())
        {
            if (!quickly || !Soft.Enabled) { Remove(image); continue; }
            if (image.Tag is "leaving") continue;
            image.Tag = "leaving";
            var leave = Soft.Fade(image.Opacity, 0, LeaveMs, Soft.Out);
            leave.Completed += (_, _) => Remove(image);
            image.BeginAnimation(OpacityProperty, leave);
        }
    }

    // Every picture on the island now: the current pair and any left from an earlier change.
    private List<Image> Fading()
    {
        var pictures = new List<Image>();
        if (Island.Child is Panel content) pictures.AddRange(content.Children.OfType<Image>().Where(image => image.Tag is "fade" or "leaving"));
        pictures.AddRange(HoverZone.Children.OfType<Image>().Where(image => image.Tag is "fade" or "leaving"));
        return pictures;
    }

    private void Remove(Image image)
    {
        if (ReferenceEquals(image, _themeFade)) _themeFade = null;
        if (ReferenceEquals(image, _materialFade)) _materialFade = null;
        image.BeginAnimation(OpacityProperty, null);
        (image.Parent as Panel)?.Children.Remove(image);
    }
}
