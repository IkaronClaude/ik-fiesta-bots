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
    private IReadOnlyDictionary<string, int>? _skillIdByInx; // ActiveSkill InxName -> skill ID
    private readonly object _skillInxLock = new();

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
            Sp: GetInt(row, "SP"),
            UseClass: GetInt(row, "UseClass"),
            // MaxWC = the skill's weapon-damage coefficient. >0 = a real damage skill (Slice&Dice/Bone
            // Slicer/Fatal Slash); 0 = a utility/no-damage skill (Snearing Kick, Concussive Charge). Lets
            // the driver pick DAMAGE skills for the kite-chip so a fled mob keeps bleeding vs regenerating.
            MaxWc: GetInt(row, "MaxWC"));
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

    /// <summary>Item fields from client <c>ItemInfo</c> for shop eval: <see cref="ItemData.UseClass"/>
    /// (class line — Fighter 2–7, 0 = all), <see cref="ItemData.DemandLv"/> (level to use/equip),
    /// <see cref="ItemData.Grade"/> (rarity), <see cref="ItemData.EquipSlot"/> (the <c>Equip</c> slot),
    /// and <see cref="ItemData.IsScroll"/> (a skill scroll — <c>ItemUseSkill=="UseSkill"</c>; USE it to
    /// learn the skill named the same as the item, e.g. "Slice and Dice [02]"). null if unknown.</summary>
    public ItemData? Item(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        if (row is null) return null;
        return new ItemData(itemId, GetStr(row, "Name"), GetInt(row, "UseClass"), GetInt(row, "DemandLv"),
            GetInt(row, "Grade"), GetInt(row, "Equip"), GetStr(row, "ItemUseSkill") == "UseSkill",
            GetInt(row, "Type"), GetInt(row, "ItemGradeType"),
            GetInt(row, "Class"), GetInt(row, "MaxLot"), GetInt(row, "SellPrice"),
            // TwoHand=1 → a 2-handed weapon (occupies the weapon AND off-hand slot); ShieldAC>0 → a shield
            // (off-hand). A shield can't be worn with a 2H weapon — the driver uses these to avoid the
            // infinite "equip shield → server rejects → re-equip" loop on a 2H wielder (operator 2026-07-07).
            GetInt(row, "TwoHand") != 0, GetInt(row, "ShieldAC"));
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

    /// <summary>The ACTIVE-skill id a skill scroll teaches, or -1 if the item isn't a skill scroll
    /// (or no matching skill). A scroll's <c>ItemInfo.InxName</c> equals the <c>ActiveSkill.InxName</c>
    /// of the skill it teaches (e.g. scroll item 4720 "Bone Slicer [01]" InxName <c>SeverBone01</c> →
    /// ActiveSkill id 20). <c>ItemUseSkill</c> is only the generic use-handler ("UseSkill"), NOT the
    /// skill id — so we join on InxName. Lets the leveler avoid buying a scroll for a skill it already
    /// knows: <c>if HasSkill(ScrollSkillId(itemId)) skip</c>.</summary>
    public int ScrollSkillId(int itemId)
    {
        var it = Table("ItemInfo");
        var row = it?.FindByLong("ID", itemId) ?? it?.FindByLong("id", itemId);
        if (row is null) return -1;
        if (GetStr(row, "ItemUseSkill") != "UseSkill") return -1; // not a skill scroll
        var inx = GetStr(row, "InxName");
        if (string.IsNullOrEmpty(inx)) return -1;
        return SkillIdByInx().TryGetValue(inx, out var id) ? id : -1;
    }

    /// <summary>The prerequisite ACTIVE-skill id a skill must already have learned before it can itself
    /// be learned — the client <c>ActiveSkill.DemandSk</c> column holds the prereq skill's <c>InxName</c>
    /// (e.g. Fatal Slash [02] / <c>RedSlash02</c> has <c>DemandSk="RedSlash01"</c> = Fatal Slash [01]).
    /// Returns 0 if there is no prereq ("-"/empty) or it can't be resolved. Lets the learn-from-bag sweep
    /// skip a rank-[02] scroll until rank-[01] is learned — the server refuses the out-of-order USE, which
    /// otherwise loops forever re-using the unlearnable scroll and starves the learnable ones.</summary>
    public int SkillPrereqId(int skillId)
    {
        var t = Table("ActiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        if (row is null) return 0;
        var dsk = GetStr(row, "DemandSk");
        if (string.IsNullOrEmpty(dsk) || dsk == "-") return 0;
        return SkillIdByInx().TryGetValue(dsk, out var id) ? id : 0;
    }

    private IReadOnlyDictionary<string, int> SkillIdByInx()
    {
        if (_skillIdByInx is { } cached) return cached;
        lock (_skillInxLock)
        {
            if (_skillIdByInx is { } c2) return c2;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var t = Table("ActiveSkill");
            if (t is not null)
                foreach (var row in t.Rows)
                {
                    var inx = GetStr(row, "InxName");
                    if (!string.IsNullOrEmpty(inx)) map[inx] = GetInt(row, "ID");
                }
            return _skillIdByInx = map;
        }
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

    /// <summary>Where a mob type lives, from the client <c>MobCoordinate.shn</c> (the table the
    /// real client uses to draw the quest-log minimap marker): map name + spawn-area centre. A
    /// mob can have several rows (multiple spawn patches); we pick the one with the largest
    /// Width×Height (the main field — the densest grind spot), ignoring the zero-area point
    /// markers. Null if the table/mob is missing. Pure client data — this is how the quest
    /// driver decides which map to travel to for an objective, with no server files.</summary>
    public MobLocation? MobCoordinate(int mobId, string? preferMap = null)
    {
        var t = Table("MobCoordinate");
        if (t is null) return null;
        MobLocation? best = null, onPrefer = null;
        long bestArea = -1, preferArea = -1;
        foreach (var row in t.Rows)
        {
            if (GetInt(row, "Mob_ID") != mobId) continue;
            var map = GetStr(row, "MapName");
            if (string.IsNullOrEmpty(map)) continue;
            long area = (long)GetInt(row, "Width") * GetInt(row, "Height");
            var loc = new MobLocation(mobId, map, GetInt(row, "CenterX"), GetInt(row, "CenterY"),
                GetInt(row, "Width"), GetInt(row, "Height"));
            // Prefer the largest spawn ON THE CURRENT MAP (if the mob lives here, grind here
            // instead of traveling to a bigger patch elsewhere); else the largest overall.
            if (preferMap != null && string.Equals(map, preferMap, StringComparison.OrdinalIgnoreCase)
                && area > preferArea) { preferArea = area; onPrefer = loc; }
            if (area > bestArea) { bestArea = area; best = loc; }
        }
        return onPrefer ?? best;
    }

    /// <summary>All maps a mob spawns on (the largest spawn patch per map), from
    /// <c>MobCoordinate.shn</c>. Lets the caller pick a spawn on a map it can actually reach
    /// (e.g. one gated directly off the current map) instead of just the single biggest patch.</summary>
    public IReadOnlyList<MobLocation> MobCoordinatesAll(int mobId)
    {
        var t = Table("MobCoordinate");
        var byMap = new Dictionary<string, MobLocation>(StringComparer.OrdinalIgnoreCase);
        if (t is null) return Array.Empty<MobLocation>();
        foreach (var row in t.Rows)
        {
            if (GetInt(row, "Mob_ID") != mobId) continue;
            var map = GetStr(row, "MapName");
            if (string.IsNullOrEmpty(map)) continue;
            long area = (long)GetInt(row, "Width") * GetInt(row, "Height");
            var loc = new MobLocation(mobId, map, GetInt(row, "CenterX"), GetInt(row, "CenterY"),
                GetInt(row, "Width"), GetInt(row, "Height"));
            if (!byMap.TryGetValue(map, out var ex) || area > (long)ex.Width * ex.Height) byMap[map] = loc;
        }
        return byMap.Values.ToArray();
    }

    /// <summary>The <c>ItemInfo.UseClass</c> of an item — the item-gating class enum (a DIFFERENT
    /// enum from ClassName's ClassID; 1 = Any). 0 if missing. Used to pick a class-appropriate
    /// quest reward.</summary>
    public int ItemUseClass(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        return row is null ? 0 : GetInt(row, "UseClass");
    }

    /// <summary>The set of <c>UseClass</c> values that belong to a character's archetype line,
    /// keyed by the ClassName <c>ClassID</c> of the character (any tier in the line maps to the
    /// whole line). The UseClass enum runs: Fighter 2–7, Cleric 8–13, Archer 14–19, Mage 20–25,
    /// Joker 27–32, Sentinel/Savior 33–34 (26 is a non-class consumable slot). Lets the reward
    /// picker accept gear for the char's class at any promotion tier (lower/higher/promotion).</summary>
    public static IReadOnlySet<int> UseClassLineFor(int classId)
    {
        // classId is a ClassName ClassID; resolve its archetype, return that line's UseClass band.
        int[] band =
            classId is >= 1 and <= 5  ? [2, 3, 4, 5, 6, 7]        // Fighter line (incl. CleverFighter)
          : classId is >= 6 and <= 10 ? [8, 9, 10, 11, 12, 13]    // Cleric line
          : classId is >= 11 and <= 15 ? [14, 15, 16, 17, 18, 19] // Archer line
          : classId is >= 16 and <= 20 ? [20, 21, 22, 23, 24, 25] // Mage line
          : classId is >= 21 and <= 25 ? [27, 28, 29, 30, 31, 32] // Joker line
          : classId is >= 26 and <= 27 ? [33, 34]                 // Sentinel/Savior
          : [];
        return new HashSet<int>(band);
    }

    /// <summary>Build the complete CROSS-MAP gate web from the client nav tables
    /// <c>MapWayPoint.shn</c> (nodes: MapID, X=Undefined0, Y=Undefined1, MWP_Gate) +
    /// <c>MapLinkPoint.shn</c> (edges: MLP_FromID, MLP_ToID, MLP_OneWay_Street — 0-based row
    /// indices into MapWayPoint). A link whose two endpoints sit on DIFFERENT MapIDs is a
    /// map-to-map gate; the from-point's (X,Y) is where to stand to take it. This is the game's
    /// own routing graph — seeding it (vs the bot's slow auto-discovery) is what makes cross-map
    /// pathfinding reliable: every map has a few interconnected teleports, so a route always
    /// exists. Returns (fromMap, toMap, gateX, gateY); reverse direction added unless one-way.</summary>
    public IReadOnlyList<(string From, string To, uint X, uint Y, uint ToX, uint ToY)> BuildGateEdges()
    {
        var edges = new List<(string, string, uint, uint, uint, uint)>();
        var wp = Table("MapWayPoint");
        var lp = Table("MapLinkPoint");
        if (wp is null || lp is null) return edges;
        var rows = wp.Rows; int n = rows.Count;
        var nameCache = new Dictionary<int, string?>();
        string? NameOf(int id) => nameCache.TryGetValue(id, out var c) ? c : (nameCache[id] = MapName(id));
        foreach (var link in lp.Rows)
        {
            int from = GetInt(link, "MLP_FromID"), to = GetInt(link, "MLP_ToID");
            if (from < 0 || from >= n || to < 0 || to >= n) continue;
            var wf = rows[from]; var wt = rows[to];
            int mf = GetInt(wf, "MapID"), mt = GetInt(wt, "MapID");
            if (mf == mt) continue; // same-map waypoint edge (in-map nav), not a cross-map gate
            var fromName = NameOf(mf); var toName = NameOf(mt);
            if (fromName is null || toName is null) continue;
            // The from-point's (X,Y) is where to stand to take the gate; the to-point's (X,Y) is
            // where you EMERGE on the destination map — the entry point for costing the next hop.
            uint fx = (uint)GetInt(wf, "Undefined0"), fy = (uint)GetInt(wf, "Undefined1");
            uint tx = (uint)GetInt(wt, "Undefined0"), ty = (uint)GetInt(wt, "Undefined1");
            edges.Add((fromName, toName, fx, fy, tx, ty));
            if (GetInt(link, "MLP_OneWay_Street") == 0)
                edges.Add((toName, fromName, tx, ty, fx, fy));
        }
        return edges;
    }

    /// <summary>All town-portal destinations from <c>TownPortal.shn</c> (rows:
    /// <c>Index, MinLevel, TP_GroupNo, MapName, X=Undefined0, Y=Undefined1</c>). A portal NPC
    /// standing in any map of a <c>GroupNo</c> network offers warps to the OTHER maps in the same
    /// group; you pick a destination by its (global) row <c>Index</c> — the <c>dest</c> byte for
    /// the portal packet (0x181A). <c>X</c>/<c>Y</c> is the arrival coord on that map, which sits
    /// at/next to the map's portal NPC (so it doubles as "where the portal NPC is"). Used to add
    /// town-portal edges to the routing graph. Returns empty if the table is absent.</summary>
    public IReadOnlyList<PortalDest> BuildPortalDests()
    {
        var outp = new List<PortalDest>();
        var tp = Table("TownPortal");
        if (tp is null) return outp;
        int i = 0;
        foreach (var r in tp.Rows)
        {
            var map = GetStr(r, "MapName");
            // The destination index sent to the portal is the (global) row ordinal — 0=RouN,
            // 1=RouVal01, 2=Eld, … — matching TownPortalAsync's `dest`. Read it positionally so
            // we don't depend on an "Index" column that may be the tool's row number.
            if (!string.IsNullOrWhiteSpace(map))
                outp.Add(new PortalDest(i, GetInt(r, "TP_GroupNo"), map, GetInt(r, "MinLevel"),
                    (uint)GetInt(r, "Undefined0"), (uint)GetInt(r, "Undefined1")));
            i++;
        }
        return outp;
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

/// <summary>Shop-eval fields of an <c>ItemInfo</c> row. <see cref="IsScroll"/> = a skill scroll
/// (USE to learn the skill named the same as the item); otherwise an equip if <see cref="EquipSlot"/>
/// is a real slot. <see cref="UseClass"/> = the class line that may use it (Fighter 2–7, 0 = all),
/// <see cref="DemandLv"/> = the level required, <see cref="Grade"/> = rarity tier. <see cref="GradeType"/>
/// (client ItemInfo.shn column <c>ItemGradeType</c>) is the VENDOR-TRASH signal: verified against
/// server ground truth (ItemInfo table) that every plain smith-bought armor piece (Leather/Chain
/// Boots/Helmet/Pants, Buckler — the exact "basic starter gear" the bot auto-equips) is
/// <c>ItemGradeType=0</c>, while every named/event variant (e.g. "Solar Eclipse Leather Boots") is
/// &gt;=1 — so 0 = ordinary/replaceable gear (safe to sell once outgrown), &gt;=1 = a special/named
/// drop worth keeping regardless of level (operator 2026-06-26: "dropped 'special' gear is a
/// DIFFERENT (higher) rarity — never sell those").</summary>
public sealed record ItemData(int Id, string Name, int UseClass, int DemandLv, int Grade,
    int EquipSlot, bool IsScroll, int Type = 0, int GradeType = 0, int ItemClass = 0,
    int MaxLot = 0, int SellPrice = 0, bool TwoHand = false, int ShieldAc = 0);

/// <summary>Where a mob type spawns, from client <c>MobCoordinate.shn</c>: the
/// <see cref="Map"/> short-name and the <see cref="CenterX"/>/<see cref="CenterY"/> of its
/// main spawn field (with the field <see cref="Width"/>/<see cref="Height"/>). The quest
/// driver travels to <see cref="Map"/> and grinds around the centre.</summary>
public sealed record MobLocation(int MobId, string Map, int CenterX, int CenterY, int Width, int Height);

/// <summary>One town-portal destination from <c>TownPortal.shn</c>: within the <see cref="GroupNo"/>
/// portal network, selecting <see cref="Index"/> at any portal NPC of that group warps to
/// <see cref="Map"/> (arriving near <see cref="X"/>,<see cref="Y"/>), gated by <see cref="MinLevel"/>.
/// <see cref="Index"/> is the <c>dest</c> byte for the portal packet (0x181A).</summary>
public sealed record PortalDest(int Index, int GroupNo, string Map, int MinLevel, uint X, uint Y);

/// <summary>Combat-relevant fields of an <c>ActiveSkill</c> row, projected from the client
/// table. <see cref="UsableDegree"/> = the facing arc the target must be within (the cast
/// fails otherwise — the root cause behind the earlier SKILLBASH_CAST_FAIL); 0 means no
/// facing requirement. <see cref="IsMovingSkill"/> = castable while moving (no STOP needed).
/// <see cref="DelayTimeMs"/> = cooldown (ms). <see cref="Range"/> = cast range (0 = melee).
/// <see cref="Sp"/> = mana cost.</summary>
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp, int UseClass = 0, int MaxWc = 0);
