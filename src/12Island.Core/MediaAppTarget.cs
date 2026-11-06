namespace TwelveIsland.Core;

// The application that plays the current song: the exact process when a player connection knows it, otherwise only the
// media session's AppUserModelID. Win32 players report their executable ("cloudmusic.exe"), a short name ("Chrome",
// "MSEdge") or an id of their own choosing ("BroNekoX.QueMusic" for QueMusic.exe); packaged applications report
// "PackageFamilyName!App".
public sealed record MediaAppOwner(int? ProcessId, string ProcessName, string AppId)
{
    public string? PackageFamily => AppId.IndexOf('!') is > 0 and var bang ? AppId[..bang] : null;
}

// A top-level window as the window selection sees it, listed from the top of the Z-order down.
public sealed record MediaAppWindow(nint Handle, int ProcessId, string ProcessName, string? PackageFamily, string AppId,
    bool Visible, bool Minimized, bool Tool, bool Owned, bool Cloaked, string Title);

// A process as Windows identifies it: its package family when packaged, and the AppUserModelID Windows resolves for it
// (the packaged application's id, the id the process set for itself, or one Windows derives from its path; "" when unknown).
public sealed record MediaAppProcess(int ProcessId, string ProcessName, string? PackageFamily, string AppId);

public static class MediaAppTarget
{
    // The process name a media session's AppUserModelID names, or "" when it names none. Only an executable's file name or
    // a one-word id is a process name; an id with dots is the application's own choice and says nothing about its
    // executable, so cutting "BroNekoX.QueMusic" at its last dot would look for a "BroNekoX" process. Such applications,
    // and packaged ones, are matched by the id Windows resolves for their processes instead (see Plays and Belongs).
    public static string ProcessName(string appId)
    {
        string lower = appId.ToLowerInvariant();
        if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "cloudmusic";
        if (lower.Contains("qqmusic")) return "QQMusic";
        if (lower.Contains("msedge")) return "msedge";
        if (lower.Contains("chrome")) return "chrome";
        // Apple Music's sound comes from its AMPLibraryAgent.exe, not AppleMusic.exe (measured in the audio sessions); its
        // window is matched by package family.
        if (lower.Contains("applemusic")) return "AMPLibraryAgent";
        if (appId.Contains('!')) return "";
        if (lower.EndsWith(".exe", StringComparison.Ordinal)) return Path.GetFileNameWithoutExtension(appId);
        return appId.Contains('.') || appId.Contains('\\') ? "" : appId;
    }

    // Whether a process with an audio session is the owner's: its known process, a process Windows identifies by the same
    // application id or package, or a process of the executable the id names. Every one of the owner's processes counts,
    // since a player may play from a process other than the one it reports (a browser's audio process, NetEase's own).
    public static bool Plays(MediaAppOwner owner, MediaAppProcess process)
    {
        if (owner.ProcessId is { } pid && process.ProcessId == pid) return true;
        if (owner.AppId.Length > 0 && string.Equals(process.AppId, owner.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (owner.PackageFamily is { } family && string.Equals(process.PackageFamily, family, StringComparison.OrdinalIgnoreCase)) return true;
        return owner.ProcessName.Length > 0 && string.Equals(process.ProcessName, owner.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    // The player's name as the panel shows it: a well-known player's own name, otherwise the name Windows lists the
    // application under (registered, null when it lists none), otherwise its executable's name or its id.
    public static string DisplayName(string appId, Func<string, string?> registered)
    {
        string lower = appId.ToLowerInvariant();
        if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "网易云音乐";
        if (lower.Contains("qqmusic")) return "QQ 音乐";
        if (lower == "kugou") return "酷狗音乐";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("itunes") || lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("qiyi")) return "爱奇艺";
        string id = appId.Split('!')[0];
        if (lower.EndsWith(".exe", StringComparison.Ordinal)) return Path.GetFileNameWithoutExtension(id);
        return registered(appId) is { Length: > 0 } name ? name : id;
    }

    public static bool Belongs(MediaAppOwner owner, MediaAppWindow window)
    {
        if (owner.ProcessId is { } pid) return window.ProcessId == pid;
        if (owner.AppId.Length > 0 && string.Equals(window.AppId, owner.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (owner.PackageFamily is { } family) return string.Equals(window.PackageFamily, family, StringComparison.OrdinalIgnoreCase);
        return owner.ProcessName.Length > 0 && string.Equals(window.ProcessName, owner.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    // The window to bring forward: an ordinary application window (shown or minimized, titled, not owned, not a tool window)
    // of the owner. A window the shell hides on another virtual desktop counts only when there is no other; among the rest
    // the Z-order decides, so the most recently active one wins. Null when the player only has a tray icon left.
    public static MediaAppWindow? Choose(MediaAppOwner owner, IEnumerable<MediaAppWindow> zOrder) =>
        zOrder.Where(w => w.Visible && !w.Tool && !w.Owned && w.Title.Length > 0 && Belongs(owner, w))
            .OrderBy(w => w.Cloaked).FirstOrDefault();
}
