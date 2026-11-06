using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwelveIsland.Native;

// Brings a program's window up from the collapsed island: the window that flashed for it, else its main window. A mailbox
// that has no client running has no window to bring up — the caller then reads the notice and ends the capsule itself, and
// no mail program is started for it.
internal static class AppActivate
{
    public static bool Open(string program, IntPtr known)
    {
        if (known != IntPtr.Zero && IsWindow(known) && Show(known)) return true;
        foreach (var name in Names(program))
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            foreach (var process in processes)
            {
                try { if (process.MainWindowHandle != IntPtr.Zero && Show(process.MainWindowHandle)) return true; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
        }
        return false;
    }

    // The processes a program runs under; the mailbox itself answers with every mail program it knows.
    private static string[] Names(string program)
    {
        if (program == NotifyPrograms.MailKey) return ["outlook", "foxmail", "thunderbird", "mailmaster", "qqmail"];
        var names = NotifyPrograms.Processes(program);
        return names.Length > 0 ? names : [program];
    }

    // Restores a minimised window and puts it in front. A window the user did not just click on cannot always take the
    // foreground on its own, so the switch is asked for twice, the second time through the shell's own path. The island is a
    // no-activate window: Windows often refuses its SetForegroundWindow, and activation is asynchronous in any case. The
    // program has been opened once its window is restored and visible — waiting for the foreground here reported a failure
    // for a window the user could plainly see, and left the notice capsule on screen after the click.
    private static bool Show(IntPtr window)
    {
        if (!IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        else ShowWindow(window, 5);                  // SW_SHOW
        SetForegroundWindow(window);
        if (GetForegroundWindow() != window) SwitchToThisWindow(window, true);
        return IsWindow(window) && IsWindowVisible(window);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr window, bool alt);
}
