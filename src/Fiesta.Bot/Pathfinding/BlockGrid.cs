namespace Fiesta.Bot.Pathfinding;

public sealed class BlockGrid
{
    /// <summary>World units per tile (50 world per map-unit ÷ 8 tiles per map-unit)</summary>
    public const double WorldPerTile = 6.25;

    // SHBD 1-TILE ORIGIN SHIFT (operator + godmode wall-hug trace, 2026-07-22) ────────────────────────── The .shbd blocked-bit at array index (i,j) physically represents the world cell one tile OVER in eac…
    private const int ShbdTileShift = 1;
    /// <summary>The same shift, for code outside this class that must place `.sbi` door boxes on the tile grid --
    /// the door bitmaps are indexed like the .shbd and inherit its one-tile origin offset.</summary>
    public const int ShbdTileShiftPublic = ShbdTileShift;

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

    /// <summary>Is the tile at world (x,y) walkable?</summary>
    public bool IsWalkableWorld(uint worldX, uint worldY)
        => IsWalkableTile((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);

    public bool IsWalkableTile(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (RtBlocked(tx, ty)) return false; // server-rejected tile (learned from MOVEFAIL)
        // DYNAMIC DOOR OVERLAY (scenario instances): inside a scenario door's box, the CURRENT door state (open/closed,…
        if (_doorForced is { } df && df.TryGetValue(ty * WidthTiles + tx, out bool doorBlocked))
            return !doorBlocked; // overlay is authoritative within a known-state door box
        if (((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) != 0) return false; // .shbd bit set = blocked
        if (_erode && ErodedBlocked(tx, ty)) return false; // 1-tile inset for instances (edge-mismatch, below)
        // NOTE (2026-07-22): tried intersecting with the .bdt here (walkable = shbd AND bdt) after live Eld evidence tha…
        return true;
    }

    // --- DYNAMIC SCENARIO DOOR COLLISION (2026-07-15)
    private DoorCollision? _doorCol;
    // tile index -> is-blocked, for every tile inside a KNOWN-state door box (overlay wins over the .shbd there)
    private Dictionary<int, bool>? _doorForced;
    private string _doorSig = ""; // signature of the last-applied door-state map, to skip redundant rebuilds

    /// <summary>Attach this map's scenario-door collision (from its .sbi )</summary>
    public void AttachDoors(DoorCollision? doors) => _doorCol ??= doors;

    /// <summary>Which `.sbi` doors are shut RIGHT NOW, as a predicate over door index, or null when this map has no
    /// doors. Reads the same live state `SetDoorStates` receives, so the mesh routes against what the server has
    /// actually told us rather than against the all-doors-open `.shbd` it was carved from.
    /// State 255 means "not yet heard"; treated as OPEN, since refusing to route through every unheard-of door
    /// would strand the bot on entry to any instance before its door broadcasts arrive.</summary>
    public Func<int, bool>? DoorClosedPredicate()
    {
        var col = _doorCol;
        if (col is null) return null;
        var states = _packetDoorStates;
        return di =>
        {
            if ((uint)di >= (uint)col.Doors.Count) return false;
            var name = col.Doors[di].Name;
            if (states.TryGetValue(name, out var st)) return st == 0;
            if (_learnedDoorStates.TryGetValue(name, out var ls)) return ls == 0;
            return false;
        };
    }

    /// <summary>Dynamic door collision for this map, from its `.sbi`.</summary>
    /// <summary>This map's convex decomposition, built once and SHARED by every bot on the map -- the grid itself
    /// is cached per map, so hanging the mesh here means one decomposition serves the whole fleet rather than one
    /// per bot. Lazy so that two bots entering a map at the same moment cannot both build it (observed live with
    /// the island maps: RouCos02 built twice, 688ms and 693ms, one result discarded).</summary>
    private Lazy<NavMesh>? _mesh;
    public NavMesh Mesh(double margin = PathFinder.DefaultMargin)
    {
        var l = _mesh;
        if (l is null || Math.Abs(l.Value.Margin - margin) > 1e-9)
        {
            l = new Lazy<NavMesh>(() => NavMesh.Build(this, margin, _doorCol), LazyThreadSafetyMode.ExecutionAndPublication);
            _mesh = l;
        }
        return l.Value;
    }
    /// <summary>Seed the mesh from a cache file so the first request does not pay for the decomposition.</summary>
    public void AttachMesh(NavMesh? mesh)
    {
        if (mesh is not null) _mesh ??= new Lazy<NavMesh>(mesh);
    }

    // --- COMPANION .bdt (server-collision candidate, reverse-engineered 2026-07-21)
    private BdtGrid? _bdt;
    /// <summary>Attach this map's .bdt quadtree collision</summary>
    public void AttachBdt(BdtGrid? bdt) => _bdt ??= bdt;
    /// <summary>True if this map has a .bdt (terrain/hill map)</summary>
    public bool HasBdt => _bdt is not null;

    /// <summary>Resident bytes: the packed grid plus the clearance map, which is EIGHT TIMES the grid
    /// (one byte per tile vs one bit) and so dominates -- a 7.2MB .shbd costs ~58MB once inflated.</summary>
    public long ApproxBytes => _data.LongLength + (_clearance?.LongLength ?? 0);
    public bool ClearanceBuilt => _clearance is not null;
    /// <summary>Is world (x,y) walkable per the .bdt quadtree?</summary>
    public bool? BdtWalkableWorld(uint worldX, uint worldY) => _bdt?.IsWalkableWorld(worldX, worldY);

    /// <summary>True if this grid has scenario-door overlays to apply (an instance map with a .sbi )</summary>
    public bool HasDoors => _doorCol is { Doors.Count: > 0 };

    // Door states from two sources, MERGED into the overlay (packet WINS over learned): • _packetDoorStates — scenar…
    private Dictionary<string, byte> _packetDoorStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte> _learnedDoorStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apply the CURRENT scenario-door states from PACKETS (name → doorstate byte, 0 closed / 1 open) — wired to 0x1C…</summary>
    public void SetDoorStates(IReadOnlyDictionary<string, byte> states)
    {
        if (_doorCol is null) return;
        _packetDoorStates = new Dictionary<string, byte>(states, StringComparer.OrdinalIgnoreCase);
        RebuildDoorOverlay();
    }

    // Rebuild the per-tile door overlay from the merged door states (packet
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

    // FIELD .sbi DOOR STATE LEARNED FROM MOVEFAIL (operator-confirmed 2026-07-22) ──────────────────────────── The E…
    public enum SbiMoveFail { NotInDoor, Poisoned, DoorClosed }
    public const int SbiClosedThreshold = 6;
    /// <summary>Total MOVEFAILs against one door's wall tiles before we call it CLOSED, regardless of how many distinct tiles…</summary>
    public const int SbiClosedFailCountThreshold = 12;
    private readonly Dictionary<string, HashSet<int>> _sbiFailTiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sbiFailCount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record a MOVEFAIL against the field .sbi doors</summary>
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

    // One sampled point of the swept MOVEFAIL segment: if world (wx,wy) is a state0-WALL tile of a field door (block…
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
            var fails = _sbiFailCount[d.Name] = _sbiFailCount.GetValueOrDefault(d.Name) + 1;
            // Either signal proves the door is shut: six DIFFERENT wall tiles refused us, or we bounced off this wall enough…
            if (set.Count > SbiClosedThreshold || fails > SbiClosedFailCountThreshold)
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

    public const int PuzzleMobBoard = 15035;   // PzlBoard_4x4 — the empty puzzle frame
    public static bool IsPuzzlePieceMob(int mobId) => mobId == PuzzleMobBoard;

    /// <summary>Mark any field .sbi door CLOSED that currently contains a puzzle-piece entity</summary>
    public IReadOnlyList<string> NotePuzzleEntities(IEnumerable<(uint X, uint Y, int MobId)> entities)
    {
        if (_doorCol is not { } col) return Array.Empty<string>();
        List<string>? closed = null;
        foreach (var e in entities)
        {
            if (!IsPuzzlePieceMob(e.MobId)) continue;
            foreach (var d in col.Doors)
            {
                if (_packetDoorStates.ContainsKey(d.Name)) continue;      // instance doors are packet-authoritative
                if (_learnedDoorStates.TryGetValue(d.Name, out var k) && k == 0) continue;  // already known closed
                double x0 = d.StartX * WorldPerTile, x1 = (d.EndX + 1) * WorldPerTile;
                double y0 = d.StartY * WorldPerTile, y1 = (d.EndY + 1) * WorldPerTile;
                if (e.X < x0 || e.X >= x1 || e.Y < y0 || e.Y >= y1) continue;
                _learnedDoorStates[d.Name] = 0;                            // CLOSED — apply the whole state0 wall
                (closed ??= new List<string>()).Add(d.Name);
            }
        }
        if (closed is not null) RebuildDoorOverlay();
        return (IReadOnlyList<string>?)closed ?? Array.Empty<string>();
    }

    /// <summary>Reset MOVEFAIL-learned field-door state on MAP RE-ENTRY — the door may have opened while we were off the map,…</summary>
    public void ResetDoorLearning()
    {
        _learnedDoorStates.Clear();
        _sbiFailTiles.Clear();
        _sbiFailCount.Clear();
        ClearRuntimeBlocked();
        RebuildDoorOverlay();
    }

    // Raw STATIC .shbd walkability (NO runtime blocks, NO erosion) — the basis for the erosion mask
    private bool StaticWalk(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles
           && ((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) == 0;

    /// <summary>Raw STATIC .shbd walkability at world (x,y) — the baked map bit ONLY, with NO runtime MOVEFAIL-poison, NO eros…</summary>
    public bool IsStaticWalkableWorld(uint worldX, uint worldY)
        => StaticWalk((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);

    // --- 1-TILE EROSION (scenario instances)
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
    /// <summary>Enable 1-tile erosion of the walkable area — for scenario-instance maps whose .shbd is wider than the server c…</summary>
    public void EnableErosion()
    {
        if (_erode) return;
        _erode = true;
        _clearance = null;
    }
    /// <summary>True if erosion has been enabled on this grid (diagnostics)</summary>
    public bool IsEroded => _erode;

    /// <summary>Unit world-direction from (worldX,worldY) toward the NEAREST blocked/OOB tile within ~ tiles, or null if none…</summary>
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

    // Runtime "server-blocked" tiles LEARNED from MOVEFAIL: the SHBD says a tile is walkable but the server rejected…
    private Dictionary<int, long>? _rtBlocked;

    // HOW MANY TIMES THE SERVER HAS REFUSED EACH CELL. A cell MOVEFAILed once may well be a desync artefact; a cell
    // refused again and again is a WALL the .shbd does not know about (a closed scenario door, most often), and it
    // is the single most valuable thing the nav has learned. The wedge recovery used to delete it along with
    // everything else, which is what made the wedge permanent -- see ClearRuntimeBlocked.
    private Dictionary<int, int>? _rtHits;
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
    /// <summary>Mark a tile PERMANENTLY server-blocked (learned from a MOVEFAIL on a normal map)</summary>
    public void MarkBlocked(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return;
        bool isNew;
        lock (_rtLock) { _rtBlocked ??= new(); _rtHits ??= new(); int k = ty * WidthTiles + tx; isNew = !_rtBlocked.ContainsKey(k); _rtBlocked[k] = long.MaxValue; _rtHits[k] = _rtHits.TryGetValue(k, out var hp) ? hp + 1 : 1; }
        if (isNew) _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
    }
    /// <summary>Mark a tile server-blocked with a short TTL — for a SCENARIO INSTANCE MOVEFAIL, where the rejected cell is oft…</summary>
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
            _rtHits ??= new();
            _rtHits[key] = _rtHits.TryGetValue(key, out var h) ? h + 1 : 1;   // re-confirmed by another refusal
        }
        if (isNew) _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
    }
    /// <summary>Count of learned server-blocked tiles (diagnostics)</summary>
    public int RuntimeBlockedCount { get { lock (_rtLock) return _rtBlocked?.Count ?? 0; } }

    /// <summary>Forget MOVEFAIL-learned runtime blocks, KEEPING any cell the server has refused at least
    /// <paramref name="keepAtHits"/> times (0 = forget everything, which is what a map load wants).
    /// Returns how many blocks were KEPT.
    /// WHY THE FILTER EXISTS: the wedge recovery clears these on the theory that accumulated blocks have boxed the
    /// bot in. That is a real failure, but the clear was indiscriminate, so it also deleted the cell the server was
    /// refusing RIGHT THEN -- and then re-pathed "on the clean .shbd" straight back into it. Measured on MageFresh
    /// 2026-08-20 12:29 in Job1_Dn01: block cell (337,489), streak to 8, clear, re-path, MOVEFAIL at the same
    /// (2113,3050), forever, with `total 1` never growing because the one real block was wiped every cycle. The
    /// escape hatch was destroying the only state that could produce an escape.</summary>
    public int ClearRuntimeBlocked(int keepAtHits = 0)
    {
        int kept = 0;
        lock (_rtLock)
        {
            if (_rtBlocked is null || _rtBlocked.Count == 0) return 0;
            if (keepAtHits <= 0) { _rtBlocked.Clear(); _rtHits?.Clear(); }
            else
            {
                foreach (var key in _rtBlocked.Keys.ToList())
                {
                    if (_rtHits is not null && _rtHits.TryGetValue(key, out var h) && h >= keepAtHits) { kept++; continue; }
                    _rtBlocked.Remove(key);
                    _rtHits?.Remove(key);
                }
            }
        }
        _clearance = null; // obstacle inflation was built around the (now-gone) blocks → rebuild
        return kept;
    }

    // --- Obstacle inflation (P0 2026-06-30: paths hugged obstacle edges → the straight-run MOVERUN between waypoint…

    // PACKED 2 BITS PER TILE. A byte per tile made the clearance map EIGHT TIMES the packed .shbd (~58MB for a
    // 7.2MB map), which is what pushed the grid cache past the container limit -- and bounding that cache instead
    // made things far worse, because evicting a grid means recomputing this transform (2026-08-18: CPU pinned at
    // 863m/1000m, 67% throttled, the leveler down to one tick a minute).
    // Two bits is exactly enough: IsPathable only ever asks `clearance > margin`, and the margin ladder is
    // {2.0, 1.5, 1.0, 0.5, 0.0} (CoveragePath uses 1), so the only distinctions that exist are 0, 1, 2 and 3+.
    // A saturating Chebyshev transform capped at 3 answers all of them exactly; one bit could not, since >=1,
    // >=2 and >=3 are three different questions. Costs a shift+mask per read and buys back 75% of the memory.
    private byte[]? _clearance;                 // 4 tiles per byte, 2 bits each
    private readonly object _clearanceLock = new();
    private const byte ClearanceCap = 3;        // max representable in 2 bits

    private static byte ClrGet(byte[] a, int i) => (byte)((a[i >> 2] >> ((i & 3) << 1)) & 0x3);
    private static void ClrSet(byte[] a, int i, int v)
    {
        int b = i >> 2, sh = (i & 3) << 1;
        a[b] = (byte)((a[b] & ~(0x3 << sh)) | ((v & 0x3) << sh));
    }

    private byte[] Clearance()
    {
        if (_clearance is { } c) return c;
        lock (_clearanceLock)
        {
            if (_clearance is { } c2) return c2;
            int W = WidthTiles, H = HeightTiles;
            var dist = new byte[(W * H + 3) >> 2];
            // seed: blocked = 0, walkable = cap
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    ClrSet(dist, y * W + x, IsWalkableTile(x, y) ? ClearanceCap : 0);
            // forward pass -- pull from already-visited neighbours (and OOB = blocked at borders)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    int cur = ClrGet(dist, i);
                    if (cur == 0) continue;
                    int best = cur;
                    if (x == 0 || y == 0 || x == W - 1) best = Math.Min(best, 1); // touches OOB
                    if (x > 0) best = Math.Min(best, ClrGet(dist, i - 1) + 1);
                    if (y > 0) best = Math.Min(best, ClrGet(dist, i - W) + 1);
                    if (x > 0 && y > 0) best = Math.Min(best, ClrGet(dist, i - W - 1) + 1);
                    if (x < W - 1 && y > 0) best = Math.Min(best, ClrGet(dist, i - W + 1) + 1);
                    if (best != cur) ClrSet(dist, i, best);
                }
            // backward pass -- pull from the other four neighbours
            for (int y = H - 1; y >= 0; y--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = y * W + x;
                    int cur = ClrGet(dist, i);
                    if (cur == 0) continue;
                    int best = cur;
                    if (x == W - 1 || y == H - 1 || x == 0) best = Math.Min(best, 1); // touches OOB
                    if (x < W - 1) best = Math.Min(best, ClrGet(dist, i + 1) + 1);
                    if (y < H - 1) best = Math.Min(best, ClrGet(dist, i + W) + 1);
                    if (x < W - 1 && y < H - 1) best = Math.Min(best, ClrGet(dist, i + W + 1) + 1);
                    if (x > 0 && y < H - 1) best = Math.Min(best, ClrGet(dist, i + W - 1) + 1);
                    if (best != cur) ClrSet(dist, i, best);
                }
            _clearance = dist;
            return dist;
        }
    }


    /// <summary>Walkable AND at least tiles clear of the nearest blocked/out-of-bounds tile (Chebyshev)</summary>
    public bool IsPathable(int tx, int ty, double margin)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (margin <= 0) return IsWalkableTile(tx, ty);
        // clearance c means the nearest blocked tile is Chebyshev-distance c away; we require every tile within `margin`…
        // 3+ saturates, so a margin at or above the cap is not answerable from this map and would
        // silently read as "blocked everywhere". Nothing passes one today (the ladder stops at 2.0);
        // if that changes, widen ClearanceCap rather than letting the query quietly lie.
        if (margin >= ClearanceCap) throw new ArgumentOutOfRangeException(nameof(margin),
            $"margin {margin} needs more than the {ClearanceCap} levels the packed clearance map stores");
        return ClrGet(Clearance(), ty * WidthTiles + tx) > margin;
    }

    /// <summary>Chebyshev distance (in tiles, saturating at 3 -- see the packed clearance map) from tile (tx,ty) to the nearest blocked/OOB tile</summary>
    public int ClearanceAt(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles ? ClrGet(Clearance(), ty * WidthTiles + tx) : 0;

    /// <summary>World coordinate of a tile's centre (for issuing move packets)</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx - ShbdTileShift + 0.5) * WorldPerTile), (uint)((ty - ShbdTileShift + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);
}
