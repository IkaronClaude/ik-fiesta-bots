namespace Fiesta.Bot.Pathfinding;

/// <summary>Pathing over a <see cref="NavMesh"/>: a Dijkstra across regions, then string-pulling through the
/// portals to get a path that hugs corners instead of bouncing off region centres.</summary>
public static class NavMeshPath
{
    /// <summary>Region path from the region containing (sx,sy) to the one containing (gx,gy), or null.
    /// Costs are centre-to-portal-to-centre, which is approximate -- it is the funnel afterwards that recovers the
    /// real geometry, so this only has to get the SEQUENCE of regions right.</summary>
    private static List<int>? RegionRoute(NavMesh mesh, int startR, int goalR)
    {
        if (startR < 0 || goalR < 0) return null;
        if (startR == goalR) return new List<int> { startR };
        int n = mesh.Rects.Count;
        var dist = new int[n];
        var prev = new int[n];
        Array.Fill(dist, int.MaxValue);
        Array.Fill(prev, -1);
        dist[startR] = 0;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(startR, 0);
        while (open.TryDequeue(out var cur, out var d))
        {
            if (cur == goalR) break;
            if (d > dist[cur]) continue;
            var rc = mesh.Rects[cur];
            foreach (var p in mesh.Portals[cur])
            {
                var rn = mesh.Rects[p.To];
                int dx = rc.CX - rn.CX, dy = rc.CY - rn.CY;
                int step = (int)(Math.Sqrt((double)dx * dx + (double)dy * dy) * 10);
                int nd = d + Math.Max(1, step);
                if (nd >= dist[p.To]) continue;
                dist[p.To] = nd; prev[p.To] = cur;
                open.Enqueue(p.To, nd);
            }
        }
        if (dist[goalR] == int.MaxValue) return null;
        var route = new List<int>();
        for (int c = goalR; c >= 0; c = prev[c]) route.Add(c);
        route.Reverse();
        return route;
    }

    /// <summary>Full path in TILE coordinates, or null when the goal is not reachable through the mesh.</summary>
    public static List<(int X, int Y)>? Find(NavMesh mesh, int sx, int sy, int gx, int gy, bool swapWinding = false)
    {
        int sr = mesh.RegionAt(sx, sy), gr = mesh.RegionAt(gx, gy);
        var route = RegionRoute(mesh, sr, gr);
        if (route is null) return null;
        if (route.Count == 1) return new List<(int, int)> { (sx, sy), (gx, gy) };

        // Portal list as (left,right) pairs. The funnel needs each gate as a SEGMENT with consistent handedness,
        // not a midpoint -- handing it midpoints is precisely the centre-to-centre path we are trying to avoid.
        var lefts = new List<(double X, double Y)>();
        var rights = new List<(double X, double Y)>();
        for (int i = 0; i + 1 < route.Count; i++)
        {
            var p = FindPortal(mesh, route[i], route[i + 1]);
            if (p is not { } pt) return null;
            // Half-tile inset keeps the funnel off the exact boundary line, where rounding can place a waypoint in
            // the neighbouring wall.
            // HANDEDNESS IS RELATIVE TO THE DIRECTION OF TRAVEL. The funnel keeps a left/right wedge, so a portal
            // crossed in -x or -y has its endpoints in the opposite order; assigning them positionally (as a first
            // version did) inverts the wedge on every such crossing, which pulls the path to the WRONG side of the
            // doorway. Measured signature of that bug: 83 of 91 paths clipping walls and 2.6x the length of A*.
            var ra = mesh.Rects[route[i]];
            var rb = mesh.Rects[route[i + 1]];
            double dirX = rb.CX - ra.CX, dirY = rb.CY - ra.CY;
            (double X, double Y) e1, e2;
            bool forward;
            if (pt.Vertical)
            {
                double x = pt.At;
                e1 = (x, pt.From); e2 = (x, pt.Until + 1.0);
                forward = dirX >= 0;
            }
            else
            {
                double y = pt.At;
                e1 = (pt.From, y); e2 = (pt.Until + 1.0, y);
                forward = dirY >= 0;
            }
            if (forward != swapWinding) { lefts.Add(e1); rights.Add(e2); }
            else { lefts.Add(e2); rights.Add(e1); }
        }
        // Portal midpoints first, then a VERIFIED string-pull.
        //
        // The first version used a Simple Stupid Funnel here and it did not work: ~90% of paths clipped walls, at
        // both windings, with segments averaging 85-320 tiles -- the wedge was collapsing and emitting near-straight
        // lines across the map. A funnel is only safe if it is exactly right, because it never CHECKS anything: it
        // assumes the corridor is convex, and a chain of rectangles is not.
        //
        // So this pulls the string by testing instead of assuming. From each anchor, reach for the furthest later
        // point that still has clear line of sight, and settle for the last one that did. Worst case it degrades to
        // the portal midpoints, which are inside free space by construction -- it cannot produce a path through a
        // wall, which is the property that actually matters.
        // INTERLEAVE REGION CENTRES WITH PORTAL MIDPOINTS.
        // Consecutive portal midpoints lie in DIFFERENT regions. Each region is convex, but the union of two
        // adjacent rectangles is not, so a straight line between two midpoints can leave the corridor -- which is
        // what the first string-pull did whenever no longer sight-line existed and it fell back to the next point
        // untested (126 of 190 invalid on Sand Hill).
        // Alternating centre, portal, centre, portal makes every consecutive pair lie inside ONE convex rectangle,
        // so the unpulled path is valid by construction and the pull can only shorten it.
        var via = new List<(double X, double Y)> { (sx + 0.5, sy + 0.5) };
        for (int i = 0; i < lefts.Count; i++)
        {
            var rc = mesh.Rects[route[i]];
            via.Add((rc.CX + 0.5, rc.CY + 0.5));
            // The MIDPOINT of a portal is not guaranteed to survive flooring: it sits on the boundary, and the
            // tile it lands in can be one the mesh excluded for clearance. Walk outward along the span for the
            // nearest tile that is genuinely in the mesh, so every via point is real free space.
            var pp = FindPortal(mesh, route[i], route[i + 1]);
            via.Add(pp is { } pt2
                ? OnPortal(mesh, pt2)
                : ((lefts[i].X + rights[i].X) / 2.0, (lefts[i].Y + rights[i].Y) / 2.0));
        }
        var rl = mesh.Rects[route[^1]];
        via.Add((rl.CX + 0.5, rl.CY + 0.5));
        via.Add((gx + 0.5, gy + 0.5));

        // SNAP TO TILES *BEFORE* TESTING, so the segment we verify is the segment we return.
        // The first version tested `via` in continuous coordinates and then floored each endpoint into a tile on
        // the way out. Flooring moves an endpoint by up to a whole tile, which alongside a wall is enough to push
        // the emitted line into the clearance band that the mesh deliberately excludes -- so Clear() would pass on
        // a line that was never the one handed back. Caught by dumping a failing case: tile (809,761),
        // IsWalkable=True but IsPathable(margin 2)=False, on a 114-tile segment whose ENDPOINTS were both fine.
        var tiles = new List<(int X, int Y)>(via.Count);
        foreach (var (vx, vy) in via)
        {
            var t = ((int)Math.Floor(vx), (int)Math.Floor(vy));
            if (tiles.Count == 0 || tiles[^1] != t) tiles.Add(t);
        }

        var outp = new List<(int X, int Y)>();
        int at = 0;
        while (at < tiles.Count - 1)
        {
            // Reach for the furthest point still in sight; fall back to the next one, which is inside a single
            // convex region and therefore always reachable.
            int best = at + 1;
            for (int k = tiles.Count - 1; k > at; k--)
                if (Clear(mesh, tiles[at], tiles[k])) { best = k; break; }
            if (outp.Count == 0 || outp[^1] != tiles[at]) outp.Add(tiles[at]);
            at = best;
        }
        if (outp.Count == 0 || outp[^1] != tiles[^1]) outp.Add(tiles[^1]);
        return outp;
    }

    /// <summary>Is the straight segment between two points entirely inside free space? Sampled at half-tile steps,
    /// which is finer than the one-tile features the grid can express.</summary>
    private static bool Clear(NavMesh mesh, (int X, int Y) a, (int X, int Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        int steps = (int)Math.Max(1, len * 2);
        for (int q = 0; q <= steps; q++)
        {
            double t = (double)q / steps;
            if (mesh.RegionAt((int)Math.Round(a.X + dx * t), (int)Math.Round(a.Y + dy * t)) < 0) return false;
        }
        return true;
    }

    /// <summary>A point ON a portal that is actually inside the mesh. Starts at the midpoint -- the natural
    /// crossing -- and walks outward along the span until it finds a tile the mesh kept, because the ends of a
    /// span often fall inside the clearance band that the decomposition deliberately excluded.</summary>
    private static (double X, double Y) OnPortal(NavMesh mesh, NavMesh.Portal p)
    {
        int lo = p.From, hi = p.Until, mid = (lo + hi) / 2;
        for (int d = 0; d <= hi - lo; d++)
        {
            foreach (var a in d == 0 ? new[] { mid } : new[] { mid - d, mid + d })
            {
                if (a < lo || a > hi) continue;
                int tx = p.Vertical ? p.At : a;
                int ty = p.Vertical ? a : p.At;
                if (mesh.RegionAt(tx, ty) >= 0) return (tx + 0.5, ty + 0.5);
            }
        }
        return p.Vertical ? (p.At + 0.5, mid + 0.5) : (mid + 0.5, p.At + 0.5);
    }

    private static NavMesh.Portal? FindPortal(NavMesh mesh, int a, int b)
    {
        foreach (var p in mesh.Portals[a]) if (p.To == b) return p;
        return null;
    }

}
