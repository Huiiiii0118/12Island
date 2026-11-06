namespace TwelveIsland.Core;

// KuGou's libcef.dll ignores --remote-debugging-port. Nine changes in it open the DevTools port on 127.0.0.1:12233 each time
// KuGou starts: the port is written where CEF keeps its "not set" value, and the checks that would skip starting the DevTools
// server are removed. The bytes come from Metabox-Nexus-PlayerCap (player/kugou/watchdog/watchdog.go, Copyright (c) 2026
// VTB-LINK, MIT License); they were made for KuGou 20.1.22.27795 and match 20.1.51.27967 (CEF 89). Each site is located by its
// surrounding bytes rather than a fixed offset (LibcefPatch), so a rebuild that only moved the code is still found; tools that
// patch KuGou the same way (LyricsWall, PlayerCap) write the same bytes.
public static class KugouPatch
{
    public const int Port = 12233;
    private static byte[] H(string hex) => Convert.FromHexString(hex);
    public static readonly LibcefPatch Profile = new(Port,
        new(H("00"), H("C706AAAAAAAA"), H("C706C92F0000"), H("0FB6431784C048")),
        new(H("4889F2"), H("E896A9E9FC"), H("9090909090"), H("813EFEFF0000")),
        new(H("813EFEFF0000"), H("0F8796010000"), H("909090909090"), H("84C0")),
        new(H(""), H("0F848E010000"), H("909090909090"), H("488D8C2400010000")),
        new(H(""), H("8B957C010000"), H("BAC92F000090"), H("8D8200FCFFFF3DFF")),
        new(H("FD84C0"), H("0F8470010000"), H("909090909090"), H("4C8D7C247C")),
        new(H("7C"), H("41C707AAAAAAAA"), H("41C707C92F0000"), H("48B8AAAAAAAA")),
        new(H(""), H("E8F62EB7FD"), H("9090909090"), H("418B1F8D8B00FCFFFF")),
        new(H(""), H("0F44DA"), H("909090"), H("807F1700790A488B4C2460")));

    public static LibcefPatchState Classify(System.IO.Stream file) => Profile.Classify(file);
    public static void Write(System.IO.Stream file, bool patch) => Profile.Write(file, patch);
}
