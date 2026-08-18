using System.Collections.Concurrent;

namespace Fiesta.Bot.Navigation;

/// <summary>One discovered gate edge: on there is a gate at ( , ) — the NPC handle while it's in view — that links to</summary>
public sealed record GateEdge(string FromMap, string ToMap, uint GateX, uint GateY, ushort GateHandle,
    int? PortalDestIndex = null, int MinLevel = 0, uint? ToX = null, uint? ToY = null)
{
    /// <summary>True if this edge is taken via the town-portal packet (not a field gate click)</summary>
    public bool IsPortal => PortalDestIndex is not null;

    /// <summary>Where we land on : the recorded arrival when there is one, otherwise the transition coord</summary>
    public (uint X, uint Y) Arrival => ToX is { } x && ToY is { } y ? (x, y) : (GateX, GateY);
}

/// <summary>A directed graph of maps connected by gates, used to plan multi-map routes</summary>
public sealed class MapGraph
{
    // fromMap -> (toMap -> the most recently seen edge for that link)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GateEdge>> _edges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once has populated the graph from server/client nav data, so callers seed only once</summary>
    public bool Seeded { get; private set; }

    /// <summary>Seed the graph from the game's own cross-map gate web (ClientData.BuildGateEdges, from MapWayPoint/MapLinkPoin…</summary>
    public int Seed(IEnumerable<(string From, string To, uint X, uint Y, uint ToX, uint ToY)> edges)
    {
        var n = 0;
        foreach (var (from, to, x, y, tx, ty) in edges) { ObserveGate(from, to, x, y, 0, tx, ty); n++; }
        // ONLY latch when the seed actually produced edges
        if (n > 0) Seeded = true;
        return n;
    }

    /// <summary>Record/refresh a gate seen in</summary>
    public void ObserveGate(string fromMap, string toMap, uint gateX, uint gateY, ushort gateHandle,
        uint? toX = null, uint? toY = null)
    {
        if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap)) return;
        var dests = _edges.GetOrAdd(fromMap, _ => new(StringComparer.OrdinalIgnoreCase));
        // Preserve a previously-seeded arrival coord when a live re-observe doesn't carry one
        if (toX is null && toY is null && dests.TryGetValue(toMap, out var old) && old.ToX is not null)
            (toX, toY) = (old.ToX, old.ToY);
        dests[toMap] = new GateEdge(fromMap, toMap, gateX, gateY, gateHandle, ToX: toX, ToY: toY);
    }

    /// <summary>Maps directly reachable from by one field gate</summary>
    public IReadOnlyCollection<GateEdge> EdgesFrom(string fromMap) =>
        _edges.TryGetValue(fromMap, out var d) ? d.Values.ToArray() : Array.Empty<GateEdge>();

    /// <summary>Prune a field-gate edge that was proven BOGUS at runtime — the travel loop walked to the edge's stored gate co…</summary>
    public bool RemoveEdge(string fromMap, string toMap)
    {
        if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap)) return false;
        return _edges.TryGetValue(fromMap, out var d) && d.TryRemove(toMap, out _);
    }

    // fromMap -> town-portal edges out of it (parallel to _edges so a field gate AND a portal to the SAME destinatio…
    private readonly ConcurrentDictionary<string, List<GateEdge>> _portalEdges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seed the town-portal edges (built from TownPortal.shn)</summary>
    public void SeedPortals(IEnumerable<GateEdge> portalEdges)
    {
        _portalEdges.Clear();
        foreach (var e in portalEdges)
        {
            if (e.PortalDestIndex is null) continue;
            _portalEdges.GetOrAdd(e.FromMap, _ => new()).Add(e);
        }
    }

    /// <summary>Town-portal edges out of (empty if none)</summary>
    public IReadOnlyCollection<GateEdge> PortalEdgesFrom(string fromMap) =>
        _portalEdges.TryGetValue(fromMap, out var l) ? l.ToArray() : Array.Empty<GateEdge>();

    /// <summary>Every outgoing edge from — field gates AND town portals</summary>
    private IEnumerable<GateEdge> AllEdgesFrom(string map) => EdgesFrom(map).Concat(PortalEdgesFrom(map));

    /// <summary>All known maps (any that appear as a source or destination)</summary>
    public IReadOnlyCollection<string> Maps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, dests) in _edges)
        {
            set.Add(from);
            foreach (var to in dests.Keys) set.Add(to);
        }
        return set;
    }

    /// <summary>Shortest gate sequence from to (BFS over discovered edges), or null if no known route</summary>
    public IReadOnlyList<GateEdge>? Route(string fromMap, string toMap)
    {
        if (string.Equals(fromMap, toMap, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<GateEdge>();

        var prev = new Dictionary<string, GateEdge>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromMap };
        var queue = new Queue<string>();
        queue.Enqueue(fromMap);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var edge in EdgesFrom(cur))
            {
                if (!visited.Add(edge.ToMap)) continue;
                prev[edge.ToMap] = edge;
                if (string.Equals(edge.ToMap, toMap, StringComparison.OrdinalIgnoreCase))
                    return Backtrack(prev, toMap);
                queue.Enqueue(edge.ToMap);
            }
        }
        return null;
    }

    /// <summary>Least-COST route from @ to @ over field gates AND town portals (Dijkstra)</summary>
    public (IReadOnlyList<GateEdge> Route, double Cost)? RouteCost(
        string fromMap, (uint X, uint Y) fromPos, string toMap, (uint X, uint Y)? toPos,
        int botLevel, Func<(uint X, uint Y), (uint X, uint Y), double> walkCost, double portalHopCost = 0)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        double FinalLeg((uint X, uint Y) arr) => toPos is { } t ? walkCost(arr, t) : 0;
        if (cmp.Equals(fromMap, toMap))
            return (Array.Empty<GateEdge>(), FinalLeg(fromPos));

        var dist = new Dictionary<string, double>(cmp) { [fromMap] = 0 };
        var arrival = new Dictionary<string, (uint X, uint Y)>(cmp) { [fromMap] = fromPos };
        var prev = new Dictionary<string, GateEdge>(cmp);
        var done = new HashSet<string>(cmp);
        var pq = new PriorityQueue<string, double>();
        pq.Enqueue(fromMap, 0);

        while (pq.TryDequeue(out var m, out _))
        {
            if (!done.Add(m)) continue;             // stale (already settled at a lower cost)
            if (cmp.Equals(m, toMap)) break;        // best arrival on the target is now settled
            var p = arrival[m];
            double dm = dist[m];
            foreach (var e in AllEdgesFrom(m))
            {
                if (done.Contains(e.ToMap)) continue;
                if (e.MinLevel > 0 && botLevel < e.MinLevel) continue; // level-gated portal tier
                double nd = dm + walkCost(p, (e.GateX, e.GateY)) + (e.IsPortal ? portalHopCost : 0);
                if (dist.TryGetValue(e.ToMap, out var od) && nd >= od) continue;
                dist[e.ToMap] = nd;
                prev[e.ToMap] = e;
                // Entry point on the next map = the edge's arrival coord (portal dest / paired gate); fall back to the transitio…
                arrival[e.ToMap] = e.Arrival;
                pq.Enqueue(e.ToMap, nd);
            }
        }
        if (!dist.ContainsKey(toMap)) return null;
        var route = Backtrack(prev, toMap);
        return (route, dist[toMap] + FinalLeg(arrival[toMap])); // + final walk to target (if any)
    }

    private static List<GateEdge> Backtrack(Dictionary<string, GateEdge> prev, string toMap)
    {
        var route = new List<GateEdge>();
        var cur = toMap;
        while (prev.TryGetValue(cur, out var edge))
        {
            route.Add(edge);
            cur = edge.FromMap;
        }
        route.Reverse();
        return route;
    }
}
