using Fiesta.Bot.Manager;
using Fiesta.Bot.Pathfinding;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>
/// The Lua-facing facade for ONE bot — the <c>bot</c> global a behaviour script
/// drives. Every action method forwards to <see cref="BotManager"/> (the same seam
/// the HTTP endpoints use); every getter reads the bot's live <see cref="Session.ZoneView"/>
/// / <see cref="BotHandle"/> perception. Actions are synchronous from Lua's view: the
/// underlying sends are quick and the runner gives each bot its own dedicated script
/// thread, so blocking on them is fine and keeps scripts deterministic.
///
/// <para>Method names are intentionally camelCase (not PascalCase) so scripts read
/// idiomatically — <c>bot.cast(1500, h)</c>, <c>bot.hp()</c>. Registered as a MoonSharp
/// userdata; table-returning getters build Lua tables via the attached <see cref="Script"/>
/// (always called on the script thread, so the non-thread-safe VM is never touched
/// concurrently).</para>
/// </summary>
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

    /// <summary>Attach the owning VM (so getters can build Lua tables). Called once by
    /// the runner on the script thread before any script code runs.</summary>
    internal void AttachScript(Script lua) => _lua = lua;

    /// <summary>Set by the runner to receive the current state-machine state name as the
    /// Lua harness transitions, so the runner/debug endpoint can report it.</summary>
    internal Action<string>? StateReporter;

    /// <summary>Called by the state-machine harness on each transition to report the new
    /// state to C# (surfaced in the script status). Not normally called by hand.</summary>
    public void __state(string name) => StateReporter?.Invoke(name);

    /// <summary>Set by the behaviour-graph runner so a state script can request a graph
    /// transition (<c>bot.requestState("stay_alive")</c>).</summary>
    internal Action<string>? RequestStateHandler;

    /// <summary>Request a behaviour-graph transition to <paramref name="state"/> next tick.
    /// Returns false if not running under a graph (no handler).</summary>
    public bool requestState(string state)
    {
        if (RequestStateHandler is null) return false;
        RequestStateHandler(state); return true;
    }

    /// <summary>Skill info from client <c>ActiveSkill</c> (cooldown ms, SP cost, range, facing
    /// arc) so scripts can track cooldowns / costs without hardcoding. nil if unknown.</summary>
    public DynValue skillInfo(int id)
    {
        var si = _mgr.ClientData?.Skill(id);
        if (si is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = id; t["name"] = _mgr.ClientData?.SkillName(id) ?? "";
        t["cooldownMs"] = si.DelayTimeMs; t["sp"] = si.Sp; t["range"] = si.Range;
        t["usableDegree"] = si.UsableDegree; t["moving"] = si.IsMovingSkill;
        // UseClass: real class combat skills are >=2 (Fighter line 2-7, Cleric 8-13, Archer 14-19,
        // Mage 20-25, Joker 27+); ==1 is the Trainee/alchemy/event bucket (Mining, Ride Mover, the
        // event water-balloons) — the cast rotation must skip those. Passives aren't in ActiveSkill.
        t["useClass"] = si.UseClass;
        // maxWc = the skill's weapon-damage coefficient (ActiveSkill.MaxWC). >0 = deals real damage
        // (Slice&Dice/Bone Slicer/Fatal Slash); 0 = utility with NO damage (Snearing Kick, Concussive
        // Charge). The kite-chip rotation casts only maxWc>0 skills so a fled mob keeps bleeding.
        t["maxWc"] = si.MaxWc;
        return DynValue.NewTable(t);
    }

    /// <summary>Item fields from client ItemInfo for shop eval: {id, name, useClass, demandLv, grade,
    /// equipSlot, isScroll}. isScroll = a skill scroll (USE it to learn the skill named the same as
    /// the item). useClass = class line (Fighter 2–7, 0 = all). nil if unknown.</summary>
    public DynValue itemInfo(int id)
    {
        var it = _mgr.ClientData?.Item(id);
        if (it is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = id; t["name"] = it.Name; t["useClass"] = it.UseClass; t["demandLv"] = it.DemandLv;
        t["grade"] = it.Grade; t["equipSlot"] = it.EquipSlot; t["isScroll"] = it.IsScroll; t["type"] = it.Type;
        // itemClass = the ItemInfo `Class` sub-type WITHIN a Type (e.g. Type 2 material: Class 0 =
        // plain material/dust [sellable junk], 14 = Elrue enchant stone, 34 = enchant "rune", 19/20/25
        // = Red Eye/Blue Mile/Gold Nine safety gems, 16 = license, 17 = key — all KEEP; Type 1 usable:
        // Class 0 = potion/buff, 11 = skill scroll, 12 = recall/port scroll, 30 = unopened card).
        // sellPrice 0 = the server will NOT vendor it (quest items, Ex Elreu, keys) — never attempt.
        // maxLot 1 = non-stacking (enchant runes fill the bag; operator 2026-07-02). Used by the
        // sell/keep classifier (classifyItem in level_quest.lua). See QuestsNew.pcapng sell demo.
        t["itemClass"] = it.ItemClass; t["maxLot"] = it.MaxLot; t["sellPrice"] = it.SellPrice;
        // gradeType 0 = ordinary/replaceable gear (every plain smith-bought item — Leather/Chain Boots,
        // Chain Helmet/Pants, Buckler — verified ItemGradeType=0); >=1 = a named/special drop (e.g. "Solar
        // Eclipse Leather Boots") worth keeping. The vendor-trash signal for grey-gear selling.
        t["gradeType"] = it.GradeType;
        // For a skill scroll, the ACTIVE-skill id it teaches (InxName join), else -1. Lets the driver
        // skip buying a scroll for a skill already learned: if hasSkill(itemInfo(id).scrollSkillId) skip.
        var scrollSid = it.IsScroll ? (_mgr.ClientData?.ScrollSkillId(id) ?? -1) : -1;
        t["scrollSkillId"] = scrollSid;
        // The PREREQUISITE skill id the taught skill needs first (ActiveSkill DemandSk), or 0 if none.
        // Gate learn-from-bag on hasSkill(prereq) so a rank-[02] scroll isn't USE'd (and looped forever)
        // before rank-[01] is learned — the server refuses the out-of-order learn.
        t["scrollSkillPrereq"] = scrollSid >= 0 ? (_mgr.ClientData?.SkillPrereqId(scrollSid) ?? 0) : 0;
        return DynValue.NewTable(t);
    }

    /// <summary>The ACTIVE-skill id a skill scroll teaches (via the ItemInfo↔ActiveSkill InxName join),
    /// or -1 if the item isn't a skill scroll. Pair with <see cref="hasSkill"/> to avoid over-buying:
    /// don't buy a scroll whose <c>scrollSkillId</c> is already learned (or whose item is in the bag).</summary>
    public int scrollSkillId(int itemId) => _mgr.ClientData?.ScrollSkillId(itemId) ?? -1;

    /// <summary>The PERSISTED learnt shop kind of an NPC on the current server+map ("weapon"|"skill"|
    /// "item"|"soulstone"|"notshop"), or "" if never encountered. Lets discovery skip re-probing an NPC
    /// it (or another bot, on a prior run) already classified — a town is classified ONCE EVER.</summary>
    public string knownShopKind(int npcId)
    {
        var map = _handle.CurrentMap;
        if (string.IsNullOrEmpty(map)) return "";
        return _mgr.Knowledge.ShopKind(_handle.Options.Host, map!, npcId) ?? "";
    }

    /// <summary>Record + PERSIST what an NPC's shop turned out to be (current server+map). Call after a
    /// shop-open classifies it so the knowledge survives relog/restart.</summary>
    public void recordShop(int npcId, string kind)
    {
        var map = _handle.CurrentMap;
        if (!string.IsNullOrEmpty(map)) _mgr.Knowledge.RecordShop(_handle.Options.Host, map!, npcId, kind);
    }

    /// <summary>The character level at which quest <paramref name="questId"/> was last deprioritized
    /// (a flee happened pursuing its objective mob), or -1 if never. PERSISTED (survives relog/restart)
    /// so a rebuild-cycle doesn't forget it and immediately re-trigger the same overwhelming fight.
    /// Compare against <see cref="level"/> — once you've gained a level since, treat it as expired
    /// (operator 2026-07-01: "after 1 level up, reset this").</summary>
    public int questDeprioritizedAtLevel(int questId) => _mgr.Knowledge.QuestDeprioritizedAtLevel(_handle.Options.Host, questId);

    /// <summary>Record + PERSIST that a flee happened while pursuing this quest's objective mob, at the
    /// current character level — deprioritizes it to "last resort" until a level-up.</summary>
    public void recordQuestDeprioritized(int questId, int atLevel) => _mgr.Knowledge.RecordQuestDeprioritized(_handle.Options.Host, questId, atLevel);

    /// <summary>The item ids the last-opened merchant sells (from SHOPOPEN/SHOPOPENTABLE). Empty
    /// until a shop is opened. The driver reads this + <see cref="itemInfo"/> to decide what to buy.</summary>
    public DynValue shopItems()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var id in v.ShopItems) t[i++] = (int)id;
        return DynValue.NewTable(t);
    }

    /// <summary>True if the bot has already learned this skill id (from the login skill list +
    /// any learned this session). Lets the driver skip re-buying a scroll it already knows.</summary>
    public bool hasSkill(int id) => View?.HasSkill((ushort)id) ?? false;

    /// <summary>The inventory bag slot currently holding <paramref name="itemId"/> (e.g. a scroll
    /// just bought), or -1 if not in the bag. Use with <see cref="useItem"/> to consume it.</summary>
    public int invenSlotOf(int itemId)
    {
        var v = View; if (v is null) return -1;
        foreach (var kv in v.Inventory) if (kv.Value == (ushort)itemId) return kv.Key;
        return -1;
    }

    /// <summary>The stack count in main-bag <paramref name="slot"/> (from the wire lot field), 0 if
    /// empty. NOTE: SELL's lot is a 0/1 toggle (1 = sell the WHOLE stack), not a count — so to sell
    /// a slot use <c>bot.sell(slot, 1)</c>, not this count. Kept for inventory-fullness checks.</summary>
    public int invenCount(int slot) => View?.ItemCount((byte)slot) ?? 0;

    /// <summary>Total quantity of an item id carried across ALL bag slots (sums stacks). Used by collect
    /// quests to know how many of the drop item the bot is holding vs the required count — the hand-in
    /// gate is "carried >= required", not the kill-objective progress counter.</summary>
    public int invenCountOf(int itemId)
    {
        var v = View; if (v is null) return 0;
        int total = 0;
        foreach (var (slot, id) in v.Inventory)
            if (id == itemId) total += v.ItemCount(slot);
        return total;
    }

    /// <summary>True when the bag is FULL (a pickup failed with the inventory-full ack 0x346). The leveler
    /// uses this to break the death spiral: when full it travels to town and sells/declutters instead of
    /// pacing over a drop it can't carry. Cleared on a successful sell or pick.</summary>
    public bool bagFull() => View?.BagFull ?? false;

    /// <summary>Current money ("cen"), or -1 if no money packet seen yet. Use to gate buys and to
    /// confirm a sell paid out (money rises after a successful sell).</summary>
    public double money() => View?.Money ?? -1;
    /// <summary>Current total experience (seeded at zone-enter, updated by per-kill EXPGAIN), or -1
    /// if not yet seeded. Lets the leveler see grind progress toward the next level.</summary>
    public double exp() => View?.Exp ?? -1;

    /// <summary>The current character class id. Reflects a live JOB CHANGE (PROMOTE_ACK 0x1059) — the
    /// leveler reads this to confirm the JCQ succeeded (class changed) and to pick class-appropriate rewards.</summary>
    public int charClass() => _handle.Class;
    /// <summary>The class id from the most recent PROMOTE_ACK this session, or -1 if no promotion yet.
    /// Use to DETECT that a job change just happened (was -1 / old class → new class).</summary>
    public int promotedClass() => View?.PromotedClass ?? -1;
    /// <summary>The scenario/instance trigger-area last entered+armed (e.g. "Zone_Mob01"), or "" if not in
    /// a scenario. The clear-room driver uses this to know it's inside the instance and which room armed.</summary>
    public string scenarioArea() => View?.LastScenarioArea ?? "";

    /// <summary>The raw code from the last SELL_ACK (0x3005): 0x0381 = success, else rejected;
    /// -1 if no sell acked yet this session. Lets the driver verify a sell took.</summary>
    public int lastSellAck() => View?.LastSellAck ?? -1;

    /// <summary>The raw code from the last BUY_ACK (0x3004): 0x0201 = success (item added), else rejected
    /// (e.g. 0x0204); -1 if no buy acked yet. Lets buyGear/learnSkills confirm a buy took before marking
    /// it bought/learned — and pace buys one-per-ACK to dodge the rapid-fire rejection.</summary>
    public int lastBuyAck() => View?.LastBuyAck ?? -1;
    /// <summary>True if the last buy succeeded (0x0201).</summary>
    public bool lastBuyOk() => (View?.LastBuyAck ?? -1) == 0x0201;
    /// <summary>Monotonic count of BUY_ACKs seen this session. Record it just before firing a buy;
    /// when it increases, THIS buy's result has landed in <see cref="lastBuyOk"/> — so buyGear/learnSkills
    /// can gate "bought/learned" on the actual ack (and detect a buy that got NO ack = shop closed).</summary>
    public int buyAckCount() => View?.BuyAckCount ?? 0;

    /// <summary>True if a shop is genuinely open right now (item or soul-stone) — a SELL/BUY will be
    /// accepted. openShop() confirms this before returning; check it before selling.</summary>
    public bool shopOpen() => View?.ShopOpen ?? false;

    /// <summary>The KIND of the last-opened shop: "skill" | "weapon" | "item" | "soulstone" |
    /// "unknown". Lets the driver classify an NPC by what shop it opens (find the skill master /
    /// smith / item merchant / healer dynamically — no hardcoded ids). Read right after openShop().</summary>
    public string shopKind() => (View?.LastShopKind ?? Session.ShopKind.Unknown) switch
    {
        Session.ShopKind.Skill => "skill",
        Session.ShopKind.Weapon => "weapon",
        Session.ShopKind.Item => "item",
        Session.ShopKind.SoulStone => "soulstone",
        _ => "unknown",
    };

    /// <summary>The learned skill id of the highest rank whose name starts with
    /// <paramref name="prefix"/> (e.g. <c>"Heal"</c> → the best heal you've learned), or 0 if
    /// none. Rank parsed from the <c>"[NN]"</c> in the skill name. Lets a script pick a skill
    /// by role without hardcoding the id/rank.</summary>
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

    /// <summary>All learned skill ids (from the zone-login skill list + any learned this session).
    /// The combat rotation reads this and casts each offensive skill by its own cooldown
    /// (<see cref="skillInfo"/>) — class-agnostic, no hardcoded skill names.</summary>
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
    public bool heal(int skill) => Ok(Wait(_mgr.HealSelfAsync(Id, (ushort)skill)));
    public bool useItem(int slot, int invenType = 9) => Ok(Wait(_mgr.UseItemAsync(Id, (byte)slot, (byte)invenType)));
    public bool equip(int slot) => Ok(Wait(_mgr.EquipAsync(Id, (byte)slot)));
    public bool pickup(int itemHandle) => Ok(Wait(_mgr.PickupAsync(Id, (ushort)itemHandle)));
    public bool loot(int itemHandle = 0) => Ok(Wait(_mgr.LootAsync(Id, (ushort)itemHandle)));
    /// <summary>Fire the client's inventory auto-sort (compact + STACK the bag — e.g. merge the
    /// single quest-reward port scrolls that don't stack by default). Frees slots so pickups/hand-ins
    /// don't block on a full bag. ⚠️ Blocked while MOUNTED and the bag is locked ~5s during the
    /// relayout — gate on <c>not bot.mounted()</c> and don't fire other inventory ops right after.</summary>
    public bool sortInventory() => Ok(Wait(_mgr.SortInventoryAsync(Id)));
    /// <summary>Pick-pacing poll gate (operator 2026-07-02): the server processes ONE item-cell
    /// pick at a time — the driver polls this and fires ONE <see cref="pickup"/> when true
    /// (pick→ack→pick→ack, never a burst). Cleared by firing a pick; set again by its PICK_ACK
    /// (with a 2s staleness escape for a lost ack).</summary>
    public bool canPick() => View?.CanPick ?? false;
    public bool clickNpc(int handle) => Ok(Wait(_mgr.ClickNpcAsync(Id, (ushort)handle)));
    public bool answerQuest(int result = 1) => Ok(Wait(_mgr.ProceedQuestAsync(Id, (uint)result)));
    /// <summary>Drive a whole quest dialogue with one NPC (accept or turn-in): click it and
    /// ACK every server-pushed script page until the dialogue ends. <c>result</c>=1 accepts.</summary>
    public bool doQuest(int npcHandle, int result = 1, int rewardIndex = -1, int questId = 0) => Ok(Wait(_mgr.DriveQuestDialogueAsync(Id, (ushort)npcHandle, (uint)result, rewardIndex, questId: (ushort)questId)));
    public bool selectReward(int questId, int index) => Ok(Wait(_mgr.SelectQuestRewardAsync(Id, (ushort)questId, (uint)index)));

    /// <summary>The character's ClassName ClassID (1=Fighter, 6=Cleric, …). 0 until selected.</summary>
    public int classId() => _handle.Class;

    /// <summary>The choice-reward index (0-based among a quest's method-2 "choose one" rewards) to
    /// pick for THIS character's class, cross-referencing each reward item's UseClass against the
    /// char's class line. Returns -1 if the quest has no choice rewards (nothing to select). When
    /// the choices are explicitly other-class gear and the char's own slot carries a placeholder
    /// item id 0 (a data quirk seen on the Roumen starter quests), that placeholder index is
    /// chosen. Falls back to index 0. Lets the turn-in grab the right class's reward.</summary>
    public int bestRewardIndex(int questId)
    {
        var q = _mgr.ClientData?.Quest(questId); var cd = _mgr.ClientData;
        if (q is null || cd is null) return -1;
        var choices = q.Rewards.Where(r => r.Method == 2 && r.Type == 2).ToList();
        if (choices.Count == 0) return -1;
        var line = GameData.ClientData.UseClassLineFor(_handle.Class);
        // Return the RAW reward-block slot (RawIndex) — that's what the server's reward-select
        // packet expects, not the compacted position.
        int placeholder = -1;
        for (int i = 0; i < choices.Count; i++)
        {
            var uc = cd.ItemUseClass(choices[i].ItemId);
            if (uc == 1 || line.Contains(uc)) return choices[i].RawIndex;        // gear for our class line
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

    /// <summary>Quest definition from QuestData.shn (nil if unknown): startNpc, turnInNpc,
    /// minLevel/maxLevel, class, linkedQuest (LINK chain), plus mobs/items/rewards arrays and
    /// the start/action/finish scripts. Lets a quest script drive the chain data-driven.</summary>
    public DynValue quest(int id)
    {
        var q = _mgr.ClientData?.Quest(id);
        if (q is null) return DynValue.Nil;
        var t = NewTable();
        t["id"] = q.Id; t["startNpc"] = q.StartNpc; t["turnInNpc"] = q.TurnInNpc;
        t["minLevel"] = q.MinLevel; t["maxLevel"] = q.MaxLevel; t["isNeedLevel"] = q.IsNeedLevel;
        // EndCondition "reach Level N to COMPLETE" gate (distinct from the accept-level window above) —
        // e.g. q20001 reach-20: endNeedsLevel=true, endLevel=20. Gate hand-in on bot.level() >= endLevel.
        t["endNeedsLevel"] = q.EndNeedsLevel; t["endLevel"] = q.EndLevel;
        t["class"] = q.Class; t["linkedQuest"] = q.LinkedQuest;
        t["needsNpc"] = q.NeedsNpc; t["needsItem"] = q.NeedsItem; t["needsItemId"] = q.NeedsItemId;
        t["needsClass"] = q.NeedsClass; t["isVisible"] = q.IsVisible;
        t["remoteAcceptable"] = q.IsInstantAccept; t["instantHandIn"] = q.IsInstantHandIn;
        t["region"] = q.Region; t["questType"] = q.QuestType; t["repeatable"] = q.Repeatable;
        t["exp"] = q.ExpReward;                // turn-in EXP reward (Type-0 reward) — drives exp prioritisation
        t["objectiveMob"] = q.ObjectiveMob;   // mobId to grind for this quest (-1 = meeting quest)
        t["startScript"] = q.StartScript; t["actionScript"] = q.ActionScript; t["finishScript"] = q.FinishScript;
        var npcs = NewTable(); int ni = 1;
        foreach (var n in q.Npcs) { npcs[ni++] = n.Id; }
        t["npcs"] = DynValue.NewTable(npcs);
        var objs = NewTable(); int oi = 1;
        foreach (var o in q.Objectives) { var e = NewTable(); e["type"] = o.Type; e["mob"] = o.Mob; e["count"] = o.Count; e["item"] = o.Item; objs[oi++] = DynValue.NewTable(e); }
        t["objectives"] = DynValue.NewTable(objs);
        var rewards = NewTable(); int ri = 1;
        foreach (var r in q.Rewards) { var e = NewTable(); e["method"] = r.Method; e["type"] = r.Type; e["itemId"] = r.ItemId; e["itemCount"] = r.ItemCount; e["amount"] = r.Amount; rewards[ri++] = DynValue.NewTable(e); }
        t["rewards"] = DynValue.NewTable(rewards);
        return DynValue.NewTable(t);
    }

    /// <summary>Resolve a quest dialog/title id to its text (QuestDialog.shn). Empty if unknown.</summary>
    public string questDialog(int id) => _mgr.ClientData?.QuestDialog(id) ?? "";

    /// <summary>True if the character has completed this quest (from the login QUEST_DONE state).</summary>
    public bool questDone(int id) => View?.IsQuestDone(id) ?? false;
    /// <summary>True if the quest is currently in progress (accepted, not yet turned in).</summary>
    public bool questActive(int id) => View?.IsQuestActive(id) ?? false;

    /// <summary>Ids of quests currently in progress (accepted, awaiting objective/turn-in) —
    /// the driver resumes these before accepting new ones.</summary>
    public DynValue activeQuests()
    {
        var t = NewTable(); var v = View;
        if (v is null) return DynValue.NewTable(t);
        int i = 1; foreach (var id in v.ActiveQuests.Keys) t[i++] = id;
        return DynValue.NewTable(t);
    }

    /// <summary>The PLAYER_QUEST_INFO status byte of an active quest (0 if not active). From the
    /// login QUEST_DOING snapshot — NOT live (it lied: read 8 while the quest was 0/5). Treat
    /// <b>8</b> as the glitched/stuck state and <b>6</b> as healthy in-progress; for live progress
    /// use <see cref="questProgress"/>, not this.</summary>
    public int questStatus(int id) => View is { } v && v.ActiveQuests.TryGetValue(id, out var s) ? s : 0;

    /// <summary>Kills the SERVER has credited to this quest this session (from 0x440D). The
    /// authoritative objective-progress count — when it reaches the objective count, turn in.
    /// If the bot lands kills (killsByMe rises) but this stays 0, the quest is stuck → abandon it.</summary>
    public int questProgress(int id) => View?.QuestProgress(id) ?? 0;

    /// <summary>Abandon a quest (NC_QUEST_GIVE_UP_REQ). Used to clear a persistence-glitched
    /// quest (active but stuck at 0 progress) so it can be re-accepted fresh.</summary>
    public bool giveUpQuest(int id) => Ok(Wait(_mgr.GiveUpQuestAsync(Id, (ushort)id)));

    /// <summary>Start a quest by id (NC_QUEST_START_REQ) — the accept for menu/remote quests
    /// where clicking the NPC opens a selection menu instead of a direct dialogue.</summary>
    public bool startQuest(int id) => Ok(Wait(_mgr.StartQuestAsync(Id, (ushort)id)));

    /// <summary>The server's last accept/start result for a quest: <b>0</b> = accepted, <b>&gt;0</b> =
    /// a refusal reason code (from NC_QUEST_START_ACK.err / SELECT_START_ACK.ErrorType / QUEST_ERR),
    /// <b>-1</b> = never attempted. Lets the driver react to WHY an accept failed (level / prereq /
    /// quest-log full / wrong dialogue) and stop churning, instead of inferring from questActive not
    /// flipping. Codes are mapped to meanings from the live churn — see PROJECT_PLAN.md.</summary>
    public int questAcceptErr(int id) => View?.QuestAcceptErr(id) ?? -1;

    /// <summary>Quests the character can accept right now — the server's authoritative
    /// available list from the login QUEST_READ burst (the orange-! set), joined with QuestData
    /// for startNpc/turnIn details. Each: {id, startNpc, turnInNpc, title, inView} where inView
    /// = the start NPC is currently spawned near the bot (event quests' NPCs often aren't).
    /// Refreshed on relog (the server doesn't push READ mid-session).</summary>
    public DynValue availableQuests()
    {
        var t = NewTable();
        var v = View; var cd = _mgr.ClientData;
        if (v is null) return DynValue.NewTable(t);
        var npcsInView = new HashSet<int>();
        foreach (var n in v.NearbyNpcs) npcsInView.Add(n.MobId);
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
    /// <summary>Quests the bot should consider accepting, driven from QuestData.shn directly
    /// (NOT the server's QUEST_READ list — in these data files the low-level quests are buggily
    /// absent from it). A quest is eligible when: not already done, not already active, it has at
    /// least one kill objective (type==1), and it has a real start NPC. Class is not filtered
    /// (≈99% of quests are unrestricted). Level isn't hard-gated either — the server rejects an
    /// out-of-level accept, which the driver detects via <c>questActive</c> not flipping. Each
    /// row: {id, startNpc, turnInNpc, minLevel, repeatable, title, objectives[{mob,count}]}.
    /// The driver bulk-accepts these at their start NPCs in town.</summary>
    /// <summary>Maps a Fiesta class id to its base/first-job class (the LINE leader): Fighter(1-5)→1,
    /// Cleric(6-10)→6, Archer(11-15)→11, Mage(16-20)→16, Joker(21+)→21. Quest Class@63 is a base id;
    /// matching by line lets an advanced char (e.g. Warrior 3) still take its line's base quests.</summary>
    private static int ClassLine(int c) => c > 0 ? ((c - 1) / 5) * 5 + 1 : 0;

    public DynValue eligibleQuests()
    {
        var t = NewTable();
        var v = View; var cd = _mgr.ClientData;
        if (v is null || cd is null) return DynValue.NewTable(t);
        int i = 1;
        foreach (var q in cd.Quests.Values)
        {
            // ACCEPT GATE = the StartCondition Needs* flags, NOT a bare StartNpc. A quest is
            // NPC-startable only when NeedsNpc && NPCID set; quests with NeedsNpc=0 (e.g. RouN's
            // "Cursed Doll" from Pey) are NOT acceptable by clicking — the server rejects them
            // (SELECT_START err 2887). NeedsItem=1 = a "hidden"/trigger-item quest, also not
            // plain-NPC-startable. (Validated against QuestData.shn + live wire, 2026-06-24.)
            if (!q.NeedsNpc || q.StartNpc == 0 || q.NeedsItem) continue;
            // CLASS GATE (@62 NeedsClass / @63 Class): the starter quests come in one copy PER CLASS
            // (q1 "Baby Steps" Fighter / q944 Cleric / q945 Archer / q946 Mage) — a Fighter must only
            // see the Fighter copy, else it loops clicking the giver for a quest it can't get. Match by
            // class LINE (Fighter 1-5, Cleric 6-10, Archer 11-15, Mage 16-20, Joker 21+) so advanced
            // classes still match their base quest. q.Class 0 = any class.
            if (q.NeedsClass && q.Class != 0 && _handle.Class != 0 && ClassLine(q.Class) != ClassLine(_handle.Class)) continue;
            if (v.IsQuestDone(q.Id) || v.IsQuestActive(q.Id)) continue;
            // Accept ALL NPC-startable, level-appropriate quests: kill (Type 1), item-collect (Type 2),
            // find/visit (Type 3) AND 0-objective story quests (the Roumen newbie chain q1→q2→q3, which
            // instant-complete at the turn-in NPC). Earlier this required a kill objective, so the
            // leveler skipped the newbie chain + item-pickup quests and went straight to grind.
            // LEVEL WINDOW only when the quest is level-gated (NeedsLevel@26).
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
            // remoteAcceptable = can be accepted from the quest log without walking (0x4414 START_REQ).
            // A separate client-side level-floor (~lvl 10–20) also applies — the driver ANDs that in.
            e["remoteAcceptable"] = q.IsInstantAccept; e["instantHandIn"] = q.IsInstantHandIn;
            var objs = NewTable(); int oi = 1;
            foreach (var o in q.Objectives)
            { var oe = NewTable(); oe["type"] = o.Type; oe["mob"] = o.Mob; oe["count"] = o.Count; oe["item"] = o.Item; objs[oi++] = DynValue.NewTable(oe); }
            e["objectives"] = DynValue.NewTable(objs);
            t[i++] = DynValue.NewTable(e);
        }
        return DynValue.NewTable(t);
    }

    /// <summary>Where a mob type spawns, from client <c>MobCoordinate.shn</c> (the table the
    /// real client uses for the quest-log marker): {map, x, y, width, height}, or nil if unknown.
    /// Lets the driver travel to the right field for a kill objective with no server data.</summary>
    /// <summary>The mob's level from client MobInfo.shn, or -1 if unknown. Used by the leveler to pick a
    /// SURVIVABLE grind target (avoid mobs at/above the char's level whose dense spawns swarm-kill it) and to
    /// prefer the highest-value mob it can safely handle.</summary>
    public int mobLevel(int mobId) => _mgr.ClientData?.Mob(mobId)?.Level ?? -1;

    public DynValue mobLocation(int mobId)
    {
        var cd = _mgr.ClientData;
        if (cd is null) return DynValue.Nil;
        var all = cd.MobCoordinatesAll(mobId);
        // Drop zero-area rows: those are quest-log MARKERS (e.g. the gate to go through), not real
        // spawn fields. Keeping them made "prefer current map" pick the RouN gate-marker and freeze.
        var nonZero = all.Where(l => (long)l.Width * l.Height > 0).ToList();
        if (nonZero.Count > 0) all = nonZero;
        if (all.Count == 0) return DynValue.Nil;
        // Prefer (1) the current map (hunt here if the mob spawns here), else (2) the LARGEST patch
        // overall — deterministic / position-INDEPENDENT. The old middle tier ("a map reachable via an
        // IN-VIEW gate") flipped as the bot moved (different gates in view on each map), so a mob with
        // spawns on two maps had its target map ping-pong every tick → the RouCos01↔RouN↔RouVal01
        // map-thrash (exp frozen). A stable pick (+ the Lua travel commitment) fixes that; the largest
        // patch is the real spawn field (quest-log markers are tiny and already dropped above).
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
    /// <summary>True once an HP soul-stone USE failed (reserve empty / on cooldown) — gate
    /// <see cref="soulstoneHp"/> on <c>not bot.hpStoneDepleted()</c> so the bot stops spamming
    /// the use on an empty reserve and goes to a healer to restock instead. Cleared by a
    /// successful use or an HP-stone buy.</summary>
    public bool hpStoneDepleted() => View?.HpStoneDepleted ?? false;
    /// <summary>Current HP soul-stone reserve count, or -1 if unknown (no buy/use seen yet).
    /// Decrements on a successful use, refilled on a buy ack.</summary>
    public int hpStones() => View?.HpStones ?? -1;
    /// <summary>Max HP soul-stone reserve capacity (from the [1802] param block), or 0 if not
    /// seeded. Lets a script restock at a percentage of capacity (e.g. &lt;10%).</summary>
    public int maxHpStones() => (int)(View?.MaxHpStones ?? 0);
    /// <summary>Unit price (cen) of one HP soul-stone charge, from the healer's soul-stone
    /// shop-open (0x3C05). 0 until a soul-stone shop has been opened this session. Buy the MAX
    /// AFFORDABLE: <c>min(deficit, money/hpStonePrice)</c> — never ask for more than you can pay
    /// (the server silently rejects an unaffordable buy with no ack).</summary>
    public int hpStonePrice() => (int)(View?.HpStonePrice ?? 0);
    /// <summary>Unit price (cen) of one SP soul-stone charge (0x3C05 shop-open). 0 until opened.</summary>
    public int spStonePrice() => (int)(View?.SpStonePrice ?? 0);
    /// <summary>SP analogue of <see cref="hpStoneDepleted"/> — gate <see cref="soulstoneSp"/> on
    /// <c>not bot.spStoneDepleted()</c>. USEFAIL (0x5006) is attributed HP vs SP by correlating to
    /// the USE the bot actually fired (it carries no marker on the wire).</summary>
    public bool spStoneDepleted() => View?.SpStoneDepleted ?? false;
    /// <summary>Current SP soul-stone reserve count, or -1 if unknown. Seeded at zone-enter
    /// (NC_CHAR_BASE CurSPStone@40), refilled by SP BUY ack (0x5004), decrements on SP USESUC.</summary>
    public int spStones() => View?.SpStones ?? -1;
    /// <summary>Max SP soul-stone reserve capacity, or 0 if not seeded.</summary>
    public int maxSpStones() => (int)(View?.MaxSpStones ?? 0);
    /// <summary>Monotonic count of soul-stone BUY failures (0x5005 NC_SOULSTONE_BUYFAIL_ACK). Record
    /// it before firing a buy — if it increased while the BUY_ACK never came, THAT buy was refused
    /// (e.g. err 0x0742 = count would exceed the max reserve); recompute instead of re-firing.</summary>
    public int stoneBuyFailCount() => View?.StoneBuyFailCount ?? 0;
    /// <summary>Error code of the last soul-stone BUY failure (0x5005), 0 if none seen.</summary>
    public int lastStoneBuyFailErr() => View?.LastStoneBuyFailErr ?? 0;
    public bool dead() => View?.Dead ?? false;
    public bool inCombat() => View?.InCombat ?? false;
    /// <summary>True if EITHER we were hit OR we landed a hit within the last <paramref name="withinMs"/>
    /// ms (default 15000) — unlike <see cref="inCombat"/> (us being hit only), this also covers a mob
    /// that's genuinely taking damage from us but never retaliates (weak/passive, or a facing bug false
    /// negative). Operator 2026-07-01: the "give up, this mob is un-killable" guard must check damage
    /// dealt OR received, not just received — a target we're actually damaging is never un-killable.</summary>
    public bool recentDamage(int withinMs = 15000)
    {
        var v = View; if (v is null) return false;
        var now = DateTime.UtcNow;
        return (now - v.LastHitAtUtc).TotalMilliseconds < withinMs
            || (now - v.LastDamageDealtAtUtc).TotalMilliseconds < withinMs;
    }
    /// <summary>True if WE landed a CONNECTING hit (Damage&gt;0, not a whiff/out-of-range) within the last
    /// <paramref name="withinMs"/> ms. Lets the kite-chip logic confirm a damage skill actually connected
    /// (operator 2026-07-07: "check it didn't miss via packets") — a miss/out-of-range leaves this false.</summary>
    public bool damageDealt(int withinMs = 3000)
    {
        var v = View; if (v is null) return false;
        return v.LastRealDamageDealtAtUtc > DateTime.MinValue
            && (DateTime.UtcNow - v.LastRealDamageDealtAtUtc).TotalMilliseconds < withinMs;
    }
    /// <summary>Count of mobs the bot itself landed the killing blow on (REALLYKILL attacker==self).
    /// The real kill signal for quest/XP credit — a mob merely vanishing (despawn / another
    /// player's kill) does NOT count. A grind loop credits a kill when this increments.</summary>
    public int killsByMe() => View?.KillsByMe ?? 0;
    public bool respawn() => Ok(Wait(_mgr.RespawnAsync(Id)));
    public bool buyHpStone(int number = 1) => Ok(Wait(_mgr.BuyHpStoneAsync(Id, (ushort)number)));
    public bool buySpStone(int number = 1) => Ok(Wait(_mgr.BuySpStoneAsync(Id, (ushort)number)));
    /// <summary>Open an NPC's shop SYNCHRONOUSLY and return the OUTCOME (operator 2026-06-30: no recency
    /// window). Blocks a few seconds for a definitive reply, then returns: "weapon"/"skill"/"item"/
    /// "soulstone" if a real shop opened, "randomoption" if the NPC is a non-shop RandomOption menu (the
    /// Anvil — closed for you), or "none" on timeout/untracked. The driver classifies the NPC from THIS
    /// return, not a time window. (bot.shopOpen()/shopKind() also reflect the result.)</summary>
    public string openShop(int npcHandle, int menuOption = 1)
    {
        Wait(_mgr.OpenShopAsync(Id, (ushort)npcHandle, (byte)menuOption));
        var v = View; if (v is null) return "none";
        if (v.ShopOpen) return shopKind();
        if (v.RandomOptionUtc > DateTime.MinValue) return "randomoption";
        if (v.PendingQuest != null) return "quest";  // dual-role NPC: click opened quest dialogue, not a shop
        return "none";
    }

    /// <summary>True if the last openShop() got a RandomOption menu (0x3C0E, e.g. the Anvil) rather than a
    /// shop — i.e. the NPC is definitively NOT a merchant. Lets noteShop reclassify it as notshop.</summary>
    public bool lastOpenWasRandomOption() => (View?.RandomOptionUtc ?? DateTime.MinValue) > DateTime.MinValue;
    public bool buy(int itemId, int lot = 1) => Ok(Wait(_mgr.BuyAsync(Id, (ushort)itemId, (uint)lot)));
    public bool sell(int slot, int lot = 1) => Ok(Wait(_mgr.SellAsync(Id, (byte)slot, (uint)lot)));
    public bool enchant(int equip, int raw, int rawLeft = 255, int rawMiddle = 255, int rawRight = 255, int money = 0)
        => Ok(Wait(_mgr.EnchantAsync(Id, (byte)equip, (byte)raw, (byte)rawLeft, (byte)rawMiddle, (byte)rawRight, (uint)money)));
    public bool target(int handle) => Ok(Wait(_mgr.TargetAsync(Id, (ushort)handle)));
    public bool untarget() => Ok(Wait(_mgr.UntargetAsync(Id)));
    public bool walk(double fx, double fy, double tx, double ty) => Ok(Wait(_mgr.WalkAsync(Id, (uint)fx, (uint)fy, (uint)tx, (uint)ty)));
    public bool travelTo(string map) => _mgr.TravelTo(Id, map).Result == BotManager.TravelResult.Started;
    public bool stopTravel() => Ok(_mgr.StopTravel(Id));
    public bool follow(string name) => Ok(_mgr.Follow(Id, name));
    public bool stopFollow() => Ok(_mgr.StopFollow(Id));
    public bool useGate(int handle) => Ok(Wait(_mgr.UseGateAsync(Id, (ushort)handle)));
    public bool townPortal(int npcHandle, int dest) => Ok(Wait(_mgr.TownPortalAsync(Id, (ushort)npcHandle, (byte)dest)));

    /// <summary>Issue a GM command (prepends '&amp;' if no prefix), e.g. <c>bot.gm("levelup 1")</c>.</summary>
    public bool gm(string command)
    {
        var c = command.Trim();
        if (c.Length > 0 && c[0] != '&' && c[0] != '$') c = "&" + c;
        return Ok(Wait(_mgr.GmAsync(Id, c)));
    }

    /// <summary>Pathfind over the current map's block grid and walk to (x,y). Returns
    /// false if no grid / position / path. Uses the bot's tracked position as the start
    /// and its current map — same machinery as the <c>/walkto</c> endpoint.</summary>
    public bool walkTo(double x, double y)
    {
        if (_handle.CurrentMap is not { } map) return false;
        if (_mgr.GridProvider?.Invoke(map) is not { } grid) return false;
        if (_handle.Position is not { } pos) return false;
        var path = PathFinder.FindPath(grid, pos.X, pos.Y, (uint)x, (uint)y);
        if (path.Count == 0) return false;
        return Ok(_mgr.WalkPath(Id, PathFinder.Simplify(path)));
    }

    public bool partyInvite(string name) => Ok(Wait(_mgr.PartyInviteAsync(Id, name)));
    public bool partyAccept(string name = null) => Ok(Wait(_mgr.PartyAcceptAsync(Id, name)));
    public bool partyDecline(string name = null) => Ok(Wait(_mgr.PartyDeclineAsync(Id, name)));
    public string pendingInvite() => _handle.PendingPartyInviter ?? "";
    public bool partyChat(string text) => Ok(Wait(_mgr.PartyChatAsync(Id, text)));
    public bool friendAdd(string name) => Ok(Wait(_mgr.FriendAddAsync(Id, name)));
    public bool friendConfirm(string name, bool accept) => Ok(Wait(_mgr.FriendConfirmAsync(Id, name, accept)));
    public bool friendDelete(string name) => Ok(Wait(_mgr.FriendDeleteAsync(Id, name)));
    /// <summary>Name of a pending incoming friend request (someone added the bot), or "" if none.</summary>
    public string pendingFriend() => _handle.PendingFriendRequester ?? "";
    /// <summary>Accept the pending incoming friend request (no-op if none). Lets a social
    /// script auto-confirm so an operator can friend the bot and watch it.</summary>
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

    /// <summary>Current HP as a 0–100 percentage of max, or -1 if HP/max isn't known yet.
    /// The usual "heal / HP-stone when low" gate: <c>if bot.hpPct() &lt; 40 then ...</c>.</summary>
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

    /// <summary>How many mobs are currently aggroing the bot (combat window) — the
    /// "am I overwhelmed?" signal for a flee transition.</summary>
    public int aggressors() => _mgr.AggressorCount(Id);

    /// <summary>Flee: walk away from the threat by <paramref name="dist"/> units. NON-BLOCKING
    /// (returns immediately), so a survival tick can keep healing while retreating.</summary>
    public bool flee(double dist = 500) => Ok(_mgr.Flee(Id, dist));

    public double? x() => _handle.Position?.X;
    public double? y() => _handle.Position?.Y;
    public string? map() => _handle.CurrentMap;
    public int? selfHandle() => _handle.SelfHandle;
    public bool mounted() => View?.IsMounted ?? false;
    public double walkSpeed() => _handle.WalkSpeed;
    public int level() => (int)_handle.Level;
    public string phase() => _handle.Phase.ToString();
    public bool inZone() => _handle.Phase == BotPhase.InZone;

    /// <summary>A monotonic millisecond clock for script-side cooldowns
    /// (<c>if bot.now() - last > 3000 then ...</c>).</summary>
    public double now() => Environment.TickCount64;

    /// <summary>Headline log (Note): quest accept/finish, level-up, death, purchase, errors.</summary>
    public void log(string message) => _handle.Log(BotLogLevel.Note, $"[lua] {message}");
    /// <summary>Progress log (Info): each kill, quest-objective credit, restock/travel choices.</summary>
    public void logi(string message) => _handle.Log(BotLogLevel.Info, $"[lua] {message}");
    /// <summary>Firehose log (Verbose): per-tick move/cast/auto-attack + the state dump.</summary>
    public void logv(string message) => _handle.Log(BotLogLevel.Verbose, $"[lua] {message}");

    // ── perception (tables) ───────────────────────────────────────────────────
    /// <summary>Zone handle of the nearest non-gate mob/NPC in view, or nil.</summary>
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

    /// <summary>Resolve a mob/NPC id (e.g. a quest's startNpc/turnInNpc) to a live entity in
    /// view: {handle, x, y, dist}, or nil if not currently spawned near the bot. Lets the quest
    /// driver turn QuestData ids into something it can walkTo + doQuest.</summary>
    /// <summary>✅ {x, y, dist, isGate, linkMap} of an NPC/gate by mobId from the AUTHORITATIVE map-enter
    /// SEED (the bulk 0x1C09 broadcast at infinite range — ALL the map's NPCs+gates). The source of truth
    /// for "where is NPC X on THIS map" — walkTo any quest giver / merchant / gate WITHOUT hardcoded coords
    /// and WITHOUT having seen it. For a gate, <c>isGate=true</c> and <c>linkMap</c> = where it leads. nil
    /// if the seed has no such NPC on the current map (it's on another map — don't use stale coords).</summary>
    public DynValue npcLocation(int mobId)
    {
        if (View?.Npc(mobId) is not { } e) return DynValue.Nil;
        var t = NewTable(); t["x"] = e.X; t["y"] = e.Y; t["isGate"] = e.IsGate; t["linkMap"] = e.LinkMap;
        if (_handle.Position is { } p) t["dist"] = Math.Sqrt(Sq((double)e.X - p.X) + Sq((double)e.Y - p.Y));
        return DynValue.NewTable(t);
    }

    /// <summary>A quest/turn-in NPC's canonical location from client <c>MobCoordinate.shn</c>
    /// ({map,x,y}), or nil if the NPC isn't in the table. Unlike <see cref="mobLocation"/> this KEEPS
    /// zero-area POINT rows — a stationary NPC is a point, not a spawn field — so a cross-map hand-in can
    /// resolve the right map AND walk to the NPC even when it was never roam-learned and isn't in the
    /// current zone seed. Prefers the current map if the NPC is listed there. Client data (the same table
    /// the quest-log marker uses) — no server files, no hardcoding. Operator 2026-07-07.</summary>
    public DynValue npcCoord(int npcId)
    {
        var loc = _mgr.ClientData?.MobCoordinate(npcId, _handle.CurrentMap);
        if (loc is null) return DynValue.Nil;
        var t = NewTable(); t["map"] = loc.Map; t["x"] = loc.CenterX; t["y"] = loc.CenterY;
        return DynValue.NewTable(t);
    }
    /// <summary>Count of NPCs in the current map's seed roster.</summary>
    public int npcSeedCount() => View?.NpcSeedCount ?? 0;

    /// <summary>The full map-enter NPC SEED roster as a lua array of {mobId, x, y, isGate, linkMap, dist}
    /// — every NPC+gate on the current map (authoritative). Iterate it to DISCOVER/visit services
    /// (skill master / Smith / healer) by walking to their seed coords, instead of only probing the few
    /// NPCs currently in view. dist is from the bot's position (nil if unknown).</summary>
    public DynValue npcSeedList()
    {
        var v = View; var arr = NewTable();
        if (v is not null)
        {
            int i = 1;
            foreach (var e in v.NpcSeed)
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
            if (n.MobId != mobId) continue;
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
            // Huntable = a real monster (not a guard / shop NPC / quest giver / resource node). Lets the
            // field-grind avoid mistargeting a town NPC (it would walk into it forever). Unknown → true.
            row["isHuntable"] = v.IsHuntableMob?.Invoke((ushort)n.MobId) ?? true;
            if (pos is { } p) row["dist"] = Math.Sqrt(Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y));
            t[i++] = row;
        }
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

    /// <summary>Items on the ground in view (rows: handle, itemId, x, y, dropMob, dist).
    /// Loot a kill with <c>bot.loot()</c> (nearest) or <c>bot.loot(handle)</c>.</summary>
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

    /// <summary>Handle of the ground drop nearest the bot, or nil if nothing's on the ground.</summary>
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

    public DynValue equipment()
    {
        var t = NewTable();
        var eq = View?.Equipment; if (eq is null) return DynValue.NewTable(t);
        foreach (var (slot, itemId) in eq) t[(int)slot] = itemId;
        return DynValue.NewTable(t);
    }

    /// <summary>Resolve a nearby player by name (case-insensitive) to a row table, or nil.</summary>
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
        return DynValue.NewTable(row);
    }

    private Table NewTable() => new(_lua);
    private static double Sq(double a) => a * a;
}
