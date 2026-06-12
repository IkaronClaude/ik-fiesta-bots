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

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && ShnTable.TryToLong(v, out var l) ? (int)l : 0;
}

/// <summary>Combat-relevant fields of an <c>ActiveSkill</c> row, projected from the client
/// table. <see cref="UsableDegree"/> = the facing arc the target must be within (the cast
/// fails otherwise — the root cause behind the earlier SKILLBASH_CAST_FAIL); 0 means no
/// facing requirement. <see cref="IsMovingSkill"/> = castable while moving (no STOP needed).
/// <see cref="DelayTimeMs"/> = cooldown (ms). <see cref="Range"/> = cast range (0 = melee).
/// <see cref="Sp"/> = mana cost.</summary>
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp);
