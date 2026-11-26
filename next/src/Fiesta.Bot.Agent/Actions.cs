using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Agent;

/// <summary>Action identifier (a monotonically increasing number; the table keeps the last 1,024).</summary>
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
    MenuReply, BuyStones, StatPoint, Equip, StorageMove, TradeAnswer, TradeTakeBack, Produce, ProductField, Split, Tent, BagSort, Upgrade, UpgradeAtNpc,
}

/// <summary>Resources a layer can claim (REDESIGN 5.1).</summary>
[Flags]
public enum Resource : byte { None = 0, Movement = 1, Combat = 2, Cast = 4, Inventory = 8, Npc = 16, Chat = 32, All = 63 }

/// <summary>Local refusal codes (`act.L_*`).</summary>
public enum LocalRefusal : ushort
{
    None = 0, LinkDown = 0xF001, Preempted, UnknownSkill, NotReady, OutOfRange, Moving, Mounted, Busy, QueueFull, InvalidTarget,
    NoPath, MoveFailed, Desync, NotInWorld, Dead,
}

/// <summary>Stored state of one action.</summary>
public struct ActionRecord
{
    /// <summary>Id (validates the slot).</summary>
    public int Id;
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
    /// <summary>Is the action still pending?</summary>
    public readonly bool Pending => State is ActionState.Queued or ActionState.Sent;
}

/// <summary>Decides whether an inbound event resolves a pending action.</summary>
public interface IResultMatcher
{
    /// <summary>The new state and code if <paramref name="evt"/> resolves <paramref name="action"/>, else null.</summary>
    (ActionState State, ushort Code)? Match(in ActionRecord action, in InboundEvent evt, WorldState world);
}

/// <summary>Static description of an action kind: matcher, timeout, resource.</summary>
public sealed record ActionSpec(ActionKind Kind, Resource Resource, IResultMatcher Matcher, TimeSpan BaseTimeout);

/// <summary>Matcher implementations.</summary>
internal sealed class FnMatcher(Func<ActionRecord, InboundEvent, WorldState, (ActionState, ushort)?> f) : IResultMatcher
{
    public (ActionState State, ushort Code)? Match(in ActionRecord a, in InboundEvent e, WorldState w) => f(a, e, w);
}

/// <summary>The table of every action kind with its result matcher; an action kind without a matcher has no entry
/// and <see cref="ActionSystem.Issue"/> refuses it, so "wire an action = wire its result" is enforced at one place.</summary>
public static class ActionCatalog
{
    private static readonly ActionSpec?[] Specs = new ActionSpec?[64];
    /// <summary>Resolved by the controller / procedure that runs the action (moves, procedures).</summary>
    public static readonly IResultMatcher Controller = new FnMatcher((_, _, _) => null);
    /// <summary>Fire-and-forget: resolved as acked when the intent is queued (the game sends no result).</summary>
    public static readonly IResultMatcher OnSend = new FnMatcher((_, _, _) => null);

    static ActionCatalog()
    {
        void S(ActionKind k, Resource r, IResultMatcher m, int ms) => Specs[(int)k] = new ActionSpec(k, r, m, TimeSpan.FromMilliseconds(ms));
        var cast = new FnMatcher((a, e, w) => e.Kind switch
        {
            InboundKind.SkillCastStart when e.A == a.Arg0 => (ActionState.Acked, (ushort)0),
            InboundKind.CastFail => (ActionState.Refused, (ushort)e.A),
            _ => null,
        });
        S(ActionKind.Cast, Resource.Cast, cast, 1500);
        S(ActionKind.CastGround, Resource.Cast, cast, 1500);
        S(ActionKind.Bash, Resource.Combat, new FnMatcher((a, e, w) =>
            (e.Kind is InboundKind.Damage or InboundKind.SwingStart) && e.Handle == w.Self.Handle && e.Handle2 == a.Arg0 ? (ActionState.Acked, (ushort)0) : null), 2500);
        S(ActionKind.StopBash, Resource.Combat, OnSend, 0);
        S(ActionKind.Target, Resource.Combat, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.EntityHp && e.Opcode == 0x2402 && e.Handle == a.Arg0 ? (ActionState.Acked, (ushort)0) : null), 1500);
        S(ActionKind.Untarget, Resource.Combat, OnSend, 0);
        S(ActionKind.Move, Resource.Movement, Controller, 60000);
        S(ActionKind.MoveDirect, Resource.Movement, Controller, 30000);
        S(ActionKind.Follow, Resource.Movement, Controller, 600000);
        S(ActionKind.Stop, Resource.Movement, OnSend, 0);
        S(ActionKind.UseItem, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.ItemUseResult ? (e.A == 0x0700 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.A)) : null), 2000);
        S(ActionKind.Mount, Resource.Movement, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.Mount && e.A == 1 ? (ActionState.Acked, (ushort)0)
            : e.Kind == InboundKind.ItemUseResult && e.A != 0x0700 ? (ActionState.Refused, (ushort)e.A) : null), 6000);
        S(ActionKind.Dismount, Resource.Movement, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.Mount && e.A == 0 ? (ActionState.Acked, (ushort)0)
            : e.Kind == InboundKind.ItemUseResult && e.A != 0x0700 ? (ActionState.Refused, (ushort)e.A) : null), 6000);
        var stone = new FnMatcher((a, e, w) => e.Kind == InboundKind.StoneUse
            ? (e.A == a.Arg0 ? (ActionState.Acked, (ushort)0) : e.A >= 2 ? (ActionState.Refused, (ushort)e.B) : null) : null);
        S(ActionKind.StoneHp, Resource.Inventory, stone, 2000);
        S(ActionKind.StoneSp, Resource.Inventory, stone, 2000);
        S(ActionKind.Loot, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.PickAck ? (e.C == 0x0341 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.C)) : null), 2000);
        S(ActionKind.Buy, Resource.Npc, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.BuySellAck && e.B == 0 ? (e.A == 0x0201 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.A)) : null), 2000);
        S(ActionKind.Sell, Resource.Npc, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.BuySellAck && e.B == 1 ? (e.A == 0x0381 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.A)) : null), 2000);
        S(ActionKind.OpenShop, Resource.Npc, Controller, 6000);
        S(ActionKind.UpgradeAtNpc, Resource.Npc, Controller, 15000);
        // soul stones: HP_BUY_ACK / SP_BUY_ACK carry the new total; BUYFAIL the reason
        S(ActionKind.BuyStones, Resource.Npc, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.StoneReserve && e.A == a.Arg0 ? (ActionState.Acked, (ushort)0)
            : e.Kind == InboundKind.StoneUse && e.A == 3 ? (ActionState.Refused, (ushort)e.B) : null), 3000);
        // equipping: EQUIPCHANGE puts the item into its equipment slot
        S(ActionKind.Equip, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.ItemCell && e.Opcode == 0x3002 && e.A == 8 && e.C == a.Arg1 ? (ActionState.Acked, (ushort)0) : null), 3000);
        S(ActionKind.StatPoint, Resource.None, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.StatPointSpent && e.A == a.Arg0 ? (ActionState.Acked, (ushort)0)
            : e.Kind == InboundKind.StatPointRefused ? (ActionState.Refused, (ushort)e.A) : null), 3000);
        // a move between the bag and storage (ITEM_RELOC_REQ): done when the source cell changes; RELOC_ACK other than
        // 0x0241 is the refusal (0x0241 without the source cell changing is not done: it times out)
        S(ActionKind.StorageMove, Resource.Npc, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.ItemCell && e.B >= 0 && e.A == a.Arg0 >> 10 && e.B == (a.Arg0 & 0x3FF) ? (ActionState.Acked, (ushort)0)
            : e.Kind == InboundKind.RelocAck && e.A != 0x0241 ? (ActionState.Refused, (ushort)e.A) : null), 3000);
        // trade steps: each resolves on its own ack / fail (TradeDecoder commands); a trade that ends while a step is
        // pending refuses the step with the ending command (11 / 12 cancelled, 35 failed, 4 / 7 / 8 the proposal died)
        static FnMatcher T(int ok, int fail, Func<ActionRecord, InboundEvent, bool>? match = null) => new((a, e, w) =>
            e.Kind != InboundKind.TradeEvent ? null
            : e.A == ok && (match is null || match(a, e)) ? (ActionState.Acked, (ushort)0)
            : e.A == fail ? (ActionState.Refused, (ushort)e.B)
            : e.A is 4 or 7 or 8 or 11 or 12 or 35 ? (ActionState.Refused, (ushort)(0x4C00 | e.A)) : null);
        S(ActionKind.TradePropose, Resource.Npc, T(9, 4), 30000);
        S(ActionKind.TradeAnswer, Resource.Npc, T(9, -1), 10000);
        S(ActionKind.TradeOfferItem, Resource.Npc, T(15, 14, (a, e) => e.B == a.Arg0), 5000);
        S(ActionKind.TradeTakeBack, Resource.Npc, T(19, 18, (a, e) => e.B == a.Arg0), 5000);
        S(ActionKind.TradeOfferMoney, Resource.Npc, T(23, 22), 5000);
        S(ActionKind.TradeLock, Resource.Npc, T(27, 26), 5000);
        // the trade may complete before our own DECIDE_ACK arrives (seen live: the partner decided first, COMPLETE came
        // in place of the ack)
        var decide = T(33, 32);
        S(ActionKind.TradeConfirm, Resource.Npc, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.TradeEvent && e.A == 36 ? (ActionState.Acked, (ushort)0) : decide.Match(a, e, w)), 5000);
        S(ActionKind.TradeCancel, Resource.None, new FnMatcher((a, e, w) => e.Kind == InboundKind.TradeEvent && e.A is 7 or 8 or 11 or 12 ? (ActionState.Acked, (ushort)0) : null), 5000);
        // production: one cast makes one lot (PRODUCE_MAKE 0x0B01), refused by its code; choosing a field answers 0x0B01
        S(ActionKind.Produce, Resource.Cast, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.ProduceMade ? (e.B == 0x0B01 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.B))
            : e.Kind == InboundKind.ProduceFail ? (ActionState.Refused, (ushort)e.A) : null), 10000);
        S(ActionKind.ProductField, Resource.None, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.ProduceField && e.A == a.Arg0 ? (e.B == 0x0B01 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.B)) : null), 3000);
        S(ActionKind.Split, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.SplitAck ? (e.A == 0x0441 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.A)) : null), 3000);
        // pitch (Arg0 1) / fold (Arg0 0) the rest tent: its ack carries 0x0A81 or why not
        S(ActionKind.Tent, Resource.Movement, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.Tent && e.A == a.Arg0 ? (e.B == 0x0A81 ? (ActionState.Acked, (ushort)0) : (ActionState.Refused, (ushort)e.B)) : null), 4000);
        // the client's bag auto-arrange (stacks loose items together, compacts): resolved by its answer, whatever the code
        // (0x30A8 is what the real client's sort receives)
        // the upgrade answer is a code for every outcome (success, failed, -1, destroyed, refused): the matcher acks on any
        // of them and the code says which; the outcome is the caller's to read
        S(ActionKind.Upgrade, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.UpgradeAck ? (e.A == 0x08C3 || e.A == 0x08C5 || e.A == 0x08C6 || e.A == 0x08C7 ? ActionState.Acked : ActionState.Failed, (ushort)Math.Max(0, e.A)) : null), 8000);
        S(ActionKind.BagSort, Resource.Inventory, new FnMatcher((a, e, w) =>
            e.Kind == InboundKind.SortAck ? (ActionState.Acked, (ushort)Math.Max(0, e.A)) : null), 4000);
        S(ActionKind.Talk, Resource.Npc, Controller, 20000);
        S(ActionKind.Storage, Resource.Npc, Controller, 6000);
        S(ActionKind.Gate, Resource.Movement, Controller, 20000);
        S(ActionKind.Travel, Resource.Movement, Controller, 600000);
        S(ActionKind.Revive, Resource.None, Controller, 30000);
        S(ActionKind.Say, Resource.Chat, OnSend, 0);
        S(ActionKind.Whisper, Resource.Chat, OnSend, 0);
        S(ActionKind.MenuReply, Resource.Npc, OnSend, 0);
        S(ActionKind.Raw, Resource.None, OnSend, 0);
        S(ActionKind.PartyAccept, Resource.None, OnSend, 0);   // the member list that follows is the visible result
    }

    /// <summary>Spec of a kind, null when the kind has no result wiring yet.</summary>
    public static ActionSpec? Get(ActionKind kind) => Specs[(int)kind];
    /// <summary>All specs.</summary>
    public static IReadOnlyList<ActionSpec> All => Specs.Where(s => s is not null).ToList()!;
}

/// <summary>Creates actions, tracks them, resolves them from inbound events, times them out. Tick thread only.</summary>
public sealed class ActionSystem
{
    private readonly ActionRecord[] _t;
    private readonly int[] _pending = new int[256];
    private int _pendingN;
    private int _next = 1;
    private readonly OutboundRing _out;
    private readonly Resource[] _claims = new Resource[16];
    private long _now;
    /// <summary>Events (set by the agent).</summary>
    public BrainEventBus? Events { get; set; }
    /// <summary>World (for matchers).</summary>
    public WorldState? World { get; set; }
    /// <summary>Logger (set by the agent).</summary>
    public BotLogger? Log { get; set; }
    /// <summary>Issued / resolved counters.</summary>
    public long Issued, Resolved;

    /// <summary>Create for one bot.</summary>
    public ActionSystem(OutboundRing outbound, int capacity = 1024) { _out = outbound; _t = new ActionRecord[capacity]; }

    /// <summary>The tick's clock (set at tick start).</summary>
    public long Now { get => _now; set => _now = value; }

    /// <summary>Queue an action with its intents; returns <see cref="ActionId.None"/> with <see cref="LastRejection"/>
    /// when it cannot be queued.</summary>
    public ActionId Issue(ActionKind kind, byte layer, long arg0, long arg1, ReadOnlySpan<Intent> intents, int timeoutMs = -1)
    {
        var spec = ActionCatalog.Get(kind);
        if (spec is null) return Reject(LocalRefusal.UnknownSkill);
        if (!_out.LinkUp) return Reject(LocalRefusal.LinkDown);
        if (Preempted(layer, spec.Resource)) return Reject(LocalRefusal.Preempted);
        if (_out.Free < intents.Length) return Reject(LocalRefusal.QueueFull);
        var id = New(kind, layer, arg0, arg1, timeoutMs >= 0 ? timeoutMs : (int)spec.BaseTimeout.TotalMilliseconds);
        for (var i = 0; i < intents.Length; i++)
        {
            var it = intents[i]; it.ActionId = id.Value;
            _out.TryEnqueue(it);
        }
        ref var r = ref Slot(id.Value);
        r.State = ActionState.Sent; r.SentAt = _now;
        if (ReferenceEquals(spec.Matcher, ActionCatalog.OnSend)) Resolve(id, ActionState.Acked, 0);
        return id;
    }

    /// <summary>Start an action that a controller or procedure resolves (no intents now).</summary>
    public ActionId Begin(ActionKind kind, byte layer, long arg0, long arg1, int timeoutMs = -1)
    {
        var spec = ActionCatalog.Get(kind);
        if (spec is null) return Reject(LocalRefusal.UnknownSkill);
        if (!_out.LinkUp) return Reject(LocalRefusal.LinkDown);
        if (Preempted(layer, spec.Resource)) return Reject(LocalRefusal.Preempted);
        var id = New(kind, layer, arg0, arg1, timeoutMs >= 0 ? timeoutMs : (int)spec.BaseTimeout.TotalMilliseconds);
        ref var r = ref Slot(id.Value);
        r.State = ActionState.Sent; r.SentAt = _now;
        return id;
    }

    /// <summary>Enqueue an intent outside any action (controllers' housekeeping: STOP after a leg, re-arm a bash).</summary>
    public bool Send(in Intent intent) => _out.LinkUp && _out.TryEnqueue(intent);

    private ActionId New(ActionKind kind, byte layer, long a0, long a1, int timeout)
    {
        var id = _next++;
        ref var r = ref Slot(id);
        r = new ActionRecord { Id = id, Kind = kind, State = ActionState.Queued, Layer = layer, QueuedAt = _now, Arg0 = a0, Arg1 = a1, Deadline = timeout > 0 ? _now + timeout : long.MaxValue };
        if (_pendingN < _pending.Length) _pending[_pendingN++] = id;
        Issued++;
        return new ActionId(id);
    }

    private ref ActionRecord Slot(int id) => ref _t[id % _t.Length];

    private ActionId Reject(LocalRefusal why) { LastRejection = why; return ActionId.None; }

    /// <summary>Queue a locally refused action so the brain sees a handle with the reason.</summary>
    public ActionId Refuse(ActionKind kind, byte layer, LocalRefusal reason)
    {
        var id = New(kind, layer, 0, 0, 0);
        Resolve(id, ActionState.Refused, (ushort)reason);
        return id;
    }

    /// <summary>Record of an action (a default record with Id 0 when unknown / evicted).</summary>
    public ActionRecord Get(ActionId id)
    {
        if (id.Value <= 0) return default;
        var r = Slot(id.Value);
        return r.Id == id.Value ? r : default;
    }

    /// <summary>Resolve (controllers, procedures, matchers).</summary>
    public void Resolve(ActionId id, ActionState state, ushort code)
    {
        if (id.Value <= 0) return;
        ref var r = ref Slot(id.Value);
        if (r.Id != id.Value || !r.Pending) return;
        r.State = state; r.Code = code; r.ResolvedAt = _now;
        for (var i = 0; i < _pendingN; i++) if (_pending[i] == id.Value) { _pending[i] = _pending[--_pendingN]; break; }
        Resolved++;
        Events?.Raise(EventKind.ActionResolved, id.Value, (long)state);
        if (r.Kind is ActionKind.Cast or ActionKind.CastGround) Events?.Raise(EventKind.CastResult, r.Arg0, code);
        if (state is ActionState.Refused or ActionState.TimedOut or ActionState.Failed && Log is { } log && log.IsEnabled(LogCategory.Core, LogLevel.Info))
            log.Log(LogCategory.Core, LogLevel.Info, $"action {id.Value} {r.Kind}({r.Arg0},{r.Arg1}) {state} 0x{code:X4} {ErrorCodes.Describe(code)}");
    }

    /// <summary>Match an inbound event against pending actions.</summary>
    public void Apply(in InboundEvent evt)
    {
        if (_pendingN == 0 || World is null) return;
        for (var i = _pendingN - 1; i >= 0; i--)
        {
            if (i >= _pendingN) continue;
            var id = _pending[i];
            ref var r = ref Slot(id);
            var spec = ActionCatalog.Get(r.Kind);
            if (spec is null || ReferenceEquals(spec.Matcher, ActionCatalog.Controller)) continue;
            if (spec.Matcher.Match(r, evt, World) is { } m)
            {
                Resolve(new ActionId(id), m.State, m.Code);
                if (evt.Kind == InboundKind.CastFail) break;   // one refusal resolves one cast
            }
        }
    }

    /// <summary>Time out stale actions (tick step 3); a link going down fails everything pending.</summary>
    public void Advance(long nowMs)
    {
        _now = nowMs;
        for (var i = _pendingN - 1; i >= 0; i--)
        {
            if (i >= _pendingN) continue;
            var id = _pending[i];
            ref var r = ref Slot(id);
            // trips span map changes, and a change to another zone server takes the link down for the reconnect: their
            // procedures judge the outcome themselves
            if (!_out.LinkUp && Slot(id).Kind is not (ActionKind.Travel or ActionKind.Gate)) Resolve(new ActionId(id), ActionState.Failed, (ushort)LocalRefusal.LinkDown);
            else if (nowMs > r.Deadline) Resolve(new ActionId(id), ActionState.TimedOut, 0);
        }
    }

    /// <summary>Cancel if pending.</summary>
    public bool Cancel(ActionId id)
    {
        var r = Get(id);
        if (r.Id == 0 || !r.Pending) return false;
        Resolve(id, ActionState.Cancelled, 0);
        return true;
    }

    /// <summary>Cancel every pending action a brain layer issued (layer 0, the operator, is kept); returns the count.</summary>
    public int CancelBrainActions()
    {
        var n = 0;
        for (var i = _pendingN - 1; i >= 0; i--)
        {
            if (i >= _pendingN) continue;
            var id = _pending[i];
            if (Slot(id).Layer != 0) { Resolve(new ActionId(id), ActionState.Cancelled, 0); n++; }
        }
        return n;
    }

    /// <summary>Newest unresolved action of a kind.</summary>
    public ActionId Pending(ActionKind kind)
    {
        var best = -1;
        for (var i = 0; i < _pendingN; i++) { var id = _pending[i]; if (Slot(id).Kind == kind && id > best) best = id; }
        return new ActionId(best);
    }

    /// <summary>Pending actions (debugger).</summary>
    public int PendingCount => _pendingN;
    /// <summary>The last N records, newest first (debugger; allocates, off the tick).</summary>
    public IReadOnlyList<ActionRecord> Recent(int n)
    {
        var o = new List<ActionRecord>(n);
        for (var id = _next - 1; id > 0 && id > _next - 1 - _t.Length && o.Count < n; id--)
        {
            var r = Slot(id);
            if (r.Id == id) o.Add(r);
        }
        return o;
    }

    /// <summary>Reason of the last Issue that returned None.</summary>
    public LocalRefusal LastRejection { get; private set; }
    /// <summary>Resource claims per layer index (0 = highest priority), set by the brain after transitions.</summary>
    public void SetClaims(byte layer, Resource claims) { if (layer < _claims.Length) _claims[layer] = claims; }
    /// <summary>Claims of a layer.</summary>
    public Resource Claims(byte layer) => layer < _claims.Length ? _claims[layer] : Resource.None;
    /// <summary>Is a resource claimed by a layer above <paramref name="layer"/>?</summary>
    public bool Preempted(byte layer, Resource resource)
    {
        if (resource == Resource.None) return false;
        for (var i = 0; i < layer && i < _claims.Length; i++) if ((_claims[i] & resource) != 0) return true;
        return false;
    }
}

/// <summary>Text for result codes: local refusals and the game's common refusal codes (as observed on the wire).</summary>
public static class ErrorCodes
{
    /// <summary>Describe a code.</summary>
    public static string Describe(ushort code) => code switch
    {
        0 => "ok",
        (ushort)LocalRefusal.LinkDown => "link down",
        (ushort)LocalRefusal.Preempted => "resource claimed by a higher layer",
        (ushort)LocalRefusal.UnknownSkill => "unknown skill / action",
        (ushort)LocalRefusal.NotReady => "not ready",
        (ushort)LocalRefusal.OutOfRange => "out of range",
        (ushort)LocalRefusal.Moving => "moving",
        (ushort)LocalRefusal.Mounted => "mounted",
        (ushort)LocalRefusal.Busy => "busy (procedure running)",
        (ushort)LocalRefusal.QueueFull => "outbound queue full",
        (ushort)LocalRefusal.InvalidTarget => "invalid target",
        (ushort)LocalRefusal.NoPath => "no path",
        (ushort)LocalRefusal.MoveFailed => "move refused",
        (ushort)LocalRefusal.Desync => "position desync",
        (ushort)LocalRefusal.NotInWorld => "not in the world",
        (ushort)LocalRefusal.Dead => "dead",
        0x0FC0 => "cast refused: not in battle mode / state",
        0x0FC4 => "cast refused: casting another skill now",
        0x0FC6 => "cast refused: incorrect skill",
        0x0FCA => "cast refused: out of range",
        0x0FD5 => "cast refused: not enough SP",
        0x0FDC => "cast refused: not enough HP",
        0x0700 => "item used",
        0x0381 => "sold",
        0x0201 => "bought",
        0x0341 => "picked up",
        0x0346 => "bag full",
        0x0241 => "moved",
        0x0441 => "split",
        0x30A8 => "bag arranged",
        0x08C3 => "upgrade succeeded (+1)",
        0x08C5 => "upgrade failed, nothing lost",
        0x08C6 => "upgrade failed, the item lost a level",
        0x08C7 => "upgrade failed, the item was destroyed",
        0x08C9 => "upgrade refused, nothing consumed",
        0x08D9 => "upgrade answered 0x08D9 (seen once with a Blessed Xir; meaning not known)",
        0x0A81 => "tent ok",
        0x0A82 => "tent: not in this mode (moving, mounted, busy)",
        0x0A83 => "tent: too near an NPC or another tent",
        0x0A84 => "tent: not on this map",
        0x0A85 => "tent: no mini-house skin",
        0x0A86 => "tent: not while feared",
        0x070B => "the skill is learned already (seen: a second book of a skill just learned)",
        0x0717 => "the recipe's production field is not chosen (seen: read after choosing the field)",
        0x0B01 => "made / chosen",
        0x024A => "the item cannot go there",
        _ => "",
    };
}

/// <summary>Procedure kinds (multi-packet interactions).</summary>
public enum ProcedureKind : byte { OpenShop, QuestDialogue, GateHop, Dismount, Revive, Trade, Tent, Storage, Travel, Upgrade }

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
    /// <summary>When the current step began (ms).</summary>
    long StepSince { get; }
    /// <summary>Advance: look at new events/state, issue intents, update status.</summary>
    void Advance(AgentContext ctx);
    /// <summary>Feed one inbound event.</summary>
    void Apply(in InboundEvent evt, AgentContext ctx);
    /// <summary>Abort.</summary>
    void Cancel();
    /// <summary>The action this procedure resolves.</summary>
    ActionId Action { get; set; }
}

/// <summary>Base for procedures: steps with deadlines, status plumbing.</summary>
public abstract class ProcedureBase : IProcedure
{
    /// <inheritdoc/>
    public abstract ProcedureKind Kind { get; }
    /// <inheritdoc/>
    public string Step { get; private set; } = "start";
    /// <inheritdoc/>
    public ProcedureStatus Status { get; protected set; }
    /// <inheritdoc/>
    public string? FailReason { get; protected set; }
    /// <inheritdoc/>
    public long StepSince { get; private set; }
    /// <inheritdoc/>
    public ActionId Action { get; set; }
    /// <summary>Deadline of the current step.</summary>
    protected long Deadline;
    /// <inheritdoc/>
    public abstract void Advance(AgentContext ctx);
    /// <inheritdoc/>
    public abstract void Apply(in InboundEvent evt, AgentContext ctx);
    /// <inheritdoc/>
    public void Cancel() { if (Status == ProcedureStatus.Running) { Status = ProcedureStatus.Cancelled; FailReason = "cancelled"; } }
    /// <summary>Go to a step with a timeout.</summary>
    protected void Go(string step, AgentContext ctx, int timeoutMs) { Step = step; StepSince = ctx.Now; Deadline = ctx.Now + timeoutMs; }
    /// <summary>Did the step time out?</summary>
    protected bool Expired(AgentContext ctx) => ctx.Now > Deadline;
    /// <summary>Fail the procedure with a reason.</summary>
    protected void Fail(string reason) { if (Status == ProcedureStatus.Running) { Status = ProcedureStatus.Failed; FailReason = reason; } }
    /// <summary>Succeed.</summary>
    protected void Done() { if (Status == ProcedureStatus.Running) Status = ProcedureStatus.Succeeded; }
}

/// <summary>Enhance an item at a smith the way the game client does: END_OF_TRADE -> NPCCLICK -> the smith's NPC menu
/// (NPCMENUOPEN_REQ) is left OPEN, unanswered -> ITEM_UPGRADE_REQ -> ITEM_UPGRADE_ACK {code} -> END_OF_TRADE. (Answering
/// the menu opens the shop list instead, and an upgrade sent from the shop state was answered 0x08D7: NextA, 05:42.)
/// Succeeds on an outcome code (+1, failed, -1, destroyed) and fails on a refusal; the code is Bag.LastUpgradeCode.</summary>
public class UpgradeAtNpcProcedure(int npcHandle, byte itemSlot, byte stoneSlot, byte left, byte right, byte middle) : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Upgrade;
    private bool _started;

    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (!_started)
        {
            if (Camp.FoldFirst(ctx, Action.Value)) return;
            _started = true;
            ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, Action.Value));
            ctx.World.Bag.StorageOpenAt = -1;
            ctx.Actions.Send(IntentEncoder.NpcClick((ushort)npcHandle, Action.Value));
            Go("awaitMenu", ctx, 5000);
            return;
        }
        if (Expired(ctx)) Fail($"upgrade at npc {npcHandle}: nothing in step {Step}");
    }

    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx)
    {
        switch (e.Kind)
        {
            // the smith's window: its NPC menu, or a quest page when it has a quest for us (Blacksmith James, 05:51:43)
            case InboundKind.NpcMenu when Step == "awaitMenu":
            case InboundKind.QuestDialog when Step == "awaitMenu" && e.B == 2:
                ctx.Actions.Send(IntentEncoder.Upgrade(itemSlot, stoneSlot, left, right, middle, Action.Value));
                ctx.Log.Log(LogCategory.Item, LogLevel.Note, $"upgrade: smith window open ({(e.Kind == InboundKind.NpcMenu ? "menu" : "quest page")} {e.A}), sending ITEM_UPGRADE_REQ item slot {itemSlot}, stone slot {stoneSlot}, safety {left}/{right}/{middle}");
                Go("awaitAnswer", ctx, 8000);
                break;
            case InboundKind.UpgradeAck when Step == "awaitAnswer":
                ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, Action.Value));
                if (e.A is 0x08C3 or 0x08C5 or 0x08C6 or 0x08C7) Done();
                else Fail($"upgrade refused 0x{e.A:X4} {ErrorCodes.Describe((ushort)e.A)}");
                break;
        }
    }
}

/// <summary>Open an NPC's shop: END_OF_TRADE -> NPCCLICK -> answer the NPC menu (or a server menu) -> the shop list.</summary>
public class OpenShopProcedure(int npcHandle, int menuReply = 1) : ProcedureBase
{
    /// <summary>The NPC role a shop window shows (the SHOPOPEN* opcode): weapon = smith, skill = skill master,
    /// soul stone = healer, item = merchant (npc role bits as in the Lua `npc.*` constants).</summary>
    public static int RoleOfShop(int opcode) => opcode switch
    {
        0x3C03 or 0x3C09 => 8,    // SHOPOPENWEAPON / TABLE_WEAPON
        0x3C04 or 0x3C0A => 4,    // SHOPOPENSKILL / TABLE_SKILL
        0x3C05 => 2,              // SHOPOPENSOULSTONE
        0x3C08 => 16,             // MENU_OPENSTORAGE (the storage keeper)
        _ => 1,                   // SHOPOPENITEM / TABLE_ITEM
    };

    /// <summary>NPC handle.</summary>
    public int NpcHandle { get; } = npcHandle;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.OpenShop;
    private bool _started;

    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (!_started)
        {
            if (Camp.FoldFirst(ctx, Action.Value)) return;
            _started = true;
            ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, Action.Value));
            ctx.World.Bag.StorageOpenAt = -1;
            ctx.Actions.Send(IntentEncoder.NpcClick((ushort)NpcHandle, Action.Value));
            Go("awaitMenuOrShop", ctx, 5000);
            return;
        }
        if (Expired(ctx)) Fail($"no shop list from npc {NpcHandle} in step {Step}");
    }

    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx)
    {
        switch (e.Kind)
        {
            case InboundKind.NpcMenu:
                ctx.Actions.Send(IntentEncoder.Reply(IntentEncoder.OpNpcMenuAck, (byte)menuReply, Action.Value));
                ctx.Log.Log(LogCategory.Item, LogLevel.Info, $"shop: npc menu (mob {e.A}) answered {menuReply}");
                Go("awaitShop", ctx, 5000);
                break;
            // an NPC with a quest for us answers the click with a quest page instead of its menu; the game client then
            // opens the shop with the menu answer all the same (captured: click -> QUEST_SCRIPT SAY -> NPCMENUOPEN_ACK 1
            // -> SHOPOPENSOULSTONE)
            case InboundKind.QuestDialog when Step == "awaitMenuOrShop" && e.B == 2:
                ctx.Actions.Send(IntentEncoder.Reply(IntentEncoder.OpNpcMenuAck, (byte)menuReply, Action.Value));
                ctx.Log.Log(LogCategory.Item, LogLevel.Info, $"shop: quest page {e.A} instead of a menu: answered the menu {menuReply}");
                Go("awaitShop", ctx, 5000);
                break;
            case InboundKind.ServerMenu when e.Handle == NpcHandle && e.Options is { Length: > 0 } opts:
                var pick = opts[Math.Clamp(menuReply - 1, 0, opts.Length - 1)];
                ctx.Actions.Send(IntentEncoder.Reply(IntentEncoder.OpServerMenuAck, pick.Reply, Action.Value));
                ctx.Log.Log(LogCategory.Item, LogLevel.Info, $"shop: server menu '{e.Text}' answered {pick.Reply}={pick.Text}");
                Go("awaitShop", ctx, 5000);
                break;
            case InboundKind.ShopList when e.Handle == NpcHandle || e.Handle < 0:
                Learn(ctx, RoleOfShop((int)e.A));
                Opened(ctx, false);
                break;
            case InboundKind.StorageList:
                Learn(ctx, RoleOfShop(0x3C08));
                ctx.Log.Log(LogCategory.Item, LogLevel.Note, $"storage open at npc {NpcHandle}: page {e.C}/{e.B}, {e.X} items, {e.A} cen");
                Opened(ctx, true);
                break;
            case InboundKind.StorageFail:
                Fail($"the storage window was refused 0x{e.A:X4}");
                break;
        }
    }

    private void Learn(AgentContext ctx, int role) =>
        ctx.Knowledge.LearnNpcRole(ctx.World.Entities.SlotOf(NpcHandle) is var s and >= 0 ? ctx.World.Entities.MobId(s) : -1,
            role, ctx.World.Map.MapId, (uint)ctx.World.Self.Pos.X, (uint)ctx.World.Self.Pos.Y);

    /// <summary>The window arrived: a shop list or (<paramref name="storage"/>) the storage window.</summary>
    protected virtual void Opened(AgentContext ctx, bool storage) => Done();
}

/// <summary>Quest dialogue page by page: click the NPC, select the quest when the NPC lists several, answer every
/// SAY page with the answer the quest script needs to reach ACCEPT / DONE (derived from QuestData), stop at the
/// terminal command.</summary>
public sealed class QuestDialogueProcedure(int npcHandle, int questId, int rewardIndex) : ProcedureBase
{
    /// <summary>NPC handle (-1 = remote: pages already triggered).</summary>
    public int NpcHandle { get; } = npcHandle;
    /// <summary>Quest id (-1 = whatever the NPC offers).</summary>
    public int QuestId { get; } = questId;
    /// <summary>Reward choice (-1 none).</summary>
    public int RewardIndex { get; } = rewardIndex;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.QuestDialogue;
    private bool _started, _selected;
    private int _selectCode = -1;
    private Dictionary<int, uint>? _answers;
    /// <summary>Pages answered.</summary>
    public int Pages { get; private set; }
    /// <summary>The terminal command seen (6 accept, 10 done, 1 end), -1 none.</summary>
    public int Terminal { get; private set; } = -1;

    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (!_started)
        {
            if (NpcHandle >= 0 && Camp.FoldFirst(ctx, Action.Value)) return;
            _started = true;
            if (QuestId >= 0 && ctx.Data.Quest(QuestId) is { } q) _answers = QuestScripts.MenuAnswers(q.Script);
            if (NpcHandle >= 0)
            {
                ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, Action.Value)); ctx.World.Bag.StorageOpenAt = -1;
                ctx.Actions.Send(IntentEncoder.NpcClick((ushort)NpcHandle, Action.Value));
            }
            Go("awaitPage", ctx, 4000);
            return;
        }
        if (Expired(ctx))
        {
            if (Terminal >= 0 || (Pages > 0 && _selectCode < 0)) Done();
            // the NPC listed its quests but would not start ours: the server's answer to the selection says why
            else if (_selectCode >= 0) Fail($"quest {QuestId} not offered by the NPC: QUEST_SELECT_START_ACK 0x{_selectCode:X4}");
            else Fail($"no dialogue page in step {Step}");
        }
    }

    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx)
    {
        // QUEST_SELECT_START_ACK {npc, quest, code}: the answer to our pick from the NPC's quest list
        if (e.Kind == InboundKind.QuestAck && e.A == 0x4410 && _selected)
        {
            _selectCode = (int)e.B;
            ctx.Log.Log(LogCategory.Quest, LogLevel.Info, $"talk: quest {QuestId} selected at the NPC, answer 0x{e.B:X4}");
            return;
        }
        if (e.Kind == InboundKind.NpcMenu && QuestId >= 0 && !_selected)
        {
            _selected = true;
            ctx.Actions.Send(IntentEncoder.QuestSelectStart((ushort)e.A, (ushort)QuestId, Action.Value));
            ctx.Log.Log(LogCategory.Quest, LogLevel.Info, $"talk: npc menu (mob {e.A}) -> select quest {QuestId}");
            Go("awaitPage", ctx, 4000);
            return;
        }
        if (e.Kind == InboundKind.NpcMenu)
        {
            ctx.Actions.Send(IntentEncoder.Reply(IntentEncoder.OpNpcMenuAck, 1, Action.Value));
            Go("awaitPage", ctx, 4000);
            return;
        }
        if (e.Kind != InboundKind.QuestDialog) return;
        var quest = (int)e.A; var cmd = (int)e.B;
        if (cmd is 6 or 10 or 1)
        {
            if (QuestId < 0 || quest == QuestId) { Terminal = cmd; ctx.Log.Log(LogCategory.Quest, LogLevel.Note, $"talk: quest {Q(ctx, quest)} {(cmd == 6 ? "ACCEPTED" : cmd == 10 ? "DONE" : "ended")}"); Done(); }
            return;
        }
        if (cmd == 2)
        {
            var dialog = (int)e.C;
            uint answer = _answers is not null && _answers.TryGetValue(dialog, out var a) ? a : 1u;
            if (QuestId >= 0 && quest != QuestId && !_selected && ctx.Data.Dialog(dialog)?.Contains("[MENU]", StringComparison.OrdinalIgnoreCase) == true)
            {
                _selected = true;
                ctx.Actions.Send(IntentEncoder.QuestSelectStart((ushort)e.Handle2, (ushort)QuestId, Action.Value));
                Go("awaitPage", ctx, 4000);
                return;
            }
            // a hand-in with a choice of reward items: the client names its pick before acknowledging the page
            // (captured: QUEST_REWARD_SELECT_ITEM_INDEX {quest, index u32} then the ack; without it the server answers
            // REWARD_NEED_SELECT_ITEM and the quest stays open)
            if (ctx.World.Quests.IsActive(quest) && RewardChoice(ctx, quest) is var pick and >= 0)
            {
                Span<byte> rb = stackalloc byte[6];
                rb[0] = (byte)quest; rb[1] = (byte)(quest >> 8); rb[2] = (byte)pick; rb[3] = (byte)(pick >> 8); rb[4] = rb[5] = 0;
                ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpQuestRewardSelect, rb, Action.Value));
                ctx.Log.Log(LogCategory.Quest, LogLevel.Info, $"talk: quest {Q(ctx, quest)} reward choice: row {pick}");
            }
            ctx.Actions.Send(IntentEncoder.QuestScriptAck((ushort)quest, (byte)cmd, answer, Action.Value));
            Pages++;
            if (ctx.Log.IsEnabled(LogCategory.Quest, LogLevel.Info))
                ctx.Log.Log(LogCategory.Quest, LogLevel.Info, $"talk: quest {Q(ctx, quest)} page {dialog} answered {answer}");
            Go("awaitPage", ctx, 3000);
        }
        else
        {
            ctx.Actions.Send(IntentEncoder.QuestScriptAck((ushort)quest, (byte)cmd, 1, Action.Value));
            Go("awaitPage", ctx, 3000);
        }
    }

    private static string Q(AgentContext ctx, int q) => $"'{ctx.Data.Quest(q)?.Name ?? "?"}' ({q})";

    /// <summary>The reward to pick, as the quest's reward-row index (0-based within its QuestReward rows): the caller's
    /// RewardIndex when given, else the first choosable item our class can use (else the first choosable row); -1 when
    /// the quest has no choice. The index meaning is read from one capture (quest 8: rows 1-5 choosable, the client sent
    /// 5); which item a player would take is judgement (UseClass first).</summary>
    public int RewardChoice(AgentContext ctx, int quest)
    {
        if (ctx.Data.Quest(quest) is not { } q) return -1;
        int first = -1, usable = -1;
        for (var i = 0; i < q.Rewards.Count; i++)
        {
            if (q.Rewards[i].Selectable != 2) continue;
            if (first < 0) first = i;
            if (usable < 0 && q.Rewards[i].Type == 2 && ctx.Data.Item(q.Rewards[i].Value) is { } it
                && (ctx.Data as Fiesta.Bot.Services.ClientData)?.ClassAllowed(it.UseClass, ctx.World.Self.ClassId) != false) usable = i;
        }
        if (first < 0) return -1;
        return RewardIndex >= 0 ? RewardIndex : usable >= 0 ? usable : first;
    }
}

/// <summary>Quest script reading (client QuestData script text).</summary>
public static class QuestScripts
{
    /// <summary>For each SAY page followed by an IF RESULT branch, the answer whose branch reaches ACCEPT or DONE.</summary>
    public static Dictionary<int, uint> MenuAnswers(string? script)
    {
        var map = new Dictionary<int, uint>();
        if (string.IsNullOrWhiteSpace(script)) return map;
        var lines = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var label = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++) if (lines[i].StartsWith(':')) label[lines[i][1..].Trim()] = i;
        static (bool If, uint Val, string Lbl) ParseIf(string l)
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length >= 6 && p[0].Equals("IF", StringComparison.OrdinalIgnoreCase) && p[1].Equals("RESULT", StringComparison.OrdinalIgnoreCase)
                   && p[2] == "==" && uint.TryParse(p[3], out var v) && p[4].Equals("GOTO", StringComparison.OrdinalIgnoreCase)
                ? (true, v, p[5]) : (false, 0, "");
        }
        bool Reaches(string lbl, HashSet<string> seen)
        {
            if (!seen.Add(lbl) || !label.TryGetValue(lbl, out var start)) return false;
            for (var i = start + 1; i < lines.Count; i++)
            {
                var ln = lines[i];
                if (ln.StartsWith(':') || ln.Equals("END", StringComparison.OrdinalIgnoreCase)) return false;
                if (ln.Equals("ACCEPT", StringComparison.OrdinalIgnoreCase) || ln.Equals("DONE", StringComparison.OrdinalIgnoreCase)) return true;
                var f = ParseIf(ln);
                if (f.If && Reaches(f.Lbl, seen)) return true;
                if (ln.StartsWith("GOTO ", StringComparison.OrdinalIgnoreCase)) return Reaches(ln[5..].Trim(), seen);
            }
            return false;
        }
        var lastSay = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var p = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0].Equals("SAY", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[1], out var say)) { lastSay = say; continue; }
            var f = ParseIf(lines[i]);
            if (!f.If || lastSay < 0) continue;
            for (var j = i; j < lines.Count; j++)
            {
                var g = ParseIf(lines[j]);
                if (!g.If) break;
                if (Reaches(g.Lbl, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) { map[lastSay] = g.Val; break; }
            }
            lastSay = -1;
        }
        return map;
    }
}

/// <summary>Gate hop: dismount -> walk into gate range -> click -> answer the menu -> the map link.</summary>
public sealed class GateHopProcedure(int gateHandle, string? destMap) : ProcedureBase
{
    /// <summary>Gate handle.</summary>
    public int GateHandle { get; } = gateHandle;
    /// <summary>Expected destination map.</summary>
    public string? DestMap { get; } = destMap;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.GateHop;
    private ActionId _move = ActionId.None;
    private bool _clicked;
    private int _mapAtStart = -1;
    /// <summary>Distance at which the gate answers a click.</summary>
    public const double ClickRange = 150;
    /// <summary>The action budget: the walk to the gate at run speed plus the click and link (callers pass it to Start).</summary>
    public static int TimeoutFor(AgentContext ctx, int gate)
    {
        var s = ctx.World.Entities.SlotOf(gate);
        if (s < 0) return 20000;
        var (x, y) = ctx.World.Entities.Pos(s, ctx.Now);
        return 20000 + (int)(MoveController.Dist(x, y, ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y) / Math.Max(30, ctx.World.Self.RunSpeed) * 2000);
    }

    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (Step == "start")
        {
            _mapAtStart = ctx.World.Map.MapId;
            if (ctx.World.Self.Mounted)
            {
                // a gate ignores a mounted rider: get off first (using the mount item again), then go on
                var m = Mounts.FindMount(ctx);
                if (m < 0) { Fail("mounted, and no mount item in the bag to get off with"); return; }
                ctx.Actions.Send(IntentEncoder.UseItem((byte)m, 9, Action.Value));
                Go("dismount", ctx, 8000);
                return;
            }
            var s = ctx.World.Entities.SlotOf(GateHandle);
            if (s < 0) { Fail($"gate {GateHandle} not in view"); return; }
            var (gx, gy) = ctx.World.Entities.Pos(s, ctx.Now);
            var d = Dist(ctx.World.Self.Pos, (gx, gy));
            if (d > ClickRange)
            {
                _move = ctx.Move.Order(new MoveOrder(gx, gy, true, ClickRange * 0.6, ActionId.None), ctx, 0);
                Go("walk", ctx, 60000);
            }
            else Go("click", ctx, 0);
            return;
        }
        if (Step == "dismount")
        {
            if (!ctx.World.Self.Mounted) { Go("start", ctx, 0); Advance(ctx); }
            else if (Expired(ctx)) Fail("could not get off the mount");
            return;
        }
        if (Step == "walk")
        {
            var r = ctx.Actions.Get(_move);
            if (r.Id != 0 && !r.Pending)
            {
                if (r.State == ActionState.Acked) Go("click", ctx, 0);
                else Fail($"could not reach the gate: {r.State} {ErrorCodes.Describe(r.Code)}");
            }
            else if (Expired(ctx)) Fail("walk to gate timed out");
            return;
        }
        if (Step == "click" && !_clicked)
        {
            if (Camp.FoldFirst(ctx, Action.Value)) return;
            _clicked = true;
            ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, Action.Value)); ctx.World.Bag.StorageOpenAt = -1;
            ctx.Actions.Send(IntentEncoder.NpcClick((ushort)GateHandle, Action.Value));
            Go("awaitMenu", ctx, 6000);
            return;
        }
        if (Expired(ctx)) Fail($"gate: nothing in step {Step}");
    }

    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx)
    {
        if (e.Kind == InboundKind.ServerMenu && Step == "awaitMenu" && e.Options is { Length: > 0 } opts)
        {
            // the gate's menu: the first option is the move (yes); log the choice
            var pick = opts[0];
            ctx.Actions.Send(IntentEncoder.Reply(IntentEncoder.OpServerMenuAck, pick.Reply, Action.Value));
            ctx.Log.Log(LogCategory.Nav, LogLevel.Note, $"gate: '{e.Text}' answered {pick.Reply}={pick.Text}");
            Go("awaitLink", ctx, 15000);
        }
        else if (e.Kind == InboundKind.MapLink && e.A != _mapAtStart) Done();
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>Use the mount item and wait for the ride-off event.</summary>
public sealed class DismountProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Dismount;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (!ctx.World.Self.Mounted) { Done(); return; }
        if (Step == "start")
        {
            var slot = Mounts.FindMount(ctx);
            if (slot < 0) { Fail("mounted but no mount item in the bag"); return; }
            ctx.Actions.Send(IntentEncoder.UseItem((byte)slot, 9, Action.Value));
            Go("awaitRideOff", ctx, 8000);
            return;
        }
        if (Expired(ctx)) Fail("no ride-off after using the mount");
    }
    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx) { if (e.Kind == InboundKind.Mount && e.A == 0) Done(); }
}

/// <summary>Mount helpers.</summary>
public static class Mounts
{
    /// <summary>The bag slot of a mount item (ItemInfo class of mounts, learned as the class that RIDE_ON follows; the
    /// first item whose ItemInfo class is the mount class), -1 none.</summary>
    public static int FindMount(AgentContext ctx)
    {
        for (var i = 0; i < ctx.World.Bag.Size; i++)
        {
            var (item, _) = ctx.World.Bag.At(i);
            if (item >= 0 && (ctx.Data as ClientData)?.IsMountItem(item) == true) return i;   // named by the client's mount tables
        }
        return -1;
    }
}

/// <summary>Revive: answer the death menu (revive at the respawn point) and wait for the revive packet.</summary>
public sealed class ReviveProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Revive;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        if (Step == "start")
        {
            if (!ctx.World.Self.Dead) { Done(); return; }
            ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpRevive, default, Action.Value));
            Go("awaitRevive", ctx, 25000);
            return;
        }
        if (Expired(ctx)) Fail("no revive packet");
    }
    /// <inheritdoc/>
    public override void Apply(in InboundEvent e, AgentContext ctx)
    {
        if (e.Kind is InboundKind.Revive or InboundKind.MapLink) Done();
    }
}

/// <summary>Tent: not wired yet (fails with a reason).</summary>
public sealed class TentProcedure : ProcedureBase
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Tent;
    /// <inheritdoc/>
    public override void Advance(AgentContext ctx) => Fail("tent is not wired in this runtime yet");
    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt, AgentContext ctx) { }
}

/// <summary>Open the storage keeper: click, answer the menu (the keeper's menu answer opens the storage, as the shop
/// answer opens a merchant's shop), wait for the storage window. A shop list instead fails: that NPC keeps no storage.</summary>
public sealed class StorageProcedure(int keeperHandle, int menuReply = 1) : OpenShopProcedure(keeperHandle, menuReply)
{
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Storage;
    /// <inheritdoc/>
    protected override void Opened(AgentContext ctx, bool storage)
    {
        if (storage) Done(); else Fail($"npc {NpcHandle} opened a shop, not a storage");
    }
}

/// <summary>Runs the bot's active procedures (at most one per resource).</summary>
public sealed class ProcedureRunner
{
    private readonly List<IProcedure> _active = new();
    private readonly List<(IProcedure P, long At)> _recent = new();

    /// <summary>Start a procedure bound to an action.</summary>
    public ActionId Start(IProcedure procedure, ActionKind kind, byte layer, AgentContext ctx, int timeoutMs = -1)
    {
        var res = ActionCatalog.Get(kind)?.Resource ?? Resource.None;
        foreach (var p in _active)
            if (p.Status == ProcedureStatus.Running && (ActionCatalog.Get(ctx.Actions.Get(p.Action).Kind)?.Resource ?? Resource.None) == res && res != Resource.None)
                return ctx.Actions.Refuse(kind, layer, LocalRefusal.Busy);
        var id = ctx.Actions.Begin(kind, layer, 0, 0, timeoutMs);
        if (id.Value < 0) return id;
        procedure.Action = id;
        _active.Add(procedure);
        procedure.Advance(ctx);
        Settle(ctx);
        return id;
    }

    /// <summary>Advance all active procedures.</summary>
    public void Advance(AgentContext ctx)
    {
        for (var i = 0; i < _active.Count; i++)
        {
            var p = _active[i];
            if (p.Status == ProcedureStatus.Running) p.Advance(ctx);
        }
        Settle(ctx);
    }

    /// <summary>Feed an inbound event to active procedures.</summary>
    public void Apply(in InboundEvent evt, AgentContext ctx)
    {
        var changed = false;
        for (var i = 0; i < _active.Count; i++)
            if (_active[i].Status == ProcedureStatus.Running)
            {
                _active[i].Apply(evt, ctx);
                changed |= _active[i].Status != ProcedureStatus.Running;
            }
        // settle at once: the event that finishes a procedure (a map link) may be followed in the same tick by the
        // link going down for the handoff, which must not fail an action that already succeeded
        if (changed) Settle(ctx);
    }

    private void Settle(AgentContext ctx)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            if (p.Status == ProcedureStatus.Running)
            {
                // the action was cancelled or timed out from outside
                var r = ctx.Actions.Get(p.Action);
                if (r.Id != 0 && !r.Pending) p.Cancel(); else continue;
            }
            var ok = p.Status == ProcedureStatus.Succeeded;
            ctx.Actions.Resolve(p.Action, ok ? ActionState.Acked : p.Status == ProcedureStatus.Cancelled ? ActionState.Cancelled : ActionState.Failed, 0);
            ctx.Events.Raise(EventKind.ProcedureDone, (long)p.Kind, ok ? 1 : 0);
            if (!ok) ctx.Log.Text(LogCategory.Core, LogLevel.Note, $"procedure {p.Kind} {p.Status}: {p.FailReason}");
            _active.RemoveAt(i);
            _recent.Add((p, ctx.Now));
            if (_recent.Count > 20) _recent.RemoveAt(0);
        }
    }

    /// <summary>Active procedures (for the debugger).</summary>
    public IReadOnlyList<IProcedure> Active => _active;
    /// <summary>Recently finished.</summary>
    public IReadOnlyList<(IProcedure P, long At)> Recent => _recent;
}

/// <summary>A click on an NPC or a gate from inside a pitched tent is ignored by the server without any answer (NextM
/// camped at 02:24:35 and clicked three gates over five minutes: no menu, no error, no log). Every click folds the tent
/// first and waits for the fold ack.</summary>
public static class Camp
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentContext, System.Runtime.CompilerServices.StrongBox<long>> _sentAt = new();

    /// <summary>True while the tent is still pitched (a fold was sent, at most once every 2 s): the caller waits.</summary>
    public static bool FoldFirst(AgentContext ctx, int actionId)
    {
        if (!ctx.World.Self.TentPitched) return false;
        var at = _sentAt.GetOrCreateValue(ctx);
        if (ctx.Now - at.Value >= 2000)
        {
            at.Value = ctx.Now;
            ctx.Actions.Send(IntentEncoder.Raw(IntentEncoder.OpFoldTent, default, actionId));
            ctx.Log.Log(LogCategory.Core, LogLevel.Info, $"tent: folding before an npc click (a camped click gets no answer)");
        }
        return true;
    }
}
