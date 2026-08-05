using System.Collections.Concurrent;

namespace Fiesta.Bot.Metrics;

/// <summary>Which tail of a metric is the one that matters. Drives whether the reported percentiles are the
/// LOW tail (for things you want high — HP, exp rate) or the HIGH tail (for things you want low — damage
/// taken, deaths). Without this a "p95" is ambiguous and the panel shows the uninteresting end.</summary>
public enum MetricDirection
{
    /// <summary>Higher is healthier (HP, exp/min, kills). The LOW percentiles are the warning signs.</summary>
    HigherIsBetter,
    /// <summary>Lower is healthier (damage taken, deaths, time stunned). The HIGH percentiles are the warnings.</summary>
    LowerIsBetter,
}

/// <summary>How a metric accumulates within its batch window.</summary>
public enum MetricKind
{
    /// <summary>A level/reading sampled over time (HP, SP). Batched samples are AVERAGED.</summary>
    Gauge,
    /// <summary>An event amount that should add up (damage dealt, exp earned, items picked up). Batched
    /// samples are SUMMED, so "log 30 damage" three times in a batch window records 90, not 30.</summary>
    Counter,
}

/// <summary>One recorded sample: a value and when it happened.</summary>
public readonly record struct MetricSample(DateTime At, double Value);

/// <summary>Windowed statistics for one metric over one time window.</summary>
public sealed record MetricWindow(
    string Window, int Count, double Avg, double StdDev, double Min, double Max,
    double P95, double P99, double Sum, double PerMinute);

/// <summary>A metric's full current picture: its definition plus stats over every configured window.</summary>
public sealed record MetricSnapshot(
    string Name, string Direction, string Kind, double? Latest, DateTime? LatestAt,
    IReadOnlyList<MetricWindow> Windows);

/// <summary>
/// A tiny in-memory metrics engine, one per bot: <c>InitMetric</c> once, <c>LogMetric</c> freely, then read
/// windowed stats for a live "stat panel" view of the bot.
/// <para>The point (operator 2026-08-05): <i>"a window into everything going on with the bot — like a stat
/// panel, you look at it and immediately know where the bot is."</i></para>
/// <para><b>Batching is what makes the write API safe to call anywhere.</b> Callers log at whatever rate
/// their code happens to run at — a 400ms Lua tick, a per-packet handler, a burst of 20 hits in one second —
/// and everything inside a metric's batch window collapses to ONE sample (averaged for a Gauge, summed for a
/// Counter). So the ring buffer measures TIME, not caller frequency, and nobody has to think about tick
/// rates. Without it a chatty caller would flush the buffer in seconds and a quiet one would span hours,
/// making the same "1m avg" mean completely different things per metric.</para>
/// <para>Thread-safe: packets arrive on the session read loop while HTTP reads the snapshot.</para>
/// </summary>
public sealed class MetricStore
{
    /// <summary>Ring capacity per metric. At the default 500ms batch this is ~8 minutes of dense history,
    /// and far more for sparse event metrics — enough for the 1m/5m/10m windows below with headroom.</summary>
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
        // Open batch: accumulating until BatchUntil, then flushed into Samples as one point.
        public DateTime BatchUntil;
        public double BatchSum;
        public int BatchCount;
        public double? Latest;
        public DateTime? LatestAt;
    }

    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declare a metric. Safe to call repeatedly (the first definition wins) so call sites can
    /// self-register without a central list.</summary>
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

    /// <summary>Record a value. Auto-registers an unknown metric as a HigherIsBetter Gauge so a new call site
    /// can never silently drop data just because someone forgot to InitMetric it.</summary>
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

    /// <summary>Close the open batch into a single sample. A Gauge averages (it is a level), a Counter sums
    /// (it is an amount) — averaging a counter would silently under-report bursts.</summary>
    private static void FlushLocked(Series s, DateTime at)
    {
        if (s.BatchCount == 0) return;
        var v = s.Kind == MetricKind.Counter ? s.BatchSum : s.BatchSum / s.BatchCount;
        s.Samples.Enqueue(new MetricSample(at, v));
        while (s.Samples.Count > Capacity) s.Samples.Dequeue();
        s.BatchSum = 0;
        s.BatchCount = 0;
    }

    /// <summary>Every metric's current snapshot, name-sorted for a stable panel layout.</summary>
    public IReadOnlyList<MetricSnapshot> Snapshot(IReadOnlyList<(string Label, TimeSpan Span)>? windows = null)
    {
        var wins = windows ?? DefaultWindows;
        var outp = new List<MetricSnapshot>();
        foreach (var name in _series.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            if (_series.TryGetValue(name, out var s))
                outp.Add(SnapshotOf(s, wins));
        return outp;
    }

    /// <summary>One metric's snapshot, or null if never declared/logged.</summary>
    public MetricSnapshot? Snapshot(string name, IReadOnlyList<(string Label, TimeSpan Span)>? windows = null)
        => _series.TryGetValue(name, out var s) ? SnapshotOf(s, windows ?? DefaultWindows) : null;

    private static MetricSnapshot SnapshotOf(Series s, IReadOnlyList<(string Label, TimeSpan Span)> wins)
    {
        MetricSample[] all;
        double? latest;
        DateTime? latestAt;
        lock (s.Lock)
        {
            // Flush the open batch first so a metric that just stopped updating still reports its last
            // partial window — otherwise a freshly-logged value is invisible for up to one batch period.
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
        // Report the tail that MATTERS for this metric's direction: for HigherIsBetter the bad case is the
        // LOW end ("95% of the time HP was at least X"), for LowerIsBetter it is the HIGH end ("95% of the
        // time damage taken was at most X"). Reporting the wrong tail makes the panel look fine while the
        // thing you care about is the other end of the distribution.
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
