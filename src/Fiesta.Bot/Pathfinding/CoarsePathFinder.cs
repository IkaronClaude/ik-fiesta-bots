using System.Runtime.CompilerServices;

namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// TWO-LEVEL PATHFINDING. The .shbd resolution (6.25 world units per tile) is what we need to squeeze past a
/// lantern; it is absurd for deciding which side of the map to walk down. A full-resolution A* across a 2048x2048
/// map measured 9.3s locally and 976 SECONDS in-cluster (2026-08-18), blocking the lua tick the whole time.
///
/// So: route on a COARSE grid (one cell per <see cref="Block"/> tiles), then re-solve at full resolution inside a
/// corridor around that route. The corridor is a few coarse cells wide, so the fine search expands thousands of
/// tiles instead of millions. If the corridor yields nothing we widen it, and finally fall back to the
/// unconstrained search -- capping the cap is not on the table, because a path that exists must be found.
/// </summary>
public static class CoarsePathFinder
{
    /// <summary>Tiles per coarse cell. 16 puts a 2048-tile map at 128x128 -- thousands of cells, not millions.</summary>
    public const int Block = 16;

    /// <summary>How many coarse cells either side of the route the fine search may use, tried in order.</summary>
    private static readonly int[] CorridorWidths = { 1, 2, 4 };

    private sealed class Coarse
    {
        public required int W, H;
        public required bool[] Passable;   // a cell is passable if ANY tile in it is
        // AN EDGE MEANS A REAL CROSSING. "Both cells contain a walkable tile" is not connectivity: it routed
        // through cells that are 99% wall, the fine search could not cross, and 15 of 90 corpus cases fell back
        // to the unconstrained search -- 86% of all candidate time. An edge now requires two ADJACENT walkable
        // tiles straddling the shared border. Bit i corresponds to N8[i].
        public required byte[] Edges;
    }

    // Keyed by grid instance so a reloaded map rebuilds; keyed by margin bucket because passability differs.
    private static readonly ConditionalWeakTable<BlockGrid, Coarse[]> _cache = new();

    private static int Bucket(double margin) => margin <= 0 ? 0 : margin < 1 ? 1 : margin < 2 ? 2 : 3;

    private static Coarse Build(BlockGrid grid, double margin)
    {
        var all = _cache.GetValue(grid, _ => new Coarse[4]);
        var b = Bucket(margin);
        if (all[b] is { } hit) return hit;
        lock (all)
        {
            if (all[b] is { } hit2) return hit2;
            int cw = (grid.WidthTiles + Block - 1) / Block, ch = (grid.HeightTiles + Block - 1) / Block;
            var pass = new bool[cw * ch];
            for (int ty = 0; ty < grid.HeightTiles; ty++)
            {
                int cy = ty / Block, row = cy * cw;
                for (int tx = 0; tx < grid.WidthTiles; tx++)
                {
                    int i = row + tx / Block;
                    if (!pass[i] && grid.IsPathable(tx, ty, margin)) pass[i] = true;
                }
            }
            // Border crossings, orthogonal first; a diagonal needs both of its orthogonals so we never cut a corner
            // through a wall join.
            var edges = new byte[cw * ch];
            for (int cy = 0; cy < ch; cy++)
                for (int cx = 0; cx < cw; cx++)
                {
                    int ci = cy * cw + cx;
                    if (!pass[ci]) continue;
                    if (cx + 1 < cw && pass[ci + 1] && CrossesX(grid, margin, (cx + 1) * Block, cy)) edges[ci] |= 1 << 0;
                    if (cx - 1 >= 0 && pass[ci - 1] && CrossesX(grid, margin, cx * Block, cy)) edges[ci] |= 1 << 1;
                    if (cy + 1 < ch && pass[ci + cw] && CrossesY(grid, margin, (cy + 1) * Block, cx)) edges[ci] |= 1 << 2;
                    if (cy - 1 >= 0 && pass[ci - cw] && CrossesY(grid, margin, cy * Block, cx)) edges[ci] |= 1 << 3;
                }
            for (int i = 0; i < edges.Length; i++)
            {
                bool e = (edges[i] & 1) != 0, w = (edges[i] & 2) != 0, s = (edges[i] & 4) != 0, n2 = (edges[i] & 8) != 0;
                if (e && s) edges[i] |= 1 << 4;   // (1,1)
                if (e && n2) edges[i] |= 1 << 5;  // (1,-1)
                if (w && s) edges[i] |= 1 << 6;   // (-1,1)
                if (w && n2) edges[i] |= 1 << 7;  // (-1,-1)
            }
            return all[b] = new Coarse { W = cw, H = ch, Passable = pass, Edges = edges };
        }
    }

    /// <summary>Is there a walkable pair straddling the vertical border at tile column <paramref name="bx"/>?</summary>
    private static bool CrossesX(BlockGrid grid, double margin, int bx, int cy)
    {
        if (bx <= 0 || bx >= grid.WidthTiles) return false;
        int y0 = cy * Block, y1 = Math.Min(y0 + Block, grid.HeightTiles);
        for (int y = y0; y < y1; y++)
            if (grid.IsPathable(bx - 1, y, margin) && grid.IsPathable(bx, y, margin)) return true;
        return false;
    }

    /// <summary>Is there a walkable pair straddling the horizontal border at tile row <paramref name="by"/>?</summary>
    private static bool CrossesY(BlockGrid grid, double margin, int by, int cx)
    {
        if (by <= 0 || by >= grid.HeightTiles) return false;
        int x0 = cx * Block, x1 = Math.Min(x0 + Block, grid.WidthTiles);
        for (int x = x0; x < x1; x++)
            if (grid.IsPathable(x, by - 1, margin) && grid.IsPathable(x, by, margin)) return true;
        return false;
    }

    private static readonly (int dx, int dy)[] N8 =
        { (1,0), (-1,0), (0,1), (0,-1), (1,1), (1,-1), (-1,1), (-1,-1) };

    /// <summary>Coarse route as cell indices, or null when the coarse level says there is no way through.</summary>
    private static List<int>? CoarseRoute(Coarse c, int scx, int scy, int gcx, int gcy)
    {
        int W = c.W, H = c.H;
        if (!c.Passable[scy * W + scx] || !c.Passable[gcy * W + gcx]) return null;
        int start = scy * W + scx, goal = gcy * W + gcx;
        var came = new Dictionary<int, int>();
        var g = new Dictionary<int, int> { [start] = 0 };
        var open = new PriorityQueue<int, int>();
        open.Enqueue(start, 0);
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur == goal)
            {
                var outp = new List<int> { cur };
                var guard = 0;
                while (came.TryGetValue(outp[^1], out var p) && ++guard < W * H) outp.Add(p);
                outp.Reverse();
                return outp;
            }
            int cx = cur % W, cy = cur / W, cg = g[cur];
            foreach (var (dx, dy) in N8)
            {
                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                int ni = ny * W + nx;
                if (!c.Passable[ni]) continue;
                if ((c.Edges[cur] & (1 << Array.IndexOf(N8, (dx, dy)))) == 0) continue;   // no real crossing
                int step = (dx == 0 || dy == 0) ? 10 : 14;
                int ng = cg + step;
                if (g.TryGetValue(ni, out var old) && old <= ng) continue;
                g[ni] = ng; came[ni] = cur;
                int h = 10 * (Math.Abs(nx - gcx) + Math.Abs(ny - gcy));
                open.Enqueue(ni, ng + h);
            }
        }
        return null;
    }

    /// <summary>The coarse route for this margin, or null when the coarse level sees no way through.
    /// Solve it ONCE and dilate it into as many corridors as you need -- re-solving per width was 3.6x slower
    /// than the search it was meant to replace (measured against the corpus, 2026-08-18).</summary>
    public static List<int>? Route(BlockGrid grid, uint startX, uint startY, uint goalX, uint goalY, double margin)
    {
        var c = Build(grid, margin);
        var (stx, sty) = grid.WorldToTile(startX, startY);
        var (gtx, gty) = grid.WorldToTile(goalX, goalY);
        int scx = Math.Clamp(stx / Block, 0, c.W - 1), scy = Math.Clamp(sty / Block, 0, c.H - 1);
        int gcx = Math.Clamp(gtx / Block, 0, c.W - 1), gcy = Math.Clamp(gty / Block, 0, c.H - 1);
        return CoarseRoute(c, scx, scy, gcx, gcy);
    }

    /// <summary>Dilate a coarse route into the tile mask the fine search may expand within.</summary>
    public static bool[] Mask(BlockGrid grid, double margin, List<int> route, int widen)
    {
        var c = Build(grid, margin);
        var mask = new bool[c.W * c.H];
        foreach (var cell in route)
        {
            int cx = cell % c.W, cy = cell / c.W;
            for (int dy = -widen; dy <= widen; dy++)
                for (int dx = -widen; dx <= widen; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((uint)nx < (uint)c.W && (uint)ny < (uint)c.H) mask[ny * c.W + nx] = true;
                }
        }
        return mask;
    }

    /// <summary>Coarse dimensions for a grid at this margin (so callers can index a corridor mask).</summary>
    public static (int W, int H) CoarseSize(BlockGrid grid, double margin)
    {
        var c = Build(grid, margin);
        return (c.W, c.H);
    }

    public static IReadOnlyList<int> Widths => CorridorWidths;
}
