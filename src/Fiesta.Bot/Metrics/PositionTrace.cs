namespace Fiesta.Bot.Metrics;

/// <summary>One position sample: where the bot was, and when</summary>
public readonly record struct TracePoint(long T, string Map, int X, int Y);

/// <summary>A rolling trace of where the bot has been — one sample per second , tagged with the map</summary>
public sealed class PositionTrace
{
    /// <summary>Max points retained. At 1/s this is ~2.7 hours of continuous movement</summary>
    public const int Capacity = 10_000;

    /// <summary>The "recent" cutoff — samples older than this are excluded from the recent view</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Queue<TracePoint> _points = new(Capacity);
    private DateTime _lastSampleUtc = DateTime.MinValue;

    /// <summary>Offer the current position</summary>
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

    /// <summary>Points newer than (0 = everything retained), optionally for one map</summary>
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

    /// <summary>Per-map point counts — a cheap "where has this bot spent its time" summary for the panel</summary>
    public IReadOnlyDictionary<string, int> MapCounts(bool recentOnly = true)
    {
        var pts = Since(0, null, recentOnly);
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pts) d[p.Map] = d.TryGetValue(p.Map, out var c) ? c + 1 : 1;
        return d;
    }

    /// <summary>Total retained points (for diagnostics)</summary>
    public int Count { get { lock (_lock) return _points.Count; } }
}
