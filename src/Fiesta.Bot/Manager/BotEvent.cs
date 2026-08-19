using Fiesta.Bot.Navigation;
using Fiesta.Bot.Session;

namespace Fiesta.Bot.Manager;

/// <summary>The kind of a — what happened to/around the bot</summary>
public enum BotEventKind
{
    /// <summary>— a nearby/overheard chat line</summary>
    Chat,
    /// <summary>— a cast-fail reason code (0x0FC9 SP, 0x0FCA range…)</summary>
    CastFail,
    PlayerAppeared,
    /// <summary>— the zone handle of a player that left view</summary>
    PlayerLeft,
    /// <summary>— the bot changed map (gate / town portal)</summary>
    MapChanged,
    /// <summary>(uint X, uint Y) — the server snapped us back (MOVEFAIL)</summary>
    MoveFailed,
    /// <summary>— the bot's new current HP (HPCHANGE)</summary>
    Hp,
    /// <summary>— the bot's new current SP (SPCHANGE)</summary>
    Sp,
    /// <summary>— a combat hit in view (own swing or others')</summary>
    Hit,
    /// <summary>SkillLearnedInfo — the server CONFIRMED a skill/passive was learned (0x4804)</summary>
    SkillLearned,
    /// <summary>SkillCastInfo — the server confirmed one of OUR casts started and named the skill (0x244E)</summary>
    SkillCast,
}

/// <summary>A skill the server just confirmed we learned. Passive and active ids are SEPARATE, OVERLAPPING spaces,
/// so the flag is part of the identity, not a decoration.</summary>
public sealed record SkillLearnedInfo(int SkillId, int Level, bool Passive);

/// <summary>A cast the server confirmed started (0x244E), with the target it named.</summary>
public sealed record SkillCastInfo(int SkillId, int Target);

/// <summary>One thing that happened to a bot, carried on the stable hub</summary>
public sealed record BotEvent(BotEventKind Kind, object? Data);
