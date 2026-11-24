using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Agent;

/// <summary>Brain-visible event kinds (API_SPEC section 3; the Lua `ev.*` constants).</summary>
public enum EventKind : byte
{
    Damaged, Hit, TargetDied, Kill, AggroGained, AggroLost, EntityAppeared, EntityLeft, HpChanged, SpChanged,
    LevelUp, MapChanged, MoveFailed, Arrived, CastResult, ActionResolved, ProcedureDone, JobDone,
    QuestProgress, QuestDone, QuestAccepted, ItemGained, ItemLost, BagFull, MoneyChanged, Chat, Whisper,
    PartyInvite, TradeAsked, Died, Revived, Stuck, Budget, Operator,
}

/// <summary>One brain event.</summary>
public readonly record struct BrainEvent(EventKind Kind, long Tick, long Arg1, long Arg2);

/// <summary>Per-tick event bitset plus a small ring per kind (for ev.fired / ev.count / ev.last).</summary>
public sealed class BrainEventBus
{
    /// <summary>Raise an event in the current tick.</summary>
    public void Raise(EventKind kind, long arg1 = -1, long arg2 = -1) => throw new NotImplementedException();
    /// <summary>Did the kind fire this tick?</summary>
    public bool Fired(EventKind kind) => throw new NotImplementedException();
    /// <summary>Occurrences in the last ms.</summary>
    public int Count(EventKind kind, long withinMs, long nowMs) => throw new NotImplementedException();
    /// <summary>Most recent occurrence, null none.</summary>
    public BrainEvent? Last(EventKind kind) => throw new NotImplementedException();
    /// <summary>Start a new tick (clears the bitset).</summary>
    public void BeginTick(long tick, long nowMs) => throw new NotImplementedException();
}

/// <summary>Bounded single-producer/single-consumer ring (I/O thread -> tick). Never blocks.</summary>
public sealed class InboundRing : IEventSink
{
    /// <summary>Create with a power-of-two capacity.</summary>
    public InboundRing(int capacity = 4096) => throw new NotImplementedException();
    /// <inheritdoc/>
    public bool TryWrite(in InboundEvent evt) => throw new NotImplementedException();
    /// <summary>Consumer side: take one.</summary>
    public bool TryRead(out InboundEvent evt) => throw new NotImplementedException();
    /// <summary>Events dropped because the ring was full.</summary>
    public long Dropped => throw new NotImplementedException();
}

/// <summary>Outbound ring (tick -> I/O writer).</summary>
public sealed class OutboundRing : Fiesta.Bot.Net.IOutboundSource
{
    /// <summary>Create with a capacity.</summary>
    public OutboundRing(int capacity = 1024) => throw new NotImplementedException();
    /// <summary>Tick side: enqueue (false = full or link down).</summary>
    public bool TryEnqueue(in Intent intent) => throw new NotImplementedException();
    /// <inheritdoc/>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public bool TryDequeue(out ushort opcode, out ReadOnlyMemory<byte> payload) => throw new NotImplementedException();
    /// <summary>Whether the link can accept intents.</summary>
    public bool LinkUp { get; set; }
}

/// <summary>Operator commands drained at the start of a tick.</summary>
public enum ControlKind : byte { Pause, Resume, Step, ForceState, SetBlackboard, Action, Stop, Relog, ApplyVersion }

/// <summary>One operator command.</summary>
public sealed record ControlCommand(ControlKind Kind, string? Layer = null, string? State = null, string? Key = null,
    string? Value = null, int Count = 1, string? VersionHash = null, string By = "operator");

/// <summary>Thread-safe queue of operator commands (HTTP -> tick).</summary>
public sealed class ControlQueue
{
    /// <summary>Post a command (any thread).</summary>
    public void Post(ControlCommand command) => throw new NotImplementedException();
    /// <summary>Tick side: take one.</summary>
    public bool TryTake(out ControlCommand? command) => throw new NotImplementedException();
}

/// <summary>Per-tick timing and allocation, recorded every tick.</summary>
public struct TickSample
{
    /// <summary>Tick number.</summary>
    public long Tick;
    /// <summary>Step durations in microseconds.</summary>
    public float TotalUs, ControlUs, ApplyUs, ActionsUs, MoveUs, CombatUs, BrainUs, FlushUs, PublishUs;
    /// <summary>Managed bytes allocated by this tick.</summary>
    public long AllocBytes;
    /// <summary>Counts.</summary>
    public int Events, Intents;
    /// <summary>Lua instructions executed.</summary>
    public long LuaInstructions;
}

/// <summary>HDR-style histograms of tick samples (1 s / 1 m / 10 m windows).</summary>
public sealed class TickStats
{
    /// <summary>Record one tick.</summary>
    public void Record(in TickSample sample) => throw new NotImplementedException();
    /// <summary>Quantile of a step in a window (microseconds).</summary>
    public double Quantile(string step, TimeSpan window, double q) => throw new NotImplementedException();
    /// <summary>Quantile of allocation bytes in a window.</summary>
    public double AllocQuantile(TimeSpan window, double q) => throw new NotImplementedException();
    /// <summary>The last n samples, newest last.</summary>
    public int Last(Span<TickSample> dest) => throw new NotImplementedException();
}

/// <summary>Hook the brain runtime implements; the agent calls it in tick step 6.</summary>
public interface IBrainTicker
{
    /// <summary>Run the brain for this tick.</summary>
    void Tick(AgentContext ctx);
    /// <summary>Lua instructions executed in the last tick.</summary>
    long LastInstructions { get; }
}

/// <summary>Everything a tick step or helper module may touch for one bot (tick thread only).</summary>
public sealed class AgentContext
{
    /// <summary>Bot index in the fleet (stable for the process).</summary>
    public int BotIndex { get; init; }
    /// <summary>Bot id.</summary>
    public string BotId { get; init; } = "";
    /// <summary>World state.</summary>
    public WorldState World { get; init; } = new();
    /// <summary>Events.</summary>
    public BrainEventBus Events { get; init; } = new();
    /// <summary>Actions.</summary>
    public ActionSystem Actions { get; init; } = null!;
    /// <summary>Movement.</summary>
    public MoveController Move { get; init; } = null!;
    /// <summary>Combat.</summary>
    public CombatController Combat { get; init; } = null!;
    /// <summary>Client data.</summary>
    public IClientData Data { get; init; } = null!;
    /// <summary>Navigation.</summary>
    public INavService Nav { get; init; } = null!;
    /// <summary>Per-bot nav overlay.</summary>
    public NavOverlay Overlay { get; init; } = new();
    /// <summary>Jobs.</summary>
    public IJobScheduler Jobs { get; init; } = null!;
    /// <summary>Knowledge.</summary>
    public IKnowledgeStore Knowledge { get; init; } = null!;
    /// <summary>Tick start time (ms).</summary>
    public long Now { get; internal set; }
    /// <summary>Tick counter.</summary>
    public long Tick { get; internal set; }
    /// <summary>Lazily created per-bot scratch buffer for query results (reused, never shrinks).</summary>
    public Span<int> Scratch(int minLength) => throw new NotImplementedException();
}

/// <summary>One bot: owns its state; ticked by exactly one worker; never awaits.</summary>
public sealed class BotAgent
{
    /// <summary>Create an agent around its context and rings.</summary>
    public BotAgent(AgentContext context, InboundRing inbound, OutboundRing outbound, ControlQueue control, IBrainTicker brain, IClock clock)
        => throw new NotImplementedException();
    /// <summary>The context.</summary>
    public AgentContext Context => throw new NotImplementedException();
    /// <summary>Tick statistics.</summary>
    public TickStats Stats => throw new NotImplementedException();
    /// <summary>Paused by the operator (controllers safe-stopped, link kept).</summary>
    public bool Paused => throw new NotImplementedException();
    /// <summary>Run one tick (steps 1-9 of REDESIGN 2.3).</summary>
    public void Tick() => throw new NotImplementedException();
    /// <summary>Latest published snapshot (immutable, any thread).</summary>
    public AgentSnapshot Snapshot => throw new NotImplementedException();
}

/// <summary>Immutable view published every 100 ms for HTTP/UI readers.</summary>
public sealed record AgentSnapshot(string BotId, long Tick, DateTime At, string Map, double X, double Y, int Level, double Hp,
    double MaxHp, double Sp, double MaxSp, long Money, int BagFree, bool Dead, bool InCombat);
