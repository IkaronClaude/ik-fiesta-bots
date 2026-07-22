using System.Collections.Concurrent;

namespace Fiesta.Bot.Navigation;

/// <summary>One discovered gate edge: on <see cref="FromMap"/> there is a gate at
/// (<see cref="GateX"/>,<see cref="GateY"/>) — the NPC handle <see cref="GateHandle"/>
/// while it's in view — that links to <see cref="ToMap"/>. Coordinates are the gate
/// NPC's world position (from the zone MOB briefinfo); the bot walks to within range
/// of it, then targets + clicks it to take the link.</summary>
/// <param name="PortalDestIndex">null for a normal field gate (walk to it + click/UseGate); set for a
/// TOWN PORTAL hop — the <c>dest</c> byte to send after clicking the portal NPC (TownPortalAsync).</param>
/// <param name="MinLevel">Character level required to take this edge (town-portal tiers); 0 = none.</param>
/// <param name="ToX">Arrival coord on <see cref="ToMap"/> (portal: the destination's arrival point;
/// field gate: the paired gate's coord). Used as the entry point for costing the next hop; 0 = unknown.</param>
public sealed record GateEdge(string FromMap, string ToMap, uint GateX, uint GateY, ushort GateHandle,
    int? PortalDestIndex = null, int MinLevel = 0, uint ToX = 0, uint ToY = 0)
{
    /// <summary>True if this edge is taken via the town-portal packet (not a field gate click).</summary>
    public bool IsPortal => PortalDestIndex is not null;
}

/// <summary>
/// A directed graph of maps connected by gates, used to plan multi-map routes.
/// Nodes are map short-names; edges are <see cref="GateEdge"/>s. Built by
/// <b>auto-discovery</b>: every time a bot sees gates in a map it calls
/// <see cref="ObserveGate"/>, so the world map is learned by playing — no server
/// files required. (It can equally be seeded offline from the server NPC table.)
///
/// <para><see cref="Route"/> does a breadth-first search for the shortest gate
/// sequence between two maps. Thread-safe.</para>
/// </summary>
public sealed class MapGraph
{
    // fromMap -> (toMap -> the most recently seen edge for that link)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GateEdge>> _edges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once <see cref="Seed"/> has populated the graph from server/client nav data,
    /// so callers seed only once.</summary>
    public bool Seeded { get; private set; }

    /// <summary>Seed the graph from the game's own cross-map gate web (ClientData.BuildGateEdges,
    /// from MapWayPoint/MapLinkPoint) so routing is COMPLETE up-front instead of relying on the bot
    /// to have physically explored each link. Edges are stored with handle 0 (no live handle yet —
    /// the travel loop walks to the stored coords, which brings the gate NPC into view, then clicks
    /// it). A later <see cref="ObserveGate"/> with a live handle just refreshes the same edge.</summary>
    public void Seed(IEnumerable<(string From, string To, uint X, uint Y, uint ToX, uint ToY)> edges)
    {
        foreach (var (from, to, x, y, tx, ty) in edges) ObserveGate(from, to, x, y, 0, tx, ty);
        Seeded = true;
    }

    /// <summary>Record/refresh a gate seen in <paramref name="fromMap"/>. Ignores
    /// gates with no known destination. The latest coordinates/handle win (the handle
    /// is only valid while in view, the link itself is stable). <paramref name="toX"/>/
    /// <paramref name="toY"/> = the arrival coord on the destination (0 if unknown, e.g. a
    /// live-observed gate); it's kept when re-observing so a seeded arrival isn't lost.</summary>
    public void ObserveGate(string fromMap, string toMap, uint gateX, uint gateY, ushort gateHandle,
        uint toX = 0, uint toY = 0)
    {
        if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap)) return;
        var dests = _edges.GetOrAdd(fromMap, _ => new(StringComparer.OrdinalIgnoreCase));
        // Preserve a previously-seeded arrival coord when a live re-observe doesn't carry one.
        if ((toX == 0 && toY == 0) && dests.TryGetValue(toMap, out var old) && (old.ToX != 0 || old.ToY != 0))
            (toX, toY) = (old.ToX, old.ToY);
        dests[toMap] = new GateEdge(fromMap, toMap, gateX, gateY, gateHandle, ToX: toX, ToY: toY);
    }

    /// <summary>Maps directly reachable from <paramref name="fromMap"/> by one field gate.</summary>
    public IReadOnlyCollection<GateEdge> EdgesFrom(string fromMap) =>
        _edges.TryGetValue(fromMap, out var d) ? d.Values.ToArray() : Array.Empty<GateEdge>();

    /// <summary>Prune a field-gate edge that was proven BOGUS at runtime — the travel loop walked to the
    /// edge's stored gate coord on <paramref name="fromMap"/> but the gate to <paramref name="toMap"/> was
    /// NOT there (coord off the map's walkable grid / no gate NPC in view after the wait). A stale/mis-learned
    /// edge (e.g. RouVal02-&gt;Eld carrying EldCem01's Eld-gate coord (11829,1135), which made Dijkstra pick a
    /// non-existent 1-hop over the real RouVal02-&gt;EldCem01-&gt;Eld) permanently breaks all travel to that dest.
    /// Removing it lets the next route find the real multi-hop path; if the edge was actually valid (a transient
    /// nav miss), it's re-learned by <see cref="ObserveGate"/> the next time the bot is on that map and sees the
    /// gate. Returns true if an edge was removed. (operator 2026-07-22 — confirmed root of the NPC-&gt;map hand-in P1.)</summary>
    public bool RemoveEdge(string fromMap, string toMap)
    {
        if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap)) return false;
        return _edges.TryGetValue(fromMap, out var d) && d.TryRemove(toMap, out _);
    }

    // fromMap -> town-portal edges out of it (parallel to _edges so a field gate AND a portal to
    // the SAME destination map can coexist). Seeded once from TownPortal.shn via SeedPortals.
    private readonly ConcurrentDictionary<string, List<GateEdge>> _portalEdges =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seed the town-portal edges (built from TownPortal.shn). Each carries a
    /// <see cref="GateEdge.PortalDestIndex"/> (the dest byte) + <see cref="GateEdge.MinLevel"/>;
    /// <c>GateX/Y</c> = the portal NPC on <c>FromMap</c>, <c>ToX/Y</c> = the arrival coord on
    /// <c>ToMap</c>. Replaces any previously-seeded portal edges.</summary>
    public void SeedPortals(IEnumerable<GateEdge> portalEdges)
    {
        _portalEdges.Clear();
        foreach (var e in portalEdges)
        {
            if (e.PortalDestIndex is null) continue;
            _portalEdges.GetOrAdd(e.FromMap, _ => new()).Add(e);
        }
    }

    /// <summary>Town-portal edges out of <paramref name="fromMap"/> (empty if none).</summary>
    public IReadOnlyCollection<GateEdge> PortalEdgesFrom(string fromMap) =>
        _portalEdges.TryGetValue(fromMap, out var l) ? l.ToArray() : Array.Empty<GateEdge>();

    /// <summary>Every outgoing edge from <paramref name="map"/> — field gates AND town portals.</summary>
    private IEnumerable<GateEdge> AllEdgesFrom(string map) => EdgesFrom(map).Concat(PortalEdgesFrom(map));

    /// <summary>All known maps (any that appear as a source or destination).</summary>
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

    /// <summary>Shortest gate sequence from <paramref name="fromMap"/> to
    /// <paramref name="toMap"/> (BFS over discovered edges), or null if no known
    /// route. Empty list if already there.</summary>
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

    /// <summary>Least-COST route from <paramref name="fromMap"/>@<paramref name="fromPos"/> to
    /// <paramref name="toMap"/>@<paramref name="toPos"/> over field gates AND town portals (Dijkstra).
    /// Edge cost = the on-map walk from the entry point to the transition (gate/portal) via
    /// <paramref name="walkCost"/>, plus <paramref name="portalHopCost"/> for a portal warp (~0). The
    /// final leg (arrival→target on <c>toMap</c>) is folded into the returned cost. This is what makes
    /// the bot take a town portal ONLY when it's cheaper than hiking the field-gate chain
    /// (toGate+fromGate &lt; directWalk), and it generalises to multi-hop. Portal edges whose
    /// <see cref="GateEdge.MinLevel"/> exceeds <paramref name="botLevel"/> are skipped. Null if no route.</summary>
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
                // Entry point on the next map = the edge's arrival coord (portal dest / paired
                // gate); fall back to the transition coord if a live edge didn't record one.
                arrival[e.ToMap] = (e.ToX != 0 || e.ToY != 0) ? (e.ToX, e.ToY) : (e.GateX, e.GateY);
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
