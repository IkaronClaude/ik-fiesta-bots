using Fiesta.Bot.Agent;

namespace Fiesta.Bot.Api;

/// <summary>Skill tag bits (precomputed from ActiveSkill at load; the Lua `skills.*` constants).</summary>
[Flags]
public enum SkillTag : int
{
    None = 0, Damage = 1, Heal = 2, Buff = 4, Debuff = 8, Stun = 16, Aoe = 32, Ground = 64, Self = 128, Party = 256,
    Moving = 512, Dot = 1024, Slow = 2048, Dispel = 4096, Revive = 8192, Toggle = 16384, PartyOnly = 32768,
}

/// <summary>Bag categories (the Lua `items.*` constants).</summary>
[Flags]
public enum ItemCategory : int { None = 0, Consumable = 1, Equip = 2, Quest = 4, Material = 8, Junk = 16, Potion = 32, Scroll = 64, Mount = 128 }

/// <summary>NPC roles learned from menus (the Lua `npc.*` constants).</summary>
[Flags]
public enum NpcRole : int { None = 0, Shop = 1, SoulStone = 2, Skill = 4, Smith = 8, Storage = 16, Quest = 32, Teleport = 64, Gate = 128 }

/// <summary>Constant tables pushed into each module table at VM creation.</summary>
public static class LuaConstants
{
    /// <summary>`ents.*` filter values.</summary>
    public static IReadOnlyDictionary<string, long> Ents => throw new NotImplementedException();
    /// <summary>`skills.*` tag values and `skills.R_*` reason codes (<see cref="CanUseReason"/>).</summary>
    public static IReadOnlyDictionary<string, long> Skills => throw new NotImplementedException();
    /// <summary>`items.*` categories.</summary>
    public static IReadOnlyDictionary<string, long> Items => throw new NotImplementedException();
    /// <summary>`npc.*` roles.</summary>
    public static IReadOnlyDictionary<string, long> Npc => throw new NotImplementedException();
    /// <summary>`quests.*` objective and plan kinds.</summary>
    public static IReadOnlyDictionary<string, long> Quests => throw new NotImplementedException();
    /// <summary>`act.*` action kinds and `act.L_*` local refusals.</summary>
    public static IReadOnlyDictionary<string, long> Act => throw new NotImplementedException();
    /// <summary>`ev.*` event kinds.</summary>
    public static IReadOnlyDictionary<string, long> Ev => throw new NotImplementedException();
    /// <summary>`world.*` component ids.</summary>
    public static IReadOnlyDictionary<string, long> World => throw new NotImplementedException();
    /// <summary>`self.STAT_*` ids.</summary>
    public static IReadOnlyDictionary<string, long> Self => throw new NotImplementedException();
}
