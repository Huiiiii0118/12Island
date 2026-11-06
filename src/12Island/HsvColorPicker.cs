using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

// A saturation/value square over a hue strip. Dragging (with the mouse captured, so it continues outside the control) or the
// arrow keys change the color and raise Changed; SetColor moves both thumbs without raising it.
internal sealed class HsvColorPicker : StackPanel
{
    private const double Thumb = 14, HueHeight = 12, HueThumb = 16;
    private readonly double _width, _height;
    private double _h, _s, _v;
    private readonly SolidColorBrush _hueBase = new(Colors.Red), _current = new(Colors.White), _hueColor = new(Colors.Red);
    private readonly Ellipse _squareThumb, _hueThumb;
    private readonly FrameworkElement _square, _hue;
    public event Action<Color>? Changed;
    public Color Color { get { var (r, g, b) = ColorMath.FromHsv(_h, _s, _v); return Color.FromRgb(r, g, b); } }

    public HsvColorPicker(double width, double height, string border)
    {
        _width = width; _height = height;
        var outline = (SolidColorBrush)new BrushConverter().ConvertFromString(border)!;
        // The square: the pure hue, white towards the left, black towards the bottom.
        var layers = new Grid { Width = width, Height = height, Clip = new RectangleGeometry(new Rect(0, 0, width, height), 6, 6) };
        layers.Children.Add(new Border { Background = _hueBase });
        layers.Children.Add(new Border { Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        layers.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        layers.Children.Add(new Border { BorderBrush = outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Opacity = .6 });
        _squareThumb = ThumbShape(Thumb, _current);
        var square = new Grid { Width = width, Height = height, Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null, Cursor = Cursors.Cross };
        square.Children.Add(layers);
        var squareCanvas = new Canvas { IsHitTestVisible = false }; squareCanvas.Children.Add(_squareThumb); square.Children.Add(squareCanvas);
        AutomationProperties.SetName(square, Loc.T("饱和度和亮度", "Saturation and brightness"));
        Drag(square, p => { _s = Math.Clamp(p.X / width, 0, 1); _v = 1 - Math.Clamp(p.Y / height, 0, 1); });
        square.KeyDown += (_, e) =>
        {
            const double step = .02;
            switch (e.Key)
            {
                case Key.Left: _s = Math.Max(0, _s - step); break;
                case Key.Right: _s = Math.Min(1, _s + step); break;
                case Key.Up: _v = Math.Min(1, _v + step); break;
                case Key.Down: _v = Math.Max(0, _v - step); break;
                default: return;
            }
            e.Handled = true; Update(); Changed?.Invoke(Color);
        };
        _square = square; Children.Add(square);

        // The hue strip: a rounded rainbow with a round thumb that shows the pure hue.
        var strip = new Border
        {
            Height = HueHeight, CornerRadius = new CornerRadius(HueHeight / 2), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(new GradientStopCollection(
                new[] { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" }
                    .Select((c, i) => new GradientStop((Color)ColorConverter.ConvertFromString(c), i / 6d))), new Point(0, 0), new Point(1, 0))
        };
        _hueThumb = ThumbShape(HueThumb, _hueColor);
        var hue = new Grid { Width = width, Height = HueThumb, Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 0) };
        hue.Children.Add(strip);
        var hueCanvas = new Canvas { IsHitTestVisible = false }; hueCanvas.Children.Add(_hueThumb); hue.Children.Add(hueCanvas);
        AutomationProperties.SetName(hue, Loc.T("色相", "Hue"));
        Drag(hue, p => _h = Math.Min(Math.Clamp(p.X / width, 0, 1) * 360, 359.9));
        hue.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            _h = (_h + (e.Key == Key.Right ? 3 : 357)) % 360; e.Handled = true; Update(); Changed?.Invoke(Color);
        };
        _hue = hue; Children.Add(hue);
        Update();
    }

    public void SetColor(Color color)
    {
        var (h, s, v) = ColorMath.ToHsv(color.R, color.G, color.B);
        // Grays and black have no hue of their own; the strip keeps the last one.
        if (s > 0 && v > 0) _h = h;
        _s = s; _v = v; Update();
    }

    private static Ellipse ThumbShape(double size, Brush fill) => new()
    {
        Width = size, Height = size, Fill = fill, Stroke = Brushes.White, StrokeThickness = 2,
        Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = .55, Color = Colors.Black }
    };

    // Capturing the mouse inside a popup that closes on outside clicks is safe: WPF's Popup accepts capture moving to an element
    // within it and takes capture back when that element releases it, so the popup stays open during and after the drag.
    private void Drag(FrameworkElement target, Action<Point> set)
    {
        void Apply(MouseEventArgs e) { set(e.GetPosition(target)); Update(); Changed?.Invoke(Color); }
        target.MouseLeftButtonDown += (_, e) => { target.Focus(); target.CaptureMouse(); Apply(e); e.Handled = true; };
        target.MouseMove += (_, e) => { if (target.IsMouseCaptured) Apply(e); };
        target.MouseLeftButtonUp += (_, e) => { if (target.IsMouseCaptured) { target.ReleaseMouseCapture(); e.Handled = true; } };
    }

    private void Update()
    {
        var (r, g, b) = ColorMath.FromHsv(_h, 1, 1); _hueBase.Color = Color.FromRgb(r, g, b); _hueColor.Color = _hueBase.Color; _current.Color = Color;
        Canvas.SetLeft(_squareThumb, _s * _width - Thumb / 2); Canvas.SetTop(_squareThumb, (1 - _v) * _height - Thumb / 2);
        Canvas.SetLeft(_hueThumb, _h / 360 * _width - HueThumb / 2); Canvas.SetTop(_hueThumb, 0);
        AutomationProperties.SetHelpText(_square, $"{_s * 100:0}% / {_v * 100:0}%"); AutomationProperties.SetHelpText(_hue, $"{_h:0}°");
    }
}
