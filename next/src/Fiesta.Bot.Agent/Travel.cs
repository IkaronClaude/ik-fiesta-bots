using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Agent;

/// <summary>Travel to a map (and a spot on it) across any number of gates: route over the world atlas, walk to each
/// gate exit, hop through the gate whose destination is the next map, re-plan after every map change. When the atlas
/// has no route from here (a map the client graph does not link), the map's unexplored gate exits are visited so the
/// gates seen there are learned, then it plans again.</summary>
public sealed class TravelProcedure(string targetMap, double x = double.NaN, double y = double.NaN, double within = 150, int maxMobLevel = int.MaxValue) : ProcedureBase
{
    /// <summary>Target map name.</summary>
    public string TargetMap { get; } = targetMap;
    /// <inheritdoc/>
    public override ProcedureKind Kind => ProcedureKind.Travel;
    private ActionId _move = ActionId.None;
    private GateHopProcedure? _hop;
    private RouteHop _next;
    private int _hopFails, _replans;
    // destinations whose gate refused us (no menu: the server refuses silently, e.g. a level requirement); routed around
    private readonly HashSet<int> _refusedInto = new();
    private int _menuRefusals;
    private readonly HashSet<(int, int)> _explored = new();
    private const double GateSight = 700;

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <inheritdoc/>
    public override void Advance(AgentContext ctx)
    {
        switch (Step)
        {
            case "start": Plan(ctx); return;
            case "walkFinal": case "walkGate": case "explore": AdvanceWalk(ctx); return;
            case "hop":
                _hop!.Advance(ctx);
                Settle(ctx);
                return;
        }
    }

    private void Plan(AgentContext ctx)
    {
        var w = ctx.World;
        if (!w.Self.InWorld) { Go("start", ctx, 0); return; }
        var target = ctx.Data.Map(TargetMap);
        if (target is null) { Fail($"unknown map {TargetMap}"); return; }
        var (px, py) = w.Self.Pos;
        if (w.Map.MapId == target.Id)
        {
            if (double.IsNaN(x) || Dist(px, py, x, y) <= within) { Done(); return; }
            _move = ctx.Move.Order(new MoveOrder(x, y, true, within, ActionId.None), ctx, 0);
            Go("walkFinal", ctx, 600000);
            return;
        }
        if (++_replans > 40) { Fail("too many re-plans"); return; }
        // maps whose easiest hostile mob is above maxMobLevel are routed around (crossing them is how bots died)
        Func<int, bool>? avoid = maxMobLevel == int.MaxValue && _refusedInto.Count == 0 ? null
            : m => _refusedInto.Contains(m) || (maxMobLevel != int.MaxValue
                && ctx.Atlas.EasiestMobLevel(m, id => ctx.Data.Mob(id) is { IsNpc: false, IsPlayerSide: false, MaxHp: > 0 } mi ? mi.Level : -1) > maxMobLevel);
        if (_refusedInto.Contains(target.Id)) { Fail($"the gate into {TargetMap} refuses us"); return; }
        var route = ctx.Atlas.Route(w.Map.MapId, px, py, target.Id, x, y, avoid);
        if (route is null || route.Hops.Count == 0)
        {
            // no known link from this map: walk to the nearest gate exit we have not explored, to learn where it leads
            (double X, double Y)? best = null; var bd = double.MaxValue;
            foreach (var g in ctx.Atlas.GatePoints(w.Map.MapId))
            {
                var key = ((int)g.X / 100, (int)g.Y / 100);
                if (_explored.Contains(key) || ctx.Atlas.GateKnown(w.Map.MapId, g.X, g.Y)) continue;
                var d = Dist(px, py, g.X, g.Y);
                if (d < bd) { bd = d; best = g; }
            }
            if (best is not { } e) { Fail($"no route from {w.Map.MapName} to {TargetMap}"); return; }
            _explored.Add(((int)e.X / 100, (int)e.Y / 100));
            ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"travel: no known route {w.Map.MapName} -> {TargetMap}; exploring the gate exit at ({e.X:0},{e.Y:0})");
            _move = ctx.Move.Order(new MoveOrder(e.X, e.Y, true, 200, ActionId.None), ctx, 0);
            Go("explore", ctx, 600000);
            return;
        }
        _next = route.Hops[0];
        ctx.Log.Log(LogCategory.Nav, LogLevel.Info,
            $"travel: {w.Map.MapName} -> {TargetMap} in {route.Hops.Count} hop(s), next gate ({_next.X:0},{_next.Y:0}) to {ctx.Data.Map(_next.To)?.MapName}");
        if (TryHop(ctx)) return;
        _move = ctx.Move.Order(new MoveOrder(_next.X, _next.Y, true, 120, ActionId.None), ctx, 0);
        Go("walkGate", ctx, 600000);
    }

    // a gate to the next map in sight: hop through it
    private bool TryHop(AgentContext ctx)
    {
        var name = ctx.Data.Map(_next.To)?.MapName;
        if (name is null) return false;
        var e = ctx.World.Entities; var (px, py) = ctx.World.Self.Pos;
        var best = -1; var bd = GateSight;
        for (var i = 0; i < e.Count; i++)
        {
            var s = e.LiveSlot(i);
            if ((e.Kind(s) & EntityKind.Gate) == 0 || !string.Equals(e.NameOf(s), name, StringComparison.OrdinalIgnoreCase)) continue;
            var (gx, gy) = e.Pos(s, ctx.Now);
            var d = Dist(px, py, gx, gy);
            if (d < bd) { bd = d; best = e.HandleOf(s); }
        }
        if (best < 0) return false;
        if (_move.Value > 0 && ctx.Actions.Get(_move).Pending) ctx.Move.Stop(ctx, resolveCancelled: true);
        _hop = new GateHopProcedure(best, name) { Action = Action };
        Go("hop", ctx, 120000);
        _hop.Advance(ctx);
        return true;
    }

    private void AdvanceWalk(AgentContext ctx)
    {
        if (Step == "walkGate" && TryHop(ctx)) return;
        var r = ctx.Actions.Get(_move);
        if (r.Id != 0 && r.Pending && !Expired(ctx)) return;
        if (Step == "walkFinal")
        {
            var (px, py) = ctx.World.Self.Pos;
            if (r.State == ActionState.Acked || Dist(px, py, x, y) <= within * 1.5) Done();
            else Fail($"could not reach ({x:0},{y:0}) on {TargetMap}: {r.State}");
            return;
        }
        if (Step == "walkGate")
        {
            if (TryHop(ctx)) return;
            ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"travel: at the gate exit ({_next.X:0},{_next.Y:0}) but no gate to {ctx.Data.Map(_next.To)?.MapName} in view");
            if (++_hopFails > 3) { Fail("the gate to the next map never came into view"); return; }
        }
        Go("start", ctx, 0);   // explore arrival or a retry: plan again
        Plan(ctx);
    }

    private void Settle(AgentContext ctx)
    {
        if (_hop is null || _hop.Status == ProcedureStatus.Running) return;
        if (_hop.Status == ProcedureStatus.Succeeded) { _hop = null; _hopFails = 0; Go("start", ctx, 0); return; }
        ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"travel: gate hop failed ({_hop.FailReason})");
        if (_hop.FailReason?.Contains("awaitMenu") == true && ++_menuRefusals >= 2)
        {
            _refusedInto.Add(_next.To); _menuRefusals = 0;
            ctx.Log.Log(LogCategory.Nav, LogLevel.Note, $"travel: the gate to {ctx.Data.Map(_next.To)?.MapName} answers no menu twice: routing around it");
        }
        _hop = null;
        if (++_hopFails > 3) { Fail("gate hops keep failing"); return; }
        Go("start", ctx, 0);
    }

    /// <inheritdoc/>
    public override void Apply(in InboundEvent evt, AgentContext ctx)
    {
        if (_hop is not null) { _hop.Apply(evt, ctx); Settle(ctx); }
        else if (evt.Kind == InboundKind.MapLink && Step is "walkGate" or "explore") Go("start", ctx, 0);   // walked into a gate by chance
    }
}

/// <summary>Teaches the world atlas the gates seen in the world (their destination is on the gate) and where hops land.</summary>
public sealed class AtlasLearner : ILearner
{
    private int _mapBefore = -1;

    /// <inheritdoc/>
    public void Observe(in InboundEvent e, AgentContext ctx)
    {
        switch (e.Kind)
        {
            case InboundKind.EntityAppeared when (((EntityKind)(int)e.A) & EntityKind.Gate) != 0 && !string.IsNullOrEmpty(e.Text):
                if (ctx.Data.Map(e.Text!) is { } dest && ctx.Atlas.LearnGate(ctx.World.Map.MapId, e.X, e.Y, dest.Id))
                    ctx.Log.Log(LogCategory.Nav, LogLevel.Info, $"atlas: learned gate {ctx.World.Map.MapName} ({e.X},{e.Y}) -> {dest.MapName}");
                break;
            case InboundKind.MapLink:
                if (_mapBefore >= 0 && e.A != _mapBefore) ctx.Atlas.LearnArrival(_mapBefore, (int)e.A, e.X, e.Y);
                break;
        }
        if (e.Kind is InboundKind.MapLoginAck or InboundKind.MapLink) _mapBefore = e.Kind == InboundKind.MapLink ? (int)e.A : ctx.World.Map.MapId;
    }
}
