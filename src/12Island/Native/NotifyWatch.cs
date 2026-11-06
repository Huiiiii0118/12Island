using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// Runs the sources the notification page shows: the windows of QQ and WeChat, the notifications Windows keeps for those three
// programs, and the mailbox. Everything is polled from the panel's own thread in short steps; the mailbox is asked on the
// thread pool because a server can take seconds to answer. Arrivals are reported on the UI thread.
internal sealed class NotifyWatch : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ToastReader _toasts = new();
    private readonly WindowReader _windows = new();
    private readonly MailReader _mail = new();
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _mailTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly Func<DeckSettings> _settings;
    private bool _mailBusy, _disposed;

    public NotifyWatch(Dispatcher dispatcher, Func<DeckSettings> settings)
    {
        _dispatcher = dispatcher; _settings = settings;
        _fast.Tick += (_, _) => Poll();
        _mailTimer.Tick += (_, _) => PollMail();
    }

    // Notices found, on the UI thread.
    public event Action<IReadOnlyList<NotifyArrival>>? Arrived;
    // The mailbox's state changed (connected, refused, waiting), for the page's status line.
    public event Action? MailStateChanged;
    public string? MailError { get; private set; }
    public DateTimeOffset? MailSeen { get; private set; }
    public bool MailBusy => _mailBusy;

    // Asks the mailbox now, whatever the interval: the page's "check now" button.
    public void PokeMail() { _mailTimer.Stop(); _mailTimer.Start(); PollMail(); }

    public void Start()
    {
        if (_disposed) return;
        ApplyInterval();
        _fast.Start();
        // The first look marks what is already there, so past messages are not reported as new.
        Poll();
        PollMail();
    }

    public void ApplyInterval()
    {
        var settings = _settings();
        int seconds = Math.Clamp(settings.NotifyMailInterval, 15, 3600);
        _mailTimer.Interval = TimeSpan.FromSeconds(seconds);
        if (settings.NotifyMail) { if (!_mailTimer.IsEnabled) _mailTimer.Start(); }
        else _mailTimer.Stop();
    }

    private void Poll()
    {
        if (_disposed) return;
        var settings = _settings();
        var found = new List<NotifyArrival>();
        // The store holds every program's notifications; only those of the programs the page listens to are read out of it.
        try
        {
            foreach (var arrival in _toasts.Poll())
                if (Wanted(settings, arrival.Source)) found.Add(arrival);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        try
        {
            foreach (var arrival in _windows.Poll())
                if (Wanted(settings, arrival.Source)) found.Add(arrival);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (found.Count > 0) Arrived?.Invoke(found);
    }

    // Whether a group of programs is switched on; each has its own switch in the settings.
    private static bool Wanted(DeckSettings settings, NotifySource source) => source switch
    {
        NotifySource.Qq => settings.NotifyQq,
        NotifySource.WeChat => settings.NotifyWeChat,
        NotifySource.Mail => settings.NotifyMail,
        _ => settings.NotifyApps
    };

    // The mailbox is asked only with an address and its password; a look still running keeps the next one waiting. A check
    // without what it needs says so, rather than leaving the page's status line claiming the mailbox is being watched.
    private void PollMail()
    {
        var settings = _settings();
        if (!settings.NotifyMail)
        {
            MailError = Loc.T("邮箱通知未开启，请在设置中打开", "Mail notifications are off; turn them on in settings");
            MailStateChanged?.Invoke();
            return;
        }
        if (settings.NotifyMailUser.Length == 0)
        {
            MailError = Loc.T("尚未填写邮箱地址", "No mailbox address yet");
            MailStateChanged?.Invoke();
            return;
        }
        if (_mailBusy || _disposed) return;
        string password = MailSecret.Read();
        if (password.Length == 0) { MailError = Loc.T("尚未填写授权码", "No password yet"); MailStateChanged?.Invoke(); return; }
        _mailBusy = true; MailStateChanged?.Invoke();
        string host = settings.NotifyMailHost.Length > 0 ? settings.NotifyMailHost : "imap.qq.com";
        int port = settings.NotifyMailPort;
        string user = settings.NotifyMailUser;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<NotifyArrival> found = [];
            string? error = null;
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                found = await _mail.PollAsync(host, port, user, password, cancel.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException
                or OperationCanceledException or InvalidOperationException)
            {
                error = ex is OperationCanceledException ? Loc.T("连接超时", "Timed out") : Loc.T("无法连接邮箱：" + ex.Message, "Mailbox unreachable: " + ex.Message);
            }
            if (_disposed) return;
            await _dispatcher.InvokeAsync(() =>
            {
                _mailBusy = false; MailError = error;
                if (error is null) MailSeen = DateTimeOffset.Now;
                MailStateChanged?.Invoke();
                if (found.Count > 0) Arrived?.Invoke(found);
            });
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fast.Stop(); _mailTimer.Stop();
    }
}
