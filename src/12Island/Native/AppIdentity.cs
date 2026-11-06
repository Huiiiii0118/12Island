using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TwelveIsland.Core;
using Microsoft.Win32;

namespace TwelveIsland.Native;

// What Windows knows processes and applications by. A media session names its application by an AppUserModelID the
// application may choose freely, so a process is matched by the id Windows resolves for it (MediaAppTarget.Plays) and the
// application is named by the name Windows lists it under, not by anything guessed from the id.
internal static class AppIdentity
{
    private static readonly ConcurrentDictionary<string, string?> Names = new(StringComparer.OrdinalIgnoreCase);
    // The shell's resolver, the one the taskbar groups windows with. It is not a documented interface, so every failure
    // leaves the id unknown and the process is matched by name or package alone. Created per thread: its interface has no
    // proxy to cross apartments.
    [ThreadStatic] private static IApplicationResolver? _resolver;

    // The executable's name is read from the process handle: Process.GetProcessById lists every process on each call
    // (about 12 ms each, measured), and the level meter describes every audio session's process.
    public static MediaAppProcess Describe(int pid)
    {
        string? name = null, family = null, appId = null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle != 0)
        {
            try
            {
                var path = new StringBuilder(1024); int length = path.Capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref length)) name = Path.GetFileNameWithoutExtension(path.ToString());
                family = Read(handle, GetPackageFamilyName); appId = Read(handle, GetApplicationUserModelId);
            }
            finally { CloseHandle(handle); }
        }
        if (name is null)
            try { using var process = Process.GetProcessById(pid); name = process.ProcessName; } catch (ArgumentException) { } catch (InvalidOperationException) { }
        return new(pid, name ?? "", family, appId ?? Resolve(pid));
    }

    // The name Windows lists an application under: the display name it registered with its id, or its Start menu entry's.
    // Null when it has neither; cached, since the Start menu lookup reads the shell's application list.
    public static string? RegisteredName(string appId) => Names.GetOrAdd(appId, Lookup);

    private static string? Lookup(string appId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"AppUserModelId\" + appId);
            if (key?.GetValue("DisplayName") is string { Length: > 0 } registered && Indirect(registered) is { Length: > 0 } name) return name;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, 0, typeof(IShellItem).GUID, out var item) != 0) return null;
        try
        {
            if (item.GetDisplayName(NormalDisplay, out var text) != 0) return null;
            try { return Marshal.PtrToStringUni(text) is { Length: > 0 } listed ? listed : null; }
            finally { Marshal.FreeCoTaskMem(text); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    // A display name may point into a resource ("@%SystemRoot%\system32\shell32.dll,-22067").
    private static string? Indirect(string value)
    {
        if (!value.StartsWith('@')) return value;
        var buffer = new StringBuilder(260);
        return SHLoadIndirectString(value, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : null;
    }

    private static string Resolve(int pid)
    {
        try
        {
            _resolver ??= (IApplicationResolver)Activator.CreateInstance(Type.GetTypeFromCLSID(ResolverClass, true)!)!;
            if (_resolver.GetAppIdForProcess((uint)pid, out var id, out _, out _, out _) != 0 || id == 0) return "";
            try { return Marshal.PtrToStringUni(id) ?? ""; }
            finally { Marshal.FreeCoTaskMem(id); }
        }
        catch (COMException) { return ""; }
        catch (InvalidCastException) { return ""; }
    }

    private delegate int ProcessString(nint process, ref uint length, StringBuilder? text);
    private static string? Read(nint process, ProcessString read)
    {
        uint length = 0;
        if (read(process, ref length, null) != ErrorInsufficientBuffer) return null;
        var buffer = new StringBuilder((int)length);
        return read(process, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    private static readonly Guid ResolverClass = new("660B90C8-73A9-4B58-8CAE-355B7F55341B");
    private const int ErrorInsufficientBuffer = 122;
    private const uint ProcessQueryLimitedInformation = 0x1000, NormalDisplay = 0;
    // Windows 8 and later; only the methods up to the one used are declared, in their order.
    [ComImport, Guid("DE25675A-72DE-44B4-9373-05170450C140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationResolver
    {
        [PreserveSig] int GetAppIdForShortcut(nint item, out nint id);
        [PreserveSig] int GetAppIdForShortcutObject(nint link, nint item, out nint id);
        [PreserveSig] int GetAppIdForWindow(nint window, out nint id, out int pinningPrevented, out int explicitId, out int embeddedShortcut);
        [PreserveSig] int GetAppIdForProcess(uint processId, out nint id, out int pinningPrevented, out int explicitId, out int embeddedShortcut);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(nint process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder output, int capacity, nint reserved);
}
