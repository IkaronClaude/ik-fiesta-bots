namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// A* over a <see cref="BlockGrid"/> (8-directional, no corner-cutting through
/// blocked diagonals). Returns a list of world waypoints (tile centres) from the
/// start tile to the goal tile, ready to feed as MoverunCmd steps. Empty if no
/// path (or start/goal unwalkable).
/// </summary>
public static class PathFinder
{
    private static readonly (int dx, int dy)[] Neighbors =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    // A modest heuristic weight makes A* greedier — it explores roughly a corridor toward the
    // goal instead of a full ellipse, cutting expansions by orders of magnitude on the large,
    // mostly-open field maps. Paths become slightly suboptimal (fine for a bot) but LONG paths
    // now finish within budget instead of hitting the cap and falsely reporting "no path" (the
    // bug that made fully-connected maps like RouCos02 look divided). Bumped 1.5x->2.0x
    // (2026-06-30): the full cross-map RouCos03 route to the Forest-of-Mist gate needed >10M
    // expansions at 1.5x but resolves under ~6M at 2.0x (see the raised FindPath maxExpansions).
    private const int HeurWeightNum = 2, HeurWeightDen = 1; // 2.0x

    /// <param name="margin">Obstacle-inflation border in tiles (P0 2026-06-30): the interior of
    /// the path stays this many tiles clear of any blocked tile so straight runs don't clip an
    /// object corner (→ MOVEFAIL). The START and GOAL cells (and a small pocket around each) are
    /// EXEMPT so a bot/target legitimately standing next to a wall isn't stranded. A tile is
    /// ~6.25 world units, so margin 2 ≈ 12.5 units ≈ 20 cm — safe for narrow gates.</param>
    public static IReadOnlyList<(uint X, uint Y)> FindPath(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions = 8_000_000, int margin = 2)
    {
        var path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, margin);
        // Never regress below the old (no-margin) behaviour: if inflation over-constrains a
        // genuinely narrow route to "no path", retry once tight so the bot still gets a route
        // (a slightly-clipping path beats being stranded). Only the inflated attempt is skipped.
        if (path.Count == 0 && margin > 0)
            path = FindPathCore(grid, startX, startY, goalX, goalY, maxExpansions, 0);
        // Disc-swept line-of-sight smoothing: the emitted straight runs (one MOVERUN each) are
        // validated by sweeping the player disc (radius = margin tiles) along the line, not just
        // testing the centre — so a run never clips an obstacle corner it passes near. This also
        // opportunistically shortens the path (fewer, longer clear runs). See SmoothLineOfSight.
        return SmoothLineOfSight(grid, path, margin);
    }

    private static IReadOnlyList<(uint X, uint Y)> FindPathCore(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY,
        int maxExpansions, int margin)
    {
        var (sx, sy) = grid.WorldToTile(startX, startY);
        var (gx, gy) = grid.WorldToTile(goalX, goalY);
        // Snap a blocked start/goal to the nearest walkable tile. Mob-spawn CENTRES (from
        // MobCoordinate) often land on a blocked tile (decoration/edge), which would otherwise
        // make walkTo reject the whole trip and the grind loop freeze. Snapping lets the bot
        // path to the walkable edge of the spawn area and pick the mob up from there.
        if (!grid.IsWalkableTile(sx, sy) && NearestWalkable(grid, sx, sy) is { } ns) (sx, sy) = ns;
        if (!grid.IsWalkableTile(gx, gy) && NearestWalkable(grid, gx, gy) is { } ng2) (gx, gy) = ng2;
        if (!grid.IsWalkableTile(sx, sy) || !grid.IsWalkableTile(gx, gy))
            return Array.Empty<(uint, uint)>();

        int W = grid.WidthTiles;
        int Id(int x, int y) => y * W + x;
        // A cell is passable if it satisfies the inflation margin, OR it lies within `margin`
        // (Chebyshev) of the start/goal tile — the exemption that lets the path leave a tight
        // start pocket and approach a goal that legitimately hugs an obstacle. Never passes
        // through a genuinely blocked tile (IsPathable(...,0) == IsWalkableTile).
        int esc = Math.Max(1, margin);
        bool NearEnd(int x, int y) =>
            (Math.Max(Math.Abs(x - sx), Math.Abs(y - sy)) <= esc ||
             Math.Max(Math.Abs(x - gx), Math.Abs(y - gy)) <= esc) && grid.IsWalkableTile(x, y);
        bool Passable(int x, int y) => grid.IsPathable(x, y, margin) || NearEnd(x, y);
        var came = new Dictionary<int, int>();
        var g = new Dictionary<int, int> { [Id(sx, sy)] = 0 };
        var open = new PriorityQueue<(int x, int y), int>();
        open.Enqueue((sx, sy), Heur(sx, sy, gx, gy));

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
                open.Enqueue((nx, ny), ng + Heur(nx, ny, gx, gy));
            }
        }
        return Array.Empty<(uint, uint)>();
    }

    /// <summary>Spiral outward from a (blocked) tile to the nearest walkable tile, up to
    /// <paramref name="maxRadius"/> tiles. Null if none found (the point is deep inside a
    /// large obstacle). Used to snap a blocked start/goal onto walkable ground.</summary>
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

    /// <summary>Drop only TRULY collinear intermediate waypoints, keeping the start, every real
    /// corner, and the goal — so we issue one MoverunCmd per straight run instead of one per tile.
    /// Uses an exact collinearity (cross-product) test: a point is dropped only when a→b→c lie on
    /// one line. (A sign-of-direction test is WRONG for the arbitrary-angle corners that
    /// <see cref="SmoothLineOfSight"/> now emits — it would merge two differently-sloped runs that
    /// merely share a direction sign into a single diagonal that cuts through an obstacle.)</summary>
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

    /// <summary>Greedy line-of-sight smoothing where each candidate straight run is validated
    /// by <see cref="SegmentDiscClear"/> — sweeping the player disc (radius = <paramref name="margin"/>
    /// tiles) along the line rather than testing only its centre. Keeps a waypoint only when the
    /// disc-swept line from the current anchor to the NEXT point would clip an obstacle; otherwise
    /// the point is skipped, collapsing many tiles into one clean MOVERUN. The START and GOAL tiles
    /// (and an <c>esc</c>-tile pocket around each) are margin-exempt so a run that legitimately hugs
    /// a wall at either end is still allowed (matches the pathfinder's own exemption).</summary>
    private static IReadOnlyList<(uint X, uint Y)> SmoothLineOfSight(
        BlockGrid grid, IReadOnlyList<(uint X, uint Y)> path, int margin)
    {
        if (path.Count <= 2 || margin <= 0) return path;
        var sTile = grid.WorldToTile(path[0].X, path[0].Y);
        var gTile = grid.WorldToTile(path[^1].X, path[^1].Y);
        int esc = Math.Max(1, margin);
        bool Passable(int x, int y) => grid.IsPathable(x, y, margin) ||
            ((Math.Max(Math.Abs(x - sTile.X), Math.Abs(y - sTile.Y)) <= esc ||
              Math.Max(Math.Abs(x - gTile.X), Math.Abs(y - gTile.Y)) <= esc) && grid.IsWalkableTile(x, y));

        var outp = new List<(uint X, uint Y)> { path[0] };
        int anchor = 0;
        for (int i = 1; i < path.Count - 1; i++)
        {
            // Can the anchor still "see" the point after i with a clear disc-sweep? If not, we
            // must commit point i as a corner and re-anchor there.
            if (!SegmentDiscClear(grid, path[anchor], path[i + 1], Passable))
            {
                outp.Add(path[i]);
                anchor = i;
            }
        }
        outp.Add(path[^1]);
        return outp;
    }

    /// <summary>True if the player disc can sweep the straight world line a→b without touching a
    /// non-<paramref name="passable"/> tile. Samples at ~half-tile spacing; each sampled tile's
    /// <c>Passable</c> already encodes the disc (IsPathable(margin) = every tile within margin is
    /// walkable), so testing the centre samples effectively sweeps a disc of that radius.</summary>
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

    private static int Heur(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        int octile = 10 * (dx + dy) + (14 - 2 * 10) * Math.Min(dx, dy); // octile distance
        return octile * HeurWeightNum / HeurWeightDen; // weighted (greedier) — see FindPath
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
