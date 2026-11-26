using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Agent;

/// <summary>Why the game refused a move.</summary>
public enum MoveFailKind : byte
{
    /// <summary>Our `from` was more than 200 u from the game's position (adopt the carried position; no wall learned).</summary>
    Desync,
    /// <summary>The segment was refused on a plausible position (a wall the static grid lacks; learn in the bot's overlay).</summary>
    Blocked,
}

/// <summary>What the movement controller is asked to do.</summary>
public readonly record struct MoveOrder(double X, double Y, bool UsePath, double ArriveWithin, ActionId Action);

/// <summary>Owns MOVERUN/STOP: requests the path (a job), streams legs at the client's pace, keeps the believed position
/// within the game's tolerance, classifies MOVEFAIL, and tells the combat controller about moves.</summary>
public sealed class MoveController
{
    private readonly List<(uint X, uint Y)> _path = new();
    private int _leg = -1;
    private double _legFx, _legFy, _legTx, _legTy;
    private long _legStart;
    private bool _awaitFold;
    private ActionId _action = ActionId.None;
    private MoveOrder? _order;
    private JobTicket _ticket = JobTicket.None;
    private volatile bool _jobDone;
    private bool _jobOk;
    private int _fails;
    private bool _waitMesh;
    private (uint X, uint Y) _lastSentFrom;
    /// <summary>Maximum leg length sent in one MOVERUN (longer funnel legs are split).</summary>
    public const double MaxLeg = 800;
    /// <summary>After a spawn (map login, link, revive) the game refuses moves for about a second (measured on the
    /// live bots: 63 % of MOVEFAILs were moves sent within 1 s of a spawn); orders wait this long.</summary>
    public const int SpawnSettleMs = 1000;
    /// <summary>Moves streamed / failed (telemetry).</summary>
    public long Legs, MoveFails, Desyncs;

    /// <summary>Moving now?</summary>
    public bool Moving => _leg >= 0 && _leg < _path.Count;
    /// <summary>Where the current order goes, null without one.</summary>
    public (double X, double Y)? Destination => _order is { } o ? (o.X, o.Y) : null;
    /// <summary>The leg being walked now (its end point), null standing.</summary>
    public (double X, double Y)? LegTarget => Moving ? (_legTx, _legTy) : null;
    /// <summary>Index of the path point being walked to (-1 standing).</summary>
    public int Leg => _leg;
    /// <summary>Waiting for a path job.</summary>
    public bool Planning => _order is not null && _ticket.Value >= 0 && !_jobDone;
    /// <summary>Current waypoint list (sensor view; the list object is reused).</summary>
    public IReadOnlyList<(uint X, uint Y)> Path => _path;
    /// <summary>The current order's action.</summary>
    public ActionId Current => _action;
    /// <summary>Raised to the combat controller when a leg starts.</summary>
    public Action<AgentContext>? Moved { get; set; }

    /// <summary>Start a move (replaces the current one); returns the MOVE action.</summary>
    public ActionId Order(MoveOrder order, AgentContext ctx, byte layer)
    {
        var kind = order.UsePath ? ActionKind.Move : ActionKind.MoveDirect;
        var d = Dist(ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y, order.X, order.Y);
        var timeout = (int)(d / Math.Max(30, ctx.World.Self.RunSpeed) * 1000 * 2.5) + 5000;
        // the same destination again (a state tick re-issuing its move): keep the running order and its action
        if (_order is { } cur && _action.Value > 0 && cur.UsePath == order.UsePath && Dist(cur.X, cur.Y, order.X, order.Y) < Math.Max(25, order.ArriveWithin * 0.5)
            && !ctx.Actions.Preempted(layer, Resource.Movement))
            return _action;
        var id = order.Action.Value > 0 ? order.Action : ctx.Actions.Begin(kind, layer, (long)order.X, (long)order.Y, timeout);
        if (id.Value < 0) return id;
        if (_action.Value > 0 && _action != id) ctx.Actions.Resolve(_action, ActionState.Cancelled, 0);
        _action = id; _order = order; _fails = 0;
        _path.Clear(); _leg = -1;
        if (d <= order.ArriveWithin) { Arrive(ctx); return id; }
        Plan(ctx);
        return id;
    }

    private void Plan(AgentContext ctx)
    {
        var o = _order!.Value;
        _jobDone = false;
        _waitMesh = false;
        if (o.UsePath && ctx.Nav.Layer(ctx.World.Map.MapName) is null && !ctx.Nav.Ready(ctx.World.Map.MapName))
        {
            // the map's walkability is still loading on the job pool: plan when it is ready
            _waitMesh = true; _ticket = JobTicket.None;
            return;
        }
        if (!o.UsePath || ctx.Nav.Layer(ctx.World.Map.MapName) is null)
        {
            _path.Clear(); _path.Add(((uint)o.X, (uint)o.Y));
            _jobDone = true; _jobOk = true; _ticket = JobTicket.None;
            return;
        }
        ctx.Overlay.OnMap(ctx.World.Map.MapName);   // the blocks learned on this map (kept across visits)
        var (sx, sy) = ctx.World.Self.Pos;
        _ticket = ctx.Nav.RequestPath(ctx.BotIndex, ctx.World.Map.MapName, ctx.Overlay.Count > 0 ? ctx.Overlay.SnapshotBlocked(ctx.Now) : null,
            (uint)sx, (uint)sy, (uint)o.X, (uint)o.Y, (_, ok) => { _jobOk = ok; _jobDone = true; ctx.PostJobDone(_ticket, ok); });
        if (_ticket.Value < 0) Fail(ctx, LocalRefusal.NoPath, "path queue full");
    }

    /// <summary>Stop and commit the position (sends STOP at the believed position).</summary>
    public void Stop(AgentContext ctx, bool resolveCancelled = true)
    {
        var was = Moving;
        if (_action.Value > 0 && resolveCancelled) ctx.Actions.Resolve(_action, ActionState.Cancelled, 0);
        _action = ActionId.None; _order = null; _path.Clear(); _leg = -1;
        if (was)
        {
            var (x, y) = ctx.World.Self.Pos;
            ctx.Actions.Send(IntentEncoder.Stop((uint)x, (uint)y, -1));
            ctx.World.Self.ServerPos = ((uint)x, (uint)y);
        }
    }

    /// <summary>Step 4 of the tick.</summary>
    public void Advance(AgentContext ctx)
    {
        var self = ctx.World.Self;
        if (_awaitFold && _leg >= 0) { StartLeg(ctx); if (_awaitFold) return; }
        // the order's action was resolved from outside (timed out, cancelled by the caller): stop walking for it
        if (_action.Value > 0 && ctx.Actions.Get(_action) is { Id: > 0, Pending: false })
        {
            ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"move {_action.Value} ended from outside ({ctx.Actions.Get(_action).State}): stopping");
            Stop(ctx, resolveCancelled: false);
        }
        // believed position along the current leg
        if (Moving)
        {
            var d = Dist(_legFx, _legFy, _legTx, _legTy);
            var t = (ctx.Now - _legStart) / 1000.0 * self.RunSpeed;
            if (t >= d)
            {
                self.Pos = (_legTx, _legTy);
                NextLeg(ctx);
            }
            else self.Pos = (_legFx + (_legTx - _legFx) / d * t, _legFy + (_legTy - _legFy) / d * t);
            return;
        }
        if (_order is null) return;
        if (_waitMesh) { if (ctx.Nav.Ready(ctx.World.Map.MapName)) Plan(ctx); return; }
        if (!_jobDone) return;
        if (_leg >= 0) return;
        // the path arrived (or a direct move): start streaming once the spawn settle has passed
        if (self.SpawnedAt >= 0 && ctx.Now - self.SpawnedAt < SpawnSettleMs) return;
        if (self.Dead) { Fail(ctx, LocalRefusal.Dead, "dead"); return; }
        if (_ticket.Value >= 0)
        {
            var r = ctx.Nav.Result(_ticket);
            ctx.Jobs.Forget(_ticket);
            _ticket = JobTicket.None;
            if (r is null || r.Status != PathStatus.Ready || !_jobOk) { Fail(ctx, LocalRefusal.NoPath, r?.FailReason ?? "path job failed"); return; }
            _path.Clear();
            foreach (var p in r.Waypoints) _path.Add(p);
            if (ctx.Log.IsEnabled(LogCategory.Nav, LogLevel.Debug))
                ctx.Log.Log(LogCategory.Nav, LogLevel.Debug, $"path to ({_order.Value.X:0},{_order.Value.Y:0}): {_path.Count} waypoints, {r.Length:0} u{(r.Partial ? " (partial: goal not reachable, walking to the nearest point)" : "")}");
        }
        _leg = 0;
        StartLeg(ctx);
    }

    private void StartLeg(AgentContext ctx)
    {
        var self = ctx.World.Self;
        if (_leg >= _path.Count) { Arrive(ctx); return; }
        // a move from a pitched tent is not taken by the game either (NextM walked 133 legs across RouCos03 camped while
        // the server kept it at the tent, 02:24-02:33): fold first, the leg starts on the fold ack (Advance)
        _awaitFold = Camp.FoldFirst(ctx, _action.Value);
        if (_awaitFold) return;
        var (fx, fy) = self.Pos;
        var (tx, ty) = (_path[_leg].X, (double)_path[_leg].Y);
        var d = Dist(fx, fy, tx, ty);
        if (d > MaxLeg)
        {
            // split a long leg so a refusal costs little and the believed position stays close to the game's
            var k = MaxLeg / d;
            _path.Insert(_leg, ((uint)(fx + (tx - fx) * k), (uint)(fy + (ty - fy) * k)));
            (tx, ty) = (_path[_leg].X, _path[_leg].Y);
        }
        if (Dist(fx, fy, tx, ty) < 1) { _leg++; StartLeg(ctx); return; }
        _legFx = fx; _legFy = fy; _legTx = tx; _legTy = ty; _legStart = ctx.Now;
        _lastSentFrom = ((uint)fx, (uint)fy);
        ctx.Actions.Send(IntentEncoder.MoveRun((uint)fx, (uint)fy, (uint)tx, (uint)ty, _action.Value));
        var dd = Dist(fx, fy, tx, ty);
        if (dd > 1e-6) self.Facing = ((tx - fx) / dd, (ty - fy) / dd);
        Legs++;
        Moved?.Invoke(ctx);
    }

    private void NextLeg(AgentContext ctx)
    {
        _leg++;
        if (_order is { } ord && Dist(ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y, ord.X, ord.Y) <= ord.ArriveWithin && _leg < _path.Count)
        {
            Stop(ctx, resolveCancelled: false);
            Arrive(ctx);
            return;
        }
        if (_leg >= _path.Count) { Arrive(ctx); return; }
        StartLeg(ctx);
    }

    private void Arrive(AgentContext ctx)
    {
        var id = _action;
        _leg = -1; _path.Clear(); _order = null; _action = ActionId.None;
        ctx.World.Self.ServerPos = ((uint)ctx.World.Self.Pos.X, (uint)ctx.World.Self.Pos.Y);
        ctx.Actions.Resolve(id, ActionState.Acked, 0);
        ctx.Events.Raise(EventKind.Arrived, id.Value);
    }

    private void Fail(AgentContext ctx, LocalRefusal why, string text)
    {
        var id = _action;
        _leg = -1; _path.Clear(); _order = null; _action = ActionId.None;
        ctx.Actions.Resolve(id, ActionState.Failed, (ushort)why);
        if (ctx.Log.IsEnabled(LogCategory.Nav, LogLevel.Info)) ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"move failed: {text}");
    }

    /// <summary>Feed MOVEFAIL / map events.</summary>
    public void Apply(in InboundEvent evt, AgentContext ctx)
    {
        switch (evt.Kind)
        {
            case InboundKind.MoveFail:
            {
                MoveFails++;
                var kind = Classify(_lastSentFrom, (evt.X, evt.Y));
                ctx.World.Self.Pos = (evt.X, evt.Y);
                if (kind == MoveFailKind.Desync) Desyncs++;
                ctx.Events.Raise(EventKind.MoveFailed, (long)kind);
                if (ctx.Log.IsEnabled(LogCategory.Nav, LogLevel.Info))
                    ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"MOVEFAIL {kind}: game has us at ({evt.X},{evt.Y}), our leg was ({_legFx:0},{_legFy:0})->({_legTx:0},{_legTy:0})");
                if (!Moving && _order is null) break;
                if (kind == MoveFailKind.Blocked && Moving)
                {
                    // the game refused the whole leg: every tile it crosses past the one we stand on is learned as blocked
                    // for this map (the client's grid allows moves the game refuses in places), so the
                    // next path goes round it instead of repeating the same refused leg
                    var dx = _legTx - evt.X; var dy = _legTy - evt.Y; var d = Math.Sqrt(dx * dx + dy * dy);
                    var (sx, sy) = BlockLayer.ToTile(evt.X, evt.Y);
                    for (var s = 0.0; d > 1 && s <= d; s += BlockLayer.WorldPerTile / 2)
                    {
                        var (tx, ty) = BlockLayer.ToTile(evt.X + dx / d * s, evt.Y + dy / d * s);
                        if (tx != sx || ty != sy) ctx.Overlay.LearnBlocked(tx, ty, long.MaxValue);
                    }
                    ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"nav: the game refused ({_legFx:0},{_legFy:0})->({_legTx:0},{_legTy:0}) on {ctx.World.Map.MapName}: its tiles are blocked for this map now, re-planning");
                }
                if (++_fails > 4 || _order is null) { Fail(ctx, kind == MoveFailKind.Desync ? LocalRefusal.Desync : LocalRefusal.MoveFailed, $"{_fails} refusals"); break; }
                _leg = -1; _path.Clear();
                Plan(ctx);
                break;
            }
            case InboundKind.MapLink:
            case InboundKind.Revive:
            case InboundKind.MapLoginAck:
                if (_action.Value > 0) ctx.Actions.Resolve(_action, ActionState.Failed, (ushort)LocalRefusal.NotInWorld);
                _action = ActionId.None; _order = null; _path.Clear(); _leg = -1;
                ctx.Overlay.Clear();
                ctx.Overlay.OnMap(ctx.World.Map.MapName);
                break;
        }
    }

    /// <summary>Classify a MOVEFAIL from our last sent `from` and the carried position.</summary>
    public static MoveFailKind Classify((uint X, uint Y) sentFrom, (uint X, uint Y) carried)
        => Dist(sentFrom.X, sentFrom.Y, carried.X, carried.Y) > GameRules.MoveFromTolerance ? MoveFailKind.Desync : MoveFailKind.Blocked;

    public static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}

/// <summary>Owns battle mode, targeting, BASHSTART/STOP and the cast state (reach from GameRules, nothing learned).</summary>
public sealed class CombatController
{
    private int _bashTarget = -1;
    private bool _bashWanted;
    private bool _rearm;
    private long _castUntil;
    private int _castSkill = -1;
    private int _castTarget = -1;
    private double _castDist = -1;
    /// <summary>Casts the game started (SKILLBASH_HIT_OBJ_START for us) and casts it refused (CAST_FAIL), since the bot
    /// started: a fight's own count is the difference across it.</summary>
    public long CastsStarted, CastsRefused;
    private int _assertedTarget = -1;
    private readonly Intent[] _buf = new Intent[8];
    private bool _landedSinceStart;
    private int _refusedStarts;
    /// <summary>The last target whose swing starts the game refused (no damage before CEASE_FIRE, twice), and when.</summary>
    public int RefusedTarget { get; private set; } = -1;
    /// <summary>When (ms).</summary>
    public long RefusedAt { get; private set; } = -1;
    /// <summary>Server-confirmed / asserted target.</summary>
    public int Target => _assertedTarget;
    /// <summary>Bashing now?</summary>
    public bool Bashing => _bashWanted;
    /// <summary>Bash target.</summary>
    public int BashTarget => _bashTarget;
    /// <summary>Casting (cast bar open).</summary>
    public bool Casting(long now) => now < _castUntil;
    /// <summary>Last swing we landed (ms), -1 none.</summary>
    public long LastSwingAt { get; private set; } = -1;
    /// <summary>Damage out in the current fight and when it started.</summary>
    public long FightDamage; private long _fightStart = -1;

    /// <summary>Our damage per second over the current fight.</summary>
    public double DpsOut(long now) => _fightStart < 0 ? 0 : FightDamage / Math.Max(1.0, (now - _fightStart) / 1000.0);

    private void EnsureTarget(int target, AgentContext ctx, int action, Span<Intent> buf, ref int n)
    {
        if (target != _assertedTarget) { buf[n++] = IntentEncoder.Target((ushort)target, action); _assertedTarget = target; }
    }

    private void EnsureMode(AgentContext ctx, Span<Intent> buf, ref int n, int action)
    {
        // battle mode once per zone session: the real client sends it after zone entry / item use, not per cast
        if (!ctx.World.Self.BattleMode) { buf[n++] = IntentEncoder.ChangeMode(2, action); ctx.World.Self.BattleMode = true; }
    }

    /// <summary>Cast a skill at an object; sequencing (target, mode, stop, face) is handled here.</summary>
    public ActionId Cast(int skillId, int targetHandle, byte layer, AgentContext ctx)
    {
        var why = GameRules.CanUse(ctx, skillId, targetHandle);
        if (why != CanUseReason.Ok) return ctx.Actions.Refuse(ActionKind.Cast, layer, MapReason(why));
        var sk = ctx.Data.Skill(skillId)!;
        Span<Intent> buf = _buf;
        var n = 0;
        var self = targetHandle == ctx.World.Self.Handle;
        if (!self) EnsureTarget(targetHandle, ctx, -1, buf, ref n);
        EnsureMode(ctx, buf, ref n, -1);
        // the client stops before a cast, except a moving skill (ActiveSkill IsMovingSkill) cast on the way somewhere else:
        // one cast while walking into the target's range stops first too (its STOP_REQ before the cast)
        if (ctx.Move.Moving && (!sk.IsMovingSkill || Approaching(ctx, targetHandle))) { ctx.Move.Stop(ctx); }
        // inside an instance, while our swings on this target land, we already face it (the swing stream turns us): the
        // face step's MOVERUN is refused in tight rooms and turns us away again (all casts refused 0x0FCA there)
        else if (ctx.World.Map.IsInstance && Bashing && BashTarget == targetHandle && LastSwingAt >= 0 && ctx.Now - LastSwingAt < 2500) { }
        else if (!self && sk.UsableDegree > 0 && ctx.World.Entities.SlotOf(targetHandle) is var s and >= 0)
        {
            // a skill with a usable arc must be cast facing the target: a short face step + STOP when we are not
            var (tx, ty) = ctx.World.Entities.Pos(s, ctx.Now);
            var (px, py) = ctx.World.Self.Pos;
            var d = MoveController.Dist(px, py, tx, ty);
            if (d > 1)
            {
                var (fx, fy) = ctx.World.Self.Facing;
                var cos = ((tx - px) * fx + (ty - py) * fy) / d;
                if (cos < Math.Cos(sk.UsableDegree * Math.PI / 360.0))
                {
                    var step = Math.Min(16, d - 1);
                    uint ox = (uint)px, oy = (uint)py, nx = (uint)(px + (tx - px) / d * step), ny = (uint)(py + (ty - py) / d * step);
                    buf[n++] = IntentEncoder.MoveRun(ox, oy, nx, ny, -1);
                    buf[n++] = IntentEncoder.Stop(nx, ny, -1);
                    ctx.World.Self.Pos = (nx, ny);
                    ctx.World.Self.Facing = ((tx - px) / d, (ty - py) / d);
                }
            }
        }
        buf[n++] = IntentEncoder.CastObject((ushort)skillId, (ushort)targetHandle, -1);
        var id = ctx.Actions.Issue(ActionKind.Cast, layer, skillId, targetHandle, buf[..n], 1500 + sk.CastTimeMs);
        if (id.Value > 0)
        {
            _castSkill = skillId; _castTarget = targetHandle; _castUntil = ctx.Now + sk.CastTimeMs + 300;
            var ts = ctx.World.Entities.SlotOf(targetHandle);
            if (ts >= 0) { var (cx, cy) = ctx.World.Entities.Pos(ts, ctx.Now); _castDist = MoveController.Dist(ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y, cx, cy); }
            else _castDist = -1;
        }
        return id;
    }

    // our move is a walk towards the target: its destination is nearer the target than we are
    private static bool Approaching(AgentContext ctx, int targetHandle)
    {
        var s = ctx.World.Entities.SlotOf(targetHandle);
        if (s < 0 || ctx.Move.Destination is not { } dst) return false;
        var (tx, ty) = ctx.World.Entities.Pos(s, ctx.Now);
        return MoveController.Dist(dst.X, dst.Y, tx, ty) < MoveController.Dist(ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y, tx, ty);
    }

    private static LocalRefusal MapReason(CanUseReason r) => r switch
    {
        CanUseReason.NotLearned => LocalRefusal.UnknownSkill,
        CanUseReason.Range or CanUseReason.LineOfFire => LocalRefusal.OutOfRange,
        CanUseReason.Moving => LocalRefusal.Moving,
        CanUseReason.Mounted => LocalRefusal.Mounted,
        CanUseReason.Target => LocalRefusal.InvalidTarget,
        CanUseReason.Dead => LocalRefusal.Dead,
        _ => LocalRefusal.NotReady,
    };

    /// <summary>Queue a ground cast.</summary>
    public ActionId CastAt(int skillId, double x, double y, byte layer, AgentContext ctx)
    {
        var sk = ctx.Data.Skill(skillId);
        if (sk is null || !ctx.World.Skills.Has(skillId)) return ctx.Actions.Refuse(ActionKind.CastGround, layer, LocalRefusal.UnknownSkill);
        Span<Intent> buf = _buf;
        var n = 0;
        EnsureMode(ctx, buf, ref n, -1);
        if (ctx.Move.Moving && !sk.IsMovingSkill) ctx.Move.Stop(ctx);
        buf[n++] = IntentEncoder.CastGround((ushort)skillId, (uint)x, (uint)y, -1);
        var id = ctx.Actions.Issue(ActionKind.CastGround, layer, skillId, -1, buf[..n], 1500 + sk.CastTimeMs);
        if (id.Value > 0) { _castSkill = skillId; _castTarget = -1; _castUntil = ctx.Now + sk.CastTimeMs + 300; }
        return id;
    }

    /// <summary>Start auto-attack on a target. The caller must be within bash reach (walk there first): nothing moves us
    /// toward the target except our own moves.</summary>
    public ActionId Bash(int targetHandle, byte layer, AgentContext ctx)
    {
        var s = ctx.World.Entities.SlotOf(targetHandle);
        if (s < 0 || ctx.World.Entities.IsDead(s)) return ctx.Actions.Refuse(ActionKind.Bash, layer, LocalRefusal.InvalidTarget);
        Span<Intent> buf = _buf;
        var n = 0;
        EnsureTarget(targetHandle, ctx, -1, buf, ref n);
        EnsureMode(ctx, buf, ref n, -1);
        if (ctx.Move.Moving) ctx.Move.Stop(ctx);
        var (px, py) = ctx.World.Self.Pos;
        buf[n++] = IntentEncoder.Stop((uint)px, (uint)py, -1);
        buf[n++] = IntentEncoder.Bash(true, -1);
        var id = ctx.Actions.Issue(ActionKind.Bash, layer, targetHandle, 0, buf[..n]);
        if (id.Value > 0)
        {
            if (_bashTarget != targetHandle) _refusedStarts = 0;
            _bashTarget = targetHandle; _bashWanted = true; _rearm = false; _landedSinceStart = false;
            if (_fightStart < 0) { _fightStart = ctx.Now; FightDamage = 0; }
        }
        return id;
    }

    /// <summary>Stop auto-attack.</summary>
    public ActionId StopBash(byte layer, AgentContext ctx)
    {
        _bashWanted = false; _bashTarget = -1; _rearm = false;
        Span<Intent> buf = [IntentEncoder.Bash(false, -1)];
        return ctx.Actions.Issue(ActionKind.StopBash, layer, 0, 0, buf);
    }

    /// <summary>A selection sent outside this controller (the NPC click's TARGETTING): remembered so the next cast or
    /// bash re-selects its own target.</summary>
    public void NoteTarget(int handle) => _assertedTarget = handle;

    /// <summary>Set the selection explicitly (act.target).</summary>
    public ActionId SetTarget(int handle, byte layer, AgentContext ctx)
    {
        _assertedTarget = handle;
        Span<Intent> buf = [IntentEncoder.Target((ushort)handle, -1)];
        return ctx.Actions.Issue(ActionKind.Target, layer, handle, 0, buf);
    }

    /// <summary>Called by the move controller: a move ends the swing stream; the bash is re-armed when we stop.</summary>
    public void OnMoved(AgentContext ctx)
    {
        if (_bashWanted) _rearm = true;
        if (Casting(ctx.Now) && ctx.Data.Skill(_castSkill) is { IsMovingSkill: false }) _castUntil = 0;
    }

    /// <summary>Step 5 of the tick: re-arm the bash after moves, drop a dead target.</summary>
    public void Advance(AgentContext ctx)
    {
        if (!_bashWanted) return;
        var s = ctx.World.Entities.SlotOf(_bashTarget);
        if (s < 0 || ctx.World.Entities.IsDead(s)) { _bashWanted = false; _bashTarget = -1; _rearm = false; if (_fightStart >= 0 && !ctx.World.Self.InCombat(ctx.Now)) _fightStart = -1; return; }
        if (_rearm && !ctx.Move.Moving)
        {
            _rearm = false;
            var (px, py) = ctx.World.Self.Pos;
            ctx.Actions.Send(IntentEncoder.Stop((uint)px, (uint)py, -1));
            ctx.Actions.Send(IntentEncoder.Bash(true, -1));
        }
    }

    // a cast at a mob that spawned in view: its age then bounds the spawn guard (refused: at least; started: at most)
    private static bool SpawnGuardSample(AgentContext ctx, int target, bool refused)
    {
        var s = target >= 0 ? ctx.World.Entities.SlotOf(target) : -1;
        if (s < 0 || ctx.World.Entities.SpawnedAt(s) < 0) return false;
        var age = ctx.Now - ctx.World.Entities.SpawnedAt(s);
        var mob = ctx.World.Entities.MobId(s);
        var f = refused ? MobFact.SpawnRefused : MobFact.SpawnTaken;
        if (refused) { ctx.Knowledge.ObserveAtLeast(mob, f, age); ctx.Knowledge.ObserveAtLeast(-1, f, age); }
        else { ctx.Knowledge.ObserveAtMost(mob, f, age); ctx.Knowledge.ObserveAtMost(-1, f, age); }
        if (ctx.Log.IsEnabled(LogCategory.Combat, LogLevel.Info))
        {
            var lo = ctx.Knowledge.Mob(mob, MobFact.SpawnRefused); var hi = ctx.Knowledge.Mob(mob, MobFact.SpawnTaken);
            ctx.Log.Log(LogCategory.Combat, LogLevel.Info, $"spawn guard: cast at {target} (mob {mob}) {(refused ? "refused" : "taken")} {age} ms after its spawn; refused up to {lo.Value:0} ms, taken from {hi.Value:0} ms");
        }
        return true;
    }

    /// <summary>Feed combat events.</summary>
    public void Apply(in InboundEvent e, AgentContext ctx)
    {
        switch (e.Kind)
        {
            case InboundKind.Damage when e.Handle == ctx.World.Self.Handle:
                if (!_landedSinceStart && ctx.Log.IsEnabled(LogCategory.Combat, LogLevel.Info) && ctx.World.Entities.SlotOf(e.Handle2) is var ls and >= 0)
                {
                    var (tx, ty) = ctx.World.Entities.Pos(ls, ctx.Now);
                    ctx.Log.Log(LogCategory.Combat, LogLevel.Info, $"swing landed on {e.Handle2} at dist {MoveController.Dist(ctx.World.Self.Pos.X, ctx.World.Self.Pos.Y, tx, ty):0}");
                }
                LastSwingAt = ctx.Now; FightDamage += Math.Max(0, e.A); _landedSinceStart = true; _refusedStarts = 0;
                break;
            case InboundKind.SkillDamage when e.Handle == ctx.World.Self.Handle:
                FightDamage += e.A; _castUntil = 0;
                break;
            case InboundKind.SkillCastStart:
                CastsStarted++;
                SpawnGuardSample(ctx, (int)e.Handle2, refused: false);
                break;
            // 0x0FCD "Skill has not been finished normally." (the client's text): observed only at a mob that spawned in
            // view under ~2 s before (every one of 121 runs, 2026-11-26): learn the window, CanUse waits it out
            case InboundKind.CastFail when e.A == 0x0FCD:
                CastsRefused++;
                _castUntil = 0;
                if (!SpawnGuardSample(ctx, _castTarget, refused: true))
                    ctx.Log.Log(LogCategory.Combat, LogLevel.Note, $"skill {_castSkill} at {_castTarget}: refused 0x0FCD at a target not seen spawning");
                break;
            // 0x0FC4 "Casting another skill now." / 0x0FC6 "Incorrect Skill." (the client's own error texts): every cast
            // waits 600 ms, as the old drivers learned - re-pressing at tick cadence drew runs of refusals (NextM: 138
            // 0x0FC4 refusals in an hour, 01:00-01:57)
            // 0x0FC1 "Cannot use the skill right after logging in." (the client's text): learn how long after our map entry
            case InboundKind.CastFail when e.A == 0x0FC1:
            {
                CastsRefused++;
                _castUntil = 0;
                if (ctx.World.Self.SpawnedAt >= 0)
                {
                    var age = ctx.Now - ctx.World.Self.SpawnedAt;
                    ctx.Knowledge.ObserveAtLeast(-1, MobFact.LoginRefused, age);
                    ctx.Log.Log(LogCategory.Combat, LogLevel.Info, $"login guard: cast refused {age} ms after our map entry; refused up to {ctx.Knowledge.Mob(-1, MobFact.LoginRefused).Value:0} ms");
                }
                break;
            }
            case InboundKind.CastFail when e.A is 0x0FC4 or 0x0FC6:
                CastsRefused++;
                _castUntil = ctx.Now + 600;
                break;
            // out of range although the client's own range rule said in range: the game holds us (or the target) elsewhere
            // than we believe - re-sync our position to the game's, so the next check walks in from where we really are
            case InboundKind.CastFail when e.A == 0x0FCA && _castSkill >= 0:
                CastsRefused++;
                _castUntil = 0;
                ctx.Log.Log(LogCategory.Combat, LogLevel.Info, $"skill {_castSkill}: refused out of range at {_castDist:0} by our position: re-syncing to the game's ({ctx.World.Self.ServerPos.X},{ctx.World.Self.ServerPos.Y})");
                ctx.World.Self.Pos = (ctx.World.Self.ServerPos.X, ctx.World.Self.ServerPos.Y);
                break;
            case InboundKind.CastFail:
                CastsRefused++;
                _castUntil = 0;
                break;
            case InboundKind.CastAborted:
                _castUntil = 0;
                break;
            case InboundKind.BashStop when e.Handle == ctx.World.Self.Handle:
                // the swing stream ended. Re-arm while the target lives - unless the stream landed nothing: then the
                // game refused the start (out of its reach) and re-arming from the same spot only repeats it. Two
                // such refusals in a row stop the bash and raise CAST_RESULT(-1, L_OUT_OF_RANGE) so the brain closes in.
                if (_bashWanted)
                {
                    if (_landedSinceStart) { _refusedStarts = 0; _rearm = true; }
                    else if (++_refusedStarts >= 2)
                    {
                        _bashWanted = false; _rearm = false; _refusedStarts = 0;
                        ctx.Events.Raise(EventKind.CastResult, -1, (long)LocalRefusal.OutOfRange);
                        RefusedTarget = _bashTarget; RefusedAt = ctx.Now;
                    }
                    else _rearm = true;
                }
                _landedSinceStart = false;
                if (ctx.Log.IsEnabled(LogCategory.Combat, LogLevel.Info) && ctx.World.Entities.SlotOf(_bashTarget) is var bs and >= 0)
                {
                    var (tx, ty) = ctx.World.Entities.Pos(bs, ctx.Now);
                    var (px, py) = ctx.World.Self.Pos;
                    ctx.Log.Log(LogCategory.Combat, LogLevel.Info, $"cease fire on {_bashTarget}: us ({px:0},{py:0}) server ({ctx.World.Self.ServerPos.X},{ctx.World.Self.ServerPos.Y}) target ({tx:0},{ty:0}) moving {ctx.World.Entities.Velocity(bs, ctx.Now).Vx:0.#} dist {MoveController.Dist(px, py, tx, ty):0} reach {GameRules.BashReach(ctx.World.Entities.BodyOf(bs), GameRules.Weapon(ctx)):0}");
                }
                break;
            case InboundKind.EntityLeft when e.Handle == _assertedTarget:
            case InboundKind.EntityDied when e.Handle == _assertedTarget:
                _assertedTarget = -1;
                break;
            case InboundKind.MapLink or InboundKind.MapLoginAck or InboundKind.Revive:
                _assertedTarget = -1; _bashWanted = false; _bashTarget = -1; _castUntil = 0; _fightStart = -1;
                break;
        }
    }
}

/// <summary>Why <see cref="GameRules.CanUse"/> said no (the Lua `skills.R_*` codes).</summary>
public enum CanUseReason : byte
{
    Ok, NotLearned, Cooldown, Sp, Hp, Dead, State, Mounted, Casting, Moving, Weapon, Target, Range, LineOfFire, Map,
    /// <summary>The target spawned in view moments ago: the game refuses skills at it (0x0FCD) for a learned window.</summary>
    Spawning,
    /// <summary>We entered the map moments ago: the game refuses our casts (0x0FC1) for a learned window.</summary>
    JustEntered,
}

/// <summary>Weapon class for reach.</summary>
public enum WeaponClass : byte { Melee, Bow, Crossbow, Staff, Wand, None }

/// <summary>The game's rules in one tested place (REVIEW section 5; each stated as measured behaviour).</summary>
public static class GameRules
{
    /// <summary>A player's body size.</summary>
    public const double PlayerBody = 50;
    // ---- the game client's range rules (its GetSkillableDistance / GetAttackableDistance and their callers):
    //   skill: a mob target adds its body (MobInfo AbsoluteSize * 50 / 1000) to the skill's Range, a character target 50,
    //   no target nothing, no skill row 50; it is cast while Range + 50 exceeds the distance, else walked to Range - 1.
    //   auto-attack: a gather object (MobInfo Type 8, 9, 10, 21, 22) 10, a mob its body (+ 450 for a bow), a character
    //   50 (450 for a bow); started within that + 50 - 10, else walked to that - 1.
    /// <summary>What the client adds to a skill's range for a character target, and the slack a cast is allowed.</summary>
    public const double ClientSlack = 50;
    /// <summary>A bow's extra auto-attack reach in the client's rule.</summary>
    public const double BowExtra = 450;
    /// <summary>The auto-attack reach of a gather object (the client's fixed 10).</summary>
    public const double GatherReach = 10;
    /// <summary>The client's swing start tolerance: within attackable + 50 - 10.</summary>
    public const double SwingStartSlack = ClientSlack - 10;
    /// <summary>MobInfo Type values the client treats as gather objects (fixed auto-attack reach).</summary>
    public static readonly HashSet<int> GatherTypes = [8, 9, 10, 21, 22];
    /// <summary>Maximum distance between our move's `from` and the game's position.</summary>
    public const double MoveFromTolerance = 200;
    /// <summary>A mob further than this from its spawn stops acquiring new targets.</summary>
    public const double HomeAcquireLimit = 3000;

    /// <summary>Body size of a mob (MobInfo AbsoluteSize * 50 / 1000).</summary>
    public static double MobBody(MobInfoRow mob) => mob.AbsoluteSize * 50.0 / 1000.0;
    static bool IsBow(WeaponClass w) => w is WeaponClass.Bow or WeaponClass.Crossbow;
    /// <summary>The client's attackable distance to a mob of this body (not a gather object).</summary>
    public static double Attackable(double mobBody, WeaponClass weapon) => mobBody + (IsBow(weapon) ? BowExtra : 0);
    /// <summary>The client's attackable distance to an entity in view.</summary>
    public static double Attackable(AgentContext ctx, int slot)
    {
        var w = ctx.World; var weapon = Weapon(ctx);
        if ((w.Entities.Kind(slot) & EntityKind.Mob) == 0) return IsBow(weapon) ? BowExtra : ClientSlack;   // a character
        if (ctx.Data.Mob(w.Entities.MobId(slot)) is { } mi && GatherTypes.Contains(mi.Type)) return GatherReach;
        return Attackable(w.Entities.BodyOf(slot), weapon);
    }
    /// <summary>Auto-attack keep reach (a running swing stream): the client's attackable + 50.</summary>
    public static double BashReach(double targetBody, WeaponClass weapon) => Attackable(targetBody, weapon) + ClientSlack;
    /// <summary>Auto-attack start reach: the client's attackable + 50 - 10.</summary>
    public static double BashStartReach(double targetBody, WeaponClass weapon) => Attackable(targetBody, weapon) + SwingStartSlack;
    /// <summary>The client's skillable distance: Range + the mob's body (mob), Range + 50 (character), Range (no target).</summary>
    public static double Skillable(ActiveSkillRow? skill, double mobBody, bool targetIsMob, bool hasTarget)
        => skill is null ? ClientSlack : !hasTarget ? skill.Range : targetIsMob ? mobBody + skill.Range : skill.Range + ClientSlack;
    /// <summary>The client's skillable distance to an entity in view.</summary>
    public static double Skillable(AgentContext ctx, ActiveSkillRow? skill, int slot)
        => slot < 0 ? Skillable(skill, 0, false, false)
            : Skillable(skill, ctx.World.Entities.BodyOf(slot), (ctx.World.Entities.Kind(slot) & EntityKind.Mob) != 0, true);
    /// <summary>The distance a skill is cast within on a mob of this body: skillable + 50 (the client casts while that
    /// exceeds the distance).</summary>
    public static double SkillReach(ActiveSkillRow skill, double mobBody) => Skillable(skill, mobBody, true, true) + ClientSlack;
    /// <summary>A move destination is accepted when any tile of its 3x3 block is walkable.</summary>
    public static bool MoveTargetAccepted(BlockLayer layer, double x, double y)
    {
        var (tx, ty) = BlockLayer.ToTile(x, y);
        for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) if (!layer.Blocked(tx + dx, ty + dy)) return true;
        return false;
    }
    /// <summary>A straight segment is refused only at two consecutive 10 u samples that are blocked with all four 10 u
    /// neighbours blocked.</summary>
    public static bool MoveSegmentAccepted(BlockLayer layer, double fx, double fy, double tx, double ty)
    {
        var d = Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
        var n = Math.Max(1, (int)(d / 10));
        var prevBad = false;
        for (var i = 0; i <= n; i++)
        {
            double x = fx + (tx - fx) * i / n, y = fy + (ty - fy) * i / n;
            var bad = !layer.WalkableWorld(x, y) && !layer.WalkableWorld(x + 10, y) && !layer.WalkableWorld(x - 10, y)
                      && !layer.WalkableWorld(x, y + 10) && !layer.WalkableWorld(x, y - 10);
            if (bad && prevBad) return false;
            prevBad = bad;
        }
        return true;
    }
    /// <summary>Line of fire through the attack-block layer (always clear on maps without one).</summary>
    public static bool LineOfFire(BlockLayer? layer, double fx, double fy, double tx, double ty)
    {
        if (layer is null || !layer.HasAttackLayer) return true;
        var d = Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
        var n = Math.Max(1, (int)(d / 6.25));
        for (var i = 0; i <= n; i++)
        {
            var (x, y) = BlockLayer.ToTile(fx + (tx - fx) * i / n, fy + (ty - fy) * i / n);
            if (layer.AttackBlocked(x, y)) return false;
        }
        return true;
    }
    /// <summary>Distance left before a chasing mob gives up (chase-distance estimate from its last-hit point).</summary>
    public static double LeashLeft(double mobX, double mobY, double lastHitX, double lastHitY, double chaseDistance)
        => chaseDistance < 0 ? -1 : Math.Max(0, chaseDistance - Math.Sqrt((mobX - lastHitX) * (mobX - lastHitX) + (mobY - lastHitY) * (mobY - lastHitY)));

    /// <summary>Weapon class of our equipped weapon (ItemInfo WeaponType of equip slot 12 = right hand by the
    /// 2016 equipment order; bow types read from the item name space are not hardcoded: unknown = melee).</summary>
    public static WeaponClass Weapon(AgentContext ctx)
    {
        for (var slot = 0; slot < 64; slot++)
        {
            var item = ctx.World.Bag.Equipped(slot);
            if (item < 0 || ctx.Data.Item(item) is not { } it || it.WeaponType == 0) continue;
            if (it.InxName.Contains("CrossBow", StringComparison.OrdinalIgnoreCase)) return WeaponClass.Crossbow;
            if (it.InxName.Contains("Bow", StringComparison.OrdinalIgnoreCase)) return WeaponClass.Bow;
            return WeaponClass.Melee;
        }
        return WeaponClass.Melee;
    }

    /// <summary>Ms until a mob that spawned in our view takes skills (0 = now): observed, skills at a mob that has just
    /// spawned are refused 0x0FCD ("Skill has not been finished normally.") for about 2 s - learned per mob from the
    /// refused and accepted casts (the value over all mobs for one never fought; nothing known = try).</summary>
    public static long SpawnGuardLeftMs(AgentContext ctx, int slot)
    {
        var at = ctx.World.Entities.SpawnedAt(slot);
        if (at < 0) return 0;
        var g = ctx.Knowledge.Mob(ctx.World.Entities.MobId(slot), MobFact.SpawnRefused);
        if (g.Samples == 0) g = ctx.Knowledge.Mob(-1, MobFact.SpawnRefused);
        if (g.Samples == 0 || g.Value <= 0) return 0;
        return Math.Max(0, (long)Math.Ceiling(g.Value) - (ctx.Now - at));
    }

    /// <summary>Ms until our casts are taken after our map entry (0 = now): observed, casts right after a map entry are
    /// refused 0x0FC1 ("Cannot use the skill right after logging in."); learned from the refused ones (nothing known = try).</summary>
    public static long LoginGuardLeftMs(AgentContext ctx)
    {
        var at = ctx.World.Self.SpawnedAt;
        if (at < 0) return 0;
        var g = ctx.Knowledge.Mob(-1, MobFact.LoginRefused);
        if (g.Samples == 0 || g.Value <= 0) return 0;
        return Math.Max(0, (long)Math.Ceiling(g.Value) - (ctx.Now - at));
    }

    /// <summary>The generic usability check behind Lua `canUse` (API_SPEC 2.5.1). Objective only: no policy.</summary>
    public static CanUseReason CanUse(AgentContext ctx, int skillId, int targetHandle)
    {
        var w = ctx.World; var self = w.Self;
        var sk = ctx.Data.Skill(skillId);
        if (sk is null || !w.Skills.Has(skillId)) return CanUseReason.NotLearned;
        if (self.Dead) return CanUseReason.Dead;
        if (w.Skills.ReadyInMs(skillId, sk.DelayGroup, ctx.Now) > 0) return CanUseReason.Cooldown;
        if (sk.Sp > self.Sp) return CanUseReason.Sp;
        if (sk.Hp > 0 && sk.Hp >= self.Hp) return CanUseReason.Hp;
        if (self.Mounted) return CanUseReason.Mounted;
        if (ctx.Combat.Casting(ctx.Now)) return CanUseReason.Casting;
        if (LoginGuardLeftMs(ctx) > 0) return CanUseReason.JustEntered;
        if (targetHandle == self.Handle) return CanUseReason.Ok;
        var s = w.Entities.SlotOf(targetHandle);
        if (s < 0 || w.Entities.IsDead(s)) return CanUseReason.Target;
        // neither a mob nor a character: an NPC, or a scripted object in its NPC phase (the job change's shadow copy of
        // us turns npc between its fights; every cast at it then drew 0x0FCD, 164 in 10 s - NextA 17:34)
        if ((w.Entities.Kind(s) & (EntityKind.Mob | EntityKind.Player)) == 0) return CanUseReason.Target;
        if (SpawnGuardLeftMs(ctx, s) > 0) return CanUseReason.Spawning;
        var (tx, ty) = w.Entities.Pos(s, ctx.Now);
        var d = MoveController.Dist(self.Pos.X, self.Pos.Y, tx, ty);
        if (d >= Skillable(ctx, sk, s) + ClientSlack) return CanUseReason.Range;
        if (!LineOfFire(ctx.Nav.Layer(w.Map.MapName), self.Pos.X, self.Pos.Y, tx, ty)) return CanUseReason.LineOfFire;
        return CanUseReason.Ok;
    }
}

/// <summary>Learners turn observations into knowledge-store samples (server-only facts, client-legal).</summary>
public interface ILearner
{
    /// <summary>Observe one inbound event with the world as context.</summary>
    void Observe(in InboundEvent evt, AgentContext ctx);
}

/// <summary>Detect radius: when a mob that never touched us starts running at us, the distance is a lower bound of its
/// detect radius.</summary>
public sealed class AggroLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent e, AgentContext ctx)
    {
        if (e.Kind != InboundKind.EntityMoved || e.Handle2 != 1) return;
        var w = ctx.World; var s = w.Entities.SlotOf(e.Handle);
        if (s < 0 || w.Entities.MobId(s) < 0 || w.Entities.LastHitUsAt(s) >= 0 || w.Entities.DamageByUs[s] > 0) return;
        var (px, py) = w.Self.Pos;
        double d0 = MoveController.Dist(e.A, e.B, px, py), d1 = MoveController.Dist(e.X, e.Y, px, py);
        if (d1 < d0 - 50 && d1 < 200) ctx.Knowledge.ObserveAtLeast(w.Entities.MobId(s), MobFact.DetectRadius, d0);
    }
}

/// <summary>Chase distance: not learned yet (needs the turn-back moment; open item).</summary>
public sealed class LeashLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) { }
}

/// <summary>Mob reach and swing interval are sampled in WorldState's damage handling (same events, no second pass).</summary>
public sealed class MobAttackLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) { }
}

/// <summary>Family assist radius: not learned yet (open item).</summary>
public sealed class AssistLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) { }
}

/// <summary>Weapon range / armour fit: not learned yet (open item; the damage formula port is pending).</summary>
public sealed class DamageFitter : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) { }
}
