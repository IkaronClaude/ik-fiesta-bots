using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Agent;

/// <summary>World components that carry a version counter (incremented on change).</summary>
public enum WorldComponent : byte { Self, Entities, Bag, Skills, Quests, Party, Map, Trade }

/// <summary>Entity kind bits (also the Lua `ents.*` filter values).</summary>
[Flags]
public enum EntityKind : int
{
    None = 0, Mob = 1, Huntable = 2, Npc = 4, Player = 8, Gate = 16, Drop = 32, Aggro = 64, Party = 128, Alive = 256,
    All = Mob | Npc | Player | Gate | Drop,
}

/// <summary>Our character's state.</summary>
public sealed class SelfState
{
    /// <summary>Our zone handle, -1 before zone entry.</summary>
    public int Handle { get; internal set; } = -1;
    /// <summary>Character name.</summary>
    public string Name { get; internal set; } = "";
    /// <summary>ClassName id.</summary>
    public int ClassId { get; internal set; }
    /// <summary>Base class of our line (from client data).</summary>
    public int ClassFamily { get; internal set; } = -1;
    /// <summary>Level.</summary>
    public int Level { get; internal set; }
    /// <summary>Experience.</summary>
    public long Exp { get; internal set; }
    /// <summary>Exp at the start / end of this level.</summary>
    public long PrevExp { get; internal set; }
    /// <summary>Exp needed for the next level (cumulative).</summary>
    public long NextExp { get; internal set; }
    /// <summary>HP.</summary>
    public double Hp { get; internal set; }
    /// <summary>Max HP.</summary>
    public double MaxHp { get; internal set; }
    /// <summary>SP.</summary>
    public double Sp { get; internal set; }
    /// <summary>Max SP.</summary>
    public double MaxSp { get; internal set; }
    /// <summary>Last server-confirmed position.</summary>
    public (uint X, uint Y) ServerPos { get; internal set; }
    /// <summary>Believed position (server pos + walk progress).</summary>
    public (double X, double Y) Pos { get; internal set; }
    /// <summary>Facing unit vector.</summary>
    public (double Dx, double Dy) Facing { get; internal set; } = (1, 0);
    /// <summary>Money (seeded at zone entry from the character base).</summary>
    public long Money { get; internal set; }
    /// <summary>Money known (seeded).</summary>
    public bool MoneyKnown { get; internal set; }
    /// <summary>Run speed (units/s).</summary>
    public double RunSpeed { get; internal set; } = 120;
    /// <summary>Walk speed (units/s).</summary>
    public double WalkSpeed { get; internal set; } = 33;
    /// <summary>Mounted.</summary>
    public bool Mounted { get; internal set; }
    /// <summary>Dead.</summary>
    public bool Dead { get; internal set; }
    /// <summary>Battle mode asserted this zone session.</summary>
    public bool BattleMode { get; internal set; }
    /// <summary>Soul stone reserves.</summary>
    public int HpStones { get; internal set; }
    /// <summary>SP stones.</summary>
    public int SpStones { get; internal set; }
    /// <summary>Unspent stat points.</summary>
    public int StatPoints { get; internal set; }
    /// <summary>Last time we were hit (ms), -1 never.</summary>
    public long LastHitAt { get; internal set; } = -1;
    /// <summary>Last time we hit something (ms), -1 never.</summary>
    public long LastHitOutAt { get; internal set; } = -1;
    /// <summary>When we (re)spawned last (map login, link, revive).</summary>
    public long SpawnedAt { get; internal set; } = -1;
    /// <summary>In the world (MAP_LOGIN_ACK seen on this link).</summary>
    public bool InWorld { get; internal set; }
    /// <summary>Kills this session.</summary>
    public int Kills { get; internal set; }
    /// <summary>Deaths this session.</summary>
    public int Deaths { get; internal set; }
    /// <summary>Exp gained this session.</summary>
    public long ExpGained { get; internal set; }
    private readonly int[] _abId = new int[64];
    private readonly long[] _abUntil = new long[64];
    private int _abN;

    /// <summary>Hit or hitting within 8 s.</summary>
    public bool InCombat(long now) => (LastHitAt >= 0 && now - LastHitAt < 8000) || (LastHitOutAt >= 0 && now - LastHitOutAt < 8000);
    /// <summary>HP percent.</summary>
    public double HpPct => MaxHp > 0 ? Hp * 100 / MaxHp : 0;
    /// <summary>SP percent.</summary>
    public double SpPct => MaxSp > 0 ? Sp * 100 / MaxSp : 0;

    internal void SetAbstate(int id, bool on, long until)
    {
        for (var i = 0; i < _abN; i++)
            if (_abId[i] == id)
            {
                if (on) _abUntil[i] = until;
                else { _abN--; _abId[i] = _abId[_abN]; _abUntil[i] = _abUntil[_abN]; }
                return;
            }
        if (on && _abN < _abId.Length) { _abId[_abN] = id; _abUntil[_abN] = until; _abN++; }
    }

    /// <summary>Seed the identity known from the character list (before zone entry).</summary>
    public void Seed(string name, int classId, int classFamily, int level) { Name = name; ClassId = classId; ClassFamily = classFamily; Level = level; }

    /// <summary>Abstate present?</summary>
    public bool HasAbstate(int id, long now) => AbstateLeftMs(id, now) != -1;
    /// <summary>Remaining time of an abstate, -1 if absent, long.MaxValue when unknown duration.</summary>
    public long AbstateLeftMs(int abstateId, long nowMs)
    {
        for (var i = 0; i < _abN; i++)
            if (_abId[i] == abstateId) return _abUntil[i] == long.MaxValue ? long.MaxValue : Math.Max(0, _abUntil[i] - nowMs);
        return -1;
    }
    /// <summary>Abstates held (for snapshots).</summary>
    public int AbstateCount => _abN;
    /// <summary>Displayed stat by id (not decoded yet: -1).</summary>
    public double Stat(int statId) => (uint)statId < (uint)_stats.Length ? _stats[statId] : -1;
    private readonly long[] _stats = InitStats();
    private static long[] InitStats() { var a = new long[0x23]; Array.Fill(a, -1L); return a; }
    internal bool SetStat(int id, long v) { if ((uint)id >= (uint)_stats.Length || _stats[id] == v) return false; _stats[id] = v; return true; }
    /// <summary>Fame (-1 until the character base arrived).</summary>
    public long Fame => _stats[0x20];
    /// <summary>Soul stone capacity (-1 unknown).</summary>
    public (long Hp, long Sp) MaxStones => (_stats[0x21], _stats[0x22]);
    /// <summary>Active abstate i (0 .. AbstateCount-1): wire id and end (monotonic ms, long.MaxValue = no end).</summary>
    public (int Id, long Until) AbstateAt(int i) => (_abId[i], _abUntil[i]);
}

/// <summary>Entities in view as struct-of-arrays with a handle -> slot map (no per-entity objects).</summary>
public sealed class EntityTable
{
    /// <summary>Capacity (entities in view).</summary>
    public const int Cap = 1024;
    private readonly short[] _slotOf = new short[65536];
    private readonly int[] _free = new int[Cap];
    private int _freeN;
    /// <summary>Live slots, dense list (for iteration).</summary>
    internal readonly int[] Live = new int[Cap];
    internal int LiveN;
    private readonly int[] _livePos = new int[Cap];

    internal readonly int[] Handle = new int[Cap];
    internal readonly EntityKind[] Kinds = new EntityKind[Cap];
    internal readonly int[] Mob = new int[Cap];
    internal readonly int[] Lvl = new int[Cap];
    internal readonly double[] HpV = new double[Cap], MaxHpV = new double[Cap];
    internal readonly double[] X = new double[Cap], Y = new double[Cap];
    internal readonly double[] ToX = new double[Cap], ToY = new double[Cap], Spd = new double[Cap];
    internal readonly long[] MoveAt = new long[Cap];
    internal readonly double[] FaceX = new double[Cap], FaceY = new double[Cap];
    internal readonly bool[] HasFace = new bool[Cap];
    internal readonly int[] TargetH = new int[Cap];
    internal readonly long[] HitUsAt = new long[Cap], DamagedAt = new long[Cap], AppearedAt = new long[Cap];
    internal readonly double[] HitPosX = new double[Cap], HitPosY = new double[Cap], SpawnX = new double[Cap], SpawnY = new double[Cap];
    internal readonly bool[] Dead = new bool[Cap];
    internal readonly long[] DamageByUs = new long[Cap];
    internal readonly string?[] Name = new string?[Cap];
    internal readonly double[] Body = new double[Cap];
    internal readonly long[] LastSwingAt = new long[Cap];
    internal readonly double[] HitDist = new double[Cap];

    /// <summary>Create.</summary>
    public EntityTable()
    {
        Array.Fill(_slotOf, (short)-1);
        for (var i = 0; i < Cap; i++) _free[i] = Cap - 1 - i;
        _freeN = Cap;
    }

    /// <summary>Capacity.</summary>
    public int Capacity => Cap;
    /// <summary>Entities in view.</summary>
    public int Count => LiveN;
    /// <summary>The i-th live slot (0 .. Count-1).</summary>
    public int LiveSlot(int i) => Live[i];
    /// <summary>Slot of a handle, -1 when not in view.</summary>
    public int SlotOf(int handle) => (uint)handle < 65536 ? _slotOf[handle] : -1;
    /// <summary>Handle of a slot.</summary>
    public int HandleOf(int slot) => Handle[slot];
    /// <summary>Kind bits of a slot (dynamic bits Aggro/Alive/Huntable folded in by the caller).</summary>
    public EntityKind Kind(int slot) => Kinds[slot];
    /// <summary>Mob id (-1 non-mob).</summary>
    public int MobId(int slot) => Mob[slot];
    /// <summary>Level (-1 unknown).</summary>
    public int Level(int slot) => Lvl[slot];
    /// <summary>HP, -1 unknown.</summary>
    public double Hp(int slot) => HpV[slot];
    /// <summary>Max HP.</summary>
    public double MaxHp(int slot) => MaxHpV[slot];
    /// <summary>Is dead.</summary>
    public bool IsDead(int slot) => Dead[slot];
    /// <summary>Name (players, gate destination).</summary>
    public string? NameOf(int slot) => Name[slot];
    /// <summary>Body size.</summary>
    public double BodyOf(int slot) => Body[slot];

    /// <summary>Position extrapolated to now along the current move leg.</summary>
    public (double X, double Y) Pos(int slot, long nowMs)
    {
        var s = Spd[slot];
        if (s <= 0) return (X[slot], Y[slot]);
        double dx = ToX[slot] - X[slot], dy = ToY[slot] - Y[slot];
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-6) return (ToX[slot], ToY[slot]);
        var t = (nowMs - MoveAt[slot]) / 1000.0 * s;
        if (t >= d) return (ToX[slot], ToY[slot]);
        return (X[slot] + dx / d * t, Y[slot] + dy / d * t);
    }
    /// <summary>Velocity (units/s), zero when standing or arrived.</summary>
    public (double Vx, double Vy) Velocity(int slot, long nowMs)
    {
        var s = Spd[slot];
        if (s <= 0) return (0, 0);
        double dx = ToX[slot] - X[slot], dy = ToY[slot] - Y[slot];
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-6 || (nowMs - MoveAt[slot]) / 1000.0 * s >= d) return (0, 0);
        return (dx / d * s, dy / d * s);
    }
    /// <summary>Facing, null if never moved.</summary>
    public (double Dx, double Dy)? Facing(int slot) => HasFace[slot] ? (FaceX[slot], FaceY[slot]) : null;
    /// <summary>Who it targets (-1 unknown).</summary>
    public int TargetOf(int slot) => TargetH[slot];
    /// <summary>Last time it hit us (ms), -1 never.</summary>
    public long LastHitUsAt(int slot) => HitUsAt[slot];
    /// <summary>Where it was last damaged (leash origin), null never.</summary>
    public (double X, double Y)? LastHitPos(int slot) => DamagedAt[slot] >= 0 ? (HitPosX[slot], HitPosY[slot]) : null;
    /// <summary>First position seen.</summary>
    public (double X, double Y) SpawnPos(int slot) => (SpawnX[slot], SpawnY[slot]);

    internal int Add(int handle, EntityKind kind, int mob, double x, double y, long now)
    {
        var s = SlotOf(handle);
        if (s < 0)
        {
            if (_freeN == 0) return -1;
            s = _free[--_freeN];
            _slotOf[handle] = (short)s;
            _livePos[s] = LiveN; Live[LiveN++] = s;
            Handle[s] = handle; HpV[s] = -1; MaxHpV[s] = -1; Lvl[s] = -1; TargetH[s] = -1; HitUsAt[s] = -1; DamagedAt[s] = -1;
            HasFace[s] = false; Dead[s] = false; DamageByUs[s] = 0; Name[s] = null; SpawnX[s] = x; SpawnY[s] = y; AppearedAt[s] = now;
            LastSwingAt[s] = -1; HitDist[s] = -1;
        }
        Kinds[s] = kind; Mob[s] = mob; X[s] = x; Y[s] = y; ToX[s] = x; ToY[s] = y; Spd[s] = 0; MoveAt[s] = now;
        return s;
    }

    internal void Remove(int handle)
    {
        var s = SlotOf(handle);
        if (s < 0) return;
        _slotOf[handle] = -1;
        var p = _livePos[s]; var last = Live[--LiveN];
        Live[p] = last; _livePos[last] = p;
        _free[_freeN++] = s;
        Name[s] = null;
    }

    internal void Move(int slot, double fx, double fy, double tx, double ty, double speed, long now)
    {
        X[slot] = fx; Y[slot] = fy; ToX[slot] = tx; ToY[slot] = ty; Spd[slot] = speed; MoveAt[slot] = now;
        double dx = tx - fx, dy = ty - fy; var d = Math.Sqrt(dx * dx + dy * dy);
        if (d > 1e-6) { FaceX[slot] = dx / d; FaceY[slot] = dy / d; HasFace[slot] = true; }
    }

    internal void Stop(int slot, double x, double y, long now) { X[slot] = x; Y[slot] = y; ToX[slot] = x; ToY[slot] = y; Spd[slot] = 0; MoveAt[slot] = now; }

    /// <summary>Remove everything (map change).</summary>
    public void Clear() { while (LiveN > 0) Remove(Handle[Live[LiveN - 1]]); }
}

/// <summary>Uniform-grid spatial index over the entity table (200 u cells, 64 x 64 buckets hashed by cell; view
/// ranges are far smaller than 64 cells so buckets never alias inside a query).</summary>
public sealed class SpatialGrid
{
    /// <summary>Cell size in world units.</summary>
    public const double CellSize = 200;
    private const int B = 64;
    private readonly int[][] _bucket = new int[B * B][];
    private readonly int[] _bn = new int[B * B];
    private readonly int[] _cellOf = new int[EntityTable.Cap];
    private readonly (int Slot, double D)[] _tmp = new (int, double)[EntityTable.Cap];

    /// <summary>Create.</summary>
    public SpatialGrid() => Array.Fill(_cellOf, -1);

    private static int Cell(double x, double y) => ((int)(y / CellSize) & (B - 1)) * B + ((int)(x / CellSize) & (B - 1));

    /// <summary>Move an entity's cell membership.</summary>
    public void Update(int slot, double x, double y)
    {
        var c = Cell(x, y);
        if (_cellOf[slot] == c) return;
        Remove(slot);
        var a = _bucket[c] ??= new int[8];
        if (_bn[c] == a.Length) { Array.Resize(ref _bucket[c], a.Length * 2); a = _bucket[c]; }
        a[_bn[c]++] = slot;
        _cellOf[slot] = c;
    }

    /// <summary>Remove a slot.</summary>
    public void Remove(int slot)
    {
        var c = _cellOf[slot];
        if (c < 0) return;
        var a = _bucket[c];
        for (var i = 0; i < _bn[c]; i++) if (a[i] == slot) { a[i] = a[--_bn[c]]; break; }
        _cellOf[slot] = -1;
    }

    /// <summary>Clear all.</summary>
    public void Clear() { Array.Clear(_bn); Array.Fill(_cellOf, -1); }

    /// <summary>Write slots within r of (x,y) matching a mask into dest, nearest first; returns the count. The table's
    /// stored (unextrapolated) positions are used for cell membership, extrapolated ones for the distance.</summary>
    public int Query(double x, double y, double r, EntityKind mask, WorldState w, long now, Span<int> dest)
    {
        var n = 0;
        var cr = (int)Math.Ceiling(r / CellSize) + 1;
        if (cr > B / 2) cr = B / 2;
        int cx = (int)(x / CellSize), cy = (int)(y / CellSize);
        var r2 = r * r;
        for (var gy = cy - cr; gy <= cy + cr; gy++)
            for (var gx = cx - cr; gx <= cx + cr; gx++)
            {
                var c = (gy & (B - 1)) * B + (gx & (B - 1));
                var a = _bucket[c];
                for (var i = 0; i < _bn[c]; i++)
                {
                    var s = a[i];
                    var (px, py) = w.Entities.Pos(s, now);
                    double dx = px - x, dy = py - y, d2 = dx * dx + dy * dy;
                    if (d2 > r2 || !w.Matches(s, mask, now)) continue;
                    // insertion sort by distance into _tmp
                    var j = n++;
                    while (j > 0 && _tmp[j - 1].D > d2) { _tmp[j] = _tmp[j - 1]; j--; }
                    _tmp[j] = (s, d2);
                }
            }
        var m = Math.Min(n, dest.Length);
        for (var i = 0; i < m; i++) dest[i] = _tmp[i].Slot;
        return m;
    }

    /// <summary>Nearest slot matching, -1 none (scans the table when r is unbounded: the view is small).</summary>
    public int Nearest(double x, double y, double maxR, EntityKind mask, WorldState w, long now)
    {
        var best = -1; var bd = maxR < 0 ? double.MaxValue : maxR * maxR;
        var table = w.Entities;
        for (var i = 0; i < table.LiveN; i++)
        {
            var s = table.Live[i];
            var (px, py) = table.Pos(s, now);
            double dx = px - x, dy = py - y, d2 = dx * dx + dy * dy;
            if (d2 < bd && w.Matches(s, mask, now)) { bd = d2; best = s; }
        }
        return best;
    }
}

/// <summary>Bag, equipment and per-item indexes.</summary>
public sealed class Inventory
{
    /// <summary>Slots per bag page.</summary>
    public const int PageSlots = 24;
    private readonly int[] _bag = new int[256], _lot = new int[256];
    private readonly int[] _equip = new int[64];
    private readonly Dictionary<int, int> _count = new();
    private int _used;

    /// <summary>Create.</summary>
    public Inventory() { Array.Fill(_bag, -1); Array.Fill(_equip, -1); }
    /// <summary>Bag pages (the base bag is two pages; expansions arrive as charged buffs, not decoded yet).</summary>
    public int Pages { get; set; } = 2;
    /// <summary>Item id and lot at a slot ((-1,0) empty; item id 0 is real).</summary>
    public (int ItemId, int Lot) At(int slot) => (uint)slot < 256 && _bag[slot] >= 0 ? (_bag[slot], _lot[slot]) : (-1, 0);
    /// <summary>Total count of an item id.</summary>
    public int Count(int itemId) => _count.GetValueOrDefault(itemId);
    /// <summary>First slot holding an item id, -1 none.</summary>
    public int Find(int itemId) { for (var i = 0; i < Size; i++) if (_bag[i] == itemId) return i; return -1; }
    /// <summary>Free bag slots.</summary>
    public int Free => Math.Max(0, Size - _used);
    /// <summary>Bag size.</summary>
    public int Size => Pages * PageSlots;
    /// <summary>Used slots.</summary>
    public int Used => _used;
    /// <summary>Equipped item id by equipment slot, -1 none.</summary>
    public int Equipped(int equipSlot) => (uint)equipSlot < 64 ? _equip[equipSlot] : -1;
    /// <summary>HP/SP soul stone reserves (mirrors SelfState).</summary>
    public (int Hp, int Sp) Stones { get; internal set; }

    internal void ClearBox(int box)
    {
        if (box == 9) { Array.Fill(_bag, -1); Array.Clear(_lot); _count.Clear(); _used = 0; }
        else if (box == 8) Array.Fill(_equip, -1);
    }

    /// <summary>Set a cell; returns (old item, old lot).</summary>
    internal (int Item, int Lot) Set(int box, int slot, int item, int lot)
    {
        if (box == 8) { if ((uint)slot < 64) { var o = _equip[slot]; _equip[slot] = item; return (o, 1); } return (-1, 0); }
        if (box != 9 || (uint)slot >= 256) return (-1, 0);
        var old = (_bag[slot], _lot[slot]);
        if (old.Item1 >= 0) { _used--; var c = _count[old.Item1] - old.Item2; if (c <= 0) _count.Remove(old.Item1); else _count[old.Item1] = c; }
        _bag[slot] = item; _lot[slot] = item >= 0 ? Math.Max(1, lot) : 0;
        if (item >= 0) { _used++; _count[item] = _count.GetValueOrDefault(item) + _lot[slot]; }
        return old;
    }
}

/// <summary>Learned skills and cooldowns (incl. groups) as confirmed by the server's cast starts.</summary>
public sealed class SkillBook
{
    private readonly Dictionary<int, long> _readyAt = new();
    private readonly Dictionary<int, long> _groupReadyAt = new();
    private readonly List<int> _learned = new();
    /// <summary>Version.</summary>
    public int Version { get; private set; }
    /// <summary>Learned?</summary>
    public bool Has(int skillId) => _readyAt.ContainsKey(skillId);
    /// <summary>Learned skills (in order learned).</summary>
    public IReadOnlyList<int> Learned => _learned;
    /// <summary>Milliseconds until ready (0 ready).</summary>
    public long ReadyInMs(int skillId, int group, long nowMs)
    {
        var a = _readyAt.TryGetValue(skillId, out var r) ? Math.Max(0, r - nowMs) : 0;
        var b = group > 0 && _groupReadyAt.TryGetValue(group, out var g) ? Math.Max(0, g - nowMs) : 0;
        return Math.Max(a, b);
    }
    /// <summary>Empower levels (not decoded yet: zeros).</summary>
    public (int Dmg, int Sp, int Dur, int Cd) Empower(int skillId) => (0, 0, 0, 0);
    /// <summary>Monotonic time the skill itself is ready again (0 = never cast).</summary>
    public long ReadyAt(int skill) => _readyAt.TryGetValue(skill, out var r) ? r : 0;
    /// <summary>Monotonic time a cooldown group is ready again (0 = none).</summary>
    public long GroupReadyAt(int group) => group > 0 && _groupReadyAt.TryGetValue(group, out var g) ? g : 0;
    internal void Learn(int skill, long readyAt) { if (!_readyAt.ContainsKey(skill)) _learned.Add(skill); _readyAt[skill] = readyAt; Version++; }
    internal void Started(int skill, long nowMs, int cooldownMs, int group, int groupMs)
    {
        if (_readyAt.ContainsKey(skill)) _readyAt[skill] = nowMs + cooldownMs;
        if (group > 0) _groupReadyAt[group] = nowMs + groupMs;
        Version++;
    }
    internal void Clear() { _readyAt.Clear(); _groupReadyAt.Clear(); _learned.Clear(); Version++; }
}

/// <summary>Quest log: active, done, available, progress.</summary>
public sealed class QuestLog
{
    private readonly Dictionary<int, (int Status, long Counts)> _active = new();
    private readonly HashSet<int> _done = new(), _available = new();
    private readonly List<int> _activeList = new();
    /// <summary>Quest log capacity.</summary>
    public const int Capacity = 40;
    /// <summary>Active?</summary>
    public bool IsActive(int questId) => _active.ContainsKey(questId);
    /// <summary>Done?</summary>
    public bool IsDone(int questId) => _done.Contains(questId);
    /// <summary>Available (readable) now per the server's list.</summary>
    public bool IsAvailable(int questId) => _available.Contains(questId);
    /// <summary>Status byte of an active quest, -1 not active.</summary>
    public int Status(int questId) => _active.TryGetValue(questId, out var q) ? q.Status : -1;
    /// <summary>Have-count of objective i (1-based, 0..5).</summary>
    public int Have(int questId, int objective) => _active.TryGetValue(questId, out var q) && objective is >= 1 and <= 5 ? (int)((q.Counts >> (8 * (objective - 1))) & 0xFF) : 0;
    /// <summary>Active quest ids.</summary>
    public IReadOnlyList<int> Active => _activeList;
    /// <summary>Available quest ids.</summary>
    public IReadOnlyCollection<int> Available => _available;
    /// <summary>Free log slots.</summary>
    public int SlotsFree => Capacity - _active.Count;
    internal void Set(int q, int status, long counts) { if (!_active.ContainsKey(q)) _activeList.Add(q); _active[q] = (status, counts); _available.Remove(q); }
    internal void Done(int q) { _done.Add(q); if (_active.Remove(q)) _activeList.Remove(q); }
    internal void AddAvailable(int q) => _available.Add(q);
    internal void Kill(int q, int objective)
    {
        if (!_active.TryGetValue(q, out var a) || objective < 0 || objective > 4) return;
        var shift = 8 * objective;
        var have = (int)((a.Counts >> shift) & 0xFF) + 1;
        _active[q] = (a.Status, (a.Counts & ~(0xFFL << shift)) | ((long)Math.Min(255, have) << shift));
    }
    internal void Clear() { _active.Clear(); _done.Clear(); _available.Clear(); _activeList.Clear(); }
}

/// <summary>Party membership.</summary>
public sealed class PartyState
{
    private readonly List<int> _members = new();
    /// <summary>Member handles in view; returns count.</summary>
    public int Members(Span<int> dest) { var n = Math.Min(dest.Length, _members.Count); for (var i = 0; i < n; i++) dest[i] = _members[i]; return n; }
    /// <summary>Party size.</summary>
    public int Size => _members.Count;
}

/// <summary>Trade window state.</summary>
public enum TradePhase : byte { None, Asked, Proposed, Open, Locked, Confirmed, Done, Cancelled }

/// <summary>Trade state.</summary>
public sealed class TradeState
{
    /// <summary>Current phase.</summary>
    public TradePhase Phase { get; internal set; }
    /// <summary>Partner handle, -1 none.</summary>
    public int Partner { get; internal set; } = -1;
}

/// <summary>Current map.</summary>
public sealed class MapState
{
    /// <summary>Map id (-1 unknown).</summary>
    public int MapId { get; internal set; } = -1;
    /// <summary>Map short name.</summary>
    public string MapName { get; internal set; } = "";
    /// <summary>Instance map?</summary>
    public bool IsInstance { get; internal set; }
}

/// <summary>The open shop / menu / dialogue state the NPC procedures read.</summary>
public sealed class NpcState
{
    /// <summary>The last soul stone window: HP price, HP cap, SP price, SP cap, when (ms; -1 never).</summary>
    public (int HpPrice, int HpCap, int SpPrice, int SpCap, long At) StonePrices { get; internal set; } = (-1, -1, -1, -1, -1);
    /// <summary>Shop list: npc handle, opened at, items (slot, item id).</summary>
    public int ShopNpc { get; internal set; } = -1;
    /// <summary>Opcode of the shop table.</summary>
    public int ShopKind { get; internal set; }
    /// <summary>When the shop list arrived (ms), -1 none.</summary>
    public long ShopAt { get; internal set; } = -1;
    /// <summary>Shop items.</summary>
    public List<(int Slot, int ItemId)> ShopItems { get; } = new();
    /// <summary>Last server menu (title, npc, options).</summary>
    public (string Title, int Npc, (byte Reply, string Text)[] Options, long At)? Menu { get; internal set; }
    /// <summary>Last NPC menu request (mob id, at).</summary>
    public (int MobId, long At)? NpcMenu { get; internal set; }
    /// <summary>Last quest script step.</summary>
    public (int Quest, int Cmd, long Data, int Npc, long At)? Script { get; internal set; }
    /// <summary>Last death menu (seconds, at).</summary>
    public (long Seconds, long At)? DeadMenu { get; internal set; }
    /// <summary>Last chat (speaker, text).</summary>
    public (int Speaker, string Text)? LastChat { get; internal set; }
    /// <summary>Last whisper (name, text).</summary>
    public (string Name, string Text)? LastWhisper { get; internal set; }
}

/// <summary>Everything one bot knows, owned by its tick thread. Inbound events are applied at the start of the tick.</summary>
public sealed class WorldState
{
    private readonly int[] _versions = new int[8];
    /// <summary>Self.</summary>
    public SelfState Self { get; } = new();
    /// <summary>Entities.</summary>
    public EntityTable Entities { get; } = new();
    /// <summary>Spatial index over entities.</summary>
    public SpatialGrid Grid { get; } = new();
    /// <summary>Bag and equipment.</summary>
    public Inventory Bag { get; } = new();
    /// <summary>Skills.</summary>
    public SkillBook Skills { get; } = new();
    /// <summary>Quests.</summary>
    public QuestLog Quests { get; } = new();
    /// <summary>Party.</summary>
    public PartyState Party { get; } = new();
    /// <summary>Trade.</summary>
    public TradeState Trade { get; } = new();
    /// <summary>Map.</summary>
    public MapState Map { get; } = new();
    /// <summary>Open shop / menus / dialogue.</summary>
    public NpcState Npc { get; } = new();
    /// <summary>Client data (for names, bodies).</summary>
    public IClientData? Data { get; set; }
    /// <summary>Knowledge (learners write).</summary>
    public IKnowledgeStore? Knowledge { get; set; }
    /// <summary>Link up.</summary>
    public bool LinkUp { get; internal set; }
    /// <summary>Events applied (lifetime).</summary>
    public long Applied { get; private set; }

    /// <summary>Version of a component (incremented on every change).</summary>
    public int Version(WorldComponent component) => _versions[(int)component];
    private void Bump(WorldComponent c) => _versions[(int)c]++;

    /// <summary>The slot's kind with the dynamic bits folded in (Alive, Aggro, Huntable).</summary>
    public EntityKind KindOf(int slot, long now)
    {
        var k = Entities.Kinds[slot];
        if (!Entities.Dead[slot]) k |= EntityKind.Alive;
        var hit = Entities.HitUsAt[slot];
        if ((hit >= 0 && now - hit < 10000) || (Entities.TargetH[slot] == Self.Handle && Self.Handle >= 0)) k |= EntityKind.Aggro;
        // huntable: a live mob that has hit points and is not on the player side (gathering nodes have MaxHP 0)
        if ((k & EntityKind.Mob) != 0 && !Entities.Dead[slot] && Data?.Mob(Entities.Mob[slot]) is not { IsPlayerSide: true } && Data?.Mob(Entities.Mob[slot]) is not { MaxHp: 0 }) k |= EntityKind.Huntable;
        return k;
    }

    /// <summary>Filter: the type bits of the mask (Mob/Npc/Player/Gate/Drop) are alternatives, the flag bits
    /// (Huntable/Aggro/Party/Alive) are all required. A mask of 0 means Huntable.</summary>
    public bool Matches(int slot, EntityKind mask, long now)
    {
        if (mask == EntityKind.None) mask = EntityKind.Huntable;
        var k = KindOf(slot, now);
        var type = mask & EntityKind.All; var flags = mask & ~EntityKind.All;
        return (type == 0 || (k & type) != 0) && (k & flags) == flags;
    }

    /// <summary>Apply one inbound event and raise the derived brain events.</summary>
    public void Apply(in InboundEvent e, BrainEventBus ev, long now)
    {
        Applied++;
        var me = Self.Handle;
        switch (e.Kind)
        {
            case InboundKind.EntityAppeared:
            {
                if (e.Handle == me) { Self.ServerPos = (e.X, e.Y); break; }
                var kind = (EntityKind)(int)e.A;
                var s = Entities.Add(e.Handle, kind, (int)e.B, e.X, e.Y, now);
                if (s < 0) break;
                if (e.C >= 0) Entities.Lvl[s] = (int)e.C;
                Entities.Name[s] = e.Text;
                if ((kind & (EntityKind.Mob | EntityKind.Npc)) != 0 && Data?.Mob((int)e.B) is { } mi)
                {
                    if (Entities.Lvl[s] < 0) Entities.Lvl[s] = mi.Level;
                    Entities.MaxHpV[s] = mi.MaxHp; Entities.HpV[s] = mi.MaxHp;
                    Entities.Body[s] = GameRules.MobBody(mi);
                    Entities.Name[s] ??= mi.Name;
                }
                else Entities.Body[s] = GameRules.PlayerBody;
                Grid.Update(s, e.X, e.Y);
                Bump(WorldComponent.Entities);
                ev.Raise(EventKind.EntityAppeared, e.Handle, (long)kind);
                break;
            }
            case InboundKind.EntityLeft:
            {
                var s = Entities.SlotOf(e.Handle);
                if (s < 0) break;
                var wasAggro = Entities.HitUsAt[s] >= 0 && now - Entities.HitUsAt[s] < 10000;
                Grid.Remove(s);
                Entities.Remove(e.Handle);
                Bump(WorldComponent.Entities);
                ev.Raise(EventKind.EntityLeft, e.Handle, 0);
                if (wasAggro) ev.Raise(EventKind.AggroLost, e.Handle);
                break;
            }
            case InboundKind.EntityMoved:
            {
                if (e.Handle == me) break;
                var s = Entities.SlotOf(e.Handle);
                if (s < 0) break;
                Entities.Move(s, e.A, e.B, e.X, e.Y, e.C > 0 ? e.C : 100, now);
                Grid.Update(s, e.A, e.B);
                break;
            }
            case InboundKind.EntityStopped:
            {
                if (e.Handle == me) { Self.ServerPos = (e.X, e.Y); break; }
                var s = Entities.SlotOf(e.Handle);
                if (s < 0) break;
                Entities.Stop(s, e.X, e.Y, now);
                Grid.Update(s, e.X, e.Y);
                break;
            }
            case InboundKind.EntityHp:
            {
                var s = Entities.SlotOf(e.Handle);
                if (s < 0) break;
                Entities.HpV[s] = e.A; Entities.MaxHpV[s] = e.B;
                if (e.C > 0) Entities.Lvl[s] = (int)e.C;
                break;
            }
            case InboundKind.EntityTarget:
                break;
            case InboundKind.EntityDied:
            {
                if (e.Handle == me) break;
                var s = Entities.SlotOf(e.Handle);
                if (s < 0) break;
                Entities.Dead[s] = true; Entities.HpV[s] = 0; Entities.Spd[s] = 0;
                var mob = Entities.Mob[s];
                if (e.Handle2 == me || Entities.DamageByUs[s] > 0)
                {
                    Self.Kills++;
                    ev.Raise(EventKind.Kill, e.Handle, mob);
                }
                if (Entities.HitUsAt[s] >= 0 && now - Entities.HitUsAt[s] < 10000) ev.Raise(EventKind.AggroLost, e.Handle);
                ev.Raise(EventKind.TargetDied, e.Handle, mob);
                Bump(WorldComponent.Entities);
                break;
            }
            case InboundKind.SelfHp:
            {
                var old = Self.Hp;
                Self.Hp = e.A;
                if (Self.Hp > Self.MaxHp && Self.MaxHp > 0) Self.MaxHp = Self.Hp;
                if (Self.Dead && Self.Hp > 0) { Self.Dead = false; ev.Raise(EventKind.Revived); }
                if (old != Self.Hp) { Bump(WorldComponent.Self); ev.Raise(EventKind.HpChanged, (long)Self.Hp, (long)Self.MaxHp); }
                break;
            }
            case InboundKind.SelfSp:
                if (Self.Sp != e.A) { Self.Sp = e.A; Bump(WorldComponent.Self); ev.Raise(EventKind.SpChanged, e.A, (long)Self.MaxSp); }
                break;
            case InboundKind.SelfMaxHpSp:
                if (e.A >= 0) Self.MaxHp = e.A;
                if (e.B >= 0) Self.MaxSp = e.B;
                Bump(WorldComponent.Self);
                break;
            case InboundKind.SelfBase:
                Self.Level = (int)e.A; Self.Exp = e.B; Self.Money = e.C; Self.MoneyKnown = true;
                Self.HpStones = (int)e.X; Self.SpStones = (int)e.Y; Bag.Stones = (Self.HpStones, Self.SpStones);
                if (!string.IsNullOrEmpty(e.Text)) SetMap(Data?.Map(e.Text!)?.Id ?? -1, e.Text!);
                Bump(WorldComponent.Self);
                break;
            case InboundKind.MapLoginAck:
                Self.Handle = e.Handle;
                Self.ServerPos = (e.X, e.Y); Self.Pos = (e.X, e.Y);
                if (e.A > 0) Self.MaxHp = e.A;
                if (e.B > 0) Self.MaxSp = e.B;
                Self.InWorld = true; Self.SpawnedAt = now; Self.BattleMode = false; Self.Dead = false;
                Bump(WorldComponent.Self);
                ev.Raise(EventKind.MapChanged, Map.MapId, -1);
                break;
            case InboundKind.SelfExpBand:
                Self.PrevExp = e.A; Self.NextExp = e.B;
                break;
            case InboundKind.SelfLevel:
                Self.Level = (int)e.A;
                if (e.C > 0) { Self.PrevExp = e.B; Self.NextExp = e.C; }
                Bump(WorldComponent.Self);
                ev.Raise(EventKind.LevelUp, e.A);
                break;
            case InboundKind.SelfExp:
                Self.Exp += e.A;
                if (e.A > 0) Self.ExpGained += e.A;
                Bump(WorldComponent.Self);
                break;
            case InboundKind.SelfMoney:
            {
                var d = e.A - Self.Money;
                Self.Money = e.A; Self.MoneyKnown = true;
                Bump(WorldComponent.Self);
                ev.Raise(EventKind.MoneyChanged, e.A, d);
                break;
            }
            case InboundKind.SelfSpeed:
                // the self speed packet carries raw values; 127 raw = 120 u/s (measured on a capture)
                if (e.B > 0) Self.RunSpeed = e.B * 120.0 / 127.0;
                if (e.A > 0) Self.WalkSpeed = e.A * 120.0 / 127.0;
                break;
            case InboundKind.SelfAbstate:
                if (e.Handle == me || me < 0) Self.SetAbstate((int)e.A, e.B == 1, e.C > 0 ? now + e.C : long.MaxValue);
                break;
            case InboundKind.Damage:
            case InboundKind.SkillDamage:
            {
                if (e.Handle2 == me)
                {
                    Self.LastHitAt = now;
                    var dmg = e.A >= 0 ? e.A : Math.Max(0, (long)Self.Hp - e.B);
                    if (e.B >= 0) { Self.Hp = e.B; ev.Raise(EventKind.HpChanged, e.B, (long)Self.MaxHp); }
                    var s = Entities.SlotOf(e.Handle);
                    if (s >= 0)
                    {
                        var first = Entities.HitUsAt[s] < 0 || now - Entities.HitUsAt[s] > 15000;
                        Entities.HitUsAt[s] = now; Entities.TargetH[s] = me;
                        var (mx, my) = Entities.Pos(s, now);
                        var d = Math.Sqrt((mx - Self.Pos.X) * (mx - Self.Pos.X) + (my - Self.Pos.Y) * (my - Self.Pos.Y));
                        Entities.HitDist[s] = d;
                        if (Entities.LastSwingAt[s] > 0 && e.Kind == InboundKind.Damage && Knowledge is { } k && Entities.Mob[s] >= 0)
                        {
                            var iv = now - Entities.LastSwingAt[s];
                            if (iv is > 300 and < 6000) k.Sample(Entities.Mob[s], MobFact.AttackInterval, iv);
                        }
                        if (e.Kind == InboundKind.Damage) Entities.LastSwingAt[s] = now;
                        if (Knowledge is { } kk && Entities.Mob[s] >= 0)
                        {
                            kk.Sample(Entities.Mob[s], MobFact.HitAvg, dmg);
                            if (e.Kind == InboundKind.Damage) kk.ObserveAtLeast(Entities.Mob[s], MobFact.Reach, Math.Max(0, d - GameRules.PlayerBody));
                        }
                        if (first) ev.Raise(EventKind.AggroGained, e.Handle);
                    }
                    ev.Raise(EventKind.Damaged, e.Handle, dmg);
                }
                else
                {
                    var s = Entities.SlotOf(e.Handle2);
                    if (s >= 0)
                    {
                        if (e.B >= 0) Entities.HpV[s] = e.B;
                        Entities.DamagedAt[s] = now;
                        var (px, py) = Entities.Pos(s, now);
                        Entities.HitPosX[s] = px; Entities.HitPosY[s] = py;
                        if (e.Handle == me && e.A > 0) { Entities.DamageByUs[s] += e.A; Self.LastHitOutAt = now; ev.Raise(EventKind.Hit, e.Handle2, e.A); }
                    }
                }
                break;
            }
            case InboundKind.SkillCastStart:
            {
                var sk = Data?.Skill((int)e.A);
                Skills.Started((int)e.A, now, sk?.DelayMs ?? 0, sk?.DelayGroup ?? 0, sk?.DelayGroupMs ?? 0);
                Bump(WorldComponent.Skills);
                break;
            }
            case InboundKind.CastFail:
                ev.Raise(EventKind.CastResult, -1, e.A);
                break;
            case InboundKind.SwingStart:
                if (e.Handle2 == me)
                {
                    var s = Entities.SlotOf(e.Handle);
                    if (s >= 0) Entities.TargetH[s] = me;
                }
                break;
            case InboundKind.MoveFail:
                Self.ServerPos = (e.X, e.Y);
                break;
            case InboundKind.ItemCell:
            {
                if (e.B < 0) { Bag.ClearBox((int)e.A); Bump(WorldComponent.Bag); break; }
                var (oldItem, oldLot) = Bag.Set((int)e.A, (int)e.B, (int)e.C, (int)e.X);
                Bump(WorldComponent.Bag);
                if (e.A == 9 && Self.InWorld)
                {
                    if (e.C >= 0 && (e.C != oldItem || e.X > oldLot)) ev.Raise(EventKind.ItemGained, e.C, e.C == oldItem ? e.X - oldLot : e.X);
                    if (oldItem >= 0 && (e.C != oldItem || e.X < oldLot)) ev.Raise(EventKind.ItemLost, oldItem, e.C == oldItem ? oldLot - e.X : oldLot);
                    if (Bag.Free == 0) ev.Raise(EventKind.BagFull);
                }
                break;
            }
            case InboundKind.PickAck:
                if (e.C == 0x0346) ev.Raise(EventKind.BagFull);
                break;
            case InboundKind.StoneReserve:
                if (e.A == 0) Self.HpStones = (int)e.B; else Self.SpStones = (int)e.B;
                Bag.Stones = (Self.HpStones, Self.SpStones);
                Bump(WorldComponent.Self);
                break;
            case InboundKind.StoneUse:
                if (e.A == 0 && Self.HpStones > 0) Self.HpStones--;
                else if (e.A == 1 && Self.SpStones > 0) Self.SpStones--;
                Bag.Stones = (Self.HpStones, Self.SpStones);
                break;
            case InboundKind.SkillLearned:
                Skills.Learn((int)e.A, now + e.B);
                Bump(WorldComponent.Skills);
                break;
            case InboundKind.QuestProgress:
            {
                var was = Quests.IsActive((int)e.A);
                Quests.Set((int)e.A, (int)e.B, e.C);
                Bump(WorldComponent.Quests);
                if (!was && Self.InWorld) ev.Raise(EventKind.QuestAccepted, e.A);
                break;
            }
            case InboundKind.QuestState:
                Quests.Done((int)e.A);
                Bump(WorldComponent.Quests);
                if (Self.InWorld) ev.Raise(EventKind.QuestDone, e.A);
                break;
            case InboundKind.QuestAvailable:
                Quests.AddAvailable((int)e.A);
                Bump(WorldComponent.Quests);
                break;
            case InboundKind.QuestMobKill:
                Quests.Kill((int)e.A, (int)e.B);
                Bump(WorldComponent.Quests);
                ev.Raise(EventKind.QuestProgress, e.A, e.B);
                break;
            case InboundKind.QuestDialog:
                Npc.Script = ((int)e.A, (int)e.B, e.C, e.Handle2, now);
                // the quest script's ACCEPT is the only notice of a new quest (no quest list follows it on the wire)
                if (e.B is 6) { if (!Quests.IsActive((int)e.A)) Quests.Set((int)e.A, 0, 0); Bump(WorldComponent.Quests); ev.Raise(EventKind.QuestAccepted, e.A); }
                if (e.B is 10) { Quests.Done((int)e.A); Bump(WorldComponent.Quests); ev.Raise(EventKind.QuestDone, e.A); }
                break;
            case InboundKind.NpcMenu:
                Npc.NpcMenu = ((int)e.A, now);
                break;
            case InboundKind.ServerMenu:
                Npc.Menu = (e.Text ?? "", e.Handle, e.Options ?? [], now);
                break;
            case InboundKind.ShopList:
                Npc.ShopNpc = e.Handle; Npc.ShopKind = (int)e.A; Npc.ShopAt = now; Npc.ShopItems.Clear();
                break;
            case InboundKind.ShopItem:
                Npc.ShopItems.Add(((int)e.A, (int)e.B));
                break;
            case InboundKind.Chat:
                Npc.LastChat = (e.Handle, e.Text ?? "");
                ev.Raise(EventKind.Chat, e.Handle);
                break;
            case InboundKind.Whisper:
            {
                var t = e.Text ?? ""; var nl = t.IndexOf('\n');
                Npc.LastWhisper = nl < 0 ? ("", t) : (t[..nl], t[(nl + 1)..]);
                ev.Raise(EventKind.Whisper);
                break;
            }
            case InboundKind.MapLink:
            {
                var prev = Map.MapId;
                Entities.Clear(); Grid.Clear();
                var name = Data?.Map((int)e.A)?.MapName ?? "";
                SetMap((int)e.A, name);
                Self.ServerPos = (e.X, e.Y); Self.Pos = (e.X, e.Y);
                Self.SpawnedAt = now; Self.BattleMode = false;
                Bump(WorldComponent.Entities);
                ev.Raise(EventKind.MapChanged, e.A, prev);
                break;
            }
            case InboundKind.Revive:
            {
                Self.Dead = false;
                if (e.B == 0 && e.A != Map.MapId) { Entities.Clear(); Grid.Clear(); SetMap((int)e.A, Data?.Map((int)e.A)?.MapName ?? ""); }
                Self.ServerPos = (e.X, e.Y); Self.Pos = (e.X, e.Y); Self.SpawnedAt = now;
                ev.Raise(EventKind.Revived, e.A);
                break;
            }
            case InboundKind.DeadMenu:
                Self.Dead = true; Self.Deaths++; Self.Hp = 0;
                Npc.DeadMenu = (e.A, now);
                Bump(WorldComponent.Self);
                ev.Raise(EventKind.Died, e.A);
                break;
            case InboundKind.Mount:
                Self.Mounted = e.A == 1;
                Bump(WorldComponent.Self);
                break;
            case InboundKind.SelfStat:
                if (Self.SetStat((int)e.A, e.B)) Bump(WorldComponent.Self);
                break;
            case InboundKind.StatPoints:
                Self.StatPoints = (int)e.A;
                break;
            case InboundKind.StoneShop:
                Npc.StonePrices = ((int)e.A, (int)e.B, (int)e.C, (int)e.X, now);
                break;
            case InboundKind.LinkChanged:
                LinkUp = e.A == (long)Fiesta.Bot.Net.LinkState.Ready;
                if (!LinkUp) Self.InWorld = false;
                break;
        }
    }

    private void SetMap(int id, string name)
    {
        Map.MapId = id; Map.MapName = name;
        Map.IsInstance = Data?.Map(id)?.IsInstance ?? false;
        Bump(WorldComponent.Map);
    }
}
