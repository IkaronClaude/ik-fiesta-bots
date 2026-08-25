using System;
using static Fiesta.Bot.Combat.ServerArithmetic;

namespace Fiesta.Bot.Combat;

/// <summary>Fiesta's damage engine, ported 1:1 from <c>RulesOfEngagement</c> in <c>Zone.exe</c>.
///
/// <para>Everything here was read out of the server binary and is verified against it by differential
/// fuzzing: <c>tools/fuzz_extreme.py</c> drives this code and the real code (under emulation) on identical
/// inputs and requires <b>exact bitwise agreement</b>. Deterministic edge cases live in
/// <c>tests/Fiesta.Bot.Tests/DamageCalculatorTests.cs</c>.</para>
///
/// <para>⚠️ <b>Do not simplify the arithmetic.</b> Operation order, the floors, and the truncation points
/// are all observable in the output. Several of the comments below mark places where the mathematically
/// equivalent rewrite produces a different number.</para>
///
/// <para>Typical use — "how hard does this mob hit me, worst case":</para>
/// <code>
/// var worst = DamageCalculator.Resolve(mob, me, new AttackModifiers
/// {
///     RollPermille = 1000, ForceCritical = true
/// });
/// </code>
///
/// See docs/DAMAGE_FORMULA.md.</summary>
public static class DamageCalculator
{
    // ---- the shared stat chain -------------------------------------------------------------------------

    /// <summary>One stat's value across every modifier layer:
    /// <c>(Base + Item.plus) x four rate halves / 1e12 + five plus halves</c>.
    ///
    /// <para>The rate halves are multiplied in the order AbnormalState, PassiveSkill, ItemPowerRate,
    /// LastTune — the order of the <c>fild</c> sequence at the head of every accessor — before a single
    /// divide. Floating-point multiplication is not associative, so a different order lands an ULP away,
    /// which is invisible until one of the truncating accessors turns it into a whole point.</para></summary>
    private static double Chain(CombatStats s, Stat stat)
    {
        var value = (double)s.Base[stat] + s.Plus(StatModifier.Item)[stat];

        value = value * s.Rate(StatModifier.AbnormalState)[stat]
                      * s.Rate(StatModifier.PassiveSkill)[stat]
                      * s.Rate(StatModifier.ItemPowerRate)[stat]
                      * s.Rate(StatModifier.LastTune)[stat] / RateDivisor;

        // Five SEPARATE additions, in this order, exactly as the binary's fild/fadd pairs do. Folding them
        // into one sum is wrong twice over: with `int` operands it overflows int32 before promoting, and
        // even in double it re-associates — above 2^53, where the gap between representable doubles is 2,
        // pre-summing lands one ULP out (1841195746 against the server's 1841195744).
        value += s.Plus(StatModifier.Upgrade)[stat];
        value += s.Plus(StatModifier.WeaponTitle)[stat];
        value += s.Plus(StatModifier.PassiveSkill)[stat];
        value += s.Plus(StatModifier.AbnormalState)[stat];
        value += s.Plus(StatModifier.LastTune)[stat];
        return value;
    }

    /// <summary>The chain over the stat that GOVERNS an accessor — Str for weapon damage, Con for armour,
    /// Dex for hit and block, Men for magic resistance — floored at 1.
    ///
    /// <para>The floor belongs here and not on the accessor's own half: with an empty container every
    /// accessor returns exactly 1, and flooring both halves would return 2.</para></summary>
    private static double GoverningChain(CombatStats s, Stat governing) => FloorAtOne(Chain(s, governing));

    // ---- defensive accessors ---------------------------------------------------------------------------

    /// <summary>Armour class — physical damage reduction. The server's <c>roe_AC</c>.
    ///
    /// <para>Its first trailing rate is applied INSIDE the truncation and the second outside, which is why
    /// the result is not always a whole number: at (AbnormalState, ItemPowerRate) = (500, 7) it returns
    /// 3.5. Verified at four rate pairs.</para></summary>
    public static double ArmourClass(ICombatant defender)
    {
        var s = defender.CombatStats;
        var sum = GoverningChain(s, Stat.Con) + Chain(s, Stat.AC);
        var truncated = Ftol32(ApplyRate(sum, s.Rate(StatModifier.AbnormalState)[Stat.AC]));
        return FloorAtOne(ApplyRate(truncated, s.Rate(StatModifier.ItemPowerRate)[Stat.AC]));
    }

    /// <summary>Magic resistance. The server's <c>roe_MR</c>.
    ///
    /// <para>Unlike <see cref="ArmourClass"/> its rate is applied OUTSIDE the truncation — with a sum of
    /// 1000.6 and a rate of 3000, AC gives 3001 and MR gives 3000. Asymmetric, but measured.</para></summary>
    public static double MagicResistance(ICombatant defender)
    {
        var s = defender.CombatStats;
        var truncated = Ftol32(GoverningChain(s, Stat.Men) + Chain(s, Stat.MR));
        return FloorAtOne(ApplyRate(truncated, s.Rate(StatModifier.ItemPowerRate)[Stat.MR]));
    }

    /// <summary>Block rating. The server's <c>roe_TB</c>. Truncates its sum; <see cref="ToHitRating"/> does not.</summary>
    public static double ToBlockRating(ICombatant defender)
    {
        var s = defender.CombatStats;
        return FloorAtOne(Ftol32(GoverningChain(s, Stat.Dex) + Chain(s, Stat.TB)));
    }

    // ---- offensive accessors ---------------------------------------------------------------------------

    /// <summary>Hit rating. The server's <c>roe_TH</c>. Applies no trailing rate and does NOT truncate,
    /// which makes it the cleanest probe of the shared chain.</summary>
    public static double ToHitRating(ICombatant attacker)
    {
        var s = attacker.CombatStats;
        return FloorAtOne(GoverningChain(s, Stat.Dex) + Chain(s, Stat.TH));
    }

    /// <summary>Bottom of the weapon damage range. The server's <c>roe_MinWC</c>.</summary>
    public static double MinWeaponDamage(ICombatant attacker) => WeaponDamage(attacker.CombatStats, Stat.WCmin);

    /// <summary>Top of the weapon damage range. The server's <c>roe_MaxWC</c>.</summary>
    public static double MaxWeaponDamage(ICombatant attacker) => WeaponDamage(attacker.CombatStats, Stat.WCmax);

    /// <summary>The weapon-damage accessors, which share a shape but differ only in which slot they read.</summary>
    private static double WeaponDamage(CombatStats s, Stat bound)
    {
        // The Str chain and the five own terms accumulate in ONE running total. The six additions are a
        // single fld plus five fadds in the binary and that association is observable: computing the own
        // half separately and adding the Str chain last gives 7732.317384936001 where the server gives
        // 7732.317384936. One ULP -- which then feeds attack power and an integer damage, where it became
        // a 256-point difference.
        //
        // Upgrade.plus and AbnormalState.plus are read at WCmax even when computing WCmin, and that is
        // not a quirk of the code -- it is what the stat MEANS. An enhanced weapon reads in the client as
        //
        //     1000~2000 (+3000)
        //
        // one flat bonus that shifts BOTH ends of the range, not a separate bump to each end. So the
        // enhancement and buff layers store that single figure once, in the WCmax slot, and both bounds
        // read it from there. Only the weapon's own base range is per-bound.
        var value = GoverningChain(s, Stat.Str);
        value += s.Plus(StatModifier.Upgrade)[Stat.WCmax];
        value += s.Plus(StatModifier.AbnormalState)[Stat.WCmax];
        value += s.Base[bound];
        value += ScaledWeaponItemBonus(s, bound);
        value += s.Plus(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery];

        // Three trailing rates, multiplying and dividing at each step rather than taking the product
        // first. Scored 397/400 exact against the server where the alternative groupings scored 221 and 208.
        value = ApplyRate(value, s.Rate(StatModifier.AbnormalState)[Stat.WCmax]);
        value = ApplyRate(value, s.Rate(StatModifier.ItemPowerRate)[bound]);
        value = ApplyRate(value, s.Rate(StatModifier.PassiveSkill)[bound]);
        return FloorAtOne(value);
    }

    /// <summary>The weapon's own damage bonus is NOT added raw: it is scaled by the weapon title's rate for
    /// the slot and then by physical weapon mastery.</summary>
    private static double ScaledWeaponItemBonus(CombatStats s, Stat bound)
    {
        //     fld WeaponTitle.rate.WCmin; fmul Item.plus.WCmin; fdiv 1000; fmul PassiveSkill.rate.Mastery
        // Written out rather than via ApplyRate because the roles are reversed here -- the weapon-title
        // RATE is the multiplicand and the item PLUS is the multiplier -- and expressing that as
        // "apply a rate" would read as the opposite of what it does.
        var scaled = (double)s.Rate(StatModifier.WeaponTitle)[bound] * s.Plus(StatModifier.Item)[bound] / 1000.0;
        return ApplyRate(scaled, s.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery]);
    }

    // ---- attack / defend power -------------------------------------------------------------------------

    /// <summary>Physical attack power: a point in the weapon's damage range, scaled by weapon mastery.
    ///
    /// <para><paramref name="rollPermille"/> selects the point: 0 = <see cref="MinWeaponDamage"/>,
    /// 1000 = <see cref="MaxWeaponDamage"/>. The server draws an INTEGER over the truncated range, so the
    /// roll is quantised, not continuous.</para>
    ///
    /// <para><b>The mastery multiplier is not floored.</b> With a physical-weapon-mastery rate of 0 this
    /// returns exactly 0 even though both bounds are floored at 1 — no mastery means no damage, and the
    /// floors on the bounds do not rescue it.</para></summary>
    public static double AttackPower(ICombatant attacker, int rollPermille)
    {
        var s = attacker.CombatStats;
        var low = MinWeaponDamage(attacker);
        var high = MaxWeaponDamage(attacker);

        // The range goes through _ftol because the server's RNG takes an int.
        var range = (long)Ftol32(high - low);
        var draw = range * rollPermille / 1000;

        return ApplyRate(low + draw, s.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery]);
    }

    /// <summary>Physical defend power. The server's <c>NormalPY::roe_DefendPower</c> IS <c>roe_AC</c>.</summary>
    public static double DefendPower(ICombatant defender) => ArmourClass(defender);

    // ---- the damage pipeline ---------------------------------------------------------------------------

    /// <summary>The core damage step, <c>roe_Damage</c>, byte for byte:
    /// <code>
    /// v = (nBMPDamageRate * attack) / 1000.0;
    /// if (v &lt;= 0) v = 1.0;
    /// return ((attackerLevel + 1) * v) / defend;
    /// </code>
    /// The only literals in the real function are 1000.0 and 1.0 — there is no magic constant anywhere in
    /// it, and any fitted one (an earlier attempt produced <c>K / (DEF - 141)</c>) is an artefact of
    /// fitting rather than reading.
    ///
    /// <para>Note this floor really is <c>&lt;= 0</c>, unlike the accessors' <c>&lt; 1</c>.</para></summary>
    public static double CoreDamage(double attackPower, double defendPower, int attackerLevel,
                                    int baseDamageRatePermille = 1000)
    {
        var v = baseDamageRatePermille * attackPower / 1000.0;
        if (v <= 0) v = 1.0;
        return (attackerLevel + 1) * v / defendPower;
    }

    /// <summary>Resolve one physical swing to an integer damage figure.
    ///
    /// <para>Both random draws — where in the weapon range the swing lands, and whether it crits — are
    /// taken from <paramref name="rng"/> unless <paramref name="modifiers"/> pins them. Pass a seeded
    /// <see cref="Random"/> for a reproducible simulation, or pin both for a bounding question.</para></summary>
    public static AttackOutcome Resolve(ICombatant attacker, ICombatant defender,
                                        AttackModifiers? modifiers = null, Random? rng = null)
    {
        var mods = modifiers ?? AttackModifiers.Default;
        rng ??= Random.Shared;

        var rollPermille = mods.RollPermille ?? rng.Next(0, 1001);
        var isCritical = mods.ForceCritical ?? rng.Next(0, 1000) < mods.CriticalChancePermille;

        var attackPower = AttackPower(attacker, rollPermille);
        var defendPower = DefendPower(defender);

        var damage = CoreDamage(attackPower, defendPower, attacker.Level, mods.BaseDamageRatePermille);
        if (isCritical) damage *= 2.0;

        // ANGLE FIRST, THEN DAMAGE RATE -- the order the binary uses at roe_CalcDamage+0x572..+0x585:
        //     fild angleRate; fmul damage; fdiv 1000; fild damagerate; fmulp; fdivp
        // Both are doubles applied BEFORE the integer conversion, so this ordering is the only thing that
        // separates the port from the server here, and it is at most an ULP -- but ULPs on the wrong side
        // of the conversion have already cost a whole point of armour once in this file.
        damage = ApplyRate(damage, mods.AngleRatePermille);
        damage = ApplyRate(damage, mods.DamageRatePermille);

        // The LEVEL GAP is deliberately NOT applied here: on the server it runs AFTER the integer
        // conversion, as an integer function. See below.

        // The final conversion is the same wrapping _ftol as the accessors use, NOT a saturating cast: a
        // damage of 8.5e12 comes back as its low 32 bits, which is negative and so floors to 1. A plain
        // (int)Math.Floor gave 2147483647 -- "maximum possible hit" where the server deals the minimum.
        var final = (int)Ftol32(damage);
        final = ApplyLevelGap(final, mods.LevelGapRatePermille);
        return new AttackOutcome(final > 0 ? final : 1, isCritical, rollPermille, attackPower, defendPower);
    }

    /// <summary>`roe_LevelGapDamageRevision` — the level-difference multiplier, applied to the INTEGER
    /// damage after the conversion, not to the double before it.
    ///
    /// <para>The server does <c>imul ecx, damage</c> (a 32-bit multiply, so it WRAPS) followed by a signed
    /// divide by 1000 truncating toward zero — the <c>0x10624DD3</c> / <c>sar 6</c> magic sequence at
    /// <c>roe_LevelGapDamageRevision+0x62</c>. C#'s <c>int * int</c> wraps and <c>/</c> truncates toward
    /// zero, so this is the same operation, not an approximation of it.</para>
    ///
    /// <para>The server only reaches this for specific combatant-type pairs (player attacking a monster
    /// selects <c>LevelGap_Player_to_Monster</c>); other pairings leave the damage untouched, which is a
    /// rate of 1000 here.</para></summary>
    private static int ApplyLevelGap(int damage, int ratePermille) => unchecked(ratePermille * damage) / 1000;

    /// <summary>Convenience for the common question: how much damage, ignoring the breakdown.</summary>
    public static int ResolveDamage(ICombatant attacker, ICombatant defender,
                                    AttackModifiers? modifiers = null, Random? rng = null)
        => Resolve(attacker, defender, modifiers, rng).Damage;

    /// <summary>Two degrees per direction unit. The server never works in degrees: <c>ddt_Initialize</c>
    /// builds its direction table with <c>atan(...) * 90 / PI</c>, and degrees would be <c>* 180 / PI</c>,
    /// so one unit is half a degree's worth — a full turn is <see cref="DirectionUnitsPerTurn"/>.</summary>
    public const int DegreesPerDirectionUnit = 2;

    /// <summary>A full turn in direction units (360 degrees / 2).</summary>
    public const int DirectionUnitsPerTurn = 180;

    /// <summary>Fold a facing difference, <b>in direction units</b>, into the 0..90 index that
    /// <c>DamageByAngle</c>'s 91-entry table expects.
    ///
    /// <para>The server computes the argument as <c>defenderFacing - directionFromDefenderToAttacker</c>,
    /// both direction bytes, then indexes the table with it. So the index is a measure of how far round
    /// the defender the attacker is:</para>
    ///
    /// <list type="table">
    ///   <item><term>index 0</term><description>0 degrees — attacked from the FRONT</description></item>
    ///   <item><term>index 45</term><description>90 degrees — from the SIDE</description></item>
    ///   <item><term>index 90</term><description>180 degrees — from BEHIND, the largest multiplier</description></item>
    /// </list>
    ///
    /// <para>⚠️ This takes DIRECTION UNITS, not degrees. Passing degrees folds 180 to index 0 and makes a
    /// backstab read as a frontal hit — which is the inverse of the truth, and is what this method did
    /// while its parameter was misnamed <c>angleDegrees</c>. Use
    /// <see cref="AngleDamageIndexFromDegrees"/> if you are holding degrees.</para></summary>
    public static int AngleDamageIndex(int directionUnitDelta)
    {
        var folded = Math.Abs(directionUnitDelta) % DirectionUnitsPerTurn;
        return Math.Abs(((folded + 90) % DirectionUnitsPerTurn) - 90);
    }

    /// <summary>As <see cref="AngleDamageIndex"/>, for a caller holding real degrees (0 = front,
    /// 180 = behind).</summary>
    public static int AngleDamageIndexFromDegrees(int degrees)
        => AngleDamageIndex(degrees / DegreesPerDirectionUnit);
}
