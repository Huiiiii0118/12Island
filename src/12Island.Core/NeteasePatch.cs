namespace TwelveIsland.Core;

// NetEase Cloud Music starts its DevTools port only when --remote-debugging-port is passed. Two changes in its libcef.dll make
// it open the port on 127.0.0.1:21631 every time it starts, without the switch: each replaces the instruction that loads the
// configured port with "set the port to 21631". Found for NetEase Cloud Music 3.1.41 (CEF 91). Each site is located by its
// surrounding bytes rather than a fixed offset (LibcefPatch), so a rebuild that only moved the code is still found.
public static class NeteasePatch
{
    public const int Port = 21631;
    private static byte[] H(string hex) => Convert.FromHexString(hex);
    // mov edx,[reg+0x17C]  ->  mov edx,0x547F ; nop  (0x547F = 21631), before the port's range check.
    public static readonly LibcefPatch Profile = new(Port,
        new(H(""), H("8B907C010000"), H("BA7F54000090"), H("8D8A00FCFFFF81F9FFFB0000")),
        new(H(""), H("8B957C010000"), H("BA7F54000090"), H("8D8200FCFFFF3DFFFB000077")));

    public static LibcefPatchState Classify(System.IO.Stream file) => Profile.Classify(file);
    public static void Write(System.IO.Stream file, bool patch) => Profile.Write(file, patch);
}
