using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TwelveIsland;

public sealed partial class SettingsWindow
{
    // Inherit through control templates, including slider thumbs and number editors.
    private static readonly DependencyProperty PreviewControlProperty = DependencyProperty.RegisterAttached(
        "PreviewControl", typeof(bool), typeof(SettingsWindow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static T KeepPreview<T>(T control) where T : DependencyObject
    { control.SetValue(PreviewControlProperty, true); return control; }

    private void DismissPreviewOutsideControl(object? source)
    {
        if (_page is 1 or 2 or ThemePage or MonitorSettingsPage && (source is not DependencyObject control || !(bool)control.GetValue(PreviewControlProperty)))
            QueuePreviewDismissal();
    }

    // The top of the window is its caption (SettingsChrome), where a press is a non-client message and raises no mouse event,
    // so the press there is caught from the window's messages instead. The caption buttons are hit-testable and take the path above.
    private void DismissPreviewOnCaptionPress()
    {
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool _) =>
        {
            const int LeftDown = 0x00A1, RightDown = 0x00A4, MiddleDown = 0x00A7;
            if (message is LeftDown or RightDown or MiddleDown) DismissPreviewOutsideControl(null);
            return IntPtr.Zero;
        });
    }

    private void QueuePreviewDismissal()
    {
        // A number editor commits on focus loss and may start preview again.
        // Finish that input event before releasing the forced expansion.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_preview || _app.Exiting) return;
            _preview = false;
            _app.Deck.ResumeAfterPreview();
        });
    }

    // Ends the preview at once, for callers outside this window: clears this window's flag and resumes the main panel.
    internal void EndPreview() { if (!_preview) return; _preview = false; _app.Deck.ResumeAfterPreview(); }
}
