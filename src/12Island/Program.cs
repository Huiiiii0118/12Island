namespace TwelveIsland;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The elevated instance a player's install starts writes its libcef.dll and exits without opening the app.
        if (args is [Native.KugouInstall.ElevatedSwitch, var mode, var library]) { Environment.ExitCode = Native.KugouInstall.RunElevated(mode, library); return; }
        if (args is [Native.NeteaseInstall.ElevatedSwitch, var neteaseMode, var neteaseLibrary]) { Environment.ExitCode = Native.NeteaseInstall.RunElevated(neteaseMode, neteaseLibrary); return; }
        // The elevated instance FramePermission starts adds the user to Performance Log Users and exits.
        if (args is [Native.FramePermission.ElevatedSwitch, var user]) { Environment.ExitCode = Native.FramePermission.RunElevated(user); return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
