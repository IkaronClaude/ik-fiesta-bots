namespace Fiesta.Bot.Services;

/// <summary>A place on a map.</summary>
public readonly record struct MapSpot(int MapId, double X, double Y);

/// <summary>A mob spawn region (MobCoordinate): centre and half extents.</summary>
public readonly record struct SpawnArea(int MobId, int MapId, double X, double Y, double HalfW, double HalfH);

/// <summary>One map change of a route: leave <see cref="From"/> through the gate at (X, Y) into <see cref="To"/>.</summary>
public readonly record struct RouteHop(int From, double X, double Y, int To);

/// <summary>A route between maps: the hops in order and the walking cost (world units, straight segments).</summary>
public sealed record WorldRoute(IReadOnlyList<RouteHop> Hops, double Cost);

/// <summary>What the client itself knows about the world's layout, from its own tables: where NPCs stand and mobs
/// spawn (MobCoordinate), the gate exits of each map (MapWayPoint rows marked as gates) and the waypoint graph its
/// auto-path uses (MapLinkPoint; an edge between two maps is a gate). The client graph does not cover every map the
/// server runs (it links the new Roumen, not the old one), so gates seen in the world are learned into the same graph
/// (<see cref="LearnGate"/>). Thread-safe; shared by all bots.</summary>
public sealed class WorldAtlas
{
    /// <summary>An atlas with nothing in it (no client tables).</summary>
    public static readonly WorldAtlas Empty = new();

    private readonly Dictionary<int, List<MapSpot>> _npc = new();
    private readonly Dictionary<int, List<SpawnArea>> _spawn = new();
    private readonly Dictionary<int, List<SpawnArea>> _spawnByMap = new();
    private readonly List<(int Map, double X, double Y)> _nodes = new();
    private readonly List<List<(int To, double Cost, bool Gate)>> _adj = new();
    private readonly Dictionary<int, List<int>> _nodesByMap = new();
    private readonly Dictionary<int, List<(double X, double Y)>> _gatePoints = new();
    private readonly Dictionary<int, (double X, double Y)> _regen = new();
    private readonly List<(int Map, double X, double Y, int To)> _learned = new();
    private readonly Dictionary<(int, int), WorldRoute?> _routes = new();
    private readonly object _gate = new();

    /// <summary>Raised (outside the lock) when a gate was learned, to persist it.</summary>
    public event Action<int, double, double, int>? GateLearned;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _pvp = new();
    /// <summary>Raised when a map is learned to be a player-fight area (map id).</summary>
    public event Action<int>? PvpLearned;
    /// <summary>Is the map a player-fight area (told by its zone on entry; shared by every bot)?</summary>
    public bool IsPvp(int mapId) => _pvp.ContainsKey(mapId);
    /// <summary>Learn a player-fight map; true when new.</summary>
    public bool LearnPvp(int mapId)
    {
        if (ReferenceEquals(this, Empty)) return false;   // the shared placeholder never learns
        if (!_pvp.TryAdd(mapId, 0)) return false;
        PvpLearned?.Invoke(mapId);
        return true;
    }
    /// <summary>Waypoints read.</summary>
    public int Waypoints { get; }
    /// <summary>Map-to-map gate links from the client graph.</summary>
    public int Links { get; }

    private WorldAtlas() { }
    /// <summary>An atlas with no client tables (tests, a bot without data): learns gates and player-fight maps only.</summary>
    public static WorldAtlas CreateEmpty() => new();

    /// <summary>Load from a ressystem directory (missing tables leave that part empty). Regen points are the first
    /// guess of where a learned gate lands.</summary>
    public WorldAtlas(string ressystemDir, Func<string, int> mapIdByName, IEnumerable<(int Id, double X, double Y)>? regenPoints = null)
    {
        if (regenPoints is not null) foreach (var (id, x, y) in regenPoints) _regen[id] = (x, y);
        ShnTable? T(string n) { var p = Path.Combine(ressystemDir, n + ".shn"); return File.Exists(p) ? ShnTable.Load(p) : null; }
        if (T("MobCoordinate") is { } mc)
            for (var r = 0; r < mc.RowCount; r++)
            {
                var map = mapIdByName(mc.Str(r, "MapName") ?? "");
                if (map < 0) continue;
                var mob = (int)mc.Num(r, "Mob_ID");
                double x = mc.Num(r, "CenterX"), y = mc.Num(r, "CenterY"), w = mc.Num(r, "Width"), h = mc.Num(r, "Height");
                if (w == 0 && h == 0) Add(_npc, mob, new MapSpot(map, x, y));   // a point = a standing NPC
                else
                {
                    var a = new SpawnArea(mob, map, x, y, w / 2, h / 2);
                    Add(_spawn, mob, a); Add(_spawnByMap, map, a);
                }
            }
        var wp = T("MapWayPoint"); var links = T("MapLinkPoint");
        if (wp is null || links is null) return;
        Waypoints = wp.RowCount;
        for (var r = 0; r < wp.RowCount; r++)
            if (wp.Num(r, "MWP_Gate") == 2) Add(_gatePoints, (int)wp.Num(r, "MapID"), (wp.Num(r, 1), wp.Num(r, 2)));
        var nodeOf = new Dictionary<int, int>();
        int Node(int row)
        {
            if (nodeOf.TryGetValue(row, out var n)) return n;
            n = _nodes.Count;
            var map = (int)wp.Num(row, "MapID");
            _nodes.Add((map, wp.Num(row, 1), wp.Num(row, 2)));
            Add(_nodesByMap, map, n);
            nodeOf[row] = n;
            return n;
        }
        var cross = new List<(int A, int B)>();
        for (var r = 0; r < links.RowCount; r++)
        {
            int a = (int)links.Num(r, "MLP_FromID"), b = (int)links.Num(r, "MLP_ToID");
            if ((uint)a >= (uint)wp.RowCount || (uint)b >= (uint)wp.RowCount) continue;
            if (wp.Num(a, "MapID") == wp.Num(b, "MapID")) continue;
            cross.Add((Node(a), Node(b)));
        }
        Links = cross.Count;
        for (var i = 0; i < _nodes.Count; i++) _adj.Add(new());
        foreach (var (a, b) in cross) _adj[a].Add((b, 0, true));
        // inside a map every gate end reaches every other one (the walk itself is planned on the walkable grid)
        foreach (var list in _nodesByMap.Values)
            foreach (var i in list)
                foreach (var j in list)
                    if (i != j) _adj[i].Add((j, Dist(_nodes[i].X, _nodes[i].Y, _nodes[j].X, _nodes[j].Y), false));
    }

    private static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK k, TV v) where TK : notnull
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new();
        l.Add(v);
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>Where an NPC (MobInfo id) stands; several entries when it stands in more than one map.</summary>
    public IReadOnlyList<MapSpot> NpcSpots(int npcId) => _npc.TryGetValue(npcId, out var l) ? l : [];

    /// <summary>Where a mob spawns.</summary>
    public IReadOnlyList<SpawnArea> SpawnAreas(int mobId) => _spawn.TryGetValue(mobId, out var l) ? l : [];

    /// <summary>Spawn areas of a map.</summary>
    public IReadOnlyList<SpawnArea> SpawnsOn(int mapId) => _spawnByMap.TryGetValue(mapId, out var l) ? l : [];

    /// <summary>Maps that have spawn areas.</summary>
    public IEnumerable<int> SpawnMaps => _spawnByMap.Keys;

    /// <summary>Gate exits of a map as the client's waypoint table marks them (destinations not given there).</summary>
    public IReadOnlyList<(double X, double Y)> GatePoints(int mapId) => _gatePoints.TryGetValue(mapId, out var l) ? l : [];

    /// <summary>Is a gate exit of this map near (x, y) linked (from the client graph or learned)?</summary>
    public bool GateKnown(int mapId, double x, double y, double within = 300)
    {
        lock (_gate)
        {
            if (!_nodesByMap.TryGetValue(mapId, out var list)) return false;
            foreach (var n in list)
                if (Dist(_nodes[n].X, _nodes[n].Y, x, y) < within && _adj[n].Exists(e => e.Gate)) return true;
            return false;
        }
    }

    private int AddNode(int map, double x, double y)
    {
        var n = _nodes.Count;
        _nodes.Add((map, x, y)); _adj.Add(new());
        if (_nodesByMap.TryGetValue(map, out var list))
            foreach (var j in list)
            {
                var d = Dist(x, y, _nodes[j].X, _nodes[j].Y);
                _adj[n].Add((j, d, false)); _adj[j].Add((n, d, false));
            }
        Add(_nodesByMap, map, n);
        return n;
    }

    /// <summary>Learn a gate seen in the world (its destination is on the gate itself). It lands on the destination
    /// map's regen point until a hop teaches the real arrival (<see cref="LearnArrival"/>). True when new.</summary>
    public bool LearnGate(int mapId, double x, double y, int toMap)
    {
        if (mapId < 0 || toMap < 0 || mapId == toMap) return false;
        lock (_gate)
        {
            if (_nodesByMap.TryGetValue(mapId, out var list))
                foreach (var n in list)
                    if (Dist(_nodes[n].X, _nodes[n].Y, x, y) < 300 && _adj[n].Exists(e => e.Gate && _nodes[e.To].Map == toMap)) return false;
            var a = AddNode(mapId, x, y);
            var (ax, ay) = _regen.TryGetValue(toMap, out var rg) ? rg : (0.0, 0.0);
            var b = AddNode(toMap, ax, ay);
            _adj[a].Add((b, 0, true));
            _learned.Add((mapId, x, y, toMap));
            _routes.Clear();
        }
        GateLearned?.Invoke(mapId, x, y, toMap);
        return true;
    }

    /// <summary>Learn where a hop from one map landed on the next (replaces a regen-point guess near nothing better).</summary>
    public void LearnArrival(int fromMap, int toMap, double x, double y)
    {
        lock (_gate)
        {
            if (!_nodesByMap.TryGetValue(fromMap, out var list)) return;
            foreach (var n in list)
                foreach (var (to, _, gate) in _adj[n])
                    if (gate && _nodes[to].Map == toMap && _regen.TryGetValue(toMap, out var rg) && _nodes[to].X == rg.X && _nodes[to].Y == rg.Y)
                        _nodes[to] = (toMap, x, y);
            _routes.Clear();
        }
    }

    /// <summary>Gates learned so far (for persistence).</summary>
    public IReadOnlyList<(int Map, double X, double Y, int To)> Learned { get { lock (_gate) return _learned.ToArray(); } }

    /// <summary>Route from a spot to a map (and a spot on it when x/y are given): Dijkstra over the gate graph. Null
    /// when the maps are not connected; an empty hop list when already on the map.</summary>
    public WorldRoute? Route(int fromMap, double fx, double fy, int toMap, double tx = double.NaN, double ty = double.NaN, Func<int, bool>? avoid = null,
        Func<SpawnArea, bool>? dangerous = null)
    {
        lock (_gate) return RouteLocked(fromMap, fx, fy, toMap, tx, ty, avoid, dangerous);
    }

    /// <summary>Does the straight walk between two points of a map pass through (within margin of) a spawn area the
    /// predicate calls dangerous?</summary>
    public bool SegmentHitsDanger(int mapId, double ax, double ay, double bx, double by, Func<SpawnArea, bool> dangerous, double margin = 300)
    {
        foreach (var a in SpawnsOn(mapId))
        {
            if (!dangerous(a)) continue;
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            var t = l2 <= 0 ? 0 : Math.Clamp(((a.X - ax) * dx + (a.Y - ay) * dy) / l2, 0, 1);
            if (Dist(ax + t * dx, ay + t * dy, a.X, a.Y) < Math.Max(a.HalfW, a.HalfH) + margin) return true;
        }
        return false;
    }

    /// <summary>The level of the easiest hostile mob that spawns on a map (-1 none), given MobInfo levels (cached).</summary>
    public int EasiestMobLevel(int mapId, Func<int, int> hostileLevel)
    {
        lock (_gate)
        {
            if (_easiest.TryGetValue(mapId, out var v)) return v;
            var min = int.MaxValue;
            foreach (var a in SpawnsOn(mapId)) { var l = hostileLevel(a.MobId); if (l >= 0 && l < min) min = l; }
            return _easiest[mapId] = min == int.MaxValue ? -1 : min;
        }
    }
    private readonly Dictionary<int, int> _easiest = new();

    private WorldRoute? RouteLocked(int fromMap, double fx, double fy, int toMap, double tx, double ty, Func<int, bool>? avoid = null,
        Func<SpawnArea, bool>? dangerous = null)
    {
        if (fromMap == toMap) return new WorldRoute([], double.IsNaN(tx) ? 0 : Dist(fx, fy, tx, ty));
        if (!_nodesByMap.TryGetValue(fromMap, out var starts) || !_nodesByMap.ContainsKey(toMap)) return null;
        var n = _nodes.Count;
        var dist = new double[n]; var prev = new int[n];
        Array.Fill(dist, double.PositiveInfinity); Array.Fill(prev, -1);
        var pq = new PriorityQueue<int, double>();
        foreach (var s in starts) { dist[s] = Dist(fx, fy, _nodes[s].X, _nodes[s].Y); pq.Enqueue(s, dist[s]); }
        var best = -1; var bestCost = double.PositiveInfinity;
        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > dist[u]) continue;
            if (_nodes[u].Map == toMap)
            {
                var end = du + (double.IsNaN(tx) ? 0 : Dist(_nodes[u].X, _nodes[u].Y, tx, ty));
                if (end < bestCost) { bestCost = end; best = u; }
                continue;   // arrived; do not walk on through the target map's other gates
            }
            if (du >= bestCost) break;
            foreach (var (v, c, _) in _adj[u])
            {
                var vm = _nodes[v].Map;
                if (avoid is not null && vm != toMap && vm != fromMap && avoid(vm)) continue;
                // a walk inside a map that passes a dangerous spawn area is not taken (danger on the path, not on the map)
                // (not on the map we stand on: the way out has to be walked whatever spawns there)
                if (dangerous is not null && vm == _nodes[u].Map && vm != fromMap
                    && SegmentHitsDanger(vm, _nodes[u].X, _nodes[u].Y, _nodes[v].X, _nodes[v].Y, dangerous)) continue;
                var nd = du + c;
                if (nd < dist[v]) { dist[v] = nd; prev[v] = u; pq.Enqueue(v, nd); }
            }
        }
        if (best < 0) return null;
        var hops = new List<RouteHop>();
        for (var v = best; prev[v] >= 0; v = prev[v])
        {
            var u = prev[v];
            if (_nodes[u].Map != _nodes[v].Map) hops.Add(new RouteHop(_nodes[u].Map, _nodes[u].X, _nodes[u].Y, _nodes[v].Map));
        }
        hops.Reverse();
        return new WorldRoute(hops, bestCost);
    }

    /// <summary>The route cost from a spot to EVERY reachable map in one search (one Dijkstra instead of one per target;
    /// the planner asks for many maps per decision). A map's cost is the cheapest arrival at any of its nodes; avoided
    /// maps and walks past dangerous spawn areas are treated as in <see cref="Route"/> (an avoided map can be a target,
    /// never a way through). Fills <paramref name="costs"/> (cleared first).</summary>
    public void RouteCosts(int fromMap, double fx, double fy, Dictionary<int, double> costs, Func<int, bool>? avoid = null,
        Func<SpawnArea, bool>? dangerous = null)
    {
        costs.Clear();
        costs[fromMap] = 0;
        lock (_gate)
        {
            if (!_nodesByMap.TryGetValue(fromMap, out var starts)) return;
            var n = _nodes.Count;
            var dist = new double[n];
            Array.Fill(dist, double.PositiveInfinity);
            var pq = new PriorityQueue<int, double>();
            foreach (var s in starts) { dist[s] = Dist(fx, fy, _nodes[s].X, _nodes[s].Y); pq.Enqueue(s, dist[s]); }
            while (pq.TryDequeue(out var u, out var du))
            {
                if (du > dist[u]) continue;
                var um = _nodes[u].Map;
                if (um != fromMap && !costs.ContainsKey(um)) costs[um] = du;
                // arrived on an avoided map: a target, never a way through
                if (avoid is not null && um != fromMap && avoid(um)) continue;
                foreach (var (v, c, _) in _adj[u])
                {
                    var vm = _nodes[v].Map;
                    if (dangerous is not null && vm == um && vm != fromMap
                        && SegmentHitsDanger(vm, _nodes[u].X, _nodes[u].Y, _nodes[v].X, _nodes[v].Y, dangerous)) continue;
                    var nd = du + c;
                    if (nd < dist[v]) { dist[v] = nd; pq.Enqueue(v, nd); }
                }
            }
        }
    }

    /// <summary>Route cost between maps (cached per map pair, from the map's first node; -1 not connected, 0 same).</summary>
    public double MapDistance(int fromMap, int toMap)
    {
        if (fromMap == toMap) return 0;
        lock (_gate)
        {
            if (!_routes.TryGetValue((fromMap, toMap), out var r))
            {
                var start = _nodesByMap.TryGetValue(fromMap, out var l) && l.Count > 0 ? _nodes[l[0]] : default;
                r = _routes[(fromMap, toMap)] = RouteLocked(fromMap, start.X, start.Y, toMap, double.NaN, double.NaN);
            }
            return r?.Cost ?? -1;
        }
    }
}
