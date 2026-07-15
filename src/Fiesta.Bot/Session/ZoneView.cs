using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Navigation;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Session;

/// <summary>A player the bot can currently see in zone (from Briefinfo broadcasts).</summary>
public sealed record NearbyPlayer(ushort Handle, string Name, byte Class, byte Level, uint X, uint Y,
    byte Mode = 0, byte Type = 0, byte KQTeamType = 0)
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
/// <summary>What service an NPC's shop offers, classified from the shop-open opcode it sends when
/// clicked (so the bot finds the skill master / smith / item merchant / healer dynamically).</summary>
public enum ShopKind { Unknown, Item, Weapon, Skill, SoulStone }

public sealed record NearbyNpc(ushort Handle, ushort MobId, byte Mode, uint X, uint Y, byte Flag = 0, string? LinkMap = null, byte Team = 0)
{
    public bool IsGate => Flag == 1;
    /// <summary><c>nKQTeamType</c> from the mob briefinfo (record offset 147): a King's-Quest
    /// battlefield team. Verified live that it is <b>uniformly 2</b> in a normal field for
    /// guards, mobs, herbs and NPCs alike — so it does NOT tell allies from enemies. The real
    /// guard/enemy discriminator is client MobInfo <c>IsPlayerSide</c>/<c>Type</c>
    /// (<see cref="GameData.ClientData.IsHuntableEnemy"/>). Kept only for completeness.</summary>
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An in-zone chat line overheard from a nearby speaker.</summary>
public sealed record ChatMessage(ushort Handle, string? SenderName, string Text)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An item lying on the ground, broadcast by NC_BRIEFINFO_DROPEDITEM_CMD
/// (0x1C0A) when a mob dies (or a player drops loot). <paramref name="Handle"/> is the
/// ground entity's handle — that's what NC_ITEM_PICK_REQ asks for, and what
/// NC_MAP_LOGOUT_CMD names when the item is gone (picked by anyone / despawned).
/// <paramref name="DropMobHandle"/> is the mob that dropped it (0 for a player drop).</summary>
public sealed record GroundItem(ushort Handle, ushort ItemId, uint X, uint Y, ushort DropMobHandle)
{
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Result of a pickup attempt, from NC_ITEM_PICK_ACK (0x300A):
/// <paramref name="ItemId"/> + <paramref name="Lot"/> picked, plus the raw
/// <paramref name="Error"/> code. NOTE: on a *successful* pick the captured Error was
/// 0x341 (not 0) while the item still entered the bag — so success is judged by the
/// paired NC_ITEM_CELLCHANGE_CMD (the bag slot gained the item), not by Error==0. The
/// failure code (e.g. inventory full) is unknown until a failing capture is taken — the
/// raw value is surfaced so it can be learned then.</summary>
public sealed record PickResult(ushort ItemId, uint Lot, ushort Error)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A pending quest-dialogue step the server is prompting (NC_QUEST_SCRIPT_CMD_REQ,
/// 0x4401): <paramref name="QuestId"/> + the QSC command code (<paramref name="Qsc"/>) + the
/// command's first <c>Data</c> word (<paramref name="DialogId"/>). The QSC command (Cmd, payload
/// offset 2) drives the dialogue: <b>Cmd 2 = SAY</b> a line (its <see cref="DialogId"/> is the
/// QuestDialog.shn text id, e.g. 202), <b>Cmd 0x0A = complete/reward</b>. The script runs
/// server-side, so every SAY page is its own 0x4401 the client must ACK. The client answers
/// QUEST_SCRIPT_CMD_ACK {questId, nQSC=Qsc, nResult} — nResult=1 proceeds/accepts (and at the
/// IF-RESULT branch, accepts the quest). Branching quests read the answer from QuestData.shn.
/// <see cref="DialogId"/> is the u32 at payload offset 7 (STRUCT_QSC.Data[0]) — for SAY it's the
/// spoken line id, letting the driver follow the script and detect the accept/complete step.</summary>
public sealed record QuestStep(ushort QuestId, byte Qsc, int DialogId = 0)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>One option of a server menu (0x3C01 SERVERMENU): <paramref name="Reply"/> is the byte
/// to send back in SERVERMENU_ACK (0x3C02) to select it; <paramref name="Text"/> is its label
/// (e.g. {0,"Yes"}, {1,"No"}). The answerer picks by matching Text, not a fixed reply.</summary>
public sealed record ServerMenuOption(byte Reply, string Text);

/// <summary>One entry in the MAP-ENTER NPC SEED — the authoritative full-map roster the server sends
/// in the bulk <c>NC_BRIEFINFO_MOB_CMD</c> (0x1C09) on map-enter (ALL NPCs+gates at infinite range, as
/// shown on the minimap). <paramref name="MobId"/> is the NPC's identity (stable across sessions; the
/// runtime handle is not), <paramref name="X"/>/<paramref name="Y"/> its server position, plus the gate
/// flag + link map. This is the SOURCE OF TRUTH for "where is NPC X on this map" — prefer it over the
/// lossy in-view (<c>_npcs</c>) cache for navigation.</summary>
public sealed record NpcSeedEntry(int MobId, uint X, uint Y, bool IsGate, string? LinkMap);

/// <summary>A scenario corridor DOOR's runtime state, from <c>NC_SCENARIO_DOORSTATE_CMD</c> (0x6C09).
/// <paramref name="State"/> is the raw state byte off the wire; <paramref name="X"/>/<paramref name="Y"/> is the
/// door entity's last-known position (from <c>_npcs</c> when the state changed), or null if the handle wasn't
/// in view. Used by the instance nav to wait at a closed door instead of thrashing into it.</summary>
public sealed record DoorState(ushort Handle, byte State, uint? X, uint? Y);

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
    private static readonly ushort OpReallyKill = PacketRegistry.GetOpcode<PROTO_NC_BAT_REALLYKILL_CMD>();
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
    // NC_QUEST_NOTIFY_MOB_KILL_CMD (Quest dept 0x11, cmd 13) = live quest kill-progress.
    private const ushort OpQuestMobKill = 0x440D;
    // NC_QUEST_REWARD_NEED_SELECT_ITEM_CMD (cmd 18) = server asks the client to choose a reward
    // during a turn-in (the [SHOW_REWARD] page). The driver answers with REWARD_SELECT_ITEM_INDEX.
    private const ushort OpQuestRewardNeedSelect = 0x4412;
    // NC_QUEST_GIVE_UP_ACK (cmd 8) = server confirms a quest abandon {questId, errorCode}.
    private const ushort OpQuestGiveUpAck = 0x4408;
    // NC_QUEST_START_ACK (cmd 21) = result of START_REQ {err u16}. NO questId — correlated with
    // the last START_REQ we sent (see _lastStartReqQuestId). err==0 = accepted; nonzero = refusal
    // reason (level/prereq/log-full/…); the live churn maps the codes to meanings.
    private const ushort OpQuestStartAck = 0x4415;
    // NC_QUEST_SELECT_START_ACK (cmd 16) = result of a menu SELECT_START {nNPCID, nQuestID, ErrorType}.
    private const ushort OpQuestSelectStartAck = 0x4410;
    // NC_QUEST_ERR (cmd 19) = generic quest error push. Layout not in the PDB — decoded raw.
    private const ushort OpQuestErr = 0x4413;

    /// <summary>Quest id the server is currently asking us to pick a reward for (from 0x4412),
    /// or null. Set when the turn-in shows the reward choices; consumed by the dialogue driver
    /// which sends the class-appropriate REWARD_SELECT_ITEM_INDEX.</summary>
    public int? RewardSelectQuestId { get; private set; }
    public void ClearRewardSelect() => RewardSelectQuestId = null;
    /// <summary>Known NC_BAT_SKILLBASH_CAST_FAIL_ACK reason codes (empirically
    /// captured, not in FiestaLib enums). The <c>0x0F</c>-prefix is consistent across
    /// all codes seen so far — treat as a server-side subsystem, not a random constant.</summary>
    public static class CastFailReason
    {
        public const ushort NotEnoughSp = 0x0FC9;
        public const ushort OutOfRange = 0x0FCA;
        // 0x0FC4, 0x0FC6 — unpinned (facing / cooldown / weapon type)

        /// <summary>Human-readable description of a cast-fail code, for logs and the
        /// <c>on_cast_fail</c> script hook (so failures like "not enough SP" are obvious
        /// instead of a bare hex code).</summary>
        public static string Describe(ushort code) => code switch
        {
            NotEnoughSp => "not enough SP",
            OutOfRange  => "target out of range",
            0x0FC0      => "cannot cast (dead / invalid state)",
            0x0FC4      => "cooldown/facing/weapon (0x0FC4)",
            0x0FC6      => "cooldown/facing/weapon (0x0FC6)",
            _           => $"cast failed (0x{code:X4})",
        };
    }

    // MOVEFAIL (ACT cmd 27): the server rejected our last move (walked into an
    // obstacle the static grid doesn't have — a lantern, an NPC, a closed area) and
    // tells us the position to snap back to. The authoritative source of truth for
    // where we actually are; the client shows "this area is not accessible".
    private const ushort OpActMoveFail = 0x201B;
    private DateTime _lastMoveFailLog = DateTime.MinValue;   // throttle for the NOTE-level MOVEFAIL desync diag
    // Time of the last SIGNIFICANT MOVEFAIL (a real shove-back, not a <64u micro-correction). The scenario
    // AreaEntry re-send-ack gates on this: it only ACKs once we've been shove-free for a moment = we've
    // actually ARRIVED and parked at the trigger at a server-valid position (operator 2026-07-15: "only start
    // the ack spam once near/inside, as confirmed by lack of movefail" — acking while still navigating there
    // is pointless, and the old 90s hard timer expired before the bot even reached the finale trigger).
    private DateTime _lastSignificantMoveFailUtc = DateTime.MinValue;
    // Abnormal-state set/reset on an entity: NC_BAT_ABSTATESET_CMD (0x2427) / _RESET (0x2428).
    // Layout [targetHandle u16][abStataIndex u32] (6 bytes). abStataIndex maps to AbState.shn; some
    // states IMMOBILIZE (stun/root/entangle) — e.g. the JCQ clone roots the player with StaQuestEntangle
    // (index 290). We track these on SELF so nav knows a MOVEFAIL is a root (don't learn a wall) and
    // combat knows to WAIT. Set = state applied, Reset = state cleared.
    private const ushort OpAbStateSet = 0x2427;
    private const ushort OpAbStateReset = 0x2428;
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
    // Shop open (Menu dept 0x0F): the server sends a sell list when you click an NPC.
    // Variants by shop type (Menu cmd): weapon(3), SKILL(4), item(6), and their "table"
    // forms weapon(9), SKILL(10), item(11) — all wire as [itemnum u16][npc u16][MENUITEM ×
    // itemnum]. We read each MENUITEM's leading u16 as the item id (stride computed from
    // the frame, robust to the element size we don't have a struct for). buy with
    // NC_ITEM_BUY_REQ {itemid, lot}. The SKILL shops (4/10) sell skill-scroll items
    // (e.g. Heal[12]=5451) — buy + useItem to LEARN the skill. Verified: Skill Master
    // Cyburn sends 0x3C0A (table-skill), which is why the old weapon/item-only list missed it.
    private static readonly ushort[] OpShopOpen =
        { 0x3C03, 0x3C04, 0x3C06, 0x3C09, 0x3C0A, 0x3C0B };
    // Soul-stone shop open (Menu cmd 5 = 0x3C05, NC_MENU_SHOPOPENSOULSTONE_CMD): a soul-stone
    // merchant's shop. It has its own payload (no item list) so it's NOT in OpShopOpen, but it
    // IS a real shop session — it accepts BOTH soul-stone buys AND item SELLs (verified in
    // SellAndInventoryManagement.pcapng). So it must flip the generic "shop is open" signal that
    // SELL gates on, else a sell into it is rejected 0x0383 ("shop not open").
    private const ushort OpShopOpenSoulStone = 0x3C05;
    // NC_MENU_RANDOMOPTION_CMD (Menu cmd 14 = 0x3C0E): a NON-shop NPC menu (the Anvil reforge/reroll
    // service). Clicking such an NPC returns this instead of a shop-open — the sync open flow uses it
    // to classify the NPC as "not a shop". (Catalogue the other niche NPC menus per the P1 ticket.)
    private const ushort OpMenuRandomOption = 0x3C0E;
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
    // Soul-stone USE result (verified C->S 0x5007 -> S->C 0x5008/0x5006 in this server's
    // packetlog): HP_USESUC 0x5008 (empty; the HP gain arrives via HPCHANGE 0x240E) = the
    // reserve had a charge and it was spent; USEFAIL 0x5006 (empty, SHARED HP+SP) = no charge
    // available — the reserve is empty (or on cooldown). Without tracking these the driver
    // re-fires 0x5007 every tick on an empty reserve forever (880 USE->USEFAIL pairs in one
    // capture) and never realises it must restock. SP_USESUC is 0x500A (distinct).
    private const ushort OpSoulStoneHpUseSuc = 0x5008;
    private const ushort OpSoulStoneSpUseSuc = 0x500A;
    private const ushort OpSoulStoneUseFail = 0x5006;
    // Soul-stone BUY fail (0x5005 NC_SOULSTONE_BUYFAIL_ACK {err u16}): the server's definitive
    // "no" to a 0x5001/0x5002 buy. Observed live 2026-07-01: err=0x0742 when the requested count
    // would exceed the max reserve (bot believed 0 held, server held 30, cap 37, asked for 37) —
    // without parsing this the restock loop re-fired the same doomed buy forever (~40 min stuck).
    private const ushort OpSoulStoneBuyFail = 0x5005;
    // Death/revive (Char dept): DEADMENU 0x104D = server opens the death menu (you died);
    // REVIVE_REQ 0x104E (C->S) = "move to respawn point" (-> nearest town); REVIVESAME 0x104F
    // = revived in place (e.g. a cleric's resurrection — no town trip). Auto-respawn caps at
    // ~2 min dead. Track Dead so behaviours can wait for an in-place revive vs respawn.
    private const ushort OpCharDeadMenu = 0x104D;
    private const ushort OpCharReviveSame = 0x104F;
    private const ushort OpCharReviveOther = 0x1050;
    // NC_CHAR_LEVEL_CHANGED_CMD (Char dept, cmd 116): {wmhandle u16, charNo u32, newLevel u8}.
    // Broadcast when ANY char levels; we update OUR level only when wmhandle == our WM handle.
    // Without this the bot's Level stays stale at the login value (it's only set from the WM
    // avatar list at login), so eligibleQuests filters on a wrong level and goal-detection (lvl 20) fails.
    private const ushort OpCharLevelChanged = 0x1074;
    // NC_CHAR_PROMOTE_ACK (Char cmd 89 = 0x1059): {newclass u8}. The server sends this on a JOB CHANGE
    // (the lvl-20/60/100 class advancement, done at the end of the JCQ turn-in) — the ONE packet that
    // actually changes our class. Followed by STAT_REMAINPOINT (0x105B) + CHANGEPARAMCHANGE (0x1035).
    // Ground truth: JCQ.pcapng, Fighter → newclass=2. See scratchpad/JCQ-INDEX.md.
    private const ushort OpCharPromoteAck = 0x1059;
    // NC_SCENARIO_AREAENTRY_REQ (Scenario cmd 5 = 0x6C05): {Name8 areaindex}. In a SCENARIO/instance the
    // server fires this when the player crosses into a named trigger area (e.g. "Zone_Mob01"); the client
    // MUST echo NC_SCENARIO_AREAENTRY_ACK (0x6C06, same areaindex) to arm that room (server then spawns its
    // mob wave). Reflexive, like a keepalive. Ground truth: JCQ.pcapng (5 rooms Zone_Mob01..05).
    private const ushort OpScenarioAreaEntryReq = 0x6C05;
    // NC_SCENARIO_OBJTYPECHANGE_CMD (Scenario cmd 11 = 0x6C0B): {handle u16, type u8}. The scenario script
    // (ServerSource JobChange1.ps) changes a scripted entity's kind: `change2mob`→type 5 (a fightable MOB),
    // `change2npc`→type 4 (a non-combatant NPC — the shadow clone once it FLEES/leaves the fight). We didn't
    // handle this at all, so a clone that turned into an NPC lingered in _nearby as a PHANTOM fightable clone
    // → the instance driver held/meleed it forever and never moved on to the real wave. (`vanish` removes it
    // via MAP_LOGOUT 0x1805, which IS handled.) Values derived from JobChange1.ps + JCQ.pcapng (port 9019).
    private const ushort OpScenarioObjTypeChange = 0x6C0B;
    private const byte ScenObjTypeMob = 5;   // change2mob → fightable
    private const byte ScenObjTypeNpc = 4;   // change2npc → non-combatant (clone leaving the fight)
    // NC_SCENARIO_DOORSTATE_CMD (Scenario cmd 9 = 0x6C09): {door u16 (entity HANDLE), doorstate u8}. The
    // JCQ instance (JobChange1.ps) is NOT open rooms — it's a LINEAR CORRIDOR gated by KQ_Gate4 DOORS the
    // script opens/closes per phase (`doorbuild`/`dooropen`/`doorclose`). This CMD is how the server tells
    // the client a door's state changed. We didn't decode it at all → the bot had ZERO runtime knowledge of
    // which corridor door is shut, so it kept pathing INTO a closed door and MOVEFAIL-thrashed to death
    // (the "wedge at (3726,3244) = closed Door3" JCQ blocker). Decode + track state-by-handle + correlate to
    // a position (via _npcs) so the nav can wait at a closed door instead of thrashing. State byte: observed
    // on the wire (log it), infer open/closed from the script's dooropen/doorclose ordering.
    private const ushort OpScenarioDoorState = 0x6C09;
    // NC_BRIEFINFO_BUILDDOOR_CMD (Briefinfo cmd 15 = 0x1C0F): {handle u16, mobid u16, coord, doorstate u8,
    // Name8 blockindex ("Door04"), scale u16}. Sent on zone-enter for EACH scenario door — the authoritative
    // link between a door's entity HANDLE, its .sbi block NAME, and its INITIAL open/closed state (e.g.
    // Job1_Dn01: Door02=0 closed, Door03=0 closed, Door04=1 open). We decode it to seed the by-name door
    // state that drives the pathfinding overlay (BlockGrid.SetDoorStates) — without it the grid can't know
    // which .sbi door a later 0x6C09 handle refers to, nor the initial states before any 0x6C09 fires.
    private const ushort OpBriefInfoBuildDoor = 0x1C0F;
    // NC_BAT_EXPGAIN_CMD (Bat cmd 11 = 0x240B): {expgain u32@0, mobhandle u16@4}. The server credits
    // exp per kill via this delta (it does NOT send an absolute NC_CHAR_EXP_CHANGED here), so we seed
    // the absolute at zone-enter and accumulate these to track live exp progress toward the next level.
    private const ushort OpBatExpGain = 0x240B;
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
    // NC_CHAR_CENCHANGE_CMD (Char 0x1033): {cen u64} = the new money ("cen") total. The
    // authoritative money signal — sent whenever money changes (sell/buy/quest reward/drop).
    // Without it the bot sells blind and can't tell a sell worked. Seeds Money for afford-checks.
    private static readonly ushort OpCenChange = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CENCHANGE_CMD>();
    // NC_ITEM_SELL_ACK (Item 0x3005): a 2-byte result code for our SELL_REQ. No PDB struct, but
    // the wire is unambiguous — a real client's successful sells return 0x0381; a rejected sell
    // (e.g. shop-not-open / bad lot) returns a different code (seen 0x0383). We record the raw
    // code so the driver/log can SEE whether a sell took, instead of firing it blind.
    private const ushort OpSellAck = 0x3005;
    // NC_ITEM_BUY_ACK (Item 0x3004): a 2-byte result code for our BUY_REQ. No PDB struct. Verified
    // live at the weapon smith: a successful buy returns 0x0201 (+ a CELLCHANGE adding the item); a
    // rejected buy returns 0x0204. Track it so buyGear/learnSkills don't mark a FAILED buy as bought.
    private const ushort OpItemBuyAck = 0x3004;
    // Self HP/SP change (BAT 0x240E/0x240F): the server's authoritative current HP/SP
    // after any change (combat damage, heal, regen, soul-stone). MaxHp/MaxSp come from
    // the [1802] login param block (seeded by the manager). These drive "HP-stone when
    // low" / "heal when low" scripts. Damage broadcasts (own swing 0x2448 + others'
    // 0x2449) carry attacker/defender/damage/resthp for the on_hit script hook.
    private static readonly ushort OpHpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_HPCHANGE_CMD>();
    private static readonly ushort OpSpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_SPCHANGE_CMD>();
    // NC_CHAR_CHANGEPARAMCHANGE_CMD (Char dept, cmd 53): a {paramId u8, value u32} list that carries the
    // char's MAX HP/SP (paramId 0x10/0x11) + stats (0x12+ = END/DEX/… — the P3). This is the AUTHORITATIVE
    // MID-ZONE source of MaxHp/MaxSp: it's sent on a level-up (beside the HP/SP refill) and at zone-enter,
    // so without it MaxHp/MaxSp only refreshed at the next handoff and lagged after a mid-zone level-up.
    private const ushort OpCharParamChange = 0x1035;
    private static readonly ushort OpSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SWING_DAMAGE_CMD>();
    private static readonly ushort OpSomeoneSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SOMEONESWING_DAMAGE_CMD>();
    // Ground loot: DROPEDITEM (Briefinfo 0x1C0A) broadcasts an item that hit the ground
    // (mob death or a player drop); MAP_LOGOUT (Map 0x1805) is the universal "this handle
    // left view" — for a ground item that means it was picked (by anyone) or despawned, so
    // it's how we retire a tracked drop (SOMEONEPICK carries no handle). PICK_ACK (Item
    // 0x300A) reports the result of OUR pick attempt. See the GroundItem/PickResult records.
    private static readonly ushort OpDropedItem = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_DROPEDITEM_CMD>();
    private static readonly ushort OpMapLogout = PacketRegistry.GetOpcode<PROTO_NC_MAP_LOGOUT_CMD>();
    private static readonly ushort OpPickAck = PacketRegistry.GetOpcode<PROTO_NC_ITEM_PICK_ACK>();
    // Result of the bot's inventory auto-sort (NC_ITEM_AUTO_ARRANGE_INVEN_ACK, Item 0x304B); the new
    // bag layout arrives as the ensuing CELLCHANGE burst, already applied by the item model.
    private static readonly ushort OpSortAck = PacketRegistry.GetOpcode<PROTO_NC_ITEM_AUTO_ARRANGE_INVEN_ACK>();
    // Learned-skill list, sent at zone login (NC_CHAR_CLIENT_SKILL_CMD, Char 0x0F3D):
    // [restempow:1][PartMark:1][nMaxNum:2][chrregnum:4][number:2][SKILLREADBLOCK × number].
    // Each SKILLREADBLOCK is 12 bytes; its leading u16 is the skill id. This is how the bot
    // learns which skills it actually has (heal, buffs, attacks) — read from the wire, not
    // hard-coded. Names resolve via client ActiveSkill (ClientData.SkillName).
    private static readonly ushort OpClientSkill = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_SKILL_CMD>();
    // NC_CHAR_CLIENT_PASSIVE_CMD (CHAR dept 4, cmd 62 = 0x103E) — the login PASSIVE-skill list, sent
    // right after the active list (0x103D). NOT in FiestaLib's registry (only the 0x100E update variant
    // is), so it arrives unnamed — hand-parse it: {number u16 @0, passive u16[number] @2}. Verified live:
    // IkFresh sent 01 00 09 00 = 1 passive, id 9 = One Handed Sword Mastery [01].
    private const ushort OpClientPassive = 0x103E;
    // NC_SKILL_SKILL_LEARNSUC_CMD (SKILL dept 18, cmd 4) — server confirms a skill was learned.
    private const ushort OpSkillLearnSuc = 0x4804;
    // NC_SKILL_SKILL_LEARNFAIL_CMD (cmd 5) — server REJECTED a learn (carries the reason err code).
    private const ushort OpSkillLearnFail = 0x4805;
    // NC_ITEM_USE_ACK (ITEM dept 12, cmd 22) — result of using an item (e.g. a skill scroll).
    private const ushort OpItemUseAck = 0x3016;
    // Quest dialogue: the server drives accept/turn-in via NC_QUEST_SCRIPT_CMD_REQ (0x4401)
    // {questId u16, STRUCT_QSC}; the QSC command code is the first byte of STRUCT_QSC (payload
    // offset 2). The client answers QUEST_SCRIPT_CMD_ACK with {questId, nQSC=code, nResult}.
    private static readonly ushort OpQuestScriptReq = PacketRegistry.GetOpcode<PROTO_NC_QUEST_SCRIPT_CMD_REQ>();
    private const int SkillListHeaderLen = 10; // restempow+PartMark+nMaxNum+chrregnum+number
    private const int SkillBlockLen = 12;

    private readonly BotSession _session;
    private readonly Action<string>? _log;            // Note channel (also fans out to host stdout)
    private readonly Action<BotLogLevel, string>? _logLevel; // leveled channel for verbose perception spam
    private readonly ConcurrentDictionary<ushort, NearbyPlayer> _nearby = new();
    private readonly ConcurrentDictionary<ushort, NearbyNpc> _npcs = new();   // ⚠ view-scoped (pruned by
    // BRIEFINFODELETE as you move). Use ONLY for live in-view things (combat targets, the nearby gate
    // HANDLE to click). For "where is NPC X on this map" use _npcSeed (below), not this.
    // STICKY recently-seen mob cache (2026-07-15) — the fix for the instance AoI-flicker combat stall
    // ([[fiesta-instance-roomba-coverage]]): in Job1_Dn01 a mob melee-hits us then 159ms later sends
    // MAP_LOGOUT 0x1805 (leaves our AoI) then REGENs — the constant blink meant the bot DROPPED the target
    // every flicker (BRIEFINFODELETE/MAP_LOGOUT hard-removed it), so it re-acquired/re-faced/re-cast slower
    // than the flicker interval → 0 kills, casts fail out-of-range 0x0FCA on the departed handle. Fix: an
    // AoI-leave (BriefDelete/MapLogout) moves a mob HERE (last pos + a short expiry) instead of dropping it,
    // so combat keeps holding the target through the blink; a re-appear (AddOrUpdateNpc) or a real death
    // (REALLYKILL) removes it. NearbyNpcs returns _npcs ∪ non-expired _recentNpcs. Value = (npc, expiryTick).
    private readonly ConcurrentDictionary<ushort, (NearbyNpc Npc, long Expiry)> _recentNpcs = new();
    private const int RecentNpcTtlMs = 4000; // long enough to bridge the ~200ms flicker; short enough that a
                                             // genuinely-departed mob is dropped fast (no chasing a ghost).
    // ✅ THE NPC SEED — the single authoritative full-map roster, keyed by mobId, holding position + the
    // gate flag + link-destination map. Populated by the bulk 0x1C09 NC_BRIEFINFO_MOB_CMD on map-enter
    // (ALL NPCs+gates at infinite range, as on the minimap) and any later 0x1C09/REGENMOB. Cleared on
    // map-change, NOT pruned on BRIEFINFODELETE (NPCs are static). SOURCE OF TRUTH for NPC + gate
    // positions — navigation (quest giver / merchant / gate / cross-map hop) reads from HERE.
    private readonly ConcurrentDictionary<int, NpcSeedEntry> _npcSeed = new();
    // Scenario DOOR state, keyed by the door entity HANDLE (from 0x6C09 NC_SCENARIO_DOORSTATE_CMD). Value =
    // last-seen {state byte, last-known position}. Position is captured from _npcs at the time the door state
    // changed (doors spawn as tracked entities); if the handle isn't in _npcs the position is null and only
    // the state is known. Cleared on map handoff. Nav reads this (via bot.doorStates) to avoid pathing into a
    // CLOSED corridor door. Data-driven — no baked door coords (the .sbi gives static centres; this gives
    // which of them is open/closed RIGHT NOW).
    private readonly ConcurrentDictionary<ushort, DoorState> _doorStates = new();
    // Scenario door HANDLE -> .sbi block NAME ("Door04"), from 0x1C0F BUILDDOOR. Bridges a 0x6C09 DOORSTATE
    // (handle-keyed) to the .sbi door box so the pathfinding overlay knows which door changed. Cleared on map handoff.
    private readonly ConcurrentDictionary<ushort, string> _doorNames = new();
    // Scenario door NAME -> current doorstate byte (0 closed / 1 open). Seeded by BUILDDOOR, updated by DOORSTATE.
    // The pathfinder reads this (via DoorStatesByNameChanged → BlockGrid.SetDoorStates) so closed doors become
    // walls in our collision, matching the server. Cleared on map handoff.
    private readonly ConcurrentDictionary<string, byte> _doorStateByName = new();
    private readonly ConcurrentDictionary<byte, ushort> _inventory = new(); // bag slot -> itemId
    private readonly ConcurrentDictionary<byte, int> _invCount = new();      // bag slot -> stack count
    private readonly ConcurrentDictionary<byte, ushort> _equipment = new(); // equip slot -> itemId
    private readonly ConcurrentDictionary<ushort, GroundItem> _drops = new(); // ground-item handle -> drop
    private readonly ConcurrentDictionary<ushort, byte> _skills = new(); // learned ACTIVE skill id -> 1 (set)
    private readonly ConcurrentDictionary<ushort, byte> _passives = new(); // learned PASSIVE skill id -> 1 (set)
    private ushort? _mountHandle; // last known mount mover handle (from RIDE_ON 0xCC02 payload)

    public ZoneView(BotSession session, Action<string>? log = null, Action<BotLogLevel, string>? logLevel = null)
    {
        _session = session;
        _log = log;
        _logLevel = logLevel;
        _session.PacketReceived += OnPacket;
    }

    // Verbose (per-frame perception) log: mob/player appeared, MOVEFAIL, speed changes — the
    // firehose that would otherwise drown the headline events. Routes to the leveled channel at
    // Verbose; falls back to the plain Note channel if no leveled logger was supplied.
    private void LogV(string m) { if (_logLevel is not null) _logLevel(BotLogLevel.Verbose, m); else _log?.Invoke(m); }

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
    public IReadOnlyCollection<NearbyNpc> NearbyNpcs
    {
        get
        {
            // Live in-view mobs PLUS recently-seen ones still within their sticky TTL (bridges the instance
            // AoI-flicker so combat can hold a target through the blink). Expired entries are pruned here.
            if (_recentNpcs.IsEmpty) return _npcs.Values.ToArray();
            long now = Environment.TickCount64;
            var result = new Dictionary<ushort, NearbyNpc>();
            foreach (var kv in _recentNpcs)
            {
                if (kv.Value.Expiry <= now) { _recentNpcs.TryRemove(kv.Key, out _); continue; }
                result[kv.Key] = kv.Value.Npc;
            }
            foreach (var kv in _npcs) result[kv.Key] = kv.Value; // live entries win over stale sticky ones
            return result.Values.ToArray();
        }
    }

    /// <summary>A mob leaving our AoI (BRIEFINFODELETE / MAP_LOGOUT — NOT death) → move it to the sticky
    /// recently-seen cache instead of dropping it, so combat holds the target through an instance flicker.
    /// Only real combat mobs are stickied (not gates or static NPCs); the removal from <c>_npcs</c> already
    /// happened via the caller's TryRemove. A re-appear or a REALLYKILL death evicts it.</summary>
    private void StashRecentNpc(ushort hnd, NearbyNpc npc)
    {
        if (npc.Flag == 1) return;                              // a gate, not a combat target
        if (IsHuntableMob is { } huntable && !huntable(npc.MobId)) return; // a static/friendly NPC
        _recentNpcs[hnd] = (npc, Environment.TickCount64 + RecentNpcTtlMs);
        LogV($"[ZoneView] mob h={hnd} id={npc.MobId} left AoI — stickied {RecentNpcTtlMs}ms (flicker bridge)");
    }

    /// <summary>Live scenario corridor DOOR states (0x6C09), keyed by door handle. The instance nav reads
    /// these (via bot.doorStates) to hold at a closed door instead of MOVEFAIL-thrashing through it.</summary>
    public IReadOnlyCollection<DoorState> DoorStates => _doorStates.Values.ToArray();

    /// <summary>Live scenario door states keyed by <c>.sbi</c> block NAME ("Door04") → doorstate byte (0
    /// closed / 1 open), from 0x1C0F BUILDDOOR (initial) + 0x6C09 DOORSTATE (updates). The pathfinding
    /// door-collision overlay consumes this so closed doors become walls in our grid, matching the server.</summary>
    public IReadOnlyDictionary<string, byte> DoorStatesByName => new Dictionary<string, byte>(_doorStateByName);

    /// <summary>Raised whenever a scenario door's state changes (BUILDDOOR seed or DOORSTATE update), carrying
    /// the full current name→state snapshot. The manager wires this to <c>BlockGrid.SetDoorStates</c> so the
    /// pathfinder's collision tracks the doors live (the fix for the JCQ instance MOVEFAIL storm).</summary>
    public event Action<IReadOnlyDictionary<string, byte>>? DoorStatesByNameChanged;

    /// <summary>✅ (x,y) of an NPC by mobId from the authoritative map-enter SEED (bulk 0x1C09 at infinite
    /// range) — the source of truth for "where is NPC X on this map." null if the seed has no such NPC on
    /// the current map (it's on another map — don't fall back to stale coords). Use to walkTo any quest
    /// giver / merchant / gate without hardcoded coords and without having seen it.</summary>
    public (uint X, uint Y)? NpcPosition(int mobId)
        => _npcSeed.TryGetValue(mobId, out var e) ? (e.X, e.Y) : null;

    /// <summary>The full seed entry for an NPC/gate by mobId (position + gate flag + link-destination map),
    /// or null if not on the current map's seed.</summary>
    public NpcSeedEntry? Npc(int mobId) => _npcSeed.TryGetValue(mobId, out var e) ? e : null;

    /// <summary>The full map-enter NPC seed roster (all NPCs+gates the server broadcast on map-enter).</summary>
    public IReadOnlyCollection<NpcSeedEntry> NpcSeed => _npcSeed.Values.ToArray();
    /// <summary>Gate entries in the seed: linkMap -> (x,y) — the LIVE current-map gate positions, better
    /// than the static MapLink/MapWayPoint SHN coords for taking a gate. (Current map only.)</summary>
    public IReadOnlyList<(string LinkMap, uint X, uint Y)> SeedGates()
        => _npcSeed.Values.Where(e => e.IsGate && !string.IsNullOrEmpty(e.LinkMap))
                          .Select(e => (e.LinkMap!, e.X, e.Y)).ToArray();
    /// <summary>Count of NPCs in the current map's seed roster (for logging/diagnostics).</summary>
    public int NpcSeedCount => _npcSeed.Count;
    public ChatMessage? LastChat { get; private set; }

    /// <summary>Handle of the most recently killed entity (from REALLYKILL) — lets a grind
    /// script confirm a kill landed and move on without waiting for the despawn. Only set when
    /// the bot itself was the attacker (so a script counts its own kills, not a passer-by's).</summary>
    public ushort LastKill { get; private set; }

    /// <summary>Count of mobs the bot itself killed (REALLYKILL with attacker == self). The
    /// authoritative "I got the killing blow" signal — quest/XP credit only comes from these,
    /// not from a mob merely disappearing (despawn, or another player's kill).</summary>
    public int KillsByMe { get; private set; }

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

    /// <summary>Unit price (cen) of one HP/SP soul-stone charge, as sent by the healer's
    /// soul-stone shop-open (0x3C05 SOULSTONEMENU = {price u32, max u32, cur u32}). 0 until a
    /// soul-stone shop has been opened. The restock logic reads this to buy the MAX AFFORDABLE
    /// charges — <c>min(deficit, money / price)</c> — instead of asking for the full deficit and
    /// having the server silently reject the buy when <c>price*count &gt; money</c>.</summary>
    public uint HpStonePrice { get; private set; }
    public uint SpStonePrice { get; private set; }

    /// <summary>Current soul-stone reserve charges (HP/SP), as last reported by a BUY_ACK
    /// (0x5003/0x5004, <c>totalnumber</c>). Null until a buy ack is seen (the initial count
    /// from the login char-info isn't decoded yet — TODO). The restock SM gates on this.</summary>
    public int? HpStones { get; private set; }
    public int? SpStones { get; private set; }

    /// <summary>True once an HP soul-stone USE failed (USEFAIL 0x5006) — the reserve is empty
    /// (or on cooldown), so further <c>UseSoulStoneHpAsync</c> calls are pointless until the bot
    /// restocks. Cleared by a successful HP USE (USESUC 0x5008) or an HP BUY ack (0x5003, reserve
    /// refilled). The driver gates healing on this so it stops spamming 0x5007 on an empty reserve
    /// and instead goes to a healer to restock.</summary>
    public bool HpStoneDepleted { get; private set; }

    /// <summary>SP analogue of <see cref="HpStoneDepleted"/> (USEFAIL attributed to an SP USE).
    /// Cleared by SP USESUC (0x500A), an SP BUY ack (0x5004) or a non-zero SP seed.</summary>
    public bool SpStoneDepleted { get; private set; }

    // USEFAIL (0x5006) carries NO hp/sp marker — but WE know which USE we fired. Each outbound
    // 0x5007/0x5009 is noted here and its result (0x5008/0x500A/0x5006) pops the oldest pending
    // entry, so a fail is attributed to the USE that caused it. Before this, EVERY USEFAIL was
    // assumed to be HP ("a melee bot never uses SP stones") — dead wrong once the proactive SP
    // top-up shipped: SP USEFAILs (empty SP reserve) zeroed a real 30-charge HP reserve, which
    // sent the bot on a doomed over-cap restock loop (live 2026-07-01, ~40 min stuck in town).
    private readonly Queue<(bool Hp, DateTime AtUtc)> _pendingStoneUse = new();

    /// <summary>Note an outbound soul-stone USE (0x5007 hp / 0x5009 sp) so its result packet can
    /// be attributed to the right pool. Called by BotManager at the send site.</summary>
    public void NoteStoneUseFired(bool hp)
    {
        lock (_pendingStoneUse)
        {
            _pendingStoneUse.Enqueue((hp, DateTime.UtcNow));
            while (_pendingStoneUse.Count > 8) _pendingStoneUse.Dequeue(); // bound stale build-up
        }
    }

    /// <summary>Pop the pending-USE kind for an arriving USE result. Null when nothing (recent)
    /// is pending — e.g. a result for a USE fired before a reconnect.</summary>
    private bool? PopStoneUseKind()
    {
        lock (_pendingStoneUse)
        {
            while (_pendingStoneUse.Count > 0)
            {
                var (hp, at) = _pendingStoneUse.Dequeue();
                if (DateTime.UtcNow - at < TimeSpan.FromSeconds(5)) return hp;
                // stale (reply lost / never came) — skip and keep looking
            }
            return null;
        }
    }

    /// <summary>Monotonic count of soul-stone BUY failures (0x5005) + the last error code. The
    /// script correlates a fired buy with these (record the count before firing; it increased =
    /// THIS buy was refused) instead of waiting forever for a BUY_ACK that will never come.</summary>
    public int StoneBuyFailCount { get; private set; }
    public ushort LastStoneBuyFailErr { get; private set; }

    // ── Pick pacing (operator 2026-07-02): the server processes ONE item-cell pick at a time —
    // the flow must be pick→ack→pick→ack, never a burst of picks. Polling model (NOT synchronous):
    // the driver checks CanPick each tick, fires ONE pick (which sets PickPending), and the pick
    // ack (OpPickAck) clears it. The 2s staleness escape covers a lost/never-sent ack so a dropped
    // frame can't freeze looting forever.
    public bool PickPending { get; private set; }
    public DateTime PickSentUtc { get; private set; } = DateTime.MinValue;
    public bool CanPick => !PickPending || (DateTime.UtcNow - PickSentUtc) > TimeSpan.FromSeconds(2);

    /// <summary>Called at the PICK_REQ send site (BotManager) — arms the pick-ack pacing gate.</summary>
    public void MarkPickSent() { PickPending = true; PickSentUtc = DateTime.UtcNow; }

    public void SeedMaxStones(uint? maxHpStones, uint? maxSpStones)
    {
        if (maxHpStones is { } h && h > 0) MaxHpStones = h;
        if (maxSpStones is { } s && s > 0) MaxSpStones = s;
    }

    /// <summary>Seed the CURRENT soul-stone reserve counts from the zone-enter char-info
    /// (NC_CHAR_BASE, decoded in <see cref="Zone.ZoneEntry"/>). This is the authoritative starting
    /// reserve — without it the bot can't tell "reserve full" from "empty", spam-USEs at full HP
    /// (which fails), and over-buys past the cap. A non-zero count also clears any stale
    /// depletion flag. -1/null leaves the count unknown.</summary>
    public void SeedStones(int? hpStones, int? spStones)
    {
        if (hpStones is { } h && h >= 0) { HpStones = h; if (h > 0) HpStoneDepleted = false; }
        if (spStones is { } s && s >= 0) { SpStones = s; if (s > 0) SpStoneDepleted = false; }
    }

    /// <summary>Raised when the bot's own HP changes (HPCHANGE 0x240E), with the new
    /// current HP. The combat/script layer reacts (heal / HP soul-stone when low).</summary>
    /// <summary>Raised when the bot's OWN level changes (NC_CHAR_LEVEL_CHANGED_CMD for our WM
    /// handle) — carries the new level. BotManager updates BotHandle.Level off this.</summary>
    public event Action<byte>? LevelChanged;

    /// <summary>Raised on a JOB CHANGE (NC_CHAR_PROMOTE_ACK) — carries the new class id. BotManager
    /// updates BotHandle.Class off this so class-appropriate quest-reward selection and goal-detection
    /// track the promotion. The last promoted class is also kept in <see cref="PromotedClass"/>.</summary>
    public event Action<byte>? Promoted;
    /// <summary>The class id from the most recent NC_CHAR_PROMOTE_ACK this session, or null if we
    /// haven't seen a promotion (the char-select ClassID on BotHandle remains the baseline).</summary>
    public byte? PromotedClass { get; private set; }

    /// <summary>The most recent scenario trigger-area we entered + acked (e.g. "Zone_Mob01"), or null if
    /// not in a scenario/instance. The clear-room driver watches this to know a room's mob wave is armed.</summary>
    public string? LastScenarioArea { get; private set; }
    /// <summary>Latches true once we're inside a scenario instance (any AREAENTRY seen) and stays true across
    /// between-room gaps where <see cref="LastScenarioArea"/> flips null; reset on map handoff. Nav uses it to
    /// NOT learn a MOVEFAIL as a permanent wall inside an instance — the block is a dynamic scenario DOOR
    /// (KQ_Gate4, opens/closes per the script), not a static obstacle, so learning it poisons the grid and the
    /// bot can never path through once the door opens (the JCQ stuck-at-Door3 grid-poison).</summary>
    public bool InScenarioInstance { get; private set; }
    // Count of REGENMOB (0x1C08) received — a monotonic "a wave just spawned" signal the AREAENTRY_ACK re-send
    // loop watches to know the room's interrupt armed (stop re-sending).
    private long _scenarioRegenCount;
    /// <summary>Raised when we auto-ack a scenario AREAENTRY (carries the area name) — a new instance room armed.</summary>
    public event Action<string>? ScenarioAreaEntered;
    // Scenario areas we've ARRIVED IN and ACKED (name → 1). Since the ack only fires once we're shove-free =
    // parked at a server-valid position INSIDE the trigger (no desync — we detect MOVEFAILs), the moment we
    // send the first ack for area A is the authoritative "we entered + handled A" signal (operator 2026-07-15:
    // "treat sending the acks as the real completion, the moment the first one is sent" + "when you ACK inside
    // area A count A as done"). The instance driver's visited-set consumes THIS (via bot.scenarioAckedAreas)
    // instead of the flaky proximity / LastScenarioArea-flip heuristic that mis-marked co-armed areas. Cleared
    // on map handoff.
    private readonly ConcurrentDictionary<string, byte> _scenarioAckedAreas = new();
    /// <summary>Scenario areas we've arrived-in and acked this instance run (authoritative "area done" set).</summary>
    public IReadOnlyCollection<string> ScenarioAckedAreas => _scenarioAckedAreas.Keys.ToArray();
    /// <summary>(areaName,(x,y)) → is the point inside that scenario area's <c>.aid</c> box? Set by the manager.
    /// Used to HOLD the AREAENTRY_ACK until we're genuinely INSIDE the room. Proven on the wire 2026-07-13: the
    /// server fires the room's interrupt (SkelRegen) on an ACK whose position is inside the area — the bot was
    /// reflexive-acking Zone_Mob02 from ROOM 1 (x≈1546, before Door2@2098) so it never fired; the real client
    /// walks in and acks from x≈3110 (inside). Ack-from-inside = the fix.</summary>
    public Func<string, (uint X, uint Y), bool>? IsInsideScenarioArea { get; set; }

    public event Action<uint>? HpChanged;

    /// <summary>Raised when the bot's own SP changes (SPCHANGE 0x240F).</summary>
    public event Action<uint>? SpChanged;

    /// <summary>Raised for every combat hit broadcast in view (own swing + others').
    /// The "process every hit" seam — scripts filter by attacker/defender vs self.</summary>
    public event Action<HitInfo>? Damaged;

    private readonly ConcurrentDictionary<ushort, DateTime> _aggressors = new();      // confident: hit us / clearly running at us
    private readonly ConcurrentDictionary<ushort, DateTime> _maybeAggressors = new();  // running our way, but a player shares the angle
    private static readonly TimeSpan CombatWindow = TimeSpan.FromSeconds(8);

    /// <summary>Mobs we're confident are aggroing us within the combat window — hit us
    /// (incoming SWING_DAMAGE, defender==self) or ran unambiguously at us.</summary>
    public IReadOnlyCollection<ushort> Aggressors =>
        _aggressors.Where(kv => DateTime.UtcNow - kv.Value < CombatWindow).Select(kv => kv.Key).ToArray();

    /// <summary>Mobs running roughly toward us but where a nearby player shares the heading,
    /// so the target is uncertain — "maybe aggro'd me". Promote to <see cref="Aggressors"/>
    /// if one then hits us.</summary>
    public IReadOnlyCollection<ushort> MaybeAggressors =>
        _maybeAggressors.Where(kv => DateTime.UtcNow - kv.Value < CombatWindow).Select(kv => kv.Key).ToArray();

    /// <summary>True if the bot has been hit in the last few seconds — i.e. it's taking
    /// damage. IMPORTANT: a clean logout will NOT complete while in combat (the server's
    /// logout countdown resets on damage) — flee out of enemy range first. Used to gate
    /// safe-logout and to know when to disengage/heal.</summary>
    public bool InCombat => DateTime.UtcNow - LastHitAtUtc < CombatWindow;

    /// <summary>When the bot was last hit (UtcMinValue if never).</summary>
    public DateTime LastHitAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>When the bot last LANDED a hit on something (Attacker==self in a SWING_DAMAGE/
    /// SOMEONESWING_DAMAGE broadcast) — UtcMinValue if never. Distinct from <see cref="LastHitAtUtc"/>
    /// (us being hit): a mob that never retaliates (weak/passive, or a facing-bug false negative)
    /// would never trip <see cref="InCombat"/>, even while we're genuinely damaging it every swing.
    /// Operator 2026-07-01: "there are packets that show us [the] enemy is taking damage, so keep
    /// trying so long as any damage happened in the last 15s" — the "un-killable, give up" guard
    /// must check damage dealt OR received, not just received.</summary>
    public DateTime LastDamageDealtAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>When the bot last landed a CONNECTING hit (Attacker==self AND Damage&gt;0) — distinct from
    /// <see cref="LastDamageDealtAtUtc"/> which fires on any self-swing including a whiff/out-of-range
    /// (Damage==0). Lets the driver confirm a kite-chip damage skill actually connected vs missed.</summary>
    public DateTime LastRealDamageDealtAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>LEARNED effective attack range (operator 2026-07-15): the max distance at which our OWN swing
    /// has CONNECTED (SWING_DAMAGE Attacker==self, Damage&gt;0). The real weapon range is not in any client
    /// file (ItemInfo has WeaponType but no range column; no client PDB) — so we measure it from the wire, the
    /// golden-rule way. 0 until the first connecting hit. Feeds the combat standoff so the bot stops at real
    /// weapon range instead of overlapping the mob at ~1u (the 0x0FCA "out of range" wedge). For a melee char
    /// every swing is ~weapon range so the max ≈ the range; a clamp excludes any long-range skill damage.</summary>
    public double LearnedMeleeRange { get; private set; }

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
            // Combat-START marker for the tail: a hit arriving after a CombatWindow gap is a fresh
            // engagement. Just a LOG line (no metric state kept here — pair START↔KILLED/DIED
            // timestamps in the tail to read time-to-kill / time-to-death and spot a death-loop).
            if (DateTime.UtcNow - LastHitAtUtc > CombatWindow)
                _log?.Invoke($"[combat] START vs mob h={h.Attacker}");
            _aggressors[h.Attacker] = DateTime.UtcNow;
            LastHitAtUtc = DateTime.UtcNow;
        }
        if (SelfHandle is { } me && h.Attacker == me)
        {
            LastDamageDealtAtUtc = DateTime.UtcNow;
            // A CONNECTING hit (Damage>0) vs a whiff/out-of-range (Damage==0). LastDamageDealtAtUtc fires
            // on any self-swing; this one only on real damage — so the driver can tell a kite-chip skill
            // actually landed (operator 2026-07-07: "check it didn't miss via packets") vs it whiffed.
            if (h.Damage > 0)
            {
                LastRealDamageDealtAtUtc = DateTime.UtcNow;
                // LEARN THE ATTACK RANGE from the wire (operator 2026-07-15): the distance at which our swing
                // CONNECTS is the effective weapon range. Read self + defender positions at this moment; the max
                // connecting distance ≈ the range. Clamp excludes garbage / long-range skill damage (melee ≪ 150u).
                if (SelfPositionProvider?.Invoke() is { } sp)
                {
                    double dx = double.NaN, dy = double.NaN;
                    if (_npcs.TryGetValue(h.Defender, out var dn)) { dx = dn.X; dy = dn.Y; }
                    else if (_nearby.TryGetValue(h.Defender, out var dp)) { dx = dp.X; dy = dp.Y; }
                    if (!double.IsNaN(dx))
                    {
                        var ddx = (double)sp.X - dx; var ddy = (double)sp.Y - dy;
                        var dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dist > 0 && dist < 150 && dist > LearnedMeleeRange + 0.5)
                        {
                            LearnedMeleeRange = dist;
                            _log?.Invoke($"[combat] LEARNED attack-range ↑ {dist:F0}u (connecting swing on h={h.Defender}, dmg={h.Damage}) — new session max");
                        }
                    }
                }
            }
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
    public void ClearServerMenu() { ServerMenuOpen = false; ServerMenuTitle = null; ServerMenuOptions = Array.Empty<ServerMenuOption>(); }

    /// <summary>The prompt text of the currently-open server menu (0x3C01), e.g. "Do you want to
    /// move to Roumen field?" or a quest confirm. Null when none open. Lets the answerer pick the
    /// right option by MEANING (parse the options below) instead of guessing a fixed reply byte.</summary>
    public string? ServerMenuTitle { get; private set; }

    /// <summary>The options of the open server menu (0x3C01), each = the reply byte to send in
    /// SERVERMENU_ACK (0x3C02) to SELECT it + its display text (e.g. {0,"Yes"},{1,"No"}). Parsed
    /// from the SERVERMENU[menunum] array. Empty when no menu is open. The answerer matches an
    /// option's Text to choose its Reply — never hardcode the reply value.</summary>
    public IReadOnlyList<ServerMenuOption> ServerMenuOptions { get; private set; } = Array.Empty<ServerMenuOption>();

    /// <summary>The reply byte for the FIRST option whose text matches any of <paramref name="wants"/>
    /// (case-insensitive substring), or null if none match. e.g. <c>ServerMenuReplyFor("yes")</c> for a
    /// gate confirm, or a quest-title / "quest" keyword to reach a service NPC's quest dialogue.</summary>
    public byte? ServerMenuReplyFor(params string[] wants)
    {
        foreach (var o in ServerMenuOptions)
            foreach (var w in wants)
                if (!string.IsNullOrEmpty(o.Text) && o.Text.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return o.Reply;
        return null;
    }

    private volatile ushort[] _shopItems = Array.Empty<ushort>();

    /// <summary>The item ids the last-opened merchant sells (from SHOPOPEN). Empty until
    /// a shop is opened (click a merchant). <see cref="Manager.BotManager.BuyAsync"/> buys
    /// any of these by id.</summary>
    public IReadOnlyList<ushort> ShopItems => _shopItems;

    /// <summary>The npc handle of the last-opened shop (0 if none).</summary>
    public ushort ShopNpc { get; private set; }

    /// <summary>UTC of the last shop-open packet (item 0x3C0x OR soul-stone 0x3C05). A SELL is only
    /// accepted while a shop is genuinely open — firing into a closed shop is rejected 0x0383. The
    /// open-shop flow waits on this; <see cref="ShopOpen"/> is the recency view used to gate sells.</summary>
    public DateTime ShopOpenUtc { get; private set; }
    /// <summary>True if a shop opened recently (within ~10s) and we haven't left the map / been
    /// rejected since — i.e. a SELL should be accepted now.</summary>
    public bool ShopOpen => (DateTime.UtcNow - ShopOpenUtc) < TimeSpan.FromSeconds(10);

    /// <summary>UTC of the last NC_MENU_RANDOMOPTION_CMD (0x3C0E) — a NON-shop NPC menu (e.g. the
    /// RouN Anvil: reforge/reroll item stats, costs a Hammer of Bijou + premium currency). The
    /// sync open flow treats this as "this NPC is NOT a shop" (vs a shop-open packet). Reset per open.</summary>
    public DateTime RandomOptionUtc { get; private set; }

    /// <summary>The KIND of the last shop that opened, derived from the shop-open opcode (skill
    /// master / smith / item merchant / soul-stone healer). Lets the driver classify an NPC's
    /// service by what it sends when clicked — no hardcoded NPC ids. Unknown until a shop opens.</summary>
    public ShopKind LastShopKind { get; private set; } = ShopKind.Unknown;

    /// <summary>Reset the shop/menu-open signals to "nothing opened" — called BEFORE each open attempt
    /// so the result reflects ONLY the current NPC click (a proper sync request→response), never a
    /// stale recency window. The old 10s window mis-tagged the Anvil as a weapon shop because it was
    /// probed within 10s of the adjacent smith. (operator 2026-06-30.)</summary>
    public void ResetShopState()
    {
        ShopOpenUtc = DateTime.MinValue;
        RandomOptionUtc = DateTime.MinValue;
        LastShopKind = ShopKind.Unknown;
    }

    /// <summary>True while an NPC menu prompt is open and unanswered (server sent
    /// NPCMENUOPEN_REQ after we clicked a merchant/script NPC). The shop-open flow replies
    /// with NPCMENUOPEN_ACK to advance to the sell list.</summary>
    public bool NpcMenuOpen { get; private set; }

    /// <summary>The NPC mobId the last 0x201C menu belongs to (its payload = the NPC mobId). Needed
    /// to drive a multi-quest giver: SELECT_START_REQ keys the quest by this NPC id, not the entity
    /// handle.</summary>
    public ushort MenuNpcId { get; private set; }

    /// <summary>Mark the NPC menu answered (after sending NPCMENUOPEN_ACK / SELECT_START_REQ).</summary>
    public void ClearNpcMenu() { NpcMenuOpen = false; MenuNpcId = 0; }

    /// <summary>Raised when a merchant's shop opens, with the sell-list item ids.</summary>
    public event Action<IReadOnlyList<ushort>>? ShopOpened;

    /// <summary>Current money ("cen"). SEEDED at zone-enter from NC_CHAR_BASE (0x1038, Cen@58) so it's
    /// never unknown, then kept current by NC_CHAR_CENCHANGE_CMD (0x1033). -1 only if neither was seen
    /// yet. Gates afford-checks (skills/gear/stones) and confirms a SELL paid out (Money rises).</summary>
    public long Money { get; private set; } = -1;

    /// <summary>Seed money from the zone-enter char-info (NC_CHAR_BASE Cen). Money is always in the
    /// login data, so the bot should know it immediately — not wait for the first transaction.</summary>
    public void SeedMoney(long cen) => Money = cen;

    /// <summary>Current total experience. SEEDED at zone-enter from NC_CHAR_BASE (0x1038, Experience@26)
    /// then kept current by adding each NC_BAT_EXPGAIN_CMD (0x240B) kill credit. -1 until seeded. Lets
    /// the bot SEE grind progress (the server doesn't send an absolute NC_CHAR_EXP_CHANGED here).</summary>
    public long Exp { get; private set; } = -1;
    /// <summary>Experience gained since this zone session started (Σ of EXPGAIN credits) — progress rate.</summary>
    public long SessionExpGained { get; private set; }
    /// <summary>Seed the absolute exp from the zone-enter char-info (NC_CHAR_BASE Experience).</summary>
    public void SeedExp(long exp) => Exp = exp;

    /// <summary>The raw 2-byte code from the last NC_ITEM_SELL_ACK (0x3005), or -1 if none yet.
    /// 0x0381 = the success code a real client sees; a different code (e.g. 0x0383) = rejected.</summary>
    public int LastSellAck { get; private set; } = -1;
    /// <summary>UTC time of the last SELL_ACK — lets the driver wait for the result of a sell.</summary>
    public DateTime LastSellAckUtc { get; private set; }
    /// <summary>The raw 2-byte code from the last NC_ITEM_BUY_ACK (0x3004), or -1 if none yet.
    /// 0x0201 = success (the item was added); anything else (e.g. 0x0204) = rejected. Lets the driver
    /// confirm a buy actually took before marking it bought/learned.</summary>
    public int LastBuyAck { get; private set; } = -1;
    /// <summary>UTC time of the last BUY_ACK — lets the driver wait for / pace on a buy result.</summary>
    public DateTime LastBuyAckUtc { get; private set; }
    /// <summary>Monotonic count of BUY_ACKs (0x3004) seen this session. Lets the driver correlate a
    /// fired buy to ITS ack (record the count before buying; a new ack arrived once it goes up) instead
    /// of racing on the shared <see cref="LastBuyAck"/> value — so a buy with NO ack (shop closed) isn't
    /// mistaken for the previous buy's result.</summary>
    public int BuyAckCount { get; private set; }

    /// <summary>Error code of the last NC_ITEM_USE_ACK (0x700 ok, 0x708 skill-level-too-low,
    /// 0x70B already-know-the-skill). -1 until a use is acked. Lets the driver see WHY a scroll-use
    /// failed and skip re-buying/re-using that scroll.</summary>
    public int LastUseAckError { get; private set; } = -1;
    /// <summary>Item id from the last NC_ITEM_USE_ACK (which item the use result is for).</summary>
    public int LastUseAckItem { get; private set; } = -1;

    /// <summary>Current bag contents: slot → itemId (built from the login item list
    /// and live cell/equip changes).</summary>
    public IReadOnlyDictionary<byte, ushort> Inventory => _inventory;

    /// <summary>The stack count in main-bag <paramref name="slot"/> (from the wire lot field), or 0
    /// if the slot is empty. Used to sell the EXACT whole stack (not a guessed upper bound).</summary>
    public int ItemCount(byte slot) => _invCount.TryGetValue(slot, out var c) ? c : 0;

    /// <summary>Currently worn gear: equip slot → itemId (from equip-change events).</summary>
    public IReadOnlyDictionary<byte, ushort> Equipment => _equipment;

    /// <summary>Items currently lying on the ground in view (handle → drop), from
    /// DROPEDITEM broadcasts; retired when MAP_LOGOUT names the handle (picked/despawned).
    /// The runtime source for looting kills — walk to one and <see cref="Manager.BotManager.PickupAsync"/>.</summary>
    public IReadOnlyCollection<GroundItem> Drops => _drops.Values.ToArray();

    /// <summary>The ground drop nearest to (<paramref name="x"/>,<paramref name="y"/>), or
    /// null if nothing is on the ground. Loot picks the nearest by default.</summary>
    public GroundItem? NearestDrop(uint x, uint y)
    {
        GroundItem? best = null; var bestD = double.MaxValue;
        foreach (var g in _drops.Values)
        {
            var d = Math.Pow((double)g.X - x, 2) + Math.Pow((double)g.Y - y, 2);
            if (d < bestD) { bestD = d; best = g; }
        }
        return best;
    }

    /// <summary>Result of the bot's last pickup attempt (PICK_ACK), or null if none yet.
    /// NOTE: success is judged by the paired CELLCHANGE (the bag gained the item), not by
    /// <see cref="PickResult.Error"/> (which was 0x341 on a captured success). The
    /// inventory-full failure was captured live 2026-06-26 as <see cref="PickInventoryFull"/>
    /// (itemid 0xFFFF, lot 0) — surfaced raw here.</summary>
    public PickResult? LastPickResult { get; private set; }

    /// <summary>PICK_ACK error code meaning "inventory full" — captured live 2026-06-26 when
    /// IkFresh ran with a completely full bag: every pick returned itemid 0xFFFF, lot 0, error
    /// 0x346. (Contrast 0x341, seen on a SUCCESSFUL pick.)</summary>
    public const ushort PickInventoryFull = 0x346;

    /// <summary>PICK_ACK error code meaning the pick SUCCEEDED (the bag gained the item via the
    /// accompanying CELLCHANGE) — confirmed in KillAndPickupItems.pcapng: two picks (item 3001, 3004)
    /// each added to the bag with error 0x341. Not a failure despite the non-zero "error" field.</summary>
    public const ushort PickSuccess = 0x341;

    /// <summary>True when the bag is FULL — set when a pickup fails with <see cref="PickInventoryFull"/>,
    /// cleared on a successful SELL (room freed) or a successful pick. The leveler watches this to break
    /// the death spiral (full bag → loot picks fail → it paces over un-pickable drops forever): when set,
    /// it travels to town and sells instead of looting. Exposed as <c>bot.bagFull()</c>.</summary>
    public bool BagFull { get; private set; }

    /// <summary>Raised when a new item appears on the ground (DROPEDITEM).</summary>
    public event Action<GroundItem>? DropAppeared;

    /// <summary>Raised when a tracked ground item leaves view (MAP_LOGOUT — picked by
    /// anyone, or despawned), with its handle.</summary>
    public event Action<ushort>? DropRemoved;

    /// <summary>Raised on the result of the bot's own pickup attempt (PICK_ACK).</summary>
    public event Action<PickResult>? PickedUp;

    /// <summary>Skill ids the character has actually learned, from the zone-login skill list
    /// (NC_CHAR_CLIENT_SKILL_CMD). The source of truth for "do I have a heal / this buff" —
    /// read from the wire, never hard-coded. Resolve a name with client ActiveSkill.</summary>
    public IReadOnlyCollection<ushort> LearnedSkills => _skills.Keys.ToArray();

    /// <summary>Passive skill ids the character has learned, from the login passive list
    /// (NC_CHAR_CLIENT_PASSIVE_CMD 0x103E). Resolve a name with client PassiveSkill.</summary>
    public IReadOnlyCollection<ushort> LearnedPassives => _passives.Keys.ToArray();

    /// <summary>True if the character has learned the given skill id (active OR passive) — the
    /// "do I already know this" check (e.g. to avoid buying a scroll for a skill already learned).</summary>
    public bool HasSkill(ushort skillId) => _skills.ContainsKey(skillId) || _passives.ContainsKey(skillId);

    /// <summary>The NC_CHAR_CLIENT_ITEM_CMD <c>box</c> value that holds WORN gear (vs bag
    /// pages). Confirmed from the ZoneEntry item-frame log: box 8 carried the 6 equipped
    /// pieces (helmet 525 at inven 0x2001 → equip slot 1, etc.). Other boxes are inventory
    /// (9 = main bag, 12 = special, 15 = empty).</summary>
    private const byte EquipBox = 8;
    // The character has MULTIPLE inventory boxes; the box is encoded in the item's inven position as
    // (inven >> 10) — box 8 (0x20xx) = equipped, box 9 (0x24xx) = the MAIN BAG (loot/sell/buy go
    // here; useItem/sell default invenType 9), box 12 (0x30xx) = premium mini-houses, box 15 = empty.
    // We track ONLY the main bag (9) + equip (8); other boxes' slots COLLIDE on (inven & 0xFF) and
    // clobbered the bag before (the "Mushroom House" mini-house item hid the real loot). Main bag has
    // the lvl-1 "Mystery Vault" item as a tell.
    private const byte MainBag = 9;
    private static byte BoxOf(int inven) => (byte)(inven >> 10);

    /// <summary>Seed bag + worn-gear from the zone-login item list (captured by
    /// <see cref="Zone.ZoneEntry"/> during the login burst, which the session loop misses —
    /// the cause of empty Inventory/Equipment at login). Routes each item by its container
    /// <paramref name="items"/>.box: <see cref="EquipBox"/> → <see cref="Equipment"/>, else
    /// <see cref="Inventory"/> (slot = low byte of the inven position).</summary>
    public void SeedItems(IEnumerable<(byte box, ushort inven, ushort itemId, int count)>? items)
    {
        if (items is null) return;
        int bag = 0, eq = 0;
        foreach (var (box, inven, itemId, count) in items)
        {
            // itemId 0 = the REAL item "Leather Boots" (a real occupied slot), NOT empty — the login list
            // sends only occupied slots, so keep item-0 entries (the old skip lost them → bagFull()/free-slot
            // wrong → GET_PLAYER_EMPTY_INVENTORY hand-ins failed; wire+DB proof 2026-07-07).
            var slot = (byte)(inven & 0xFF);
            if (box == EquipBox) { _equipment[slot] = itemId; eq++; }
            else if (box == MainBag) { _inventory[slot] = itemId; _invCount[slot] = count; bag++; } // ONLY
            // the main bag (other boxes — premium/mini-house — collide on slot and hide the real loot)
        }
        if (bag + eq > 0)
        {
            // Log the actual EQUIPPED item ids (by slot) — so "what is the bot wearing" is traceable
            // (a fighter on just a starter Shortsword vs upgraded gear). Per the decode->log rule.
            var worn = string.Join(",", _equipment.OrderBy(kv => kv.Key).Select(kv => $"slot{kv.Key}=item{kv.Value}"));
            _log?.Invoke($"[ZoneView] seeded {bag} bag + {eq} equipped items from login — worn: {worn}");
        }
    }

    /// <summary>Seed the learned-skill set from the zone-login skill list (captured by
    /// <see cref="Zone.ZoneEntry"/> during the login burst, which the session loop misses).</summary>
    public void SeedSkills(IEnumerable<ushort>? skills)
    {
        if (skills is null) return;
        var added = 0;
        // id 0 is a REAL skill (ActiveSkill.ID=0), not a sentinel — see the OpClientSkill handler's note.
        foreach (var s in skills) if (_skills.TryAdd(s, 1)) added++;
        if (added > 0)
        {
            _log?.Invoke($"[ZoneView] seeded {added} learned skills: {string.Join(",", _skills.Keys.OrderBy(k => k))}");
            SkillsChanged?.Invoke();
        }
    }

    /// <summary>Seed the learned PASSIVE skills from the zone-login passive list (0x103E).
    /// Resolve names via client PassiveSkill; <see cref="HasSkill"/> covers actives + passives.</summary>
    public void SeedPassives(IEnumerable<ushort>? passives)
    {
        if (passives is null) return;
        var added = 0;
        foreach (var p in passives) if (p != 0 && _passives.TryAdd(p, 1)) added++;
        if (added > 0)
        {
            _log?.Invoke($"[ZoneView] seeded {added} learned passives: {string.Join(",", _passives.Keys.OrderBy(k => k))}");
            SkillsChanged?.Invoke();
        }
    }

    /// <summary>Raised when the learned-skill list is (re)populated at zone login.</summary>
    public event Action? SkillsChanged;

    private readonly HashSet<int> _doneQuests = new();
    private readonly ConcurrentDictionary<int, byte> _activeQuests = new();
    private readonly HashSet<int> _availableQuests = new();
    private readonly ConcurrentDictionary<int, int> _questProgress = new(); // questId -> kills credited this session (0x440D)

    /// <summary>Kills the server has CREDITED to a quest this session (counted from
    /// 0x440D NC_QUEST_NOTIFY_MOB_KILL_CMD). The authoritative objective-progress signal —
    /// distinct from how many mobs the bot killed: a status-glitched quest gets 0 credit even
    /// while the bot lands kills, which is how the driver detects a stuck quest to abandon.</summary>
    public int QuestProgress(int id) => _questProgress.TryGetValue(id, out var n) ? n : 0;

    /// <summary>Quest ids the character can accept right now — the server's available list from
    /// the login QUEST_READ burst (0x10CE). This is the authoritative orange-! set (the client
    /// derives the marker from it); the driver accepts from here rather than guessing from
    /// QuestData level/prereq.</summary>
    public IReadOnlyCollection<int> AvailableQuests => _availableQuests;
    public bool IsQuestAvailable(int id) => _availableQuests.Contains(id);

    /// <summary>Quest ids the character has completed (from the login QUEST_DONE burst). The
    /// quest driver diffs this against QuestData.shn to know what's still available.</summary>
    public IReadOnlyCollection<int> DoneQuests => _doneQuests;

    /// <summary>Quest ids currently in progress → their Status byte (from the login QUEST_DOING
    /// burst). An active quest needs resuming (do objective + turn in), not re-accepting.</summary>
    public IReadOnlyDictionary<int, byte> ActiveQuests => _activeQuests;

    public bool IsQuestDone(int id) => _doneQuests.Contains(id);
    public bool IsQuestActive(int id) => _activeQuests.ContainsKey(id);

    /// <summary>Seed completed + in-progress quest ids from the zone-login burst
    /// (NC_CHAR_QUEST_DONE_CMD / QUEST_DOING, captured by <see cref="Zone.ZoneEntry"/>).</summary>
    public void SeedQuests(IEnumerable<ushort>? done, IEnumerable<(ushort id, byte status, int progress)>? active,
        IEnumerable<ushort>? available = null)
    {
        if (done is not null) foreach (var d in done) _doneQuests.Add(d);
        // Seed both the status AND the credited progress (sum of End_NPCMobCount) from the zone's
        // QUEST_DOING snapshot. This is the authoritative count the zone re-sends on every entry,
        // so it restores progress after a handover (a fresh ZoneView) instead of reading back 0.
        // ACTIVE is authoritative over DONE: a quest that is currently in progress is NOT "done" right
        // now even if a PRIOR completion left it in the done set — a REPEATABLE quest re-accepted after
        // completion is the case that bit us (q11 looped "COMPLETE" forever because IsQuestDone stayed
        // true while it was active again, so handin short-circuited and never drove the turn-in).
        if (active is not null) foreach (var (id, st, prog) in active) { _activeQuests[id] = st; _questProgress[id] = prog; _doneQuests.Remove(id); }
        if (available is not null) foreach (var a in available) _availableQuests.Add(a);
        if (_doneQuests.Count > 0 || _activeQuests.Count > 0 || _availableQuests.Count > 0)
            _log?.Invoke($"[ZoneView] seeded quests: done={_doneQuests.Count} active={_activeQuests.Count} available={_availableQuests.Count}");
    }

    // --- Quest accept/start result (NC_QUEST_START_ACK / SELECT_START_ACK / QUEST_ERR) ---
    // NC_QUEST_START_ACK carries only {err} with no questId, so the START_REQ questId is stashed
    // here when the manager sends it and paired with the next START_ACK.
    private int _lastStartReqQuestId;
    private readonly ConcurrentDictionary<int, int> _questAcceptErr = new(); // questId -> last server err code

    /// <summary>Record that a START_REQ for <paramref name="questId"/> was just sent, so the next
    /// NC_QUEST_START_ACK (which has no questId) can be attributed to it.</summary>
    public void NoteQuestStartAttempt(int questId) => _lastStartReqQuestId = questId;

    /// <summary>The server's last accept/start result for a quest: 0 = accepted OK, &gt;0 = a refusal
    /// reason code (from START_ACK.err / SELECT_START_ACK.ErrorType / QUEST_ERR), -1 = never attempted.
    /// Lets the driver react to WHY an accept failed (and stop blind-retrying) instead of inferring
    /// from <see cref="IsQuestActive"/> not flipping.</summary>
    public int QuestAcceptErr(int id) => _questAcceptErr.TryGetValue(id, out var e) ? e : -1;

    /// <summary>(questId, err) of the most recent accept result, or null. err==0 means accepted.</summary>
    public (int QuestId, int Err)? LastQuestAcceptResult { get; private set; }

    /// <summary>Raised on every quest accept/start result (success or refusal) with (questId, err).</summary>
    public event Action<int, int>? QuestAcceptResult;

    private void RecordQuestAcceptResult(int questId, int err)
    {
        if (questId != 0) _questAcceptErr[questId] = err;
        LastQuestAcceptResult = (questId, err);
        if (err == 0 && questId != 0) MarkQuestActive(questId);
        _log?.Invoke($"[ZoneView] QUEST_ACCEPT_RESULT quest={questId} err={err}{(err == 0 ? " (accepted)" : " (refused)")}");
        QuestAcceptResult?.Invoke(questId, err);
    }

    /// <summary>Mark a quest active (just accepted) / done (just turned in) so the driver's
    /// view stays current within the session without waiting for a relog.</summary>
    // Re-accepting a quest makes it active and NOT done (clear any stale prior completion — repeatable
    // quests re-accepted after completion otherwise stay IsQuestDone=true and loop the hand-in forever).
    public void MarkQuestActive(int id, byte status = 1) { _activeQuests[id] = status; _availableQuests.Remove(id); _doneQuests.Remove(id); }
    public void MarkQuestDone(int id) { _activeQuests.TryRemove(id, out _); _availableQuests.Remove(id); _doneQuests.Add(id); }

    /// <summary>The quest-dialogue step the server is currently prompting (last
    /// NC_QUEST_SCRIPT_CMD_REQ), or null if none pending. The quest driver answers it with
    /// QUEST_SCRIPT_CMD_ACK ("proceed"); cleared after a few seconds of no new prompt.</summary>
    public QuestStep? PendingQuest { get; private set; }

    // The server sends the accept/turn-in script as a BURST of NC_QUEST_SCRIPT_CMD_REQ (0x4401) pages
    // (e.g. 4 SAY pages ~100ms apart, then a DONE) WITHOUT waiting for an ack between them. The driver
    // must ack EACH page — but a single PendingQuest field gets overwritten by the burst, so it only saw
    // (and acked) the LAST page → "1 page answered" → QUEST_DONE never fired. Queue every page so the
    // driver can drain + ack all of them (verified vs QuestsNew.pcapng: q11 turn-in = 5× SCRIPT_CMD_ACK).
    private readonly System.Collections.Concurrent.ConcurrentQueue<QuestStep> _questScript = new();
    /// <summary>Dequeue the next un-answered quest-script page (FIFO), or null if none queued.</summary>
    public QuestStep? DequeueQuestStep() => _questScript.TryDequeue(out var s) ? s : null;
    /// <summary>Drop any stale queued pages + the pending prompt — call before driving a fresh dialogue.</summary>
    public void ClearQuestScript() { while (_questScript.TryDequeue(out _)) { } PendingQuest = null; }

    /// <summary>Raised on each quest-dialogue prompt (NC_QUEST_SCRIPT_CMD_REQ).</summary>
    public event Action<QuestStep>? QuestPrompt;

    public bool TryGetPlayer(ushort handle, out NearbyPlayer player) => _nearby.TryGetValue(handle, out player!);

    /// <summary>The bot's own zone handle (from the [1802] MAP_LOGIN_ACK). Set once
    /// zone entry completes; used to filter MOVESPEED broadcasts to self only.</summary>
    public ushort? SelfHandle { get; set; }

    /// <summary>Supplies the bot's current world position (set by the manager to the live
    /// tracked position). Lets aggro detection tell whether a mob is running toward us.</summary>
    public Func<(uint X, uint Y)?>? SelfPositionProvider { get; set; }

    /// <summary>Returns true if a mob id is a huntable enemy (set by the manager from client
    /// MobInfo — see <see cref="GameData.ClientData.IsHuntableEnemy"/>). Used to suppress the
    /// angle-aggro heuristic for town guards (player-side) and other non-enemies that wander
    /// near us. Null = treat everything as huntable (no client data).</summary>
    public Func<ushort, bool>? IsHuntableMob { get; set; }

    /// <summary>Returns true if an abstate index IMMOBILIZES the target (set by the manager from
    /// client AbState/SubAbState — see <see cref="GameData.ClientData.IsMoveBlockingAbstate"/>).
    /// Used to know when a self-abstate is a root/stun so nav won't learn a wall and combat waits.</summary>
    public Func<uint, bool>? IsMoveBlockingAbstate { get; set; }

    // The abstate indices currently ACTIVE on SELF (added on ABSTATESET, removed on ABSTATERESET for
    // our own handle). Rooted = any of them immobilizes us. Concurrent set: the read loop writes it,
    // the nav/combat callers read Rooted.
    private readonly HashSet<uint> _selfAbstates = new();
    private readonly object _selfAbstateLock = new();

    /// <summary>True while a movement-blocking abnormal state (stun/root/entangle) is active on the bot
    /// — the server will MOVEFAIL every move until it clears. Nav uses this to NOT learn the tile as a
    /// wall (the JCQ grid-poisoning bug), and combat/instance code to WAIT instead of thrashing.</summary>
    public bool Rooted
    {
        get
        {
            lock (_selfAbstateLock)
            {
                if (_selfAbstates.Count == 0 || IsMoveBlockingAbstate is not { } f) return false;
                foreach (var a in _selfAbstates) if (f(a)) return true;
                return false;
            }
        }
    }

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
                LogV($"[ZoneView] player left: {gone.Name} (h={hnd})");
                PlayerLeft?.Invoke(hnd);
            }
            if (_npcs.TryRemove(hnd, out var goneNpc)) StashRecentNpc(hnd, goneNpc); // sticky-hold mobs through AoI flicker
        }
        else if (op == OpReallyKill)
        {
            // A mob died (REALLYKILL {dead, attacker}) — retire it NOW rather than waiting for
            // the delayed briefinfo despawn, so a grind script moves to the next target at once.
            // Only credit it as OUR kill (LastKill, KillsByMe) when WE are the attacker — in a
            // busy field other players kill mobs we were on, which earns us no quest/XP credit.
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                var dead = (ushort)(p[0] | (p[1] << 8));
                var attacker = (ushort)(p[2] | (p[3] << 8));
                bool mine = SelfHandle != 0 && attacker == SelfHandle;
                LogV($"[ZoneView] REALLYKILL dead={dead} attacker={attacker} self={SelfHandle} mine={mine}");
                // Retire the dead entity from BOTH maps: regular mobs live in _npcs, but scenario/instance
                // enemies (the JCQ "shadow" CLONES) are CHARACTERS in _nearby. Without clearing _nearby a
                // killed clone lingers with a stale position (the "dist 3920" chase-a-corpse bug) and the
                // kill is never credited (KillsByMe gated on _npcs) — so the instance driver can't tell it
                // won and move to the next clone/room.
                bool wasMob = _npcs.TryRemove(dead, out _);
                bool wasRecent = _recentNpcs.TryRemove(dead, out _); // died while flickered-out of view → evict sticky copy
                bool wasChar = _nearby.TryRemove(dead, out _);
                if ((wasMob || wasRecent || wasChar) && mine)
                {
                    LastKill = dead; KillsByMe++;
                    _log?.Invoke($"[combat] KILLED {(wasChar && !wasMob ? "clone/char" : "mob")} h={dead} (totalKills={KillsByMe})");
                }
            }
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
                // The bulk batch (the map-enter NPC SEED) carries many records — log the roster size +
                // a few entries so "what does the bot know about this map's NPCs" is traceable.
                if (n > 1)
                {
                    var sample = string.Join(",", _npcSeed.Values.Take(8)
                        .Select(e => e.IsGate ? $"gate->{e.LinkMap}" : $"npc{e.MobId}"));
                    _log?.Invoke($"[ZoneView] NPC SEED received: {n} records (roster now {_npcSeed.Count}) — {sample}…");
                }
            }
        }
        else if (op == OpRegenMob)
        {
            System.Threading.Interlocked.Increment(ref _scenarioRegenCount); // wave-armed signal for the AREAENTRY_ACK re-send loop
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
                LogV($"[ZoneView] move speed: {WalkSpeed:F0} -> 120 u/s (dismounted)");
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
                // DIAGNOSTIC (operator 2026-07-15: "movefail sends the client pos back → should self-heal;
                // something's fishy"): log the bot's BELIEVED position vs the server's authoritative snap-back
                // + the delta, at NOTE (throttled), so we can SEE whether the self-heal actually keeps them in
                // sync — and, in a scenario instance, HOW FAR the server thinks we are from where we believe.
                var believed = SelfPositionProvider?.Invoke();
                var deltaU = believed is { } bd ? Math.Sqrt(Math.Pow((double)bx - bd.X, 2) + Math.Pow((double)by - bd.Y, 2)) : 1e9;
                // A real shove-back (delta >= 64u, or unknown) = we're still navigating, NOT parked at the
                // trigger. Sub-64u corrections are just the server settling us in place; they don't count as
                // "still moving" for the AreaEntry ack gate (else the ack never fires where the server holds us
                // a few units off the exact centre). Drives the re-send-ack's "have we arrived?" check.
                if (deltaU >= 64) _lastSignificantMoveFailUtc = DateTime.UtcNow;
                if (InScenarioInstance && DateTime.UtcNow - _lastMoveFailLog > TimeSpan.FromMilliseconds(700))
                {
                    _lastMoveFailLog = DateTime.UtcNow;
                    _log?.Invoke($"[ZoneView] MOVEFAIL desync — believed @{believed}, server snapped to ({bx},{by}), delta={deltaU:F0} (area='{LastScenarioArea}')");
                }
                else LogV($"[ZoneView] MOVEFAIL — server snapped us to ({bx},{by})");
                MoveFailed?.Invoke((bx, by));
            }
        }
        else if (op == OpAbStateSet || op == OpAbStateReset)
        {
            // [targetHandle u16][abStataIndex u32]. Track states applied to SELF so nav/combat know when
            // we're rooted (a movement-blocking abstate → the server MOVEFAILs every move; NOT a wall).
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var target = (ushort)(p[0] | (p[1] << 8));
                var idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
                if (SelfHandle is { } self && target == self)
                {
                    bool set = op == OpAbStateSet;
                    bool moveBlock = IsMoveBlockingAbstate?.Invoke(idx) == true;
                    bool changed;
                    lock (_selfAbstateLock) changed = set ? _selfAbstates.Add(idx) : _selfAbstates.Remove(idx);
                    if (changed && moveBlock)
                        _log?.Invoke($"[ZoneView] ABSTATE {(set ? "SET" : "RESET")} idx={idx} on SELF — MOVE-BLOCKING (rooted={Rooted})");
                    else
                        LogV($"[ZoneView] ABSTATE {(set ? "SET" : "RESET")} idx={idx} on SELF (moveBlock={moveBlock})");
                }
                else LogV($"[ZoneView] ABSTATE {(op == OpAbStateSet ? "SET" : "RESET")} idx={idx} on h={target}");
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
        else if (op == OpCharParamChange)
        {
            // {count u8}{paramId u8, value u32}* — apply MaxHP(0x10)/MaxSP(0x11) live so they track a
            // MID-ZONE level-up (verified on the wire: a level-up cluster carried {0x10=250, 0x11=109},
            // matching the HPCHANGE refill). Other paramIds (0x12+ = END/DEX/… stats) are the P3 — the loop
            // already iterates them, so surfacing the rest later is a small extension. A partial variant that
            // omits 0x10/0x11 just leaves MaxHp/MaxSp unchanged.
            try
            {
                var p = pkt.Payload.Span;
                if (p.Length >= 1)
                {
                    int count = p[0], o = 1;
                    for (int e = 0; e < count && o + 5 <= p.Length; e++, o += 5)
                    {
                        byte pid = p[o];
                        uint val = (uint)(p[o + 1] | (p[o + 2] << 8) | (p[o + 3] << 16) | (p[o + 4] << 24));
                        if (pid == 0x10 && val > 0 && val != MaxHp) { MaxHp = val; _log?.Invoke($"[ZoneView] MaxHP -> {val} (CHANGEPARAM 0x1035)"); }
                        else if (pid == 0x11 && val > 0 && val != MaxSp) { MaxSp = val; _log?.Invoke($"[ZoneView] MaxSP -> {val} (CHANGEPARAM 0x1035)"); }
                    }
                }
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
            // NC_MENU_SERVERMENU_REQ: title[128], priority u8 @128, npcHandle u16 @129,
            // npcPosition @131 (8B), limitRange u16 @139, menunum u8 @141, then menunum ×
            // SERVERMENU{ reply u8, string[32] } @142 (33B each). The reply byte is what to send
            // in SERVERMENU_ACK (0x3C02) to pick that option; the string is its label. Verified
            // live: "Do you want to move to Forest of Tides field?" -> {0,"Yes"},{1,"No"}.
            LastMenuAtUtc = DateTime.UtcNow;
            ServerMenuOpen = true;
            var p = pkt.Payload.Span;
            ServerMenuTitle = ReadCString(p, 0, 128);
            var opts = new List<ServerMenuOption>();
            if (p.Length >= 142)
            {
                int menunum = p[141];
                for (int i = 0; i < menunum; i++)
                {
                    int off = 142 + i * 33;
                    if (off + 33 > p.Length) break;
                    opts.Add(new ServerMenuOption(p[off], ReadCString(p, off + 1, 32) ?? ""));
                }
            }
            ServerMenuOptions = opts;
            var optStr = string.Join(", ", opts.Select(o => $"[{o.Reply}]={o.Text}"));
            _log?.Invoke($"[ZoneView] server menu opened (0x3C01): \"{ServerMenuTitle}\" {{{optStr}}}");
        }
        else if (op == OpCharDeadMenu)
        {
            Dead = true; DeadAtUtc = DateTime.UtcNow;
            _log?.Invoke("[combat] DIED (death menu) — revive in place or respawn to town");
        }
        else if (op == OpCharReviveSame)
        {
            Dead = false; DeadAtUtc = DateTime.MinValue;
            // REVIVESAME (same zone server) payload == LINKSAME format {mapId u16, x u32, y u32}.
            // The real client treats a revive as an in-band map change: after REVIVE_REQ it
            // re-sends MAP_LOGINCOMPLETE (0x1803), which makes the server spawn it back in and
            // stream the post-revive state — INCLUDING the HPCHANGE that restores HP (confirmed
            // in Death.pcapng: REVIVE_REQ → REVIVESAME → LOGINCOMPLETE → HPCHANGE hp=34). Without
            // re-sending LOGINCOMPLETE the bot never gets that HPCHANGE and sits at 0 HP forever
            // (the "stuck dead" wedge). Routing through MapChanged reuses the LINKSAME path that
            // already does SetPosition + re-send LOGINCOMPLETE.
            if (Navigation.MapHandoff.ParseLinkSame(pkt.Payload.Span) is { } h)
            {
                _log?.Invoke($"[ZoneView] revived (same-server) -> mapId={h.MapId} @({h.X},{h.Y}) — re-spawning via LOGINCOMPLETE");
                CurrentMapId = h.MapId;
                _npcs.Clear(); _recentNpcs.Clear(); _npcSeed.Clear(); _nearby.Clear(); _drops.Clear();
                lock (_selfAbstateLock) _selfAbstates.Clear();  // abstates are per-map; server re-broadcasts
                LastScenarioArea = null; InScenarioInstance = false; _scenarioAckedAreas.Clear();
                MapChanged?.Invoke(h);
            }
        }
        else if (op == OpBatExpGain)
        {
            // {expgain u32@0, mobhandle u16@4}. Per-kill exp credit. Accumulate onto the seeded
            // absolute so Exp tracks live; log the delta + total (Info) so the grind rate is visible.
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                long gain = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(0, 4));
                SessionExpGained += gain;
                if (Exp >= 0) Exp += gain;
                _logLevel?.Invoke(BotLogLevel.Info, $"[exp] +{gain} -> {(Exp >= 0 ? Exp.ToString() : "?")} (session +{SessionExpGained})");
            }
        }
        else if (op == OpCharLevelChanged)
        {
            // {wmhandle u16, charNo u32, newLevel u8}. Update OUR level only.
            var p = pkt.Payload.Span;
            if (p.Length >= 7)
            {
                ushort wm = (ushort)(p[0] | (p[1] << 8));
                byte newLevel = p[6];
                if (wm == _session.State.WmHandle && newLevel > 0)
                {
                    _log?.Invoke($"[ZoneView] LEVEL UP -> {newLevel}");
                    LevelChanged?.Invoke(newLevel);
                }
            }
        }
        else if (op == OpCharPromoteAck)
        {
            // JOB CHANGE — {newclass u8}. The linchpin JCQ packet: our class actually changed here.
            var p = pkt.Payload.Span;
            if (p.Length >= 1)
            {
                byte newclass = p[0];
                PromotedClass = newclass;
                _log?.Invoke($"[ZoneView] *** JOB CHANGE (PROMOTE_ACK) -> class {newclass} ***");
                Promoted?.Invoke(newclass);
            }
        }
        else if (op == OpScenarioAreaEntryReq)
        {
            // SCENARIO room trigger — echo the ACK (same areaindex) to arm the mob wave. Reflexive: the real
            // client always does this, and without it the room never spawns (the JCQ/instance clear stalls).
            var req = pkt.ReadBody<PROTO_NC_SCENARIO_AREAENTRY_REQ>();
            int z = Array.IndexOf(req.areaindex.n8_name, (byte)0);
            var area = System.Text.Encoding.ASCII.GetString(req.areaindex.n8_name, 0, z < 0 ? req.areaindex.n8_name.Length : z);
            LastScenarioArea = area;
            InScenarioInstance = true;   // latch: we're inside a scenario instance (survives between-room gaps)
            // DIAGNOSTIC (operator 2026-07-15): log WHERE we are when the server sends each AreaEntry REQ — the
            // server sends it when IT detects us crossing the trigger, so this is the server-agreed cross point.
            // Compare to the .aid box + the ack self-positions to see if the walk-in interrupt (e.g. Zone_Mob04
            // LightOff) should have fired. If REQ fires but LightOff doesn't, the cross was seen — look elsewhere.
            _log?.Invoke($"[ZoneView] SCENARIO AREAENTRY_REQ '{area}' — server saw us cross; self@{SelfPositionProvider?.Invoke()}");
            ScenarioAreaEntered?.Invoke(area);
            // ROOT CAUSE (operator + JCQMany diff, 2026-07-14): the server fires the room's interrupt (SkelRegen)
            // on the ACK using the player's SERVER-side position. The bot MOVEFAIL-storms entering the area, so its
            // server position lags OUTSIDE the trigger while the client thinks it's inside — a single ack from that
            // desynced moment doesn't fire (the R2 intermittency + "bot does something different"; the real client
            // never MOVEFAILs so its one ack always lands server-valid). FIX: RE-SEND the ACK every ~500ms while our
            // (MOVEFAIL-resynced) position is INSIDE the .aid box, so an ack lands on a tick where the SERVER agrees
            // we're inside → the server re-checks position per ack. Stop once a REGENMOB arrives (wave armed) or ~15s.
            var ackArea = req.areaindex;
            var reqAt = DateTime.UtcNow;
            var mapAtReq = CurrentMapId;
            _ = Task.Run(async () =>
            {
                // (1) WAIT until we've ARRIVED and parked at the trigger — no significant MOVEFAIL for ~900ms =
                //     the server has settled us at a stable, server-valid position (operator 2026-07-15: "only
                //     start the ack spam once near/inside, as confirmed by lack of movefail"). Acking while still
                //     navigating there is pointless — the server re-checks OUR server position on each ack, so an
                //     ack from outside the trigger fires nothing. WHY the old design failed the FINALE: the server
                //     sends AREAENTRY_REQ 'Zone_Mob05' while we're still EAST fighting the Chiefs (self@5293,5194,
                //     in Zone_Mob04); the old loop spammed acks from there and hit a hard 90s cutoff BEFORE we
                //     killed the Chiefs and reached Zone_Mob05 → LightOn never got an ack from inside → never fired.
                //     ARRIVED = shove-free (settled at a server-valid position) AND actually INSIDE this area's
                //     .aid box (else "shove-free" opens anywhere the bot pauses — e.g. it acked Zone_Mob02 from
                //     1703,3140, still in Zone_Mob01, ~1300u short). The box check makes the ack fire ONLY from
                //     inside the trigger, so the finale ack for Zone_Mob05 waits until we've killed the Chiefs and
                //     walked west INTO Zone_Mob05 — not from the Chief area where the REQ first arrived. The 5-min
                //     window is plenty; no IsInsideScenarioArea data (non-scenario) → box check passes (true).
                const int ArriveTimeoutMin = 5;
                bool arrived = false;
                while (DateTime.UtcNow - reqAt < TimeSpan.FromMinutes(ArriveTimeoutMin) && CurrentMapId == mapAtReq)
                {
                    bool shoveFree = DateTime.UtcNow - _lastSignificantMoveFailUtc > TimeSpan.FromMilliseconds(900);
                    bool insideBox = SelfPositionProvider?.Invoke() is { } p && (IsInsideScenarioArea?.Invoke(area, p) ?? true);
                    if (shoveFree && insideBox) { arrived = true; break; } // arrived + parked INSIDE the trigger box
                    await Task.Delay(300).ConfigureAwait(false);
                }
                if (CurrentMapId != mapAtReq) return; // left the instance while travelling
                if (!arrived)
                {
                    // TIMEOUT — we never got shove-free INSIDE area A's box within the window. This should NEVER
                    // happen on a healthy run; if it fires, the bot is nav-stuck short of the trigger (a wall / an
                    // unreachable box) or the window is too small. Scream it so we don't silently stall the instance.
                    _log?.Invoke($"[ZoneView] ⛔ CRITICAL: AreaEntry ack for '{area}' TIMED OUT after {ArriveTimeoutMin}min — never arrived shove-free INSIDE its box (nav stuck short of the trigger? box unreachable?). Consider increasing the window or fixing nav. Acking from here as a last resort.");
                }
                // (2) ARRIVED inside the trigger (shove-free = server-valid position, no desync since we detect
                //     MOVEFAILs). SENDING the ack IS the completion — the server dispatches area A's interrupt on
                //     it, and we mark A DONE the moment the first ack goes out (operator 2026-07-15: "treat sending
                //     the acks (with retries) as the real completion, the moment the first one is sent"; "when you
                //     ACK inside area A count A as done"). We do NOT wait for/gate on a REGENMOB (unreliable — a
                //     global counter that cross-contaminates areas, and some interrupts (AnotherKebing/LightOn)
                //     don't REGEN). The remaining acks are just delivery retries (harmless if the interrupt already
                //     ran — the server ignores a duplicate AreaEntry for an area we're already inside).
                _scenarioAckedAreas[area] = 1;   // AUTHORITATIVE "area done" (the instance driver reads this)
                _log?.Invoke($"[ZoneView] SCENARIO area '{area}' — ARRIVED + ACKED (done) @{SelfPositionProvider?.Invoke()} → sending 10 ACKs @1s (retries)");
                for (int i = 1; i <= 10; i++)
                {
                    await _session.SendAsync(new PROTO_NC_SCENARIO_AREAENTRY_ACK { areaindex = ackArea }, default).ConfigureAwait(false);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });
        }
        else if (op == OpScenarioObjTypeChange)
        {
            // A scripted scenario entity changed kind (see const doc). When the shadow clone turns into a
            // non-combatant NPC (change2npc, type 4) it is FLEEING/leaving — stop treating it as a fightable
            // clone: drop it from _nearby so the instance driver stops chasing/holding the PHANTOM and the
            // hoover moves on to the real wave (Kebings/Skeletons/Chiefs). Keep it on `mob` (type 5 = it's a
            // live fightable enemy). Always log (decode→log) so a new/unknown type value is visible.
            var b = pkt.ReadBody<PROTO_NC_SCENARIO_OBJTYPECHANGE_CMD>();
            _log?.Invoke($"[ZoneView] scenario OBJTYPECHANGE h={b.handle} type={b.type}" +
                (b.type == ScenObjTypeNpc ? " (change2npc → clearing phantom clone)" :
                 b.type == ScenObjTypeMob ? " (change2mob → fightable)" : " (unknown type)"));
            if (b.type != ScenObjTypeMob)
            {
                if (_nearby.TryRemove(b.handle, out var gone)) PlayerLeft?.Invoke(b.handle);
                _npcs.TryRemove(b.handle, out _);
            }
        }
        else if (op == OpBriefInfoBuildDoor)
        {
            // A scenario DOOR spawned (0x1C0F) — the authoritative handle→name→initial-state link. Seed the
            // by-name state that drives the pathfinding door overlay, so closed doors are walls from the very
            // first tick (before any 0x6C09), and later handle-keyed DOORSTATEs can resolve their .sbi door.
            var bd = pkt.ReadBody<PROTO_NC_BRIEFINFO_BUILDDOOR_CMD>();
            int z = Array.IndexOf(bd.blockindex.n8_name, (byte)0);
            var name = System.Text.Encoding.ASCII.GetString(bd.blockindex.n8_name, 0,
                z < 0 ? bd.blockindex.n8_name.Length : z);
            if (!string.IsNullOrEmpty(name))
            {
                _doorNames[bd.handle] = name;
                _doorStateByName[name] = bd.doorstate;
                _log?.Invoke($"[ZoneView] SCENARIO DOOR BUILD '{name}' h={bd.handle} state={bd.doorstate} ({(bd.doorstate == 0 ? "CLOSED" : "open")}) — seeded nav overlay");
                DoorStatesByNameChanged?.Invoke(DoorStatesByName);
            }
        }
        else if (op == OpScenarioDoorState)
        {
            // A scenario corridor DOOR changed state (open/close). Decode + track by handle, correlate to a
            // position via _npcs (doors are tracked entities), and LOG it so the R2/R3 door choreography is
            // finally visible on the tail (the previous "MOVEFAIL — likely a closed scenario door" was a GUESS;
            // now we KNOW which door + where + open/closed). The instance nav reads DoorStates to hold at a
            // closed door instead of thrashing through it. State byte is raw off the wire (logged so we can pin
            // the open/closed value against JobChange1.ps's dooropen/doorclose ordering).
            var b = pkt.ReadBody<PROTO_NC_SCENARIO_DOORSTATE_CMD>();
            uint? dx = null, dy = null;
            if (_npcs.TryGetValue(b.door, out var dn)) { dx = dn.X; dy = dn.Y; }
            else if (_doorStates.TryGetValue(b.door, out var prev)) { dx = prev.X; dy = prev.Y; } // keep last-known pos
            _doorStates[b.door] = new DoorState(b.door, b.doorstate, dx, dy);
            // Update the by-NAME state (bridged via the BUILDDOOR handle→name map) → drives the nav overlay so a
            // door that just closed becomes a wall in our collision (matching the server), state 0=closed 1=open.
            if (_doorNames.TryGetValue(b.door, out var dname))
            {
                _doorStateByName[dname] = b.doorstate;
                _log?.Invoke($"[ZoneView] SCENARIO DOOR '{dname}' h={b.door} state={b.doorstate} ({(b.doorstate == 0 ? "CLOSED" : "open")}) @({dx?.ToString() ?? "?"},{dy?.ToString() ?? "?"}) — nav overlay updated");
                DoorStatesByNameChanged?.Invoke(DoorStatesByName);
            }
            else
                _log?.Invoke($"[ZoneView] SCENARIO DOOR h={b.door} state={b.doorstate} @({dx?.ToString() ?? "?"},{dy?.ToString() ?? "?"}) (name not yet known — no BUILDDOOR seen)");
        }
        else if (op == OpCharReviveOther)
        {
            if (Dead) _log?.Invoke("[ZoneView] revived (cross-server) — REVIVEOTHER not fully wired");
            Dead = false; DeadAtUtc = DateTime.MinValue;
            // TODO: REVIVEOTHER (0x1050) = revive on ANOTHER zone server (payload embeds a
            // LOGIN_ACK + wm handle, like LINKOTHER). Needs the cross-server reconnect path.
            // Rare vs same-server REVIVESAME; wire it like the LINKOTHER handoff when needed.
        }
        else if (op == OpActNpcMenuOpen)
        {
            NpcMenuOpen = true;
            // Payload = the NPC mobId that opened the menu (e.g. 5D 00 = 93 = Pey). A multi-quest
            // giver opens this; SELECT_START_REQ then keys the chosen quest by this id.
            var mp = pkt.Payload.Span;
            MenuNpcId = mp.Length >= 2 ? (ushort)(mp[0] | (mp[1] << 8)) : (ushort)0;
            _log?.Invoke($"[ZoneView] NPC menu opened (0x201C) npc={MenuNpcId} — awaiting select");
        }
        else if (op == OpSoulStoneHpBuyAck || op == OpSoulStoneSpBuyAck)
        {
            // BUY_ACK {totalnumber u16} = new reserve count + proof the buy took (only
            // succeeds near a healer). Missing ack after a buy = it didn't work.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                int total = p[0] | (p[1] << 8);
                if (op == OpSoulStoneHpBuyAck) { HpStones = total; if (total > 0) HpStoneDepleted = false; }
                else { SpStones = total; if (total > 0) SpStoneDepleted = false; }
                _log?.Invoke($"[ZoneView] soul-stone {(op == OpSoulStoneHpBuyAck ? "HP" : "SP")} BUY ok — reserve now {total}");
            }
        }
        else if (op == OpSoulStoneBuyFail)
        {
            // NC_SOULSTONE_BUYFAIL_ACK {err u16} — the server REFUSED a stone buy (e.g. err 0x0742 =
            // requested count would exceed the max reserve). Definitive: no BUY_ACK is coming for
            // that request. Count + code let the script react instead of re-firing forever.
            var p = pkt.Payload.Span;
            LastStoneBuyFailErr = p.Length >= 2 ? (ushort)(p[0] | (p[1] << 8)) : (ushort)0;
            StoneBuyFailCount++;
            _log?.Invoke($"[ZoneView] soul-stone BUY FAILED (0x5005) err=0x{LastStoneBuyFailErr:X4} — server refused the buy (count vs reserve/afford mismatch?)");
        }
        else if (op == OpSoulStoneHpUseSuc || op == OpSoulStoneSpUseSuc)
        {
            // The reserve had a charge and it was spent (the HP/SP gain itself comes via
            // HPCHANGE/SPCHANGE). A success means that pool isn't depleted; decrement its count.
            PopStoneUseKind(); // keep the pending queue in sync
            if (op == OpSoulStoneHpUseSuc)
            {
                HpStoneDepleted = false;
                if (HpStones is { } n && n > 0) HpStones = n - 1;
            }
            else
            {
                SpStoneDepleted = false;
                if (SpStones is { } n && n > 0) SpStones = n - 1;
            }
        }
        else if (op == OpSoulStoneUseFail)
        {
            // USEFAIL (0x5006) is SHARED HP+SP and carries no marker — attribute it to the USE we
            // actually fired (the pending queue noted at the send site). Misattributing SP fails to
            // HP zeroed a REAL 30-charge HP reserve live (2026-07-01) → doomed over-cap restock loop.
            // A USE also fails at FULL HP/SP ("nothing to restore"), so only mark a pool depleted
            // when that pool was actually below max. (operator-confirmed)
            bool? kind = PopStoneUseKind();
            // A USEFAIL is EITHER an empty reserve, OR the stone COOLDOWN, OR firing at full HP/SP (operator
            // 2026-07-04: do NOT assume empty). The reserve COUNT is authoritative — decremented ONLY on a real
            // 0x5008/0x500A success — so NEVER zero it here. Zeroing a full reserve on a cooldown-fail was the
            // 46→0-in-two-fights + bogus cross-map-restock bug. Mark a pool depleted only when its tracked COUNT
            // actually says empty; otherwise the fail is harmless (the driver just spams again and one lands).
            if (kind is null or true)
            {
                if (kind is not null)
                {
                    bool empty = HpStones is { } n && n <= 0;
                    if (empty && !HpStoneDepleted) _log?.Invoke("[ZoneView] HP soul-stone reserve EMPTY (0x5006 + count 0) — need restock");
                    HpStoneDepleted = empty;
                }
                else
                    _log?.Invoke("[ZoneView] soul-stone USE FAILED (0x5006) with no pending USE — ignoring (can't attribute HP vs SP)");
            }
            else
            {
                bool empty = SpStones is { } n && n <= 0;
                if (empty && !SpStoneDepleted) _log?.Invoke("[ZoneView] SP soul-stone reserve EMPTY (0x5006 + count 0) — need restock");
                SpStoneDepleted = empty;
            }
        }
        else if (Array.IndexOf(OpShopOpen, op) >= 0)
        {
            // [itemnum u16][npc u16][MENUITEM × itemnum]. The TABLE shops (skill master/smith/
            // general — 0x3C09/0A/0B) use MENUITEM = {slot u8, itemid u16} = 3 bytes, so the itemid
            // is at off+1 (NOT the leading byte — verified on 0x3C09: slot 0x18 → itemid 0x0324).
            // The simple shops (0x3C03/04/06) lead with the itemid u16. Derive stride and place the
            // itemid read accordingly: a 3-byte (slot-prefixed) record reads at off+1, else off.
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
                    int idAt = stride == 3 ? 1 : 0; // 3-byte MENUITEM is {slot u8, itemid u16}
                    for (int i = 0; i < itemnum; i++)
                    {
                        var off = 4 + i * stride + idAt;
                        if (off + 2 > p.Length) break;
                        items.Add((ushort)(p[off] | (p[off + 1] << 8)));
                    }
                    _log?.Invoke($"[ZoneView] shop opened (0x{op:X4}) npc={ShopNpc} items={itemnum} stride={stride}");
                }
                _shopItems = items.ToArray();
                ShopOpenUtc = DateTime.UtcNow;
                LastShopKind = op is 0x3C03 or 0x3C09 ? ShopKind.Weapon
                    : op is 0x3C04 or 0x3C0A ? ShopKind.Skill
                    : ShopKind.Item; // 0x3C06 / 0x3C0B
                ShopOpened?.Invoke(_shopItems);
            }
        }
        else if (op == OpShopOpenSoulStone)
        {
            // Soul-stone shop opened — a real shop session (buys soul stones AND accepts item
            // sells). Payload = SHOPOPENSOULSTONE_CMD: two SOULSTONEMENU (hp @0, sp @12), each
            // 3× u32 = {restorePerStone @0, maxReserve @4, UNIT PRICE @8}. CORRECTED 2026-06-24:
            // the PRICE is the THIRD field, not the first — verified live (IkFresh hp={79,17,7} →
            // a 5-stone buy cost 35 cen = 7/stone, matching field@8=7; field@0=79 is HP-restored-
            // per-stone, which scales with level). The original capture hp={207,29,16} → price 16.
            // The CURRENT reserve is NOT in this packet (it's in NC_CHAR_BASE) — don't seed it here.
            var p = pkt.Payload.Span;
            if (p.Length >= 24)
            {
                uint hpRestore = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(0, 4));
                uint hpMax     = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(4, 4));
                HpStonePrice   = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(8, 4));
                uint spRestore = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(12, 4));
                uint spMax     = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(16, 4));
                SpStonePrice   = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(20, 4));
                if (hpMax > 0) MaxHpStones = hpMax;
                if (spMax > 0) MaxSpStones = spMax;
                _log?.Invoke($"[ZoneView] soul-stone shop opened (0x3C05) — HP restore {hpRestore} max {hpMax} @{HpStonePrice}cen, SP restore {spRestore} max {spMax} @{SpStonePrice}cen");
            }
            else _log?.Invoke("[ZoneView] soul-stone shop opened (0x3C05) — sells accepted (no menu payload)");
            ShopOpenUtc = DateTime.UtcNow;
            LastShopKind = ShopKind.SoulStone;
        }
        else if (op == OpMenuRandomOption)
        {
            // 0x3C0E NC_MENU_RANDOMOPTION_CMD — a NON-shop NPC menu (the RouN Anvil: reforge/reroll item
            // stats, needs a Hammer of Bijou + premium currency). NOT a merchant. Record the time so the
            // sync open flow classifies this NPC as "not a shop" and CLOSES the UI before moving on.
            RandomOptionUtc = DateTime.UtcNow;
            _log?.Invoke("[ZoneView] NPC RandomOption menu (0x3C0E) — NOT a shop (e.g. Anvil reforge)");
        }
        else if (op == OpCenChange)
        {
            // {cen u64} = the new money total. The authoritative money signal. Log the DELTA with a
            // sign + a greppable [money] tag so "where does the money go?" is answerable from the tail
            // (grep '[money]'): a '-' is a spend (shop buy / restock), a '+' is income (drop / sell /
            // quest reward). Income should outpace spend — a long run of '-' with no '+' is the bug.
            var p = pkt.Payload.Span;
            if (p.Length >= 8)
            {
                var cen = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p);
                if (cen != Money)
                {
                    var delta = Money < 0 ? 0 : cen - Money;  // first seed (Money==-1) isn't a real delta
                    var line = Money < 0
                        ? $"[money] seed {cen}"
                        : $"[money] {(delta >= 0 ? "+" : "")}{delta} -> {cen} (was {Money})";
                    if (_logLevel is not null) _logLevel(BotLogLevel.Info, $"[ZoneView] {line}");
                    else _log?.Invoke($"[ZoneView] {line}");
                }
                Money = cen;
            }
        }
        else if (op == OpSellAck)
        {
            // 2-byte result code for our SELL_REQ (no PDB struct). 0x0381 = success (real client);
            // anything else = rejected. Record it so the driver can verify the sell took.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                LastSellAck = p[0] | (p[1] << 8);
                LastSellAckUtc = DateTime.UtcNow;
                // A reject (not 0x0381) usually means the shop isn't really open — drop the
                // open signal so the driver re-opens cleanly before retrying.
                if (LastSellAck != 0x0381) ShopOpenUtc = default;
                else BagFull = false;   // a successful sell freed a bag slot — clear the full flag
                _log?.Invoke($"[ZoneView] SELL_ACK 0x{LastSellAck:X4}{(LastSellAck == 0x0381 ? " (OK)" : " (rejected)")}");
            }
        }
        else if (op == OpItemBuyAck)
        {
            // 2-byte result code for our BUY_REQ. 0x0201 = success (item added via CELLCHANGE);
            // anything else (e.g. 0x0204) = rejected. Record so the driver verifies the buy took.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                LastBuyAck = p[0] | (p[1] << 8);
                LastBuyAckUtc = DateTime.UtcNow;
                BuyAckCount++;
                _log?.Invoke($"[ZoneView] BUY_ACK 0x{LastBuyAck:X4}{(LastBuyAck == 0x0201 ? " (OK)" : " (rejected)")}");
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
                    // Keep mob positions live as they move. A mob RUNS (0x201A) when aggro'd vs
                    // WALKS (0x2018) when idle — and an aggro'd mob runs ALONG THE VECTOR AT its
                    // target. So compare the run heading to the direction to us (angle), not a
                    // hardcoded distance (aggro range varies per mob). If another nearby player
                    // sits at a similar angle the target is ambiguous -> "maybe aggro'd" (track
                    // the uncertainty) rather than a confident aggro.
                    var (ox, oy) = (npc.X, npc.Y);
                    _npcs[hnd] = npc with { X = toX, Y = toY };
                    // A player-side mob (town guard) running near us isn't aggro — skip it.
                    if (op == OpSomeoneMoveRun && IsHuntableMob?.Invoke(npc.MobId) != false
                        && SelfPositionProvider?.Invoke() is { } me)
                    {
                        double hx = (double)toX - ox, hy = (double)toY - oy;          // run heading
                        if (Cos(hx, hy, (double)me.X - ox, (double)me.Y - oy) > 0.94)  // running ~at us
                        {
                            var ambiguous = _nearby.Values.Any(pl =>
                                pl.Handle != SelfHandle && Cos(hx, hy, (double)pl.X - ox, (double)pl.Y - oy) > 0.94);
                            if (ambiguous)
                            {
                                _maybeAggressors[hnd] = DateTime.UtcNow;
                                _log?.Invoke($"[ZoneView] mob {npc.MobId} (h={hnd}) running our way — MAYBE aggro (a player shares the angle)");
                            }
                            else
                            {
                                _aggressors[hnd] = DateTime.UtcNow;
                                LastHitAtUtc = DateTime.UtcNow;   // charging at me -> in combat
                                _log?.Invoke($"[ZoneView] mob {npc.MobId} (h={hnd}) running at us — AGGRO");
                            }
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
                _npcs.Clear(); _recentNpcs.Clear(); _npcSeed.Clear();  // entities are per-map; the new map re-broadcasts
                _nearby.Clear();
                _drops.Clear();  // ground items are per-map too
                lock (_selfAbstateLock) _selfAbstates.Clear();  // abstates are per-map; server re-broadcasts
                ShopOpenUtc = default;  // any open shop closes when we leave the map
                InScenarioInstance = false;   // left the map → no longer in the instance
                LastScenarioArea = null;  // scenario/instance area is per-map — clear on leaving (else the
                                          // instance driver thinks we're still inside + hoovers field mobs)
                _doorStates.Clear();  // corridor doors are per-instance-run; a re-entry rebuilds them
                _doorNames.Clear(); _doorStateByName.Clear(); // handle→name + name→state overlay seeds, likewise
                _scenarioAckedAreas.Clear(); // acked-areas "done" set is per-instance-run; a re-entry starts fresh
                _log?.Invoke(h.IsCrossServer
                    ? $"[ZoneView] map handoff (cross-server) -> mapId={h.MapId} @({h.X},{h.Y}) via {h.Ip}:{h.Port} wm={h.WmHandle}"
                    : $"[ZoneView] map change (in-band) -> mapId={h.MapId} @({h.X},{h.Y})");
                MapChanged?.Invoke(h);
            }
        }
        else if (op == OpQuestMobKill)
        {
            // NC_QUEST_NOTIFY_MOB_KILL_CMD (Quest dept, cmd 13): the server's authoritative
            // per-kill quest credit — [NumOfActionQuest u8][MobOfQuest × N], MobOfQuest = 4 bytes
            // {u16 objIdx, u16 questId} (decoded from Quest.pcapng: payload 01 00 00 08 00 = 1
            // quest, objIdx 0, questId 8). Each notify = +1 credited kill for that quest. This is
            // the ONLY reliable progress signal — a mob merely dying credits nothing if the quest
            // isn't actually tracking it (the persistence-glitched status-8 quests).
            var p = pkt.Payload.Span;
            if (p.Length >= 1)
            {
                int n = p[0];
                for (int i = 0; i < n && 1 + i * 4 + 4 <= p.Length; i++)
                {
                    int qid = p[1 + i * 4 + 2] | (p[1 + i * 4 + 3] << 8);
                    _questProgress.AddOrUpdate(qid, 1, (_, v) => v + 1);
                    _log?.Invoke($"[ZoneView] QUEST_MOB_KILL quest={qid} credited (total {_questProgress[qid]})");
                }
            }
        }
        else if (op == OpQuestRewardNeedSelect)
        {
            var p = pkt.Payload.Span;
            if (p.Length >= 2) { RewardSelectQuestId = p[0] | (p[1] << 8); _log?.Invoke($"[ZoneView] REWARD_NEED_SELECT quest={RewardSelectQuestId}"); }
        }
        else if (op == OpQuestGiveUpAck)
        {
            // Abandon confirmed — drop the quest from the active view (and its progress) so the
            // driver sees it as no-longer-active and can re-accept it fresh. Without this the
            // active list stays stale and a glitched quest loops on "abandon" forever.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                int qid = p[0] | (p[1] << 8);
                int err = p.Length >= 4 ? (p[2] | (p[3] << 8)) : 0;
                _activeQuests.TryRemove(qid, out _);
                _questProgress.TryRemove(qid, out _);
                _log?.Invoke($"[ZoneView] QUEST_GIVE_UP_ACK quest={qid} err={err} — removed from active");
            }
        }
        else if (op == OpQuestStartAck)
        {
            // NC_QUEST_START_ACK {err u16} — the result of our last START_REQ. No questId on the
            // wire, so attribute it to the quest we just tried to start. err==0 → accepted.
            var p = pkt.Payload.Span;
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : -1;
            RecordQuestAcceptResult(_lastStartReqQuestId, err);
        }
        else if (op == OpQuestSelectStartAck)
        {
            // NC_QUEST_SELECT_START_ACK {nNPCID u16, nQuestID u16, ErrorType u16} — result of a
            // menu-driven SELECT_START. Carries its own questId, so it's self-correlating.
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                int qid = p[2] | (p[3] << 8);
                int err = p[4] | (p[5] << 8);
                RecordQuestAcceptResult(qid, err);
            }
        }
        else if (op == OpQuestErr)
        {
            // NC_QUEST_ERR — generic quest error push (layout not in the PDB). Log the raw bytes so
            // the live churn reveals its shape; attribute to the last start attempt as a best guess.
            var p = pkt.Payload.Span;
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : (p.Length == 1 ? p[0] : -1);
            _log?.Invoke($"[ZoneView] QUEST_ERR raw=[{Convert.ToHexString(p)}] (lastStartReq={_lastStartReqQuestId})");
            if (_lastStartReqQuestId != 0) RecordQuestAcceptResult(_lastStartReqQuestId, err == 0 ? -2 : err);
        }
        else if (op == OpClientItem)
        {
            // Full bag snapshot at login (one frame per box). Decode typed; tolerate
            // any struct quirk so a bad frame never kills the read loop.
            try
            {
                // Hand-parse (like ZoneEntry) to read box + per-item stack count, which the typed
                // struct doesn't expose: [num u8][box u8][flag u8] then [datasize u8][location u16]
                // [itemid u16][attr…]; count = the lot byte/word after itemid (attr size = datasize-4).
                var p = pkt.Payload.Span;
                if (p.Length >= 3 && p[1] == MainBag)
                {
                    int num = p[0], off = 3;
                    for (int i = 0; i < num && off + 5 <= p.Length; i++)
                    {
                        int datasize = p[off];
                        var slot = p[off + 1]; // inven low byte = slot
                        var itemId = (ushort)(p[off + 3] | (p[off + 4] << 8));
                        int attr = datasize - 4;
                        int count = (attr == 1 && off + 5 < p.Length) ? p[off + 5]
                                  : (attr == 2 && off + 6 < p.Length) ? (p[off + 5] | (p[off + 6] << 8)) : 1;
                        if (itemId != 0) { _inventory[slot] = itemId; _invCount[slot] = count; }
                        off += 1 + datasize;
                    }
                }
            }
            catch { /* skip unparseable inventory frame */ }
        }
        else if (op == OpCellChange)
        {
            // [exchange:2][location:2][itemid:2][attr…] — a slot gained/changed an item. location
            // encodes box (>>10) + slot (&0xFF); only track the MAIN BAG so mini-house/premium box
            // changes don't collide with / clobber bag slots.
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var location = (ushort)(p[2] | (p[3] << 8));
                if (BoxOf(location) == MainBag)
                {
                    var slot = (byte)(location & 0xFF);
                    var itemId = (ushort)(p[4] | (p[5] << 8));
                    if (itemId != 0)
                    {
                        _inventory[slot] = itemId;
                        // stack count = the lot after itemid: len 7 = byte-lot, len 8 = word-lot,
                        // bigger = gear/complex (count 1). Mirrors the CLIENT_ITEM layout.
                        _invCount[slot] = p.Length == 7 ? p[6]
                                        : p.Length == 8 ? (p[6] | (p[7] << 8)) : 1;
                    }
                    else { _inventory.TryRemove(slot, out _); _invCount.TryRemove(slot, out _); }
                }
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
        else if (op == OpDropedItem)
        {
            // An item hit the ground (mob death / player drop). Typed parse: the struct is
            // {handle, itemid, location.xy, dropmobhandle, attr}. Track it so loot can find it.
            try
            {
                var d = pkt.ReadBody<PROTO_NC_BRIEFINFO_DROPEDITEM_CMD>();
                var gi = new GroundItem(d.handle, d.itemid, d.location.x, d.location.y, d.dropmobhandle);
                _drops[d.handle] = gi;
                LogV($"[ZoneView] drop appeared: item {gi.ItemId} (h={gi.Handle}) @({gi.X},{gi.Y}) from mob h={gi.DropMobHandle}");
                DropAppeared?.Invoke(gi);
            }
            catch { /* skip an unparseable drop frame */ }
        }
        else if (op == OpMapLogout)
        {
            // Universal "this handle left view": for a ground item it was picked (by anyone)
            // or despawned; for a char/mob it walked out / died. Retire from whichever
            // collection holds it (same cleanup as BriefDelete, plus drops).
            var hnd = pkt.ReadBody<PROTO_NC_MAP_LOGOUT_CMD>().handle;
            if (_drops.TryRemove(hnd, out var goneDrop))
            {
                _log?.Invoke($"[ZoneView] drop gone: item {goneDrop.ItemId} (h={hnd})");
                DropRemoved?.Invoke(hnd);
            }
            if (_nearby.TryRemove(hnd, out var gonePlayer))
            {
                LogV($"[ZoneView] player left (logout): {gonePlayer.Name} (h={hnd})");
                PlayerLeft?.Invoke(hnd);
            }
            if (_npcs.TryRemove(hnd, out var goneNpc)) StashRecentNpc(hnd, goneNpc); // sticky-hold mobs through AoI flicker
        }
        else if (op == OpPickAck)
        {
            // Result of OUR pickup. Error 0x341 was seen on a SUCCESS (the bag still gained
            // the item via CELLCHANGE), so don't treat Error!=0 as failure here — surface it
            // raw and let callers judge success by the inventory change.
            // The ack also paces the NEXT pick (operator 2026-07-02: the server handles ONE
            // item-cell at a time — pick→ack→pick→ack, never pick-pick-pick):
            PickPending = false;
            try
            {
                var a = pkt.ReadBody<PROTO_NC_ITEM_PICK_ACK>();
                var r = new PickResult(a.itemid, a.lot, a.error);
                LastPickResult = r;
                // Inventory-full (0x346, itemid 0xFFFF) → flag a full bag so the driver sells/declutters
                // instead of pacing over an un-pickable drop. A real pick (a valid itemid) means there was
                // room → clear it.
                if (r.Error == PickInventoryFull) { if (!BagFull) _log?.Invoke("[ZoneView] BAG FULL (pick ack 0x346) — needs a sell/declutter trip"); BagFull = true; }
                else if (r.ItemId != 0xFFFF) BagFull = false;
                // 0x341 is the SUCCESS code (the bag gained the item — confirmed in KillAndPickupItems.pcapng),
                // 0x346 is bag-full; label it so the trace isn't misread as a failure (cost an earlier session).
                var pickStatus = r.Error switch { PickSuccess => "OK", PickInventoryFull => "BAG FULL", _ => $"0x{r.Error:X}" };
                _log?.Invoke($"[ZoneView] pick ack: item {r.ItemId} lot {r.Lot} -> {pickStatus}");
                PickedUp?.Invoke(r);
            }
            catch { /* skip an unparseable pick ack */ }
        }
        else if (op == OpSortAck)
        {
            // Result of the bot's inventory auto-sort (0x304A). The compacted/stacked layout arrives
            // as the ensuing CELLCHANGE burst (already applied by the item model); just note the ack.
            try
            {
                var a = pkt.ReadBody<PROTO_NC_ITEM_AUTO_ARRANGE_INVEN_ACK>();
                _log?.Invoke($"[ZoneView] inventory auto-sorted (ack 0x304B err=0x{a.err:X})");
            }
            catch { /* skip an unparseable sort ack */ }
        }
        else if (op == OpClientSkill)
        {
            // Learned-skill list at zone login: header then `number` × 12-byte blocks,
            // each leading with the skill id (u16). Hand-parse (house style) so a struct
            // quirk never kills the read loop.
            var p = pkt.Payload.Span;
            if (p.Length >= SkillListHeaderLen)
            {
                var number = (ushort)(p[8] | (p[9] << 8));
                var added = 0;
                for (var i = 0; i < number; i++)
                {
                    var off = SkillListHeaderLen + i * SkillBlockLen;
                    if (off + 2 > p.Length) break;
                    var skillId = (ushort)(p[off] | (p[off + 1] << 8));
                    // id 0 is a REAL skill (e.g. ActiveSkill.ID=0 "Slice and Dice [01]"/TripleHit01), not
                    // an empty-slot sentinel — the packet's own `number` field already bounds the loop to
                    // real entries only. Filtering `!= 0` here made hasSkill(0) permanently false even
                    // after the server confirmed it learned (0x70B), causing an endless re-learn retry
                    // (operator 2026-07-01 — same bug class as "item id 0 = Leather Boots").
                    if (_skills.TryAdd(skillId, 1)) added++;
                }
                if (added > 0)
                {
                    _log?.Invoke($"[ZoneView] learned skills: {string.Join(",", _skills.Keys.OrderBy(k => k))}");
                    SkillsChanged?.Invoke();
                }
            }
        }
        else if (op == OpClientPassive)
        {
            // Login PASSIVE-skill list (0x103E): {number u16 @0, passive u16[number] @2}. Hand-parse
            // (no FiestaLib struct for the 0x103E variant). IkFresh: 01 00 09 00 = 1 passive, id 9.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                var number = (ushort)(p[0] | (p[1] << 8));
                var added = 0;
                for (var i = 0; i < number; i++)
                {
                    var off = 2 + i * 2;
                    if (off + 2 > p.Length) break;
                    var pid = (ushort)(p[off] | (p[off + 1] << 8));
                    if (pid != 0 && _passives.TryAdd(pid, 1)) added++;
                }
                if (added > 0)
                {
                    _log?.Invoke($"[ZoneView] learned passives: {string.Join(",", _passives.Keys.OrderBy(k => k))}");
                    SkillsChanged?.Invoke();
                }
            }
        }
        else if (op == OpSkillLearnSuc)
        {
            // NC_SKILL_SKILL_LEARNSUC_CMD (0x4804): the server CONFIRMS a skill was learned (e.g. after
            // using a skill scroll) = {skillId u16 @0, level u8 @2}. Without handling this, learnedSkills()
            // stayed at the login seed and castRotation never used a freshly-learned skill. Add it so the
            // bot recognizes + casts the real combat skills it just bought from the skill master.
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                var skillId = (ushort)(p[0] | (p[1] << 8));
                var lvl = p.Length >= 3 ? p[2] : (byte)0;
                if (_skills.TryAdd(skillId, 1))
                {
                    _log?.Invoke($"[ZoneView] SKILL LEARNED: id={skillId} lv{lvl} (now know {_skills.Count})");
                    SkillsChanged?.Invoke();
                }
            }
        }
        else if (op == OpSkillLearnFail)
        {
            // NC_SKILL_SKILL_LEARNFAIL_CMD: the server REJECTED the scroll-learn. Log the raw bytes
            // (err code) so we see WHY (prerequisite? already known? wrong class? not at trainer?).
            var p = pkt.Payload.Span;
            var hex = Convert.ToHexString(p.Length > 8 ? p.Slice(0, 8) : p);
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : (p.Length == 1 ? p[0] : -1);
            _log?.Invoke($"[ZoneView] SKILL LEARN FAILED — err={err} ({p.Length}b: {hex})");
        }
        else if (op == OpItemUseAck)
        {
            // NC_ITEM_USE_ACK {error u16 @0, useditem u16 @2, invenType u8 @4}. Map the error so a
            // failed scroll/item use is human-readable. Codes from Skills.pcapng (operator-annotated):
            //   0x700 ok · 0x708 skill level too low · 0x70B already know the skill.
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                int err = p[0] | (p[1] << 8);
                int item = p[2] | (p[3] << 8);
                LastUseAckError = err;
                LastUseAckItem = item;
                var meaning = err switch
                {
                    0x700 => "ok",
                    0x708 => "FAIL: skill level too low",
                    0x70B => "FAIL: already know the skill",
                    _ => $"err 0x{err:X}",
                };
                if (err != 0x700) _log?.Invoke($"[ZoneView] item USE item={item} -> {meaning} (0x{err:X})");
            }
        }
        else if (op == OpQuestScriptReq)
        {
            // Server quest-dialogue step: [questId u16][STRUCT_QSC...] — QSC command code is
            // the first STRUCT_QSC byte (payload offset 2). Track as the pending step to answer.
            var p = pkt.Payload.Span;
            if (p.Length >= 3)
            {
                var questId = (ushort)(p[0] | (p[1] << 8));
                var qsc = p[2];
                // STRUCT_QSC: Cmd(u32)@2, IsPigeonStartType@6, Data@7. For a SAY (Cmd 2) the
                // first Data word is the QuestDialog text id being spoken (e.g. 202).
                int dialogId = p.Length >= 11 ? (p[7] | (p[8] << 8) | (p[9] << 16) | (p[10] << 24)) : 0;
                var step = new QuestStep(questId, qsc, dialogId);
                PendingQuest = step;
                _questScript.Enqueue(step);  // queue every page so a burst isn't collapsed to just the last
                // Keep the active/done view current as the script runs: Cmd 0x06 = ACCEPT
                // (quest becomes active), Cmd 0x0A = DONE (completed). Lets the driver re-derive
                // availability mid-session without waiting for a relog's QUEST_DONE burst.
                if (qsc == 0x06) MarkQuestActive(questId);
                else if (qsc == 0x0A) MarkQuestDone(questId);
                _log?.Invoke($"[ZoneView] quest dialogue: quest {questId} qsc=0x{qsc:X2} dialog={dialogId} (answer to proceed)");
                QuestPrompt?.Invoke(step);
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
        var player = new NearbyPlayer(c.handle, name, c.chrclass, c.Level, c.coord.xy.x, c.coord.xy.y,
            c.mode, c.type, c.nKQTeamType);
        var isNew = !_nearby.ContainsKey(c.handle);
        _nearby[c.handle] = player;
        if (isNew)
        {
            // type / nKQTeamType distinguish a real player from a scenario/KQ enemy "character" (the JCQ
            // promotion "shadow" clones arrive via this same LOGINCHARACTER packet) — log them so we can
            // classify hostile scenario entities as huntable and fight them via the normal combat path.
            LogV($"[ZoneView] player appeared: {name} (h={c.handle} class={c.chrclass} lvl={c.Level} mode={c.mode} type={c.type} kqTeam={c.nKQTeamType})");
            PlayerAppeared?.Invoke(player);
        }
    }

    // REGENMOB record layout (fixed 149 bytes — verified against Full.pcapng):
    // handle u16 | mode u8 | mobid u16 | x u32 | y u32 | dir u8 | flagstate u8 |
    // flag-blob[99] (gate dest-map string when flagstate==1) | sAnimation[32] | 3 tail.
    private const int MobRecordLen = 149;
    private const int FlagBlobOffset = 15; // within a record
    private const int MobTeamOffset = 147; // nKQTeamType, within a record (3-byte tail: animLvl, team, regenAni)

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
        // nKQTeamType (record offset 147) — faction/team byte; tells allies (guards) from
        // enemies. Defensive read in case the last record is truncated.
        var team = (off + MobTeamOffset < p.Length) ? p[off + MobTeamOffset] : (byte)0;

        var npc = new NearbyNpc(handle, mobid, mode, x, y, flag, linkMap, team);
        var isNew = !_npcs.ContainsKey(handle);
        _npcs[handle] = npc;
        _recentNpcs.TryRemove(handle, out _); // back in view (live) → drop the sticky flicker-bridge copy
        // THE SEED: record every NPC/gate by mobId (the bulk 0x1C09 on map-enter populates this fully).
        // Authoritative roster, kept until map change — the navigation source of truth.
        _npcSeed[mobid] = new NpcSeedEntry(mobid, x, y, flag == 1, linkMap);
        if (isNew)
            LogV(flag == 1
                ? $"[ZoneView] gate appeared: id={mobid} h={handle} @({x},{y}) -> {linkMap}"
                : $"[ZoneView] npc/mob appeared: id={mobid} h={handle} @({x},{y}) mode={mode} flag={flag} team={team}");
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
            LogV($"[ZoneView] move speed: {WalkSpeed:F0} -> {newSpeed:F0} u/s (raw: walk={rawWalk} run={rawRun}, {source})");
            WalkSpeed = newSpeed;
            WalkSpeedChanged?.Invoke(newSpeed);
        }
    }

    /// <summary>Cosine of the angle between vectors (ax,ay) and (bx,by) — 1 = same direction.
    /// Used to tell if a mob's run heading points along the direction to a target. 0 if either
    /// vector is zero-length.</summary>
    private static double Cos(double ax, double ay, double bx, double by)
    {
        var ma = Math.Sqrt(ax * ax + ay * ay);
        var mb = Math.Sqrt(bx * bx + by * by);
        return ma < 1e-6 || mb < 1e-6 ? 0 : (ax * bx + ay * by) / (ma * mb);
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
