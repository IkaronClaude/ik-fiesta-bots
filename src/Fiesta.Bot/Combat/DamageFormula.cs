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
        v = v * c.ItemPowerRateRate[f] * c.PassiveSkillRate[f] * c.AbnormalStateRate[f] * c.LastTuneRate[f]
            / RateDivisor;
        v += c.UpgradePlus[f] + c.WeaponTitlePlus[f] + c.PassiveSkillPlus[f]
             + c.AbnormalStatePlus[f] + c.LastTunePlus[f];
        return v;
    }

    /// <summary>The chain over the governing CORE stat, clamped so a non-positive result becomes 1.
    ///
    /// ⚠️ The clamp belongs HERE and nowhere else. With an all-zero container the real accessors return
    /// exactly 1 — clamping both halves would give 2, which is precisely how the first fuzz run caught it.</summary>
    public static double CoreChain(ParamContainer c, ParamField core)
    {
        var v = Chain(c, core);
        return v <= 0 ? 1.0 : v;
    }

    /// <summary>Each accessor reads a FIXED side of the engagement, taken from the binary
    /// (`[esi]`/`[edi]` = attacker, `[esi+4]` = defender):
    /// attacker supplies MinWC / MaxWC / TH; defender supplies AC / TB / MR. Semantically exactly right —
    /// your weapon and your accuracy, their armour, block and magic resist.</summary>
    public static double Stat(ParamContainer c, ParamField core, ParamField own) => CoreChain(c, core) + Chain(c, own);

    public static double MinWc(ParamContainer attacker) => Stat(attacker, ParamField.Str, ParamField.WCmin);
    public static double MaxWc(ParamContainer attacker) => Stat(attacker, ParamField.Str, ParamField.WCmax);
    public static double Th(ParamContainer attacker) => Stat(attacker, ParamField.Dex, ParamField.TH);
    public static double Ac(ParamContainer defender) => Stat(defender, ParamField.Con, ParamField.AC);
    public static double Tb(ParamContainer defender) => Stat(defender, ParamField.Dex, ParamField.TB);
    public static double Mr(ParamContainer defender) => Stat(defender, ParamField.Men, ParamField.MR);

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
