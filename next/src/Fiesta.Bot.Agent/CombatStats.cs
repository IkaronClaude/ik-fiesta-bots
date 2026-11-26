namespace Fiesta.Bot.Agent;

/// <summary>A running mean and spread (Welford).</summary>
public struct RunningStat
{
    /// <summary>Samples.</summary>
    public int N;
    /// <summary>Mean.</summary>
    public double Mean;
    private double _m2;
    /// <summary>Add a sample.</summary>
    public void Add(double x) { N++; var d = x - Mean; Mean += d / N; _m2 += d * (x - Mean); }
    /// <summary>Standard deviation (0 below two samples).</summary>
    public readonly double Sd => N > 1 ? Math.Sqrt(_m2 / (N - 1)) : 0;
}

/// <summary>What our own fighting measures on the wire (per character): damage per skill, swing damage and interval,
/// the spacing between our consecutive casts, the HP a soul stone restores and the gap between two accepted stones. The
/// burst decision reads these; data-derived priors stand in until a value has samples.</summary>
public sealed class CombatStats
{
    private readonly Dictionary<int, RunningStat> _skill = new();
    private readonly Dictionary<int, int> _castOfIndex = new();   // HIT_OBJ_START index -> skill, to pair the damage
    /// <summary>Our swing damage.</summary>
    public RunningStat Swing;
    /// <summary>The time between our swings on one target (ms).</summary>
    public RunningStat SwingInterval;
    /// <summary>The shortest spacing seen between two of our cast starts (ms; the game's global / animation delay is at
    /// most this), -1 none yet.</summary>
    public double CastSpacing { get; private set; } = -1;
    /// <summary>HP a soul stone restored (only uncapped samples: a heal that reached max HP is not counted).</summary>
    public RunningStat StoneHeal;
    /// <summary>The shortest gap seen between two accepted HP stones (ms), -1 none yet.</summary>
    public double StoneGap { get; private set; } = -1;
    /// <summary>When the last accepted HP stone was taken (ms), -1 none.</summary>
    public long LastStoneAt { get; private set; } = -1;
    private long _lastCastAt = -1, _lastSwingAt = -1; private int _lastSwingTarget = -1;
    private long _stoneAt = -1; private double _stoneHpBefore;

    /// <summary>Damage of one skill: (mean, sd, samples).</summary>
    public (double Mean, double Sd, int N) Skill(int skill) => _skill.TryGetValue(skill, out var s) ? (s.Mean, s.Sd, s.N) : (0, 0, 0);
    /// <summary>Measured skills (for the prior scale).</summary>
    public IEnumerable<int> MeasuredSkills => _skill.Keys;

    internal void CastStarted(int skill, int index, long now)
    {
        _castOfIndex[index] = skill;
        if (_lastCastAt >= 0) { var gap = now - _lastCastAt; if (gap > 0 && (CastSpacing < 0 || gap < CastSpacing)) CastSpacing = gap; }
        _lastCastAt = now;
    }
    internal void SkillHit(int index, long damage)
    {
        if (damage <= 0 || !_castOfIndex.TryGetValue(index, out var skill)) return;
        var s = _skill.GetValueOrDefault(skill); s.Add(damage); _skill[skill] = s;
    }
    internal void SwingHit(int target, long damage, long now)
    {
        if (damage > 0) Swing.Add(damage);
        if (_lastSwingTarget == target && _lastSwingAt >= 0) { var iv = now - _lastSwingAt; if (iv > 0) SwingInterval.Add(iv); }
        _lastSwingAt = now; _lastSwingTarget = target;
    }
    internal void StoneAccepted(double hp, long now)
    {
        if (LastStoneAt >= 0) { var gap = now - LastStoneAt; if (gap > 0 && (StoneGap < 0 || gap < StoneGap)) StoneGap = gap; }
        LastStoneAt = now; _stoneAt = now; _stoneHpBefore = hp;
    }
    internal void HpChanged(double hp, double maxHp, long now)
    {
        if (_stoneAt < 0) return;
        if (hp > _stoneHpBefore && hp < maxHp) StoneHeal.Add(hp - _stoneHpBefore);
        if (hp != _stoneHpBefore) _stoneAt = -1;   // the stone's change (or a hit) closes the sample
    }
}
