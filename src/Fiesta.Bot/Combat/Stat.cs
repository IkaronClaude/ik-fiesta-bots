namespace Fiesta.Bot.Combat;

/// <summary>A stat slot inside one parameter block, in the server's own order.
///
/// Recovered field-exact from Zone.pdb's CodeView type stream (docs/DAMAGE_FORMULA.md Appendix A).
/// <b>The ordinal IS the slot index</b> within a block — a block is exactly these 51 ints — so the values
/// are not free to renumber or reorder.
///
/// Spellings such as <c>PhisycalWeaponMastery</c> are the server's, kept verbatim so a name here can be
/// grepped against the PDB dump without a translation table.</summary>
public enum Stat
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

/// <summary>Where a stat modification came from.
///
/// A character's stats are not one flat table: the server keeps a separate block per source, and the damage
/// formula reads several of them individually rather than reading a pre-combined total. Each source has two
/// halves — a flat <c>Plus</c> and a permille <c>Rate</c> — and which half applies where is part of the
/// formula, not a detail. <see cref="Stat.PhisycalWeaponMastery"/> scaled by
/// <see cref="PassiveSkill"/>'s rate, for instance, gates <em>all</em> physical attack power.
///
/// This is why <see cref="CombatStats"/> cannot collapse to a single dictionary of effective values: the
/// layers are inputs to the formula, not a presentation detail.</summary>
public enum StatModifier
{
    /// <summary>Equipped gear.</summary>
    Item,
    /// <summary>Gear "power rate" — a second, separate item layer the server tracks apart from Item.</summary>
    ItemPowerRate,
    /// <summary>Enhancement / +N upgrade levels.</summary>
    Upgrade,
    /// <summary>Weapon title (the prefix/suffix affix on a named weapon).</summary>
    WeaponTitle,
    /// <summary>Learned passive skills.</summary>
    PassiveSkill,
    /// <summary>Buffs and debuffs currently applied.</summary>
    AbnormalState,
    /// <summary>"Last tune" — the final adjustment layer.</summary>
    LastTune,
}
