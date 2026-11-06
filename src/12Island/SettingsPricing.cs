using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private sealed record PriceRow(ModelPrice Model)
    {
        public string Name => Model.Model;
        public string Input => Format(Model.Prices.Input);
        public string Output => Format(Model.Prices.Output);
        public string Read => Format(Model.Prices.CacheRead);
        public string Write => Format(Model.Prices.CacheWrite);
        private static string Format(decimal? price) => price is null ? "—" : (price.Value * 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
    }
    private DataGrid? _priceTable;
    private TextBox? _priceSearch;
    private TextBlock? _priceCount;
    private TextBlock? _priceUpdated;
    // Everything on the page except the table: title, description, toolbar, search and the footer.
    private const double PriceTableReserve = 358;
    private void ModelPrices()
    {
        // Sits directly under the page title, closer to it than the usual gap below a title.
        var description=TextLine(Loc.T("面板和模型明细中的费用按这里的单价估算。价格每天从 OpenRouter 更新，手动添加的价格优先。",
            "Costs in the panel and model details are estimated with these unit prices. Prices are updated daily from OpenRouter; prices added by hand take precedence."),12,TextSecondary);
        description.TextWrapping=TextWrapping.Wrap;description.Margin=new Thickness(0,-8,0,14);_content.Children.Add(description);
        var toolbar=new DockPanel {Margin=new Thickness(0,0,0,20)};
        var refresh=ActionButton(Loc.T("更新价格", "Update prices"),()=>{});refresh.BorderBrush=UI.Brush(_palette.Border);refresh.Background=UI.Brush(_palette.Input);
        AutomationProperties.SetName(refresh,Loc.T("更新价格", "Update prices"));DockPanel.SetDock(refresh,Dock.Right);toolbar.Children.Add(refresh);
        refresh.Click+=async(_,_)=>
        {
            refresh.IsEnabled=false;refresh.Content=Loc.T("正在更新…", "Updating…");
            try {await _app.Prices.RefreshAsync(true,_app.Lifetime.Token);foreach(var deck in _app.Decks)deck.RenderUsage();RefreshPriceRows();}
            catch(OperationCanceledException) { }
            finally {refresh.IsEnabled=true;refresh.Content=Loc.T("更新价格", "Update prices");}
        };
        _priceUpdated=TextLine("",11,TextTertiary);_priceUpdated.VerticalAlignment=VerticalAlignment.Center;toolbar.Children.Add(_priceUpdated);_content.Children.Add(toolbar);
        _priceSearch=new TextBox {Style=(Style)FindResource("SettingsField"),MaxLength=200,Margin=new Thickness(0,0,0,16)};
        AutomationProperties.SetName(_priceSearch,Loc.T("搜索模型", "Search models"));_priceSearch.ToolTip=Loc.T("按模型 ID 搜索", "Search by model ID");
        var search=new DockPanel();var searchLabel=TextLine(Loc.T("搜索模型", "Search models"),12,TextSecondary);searchLabel.Margin=new Thickness(0,8,16,0);DockPanel.SetDock(searchLabel,Dock.Left);search.Children.Add(searchLabel);search.Children.Add(_priceSearch);_content.Children.Add(search);
        _priceTable=new DataGrid {Style=(Style)FindResource("SettingsPriceTable"),Height=Math.Max(220,ActualHeight-PriceTableReserve),FontSize=12,FontFamily=UI.PanelFont};
        _priceTable.Resources[typeof(ScrollViewer)]=new Style(typeof(ScrollViewer));
        AutomationProperties.SetName(_priceTable,Loc.T("模型单价", "Model unit prices"));
        var numericHeader=new Style(typeof(DataGridColumnHeader),_priceTable.ColumnHeaderStyle);
        numericHeader.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Right));
        // English price headings are longer, so the model column gives some of its width to the four price columns.
        double modelWeight=Loc.IsEnglish?2.4:2.8;
        foreach(var column in new[]{(Loc.T("模型", "Model"),nameof(PriceRow.Name),modelWeight),(Loc.T("输入", "Input"),nameof(PriceRow.Input),1d),(Loc.T("输出", "Output"),nameof(PriceRow.Output),1d),(Loc.T("缓存读取", "Cache read"),nameof(PriceRow.Read),1d),(Loc.T("缓存写入", "Cache write"),nameof(PriceRow.Write),1d)})
        {
            var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));
            // A model ID cut off by its ellipsis shows whole on hover; whether it is cut is checked when the tooltip would open,
            // so a resized column or a reused row is measured afresh. Prices have no tooltip.
            if(column.Item2==nameof(PriceRow.Name)){style.Setters.Add(new Setter(TextBlock.ToolTipProperty,new Binding(column.Item2)));
                style.Setters.Add(new EventSetter(ToolTipOpeningEvent,new ToolTipEventHandler((sender,e)=>{if(sender is TextBlock block&&!UI.IsCut(block))e.Handled=true;})));}
            if(column.Item2!=nameof(PriceRow.Name))style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,TextAlignment.Right));
            // The application's TextBlock style wraps; a heading stays on one line.
            _priceTable.Columns.Add(new DataGridTextColumn {Header=new TextBlock {Text=column.Item1,TextWrapping=TextWrapping.NoWrap},Binding=new Binding(column.Item2),Width=new DataGridLength(column.Item3,DataGridLengthUnitType.Star),ElementStyle=style,
                HeaderStyle=column.Item2==nameof(PriceRow.Name)?_priceTable.ColumnHeaderStyle:numericHeader});
        }
        var factory=new FrameworkElementFactory(typeof(Button));factory.SetValue(StyleProperty,FindResource("SettingsButton"));factory.SetValue(ContentControl.ContentProperty,Loc.T("编辑", "Edit"));factory.SetValue(FontSizeProperty,11d);factory.SetValue(Control.PaddingProperty,new Thickness(4,5,4,5));
        factory.SetValue(AutomationProperties.NameProperty,Loc.T("编辑模型价格", "Edit model price"));
        factory.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,_)=>{if(((FrameworkElement)sender).DataContext is PriceRow item)CreatePriceEditor(item.Model).ShowDialog();}));
        _priceTable.Columns.Add(new DataGridTemplateColumn {Width=50,CellTemplate=new DataTemplate {VisualTree=factory}});
        _priceTable.SizeChanged+=(_,_)=>
        {
            var available=Math.Max(400,_priceTable.ActualWidth-64);
            var weights=new[]{modelWeight,1,1,1,1};
            for(int i=0;i<weights.Length;i++)_priceTable.Columns[i].Width=new DataGridLength(available*weights[i]/weights.Sum());
        };
        _priceTable.MouseDoubleClick+=(_,e)=>{if(_priceTable.SelectedItem is PriceRow item&&e.OriginalSource is FrameworkElement element&&FindVisualParent<DataGridRow>(element) is not null)CreatePriceEditor(item.Model).ShowDialog();};
        _content.Children.Add(_priceTable);
        var tail=new DockPanel {Margin=new Thickness(0,16,0,0),HorizontalAlignment=HorizontalAlignment.Left};
        var add=ActionButton(Loc.T("+  添加模型", "+  Add model"),()=>CreatePriceEditor(null).ShowDialog());add.BorderBrush=UI.Brush(_palette.Border);add.Background=UI.Brush(_palette.Input);AutomationProperties.SetName(add,Loc.T("添加模型", "Add model"));DockPanel.SetDock(add,Dock.Left);tail.Children.Add(add);
        var details=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,2)};
        var units=TextLine(Loc.T("单价单位：美元 / 百万 Token", "Unit prices in US dollars per million tokens"),11,TextTertiary);units.Margin=new Thickness(16,0,16,0);details.Children.Add(units);
        _priceCount=TextLine("",11,TextTertiary);details.Children.Add(_priceCount);tail.Children.Add(details);_content.Children.Add(tail);
        var table=_priceTable;
        table.Loaded+=(_,_)=>
        {
            if(FindVisualChild<ScrollContentPresenter>(table) is {} viewport)
                tail.SetBinding(WidthProperty,new Binding(nameof(ActualWidth)){Source=viewport});
        };
        _priceSearch.TextChanged+=(_,_)=>RefreshPriceRows();RefreshPriceRows();
    }
    private static T? FindVisualChild<T>(DependencyObject element) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)
        {
            var child=VisualTreeHelper.GetChild(element,i);
            if(child is T match)return match;
            if(FindVisualChild<T>(child) is {} found)return found;
        }
        return null;
    }
    private static T? FindVisualParent<T>(DependencyObject element) where T:DependencyObject
    {for(var item=element;item is not null;item=VisualTreeHelper.GetParent(item))if(item is T match)return match;return null;}
    internal void RefreshPriceRows()
    {
        if(_page!=4||_priceTable is null)return;
        var query=_priceSearch?.Text.Trim()??"";
        var models=_app.Prices.Models.Where(m=>m.Model.Contains(query,StringComparison.OrdinalIgnoreCase)).Select(m=>new PriceRow(m)).ToArray();
        _priceTable.ItemsSource=models;
        if(_priceCount is not null)_priceCount.Text=query.Length==0?Loc.T($"{models.Length} 个模型", Loc.Count(models.Length, "model", "models")):Loc.T($"{models.Length} / {_app.Prices.ModelCount} 个模型", $"{models.Length} / " + Loc.Count(_app.Prices.ModelCount, "model", "models"));
        if(_priceUpdated is not null)_priceUpdated.Text=_app.Prices.LastError??(_app.Prices.UpdatedAt is {} at?Loc.T($"上次更新 {at.LocalDateTime:MM-dd HH:mm}", $"Last updated {at.LocalDateTime:MM-dd HH:mm}"):Loc.T("尚未更新价格", "Prices not updated yet"));
        if(_app.Prices.ManualLoadWarning is {} warning)SaveStatus(warning,true);
    }
    internal Window CreatePriceEditor(ModelPrice? model)
    {
        bool adding=model is null;
        var dialog=new Window {Title=adding?Loc.T("添加模型", "Add model"):Loc.T("编辑模型价格", "Edit model price"),Owner=this,Width=500,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=UI.Brush(Surface),Foreground=UI.Brush(TextPrimary),FontFamily=UI.PanelFont,ShowInTaskbar=false};
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/12Island;component/SettingsResources.xaml",UriKind.Relative)});_palette.Apply(dialog.Resources);
        void UpdateDialogTheme()
        {
            _palette.Apply(dialog.Resources);dialog.Background=UI.Brush(Surface);dialog.Foreground=UI.Brush(TextPrimary);
            Native.SettingsChrome.SetTheme(dialog,_palette.Dark);
        }
        PaletteChanged+=UpdateDialogTheme;dialog.Closed+=(_,_)=>PaletteChanged-=UpdateDialogTheme;
        Native.SettingsChrome.Apply(dialog);dialog.ResizeMode=ResizeMode.NoResize;
        dialog.SourceInitialized+=(_,_)=>Native.SettingsChrome.SetTheme(dialog,_palette.Dark);
        // The title and the button stay put; on a screen too short for the fields, the fields scroll between them.
        var panel=new DockPanel {Margin=new Thickness(26,18,26,24)};var body=new StackPanel();
        var head=new DockPanel {Margin=new Thickness(0,0,0,22)};
        var close=ActionButton("",dialog.Close);close.Content=CaptionIcon("close");close.Width=30;close.Height=30;close.Padding=new Thickness(0);close.ToolTip=Loc.T("关闭", "Close");AutomationProperties.SetName(close,Loc.T("关闭价格编辑", "Close price editor"));WindowChromeVisible(close);DockPanel.SetDock(close,Dock.Right);head.Children.Add(close);
        head.Children.Add(TextLine(dialog.Title,20,TextPrimary,FontWeights.SemiBold));DockPanel.SetDock(head,Dock.Top);panel.Children.Add(head);
        body.Children.Add(TextLine(Loc.T("模型 ID", "Model ID"),12,TextSecondary));
        var id=new TextBox {Style=(Style)dialog.FindResource("SettingsField"),Text=model?.Model??"",MaxLength=200,IsReadOnly=!adding,Margin=new Thickness(0,7,0,18)};
        AutomationProperties.SetName(id,Loc.T("模型 ID", "Model ID"));body.Children.Add(id);
        var units=TextLine(Loc.T("美元 / 百万 Token", "US dollars per million tokens"),11,TextTertiary);units.Margin=new Thickness(0,0,0,14);body.Children.Add(units);
        var fields=new Dictionary<string,TextBox>();var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
        var values=new (string Name,decimal? Value)[]{(Loc.T("输入", "Input"),model?.Prices.Input),(Loc.T("输出", "Output"),model?.Prices.Output),(Loc.T("缓存读取", "Cache read"),model?.Prices.CacheRead),
            (Loc.T("缓存写入", "Cache write"),model?.Prices.CacheWrite),(Loc.T("缓存写入（1 小时）", "Cache write (1 hour)"),model?.Prices.CacheWriteHour)};
        for(int i=0;i<values.Length;i++)
        {
            if(i%2==0)grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var cell=new StackPanel {Margin=new Thickness(i%2==0?0:9,0,i%2==0?9:0,16)};cell.Children.Add(TextLine(values[i].Name,12,TextSecondary));
            var input=new TextBox {Style=(Style)dialog.FindResource("SettingsField"),Text=values[i].Value is {} v?(v*1_000_000m).ToString("0.############################",CultureInfo.InvariantCulture):"",Margin=new Thickness(0,7,0,0),MaxLength=40};
            AutomationProperties.SetName(input,Loc.T(values[i].Name+"单价", values[i].Name+" price"));fields.Add(values[i].Name,input);
            if(i<2)cell.Children.Add(input);
            else
            {
                // Cache prices may stay blank; the hint sits where typed text starts (border 1 + padding 10 + text inset 2).
                var hint=TextLine(i switch {2=>Loc.T("留空按 0 计算", "Empty counts as 0"),3=>Loc.T("留空按输入价格计算", "Empty uses the input price"),_=>Loc.T("留空按缓存写入价格计算", "Empty uses the cache write price")},13,TextTertiary);hint.IsHitTestVisible=false;hint.VerticalAlignment=VerticalAlignment.Center;hint.Margin=new Thickness(13,7,0,0);
                void Hint()=>hint.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
                input.TextChanged+=(_,_)=>Hint();Hint();
                var overlay=new Grid();overlay.Children.Add(input);overlay.Children.Add(hint);cell.Children.Add(overlay);
            }Grid.SetColumn(cell,i%2);Grid.SetRow(cell,i/2);grid.Children.Add(cell);
        }
        body.Children.Add(grid);
        var error=TextLine("",12,_palette.Dark?"#F0A6AA":"#B12E3A");error.TextWrapping=TextWrapping.Wrap;error.Visibility=Visibility.Collapsed;AutomationProperties.SetLiveSetting(error,AutomationLiveSetting.Assertive);body.Children.Add(error);
        bool Commit()
        {
            var prices=new List<decimal?>();
            foreach(var (name,input) in fields)
            {
                var text=input.Text.Trim();
                if(text.Length==0&&name!=values[0].Name&&name!=values[1].Name){prices.Add(null);continue;}
                if(!decimal.TryParse(text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||value<0||value>1_000_000_000m)
                {error.Text=Loc.T(name+"单价必须为零或正数。", "The "+name.ToLowerInvariant()+" price must be zero or a positive number.");error.Visibility=Visibility.Visible;return false;}
                prices.Add(value/1_000_000m);
            }
            var next=new TokenPrices(prices[0]!.Value,prices[1]!.Value,prices[2],prices[3],prices[4]);
            if(!adding&&model!.Prices==next){error.Visibility=Visibility.Collapsed;return true;}
            if(!_app.Prices.TrySetManual(id.Text,next,!adding,out var message)){error.Text=message;error.Visibility=Visibility.Visible;return false;}
            if(!adding)model=model! with {Prices=next,Tiers=[]};
            error.Visibility=Visibility.Collapsed;foreach(var deck in _app.Decks)deck.RenderUsage();RefreshPriceRows();return true;
        }
        // Existing rows commit valid edits on focus loss. Creating a new identity is explicit.
        if(!adding)foreach(var field in fields.Values)field.LostKeyboardFocus+=(_,_)=>Commit();
        var done=ActionButton(adding?Loc.T("添加模型", "Add model"):Loc.T("完成", "Done"),()=>{if(Commit())dialog.Close();},true);done.Margin=new Thickness(0,10,0,0);done.HorizontalAlignment=HorizontalAlignment.Right;done.MinWidth=84;AutomationProperties.SetName(done,adding?Loc.T("确认添加模型", "Confirm adding the model"):Loc.T("完成价格编辑", "Finish editing the price"));DockPanel.SetDock(done,Dock.Bottom);panel.Children.Add(done);
        panel.Children.Add(new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false});
        // Opened over the settings window, on its screen; the dialog grows with its content up to that screen's work area.
        var screen=Native.WindowsHost.WindowScreen(this);dialog.MaxHeight=screen.Work.Height/screen.Scale-24;
        var frame=new Border {Child=panel,BorderThickness=new Thickness(1),Focusable=true,FocusVisualStyle=null};
        KeyboardNavigation.SetIsTabStop(frame,false);ReleaseFocusOnEmptyClick(dialog,()=>frame);
        frame.SetResourceReference(Border.BackgroundProperty,"SettingsSurface");frame.SetResourceReference(Border.BorderBrushProperty,"SettingsBorder");dialog.Content=frame;
        dialog.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};
        dialog.Loaded+=(_,_)=>{if(adding)id.Focus();else fields[values[0].Name].Focus();};
        return dialog;
    }
    private static void WindowChromeVisible(UIElement element)=>System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(element,true);
}
