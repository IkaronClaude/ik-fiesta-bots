namespace Fiesta.Bot.Manager;

/// <summary>
/// The bot's tail, on disk. One rotating file set per bot, flushed after every line.
///
/// <para>⛔ WHY: the log ring buffer lived only in memory, so every deploy, pod restart or respawn
/// destroyed it — and those are precisely the moments you most want to read back what happened. It also
/// spans only ~21 minutes at note density, so even a healthy bot could not answer "what happened last
/// night". Roster, learned knowledge and phase totals already persist; this closes the last gap
/// (operator 2026-08-12).</para>
///
/// <para>Flushed per line ON PURPOSE. A buffered writer loses exactly the tail you need — the lines just
/// before a crash or a SIGTERM — which is the one case this exists for. Cost is an NFS write per line,
/// accepted deliberately.</para>
///
/// <para>Rotation: <c>&lt;id&gt;.log</c> fills to <see cref="MaxLines"/>, then shifts to <c>.1</c>,
/// <c>.2</c> … and the oldest beyond <see cref="MaxFiles"/> is deleted, so a long-running bot cannot fill
/// the claim.</para>
/// </summary>
public sealed class BotLogFile : IDisposable
{
    public const int MaxLines = 10_000;
    public const int MaxFiles = 10;

    private readonly string _dir;
    private readonly string _id;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private int _lines;
    private bool _disposed;

    public BotLogFile(string dir, string id)
    {
        _dir = dir; _id = Sanitize(id);
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Current;
            // Continue an existing file rather than truncating: a respawn inside one pod lifetime should
            // extend the bot's story, not restart it.
            _lines = File.Exists(path) ? CountLines(path) : 0;
            Open();
        }
        catch { _writer = null; }   // logging must never take down the bot
    }

    private string Current => Path.Combine(_dir, $"{_id}.log");
    private string Nth(int n) => Path.Combine(_dir, $"{_id}.log.{n}");

    private void Open() =>
        _writer = new StreamWriter(new FileStream(Current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,   // per-line flush: see the class note
        };

    public void Append(string line)
    {
        if (_disposed) return;
        lock (_gate)
        {
            try
            {
                if (_writer is null) return;
                _writer.WriteLine(line);
                if (++_lines >= MaxLines) Rotate();
            }
            catch { /* never let disk trouble break the bot */ }
        }
    }

    private void Rotate()
    {
        try
        {
            _writer?.Dispose(); _writer = null;
            // Shift downward from the oldest so nothing is overwritten mid-chain, and drop what falls
            // off the end — MaxFiles is the cap on total history, not a per-file cap.
            var oldest = Nth(MaxFiles - 1);
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var n = MaxFiles - 2; n >= 1; n--)
                if (File.Exists(Nth(n))) File.Move(Nth(n), Nth(n + 1), overwrite: true);
            if (File.Exists(Current)) File.Move(Current, Nth(1), overwrite: true);
            _lines = 0;
        }
        catch { }
        finally { try { Open(); } catch { _writer = null; } }
    }

    /// <summary>Read back the most recent <paramref name="max"/> lines for a bot, oldest first, walking
    /// backwards through the rotated set. Used at spawn so the in-memory ring — and therefore every
    /// /log and snapshot endpoint — carries history from BEFORE this process started.</summary>
    public static IReadOnlyList<string> LoadRecent(string dir, string id, int max)
    {
        var res = new List<string>();
        try
        {
            var safe = Sanitize(id);
            // newest file first, then .1, .2 … stopping once we have enough
            var files = new List<string> { Path.Combine(dir, $"{safe}.log") };
            for (var n = 1; n < MaxFiles; n++) files.Add(Path.Combine(dir, $"{safe}.log.{n}"));
            foreach (var f in files)
            {
                if (res.Count >= max) break;
                if (!File.Exists(f)) continue;
                // ⛔ NEVER File.ReadAllLines HERE (fixed 2026-08-18). These rotated files are 1-4.6MB each
                // (100MB total on disk), and reading one whole just to keep its TAIL allocated a string[]
                // of every line — for up to 10 files x 4 bots, all during spawn. That transient spike is
                // what OOMKilled the container 96s after start (exit 137, reason=OOMKilled) and dropped all
                // four bots. Stream instead, keeping only the last `take` lines in a fixed ring.
                var take = max - res.Count;
                var ring = new string[take];
                var n = 0; var total = 0;
                using (var r = new StreamReader(f))
                    while (r.ReadLine() is { } line) { ring[n] = line; n = (n + 1) % take; total++; }
                var have = Math.Min(total, take);
                var start = total <= take ? 0 : n;          // oldest kept line
                var tail = new string[have];
                for (var i = 0; i < have; i++) tail[i] = ring[(start + i) % take];
                // prepend, since we are walking newest file -> oldest
                res.InsertRange(0, tail);
            }
        }
        catch { }
        return res;
    }

    private static int CountLines(string path)
    {
        var n = 0;
        try { using var r = new StreamReader(path); while (r.ReadLine() is not null) n++; } catch { }
        return n;
    }

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; try { _writer?.Dispose(); } catch { } _writer = null; }
    }
}
