namespace Fiesta.Bot.Combat;

/// <summary>The per-swing inputs that are not stats: the situational multipliers, plus deterministic
/// overrides for the two random draws.
///
/// <para>All the <c>*RatePermille</c> values are permille and default to 1000 = unchanged, so
/// <see cref="Default"/> describes a plain head-on hit between equals.</para></summary>
public sealed record AttackModifiers
{
    /// <summary>A plain hit: no situational multipliers, both random draws left to chance.</summary>
    public static AttackModifiers Default { get; } = new();

    /// <summary>Where in the weapon's damage range this swing lands, in permille: 0 = minimum,
    /// 1000 = maximum. <c>null</c> draws it randomly.
    ///
    /// <para>Pin it to make a calculation reproducible, or to ask a bounding question — "what is the worst
    /// this mob can hit me for" is <c>RollPermille = 1000</c>, not a simulation.</para></summary>
    public int? RollPermille { get; init; }

    /// <summary><c>true</c> forces a critical, <c>false</c> forbids one, <c>null</c> rolls against
    /// <see cref="CriticalChancePermille"/>.</summary>
    public bool? ForceCritical { get; init; }

    /// <summary>Chance of a critical in permille, used only when <see cref="ForceCritical"/> is
    /// <c>null</c>. A critical doubles the damage before the situational rates are applied.</summary>
    public int CriticalChancePermille { get; init; }

    /// <summary>Skill / attack damage rate. The server's <c>EngageArgument.damagerate</c>.
    ///
    /// <para>Zero is a real value and produces a raw damage of 0, which the final floor turns into 1 — not
    /// an error, and not "unset". It defaults to 1000, never to 0.</para></summary>
    public int DamageRatePermille { get; init; } = 1000;

    /// <summary>Positional multiplier from <c>DamageByAngle</c>: 1000 head-on, up to 1200 from behind.
    /// Index the table with <see cref="DamageCalculator.AngleDamageIndex"/>.</summary>
    public int AngleRatePermille { get; init; } = 1000;

    /// <summary>Level-difference multiplier from <c>DamageLvGap</c>: flat 1000 for monster → player, up to
    /// 1500 for player → monster.</summary>
    public int LevelGapRatePermille { get; init; } = 1000;

    /// <summary>The server's <c>EngageArgument.nBMPDamageRate</c>, applied to attack power inside the core
    /// damage step rather than to the final figure.</summary>
    public int BaseDamageRatePermille { get; init; } = 1000;
}

/// <summary>The result of one resolved swing — the damage plus the intermediates worth logging.
///
/// <para>The intermediates are here so a surprising number can be explained without re-running anything:
/// a hit that looks too small is usually a low <see cref="RollPermille"/> or an unexpectedly high
/// <see cref="DefendPower"/>, and those are the two figures needed to tell which.</para></summary>
public readonly record struct AttackOutcome(
    int Damage,
    bool WasCritical,
    int RollPermille,
    double AttackPower,
    double DefendPower);
