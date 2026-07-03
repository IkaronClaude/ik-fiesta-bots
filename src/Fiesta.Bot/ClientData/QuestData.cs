using System.Text;

namespace Fiesta.Bot.GameData;

/// <summary>
/// Parses <c>QuestData.shn</c> — a bespoke, NOT-encrypted, little-endian quest-definition format
/// (the normal column-SHN parser throws on it; strings are EUC-KR). Layout VALIDATED 2026-06-24
/// against the authoritative C# type defs in gherblino's Fiesta-Online-Tools (cloned at
/// <c>Z:/Fiesta-Online-Tools</c>; see its <c>INDEX-claude.md</c>) — the record is a sequence of
/// nested structs, NOT a flat field bag. Cross-checked on q1/q8/q12/q21/q415/q392.
///
/// File: [0:2] u16 marker(6) · [2:4] u16 questCount · then questCount records, each:
///   +0  u32 dataLen (whole record; next = off+dataLen)
///   +4  u32 QuestID · +8 u32 TitleID · +12 u32 DescriptionID (all QuestDialog.shn ids)
///   +16 u8 Region · +17 u8 QuestType · +18 IsRepeatable · +19 IsDailyQuest · +20 DailyType
///   +21 StartCondition (67B, the ACCEPT GATE):
///       +24 IsVisible(=shows in the available quest LOG; 0 = hidden but still completable) ·
///       +25 IsInstantAccept(=remote accept via quest log) · +26 NeedsLevel
///       +27 LevelMin · +28 LevelMax · +29 NeedsNPC · +30 u16 NPCID(giver) · +32 NeedsItem
///       +33 u16 ItemID · +56 NeedsPreviousQuest · +58 u16 PreviousQuestID · +62 NeedsClass · +63 Class
///   +88 EndCondition (104B, turn-in gate + OBJECTIVES):
///       +88 IsInstantHandIn · +92 NPCMobList[5] stride 8 (need,_,u16 mobId,action,count,target,_;
///           action 0=turn-in NPC ref, 1=Kill, 2=Find, 3=Talk) · +132 ItemList[5] stride 6
///           (need,itemType,u16 itemId,u16 lot) = item-collect objectives
///   +192 i32 NumOfActions · +196 Action[10] (32B each) · +516 Reward[12] (12B each)
///   +660 u16 StartScriptLen · +662 u16 EndScriptLen · +664 u16 DoingScriptLen · +666 14B Unk1
///   +680 scripts in DATA order Start, Doing(=Action), End(=Finish) (EUC-KR, NUL-terminated)
///
/// Eligibility is the StartCondition <c>Needs*</c> gates, NOT a bare StartNPC: a quest is
/// NPC-startable only when <c>NeedsNPC &amp;&amp; NPCID==npc</c>; <c>IsInstantAccept</c> means it can
/// also be accepted remotely from the quest log (0x4414 START_REQ, no walking). See [[questdata-shn-format]].
/// </summary>
public static class QuestData
{
    private const int Fixed = 680;
    private const int StartCond = 21;  // StartCondition base
    private const int EndCond = 88;     // EndCondition base
    private static readonly Encoding EucKr;

    static QuestData()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EucKr = Encoding.GetEncoding(949);
    }

    public static IReadOnlyDictionary<int, QuestDef> Load(string path)
    {
        var map = new Dictionary<int, QuestDef>();
        if (!File.Exists(path)) return map;
        byte[] b = File.ReadAllBytes(path);

        ushort U16(int o) => (ushort)(b[o] | (b[o + 1] << 8));
        uint U32(int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        bool Flag(int o) => b[o] != 0;

        if (b.Length < 4) return map;
        ushort count = U16(2);
        int off = 4;
        for (int qi = 0; qi < count; qi++)
        {
            if (off + Fixed > b.Length) break;
            uint dataLen = U32(off);
            if (dataLen < Fixed || off + dataLen > b.Length) break;

            ushort id = U16(off + 4);
            int title = (int)U32(off + 8);   // QuestDialog id of the title (DescriptionID is @12)

            // --- StartCondition (the accept gate) ---
            bool isVisible = Flag(off + StartCond + 3);          // @24 = shows in available quest LOG
            bool isInstantAccept = Flag(off + StartCond + 4);    // @25 = remote-accept via quest log
            bool needsLevel = Flag(off + StartCond + 5);         // @26
            int minLevel = b[off + StartCond + 6];               // @27
            int maxLevel = b[off + StartCond + 7];               // @28
            bool needsNpc = Flag(off + StartCond + 8);           // @29
            ushort startNpc = U16(off + StartCond + 9);          // @30 (giver mobId)
            bool needsItem = Flag(off + StartCond + 11);         // @32
            int needsItemId = U16(off + StartCond + 12);         // @33 (trigger item — "hidden quest")
            bool needsPrereq = Flag(off + StartCond + 35);       // @56
            int prereqQuest = needsPrereq ? U16(off + StartCond + 37) : 0; // @58
            bool needsClass = Flag(off + StartCond + 41);        // @62
            int reqClass = b[off + StartCond + 42];              // @63

            // --- EndCondition (turn-in gate + objectives) ---
            bool isInstantHandIn = Flag(off + EndCond);          // @88
            // EndCondition level gate (gherblino EndCondition.cs: NeedsLevel@+1, Level@+2, then Unk6@+3,
            // then NPCMobList@+4=@92). This is the "reach Level N to COMPLETE" gate (distinct from the
            // StartCondition accept-level window @27/@28) — e.g. q20001 "reach Lv.20": EndNeedsLevel=true,
            // EndLevel=20. Without it a 0-objective reach-level quest false-reads as instantly hand-in-ready.
            bool endNeedsLevel = Flag(off + EndCond + 1);        // @89
            int endLevel = b[off + EndCond + 2];                 // @90
            // NPCMobList[5] @92 stride 8: need(1) _(1) mobId(u16) action(1) count(1) target(1) _(1).
            // action 0 = the turn-in/reward NPC reference; 1 = Kill, 2 = Find, 3 = Talk.
            var npcs = new List<QuestTarget>();
            var objectives = new List<QuestObjective>();
            for (int m = 0; m < 5; m++)
            {
                int o = off + EndCond + 4 + m * 8;   // = off+92 + m*8
                if (b[o] == 0 && U16(o + 2) == 0) continue;
                int mobId = U16(o + 2); byte action = b[o + 4]; int cnt = b[o + 5];
                if (action == 0)           // RewardObject = the turn-in NPC
                    npcs.Add(new QuestTarget(true, mobId, false, 0));
                else if (action == 1)      // Kill
                    objectives.Add(new QuestObjective(1, mobId, cnt, 0));
                else if (action == 2 || action == 3) // Find / Talk (visit a mob/NPC)
                    objectives.Add(new QuestObjective(3, mobId, cnt, 0));
            }
            // Actions @196 (i32 NumOfActions @192, then Action[10] stride 32) carry the DROP-SOURCE
            // map: which mob drops a collect item. The dropping mob is NOT in the ItemList objective
            // (mob=0) — it's an Action "IF MobKill(IfTarget) THEN DropItem(ThenTarget) @ThenPercent".
            // (gherblino Action.cs: +0 IfType +4 IfTarget +8 ThenType +12 ThenTarget; TypeIf.MobKill=1,
            // TypeThen.DropItem=1.) Validated: q14 mob3->item3079, q316 mob305(Blue Crab)->item3240
            // (Blue Crab Meat). This is the SAME mob<=>item link the client's hover box uses, fully
            // client-side — so a collect quest can target the right mob without any server data.
            var dropMobForItem = new Dictionary<int, int>();
            int numActions = (int)U32(off + 192);
            for (int a = 0; a < numActions && a < 10; a++)
            {
                int o = off + 196 + a * 32;
                if (o + 32 > off + dataLen) break;
                int ifType = (int)U32(o), ifTarget = (int)U32(o + 4);
                int thenType = (int)U32(o + 8), thenItem = (int)U32(o + 12);
                if (ifType == 1 && thenType == 1 && ifTarget != 0 && thenItem != 0)
                    dropMobForItem[thenItem] = ifTarget;   // item -> mob that drops it (first wins)
            }
            // ItemList[5] @132 stride 6: need(1) itemType(1) itemId(u16) lot(u16) — item-collect goals.
            for (int it = 0; it < 5; it++)
            {
                int o = off + EndCond + 44 + it * 6;  // = off+132 + it*6
                if (b[o] == 0 && U16(o + 2) == 0) continue;
                int itemId = U16(o + 2); int lot = U16(o + 4);
                // Attach the dropping mob from the Action map so the collect objective knows what to kill.
                if (itemId != 0) objectives.Add(new QuestObjective(2,
                    dropMobForItem.TryGetValue(itemId, out var dm) ? dm : 0, lot == 0 ? 1 : lot, itemId));
            }

            // --- Rewards @516, stride 12 ---
            var rewards = new List<QuestRewardDef>();
            for (int r = 0; r < 12; r++)
            {
                int o = off + 516 + r * 12;
                byte method = b[o], type = b[o + 1];
                if (method == 0) continue;
                // RawIndex = r: the server's NC_QUEST_REWARD_SELECT_ITEM_INDEX wants THIS slot 0..11
                // (incl. fixed rewards + empty gaps), not the compacted position (verified Quest.pcapng).
                if (type == 2) // Item
                    rewards.Add(new QuestRewardDef(method, type, U16(o + 4), U16(o + 6), 0, r));
                else
                    rewards.Add(new QuestRewardDef(method, type, 0, 0, BitConverter.ToUInt64(b, o + 4), r));
            }

            // --- Scripts: lens @660 (Start,End,Doing), bytes @680 in DATA order Start,Doing,End ---
            int sLen = U16(off + 660), eLen = U16(off + 662), dLen = U16(off + 664);
            int ss = off + Fixed;
            string start = sLen > 0 ? EucKr.GetString(b, ss, sLen).TrimEnd('\0') : "";
            string action2 = dLen > 0 ? EucKr.GetString(b, ss + sLen, dLen).TrimEnd('\0') : "";
            string finish = eLen > 0 ? EucKr.GetString(b, ss + sLen + dLen, eLen).TrimEnd('\0') : "";

            map[id] = new QuestDef(id, title, startNpc, needsLevel, minLevel, maxLevel, reqClass,
                npcs, objectives, rewards, start, action2, finish,
                Repeatable: Flag(off + 18), PrereqQuest: prereqQuest,
                IsVisible: isVisible, IsInstantAccept: isInstantAccept, IsInstantHandIn: isInstantHandIn,
                NeedsNpc: needsNpc, NeedsItem: needsItem, NeedsItemId: needsItemId, NeedsClass: needsClass,
                Region: b[off + 16], QuestType: b[off + 17],
                EndNeedsLevel: endNeedsLevel, EndLevel: endLevel);

            off += (int)dataLen;
        }
        return map;
    }
}

/// <summary>A quest definition decoded from QuestData.shn. <see cref="StartNpc"/> is the mobId of
/// the giver (valid only when <see cref="NeedsNpc"/>). <see cref="Npcs"/> = the turn-in NPC(s)
/// (EndCondition action-0 entries). <see cref="Objectives"/> = kill (Type 1) / item-collect (Type 2)
/// / find (Type 3) goals. Scripts: Start=accept dialogue, Action=in-progress, Finish=turn-in.</summary>
public sealed record QuestDef(
    int Id, int Title, int StartNpc, bool IsNeedLevel, int MinLevel, int MaxLevel, int Class,
    IReadOnlyList<QuestTarget> Npcs, IReadOnlyList<QuestObjective> Objectives,
    IReadOnlyList<QuestRewardDef> Rewards, string StartScript, string ActionScript, string FinishScript,
    bool Repeatable = false, int PrereqQuest = 0,
    bool IsVisible = false, bool IsInstantAccept = false, bool IsInstantHandIn = false,
    bool NeedsNpc = false, bool NeedsItem = false, int NeedsItemId = 0, bool NeedsClass = false,
    int Region = 0, int QuestType = 0, bool EndNeedsLevel = false, int EndLevel = 0)
{
    /// <summary>The npc this quest is turned in at: the first NPC in the turn-in list that isn't the
    /// giver, else the giver (most quests turn in where they started; e.g. q1 lists Julia(29)).</summary>
    public int TurnInNpc
    {
        get
        {
            foreach (var t in Npcs) if (t.Id != 0 && t.Id != StartNpc) return t.Id;
            return StartNpc;
        }
    }

    /// <summary>The mobId to kill for this quest's (first) kill objective, or -1 if none.</summary>
    public int ObjectiveMob
    {
        get { foreach (var o in Objectives) if (o.Type == 1) return o.Mob; return -1; }
    }

    /// <summary>The EXP this quest awards on turn-in — the reward with Type 0 (verified 2026-07-03:
    /// reward Type 0 = EXP, Type 1 = Money, Type 2 = Item; q414 type0=108 matched the live hand-in exp).
    /// Drives exp-based quest prioritisation (do the fattest quests first). 0 if none.</summary>
    public long ExpReward
    {
        get { foreach (var r in Rewards) if (r.Type == 0) return (long)r.Amount; return 0; }
    }

    /// <summary>Can this quest be accepted by clicking/selecting NPC <paramref name="npc"/> with a
    /// char of <paramref name="level"/>? Mirrors the server gate: must be NPC-startable from this NPC,
    /// level window (if gated), prereq done, no trigger-item requirement, class matches. <paramref
    /// name="isDone"/> answers "is quest X completed?"; <paramref name="charClass"/> is the char's
    /// base ClassID (0 = unknown → class gate skipped).</summary>
    public bool AcceptableFromNpc(int npc, int level, Func<int, bool> isDone, int charClass = 0)
        => NeedsNpc && StartNpc == npc
           && (!IsNeedLevel || (level >= MinLevel && level <= MaxLevel))
           && (PrereqQuest == 0 || isDone(PrereqQuest))
           && !NeedsItem                                   // trigger-item quests aren't NPC-startable
           && (!NeedsClass || charClass == 0 || ClassMatches(charClass));

    /// <summary>True if this quest can be accepted remotely from the quest log (no walking to the
    /// giver) — gated by <see cref="IsInstantAccept"/>. NOTE a separate client-side level-floor
    /// (~lvl 10–20) also applies; the caller must AND in that floor.</summary>
    public bool RemoteAcceptable => IsInstantAccept;

    /// <summary>Best-effort class match: the quest's required base class vs the char's base class.
    /// Fiesta class lines (Fighter 1.., Cleric 6.., Archer 11.., Mage 16.., Joker 21..) — a quest
    /// usually keys off the base (first-job) class id, so accept if equal. Conservative: unknown
    /// (0) matches.</summary>
    private bool ClassMatches(int charClass) => Class == 0 || charClass == 0 || Class == charClass;

    /// <summary>The questId this chains to via a <c>LINK n</c> in any script, or 0 if none.</summary>
    public int LinkedQuest
    {
        get
        {
            foreach (var s in new[] { FinishScript, ActionScript, StartScript })
            {
                int i = s.IndexOf("LINK", StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                var rest = s[(i + 4)..].TrimStart();
                int j = 0; while (j < rest.Length && char.IsDigit(rest[j])) j++;
                if (j > 0 && int.TryParse(rest[..j], out var n)) return n;
            }
            return 0;
        }
    }
}

/// <summary>An NPC referenced by a quest (turn-in), from the EndCondition action-0 entry.</summary>
public sealed record QuestTarget(bool IsNpc, int Id, bool ToKill, int Amount);

/// <summary>A quest objective. <see cref="Type"/>: 1 = kill the mob, 2 = collect an item, 3 =
/// find/visit a mob/NPC. <see cref="Mob"/> = the mobId (kill/find), <see cref="Count"/> = how many,
/// <see cref="Item"/> = the item id (collect).</summary>
public sealed record QuestObjective(int Type, int Mob, int Count, int Item);

/// <summary>A quest reward: <see cref="Method"/> (1=Fixed,2=Choice), <see cref="Type"/>
/// (0=EXP,1=Money,2=Item,3=Fame), with item <see cref="ItemId"/>/<see cref="ItemCount"/> for
/// item rewards or <see cref="Amount"/> otherwise.</summary>
public sealed record QuestRewardDef(int Method, int Type, int ItemId, int ItemCount, ulong Amount, int RawIndex = 0);
