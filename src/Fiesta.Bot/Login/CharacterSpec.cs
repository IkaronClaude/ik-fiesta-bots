namespace Fiesta.Bot.Login;

/// <summary>Class IDs (the chrclass bitfield in PROTO_AVATAR_SHAPE_INFO)</summary>
public enum ClassId : byte
{
    Fighter = 1,    // Fighter
    Priest = 6,     // Cleric
    Archer = 11,    // Archer
    Mage = 16,      // Mage
    Joker = 21,     // Joker (Trickster) — lvl-100 → Spectre(24) or Reaper(25)
    Crusader = 26,  // Sentinel — creatable at level 60, but only if the account
                    // already has at least one level-60+ character
}

/// <summary>What character to create (first-class feature — the bot provisions its own avatar instead of relying on a pre-…</summary>
public sealed record CharacterSpec(
    string Name,
    ClassId Class = ClassId.Fighter,
    byte Gender = 0,
    byte? Race = null,
    byte? HairType = null,
    byte? HairColor = null,
    byte? FaceShape = null,
    byte Slot = 0);
