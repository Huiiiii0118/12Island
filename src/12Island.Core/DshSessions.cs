using System.Text.Json;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace TwelveIsland.Core;

// DeepSeek Harness writes each session to sessions\<project>\<session>\session.v<N>.jsonl.zstd (or .jsonl when compression
// is off): one JSON event per line, appended in batches, each batch one Zstandard frame. Older format generations stay
// next to the current one, so only the highest version in a directory is read. Model requests are the
// assistant/message events: { time (ms), data: { message: { id, source: { model } }, usage: { inputTokens, outputTokens,
// cacheReadTokens, cacheWriteTokens } } }, where inputTokens excludes the cached part. A forked session copies its
// parent's events with their message ids, so records are counted once per message id.
public sealed class DshSessions
{
    private static readonly Regex Generation = new(@"^session(?:\.v(\d+))?\.jsonl(\.zstd)?$", RegexOptions.CultureInvariant);

    // Files only grow at the end. Each one remembers how many bytes of complete frames it has decoded and the start of a
    // line not yet finished, so a refresh decompresses only the frames written since. A frame still being written is
    // left for the next refresh. A shorter or rewritten file, or an earlier start date, is read again from the start.
    private sealed class CacheItem(DateTimeOffset cutoff)
    {
        public readonly DateTimeOffset Cutoff = cutoff;
        public long Length, Offset;
        public DateTime Modified;
        public byte[] Pending = [];
        // A line over the length limit is being dropped up to its end, which may come in a later refresh.
        public bool Skipping, SkippedUsage;
        public readonly List<TokenEntry> Entries = [];
        public int Errors;
    }
    private readonly Dictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);
    // A line longer than this (a huge tool result) is dropped and counted as unreadable instead of growing the buffer without
    // end, as LogReader does (C03).
    private readonly int _maxLine;

    public DshSessions(int maxLine = LogReader.DefaultMaxLine) => _maxLine = Math.Max(1024, maxLine);

    public static string Folder(string home) => Path.Combine(home, "sessions");

    public void Read(ProviderSnapshot snapshot, string home, DateTimeOffset start, DateTimeOffset now)
    {
        var root = Folder(home);
        snapshot.UsageAvailable = Directory.Exists(root);
        if (!snapshot.UsageAvailable) { _cache.Clear(); snapshot.UsageNote = Loc.T("未找到本机 DeepSeek Harness 会话记录。", "No local DeepSeek Harness session records were found."); return; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var current = Directory.EnumerateFiles(root, "session*.jsonl*", options)
            .Select(file => (File: file, Match: Generation.Match(Path.GetFileName(file)))).Where(f => f.Match.Success)
            .GroupBy(f => Path.GetDirectoryName(f.File)!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(f => f.Match.Groups[1].Success ? int.Parse(f.Match.Groups[1].Value) : 0)
                .ThenByDescending(f => File.GetLastWriteTimeUtc(f.File)).First().File);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, TokenEntry>();
        int count = 0, errors = 0;
        foreach (var file in current)
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
                count++; errors += cached.Errors;
                foreach (var entry in cached.Entries)
                    if (entry.Time >= start && entry.Time <= now && entry.Total > 0) entries.TryAdd(entry.Key, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdException) { errors++; _cache.Remove(file); }
        }
        foreach (var gone in _cache.Keys.Where(file => !visited.Contains(file)).ToList()) _cache.Remove(gone);
        snapshot.Entries = entries.Values.OrderBy(e => e.Time).ToList();
        snapshot.UsageComplete = errors == 0;
        snapshot.UsageNote = Loc.T($"本机 DeepSeek Harness {start.LocalDateTime:yyyy-MM-dd} 起 · {count} 个会话 · {snapshot.Entries.Count} 次模型请求",
            $"Local DeepSeek Harness since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(count, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
        if (errors > 0) snapshot.UsageNote += Loc.T($" · {errors} 处记录未能读取", " · " + Loc.Count(errors, "record", "records") + " could not be read");
    }

    private void Parse(string file, CacheItem item, FileInfo info)
    {
        using var stream = SharedFile.Open(file);
        stream.Seek(item.Offset, SeekOrigin.Begin);
        long complete;
        Stream text;
        if (file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
        {
            // Only the compressed bytes are held whole, to find where the complete frames end. More than an array holds (2 GiB)
            // makes this one file unreadable, rather than the whole source (C03).
            long unread = Math.Max(0, stream.Length - item.Offset);
            if (unread > Array.MaxLength) throw new InvalidDataException("The session file is too large to read.");
            var tail = new byte[unread];
            stream.ReadExactly(tail);
            var frames = Frames(tail);
            complete = frames.Count == 0 ? 0 : frames[^1].Start + frames[^1].Length;
            // A first read starts at the batch holding the start date; earlier batches are not decompressed.
            int skip = item.Offset == 0 ? FirstFrameFrom(tail, frames, item.Cutoff, _maxLine) : 0;
            int from = skip < frames.Count ? frames[skip].Start : (int)complete;
            text = new DecompressionStream(new MemoryStream(tail, from, (int)complete - from));
        }
        else { complete = 0; text = stream; }
        using (text)
        {
            // The text is decompressed and split into lines piece by piece; only the token counts are kept.
            var buffer = new byte[Math.Min(_maxLine, Math.Max(1 << 20, item.Pending.Length * 2))];
            item.Pending.CopyTo(buffer, 0);
            int filled = item.Pending.Length, read;
            while ((read = text.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                // A plain file is read to its end; what follows the last line break waits in Pending.
                if (text == stream) complete += read;
                filled += read;
                int lineStart = 0, newline;
                while ((newline = Array.IndexOf(buffer, (byte)'\n', lineStart, filled - lineStart)) >= 0)
                {
                    // The end of a line over the limit: only one that held usage leaves a gap in the statistics, so only that
                    // counts as unreadable; a large tool result is dropped quietly.
                    if (item.Skipping)
                    {
                        if (item.SkippedUsage || buffer.AsSpan(lineStart, newline - lineStart).IndexOf("\"usage\""u8) >= 0) item.Errors++;
                        item.Skipping = item.SkippedUsage = false;
                    }
                    else ParseLine(buffer.AsSpan(lineStart, newline - lineStart), file, item);
                    lineStart = newline + 1;
                }
                Buffer.BlockCopy(buffer, lineStart, buffer, 0, filled - lineStart);
                filled -= lineStart;
                if (filled < buffer.Length) continue;
                // A single line longer than the buffer (a large tool result) grows it instead of being split, up to the limit;
                // beyond it the line is dropped up to its end. Its last bytes are kept, so a "usage" across two dropped pieces is
                // still seen.
                if (buffer.Length < _maxLine) Array.Resize(ref buffer, (int)Math.Min(_maxLine, buffer.Length * 2L));
                else
                {
                    item.SkippedUsage |= buffer.AsSpan(0, filled).IndexOf("\"usage\""u8) >= 0; item.Skipping = true;
                    int keep = "\"usage\""u8.Length - 1;
                    buffer.AsSpan(filled - keep, keep).CopyTo(buffer); filled = keep;
                }
            }
            item.Pending = buffer[..filled];
        }
        item.Offset += complete;
        item.Length = info.Length; item.Modified = info.LastWriteTimeUtc;
    }

    // The complete Zstandard frames at the start of the data, read from the frame and block headers (RFC 8878 section 3.1)
    // without decompressing them.
    private static List<(int Start, int Length)> Frames(ReadOnlySpan<byte> data)
    {
        var frames = new List<(int Start, int Length)>();
        int position = 0;
        while (FrameLength(data[position..]) is > 0 and var length) { frames.Add((position, length)); position += length; }
        return frames;
    }

    // Each batch is a frame that decodes on its own and holds whole lines, and batches are appended in time order.
    // The batch to start from is the last one that begins before the start date, found by decoding only the first
    // line of a few frames. Frames without a readable time (the session header) count as earlier than any date; one whose
    // first line is over the limit counts as later, so the search starts before it rather than past it.
    private static int FirstFrameFrom(byte[] data, List<(int Start, int Length)> frames, DateTimeOffset cutoff, int maxLine)
    {
        int low = 0, high = frames.Count - 1, found = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (FirstTime(data, frames[middle], maxLine) is { } time && time >= cutoff) high = middle - 1;
            else { found = middle; low = middle + 1; }
        }
        return found;
    }

    private static DateTimeOffset? FirstTime(byte[] data, (int Start, int Length) frame, int maxLine)
    {
        using var decoder = new DecompressionStream(new MemoryStream(data, frame.Start, frame.Length));
        var line = new MemoryStream();
        int value;
        while ((value = decoder.ReadByte()) is >= 0 and not '\n')
        {
            // A first line over the limit tells no time; the frame counts as not before any date (FirstFrameFrom), so it is read.
            if (line.Length >= maxLine) return DateTimeOffset.MaxValue;
            line.WriteByte((byte)value);
        }
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            return doc.RootElement.Get("type").Text() == "session" ? null : doc.RootElement.Get("time").Date();
        }
        catch (JsonException) { return null; }
    }

    private static int FrameLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return 0;
        uint magic = BitConverter.ToUInt32(data);
        // A skippable frame is its magic number, a four-byte size and that many bytes.
        if ((magic & 0xFFFFFFF0) == 0x184D2A50)
        {
            long skippable = 8L + BitConverter.ToUInt32(data[4..]);
            return skippable <= data.Length ? (int)skippable : 0;
        }
        if (magic != 0xFD2FB528) throw new InvalidDataException();
        byte descriptor = data[4];
        int contentSizeFlag = descriptor >> 6, dictionaryFlag = descriptor & 3;
        bool singleSegment = (descriptor & 0x20) != 0, checksum = (descriptor & 0x04) != 0;
        int position = 5 + (singleSegment ? 0 : 1) + dictionaryFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 }
            + contentSizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        while (true)
        {
            if (position + 3 > data.Length) return 0;
            int header = data[position] | data[position + 1] << 8 | data[position + 2] << 16;
            bool last = (header & 1) != 0;
            int type = header >> 1 & 3, size = header >> 3;
            if (type == 3) throw new InvalidDataException();
            position += 3 + (type == 1 ? 1 : size);
            if (position > data.Length) return 0;
            if (last) break;
        }
        position += checksum ? 4 : 0;
        return position <= data.Length ? position : 0;
    }

    private static void ParseLine(ReadOnlySpan<byte> line, string file, CacheItem item)
    {
        if (line.IndexOf("\"assistant/message\""u8) < 0 || line.IndexOf("\"usage\""u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            var data = root.Get("data"); var usage = data.Get("usage");
            if (root.Get("type").Text() != "assistant/message" || usage.ValueKind != JsonValueKind.Object || root.Get("time").Date() is not { } time) return;
            var message = data.Get("message");
            var id = message.Get("id").Text() is { Length: > 0 } messageId ? messageId : file + ":" + root.Get("seq").GetRawText();
            item.Entries.Add(new TokenEntry("dsh:" + id, time.ToLocalTime(), message.Get("source").Get("model").Text(),
                usage.Get("inputTokens").Count(), usage.Get("cacheReadTokens").Count(), usage.Get("cacheWriteTokens").Count(), 0, usage.Get("outputTokens").Count()));
        }
        catch (JsonException) { item.Errors++; }
    }
}
