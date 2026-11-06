using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The unread mail a mailbox holds, asked over IMAP: the first look only notes which messages are already there, so turning the
// mailbox on does not replay the past. Only headers are asked for, never a body or an attachment, and a message is not marked
// read on the server.
internal sealed partial class MailReader
{
    private readonly HashSet<uint> _seen = [];
    private bool _seeded;

    public bool Seeded => _seeded;
    public string? LastError { get; private set; }

    // The messages that are new since the last look. Throws when the mailbox cannot be reached; the caller reports it.
    public async Task<IReadOnlyList<NotifyArrival>> PollAsync(string host, int port, string user, string password, CancellationToken token)
    {
        var found = new List<NotifyArrival>();
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await client.ConnectAsync(host, port, timeout.Token);
        using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        var session = new Session(ssl);
        await session.ReadLineAsync(timeout.Token); // the server's greeting
        await session.CommandAsync("LOGIN " + Quote(user) + " " + Quote(password), timeout.Token);
        await session.CommandAsync("SELECT \"INBOX\"", timeout.Token);
        var search = await session.CommandAsync("UID SEARCH UNSEEN", timeout.Token);
        var uids = ParseSearch(search);
        bool first = !_seeded;
        var fresh = uids.Where(uid => !_seen.Contains(uid)).OrderBy(uid => uid).ToList();
        // At most three at a time: a mailbox with fifty unread messages should not open fifty notifications.
        foreach (var uid in fresh.Take(3))
        {
            var header = await session.CommandAsync($"UID FETCH {uid} (BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])", timeout.Token);
            var (from, subject) = ParseHeader(header);
            if (!first) found.Add(new NotifyArrival(NotifySource.Mail, NotifyPrograms.MailKey, subject.Length > 0 ? subject : Loc.T("新邮件", "New mail"), from, Loc.T("邮箱", "Mail")));
        }
        foreach (var uid in fresh) _seen.Add(uid);
        _seen.IntersectWith(uids);
        _seeded = true;
        try { await session.CommandAsync("LOGOUT", timeout.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return found;
    }

    private static List<uint> ParseSearch(string response)
    {
        var uids = new List<uint>();
        foreach (var line in response.Split('\n'))
        {
            if (!line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in line["* SEARCH".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (uint.TryParse(part, out var uid)) uids.Add(uid);
        }
        return uids;
    }

    // From and Subject out of a header block, decoded from any of the encodings a mailbox may use.
    private static (string From, string Subject) ParseHeader(string block)
    {
        string from = "", subject = "";
        var unfolded = new StringBuilder();
        foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) { if (unfolded.Length > 0) break; continue; }
            if (line[0] is ' ' or '\t' && unfolded.Length > 0) { unfolded.Append(' ').Append(line.Trim()); continue; }
            if (unfolded.Length > 0) { Take(unfolded.ToString()); unfolded.Clear(); }
            unfolded.Append(line);
        }
        if (unfolded.Length > 0) Take(unfolded.ToString());
        void Take(string line)
        {
            if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)) subject = Decode(line["Subject:".Length..].Trim());
            else if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase)) from = Short(Decode(line["From:".Length..].Trim()));
        }
        return (from, subject);
    }

    // "张三 <a@b.c>" becomes "张三"; an address alone keeps its name part.
    private static string Short(string from)
    {
        int angle = from.IndexOf('<');
        if (angle > 0) return from[..angle].Trim().Trim('"');
        if (angle == 0) return from.Trim('<', '>', ' ');
        int at = from.IndexOf('@');
        return at > 0 ? from[..at] : from;
    }

    // A header value as its sender wrote it: "=?UTF-8?B?...?=" and "=?GBK?Q?...?=" are decoded.
    internal static string Decode(string value)
    {
        if (!value.Contains("=?", StringComparison.Ordinal)) return value;
        try
        {
            return EncodedWord().Replace(value, match =>
            {
                string charset = match.Groups[1].Value.Split('*')[0];
                string text = match.Groups[3].Value;
                Encoding encoding;
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = Encoding.UTF8; }
                try
                {
                    return match.Groups[2].Value.ToUpperInvariant() switch
                    {
                        "B" => encoding.GetString(Convert.FromBase64String(text)),
                        _ => encoding.GetString(Unquote(Encoding.ASCII.GetBytes(text.Replace('_', ' '))))
                    };
                }
                catch (FormatException) { return match.Value; }
            }).Replace("?= =?", "");
        }
        catch (ArgumentException) { return value; }
    }

    private static byte[] Unquote(byte[] text)
    {
        var result = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length && Hex(text[i + 1]) is int high && high >= 0 && Hex(text[i + 2]) is int low && low >= 0)
            {
                result.Add((byte)(high * 16 + low)); i += 2;
            }
            else result.Add(text[i]);
        }
        return [.. result];
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1
    };

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=")]
    private static partial Regex EncodedWord();

    // One IMAP session: numbered commands, responses read up to the tag that answered them, literals included.
    private sealed class Session(SslStream stream)
    {
        private readonly List<byte> _buffer = [];
        private int _counter;

        public async Task<string> CommandAsync(string command, CancellationToken token)
        {
            string tag = "A" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bytes = Encoding.UTF8.GetBytes($"{tag} {command}\r\n");
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            return await ReadResponseAsync(tag, token);
        }

        private async Task<string> ReadResponseAsync(string tag, CancellationToken token)
        {
            var text = new StringBuilder();
            while (true)
            {
                string line = await ReadLineAsync(token);
                if (line.Length == 0) continue;
                int literal = LiteralLength(line);
                text.AppendLine(line);
                if (literal > 0)
                {
                    var block = await ReadBlockAsync(literal, token);
                    text.Append(Encoding.UTF8.GetString(block));
                }
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) return text.ToString();
            }
        }

        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            while (true)
            {
                for (int i = 0; i < _buffer.Count - 1; i++)
                {
                    if (_buffer[i] != '\r' || _buffer[i + 1] != '\n') continue;
                    string line = Encoding.UTF8.GetString([.. _buffer.GetRange(0, i)]);
                    _buffer.RemoveRange(0, i + 2);
                    return line;
                }
                await FillAsync(token);
            }
        }

        private async Task<byte[]> ReadBlockAsync(int length, CancellationToken token)
        {
            while (_buffer.Count < length) await FillAsync(token);
            var block = _buffer.GetRange(0, length).ToArray();
            _buffer.RemoveRange(0, length);
            return block;
        }

        private async Task FillAsync(CancellationToken token)
        {
            var chunk = new byte[8192];
            int read = await stream.ReadAsync(chunk, token);
            if (read <= 0) throw new IOException("the mailbox closed the connection");
            _buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        private static int LiteralLength(string line)
        {
            int open = line.LastIndexOf('{');
            int close = line.LastIndexOf('}');
            if (open < 0 || close != line.Length - 1 || close - open is < 2 or > 12) return 0;
            return int.TryParse(line[(open + 1)..close], out int length) ? length : 0;
        }
    }
}
