using Fiesta.Bot.Protocol;

namespace Fiesta.Bot.Agent;

/// <summary>Action identifier (index + generation into the action table).</summary>
public readonly record struct ActionId(int Value)
{
    /// <summary>"Rejected before queueing".</summary>
    public static readonly ActionId None = new(-1);
}

/// <summary>Lifecycle of an action (API_SPEC 4.1).</summary>
public enum ActionState : byte { Queued, Sent, Acked, Refused, TimedOut, Cancelled, Failed }

/// <summary>Action kinds (API_SPEC 4.2).</summary>
public enum ActionKind : byte
{
    Cast, CastGround, Bash, StopBash, Target, Untarget, Move, MoveDirect, Follow, Stop, Mount, Dismount,
    UseItem, StoneHp, StoneSp, Loot, Buy, Sell, OpenShop, Talk, Storage, Gate, Travel, Revive, Say, Whisper,
    TradePropose, TradeOfferItem, TradeOfferMoney, TradeLock, TradeConfirm, TradeCancel, PartyInvite, PartyAccept, PartyLeave, Raw,
}

/// <summary>Resources a layer can claim (REDESIGN 5.1).</summary>
[Flags]
public enum Resource : byte { None = 0, Movement = 1, Combat = 2, Cast = 4, Inventory = 8, Npc = 16, Chat = 32, All = 63 }

/// <summary>Local refusal codes (`act.L_*`).</summary>
public enum LocalRefusal : ushort
{
    None = 0, LinkDown = 0xF001, Preempted, UnknownSkill, NotReady, OutOfRange, Moving, Mounted, Busy, QueueFull, InvalidTarget,
}

/// <summary>Stored state of one action.</summary>
public struct ActionRecord
{
    /// <summary>Kind.</summary>
    public ActionKind Kind;
    /// <summary>State.</summary>
    public ActionState State;
    /// <summary>Server refusal code or a <see cref="LocalRefusal"/>; 0 = ok.</summary>
    public ushort Code;
    /// <summary>Layer that issued it.</summary>
    public byte Layer;
    /// <summary>Timestamps (ms).</summary>
    public long QueuedAt, SentAt, ResolvedAt, Deadline;
    /// <summary>Arguments for matching (skill, target, item...).</summary>
    public long Arg0, Arg1;
}

/// <summary>Decides whether an inbound event resolves a pending action.</summary>
public interface IResultMatcher
{
    /// <summary>Return the new state if <paramref name="evt"/> resolves <paramref name="action"/>, else null.</summary>
    (ActionState State, ushort Code)? Match(in ActionRecord action, in InboundEvent evt);
}

/// <summary>Static description of an action kind: matcher, timeout, resource.</summary>
public sealed record ActionSpec(ActionKind Kind, Resource Resource, IResultMatcher Matcher, TimeSpan BaseTimeout);

/// <summary>The table of every action kind; an action kind without a matcher cannot be registered.</summary>
public static class ActionCatalog
{
    /// <summary>Spec of a kind.</summary>
    public static ActionSpec Get(ActionKind kind) => throw new NotImplementedException();
    /// <summary>All specs.</summary>
    public static IReadOnlyList<ActionSpec> All => throw new NotImplementedException();
}

/// <summary>Creates actions, tracks them, resolves them from inbound events, times them out. Tick thread only.</summary>
public sealed class ActionSystem
{
    /// <summary>Create for one bot.</summary>
    public ActionSystem(OutboundRing outbound, int capacity = 1024) => throw new NotImplementedException();
    /// <summary>Queue an action with its intents; returns <see cref="ActionId.None"/> with a refusal when it cannot be queued.</summary>
    public ActionId Issue(ActionKind kind, byte layer, long arg0, long arg1, ReadOnlySpan<Intent> intents) => throw new NotImplementedException();
    /// <summary>Queue a locally refused action so the brain sees a handle with the reason.</summary>
    public ActionId Refuse(ActionKind kind, byte layer, LocalRefusal reason) => throw new NotImplementedException();
    /// <summary>Record of an action.</summary>
    public ref readonly ActionRecord Get(ActionId id) => throw new NotImplementedException();
    /// <summary>Match an inbound event against pending actions.</summary>
    public void Apply(in InboundEvent evt, BrainEventBus events) => throw new NotImplementedException();
    /// <summary>Time out stale actions (tick step 3).</summary>
    public void Advance(long nowMs, BrainEventBus events) => throw new NotImplementedException();
    /// <summary>Cancel if possible.</summary>
    public bool Cancel(ActionId id) => throw new NotImplementedException();
    /// <summary>Newest unresolved action of a kind.</summary>
    public ActionId Pending(ActionKind kind) => throw new NotImplementedException();
    /// <summary>Reason of the last Issue that returned None.</summary>
    public LocalRefusal LastRejection => throw new NotImplementedException();
    /// <summary>Resource claims per layer (set by the brain each tick).</summary>
    public void SetClaims(byte layer, Resource claims) => throw new NotImplementedException();
    /// <summary>Is a resource claimed by a layer above <paramref name="layer"/>?</summary>
    public bool Preempted(byte layer, Resource resource) => throw new NotImplementedException();
}

/// <summary>Procedure kinds (multi-packet interactions).</summary>
public enum ProcedureKind : byte { OpenShop, QuestDialogue, GateHop, Dismount, Revive, Trade, Tent, Storage, Travel }

/// <summary>Procedure outcome.</summary>
public enum ProcedureStatus : byte { Running, Succeeded, Failed, Cancelled }

/// <summary>A resumable C# state machine stepped once per tick; it never waits.</summary>
public interface IProcedure
{
    /// <summary>Kind.</summary>
    ProcedureKind Kind { get; }
    /// <summary>Current step name (for the debugger).</summary>
    string Step { get; }
    /// <summary>Status.</summary>
    ProcedureStatus Status { get; }
    /// <summary>Failure reason when failed.</summary>
    string? FailReason { get; }
    /// <summary>Advance: look at new events/state, issue intents, update status.</summary>
    void Advance(AgentContext ctx);
    /// <summary>Feed one inbound event.</summary>
    void Apply(in InboundEvent evt);
    /// <summary>Abort.</summary>
    void Cancel();
}

/// <summary>Base for procedures: timeouts per step, status plumbing.</summary>
public abstract class ProcedureBase : IProcedure
{
    /// <inheritdoc/>
    public abstract ProcedureKind Kind { get; }
    /// <inheritdoc/>
    public string Step { get; protected set; } = "start";
    /// <inheritdoc/>
    public ProcedureStatus Status { get; protected set; }
    /// <inheritdoc/>
    public string? FailReason { get; protected set; }
    /// <inheritdoc/>
    public abstract void Advance(AgentContext ctx);
    /// <inheritdoc/>
    public abstract void Apply(in InboundEvent evt);
    /// <inheritdoc/>
    public void Cancel() => throw new NotImplementedException();
    /// <summary>Fail the procedure with a reason.</summary>
    protected void Fail(string reason) => throw new NotImplementedException();
}

/// <summary>END_OF_TRADE -> NPCCLICK -> menu -> shop list.</summary>
public sealed class OpenShopProcedure(int npcHandle, byte menuOption) : ProcedureBase
{
    /// <summary>NPC handle.</summary>
    public int NpcHandle { get; } = npcHandle;
    /// <summary>Menu option to answer.</summary>
    public byte MenuOption { get; } = menuOption;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.OpenShop;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Quest dialogue page by page ([NEXT], [MENU], [BUTTON], [SHOW_REWARD]).</summary>
public sealed class QuestDialogueProcedure(int npcHandle, int questId, int rewardIndex) : ProcedureBase
{
    /// <summary>NPC handle.</summary>
    public int NpcHandle { get; } = npcHandle;
    /// <summary>Quest id.</summary>
    public int QuestId { get; } = questId;
    /// <summary>Reward choice (-1 none).</summary>
    public int RewardIndex { get; } = rewardIndex;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.QuestDialogue;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Dismount -> walk into gate range -> click -> answer -> await the map link.</summary>
public sealed class GateHopProcedure(int gateHandle, string? destMap) : ProcedureBase
{
    /// <summary>Gate handle.</summary>
    public int GateHandle { get; } = gateHandle;
    /// <summary>Expected destination map.</summary>
    public string? DestMap { get; } = destMap;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.GateHop;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Use the mount item and wait for the ride-off event (no fixed sleeps).</summary>
public sealed class DismountProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Dismount;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Revive request and the same-map / other-map revive paths.</summary>
public sealed class ReviveProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Revive;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Propose/accept -> board items/money -> lock -> confirm.</summary>
public sealed class TradeProcedure(int partnerHandle) : ProcedureBase
{
    /// <summary>Partner handle.</summary>
    public int PartnerHandle { get; } = partnerHandle;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Trade;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Find a clear spot (nav.clearSpot) -> walk -> pitch -> rest -> fold.</summary>
public sealed class TentProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Tent;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Open the storage keeper and move items.</summary>
public sealed class StorageProcedure(int keeperHandle) : ProcedureBase
{
    /// <summary>Keeper handle.</summary>
    public int KeeperHandle { get; } = keeperHandle;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Storage;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Multi-map travel: a chain of GateHop procedures along a planned map route.</summary>
public sealed class TravelProcedure(string destMap) : ProcedureBase
{
    /// <summary>Destination map.</summary>
    public string DestMap { get; } = destMap;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Travel;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Runs the bot's active procedures (at most one per resource).</summary>
public sealed class ProcedureRunner
{
    /// <summary>Start a procedure bound to an action.</summary>
    public ActionId Start(IProcedure procedure, ActionKind kind, byte layer) => throw new NotImplementedException();
    /// <summary>Advance all active procedures.</summary>
    public void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Feed an inbound event to active procedures.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
    /// <summary>Active procedures (for the debugger).</summary>
    public IReadOnlyList<IProcedure> Active => throw new NotImplementedException();
}
