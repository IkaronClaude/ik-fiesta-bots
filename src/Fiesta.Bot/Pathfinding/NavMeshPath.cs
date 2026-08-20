namespace Fiesta.Bot.Pathfinding;

/// <summary>Pathing over a <see cref="NavMesh"/>: a Dijkstra across regions, then string-pulling through the
/// portals to get a path that hugs corners instead of bouncing off region centres.</summary>
public static class NavMeshPath
{
    /// <summary>Region path from the region containing (sx,sy) to the one containing (gx,gy), or null.
    /// Costs are centre-to-portal-to-centre, which is approximate -- it is the funnel afterwards that recovers the
    /// real geometry, so this only has to get the SEQUENCE of regions right.</summary>
    /// <summary>Is this region currently usable? A region tagged with a door disappears while that door is shut --
    /// which is why doors are FILLED with their own rectangles rather than carved out: closing one is a node
    /// toggle, and every portal into those nodes goes with them, so no portal bookkeeping is needed at all.</summary>
    private static bool Usable(NavMesh mesh, int r, Func<int, bool>? doorClosed)
        => doorClosed is null || mesh.RegionDoor[r] < 0 || !doorClosed(mesh.RegionDoor[r]);

    /// <summary>Can these two tiles reach each other at all, honouring live door state?
    ///
    /// Replaces the separate IslandMap. At margin 0 the mesh covers exactly the walkable ground, so connected
    /// components of the REGION graph answer the same question as a tile flood fill -- over 8-17k regions instead
    /// of millions of tiles, and unlike a baked island plane it can account for a door being shut right now.</summary>
    public static bool Reachable(NavMesh mesh, int sx, int sy, int gx, int gy, Func<int, bool>? doorClosed = null)
    {
        int a = mesh.RegionAt(sx, sy), b = mesh.RegionAt(gx, gy);
        if (a < 0 || b < 0) return true;              // outside the mesh: cannot prove anything, so do not claim to
        if (a == b) return Usable(mesh, a, doorClosed);
        return RegionRoute(mesh, a, b, doorClosed) is not null;
    }

    private static List<int>? RegionRoute(NavMesh mesh, int startR, int goalR, Func<int, bool>? doorClosed)
    {
        if (startR < 0 || goalR < 0) return null;
        if (!Usable(mesh, startR, doorClosed) || !Usable(mesh, goalR, doorClosed)) return null;
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
                if (!Usable(mesh, p.To, doorClosed)) continue;
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
    public static List<(int X, int Y)>? Find(NavMesh mesh, int sx, int sy, int gx, int gy, Func<int, bool>? doorClosed = null)
    {
        // SNAP ENDPOINTS INTO THE MESH FIRST.
        // The mesh excludes the clearance band, and the bot is very often standing IN it -- measured, the incumbent
        // A* crosses that band on 60-94% of its paths, so it routinely parks the character there. Without snapping,
        // RegionAt would be -1 for the start and every such request would fail outright.
        // The real start/goal are re-attached afterwards, so the caller still gets a path from where the bot ACTUALLY
        // is to where it was actually asked to go; only the middle is mesh-routed.
        int osx = sx, osy = sy, ogx = gx, ogy = gy;
        if (mesh.RegionAt(sx, sy) < 0 && NearestRegion(mesh, sx, sy) is { } ns) { sx = ns.X; sy = ns.Y; }
        if (mesh.RegionAt(gx, gy) < 0 && NearestRegion(mesh, gx, gy) is { } ng) { gx = ng.X; gy = ng.Y; }

        int sr = mesh.RegionAt(sx, sy), gr = mesh.RegionAt(gx, gy);
        var route = RegionRoute(mesh, sr, gr, doorClosed);
        if (route is null) return null;
        if (route.Count == 1) return Reattach(osx, osy, ogx, ogy, new List<(int, int)> { (sx, sy), (gx, gy) });

        // BUILD A PATH THAT IS VALID BY CONSTRUCTION, IN TILES, AND ONLY THEN SHORTEN IT.
        //
        // The invariant we need is: EVERY consecutive pair of via points lies inside ONE rectangle. That is the
        // only statement convexity actually gives us -- the union of two adjacent rectangles is NOT convex, so a
        // pair that merely "straddles a portal" is not covered by it.
        //
        // The previous version alternated centre, PORTAL POINT, centre. A portal point sits ON the far side of the
        // boundary (Portal.At is the neighbour's first row/column -- measured: true for all 111,598 portal records
        // across the five test maps), so every centre<->portal pair straddled the boundary, and a straddling pair
        // is exactly the case the convexity argument does not cover. In CONTINUOUS coordinates the excursion looks
        // harmless (half a tile); in TILE coordinates -- which is what we emit and what gets walked -- the sampled
        // column flips at the MIDPOINT of the x-travel, not at the boundary. So on a long thin strip the line
        // spends HALF its length in the neighbour's column while its y has already run off the end of the
        // neighbour's extent. Measured failing case on Sand Hill (EldGbl02): portal point (1023,732) in r2621
        // (x1023, y710..756) to the centre (1022,473) of r120 (x1022, y191..755) clipped at (1023,709) -- one row
        // past the end of r2621, with 130 more rows of the same column still to come. That single geometry error
        // accounted for 216 of 380 invalid paths there.
        //
        // The fix is to never straddle: cross the boundary with its OWN one-tile step. Each portal contributes the
        // pair of adjacent tiles either side of it, at the same offset along the span, near side first:
        //     centre(A) -> nearTile   both in A
        //     nearTile  -> farTile    one tile apart, both in the mesh
        //     farTile   -> centre(B)  both in B
        // Any offset in [From,Until] works for this: the run was grouped by owner, so along the whole span the low
        // tile is in one region and the high tile in the other (measured: 0 violations over those same 111,598
        // portals). Which offset to use is therefore free, and is chosen below.
        var tiles = new List<(int X, int Y)>();
        void Push(int x, int y) { if (tiles.Count == 0 || tiles[^1] != (x, y)) tiles.Add((x, y)); }

        // WHERE along each doorway to cross is a FREE PARAMETER, so choose it instead of always taking the middle.
        // Every offset in [From,Until] is equally legal (the run was grouped by owner, so the straddling pair is
        // {this region, that region} along the whole span), which means picking a good one cannot cost validity --
        // only length. Do not expect much from it on these maps: measured against A*, always-midpoint gave
        // 1.133/1.188/1.200/1.177 x on EldGbl02/Eld/RouVal01/RouCos02 and relaxing the offset gives
        // 1.115/1.194/1.200/1.162 -- a percent or two, because greedy strips make most spans short. It is kept
        // because it is free and because a wide doorway is exactly where a midpoint crossing is worst.
        // Relaxation: each crossing is placed where it minimises (previous crossing -> here -> next crossing),
        // sweeping a few times because moving one crossing changes its neighbours' best answer. Both terms are
        // convex in the offset, so the minimum is found by ternary search rather than scanning the span.
        var ports = new NavMesh.Portal[route.Count - 1];
        for (int i = 0; i + 1 < route.Count; i++)
        {
            if (FindPortal(mesh, route[i], route[i + 1]) is not { } p) return null;
            ports[i] = p;
        }
        var cut = new int[ports.Length];
        for (int i = 0; i < ports.Length; i++) cut[i] = ports[i].MidA;
        // The crossing point is ON the shared edge: x = At for a vertical portal (the line between column At-1 and
        // column At), and the offset picks where along it.
        (double X, double Y) Cross(int i) => ports[i].Vertical ? (ports[i].At, cut[i] + 0.5) : (cut[i] + 0.5, ports[i].At);
        for (int pass = 0; pass < 3; pass++)
            for (int i = 0; i < ports.Length; i++)
            {
                var a = i == 0 ? (X: sx + 0.5, Y: sy + 0.5) : Cross(i - 1);
                var b = i == ports.Length - 1 ? (X: gx + 0.5, Y: gy + 0.5) : Cross(i + 1);
                var pt = ports[i];
                double Cost(int o)
                {
                    double px = pt.Vertical ? pt.At : o + 0.5, py = pt.Vertical ? o + 0.5 : pt.At;
                    return Math.Sqrt((px - a.X) * (px - a.X) + (py - a.Y) * (py - a.Y))
                         + Math.Sqrt((px - b.X) * (px - b.X) + (py - b.Y) * (py - b.Y));
                }
                int lo = pt.From, hi = pt.Until;
                while (hi - lo > 2)
                {
                    int m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                    if (Cost(m1) <= Cost(m2)) hi = m2 - 1; else lo = m1 + 1;
                }
                int bestO = lo;
                for (int o = lo + 1; o <= hi; o++) if (Cost(o) < Cost(bestO)) bestO = o;
                cut[i] = bestO;
            }

        Push(sx, sy);
        for (int i = 0; i < ports.Length; i++)
        {
            // The region centre stays in the list as an extra anchor for the string-pull. It is inside the region
            // by construction, so it can never invalidate anything, and having more candidates measurably helps
            // the pull find a longer clear run (removing it made Eld 8% LONGER).
            var rc = mesh.Rects[route[i]];
            Push(rc.CX, rc.CY);
            var (near, far) = PortalStep(mesh, ports[i], route[i], cut[i]);
            Push(near.X, near.Y);
            Push(far.X, far.Y);
        }
        var rl = mesh.Rects[route[^1]];
        Push(rl.CX, rl.CY);
        Push(gx, gy);

        // String-pull by TESTING, not by assuming. From each anchor, reach for the furthest later point that still
        // has clear line of sight and settle for the last one that did. Worst case it degrades to the list above,
        // which is valid by construction -- so it can shorten a path but never invalidate one. (A Simple Stupid
        // Funnel was tried here and removed: it never checks anything and assumes a convex corridor, which a chain
        // of rectangles is not; ~90% of paths clipped at either winding.)
        var outp = new List<(int X, int Y)>();
        int at = 0;
        while (at < tiles.Count - 1)
        {
            int best = at + 1;
            for (int k = tiles.Count - 1; k > at; k--)
                if (Clear(mesh, tiles[at], tiles[k])) { best = k; break; }
            if (outp.Count == 0 || outp[^1] != tiles[at]) outp.Add(tiles[at]);
            at = best;
        }
        if (outp.Count == 0 || outp[^1] != tiles[^1]) outp.Add(tiles[^1]);
        return Reattach(osx, osy, ogx, ogy, outp);
    }

    /// <summary>The two adjacent tiles either side of a portal, near side (the one inside <paramref name="from"/>)
    /// first. Portal.At is the HIGH side's first row/column and At-1 is the low side's last, so the pair straddles
    /// the boundary by exactly one tile step.</summary>
    private static ((int X, int Y) Near, (int X, int Y) Far) PortalStep(NavMesh mesh, NavMesh.Portal p, int from, int a)
    {
        var lo = p.Vertical ? (X: p.At - 1, Y: a) : (X: a, Y: p.At - 1);
        var hi = p.Vertical ? (X: p.At, Y: a) : (X: a, Y: p.At);
        if (mesh.RegionAt(lo.X, lo.Y) == from) return (lo, hi);
        if (mesh.RegionAt(hi.X, hi.Y) == from) return (hi, lo);
        // Not reachable for a mesh built by NavMesh.Build (checked against every portal of the test maps); walking
        // the span is a cheap guard against a hand-made or future mesh whose runs are not owner-grouped.
        for (int d = 1; d <= p.Until - p.From; d++)
            foreach (int b in new[] { a - d, a + d })
            {
                if (b < p.From || b > p.Until) continue;
                var l2 = p.Vertical ? (X: p.At - 1, Y: b) : (X: b, Y: p.At - 1);
                var h2 = p.Vertical ? (X: p.At, Y: b) : (X: b, Y: p.At);
                if (mesh.RegionAt(l2.X, l2.Y) == from && mesh.RegionAt(h2.X, h2.Y) >= 0) return (l2, h2);
                if (mesh.RegionAt(h2.X, h2.Y) == from && mesh.RegionAt(l2.X, l2.Y) >= 0) return (h2, l2);
            }
        return (lo, hi);
    }

    /// <summary>Is the straight segment between two points entirely inside free space? Sampled at half-tile steps,
    /// which is finer than the one-tile features the grid can express.</summary>
    /// <summary>Is the straight segment between two tiles entirely inside free space?
    ///
    /// SUPERCOVER, NOT POINT SAMPLING. Sampling a line at N places is sampling-rate dependent: it happily approves
    /// a segment that shaves a corner BETWEEN two samples. That is not hypothetical -- at 2 samples per tile this
    /// reported 0 invalid paths over 3,387, while a supercover audit of the very same paths found 635 of them
    /// clipping a tile the mesh excludes (always exactly ONE tile, always at a corner). If the pull's own predicate
    /// is not exact, the pull will keep choosing shortcuts that are not actually clear.
    ///
    /// This walks EVERY tile the segment touches, so it cannot miss a clip at any geometry. Cost is O(dx+dy) with
    /// integer arithmetic only -- cheaper per tile than the old floating-point sampling, and it examines each tile
    /// exactly once instead of oversampling short segments and undersampling long ones.</summary>
    private static bool Clear(NavMesh mesh, (int X, int Y) a, (int X, int Y) b)
    {
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        int x = a.X, y = a.Y, n = 1 + dx + dy;
        int xi = b.X > a.X ? 1 : -1, yi = b.Y > a.Y ? 1 : -1;
        int err = dx - dy;
        dx *= 2; dy *= 2;
        for (; n > 0; --n)
        {
            if (mesh.RegionAt(x, y) < 0) return false;
            if (err > 0) { x += xi; err -= dy; }
            else if (err < 0) { y += yi; err += dx; }
            else { x += xi; y += yi; err -= dy; err += dx; --n; }   // exact diagonal: one step, not two
        }
        return true;
    }

    /// <summary>Spiral out to the nearest tile that belongs to a region. Bounded, because an unbounded search
    /// from deep inside a wall would scan the map.</summary>
    private static (int X, int Y)? NearestRegion(NavMesh mesh, int tx, int ty, int maxRadius = 48)
    {
        for (int r = 1; r <= maxRadius; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;   // ring only
                    int nx = tx + dx, ny = ty + dy;
                    if (mesh.RegionAt(nx, ny) >= 0) return (nx, ny);
                }
        return null;
    }

    /// <summary>Put the caller's real endpoints back on the ends. The bot walks from where it IS, not from the
    /// snapped tile, and the short hop between them is inside the clearance band by construction -- walkable
    /// ground that only the margin excludes.</summary>
    private static List<(int X, int Y)> Reattach(int sx, int sy, int gx, int gy, List<(int X, int Y)> path)
    {
        if (path.Count == 0) return path;
        if (path[0] != (sx, sy)) path.Insert(0, (sx, sy));
        if (path[^1] != (gx, gy)) path.Add((gx, gy));
        return path;
    }

    private static NavMesh.Portal? FindPortal(NavMesh mesh, int a, int b)
    {
        foreach (var p in mesh.Portals[a]) if (p.To == b) return p;
        return null;
    }

}
