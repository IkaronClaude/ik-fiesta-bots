using System.Linq;
using System.Text;
using Fiesta.Bot.Login;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Zone;

/// <summary>
/// The zone phase: connect to the zone endpoint from CHAR_LOGIN_ACK, handshake,
/// and send a from-scratch MAP_LOGIN_REQ (0x1801) — chardata (live WM handle +
/// char name) plus the 49 data-file checksums. The zone replies MAP_LOGINFAIL
/// (0x1804, with nWrongDataFileIndex) on a checksum mismatch, or streams the
/// character's initial state (the [1038] burst) once in zone.
///
/// The caller must keep the WM connection OPEN across this call — the zone
/// validates the incoming player against a live WM session.
/// </summary>
public sealed class ZoneEntry
{
    private static readonly ushort OpMapLoginFail = PacketRegistry.GetOpcode<PROTO_NC_MAP_LOGINFAIL_ACK>();
    // MAP_LOGINCOMPLETE (0x1803): the client's "finished loading — spawn me in
    // world" signal, sent right after the server's MAP_LOGIN_ACK. Without it the
    // character stays in a loading limbo (invisible to others, no broadcasts, GM
    // commands ignored). Bare opcode, empty payload — like the heartbeat ack.
    private static readonly ushort OpMapLoginComplete =
        (ushort)(((int)ProtocolCommand.Map << 10) | (int)MapOpcode.LogincompleteCmd);
    // MAP_LOGIN_ACK (0x1802): the server's ack that ends the post-[1801] chardata
    // burst; the client sends MAP_LOGINCOMPLETE only after seeing it.
    private static readonly ushort OpMapLoginAck =
        (ushort)(((int)ProtocolCommand.Map << 10) | (int)MapOpcode.LoginAck);
    // NC_CHAR_CLIENT_SKILL_CMD (0x103D): the learned-skill list, sent DURING the post-[1801]
    // burst (before MAP_LOGINCOMPLETE) — so the in-zone session loop / ZoneView never sees it.
    // We capture it here and seed ZoneView. Layout: [restempow:1][PartMark:1][nMaxNum:2]
    // [chrregnum:4][number:2][SKILLREADBLOCK(12) × number]; each block leads with skillid u16.
    private static readonly ushort OpClientSkill = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_SKILL_CMD>();
    private const int SkillListHeaderLen = 10;
    private const int SkillBlockLen = 12;
    // NC_CHAR_CLIENT_PASSIVE_CMD (0x103E): the PASSIVE-skill list, sent in the same burst right after
    // the active list (0x103D). Unnamed in FiestaLib (only the 0x100E update variant is mapped), so
    // hand-parse: {number u16 @0, passive u16[number] @2}. Verified: IkFresh 01 00 09 00 = 1 passive id 9.
    private const ushort OpClientPassive = 0x103E;
    // NC_CHAR_CLIENT_ITEM_CMD (0x1047): the bag + worn-gear list, sent (per `box`) once per
    // container during the login burst — also drained here, so the bag AND equipment are
    // empty until a live CELL/EQUIP change. We capture every frame and seed ZoneView.
    private static readonly ushort OpClientItem = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_ITEM_CMD>();
    // NC_CHAR_CLIENT_QUEST_DONE_CMD (0x103B) / QUEST_DOING (0x103A): the character's quest
    // completion + in-progress state, sent in the login burst. DONE = header(chrregnum u32,
    // nTotalDoneQuest u16, nTotalDoneQuestSize u16, nDoneQuestCount u16, nIndex u16) then
    // nDoneQuestCount × PLAYER_QUEST_DONE_INFO(10: id u16, tEndTime i64). DOING = header
    // (chrregnum u32, bNeedClear u8, nNumOfDoingQuest u8) then n × PLAYER_QUEST_INFO(32:
    // id u16, status u8, ...). Captured here and seeded into ZoneView so the quest driver can
    // diff against QuestData.shn to know what's available (the client computes the orange-! the
    // same way). Verified vs QuestsLowLevel.pcapng (done {1,2,3}, doing {8,956}).
    // No CLIENT_QUEST struct exists, so build the opcode from the Char dept + CharOpcode enum
    // (same pattern as OpMapLoginComplete): ClientQuestDoneCmd=59 → 0x103B, Doing=58 → 0x103A.
    private static readonly ushort OpQuestDone =
        (ushort)(((int)ProtocolCommand.Char << 10) | (int)CharOpcode.ClientQuestDoneCmd);
    private static readonly ushort OpQuestDoing =
        (ushort)(((int)ProtocolCommand.Char << 10) | (int)CharOpcode.ClientQuestDoingCmd);
    // NC_CHAR_CLIENT_QUEST_READ_CMD (0x10CE): the AVAILABLE-quest list — the ids the character
    // can accept right now (this is what the client turns into the orange-! / available-Q
    // marker; operator-confirmed). Layout: chrregnum u32@0, nNumOfReadQuest u16@4, then
    // nNumOfReadQuest × quest-id u16. (Verified: it listed the event quests 20036 "Please Find
    // My Candy" / 20046, with their unusual ids.) Active quests are excluded (they're in DOING).
    private static readonly ushort OpQuestRead =
        (ushort)(((int)ProtocolCommand.Char << 10) | (int)CharOpcode.ClientQuestReadCmd);
    // NC_CHAR_BASE (live server: Char cmd 56 = 0x1038, the first burst frame) carries the
    // CURRENT vitals + soul-stone reserve. Layout (verified live — CurHP@42 == MaxHp 208):
    // CurPwrStone u16@34, CurGrdStone u16@36, CurHPStone u16@38, CurSPStone u16@40,
    // CurHP u32@42, CurSP u32@46. This is the authoritative CURRENT soul-stone reserve at zone
    // enter — the [1802] param block only has the MAX (its current/PwrStone fields read 0). The
    // bot MUST seed from here, else it can't tell "reserve full" from "empty" and (a) spam-USEs at
    // full HP (a USE at 100% HP fails), (b) tries to over-buy past the cap (buy is refused if
    // current+buy > max). Verified: reserve was 18/23 the whole time it looked "depleted".
    private const ushort OpCharBase = 0x1038;

    private readonly byte[] _xorTable;
    private readonly Action<string> _log;
    private readonly string[] _checksums; // 49, precomputed from the client data

    public ZoneEntry(byte[] xorTable, Action<string> log, string[] checksums)
    {
        if (checksums.Length != DataFileChecksums.Files.Length)
            throw new ArgumentException($"expected {DataFileChecksums.Files.Length} checksums, got {checksums.Length}");
        _xorTable = xorTable;
        _log = log;
        _checksums = checksums;
    }

    /// <summary>Build a ZoneEntry by computing checksums from a client ressystem dir.</summary>
    public static ZoneEntry FromDataDir(byte[] xorTable, Action<string> log, string ressystemDir)
        => new(xorTable, log, DataFileChecksums.ComputeAll(ressystemDir));

    /// <summary>
    /// Enter the zone. Returns the open zone connection on success (in zone), or
    /// throws ZoneEntryException on MAP_LOGINFAIL / timeout.
    /// </summary>
    public async Task<ZoneEntryResult> EnterAsync(
        FiestaEndpoint zoneEp, ushort wmHandle, string charName, CancellationToken ct,
        Action<bool, ushort, ReadOnlyMemory<byte>>? packetTap = null)
    {
        var conn = await FiestaClientConnection.ConnectAsync(zoneEp.Host, zoneEp.Port, _xorTable, ct);
        conn.PacketTap = packetTap; // tap BEFORE the zone-enter burst is drained, so [1802]/charinfo is captured
        try
        {
            await conn.WaitForHandshakeAsync(ct: ct);
            _log($"[Zone] connected {zoneEp}, handshake seed=0x{conn.Seed:X4}");

            var req = new PROTO_NC_MAP_LOGIN_REQ();
            req.chardata.wldmanhandle = wmHandle;
            FillBytes(req.chardata.charid.n5_name, charName);
            for (var i = 0; i < _checksums.Length; i++)
            {
                req.checksum[i] = new Name8();
                FillBytes(req.checksum[i].n8_name, _checksums[i]); // 32 ASCII hex chars
            }
            await conn.SendAsync(req, ct);
            _log($"[Zone] >> MAP_LOGIN_REQ (0x1801) handle={wmHandle} char='{charName}' (+49 checksums)");

            // After [1801] the server streams the chardata burst and ends it with
            // MAP_LOGIN_ACK [1802]. The real client waits for [1802], THEN sends
            // MAP_LOGINCOMPLETE [1803] to finish spawning into the world. Sending
            // [1803] too early (before [1802]) leaves the char in loading limbo —
            // invisible to others, no broadcasts, GM/chat ignored. So drain the
            // burst until [1802] (or, as a fallback, the deadline) before [1803].
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var sawFrame = false;
            List<ushort>? skills = null;
            List<ushort>? passives = null;
            List<(byte box, ushort inven, ushort itemId, int count)>? items = null;
            List<ushort>? doneQuests = null;
            List<(ushort id, byte status, int progress, IReadOnlyList<int> objCounts)>? activeQuests = null;
            List<ushort>? readQuests = null;
            int? curHpStone = null, curSpStone = null;
            ulong? cen = null, exp = null;
            byte? charLevel = null;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(remaining);
                FiestaPacket pkt;
                try { pkt = await conn.ReadPacketAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // deadline
                _log($"[Zone] << 0x{pkt.Opcode:X4} dept={pkt.Department} cmd={pkt.Command} len={pkt.Payload.Length}");

                if (pkt.Opcode == OpMapLoginFail)
                {
                    var f = pkt.ReadBody<PROTO_NC_MAP_LOGINFAIL_ACK>();
                    var file = f.nWrongDataFileIndex < DataFileChecksums.Files.Length
                        ? DataFileChecksums.Files[f.nWrongDataFileIndex] + ".shn"
                        : "?";
                    throw new ZoneEntryException(
                        $"MAP_LOGINFAIL err={f.err} wrongDataFileIndex={f.nWrongDataFileIndex} ({file})");
                }

                sawFrame = true;
                if (pkt.Opcode == OpCharBase) // current vitals + soul-stone reserve counts + MONEY
                {
                    var p = pkt.Payload.Span;
                    // Experience u64 @26 (verified live: chrregnum u32@0, charid[20]@4, slotno@24,
                    // Level@25, Experience@26 — 6468 matched the DB). Seed exp so progress is visible
                    // from login and the leveler can gate on level/exp (not just the login-only level).
                    if (p.Length >= 34)
                    {
                        exp = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(26, 8));
                        // Level@25 is the char's AUTHORITATIVE current level, sent on EVERY zone-enter
                        // (login AND every cross-server handoff). Capture + apply it so bot.level() tracks
                        // level-ups even when the live 0x1074 LEVELUP packet is missed — otherwise level
                        // stays frozen at the login value and level-gated quest eligibility never advances.
                        charLevel = p[25];
                        _log($"[Zone] exp = {exp} (level {p[25]})");
                    }
                    else
                    {
                        // ⛔ A REAL CLIENT ALWAYS HAS EXP ON LOGIN — it draws the exp bar immediately
                        // (operator 2026-08-06: "'no exp seed packet' is not a failure that happens to a
                        // real client => our bug"). So a CHAR_BASE too short to hold Experience@26 is a
                        // DECODE GAP on our side, not a server quirk, and it must not pass silently: it
                        // left Exp at -1, which then silently discarded every later gain (see
                        // ZoneView._expPendingDelta) and made a levelling bot report exp:null for an hour.
                        // Name the actual length so the next pass can pin the real layout instead of
                        // re-deriving that something went missing.
                        _log($"[Zone] ⛔ CHAR_BASE too short for Experience@26 — len={p.Length} (need >=34). " +
                             "exp NOT seeded; this is OUR decode gap, the real client always has exp at login.");
                    }
                    if (p.Length >= 42)
                    {
                        curHpStone = p[38] | (p[39] << 8);
                        curSpStone = p[40] | (p[41] << 8);
                        _log($"[Zone] reserve: HPStone={curHpStone} SPStone={curSpStone}");
                    }
                    // Cen (money) u64 @58 (PDB: ...CurHP@42, CurSP@46, CurLP@50, fame@54, Cen@58).
                    // Money is ALWAYS in the char-info — seed it at login so money() is never -1/unknown.
                    if (p.Length >= 66)
                    {
                        cen = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(58, 8));
                        _log($"[Zone] money (cen) = {cen}");
                    }
                    continue;
                }
                if (pkt.Opcode == OpClientSkill) // learned-skill list (drained here; seed ZoneView)
                {
                    skills = ParseSkillList(pkt.Payload.Span);
                    _log($"[Zone] learned skills ({skills.Count}): {string.Join(",", skills)}");
                    continue;
                }
                if (pkt.Opcode == OpClientPassive) // learned-passive list (drained here; seed ZoneView)
                {
                    passives = ParsePassiveList(pkt.Payload.Span);
                    _log($"[Zone] learned passives ({passives.Count}): {string.Join(",", passives)}");
                    continue;
                }
                if (pkt.Opcode == OpClientItem) // bag/equip list (per box) — capture + seed ZoneView
                {
                    // Hand-parse: [numofitem u8][box u8][flag u8] then numofitem entries, each
                    // = [datasize u8][location u16][info...] where datasize = info byte-count.
                    // The typed struct reads a FIXED info size and misaligns/throws on the big
                    // frames (equipped/enchanted items carry more data) — so walk by datasize.
                    var p = pkt.Payload.Span;
                    if (p.Length >= 3)
                    {
                        int num = p[0]; byte box = p[1]; int off = 3;
                        items ??= new();
                        var logged = new List<string>();
                        for (int i = 0; i < num && off + 5 <= p.Length; i++)
                        {
                            int datasize = p[off];
                            ushort inven = (ushort)(p[off + 1] | (p[off + 2] << 8));
                            ushort itemId = (ushort)(p[off + 3] | (p[off + 4] << 8));
                            // Stack count = the lot bytes right after itemid. datasize = location(2)+
                            // itemid(2)+attr; a stackable material has attr = 1 byte-lot (e.g. Bone x29
                            // = datasize 5) or 2 word-lot; gear has a big attr blob (no lot → count 1).
                            int attr = datasize - 4;
                            int count = (attr == 1 && off + 5 < p.Length) ? p[off + 5]
                                      : (attr == 2 && off + 6 < p.Length) ? (p[off + 5] | (p[off + 6] << 8))
                                      : 1;
                            // itemId 0 is the REAL item "Leather Boots", NOT an empty slot. The frame lists
                            // only OCCUPIED slots (num = the server's item count — verified vs tItem: 48 bag
                            // items = num 48), so EVERY entry is a real item. The old `itemId != 0` guard
                            // silently dropped item-0 (e.g. 6 Leather Boots in slots 28-33), making bagFull()/
                            // free-slot wrong so GET_PLAYER_EMPTY_INVENTORY hand-ins failed. (wire+DB 2026-07-07)
                            items.Add((box, inven, itemId, count)); logged.Add($"{inven}:{itemId}x{count}");
                            off += 1 + datasize; // entry = datasize-byte + datasize bytes (location(2)+info)
                        }
                        _log($"[Zone] item frame box={box} n={num} ds0={(num > 0 && p.Length > 3 ? p[3] : 0)} items=[{string.Join(",", logged)}]");
                    }
                    continue;
                }
                if (pkt.Opcode == OpQuestDone) // completed-quest list (id u16 + tEndTime i64 per entry)
                {
                    var p = pkt.Payload.Span;
                    if (p.Length >= 12)
                    {
                        int n = p[8] | (p[9] << 8); // nDoneQuestCount
                        doneQuests ??= new();
                        for (int i = 0; i < n && 12 + i * 10 + 2 <= p.Length; i++)
                        {
                            int o = 12 + i * 10;
                            doneQuests.Add((ushort)(p[o] | (p[o + 1] << 8)));
                        }
                        _log($"[Zone] quests done ({doneQuests.Count}): {string.Join(",", doneQuests)}");
                    }
                    continue;
                }
                if (pkt.Opcode == OpQuestDoing) // in-progress quests (PLAYER_QUEST_INFO 32B: id u16, status u8)
                {
                    var p = pkt.Payload.Span;
                    if (p.Length >= 6)
                    {
                        int n = p[5]; // nNumOfDoingQuest
                        activeQuests ??= new();
                        for (int i = 0; i < n && 6 + i * 32 + 3 <= p.Length; i++)
                        {
                            int o = 6 + i * 32;
                            // PLAYER_QUEST_DATA.End_NPCMobCount[5] at record offset 24 = per-objective
                            // kill counts; their sum = the quest's credited progress. The zone re-sends
                            // this authoritatively on every entry (incl. after a handover), so it's how
                            // progress survives without a persistent cache.
                            // ⭐ KEEP THE PER-OBJECTIVE ARRAY, don't only sum it. Summing here and
                            // discarding the parts is why every goal row on the watch page read 0/N while
                            // the quest header showed the true total: the aggregate was seeded on zone
                            // entry and the per-objective counters were not, so after ANY relog (and this
                            // bot relogs constantly) the only per-goal numbers came from kills credited
                            // since that login. Live 2026-08-06: "Kid Woz's Small Wish" 7/8 in the header,
                            // "kill Skeleton 0/8" underneath it. The data was on the wire the whole time.
                            // ⭐ OFFSET 25, NOT 24 — measured, not assumed. With the array read at 24 the
                            // live burst reported `48(s6,7=[0/7/0/0/0])` and `52(s8,8=[0/8/0/0/0])`: both
                            // are SINGLE-objective quests whose only objective is index 0, yet the count
                            // landed in slot 1 every time, and slot 0 was 0 for all 30 active quests. One
                            // byte early. The aggregate never showed it, because summing five bytes from
                            // one byte early still catches the real values while that leading byte is 0 —
                            // which is why this survived undetected behind a correct-looking total.
                            // Cross-check that this is the ARRAY and not the indexing: the live 0x440D
                            // credit for "Secret Hideout 2" arrived with objIdx 1 for its second
                            // objective, so the wire's objIdx does match QuestData's ordering.
                            int prog = 0;
                            var objCounts = new int[5];
                            if (o + 30 <= p.Length)
                                for (int k = 0; k < 5; k++) { objCounts[k] = p[o + 25 + k]; prog += objCounts[k]; }
                            activeQuests.Add(((ushort)(p[o] | (p[o + 1] << 8)), p[o + 2], prog, objCounts));
                        }
                        _log($"[Zone] quests active ({activeQuests.Count}): {string.Join(",", activeQuests.Select(q => $"{q.id}(s{q.status},{q.progress}=[{string.Join('/', q.objCounts)}])"))}");
                    }
                    continue;
                }
                if (pkt.Opcode == OpQuestRead) // available-quest list (chrregnum u32, count u16, ids u16[])
                {
                    var p = pkt.Payload.Span;
                    if (p.Length >= 6)
                    {
                        int n = p[4] | (p[5] << 8);
                        readQuests ??= new();
                        for (int i = 0; i < n && 6 + i * 2 + 2 <= p.Length; i++)
                            readQuests.Add((ushort)(p[6 + i * 2] | (p[7 + i * 2] << 8)));
                        _log($"[Zone] quests available ({readQuests.Count}): {string.Join(",", readQuests)}");
                    }
                    continue;
                }
                if (pkt.Opcode == OpMapLoginAck) // [1802] — the login ack ending the burst
                {
                    // The spawn position is PROTO_NC_CHAR_MAPLOGIN_ACK.logincoord — the
                    // final SHINE_XY (two u32 LE) of the fixed 242-byte body. Parsing the
                    // tail is robust to the big param sub-struct in between. Verified vs
                    // the first MoverunCmd's from-coord (Portals.pcapng).
                    uint? sx = null, sy = null;
                    var span = pkt.Payload.Span;
                    // PROTO_NC_CHAR_MAPLOGIN_ACK.charhandle is the FIRST u16 — the bot's
                    // own in-zone handle, needed to self-target (e.g. self-heal).
                    ushort? charHandle = span.Length >= 2 ? (ushort)(span[0] | (span[1] << 8)) : null;
                    if (span.Length >= 8)
                    {
                        var tail = span[^8..];
                        sx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail);
                        sy = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
                        _log($"[Zone] self handle={charHandle} spawn=({sx},{sy})");
                    }
                    // The body's middle is CHAR_PARAMETER_DATA (232 B) starting after the
                    // charhandle u16, so MaxHp/MaxSp are unsigned longs at param offsets
                    // 144/148 → body offsets 146/150 (PDB-extracted layout). Pull them so
                    // scripts can gate on a fraction of max (HP-stone when low). Current
                    // HP/SP arrive separately via 0x240E/0x240F once in-world.
                    uint? maxHp = null, maxSp = null;
                    if (span.Length >= 154)
                    {
                        maxHp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(146, 4));
                        maxSp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(150, 4));
                        _log($"[Zone] maxHp={maxHp} maxSp={maxSp}");
                    }
                    // Stone region of CHAR_PARAMETER_DATA: MaxHPStone @param160→body162,
                    // MaxSPStone @param164→body166 (max soul-stone reserve CAPACITY). The CURRENT
                    // counts live in NC_CHAR_BASE (0x1038, parsed above), not here. Seed the max so
                    // the bot can restock at a fraction of capacity (<10%) and compute the buy deficit.
                    uint? maxHpStone = null, maxSpStone = null;
                    CharStats? charStats = null;
                    if (span.Length >= 170)
                    {
                        maxHpStone = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(162, 4));
                        maxSpStone = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(166, 4));
                        _log($"[Zone] maxHPStone={maxHpStone} maxSPStone={maxSpStone}");
                    }
                    // CHAR combat/defense stats — same CHAR_PARAMETER_DATA block, u32 LE at fixed body offsets
                    // (verified 2026-07-29 by matching Bot7170's screenshot values AND cross-checked vs the known
                    // MaxHp@146 above). These are the DEFENDER side of the damage-survivability model (mob dps vs
                    // DEF/M.Def + dodge). Decode→log now; ZoneView/BotApi exposure + the survivability calc follow.
                    // See memory [[fiesta-combat-damage-model]] for the full offset map.
                    if (span.Length >= 130)
                    {
                        static uint U(ReadOnlySpan<byte> s, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));
                        // Exp band: CHAR_PARAMETER_DATA.PrevExp @param0 and .NextExp @param8, i.e. body
                        // offsets 2 and 10 (the block starts after the charhandle u16 — same +2 shift the
                        // stone reads above rely on). u64 each.
                        static ulong U64(ReadOnlySpan<byte> s, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o, 8));
                        var prevExp = span.Length >= 18 ? U64(span, 2) : 0UL;
                        var nextExp = span.Length >= 18 ? U64(span, 10) : 0UL;
                        charStats = new CharStats(
                            U(span, 0x16), U(span, 0x1E), U(span, 0x26), U(span, 0x2E), U(span, 0x3E),
                            U(span, 0x46), U(span, 0x4E), U(span, 0x56), U(span, 0x5E), U(span, 0x66),
                            U(span, 0x6E), U(span, 0x7E), prevExp, nextExp);
                        // Log the band with the live exp so a wrong offset is obvious immediately: the
                        // invariant is prev <= exp <= next. If it doesn't hold, the decode is wrong.
                        _log($"[exp] band prev={prevExp} next={nextExp} (level {charLevel?.ToString() ?? "?"}) " +
                             $"— exp={exp?.ToString() ?? "?"}" +
                             (exp is { } e && (e < prevExp || e > nextExp) ? "  ⚠️ OUT OF BAND — offsets wrong" : ""));
                        _log($"[stats] STR={charStats.Str} END={charStats.End} DEX={charStats.Dex} INT={charStats.Int} SPR={charStats.Spr} | " +
                             $"Dmg={charStats.DmgMin}~{charStats.DmgMax} DEF={charStats.Def} Aim={charStats.Aim} Evasion={charStats.Evasion} M.Dmg={charStats.MagicDmg} M.Def={charStats.MagicDef}");
                    }
                    if (exp is null)
                        _log("[Zone] ⛔ MAP_LOGIN_ACK reached with NO exp seed — CHAR_BASE never arrived or " +
                             "was too short. OUR decode gap: a real client has exp the instant it logs in.");
                    return await CompleteLoginAsync(conn, "MAP_LOGIN_ACK", sx, sy, charHandle, maxHp, maxSp, skills, passives, items, doneQuests, activeQuests, readQuests, ct, curHpStone, curSpStone, maxHpStone, maxSpStone, cen, exp, charLevel, charStats);
                }
                // else: a chardata burst frame ([1038] etc.) — keep draining.
            }

            // Fallback: we saw the burst but no explicit [1802] before the deadline.
            // Still complete the login so we spawn rather than hang (position unknown).
            if (sawFrame)
            {
                if (exp is null)
                    _log("[Zone] ⛔ zone-enter burst completed with NO exp seed — CHAR_BASE never arrived. " +
                         "OUR bug, not the server's: the real client shows exp from the moment it logs in.");
                return await CompleteLoginAsync(conn, "burst (no explicit [1802])", null, null, null, null, null, skills, passives, items, doneQuests, activeQuests, readQuests, ct, curHpStone, curSpStone, null, null, cen, exp, charLevel, null);
            }
            throw new ZoneEntryException("Zone phase timed out with no MAP_LOGINFAIL and no zone traffic");
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Send MAP_LOGINCOMPLETE [1803] to finish spawning into the world,
    /// then hand back the open connection (now fully in zone).</summary>
    private async Task<ZoneEntryResult> CompleteLoginAsync(
        FiestaClientConnection conn, string via, uint? spawnX, uint? spawnY, ushort? charHandle,
        uint? maxHp, uint? maxSp, IReadOnlyList<ushort>? skills, IReadOnlyList<ushort>? passives,
        IReadOnlyList<(byte box, ushort inven, ushort itemId, int count)>? items,
        IReadOnlyList<ushort>? doneQuests, IReadOnlyList<(ushort id, byte status, int progress, IReadOnlyList<int> objCounts)>? activeQuests,
        IReadOnlyList<ushort>? readQuests, CancellationToken ct, int? curHpStone = null, int? curSpStone = null,
        uint? maxHpStone = null, uint? maxSpStone = null, ulong? cen = null, ulong? exp = null, byte? charLevel = null,
        CharStats? stats = null)
    {
        await conn.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), ct);
        _log($"[Zone] *** IN ZONE ({via}) >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) ***");
        return new ZoneEntryResult(conn, spawnX, spawnY, charHandle, maxHp, maxSp, skills, passives, items, doneQuests, activeQuests, readQuests, curHpStone, curSpStone, maxHpStone, maxSpStone, cen, exp, charLevel, stats, WasBurst: via.Contains("burst"));
    }

    /// <summary>Parse the learned skill ids out of a NC_CHAR_CLIENT_SKILL_CMD body
    /// (header then <c>number</c> × 12-byte blocks, each leading with the skill id u16).</summary>
    private static List<ushort> ParseSkillList(ReadOnlySpan<byte> p)
    {
        var skills = new List<ushort>();
        if (p.Length < SkillListHeaderLen) return skills;
        var number = (ushort)(p[8] | (p[9] << 8));
        for (var i = 0; i < number; i++)
        {
            var off = SkillListHeaderLen + i * SkillBlockLen;
            if (off + 2 > p.Length) break;
            var skillId = (ushort)(p[off] | (p[off + 1] << 8));
            // id 0 is a REAL skill (ActiveSkill.ID=0, e.g. "Slice and Dice [01]"/TripleHit01), not a
            // sentinel — `number` (above) already bounds this loop to real entries only (operator
            // 2026-07-01 — same bug class as "item id 0 = Leather Boots").
            skills.Add(skillId);
        }
        return skills;
    }

    /// <summary>Parse the learned passive ids out of a NC_CHAR_CLIENT_PASSIVE_CMD (0x103E) body:
    /// {number u16 @0, passive u16[number] @2}.</summary>
    private static List<ushort> ParsePassiveList(ReadOnlySpan<byte> p)
    {
        var passives = new List<ushort>();
        if (p.Length < 2) return passives;
        var number = (ushort)(p[0] | (p[1] << 8));
        for (var i = 0; i < number; i++)
        {
            var off = 2 + i * 2;
            if (off + 2 > p.Length) break;
            var pid = (ushort)(p[off] | (p[off + 1] << 8));
            // id 0 is a REAL passive ("Bravery Mastery [01]"/BraveMastery01) — `number` already bounds
            // the loop, so there is no empty-slot sentinel to filter. Wire proof (Bot7170, 2026-08-05):
            // 0x103E body `03 00 | 00 00 | 09 00 | 0A 00` = THREE passives 0/9/10, but this filter
            // reported only "9,10" and hid a learned Bravery Mastery [01] from the whole bot.
            // Third and last copy of the same "id 0 is a sentinel" bug (see ZoneView's two).
            passives.Add(pid);
        }
        return passives;
    }

    private static void FillBytes(byte[] dst, string s)
    {
        Array.Clear(dst);
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length));
    }
}

/// <summary>Result of a successful zone entry: the open connection, the char's
/// spawn position, its in-zone <see cref="CharHandle"/> (self handle), and its
/// <see cref="MaxHp"/>/<see cref="MaxSp"/> — all decoded from the [1802] login ack
/// (null if it wasn't seen). Current HP/SP arrive later via HPCHANGE/SPCHANGE.</summary>
/// <summary>Character combat/defence stats from the CHAR_PARAMETER_DATA block. These have been decoded
/// and LOGGED at zone-entry since 2026-07-29 and never stored — the comment there said "ZoneView/BotApi
/// exposure follows" and it hadn't. They are the defender side of the survivability model and what a
/// human needs on the watch panel beside HP.</summary>
/// <param name="PrevExp">Total exp at the START of the current level.</param>
/// <param name="NextExp">Total exp required to reach the NEXT level. Together these give the exp bar the
/// real client draws: <c>(Exp - PrevExp) / (NextExp - PrevExp)</c>. They are the FIRST two fields of
/// CHAR_PARAMETER_DATA and were sitting in a block we already parse — I previously reported the level
/// curve as underivable after searching the PDB field names and piping the result through `head -20`,
/// which truncated the answer off the end of the list. A narrow sample is not a negative result.</param>
public sealed record CharStats(
    uint Str, uint End, uint Dex, uint Int, uint Spr,
    uint DmgMin, uint DmgMax, uint Def, uint Aim, uint Evasion, uint MagicDmg, uint MagicDef,
    ulong PrevExp = 0, ulong NextExp = 0);

public sealed record ZoneEntryResult(
    FiestaClientConnection Conn, uint? SpawnX, uint? SpawnY, ushort? CharHandle, uint? MaxHp = null, uint? MaxSp = null,
    IReadOnlyList<ushort>? Skills = null,
    IReadOnlyList<ushort>? Passives = null,
    IReadOnlyList<(byte box, ushort inven, ushort itemId, int count)>? Items = null,
    IReadOnlyList<ushort>? DoneQuests = null,
    IReadOnlyList<(ushort id, byte status, int progress, IReadOnlyList<int> objCounts)>? ActiveQuests = null,
    IReadOnlyList<ushort>? ReadQuests = null,
    int? CurHpStone = null, int? CurSpStone = null,
    uint? MaxHpStone = null, uint? MaxSpStone = null,
    ulong? Cen = null, ulong? Exp = null, byte? Level = null,
    CharStats? Stats = null,
    // True when login completed WITHOUT the explicit [1802] MAP_LOGIN_ACK ("burst") → position/HP were NOT
    // seeded (null) and the bot's nav is broken (can't find gates). The caller retries the zone-entry to get a
    // clean login instead of running blind. (operator 2026-07-18 — root of the freeze/stone-starve death loop.)
    bool WasBurst = false);

public sealed class ZoneEntryException : Exception
{
    public ZoneEntryException(string message) : base(message) { }
}
