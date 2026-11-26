using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Fiesta.Bot.Net;

namespace Fiesta.Bot.Protocol;

/// <summary>Kinds of decoded inbound events applied to the world state. Field meaning per kind is documented on each
/// value (H = Handle, H2 = Handle2).</summary>
public enum InboundKind : ushort
{
    /// <summary>Not decoded.</summary>
    Unknown,
    /// <summary>H appeared. A = EntityKind bits (Mob/Npc/Player/Gate/Drop), B = mob id / item id (-1), C = level (-1), X,Y,
    /// H2 = dropping mob (drops), Text = player name / gate destination map.</summary>
    EntityAppeared,
    /// <summary>H left view.</summary>
    EntityLeft,
    /// <summary>H moves from (A,B) to (X,Y) at raw speed C; H2 = 1 run, 0 walk.</summary>
    EntityMoved,
    /// <summary>H stopped at (X,Y).</summary>
    EntityStopped,
    /// <summary>H hp A / max B, level C (-1 unknown).</summary>
    EntityHp,
    /// <summary>H died, killed by H2.</summary>
    EntityDied,
    /// <summary>H targets H2 (a selection broadcast; for us, the server-confirmed selection).</summary>
    EntityTarget,
    /// <summary>H changed mode A.</summary>
    EntityMode,
    /// <summary>Our HP = A.</summary>
    SelfHp,
    /// <summary>Our SP = A.</summary>
    SelfSp,
    /// <summary>Our max HP A / max SP B (parameter change).</summary>
    SelfMaxHpSp,
    /// <summary>A character stat: A stat id (0 Str, 1 End, 2 Dex, 3 Int, 5 Spr, 6/7 WC min/max, 8 defence, 9 aim, 10 evasion,
    /// 11/12 MA min/max, 13 magic defence, 14 magic aim, 15 magic evasion; 0x20 fame, 0x21 max HP stones, 0x22 max SP stones),
    /// B value.</summary>
    SelfStat,
    /// <summary>Character base: A level, B exp, C money, X hp stones, Y sp stones, Text = login map name.</summary>
    SelfBase,
    /// <summary>Level up to A (H = mob whose kill did it); B = prev exp, C = next exp of the new level.</summary>
    SelfLevel,
    /// <summary>Exp gained A (H = mob).</summary>
    SelfExp,
    /// <summary>Money now A.</summary>
    SelfMoney,
    /// <summary>Walk speed A / run speed B (raw).</summary>
    SelfSpeed,
    /// <summary>Abstate A on H set (B = 1) or reset (B = 0), C = ms left (-1 unknown).</summary>
    SelfAbstate,
    /// <summary>MAP_LOGIN_ACK: H = our handle, X,Y spawn, A max HP, B max SP, C next exp; H2 = prev exp (low 31 bits).</summary>
    MapLoginAck,
    /// <summary>MAP_LOGINFAIL: A = error, B = wrong data file index.</summary>
    MapLoginFail,
    /// <summary>Exp band of our level: A = prev exp, B = next exp.</summary>
    SelfExpBand,
    /// <summary>A swing (or a damage-over-time tick) landed: H attacker (-1 for a DoT), H2 defender, A damage (-1 when
    /// the packet carries none), B defender HP left, C flags.</summary>
    Damage,
    /// <summary>A skill hit: H caster, H2 target, A damage, B target HP left, C cast index.</summary>
    SkillDamage,
    /// <summary>Our cast started: A skill, H2 target, B cast index.</summary>
    SkillCastStart,
    /// <summary>Skill result (unused placeholder for aggregate results).</summary>
    SkillResult,
    /// <summary>Our cast refused: A = code.</summary>
    CastFail,
    /// <summary>Our cast accepted (cast bar started).</summary>
    CastSucceeded,
    /// <summary>Our cast cut / aborted: A = 1 abort, 0 cut.</summary>
    CastAborted,
    /// <summary>Someone (H) stopped bashing (cease fire).</summary>
    BashStop,
    /// <summary>A swing started: H attacker, H2 defender.</summary>
    SwingStart,
    /// <summary>Our move refused; (X,Y) = where the game has us.</summary>
    MoveFail,
    /// <summary>Bag/equip cell: A box, B slot, C item id (-1 empty), X lot.</summary>
    ItemCell,
    /// <summary>Item use result: A error code, B item id.</summary>
    ItemUseResult,
    /// <summary>Buy (B = 0) / sell (B = 1) ack: A code.</summary>
    BuySellAck,
    /// <summary>Soul stone reserve: A = 0 hp / 1 sp, B = count; C = 1 when from a buy ack.</summary>
    StoneReserve,
    /// <summary>Soul stone use result: A = 0 hp / 1 sp / 2 fail, B = code.</summary>
    StoneUse,
    /// <summary>Pick up result: A item, B lot, C error, H item handle.</summary>
    PickAck,
    /// <summary>Learned skill A, B = cooldown left ms.</summary>
    SkillLearned,
    /// <summary>Quest A in the log with status B, objective counts packed in C (5 bytes).</summary>
    QuestProgress,
    /// <summary>Quest A done (completed list).</summary>
    QuestState,
    /// <summary>Quest script step: A quest, B command, C data (SAY: dialog id), H2 npc mob id, X talker type.</summary>
    QuestDialog,
    /// <summary>A kill counted for quest A objective B.</summary>
    QuestMobKill,
    /// <summary>Quest A is available (readable) now.</summary>
    QuestAvailable,
    /// <summary>Quest error / start ack: A = opcode, B = code, C = quest (-1).</summary>
    QuestAck,
    /// <summary>The zone asks the client to open NPC A's menu (mob id).</summary>
    NpcMenu,
    /// <summary>Server menu: H = npc handle, A = option count, Text = title, Options = replies.</summary>
    ServerMenu,
    /// <summary>Shop list header: H npc handle, A = opcode, B = item count (ShopItem events follow).</summary>
    ShopList,
    /// <summary>One shop item: A slot, B item id.</summary>
    ShopItem,
    /// <summary>Party event (raw opcode in A).</summary>
    PartyEvent,
    /// <summary>A party invitation (world manager JOINPROPOSE_REQ): Text = the inviting party master.</summary>
    PartyProposal,
    /// <summary>A friend request (world manager FRIEND_SET_CONFIRM_REQ): Text = "charid\tfriendid" as sent.</summary>
    FriendProposal,
    /// <summary>Player trade (dept 19): A = command (opcode &amp; 0x3FF), B / C / X per command (see TradeDecoder).</summary>
    TradeEvent,
    /// <summary>Chat: H speaker, Text.</summary>
    Chat,
    /// <summary>Whisper: Text = "name\ntext".</summary>
    Whisper,
    /// <summary>Map link: A map id, X,Y, B = 1 other server.</summary>
    MapLink,
    /// <summary>Revived on map A at (X,Y).</summary>
    Revive,
    /// <summary>Death menu: A seconds.</summary>
    DeadMenu,
    /// <summary>Door state (raw).</summary>
    Door,
    /// <summary>Tent ack (raw opcode A).</summary>
    TentAck,
    /// <summary>A job finished: A ticket, B ok (1/0).</summary>
    JobCompleted,
    /// <summary>The zone link changed state: A = LinkState.</summary>
    LinkChanged,
    /// <summary>Mount: A = 1 on, 0 off; H = mount handle.</summary>
    Mount,
    /// <summary>Stat points left A.</summary>
    StatPoints,
    /// <summary>The soul stone merchant's window (SHOPOPENSOULSTONE): A HP stone price, B HP stone cap, C SP stone price,
    /// X SP stone cap.</summary>
    StoneShop,
    /// <summary>A stat point spent (CHAR_STAT_INCPOINTSUC_ACK): A stat id (0 Str, 1 End, 2 Dex, 3 Int, 4 Spr).</summary>
    StatPointSpent,
    /// <summary>A stat point refused (CHAR_STAT_INCPOINTFAIL_ACK 0x1061): A error code.</summary>
    StatPointRefused,
    /// <summary>The storage keeper's window (MENU_OPENSTORAGE_CMD): A money held in storage, B pages, C current page,
    /// X item count. The items follow as <see cref="ItemCell"/> events of the storage box, after a clear of that box.</summary>
    StorageList,
    /// <summary>The storage window was refused (MENU_OPENSTORAGE_FAIL_CMD): A error code.</summary>
    StorageFail,
    /// <summary>The answer to an item move between boxes (ITEM_RELOC_ACK): A code (0x0241 moved).</summary>
    RelocAck,
    /// <summary>Production field chosen (SKILL_PRODUCTFIELD_ACK): A main skill, B code (0x0B01 ok).</summary>
    ProduceField,
    /// <summary>A production made (ACT_PRODUCE_MAKE_CMD): A item made, B code (0x0B01 ok).</summary>
    ProduceMade,
    /// <summary>A production cast refused (ACT_PRODUCE_CAST_FAIL_ACK): A code.</summary>
    ProduceFail,
    /// <summary>A skill's mastery total (SKILL_SKILLEXP_CLIENT_CMD): A skill, B mastery.</summary>
    SkillExp,
    /// <summary>The map we stand on is a player-fight area (MAP_FREEPKZONE_ON / PARTYBATTLEZONE_ON) or no longer one
    /// (the OFF commands): A = 1 on / 0 off, B = 2 free player kill, 1 party battle.</summary>
    PvpZone,
    /// <summary>A stack split answered (ITEM_SPLIT_ACK): A code (0x0441 split; captured after the two cell changes).</summary>
    SplitAck,
    /// <summary>The bag's auto-arrange answered (ITEM_AUTO_ARRANGE_INVEN_ACK): A code (0x30A8 = done, as the real client's
    /// own sort receives it).</summary>
    SortAck,
    /// <summary>ITEM_UPGRADE_ACK {err u16}: A = the result code (0x08C3 success, 0x08C5 failed, 0x08C6 failed -1, 0x08C7 destroyed, 0x08C9 refused).</summary>
    UpgradeAck,
    /// <summary>BAT_EXPLOST {explost u32}: exp a death took (A).</summary>
    ExpLost,
    /// <summary>Instance scenario (dept 27) and doors: A = scenario command (5 area entry asked, 7 area leave asked,
    /// 9 door state, 11 object type change, 22 timer, 33 timer start, 34 timer end, 4 notice, 27 system message; 1000 =
    /// BUILDDOOR), Text = area name / message, Handle = door / object, B = door state / object type / seconds / notice.</summary>
    Scenario,
    /// <summary>The job change is done (CHAR_PROMOTE_ACK): A the new class id.</summary>
    Promoted,
    /// <summary>A skill could not be learned (SKILL_LEARNFAIL): A code.</summary>
    SkillLearnFail,
    /// <summary>What kind of map we just entered (MAP_FIELD_ATTRIBUTE, sent on every map entry): A eFieldMapType
    /// (0 the open world; 1 on entering the job change's instance, Z:/JCQ.pcapng).</summary>
    FieldAttribute,
    /// <summary>MAP_CAN_USE_REVIVEITEM (sent on every map entry): A 1 revive items allowed, 0 not (0 in the job change's
    /// instance).</summary>
    ReviveItemAllowed,
    /// <summary>The rest tent (the client's "home" button): A 1 pitch ack / 0 fold ack / 2 folded by the server, B code
    /// (0x0A81 ok).</summary>
    Tent,
    /// <summary>Not decoded but passed (raw logging).</summary>
    Raw,
}

/// <summary>One decoded inbound event stored in rings. Variable text lives in <see cref="Text"/> (rare kinds only).</summary>
public struct InboundEvent
{
    /// <summary>Event kind.</summary>
    public InboundKind Kind;
    /// <summary>Wire opcode it came from.</summary>
    public ushort Opcode;
    /// <summary>Receive time (monotonic ms).</summary>
    public long At;
    /// <summary>Main subject handle (entity), -1 when none.</summary>
    public int Handle;
    /// <summary>Second handle (attacker/target), -1 when none.</summary>
    public int Handle2;
    /// <summary>Integer arguments (meaning per kind).</summary>
    public long A, B, C;
    /// <summary>Coordinates when the kind carries a position.</summary>
    public uint X, Y;
    /// <summary>Text for the few kinds that carry it (chat, names, menus); null otherwise.</summary>
    public string? Text;
    /// <summary>Menu options (server menu only).</summary>
    public (byte Reply, string Text)[]? Options;

    /// <summary>Create an event of a kind with no handles.</summary>
    public static InboundEvent Of(InboundKind k, ushort op, long at) => new() { Kind = k, Opcode = op, At = at, Handle = -1, Handle2 = -1 };
}

/// <summary>Inline payload storage for intents (no allocation for frames up to 64 bytes).</summary>
[InlineArray(64)]
public struct IntentBytes { private byte _e0; }

/// <summary>One outbound request produced by the tick: opcode + inline payload (or a heap payload for long frames).</summary>
public struct Intent
{
    /// <summary>Wire opcode.</summary>
    public ushort Opcode;
    /// <summary>Action this intent belongs to (-1 none).</summary>
    public int ActionId;
    /// <summary>Inline bytes.</summary>
    public IntentBytes Inline;
    /// <summary>Inline length.</summary>
    public byte Length;
    /// <summary>Long payload (chat text), null when inline.</summary>
    public byte[]? Heap;

    /// <summary>Payload span.</summary>
    [UnscopedRef] public ReadOnlySpan<byte> Payload => Heap ?? ((ReadOnlySpan<byte>)Inline)[..Length];
}

/// <summary>Receiver for decoded events (the bot's inbound ring).</summary>
public interface IEventSink
{
    /// <summary>Append one event; false when the ring is full (counted, never blocks).</summary>
    bool TryWrite(in InboundEvent evt);
}

/// <summary>Decodes the frames of one packet family into <see cref="InboundEvent"/>s.</summary>
public interface IPacketDecoder
{
    /// <summary>Opcodes this decoder handles.</summary>
    IReadOnlyList<ushort> Opcodes { get; }
    /// <summary>Decode one (canonical-shaped) payload; writes 0..n events into the sink.</summary>
    void Decode(ushort opcode, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink);
}

/// <summary>Little-endian readers that never throw on short frames (they return the fallback).</summary>
internal static class Rd
{
    public static ushort U16(ReadOnlySpan<byte> p, int at) => at + 2 <= p.Length ? BinaryPrimitives.ReadUInt16LittleEndian(p[at..]) : (ushort)0;
    public static uint U32(ReadOnlySpan<byte> p, int at) => at + 4 <= p.Length ? BinaryPrimitives.ReadUInt32LittleEndian(p[at..]) : 0;
    public static long U64(ReadOnlySpan<byte> p, int at) => at + 8 <= p.Length ? (long)BinaryPrimitives.ReadUInt64LittleEndian(p[at..]) : 0;
}

/// <summary>Opcode -> decoder table for one wire profile, built once. Dispatch canonicalises the frame through the
/// profile, then decodes.</summary>
public sealed class DecoderTable
{
    private readonly IPacketDecoder?[] _byOp = new IPacketDecoder?[65536];
    private readonly WireProfile _wire;

    /// <summary>Create for a profile.</summary>
    public DecoderTable(WireProfile wire) => _wire = wire;
    /// <summary>The profile.</summary>
    public WireProfile Wire => _wire;
    /// <summary>Frames with no decoder (opcode counts, for the "decode it, don't leave it unknown" backlog).</summary>
    public long[] Undecoded { get; } = new long[65536];

    /// <summary>Build with every family decoder.</summary>
    public static DecoderTable CreateDefault(WireProfile wire)
    {
        var t = new DecoderTable(wire);
        t.Register(new EntityDecoder()); t.Register(new CombatDecoder()); t.Register(new SelfDecoder());
        t.Register(new MovementDecoder()); t.Register(new ItemDecoder()); t.Register(new QuestMenuDecoder());
        t.Register(new SocialDecoder()); t.Register(new WorldDecoder()); t.Register(new TradeDecoder()); t.Register(new ProduceDecoder()); t.Register(new ScenarioDecoder());
        return t;
    }

    /// <summary>Register a decoder for its opcodes.</summary>
    public void Register(IPacketDecoder decoder) { foreach (var op in decoder.Opcodes) _byOp[op] = decoder; }

    /// <summary>Is there a decoder for an opcode?</summary>
    public bool Handles(ushort op) => _byOp[op] is not null;

    /// <summary>Decode a frame with its registered decoder.</summary>
    public void Dispatch(in RawFrame frame, IEventSink sink) => Dispatch(frame.Opcode, frame.Payload.Span, frame.ReceivedAt, sink);

    /// <summary>Decode a payload.</summary>
    public void Dispatch(ushort op, ReadOnlySpan<byte> payload, long at, IEventSink sink)
    {
        var d = _byOp[op];
        if (d is null) { Undecoded[op]++; return; }
        byte[]? scratch = null;
        var p = _wire.Normalize(op, payload, ref scratch);
        d.Decode(op, p, at, _wire, sink);
    }
}

/// <summary>Briefinfo: players, mobs/NPCs/gates, drops, deletes.</summary>
public sealed class EntityDecoder : IPacketDecoder
{
    /// <summary>Entity kind bits as sent in EntityAppeared.A (mirrors the agent's EntityKind values).</summary>
    public const int KMob = 1, KNpc = 4, KPlayer = 8, KGate = 16, KDrop = 32;
    /// <summary>Mob ids that are NPCs (client MobInfo.IsNPC); set by the host from client data.</summary>
    public static Func<int, bool> IsNpc { get; set; } = _ => false;

    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x1C06, 0x1C07, 0x1C08, 0x1C09, 0x1C0A, 0x1C0E, 0x1805];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        switch (op)
        {
            case 0x1C06: Player(p, op, at, sink); break;
            case 0x1C07:
                if (p.Length < 1) return;
                for (int i = 0, n = p[0]; i < n && 1 + (i + 1) * 235 <= p.Length; i++) Player(p.Slice(1 + i * 235, 235), op, at, sink);
                break;
            case 0x1C08: Mob(p, op, at, sink); break;
            case 0x1C09:
                if (p.Length < 1) return;
                for (int i = 0, n = p[0]; i < n && 1 + (i + 1) * 149 <= p.Length; i++) Mob(p.Slice(1 + i * 149, 149), op, at, sink);
                break;
            case 0x1C0A:
                if (p.Length < 14) return;
                var e = InboundEvent.Of(InboundKind.EntityAppeared, op, at);
                e.Handle = Rd.U16(p, 0); e.A = KDrop; e.B = Rd.U16(p, 2); e.C = -1; e.X = Rd.U32(p, 4); e.Y = Rd.U32(p, 8); e.Handle2 = Rd.U16(p, 12);
                sink.TryWrite(e);
                break;
            case 0x1C0E or 0x1805:
                if (p.Length < 2) return;
                var l = InboundEvent.Of(InboundKind.EntityLeft, op, at); l.Handle = Rd.U16(p, 0);
                sink.TryWrite(l);
                break;
        }
    }

    // PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD (235): handle, charid[20], coord {xy, dir}, mode, class, ... Level @197
    private static void Player(ReadOnlySpan<byte> r, ushort op, long at, IEventSink sink)
    {
        if (r.Length < 31) return;
        var e = InboundEvent.Of(InboundKind.EntityAppeared, op, at);
        e.Handle = Rd.U16(r, 0); e.A = KPlayer; e.B = -1; e.C = r.Length > 197 ? r[197] : -1;
        e.X = Rd.U32(r, 22); e.Y = Rd.U32(r, 26); e.Text = Ascii.Get(r.Slice(2, 20));
        e.Handle2 = r.Length > 32 ? r[32] : -1;   // class
        sink.TryWrite(e);
    }

    // PROTO_NC_BRIEFINFO_REGENMOB_CMD (149): handle, mode, mobid, coord {xy, dir}, flagstate, flag blob (a gate's blob
    // starts with its destination map name)
    private static void Mob(ReadOnlySpan<byte> r, ushort op, long at, IEventSink sink)
    {
        if (r.Length < 15) return;
        var e = InboundEvent.Of(InboundKind.EntityAppeared, op, at);
        e.Handle = Rd.U16(r, 0);
        var mobId = Rd.U16(r, 3);
        var gate = r[14] == 1;
        e.A = gate ? KGate | KNpc : IsNpc(mobId) ? KNpc : KMob;
        e.B = mobId; e.C = -1; e.X = Rd.U32(r, 5); e.Y = Rd.U32(r, 9);
        e.Handle2 = r[13];   // direction byte
        if (gate && r.Length >= 15 + 32) e.Text = Ascii.Get(r.Slice(15, 32));
        sink.TryWrite(e);
    }
}

/// <summary>Combat: damage, cast start/result/fail, bash stop, kills, target info.</summary>
public sealed class CombatDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } =
        [0x2448, 0x2449, 0x243C, 0x244A, 0x2402, 0x244E, 0x2452, 0x2434, 0x2435, 0x2437, 0x2438, 0x243D, 0x2447, 0x2009, 0x244D];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x2448:   // SWING_DAMAGE {attacker, defender, flag u16, damage (u16 / u32 in 2026), resthp u32, ...}
            {
                var (len, dmg) = wire.SwingDamage;
                if (p.Length < len && p.Length < 16) return;
                if (p.Length != len) { dmg = p.Length >= 25 ? 4 : 2; }
                e.Kind = InboundKind.Damage; e.Handle = Rd.U16(p, 0); e.Handle2 = Rd.U16(p, 2); e.C = Rd.U16(p, 4);
                e.A = dmg == 4 ? Rd.U32(p, 6) : Rd.U16(p, 6);
                e.B = Rd.U32(p, 6 + dmg);
                break;
            }
            case 0x2449:   // SOMEONESWING_DAMAGE (canonical 13): attacker, defender, flag u8, resthp u32 @5 (no damage amount)
                if (p.Length < 9) return;
                e.Kind = InboundKind.Damage; e.Handle = Rd.U16(p, 0); e.Handle2 = Rd.U16(p, 2); e.C = p[4]; e.A = -1; e.B = Rd.U32(p, 5);
                break;
            case 0x243C:   // DOTDAMAGE (canonical 13): object, resthp u32 @2, damage u16 @6, abstate u16 @8
                if (p.Length < 8) return;
                e.Kind = InboundKind.Damage; e.Handle = -1; e.Handle2 = Rd.U16(p, 0); e.B = Rd.U32(p, 2); e.A = Rd.U16(p, 6); e.C = Rd.U16(p, 8);
                break;
            case 0x244A:   // REALLYKILL {dead, attacker}
                if (p.Length < 4) return;
                e.Kind = InboundKind.EntityDied; e.Handle = Rd.U16(p, 0); e.Handle2 = Rd.U16(p, 2);
                break;
            case 0x2402:   // TARGETINFO {order, handle, hp, maxhp, sp, maxsp, lp, maxlp, level}
                if (p.Length < 28) return;
                e.Kind = InboundKind.EntityHp; e.Handle = Rd.U16(p, 1); e.A = Rd.U32(p, 3); e.B = Rd.U32(p, 7); e.C = p[27];
                break;
            case 0x244E:   // SKILLBASH_HIT_OBJ_START {skill, target, index}
                if (p.Length < 6) return;
                e.Kind = InboundKind.SkillCastStart; e.A = Rd.U16(p, 0); e.Handle2 = Rd.U16(p, 2); e.B = Rd.U16(p, 4);
                break;
            case 0x2452:   // SKILLBASH_HIT_DAMAGE {index, caster, n, n x 14 {target u16, flag u16?, damage u32, resthp u32, ...}}
            {
                if (p.Length < 5) return;
                var caster = Rd.U16(p, 2); var idx = Rd.U16(p, 0);
                for (int i = 0, n = p[4]; i < n && 5 + (i + 1) * 14 <= p.Length; i++)
                {
                    var t = p.Slice(5 + i * 14, 14);
                    var d = InboundEvent.Of(InboundKind.SkillDamage, op, at);
                    d.Handle = caster; d.Handle2 = Rd.U16(t, 0); d.A = Rd.U32(t, 4); d.B = Rd.U32(t, 8); d.C = idx;
                    sink.TryWrite(d);
                }
                return;
            }
            case 0x2434:   // SKILLBASH_CAST_FAIL_ACK {code u16}
                e.Kind = InboundKind.CastFail; e.A = Rd.U16(p, 0);
                break;
            case 0x2435: e.Kind = InboundKind.CastSucceeded; break;
            case 0x2437: e.Kind = InboundKind.CastAborted; e.A = 1; break;
            case 0x2438: e.Kind = InboundKind.CastAborted; e.A = 0; e.Handle = Rd.U16(p, 0); break;
            case 0x243D:   // CEASE_FIRE {handle}
                e.Kind = InboundKind.BashStop; e.Handle = Rd.U16(p, 0);
                break;
            case 0x2447:   // SWING_START {attacker, defender, ...}
                if (p.Length < 4) return;
                e.Kind = InboundKind.SwingStart; e.Handle = Rd.U16(p, 0); e.Handle2 = Rd.U16(p, 2);
                break;
            case 0x2009:   // SOMEONECHANGEMODE {handle, mode}
                if (p.Length < 3) return;
                e.Kind = InboundKind.EntityMode; e.Handle = Rd.U16(p, 0); e.A = p[2];
                break;
            case 0x244D:   // TARGETCHANGE: our selection as the game holds it {target}
                if (p.Length < 2) return;
                e.Kind = InboundKind.EntityTarget; e.Handle = -1; e.Handle2 = Rd.U16(p, 0);
                break;
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Self: HP/SP, base, level, exp, money, speed, abstates, map login, skills.</summary>
public sealed class SelfDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } =
        [0x240E, 0x240F, 0x1035, 0x1038, 0x240C, 0x240B, 0x1033, 0x203E, 0x2427, 0x2428, 0x1802, 0x1804, 0x103D, 0x105B, 0x105F, 0x1061, 0x2411, 0xCC02, 0xCC06, 0x4804, 0x4805, 0x1059];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x240E: if (p.Length < 4) return; e.Kind = InboundKind.SelfHp; e.A = Rd.U32(p, 0); break;
            case 0x240F: if (p.Length < 4) return; e.Kind = InboundKind.SelfSp; e.A = Rd.U32(p, 0); break;
            case 0x1035:   // CHANGEPARAMCHANGE {n u8, (id u8, value u32) x n}: 0x10 MaxHp, 0x11 MaxSp
            {
                if (p.Length < 1) return;
                long mh = -1, ms = -1;
                for (int i = 0, n = p[0]; i < n && 1 + 5 * (i + 1) <= p.Length; i++)
                {
                    // ids 0..15 are the CHAR_PARAMETER_DATA stats in order (checked on a capture against the map login
                    // values: id 0 = Str 290, 1 = End 250, 2 = Dex 214, 3 = Int 143, 5 = Spr 241, 6 = WC min 295 ...)
                    var id = p[1 + 5 * i]; var v = Rd.U32(p, 2 + 5 * i);
                    if (id == 0x10) mh = v; else if (id == 0x11) ms = v;
                    else if (id < 16) { var st = InboundEvent.Of(InboundKind.SelfStat, op, at); st.A = id; st.B = v; sink.TryWrite(st); }
                }
                if (mh < 0 && ms < 0) return;
                e.Kind = InboundKind.SelfMaxHpSp; e.A = mh; e.B = ms;
                break;
            }
            case 0x1038:   // CHAR_BASE (canonical 105): level @25, exp @26, stones @38/@40, hp @42, sp @46, cen @58, map @66
            {
                if (p.Length < 66) return;
                e.Kind = InboundKind.SelfBase; e.A = p[25]; e.B = Rd.U64(p, 26); e.C = Rd.U64(p, 58);
                e.X = Rd.U16(p, 38); e.Y = Rd.U16(p, 40);
                e.Text = p.Length >= 78 ? Ascii.Get(p.Slice(66, 12)) : null;
                sink.TryWrite(e);
                var hp = InboundEvent.Of(InboundKind.SelfHp, op, at); hp.A = Rd.U32(p, 42); sink.TryWrite(hp);
                var sp = InboundEvent.Of(InboundKind.SelfSp, op, at); sp.A = Rd.U32(p, 46); sink.TryWrite(sp);
                var fame = InboundEvent.Of(InboundKind.SelfStat, op, at); fame.A = 0x20; fame.B = Rd.U32(p, 54); sink.TryWrite(fame);
                // @87 statdistribute {Str, End, Dex, Int, Spr, RedistributePoint}: points already placed and the
                // redistribution allowance, not free points (those come in STAT_REMAINPOINT 0x105B; the server ignored an
                // INCPOINT spent on this number)
                return;
            }
            case 0x240C:   // LEVELUP {level u8, mob u16, CHAR_PARAMETER_DATA {prevExp u64, nextExp u64, ...}}
                if (p.Length < 3) return;
                e.Kind = InboundKind.SelfLevel; e.A = p[0]; e.Handle = Rd.U16(p, 1); e.B = Rd.U64(p, 3); e.C = Rd.U64(p, 11);
                break;
            case 0x240B: if (p.Length < 4) return; e.Kind = InboundKind.SelfExp; e.A = Rd.U32(p, 0); e.Handle = Rd.U16(p, 4); break;
            case 0x2411: if (p.Length < 4) return; e.Kind = InboundKind.ExpLost; e.A = Rd.U32(p, 0); break;   // BAT_EXPLOST {explost u32}
            case 0x1033: if (p.Length < 8) return; e.Kind = InboundKind.SelfMoney; e.A = Rd.U64(p, 0); break;
            case 0x203E: if (p.Length < 4) return; e.Kind = InboundKind.SelfSpeed; e.A = Rd.U16(p, 0); e.B = Rd.U16(p, 2); break;
            case 0x2427 or 0x2428:   // ABSTATESET / RESET {handle u16, abstate u32 ...}
                if (p.Length < 6) return;
                e.Kind = InboundKind.SelfAbstate; e.Handle = Rd.U16(p, 0); e.A = Rd.U32(p, 2); e.B = op == 0x2427 ? 1 : 0;
                e.C = p.Length >= 10 ? Rd.U32(p, 6) : -1;
                break;
            case 0x1802:   // MAP_LOGIN_ACK {charhandle, CHAR_PARAMETER_DATA (232), logincoord}
            {
                if (p.Length < 10) return;
                e.Kind = InboundKind.MapLoginAck; e.Handle = Rd.U16(p, 0);
                e.X = Rd.U32(p, p.Length - 8); e.Y = Rd.U32(p, p.Length - 4);
                e.A = p.Length >= 154 ? Rd.U32(p, 146) : -1; e.B = p.Length >= 154 ? Rd.U32(p, 150) : -1;
                sink.TryWrite(e);
                if (p.Length >= 18)
                {
                    var b = InboundEvent.Of(InboundKind.SelfExpBand, op, at); b.A = Rd.U64(p, 2); b.B = Rd.U64(p, 10);
                    sink.TryWrite(b);
                }
                // CHAR_PARAMETER_DATA @2: 16 stats {base u32, value u32} from @16; max HP / SP stones @160 / @164
                if (p.Length >= 2 + 168)
                {
                    for (var i = 0; i < 16; i++)
                    {
                        if (i == 4) continue;   // the unused fifth stat carries no value
                        var st = InboundEvent.Of(InboundKind.SelfStat, op, at); st.A = i; st.B = Rd.U32(p, 2 + 16 + 8 * i + 4); sink.TryWrite(st);
                    }
                    var mh = InboundEvent.Of(InboundKind.SelfStat, op, at); mh.A = 0x21; mh.B = Rd.U32(p, 2 + 160); sink.TryWrite(mh);
                    var ms = InboundEvent.Of(InboundKind.SelfStat, op, at); ms.A = 0x22; ms.B = Rd.U32(p, 2 + 164); sink.TryWrite(ms);
                }
                return;
            }
            case 0x1804:   // MAP_LOGINFAIL {err u16, wrong file index u8}
                e.Kind = InboundKind.MapLoginFail; e.A = Rd.U16(p, 0); e.B = p.Length >= 3 ? p[2] : -1;
                break;
            case 0x103D:   // CLIENT_SKILL {restempow, partmark, maxnum u16, chrregnum u32, number u16, n x 12 {skill, cooltime u32, empow, mastery}}
            {
                if (p.Length < 10) return;
                for (int i = 0, n = Rd.U16(p, 8); i < n && 10 + (i + 1) * 12 <= p.Length; i++)
                {
                    var s = InboundEvent.Of(InboundKind.SkillLearned, op, at);
                    s.A = Rd.U16(p, 10 + i * 12); s.B = Rd.U32(p, 12 + i * 12);
                    sink.TryWrite(s);
                }
                return;
            }
            case 0x105B: if (p.Length < 1) return; e.Kind = InboundKind.StatPoints; e.A = p[0]; break;
            case 0x1059:   // CHAR_PROMOTE_ACK {new class u8} (captured at the end of the job change instance: {02})
                if (p.Length < 1) return;
                e.Kind = InboundKind.Promoted; e.A = p[0];
                break;
            case 0x4804:   // SKILL_LEARNSUC {skill u16, u8} (captured right after a skill book's ITEM_USE_ACK 0x0700)
                if (p.Length < 2) return;
                e.Kind = InboundKind.SkillLearned; e.A = Rd.U16(p, 0); e.B = 0; e.C = 1;
                break;
            case 0x4805:   // SKILL_LEARNFAIL {err u16}
                e.Kind = InboundKind.SkillLearnFail; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1;
                break;
            case 0x105F: if (p.Length < 1) return; e.Kind = InboundKind.StatPointSpent; e.A = p[0]; break;   // INCPOINTSUC {stat}
            case 0x1061: if (p.Length < 2) return; e.Kind = InboundKind.StatPointRefused; e.A = Rd.U16(p, 0); break;   // INCPOINTFAIL {err}
            case 0xCC02: e.Kind = InboundKind.Mount; e.A = 1; e.Handle = Rd.U16(p, 0); break;
            case 0xCC06: e.Kind = InboundKind.Mount; e.A = 0; break;
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Movement: moves, stops, MOVEFAIL.</summary>
public sealed class MovementDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x2018, 0x201A, 0x2013, 0x201B, 0x2007];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x2018 or 0x201A:   // SOMEONEMOVEWALK / RUN {handle, from xy, to xy, speed u16, attr}
                if (p.Length < 18) return;
                e.Kind = InboundKind.EntityMoved; e.Handle = Rd.U16(p, 0); e.A = Rd.U32(p, 2); e.B = Rd.U32(p, 6);
                e.X = Rd.U32(p, 10); e.Y = Rd.U32(p, 14); e.C = Rd.U16(p, 18); e.Handle2 = op == 0x201A ? 1 : 0;
                break;
            case 0x2013:   // SOMEONESTOP {handle, xy}
                if (p.Length < 10) return;
                e.Kind = InboundKind.EntityStopped; e.Handle = Rd.U16(p, 0); e.X = Rd.U32(p, 2); e.Y = Rd.U32(p, 6);
                break;
            case 0x201B or 0x2007:   // MOVEFAIL {back xy}
                if (p.Length < 8) return;
                e.Kind = InboundKind.MoveFail; e.X = Rd.U32(p, 0); e.Y = Rd.U32(p, 4);
                break;
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Items: bag list, cell changes, use results, buy/sell acks, stone reserves, pick up.</summary>
public sealed class ItemDecoder : IPacketDecoder
{
    /// <summary>The bag box id in item locations ((box &lt;&lt; 10) | slot).</summary>
    public const int BagBox = 9, EquipBox = 8;

    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } =
        [0x1047, 0x3001, 0x3002, 0x3016, 0x3004, 0x3005, 0x5003, 0x5004, 0x5005, 0x5006, 0x5008, 0x500A, 0x300A, 0x0700, 0x3C07, 0x3C08, 0x300C, 0x300E, 0x304B, 0x3018];

    /// <summary>The storage box id in item locations.</summary>
    public const int StorageBox = 6;

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x1047:   // CLIENT_ITEM (canonical) {count u8, box u8, flag u8, records {datasize, location u16, itemid u16, attr..}}
            {
                if (p.Length < 3) return;
                // an empty-box marker so a fresh login clears the box first
                var clear = InboundEvent.Of(InboundKind.ItemCell, op, at); clear.A = p[1]; clear.B = -1; clear.C = -1;
                sink.TryWrite(clear);
                Records(op, p, 3, p[0], at, sink);
                return;
            }
            case 0x3C08:   // MENU_OPENSTORAGE_CMD (canonical) {cen u64, maxpage u8, curpage u8, opentype u8, count u8, records}
            {
                // one frame PER PAGE, in any order (Z:/Storage.pcapng: pages 3, 0, 1, 2 back to back). The box is cleared
                // once per window open (WorldState, on the first page), never per frame: clearing per frame wiped the
                // pages that came first, the bot saw occupied cells as free and its deposits swapped stored items back
                // into the bag (NextA, 21:58: seven 'deposits' all from bag slot 22)
                if (p.Length < 12) return;
                e.Kind = InboundKind.StorageList; e.A = Rd.U64(p, 0); e.B = p[8]; e.C = p[9]; e.X = p[11];
                sink.TryWrite(e);
                Records(op, p, 12, p[11], at, sink);
                return;
            }
            case 0x3C07:   // MENU_OPENSTORAGE_FAIL_CMD {err u16}
                e.Kind = InboundKind.StorageFail; e.A = p.Length >= 2 ? Rd.U16(p, 0) : p.Length == 1 ? p[0] : -1;
                break;
            case 0x300C:   // ITEM_RELOC_ACK {err u16}
                e.Kind = InboundKind.RelocAck; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1;
                break;
            case 0x3018:   // ITEM_UPGRADE_ACK {err u16}
                e.Kind = InboundKind.UpgradeAck; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1;
                break;
            case 0x304B:   // ITEM_AUTO_ARRANGE_INVEN_ACK {err u16}
                e.Kind = InboundKind.SortAck; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1;
                break;
            case 0x300E:   // ITEM_SPLIT_ACK {err u16}
                e.Kind = InboundKind.SplitAck; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1;
                break;
            case 0x3001:   // CELLCHANGE {exchange, location u16, itemid u16, attr}
            {
                if (p.Length < 6) return;
                var loc = Rd.U16(p, 2);
                e.Kind = InboundKind.ItemCell; e.A = loc >> 10; e.B = loc & 0x3FF; e.C = Rd.U16(p, 4);
                if (e.C == 0xFFFF) e.C = -1;
                e.X = (uint)Lot(p[6..]);
                break;
            }
            case 0x3002:   // EQUIPCHANGE {exchange u16 (bag location), location u8 (equip slot), itemid u16, attr}
            {
                if (p.Length < 5) return;
                var from = Rd.U16(p, 0);
                var v = InboundEvent.Of(InboundKind.ItemCell, op, at); v.A = from >> 10; v.B = from & 0x3FF; v.C = -1;
                sink.TryWrite(v);
                e.Kind = InboundKind.ItemCell; e.A = EquipBox; e.B = p[2]; e.C = Rd.U16(p, 3);
                if (e.C == 0xFFFF) e.C = -1;
                e.X = 1;
                break;
            }
            case 0x3016 or 0x0700:   // ITEM_USE_ACK {error u16, item u16, invenType}
                e.Kind = InboundKind.ItemUseResult; e.A = Rd.U16(p, 0); e.B = Rd.U16(p, 2);
                break;
            case 0x3004 or 0x3005: e.Kind = InboundKind.BuySellAck; e.A = Rd.U16(p, 0); e.B = op == 0x3004 ? 0 : 1; break;
            case 0x5003 or 0x5004: e.Kind = InboundKind.StoneReserve; e.A = op == 0x5003 ? 0 : 1; e.B = Rd.U16(p, 0); e.C = 1; break;
            case 0x5005: e.Kind = InboundKind.StoneUse; e.A = 3; e.B = Rd.U16(p, 0); break;
            case 0x5006: e.Kind = InboundKind.StoneUse; e.A = 2; e.B = Rd.U16(p, 0); break;
            case 0x5008 or 0x500A: e.Kind = InboundKind.StoneUse; e.A = op == 0x5008 ? 0 : 1; e.B = 0; break;
            case 0x300A:   // PICK_ACK {itemid, lot u32, error, itemhandle}
                if (p.Length < 10) return;
                e.Kind = InboundKind.PickAck; e.A = Rd.U16(p, 0); e.B = Rd.U32(p, 2); e.C = Rd.U16(p, 6); e.Handle = Rd.U16(p, 8);
                break;
            default: return;
        }
        sink.TryWrite(e);
    }

    // item records {datasize, location u16, itemid u16, attr}: datasize counts location + item, not itself
    private static void Records(ushort op, ReadOnlySpan<byte> p, int off, int n, long at, IEventSink sink)
    {
        for (var i = 0; i < n && off + 5 <= p.Length; i++)
        {
            int ds = p[off];
            var c = InboundEvent.Of(InboundKind.ItemCell, op, at);
            var loc = Rd.U16(p, off + 1);
            c.A = loc >> 10; c.B = loc & 0x3FF; c.C = Rd.U16(p, off + 3);
            if (c.C == 0xFFFF) c.C = -1;
            c.X = (uint)Lot(p.Slice(off + 5, Math.Max(0, Math.Min(ds - 4, p.Length - off - 5))));
            sink.TryWrite(c);
            if (ds == 0) return;
            off += 1 + ds;
        }
    }

    // the lot is the attribute byte(s) right after the item id for stackables: 1 byte or 2 bytes; gear has more
    private static int Lot(ReadOnlySpan<byte> attr) => attr.Length switch { 1 => attr[0], 2 => attr[0] | (attr[1] << 8), _ => 1 };
}

/// <summary>Quests, dialogue pages, NPC menus, shop lists.</summary>
public sealed class QuestMenuDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } =
        [0x103A, 0x103B, 0x10CE, 0x4401, 0x440D, 0x4408, 0x4410, 0x4413, 0x4415, 0x201C, 0x3C01, 0x3C03, 0x3C04, 0x3C06, 0x3C09, 0x3C0A, 0x3C0B, 0x3C05];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x103A:   // CLIENT_QUEST_DOING {chr u32, clear u8, count u8, n x PLAYER_QUEST_INFO (32)}
            {
                if (p.Length < 6) return;
                for (int i = 0, n = p[5]; i < n && 6 + (i + 1) * 32 <= p.Length; i++)
                {
                    var q = p.Slice(6 + i * 32, 32);
                    var x = InboundEvent.Of(InboundKind.QuestProgress, op, at);
                    x.A = Rd.U16(q, 0); x.B = q[2];
                    long packed = 0;
                    for (var k = 0; k < 5; k++) packed |= (long)q[24 + k] << (8 * k);
                    x.C = packed;
                    sink.TryWrite(x);
                }
                return;
            }
            case 0x103B:   // CLIENT_QUEST_DONE {chr u32, total u32?, count u16 @8, pad, n x {id u16, end time i64}}
            {
                if (p.Length < 12) return;
                for (int i = 0, n = Rd.U16(p, 8); i < n && 12 + i * 10 + 2 <= p.Length; i++)
                {
                    var x = InboundEvent.Of(InboundKind.QuestState, op, at); x.A = Rd.U16(p, 12 + i * 10);
                    sink.TryWrite(x);
                }
                return;
            }
            case 0x10CE:   // CLIENT_QUEST_READ {chr u32, count u16, ids u16[]}
            {
                if (p.Length < 6) return;
                for (int i = 0, n = Rd.U16(p, 4); i < n && 6 + i * 2 + 2 <= p.Length; i++)
                {
                    var x = InboundEvent.Of(InboundKind.QuestAvailable, op, at); x.A = Rd.U16(p, 6 + i * 2);
                    sink.TryWrite(x);
                }
                return;
            }
            case 0x4401:   // QUEST_SCRIPT_CMD_REQ {quest u16, cmd u32, pigeon u8, data: SAY {dialog u32, talker u32, npc u16} / others {id u16}}
                if (p.Length < 9) return;
                e.Kind = InboundKind.QuestDialog; e.A = Rd.U16(p, 0); e.B = Rd.U32(p, 2);
                var say = e.B == 2;
                e.C = say ? Rd.U32(p, 7) : Rd.U16(p, 7);
                e.X = say ? Rd.U32(p, 11) : 0;
                e.Handle2 = say ? Rd.U16(p, 15) : -1;
                break;
            case 0x440D:   // NOTIFY_MOB_KILL {n u8, n x {objIdx u16, quest u16}}
            {
                if (p.Length < 1) return;
                for (int i = 0, n = p[0]; i < n && 1 + (i + 1) * 4 <= p.Length; i++)
                {
                    var x = InboundEvent.Of(InboundKind.QuestMobKill, op, at); x.B = Rd.U16(p, 1 + i * 4); x.A = Rd.U16(p, 3 + i * 4);
                    sink.TryWrite(x);
                }
                return;
            }
            case 0x4408: e.Kind = InboundKind.QuestAck; e.A = op; e.C = Rd.U16(p, 0); e.B = Rd.U16(p, 2); break;
            // QUEST_SELECT_START_ACK {npc u16, quest u16, code u16} (captured: {27, 20028, 0x0B47} for an event quest)
            case 0x4410 when p.Length >= 6: e.Kind = InboundKind.QuestAck; e.A = op; e.B = Rd.U16(p, 4); e.C = Rd.U16(p, 2); break;
            case 0x4410 or 0x4413 or 0x4415: e.Kind = InboundKind.QuestAck; e.A = op; e.B = Rd.U16(p, 0); e.C = -1; break;
            case 0x201C: if (p.Length < 2) return; e.Kind = InboundKind.NpcMenu; e.A = Rd.U16(p, 0); break;
            case 0x3C01:   // SERVERMENU_REQ {title[128], priority, npc u16, npc xy, range u16, menunum, n x {reply u8, text[32]}}
            {
                if (p.Length < 142) return;
                e.Kind = InboundKind.ServerMenu; e.Handle = Rd.U16(p, 129); e.Text = Ascii.Get(p[..128]);
                e.X = Rd.U32(p, 131); e.Y = Rd.U32(p, 135);
                int n = p[141];
                var opts = new (byte, string)[n];
                for (var i = 0; i < n && 142 + (i + 1) * 33 <= p.Length; i++) opts[i] = (p[142 + i * 33], Ascii.Get(p.Slice(143 + i * 33, 32)));
                e.Options = opts; e.A = n;
                break;
            }
            case 0x3C05:   // SHOPOPENSOULSTONE {hp {price u32, cap u32, u32}, sp {price u32, cap u32, u32}} (24)
            {
                e.Kind = InboundKind.ShopList; e.A = op; e.Handle = -1; e.B = 0;
                sink.TryWrite(e);
                if (p.Length < 20) return;
                var m = InboundEvent.Of(InboundKind.StoneShop, op, at);
                m.A = Rd.U32(p, 0); m.B = Rd.U32(p, 4); m.C = Rd.U32(p, 12); m.X = Rd.U32(p, 16);
                sink.TryWrite(m);
                return;
            }
            case 0x3C03 or 0x3C04 or 0x3C06 or 0x3C09 or 0x3C0A or 0x3C0B:   // SHOPOPEN tables (canonical {n u16, npc u16, n x {slot u8, item u16}})
            {
                if (p.Length < 4) return;
                int n = Rd.U16(p, 0);
                e.Kind = InboundKind.ShopList; e.A = op; e.Handle = Rd.U16(p, 2); e.B = n;
                sink.TryWrite(e);
                for (var i = 0; i < n && 4 + (i + 1) * 3 <= p.Length; i++)
                {
                    var s = InboundEvent.Of(InboundKind.ShopItem, op, at); s.A = p[4 + i * 3]; s.B = Rd.U16(p, 5 + i * 3);
                    sink.TryWrite(s);
                }
                return;
            }
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Chat, whisper (party and trade are passed as raw events for now).</summary>
public sealed class SocialDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x2002, 0x200D, 0x2011];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x2002:   // SOMEONECHAT {links u8, handle u16, len u8, flag, font, balloon, content}
                if (p.Length < 7) return;
                e.Kind = InboundKind.Chat; e.Handle = Rd.U16(p, 1);
                e.Text = Text(p.Slice(7, Math.Min(p[3], p.Length - 7)));
                break;
            case 0x200D:   // SOMEONEWHISPER {links u8, talker[20], flag, len, content}
                if (p.Length < 23) return;
                e.Kind = InboundKind.Whisper;
                e.Text = Ascii.Get(p.Slice(1, 20)) + "\n" + Text(p.Slice(23, Math.Min(p[22], p.Length - 23)));
                break;
            case 0x2011:   // NOTICE {u8, len, text}
                if (p.Length < 2) return;
                e.Kind = InboundKind.Chat; e.Handle = -1; e.Text = Text(p.Slice(2, Math.Min(p[1], p.Length - 2)));
                break;
            default: return;
        }
        sink.TryWrite(e);
    }

    private static string Text(ReadOnlySpan<byte> b)
    {
        var n = b.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(n < 0 ? b : b[..n]);
    }
}

/// <summary>Map links, revive, dead menu.</summary>
public sealed class WorldDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x1809, 0x180A, 0x104D, 0x104F, 0x1050, 0x1812, 0x1813, 0x1818, 0x1819, 0x1827, 0x182B];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x1809 or 0x180A:   // LINKSAME {map u16, x, y} / LINKOTHER {..., ip[16], port, wm}
                if (p.Length < 10) return;
                e.Kind = InboundKind.MapLink; e.A = Rd.U16(p, 0); e.X = Rd.U32(p, 2); e.Y = Rd.U32(p, 6); e.B = op == 0x180A ? 1 : 0;
                break;
            case 0x104D: e.Kind = InboundKind.DeadMenu; e.A = Rd.U32(p, 0); break;
            case 0x1827: if (p.Length < 4) return; e.Kind = InboundKind.FieldAttribute; e.A = Rd.U32(p, 0); break;   // FIELD_ATTRIBUTE {eFieldMapType u32}
            case 0x182B: if (p.Length < 1) return; e.Kind = InboundKind.ReviveItemAllowed; e.A = p[0]; break;           // CAN_USE_REVIVEITEM {u8}
            case 0x1812 or 0x1813 or 0x1818 or 0x1819:   // MAP_FREEPKZONE_ON / OFF, MAP_PARTYBATTLEZONE_ON / OFF
                e.Kind = InboundKind.PvpZone; e.A = op is 0x1812 or 0x1818 ? 1 : 0; e.B = op is 0x1812 or 0x1813 ? 2 : 1;
                break;
            case 0x104F or 0x1050:
                if (p.Length < 10) return;
                e.Kind = InboundKind.Revive; e.A = Rd.U16(p, 0); e.X = Rd.U32(p, 2); e.Y = Rd.U32(p, 6); e.B = op == 0x1050 ? 1 : 0;
                break;
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Player trade, server to client (dept 19, opcode 0x4C00 | command). One <see cref="InboundKind.TradeEvent"/>
/// per frame, A = command:
/// 2 PROPOSE_ASK_REQ (B proposer handle), 4 PROPOSENO_ACK (B code: declined / refused), 7 / 8 PROPOSE_CANCEL(ED),
/// 9 START_CMD (B partner handle), 11 CANCEL_ACK, 12 CANCEL_CMD (the partner cancelled),
/// 14 / 18 / 22 / 26 / 32 UPBOARD / DOWNBOARD / CENBOARDING / BOARDLOCK / DECIDE fail (B code),
/// 15 UPBOARD_ACK (B bag slot, C board slot), 16 OPPOSITUPBOARD_CMD (B board slot, C item id, X lot),
/// 19 DOWNBOARD_ACK / 20 OPPOSITDOWNBOARD_CMD (B board slot), 23 CENBOARDING_ACK / 24 OPPOSITCENBOARDING_CMD (C money),
/// 27 BOARDLOCK_ACK, 28 OPPOSITBOARDLOCK_CMD, 29 BOARDUNLOCK_CMD, 30 OPPOSITBOARDUNLOCK_CMD, 33 DECIDE_ACK,
/// 34 OPPOSITDECIDE_CMD, 35 TRADEFAIL_CMD (B code when sent), 36 TRADECOMPLETE_CMD. Text = the frame as hex (for the log:
/// trade frames are rare and the shapes past the first fields are not all confirmed).</summary>
public sealed class TradeDecoder : IPacketDecoder
{
    /// <summary>Department of the trade packets.</summary>
    public const int Dept = 19;
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = Enumerable.Range(1, 36).Select(c => (ushort)((Dept << 10) | c)).ToArray();

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var cmd = op & 0x3FF;
        var e = InboundEvent.Of(InboundKind.TradeEvent, op, at);
        e.A = cmd;
        switch (cmd)
        {
            case 2 or 4 or 9 or 14 or 18 or 22 or 26 or 32 or 35: e.B = p.Length >= 2 ? Rd.U16(p, 0) : -1; break;
            case 15: e.B = p.Length >= 1 ? p[0] : -1; e.C = p.Length >= 2 ? p[1] : -1; break;
            case 16:   // {board u8, SHINE_ITEM_STRUCT {itemid u16, attr}}
                e.B = p.Length >= 1 ? p[0] : -1; e.C = p.Length >= 3 ? Rd.U16(p, 1) : -1;
                e.X = p.Length switch { 4 => p[3], 5 => (uint)(p[3] | (p[4] << 8)), _ => 1 };
                break;
            case 19 or 20: e.B = p.Length >= 1 ? p[0] : -1; break;
            case 23 or 24: e.C = Rd.U64(p, 0); break;
        }
        e.Text = Convert.ToHexString(p.Length > 24 ? p[..24] : p);
        sink.TryWrite(e);
    }
}

/// <summary>Production (checked on a capture: PRODUCTFIELD_REQ {main skill} -> PRODUCTFIELD_ACK {main skill, 0x0B01};
/// PRODUCE_CAST_REQ {product id} -> SKILLEXP_CLIENT {skill, mastery total}, cell changes, PRODUCE_MAKE {item, 0x0B01}).</summary>
public sealed class ProduceDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x4823, 0x203B, 0x2036, 0x481D, 0x2028, 0x202B, 0x203D];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Unknown, op, at);
        switch (op)
        {
            case 0x4823: if (p.Length < 4) return; e.Kind = InboundKind.ProduceField; e.A = Rd.U16(p, 0); e.B = Rd.U16(p, 2); break;
            case 0x203B: if (p.Length < 4) return; e.Kind = InboundKind.ProduceMade; e.A = Rd.U16(p, 0); e.B = Rd.U16(p, 2); break;
            case 0x2036: e.Kind = InboundKind.ProduceFail; e.A = p.Length >= 2 ? Rd.U16(p, 0) : -1; break;
            case 0x481D: if (p.Length < 6) return; e.Kind = InboundKind.SkillExp; e.A = Rd.U16(p, 0); e.B = Rd.U32(p, 2); break;
            // the rest tent: PITCHTENT_ACK / FOLDTENT_ACK {code u16} (0x0A81 ok); REINFORCE_FOLDTENT_CMD = the server folded it
            case 0x2028 or 0x202B: if (p.Length < 2) return; e.Kind = InboundKind.Tent; e.A = op == 0x2028 ? 1 : 0; e.B = Rd.U16(p, 0); break;
            case 0x203D: e.Kind = InboundKind.Tent; e.A = 2; e.B = 0x0A81; break;
            default: return;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Instance scenario packets (dept 27, PDB layouts): the server asks the client to confirm a trigger area it
/// crossed (AREAENTRY_REQ {Name8}; the client answers AREAENTRY_ACK with the same name), doors (DOORSTATE {door u16,
/// state u8}; BUILDDOOR {handle, mob, coord(9), state u8 @13, block name[32] @14, scale}), objects that turn into mobs
/// or NPCs (OBJTYPECHANGE {handle, type: 5 mob, 4 npc}), timers and messages.</summary>
public sealed class ScenarioDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes { get; } = [0x6C04, 0x6C05, 0x6C07, 0x6C09, 0x6C0B, 0x6C16, 0x6C1B, 0x6C21, 0x6C22, 0x1C0F];

    /// <inheritdoc/>
    public void Decode(ushort op, ReadOnlySpan<byte> p, long at, WireProfile wire, IEventSink sink)
    {
        var e = InboundEvent.Of(InboundKind.Scenario, op, at);
        e.A = op == 0x1C0F ? 1000 : op & 0x3FF;
        switch (op)
        {
            case 0x6C05 or 0x6C07: e.Text = Ascii.Get(p.Length >= 32 ? p[..32] : p); break;
            case 0x6C09: if (p.Length < 3) return; e.Handle = Rd.U16(p, 0); e.B = p[2]; break;
            case 0x6C0B: if (p.Length < 3) return; e.Handle = Rd.U16(p, 0); e.B = p[2]; break;
            case 0x6C16: e.B = p.Length >= 2 ? Rd.U16(p, 0) : -1; break;
            case 0x6C22: e.B = p.Length >= 4 ? Rd.U32(p, 0) : -1; break;
            case 0x6C04: e.B = p.Length >= 4 ? Rd.U32(p, 0) : -1; break;
            case 0x6C1B: e.Text = p.Length >= 1 ? Ascii.Get(p.Slice(1, Math.Min(p[0], p.Length - 1))) : ""; break;
            case 0x1C0F:
                if (p.Length < 46) return;
                e.Handle = Rd.U16(p, 0); e.C = Rd.U16(p, 2); e.X = Rd.U32(p, 4); e.Y = Rd.U32(p, 8); e.B = p[13];
                e.Text = Ascii.Get(p.Slice(14, 32));
                break;
        }
        sink.TryWrite(e);
    }
}

/// <summary>Encodes typed requests into intents without allocation (inline payloads).</summary>
public static class IntentEncoder
{
    /// <summary>Opcodes the encoder uses (shared by both packet sets).</summary>
    public const ushort OpMoveRun = 0x2019, OpMoveWalk = 0x2017, OpStop = 0x2012, OpTarget = 0x2401, OpUntarget = 0x2408,
        OpChangeMode = 0x2008, OpCastObj = 0x2440, OpCastFld = 0x2441, OpBashStart = 0x242B, OpBashStop = 0x2432,
        OpItemUse = 0x3015, OpBuy = 0x3003, OpSell = 0x3006, OpNpcClick = 0x200A, OpChat = 0x2001, OpPick = 0x3009,
        OpServerMenuAck = 0x3C02, OpNpcMenuAck = 0x201D, OpEndOfTrade = 0x200B, OpQuestScriptAck = 0x4402,
        OpQuestSelectStart = 0x440F, OpRevive = 0x104E, OpHeartbeatAck = 0x0805, OpStoneHp = 0x5007, OpStoneSp = 0x5009, OpStoneBuyHp = 0x5001, OpStoneBuySp = 0x5002, OpStatPoint = 0x105C, OpEquip = 0x300F, OpPartyAllow = 0x3804, OpFriendConfirm = 0x5404, OpQuestGiveUp = 0x4407, OpQuestRewardSelect = 0x4411, OpReloc = 0x300B, OpSplit = 0x300D, OpAreaEntryAck = 0x6C06, OpProduceCast = 0x2035, OpProductField = 0x4822, OpPitchTent = 0x2027, OpFoldTent = 0x202A, OpBagSort = 0x304A, OpUpgrade = 0x3017,
        OpLoginComplete = 0x1803;

    private static Intent Make(ushort op, int action, ReadOnlySpan<byte> payload)
    {
        var i = new Intent { Opcode = op, ActionId = action };
        if (payload.Length <= 64) { payload.CopyTo(i.Inline); i.Length = (byte)payload.Length; }
        else i.Heap = payload.ToArray();
        return i;
    }

    /// <summary>MOVERUN (or MOVEWALK): from/to.</summary>
    public static Intent MoveRun(uint fx, uint fy, uint tx, uint ty, int actionId, bool walk = false)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, fx); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], fy);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], tx); BinaryPrimitives.WriteUInt32LittleEndian(b[12..], ty);
        return Make(walk ? OpMoveWalk : OpMoveRun, actionId, b);
    }
    /// <summary>STOP at a position.</summary>
    public static Intent Stop(uint x, uint y, int actionId)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, x); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], y);
        return Make(OpStop, actionId, b);
    }
    /// <summary>TARGETTING {handle}.</summary>
    public static Intent Target(ushort handle, int actionId) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, handle); return Make(OpTarget, actionId, b); }
    /// <summary>Clear the selection.</summary>
    public static Intent Untarget(int actionId) => Make(OpUntarget, actionId, default);
    /// <summary>CHANGEMODE (2 = battle).</summary>
    /// <summary>ITEM_UPGRADE_REQ {equip u8 = bag slot of the item, raw u8 = bag slot of the enhance stone, raw_left u8,
    /// raw_right u8, gift_money u32, raw_middle u8} - the three safety-stone bag slots, 0xFF for none (real-client capture).</summary>
    public static Intent Upgrade(byte itemSlot, byte stoneSlot, byte left, byte right, byte middle, int actionId)
    { Span<byte> b = [itemSlot, stoneSlot, left, right, 0, 0, 0, 0, middle]; return Make(OpUpgrade, actionId, b); }
    public static Intent ChangeMode(byte mode, int actionId) { Span<byte> b = [mode]; return Make(OpChangeMode, actionId, b); }
    /// <summary>SKILLBASH_OBJ_CAST {skill, target}.</summary>
    public static Intent CastObject(ushort skill, ushort target, int actionId)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, skill); BinaryPrimitives.WriteUInt16LittleEndian(b[2..], target);
        return Make(OpCastObj, actionId, b);
    }
    /// <summary>SKILLBASH_FLD_CAST {skill, xy}.</summary>
    public static Intent CastGround(ushort skill, uint x, uint y, int actionId)
    {
        Span<byte> b = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(b, skill); BinaryPrimitives.WriteUInt32LittleEndian(b[2..], x); BinaryPrimitives.WriteUInt32LittleEndian(b[6..], y);
        return Make(OpCastFld, actionId, b);
    }
    /// <summary>BASHSTART / BASHSTOP (empty).</summary>
    public static Intent Bash(bool start, int actionId) => Make(start ? OpBashStart : OpBashStop, actionId, default);
    /// <summary>ITEM_USE {slot, box}.</summary>
    public static Intent UseItem(byte slot, byte box, int actionId) { Span<byte> b = [slot, box]; return Make(OpItemUse, actionId, b); }
    /// <summary>ITEM_BUY {itemid u16, lot u32}.</summary>
    public static Intent Buy(ushort itemId, uint lot, int actionId)
    {
        Span<byte> b = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(b, itemId); BinaryPrimitives.WriteUInt32LittleEndian(b[2..], lot);
        return Make(OpBuy, actionId, b);
    }
    /// <summary>ITEM_SELL {slot u8, lot u32}.</summary>
    public static Intent Sell(byte slot, uint lot, int actionId)
    {
        Span<byte> b = stackalloc byte[5];
        b[0] = slot; BinaryPrimitives.WriteUInt32LittleEndian(b[1..], lot);
        return Make(OpSell, actionId, b);
    }
    /// <summary>NPCCLICK {handle}.</summary>
    public static Intent NpcClick(ushort handle, int actionId) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, handle); return Make(OpNpcClick, actionId, b); }
    /// <summary>ITEM_PICK {handle}.</summary>
    public static Intent Pick(ushort handle, int actionId) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, handle); return Make(OpPick, actionId, b); }
    /// <summary>A one-byte reply (server menu / npc menu ack).</summary>
    public static Intent Reply(ushort op, byte reply, int actionId) { Span<byte> b = [reply]; return Make(op, actionId, b); }
    /// <summary>QUEST_SCRIPT_CMD_ACK {quest u16, qsc u8, result u32}.</summary>
    public static Intent QuestScriptAck(ushort quest, byte qsc, uint result, int actionId)
    {
        Span<byte> b = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(b, quest); b[2] = qsc; BinaryPrimitives.WriteUInt32LittleEndian(b[3..], result);
        return Make(OpQuestScriptAck, actionId, b);
    }
    /// <summary>QUEST_SELECT_START {npc mob id u16, quest u16}.</summary>
    public static Intent QuestSelectStart(ushort npcMob, ushort quest, int actionId)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, npcMob); BinaryPrimitives.WriteUInt16LittleEndian(b[2..], quest);
        return Make(OpQuestSelectStart, actionId, b);
    }
    /// <summary>CHAT_REQ {links u8 = 0, len u8, text}.</summary>
    public static Intent Chat(ReadOnlySpan<char> text, int actionId)
    {
        var n = Math.Min(text.Length, 120);
        var b = new byte[2 + n];
        b[1] = (byte)n;
        for (var i = 0; i < n; i++) b[2 + i] = (byte)text[i];
        return new Intent { Opcode = OpChat, ActionId = actionId, Heap = b };
    }
    /// <summary>ITEM_RELOC_REQ {from u16, to u16}: item locations ((box &lt;&lt; 10) | slot).</summary>
    public static Intent Reloc(ushort from, ushort to, int actionId)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, from); BinaryPrimitives.WriteUInt16LittleEndian(b[2..], to);
        return Make(OpReloc, actionId, b);
    }
    /// <summary>A trade request (dept 19 command) with an optional payload: 1 PROPOSE {partner u16}, 6 yes / 3 no to an
    /// ask, 13 UPBOARD {bag slot}, 17 DOWNBOARD {board slot}, 21 CENBOARDING {money u64}, 25 BOARDLOCK, 31 DECIDE, 10 CANCEL.</summary>
    public static Intent Trade(int cmd, ReadOnlySpan<byte> payload, int actionId) => Make((ushort)((TradeDecoder.Dept << 10) | cmd), actionId, payload);
    /// <summary>ITEM_SPLIT_REQ {from u16, to u16, lot u32}: part of a stack into an empty cell (item locations).</summary>
    public static Intent Split(ushort from, ushort to, uint lot, int actionId)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, from); BinaryPrimitives.WriteUInt16LittleEndian(b[2..], to);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], lot);
        return Make(OpSplit, actionId, b);
    }
    /// <summary>Raw opcode + payload.</summary>
    public static Intent Raw(ushort opcode, ReadOnlySpan<byte> payload, int actionId) => Make(opcode, actionId, payload);
}

/// <summary>Writes every frame, both directions, to a rolling per-bot sink.</summary>
public interface IPacketLog
{
    /// <summary>Whether logging is enabled.</summary>
    bool Enabled { get; set; }
    /// <summary>Record a frame (C->S logged before encryption).</summary>
    void Record(bool outbound, ushort opcode, ReadOnlySpan<byte> payload, long at);
}
