using System.Collections.Concurrent;
using FiestaLibReloaded.Shn;

namespace Fiesta.Bot.GameData;

/// <summary>
/// Loads client-side game-data tables (<c>.shn</c>) from a BYO <c>ressystem</c> directory
/// the operator supplies, caching each parsed table. A bot is a synthetic <i>client</i>,
/// so it may read anything a real client reads — item/skill/class/map tables — which lets
/// feature code resolve game data (e.g. a skill's facing arc / cooldown / mana) from the
/// operator's client files instead of hard-coding it.
///
/// <para><b>Boundary (see PROJECT_PLAN "Data-source boundary"):</b> this reads
/// <i>client</i> SHNs only. Server-only tables (<c>NPC.txt</c>, <c>*Server.shn</c>, the
/// shine text tables) are NOT a legitimate runtime source unless the operator actually
/// has that server's files — don't load them here.</para>
///
/// <para>Same BYO data dir the [1801] checksums use (default
/// <c>Z:/ClientProd2/ressystem</c>); nothing is shipped or committed. Thread-safe.</para>
/// </summary>
public sealed class ClientData
{
    private readonly string _dataDir;
    private readonly ConcurrentDictionary<string, ShnTable?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<int, QuestDef>? _quests;
    private readonly object _questLock = new();

    public ClientData(string dataDir) => _dataDir = dataDir;

    /// <summary>The BYO client data directory tables are loaded from.</summary>
    public string DataDir => _dataDir;

    /// <summary>Load a client SHN table by name (e.g. "ActiveSkill", "ItemInfo",
    /// "ClassName"), cached after the first read. Returns null if the file isn't present
    /// in the data dir or fails to parse (callers fall back to their defaults).</summary>
    public ShnTable? Table(string name) => _cache.GetOrAdd(name, n =>
    {
        var path = Path.Combine(_dataDir, n + ".shn");
        try { return File.Exists(path) ? ShnTable.Load(path) : null; }
        catch { return null; }
    });

    /// <summary>Look up an <c>ActiveSkill</c> row by its skill id and project the combat-
    /// relevant fields. Null if the table is unavailable or the id isn't found. This is
    /// the data the (future) data-driven cast keys off — facing arc, cast-while-moving,
    /// cooldown, range, and mana — instead of the current hard-coded heuristic.</summary>
    public SkillInfo? Skill(int skillId)
    {
        var t = Table("ActiveSkill");
        if (t is null) return null;
        // The id column is "ID" in the client ActiveSkill table (verified against the BYO
        // ressystem file). Fall back to "id" defensively in case of casing differences.
        var row = t.FindByLong("ID", skillId) ?? t.FindByLong("id", skillId);
        if (row is null) return null;
        return new SkillInfo(
            Id: skillId,
            UsableDegree: GetInt(row, "UsableDegree"),
            IsMovingSkill: GetInt(row, "IsMovingSkill") != 0,
            DelayTimeMs: GetInt(row, "DlyTime"),
            Range: GetInt(row, "Range"),
            Sp: GetInt(row, "SP"));
    }

    /// <summary>Look up a mob/NPC by its id in the client <c>MobInfo</c> table and project
    /// the display fields — the bot reports only numeric <c>mobId</c>s from briefinfo, so
    /// this is how a name ("Teleport Gate"), level, and max-HP get attached. Null if the
    /// table is unavailable or the id isn't found. Client data, so always legitimate.</summary>
    public MobData? Mob(int mobId)
    {
        var t = Table("MobInfo");
        if (t is null) return null;
        var row = t.FindByLong("ID", mobId) ?? t.FindByLong("id", mobId);
        if (row is null) return null;
        return new MobData(
            Id: mobId,
            Name: GetStr(row, "Name"),
            InxName: GetStr(row, "InxName"),
            Level: GetInt(row, "Level"),
            MaxHp: GetInt(row, "MaxHP"),
            IsNpc: GetInt(row, "IsNPC") != 0,
            IsPlayerSide: GetInt(row, "IsPlayerSide") != 0,
            Type: GetInt(row, "Type"));
    }

    /// <summary>Resolve a map id to its short name (e.g. 17 → "Urg") from the client
    /// <c>MapInfo</c> table. A transition packet carries only the map <b>id</b>; the
    /// client (and so the bot) resolves the name here — never from the wire. Null if the
    /// table/id is missing.</summary>
    public string? MapName(int mapId)
    {
        var t = Table("MapInfo");
        var row = t?.FindByLong("ID", mapId) ?? t?.FindByLong("id", mapId);
        if (row is null) return null;
        var n = GetStr(row, "MapName");
        return string.IsNullOrEmpty(n) ? null : n;
    }

    /// <summary>The display name of an item id (e.g. for a shop list) from client
    /// <c>ItemInfo</c>. Empty if missing.</summary>
    public string ItemName(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>The display name of a skill id from client <c>ActiveSkill</c> (col "Name").
    /// Empty if missing. Lets the bot resolve a learned-skill id (e.g. find the one named
    /// "Heal") without hard-coding ids.</summary>
    public string SkillName(int skillId)
    {
        var t = Table("ActiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>True if the mob id is a huntable enemy: not a shop NPC, not player-side
    /// (a town guard reads <c>IsPlayerSide!=0</c> and must be skipped), and not a gatherable
    /// resource node (<c>Type==9</c> = herb/wood/mushroom). The combat target filter — keeps
    /// the bot from auto-attacking guards or harvest nodes. Verified live (Town Guard 9908
    /// IsPlayerSide=2; Pinky/Orc=0; Herb/Mushroom Type=9). Unknown ids (no client data) are
    /// treated as huntable so we don't silently skip a real mob.</summary>
    public bool IsHuntableEnemy(int mobId)
    {
        var m = Mob(mobId);
        if (m is null) return true; // no data — don't filter out a potential mob
        return !m.IsNpc && !m.IsPlayerSide && m.Type != ResourceNodeType;
    }

    /// <summary>Look up a quest definition by its (wire) quest id from the bespoke
    /// <c>QuestData.shn</c> — StartNPC, level/class gate, kill/collect objectives, rewards
    /// and the Start/Action/Finish scripts. Parsed once and cached. Null if missing.
    /// This is how the quest driver knows which NPC to visit and what the quest wants,
    /// without hard-coding any of it.</summary>
    public QuestDef? Quest(int questId)
    {
        var q = Quests;
        return q.TryGetValue(questId, out var def) ? def : null;
    }

    /// <summary>All decoded quests, keyed by id (loaded once from QuestData.shn).</summary>
    public IReadOnlyDictionary<int, QuestDef> Quests
    {
        get
        {
            if (_quests is not null) return _quests;
            lock (_questLock)
            {
                if (_quests is null)
                {
                    try { _quests = QuestData.Load(Path.Combine(_dataDir, "QuestData.shn")); }
                    catch { _quests = new Dictionary<int, QuestDef>(); }
                }
            }
            return _quests;
        }
    }

    /// <summary>Resolve a quest dialog/title id to its text from the standard-SHN
    /// <c>QuestDialog.shn</c> (the indices used by quest scripts' <c>SAY n</c> and a quest's
    /// Title/Description). Empty if missing.</summary>
    public string QuestDialog(int dialogId)
    {
        var t = Table("QuestDialog");
        var row = t?.FindByLong("ID", dialogId) ?? t?.FindByLong("id", dialogId);
        return row is null ? "" : GetStr(row, "Dialog");
    }

    /// <summary>MobInfo <c>Type</c> value for a gatherable resource node (herb/wood/mushroom).</summary>
    public const int ResourceNodeType = 9;

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && ShnTable.TryToLong(v, out var l) ? (int)l : 0;

    private static string GetStr(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? v?.ToString() ?? "" : "";
}

/// <summary>Display fields of a <c>MobInfo</c> row: the human-readable <see cref="Name"/>
/// (e.g. "Teleport Gate", "Uruga"), the <see cref="InxName"/> (internal id like
/// "Gate_Town"), plus <see cref="Level"/>/<see cref="MaxHp"/> and whether it's an
/// <see cref="IsNpc"/> (vs a monster) — enough to label/triage what the bot sees.</summary>
public sealed record MobData(int Id, string Name, string InxName, int Level, int MaxHp, bool IsNpc,
    bool IsPlayerSide = false, int Type = 0);

/// <summary>Combat-relevant fields of an <c>ActiveSkill</c> row, projected from the client
/// table. <see cref="UsableDegree"/> = the facing arc the target must be within (the cast
/// fails otherwise — the root cause behind the earlier SKILLBASH_CAST_FAIL); 0 means no
/// facing requirement. <see cref="IsMovingSkill"/> = castable while moving (no STOP needed).
/// <see cref="DelayTimeMs"/> = cooldown (ms). <see cref="Range"/> = cast range (0 = melee).
/// <see cref="Sp"/> = mana cost.</summary>
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp);
