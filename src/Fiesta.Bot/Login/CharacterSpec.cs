namespace Fiesta.Bot.Login;

/// <summary>
/// Class IDs (the <c>chrclass</c> bitfield in PROTO_AVATAR_SHAPE_INFO). These are
/// the ground-truth ClassID values from the client's ClassName.shn (dumped with
/// the ik-fiesta-collab `shn` tool), NOT the wire protocol. The five level-1
/// creatable classes are Fighter/Priest/Archer/Mage/Joker. Advancement is at
/// lvl 20 → 60 → 100 (the lvl-100 step is a branch choice, e.g. Mage → Wizard or
/// Warlock); Reaper (Assassin, 25) is the Joker line's lvl-100 choice. Crusader
/// (Sentinel, 26) is a special class that starts at level 60.
/// (acEngName in parentheses where the in-game name differs.)
/// </summary>
public enum ClassId : byte
{
    Fighter = 1,    // Fighter
    Priest = 6,     // Cleric
    Archer = 11,    // Archer
    Mage = 16,      // Mage
    Joker = 21,     // Joker (Trickster) — lvl-100 → Spectre(24) or Reaper(25)
    Crusader = 26,  // Sentinel — creatable at level 60, but only if the account
                    // already has at least one level-60+ character.
}

/// <summary>
/// What character to create (first-class feature — the bot provisions its own
/// avatar instead of relying on a pre-seeded one). Appearance fields are the
/// 4-byte PROTO_AVATAR_SHAPE_INFO bitfields; defaults are a valid level-1 char.
/// </summary>
/// <remarks>⛔ THE APPEARANCE FIELDS ARE NULLABLE ON PURPOSE — <b>0 is a VALID value for every one of
/// them</b>, so it can never double as "not specified" (see the golden rule in CLAUDE.md). faceshape 0 is
/// a real Fighter face, race 0 is a real (if blank) race id, hair 0 is a real hairstyle. Modelling
/// "unset" as 0 made it impossible to even TEST zero: a create sent with <c>Race: 0</c> was silently
/// rewritten to the derived race, the log read <c>race=2 (derived)</c>, and the experiment proved
/// nothing. null means "you did not say"; 0 means "send zero".</remarks>
public sealed record CharacterSpec(
    string Name,
    ClassId Class = ClassId.Fighter,
    byte Gender = 0,
    byte? Race = null,
    byte? HairType = null,
    byte? HairColor = null,
    byte? FaceShape = null,
    byte Slot = 0);
