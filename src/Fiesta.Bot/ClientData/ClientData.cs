using System.Collections.Concurrent;
using FiestaLibReloaded.Shn;

namespace Fiesta.Bot.GameData;

/// <summary>Loads client-side game-data tables ( .shn ) from a BYO ressystem directory the operator supplies, caching each…</summary>
public sealed class ClientData
{
    private readonly string _dataDir;
    private readonly ConcurrentDictionary<string, ShnTable?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<int, QuestDef>? _quests;
    private readonly object _questLock = new();
    private IReadOnlyDictionary<string, int>? _skillIdByInx;   // ActiveSkill  InxName -> skill ID
    private IReadOnlyDictionary<string, int>? _passiveIdByInx; // PassiveSkill InxName -> skill ID (SEPARATE id space)
    private readonly object _skillInxLock = new();
    private IReadOnlyDictionary<int, IReadOnlyList<MobLocation>>? _mobCoords; // Mob_ID -> every spawn patch
    private readonly object _mobCoordLock = new();
    private IReadOnlySet<uint>? _moveBlockAbstates; // AbState.AbStataIndex set that immobilizes (stun/root)
    private readonly object _abstateLock = new();

    public ClientData(string dataDir) => _dataDir = dataDir;

    /// <summary>The BYO client data directory tables are loaded from</summary>
    public string DataDir => _dataDir;

    /// <summary>The CLIENT ROOT — the directory holding ressystem , resmenu and friends as siblings (operator 2026-08-12: "do…</summary>
    public string? ClientRoot => Path.GetDirectoryName(_dataDir.TrimEnd(Path.DirectorySeparatorChar, '/'));

    /// <summary>Where the item icon ATLASES live: &amp;lt;client root&amp;gt;/resmenu/Icon</summary>
    public string? IconDir => ClientRoot is { } r ? Path.Combine(r, "resmenu", "Icon") : null;

    /// <summary>Where the per-map MINIMAP art lives: &amp;lt;client root&amp;gt;/resmenu/minimap</summary>
    public string? MinimapDir => ClientRoot is { } r ? Path.Combine(r, "resmenu", "minimap") : null;

    /// <summary>A map's minimap as a PNG, or null when the client ships no art for it</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _minimapPng =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[]? MinimapPng(string mapName)
    {
        if (_minimapPng.TryGetValue(mapName, out var hit)) return hit;
        var png = MinimapDir is { } dir ? MinimapImage.Png(dir, mapName) : null;
        // Cache HITS only. A null means "no art found *this time*" — which is also what a not-yet-populated BYO director…
        if (png is not null) _minimapPng[mapName] = png;
        return png;
    }

    /// <summary>The fraction of a map's grid that its MINIMAP IMAGE actually covers, read from the client's own MapViewInfo.sh…</summary>
    public (double X0, double Y0, double X1, double Y1)? MinimapWorldRect(string mapName, double gridWorldW, double gridWorldH)
    {
        var t = Table("MapViewInfo");
        if (t is null) return null;
        IReadOnlyDictionary<string, object?>? row = null;
        foreach (var r in t.Rows)
            if (string.Equals(GetStr(r, "MapName"), mapName, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
        if (row is null) return null;
        // 0..511 inclusive over the map; +1 on the ends because End is the last cell, not the edge
        const double Norm = 512.0;
        double sx = GetInt(row, "StartX"), sy = GetInt(row, "StartY");
        double ex = GetInt(row, "EndX") + 1, ey = GetInt(row, "EndY") + 1;
        if (ex <= sx || ey <= sy) return null;
        return (sx / Norm * gridWorldW,
                (1 - ey / Norm) * gridWorldH,       // flip Y into world orientation
                ex / Norm * gridWorldW,
                (1 - sy / Norm) * gridWorldH);
    }

    /// <summary>Which atlas cell draws this item, from ItemViewInfo.shn ( IconFile + IconIndex )</summary>
    public (string File, int Index)? ItemIcon(int itemId)
    {
        var t = Table("ItemViewInfo");
        var row = t is null ? null : (t.FindByLong("ID", itemId) ?? t.FindByLong("id", itemId));
        if (row is null) return null;
        var file = GetStr(row, "IconFile");
        if (string.IsNullOrEmpty(file) || file == "-") return null;
        return (file, GetInt(row, "IconIndex"));
    }

    /// <summary>A skill's icon cell + display text from ActiveSkillView.shn — the same IconFile / IconIndex atlas scheme the i…</summary>
    public (string File, int Index, string? Name, string? Descript)? SkillView(int skillId)
    {
        var t = Table("ActiveSkillView");
        var row = t?.FindByLong("ID", skillId);
        if (row is null) return null;
        var file = GetStr(row, "IconFile");
        if (string.IsNullOrEmpty(file) || file == "-") return null;
        return (file, GetInt(row, "IconIndex"), GetStr(row, "InxName"), GetStr(row, "Descript"));
    }

    /// <summary>A skill's icon as a PNG, cut from the same atlases as the item icons (ClericSk00 and friends already ship alon…</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _skillPng = new();
    public byte[]? SkillIconPng(int skillId)
    {
        if (_skillPng.TryGetValue(skillId, out var hit)) return hit;
        if (IconDir is not { } dir || SkillView(skillId) is not { } sv) return null;
        var path = ResolveIconFile(dir, sv.File);
        var png = path is null ? null : IconAtlas.IconPng(path, sv.Index);
        if (png is not null) _skillPng[skillId] = png;   // cache hits only, same reasoning as the minimap
        return png;
    }

    /// <summary>A buff/debuff's icon cell + text from AbStateView.shn ( iconFile / icon , lower-cased column names here, unlik…</summary>
    public (string? File, int Index, string? Name, string? Descript)? AbStateView(int abStateId)
    {
        var t = Table("AbStateView");
        var row = t?.FindByLong("ID", abStateId);
        if (row is null) return null;
        var file = GetStr(row, "iconFile");
        if (string.IsNullOrEmpty(file) || file == "-") file = null;
        return (file, GetInt(row, "icon"), GetStr(row, "inxName"), GetStr(row, "Descript"));
    }

    /// <summary>Resolve a WIRE abstate index (what NC_BRIEFINFO_ABSTATE_CHANGE carries</summary>
    public (string? File, int Index, string? Name, string? Descript)? AbStateByWireIndex(int wireIndex)
    {
        var t = Table("AbState");
        if (t is null) return AbStateView(wireIndex);       // no join table — try the direct read
        foreach (var row in t.Rows)
            if (GetInt(row, "AbStataIndex") == wireIndex)
            {
                var view = AbStateView(GetInt(row, "ID"));
                // Even with no view row, AbState itself knows the internal name — better than a number
                return view ?? (null, 0, GetStr(row, "InxName"), null);
            }
        return null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _abStatePng = new();
    public byte[]? AbStateIconPng(int abStateId)
    {
        if (_abStatePng.TryGetValue(abStateId, out var hit)) return hit;
        if (IconDir is not { } dir || AbStateByWireIndex(abStateId) is not { } av || av.File is null) return null;
        var path = ResolveIconFile(dir, av.File);
        var png = path is null ? null : IconAtlas.IconPng(path, av.Index);
        if (png is not null) _abStatePng[abStateId] = png;
        return png;
    }

    /// <summary>The item icon as a PNG, or null when we have no art for it</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]?> _iconPng = new();
    public byte[]? ItemIconPng(int itemId) => _iconPng.GetOrAdd(itemId, id =>
    {
        if (IconDir is not { } dir || ItemIcon(id) is not { } icon) return null;
        var path = ResolveIconFile(dir, icon.File);
        return path is null ? null : IconAtlas.IconPng(path, icon.Index);
    });

    // THE ATLAS FILENAMES DISAGREE WITH THE TABLE ON CASE, AND THE HOST RUNS ON LINUX
    private Dictionary<string, string>? _iconFiles;
    private string? ResolveIconFile(string dir, string name)
    {
        if (_iconFiles is null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    map[Path.GetFileNameWithoutExtension(f)] = f;   // extension case is irrelevant too
            }
            catch { /* no icon dir — the caller renders name tiles */ }
            _iconFiles = map;
        }
        return _iconFiles.TryGetValue(name, out var path) ? path : null;
    }

    /// <summary>Load a client SHN table by name</summary>
    public ShnTable? Table(string name)
    {
        if (_cache.TryGetValue(name, out var hit)) return hit;
        // A FAILURE MUST BE REMEMBERED TOO. Only successes were cached, so every call for a table that is not
        // there re-ran File.Exists + a full load -- and client data is an NFS mount whose stat latency measured
        // 5-383ms from the pod. With four bots asking per tick that is the whole tick budget spent re-asking a
        // question already answered: the leveler fell to ~6.4 SECONDS per bot.* call (557 calls in an hour).
        // Re-checked on a TTL rather than never, so mounting the data later still recovers without a restart.
        if (_tableFailures.TryGetValue(name, out var prev) && DateTime.UtcNow - prev.At < TableRetry) return null;
        var path = Path.Combine(_dataDir, name + ".shn");
        try
        {
            if (!File.Exists(path)) { NoteTableFailure(name, "file not present"); return null; }
            var t = ShnTable.Load(path);
            // ONLY a SUCCESSFUL load is cached
            _cache[name] = t;
            _tableFailures.TryRemove(name, out _);
            return t;
        }
        catch (Exception ex) { NoteTableFailure(name, ex.Message); return null; }
    }

    private static readonly TimeSpan TableRetry = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (string Why, DateTime At)> _tableFailures = new(StringComparer.OrdinalIgnoreCase);
    private void NoteTableFailure(string name, string why) => _tableFailures[name] = (why, DateTime.UtcNow);

    /// <summary>Tables that failed to load, name → reason</summary>
    public IReadOnlyDictionary<string, string> TableFailures =>
        _tableFailures.ToDictionary(kv => kv.Key, kv => kv.Value.Why, StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up an ActiveSkill row by its skill id and project the combat- relevant fields</summary>
    public SkillInfo? Skill(int skillId)
    {
        var t = Table("ActiveSkill");
        if (t is null) return null;
        // The id column is "ID" in the client ActiveSkill table (verified against the BYO ressystem file)
        var row = t.FindByLong("ID", skillId) ?? t.FindByLong("id", skillId);
        if (row is null) return null;
        return new SkillInfo(
            Id: skillId,
            UsableDegree: GetInt(row, "UsableDegree"),
            IsMovingSkill: GetInt(row, "IsMovingSkill") != 0,
            DelayTimeMs: GetInt(row, "DlyTime"),
            // ActiveSkill.DemandType separates real COMBAT skills from gathering/event toys
            DemandType: GetInt(row, "DemandType"),
            CastTimeMs: GetInt(row, "CastTime"),
            Range: GetInt(row, "Range"),
            // WHO THE SKILL LANDS ON. ActiveSkill First/Last are target-side codes, verified across the table:
            // Last 0=enemy (Slice and Dice, Concussive Charge), 1=self (Vitality), 2=party (Protect, Sacrifice,
            // Invigorate), 3=ally (Heal, Cure). First is where the cast STARTS, which is why Demoralizing Hit is
            // (1,0) -- self-cast, lands on an enemy -- and Sacrifice is (1,2). Keying on the SIDE rather than on
            // whether a state reads as a buff keeps "debuff yourself" and "buff an enemy" expressible.
            CastFrom: GetInt(row, "First"),
            LandsOn: GetInt(row, "Last"),
            Sp: GetInt(row, "SP"),
            UseClass: GetInt(row, "UseClass"),
            // MaxWC = the skill's weapon-damage coefficient
            MaxWc: GetInt(row, "MaxWC"),
            // MaxMA IS THE OTHER HALF OF "DOES THIS SKILL DEAL DAMAGE", AND IT WAS MISSING
            MaxMa: GetInt(row, "MaxMA"),
            Stun: GetStr(row, "StaNameA").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameB").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameC").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameD").Contains("Stun", System.StringComparison.OrdinalIgnoreCase),
            // HEAL: EffectType==5 is the client's "heal applied" effect (verified over ALL Heal01-20 + GreatHeal01-05 in Act…
            Heal: GetInt(row, "EffectType") == 5,
            // HEAL-OVER-TIME: a skill that applies a healing ABSTATE — decided by the abstate's STAT EFFECT, NOT its name (o…
            HealOverTime: IsHealOverTimeState(GetStr(row, "StaNameA")) || IsHealOverTimeState(GetStr(row, "StaNameB"))
                       || IsHealOverTimeState(GetStr(row, "StaNameC")) || IsHealOverTimeState(GetStr(row, "StaNameD")));
    }

    /// <summary>The English class name for a ClassName.shn ClassID</summary>
    public string? ClassName(int classId)
    {
        var row = Table("ClassName")?.FindByLong("ClassID", classId);
        if (row is null) return null;
        var name = GetStr(row, "acEngName");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // The SubAbState "action" index that means RECOVER HP OVER TIME (per-tick HP add)
    private const int HpRecoverOverTimeAction = 30;
    private HashSet<string>? _healOverTimeStates; // AbState InxNames whose SubAbState recovers HP over time

    /// <summary>True if is an abnormal-state that HEALS OVER TIME — resolved by its actual stat effect (AbState → SubAbState →…</summary>
    private bool IsHealOverTimeState(string? staName)
    {
        if (string.IsNullOrEmpty(staName) || staName == "-") return false;
        if (_healOverTimeStates is null)
            lock (_abstateLock)
                if (_healOverTimeStates is null)
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var sub = Table("SubAbState");
                    var ab = Table("AbState");
                    if (sub != null && ab != null)
                    {
                        // SubAbState InxNames that apply the HP-recover-over-time action (any ActionIndexA..D)
                        var recoverSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in sub.Rows)
                            if (GetInt(r, "ActionIndexA") == HpRecoverOverTimeAction || GetInt(r, "ActionIndexB") == HpRecoverOverTimeAction
                             || GetInt(r, "ActionIndexC") == HpRecoverOverTimeAction || GetInt(r, "ActionIndexD") == HpRecoverOverTimeAction)
                            { var n = GetStr(r, "InxName"); if (!string.IsNullOrEmpty(n)) recoverSubs.Add(n); }
                        // AbStates whose SubAbState is one of those → the heal-over-time states
                        foreach (var r in ab.Rows)
                            if (recoverSubs.Contains(GetStr(r, "SubAbState")))
                            { var n = GetStr(r, "InxName"); if (!string.IsNullOrEmpty(n)) set.Add(n); }
                    }
                    _healOverTimeStates = set;
                }
        return _healOverTimeStates.Contains(staName);
    }

    /// <summary>Look up a mob/NPC by its id in the client MobInfo table and project the display fields — the bot reports only…</summary>
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
            Type: GetInt(row, "Type"),
            GradeType: GetInt(row, "GradeType"));   // 0 = normal mob; >=1 = named boss/elite (e.g. Mara GradeType 1)
    }

    /// <summary>Resolve a map id to its short name</summary>
    public string? MapName(int mapId)
    {
        var t = Table("MapInfo");
        var row = t?.FindByLong("ID", mapId) ?? t?.FindByLong("id", mapId);
        if (row is null) return null;
        var n = GetStr(row, "MapName");
        return string.IsNullOrEmpty(n) ? null : n;
    }

    /// <summary>The map's DISPLAY name — MapInfo.shn's Name column</summary>
    public string? MapDisplayName(string? mapCode)
    {
        if (string.IsNullOrEmpty(mapCode)) return null;
        var t = Table("MapInfo");
        if (t is null) return null;
        foreach (var row in t.Rows)
            if (string.Equals(GetStr(row, "MapName"), mapCode, StringComparison.OrdinalIgnoreCase))
            {
                var n = GetStr(row, "Name");
                return string.IsNullOrEmpty(n) ? null : n;
            }
        return null;
    }

    private HashSet<string>? _insideMaps;
    /// <summary>True if the map (by MapName) is an INDOOR/dungeon/instance map — MapInfo.shn InSide=1</summary>
    public bool MapInside(string? mapName)
    {
        if (string.IsNullOrEmpty(mapName)) return false;
        if (_insideMaps is null)
        {
            _insideMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var t = Table("MapInfo");
            if (t != null)
                foreach (var row in t.Rows)
                    if (GetInt(row, "InSide") != 0)
                    { var n2 = GetStr(row, "MapName"); if (!string.IsNullOrEmpty(n2)) _insideMaps.Add(n2); }
        }
        return _insideMaps.Contains(mapName);
    }

    /// <summary>The display name of an item id</summary>
    public string ItemName(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>Item fields from client ItemInfo for shop eval: (class line — Fighter 2–7, 0 = all), (level to use/equip), (ra…</summary>
    public ItemData? Item(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        if (row is null) return null;
        return new ItemData(itemId, GetStr(row, "Name"), GetInt(row, "UseClass"), GetInt(row, "DemandLv"),
            GetInt(row, "Grade"), GetInt(row, "Equip"), GetStr(row, "ItemUseSkill") == "UseSkill",
            GetInt(row, "Type"), GetInt(row, "ItemGradeType"),
            GetInt(row, "Class"), GetInt(row, "MaxLot"), GetInt(row, "SellPrice"),
            // TwoHand=1 → a 2-handed weapon (occupies the weapon AND off-hand slot); ShieldAC>0 → a shield (off-hand)
            GetInt(row, "TwoHand") != 0, GetInt(row, "ShieldAC"),
            GetInt(row, "BuyPrice"),
            // WeaponType tells us whether our AUTO-ATTACK reaches: 2 bow, 10 crossbow, 3 staff, 11 wand are RANGED; 1/4/5/13…
            GetInt(row, "WeaponType"));
    }

    /// <summary>The display name of a skill id from client ActiveSkill (col "Name")</summary>
    public string SkillName(int skillId)
    {
        var t = Table("ActiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>The display name of a PASSIVE skill id from client PassiveSkill (col "Name")</summary>
    public string PassiveSkillName(int skillId)
    {
        var t = Table("PassiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>The skill a skill book/scroll teaches: its id and WHICH TABLE that id belongs to</summary>
    public (int Id, bool Passive) ScrollSkill(int itemId)
    {
        var it = Table("ItemInfo");
        var row = it?.FindByLong("ID", itemId) ?? it?.FindByLong("id", itemId);
        if (row is null) return (-1, false);
        if (GetStr(row, "ItemUseSkill") != "UseSkill") return (-1, false); // not a skill book
        var inx = GetStr(row, "InxName");
        if (string.IsNullOrEmpty(inx)) return (-1, false);
        if (SkillIdByInx(passive: false).TryGetValue(inx, out var active)) return (active, false);
        if (SkillIdByInx(passive: true).TryGetValue(inx, out var passive)) return (passive, true);
        return (-1, false);
    }

    /// <summary>The ACTIVE-skill id a skill scroll teaches, or -1</summary>
    public int ScrollSkillId(int itemId)
    {
        var (id, passive) = ScrollSkill(itemId);
        return passive ? -1 : id;
    }

    /// <summary>The prerequisite skill a skill must already have learned before it can itself be learned — the DemandSk column…</summary>
    public int SkillPrereqId(int skillId, bool passive = false)
    {
        var t = Table(passive ? "PassiveSkill" : "ActiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        if (row is null) return -1;
        var dsk = GetStr(row, "DemandSk");
        if (string.IsNullOrEmpty(dsk) || dsk == "-") return -1;
        return SkillIdByInx(passive).TryGetValue(dsk, out var id) ? id : -1;
    }

    private IReadOnlyDictionary<string, int> SkillIdByInx(bool passive)
    {
        if ((passive ? _passiveIdByInx : _skillIdByInx) is { } cached) return cached;
        lock (_skillInxLock)
        {
            if ((passive ? _passiveIdByInx : _skillIdByInx) is { } c2) return c2;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var t = Table(passive ? "PassiveSkill" : "ActiveSkill");
            if (t is not null)
                foreach (var row in t.Rows)
                {
                    var inx = GetStr(row, "InxName");
                    if (!string.IsNullOrEmpty(inx)) map[inx] = GetInt(row, "ID");
                }
            return passive ? (_passiveIdByInx = map) : (_skillIdByInx = map);
        }
    }

    /// <summary>True if the abstate index (the value carried in NC_BAT_ABSTATESET_CMD / NC_BAT_ABSTATERESET_CMD) IMMOBILIZES t…</summary>
    public bool IsMoveBlockingAbstate(uint abStataIndex) => MoveBlockAbstates().Contains(abStataIndex);

    private const int ImmobilizeActionIndex = 19;

    private IReadOnlySet<uint> MoveBlockAbstates()
    {
        if (_moveBlockAbstates is { } cached) return cached;
        lock (_abstateLock)
        {
            if (_moveBlockAbstates is { } c2) return c2;
            var set = new HashSet<uint>();
            var sub = Table("SubAbState");
            var ab = Table("AbState");
            if (sub is not null && ab is not null)
            {
                // 1) SubAbState InxNames whose any Action*Index == the immobilize action (19)
                var immobilizeSubs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in sub.Rows)
                {
                    var inx = GetStr(row, "InxName");
                    if (string.IsNullOrEmpty(inx)) continue;
                    if (GetInt(row, "ActionIndexA") == ImmobilizeActionIndex ||
                        GetInt(row, "ActionIndexB") == ImmobilizeActionIndex ||
                        GetInt(row, "ActionIndexC") == ImmobilizeActionIndex ||
                        GetInt(row, "ActionIndexD") == ImmobilizeActionIndex)
                        immobilizeSubs.Add(inx);
                }
                // 2) AbState rows referencing an immobilize SubAbState → collect their AbStataIndex (the value on the wire in AB…
                foreach (var row in ab.Rows)
                {
                    var subName = GetStr(row, "SubAbState");
                    if (!string.IsNullOrEmpty(subName) && immobilizeSubs.Contains(subName))
                        set.Add((uint)GetInt(row, "AbStataIndex"));
                }
            }
            return _moveBlockAbstates = set;
        }
    }

    private const int ActionBlockActionIndex = 25;
    private IReadOnlySet<uint>? _stunAbstates;

    // AbState InxName -> AbStataIndex (the value that travels on the wire in ABSTATESET/RESET)
    private Dictionary<string, uint>? _abstateIndexByName;

    /// <summary>The abstate INDICES a skill applies, from its StaNameA..D resolved through AbState.InxName → AbStataIndex</summary>
    public IReadOnlyList<uint> SkillAbstates(int skillId)
    {
        var t = Table("ActiveSkill");
        var row = t is null ? null : (t.FindByLong("ID", skillId) ?? t.FindByLong("id", skillId));
        if (row is null) return [];
        if (_abstateIndexByName is null)
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (Table("AbState") is { } ab)
                foreach (var r in ab.Rows)
                {
                    var n = GetStr(r, "InxName");
                    if (!string.IsNullOrEmpty(n)) map[n] = (uint)GetInt(r, "AbStataIndex");
                }
            _abstateIndexByName = map;
        }
        List<uint>? outp = null;
        foreach (var col in (string[])["StaNameA", "StaNameB", "StaNameC", "StaNameD"])
        {
            var n = GetStr(row, col);
            // "-" is the client's empty marker in these columns
            if (string.IsNullOrEmpty(n) || n == "-") continue;
            if (_abstateIndexByName.TryGetValue(n, out var idx)) (outp ??= []).Add(idx);
        }
        return (IReadOnlyList<uint>?)outp ?? [];
    }

    /// <summary>True if this abstate is a STUN (blocks actions as well as movement), as opposed to a ROOT/entangle which only…</summary>
    public bool IsStunAbstate(uint abStataIndex) => StunAbstates().Contains(abStataIndex);

    private IReadOnlySet<uint> StunAbstates()
    {
        if (_stunAbstates is { } cached) return cached;
        lock (_abstateLock)
        {
            if (_stunAbstates is { } c2) return c2;
            var set = new HashSet<uint>();
            var sub = Table("SubAbState");
            var ab = Table("AbState");
            if (sub is not null && ab is not null)
            {
                var stunSubs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in sub.Rows)
                {
                    var inx = GetStr(row, "InxName");
                    if (string.IsNullOrEmpty(inx)) continue;
                    int a = GetInt(row, "ActionIndexA"), b = GetInt(row, "ActionIndexB"),
                        c = GetInt(row, "ActionIndexC"), d = GetInt(row, "ActionIndexD");
                    var immobilizes = a == ImmobilizeActionIndex || b == ImmobilizeActionIndex
                                   || c == ImmobilizeActionIndex || d == ImmobilizeActionIndex;
                    var blocksActions = a == ActionBlockActionIndex || b == ActionBlockActionIndex
                                     || c == ActionBlockActionIndex || d == ActionBlockActionIndex;
                    if (immobilizes && blocksActions) stunSubs.Add(inx);
                }
                foreach (var row in ab.Rows)
                {
                    var subName = GetStr(row, "SubAbState");
                    if (!string.IsNullOrEmpty(subName) && stunSubs.Contains(subName))
                        set.Add((uint)GetInt(row, "AbStataIndex"));
                }
            }
            return _stunAbstates = set;
        }
    }

    /// <summary>True if the mob id is a huntable enemy: not a shop NPC, not player-side (a town guard reads IsPlayerSide!=0 an…</summary>
    public bool IsHuntableEnemy(int mobId)
    {
        var m = Mob(mobId);
        if (m is null) return true; // no data — don't filter out a potential mob
        return !m.IsNpc && !m.IsPlayerSide && m.Type != ResourceNodeType;
    }

    /// <summary>Look up a quest definition by its (wire) quest id from the bespoke QuestData.shn — StartNPC, level/class gate,…</summary>
    public QuestDef? Quest(int questId)
    {
        var q = Quests;
        return q.TryGetValue(questId, out var def) ? def : null;
    }

    /// <summary>All decoded quests, keyed by id (loaded once from QuestData.shn)</summary>
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

    /// <summary>Resolve a quest dialog/title id to its text from the standard-SHN QuestDialog.shn (the indices used by quest s…</summary>
    public string QuestDialog(int dialogId)
    {
        var t = Table("QuestDialog");
        var row = t?.FindByLong("ID", dialogId) ?? t?.FindByLong("id", dialogId);
        return row is null ? "" : GetStr(row, "Dialog");
    }

    /// <summary>The human NAME of a quest for LOGGING — QuestData title id → QuestDialog.shn text — formatted "Name(q{id})" so…</summary>
    public string QuestName(int questId)
    {
        var q = Quest(questId);
        var n = q is not null ? QuestDialog(q.Title) : "";
        return string.IsNullOrEmpty(n) ? $"q{questId}" : $"{n}(q{questId})";
    }

    // INDEXED ONCE. Both of these used to scan every row of MobCoordinate.shn PER CALL, doing a string-keyed
    // lookup per column per row. Measured 2026-08-18 on the live bot: 3,523 rows x ~6 column reads x 23 calls
    // in a single tick, and npcCoord alone cost 980ms of one 2,977ms tick (one call peaked at 959ms). There are
    // only 878 distinct Mob_IDs, so the whole table collapses to one dictionary built on first use.
    private IReadOnlyDictionary<int, IReadOnlyList<MobLocation>> MobCoords()
    {
        if (_mobCoords is { } hit) return hit;
        lock (_mobCoordLock)
        {
            if (_mobCoords is { } inner) return inner;
            var by = new Dictionary<int, List<MobLocation>>();
            if (Table("MobCoordinate") is { } t)
            {
                // Resolve the column ordinals ONCE, then read cells by index rather than by name per row.
                int cMob = t.IndexOfColumn("Mob_ID"), cMap = t.IndexOfColumn("MapName");
                int cX = t.IndexOfColumn("CenterX"), cY = t.IndexOfColumn("CenterY");
                int cW = t.IndexOfColumn("Width"), cH = t.IndexOfColumn("Height");
                if (cMob >= 0 && cMap >= 0)
                {
                    for (var r = 0; r < t.RowCount; r++)
                    {
                        if (!ShnTable.TryToLong(t.Value(cMob, r), out var mob)) continue;
                        var map = t.Value(cMap, r) as string;
                        if (string.IsNullOrEmpty(map)) continue;
                        var loc = new MobLocation((int)mob, map, CellInt(t, cX, r), CellInt(t, cY, r),
                                                  CellInt(t, cW, r), CellInt(t, cH, r));
                        if (!by.TryGetValue((int)mob, out var list)) by[(int)mob] = list = new List<MobLocation>();
                        list.Add(loc);
                    }
                }
            }
            return _mobCoords = by.ToDictionary(k => k.Key, v => (IReadOnlyList<MobLocation>)v.Value);
        }
    }

    private static int CellInt(ShnTable t, int col, int row)
        => col >= 0 && ShnTable.TryToLong(t.Value(col, row), out var v) ? (int)v : 0;

    /// <summary>Where a mob type lives, from the client MobCoordinate.shn (the table the real client uses to draw the quest-log marker)</summary>
    public MobLocation? MobCoordinate(int mobId, string? preferMap = null)
    {
        if (!MobCoords().TryGetValue(mobId, out var rows)) return null;
        MobLocation? best = null, onPrefer = null, bestField = null, onPreferField = null;
        long bestArea = -1, preferArea = -1, bestFieldArea = -1, preferFieldArea = -1;
        foreach (var loc in rows)
        {
            long area = (long)loc.Width * loc.Height;
            bool onCur = preferMap != null && string.Equals(loc.Map, preferMap, StringComparison.OrdinalIgnoreCase);
            // Prefer the largest spawn ON THE CURRENT MAP (if the mob lives here, grind here instead of traveling to a bigger patch)
            if (onCur && area > preferArea) { preferArea = area; onPrefer = loc; }
            if (area > bestArea) { bestArea = area; best = loc; }
            // FIELD-OVER-DUNGEON (operator 2026-07-16): a solo field-leveling char must hunt the sparse FIELD spawn
            if (!MapInside(loc.Map))
            {
                if (onCur && area > preferFieldArea) { preferFieldArea = area; onPreferField = loc; }
                if (area > bestFieldArea) { bestFieldArea = area; bestField = loc; }
            }
        }
        return onPreferField ?? bestField ?? onPrefer ?? best;
    }

    /// <summary>All maps a mob spawns on (the largest spawn patch per map), from MobCoordinate.shn</summary>
    public IReadOnlyList<MobLocation> MobCoordinatesAll(int mobId)
    {
        if (!MobCoords().TryGetValue(mobId, out var rows)) return Array.Empty<MobLocation>();
        var byMap = new Dictionary<string, MobLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in rows)
            if (!byMap.TryGetValue(loc.Map, out var ex) || (long)loc.Width * loc.Height > (long)ex.Width * ex.Height)
                byMap[loc.Map] = loc;
        return byMap.Values.ToArray();
    }

    /// <summary>The ItemInfo.UseClass of an item — the item-gating class enum (a DIFFERENT enum from ClassName's ClassID; 1 =…</summary>
    public int ItemUseClass(int itemId)
    {
        var t = Table("ItemInfo");
        var row = t?.FindByLong("ID", itemId) ?? t?.FindByLong("id", itemId);
        return row is null ? 0 : GetInt(row, "UseClass");
    }

    /// <summary>The set of UseClass values that belong to a character's archetype line, keyed by the ClassName ClassID of the…</summary>
    public (byte HairType, byte HairColor, byte FaceShape) PickAppearance(int classId, byte genderBit)
    {
        // THE WIRE GENDER BIT IS INVERTED RELATIVE TO THE SHN's 1=male/2=female
        var shnGender = (uint)(genderBit == 0 ? 2 : 1);
        var classCol = classId switch
        {
            >= 1 and <= 5   => "fighter",
            >= 6 and <= 10  => "cleric",
            >= 11 and <= 15 => "archer",
            >= 16 and <= 20 => "mage",
            >= 21 and <= 25 => "Joker",
            _ => "Sentinel",
        };
        byte hair = 0, colour = 0, face = 0;
        if (Table("HairInfo") is { } hi)
            foreach (var row in hi.Rows)
                if (ToU32(row, classCol) == shnGender) { hair = (byte)ToU32(row, "ID"); break; }
        if (Table("HairColorInfo") is { } hc)
            foreach (var row in hc.Rows) { colour = (byte)ToU32(row, "ID"); break; }
        // Face columns are FM_ _ ; non-zero means selectable
        var faceCol = $"FM_{classCol[0].ToString().ToUpperInvariant()}_{(genderBit == 0 ? "Female" : "Male")}";
        if (classCol == "Joker") faceCol = $"FM_J_{(genderBit == 0 ? "Female" : "Male")}";
        if (classCol == "Sentinel") faceCol = $"FM_S_{(genderBit == 0 ? "Female" : "Male")}";
        if (Table("FaceInfo") is { } fi)
            foreach (var row in fi.Rows)
                if (ToU32(row, faceCol) != 0) { face = (byte)ToU32(row, "ID"); break; }
        return (hair, colour, face);
    }

    private static uint ToU32(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && v is not null && uint.TryParse(v.ToString(), out var n) ? n : 0;

    /// <summary>The RACE a class must be created as — RaceNameInfo.shn ids: 1=Human, 2=Elf, 3=DarkElf (0 is the blank row and…</summary>
    public static int RaceForClass(int classId) => classId switch
    {
        >= 1 and <= 5   => 1,   // Fighter line   — Human
        >= 6 and <= 10  => 1,   // Cleric line    — Human
        >= 11 and <= 15 => 2,   // Archer line    — Elf
        >= 16 and <= 20 => 3,   // Mage line      — DarkElf
        >= 21 and <= 25 => 3,   // Joker line     — DarkElf (unverified: no Joker exists on this server yet)
        _ => 0,
    };

    public (int MasteryType, int NeededPoints)? RecipeRequirement(int productId)
    {
        var t = Table("Produce");
        if (t is null) return null;
        var row = t.FindByLong("ProductID", productId);
        if (row is null) return null;
        return ((int)ToU32(row, "NeededMasteryType"), (int)ToU32(row, "NeededMasteryGain"));
    }

    /// <summary>Display name of a Produce mastery type (job) from ProduceView.shn</summary>
    public string? MasteryTypeName(int masteryType)
    {
        var t = Table("ProduceView");
        if (t is null) return null;
        foreach (var row in t.Rows)
            if ((int)ToU32(row, "MasteryType") == masteryType)
                return row.TryGetValue("Name", out var v) ? v?.ToString() : null;
        return null;
    }

    public bool UseClassAllows(int classId, int useClass)
    {
        if (classId is < 1 or > 27) return false;             // unknown/unselected class → no claim
        var t = Table("UseClassTypeInfo");
        if (t is null)
        {
            // Fallback ONLY when the table is missing: the old ladder, plus the all-class value it never knew about
            return useClass == 1 || UseClassLineFor(classId).Contains(useClass);
        }
        var row = t.FindByLong("UseClass", useClass);
        if (row is null) return false;                        // value not in the matrix → not usable
        // Flag columns are everything after the leading UseClass column, in ClassID order
        var flags = t.Columns.Where(c => !string.Equals(c.Name, "UseClass", StringComparison.OrdinalIgnoreCase))
                             .ToList();
        if (classId > flags.Count) return false;
        return ToU32(row, flags[classId - 1].Name) != 0;
    }

    /// <summary>APPROXIMATE — prefer , which reads the client's own UseClassTypeInfo matrix</summary>
    public static IReadOnlySet<int> UseClassLineFor(int classId)
    {
        // classId is a ClassName ClassID; resolve its archetype, return that line's UseClass band
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

    /// <summary>Build the complete CROSS-MAP gate web from the client nav tables MapWayPoint.shn (nodes: MapID, X=Undefined0,…</summary>
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
            // The from-point's (X,Y) is where to stand to take the gate; the to-point's (X,Y) is where you EMERGE on the des…
            uint fx = (uint)GetInt(wf, "Undefined0"), fy = (uint)GetInt(wf, "Undefined1");
            uint tx = (uint)GetInt(wt, "Undefined0"), ty = (uint)GetInt(wt, "Undefined1");
            edges.Add((fromName, toName, fx, fy, tx, ty));
            if (GetInt(link, "MLP_OneWay_Street") == 0)
                edges.Add((toName, fromName, tx, ty, fx, fy));
        }
        return edges;
    }

    /// <summary>All town-portal destinations from TownPortal.shn (rows: Index, MinLevel, TP_GroupNo, MapName, X=Undefined0, Y=…</summary>
    public IReadOnlyList<PortalDest> BuildPortalDests()
    {
        var outp = new List<PortalDest>();
        var tp = Table("TownPortal");
        if (tp is null) return outp;
        int i = 0;
        foreach (var r in tp.Rows)
        {
            var map = GetStr(r, "MapName");
            // The destination index sent to the portal is the (global) row ordinal — 0=RouN, 1=RouVal01, 2=Eld, … — matching…
            if (!string.IsNullOrWhiteSpace(map))
                outp.Add(new PortalDest(i, GetInt(r, "TP_GroupNo"), map, GetInt(r, "MinLevel"),
                    (uint)GetInt(r, "Undefined0"), (uint)GetInt(r, "Undefined1")));
            i++;
        }
        return outp;
    }

    /// <summary>MobInfo Type value for a gatherable resource node (herb/wood/mushroom)</summary>
    public const int ResourceNodeType = 9;

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && ShnTable.TryToLong(v, out var l) ? (int)l : 0;

    /// <summary>A string cell, with SHN's fixed-width padding removed</summary>
    private static string GetStr(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? (v?.ToString() ?? "").Trim(' ').Trim() : "";
}

/// <summary>Display fields of a MobInfo row: the human-readable</summary>
public sealed record MobData(int Id, string Name, string InxName, int Level, int MaxHp, bool IsNpc,
    bool IsPlayerSide = false, int Type = 0, int GradeType = 0);

/// <summary>Shop-eval fields of an ItemInfo row</summary>
public sealed record ItemData(int Id, string Name, int UseClass, int DemandLv, int Grade,
    int EquipSlot, bool IsScroll, int Type = 0, int GradeType = 0, int ItemClass = 0,
    int MaxLot = 0, int SellPrice = 0, bool TwoHand = false, int ShieldAc = 0, int BuyPrice = 0,
    int WeaponType = 0);

/// <summary>Where a mob type spawns, from client MobCoordinate.shn : the short-name and the / of its main spawn field (wit…</summary>
public sealed record MobLocation(int MobId, string Map, int CenterX, int CenterY, int Width, int Height);

/// <summary>One town-portal destination from TownPortal.shn : within the portal network, selecting at any portal NPC of th…</summary>
public sealed record PortalDest(int Index, int GroupNo, string Map, int MinLevel, uint X, uint Y);

/// <summary>Combat-relevant fields of an ActiveSkill row, projected from the client table</summary>
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp, int UseClass = 0, int MaxWc = 0, bool Stun = false, bool Heal = false, bool HealOverTime = false, int CastTimeMs = 0, int DemandType = 0, int MaxMa = 0, int CastFrom = 0, int LandsOn = 0)
{
    /// <summary>How hard this skill hits, whichever school it uses: for a weapon skill, for a spell</summary>
    public int Damage => Math.Max(MaxWc, MaxMa);

    /// <summary>Gathering / mount / event-toy skill rather than a combat one — see the DemandType note at the read site</summary>
    public bool IsMisc => DemandType == 2;
};
