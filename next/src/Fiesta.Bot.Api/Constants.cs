using System.Text;
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
    /// <summary>"HpChanged" -> "HP_CHANGED".</summary>
    public static string Upper(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }

    private static Dictionary<string, long> Enum<T>(string prefix = "", bool skipNone = true) where T : struct, System.Enum
    {
        var d = new Dictionary<string, long>();
        foreach (var v in System.Enum.GetValues<T>())
        {
            var n = v.ToString();
            if (skipNone && n is "None") continue;
            d[prefix + Upper(n)] = Convert.ToInt64(v);
        }
        return d;
    }

    private static readonly Lazy<Dictionary<string, Dictionary<string, long>>> Tables = new(() =>
    {
        var ents = Enum<EntityKind>();
        var skills = Enum<SkillTag>();
        foreach (var kv in Enum<CanUseReason>("R_", false)) skills[kv.Key] = kv.Value;
        var act = Enum<ActionKind>();
        foreach (var kv in Enum<LocalRefusal>("L_")) act[kv.Key] = kv.Value;
        foreach (var kv in Enum<ActionState>("S_", false)) act[kv.Key] = kv.Value;
        var dbg = new Dictionary<string, long> { ["CRIT"] = 0, ["NOTE"] = 1, ["INFO"] = 2, ["DEBUG"] = 3, ["TRACE"] = 4 };
        var quests = new Dictionary<string, long> { ["KILL"] = 0, ["COLLECT"] = 1, ["TALK"] = 2, ["REACH_LEVEL"] = 3, ["OTHER"] = 4, ["HUNT"] = 10, ["HANDIN"] = 11, ["ACCEPT"] = 12, ["NONE"] = 13 };
        return new Dictionary<string, Dictionary<string, long>>
        {
            ["ents"] = ents,
            ["skills"] = skills,
            ["items"] = Enum<ItemCategory>(),
            ["npc"] = Enum<NpcRole>(),
            ["quests"] = quests,
            ["act"] = act,
            ["ev"] = Enum<EventKind>(skipNone: false),
            ["world"] = Enum<WorldComponent>(skipNone: false),
            ["dbg"] = dbg,
            ["self"] = new Dictionary<string, long>(),
        };
    });

    /// <summary>Constants of a module, null when none.</summary>
    public static IReadOnlyDictionary<string, long>? For(string module) => Tables.Value.GetValueOrDefault(module);
    /// <summary>Every module's constants.</summary>
    public static IReadOnlyDictionary<string, Dictionary<string, long>> All() => Tables.Value;
    /// <summary>`ents.*` filter values.</summary>
    public static IReadOnlyDictionary<string, long> Ents => Tables.Value["ents"];
    /// <summary>`skills.*` tag values and `skills.R_*` reason codes.</summary>
    public static IReadOnlyDictionary<string, long> Skills => Tables.Value["skills"];
    /// <summary>`ev.*` event kinds.</summary>
    public static IReadOnlyDictionary<string, long> Ev => Tables.Value["ev"];
}
