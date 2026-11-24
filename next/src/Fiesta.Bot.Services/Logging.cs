using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Fiesta.Bot.Services.Logging;

/// <summary>Log levels; a level is enabled when it is at or below the threshold (REDESIGN 16.2).</summary>
public enum LogLevel : byte { Crit = 0, Note = 1, Info = 2, Debug = 3, Trace = 4 }

/// <summary>Log categories, each with its own runtime level per bot.</summary>
public enum LogCategory : byte { Core, Net, Packet, Nav, Combat, Quest, Item, Social, Brain, Script, Prof, Knowledge }

/// <summary>One ring entry: arguments are stored, text is rendered when read (never on the tick).</summary>
public struct LogEntry
{
    /// <summary>Time (ms, process clock).</summary>
    public long At;
    /// <summary>Level.</summary>
    public LogLevel Level;
    /// <summary>Category.</summary>
    public LogCategory Category;
    /// <summary>Generated event id; 0 = free text in <see cref="Text"/>.</summary>
    public ushort EventId;
    /// <summary>Brain layer for script lines.</summary>
    public byte Layer;
    /// <summary>Numeric arguments.</summary>
    public long A0, A1, A2, A3;
    /// <summary>Floating arguments.</summary>
    public double D0, D1;
    /// <summary>Free text (only for enabled free-text lines).</summary>
    public string? Text;
    /// <summary>Collapsed repeats represented by this entry.</summary>
    public int Repeat;
}

/// <summary>Marks a partial method whose body the generator emits as a structured log event.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LogEventAttribute : Attribute
{
    /// <summary>Event id (unique).</summary>
    public ushort Id { get; init; }
    /// <summary>Category.</summary>
    public LogCategory Category { get; init; }
    /// <summary>Level.</summary>
    public LogLevel Level { get; init; }
    /// <summary>Template with {name} placeholders matching the parameters.</summary>
    public string Template { get; init; } = "";
}

/// <summary>Registered event definitions (for rendering entries at read time).</summary>
public sealed record LogEventDef(ushort Id, LogCategory Category, LogLevel Level, string Template, IReadOnlyList<string> Arguments);

/// <summary>Interpolated-string handler that formats only when the category/level is enabled (24 ns, 0 B when off).</summary>
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
    /// <summary>Produce the text.</summary>
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

/// <summary>Per-bot logger: level table, ring, repeat collapsing, rate limit; hands entries to the shared writer.</summary>
public sealed class BotLogger
{
    /// <summary>Create for a bot.</summary>
    public BotLogger(string botId, LogRing ring, LogWriter writer, IClock clock) => throw new NotImplementedException();
    /// <summary>Is (category, level) enabled? Array lookup; no allocation.</summary>
    public bool IsEnabled(LogCategory category, LogLevel level) => throw new NotImplementedException();
    /// <summary>Free-text line; the handler formats only when enabled.</summary>
    public void Log(LogCategory category, LogLevel level,
        [InterpolatedStringHandlerArgument("", "category", "level")] ref LogHandler message) => throw new NotImplementedException();
    /// <summary>Structured event (generated callers).</summary>
    public void Event(in LogEntry entry) => throw new NotImplementedException();
    /// <summary>Set a category's level, optionally reverting after a time.</summary>
    public void SetLevel(LogCategory category, LogLevel level, TimeSpan? expiry = null) => throw new NotImplementedException();
    /// <summary>Current levels with expiry.</summary>
    public IReadOnlyDictionary<LogCategory, (LogLevel Level, DateTime? ExpiresAt)> Levels => throw new NotImplementedException();
    /// <summary>Counters (lines, bytes, dropped, collapsed).</summary>
    public LogCounters Counters => throw new NotImplementedException();
}

/// <summary>Logging counters exported as metrics.</summary>
public sealed record LogCounters(long Lines, long Bytes, long Dropped, long Collapsed);

/// <summary>Fixed-capacity per-bot ring of entries (default 16,384), single writer (the tick), lock-free readers by
/// sequence number.</summary>
public sealed class LogRing
{
    /// <summary>Create with a capacity.</summary>
    public LogRing(int capacity = 16_384) => throw new NotImplementedException();
    /// <summary>Append.</summary>
    public void Append(in LogEntry entry) => throw new NotImplementedException();
    /// <summary>Copy entries matching a filter, newest last; returns the count.</summary>
    public int Read(LogLevel maxLevel, LogCategory? category, long fromMs, long toMs, Span<LogEntry> dest) => throw new NotImplementedException();
}

/// <summary>Collapses repeats of the same (category, event or template) within a window into one line with a count.</summary>
public sealed class RepeatCollapser
{
    /// <summary>Window length.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Returns true when the entry should be emitted now; repeats are counted and flushed later.</summary>
    public bool Admit(ref LogEntry entry, long nowMs) => throw new NotImplementedException();
    /// <summary>Emit closing "x N" entries for windows that ended.</summary>
    public int Flush(long nowMs, Span<LogEntry> dest) => throw new NotImplementedException();
}

/// <summary>Per-bot token bucket for Note/Info and Debug/Trace volumes; Crit always passes.</summary>
public sealed class LogRateLimiter
{
    /// <summary>Lines per minute for Note+Info.</summary>
    public int NoteInfoPerMinute { get; init; } = 300;
    /// <summary>Lines per minute for Debug+Trace.</summary>
    public int DebugPerMinute { get; init; } = 1200;
    /// <summary>Take a token for a level.</summary>
    public bool TryTake(LogLevel level, long nowMs) => throw new NotImplementedException();
}

/// <summary>Process-wide background writer: drains entries from all bots, renders, writes batched, rotates by size,
/// gzips, enforces retention. Never runs on tick or I/O threads.</summary>
public sealed class LogWriter : IAsyncDisposable
{
    /// <summary>Create for a directory.</summary>
    public LogWriter(string directory, LogRetention retention, IReadOnlyDictionary<ushort, LogEventDef> events) => throw new NotImplementedException();
    /// <summary>Queue an entry (drop-oldest when full; counted).</summary>
    public bool TryEnqueue(string botId, in LogEntry entry) => throw new NotImplementedException();
    /// <summary>The queue (for metrics).</summary>
    public ChannelReader<(string Bot, LogEntry Entry)> Queue => throw new NotImplementedException();
    /// <summary>Run the writer loop.</summary>
    public Task RunAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Rotation and retention.</summary>
public sealed record LogRetention(long RotateBytes = 16 << 20, TimeSpan? MaxAge = null, long TotalCapBytes = 1L << 30, bool Gzip = true);

/// <summary>Renders entries to text (with date, level, category, mob names) for files, HTTP and the UI.</summary>
public static class LogRenderer
{
    /// <summary>Render one entry.</summary>
    public static string Render(in LogEntry entry, IReadOnlyDictionary<ushort, LogEventDef> events, Func<int, string?>? mobName) => throw new NotImplementedException();
}

/// <summary>Packet capture settings for one bot (off by default; REDESIGN 16.4).</summary>
public sealed record PacketCaptureSettings(bool Enabled, IReadOnlyList<ushort>? Opcodes, IReadOnlyList<byte>? Depts,
    TimeSpan Duration, long MaxBytes, bool Binary);

/// <summary>Copies matching frames into pooled buffers on the I/O thread and hands them to the writer.</summary>
public sealed class PacketCapture
{
    /// <summary>Apply settings (starts or stops; auto-off after the duration).</summary>
    public void Configure(PacketCaptureSettings settings) => throw new NotImplementedException();
    /// <summary>Called by the link for every frame; cheap when off or filtered.</summary>
    public void OnFrame(bool outbound, ushort opcode, ReadOnlySpan<byte> payload, long at) => throw new NotImplementedException();
    /// <summary>Current settings.</summary>
    public PacketCaptureSettings Settings => throw new NotImplementedException();
}
