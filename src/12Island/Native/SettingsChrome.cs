using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace TwelveIsland.Native;

internal static class SettingsChrome
{
    // These DWM attributes are documented only on Windows 11. Windows 10 uses
    // the existing WPF frame and palette, with square outer corners.
    private static readonly bool SupportsDwmStyling = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void Apply(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 52, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(SupportsDwmStyling ? 12 : 0), UseAeroCaptionButtons = false
        });
        window.SourceInitialized += (_, _) =>
        {
            if (!SupportsDwmStyling) return;
            var handle = new WindowInteropHelper(window).Handle;
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        };
    }
    public static void SetTheme(Window window, bool isDark)
    {
        if (!SupportsDwmStyling) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = isDark ? 1 : 0, border = isDark ? 0x00424248 : 0x00D3CBC9;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
