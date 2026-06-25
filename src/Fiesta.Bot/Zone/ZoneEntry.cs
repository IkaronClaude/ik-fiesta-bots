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
            List<(ushort id, byte status, int progress)>? activeQuests = null;
            List<ushort>? readQuests = null;
            int? curHpStone = null, curSpStone = null;
            ulong? cen = null;
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
                            if (itemId != 0) { items.Add((box, inven, itemId, count)); logged.Add($"{inven}:{itemId}x{count}"); }
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
                            int prog = 0;
                            if (o + 29 <= p.Length) for (int k = 0; k < 5; k++) prog += p[o + 24 + k];
                            activeQuests.Add(((ushort)(p[o] | (p[o + 1] << 8)), p[o + 2], prog));
                        }
                        _log($"[Zone] quests active ({activeQuests.Count}): {string.Join(",", activeQuests.Select(q => $"{q.id}(s{q.status},{q.progress}))"))}");
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
                    if (span.Length >= 170)
                    {
                        maxHpStone = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(162, 4));
                        maxSpStone = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(166, 4));
                        _log($"[Zone] maxHPStone={maxHpStone} maxSPStone={maxSpStone}");
                    }
                    return await CompleteLoginAsync(conn, "MAP_LOGIN_ACK", sx, sy, charHandle, maxHp, maxSp, skills, passives, items, doneQuests, activeQuests, readQuests, ct, curHpStone, curSpStone, maxHpStone, maxSpStone, cen);
                }
                // else: a chardata burst frame ([1038] etc.) — keep draining.
            }

            // Fallback: we saw the burst but no explicit [1802] before the deadline.
            // Still complete the login so we spawn rather than hang (position unknown).
            if (sawFrame)
                return await CompleteLoginAsync(conn, "burst (no explicit [1802])", null, null, null, null, null, skills, passives, items, doneQuests, activeQuests, readQuests, ct, curHpStone, curSpStone, null, null, cen);
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
        IReadOnlyList<ushort>? doneQuests, IReadOnlyList<(ushort id, byte status, int progress)>? activeQuests,
        IReadOnlyList<ushort>? readQuests, CancellationToken ct, int? curHpStone = null, int? curSpStone = null,
        uint? maxHpStone = null, uint? maxSpStone = null, ulong? cen = null)
    {
        await conn.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), ct);
        _log($"[Zone] *** IN ZONE ({via}) >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) ***");
        return new ZoneEntryResult(conn, spawnX, spawnY, charHandle, maxHp, maxSp, skills, passives, items, doneQuests, activeQuests, readQuests, curHpStone, curSpStone, maxHpStone, maxSpStone, cen);
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
            if (skillId != 0) skills.Add(skillId);
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
            if (pid != 0) passives.Add(pid);
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
public sealed record ZoneEntryResult(
    FiestaClientConnection Conn, uint? SpawnX, uint? SpawnY, ushort? CharHandle, uint? MaxHp = null, uint? MaxSp = null,
    IReadOnlyList<ushort>? Skills = null,
    IReadOnlyList<ushort>? Passives = null,
    IReadOnlyList<(byte box, ushort inven, ushort itemId, int count)>? Items = null,
    IReadOnlyList<ushort>? DoneQuests = null,
    IReadOnlyList<(ushort id, byte status, int progress)>? ActiveQuests = null,
    IReadOnlyList<ushort>? ReadQuests = null,
    int? CurHpStone = null, int? CurSpStone = null,
    uint? MaxHpStone = null, uint? MaxSpStone = null,
    ulong? Cen = null);

public sealed class ZoneEntryException : Exception
{
    public ZoneEntryException(string message) : base(message) { }
}
