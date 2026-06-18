using System.Text;

namespace Fiesta.Bot.GameData;

/// <summary>
/// Parses <c>QuestData.shn</c> — a bespoke quest-definition format that is NOT a standard
/// column-SHN (the normal SHN parser throws on it). Not encrypted; little-endian; strings
/// are EUC-KR. Layout reverse-engineered + validated against all 2304 live-client quests
/// (script-length sums equal each record size exactly; file consumed to EOF). The same
/// layout is decoded in ik-fiesta-collab's QuestDataProvider.
///
/// File: [0:2] u16 marker · [2:4] u16 questCount · then questCount records, each:
///   +0 u32 dataLen (whole record; next = off+dataLen) · +4 u16 QuestID ·
///   +16 IsNeedLevel · +17 MinLevel · +18 MaxLevel · +30 u16 StartNPC · +51 Class ·
///   +74 Mobs[5] stride 6 (en,isNpc,u16 id,toKill,amount) ·
///   +104 Items[10] stride 6 (en,type,u16 id,u16 amount) ·
///   +516 Rewards[12] stride 12 (method,type,pad2, then Item{u16 id,u16 cnt,pad4} | u64 amount) ·
///   +660 scriptLens (StartLen,FinishLen,ActionLen u16) · +666 14B scriptdata ·
///   +680 scripts in order Start, Action, Finish (EUC-KR, null-terminated).
///
/// The scripts are the quest logic (SAY &lt;dlgId&gt; NPC|ME / IF RESULT==1 GOTO / ACCEPT /
/// DONE / LINK &lt;questId&gt;) and map 1:1 to the wire 0x4401/0x4402 exchange. Dialog text
/// (&lt;dlgId&gt; and the Title/Description indices) lives in QuestDialog.shn (standard SHN).
/// </summary>
public static class QuestData
{
    private const int Fixed = 680;
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

        if (b.Length < 4) return map;
        ushort count = U16(2);
        int off = 4;
        for (int qi = 0; qi < count; qi++)
        {
            if (off + Fixed > b.Length) break;
            uint dataLen = U32(off);
            if (dataLen < Fixed || off + dataLen > b.Length) break;

            ushort id = U16(off + 4);
            ushort title = U16(off + 8);        // QuestDialog id of the quest title/description
            ushort startNpc = U16(off + 30);

            // @74 block = the quest's NPC list (start + turn-in NPCs), NOT kill targets. Each
            // 6-byte entry: enabled, ?, npcId u16, ?, ?. (e.g. Master Zach's entry here is Zach
            // himself = the turn-in NPC.)
            var npcs = new List<QuestTarget>();
            for (int m = 0; m < 5; m++)
            {
                int o = off + 74 + m * 6;
                if (b[o] == 0) continue;
                npcs.Add(new QuestTarget(true, U16(o + 2), false, 0));
            }

            // Objectives block @+102: the kill/collect goals, up to 5 slots, STRIDE 8. Per
            // objective: mob u16 @+0 (the mob to slay / that drops the item), TYPE @+2 (0 =
            // disabled, 1 = mob kill, 2 = item pickup), count @+3, item u16 @+4. Verified against
            // the wire + QuestData.shn: q8 mob 0 (Slime) x5; q12 mob 2002 x1; q4 mob 300 x5;
            // q392 mob 515 x50; and the multi-objective q415 = mob 303 x1 AND mob 304 x2 (the
            // stride-30 bug silently dropped every objective after the first, so multi-goal
            // quests could never be turned in — the server kept serving the in-progress dialogue).
            var objectives = new List<QuestObjective>();
            for (int i = 0; i < 5; i++)
            {
                int o = off + 102 + i * 8;
                if (o + 8 > off + Fixed) break;
                byte type = b[o + 2];
                if (type == 0) continue; // disabled
                objectives.Add(new QuestObjective(type, U16(o), b[o + 3], U16(o + 4)));
            }

            var rewards = new List<QuestRewardDef>();
            for (int r = 0; r < 12; r++)
            {
                int o = off + 516 + r * 12;
                byte method = b[o], type = b[o + 1];
                if (method == 0) continue;
                // RawIndex = r: the raw 0..11 slot. The server's NC_QUEST_REWARD_SELECT_ITEM_INDEX
                // wants THIS slot number (incl. fixed rewards + empty gaps), NOT the compacted
                // position — verified in Quest.pcapng (q8 Fighter choice at r5 → nSelectedItemIndex=5).
                if (type == 2) // Item
                    rewards.Add(new QuestRewardDef(method, type, U16(o + 4), U16(o + 6), 0, r));
                else
                    rewards.Add(new QuestRewardDef(method, type, 0, 0, BitConverter.ToUInt64(b, o + 4), r));
            }

            int sLen = U16(off + 660), fLen = U16(off + 662), aLen = U16(off + 664);
            int ss = off + Fixed;
            string start = sLen > 0 ? EucKr.GetString(b, ss, sLen).TrimEnd('\0') : "";
            string action = aLen > 0 ? EucKr.GetString(b, ss + sLen, aLen).TrimEnd('\0') : "";
            string finish = fLen > 0 ? EucKr.GetString(b, ss + sLen + aLen, fLen).TrimEnd('\0') : "";

            // Prerequisite quest: @56 u16 = prereq count (0/1), @58 u16 = the required quest id
            // (verified: q41 requires q40, q957 requires q956 — the LINK chain's downstream side).
            // The quest can't be accepted until that quest is DONE.
            int prereqQuest = U16(off + 56) > 0 ? U16(off + 58) : 0;
            // LEVEL GATE: MinLevel = byte@27, MaxLevel = byte@28 (CORRECTED 2026-06-18 — was wrongly
            // @17/@18 which read ~0 for everything and never narrowed the eligible set). Verified by
            // zone: q1 "Baby Steps" 1–10, Forest-of-Mist quests 10–21, Burning Rock quests 79–91; the
            // @27 distribution spans 0–124 properly. The quest is offered only while MinLevel ≤ char
            // level ≤ MaxLevel (above MaxLevel it drops from Available but an in-progress one stays).
            // @17 is a different (small 0–10) field; @16 is a 0–150 recommended/area level.
            int minLevel = b[off + 27], maxLevel = b[off + 28];
            // Repeatable flag: not yet pinned to an offset. Defaults false; TODO decode (cross-check
            // against the login QUEST_REPEAT 0x10D7 set) so the driver can prioritise repeatables.
            map[id] = new QuestDef(id, title, startNpc, b[off + 16] != 0, minLevel, maxLevel,
                b[off + 51], npcs, objectives, rewards, start, action, finish, Repeatable: false,
                PrereqQuest: prereqQuest);

            off += (int)dataLen;
        }
        return map;
    }
}

/// <summary>A quest definition decoded from QuestData.shn. <see cref="StartNpc"/> is the mobId
/// of the giver. <see cref="Npcs"/> = the quest's NPC list (start + turn-in NPCs — the @74
/// block, NOT kill targets). <see cref="Objectives"/> = the kill/collect goals (@102 block):
/// each names the mob to slay (and optionally an item it drops) + a count. Scripts are the
/// quest logic (Start = accept dialogue, Action = in-progress, Finish = turn-in).</summary>
public sealed record QuestDef(
    int Id, int Title, int StartNpc, bool IsNeedLevel, int MinLevel, int MaxLevel, int Class,
    IReadOnlyList<QuestTarget> Npcs, IReadOnlyList<QuestObjective> Objectives,
    IReadOnlyList<QuestRewardDef> Rewards, string StartScript, string ActionScript, string FinishScript,
    bool Repeatable = false, int PrereqQuest = 0)
{
    /// <summary>The npc this quest is turned in at: the first NPC in the quest's NPC list that
    /// isn't the start NPC, else the start NPC (most low-level quests turn in where they
    /// started; e.g. "Baby Steps" lists Julia(29) here as the turn-in).</summary>
    public int TurnInNpc
    {
        get
        {
            foreach (var t in Npcs) if (t.Id != 0 && t.Id != StartNpc) return t.Id;
            return StartNpc;
        }
    }

    /// <summary>The mobId to kill for this quest's (first) objective, or -1 if it has no kill
    /// objective (a pure meeting/talk quest). This is what the grind driver targets — Master
    /// Zach → 0 (Slime); a mushroom quest → 1 (Mushroom); King Crab → 2002.</summary>
    public int ObjectiveMob => Objectives.Count > 0 ? Objectives[0].Mob : -1;

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

/// <summary>An NPC referenced by a quest (start / turn-in), from the @74 NPC-list block.</summary>
public sealed record QuestTarget(bool IsNpc, int Id, bool ToKill, int Amount);

/// <summary>A quest kill/collect objective. <see cref="Type"/>: 1 = kill the mob, 2 = pick up
/// an item (dropped by the mob). <see cref="Mob"/> = the mobId to slay / that drops the item,
/// <see cref="Count"/> = how many, <see cref="Item"/> = the item id for type 2 (best-effort
/// offset — uncertain).</summary>
public sealed record QuestObjective(int Type, int Mob, int Count, int Item);

/// <summary>A quest reward: <see cref="Method"/> (1=Fixed,2=Choice), <see cref="Type"/>
/// (0=EXP,1=Money,2=Item,3=Fame), with item <see cref="ItemId"/>/<see cref="ItemCount"/> for
/// item rewards or <see cref="Amount"/> otherwise.</summary>
public sealed record QuestRewardDef(int Method, int Type, int ItemId, int ItemCount, ulong Amount, int RawIndex = 0);
