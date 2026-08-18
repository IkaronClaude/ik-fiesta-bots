namespace Fiesta.Bot.Pathfinding;

/// <summary>A* over a (8-directional, no corner-cutting through blocked diagonals)</summary>
public static class PathFinder
{
    private static readonly (int dx, int dy)[] Neighbors =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    // A modest heuristic weight makes A* greedier — it explores roughly a corridor toward the goal instead of a full…
    private const int GreedyWeightNum = 2, GreedyWeightDen = 1; // 2.0x — fast on open maps

    /// <summary>Raised when the coarse pathfinder fails and we fall back to the unconstrained search. Never routine.</summary>
    public static Action<string>? OnFallback;

    /// <summary>Diagnostics for the coarse path: which margin/width won, how many attempts it cost.</summary>
    public static Action<string>? OnTrace;

    /// <summary>Chebyshev tile distance under which the direct search is used unchanged.</summary>
    private const int ShortRouteTiles = 96;

    /// <summary>Obstacle-inflation border in tiles (P0 2026-06-30): the interior of the path stays this many tiles clear of an…</summary>
    /// <summary>
    /// TWO-LEVEL SEARCH: route on the coarse grid, re-solve at full resolution inside a corridor around that
    /// route, and only fall back to the unconstrained search if every corridor fails. The fallback is what keeps
    /// this honest -- the operator's rule is that a path which exists must be found, so the corridor may make us
    /// FASTER but is never allowed to make us wrong.
    /// </summary>
    public static IReadOnlyList<(uint X, uint Y)> FindPathFast(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions = 8_000_000, double margin = 2)
    {
        double[] steps = margin >= 2 ? new[] { 2.0, 1.5, 1.0, 0.5, 0.0 }
                        : margin > 0 ? new[] { margin, 0.0 }
                        : new[] { 0.0 };
        // SHORT ROUTES ARE ALREADY FAST -- the corpus median is 4ms. Paying for a coarse route and a corridor
        // there is pure overhead (it cost 72ms median before this guard). The hierarchy is for the long tail.
        var (stx, sty) = grid.WorldToTile(startX, startY);
        var (gtx, gty) = grid.WorldToTile(goalX, goalY);
        if (Math.Max(Math.Abs(stx - gtx), Math.Abs(sty - gty)) <= ShortRouteTiles)
            return FindPath(grid, startX, startY, goalX, goalY, maxExpansions, margin);

        int attempts = 0; long coarseMs = 0; var swC = System.Diagnostics.Stopwatch.StartNew();
        foreach (var m in steps)
        {
            swC.Restart();
            var route = CoarsePathFinder.Route(grid, startX, startY, goalX, goalY, m);
            coarseMs += swC.ElapsedMilliseconds;
            if (route is null) continue;                    // coarse level: no route at this margin
            var (cw, _) = CoarsePathFinder.CoarseSize(grid, m);
            foreach (var widen in CoarsePathFinder.Widths)
            {
                var p = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m,
                                     GreedyWeightNum, GreedyWeightDen, CoarsePathFinder.Mask(grid, m, route, widen), cw);
                attempts++;
                if (p.Count > 0) { OnTrace?.Invoke($"win m={m} widen={widen} attempts={attempts} coarseMs={coarseMs}"); return SmoothLineOfSight(grid, p, m); }
            }
        }
        // ⛔⛔ CRITICAL FAILURE OF THE COARSE PATHFINDER ⛔⛔ Reaching here means every corridor, at every
        // margin, failed to produce a route the coarse level said existed. The unconstrained search below is
        // CORRECT but it is the thing that froze a bot for 16 minutes -- it is a safety net, NOT an outcome we
        // accept. Every occurrence is a bug in the coarse layer (block size, passability rule, or corridor
        // width) and must be investigated, not tolerated. It is logged loudly for exactly that reason.
        OnTrace?.Invoke($"FALLBACK attempts={attempts} coarseMs={coarseMs}");
        OnFallback?.Invoke($"⛔ PATHFIND FALLBACK (CRITICAL): coarse+corridor found nothing for ({startX},{startY})->({goalX},{goalY}); "
            + "running the UNCONSTRAINED search, which can block for minutes. This is a defect in the coarse layer.");
        return FindPath(grid, startX, startY, goalX, goalY, maxExpansions, margin);
    }


    public static IReadOnlyList<(uint X, uint Y)> FindPath(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions = 8_000_000, double margin = 2)
    {
        // Use the HIGHEST obstacle-inflation margin that yields a path, stepping DOWN only as needed (operator 2026-07-1…
        double[] steps = margin >= 2 ? new[] { 2.0, 1.5, 1.0, 0.5, 0.0 }
                        : margin > 0 ? new[] { margin, 0.0 }
                        : new[] { 0.0 };
        double used = 0;
        IReadOnlyList<(uint X, uint Y)> path = System.Array.Empty<(uint, uint)>();
        foreach (var m in steps)
        {
            path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m, GreedyWeightNum, GreedyWeightDen);
            if (path.Count > 0) { used = m; break; }
        }
        // Completeness fallback: the greedy heuristic is INADMISSIBLE, so on a route whose direct corridor is walled it…
        if (path.Count == 0)
            foreach (var m in steps)
            {
                path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m, 1, 1);
                if (path.Count > 0) { used = m; break; }
            }
        // Disc-swept line-of-sight smoothing at the margin we actually pathed with (see SmoothLineOfSight)
        return SmoothLineOfSight(grid, path, used);
    }

    private static IReadOnlyList<(uint X, uint Y)> FindPathCore(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions, double margin, int heurNum, int heurDen, bool[]? corridor = null, int corridorW = 0)
    {
        var (sx, sy) = grid.WorldToTile(startX, startY);
        var (gx, gy) = grid.WorldToTile(goalX, goalY);
        // Snap a blocked start/goal to the nearest walkable tile
        if (!grid.IsWalkableTile(sx, sy) && NearestWalkable(grid, sx, sy) is { } ns) (sx, sy) = ns;
        if (!grid.IsWalkableTile(gx, gy) && NearestWalkable(grid, gx, gy) is { } ng2) (gx, gy) = ng2;
        if (!grid.IsWalkableTile(sx, sy) || !grid.IsWalkableTile(gx, gy))
            return Array.Empty<(uint, uint)>();

        int W = grid.WidthTiles;
        int Id(int x, int y) => y * W + x;
        // A cell is passable if it satisfies the inflation margin, OR it lies within `margin` (Chebyshev) of the start/g…
        int esc = Math.Max(1, (int)Math.Ceiling(margin));
        bool NearEnd(int x, int y) =>
            (Math.Max(Math.Abs(x - sx), Math.Abs(y - sy)) <= esc ||
             Math.Max(Math.Abs(x - gx), Math.Abs(y - gy)) <= esc) && grid.IsWalkableTile(x, y);
        // A corridor restricts the fine search to the coarse route's neighbourhood. NearEnd still applies so a
        // start or goal sitting just outside the mask cannot make its own tile unreachable.
        bool InCorridor(int x, int y) => corridor is null
            || corridor[(y / CoarsePathFinder.Block) * corridorW + (x / CoarsePathFinder.Block)];
        bool Passable(int x, int y) => (grid.IsPathable(x, y, margin) && InCorridor(x, y)) || NearEnd(x, y);
        var came = new Dictionary<int, int>();
        var g = new Dictionary<int, int> { [Id(sx, sy)] = 0 };
        var open = new PriorityQueue<(int x, int y), int>();
        open.Enqueue((sx, sy), Heur(sx, sy, gx, gy, heurNum, heurDen));

        var expansions = 0;
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur.x == gx && cur.y == gy) return Reconstruct(grid, came, Id(gx, gy), W);
            if (++expansions > maxExpansions) break;
            int curG = g[Id(cur.x, cur.y)];

            foreach (var (dx, dy) in Neighbors)
            {
                int nx = cur.x + dx, ny = cur.y + dy;
                if (!Passable(nx, ny)) continue;
                if (dx != 0 && dy != 0 && // no cutting through a blocked/too-tight corner
                    (!Passable(cur.x + dx, cur.y) || !Passable(cur.x, cur.y + dy)))
                    continue;

                int step = (dx != 0 && dy != 0) ? 14 : 10; // ~10 ortho, ~14 diagonal
                int ng = curG + step;
                int nid = Id(nx, ny);
                if (g.TryGetValue(nid, out var prev) && ng >= prev) continue;
                g[nid] = ng;
                came[nid] = Id(cur.x, cur.y);
                open.Enqueue((nx, ny), ng + Heur(nx, ny, gx, gy, heurNum, heurDen));
            }
        }
        return Array.Empty<(uint, uint)>();
    }

    /// <summary>Spiral outward from a (blocked) tile to the nearest walkable tile, up to tiles</summary>
    private static (int x, int y)? NearestWalkable(BlockGrid grid, int tx, int ty, int maxRadius = 40)
    {
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
                    int nx = tx + dx, ny = ty + dy;
                    if (grid.IsWalkableTile(nx, ny)) return (nx, ny);
                }
        }
        return null;
    }

    /// <summary>Drop only TRULY collinear intermediate waypoints, keeping the start, every real corner, and the goal — so we i…</summary>
    public static IReadOnlyList<(uint X, uint Y)> Simplify(IReadOnlyList<(uint X, uint Y)> path)
    {
        if (path.Count <= 2) return path;
        var outp = new List<(uint X, uint Y)> { path[0] };
        for (int i = 1; i < path.Count - 1; i++)
        {
            var (ax, ay) = path[i - 1];
            var (bx, by) = path[i];
            var (cx, cy) = path[i + 1];
            // keep b unless a, b, c are exactly collinear (2D cross product of a→b and b→c is 0)
            long cross = ((long)bx - ax) * ((long)cy - by) - ((long)by - ay) * ((long)cx - bx);
            if (cross != 0) outp.Add(path[i]);
        }
        outp.Add(path[^1]);
        return outp;
    }

    /// <summary>Greedy line-of-sight smoothing where each candidate straight run is validated by — sweeping the player disc (r…</summary>
    private static IReadOnlyList<(uint X, uint Y)> SmoothLineOfSight(
        BlockGrid grid, IReadOnlyList<(uint X, uint Y)> path, double margin)
    {
        if (path.Count <= 2 || margin <= 0) return path;
        var sTile = grid.WorldToTile(path[0].X, path[0].Y);
        var gTile = grid.WorldToTile(path[^1].X, path[^1].Y);
        int esc = Math.Max(1, (int)Math.Ceiling(margin));
        bool Passable(int x, int y) => grid.IsPathable(x, y, margin) ||
            ((Math.Max(Math.Abs(x - sTile.X), Math.Abs(y - sTile.Y)) <= esc ||
              Math.Max(Math.Abs(x - gTile.X), Math.Abs(y - gTile.Y)) <= esc) && grid.IsWalkableTile(x, y));

        var outp = new List<(uint X, uint Y)> { path[0] };
        int anchor = 0;
        for (int i = 1; i < path.Count - 1; i++)
        {
            // Can the anchor still "see" the point after i with a clear disc-sweep?
            if (!SegmentDiscClear(grid, path[anchor], path[i + 1], Passable))
            {
                outp.Add(path[i]);
                anchor = i;
            }
        }
        outp.Add(path[^1]);
        return outp;
    }

    /// <summary>True if the player disc can sweep the straight world line a→b without touching a non- tile</summary>
    private static bool SegmentDiscClear(
        BlockGrid grid, (uint X, uint Y) a, (uint X, uint Y) b, Func<int, int, bool> passable)
    {
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y;
        double dist = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int steps = Math.Max(1, (int)Math.Ceiling(dist / (BlockGrid.WorldPerTile / 2)));
        int lastTx = int.MinValue, lastTy = int.MinValue;
        for (int k = 0; k <= steps; k++)
        {
            double t = (double)k / steps;
            var (tx, ty) = grid.WorldToTile((uint)(ax + (bx - ax) * t), (uint)(ay + (by - ay) * t));
            if (tx == lastTx && ty == lastTy) continue; // same tile as last sample — skip recheck
            lastTx = tx; lastTy = ty;
            if (!passable(tx, ty)) return false;
        }
        return true;
    }

    private static int Heur(int x, int y, int gx, int gy, int weightNum, int weightDen)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        int octile = 10 * (dx + dy) + (14 - 2 * 10) * Math.Min(dx, dy); // octile distance
        return octile * weightNum / weightDen; // weight 2.0x = greedy (fast); 1.0x = admissible (complete)
    }

    private static List<(uint, uint)> Reconstruct(BlockGrid grid, Dictionary<int, int> came, int goal, int W)
    {
        var tiles = new List<int> { goal };
        while (came.TryGetValue(tiles[^1], out var p)) tiles.Add(p);
        tiles.Reverse();
        var path = new List<(uint, uint)>(tiles.Count);
        foreach (var id in tiles) path.Add(grid.TileToWorld(id % W, id / W));
        return path;
    }
}
