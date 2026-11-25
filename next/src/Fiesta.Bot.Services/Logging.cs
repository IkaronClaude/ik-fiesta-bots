using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Fiesta.Bot.Services.Logging;

/// <summary>Log levels; a level is enabled when it is at or below the threshold (REDESIGN 16.2).</summary>
public enum LogLevel : byte { Crit = 0, Note = 1, Info = 2, Debug = 3, Trace = 4 }

/// <summary>Log categories, each with its own runtime level per bot.</summary>
public enum LogCategory : byte { Core, Net, Packet, Nav, Combat, Quest, Item, Social, Brain, Script, Prof, Knowledge }

/// <summary>One ring entry: arguments are stored, text is rendered when read (never on the tick).</summary>
public struct LogEntry
{
    /// <summary>Time (ms, process monotonic clock).</summary>
    public long At;
    /// <summary>Level.</summary>
    public LogLevel Level;
    /// <summary>Category.</summary>
    public LogCategory Category;
    /// <summary>Registered event id; 0 = free text in <see cref="Text"/>.</summary>
    public ushort EventId;
    /// <summary>Brain layer for script lines.</summary>
    public byte Layer;
    /// <summary>Numeric arguments.</summary>
    public long A0, A1, A2, A3;
    /// <summary>Floating arguments.</summary>
    public double D0, D1;
    /// <summary>Free text (only for enabled free-text lines).</summary>
    public string? Text;
    /// <summary>Collapsed repeats represented by this entry (0 = a normal line).</summary>
    public int Repeat;
    /// <summary>Sequence number in the ring.</summary>
    public long Seq;
}

/// <summary>Marks a method that emits a structured log event (documentation for the event registry).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LogEventAttribute : Attribute
{
    /// <summary>Event id (unique).</summary>
    public ushort Id { get; init; }
    /// <summary>Category.</summary>
    public LogCategory Category { get; init; }
    /// <summary>Level.</summary>
    public LogLevel Level { get; init; }
    /// <summary>Template with {a0}..{a3}, {d0}, {d1}, {text} placeholders.</summary>
    public string Template { get; init; } = "";
}

/// <summary>Registered event definitions (for rendering entries at read time).</summary>
public sealed record LogEventDef(ushort Id, LogCategory Category, LogLevel Level, string Template, IReadOnlyList<string> Arguments);

/// <summary>The process-wide event registry.</summary>
public static class LogEvents
{
    private static readonly Dictionary<ushort, LogEventDef> Defs = new();
    /// <summary>All definitions.</summary>
    public static IReadOnlyDictionary<ushort, LogEventDef> All => Defs;
    /// <summary>Register an event (startup).</summary>
    public static ushort Define(ushort id, LogCategory c, LogLevel l, string template, params string[] args)
    {
        lock (Defs) Defs[id] = new LogEventDef(id, c, l, template, args);
        return id;
    }
}

/// <summary>Interpolated-string handler that formats only when the category/level is enabled (no allocation when off).</summary>
[InterpolatedStringHandler]
public ref struct LogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    /// <summary>Whether formatting happens.</summary>
    public readonly bool Enabled;
    /// <summary>Compiler-called constructor.</summary>
    public LogHandler(int literalLength, int formattedCount, BotLogger logger, LogCategory category, LogLevel level, out bool enabled)
    {
        enabled = Enabled = logger.IsEnabled(category, level);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }
    /// <summary>Append a literal.</summary>
    public void AppendLiteral(string s) { if (Enabled) _inner.AppendLiteral(s); }
    /// <summary>Append a value.</summary>
    public void AppendFormatted<T>(T value) { if (Enabled) _inner.AppendFormatted(value); }
    /// <summary>Append a value with a format.</summary>
    public void AppendFormatted<T>(T value, string? format) { if (Enabled) _inner.AppendFormatted(value, format); }
    /// <summary>Append a span.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value) { if (Enabled) _inner.AppendFormatted(value); }
    /// <summary>Produce the text.</summary>
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>Per-bot logger: level table, ring, repeat collapsing, rate limit; hands entries to the shared writer.</summary>
public sealed class BotLogger
{
    private readonly LogLevel[] _levels = new LogLevel[16];
    private readonly long[] _expiry = new long[16];
    private readonly LogRing _ring;
    private readonly LogWriter? _writer;
    private readonly IClock _clock;
    private readonly RepeatCollapser _collapse = new();
    private readonly LogRateLimiter _rate = new();
    private long _lines, _bytes, _dropped, _collapsed;
    private readonly LogEntry[] _flush = new LogEntry[8];

    /// <summary>Bot id.</summary>
    public string BotId { get; }
    /// <summary>The ring.</summary>
    public LogRing Ring => _ring;
    /// <summary>Default level for every category.</summary>
    public const LogLevel DefaultLevel = LogLevel.Note;
    /// <summary>Raised for every admitted entry (WebSocket fan-out subscribes; must not block).</summary>
    public event Action<string, LogEntry>? Admitted;

    /// <summary>Create for a bot.</summary>
    public BotLogger(string botId, LogRing ring, LogWriter? writer, IClock clock)
    {
        BotId = botId; _ring = ring; _writer = writer; _clock = clock;
        Array.Fill(_levels, DefaultLevel);
    }

    /// <summary>Is (category, level) enabled? Array lookup; no allocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(LogCategory category, LogLevel level) => level <= _levels[(int)category];

    /// <summary>Free-text line; the handler formats only when enabled.</summary>
    public void Log(LogCategory category, LogLevel level, [InterpolatedStringHandlerArgument("", "category", "level")] ref LogHandler message)
    {
        if (!message.Enabled) return;
        var e = new LogEntry { Level = level, Category = category, Text = message.ToStringAndClear() };
        Event(e);
    }

    /// <summary>Log a plain string (when the caller already checked <see cref="IsEnabled"/> or the text is constant).</summary>
    public void Text(LogCategory category, LogLevel level, string text, byte layer = 0)
    {
        if (!IsEnabled(category, level)) return;
        Event(new LogEntry { Level = level, Category = category, Text = text, Layer = layer });
    }

    /// <summary>Structured or prepared entry.</summary>
    public void Event(in LogEntry entry)
    {
        if (!IsEnabled(entry.Category, entry.Level)) return;
        var e = entry;
        var now = _clock.NowMs;
        e.At = e.At == 0 ? now : e.At;
        ExpireLevels(now);
        var n = _collapse.Flush(now, _flush);
        for (var i = 0; i < n; i++) Admit(_flush[i]);
        if (!_collapse.Admit(ref e, now)) { _collapsed++; return; }
        if (!_rate.TryTake(e.Level, now)) { _dropped++; return; }
        Admit(e);
    }

    private void Admit(in LogEntry e)
    {
        var seq = _ring.Append(e);
        _lines++; _bytes += 32 + (e.Text?.Length ?? 0);
        var copy = e; copy.Seq = seq;
        _writer?.TryEnqueue(BotId, copy);
        Admitted?.Invoke(BotId, copy);
    }

    private void ExpireLevels(long now)
    {
        for (var i = 0; i < _expiry.Length; i++)
            if (_expiry[i] != 0 && now > _expiry[i]) { _expiry[i] = 0; _levels[i] = DefaultLevel; }
    }

    /// <summary>Set a category's level, optionally reverting after a time.</summary>
    public void SetLevel(LogCategory category, LogLevel level, TimeSpan? expiry = null)
    {
        _levels[(int)category] = level;
        _expiry[(int)category] = expiry is { } x ? _clock.NowMs + (long)x.TotalMilliseconds : 0;
    }

    /// <summary>Current levels with expiry.</summary>
    public IReadOnlyDictionary<LogCategory, (LogLevel Level, DateTime? ExpiresAt)> Levels
        => Enum.GetValues<LogCategory>().ToDictionary(c => c, c => (_levels[(int)c], _expiry[(int)c] == 0 ? (DateTime?)null : SystemClock.ToUtc(_expiry[(int)c])));

    /// <summary>Counters (lines, bytes, dropped, collapsed).</summary>
    public LogCounters Counters => new(_lines, _bytes, _dropped, _collapsed);
}

/// <summary>Logging counters exported as metrics.</summary>
public sealed record LogCounters(long Lines, long Bytes, long Dropped, long Collapsed);

/// <summary>Fixed-capacity per-bot ring of entries (default 16,384), single writer, readers copy entries and validate
/// them by sequence number (no lock shared with the writer).</summary>
public sealed class LogRing
{
    private readonly LogEntry[] _e;
    private long _seq;   // next sequence

    /// <summary>Create with a capacity.</summary>
    public LogRing(int capacity = 16_384) => _e = new LogEntry[capacity];
    /// <summary>Capacity.</summary>
    public int Capacity => _e.Length;
    /// <summary>Total appended.</summary>
    public long Count => Volatile.Read(ref _seq);

    /// <summary>Append; returns the sequence number.</summary>
    public long Append(in LogEntry entry)
    {
        var s = _seq;
        ref var slot = ref _e[s % _e.Length];
        slot = entry;
        slot.Seq = s;
        Volatile.Write(ref _seq, s + 1);
        return s;
    }

    /// <summary>Copy entries matching a filter, oldest first; returns the count (at most dest.Length, the newest ones).</summary>
    public int Read(LogLevel maxLevel, LogCategory? category, long fromMs, long toMs, Span<LogEntry> dest)
    {
        var end = Volatile.Read(ref _seq);
        var start = Math.Max(0, end - _e.Length + Math.Min(64, _e.Length / 4));   // keep clear of the slots the writer is about to reuse
        var tmp = new List<LogEntry>();
        for (var s = start; s < end; s++)
        {
            var x = _e[s % _e.Length];
            if (x.Seq != s) continue;
            if (x.Level > maxLevel) continue;
            if (category is { } c && x.Category != c) continue;
            if (x.At < fromMs || x.At > toMs) continue;
            tmp.Add(x);
        }
        var skip = Math.Max(0, tmp.Count - dest.Length);
        var n = 0;
        for (var i = skip; i < tmp.Count; i++) dest[n++] = tmp[i];
        return n;
    }
}

/// <summary>Collapses repeats of the same (category, event or text shape) within a window into one line with a count.</summary>
public sealed class RepeatCollapser
{
    private struct Win { public long Key; public long Start; public int Count; public LogEntry First; }
    private readonly Win[] _w = new Win[32];
    /// <summary>Window length.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);

    private static long KeyOf(in LogEntry e)
    {
        long h = ((long)e.Category << 56) ^ ((long)e.EventId << 40) ^ e.Layer;
        if (e.EventId == 0 && e.Text is { } t)
        {
            // the text's shape: digits ignored, so "x=12" and "x=13" collapse
            ulong f = 1469598103934665603;
            foreach (var ch in t) { if (char.IsDigit(ch)) continue; f = (f ^ ch) * 1099511628211; }
            h ^= (long)f;
        }
        return h;
    }

    /// <summary>Returns true when the entry should be emitted now; repeats are counted and flushed later. Crit lines
    /// collapse too but are never dropped (the count is emitted).</summary>
    public bool Admit(ref LogEntry entry, long nowMs)
    {
        var k = KeyOf(entry);
        var i = (int)((ulong)k % (ulong)_w.Length);
        ref var w = ref _w[i];
        if (w.Count > 0 && w.Key == k && nowMs - w.Start < Window.TotalMilliseconds) { w.Count++; return false; }
        w = new Win { Key = k, Start = nowMs, Count = 1, First = entry };
        return true;
    }

    /// <summary>Emit closing "x N" entries for windows that ended.</summary>
    public int Flush(long nowMs, Span<LogEntry> dest)
    {
        var n = 0;
        for (var i = 0; i < _w.Length && n < dest.Length; i++)
        {
            ref var w = ref _w[i];
            if (w.Count > 1 && nowMs - w.Start >= Window.TotalMilliseconds)
            {
                var e = w.First; e.Repeat = w.Count - 1; e.At = nowMs;
                dest[n++] = e;
                w.Count = 0;
            }
            else if (w.Count == 1 && nowMs - w.Start >= Window.TotalMilliseconds) w.Count = 0;
        }
        return n;
    }
}

/// <summary>Per-bot token bucket for Note/Info and Debug/Trace volumes; Crit always passes.</summary>
public sealed class LogRateLimiter
{
    private double _ni = 50, _dt = 200; private long _last;
    /// <summary>Lines per minute for Note+Info.</summary>
    public int NoteInfoPerMinute { get; init; } = 300;
    /// <summary>Lines per minute for Debug+Trace.</summary>
    public int DebugPerMinute { get; init; } = 1200;
    /// <summary>Take a token for a level.</summary>
    public bool TryTake(LogLevel level, long nowMs)
    {
        if (level == LogLevel.Crit) return true;
        var dt = Math.Max(0, nowMs - _last); _last = nowMs;
        _ni = Math.Min(50, _ni + dt * NoteInfoPerMinute / 60000.0);
        _dt = Math.Min(200, _dt + dt * DebugPerMinute / 60000.0);
        if (level <= LogLevel.Info) { if (_ni < 1) return false; _ni -= 1; return true; }
        if (_dt < 1) return false; _dt -= 1; return true;
    }
}

/// <summary>Rotation and retention.</summary>
public sealed record LogRetention(long RotateBytes = 16 << 20, TimeSpan? MaxAge = null, long TotalCapBytes = 1L << 30, bool Gzip = true);

/// <summary>Process-wide background writer: drains entries from all bots, renders, writes batched, rotates by size,
/// gzips, enforces retention. Never runs on tick or I/O threads.</summary>
public sealed class LogWriter : IAsyncDisposable
{
    private readonly Channel<(string Bot, LogEntry Entry)> _ch = Channel.CreateBounded<(string, LogEntry)>(
        new BoundedChannelOptions(65536) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly string _dir;
    private readonly LogRetention _ret;
    private readonly Dictionary<string, (StreamWriter W, long Bytes, string Path)> _files = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;
    /// <summary>Entries written.</summary>
    public long Written;

    /// <summary>Create for a directory.</summary>
    public LogWriter(string directory, LogRetention retention)
    {
        _dir = directory; _ret = retention;
        Directory.CreateDirectory(directory);
    }
    /// <summary>The directory.</summary>
    public string Directory_ => _dir;
    /// <summary>Queue an entry (drop-oldest when full).</summary>
    public bool TryEnqueue(string botId, in LogEntry entry) => _ch.Writer.TryWrite((botId, entry));
    /// <summary>The queue (for metrics).</summary>
    public ChannelReader<(string Bot, LogEntry Entry)> Queue => _ch.Reader;
    /// <summary>Start the loop.</summary>
    public void Start() => _run ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Run the writer loop.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var lastFlush = Environment.TickCount64; var lastSweep = 0L;
        var sb = new StringBuilder(256);
        while (!ct.IsCancellationRequested)
        {
            try { await _ch.Reader.WaitToReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { break; }
            var crit = false;
            while (_ch.Reader.TryRead(out var x))
            {
                sb.Clear();
                LogRenderer.Render(sb, x.Entry);
                sb.Append('\n');
                var f = File(x.Bot);
                f.W.Write(sb);
                f = (f.W, f.Bytes + sb.Length, f.Path);
                _files[x.Bot] = f;
                Written++;
                if (x.Entry.Level == LogLevel.Crit) crit = true;
                if (f.Bytes > _ret.RotateBytes) Rotate(x.Bot);
            }
            var now = Environment.TickCount64;
            if (crit || now - lastFlush > 1000) { foreach (var f in _files.Values) f.W.Flush(); lastFlush = now; }
            if (now - lastSweep > 600_000) { Sweep(); lastSweep = now; }
        }
        foreach (var f in _files.Values) f.W.Dispose();
    }

    private (StreamWriter W, long Bytes, string Path) File(string bot)
    {
        if (_files.TryGetValue(bot, out var f)) return f;
        var path = Path.Combine(_dir, $"{Safe(bot)}.log");
        var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, 1 << 16);
        f = (w, new FileInfo(path).Length, path);
        _files[bot] = f;
        return f;
    }

    private void Rotate(string bot)
    {
        var f = _files[bot];
        f.W.Dispose();
        _files.Remove(bot);
        var rotated = Path.Combine(_dir, $"{Safe(bot)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        System.IO.File.Move(f.Path, rotated, true);
        if (_ret.Gzip)
        {
            using (var i = System.IO.File.OpenRead(rotated))
            using (var o = new GZipStream(System.IO.File.Create(rotated + ".gz"), CompressionLevel.Fastest))
                i.CopyTo(o);
            System.IO.File.Delete(rotated);
        }
    }

    private void Sweep()
    {
        var age = _ret.MaxAge ?? TimeSpan.FromDays(3);
        var files = new DirectoryInfo(_dir).GetFiles("*.gz").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
            if (DateTime.UtcNow - f.LastWriteTimeUtc > age || total > _ret.TotalCapBytes) { total -= f.Length; try { f.Delete(); } catch (IOException) { } }
    }

    /// <summary>Read the last lines of a bot's current file (survives relogs; used when the ring does not reach back).</summary>
    public IReadOnlyList<string> Tail(string bot, int max)
    {
        var path = Path.Combine(_dir, $"{Safe(bot)}.log");
        if (!System.IO.File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        var q = new Queue<string>();
        while (r.ReadLine() is { } l) { q.Enqueue(l); if (q.Count > max) q.Dequeue(); }
        return q.ToList();
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_run is not null) try { await _run; } catch { }
    }
}

/// <summary>Renders entries to text (with date, level, category) for files, HTTP and the UI.</summary>
public static class LogRenderer
{
    /// <summary>Mob name lookup used at render time (set by the host).</summary>
    public static Func<int, string?>? MobName { get; set; }
    private static readonly string[] Lv = ["CRIT", "NOTE", "INFO", "DBG ", "TRC "];

    /// <summary>Render one entry.</summary>
    public static string Render(in LogEntry entry) { var sb = new StringBuilder(128); Render(sb, entry); return sb.ToString(); }

    /// <summary>Render one entry into a builder.</summary>
    public static void Render(StringBuilder sb, in LogEntry e)
    {
        sb.Append(SystemClock.ToUtc(e.At).ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
          .Append(Lv[(int)e.Level]).Append(' ').Append(e.Category.ToString().ToLowerInvariant()).Append(": ");
        Body(sb, e);
        if (e.Repeat > 0) sb.Append(" ... (x ").Append(e.Repeat).Append(" more in 10 s)");
    }

    /// <summary>The message text alone.</summary>
    public static string Body(in LogEntry e) { var sb = new StringBuilder(64); Body(sb, e); return sb.ToString(); }

    private static void Body(StringBuilder sb, in LogEntry e)
    {
        if (e.EventId == 0 || !LogEvents.All.TryGetValue(e.EventId, out var def)) { sb.Append(e.Text); return; }
        var t = def.Template;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] != '{') { sb.Append(t[i]); continue; }
            var j = t.IndexOf('}', i);
            if (j < 0) { sb.Append(t[i..]); break; }
            var k = t[(i + 1)..j];
            switch (k)
            {
                case "a0": sb.Append(e.A0); break;
                case "a1": sb.Append(e.A1); break;
                case "a2": sb.Append(e.A2); break;
                case "a3": sb.Append(e.A3); break;
                case "d0": sb.Append(e.D0.ToString("0.#")); break;
                case "d1": sb.Append(e.D1.ToString("0.#")); break;
                case "text": sb.Append(e.Text); break;
                case "mob0": sb.Append(MobName?.Invoke((int)e.A0) ?? $"mob {e.A0}").Append(" (").Append(e.A0).Append(')'); break;
                case "mob1": sb.Append(MobName?.Invoke((int)e.A1) ?? $"mob {e.A1}").Append(" (").Append(e.A1).Append(')'); break;
                default: sb.Append('{').Append(k).Append('}'); break;
            }
            i = j;
        }
    }
}

/// <summary>Packet capture settings for one bot (off by default; REDESIGN 16.4).</summary>
public sealed record PacketCaptureSettings(bool Enabled, IReadOnlyList<ushort>? Opcodes, IReadOnlyList<byte>? Depts,
    TimeSpan Duration, long MaxBytes, bool Binary)
{
    /// <summary>Off.</summary>
    public static readonly PacketCaptureSettings Off = new(false, null, null, TimeSpan.FromMinutes(15), 64 << 20, false);
}

/// <summary>Copies matching frames to a per-bot file on a background writer; cheap when off or filtered.</summary>
public sealed class PacketCapture : IDisposable
{
    private readonly string _dir;
    private readonly string _bot;
    private readonly IClock _clock;
    private readonly Channel<(bool Out, ushort Op, byte[] Data, long At)> _ch = Channel.CreateBounded<(bool, ushort, byte[], long)>(
        new BoundedChannelOptions(8192) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile PacketCaptureSettings _s = PacketCaptureSettings.Off;
    private long _until, _bytes;
    private Task? _run;
    /// <summary>Opcode names (FiestaLib registry), set by the host.</summary>
    public static Func<ushort, string?>? OpcodeName { get; set; }

    /// <summary>Create for a bot.</summary>
    public PacketCapture(string dir, string botId, IClock clock) { _dir = dir; _bot = botId; _clock = clock; }
    /// <summary>The file.</summary>
    public string FilePath => Path.Combine(_dir, $"packets-{_bot}.log");
    /// <summary>Current settings.</summary>
    public PacketCaptureSettings Settings => _s;

    /// <summary>Apply settings (starts or stops; auto-off after the duration).</summary>
    public void Configure(PacketCaptureSettings settings)
    {
        _s = settings;
        _until = _clock.NowMs + (long)settings.Duration.TotalMilliseconds;
        _bytes = 0;
        if (settings.Enabled) _run ??= Task.Run(WriteLoop);
    }

    /// <summary>Called by the link for every frame.</summary>
    public void OnFrame(bool outbound, ushort opcode, ReadOnlySpan<byte> payload)
    {
        var s = _s;
        if (!s.Enabled) return;
        var now = _clock.NowMs;
        if (now > _until || _bytes > s.MaxBytes) { _s = s with { Enabled = false }; return; }
        if (s.Opcodes is { Count: > 0 } ops && !ops.Contains(opcode)) return;
        if (s.Depts is { Count: > 0 } d && !d.Contains((byte)(opcode >> 10))) return;
        _bytes += payload.Length + 16;
        _ch.Writer.TryWrite((outbound, opcode, payload.ToArray(), now));
    }

    private async Task WriteLoop()
    {
        Directory.CreateDirectory(_dir);
        await using var w = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        var sb = new StringBuilder(512);
        while (await _ch.Reader.WaitToReadAsync())
        {
            while (_ch.Reader.TryRead(out var f))
            {
                sb.Clear();
                sb.Append(SystemClock.ToUtc(f.At).ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(f.Out ? " C->S " : " S->C ")
                  .Append("0x").Append(f.Op.ToString("X4")).Append(' ').Append(OpcodeName?.Invoke(f.Op) ?? "?")
                  .Append(" len=").Append(f.Data.Length).Append(' ').Append(Convert.ToHexString(f.Data));
                await w.WriteLineAsync(sb);
            }
            await w.FlushAsync();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _ch.Writer.TryComplete();
}
