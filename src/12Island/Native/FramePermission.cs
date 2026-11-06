using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Whether this user may read the frame events FrameRates reads, and the grant. Windows lets the members of its Performance Log
// Users group (S-1-5-32-559) and elevated processes start an event tracing session; adding the user to the group takes
// administrator rights, so TwelveIsland starts itself elevated for that alone (a UAC prompt). The user's own SID is passed on the
// command line: a standard user's prompt asks for an administrator's password, and the elevated copy then runs as that account.
// Windows reads a user's groups at sign-in, so a user added reads frame rates from the next sign-in (SignInAgain until then).
internal enum FrameAccess { Allowed, SignInAgain, Missing }

internal static class FramePermission
{
    internal const string ElevatedSwitch = "--frame-access";
    private static readonly SecurityIdentifier Group = new("S-1-5-32-559");

    public static FrameAccess Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        // An administrator's group is for denying only until elevated, so it counts only in an elevated TwelveIsland.
        if (principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(Group)) return FrameAccess.Allowed;
        return identity.User is { } user && Members().Contains(user) ? FrameAccess.SignInAgain : FrameAccess.Missing;
    }

    public static async Task GrantAsync()
    {
        string user;
        using (var identity = WindowsIdentity.GetCurrent()) user = identity.User!.Value;
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(ElevatedSwitch); info.ArgumentList.Add(user);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException(Loc.T("未取得管理员权限", "Administrator rights were not given")); }
        using (process)
        {
            if (process is null) throw new InvalidOperationException(Loc.T("无法以管理员身份运行", "Could not run as administrator"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(Loc.T("无法加入用户组", "Could not join the group"));
        }
    }

    // The elevated instance: adds the account named on its command line (a user's SID, nothing else) to the group, and exits
    // with 0 once it is a member.
    public static int RunElevated(string sid)
    {
        SecurityIdentifier user;
        try { user = new SecurityIdentifier(sid); }
        catch (ArgumentException) { return 2; }
        if (!user.IsAccountSid() || GroupName() is not { } group) return 2;
        var bytes = new byte[user.BinaryLength];
        user.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            // One LOCALGROUP_MEMBERS_INFO_0: the member's SID.
            IntPtr member = pinned.AddrOfPinnedObject();
            uint error = NetLocalGroupAddMembers(null, group, 0, ref member, 1);
            return error is 0 or MemberInAlias ? 0 : 1;
        }
        finally { pinned.Free(); }
    }

    // The group's members as its local list holds them, which any user may read; the group's name differs with the language.
    private static HashSet<SecurityIdentifier> Members()
    {
        var members = new HashSet<SecurityIdentifier>();
        if (GroupName() is not { } group) return members;
        if (NetLocalGroupGetMembers(null, group, 0, out var buffer, uint.MaxValue, out uint read, out _, IntPtr.Zero) != 0) return members;
        try
        {
            for (int i = 0; i < read; i++) members.Add(new SecurityIdentifier(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)));
        }
        finally { NetApiBufferFree(buffer); }
        return members;
    }
    private static string? GroupName()
    {
        try { return Group.Translate(typeof(NTAccount)).Value.Split('\\')[^1]; }
        catch (SystemException) { return null; }
    }

    private const uint MemberInAlias = 1378;
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref IntPtr members, uint count);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetLocalGroupGetMembers(string? server, string group, uint level, out IntPtr buffer, uint most, out uint read, out uint total, IntPtr resume);
    [DllImport("netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buffer);
}
