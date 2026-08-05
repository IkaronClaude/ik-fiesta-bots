namespace Fiesta.Bot.Metrics;

/// <summary>One position sample: where the bot was, and when.</summary>
public readonly record struct TracePoint(long T, string Map, int X, int Y);

/// <summary>
/// A rolling trace of where the bot has been — <b>one sample per second</b>, tagged with the map.
/// <para>Deliberately stores RAW points (timestamp + map + coord) rather than a rendered grid, per the
/// operator's preference: <i>"Trace samples as just timestamp + map + coord and then render the heatmap in
/// the browser (javascript) … polling with time since, so I can watch what the bot is doing live and also it
/// takes up less data."</i> A client polls with <c>since</c> and receives only new points, so the live view
/// costs a handful of numbers per second instead of an image.</para>
/// <para>Two retention rules are kept simultaneously, because they answer different questions:
/// <b>recent</b> (&lt;= <see cref="RecentWindow"/>, 30 min) = "where has it been lately", and the full
/// buffer = "where has it been this session" for a decayed all-time heatmap. Age is exposed per point so the
/// browser can apply decay itself without the server picking a curve.</para>
/// </summary>
public sealed class PositionTrace
{
    /// <summary>Max points retained. At 1/s this is ~2.7 hours of continuous movement.</summary>
    public const int Capacity = 10_000;

    /// <summary>The "recent" cutoff — samples older than this are excluded from the recent view.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Queue<TracePoint> _points = new(Capacity);
    private DateTime _lastSampleUtc = DateTime.MinValue;

    /// <summary>Offer the current position. Rate-limits itself to one sample per second, so callers can feed
    /// it from any tick without thinking about cadence (same principle as the metric batching).</summary>
    public void Sample(string? map, int x, int y)
    {
        if (string.IsNullOrEmpty(map)) return;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (now - _lastSampleUtc < SampleEvery) return;
            _lastSampleUtc = now;
            _points.Enqueue(new TracePoint(new DateTimeOffset(now).ToUnixTimeMilliseconds(), map!, x, y));
            while (_points.Count > Capacity) _points.Dequeue();
        }
    }

    /// <summary>Points newer than <paramref name="sinceUnixMs"/> (0 = everything retained), optionally for one
    /// map. This is the polling primitive: the browser passes back the newest <c>t</c> it has seen.</summary>
    public IReadOnlyList<TracePoint> Since(long sinceUnixMs, string? map = null, bool recentOnly = false)
    {
        TracePoint[] all;
        lock (_lock) all = _points.ToArray();
        var floor = recentOnly
            ? new DateTimeOffset(DateTime.UtcNow - RecentWindow).ToUnixTimeMilliseconds()
            : long.MinValue;
        var outp = new List<TracePoint>();
        foreach (var p in all)
        {
            if (p.T <= sinceUnixMs || p.T < floor) continue;
            if (map is not null && !string.Equals(p.Map, map, StringComparison.OrdinalIgnoreCase)) continue;
            outp.Add(p);
        }
        return outp;
    }

    /// <summary>Per-map point counts — a cheap "where has this bot spent its time" summary for the panel.</summary>
    public IReadOnlyDictionary<string, int> MapCounts(bool recentOnly = true)
    {
        var pts = Since(0, null, recentOnly);
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pts) d[p.Map] = d.TryGetValue(p.Map, out var c) ? c + 1 : 1;
        return d;
    }

    /// <summary>Total retained points (for diagnostics).</summary>
    public int Count { get { lock (_lock) return _points.Count; } }
}
