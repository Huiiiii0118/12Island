using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland;

// The clipboard page: what was copied, newest first, with a search box, the favourites, and the note a user adds to an entry.
// Clicking an entry puts it back on the clipboard, ready to be pasted.
public partial class MainWindow
{
    private TextBox? _clipSearchBox;
    private ScrollViewer? _clipList;
    private Border? _clipToolbar;
    private TextBlock? _clipHint;
    private string _clipWord = "";
    private bool _clipFavoritesOnly;
    private Guid? _clipEditing;
    private string _clipStatus = "";
    // Clears the "copied back" hint a few seconds after it appears, so the line returns to its usual instruction.
    private DispatcherTimer? _clipStatusTimer;

    private void InitializeClipboard()
    {
        _app.ClipboardView += ClipboardViewChanged;
        Closed += (_, _) => _app.ClipboardView -= ClipboardViewChanged;
    }

    // A copy arrived or an entry changed: only a page on screen is drawn again.
    private void ClipboardViewChanged()
    {
        if (IsClipboardPage && _expanded) RenderClipboard();
        else _clipBuilt = "";
    }

    // What the page was built from; a new copy, a search word or a theme builds it again.
    private string _clipBuilt = "";
    private string ClipBuildKey() => string.Join("|", _app.Clips?.Count ?? 0, _app.Clips?.Items.FirstOrDefault()?.Id ?? Guid.Empty,
        _clipWord, _clipFavoritesOnly, _clipEditing, _clipStatus, PanelTheme.Generation, Loc.Language, Math.Round(ContentCanvas.Width), Math.Round(ContentCanvas.Height));

    internal void RenderClipboard()
    {
        var (width, height) = PreparePage(DeckPage.Clipboard);
        if (width <= 0) return;
        var content = ClipboardContent;
        string key = ClipBuildKey();
        if (key == _clipBuilt && content.Children.Count > 0) return;
        // The page is built aside and swapped in whole: a render that throws leaves the page that was there.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(ClipboardToolbar(width, height));
        var list = ClipboardRows(width, height);
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        _clipHint = UI.Line("", 10.5, UI.Tertiary);
        _clipHint.Margin = new Thickness(2, 4, 2, 0);
        _clipHint.Text = _clipStatus.Length > 0 ? _clipStatus : Loc.T("点击条目复制回剪贴板 · 右键添加备注或收藏", "Click an entry to copy it back · right-click for a note or a star");
        Grid.SetRow(_clipHint, 2);
        grid.Children.Add(_clipHint);
        content.Children.Clear();
        content.Children.Add(grid);
        _clipBuilt = key;
    }

    private FrameworkElement ClipboardToolbar(double width, double height)
    {
        var row = new Grid { Height = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(UI.Centered(Loc.T("剪贴板", "Clipboard"), 12, UI.Secondary, FontWeights.SemiBold));
        int count = _app.Clips?.Count ?? 0;
        var amount = UI.Centered(Loc.T($" {count} 条", Loc.Count(count, "item", "items")), 10.5, UI.Tertiary);
        amount.Margin = new Thickness(6, 0, 0, 0);
        label.Children.Add(amount);
        row.Children.Add(label);

        // The search box: the theme's own fill, no border, and the caret in the text color.
        string placeholder = Loc.T("搜索内容或备注", "Search text or notes");
        var box = new TextBox
        {
            Height = 24, Margin = new Thickness(14, 1, 12, 1), VerticalContentAlignment = VerticalAlignment.Center,
            Background = UI.Brush(UI.Fill), Foreground = UI.Brush(UI.Primary), BorderThickness = new Thickness(0),
            CaretBrush = UI.Brush(UI.Primary), FontFamily = UI.TextFont, FontSize = 12, Padding = new Thickness(8, 0, 8, 0),
            Text = _clipWord, ToolTip = placeholder
        };
        box.Template = TextBoxTemplate();
        Grid.SetColumn(box, 1);
        box.TextChanged += (_, _) =>
        {
            if (box.Text == _clipWord) return;
            _clipWord = box.Text; _clipStatus = ""; _clipBuilt = "";
            RenderClipboard();
            // The box is built again with the list; the caret goes back to where it was.
            Dispatcher.BeginInvoke(() =>
            {
                if (_clipSearchBox is null) return;
                _clipSearchBox.Focus(); _clipSearchBox.CaretIndex = _clipSearchBox.Text.Length;
            });
        };
        _clipSearchBox = box;
        if (_clipWord.Length == 0)
        {
            var hint = UI.Line(placeholder, 11.5, UI.Tertiary);
            hint.IsHitTestVisible = false; hint.Margin = new Thickness(10, 0, 0, 0); hint.VerticalAlignment = VerticalAlignment.Center;
            var host = new Grid(); host.Children.Add(box); host.Children.Add(hint);
            box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(host, 1);
            row.Children.Add(host);
        }
        else row.Children.Add(box);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var current = Loc.T("全部", "All");
        var starred = Loc.T("收藏", "Starred");
        var segments = Segmented(
        [
            new SegmentItem(current, current, !_clipFavoritesOnly, () => { _clipFavoritesOnly = false; _clipBuilt = ""; RenderClipboard(); }),
            new SegmentItem(starred, starred, _clipFavoritesOnly, () => { _clipFavoritesOnly = true; _clipBuilt = ""; RenderClipboard(); })
        ], 11, 20, 8, centerText: true);
        right.Children.Add(segments);
        var clear = IconText(Loc.T("清空", "Clear"));
        clear.Margin = new Thickness(10, 0, 0, 0);
        clear.Click += (_, _) =>
        {
            _app.Clips?.Clear(favoritesToo: false);
            _clipStatus = Loc.T("已清空（收藏的条目保留）", "Cleared; starred entries kept");
            _clipBuilt = "";
            RenderClipboard();
        };
        right.Children.Add(clear);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        _clipToolbar = new Border { Child = row, Margin = new Thickness(2, 0, 2, 6) };
        return _clipToolbar;
    }

    // The list of entries as a scrolling column, each row a line of text with its time, a star and a way to remove it.
    private FrameworkElement ClipboardRows(double width, double height)
    {
        var panel = new StackPanel();
        var entries = (_app.Clips?.Search(_clipWord, _clipFavoritesOnly) ?? []).ToList();
        if (entries.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            empty.Children.Add(UI.Fixed(_app.Clips is null ? Loc.T("剪贴板记录已关闭", "Clipboard history is off") : Loc.T("还没有记录", "Nothing copied yet"), 12.5, 20, UI.Secondary));
            empty.Children.Add(UI.Fixed(_app.Clips is null ? Loc.T("可在设置中打开", "Turn it on in settings") : Loc.T("复制任何内容后会出现在这里", "Anything copied shows up here"), 10.5, 18, UI.Tertiary));
            return empty;
        }
        foreach (var entry in entries) panel.Children.Add(ClipboardRow(entry, width));
        var scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0), Focusable = false
        };
        scroll.PreviewMouseWheel += (_, e) => { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; };
        _clipList = scroll;
        return scroll;
    }

    private FrameworkElement ClipboardRow(ClipEntry entry, double width)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        var kind = UI.Centered(entry.Kind switch { ClipKind.Image => "▣", ClipKind.Files => "❏", _ => "T" }, 12,
            entry.Kind == ClipKind.Image ? PanelTheme.Hex("accent") : UI.Secondary);
        kind.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(kind);

        var text = new StackPanel { Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        var title = UI.Line(entry.FirstLine, 12.5, UI.Primary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        string second = entry.Preview;
        if (second.Length > 0)
        {
            var preview = UI.Line(second, 10.5, UI.Tertiary);
            preview.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(preview);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var time = UI.Centered(entry.At.LocalDateTime.Date == DateTime.Today ? entry.At.ToString("HH:mm") : entry.At.ToString("MM-dd"), 10.5, UI.Tertiary);
        time.Margin = new Thickness(6, 0, 8, 0);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var star = SymbolButton(entry.Favorite ? "★" : "☆", entry.Favorite ? PanelTheme.Hex("accent") : UI.Tertiary,
            entry.Favorite ? Loc.T("取消收藏", "Remove star") : Loc.T("收藏", "Star"));
        star.Click += (_, e) => { e.Handled = true; _app.Clips?.SetFavorite(entry.Id, !entry.Favorite); _clipBuilt = ""; RenderClipboard(); };
        Grid.SetColumn(star, 3);
        grid.Children.Add(star);

        var remove = SymbolButton("✕", UI.Tertiary, Loc.T("删除", "Delete"));
        remove.Click += (_, e) => { e.Handled = true; _app.Clips?.Remove(entry.Id); _clipBuilt = ""; RenderClipboard(); };
        Grid.SetColumn(remove, 4);
        grid.Children.Add(remove);

        var row = new Border
        {
            Child = grid, Background = Brushes.Transparent, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(4, 0, 2, 0),
            Tag = entry, ContextMenu = ClipboardMenu(entry)
        };
        row.MouseEnter += (_, _) => row.Background = UI.Brush(UI.Fill);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is Button) return; CopyEntry(entry); };
        return row;
    }

    private ContextMenu ClipboardMenu(ClipEntry entry)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        void Item(string text, Action action)
        {
            var item = new MenuItem { Header = text, Style = (Style)FindResource("MediaSourceMenuItem") };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item(Loc.T("复制", "Copy"), () => CopyEntry(entry));
        Item(entry.Favorite ? Loc.T("取消收藏", "Remove star") : Loc.T("收藏", "Star"), () => { _app.Clips?.SetFavorite(entry.Id, !entry.Favorite); _clipBuilt = ""; RenderClipboard(); });
        Item(entry.HasNote ? Loc.T("修改备注", "Edit note") : Loc.T("添加备注", "Add a note"), () => { _clipEditing = entry.Id; _clipBuilt = ""; RenderClipboard(); StartNoteEdit(); });
        menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
        Item(Loc.T("删除", "Delete"), () => { _app.Clips?.Remove(entry.Id); _clipBuilt = ""; RenderClipboard(); });
        return menu;
    }

    // The note editor replaces the list's first line while a note is written, so nothing jumps when it opens.
    private void StartNoteEdit() => Dispatcher.BeginInvoke(() =>
    {
        if (_clipEditing is not { } id || _app.Clips?.Items.FirstOrDefault(item => item.Id == id) is not { } entry) return;
        var content = ClipboardContent;
        var editor = new Grid { Height = 30, Margin = new Thickness(2, 0, 2, 6) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Text = entry.Note, FontFamily = UI.TextFont, FontSize = 12, Height = 26, VerticalContentAlignment = VerticalAlignment.Center,
            Background = UI.Brush(UI.Fill), Foreground = UI.Brush(UI.Primary), BorderThickness = new Thickness(0), CaretBrush = UI.Brush(UI.Primary),
            Padding = new Thickness(8, 0, 8, 0), ToolTip = Loc.T("给这条记录加备注，搜索时也能找到", "A note for this entry; the search finds it too")
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        void Save()
        {
            _app.Clips?.SetNote(id, box.Text);
            _clipEditing = null; _clipBuilt = ""; _clipStatus = Loc.T("备注已保存", "Note saved"); RenderClipboard();
        }
        void Cancel() { _clipEditing = null; _clipBuilt = ""; RenderClipboard(); }
        editor.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var save = IconText(Loc.T("保存", "Save")); save.Click += (_, _) => Save();
        var cancel = IconText(Loc.T("取消", "Cancel")); cancel.Margin = new Thickness(6, 0, 0, 0); cancel.Click += (_, _) => Cancel();
        actions.Children.Add(save); actions.Children.Add(cancel);
        Grid.SetColumn(actions, 1);
        editor.Children.Add(actions);
        Grid.SetRow(editor, 2);
        if (content.Children.OfType<Grid>().FirstOrDefault() is { } host)
        {
            // The editor takes the hint line's place, under the list.
            if (_clipHint is not null) host.Children.Remove(_clipHint);
            host.Children.Add(editor);
        }
        box.Focus(); box.CaretIndex = box.Text.Length;
    });

    private void CopyEntry(ClipEntry entry)
    {
        _app.CopyToClipboard(entry);
        _app.Clips?.Touch(entry.Id);
        _clipStatus = entry.Kind == ClipKind.Image ? Loc.T("图片已复制，可以直接粘贴", "Picture copied; paste it anywhere")
            : entry.Kind == ClipKind.Files ? Loc.T("文件已复制，可以直接粘贴", "Files copied; paste them anywhere")
            : Loc.T("已复制回剪贴板", "Copied back to the clipboard");
        _clipBuilt = "";
        RenderClipboard();
        // The copied hint says its piece and steps aside five seconds later, so the line reads the usual instruction again.
        _clipStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clipStatusTimer.Tick -= ClipStatusExpired;
        _clipStatusTimer.Tick += ClipStatusExpired;
        _clipStatusTimer.Stop();
        _clipStatusTimer.Start();
    }
    private void ClipStatusExpired(object? _, EventArgs _e)
    {
        _clipStatusTimer?.Stop();
        _clipStatus = "";
        if (IsClipboardPage && _expanded) { _clipBuilt = ""; RenderClipboard(); }
    }

    // A small button with a symbol, the size of a line of text; it does not take the row's click.
    private static Button SymbolButton(string symbol, string color, string tip) => new()
    {
        Content = UI.Centered(symbol, 12.5, color), Width = 20, Height = 24, Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = tip,
        Focusable = false, Template = FlatButtonTemplate()
    };

    private static ControlTemplate? _flatButton;
    internal static ControlTemplate FlatButtonTemplate() => _flatButton ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
        + "<Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");

    private static ControlTemplate? _textBoxTemplate;
    internal static ControlTemplate TextBoxTemplate() => _textBoxTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='TextBox' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
        + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>"
        + "<Border Background='{TemplateBinding Background}' CornerRadius='6'><ScrollViewer x:Name='PART_ContentHost' Margin='0' VerticalAlignment='Center'/></Border></ControlTemplate>");

    // A text button in the page's own style: no chrome, the label in the secondary color.
    private static Button IconText(string text) => new()
    {
        Content = UI.Centered(text, 11, UI.Secondary), Height = 20, Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false, Template = FlatButtonTemplate()
    };
}
