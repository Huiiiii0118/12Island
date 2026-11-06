using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

// The conversion page: files dropped or picked, the format each one becomes, and one button that converts them one after
// another. Pictures, documents, spreadsheets and text are converted by the panel itself; sound and video need FFmpeg, and the
// office formats LibreOffice.
public partial class MainWindow
{
    private sealed class ConvertJob
    {
        public string Path = "";
        public ConvertTarget? Target;
        public string Status = "";
        public string? Result;
        public bool Failed;
        public bool Done;
        // Whether this file takes part when "开始转换" runs; the box starts ticked so the old "convert everything" behaviour holds
        // until the user clears it.
        public bool Selected = true;
    }

    private readonly List<ConvertJob> _convertJobs = [];
    private FileConverter? _converter;
    private bool _converting;
    private CancellationTokenSource? _convertCancel;
    private string _convertBuilt = "";
    private string _convertMessage = "";
    private TextBlock? _convertCountLabel;

    private void InitializeConvert()
    {
        ConvertContent.AllowDrop = true;
        ConvertContent.DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        ConvertContent.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { AddConvertFiles(files); e.Handled = true; }
        };
    }

    private FileConverter Converter => _converter ??= NewConverter();
    private FileConverter NewConverter() => new(Settings);

    private void AddConvertFiles(IEnumerable<string> files)
    {
        foreach (var path in files)
        {
            if (!File.Exists(path) || _convertJobs.Any(job => string.Equals(job.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var targets = ConvertFormats.TargetsFor(path, Converter.HasFfmpeg, Converter.HasOffice);
            _convertJobs.Add(new ConvertJob { Path = path, Target = targets.FirstOrDefault() });
        }
        _convertBuilt = "";
        RenderConvert();
    }

    private string ConvertBuildKey() => string.Join("|", _convertJobs.Count,
        string.Join(";", _convertJobs.Select(job => job.Path + job.Target?.Extension + job.Selected + job.Status + job.Done + job.Failed)),
        _converting, _convertMessage, Settings.ConvertOutputFolder, Converter.HasFfmpeg, Converter.HasOffice,
        PanelTheme.Generation, Loc.Language, Math.Round(ContentCanvas.Width), Math.Round(ContentCanvas.Height));

    internal void RenderConvert()
    {
        var (width, height) = PreparePage(DeckPage.Convert);
        if (width <= 0) return;
        string key = ConvertBuildKey();
        if (key == _convertBuilt && ConvertContent.Children.Count > 0) return;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(ConvertToolbar());
        var body = _convertJobs.Count == 0 ? ConvertDropZone(height) : ConvertList();
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        var status = UI.Line(_convertMessage.Length > 0 ? _convertMessage : ConvertHint(), 10.5, UI.Tertiary);
        status.Margin = new Thickness(2, 4, 2, 0);
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        ConvertContent.Children.Clear();
        ConvertContent.Children.Add(grid);
        _convertBuilt = key;
    }

    // What the page can do on this machine, in one line.
    private string ConvertHint()
    {
        var parts = new List<string> { Loc.T("图片 · 文档 · 表格 · 文本 · 音视频 · PDF（全部本机完成）", "Pictures, documents, spreadsheets, text, sound, video, PDF (all built in)") };
        if (Converter.HasOffice) parts.Add("LibreOffice ✓");
        return string.Join(" · ", parts);
    }

    private FrameworkElement ConvertToolbar()
    {
        var row = new Grid { Height = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(UI.Centered(Loc.T("格式转换", "Convert"), 12, UI.Secondary, FontWeights.SemiBold));
        if (_convertJobs.Count > 0)
        {
            int selected = _convertJobs.Count(job => job.Selected);
            var amount = UI.Centered(selected == _convertJobs.Count
                ? Loc.T($" {_convertJobs.Count} 个文件", Loc.Count(_convertJobs.Count, "file", "files"))
                : Loc.T($" 已选 {selected}/{_convertJobs.Count}", $" {selected}/{_convertJobs.Count} selected"), 10.5, UI.Tertiary);
            amount.Margin = new Thickness(6, 0, 0, 0);
            label.Children.Add(amount);
            _convertCountLabel = amount;
        }
        else _convertCountLabel = null;
        row.Children.Add(label);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var add = IconText(Loc.T("添加文件", "Add files"));
        add.Click += (_, _) => PickConvertFiles();
        right.Children.Add(add);
        if (_convertJobs.Count > 0)
        {
            var all = IconText(_convertJobs.All(job => job.Selected) ? Loc.T("全不选", "None") : Loc.T("全选", "All"));
            all.Margin = new Thickness(8, 0, 0, 0);
            all.Click += (_, _) =>
            {
                bool turnOn = !_convertJobs.All(job => job.Selected);
                foreach (var job in _convertJobs) job.Selected = turnOn;
                _convertBuilt = ""; RenderConvert();
            };
            right.Children.Add(all);
            var clear = IconText(Loc.T("移除全部", "Remove all"));
            clear.Margin = new Thickness(8, 0, 0, 0);
            clear.Click += (_, _) => { _convertJobs.Clear(); _convertMessage = ""; _convertBuilt = ""; RenderConvert(); };
            right.Children.Add(clear);
            var start = IconText(_converting ? Loc.T("取消", "Cancel") : Loc.T("开始转换", "Convert"));
            start.Foreground = UI.Brush(_converting ? PanelTheme.Hex("danger") : UI.Accent);
            start.Margin = new Thickness(10, 0, 0, 0);
            start.Click += (_, _) => { if (_converting) _convertCancel?.Cancel(); else _ = StartConvertAsync(); };
            right.Children.Add(start);
        }
        Grid.SetColumn(right, 1); Grid.SetColumnSpan(right, 2);
        row.Children.Add(right);
        return new Border { Child = row, Margin = new Thickness(2, 0, 2, 6) };
    }

    // Opens the page with files already in it: what "用 12 island 转换" (a file's context menu, or --convert on the command line) does.
    internal void OpenConvert(IEnumerable<string> files)
    {
        AddConvertFiles(files);
        SelectPage(DeckPage.Convert);
        if (IsLoaded) SetExpanded(true);
    }

    private void PickConvertFiles()    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, Filter = ConvertFormats.Filter, Title = Loc.T("选择要转换的文件", "Choose files to convert"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) AddConvertFiles(dialog.FileNames);
    }

    // Before anything is dropped: where files go and what the page does.
    private FrameworkElement ConvertDropZone(double height)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(UI.Fixed(Loc.T("把文件拖到这里", "Drop files here"), 13, 22, UI.Secondary, FontWeights.SemiBold));
        panel.Children.Add(UI.Fixed(Loc.T("或点击右上角「添加文件」", "or use Add files at the top right"), 10.5, 18, UI.Tertiary));
        var hint = UI.Fixed(ConvertHint(), 10, 16, UI.Tertiary);
        hint.Margin = new Thickness(0, 10, 0, 0);
        hint.TextAlignment = TextAlignment.Center;
        panel.Children.Add(hint);
        var zone = new Border
        {
            Child = panel, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PanelTheme.Corner(14)), Background = UI.Brush(UI.Fill), Cursor = Cursors.Hand
        };
        zone.MouseLeftButtonUp += (_, _) => PickConvertFiles();
        return new Grid { Children = { zone } };
    }

    private FrameworkElement ConvertList()
    {
        var panel = new StackPanel();
        foreach (var job in _convertJobs) panel.Children.Add(ConvertRow(job));
        return PageScroll(panel);
    }

    private FrameworkElement ConvertRow(ConvertJob job)
    {
        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        // Per-row opt-in: only the files the user ticks are converted when "开始转换" runs.
        var check = new CheckBox
        {
            IsChecked = job.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Focusable = false, Cursor = Cursors.Hand, IsEnabled = !_converting
        };
        check.Checked += (_, _) => { job.Selected = true; _convertBuilt = ""; RenderConvert(); };
        check.Unchecked += (_, _) => { job.Selected = false; _convertBuilt = ""; RenderConvert(); };
        grid.Children.Add(check);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
        var name = UI.Line(Path.GetFileName(job.Path), 12.5, UI.Primary);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        string second = job.Status.Length > 0 ? job.Status
            : $"{ConvertFormats.FamilyNameOf(job.Path)} · {Size(new FileInfo(job.Path).Length)}";
        var detail = UI.Line(second, 10.5, job.Failed ? PanelTheme.Hex("danger") : job.Done ? PanelTheme.Hex("accent") : UI.Tertiary);
        detail.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // The format the file becomes: clicking it offers what this file may become on this machine.
        var target = job.Target?.Extension.TrimStart('.').ToUpperInvariant() ?? Loc.T("无可用格式", "No format");
        var chip = new Button
        {
            Content = UI.Centered(target, 11, job.Target is null ? UI.Tertiary : UI.Secondary), Height = 20, Padding = new Thickness(8, 0, 8, 0),
            Background = UI.Brush(UI.Fill), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
            Template = RoundButtonTemplate(), IsEnabled = !_converting
        };
        chip.Click += (_, _) => TargetMenu(job, chip);
        chip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chip, 2);
        grid.Children.Add(chip);

        var remove = SymbolButton("✕", UI.Tertiary, Loc.T("移除", "Remove"));
        remove.Click += (_, _) => { _convertJobs.Remove(job); _convertBuilt = ""; RenderConvert(); };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        var row = PageRow(grid, 34);
        if (job.Result is not null)
            row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is Button or CheckBox) return; RevealFile(job.Result!); };
        return row;
    }

    private void TargetMenu(ConvertJob job, FrameworkElement owner)
    {
        var targets = ConvertFormats.TargetsFor(job.Path, Converter.HasFfmpeg, Converter.HasOffice);
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu"), PlacementTarget = owner };
        if (targets.Count == 0)
        {
            var missing = ConvertFormats.Missing(job.Path);
            menu.Items.Add(new MenuItem
            {
                Header = missing.NeedsFfmpeg ? Loc.T("需要 FFmpeg", "FFmpeg is needed") : Loc.T("需要 LibreOffice", "LibreOffice is needed"),
                Style = (Style)FindResource("MediaSourceMenuItem"), IsEnabled = false
            });
        }
        foreach (var target in targets)
        {
            var item = new MenuItem
            {
                Header = target.Extension.TrimStart('.').ToUpperInvariant(), Style = (Style)FindResource("MediaSourceMenuItem"),
                IsChecked = job.Target?.Extension == target.Extension
            };
            item.Click += (_, _) => { job.Target = target; job.Status = ""; job.Done = false; job.Failed = false; _convertBuilt = ""; RenderConvert(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // Converts every file that has a format, one after another, and reports each result under its name.
    private async Task StartConvertAsync()
    {
        if (_converting) return;
        // Only the files the user ticked take part; nothing happens if none are chosen.
        var selectedJobs = _convertJobs.Where(job => job.Selected && job.Target is not null).ToList();
        if (selectedJobs.Count == 0)
        {
            _convertMessage = _convertJobs.Any(job => job.Selected)
                ? Loc.T("勾选项无可用格式", "No usable format for the ticked files")
                : Loc.T("请先勾选要转换的项目", "Tick the files you want to convert first");
            _convertBuilt = ""; RenderConvert();
            return;
        }
        _converting = true; _convertMessage = ""; _convertBuilt = "";
        _convertCancel = new CancellationTokenSource();
        _converter = NewConverter();
        RenderConvert();
        int done = 0, failed = 0;
        try
        {
            foreach (var job in selectedJobs)
            {
                if (_convertCancel.IsCancellationRequested) break;
                job.Status = Loc.T("转换中…", "Converting…"); job.Failed = false; job.Done = false; _convertBuilt = ""; RenderConvert();
                try
                {
                    var target = job.Target!;
                    string output = await Task.Run(() => _converter.ConvertAsync(job.Path, target, Settings.ConvertOutputFolder, null, _convertCancel.Token), _convertCancel.Token);
                    job.Result = output; job.Done = true; job.Status = Loc.T("已保存 " + Path.GetFileName(output), "Saved " + Path.GetFileName(output));
                    done++;
                }
                catch (OperationCanceledException) { job.Status = Loc.T("已取消", "Cancelled"); break; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                    or NotSupportedException or System.Runtime.InteropServices.COMException or FormatException)
                {
                    job.Failed = true; job.Status = ex.Message; failed++;
                }
                _convertBuilt = ""; RenderConvert();
            }
        }
        finally
        {
            _converting = false;
            _convertCancel?.Dispose(); _convertCancel = null;
            _convertMessage = failed > 0 ? Loc.T($"完成 {done} 个，失败 {failed} 个", $"{done} done, {failed} failed")
                : done > 0 ? Loc.T($"完成 {done} 个文件", $"{done} file(s) done") : "";
            _convertBuilt = "";
            RenderConvert();
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };

    // Shows the result in the folder it was written to, with the file chosen.
    private static void RevealFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    internal static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static ControlTemplate? _roundButton;
    private static ControlTemplate RoundButtonTemplate() => _roundButton ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
        + "<Border Background='{TemplateBinding Background}' CornerRadius='5'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");
}
