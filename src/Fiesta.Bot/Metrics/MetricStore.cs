using System.Collections.Concurrent;

namespace Fiesta.Bot.Metrics;

/// <summary>Which tail of a metric is the one that matters</summary>
public enum MetricDirection
{
    /// <summary>Higher is healthier (HP, exp/min, kills)</summary>
    HigherIsBetter,
    /// <summary>Lower is healthier (damage taken, deaths, time stunned)</summary>
    LowerIsBetter,
}

/// <summary>How a metric accumulates within its batch window</summary>
public enum MetricKind
{
    /// <summary>A level/reading sampled over time (HP, SP)</summary>
    Gauge,
    /// <summary>An event amount that should add up (damage dealt, exp earned, items picked up)</summary>
    Counter,
}

/// <summary>One recorded sample: a value and when it happened</summary>
public readonly record struct MetricSample(DateTime At, double Value);

/// <summary>Windowed statistics for one metric over one time window</summary>
public sealed record MetricWindow(
    string Window, int Count, double Avg, double StdDev, double Min, double Max,
    double P95, double P99, double Sum, double PerMinute);

/// <summary>A metric's full current picture: its definition plus stats over every configured window</summary>
public sealed record MetricSnapshot(
    string Name, string Direction, string Kind, double? Latest, DateTime? LatestAt,
    IReadOnlyList<MetricWindow> Windows);

public sealed class MetricStore
{
    public const int Capacity = 1000;

    private static readonly (string Label, TimeSpan Span)[] DefaultWindows =
    {
        ("1m", TimeSpan.FromMinutes(1)),
        ("5m", TimeSpan.FromMinutes(5)),
        ("10m", TimeSpan.FromMinutes(10)),
    };

    private sealed class Series
    {
        public required string Name { get; init; }
        public required MetricDirection Direction { get; init; }
        public required MetricKind Kind { get; init; }
        public required TimeSpan Batch { get; init; }
        public readonly object Lock = new();
        public readonly Queue<MetricSample> Samples = new(Capacity);
        // Open batch: accumulating until BatchUntil, then flushed into Samples as one point
        public DateTime BatchUntil;
        public double BatchSum;
        public int BatchCount;
        public double? Latest;
        public DateTime? LatestAt;
    }

    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declare a metric. Safe to call repeatedly (the first definition wins) so call sites can self-register without…</summary>
    public void InitMetric(string name, MetricDirection direction, MetricKind kind = MetricKind.Gauge,
        TimeSpan? batch = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _series.TryAdd(name, new Series
        {
            Name = name,
            Direction = direction,
            Kind = kind,
            Batch = batch ?? TimeSpan.FromMilliseconds(500),
        });
    }

    /// <summary>Record a value. Auto-registers an unknown metric as a HigherIsBetter Gauge so a new call site can never silent…</summary>
    public void LogMetric(string name, double value)
    {
        if (string.IsNullOrWhiteSpace(name) || double.IsNaN(value) || double.IsInfinity(value)) return;
        if (!_series.TryGetValue(name, out var s))
        {
            InitMetric(name, MetricDirection.HigherIsBetter);
            s = _series[name];
        }
        var now = DateTime.UtcNow;
        lock (s.Lock)
        {
            s.Latest = value;
            s.LatestAt = now;
            if (s.BatchCount == 0) s.BatchUntil = now + s.Batch;
            else if (now >= s.BatchUntil) { FlushLocked(s, s.BatchUntil); s.BatchUntil = now + s.Batch; }
            s.BatchSum += value;
            s.BatchCount++;
        }
    }

    /// <summary>Close the open batch into a single sample</summary>
    private static void FlushLocked(Series s, DateTime at)
    {
        if (s.BatchCount == 0) return;
        var v = s.Kind == MetricKind.Counter ? s.BatchSum : s.BatchSum / s.BatchCount;
        s.Samples.Enqueue(new MetricSample(at, v));
        while (s.Samples.Count > Capacity) s.Samples.Dequeue();
        s.BatchSum = 0;
        s.BatchCount = 0;
    }

    /// <summary>Every metric's current snapshot, name-sorted for a stable panel layout</summary>
    public IReadOnlyList<MetricSnapshot> Snapshot(IReadOnlyList<(string Label, TimeSpan Span)>? windows = null)
    {
        var wins = windows ?? DefaultWindows;
        var outp = new List<MetricSnapshot>();
        foreach (var name in _series.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            if (_series.TryGetValue(name, out var s))
                outp.Add(SnapshotOf(s, wins));
        return outp;
    }

    /// <summary>One metric's snapshot, or null if never declared/logged</summary>
    public MetricSnapshot? Snapshot(string name, IReadOnlyList<(string Label, TimeSpan Span)>? windows = null)
        => _series.TryGetValue(name, out var s) ? SnapshotOf(s, windows ?? DefaultWindows) : null;

    private static MetricSnapshot SnapshotOf(Series s, IReadOnlyList<(string Label, TimeSpan Span)> wins)
    {
        MetricSample[] all;
        double? latest;
        DateTime? latestAt;
        lock (s.Lock)
        {
            // Flush the open batch first so a metric that just stopped updating still reports its last partial window — othe…
            FlushLocked(s, DateTime.UtcNow);
            s.BatchUntil = DateTime.UtcNow + s.Batch;
            all = s.Samples.ToArray();
            latest = s.Latest;
            latestAt = s.LatestAt;
        }
        var now = DateTime.UtcNow;
        var windows = new List<MetricWindow>(wins.Count);
        foreach (var (label, span) in wins)
        {
            var cutoff = now - span;
            var vals = new List<double>();
            for (var i = 0; i < all.Length; i++) if (all[i].At >= cutoff) vals.Add(all[i].Value);
            windows.Add(Compute(label, vals, span, s.Direction));
        }
        return new MetricSnapshot(s.Name, s.Direction.ToString(), s.Kind.ToString(), latest, latestAt, windows);
    }

    private static MetricWindow Compute(string label, List<double> vals, TimeSpan span, MetricDirection dir)
    {
        if (vals.Count == 0) return new MetricWindow(label, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var n = vals.Count;
        var sum = 0.0;
        for (var i = 0; i < n; i++) sum += vals[i];
        var avg = sum / n;
        var varSum = 0.0;
        for (var i = 0; i < n; i++) { var d = vals[i] - avg; varSum += d * d; }
        var std = n > 1 ? Math.Sqrt(varSum / (n - 1)) : 0;
        var sorted = vals.ToArray();
        Array.Sort(sorted);
        double p95, p99;
        if (dir == MetricDirection.HigherIsBetter) { p95 = Quantile(sorted, 0.05); p99 = Quantile(sorted, 0.01); }
        else { p95 = Quantile(sorted, 0.95); p99 = Quantile(sorted, 0.99); }
        var perMin = span.TotalMinutes > 0 ? sum / span.TotalMinutes : 0;
        return new MetricWindow(label, n, Round(avg), Round(std), Round(sorted[0]), Round(sorted[^1]),
            Round(p95), Round(p99), Round(sum), Round(perMin));
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static double Round(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
