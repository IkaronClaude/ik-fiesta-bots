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
    // ✅ THE NPC SEED — the single authoritative full-map roster, keyed by mobId, holding position + the
    // gate flag + link-destination map. Populated by the bulk 0x1C09 NC_BRIEFINFO_MOB_CMD on map-enter
    // (ALL NPCs+gates at infinite range, as on the minimap) and any later 0x1C09/REGENMOB. Cleared on
    // map-change, NOT pruned on BRIEFINFODELETE (NPCs are static). SOURCE OF TRUTH for NPC + gate
    // positions — navigation (quest giver / merchant / gate / cross-map hop) reads from HERE.
    private readonly ConcurrentDictionary<int, NpcSeedEntry> _npcSeed = new();
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
    public IReadOnlyCollection<NearbyNpc> NearbyNpcs => _npcs.Values.ToArray();

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
        if (SelfHandle is { } me && h.Attacker == me) LastDamageDealtAtUtc = DateTime.UtcNow;
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
            if (itemId == 0) continue;
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
            _npcs.TryRemove(hnd, out _); // the same delete also retires NPCs/mobs
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
                if (_npcs.TryRemove(dead, out _) && mine) { LastKill = dead; KillsByMe++; _log?.Invoke($"[combat] KILLED mob h={dead} (totalKills={KillsByMe})"); }
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
                LogV($"[ZoneView] MOVEFAIL — server snapped us to ({bx},{by})");
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
                _npcs.Clear(); _npcSeed.Clear(); _nearby.Clear(); _drops.Clear();
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
            if (kind is null or true)
            {
                bool full = Hp is { } hp && MaxHp > 0 && hp >= MaxHp;
                if (!full && kind is not null)
                {
                    if (!HpStoneDepleted) _log?.Invoke("[ZoneView] soul-stone HP USE FAILED (0x5006) at non-full HP — reserve empty, need restock");
                    HpStoneDepleted = true;
                    HpStones = 0;
                }
                else if (kind is null)
                    _log?.Invoke("[ZoneView] soul-stone USE FAILED (0x5006) with no pending USE — ignoring (can't attribute HP vs SP)");
            }
            else
            {
                bool full = Sp is { } sp && MaxSp > 0 && sp >= MaxSp;
                if (!full)
                {
                    if (!SpStoneDepleted) _log?.Invoke("[ZoneView] soul-stone SP USE FAILED (0x5006) at non-full SP — reserve empty, need restock");
                    SpStoneDepleted = true;
                    SpStones = 0;
                }
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
                _npcs.Clear(); _npcSeed.Clear();  // entities are per-map; the new map re-broadcasts
                _nearby.Clear();
                _drops.Clear();  // ground items are per-map too
                ShopOpenUtc = default;  // any open shop closes when we leave the map
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
            _npcs.TryRemove(hnd, out _);
        }
        else if (op == OpPickAck)
        {
            // Result of OUR pickup. Error 0x341 was seen on a SUCCESS (the bag still gained
            // the item via CELLCHANGE), so don't treat Error!=0 as failure here — surface it
            // raw and let callers judge success by the inventory change.
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
                _log?.Invoke($"[ZoneView] pick ack: item {r.ItemId} lot {r.Lot} error 0x{r.Error:X}");
                PickedUp?.Invoke(r);
            }
            catch { /* skip an unparseable pick ack */ }
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
        var player = new NearbyPlayer(c.handle, name, c.chrclass, c.Level, c.coord.xy.x, c.coord.xy.y);
        var isNew = !_nearby.ContainsKey(c.handle);
        _nearby[c.handle] = player;
        if (isNew)
        {
            LogV($"[ZoneView] player appeared: {name} (h={c.handle} class={c.chrclass} lvl={c.Level})");
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
