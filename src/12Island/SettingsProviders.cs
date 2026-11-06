using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TwelveIsland.Core;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    private Guid? _expandedSource;
    private CancellationTokenSource? _sourceTestCancellation;
    private readonly Dictionary<Guid, (Border Row, FrameworkElement Detail, Button Arrow)> _sourceRows = [];

    private void CollapseSourceRows()
    {
        _expandedSource = null;
        foreach (var (_, view) in _sourceRows)
        { view.Detail.Visibility = Visibility.Collapsed; view.Row.Height = AppRowHeight; PointArrow(view.Arrow, false); }
    }
    // The same chevron as the selectors, turned to point right while the row is closed.
    private static void PointArrow(Button arrow, bool open)
    {
        if (arrow.Content is UIElement chevron) chevron.RenderTransform = new RotateTransform(open ? 0 : -90);
        AutomationProperties.SetItemStatus(arrow, open ? Loc.T("已展开", "Expanded") : Loc.T("已收起", "Collapsed"));
    }
    // The Windows 11 icon font's small chevron (ChevronDownSmall) is hinted like text and heavy enough beside a bold label.
    // Expandable rows, selectors, combo boxes and the panel's source button all use it.
    // Measured ink: the glyph's center sits at 0.4688 of its box, so it turns about that point, and the labels' glyphs sit
    // lower than their boxes, so it drops 1 DIP to meet them.
    internal static readonly Thickness ChevronDrop = new(0, 1, 0, -1);
    internal static TextBlock Chevron() => new()
    {
        Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = ChevronDrop,
        RenderTransformOrigin = new Point(.5, .4688), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    // The chevron follows the button's foreground, so hover brightens it.
    private Button ChevronButton(string label, Action action)
    {
        var button = new Button { Content = Chevron(), Style = (Style)FindResource("SettingsGlyphButton"), Width = 18, Height = 28 };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    // A bare icon whose stroke follows the button, so hover brightens the icon instead of filling a box.
    private Button GlyphButton(string data, double width, double height, string label, Action action)
    {
        var icon = Stroke(data, width, height, TextSecondary); icon.StrokeThickness = 1.3; icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Content = icon, Style = (Style)FindResource("SettingsGlyphButton"), ToolTip = label };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    private void AttachSourceDetails(AppEntry entry, Grid row, Border border)
    {
        var arrow = ChevronButton(Loc.T("展开 " + entry.Name + " 配额配置", "Show " + entry.Name + " quota configuration"), () => ToggleSourceRow(entry.InstanceId));
        arrow.HorizontalAlignment = HorizontalAlignment.Right; arrow.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(arrow, "expand-source-" + entry.InstanceId.ToString("N"));
        // Right after the drag handle, so Tab reaches the arrow at the row's left before the row's other controls (SW-05). The two
        // share the first column side by side (22 + 18 DIP), so the order changes nothing on screen.
        int handle = row.Children.OfType<UIElement>().ToList().FindIndex(child => child is GripHandle);
        row.Children.Insert(handle + 1, arrow);
        var detail = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; Grid.SetIsSharedSizeScope(detail, true);
        Grid.SetRow(detail, 1); Grid.SetColumnSpan(detail, 9);
        if (SourceDetails(entry) is { } source) { source.Margin = new Thickness(0); detail.Children.Add(source); }
        if (ProviderCatalog.HasAccountBalance(entry.QuotaSource)) detail.Children.Add(BalanceTargetDetails(entry));
        detail.Children.Add(OffPeakDetails(entry));
        bool open = _expandedSource == entry.InstanceId;
        detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(detail);
        if (open) border.Height = double.NaN;
        PointArrow(arrow, open);
        _sourceRows[entry.InstanceId] = (border, detail, arrow);
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, row)) { ToggleSourceRow(entry.InstanceId); e.Handled = true; }
        };
    }
    internal void ToggleSourceRow(Guid id)
    {
        bool open = _expandedSource != id;
        CollapseSourceRows();
        if (open && _sourceRows.TryGetValue(id, out var view))
        { _expandedSource = id; view.Detail.Visibility = Visibility.Visible; view.Row.Height = double.NaN; PointArrow(view.Arrow, true); }
    }

    // The same help mark as the column headings of the application list, for a label inside an expanded row.
    private FrameworkElement SmallHelp(string title, Func<string> help)
    {
        var mark = Stroke("M 6,1 A 5,5 0 1 1 6,11 A 5,5 0 1 1 6,1 Z M 6,5.4 V 8.4 M 6,3.6 V 3.7", 12, 12, TextTertiary); mark.StrokeThickness = 1.1;
        // The 12 DIP mark takes the pointer 24 DIP square (SW-08); the negative margin keeps its place.
        var info = new HelpMark { Width = 24, Height = 24, Background = Brushes.Transparent, Margin = new Thickness(4 - 6, -6, -6, -6),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Help, Focusable = true, Child = mark,
            FocusVisualStyle = (Style)FindResource("SettingsSmallHelpFocus") };
        AttachSettingHelp(info, title, help);
        return info;
    }

    // Claude and Grok Bot need no site or key; the expanded row holds this row's own switch for reading the quota online.
    private FrameworkElement OnlineQuotaDetails(AppEntry entry)
    {
        var grid = AppColumns(); grid.Margin = new Thickness(0, 0, 0, 12);
        bool grokBot = entry.QuotaSource == ProviderId.GrokBot;
        string title = Loc.T("联网获取额度", "Fetch quota online");
        string help = grokBot
            ? Loc.T("开启：使用 Grok Bot 桌面版的登录，联网获取每周额度。\n关闭：不联网，只显示 Grok Bot 桌面版在本机缓存的最新每周额度。",
                "On: fetches the weekly allowance online with the Grok Bot desktop app sign-in.\nOff: no network access; shows the latest weekly allowance the Grok Bot desktop app cached on this PC.")
            : Loc.T("开启：使用 Claude Code 或桌面版的登录，联网获取额度。\n关闭：不联网，只显示 Claude 桌面版在本机记录的最新额度。",
                "On: fetches the quota online with the Claude Code or desktop app sign-in.\nOff: no network access; shows the latest quota the Claude desktop app recorded on this PC.");
        var line = new Grid();
        // The label column is shared with the off-peak lines below, so the switch starts where their controls start.
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = TextLine(title, 12, TextSecondary);
        var heading = new HelpHeading(caption, SmallHelp(title, () => help)) { Margin = new Thickness(16, 0, 16, 0), MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(heading);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.QuotaOnline, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); AutomationProperties.SetHelpText(toggle, help);
        AutomationProperties.SetAutomationId(toggle, (grokBot ? "grokbot-online-" : "claude-online-") + entry.InstanceId.ToString("N"));
        Grid.SetColumn(toggle, 1); line.Children.Add(toggle);
        void Set(bool online)
        {
            if (S.Entry(entry.InstanceId) is not { } current || current.QuotaOnline == online) return;
            Change(s => s.Entry(entry.InstanceId)!.QuotaOnline = online);
        }
        toggle.Checked += (_, _) => Set(true); toggle.Unchecked += (_, _) => Set(false);
        line.Margin = new Thickness(0, 5, 0, 5);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, 6); grid.Children.Add(line);
        return grid;
    }

    // Sources billed per API call: the balance that counts as 0% used, so that the account balance is drawn as a used share
    // with the row's warning and critical levels. New rows start at 100; empty leaves the balance a bare amount.
    private FrameworkElement BalanceTargetDetails(AppEntry entry)
    {
        var grid = AppColumns();
        string title = Loc.T("目标余额", "Target balance");
        string help = Loc.T("以目标余额为 100%，按剩余比例显示用量。例如目标余额 100，剩余 20 时显示已用 80%。",
            "The target balance counts as 100%, and usage is shown from what is left. For example, with a target of 100 and 20 left, 80% is shown as used.");
        var line = new Grid();
        // The label column is shared with the lines above and below, so the field starts where their controls start.
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = TextLine(title, 12, TextSecondary);
        var heading = new HelpHeading(caption, SmallHelp(title, () => help)) { Margin = new Thickness(16, 0, 16, 0), MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(heading);
        static string Format(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left, FontSize = 12.5, MaxLength = 20,
            VerticalAlignment = VerticalAlignment.Center, Text = Format(entry.BalanceTarget) };
        AutomationProperties.SetName(input, title); AutomationProperties.SetHelpText(input, help);
        AutomationProperties.SetAutomationId(input, "balance-target-" + entry.InstanceId.ToString("N"));
        var hint = TextLine(Loc.T("未设置", "Not set"), 12.5, TextTertiary);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; hint.Margin = new Thickness(5, 0, 0, 0);
        var content = new Grid(); content.Children.Add(hint); content.Children.Add(input);
        var field = new Border { Child = content, Width = 140, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 5, 0, 5),
            Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1) };
        FieldFocus(field, field, () => UI.Brush(_palette.InputBorder));
        field.MouseLeftButtonDown += (_, e) => { input.Focus(); e.Handled = true; };
        void Hint() => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Commit()
        {
            if (S.Entry(entry.InstanceId) is not { } current) return;
            // A currency sign or thousands separator copied with the amount is accepted.
            var text = input.Text.Trim().TrimStart('¥', '￥', '$').Replace(",", "").Replace("，", "").Replace('．', '.').Trim();
            decimal? value = null;
            if (text.Length > 0)
            {
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || parsed > AppPresets.MaximumBalanceTarget)
                {
                    SaveStatus(Loc.T("目标余额必须是大于 0 的金额，例如 100。", "The target balance must be an amount greater than 0, such as 100."), true);
                    input.Text = Format(current.BalanceTarget); Hint();
                    return;
                }
                value = decimal.Round(parsed, 2, MidpointRounding.AwayFromZero);
            }
            if (value != current.BalanceTarget) Change(s => s.Entry(entry.InstanceId)!.BalanceTarget = value);
            input.Text = Format(value); Hint();
        }
        input.TextChanged += (_, _) => Hint();
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { input.Text = Format(S.Entry(entry.InstanceId)?.BalanceTarget); Hint(); e.Handled = true; }
        };
        Hint();
        Grid.SetColumn(field, 1); line.Children.Add(field);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, 6); grid.Children.Add(line);
        return grid;
    }

    // The expanded configuration starts under the quota source: a short label column, then fields that end midway
    // between the warning and critical colors. Labels share one width so every field starts at the same place.
    // Built-in sources other than Claude and Grok Bot have no configuration of their own; their rows open on the off-peak time alone.
    private FrameworkElement? SourceDetails(AppEntry entry)
    {
        if (ProviderCatalog.HasOnlineSwitch(entry.QuotaSource)) return OnlineQuotaDetails(entry);
        if (ProviderCatalog.IsBuiltIn(entry.QuotaSource)) return null;
        var grid = AppColumns();
        var result = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(6, 4, 6, 4) };
        Func<bool> commitSite = () => true;
        Action commitScript = () => { };
        void InvalidateTest() { result.Visibility = Visibility.Collapsed; _sourceTestCancellation?.Cancel(); }
        TextBlock? Line(string label, FrameworkElement control, int span = 5)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            // A minimum width keeps fields of different sources starting on the same line.
            var caption = TextLine(label, 12, TextSecondary); caption.Margin = new Thickness(16, 0, 16, 0); caption.VerticalAlignment = VerticalAlignment.Center; caption.MinWidth = 52;
            // An unlabeled line still shares the label width but adds no height of its own.
            if (label.Length == 0) caption.Visibility = Visibility.Collapsed;
            line.Children.Add(caption);
            control.Margin = new Thickness(0, 5, 0, 5); Grid.SetColumn(control, 1); line.Children.Add(control);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(line, grid.RowDefinitions.Count - 1); Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, span); grid.Children.Add(line);
            return label.Length > 0 ? caption : null;
        }
        // A field drawn like the display name box; the hint sits inside it until something is typed.
        Border Field(UIElement input, TextBlock hint, UIElement? trailing = null)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Typed text starts 2 in from the input's edge; the hint starts 5 in, so the caret stands just before it rather than on it.
            hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; hint.TextTrimming = TextTrimming.CharacterEllipsis; hint.Margin = new Thickness(5, 0, 0, 0);
            content.Children.Add(hint); content.Children.Add(input);
            if (trailing is not null) { Grid.SetColumn(trailing, 1); content.Children.Add(trailing); }
            var field = new Border { Child = content, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 4, 0),
                Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1) };
            FieldFocus(field, field, () => UI.Brush(_palette.InputBorder));
            return field;
        }
        TextBox Plain(string id, string name)
        {
            var box = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left, FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center, MaxLength = 2000, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetAutomationId(box, id + "-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(box, name);
            return box;
        }

        if (ProviderCatalog.NeedsSite(entry.QuotaSource))
        {
            bool custom = entry.QuotaSource == ProviderId.Custom;
            var site = Plain("site", Loc.T("站点地址", "Site address")); site.Text = entry.Site;
            string usage = custom ? Loc.T("可选，作为 ctx.site 传给脚本", "Optional. Passed to the script as ctx.site")
                : Loc.T("末尾的 /、/v1 和控制台路径会自动去掉", "A trailing /, /v1 or console path is removed automatically");
            var hint = TextLine(usage, 12.5, TextTertiary);
            // An HTTP address the row does not allow (one saved by an earlier version included) is not read; the line under
            // the field says why, apart from the hint inside it.
            var warning = TextLine(ProviderCatalog.HttpsRequired, 12, PanelTheme.WarningDefault); warning.TextWrapping = TextWrapping.Wrap;
            bool AllowsHttp() => S.Entry(entry.InstanceId)?.AllowHttp == true;
            void Hint()
            {
                hint.Visibility = site.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                bool insecure = Uri.TryCreate(site.Text.Trim(), UriKind.Absolute, out var typed) && ProviderCatalog.NeedsHttps(typed, AllowsHttp());
                warning.Visibility = insecure ? Visibility.Visible : Visibility.Collapsed;
            }
            bool CommitSite()
            {
                if (S.Entry(entry.InstanceId) is not { } current) return false;
                var normalized = ProviderCatalog.NormalizeSite(site.Text, !custom);
                if (normalized.Length > 0)
                {
                    try
                    {
                        var uri = ProviderCatalog.ValidateUrl(normalized, current.AllowHttp);
                        if (uri.Query.Length > 0) throw new InvalidDataException(Loc.T("站点地址不应包含查询参数。", "The site address must not contain query parameters."));
                    }
                    catch (InvalidDataException ex)
                    {
                        // HTTPS is already explained by the line under the field; other faults still go to the status bar.
                        if (!string.Equals(ex.Message, ProviderCatalog.HttpsRequired, StringComparison.Ordinal)) SaveStatus(ex.Message, true);
                        return false;
                    }
                }
                if (normalized != current.Site) Change(s => s.Entry(entry.InstanceId)!.Site = normalized);
                site.Text = normalized;
                return true;
            }
            commitSite = CommitSite;
            site.TextChanged += (_, _) => { Hint(); InvalidateTest(); };
            site.LostKeyboardFocus += (_, _) => CommitSite();
            site.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSite(); e.Handled = true; } };
            Line(Loc.T("站点地址", "Site address"), Field(site, hint));
            Line("", warning);

            string allowTitle = Loc.T("允许 HTTP", "Allow HTTP");
            string allowHelp = Loc.T("允许用 HTTP 访问，但 API 密钥会以明文发送。只建议在可信网络中开启，例如局域网。",
                "HTTP is allowed, but the API key is sent in plain text. Turn this on only on a trusted network, such as a LAN.");
            var allow = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.AllowHttp, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(allow, allowTitle); AutomationProperties.SetHelpText(allow, allowHelp);
            AutomationProperties.SetAutomationId(allow, "allow-http-" + entry.InstanceId.ToString("N"));
            var allowLine = new StackPanel { Orientation = Orientation.Horizontal };
            var allowInfo = HelpIcon(allowTitle, () => allowHelp, 14); allowInfo.Margin = new Thickness(8, 0, 0, 0);
            allowLine.Children.Add(allow); allowLine.Children.Add(allowInfo);
            void SetAllowHttp(bool value)
            {
                if (S.Entry(entry.InstanceId) is not { } current || current.AllowHttp == value) return;
                Change(s => s.Entry(entry.InstanceId)!.AllowHttp = value);
                Hint(); InvalidateTest();
                // An HTTP address typed while it was refused is saved now that it is allowed.
                if (value) CommitSite();
            }
            allow.Checked += (_, _) => SetAllowHttp(true); allow.Unchecked += (_, _) => SetAllowHttp(false);
            Line(allowTitle, allowLine);
            Hint();
        }

        var password = new PasswordBox { FontSize = 12.5, MaxLength = 4096, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = UI.Brush(TextPrimary), CaretBrush = UI.Brush(_palette.Accent),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetAutomationId(password, "secret-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(password, Loc.T("API 密钥", "API key"));
        var visible = Plain("secret-text", Loc.T("API 密钥", "API key")); visible.MaxLength = 4096; visible.Visibility = Visibility.Collapsed;
        string emptyHint = entry.QuotaSource switch
        {
            ProviderId.GlmChina or ProviderId.GlmGlobal => Loc.T("GLM 套餐密钥，加密保存在本机", "GLM plan key, stored encrypted on this PC"),
            ProviderId.DeepSeek => Loc.T("DeepSeek API 密钥，加密保存在本机", "DeepSeek API key, stored encrypted on this PC"),
            ProviderId.KimiApi => Loc.T("Kimi Code API 密钥（sk-kimi-…），加密保存在本机", "Kimi Code API key (sk-kimi-…), stored encrypted on this PC"),
            ProviderId.OpenCodeGo => Loc.T("OpenCode Console API 密钥（oc_sk_…），加密保存在本机", "OpenCode Console API key (oc_sk_…), stored encrypted on this PC"),
            ProviderId.OpenRouter => Loc.T("OpenRouter API 密钥（sk-or-v1-…），加密保存在本机", "OpenRouter API key (sk-or-v1-…), stored encrypted on this PC"),
            ProviderId.Custom => Loc.T("可选，加密保存在本机", "Optional, stored encrypted on this PC"),
            _ => Loc.T("填写 API 密钥，加密保存在本机", "Enter the API key; it is stored encrypted on this PC")
        };
        string savedHint = Loc.T("已加密保存在本机，输入新密钥以更换", "Stored encrypted on this PC. Enter a new key to replace it");
        var keyHint = TextLine(entry.SecretRevision != Guid.Empty ? savedHint : emptyHint, 12.5, TextTertiary);
        var inputs = new Grid(); inputs.Children.Add(password); inputs.Children.Add(visible);
        // The icon shows the current state: struck through while the key is dots, open while it is plain text.
        const string eye = "M 1,6 C 3.5,0.5 12.5,0.5 15,6 C 12.5,11.5 3.5,11.5 1,6 Z M 8,4 A 2,2 0 1 1 8,8 A 2,2 0 1 1 8,4 Z";
        const string eyeOff = eye + " M 2.5,0.5 L 13.5,11.5";
        bool syncing = false, dirty = false, swapping = false;
        Button? reveal = null;
        reveal = GlyphButton(eyeOff, 16, 12, Loc.T("显示密钥", "Show key"), () => ToggleReveal());
        reveal.Width = 28; reveal.Height = 28; reveal.Focusable = false; reveal.ToolTip = null; ((Shape)reveal.Content).StrokeThickness = 1.2;
        // Only the other box is updated: writing back into the box being typed in would move its caret to the start.
        void EditSecret(string value, bool fromPassword)
        {
            if (syncing) return;
            syncing = true; if (fromPassword) visible.Text = value; else password.Password = value; syncing = false;
            dirty = true; keyHint.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed; InvalidateTest();
        }
        password.PasswordChanged += (_, _) => EditSecret(password.Password, true); visible.TextChanged += (_, _) => EditSecret(visible.Text, false);
        void SaveSecret()
        {
            if (!dirty || S.Entry(entry.InstanceId) is null) return;
            try
            {
                var secret = password.Password.Trim();
                _app.Secrets.Write(entry.InstanceId, secret);
                Change(s => s.Entry(entry.InstanceId)!.SecretRevision = secret.Length == 0 ? Guid.Empty : Guid.NewGuid());
                dirty = false;
                syncing = true; password.Clear(); visible.Clear(); syncing = false;
                keyHint.Text = S.Entry(entry.InstanceId)!.SecretRevision == Guid.Empty ? emptyHint : savedHint;
                keyHint.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
            { SaveStatus(Loc.T("密钥保存失败，请检查本机设置目录。", "The key could not be saved. Check the local settings folder."), true); }
        }
        // Swapping the dots for plain text hides the box being typed in; that is not leaving the field, so nothing is saved.
        void ToggleReveal()
        {
            bool show = visible.Visibility != Visibility.Visible, editing = password.IsKeyboardFocused || visible.IsKeyboardFocused;
            swapping = true;
            syncing = true;
            // With nothing typed, revealing shows the saved key and hiding puts the saved hint back.
            if (!dirty && show && S.Entry(entry.InstanceId)?.SecretRevision is { } revision && revision != Guid.Empty)
            {
                string saved = "";
                try { saved = _app.Secrets.Read(entry.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException or FormatException) { }
                password.Password = saved; visible.Text = saved;
            }
            else if (!dirty && !show) { password.Clear(); visible.Clear(); }
            syncing = false;
            keyHint.Visibility = password.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            visible.Visibility = show ? Visibility.Visible : Visibility.Collapsed; password.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (editing)
            {
                if (show) { visible.Focus(); visible.CaretIndex = visible.Text.Length; }
                else
                {
                    // PasswordBox puts the caret at the start when focused and exposes no caret property;
                    // its internal Select(start, length) keeps the caret where it was in the plain-text box.
                    int caret = visible.CaretIndex;
                    password.Focus();
                    typeof(PasswordBox).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(int), typeof(int)])
                        ?.Invoke(password, [caret, 0]);
                }
            }
            swapping = false;
            ((Shape)reveal!.Content).SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse(show ? eye : eyeOff));
            AutomationProperties.SetName(reveal, show ? Loc.T("隐藏密钥", "Hide key") : Loc.T("显示密钥", "Show key"));
        }
        password.LostKeyboardFocus += (_, _) => { if (!swapping) SaveSecret(); };
        visible.LostKeyboardFocus += (_, _) => { if (!swapping) SaveSecret(); };
        Line(entry.QuotaSource == ProviderId.Custom ? Loc.T("API 密钥（可选）", "API key (optional)") : Loc.T("API 密钥", "API key"), Field(inputs, keyHint, reveal));

        if (entry.QuotaSource == ProviderId.Custom)
        {
            var (frame, code) = ScriptEditor.Create(entry.Script, _palette);
            AutomationProperties.SetAutomationId(code, "script-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(code, Loc.T("配额脚本", "Quota script"));
            void SaveCode() { if (S.Entry(entry.InstanceId) is { } current && current.Script != code.Text) Change(s => s.Entry(entry.InstanceId)!.Script = code.Text); }
            commitScript = SaveCode;
            code.IsKeyboardFocusWithinChanged += (_, _) => { if (!code.IsKeyboardFocusWithin) SaveCode(); };
            code.TextChanged += (_, _) => InvalidateTest();
            // The code reaches across to the show switch: the switch is 38 wide and centred in its 50-wide column, so the frame stops 6 short of it.
            var codeLabel = Line(Loc.T("代码", "Code"), frame, 7)!; codeLabel.VerticalAlignment = VerticalAlignment.Top; codeLabel.Margin = new Thickness(16, 13, 16, 0);
            frame.Margin = new Thickness(0, 5, 6, 5);
        }

        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { }); test.Style = (Style)FindResource("SettingsOutlineButton");
        test.Height = 34; test.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(test, "test-source-" + entry.InstanceId.ToString("N"));
        test.Click += async (_, _) =>
        {
            if (!commitSite()) return;
            commitScript();
            SaveSecret();
            if (dirty || S.Entry(entry.InstanceId) is not { } current) return;
            _sourceTestCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token); _sourceTestCancellation = cancellation;
            var configuration = current.ConfigurationKey;
            test.IsEnabled = false; test.Content = Loc.T("正在测试…", "Testing…"); result.Visibility = Visibility.Collapsed;
            try
            {
                var response = await _app.Usage.TestProviderAsync(current, cancellation.Token);
                if (cancellation.IsCancellationRequested || S.Entry(entry.InstanceId)?.ConfigurationKey != configuration) return;
                result.Children.Clear(); result.Visibility = Visibility.Visible;
                var snapshot = response.Snapshot;
                result.Children.Add(TextLine(snapshot.StatusLabel + Loc.T($" · {response.Seconds:0.0} 秒", $" · {response.Seconds:0.0} s"), 12, TextPrimary, FontWeights.SemiBold));
                // The first line already names the state, so only the extra explanation is shown below it.
                string separator = Loc.T("；", "; ");
                var explanation = snapshot.Status.StartsWith(snapshot.StatusLabel + separator, StringComparison.Ordinal) ? snapshot.Status[(snapshot.StatusLabel.Length + separator.Length)..] : snapshot.Status;
                if (explanation.Length > 0 && explanation != snapshot.StatusLabel)
                { var description = TextLine(explanation, 11.5, TextSecondary); description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0, 3, 0, 0); result.Children.Add(description); }
                if (snapshot.LiveQuota && snapshot.Details.Count > 0)
                { var details = TextLine(string.Join("\n", snapshot.Details.Select(p => p.Key + Loc.T("：", ": ") + p.Value)), 11.5, TextSecondary); details.TextWrapping = TextWrapping.Wrap; result.Children.Add(details); }
                if (response.Logs.Count > 0) { var logs = TextLine(string.Join("\n", response.Logs), 11.5, TextSecondary); logs.TextWrapping = TextWrapping.Wrap; result.Children.Add(logs); }
            }
            catch (OperationCanceledException) { }
            finally { test.Content = Loc.T("测试连接", "Test connection"); test.IsEnabled = true; if (_sourceTestCancellation == cancellation) _sourceTestCancellation = null; cancellation.Dispose(); }
        };
        Line("", test);
        Line("", result);
        return grid;
    }

    // Every row's expanded area ends with its off-peak time: daily periods, each dragged out on a bar from noon to noon,
    // and, once there is a period, the color the panel draws this row's quotas in during them. The labels share the
    // source configuration's label width, so every control in the expanded area starts at the same place.
    private FrameworkElement OffPeakDetails(AppEntry entry)
    {
        var grid = AppColumns();
        string id = entry.InstanceId.ToString("N");
        void Line(string label, FrameworkElement control)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            // The heading stands level with the first control, which is 34 high under a margin of 5.
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 5, 16, 0), Height = 34, MinWidth = 52, VerticalAlignment = VerticalAlignment.Top };
            var caption = TextLine(label, 12, TextSecondary); caption.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(caption);
            line.Children.Add(heading);
            Grid.SetColumn(control, 1); line.Children.Add(control);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(line, grid.RowDefinitions.Count - 1); Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, 5); grid.Children.Add(line);
        }
        string Span(int start, int end) => OffPeakPeriod.Format(start) + " – " + (end <= start ? Loc.T("次日 ", "next day ") : "") + OffPeakPeriod.Format(end);

        var periods = new StackPanel();
        for (int i = 0; i < entry.OffPeak.Count; i++)
        {
            int index = i;
            var saved = entry.OffPeak[index];
            var period = new Grid { Margin = new Thickness(0, 5, 0, 5), Height = 34 };
            period.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 380 });
            period.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            period.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = new OffPeakBar(saved.Start, saved.End)
            {
                Track = UI.Brush(_palette.Border), Range = UI.Brush(entry.OffPeakColor.Length > 0 ? PanelTheme.Readable(entry.OffPeakColor) : PanelTheme.AppTheme(entry)), Knobs = Brushes.White,
                Outline = UI.Brush(_palette.Border), FocusRing = UI.Brush(_palette.Accent), Scale = UI.Brush(TextTertiary),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            AutomationProperties.SetAutomationId(bar, "offpeak-bar-" + index + "-" + id);
            var times = TextLine(Span(saved.Start, saved.End), 12.5, TextPrimary); times.VerticalAlignment = VerticalAlignment.Center;
            times.Margin = new Thickness(16, 0, 0, 0);
            void Describe() => AutomationProperties.SetName(bar, Loc.T($"低峰时段 {index + 1}：", $"Off-peak period {index + 1}: ") + Span(bar.Start, bar.End));
            Describe();
            bar.Moved += () => { times.Text = Span(bar.Start, bar.End); Describe(); };
            bar.Committed += () =>
            {
                if (S.Entry(entry.InstanceId)?.OffPeak.ElementAtOrDefault(index) is not { } current || current.Start == bar.Start && current.End == bar.End) return;
                Change(s => { var changed = s.Entry(entry.InstanceId)!.OffPeak[index]; changed.Start = bar.Start; changed.End = bar.End; });
            };
            Grid.SetColumn(times, 1);
            var cross = Stroke("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, TextSecondary);
            cross.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var remove = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Content = cross, Width = 24, Height = 26, Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, Loc.T($"删除低峰时段 {index + 1}", $"Remove off-peak period {index + 1}"));
            AutomationProperties.SetAutomationId(remove, "offpeak-remove-" + index + "-" + id);
            remove.Click += (_, _) => { Change(s => s.Entry(entry.InstanceId)!.OffPeak.RemoveAt(index)); RefreshAppRow(entry.InstanceId); };
            Grid.SetColumn(remove, 2);
            period.Children.Add(bar); period.Children.Add(times); period.Children.Add(remove);
            periods.Children.Add(period);
        }
        // Drawn like "Add app" under the application list: the plus and label only, filled on hover. The negative margin
        // puts the plus where the controls above start.
        bool full = entry.OffPeak.Count >= AppPresets.MaximumOffPeakPeriods;
        var add = ActionButton("", () =>
        {
            Change(s => s.Entry(entry.InstanceId)!.OffPeak.Add(new OffPeakPeriod()));
            RefreshAppRow(entry.InstanceId);
        });
        add.HorizontalAlignment = HorizontalAlignment.Left; add.Height = 34; add.Margin = new Thickness(-10, 5, 0, 5); add.Padding = new Thickness(10, 0, 12, 0); add.IsEnabled = !full;
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        addContent.Children.Add(Stroke("M 5,0 L 5,10 M 0,5 L 10,5", 10, 10, TextSecondary));
        var addLabel = TextLine(Loc.T("添加时段", "Add period"), 12.5, TextSecondary); addLabel.Margin = new Thickness(8, 0, 0, 0); addLabel.VerticalAlignment = VerticalAlignment.Center;
        addContent.Children.Add(addLabel); add.Content = addContent;
        if (full) { add.ToolTip = Loc.T($"最多 {AppPresets.MaximumOffPeakPeriods} 个时段", $"Up to {AppPresets.MaximumOffPeakPeriods} periods"); ToolTipService.SetShowOnDisabled(add, true); }
        AutomationProperties.SetName(add, Loc.T("添加低峰时段", "Add off-peak period")); AutomationProperties.SetAutomationId(add, "offpeak-add-" + id);
        periods.Children.Add(add);
        Line(Loc.T("低峰时间", "Off-peak time"), periods);

        if (entry.OffPeak.Count > 0)
        {
            // Three swatches, as the row has them: an unchosen one shows the row's usual color for its level, which the panel
            // keeps drawing during off-peak periods.
            var swatchLine = new StackPanel { Orientation = Orientation.Horizontal, Height = 34, Margin = new Thickness(0, 5, 0, 5) };
            void Swatch(string label, string key, string chosen, string shown, Action<AppEntry, string> apply)
            {
                var caption = TextLine(label, 12, TextSecondary); caption.VerticalAlignment = VerticalAlignment.Center;
                caption.Margin = new Thickness(swatchLine.Children.Count == 0 ? 0 : 20, 0, 8, 0);
                var swatch = ColorSwatch(entry.Name, Loc.T("低峰", "off-peak ") + label, chosen.Length > 0 ? chosen : shown, chosen.Length == 0,
                    value => Change(s => apply(s.Entry(entry.InstanceId)!, value)), owner: entry.InstanceId);
                swatch.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetAutomationId(swatch, "offpeak-" + key + "-" + id);
                swatchLine.Children.Add(caption); swatchLine.Children.Add(swatch);
            }
            Swatch(Loc.T("主题", "Theme"), "theme", entry.OffPeakColor, entry.ThemeColor.Length > 0 ? entry.ThemeColor : PanelTheme.AppDefault(entry.QuotaSource), (e, v) => e.OffPeakColor = v);
            Swatch(Loc.T("预警", "Warning"), "warning", entry.OffPeakWarningColor, entry.WarningColor.Length > 0 ? entry.WarningColor : PanelTheme.WarningDefault, (e, v) => e.OffPeakWarningColor = v);
            Swatch(Loc.T("告警", "Critical"), "critical", entry.OffPeakCriticalColor, entry.CriticalColor.Length > 0 ? entry.CriticalColor : PanelTheme.CriticalDefault, (e, v) => e.OffPeakCriticalColor = v);
            Line(Loc.T("低峰颜色", "Off-peak colors"), swatchLine);
        }
        return grid;
    }
}
