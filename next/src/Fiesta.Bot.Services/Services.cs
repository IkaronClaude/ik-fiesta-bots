namespace Fiesta.Bot.Services;

/// <summary>Monotonic clock; the tick reads it once (replayable).</summary>
public interface IClock
{
    /// <summary>Milliseconds since an arbitrary epoch.</summary>
    long NowMs { get; }
    /// <summary>Wall-clock time for display.</summary>
    DateTime UtcNow { get; }
}

/// <summary>Real clock.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc/>
    public long NowMs => throw new NotImplementedException();
    /// <inheritdoc/>
    public DateTime UtcNow => throw new NotImplementedException();
}

/// <summary>Client MobInfo row (the fields the runtime uses).</summary>
public sealed record MobInfoRow(int Id, string InxName, string Name, int Level, int MaxHp, int WalkSpeed, int RunSpeed,
    bool IsNpc, int AbsoluteSize, int WeaponType, int GradeType, int Type);

/// <summary>Client ActiveSkill row (the fields the runtime uses).</summary>
public sealed record ActiveSkillRow(int Id, string InxName, string Name, int Range, int Area, int TargetNumber, int CastTimeMs,
    int DelayMs, int DelayGroup, int Sp, int Hp, bool IsMovingSkill, int First, int EffectType, int UsableDegree, int DemandWeapon);

/// <summary>Client ItemInfo row.</summary>
public sealed record ItemInfoRow(int Id, string InxName, string Name, int Type, int ItemClass, int DemandLevel, int WeaponType,
    int SellPrice, int MaxLot, bool IsQuestItem);

/// <summary>Client MapInfo row.</summary>
public sealed record MapInfoRow(int Id, string MapName, string Name, bool IsInstance);

/// <summary>A quest definition parsed from QuestData.</summary>
public sealed record QuestDef(int Id, string Name, int MinLevel, int MaxLevel, bool Repeatable, int StartNpc,
    IReadOnlyList<QuestObjectiveDef> Objectives, long Exp, string StartScript, string ActionScript, string FinishScript);

/// <summary>One quest objective.</summary>
public sealed record QuestObjectiveDef(QuestObjectiveKind Kind, int MobId, int ItemId, int Need);

/// <summary>Objective kinds.</summary>
public enum QuestObjectiveKind : byte { Kill, Collect, Talk, ReachLevel, Other }

/// <summary>Read-only client data (ressystem SHN), loaded once and shared.</summary>
public interface IClientData
{
    /// <summary>MobInfo by id, null if absent (0 is a valid id).</summary>
    MobInfoRow? Mob(int id);
    /// <summary>ActiveSkill by id.</summary>
    ActiveSkillRow? Skill(int id);
    /// <summary>ItemInfo by id.</summary>
    ItemInfoRow? Item(int id);
    /// <summary>MapInfo by id.</summary>
    MapInfoRow? Map(int id);
    /// <summary>MapInfo by short name.</summary>
    MapInfoRow? Map(string mapName);
    /// <summary>Quest by id.</summary>
    QuestDef? Quest(int id);
    /// <summary>QuestDialog text by dialog id.</summary>
    string? Dialog(int id);
}

/// <summary>Walkability for one map: .shbd OR .shab, with door overlays; immutable once built.</summary>
public sealed class BlockLayer
{
    /// <summary>World units per tile.</summary>
    public const double WorldPerTile = 6.25;
    /// <summary>Load a map's block files from a directory.</summary>
    public static BlockLayer Load(string blockInfoDir, string mapName) => throw new NotImplementedException();
    /// <summary>Width in tiles.</summary>
    public int Width => throw new NotImplementedException();
    /// <summary>Height in tiles.</summary>
    public int Height => throw new NotImplementedException();
    /// <summary>Is the tile blocked for movement (.shbd | .shab, doors applied)?</summary>
    public bool Blocked(int tx, int ty) => throw new NotImplementedException();
    /// <summary>Is the tile in the attack-block layer (line of fire)?</summary>
    public bool AttackBlocked(int tx, int ty) => throw new NotImplementedException();
    /// <summary>World to tile.</summary>
    public static (int Tx, int Ty) ToTile(double x, double y) => throw new NotImplementedException();
}

/// <summary>Per-bot changes on top of the shared block layer: learned blocks with TTL, observed doors.</summary>
public sealed class NavOverlay
{
    /// <summary>Record a server-refused segment end as blocked for a time.</summary>
    public void LearnBlocked(int tx, int ty, long untilMs) => throw new NotImplementedException();
    /// <summary>Observed door state.</summary>
    public void SetDoor(string door, bool closed) => throw new NotImplementedException();
    /// <summary>Is the tile blocked by the overlay at time now?</summary>
    public bool Blocked(int tx, int ty, long nowMs) => throw new NotImplementedException();
    /// <summary>Forget everything (map change).</summary>
    public void Clear() => throw new NotImplementedException();
}

/// <summary>Rectangle/portal split of a block layer (kept from today's NavMesh).</summary>
public sealed class NavMesh
{
    /// <summary>Build from a block layer.</summary>
    public static NavMesh Build(BlockLayer layer) => throw new NotImplementedException();
    /// <summary>Region index at a tile, -1 outside.</summary>
    public int RegionAt(int tx, int ty) => throw new NotImplementedException();
    /// <summary>Number of regions.</summary>
    public int RegionCount => throw new NotImplementedException();
}

/// <summary>Outcome of a path search.</summary>
public enum PathStatus : byte { Pending, Ready, Failed }

/// <summary>A finished path (pooled waypoint array, world units).</summary>
public sealed record PathResult(PathStatus Status, IReadOnlyList<(uint X, uint Y)> Waypoints, double Length, string? FailReason);

/// <summary>Corridor A* over regions + funnel + corner nudge + goal snapping (ported from fiesta-godot).</summary>
public static class PathSearch
{
    /// <summary>Find a path in tiles; null when unreachable.</summary>
    public static List<(int X, int Y)>? Find(NavMesh mesh, BlockLayer layer, NavOverlay? overlay, int sx, int sy, int gx, int gy) => throw new NotImplementedException();
    /// <summary>Snap a target to the nearest walkable tile, ring search toward the start (client behaviour).</summary>
    public static (int X, int Y)? SnapGoal(BlockLayer layer, NavOverlay? overlay, int sx, int sy, int gx, int gy) => throw new NotImplementedException();
    /// <summary>Region connectivity with doors.</summary>
    public static bool Reachable(NavMesh mesh, int sx, int sy, int gx, int gy) => throw new NotImplementedException();
}

/// <summary>Shared navigation per map plus the job entry points.</summary>
public interface INavService
{
    /// <summary>Block layer for a map (loaded once).</summary>
    BlockLayer Layer(string mapName);
    /// <summary>Mesh for a map (built once, shared).</summary>
    NavMesh Mesh(string mapName);
    /// <summary>Queue a path search; the result is posted to the bot as a job completion.</summary>
    JobTicket RequestPath(int botIndex, string mapName, NavOverlay overlay, uint fx, uint fy, uint tx, uint ty);
    /// <summary>Read a finished path.</summary>
    PathResult? Result(JobTicket ticket);
}

/// <summary>Job kinds that run off the tick.</summary>
public enum JobKind : byte { Path, RouteLength, QuestPlan, TravelPlan, SafeSpot, Custom }

/// <summary>Handle to a job.</summary>
public readonly record struct JobTicket(int Value);

/// <summary>Bounded worker pool for off-tick work; completions are posted to the owning bot's inbound lane.</summary>
public interface IJobScheduler
{
    /// <summary>Submit work; never blocks (rejects with an invalid ticket when saturated).</summary>
    JobTicket Submit(int botIndex, JobKind kind, Func<CancellationToken, object?> work);
    /// <summary>Result of a completed job, null while pending.</summary>
    object? Result(JobTicket ticket);
    /// <summary>Cancel if not started.</summary>
    bool Cancel(JobTicket ticket);
}

/// <summary>An interval estimate learned from observations.</summary>
public sealed record Estimate(double Value, double Low, double High, int Samples);

/// <summary>What a mob estimator tracks.</summary>
public enum MobFact : byte { DetectRadius, ChaseDistance, Reach, AttackInterval, Armour, MagicResist, HitAvg, HitMax }

/// <summary>Host-wide learned knowledge (server-only facts estimated from the wire), persisted and shared.</summary>
public interface IKnowledgeStore
{
    /// <summary>Current estimate for a mob fact.</summary>
    Estimate Mob(int mobId, MobFact fact);
    /// <summary>Add a lower bound observation (e.g. noticed us at distance d).</summary>
    void ObserveAtLeast(int mobId, MobFact fact, double value);
    /// <summary>Add an upper bound observation (e.g. did not notice us at distance d).</summary>
    void ObserveAtMost(int mobId, MobFact fact, double value);
    /// <summary>Add a direct sample (an interval, a hit).</summary>
    void Sample(int mobId, MobFact fact, double value);
    /// <summary>Family assist radius.</summary>
    Estimate Assist(int familyId);
    /// <summary>NPC role mask learned from menus.</summary>
    int NpcRole(int npcId);
    /// <summary>Record an NPC role.</summary>
    void LearnNpcRole(int npcId, int roleMask, int mapId, uint x, uint y);
}
