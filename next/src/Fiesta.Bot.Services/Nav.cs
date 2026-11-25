using System.Collections.Concurrent;

namespace Fiesta.Bot.Services;

/// <summary>Walkability for one map: the client's .shbd (set bit = blocked), OR-ed with .shab where present;
/// immutable once built. Tile = floor(world / 6.25); outside the grid is blocked.</summary>
public sealed class BlockLayer
{
    /// <summary>World units per tile.</summary>
    public const double WorldPerTile = 6.25;
    private readonly byte[] _bits;
    private readonly byte[]? _attack;
    private readonly int _stride;

    /// <summary>Width in tiles.</summary>
    public int Width { get; }
    /// <summary>Height in tiles.</summary>
    public int Height { get; }
    /// <summary>Map name.</summary>
    public string Map { get; }

    private BlockLayer(string map, byte[] bits, byte[]? attack, int stride, int h) { Map = map; _bits = bits; _attack = attack; _stride = stride; Width = stride * 8; Height = h; }

    /// <summary>Create from raw bits (tests).</summary>
    public static BlockLayer FromBits(string map, int widthTiles, int heightTiles, Func<int, int, bool> blocked)
    {
        var stride = (widthTiles + 7) / 8;
        var b = new byte[stride * heightTiles];
        for (var y = 0; y < heightTiles; y++)
            for (var x = 0; x < widthTiles; x++)
                if (blocked(x, y)) b[y * stride + (x >> 3)] |= (byte)(1 << (x & 7));
        return new BlockLayer(map, b, null, stride, heightTiles);
    }

    private static (byte[] Bits, int Stride, int H)? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        if (b.Length < 8) return null;
        var stride = BitConverter.ToInt32(b, 0); var h = BitConverter.ToInt32(b, 4);
        if (stride <= 0 || h <= 0 || b.Length < 8L + (long)stride * h) return null;
        return (b.AsSpan(8, stride * h).ToArray(), stride, h);
    }

    /// <summary>Find a map's folder under a client root (resmap/&lt;kind&gt;/&lt;folder&gt;) or a flat block directory.</summary>
    public static string? FindFile(string root, string mapName, string folder, string ext)
    {
        var flat = Path.Combine(root, mapName + ext);
        if (File.Exists(flat)) return flat;
        var resmap = Path.Combine(root, "resmap");
        if (!Directory.Exists(resmap)) return null;
        foreach (var kind in new[] { "field", "IDField", "KDField", "MHField" })
        {
            foreach (var f in new[] { folder, mapName })
            {
                var p = Path.Combine(resmap, kind, f, mapName + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Load a map's block files from a client root (or a flat .shbd directory); null when absent.</summary>
    public static BlockLayer? Load(string root, string mapName, string? folder = null)
    {
        var shbd = FindFile(root, mapName, folder ?? mapName, ".shbd");
        if (shbd is null || Read(shbd) is not { } m) return null;
        var bits = m.Bits;
        byte[]? attack = null;
        var shabPath = FindFile(root, mapName, folder ?? mapName, ".shab");
        if (shabPath is not null && Read(shabPath) is { } a && a.Stride == m.Stride && a.H == m.H)
        {
            attack = a.Bits;
            bits = (byte[])bits.Clone();
            for (var i = 0; i < bits.Length; i++) bits[i] |= attack[i];
        }
        return new BlockLayer(mapName, bits, attack, m.Stride, m.H);
    }

    /// <summary>Is the tile blocked for movement?</summary>
    public bool Blocked(int tx, int ty)
        => (uint)tx >= (uint)Width || (uint)ty >= (uint)Height || (_bits[ty * _stride + (tx >> 3)] & (1 << (tx & 7))) != 0;
    /// <summary>Is the tile in the attack-block layer (line of fire)?</summary>
    public bool AttackBlocked(int tx, int ty)
        => _attack is not null && ((uint)tx >= (uint)Width || (uint)ty >= (uint)Height || (_attack[ty * _stride + (tx >> 3)] & (1 << (tx & 7))) != 0);
    /// <summary>Has an attack-block layer.</summary>
    public bool HasAttackLayer => _attack is not null;
    /// <summary>World to tile.</summary>
    public static (int Tx, int Ty) ToTile(double x, double y) => ((int)Math.Floor(x / WorldPerTile), (int)Math.Floor(y / WorldPerTile));
    /// <summary>Walkable at a world point.</summary>
    public bool WalkableWorld(double x, double y) { var (tx, ty) = ToTile(x, y); return !Blocked(tx, ty); }
}

/// <summary>Per-bot changes on top of the shared block layer: learned blocks with TTL, observed doors.</summary>
public sealed class NavOverlay
{
    private readonly Dictionary<long, long> _blocked = new();
    private readonly Dictionary<string, bool> _doors = new();
    private readonly Dictionary<string, HashSet<long>> _doorTiles = new(StringComparer.Ordinal);
    private int _doorTileCount;
    /// <summary>Version (changes on every learn / clear).</summary>
    public int Version { get; private set; }
    /// <summary>Learned blocks.</summary>
    public int Count => _blocked.Count + _doorTileCount;
    /// <summary>The tiles a door blocks in its current state beyond the map's own blocks (replaces the door's previous
    /// tiles; empty = the door blocks nothing extra).</summary>
    public void SetDoorTiles(string door, HashSet<long> tiles)
    {
        if (_doorTiles.TryGetValue(door, out var old)) _doorTileCount -= old.Count;
        _doorTiles[door] = tiles; _doorTileCount += tiles.Count; Version++;
    }
    /// <summary>Record a server-refused segment end as blocked for a time.</summary>
    public void LearnBlocked(int tx, int ty, long untilMs) { _blocked[((long)tx << 32) | (uint)ty] = untilMs; Version++; }
    /// <summary>Observed door state.</summary>
    public void SetDoor(string door, bool closed) { _doors[door] = closed; Version++; }
    /// <summary>Is the tile blocked by the overlay at time now?</summary>
    public bool Blocked(int tx, int ty, long nowMs)
    {
        var k = ((long)tx << 32) | (uint)ty;
        if (_blocked.Count > 0 && _blocked.TryGetValue(k, out var until) && until > nowMs) return true;
        if (_doorTileCount > 0) foreach (var t in _doorTiles.Values) if (t.Contains(k)) return true;
        return false;
    }
    /// <summary>Forget everything (map change).</summary>
    public void Clear() { _blocked.Clear(); _doors.Clear(); _doorTiles.Clear(); _doorTileCount = 0; Version++; }
    /// <summary>A copy for a path job (jobs must not read the tick's live overlay).</summary>
    public HashSet<long> SnapshotBlocked(long nowMs)
    {
        var s = new HashSet<long>();
        foreach (var kv in _blocked) if (kv.Value > nowMs) s.Add(kv.Key);
        foreach (var t in _doorTiles.Values) foreach (var k in t) s.Add(k);
        return s;
    }
}

/// <summary>Rectangle/portal split of a block layer: greedy maximal rectangles of free space and the shared edges
/// between them.</summary>
public sealed class NavMesh
{
    /// <summary>A free rectangle in tiles.</summary>
    public readonly record struct Rect(int X, int Y, int W, int H);
    /// <summary>A shared edge: vertical portals lie on x = At between rows From..Until; horizontal on y = At.</summary>
    public readonly record struct Portal(int To, bool Vertical, int At, int From, int Until);

    private readonly int[] _owner;
    /// <summary>Width.</summary>
    public int Width { get; }
    /// <summary>Height.</summary>
    public int Height { get; }
    /// <summary>Rectangles.</summary>
    public Rect[] Rects { get; }
    /// <summary>Portals per rectangle.</summary>
    public Portal[][] Portals { get; }
    /// <summary>Connected component per region (for reachability).</summary>
    public int[] Component { get; }

    private NavMesh(int w, int h, int[] owner, Rect[] rects, Portal[][] portals)
    {
        Width = w; Height = h; _owner = owner; Rects = rects; Portals = portals;
        Component = new int[rects.Length];
        Array.Fill(Component, -1);
        var stack = new Stack<int>(); var c = 0;
        for (var i = 0; i < rects.Length; i++)
        {
            if (Component[i] >= 0) continue;
            Component[i] = c; stack.Push(i);
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                foreach (var p in portals[r]) if (Component[p.To] < 0) { Component[p.To] = c; stack.Push(p.To); }
            }
            c++;
        }
    }

    /// <summary>Number of regions.</summary>
    public int RegionCount => Rects.Length;
    /// <summary>Region index at a tile, -1 outside.</summary>
    public int RegionAt(int tx, int ty) => (uint)tx >= (uint)Width || (uint)ty >= (uint)Height ? -1 : _owner[ty * Width + tx];

    /// <summary>Build from a block layer.</summary>
    public static NavMesh Build(BlockLayer layer)
    {
        int W = layer.Width, H = layer.Height;
        var owner = new int[W * H];
        Array.Fill(owner, -1);
        var rects = new List<Rect>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var id = y * W + x;
                if (layer.Blocked(x, y) || owner[id] >= 0) continue;
                var rw = 0;
                while (x + rw < W && !layer.Blocked(x + rw, y) && owner[id + rw] < 0) rw++;
                var rh = 1;
                for (; y + rh < H; rh++)
                {
                    var ok = true;
                    for (var k = 0; k < rw && ok; k++) ok = !layer.Blocked(x + k, y + rh) && owner[(y + rh) * W + x + k] < 0;
                    if (!ok) break;
                }
                var rid = rects.Count;
                for (var yy = 0; yy < rh; yy++)
                    for (var xx = 0; xx < rw; xx++) owner[(y + yy) * W + x + xx] = rid;
                rects.Add(new Rect(x, y, rw, rh));
            }
        var portals = new List<Portal>[rects.Count];
        for (var i = 0; i < portals.Length; i++) portals[i] = new List<Portal>();
        void Link(int a, int b, bool v, int at, int from, int until) { portals[a].Add(new Portal(b, v, at, from, until)); portals[b].Add(new Portal(a, v, at, from, until)); }
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            var nx = r.X + r.W;
            if (nx < W)
            {
                int run = -1, from = 0;
                for (var y = r.Y; y < r.Y + r.H; y++)
                {
                    var nb = owner[y * W + nx];
                    if (nb != run) { if (run >= 0 && run != i) Link(i, run, true, nx, from, y - 1); run = nb; from = y; }
                }
                if (run >= 0 && run != i) Link(i, run, true, nx, from, r.Y + r.H - 1);
            }
            var ny = r.Y + r.H;
            if (ny < H)
            {
                int run = -1, from = 0;
                for (var x = r.X; x < r.X + r.W; x++)
                {
                    var nb = owner[ny * W + x];
                    if (nb != run) { if (run >= 0 && run != i) Link(i, run, false, ny, from, x - 1); run = nb; from = x; }
                }
                if (run >= 0 && run != i) Link(i, run, false, ny, from, r.X + r.W - 1);
            }
        }
        return new NavMesh(W, H, owner, rects.ToArray(), portals.Select(p => p.ToArray()).ToArray());
    }
}

/// <summary>Outcome of a path search.</summary>
public enum PathStatus : byte { Pending, Ready, Failed }

/// <summary>A finished path (waypoints in world units, the start excluded).</summary>
public sealed record PathResult(PathStatus Status, IReadOnlyList<(uint X, uint Y)> Waypoints, double Length, string? FailReason, bool Partial = false);

/// <summary>Corridor A* over regions + funnel + corner nudge + goal snapping (ported from fiesta-godot; see
/// docs/NAVMESH_PATH_IMPROVEMENT.md). Works in tile units; the caller converts.</summary>
public static class PathSearch
{
    private readonly record struct V(double X, double Y);

    /// <summary>Find a path from world (fx,fy) to (tx,ty). Blocked endpoints are snapped to the nearest free tile
    /// (ring search, 48 tiles); an unreachable goal yields a partial path to the reachable point nearest it.</summary>
    // does a leg (tile coordinates) pass over a tile the predicate blocks? sampled every half tile
    private static bool SegmentHits(Func<int, int, bool> blocked, double ax, double ay, double bx, double by)
    {
        var n = (int)Math.Ceiling(Hyp(bx - ax, by - ay) * 2) + 1;
        for (var i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            if (blocked((int)Math.Floor(ax + (bx - ax) * t), (int)Math.Floor(ay + (by - ay) * t))) return true;
        }
        return false;
    }

    public static PathResult Find(NavMesh mesh, double fx, double fy, double tx, double ty, Func<int, int, bool>? extraBlocked = null)
    {
        const double T = BlockLayer.WorldPerTile;
        bool Free(int x, int y) => mesh.RegionAt(x, y) >= 0 && (extraBlocked is null || !extraBlocked(x, y));
        var (sxT, syT) = BlockLayer.ToTile(fx, fy);
        var (gxT, gyT) = BlockLayer.ToTile(tx, ty);
        if (!Snap(Free, ref sxT, ref syT)) return new PathResult(PathStatus.Failed, [], 0, "start not near walkable ground");
        var goalMoved = !Free(gxT, gyT);
        if (!Snap(Free, ref gxT, ref gyT)) return new PathResult(PathStatus.Failed, [], 0, "goal not near walkable ground");
        var partial = goalMoved;
        int sr = mesh.RegionAt(sxT, syT), gr = mesh.RegionAt(gxT, gyT);
        double sx = sxT + 0.5, sy = syT + 0.5, gx = gxT + 0.5, gy = gyT + 0.5;
        int n = mesh.RegionCount;
        var dist = new double[n]; Array.Fill(dist, double.MaxValue);
        var prev = new int[n]; Array.Fill(prev, -1);
        var entry = new V[n];
        var open = new PriorityQueue<int, double>();
        dist[sr] = 0; entry[sr] = new V(sx, sy);
        open.Enqueue(sr, Hyp(sx - gx, sy - gy));
        while (open.TryDequeue(out var cur, out var pri))
        {
            var e = entry[cur];
            if (pri > dist[cur] + Hyp(e.X - gx, e.Y - gy) + 1e-6) continue;
            if (cur == gr) break;
            foreach (var pt in mesh.Portals[cur])
            {
                double lo = pt.From + 0.5, hi = pt.Until + 0.5, o;
                double a = pt.Vertical ? e.X : e.Y, b = pt.Vertical ? gx : gy;
                double ea = pt.Vertical ? e.Y : e.X, ga = pt.Vertical ? gy : gx;
                if ((a - pt.At) * (b - pt.At) < 0) o = ea + (ga - ea) * (pt.At - a) / (b - a); else o = ga;
                o = Math.Clamp(o, lo, hi);
                double px = pt.Vertical ? pt.At : o, py = pt.Vertical ? o : pt.At;
                var nd = dist[cur] + Hyp(px - e.X, py - e.Y);
                if (nd >= dist[pt.To]) continue;
                // the overlay's tiles (a closed door, a refused spot) inside the free rectangles: a crossing whose leg runs
                // over one is not taken (the rectangles come from the map's own blocks only)
                if (extraBlocked is not null && SegmentHits(extraBlocked, e.X, e.Y, px, py)) continue;
                dist[pt.To] = nd; prev[pt.To] = cur; entry[pt.To] = new V(px, py);
                open.Enqueue(pt.To, nd + Hyp(px - gx, py - gy));
            }
        }
        if (dist[gr] == double.MaxValue)
        {
            int best = -1; double bd = double.MaxValue; int btx = 0, bty = 0;
            for (var r = 0; r < n; r++)
            {
                if (dist[r] == double.MaxValue) continue;
                var rc = mesh.Rects[r];
                int cx = Math.Clamp(gxT, rc.X, rc.X + rc.W - 1), cy = Math.Clamp(gyT, rc.Y, rc.Y + rc.H - 1);
                var d = Hyp(cx - gxT, cy - gyT);
                if (d < bd) { bd = d; best = r; btx = cx; bty = cy; }
            }
            if (best < 0) return new PathResult(PathStatus.Failed, [], 0, "unreachable");
            gxT = btx; gyT = bty; gr = best; gx = gxT + 0.5; gy = gyT + 0.5; partial = true;
        }
        var route = new List<int>();
        for (var c = gr; c >= 0; c = prev[c]) route.Add(c);
        route.Reverse();
        var lefts = new List<V> { new(sx, sy) }; var rights = new List<V> { new(sx, sy) };
        for (var i = 0; i + 1 < route.Count; i++)
        {
            NavMesh.Portal? pp = null;
            foreach (var pt in mesh.Portals[route[i]]) if (pt.To == route[i + 1]) { pp = pt; break; }
            if (pp is not { } p) return new PathResult(PathStatus.Failed, [], 0, "broken corridor");
            var ra = mesh.Rects[route[i]];
            var p1 = p.Vertical ? new V(p.At, p.From + 0.5) : new V(p.From + 0.5, p.At);
            var p2 = p.Vertical ? new V(p.At, p.Until + 0.5) : new V(p.Until + 0.5, p.At);
            double dx = p.Vertical ? (p.At >= ra.X + ra.W ? 1 : -1) : 0, dy = p.Vertical ? 0 : (p.At >= ra.Y + ra.H ? 1 : -1);
            double mx = (p1.X + p2.X) / 2, my = (p1.Y + p2.Y) / 2;
            var p1Right = dx * (p1.Y - my) - dy * (p1.X - mx) < 0;
            lefts.Add(p1Right ? p2 : p1); rights.Add(p1Right ? p1 : p2);
        }
        lefts.Add(new V(gx, gy)); rights.Add(new V(gx, gy));
        static double Tri(V a, V b, V c) => (c.X - a.X) * (b.Y - a.Y) - (b.X - a.X) * (c.Y - a.Y);
        static bool Same(V a, V b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
        var way = new List<V>();
        V apex = lefts[0], fl = lefts[0], fr = rights[0];
        int ai = 0, li = 0, ri = 0;
        for (var i = 1; i < lefts.Count; i++)
        {
            V l = lefts[i], r = rights[i];
            if (Tri(apex, fr, r) <= 0)
            {
                if (Same(apex, fr) || Tri(apex, fl, r) > 0) { fr = r; ri = i; }
                else { way.Add(fl); apex = fl; ai = li; fl = fr = apex; li = ri = ai; i = ai; continue; }
            }
            if (Tri(apex, fl, l) >= 0)
            {
                if (Same(apex, fl) || Tri(apex, fr, l) < 0) { fl = l; li = i; }
                else { way.Add(fr); apex = fr; ai = ri; fl = fr = apex; li = ri = ai; i = ai; continue; }
            }
        }
        bool SegFree(V a, V b)
        {
            int x = (int)Math.Floor(a.X), y = (int)Math.Floor(a.Y);
            int ex = (int)Math.Floor(b.X), ey = (int)Math.Floor(b.Y);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            int stx = dx > 0 ? 1 : -1, sty = dy > 0 ? 1 : -1;
            double tdx = dx != 0 ? Math.Abs(1.0 / dx) : 1e30, tdy = dy != 0 ? Math.Abs(1.0 / dy) : 1e30;
            double tmx = dx != 0 ? (dx > 0 ? x + 1 - a.X : a.X - x) * tdx : 1e30;
            double tmy = dy != 0 ? (dy > 0 ? y + 1 - a.Y : a.Y - y) * tdy : 1e30;
            for (var guard = 0; guard < 1 << 20; guard++)
            {
                if (!Free(x, y)) return false;
                if (x == ex && y == ey) return true;
                if (Math.Abs(tmx - tmy) < 1e-9)
                {
                    if (!Free(x + stx, y) || !Free(x, y + sty)) return false;
                    x += stx; y += sty; tmx += tdx; tmy += tdy;
                }
                else if (tmx < tmy) { x += stx; tmx += tdx; }
                else { y += sty; tmy += tdy; }
            }
            return true;
        }
        ReadOnlySpan<V> nudges = [new(0.5, 0), new(-0.5, 0), new(0, 0.5), new(0, -0.5), new(0.5, 0.5), new(-0.5, 0.5), new(0.5, -0.5), new(-0.5, -0.5)];
        for (var k = 0; k < way.Count; k++)
        {
            var before = k > 0 ? way[k - 1] : new V(sx, sy);
            var after = k + 1 < way.Count ? way[k + 1] : new V(gx, gy);
            if (SegFree(before, way[k]) && SegFree(way[k], after)) continue;
            foreach (var d in nudges)
            {
                var c = new V(way[k].X + d.X, way[k].Y + d.Y);
                if (SegFree(before, c) && SegFree(c, after)) { way[k] = c; break; }
            }
        }
        var pts = new List<(uint, uint)>(way.Count + 1);
        var last = new V(sx, sy); var len = 0.0; var prevW = new V(fx / T, fy / T);
        for (var k = 0; k < way.Count; k++)
        {
            var v = way[k];
            if (Same(v, new V(sx, sy))) continue;
            if (k + 1 == way.Count && Hyp(v.X - gx, v.Y - gy) < 1.0 && SegFree(last, new V(gx, gy))) continue;
            last = v;
            pts.Add(((uint)(v.X * T), (uint)(v.Y * T)));
            len += Hyp(v.X - prevW.X, v.Y - prevW.Y) * T; prevW = v;
        }
        var end = partial ? ((uint)(gx * T), (uint)(gy * T)) : ((uint)tx, (uint)ty);
        pts.Add(end);
        len += Hyp(end.Item1 / T - prevW.X, end.Item2 / T - prevW.Y) * T;
        return new PathResult(PathStatus.Ready, pts, len, null, partial);
    }

    private static double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>Snap a tile to the nearest free tile, ring search outward (the client's behaviour for a blocked click).</summary>
    public static bool Snap(Func<int, int, bool> free, ref int x, ref int y, int maxR = 48)
    {
        if (free(x, y)) return true;
        for (var r = 1; r <= maxR; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (free(x + dx, y + dy)) { x += dx; y += dy; return true; }
                }
        return false;
    }

    /// <summary>Region connectivity.</summary>
    public static bool Reachable(NavMesh mesh, int sx, int sy, int gx, int gy)
    {
        int a = mesh.RegionAt(sx, sy), b = mesh.RegionAt(gx, gy);
        return a >= 0 && b >= 0 && mesh.Component[a] == mesh.Component[b];
    }
}

/// <summary>Shared navigation per map plus the path job entry point.</summary>
public interface INavService
{
    /// <summary>Block layer for a map (loaded once), null when the client has no block file for it.</summary>
    BlockLayer? Layer(string mapName);
    /// <summary>Mesh for a map (built once, shared), null when no layer.</summary>
    NavMesh? Mesh(string mapName);
    /// <summary>Queue a path search; <paramref name="done"/> runs on the job thread when it finishes.</summary>
    JobTicket RequestPath(int botIndex, string mapName, HashSet<long>? overlay, uint fx, uint fy, uint tx, uint ty, Action<JobTicket, bool>? done);
    /// <summary>Read a finished path.</summary>
    PathResult? Result(JobTicket ticket);
    /// <summary>Is a map's mesh ready (built)? Building is a job; the first request for a map starts it.</summary>
    bool Ready(string mapName);
    /// <summary>The trigger areas of a map (its .aid), empty when it has none (read once per map, a small file).</summary>
    IReadOnlyList<ScenarioArea> Areas(string mapName) => [];
    /// <summary>The doors of a map (its .sbi), empty when it has none.</summary>
    IReadOnlyList<MapDoor> Doors(string mapName) => [];
}

/// <summary>Default nav service over a client root.</summary>
public sealed class NavService(string clientRoot, IJobScheduler jobs, Func<string, string?>? folderOf = null) : INavService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ScenarioArea>> _areas = new(StringComparer.OrdinalIgnoreCase);
    /// <inheritdoc/>
    public IReadOnlyList<ScenarioArea> Areas(string mapName) => _areas.GetOrAdd(mapName, m => ScenarioAreaFile.Load(clientRoot, m, folderOf?.Invoke(m)));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<MapDoor>> _doorFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <inheritdoc/>
    public IReadOnlyList<MapDoor> Doors(string mapName) => _doorFiles.GetOrAdd(mapName, m => MapDoorFile.Load(clientRoot, m, folderOf?.Invoke(m)));
    private readonly ConcurrentDictionary<string, Lazy<(BlockLayer? L, NavMesh? M)>> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths computed / total ms.</summary>
    public long Paths, PathMicros;

    private (BlockLayer? L, NavMesh? M) Get(string map)
        => _maps.GetOrAdd(map, m => new Lazy<(BlockLayer?, NavMesh?)>(() =>
        {
            var l = BlockLayer.Load(clientRoot, m, folderOf?.Invoke(m));
            var r = (l, l is null ? null : NavMesh.Build(l));
            _ready[m] = true;
            return r;
        })).Value;

    /// <summary>Non-blocking: the layer when the map is loaded, else null (and loading starts on the job pool). The
    /// tick thread must never build a mesh (hundreds of milliseconds on a large map).</summary>
    public BlockLayer? Layer(string mapName) { if (_ready.ContainsKey(mapName)) return Get(mapName).L; Prepare(mapName); return null; }
    /// <inheritdoc/>
    public NavMesh? Mesh(string mapName) { if (_ready.ContainsKey(mapName)) return Get(mapName).M; Prepare(mapName); return null; }
    private readonly ConcurrentDictionary<string, bool> _preparing = new(StringComparer.OrdinalIgnoreCase);
    /// <inheritdoc/>
    public bool Ready(string mapName) => _ready.ContainsKey(mapName);
    /// <summary>Warm a map off the tick.</summary>
    public void Prepare(string mapName)
    {
        if (string.IsNullOrEmpty(mapName) || _ready.ContainsKey(mapName) || !_preparing.TryAdd(mapName, true)) return;
        jobs.Submit(-1, JobKind.Custom, _ => Get(mapName));
    }

    /// <inheritdoc/>
    public JobTicket RequestPath(int botIndex, string mapName, HashSet<long>? overlay, uint fx, uint fy, uint tx, uint ty, Action<JobTicket, bool>? done)
        => jobs.Submit(botIndex, JobKind.Path, _ =>
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var mesh = Get(mapName).M;
            PathResult r = mesh is null
                ? new PathResult(PathStatus.Failed, [], 0, $"no block data for {mapName}")
                : PathSearch.Find(mesh, fx, fy, tx, ty, overlay is { Count: > 0 } o ? (x, y) => o.Contains(((long)x << 32) | (uint)y) : null);
            Interlocked.Increment(ref Paths);
            Interlocked.Add(ref PathMicros, (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            return r;
        }, done);

    /// <inheritdoc/>
    public PathResult? Result(JobTicket ticket) => jobs.Result(ticket) as PathResult;
}
