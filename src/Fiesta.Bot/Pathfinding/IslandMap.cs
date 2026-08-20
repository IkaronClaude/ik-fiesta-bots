using System.IO.Compression;

namespace Fiesta.Bot.Pathfinding;

/// <summary>A map's precomputed walkable-connectivity ("island") plane, built offline by tools/IslandMapBuilder.
///
/// WHAT IT BUYS: reachability without a search. A* is the worst possible tool for proving NO path exists -- it has
/// to expand everything, and with an inadmissible heuristic it re-expands closed nodes on top. Measured on
/// EldGbl02 (2048x2048): A* took 14,455ms to conclude "unreachable" where a flood fill answered in 290ms, and the
/// live cost of that shape was a 357-SECOND stall on a 511-unit walk while the bot stood still.
///
/// WHY A CACHE BUILT FROM THE BASE .shbd IS STILL CORRECT AT RUNTIME: learned MOVEFAIL blocks and closed .sbi
/// doors only ever REMOVE connectivity, never add it. So
///     different islands => DEFINITELY unreachable  (answer immediately, skip the search)
///     same island       => MAYBE reachable         (still needs the search)
/// The test is one-way, and the direction it gives away for free is the expensive one.</summary>
public sealed class IslandMap
{
    /// <summary>Slot value meaning "no tracked island here": a blocked tile, or an island too small / too far down
    /// the ranking to have been stored. Never conclude anything from it -- see <see cref="DefinitelyUnreachable"/>.</summary>
    public const byte Unknown = 7;

    public int WidthTiles { get; }
    public int HeightTiles { get; }
    public IReadOnlyList<(int MinX, int MinY, int MaxX, int MaxY, uint Tiles)> Islands { get; }

    private readonly byte[] _packed;   // 3 bits per tile, row-major

    private IslandMap(int w, int h, byte[] packed, List<(int, int, int, int, uint)> islands)
        => (WidthTiles, HeightTiles, _packed, Islands) = (w, h, packed, islands);

    /// <summary>The island slot at a tile, or <see cref="Unknown"/>. Bounds-safe.</summary>
    public byte At(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return Unknown;
        long bit = ((long)ty * WidthTiles + tx) * 3;
        int by = (int)(bit >> 3), sh = (int)(bit & 7);
        int v = _packed[by] >> sh;
        if (sh > 5) v |= _packed[by + 1] << (8 - sh);
        return (byte)(v & 7);
    }

    /// <summary>True when the two tiles are in DIFFERENT tracked islands, which proves no path can exist.
    /// False means "not proven" -- either they share an island, or one of them is Unknown and the cache simply has
    /// nothing to say. Deliberately asymmetric: this may only ever be used to SKIP a doomed search, never to
    /// conclude that a route exists.</summary>
    public bool DefinitelyUnreachable(int ax, int ay, int bx, int by)
    {
        byte a = At(ax, ay), b = At(bx, by);
        if (a == Unknown || b == Unknown) return false;
        return a != b;
    }

    /// <summary>Build the plane directly from a grid, for when no cache file is present.
    ///
    /// This is why the feature needs no deployment artifact. The .shbd files are BYO game data mounted at runtime
    /// and the reference tree is read-only, so shipping precomputed caches alongside them is awkward and shipping
    /// them in the repo would be committing derived game data. Building costs one flood fill -- 50-350ms for the
    /// map sizes in this game, once per map per process -- which is less than a SINGLE avoidable A* on the same
    /// map, and the answer is then reused for the life of the process.
    ///
    /// Ranking and slot assignment match the offline builder exactly, so a cached and a built plane are
    /// interchangeable.</summary>
    public static IslandMap Build(BlockGrid grid, int minIslandTiles = 100)
    {
        int W = grid.WidthTiles, H = grid.HeightTiles;
        var comp = new int[W * H];
        var sizes = new List<int> { 0 };
        var boxes = new List<(int, int, int, int)> { (0, 0, 0, 0) };
        var q = new Queue<int>();
        ReadOnlySpan<int> dx = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
        ReadOnlySpan<int> dy = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };
        int n = 0;
        for (int sy = 0; sy < H; sy++)
        for (int sx = 0; sx < W; sx++)
        {
            int si = sy * W + sx;
            if (comp[si] != 0 || !grid.IsWalkableTile(sx, sy)) continue;
            n++; comp[si] = n; q.Enqueue(si);
            int size = 0, mnx = sx, mny = sy, mxx = sx, mxy = sy;
            while (q.Count > 0)
            {
                int cur = q.Dequeue(); size++;
                int cx = cur % W, cy = cur / W;
                if (cx < mnx) mnx = cx; if (cx > mxx) mxx = cx;
                if (cy < mny) mny = cy; if (cy > mxy) mxy = cy;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + dx[k], ny = cy + dy[k];
                    if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                    int ni = ny * W + nx;
                    if (comp[ni] != 0 || !grid.IsWalkableTile(nx, ny)) continue;
                    comp[ni] = n; q.Enqueue(ni);
                }
            }
            sizes.Add(size); boxes.Add((mnx, mny, mxx, mxy));
        }
        var kept = Enumerable.Range(1, n).Where(c => sizes[c] >= minIslandTiles)
                             .OrderByDescending(c => sizes[c]).ToList();
        var slot = new byte[n + 1];
        Array.Fill(slot, Unknown);
        for (int i = 0; i < kept.Count && i < 7; i++) slot[kept[i]] = (byte)i;
        var packed = new byte[((long)W * H * 3 + 7) / 8];
        for (int i = 0; i < W * H; i++)
        {
            byte v = comp[i] == 0 ? Unknown : slot[comp[i]];
            long bit = (long)i * 3;
            int by = (int)(bit >> 3), sh = (int)(bit & 7);
            packed[by] |= (byte)(v << sh);
            if (sh > 5) packed[by + 1] |= (byte)(v >> (8 - sh));
        }
        var islands = new List<(int, int, int, int, uint)>();
        for (int i = 0; i < kept.Count && i < 7; i++)
        {
            var b = boxes[kept[i]];
            islands.Add((b.Item1, b.Item2, b.Item3, b.Item4, (uint)sizes[kept[i]]));
        }
        return new IslandMap(W, H, packed, islands);
    }

    /// <summary>Load "&lt;dir&gt;/&lt;map&gt;.islands", or null if absent/unreadable/not the expected size for this grid.
    /// A missing cache is not an error -- callers fall back to searching, exactly as before.</summary>
    public static IslandMap? Load(string dir, string map, int expectWidth = 0, int expectHeight = 0)
    {
        try
        {
            var path = Path.Combine(dir, map + ".islands");
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'F' || magic[1] != 'I' || magic[2] != 'S' || magic[3] != 'L') return null;
            if (br.ReadUInt16() != 1) return null;                       // version
            int w = br.ReadUInt16(), h = br.ReadUInt16();
            // A cache built against a DIFFERENT .shbd than the one loaded would silently mis-answer every query,
            // so refuse it rather than trust it.
            if (expectWidth > 0 && (w != expectWidth || h != expectHeight)) return null;
            int stored = br.ReadByte();
            br.ReadByte();                                               // reserved
            var islands = new List<(int, int, int, int, uint)>(stored);
            for (int i = 0; i < stored; i++)
                islands.Add((br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt32()));
            int inflated = (int)br.ReadUInt32();
            if (inflated != (int)(((long)w * h * 3 + 7) / 8)) return null;
            var packed = new byte[inflated];
            using var z = new DeflateStream(fs, CompressionMode.Decompress);
            int got = 0;
            while (got < inflated)
            {
                int r = z.Read(packed, got, inflated - got);
                if (r <= 0) break;
                got += r;
            }
            return got == inflated ? new IslandMap(w, h, packed, islands) : null;
        }
        catch { return null; }
    }
}
