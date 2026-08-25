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

    /// <summary>An enhancement bonus shifts the WHOLE range, keeping its spread — which is why the
    /// enhancement and buff layers store it once in the WCmax slot and both bounds read it from there.
    ///
    /// A weapon reads in the client as <c>1000~2000 (+3000)</c>: one flat bonus on both ends, not a
    /// separate bump per end. All values read from the oracle.</summary>
    [Theory]
    [InlineData(StatModifier.Upgrade, 3000, 4001.0, 5001.0)]
    [InlineData(StatModifier.Upgrade, 500, 1501.0, 2501.0)]
    [InlineData(StatModifier.AbnormalState, 750, 1751.0, 2751.0)]
    public void FlatWeaponBonus_ShiftsBothBounds_AndPreservesTheSpread(
        StatModifier layer, int bonus, double expectedMin, double expectedMax)
    {
        var c = With(s =>
        {
            s.Base[Stat.WCmin] = 1000;
            s.Base[Stat.WCmax] = 2000;
            s.Plus(layer)[Stat.WCmax] = bonus;      // stored once, read by BOTH bounds
        });

        DamageCalculator.MinWeaponDamage(c).ShouldBe(expectedMin);
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(expectedMax);
        // The unenhanced weapon is 1001~2001, so the spread must be untouched.
        (DamageCalculator.MaxWeaponDamage(c) - DamageCalculator.MinWeaponDamage(c)).ShouldBe(1000.0);
    }

    /// <summary>The control for the test above: a genuinely per-bound layer moves ONE end only.
    /// Without this, "both bounds moved" could just mean every WCmax value leaks into MinWC.</summary>
    [Fact]
    public void PerBoundItemBonus_MovesOnlyItsOwnEnd_AndWidensTheSpread()
    {
        var c = With(s =>
        {
            s.Base[Stat.WCmin] = 1000;
            s.Base[Stat.WCmax] = 2000;
            s.Plus(StatModifier.Item)[Stat.WCmax] = 400;
        });

        DamageCalculator.MinWeaponDamage(c).ShouldBe(1001.0);   // unchanged
        DamageCalculator.MaxWeaponDamage(c).ShouldBe(2401.0);   // +400
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

    // ---- magic and the engagement rules ---------------------------------------------------------------

    /// <summary>Magic draws on Int + MAmin/MAmax and is defended by magic resistance; physical draws on
    /// Str + WCmin/WCmax and is defended by armour. Values read from the oracle.</summary>
    [Fact]
    public void MagicAndPhysical_DrawOnDifferentStats()
    {
        var attacker = With(s =>
        {
            s.Base[Stat.Str] = 300; s.Base[Stat.Int] = 250;
            s.Base[Stat.WCmin] = 400; s.Base[Stat.WCmax] = 600;
            s.Base[Stat.MAmin] = 350; s.Base[Stat.MAmax] = 550;
        });
        var defender = With(s =>
        {
            s.Base[Stat.Con] = 150; s.Base[Stat.Men] = 170;
            s.Base[Stat.AC] = 120; s.Base[Stat.MR] = 110;
        });

        DamageCalculator.MinWeaponDamage(attacker).ShouldBe(700.0);   // Str 300 + WCmin 400
        DamageCalculator.MaxWeaponDamage(attacker).ShouldBe(900.0);
        DamageCalculator.MinMagicAttack(attacker).ShouldBe(600.0);    // Int 250 + MAmin 350
        DamageCalculator.MaxMagicAttack(attacker).ShouldBe(800.0);

        DamageCalculator.AttackPower(attacker, 500, EngagementRule.NormalPhysical).ShouldBe(800.0);
        DamageCalculator.AttackPower(attacker, 500, EngagementRule.NormalMagic).ShouldBe(700.0);
        DamageCalculator.DefendPower(defender, EngagementRule.NormalPhysical).ShouldBe(270.0);  // Con + AC
        DamageCalculator.DefendPower(defender, EngagementRule.NormalMagic).ShouldBe(280.0);     // Men + MR

        DamageCalculator.ResolveDamage(attacker, defender,
            new AttackModifiers { RollPermille = 500, ForceCritical = false },
            null, EngagementRule.NormalPhysical).ShouldBe(183);
        DamageCalculator.ResolveDamage(attacker, defender,
            new AttackModifiers { RollPermille = 500, ForceCritical = false },
            null, EngagementRule.NormalMagic).ShouldBe(155);
    }

    /// <summary>Every rule scales attack power by weapon mastery EXCEPT a magical skill, which ignores it.
    /// A mage with no mastery still lands full skill damage but their plain attack does nothing.</summary>
    [Theory]
    [InlineData(EngagementRule.NormalPhysical, 0, 0.0)]
    [InlineData(EngagementRule.PhysicalSkill, 0, 0.0)]
    [InlineData(EngagementRule.NormalMagic, 0, 0.0)]
    [InlineData(EngagementRule.MagicalSkill, 0, 151.0)]      // unaffected
    [InlineData(EngagementRule.NormalPhysical, 2000, 302.0)]
    [InlineData(EngagementRule.NormalMagic, 2000, 302.0)]
    [InlineData(EngagementRule.MagicalSkill, 2000, 151.0)]   // still unaffected
    public void WeaponMastery_ScalesEveryRuleExceptMagicalSkill(
        EngagementRule rule, int masteryRate, double expected)
    {
        var attacker = With(s =>
        {
            s.Base[Stat.MAmin] = 100; s.Base[Stat.MAmax] = 200;
            s.Base[Stat.WCmin] = 100; s.Base[Stat.WCmax] = 200;
            s.Rate(StatModifier.PassiveSkill)[Stat.MagicalWeaponMastery] = masteryRate;
            s.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery] = masteryRate;
        });
        DamageCalculator.AttackPower(attacker, 500, rule).ShouldBe(expected);
    }

    [Fact]
    public void AlwaysCritical_CritsWithoutBeingAsked()
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 400; s.Base[Stat.WCmax] = 400; });
        var outcome = DamageCalculator.Resolve(attacker, Blank(),
            new AttackModifiers { RollPermille = 500, ForceCritical = false, CriticalChancePermille = 0 },
            null, EngagementRule.AlwaysCritical);
        outcome.WasCritical.ShouldBeTrue();
    }

    // ---- level and the level gap --------------------------------------------------------------------

    /// <summary>Damage scales with the ATTACKER's level + 1, and the defender's level does not enter the
    /// core damage at all. Both read from the oracle with the defender held at 61.</summary>
    [Theory]
    [InlineData(1, 1002)]
    [InlineData(10, 5511)]
    [InlineData(61, 31062)]
    [InlineData(100, 50601)]
    [InlineData(255, 128256)]
    public void Damage_ScalesWithAttackerLevelPlusOne(int attackerLevel, int expected)
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 500; s.Base[Stat.WCmax] = 500; }, attackerLevel);
        DamageCalculator.ResolveDamage(attacker, Blank(61),
            new AttackModifiers { RollPermille = 500, ForceCritical = false }).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(255)]
    public void Damage_IgnoresTheDefendersLevel(int defenderLevel)
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 500; s.Base[Stat.WCmax] = 500; }, 61);
        DamageCalculator.ResolveDamage(attacker, Blank(defenderLevel),
            new AttackModifiers { RollPermille = 500, ForceCritical = false }).ShouldBe(31062);
    }

    /// <summary>The level gap runs AFTER the integer conversion as <c>(rate * damage) / 1000</c> with a
    /// 32-BIT multiply, so the product WRAPS. With a post-conversion damage of 31062 the server returns
    /// these — a double multiply would give 3106200, 6212400 and 31062000 instead.</summary>
    [Theory]
    [InlineData(1000, 31062)]
    [InlineData(100000, 1)]          // wraps negative, then floors to 1
    [InlineData(200000, 1917432)]
    [InlineData(1000000, 997228)]
    public void LevelGap_MultipliesWithA32BitWrap(int ratePermille, int expected)
    {
        var attacker = With(s => { s.Base[Stat.WCmin] = 500; s.Base[Stat.WCmax] = 500; }, 61);
        DamageCalculator.ResolveDamage(attacker, Blank(61), new AttackModifiers
        {
            RollPermille = 500, ForceCritical = false, LevelGapRatePermille = ratePermille
        }).ShouldBe(expected);
    }

    // ---- angle table --------------------------------------------------------------------------------

    /// <summary>The table index is a fold of the DIRECTION-UNIT delta (2 degrees per unit), so a full
    /// turn is 180 units and the 0..90 index spans 0..180 degrees.</summary>
    [Theory]
    [InlineData(0, 0)]        // front
    [InlineData(45, 45)]      // 90 degrees, side
    [InlineData(90, 90)]      // 180 degrees, behind -- the largest multiplier
    [InlineData(135, 45)]
    [InlineData(180, 0)]      // a full turn, back to the front
    [InlineData(-45, 45)]     // sign does not matter, left and right are symmetric
    [InlineData(-90, 90)]
    public void AngleDamageIndex_FoldsDirectionUnitsToATriangleWave(int units, int expected)
        => DamageCalculator.AngleDamageIndex(units).ShouldBe(expected);

    /// <summary>The gameplay-visible ordering the operator confirmed: from behind hits hardest, then the
    /// side, then head-on. If this ever inverts, the degrees/units confusion is back.</summary>
    [Theory]
    [InlineData(0, 0)]        // front
    [InlineData(90, 45)]      // side
    [InlineData(180, 90)]     // behind
    [InlineData(270, 45)]
    [InlineData(360, 0)]
    [InlineData(-180, 90)]
    public void AngleDamageIndexFromDegrees_PutsBehindAtTheTopOfTheTable(int degrees, int expected)
        => DamageCalculator.AngleDamageIndexFromDegrees(degrees).ShouldBe(expected);

    [Fact]
    public void AngleDamageIndex_RanksBehindAboveSideAboveFront()
    {
        var front = DamageCalculator.AngleDamageIndexFromDegrees(0);
        var side = DamageCalculator.AngleDamageIndexFromDegrees(90);
        var behind = DamageCalculator.AngleDamageIndexFromDegrees(180);
        behind.ShouldBeGreaterThan(side);
        side.ShouldBeGreaterThan(front);
    }

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
