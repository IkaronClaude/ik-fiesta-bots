namespace Fiesta.Bot.Pathfinding;

/// <summary>A* over a (8-directional, no corner-cutting through blocked diagonals)</summary>
public static class PathFinder
{
    private static readonly (int dx, int dy)[] Neighbors =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    // A modest heuristic weight makes A* greedier — it explores roughly a corridor toward the goal instead of a full…
    /// <summary>Obstacle-inflation margin, in tiles. ZERO: the clearance band is gone.
    ///
    /// It was added 2026-06-30 because "paths hugged obstacle edges -> the straight-run MOVERUN between waypoints"
    /// failed. Three weeks later, on 2026-07-22, the actual cause of those failures was found -- ShbdTileShift, the
    /// .shbd blocked-bit at index (i,j) representing the world cell one tile over in EACH axis -- and the workaround
    /// was never removed.
    ///
    /// Measured 2026-08-20, which is what settles it: the search was not even enforcing its own margin. Validated in
    /// world space at ~6 samples per tile with no rounding, A* paths entered the clearance band on 62/66, 41/66 and
    /// 32/66 routes on EldGbl02, Job1_Dn01 and Eld -- and crossed an actual WALL zero times, with the longest
    /// excursion 3 world units. The fleet has been walking those paths for weeks.
    ///
    /// So the band cost a five-rung margin ladder (run twice, greedy then admissible), a whole-map clearance
    /// transform, and the island/navmesh disagreement -- while not actually being applied. One constant, one line
    /// to put back if live movement ever disagrees.</summary>
    public const double DefaultMargin = 0;

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
        int maxExpansions = 8_000_000, double margin = DefaultMargin)
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
        int maxExpansions = 8_000_000, double margin = DefaultMargin, CancellationToken ct = default)
    {
        // Use the HIGHEST obstacle-inflation margin that yields a path, stepping DOWN only as needed (operator 2026-07-1…
        double[] steps = margin >= 2 ? new[] { 2.0, 1.5, 1.0, 0.5, 0.0 }
                        : margin > 0 ? new[] { margin, 0.0 }
                        : new[] { 0.0 };
        double used = 0;
        IReadOnlyList<(uint X, uint Y)> path = System.Array.Empty<(uint, uint)>();
        foreach (var m in steps)
        {
            path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m, GreedyWeightNum, GreedyWeightDen, ct: ct);
            if (path.Count > 0) { used = m; break; }
        }
        // Completeness fallback: the greedy heuristic is INADMISSIBLE, so on a route whose direct corridor is walled it…
        if (path.Count == 0)
            foreach (var m in steps)
            {
                path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m, 1, 1, ct: ct);
                if (path.Count > 0) { used = m; break; }
            }
        // Disc-swept line-of-sight smoothing at the margin we actually pathed with (see SmoothLineOfSight)
        return SmoothLineOfSight(grid, path, used);
    }

    /// <summary>Uniform-cost search: A* with the heuristic switched off entirely (weight 0).
    ///
    /// Worth having as a real option rather than a curiosity. The default heuristic here is deliberately
    /// INADMISSIBLE (octile x2.0), which is fast on open ground but is exactly what makes the search explode on
    /// certain starts -- it drives expansion confidently into a dead area and then has to unwind it, re-expanding
    /// closed nodes on the way. That is why the same route measured 1ms one way and 2292ms reversed.
    ///
    /// Dijkstra cannot be misled, because it has no opinion about direction. It expands strictly by distance from
    /// the start, so its cost depends on how much of the map lies within the goal's radius and NOT on which end
    /// you begin at -- which should make it both symmetric and predictable, at the price of exploring more of the
    /// map on an easy route where the heuristic would have gone straight there.
    ///
    /// One structural saving comes free: FindPath runs its whole margin ladder with the greedy heuristic and then,
    /// if that found nothing, runs the ENTIRE ladder again admissibly, because an inadmissible heuristic can miss
    /// a path that exists. A zero heuristic is admissible, so there is nothing to fall back to -- one ladder, not
    /// potentially two.</summary>
    public static IReadOnlyList<(uint X, uint Y)> FindPathDijkstra(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions = 8_000_000, double margin = DefaultMargin, CancellationToken ct = default)
    {
        double[] steps = margin >= 2 ? new[] { 2.0, 1.5, 1.0, 0.5, 0.0 }
                        : margin > 0 ? new[] { margin, 0.0 }
                        : new[] { 0.0 };
        foreach (var m in steps)
        {
            var p = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, m, 0, 1, ct: ct);
            if (p.Count > 0) return SmoothLineOfSight(grid, p, m);
        }
        return Array.Empty<(uint, uint)>();
    }

    /// <summary>Search FORWARD and BACKWARD at the same time and keep whichever finishes first.
    ///
    /// WHY: the cost of a search is dominated by WHICH END IT STARTS FROM, not by the distance. Measured on
    /// EldGbl02 over random reachable pairs: (8340,9215)->(5828,9915) takes 1ms and the identical route reversed
    /// takes 2292ms; (6209,6578)->(3428,3728) is 991ms one way and 57ms the other. The worst start random sampling
    /// found cost ~10.5 SECONDS to every destination tried, and 388ms from the far end -- and it was not walled in,
    /// it reaches a near neighbour in 0ms. The greedy heuristic simply leads the search into a large dead area from
    /// one end and not from the other.
    ///
    /// Nothing about the two endpoints predicts which end is the bad one, so do not try to choose: run both and
    /// take the winner. The grid is undirected, so the loser's answer would have been the same path reversed.
    ///
    /// This is a RACE, not a meet-in-the-middle bidirectional A*. A true bidirectional search would be better
    /// still, but the heuristic here is deliberately inadmissible (GreedyWeight) and meeting two inadmissible
    /// frontiers correctly is a real piece of work; racing needs no new pathfinder and cannot change which paths
    /// are found -- only how fast one of them arrives.
    ///
    /// An EMPTY result never wins. A direction that finds nothing is not evidence there is nothing to find (it may
    /// have been cancelled, or hit its budget), so we wait for the other one before reporting failure.</summary>
    public static IReadOnlyList<(uint X, uint Y)> FindPathBidirectional(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions = 8_000_000, double margin = DefaultMargin)
    {
        // Three searches advanced IN LOCKSTEP on this thread -- a slice of each in turn, first to finish wins.
        //
        // WHY THESE THREE. Search cost is dominated by which end you start from and by whether the heuristic is
        // lying to you, and neither is predictable from the endpoints:
        //   * A* forward and A* backward differ wildly on the SAME route -- measured 1ms against 2292ms, and a
        //     start that cost ~10.5s to every destination tried cost 388ms from the far end.
        //   * A* and Dijkstra differ by map shape. Over 455 pairs: Eld favours A* 37x (356ms vs 13,204ms, open
        //     ground where the heuristic walks straight there), EldGbl02 favours Dijkstra 5x (wide lobes joined by
        //     narrow necks, where a greedy heuristic charges into a lobe and must unwind it to find the neck).
        // Racing two A*s could never fix the tail, because both inherit the same lie: worst case stayed 9,730ms
        // against A*'s 10,374ms. Dijkstra caps it at 1,676ms because it has no opinion to be wrong about.
        //
        // WHY LOCKSTEP AND NOT THREADS. Threads cost scheduler pressure and cancellation plumbing, and burn k cores
        // to produce one answer. Interleaving costs one thread and bounds the answer at k x the winner's work,
        // deterministically. Nothing here is CPU-bound enough to want a core each.
        //
        // One direction of Dijkstra is enough: with no heuristic there is no direction to be wrong about.
        // LADDER THE RACE, don't fall back to a single algorithm.
        // The first version raced only at the top margin and, when all three failed there, called FindPath() --
        // the full sequential A* ladder. So the worst case was A*'s worst case (measured 10,185ms) even with
        // Dijkstra in the race capping at 1,735ms: Dijkstra never got to run at the margin that had the answer.
        // Dropping the inflation margin IS the completeness path, so run the race at each rung instead. Dijkstra
        // at margin 0 is admissible and complete, so there is nothing left to fall back to.
        double[] steps = margin >= 2 ? new[] { 2.0, 1.5, 1.0, 0.5, 0.0 }
                        : margin > 0 ? new[] { margin, 0.0 }
                        : new[] { 0.0 };
        foreach (var m in steps)
        {
            var racers = new[]
            {
                new Stepper(grid, startX, startY, goalX, goalY, m, GreedyWeightNum, GreedyWeightDen, false),
                new Stepper(grid, goalX, goalY, startX, startY, m, GreedyWeightNum, GreedyWeightDen, true),
                new Stepper(grid, startX, startY, goalX, goalY, m, 0, 1, false),
            };
            // Slice size trades interleave overhead against overshoot: too small and we pay the loop, too large and
            // a finished racer waits while a doomed one burns its slice.
            const int Slice = 4096;
            int budgetEach = Math.Max(Slice, maxExpansions / racers.Length);
            bool anyAlive = true;
            while (anyAlive)
            {
                anyAlive = false;
                foreach (var r in racers)
                {
                    if (r.Finished) continue;
                    r.Step(Slice, budgetEach);
                    if (r.Finished)
                    {
                        // An empty result never wins: a racer that exhausted its budget has proved nothing, so let
                        // the others keep going. Only a real path ends the race.
                        if (r.Result.Count > 0)
                            return r.Reversed ? Flip(SmoothLineOfSight(grid, r.Result, m))
                                              : SmoothLineOfSight(grid, r.Result, m);
                    }
                    else anyAlive = true;
                }
            }
        }
        return Array.Empty<(uint, uint)>();
    }

    private static IReadOnlyList<(uint X, uint Y)> Flip(IReadOnlyList<(uint X, uint Y)> p)
    {
        var f = new (uint X, uint Y)[p.Count];
        for (int i = 0; i < p.Count; i++) f[i] = p[p.Count - 1 - i];
        return f;
    }

    /// <summary>One resumable A*/Dijkstra search. Holds the frontier between slices so lockstep interleaving costs
    /// nothing but the loop -- restarting each racer per round would throw away exactly the work that makes the
    /// next round cheaper.</summary>
    private sealed class Stepper
    {
        private readonly BlockGrid _grid;
        private readonly int _sx, _sy, _gx, _gy, _w, _esc, _hn, _hd;
        private readonly double _margin;
        private readonly Dictionary<int, int> _came = new();
        private readonly Dictionary<int, int> _g = new();
        private readonly PriorityQueue<(int x, int y), int> _open = new();
        private int _expansions;

        public bool Finished { get; private set; }
        public bool Reversed { get; }
        public IReadOnlyList<(uint X, uint Y)> Result { get; private set; } = Array.Empty<(uint, uint)>();

        public Stepper(BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
                       double margin, int heurNum, int heurDen, bool reversed)
        {
            _grid = grid; _margin = margin; _hn = heurNum; _hd = heurDen; Reversed = reversed;
            _w = grid.WidthTiles;
            var (sx, sy) = grid.WorldToTile(startX, startY);
            var (gx, gy) = grid.WorldToTile(goalX, goalY);
            if (!grid.IsWalkableTile(sx, sy) && NearestWalkable(grid, sx, sy) is { } ns) (sx, sy) = ns;
            if (!grid.IsWalkableTile(gx, gy) && NearestWalkable(grid, gx, gy) is { } ng) (gx, gy) = ng;
            _sx = sx; _sy = sy; _gx = gx; _gy = gy;
            _esc = Math.Max(1, (int)Math.Ceiling(margin));
            if (!grid.IsWalkableTile(sx, sy) || !grid.IsWalkableTile(gx, gy)) { Finished = true; return; }
            _g[sy * _w + sx] = 0;
            _open.Enqueue((sx, sy), Heur(sx, sy, gx, gy, heurNum, heurDen));
        }

        private bool NearEnd(int x, int y) =>
            (Math.Max(Math.Abs(x - _sx), Math.Abs(y - _sy)) <= _esc ||
             Math.Max(Math.Abs(x - _gx), Math.Abs(y - _gy)) <= _esc) && _grid.IsWalkableTile(x, y);
        private bool Passable(int x, int y) => _grid.IsPathable(x, y, _margin) || NearEnd(x, y);

        /// <summary>Advance at most <paramref name="slice"/> expansions. Sets Finished when the goal is reached or
        /// the frontier is exhausted or this racer's share of the budget is spent.</summary>
        public void Step(int slice, int budget)
        {
            int done = 0;
            while (done < slice && _open.TryDequeue(out var cur, out _))
            {
                if (cur.x == _gx && cur.y == _gy)
                {
                    Result = Reconstruct(_grid, _came, _gy * _w + _gx, _w);
                    Finished = true;
                    return;
                }
                if (++_expansions > budget) { Finished = true; return; }
                done++;
                int curG = _g[cur.y * _w + cur.x];
                foreach (var (dx, dy) in Neighbors)
                {
                    int nx = cur.x + dx, ny = cur.y + dy;
                    if (!Passable(nx, ny)) continue;
                    if (dx != 0 && dy != 0 &&
                        (!Passable(cur.x + dx, cur.y) || !Passable(cur.x, cur.y + dy))) continue;
                    int ng2 = curG + ((dx != 0 && dy != 0) ? 14 : 10);
                    int nid = ny * _w + nx;
                    if (_g.TryGetValue(nid, out var prev) && ng2 >= prev) continue;
                    _g[nid] = ng2;
                    _came[nid] = cur.y * _w + cur.x;
                    _open.Enqueue((nx, ny), ng2 + Heur(nx, ny, _gx, _gy, _hn, _hd));
                }
            }
            if (_open.Count == 0) Finished = true;
        }
    }

    private static IReadOnlyList<(uint X, uint Y)> FindPathCore(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions, double margin, int heurNum, int heurDen, bool[]? corridor = null, int corridorW = 0,
        CancellationToken ct = default)
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
            // Checked rarely enough to be free (once per 4096 expansions) and often enough that the losing half of a
            // raced pair stops within milliseconds instead of running its full budget on another core.
            if ((expansions & 0xFFF) == 0 && ct.IsCancellationRequested) break;
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
