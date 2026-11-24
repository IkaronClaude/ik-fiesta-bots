using Fiesta.Bot.Protocol;

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
    /// <summary>Level.</summary>
    public int Level { get; internal set; }
    /// <summary>Experience.</summary>
    public long Exp { get; internal set; }
    /// <summary>HP / max HP.</summary>
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
    public (double Dx, double Dy) Facing { get; internal set; }
    /// <summary>Money (seeded at zone entry).</summary>
    public long Money { get; internal set; }
    /// <summary>Run speed (units/s).</summary>
    public double RunSpeed { get; internal set; }
    /// <summary>Flags.</summary>
    public bool Mounted { get; internal set; }
    /// <summary>Dead.</summary>
    public bool Dead { get; internal set; }
    /// <summary>Battle mode asserted this zone session.</summary>
    public bool BattleMode { get; internal set; }
    /// <summary>Remaining time of an abstate, -1 if absent.</summary>
    public long AbstateLeftMs(int abstateId, long nowMs) => throw new NotImplementedException();
    /// <summary>Displayed stat by id.</summary>
    public double Stat(int statId) => throw new NotImplementedException();
}

/// <summary>Entities in view as struct-of-arrays with a handle -> slot map (no per-entity objects).</summary>
public sealed class EntityTable
{
    /// <summary>Capacity (entities in view).</summary>
    public int Capacity => throw new NotImplementedException();
    /// <summary>Slot of a handle, -1 when not in view.</summary>
    public int SlotOf(int handle) => throw new NotImplementedException();
    /// <summary>Kind bits of a slot.</summary>
    public EntityKind Kind(int slot) => throw new NotImplementedException();
    /// <summary>Mob id (-1 non-mob or no id).</summary>
    public int MobId(int slot) => throw new NotImplementedException();
    /// <summary>HP, -1 unknown.</summary>
    public double Hp(int slot) => throw new NotImplementedException();
    /// <summary>Max HP.</summary>
    public double MaxHp(int slot) => throw new NotImplementedException();
    /// <summary>Position extrapolated to now.</summary>
    public (double X, double Y) Pos(int slot, long nowMs) => throw new NotImplementedException();
    /// <summary>Velocity (units/s).</summary>
    public (double Vx, double Vy) Velocity(int slot) => throw new NotImplementedException();
    /// <summary>Facing, null if never moved.</summary>
    public (double Dx, double Dy)? Facing(int slot) => throw new NotImplementedException();
    /// <summary>Who it targets (-1 unknown).</summary>
    public int TargetOf(int slot) => throw new NotImplementedException();
    /// <summary>Last time it hit us (ms), -1 never.</summary>
    public long LastHitUsAt(int slot) => throw new NotImplementedException();
    /// <summary>Where it was last damaged (leash origin).</summary>
    public (uint X, uint Y)? LastHitPos(int slot) => throw new NotImplementedException();
    /// <summary>First idle position seen.</summary>
    public (uint X, uint Y)? SpawnPos(int slot) => throw new NotImplementedException();
    /// <summary>Apply an entity event (appear/leave/move/hp/target/die).</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
    /// <summary>Remove everything (map change).</summary>
    public void Clear() => throw new NotImplementedException();
}

/// <summary>Uniform-grid spatial index over the entity table (200 u cells).</summary>
public sealed class SpatialGrid
{
    /// <summary>Cell size in world units.</summary>
    public const double CellSize = 200;
    /// <summary>Move an entity's cell membership.</summary>
    public void Update(int slot, double x, double y) => throw new NotImplementedException();
    /// <summary>Remove a slot.</summary>
    public void Remove(int slot) => throw new NotImplementedException();
    /// <summary>Write slots within r of (x,y) matching a mask into dest, nearest first; returns the count.</summary>
    public int Query(double x, double y, double r, EntityKind mask, EntityTable table, Span<int> dest) => throw new NotImplementedException();
    /// <summary>Nearest slot matching the mask, -1 none (ring search, unbounded r allowed).</summary>
    public int Nearest(double x, double y, double maxR, EntityKind mask, EntityTable table) => throw new NotImplementedException();
    /// <summary>Count within r.</summary>
    public int Count(double x, double y, double r, EntityKind mask, EntityTable table) => throw new NotImplementedException();
}

/// <summary>Bag, equipment and per-item indexes.</summary>
public sealed class Inventory
{
    /// <summary>Item id and lot at a slot ((-1,0) empty; item id 0 is real).</summary>
    public (int ItemId, int Lot) At(int slot) => throw new NotImplementedException();
    /// <summary>Total count of an item id.</summary>
    public int Count(int itemId) => throw new NotImplementedException();
    /// <summary>First slot holding an item id, -1 none.</summary>
    public int Find(int itemId) => throw new NotImplementedException();
    /// <summary>Free bag slots.</summary>
    public int Free => throw new NotImplementedException();
    /// <summary>Bag size.</summary>
    public int Size => throw new NotImplementedException();
    /// <summary>Equipped item id by equipment slot, -1 none.</summary>
    public int Equipped(int equipSlot) => throw new NotImplementedException();
    /// <summary>HP/SP soul stone reserves.</summary>
    public (int Hp, int Sp) Stones => throw new NotImplementedException();
    /// <summary>Apply an item event.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Learned skills, cooldowns (incl. groups) confirmed by the server, empower.</summary>
public sealed class SkillBook
{
    /// <summary>Learned?</summary>
    public bool Has(int skillId) => throw new NotImplementedException();
    /// <summary>Milliseconds until ready (0 ready).</summary>
    public long ReadyInMs(int skillId, long nowMs) => throw new NotImplementedException();
    /// <summary>Empower levels (damage, sp, duration, cooldown).</summary>
    public (int Dmg, int Sp, int Dur, int Cd) Empower(int skillId) => throw new NotImplementedException();
    /// <summary>Highest learned rank of a family, -1 none.</summary>
    public int HighestRank(int familyId) => throw new NotImplementedException();
    /// <summary>Learned skill ids, highest rank per family, matching a tag mask; returns count.</summary>
    public int List(int tagMask, Span<int> dest) => throw new NotImplementedException();
    /// <summary>Apply skill list / cooldown / learn events.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Quest log: active, done, progress.</summary>
public sealed class QuestLog
{
    /// <summary>Active?</summary>
    public bool IsActive(int questId) => throw new NotImplementedException();
    /// <summary>Done?</summary>
    public bool IsDone(int questId) => throw new NotImplementedException();
    /// <summary>Progress of objective i (1-based).</summary>
    public (int Have, int Need) Progress(int questId, int objective) => throw new NotImplementedException();
    /// <summary>Active quest ids; returns count.</summary>
    public int Active(Span<int> dest) => throw new NotImplementedException();
    /// <summary>Free log slots.</summary>
    public int SlotsFree => throw new NotImplementedException();
    /// <summary>Apply quest events.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Party membership.</summary>
public sealed class PartyState
{
    /// <summary>Member handles in view; returns count.</summary>
    public int Members(Span<int> dest) => throw new NotImplementedException();
    /// <summary>Party size.</summary>
    public int Size => throw new NotImplementedException();
    /// <summary>Apply party events.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Trade window state.</summary>
public enum TradePhase : byte { None, Asked, Proposed, Open, Locked, Confirmed, Done, Cancelled }

/// <summary>Trade state.</summary>
public sealed class TradeState
{
    /// <summary>Current phase.</summary>
    public TradePhase Phase => throw new NotImplementedException();
    /// <summary>Partner handle, -1 none.</summary>
    public int Partner => throw new NotImplementedException();
    /// <summary>Apply trade events.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Current map, doors, gates.</summary>
public sealed class MapState
{
    /// <summary>Map id (-1 unknown).</summary>
    public int MapId => throw new NotImplementedException();
    /// <summary>Map short name.</summary>
    public string MapName => throw new NotImplementedException();
    /// <summary>Instance map?</summary>
    public bool IsInstance => throw new NotImplementedException();
    /// <summary>Apply map/door events.</summary>
    public void Apply(in InboundEvent evt) => throw new NotImplementedException();
}

/// <summary>Everything one bot knows, owned by its tick thread. Inbound events are applied at the start of the tick.</summary>
public sealed class WorldState
{
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
    /// <summary>Version of a component (incremented on every change).</summary>
    public int Version(WorldComponent component) => throw new NotImplementedException();
    /// <summary>Apply one inbound event and raise the derived brain events.</summary>
    public void Apply(in InboundEvent evt, BrainEventBus events) => throw new NotImplementedException();
}
