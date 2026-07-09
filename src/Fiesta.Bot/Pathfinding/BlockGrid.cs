namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// A map's walkability grid, loaded from a server <c>.shbd</c> (Shine Block Data)
/// file under <c>9Data/Shine/BlockInfo/&lt;Map&gt;.shbd</c> (BYO at runtime).
///
/// Format (recovered + calibrated live 2026-06-11): 8-byte header = two LE u32
/// [bytesPerRow, height]; then <c>height</c> rows of <c>bytesPerRow</c> bytes,
/// <b>1 bit per tile</b>, LSB-first within a byte. <b>bit 0 = walkable, bit 1 =
/// blocked/void.</b> The playable map is a small region (~7% of tiles) inside a
/// large void of bit-1 padding.
///
/// <para>World↔tile is a plain linear scale with <b>no offset and no Y-flip</b>:
/// a Shine map-unit is 50 world units and the grid stores 8 tiles per map-unit, so
/// each tile spans 50/8 = <see cref="WorldPerTile"/> = 6.25 world units. The grid
/// therefore covers world [0, tiles·6.25] (Eld: 4096·6.25 = 25600 = 512 map-units ·
/// 50). Verified exactly against Eld live — every gate and landmark lines up.
///   tileX = worldX / 6.25
///   tileY = worldY / 6.25
///   walkable = ((row[tileX >> 3] >> (tileX &amp; 7)) &amp; 1) == 0
/// </para>
/// </summary>
public sealed class BlockGrid
{
    /// <summary>World units per tile (50 world per map-unit ÷ 8 tiles per map-unit).</summary>
    public const double WorldPerTile = 6.25;

    private readonly byte[] _data;
    private readonly int _bytesPerRow;

    public int WidthTiles { get; }
    public int HeightTiles { get; }

    private BlockGrid(byte[] data, int bytesPerRow, int height)
    {
        _data = data;
        _bytesPerRow = bytesPerRow;
        HeightTiles = height;
        WidthTiles = bytesPerRow * 8;
    }

    public static BlockGrid Load(string shbdPath)
    {
        var b = File.ReadAllBytes(shbdPath);
        if (b.Length < 8) throw new InvalidDataException($"{shbdPath}: too short for a .shbd header");
        var bytesPerRow = BitConverter.ToInt32(b, 0);
        var height = BitConverter.ToInt32(b, 4);
        var need = 8L + (long)bytesPerRow * height;
        if (bytesPerRow <= 0 || height <= 0 || b.Length < need)
            throw new InvalidDataException($"{shbdPath}: bad .shbd dims {bytesPerRow}x{height} for {b.Length} bytes");
        return new BlockGrid(b, bytesPerRow, height);
    }

    /// <summary>Is the tile at world (x,y) walkable? Out-of-bounds = blocked.</summary>
    public bool IsWalkableWorld(uint worldX, uint worldY)
        => IsWalkableTile((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));

    public bool IsWalkableTile(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (RtBlocked(tx, ty)) return false; // server-rejected tile (learned from MOVEFAIL)
        var bytePos = 8 + ty * _bytesPerRow + (tx >> 3);
        return ((_data[bytePos] >> (tx & 7)) & 1) == 0; // bit 0 = walkable
    }

    // Runtime "server-blocked" tiles LEARNED from MOVEFAIL: the SHBD says a tile is walkable but the
    // server rejected a move into it (off-grid obstacle, a scale/edge mismatch, a dynamic block). We add
    // it here so the pathfinder routes AROUND it instead of re-issuing the same rejected step forever
    // (the MOVEFAIL-resync freeze). Per-map + cached, so all bots on the map benefit. Adapts our model to
    // the server's truth rather than papering over the stuck with a retry.
    private HashSet<int>? _rtBlocked;
    private readonly object _rtLock = new();
    private bool RtBlocked(int tx, int ty)
    {
        if (_rtBlocked is null) return false;
        lock (_rtLock) return _rtBlocked.Contains(ty * WidthTiles + tx);
    }
    /// <summary>Mark a tile server-blocked (learned from a MOVEFAIL). Idempotent; invalidates the
    /// clearance field so obstacle inflation re-forms around the new block on next use.</summary>
    public void MarkBlocked(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return;
        lock (_rtLock) { if (!(_rtBlocked ??= new()).Add(ty * WidthTiles + tx)) return; } // already known → no-op
        _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
    }
    /// <summary>Count of learned server-blocked tiles (diagnostics).</summary>
    public int RuntimeBlockedCount { get { lock (_rtLock) return _rtBlocked?.Count ?? 0; } }

    /// <summary>Forget all MOVEFAIL-learned runtime blocks. Called when a pathfind fails on the
    /// runtime-augmented grid even though the base <c>.shbd</c> is connected — the accumulated learned
    /// blocks have wrongly SEVERED a reachable route (grid-poison that bricked cross-map travel). Clearing
    /// lets the bot re-path over the true static geometry and re-learn only obstacles it actually hits.</summary>
    public void ClearRuntimeBlocked()
    {
        lock (_rtLock) { if (_rtBlocked is null || _rtBlocked.Count == 0) return; _rtBlocked.Clear(); }
        _clearance = null; // obstacle inflation was built around the (now-gone) blocks → rebuild
    }

    // --- Obstacle inflation (P0 2026-06-30: paths hugged obstacle edges → the straight-run
    // MOVERUN between waypoints clipped an object corner → server MOVEFAIL → bot stuck). We
    // keep the path a few tiles clear of any blocked tile. A tile is 6.25 world units (~10 cm),
    // so a 2-3 tile margin is only ~20-30 cm — plenty to stop corner-clipping without closing
    // narrow gates. Implemented as a Chebyshev distance-to-nearest-blocked field, computed once
    // per grid and cached: clearance[t] = how far tile t is from the nearest blocked/OOB tile.

    private byte[]? _clearance;
    private readonly object _clearanceLock = new();
    private const byte ClearanceCap = 63; // margins are tiny; cap keeps it a byte

    private byte[] Clearance()
    {
        if (_clearance is { } c) return c;
        lock (_clearanceLock)
        {
            if (_clearance is { } c2) return c2;
            int W = WidthTiles, H = HeightTiles;
            var dist = new byte[W * H];
            // seed: blocked = 0, walkable = cap
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    dist[y * W + x] = IsWalkableTile(x, y) ? ClearanceCap : (byte)0;
            // forward pass — pull from already-visited neighbours (and OOB = blocked at borders)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x == 0 || y == 0 || x == W - 1) best = Math.Min(best, 1); // touches OOB
                    if (x > 0) best = Math.Min(best, dist[i - 1] + 1);
                    if (y > 0) best = Math.Min(best, dist[i - W] + 1);
                    if (x > 0 && y > 0) best = Math.Min(best, dist[i - W - 1] + 1);
                    if (x < W - 1 && y > 0) best = Math.Min(best, dist[i - W + 1] + 1);
                    dist[i] = (byte)best;
                }
            // backward pass — pull from the other four neighbours
            for (int y = H - 1; y >= 0; y--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = y * W + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x == W - 1 || y == H - 1 || x == 0) best = Math.Min(best, 1); // touches OOB
                    if (x < W - 1) best = Math.Min(best, dist[i + 1] + 1);
                    if (y < H - 1) best = Math.Min(best, dist[i + W] + 1);
                    if (x < W - 1 && y < H - 1) best = Math.Min(best, dist[i + W + 1] + 1);
                    if (x > 0 && y < H - 1) best = Math.Min(best, dist[i + W - 1] + 1);
                    dist[i] = (byte)best;
                }
            _clearance = dist;
            return dist;
        }
    }

    /// <summary>Walkable AND at least <paramref name="margin"/> tiles clear of the nearest
    /// blocked/out-of-bounds tile (Chebyshev). <paramref name="margin"/> ≤ 0 is just
    /// <see cref="IsWalkableTile"/>. Used by the pathfinder to keep routes off obstacle edges.</summary>
    public bool IsPathable(int tx, int ty, int margin)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (margin <= 0) return IsWalkableTile(tx, ty);
        // clearance c means the nearest blocked tile is Chebyshev-distance c away; we require
        // every tile within `margin` to be walkable, i.e. nearest blocked is farther than margin.
        return Clearance()[ty * WidthTiles + tx] > margin;
    }

    /// <summary>World coordinate of a tile's centre (for issuing move packets).</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx + 0.5) * WorldPerTile), (uint)((ty + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));
}
