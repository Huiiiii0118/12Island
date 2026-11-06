namespace TwelveIsland.Core;

// When the picture beneath an open pop-up is taken (TwelveIsland.PopupMotion, motion-review F7). Until the pop-up has entered
// (faded in and dropped into place) nothing is taken: a picture taken then lay opaque under the real list while it still
// faded in and showed through it, a few DIPs off. The first picture is taken once it has entered; after that, each change
// that can alter the list is taken at once and once more when a hover's fade has settled. A change while it entered is kept
// for then: the first picture may catch its hover half faded in (and the list closing then left with a half-tinted option), so
// it is taken again once a hover's fade has settled.
public sealed class PopupPicture
{
    private bool _missed;
    public bool Entered { get; private set; }

    // The pop-up has entered: its first picture is taken now. Returns whether something changed while it entered, which is
    // then taken again once a hover's fade has settled (once; later calls return false).
    public bool Enter()
    {
        Entered = true;
        bool missed = _missed; _missed = false;
        return missed;
    }

    // Something that can change the list happened: whether its picture is taken now (and again once settled). Before the
    // pop-up has entered, the first picture is still to come; the change is remembered for it (Enter).
    public bool Changed()
    {
        if (!Entered) _missed = true;
        return Entered;
    }

    // How long after a change the picture is taken again: the hover fade's time (milliseconds) and a little more, so a hover's
    // fade has settled.
    public static int SettleMilliseconds(int hover) => hover + 40;
}
