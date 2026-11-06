using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TwelveIsland.Core;

namespace TwelveIsland;

// The expanded panel's scale (DeckSettings.PanelScale): the open panel's content, its header included, is laid out at the page
// sizes and drawn at the scale (UI.Scale), so it looks as it would with Windows' display scaling raised for TwelveIsland alone. Page
// sizes, the resize limits and the settings' sliders stay in unscaled DIPs; the island, the window and the panel's place on the
// monitor take the sizes times the scale. The collapsed surface takes the scale in CollapsedGeometry.Resolve (ShapeFor).
public partial class MainWindow
{
    private double _scale = 1;
    private readonly Unscaled _unscaled = new();

    private void InitializeScale()
    {
        // The content is as wide as the island before the scale, so drawn at the scale it fills the island as the island moves.
        ExpandedContent.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = Island, Converter = _unscaled });
    }

    // Takes the scale from the settings; ApplySettings calls it before it sizes the window.
    private void ApplyScale()
    {
        double scale = PanelScales.Factor(Settings);
        if (scale == _scale) return;
        _scale = scale; _unscaled.Scale = scale;
        UI.Scale(ExpandedContent, scale);
        // On a side edge the width is set by SetExpanded instead (MainWindow.Edge.cs).
        BindingOperations.GetBindingExpression(ExpandedContent, WidthProperty)?.UpdateTarget();
    }

    internal double Scale => _scale;
    // The open panel's corner radius as drawn.
    private double PanelRadius => PanelHeader.PanelRadius * _scale;

    private sealed class Unscaled : IValueConverter
    {
        public double Scale { get; set; } = 1;
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? width / Scale : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
