using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fiesta.Bot.LuaBench;

/// <summary>Cost of one log line on the current path (a faithful copy of BotHandle.Log + BotLogFile.Append + the
/// PacketLog hex dump) versus a level-gated, deferred-format call. Mode: `logcost`.</summary>
public static class LogCost
{
    private enum Level { Note, Info, Verbose }

    // ── today's BotHandle.Log ────────────────────────────────────────────────────────────────────────────────────
    private static readonly Regex MobTokenRx = new(@"\bmob(\d{1,6})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Dictionary<int, string> MobNames = new() { [22] = "Marlone", [4002] = "Dungeon Mage Book", [4003] = "Dungeon Marlone Archer" };
    private static readonly List<(Level, string)> Ring = new();
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    private static string Resolve(string message)
    {
        if (message.IndexOf("mob", StringComparison.OrdinalIgnoreCase) < 0) return message;
        return MobTokenRx.Replace(message, m =>
        {
            var id = int.Parse(m.Groups[1].Value);
            return MobNames.TryGetValue(id, out var n) ? $"{n} (Id {id})" : m.Value;
        });
    }

    private static void CurrentLog(Level level, string message)
    {
        var tag = level switch { Level.Verbose => "V", Level.Info => "I", _ => "N" };
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {tag} {Resolve(message)}";
        lock (Gate)
        {
            Ring.Add((level, line));
            if (Ring.Count > 100_000) Ring.RemoveRange(0, 8192);
        }
        _file?.WriteLine(line);
    }

    // ── proposed: level check first, interpolated-string handler that formats only when enabled ──────────────────
    private static Level _enabled = Level.Note;

    [InterpolatedStringHandler]
    public ref struct GatedHandler
    {
        private DefaultInterpolatedStringHandler _inner;
        public readonly bool Enabled;
        public GatedHandler(int literalLength, int formattedCount, int level, out bool enabled)
        {
            enabled = Enabled = level <= (int)_enabled;
            _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }
        public void AppendLiteral(string s) { if (Enabled) _inner.AppendLiteral(s); }
        public void AppendFormatted<T>(T v) { if (Enabled) _inner.AppendFormatted(v); }
        public string ToStringAndClear() => _inner.ToStringAndClear();
    }

    private static void GatedLog(int level, [InterpolatedStringHandlerArgument("level")] ref GatedHandler h)
    {
        if (!h.Enabled) return;
        CurrentLog((Level)level, h.ToStringAndClear());
    }

    // ── today's PacketLog.Write (hex dump, one string per byte via ToString("X2")) ───────────────────────────────
    private static string PacketDump(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var sb = new StringBuilder();
        sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] S->C 0x{opcode:X4} d={opcode >> 10} c={opcode & 0x3FF} len={payload.Length} NC_SOMETHING");
        sb.Append('\n');
        for (int off = 0; off < payload.Length; off += 16)
        {
            int n = Math.Min(16, payload.Length - off);
            sb.Append("  ").Append(off.ToString("X4")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (i < n) sb.Append(payload[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < n; i++) { byte b = payload[off + i]; sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.'); }
            sb.Append("|\n");
        }
        return sb.ToString();
    }

    private static (double Ns, double Bytes) Measure(int n, Action body)
    {
        for (int i = 0; i < 2000; i++) body();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) body();
        sw.Stop();
        return (sw.Elapsed.TotalNanoseconds / n, (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n);
    }

    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "fiesta-logcost.log");
        int x = 11827, y = 10466, ms = 7, wps = 12; var map = "Eld";
        var bigQuest = string.Join(" ", Enumerable.Range(0, 30).Select(i =>
            $"Secret Hideout {i}(q27{i})[s6 5/10 m:mob4002 5/5done,mob4003 0/5] i:item3219<-mob22 0/3"));
        var payload = new byte[200]; new Random(1).NextBytes(payload);

        Console.WriteLine($"{"case",-62} {"ns/line",10} {"B/line",10}");
        void Row(string name, (double Ns, double Bytes) r) => Console.WriteLine($"{name,-62} {r.Ns,10:F0} {r.Bytes,10:F0}");

        _file = null;
        Row("current Log, short Note line, no file", Measure(200_000, () => CurrentLog(Level.Note, $"[nav] route to ({x},{y}) on {map}: {ms}ms off-tick -> {wps} waypoints (Sent)")));
        Row("current Log, Verbose line nobody reads (no level check), no file", Measure(200_000, () => CurrentLog(Level.Verbose, $"[ZoneView] mob {x} (h={y}) running at us - AGGRO")));
        Row("current Log, 3.3 KB status line with mob tokens, no file", Measure(20_000, () => CurrentLog(Level.Verbose, $"[lq] lvl34 {map} ({x},{y}) hp88% | {bigQuest}")));
        _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Row("current Log + BotLogFile (AutoFlush per line), short line", Measure(50_000, () => CurrentLog(Level.Note, $"[nav] route to ({x},{y}) on {map}: {ms}ms off-tick -> {wps} waypoints (Sent)")));
        Row("current Log + BotLogFile, 3.3 KB status line", Measure(10_000, () => CurrentLog(Level.Verbose, $"[lq] lvl34 {map} ({x},{y}) hp88% | {bigQuest}")));
        _file.Dispose(); _file = null;
        _enabled = Level.Note;
        Row("gated handler, Verbose call filtered out", Measure(1_000_000, () => GatedLog(2, $"[ZoneView] mob {x} (h={y}) running at us - AGGRO")));
        Row("gated handler, Note call enabled, no file", Measure(200_000, () => GatedLog(0, $"[nav] route to ({x},{y}) on {map}: {ms}ms off-tick -> {wps} waypoints (Sent)")));
        Row("current PacketLog dump, 200-byte frame (formatting only)", Measure(20_000, () => PacketDump(0x2401, payload)));
        File.Delete(path);
    }
}
