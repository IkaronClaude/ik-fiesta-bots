using Fiesta.Bot.Manager;
using Fiesta.Bot.Pathfinding;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>The Lua-facing facade for ONE bot — the bot global a behaviour script drives</summary>
[MoonSharpUserData]
public sealed class BotApi
{
    private readonly BotManager _mgr;
    private readonly BotHandle _handle;
    private Script? _lua;

    internal BotApi(BotManager mgr, BotHandle handle)
    {
        _mgr = mgr;
        _handle = handle;
    }

    /// <summary>Attach the owning VM (so getters can build Lua tables)</summary>
    internal void AttachScript(Script lua) => _lua = lua;

    /// <summary>Set by the runner to receive the current state-machine state name as the Lua harness transitions, so the runne…</summary>
    internal Action<string>? StateReporter;

    /// <summary>Called by the state-machine harness on each transition to report the new state to C# (surfaced in the script s…</summary>
    public void __state(string name) => StateReporter?.Invoke(name);

    /// <summary>Can THIS character use an item/scroll whose ItemInfo UseClass is ?</summary>
    public bool canUseClass(int useClass)
    {
        var cd = _mgr.ClientData;
        if (cd is null) return false;                  // no client data → make no claim (driver fails closed)
        return cd.UseClassAllows(_handle.Class, useClass);
    }

    /// <summary>Publish what the driver is working on RIGHT NOW: the focused quest (0 for none), its own phase name, where it…</summary>
    public void setFocus(int questId, string phase, string dest, string reason) =>
        _handle.Focus = new Manager.BotFocus(questId, phase ?? "", dest ?? "", reason ?? "",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Set by the behaviour-graph runner so a state script can request a graph transition ( bot.requestState("stay_al…</summary>
    internal Action<string>? RequestStateHandler;

    /// <summary>Request a behaviour-graph transition to next tick</summary>
    public bool requestState(string state)
    {
        if (RequestStateHandler is null) return false;
        RequestStateHandler(state); return true;
    }

    public double skillDamageAvg(int id) => View?.SkillDamageAvg((ushort)id) ?? -1;

    /// <summary>How many landed hits have been sampled for this skill</summary>
    public int skillDamageSamples(int id) => View?.SkillDamageSamples((ushort)id) ?? 0;

    public double skillReadyInMs(int id)
    {
        var si = _mgr.ClientData?.Skill(id);
        if (si is null || View is null) return 0;      // unknown skill: never report a phantom cooldown
        return View.SkillReadyInMs((ushort)id, si.DelayTimeMs, si.CastTimeMs);
    }

    /// <summary>Skill info from client ActiveSkill (cooldown ms, SP cost, range, facing arc) so scripts can track cooldowns /…</summary>
    public DynValue skillInfo(int id)
    {
        var si = _mgr.ClientData?.Skill(id);
        if (si is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = id; t["name"] = _mgr.ClientData?.SkillName(id) ?? "";
        t["cooldownMs"] = si.DelayTimeMs; t["sp"] = si.Sp; t["range"] = si.Range;
        // castTimeMs = the CAST ANIMATION length
        t["castTimeMs"] = si.CastTimeMs;
        t["usableDegree"] = si.UsableDegree; t["moving"] = si.IsMovingSkill;
        // UseClass: real class combat skills are >=2 (Fighter line 2-7, Cleric 8-13, Archer 14-19, Mage 20-25, Joker 27+…
        t["useClass"] = si.UseClass;
        // ASK `damage`, NOT `maxWc`
        t["maxWc"] = si.MaxWc; t["maxMa"] = si.MaxMa; t["damage"] = si.Damage;
        // stun = the skill applies a STUN abnormal-state (ActiveSkill StaName*
        t["stun"] = si.Stun;
        // heal = EffectType==5 (client "heal applied") → the DATA-DRIVEN heal categorization (no name-match)
        t["heal"] = si.Heal; t["healOverTime"] = si.HealOverTime;
        // abstates = the abnormal-state INDICES this skill applies (ActiveSkill StaNameA..D resolved through AbState → A…
        var abs = NewTable(); var ai = 1;
        foreach (var a in _mgr.ClientData?.SkillAbstates(id) ?? []) abs[ai++] = (double)a;
        t["abstates"] = DynValue.NewTable(abs);
        // landsOn = which SIDE the skill affects (ActiveSkill Last): 0=enemy, 1=self, 2=party, 3=ally.
        // castFrom (First) is where the cast starts. Ask this, not whether the state looks like a buff:
        // a stun is a DEBUFF that lands on an enemy, and its abstate will never show up in selfAbstates().
        t["landsOn"] = si.LandsOn; t["castFrom"] = si.CastFrom;
        t["selfTargeted"] = si.LandsOn == 1;
        return DynValue.NewTable(t);
    }

    /// <summary>Item fields from client ItemInfo for shop eval: {id, name, useClass, demandLv, grade, equipSlot, isScroll}</summary>
    public DynValue itemInfo(int id)
    {
        var it = _mgr.ClientData?.Item(id);
        if (it is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = id; t["name"] = it.Name; t["useClass"] = it.UseClass; t["demandLv"] = it.DemandLv;
        t["grade"] = it.Grade; t["equipSlot"] = it.EquipSlot; t["isScroll"] = it.IsScroll; t["type"] = it.Type;
        // itemClass = the ItemInfo `Class` sub-type WITHIN a Type
        t["itemClass"] = it.ItemClass; t["maxLot"] = it.MaxLot; t["sellPrice"] = it.SellPrice;
        t["buyPrice"] = it.BuyPrice;
        // 2 bow / 10 crossbow / 3 staff / 11 wand = RANGED auto-attack; the melee types are 1/4/5/13/17/18/19/21
        t["weaponType"] = it.WeaponType;
        // gradeType 0 = ordinary/replaceable gear (every plain smith-bought item — Leather/Chain Boots, Chain Helmet/Pan…
        t["gradeType"] = it.GradeType;
        // twoHand = a 2-handed weapon (occupies BOTH hand slots — Equip 10 left + 12 right)
        t["twoHand"] = it.TwoHand; t["shieldAc"] = it.ShieldAc;
        // For a skill book, the skill id it teaches (InxName join), else -1 — PLUS which table that id lives in
        var (scrollSid, scrollPassive) = it.IsScroll
            ? (_mgr.ClientData?.ScrollSkill(id) ?? (-1, false))
            : (-1, false);
        t["scrollSkillId"] = scrollSid;
        t["scrollSkillPassive"] = scrollPassive;
        // The PREREQUISITE skill id the taught skill needs first (DemandSk, same table as the skill), or -1 if none
        t["scrollSkillPrereq"] = scrollSid >= 0 ? (_mgr.ClientData?.SkillPrereqId(scrollSid, scrollPassive) ?? -1) : -1;
        return DynValue.NewTable(t);
    }

    /// <summary>The skill id a skill book teaches (ItemInfo↔ActiveSkill/PassiveSkill InxName join), or -1 if the item isn't a…</summary>
    public int scrollSkillId(int itemId) => _mgr.ClientData?.ScrollSkill(itemId).Id ?? -1;

    /// <summary>True if the skill taught by this book is a PASSIVE (mastery — in PassiveSkill ), false if an active ( ActiveSk…</summary>
    public bool scrollSkillPassive(int itemId) => _mgr.ClientData?.ScrollSkill(itemId).Passive ?? false;

    /// <summary>The PERSISTED learnt shop kind of an NPC on the current server+map ("weapon"|"skill"| "item"|"soulstone"|"nots…</summary>
    public string knownShopKind(int npcId)
    {
        var map = _handle.CurrentMap;
        if (string.IsNullOrEmpty(map)) return "";
        return _mgr.Knowledge.ShopKind(_handle.Options.Host, map!, npcId) ?? "";
    }

    /// <summary>High-resolution seconds for the profile shim. bot.now() is milliseconds and far too coarse to
    /// attribute a call that costs microseconds -- 1,370 of those per tick is what we are trying to see.</summary>
    public double nowPrecise() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;



    // ── RAW SHN ACCESS FOR LUA (operator 2026-08-18) ────────────────────────────────────────────────
    // The pattern this exists for: resolve the handles ONCE in on_start, then read by integer forever.
    //   local T   = bot.shnTable("ItemInfo")
    //   local C   = { id = bot.shnCol(T,"ID"), name = bot.shnCol(T,"InxName") }
    //   local row = bot.shnFind(T, C.id, itemId)
    //   local nm  = bot.shnGet(T, C.name, row)
    // Every call after the first is an array index -- no table name hashing, no column name hashing, and
    // no building of a 40-field Lua table just to read one field out of it.
    private readonly List<FiestaLibReloaded.Shn.ShnTable> _shnHandles = [];

    /// <summary>Handle for a client SHN table, or -1 if the table is not loadable. Resolve once, keep it.</summary>
    public int shnTable(string name)
    {
        if (_mgr.ClientData?.Table(name) is not { } t) return -1;
        var at = _shnHandles.IndexOf(t);
        if (at >= 0) return at;
        _shnHandles.Add(t);
        return _shnHandles.Count - 1;
    }

    private FiestaLibReloaded.Shn.ShnTable? Shn(int handle)
        => (uint)handle < (uint)_shnHandles.Count ? _shnHandles[handle] : null;

    /// <summary>Ordinal of a column, or -1 if absent. Case-insensitive.</summary>
    public int shnCol(int table, string column) => Shn(table)?.IndexOfColumn(column) ?? -1;

    /// <summary>Row count, or -1 for a bad handle -- so "no such table" cannot read as "empty table".</summary>
    public int shnRows(int table) => Shn(table)?.RowCount ?? -1;

    /// <summary>Row number whose  equals , or -1. Backed by a built-once index.</summary>
    public int shnFind(int table, string column, double value)
        => Shn(table)?.FindRow(column, (long)value) ?? -1;

    /// <summary>One cell by ordinals, as a Lua value (number, string or nil).</summary>
    public DynValue shnGet(int table, int column, int row)
    {
        if (Shn(table)?.Value(column, row) is not { } v) return DynValue.Nil;
        return v switch
        {
            string str => DynValue.NewString(str),
            float f => DynValue.NewNumber(f),
            double d => DynValue.NewNumber(d),
            _ => FiestaLibReloaded.Shn.ShnTable.TryToLong(v, out var l) ? DynValue.NewNumber(l) : DynValue.NewString(v.ToString() ?? ""),
        };
    }

    /// <summary>Every column name of a table, in on-disk order, so a script can build its own name->ordinal map.</summary>
    public DynValue shnColumns(int table)
    {
        var t = NewTable();
        if (Shn(table) is { } shn)
            for (var c = 0; c < shn.Columns.Count; c++) t[c + 1] = shn.Columns[c].Name;
        return DynValue.NewTable(t);
    }

    /// <summary>Every key of a column's index (e.g. every quest id QuestData defines), unordered.</summary>
    public DynValue shnKeys(int table, string column)
    {
        var t = NewTable();
        if (Shn(table) is { } shn)
        {
            var i = 1;
            foreach (var k in shn.RowIndex(column).Keys) t[i++] = k;
        }
        return DynValue.NewTable(t);
    }

    public void metric(string name, double value = 1) => _handle.Metrics.LogMetric(name, value);

    /// <summary>Declare a metric up front (direction + kind), so it appears on /metrics with the right percentile tail even be…</summary>
    public void initMetric(string name, string direction = "higher", string kind = "counter")
        => _handle.Metrics.InitMetric(name,
            direction.StartsWith("lower", StringComparison.OrdinalIgnoreCase)
                ? Metrics.MetricDirection.LowerIsBetter : Metrics.MetricDirection.HigherIsBetter,
            kind.StartsWith("gauge", StringComparison.OrdinalIgnoreCase)
                ? Metrics.MetricKind.Gauge : Metrics.MetricKind.Counter);

    /// <summary>Every KNOWN shop of a kind across ALL maps, as {map=..., npc=...} — PERSISTED, so it answers "where is there a storage keeper / smith?" even for a town the bot is not standing in</summary>
    public DynValue knownShopsOfKind(string kind)
    {
        var t = NewTable(); var i = 1;
        foreach (var (map, npc) in _mgr.Knowledge.ShopsOfKind(_handle.Options.Host, kind))
        {
            var e = NewTable(); e["map"] = map; e["npc"] = npc;
            t[i++] = DynValue.NewTable(e);
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Record + PERSIST what an NPC's shop turned out to be (current server+map)</summary>
    public void recordShop(int npcId, string kind)
    {
        var map = _handle.CurrentMap;
        if (!string.IsNullOrEmpty(map)) _mgr.Knowledge.RecordShop(_handle.Options.Host, map!, npcId, kind);
    }

    /// <summary>The character level at which quest was last deprioritized (a flee happened pursuing its objective mob), or -1…</summary>
    public int questDeprioritizedAtLevel(int questId) => _mgr.Knowledge.QuestDeprioritizedAtLevel(_handle.KnowledgeScope, questId);

    /// <summary>Record + PERSIST that a flee happened while pursuing this quest's objective mob, at the current character leve…</summary>
    public void recordQuestDeprioritized(int questId, int atLevel) => _mgr.Knowledge.RecordQuestDeprioritized(_handle.KnowledgeScope, questId, atLevel);

    /// <summary>Has the server already refused to STORE this item?</summary>
    public bool isUnstorable(int itemId) => _mgr.Knowledge.IsUnstorable(_handle.Options.Host, itemId);

    /// <summary>Record + PERSIST that the server refuses to store this item id</summary>
    public void noteUnstorable(int itemId) => _mgr.Knowledge.RecordUnstorable(_handle.Options.Host, itemId);

    /// <summary>The reason code from the last NC_ITEM_RELOC_ACK (0x300C), or -1 if none seen</summary>
    public int lastRelocAck() => View?.LastRelocAckCode ?? -1;

    /// <summary>Clear this quest's flee-deprioritization; true if a mark was removed</summary>
    public bool clearQuestDeprioritized(int questId) => _mgr.Knowledge.ClearQuestDeprioritized(_handle.KnowledgeScope, questId);

    public int questDeaths(int questId) => _mgr.Knowledge.QuestDeaths(_handle.KnowledgeScope, questId);

    /// <summary>Record + PERSIST a death while pursuing this quest</summary>
    public int recordQuestDeath(int questId, int level = -1) => _mgr.Knowledge.RecordQuestDeath(_handle.KnowledgeScope, questId, level);

    /// <summary>Contents of personal storage as of the last open: a table of {slot=..., id=...}</summary>
    public DynValue storageItems()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1;
        foreach (var (slot, id) in v.StorageItems)
        {
            var e = NewTable(); e["slot"] = (int)slot; e["id"] = (int)id;
            t[i++] = DynValue.NewTable(e);
        }
        return DynValue.NewTable(t);
    }

    /// <summary>True if a storage session is currently open (the last open SUCCEEDED)</summary>
    public bool storageOpen() => View?.StorageOpen ?? false;

    /// <summary>The inventory box id storage lives in, LEARNED from the wire, or -1 if not yet known (storage was empty the on…</summary>
    public int storageBox() => View?.StorageBox ?? -1;

    /// <summary>Money held in storage, and the current/max storage page from the last open</summary>
    public double storageCen() => View?.StorageCen ?? 0;
    public int storagePage() => View?.StoragePage ?? 0;
    public int storageMaxPage() => View?.StorageMaxPage ?? 0;

    /// <summary>The item ids the last-opened merchant sells (from SHOPOPEN/SHOPOPENTABLE)</summary>
    public DynValue shopItems()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var id in v.ShopItems) t[i++] = (int)id;
        return DynValue.NewTable(t);
    }

    /// <summary>True if the bot has already learned this skill id (from the login skill/passive lists + anything learned this…</summary>
    public bool hasSkill(int id, bool passive = false) => View?.HasSkill((ushort)id, passive) ?? false;

    /// <summary>How many times IN A ROW the server has refused to USE this item id (non-0x700 NC_ITEM_USE_ACK); 0 if never ref…</summary>
    public int itemUseFails(int itemId) => View?.ItemUseFailCount(itemId) ?? 0;

    /// <summary>If this id is a CRAFTING RECIPE, a table {masteryType, neededPoints, masteryName} — the job it requires and th…</summary>
    public DynValue recipeRequirement(int id)
    {
        var cd = _mgr.ClientData;
        if (cd?.RecipeRequirement(id) is not { } req) return DynValue.Nil;
        var t = NewTable();
        t["masteryType"] = req.MasteryType;
        t["neededPoints"] = req.NeededPoints;
        t["masteryName"] = cd.MasteryTypeName(req.MasteryType) ?? $"mastery{req.MasteryType}";
        return DynValue.NewTable(t);
    }

    /// <summary>The PASSIVE skill ids the character has learned (login 0x103E list)</summary>
    public DynValue learnedPassives()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var id in v.LearnedPassives) t[i++] = (int)id;
        return DynValue.NewTable(t);
    }

    /// <summary>The inventory bag slot currently holding</summary>
    public int invenSlotOf(int itemId)
    {
        var v = View; if (v is null) return -1;
        foreach (var kv in v.Inventory) if (kv.Value == (ushort)itemId) return kv.Key;
        return -1;
    }

    /// <summary>The stack count in main-bag (from the wire lot field), 0 if empty</summary>
    public int invenCount(int slot) => View?.ItemCount((byte)slot) ?? 0;

    /// <summary>Total quantity of an item id carried across ALL bag slots (sums stacks)</summary>
    public int invenCountOf(int itemId)
    {
        var v = View; if (v is null) return 0;
        int total = 0;
        foreach (var (slot, id) in v.Inventory)
            if (id == itemId) total += v.ItemCount(slot);
        return total;
    }

    /// <summary>True when the bag is FULL (a pickup failed with the inventory-full ack 0x346)</summary>
    public bool bagFull() => View?.BagFull ?? false;

    /// <summary>Empty bag slots, computed from the (now-complete, item-0-inclusive) inventory model: default 48-slot bag (oper…</summary>
    public int bagFreeSlots() => View?.BagFreeSlots ?? 48;   // delegates: ZoneView owns the capacity rule

    /// <summary>Current money ("cen"), or -1 if no money packet seen yet</summary>
    public double money() => View?.Money ?? -1;
    /// <summary>Current total experience (seeded at zone-enter, updated by per-kill EXPGAIN), or -1 if not yet seeded</summary>
    public double exp() => View?.Exp ?? -1;

    /// <summary>Learned average exp-per-kill for a mob id (from EXPGAIN packets), or 0 if we've never killed one</summary>
    public long mobExp(int mobId) => View?.MobExpAvg(mobId) ?? 0;

    /// <summary>The current character class id</summary>
    public int charClass() => _handle.Class;
    /// <summary>The class id from the most recent PROMOTE_ACK this session, or -1 if no promotion yet</summary>
    public int promotedClass() => View?.PromotedClass ?? -1;
    /// <summary>The scenario/instance trigger-area last entered+armed</summary>
    public string scenarioArea() => View?.LastScenarioArea ?? "";

    /// <summary>The scenario areas we've ARRIVED-IN and ACKED this instance run — the authoritative "area done" set (a landed…</summary>
    public DynValue scenarioAckedAreas()
    {
        var t = NewTable();
        if (View is { } v) foreach (var a in v.ScenarioAckedAreas) t[a] = true;
        return DynValue.NewTable(t);
    }

    /// <summary>True while a movement-blocking abnormal state (stun/root/entangle</summary>
    public bool rooted() => View?.Rooted ?? false;

    /// <summary>The abnormal-state indices currently active on US (unexpired), as a Lua array</summary>
    public DynValue selfAbstates()
    {
        var t = NewTable();
        var i = 1;
        foreach (var a in View?.SelfAbstateSnapshot() ?? []) t[i++] = (double)a;
        return DynValue.NewTable(t);
    }

    /// <summary>The current map's instance DOORS (room connectors) from its .sbi , each { name, x, y } with a WORLD-coord cent…</summary>
    public DynValue instanceDoors()
    {
        var t = NewTable();
        var doors = _handle.CurrentMap is { } map ? _mgr.DoorProvider?.Invoke(map) : null;
        if (doors is not null)
        {
            int i = 1;
            foreach (var d in doors) { var r = NewTable(); r["name"] = d.Name; r["x"] = d.WorldX; r["y"] = d.WorldY; t[i++] = DynValue.NewTable(r); }
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Live scenario corridor DOOR states from NC_SCENARIO_DOORSTATE_CMD (0x6C09), each { handle, state (raw byte), x…</summary>
    public DynValue doorStates()
    {
        var t = NewTable();
        int i = 1;
        if (View is { } v)
            foreach (var d in v.DoorStates)
            {
                var r = NewTable();
                r["handle"] = (double)d.Handle; r["state"] = (double)d.State;
                if (d.X is { } x) r["x"] = (double)x;
                if (d.Y is { } y) r["y"] = (double)y;
                t[i++] = DynValue.NewTable(r);
            }
        return DynValue.NewTable(t);
    }

    /// <summary>The current map's scenario trigger AREAS from its .aid , each { name, x, y (centre), rx, ry (half-extents) } i…</summary>
    public DynValue scenarioAreas()
    {
        var t = NewTable();
        var areas = _handle.CurrentMap is { } map ? _mgr.AreaProvider?.Invoke(map) : null;
        if (areas is not null)
        {
            int i = 1;
            foreach (var a in areas)
            {
                var r = NewTable();
                r["name"] = a.Name; r["x"] = (double)a.CenterX; r["y"] = (double)a.CenterY;
                r["rx"] = (double)a.HalfX; r["ry"] = (double)a.HalfY;
                t[i++] = DynValue.NewTable(r);
            }
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Centre { x, y, rx, ry, name } of the CURRENTLY ARMED scenario area (its name = the server's LastScenarioArea ,…</summary>
    public DynValue scenarioAreaCenter()
    {
        var name = View?.LastScenarioArea;
        if (string.IsNullOrEmpty(name)) return DynValue.Nil;
        var areas = _handle.CurrentMap is { } map ? _mgr.AreaProvider?.Invoke(map) : null;
        if (areas is null) return DynValue.Nil;
        foreach (var a in areas)
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                var r = NewTable();
                r["x"] = (double)a.CenterX; r["y"] = (double)a.CenterY;
                r["rx"] = (double)a.HalfX; r["ry"] = (double)a.HalfY; r["name"] = a.Name;
                return DynValue.NewTable(r);
            }
        return DynValue.Nil;
    }

    public DynValue kiteLoop(double corridorMinWidth)
    {
        var grid = _handle.CurrentMap is { } map ? _mgr.GridProvider?.Invoke(map) : null;
        var t = NewTable();
        if (grid is null || _handle.Position is not { } p) return DynValue.NewTable(t);
        var key = $"{_handle.CurrentMap}|{(int)(p.X / 500)}|{(int)(p.Y / 500)}|{(int)corridorMinWidth}";
        if (!_kiteLoops.TryGetValue(key, out var ring))
        {
            ring = Navigation.KiteLoop.Fit(grid, p.X, p.Y,
                       corridorMinWidth <= 0 ? 260 : corridorMinWidth);
            _kiteLoops[key] = ring;
        }
        var i = 1;
        foreach (var (x, y) in ring)
        {
            var r = NewTable(); r["x"] = x; r["y"] = y; t[i++] = DynValue.NewTable(r);
        }
        return DynValue.NewTable(t);
    }
    private readonly Dictionary<string, IReadOnlyList<(uint X, uint Y)>> _kiteLoops = new();

    public DynValue kiteCircle(double maxDiameter, double enemyRange)
    {
        var grid = _handle.CurrentMap is { } map ? _mgr.GridProvider?.Invoke(map) : null;
        if (grid is null || _handle.Position is not { } p) return DynValue.Nil;
        var key = $"{_handle.CurrentMap}|{(int)(p.X / 500)}|{(int)(p.Y / 500)}|{(int)maxDiameter}|{(int)enemyRange}";
        if (!_kiteCircles.TryGetValue(key, out var fit))
        {
            fit = Navigation.KiteCircle.Fit(grid, p.X, p.Y,
                                            maxDiameter <= 0 ? 5000 : maxDiameter,
                                            enemyRange <= 0 ? 400 : enemyRange);
            _kiteCircles[key] = fit;
        }
        if (fit is not { } c) return DynValue.Nil;
        var t = NewTable(); t["cx"] = c.Cx; t["cy"] = c.Cy; t["r"] = c.R;
        return DynValue.NewTable(t);
    }
    private readonly Dictionary<string, (double Cx, double Cy, double R)?> _kiteCircles = new();

    /// <summary>The next kite step: a point to walk to that flees the enemy AND blends smoothly onto the kite circle</summary>
    public DynValue kiteStepPoint(double ex, double ey, double cx, double cy, double r, double step, int dir)
    {
        if (_handle.Position is not { } p || r <= 1) return DynValue.Nil;
        double px = p.X, py = p.Y;
        // Straight away from the enemy — what a naive kite does, and correct while we are well inside
        double ax = px - ex, ay = py - ey;
        var alen = Math.Max(1e-6, Math.Sqrt(ax * ax + ay * ay));
        ax /= alen; ay /= alen;
        // Tangent to the circle at our bearing, taken in the requested orbit direction
        double rx = px - cx, ry = py - cy;
        var rlen = Math.Max(1e-6, Math.Sqrt(rx * rx + ry * ry));
        double ux = rx / rlen, uy = ry / rlen;
        double tx = -uy * Math.Sign(dir == 0 ? 1 : dir), ty = ux * Math.Sign(dir == 0 ? 1 : dir);
        // Blend weight: 0 at the centre (pure flee) -> 1 at the rim (pure tangent)
        var w = Math.Clamp(rlen / r, 0, 1);
        // Past the rim, steer back toward it instead of running off into unfitted ground
        double ix = 0, iy = 0;
        if (rlen > r) { ix = -ux; iy = -uy; }
        var hx = ax * (1 - w) + tx * w + ix * Math.Min(1, (rlen - r) / Math.Max(1, r * 0.15));
        var hy = ay * (1 - w) + ty * w + iy * Math.Min(1, (rlen - r) / Math.Max(1, r * 0.15));
        var hlen = Math.Max(1e-6, Math.Sqrt(hx * hx + hy * hy));
        var t2 = NewTable();
        t2["x"] = Math.Max(0, px + hx / hlen * step);
        t2["y"] = Math.Max(0, py + hy / hlen * step);
        t2["mode"] = rlen > r ? "ride" : (w < 0.35 ? "flee" : (w < 0.9 ? "blend" : "ride"));
        t2["w"] = w;
        return DynValue.NewTable(t2);
    }

    public DynValue coveragePath(double stepWorld)
    {
        var t = NewTable();
        var grid = _handle.CurrentMap is { } map ? _mgr.GridProvider?.Invoke(map) : null;
        if (grid is not null && stepWorld > 0)
        {
            int i = 1;
            foreach (var (x, y) in Navigation.CoveragePath.Compute(grid, stepWorld))
            {
                var r = NewTable(); r["x"] = x; r["y"] = y; t[i++] = DynValue.NewTable(r);
            }
        }
        return DynValue.NewTable(t);
    }

    /// <summary>The raw code from the last SELL_ACK (0x3005): 0x0381 = success, else rejected; -1 if no sell acked yet this se…</summary>
    public int lastSellAck() => View?.LastSellAck ?? -1;

    /// <summary>The raw code from the last BUY_ACK (0x3004): 0x0201 = success (item added), else rejected</summary>
    public int lastBuyAck() => View?.LastBuyAck ?? -1;
    /// <summary>True if the last buy succeeded (0x0201)</summary>
    public bool lastBuyOk() => (View?.LastBuyAck ?? -1) == 0x0201;
    /// <summary>Monotonic count of BUY_ACKs seen this session</summary>
    public int buyAckCount() => View?.BuyAckCount ?? 0;

    /// <summary>True if a shop is genuinely open right now (item or soul-stone) — a SELL/BUY will be accepted</summary>
    public bool shopOpen() => View?.ShopOpen ?? false;

    /// <summary>The KIND of the last-opened shop: "skill" | "weapon" | "item" | "soulstone" | "unknown"</summary>
    public string shopKind() => (View?.LastShopKind ?? Session.ShopKind.Unknown) switch
    {
        Session.ShopKind.Skill => "skill",
        Session.ShopKind.Weapon => "weapon",
        Session.ShopKind.Item => "item",
        Session.ShopKind.SoulStone => "soulstone",
        Session.ShopKind.Storage => "storage",   // personal warehouse (0x3C08) — found by role, not by id
        _ => "unknown",
    };

    /// <summary>The learned skill id of the highest rank whose name starts with</summary>
    public int highestSkill(string prefix)
    {
        var v = View; var cd = _mgr.ClientData;
        if (v is null || cd is null) return 0;
        int best = 0, bestRank = int.MinValue;
        foreach (var s in v.LearnedSkills)
        {
            var nm = cd.SkillName(s);
            if (string.IsNullOrEmpty(nm) || !nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rank = ParseRank(nm);
            if (rank > bestRank) { bestRank = rank; best = s; }
        }
        return best;
    }

    /// <summary>All learned skill ids (from the zone-login skill list + any learned this session)</summary>
    public DynValue learnedSkills()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var s in v.LearnedSkills) t[i++] = (int)s;
        return DynValue.NewTable(t);
    }

    private static int ParseRank(string name)
    {
        var i = name.IndexOf('[');
        var j = i >= 0 ? name.IndexOf(']', i) : -1;
        return i >= 0 && j > i && int.TryParse(name.AsSpan(i + 1, j - i - 1).Trim(), out var r) ? r : 0;
    }

    private string Id => _handle.Id;
    private Session.ZoneView? View => _handle.ZoneView;
    private static bool Ok(BotManager.ActionResult r) => r == BotManager.ActionResult.Sent;
    private static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();

    // ── actions (C→S) ─────────────────────────────────────────────────────────
    public bool say(string text) => Ok(Wait(_mgr.SayAsync(Id, text)));
    public bool whisper(string to, string text) => Ok(Wait(_mgr.WhisperAsync(Id, to, text)));
    public bool cast(int skill, int target) => Ok(Wait(_mgr.CastAsync(Id, (ushort)skill, (ushort)target)));
    public bool castGround(int skill, double x, double y) => Ok(Wait(_mgr.CastGroundAsync(Id, (ushort)skill, (uint)x, (uint)y)));
    public bool attack(int skill, int target = 0) => Ok(Wait(_mgr.AttackAsync(Id, (ushort)skill, (ushort)target)));
    public bool autoAttack(int target = 0) => Ok(Wait(_mgr.AutoAttackAsync(Id, (ushort)target)));
    public bool stopAttack() => Ok(Wait(_mgr.StopAttackAsync(Id)));
    /// <summary>Clean logout + re-login in place, re-applying this same script once back in zone (recovery for a stuck bot, e.…</summary>
    public bool relog() => _mgr.Relog(Id);
    public bool heal(int skill) => Ok(Wait(_mgr.HealSelfAsync(Id, (ushort)skill)));
    public bool useItem(int slot, int invenType = 9) => Ok(Wait(_mgr.UseItemAsync(Id, (byte)slot, (byte)invenType)));

    /// <summary>Unspent stat points the server told us about (NC_CHAR_STAT_REMAINPOINT_CMD)</summary>
    public int freeStatPoints() => View?.FreeStatPoints ?? -1;

    /// <summary>Spend ONE stat point on (0=STR,1=END,2=DEX,3=INT,4=MP)</summary>
    public bool incStat(int stat) => Ok(Wait(_mgr.IncStatAsync(Id, (byte)stat)));
    public bool equip(int slot) => Ok(Wait(_mgr.EquipAsync(Id, (byte)slot)));
    public bool pickup(int itemHandle) => Ok(Wait(_mgr.PickupAsync(Id, (ushort)itemHandle)));
    public bool loot(int itemHandle = 0) => Ok(Wait(_mgr.LootAsync(Id, (ushort)itemHandle)));
    /// <summary>Fire the client's inventory auto-sort (compact + STACK the bag</summary>
    public bool sortInventory() => Ok(Wait(_mgr.SortInventoryAsync(Id)));
    /// <summary>Pick-pacing poll gate (operator 2026-07-02): the server processes ONE item-cell pick at a time — the driver po…</summary>
    public bool canPick() => View?.CanPick ?? false;
    public bool clickNpc(int handle) => Ok(Wait(_mgr.ClickNpcAsync(Id, (ushort)handle)));
    public bool answerQuest(int result = 1) => Ok(Wait(_mgr.ProceedQuestAsync(Id, (uint)result)));
    /// <summary>Drive a whole quest dialogue with one NPC (accept or turn-in): click it and ACK every server-pushed script pag…</summary>
    public bool doQuest(int npcHandle, int result = 1, int rewardIndex = -1, int questId = -1)
        => Ok(Wait(_mgr.DriveQuestDialogueAsync(Id,
            npcHandle < 0 ? null : (ushort)npcHandle, (uint)result, rewardIndex,
            questId: questId < 0 ? null : (ushort)questId)));
    /// <summary>True if the LAST doQuest drive CONCLUDED (reached the Qsc 0x06/0x0A terminal for our quest)</summary>
    public bool dialogConcluded() => _handle.LastDialogConcluded;
    /// <summary>Reset a quest's credited-kill progress to 0 (the 0x440D counter only counts up + only reset on GIVE_UP)</summary>
    public void resetQuestProgress(int id) => View?.ResetQuestProgress(id);
    public bool selectReward(int questId, int index) => Ok(Wait(_mgr.SelectQuestRewardAsync(Id, (ushort)questId, (uint)index)));

    /// <summary>The character's ClassName ClassID (1=Fighter, 6=Cleric, …)</summary>
    public int classId() => _handle.Class;

    /// <summary>The choice-reward index (0-based among a quest's method-2 "choose one" rewards) to pick for THIS character's c…</summary>
    public int bestRewardIndex(int questId)
    {
        var q = _mgr.ClientData?.Quest(questId); var cd = _mgr.ClientData;
        if (q is null || cd is null) return -1;
        var choices = q.Rewards.Where(r => r.Method == 2 && r.Type == 2).ToList();
        if (choices.Count == 0) return -1;
        // Return the RAW reward-block slot (RawIndex) — that's what the server's reward-select packet expects, not the c…
        int placeholder = -1;
        for (int i = 0; i < choices.Count; i++)
        {
            var uc = cd.ItemUseClass(choices[i].ItemId);
            // The client's own UseClass matrix (was: `uc == 1 || approximate-ladder.Contains(uc)`; the hand-rolled `uc == 1`…
            if (cd.UseClassAllows(_handle.Class, uc)) return choices[i].RawIndex;
            if (choices[i].ItemId == 0 && placeholder < 0) placeholder = choices[i].RawIndex; // our-class placeholder
        }
        return placeholder >= 0 ? placeholder : choices[0].RawIndex;
    }
    public DynValue pendingQuest()
    {
        var q = View?.PendingQuest;
        if (q is null) return DynValue.Nil;
        var t = NewTable(); t["questId"] = q.QuestId; t["qsc"] = q.Qsc; t["dialogId"] = q.DialogId;
        return DynValue.NewTable(t);
    }

    /// <summary>Quest definition from QuestData.shn (nil if unknown): startNpc, turnInNpc, minLevel/maxLevel, class, linkedQue…</summary>
    public DynValue quest(int id)
    {
        var q = _mgr.ClientData?.Quest(id);
        if (q is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = q.Id; t["name"] = _mgr.ClientData?.QuestDialog(q.Title) ?? ""; t["startNpc"] = q.StartNpc; t["turnInNpc"] = q.TurnInNpc;
        t["minLevel"] = q.MinLevel; t["maxLevel"] = q.MaxLevel; t["isNeedLevel"] = q.IsNeedLevel;
        // EndCondition "reach Level N to COMPLETE" gate (distinct from the accept-level window above)
        t["endNeedsLevel"] = q.EndNeedsLevel; t["endLevel"] = q.EndLevel;
        t["class"] = q.Class; t["linkedQuest"] = q.LinkedQuest;
        t["needsNpc"] = q.NeedsNpc; t["needsItem"] = q.NeedsItem; t["needsItemId"] = q.NeedsItemId;
        t["needsClass"] = q.NeedsClass; t["isWaitListView"] = q.IsWaitListView;
        t["remoteAcceptable"] = q.RemoteAcceptable; t["questListVisible"] = q.IsWaitListView; t["remoteHandIn"] = q.RemoteHandIn;
        t["region"] = q.Region; t["questType"] = q.QuestType; t["repeatable"] = q.Repeatable;
        t["exp"] = q.ExpReward;                // turn-in EXP reward (Type-0 reward) — drives exp prioritisation
        t["objectiveMob"] = q.ObjectiveMob;   // mobId to grind for this quest (-1 = meeting quest)
        t["startScript"] = q.StartScript; t["actionScript"] = q.ActionScript; t["finishScript"] = q.FinishScript;
        var npcs = NewTable(); int ni = 1;
        foreach (var n in q.Npcs) { npcs[ni++] = n.Id; }
        t["npcs"] = DynValue.NewTable(npcs);
        // Each objective is an INDIVIDUAL GOAL carrying its OWN progress (operator 2026-08-11: "expose to lua as individ…
        var objs = NewTable(); int oi = 1;
        foreach (var o in q.Objectives)
        {
            var e = NewTable(); e["type"] = o.Type; e["mob"] = o.Mob; e["count"] = o.Count; e["item"] = o.Item;
            var idx = oi - 1;                       // wire objIdx is 0-based; oi is Lua's 1-based cursor
            var prog = View?.QuestObjProgress(id, idx) ?? 0;
            e["idx"] = idx; e["prog"] = prog; e["done"] = o.Count > 0 && prog >= o.Count;
            objs[oi++] = DynValue.NewTable(e);
        }
        t["objectives"] = DynValue.NewTable(objs);
        var rewards = NewTable(); int ri = 1;
        foreach (var r in q.Rewards) { var e = NewTable(); e["method"] = r.Method; e["type"] = r.Type; e["itemId"] = r.ItemId; e["itemCount"] = r.ItemCount; e["amount"] = r.Amount; rewards[ri++] = DynValue.NewTable(e); }
        t["rewards"] = DynValue.NewTable(rewards);
        return DynValue.NewTable(t);
    }

    /// <summary>Resolve a quest dialog/title id to its text (QuestDialog.shn)</summary>
    public string questDialog(int id) => _mgr.ClientData?.QuestDialog(id) ?? "";

    /// <summary>The human NAME of a quest for LOGGING (QuestData title id → QuestDialog.shn text), formatted "Name(q{id})" so…</summary>
    public string questName(int id) => _mgr.ClientData?.QuestName(id) ?? $"q{id}";

    /// <summary>True if the character has completed this quest (from the login QUEST_DONE state)</summary>
    public bool questDone(int id) => View?.IsQuestDone(id) ?? false;
    /// <summary>True if the quest is currently in progress (accepted, not yet turned in)</summary>
    public bool questActive(int id) => View?.IsQuestActive(id) ?? false;

    /// <summary>Ids of quests currently in progress (accepted, awaiting objective/turn-in) — the driver resumes these before a…</summary>
    public DynValue activeQuests()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var id in v.ActiveQuests.Keys) t[i++] = id;
        return DynValue.NewTable(t);
    }

    /// <summary>The PLAYER_QUEST_INFO status byte of an active quest (0 if not active)</summary>
    public int questStatus(int id) => View is { } v && v.ActiveQuests.TryGetValue(id, out var s) ? s : 0;

    /// <summary>Kills the SERVER has credited to this quest this session (from 0x440D)</summary>
    public int questProgress(int id) => View?.QuestProgress(id) ?? 0;

    /// <summary>Kills credited to ONE objective of a quest — is the 0-based position in the quest's objective list, exactly as…</summary>
    public int questObjProgress(int id, int objIdx) => View?.QuestObjProgress(id, objIdx) ?? 0;

    /// <summary>Report the driver's current phase so the host can account time to it</summary>
    public void notePhase(string phase) { if (_mgr.Get(Id) is { } h) h.NotePhase(phase); }

    /// <summary>Abandon a quest (NC_QUEST_GIVE_UP_REQ)</summary>
    public bool giveUpQuest(int id) => Ok(Wait(_mgr.GiveUpQuestAsync(Id, (ushort)id)));

    /// <summary>Start a quest by id (NC_QUEST_START_REQ) — the accept for menu/remote quests where clicking the NPC opens a se…</summary>
    public bool startQuest(int id) => Ok(Wait(_mgr.StartQuestAsync(Id, (ushort)id)));

    /// <summary>ACCEPT A QUEST REMOTELY from the quest log — no travel to the giver, no NPC click</summary>
    public bool remoteAcceptQuest(int id) => Ok(Wait(_mgr.RemoteAcceptQuestAsync(Id, (ushort)id)));

    /// <summary>The server's last accept/start result for a quest: 0 = accepted, &amp;gt;0 = a refusal reason code (from NC_QUEST_…</summary>
    public int questAcceptErr(int id) => View?.QuestAcceptErr(id) ?? -1;

    /// <summary>Quests the character can accept right now — the server's authoritative available list from the login QUEST_REA…</summary>
    public DynValue availableQuests()
    {
        var t = NewTable();
        var v = View; var cd = _mgr.ClientData;
        if (v is null) return DynValue.NewTable(t);
        var npcsInView = new HashSet<int>();
        // Skip scenario clones: they have no mob id, so counting their placeholder would put NPC id 0 "in view" and make…
        foreach (var n in v.NearbyNpcs) if (!n.IsScenarioClone) npcsInView.Add(n.MobId);
        int i = 1;
        foreach (var id in v.AvailableQuests)
        {
            var q = cd?.Quest(id);
            var e = NewTable();
            e["id"] = id;
            e["startNpc"] = q?.StartNpc ?? 0;
            e["turnInNpc"] = q?.TurnInNpc ?? 0;
            e["title"] = q is not null ? cd!.QuestDialog(q.Title) : "";
            e["inView"] = q is not null && npcsInView.Contains(q.StartNpc);
            t[i++] = DynValue.NewTable(e);
        }
        return DynValue.NewTable(t);
    }
    private static int ClassLine(int c) => c > 0 ? ((c - 1) / 5) * 5 + 1 : 0;

    public DynValue eligibleQuests()
    {
        var t = NewTable();
        var v = View; var cd = _mgr.ClientData;
        if (v is null || cd is null) return DynValue.NewTable(t);
        int i = 1;
        foreach (var q in cd.Quests.Values)
        {
            // ACCEPT GATE = the StartCondition Needs* flags, NOT a bare StartNpc
            if (!q.NeedsNpc || q.StartNpc == 0 || q.NeedsItem) continue;
            // CLASS GATE (@62 NeedsClass / @63 Class): the starter quests come in one copy PER CLASS (q1 "Baby Steps" Fighte…
            if (q.NeedsClass && q.Class != 0 && _handle.Class != 0 && ClassLine(q.Class) != ClassLine(_handle.Class)) continue;
            if (v.IsQuestDone(q.Id) || v.IsQuestActive(q.Id)) continue;
            // Accept ALL NPC-startable, level-appropriate quests: kill (Type 1), item-collect (Type 2), find/visit (Type 3)…
            if (!q.IsNeedLevel) continue;
            if (q.MinLevel > _handle.Level || _handle.Level > q.MaxLevel) continue;
            if (q.PrereqQuest != 0 && !v.IsQuestDone(q.PrereqQuest)) continue; // prerequisite quest not done (@58)
            var e = NewTable();
            e["id"] = q.Id; e["startNpc"] = q.StartNpc; e["turnInNpc"] = q.TurnInNpc;
            e["minLevel"] = q.MinLevel; e["maxLevel"] = q.MaxLevel; e["prereq"] = q.PrereqQuest;
            e["repeatable"] = q.Repeatable; e["title"] = cd.QuestDialog(q.Title);
            int kills = q.Objectives.Count(o => o.Type == 1);
            e["hasKill"] = kills > 0; e["hasItem"] = q.Objectives.Any(o => o.Type == 2);
            e["noObjective"] = q.Objectives.Count == 0;  // 0-objective: accept + instant turn-in
            // remoteAcceptable = can be accepted from the quest log without walking (0x4414 START_REQ)
            e["remoteAcceptable"] = q.RemoteAcceptable; e["questListVisible"] = q.IsWaitListView; e["remoteHandIn"] = q.RemoteHandIn;
            var objs = NewTable(); int oi = 1;
            foreach (var o in q.Objectives)
            { var oe = NewTable(); oe["type"] = o.Type; oe["mob"] = o.Mob; oe["count"] = o.Count; oe["item"] = o.Item; objs[oi++] = DynValue.NewTable(oe); }
            e["objectives"] = DynValue.NewTable(objs);
            t[i++] = DynValue.NewTable(e);
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Where a mob type spawns, from client MobCoordinate.shn (the table the real client uses for the quest-log marke…</summary>
    public int mobLevel(int mobId) => _mgr.ClientData?.Mob(mobId)?.Level ?? -1;

    /// <summary>The mob's max HP from client MobInfo.shn, or -1 if unknown</summary>
    public int mobMaxHp(int mobId) => _mgr.ClientData?.Mob(mobId)?.MaxHp ?? -1;

    /// <summary>The mob's MobInfo.shn GradeType: 0 = normal grindable mob, &gt;=1 = a NAMED BOSS/ELITE</summary>
    public int mobGrade(int mobId) => _mgr.ClientData?.Mob(mobId)?.GradeType ?? -1;

    public DynValue mobLocation(int mobId)
    {
        var cd = _mgr.ClientData;
        if (cd is null) return DynValue.Nil;
        var all = cd.MobCoordinatesAll(mobId);
        // Drop zero-area rows: those are quest-log MARKERS
        var nonZero = all.Where(l => (long)l.Width * l.Height > 0).ToList();
        if (nonZero.Count > 0) all = nonZero;
        if (all.Count == 0) return DynValue.Nil;
        // Prefer (1) the current map (hunt here if the mob spawns here), else (2) the LARGEST patch overall — determinis…
        var cur = _handle.CurrentMap;
        GameData.MobLocation? pick = cur is null ? null : all.FirstOrDefault(l => string.Equals(l.Map, cur, StringComparison.OrdinalIgnoreCase));
        pick ??= all.OrderByDescending(l => (long)l.Width * l.Height)
                    .ThenBy(l => l.Map, StringComparer.OrdinalIgnoreCase).First();
        var t = NewTable();
        t["map"] = pick.Map; t["x"] = pick.CenterX; t["y"] = pick.CenterY;
        t["width"] = pick.Width; t["height"] = pick.Height;
        return DynValue.NewTable(t);
    }

    public bool soulstoneHp() => Ok(Wait(_mgr.UseSoulStoneHpAsync(Id)));
    public bool soulstoneSp() => Ok(Wait(_mgr.UseSoulStoneSpAsync(Id)));
    /// <summary>True once an HP soul-stone USE failed (reserve empty / on cooldown) — gate on not bot.hpStoneDepleted() so the…</summary>
    public bool hpStoneDepleted() => View?.HpStoneDepleted ?? false;

    /// <summary>Hardest hit ever observed from this mob id, or -1 if we have NEVER been hit by it</summary>
    public int mobHitMax(int mobId) => View?.MobHitMax(mobId) ?? -1;

    /// <summary>Observed attack RANGE of a mob (world units), or -1 if it has never hit us</summary>
    public double mobAttackRange(int mobId) => View?.MobAttackRange(mobId) ?? -1;

    /// <summary>Observed attack range of a specific ENTITY by handle, or -1</summary>
    public double handleAttackRange(int handle) => View?.HandleAttackRange((ushort)handle) ?? -1;

    /// <summary>Hardest hit taken from a specific ENTITY by handle, or -1 if it has never hit us</summary>
    public int handleHitMax(int handle) => View?.HandleHitMax((ushort)handle) ?? -1;

    /// <summary>Mean damage per connecting hit from this ENTITY, or -1 if never observed</summary>
    public double handleHitAvg(int handle) => View?.HandleHitAvg((ushort)handle) ?? -1;

    /// <summary>How many hits from this ENTITY have been sampled</summary>
    public int handleHitSamples(int handle) => View?.HandleHitSamples((ushort)handle) ?? 0;

    /// <summary>Mean damage per connecting hit from this mob id, -1 if never observed</summary>
    public double mobHitAvg(int mobId) => View?.MobHitAvg(mobId) ?? -1;

    /// <summary>How many hits from this mob we have sampled</summary>
    public int mobHitSamples(int mobId) => View?.MobHitSamples(mobId) ?? 0;

    public int hpStoneRestore() => (int)(View?.HpStoneRestore ?? 0);
    /// <summary>SP restored by one soul-stone charge (same packet); 0 until known</summary>
    public int spStoneRestore() => (int)(View?.SpStoneRestore ?? 0);

    public double hpStoneHealAvg() => View?.HpStoneHealAvg ?? -1;

    public double sustainableHealDps() => View?.SustainableHealDps ?? -1;

    /// <summary>Observed incoming damage per second over the last , using the real hits we took (not an estimate)</summary>
    public double incomingDps(double windowMs = 5000)
    {
        var v = View;
        if (v is null) return 0;
        return v.IncomingDamageSince(TimeSpan.FromMilliseconds(windowMs));
    }

    public int mobHitsToKillUs(int mobId)
    {
        var max = View?.MobHitMax(mobId) ?? -1;
        var maxHp = View?.MaxHp ?? 0;
        if (max <= 0 || maxHp <= 0) return -1;
        return (int)Math.Ceiling((double)maxHp / max);
    }

    /// <summary>Milliseconds until the HP soul stone can heal again — 0 = ready now , -1 = the cooldown has not been learned y…</summary>
    public double hpStoneReadyIn() => View?.HpStoneReadyInMs ?? -1;

    public double hpStoneCooldownMs() => View?.HpStoneCooldownMs ?? -1;

    /// <summary>Milliseconds until the SP soul stone can fire again (0 = ready, -1 = never used yet)</summary>
    public double spStoneReadyIn() => View?.SpStoneReadyInMs ?? -1;

    /// <summary>The SP soul-stone cooldown in ms</summary>
    public double spStoneCooldownMs() => View?.SpStoneCooldownMs ?? -1;

    /// <summary>Consecutive HP-stone USEFAILs since the last real heal</summary>
    public int hpStoneFailsInARow() => View?.HpStoneFailsSinceSuccess ?? 0;

    /// <summary>True while a skill cast is animating — the character is LOCKED, so starting another cast now is wasted</summary>
    public bool casting() => View?.IsCasting ?? false;

    /// <summary>Whether the server has CONFIRMED the in-flight cast (CAST_SUC_ACK seen), as opposed to it still being our spec…</summary>
    public bool castConfirmed() => View?.CastServerConfirmed ?? false;
    /// <summary>Current HP soul-stone reserve count, or -1 if unknown (no buy/use seen yet)</summary>
    public int hpStones() => View?.HpStones ?? -1;
    public int maxHpStones() => (int)(View?.MaxHpStones ?? 0);
    /// <summary>Unit price (cen) of one HP soul-stone charge, from the healer's soul-stone shop-open (0x3C05)</summary>
    public int hpStonePrice() => (int)(View?.HpStonePrice ?? 0);
    /// <summary>Unit price (cen) of one SP soul-stone charge (0x3C05 shop-open)</summary>
    public int spStonePrice() => (int)(View?.SpStonePrice ?? 0);
    /// <summary>SP analogue of — gate on not bot.spStoneDepleted()</summary>
    public bool spStoneDepleted() => View?.SpStoneDepleted ?? false;
    /// <summary>Current SP soul-stone reserve count, or -1 if unknown</summary>
    public int spStones() => View?.SpStones ?? -1;
    /// <summary>Max SP soul-stone reserve capacity, or 0 if not seeded</summary>
    public int maxSpStones() => (int)(View?.MaxSpStones ?? 0);
    /// <summary>Monotonic count of soul-stone BUY failures (0x5005 NC_SOULSTONE_BUYFAIL_ACK)</summary>
    public int stoneBuyFailCount() => View?.StoneBuyFailCount ?? 0;
    /// <summary>Error code of the last soul-stone BUY failure (0x5005), 0 if none seen</summary>
    public int lastStoneBuyFailErr() => View?.LastStoneBuyFailErr ?? 0;
    public bool dead() => View?.Dead ?? false;
    /// <summary>True if WE were hit in roughly the last 8s</summary>
    public bool inCombat() => View?.InCombat ?? false;

    /// <summary>True while OUR melee auto-attack (BASHSTART) swing stream is actually running — set when the server starts our…</summary>
    public bool bashing() => View?.BashActive ?? false;
    public double learnedRange() => View?.LearnedMeleeRange ?? 0;
    /// <summary>True if EITHER we were hit OR we landed a hit within the last ms (default 15000) — unlike (us being hit only),…</summary>
    public bool recentDamage(int withinMs = 15000)
    {
        var v = View; if (v is null) return false;
        var now = DateTime.UtcNow;
        return (now - v.LastHitAtUtc).TotalMilliseconds < withinMs
            || (now - v.LastDamageDealtAtUtc).TotalMilliseconds < withinMs;
    }
    /// <summary>True if WE landed a CONNECTING hit (Damage&amp;gt;0, not a whiff/out-of-range) within the last ms</summary>
    public bool damageDealt(int withinMs = 3000)
    {
        var v = View; if (v is null) return false;
        return v.LastRealDamageDealtAtUtc > DateTime.MinValue
            && (DateTime.UtcNow - v.LastRealDamageDealtAtUtc).TotalMilliseconds < withinMs;
    }
    /// <summary>Count of mobs the bot itself landed the killing blow on (REALLYKILL attacker==self)</summary>
    public int killsByMe() => View?.KillsByMe ?? 0;
    public bool respawn() => Ok(Wait(_mgr.RespawnAsync(Id)));
    public bool buyHpStone(int number = 1) => Ok(Wait(_mgr.BuyHpStoneAsync(Id, (ushort)number)));
    public bool buySpStone(int number = 1) => Ok(Wait(_mgr.BuySpStoneAsync(Id, (ushort)number)));
    /// <summary>Open an NPC's shop SYNCHRONOUSLY and return the OUTCOME (operator 2026-06-30: no recency window)</summary>
    public string openShop(int npcHandle, int menuOption = 1) => OpenShopInner(npcHandle, menuOption, 8);

    /// <summary>PROBE a candidate NPC: one click, ~2s wait, then give up. Use this for classification
    /// sweeps — openShop() re-clicks 8x (16s) which is right for a known shop and ruinous for a survey.</summary>
    public string probeShop(int npcHandle, int menuOption = 1) => OpenShopInner(npcHandle, menuOption, 1);

    private string OpenShopInner(int npcHandle, int menuOption, int maxReclicks)
    {
        Wait(_mgr.OpenShopAsync(Id, (ushort)npcHandle, (byte)menuOption, default, maxReclicks));
        var v = View; if (v is null) return "none";
        if (v.ShopOpen) return shopKind();
        if (v.RandomOptionUtc > DateTime.MinValue) return "randomoption";
        if (v.PendingQuest != null) return "quest";  // dual-role NPC: click opened quest dialogue, not a shop
        return "none";
    }

    /// <summary>True if the last openShop() got a RandomOption menu (0x3C0E</summary>
    public bool lastOpenWasRandomOption() => (View?.RandomOptionUtc ?? DateTime.MinValue) > DateTime.MinValue;
    public bool buy(int itemId, int lot = 1) => Ok(Wait(_mgr.BuyAsync(Id, (ushort)itemId, (uint)lot)));
    public bool sell(int slot, int lot = 1) => Ok(Wait(_mgr.SellAsync(Id, (byte)slot, (uint)lot)));

    /// <summary>Move ONE item between the bag and personal storage and return whether it was CONFIRMED</summary>
    public bool storageMove(int fromSlot, int toSlot, bool deposit = true)
        => Ok(Wait(_mgr.StorageMoveAsync(Id, (byte)fromSlot, (byte)toSlot, deposit)));
    public bool enchant(int equip, int raw, int rawLeft = 255, int rawMiddle = 255, int rawRight = 255, int money = 0)
        => Ok(Wait(_mgr.EnchantAsync(Id, (byte)equip, (byte)raw, (byte)rawLeft, (byte)rawMiddle, (byte)rawRight, (uint)money)));
    public bool target(int handle) => Ok(Wait(_mgr.TargetAsync(Id, (ushort)handle)));
    public bool untarget() => Ok(Wait(_mgr.UntargetAsync(Id)));
    public bool walk(double fx, double fy, double tx, double ty) => Ok(Wait(_mgr.WalkAsync(Id, (uint)fx, (uint)fy, (uint)tx, (uint)ty)));
    public bool travelTo(string map) => _mgr.TravelTo(Id, map).Result == BotManager.TravelResult.Started;
    public bool stopTravel() => Ok(_mgr.StopTravel(Id));
    /// <summary>True while a multi-hop cross-map is in flight</summary>
    public bool traveling() => _handle.TravelCts is { IsCancellationRequested: false };

    public bool walking() => _handle.WalkCts is { IsCancellationRequested: false };

    /// <summary>Non-moving route query (diagnostic / decision helper): can the bot route to from where it is?</summary>
    public DynValue routeTo(string map)
    {
        var (res, route) = _mgr.RouteInfo(Id, map);
        var t = NewTable();
        t["result"] = res.ToString();
        t["ok"] = res is BotManager.TravelResult.Started or BotManager.TravelResult.AlreadyThere;
        if (route is not null)
        {
            t["hops"] = route.Count;
            t["portals"] = route.Count(e => e.IsPortal);
            var maps = NewTable(); int i = 1;
            foreach (var e in route) maps[i++] = e.IsPortal ? $"{e.ToMap}(portal)" : e.ToMap;
            t["maps"] = DynValue.NewTable(maps);
        }
        return DynValue.NewTable(t);
    }

    /// <summary>All map nodes currently in the routing graph (seeded client nav + live-observed gates)</summary>
    public DynValue knownMaps()
    {
        var t = NewTable(); int i = 1;
        foreach (var m in _mgr.Graph.Maps().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) t[i++] = m;
        return DynValue.NewTable(t);
    }

    /// <summary>Outgoing edges from in the routing graph — field gates AND town portals — as { to=&amp;lt;map&amp;gt;, portal=bool } r…</summary>
    public DynValue mapEdges(string map)
    {
        var t = NewTable(); int i = 1;
        foreach (var e in _mgr.Graph.EdgesFrom(map)) { var r = NewTable(); r["to"] = e.ToMap; r["portal"] = false; t[i++] = DynValue.NewTable(r); }
        foreach (var e in _mgr.Graph.PortalEdgesFrom(map)) { var r = NewTable(); r["to"] = e.ToMap; r["portal"] = true; r["minLevel"] = e.MinLevel; t[i++] = DynValue.NewTable(r); }
        return DynValue.NewTable(t);
    }
    public bool follow(string name) => Ok(_mgr.Follow(Id, name));
    public bool stopFollow() => Ok(_mgr.StopFollow(Id));
    public bool useGate(int handle) => Ok(Wait(_mgr.UseGateAsync(Id, (ushort)handle)));
    public bool townPortal(int npcHandle, int dest) => Ok(Wait(_mgr.TownPortalAsync(Id, (ushort)npcHandle, (byte)dest)));

    /// <summary>Issue a GM command (prepends '&amp;amp;' if no prefix)</summary>
    public bool gm(string command)
    {
        var c = command.Trim();
        if (c.Length > 0 && c[0] != '&' && c[0] != '$') c = "&" + c;
        return Ok(Wait(_mgr.GmAsync(Id, c)));
    }

    /// <summary>Ask for a route to (x,y) on the current map. Returns WITHOUT pathfinding: the search runs off the
    /// tick and the walk is issued when it lands. False means the last COMPLETED search for this exact target
    /// found no route -- a caller that treats false as "unsolvable" learns it one tick later than it used to.</summary>
    public bool walkTo(double x, double y)
    {
        if (_handle.CurrentMap is not { } map) return false;
        if (_handle.Position is null) return false;
        var planner = _handle.NavPlanner ??= new Navigation.NavPlanner(RouteAndWalk);
        return planner.Request(map, (uint)x, (uint)y) != Navigation.NavPlanner.Verdict.Unreachable;
    }

    /// <summary>True while a pathfind for this bot is still running, so a script can wait instead of re-asking.</summary>
    public bool pathPending() => _handle.NavPlanner?.Busy ?? false;

    /// <summary>The pathfind + walk, run on the NavPlanner's worker thread. Returns whether a route was issued.
    /// This is the ORIGINAL walkTo body: nothing about the search changed, it just does not block the tick.</summary>
    private bool RouteAndWalk((string Map, uint X, uint Y) req)
    {
        var (map, x, y) = req;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_mgr.GridProvider?.Invoke(map) is not { } grid) return false;
        // The bot keeps walking its previous route while we search, so read the position HERE, not when the
        // script asked -- a route computed from a stale start point is a route the server rejects.
        if (_handle.Position is not { } pos) return false;
        if (!string.Equals(_handle.CurrentMap, map, StringComparison.OrdinalIgnoreCase)) return false;
        var path = PathFinder.FindPath(grid, pos.X, pos.Y, x, y);
        if (path.Count == 0 && grid.RuntimeBlockedCount > 0)
        {
            // UNREACHABLE on the runtime-augmented grid, but learned MOVEFAIL blocks may have wrongly SEVERED a route
            var poisoned = grid.RuntimeBlockedCount;
            grid.ClearRuntimeBlocked();
            path = PathFinder.FindPath(grid, pos.X, pos.Y, x, y);
            if (path.Count > 0)
                _handle.Log($"[nav] walkTo ({x},{y}) was UNREACHABLE — cleared {poisoned} poisoned learned-blocks, route re-opened");
        }
        if (path.Count == 0)
        {
            var (stx, sty) = grid.WorldToTile(pos.X, pos.Y);
            var (gtx, gty) = grid.WorldToTile(x, y);
            bool startUnwalkable = !grid.IsWalkableTile(stx, sty);
            _handle.Log($"[nav] walkTo ({x},{y}) UNREACHABLE on {map} after {sw.ElapsedMilliseconds}ms — " +
                $"start=({stx},{sty})walk={startUnwalkable} goal=({gtx},{gty})walk={grid.IsWalkableTile(gtx, gty)} " +
                $"grid={grid.WidthTiles}x{grid.HeightTiles}");
            // BLIND-MOVE ESCAPE (2026-07-17): the .shbd marks the bot's OWN tile unwalkable, but the SERVER has it standing there
            if (startUnwalkable)
            {
                // Escape toward the NEAREST WALKABLE .shbd tile — NOT blindly toward the target
                (uint X, uint Y)? esc = null;
                for (int r = 1; r <= 16 && esc is null; r++)
                    for (int ddx = -r; ddx <= r && esc is null; ddx++)
                        for (int ddy = -r; ddy <= r; ddy++)
                        {
                            if (System.Math.Max(System.Math.Abs(ddx), System.Math.Abs(ddy)) != r) continue; // ring perimeter
                            int nx = stx + ddx, ny = sty + ddy;
                            if (nx < 0 || ny < 0 || nx >= grid.WidthTiles || ny >= grid.HeightTiles) continue;
                            if (grid.IsWalkableTile(nx, ny)) { esc = grid.TileToWorld(nx, ny); break; }
                        }
                if (esc is { } e)
                {
                    _handle.Log($"[nav] walkTo: start tile UNWALKABLE (.shbd gap) — BLIND-MOVE escape toward nearest walkable ({e.X},{e.Y}) [server governs walkability]");
                    _ = _mgr.WalkAsync(Id, pos.X, pos.Y, e.X, e.Y);
                    return true;
                }
                double dx = (double)x - pos.X, dy = (double)y - pos.Y;
                double len = System.Math.Sqrt(dx * dx + dy * dy);
                if (len > 1)
                {
                    const double ESCAPE = 160.0;
                    var ex = (uint)System.Math.Max(0, pos.X + dx / len * ESCAPE);
                    var ey = (uint)System.Math.Max(0, pos.Y + dy / len * ESCAPE);
                    _handle.Log($"[nav] walkTo: start tile UNWALKABLE — BLIND-MOVE toward target ({ex},{ey}) [no walkable tile nearby]");
                    _ = _mgr.WalkAsync(Id, pos.X, pos.Y, ex, ey);
                    return true;
                }
            }
            return false;
        }
        var simplified = PathFinder.Simplify(path);
        var wr = _mgr.WalkPath(Id, simplified);
        // The search cost is the number that mattered: it used to be charged to the tick.
        _handle.Log(BotLogLevel.Info,
            $"[nav] route to ({x},{y}) on {map}: {sw.ElapsedMilliseconds}ms off-tick -> {simplified.Count} waypoints ({wr})");
        return Ok(wr);
    }

    /// <summary>Face (x,y) and STOP, committing the bot's position to the server (MOVERUN+STOP, exactly what the real client s…</summary>
    public bool commitStop(double x, double y) => Ok(Wait(_mgr.CommitStopAsync(Id, (uint)x, (uint)y)));

    public bool partyInvite(string name) => Ok(Wait(_mgr.PartyInviteAsync(Id, name)));
    public bool partyAccept(string name = null) => Ok(Wait(_mgr.PartyAcceptAsync(Id, name)));
    public bool partyDecline(string name = null) => Ok(Wait(_mgr.PartyDeclineAsync(Id, name)));
    public string pendingInvite() => _handle.PendingPartyInviter ?? "";
    public bool partyChat(string text) => Ok(Wait(_mgr.PartyChatAsync(Id, text)));

    /// <summary>The live party roster (from the WM party member-state packets) — an array of tables { name, class, level, hp,…</summary>
    public DynValue partyMembers()
    {
        var t = NewTable(); int i = 1;
        foreach (var m in _handle.PartyMembers.Values)
        {
            var mt = NewTable();
            mt["name"] = m.Name; mt["class"] = (int)m.ChrClass; mt["level"] = (int)m.Level;
            mt["hp"] = (double)m.Hp; mt["maxhp"] = (double)m.MaxHp;
            mt["sp"] = (double)m.Sp; mt["maxsp"] = (double)m.MaxSp;
            mt["x"] = (double)m.X; mt["y"] = (double)m.Y;
            t[i++] = DynValue.NewTable(mt);
        }
        return DynValue.NewTable(t);
    }
    public bool friendAdd(string name) => Ok(Wait(_mgr.FriendAddAsync(Id, name)));
    public bool friendConfirm(string name, bool accept) => Ok(Wait(_mgr.FriendConfirmAsync(Id, name, accept)));
    public bool friendDelete(string name) => Ok(Wait(_mgr.FriendDeleteAsync(Id, name)));
    /// <summary>Name of a pending incoming friend request (someone added the bot), or "" if none</summary>
    public string pendingFriend() => _handle.PendingFriendRequester ?? "";
    /// <summary>Accept the pending incoming friend request (no-op if none)</summary>
    public bool friendAccept()
    {
        var who = _handle.PendingFriendRequester;
        return !string.IsNullOrEmpty(who) && Ok(Wait(_mgr.FriendConfirmAsync(Id, who!, true)));
    }

    // ── state / vitals ──────────────────────────────────────────────────────────
    public double? hp() => View?.Hp;
    public double? sp() => View?.Sp;
    public double maxHp() => View?.MaxHp ?? 0;
    public double maxSp() => View?.MaxSp ?? 0;

    /// <summary>Current HP as a 0–100 percentage of max, or -1 if HP/max isn't known yet</summary>
    public double hpPct()
    {
        var v = View;
        if (v is null || v.MaxHp == 0 || v.Hp is not { } h) return -1;
        return 100.0 * h / v.MaxHp;
    }

    public double spPct()
    {
        var v = View;
        if (v is null || v.MaxSp == 0 || v.Sp is not { } s) return -1;
        return 100.0 * s / v.MaxSp;
    }

    /// <summary>How many mobs are currently aggroing the bot (combat window) — the "am I overwhelmed?" signal for a flee trans…</summary>
    public int aggressors() => _mgr.AggressorCount(Id);

    /// <summary>The HANDLES of entities currently attacking the bot (from inbound SWING_DAMAGE where the bot is the defender,…</summary>
    public DynValue aggressorHandles()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        int i = 1;
        foreach (var h in v.Aggressors) t[i++] = (int)h;
        return DynValue.NewTable(t);
    }

    public DynValue aggressorSpawns()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var aggro = v.Aggressors;
        int i = 1;
        foreach (var n in v.NearbyNpcs)
        {
            if (!aggro.Contains(n.Handle)) continue;
            var row = NewTable();
            row["handle"] = n.Handle; row["mobId"] = n.MobId; row["x"] = n.X; row["y"] = n.Y;
            if (v.MobAnchor(n.Handle) is { } a) { row["anchorX"] = a.X; row["anchorY"] = a.Y; }
            var from = v.AnchorDistance(n.Handle) ?? 0;
            // A clone has no mob id, so it has no learned chase limit — reading one would return mob 0's (a Slime's) leash a…
            var limit = n.IsScenarioClone ? 0 : v.MobChaseLimit(n.MobId);
            if (n.IsScenarioClone) row["isClone"] = true;
            row["fromSpawn"] = from; row["chaseLimit"] = limit;
            row["willDropIn"] = limit > 0 ? limit - from : 0;
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    public double mobChaseLimit(int mobId) => View?.MobChaseLimit(mobId) ?? 0;

    /// <summary>World position { x, y } of ANY tracked entity by handle — a mob ( _npcs ) OR a character ( _nearby )</summary>
    public DynValue entityPos(int handle)
    {
        var v = View; if (v is null) return DynValue.Nil;
        var h = (ushort)handle;
        foreach (var n in v.NearbyNpcs) if (n.Handle == h) { var r = NewTable(); r["x"] = n.X; r["y"] = n.Y; return DynValue.NewTable(r); }
        foreach (var p in v.NearbyPlayers) if (p.Handle == h) { var r = NewTable(); r["x"] = p.X; r["y"] = p.Y; return DynValue.NewTable(r); }
        return DynValue.Nil;
    }

    /// <summary>Flee: walk away from the threat by units</summary>
    public bool flee(double dist = 500) => Ok(_mgr.Flee(Id, dist));

    public double? x() => _handle.Position?.X;
    public double? y() => _handle.Position?.Y;
    public string? map() => _handle.CurrentMap;

    /// <summary>True if a map (by name; defaults to the current map) is an INDOOR/dungeon/instance map (MapInfo.shn InSide=1,…</summary>
    public bool mapInside(string mapName = null) => _mgr.ClientData?.MapInside(mapName ?? _handle.CurrentMap) ?? false;
    public int? selfHandle() => _handle.SelfHandle;
    public bool mounted() => View?.IsMounted ?? false;

    /// <summary>True while the travel driver is taking a GATE HOP and mounting must be held off</summary>
    public bool noMount() => _handle.SuppressMount;
    public double walkSpeed() => _handle.WalkSpeed;
    public int level() => (int)_handle.Level;
    public string phase() => _handle.Phase.ToString();
    public bool inZone() => _handle.Phase == BotPhase.InZone;

    /// <summary>A monotonic millisecond clock for script-side cooldowns ( if bot.now() - last &gt; 3000 then</summary>
    public double now() => Environment.TickCount64;

    /// <summary>Headline log (Note): quest accept/finish, level-up, death, purchase, errors</summary>
    public void log(string message) => _handle.Log(BotLogLevel.Note, $"[lua] {message}");
    /// <summary>Progress log (Info): each kill, quest-objective credit, restock/travel choices</summary>
    public void logi(string message) => _handle.Log(BotLogLevel.Info, $"[lua] {message}");
    /// <summary>Firehose log (Verbose): per-tick move/cast/auto-attack + the state dump</summary>
    public void logv(string message) => _handle.Log(BotLogLevel.Verbose, $"[lua] {message}");

    // perception (tables) ─────────────────────────────────────────────────── Zone handle of the nearest non-gate mo…
    public DynValue nearestMob()
    {
        var v = View; var pos = _handle.Position;
        if (v is null || pos is not { } p) return DynValue.Nil;
        int? best = null; var bestD = double.MaxValue;
        foreach (var n in v.NearbyNpcs)
        {
            if (n.IsGate) continue;
            var d = Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y);
            if (d < bestD) { bestD = d; best = n.Handle; }
        }
        return best is { } b ? DynValue.NewNumber(b) : DynValue.Nil;
    }

    /// <summary>Resolve a mob/NPC id</summary>
    public DynValue npcLocation(int mobId)
    {
        if (View?.Npc(mobId) is not { } e) return DynValue.Nil;
        var t = NewTable(); t["x"] = e.X; t["y"] = e.Y; t["isGate"] = e.IsGate; t["linkMap"] = e.LinkMap;
        if (_handle.Position is { } p) t["dist"] = Math.Sqrt(Sq((double)e.X - p.X) + Sq((double)e.Y - p.Y));
        return DynValue.NewTable(t);
    }

    /// <summary>A quest/turn-in NPC's canonical location from client MobCoordinate.shn ({map,x,y}), or nil if the NPC isn't in…</summary>
    public DynValue npcCoord(int npcId)
    {
        var loc = _mgr.ClientData?.MobCoordinate(npcId, _handle.CurrentMap);
        if (loc is null) return DynValue.Nil;
        var t = NewTable(); t["map"] = loc.Map; t["x"] = loc.CenterX; t["y"] = loc.CenterY;
        return DynValue.NewTable(t);
    }
    /// <summary>Count of NPCs in the current map's seed roster</summary>
    public int npcSeedCount() => View?.NpcSeedCount ?? 0;

    /// <summary>The full map-enter NPC SEED roster as a lua array of {mobId, x, y, isGate, linkMap, dist} — every NPC+gate on…</summary>
    public DynValue npcSeedList()
    {
        var v = View; var arr = NewTable();
        if (v is not null)
        {
            int i = 1;
            foreach (var e in v.NpcSeedAll)
            {
                var t = NewTable();
                t["mobId"] = e.MobId; t["x"] = e.X; t["y"] = e.Y; t["isGate"] = e.IsGate; t["linkMap"] = e.LinkMap;
                if (_handle.Position is { } p) t["dist"] = Math.Sqrt(Sq((double)e.X - p.X) + Sq((double)e.Y - p.Y));
                arr.Append(DynValue.NewTable(t));
                i++;
            }
        }
        return DynValue.NewTable(arr);
    }

    public DynValue npcByMob(int mobId)
    {
        var v = View; if (v is null) return DynValue.Nil;
        var pos = _handle.Position;
        foreach (var n in v.NearbyNpcs)
        {
            if (n.IsScenarioClone || n.MobId != mobId) continue;   // a clone answers to no mob id, not even 0
            var row = NewTable();
            row["handle"] = n.Handle; row["mobId"] = n.MobId; row["x"] = n.X; row["y"] = n.Y;
            if (pos is { } p) row["dist"] = Math.Sqrt(Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y));
            return DynValue.NewTable(row);
        }
        return DynValue.Nil;
    }

    public DynValue nearbyMobs()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var pos = _handle.Position;
        var i = 1;
        foreach (var n in v.NearbyNpcs)
        {
            var row = NewTable();
            row["handle"] = n.Handle; row["mobId"] = n.MobId; row["mode"] = n.Mode;
            row["x"] = n.X; row["y"] = n.Y; row["isGate"] = n.IsGate; row["linkMap"] = n.LinkMap;
            // Facing (SHINE_COORD_TYPE.dir) and the cur/max hp pair the client's health bar shows
            row["dir"] = n.Dir;
            if (View?.EntityHp(n.Handle) is { } eh) row["hp"] = (double)eh;
            // A SCENARIO CLONE HAS NO MOB ID — do not look one up
            if (n.IsScenarioClone)
            {
                row["isClone"] = true;
                row["name"] = n.CharName;
                if (n.CharLevel is { } cl) row["level"] = (double)cl;
            }
            else
            {
                var mx = _mgr.ClientData?.Mob(n.MobId)?.MaxHp ?? -1;
                if (mx > 0) row["maxhp"] = (double)mx;
            }
            // Huntable = a real monster (not a guard / shop NPC / quest giver / resource node)
            row["isHuntable"] = n.IsScenarioClone
                                || (v.IsHuntableMob?.Invoke((ushort)n.MobId) ?? true);
            if (pos is { } p) row["dist"] = Math.Sqrt(Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y));
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Human label for an entity handle, for post-mortems: "SELF", "aggressor #N" (N = its place in the current aggre…</summary>
    private string HandleLabel(ushort h, IReadOnlyList<ushort> aggro)
    {
        if (View?.SelfHandle == h) return "SELF";
        for (var i = 0; i < aggro.Count; i++) if (aggro[i] == h) return $"aggressor #{i + 1}";
        var npc = View?.NearbyNpcs.FirstOrDefault(n => n.Handle == h);
        // A clone is named by its CHARACTER name — "mob0" would read as the Slime that mob id 0 really is
        if (npc is { IsScenarioClone: true }) return $"clone {npc.CharName ?? "?"} L{npc.CharLevel?.ToString() ?? "?"}";
        if (npc is not null && npc.Handle == h) return $"mob{npc.MobId}";
        return $"h={h}";
    }

    /// <summary>The always-on packet ring: the last frames in BOTH directions, oldest first, as rows { ts, outbound, opcode, n…</summary>
    public DynValue recentPackets(int max = 100)
    {
        var t = NewTable();
        var frames = _handle.PacketRing.Snapshot(max <= 0 ? 100 : max);
        var aggro = View?.Aggressors?.ToList() ?? new List<ushort>();
        var i = 1;
        foreach (var f in frames)
        {
            var row = NewTable();
            row["ts"] = f.AtUtc.ToString("HH:mm:ss.fff");
            row["outbound"] = f.Outbound;
            row["opcode"] = f.Opcode;
            row["name"] = FiestaLibReloaded.Networking.PacketRegistry.GetType(f.Opcode)?.Name ?? "?";   // same resolver the file log uses
            row["len"] = f.Payload.Length;
            row["hex"] = Convert.ToHexString(f.Payload);
            var sb = new System.Text.StringBuilder(f.Payload.Length);
            foreach (var b in f.Payload) sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            row["ascii"] = sb.ToString();
            row["note"] = CombatNote(f, aggro);
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    private string CombatNote(Net.RingFrame f, IReadOnlyList<ushort> aggro)
    {
        var p = f.Payload;
        ushort U16(int o) => (ushort)(p[o] | (p[o + 1] << 8));
        uint U32(int o) => (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));
        try
        {
            switch (f.Opcode)
            {
                case 0x2448 when p.Length >= 12:   // NC_BAT_SWING_DAMAGE_CMD
                    return $"SWING {HandleLabel(U16(0), aggro)} -> {HandleLabel(U16(2), aggro)} dmg={U16(6)} rest={U32(8)}";
                case 0x2449 when p.Length >= 9:    // NC_BAT_SOMEONESWING_DAMAGE_CMD (no damage field)
                    return $"SWING(other) {HandleLabel(U16(0), aggro)} -> {HandleLabel(U16(2), aggro)} rest={U32(5)}";
                case 0x2452 when p.Length >= 5:    // NC_BAT_SKILLBASH_HIT_DAMAGE_CMD
                    return $"SKILLHIT skill={U16(0)} caster={HandleLabel(U16(2), aggro)} targets={p[4]}";
                default:
                    return "";
            }
        }
        catch { return ""; }
    }

    /// <summary>Every learned skill whose cooldown has NOT yet elapsed, as rows { id, name, remainMs }</summary>
    public DynValue skillCooldowns()
    {
        var t = NewTable(); var i = 1;
        var v = View; if (v is null) return DynValue.NewTable(t);
        foreach (var sid in v.LearnedSkills)
        {
            var last = v.SkillLastCastAtUtc(sid);
            if (last is null) continue;                       // never cast → ready
            var cd = _mgr.ClientData?.Skill(sid)?.DelayTimeMs ?? 0;
            if (cd <= 0) continue;
            var remain = cd - (DateTime.UtcNow - last.Value).TotalMilliseconds;
            if (remain <= 0) continue;                        // elapsed → ready
            var row = NewTable();
            row["id"] = (int)sid;
            row["name"] = _mgr.ClientData?.SkillName(sid) ?? "";
            row["remainMs"] = Math.Round(remain);
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Milliseconds until the HP soul stone can be used again (0 = ready, -1 = cooldown not yet learned — it takes tw…</summary>
    public double hpStoneReadyInMs() => View?.HpStoneReadyInMs ?? -1;

    /// <summary>Our FACING as a compass angle in degrees (0-360), or -1 if no heading has been set yet</summary>
    public double facingDeg() => _handle.FacingDeg;

    /// <summary>Facing as its raw unit vector { dx, dy } plus deg , for callers doing their own geometry rather than re-derivi…</summary>
    public DynValue facing()
    {
        var t = NewTable();
        t["dx"] = _handle.FacingDx; t["dy"] = _handle.FacingDy; t["deg"] = _handle.FacingDeg;
        return DynValue.NewTable(t);
    }

    public DynValue nearbyPlayers()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var i = 1;
        foreach (var p in v.NearbyPlayers)
            t[i++] = PlayerRow(p);
        return DynValue.NewTable(t);
    }

    public DynValue gates()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var i = 1;
        foreach (var n in v.NearbyNpcs)
        {
            if (!n.IsGate) continue;
            var row = NewTable();
            row["handle"] = n.Handle; row["x"] = n.X; row["y"] = n.Y; row["linkMap"] = n.LinkMap;
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Items on the ground in view (rows: handle, itemId, x, y, dropMob, dist)</summary>
    public DynValue drops()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var pos = _handle.Position;
        var i = 1;
        foreach (var g in v.Drops)
        {
            var row = NewTable();
            row["handle"] = g.Handle; row["itemId"] = g.ItemId; row["x"] = g.X; row["y"] = g.Y;
            row["dropMob"] = g.DropMobHandle;
            if (pos is { } p) row["dist"] = Math.Sqrt(Sq((double)g.X - p.X) + Sq((double)g.Y - p.Y));
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Handle of the ground drop nearest the bot, or nil if nothing's on the ground</summary>
    public DynValue nearestDrop()
    {
        var v = View; var pos = _handle.Position;
        if (v is null || pos is not { } p) return DynValue.Nil;
        var g = v.NearestDrop(p.X, p.Y);
        return g is null ? DynValue.Nil : DynValue.NewNumber(g.Handle);
    }

    public DynValue inventory()
    {
        var t = NewTable();
        var inv = View?.Inventory; if (inv is null) return DynValue.NewTable(t);
        foreach (var (slot, itemId) in inv) t[(int)slot] = itemId;
        return DynValue.NewTable(t);
    }

    /// <summary>Bag stack counts: slot → how many of that slot's item we hold</summary>
    public DynValue inventoryCounts()
    {
        var t = NewTable();
        var v = View; var inv = v?.Inventory; if (v is null || inv is null) return DynValue.NewTable(t);
        foreach (var (slot, _) in inv) t[(int)slot] = v.ItemCount(slot);
        return DynValue.NewTable(t);
    }

    public DynValue equipment()
    {
        var t = NewTable();
        var eq = View?.Equipment; if (eq is null) return DynValue.NewTable(t);
        foreach (var (slot, itemId) in eq) t[(int)slot] = itemId;
        return DynValue.NewTable(t);
    }

    /// <summary>Resolve a nearby player by name (case-insensitive) to a row table, or nil</summary>
    public DynValue playerByName(string name)
    {
        var p = View?.NearbyPlayers.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return p is null ? DynValue.Nil : PlayerRow(p);
    }

    private DynValue PlayerRow(Session.NearbyPlayer p)
    {
        var row = NewTable();
        row["handle"] = p.Handle; row["name"] = p.Name; row["class"] = p.Class;
        row["level"] = p.Level; row["x"] = p.X; row["y"] = p.Y;
        row["mode"] = p.Mode; row["type"] = p.Type; row["kqTeam"] = p.KQTeamType;
        return DynValue.NewTable(row);
    }

    private Table NewTable() => new(_lua);
    private static double Sq(double a) => a * a;
}
