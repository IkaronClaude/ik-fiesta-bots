using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// Builds a WALL-HUGGING kite loop: the outer contour of the room we are standing in, with narrow
/// corridors removed, as an ordered ring of world waypoints.
///
/// <para><b>Why this replaced the inscribed circle</b> (operator 2026-08-13). A circle is capped by the
/// room's SHORT axis, and worse, at radius ~= the chaser's range it is actively pointless: <i>"range =
/// radius means we just circle but the enemy never has to move => no effect from kiting"</i> — the chaser
/// parks in the middle and covers the whole rim. Measured, the JCQ clone room holds a largest circle of
/// r=524u while the mage needed 562u, so it fitted 0 of 15 attempts. Hugging the wall instead uses the
/// room's full extent and keeps the greatest possible separation from anything inside it.</para>
///
/// <para><b>Corridors are skipped by construction</b>, per the operator's "detection of narrow corridors
/// that attach to it and skipping those": every cell whose clearance is below
/// <paramref name="corridorMinWidth"/>/2 is deleted BEFORE the contour is traced, so a side passage is
/// simply not part of the shape. That also removes the hard turn a corridor would force — the thing that
/// makes linear kiting deadly ("during this turn you will be in enemy range for like 10 secs").</para>
///
/// <para>Pure BYO nav geometry from the <c>.shbd</c>, like CoveragePath — no ids, no coordinates.</para>
/// </summary>
public static class KiteLoop
{
    /// <param name="corridorMinWidth">Passages narrower than this (world units) are not part of the loop.</param>
    /// <param name="marginWorld">Keep the path this far off the wall, so the server's own collision and the
    /// pathfinder both have room; hugging the exact boundary tile strands the bot on obstacle edges.</param>
    /// <param name="maxSpanWorld">Only look this far around the bot — bounds the search on a 4096² grid.</param>
    /// <param name="maxPoints">Contour points are decimated to about this many waypoints.</param>
    /// <returns>An ordered ring of world waypoints (last connects back to first), or an empty list when the
    /// room will not yield one.</returns>
    public static IReadOnlyList<(uint X, uint Y)> Fit(
        BlockGrid grid, double px, double py,
        double corridorMinWidth = 260, double marginWorld = 40,
        double maxSpanWorld = 5000, int maxPoints = 48)
    {
        var W = grid.WidthTiles;
        var H = grid.HeightTiles;
        var (btx, bty) = grid.WorldToTile((uint)Math.Max(0, px), (uint)Math.Max(0, py));
        if (btx < 0 || bty < 0 || btx >= W || bty >= H) return [];

        // Bounded window: a full-grid scan of a 4096² map would be 16M cells for no benefit.
        var span = (int)Math.Ceiling(maxSpanWorld / BlockGrid.WorldPerTile / 2);
        int x0 = Math.Max(0, btx - span), y0 = Math.Max(0, bty - span);
        int x1 = Math.Min(W - 1, btx + span), y1 = Math.Min(H - 1, bty + span);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w < 8 || h < 8) return [];

        bool Walk(int lx, int ly) => grid.IsWalkableTile(x0 + lx, y0 + ly);

        // 1. CLEARANCE of every walkable cell — BFS outward from all blocked cells at once, so each cell
        //    ends up holding its distance (in tiles) to the nearest wall. This is what lets a corridor be
        //    recognised as "narrow" without any notion of what a corridor looks like.
        var clear = new int[w * h];
        var q = new Queue<int>();
        for (var i = 0; i < w * h; i++)
        {
            var lx = i % w; var ly = i / w;
            var open = Walk(lx, ly);
            // Treat the window edge as wall, so a room running off the window cannot report false clearance.
            var edge = lx == 0 || ly == 0 || lx == w - 1 || ly == h - 1;
            if (!open || edge) { clear[i] = 0; q.Enqueue(i); }
            else clear[i] = int.MaxValue;
        }
        while (q.Count > 0)
        {
            var i = q.Dequeue();
            int cx = i % w, cy = i / w;
            for (var d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var ni = ny * w + nx;
                if (clear[ni] > clear[i] + 1) { clear[ni] = clear[i] + 1; q.Enqueue(ni); }
            }
        }

        // 2. KEEP only cells wide enough to be "room" rather than "corridor". Half the required width,
        //    because clearance is measured from the centre outward to the nearest wall.
        var minClear = Math.Max(2, (int)Math.Round(corridorMinWidth / BlockGrid.WorldPerTile / 2));
        var keep = new bool[w * h];
        for (var i = 0; i < w * h; i++) keep[i] = clear[i] >= minClear;

        // 3. The component WE are in. If the bot stands in a corridor (or against a wall) its own cell may
        //    have been dropped, so start from the nearest kept cell instead of giving up.
        var start = NearestKept(keep, w, h, btx - x0, bty - y0);
        if (start < 0) return [];
        var comp = new bool[w * h];
        var stack = new Stack<int>();
        stack.Push(start); comp[start] = true;
        var count = 1;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            int cx = i % w, cy = i / w;
            for (var d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var ni = ny * w + nx;
                if (keep[ni] && !comp[ni]) { comp[ni] = true; count++; stack.Push(ni); }
            }
        }
        if (count < 64) return [];      // too small to be a room worth circling

        // 4. INSET from the wall, then take the boundary of what remains: the boundary of the inset shape
        //    IS the wall-hugging path, already margin off the wall.
        var inset = Math.Max(1, (int)Math.Round(marginWorld / BlockGrid.WorldPerTile));
        var body = new bool[w * h];
        for (var i = 0; i < w * h; i++) body[i] = comp[i] && clear[i] >= minClear + inset;
        if (!body.Any(b => b)) body = comp;      // room too tight to inset — hug the raw contour instead

        // ⛔ RE-TAKE THE COMPONENT AFTER INSETTING. The inset can SPLIT the room into several blobs, and
        // the contour trace starts at the first filled cell in raster order — which is then some stray
        // fragment, not the room we are standing in. Measured: this produced a 5-waypoint loop spanning
        // 6x6 world units, i.e. a single tile, in a room hundreds of units across.
        body = ComponentContaining(body, w, h, btx - x0, bty - y0);
        if (body is null) return [];

        var ring = TraceOuterContour(body, w, h);
        if (ring.Count < 8) return [];

        // 5. Decimate to a walkable number of waypoints, preserving order.
        var stepN = Math.Max(1, ring.Count / Math.Max(8, maxPoints));
        var outp = new List<(uint, uint)>();
        for (var i = 0; i < ring.Count; i += stepN)
        {
            var (lx, ly) = ring[i];
            var (wx, wy) = grid.TileToWorld(x0 + lx, y0 + ly);
            outp.Add((wx, wy));
        }
        return outp;
    }

    /// <summary>The connected blob of <paramref name="mask"/> containing (or nearest to) the bot, with
    /// everything else cleared — so a contour trace cannot wander off onto an unrelated fragment.</summary>
    private static bool[]? ComponentContaining(bool[] mask, int w, int h, int sx, int sy)
    {
        var seed = NearestKept(mask, w, h, sx, sy);
        if (seed < 0) return null;
        var outp = new bool[w * h];
        var st = new Stack<int>();
        st.Push(seed); outp[seed] = true;
        var n = 1;
        while (st.Count > 0)
        {
            var i = st.Pop();
            int cx = i % w, cy = i / w;
            for (var d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var ni = ny * w + nx;
                if (mask[ni] && !outp[ni]) { outp[ni] = true; n++; st.Push(ni); }
            }
        }
        return n >= 64 ? outp : null;
    }

    private static int NearestKept(bool[] keep, int w, int h, int sx, int sy)
    {
        if (sx >= 0 && sy >= 0 && sx < w && sy < h && keep[sy * w + sx]) return sy * w + sx;
        for (var r = 1; r < Math.Max(w, h); r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;   // ring only
                    int nx = sx + dx, ny = sy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (keep[ny * w + nx]) return ny * w + nx;
                }
        return -1;
    }

    /// <summary>Moore-neighbourhood boundary trace of the shape's OUTER contour, returned in walk order.</summary>
    private static List<(int X, int Y)> TraceOuterContour(bool[] body, int w, int h)
    {
        // Topmost-leftmost filled cell is guaranteed to be on the outer contour.
        var startIdx = -1;
        for (var i = 0; i < body.Length && startIdx < 0; i++) if (body[i]) startIdx = i;
        var res = new List<(int, int)>();
        if (startIdx < 0) return res;

        int sx = startIdx % w, sy = startIdx / w;
        // 8-neighbourhood, clockwise from "west" so the first probe of a top-left start is outside.
        int[] dx = [-1, -1, 0, 1, 1, 1, 0, -1];
        int[] dy = [0, -1, -1, -1, 0, 1, 1, 1];
        // ⛔ START POINTING SO THE FIRST BACKTRACK PROBE IS *WEST*. The start cell is topmost-leftmost, so
        // west is guaranteed to be outside the shape. With dir=0 the probe starts north-west instead and
        // the trace closes on itself almost immediately — measured, it produced a 5-cell "loop" spanning
        // one tile in a room 1400 units across.
        int cx = sx, cy = sy, dir = 3;
        var guard = w * h * 4;
        do
        {
            res.Add((cx, cy));
            // Turn back toward where we came from, then sweep clockwise for the next filled neighbour.
            var found = false;
            var back = (dir + 5) % 8;
            for (var k = 0; k < 8; k++)
            {
                var nd = (back + k) % 8;
                int nx = cx + dx[nd], ny = cy + dy[nd];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (!body[ny * w + nx]) continue;
                cx = nx; cy = ny; dir = nd; found = true; break;
            }
            if (!found) break;                       // isolated cell
        } while ((cx != sx || cy != sy) && res.Count < guard);
        return res;
    }
}
