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
    private IReadOnlyDictionary<string, int>? _skillIdByInx;   // ActiveSkill  InxName -> skill ID
    private IReadOnlyDictionary<string, int>? _passiveIdByInx; // PassiveSkill InxName -> skill ID (SEPARATE id space)
    private readonly object _skillInxLock = new();
    private IReadOnlySet<uint>? _moveBlockAbstates; // AbState.AbStataIndex set that immobilizes (stun/root)
    private readonly object _abstateLock = new();

    public ClientData(string dataDir) => _dataDir = dataDir;

    /// <summary>The BYO client data directory tables are loaded from.</summary>
    public string DataDir => _dataDir;

    /// <summary>Load a client SHN table by name (e.g. "ActiveSkill", "ItemInfo",
    /// "ClassName"), cached after the first read. Returns null if the file isn't present
    /// in the data dir or fails to parse (callers fall back to their defaults).</summary>
    public ShnTable? Table(string name)
    {
        if (_cache.TryGetValue(name, out var hit)) return hit;
        var path = Path.Combine(_dataDir, name + ".shn");
        try
        {
            if (!File.Exists(path)) { NoteTableFailure(name, "file not present"); return null; }
            var t = ShnTable.Load(path);
            // ⛔ ONLY a SUCCESSFUL load is cached. This used to be _cache.GetOrAdd(...), which stored the
            // NULL from a failed/missing read forever — so one transient miss (data dir not mounted yet,
            // a partial read) permanently answered "this table does not exist" for the whole process, with
            // the exception swallowed silently. Live 2026-08-06: MapWayPoint/MapLinkPoint came back null
            // once, BuildGateEdges returned 0 edges, the map graph latched as "seeded" with nothing in it,
            // and the bot was stranded in a dungeon with one routable exit, dying on repeat.
            // A cache must hold ANSWERS, never failures — leaving it uncached simply retries next call.
            _cache[name] = t;
            return t;
        }
        catch (Exception ex) { NoteTableFailure(name, ex.Message); return null; }
    }

    private readonly ConcurrentDictionary<string, string> _tableFailures = new(StringComparer.OrdinalIgnoreCase);
    private void NoteTableFailure(string name, string why) => _tableFailures[name] = why;

    /// <summary>Tables that failed to load, name → reason. Empty when everything read cleanly. Surfaced so a
    /// downstream "I have no data" symptom (an empty nav graph, no item names) can name its actual cause
    /// instead of being silently absorbed.</summary>
    public IReadOnlyDictionary<string, string> TableFailures => _tableFailures;

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
            // ActiveSkill.DemandType separates real COMBAT skills from gathering/event toys. Verified over
            // every one of this character's 27 learned skills: DemandType 2 covers Mining, Ride Mover,
            // Water Cannon, Throw a Water Balloon, Cake/Soda summons and all the Korean event skills (18 of
            // them), while every combat skill — Slice and Dice, Bone Slicer, Fatal Slash, Concussive Charge,
            // Snearing Kick, Vitality — is 0, 3 or 6. A DATA test, replacing a name blocklist that kept
            // leaking new toys (operator P1 2026-08-06).
            DemandType: GetInt(row, "DemandType"),
            // CastTime = the CAST ANIMATION length (ActiveSkill.shn col 26), distinct from DlyTime (the
            // cooldown). While it runs the character is locked in the cast, so firing another skill during
            // it is wasted — that is how castRotation ended up sending FIVE casts in 18ms, each one's STOP
            // cancelling the melee swing stream (see the CEASE_FIRE ticket / packets-JcqFresh.log).
            CastTimeMs: GetInt(row, "CastTime"),
            Range: GetInt(row, "Range"),
            Sp: GetInt(row, "SP"),
            UseClass: GetInt(row, "UseClass"),
            // MaxWC = the skill's weapon-damage coefficient. >0 = a real damage skill (Slice&Dice/Bone
            // Slicer/Fatal Slash); 0 = a utility/no-damage skill (Snearing Kick, Concussive Charge). Lets
            // the driver pick DAMAGE skills for the kite-chip so a fled mob keeps bleeding vs regenerating.
            MaxWc: GetInt(row, "MaxWC"),
            // STUN: does any abnormal-state effect (StaNameA..D) apply a STUN? A stun skill (Concussive Charge =
            // StaBattleBlowStun, 100%) freezes the target so the bot can safely kite+heal on low HP (operator
            // "stun and kite on low hp"). Detected by the "Stun" substring in the state name — data-driven, NO
            // hardcoded skill id. MaxWc is 0 for these (utility), so they were never in the damage rotation.
            Stun: GetStr(row, "StaNameA").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameB").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameC").Contains("Stun", System.StringComparison.OrdinalIgnoreCase)
               || GetStr(row, "StaNameD").Contains("Stun", System.StringComparison.OrdinalIgnoreCase),
            // HEAL: EffectType==5 is the client's "heal applied" effect (verified over ALL Heal01-20 +
            // GreatHeal01-05 in ActiveSkill.shn — 197 skills carry it). This is the DATA-DRIVEN way to
            // categorise a heal (operator 2026-07-23: don't string-match the skill NAME). A direct heal.
            Heal: GetInt(row, "EffectType") == 5,
            // HEAL-OVER-TIME: a skill that applies a healing ABSTATE — decided by the abstate's STAT EFFECT,
            // NOT its name (operator 2026-07-23: name-matching "Heal" sucks; go by stats). Resolve each
            // StaNameA..D → AbState.shn → its SubAbState → and check whether that SubAbState applies the
            // HP-RECOVER-OVER-TIME action (SubAbState ActionIndex == 30). VERIFIED via the real priest HoT:
            // Restore → StaRestore, and the party HoT MultiProtect → StaMultiHeal, both resolve to a
            // SubAbState with ActionIndexB=30 (subType 29); the poison DoT StaNorthPoison uses ActionIndex 27
            // (HP damage) — so 30 cleanly means "recovers HP over time". Boss self-heals (KarenDotHeal etc.)
            // also use 30, but they never enter the picture: the caller filters over the bot's OWN learned skills.
            HealOverTime: IsHealOverTimeState(GetStr(row, "StaNameA")) || IsHealOverTimeState(GetStr(row, "StaNameB"))
                       || IsHealOverTimeState(GetStr(row, "StaNameC")) || IsHealOverTimeState(GetStr(row, "StaNameD")));
    }

    /// <summary>The English class name for a <c>ClassName.shn</c> <c>ClassID</c> (e.g. 1→"Fighter",
    /// 6→"Cleric", plus promotion tiers), or null if the table/id isn't found. For the bots.ikaron.uk
    /// status page (operator P1 2026-07-28) so it shows a class NAME, not the raw ClassID. ClassName.shn
    /// columns: ClassID(Byte), acPrefix, acEngName, acLocalName — acEngName is the English display name.</summary>
    public string? ClassName(int classId)
    {
        var row = Table("ClassName")?.FindByLong("ClassID", classId);
        if (row is null) return null;
        var name = GetStr(row, "acEngName");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // The SubAbState "action" index that means RECOVER HP OVER TIME (per-tick HP add). Empirically the heal
    // discriminator: all heal abstates (StaRestore/StaMultiHeal/*DotHeal) carry it; the poison DoT uses 27.
    private const int HpRecoverOverTimeAction = 30;
    private HashSet<string>? _healOverTimeStates; // AbState InxNames whose SubAbState recovers HP over time

    /// <summary>True if <paramref name="staName"/> is an abnormal-state that HEALS OVER TIME — resolved by
    /// its actual stat effect (AbState → SubAbState → ActionIndex == HpRecoverOverTimeAction), not its name.
    /// Built once from AbState.shn + SubAbState.shn (both client-side).</summary>
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
                        // SubAbState InxNames that apply the HP-recover-over-time action (any ActionIndexA..D).
                        var recoverSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var r in sub.Rows)
                            if (GetInt(r, "ActionIndexA") == HpRecoverOverTimeAction || GetInt(r, "ActionIndexB") == HpRecoverOverTimeAction
                             || GetInt(r, "ActionIndexC") == HpRecoverOverTimeAction || GetInt(r, "ActionIndexD") == HpRecoverOverTimeAction)
                            { var n = GetStr(r, "InxName"); if (!string.IsNullOrEmpty(n)) recoverSubs.Add(n); }
                        // AbStates whose SubAbState is one of those → the heal-over-time states.
                        foreach (var r in ab.Rows)
                            if (recoverSubs.Contains(GetStr(r, "SubAbState")))
                            { var n = GetStr(r, "InxName"); if (!string.IsNullOrEmpty(n)) set.Add(n); }
                    }
                    _healOverTimeStates = set;
                }
        return _healOverTimeStates.Contains(staName);
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
            Type: GetInt(row, "Type"),
            GradeType: GetInt(row, "GradeType"));   // 0 = normal mob; >=1 = named boss/elite (e.g. Mara GradeType 1)
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

    /// <summary>The map's DISPLAY name — MapInfo.shn's <c>Name</c> column (e.g. "Elderine Cemetery"), as
    /// opposed to <see cref="MapName"/> which returns the internal CODE (<c>EldCem01</c>). Both columns
    /// have always been in the table; we only ever read the code, so every surface showed operators a
    /// filename-ish token where the game shows a place. Accepts the CODE because that is what the bot
    /// tracks as CurrentMap. Null when unknown — callers fall back to the code rather than inventing one.</summary>
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
    /// <summary>True if the map (by MapName) is an INDOOR/dungeon/instance map — MapInfo.shn <c>InSide=1</c>
    /// (e.g. RouTemDn01 "Luminous Stone 1"); field/town maps are InSide=0. A solo field-leveling char must not
    /// route into a dungeon's dense packs to hunt a quest mob (operator 2026-07-16 "prefer field over dungeon").
    /// Built once from MapInfo. False if unknown.</summary>
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

    /// <summary>The display name of a PASSIVE skill id from client <c>PassiveSkill</c> (col "Name").
    /// Passives live in their OWN table with their OWN id space — see <see cref="ScrollSkill"/>.</summary>
    public string PassiveSkillName(int skillId)
    {
        var t = Table("PassiveSkill");
        var row = t?.FindByLong("ID", skillId) ?? t?.FindByLong("id", skillId);
        return row is null ? "" : GetStr(row, "Name");
    }

    /// <summary>The skill a skill book/scroll teaches: its id and WHICH TABLE that id belongs to.
    /// Returns <c>(-1, false)</c> if the item isn't a skill book (or no matching skill).
    /// <para>⚠️ There are TWO skill tables with SEPARATE, OVERLAPPING id spaces: <c>ActiveSkill</c>
    /// (castable skills) and <c>PassiveSkill</c> (masteries — "One Handed Sword Mastery [01]",
    /// "Bravery Mastery [01]"). Both start at id 0 and collide: ActiveSkill 0 = "Slice and Dice [01]",
    /// PassiveSkill 0 = "Bravery Mastery [01]"; ActiveSkill 9/10 are real skills and so are
    /// PassiveSkill 9/10 (One Handed Sword Mastery [01]/[02]). So a bare skill id is MEANINGLESS
    /// without knowing its table — hence the <c>Passive</c> flag, which must be carried all the way
    /// through to <see cref="Session.ZoneView.HasSkill"/>.</para>
    /// A book's <c>ItemInfo.InxName</c> equals the <c>InxName</c> of the skill it teaches
    /// (scroll item 4720 "Bone Slicer [01]" InxName <c>SeverBone01</c> → ActiveSkill id 20;
    /// book item 7613 "One Handed Sword Mastery [01]" InxName <c>OHSwdMastery01</c> → PassiveSkill id 9).
    /// <c>ItemUseSkill</c> is only the generic use-handler ("UseSkill"), NOT the skill id — so we join
    /// on InxName, ACTIVE first then PASSIVE.
    /// <para>(2026-08-05: resolving against ActiveSkill ONLY is why the mastery books never learned —
    /// they returned -1, and the leveler's learn-from-bag sweep skips anything with id &lt; 0, so three
    /// mastery books sat unlearned in Bot7170's bag while <c>scrollEligible</c> refused to re-buy them.)</para></summary>
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

    /// <summary>The ACTIVE-skill id a skill scroll teaches, or -1. Thin wrapper over
    /// <see cref="ScrollSkill"/> that DISCARDS the passive flag — only safe where the caller has
    /// already established the skill is an active. Prefer <see cref="ScrollSkill"/>.</summary>
    public int ScrollSkillId(int itemId)
    {
        var (id, passive) = ScrollSkill(itemId);
        return passive ? -1 : id;
    }

    /// <summary>The prerequisite skill a skill must already have learned before it can itself be learned —
    /// the <c>DemandSk</c> column (in the SAME table as the skill) holds the prereq's <c>InxName</c>
    /// (Fatal Slash [02] / <c>RedSlash02</c> → <c>DemandSk="RedSlash01"</c>; One Handed Sword Mastery [02]
    /// / <c>OHSwdMastery02</c> → <c>DemandSk="OHSwdMastery01"</c>). A prereq is always in the same table as
    /// the skill that demands it, so the returned id shares <paramref name="passive"/>'s id space.
    /// <para>Returns <b>-1</b> when there is no prereq ("-"/empty/unresolvable) — NOT 0, because
    /// <b>0 is a real skill id in both tables</b> (ActiveSkill 0 = "Slice and Dice [01]", the genuine
    /// prereq of "Slice and Dice [02]"; PassiveSkill 0 = "Bravery Mastery [01]"). The old 0-means-none
    /// sentinel made those two prereqs invisible — the same "id 0 is a REAL skill" trap already
    /// documented for the login skill list.</para>
    /// Lets the learn-from-bag sweep skip a rank-[02] book until rank-[01] is learned — the server
    /// refuses the out-of-order USE, which otherwise loops forever re-using the unlearnable book.</summary>
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

    /// <summary>True if the abstate index (the value carried in NC_BAT_ABSTATESET_CMD /
    /// NC_BAT_ABSTATERESET_CMD) IMMOBILIZES the target — i.e. the character cannot move while it is
    /// active (stun / root / entangle / sleep). Derived data-driven from the client SHNs, no baked ids:
    /// an <c>AbState</c> row (keyed by <c>AbStataIndex</c>) → its <c>SubAbState</c> → the SubAbState row
    /// whose any <c>ActionIndex*</c> equals the immobilize action (19). This is how the bot knows a
    /// server MOVEFAIL is caused by a ROOT (so it must NOT learn that tile as a wall — the grid-poisoning
    /// bug that wedged it in the JCQ instance) and to WAIT rather than thrash. Verified: the JCQ clone
    /// applies <c>StaQuestEntangle</c> (AbStataIndex 290 → SubStaQuestEntangle, ActionIndexA=19).</summary>
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
                // 1) SubAbState InxNames whose any Action*Index == the immobilize action (19).
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
                // 2) AbState rows referencing an immobilize SubAbState → collect their AbStataIndex (the
                //    value on the wire in ABSTATESET/RESET).
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

    // A stun ALSO carries the action-block action (25) alongside immobilize (19); a root/entangle carries
    // 19 alone. Derived from the client SHNs exactly like the immobilize rule — the InxNames only VALIDATE
    // it, they are never matched on. Validated over all 107 immobilizing SubAbState rows (2026-08-06):
    //   19 + 25  (n=82) -> 46 "…Stun" names (SubStaBattleBlowStun, SubStaShockBladeStun, …)
    //   19 alone (n=25) -> 16 "…Entangle" + 7 "…Bind" (SubStaSpiritThornEntangle, SubStaMarloneEntangle, …)
    private const int ActionBlockActionIndex = 25;
    private IReadOnlySet<uint>? _stunAbstates;

    /// <summary>True if this abstate is a STUN (blocks actions as well as movement), as opposed to a
    /// ROOT/entangle which only blocks movement. Both are move-blocking, so
    /// <see cref="IsMoveBlockingAbstate"/> is true for either; this splits them so the two can be
    /// counted and reacted to separately (a root still lets us cast; a stun does not).</summary>
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

    /// <summary>The human NAME of a quest for LOGGING — QuestData title id → QuestDialog.shn text — formatted
    /// "Name(q{id})" so the name leads but the id stays greppable. "q{id}" if unknown. Operator 2026-07-18:
    /// never log a bare quest id.</summary>
    public string QuestName(int questId)
    {
        var q = Quest(questId);
        var n = q is not null ? QuestDialog(q.Title) : "";
        return string.IsNullOrEmpty(n) ? $"q{questId}" : $"{n}(q{questId})";
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
        MobLocation? best = null, onPrefer = null, bestField = null, onPreferField = null;
        long bestArea = -1, preferArea = -1, bestFieldArea = -1, preferFieldArea = -1;
        foreach (var row in t.Rows)
        {
            if (GetInt(row, "Mob_ID") != mobId) continue;
            var map = GetStr(row, "MapName");
            if (string.IsNullOrEmpty(map)) continue;
            long area = (long)GetInt(row, "Width") * GetInt(row, "Height");
            var loc = new MobLocation(mobId, map, GetInt(row, "CenterX"), GetInt(row, "CenterY"),
                GetInt(row, "Width"), GetInt(row, "Height"));
            bool onCur = preferMap != null && string.Equals(map, preferMap, StringComparison.OrdinalIgnoreCase);
            // Prefer the largest spawn ON THE CURRENT MAP (if the mob lives here, grind here
            // instead of traveling to a bigger patch elsewhere); else the largest overall.
            if (onCur && area > preferArea) { preferArea = area; onPrefer = loc; }
            if (area > bestArea) { bestArea = area; best = loc; }
            // FIELD-OVER-DUNGEON (operator 2026-07-16): a solo field-leveling char must hunt the sparse FIELD
            // spawn, never a dense dungeon/instance pack (MapInfo InSide=1, e.g. RouTemDn01 — packs of 6 that
            // net-negative death-loop the bot). Track the best FIELD (InSide=0) spawn separately and prefer it;
            // fall back to a dungeon spawn only when the mob has NO field spawn at all.
            if (!MapInside(map))
            {
                if (onCur && area > preferFieldArea) { preferFieldArea = area; onPreferField = loc; }
                if (area > bestFieldArea) { bestFieldArea = area; bestField = loc; }
            }
        }
        return onPreferField ?? bestField ?? onPrefer ?? best;
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
    /// <summary>Pick a VALID (hairType, hairColor, faceShape) for a class+gender, straight from the
    /// client's own character-creation tables. Returns (0,0,0) only if the tables cannot be read.
    /// <para>⛔ WE SENT 0/0/0 ON EVERY CREATE, WHICH IS NOT A VALID APPEARANCE. The client ships the
    /// creation rules and we were ignoring them:</para>
    /// <para>• <c>HairInfo.shn</c> has a column per class (fighter/archer/cleric/mage/Joker/Sentinel).
    /// The value is the GENDER that hairstyle belongs to — 1 for the male cuts ("Wolf Cut", "Hero Cut"),
    /// 2 for the female ones ("Pig Tails", "Long Hair") — so a hair is only legal when its column matches
    /// the character's gender.</para>
    /// <para>• <c>FaceInfo.shn</c> has a column per class AND gender (FM_A_Male, FM_A_Female, …).
    /// <b>Face id 0 is 0 for every single class/gender combination</b> — nobody can pick it — yet 0 is
    /// exactly what we were sending.</para>
    /// <para>• <c>HairColorInfo.shn</c> lists the legal colours; the real client used 12.</para>
    /// <para>Gender encoding here is the SHN's own (1=male, 2=female) while the wire's gender bit is 0/1,
    /// hence the +1. Reading the tables is what keeps this honest: no baked ids, and it fixes every class
    /// at once rather than special-casing the one that happened to fail.</para></summary>
    public (byte HairType, byte HairColor, byte FaceShape) PickAppearance(int classId, byte genderBit)
    {
        var shnGender = (uint)(genderBit + 1);          // wire 0/1 -> SHN 1=male, 2=female
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
        // Face columns are FM_<classletter>_<Male|Female>; non-zero means selectable.
        var faceCol = $"FM_{classCol[0].ToString().ToUpperInvariant()}_{(genderBit == 0 ? "Male" : "Female")}";
        if (classCol == "Joker") faceCol = $"FM_J_{(genderBit == 0 ? "Male" : "Female")}";
        if (classCol == "Sentinel") faceCol = $"FM_S_{(genderBit == 0 ? "Male" : "Female")}";
        if (Table("FaceInfo") is { } fi)
            foreach (var row in fi.Rows)
                if (ToU32(row, faceCol) != 0) { face = (byte)ToU32(row, "ID"); break; }
        return (hair, colour, face);
    }

    private static uint ToU32(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && v is not null && uint.TryParse(v.ToString(), out var n) ? n : 0;

    /// <summary>The RACE a class must be created as — <c>RaceNameInfo.shn</c> ids: 1=Human, 2=Elf,
    /// 3=DarkElf (0 is the blank row and is NOT a valid race).
    /// <para>⛔ WE WERE SENDING RACE 0 ON EVERY CREATE, and it is why Archer creation always failed with
    /// <c>AVATAR_CREATEFAIL err=132</c> while Fighter/Cleric/Mage happened to survive it. Ground truth,
    /// three independent sources agreeing:</para>
    /// <para>• A REAL client create in Z:/LongCaptureNoDc.pcapng: <c>char_shape = 05 01 0c 00</c>, which
    /// against the PDB bitfields (race:2, chrclass:5, gender:1) decodes to race=<b>1</b>, chrclass=1
    /// (Fighter) — never 0.</para>
    /// <para>• <c>World00_Character.tCharacterShape</c> on this server: nRace=<b>1</b> for every
    /// Fighter/Cleric-line character, nRace=<b>3</b> for every Mage-line one. No row has race 0, and no
    /// class-11 row exists at all — nothing had ever been created as an Archer.</para>
    /// <para>• Operator, 2026-08-11: <i>"archers are always elves, not human"</i> — and RaceNameInfo says
    /// Elf is exactly the id (2) left unclaimed between Human (Fighter/Cleric) and DarkElf (Mage).</para>
    /// <para>The class→race pairing itself is not in any client table I could find (ClassName.shn has only
    /// ClassID/prefix/names; RaceNameInfo.shn names the races but does not join them to classes), so it is
    /// stated here once, against the SAME class-line ladder <see cref="UseClassLineFor"/> already uses,
    /// rather than being scattered. Race is a 2-BIT field — 0-3 is the whole space.</para>
    /// Returns 0 for an unknown class, which the caller must treat as "don't override".</summary>
    public static int RaceForClass(int classId) => classId switch
    {
        >= 1 and <= 5   => 1,   // Fighter line   — Human
        >= 6 and <= 10  => 1,   // Cleric line    — Human
        >= 11 and <= 15 => 2,   // Archer line    — Elf
        >= 16 and <= 20 => 3,   // Mage line      — DarkElf
        >= 21 and <= 25 => 3,   // Joker line     — DarkElf (unverified: no Joker exists on this server yet)
        _ => 0,
    };

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

    /// <summary>A string cell, with SHN's fixed-width padding removed. Columns are fixed-width and
    /// NUL-padded (MapName is 12 bytes, Name 32), so the raw value of "RouCos03" is
    /// <c>"RouCos03    "</c>. That PRINTS identically to the trimmed form — including through JSON —
    /// so a padded value looks perfectly correct while failing every string comparison against it. That is
    /// exactly how MapDisplayName silently returned null for every map (found 2026-08-06). Trim once, here,
    /// so no caller has to know.</summary>
    private static string GetStr(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? (v?.ToString() ?? "").Trim(' ').Trim() : "";
}

/// <summary>Display fields of a <c>MobInfo</c> row: the human-readable <see cref="Name"/>
/// (e.g. "Teleport Gate", "Uruga"), the <see cref="InxName"/> (internal id like
/// "Gate_Town"), plus <see cref="Level"/>/<see cref="MaxHp"/> and whether it's an
/// <see cref="IsNpc"/> (vs a monster) — enough to label/triage what the bot sees.</summary>
public sealed record MobData(int Id, string Name, string InxName, int Level, int MaxHp, bool IsNpc,
    bool IsPlayerSide = false, int Type = 0, int GradeType = 0);

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
public sealed record SkillInfo(int Id, int UsableDegree, bool IsMovingSkill, int DelayTimeMs, int Range, int Sp, int UseClass = 0, int MaxWc = 0, bool Stun = false, bool Heal = false, bool HealOverTime = false, int CastTimeMs = 0, int DemandType = 0)
{
    /// <summary>Gathering / mount / event-toy skill rather than a combat one — see the DemandType note
    /// at the read site. The watch page hides these unless "show misc" is ticked.</summary>
    public bool IsMisc => DemandType == 2;
};
