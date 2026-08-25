using Fiesta.Bot.Combat;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>Edge cases that pin the damage formula's integer boundaries.
///
/// The primary evidence is the differential fuzz in `tools/fuzz_damage.py`, which runs this code and the
/// REAL `RulesOfEngagement` code from Zone.exe (under Unicorn) on the same inputs. That needs Windows, the
/// server binary and a Unicorn install, so it cannot run in CI. These tests are the residue: every law the
/// fuzz discovered, expressed as a case that fails if the law is broken.
///
/// Every expected value here was READ FROM THE ORACLE, not derived by hand. Where a number looks arbitrary
/// it is because the server is arbitrary -- see docs/DAMAGE_FORMULA.md.</summary>
public class DamageFormulaTests
{
    private static ParamContainer C() => ParamContainer.Neutral();

    // ---- clamps -------------------------------------------------------------------------------------

    [Fact]
    public void AllZeroContainer_EveryAccessorReturnsOne_NotTwo()
    {
        // The clamp lives on the CORE chain only. Clamping both halves would give 2.
        var c = C();
        DamageFormula.Ac(c).ShouldBe(1.0);
        DamageFormula.Mr(c).ShouldBe(1.0);
        DamageFormula.Th(c).ShouldBe(1.0);
        DamageFormula.Tb(c).ShouldBe(1.0);
        DamageFormula.MinWc(c).ShouldBe(1.0);
        DamageFormula.MaxWc(c).ShouldBe(1.0);
    }

    [Fact]
    public void CoreChainClamp_IsLessThanOne_NotLessThanOrEqualToZero()
    {
        // core = 0.5 (Men 5 scaled by PassiveSkill.rate 100/1000), own = 10.
        // A `<= 0` clamp leaves the core at 0.5 and gives 10.5; the server returns 11.
        var c = C();
        c.PureCharParam[ParamField.Men] = 5;
        c.PureCharParam[ParamField.MR] = 10;
        c.PassiveSkillRate[ParamField.Men] = 100;
        DamageFormula.Mr(c).ShouldBe(11.0);

        // roe_TH is the decisive one: it does not truncate its sum, so 11.0 cannot be a rounded 10.5.
        var t = C();
        t.PureCharParam[ParamField.Dex] = 5;
        t.PureCharParam[ParamField.TH] = 10;
        t.PassiveSkillRate[ParamField.Dex] = 100;
        DamageFormula.Th(t).ShouldBe(11.0);
    }

    [Fact]
    public void ResultClamp_RaisesAPositiveFraction_ToOne()
    {
        // WCmax 600 plus the clamped core (1) sums to 601. At AbnormalState.rate.WCmax = 1 that scales to
        // 0.601 and the server returns 1.0; at rate 2 it is 1.202 and the server returns 1.202 unchanged.
        // So the floor is `< 1`, not `<= 0` -- a positive fraction is raised, a value above 1 is not.
        // (The 0.6/1.2 pair in the first version of this test came from a note written before the core
        // clamp was corrected, when the core contributed 0 rather than 1. All three values below were
        // re-read from the oracle: rate 1 -> 1.0, rate 2 -> 1.202, rate 3 -> 1.803.)
        var a = C();
        a.PureCharParam[ParamField.WCmax] = 600;
        a.AbnormalStateRate[ParamField.WCmax] = 1;
        DamageFormula.MaxWc(a).ShouldBe(1.0);

        a.AbnormalStateRate[ParamField.WCmax] = 2;
        DamageFormula.MaxWc(a).ShouldBe(1.202, 1e-9);

        a.AbnormalStateRate[ParamField.WCmax] = 3;
        DamageFormula.MaxWc(a).ShouldBe(1.803, 1e-9);
    }

    // ---- truncation ---------------------------------------------------------------------------------

    /// <summary>Three of six accessors discard the fraction of their summed value and three do not.
    /// Measured by feeding a sum of exactly 2001.2.</summary>
    [Theory]
    [InlineData("AC", 2001.0)]
    [InlineData("MR", 2001.0)]
    [InlineData("TB", 2001.0)]
    [InlineData("TH", 2001.2)]
    public void SumTruncation_AppliesToAcMrTb_ButNotTh(string fn, double expected)
    {
        // core 10006 * 100/1000 = 1000.6 ; own 10006 * 100/1000 = 1000.6 ; sum 2001.2
        var core = fn == "AC" ? ParamField.Con : fn == "MR" ? ParamField.Men : ParamField.Dex;
        var own = fn switch
        {
            "AC" => ParamField.AC,
            "MR" => ParamField.MR,
            "TB" => ParamField.TB,
            _ => ParamField.TH
        };
        var c = C();
        c.PureCharParam[core] = 10006;
        c.PassiveSkillRate[core] = 100;
        c.ItemPlus[own] = 10006;
        c.LastTuneRate[own] = 100;
        var got = fn switch
        {
            "AC" => DamageFormula.Ac(c),
            "MR" => DamageFormula.Mr(c),
            "TB" => DamageFormula.Tb(c),
            _ => DamageFormula.Th(c)
        };
        got.ShouldBe(expected, 1e-9);
    }

    /// <summary>AC applies its first trailing rate INSIDE the truncation, MR applies its rate OUTSIDE.
    /// Asymmetric, but that is what the binary does -- with a sum of 1000.6 and rate 3000, AC gives 3001
    /// and MR gives 3000.</summary>
    [Fact]
    public void TrailingRatePosition_DiffersBetweenAcAndMr()
    {
        var ac = C();
        ac.PureCharParam[ParamField.Con] = 10006;
        ac.PassiveSkillRate[ParamField.Con] = 100;      // core = 1000.6
        ac.AbnormalStateRate[ParamField.AC] = 3000;
        DamageFormula.Ac(ac).ShouldBe(3001.0);          // trunc(1000.6 * 3) = 3001, rate is INSIDE

        var mr = C();
        mr.PureCharParam[ParamField.Men] = 10006;
        mr.PassiveSkillRate[ParamField.Men] = 100;      // core = 1000.6
        mr.ItemPowerRateRate[ParamField.MR] = 3000;
        DamageFormula.Mr(mr).ShouldBe(3000.0);          // trunc(1000.6) * 3 = 3000, rate is OUTSIDE
    }

    [Fact]
    public void Ac_CanReturnANonIntegerResult_WhenTheSecondRateShrinksIt()
    {
        // The second rate is applied after the truncation, so it reintroduces a fraction.
        var c = C();
        c.PureCharParam[ParamField.Con] = 10006;
        c.PassiveSkillRate[ParamField.Con] = 100;
        c.AbnormalStateRate[ParamField.AC] = 500;
        c.ItemPowerRateRate[ParamField.AC] = 7;
        DamageFormula.Ac(c).ShouldBe(3.5, 1e-9);
    }

    // ---- the minimised regression that found the core clamp -----------------------------------------

    [Fact]
    public void MinimisedMrCase_CoreOfPointNineNineNine_ClampsUpToOne()
    {
        // Shrunk by tools/minimise_case.py from a 40-field fuzz failure to these four fields.
        // core = (-1 * 1/1000) + 1 = 0.999 -> clamped to 1 ; own = 1 ; sum = 2.
        var c = C();
        c.PureCharParam[ParamField.Men] = -1;
        c.PureCharParam[ParamField.MR] = 1;
        c.AbnormalStatePlus[ParamField.Men] = 1;
        c.AbnormalStateRate[ParamField.Men] = 1;
        DamageFormula.Mr(c).ShouldBe(2.0);
    }

    // ---- CalcDamage ---------------------------------------------------------------------------------

    [Fact]
    public void Damage_ScalesWithLevelPlusOne()
    {
        // roe_Damage(1569, 242) at level 61 reproduces the closed form bit-exactly.
        DamageFormula.Damage(1569.0, 242.0, 61).ShouldBe(62 * 1569.0 / 242.0);
    }

    [Fact]
    public void CalcDamage_NeverReturnsLessThanOne()
    {
        // damagerate 0 makes the raw damage 0; the server clamps the final integer to 1.
        DamageFormula.CalcDamage(C(), C(), 61, 500, critical: false, damageRate: 0).ShouldBe(1);
    }

    [Fact]
    public void CalcDamage_CriticalDoublesTheDamage()
    {
        var a = C();
        a.PureCharParam[ParamField.WCmin] = 400;
        a.PureCharParam[ParamField.WCmax] = 400;
        var normal = DamageFormula.CalcDamage(a, C(), 61, 500, critical: false);
        var crit = DamageFormula.CalcDamage(a, C(), 61, 500, critical: true);
        crit.ShouldBe(normal * 2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(90, 90)]
    [InlineData(180, 0)]
    [InlineData(270, 90)]
    [InlineData(360, 0)]
    [InlineData(-45, 45)]
    [InlineData(-135, 45)]
    public void AngleIndex_FoldsToASymmetricTriangleWave(int angle, int expected)
        => DamageFormula.AngleIndex(angle).ShouldBe(expected);
}
