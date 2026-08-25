namespace Fiesta.Bot.Combat;

/// <summary>Which set of combat rules governs a hit — the server's eight
/// <c>RulesOfEngagement</c> singletons.
///
/// <para><c>roe_CalcDamage</c> is not overridden by any of them: one virtual pipeline on the base class
/// drives every rule and only the components it calls differ. So a rule changes <b>which stats feed attack
/// and defence</b>, and <b>how hit and critical are decided</b> — never the core damage formula, which was
/// measured identical across all eight.</para></summary>
public enum EngagementRule
{
    /// <summary>An ordinary weapon swing. <c>roe_normalPY</c>.</summary>
    NormalPhysical,

    /// <summary>A physical skill. <c>roe_physical</c>. Same stats as <see cref="NormalPhysical"/>.</summary>
    PhysicalSkill,

    /// <summary>An ordinary magic attack. <c>roe_normalMA</c>. Driven by Int and MAmin/MAmax, defended by
    /// magic resistance rather than armour.</summary>
    NormalMagic,

    /// <summary>A magical skill. <c>roe_magical</c>. Same stats as <see cref="NormalMagic"/>.</summary>
    MagicalSkill,

    /// <summary>A cure skill. <c>roe_cure</c>. Overrides hit rate only.</summary>
    CureSkill,

    /// <summary>Cannot miss. <c>roe_always</c>.</summary>
    AlwaysHit,

    /// <summary>Always critical. <c>roe_alwaysCritical</c>.</summary>
    AlwaysCritical,

    /// <summary>A healing "attack". <c>roe_alwaysHealAttack</c>.</summary>
    HealAttack,
}

/// <summary>Whether a rule draws on the physical or the magical stat set.</summary>
public enum DamageSchool
{
    /// <summary>Attack from Str + WCmin..WCmax, defended by armour class (Con + AC).</summary>
    Physical,

    /// <summary>Attack from Int + MAmin..MAmax, defended by magic resistance (Men + MR).</summary>
    Magical,
}

public static class EngagementRuleExtensions
{
    /// <summary>The stat set a rule draws on. Measured on one input (attacker Str 300 Int 250 WCmin 400
    /// WCmax 600 MAmin 350 MAmax 550, defender Con 150 Men 170 AC 120 MR 110): the physical rules give
    /// attack 800 / defend 270, the magical ones 700 / 280.</summary>
    public static DamageSchool School(this EngagementRule rule) => rule switch
    {
        EngagementRule.NormalMagic or EngagementRule.MagicalSkill => DamageSchool.Magical,
        _ => DamageSchool.Physical,
    };

    /// <summary>Whether the rule forces every hit to be a critical, regardless of the rolled chance.</summary>
    public static bool AlwaysCriticals(this EngagementRule rule) => rule == EngagementRule.AlwaysCritical;

    /// <summary>Whether attack power is scaled by weapon mastery.
    ///
    /// <para>Every rule does EXCEPT <see cref="EngagementRule.MagicalSkill"/>, which ignores it entirely.
    /// Measured with both mastery rates swept 0 to 4000 over the same weapon bounds: normal physical,
    /// physical skill and normal magic all scale linearly (0 gives 0, 2000 doubles), while a magical skill
    /// stays flat at 151.0 throughout. So a mage with no mastery still lands full skill damage, but their
    /// plain attack does nothing.</para>
    ///
    /// <para>The asymmetry is real and was measured, not inferred from the symmetry of the other three.</para></summary>
    public static bool AppliesWeaponMastery(this EngagementRule rule) => rule != EngagementRule.MagicalSkill;
}
