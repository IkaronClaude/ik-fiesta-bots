using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Navigation;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Session;

/// <summary>A player the bot can currently see in zone (from Briefinfo broadcasts).</summary>
public sealed record NearbyPlayer(ushort Handle, string Name, byte Class, byte Level, uint X, uint Y)
{
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An NPC/mob the bot can see in zone (from the MOB briefinfo the zone
/// broadcasts on field enter). <see cref="MobId"/> indexes the server mob table —
/// resolve to a name/role offline (the ServerSource SQL project), or learn it by
/// play (auto-discovery). Coord is the live world position. Sourcing NPC positions
/// from this zone packet (not the server <c>NPC.txt</c>) is deliberate: it works
/// even when we don't have the server files.
///
/// <para><see cref="Flag"/> is the per-mob flagstate byte: <b>0 = plain NPC/mob,
/// 1 = a gate</b> (field link). For a gate, <see cref="LinkMap"/> carries the
/// destination map name the packet embeds (e.g. RouN's GateRou1 → "RouCos02") —
/// verified against the server NPC table, 2026-06-11. So gate discovery AND where
/// each gate leads come straight from the zone, no server files needed.</para></summary>
public sealed record NearbyNpc(ushort Handle, ushort MobId, byte Mode, uint X, uint Y, byte Flag = 0, string? LinkMap = null)
{
    public bool IsGate => Flag == 1;
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An in-zone chat line overheard from a nearby speaker.</summary>
public sealed record ChatMessage(ushort Handle, string? SenderName, string Text)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A live, read-only model of what one bot perceives in its zone, built by
/// decoding the inbound frames a <see cref="BotSession"/> fans out. Tracks nearby
/// players (appear/leave from Briefinfo) and raises chat events — the shared
/// perception layer the buff/party behaviors and the future LLM-control API both
/// consume. Decoding runs on the session read loop, so it stays cheap; reactions
/// are fired as events for handlers to offload.
/// </summary>
public sealed class ZoneView : IDisposable
{
    private static readonly ushort OpBriefChar = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_CHARACTER_CMD>();
    private static readonly ushort OpBriefLogin = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD>();
    private static readonly ushort OpBriefDelete = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_BRIEFINFODELETE_CMD>();
    private static readonly ushort OpBriefMob = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_MOB_CMD>();
    private static readonly ushort OpRegenMob = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_REGENMOB_CMD>();
    // Mover (mount) ride state — self only (0xCC02/0xCC06; 0xCC04 = someone else).
    private const ushort OpMoverRideOn = 0xCC02;
    private const ushort OpMoverRideOff = 0xCC06;
    // Map transition (gate / town portal): LINKSAME = in-band map change on the same
    // zone server, LINKOTHER = handoff to a different zone server (reconnect).
    private const ushort OpMapLinkSame = 0x1809;
    private const ushort OpMapLinkOther = 0x180A;
    /// <summary>Known NC_BAT_SKILLBASH_CAST_FAIL_ACK reason codes (empirically
    /// captured, not in FiestaLib enums). The <c>0x0F</c>-prefix is consistent across
    /// all codes seen so far — treat as a server-side subsystem, not a random constant.</summary>
    public static class CastFailReason
    {
        public const ushort NotEnoughSp = 0x0FC9;
        public const ushort OutOfRange = 0x0FCA;
        // 0x0FC4, 0x0FC6 — unpinned (facing / cooldown / weapon type)
    }

    // MOVEFAIL (ACT cmd 27): the server rejected our last move (walked into an
    // obstacle the static grid doesn't have — a lantern, an NPC, a closed area) and
    // tells us the position to snap back to. The authoritative source of truth for
    // where we actually are; the client shows "this area is not accessible".
    private const ushort OpActMoveFail = 0x201B;
    // Other players' movement broadcasts: SOMEONE_MOVEWALK (ACT cmd 24) / MOVERUN
    // (cmd 26). Layout [handle u16][from xy(8)][to xy(8)][speed u16][attr]. Briefinfo
    // only gives a player's spawn position; these keep NearbyPlayer.X/Y live as they
    // move — needed for follow to chase the real position, not the appear-spot.
    private const ushort OpSomeoneMoveWalk = 0x2018;
    private const ushort OpSomeoneMoveRun = 0x201A;
    // Server menu (0x3C01): a Yes/No or list prompt an NPC/gate opens (e.g. an
    // instance gate like EldPri01 asks "move to Collapsed Prison field?"). The client
    // answers with SERVERMENU_ACK (0x3C02) selecting an option. Track that one is open
    // so gate-taking can auto-confirm it.
    private const ushort OpMenuServerMenu = 0x3C01;
    // NC_BAT_SKILLBASH_CAST_FAIL_ACK (Bat cmd 52 = 0x2434): the server rejected a
    // skill cast. Payload is a 2-byte LE u16 reason code. Known codes (empirically
    // captured):
    //   0x0FC9 = not enough SP
    //   0x0FCA = out of range
    //   0x0FC4, 0x0FC6 = unpinned (facing / cooldown / weapon — TODO)
    private const ushort OpBatCastFail = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.SkillbashCastFailAck);
    private static readonly ushort OpClientItem = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_ITEM_CMD>();
    private static readonly ushort OpCellChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_CELLCHANGE_CMD>();
    private static readonly ushort OpEquipChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_EQUIPCHANGE_CMD>();

    private readonly BotSession _session;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<ushort, NearbyPlayer> _nearby = new();
    private readonly ConcurrentDictionary<ushort, NearbyNpc> _npcs = new();
    private readonly ConcurrentDictionary<byte, ushort> _inventory = new(); // bag slot -> itemId
    private readonly ConcurrentDictionary<byte, ushort> _equipment = new(); // equip slot -> itemId

    public ZoneView(BotSession session, Action<string>? log = null)
    {
        _session = session;
        _log = log;
        _session.PacketReceived += OnPacket;
    }

    /// <summary>Raised when a player enters (or refreshes in) view.</summary>
    public event Action<NearbyPlayer>? PlayerAppeared;

    /// <summary>Raised when a tracked handle leaves view.</summary>
    public event Action<ushort>? PlayerLeft;

    /// <summary>Raised for every overheard nearby chat line.</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>Raised when the zone moves the bot to another map (gate / town portal).
    /// In-band (<see cref="MapHandoff.IsCrossServer"/> = false) means just re-seed and
    /// switch grid; cross-server means reconnect to the carried endpoint. The
    /// navigation layer subscribes to drive cross-map travel.</summary>
    public event Action<MapHandoff>? MapChanged;

    /// <summary>The server map id (MapInfo.ID) the bot is currently on, as last
    /// reported by a transition. Null until the first transition (the starting map
    /// id isn't in the login ack — the bot tracks the start map by name instead).</summary>
    public ushort? CurrentMapId { get; private set; }

    /// <summary>Raised when the server rejects a move (MOVEFAIL) and snaps us back to
    /// the carried coord — the bot walked into something not in the static grid. The
    /// navigation layer resyncs the tracked position and aborts the current walk.</summary>
    public event Action<(uint X, uint Y)>? MoveFailed;

    /// <summary>Raised when a skill cast is rejected by the server
    /// (NC_BAT_SKILLBASH_CAST_FAIL_ACK). The 2-byte reason code identifies the failure:
    /// <see cref="CastFailReason.NotEnoughSp"/> (0x0FC9) = insufficient SP (recharge
    /// from soul-stone); <see cref="CastFailReason.OutOfRange"/> (0x0FCA) = target too
    /// far (move closer and retry); other codes are unpinned (facing, cooldown, weapon —
    /// log them to help reverse-engineering).</summary>
    public event Action<ushort>? CastFailed;

    public IReadOnlyCollection<NearbyPlayer> NearbyPlayers => _nearby.Values.ToArray();
    public int NearbyCount => _nearby.Count;

    /// <summary>NPCs/mobs currently in view (handle → id/coord), from the zone's MOB
    /// briefinfo. The runtime source for walk-to-NPC and gate location.</summary>
    public IReadOnlyCollection<NearbyNpc> NearbyNpcs => _npcs.Values.ToArray();
    public ChatMessage? LastChat { get; private set; }

    /// <summary>True while the bot is riding a mount (tracked from MOVER ride
    /// on/off, 0xCC02/0xCC06). Drives mount-aware routing (auto-use when far).</summary>
    public bool IsMounted { get; private set; }

    /// <summary>When the server last opened a menu prompt (0x3C01) — e.g. an instance
    /// gate's Yes/No confirm. Gate-taking checks this to auto-answer with a
    /// SERVERMENU_ACK. Null if no menu has opened.</summary>
    public DateTime? LastMenuAtUtc { get; private set; }

    /// <summary>Whether a server menu prompt (0x3C01) is currently open and unanswered.
    /// Set when one arrives, cleared once we send a SERVERMENU_ACK. An instance gate
    /// auto-opens its confirm menu when you stand on the trigger (e.g. on zone entry if
    /// you spawn on it), so gate-taking must answer an already-open menu, not just one
    /// that opens after the click.</summary>
    public bool ServerMenuOpen { get; private set; }

    /// <summary>Mark the open server menu as answered (called after sending the ack).</summary>
    public void ClearServerMenu() => ServerMenuOpen = false;

    /// <summary>Current bag contents: slot → itemId (built from the login item list
    /// and live cell/equip changes).</summary>
    public IReadOnlyDictionary<byte, ushort> Inventory => _inventory;

    /// <summary>Currently worn gear: equip slot → itemId (from equip-change events).</summary>
    public IReadOnlyDictionary<byte, ushort> Equipment => _equipment;

    public bool TryGetPlayer(ushort handle, out NearbyPlayer player) => _nearby.TryGetValue(handle, out player!);

    private void OnPacket(FiestaPacket pkt)
    {
        var op = pkt.Opcode;
        if (op == OpBriefChar)
        {
            foreach (var c in pkt.ReadBody<PROTO_NC_BRIEFINFO_CHARACTER_CMD>().chars)
                AddOrUpdate(c);
        }
        else if (op == OpBriefLogin)
        {
            AddOrUpdate(pkt.ReadBody<PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD>());
        }
        else if (op == OpBriefDelete)
        {
            var hnd = pkt.ReadBody<PROTO_NC_BRIEFINFO_BRIEFINFODELETE_CMD>().hnd;
            if (_nearby.TryRemove(hnd, out var gone))
            {
                _log?.Invoke($"[ZoneView] player left: {gone.Name} (h={hnd})");
                PlayerLeft?.Invoke(hnd);
            }
            _npcs.TryRemove(hnd, out _); // the same delete also retires NPCs/mobs
        }
        else if (op == OpBriefMob)
        {
            // A batch of NPC/mob spawns (sent on field enter): [mobnum:1][record × N].
            // We parse the fixed-stride record by hand instead of via the typed struct
            // because the struct skips the 99-byte flag blob — which is exactly where a
            // gate's destination-map string lives.
            var p = pkt.Payload.Span;
            if (p.Length >= 1)
            {
                int n = p[0];
                for (int i = 0; i < n; i++)
                    AddOrUpdateNpc(p, 1 + i * MobRecordLen);
            }
        }
        else if (op == OpRegenMob)
        {
            AddOrUpdateNpc(pkt.Payload.Span, 0); // single record, no count prefix
        }
        else if (op == OpMoverRideOn)
        {
            IsMounted = true;
            _log?.Invoke("[ZoneView] mounted (RIDE_ON)");
        }
        else if (op == OpMoverRideOff)
        {
            IsMounted = false;
            _log?.Invoke("[ZoneView] dismounted (RIDE_OFF)");
        }
        else if (op == OpActMoveFail)
        {
            // [back: SHINE_XY] — the server's authoritative position after rejecting
            // our move. Resync to it (we walked into something off-grid).
            var p = pkt.Payload.Span;
            if (p.Length >= 8)
            {
                var bx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p);
                var by = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
                _log?.Invoke($"[ZoneView] MOVEFAIL — server snapped us to ({bx},{by})");
                MoveFailed?.Invoke((bx, by));
            }
        }
        else if (op == OpBatCastFail)
        {
            // Payload = 2-byte LE u16 reason code (e.g. 0x0FC9 = not enough SP,
            // 0x0FCA = out of range). Log and fire the event so the combat layer
            // can react (recharge SP, re-approach, etc.).
            var reason = pkt.Payload.Length >= 2
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pkt.Payload.Span)
                : (ushort)0;
            if (reason == CastFailReason.NotEnoughSp)
                _log?.Invoke($"[ZoneView] cast FAILED — not enough SP (0x{reason:X4})");
            else if (reason == CastFailReason.OutOfRange)
                _log?.Invoke($"[ZoneView] cast FAILED — out of range (0x{reason:X4})");
            else
                _log?.Invoke($"[ZoneView] cast FAILED — unknown reason 0x{reason:X4} ({pkt.Payload.Length}b payload)" +
                             (pkt.Payload.Length > 2 ? $" raw={Convert.ToHexString(pkt.Payload.Span)}" : ""));
            CastFailed?.Invoke(reason);
        }
        else if (op == OpMenuServerMenu)
        {
            LastMenuAtUtc = DateTime.UtcNow;
            ServerMenuOpen = true;
            _log?.Invoke($"[ZoneView] server menu opened (0x3C01, {pkt.Payload.Length}b) — awaiting select");
        }
        else if (op == OpSomeoneMoveWalk || op == OpSomeoneMoveRun)
        {
            // Keep a tracked player's position current as they move (chase the
            // destination they're heading to). Only update players we already know.
            var p = pkt.Payload.Span;
            if (p.Length >= 18)
            {
                var hnd = (ushort)(p[0] | (p[1] << 8));
                if (_nearby.TryGetValue(hnd, out var pl))
                {
                    var toX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(10, 4));
                    var toY = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(14, 4));
                    _nearby[hnd] = pl with { X = toX, Y = toY };
                }
            }
        }
        else if (op == OpMapLinkSame || op == OpMapLinkOther)
        {
            var handoff = op == OpMapLinkSame
                ? MapHandoff.ParseLinkSame(pkt.Payload.Span)
                : MapHandoff.ParseLinkOther(pkt.Payload.Span);
            if (handoff is { } h)
            {
                CurrentMapId = h.MapId;
                _npcs.Clear();   // entities are per-map; the new map will re-broadcast
                _nearby.Clear();
                _log?.Invoke(h.IsCrossServer
                    ? $"[ZoneView] map handoff (cross-server) -> mapId={h.MapId} @({h.X},{h.Y}) via {h.Ip}:{h.Port} wm={h.WmHandle}"
                    : $"[ZoneView] map change (in-band) -> mapId={h.MapId} @({h.X},{h.Y})");
                MapChanged?.Invoke(h);
            }
        }
        else if (op == OpClientItem)
        {
            // Full bag snapshot at login (one frame per box). Decode typed; tolerate
            // any struct quirk so a bad frame never kills the read loop.
            try
            {
                foreach (var it in pkt.ReadBody<PROTO_NC_CHAR_CLIENT_ITEM_CMD>().ItemArray)
                {
                    var slot = (byte)(it.location.Inven & 0xFF);
                    if (it.info.itemid != 0) _inventory[slot] = it.info.itemid;
                }
            }
            catch { /* skip unparseable inventory frame */ }
        }
        else if (op == OpCellChange)
        {
            // [exchange:2][location:2][itemid:2][attr…] — a slot gained/changed an item.
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var slot = p[2];
                var itemId = (ushort)(p[4] | (p[5] << 8));
                if (itemId != 0) _inventory[slot] = itemId; else _inventory.TryRemove(slot, out _);
            }
        }
        else if (op == OpEquipChange)
        {
            // [exchange:2][location:1][itemid:2…] — item moved bag→equip slot.
            var p = pkt.Payload.Span;
            if (p.Length >= 1) _inventory.TryRemove(p[0], out _);   // vacate bag slot
            if (p.Length >= 5)
            {
                var equipSlot = p[2];
                var itemId = (ushort)(p[3] | (p[4] << 8));
                if (itemId != 0) _equipment[equipSlot] = itemId; else _equipment.TryRemove(equipSlot, out _);
            }
        }
        else if (op == ChatCodec.SomeoneChatOpcode)
        {
            if (ChatCodec.TryDecodeSomeoneChat(pkt.Payload.Span, out var handle, out var text)
                && text.Length > 0)
            {
                var name = _nearby.TryGetValue(handle, out var p) ? p.Name : null;
                var msg = new ChatMessage(handle, name, text);
                LastChat = msg;
                _log?.Invoke($"[ZoneView] chat <{name ?? $"h{handle}"}>: {text}");
                ChatReceived?.Invoke(msg);
            }
        }
    }

    private void AddOrUpdate(PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD c)
    {
        var name = FiestaText.Decode(c.charid.n5_name);
        var player = new NearbyPlayer(c.handle, name, c.chrclass, c.Level, c.coord.xy.x, c.coord.xy.y);
        var isNew = !_nearby.ContainsKey(c.handle);
        _nearby[c.handle] = player;
        if (isNew)
        {
            _log?.Invoke($"[ZoneView] player appeared: {name} (h={c.handle} class={c.chrclass} lvl={c.Level})");
            PlayerAppeared?.Invoke(player);
        }
    }

    // REGENMOB record layout (fixed 149 bytes — verified against Full.pcapng):
    // handle u16 | mode u8 | mobid u16 | x u32 | y u32 | dir u8 | flagstate u8 |
    // flag-blob[99] (gate dest-map string when flagstate==1) | sAnimation[32] | 3 tail.
    private const int MobRecordLen = 149;
    private const int FlagBlobOffset = 15; // within a record

    private void AddOrUpdateNpc(ReadOnlySpan<byte> p, int off)
    {
        if (off < 0 || off + FlagBlobOffset > p.Length) return; // need at least the header
        var handle = (ushort)(p[off] | (p[off + 1] << 8));
        var mode = p[off + 2];
        var mobid = (ushort)(p[off + 3] | (p[off + 4] << 8));
        var x = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(off + 5, 4));
        var y = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(off + 9, 4));
        var flag = p[off + 14];
        string? linkMap = null;
        if (flag == 1) // gate: the flag blob begins with the null-terminated dest-map name
            linkMap = ReadCString(p, off + FlagBlobOffset, 32);

        var npc = new NearbyNpc(handle, mobid, mode, x, y, flag, linkMap);
        var isNew = !_npcs.ContainsKey(handle);
        _npcs[handle] = npc;
        if (isNew)
            _log?.Invoke(flag == 1
                ? $"[ZoneView] gate appeared: id={mobid} h={handle} @({x},{y}) -> {linkMap}"
                : $"[ZoneView] npc/mob appeared: id={mobid} h={handle} @({x},{y})");
    }

    private static string? ReadCString(ReadOnlySpan<byte> p, int off, int max)
    {
        int end = off;
        int limit = Math.Min(p.Length, off + max);
        while (end < limit && p[end] != 0) end++;
        return end > off ? System.Text.Encoding.ASCII.GetString(p.Slice(off, end - off)) : null;
    }

    public void Dispose() => _session.PacketReceived -= OnPacket;
}
