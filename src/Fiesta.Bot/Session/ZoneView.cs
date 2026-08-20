using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Navigation;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Session;

/// <summary>A player the bot can currently see in zone (from Briefinfo broadcasts)</summary>
public sealed record NearbyPlayer(ushort Handle, string Name, byte Class, byte Level, uint X, uint Y,
    byte Mode = 0, byte Type = 0, byte KQTeamType = 0)
{
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An NPC/mob the bot can see in zone (from the MOB briefinfo the zone broadcasts on field enter)</summary>
public enum ShopKind { Unknown, Item, Weapon, Skill, SoulStone, Storage }

/// <summary>Facing, from the REGENMOB record's dir byte (record offset 13)</summary>
public sealed record NearbyNpc(ushort Handle, ushort MobId, byte Mode, uint X, uint Y, byte Flag = 0, string? LinkMap = null, byte Team = 0, byte Dir = 0,
    bool IsScenarioClone = false, string? CharName = null, byte? CharLevel = null)
{
    public bool IsGate => Flag == 1;
    /// <summary>May be used to look this entity up in MobInfo?</summary>
    public bool HasMobId => !IsScenarioClone;
    /// <summary>nKQTeamType from the mob briefinfo (record offset 147): a King's-Quest battlefield team</summary>
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An in-zone chat line overheard from a nearby speaker</summary>
public sealed record ChatMessage(ushort Handle, string? SenderName, string Text)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An item lying on the ground, broadcast by NC_BRIEFINFO_DROPEDITEM_CMD (0x1C0A) when a mob dies (or a player dr…</summary>
public sealed record GroundItem(ushort Handle, ushort ItemId, uint X, uint Y, ushort DropMobHandle)
{
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Result of a pickup attempt, from NC_ITEM_PICK_ACK (0x300A): + picked, plus the raw code</summary>
public sealed record PickResult(ushort ItemId, uint Lot, ushort Error)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A pending quest-dialogue step the server is prompting (NC_QUEST_SCRIPT_CMD_REQ, 0x4401): + the QSC command cod…</summary>
public sealed record QuestStep(ushort QuestId, byte Qsc, int DialogId = 0)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>One option of a server menu (0x3C01 SERVERMENU): is the byte to send back in SERVERMENU_ACK (0x3C02) to select…</summary>
public sealed record ServerMenuOption(byte Reply, string Text);

/// <summary>One entry in the MAP-ENTER NPC SEED — the authoritative full-map roster the server sends in the bulk NC_BRIEFI…</summary>
public sealed record NpcSeedEntry(int MobId, uint X, uint Y, bool IsGate, string? LinkMap);

/// <summary>A scenario corridor DOOR's runtime state, from NC_SCENARIO_DOORSTATE_CMD (0x6C09)</summary>
public sealed record DoorState(ushort Handle, byte State, uint? X, uint? Y);

/// <summary>A combat hit broadcast: hit for , leaving the defender at</summary>
public sealed record HitInfo(ushort Attacker, ushort Defender, ushort Damage, uint RestHp)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A live, read-only model of what one bot perceives in its zone, built by decoding the inbound frames a fans out…</summary>
public sealed class ZoneView : IDisposable
{
    private static readonly ushort OpBriefChar = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_CHARACTER_CMD>();
    private static readonly ushort OpBriefLogin = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD>();
    private static readonly ushort OpBriefDelete = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_BRIEFINFODELETE_CMD>();
    private static readonly ushort OpReallyKill = PacketRegistry.GetOpcode<PROTO_NC_BAT_REALLYKILL_CMD>();
    private static readonly ushort OpBriefMob = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_MOB_CMD>();
    private static readonly ushort OpRegenMob = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_REGENMOB_CMD>();
    // Mover (mount) ride state — self only (0xCC02/0xCC06; 0xCC04 = someone else)
    private const ushort OpItemRelocAck = 0x300C;
    private const ushort OpCreateCastBar = 0x2047;
    private const ushort OpCancelCastBar = 0x2048;
    private const ushort OpActMoveSpeed = 0x203E;
    private const ushort OpMoverRideOn = 0xCC02;
    private const ushort OpMoverRideOff = 0xCC06;
    private const ushort OpMoveSpeed = 0xCC0D;
    // Map transition (gate / town portal): LINKSAME = in-band map change on the same zone server, LINKOTHER = handof…
    private const ushort OpMapLinkSame = 0x1809;
    private const ushort OpMapLinkOther = 0x180A;
    // NC_QUEST_NOTIFY_MOB_KILL_CMD (Quest dept 0x11, cmd 13) = live quest kill-progress
    private const ushort OpQuestMobKill = 0x440D;
    // NC_QUEST_REWARD_NEED_SELECT_ITEM_CMD (cmd 18) = server asks the client to choose a reward during a turn-in (th…
    private const ushort OpQuestRewardNeedSelect = 0x4412;
    // NC_QUEST_GIVE_UP_ACK (cmd 8) = server confirms a quest abandon {questId, errorCode}
    private const ushort OpQuestGiveUpAck = 0x4408;
    // NC_QUEST_START_ACK (cmd 21) = result of START_REQ {err u16}
    private const ushort OpQuestStartAck = 0x4415;
    // NC_QUEST_SELECT_START_ACK (cmd 16) = result of a menu SELECT_START {nNPCID, nQuestID, ErrorType}
    private const ushort OpQuestSelectStartAck = 0x4410;
    // NC_QUEST_ERR (cmd 19) = generic quest error push
    private const ushort OpQuestErr = 0x4413;

    /// <summary>Quest id the server is currently asking us to pick a reward for (from 0x4412), or null</summary>
    public int? RewardSelectQuestId { get; private set; }
    public void ClearRewardSelect() => RewardSelectQuestId = null;
    /// <summary>Known NC_BAT_SKILLBASH_CAST_FAIL_ACK reason codes (empirically captured, not in FiestaLib enums)</summary>
    public static class CastFailReason
    {
        public const ushort NotEnoughSp = 0x0FC9;
        /// <summary>The server has us in NON-battle mode, so no skill can be cast until we re-send CHANGEMODE_REQ.</summary>
        public const ushort NonBattleMode = 0x0FC0;
        public const ushort OutOfRange = 0x0FCA;

        public const ushort NotReady = 0x0FC8;
        // 0x0FC4, 0x0FC6 — unpinned (facing / weapon type)

        /// <summary>Human-readable description of a cast-fail code, for logs and the on_cast_fail script hook (so failures like "n…</summary>
        public static string Describe(ushort code) => code switch
        {
            0x0FC0      => "cannot use the skill while in NONBATTLE MODE (0x0FC0)",
            0x0FC1      => "cannot use the skill right after logging in (0x0FC1)",
            0x0FC2      => "target logged in just now (0x0FC2)",
            0x0FC3      => "cannot use the skill in this field (0x0FC3)",
            0x0FC4      => "already casting another skill (0x0FC4)",
            0x0FC5      => "the skill has been reserved (0x0FC5)",
            0x0FC6      => "incorrect skill (0x0FC6)",
            0x0FC7      => "silenced (0x0FC7)",
            NotReady    => "skill on cooldown — 'cannot use the skill yet' (0x0FC8)",
            NotEnoughSp => "not enough SP (0x0FC9)",
            OutOfRange  => "the target is OUT OF CASTING RANGE (0x0FCA)",
            0x0FCB      => "cannot find the target (0x0FCB)",
            0x0FCC      => "target is in Fear state (0x0FCC)",
            0x0FCD      => "skill did not finish normally (0x0FCD)",
            0x0FCE      => "skill use is prohibited in this area (0x0FCE)",
            0x0FD1 or 0x0FD2 or 0x0FD3 or 0x0FD5 or 0x0FD6 => $"failed to cast the skill (0x{code:X4})",
            0x0FD4      => "a higher-level effect is already active (0x0FD4)",
            0x0FD7      => "target cannot be healed at this time (0x0FD7) — are we aiming a heal at a MOB?",
            0x0FD8      => "target is under Blessing of Teva (0x0FD8)",
            _           => $"cast failed (0x{code:X4}) — not in the decoded table; see docs/ERROR_CODE_RUNBOOK.md",
        };
    }

    // MOVEFAIL (ACT cmd 27): the server rejected our last move (walked into an obstacle the static grid doesn't have…
    private const ushort OpActMoveFail = 0x201B;
    private DateTime _lastMoveFailLog = DateTime.MinValue;   // throttle for the NOTE-level MOVEFAIL desync diag
    // Time of the last SIGNIFICANT MOVEFAIL (a real shove-back, not a <64u micro-correction)
    private DateTime _lastSignificantMoveFailUtc = DateTime.MinValue;
    // Abnormal-state set/reset on an entity: NC_BAT_ABSTATESET_CMD (0x2427) / _RESET (0x2428)
    private const ushort OpAbStateSet = 0x2427;
    private const ushort OpAbStateReset = 0x2428;
    // NC_BRIEFINFO_ABSTATE_CHANGE_CMD (0x1C18) / _LIST_CMD (0x1C19): the PERCEPTION channel for abnormal states — an…
    private const ushort OpBriefAbstateChange = 0x1C18;
    private const ushort OpBriefAbstateChangeList = 0x1C19;
    // Other players' movement broadcasts: SOMEONE_MOVEWALK (ACT cmd 24) / MOVERUN (cmd 26)
    private const ushort OpSomeoneMoveWalk = 0x2018;
    private const ushort OpSomeoneMoveRun = 0x201A;
    // Server menu (0x3C01): a Yes/No or list prompt an NPC/gate opens
    private const ushort OpMenuServerMenu = 0x3C01;
    // Shop open (Menu dept 0x0F): the server sends a sell list when you click an NPC
    private static readonly ushort[] OpShopOpen =
        { 0x3C03, 0x3C04, 0x3C06, 0x3C09, 0x3C0A, 0x3C0B };
    // Soul-stone shop open (Menu cmd 5 = 0x3C05, NC_MENU_SHOPOPENSOULSTONE_CMD): a soul-stone merchant's shop
    private const ushort OpShopOpenSoulStone = 0x3C05;
    // Personal STORAGE (warehouse)
    private const ushort OpStorageOpenFail = 0x3C07;
    private const ushort OpStorageOpen = 0x3C08;
    // NC_MENU_RANDOMOPTION_CMD (Menu cmd 14 = 0x3C0E): a NON-shop NPC menu (the Anvil reforge/reroll service)
    private const ushort OpMenuRandomOption = 0x3C0E;
    // NPC menu (Act cmd 28 = 0x201C): clicking a merchant/script NPC makes the server open its menu and wait for the…
    private const ushort OpActNpcMenuOpen = 0x201C;
    // Soul-stone reserve BUY ack (HP 0x5003 / SP 0x5004): the server confirms a charge purchase and reports the new…
    private const ushort OpSoulStoneHpBuyAck = 0x5003;
    private const ushort OpSoulStoneSpBuyAck = 0x5004;
    // Soul-stone USE result (verified C->S 0x5007 -> S->C 0x5008/0x5006 in this server's packetlog): HP_USESUC 0x500…
    private const ushort OpSoulStoneHpUseSuc = 0x5008;
    private const ushort OpSoulStoneSpUseSuc = 0x500A;
    private const ushort OpSoulStoneUseFail = 0x5006;
    // Soul-stone BUY fail (0x5005 NC_SOULSTONE_BUYFAIL_ACK {err u16}): the server's definitive "no" to a 0x5001/0x50…
    private const ushort OpSoulStoneBuyFail = 0x5005;
    // Death/revive (Char dept): DEADMENU 0x104D = server opens the death menu (you died); REVIVE_REQ 0x104E (C->S) =…
    private const ushort OpCharDeadMenu = 0x104D;
    private const ushort OpCharReviveSame = 0x104F;
    private const ushort OpCharReviveOther = 0x1050;
    // NC_CHAR_LEVEL_CHANGED_CMD (Char dept, cmd 116): {wmhandle u16, charNo u32, newLevel u8}
    private const ushort OpCharLevelChanged = 0x1074;
    // NC_CHAR_PROMOTE_ACK (Char cmd 89 = 0x1059): {newclass u8}
    private const ushort OpCharPromoteAck = 0x1059;
    // NC_SCENARIO_AREAENTRY_REQ (Scenario cmd 5 = 0x6C05): {Name8 areaindex}
    private const ushort OpScenarioAreaEntryReq = 0x6C05;
    // NC_SCENARIO_OBJTYPECHANGE_CMD (Scenario cmd 11 = 0x6C0B): {handle u16, type u8}
    private const ushort OpScenarioObjTypeChange = 0x6C0B;
    private const byte ScenObjTypeMob = 5;   // change2mob → fightable
    private const byte ScenObjTypeNpc = 4;   // change2npc → non-combatant (clone leaving the fight)
    // NC_SCENARIO_DOORSTATE_CMD (Scenario cmd 9 = 0x6C09): {door u16 (entity HANDLE), doorstate u8}
    private const ushort OpScenarioDoorState = 0x6C09;
    // NC_BRIEFINFO_BUILDDOOR_CMD (Briefinfo cmd 15 = 0x1C0F): {handle u16, mobid u16, coord, doorstate u8, Name8 blo…
    private const ushort OpBriefInfoBuildDoor = 0x1C0F;
    // NC_BAT_EXPGAIN_CMD (Bat cmd 11 = 0x240B): {expgain u32@0, mobhandle u16@4}
    private const ushort OpBatExpGain = 0x240B;
    // NC_BAT_EXPLOST_CMD (Bat cmd 17 = 0x2411): the exp PENALTY on death — {explost u32@0}
    private const ushort OpBatExpLost = (ushort)(((int)ProtocolCommand.Bat << 10) | 17);
    private const ushort OpBatCeaseFire = (ushort)(((int)ProtocolCommand.Bat << 10) | 61);
    // NC_BAT_TARGETINFO_CMD — the server CONFIRMING our selection, and the only proof it has committed it
    private const ushort OpBatTargetInfo = (ushort)(((int)ProtocolCommand.Bat << 10) | 2);
    // NC_CHAR_EXP_CHANGED_CMD (Char cmd 115 = 0x1073): an AUTHORITATIVE absolute exp value — {wmhandle u16@0, CharNo…
    private const ushort OpCharExpChanged = (ushort)(((int)ProtocolCommand.Char << 10) | 115);
    // NC_BAT_SKILLBASH_CAST_FAIL_ACK (Bat cmd 52 = 0x2434): the server rejected a skill cast
    private const ushort OpBatCastFail = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.SkillbashCastFailAck);
    private const ushort OpBatCastSuc = (ushort)(((int)ProtocolCommand.Bat << 10) | 53);
    private const ushort OpBatHitObjStart = (ushort)(((int)ProtocolCommand.Bat << 10) | 78);
    private const ushort OpBatHitDamage = (ushort)(((int)ProtocolCommand.Bat << 10) | 82);
    private const ushort OpBatCastAbort = (ushort)(((int)ProtocolCommand.Bat << 10) | 55);
    private const ushort OpBatCastCut = (ushort)(((int)ProtocolCommand.Bat << 10) | 56);
    private static readonly ushort OpClientItem = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_ITEM_CMD>();
    private static readonly ushort OpCellChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_CELLCHANGE_CMD>();
    private static readonly ushort OpEquipChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_EQUIPCHANGE_CMD>();
    // NC_CHAR_CENCHANGE_CMD (Char 0x1033): {cen u64} = the new money ("cen") total
    private static readonly ushort OpCenChange = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CENCHANGE_CMD>();
    // NC_ITEM_SELL_ACK (Item 0x3005): a 2-byte result code for our SELL_REQ
    private const ushort OpSellAck = 0x3005;
    // NC_ITEM_BUY_ACK (Item 0x3004): a 2-byte result code for our BUY_REQ
    private const ushort OpItemBuyAck = 0x3004;
    // Self HP/SP change (BAT 0x240E/0x240F): the server's authoritative current HP/SP after any change (combat damag…
    private static readonly ushort OpHpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_HPCHANGE_CMD>();
    private static readonly ushort OpSpChange = PacketRegistry.GetOpcode<PROTO_NC_BAT_SPCHANGE_CMD>();
    // NC_CHAR_CHANGEPARAMCHANGE_CMD (Char dept, cmd 53): a {paramId u8, value u32} list that carries the char's MAX…
    private const ushort OpCharParamChange = 0x1035;
    // Stat-point allocation (CHAR dept 4)
    private static readonly ushort OpStatRemainPoint = PacketRegistry.GetOpcode<PROTO_NC_CHAR_STAT_REMAINPOINT_CMD>(); // 0x105B {byte remain}
    private static readonly ushort OpStatIncSuc = PacketRegistry.GetOpcode<PROTO_NC_CHAR_STAT_INCPOINTSUC_ACK>();       // 0x105F {byte stat}
    private const ushort OpStatIncFail = 0x1061;   // NC_CHAR_STAT_INCPOINTFAIL_ACK (cmd 97) — client-facing fail
    private static string StatName(byte s) => s switch { 0 => "STR", 1 => "END", 2 => "DEX", 3 => "INT", 4 => "MP", _ => $"?{s}" };
    private static readonly ushort OpSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SWING_DAMAGE_CMD>();
    private static readonly ushort OpSomeoneSwingDamage = PacketRegistry.GetOpcode<PROTO_NC_BAT_SOMEONESWING_DAMAGE_CMD>();
    // OUR OWN level-up broadcast (operator 2026-07-15): NC_BAT_LEVELUP_CMD (Bat cmd 12 = 0x240C) fires when we level…
    private static readonly ushort OpBatLevelup = PacketRegistry.GetOpcode<PROTO_NC_BAT_LEVELUP_CMD>();
    // 0x2009 NC_ACT_SOMEONECHANGEMODE_CMD {handle u16 @0, mode u8 @2} — the server telling us who is in BATTLE mode
    private static readonly ushort OpSomeoneChangeMode = PacketRegistry.GetOpcode<PROTO_NC_ACT_SOMEONECHANGEMODE_CMD>();
    // 0x244E — the server CONFIRMING a cast started, and it NAMES the skill (skill u16 @0, targetobj u16 @2, index u…
    private static readonly ushort OpSkillStart = PacketRegistry.GetOpcode<PROTO_NC_BAT_SKILLBASH_HIT_OBJ_START_CMD>();
    // Ground loot: DROPEDITEM (Briefinfo 0x1C0A) broadcasts an item that hit the ground (mob death or a player drop)…
    private static readonly ushort OpDropedItem = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_DROPEDITEM_CMD>();
    private static readonly ushort OpMapLogout = PacketRegistry.GetOpcode<PROTO_NC_MAP_LOGOUT_CMD>();
    private static readonly ushort OpPickAck = PacketRegistry.GetOpcode<PROTO_NC_ITEM_PICK_ACK>();
    // Result of the bot's inventory auto-sort (NC_ITEM_AUTO_ARRANGE_INVEN_ACK, Item 0x304B); the new bag layout arri…
    private static readonly ushort OpSortAck = PacketRegistry.GetOpcode<PROTO_NC_ITEM_AUTO_ARRANGE_INVEN_ACK>();
    // Learned-skill list, sent at zone login (NC_CHAR_CLIENT_SKILL_CMD, Char 0x0F3D): [restempow:1][PartMark:1][nMax…
    private static readonly ushort OpClientSkill = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_SKILL_CMD>();
    // NC_CHAR_CLIENT_PASSIVE_CMD (CHAR dept 4, cmd 62 = 0x103E) — the login PASSIVE-skill list, sent right after the…
    private const ushort OpClientPassive = 0x103E;
    // NC_SKILL_SKILL_LEARNSUC_CMD (SKILL dept 18, cmd 4) — server confirms a skill was learned
    private const ushort OpSkillLearnSuc = 0x4804;
    // NC_SKILL_SKILL_LEARNFAIL_CMD (cmd 5) — server REJECTED a learn (carries the reason err code)
    private const ushort OpSkillLearnFail = 0x4805;
    // NC_ITEM_USE_ACK (ITEM dept 12, cmd 22) — result of using an item
    private const ushort OpItemUseAck = 0x3016;
    // Quest dialogue: the server drives accept/turn-in via NC_QUEST_SCRIPT_CMD_REQ (0x4401) {questId u16, STRUCT_QSC…
    private static readonly ushort OpQuestScriptReq = PacketRegistry.GetOpcode<PROTO_NC_QUEST_SCRIPT_CMD_REQ>();
    private const int SkillListHeaderLen = 10; // restempow+PartMark+nMaxNum+chrregnum+number
    private const int SkillBlockLen = 12;

    private readonly BotSession _session;
    private readonly Action<string>? _log;            // Note channel (also fans out to host stdout)
    private readonly Action<BotLogLevel, string>? _logLevel; // leveled channel for verbose perception spam
    private readonly ConcurrentDictionary<ushort, NearbyPlayer> _nearby = new();
    private readonly ConcurrentDictionary<ushort, NearbyNpc> _npcs = new();   // ⚠ view-scoped (pruned by
    private readonly ConcurrentDictionary<ushort, (NearbyNpc Npc, long Expiry)> _recentNpcs = new();
    // AoI CHURN SUMMARISER (P0 observability fix, 2026-08-05)
    private int _aoiIn, _aoiOut;
    private long _aoiNextFlush;
    private const int AoiFlushMs = 1000;
    private void NoteAoiChurn(bool entered)
    {
        if (entered) _aoiIn++; else _aoiOut++;
        var now = Environment.TickCount64;
        if (_aoiNextFlush == 0) { _aoiNextFlush = now + AoiFlushMs; return; }
        if (now < _aoiNextFlush) return;
        // [AoI] +/- churn line deleted (comment-scrub P0): 2,592 of 65,961 sampled lines, never diagnostic.
        _aoiIn = _aoiOut = 0;
        _aoiNextFlush = now + AoiFlushMs;
    }

    private const int RecentNpcTtlMs = 4000; // long enough to bridge the ~200ms flicker; short enough that a
                                             // genuinely-departed mob is dropped fast (no chasing a ghost)

    // LEARNED per-mob EXP (2026-07-21): the EXPGAIN packet carries {gain, mobHandle}, and a just-killed mob is still…
    private readonly ConcurrentDictionary<int, (long Total, int Kills)> _mobExp = new();
    /// <summary>Learned average exp per kill for a mob id (from EXPGAIN), or 0 if never killed one yet</summary>
    public long MobExpAvg(int mobId) => _mobExp.TryGetValue(mobId, out var v) && v.Kills > 0 ? v.Total / v.Kills : 0;
    // THE NPC SEED — the single authoritative full-map roster, keyed by mobId, holding position + the gate flag + li…
    private readonly ConcurrentDictionary<int, NpcSeedEntry> _npcSeed = new();
    // Same entries, keyed by mob id + POSITION, so several gates/NPCs sharing a mob id all survive
    private readonly ConcurrentDictionary<(int Mob, uint X, uint Y), NpcSeedEntry> _npcSeedAll = new();
    // Scenario DOOR state, keyed by the door entity HANDLE (from 0x6C09 NC_SCENARIO_DOORSTATE_CMD)
    private readonly ConcurrentDictionary<ushort, DoorState> _doorStates = new();
    // Scenario door HANDLE -> .sbi block NAME ("Door04"), from 0x1C0F BUILDDOOR
    private readonly ConcurrentDictionary<ushort, string> _doorNames = new();
    // Scenario door NAME -> current doorstate byte (0 closed / 1 open)
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

    // Verbose (per-frame perception) log: mob/player appeared, MOVEFAIL, speed changes — the firehose that would oth…
    private void LogV(string m) { if (_logLevel is not null) _logLevel(BotLogLevel.Verbose, m); else _log?.Invoke(m); }

    /// <summary>Raised when a player enters (or refreshes in) view</summary>
    public Func<int, string>? QuestNameResolver { get; set; }
    private string QName(int id) => QuestNameResolver?.Invoke(id) ?? $"q{id}";

    /// <summary>Resolves an item id → the skill its book teaches and WHICH TABLE that id is in (set from ClientData.ScrollSkil…</summary>
    public Func<int, (int Id, bool Passive)>? ScrollSkillResolver { get; set; }

    public event Action<NearbyPlayer>? PlayerAppeared;

    /// <summary>Raised when a tracked handle leaves view</summary>
    public event Action<ushort>? PlayerLeft;

    /// <summary>Raised for every overheard nearby chat line</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>Raised when the zone moves the bot to another map (gate / town portal)</summary>
    public event Action<MapHandoff>? MapChanged;

    /// <summary>The server map id (MapInfo.ID) the bot is currently on, as last reported by a transition</summary>
    public ushort? CurrentMapId { get; private set; }

    /// <summary>Raised when the server rejects a move (MOVEFAIL) and snaps us back to the carried coord — the bot walked into…</summary>
    public event Action<(uint X, uint Y)>? MoveFailed;

    /// <summary>Raised when a skill cast is rejected by the server (NC_BAT_SKILLBASH_CAST_FAIL_ACK)</summary>
    public event Action<ushort>? CastFailed;

    /// <summary>Raised when the server reports that OUR melee auto-attack stopped (NC_BAT_CEASE_FIRE_CMD with our own handle)</summary>
    public event Action<ushort>? BashCeased;

    public IReadOnlyCollection<NearbyPlayer> NearbyPlayers => _nearby.Values.ToArray();
    public int NearbyCount => _nearby.Count;

    /// <summary>NPCs/mobs currently in view (handle → id/coord), from the zone's MOB briefinfo</summary>
    public IReadOnlyCollection<NearbyNpc> NearbyNpcs
    {
        get
        {
            // Live in-view mobs PLUS recently-seen ones still within their sticky TTL (bridges the instance AoI-flicker so c…
            var clones = ScenarioClonesInView();
            if (_recentNpcs.IsEmpty && clones is null) return _npcs.Values.ToArray();
            long now = Environment.TickCount64;
            var result = new Dictionary<ushort, NearbyNpc>();
            foreach (var kv in _recentNpcs)
            {
                if (kv.Value.Expiry <= now) { _recentNpcs.TryRemove(kv.Key, out _); continue; }
                result[kv.Key] = kv.Value.Npc;
            }
            foreach (var kv in _npcs) result[kv.Key] = kv.Value; // live entries win over stale sticky ones
            // Scenario clones are PROJECTED from the player list on every read, never stored as a mob
            if (clones is not null)
                foreach (var c in clones) result[c.Handle] = c;
            return result.Values.ToArray();
        }
    }

    /// <summary>The scenario clones currently in view, built fresh from the PLAYER list so their position, name and level are…</summary>
    private List<NearbyNpc>? ScenarioClonesInView()
    {
        ushort[] handles;
        lock (_scenarioFightable)
        {
            if (_scenarioFightable.Count == 0) return null;
            handles = _scenarioFightable.ToArray();
        }
        List<NearbyNpc>? outp = null;
        foreach (var h in handles)
        {
            // Only project what we can actually locate
            if (!_nearby.TryGetValue(h, out var pl)) continue;
            (outp ??= new()).Add(new NearbyNpc(h, MobId: 0, Mode: pl.Mode, X: pl.X, Y: pl.Y,
                IsScenarioClone: true, CharName: pl.Name, CharLevel: pl.Level));
        }
        return outp;
    }

    /// <summary>A mob leaving our AoI (BRIEFINFODELETE / MAP_LOGOUT — NOT death) → move it to the sticky recently-seen cache i…</summary>
    private void StashRecentNpc(ushort hnd, NearbyNpc npc)
    {
        if (npc.Flag == 1) return;                              // a gate, not a combat target
        if (IsHuntableMob is { } huntable && !huntable(npc.MobId)) return; // a static/friendly NPC
        _recentNpcs[hnd] = (npc, Environment.TickCount64 + RecentNpcTtlMs);
        NoteAoiChurn(entered: false);   // was one LogV per mob — see the AoI summariser above
    }

    /// <summary>Live scenario corridor DOOR states (0x6C09), keyed by door handle</summary>
    public IReadOnlyCollection<DoorState> DoorStates => _doorStates.Values.ToArray();

    /// <summary>Live scenario door states keyed by .sbi block NAME ("Door04") → doorstate byte (0 closed / 1 open), from 0x1C0…</summary>
    public IReadOnlyDictionary<string, byte> DoorStatesByName => new Dictionary<string, byte>(_doorStateByName);

    /// <summary>Raised whenever a scenario door's state changes (BUILDDOOR seed or DOORSTATE update), carrying the full curren…</summary>
    public event Action<IReadOnlyDictionary<string, byte>>? DoorStatesByNameChanged;

    /// <summary>(x,y) of an NPC by mobId from the authoritative map-enter SEED (bulk 0x1C09 at infinite range) — the source of…</summary>
    public (uint X, uint Y)? NpcPosition(int mobId)
        => _npcSeed.TryGetValue(mobId, out var e) ? (e.X, e.Y) : null;

    /// <summary>The full seed entry for an NPC/gate by mobId (position + gate flag + link-destination map), or null if not on…</summary>
    public NpcSeedEntry? Npc(int mobId) => _npcSeed.TryGetValue(mobId, out var e) ? e : null;

    /// <summary>The full map-enter NPC seed roster (all NPCs+gates the server broadcast on map-enter)</summary>
    public IReadOnlyCollection<NpcSeedEntry> NpcSeed => _npcSeed.Values.ToArray();

    /// <summary>EVERY static entry, including several that share one mob id — unlike , which is keyed by mob id and therefore…</summary>
    public IReadOnlyCollection<NpcSeedEntry> NpcSeedAll => _npcSeedAll.Values.ToArray();
    /// <summary>Gate entries in the seed: linkMap -&gt; (x,y) — the LIVE current-map gate positions, better than the static MapLi…</summary>
    public IReadOnlyList<(string LinkMap, uint X, uint Y)> SeedGates()
        => _npcSeed.Values.Where(e => e.IsGate && !string.IsNullOrEmpty(e.LinkMap))
                          .Select(e => (e.LinkMap!, e.X, e.Y)).ToArray();
    /// <summary>Count of NPCs in the current map's seed roster (for logging/diagnostics)</summary>
    public int NpcSeedCount => _npcSeed.Count;
    public ChatMessage? LastChat { get; private set; }

    /// <summary>Handle of the most recently killed entity (from REALLYKILL) — lets a grind script confirm a kill landed and mo…</summary>
    public ushort LastKill { get; private set; }

    /// <summary>Count of mobs the bot itself killed (REALLYKILL with attacker == self)</summary>
    public int KillsByMe { get; private set; }

    /// <summary>True while the bot is riding a mount (tracked from MOVER ride on/off, 0xCC02/0xCC06)</summary>
    public bool IsMounted { get; private set; }

    /// <summary>The bot's current walk speed in world-units per second, as last reported by the server's MOVESPEED broadcast (…</summary>
    public double WalkSpeed { get; private set; } = 120.0;

    /// <summary>Raised when the server broadcasts a MOVESPEED (0xCC0D) for the bot itself — fires with the new walk speed in w…</summary>
    public event Action<double>? WalkSpeedChanged;

    /// <summary>The bot's current HP, as last reported by the server (HPCHANGE 0x240E)</summary>
    public uint? Hp { get; private set; }

    /// <summary>The bot's current SP (SPCHANGE 0x240F)</summary>
    public uint? Sp { get; private set; }

    /// <summary>The bot's maximum HP, from the [1802] login param block (seeded by the manager via )</summary>
    public uint MaxHp { get; private set; }

    /// <summary>The bot's maximum SP, from the [1802] login param block</summary>
    public uint MaxSp { get; private set; }

    /// <summary>Seed MaxHp/MaxSp from the zone-entry param block</summary>
    public void SeedMaxVitals(uint? maxHp, uint? maxSp)
    {
        if (maxHp is { } h && h > 0) MaxHp = h;
        if (maxSp is { } s && s > 0) MaxSp = s;
    }

    public uint HpStoneRestore { get; private set; }
    /// <summary>SP restored by one soul-stone charge (same packet)</summary>
    public uint SpStoneRestore { get; private set; }

    public uint MaxHpStones { get; private set; }
    public uint MaxSpStones { get; private set; }

    /// <summary>Unit price (cen) of one HP/SP soul-stone charge, as sent by the healer's soul-stone shop-open (0x3C05 SOULSTON…</summary>
    public uint HpStonePrice { get; private set; }
    public uint SpStonePrice { get; private set; }

    /// <summary>Current soul-stone reserve charges (HP/SP), as last reported by a BUY_ACK (0x5003/0x5004, totalnumber )</summary>
    public int? HpStones { get; private set; }
    public int? SpStones { get; private set; }

    /// <summary>True once an HP soul-stone USE failed (USEFAIL 0x5006) — the reserve is empty (or on cooldown), so further Use…</summary>
    public bool HpStoneDepleted { get; private set; }

    /// <summary>SP analogue of (USEFAIL attributed to an SP USE)</summary>
    public bool SpStoneDepleted { get; private set; }

    // USEFAIL (0x5006) carries NO hp/sp marker — but WE know which USE we fired
    private readonly Queue<(bool Hp, DateTime AtUtc)> _pendingStoneUse = new();

    /// <summary>Note an outbound soul-stone USE (0x5007 hp / 0x5009 sp) so its result packet can be attributed to the right po…</summary>
    public void NoteStoneUseFired(bool hp)
    {
        lock (_pendingStoneUse)
        {
            _pendingStoneUse.Enqueue((hp, DateTime.UtcNow));
            while (_pendingStoneUse.Count > 8) _pendingStoneUse.Dequeue(); // bound stale build-up
        }
    }

    /// <summary>Pop the pending-USE kind for an arriving USE result</summary>
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

    /// <summary>Monotonic count of soul-stone BUY failures (0x5005) + the last error code</summary>
    public int StoneBuyFailCount { get; private set; }
    public ushort LastStoneBuyFailErr { get; private set; }

    // Pick pacing (operator 2026-07-02): the server processes ONE item-cell pick at a time — the flow must be pick→a…
    public bool PickPending { get; private set; }
    public DateTime PickSentUtc { get; private set; } = DateTime.MinValue;
    public bool CanPick => !PickPending || (DateTime.UtcNow - PickSentUtc) > TimeSpan.FromSeconds(2);

    /// <summary>Called at the PICK_REQ send site (BotManager) — arms the pick-ack pacing gate</summary>
    public void MarkPickSent() { PickPending = true; PickSentUtc = DateTime.UtcNow; }

    public void SeedMaxStones(uint? maxHpStones, uint? maxSpStones)
    {
        if (maxHpStones is { } h && h > 0) MaxHpStones = h;
        if (maxSpStones is { } s && s > 0) MaxSpStones = s;
    }

    /// <summary>Seed the CURRENT soul-stone reserve counts from the zone-enter char-info (NC_CHAR_BASE, decoded in )</summary>
    public void SeedStones(int? hpStones, int? spStones)
    {
        if (hpStones is { } h && h >= 0) { HpStones = h; if (h > 0) HpStoneDepleted = false; }
        if (spStones is { } s && s >= 0) { SpStones = s; if (s > 0) SpStoneDepleted = false; }
        StonesChanged?.Invoke();
    }

    /// <summary>Raised when the bot's own HP changes (HPCHANGE 0x240E), with the new current HP</summary>
    public event Action<byte>? LevelChanged;

    /// <summary>Raised on a JOB CHANGE (NC_CHAR_PROMOTE_ACK) — carries the new class id</summary>
    public event Action<byte>? Promoted;
    /// <summary>The class id from the most recent NC_CHAR_PROMOTE_ACK this session, or null if we haven't seen a promotion (th…</summary>
    public byte? PromotedClass { get; private set; }

    /// <summary>The most recent scenario trigger-area we entered + acked</summary>
    public string? LastScenarioArea { get; private set; }
    /// <summary>Latches true once we're inside a scenario instance (any AREAENTRY seen) and stays true across between-room gap…</summary>
    public bool InScenarioInstance { get; private set; }
    // Count of REGENMOB (0x1C08) received — a monotonic "a wave just spawned" signal the AREAENTRY_ACK re-send loop…
    private long _scenarioRegenCount;
    /// <summary>Raised when we auto-ack a scenario AREAENTRY (carries the area name) — a new instance room armed</summary>
    public event Action<string>? ScenarioAreaEntered;
    // Scenario areas we've ARRIVED IN and ACKED (name → 1)
    private readonly ConcurrentDictionary<string, byte> _scenarioAckedAreas = new();
    /// <summary>Scenario areas we've arrived-in and acked this instance run (authoritative "area done" set)</summary>
    public IReadOnlyCollection<string> ScenarioAckedAreas => _scenarioAckedAreas.Keys.ToArray();
    /// <summary>(areaName,(x,y)) → is the point inside that scenario area's .aid box?</summary>
    public Func<string, (uint X, uint Y), bool>? IsInsideScenarioArea { get; set; }

    /// <summary>The character total after an EXP change (0x1073). Exp already drives the whole progress panel and
    /// was only ever visible through a poll, so a level of grinding showed up as a step every 2 seconds.</summary>
    /// <summary>The soul-stone reserve or its cooldown changed — a buy ack, a use, or the zone-enter seed. No
    /// payload: the reserve is four numbers plus two cooldowns, and a subscriber that needs them can read the live
    /// values rather than have a snapshot shape frozen into an event signature.</summary>
    public event Action? StonesChanged;
    public event Action<long>? ExpChanged;
    /// <summary>Money ("cen") after a CENCHANGE. Same reason: it is on the wire, it just never left ZoneView.</summary>
    public event Action<long>? MoneyChanged;
    public event Action<uint>? HpChanged;

    /// <summary>Raised when the bot's own SP changes (SPCHANGE 0x240F)</summary>
    public event Action<uint>? SpChanged;

    /// <summary>Raised for every combat hit broadcast in view (own swing + others')</summary>
    public event Action<HitInfo>? Damaged;

    private readonly ConcurrentDictionary<ushort, DateTime> _aggressors = new();      // confident: hit us / clearly running at us
    private readonly ConcurrentDictionary<ushort, DateTime> _maybeAggressors = new();  // running our way, but a player shares the angle
    private static readonly TimeSpan CombatWindow = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<ushort, (uint X, uint Y, bool Frozen, bool IdleConfirmed)> _mobAnchor = new();
    private readonly ConcurrentDictionary<int, double> _mobChase = new();

    /// <summary>The spawn anchor we've learned for a live mob handle, or null if never seen</summary>
    public (uint X, uint Y)? MobAnchor(ushort handle) =>
        _mobAnchor.TryGetValue(handle, out var a) ? (a.X, a.Y) : null;

    /// <summary>How far this mob currently is from its own spawn anchor, or null if unknown</summary>
    public double? AnchorDistance(ushort handle)
    {
        if (!_mobAnchor.TryGetValue(handle, out var a)) return null;
        foreach (var n in NearbyNpcs)
            if (n.Handle == handle)
                return Math.Sqrt(Math.Pow((double)n.X - a.X, 2) + Math.Pow((double)n.Y - a.Y, 2));
        return null;
    }

    public double MobChaseLimit(int mobId) => _mobChase.TryGetValue(mobId, out var d) ? d : 0;

    /// <summary>Seed/refresh a mob's spawn anchor</summary>
    private void NoteMobAnchor(ushort handle, uint x, uint y, bool idle)
    {
        _mobAnchor.AddOrUpdate(handle, (x, y, false, idle && !_aggressors.ContainsKey(handle)),
            (_, old) => old.Frozen || !idle ? old : (x, y, false, true));
    }

    /// <summary>Freeze a mob's anchor (it just started chasing us) and fold its current distance-from-home into the learned ch…</summary>
    private void FreezeMobAnchor(ushort handle)
    {
        if (_mobAnchor.TryGetValue(handle, out var a) && !a.Frozen)
            _mobAnchor[handle] = (a.X, a.Y, true, a.IdleConfirmed);
        if (!a.IdleConfirmed) return;            // never saw it at rest → its "spawn" is a guess; don't learn from it
        if (AnchorDistance(handle) is not { } d) return;
        foreach (var n in NearbyNpcs)
            if (n.Handle == handle)
            {
                _mobChase.AddOrUpdate(n.MobId, d, (_, old) => d > old ? d : old);
                return;
            }
    }

    /// <summary>Mobs we're confident are aggroing us within the combat window — hit us (incoming SWING_DAMAGE, defender==self)…</summary>
    public IReadOnlyCollection<ushort> Aggressors =>
        _aggressors.Where(kv => DateTime.UtcNow - kv.Value < CombatWindow).Select(kv => kv.Key).ToArray();

    /// <summary>Mobs running roughly toward us but where a nearby player shares the heading, so the target is uncertain — "may…</summary>
    public IReadOnlyCollection<ushort> MaybeAggressors =>
        _maybeAggressors.Where(kv => DateTime.UtcNow - kv.Value < CombatWindow).Select(kv => kv.Key).ToArray();

    /// <summary>True if the bot has been hit in the last few seconds</summary>
    public bool InCombat => DateTime.UtcNow - LastHitAtUtc < CombatWindow;

    /// <summary>When the bot was last hit (UtcMinValue if never)</summary>
    public DateTime LastHitAtUtc { get; private set; } = DateTime.MinValue;

    public DateTime CastBarStartedAtUtc { get; private set; } = DateTime.MinValue;

    public bool CastBarActive => CastBarStartedAtUtc > DateTime.MinValue
                                 && DateTime.UtcNow - CastBarStartedAtUtc < CastBarMaxWait;

    private static readonly TimeSpan CastBarMaxWait = TimeSpan.FromSeconds(4.5);

    /// <summary>Clear the in-flight cast bar (the cast finished or was cancelled)</summary>
    private void ClearCastBar() => CastBarStartedAtUtc = DateTime.MinValue;

    private readonly ConcurrentDictionary<int, (int Max, int Count, long Sum)> _mobHits = new();
    // mobId -> (highest, second-highest) observed distance at which it damaged us
    private readonly ConcurrentDictionary<int, (double, double)> _mobRange = new();
    // Same, keyed by ENTITY HANDLE, for attackers that have no usable mob id (a scenario clone reads 0)
    private readonly ConcurrentDictionary<ushort, (double, double)> _handleRange = new();
    // AND THE HITS PER HANDLE TOO
    private readonly ConcurrentDictionary<ushort, (int Max, int Count, long Sum)> _handleHits = new();

    /// <summary>Observed attack range of one ENTITY (world units), or -1 if it has never hit us</summary>
    public double HandleAttackRange(ushort handle) =>
        _handleRange.TryGetValue(handle, out var r) && r.Item2 > 0 ? r.Item2 : -1;

    /// <summary>The observed ATTACK RANGE of in world units, or -1 if it has never hit us</summary>
    public double MobAttackRange(int mobId) => _mobRange.TryGetValue(mobId, out var r) && r.Item2 > 0 ? r.Item2 : -1;

    /// <summary>Hardest hit ever taken from ONE ENTITY, or -1 if it has never hit us</summary>
    public int HandleHitMax(ushort handle) => _handleHits.TryGetValue(handle, out var s) ? s.Max : -1;

    /// <summary>Mean damage per connecting hit from ONE ENTITY, or -1 if it has never hit us</summary>
    public double HandleHitAvg(ushort handle) => _handleHits.TryGetValue(handle, out var s) && s.Count > 0
        ? (double)s.Sum / s.Count : -1;

    /// <summary>How many hits from this ENTITY we have sampled</summary>
    public int HandleHitSamples(ushort handle) => _handleHits.TryGetValue(handle, out var s) ? s.Count : 0;

    /// <summary>Hardest hit ever taken from , or -1 if we have never been hit by it</summary>
    public int MobHitMax(int mobId) => _mobHits.TryGetValue(mobId, out var s) ? s.Max : -1;

    /// <summary>Mean damage per connecting hit from , or -1 if never observed</summary>
    public double MobHitAvg(int mobId) => _mobHits.TryGetValue(mobId, out var s) && s.Count > 0
        ? (double)s.Sum / s.Count : -1;

    /// <summary>How many hits from we have sampled (0 = no evidence; treat an unknown mob as unknown, NOT as safe — see [[fies…</summary>
    public int MobHitSamples(int mobId) => _mobHits.TryGetValue(mobId, out var s) ? s.Count : 0;

    /// <summary>Metrics sink: (name, value)</summary>
    public Action<string, double>? MetricSink;
    private DateTime? _mountedSinceUtc;

    /// <summary>Raised for every observed incoming hit so a DURABLE store can retain it across sessions (this in-memory table…</summary>
    public Action<int, int>? MobHitSampled;

    public Action<string, double>? ScalarLearned;

    /// <summary>Names of the persisted scalars (shared between the seeder and the learner)</summary>
    public const string ScalarStoneCooldownMs = "hpStoneCooldownMs";
    public const string ScalarStoneHeal = "hpStoneHeal";
    /// <summary>Durable name for — see SeedMeleeRange for why it must persist</summary>
    public const string ScalarMeleeRange = "meleeRange";

    /// <summary>Seed learned scalars from durable knowledge so the survivability inequality is answerable from the FIRST tick…</summary>
    public void SeedScalars(double cooldownMsMin, double healAvg, int healCount, double healMax = -1)
    {
        // The persisted scalar is a min SUCCESS gap — a ceiling, not the cooldown (see HpStoneCooldownMs)
        if (cooldownMsMin > 0) _cdMinSuccessGapMs = cooldownMsMin;
        if (healAvg > 0 && healCount > 0) { _healSum = (long)(healAvg * healCount); _healSamples = healCount; }
        // Seed the MAX as well — SustainableHealDps keys off it, so restoring only the mean would leave the inequality u…
        if (healMax > 0 && healMax > HpStoneHealMax) HpStoneHealMax = (int)healMax;
        if (cooldownMsMin > 0 || healAvg > 0)
            _logLevel?.Invoke(BotLogLevel.Note,
                $"[heal] seeded from durable knowledge — stone cooldown {(cooldownMsMin > 0 ? $"{cooldownMsMin:F0}ms" : "unknown")}, " +
                $"heal avg {(healAvg > 0 ? $"{healAvg:F0}" : "unknown")} ⇒ sustainable {(SustainableHealDps > 0 ? $"{SustainableHealDps:F0} HP/s" : "unknown")} " +
                "(no need to heal twice before we can judge a fight)");
    }

    public void SeedMeleeRange(double maxObserved)
    {
        if (maxObserved <= 0 || maxObserved > 150) return;
        if (maxObserved <= _learnedMeleeRange) return;
        _learnedMeleeRange = maxObserved;
        _rangeMax1 = Math.Max(_rangeMax1, maxObserved);
        _rangeMax2 = Math.Max(_rangeMax2, maxObserved);   // the seed is already a trusted 2nd-highest
        _logLevel?.Invoke(BotLogLevel.Note,
            $"[combat] seeded attack-range {maxObserved:F0}u from durable knowledge — no re-learning from 0 after this handoff");
    }

    /// <summary>Seed the threat table from durable knowledge at zone-enter, so a mob learned in an earlier session is dangerou…</summary>
    public void SeedMobHits(IEnumerable<(int MobId, int Max, int Count, long Sum)> seeds)
    {
        var n = 0;
        foreach (var (mobId, max, count, sum) in seeds)
        {
            if (mobId <= 0 || max <= 0) continue;
            _mobHits[mobId] = (max, count, sum);
            n++;
        }
        if (n > 0)
            _logLevel?.Invoke(BotLogLevel.Note, $"[threat] seeded {n} mob(s) from durable knowledge — " +
                "previously-learned threats apply immediately, no need to be hit again to re-learn them");
    }

    /// <summary>Every mob we have damage evidence for: mobId → (max, samples, avg)</summary>
    public IReadOnlyDictionary<int, (int Max, int Count, double Avg)> LearnedMobHits =>
        _mobHits.ToDictionary(kv => kv.Key,
            kv => (kv.Value.Max, kv.Value.Count, kv.Value.Count > 0 ? (double)kv.Value.Sum / kv.Value.Count : 0d));

    private int _hpAtStoneUse = -1;
    private DateTime _stoneHealPendingUntil = DateTime.MinValue;

    // Rolling window of incoming hits (utc, damage) so the driver can ask what the PACK is actually doing to us righ…
    private readonly ConcurrentQueue<(DateTime At, int Dmg)> _recentIncoming = new();

    /// <summary>Observed incoming damage per second over the trailing</summary>
    public double IncomingDamageSince(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        while (_recentIncoming.TryPeek(out var head) && head.At < cutoff) _recentIncoming.TryDequeue(out _);
        var total = 0L;
        foreach (var (at, dmg) in _recentIncoming) if (at >= cutoff) total += dmg;
        var secs = window.TotalSeconds;
        return secs > 0 ? total / secs : 0;
    }

    public int HpStoneHealMax { get; private set; } = -1;

    /// <summary>Largest UNCENSORED heal — one that stopped short of full HP, so nothing clipped it and it is the exact charge</summary>
    public int HpStoneChargeMeasured { get; private set; } = -1;

    public double HpStoneChargePerUse =>
        HpStoneRestore > 0 ? HpStoneRestore
        : HpStoneChargeMeasured > 0 ? HpStoneChargeMeasured
        : HpStoneHealMax;

    public double HpStoneHealAvg => _healSamples > 0 ? (double)_healSum / _healSamples : -1;
    private int _healSamples;
    private long _healSum;

    public double SustainableHealDps
    {
        get
        {
            var perCharge = HpStoneChargePerUse;
            return perCharge > 0 && HpStoneCooldownMs > 0 ? perCharge / (HpStoneCooldownMs / 1000.0) : -1;
        }
    }

    /// <summary>When the HP soul stone last ACTUALLY healed (0x5008), UtcMinValue if never</summary>
    public DateTime LastHpStoneSuccessUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Smallest gap between two SUCCESSFUL stone uses, -1 if fewer than two seen</summary>
    private double _cdMinSuccessGapMs = -1;

    /// <summary>Largest gap-since-success at which a USE still FAILED while charges remained and HP was below max, -1 if never…</summary>
    private double _cdMaxFailGapMs = -1;

    public double HpStoneCooldownMs => HpStoneCooldownDefaultMs;

    /// <summary>True when this character's own wire evidence contradicts the hardcoded cooldown: a USEFAIL proved the stone wa…</summary>
    public bool StoneCooldownDisagrees => _cdMaxFailGapMs > HpStoneCooldownDefaultMs * 1.25;

    public const double HpStoneCooldownDefaultMs = 7000;

    /// <summary>The best LOWER BOUND on sustain we can state without a corroborated cooldown: one charge over the shortest gap…</summary>
    public double HealDpsLowerBound
    {
        get
        {
            double perCharge = HpStoneRestore > 0 ? HpStoneRestore : HpStoneHealMax;
            return perCharge > 0 && _cdMinSuccessGapMs > 0 ? perCharge / (_cdMinSuccessGapMs / 1000.0) : -1;
        }
    }

    /// <summary>Consecutive HP stone USEFAILs since the last success</summary>
    public int HpStoneFailsSinceSuccess { get; private set; }

    /// <summary>Milliseconds until the HP stone is likely usable again (0 = ready now, -1 = cooldown not learned yet)</summary>
    public double HpStoneReadyInMs => HpStoneCooldownMs < 0 || LastHpStoneSuccessUtc == DateTime.MinValue
        ? -1
        : Math.Max(0, HpStoneCooldownMs - (DateTime.UtcNow - LastHpStoneSuccessUtc).TotalMilliseconds);

    /// <summary>When the SP soul stone last succeeded (0x500A), UtcMinValue if never</summary>
    public DateTime LastSpStoneSuccessUtc { get; private set; } = DateTime.MinValue;

    public double SpStoneCooldownMs => HpStoneCooldownMs;

    /// <summary>Milliseconds until the SP stone is usable again (0 = ready now, -1 = never used yet)</summary>
    public double SpStoneReadyInMs => SpStoneCooldownMs < 0 || LastSpStoneSuccessUtc == DateTime.MinValue
        ? -1
        : Math.Max(0, SpStoneCooldownMs - (DateTime.UtcNow - LastSpStoneSuccessUtc).TotalMilliseconds);

    /// <summary>Result code from the most recent NC_ITEM_RELOC_ACK (0x300C), -1 if none seen</summary>
    public int LastRelocAckCode { get; private set; } = -1;

    /// <summary>When was set (UtcMinValue if never) — lets a caller tell a fresh ack from a stale one when a move times out</summary>
    public DateTime LastRelocAckAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>When the bot last LANDED a hit on something (Attacker==self in a SWING_DAMAGE/ SOMEONESWING_DAMAGE broadcast)…</summary>
    public DateTime LastDamageDealtAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>When the bot last landed a CONNECTING hit (Attacker==self AND Damage&amp;gt;0) — distinct from which fires on any…</summary>
    public DateTime LastRealDamageDealtAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Whether our melee auto-attack (BASHSTART) is believed to be running</summary>
    public bool BashActive { get; set; }

    /// <summary>When the server last told us our auto-attack ceased (CEASE_FIRE on our handle)</summary>
    public DateTime LastBashCeasedAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>How many times the server has ceased our fire this session — a direct measure of how often our own STOP/cast c…</summary>
    public int BashCeasedCount { get; private set; }

    /// <summary>Whether a skill cast is currently in flight / animating</summary>
    public bool CastInFlight { get; private set; }

    /// <summary>Speculative deadline for the in-flight cast (local prediction from ActiveSkill.CastTime plus round-trip margin…</summary>
    public DateTime CastPredictedUntilUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Server-confirmed cast state: true once CAST_SUC_ACK arrived for the in-flight cast</summary>
    public bool CastServerConfirmed { get; private set; }

    /// <summary>True while a cast is genuinely believed to be animating — server state first, with the speculative window as t…</summary>
    public bool IsCasting => CastInFlight && DateTime.UtcNow < CastPredictedUntilUtc;

    /// <summary>Called when WE send a cast: begin the speculative window immediately (don't wait for the server — that round t…</summary>
    public void NoteCastSent(int predictedCastMs)
    {
        CastInFlight = true;
        CastServerConfirmed = false;
        // Predicted animation + a round-trip margin
        CastPredictedUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(predictedCastMs, 0) + 900);
    }

    private void EndCast(string why)
    {
        if (!CastInFlight) return;
        CastInFlight = false;
        CastServerConfirmed = false;
        CastPredictedUntilUtc = DateTime.MinValue;
        _logLevel?.Invoke(BotLogLevel.Verbose, $"[combat] cast END ({why})");
    }

    /// <summary>LEARNED effective attack range (operator 2026-07-15): the max distance at which our OWN swing has CONNECTED (S…</summary>
    public double LearnedMeleeRange => MeleeRangeExperimentU > 0 ? MeleeRangeExperimentU : _learnedMeleeRange;
    private double _learnedMeleeRange;

    public const double MeleeRangeExperimentU = 0;

    // Top TWO connect distances ever seen
    private double _rangeMax1, _rangeMax2;

    /// <summary>True while the bot is dead (DEADMENU opened, not yet revived)</summary>
    public bool Dead { get; private set; }

    /// <summary>When the bot died (DEADMENU), for the ~2-min auto-respawn timeout / "wait for a cleric" window</summary>
    public DateTime DeadAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Last known CURRENT hp of an entity, by handle, harvested from the RestHp every damage packet already carries</summary>
    private readonly ConcurrentDictionary<ushort, uint> _entityHp = new();
    private readonly ConcurrentDictionary<ushort, uint> _entityMaxHp = new();
    /// <summary>Max HP the SERVER stated for a target in TARGETINFO — an exact figure, not MobInfo's table value, and the only…</summary>
    public uint? EntityMaxHp(ushort handle) => _entityMaxHp.TryGetValue(handle, out var v) ? v : null;

    /// <summary>The handle the server last CONFIRMED as our target (NC_BAT_TARGETINFO_CMD), and when</summary>
    public ushort TargetConfirmedHandle { get; private set; }
    public DateTime TargetConfirmedAtUtc { get; private set; } = DateTime.MinValue;
    public uint? EntityHp(ushort handle) => _entityHp.TryGetValue(handle, out var v) ? v : null;

    // LIVE ENTITY CHANGE FEED (operator 2026-08-13) ──────────────────────────────────────────────── "Each entity is…
    private readonly ConcurrentDictionary<ushort, (uint FromX, uint FromY, uint ToX, uint ToY, double Speed, DateTime AtUtc)> _entityMove = new();

    /// <summary>The in-flight move for , or null if it has never been seen to move</summary>
    public (uint FromX, uint FromY, uint ToX, uint ToY, double Speed, DateTime AtUtc)? EntityMove(ushort handle)
        => _entityMove.TryGetValue(handle, out var m) ? m : null;

    /// <summary>One tracked entity's state changed (appeared, moved, took damage)</summary>
    public event Action<ushort>? EntityChanged;
    /// <summary>One tracked entity left view or died</summary>
    public event Action<ushort>? EntityGone;

    /// <summary>Announce a change. Never throws — a bad subscriber cannot kill the read loop</summary>
    internal void NoteEntityChanged(ushort handle)
    {
        try { EntityChanged?.Invoke(handle); } catch { /* subscriber threw */ }
    }

    internal void NoteEntityGone(ushort handle)
    {
        _entityMove.TryRemove(handle, out _);   // it is not walking anywhere; don't retain it per-handle
        try { EntityGone?.Invoke(handle); } catch { /* subscriber threw */ }
    }

    private void NoteHit(HitInfo h)
    {
        // Whoever took the hit just told us its remaining hp — attacker or defender, us or them
        _entityHp[h.Defender] = h.RestHp;
        NoteEntityChanged(h.Defender);       // its health bar just moved — push it, don't wait for a poll
        if (SelfHandle is { } self && h.Defender == self)
        {
            // Combat-START marker for the tail: a hit arriving after a CombatWindow gap is a fresh engagement
            if (DateTime.UtcNow - LastHitAtUtc > CombatWindow)
                _log?.Invoke($"[combat] START vs mob h={h.Attacker}");
            _aggressors[h.Attacker] = DateTime.UtcNow;
            FreezeMobAnchor(h.Attacker);   // it's on us now → its anchor stops moving; measure the chase from home
            LastHitAtUtc = DateTime.UtcNow;
            // DAMAGE-TAKEN SAMPLE for the survivability model (operator 2026-07-29): every incoming hit, labeled by the atta…
            if (h.Damage > 0)
            {
                int atkMob = _npcs.TryGetValue(h.Attacker, out var an) ? an.MobId
                           : _recentNpcs.TryGetValue(h.Attacker, out var ar) ? ar.Npc.MobId : 0;
                // LEARN THE ENEMY'S ATTACK RANGE from the distance it hit us at
                if (SelfPositionProvider?.Invoke() is { } msp)
                {
                    double axp = double.NaN, ayp = double.NaN;
                    if (_npcs.TryGetValue(h.Attacker, out var apn)) { axp = apn.X; ayp = apn.Y; }
                    else if (_nearby.TryGetValue(h.Attacker, out var app)) { axp = app.X; ayp = app.Y; }
                    if (!double.IsNaN(axp))
                    {
                        var mdx = (double)msp.X - axp; var mdy = (double)msp.Y - ayp;
                        var mdist = Math.Sqrt(mdx * mdx + mdy * mdy);
                        if (mdist > 0 && mdist < 2000)
                        {
                            if (atkMob > 0)
                            {
                                var cur = _mobRange.TryGetValue(atkMob, out var mr) ? mr : (0.0, 0.0);
                                if (mdist > cur.Item1) cur = (mdist, cur.Item1);
                                else if (mdist > cur.Item2) cur = (cur.Item1, mdist);
                                _mobRange[atkMob] = cur;
                            }
                            var hcur = _handleRange.TryGetValue(h.Attacker, out var hr) ? hr : (0.0, 0.0);
                            if (mdist > hcur.Item1) hcur = (mdist, hcur.Item1);
                            else if (mdist > hcur.Item2) hcur = (hcur.Item1, mdist);
                            _handleRange[h.Attacker] = hcur;
                        }
                    }
                }
                _logLevel?.Invoke(BotLogLevel.Info, $"[dmgtaken] mob={atkMob} dmg={h.Damage} resthp={h.RestHp} h={h.Attacker}");
                _recentIncoming.Enqueue((DateTime.UtcNow, h.Damage));   // feed the live incoming-DPS window
                MetricSink?.Invoke("damageTaken", h.Damage);
                while (_recentIncoming.Count > 512) _recentIncoming.TryDequeue(out _);
                // Per HANDLE first, and OUTSIDE the atkMob guard — see _handleHits
                var hPrevMax = _handleHits.TryGetValue(h.Attacker, out var hOld) ? hOld.Max : -1;
                var hUpd = _handleHits.AddOrUpdate(h.Attacker,
                    _ => (h.Damage, 1, h.Damage),
                    (_, s) => (Math.Max(s.Max, h.Damage), s.Count + 1, s.Sum + h.Damage));
                // Announce a new worst-case for a MobId-less attacker; when it HAS a mob id the [threat] line below says the sam…
                if (atkMob <= 0 && hUpd.Max > hPrevMax && MaxHp > 0)
                {
                    _logLevel?.Invoke(BotLogLevel.Note,
                        $"[threat] entity h={h.Attacker} (no mob id) hits for up to {hUpd.Max} " +
                        $"(avg {(double)hUpd.Sum / hUpd.Count:F0} over {hUpd.Count}) — that is " +
                        $"{(int)Math.Ceiling((double)MaxHp / Math.Max(1, hUpd.Max))} hit(s) to kill us at {MaxHp} maxHp");
                }
                // RETAIN the sample (mobId 0 = unresolved attacker, worthless as a key — skip it)
                if (atkMob > 0)
                {
                    var prevMax = _mobHits.TryGetValue(atkMob, out var old) ? old.Max : -1;
                    var upd = _mobHits.AddOrUpdate(atkMob,
                        _ => (h.Damage, 1, h.Damage),
                        (_, s) => (Math.Max(s.Max, h.Damage), s.Count + 1, s.Sum + h.Damage));
                    MobHitSampled?.Invoke(atkMob, h.Damage);   // persist it — this table dies with the session
                    // Announce a new worst-case only — the headline a human needs is "this thing can take N of my HP in one hit", no…
                    if (upd.Max > prevMax && MaxHp > 0)
                    {
                        var hitsToKill = (int)Math.Ceiling((double)MaxHp / Math.Max(1, upd.Max));
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[threat] mob{atkMob} hits for up to {upd.Max} (avg {(double)upd.Sum / upd.Count:F0} " +
                            $"over {upd.Count}) — that is {hitsToKill} hit(s) to kill us at {MaxHp} maxHp");
                    }
                }
            }
        }
        if (SelfHandle is { } me && h.Attacker == me)
        {
            LastDamageDealtAtUtc = DateTime.UtcNow;
            // A CONNECTING hit (Damage>0) vs a whiff/out-of-range (Damage==0)
            {
                int defMob = _npcs.TryGetValue(h.Defender, out var dnm) ? dnm.MobId
                           : _recentNpcs.TryGetValue(h.Defender, out var drm) ? drm.Npc.MobId : 0;
                _logLevel?.Invoke(BotLogLevel.Info,
                    $"[dmgdealt] mob={defMob} dmg={h.Damage} resthp={h.RestHp} h={h.Defender}" +
                    (h.Damage > 0 ? "" : " — WHIFF (no connect)"));
                if (h.Damage > 0) MetricSink?.Invoke("damageDealt", h.Damage);
            }
            if (h.Damage > 0)
            {
                LastRealDamageDealtAtUtc = DateTime.UtcNow;
                // LEARN THE ATTACK RANGE from the wire (operator 2026-07-15): the distance at which our swing CONNECTS is the ef…
                if (SelfPositionProvider?.Invoke() is { } sp)
                {
                    double dx = double.NaN, dy = double.NaN;
                    if (_npcs.TryGetValue(h.Defender, out var dn)) { dx = dn.X; dy = dn.Y; }
                    else if (_nearby.TryGetValue(h.Defender, out var dp)) { dx = dp.X; dy = dp.Y; }
                    if (!double.IsNaN(dx))
                    {
                        var ddx = (double)sp.X - dx; var ddy = (double)sp.Y - dy;
                        var dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                        // CORROBORATE BEFORE RAISING
                        if (dist > 0 && dist < 150)
                        {
                            if (dist > _rangeMax1) { _rangeMax2 = _rangeMax1; _rangeMax1 = dist; }
                            else if (dist > _rangeMax2) { _rangeMax2 = dist; }
                            if (_rangeMax2 > LearnedMeleeRange + 0.5)
                            {
                                _learnedMeleeRange = _rangeMax2;
                                _log?.Invoke($"[combat] LEARNED attack-range ↑ {LearnedMeleeRange:F0}u (2nd-highest of " +
                                             $"connects; top={_rangeMax1:F0}u ignored as a possible outlier, h={h.Defender})");
                                ScalarLearned?.Invoke(ScalarMeleeRange, LearnedMeleeRange);
                            }
                        }
                    }
                }
            }
        }
        Damaged?.Invoke(h);
    }

    /// <summary>When the server last opened a menu prompt (0x3C01)</summary>
    public DateTime? LastMenuAtUtc { get; private set; }

    /// <summary>Whether a server menu prompt (0x3C01) is currently open and unanswered</summary>
    public bool ServerMenuOpen { get; private set; }

    /// <summary>Mark the open server menu as answered (called after sending the ack)</summary>
    public void ClearServerMenu() { ServerMenuOpen = false; ServerMenuTitle = null; ServerMenuOptions = Array.Empty<ServerMenuOption>(); }

    /// <summary>The prompt text of the currently-open server menu (0x3C01)</summary>
    public string? ServerMenuTitle { get; private set; }

    /// <summary>The options of the open server menu (0x3C01), each = the reply byte to send in SERVERMENU_ACK (0x3C02) to SELE…</summary>
    public IReadOnlyList<ServerMenuOption> ServerMenuOptions { get; private set; } = Array.Empty<ServerMenuOption>();

    /// <summary>The reply byte for the FIRST option whose text matches any of (case-insensitive substring), or null if none ma…</summary>
    public byte? ServerMenuReplyFor(params string[] wants)
    {
        foreach (var o in ServerMenuOptions)
            foreach (var w in wants)
                if (!string.IsNullOrEmpty(o.Text) && o.Text.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return o.Reply;
        return null;
    }

    private volatile ushort[] _shopItems = Array.Empty<ushort>();

    /// <summary>The item ids the last-opened merchant sells (from SHOPOPEN)</summary>
    public IReadOnlyList<ushort> ShopItems => _shopItems;

    /// <summary>The npc handle of the last-opened shop (0 if none)</summary>
    public ushort ShopNpc { get; private set; }

    /// <summary>UTC of the last shop-open packet (item 0x3C0x OR soul-stone 0x3C05)</summary>
    public DateTime ShopOpenUtc { get; private set; }
    /// <summary>True if a shop opened recently (within ~10s) and we haven't left the map / been rejected since</summary>
    public bool ShopOpen => (DateTime.UtcNow - ShopOpenUtc) < TimeSpan.FromSeconds(10);

    /// <summary>UTC of the last NC_MENU_RANDOMOPTION_CMD (0x3C0E) — a NON-shop NPC menu</summary>
    public DateTime RandomOptionUtc { get; private set; }

    /// <summary>The KIND of the last shop that opened, derived from the shop-open opcode (skill master / smith / item merchant…</summary>
    public ShopKind LastShopKind { get; private set; } = ShopKind.Unknown;

    /// <summary>Unspent stat points (NC_CHAR_STAT_REMAINPOINT_CMD 0x105B)</summary>
    public int FreeStatPoints { get; private set; } = -1;

    /// <summary>Character combat/defence stats from the zone-entry CHAR_PARAMETER_DATA block, or null if that block never arri…</summary>
    public Zone.CharStats? Stats { get; private set; }
    public void SeedStats(Zone.CharStats? stats) { if (stats is not null) Stats = stats; }

    /// <summary>Reset the shop/menu-open signals to "nothing opened" — called BEFORE each open attempt so the result reflects…</summary>
    public void ResetShopState()
    {
        ShopOpenUtc = DateTime.MinValue;
        RandomOptionUtc = DateTime.MinValue;
        LastShopKind = ShopKind.Unknown;
    }

    /// <summary>True while an NPC menu prompt is open and unanswered (server sent NPCMENUOPEN_REQ after we clicked a merchant/…</summary>
    public bool NpcMenuOpen { get; private set; }

    /// <summary>The NPC mobId the last 0x201C menu belongs to (its payload = the NPC mobId)</summary>
    public ushort MenuNpcId { get; private set; }

    /// <summary>Mark the NPC menu answered (after sending NPCMENUOPEN_ACK / SELECT_START_REQ)</summary>
    public void ClearNpcMenu() { NpcMenuOpen = false; MenuNpcId = 0; }

    /// <summary>Raised when a merchant's shop opens, with the sell-list item ids</summary>
    public event Action<IReadOnlyList<ushort>>? ShopOpened;

    public long Money { get; private set; } = -1;

    /// <summary>Seed money from the zone-enter char-info (NC_CHAR_BASE Cen)</summary>
    public void SeedMoney(long cen) => Money = cen;

    /// <summary>Current total experience</summary>
    public long Exp { get; private set; } = -1;
    /// <summary>Experience gained since this zone session started (Σ of EXPGAIN credits) — progress rate</summary>
    public long SessionExpGained { get; private set; }
    /// <summary>Experience LOST to deaths this zone session (Σ of EXPLOST penalties) — so the "phantom relog exp loss" is now…</summary>
    public long SessionExpLost { get; private set; }
    /// <summary>Seed the absolute exp from the zone-enter char-info (NC_CHAR_BASE Experience)</summary>
    public void SeedExp(long exp)
    {
        // Apply anything that accrued BEFORE the seed arrived
        Exp = exp + _expPendingDelta;
        _expPendingDelta = 0;
    }

    /// <summary>Exp gained/lost while was still unseeded, held until a seed or an authoritative absolute arrives to reconcile…</summary>
    private long _expPendingDelta;

    /// <summary>The raw 2-byte code from the last NC_ITEM_SELL_ACK (0x3005), or -1 if none yet</summary>
    public int LastSellAck { get; private set; } = -1;
    /// <summary>UTC time of the last SELL_ACK — lets the driver wait for the result of a sell</summary>
    public DateTime LastSellAckUtc { get; private set; }
    /// <summary>The raw 2-byte code from the last NC_ITEM_BUY_ACK (0x3004), or -1 if none yet</summary>
    public int LastBuyAck { get; private set; } = -1;
    /// <summary>UTC time of the last BUY_ACK — lets the driver wait for / pace on a buy result</summary>
    public DateTime LastBuyAckUtc { get; private set; }
    /// <summary>Monotonic count of BUY_ACKs (0x3004) seen this session</summary>
    public int BuyAckCount { get; private set; }

    /// <summary>Error code of the last NC_ITEM_USE_ACK (0x700 ok, 0x708 skill-level-too-low, 0x70B already-know-the-skill)</summary>
    public int LastUseAckError { get; private set; } = -1;
    /// <summary>Item id from the last NC_ITEM_USE_ACK (which item the use result is for)</summary>
    public int LastUseAckItem { get; private set; } = -1;

    private readonly ConcurrentDictionary<int, int> _useFails = new();

    /// <summary>How many times IN A ROW the server has REFUSED to use this item id (any non-0x700 NC_ITEM_USE_ACK)</summary>
    public int ItemUseFailCount(int itemId) => _useFails.TryGetValue(itemId, out var n) ? n : 0;

    /// <summary>Current bag contents: slot → itemId (built from the login item list and live cell/equip changes)</summary>
    public IReadOnlyDictionary<byte, ushort> Inventory => _inventory;

    /// <summary>The stack count in main-bag (from the wire lot field), or 0 if the slot is empty</summary>
    public int ItemCount(byte slot) => _invCount.TryGetValue(slot, out var c) ? c : 0;

    /// <summary>Currently worn gear: equip slot → itemId (from equip-change events)</summary>
    public IReadOnlyDictionary<byte, ushort> Equipment => _equipment;

    /// <summary>Items currently lying on the ground in view (handle → drop), from DROPEDITEM broadcasts; retired when MAP_LOGO…</summary>
    public IReadOnlyCollection<GroundItem> Drops => _drops.Values.ToArray();

    /// <summary>The ground drop nearest to ( , ), or null if nothing is on the ground</summary>
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

    /// <summary>Result of the bot's last pickup attempt (PICK_ACK), or null if none yet</summary>
    public PickResult? LastPickResult { get; private set; }

    /// <summary>PICK_ACK error code meaning "inventory full" — captured live 2026-06-26 when IkFresh ran with a completely ful…</summary>
    public const ushort PickInventoryFull = 0x346;

    /// <summary>PICK_ACK error code meaning the pick SUCCEEDED (the bag gained the item via the accompanying CELLCHANGE) — con…</summary>
    public const ushort PickSuccess = 0x341;

    /// <summary>True when the bag is FULL — set when a pickup fails with , cleared on a successful SELL (room freed) or a succ…</summary>
    public bool BagFull { get; private set; }

    /// <summary>Raised when a new item appears on the ground (DROPEDITEM)</summary>
    public event Action<GroundItem>? DropAppeared;

    /// <summary>Raised when a tracked ground item leaves view (MAP_LOGOUT — picked by anyone, or despawned), with its handle</summary>
    public event Action<ushort>? DropRemoved;

    /// <summary>Raised on the result of the bot's own pickup attempt (PICK_ACK)</summary>
    public event Action<PickResult>? PickedUp;

    /// <summary>Skill ids the character has actually learned, from the zone-login skill list (NC_CHAR_CLIENT_SKILL_CMD)</summary>
    public IReadOnlyCollection<ushort> LearnedSkills => _skills.Keys.ToArray();

    // PER-SKILL LAST-CAST, so the watch panel can show real cooldowns
    private readonly ConcurrentDictionary<ushort, DateTime> _lastSkillCast = new();

    /// <summary>Record that this skill was just cast (called at the send site)</summary>
    public void NoteSkillCast(ushort skillId) => _lastSkillCast[skillId] = DateTime.UtcNow;

    private readonly ConcurrentDictionary<ushort, DateTime> _skillStartedAt = new();

    public void NoteSkillStarted(ushort skillId) => _skillStartedAt[skillId] = DateTime.UtcNow;

    /// <summary>When the server last confirmed this skill STARTED, or null if never</summary>
    public DateTime? SkillStartedAtUtc(ushort skillId) =>
        _skillStartedAt.TryGetValue(skillId, out var t) ? t : null;

    private readonly ConcurrentDictionary<ushort, ushort> _castIndexSkill = new();
    private readonly ConcurrentQueue<ushort> _castIndexOrder = new();
    private readonly ConcurrentDictionary<ushort, (int Count, long Sum, int Max)> _skillDamage = new();

    /// <summary>Mean FINAL damage this skill has actually landed, or -1 with no samples</summary>
    public double SkillDamageAvg(ushort skillId) =>
        _skillDamage.TryGetValue(skillId, out var s) && s.Count > 0 ? (double)s.Sum / s.Count : -1;

    /// <summary>How many landed hits have been sampled for this skill (0 = no evidence)</summary>
    public int SkillDamageSamples(ushort skillId) =>
        _skillDamage.TryGetValue(skillId, out var s) ? s.Count : 0;

    /// <summary>Milliseconds until this skill is usable again, 0 = ready now</summary>
    public double SkillReadyInMs(ushort skillId, double cooldownMs, double castTimeMs)
    {
        if (cooldownMs <= 0) return 0;
        if (SkillStartedAtUtc(skillId) is not { } started) return 0;
        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
        return Math.Max(0, (cooldownMs + Math.Max(0, castTimeMs)) - elapsed);
    }

    /// <summary>When this skill was last cast, or null if never this session</summary>
    public DateTime? SkillLastCastAtUtc(ushort skillId) =>
        _lastSkillCast.TryGetValue(skillId, out var t) ? t : null;

    // BAG CAPACITY — single source of truth
    public const int BagPageSlots = 24;

    public const int BagPagesAssumed = 2;

    // BAG CAPACITY = 2 pages x 24 slots = 48
    public int BagCapacity => BagPageSlots * BagPagesAssumed;

    /// <summary>Free bag slots (capacity minus occupied)</summary>
    public int BagFreeSlots => Math.Max(0, BagCapacity - _inventory.Count);

    /// <summary>Passive skill ids the character has learned, from the login passive list (NC_CHAR_CLIENT_PASSIVE_CMD 0x103E)</summary>
    public IReadOnlyCollection<ushort> LearnedPassives => _passives.Keys.ToArray();

    /// <summary>True if the character has learned the given skill id — the "do I already know this" check</summary>
    public bool HasSkill(ushort skillId, bool passive) =>
        passive ? _passives.ContainsKey(skillId) : _skills.ContainsKey(skillId);

    /// <summary>The NC_CHAR_CLIENT_ITEM_CMD box value that holds WORN gear (vs bag pages)</summary>
    private const byte EquipBox = 8;
    // The character has MULTIPLE inventory boxes; the box is encoded in the item's inven position as (inven >> 10) —…
    private const byte MainBag = 9;
    /// <summary>The PERSONAL STORAGE (warehouse) container</summary>
    public const byte StorageBoxId = 6;
    private static byte BoxOf(int inven) => (byte)(inven >> 10);

    /// <summary>Seed bag + worn-gear from the zone-login item list (captured by during the login burst, which the session loop…</summary>
    public void SeedItems(IEnumerable<(byte box, ushort inven, ushort itemId, int count)>? items)
    {
        if (items is null) return;
        int bag = 0, eq = 0;
        foreach (var (box, inven, itemId, count) in items)
        {
            // itemId 0 = the REAL item "Leather Boots" (a real occupied slot), NOT empty — the login list sends only occupie…
            var slot = (byte)(inven & 0xFF);
            if (box == EquipBox) { _equipment[slot] = itemId; eq++; }
            else if (box == MainBag) { _inventory[slot] = itemId; _invCount[slot] = count; bag++; } // ONLY
            // the main bag (other boxes — premium/mini-house — collide on slot and hide the real loot)
        }
        if (bag + eq > 0)
        {
            // Log the actual EQUIPPED item ids (by slot) — so "what is the bot wearing" is traceable (a fighter on just a st…
            var worn = string.Join(",", _equipment.OrderBy(kv => kv.Key).Select(kv => $"slot{kv.Key}=item{kv.Value}"));
            _log?.Invoke($"[ZoneView] seeded {bag} bag + {eq} equipped items from login — worn: {worn}");
        }
    }

    /// <summary>Seed the learned-skill set from the zone-login skill list (captured by during the login burst, which the sessi…</summary>
    public void SeedSkills(IEnumerable<ushort>? skills)
    {
        if (skills is null) return;
        var added = 0;
        // id 0 is a REAL skill (ActiveSkill.ID=0), not a sentinel — see the OpClientSkill handler's note
        foreach (var s in skills) if (_skills.TryAdd(s, 1)) added++;
        if (added > 0)
        {
            _log?.Invoke($"[ZoneView] seeded {added} learned skills: {string.Join(",", _skills.Keys.OrderBy(k => k))}");
            SkillsChanged?.Invoke();
        }
    }

    /// <summary>Seed the learned PASSIVE skills from the zone-login passive list (0x103E)</summary>
    public void SeedPassives(IEnumerable<ushort>? passives)
    {
        if (passives is null) return;
        var added = 0;
        foreach (var p in passives) if (_passives.TryAdd(p, 1)) added++;
        if (added > 0)
        {
            _log?.Invoke($"[ZoneView] seeded {added} learned passives: {string.Join(",", _passives.Keys.OrderBy(k => k))}");
            SkillsChanged?.Invoke();
        }
    }

    /// <summary>Raised when the learned-skill list is (re)populated at zone login</summary>
    public event Action? SkillsChanged;
    /// <summary>(skillId, level, isPassive) — the server CONFIRMED a learn (0x4804). Carries WHICH skill, so a
    /// viewer can react to the learn itself instead of re-fetching the whole list and diffing it.</summary>
    public event Action<int, int, bool>? SkillLearned;
    /// <summary>(skillId, target) — the server confirmed one of OUR casts started and named the skill (0x244E)</summary>
    public event Action<int, int>? SkillCastStarted;

    // ── Personal storage (warehouse) ───────────────────────────────────────────────────────────────
    private (byte Slot, ushort ItemId)[] _storageItems = [];

    /// <summary>Contents of the personal storage as of the last open (0x3C08), as (slot, itemId)</summary>
    public IReadOnlyList<(byte Slot, ushort ItemId)> StorageItems => _storageItems;

    /// <summary>The inventory BOX id storage lives in — learned from the wire (every item `location` in the storage-open packe…</summary>
    public int StorageBox { get; private set; } = StorageBoxId;

    /// <summary>Money currently held IN storage (the `cen` field of the storage-open packet)</summary>
    public ulong StorageCen { get; private set; }

    /// <summary>Current / maximum storage page from the last open</summary>
    public byte StoragePage { get; private set; }
    public byte StorageMaxPage { get; private set; }

    /// <summary>UTC of the last successful storage open, or null if the last attempt FAILED (0x3C07)</summary>
    public DateTime? StorageOpenUtc { get; private set; }

    /// <summary>True while a storage session is genuinely open RIGHT NOW</summary>
    public bool StorageOpen => StorageOpenUtc is { } t && (DateTime.UtcNow - t) < TimeSpan.FromSeconds(10);

    /// <summary>Monotonic count of NC_ITEM_CELLCHANGE_CMD received</summary>
    public int CellChangeCount { get; private set; }

    /// <summary>Raised when storage opens with its contents</summary>
    public event Action<IReadOnlyList<(byte Slot, ushort ItemId)>>? StorageOpened;

    private readonly HashSet<int> _doneQuests = new();
    private readonly ConcurrentDictionary<int, byte> _activeQuests = new();
    private readonly HashSet<int> _availableQuests = new();
    private readonly ConcurrentDictionary<int, int> _questProgress = new(); // questId -> kills credited this session (0x440D)

    /// <summary>Kills the server has CREDITED to a quest this session (counted from 0x440D NC_QUEST_NOTIFY_MOB_KILL_CMD)</summary>
    public int QuestProgress(int id) => _questProgress.TryGetValue(id, out var n) ? n : 0;

    /// <summary>Credited kills for ONE objective of a quest, from the objIdx the server sends alongside each credit</summary>
    private readonly ConcurrentDictionary<int, int> _questObjProgress = new();
    public int QuestObjProgress(int questId, int objIdx) =>
        _questObjProgress.TryGetValue((questId << 16) | (objIdx & 0xFFFF), out var n) ? n : 0;

    /// <summary>Reset a quest's credited-kill progress to 0</summary>
    public void ResetQuestProgress(int id)
    {
        _questProgress[id] = 0;
        // The per-objective counters must reset with the aggregate, or a repeatable's second run shows the previous run'…
        for (var oi = 0; oi < 5; oi++) _questObjProgress.TryRemove((id << 16) | oi, out _);
    }

    /// <summary>Quest ids the character can accept right now — the server's available list from the login QUEST_READ burst (0x…</summary>
    public IReadOnlyCollection<int> AvailableQuests => _availableQuests;
    public bool IsQuestAvailable(int id) => _availableQuests.Contains(id);

    /// <summary>Quest ids the character has completed (from the login QUEST_DONE burst)</summary>
    public IReadOnlyCollection<int> DoneQuests => _doneQuests;

    /// <summary>Quest ids currently in progress → their Status byte (from the login QUEST_DOING burst)</summary>
    public IReadOnlyDictionary<int, byte> ActiveQuests => _activeQuests;

    public bool IsQuestDone(int id) => _doneQuests.Contains(id);
    public bool IsQuestActive(int id) => _activeQuests.ContainsKey(id);

    /// <summary>Seed completed + in-progress quest ids from the zone-login burst (NC_CHAR_QUEST_DONE_CMD / QUEST_DOING, captur…</summary>
    public void SeedQuests(IEnumerable<ushort>? done,
        IEnumerable<(ushort id, byte status, int progress, IReadOnlyList<int> objCounts)>? active,
        IEnumerable<ushort>? available = null)
    {
        if (done is not null) foreach (var d in done) _doneQuests.Add(d);
        // Seed both the status AND the credited progress (sum of End_NPCMobCount) from the zone's QUEST_DOING snapshot
        if (active is not null) foreach (var (id, st, prog, objCounts) in active)
        {
            _activeQuests[id] = st; _questProgress[id] = prog; _doneQuests.Remove(id);
            // Seed the PER-OBJECTIVE counters from the same snapshot
            if (objCounts is null) continue;
            for (var oi = 0; oi < objCounts.Count; oi++)
                if (objCounts[oi] > 0) _questObjProgress[(id << 16) | (oi & 0xFFFF)] = objCounts[oi];
        }
        if (available is not null) foreach (var a in available) _availableQuests.Add(a);
        if (_doneQuests.Count > 0 || _activeQuests.Count > 0 || _availableQuests.Count > 0)
            _log?.Invoke($"[ZoneView] seeded quests: done={_doneQuests.Count} active={_activeQuests.Count} available={_availableQuests.Count}");
    }

    // --- Quest accept/start result (NC_QUEST_START_ACK / SELECT_START_ACK / QUEST_ERR) --- NC_QUEST_START_ACK carri…
    private int _lastStartReqQuestId = -1;
    private readonly ConcurrentDictionary<int, int> _questAcceptErr = new(); // questId -> last server err code

    /// <summary>Record that a START_REQ for was just sent, so the next NC_QUEST_START_ACK (which has no questId) can be attrib…</summary>
    public void NoteQuestStartAttempt(int questId) => _lastStartReqQuestId = questId;

    /// <summary>The server's last accept/start result for a quest: 0 = accepted OK, &amp;gt;0 = a refusal reason code (from START_…</summary>
    public int QuestAcceptErr(int id) => _questAcceptErr.TryGetValue(id, out var e) ? e : -1;

    /// <summary>(questId, err) of the most recent accept result, or null</summary>
    public (int QuestId, int Err)? LastQuestAcceptResult { get; private set; }

    /// <summary>(questId, err) of the most recent GIVE_UP_ACK, or null if we have never abandoned a quest this session.
    /// err 0 = the server abandoned it; anything else = REFUSED and the quest is still held. Give-up is irreversible,
    /// so the caller must read this rather than assume the REQ landed.</summary>
    public (int QuestId, int Err)? LastGiveUpResult { get; private set; }

    /// <summary>Raised on every quest accept/start result (success or refusal) with (questId, err)</summary>
    public event Action<int, int>? QuestAcceptResult;

    private void RecordQuestAcceptResult(int questId, int err)
    {
        if (questId >= 0) _questAcceptErr[questId] = err;
        LastQuestAcceptResult = (questId, err);
        if (err == 0 && questId >= 0) MarkQuestActive(questId);
        _log?.Invoke($"[ZoneView] QUEST_ACCEPT_RESULT quest={questId} err={err}{(err == 0 ? " (accepted)" : " (refused)")}");
        QuestAcceptResult?.Invoke(questId, err);
    }

    /// <summary>Mark a quest active (just accepted) / done (just turned in) so the driver's view stays current within the sess…</summary>
    public void MarkQuestActive(int id, byte status = 1) { _activeQuests[id] = status; _availableQuests.Remove(id); _doneQuests.Remove(id); }
    public void MarkQuestDone(int id) { _activeQuests.TryRemove(id, out _); _availableQuests.Remove(id); _doneQuests.Add(id); }

    /// <summary>The quest-dialogue step the server is currently prompting (last NC_QUEST_SCRIPT_CMD_REQ), or null if none pend…</summary>
    public QuestStep? PendingQuest { get; private set; }

    private readonly System.Collections.Concurrent.ConcurrentQueue<QuestStep> _questScript = new();
    /// <summary>Dequeue the next un-answered quest-script page (FIFO), or null if none queued</summary>
    public QuestStep? DequeueQuestStep() => _questScript.TryDequeue(out var s) ? s : null;
    /// <summary>Drop any stale queued pages + the pending prompt — call before driving a fresh dialogue</summary>
    public void ClearQuestScript() { while (_questScript.TryDequeue(out _)) { } PendingQuest = null; }

    /// <summary>Raised on each quest-dialogue prompt (NC_QUEST_SCRIPT_CMD_REQ)</summary>
    public event Action<QuestStep>? QuestPrompt;

    public bool TryGetPlayer(ushort handle, out NearbyPlayer player) => _nearby.TryGetValue(handle, out player!);

    /// <summary>The bot's own zone handle (from the [1802] MAP_LOGIN_ACK)</summary>
    public ushort? SelfHandle { get; set; }

    /// <summary>Are WE in battle mode, as the SERVER last said (0x2009)?</summary>
    public bool? SelfInBattleMode { get; private set; }

    /// <summary>Supplies the bot's current world position (set by the manager to the live tracked position)</summary>
    public Func<(uint X, uint Y)?>? SelfPositionProvider { get; set; }

    // Geometry captured at the instant we transmitted a cast, so a CAST_FAIL can be reported against what was true W…
    private ushort _castAtSkill, _castAtTarget;
    private DateTime _castAtUtc = DateTime.MinValue;
    private (uint X, uint Y)? _castAtSelf, _castAtTargetPos;

    /// <summary>Called by the sender the moment a cast goes out — records the skill, target and both positions so CAST_FAIL ca…</summary>
    public void NoteCastAttempt(ushort skill, ushort target)
    {
        _castAtSkill = skill; _castAtTarget = target; _castAtUtc = DateTime.UtcNow;
        _castAtSelf = SelfPositionProvider?.Invoke();
        _castAtTargetPos = null;
        foreach (var n in NearbyNpcs)
            if (n.Handle == target) { _castAtTargetPos = (n.X, n.Y); break; }
    }

    private static double Dist((uint X, uint Y)? a, (uint X, uint Y)? b) =>
        a is { } p && b is { } q ? Math.Sqrt(Math.Pow((double)p.X - q.X, 2) + Math.Pow((double)p.Y - q.Y, 2)) : -1;

    /// <summary>Returns true if a mob id is a huntable enemy (set by the manager from client MobInfo — see )</summary>
    private readonly HashSet<ushort> _scenarioFightable = new();
    public bool IsScenarioFightable(ushort handle) { lock (_scenarioFightable) return _scenarioFightable.Contains(handle); }

    /// <summary>Raised when the SERVER's target selection is (or may be) gone: our death, or the target's</summary>
    public Action<string, string>? BotEventSink { get; set; }

    /// <summary>Raised when the SERVER's target selection is (or may be) gone — the target died, we died, or the map changed</summary>
    public Action<string>? TargetInvalidated { get; set; }

    /// <summary>What the manager currently believes it has targeted, so death handling can tell whether the entity that just d…</summary>
    public ushort CurrentTargetHandle { get; set; }

    public Func<ushort, bool>? IsHuntableMob { get; set; }

    /// <summary>Returns true if an abstate index IMMOBILIZES the target (set by the manager from client AbState/SubAbState — s…</summary>
    public Func<uint, bool>? IsMoveBlockingAbstate { get; set; }

    /// <summary>Of the move-blocking abstates, which are STUNS (block actions too) rather than roots/entangles (movement only)…</summary>
    public Func<uint, bool>? IsStunAbstate { get; set; }

    // The abstate indices currently ACTIVE on SELF → EXPIRY tick (Environment.TickCount64)
    private readonly Dictionary<uint, long> _selfAbstates = new();
    private readonly object _selfAbstateLock = new();

    /// <summary>True while a movement-blocking abnormal state (stun/root/entangle) is active on the bot — the server will MOVE…</summary>
    public bool Rooted
    {
        get
        {
            if (IsMoveBlockingAbstate is not { } f) return false;
            long now = Environment.TickCount64;
            lock (_selfAbstateLock)
            {
                foreach (var kv in _selfAbstates) if (kv.Value > now && f(kv.Key)) return true;
                return false;
            }
        }
    }

    /// <summary>Snapshot of the abstate indices currently active (unexpired) on the bot (for loud logging)</summary>
    public uint[] SelfAbstateSnapshot()
    {
        long now = Environment.TickCount64;
        lock (_selfAbstateLock) return _selfAbstates.Where(kv => kv.Value > now).Select(kv => kv.Key).ToArray();
    }

    /// <summary>Record a SELF abstate change from any channel and LOG IT LOUD (operator 2026-07-21)</summary>
    private void SelfAbstate(uint idx, uint restKeeptimeMs, bool active, string src)
    {
        bool moveBlock = IsMoveBlockingAbstate?.Invoke(idx) == true;
        if (moveBlock && active)
            MetricSink?.Invoke(IsStunAbstate?.Invoke(idx) == true ? "stuns" : "roots", 1);
        long now = Environment.TickCount64;
        bool changed;
        lock (_selfAbstateLock)
        {
            if (active)
            {
                changed = !_selfAbstates.ContainsKey(idx);
                _selfAbstates[idx] = restKeeptimeMs > 0 ? now + restKeeptimeMs : long.MaxValue;
            }
            else changed = _selfAbstates.Remove(idx);
        }
        var msg = $"[ZoneView] ABSTATE {(active ? "SET" : "RESET")} idx={idx} on SELF via {src}" +
                  $"{(moveBlock ? " — MOVE-BLOCKING (stun/root)" : "")}" +
                  $"{(active && restKeeptimeMs > 0 ? $" keeptime={restKeeptimeMs}ms" : "")}" +
                  $" (moveBlock={moveBlock}, rooted={Rooted})";
        // Loud on any move-blocking change (a stun/root is critical) and on any first SET/RESET; quiet on the periodic B…
        if (moveBlock || changed) _log?.Invoke(msg); else LogV(msg);
    }

    /// <summary>The value this protocol uses for "this slot is empty".
    ///
    /// It is 0xFFFF, NOT 0. Item id 0 is the real item "Leather Boots", so every <c>itemId != 0</c> test written to
    /// mean "slot occupied" silently deletes that item instead: the login bag snapshot dropped the slot (BagFreeSlots
    /// then over-reported, and the sell/declutter classifier never saw it), and an equip of item 0 was read as
    /// UNequipping, leaving the character reported bare in that slot for the rest of the session.
    ///
    /// Named rather than repeated, so the next bare <c>!= 0</c> reads as visibly wrong beside it.</summary>
    private const ushort EmptyCellItemId = 0xFFFF;

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
            NoteEntityGone(hnd);
            // LEAVING VIEW INVALIDATES THE SELECTION TOO, exactly as death and teleport already do. Without this the
            // assertion outlived the entity: MageFresh 2026-08-20 reported target h=6755 held for 225 SECONDS with
            // inView=false and kind="unseen". Combat still recovered, because attacking a DIFFERENT handle re-sends
            // TARGETTING on the CurrentTarget != target check -- but re-engaging the SAME handle after it flickered
            // out and back would skip the re-assert, and every readout of our target was a handle that is not there.
            // Re-asserting on a flicker costs one TARGETTING packet, which is what the real client sends anyway.
            if (hnd == CurrentTargetHandle) TargetInvalidated?.Invoke($"target h={hnd} left view");
        }
        else if (op == OpReallyKill)
        {
            // A mob died (REALLYKILL {dead, attacker}) — retire it NOW rather than waiting for the delayed briefinfo despawn…
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                var dead = (ushort)(p[0] | (p[1] << 8));
                var attacker = (ushort)(p[2] | (p[3] << 8));
                bool mine = SelfHandle != 0 && attacker == SelfHandle;
                LogV($"[ZoneView] REALLYKILL dead={dead} attacker={attacker} self={SelfHandle} mine={mine}");
                // Retire the dead entity from BOTH maps: regular mobs live in _npcs, but scenario/instance enemies (the JCQ "sha…
                bool wasMob = _npcs.TryRemove(dead, out _);
                bool wasRecent = _recentNpcs.TryRemove(dead, out _); // died while flickered-out of view → evict sticky copy
                bool wasChar = _nearby.TryRemove(dead, out _);
                NoteEntityGone(dead);
                if (dead == CurrentTargetHandle) TargetInvalidated?.Invoke($"target h={dead} died");
                if ((wasMob || wasRecent || wasChar) && mine)
                {
                    LastKill = dead; KillsByMe++;
                    _log?.Invoke($"[combat] KILLED {(wasChar && !wasMob ? "clone/char" : "mob")} h={dead} (totalKills={KillsByMe})");
                    MetricSink?.Invoke("kills", 1);
                }
            }
        }
        else if (op == OpBriefMob)
        {
            // A batch of NPC/mob spawns (sent on field enter): [mobnum:1][record × N]
            var p = pkt.Payload.Span;
            if (p.Length >= 1)
            {
                int n = p[0];
                for (int i = 0; i < n; i++)
                    AddOrUpdateNpc(p, 1 + i * MobRecordLen);
                // The bulk batch (the map-enter NPC SEED) carries many records — log the roster size + a few entries so "what do…
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
            // 0xCC02 payload = [mountHandle u16][zero...]
            IsMounted = true;
            MetricSink?.Invoke("mounts", 1);
            _mountedSinceUtc = DateTime.UtcNow;
            ClearCastBar();   // the summon's cast completed — stop holding still
            var p = pkt.Payload.Span;
            if (p.Length >= 2) _mountHandle = (ushort)(p[0] | (p[1] << 8));
            _log?.Invoke($"[ZoneView] mounted (RIDE_ON, mountH={_mountHandle})");
        }
        else if (op == OpMoverRideOff)
        {
            IsMounted = false;
            MetricSink?.Invoke("dismounts", 1);
            // Bank the ride as SECONDS MOUNTED so "time spent on mount" is a real duration, not a count
            if (_mountedSinceUtc is { } ms) { MetricSink?.Invoke("secondsMounted", (DateTime.UtcNow - ms).TotalSeconds); _mountedSinceUtc = null; }
            ClearCastBar();   // the dismount's cast completed
            _mountHandle = null;
            // Reset speed to default running pace (120 u/s)
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
            // Mover-broadcast speed (0xCC0D): any mover's current walk/run speed
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
            // Self-only ACT_MOVESPEED (0x203E): always-self base walk/run speed
            try
            {
                var spd = pkt.ReadBody<PROTO_NC_ACT_MOVESPEED_CMD>();
                ApplySpeed((double)spd.walkspeed, (double)spd.runspeed, "203E");
            }
            catch { }
        }
        else if (op == OpActMoveFail)
        {
            // [back: SHINE_XY] — the server's authoritative position after rejecting our move
            var p = pkt.Payload.Span;
            if (p.Length >= 8)
            {
                var bx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p);
                var by = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
                // DIAGNOSTIC (operator 2026-07-15: "movefail sends the client pos back → should self-heal; something's fishy"): log the bot's BELIEVED position vs the server's authoritative snap-back + the delta, at NO…
                var believed = SelfPositionProvider?.Invoke();
                var deltaU = believed is { } bd ? Math.Sqrt(Math.Pow((double)bx - bd.X, 2) + Math.Pow((double)by - bd.Y, 2)) : 1e9;
                // A real shove-back (delta >= 64u, or unknown) = we're still navigating, NOT parked at the trigger
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
        else if (op == OpItemRelocAck)
        {
            // 2-byte payload = a u16 result code
            var rp = pkt.Payload.Span;
            if (rp.Length >= 2)
            {
                LastRelocAckCode = rp[0] | (rp[1] << 8);
                LastRelocAckAtUtc = DateTime.UtcNow;
                _logLevel?.Invoke(BotLogLevel.Info,
                    $"[ZoneView] RELOC_ACK (0x300C) code={LastRelocAckCode} (0x{LastRelocAckCode:X4})");
            }
        }
        else if (op == OpCreateCastBar)
        {
            // A timed action started on us (mount summon, skill)
            CastBarStartedAtUtc = DateTime.UtcNow;
            _logLevel?.Invoke(BotLogLevel.Note, "[ZoneView] CASTBAR opened (0x2047) — holding still; moving would cancel it");
        }
        else if (op == OpCancelCastBar)
        {
            var heldMs = CastBarStartedAtUtc > DateTime.MinValue
                ? (DateTime.UtcNow - CastBarStartedAtUtc).TotalMilliseconds : -1;
            ClearCastBar();
            var verdict = heldMs < 0 ? "" : heldMs < 1000
                ? " — CUT SHORT, cast interrupted"
                : " — ran to full length; completion is only confirmed by the result packet";
            _logLevel?.Invoke(BotLogLevel.Note,
                $"[ZoneView] CASTBAR closed (0x2048) after {(heldMs < 0 ? 0 : heldMs):F0}ms{verdict}");
        }
        else if (op == OpAbStateSet || op == OpAbStateReset)
        {
            // NC_BAT_ABSTATESET/RESET: [targetHandle u16][abStataIndex u32] (no duration)
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var target = (ushort)(p[0] | (p[1] << 8));
                var idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
                if (SelfHandle is { } self && target == self)
                    SelfAbstate(idx, 0, op == OpAbStateSet, "BAT");
                else LogV($"[ZoneView] ABSTATE {(op == OpAbStateSet ? "SET" : "RESET")} idx={idx} on h={target}");
            }
        }
        else if (op == OpBriefAbstateChange)
        {
            // NC_BRIEFINFO_ABSTATE_CHANGE_CMD: [handle u16] + ABSTATE_INFORMATION [idx u32][restKeeptime u32 ms][strength u3…
            var p = pkt.Payload.Span;
            if (p.Length >= 14)
            {
                var target = (ushort)(p[0] | (p[1] << 8));
                var idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
                var keep = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
                if (SelfHandle is { } self && target == self) SelfAbstate(idx, keep, keep > 0, "BRIEF");
                else LogV($"[ZoneView] ABSTATE CHANGE idx={idx} keep={keep}ms on h={target}");
            }
        }
        else if (op == OpBriefAbstateChangeList)
        {
            // NC_BRIEFINFO_ABSTATE_CHANGE_LIST_CMD: [handle u16][count u8] + count× ABSTATE_INFORMATION (12 bytes each)
            var p = pkt.Payload.Span;
            if (p.Length >= 3)
            {
                var target = (ushort)(p[0] | (p[1] << 8));
                int count = p[2];
                bool self = SelfHandle is { } sh && target == sh;
                int off = 3;
                for (int i = 0; i < count && off + 12 <= p.Length; i++, off += 12)
                {
                    var idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[off..]);
                    var keep = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[(off + 4)..]);
                    if (self) SelfAbstate(idx, keep, keep > 0, "BRIEF_LIST");
                    else LogV($"[ZoneView] ABSTATE LIST idx={idx} keep={keep}ms on h={target}");
                }
            }
        }
        else if (op == OpBatCastFail)
        {
            // Payload = 2-byte LE u16 reason code
            var reason = pkt.Payload.Length >= 2
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pkt.Payload.Span)
                : (ushort)0;
            // The cast was REJECTED — there is no animation, so release the lock at once instead of making the rotation sit…
            EndCast($"CAST_FAIL 0x{reason:X4}");
            // ONE description source (CastFailReason.Describe) for the log, the script hook and the tail
            var known = reason is CastFailReason.NotEnoughSp or CastFailReason.OutOfRange
                              or CastFailReason.NotReady or 0x0FC0 or 0x0FC4 or 0x0FC6;
            // LOG LOUD (operator 2026-08-12)
            {
                var nowSelf = SelfPositionProvider?.Invoke();
                (uint X, uint Y)? nowTgt = null;
                foreach (var n in NearbyNpcs) if (n.Handle == _castAtTarget) { nowTgt = (n.X, n.Y); break; }
                var dAtCast = Dist(_castAtSelf, _castAtTargetPos);
                var dNow    = Dist(nowSelf, nowTgt);
                var mobMoved = Dist(_castAtTargetPos, nowTgt);
                var meMoved  = Dist(_castAtSelf, nowSelf);
                var ageMs = _castAtUtc == DateTime.MinValue ? -1 : (DateTime.UtcNow - _castAtUtc).TotalMilliseconds;
                BotEventSink?.Invoke("castfail", $"0x{reason:X4} skill={_castAtSkill} h={_castAtTarget}");
                _logLevel?.Invoke(BotLogLevel.Note,
                    $"[castfail] 0x{reason:X4} {CastFailReason.Describe(reason)} — skill={_castAtSkill} h={_castAtTarget} " +
                    $"dist@cast={(dAtCast < 0 ? "?" : dAtCast.ToString("F0"))}u dist@fail={(dNow < 0 ? "?" : dNow.ToString("F0"))}u " +
                    $"mobMoved={(mobMoved < 0 ? "?" : mobMoved.ToString("F0"))}u weMoved={(meMoved < 0 ? "?" : meMoved.ToString("F0"))}u " +
                    $"after={ageMs:F0}ms inCombat={InCombat} aggro={Aggressors.Count}");
            }
            _log?.Invoke($"[ZoneView] cast FAILED — {CastFailReason.Describe(reason)} (0x{reason:X4})" +
                         (known ? "" : $" — UNMAPPED code, {pkt.Payload.Length}b payload") +
                         (pkt.Payload.Length > 2 ? $" raw={Convert.ToHexString(pkt.Payload.Span)}" : ""));
            CastFailed?.Invoke(reason);
        }
        else if (op == OpHpChange)
        {
            try
            {
                var hp = pkt.ReadBody<PROTO_NC_BAT_HPCHANGE_CMD>().hp;
                var hpNow = (int)hp;
                if (_stoneHealPendingUntil > DateTime.UtcNow && _hpAtStoneUse >= 0 && hpNow > _hpAtStoneUse)
                {
                    var healed = hpNow - _hpAtStoneUse;
                    _stoneHealPendingUntil = DateTime.MinValue;   // one attribution per use
                    _healSamples++; _healSum += healed;
                    ScalarLearned?.Invoke(ScalarStoneHeal, healed);
                    MetricSink?.Invoke("healsLanded", healed);
                    if (HpStoneRestore > 0 && healed > HpStoneRestore)
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[heal] ⚠️ measured heal {healed} EXCEEDS the shop-advertised charge {HpStoneRestore} — " +
                            "the advertised per-charge value may not be the whole story; sustain model assumption in doubt.");
                    if (MaxHp is { } mxh && mxh > 0 && hpNow < (int)mxh && healed > HpStoneChargeMeasured)
                    {
                        HpStoneChargeMeasured = healed;
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[heal] UNCENSORED charge measured: {healed} HP (healed {_hpAtStoneUse}->{hpNow} of {mxh}, " +
                            $"stopped short of full so nothing clipped it) ⇒ {SustainableHealDps:F0} HP/s");
                    }
                    if (healed > HpStoneHealMax)
                    {
                        HpStoneHealMax = healed;
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[heal] HP stone restores up to {healed} HP (avg {HpStoneHealAvg:F0} over {_healSamples}) — " +
                            $"sustainable {SustainableHealDps:F0} HP/s at the learned {HpStoneCooldownMs:F0}ms cooldown. " +
                            "Incoming damage above that CANNOT be out-healed.");
                    }
                }
                // TAKING DAMAGE MUST NOT DEPEND ON KNOWING WHO FROM (operator 2026-08-12: MageFresh "started taking damage but n…
                if (Hp is { } prevHp && hpNow < (int)prevHp && _stoneHealPendingUntil <= DateTime.UtcNow)
                {
                    var lost = (int)prevHp - hpNow;
                    LastHitAtUtc = DateTime.UtcNow;          // => InCombat true, so heal/flee engage
                    _recentIncoming.Enqueue((DateTime.UtcNow, lost));
                    while (_recentIncoming.Count > 512) _recentIncoming.TryDequeue(out _);
                    // THESE BRACES ARE LOAD-BEARING
                    var hpTail = MaxHp is { } mx && mx > 0 ? $"/{mx}" : "";
                    if (Aggressors.Count == 0)
                    {
                        BotEventSink?.Invoke("damage-unattributed", $"lost={lost} hp={hpNow}");
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[damage] ⛔ CRITICAL: took {lost} with NO tracked attacker (hp {prevHp}->{hpNow}" +
                            hpTail + "). Source is a DOT or a scripted " +
                            "hit we do not attribute — treating it as COMBAT anyway so heal/flee engage.");
                    }
                    else
                    {
                        // Attribution WORKING is the common case; log it at Info so the tail can still show who is hitting us without co…
                        _logLevel?.Invoke(BotLogLevel.Info,
                            $"[damage] took {lost} (hp {prevHp}->{hpNow}{hpTail}) from " +
                            $"{Aggressors.Count} tracked aggressor(s): {string.Join(",", Aggressors.Take(4).Select(a => "h" + a))}");
                    }
                }
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
            // {count u8}{paramId u8, value u32}* — apply MaxHP(0x10)/MaxSP(0x11) live so they track a MID-ZONE level-up (ver…
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
            // NC_MENU_SERVERMENU_REQ: title[128], priority u8 @128, npcHandle u16 @129, npcPosition @131 (8B), limitRange u1…
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
            // NOTE: dying does NOT itself drop the selection — the RESPAWN does, because it teleports you (operator 2026-08-…
        }
        else if (op == OpCharReviveSame)
        {
            Dead = false; DeadAtUtc = DateTime.MinValue;
            // REVIVESAME (same zone server) payload == LINKSAME format {mapId u16, x u32, y u32}
            if (Navigation.MapHandoff.ParseLinkSame(pkt.Payload.Span) is { } h)
            {
                _log?.Invoke($"[ZoneView] revived (same-server) -> mapId={h.MapId} @({h.X},{h.Y}) — re-spawning via LOGINCOMPLETE");
                CurrentMapId = h.MapId;
                _npcs.Clear(); _recentNpcs.Clear(); _npcSeed.Clear(); _npcSeedAll.Clear(); _nearby.Clear(); _drops.Clear();
                // TELEPORTING DROPS THE SERVER-SIDE SELECTION (operator 2026-08-13: "Teleportation in general untargets" — and s…
                TargetInvalidated?.Invoke("teleported — the server drops the selection on a teleport");
                _mobAnchor.Clear();   // handles are PER-MAP and get reused — a stale anchor from the previous
                                      // map makes a fresh mob look like it chased thousands of units from home Same reason, same danger: a retained "t…
                lock (_scenarioFightable) _scenarioFightable.Clear();
                lock (_selfAbstateLock) _selfAbstates.Clear();  // abstates are per-map; server re-broadcasts
                LastScenarioArea = null; InScenarioInstance = false; _scenarioAckedAreas.Clear();
                MapChanged?.Invoke(h);
            }
        }
        else if (op == OpBatExpGain)
        {
            // {expgain u32@0, mobhandle u16@4}
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                long gain = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(0, 4));
                SessionExpGained += gain;
                MetricSink?.Invoke("expGained", gain);
                // Accumulate ALWAYS. When unseeded this banks the gain instead of dropping it (see _expPendingDelta) — the old `…
                if (Exp >= 0) Exp += gain; else _expPendingDelta += gain;
                // Attribute this kill's exp to the MOB that gave it (handle @4) so the leveler can learn per-mob exp (decode → l…
                string mobTag = "";
                if (p.Length >= 6)
                {
                    ushort mh = (ushort)(p[4] | (p[5] << 8));
                    int mobId = _npcs.TryGetValue(mh, out var live) ? live.MobId
                              : _recentNpcs.TryGetValue(mh, out var re) ? re.Npc.MobId : -1;
                    if (mobId >= 0 && gain > 0)
                    {
                        var acc = _mobExp.AddOrUpdate(mobId, (gain, 1), (_, cur) => (cur.Total + gain, cur.Kills + 1));
                        mobTag = $" from mob{mobId} (avg {acc.Total / acc.Kills}/kill over {acc.Kills})";
                    }
                }
                _logLevel?.Invoke(BotLogLevel.Info, $"[exp] +{gain} -> {(Exp >= 0 ? Exp.ToString() : $"UNSEEDED (banked {_expPendingDelta}; login burst carried no exp — absolute unknown until the server sends one)")} (session +{SessionExpGained}){mobTag}");
            }
        }
        else if (op == OpBatExpLost)
        {
            // {explost u32@0} — exp PENALTY (death)
            var p = pkt.Payload.Span;
            if (p.Length >= 4)
            {
                long lost = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(0, 4));
                SessionExpLost += lost;
                MetricSink?.Invoke("expLostToDeath", lost);
                MetricSink?.Invoke("deaths", 1);
                if (Exp >= 0) Exp = Math.Max(0, Exp - lost); else _expPendingDelta -= lost;
                _logLevel?.Invoke(BotLogLevel.Note, $"[exp] DEATH -{lost} -> {(Exp >= 0 ? Exp.ToString() : "?")} (session lost {SessionExpLost})");
            }
        }
        else if (op == OpBatCastSuc)
        {
            // Authoritative "your cast succeeded" — the server agrees we are casting
            if (CastInFlight)
            {
                CastServerConfirmed = true;
                _logLevel?.Invoke(BotLogLevel.Verbose, "[combat] cast SUC_ACK — server confirms cast in progress");
            }
        }
        else if (op == OpBatHitDamage)
        {
            // The cast RESOLVED (damage applied) — authoritative end of the animation lock
            EndCast("HIT_DAMAGE — cast resolved");
            // OUR SKILL DAMAGE — the other half of offensive output
            var hp2 = pkt.Payload.Span;
            if (hp2.Length >= 5)
            {
                var caster = (ushort)(hp2[2] | (hp2[3] << 8));
                int targets = hp2[4];
                if (SelfHandle is { } meC && caster == meC)
                {
                    const int HeaderLen = 5, EntryLen = 14;
                    long dealt = 0; var hits = 0; var parts = new List<string>();
                    for (var i = 0; i < targets; i++)
                    {
                        var off = HeaderLen + i * EntryLen;
                        if (off + EntryLen > hp2.Length) break;   // truncated/short frame — take what parsed
                        var tgt = (ushort)(hp2[off] | (hp2[off + 1] << 8));
                        var dmg = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hp2[(off + 4)..]);
                        var rest = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hp2[(off + 8)..]);
                        var tMob = _npcs.TryGetValue(tgt, out var tn) ? tn.MobId
                                 : _recentNpcs.TryGetValue(tgt, out var tr) ? tr.Npc.MobId : 0;
                        hits++; dealt += dmg;
                        parts.Add($"mob{tMob} h={tgt} dmg={dmg} resthp={rest}" + (dmg == 0 ? " — WHIFF" : ""));
                        // ATTRIBUTE THIS DAMAGE TO THE SKILL THAT CAUSED IT, via the cast index this frame shares with its 0x244E
                        if (dmg > 0)
                        {
                            var castIdx = (ushort)(hp2[0] | (hp2[1] << 8));
                            if (_castIndexSkill.TryGetValue(castIdx, out var srcSkill))
                            {
                                var upd = _skillDamage.AddOrUpdate(srcSkill,
                                    _ => (1, dmg, (int)dmg),
                                    (_, st) => (st.Count + 1, st.Sum + dmg, Math.Max(st.Max, (int)dmg)));
                                // Log the RUNNING AVERAGE, not the single hit: one number tells you nothing about a skill, and the whole point i…
                                _logLevel?.Invoke(BotLogLevel.Info,
                                    $"[skilldmg] skill{srcSkill} landed {dmg} — avg {(double)upd.Sum / upd.Count:F1} " +
                                    $"over {upd.Count} (max {upd.Max})");
                            }
                        }
                        if (dmg > 0)
                        {
                            MetricSink?.Invoke("damageDealt", dmg);
                            // A landing SKILL is proof we are in range and faced, exactly like a landing swing — NeedsFacingAdjust keys off…
                            LastRealDamageDealtAtUtc = DateTime.UtcNow;
                        }
                    }
                    _logLevel?.Invoke(BotLogLevel.Info,
                        $"[skillhit] OUR skill hit {hits} target(s) for {dealt} total — {string.Join(" | ", parts)}");
                    MetricSink?.Invoke("skillHits", targets > 0 ? targets : 1);
                }
                else if (SelfHandle is { } meD)
                {
                    const int HeaderLen = 5, EntryLen = 14;
                    for (var i = 0; i < targets; i++)
                    {
                        var off = HeaderLen + i * EntryLen;
                        if (off + EntryLen > hp2.Length) break;
                        var tgt = (ushort)(hp2[off] | (hp2[off + 1] << 8));
                        if (tgt != meD) continue;                       // someone else's fight
                        var dmg = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hp2[(off + 4)..]);
                        var rest = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hp2[(off + 8)..]);
                        _logLevel?.Invoke(BotLogLevel.Info,
                            $"[skillhit] TOOK a skill hit from h={caster} dmg={dmg} resthp={rest}");
                        NoteHit(new HitInfo(caster, tgt, (ushort)Math.Min(ushort.MaxValue, dmg), rest));
                    }
                }
            }
        }
        else if (op == OpBatCastAbort || op == OpBatCastCut)
        {
            // Cast interrupted (moved / stunned / target lost)
            EndCast(op == OpBatCastAbort ? "CASTABORT" : "CASTCUT");
        }
        else if (op == OpBatTargetInfo)
        {
            // PROTO_NC_BAT_TARGETINFO_CMD {order u8 @0, targethandle u16 @1, targethp u32 @3, targetmaxhp u32 @7, sp/lp afte…
            var tp = pkt.Payload.Span;
            if (tp.Length >= 11)
            {
                var th = (ushort)(tp[1] | (tp[2] << 8));
                var thp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tp.Slice(3, 4));
                var tmax = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tp.Slice(7, 4));
                TargetConfirmedHandle = th; TargetConfirmedAtUtc = DateTime.UtcNow;
                // Free and worth having: this is the ONLY packet that states a target's max HP outright, so the target view stop…
                _entityHp[th] = thp; _entityMaxHp[th] = tmax;
                _logLevel?.Invoke(BotLogLevel.Verbose,
                    $"[combat] TARGETINFO — server CONFIRMED target h={th} ({thp}/{tmax} hp)");
            }
        }
        else if (op == OpBatCeaseFire)
        {
            // {handle u16@0}. Broadcast for ANY entity that stopped attacking, so filter to our own handle before treating i…
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                var who = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(0, 2));
                if (SelfHandle is { } self && who == self)
                {
                    var wasActive = BashActive;
                    BashActive = false;
                    LastBashCeasedAtUtc = DateTime.UtcNow;
                    BashCeasedCount++;
                    // Log only when this actually STOPPED something. The already-idle case is a no-op and was
                    // 538 of 65,961 sampled lines saying nothing happened.
                    if (wasActive)
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[combat] CEASE_FIRE on SELF — melee auto-attack STOPPED (session {BashCeasedCount})");
                    try { BashCeased?.Invoke(who); } catch { }
                }
                else
                {
                    _logLevel?.Invoke(BotLogLevel.Verbose, $"[combat] CEASE_FIRE h={who} (other entity)");
                }
            }
        }
        else if (op == OpCharExpChanged)
        {
            // {wmhandle u16@0, CharNo u32@2, CurrentExp u64@6} — AUTHORITATIVE absolute exp
            var p = pkt.Payload.Span;
            if (p.Length >= 14)
            {
                long cur = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(6, 8));
                long prev = Exp;
                Exp = cur;
                ExpChanged?.Invoke(cur);
                string d = prev >= 0 ? (cur - prev >= 0 ? $"+{cur - prev}" : (cur - prev).ToString()) : "seed";
                _logLevel?.Invoke(BotLogLevel.Info, $"[exp] SERVER-SET {cur} (was {(prev >= 0 ? prev.ToString() : "?")}, {d})");
            }
        }
        else if (op == OpCharLevelChanged)
        {
            // {wmhandle u16, charNo u32, newLevel u8}
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
        else if (op == OpSomeoneChangeMode)
        {
            var pm = pkt.Payload.Span;
            if (pm.Length >= 3 && SelfHandle is { } selfH && (ushort)(pm[0] | (pm[1] << 8)) == selfH)
            {
                var battle = pm[2] == 2;          // mode 2 = battle, 1 = normal
                if (SelfInBattleMode != battle)
                    _logLevel?.Invoke(BotLogLevel.Note,
                        $"[combat] battle mode -> {(battle ? "BATTLE" : "NON-BATTLE")} (server 0x2009). " +
                        (battle ? "" : "Casts would now fail 0x0FC0 until we re-enter."));
                SelfInBattleMode = battle;
            }
        }
        else if (op == OpSkillStart)
        {
            // Cast ACCEPTED — start this skill's cooldown from here, not from when we asked
            var ps = pkt.Payload.Span;
            if (ps.Length >= 2)
            {
                var startedSkill = (ushort)(ps[0] | (ps[1] << 8));
                NoteSkillStarted(startedSkill);
                // REMEMBER WHICH CAST THIS IS, so the damage that arrives later can be attributed to the SKILL that caused it
                if (ps.Length >= 6)
                {
                    var idx = (ushort)(ps[4] | (ps[5] << 8));
                    _castIndexSkill[idx] = startedSkill;
                    while (_castIndexOrder.Count > 256 && _castIndexOrder.TryDequeue(out var old))
                        _castIndexSkill.TryRemove(old, out _);
                    _castIndexOrder.Enqueue(idx);
                }
                _logLevel?.Invoke(BotLogLevel.Verbose,
                    $"[combat] skill {startedSkill} STARTED (0x244E) — cooldown clock begins");
                // 0x244E is {skill u16 @0, targetobj u16 @2, index u16 @4} and PROTO_NC_BAT_SKILLBASH_HIT_OBJ_START_CMD
                // is SizeOf=6 in the PDB — the target is ALWAYS present. An earlier comment here invented a "shorter
                // form" and fell back to handle 0, which is a REAL entity handle, so a subscriber could not tell
                // "server confirmed a cast on handle 0" from "we failed to parse it". A frame too short to hold the
                // documented struct is a decode gap and is reported as one rather than papered over with a 0.
                if (ps.Length >= 4) SkillCastStarted?.Invoke(startedSkill, ps[2] | (ps[3] << 8));
                else _log?.Invoke($"[ZoneView] 0x244E SHORT FRAME ({ps.Length}b, expected 6) — target not decoded");
            }
        }
        else if (op == OpBatLevelup)
        {
            // OUR OWN level-up (NC_BAT_LEVELUP_CMD 0x240C): new level is the first byte
            var p = pkt.Payload.Span;
            if (p.Length >= 1 && p[0] > 0)
            {
                byte newLevel = p[0];
                _log?.Invoke($"[ZoneView] LEVEL UP (NC_BAT_LEVELUP 0x240C) -> {newLevel}");
                LevelChanged?.Invoke(newLevel);
            }
        }
        else if (op == OpCharPromoteAck)
        {
            // JOB CHANGE — {newclass u8}
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
            // SCENARIO room trigger — echo the ACK (same areaindex) to arm the mob wave
            var req = pkt.ReadBody<PROTO_NC_SCENARIO_AREAENTRY_REQ>();
            int z = Array.IndexOf(req.areaindex.n8_name, (byte)0);
            var area = System.Text.Encoding.ASCII.GetString(req.areaindex.n8_name, 0, z < 0 ? req.areaindex.n8_name.Length : z);
            LastScenarioArea = area;
            InScenarioInstance = true;   // latch: we're inside a scenario instance (survives between-room gaps)
            // DIAGNOSTIC (operator 2026-07-15): log WHERE we are when the server sends each AreaEntry REQ — the server sends…
            _log?.Invoke($"[ZoneView] SCENARIO AREAENTRY_REQ '{area}' — server saw us cross; self@{SelfPositionProvider?.Invoke()}");
            ScenarioAreaEntered?.Invoke(area);
            var ackArea = req.areaindex;
            var reqAt = DateTime.UtcNow;
            var mapAtReq = CurrentMapId;
            _ = Task.Run(async () =>
            {
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
                    // TIMEOUT — we never got shove-free INSIDE area A's box within the window
                    _log?.Invoke($"[ZoneView] ⛔ CRITICAL: AreaEntry ack for '{area}' TIMED OUT after {ArriveTimeoutMin}min — never arrived shove-free INSIDE its box (nav stuck short of the trigger? box unreachable?). Consider increasing the window or fixing nav. Acking from here as a last resort.");
                }
                // (2) ARRIVED inside the trigger (shove-free = server-valid position, no desync since we detect MOVEFAILs)
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
            // A scripted scenario entity changed kind (see const doc)
            var b = pkt.ReadBody<PROTO_NC_SCENARIO_OBJTYPECHANGE_CMD>();
            _log?.Invoke($"[ZoneView] scenario OBJTYPECHANGE h={b.handle} type={b.type}" +
                (b.type == ScenObjTypeNpc ? " (change2npc → clearing phantom clone)" :
                 b.type == ScenObjTypeMob ? " (change2mob → fightable)" : " (unknown type)"));
            if (b.type != ScenObjTypeMob)
            {
                lock (_scenarioFightable) _scenarioFightable.Remove(b.handle);
                if (_nearby.TryRemove(b.handle, out var gone)) PlayerLeft?.Invoke(b.handle);
                _npcs.TryRemove(b.handle, out _);
                NoteEntityGone(b.handle);
            }
            else
            {
                // change2mob HAD NO BRANCH AT ALL — the server told us "this entity is now a FIGHTABLE MOB" and we threw it away…
                lock (_scenarioFightable) _scenarioFightable.Add(b.handle);
                NoteEntityChanged(b.handle);
                if (_nearby.TryGetValue(b.handle, out var nb))
                    _logLevel?.Invoke(BotLogLevel.Note,
                        $"[ZoneView] scenario clone h={b.handle} '{nb.Name}' L{nb.Level} is now FIGHTABLE — " +
                        $"projected into the mob list at its LIVE position ({nb.X},{nb.Y}) so it is targetable, " +
                        "drawable and can be blamed for damage");
                else
                    // No player record: say so LOUDLY rather than inventing a position, which would put a phantom at the map origin…
                    _logLevel?.Invoke(BotLogLevel.Note,
                        $"[ZoneView] ⛔ CRITICAL: scenario clone h={b.handle} turned FIGHTABLE but we hold no " +
                        "player record for it — it stays invisible to targeting and the combat map. This is the " +
                        "instance-death gap; find where its spawn/briefinfo is being missed.");
            }
        }
        else if (op == OpBriefInfoBuildDoor)
        {
            // A scenario DOOR spawned (0x1C0F) — the authoritative handle→name→initial-state link
            var bd = pkt.ReadBody<PROTO_NC_BRIEFINFO_BUILDDOOR_CMD>();
            int z = Array.IndexOf(bd.blockindex.n8_name, (byte)0);
            var name = System.Text.Encoding.ASCII.GetString(bd.blockindex.n8_name, 0,
                z < 0 ? bd.blockindex.n8_name.Length : z);
            if (!string.IsNullOrEmpty(name))
            {
                _doorNames[bd.handle] = name;
                // A DOORSTATE update for this handle may have arrived BEFORE the BUILDDOOR that names it. Those
                // could not be mapped to a name, so they never reached _doorStateByName and the nav overlay never
                // saw them -- measured on MageFresh 2026-08-20: four doors reported states at 13:21:21 and the
                // BUILDDOOR naming h=20715 'Door02' did not arrive until 13:22:27, a 66-SECOND window in which the
                // bot had been told the door was closed and could not act on it. We DO record them by handle, so
                // prefer what we actually OBSERVED over bd.doorstate, which is only the state at spawn time.
                var seeded = _doorStates.TryGetValue(bd.handle, out var obs) ? obs.State : bd.doorstate;
                _doorStateByName[name] = seeded;
                _log?.Invoke($"[ZoneView] SCENARIO DOOR BUILD '{name}' h={bd.handle} state={seeded} ({(seeded == 0 ? "CLOSED" : "open")}) — seeded nav overlay"
                    + (seeded != bd.doorstate ? $" (from an EARLIER unnamed DOORSTATE; the BUILD said {bd.doorstate} and was stale)" : ""));
                DoorStatesByNameChanged?.Invoke(DoorStatesByName);
            }
        }
        else if (op == OpScenarioDoorState)
        {
            // A scenario corridor DOOR changed state (open/close)
            var b = pkt.ReadBody<PROTO_NC_SCENARIO_DOORSTATE_CMD>();
            uint? dx = null, dy = null;
            if (_npcs.TryGetValue(b.door, out var dn)) { dx = dn.X; dy = dn.Y; }
            else if (_doorStates.TryGetValue(b.door, out var prev)) { dx = prev.X; dy = prev.Y; } // keep last-known pos
            // "Doors opened nearby" (operator's metric list): count the 0->1 TRANSITION only, so a repeated state broadcast…
            var wasOpen = _doorStates.TryGetValue(b.door, out var prevState) && prevState.State != 0;
            if (b.doorstate != 0 && !wasOpen) MetricSink?.Invoke("doorsOpened", 1);
            _doorStates[b.door] = new DoorState(b.door, b.doorstate, dx, dy);
            // Update the by-NAME state (bridged via the BUILDDOOR handle→name map) → drives the nav overlay so a door that j…
            if (_doorNames.TryGetValue(b.door, out var dname))
            {
                _doorStateByName[dname] = b.doorstate;
                _log?.Invoke($"[ZoneView] SCENARIO DOOR '{dname}' h={b.door} state={b.doorstate} ({(b.doorstate == 0 ? "CLOSED" : "open")}) @({dx?.ToString() ?? "?"},{dy?.ToString() ?? "?"}) — nav overlay updated");
                DoorStatesByNameChanged?.Invoke(DoorStatesByName);
            }
            else
                // NOT LOST: _doorStates already holds it by handle, and the BUILDDOOR that names this handle will
                // adopt this state rather than its own spawn-time field. Until then the overlay cannot place it,
                // because the overlay is keyed by .sbi block NAME and we do not yet know which block this is.
                _log?.Invoke($"[ZoneView] SCENARIO DOOR h={b.door} state={b.doorstate} @({dx?.ToString() ?? "?"},{dy?.ToString() ?? "?"}) (name not yet known — no BUILDDOOR seen; held by handle, applied when it is named)");
        }
        else if (op == OpCharReviveOther)
        {
            if (Dead) _log?.Invoke("[ZoneView] revived (cross-server) — REVIVEOTHER not fully wired");
            Dead = false; DeadAtUtc = DateTime.MinValue;
            // TODO: REVIVEOTHER (0x1050) = revive on ANOTHER zone server (payload embeds a LOGIN_ACK + wm handle, like LINKO…
        }
        else if (op == OpActNpcMenuOpen)
        {
            NpcMenuOpen = true;
            // Payload = the NPC mobId that opened the menu
            var mp = pkt.Payload.Span;
            MenuNpcId = mp.Length >= 2 ? (ushort)(mp[0] | (mp[1] << 8)) : (ushort)0;
            _log?.Invoke($"[ZoneView] NPC menu opened (0x201C) npc={MenuNpcId} — awaiting select");
        }
        else if (op == OpSoulStoneHpBuyAck || op == OpSoulStoneSpBuyAck)
        {
            // BUY_ACK {totalnumber u16} = new reserve count + proof the buy took (only succeeds near a healer)
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                int total = p[0] | (p[1] << 8);
                if (op == OpSoulStoneHpBuyAck) { HpStones = total; if (total > 0) HpStoneDepleted = false; }
                else { SpStones = total; if (total > 0) SpStoneDepleted = false; }
                StonesChanged?.Invoke();
                _log?.Invoke($"[ZoneView] soul-stone {(op == OpSoulStoneHpBuyAck ? "HP" : "SP")} BUY ok — reserve now {total}");
            }
        }
        else if (op == OpSoulStoneBuyFail)
        {
            // NC_SOULSTONE_BUYFAIL_ACK {err u16} — the server REFUSED a stone buy
            var p = pkt.Payload.Span;
            LastStoneBuyFailErr = p.Length >= 2 ? (ushort)(p[0] | (p[1] << 8)) : (ushort)0;
            StoneBuyFailCount++;
            _log?.Invoke($"[ZoneView] soul-stone BUY FAILED (0x5005) err=0x{LastStoneBuyFailErr:X4} — server refused the buy");
        }
        else if (op == OpSoulStoneHpUseSuc || op == OpSoulStoneSpUseSuc)
        {
            // The reserve had a charge and it was spent (the HP/SP gain itself comes via HPCHANGE/SPCHANGE)
            PopStoneUseKind(); // keep the pending queue in sync
            if (op == OpSoulStoneHpUseSuc)
            {
                HpStoneDepleted = false;
                if (HpStones is { } n && n > 0) HpStones = n - 1;
                StonesChanged?.Invoke();   // a USE also restarts the cooldown, which the tile draws
                if (LastHpStoneSuccessUtc > DateTime.MinValue)
                {
                    var gapMs = (DateTime.UtcNow - LastHpStoneSuccessUtc).TotalMilliseconds;
                    if (gapMs > 250 && (_cdMinSuccessGapMs < 0 || gapMs < _cdMinSuccessGapMs))
                    {
                        _cdMinSuccessGapMs = gapMs;
                        ScalarLearned?.Invoke(ScalarStoneCooldownMs, gapMs);   // persist: min-gap converges from above
                        _logLevel?.Invoke(BotLogLevel.Note,
                            $"[heal] stone cooldown CEILING now {gapMs:F0}ms (healed twice that far apart) — " +
                            $"floor {(_cdMaxFailGapMs > 0 ? $"{_cdMaxFailGapMs:F0}ms" : "none yet")}, " +
                            $"using {HpStoneCooldownMs:F0}ms ⇒ {SustainableHealDps:F0} HP/s");
                    }
                }
                LastHpStoneSuccessUtc = DateTime.UtcNow;
                HpStoneFailsSinceSuccess = 0;
                // Arm the heal-amount measurement: the HP itself arrives in a following HPCHANGE
                _hpAtStoneUse = Hp.HasValue ? (int)Hp.Value : -1;
                _stoneHealPendingUntil = DateTime.UtcNow.AddMilliseconds(1500);
            }
            else
            {
                SpStoneDepleted = false;
                if (SpStones is { } n && n > 0) SpStones = n - 1;
                StonesChanged?.Invoke();
                LastSpStoneSuccessUtc = DateTime.UtcNow;
            }
        }
        else if (op == OpSoulStoneUseFail)
        {
            // USEFAIL (0x5006) is SHARED HP+SP and carries no marker — attribute it to the USE we actually fired (the pendin…
            bool? kind = PopStoneUseKind();
            // A USEFAIL is EITHER an empty reserve, OR the stone COOLDOWN, OR firing at full HP/SP (operator 2026-07-04: do…
            if (kind is null or true)
            {
                if (kind is not null)
                {
                    bool empty = HpStones is { } n && n <= 0;
                    if (empty && !HpStoneDepleted) _log?.Invoke("[ZoneView] HP soul-stone reserve EMPTY (0x5006 + count 0) — need restock");
                    HpStoneDepleted = empty;
                    if (!empty)
                    {
                        HpStoneFailsSinceSuccess++;
                        MetricSink?.Invoke("healsFailed", 1);
                        var sinceMs = LastHpStoneSuccessUtc > DateTime.MinValue
                            ? (DateTime.UtcNow - LastHpStoneSuccessUtc).TotalMilliseconds : -1;
                        // THE COOLDOWN'S PROVEN FLOOR
                        if (sinceMs > 0 && Hp is { } hpv && MaxHp is { } mx && mx > 0 && hpv < mx
                            && sinceMs > _cdMaxFailGapMs)
                        {
                            _cdMaxFailGapMs = sinceMs;
                            _logLevel?.Invoke(BotLogLevel.Note,
                                $"[heal] stone cooldown FLOOR now {sinceMs:F0}ms (use failed that long after a " +
                                $"success with {HpStones?.ToString() ?? "?"} charge(s) and HP {hpv}/{mx}) — " +
                                $"using {HpStoneCooldownMs:F0}ms ⇒ {SustainableHealDps:F0} HP/s");
                        }
                        _logLevel?.Invoke(BotLogLevel.Info,
                            $"[ZoneView] HP soul-stone USE FAILED (0x5006) — reserve has {HpStones?.ToString() ?? "?"} " +
                            $"charge(s), so this is the COOLDOWN ({sinceMs:F0}ms since last success, learned cd " +
                            $"{(HpStoneCooldownMs < 0 ? "unknown" : $"{HpStoneCooldownMs:F0}ms")}); " +
                            $"{HpStoneFailsSinceSuccess} failed heal(s) in a row — WE ARE NOT HEALING");
                    }
                    else HpStoneFailsSinceSuccess = 0;
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
            // [itemnum u16][npc u16][MENUITEM × itemnum]
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
        else if (op == OpStorageOpen)
        {
            // NC_MENU_OPENSTORAGE_CMD (0x3C08) — the personal storage/warehouse opened
            var p = pkt.Payload.Span;
            if (p.Length >= 12)
            {
                StorageCen = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(0, 8));
                StorageMaxPage = p[8];
                StoragePage = p[9];
                var openType = p[10];
                int count = p[11];
                var items = new List<(byte Slot, ushort ItemId)>(count);
                var boxesSeen = new HashSet<byte>();
                var off = 12;
                for (var i = 0; i < count && off + 3 <= p.Length; i++)
                {
                    var datasize = p[off];
                    var loc = (ushort)(p[off + 1] | (p[off + 2] << 8));
                    var box = (byte)(loc >> 10);
                    var slot = (byte)(loc & 0xFF);
                    // itemId is the first field of SHINE_ITEM_STRUCT, immediately after `location`
                    var itemId = off + 5 <= p.Length ? (ushort)(p[off + 3] | (p[off + 4] << 8)) : (ushort)0;
                    boxesSeen.Add(box);
                    items.Add((slot, itemId));
                    if (datasize == 0) break;           // malformed; don't spin
                    off += datasize;
                }
                // Adopt an observed container ONLY when every item agrees on it
                if (boxesSeen.Count == 1)
                {
                    var only = boxesSeen.First();
                    if (only != StorageBox)
                    {
                        _log?.Invoke($"[ZoneView] STORAGE container is box {only} (was {StorageBox}) — all {count} item(s) agree, adopting");
                        StorageBox = only;
                    }
                }
                else if (boxesSeen.Count > 1)
                {
                    _log?.Invoke($"[ZoneView] STORAGE location high-bits DISAGREE across items ({string.Join(",", boxesSeen.OrderBy(b => b))}) " +
                                 $"— NOT a container id; keeping box {StorageBox}. Storage is paged ({StorageMaxPage} pages), so these are " +
                                 "likely page numbers — decode the page model (tickets.md) rather than treating them as boxes.");
                }
                _storageItems = items.ToArray();
                StorageOpenUtc = DateTime.UtcNow;
                // Classify the NPC we just clicked as the STORAGE keeper, so discovery finds it by ROLE and persists that (npcKi…
                LastShopKind = ShopKind.Storage;
                ShopOpenUtc = DateTime.UtcNow;   // a storage session counts as "a menu opened" for the probe
                _log?.Invoke($"[ZoneView] STORAGE opened (0x3C08): {count} item(s), page {StoragePage}/{StorageMaxPage}, " +
                             $"cen={StorageCen}, openType={openType}, box={(StorageBox < 0 ? "UNKNOWN (storage empty)" : StorageBox.ToString())}" +
                             (items.Count > 0 ? " — " + string.Join(",", items.Select(it => $"slot{it.Slot}=item{it.ItemId}")) : ""));
                StorageOpened?.Invoke(_storageItems);
            }
        }
        else if (op == OpStorageOpenFail)
        {
            // NC_MENU_OPENSTORAGE_FAIL_CMD (0x3C07)
            var p = pkt.Payload.Span;
            var err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : (p.Length == 1 ? p[0] : -1);
            StorageOpenUtc = null;
            _log?.Invoke($"[ZoneView] CRUTCH[CRIT] STORAGE OPEN FAILED (0x3C07) err={err} " +
                         $"({p.Length}b: {Convert.ToHexString(p.Length > 8 ? p.Slice(0, 8) : p)})");
        }
        else if (op == OpShopOpenSoulStone)
        {
            // Soul-stone shop opened — a real shop session (buys soul stones AND accepts item sells)
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
                if (hpRestore > 0) HpStoneRestore = hpRestore;
                if (spRestore > 0) SpStoneRestore = spRestore;
                _log?.Invoke($"[ZoneView] soul-stone shop opened (0x3C05) — HP restore {hpRestore} max {hpMax} @{HpStonePrice}cen, SP restore {spRestore} max {spMax} @{SpStonePrice}cen");
            }
            else _log?.Invoke("[ZoneView] soul-stone shop opened (0x3C05) — sells accepted (no menu payload)");
            ShopOpenUtc = DateTime.UtcNow;
            LastShopKind = ShopKind.SoulStone;
        }
        else if (op == OpMenuRandomOption)
        {
            // 0x3C0E NC_MENU_RANDOMOPTION_CMD — a NON-shop NPC menu (the RouN Anvil: reforge/reroll item stats, needs a Hamm…
            RandomOptionUtc = DateTime.UtcNow;
            _log?.Invoke("[ZoneView] NPC RandomOption menu (0x3C0E) — NOT a shop (e.g. Anvil reforge)");
        }
        else if (op == OpCenChange)
        {
            // {cen u64} = the new money total
            var p = pkt.Payload.Span;
            if (p.Length >= 8)
            {
                var cen = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(p);
                if (cen != Money)
                {
                    var delta = Money < 0 ? 0 : cen - Money;  // first seed (Money==-1) isn't a real delta
                    if (delta != 0) MetricSink?.Invoke("moneyDelta", delta);
                    var line = Money < 0
                        ? $"[money] seed {cen}"
                        : $"[money] {(delta >= 0 ? "+" : "")}{delta} -> {cen} (was {Money})";
                    if (_logLevel is not null) _logLevel(BotLogLevel.Info, $"[ZoneView] {line}");
                    else _log?.Invoke($"[ZoneView] {line}");
                }
                Money = cen;
                MoneyChanged?.Invoke((long)cen);
            }
        }
        else if (op == OpSellAck)
        {
            // 2-byte result code for our SELL_REQ (no PDB struct)
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                LastSellAck = p[0] | (p[1] << 8);
                LastSellAckUtc = DateTime.UtcNow;
                // A reject (not 0x0381) usually means the shop isn't really open — drop the open signal so the driver re-opens c…
                if (LastSellAck != 0x0381) ShopOpenUtc = default;
                else BagFull = false;   // a successful sell freed a bag slot — clear the full flag
                _log?.Invoke($"[ZoneView] SELL_ACK 0x{LastSellAck:X4}{(LastSellAck == 0x0381 ? " (OK)" : " (rejected)")}");
            }
        }
        else if (op == OpItemBuyAck)
        {
            // 2-byte result code for our BUY_REQ
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
            // Keep a tracked player's position current as they move (chase the destination they're heading to)
            var p = pkt.Payload.Span;
            if (p.Length >= 18)
            {
                var hnd = (ushort)(p[0] | (p[1] << 8));
                var frX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(2, 4));
                var frY = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(6, 4));
                var toX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(10, 4));
                var toY = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(14, 4));
                var rawSpeed = p.Length >= 20 ? (ushort)(p[18] | (p[19] << 8)) : (ushort)0;
                if (rawSpeed > 0)
                    _entityMove[hnd] = (frX, frY, toX, toY, rawSpeed * SpeedRawToUPerSec, DateTime.UtcNow);
                if (_nearby.TryGetValue(hnd, out var pl))
                {
                    _nearby[hnd] = pl with { X = toX, Y = toY };
                    NoteEntityChanged(hnd);
                }
                else if (_npcs.TryGetValue(hnd, out var npc))
                {
                    // Keep mob positions live as they move
                    var (ox, oy) = (npc.X, npc.Y);
                    _npcs[hnd] = npc with { X = toX, Y = toY };
                    NoteEntityChanged(hnd);
                    // WALK (0x2018) = idle wander → the mob is still around home, so let the anchor follow it
                    NoteMobAnchor(hnd, toX, toY, idle: op == OpSomeoneMoveWalk);
                    if (op == OpSomeoneMoveRun && _aggressors.ContainsKey(hnd)
                        && _mobAnchor.TryGetValue(hnd, out var anc) && anc.IdleConfirmed)
                    {
                        var away = Math.Sqrt(Math.Pow((double)toX - anc.X, 2) + Math.Pow((double)toY - anc.Y, 2));
                        var prev = _mobChase.TryGetValue(npc.MobId, out var pv) ? pv : 0;
                        _mobChase.AddOrUpdate(npc.MobId, away, (_, old) => away > old ? away : old);
                        if (prev <= 0 || away > prev * 1.25)
                            _log?.Invoke($"[leash] mob {npc.MobId} (h={hnd}) chased {away:F0}u from its spawn " +
                                         $"(prev max {prev:F0}u) — learned chase limit now {away:F0}u");
                    }
                    // A player-side mob (town guard) running near us isn't aggro — skip it
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
                                var aggroNow = DateTime.UtcNow;
                                bool wasAggro = _aggressors.TryGetValue(hnd, out var prevAggroAt)
                                                && aggroNow - prevAggroAt < CombatWindow;
                                _aggressors[hnd] = aggroNow;
                                FreezeMobAnchor(hnd);             // chasing → freeze its spawn anchor
                                LastHitAtUtc = aggroNow;          // charging at me -> in combat
                                var aggroMsg = $"[ZoneView] mob {npc.MobId} (h={hnd}) running at us — AGGRO";
                                if (_logLevel is not null)
                                    _logLevel(wasAggro ? BotLogLevel.Verbose : BotLogLevel.Note, aggroMsg);
                                else if (!wasAggro) _log?.Invoke(aggroMsg);
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
                _npcs.Clear(); _recentNpcs.Clear(); _npcSeed.Clear(); _npcSeedAll.Clear(); _mobAnchor.Clear();  // entities are per-map; the new map re-broadcasts
                // TELEPORTING DROPS THE SERVER-SIDE SELECTION (operator 2026-08-13: "Teleportation in general untargets" — and s…
                TargetInvalidated?.Invoke("teleported — the server drops the selection on a teleport");
                _nearby.Clear();
                lock (_scenarioFightable) _scenarioFightable.Clear();   // handles are per-map — see the revive path
                // AND EVERY PER-HANDLE TABLE, FOR THE SAME REASON
                _handleRange.Clear();
                _handleHits.Clear();
                _entityMove.Clear();      // in-flight moves belong to the map we just left
                _castIndexSkill.Clear(); while (_castIndexOrder.TryDequeue(out _)) { }
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
            // NC_QUEST_NOTIFY_MOB_KILL_CMD (Quest dept, cmd 13): the server's authoritative per-kill quest credit — [NumOfAc…
            var p = pkt.Payload.Span;
            if (p.Length >= 1)
            {
                int n = p[0];
                for (int i = 0; i < n && 1 + i * 4 + 4 <= p.Length; i++)
                {
                    // MobOfQuest = {u16 objIdx, u16 questId}
                    int objIdx = p[1 + i * 4] | (p[1 + i * 4 + 1] << 8);
                    int qid = p[1 + i * 4 + 2] | (p[1 + i * 4 + 3] << 8);
                    _questProgress.AddOrUpdate(qid, 1, (_, v) => v + 1);
                    _questObjProgress.AddOrUpdate((qid << 16) | (objIdx & 0xFFFF), 1, (_, v) => v + 1);
                    _log?.Invoke($"[ZoneView] QUEST_MOB_KILL quest={QName(qid)} credited (total {_questProgress[qid]})");
                    // The kill that actually COUNTED
                    MetricSink?.Invoke("questMobKills", 1);
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
            // Abandon confirmed — drop the quest from the active view (and its progress) so the driver sees it as no-longer-…
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                int qid = p[0] | (p[1] << 8);
                // PROTO_NC_QUEST_GIVE_UP_ACK {nQuestID u16, ErrorCode u16}. A SHORT frame is a decode gap, not a
                // success: without the code we cannot tell "abandoned" from "refused", so treat it as refused and say so.
                int err = p.Length >= 4 ? (p[2] | (p[3] << 8)) : -1;
                if (err == 0)
                {
                    LastGiveUpResult = (qid, 0);
                    _activeQuests.TryRemove(qid, out _);
                    _questProgress.TryRemove(qid, out _);
                    for (var oi = 0; oi < 5; oi++) _questObjProgress.TryRemove((qid << 16) | oi, out _);
                    _log?.Invoke($"[ZoneView] QUEST_GIVE_UP_ACK quest={QName(qid)} ACCEPTED - abandoned, removed from active");
                }
                else
                {
                    // The server REFUSED the abandon. Dropping it from _activeQuests here (which is what this did for
                    // months) would make the driver believe a quest it still holds is gone: it would stop working it,
                    // stop handing it in, and free a log slot that is not free - so every later accept fails at the cap
                    // with no visible cause. The refusal is the answer; keep the quest and report the code.
                    LastGiveUpResult = (qid, err);
                    _log?.Invoke($"[ZoneView] CRUTCH[CRIT] QUEST_GIVE_UP_ACK quest={QName(qid)} REFUSED err={err}"
                        + (p.Length >= 4 ? "" : $" (SHORT frame, {p.Length}B - no ErrorCode field; DECODE GAP)")
                        + " - the quest is STILL HELD and still occupies a log slot");
                }
            }
        }
        else if (op == OpQuestStartAck)
        {
            // NC_QUEST_START_ACK {err u16} — the result of our last START_REQ
            var p = pkt.Payload.Span;
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : -1;
            RecordQuestAcceptResult(_lastStartReqQuestId, err);
        }
        else if (op == OpQuestSelectStartAck)
        {
            // NC_QUEST_SELECT_START_ACK {nNPCID u16, nQuestID u16, ErrorType u16} — result of a menu-driven SELECT_START
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
            // NC_QUEST_ERR — generic quest error push (layout not in the PDB)
            var p = pkt.Payload.Span;
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : (p.Length == 1 ? p[0] : -1);
            _log?.Invoke($"[ZoneView] QUEST_ERR raw=[{Convert.ToHexString(p)}] (lastStartReq={_lastStartReqQuestId})");
            if (_lastStartReqQuestId >= 0) RecordQuestAcceptResult(_lastStartReqQuestId, err == 0 ? -2 : err);
        }
        else if (op == OpClientItem)
        {
            // Full bag snapshot at login (one frame per box)
            try
            {
                // Hand-parse (like ZoneEntry) to read box + per-item stack count, which the typed struct doesn't expose: [num u8…
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
                        // ITEM ID 0 IS THE REAL ITEM "Leather Boots" — SeedItems 1500 lines up says exactly that.
                        // The empty marker on this wire is 0xFFFF (the CELLCHANGE handler gets it right), and the
                        // login list only sends OCCUPIED slots anyway. Dropping id 0 made that slot invisible:
                        // BagFreeSlots over-reported, the sell/declutter classifier never saw the item, and
                        // AutoLootBehavior's free-slot gate was wrong until a CELLCHANGE happened to touch it.
            if (itemId != EmptyCellItemId) { _inventory[slot] = itemId; _invCount[slot] = count; }
                        off += 1 + datasize;
                    }
                }
            }
            catch { /* skip unparseable inventory frame */ }
        }
        else if (op == OpCellChange)
        {
            // [exchange:2][location:2][itemid:2][attr…] — a slot gained/changed an item
            CellChangeCount++;
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var location = (ushort)(p[2] | (p[3] << 8));
                if (BoxOf(location) == MainBag)
                {
                    var slot = (byte)(location & 0xFF);
                    var itemId = (ushort)(p[4] | (p[5] << 8));
                    // EMPTY IS 0xFFFF, NOT 0 (operator 2026-08-13: "sometimes randomly 2 items change to item 65535 x1
                    if (itemId != EmptyCellItemId)
                    {
                        _inventory[slot] = itemId;
                        // stack count = the lot after itemid: len 7 = byte-lot, len 8 = word-lot, bigger = gear/complex (count 1)
                        _invCount[slot] = p.Length == 7 ? p[6]
                                        : p.Length == 8 ? (p[6] | (p[7] << 8)) : 1;
                    }
                    else { _inventory.TryRemove(slot, out _); _invCount.TryRemove(slot, out _); }
                }
            }
        }
        else if (op == OpEquipChange)
        {
            // [exchange:2][location:1][itemid:2…] — item moved bag→equip slot
            var p = pkt.Payload.Span;
            if (p.Length >= 1) _inventory.TryRemove(p[0], out _);   // vacate bag slot
            if (p.Length >= 5)
            {
                var equipSlot = p[2];
                var itemId = (ushort)(p[3] | (p[4] << 8));
                // Same rule: id 0 is a real item, 0xFFFF is the empty marker. Treating "equipped item 0" as UNequipping
            // left the character reported as bare in that slot for the rest of the session.
            if (itemId != EmptyCellItemId) _equipment[equipSlot] = itemId;
            else _equipment.TryRemove(equipSlot, out _);
            }
        }
        else if (op == OpDropedItem)
        {
            // An item hit the ground (mob death / player drop)
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
            // Universal "this handle left view": for a ground item it was picked (by anyone) or despawned; for a char/mob it…
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
            NoteEntityGone(hnd);
        }
        else if (op == OpPickAck)
        {
            PickPending = false;
            try
            {
                var a = pkt.ReadBody<PROTO_NC_ITEM_PICK_ACK>();
                var r = new PickResult(a.itemid, a.lot, a.error);
                LastPickResult = r;
                // Inventory-full (0x346, itemid 0xFFFF) → flag a full bag so the driver sells/declutters instead of pacing over…
                if (r.Error == PickInventoryFull) { if (!BagFull) _log?.Invoke("[ZoneView] BAG FULL (pick ack 0x346) — needs a sell/declutter trip"); BagFull = true; }
                else if (r.ItemId != 0xFFFF) BagFull = false;
                // 0x341 is the SUCCESS code (the bag gained the item — confirmed in KillAndPickupItems.pcapng), 0x346 is bag-ful…
                if (r.Error == PickSuccess) MetricSink?.Invoke("itemsPickedUp", r.Lot > 0 ? r.Lot : 1);
                else MetricSink?.Invoke("pickupFails", 1);
                var pickStatus = r.Error switch { PickSuccess => "OK", PickInventoryFull => "BAG FULL", _ => $"0x{r.Error:X}" };
                _log?.Invoke($"[ZoneView] pick ack: item {r.ItemId} lot {r.Lot} -> {pickStatus}");
                PickedUp?.Invoke(r);
            }
            catch { /* skip an unparseable pick ack */ }
        }
        else if (op == OpSortAck)
        {
            // Result of the bot's inventory auto-sort (0x304A)
            try
            {
                var a = pkt.ReadBody<PROTO_NC_ITEM_AUTO_ARRANGE_INVEN_ACK>();
                _log?.Invoke($"[ZoneView] inventory auto-sorted (ack 0x304B err=0x{a.err:X})");
            }
            catch { /* skip an unparseable sort ack */ }
        }
        else if (op == OpClientSkill)
        {
            // Learned-skill list at zone login: header then `number` × 12-byte blocks, each leading with the skill id (u16)
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
                    // id 0 is a REAL skill
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
            // Login PASSIVE-skill list (0x103E): {number u16 @0, passive u16[number] @2}
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
                    // id 0 is a REAL passive ("Bravery Mastery [01]"/BraveMastery01) — the `number` field already bounds the loop, s…
                    if (_passives.TryAdd(pid, 1)) added++;
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
            // NC_SKILL_SKILL_LEARNSUC_CMD (0x4804): the server CONFIRMS a skill was learned
            var p = pkt.Payload.Span;
            if (p.Length >= 2)
            {
                var skillId = (ushort)(p[0] | (p[1] << 8));
                var lvl = p.Length >= 3 ? p[2] : (byte)0;
                var passive = false;
                if (LastUseAckItem >= 0 && ScrollSkillResolver is { } resolve)
                {
                    var (bookSkill, bookPassive) = resolve(LastUseAckItem);
                    if (bookSkill == skillId) passive = bookPassive;
                }
                var set = passive ? _passives : _skills;
                if (set.TryAdd(skillId, 1))
                {
                    _log?.Invoke($"[ZoneView] {(passive ? "PASSIVE" : "SKILL")} LEARNED: id={skillId} lv{lvl} " +
                                 $"(now know {_skills.Count} active / {_passives.Count} passive)");
                    SkillsChanged?.Invoke();
                    SkillLearned?.Invoke(skillId, lvl, passive);
                }
            }
        }
        else if (op == OpSkillLearnFail)
        {
            // NC_SKILL_SKILL_LEARNFAIL_CMD: the server REJECTED the scroll-learn
            var p = pkt.Payload.Span;
            var hex = Convert.ToHexString(p.Length > 8 ? p.Slice(0, 8) : p);
            int err = p.Length >= 2 ? (p[0] | (p[1] << 8)) : (p.Length == 1 ? p[0] : -1);
            _log?.Invoke($"[ZoneView] SKILL LEARN FAILED — err={err} ({p.Length}b: {hex})");
        }
        else if (op == OpItemUseAck)
        {
            // NC_ITEM_USE_ACK {error u16 @0, useditem u16 @2, invenType u8 @4}
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
                    // Seen live only on CRAFTING RECIPE books, whose requirement is in Produce.shn (NeededMasteryType = the job, Nee…
                    0x717 => "FAIL: refused (crafting recipe — job / job-points not met)",
                    _ => $"err 0x{err:X}",
                };
                if (err == 0x700) _useFails.TryRemove(item, out _);
                else _useFails.AddOrUpdate(item, 1, (_, n) => n + 1);
                if (err != 0x700)
                    _log?.Invoke($"[ZoneView] item USE item={item} -> {meaning} (0x{err:X}) " +
                                 $"— consecutive refusals: {ItemUseFailCount(item)}");
            }
        }
        else if (op == OpQuestScriptReq)
        {
            // Server quest-dialogue step: [questId u16][STRUCT_QSC...] — QSC command code is the first STRUCT_QSC byte (payl…
            var p = pkt.Payload.Span;
            if (p.Length >= 3)
            {
                var questId = (ushort)(p[0] | (p[1] << 8));
                var qsc = p[2];
                // STRUCT_QSC: Cmd(u32)@2, IsPigeonStartType@6, Data@7
                int dialogId = p.Length >= 11 ? (p[7] | (p[8] << 8) | (p[9] << 16) | (p[10] << 24)) : 0;
                var step = new QuestStep(questId, qsc, dialogId);
                PendingQuest = step;
                _questScript.Enqueue(step);  // queue every page so a burst isn't collapsed to just the last
                // Keep the active/done view current as the script runs: Cmd 0x06 = ACCEPT (quest becomes active), Cmd 0x0A = DON…
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
        else if (op == OpStatRemainPoint)
        {
            // NC_CHAR_STAT_REMAINPOINT_CMD {byte remain} — unspent stat points
            var p = pkt.Payload.Span;
            if (p.Length >= 1) { FreeStatPoints = p[0]; _log?.Invoke($"[ZoneView] STAT remain points = {FreeStatPoints}"); }
        }
        else if (op == OpStatIncSuc)
        {
            // NC_CHAR_STAT_INCPOINTSUC_ACK {byte stat} — a point was added to `stat`
            var p = pkt.Payload.Span;
            byte stat = p.Length >= 1 ? p[0] : (byte)0xFF;
            if (FreeStatPoints > 0) FreeStatPoints--;
            _log?.Invoke($"[ZoneView] STAT +1 {StatName(stat)} (byte {stat}) — remain now {FreeStatPoints}");
        }
        else if (op == OpStatIncFail)
        {
            var p = pkt.Payload.Span;
            int err = p.Length >= 2 ? (p[^2] | (p[^1] << 8)) : -1;
            _log?.Invoke($"[ZoneView] STAT inc FAIL err=0x{err:X4} (remain {FreeStatPoints})");
        }
    }

    private void AddOrUpdate(PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD c)
    {
        var name = FiestaText.Decode(c.charid.n5_name);
        var player = new NearbyPlayer(c.handle, name, c.chrclass, c.Level, c.coord.xy.x, c.coord.xy.y,
            c.mode, c.type, c.nKQTeamType);
        var isNew = !_nearby.ContainsKey(c.handle);
        _nearby[c.handle] = player;
        NoteEntityChanged(c.handle);
        if (isNew)
        {
            // type / nKQTeamType distinguish a real player from a scenario/KQ enemy "character" (the JCQ promotion "shadow"…
            LogV($"[ZoneView] player appeared: {name} (h={c.handle} class={c.chrclass} lvl={c.Level} mode={c.mode} type={c.type} kqTeam={c.nKQTeamType})");
            PlayerAppeared?.Invoke(player);
        }
    }

    // REGENMOB record layout (fixed 149 bytes — verified against Full.pcapng): handle u16 | mode u8 | mobid u16 | x…
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
        var dir = p[off + 13];   // SHINE_COORD_TYPE.dir — see NearbyNpc.Dir
        var flag = p[off + 14];
        string? linkMap = null;
        if (flag == 1) // gate: the flag blob begins with the null-terminated dest-map name
            linkMap = ReadCString(p, off + FlagBlobOffset, 32);
        // nKQTeamType (record offset 147) — faction/team byte; tells allies (guards) from enemies
        var team = (off + MobTeamOffset < p.Length) ? p[off + MobTeamOffset] : (byte)0;

        var npc = new NearbyNpc(handle, mobid, mode, x, y, flag, linkMap, team, dir);
        var isNew = !_npcs.ContainsKey(handle);
        _npcs[handle] = npc;
        NoteEntityChanged(handle);
        // First sighting of this handle = it's standing where it lives → seed its spawn anchor (see _mobAnchor)
        if (flag != 1) NoteMobAnchor(handle, x, y, idle: isNew);
        _recentNpcs.TryRemove(handle, out _); // back in view (live) → drop the sticky flicker-bridge copy
        // THE SEED: record every NPC/gate by mobId (the bulk 0x1C09 on map-enter populates this fully)
        var seedEntry = new NpcSeedEntry(mobid, x, y, flag == 1, linkMap);
        _npcSeed[mobid] = seedEntry;
        _npcSeedAll[(mobid, x, y)] = seedEntry;
        if (isNew)
        {
            // Gates keep a line each: they are rare and navigationally load-bearing
            if (flag == 1) LogV($"[ZoneView] gate appeared: id={mobid} h={handle} @({x},{y}) -> {linkMap}");
            else NoteAoiChurn(entered: true);
        }
    }

    // Conversion: 127 raw units (human runspeed from 0x203E capture) ≈ 120 u/s
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

    /// <summary>Cosine of the angle between vectors (ax,ay) and (bx,by) — 1 = same direction</summary>
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
