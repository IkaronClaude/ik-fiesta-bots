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
            IsNpc: GetInt(row, "IsNPC") != 0);
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

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && ShnTable.TryToLong(v, out var l) ? (int)l : 0;

    private static string GetStr(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? v?.ToString() ?? "" : "";
}

/// <summary>Display fields of a <c>MobInfo</c> row: the human-readable <see cref="Name"/>
/// (e.g. "Teleport Gate", "Uruga"), the <see cref="InxName"/> (internal id like
/// "Gate_Town"), plus <see cref="Level"/>/<see cref="MaxHp"/> and whether it's an
/// <see cref="IsNpc"/> (vs a monster) — enough to label/triage what the bot sees.</summary>
public sealed record MobData(int Id, string Name, string InxName, int Level, int MaxHp, bool IsNpc);

/// <summary>Combat-relevant fields of an <c>ActiveSkill</c> row, projected from the client
/// table. <see cref="UsableDegree"/> = the facing arc the target must be within (the cast
/// fails otherwise — the root cause behind the earlier SKILLBASH_CAST_FAIL); 0 means no
/// facing requirement. <see cref="IsMovingSkill"/> = castable while moving (no STOP needed).
/// <see cref="DelayTimeMs"/> = cooldown (ms). <see cref="Range"/> = cast range (0 = melee).
/// <see cref="Sp"/> = mana cost.</summary>
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp);
