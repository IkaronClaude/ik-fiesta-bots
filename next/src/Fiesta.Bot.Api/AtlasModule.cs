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
        // every spawn area of every map with a density count each: answers are kept per (here, band) for 30 s
        if (_grindKey == (W.Map.MapId, minLv, maxLv, Ctx.RefusedMaps.Count) && Ctx.Now - _grindAt < 30_000) return _grind;
        _grindKey = (W.Map.MapId, minLv, maxLv, Ctx.RefusedMaps.Count); _grindAt = Ctx.Now;
        return _grind = GrindSpotScan(minLv, maxLv);
    }
    private (int, int, int, int) _grindKey = (-1, -1, -1, -1);   // (here, band, maps avoided)
    private long _grindAt = long.MinValue / 2;
    private (string Map, double X, double Y, double R, int MobId) _grind;

    private (string Map, double X, double Y, double R, int MobId) GrindSpotScan(int minLv, int maxLv)
    {
        SpawnArea? best = null; var bestScore = double.NegativeInfinity;
        foreach (var (a, n) in Candidates(minLv, maxLv))
        {
            if (Ctx.Refused(a.MapId)) continue;
            var route = a.MapId == W.Map.MapId ? 0 : Ctx.Atlas.MapDistance(W.Map.MapId, a.MapId);
            if (route < 0) continue;
            var walk = a.MapId == W.Map.MapId ? MoveController.Dist(W.Self.Pos.X, W.Self.Pos.Y, a.X, a.Y) : route;
            var score = n - walk / 4000.0;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return best is { } b ? (MapName(b.MapId), b.X, b.Y, Math.Max(150, Math.Min(b.HalfW, b.HalfH)), b.MobId) : ("", -1, -1, -1, -1);
    }

    // the spawn areas of a level band with their density (matching areas within 1500 on the same map): client data
    // only, shared by every bot. The neighbourhoods (which areas lie within 1500 of each hostile area) are computed once
    // per host - Prepare runs it at host start, off the tick - and a band then only counts its own neighbours.
    private sealed record Hood(SpawnArea[] Areas, int[] Level, int[][] Near);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, Hood> _hoods = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(object, int, int), (SpawnArea A, int N)[]> _bands = new();

    /// <summary>Compute the spawn-area neighbourhoods of an atlas (seconds of work; call it off the tick at host start).</summary>
    public static void Prepare(WorldAtlas atlas, IClientData data)
    {
        HoodOf(atlas, data);
        atlas.PrepareMapCosts();
        atlas.PrepareWalkLevels(id => data.Mob(id) is { IsNpc: false, IsPlayerSide: false, MaxHp: > 0 } mi ? mi.Level : -1);
    }

    private static Hood HoodOf(WorldAtlas atlas, IClientData data) => _hoods.GetOrAdd(atlas, _ =>
    {
        var areas = new List<SpawnArea>(); var lv = new List<int>(); var near = new List<int[]>();
        foreach (var map in atlas.SpawnMaps)
        {
            if (data.Map(map) is not { IsInstance: false }) continue;
            var start = areas.Count;
            foreach (var x in atlas.SpawnsOn(map))
                if (data.Mob(x.MobId) is { IsNpc: false, IsPlayerSide: false, MaxHp: > 0 } m) { areas.Add(x); lv.Add(m.Level); }
            for (var i = start; i < areas.Count; i++)
            {
                var n = new List<int>();
                for (var j = start; j < areas.Count; j++)
                    if (MoveController.Dist(areas[j].X, areas[j].Y, areas[i].X, areas[i].Y) < 1500) n.Add(j);
                near.Add(n.ToArray());
            }
        }
        return new Hood(areas.ToArray(), lv.ToArray(), near.ToArray());
    });

    private (SpawnArea A, int N)[] Candidates(int minLv, int maxLv) => _bands.GetOrAdd((Ctx.Atlas, minLv, maxLv), _ =>
    {
        var h = HoodOf(Ctx.Atlas, Ctx.Data);
        var list = new List<(SpawnArea, int)>();
        for (var i = 0; i < h.Areas.Length; i++)
        {
            if (h.Level[i] < minLv || h.Level[i] > maxLv) continue;
            var n = 0;
            foreach (var j in h.Near[i]) if (h.Level[j] >= minLv && h.Level[j] <= maxLv) n++;
            list.Add((h.Areas[i], n));
        }
        return list.ToArray();
    });

    /// <summary>Avoid a map for this bot for a while (a death trap, a refusing gate): routes and grind spots skip it.</summary>
    [LuaFn] public bool Avoid(string map, int minutes)
    {
        if (Ctx.Data.Map(map) is not { } m) return false;
        Ctx.RefusedMaps[m.Id] = Ctx.Now + minutes * 60_000L;
        return true;
    }

    /// <summary>Is a map avoided for this bot now?</summary>
    [LuaFn] public bool Avoided(string map) => Ctx.Data.Map(map) is { } m && Ctx.Refused(m.Id);

    /// <summary>The nearest NPC (by route) whose learned role matches the mask (npc.SHOP / SOUL_STONE / SKILL / SMITH ...):
    /// map, x, y, npc id ("" none known).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached, Alloc = Alloc.LuaString)]
    public (string Map, double X, double Y, int NpcId) ServiceSpot(int roleMask)
    {
        // asked by the upkeep's conditions every tick: the answer is kept per (role, here) for 10 s
        var key = (roleMask, W.Map.MapId);
        if (_svc.TryGetValue(key, out var c) && Ctx.Now - c.At < 10_000) return c.Spot;
        var spot = ServiceSpotScan(roleMask);
        _svc[key] = (spot, Ctx.Now);
        return spot;
    }
    private readonly Dictionary<(int, int), ((string Map, double X, double Y, int NpcId) Spot, long At)> _svc = new();

    private (string Map, double X, double Y, int NpcId) ServiceSpotScan(int roleMask)
    {
        if (Ctx.Knowledge is not Fiesta.Bot.Services.KnowledgeStore ks) return ("", -1, -1, -1);
        (int Map, double X, double Y, int Id)? best = null; var bc = double.PositiveInfinity;
        foreach (var (id, mask, map, x, y) in ks.KnownNpcs())
        {
            if ((mask & roleMask) == 0 || Ctx.Refused(map)) continue;
            var c = CostTo(map, x, y);
            if (c < bc) { bc = c; best = (map, x, y, id); }
        }
        return best is { } b ? (MapName(b.Map), b.X, b.Y, b.Id) : ("", -1, -1, -1);
    }

    /// <summary>Every known NPC with a role in the mask, nearest by route first (npc ids; maps refused to us left out).
    /// The spot of one: <c>atlas.serviceSpotOf(id)</c>.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached, Alloc = Alloc.Iterator)]
    public LuaIter ServiceNpcs(int roleMask)
    {
        var s = Ctx.Scratch(256); var n = 0;
        if (Ctx.Knowledge is Fiesta.Bot.Services.KnowledgeStore ks)
        {
            var list = new List<(double C, int Id)>();
            foreach (var (id, mask, map, x, y) in ks.KnownNpcs())
                if ((mask & roleMask) != 0 && !Ctx.Refused(map)) list.Add((CostTo(map, x, y), id));
            list.Sort((a, b) => a.C.CompareTo(b.C));
            foreach (var (_, id) in list) if (n < s.Length) s[n++] = id;
        }
        return ApiScope.Iter(s[..n]);
    }

    /// <summary>Where a known service NPC stands (its learned spot): map, x, y ("" unknown).</summary>
    [LuaFn(Alloc = Alloc.LuaString)]
    public (string Map, double X, double Y) ServiceSpotOf(int npcId)
    {
        if (Ctx.Knowledge is Fiesta.Bot.Services.KnowledgeStore ks)
            foreach (var (id, _, map, x, y) in ks.KnownNpcs()) if (id == npcId && !Ctx.Refused(map)) return (MapName(map), x, y);
        return ("", -1, -1);
    }

    /// <summary>Learned role mask of an NPC id (0 = not probed or no shop).</summary>
    [LuaFn] public int NpcRole(int npcId) => Ctx.Knowledge.NpcRole(npcId);

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
    public int TravelTo(string map, double x = -1, double y = -1, double within = 150, int maxMobLevel = -1)
        => Ctx.Procedures.Start(new TravelProcedure(map, x < 0 ? double.NaN : x, x < 0 ? double.NaN : y, within, maxMobLevel < 0 ? int.MaxValue : maxMobLevel),
            ActionKind.Travel, CallingLayer, Ctx, 900000).Value;

    /// <summary>The level of the easiest hostile mob spawning on a map (-1 none / unknown map).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public int EasiestLevel(string map) => Ctx.Data.Map(map) is { } m ? Ctx.Atlas.EasiestMobLevel(m.Id, Hostile) : -1;

    /// <summary>Route cost to a map avoiding maps whose easiest hostile mob is above maxMobLevel (-1 no such route).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public double SafeCost(string map, int maxMobLevel)
    {
        if (Ctx.Data.Map(map) is not { } m) return -1;
        if (Ctx.Refused(m.Id)) return -1;
        // a route search with the danger test per walk is not cheap, and the planner asks for many maps per decision: ONE
        // search from here answers every map, kept per (here, level) for 30 s
        if (_safeFrom != W.Map.MapId || _safeLevel != maxMobLevel || Ctx.Now - _safeAt >= 30_000)
        {
            var (px, py) = W.Self.Pos;
            Ctx.Atlas.RouteCostsBelow(W.Map.MapId, px, py, _safe, x => Ctx.Refused(x), maxMobLevel, _hostile ??= Hostile);
            _safeFrom = W.Map.MapId; _safeLevel = maxMobLevel; _safeAt = Ctx.Now;
        }
        return _safe.TryGetValue(m.Id, out var c) ? c : -1;
    }
    private readonly Dictionary<int, double> _safe = new();
    private int _safeFrom = -1, _safeLevel = -1;
    private long _safeAt = long.MinValue / 2;

    private Func<int, int>? _hostile;
    private int Hostile(int id) => Ctx.Data.Mob(id) is { IsNpc: false, IsPlayerSide: false, MaxHp: > 0 } mi ? mi.Level : -1;
}
