using System.Collections.Concurrent;

namespace Fiesta.Bot.Navigation;

/// <summary>One discovered gate edge: on <see cref="FromMap"/> there is a gate at
/// (<see cref="GateX"/>,<see cref="GateY"/>) — the NPC handle <see cref="GateHandle"/>
/// while it's in view — that links to <see cref="ToMap"/>. Coordinates are the gate
/// NPC's world position (from the zone MOB briefinfo); the bot walks to within range
/// of it, then targets + clicks it to take the link.</summary>
public sealed record GateEdge(string FromMap, string ToMap, uint GateX, uint GateY, ushort GateHandle);

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

    /// <summary>Record/refresh a gate seen in <paramref name="fromMap"/>. Ignores
    /// gates with no known destination. The latest coordinates/handle win (the handle
    /// is only valid while in view, the link itself is stable).</summary>
    public void ObserveGate(string fromMap, string toMap, uint gateX, uint gateY, ushort gateHandle)
    {
        if (string.IsNullOrWhiteSpace(fromMap) || string.IsNullOrWhiteSpace(toMap)) return;
        var dests = _edges.GetOrAdd(fromMap, _ => new(StringComparer.OrdinalIgnoreCase));
        dests[toMap] = new GateEdge(fromMap, toMap, gateX, gateY, gateHandle);
    }

    /// <summary>Maps directly reachable from <paramref name="fromMap"/> by one gate.</summary>
    public IReadOnlyCollection<GateEdge> EdgesFrom(string fromMap) =>
        _edges.TryGetValue(fromMap, out var d) ? d.Values.ToArray() : Array.Empty<GateEdge>();

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
