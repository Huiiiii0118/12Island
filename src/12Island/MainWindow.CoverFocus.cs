using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TwelveIsland.Native;

namespace TwelveIsland;

// Clicking the album cover brings the playing application's window to the front, restoring it when minimized. Pressing
// shrinks the cover slightly, as the transport buttons do. A player left with only a tray icon has no window to show, so
// the cover then does not react at all.
public partial class MainWindow
{
    private void AttachCoverFocus(FrameworkElement art)
    {
        // A second scale on top of the paused one, so the press and the pause animations do not interrupt each other.
        var press = new ScaleTransform(1, 1);
        art.RenderTransform = new TransformGroup { Children = { art.RenderTransform, press } };
        bool pressed = false;
        void Press(bool down)
        {
            pressed = down;
            bool animate = Settings.Animations && SystemParameters.ClientAreaAnimation;
            MusicControlButton.Animate(press, ScaleTransform.ScaleXProperty, down ? .94 : 1, animate, Soft.Hover);
            MusicControlButton.Animate(press, ScaleTransform.ScaleYProperty, down ? .94 : 1, animate, Soft.Hover);
        }
        art.MouseLeftButtonDown += (_, e) =>
        {
            if (_media?.CurrentOwner is not { } owner || MediaAppWindows.Find(owner) == 0) return;
            Press(true); e.Handled = true;
        };
        art.MouseLeave += (_, _) => { if (pressed) Press(false); };
        art.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            Press(false); e.Handled = true;
            // Synchronous on purpose: the foreground can only be handed over while this click is being handled.
            if (_media?.CurrentOwner is { } owner) MediaAppWindows.Activate(MediaAppWindows.Find(owner));
        };
    }
}
