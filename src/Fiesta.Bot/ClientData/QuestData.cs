using System.Text;

namespace Fiesta.Bot.GameData;

/// <summary>Parses QuestData.shn — a bespoke, NOT-encrypted, little-endian quest-definition format (the normal column-SHN parser throws on it; strings are EUC-KR)</summary>
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

            // --- StartCondition (the accept gate) --- QUEST_START_CONDITION really begins at @24 — there are 3 PAD BYTES at…
            bool isWaitListView = Flag(off + StartCond + 3);      // @24 visible in the quest list
            bool isWaitListProgress = Flag(off + StartCond + 4);  // @25 REMOTE ACCEPT
            bool needsLevel = Flag(off + StartCond + 5);         // @26
            int minLevel = b[off + StartCond + 6];               // @27
            int maxLevel = b[off + StartCond + 7];               // @28
            bool needsNpc = Flag(off + StartCond + 8);           // @29
            ushort startNpc = U16(off + StartCond + 9);          // @30 (giver mobId)
            bool needsItem = Flag(off + StartCond + 11);         // @32
            // @34 — was @33, an OFF-BY-ONE fixed 2026-08-05 from the CLIENT PDB (Fiesta.pdb) type dump
            int needsItemId = U16(off + StartCond + 13);         // @34 = Start(@24) + 10
            bool needsPrereq = Flag(off + StartCond + 35);       // @56
            int prereqQuest = needsPrereq ? U16(off + StartCond + 37) : 0; // @58
            bool needsClass = Flag(off + StartCond + 41);        // @62
            int reqClass = b[off + StartCond + 42];              // @63

            // --- EndCondition (turn-in gate + objectives)
            bool isInstantHandIn = Flag(off + EndCond);          // @88
            // EndCondition level gate (gherblino EndCondition.cs: NeedsLevel@+1, Level@+2, then Unk6@+3, then NPCMobList@+4=…
            bool endNeedsLevel = Flag(off + EndCond + 1);        // @89
            int endLevel = b[off + EndCond + 2];                 // @90
            // NPCMobList[5] @92 stride 8: need(1) _(1) mobId(u16) action(1) count(1) target(1) _(1)
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
            // Actions @196 (i32 NumOfActions @192, then Action[10] stride 32) carry the DROP-SOURCE map: which mob drops a c…
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
            // ItemList[5] @132 stride 6: need(1) itemType(1) itemId(u16) lot(u16) — item-collect goals
            for (int it = 0; it < 5; it++)
            {
                int o = off + EndCond + 44 + it * 6;  // = off+132 + it*6
                if (b[o] == 0 && U16(o + 2) == 0) continue;
                int itemId = U16(o + 2); int lot = U16(o + 4);
                // Attach the dropping mob from the Action map so the collect objective knows what to kill
                if (itemId != 0) objectives.Add(new QuestObjective(2,
                    dropMobForItem.TryGetValue(itemId, out var dm) ? dm : 0, lot == 0 ? 1 : lot, itemId));
            }

            // --- Rewards @516, stride 12
            var rewards = new List<QuestRewardDef>();
            for (int r = 0; r < 12; r++)
            {
                int o = off + 516 + r * 12;
                byte method = b[o], type = b[o + 1];
                if (method == 0) continue;
                // RawIndex = r: the server's NC_QUEST_REWARD_SELECT_ITEM_INDEX wants THIS slot 0..11 (incl
                if (type == 2) // Item
                    rewards.Add(new QuestRewardDef(method, type, U16(o + 4), U16(o + 6), 0, r));
                else
                    rewards.Add(new QuestRewardDef(method, type, 0, 0, BitConverter.ToUInt64(b, o + 4), r));
            }

            // --- Scripts: lens @660 (Start,End,Doing), bytes @680 in DATA order Start,Doing,End
            int sLen = U16(off + 660), eLen = U16(off + 662), dLen = U16(off + 664);
            int ss = off + Fixed;
            string start = sLen > 0 ? EucKr.GetString(b, ss, sLen).TrimEnd('\0') : "";
            string action2 = dLen > 0 ? EucKr.GetString(b, ss + sLen, dLen).TrimEnd('\0') : "";
            string finish = eLen > 0 ? EucKr.GetString(b, ss + sLen + dLen, eLen).TrimEnd('\0') : "";

            map[id] = new QuestDef(id, title, startNpc, needsLevel, minLevel, maxLevel, reqClass,
                npcs, objectives, rewards, start, action2, finish,
                Repeatable: Flag(off + 18), PrereqQuest: prereqQuest,
                IsWaitListView: isWaitListView, IsWaitListProgress: isWaitListProgress, IsInstantHandIn: isInstantHandIn,
                NeedsNpc: needsNpc, NeedsItem: needsItem, NeedsItemId: needsItemId, NeedsClass: needsClass,
                Region: b[off + 16], QuestType: b[off + 17],
                EndNeedsLevel: endNeedsLevel, EndLevel: endLevel);

            off += (int)dataLen;
        }
        return map;
    }
}

/// <summary>A quest definition decoded from QuestData.shn</summary>
public sealed record QuestDef(
    int Id, int Title, int StartNpc, bool IsNeedLevel, int MinLevel, int MaxLevel, int Class,
    IReadOnlyList<QuestTarget> Npcs, IReadOnlyList<QuestObjective> Objectives,
    IReadOnlyList<QuestRewardDef> Rewards, string StartScript, string ActionScript, string FinishScript,
    bool Repeatable = false, int PrereqQuest = 0,
    bool IsWaitListView = false, bool IsWaitListProgress = false, bool IsInstantHandIn = false,
    bool NeedsNpc = false, bool NeedsItem = false, int NeedsItemId = 0, bool NeedsClass = false,
    int Region = 0, int QuestType = 0, bool EndNeedsLevel = false, int EndLevel = 0)
{
    /// <summary>The npc this quest is turned in at: the first NPC in the turn-in list that isn't the giver, else the giver (mo…</summary>
    public int TurnInNpc
    {
        get
        {
            foreach (var t in Npcs) if (t.Id != 0 && t.Id != StartNpc) return t.Id;
            return StartNpc;
        }
    }

    /// <summary>The mobId to kill for this quest's (first) kill objective, or -1 if none</summary>
    public int ObjectiveMob
    {
        get { foreach (var o in Objectives) if (o.Type == 1) return o.Mob; return -1; }
    }

    /// <summary>The EXP this quest awards on turn-in — the reward with Type 0 (verified 2026-07-03: reward Type 0 = EXP, Type…</summary>
    public long ExpReward
    {
        get { foreach (var r in Rewards) if (r.Type == 0) return (long)r.Amount; return 0; }
    }

    /// <summary>Can this quest be accepted by clicking/selecting NPC with a char of ?</summary>
    public bool AcceptableFromNpc(int npc, int level, Func<int, bool> isDone, int charClass = 0)
        => NeedsNpc && StartNpc == npc
           && (!IsNeedLevel || (level >= MinLevel && level <= MaxLevel))
           && (PrereqQuest == 0 || isDone(PrereqQuest))
           && !NeedsItem                                   // trigger-item quests aren't NPC-startable
           && (!NeedsClass || charClass == 0 || ClassMatches(charClass));

    /// <summary>True if this quest can be accepted remotely from the quest log (no walking to the giver) — gated by (@25)</summary>
    public bool RemoteAcceptable => IsWaitListProgress;

    /// <summary>The quest can be HANDED IN remotely from the quest log, without walking back to the turn-in NPC — END_CONDITIO…</summary>
    public bool RemoteHandIn => IsInstantHandIn;

    /// <summary>Best-effort class match: the quest's required base class vs the char's base class</summary>
    private bool ClassMatches(int charClass) => Class == 0 || charClass == 0 || Class == charClass;

    /// <summary>The dialog text id the START script SAY s</summary>
    public int StartDialogId
    {
        get
        {
            int i = StartScript.IndexOf("SAY", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            var rest = StartScript[(i + 3)..].TrimStart();
            int j = 0; while (j < rest.Length && char.IsDigit(rest[j])) j++;
            return (j > 0 && int.TryParse(rest[..j], out var n)) ? n : 0;
        }
    }

    /// <summary>The questId this chains to via a LINK n in any script, or 0 if none</summary>
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

/// <summary>An NPC referenced by a quest (turn-in), from the EndCondition action-0 entry</summary>
public sealed record QuestTarget(bool IsNpc, int Id, bool ToKill, int Amount);

/// <summary>A quest objective. : 1 = kill the mob, 2 = collect an item, 3 = find/visit a mob/NPC</summary>
public sealed record QuestObjective(int Type, int Mob, int Count, int Item);

/// <summary>A quest reward: (1=Fixed,2=Choice), (0=EXP,1=Money,2=Item,3=Fame), with item / for item rewards or otherwise</summary>
public sealed record QuestRewardDef(int Method, int Type, int ItemId, int ItemCount, ulong Amount, int RawIndex = 0);
