using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// Sends a file or a folder to the Recycle Bin, so a theme deleted by mistake can be brought back.
internal static class RecycleBin
{
    // Returns whether it went: false when the shell refused or the user cancelled.
    public static bool Send(string path)
    {
        var operation = new FileOperation
        {
            Function = 3, // FO_DELETE
            // The list of paths ends with two nulls.
            From = path + "\0\0",
            // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400
        };
        return SHFileOperationW(ref operation) == 0 && !operation.Aborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref FileOperation operation);
}
