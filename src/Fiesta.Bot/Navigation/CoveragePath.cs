using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// Generic "roomba" coverage-path generator over a map's <see cref="BlockGrid"/> walkability.
/// Produces an ORDERED list of world waypoints (a boustrophedon / lawn-mower serpentine snapped to
/// walkable ground) laid on a lattice of spacing <c>stepWorld</c>, so that a bot walking the
/// waypoints in order sweeps its whole walkable area — every walkable tile ends up within
/// <c>~stepWorld</c> of some waypoint. As the bot arrives at each point, any mob within the server's
/// AoI enters <c>nearbyMobs()</c> ⇒ the instance/KQ hoover picks it up.
///
/// <para>Pure geometry from BYO nav data (the <c>.shbd</c>) — no hardcoded ids/coords. Reusable for
/// the JCQ promotion instance (Job1_Dn01), KQs, and any other map where "clear everything" beats
/// "walk to a marker". The lattice step is a pure bot-behaviour knob (sweep density), like melee
/// range or tick cadence: smaller = slower but never misses; it only needs to be ≤ the server AoI.</para>
/// </summary>
public static class CoveragePath
{
    /// <param name="stepWorld">Lattice spacing in WORLD units (a walkable tile is
    /// <see cref="BlockGrid.WorldPerTile"/>=6.25 world). Must be ≤ the server's mob AoI radius for full
    /// coverage; a smaller value is always safe (just more waypoints).</param>
    /// <param name="margin">Keep waypoints this many tiles off walls (so the pathfinder can actually
    /// reach the point instead of stranding on an obstacle edge). 1 tile ≈ 6.25 world.</param>
    public static IReadOnlyList<(uint X, uint Y)> Compute(BlockGrid grid, double stepWorld, int margin = 1)
    {
        int W = grid.WidthTiles, H = grid.HeightTiles;
        // 1. Walkable bounding box — the playable region is a tiny island inside a large void, so
        //    restrict the lattice to it (a full-grid scan would place thousands of void waypoints).
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

        // 2. Serpentine over the lattice: rows top→bottom, alternating column direction each row so
        //    consecutive waypoints are adjacent ⇒ the A* leg between them is short (cheap + reliable).
        bool leftToRight = true;
        for (int ty = minY + step / 2; ty <= maxY; ty += step)
        {
            var cols = new List<int>();
            for (int tx = minX + step / 2; tx <= maxX; tx += step) cols.Add(tx);
            if (!leftToRight) cols.Reverse();
            foreach (int tx in cols)
            {
                // Snap the lattice point to the nearest walkable+clear tile within a step (so a cell
                // whose centre is void but which contains walkable ground still gets a waypoint).
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

    /// <summary>Spiral out from a tile to the nearest tile satisfying the inflation margin (falling
    /// back to plain walkable), up to <paramref name="maxRadius"/> tiles. Null if none — that lattice
    /// cell is all void.</summary>
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
