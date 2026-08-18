using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>Generic "roomba" coverage-path generator over a map's walkability</summary>
public static class CoveragePath
{
    /// <summary>Lattice spacing in WORLD units (a walkable tile is =6.25 world)</summary>
    public static IReadOnlyList<(uint X, uint Y)> Compute(BlockGrid grid, double stepWorld, int margin = 1)
    {
        int W = grid.WidthTiles, H = grid.HeightTiles;
        // 1. Walkable bounding box — the playable region is a tiny island inside a large void, so restrict the lattice t…
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int ty = 0; ty < H; ty++)
            for (int tx = 0; tx < W; tx++)
                if (grid.IsWalkableTile(tx, ty))
                {
                    if (tx < minX) minX = tx; if (tx > maxX) maxX = tx;
                    if (ty < minY) minY = ty; if (ty > maxY) maxY = ty;
                }
        if (maxX < minX) return Array.Empty<(uint, uint)>(); // no walkable ground at all

        int step = Math.Max(1, (int)Math.Round(stepWorld / BlockGrid.WorldPerTile));
        var pts = new List<(uint X, uint Y)>();
        var seen = new HashSet<long>();

        // 2. Serpentine over the lattice: rows top→bottom, alternating column direction each row so consecutive waypoint…
        bool leftToRight = true;
        for (int ty = minY + step / 2; ty <= maxY; ty += step)
        {
            var cols = new List<int>();
            for (int tx = minX + step / 2; tx <= maxX; tx += step) cols.Add(tx);
            if (!leftToRight) cols.Reverse();
            foreach (int tx in cols)
            {
                // Snap the lattice point to the nearest walkable+clear tile within a step (so a cell whose centre is void but wh…
                if (NearestPathable(grid, tx, ty, step, margin) is { } c)
                {
                    long id = (long)c.y * W + c.x;
                    if (seen.Add(id)) pts.Add(grid.TileToWorld(c.x, c.y));
                }
            }
            leftToRight = !leftToRight;
        }
        return pts;
    }

    /// <summary>Spiral out from a tile to the nearest tile satisfying the inflation margin (falling back to plain walkable), u…</summary>
    private static (int x, int y)? NearestPathable(BlockGrid grid, int tx, int ty, int maxRadius, int margin)
    {
        if (grid.IsPathable(tx, ty, margin)) return (tx, ty);
        (int x, int y)? walkableFallback = null;
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
                    int nx = tx + dx, ny = ty + dy;
                    if (grid.IsPathable(nx, ny, margin)) return (nx, ny);
                    if (walkableFallback is null && grid.IsWalkableTile(nx, ny)) walkableFallback = (nx, ny);
                }
        }
        return walkableFallback;
    }
}
