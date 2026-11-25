namespace Fiesta.Bot.Agent;

/// <summary>The bot's economy over time, in one-minute buckets kept for an hour: exp gained, money earned and spent,
/// money spent on soul stones, deaths. Fed by WorldState as the events arrive; read by the brain (econ.*) as rates per
/// hour over the last N minutes. No allocation after construction.</summary>
public sealed class Economy
{
    private const int Minutes = 60;
    private readonly long[] _exp = new long[Minutes], _earn = new long[Minutes], _spend = new long[Minutes], _stones = new long[Minutes];
    private readonly int[] _deaths = new int[Minutes];
    private readonly long[] _bucketMinute = new long[Minutes];
    private long _lastDropAt = -1, _lastDrop;
    private long _startedAt = -1;

    private int Bucket(long now)
    {
        if (_startedAt < 0) _startedAt = now;
        var m = now / 60_000; var i = (int)(m % Minutes);
        if (_bucketMinute[i] != m) { _bucketMinute[i] = m; _exp[i] = _earn[i] = _spend[i] = _stones[i] = 0; _deaths[i] = 0; }
        return i;
    }

    internal void Exp(long amount, long now) { if (amount > 0) _exp[Bucket(now)] += amount; }
    internal void Money(long delta, long now)
    {
        var i = Bucket(now);
        if (delta > 0) _earn[i] += delta;
        else if (delta < 0) { _spend[i] -= delta; _lastDrop = -delta; _lastDropAt = now; }
    }
    /// <summary>A soul stone buy was acked: the money drop just before (or right after) it was spent on stones.</summary>
    internal void StonesBought(long now)
    {
        if (_lastDropAt >= 0 && now - _lastDropAt < 2000) { _stones[Bucket(now)] += _lastDrop; _lastDropAt = -1; }
        else _pendingStones = now;
    }
    private long _pendingStones = -1;
    internal void MoneyAfterStones(long delta, long now)
    {
        if (_pendingStones >= 0 && delta < 0 && now - _pendingStones < 2000) { _stones[Bucket(now)] += -delta; _pendingStones = -1; }
    }
    internal void Death(long now) => _deaths[Bucket(now)]++;

    /// <summary>Per-hour rates over the last <paramref name="minutes"/> minutes (or since the first sample when younger):
    /// exp, money earned minus spent, deaths, money spent on stones.</summary>
    public (double ExpH, double MoneyH, double DeathsH, double StonesH) Rates(int minutes, long now)
    {
        minutes = Math.Clamp(minutes, 1, Minutes);
        var m = now / 60_000;
        long exp = 0, net = 0, stones = 0; int deaths = 0;
        for (var k = 0; k < minutes; k++)
        {
            var i = (int)((m - k) % Minutes);
            if (i < 0 || _bucketMinute[i] != m - k) continue;
            exp += _exp[i]; net += _earn[i] - _spend[i]; stones += _stones[i]; deaths += _deaths[i];
        }
        var hours = Math.Max(1.0 / 60, Math.Min(minutes, _startedAt < 0 ? 1 : (now - _startedAt) / 60_000.0 + 1) / 60.0);
        return (exp / hours, net / hours, deaths / hours, stones / hours);
    }
}
