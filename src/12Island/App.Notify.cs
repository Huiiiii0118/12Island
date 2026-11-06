using System.IO;
using System.Windows.Threading;
using TwelveIsland.Core;
using TwelveIsland.Native;

namespace TwelveIsland;

// Keeps the notification history and watches the programs that announce messages. One of each for the whole app: every panel
// draws the same list, and a notice read on one panel is read on all of them.
public partial class App
{
    internal NotifyStore? Notices { get; private set; }
    private NotifyWatch? _notifyWatch;
    private TaskbarWatch? _taskbarWatch;
    private string _notifyFolder = "";
    // No timer clears unread notices any more: the capsule stays while anything is unread. Kept only to stop a timer
    // left over from an earlier run when the app exits (StopNotify).
    private readonly Dictionary<string, DispatcherTimer> _noticeTimers = new(StringComparer.Ordinal);

    // Raised when a notice arrives, so a panel shows it on the collapsed island.
    internal event Action<NotifyItem>? NoticeArrived;
    // Raised when the list changed in any way (read, removed, cleared), so open pages draw it again.
    internal event Action? NoticesChanged;

    internal string? MailError => _notifyWatch?.MailError;
    internal DateTimeOffset? MailSeen => _notifyWatch?.MailSeen;
    internal bool MailBusy => _notifyWatch?.MailBusy == true;
    internal bool MailConfigured => Settings.NotifyMail && Settings.NotifyMailUser.Length > 0 && MailSecret.Has;

    // Whether a program's notices are wanted: each group has its own switch.
    internal bool Wanted(NotifySource source) => source switch
    {
        NotifySource.Qq => Settings.NotifyQq,
        NotifySource.WeChat => Settings.NotifyWeChat,
        NotifySource.Mail => Settings.NotifyMail,
        _ => Settings.NotifyApps
    };

    internal void StartNotify()
    {
        if (Notices is null)
        {
            _notifyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "12Island");
            MailSecret.Use(_notifyFolder);
            Notices = new NotifyStore(_notifyFolder);
            Notices.Changed += () => NoticesChanged?.Invoke();
        }
        // The programs' own windows and the notifications Windows keeps for them, plus the mailbox over IMAP.
        bool wanted = Settings.NotifyQq || Settings.NotifyWeChat || Settings.NotifyMail || Settings.NotifyApps;
        if (wanted && _notifyWatch is null)
        {
            _notifyWatch = new NotifyWatch(Dispatcher, () => Settings);
            _notifyWatch.Arrived += NoticesArrived;
            _notifyWatch.MailStateChanged += () => NoticesChanged?.Invoke();
            _notifyWatch.Start();
        }
        else if (!wanted && _notifyWatch is not null)
        {
            _notifyWatch.Arrived -= NoticesArrived;
            _notifyWatch.Dispose(); _notifyWatch = null;
        }
        // The taskbar and the docks flash a program's button when a message waits, which is read where the programs
        // themselves give nothing to read (their popup window, the toast store and the accessibility tree can all be empty).
        // A flash adds an unread notice; only opening the program or reading it on the page clears it.
        if (wanted && _taskbarWatch is null)
        {
            _taskbarWatch = new TaskbarWatch();
            _taskbarWatch.Attention += TaskbarAttention;
            _taskbarWatch.Settled += TaskbarSettled;
            _taskbarWatch.Start();
        }
        else if (!wanted && _taskbarWatch is not null)
        {
            _taskbarWatch.Attention -= TaskbarAttention;
            _taskbarWatch.Settled -= TaskbarSettled;
            _taskbarWatch.Dispose(); _taskbarWatch = null;
        }
        _notifyWatch?.ApplyInterval();
    }

    internal void StopNotify()
    {
        _notifyWatch?.Dispose(); _notifyWatch = null;
        foreach (var timer in _noticeTimers.Values) timer.Stop();
        _noticeTimers.Clear();
        if (_taskbarWatch is null) return;
        _taskbarWatch.Attention -= TaskbarAttention;
        _taskbarWatch.Settled -= TaskbarSettled;
        _taskbarWatch.Dispose(); _taskbarWatch = null;
    }

    // A flash on a watched program's button, in the taskbar or on a dock: a message waits. One unread notice of the program is
    // already showing unless there is none yet; the taskbar cannot say more about the message than "there is one", and a notice
    // window the program put up instead can name the sender. The watch raises this on a thread of its own; the store and the
    // panels live on the UI thread.
    private void TaskbarAttention(NotifyProgram program, string hint) => Dispatcher.BeginInvoke(() =>
    {
        if (Notices is not { } store || Exiting || !Wanted(program.Source)) return;
        // Each new flash is another message: the capsule counts them, even though the taskbar cannot say what they say. The
        // sender a notice window named is used when there is one; otherwise the program's own name, and "something new"
        // under it — nothing is made up to look like the message itself.
        string title = hint.Length > 0 && !hint.Equals(program.Name, StringComparison.OrdinalIgnoreCase)
            ? hint : Loc.T(program.Name, program.Name);
        string body = Loc.T("有新消息", "Something new");
        var added = store.Add(program.Source, program.Key, title, body, Loc.T(program.Name, program.Name), fresh: true);
        if (added is null) return;
        NoticeArrived?.Invoke(added);
    });

    // The flashing stopped: that is not the message being read. The capsule stays while anything is unread, and only
    // opening the program (or reading or clearing it on the page) ends the reminder.
    private void TaskbarSettled(NotifyProgram program) => Dispatcher.BeginInvoke(() =>
    {
        if (Exiting) return;
        CancelTimer(program.Key);
        if (program.Source == NotifySource.Mail) CancelTimer(NotifyPrograms.MailKey);
    });

    private void CancelTimer(string program)
    {
        if (!_noticeTimers.Remove(program, out var timer)) return;
        timer.Stop();
    }

    // The window that announced a program's latest notice: the icon the panel draws is taken from the program owning it, so
    // a program installed anywhere, or one that sets its taskbar icon at run time, shows the icon the user actually sees.
    internal IntPtr NotifyWindow(string program) => _taskbarWatch?.WindowOf(program) ?? IntPtr.Zero;

    // The capsule was clicked: the program that announced the newest unread notice comes up in front. Which of its notices
    // that counted as read is the caller's to say — one notice, not the program's whole list and not everyone's.
    internal bool OpenNotifyApp(string program)
    {
        if (Notices is null) return false;
        if (!AppActivate.Open(program, _taskbarWatch?.WindowOf(program) ?? IntPtr.Zero)) return false;
        CancelTimer(program);
        return true;
    }

    // Asks the mailbox once now, for the page's "立即检查" button.
    internal void CheckMailNow() => _notifyWatch?.PokeMail();

    private void NoticesArrived(IReadOnlyList<NotifyArrival> arrivals)
    {
        if (Notices is null) return;
        bool added = false;
        foreach (var arrival in arrivals)
        {
            if (!Wanted(arrival.Source)) continue;
            if (Notices.Add(arrival.Source, arrival.Program, arrival.Title, arrival.Body, arrival.App) is null) continue;
            added = true;
        }
        if (added && Notices.LatestUnread is { } latest) NoticeArrived?.Invoke(latest);
    }

    // The mailbox password is written where only this user can read it back.
    internal void SetMailPassword(string password) => MailSecret.Write(password ?? "");
}
