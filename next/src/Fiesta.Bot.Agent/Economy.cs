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
    // when the ledger began (local ms; negative for a ledger restored from before this process started - so 'not begun'
    // is a flag, never a sentinel value: a restored start below 0 used to read as unset and the rates were taken over a
    // minute, 98040 exp/h right after a restart)
    private long _startedAt;
    private bool _begun;

    private int Bucket(long now)
    {
        if (!_begun) { _startedAt = now; _begun = true; }
        var m = now / 60_000; var i = (int)(((m % Minutes) + Minutes) % Minutes);
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

    // ---- per mob type, learned as we play: what a kill costs (time, stones) and pays (exp); deaths by it
    /// <summary>What we measured against one mob type.</summary>
    public struct MobCost
    {
        /// <summary>Kills, total fight ms over them, stones used in them, exp they paid, deaths by this type.</summary>
        public int Kills; public long FightMs; public int Stones; public long Exp; public int Deaths;
    }
    private readonly Dictionary<int, MobCost> _mobs = new();
    private int _fightMob = -1; private long _fightStart = -1;
    private long _questExp;

    /// <summary>A mob type's measured numbers (all 0 when never fought).</summary>
    public MobCost Mob(int mobId) => _mobs.GetValueOrDefault(mobId);
    /// <summary>Exp that came with no kill (quest rewards), in the last-hour buckets' total.</summary>
    public long QuestExp => _questExp;

    /// <summary>Our first hit on a mob starts a fight against its type (the next stones are charged to it).</summary>
    internal void FightStart(int mobId, long now) { if (_fightMob != mobId || _fightStart < 0) { _fightMob = mobId; _fightStart = now; } }
    internal void Killed(int mobId, long firstHitAt, long now)
    {
        if (mobId < 0) return;
        var c = _mobs.GetValueOrDefault(mobId);
        c.Kills++; if (firstHitAt >= 0) c.FightMs += now - firstHitAt;
        _mobs[mobId] = c;
        if (_fightMob == mobId) { _fightMob = -1; _fightStart = -1; }
    }
    internal void StoneUsed() { if (_fightMob >= 0) { var c = _mobs.GetValueOrDefault(_fightMob); c.Stones++; _mobs[_fightMob] = c; } }
    internal void KillExp(int mobId, long exp) { if (mobId >= 0) { var c = _mobs.GetValueOrDefault(mobId); c.Exp += exp; _mobs[mobId] = c; } }
    internal void OtherExp(long exp) { _questExp += exp; }
    internal void DiedTo(int mobId) { if (mobId >= 0) { var c = _mobs.GetValueOrDefault(mobId); c.Deaths++; _mobs[mobId] = c; } }

    /// <summary>Per-hour rates over the last <paramref name="minutes"/> minutes (or since the first sample when younger):
    /// exp, money earned minus spent, deaths, money spent on stones.</summary>
    public (double ExpH, double MoneyH, double DeathsH, double StonesH) Rates(int minutes, long now)
    {
        minutes = Math.Clamp(minutes, 1, Minutes);
        var m = now / 60_000;
        long exp = 0, net = 0, stones = 0; int deaths = 0;
        for (var k = 0; k < minutes; k++)
        {
            // minutes before this process started are negative (a restored ledger): index them modulo, never skip them
            var i = (int)((((m - k) % Minutes) + Minutes) % Minutes);
            if (_bucketMinute[i] != m - k) continue;
            exp += _exp[i]; net += _earn[i] - _spend[i]; stones += _stones[i]; deaths += _deaths[i];
        }
        var hours = Math.Max(1.0 / 60, Math.Min(minutes, !_begun ? 1 : (now - _startedAt) / 60_000.0 + 1) / 60.0);
        return (exp / hours, net / hours, deaths / hours, stones / hours);
    }

    // ---- persistence across sessions and host restarts (times as Unix minutes / ms)
    /// <summary>The ledger as plain data: the last hour's buckets keyed by Unix minute, the per-mob numbers, quest exp.</summary>
    public sealed record Saved(long StartedUnixMs, long QuestExp, List<long[]> Buckets, Dictionary<int, long[]> Mobs);

    /// <summary>Export for saving (<paramref name="unixNow"/> is the wall clock matching <paramref name="now"/>).</summary>
    public Saved Export(long now, long unixNow)
    {
        var shift = unixNow / 60_000 - now / 60_000;
        var buckets = new List<long[]>();
        for (var i = 0; i < Minutes; i++)
            if (_exp[i] != 0 || _earn[i] != 0 || _spend[i] != 0 || _stones[i] != 0 || _deaths[i] != 0)
                buckets.Add([_bucketMinute[i] + shift, _exp[i], _earn[i], _spend[i], _stones[i], _deaths[i]]);
        var mobs = _mobs.ToDictionary(kv => kv.Key, kv => new long[] { kv.Value.Kills, kv.Value.FightMs, kv.Value.Stones, kv.Value.Exp, kv.Value.Deaths });
        return new Saved(!_begun ? -1 : _startedAt + (unixNow - now), _questExp, buckets, mobs);
    }

    /// <summary>Restore a saved ledger (buckets older than an hour are dropped).</summary>
    public void Import(Saved s, long now, long unixNow)
    {
        var shift = unixNow / 60_000 - now / 60_000;
        var m = now / 60_000;
        foreach (var b in s.Buckets)
        {
            if (b.Length < 6) continue;
            var local = b[0] - shift;
            if (local <= m - Minutes || local > m) continue;
            var i = (int)(((local % Minutes) + Minutes) % Minutes);
            _bucketMinute[i] = local; _exp[i] = b[1]; _earn[i] = b[2]; _spend[i] = b[3]; _stones[i] = b[4]; _deaths[i] = (int)b[5];
        }
        foreach (var (id, v) in s.Mobs)
            if (v.Length >= 5) _mobs[id] = new MobCost { Kills = (int)v[0], FightMs = v[1], Stones = (int)v[2], Exp = v[3], Deaths = (int)v[4] };
        _questExp = s.QuestExp;
        if (s.StartedUnixMs >= 0) { _startedAt = Math.Max(now - 3_600_000, s.StartedUnixMs - (unixNow - now)); _begun = true; }
    }
}
