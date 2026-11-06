using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

// The custom date range picker of the AI usage page, inside its pop-up window (ModelDateWindow). It is built once and changed in
// place, as the system monitor page is (DatePickerMotion): the start and end markers glide between days as the monitor's sort
// pill glides, showing the day under them in the accent's own text color as they pass; the range band eases in and out; the
// active field's outline glides between the fields; a changed date comes up from faint; a month change slides the grid and its
// header the way of travel, the old month leaving before the new one arrives; the validation message opens and closes on the
// shared curve, and the apply and month buttons ease between available and not. Every value is driven from one step on the
// panel's shared frame clock (Motion) that runs only while something moves, at the panel's feedback pace (DatePickerTiming.Fixed);
// with animations off (TwelveIsland's or Windows') everything is set at once. Hover and press on its buttons fade as the monitor's
// do (Feedback).
internal sealed class ModelDatePicker : Border
{
    private const double Inner = 228, FieldGap = 6, CellHeight = 24, Radius = 8, MessageGap = 5, MessageRise = 4, Faint = .3, Unavailable = .4;
    private static ResourceDictionary? _styles;

    private DateTime _start, _end, _month;
    private bool _editingEnd;
    private readonly PanelPalette _palette;

    internal event Action<UsageDateRange>? Applied;
    internal event Action? Cancelled;
    internal UsageDateRange? SelectedRange => _start <= _end ? new(_start, _end) : null;

    private readonly StackPanel _body = new();
    private readonly Button[] _fields = new Button[2];
    private readonly TextBlock[] _fieldValues = new TextBlock[2];
    private readonly Border _fieldMark;
    private readonly TranslateTransform _fieldShift = new();
    private readonly Button _previous, _next, _apply;
    private readonly TextBlock _monthText, _monthLeaving;
    private readonly TranslateTransform _monthShift = new(), _monthLeavingShift = new();
    private readonly Grid _calendar = new() { Height = DatePickerMotion.Rows * CellHeight, ClipToBounds = true };
    private readonly Grid _page = new();
    private readonly TranslateTransform _pageShift = new();
    private readonly Image _leaving = new() { Stretch = Stretch.None, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TranslateTransform _leavingShift = new();
    private readonly Button[] _days = new Button[DatePickerMotion.Cells];
    private readonly TextBlock[] _dayText = new TextBlock[DatePickerMotion.Cells];
    private readonly Ellipse[] _todayDots = new Ellipse[DatePickerMotion.Cells];
    private readonly Rectangle[] _band = new Rectangle[DatePickerMotion.Cells * 2];
    private readonly Marker[] _markers = new Marker[2];
    private readonly Border _messageSlot = new() { ClipToBounds = true, Height = 0 };
    private readonly TextBlock _message;
    private readonly TranslateTransform _messageShift = new();

    // Eased values: the active field (0 start, 1 end), a changed date's presence, each half cell of the band, the message's
    // opening, and the opacity of the buttons that can be unavailable.
    private Eased _fieldAt;
    private readonly Eased[] _fieldFade = new Eased[2];
    private readonly Eased[] _bandFade = new Eased[DatePickerMotion.Cells * 2];
    private Eased _messageOpen;
    private Eased _previousFade, _nextFade, _applyFade;
    private double _messageHeight;
    private double _slideAt = double.NaN;
    private bool _slidePending;
    private int _slideDirection;
    private bool _hooked;
    private int _stillFrames;

    // A selected day's marker: an accent face under the day buttons and, above them, the grid's figures in the accent's text
    // color clipped to the face, so the day under it reads as selected wherever the marker is on its way.
    private sealed class Marker
    {
        public required Border Face;
        public required Grid Ink;
        public readonly TranslateTransform Shift = new();
        public readonly RectangleGeometry Clip = new() { RadiusX = Radius, RadiusY = Radius };
        public readonly TextBlock[] Figures = new TextBlock[DatePickerMotion.Cells];
        public readonly Ellipse[] Dots = new Ellipse[DatePickerMotion.Cells];
        public Eased Column, Row, Presence;
        public int Cell = -1;
    }

    internal ModelDatePicker(UsageDateRange range, PanelPalette? palette = null)
    {
        _palette = palette ?? PanelTheme.Current;
        _start = range.Start.Date; _end = range.End.Date; _month = new(_start.Year, _start.Month, 1);
        _styles ??= (ResourceDictionary)Application.LoadComponent(new Uri("/12Island;component/ModelDetailsResources.xaml", UriKind.Relative));
        Resources.MergedDictionaries.Add(_styles);
        // The picker's own theme brushes, so a preview of another palette shows that palette throughout.
        foreach (var (resource, token) in new[] { ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelAccent", "accent") }) Resources[resource] = Brush(token);
        var timing = Timing;
        _fieldAt = new(0, timing.Glide / 1000.0);
        for (int i = 0; i < 2; i++) _fieldFade[i] = new(1, timing.Dissolve / 1000.0);
        for (int i = 0; i < _bandFade.Length; i++) _bandFade[i] = new(0, timing.Band / 1000.0);
        _messageOpen = new(0, timing.Message / 1000.0);
        _previousFade = new(1, timing.Enable / 1000.0); _nextFade = new(1, timing.Enable / 1000.0); _applyFade = new(1, timing.Enable / 1000.0);

        System.Windows.Documents.TextElement.SetFontFamily(this, UI.PanelFont);
        Child = _body; Background = Brush("popup"); BorderBrush = Brush("popupBorder"); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(_palette.Effects.Pixel > 0 ? 0 : 10); Padding = new Thickness(8);
        if (_palette.Effects.Pixel > 0) TextOptions.SetTextRenderingMode(this, TextRenderingMode.Aliased);

        // The fields, with the active one's outline lying over both and gliding between them.
        var fields = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FieldGap) }); fields.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (bool end in new[] { false, true })
        {
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });
            content.Children.Add(UI.Centered(end ? Loc.T("结束", "End") : Loc.T("开始", "Start"), 10, Hex("textTertiary")));
            var value = UI.Centered("", 12, Hex("textPrimary"));
            Grid.SetRow(value, 1); content.Children.Add(value);
            bool chosen = end;
            var field = Control(content, end ? Loc.T("选择结束日期", "Choose the end date") : Loc.T("选择开始日期", "Choose the start date"), () => ChooseField(chosen));
            field.Padding = new Thickness(7, 2, 7, 2); field.Background = Brush("input"); field.BorderBrush = Brush("outline");
            field.FocusVisualStyle = TryFindResource("PanelOutsetKeyboardFocus") as Style;
            Grid.SetColumn(field, end ? 2 : 0); fields.Children.Add(field);
            _fields[end ? 1 : 0] = field; _fieldValues[end ? 1 : 0] = value;
        }
        _fieldMark = new Border { BorderBrush = Brush("accent"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Radius), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, Width = (Inner - FieldGap) / 2, RenderTransform = _fieldShift };
        Grid.SetColumnSpan(_fieldMark, 3); fields.Children.Add(_fieldMark);
        _body.Children.Add(fields);

        // The month and its arrows; the header keeps a second line for the month that leaves.
        var navigation = new Grid { Height = 26, Margin = new Thickness(0, 2, 0, 3) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        _previous = MonthButton(false); navigation.Children.Add(_previous);
        var header = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        _monthText = UI.Centered("", 12, Hex("textSecondary")); _monthText.HorizontalAlignment = HorizontalAlignment.Center; _monthText.RenderTransform = _monthShift;
        _monthLeaving = UI.Centered("", 12, Hex("textSecondary")); _monthLeaving.HorizontalAlignment = HorizontalAlignment.Center; _monthLeaving.RenderTransform = _monthLeavingShift; _monthLeaving.Opacity = 0;
        header.Children.Add(_monthLeaving); header.Children.Add(_monthText);
        Grid.SetColumn(header, 1); navigation.Children.Add(header);
        _next = MonthButton(true); Grid.SetColumn(_next, 2); navigation.Children.Add(_next);
        _body.Children.Add(navigation);

        var weekdays = new UniformGrid { Columns = DatePickerMotion.Columns, Rows = 1, Height = 18 };
        foreach (var day in Loc.IsEnglish ? new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" } : new[] { "一", "二", "三", "四", "五", "六", "日" })
        { var label = UI.Centered(day, 10, Hex("textTertiary")); label.TextAlignment = TextAlignment.Center; weekdays.Children.Add(label); }
        _body.Children.Add(weekdays);

        // The grid: the band under the day buttons, the markers' faces over them and the markers' figures over the faces, so a
        // theme's opaque hover (github-light) never covers a selected day; the picture of the month leaving lies beside the page
        // in the clipped calendar.
        var band = new Canvas { IsHitTestVisible = false };
        var bandFill = BandFill();
        for (int i = 0; i < _band.Length; i++) { _band[i] = new Rectangle { Fill = bandFill, Opacity = 0 }; band.Children.Add(_band[i]); }
        _page.Children.Add(band);
        var faces = new Canvas { IsHitTestVisible = false };
        var cells = new UniformGrid { Columns = DatePickerMotion.Columns, Rows = DatePickerMotion.Rows };
        for (int i = 0; i < DatePickerMotion.Cells; i++)
        {
            int cell = i;
            var (content, text, dot) = DayFace(Hex("textPrimary"), Hex("accent"));
            var button = Control(content, "", () => ChooseDate(DatePickerMotion.FirstShown(_month).AddDays(cell)));
            button.Height = CellHeight;
            _days[i] = button; _dayText[i] = text; _todayDots[i] = dot;
            cells.Children.Add(button);
        }
        _page.Children.Add(cells);
        _page.Children.Add(faces);
        for (int m = 0; m < 2; m++)
        {
            var face = new Border { Background = Brush("accent"), CornerRadius = new CornerRadius(Radius), Height = CellHeight, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var ink = new Grid { IsHitTestVisible = false, Opacity = 0 };
            var marker = new Marker { Face = face, Ink = ink, Column = new(0, timing.Glide / 1000.0), Row = new(0, timing.Glide / 1000.0), Presence = new(0, timing.Glide / 1000.0) };
            face.RenderTransform = marker.Shift; ink.Clip = marker.Clip;
            var figures = new UniformGrid { Columns = DatePickerMotion.Columns, Rows = DatePickerMotion.Rows };
            for (int i = 0; i < DatePickerMotion.Cells; i++)
            {
                var (content, text, dot) = DayFace(Hex("onAccent"), Hex("onAccent"));
                content.Height = CellHeight;
                marker.Figures[i] = text; marker.Dots[i] = dot; figures.Children.Add(content);
            }
            ink.Children.Add(figures);
            faces.Children.Add(face); _page.Children.Add(ink);
            _markers[m] = marker;
        }
        _page.RenderTransform = _pageShift; _leaving.RenderTransform = _leavingShift;
        _calendar.Children.Add(_leaving); _calendar.Children.Add(_page);
        _calendar.SizeChanged += (_, _) => LayOut();
        _body.Children.Add(_calendar);

        // The message has a slot that opens to its height, so the buttons below move on the shared curve rather than jump.
        _message = UI.Line(Loc.T("开始日期不能晚于结束日期。", "The start date cannot be later than the end date."), 11, Hex("danger"));
        _message.TextWrapping = TextWrapping.Wrap; _message.TextTrimming = TextTrimming.None;
        _message.Margin = new Thickness(0, MessageGap, 0, 0); _message.VerticalAlignment = VerticalAlignment.Top; _message.RenderTransform = _messageShift; _message.Opacity = 0;
        _messageSlot.Child = _message;
        _body.Children.Add(_messageSlot);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        var cancel = Control(Text(Loc.T("取消", "Cancel")), Loc.T("取消日期筛选", "Cancel the date filter"), () => Cancelled?.Invoke());
        cancel.Padding = new Thickness(10, 1, 10, 1); cancel.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(cancel);
        _apply = Control(Text(Loc.T("应用", "Apply")), Loc.T("应用日期筛选", "Apply the date filter"), ApplySelection);
        _apply.Padding = new Thickness(10, 1, 10, 1); _apply.Background = Brush("accent"); _apply.Foreground = Brush("onAccent");
        // The focus ring is drawn in the accent, so on an accent button it stands outside it.
        _apply.FocusVisualStyle = TryFindResource("PanelOutsetKeyboardFocus") as Style;
        actions.Children.Add(_apply);
        _body.Children.Add(actions);

        FillMonth();
        Refresh(false, false);
        Unloaded += (_, _) => Sleep();
    }

    // Its own pace, whatever the panel's animation length, as the panel's hover and press feedback (DatePickerTiming.Fixed).
    private static DatePickerTiming Timing => DatePickerTiming.Fixed;
    private static bool Moves => Soft.Enabled;
    private static double Clock() => Eased.Now;
    private string Hex(string token) => _palette[token].ToString();
    private Brush Brush(string token) => UI.Brush(Hex(token));

    // The band's color laid over the pop-up's: the half cells overlap by a hair to leave no seam at fractional widths, and a
    // translucent fill showed each overlap as a line.
    private Brush BandFill()
    {
        var over = _palette["rangeFill"]; var under = _palette["popup"];
        double a = over.A / 255.0, b = under.A / 255.0 * (1 - a), alpha = a + b;
        if (alpha <= 0) return Brushes.Transparent;
        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * a + bottom * b) / alpha);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), Mix(over.R, under.R), Mix(over.G, under.G), Mix(over.B, under.B)));
        brush.Freeze();
        return brush;
    }

    private TextBlock Text(string label) { var text = UI.Centered(label, 12, Hex("textPrimary")); text.ClearValue(TextBlock.ForegroundProperty); return text; }

    private Button Control(object content, string name, Action click)
    {
        var button = new Button { Content = content, Foreground = Brush("textPrimary"), MinWidth = 0, MinHeight = 24 };
        if (TryFindResource("DatePickerButton") is Style style) button.Style = style;
        AutomationProperties.SetName(button, name); button.Click += (_, _) => click();
        return button;
    }

    private Button MonthButton(bool next)
    {
        var icon = UI.Centered(next ? "" : "", 10, Hex("textPrimary"), font: new FontFamily("Segoe MDL2 Assets"));
        icon.ClearValue(TextBlock.ForegroundProperty);
        return Control(icon, next ? Loc.T("下个月", "Next month") : Loc.T("上个月", "Previous month"), () => ShowMonth(_month.AddMonths(next ? 1 : -1), true));
    }

    // A day's figure, with the dot that marks today under it.
    private static (Grid Content, TextBlock Text, Ellipse Dot) DayFace(string color, string dotColor)
    {
        var grid = new Grid();
        var text = UI.Centered("", 12, color); text.HorizontalAlignment = HorizontalAlignment.Center; text.TextAlignment = TextAlignment.Center;
        var dot = new Ellipse { Width = 3, Height = 3, Fill = UI.Brush(dotColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Hidden };
        grid.Children.Add(text); grid.Children.Add(dot);
        return (grid, text, dot);
    }

    // The window keeps room for the message from the start (its height with the message open), so it never resizes while the
    // message opens or closes.
    internal double FullHeight(double width)
    {
        _message.Measure(new Size(Math.Max(0, width - 2 * (BorderThickness.Left + Padding.Left)), double.PositiveInfinity));
        _messageHeight = Math.Ceiling(_message.DesiredSize.Height);
        double shown = _messageSlot.Height;
        _messageSlot.Height = _messageHeight;
        Measure(new Size(width, double.PositiveInfinity));
        double height = DesiredSize.Height;
        _messageSlot.Height = shown;
        InvalidateMeasure();
        return Math.Ceiling(height);
    }

    // ----- Choices -----

    private void ChooseField(bool end)
    {
        _editingEnd = end;
        var date = end ? _end : _start;
        if (!ShowMonth(new DateTime(date.Year, date.Month, 1), true)) Refresh(Moves, false);
    }

    internal void ChooseDate(DateTime date)
    {
        if (date.Date > DateTime.Today) return;
        if (_editingEnd) _end = date.Date;
        else { _start = date.Date; _editingEnd = true; }
        Refresh(Moves, false);
    }

    internal void ApplySelection()
    {
        if (SelectedRange is not { } range) return;
        Applied?.Invoke(range);
    }

    // Shows another month: the old one leaves as a picture while the grid, changed in place, comes in from the other side.
    // Returns whether the month changed.
    internal bool ShowMonth(DateTime month, bool animate)
    {
        month = new DateTime(month.Year, month.Month, 1);
        if (month == _month || month < DateTime.MinValue.AddMonths(1)) return false;
        int direction = DatePickerMotion.Direction(_month, month);
        // The picture is taken before a month change still under way is ended, as the panel's page switch does (motion-review
        // F17): of the calendar as it is drawn now, the month arriving and the one still leaving where and as faint as they are,
        // so a quick second click leaves from what was on screen rather than from the grid snapped into place for a frame. The
        // header's leaving name is the one that shows more.
        var image = animate && Moves ? PageHandover.Capture(_calendar) : null;
        string leavingName = _monthLeaving.Opacity > _monthText.Opacity ? _monthLeaving.Text : _monthText.Text;
        EndSlide();
        bool slide = image is not null;
        if (image is not null)
        {
            _leaving.Source = image.Source; _leaving.Width = image.Width; _leaving.Height = image.Height;
            _monthLeaving.Text = leavingName;
        }
        _month = month;
        FillMonth();
        Refresh(animate && Moves, true);
        if (slide)
        {
            // Held out of sight until the first frame after the change: the move starts once the grid has been laid out, as the
            // page switch's does, and loses none of its beginning to the rebuild.
            _slideDirection = direction; _slidePending = true; _slideAt = double.NaN;
            ApplySlide(DatePickerMotion.Slide(0, direction, Timing));
            Wake();
        }
        return true;
    }

    // ----- State to view -----

    // The figures and the parts that come with the month (which days are there, today, which can be chosen).
    private void FillMonth()
    {
        var first = DatePickerMotion.FirstShown(_month);
        var today = DateTime.Today;
        for (int i = 0; i < DatePickerMotion.Cells; i++)
        {
            var day = first.AddDays(i);
            string figure = day.Day.ToString(CultureInfo.InvariantCulture);
            _dayText[i].Text = figure;
            var shown = day == today ? Visibility.Visible : Visibility.Hidden;
            _todayDots[i].Visibility = shown;
            foreach (var marker in _markers) { marker.Figures[i].Text = figure; marker.Dots[i].Visibility = shown; }
            _days[i].IsEnabled = day <= today;
            _days[i].Opacity = day <= today ? 1 : Unavailable;
            AutomationProperties.SetName(_days[i], day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        _monthText.Text = Loc.IsEnglish ? _month.ToString("MMMM yyyy", Loc.Culture) : _month.ToString("yyyy 年 M 月", CultureInfo.InvariantCulture);
    }

    // Brings everything that shows the choice to it: gliding and easing when motion is true, at once otherwise. A month
    // change moves the band and the markers with the grid, in place.
    private void Refresh(bool motion, bool monthChanged)
    {
        double now = Clock();
        bool inPlace = motion && !monthChanged;
        for (int f = 0; f < 2; f++)
        {
            string text = (f == 1 ? _end : _start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_fieldValues[f].Text == text) continue;
            bool changed = _fieldValues[f].Text.Length > 0;
            _fieldValues[f].Text = text;
            // A changed date goes at once and the new one comes up from faint, as the monitor changes a reading in place.
            if (changed && motion) { _fieldFade[f].Set(Faint, false, now); _fieldFade[f].Set(1, true, now); }
        }
        _fieldAt.Set(_editingEnd ? 1 : 0, motion, now);

        var first = DatePickerMotion.FirstShown(_month);
        for (int i = 0; i < DatePickerMotion.Cells; i++)
        {
            var day = first.AddDays(i);
            var part = DatePickerMotion.Band(day, _start, _end);
            _bandFade[2 * i].Set(part is BandPart.Full or BandPart.Trailing ? 1 : 0, inPlace, now);
            _bandFade[2 * i + 1].Set(part is BandPart.Full or BandPart.Leading ? 1 : 0, inPlace, now);
            // Days of the next or previous month are dimmed, except inside the range, where only the primary text reads on its fill.
            _dayText[i].Foreground = Brush(day.Month == _month.Month || part != BandPart.None ? "textPrimary" : "textTertiary");
        }
        Place(_markers[0], DatePickerMotion.CellOf(_month, _start), monthChanged, motion, now);
        Place(_markers[1], DatePickerMotion.CellOf(_month, _end), monthChanged, motion, now);

        bool error = DatePickerMotion.ShowsError(_start, _end);
        _messageOpen.Set(error ? 1 : 0, motion, now);
        _apply.IsEnabled = SelectedRange is not null;
        _previous.IsEnabled = _month > DateTime.MinValue.AddMonths(1);
        _next.IsEnabled = _month.AddMonths(1) <= DateTime.Today;
        _applyFade.Set(_apply.IsEnabled ? 1 : Unavailable, motion, now);
        _previousFade.Set(_previous.IsEnabled ? 1 : Unavailable, motion, now);
        _nextFade.Set(_next.IsEnabled ? 1 : Unavailable, motion, now);
        Apply(now);
        Wake();
    }

    private static void Place(Marker marker, int cell, bool monthChanged, bool motion, double now)
    {
        switch (DatePickerMotion.Plan(marker.Cell, cell, !monthChanged))
        {
            case MarkerMove.Hide:
                marker.Presence.Set(0, motion && !monthChanged, now);
                break;
            case MarkerMove.Appear:
                marker.Column.Set(DatePickerMotion.Column(cell), false, now); marker.Row.Set(DatePickerMotion.Row(cell), false, now);
                // One that was not shown fades in where it is; with the month it comes in place, with the grid.
                if (monthChanged || marker.Cell >= 0) marker.Presence.Set(1, false, now);
                else marker.Presence.Set(1, motion, now);
                break;
            case MarkerMove.Glide:
                marker.Column.Set(DatePickerMotion.Column(cell), motion, now); marker.Row.Set(DatePickerMotion.Row(cell), motion, now);
                marker.Presence.Set(1, motion, now);
                break;
        }
        marker.Cell = cell;
    }

    private double CellWidth => _calendar.ActualWidth > 0 ? _calendar.ActualWidth / DatePickerMotion.Columns : Inner / DatePickerMotion.Columns;

    // The day buttons are laid out on whole device pixels (the window's layout rounding): each at its column's offset rounded,
    // all of them the width rounded. A marker at rest takes exactly its button's place and size, so it neither sits a fraction
    // of a pixel off the button's hover nor gets soft edges at 125 or 150 %; on its way between two cells it moves evenly
    // between their places (at: the eased column or row; step: a cell's size; scale: device pixels per DIP).
    private static double OnPixels(double dips, double scale) => scale > 0 && double.IsFinite(scale) ? Math.Round(dips * scale) / scale : dips;
    private static double Along(double at, double step, double scale)
    {
        double low = Math.Floor(at), from = OnPixels(low * step, scale);
        return at <= low ? from : from + (at - low) * (OnPixels((low + 1) * step, scale) - from);
    }

    // The band's half cells take their places once the grid has a size.
    private void LayOut()
    {
        double width = CellWidth, half = width / 2;
        for (int i = 0; i < DatePickerMotion.Cells; i++)
        {
            double x = DatePickerMotion.Column(i) * width, y = DatePickerMotion.Row(i) * CellHeight;
            for (int side = 0; side < 2; side++)
            {
                var rect = _band[2 * i + side];
                // A hair of overlap, so neighbouring halves leave no seam at fractional widths.
                rect.Width = half + .5; rect.Height = CellHeight;
                Canvas.SetLeft(rect, x + side * half - side * .5); Canvas.SetTop(rect, y);
            }
        }
        double faceWidth = OnPixels(width, VisualTreeHelper.GetDpi(this).DpiScaleX);
        foreach (var marker in _markers) marker.Face.Width = faceWidth;
        _fieldMark.Width = Math.Max(0, (_body.ActualWidth > 0 ? _body.ActualWidth : Inner) - FieldGap) / 2;
        Apply(Clock());
    }

    // ----- Frames -----

    // On the panel's shared frame clock (Motion, in MonitorDraw.cs), paced to the display as the monitor's moves are (q-P1.2);
    // the step lets go once the picker sleeps.
    private void Wake()
    {
        _stillFrames = 0;
        if (_hooked || !Moves) return;
        _hooked = true;
        Motion.Start(this, _ => { if (!_hooked) return false; Frame(Clock()); return _hooked; });
    }

    private void Sleep() => _hooked = false;

    // One frame: every value is written where it is now, and the frame callback stops once nothing moves.
    internal void Frame(double now)
    {
        if (_slidePending) { _slidePending = false; _slideAt = now; }
        bool moving = Apply(now);
        if (moving) _stillFrames = 0; else if (++_stillFrames >= 2) Sleep();
    }

    // Writes the values at a moment; returns whether any of them still moves.
    private bool Apply(double now)
    {
        bool moving = false;
        double fieldStep = _fieldMark.Width + FieldGap;
        _fieldShift.X = _fieldAt.At(now) * fieldStep; moving |= _fieldAt.Moving(now);
        for (int f = 0; f < 2; f++) { _fieldValues[f].Opacity = _fieldFade[f].At(now); moving |= _fieldFade[f].Moving(now); }
        for (int i = 0; i < _band.Length; i++) { _band[i].Opacity = _bandFade[i].At(now); moving |= _bandFade[i].Moving(now); }
        double width = CellWidth;
        var dpi = VisualTreeHelper.GetDpi(this);
        double faceWidth = OnPixels(width, dpi.DpiScaleX), faceHeight = OnPixels(CellHeight, dpi.DpiScaleY);
        foreach (var marker in _markers)
        {
            double x = Along(marker.Column.At(now), width, dpi.DpiScaleX), y = Along(marker.Row.At(now), CellHeight, dpi.DpiScaleY), presence = marker.Presence.At(now);
            marker.Shift.X = x; marker.Shift.Y = y;
            marker.Clip.Rect = new Rect(x, y, faceWidth, faceHeight);
            marker.Face.Opacity = marker.Ink.Opacity = presence;
            moving |= marker.Column.Moving(now) || marker.Row.Moving(now) || marker.Presence.Moving(now);
        }
        double open = _messageOpen.At(now);
        // The slot's height is animated on purpose (motion-review F8 leaves it): the message pushes the rows below it down and
        // the face grows with it; a scale or a clip would leave them where they are and the message over them.
        _messageSlot.Height = _messageHeight * open;
        _message.Opacity = open; _messageShift.Y = -MessageRise * (1 - open);
        moving |= _messageOpen.Moving(now);
        _apply.Opacity = _applyFade.At(now); _previous.Opacity = _previousFade.At(now); _next.Opacity = _nextFade.At(now);
        moving |= _applyFade.Moving(now) || _previousFade.Moving(now) || _nextFade.Moving(now);
        if (_slidePending) moving = true;
        else if (!double.IsNaN(_slideAt))
        {
            var frame = DatePickerMotion.Slide((now - _slideAt) * 1000, _slideDirection, Timing);
            if (frame.Done) EndSlide(); else { ApplySlide(frame); moving = true; }
        }
        return moving;
    }

    private void ApplySlide(MonthSlideFrame frame)
    {
        _leavingShift.X = _monthLeavingShift.X = frame.OutX;
        _leaving.Opacity = _monthLeaving.Opacity = frame.OutOpacity;
        _pageShift.X = _monthShift.X = frame.InX;
        _page.Opacity = _monthText.Opacity = frame.InOpacity;
    }

    // A month change ends at once: the picture goes and the grid is in place.
    private void EndSlide()
    {
        _slideAt = double.NaN; _slidePending = false;
        _leaving.Source = null; _leaving.Opacity = 0; _monthLeaving.Opacity = 0; _monthLeaving.Text = "";
        _leavingShift.X = _monthLeavingShift.X = _pageShift.X = _monthShift.X = 0;
        _page.Opacity = _monthText.Opacity = 1;
    }
}
