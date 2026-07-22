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
        // DYNAMIC DOOR OVERLAY (scenario instances): inside a scenario door's box, the CURRENT door state
        // (open/closed, tracked live from 0x1C0F/0x6C09) fully determines walkability via the .sbi bitmap —
        // it OVERRIDES the static .shbd, because the .shbd is baked all-doors-open and a closed door is a wall
        // the server enforces but the .shbd can't show. This is the missing collision layer that caused the
        // JCQ MOVEFAIL storm. Empty/null on field maps (no .sbi) → zero cost, base behaviour unchanged.
        if (_doorForced is { } df && df.TryGetValue(ty * WidthTiles + tx, out bool doorBlocked))
            return !doorBlocked; // overlay is authoritative within a known-state door box
        if (((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) != 0) return false; // .shbd bit set = blocked
        if (_erode && ErodedBlocked(tx, ty)) return false; // 1-tile inset for instances (edge-mismatch, below)
        // NOTE (2026-07-22): tried intersecting with the .bdt here (walkable = shbd AND bdt) after live Eld
        // evidence that the server blocks a shbd-walkable/bdt-blocked cell. REVERTED: the .bdt at 50u is too
        // coarse — it over-blocks legit quest-mob targets → walkTo UNREACHABLE loops. A correct .bdt-aware
        // fix needs a finer bdt read (sub-50u) + leveler handling of UNREACHABLE (skip target). See tickets.md.
        return true;
    }

    // --- DYNAMIC SCENARIO DOOR COLLISION (2026-07-15). Root fix for the JCQ Job1_Dn01 instance-nav MOVEFAIL
    // storm: the static .shbd is baked with every door OPEN, so the pathfinder saw closed doors (Door02/Door03
    // sealed at instance start, doors that shut behind you, Door4 closing at LightOff) as passable and routed
    // straight into server-enforced walls. The .sbi carries, per door, a tile box + TWO walkability bitmaps
    // (bitmap[doorstate]); applying bitmap[current-state] over each door box makes our collision match the
    // server's exactly — the same door-aware nav the real client runs at 0 MOVEFAIL.
    private DoorCollision? _doorCol;
    // tile index -> is-blocked, for every tile inside a KNOWN-state door box (overlay wins over the .shbd there).
    private Dictionary<int, bool>? _doorForced;
    private string _doorSig = ""; // signature of the last-applied door-state map, to skip redundant rebuilds

    /// <summary>Attach this map's scenario-door collision (from its <c>.sbi</c>). No-op after the first call.
    /// Field maps without a <c>.sbi</c> pass null and keep pure-<c>.shbd</c> behaviour.</summary>
    public void AttachDoors(DoorCollision? doors) => _doorCol ??= doors;

    // --- COMPANION .bdt (server-collision candidate, reverse-engineered 2026-07-21). A 50-unit quadtree
    // walkability grid the server MAY enforce instead of / on top of the finer .shbd. Attached read-only for
    // the "measuring stick" diagnostic (compare our .shbd vs the .bdt at live MOVEFAIL points). Not yet wired
    // into IsWalkableTile — first prove it predicts the server's rejections. Null on flat maps (no .bdt).
    private BdtGrid? _bdt;
    /// <summary>Attach this map's <c>.bdt</c> quadtree collision. No-op after the first call / on null.</summary>
    public void AttachBdt(BdtGrid? bdt) => _bdt ??= bdt;
    /// <summary>True if this map has a <c>.bdt</c> (terrain/hill map).</summary>
    public bool HasBdt => _bdt is not null;
    /// <summary>Is world (x,y) walkable per the <c>.bdt</c> quadtree? Null when the map has no <c>.bdt</c>.</summary>
    public bool? BdtWalkableWorld(uint worldX, uint worldY) => _bdt?.IsWalkableWorld(worldX, worldY);

    /// <summary>True if this grid has scenario-door overlays to apply (an instance map with a <c>.sbi</c>).</summary>
    public bool HasDoors => _doorCol is { Doors.Count: > 0 };

    /// <summary>Apply the CURRENT scenario-door states (name → doorstate byte, 0 closed / 1 open) to the grid,
    /// rebuilding the per-tile door overlay so pathfinding routes exactly like the client. Cheap-skips when the
    /// state map is unchanged. Doors absent from <paramref name="states"/> keep no override (fall through to the
    /// baked <c>.shbd</c>). Invalidates the clearance field (walkability changed).</summary>
    public void SetDoorStates(IReadOnlyDictionary<string, byte> states)
    {
        if (_doorCol is not { } col) return;
        // Signature = doors we have overlays for, in a stable order, with their current state (unknown = 'x').
        var sig = string.Join(",", col.Doors.Select(d => $"{d.Name}:{(states.TryGetValue(d.Name, out var s) ? s : (byte)255)}"));
        if (sig == _doorSig) return;
        _doorSig = sig;

        var forced = new Dictionary<int, bool>();
        foreach (var d in col.Doors)
        {
            if (!states.TryGetValue(d.Name, out var st)) continue; // state unknown → defer to base .shbd
            for (int ly = 0; ly < d.Height; ly++)
            {
                int ty = d.StartY + ly;
                if ((uint)ty >= (uint)HeightTiles) continue;
                for (int lx = 0; lx < d.Width; lx++)
                {
                    int tx = d.StartX + lx;
                    if ((uint)tx >= (uint)WidthTiles) continue;
                    forced[ty * WidthTiles + tx] = d.BlockedLocal(st, lx, ly);
                }
            }
        }
        _doorForced = forced.Count > 0 ? forced : null;
        _clearance = null; // door walkability changed → obstacle-inflation margins must rebuild
    }

    // Raw STATIC .shbd walkability (NO runtime blocks, NO erosion) — the basis for the erosion mask.
    private bool StaticWalk(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles
           && ((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) == 0;

    /// <summary>Raw STATIC <c>.shbd</c> walkability at world (x,y) — the baked map bit ONLY, with NO runtime
    /// MOVEFAIL-poison, NO erosion, NO door overlay. Use this for the measuring-stick diagnostic so learned
    /// runtime blocks don't masquerade as real map walls.</summary>
    public bool IsStaticWalkableWorld(uint worldX, uint worldY)
        => StaticWalk((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));

    // --- 1-TILE EROSION (scenario instances). The client .shbd's walkable boundary is ~1-2 tiles WIDER than
    // the server's collision (proven on Job1_Dn01: the bot hugs a .shbd edge → the server MOVEFAILs that
    // boundary cell → the storm/drift, and the whole-path margin fallback made it edge-hug everywhere).
    // Eroding the walkable set by 1 tile (a walkable tile with ANY blocked/OOB 8-neighbour becomes blocked)
    // insets our paths to sit INSIDE the server's boundary, so the bot stops edge-hugging. Verified the
    // erosion keeps Job1_Dn01 FULLY connected (entry→Kebings→skeletons→Door4→Chiefs). Built from the static
    // .shbd only (stable, computed once); runtime TTL-blocks still layer on top in IsWalkableTile.
    private bool _erode;
    private HashSet<int>? _eroded;
    private bool ErodedBlocked(int tx, int ty) => (_eroded ??= BuildEroded()).Contains(ty * WidthTiles + tx);
    private HashSet<int> BuildEroded()
    {
        var set = new HashSet<int>();
        for (int ty = 0; ty < HeightTiles; ty++)
            for (int tx = 0; tx < WidthTiles; tx++)
            {
                if (!StaticWalk(tx, ty)) continue;
                bool edge = false;
                for (int dy = -1; dy <= 1 && !edge; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!StaticWalk(tx + dx, ty + dy)) { edge = true; break; }
                if (edge) set.Add(ty * WidthTiles + tx);
            }
        return set;
    }
    /// <summary>Enable 1-tile erosion of the walkable area — for scenario-instance maps whose .shbd is wider
    /// than the server collision (stops the bot hugging edges the server rejects). Idempotent; the static
    /// erosion mask is built lazily on first use. Invalidates the clearance field (obstacle inflation was
    /// built on the un-eroded set).</summary>
    public void EnableErosion()
    {
        if (_erode) return;
        _erode = true;
        _clearance = null;
    }
    /// <summary>True if erosion has been enabled on this grid (diagnostics).</summary>
    public bool IsEroded => _erode;

    /// <summary>Unit world-direction from (worldX,worldY) toward the NEAREST blocked/OOB tile within
    /// ~<paramref name="radiusTiles"/> tiles, or null if none near. The bot is wedged against that wall;
    /// walking PERPENDICULAR to this (±90°) slides ALONG the wall to unstick (operator 2026-07-13). Uses
    /// the STATIC .shbd (the real geometry), not runtime/eroded blocks.</summary>
    public (double dx, double dy)? NearestBlockedDir(uint worldX, uint worldY, int radiusTiles = 8)
    {
        var (cx, cy) = WorldToTile(worldX, worldY);
        int bestD2 = int.MaxValue, bx = 0, by = 0; bool found = false;
        for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (StaticWalk(cx + dx, cy + dy)) continue; // walkable → not a wall
                int d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; bx = dx; by = dy; found = true; }
            }
        if (!found) return null;
        double len = Math.Sqrt(bx * bx + by * by);
        return (bx / len, by / len);
    }

    // Runtime "server-blocked" tiles LEARNED from MOVEFAIL: the SHBD says a tile is walkable but the
    // server rejected a move into it (off-grid obstacle, a scale/edge mismatch, a dynamic block). We add
    // it here so the pathfinder routes AROUND it instead of re-issuing the same rejected step forever
    // (the MOVEFAIL-resync freeze). Per-map + cached, so all bots on the map benefit. Adapts our model to
    // the server's truth rather than papering over the stuck with a retry.
    // tile index -> EXPIRY tick (Environment.TickCount64). long.MaxValue = permanent (field obstacles);
    // a finite expiry = a TEMPORARY block (a scenario-instance cell the server rejected that may be a
    // DYNAMIC scenario door — it must auto-clear so the bot can path through once the door opens, instead
    // of the permanent-block grid-poison that bricked the JCQ instance).
    private Dictionary<int, long>? _rtBlocked;
    private readonly object _rtLock = new();
    private bool RtBlocked(int tx, int ty)
    {
        if (_rtBlocked is null) return false;
        int key = ty * WidthTiles + tx;
        lock (_rtLock)
        {
            if (!_rtBlocked.TryGetValue(key, out var expiry)) return false;
            if (expiry > Environment.TickCount64) return true;
            _rtBlocked.Remove(key); // expired → forget it (the dynamic block, e.g. a reopened door, is gone)
            _clearance = null;      // geometry changed → re-inflate obstacle margins on next use
            return false;
        }
    }
    /// <summary>Mark a tile PERMANENTLY server-blocked (learned from a MOVEFAIL on a normal map). Idempotent;
    /// invalidates the clearance field so obstacle inflation re-forms around the new block on next use.</summary>
    public void MarkBlocked(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return;
        bool isNew;
        lock (_rtLock) { _rtBlocked ??= new(); isNew = !_rtBlocked.ContainsKey(ty * WidthTiles + tx); _rtBlocked[ty * WidthTiles + tx] = long.MaxValue; }
        if (isNew) _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
    }
    /// <summary>Mark a tile server-blocked with a short TTL — for a SCENARIO INSTANCE MOVEFAIL, where the
    /// rejected cell is often a dynamic scenario door (KQ_Gate4) that opens later. The block lets the
    /// pathfinder route AROUND the obstacle now, and auto-expires so a reopened door becomes walkable again
    /// (no permanent grid-poison). Re-hitting the tile refreshes/extends the TTL. Never downgrades a permanent
    /// block to temporary.</summary>
    public void MarkBlockedTtl(int tx, int ty, int ttlMs)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return;
        long expiry = Environment.TickCount64 + ttlMs;
        bool isNew;
        lock (_rtLock)
        {
            _rtBlocked ??= new();
            int key = ty * WidthTiles + tx;
            isNew = !_rtBlocked.ContainsKey(key);
            if (!_rtBlocked.TryGetValue(key, out var cur) || (cur != long.MaxValue && expiry > cur)) _rtBlocked[key] = expiry;
        }
        if (isNew) _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
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
    public bool IsPathable(int tx, int ty, double margin)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (margin <= 0) return IsWalkableTile(tx, ty);
        // clearance c means the nearest blocked tile is Chebyshev-distance c away; we require
        // every tile within `margin` to be walkable, i.e. nearest blocked is farther than margin.
        return Clearance()[ty * WidthTiles + tx] > margin;
    }

    /// <summary>Chebyshev distance (in tiles, capped at 63) from tile (tx,ty) to the nearest blocked/OOB
    /// tile — i.e. how far this cell is from the nearest wall. 0 = OOB/blocked, 1 = wall-adjacent, higher =
    /// more centered. The pathfinder adds a cost that rises as this FALLS, so routes ride the corridor's
    /// high-clearance spine instead of hugging a .shbd edge the server MOVEFAILs (tick 41 tight-corridor centering).</summary>
    public int ClearanceAt(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles ? Clearance()[ty * WidthTiles + tx] : 0;

    /// <summary>World coordinate of a tile's centre (for issuing move packets).</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx + 0.5) * WorldPerTile), (uint)((ty + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));
}
