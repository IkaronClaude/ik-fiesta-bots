using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

public static class KiteLoop
{
    /// <summary>Passages narrower than this (world units) are not part of the loop</summary>
    public static IReadOnlyList<(uint X, uint Y)> Fit(
        BlockGrid grid, double px, double py,
        double corridorMinWidth = 260, double marginWorld = 40,
        double maxSpanWorld = 5000, int maxPoints = 48)
    {
        var W = grid.WidthTiles;
        var H = grid.HeightTiles;
        var (btx, bty) = grid.WorldToTile((uint)Math.Max(0, px), (uint)Math.Max(0, py));
        if (btx < 0 || bty < 0 || btx >= W || bty >= H) return [];

        // Bounded window: a full-grid scan of a 4096² map would be 16M cells for no benefit
        var span = (int)Math.Ceiling(maxSpanWorld / BlockGrid.WorldPerTile / 2);
        int x0 = Math.Max(0, btx - span), y0 = Math.Max(0, bty - span);
        int x1 = Math.Min(W - 1, btx + span), y1 = Math.Min(H - 1, bty + span);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w < 8 || h < 8) return [];

        bool Walk(int lx, int ly) => grid.IsWalkableTile(x0 + lx, y0 + ly);

        // 1. CLEARANCE of every walkable cell — BFS outward from all blocked cells at once, so each cell ends up holding…
        var clear = new int[w * h];
        var q = new Queue<int>();
        for (var i = 0; i < w * h; i++)
        {
            var lx = i % w; var ly = i / w;
            var open = Walk(lx, ly);
            // Treat the window edge as wall, so a room running off the window cannot report false clearance
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

        var minClear = Math.Max(2, (int)Math.Round(corridorMinWidth / BlockGrid.WorldPerTile / 2));
        var keep = new bool[w * h];
        for (var i = 0; i < w * h; i++) keep[i] = clear[i] >= minClear;

        // 3. The component WE are in
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

        // 4. INSET from the wall, then take the boundary of what remains: the boundary of the inset shape IS the wall-hu…
        var inset = Math.Max(1, (int)Math.Round(marginWorld / BlockGrid.WorldPerTile));
        var body = new bool[w * h];
        for (var i = 0; i < w * h; i++) body[i] = comp[i] && clear[i] >= minClear + inset;
        if (!body.Any(b => b)) body = comp;      // room too tight to inset — hug the raw contour instead

        body = ComponentContaining(body, w, h, btx - x0, bty - y0);
        if (body is null) return [];

        var ring = TraceOuterContour(body, w, h);
        if (ring.Count < 8) return [];

        // 5. Decimate to a walkable number of waypoints, preserving order
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

    /// <summary>The connected blob of containing (or nearest to) the bot, with everything else cleared — so a contour trace ca…</summary>
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

    /// <summary>Moore-neighbourhood boundary trace of the shape's OUTER contour, returned in walk order</summary>
    private static List<(int X, int Y)> TraceOuterContour(bool[] body, int w, int h)
    {
        // Topmost-leftmost filled cell is guaranteed to be on the outer contour
        var startIdx = -1;
        for (var i = 0; i < body.Length && startIdx < 0; i++) if (body[i]) startIdx = i;
        var res = new List<(int, int)>();
        if (startIdx < 0) return res;

        int sx = startIdx % w, sy = startIdx / w;
        // 8-neighbourhood, clockwise from "west" so the first probe of a top-left start is outside
        int[] dx = [-1, -1, 0, 1, 1, 1, 0, -1];
        int[] dy = [0, -1, -1, -1, 0, 1, 1, 1];
        int cx = sx, cy = sy, dir = 3;
        var guard = w * h * 4;
        do
        {
            res.Add((cx, cy));
            // Turn back toward where we came from, then sweep clockwise for the next filled neighbour
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
