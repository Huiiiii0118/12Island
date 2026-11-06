using System.Globalization;
using System.Windows;
using TwelveIsland.Core;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private FrameworkElement SettingLabel(string title, Func<string>? help = null)
    {
        var label = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        if (help is null) return label;
        return new HelpHeading(label, HelpIcon(title, help, 14)) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    }

    // The info icon that shows a setting's or a section's description on hover or focus.
    private FrameworkElement HelpIcon(string title, Func<string> help, double size)
    {
        // The geometry is drawn for a 14 DIP box; smaller icons scale it so the circle stays centered and unclipped.
        var icon = Stroke("M 7,1 A 6,6 0 1 1 6.99,1 M 7,6.5 V 10 M 7,4 V 4.1", 14, 14, TextTertiary);
        if (size != 14) icon.LayoutTransform = new ScaleTransform(size / 14, size / 14);
        var info = new HelpMark
        {
            Width = size + 8, Height = 24, Margin = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Focusable = true, Cursor = Cursors.Help,
            Child = icon, FocusVisualStyle = (Style)FindResource("SettingsHelpFocus")
        };
        AttachSettingHelp(info, title, help);
        return info;
    }

    // An info mark (ⓘ). A Border has no automation peer, so a screen reader would find the mark's name and description empty:
    // this one reads as a button named for its setting ("开机自启动说明") with the description as its help text (AttachSettingHelp),
    // and invoking it opens or closes the description, as hovering or focusing it does.
    private sealed class HelpMark : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.Button, () => AutomationProperties.GetName(this),
            () => { if (ToolTip is ToolTip tip) tip.IsOpen = !tip.IsOpen; });
    }

    private static void AttachSettingHelp(FrameworkElement info, string title, Func<string> help)
    {
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var tooltip = new ToolTip { Content = description, PlacementTarget = info, Placement = PlacementMode.Bottom, HorizontalOffset = -4, VerticalOffset = 4 };
        info.ToolTip = tooltip;
        // An explicit help target should respond immediately, including when switching between nearby icons.
        ToolTipService.SetInitialShowDelay(info, 0);
        ToolTipService.SetBetweenShowDelay(info, 0);
        void UpdateHelp()
        {
            description.Text = help();
            AutomationProperties.SetHelpText(info, description.Text);
        }
        UpdateHelp();
        AutomationProperties.SetName(info, Loc.T(title + "说明", "About " + title));
        info.ToolTipOpening += (_, _) => UpdateHelp();
        // WPF normally keeps a tooltip open over the popup and the gap below its owner.
        // Help icons own the hover boundary; reopening here also handles immediate re-entry.
        info.MouseEnter += (_, _) => { UpdateHelp(); tooltip.IsOpen = true; };
        info.MouseLeave += (_, _) => tooltip.IsOpen = false;
        info.GotKeyboardFocus += (_, _) => { UpdateHelp(); tooltip.IsOpen = true; };
        info.LostKeyboardFocus += (_, _) => tooltip.IsOpen = false;
        info.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { tooltip.IsOpen = false; e.Handled = true; } };
        info.Unloaded += (_, _) => tooltip.IsOpen = false;
    }

    // A text field's keyboard focus: the field draws no outline of its own (FocusVisualStyle is off in SettingsNumber), so its frame
    // turns to the accent color while input has the focus. rest: the frame's color without focus.
    private void FieldFocus(Border frame, UIElement input, Func<Brush> rest) =>
        input.IsKeyboardFocusWithinChanged += (_, _) => frame.BorderBrush = input.IsKeyboardFocusWithin ? UI.Brush(_palette.Accent) : rest();

    // Where each segmented control's highlight stood when the user last pressed it, so a page rebuilt by that
    // choice (theme, quick size) can still slide the highlight from the old option instead of jumping.
    private readonly Dictionary<string, (double X, DateTime Pressed)> _segmentPresses = [];

    // The selected option's fill is one shared highlight that slides between options.
    private Border SegmentBorder(FrameworkElement content)
    {
        var options = ((Panel)content).Children.OfType<RadioButton>().ToList();
        string group = options.FirstOrDefault()?.GroupName ?? "";
        var move = new TranslateTransform();
        var highlight = new Border { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, RenderTransform = move, Visibility = Visibility.Hidden };
        highlight.SetResourceReference(Border.BackgroundProperty, "SettingsFillSelected");
        // The chosen option is outlined as well: its fill alone stands too close to the control's own (SW-07).
        highlight.BorderThickness = new Thickness(1); highlight.SetResourceReference(Border.BorderBrushProperty, "SettingsInputBorder");
        var host = new Grid(); host.Children.Add(highlight); host.Children.Add(content);
        void Slide(double from, double to)
        {
            if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - to) < 0.5) { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = to; return; }
            var slide = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
            { EasingFunction = Soft.Out };
            // Hand the final position back to the property so later layout passes can move the highlight again.
            slide.Completed += (_, _) => { if (move.X == to) { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = to; } };
            move.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        void Place(bool changed)
        {
            var chosen = options.FirstOrDefault(o => o.IsChecked == true);
            if (chosen is null || chosen.ActualWidth <= 0 || !host.IsAncestorOf(chosen)) { highlight.Visibility = Visibility.Hidden; return; }
            var at = chosen.TranslatePoint(new Point(), host);
            highlight.Width = chosen.ActualWidth; highlight.Height = chosen.ActualHeight; highlight.Margin = new Thickness(0, at.Y, 0, 0);
            double from = move.X;
            bool animate = changed && highlight.Visibility == Visibility.Visible;
            // A freshly built control continues the slide the user started on the control it replaced.
            if (!changed && _segmentPresses.TryGetValue(group, out var press) && DateTime.Now - press.Pressed < TimeSpan.FromMilliseconds(800))
            { from = press.X; animate = true; _segmentPresses.Remove(group); }
            if (animate) Slide(from, at.X); else { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = at.X; }
            highlight.Visibility = Visibility.Visible;
        }
        foreach (var option in options)
        {
            option.PreviewMouseLeftButtonDown += (_, _) => { if (highlight.Visibility == Visibility.Visible) _segmentPresses[group] = (move.X, DateTime.Now); };
            option.Checked += (_, _) => Place(true);
        }
        // The first size pass places the highlight (and may start a continued slide); later passes only follow resizes.
        host.Loaded += (_, _) => { if (highlight.Visibility != Visibility.Visible) Place(false); };
        host.SizeChanged += (_, _) => { if (move.HasAnimatedProperties) return; Place(false); };
        return new Border { Child = host, Background = UI.Brush(Inset), CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Margin = new Thickness(0, 4, 0, 4) };
    }

    private (Slider Slider, Border Number) NumericControls(string title, double value, double min, double max, double tick, string unit, Action<double> changed)
    {
        var slider = new Slider { Style=(Style)FindResource("SettingsSlider"),Minimum=min,Maximum=max,Value=Math.Clamp(value,min,max),
            SmallChange=tick,LargeChange=tick*2,TickFrequency=tick,IsSnapToTickEnabled=true,IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center };
        var input = new TextBox { Style=(Style)FindResource("SettingsNumber"),Width=40,Text=slider.Value.ToString("0",CultureInfo.InvariantCulture),VerticalAlignment=VerticalAlignment.Center };
        AutomationProperties.SetName(slider,title); AutomationProperties.SetName(input,Loc.T(title+"数值", title+" value"));
        var units=TextLine(unit,10,TextTertiary);units.Margin=new Thickness(4,0,6,0);units.VerticalAlignment=VerticalAlignment.Center;
        var minus=new RepeatButton { Style=(Style)FindResource("SettingsStepper"),Content="−" };
        var plus=new RepeatButton { Style=(Style)FindResource("SettingsStepper"),Content="+" };
        AutomationProperties.SetName(minus,Loc.T("减少"+title, "Decrease "+title));AutomationProperties.SetName(plus,Loc.T("增加"+title, "Increase "+title));
        // The framed box: the number, its unit, a divider and the steppers (NumberBox).
        var chip=NumberBox.Create(input,units,minus,plus,UI.Brush(Divider),UI.Brush(_palette.Input),UI.Brush(_palette.InputBorder));
        FieldFocus(chip, input, () => UI.Brush(_palette.InputBorder));
        void Set(double number)
        {
            var next=Math.Clamp(Math.Round(number),min,max);
            if(Math.Abs(slider.Value-next)<.01) { input.Text=next.ToString("0",CultureInfo.InvariantCulture);return; }
            slider.Value=next;
        }
        slider.ValueChanged+=(_,_)=>{input.Text=slider.Value.ToString("0",CultureInfo.InvariantCulture);changed(slider.Value);};
        void Commit()
        {
            if(double.TryParse(input.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number))Set(number);
            input.Text=slider.Value.ToString("0",CultureInfo.InvariantCulture);
        }
        minus.Click+=(_,_)=>Set(slider.Value-tick);plus.Click+=(_,_)=>Set(slider.Value+tick);
        input.LostKeyboardFocus+=(_,_)=>Commit();
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}if(e.Key==Key.Escape){input.Text=slider.Value.ToString("0",CultureInfo.InvariantCulture);e.Handled=true;}};
        return(slider,chip);
    }
    private FrameworkElement Dimension(string title, double value, double min, double max, Action<double> changed)
    {
        var panel=new StackPanel();
        var controls=NumericControls(title,value,min,max,10,"DIP",changed);
        KeepPreview(controls.Slider); KeepPreview(controls.Number);
        var head=new DockPanel();DockPanel.SetDock(controls.Number,Dock.Right);head.Children.Add(controls.Number);
        var label=TextLine(title,13,TextPrimary);label.VerticalAlignment=VerticalAlignment.Center;head.Children.Add(label);panel.Children.Add(head);
        controls.Slider.Margin=new Thickness(0,24,0,6);panel.Children.Add(controls.Slider);
        var scale=new DockPanel();var last=TextLine(max.ToString("0"),10,TextTertiary);DockPanel.SetDock(last,Dock.Right);scale.Children.Add(last);scale.Children.Add(TextLine(min.ToString("0"),10,TextTertiary));panel.Children.Add(scale);
        return panel;
    }

    // A question before an action that cannot be undone, in a small dialog over the settings: Cancel (Esc) or the action, in red
    // (in the accent color when it is not a danger).
    private bool Confirm(string title, string message, string action, bool danger = true)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = UI.PanelFont,
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary), ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/12Island;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog);
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var heading = TextLine(title, 16, TextPrimary, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(heading);
        var text = TextLine(message, 12.5, TextSecondary); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        bool confirmed = false;
        var cancel = ActionButton(Loc.T("取消", "Cancel"), dialog.Close); cancel.Style = (Style)dialog.FindResource("SettingsOutlineButton"); cancel.IsCancel = true;
        var confirm = ActionButton(action, () => { confirmed = true; dialog.Close(); }); confirm.Style = (Style)dialog.FindResource(danger ? "SettingsDangerButton" : "SettingsPrimaryButton");
        foreach (var button in new[] { cancel, confirm }) { button.MinWidth = 84; button.Padding = new Thickness(14, 6, 14, 6); }
        confirm.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        var frame = new Border { BorderThickness = new Thickness(1), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        // Cancel has the focus, so Enter alone does not delete.
        dialog.Loaded += (_, _) => cancel.Focus();
        dialog.ShowDialog();
        return confirmed;
    }
}

// A drop-down drawn as a button with a popup list (the app rows' quota source and usage app). A button reads as one that only acts;
// this one reads as a combo box with its chosen value and whether its list is open, and opens or closes it on request (SW-04).
internal sealed class DropDownButton : System.Windows.Controls.Button
{
    public Func<string> Value { get; init; } = () => "";
    public System.Windows.Controls.Primitives.Popup? Menu
    {
        get => _menu;
        set
        {
            _menu = value;
            if (value is null) return;
            value.Opened += (_, _) => Changed(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded);
            value.Closed += (_, _) => Changed(ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed);
        }
    }
    private System.Windows.Controls.Primitives.Popup? _menu;
    private void Changed(ExpandCollapseState from, ExpandCollapseState to) =>
        (UIElementAutomationPeer.FromElement(this) as Peer)?.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, from, to);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(DropDownButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider, IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;
        protected override string GetClassNameCore() => nameof(DropDownButton);
        public override object GetPattern(PatternInterface pattern) =>
            pattern is PatternInterface.ExpandCollapse or PatternInterface.Value ? this : base.GetPattern(pattern);
        public ExpandCollapseState ExpandCollapseState => owner._menu?.IsOpen == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        // Opening goes through the button's own click, which first fits the list to the screen.
        public void Expand() { if (owner._menu?.IsOpen != true) ((IInvokeProvider)this).Invoke(); }
        public void Collapse() { if (owner._menu is { IsOpen: true } menu) menu.IsOpen = false; }
        public string Value => owner.Value();
        public bool IsReadOnly => true;
        public void SetValue(string value) => throw new InvalidOperationException();
    }
}
