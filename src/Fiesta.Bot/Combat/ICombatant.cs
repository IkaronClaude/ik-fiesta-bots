using System.Collections.Generic;

namespace Fiesta.Bot.Combat;

/// <summary>Anything that can attack or be attacked: our own character, another player, a mob.
///
/// This is the whole input surface of <see cref="DamageCalculator"/>. To make an existing type usable in
/// combat maths, implement these two members on it — a mob row, a tracked nearby entity, our own
/// character — rather than converting it into some other shape at every call site:
///
/// <code>
/// public sealed partial class MobInfoRow : ICombatant
/// {
///     public int Level =&gt; _level;
///     public CombatStats CombatStats =&gt; _stats ??= CombatStats.FromBaseStats(new Dictionary&lt;Stat, int&gt;
///     {
///         [Stat.Str] = Str, [Stat.WCmin] = MinWc, [Stat.WCmax] = MaxWc, [Stat.AC] = Ac, ...
///     });
/// }
/// </code>
///
/// Cache the <see cref="CombatStats"/> rather than rebuilding it per call: nothing here is recomputed for
/// you, and a damage estimate over many candidate targets will ask for it repeatedly.</summary>
public interface ICombatant
{
    /// <summary>Character level. Damage scales with the ATTACKER's <c>level + 1</c>, so this matters far
    /// more than it looks — it is a direct linear multiplier on every hit dealt.</summary>
    int Level { get; }

    /// <summary>The layered stat container. See <see cref="CombatStats"/> for why this is not a flat map.</summary>
    CombatStats CombatStats { get; }
}

/// <summary>A plain <see cref="ICombatant"/> for tests, estimates and ad-hoc "what if" questions, where
/// there is no game entity to attach the interface to.</summary>
public sealed class Combatant : ICombatant
{
    public Combatant(int level, CombatStats stats)
    {
        Level = level;
        CombatStats = stats;
    }

    /// <summary>A combatant with only base stats — the natural shape for a mob.</summary>
    public Combatant(int level, IEnumerable<KeyValuePair<Stat, int>> baseStats)
        : this(level, CombatStats.FromBaseStats(baseStats))
    {
    }

    public int Level { get; }

    public CombatStats CombatStats { get; }
}
