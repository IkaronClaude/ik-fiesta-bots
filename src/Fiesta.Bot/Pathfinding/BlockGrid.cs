namespace Fiesta.Bot.Pathfinding;

public sealed class BlockGrid
{
    /// <summary>World units per tile (50 world per map-unit ÷ 8 tiles per map-unit)</summary>
    public const double WorldPerTile = 6.25;

    // SHBD 1-TILE ORIGIN SHIFT (operator + godmode wall-hug trace, 2026-07-22) ────────────────────────── The .shbd blocked-bit at array index (i,j) physically represents the world cell one tile OVER in eac…
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

    // --- COMPANION .bdt (server-collision candidate, reverse-engineered 2026-07-21)
    private BdtGrid? _bdt;
    /// <summary>Attach this map's .bdt quadtree collision</summary>
    public void AttachBdt(BdtGrid? bdt) => _bdt ??= bdt;
    /// <summary>True if this map has a .bdt (terrain/hill map)</summary>
    public bool HasBdt => _bdt is not null;
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
        lock (_rtLock) { _rtBlocked ??= new(); isNew = !_rtBlocked.ContainsKey(ty * WidthTiles + tx); _rtBlocked[ty * WidthTiles + tx] = long.MaxValue; }
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
        }
        if (isNew) _clearance = null; // NEW block → re-inflate obstacle margins around it on next use
    }
    /// <summary>Count of learned server-blocked tiles (diagnostics)</summary>
    public int RuntimeBlockedCount { get { lock (_rtLock) return _rtBlocked?.Count ?? 0; } }

    /// <summary>Forget all MOVEFAIL-learned runtime blocks</summary>
    public void ClearRuntimeBlocked()
    {
        lock (_rtLock) { if (_rtBlocked is null || _rtBlocked.Count == 0) return; _rtBlocked.Clear(); }
        _clearance = null; // obstacle inflation was built around the (now-gone) blocks → rebuild
    }

    // --- Obstacle inflation (P0 2026-06-30: paths hugged obstacle edges → the straight-run MOVERUN between waypoint…

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

    /// <summary>Walkable AND at least tiles clear of the nearest blocked/out-of-bounds tile (Chebyshev)</summary>
    public bool IsPathable(int tx, int ty, double margin)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        if (margin <= 0) return IsWalkableTile(tx, ty);
        // clearance c means the nearest blocked tile is Chebyshev-distance c away; we require every tile within `margin`…
        return Clearance()[ty * WidthTiles + tx] > margin;
    }

    /// <summary>Chebyshev distance (in tiles, capped at 63) from tile (tx,ty) to the nearest blocked/OOB tile</summary>
    public int ClearanceAt(int tx, int ty)
        => (uint)tx < (uint)WidthTiles && (uint)ty < (uint)HeightTiles ? Clearance()[ty * WidthTiles + tx] : 0;

    /// <summary>World coordinate of a tile's centre (for issuing move packets)</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx - ShbdTileShift + 0.5) * WorldPerTile), (uint)((ty - ShbdTileShift + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile) + ShbdTileShift, (int)(worldY / WorldPerTile) + ShbdTileShift);
}
