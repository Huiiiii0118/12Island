using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using TwelveIsland.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace TwelveIsland.Native;

public sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8001;
    private readonly HwndSource _source;
    private readonly Action _settings;
    private readonly Action _exit;
    private readonly Action? _toggleHidden;
    private readonly Func<bool>? _hidden;
    private NOTIFYICONDATA _data;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    public TrayIcon(Window owner, Action settings, Action exit, Action? toggleHidden = null, Func<bool>? hidden = null)
    {
        _settings = settings; _exit = exit; _toggleHidden = toggleHidden; _hidden = hidden;
        var hwnd = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(hwnd); _source.AddHook(Hook);
        _data = new NOTIFYICONDATA { Size = Marshal.SizeOf<NOTIFYICONDATA>(), Window = hwnd, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = Callback,
            Icon = LoadTrayIcon(out _ownsIcon), Tip = "12 island", Info = "", InfoTitle = "" };
        Shell_NotifyIcon(0, ref _data);
    }
    private bool _ownsIcon;
    private long _dpi;
    // The notification area shows the small icon size at the primary monitor's scale. The executable's icon group carries
    // every size, so the exact one is extracted instead of scaling another.
    private static IntPtr LoadTrayIcon(out bool owned)
    {
        var (_, _, scale) = WindowsHost.PrimaryScreen();
        uint size = (uint)GetSystemMetricsForDpi(49 /* SM_CXSMICON */, (uint)Math.Round(96 * scale));
        if (SHDefExtractIcon(Environment.ProcessPath!, 0, 0, out var large, out var small, size | size << 16) == 0)
        {
            if (large != IntPtr.Zero && large != small) DestroyIcon(large);
            if (small != IntPtr.Zero) { owned = true; return small; }
        }
        owned = false; return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }
    // A scale or display change can change the small icon size, which otherwise stays at the size from start-up.
    private void ReloadIcon()
    {
        var (previous, ownedPrevious) = (_data.Icon, _ownsIcon);
        _data.Icon = LoadTrayIcon(out _ownsIcon);
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref _data);
        if (ownedPrevious) DestroyIcon(previous);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) Shell_NotifyIcon(0, ref _data);
        // WM_DPICHANGED carries the new DPI in its low word; it can arrive without a change, which needs no new icon.
        if (message == 0x02E0 /* WM_DPICHANGED */ && (wParam.ToInt64() & 0xFFFF) is var dpi && dpi != _dpi) { _dpi = dpi; ReloadIcon(); }
        if (message == 0x007E /* WM_DISPLAYCHANGE */) ReloadIcon();
        if (message != Callback) return IntPtr.Zero;
        var mouseMessage = lParam.ToInt32() & 0xffff;
        if (mouseMessage == 0x203) _settings();
        if (mouseMessage == 0x205)
        {
            SetForegroundWindow(hwnd);
            var menu = CreateMenu(_settings, _exit, _toggleHidden, _hidden?.Invoke() ?? false);
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        return IntPtr.Zero;
    }
    // Shared with the UI smoke run, which renders the menu without a tray.
    internal static ContextMenu CreateMenu(Action settings, Action exit, Action? toggleHidden = null, bool hidden = false)
    {
        var menu = new ContextMenu();
        // Tray menus belong to the shell, so they follow the system's app theme rather than the settings window's palette.
        // In a high contrast theme the menu takes the theme's own menu and highlight colors, as the shell's menus do.
        if (SystemParameters.HighContrast)
        {
            menu.Resources["MenuSurface"] = SystemColors.MenuBrush;
            menu.Resources["MenuInk"] = SystemColors.MenuTextBrush;
            menu.Resources["MenuEdge"] = SystemColors.MenuTextBrush;
            menu.Resources["MenuHover"] = SystemColors.HighlightBrush;
            menu.Resources["MenuInkHover"] = SystemColors.HighlightTextBrush;
            menu.Resources["MenuRule"] = SystemColors.GrayTextBrush;
        }
        else if (!SettingsPalette.IsDark(SettingsTheme.System))
        {
            menu.Resources["MenuSurface"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
            menu.Resources["MenuInk"] = menu.Resources["MenuInkHover"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
            menu.Resources["MenuEdge"] = new SolidColorBrush(Color.FromArgb(0x17, 0, 0, 0));
            menu.Resources["MenuHover"] = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE9));
            menu.Resources["MenuRule"] = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        }
        if (toggleHidden is not null) { var show = new MenuItem { Header = hidden ? Loc.T("显示面板", "Show panel") : Loc.T("隐藏面板", "Hide panel") }; show.Click += (_, _) => toggleHidden(); menu.Items.Add(show); }
        var open = new MenuItem { Header = Loc.T("设置", "Settings") }; open.Click += (_, _) => settings(); menu.Items.Add(open);
        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = Loc.T("退出", "Exit") }; quit.Click += (_, _) => exit(); menu.Items.Add(quit);
        return menu;
    }
    public void Dispose() { Shell_NotifyIcon(2, ref _data); _source.RemoveHook(Hook); if (_ownsIcon) DestroyIcon(_data.Icon); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NOTIFYICONDATA
    {
        public int Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public IntPtr BalloonIcon;
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")] private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("user32")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
