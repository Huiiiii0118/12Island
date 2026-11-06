using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// Changes the system volume by pressing the keyboard's volume keys, so Windows steps it as the keys do and shows its own
// volume flyout.
internal static class VolumeKeys
{
    private const byte VolumeDown = 0xAE, VolumeUp = 0xAF;
    private const uint ExtendedKey = 0x1, KeyUp = 0x2;

    public static void Press(bool up, int times)
    {
        byte key = up ? VolumeUp : VolumeDown;
        for (int i = 0; i < times; i++)
        {
            keybd_event(key, 0, ExtendedKey, UIntPtr.Zero);
            keybd_event(key, 0, ExtendedKey | KeyUp, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
