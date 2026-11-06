using System.Text.Json;

namespace TwelveIsland.Core;

// Kimi Code writes each session to sessions\<work dir>\<session>\agents\<agent>\wire.jsonl, one JSON event per line;
// a subagent writes its calls only to its own file. Every model response that came back with usage writes one record:
// { type: "usage.record", agentId, model: "kimi-code/<model>", usage: { inputOther, output, inputCacheRead,
// inputCacheCreation }, usageScope: "turn" | "session", time (ms) }. The four counts do not overlap. Each record is the
// usage of one call ("session" marks a call outside a turn, such as compaction), so every record is counted, and the
// step.end events, which repeat the same usage, are not. A copied session repeats its records, so a record is counted
// once per agent, time, model and usage.
public sealed class KimiSessions
{
    // Files only grow at the end. Each one remembers where its last complete line ended, so a refresh reads only the lines
    // written since. A shorter or rewritten file, or an earlier start date, is read again from the start.
    private sealed class CacheItem(DateTimeOffset cutoff)
    {
        public readonly DateTimeOffset Cutoff = cutoff;
        public long Length, Offset;
        public DateTime Modified;
        public readonly List<TokenEntry> Entries = [];
        public int Errors;
    }
    private readonly Dictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Requests and the system prompt can make a line very long; a line over this length is skipped without being parsed.
    // A usage record is far shorter, so a skipped line that held one counts as unreadable.
    public const int DefaultMaxLine = 1 << 20;
    private const int Chunk = 64 << 10;
    private readonly int _maxLine;

    public KimiSessions(int maxLine = DefaultMaxLine) => _maxLine = Math.Max(1024, maxLine);

    public static string Folder(string home) => Path.Combine(home, "sessions");

    public void Read(ProviderSnapshot snapshot, string home, DateTimeOffset start, DateTimeOffset now)
    {
        var root = Folder(home);
        snapshot.UsageAvailable = Directory.Exists(root);
        if (!snapshot.UsageAvailable) { _cache.Clear(); snapshot.UsageNote = Loc.T("未找到本机 Kimi Code 会话记录。", "No local Kimi Code session records were found."); return; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(root, "wire.jsonl", options)
            .Where(file => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))) is { } agents && agents.Equals("agents", StringComparison.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, TokenEntry>();
        int errors = 0;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < start.UtcDateTime) continue;
                visited.Add(file);
                if (!_cache.TryGetValue(file, out var cached) || start < cached.Cutoff || info.Length < cached.Length ||
                    info.Length == cached.Length && info.LastWriteTimeUtc != cached.Modified)
                    _cache[file] = cached = new CacheItem(start);
                if (info.Length != cached.Length || info.LastWriteTimeUtc != cached.Modified) Parse(file, cached, info);
                errors += cached.Errors;
                bool any = false;
                foreach (var entry in cached.Entries)
                    if (entry.Time >= start && entry.Time <= now && entry.Total > 0) { entries.TryAdd(entry.Key, entry); any = true; }
                if (any) sessions.Add(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)))!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; _cache.Remove(file); }
        }
        foreach (var gone in _cache.Keys.Where(file => !visited.Contains(file)).ToList()) _cache.Remove(gone);
        snapshot.Entries = entries.Values.OrderBy(e => e.Time).ToList();
        snapshot.UsageComplete = errors == 0;
        snapshot.UsageNote = Loc.T($"本机 Kimi Code {start.LocalDateTime:yyyy-MM-dd} 起 · {sessions.Count} 个会话 · {snapshot.Entries.Count} 次模型请求",
            $"Local Kimi Code since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(sessions.Count, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
        if (errors > 0) snapshot.UsageNote += Loc.T($" · {errors} 处记录未能读取", " · " + Loc.Count(errors, "record", "records") + " could not be read");
    }

    // Reads complete lines after the remembered offset; a line still being written is left for the next refresh. A line over
    // the limit is passed over in pieces: only its end is looked for, and the offset moves past it once that end is found.
    private void Parse(string file, CacheItem item, FileInfo info)
    {
        using var stream = SharedFile.Open(file);
        stream.Seek(item.Offset, SeekOrigin.Begin);
        int capacity = Math.Min(Chunk, _maxLine);
        var buffer = new byte[capacity];
        long skipped = 0;
        bool skipping = false, record = false;
        int filled = 0, read;
        while ((read = stream.Read(buffer, filled, capacity - filled)) > 0)
        {
            filled += read;
            int start = 0, newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start)) >= 0)
            {
                if (skipping)
                {
                    if (record || buffer.AsSpan(start, newline - start).IndexOf(RecordType) >= 0) item.Errors++;
                    skipping = record = false; item.Offset += skipped; skipped = 0;
                }
                else ParseLine(buffer.AsSpan(start, newline - start), file, item);
                start = newline + 1;
            }
            item.Offset += start;
            Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
            filled -= start;
            if (filled < capacity) continue;
            if (capacity < _maxLine)
            {
                capacity = (int)Math.Min(_maxLine, capacity * 2L);
                Array.Resize(ref buffer, capacity);
            }
            else
            {
                // The last bytes stay in the buffer, so a type name split between two reads is still found.
                record |= buffer.AsSpan(0, filled).IndexOf(RecordType) >= 0;
                skipping = true; skipped += filled - KeyTail;
                Buffer.BlockCopy(buffer, filled - KeyTail, buffer, 0, KeyTail);
                filled = KeyTail;
            }
        }
        item.Length = info.Length; item.Modified = info.LastWriteTimeUtc;
    }

    private static ReadOnlySpan<byte> RecordType => "\"usage.record\""u8;
    private const int KeyTail = 13;

    private static void ParseLine(ReadOnlySpan<byte> line, string file, CacheItem item)
    {
        if (line.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) line = line[3..];
        if (line.IndexOf(RecordType) < 0) return;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var obj = doc.RootElement;
            if (obj.Get("type").Text() != "usage.record") return;
            var usage = obj.Get("usage");
            if (usage.ValueKind != JsonValueKind.Object || obj.Get("time").Number() is not { } millis) return;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds((long)millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return; }
            if (time < item.Cutoff) return;
            long input = usage.Get("inputOther").Count(), output = usage.Get("output").Count();
            long read = usage.Get("inputCacheRead").Count(), write = usage.Get("inputCacheCreation").Count();
            var model = obj.Get("model").Text();
            // Older versions write no agentId; the agent's folder names it.
            var agent = obj.Get("agentId").Text() is { Length: > 0 } id ? id : Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            item.Entries.Add(new($"kimi:{agent}:{(long)millis}:{model}:{input}:{read}:{write}:{output}", time,
                KimiUsage.ModelName(model) is { Length: > 0 } name ? name : "unknown", input, read, write, 0, output));
        }
        catch (JsonException) { item.Errors++; }
    }
}
