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

    public static IReadOnlyList<(uint X, uint Y)> FindPath(
        BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY, int maxExpansions = 200_000)
    {
        var (sx, sy) = grid.WorldToTile(startX, startY);
        var (gx, gy) = grid.WorldToTile(goalX, goalY);
        if (!grid.IsWalkableTile(sx, sy) || !grid.IsWalkableTile(gx, gy))
            return Array.Empty<(uint, uint)>();

        int W = grid.WidthTiles;
        int Id(int x, int y) => y * W + x;
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
                if (!grid.IsWalkableTile(nx, ny)) continue;
                if (dx != 0 && dy != 0 && // no cutting through a blocked corner
                    (!grid.IsWalkableTile(cur.x + dx, cur.y) || !grid.IsWalkableTile(cur.x, cur.y + dy)))
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

    /// <summary>Drop collinear intermediate waypoints, keeping only the start, the
    /// corners (direction changes), and the goal — so we issue one MoverunCmd per
    /// straight run instead of one per tile.</summary>
    public static IReadOnlyList<(uint X, uint Y)> Simplify(IReadOnlyList<(uint X, uint Y)> path)
    {
        if (path.Count <= 2) return path;
        var outp = new List<(uint X, uint Y)> { path[0] };
        for (int i = 1; i < path.Count - 1; i++)
        {
            var (ax, ay) = path[i - 1];
            var (bx, by) = path[i];
            var (cx, cy) = path[i + 1];
            // keep b if the direction a->b differs from b->c
            if (Math.Sign((long)bx - ax) != Math.Sign((long)cx - bx) ||
                Math.Sign((long)by - ay) != Math.Sign((long)cy - by))
                outp.Add(path[i]);
        }
        outp.Add(path[^1]);
        return outp;
    }

    private static int Heur(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        return 10 * (dx + dy) + (14 - 2 * 10) * Math.Min(dx, dy); // octile distance
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
