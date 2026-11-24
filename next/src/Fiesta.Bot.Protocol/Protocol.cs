using Fiesta.Bot.Net;

namespace Fiesta.Bot.Protocol;

/// <summary>Kinds of decoded inbound events applied to the world state.</summary>
public enum InboundKind : ushort
{
    Unknown, EntityAppeared, EntityLeft, EntityMoved, EntityStopped, EntityHp, EntityDied, EntityTarget,
    SelfHp, SelfSp, SelfStats, SelfLevel, SelfExp, SelfMoney, SelfSpeed, SelfAbstate, SelfMode,
    Damage, SkillCastStart, SkillResult, CastFail, BashStop, MoveFail,
    ItemCell, ItemUseResult, BuySellAck, StoneReserve,
    QuestProgress, QuestState, QuestDialog, NpcMenu, ShopList,
    PartyEvent, TradeEvent, Chat, Whisper, MapLink, Revive, DeadMenu, Door, TentAck, JobCompleted, Raw,
}

/// <summary>One decoded inbound event (64 bytes, stored in rings). Variable data lives in a pooled buffer.</summary>
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
    /// <summary>Pooled payload for variable-size kinds (lists, text), default when none.</summary>
    public ReadOnlyMemory<byte> Payload;
}

/// <summary>One outbound request produced by the tick (opcode + inline payload or pooled bytes).</summary>
public struct Intent
{
    /// <summary>Wire opcode.</summary>
    public ushort Opcode;
    /// <summary>Action this intent belongs to (-1 none).</summary>
    public int ActionId;
    /// <summary>Encoded payload (pooled, owned by the outbound ring until sent).</summary>
    public ReadOnlyMemory<byte> Payload;
}

/// <summary>Decodes the frames of one packet family into <see cref="InboundEvent"/>s.</summary>
public interface IPacketDecoder
{
    /// <summary>Opcodes this decoder handles.</summary>
    IReadOnlyList<ushort> Opcodes { get; }
    /// <summary>Decode one frame; writes 0..n events into the sink.</summary>
    void Decode(in RawFrame frame, IEventSink sink);
}

/// <summary>Receiver for decoded events (the bot's inbound ring).</summary>
public interface IEventSink
{
    /// <summary>Append one event; false when the ring is full (counted, never blocks).</summary>
    bool TryWrite(in InboundEvent evt);
}

/// <summary>Opcode -> decoder table, built once at startup.</summary>
public sealed class DecoderTable
{
    /// <summary>Build from all decoders in this assembly.</summary>
    public static DecoderTable CreateDefault() => throw new NotImplementedException();
    /// <summary>Register a decoder for its opcodes.</summary>
    public void Register(IPacketDecoder decoder) => throw new NotImplementedException();
    /// <summary>Decode a frame with its registered decoder (unknown opcodes become Raw events when logging is on).</summary>
    public void Dispatch(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Encodes typed requests into intents without allocation (pooled buffers).</summary>
public static class IntentEncoder
{
    /// <summary>NC_ACT_MOVERUN: from/to.</summary>
    public static Intent MoveRun(uint fx, uint fy, uint tx, uint ty, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ACT_STOP at a position.</summary>
    public static Intent Stop(uint x, uint y, int actionId) => throw new NotImplementedException();
    /// <summary>NC_BAT_TARGETTING.</summary>
    public static Intent Target(ushort handle, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ACT_CHANGEMODE (battle mode).</summary>
    public static Intent ChangeMode(byte mode, int actionId) => throw new NotImplementedException();
    /// <summary>NC_BAT_SKILLBASH_OBJ_CAST.</summary>
    public static Intent CastObject(ushort skill, ushort target, int actionId) => throw new NotImplementedException();
    /// <summary>NC_BAT_SKILLBASH_FLD_CAST.</summary>
    public static Intent CastGround(ushort skill, uint x, uint y, int actionId) => throw new NotImplementedException();
    /// <summary>NC_BAT_BASHSTART / BASHSTOP.</summary>
    public static Intent Bash(bool start, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ITEM_USE.</summary>
    public static Intent UseItem(byte slot, byte box, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ITEM_BUY.</summary>
    public static Intent Buy(ushort itemId, uint lot, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ITEM_SELL.</summary>
    public static Intent Sell(byte slot, uint lot, int actionId) => throw new NotImplementedException();
    /// <summary>NC_ACT_NPCCLICK.</summary>
    public static Intent NpcClick(ushort handle, int actionId) => throw new NotImplementedException();
    /// <summary>Chat line (local).</summary>
    public static Intent Chat(ReadOnlySpan<char> text, int actionId) => throw new NotImplementedException();
    /// <summary>Raw opcode + payload (protocol experiments).</summary>
    public static Intent Raw(ushort opcode, ReadOnlySpan<byte> payload, int actionId) => throw new NotImplementedException();
}

/// <summary>Packet family decoders (each a small pure class; split out of today's ZoneView).</summary>
public sealed class EntityDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Combat: damage, cast start/result/fail, bash stop.</summary>
public sealed class CombatDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Self: HP/SP, stats, level, exp, money, speed, abstates, mode.</summary>
public sealed class SelfDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Movement: moves, stops, MOVEFAIL, speed.</summary>
public sealed class MovementDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Items: cell changes, use results, buy/sell acks, stone reserves.</summary>
public sealed class ItemDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Quests, dialogue pages, NPC menus, shop lists.</summary>
public sealed class QuestMenuDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Party, trade, chat, whisper.</summary>
public sealed class SocialDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Map links, revive, dead menu, doors, tent.</summary>
public sealed class WorldDecoder : IPacketDecoder
{
    /// <inheritdoc/>
    public IReadOnlyList<ushort> Opcodes => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Decode(in RawFrame frame, IEventSink sink) => throw new NotImplementedException();
}

/// <summary>Writes every frame, both directions, to a rolling per-bot file (survives relogs).</summary>
public interface IPacketLog
{
    /// <summary>Whether logging is enabled (persisted per bot).</summary>
    bool Enabled { get; set; }
    /// <summary>Record a frame (C->S logged before encryption).</summary>
    void Record(bool outbound, ushort opcode, ReadOnlySpan<byte> payload, long at);
}
