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

/// <summary>A combat hit broadcast: <paramref name="Attacker"/> hit
/// <paramref name="Defender"/> for <paramref name="Damage"/>, leaving the defender at
/// <paramref name="RestHp"/>. Decoded from SWING_DAMAGE (our swing) and
/// SOMEONESWING_DAMAGE (others'); the latter carries no damage value (0). Compare the
/// handles against the bot's self handle to tell "I hit / I got hit".</summary>
public sealed record HitInfo(ushort Attacker, ushort Defender, ushort Damage, uint RestHp)
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
    // MOVESPEED (0xCC0D): the server broadcasts a mover's current walk/run speed
    // (nMoverHandle, nWalk u16, nRun u16). The client uses this to pace movement
    // packets — walking, riding, or under a speed-altering abstate.
    // ACT_MOVESPEED (0x203E): self-only speed (walkspeed u16, runspeed u16) sent
    // at zone login and periodically thereafter. Both arrive; 0x203E is always-self
    // (no handle) while 0xCC0D covers any mover and needs SelfHandle filtering.
    // Conversion: field_value * (120.0 / 33.0) ≈ u/s (33 = base walk from capture).
    private const ushort OpActMoveSpeed = 0x203E;
    private const ushort OpMoverRideOn = 0xCC02;
    private const ushort OpMoverRideOff = 0xCC06;
    private const ushort OpMoveSpeed = 0xCC0D;
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
    // Shop open (Menu dept 0x0F): the server sends the merchant's sell list when you
    // click it. Variants by shop type — weapon(3)/item(6) and their table(9/11) forms —
    // all wire as [itemnum u16][npc u16][MENUITEM × itemnum]. We read each MENUITEM's
    // leading u16 as the item id (stride computed from the frame, robust to the element
    // size we don't have a struct for). buy with NC_ITEM_BUY_REQ {itemid, lot}.
    private static readonly ushort[] OpShopOpen =
        { 0x3C03, 0x3C06, 0x3C09, 0x3C0B };
    // NPC menu (Act cmd 28 = 0x201C): clicking a merchant/script NPC makes the server
    // open its menu and wait for the client to pick an option (NPCMENUOPEN_ACK 0x201D)
    // before it sends the shop list. Verified in PurchaseSell.pcapng: NPCCLICK ->
    // NPCMENUOPEN_REQ -> NPCMENUOPEN_ACK{1} -> SHOPOPEN.
    private const ushort OpActNpcMenuOpen = 0x201C;
    // Soul-stone reserve BUY ack (HP 0x5003 / SP 0x5004): the server confirms a charge
    // purchase and reports the new reserve total ({totalnumber u16}). This is BOTH the
    // buy-success packet AND the stone-count source — a buy only succeeds near the healer,
    // so a missing ack = the buy didn't take. (USE draws from this reserve: 0x5007/0x5009.)
    private const ushort OpSoulStoneHpBuyAck = 0x5003;
    private const ushort OpSoulStoneSpBuyAck = 0x5004;
    // Death/revive (Char dept): DEADMENU 0x104D = server opens the death menu (you died);
    // REVIVE_REQ 0x104E (C->S) = "move to respawn point" (-> nearest town); REVIVESAME 0x104F
    // = revived in place (e.g. a cleric's resurrection — no town trip). Auto-respawn caps at
    // ~2 min dead. Track Dead so behaviours can wait for an in-place revive vs respawn.
    private const ushort OpCharDeadMenu = 0x104D;
    private const ushort OpCharReviveSame = 0x104F;
    private const ushort OpCharReviveOther = 0x1050;
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
    // Self HP/SP change (BAT 0x240E/0x240F): the server's authoritative current HP/SP
    // after any change (combat damage, heal, regen, soul-stone). MaxHp/MaxSp come from
    // the [1802] login param block (seeded by the manager). These drive "HP-stone when
    // low" / "heal when low" scripts. Damage broadcasts (own swing 0x2448 + others'
    // 0x2449) carry attacker/defender/damage/resthp for the on_hit script hook.
    private static readonly ushort OpHpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_HPCHANGE_CMD>();
    private static readonly ushort OpSpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_SPCHANGE_CMD>();
    private static readonly ushort OpSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SWING_DAMAGE_CMD>();
    private static readonly ushort OpSomeoneSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SOMEONESWING_DAMAGE_CMD>();

    private readonly BotSession _session;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<ushort, NearbyPlayer> _nearby = new();
    private readonly ConcurrentDictionary<ushort, NearbyNpc> _npcs = new();
    private readonly ConcurrentDictionary<byte, ushort> _inventory = new(); // bag slot -> itemId
    private readonly ConcurrentDictionary<byte, ushort> _equipment = new(); // equip slot -> itemId
    private ushort? _mountHandle; // last known mount mover handle (from RIDE_ON 0xCC02 payload)

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

    /// <summary>The bot's current walk speed in world-units per second, as last
    /// reported by the server's <c>MOVESPEED</c> broadcast (0xCC0D). Defaults to
    /// 120.0 if no broadcast has been received. The navigation layer reads this to
    /// pace movement packets — a mount or speed buff updates this live so the bot
    /// never sends steps too fast for its current speed.</summary>
    public double WalkSpeed { get; private set; } = 120.0;

    /// <summary>Raised when the server broadcasts a MOVESPEED (0xCC0D) for the
    /// bot itself — fires with the new walk speed in world-units per second so
    /// the navigation layer can re-pace movement.</summary>
    public event Action<double>? WalkSpeedChanged;

    /// <summary>The bot's current HP, as last reported by the server (HPCHANGE 0x240E).
    /// Null until the first update after zone entry. Pair with <see cref="MaxHp"/> for
    /// a fraction (the "HP-stone / heal when low" gate).</summary>
    public uint? Hp { get; private set; }

    /// <summary>The bot's current SP (SPCHANGE 0x240F). Null until the first update.</summary>
    public uint? Sp { get; private set; }

    /// <summary>The bot's maximum HP, from the [1802] login param block (seeded by the
    /// manager via <see cref="SeedMaxVitals"/>). 0 until seeded.</summary>
    public uint MaxHp { get; private set; }

    /// <summary>The bot's maximum SP, from the [1802] login param block. 0 until seeded.</summary>
    public uint MaxSp { get; private set; }

    /// <summary>Seed MaxHp/MaxSp from the zone-entry param block. Current HP/SP arrive
    /// later as HPCHANGE/SPCHANGE; this just sets the denominators for the fraction.</summary>
    public void SeedMaxVitals(uint? maxHp, uint? maxSp)
    {
        if (maxHp is { } h && h > 0) MaxHp = h;
        if (maxSp is { } s && s > 0) MaxSp = s;
    }

    /// <summary>Max soul-stone reserve charges (HP/SP), from the [1802] param block
    /// (MaxHPStone/MaxSPStone). 0 until seeded.</summary>
    public uint MaxHpStones { get; private set; }
    public uint MaxSpStones { get; private set; }

    /// <summary>Current soul-stone reserve charges (HP/SP), as last reported by a BUY_ACK
    /// (0x5003/0x5004, <c>totalnumber</c>). Null until a buy ack is seen (the initial count
    /// from the login char-info isn't decoded yet — TODO). The restock SM gates on this.</summary>
    public int? HpStones { get; private set; }
    public int? SpStones { get; private set; }

    public void SeedMaxStones(uint? maxHpStones, uint? maxSpStones)
    {
        if (maxHpStones is { } h && h > 0) MaxHpStones = h;
        if (maxSpStones is { } s && s > 0) MaxSpStones = s;
    }

    /// <summary>Raised when the bot's own HP changes (HPCHANGE 0x240E), with the new
    /// current HP. The combat/script layer reacts (heal / HP soul-stone when low).</summary>
    public event Action<uint>? HpChanged;

    /// <summary>Raised when the bot's own SP changes (SPCHANGE 0x240F).</summary>
    public event Action<uint>? SpChanged;

    /// <summary>Raised for every combat hit broadcast in view (own swing + others').
    /// The "process every hit" seam — scripts filter by attacker/defender vs self.</summary>
    public event Action<HitInfo>? Damaged;

    private readonly ConcurrentDictionary<ushort, DateTime> _aggressors = new(); // handle -> last hit us
    private static readonly TimeSpan CombatWindow = TimeSpan.FromSeconds(8);

    /// <summary>Handles that have hit the bot within the combat window (who's aggroing us —
    /// there's no "mob targeted you" packet; this is derived from incoming SWING_DAMAGE where
    /// the defender is our self handle).</summary>
    public IReadOnlyCollection<ushort> Aggressors =>
        _aggressors.Where(kv => DateTime.UtcNow - kv.Value < CombatWindow).Select(kv => kv.Key).ToArray();

    /// <summary>True if the bot has been hit in the last few seconds — i.e. it's taking
    /// damage. IMPORTANT: a clean logout will NOT complete while in combat (the server's
    /// logout countdown resets on damage) — flee out of enemy range first. Used to gate
    /// safe-logout and to know when to disengage/heal.</summary>
    public bool InCombat => DateTime.UtcNow - LastHitAtUtc < CombatWindow;

    /// <summary>When the bot was last hit (UtcMinValue if never).</summary>
    public DateTime LastHitAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>True while the bot is dead (DEADMENU opened, not yet revived). Behaviours
    /// can wait for an in-place revive (cleric) before respawning to town, or respawn via
    /// <see cref="Manager.BotManager.RespawnAsync"/>; the server auto-respawns after ~2 min.</summary>
    public bool Dead { get; private set; }

    /// <summary>When the bot died (DEADMENU), for the ~2-min auto-respawn timeout / "wait
    /// for a cleric" window. UtcMinValue if alive.</summary>
    public DateTime DeadAtUtc { get; private set; } = DateTime.MinValue;

    private void NoteHit(HitInfo h)
    {
        if (SelfHandle is { } self && h.Defender == self)
        {
            _aggressors[h.Attacker] = DateTime.UtcNow;
            LastHitAtUtc = DateTime.UtcNow;
        }
        Damaged?.Invoke(h);
    }

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

    private volatile ushort[] _shopItems = Array.Empty<ushort>();

    /// <summary>The item ids the last-opened merchant sells (from SHOPOPEN). Empty until
    /// a shop is opened (click a merchant). <see cref="Manager.BotManager.BuyAsync"/> buys
    /// any of these by id.</summary>
    public IReadOnlyList<ushort> ShopItems => _shopItems;

    /// <summary>The npc handle of the last-opened shop (0 if none).</summary>
    public ushort ShopNpc { get; private set; }

    /// <summary>True while an NPC menu prompt is open and unanswered (server sent
    /// NPCMENUOPEN_REQ after we clicked a merchant/script NPC). The shop-open flow replies
    /// with NPCMENUOPEN_ACK to advance to the sell list.</summary>
    public bool NpcMenuOpen { get; private set; }

    /// <summary>Mark the NPC menu answered (after sending NPCMENUOPEN_ACK).</summary>
    public void ClearNpcMenu() => NpcMenuOpen = false;

    /// <summary>Raised when a merchant's shop opens, with the sell-list item ids.</summary>
    public event Action<IReadOnlyList<ushort>>? ShopOpened;

    /// <summary>Current bag contents: slot → itemId (built from the login item list
    /// and live cell/equip changes).</summary>
    public IReadOnlyDictionary<byte, ushort> Inventory => _inventory;

    /// <summary>Currently worn gear: equip slot → itemId (from equip-change events).</summary>
    public IReadOnlyDictionary<byte, ushort> Equipment => _equipment;

    public bool TryGetPlayer(ushort handle, out NearbyPlayer player) => _nearby.TryGetValue(handle, out player!);

    /// <summary>The bot's own zone handle (from the [1802] MAP_LOGIN_ACK). Set once
    /// zone entry completes; used to filter MOVESPEED broadcasts to self only.</summary>
    public ushort? SelfHandle { get; set; }

    /// <summary>Supplies the bot's current world position (set by the manager to the live
    /// tracked position). Lets aggro detection tell whether a mob is running toward us.</summary>
    public Func<(uint X, uint Y)?>? SelfPositionProvider { get; set; }

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
            // 0xCC02 payload = [mountHandle u16][zero...]. The mount is a separate
            // mover entity; its MOVESPEED (0xCC0D) uses this handle, not the player's.
            IsMounted = true;
            var p = pkt.Payload.Span;
            if (p.Length >= 2) _mountHandle = (ushort)(p[0] | (p[1] << 8));
            _log?.Invoke($"[ZoneView] mounted (RIDE_ON, mountH={_mountHandle})");
        }
        else if (op == OpMoverRideOff)
        {
            IsMounted = false;
            _mountHandle = null;
            // Reset speed to default running pace (120 u/s). The server will send
            // a 0x203E / 0xCC0D shortly after to confirm or adjust, but this
            // prevents a stale mount speed from pacing movement in the gap.
            if (Math.Abs(WalkSpeed - 120.0) > 0.5)
            {
                _log?.Invoke($"[ZoneView] move speed: {WalkSpeed:F0} -> 120 u/s (dismounted)");
                WalkSpeed = 120.0;
                WalkSpeedChanged?.Invoke(120.0);
            }
            _log?.Invoke("[ZoneView] dismounted (RIDE_OFF)");
        }
        else if (op == OpMoveSpeed)
        {
            // Mover-broadcast speed (0xCC0D): any mover's current walk/run speed.
            // Filter to self OR our active mount (the mount is a separate mover
            // entity, and its speed = our speed while riding). Values change on
            // mounting, dismounting, and speed-abstate changes.
            try
            {
                var spd = pkt.ReadBody<PROTO_NC_MOVER_MOVESPEED_CMD>();
                var ok = (SelfHandle is { } sh && spd.nMoverHandle == sh)
                      || (_mountHandle is { } mh && spd.nMoverHandle == mh);
                if (ok) ApplySpeed(spd.nWalk, spd.nRun, "CC0D");
            }
            catch { }
        }
        else if (op == OpActMoveSpeed)
        {
            // Self-only ACT_MOVESPEED (0x203E): always-self base walk/run speed.
            // No handle field — applies directly.
            try
            {
                var spd = pkt.ReadBody<PROTO_NC_ACT_MOVESPEED_CMD>();
                ApplySpeed((double)spd.walkspeed, (double)spd.runspeed, "203E");
            }
            catch { }
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
        else if (op == OpHpChange)
        {
            try
            {
                var hp = pkt.ReadBody<PROTO_NC_BAT_HPCHANGE_CMD>().hp;
                Hp = hp;
                HpChanged?.Invoke(hp);
            }
            catch { }
        }
        else if (op == OpSpChange)
        {
            try
            {
                var sp = pkt.ReadBody<PROTO_NC_BAT_SPCHANGE_CMD>().sp;
                Sp = sp;
                SpChanged?.Invoke(sp);
            }
            catch { }
        }
        else if (op == OpSwingDamage)
        {
            try
            {
                var d = pkt.ReadBody<PROTO_NC_BAT_SWING_DAMAGE_CMD>();
                NoteHit(new HitInfo(d.attacker, d.defender, d.damage, d.resthp));
            }
            catch { }
        }
        else if (op == OpSomeoneSwingDamage)
        {
            try
            {
                var d = pkt.ReadBody<PROTO_NC_BAT_SOMEONESWING_DAMAGE_CMD>();
                NoteHit(new HitInfo(d.attacker, d.defender, 0, d.resthp));
            }
            catch { }
        }
        else if (op == OpMenuServerMenu)
        {
            LastMenuAtUtc = DateTime.UtcNow;
            ServerMenuOpen = true;
            _log?.Invoke($"[ZoneView] server menu opened (0x3C01, {pkt.Payload.Length}b) — awaiting select");
        }
        else if (op == OpCharDeadMenu)
        {
            Dead = true; DeadAtUtc = DateTime.UtcNow;
            _log?.Invoke("[ZoneView] DIED (death menu) — revive in place or respawn to town");
        }
        else if (op == OpCharReviveSame || op == OpCharReviveOther)
        {
            if (Dead) _log?.Invoke("[ZoneView] revived in place");
            Dead = false; DeadAtUtc = DateTime.MinValue;
        }
        else if (op == OpActNpcMenuOpen)
        {
            NpcMenuOpen = true;
            _log?.Invoke($"[ZoneView] NPC menu opened (0x201C) — awaiting NPCMENUOPEN_ACK");
        }
        else if (op == OpSoulStoneHpBuyAck || op == OpSoulStoneSpBuyAck)
        {
            // BUY_ACK {totalnumber u16} = new reserve count + proof the buy took (only
            // succeeds near a healer). Missing ack after a buy = it didn't work.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                int total = p[0] | (p[1] << 8);
                if (op == OpSoulStoneHpBuyAck) HpStones = total; else SpStones = total;
                _log?.Invoke($"[ZoneView] soul-stone {(op == OpSoulStoneHpBuyAck ? "HP" : "SP")} BUY ok — reserve now {total}");
            }
        }
        else if (Array.IndexOf(OpShopOpen, op) >= 0)
        {
            // [itemnum u16][npc u16][MENUITEM × itemnum]. Read each MENUITEM's leading u16
            // as the item id; stride = remaining bytes / itemnum (we have no MENUITEM
            // struct, so derive it — robust to whatever the element size is).
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                int itemnum = p[0] | (p[1] << 8);
                ShopNpc = (ushort)(p[2] | (p[3] << 8));
                var rest = p.Length - 4;
                var items = new List<ushort>(itemnum);
                if (itemnum > 0 && rest > 0)
                {
                    var stride = rest / itemnum;
                    for (int i = 0; i < itemnum; i++)
                    {
                        var off = 4 + i * stride;
                        if (off + 2 > p.Length) break;
                        items.Add((ushort)(p[off] | (p[off + 1] << 8)));
                    }
                    _log?.Invoke($"[ZoneView] shop opened (0x{op:X4}) npc={ShopNpc} items={itemnum} stride={stride}");
                }
                _shopItems = items.ToArray();
                ShopOpened?.Invoke(_shopItems);
            }
        }
        else if (op == OpSomeoneMoveWalk || op == OpSomeoneMoveRun)
        {
            // Keep a tracked player's position current as they move (chase the
            // destination they're heading to). Only update players we already know.
            var p = pkt.Payload.Span;
            if (p.Length >= 18)
            {
                var hnd = (ushort)(p[0] | (p[1] << 8));
                var toX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(10, 4));
                var toY = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(14, 4));
                if (_nearby.TryGetValue(hnd, out var pl))
                {
                    _nearby[hnd] = pl with { X = toX, Y = toY };
                }
                else if (_npcs.TryGetValue(hnd, out var npc))
                {
                    // Keep mob positions live as they move. A mob RUNNING (0x201A, not the
                    // idle walk 0x2018) TOWARD us = it aggro'd — the earliest aggro signal,
                    // before it's in hit range. Mark it an aggressor + flag InCombat (threatened).
                    var oldD = SelfDist(npc.X, npc.Y);
                    _npcs[hnd] = npc with { X = toX, Y = toY };
                    // A mob RUNS (0x201A) when aggro'd and WALKS (0x2018) when idle/patrolling,
                    // so a mob RUNNING toward us = aggro. Aggro RANGE varies per mob — do NOT
                    // gate on a hardcoded distance; the run-vs-walk + getting-closer is the tell.
                    if (op == OpSomeoneMoveRun && oldD < double.MaxValue)
                    {
                        var newD = SelfDist(toX, toY);
                        if (newD < oldD - 4)   // running toward us (getting closer)
                        {
                            _aggressors[hnd] = DateTime.UtcNow;
                            LastHitAtUtc = DateTime.UtcNow;   // treat "charging at me" as in-combat
                            _log?.Invoke($"[ZoneView] mob {npc.MobId} (h={hnd}) running at us ({newD:F0}u) — AGGRO");
                        }
                    }
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

    // Conversion: 127 raw units (human runspeed from 0x203E capture) ≈ 120 u/s.
    // The char always runs by default; walkspeed (33) is a slow toggle.
    // Mounted runspeed (254 in 0xCC0D) ≈ 240 u/s.
    private const double SpeedRawToUPerSec = 120.0 / 127.0;

    private void ApplySpeed(double rawWalk, double rawRun, string source)
    {
        var newSpeed = rawRun * SpeedRawToUPerSec;
        if (Math.Abs(newSpeed - WalkSpeed) > 0.5)
        {
            _log?.Invoke($"[ZoneView] move speed: {WalkSpeed:F0} -> {newSpeed:F0} u/s (raw: walk={rawWalk} run={rawRun}, {source})");
            WalkSpeed = newSpeed;
            WalkSpeedChanged?.Invoke(newSpeed);
        }
    }

    /// <summary>Distance from the bot's current position to (x,y); double.MaxValue if the
    /// self position isn't known yet (so "approaching" comparisons safely fail closed).</summary>
    private double SelfDist(uint x, uint y)
    {
        if (SelfPositionProvider?.Invoke() is not { } me) return double.MaxValue;
        double dx = (double)x - me.X, dy = (double)y - me.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
