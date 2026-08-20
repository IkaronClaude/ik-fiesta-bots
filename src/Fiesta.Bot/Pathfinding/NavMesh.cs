using System.IO.Compression;

namespace Fiesta.Bot.Pathfinding;

/// <summary>A map decomposed into axis-aligned rectangles of free space, plus the portals between them.
///
/// WHY: tile A* pays full-grid cost for two different problems at once -- WHICH WAY to go, and HOW to get around
/// the rock in front of you. Splitting them collapses the first. Measured on Sand Hill (EldGbl02): 1,200,362
/// walkable tiles become 7,854 rectangles and 10,545 edges, so the global graph is ~150x smaller than the grid and
/// a Dijkstra across it is sub-millisecond, against searches that were costing 10.5 SECONDS on that same map.
///
/// WHY RECTANGLES: convexity is the whole trick -- inside a convex region any two points join by a straight line,
/// so there is no local search left to do. Rectangles are the grid-native convex shape: axis-aligned edges,
/// integer adjacency, and no floating-point geometry to get subtly wrong. A room with a rock in it simply becomes
/// a few rectangles around the rock.
///
/// WHY NOT THE EXISTING COARSE LAYER: CoarsePathFinder lays a fixed BLOCK lattice over the map, so its corridors
/// do not follow the map's shape and it needs corridor-widening retries to compensate. A rectangle IS free space
/// by construction -- there is no mask to widen and nothing to be wrong about.
///
/// A MESH IS TIED TO THE MARGIN IT WAS BUILT AT. "Walk straight inside a region" is only true for the clearance
/// that region was carved for, so a different body size needs its own mesh.</summary>
public sealed class NavMesh
{
    public readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int MaxX => X + W - 1;
        public int MaxY => Y + H - 1;
        public int CX => X + W / 2;
        public int CY => Y + H / 2;
    }

    /// <summary>A shared edge between two regions. The span is the overlapping run along the shared boundary, and
    /// the crossing point along it is what the funnel gets to choose -- which is exactly why a centre-to-centre
    /// path is valid but longer than it needs to be.</summary>
    public readonly record struct Portal(int To, bool Vertical, int At, int From, int Until)
    {
        public int MidA => (From + Until) / 2;
    }

    public IReadOnlyList<Rect> Rects { get; }
    public IReadOnlyList<IReadOnlyList<Portal>> Portals { get; }
    public double Margin { get; }
    private readonly int[] _owner;      // tile -> rect id, -1 when not free space
    private readonly int _w, _h;

    private NavMesh(int w, int h, double margin, int[] owner, List<Rect> rects, List<List<Portal>> portals)
        => (_w, _h, Margin, _owner, Rects, Portals) = (w, h, margin, owner, rects, portals.Select(p => (IReadOnlyList<Portal>)p).ToList());

    public int RegionAt(int tx, int ty)
        => (uint)tx >= (uint)_w || (uint)ty >= (uint)_h ? -1 : _owner[ty * _w + tx];

    /// <summary>Persist the mesh. Only the rectangles and portals are written -- the tile->region plane is
    /// REPAINTED on load by filling each rectangle, which is a linear pass over free space and far cheaper than
    /// storing W*H ints (16MB for a 2048x2048 map, against ~450KB for the shapes).</summary>
    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(new DeflateStream(fs, CompressionLevel.Optimal));
        bw.Write(new[] { (byte)'F', (byte)'N', (byte)'A', (byte)'V' });
        bw.Write((ushort)1);
        bw.Write((ushort)_w); bw.Write((ushort)_h);
        bw.Write(Margin);
        bw.Write(Rects.Count);
        foreach (var r in Rects) { bw.Write((ushort)r.X); bw.Write((ushort)r.Y); bw.Write((ushort)r.W); bw.Write((ushort)r.H); }
        // Portals are stored ONE-WAY (a < b) and mirrored on load; writing both directions would double the file
        // for no information.
        int edges = 0;
        for (int i = 0; i < Portals.Count; i++) foreach (var p in Portals[i]) if (p.To > i) edges++;
        bw.Write(edges);
        for (int i = 0; i < Portals.Count; i++)
            foreach (var p in Portals[i])
                if (p.To > i)
                {
                    bw.Write(i); bw.Write(p.To); bw.Write(p.Vertical);
                    bw.Write((ushort)p.At); bw.Write((ushort)p.From); bw.Write((ushort)p.Until);
                }
    }

    /// <summary>Load a cached mesh, or null when absent/unreadable/built for a different grid or margin.
    /// A mesh built against a different .shbd would mis-answer every query, so refuse rather than trust.</summary>
    public static NavMesh? Load(string path, int expectW = 0, int expectH = 0, double expectMargin = -1)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(new DeflateStream(fs, CompressionMode.Decompress));
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'F' || magic[1] != 'N' || magic[2] != 'A' || magic[3] != 'V') return null;
            if (br.ReadUInt16() != 1) return null;
            int w = br.ReadUInt16(), h = br.ReadUInt16();
            double margin = br.ReadDouble();
            if (expectW > 0 && (w != expectW || h != expectH)) return null;
            if (expectMargin >= 0 && Math.Abs(margin - expectMargin) > 1e-9) return null;
            int nr = br.ReadInt32();
            var rects = new List<Rect>(nr);
            for (int i = 0; i < nr; i++) rects.Add(new Rect(br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt16()));
            var portals = new List<List<Portal>>(nr);
            for (int i = 0; i < nr; i++) portals.Add(new List<Portal>());
            int ne = br.ReadInt32();
            for (int i = 0; i < ne; i++)
            {
                int a = br.ReadInt32(), b = br.ReadInt32();
                bool vert = br.ReadBoolean();
                int at = br.ReadUInt16(), from = br.ReadUInt16(), until = br.ReadUInt16();
                portals[a].Add(new Portal(b, vert, at, from, until));
                portals[b].Add(new Portal(a, vert, at, from, until));
            }
            var owner = new int[w * h];
            Array.Fill(owner, -1);
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                for (int yy = r.Y; yy <= r.MaxY; yy++)
                    for (int xx = r.X; xx <= r.MaxX; xx++)
                        owner[yy * w + xx] = i;
            }
            return new NavMesh(w, h, margin, owner, rects, portals);
        }
        catch { return null; }
    }

    /// <summary>Greedy maximal rectangles. NOT a minimal partition -- a smarter decomposition yields fewer regions,
    /// so the counts above are an upper bound rather than the best achievable.</summary>
    public static NavMesh Build(BlockGrid grid, double margin = PathFinder.DefaultMargin)
    {
        int W = grid.WidthTiles, H = grid.HeightTiles;
        var pass = new bool[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                pass[y * W + x] = grid.IsPathable(x, y, margin);

        var owner = new int[W * H];
        Array.Fill(owner, -1);
        var rects = new List<Rect>();
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int id = y * W + x;
            if (!pass[id] || owner[id] >= 0) continue;
            int rw = 0;
            while (x + rw < W && pass[y * W + x + rw] && owner[y * W + x + rw] < 0) rw++;
            int rh = 1;
            while (y + rh < H)
            {
                bool ok = true;
                for (int k = 0; k < rw; k++)
                {
                    int t = (y + rh) * W + x + k;
                    if (!pass[t] || owner[t] >= 0) { ok = false; break; }
                }
                if (!ok) break;
                rh++;
            }
            int rid = rects.Count;
            for (int yy = 0; yy < rh; yy++)
                for (int xx = 0; xx < rw; xx++)
                    owner[(y + yy) * W + x + xx] = rid;
            rects.Add(new Rect(x, y, rw, rh));
        }

        // Portals: walk each rectangle's right and bottom boundary and group runs by the neighbour on the far side.
        // Grouping by neighbour is what turns "these tiles touch" into "this is the doorway", and the run's extent
        // is what the funnel needs -- a portal recorded as a single point would throw away the choice.
        var portals = new List<List<Portal>>(rects.Count);
        for (int i = 0; i < rects.Count; i++) portals.Add(new List<Portal>());
        // NO DEDUPLICATION GUARD HERE, deliberately. Only each rect's RIGHT and BOTTOM edges are scanned, so every
        // adjacent pair is already visited exactly once -- from the left/top side. An earlier `run > i` guard
        // looked like dedup but actually DROPPED every adjacency whose neighbour had a lower id, which happens
        // whenever a tall rect created earlier sits to the right of a later one. The graph came out disconnected
        // and most routes simply failed (91 pairs "solved" in 3ms, path length ratio 0.000).
        void Link(int a, int b, bool vertical, int at, int from, int until)
        {
            portals[a].Add(new Portal(b, vertical, at, from, until));
            portals[b].Add(new Portal(a, vertical, at, from, until));
        }
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            // right edge: neighbours across x = MaxX+1
            int nx = r.MaxX + 1;
            if (nx < W)
            {
                int run = -1, runFrom = 0;
                for (int y = r.Y; y <= r.MaxY; y++)
                {
                    int nb = owner[y * W + nx];
                    if (nb != run)
                    {
                        if (run >= 0 && run != i) Link(i, run, true, nx, runFrom, y - 1);
                        run = nb; runFrom = y;
                    }
                }
                if (run >= 0 && run != i) Link(i, run, true, nx, runFrom, r.MaxY);
            }
            // bottom edge: neighbours across y = MaxY+1
            int ny = r.MaxY + 1;
            if (ny < H)
            {
                int run = -1, runFrom = 0;
                for (int x = r.X; x <= r.MaxX; x++)
                {
                    int nb = owner[ny * W + x];
                    if (nb != run)
                    {
                        if (run >= 0 && run != i) Link(i, run, false, ny, runFrom, x - 1);
                        run = nb; runFrom = x;
                    }
                }
                if (run >= 0 && run != i) Link(i, run, false, ny, runFrom, r.MaxX);
            }
        }
        return new NavMesh(W, H, margin, owner, rects, portals);
    }
}
