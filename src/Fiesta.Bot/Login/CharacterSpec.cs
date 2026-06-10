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
public sealed record CharacterSpec(
    string Name,
    ClassId Class = ClassId.Fighter,
    byte Gender = 0,
    byte Race = 0,
    byte HairType = 0,
    byte HairColor = 0,
    byte FaceShape = 0,
    byte Slot = 0);
