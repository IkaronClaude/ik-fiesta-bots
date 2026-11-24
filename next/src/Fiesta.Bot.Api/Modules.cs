// Mirrors the API_SPEC.md section 2 tables; the [LuaFn] source generator binds these.
using Fiesta.Bot.Agent;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `self` (30 functions, API_SPEC 2).</summary>
[LuaModule("self")]
public sealed class SelfModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Our zone handle, -1 before zone entry.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Handle() => throw new NotImplementedException();
    /// <summary>Character name.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string Name() => throw new NotImplementedException();
    /// <summary>ClassName id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int ClassId() => throw new NotImplementedException();
    /// <summary>Base class id of our class (from client data).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int ClassFamily() => throw new NotImplementedException();
    /// <summary>Level.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Level() => throw new NotImplementedException();
    /// <summary>Experience.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long Exp() => throw new NotImplementedException();
    /// <summary>Experience needed for the next level.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long ExpNext() => throw new NotImplementedException();
    /// <summary>Current HP.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Hp() => throw new NotImplementedException();
    /// <summary>Max HP.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double MaxHp() => throw new NotImplementedException();
    /// <summary>HP percent.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double HpPct() => throw new NotImplementedException();
    /// <summary>Current SP.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Sp() => throw new NotImplementedException();
    /// <summary>Max SP.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double MaxSp() => throw new NotImplementedException();
    /// <summary>SP percent.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double SpPct() => throw new NotImplementedException();
    /// <summary>Believed position.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double X, double Y) Pos() => throw new NotImplementedException();
    /// <summary>Map short name.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string Map() => throw new NotImplementedException();
    /// <summary>Map id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int MapId() => throw new NotImplementedException();
    /// <summary>Instance map?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsInstance() => throw new NotImplementedException();
    /// <summary>Facing unit vector.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Dx, double Dy) Facing() => throw new NotImplementedException();
    /// <summary>Moving now?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Moving() => throw new NotImplementedException();
    /// <summary>Casting now?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Casting() => throw new NotImplementedException();
    /// <summary>Mounted?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Mounted() => throw new NotImplementedException();
    /// <summary>Dead?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Dead() => throw new NotImplementedException();
    /// <summary>Hit or hitting within 8 s.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool InCombat() => throw new NotImplementedException();
    /// <summary>Money (never unknown).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long Money() => throw new NotImplementedException();
    /// <summary>Free bag slots.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int BagFree() => throw new NotImplementedException();
    /// <summary>Bag size.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int BagSize() => throw new NotImplementedException();
    /// <summary>Run speed (units/s).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double RunSpeed() => throw new NotImplementedException();
    /// <summary>Abstate present?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool HasAbstate(int id) => throw new NotImplementedException();
    /// <summary>Abstate time left, -1 absent.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long AbstateLeftMs(int id) => throw new NotImplementedException();
    /// <summary>Displayed stat.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Stat(int statId) => throw new NotImplementedException();
}

/// <summary>Lua module `ents` (26 functions, API_SPEC 2).</summary>
[LuaModule("ents")]
public sealed class EntsModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Handles within r, nearest first.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter Near(double r, int mask = 0) => throw new NotImplementedException();
    /// <summary>Count within r.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int Count(double r, int mask = 0) => throw new NotImplementedException();
    /// <summary>Nearest matching handle, -1 none.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int Nearest(int mask = 0, double r = -1) => throw new NotImplementedException();
    /// <summary>Write handles into a caller-owned Lua table; returns n.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int Fill(int tableRef, double r, int mask = 0) => throw new NotImplementedException();
    /// <summary>Handle in view?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Exists(int h) => throw new NotImplementedException();
    /// <summary>Kind bit.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Kind(int h) => throw new NotImplementedException();
    /// <summary>Mob id, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int MobId(int h) => throw new NotImplementedException();
    /// <summary>Name.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string Name(int h) => throw new NotImplementedException();
    /// <summary>Level (client MobInfo).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Level(int h) => throw new NotImplementedException();
    /// <summary>Grade type.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Grade(int h) => throw new NotImplementedException();
    /// <summary>HP, -1 unknown.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Hp(int h) => throw new NotImplementedException();
    /// <summary>Max HP.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double MaxHp(int h) => throw new NotImplementedException();
    /// <summary>HP percent, -1 unknown.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double HpPct(int h) => throw new NotImplementedException();
    /// <summary>Extrapolated position.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double X, double Y) Pos(int h) => throw new NotImplementedException();
    /// <summary>Straight-line distance from us.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Dist(int h) => throw new NotImplementedException();
    /// <summary>Walkable route length, -1 not computed yet.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public double RouteDist(int h) => throw new NotImplementedException();
    /// <summary>Facing, nil if never moved.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Dx, double Dy)? Facing(int h) => throw new NotImplementedException();
    /// <summary>Velocity (units/s).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Vx, double Vy) Velocity(int h) => throw new NotImplementedException();
    /// <summary>Position after ms.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double X, double Y) PredictPos(int h, int ms) => throw new NotImplementedException();
    /// <summary>Dead (corpse still listed)?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsDead(int h) => throw new NotImplementedException();
    /// <summary>Who it targets, -1 unknown.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TargetOf(int h) => throw new NotImplementedException();
    /// <summary>Aggro on us (confident)?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsAggroOnUs(int h) => throw new NotImplementedException();
    /// <summary>Ms since it hit us, -1 never.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long LastHitUsMs(int h) => throw new NotImplementedException();
    /// <summary>Body size.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Body(int h) => throw new NotImplementedException();
    /// <summary>First idle position seen.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double X, double Y)? SpawnPos(int h) => throw new NotImplementedException();
    /// <summary>Where it was last damaged (leash origin).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double X, double Y)? LastHitPos(int h) => throw new NotImplementedException();
}

/// <summary>Lua module `threat` (13 functions, API_SPEC 2).</summary>
[LuaModule("threat")]
public sealed class ThreatModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Mobs aggroed on us.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int OnUs() => throw new NotImplementedException();
    /// <summary>Expected incoming damage per second.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double IncomingDps() => throw new NotImplementedException();
    /// <summary>HP/s we can recover.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public double SustainableHps() => throw new NotImplementedException();
    /// <summary>Losing the race (incoming vs recovery, time to die vs time to kill).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Outmatched() => throw new NotImplementedException();
    /// <summary>Hostiles within r of h.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int PackAround(int h, double r) => throw new NotImplementedException();
    /// <summary>Mobs the walk to h would wake (aggro shapes + assists).</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int WouldWake(int h) => throw new NotImplementedException();
    /// <summary>Target-picker score.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public double PullCost(int h) => throw new NotImplementedException();
    /// <summary>Synchronised first-hit damage if we engage h.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public double BurstIfEngaged(int h) => throw new NotImplementedException();
    /// <summary>Point inside h's detect shape?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool InAggroShape(int h, double x, double y) => throw new NotImplementedException();
    /// <summary>Detect radius estimate, -1 unknown.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double DetectRadius(int h) => throw new NotImplementedException();
    /// <summary>Distance until h gives up the chase.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double LeashLeft(int h) => throw new NotImplementedException();
    /// <summary>Family assist radius, -1 unknown.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double AssistRadius(int h) => throw new NotImplementedException();
    /// <summary>Reachable point within r outside all aggro shapes.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public (double X, double Y)? SafeSpot(double r) => throw new NotImplementedException();
}

/// <summary>Lua module `combat` (13 functions, API_SPEC 2).</summary>
[LuaModule("combat")]
public sealed class CombatModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Auto-attack reach against h.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double BashReach(int h) => throw new NotImplementedException();
    /// <summary>Skill reach against h.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double SkillReach(int skill, int h) => throw new NotImplementedException();
    /// <summary>Within bash reach?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool InBashReach(int h) => throw new NotImplementedException();
    /// <summary>Line of fire (attack-block layer).</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public bool Los(int h) => throw new NotImplementedException();
    /// <summary>Server-confirmed target.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Target() => throw new NotImplementedException();
    /// <summary>Battle mode asserted?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool BattleMode() => throw new NotImplementedException();
    /// <summary>Auto-attacking?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Bashing() => throw new NotImplementedException();
    /// <summary>Auto-attack target, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int BashTarget() => throw new NotImplementedException();
    /// <summary>Ms since our last landed swing, -1 never.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long LastSwingMs() => throw new NotImplementedException();
    /// <summary>Our damage/s this fight.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double DpsOut() => throw new NotImplementedException();
    /// <summary>Seconds to kill h.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double TimeToKill(int h) => throw new NotImplementedException();
    /// <summary>Seconds until we die at the current rates.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double TimeToDie() => throw new NotImplementedException();
    /// <summary>Kills this session.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Kills() => throw new NotImplementedException();
}

/// <summary>Lua module `skills` (21 functions, API_SPEC 2).</summary>
[LuaModule("skills")]
public sealed class SkillsModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Learned?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Has(int id) => throw new NotImplementedException();
    /// <summary>Learned skills (highest rank per family) with the tags.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.Iterator)]
    public LuaIter List(int tagMask = 0) => throw new NotImplementedException();
    /// <summary>Best usable skill with the tags, -1 none.</summary>
    [LuaFn(Cost = Cost.On, Alloc = Alloc.None)]
    public int Best(int tagMask, int target) => throw new NotImplementedException();
    /// <summary>Generic objective check (cooldown, cost, state, weapon, target, range, line of fire, map).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None, GlobalAlias = "canUse")]
    public bool CanUse(int id, int target) => throw new NotImplementedException();
    /// <summary>First failing check.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.Small)]
    public (int Code, string Text) WhyNot(int id, int target) => throw new NotImplementedException();
    /// <summary>Off cooldown (incl. group)?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Ready(int id) => throw new NotImplementedException();
    /// <summary>Ms until ready.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long ReadyInMs(int id) => throw new NotImplementedException();
    /// <summary>Cost after empower.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None, Name = "cost")]
    public (int Sp, int Hp) CostOf(int id) => throw new NotImplementedException();
    /// <summary>Cast time after empower.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long CastTimeMs(int id) => throw new NotImplementedException();
    /// <summary>Cooldown after empower.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long CooldownMs(int id) => throw new NotImplementedException();
    /// <summary>Raw data range.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Range(int id) => throw new NotImplementedException();
    /// <summary>Area radius.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Area(int id) => throw new NotImplementedException();
    /// <summary>Castable while moving?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsMoving(int id) => throw new NotImplementedException();
    /// <summary>Has a tag?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool HasTag(int id, int tag) => throw new NotImplementedException();
    /// <summary>Tag mask.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Tags(int id) => throw new NotImplementedException();
    /// <summary>Skill family id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Family(int id) => throw new NotImplementedException();
    /// <summary>Rank within the family.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Rank(int id) => throw new NotImplementedException();
    /// <summary>Highest learned rank, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int HighestRank(int family) => throw new NotImplementedException();
    /// <summary>Expected heal on us.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double HealAmount(int id) => throw new NotImplementedException();
    /// <summary>Expected damage against h.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double DamageEstimate(int id, int h) => throw new NotImplementedException();
    /// <summary>Empower levels.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int Dmg, int Sp, int Dur, int Cd) Empower(int id) => throw new NotImplementedException();
}

/// <summary>Lua module `act` (26 functions, API_SPEC 2).</summary>
[LuaModule("act")]
public sealed class ActModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Object cast.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Cast(int skill, int h) => throw new NotImplementedException();
    /// <summary>Ground cast.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int CastAt(int skill, double x, double y) => throw new NotImplementedException();
    /// <summary>Auto-attack.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Bash(int h) => throw new NotImplementedException();
    /// <summary>Stop auto-attack.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int StopBash() => throw new NotImplementedException();
    /// <summary>Select a target.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Target(int h) => throw new NotImplementedException();
    /// <summary>Clear the target.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Untarget() => throw new NotImplementedException();
    /// <summary>Path + walk; resolves on arrival or failure.</summary>
    [LuaFn(Cost = Cost.Job, Alloc = Alloc.None)]
    public int MoveTo(double x, double y) => throw new NotImplementedException();
    /// <summary>One straight leg.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int MoveDirect(double x, double y) => throw new NotImplementedException();
    /// <summary>Follow until cancelled.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Follow(int h, double dist) => throw new NotImplementedException();
    /// <summary>Stop and commit the position.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Stop() => throw new NotImplementedException();
    /// <summary>Mount.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Mount() => throw new NotImplementedException();
    /// <summary>Dismount (procedure).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Dismount() => throw new NotImplementedException();
    /// <summary>Use an item by slot.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Use(int slot) => throw new NotImplementedException();
    /// <summary>Use an item by id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int UseItem(int itemId) => throw new NotImplementedException();
    /// <summary>Use an HP soul stone.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int StoneHp() => throw new NotImplementedException();
    /// <summary>Use an SP soul stone.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int StoneSp() => throw new NotImplementedException();
    /// <summary>Pick up a drop.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Loot(int h) => throw new NotImplementedException();
    /// <summary>Revive (procedure).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Revive() => throw new NotImplementedException();
    /// <summary>Local chat (rate limited).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Say(string text) => throw new NotImplementedException();
    /// <summary>Whisper.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Whisper(string name, string text) => throw new NotImplementedException();
    /// <summary>Action state.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string State(int a) => throw new NotImplementedException();
    /// <summary>Refusal/error code, 0 ok.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Result(int a) => throw new NotImplementedException();
    /// <summary>Text for the code.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string Reason(int a) => throw new NotImplementedException();
    /// <summary>Cancel (best effort).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Cancel(int a) => throw new NotImplementedException();
    /// <summary>Newest unresolved action of a kind, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Pending(int kind) => throw new NotImplementedException();
    /// <summary>Why the last call returned -1.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int LastRejection() => throw new NotImplementedException();
}

/// <summary>Lua module `items` (13 functions, API_SPEC 2).</summary>
[LuaModule("items")]
public sealed class ItemsModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Total count.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Count(int itemId) => throw new NotImplementedException();
    /// <summary>First slot, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Find(int itemId) => throw new NotImplementedException();
    /// <summary>Slots matching a category mask.</summary>
    [LuaFn(Cost = Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Slots(int mask = 0) => throw new NotImplementedException();
    /// <summary>Item at a slot, (-1,0) empty.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int ItemId, int Lot) At(int slot) => throw new NotImplementedException();
    /// <summary>Item class.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Class(int itemId) => throw new NotImplementedException();
    /// <summary>Level requirement.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int LevelReq(int itemId) => throw new NotImplementedException();
    /// <summary>Sell price.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long SellPrice(int itemId) => throw new NotImplementedException();
    /// <summary>Quest item?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsQuestItem(int itemId) => throw new NotImplementedException();
    /// <summary>Smallest potion covering the deficit, -1 none.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public int BestPotion(double deficit, int kindMask) => throw new NotImplementedException();
    /// <summary>Value of selling all junk.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public long JunkValue() => throw new NotImplementedException();
    /// <summary>Bag slot that improves our equipment, -1 none.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public int EquipUpgrade() => throw new NotImplementedException();
    /// <summary>Equipped item id, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Equipped(int equipSlot) => throw new NotImplementedException();
    /// <summary>Slots into a caller-owned table; returns n.</summary>
    [LuaFn(Cost = Cost.On, Alloc = Alloc.None)]
    public int Fill(int tableRef, int mask = 0) => throw new NotImplementedException();
}

/// <summary>Lua module `quests` (16 functions, API_SPEC 2).</summary>
[LuaModule("quests")]
public sealed class QuestsModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Active quest ids.</summary>
    [LuaFn(Cost = Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Active() => throw new NotImplementedException();
    /// <summary>Acceptable quest ids.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.Iterator)]
    public LuaIter Available() => throw new NotImplementedException();
    /// <summary>Active?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsActive(int q) => throw new NotImplementedException();
    /// <summary>Done?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool IsDone(int q) => throw new NotImplementedException();
    /// <summary>All objectives met?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Ready(int q) => throw new NotImplementedException();
    /// <summary>Objective progress.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int Have, int Need) Progress(int q, int i) => throw new NotImplementedException();
    /// <summary>Objective count.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Objectives(int q) => throw new NotImplementedException();
    /// <summary>Objective definition.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int Kind, int MobId, int ItemId, int Need) Objective(int q, int i) => throw new NotImplementedException();
    /// <summary>Quest name.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string Name(int q) => throw new NotImplementedException();
    /// <summary>Level range.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int Min, int Max) Levels(int q) => throw new NotImplementedException();
    /// <summary>Start NPC id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Giver(int q) => throw new NotImplementedException();
    /// <summary>Repeatable?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Repeatable(int q) => throw new NotImplementedException();
    /// <summary>Exp reward.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None, Name = "exp")]
    public long ExpOf(int q) => throw new NotImplementedException();
    /// <summary>Free quest-log slots.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int SlotsFree() => throw new NotImplementedException();
    /// <summary>Score candidates off the tick; returns a ticket.</summary>
    [LuaFn(Cost = Cost.Job, Alloc = Alloc.None)]
    public int Plan(int policyId) => throw new NotImplementedException();
    /// <summary>Plan result.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int QuestId, int Kind) PlanResult(int ticket) => throw new NotImplementedException();
}

/// <summary>Lua module `nav` (15 functions, API_SPEC 2).</summary>
[LuaModule("nav")]
public sealed class NavModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Request a path; returns a ticket.</summary>
    [LuaFn(Cost = Cost.Job, Alloc = Alloc.None)]
    public int Path(double x, double y) => throw new NotImplementedException();
    /// <summary>pending / ready / failed.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string PathState(int ticket) => throw new NotImplementedException();
    /// <summary>Length of a ready path.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double PathLength(int ticket) => throw new NotImplementedException();
    /// <summary>Route length, -1 unreachable.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public double RouteLength(double x, double y) => throw new NotImplementedException();
    /// <summary>Region connectivity with doors.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public bool Reachable(double x, double y) => throw new NotImplementedException();
    /// <summary>Walkable tile (block layer + overlay).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Walkable(double x, double y) => throw new NotImplementedException();
    /// <summary>Nearest walkable point (client rule).</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public (double X, double Y) Snap(double x, double y) => throw new NotImplementedException();
    /// <summary>Reachable walkable point away from NPCs.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public (double X, double Y)? ClearSpot(double minFromNpc, double r) => throw new NotImplementedException();
    /// <summary>Aggro shapes crossed by the route.</summary>
    [LuaFn(Cost = Cost.Cached, Alloc = Alloc.None)]
    public double RouteRisk(double x, double y) => throw new NotImplementedException();
    /// <summary>Gate toward a map, -1 none.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int GateTo(string mapName) => throw new NotImplementedException();
    /// <summary>Multi-map travel action.</summary>
    [LuaFn(Cost = Cost.Job, Alloc = Alloc.None)]
    public int Travel(string mapName) => throw new NotImplementedException();
    /// <summary>Travel in progress?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Traveling() => throw new NotImplementedException();
    /// <summary>Next kite point keeping gap from h.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public (double X, double Y) KiteStep(int h, double gap) => throw new NotImplementedException();
    /// <summary>Map name of an id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string MapOf(int mapId) => throw new NotImplementedException();
    /// <summary>Euclidean distance.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double Distance(double x1, double y1, double x2, double y2) => throw new NotImplementedException();
}

/// <summary>Lua module `npc` (11 functions, API_SPEC 2).</summary>
[LuaModule("npc")]
public sealed class NpcModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>NPCs in view with a role.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter Find(int roleMask) => throw new NotImplementedException();
    /// <summary>Nearest NPC with a role, -1 none.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.None)]
    public int Nearest(int roleMask) => throw new NotImplementedException();
    /// <summary>Known NPC with a role on a map (knowledge store).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (int NpcId, double X, double Y)? Known(int roleMask, string map) => throw new NotImplementedException();
    /// <summary>Role mask.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Role(int h) => throw new NotImplementedException();
    /// <summary>NPC id.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int NpcId(int h) => throw new NotImplementedException();
    /// <summary>Open the shop (procedure); action.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int OpenShop(int h) => throw new NotImplementedException();
    /// <summary>Open shop sells it?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool ShopHas(int itemId) => throw new NotImplementedException();
    /// <summary>Buy from the open shop.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Buy(int itemId, int lot) => throw new NotImplementedException();
    /// <summary>Sell to the open shop.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Sell(int slot, int lot) => throw new NotImplementedException();
    /// <summary>Quest dialogue (procedure).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Talk(int h, int questId) => throw new NotImplementedException();
    /// <summary>Open storage (procedure).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Storage(int h) => throw new NotImplementedException();
}

/// <summary>Lua module `social` (14 functions, API_SPEC 2).</summary>
[LuaModule("social")]
public sealed class SocialModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Invite.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int PartyInvite(string name) => throw new NotImplementedException();
    /// <summary>Accept the pending invite.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int PartyAccept() => throw new NotImplementedException();
    /// <summary>Leave.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int PartyLeave() => throw new NotImplementedException();
    /// <summary>Member handles in view.</summary>
    [LuaFn(Cost = Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter PartyMembers() => throw new NotImplementedException();
    /// <summary>Party size.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int PartySize() => throw new NotImplementedException();
    /// <summary>Propose a trade.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradePropose(int h) => throw new NotImplementedException();
    /// <summary>Trade phase.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public string TradeState() => throw new NotImplementedException();
    /// <summary>Board an item.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradeOfferItem(int slot, int lot) => throw new NotImplementedException();
    /// <summary>Board money.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradeOfferMoney(long cen) => throw new NotImplementedException();
    /// <summary>Lock.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradeLock() => throw new NotImplementedException();
    /// <summary>Confirm.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradeConfirm() => throw new NotImplementedException();
    /// <summary>Cancel.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int TradeCancel() => throw new NotImplementedException();
    /// <summary>Last local chat.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public (string Name, string Text) ChatLast() => throw new NotImplementedException();
    /// <summary>Last whisper.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public (string Name, string Text) WhisperLast() => throw new NotImplementedException();
}

/// <summary>Lua module `know` (9 functions, API_SPEC 2).</summary>
[LuaModule("know")]
public sealed class KnowModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Detect radius estimate.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Est, double Lo, double Hi, int N) Detect(int mobId) => throw new NotImplementedException();
    /// <summary>Chase distance estimate.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Est, double Lo, double Hi, int N) Leash(int mobId) => throw new NotImplementedException();
    /// <summary>Mob reach estimate.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Est, double Lo, double Hi, int N) Reach(int mobId) => throw new NotImplementedException();
    /// <summary>Swing interval estimate.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Est, double Lo, double Hi, int N) Interval(int mobId) => throw new NotImplementedException();
    /// <summary>Family assist radius.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Est, double Lo, double Hi, int N) Assist(int familyId) => throw new NotImplementedException();
    /// <summary>Hits taken from a mob.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Avg, double Max, int N) Hit(int mobId) => throw new NotImplementedException();
    /// <summary>Fitted armour.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (double Ac, double Mr, double Conf) Armour(int mobId) => throw new NotImplementedException();
    /// <summary>Median mob HP vs level median.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public double DungeonRatio(int mapId) => throw new NotImplementedException();
    /// <summary>Learned role mask.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int NpcRole(int npcId) => throw new NotImplementedException();
}

/// <summary>Lua module `time` (4 functions, API_SPEC 2).</summary>
[LuaModule("time")]
public sealed class TimeModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Tick-start clock (ms).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long Now() => throw new NotImplementedException();
    /// <summary>Ms since t.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long Since(long t) => throw new NotImplementedException();
    /// <summary>Tick counter.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public long Tick() => throw new NotImplementedException();
    /// <summary>True at most once per ms per key.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Every(int key, long ms) => throw new NotImplementedException();
}

/// <summary>Lua module `ev` (4 functions, API_SPEC 2).</summary>
[LuaModule("ev")]
public sealed class EvModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Event fired this tick?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Fired(int kind) => throw new NotImplementedException();
    /// <summary>Occurrences in the last ms.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Count(int kind, long ms) => throw new NotImplementedException();
    /// <summary>Most recent occurrence.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public (long Tick, long Arg1, long Arg2) Last(int kind) => throw new NotImplementedException();
    /// <summary>Text of the latest chat/whisper event.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.LuaString)]
    public (string Name, string Text) ChatText() => throw new NotImplementedException();
}

/// <summary>Lua module `world` (1 functions, API_SPEC 2).</summary>
[LuaModule("world")]
public sealed class WorldModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Component version counter.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public int Version(int component) => throw new NotImplementedException();
}

/// <summary>Lua module `dbg` (7 functions, API_SPEC 2).</summary>
[LuaModule("dbg")]
public sealed class DbgModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Note line in the script category of the calling layer; formatted in C# only when enabled; budgeted.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.Small, GlobalAlias = "log")]
    public void Log(string fmt, LuaVarArgs args) => throw new NotImplementedException();
    /// <summary>Info line; formatted only when enabled.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.Small)]
    public void Logi(string fmt, LuaVarArgs args) => throw new NotImplementedException();
    /// <summary>Debug line; formatted only when enabled.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.Small)]
    public void Logv(string fmt, LuaVarArgs args) => throw new NotImplementedException();
    /// <summary>Crit line + debugger flag (never dropped, collapsed).</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.Small)]
    public void Crit(string fmt, LuaVarArgs args) => throw new NotImplementedException();
    /// <summary>Is the level on for this layer's script category?</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public bool Enabled(int level) => throw new NotImplementedException();
    /// <summary>Debugger watch value.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public void Watch(string name, double value) => throw new NotImplementedException();
    /// <summary>Profiler section boundary.</summary>
    [LuaFn(Cost = Cost.O1, Alloc = Alloc.None)]
    public void Mark(string section) => throw new NotImplementedException();
}
