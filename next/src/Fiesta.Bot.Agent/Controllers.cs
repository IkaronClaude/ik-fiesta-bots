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
                    // for this map (the client's grid differs from the game's in places - Rou in 23,386 cells), so the
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
        if (ctx.Move.Moving && !sk.IsMovingSkill) { ctx.Move.Stop(ctx); }
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
        if (id.Value > 0) { _castSkill = skillId; _castUntil = ctx.Now + sk.CastTimeMs + 300; }
        return id;
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
        if (id.Value > 0) { _castSkill = skillId; _castUntil = ctx.Now + sk.CastTimeMs + 300; }
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
            // 0x0FC4 "Casting another skill now." / 0x0FC6 "Incorrect Skill." (the client's own error texts): every cast
            // waits 600 ms, as the old drivers learned - re-pressing at tick cadence drew runs of refusals (NextM: 138
            // 0x0FC4 refusals in an hour, 01:00-01:57)
            case InboundKind.CastFail when e.A is 0x0FC4 or 0x0FC6:
                _castUntil = ctx.Now + 600;
                break;
            case InboundKind.CastFail:
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
}

/// <summary>Weapon class for reach.</summary>
public enum WeaponClass : byte { Melee, Bow, Crossbow, Staff, Wand, None }

/// <summary>The game's rules in one tested place (REVIEW section 5; each stated as measured behaviour).</summary>
public static class GameRules
{
    /// <summary>A player's body size.</summary>
    public const double PlayerBody = 50;
    /// <summary>Melee weapon reach.</summary>
    public const double MeleeReach = 120;
    /// <summary>Bow/crossbow reach.</summary>
    public const double BowReach = 600;
    /// <summary>Maximum distance between our move's `from` and the game's position.</summary>
    public const double MoveFromTolerance = 200;
    /// <summary>A mob further than this from its spawn stops acquiring new targets.</summary>
    public const double HomeAcquireLimit = 3000;

    /// <summary>Body size of a mob (MobInfo AbsoluteSize * 50 / 1000).</summary>
    public static double MobBody(MobInfoRow mob) => mob.AbsoluteSize * 50.0 / 1000.0;
    /// <summary>Auto-attack keep reach: a running swing stream continues while distance &lt;= (body + weapon reach) * 1.25.</summary>
    public static double BashReach(double targetBody, WeaponClass weapon)
        => (targetBody + (weapon is WeaponClass.Bow or WeaponClass.Crossbow ? BowReach : MeleeReach)) * 1.25;
    /// <summary>Auto-attack start reach: (body + weapon reach) without the keep tolerance. Measured live: BASHSTART at
    /// 161-162 u from a Slime (keep reach 162.5) got SWING_START then CEASE_FIRE with no damage, 23 times in 10 s,
    /// while starts well inside the bare reach landed.</summary>
    public static double BashStartReach(double targetBody, WeaponClass weapon)
        => targetBody + (weapon is WeaponClass.Bow or WeaponClass.Crossbow ? BowReach : MeleeReach);
    /// <summary>Skill reach: r = Range + max(50, Range/10) + body; accepted up to min(r * 1.25, r + 60).</summary>
    public static double SkillReach(ActiveSkillRow skill, double targetBody)
    {
        var r = skill.Range + Math.Max(50, skill.Range * 0.10) + targetBody;
        return Math.Min(r * 1.25, r + 60);
    }
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
        if (targetHandle == self.Handle) return CanUseReason.Ok;
        var s = w.Entities.SlotOf(targetHandle);
        if (s < 0 || w.Entities.IsDead(s)) return CanUseReason.Target;
        var (tx, ty) = w.Entities.Pos(s, ctx.Now);
        var d = MoveController.Dist(self.Pos.X, self.Pos.Y, tx, ty);
        if (d > SkillReach(sk, w.Entities.BodyOf(s))) return CanUseReason.Range;
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
