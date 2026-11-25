using Fiesta.Bot.Agent;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `atlas`: where things are in the world, from the client's own tables (NPC spots, mob spawn
/// areas, the gate graph) plus the gates bots have learned. Map arguments and results are map names.</summary>
[LuaModule("atlas")]
public sealed partial class AtlasModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;

    // route cost from here to a spot (same map: straight line; other map: the gate route; +inf unreachable)
    private double CostTo(int map, double x, double y)
    {
        var (px, py) = W.Self.Pos;
        if (map == W.Map.MapId) return MoveController.Dist(px, py, x, y);
        var d = Ctx.Atlas.MapDistance(W.Map.MapId, map);
        return d < 0 ? double.PositiveInfinity : d + 1;
    }

    private string MapName(int id) => Ctx.Data.Map(id)?.MapName ?? "";

    /// <summary>Where an NPC (MobInfo id) stands, the reachable spot nearest by route ("" when unknown or unreachable).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached, Alloc = Alloc.LuaString)]
    public (string Map, double X, double Y) NpcSpot(int npcId)
    {
        MapSpot? best = null; var bc = double.PositiveInfinity;
        foreach (var s in Ctx.Atlas.NpcSpots(npcId)) { var c = CostTo(s.MapId, s.X, s.Y); if (c < bc) { bc = c; best = s; } }
        return best is { } b ? (MapName(b.MapId), b.X, b.Y) : ("", -1, -1);
    }

    /// <summary>Where a mob spawns, the reachable area nearest by route: map, centre, radius ("" when unknown).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached, Alloc = Alloc.LuaString)]
    public (string Map, double X, double Y, double R) MobSpot(int mobId)
    {
        SpawnArea? best = null; var bc = double.PositiveInfinity;
        foreach (var a in Ctx.Atlas.SpawnAreas(mobId)) { var c = CostTo(a.MapId, a.X, a.Y); if (c < bc) { bc = c; best = a; } }
        return best is { } b ? (MapName(b.MapId), b.X, b.Y, Math.Max(100, Math.Min(b.HalfW, b.HalfH))) : ("", -1, -1, -1);
    }

    /// <summary>A grind spot for a level band: the spawn area of hostile mobs with level in [minLv, maxLv] that has the
    /// most such areas on its map nearby, weighed against the route to it. Returns map, centre, radius, mob id.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached, Alloc = Alloc.LuaString)]
    public (string Map, double X, double Y, double R, int MobId) GrindSpot(int minLv, int maxLv)
    {
        SpawnArea? best = null; var bestScore = double.NegativeInfinity;
        foreach (var map in Ctx.Atlas.SpawnMaps)
        {
            var info = Ctx.Data.Map(map);
            if (info is null || info.IsInstance) continue;
            var route = map == W.Map.MapId ? 0 : Ctx.Atlas.MapDistance(W.Map.MapId, map);
            if (route < 0) continue;
            var areas = Ctx.Atlas.SpawnsOn(map);
            foreach (var a in areas)
            {
                if (Ctx.Data.Mob(a.MobId) is not { } m || m.IsNpc || m.IsPlayerSide || m.MaxHp <= 0 || m.Level < minLv || m.Level > maxLv) continue;
                // density: matching areas within 1500 of this one
                var n = 0;
                foreach (var o in areas)
                    if (Ctx.Data.Mob(o.MobId) is { } om && !om.IsNpc && om.MaxHp > 0 && om.Level >= minLv && om.Level <= maxLv
                        && MoveController.Dist(o.X, o.Y, a.X, a.Y) < 1500) n++;
                var walk = map == W.Map.MapId ? MoveController.Dist(W.Self.Pos.X, W.Self.Pos.Y, a.X, a.Y) : route;
                var score = n - walk / 4000.0;
                if (score > bestScore) { bestScore = score; best = a; }
            }
        }
        return best is { } b ? (MapName(b.MapId), b.X, b.Y, Math.Max(150, Math.Min(b.HalfW, b.HalfH)), b.MobId) : ("", -1, -1, -1, -1);
    }

    /// <summary>A mob's level from MobInfo (-1 unknown).</summary>
    [LuaFn] public int MobLevel(int mobId) => Ctx.Data.Mob(mobId)?.Level ?? -1;

    /// <summary>Gate hops from here to a map (-1 no known route, 0 on it).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public int Hops(string map)
    {
        if (Ctx.Data.Map(map) is not { } m) return -1;
        var (px, py) = W.Self.Pos;
        return Ctx.Atlas.Route(W.Map.MapId, px, py, m.Id) is { } r ? r.Hops.Count : -1;
    }

    /// <summary>Route cost (world units) from here to a map (-1 no known route).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public double Cost(string map) => Ctx.Data.Map(map) is { } m ? Ctx.Atlas.MapDistance(W.Map.MapId, m.Id) : -1;

    /// <summary>Travel to a map and optionally a spot on it (x &lt; 0 = anywhere on the map), across gates; an action.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Job)]
    public int TravelTo(string map, double x = -1, double y = -1, double within = 150)
        => Ctx.Procedures.Start(new TravelProcedure(map, x < 0 ? double.NaN : x, x < 0 ? double.NaN : y, within), ActionKind.Travel, CallingLayer, Ctx, 900000).Value;
}
