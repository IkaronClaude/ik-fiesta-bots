using System;

namespace Fiesta.Bot.Combat;

/// <summary>The stat fields of one `Parameter` block, in the server's own order.
///
/// Recovered field-exact from Zone.pdb's CodeView type stream (see docs/DAMAGE_FORMULA.md Appendix A).
/// One block is 51 ints / 0xCC bytes; the ordinal here IS the field's index within a block.</summary>
public enum ParamField
{
    Str = 0, Con, Dex, Int, Men,
    WCmin, WCmax, AC, TH, TB,
    MAmin, MAmax, MR, MH, MB,
    AbsoluteAttack, AbsoluteDefend, AbsoluteHit, AbsoluteBlock,
    MoveSpeed, HPRecover, SPRecover, CastingTime, Critical,
    PhisycalWeaponMastery, MagicalWeaponMastery, ShieldAC,
    HitRate, EvaRate, MACri, CriDam, MagCriDam, CriDamRate, MagCriDamRate,
    AttSpeed, MaxHP, MaxHP_2, MaxSP,
    HPAbsorption_Hitted, SPAbsorption_Hitted, HPAbsorption_Hit, SPAbsorption_Hit,
    CriticalTB, RegistNone, ResistPoison, ResistDeaseas, ResistCurse,
    ResistMoveSpdDown, ResistGTI, MaxLP, LPRecover,
}

/// <summary>One 51-int parameter block.</summary>
public sealed class ParamBlock
{
    public const int FieldCount = 51;
    private readonly int[] _v = new int[FieldCount];
    public int this[ParamField f] { get => _v[(int)f]; set => _v[(int)f] = value; }
    public int this[int i] { get => _v[i]; set => _v[i] = value; }
}

/// <summary>`Parameter::Container` — the per-character stat store the damage engine reads through
/// `so_parameter()`.
///
/// It is NOT a flat stat block: it holds parallel blocks, one per source of modification. Seven of them are
/// PAIRS (a `Plus` half and a `Rate` half, the rate being permille); `PureCharParam` and `Total` are single.
/// Layout and names are field-exact from the PDB (docs/DAMAGE_FORMULA.md Appendix A).</summary>
public sealed class ParamContainer
{
    public ParamBlock PureCharParam { get; } = new();
    public ParamBlock ItemPlus { get; } = new();
    public ParamBlock ItemRate { get; } = new();
    public ParamBlock ItemPowerRatePlus { get; } = new();
    public ParamBlock ItemPowerRateRate { get; } = new();
    public ParamBlock UpgradePlus { get; } = new();
    public ParamBlock UpgradeRate { get; } = new();
    public ParamBlock WeaponTitlePlus { get; } = new();
    public ParamBlock WeaponTitleRate { get; } = new();
    public ParamBlock PassiveSkillPlus { get; } = new();
    public ParamBlock PassiveSkillRate { get; } = new();
    public ParamBlock AbnormalStatePlus { get; } = new();
    public ParamBlock AbnormalStateRate { get; } = new();
    public ParamBlock LastTunePlus { get; } = new();
    public ParamBlock LastTuneRate { get; } = new();
    public ParamBlock Total { get; } = new();

    /// <summary>A container with every `Rate` half neutral (1000 permille) and every `Plus` half zero —
    /// the state a plain field mob is in. Anything else must be set explicitly.</summary>
    public static ParamContainer Neutral()
    {
        var c = new ParamContainer();
        foreach (var b in new[] { c.ItemRate, c.ItemPowerRateRate, c.UpgradeRate, c.WeaponTitleRate,
                                  c.PassiveSkillRate, c.AbnormalStateRate, c.LastTuneRate })
            for (var i = 0; i < ParamBlock.FieldCount; i++)
                b[i] = 1000;
        return c;
    }

    /// <summary>Fill `PureCharParam` from a mob's MobInfoServer + MobWeapon row, which IS its
    /// `PureCharParam` (those two tables carry exactly these fields).</summary>
    public static ParamContainer ForMob(int str, int con, int dex, int intel, int men,
                                        int minWc, int maxWc, int ac, int th, int tb,
                                        int minMa = 0, int maxMa = 0, int mr = 0, int mh = 0, int mb = 0)
    {
        var c = Neutral();
        var p = c.PureCharParam;
        p[ParamField.Str] = str; p[ParamField.Con] = con; p[ParamField.Dex] = dex;
        p[ParamField.Int] = intel; p[ParamField.Men] = men;
        p[ParamField.WCmin] = minWc; p[ParamField.WCmax] = maxWc;
        p[ParamField.AC] = ac; p[ParamField.TH] = th; p[ParamField.TB] = tb;
        p[ParamField.MAmin] = minMa; p[ParamField.MAmax] = maxMa;
        p[ParamField.MR] = mr; p[ParamField.MH] = mh; p[ParamField.MB] = mb;
        return c;
    }
}

/// <summary>The Fiesta damage engine, ported 1:1 from `RulesOfEngagement` in Zone.exe.
///
/// Every constant and every step here was read out of the server binary and is verified against it by
/// differential fuzzing (tools/fuzz_damage.py drives this and the Unicorn oracle on the same inputs and
/// requires exact agreement). See docs/DAMAGE_FORMULA.md.
///
/// ⚠️ Do not "tidy" the arithmetic. The ordering, the `&lt;= 0 -&gt; 1` clamps and the truncation points are
/// observable; rearranging them changes results at the integer boundary.</summary>
public static class DamageFormula
{
    /// <summary>Four permille multipliers, hence 1000^4. The literal in the binary is 1e12.</summary>
    private const double RateDivisor = 1_000_000_000_000.0;

    /// <summary>One stat accessor's chain over a single field.
    ///
    /// `(PureCharParam + Item.plus) * four rate halves / 1e12 + five plus halves`, then clamped so a
    /// non-positive result becomes 1. The clamp is real: with an all-zero container every accessor in the
    /// binary returns 1, not 0.</summary>
    public static double Chain(ParamContainer c, ParamField f)
    {
        var v = (double)c.PureCharParam[f] + c.ItemPlus[f];
        // MULTIPLY ORDER MATTERS. The binary loads the four rate halves in the order AbnormalState,
        // PassiveSkill, ItemPowerRate, LastTune (the `fild` sequence at the head of every accessor) and
        // multiplies them in that order before the single `fdiv` by 1e12 at 0x6CFE28. Floating-point
        // multiplication is not associative, so a different order is off by an ulp -- invisible almost
        // everywhere, but AC/MR/TB truncate their sum, and an ulp on the wrong side of an integer boundary
        // becomes a whole point of armour. That is exactly how it was found: one case in 500 where the
        // oracle said 3604 and this said 3603.
        v = v * c.AbnormalStateRate[f] * c.PassiveSkillRate[f] * c.ItemPowerRateRate[f]
            * c.LastTuneRate[f] / RateDivisor;
        v += c.UpgradePlus[f] + c.WeaponTitlePlus[f] + c.PassiveSkillPlus[f]
             + c.AbnormalStatePlus[f] + c.LastTunePlus[f];
        return v;
    }

    /// <summary>The chain over the governing CORE stat, floored at 1 -- and the threshold is `&lt; 1`,
    /// exactly like the result clamp, NOT `&lt;= 0`.
    ///
    /// The clamp belongs HERE and not on the own half. With an all-zero container the real accessors
    /// return exactly 1; clamping both halves would give 2, which is how the first fuzz run caught it.
    ///
    /// The `&lt; 1` threshold was measured, after a documented claim that this one was `&lt;= 0` while only
    /// the result clamp was `&lt; 1`. Feed core = 0.5 and own = 10: every accessor returns **11.0**, so the
    /// fractional core was raised to 1 rather than left at 0.5. `roe_TH` makes it decisive because it does
    /// not truncate its sum, so 11.0 cannot be confused with a rounded 10.5. It was found from a minimised
    /// `roe_MR` case (core 0.999, own 1.0) where the server returned 2 and this returned 1.</summary>
    public static double CoreChain(ParamContainer c, ParamField core) => Clamp1(Chain(c, core));
    /// <summary>Every accessor floors its RESULT at 1 -- and it is `&lt; 1`, NOT `&lt;= 0`.
    ///
    /// Pinned by a minimised case: with the sum at 600 and `AbnormalState.rate.WCmax = 1` the result is
    /// 0.6 and the server returns **1.0**; at rate 2 it is 1.2 and the server returns **1.2**. So a
    /// positive-but-fractional result is raised to 1, which `v &lt;= 0` would have let through.
    /// Note this differs from the CORE chain's clamp and from roe_Damage's, both of which really are
    /// `&lt;= 0` (they compare against zero via `fldz`, this one against one via `fld1`).</summary>
    private static double Clamp1(double v) => v < 1.0 ? 1.0 : v;

    // ---- integer truncation of the summed value -------------------------------------------------------
    // Three of the six accessors discard the fraction of their sum and three do not. This is NOT a guess
    // and NOT a `fistp` (there is no `fistp` anywhere in any of them -- an earlier comment here claimed
    // roe_AC did `fistp/fild` and that was simply wrong). It was measured: feed a sum of exactly 2001.2
    // and read the return value.
    //
    //     roe_TB, roe_AC, roe_MR  -> 2001.0     truncated
    //     roe_TH, roe_MinWC, roe_MaxWC -> 2001.2     not truncated
    //
    // The POSITION of the truncation relative to the trailing rates differs between AC and MR, which is
    // asymmetric but is what the binary does -- with a sum of 1000.6 and rate 3000, AC returns 3001
    // (rate applied INSIDE the truncation) while MR returns 3000 (rate applied OUTSIDE it).

    /// <summary>`AC` = clamp( trunc(sum x AbnormalState.rate.AC / 1000) x ItemPowerRate.rate.AC / 1000 ).
    /// Verified at (abn,ipr) = (1000,1) -> 1.0, (3000,3000) -> 9003.0, (1000,1000) -> 1000.0,
    /// (500,7) -> 3.5. The last one is why the result is NOT always an integer.</summary>
    public static double Ac(ParamContainer d) =>
        Clamp1(Math.Truncate((CoreChain(d, ParamField.Con) + Chain(d, ParamField.AC))
                             * d.AbnormalStateRate[ParamField.AC] / 1000.0)
               * d.ItemPowerRateRate[ParamField.AC] / 1000.0);

    /// <summary>`MR` = clamp( trunc(sum) x ItemPowerRate.rate.MR / 1000 ) -- rate OUTSIDE the truncation,
    /// unlike AC. Verified at rate 1000 -> 1000.0, 3000 -> 3000.0 (not 3001), 1 -> 1.0.</summary>
    public static double Mr(ParamContainer d) =>
        Clamp1(Math.Truncate(CoreChain(d, ParamField.Men) + Chain(d, ParamField.MR))
               * d.ItemPowerRateRate[ParamField.MR] / 1000.0);

    // TH / TB apply no second-pass rate: every rate on the own field showed up as doubling the own half
    // alone (2100), never the sum (2200). TB truncates its sum; TH does not.
    public static double Th(ParamContainer a) => Clamp1(CoreChain(a, ParamField.Dex) + Chain(a, ParamField.TH));
    public static double Tb(ParamContainer d) => Clamp1(Math.Truncate(CoreChain(d, ParamField.Dex) + Chain(d, ParamField.TB)));

    /// <summary>MinWC/MaxWC are the irregular pair. Their own half does NOT carry the ItemPowerRate or
    /// PassiveSkill rate (those are applied once, to the sum), and their Upgrade/AbnormalState plus-terms
    /// plus the AbnormalState rate read **WCmax even for MinWC** — asymmetric, and almost certainly a
    /// copy-paste slip in the original server, but it is the behaviour so the port reproduces it.</summary>
    private static double WcChain(ParamContainer a, ParamField own)
    {
        // Transcribed from roe_MinWC's tail, not inferred. `Item.plus.WCmin` is NOT added raw: it is scaled
        // by WeaponTitle's rate for the field and by PassiveSkill's PHYSICAL WEAPON MASTERY rate first --
        //     fld WeaponTitle.rate.WCmin; fmul Item.plus.WCmin; fdiv 1000; fmul PassiveSkill.rate.Mastery
        // which is why treating it as a flat bonus left MinWC/MaxWC at 22/40 and 27/40.
        var scaledItem = (double)a.WeaponTitleRate[own] * a.ItemPlus[own] / 1000.0
                         * a.PassiveSkillRate[ParamField.PhisycalWeaponMastery] / 1000.0;
        return a.UpgradePlus[ParamField.WCmax]
               + a.AbnormalStatePlus[ParamField.WCmax]
               + a.PureCharParam[own]
               + scaledItem
               + a.PassiveSkillPlus[ParamField.PhisycalWeaponMastery];
    }

    /// <summary>The WC pair applies its three sum-rates in the binary's order
    /// (AbnormalState.WCmax, then ItemPowerRate, then PassiveSkill) with NO integer truncation between them
    /// -- unlike roe_AC, whose tail round-trips through `fistp`/`fild`.</summary>
    private static double WcSum(double v, ParamContainer a, ParamField own) =>
        Clamp1(v * a.AbnormalStateRate[ParamField.WCmax] / 1000.0
                 * a.ItemPowerRateRate[own] / 1000.0
                 * a.PassiveSkillRate[own] / 1000.0);

    public static double MinWc(ParamContainer a) =>
        WcSum(CoreChain(a, ParamField.Str) + WcChain(a, ParamField.WCmin), a, ParamField.WCmin);

    public static double MaxWc(ParamContainer a) =>
        WcSum(CoreChain(a, ParamField.Str) + WcChain(a, ParamField.WCmax), a, ParamField.WCmax);

    /// <summary>Normal physical attack power: a roll between MinWC and MaxWC.
    ///
    /// The server computes `MinWC + rb_largerandom((int)(MaxWC - MinWC))`, so the roll is an INTEGER draw
    /// over the truncated range and `rollPermille` selects a point in it: 0 = MinWC, 1000 = MaxWC.</summary>
    public static double AttackPower(ParamContainer attacker, int rollPermille)
    {
        var lo = MinWc(attacker);
        var hi = MaxWc(attacker);
        var range = (long)(hi - lo);                       // __ftol2_sse truncates toward zero
        var draw = range * rollPermille / 1000;
        return lo + draw;
    }

    /// <summary>Normal physical defend power. `NormalPY::roe_DefendPower` is `roe_AC`.</summary>
    public static double DefendPower(ParamContainer defender) => Ac(defender);

    /// <summary>`RulesOfEngagement::roe_Damage` — the core, byte for byte:
    /// <code>
    /// v = (arg-&gt;nBMPDamageRate * attack) / 1000.0;
    /// if (v &lt;= 0) v = 1.0;
    /// return ((attackerLevel + 1) * v) / defend;
    /// </code>
    /// The only literals in the real function are 1000.0 and 1.0.</summary>
    public static double Damage(double attack, double defend, int attackerLevel, int bmpDamageRate = 1000)
    {
        var v = bmpDamageRate * attack / 1000.0;
        if (v <= 0) v = 1.0;
        return (attackerLevel + 1) * v / defend;
    }

    /// <summary>The full `roe_CalcDamage` pipeline for a normal physical hit, returning integer damage.
    ///
    /// `damagerate` is a PERMILLE and defaults to 1000 — zero makes the raw damage 0 and the server clamps
    /// the result to 1. `angleRate` comes from DamageByAngle (1000 head-on, up to 1200 from behind) and
    /// `levelGapRate` from DamageLvGap (flat 1000 for Monster→Player, up to 1500 Player→Monster).</summary>
    public static int CalcDamage(ParamContainer attacker, ParamContainer defender, int attackerLevel,
                                 int rollPermille, bool critical,
                                 int damageRate = 1000, int angleRate = 1000, int levelGapRate = 1000,
                                 int bmpDamageRate = 1000)
    {
        var attack = AttackPower(attacker, rollPermille);
        var defend = DefendPower(defender);
        var d = Damage(attack, defend, attackerLevel, bmpDamageRate);
        if (critical) d *= 2.0;
        d = d * damageRate / 1000.0 * angleRate / 1000.0 * levelGapRate / 1000.0;
        var i = (int)Math.Floor(d);
        return i > 0 ? i : 1;
    }

    /// <summary>`DamageByAngle::DamageTable::operator[]` folds any angle into 0..90 before indexing.
    /// Derived by running the real operator[] against an identity table over 0..360 and both signs.</summary>
    public static int AngleIndex(int angle) => Math.Abs(((Math.Abs(angle) + 90) % 180) - 90);
}
