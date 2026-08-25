using System;
using System.Collections.Generic;
using Fiesta.Bot.Combat;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>Edge cases that pin the damage engine's integer boundaries.
///
/// The primary evidence is the differential fuzz in <c>tools/fuzz_extreme.py</c>, which runs this code and
/// the REAL <c>RulesOfEngagement</c> code from Zone.exe (under Unicorn) on the same inputs and requires
/// exact bitwise agreement. That needs Windows, the server binary and a Unicorn install, so it cannot run
/// in CI. These tests are the residue: every law the fuzz discovered, as a case that fails if it breaks.
///
/// Every expected value here was READ FROM THE ORACLE, not derived by hand. Where a number looks arbitrary
/// it is because the server is arbitrary — see docs/DAMAGE_FORMULA.md.</summary>
public class DamageCalculatorTests
{
    /// <summary>A combatant with no modifiers at all — every rate neutral, every plus zero.</summary>
    private static Combatant Blank(int level = 61) => new(level, CombatStats.Unmodified());

    private static Combatant With(Action<CombatStats> configure, int level = 61)
    {
        var stats = CombatStats.Unmodified();
        configure(stats);
        return new Combatant(level, stats);
    }

    // ---- floors -------------------------------------------------------------------------------------

    [Fact]
    public void EmptyCombatant_EveryAccessorReturnsOne_NotTwo()
    {
        // The floor lives on the governing chain only. Flooring both halves would give 2.
        var c = Blank();
        DamageCalculator.ArmourClass(c).ShouldBe(1.0);
        DamageCalculator.MagicResistance(c).ShouldBe(1.0);
        DamageCalculator.ToHitRating(c).ShouldBe(1.0);
        DamageCalculator.ToBlockRating(c).ShouldBe(1.0);
        DamageCalculator.MinWeaponDamage(c).ShouldBe(1.0);
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(1.0);
    }

    [Fact]
    public void GoverningChainFloor_IsLessThanOne_NotLessThanOrEqualToZero()
    {
        // governing = 0.5 (Men 5 scaled by a PassiveSkill rate of 100/1000), own half = 10.
        // A `<= 0` floor leaves the governing chain at 0.5 and gives 10.5; the server returns 11.
        var mr = With(s =>
        {
            s.Base[Stat.Men] = 5;
            s.Base[Stat.MR] = 10;
            s.Rate(StatModifier.PassiveSkill)[Stat.Men] = 100;
        });
        DamageCalculator.MagicResistance(mr).ShouldBe(11.0);

        // ToHitRating is decisive: it does not truncate its sum, so 11.0 cannot be a rounded 10.5.
        var th = With(s =>
        {
            s.Base[Stat.Dex] = 5;
            s.Base[Stat.TH] = 10;
            s.Rate(StatModifier.PassiveSkill)[Stat.Dex] = 100;
        });
        DamageCalculator.ToHitRating(th).ShouldBe(11.0);
    }

    [Fact]
    public void ResultFloor_RaisesAPositiveFraction_ToOne()
    {
        // WCmax 600 plus the floored governing chain (1) sums to 601. At an AbnormalState rate of 1 that
        // scales to 0.601 and the server returns 1.0; at 2 it is 1.202 and the server returns it unchanged.
        // So the floor is `< 1`, not `<= 0` — a positive fraction is raised, a value above 1 is not.
        var stats = CombatStats.Unmodified();
        stats.Base[Stat.WCmax] = 600;
        var c = new Combatant(61, stats);

        stats.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 1;
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(1.0);

        stats.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 2;
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(1.202, 1e-9);

        stats.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 3;
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(1.803, 1e-9);
    }

    // ---- truncation ---------------------------------------------------------------------------------

    /// <summary>Three of six accessors discard the fraction of their summed value and three do not.
    /// Measured by feeding a sum of exactly 2001.2.</summary>
    [Theory]
    [InlineData("AC", 2001.0)]
    [InlineData("MR", 2001.0)]
    [InlineData("TB", 2001.0)]
    [InlineData("TH", 2001.2)]
    public void SumTruncation_AppliesToArmourResistanceAndBlock_ButNotHit(string which, double expected)
    {
        // governing 10006 * 100/1000 = 1000.6 ; own 10006 * 100/1000 = 1000.6 ; sum 2001.2
        var governing = which switch { "AC" => Stat.Con, "MR" => Stat.Men, _ => Stat.Dex };
        var own = which switch { "AC" => Stat.AC, "MR" => Stat.MR, "TB" => Stat.TB, _ => Stat.TH };

        var c = With(s =>
        {
            s.Base[governing] = 10006;
            s.Rate(StatModifier.PassiveSkill)[governing] = 100;
            s.Plus(StatModifier.Item)[own] = 10006;
            s.Rate(StatModifier.LastTune)[own] = 100;
        });

        var actual = which switch
        {
            "AC" => DamageCalculator.ArmourClass(c),
            "MR" => DamageCalculator.MagicResistance(c),
            "TB" => DamageCalculator.ToBlockRating(c),
            _ => DamageCalculator.ToHitRating(c)
        };
        actual.ShouldBe(expected, 1e-9);
    }

    /// <summary>Armour applies its first trailing rate INSIDE the truncation, magic resistance applies its
    /// rate OUTSIDE. Asymmetric, but that is what the binary does — with a sum of 1000.6 and a rate of
    /// 3000, armour gives 3001 and resistance gives 3000.</summary>
    [Fact]
    public void TrailingRatePosition_DiffersBetweenArmourAndResistance()
    {
        var ac = With(s =>
        {
            s.Base[Stat.Con] = 10006;
            s.Rate(StatModifier.PassiveSkill)[Stat.Con] = 100;   // governing = 1000.6
            s.Rate(StatModifier.AbnormalState)[Stat.AC] = 3000;
        });
        DamageCalculator.ArmourClass(ac).ShouldBe(3001.0);       // trunc(1000.6 * 3) — rate INSIDE

        var mr = With(s =>
        {
            s.Base[Stat.Men] = 10006;
            s.Rate(StatModifier.PassiveSkill)[Stat.Men] = 100;   // governing = 1000.6
            s.Rate(StatModifier.ItemPowerRate)[Stat.MR] = 3000;
        });
        DamageCalculator.MagicResistance(mr).ShouldBe(3000.0);   // trunc(1000.6) * 3 — rate OUTSIDE
    }

    [Fact]
    public void ArmourClass_CanReturnANonIntegerResult_WhenTheSecondRateShrinksIt()
    {
        // The second rate is applied after the truncation, so it reintroduces a fraction.
        var c = With(s =>
        {
            s.Base[Stat.Con] = 10006;
            s.Rate(StatModifier.PassiveSkill)[Stat.Con] = 100;
            s.Rate(StatModifier.AbnormalState)[Stat.AC] = 500;
            s.Rate(StatModifier.ItemPowerRate)[Stat.AC] = 7;
        });
        DamageCalculator.ArmourClass(c).ShouldBe(3.5, 1e-9);
    }

    /// <summary>The truncation is a WRAPPING <c>_ftol</c>, not a saturating cast: a summed value just over
    /// 2^32 comes back as its low 32 bits.</summary>
    [Theory]
    [InlineData(7, 6.0)]        // sum 4294967302 -> low 32 bits = 6
    [InlineData(100, 99.0)]     // sum 4294967395 -> low 32 bits = 99
    public void Truncation_KeepsOnlyTheLow32Bits(int tail, double expected)
    {
        var c = With(s =>
        {
            s.Plus(StatModifier.Upgrade)[Stat.AC] = int.MaxValue;
            s.Plus(StatModifier.WeaponTitle)[Stat.AC] = int.MaxValue;
            s.Plus(StatModifier.PassiveSkill)[Stat.AC] = tail;
        });
        DamageCalculator.ArmourClass(c).ShouldBe(expected);
    }

    // ---- the minimised regression that found the governing floor ------------------------------------

    [Fact]
    public void MinimisedResistanceCase_GoverningChainOfPointNineNineNine_FloorsUpToOne()
    {
        // Shrunk by tools/minimise_case.py from a 40-field fuzz failure to these four fields.
        // governing = (-1 * 1/1000) + 1 = 0.999 -> floored to 1 ; own = 1 ; sum = 2.
        var c = With(s =>
        {
            s.Base[Stat.Men] = -1;
            s.Base[Stat.MR] = 1;
            s.Plus(StatModifier.AbnormalState)[Stat.Men] = 1;
            s.Rate(StatModifier.AbnormalState)[Stat.Men] = 1;
        });
        DamageCalculator.MagicResistance(c).ShouldBe(2.0);
    }

    // ---- attack power -------------------------------------------------------------------------------

    [Fact]
    public void AttackPower_IsZero_WhenPhysicalWeaponMasteryRateIsZero()
    {
        // The mastery multiplier is NOT floored: no mastery means no damage, even though both weapon
        // bounds are floored at 1.
        var c = With(s =>
        {
            s.Base[Stat.WCmin] = 100;
            s.Base[Stat.WCmax] = 200;
            s.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery] = 0;
        });
        DamageCalculator.MinWeaponDamage(c).ShouldBe(101.0);   // the bounds are unaffected...
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(201.0);
        DamageCalculator.AttackPower(c, 1000).ShouldBe(0.0);   // ...but the attack power is not
    }

    [Theory]
    [InlineData(0, 101.0)]
    [InlineData(500, 151.0)]
    [InlineData(1000, 201.0)]
    public void AttackPower_InterpolatesAcrossTheWeaponRange(int rollPermille, double expected)
    {
        var c = With(s =>
        {
            s.Base[Stat.WCmin] = 100;
            s.Base[Stat.WCmax] = 200;
        });
        DamageCalculator.AttackPower(c, rollPermille).ShouldBe(expected);
    }

    // ---- the damage pipeline ------------------------------------------------------------------------

    [Fact]
    public void CoreDamage_ScalesWithAttackerLevelPlusOne()
    {
        // roe_Damage(1569, 242) at level 61 reproduces the closed form bit-exactly.
        DamageCalculator.CoreDamage(1569.0, 242.0, 61).ShouldBe(62 * 1569.0 / 242.0);
    }

    [Fact]
    public void Resolve_NeverReturnsLessThanOneDamage()
    {
        // A damage rate of 0 makes the raw damage 0; the server floors the final integer at 1.
        var outcome = DamageCalculator.Resolve(Blank(), Blank(),
            new AttackModifiers { RollPermille = 500, ForceCritical = false, DamageRatePermille = 0 });
        outcome.Damage.ShouldBe(1);
    }

    [Fact]
    public void Resolve_CriticalDoublesTheDamage()
    {
        var attacker = With(s =>
        {
            s.Base[Stat.WCmin] = 400;
            s.Base[Stat.WCmax] = 400;
        });
        var normal = DamageCalculator.Resolve(attacker, Blank(),
            new AttackModifiers { RollPermille = 500, ForceCritical = false });
        var crit = DamageCalculator.Resolve(attacker, Blank(),
            new AttackModifiers { RollPermille = 500, ForceCritical = true });

        crit.Damage.ShouldBe(normal.Damage * 2);
        crit.WasCritical.ShouldBeTrue();
        normal.WasCritical.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ForceCriticalNull_UsesTheChanceRoll()
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 400; s.Base[Stat.WCmax] = 400; });

        // 1000 permille = always; 0 = never. Both must hold for every seed.
        for (var seed = 0; seed < 20; seed++)
        {
            DamageCalculator.Resolve(attacker, Blank(),
                new AttackModifiers { RollPermille = 0, CriticalChancePermille = 1000 },
                new Random(seed)).WasCritical.ShouldBeTrue();

            DamageCalculator.Resolve(attacker, Blank(),
                new AttackModifiers { RollPermille = 0, CriticalChancePermille = 0 },
                new Random(seed)).WasCritical.ShouldBeFalse();
        }
    }

    [Fact]
    public void Resolve_WithASeededRandom_IsReproducible()
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 100; s.Base[Stat.WCmax] = 900; });
        var mods = new AttackModifiers { CriticalChancePermille = 300 };

        var first = DamageCalculator.Resolve(attacker, Blank(), mods, new Random(1234));
        var second = DamageCalculator.Resolve(attacker, Blank(), mods, new Random(1234));
        second.ShouldBe(first);
    }

    [Fact]
    public void Resolve_ReportsTheIntermediates_SoASurprisingNumberCanBeExplained()
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 100; s.Base[Stat.WCmax] = 900; });
        var defender = With(s => s.Base[Stat.AC] = 250);

        var outcome = DamageCalculator.Resolve(attacker, defender,
            new AttackModifiers { RollPermille = 250, ForceCritical = false });

        outcome.RollPermille.ShouldBe(250);
        outcome.AttackPower.ShouldBe(DamageCalculator.AttackPower(attacker, 250));
        outcome.DefendPower.ShouldBe(DamageCalculator.DefendPower(defender));
    }

    // ---- angle table --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(90, 90)]
    [InlineData(180, 0)]
    [InlineData(270, 90)]
    [InlineData(360, 0)]
    [InlineData(-45, 45)]
    [InlineData(-135, 45)]
    public void AngleDamageIndex_FoldsToASymmetricTriangleWave(int angle, int expected)
        => DamageCalculator.AngleDamageIndex(angle).ShouldBe(expected);

    // ---- the model itself ---------------------------------------------------------------------------

    [Fact]
    public void Unmodified_LeavesEveryRateNeutral_SoStatsPassThroughUnchanged()
    {
        var stats = CombatStats.Unmodified();
        foreach (var source in Enum.GetValues<StatModifier>())
        {
            stats.Rate(source)[Stat.Str].ShouldBe(CombatStats.NeutralRate);
            stats.Plus(source)[Stat.Str].ShouldBe(0);
        }
    }

    [Fact]
    public void FromBaseStats_IsEnoughToDescribeAMob()
    {
        // A mob's MobInfoServer / MobWeapon row IS its base block, so a dictionary is the whole input.
        var mob = new Combatant(30, new Dictionary<Stat, int>
        {
            [Stat.Str] = 120, [Stat.Con] = 90, [Stat.Dex] = 60,
            [Stat.WCmin] = 40, [Stat.WCmax] = 70, [Stat.AC] = 55,
        });

        // Str chain (120, above the floor so untouched) + WCmin 40 / WCmax 70. Both read from the oracle.
        DamageCalculator.MinWeaponDamage(mob).ShouldBe(160.0);
        DamageCalculator.MaxWeaponDamage(mob).ShouldBe(190.0);
        DamageCalculator.ResolveDamage(mob, Blank(),
            new AttackModifiers { RollPermille = 0, ForceCritical = false }).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Clone_DoesNotShareBlocksWithTheOriginal()
    {
        var original = CombatStats.Unmodified();
        original.Base[Stat.Str] = 100;

        var copy = original.Clone();
        copy.Base[Stat.Str] = 999;
        copy.Rate(StatModifier.Item)[Stat.AC] = 5;

        original.Base[Stat.Str].ShouldBe(100);
        original.Rate(StatModifier.Item)[Stat.AC].ShouldBe(CombatStats.NeutralRate);
    }
}
