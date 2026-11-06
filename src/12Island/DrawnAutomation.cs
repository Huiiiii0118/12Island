using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace TwelveIsland;

// How the panel's drawn parts read to UI Automation (Narrator, Windows' other screen readers). An element that draws itself has
// no automation peer, so a screen reader passes over it even when it has a name. DrawnPeer gives it one: what it shows now as its
// name ("CPU 占用 47% · 62 °C"), what kind of thing it is (a button, a progress bar, a list), a share as a read-only value from
// 0 to 100, and, for one that takes clicks, the click. Its tooltip stays its help text. Parts drawn inside one element (a list's
// rows, a graph's columns) are its children (DrawnPart); each is read in turn and placed where it is drawn.
internal sealed class DrawnPeer : FrameworkElementAutomationPeer, IInvokeProvider, IRangeValueProvider
{
    private readonly AutomationControlType _type;
    private readonly Func<string> _name;
    private readonly Func<bool>? _clickable;
    private readonly Action? _invoke;
    private readonly Func<double?>? _share;
    private readonly Func<IEnumerable<DrawnPart>>? _parts;
    private readonly Dictionary<string, DrawnPartPeer> _partPeers = [];
    // The keys of the parts as they were last listed, to tell a changed list from a changed reading.
    private string _keys = "";

    // type: its kind; one whose clicks come and go (clickable) is a button while it takes them, and type otherwise.
    public DrawnPeer(FrameworkElement owner, AutomationControlType type, Func<string> name, Action? invoke = null, Func<bool>? clickable = null,
        Func<double?>? share = null, Func<IEnumerable<DrawnPart>>? parts = null) : base(owner)
    {
        _type = type; _name = name; _invoke = invoke; _clickable = clickable; _share = share; _parts = parts;
    }

    private bool Clickable => _invoke is not null && (_clickable?.Invoke() ?? true);

    protected override string GetNameCore() { string name = _name(); return name.Length > 0 ? name : base.GetNameCore(); }
    protected override AutomationControlType GetAutomationControlTypeCore() => _clickable is not null && Clickable ? AutomationControlType.Button : _type;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    public override object GetPattern(PatternInterface pattern) => pattern switch
    {
        PatternInterface.Invoke when Clickable => this,
        // A share with no reading is not a value of zero: it offers no value at all.
        PatternInterface.RangeValue when _share?.Invoke() is not null && !(_clickable is not null && Clickable) => this,
        _ => base.GetPattern(pattern)
    };

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (_parts is null) return base.GetChildrenCore();
        var children = new List<AutomationPeer>();
        var current = new HashSet<string>();
        var order = new List<string>();
        foreach (var part in _parts())
        {
            // The same part keeps its peer while it stays, so a screen reader's place in the list survives a new reading.
            if (!_partPeers.TryGetValue(part.Key, out var peer)) _partPeers[part.Key] = peer = new DrawnPartPeer(this);
            peer.Part = part;
            current.Add(part.Key); order.Add(part.Key);
            children.Add(peer);
        }
        foreach (var key in _partPeers.Keys.Where(key => !current.Contains(key)).ToList()) _partPeers.Remove(key);
        _keys = string.Join('\n', order);
        return children;
    }

    // WPF keeps the children a peer listed until it is told they changed, and a part keeps the box and name it was listed with, so
    // an element that draws its parts from data it replaces calls this after each replacement (not each frame): the parts a client
    // already holds are brought up to date, the list is listed again at the next query, and a client that listens for structure
    // changes is told when the parts themselves came or went. A peer no client has asked for yet does not exist, and costs nothing.
    public static void Refresh(UIElement owner)
    {
        if (UIElementAutomationPeer.FromElement(owner) is DrawnPeer { _parts: not null } peer) peer.RefreshParts();
    }

    private void RefreshParts()
    {
        var keys = new List<string>();
        foreach (var part in _parts!())
        {
            if (_partPeers.TryGetValue(part.Key, out var peer)) peer.Part = part;
            keys.Add(part.Key);
        }
        ResetChildrenCache();
        if (string.Join('\n', keys) != _keys && ListenerExists(AutomationEvents.StructureChanged)) InvalidatePeer();
    }

    void IInvokeProvider.Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        // As a button does, the click runs after the call returns, so what it rebuilds is not torn down under the caller.
        if (Clickable) Owner.Dispatcher.BeginInvoke(_invoke!);
    }

    double IRangeValueProvider.Value => Math.Round(Math.Clamp(_share?.Invoke() ?? 0, 0, 1) * 100);
    bool IRangeValueProvider.IsReadOnly => true;
    double IRangeValueProvider.Minimum => 0;
    double IRangeValueProvider.Maximum => 100;
    double IRangeValueProvider.SmallChange => 0;
    double IRangeValueProvider.LargeChange => 0;
    void IRangeValueProvider.SetValue(double value) => throw new ElementNotEnabledException();

    // The first line of a tooltip, for a name; the rest stays in the help text.
    public static string FirstLine(object? tip) => tip is string text ? text.Split('\n')[0].Trim() : "";
}

// One part drawn inside an element: its key (stable while it stays), its name, its box in the element's coordinates, its kind,
// and what a click on it does.
internal sealed record DrawnPart(string Key, string Name, Rect Box, AutomationControlType Type = AutomationControlType.ListItem, Action? Invoke = null);

internal sealed class DrawnPartPeer(DrawnPeer owner) : AutomationPeer, IInvokeProvider
{
    public DrawnPart Part { get; set; } = new("", "", Rect.Empty);
    private UIElement Element => owner.Owner;

    protected override string GetNameCore() => Part.Name;
    protected override AutomationControlType GetAutomationControlTypeCore() => Part.Type;
    protected override string GetClassNameCore() => "";
    protected override string GetAutomationIdCore() => "";
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override bool HasKeyboardFocusCore() => false;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => Element.IsEnabled;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override void SetFocusCore() { }
    public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke && Part.Invoke is not null ? this : null;

    // The box on the screen, in physical pixels; empty while the element is not shown.
    protected override Rect GetBoundingRectangleCore()
    {
        if (Part.Box.IsEmpty || PresentationSource.FromVisual(Element) is null) return Rect.Empty;
        return new Rect(Element.PointToScreen(Part.Box.TopLeft), Element.PointToScreen(Part.Box.BottomRight));
    }
    protected override bool IsOffscreenCore() => owner.IsOffscreen() || Part.Box.IsEmpty
        || !Part.Box.IntersectsWith(new Rect(Element.RenderSize));
    protected override Point GetClickablePointCore()
    {
        var box = GetBoundingRectangleCore();
        return box.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    void IInvokeProvider.Invoke()
    {
        if (!Element.IsEnabled) throw new ElementNotEnabledException();
        if (Part.Invoke is { } invoke) Element.Dispatcher.BeginInvoke(invoke);
    }
}
