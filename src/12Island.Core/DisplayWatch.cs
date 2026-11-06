namespace TwelveIsland.Core;

// Whether the session's display is off while the session itself is not locked: many power plans turn the screen off after a
// few minutes and never lock, and nothing the panel draws can then be seen. The window pauses what runs only to be looked at
// (the scrolling title, the equalizer and the audio levels behind it, the carousels, the system readings) until it is back.
// Windows reports the state as GUID_SESSION_DISPLAY_STATUS (Native.DisplayPower); a report can be missed, so the state also
// comes back on by itself whenever the user is plainly there (the pointer on the island, an unlock or a sign-in), and is asked
// again every Recheck while it says off.
public sealed class DisplayWatch
{
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(60);
    public bool Off { get; private set; }

    // A report's data: 0 off, 1 on, 2 dimmed (still seen). Data shorter than a DWORD is not a report. Returns whether Off changed.
    public bool Report(int length, int value) => length >= 4 && Set(value == 0);

    // The user is there: whatever was last reported, the display is on. Returns whether Off changed.
    public bool Seen() => Set(false);

    private bool Set(bool off)
    {
        if (off == Off) return false;
        Off = off; return true;
    }
}
