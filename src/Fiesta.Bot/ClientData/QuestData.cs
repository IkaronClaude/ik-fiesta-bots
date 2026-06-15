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

            var mobs = new List<QuestTarget>();
            for (int m = 0; m < 5; m++)
            {
                int o = off + 74 + m * 6;
                if (b[o] == 0) continue;
                mobs.Add(new QuestTarget(b[o + 1] != 0, U16(o + 2), b[o + 4] != 0, b[o + 5]));
            }

            var items = new List<QuestItemReq>();
            for (int it = 0; it < 10; it++)
            {
                int o = off + 104 + it * 6;
                if (b[o] == 0) continue;
                items.Add(new QuestItemReq(b[o + 1], U16(o + 2), U16(o + 4)));
            }

            var rewards = new List<QuestRewardDef>();
            for (int r = 0; r < 12; r++)
            {
                int o = off + 516 + r * 12;
                byte method = b[o], type = b[o + 1];
                if (method == 0) continue;
                if (type == 2) // Item
                    rewards.Add(new QuestRewardDef(method, type, U16(o + 4), U16(o + 6), 0));
                else
                    rewards.Add(new QuestRewardDef(method, type, 0, 0, BitConverter.ToUInt64(b, o + 4)));
            }

            int sLen = U16(off + 660), fLen = U16(off + 662), aLen = U16(off + 664);
            int ss = off + Fixed;
            string start = sLen > 0 ? EucKr.GetString(b, ss, sLen).TrimEnd('\0') : "";
            string action = aLen > 0 ? EucKr.GetString(b, ss + sLen, aLen).TrimEnd('\0') : "";
            string finish = fLen > 0 ? EucKr.GetString(b, ss + sLen + aLen, fLen).TrimEnd('\0') : "";

            map[id] = new QuestDef(id, title, startNpc, b[off + 16] != 0, b[off + 17], b[off + 18],
                b[off + 51], mobs, items, rewards, start, action, finish);

            off += (int)dataLen;
        }
        return map;
    }
}

/// <summary>A quest definition decoded from QuestData.shn. <see cref="StartNpc"/> is the mobId
/// of the giver. <see cref="Mobs"/> = kill/meeting targets (an NPC target with IsToKill=false is
/// usually the turn-in NPC). <see cref="Items"/> = collect requirements. Scripts are the quest
/// logic (Start = accept dialogue, Action = in-progress, Finish = turn-in).</summary>
public sealed record QuestDef(
    int Id, int Title, int StartNpc, bool IsNeedLevel, int MinLevel, int MaxLevel, int Class,
    IReadOnlyList<QuestTarget> Mobs, IReadOnlyList<QuestItemReq> Items,
    IReadOnlyList<QuestRewardDef> Rewards, string StartScript, string ActionScript, string FinishScript)
{
    /// <summary>The npc this quest is turned in at: the first non-kill mob target if present
    /// (a non-kill entry is the related/turn-in NPC — its <c>IsNpc</c> flag is unreliable in
    /// the data, e.g. Julia(29) on "Baby Steps" reads IsNpc=false yet is the turn-in), else
    /// the start NPC (most low-level quests turn in where they started).</summary>
    public int TurnInNpc
    {
        get
        {
            foreach (var t in Mobs) if (!t.ToKill && t.Id != 0) return t.Id;
            return StartNpc;
        }
    }

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

/// <summary>A quest mob/NPC target: <see cref="IsNpc"/> (vs monster), <see cref="Id"/> (mobId),
/// <see cref="ToKill"/> (kill objective) and <see cref="Amount"/> to kill.</summary>
public sealed record QuestTarget(bool IsNpc, int Id, bool ToKill, int Amount);

/// <summary>A quest item requirement: <see cref="Type"/>, item <see cref="Id"/>, <see cref="Amount"/>.</summary>
public sealed record QuestItemReq(int Type, int Id, int Amount);

/// <summary>A quest reward: <see cref="Method"/> (1=Fixed,2=Choice), <see cref="Type"/>
/// (0=EXP,1=Money,2=Item,3=Fame), with item <see cref="ItemId"/>/<see cref="ItemCount"/> for
/// item rewards or <see cref="Amount"/> otherwise.</summary>
public sealed record QuestRewardDef(int Method, int Type, int ItemId, int ItemCount, ulong Amount);
