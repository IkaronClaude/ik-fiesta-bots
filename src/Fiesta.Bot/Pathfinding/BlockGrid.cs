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

    // ── SHBD 1-TILE ORIGIN SHIFT (operator + godmode wall-hug trace, 2026-07-22) ──────────────────────────
    // The .shbd blocked-bit at array index (i,j) physically represents the world cell one tile OVER in each
    // axis: its true span is [(i-1)*6.25, i*6.25), NOT [i*6.25, (i+1)*6.25). Reading it the naive way put every
    // wall ~1 tile off, so the pathfinder routed the bot into cells the server rejects (the recurring hilly-map
    // MOVEFAIL wedge). PROVEN: overlaying an 805-point godmode wall-hug trace on RouVal02.shbd, the naive read
    // has 98.3% of the (walkable, bot-stood-there) trace points landing inside "blocked" tiles; shifting the
    // world→tile lookup by +1 in BOTH axes drops that to 0.4% (a clean corner-convention off-by-one; the
    // "non-uniform 4–25u offset" chased earlier was an artifact of variable step size + the server not
    // grid-aligning you on a MOVEFAIL). The fix lives entirely in the world↔tile conversion: index (i,j) now
    // means world-centre ((i-0.5)*6.25), so WorldToTile adds +1 and TileToWorld subtracts a half-tile origin.
    // The pathfinder still works purely in index space; only the physical placement of indices moves.
    private const int ShbdTileShift = 1;

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
        => IsWalkableTile((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);

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

    // Door states from two sources, MERGED into the overlay (packet WINS over learned):
    //   • _packetDoorStates  — scenario-instance doors, seeded/updated by 0x1C0F/0x6C09 (authoritative).
    //   • _learnedDoorStates — FIELD .sbi doors whose state is NEVER sent to a late-joiner (the Eld "Puzzle God":
    //     even the REAL client can't know a door is already-closed on entry — it also tries and MOVEFAILs).
    //     Learned from MOVEFAILs inside the door box (see NoteMoveFailInSbiDoor); reset on map re-entry.
    private Dictionary<string, byte> _packetDoorStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte> _learnedDoorStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apply the CURRENT scenario-door states from PACKETS (name → doorstate byte, 0 closed / 1 open) —
    /// wired to 0x1C0F/0x6C09. Merged with MOVEFAIL-learned field-door states; packet states win.</summary>
    public void SetDoorStates(IReadOnlyDictionary<string, byte> states)
    {
        if (_doorCol is null) return;
        _packetDoorStates = new Dictionary<string, byte>(states, StringComparer.OrdinalIgnoreCase);
        RebuildDoorOverlay();
    }

    // Rebuild the per-tile door overlay from the merged door states (packet ?? learned). The .sbi door box tiles
    // are in raw map-tile coords; the .shbd read is +ShbdTileShift, so the overlay is placed at StartX+lx+shift to
    // line up with IsWalkableTile's shifted index space (same correction as WorldToTile). Cheap-skips on no change.
    private void RebuildDoorOverlay()
    {
        if (_doorCol is not { } col) return;
        byte StateOf(string name) =>
            _packetDoorStates.TryGetValue(name, out var ps) ? ps :
            _learnedDoorStates.TryGetValue(name, out var ls) ? ls : (byte)255;
        var sig = string.Join(",", col.Doors.Select(d => $"{d.Name}:{StateOf(d.Name)}"));
        if (sig == _doorSig) return;
        _doorSig = sig;

        var forced = new Dictionary<int, bool>();
        foreach (var d in col.Doors)
        {
            byte st = StateOf(d.Name);
            if (st == 255) continue; // state unknown → defer to base .shbd
            for (int ly = 0; ly < d.Height; ly++)
            {
                int ty = d.StartY + ly + ShbdTileShift;
                if ((uint)ty >= (uint)HeightTiles) continue;
                for (int lx = 0; lx < d.Width; lx++)
                {
                    int tx = d.StartX + lx + ShbdTileShift;
                    if ((uint)tx >= (uint)WidthTiles) continue;
                    forced[ty * WidthTiles + tx] = d.BlockedLocal(st, lx, ly);
                }
            }
        }
        _doorForced = forced.Count > 0 ? forced : null;
        _clearance = null; // door walkability changed → obstacle-inflation margins must rebuild
    }

    // ── FIELD .sbi DOOR STATE LEARNED FROM MOVEFAIL (operator-confirmed 2026-07-22) ────────────────────────────
    // The Eld "Puzzle God" walls a courtyard by closing an Eld.sbi door — but that state is NOT sent to a client
    // that enters after the door closed (verified: no 0x1C0F/0x6C09, and the REAL client ALSO bounces off it). So
    // the ONLY signal is the server's MOVEFAIL. Strategy: on a MOVEFAIL inside a door box, POISON that tile; once
    // >SbiClosedThreshold DISTINCT tiles inside ONE door have MOVEFAILed, the door is clearly CLOSED → mark the
    // WHOLE door state0 so the pathfinder routes around the courtyard (through the state0 opening) instead of
    // bouncing tile-by-tile. If the door is OPEN we simply never accumulate the threshold (few/no MOVEFAILs there).
    // MUST reset on map re-entry (ResetDoorLearning) — the door may have opened while we were off the map.
    public enum SbiMoveFail { NotInDoor, Poisoned, DoorClosed }
    public const int SbiClosedThreshold = 6;
    private readonly Dictionary<string, HashSet<int>> _sbiFailTiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record a MOVEFAIL against the field .sbi doors. CRUCIAL (operator 2026-07-22): a MOVEFAIL's
    /// <paramref name="fromX"/>,<paramref name="fromY"/> is where the server SNAPPED US BACK — the wall's OUTER
    /// face, usually still OUTSIDE the door box. The wall itself lies along the attempted segment toward
    /// <paramref name="toX"/>,<paramref name="toY"/> (which lands INSIDE the door). So SWEEP the whole from→to
    /// segment and act on the first state0-wall tile it crosses. Returns NotInDoor (fall through to the normal
    /// poison gate), Poisoned (tile poisoned, keep trying), or DoorClosed (the whole door is now walled).</summary>
    public SbiMoveFail NoteMoveFailInSbiDoor(uint fromX, uint fromY, uint toX, uint toY)
    {
        if (_doorCol is null) return SbiMoveFail.NotInDoor;
        double dx = (double)toX - fromX, dy = (double)toY - fromY;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.1) return TryDoorMoveFailAt(toX, toY);
        for (double t = 0; t <= len; t += 3.0) // sample every ~3u — fine enough to catch a single-tile wall
        {
            var r = TryDoorMoveFailAt((uint)Math.Max(0, fromX + dx / len * t), (uint)Math.Max(0, fromY + dy / len * t));
            if (r != SbiMoveFail.NotInDoor) return r;
        }
        return TryDoorMoveFailAt(toX, toY);
    }

    // One sampled point of the swept MOVEFAIL segment: if world (wx,wy) is a state0-WALL tile of a field door
    // (blocked when closed, open when open — i.e. the actual courtyard wall, not the interior/edge), poison it and
    // count it; once >SbiClosedThreshold distinct wall tiles of one door have failed, mark the WHOLE door closed.
    private SbiMoveFail TryDoorMoveFailAt(uint wx, uint wy)
    {
        if (_doorCol is not { } col) return SbiMoveFail.NotInDoor;
        foreach (var d in col.Doors)
        {
            double x0 = d.StartX * WorldPerTile, x1 = (d.EndX + 1) * WorldPerTile;
            double y0 = d.StartY * WorldPerTile, y1 = (d.EndY + 1) * WorldPerTile;
            if (wx < x0 || wx >= x1 || wy < y0 || wy >= y1) continue; // not in this door's box
            if (_learnedDoorStates.TryGetValue(d.Name, out var known) && known == 0) return SbiMoveFail.DoorClosed;
            if (_packetDoorStates.ContainsKey(d.Name)) continue; // packet-authoritative (instance) — don't learn this door
            var (tx, ty) = WorldToTile(wx, wy);
            int lx = tx - d.StartX - ShbdTileShift, ly = ty - d.StartY - ShbdTileShift; // raw .sbi-local bitmap index
            if ((uint)lx >= (uint)d.Width || (uint)ly >= (uint)d.Height) continue;
            if (!d.BlockedLocal(0, lx, ly) || d.BlockedLocal(1, lx, ly)) continue; // only a state0-only WALL tile counts
            if (!_sbiFailTiles.TryGetValue(d.Name, out var set)) { set = new HashSet<int>(); _sbiFailTiles[d.Name] = set; }
            set.Add(ty * WidthTiles + tx);
            if (set.Count > SbiClosedThreshold)
            {
                _learnedDoorStates[d.Name] = 0; // CLOSED — apply the whole state0 wall
                RebuildDoorOverlay();
                return SbiMoveFail.DoorClosed;
            }
            MarkBlocked(tx, ty); // individual poison; re-path avoids it, exploring more of the wall
            return SbiMoveFail.Poisoned;
        }
        return SbiMoveFail.NotInDoor;
    }

    /// <summary>Reset MOVEFAIL-learned field-door state on MAP RE-ENTRY — the door may have opened while we were
    /// off the map, so a stale "closed" would wall a now-open courtyard. Also clears runtime poison (transient,
    /// re-learned per visit). Packet-driven instance doors are untouched (BUILDDOOR re-seeds them on entry).</summary>
    public void ResetDoorLearning()
    {
        _learnedDoorStates.Clear();
        _sbiFailTiles.Clear();
        ClearRuntimeBlocked();
        RebuildDoorOverlay();
    }

    // Raw STATIC .shbd walkability (NO runtime blocks, NO erosion) — the basis for the erosion mask.
    private bool StaticWalk(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles
           && ((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) == 0;

    /// <summary>Raw STATIC <c>.shbd</c> walkability at world (x,y) — the baked map bit ONLY, with NO runtime
    /// MOVEFAIL-poison, NO erosion, NO door overlay. Use this for the measuring-stick diagnostic so learned
    /// runtime blocks don't masquerade as real map walls.</summary>
    public bool IsStaticWalkableWorld(uint worldX, uint worldY)
        => StaticWalk((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);

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

    /// <summary>World coordinate of a tile's centre (for issuing move packets). Index (i,j) means world-centre
    /// ((i-0.5)*6.25) per the ShbdTileShift correction, so the inverse of WorldToTile round-trips.</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx - ShbdTileShift + 0.5) * WorldPerTile), (uint)((ty - ShbdTileShift + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);
}
