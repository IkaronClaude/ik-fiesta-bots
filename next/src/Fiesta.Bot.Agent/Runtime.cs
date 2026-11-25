using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Agent;

/// <summary>Brain-visible event kinds (API_SPEC section 3; the Lua `ev.*` constants).</summary>
public enum EventKind : byte
{
    Damaged, Hit, TargetDied, Kill, AggroGained, AggroLost, EntityAppeared, EntityLeft, HpChanged, SpChanged,
    LevelUp, MapChanged, MoveFailed, Arrived, CastResult, ActionResolved, ProcedureDone, JobDone,
    QuestProgress, QuestDone, QuestAccepted, ItemGained, ItemLost, BagFull, MoneyChanged, Chat, Whisper,
    PartyInvite, TradeAsked, Died, Revived, Stuck, Budget, Operator, TradeStarted, TradeEnded, Promoted,
}

/// <summary>One brain event.</summary>
public readonly record struct BrainEvent(EventKind Kind, long Tick, long Arg1, long Arg2, long At);

/// <summary>Per-tick event bitset plus a small ring per kind (for ev.fired / ev.count / ev.last). The bitset of a
/// tick covers events raised since the previous tick's brain step (inbound events, controllers, the brain itself).</summary>
public sealed class BrainEventBus
{
    private const int Kinds = 64, Ring = 64;
    private ulong _fired, _pendingNext;
    private readonly BrainEvent[] _ring = new BrainEvent[Kinds * Ring];
    private readonly int[] _head = new int[Kinds];
    private readonly long[] _total = new long[Kinds];
    private long _tick, _now;

    /// <summary>Raise an event in the current tick.</summary>
    public void Raise(EventKind kind, long arg1 = -1, long arg2 = -1)
    {
        var k = (int)kind;
        _fired |= 1UL << k;
        var h = _head[k];
        _ring[k * Ring + h] = new BrainEvent(kind, _tick, arg1, arg2, _now);
        _head[k] = (h + 1) % Ring;
        _total[k]++;
    }
    /// <summary>Did the kind fire this tick?</summary>
    public bool Fired(EventKind kind) => (_fired & (1UL << (int)kind)) != 0;
    /// <summary>The fired bitset.</summary>
    public ulong Mask => _fired;
    /// <summary>Occurrences in the last ms.</summary>
    public int Count(EventKind kind, long withinMs, long nowMs)
    {
        var k = (int)kind; var n = 0;
        for (var i = 0; i < Ring; i++)
        {
            var e = _ring[k * Ring + i];
            if (e.Tick > 0 && nowMs - e.At <= withinMs) n++;
        }
        return n;
    }
    /// <summary>Total raised (lifetime).</summary>
    public long Total(EventKind kind) => _total[(int)kind];
    /// <summary>Most recent occurrence, null none.</summary>
    public BrainEvent? Last(EventKind kind)
    {
        var k = (int)kind;
        if (_total[k] == 0) return null;
        return _ring[k * Ring + (_head[k] + Ring - 1) % Ring];
    }
    /// <summary>Start a new tick (clears the bitset).</summary>
    public void BeginTick(long tick, long nowMs) { _tick = tick; _now = nowMs; _fired = 0; }
}

/// <summary>Bounded single-producer/single-consumer ring (I/O thread -> tick) plus a multi-producer lane for job
/// completions. Never blocks; a full ring drops the event and counts it.</summary>
public sealed class InboundRing : IEventSink
{
    private readonly InboundEvent[] _buf;
    private readonly int _mask;
    private long _head, _tail;   // head: next write (producer), tail: next read (consumer)
    private readonly ConcurrentQueue<InboundEvent> _lane = new();
    private long _dropped;

    /// <summary>Create with a power-of-two capacity.</summary>
    public InboundRing(int capacity = 4096)
    {
        if ((capacity & (capacity - 1)) != 0) throw new ArgumentException("capacity must be a power of two");
        _buf = new InboundEvent[capacity]; _mask = capacity - 1;
    }
    /// <inheritdoc/>
    public bool TryWrite(in InboundEvent evt)
    {
        var h = _head;
        if (h - Volatile.Read(ref _tail) >= _buf.Length) { Interlocked.Increment(ref _dropped); return false; }
        _buf[h & _mask] = evt;
        Volatile.Write(ref _head, h + 1);
        return true;
    }
    /// <summary>Second producer lane (job threads).</summary>
    public void Post(in InboundEvent evt) => _lane.Enqueue(evt);
    /// <summary>Consumer side: take one.</summary>
    public bool TryRead(out InboundEvent evt)
    {
        var t = _tail;
        if (t < Volatile.Read(ref _head))
        {
            evt = _buf[t & _mask];
            _buf[t & _mask] = default;
            Volatile.Write(ref _tail, t + 1);
            return true;
        }
        return _lane.TryDequeue(out evt);
    }
    /// <summary>Events dropped because the ring was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    /// <summary>Queued now.</summary>
    public long Depth => Volatile.Read(ref _head) - Volatile.Read(ref _tail);
}

/// <summary>Outbound ring (tick -> I/O writer): SPSC array of intents and a doorbell the writer awaits.</summary>
public sealed class OutboundRing : Fiesta.Bot.Net.IOutboundSource
{
    private readonly Intent[] _buf;
    private readonly int _mask;
    private long _head, _tail;
    private readonly Channel<byte> _bell = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private byte[] _scratch = new byte[256];
    /// <summary>Sent (dequeued) count.</summary>
    public long Sent;
    /// <summary>Tap for every dequeued frame (packet capture, intent recording).</summary>
    public Action<ushort, ReadOnlyMemory<byte>>? Tap { get; set; }

    /// <summary>Create with a power-of-two capacity.</summary>
    public OutboundRing(int capacity = 1024) { _buf = new Intent[capacity]; _mask = capacity - 1; }
    /// <summary>Free slots.</summary>
    public int Free => _buf.Length - (int)(_head - Volatile.Read(ref _tail));
    /// <summary>Tick side: enqueue (false = full or link down).</summary>
    public bool TryEnqueue(in Intent intent)
    {
        if (!LinkUp) return false;
        var h = _head;
        if (h - Volatile.Read(ref _tail) >= _buf.Length) return false;
        _buf[h & _mask] = intent;
        Volatile.Write(ref _head, h + 1);
        _bell.Writer.TryWrite(1);
        return true;
    }
    /// <inheritdoc/>
    [OffTick]
    public async ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _tail) >= Volatile.Read(ref _head))
        {
            if (!await _bell.Reader.WaitToReadAsync(ct)) return false;
            _bell.Reader.TryRead(out _);
        }
        return true;
    }
    /// <inheritdoc/>
    public bool TryDequeue(out ushort opcode, out ReadOnlyMemory<byte> payload)
    {
        var t = _tail;
        if (t >= Volatile.Read(ref _head)) { opcode = 0; payload = default; return false; }
        ref var i = ref _buf[t & _mask];
        opcode = i.Opcode;
        var p = i.Payload;
        if (_scratch.Length < p.Length) _scratch = new byte[p.Length * 2];
        p.CopyTo(_scratch);
        payload = _scratch.AsMemory(0, p.Length);
        i.Heap = null;
        Volatile.Write(ref _tail, t + 1);
        Sent++;
        Tap?.Invoke(opcode, payload);
        return true;
    }
    /// <summary>Whether the link can accept intents.</summary>
    public bool LinkUp { get; set; }
    /// <summary>Drop everything queued (link change).</summary>
    public void Clear() { Volatile.Write(ref _tail, Volatile.Read(ref _head)); }
}

/// <summary>Operator commands drained at the start of a tick.</summary>
public enum ControlKind : byte { Pause, Resume, Step, ForceState, SetBlackboard, Action, Stop, Relog, ApplyVersion }

/// <summary>One operator command.</summary>
public sealed record ControlCommand(ControlKind Kind, string? Layer = null, string? State = null, string? Key = null,
    string? Value = null, int Count = 1, string? VersionHash = null, string By = "operator")
{
    /// <summary>Scope (blackboard edits).</summary>
    public string? Scope { get; init; }
    /// <summary>Action arguments (operator actions).</summary>
    public IReadOnlyDictionary<string, string>? Args { get; init; }
    /// <summary>Completion for the poster (HTTP returns the result).</summary>
    public TaskCompletionSource<object?>? Reply { get; init; }
    /// <summary>A read run on the tick thread (HTTP inspection of world state without races).</summary>
    public Func<AgentContext, object?>? Query { get; init; }
}

/// <summary>Thread-safe queue of operator commands (HTTP -> tick).</summary>
public sealed class ControlQueue
{
    private readonly ConcurrentQueue<ControlCommand> _q = new();
    /// <summary>Post a command (any thread).</summary>
    public void Post(ControlCommand command) => _q.Enqueue(command);
    /// <summary>Tick side: take one.</summary>
    public bool TryTake(out ControlCommand? command) => _q.TryDequeue(out command);
}

/// <summary>Per-tick timing and allocation, recorded every tick.</summary>
public struct TickSample
{
    /// <summary>Tick number.</summary>
    public long Tick;
    /// <summary>Tick start (ms).</summary>
    public long At;
    /// <summary>Step durations in microseconds.</summary>
    public float TotalUs, ControlUs, ApplyUs, ActionsUs, MoveUs, CombatUs, BrainUs, FlushUs, PublishUs;
    /// <summary>Managed bytes allocated by this tick (steps 1-7; the debug publishing step is counted apart).</summary>
    public long AllocBytes;
    /// <summary>Managed bytes allocated by the publish step (snapshot, debug frame for the UI).</summary>
    public long PublishAllocBytes;
    /// <summary>Counts.</summary>
    public int Events, Intents;
    /// <summary>Lua instructions executed.</summary>
    public long LuaInstructions;
}

/// <summary>Tick statistics: the last samples (ring) and log-bucket histograms per minute (10 minutes kept).</summary>
public sealed class TickStats
{
    private readonly TickSample[] _ring = new TickSample[1200];
    private long _n;
    private const int Buckets = 48;   // bucket b covers [2^(b/4), 2^((b+1)/4)) microseconds / bytes
    private readonly long[,] _total = new long[10, Buckets], _brain = new long[10, Buckets], _alloc = new long[10, Buckets];
    private readonly long[] _minuteOf = new long[10];
    /// <summary>Ticks recorded.</summary>
    public long Count => _n;
    /// <summary>Max tick (us) seen.</summary>
    public float MaxUs { get; private set; }
    /// <summary>Ticks over budget (1 ms).</summary>
    public long OverBudget { get; private set; }

    private static int Bucket(double v) => v <= 1 ? 0 : Math.Min(Buckets - 1, (int)(Math.Log2(v) * 4));
    private static double Value(int b) => Math.Pow(2, (b + 0.5) / 4.0);

    /// <summary>Record one tick.</summary>
    public void Record(in TickSample s)
    {
        _ring[_n % _ring.Length] = s;
        _n++;
        var minute = s.At / 60000; var slot = (int)(minute % 10);
        if (_minuteOf[slot] != minute)
        {
            _minuteOf[slot] = minute;
            for (var b = 0; b < Buckets; b++) { _total[slot, b] = 0; _brain[slot, b] = 0; _alloc[slot, b] = 0; }
        }
        _total[slot, Bucket(s.TotalUs)]++;
        _brain[slot, Bucket(s.BrainUs)]++;
        _alloc[slot, Bucket(s.AllocBytes)]++;
        if (s.TotalUs > MaxUs) MaxUs = s.TotalUs;
        if (s.TotalUs > 1000) OverBudget++;
    }

    private double Q(long[,] h, TimeSpan window, double q, long nowMinute)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(window.TotalMinutes));
        Span<long> sum = stackalloc long[Buckets];
        long total = 0;
        for (var i = 0; i < 10; i++)
        {
            if (nowMinute - _minuteOf[i] >= minutes || _minuteOf[i] > nowMinute) continue;
            for (var b = 0; b < Buckets; b++) { sum[b] += h[i, b]; total += h[i, b]; }
        }
        if (total == 0) return 0;
        var target = q * total; long acc = 0;
        for (var b = 0; b < Buckets; b++) { acc += sum[b]; if (acc >= target) return Value(b); }
        return Value(Buckets - 1);
    }

    /// <summary>Quantile of a step ("total" or "brain") in a window (microseconds).</summary>
    public double Quantile(string step, TimeSpan window, double q)
    {
        var last = _n == 0 ? 0 : _ring[(_n - 1) % _ring.Length].At / 60000;
        return Q(step == "brain" ? _brain : _total, window, q, last);
    }
    /// <summary>Quantile of allocation bytes in a window.</summary>
    public double AllocQuantile(TimeSpan window, double q)
    {
        var last = _n == 0 ? 0 : _ring[(_n - 1) % _ring.Length].At / 60000;
        return Q(_alloc, window, q, last);
    }
    /// <summary>Exact quantile over the last samples (ring) of a selector (the benchmark uses this).</summary>
    public double RecentQuantile(Func<TickSample, double> sel, double q, int last = 1200)
    {
        var n = (int)Math.Min(Math.Min(_n, _ring.Length), last);
        if (n == 0) return 0;
        var v = new double[n];
        for (var i = 0; i < n; i++) v[i] = sel(_ring[(_n - 1 - i) % _ring.Length]);
        Array.Sort(v);
        return v[Math.Min(n - 1, (int)(q * n))];
    }
    /// <summary>The last n samples, newest last.</summary>
    public int Last(Span<TickSample> dest)
    {
        var n = (int)Math.Min(Math.Min(_n, _ring.Length), dest.Length);
        for (var i = 0; i < n; i++) dest[i] = _ring[(_n - n + i) % _ring.Length];
        return n;
    }
}

/// <summary>Hook the brain runtime implements; the agent calls it in tick step 6.</summary>
public interface IBrainTicker
{
    /// <summary>Run the brain for this tick.</summary>
    void Tick(AgentContext ctx);
    /// <summary>Lua instructions executed in the last tick.</summary>
    long LastInstructions { get; }
    /// <summary>Handle an operator command addressed to the brain (force state, blackboard, version...).</summary>
    object? Control(ControlCommand cmd, AgentContext ctx);
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
    public MoveController Move { get; init; } = new();
    /// <summary>Combat.</summary>
    public CombatController Combat { get; init; } = new();
    /// <summary>Procedures.</summary>
    public ProcedureRunner Procedures { get; init; } = new();
    /// <summary>Client data.</summary>
    public IClientData Data { get; init; } = null!;
    /// <summary>Navigation.</summary>
    public INavService Nav { get; init; } = null!;
    /// <summary>Maps whose entry gate refused this bot, until when (ms); routes avoid them meanwhile.</summary>
    public Dictionary<int, long> RefusedMaps { get; } = new();
    /// <summary>Is a map's entry gate refusing us now?</summary>
    public bool Refused(int mapId) => (RefusedMaps.TryGetValue(mapId, out var until) && Now < until) || Atlas.IsPvp(mapId);
    /// <summary>The world atlas (client map graph, NPC spots, spawn areas, learned gates).</summary>
    public WorldAtlas Atlas { get; init; } = WorldAtlas.Empty;
    /// <summary>Per-bot nav overlay.</summary>
    public NavOverlay Overlay { get; init; } = new();
    /// <summary>Jobs.</summary>
    public IJobScheduler Jobs { get; init; } = null!;
    /// <summary>Knowledge.</summary>
    public IKnowledgeStore Knowledge { get; init; } = null!;
    /// <summary>The host-wide team board (asks between our bots).</summary>
    public TeamBoard Team { get; init; } = new();
    /// <summary>Logger.</summary>
    public BotLogger Log { get; init; } = null!;
    /// <summary>The inbound ring (job completions post here).</summary>
    public InboundRing Inbound { get; init; } = null!;
    /// <summary>Tick start time (ms).</summary>
    public long Now { get; internal set; }
    /// <summary>Tick counter.</summary>
    public long Tick { get; internal set; }
    /// <summary>Learners run on every applied event.</summary>
    public List<ILearner> Learners { get; } = new();
    private int[] _scratch = new int[256];

    /// <summary>Lazily created per-bot scratch buffer for query results (reused, never shrinks).</summary>
    public Span<int> Scratch(int minLength)
    {
        if (_scratch.Length < minLength) _scratch = new int[Math.Max(minLength, _scratch.Length * 2)];
        return _scratch;
    }

    /// <summary>Post a job completion into the bot's inbound lane (job threads).</summary>
    public void PostJobDone(JobTicket t, bool ok)
    {
        var e = InboundEvent.Of(InboundKind.JobCompleted, 0, 0); e.A = t.Value; e.B = ok ? 1 : 0;
        Inbound.Post(e);
    }
}

/// <summary>One bot: owns its state; ticked by exactly one worker; never awaits.</summary>
public sealed class BotAgent
{
    private readonly InboundRing _in;
    private readonly OutboundRing _out;
    private readonly ControlQueue _control;
    private readonly IClock _clock;
    private AgentSnapshot _snapshot;
    private long _lastPublish = -1;
    private int _stepBudget;
    /// <summary>Recorder hook: every applied inbound event and tick boundary (null = off).</summary>
    public Action<long, long, InboundEvent?>? Recorder { get; set; }
    /// <summary>Supervisor hook for lifecycle commands (stop, relog) the tick cannot do itself.</summary>
    public Action<ControlCommand>? Lifecycle { get; set; }

    /// <summary>Create an agent around its context and rings.</summary>
    public BotAgent(AgentContext context, InboundRing inbound, OutboundRing outbound, ControlQueue control, IBrainTicker? brain, IClock clock)
    {
        Context = context; _in = inbound; _out = outbound; _control = control; Brain = brain; _clock = clock;
        context.Actions.Events = context.Events; context.Actions.World = context.World; context.Actions.Log = context.Log;
        context.Move.Moved = c => c.Combat.OnMoved(c);
        _snapshot = new AgentSnapshot(context.BotId, 0, clock.UtcNow, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, -1, false);
    }

    /// <summary>The context.</summary>
    public AgentContext Context { get; }
    /// <summary>Post an operator command (any thread).</summary>
    public void ControlPost(ControlCommand command) => _control.Post(command);
    /// <summary>The brain (swappable by the supervisor between ticks).</summary>
    public IBrainTicker? Brain { get; set; }
    /// <summary>Tick statistics.</summary>
    public TickStats Stats { get; } = new();
    /// <summary>Paused by the operator (controllers safe-stopped, link kept).</summary>
    public bool Paused { get; private set; }
    /// <summary>Outbound ring.</summary>
    public OutboundRing Outbound => _out;
    /// <summary>Inbound ring.</summary>
    public InboundRing Inbound => _in;
    /// <summary>Last tick time (ms), for the health monitor.</summary>
    public long LastTickAt { get; private set; } = -1;
    /// <summary>Interval between ticks (ms).</summary>
    public int IntervalMs { get; set; } = 50;
    /// <summary>Called on the tick thread at each publish (every 100 ms): the supervisor builds debug frames here.</summary>
    public Action<BotAgent>? OnPublish { get; set; }
    /// <summary>The game view tap (API_SPEC 14); null = no game view.</summary>
    public GameTap? Game { get; set; }

    private static double Us(long a, long b) => (b - a) * 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Run one tick (steps 1-9 of REDESIGN 2.3).</summary>
    public void Tick()
    {
        var c = Context;
        var t0 = Stopwatch.GetTimestamp();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var now = _clock.NowMs;
        c.Now = now; c.Tick++;
        c.Actions.Now = now;
        c.Events.BeginTick(c.Tick, now);
        Recorder?.Invoke(c.Tick, now, null);
        var s = new TickSample { Tick = c.Tick, At = now };

        // 1. control
        while (_control.TryTake(out var cmd)) HandleControl(cmd!);
        var t1 = Stopwatch.GetTimestamp();

        // 2. inbound events -> world, actions, controllers, procedures, learners
        var events = 0;
        while (events < 2048 && _in.TryRead(out var e))
        {
            events++;
            Recorder?.Invoke(c.Tick, now, e);
            if (e.Kind == InboundKind.LinkChanged) { _out.LinkUp = e.A == (long)Fiesta.Bot.Net.LinkState.Ready; if (!_out.LinkUp) _out.Clear(); }
            if (e.Kind == InboundKind.JobCompleted) c.Events.Raise(EventKind.JobDone, e.A, e.B);
            c.World.Apply(e, c.Events, now);
            if (e.Kind is InboundKind.MapLoginAck or InboundKind.MapLink or InboundKind.Revive) c.Nav.Layer(c.World.Map.MapName);   // start loading walkability
            c.Actions.Apply(e);
            c.Move.Apply(e, c);
            c.Combat.Apply(e, c);
            c.Procedures.Apply(e, c);
            for (var i = 0; i < c.Learners.Count; i++) c.Learners[i].Observe(e, c);
            LogEvent(e);
        }
        var t2 = Stopwatch.GetTimestamp();

        // 3. actions
        c.Actions.Advance(now);
        c.Procedures.Advance(c);
        var t3 = Stopwatch.GetTimestamp();
        // 4. movement, 5. combat
        c.Move.Advance(c);   // controllers run while the brain is paused (operator actions still move and fight)
        var t4 = Stopwatch.GetTimestamp();
        c.Combat.Advance(c);
        var t5 = Stopwatch.GetTimestamp();
        // 6. brain
        long lua = 0;
        if (Brain is not null && c.World.Self.InWorld && (!Paused || _stepBudget > 0))
        {
            Brain.Tick(c);
            lua = Brain.LastInstructions;
            if (_stepBudget > 0) _stepBudget--;
        }
        var t6 = Stopwatch.GetTimestamp();
        // 7. intents are already in the outbound ring; 8. publish
        var a1 = GC.GetAllocatedBytesForCurrentThread();
        if (now - _lastPublish >= 100) { Publish(now); _lastPublish = now; OnPublish?.Invoke(this); }
        Game?.MaybeCapture(c);
        var t7 = Stopwatch.GetTimestamp();
        s.PublishAllocBytes = GC.GetAllocatedBytesForCurrentThread() - a1;
        // 9. stats
        s.ControlUs = (float)Us(t0, t1); s.ApplyUs = (float)Us(t1, t2); s.ActionsUs = (float)Us(t2, t3);
        s.MoveUs = (float)Us(t3, t4); s.CombatUs = (float)Us(t4, t5); s.BrainUs = (float)Us(t5, t6); s.PublishUs = (float)Us(t6, t7);
        s.TotalUs = (float)Us(t0, t7);
        s.Events = events; s.LuaInstructions = lua;
        s.AllocBytes = a1 - a0;
        Stats.Record(s);
        LastTickAt = now;
    }

    private void LogEvent(in InboundEvent e)
    {
        var log = Context.Log;
        switch (e.Kind)
        {
            case InboundKind.MapLoginAck:
                log.Log(LogCategory.Core, LogLevel.Note, $"in the world: {Context.World.Self.Name} handle {e.Handle} on {Context.World.Map.MapName} ({Context.World.Map.MapId}) at ({e.X},{e.Y}) lv {Context.World.Self.Level} hp {Context.World.Self.Hp}/{e.A} money {Context.World.Self.Money}");
                break;
            case InboundKind.MapLoginFail:
                log.Log(LogCategory.Core, LogLevel.Crit, $"MAP_LOGINFAIL err={e.A} data file index {e.B}: the client data does not match the server's");
                break;
            case InboundKind.MapLink:
                log.Log(LogCategory.Nav, LogLevel.Note, $"map link -> {Context.World.Map.MapName} ({e.A}) at ({e.X},{e.Y}){(e.B == 1 ? " (other zone server)" : "")}");
                break;
            case InboundKind.SelfLevel:
                log.Log(LogCategory.Core, LogLevel.Note, $"LEVEL UP -> {e.A}");
                break;
            case InboundKind.DeadMenu:
                log.Log(LogCategory.Combat, LogLevel.Note, $"DIED on {Context.World.Map.MapName} at ({Context.World.Self.Pos.X:0},{Context.World.Self.Pos.Y:0}); revive menu {e.A} s");
                break;
            case InboundKind.QuestDialog when log.IsEnabled(LogCategory.Quest, LogLevel.Debug):
                log.Log(LogCategory.Quest, LogLevel.Debug, $"quest script: quest {e.A} cmd {e.B} data {e.C} npc {e.Handle2}");
                break;
            case InboundKind.ServerMenu when log.IsEnabled(LogCategory.Social, LogLevel.Info):
                log.Log(LogCategory.Social, LogLevel.Info, $"server menu '{e.Text}' from {e.Handle}: {string.Join(" | ", (e.Options ?? []).Select(o => $"{o.Reply}={o.Text}"))}");
                break;
            case InboundKind.ShopList when log.IsEnabled(LogCategory.Item, LogLevel.Info):
                log.Log(LogCategory.Item, LogLevel.Info, $"shop 0x{e.A:X4} opened by {e.Handle}: {e.B} items");
                break;
            case InboundKind.Chat when e.Text is { } t && log.IsEnabled(LogCategory.Social, LogLevel.Info):
                log.Log(LogCategory.Social, LogLevel.Info, $"chat {e.Handle}: {t}");
                break;
            case InboundKind.BuySellAck:
                log.Log(LogCategory.Item, LogLevel.Info, $"{(e.B == 0 ? "BUY" : "SELL")}_ACK 0x{e.A:X4} {ErrorCodes.Describe((ushort)e.A)}");
                break;
            case InboundKind.TradeEvent:
                log.Log(LogCategory.Social, e.A is 15 or 16 or 19 or 20 or 23 or 24 or 27 or 28 or 29 or 30 or 33 or 34 ? LogLevel.Info : LogLevel.Note,
                    $"TRADE {TradeName((int)e.A)} b={e.B} c={e.C}{(e.A == 16 ? $" x{e.X}" : "")} [{e.Text}] -> {Context.World.Trade.Phase} partner {Context.World.Trade.Partner} cen {Context.World.Trade.MyCen}/{Context.World.Trade.TheirCen} lock {Context.World.Trade.MyLocked}/{Context.World.Trade.TheirLocked} decide {Context.World.Trade.MyDecided}/{Context.World.Trade.TheirDecided}");
                break;
            case InboundKind.Scenario:
                log.Text(LogCategory.Quest, e.A is 9 or 22 ? LogLevel.Info : LogLevel.Note, e.A switch
                {
                    5 => $"SCENARIO area '{e.Text}' entered: the server asks us to confirm it",
                    7 => $"SCENARIO area '{e.Text}' left",
                    9 => $"SCENARIO door {e.Handle} -> state {e.B}",
                    1000 => $"SCENARIO door {e.Handle} built (mob {e.C}, block '{e.Text}', state {e.B}) at ({e.X},{e.Y})",
                    11 => $"SCENARIO object {e.Handle} is now {(e.B == 5 ? "a mob" : e.B == 4 ? "an npc" : $"type {e.B}")}",
                    22 => $"SCENARIO timer {e.B} s",
                    33 => "SCENARIO timer started",
                    34 => $"SCENARIO timer ended ({e.B})",
                    27 => $"SCENARIO message: {e.Text}",
                    _ => $"SCENARIO notice {e.B}",
                });
                break;
            case InboundKind.SkillLearned when e.C == 1:
                log.Log(LogCategory.Core, LogLevel.Note, $"learned skill {e.A} ({Context.Data.Skill((int)e.A)?.Name ?? "?"})");
                break;
            case InboundKind.SkillLearnFail:
                log.Log(LogCategory.Core, LogLevel.Note, $"SKILL_LEARNFAIL 0x{e.A:X4}");
                break;
            case InboundKind.Promoted:
                log.Log(LogCategory.Core, LogLevel.Note, $"PROMOTED: class {e.A} ({Context.Data.ClassName((int)e.A)})");
                break;
            case InboundKind.SplitAck:
                log.Log(LogCategory.Item, e.A == 0x0441 ? LogLevel.Info : LogLevel.Note, $"SPLIT_ACK 0x{e.A:X4} {ErrorCodes.Describe((ushort)e.A)}");
                break;
            case InboundKind.ProduceMade:
                log.Log(LogCategory.Item, e.B == 0x0B01 ? LogLevel.Info : LogLevel.Note, $"PRODUCE_MAKE item {e.A} 0x{e.B:X4} {ErrorCodes.Describe((ushort)e.B)}");
                break;
            case InboundKind.ProduceFail:
                log.Log(LogCategory.Item, LogLevel.Note, $"PRODUCE_CAST_FAIL 0x{e.A:X4}");
                break;
            case InboundKind.ProduceField:
                log.Log(LogCategory.Item, LogLevel.Info, $"PRODUCTFIELD skill {e.A} 0x{e.B:X4}");
                break;
            case InboundKind.SkillExp when log.IsEnabled(LogCategory.Item, LogLevel.Info):
                log.Log(LogCategory.Item, LogLevel.Info, $"mastery of skill {e.A}: {e.B}");
                break;
            case InboundKind.StorageFail:
                log.Log(LogCategory.Item, LogLevel.Note, $"MENU_OPENSTORAGE_FAIL 0x{e.A:X4} (storage window {(Context.World.Bag.StorageOpenAt >= 0 ? "open" : "not open")})");
                break;
            case InboundKind.RelocAck:
                log.Log(LogCategory.Item, e.A == 0x0241 ? LogLevel.Info : LogLevel.Note, $"RELOC_ACK 0x{e.A:X4} {ErrorCodes.Describe((ushort)e.A)}");
                break;
            case InboundKind.ItemCell when e.A == ItemDecoder.StorageBox && e.B >= 0 && e.Opcode != 0x3C08 && log.IsEnabled(LogCategory.Item, LogLevel.Info):
                log.Log(LogCategory.Item, LogLevel.Info, $"storage cell {e.B} <- item {e.C} x{e.X}");
                break;
            case InboundKind.ItemUseResult when log.IsEnabled(LogCategory.Item, LogLevel.Info):
                log.Log(LogCategory.Item, LogLevel.Info, $"ITEM_USE_ACK 0x{e.A:X4} item {e.B} {ErrorCodes.Describe((ushort)e.A)}");
                break;
            case InboundKind.CastFail when log.IsEnabled(LogCategory.Combat, LogLevel.Info):
                log.Log(LogCategory.Combat, LogLevel.Info, $"cast refused 0x{e.A:X4} {ErrorCodes.Describe((ushort)e.A)}");
                break;
        }
    }

    private static string TradeName(int cmd) => cmd switch
    {
        2 => "PROPOSE_ASK", 3 => "ASKNO", 4 => "PROPOSENO (refused)", 5 => "ASKYES", 6 => "PROPOSEYES", 7 => "PROPOSE_CANCEL", 8 => "PROPOSE_CANCELED",
        9 => "START", 11 => "CANCEL_ACK", 12 => "CANCEL (by the partner)", 14 => "UPBOARD_FAIL", 15 => "UPBOARD_ACK", 16 => "OPPOSIT_UPBOARD",
        18 => "DOWNBOARD_FAIL", 19 => "DOWNBOARD_ACK", 20 => "OPPOSIT_DOWNBOARD", 22 => "CENBOARDING_FAIL", 23 => "CENBOARDING_ACK",
        24 => "OPPOSIT_CENBOARDING", 26 => "BOARDLOCK_FAIL", 27 => "BOARDLOCK_ACK", 28 => "OPPOSIT_BOARDLOCK", 29 => "BOARDUNLOCK",
        30 => "OPPOSIT_BOARDUNLOCK", 32 => "DECIDE_FAIL", 33 => "DECIDE_ACK", 34 => "OPPOSIT_DECIDE", 35 => "TRADE_FAIL", 36 => "TRADE_COMPLETE",
        _ => $"cmd {cmd}",
    };

    private void HandleControl(ControlCommand cmd)
    {
        var c = Context;
        c.Events.Raise(EventKind.Operator, (long)cmd.Kind);
        object? result = null;
        if (cmd.Query is { } q)
        {
            try { cmd.Reply?.TrySetResult(q(c)); } catch (Exception e) { cmd.Reply?.TrySetException(e); }
            return;
        }
        switch (cmd.Kind)
        {
            case ControlKind.Pause:
                Paused = true;
                if (c.Move.Moving) c.Move.Stop(c);
                if (c.Combat.Bashing) c.Combat.StopBash(0, c);
                break;
            case ControlKind.Resume: Paused = false; _stepBudget = 0; break;
            case ControlKind.Step: _stepBudget += Math.Max(1, cmd.Count); break;
            case ControlKind.Stop or ControlKind.Relog:
                if (c.Move.Moving) c.Move.Stop(c);
                if (c.Combat.Bashing) c.Combat.StopBash(0, c);
                Lifecycle?.Invoke(cmd);
                break;
            case ControlKind.Action:
                result = OperatorActions.Run(cmd, c);
                break;
            default:
                try { result = Brain?.Control(cmd, c); }
                catch (Exception e) { cmd.Reply?.TrySetException(e); return; }
                break;
        }
        cmd.Reply?.TrySetResult(result);
    }

    private void Publish(long now)
    {
        var w = Context.World; var self = w.Self;
        _snapshot = new AgentSnapshot(Context.BotId, Context.Tick, _clock.UtcNow, w.Map.MapName, self.Pos.X, self.Pos.Y, self.Level,
            self.Hp, self.MaxHp, self.Sp, self.MaxSp, self.Money, w.Bag.Free, self.Dead, self.InCombat(now), w.Map.MapId, self.InWorld)
        { RunSpeed = self.RunSpeed, Exp = self.Exp, NextExp = self.NextExp, Name = self.Name, ClassId = self.ClassId, Kills = self.Kills, Deaths = self.Deaths, ExpGained = self.ExpGained, Entities = w.Entities.Count, Paused = Paused, Econ = w.Economy.Rates(30, now), QuestExp = w.Economy.QuestExp };
    }

    /// <summary>Latest published snapshot (immutable, any thread).</summary>
    public AgentSnapshot Snapshot => _snapshot;
}

/// <summary>Immutable view published every 100 ms for HTTP/UI readers.</summary>
public sealed record AgentSnapshot(string BotId, long Tick, DateTime At, string Map, double X, double Y, int Level, double Hp,
    double MaxHp, double Sp, double MaxSp, long Money, int BagFree, bool Dead, bool InCombat, int MapId, bool InWorld)
{
    /// <summary>Economy over the last 30 minutes, per hour: exp, money, deaths, stone spend.</summary>
    public (double ExpH, double MoneyH, double DeathsH, double StonesH) Econ { get; init; }
    /// <summary>Exp from quest rewards (no kill) this session.</summary>
    public long QuestExp { get; init; }
    /// <summary>Exp.</summary>
    public long Exp { get; init; }
    /// <summary>Exp for the next level.</summary>
    public long NextExp { get; init; }
    /// <summary>Character name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Class id.</summary>
    public int ClassId { get; init; }
    /// <summary>Kills / deaths / exp this session.</summary>
    public int Kills { get; init; }
    /// <summary>Deaths.</summary>
    public int Deaths { get; init; }
    /// <summary>Exp gained.</summary>
    public long ExpGained { get; init; }
    /// <summary>Entities in view.</summary>
    public int Entities { get; init; }
    /// <summary>Paused.</summary>
    public bool Paused { get; init; }
    /// <summary>Run speed (u/s).</summary>
    public double RunSpeed { get; init; }
}

/// <summary>Operator actions (the Operator layer, index 0): run on the tick from a control command.</summary>
public static class OperatorActions
{
    /// <summary>Run one; returns {action id, state} or an error string.</summary>
    public static object Run(ControlCommand cmd, AgentContext c)
    {
        var a = cmd.Args ?? new Dictionary<string, string>();
        long L(string k, long d = -1) => a.TryGetValue(k, out var v) && long.TryParse(v, out var x) ? x : d;
        double D(string k) => a.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
        ActionId id;
        switch ((cmd.Value ?? "").ToLowerInvariant())
        {
            case "move" or "moveto": id = c.Move.Order(new MoveOrder(D("x"), D("y"), true, L("within", 20), ActionId.None), c, 0); break;
            case "movedirect": id = c.Move.Order(new MoveOrder(D("x"), D("y"), false, L("within", 10), ActionId.None), c, 0); break;
            case "stop": c.Move.Stop(c); id = c.Actions.Issue(ActionKind.Stop, 0, 0, 0, []); break;
            case "cast": id = c.Combat.Cast((int)L("skill"), (int)L("target", c.World.Self.Handle), 0, c); break;
            case "bash": id = c.Combat.Bash((int)L("target"), 0, c); break;
            case "stopbash": id = c.Combat.StopBash(0, c); break;
            case "target": id = c.Combat.SetTarget((int)L("target"), 0, c); break;
            case "use": { Span<Intent> b = [IntentEncoder.UseItem((byte)L("slot"), 9, -1)]; id = c.Actions.Issue(ActionKind.UseItem, 0, L("slot"), 0, b); break; }
            case "say": { Span<Intent> b = [IntentEncoder.Chat(a.GetValueOrDefault("text") ?? "", -1)]; id = c.Actions.Issue(ActionKind.Say, 0, 0, 0, b); break; }
            case "openshop": id = c.Procedures.Start(new OpenShopProcedure((int)L("npc"), (int)L("reply", 1)), ActionKind.OpenShop, 0, c); break;
            case "talk": id = c.Procedures.Start(new QuestDialogueProcedure((int)L("npc"), (int)L("quest"), (int)L("reward")), ActionKind.Talk, 0, c); break;
            case "gate": id = c.Procedures.Start(new GateHopProcedure((int)L("gate"), a.GetValueOrDefault("map")), ActionKind.Gate, 0, c, GateHopProcedure.TimeoutFor(c, (int)L("gate"))); break;
            case "revive": id = c.Procedures.Start(new ReviveProcedure(), ActionKind.Revive, 0, c); break;
            case "buy": { Span<Intent> b = [IntentEncoder.Buy((ushort)L("item"), (uint)L("lot", 1), -1)]; id = c.Actions.Issue(ActionKind.Buy, 0, L("item"), L("lot", 1), b); break; }
            case "sell": { Span<Intent> b = [IntentEncoder.Sell((byte)L("slot"), (uint)L("lot", 1), -1)]; id = c.Actions.Issue(ActionKind.Sell, 0, L("slot"), L("lot", 1), b); break; }
            case "loot": { Span<Intent> b = [IntentEncoder.Pick((ushort)L("target"), -1)]; id = c.Actions.Issue(ActionKind.Loot, 0, L("target"), 0, b); break; }
            case "raw":
            {
                var payload = Convert.FromHexString(a.GetValueOrDefault("hex") ?? "");
                Span<Intent> b = [IntentEncoder.Raw((ushort)L("op"), payload, -1)];
                id = c.Actions.Issue(ActionKind.Raw, 0, L("op"), 0, b);
                break;
            }
            default: return $"unknown operator action '{cmd.Value}'";
        }
        c.Log.Log(LogCategory.Core, LogLevel.Note, $"operator action {cmd.Value} by {cmd.By} -> action {id.Value}{(id.Value < 0 ? $" rejected: {c.Actions.LastRejection}" : "")}");
        return new { action = id.Value, rejected = id.Value < 0 ? c.Actions.LastRejection.ToString() : null };
    }
}
