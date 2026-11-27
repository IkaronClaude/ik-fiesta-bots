// The Lua helper library (API_SPEC section 2). The [LuaFn] source generator binds these; every function runs on the
// bot's tick thread during the brain step. Return conventions: -1 / nil for "none" (0 is a valid id).
using System.Buffers.Binary;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Api;

/// <summary>Skill tags derived from client ActiveSkill columns (no per-skill table): Damage = a weapon or magic damage
/// value; Heal = effect type 5 (heal amount in SpecialValueA when SpecialIndexA is 1); Aoe = an area with more than
/// one target; Buff/Debuff = an abstate applied by a non-damaging / damaging skill; Self = range 0; Moving =
/// castable while moving; Stun = an applied abstate whose name says stun.</summary>
public static class SkillTags
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Cache = new();
    /// <summary>Tag mask of a skill.</summary>
    public static int Of(IClientData data, int id)
    {
        if (Cache.TryGetValue(id, out var t)) return t;
        var s = data.Skill(id);
        var m = 0;
        if (s is not null)
        {
            var dmg = s.MaxWc > 0 || s.MaxMa > 0;
            var heal = s.EffectType == 5;
            var sta = s.StaNameA is not ("-" or "") ? s.StaNameA : "";
            if (dmg && !heal) m |= (int)SkillTag.Damage;
            if (heal) m |= (int)SkillTag.Heal;
            if (s.Area > 0 && s.TargetNumber > 1) m |= (int)SkillTag.Aoe;
            if (s.Range == 0) m |= (int)SkillTag.Self;
            if (s.IsMovingSkill) m |= (int)SkillTag.Moving;
            if (sta.Length > 0) m |= dmg ? (int)SkillTag.Debuff : (int)SkillTag.Buff;
            if (sta.Contains("Stun", StringComparison.OrdinalIgnoreCase)) m |= (int)SkillTag.Stun;
            if (sta.Length > 0 && data is ClientData cd && cd.IsSlowState(sta)) m |= (int)SkillTag.Slow;
            if (s.EffectType == 8) m |= (int)SkillTag.Dispel;
        }
        Cache[id] = m;
        return m;
    }
}

/// <summary>Lua module `self`: our character.</summary>
[LuaModule("self")]
public sealed partial class SelfModule(AgentContext ctx) : ApiModule(ctx)
{
    private SelfState S => Ctx.World.Self;
    /// <summary>Our zone handle, -1 before zone entry.</summary>
    [LuaFn] public int Handle() => S.Handle;
    /// <summary>Character name.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Name() => S.Name;
    /// <summary>ClassName id.</summary>
    [LuaFn] public int ClassId() => S.ClassId;
    /// <summary>Base class of our class line (from client data).</summary>
    [LuaFn] public int ClassFamily() => S.ClassFamily;
    /// <summary>Level.</summary>
    [LuaFn] public int Level() => S.Level;
    /// <summary>Experience.</summary>
    [LuaFn] public long Exp() => S.Exp;
    /// <summary>Experience needed for the next level (cumulative).</summary>
    [LuaFn] public long ExpNext() => S.NextExp;
    /// <summary>The current level's exp span (next level's start minus this level's start), -1 unknown.</summary>
    [LuaFn] public long ExpSpan() => S.NextExp > S.PrevExp ? S.NextExp - S.PrevExp : -1;
    /// <summary>Exp earned into the current level (what a death can take at most).</summary>
    [LuaFn] public long ExpIntoLevel() => Math.Max(0, S.Exp - S.PrevExp);
    /// <summary>Current HP.</summary>
    [LuaFn] public double Hp() => S.Hp;
    /// <summary>Max HP.</summary>
    [LuaFn] public double MaxHp() => S.MaxHp;
    /// <summary>HP percent.</summary>
    [LuaFn] public double HpPct() => S.HpPct;
    /// <summary>Current SP.</summary>
    [LuaFn] public double Sp() => S.Sp;
    /// <summary>Max SP.</summary>
    [LuaFn] public double MaxSp() => S.MaxSp;
    /// <summary>SP percent.</summary>
    [LuaFn] public double SpPct() => S.SpPct;
    /// <summary>Believed position.</summary>
    [LuaFn] public (double X, double Y) Pos() => S.Pos;
    /// <summary>Map short name.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Map() => Ctx.World.Map.MapName;
    /// <summary>Map id.</summary>
    [LuaFn] public int MapId() => Ctx.World.Map.MapId;
    /// <summary>HP soul stones held.</summary>
    [LuaFn] public int HpStones() => S.HpStones;
    /// <summary>SP soul stones held.</summary>
    [LuaFn] public int SpStones() => S.SpStones;
    /// <summary>Soul stone capacity (HP, SP; -1 unknown).</summary>
    [LuaFn] public (long Hp, long Sp) MaxStones() => S.MaxStones;
    /// <summary>Unspent stat points.</summary>
    [LuaFn] public int StatPoints() => S.StatPoints;
    /// <summary>Fame (-1 unknown).</summary>
    [LuaFn] public long Fame() => S.Fame;
    /// <summary>Resting in the tent now (pitched and acked)?</summary>
    [LuaFn] public bool InTent() => S.TentPitched;
    /// <summary>Instance map?</summary>
    [LuaFn] public bool IsInstance() => Ctx.World.Map.IsInstance;
    /// <summary>Facing unit vector.</summary>
    [LuaFn] public (double Dx, double Dy) Facing() => S.Facing;
    /// <summary>Moving now?</summary>
    [LuaFn] public bool Moving() => Ctx.Move.Moving || Ctx.Move.Planning;
    /// <summary>Casting now?</summary>
    [LuaFn] public bool Casting() => Ctx.Combat.Casting(Ctx.Now);
    /// <summary>Mounted?</summary>
    [LuaFn] public bool Mounted() => S.Mounted;
    /// <summary>Dead?</summary>
    [LuaFn] public bool Dead() => S.Dead;
    /// <summary>Hit or hitting within 8 s.</summary>
    [LuaFn] public bool InCombat() => S.InCombat(Ctx.Now);
    /// <summary>Money (seeded at zone entry, never unknown).</summary>
    [LuaFn] public long Money() => S.Money;
    /// <summary>Free bag slots.</summary>
    [LuaFn] public int BagFree() => Ctx.World.Bag.Free;
    /// <summary>Bag size.</summary>
    [LuaFn] public int BagSize() => Ctx.World.Bag.Size;
    /// <summary>Run speed (units/s).</summary>
    [LuaFn] public double RunSpeed() => S.RunSpeed;
    /// <summary>Abstate present?</summary>
    [LuaFn] public bool HasAbstate(int id) => S.HasAbstate(id, Ctx.Now);
    /// <summary>Slowed or rooted right now (an active abnormal state whose client data cuts our speed or stops us):
    /// running away or kiting then loses ground, the fight is the better bet.</summary>
    [LuaFn]
    public bool Slowed()
    {
        if (Ctx.Data is not Fiesta.Bot.Services.ClientData cd) return false;
        for (var i = 0; i < S.AbstateCount; i++)
        {
            var (id, until) = S.AbstateAt(i);
            if (until <= Ctx.Now) continue;
            if (cd.IsSlowStateIndex(id) || cd.IsRootState(id)) return true;
        }
        return false;
    }
    /// <summary>Abstate time left (ms), -1 absent.</summary>
    [LuaFn] public long AbstateLeftMs(int id) { var v = S.AbstateLeftMs(id, Ctx.Now); return v == long.MaxValue ? int.MaxValue : v; }
    /// <summary>Displayed stat (not decoded yet: -1).</summary>
    [LuaFn] public double Stat(int statId) => S.Stat(statId);
}

/// <summary>Lua module `ents`: entities in view.</summary>
[LuaModule("ents")]
public sealed partial class EntsModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;
    private int Slot(int h) => W.Entities.SlotOf(h);
    private int Collect(double r, int mask, Span<int> dest)
    {
        var (x, y) = W.Self.Pos;
        var n = W.Grid.Query(x, y, r < 0 ? 4000 : r, (EntityKind)mask, W, Ctx.Now, dest);
        for (var i = 0; i < n; i++) dest[i] = W.Entities.HandleOf(dest[i]);
        return n;
    }
    /// <summary>Handles within r, nearest first (mask default HUNTABLE).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter Near(double r, int mask = 0) { var s = Ctx.Scratch(1024); var n = Collect(r, mask, s); return ApiScope.Iter(s[..n]); }
    /// <summary>Count within r.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)] public int Count(double r, int mask = 0) => Collect(r, mask, Ctx.Scratch(1024));
    /// <summary>Nearest matching handle, -1 none.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public int Nearest(int mask = 0, double r = -1)
    {
        var (x, y) = W.Self.Pos;
        var s = W.Grid.Nearest(x, y, r, (EntityKind)mask, W, Ctx.Now);
        return s < 0 ? -1 : W.Entities.HandleOf(s);
    }
    /// <summary>Write handles within r into a caller-owned table; returns n.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public LuaIter Fill(LuaTableArg tbl, double r, int mask = 0) { var s = Ctx.Scratch(1024); var n = Collect(r, mask, s); return ApiScope.Iter(s[..n]); }
    /// <summary>Handle in view?</summary>
    [LuaFn] public bool Exists(int h) => Slot(h) >= 0;
    /// <summary>Kind bits (with Alive / Aggro / Huntable), 0 when not in view.</summary>
    [LuaFn] public int Kind(int h) { var s = Slot(h); return s < 0 ? 0 : (int)W.KindOf(s, Ctx.Now); }
    /// <summary>Mob id, -1 for non-mobs.</summary>
    [LuaFn] public int MobId(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.MobId(s); }
    /// <summary>Name (players by name, mobs by MobInfo).</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string? Name(int h) { var s = Slot(h); return s < 0 ? null : W.Entities.NameOf(s); }
    /// <summary>Level (-1 unknown).</summary>
    [LuaFn] public int Level(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.Level(s); }
    /// <summary>MobInfo grade type (-1 unknown).</summary>
    [LuaFn] public int Grade(int h) { var s = Slot(h); return s < 0 ? -1 : Ctx.Data.Mob(W.Entities.MobId(s))?.GradeType ?? -1; }
    /// <summary>HP (-1 unknown).</summary>
    [LuaFn] public double Hp(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.Hp(s); }
    /// <summary>Max HP (-1 unknown).</summary>
    [LuaFn] public double MaxHp(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.MaxHp(s); }
    /// <summary>HP percent (-1 unknown).</summary>
    [LuaFn] public double HpPct(int h) { var s = Slot(h); if (s < 0) return -1; var m = W.Entities.MaxHp(s); return m > 0 ? W.Entities.Hp(s) * 100 / m : -1; }
    /// <summary>Position extrapolated along the last move.</summary>
    [LuaFn] public (double X, double Y) Pos(int h) { var s = Slot(h); return s < 0 ? (-1, -1) : W.Entities.Pos(s, Ctx.Now); }
    /// <summary>Straight-line distance from us (-1 not in view).</summary>
    [LuaFn] public double Dist(int h) { var s = Slot(h); if (s < 0) return -1; var (x, y) = W.Entities.Pos(s, Ctx.Now); return MoveController.Dist(x, y, W.Self.Pos.X, W.Self.Pos.Y); }
    /// <summary>Walkable route length: not computed synchronously in this runtime (-1); use nav.path.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)] public double RouteDist(int h) => -1;
    /// <summary>Facing (nil, nil when never moved).</summary>
    [LuaFn] public (double Dx, double Dy) Facing(int h) { var s = Slot(h); return s >= 0 && W.Entities.Facing(s) is { } f ? f : (0, 0); }
    /// <summary>Velocity (units/s).</summary>
    [LuaFn] public (double Vx, double Vy) Velocity(int h) { var s = Slot(h); return s < 0 ? (0, 0) : W.Entities.Velocity(s, Ctx.Now); }
    /// <summary>Position after ms along the current leg.</summary>
    [LuaFn] public (double X, double Y) PredictPos(int h, int ms) { var s = Slot(h); return s < 0 ? (-1, -1) : W.Entities.Pos(s, Ctx.Now + ms); }
    /// <summary>Dead (corpses stay listed until removed).</summary>
    [LuaFn] public bool IsDead(int h) { var s = Slot(h); return s >= 0 && W.Entities.IsDead(s); }
    /// <summary>Who it targets, -1 unknown.</summary>
    [LuaFn] public int TargetOf(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.TargetOf(s); }
    /// <summary>Hit us or targets us (confident).</summary>
    [LuaFn] public bool IsAggroOnUs(int h) { var s = Slot(h); return s >= 0 && (W.KindOf(s, Ctx.Now) & EntityKind.Aggro) != 0; }
    /// <summary>Ms since it last hit us, -1 never.</summary>
    [LuaFn] public long LastHitUsMs(int h) { var s = Slot(h); if (s < 0) return -1; var t = W.Entities.LastHitUsAt(s); return t < 0 ? -1 : Ctx.Now - t; }
    /// <summary>Body size.</summary>
    [LuaFn] public double Body(int h) { var s = Slot(h); return s < 0 ? -1 : W.Entities.BodyOf(s); }
    /// <summary>First position observed.</summary>
    [LuaFn] public (double X, double Y) SpawnPos(int h) { var s = Slot(h); return s < 0 ? (-1, -1) : W.Entities.SpawnPos(s); }
    /// <summary>Where it was last damaged (leash origin), -1,-1 never.</summary>
    [LuaFn] public (double X, double Y) LastHitPos(int h) { var s = Slot(h); return s >= 0 && W.Entities.LastHitPos(s) is { } p ? p : (-1, -1); }
}

/// <summary>Lua module `threat`: aggro and danger estimates over the entities in view.</summary>
[LuaModule("threat")]
public sealed partial class ThreatModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;
    private int Count(EntityKind mask, double r) { var (x, y) = W.Self.Pos; return W.Grid.Query(x, y, r, mask, W, Ctx.Now, Ctx.Scratch(1024)); }
    /// <summary>Mobs aggroed on us.</summary>
    [LuaFn] public int OnUs() => Count(EntityKind.Mob | EntityKind.Aggro | EntityKind.Alive, 2500);
    /// <summary>Expected damage/s from aggressors (learned hit / interval, -1 unknown).</summary>
    [LuaFn]
    public double IncomingDps()
    {
        var s = Ctx.Scratch(1024); var (x, y) = W.Self.Pos;
        var n = W.Grid.Query(x, y, 2500, EntityKind.Mob | EntityKind.Aggro | EntityKind.Alive, W, Ctx.Now, s);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var mob = W.Entities.MobId(s[i]);
            var hit = Ctx.Knowledge.Mob(mob, MobFact.HitAvg); var iv = Ctx.Knowledge.Mob(mob, MobFact.AttackInterval);
            sum += hit.Samples > 0 ? hit.Value / Math.Max(0.5, (iv.Samples > 0 ? iv.Value : 2000) / 1000.0) : 0;
        }
        return sum;
    }
    /// <summary>HP/s we can recover (soul stones only so far; -1 unknown).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)] public double SustainableHps() => W.Self.HpStones > 0 ? W.Self.MaxHp * 0.05 : 0;
    /// <summary>The fight is lost: the drain net of what we can heal would kill us within <paramref name="ttdS"/> seconds,
    /// and the attackers would not die first (within <paramref name="killFirst"/> of that time). The old Lua drivers'
    /// outmatched() (OUTMATCH_TTD_S 15, KILL_FIRST_MARGIN 0.6): "time to die before time to kill" alone sent bots
    /// running from fights they were winning (NextA fled at 79 % HP, 00:57).</summary>
    [LuaFn]
    public bool Outmatched(double ttdS = 15, double killFirst = 0.6)
    {
        var dps = IncomingDps() - SustainableHps();
        if (dps <= 0) return false;
        var ttd = W.Self.Hp / dps;
        if (ttd >= ttdS) return false;
        var mine = Ctx.Combat.DpsOut(Ctx.Now);
        double hpLeft = 0; var s = Ctx.Scratch(1024); var (x, y) = W.Self.Pos;
        var n = W.Grid.Query(x, y, 2500, EntityKind.Mob | EntityKind.Aggro | EntityKind.Alive, W, Ctx.Now, s);
        for (var i = 0; i < n; i++) hpLeft += Math.Max(0, W.Entities.Hp(s[i]));
        var ttk = mine > 0 ? hpLeft / mine : double.MaxValue;
        return ttk >= ttd * killFirst;
    }
    /// <summary>Other live mobs within r of h.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public int PackAround(int h, double r)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return 0;
        var (x, y) = W.Entities.Pos(sl, Ctx.Now);
        var n = W.Grid.Query(x, y, r, EntityKind.Mob | EntityKind.Alive, W, Ctx.Now, Ctx.Scratch(1024));
        return Math.Max(0, n - 1);
    }
    /// <summary>Mobs whose detect circle the straight walk to h crosses (unknown detect radius = not counted).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public int WouldWake(int h)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return 0;
        var (tx, ty) = W.Entities.Pos(sl, Ctx.Now); var (px, py) = W.Self.Pos;
        var s = Ctx.Scratch(1024);
        var n = W.Grid.Query((tx + px) / 2, (ty + py) / 2, MoveController.Dist(tx, ty, px, py) / 2 + 600, EntityKind.Mob | EntityKind.Alive, W, Ctx.Now, s);
        var c = 0;
        for (var i = 0; i < n; i++)
        {
            if (s[i] == sl || (W.KindOf(s[i], Ctx.Now) & EntityKind.Aggro) != 0) continue;
            var det = Ctx.Knowledge.Mob(W.Entities.MobId(s[i]), MobFact.DetectRadius);
            if (det.Samples == 0) continue;
            var (mx, my) = W.Entities.Pos(s[i], Ctx.Now);
            if (SegDist(px, py, tx, ty, mx, my) < det.Value) c++;
        }
        return c;
    }
    private static double SegDist(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        var t = l2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        return MoveController.Dist(ax + t * dx, ay + t * dy, px, py);
    }
    /// <summary>Target picker score: distance + 300 per mob it would wake + 200 per pack member (lower is better).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public double PullCost(int h)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return -1;
        var (x, y) = W.Entities.Pos(sl, Ctx.Now);
        return MoveController.Dist(x, y, W.Self.Pos.X, W.Self.Pos.Y) + 300 * WouldWake(h) + 200 * PackAround(h, 300);
    }
    /// <summary>First-hit damage if we engage h (its pack's average hits).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public double BurstIfEngaged(int h)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return 0;
        var hit = Ctx.Knowledge.Mob(W.Entities.MobId(sl), MobFact.HitAvg);
        return (hit.Samples > 0 ? hit.Value : 0) * (1 + PackAround(h, 300));
    }
    /// <summary>Is (x,y) inside h's aggro shape (detect circle offset 0.4 r along its facing)? False when unknown.</summary>
    [LuaFn]
    public bool InAggroShape(int h, double x, double y)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return false;
        var det = Ctx.Knowledge.Mob(W.Entities.MobId(sl), MobFact.DetectRadius);
        if (det.Samples == 0) return false;
        var (mx, my) = W.Entities.Pos(sl, Ctx.Now);
        var f = W.Entities.Facing(sl) ?? (0, 0);
        return MoveController.Dist(mx + f.Item1 * 0.4 * det.Value, my + f.Item2 * 0.4 * det.Value, x, y) < det.Value;
    }
    /// <summary>Detect radius estimate, -1 unknown.</summary>
    [LuaFn] public double DetectRadius(int h) { var sl = W.Entities.SlotOf(h); return sl < 0 ? -1 : Ctx.Knowledge.Mob(W.Entities.MobId(sl), MobFact.DetectRadius).Value; }
    /// <summary>Distance until h gives up the chase, -1 unknown.</summary>
    [LuaFn]
    public double LeashLeft(int h)
    {
        var sl = W.Entities.SlotOf(h); if (sl < 0) return -1;
        var chase = Ctx.Knowledge.Mob(W.Entities.MobId(sl), MobFact.ChaseDistance);
        if (chase.Samples == 0 || W.Entities.LastHitPos(sl) is not { } hp) return -1;
        var (x, y) = W.Entities.Pos(sl, Ctx.Now);
        return GameRules.LeashLeft(x, y, hp.X, hp.Y, chase.Value);
    }
    /// <summary>Family assist radius, -1 unknown.</summary>
    [LuaFn] public double AssistRadius(int h) => -1;
    /// <summary>A walkable point within r with no known aggro shape covering it (ring search); -1,-1 none.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public (double X, double Y) SafeSpot(double r)
    {
        var layer = Ctx.Nav.Layer(W.Map.MapName);
        var (px, py) = W.Self.Pos;
        var s = Ctx.Scratch(1024);
        var n = W.Grid.Query(px, py, r + 800, EntityKind.Mob | EntityKind.Alive, W, Ctx.Now, s);
        for (var ring = 1; ring <= 4; ring++)
            for (var k = 0; k < 16; k++)
            {
                var a = k * Math.PI / 8; var d = r * ring / 4;
                double x = px + Math.Cos(a) * d, y = py + Math.Sin(a) * d;
                if (layer is not null && !layer.WalkableWorld(x, y)) continue;
                var ok = true;
                for (var i = 0; i < n && ok; i++)
                {
                    var det = Ctx.Knowledge.Mob(W.Entities.MobId(s[i]), MobFact.DetectRadius);
                    var rad = det.Samples > 0 ? det.Value : 300;
                    var (mx, my) = W.Entities.Pos(s[i], Ctx.Now);
                    if (MoveController.Dist(mx, my, x, y) < rad) ok = false;
                }
                if (ok) return (x, y);
            }
        return (-1, -1);
    }
}

/// <summary>Lua module `combat`.</summary>
[LuaModule("combat")]
public sealed partial class CombatModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;
    /// <summary>Auto-attack reach against h: (body + weapon reach) * 1.25.</summary>
    [LuaFn] public double BashReach(int h) { var s = W.Entities.SlotOf(h); return s < 0 ? -1 : GameRules.Attackable(Ctx, s) + GameRules.ClientSlack; }
    /// <summary>The client's attackable distance to h: where it walks to (this - 1) before swinging.</summary>
    [LuaFn] public double Attackable(int h) { var s = W.Entities.SlotOf(h); return s < 0 ? -1 : GameRules.Attackable(Ctx, s); }
    /// <summary>The client's skillable distance for a skill on h: where it walks to (this - 1) before casting.</summary>
    [LuaFn] public double Skillable(int skill, int h) { var s = W.Entities.SlotOf(h); return GameRules.Skillable(Ctx, Ctx.Data.Skill(skill), s); }
    /// <summary>Skill reach against h.</summary>
    [LuaFn] public double SkillReach(int skill, int h) { var s = W.Entities.SlotOf(h); return GameRules.Skillable(Ctx, Ctx.Data.Skill(skill), s) + GameRules.ClientSlack; }
    /// <summary>Within reach to bash h: the start reach (body + weapon reach) when not already bashing h, the keep reach
    /// ((body + weapon reach) * 1.25) while a swing stream on h runs.</summary>
    [LuaFn]
    public bool InBashReach(int h)
    {
        var s = W.Entities.SlotOf(h); if (s < 0) return false;
        var (x, y) = W.Entities.Pos(s, Ctx.Now);
        var keep = Ctx.Combat.Bashing && Ctx.Combat.BashTarget == h;
        var reach = GameRules.Attackable(Ctx, s) + (keep ? GameRules.ClientSlack : GameRules.SwingStartSlack);
        // the game just refused swing starts on h from where we stood (its position of h differs from ours while h
        // moves): count it in reach only well inside, so the caller closes in instead of restarting from the edge
        if (!keep && Ctx.Combat.RefusedTarget == h && Ctx.Now - Ctx.Combat.RefusedAt < 5000) reach *= 0.6;
        return MoveController.Dist(x, y, W.Self.Pos.X, W.Self.Pos.Y) <= reach;
    }
    /// <summary>The reach to start a swing on h (body + weapon reach).</summary>
    [LuaFn] public double BashStartReach(int h) { var s = W.Entities.SlotOf(h); return s < 0 ? -1 : GameRules.Attackable(Ctx, s) + GameRules.SwingStartSlack; }
    /// <summary>Line of fire to h (attack-block layer).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)] public bool Los(int h) { var s = W.Entities.SlotOf(h); if (s < 0) return false; var (x, y) = W.Entities.Pos(s, Ctx.Now); return GameRules.LineOfFire(Ctx.Nav.Layer(W.Map.MapName), W.Self.Pos.X, W.Self.Pos.Y, x, y); }
    /// <summary>Our selection.</summary>
    [LuaFn] public int Target() => Ctx.Combat.Target;
    /// <summary>Battle mode asserted.</summary>
    [LuaFn] public bool BattleMode() => W.Self.BattleMode;
    /// <summary>Auto-attacking.</summary>
    [LuaFn] public bool Bashing() => Ctx.Combat.Bashing;
    /// <summary>Auto-attack target, -1 none.</summary>
    [LuaFn] public int BashTarget() => Ctx.Combat.BashTarget;
    /// <summary>Ms since our last landed swing, -1 none.</summary>
    [LuaFn] public long LastSwingMs() => Ctx.Combat.LastSwingAt < 0 ? -1 : Ctx.Now - Ctx.Combat.LastSwingAt;
    /// <summary>Our damage/s over the current fight.</summary>
    [LuaFn] public double DpsOut() => Ctx.Combat.DpsOut(Ctx.Now);
    /// <summary>Casts the game started (its cast start for us) and casts it refused, since the bot started: a fight's
    /// count is the difference across the fight.</summary>
    [LuaFn] public (int Started, int Refused) CastCounts() => ((int)Ctx.Combat.CastsStarted, (int)Ctx.Combat.CastsRefused);
    /// <summary>The burst decision for target h (Burst.Decide): commit?, time to kill and to die (s, inf = never), the
    /// first skill of the plan (-1 none: swings only / nothing). <paramref name="swings"/>: this character auto-attacks.</summary>
    [LuaFn] public (bool Commit, double Ttk, double Ttd, int First) Burst(int h, bool swings = false)
    { var p = Fiesta.Bot.Api.Burst.Decide(Ctx, h, swings); return (p.Commit, p.TtkS, p.TtdS, p.FirstSkill); }
    /// <summary>The burst plan as text for a log line: the verdict, why, and the planned sequence (allocates: call it
    /// only when logging).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)] public string BurstText(int h, bool swings = false)
    {
        var sb = new System.Text.StringBuilder();
        var p = Fiesta.Bot.Api.Burst.Decide(Ctx, h, swings, sb);
        return $"{(p.Commit ? "COMMIT" : "BACK OFF")} ({p.Why}): ttk {p.TtkS:0.0} s vs ttd {p.TtdS:0.0} s{(p.UnknownAttackers > 0 ? $", {p.UnknownAttackers} attacker(s) not measured yet" : "")}; plan{sb}";
    }
    /// <summary>Seconds to die at the incoming dps (-1 none incoming).</summary>
    [LuaFn] public double TimeToDie() { var dps = ((ThreatModule)ApiScope.Modules![ThreatModule.__ModuleIndex]).IncomingDps(); return dps <= 0 ? -1 : W.Self.Hp / dps; }
    /// <summary>Kills this session.</summary>
    [LuaFn] public int Kills() => W.Self.Kills;
    /// <summary>The HP stone's cooldown as measured (the shortest gap between two of our stones, ms; -1 not measured yet).</summary>
    [LuaFn] public double StoneGapMs() => W.Fight.StoneGap;
    /// <summary>Ms until the next HP stone can be used by the measured gap (0 ready, -1 gap not measured yet).</summary>
    [LuaFn] public double StoneWaitMs()
    {
        var f = W.Fight;
        if (f.StoneGap < 0) return -1;
        return f.LastStoneAt < 0 ? 0 : Math.Max(0, f.StoneGap - (Ctx.Now - f.LastStoneAt));
    }
}

/// <summary>Lua module `skills`.</summary>
[LuaModule("skills")]
public sealed partial class SkillsModule(AgentContext ctx) : ApiModule(ctx)
{
    private IClientData D => Ctx.Data;
    private SkillBook B => Ctx.World.Skills;

    /// <summary>Skills we are behind on: skill families where a buyable skill book (ItemInfo row named like an
    /// ActiveSkill row, a BuyPrice, demand level at most ours) for our class teaches a higher rank than the highest we
    /// know (a family we lack entirely counts too; production lines left out - no damage / heal / buff / debuff / slow /
    /// stun tag, the Spell Scroll and Recipe tiers - they are a choice of field, not power).
    /// Cached per (level, learned count); each recompute logs the families and ranks behind.</summary>
    [LuaFn]
    public int Behind()
    {
        var lv = Ctx.World.Self.Level; var known = B.Learned.Count; var cls = Ctx.World.Self.ClassId;
        // the class is part of the key: a promotion opens the new class's skills at the same level (NextA, 19:27:28)
        if (_behindLv == lv && _behindKnown == known && _behindClass == cls) return _behind;
        var n = 0;
        if (Ctx.Data is ClientData cd)
        {
            var byInx = cd.Skills.GroupBy(x => x.InxName).ToDictionary(g => g.Key, g => g.First());
            var top = new Dictionary<string, ActiveSkillRow>();
            foreach (var i in cd.AllItems)
                if (i.BuyPrice > 0 && i.DemandLevel <= lv && i.InxName is { Length: > 0 } && byInx.TryGetValue(i.InxName, out var sk)
                    && cd.Produce(sk.Id) is null && (SkillTags.Of(cd, sk.Id) & PowerTags) != 0 && cd.ClassAllowed(sk.UseClass, Ctx.World.Self.ClassId) != false
                    && (!top.TryGetValue(sk.Family, out var t) || sk.Step > t.Step))
                    top[sk.Family] = sk;
            var parts = new List<string>();
            foreach (var (fam, sk) in top)
            {
                var have = HighestRank(fam);
                var haveStep = have >= 0 ? D.Skill(have)?.Step ?? 0 : 0;
                if (sk.Step > haveStep) { n++; parts.Add($"{sk.Name} {haveStep}->{sk.Step}"); }
            }
            Ctx.Log.Text(LogCategory.Script, LogLevel.Info, $"skills behind at level {lv}: {n} [{string.Join(", ", parts)}]", CallingLayer);
        }
        _behindLv = lv; _behindKnown = known; _behindClass = cls; _behind = n;
        return n;
    }
    private int _behindLv = -1, _behindKnown = -1, _behindClass = -1, _behind;
    /// <summary>The tags of a skill that adds power (a production line - scrolls, recipes of any tier - has none).</summary>
    private const int PowerTags = (int)(SkillTag.Damage | SkillTag.Heal | SkillTag.Buff | SkillTag.Debuff | SkillTag.Slow | SkillTag.Stun);
    /// <summary>Learned?</summary>
    [LuaFn] public bool Has(int id) => B.Has(id);
    /// <summary>Learned skills (highest rank per family) with all tags of the mask.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter List(int mask = 0)
    {
        var s = Ctx.Scratch(1024); var n = 0;
        var learned = B.Learned;
        for (var i = 0; i < learned.Count && n < s.Length; i++)
        {
            var id = learned[i];
            if (mask != 0 && (SkillTags.Of(D, id) & mask) != mask) continue;
            if (HighestRank(D.Skill(id)?.Family) != id) continue;
            s[n++] = id;
        }
        return ApiScope.Iter(s[..n]);
    }
    private int HighestRank(string? family)
    {
        if (family is null) return -1;
        int best = -1, bestStep = -1;
        var learned = B.Learned;
        for (var i = 0; i < learned.Count; i++)
            if (D.Skill(learned[i]) is { } r && r.Family == family && r.Step > bestStep) { bestStep = r.Step; best = r.Id; }
        return best;
    }
    /// <summary>The usable skill with the tags that has the highest expected damage (or heal) against the target.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)]
    public int Best(int mask, int target)
    {
        var best = -1; double bestV = -1;
        var learned = B.Learned;
        for (var i = 0; i < learned.Count; i++)
        {
            var id = learned[i];
            if ((SkillTags.Of(D, id) & mask) != mask || GameRules.CanUse(Ctx, id, target) != CanUseReason.Ok) continue;
            var sk = D.Skill(id)!;
            var v = (double)Math.Max(sk.MaxWc + sk.MaxMa, sk.SpecialIndexA == 1 ? sk.SpecialValueA : 0);
            if (v > bestV) { bestV = v; best = id; }
        }
        return best;
    }
    /// <summary>The generic, objective check: learned, cooldown (+ group), SP, HP, state, mount, cast bar, range with
    /// body and tolerance, line of fire. Global alias `canUse`.</summary>
    [LuaFn(GlobalAlias = "canUse")] public bool CanUse(int id, int target) => GameRules.CanUse(Ctx, id, target) == CanUseReason.Ok;
    /// <summary>First failing check (skills.R_* code) and its text.</summary>
    [LuaFn(Alloc = Alloc.Small)]
    public (int Code, string Text) WhyNot(int id, int target) { var r = GameRules.CanUse(Ctx, id, target); return ((int)r, r.ToString()); }
    /// <summary>Cooldown and group only.</summary>
    [LuaFn] public bool Ready(int id) => B.ReadyInMs(id, D.Skill(id)?.DelayGroup ?? 0, Ctx.Now) == 0;
    /// <summary>Ms until ready.</summary>
    [LuaFn] public long ReadyInMs(int id) => B.ReadyInMs(id, D.Skill(id)?.DelayGroup ?? 0, Ctx.Now);
    /// <summary>SP and HP cost.</summary>
    [LuaFn] public (int Sp, int Hp) Cost(int id) => D.Skill(id) is { } s ? (s.Sp, s.Hp) : (-1, -1);
    /// <summary>Cast time (ms).</summary>
    [LuaFn] public int CastTimeMs(int id) => D.Skill(id)?.CastTimeMs ?? -1;
    /// <summary>Cooldown (ms).</summary>
    [LuaFn] public int CooldownMs(int id) => D.Skill(id)?.DelayMs ?? -1;
    /// <summary>Range (raw data value).</summary>
    [LuaFn] public int Range(int id) => D.Skill(id)?.Range ?? -1;
    /// <summary>Area (raw data value).</summary>
    [LuaFn] public int Area(int id) => D.Skill(id)?.Area ?? -1;
    /// <summary>Castable while moving.</summary>
    [LuaFn] public bool IsMoving(int id) => D.Skill(id)?.IsMovingSkill ?? false;
    /// <summary>Has all tags of the mask.</summary>
    [LuaFn] public bool HasTag(int id, int tag) => (SkillTags.Of(D, id) & tag) == tag;
    /// <summary>Tag mask.</summary>
    [LuaFn] public int Tags(int id) => SkillTags.Of(D, id);
    /// <summary>Family id (the lowest skill id of the family that we know), -1 unknown.</summary>
    [LuaFn] public int Family(int id) => D.Skill(id) is { } s ? id - (s.Step - 1) : -1;
    /// <summary>Rank (step) within the family.</summary>
    [LuaFn] public int Rank(int id) => D.Skill(id)?.Step ?? -1;
    /// <summary>Highest learned rank of the family of a skill id, -1 none.</summary>
    [LuaFn] public int HighestRank(int familyOrSkill) => HighestRank(D.Skill(familyOrSkill)?.Family);
    /// <summary>Expected heal amount (heal skills).</summary>
    [LuaFn] public double HealAmount(int id) => D.Skill(id) is { SpecialIndexA: 1 } s ? s.SpecialValueA : 0;
    /// <summary>Damage estimate (the skill's damage values; armour not applied yet).</summary>
    [LuaFn] public double DamageEstimate(int id, int h) => D.Skill(id) is { } s ? (s.MinWc + s.MaxWc + s.MinMa + s.MaxMa) / 2.0 : 0;
    /// <summary>Empower levels (not decoded yet: zeros).</summary>
    [LuaFn] public (int Dmg, int Sp, int Dur, int Cd) Empower(int id) => B.Empower(id);
}

/// <summary>Lua module `act`: actions return an action id at once (-1 = rejected; act.lastRejection() says why).</summary>
[LuaModule("act")]
public sealed partial class ActModule(AgentContext ctx) : ApiModule(ctx)
{
    private readonly Intent[] _one = new Intent[1];
    private int Id(ActionId a) => a.Value;
    private int One(ActionKind k, long a0, long a1, in Intent i) { _one[0] = i; return Ctx.Actions.Issue(k, CallingLayer, a0, a1, _one).Value; }
    /// <summary>Cast at an object (target/mode/face/stop sequencing included).</summary>
    [LuaFn] public int Cast(int skill, int h) => Id(Ctx.Combat.Cast(skill, h, CallingLayer, Ctx));
    /// <summary>Ground cast.</summary>
    [LuaFn] public int CastAt(int skill, double x, double y) => Id(Ctx.Combat.CastAt(skill, x, y, CallingLayer, Ctx));
    /// <summary>Auto-attack h (walk into bash reach first).</summary>
    [LuaFn] public int Bash(int h) => Id(Ctx.Combat.Bash(h, CallingLayer, Ctx));
    /// <summary>Stop auto-attack.</summary>
    [LuaFn] public int StopBash() => Id(Ctx.Combat.StopBash(CallingLayer, Ctx));
    /// <summary>Select h.</summary>
    [LuaFn] public int Target(int h) => Id(Ctx.Combat.SetTarget(h, CallingLayer, Ctx));
    /// <summary>Clear the selection.</summary>
    [LuaFn] public int Untarget() => One(ActionKind.Untarget, 0, 0, IntentEncoder.Untarget(-1));
    /// <summary>Walk to (x,y) along a path (job); resolves on arrival / failure.</summary>
    [LuaFn] public int MoveTo(double x, double y, double within = 20) => Id(Ctx.Move.Order(new MoveOrder(x, y, true, within, ActionId.None), Ctx, CallingLayer));
    /// <summary>One straight leg, no path.</summary>
    [LuaFn] public int MoveDirect(double x, double y) => Id(Ctx.Move.Order(new MoveOrder(x, y, false, 5, ActionId.None), Ctx, CallingLayer));
    /// <summary>Follow h at a distance: walks to its current position (re-issue each tick to keep following).</summary>
    [LuaFn]
    public int Follow(int h, double dist)
    {
        var s = Ctx.World.Entities.SlotOf(h);
        if (s < 0) return Id(Ctx.Actions.Refuse(ActionKind.Follow, CallingLayer, LocalRefusal.InvalidTarget));
        var (x, y) = Ctx.World.Entities.Pos(s, Ctx.Now);
        return Id(Ctx.Move.Order(new MoveOrder(x, y, true, dist, ActionId.None), Ctx, CallingLayer));
    }
    /// <summary>Stop and commit the position.</summary>
    [LuaFn] public int Stop() { if (Ctx.Actions.Preempted(CallingLayer, Resource.Movement)) return Id(Ctx.Actions.Refuse(ActionKind.Stop, CallingLayer, LocalRefusal.Preempted)); Ctx.Move.Stop(Ctx); return Id(Ctx.Actions.Issue(ActionKind.Stop, CallingLayer, 0, 0, [])); }
    /// <summary>Mount (uses the mount item).</summary>
    [LuaFn]
    public int Mount()
    {
        var slot = Mounts.FindMount(Ctx);
        if (slot < 0) return Id(Ctx.Actions.Refuse(ActionKind.Mount, CallingLayer, LocalRefusal.InvalidTarget));
        return One(ActionKind.Mount, slot, 0, IntentEncoder.UseItem((byte)slot, 9, -1));
    }
    /// <summary>Dismount (procedure).</summary>
    [LuaFn] public int Dismount() => Id(Ctx.Procedures.Start(new DismountProcedure(), ActionKind.Dismount, CallingLayer, Ctx));
    /// <summary>Use the item in a bag slot.</summary>
    [LuaFn] public int Use(int slot) => One(ActionKind.UseItem, slot, Ctx.World.Bag.At(slot).ItemId, IntentEncoder.UseItem((byte)slot, 9, -1));
    /// <summary>Use the first bag slot holding an item id.</summary>
    [LuaFn] public int UseItem(int itemId) { var s = Ctx.World.Bag.Find(itemId); return s < 0 ? Id(Ctx.Actions.Refuse(ActionKind.UseItem, CallingLayer, LocalRefusal.InvalidTarget)) : Use(s); }
    /// <summary>Buy n HP (sp = false) or SP soul stones from the open soul stone window; resolves on the new total.</summary>
    [LuaFn]
    public int BuyStones(bool sp, int n)
    {
        Span<byte> b = stackalloc byte[2]; b[0] = (byte)n; b[1] = (byte)(n >> 8);
        return One(ActionKind.BuyStones, sp ? 1 : 0, n, IntentEncoder.Raw(sp ? IntentEncoder.OpStoneBuySp : IntentEncoder.OpStoneBuyHp, b, -1));
    }
    /// <summary>Equip the item in a bag slot; resolves when it lands in its equipment slot.</summary>
    [LuaFn]
    public int Equip(int slot)
    {
        Span<byte> b = stackalloc byte[1]; b[0] = (byte)slot;
        return One(ActionKind.Equip, slot, Ctx.World.Bag.At(slot).ItemId, IntentEncoder.Raw(IntentEncoder.OpEquip, b, -1));
    }
    /// <summary>Spend one stat point (0 Str, 1 End, 2 Dex, 3 Int, 4 Spr); resolves on the server's ack.</summary>
    [LuaFn]
    public int StatPoint(int stat)
    {
        Span<byte> b = stackalloc byte[1]; b[0] = (byte)stat;
        return One(ActionKind.StatPoint, stat, 0, IntentEncoder.Raw(IntentEncoder.OpStatPoint, b, -1));
    }
    /// <summary>Soul stone prices / caps from the last stone window (-1 never seen): hpPrice, hpCap, spPrice, spCap.</summary>
    [LuaFn] public (int HpPrice, int HpCap, int SpPrice, int SpCap) StonePrices() { var p = Ctx.World.Npc.StonePrices; return (p.HpPrice, p.HpCap, p.SpPrice, p.SpCap); }
    /// <summary>Rest in the tent (the client's "home" button: stop, PITCHTENT_REQ): HP and SP come back while it stands;
    /// the character cannot act. Acked on 0x0A81, refused with why (0x0A83 too near an NPC, 0x0A84 not on this map, 0x0A85
    /// no mini-house skin).</summary>
    [LuaFn]
    public int PitchTent()
    {
        if (Ctx.Move.Moving) Ctx.Move.Stop(Ctx);
        return One(ActionKind.Tent, 1, 0, IntentEncoder.Raw(IntentEncoder.OpPitchTent, default, -1));
    }
    /// <summary>The client's bag auto-arrange (ITEM_AUTO_ARRANGE_INVEN_REQ 0x304A, empty): stacks loose items of one kind
    /// together and compacts the bag, as the client's sort button does; resolved by its answer.</summary>
    [LuaFn] public int SortBag() => One(ActionKind.BagSort, 0, 0, IntentEncoder.Raw(IntentEncoder.OpBagSort, default, -1));
    /// <summary>Enhance the item in a bag slot with the enhance stone in another (ITEM_UPGRADE_REQ 0x3017); the three
    /// safety stones (red, blue, gold kinds) by bag slot, -1 none. Resolved by ITEM_UPGRADE_ACK: act.result is the code
    /// (0x08C3 success, 0x08C5 failed, 0x08C6 -1 level, 0x08C7 destroyed; 0x08C9 refused fails the action).</summary>
    [LuaFn] public int Upgrade(int itemSlot, int stoneSlot, int left = -1, int right = -1, int middle = -1)
    {
        static byte Sl(int s) => s < 0 ? (byte)0xFF : (byte)s;
        if (itemSlot < 0 || stoneSlot < 0) return Id(Ctx.Actions.Refuse(ActionKind.Upgrade, CallingLayer, LocalRefusal.InvalidTarget));
        Ctx.Log.Log(LogCategory.Item, LogLevel.Note, $"upgrade: item in slot {itemSlot} ({Ctx.World.Bag.At(itemSlot).ItemId}) with the stone in slot {stoneSlot} ({Ctx.World.Bag.At(stoneSlot).ItemId}), safety {left}/{right}/{middle}");
        return One(ActionKind.Upgrade, itemSlot, stoneSlot, IntentEncoder.Upgrade(Sl(itemSlot), Sl(stoneSlot), Sl(left), Sl(right), Sl(middle), -1));
    }
    /// <summary>Fold the tent (FOLDTENT_REQ) before acting again.</summary>
    [LuaFn] public int FoldTent() => One(ActionKind.Tent, 0, 0, IntentEncoder.Raw(IntentEncoder.OpFoldTent, default, -1));
    /// <summary>HP soul stone.</summary>
    [LuaFn] public int StoneHp() => One(ActionKind.StoneHp, 0, 0, IntentEncoder.Raw(IntentEncoder.OpStoneHp, default, -1));
    /// <summary>SP soul stone.</summary>
    [LuaFn] public int StoneSp() => One(ActionKind.StoneSp, 1, 0, IntentEncoder.Raw(IntentEncoder.OpStoneSp, default, -1));
    /// <summary>Pick up a drop.</summary>
    [LuaFn] public int Loot(int h) => One(ActionKind.Loot, h, 0, IntentEncoder.Pick((ushort)h, -1));
    /// <summary>Revive (procedure).</summary>
    [LuaFn] public int Revive() => Id(Ctx.Procedures.Start(new ReviveProcedure(), ActionKind.Revive, CallingLayer, Ctx));
    /// <summary>Say a chat line.</summary>
    [LuaFn] public int Say(string text) => One(ActionKind.Say, 0, 0, IntentEncoder.Chat(text ?? "", -1));
    /// <summary>Whisper (not wired: refused).</summary>
    [LuaFn] public int Whisper(string name, string text) => Id(Ctx.Actions.Refuse(ActionKind.Whisper, CallingLayer, LocalRefusal.UnknownSkill));
    /// <summary>State name of an action: queued, sent, acked, refused, timedout, cancelled, failed; "unknown".</summary>
    [LuaFn(Alloc = Alloc.LuaString)]
    public string State(int a) { var r = Ctx.Actions.Get(new ActionId(a)); return r.Id == 0 ? "unknown" : StateNames[(int)r.State]; }
    private static readonly string[] StateNames = ["queued", "sent", "acked", "refused", "timedout", "cancelled", "failed"];
    /// <summary>Result code (server code or act.L_*), 0 ok.</summary>
    [LuaFn] public int Result(int a) => Ctx.Actions.Get(new ActionId(a)).Code;
    /// <summary>Text for the result code.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Reason(int a) => ErrorCodes.Describe(Ctx.Actions.Get(new ActionId(a)).Code);
    /// <summary>Cancel (best effort).</summary>
    [LuaFn] public bool Cancel(int a) => Ctx.Actions.Cancel(new ActionId(a));
    /// <summary>Newest pending action of a kind (act.CAST ...), -1 none.</summary>
    [LuaFn] public int Pending(int kind) => Ctx.Actions.Pending((ActionKind)kind).Value;
    /// <summary>Why the last call returned -1.</summary>
    [LuaFn] public int LastRejection() => (int)Ctx.Actions.LastRejection;
}

/// <summary>Lua module `items`.</summary>
[LuaModule("items")]
public sealed partial class ItemsModule(AgentContext ctx) : ApiModule(ctx)
{
    private Inventory B => Ctx.World.Bag;
    /// <summary>Item category mask of an item id (derived from ItemInfo: equip slot -> EQUIP, use skill -> CONSUMABLE).</summary>
    [LuaFn]
    public int Category(int itemId)
    {
        var it = Ctx.Data.Item(itemId);
        if (it is null) return 0;
        var m = 0;
        if (it.Equip > 0) m |= (int)ItemCategory.Equip;
        if (it.ItemUseSkill is not ("-" or "")) m |= (int)ItemCategory.Consumable;
        if (it.SellPrice == 0 && it.Equip == 0) m |= (int)ItemCategory.Quest;
        if ((Ctx.Data as ClientData)?.IsMountItem(itemId) == true) m |= (int)ItemCategory.Mount;
        return m;
    }
    /// <summary>The item's client name (for logs; allocates a string).</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Name(int itemId) => Ctx.Data.Item(itemId)?.Name ?? $"item {itemId}";
    /// <summary>Count of an item id.</summary>
    [LuaFn] public int Count(int itemId) => B.Count(itemId);
    /// <summary>The first empty bag slot, -1 none.</summary>
    [LuaFn] public int FreeSlot() { for (var i = 0; i < B.Size; i++) if (B.At(i).ItemId < 0) return i; return -1; }
    /// <summary>Split lot items off a bag stack into an empty bag slot (ITEM_SPLIT_REQ); acked on SPLIT_ACK 0x0441
    /// (after the two cell changes), refused with its code.</summary>
    [LuaFn]
    public int Split(int slot, int lot, int toSlot)
    {
        var (it, have) = B.At(slot);
        if (it < 0 || lot <= 0 || lot >= have || B.At(toSlot).ItemId >= 0) return Ctx.Actions.Refuse(ActionKind.Split, CallingLayer, LocalRefusal.InvalidTarget).Value;
        _split[0] = IntentEncoder.Split((ushort)((ItemDecoder.BagBox << 10) | slot), (ushort)((ItemDecoder.BagBox << 10) | toSlot), (uint)lot, -1);
        return Ctx.Actions.Issue(ActionKind.Split, CallingLayer, slot, toSlot, _split).Value;
    }
    private readonly Intent[] _split = new Intent[1];
    /// <summary>First slot, -1 none.</summary>
    [LuaFn] public int Find(int itemId) => B.Find(itemId);
    /// <summary>Occupied slots matching the category mask (0 = all).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Slots(int mask = 0)
    {
        var s = Ctx.Scratch(1024); var n = 0;
        for (var i = 0; i < B.Size; i++) { var (it, _) = B.At(i); if (it >= 0 && (mask == 0 || (Category(it) & mask) != 0)) s[n++] = i; }
        return ApiScope.Iter(s[..n]);
    }
    /// <summary>Item id and lot at a slot (-1, 0 empty).</summary>
    [LuaFn] public (int ItemId, int Lot) At(int slot) => B.At(slot);
    /// <summary>ItemInfo class.</summary>
    [LuaFn] public int Class(int itemId) => Ctx.Data.Item(itemId)?.ItemClass ?? -1;
    /// <summary>Required level.</summary>
    [LuaFn] public int LevelReq(int itemId) => Ctx.Data.Item(itemId)?.DemandLevel ?? -1;
    /// <summary>Sell price.</summary>
    [LuaFn] public int SellPrice(int itemId) => Ctx.Data.Item(itemId)?.SellPrice ?? -1;
    /// <summary>Quest item (no sell price, not equipment).</summary>
    [LuaFn] public bool IsQuestItem(int itemId) => (Category(itemId) & (int)ItemCategory.Quest) != 0;
    /// <summary>What using an item does (client ItemUseEffect): kind (0 restores HP, 1 restores SP, other kinds as the
    /// client table numbers them; -1 none) and amount.</summary>
    [LuaFn] public (int Kind, int Value) UseEffect(int itemId) => Ctx.Data.Item(itemId) is { } i ? (i.UseEffect, i.UseValue) : (-1, 0);

    /// <summary>Is the item a potion of a kind (0 HP, 1 SP) that we may drink now: used through the potion skill
    /// (ItemInfo ItemUseSkill "UsePotion"), an instant restore of that kind, our level and class?</summary>
    [LuaFn]
    public bool IsPotion(int itemId, int kind) => Ctx.Data.Item(itemId) is { ItemUseSkill: "UsePotion" } i && i.UseEffect == kind
        && i.UseValue > 0 && i.DemandLevel <= Ctx.World.Self.Level && ClassOk(itemId);

    /// <summary>The bag slot of the potion of a kind (0 HP, 1 SP) to drink for a deficit: the smallest that covers it,
    /// else the largest; -1 none.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)]
    public int BestPotion(double deficit, int kind = 0)
    {
        int cover = -1, coverV = int.MaxValue, big = -1, bigV = 0;
        for (var i = 0; i < B.Size; i++)
        {
            var (it, _) = B.At(i);
            if (it < 0 || !IsPotion(it, kind)) continue;
            var v = Ctx.Data.Item(it)!.UseValue;
            if (v >= deficit && v < coverV) { cover = i; coverV = v; }
            if (v > bigV) { big = i; bigV = v; }
        }
        return cover >= 0 ? cover : big;
    }

    /// <summary>How many potions of a kind (0 HP, 1 SP) we carry that we may drink now.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)]
    public int Potions(int kind = 0)
    {
        var n = 0;
        for (var i = 0; i < B.Size; i++) { var (it, lot) = B.At(i); if (it >= 0 && IsPotion(it, kind)) n += lot; }
        return n;
    }
    /// <summary>What selling all non-equipment, non-quest items would fetch.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)]
    public long JunkValue()
    {
        long v = 0;
        for (var i = 0; i < B.Size; i++) { var (it, lot) = B.At(i); if (it >= 0 && Category(it) == 0) v += (long)(Ctx.Data.Item(it)?.SellPrice ?? 0) * lot; }
        return v;
    }
    /// <summary>The skill a skill book teaches when read: the ActiveSkill row with the book's own InxName (a book named
    /// SeverBone01 teaches SeverBone01); -1 for anything else. (ItemUseSkill is what an item casts, not what it teaches.)</summary>
    [LuaFn]
    public int TeachesSkill(int itemId)
    {
        if (Ctx.Data.Item(itemId) is not { InxName: { Length: > 0 } inx }) return -1;
        _skillByInx ??= (Ctx.Data as Fiesta.Bot.Services.ClientData)?.Skills.GroupBy(s => s.InxName).ToDictionary(g => g.Key, g => g.First().Id) ?? new();
        return _skillByInx.TryGetValue(inx, out var id) ? id : -1;
    }
    private Dictionary<string, int>? _skillByInx;

    /// <summary>Can our class learn the skill a book teaches (the ActiveSkill row's UseClass)? False for no skill.</summary>
    [LuaFn]
    public bool SkillForUs(int skillId) => Ctx.Data.Skill(skillId) is { } sk
        && (Ctx.Data as ClientData)?.ClassAllowed(sk.UseClass, Ctx.World.Self.ClassId) != false;

    /// <summary>May our class use this item (ItemInfo UseClass against UseClassTypeInfo)?</summary>
    [LuaFn]
    public bool ClassOk(int itemId) => Ctx.Data.Item(itemId) is not { } it || (Ctx.Data as Fiesta.Bot.Services.ClientData)?.ClassAllowed(it.UseClass, Ctx.World.Self.ClassId) != false;

    /// <summary>Buy price.</summary>
    [LuaFn] public int BuyPrice(int itemId) => Ctx.Data.Item(itemId)?.BuyPrice ?? -1;
    /// <summary>Fame price (client ItemInfo BuyFame; 0 = none, -1 unknown item). Vitality [01] costs 1,200 fame and
    /// no cen: bought with too little fame it is refused BUY_ACK 0x020C.</summary>
    [LuaFn] public int BuyFame(int itemId) => Ctx.Data.Item(itemId) is { } it ? it.BuyFame : -1;

    /// <summary>Does using the item summon a mount (named by the client's MoverItem / Riding tables)?</summary>
    [LuaFn] public bool IsMount(int itemId) => (Ctx.Data as ClientData)?.IsMountItem(itemId) == true;
    private List<(int Npc, int Item)>? _mountOffers;
    /// <summary>Mounts on sale in the shop lists seen (host-wide catalogue): how many; read each with
    /// <see cref="MountOfferAt"/>. Rebuilt on each call (rare: a buy decision).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)] public int MountOffers()
    {
        _mountOffers = new();
        if (Ctx.Knowledge is KnowledgeStore ks && Ctx.Data is ClientData cd)
            foreach (var o in ks.ShopOffers()) if (cd.Mount(o.ItemId) is not null) _mountOffers.Add((o.NpcId, o.ItemId));
        return _mountOffers.Count;
    }
    /// <summary>The i-th mount offer (0-based) of the last <see cref="MountOffers"/>: item, seller npc id, price (ItemInfo
    /// BuyPrice), run speed multiple, hours it lasts (0 = for good); item -1 out of range.</summary>
    [LuaFn] public (int Item, int Npc, int Price, double RunMult, int Hours) MountOfferAt(int i)
    {
        if (_mountOffers is null || i < 0 || i >= _mountOffers.Count) return (-1, -1, -1, -1, -1);
        var (npc, item) = _mountOffers[i];
        var m = (Ctx.Data as ClientData)!.Mount(item)!;
        return (item, npc, Ctx.Data.Item(item)?.BuyPrice ?? -1, m.RunPermille / 1000.0, m.Hours);
    }
    /// <summary>A mount item's ride from the client data: run speed as a multiple of our foot speed, the cast before
    /// riding (ms), how long the item lasts (hours, 0 = for good); (-1, -1, -1) when it is no mount.</summary>
    [LuaFn] public (double RunMult, int CastMs, int Hours) MountRide(int itemId)
        => (Ctx.Data as ClientData)?.Mount(itemId) is { } m ? (m.RunPermille / 1000.0, m.CastMs, m.Hours) : (-1, -1, -1);

    /// <summary>Equipment slot an item goes into (ItemInfo Equip; 0 = not equipment).</summary>
    [LuaFn] public int EquipSlot(int itemId) => Ctx.Data.Item(itemId)?.Equip ?? 0;

    /// <summary>A plain worth of a piece of gear: mean weapon damage + defence + mean magic damage + magic defence.</summary>
    [LuaFn]
    public double GearScore(int itemId) => Ctx.Data.Item(itemId) is not { } i ? -1
        : (i.MinWc + i.MaxWc) / 2.0 + i.Ac + (i.MinMa + i.MaxMa) / 2.0 + i.Mr;

    /// <summary>The current tier client data says we may wear in an equipment slot at our level: buyable (a BuyPrice),
    /// our class, the highest demand level at most ours, the best <see cref="GearScore"/> of that level; -1 none. (By
    /// level first: the level-1 tutorial shirt outscores the level-2 Novice Shirt but no shop sells it.) Cached per (slot, level,
    /// class), so a readiness check costs a lookup.</summary>
    [LuaFn]
    public int BestWearable(int equipSlot)
    {
        var lv = Ctx.World.Self.Level; var cls = Ctx.World.Self.ClassId;
        var key = ((long)equipSlot << 32) | ((long)lv << 16) | (uint)(cls & 0xFFFF);
        if (_bestWearable.TryGetValue(key, out var hit)) return hit;
        var best = -1; var bestLv = -1; var bestScore = double.MinValue;
        if (Ctx.Data is ClientData cd)
            foreach (var i in cd.AllItems)
                if (i.Equip == equipSlot && i.BuyPrice > 0 && i.DemandLevel <= lv && cd.ClassAllowed(i.UseClass, cls) != false)
                {
                    var sc = GearScore(i.Id);
                    if (i.DemandLevel > bestLv || (i.DemandLevel == bestLv && sc > bestScore)) { best = i.Id; bestLv = i.DemandLevel; bestScore = sc; }
                }
        _bestWearable[key] = best;
        return best;
    }
    private readonly Dictionary<long, int> _bestWearable = new();

    /// <summary>Is the item an upgrade over what it would replace (usable by our class at our level)? See
    /// <see cref="UpgradeGain"/>.</summary>
    [LuaFn] public bool IsUpgrade(int itemId) => Gain(itemId).Upgrade;

    /// <summary>What wearing the item gains in <see cref="GearScore"/> over what it takes off; negative infinity when we
    /// may not wear it. Hands count as one: a two-hander takes off everything in both hands, and a shield or one-hander
    /// worn over a two-hander takes the two-hander off, so it is counted with the best piece the bag has for the other
    /// hand. (NextA, lv26: a level-1 shield in the "empty" off-hand stripped its lv20 Steel Axe, the empty main hand then
    /// took a lv15 Sun Broadsword, and the axe came back on later - over and over, 08:30-09:25.)</summary>
    [LuaFn] public double UpgradeGain(int itemId) => Gain(itemId).Value;

    private (bool Upgrade, double Value) Gain(int itemId)
    {
        if (Ctx.Data.Item(itemId) is not { Equip: > 0 } i || i.DemandLevel > Ctx.World.Self.Level || !ClassOk(itemId))
            return (false, double.NegativeInfinity);
        var score = GearScore(itemId);
        if (!IsHandPiece(i))
        {
            var worn = B.Equipped(i.Equip);
            return worn < 0 ? (true, score) : (score > GearScore(worn), score - GearScore(worn));
        }
        var (handsScore, handsWorn, twoHander) = WornHands();
        if (i.TwoHand) return (handsWorn == 0 || score > handsScore, score - handsScore);
        if (twoHander >= 0)
        {
            var with = score + BestBagHandPiece(i.Equip);
            return (with > GearScore(twoHander), with - GearScore(twoHander));
        }
        var inSlot = B.Equipped(i.Equip);
        return inSlot < 0 ? (true, score) : (score > GearScore(inSlot), score - GearScore(inSlot));
    }

    /// <summary>A weapon, a shield or a two-hander (ItemInfo WeaponType / ShieldAC / TwoHand).</summary>
    private static bool IsHandPiece(ItemInfoRow i) => i.WeaponType > 0 || i.ShieldAc > 0 || i.TwoHand;

    /// <summary>The hand pieces worn: their summed score (a two-hander counted once, whichever slots show it), how many
    /// there are, and the worn two-hander (-1 none).</summary>
    private (double Score, int Count, int TwoHander) WornHands()
    {
        double sum = 0; var n = 0; var two = -1;
        for (var s = 0; s < Inventory.EquipSlots; s++)
        {
            var id = B.Equipped(s);
            if (id < 0 || Ctx.Data.Item(id) is not { } w || !IsHandPiece(w)) continue;
            if (w.TwoHand) { if (two == id) continue; two = id; }
            sum += GearScore(id); n++;
        }
        return (sum, n, two);
    }

    /// <summary>The best score among bag hand pieces we may wear in a hand other than <paramref name="notSlot"/>, not
    /// two-handers (0 none): the partner that fills the other hand once a two-hander comes off.</summary>
    private double BestBagHandPiece(int notSlot)
    {
        double best = 0;
        for (var s = 0; s < B.Size; s++)
        {
            var (id, _) = B.At(s);
            if (id < 0 || Ctx.Data.Item(id) is not { Equip: > 0 } p || p.Equip == notSlot || p.TwoHand || !IsHandPiece(p)
                || p.DemandLevel > Ctx.World.Self.Level || !ClassOk(id)) continue;
            best = Math.Max(best, GearScore(id));
        }
        return best;
    }

    /// <summary>The first bag item that is an upgrade (bag slot, -1 none).</summary>
    [LuaFn]
    public int EquipUpgrade()
    {
        for (var s = 0; s < B.Size; s++) { var (it, _) = B.At(s); if (it >= 0 && IsUpgrade(it)) return s; }
        return -1;
    }
    /// <summary>The last ITEM_UPGRADE_ACK code (-1 none yet).</summary>
    [LuaFn] public int LastUpgrade() => B.LastUpgradeCode;
    /// <summary>Equipped item id by equipment slot, -1 none.</summary>
    [LuaFn] public int Equipped(int equipSlot) => B.Equipped(equipSlot);
    /// <summary>Occupied slots into a caller-owned table.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On)] public LuaIter Fill(LuaTableArg tbl, int mask = 0) => Slots(mask);
}

/// <summary>Lua module `quests`.</summary>
[LuaModule("quests")]
public sealed partial class QuestsModule(AgentContext ctx) : ApiModule(ctx)
{
    private QuestLog Q => Ctx.World.Quests;
    /// <summary>Active quest ids.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Active() { var s = Ctx.Scratch(64); var n = 0; foreach (var q in Q.Active) if (n < s.Length) s[n++] = q; return ApiScope.Iter(s[..n]); }
    /// <summary>Quests the server lists as available now.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Available() { var s = Ctx.Scratch(1024); var n = 0; foreach (var q in Q.Available) if (n < s.Length && !Q.IsActive(q) && !Q.IsDone(q)) s[n++] = q; return ApiScope.Iter(s[..n]); }
    /// <summary>Active?</summary>
    [LuaFn] public bool IsActive(int q) => Q.IsActive(q);
    /// <summary>Done?</summary>
    [LuaFn] public bool IsDone(int q) => Q.IsDone(q);
    /// <summary>All objectives met.</summary>
    [LuaFn]
    public bool Ready(int q)
    {
        if (!Q.IsActive(q)) return false;
        if (Q.Status(q) == 8) return true;   // the server's own "reward" state: every objective met
        var def = Ctx.Data.Quest(q);
        if (def is null || def.Objectives.Count == 0) return Q.Status(q) >= 2;
        var handin = HandinNpc(q);
        for (var i = 0; i < def.Objectives.Count && i < 5; i++)
        {
            var o = def.Objectives[i];
            // a quest of talks only: the talk to the hand-in NPC is the hand-in itself and never blocks readiness (60016 "Road to promotion!",
            // one talk to GrandMasterShone, sat 'not ready' at status 6 and the job change was never planned). A talk that
            // does not complete the quest is caught by the brain's give-up (talk resolved without the quest changing).
            if (o.Kind == QuestObjectiveKind.Talk && o.MobId == handin && IsLastTalk(def, i) && OnlyTalks(def)) continue;
            var have = o.Kind == QuestObjectiveKind.Collect ? Ctx.World.Bag.Count(o.ItemId) : Q.Have(q, def.CounterOf(i));
            if (o.Kind != QuestObjectiveKind.Talk && have < o.Need) return false;
            // a talk objective is met when the server says so (its reward state 8), not by assumption: NextA 'handed in'
            // Hopeless James 2 and Reward for Perseverance and Passion again and again (status 6, the server ran their
            // [SCRIPT_DOING] each time: 01:02, 01:04)
            if (o.Kind == QuestObjectiveKind.Talk && Q.Status(q) >= 0) return false;
        }
        return true;
    }
    private static bool OnlyTalks(QuestDef d)
    {
        foreach (var o in d.Objectives) if (o.Kind != QuestObjectiveKind.Talk) return false;
        return true;
    }
    private static bool IsLastTalk(QuestDef d, int i)
    {
        for (var k = i + 1; k < d.Objectives.Count; k++) if (d.Objectives[k].Kind == QuestObjectiveKind.Talk) return false;
        return true;
    }
    /// <summary>Objective i (1-based): have, need.</summary>
    [LuaFn]
    public (int Have, int Need) Progress(int q, int i)
    {
        var def = Ctx.Data.Quest(q);
        if (def is null || i < 1 || i > def.Objectives.Count) return (-1, -1);
        var o = def.Objectives[i - 1];
        return (o.Kind == QuestObjectiveKind.Collect ? Ctx.World.Bag.Count(o.ItemId) : o.Kind == QuestObjectiveKind.Kill ? Q.Have(q, def.CounterOf(i - 1)) : 0, o.Need);
    }
    /// <summary>Objective count.</summary>
    [LuaFn] public int Objectives(int q) => Ctx.Data.Quest(q)?.Objectives.Count ?? -1;
    /// <summary>Objective i: kind (quests.KILL/COLLECT/TALK...), mob id, item id, need.</summary>
    [LuaFn]
    public (int Kind, int MobId, int ItemId, int Need) Objective(int q, int i)
    {
        var def = Ctx.Data.Quest(q);
        if (def is null || i < 1 || i > def.Objectives.Count) return (-1, -1, -1, -1);
        var o = def.Objectives[i - 1];
        return ((int)o.Kind, o.MobId, o.ItemId, o.Need);
    }
    /// <summary>Quest name.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Name(int q) => Ctx.Data.Quest(q)?.Name ?? $"quest {q}";
    /// <summary>Level range.</summary>
    [LuaFn] public (int Min, int Max) Levels(int q) => Ctx.Data.Quest(q) is { } d ? (d.MinLevel, d.MaxLevel) : (-1, -1);
    /// <summary>Giver NPC (mob id).</summary>
    [LuaFn] public int Giver(int q) => Ctx.Data.Quest(q)?.StartNpc ?? -1;
    /// <summary>Repeatable.</summary>
    [LuaFn] public bool Repeatable(int q) => Ctx.Data.Quest(q)?.Repeatable ?? false;
    /// <summary>The scenario (instance) the quest's start script runs, -1 none (such a quest is played in an instance and
    /// entered again from its giver while it is active).</summary>
    [LuaFn] public int Scenario(int q) => Ctx.Data.Quest(q)?.Scenario ?? -1;

    /// <summary>Is the quest on the way to a scenario quest of our class (that quest, or a quest before it in its chain
    /// of previous quests)? The job change is such a chain: its last quest runs the promotion instance.</summary>
    [LuaFn]
    public bool ScenarioChain(int q)
    {
        var cls = Ctx.World.Self.ClassId;
        if (_chainFor != cls || _chain is null)
        {
            _chainFor = cls; _chain = new HashSet<int>();
            foreach (var d in Ctx.Data.Quests)
            {
                if (d.Scenario < 0 || d.StartClass != cls) continue;   // a scenario quest of exactly our class: the job change
                for (var c = d; c is not null && _chain.Add(c.Id); c = c.PrevQuest > 0 ? Ctx.Data.Quest(c.PrevQuest) : null) { }
            }
        }
        return _chain.Contains(q);
    }
    private HashSet<int>? _chain;
    private int _chainFor = -1;

    /// <summary>The quest's fixed rewards (QuestReward): exp, money, number of items (the choosable ones count once).</summary>
    [LuaFn]
    public (long Exp, long Money, int Items) Reward(int q)
    {
        if (Ctx.Data.Quest(q) is not { } d) return (0, 0, 0);
        long exp = 0, money = 0; int items = 0; var choice = false;
        foreach (var (sel, type, value) in d.Rewards)
        {
            if (type == 0) exp += value;
            else if (type == 1) money += value;
            else if (type == 2) { if (sel == 2) { if (!choice) { choice = true; items++; } } else items++; }
        }
        return (exp, money, items);
    }

    /// <summary>The quest's kind number (QuestData Q_TYPE, -1 unknown) and the item that starts it (0 none).</summary>
    [LuaFn] public (int QType, int StartItem) Kind(int q) => Ctx.Data.Quest(q) is { } d ? (d.QType, d.StartItem) : (-1, 0);
    /// <summary>Reward exp.</summary>
    [LuaFn] public long Exp(int q) => Ctx.Data.Quest(q)?.Exp ?? -1;
    /// <summary>Give a quest up (QUEST_GIVE_UP_REQ; the ack 0x0B41 drops it from the log).</summary>
    [LuaFn]
    public int GiveUp(int q)
    {
        Span<byte> b = stackalloc byte[2]; b[0] = (byte)q; b[1] = (byte)(q >> 8);
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpQuestGiveUp, b, -1);
        return Ctx.Actions.Issue(ActionKind.Raw, CallingLayer, IntentEncoder.OpQuestGiveUp, q, _one).Value;
    }
    private readonly Intent[] _one = new Intent[1];
    /// <summary>Quest status as the server keeps it (6 in progress, 7 failed, 8 every objective met, -1 not active).</summary>
    [LuaFn] public int Status(int q) => Q.Status(q);
    /// <summary>Free quest log slots.</summary>
    [LuaFn] public int SlotsFree() => Q.SlotsFree;

    /// <summary>Could we take this quest now, by the client's quest table: level in range, the previous quest done,
    /// the start class ours (0 = any), not active, not done unless repeatable, and its giver's spot known.</summary>
    [LuaFn]
    public bool CanAccept(int q)
    {
        if (Ctx.Data.Quest(q) is not { } d || Q.IsActive(q) || (Q.IsDone(q) && !d.Repeatable)) return false;
        var lv = Ctx.World.Self.Level;
        if (d.MinLevel < 1 || lv < d.MinLevel || (d.MaxLevel > 0 && lv > d.MaxLevel)) return false;
        if (d.PrevQuest > 0 && !Q.IsDone(d.PrevQuest)) return false;
        if (d.StartClass != 0 && d.StartClass != Ctx.World.Self.ClassId) return false;
        return d.StartNpc > 0 && Ctx.Atlas.NpcSpots(d.StartNpc).Count > 0;
    }

    /// <summary>Quests we could take now (CanAccept), best reward first.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Acceptable()
    {
        // the scan over every quest runs again only when the level, the map or the quest log changed
        var key = (Ctx.World.Self.Level, Ctx.World.Version(WorldComponent.Quests), Ctx.World.Map.MapId, Q.Active.Count);
        if (_acceptKey != key)
        {
            _acceptKey = key;
            var list = new List<int>();
            foreach (var d in Ctx.Data.Quests) if (CanAccept(d.Id)) list.Add(d.Id);
            list.Sort((a, b) => (Ctx.Data.Quest(b)?.Exp ?? 0).CompareTo(Ctx.Data.Quest(a)?.Exp ?? 0));
            _accept = list.ToArray();
        }
        var n = Math.Min(_accept.Length, 256);
        return ApiScope.Iter(_accept.AsSpan(0, n));
    }
    private (int, int, int, int) _acceptKey = (-1, -1, -1, -1);
    private int[] _accept = [];

    /// <summary>The NPC (MobInfo id) a quest is handed in to: its last talk objective, else its giver; -1 unknown.</summary>
    [LuaFn]
    public int HandinNpc(int q)
    {
        if (Ctx.Data.Quest(q) is not { } d) return -1;
        for (var i = d.Objectives.Count - 1; i >= 0; i--) if (d.Objectives[i].Kind == QuestObjectiveKind.Talk && d.Objectives[i].MobId > 0) return d.Objectives[i].MobId;
        return d.StartNpc;
    }
    private JobTicket _plan = JobTicket.None;
    private (int Quest, int Kind) _planResult = (-1, 13);
    /// <summary>Score candidates (policy 0: ready hand-ins first, then hunts in level, then accepts); a job.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Job)]
    public int Plan(int policy = 0)
    {
        var level = Ctx.World.Self.Level;
        var active = Q.Active.ToArray();
        var avail = Q.Available.ToArray();
        var data = Ctx.Data;
        var ready = new List<int>();
        foreach (var q in active) if (Ready(q)) ready.Add(q);
        _plan = Ctx.Jobs.Submit(Ctx.BotIndex, JobKind.QuestPlan, _ =>
        {
            if (ready.Count > 0) return (ready[0], 11);
            foreach (var q in active) if (data.Quest(q) is { Objectives.Count: > 0 } d && d.Objectives[0].Kind == QuestObjectiveKind.Kill) return (q, 10);
            int best = -1; var bestLv = -1;
            foreach (var q in avail)
                if (data.Quest(q) is { } d && d.MinLevel <= level && d.MinLevel > bestLv) { best = q; bestLv = d.MinLevel; }
            return best >= 0 ? (best, 12) : (-1, 13);
        }, (t, ok) => Ctx.PostJobDone(t, ok));
        return _plan.Value;
    }
    /// <summary>Result of a plan ticket: quest id, kind (quests.HUNT / HANDIN / ACCEPT / NONE); -1,-1 pending.</summary>
    [LuaFn]
    public (int Quest, int Kind) PlanResult(int t)
    {
        if (t != _plan.Value) return _planResult.Quest >= 0 || t < 0 ? _planResult : (-1, -1);
        if (!Ctx.Jobs.IsDone(_plan)) return (-1, -1);
        if (Ctx.Jobs.Result(_plan) is ValueTuple<int, int> r) _planResult = r;
        return _planResult;
    }
}

/// <summary>Lua module `nav`.</summary>
[LuaModule("nav")]
public sealed partial class NavModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;
    /// <summary>Request a path (job): corridor A* + funnel, goal snapped to walkable.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Job)]
    public int Path(double x, double y)
    {
        var (sx, sy) = W.Self.Pos;
        return Ctx.Nav.RequestPath(Ctx.BotIndex, W.Map.MapName, null, (uint)sx, (uint)sy, (uint)x, (uint)y, (t, ok) => Ctx.PostJobDone(t, ok)).Value;
    }
    /// <summary>pending, ready, failed.</summary>
    [LuaFn(Alloc = Alloc.LuaString)]
    public string PathState(int t) => !Ctx.Jobs.IsDone(new JobTicket(t)) ? "pending" : Ctx.Nav.Result(new JobTicket(t)) is { Status: PathStatus.Ready } ? "ready" : "failed";
    /// <summary>Length of a finished path (-1 not ready).</summary>
    [LuaFn] public double PathLength(int t) => Ctx.Nav.Result(new JobTicket(t))?.Length ?? -1;
    /// <summary>Route length (synchronous estimate: straight line when reachable, -1 unreachable).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)] public double RouteLength(double x, double y) => Reachable(x, y) ? MoveController.Dist(W.Self.Pos.X, W.Self.Pos.Y, x, y) : -1;
    /// <summary>Region connectivity.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public bool Reachable(double x, double y)
    {
        var mesh = Ctx.Nav.Mesh(W.Map.MapName);
        if (mesh is null) return true;
        var (sx, sy) = BlockLayer.ToTile(W.Self.Pos.X, W.Self.Pos.Y); var (gx, gy) = BlockLayer.ToTile(x, y);
        return PathSearch.Reachable(mesh, sx, sy, gx, gy);
    }
    /// <summary>Walkable (block layer + this bot's learned blocks).</summary>
    [LuaFn]
    public bool Walkable(double x, double y)
    {
        var l = Ctx.Nav.Layer(W.Map.MapName); var (tx, ty) = BlockLayer.ToTile(x, y);
        return (l is null || !l.Blocked(tx, ty)) && !Ctx.Overlay.Blocked(tx, ty, Ctx.Now);
    }
    /// <summary>Nearest walkable point (the client's ring search).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public (double X, double Y) Snap(double x, double y)
    {
        var l = Ctx.Nav.Layer(W.Map.MapName);
        if (l is null) return (x, y);
        var (tx, ty) = BlockLayer.ToTile(x, y);
        return PathSearch.Snap((a, b) => !l.Blocked(a, b), ref tx, ref ty) ? ((tx + 0.5) * BlockLayer.WorldPerTile, (ty + 0.5) * BlockLayer.WorldPerTile) : (-1, -1);
    }
    /// <summary>A walkable point within r at least minFromNpc from every NPC in view (-1,-1 none).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public (double X, double Y) ClearSpot(double minFromNpc, double r)
    {
        var l = Ctx.Nav.Layer(W.Map.MapName); var (px, py) = W.Self.Pos;
        var s = Ctx.Scratch(1024);
        var n = W.Grid.Query(px, py, r + minFromNpc, EntityKind.Npc, W, Ctx.Now, s);
        for (var ring = 0; ring <= 4; ring++)
            for (var k = 0; k < 16; k++)
            {
                var d = r * ring / 4; var a = k * Math.PI / 8;
                double x = px + Math.Cos(a) * d, y = py + Math.Sin(a) * d;
                if (l is not null && !l.WalkableWorld(x, y)) continue;
                var ok = true;
                for (var i = 0; i < n && ok; i++) { var (nx, ny) = W.Entities.Pos(s[i], Ctx.Now); if (MoveController.Dist(nx, ny, x, y) < minFromNpc) ok = false; }
                if (ok) return (x, y);
            }
        return (-1, -1);
    }
    /// <summary>Known aggro shapes crossed by the straight route.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Cached)]
    public double RouteRisk(double x, double y)
    {
        var (px, py) = W.Self.Pos;
        var s = Ctx.Scratch(1024);
        var n = W.Grid.Query((x + px) / 2, (y + py) / 2, MoveController.Dist(x, y, px, py) / 2 + 600, EntityKind.Mob | EntityKind.Alive, W, Ctx.Now, s);
        var risk = 0;
        for (var i = 0; i < n; i++)
        {
            var det = Ctx.Knowledge.Mob(W.Entities.MobId(s[i]), MobFact.DetectRadius);
            var rad = det.Samples > 0 ? det.Value : 250;
            var (mx, my) = W.Entities.Pos(s[i], Ctx.Now);
            double dx = x - px, dy = y - py, l2 = dx * dx + dy * dy;
            var t = l2 <= 0 ? 0 : Math.Clamp(((mx - px) * dx + (my - py) * dy) / l2, 0, 1);
            if (MoveController.Dist(px + t * dx, py + t * dy, mx, my) < rad) risk++;
        }
        return risk;
    }
    /// <summary>A gate in view whose destination is the map, -1 none.</summary>
    [LuaFn]
    public int GateTo(string mapName)
    {
        var e = W.Entities;
        for (var i = 0; i < e.Count; i++)
        {
            var s = e.LiveSlot(i);
            if ((e.Kind(s) & EntityKind.Gate) != 0 && string.Equals(e.NameOf(s), mapName, StringComparison.OrdinalIgnoreCase)) return e.HandleOf(s);
        }
        return -1;
    }
    /// <summary>Take a gate in view toward a map (GateHopProcedure); -1 when no such gate is in view.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Job)]
    public int Travel(string mapName)
    {
        var g = GateTo(mapName);
        if (g < 0) return Ctx.Actions.Refuse(ActionKind.Travel, CallingLayer, LocalRefusal.NoPath).Value;
        return Ctx.Procedures.Start(new GateHopProcedure(g, mapName), ActionKind.Gate, CallingLayer, Ctx, GateHopProcedure.TimeoutFor(Ctx, g)).Value;
    }
    /// <summary>A travel or gate procedure is running.</summary>
    [LuaFn] public bool Traveling() => Ctx.Actions.Pending(ActionKind.Gate).Value > 0 || Ctx.Actions.Pending(ActionKind.Travel).Value > 0;
    /// <summary>Next point keeping gap from h, away from it, on walkable ground.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public (double X, double Y) KiteStep(int h, double gap)
    {
        var s = W.Entities.SlotOf(h); if (s < 0) return (-1, -1);
        var (mx, my) = W.Entities.Pos(s, Ctx.Now); var (px, py) = W.Self.Pos;
        var d = Math.Max(1, MoveController.Dist(mx, my, px, py));
        var l = Ctx.Nav.Layer(W.Map.MapName);
        for (var k = 0; k < 8; k++)
        {
            var a = Math.Atan2(py - my, px - mx) + (k % 2 == 0 ? 1 : -1) * (k / 2) * Math.PI / 8;
            double x = mx + Math.Cos(a) * gap, y = my + Math.Sin(a) * gap;
            if (l is null || GameRules.MoveSegmentAccepted(l, px, py, x, y)) return (x, y);
        }
        return (px + (px - mx) / d * gap, py + (py - my) / d * gap);
    }
    /// <summary>Map short name of a map id.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string MapOf(int mapId) => Ctx.Data.Map(mapId)?.MapName ?? "";
    /// <summary>Distance between two points.</summary>
    [LuaFn] public double Distance(double x1, double y1, double x2, double y2) => MoveController.Dist(x1, y1, x2, y2);
}

/// <summary>Lua module `npc`: NPC roles are learned from what a click opens (client-legal).</summary>
[LuaModule("npc")]
public sealed partial class NpcModule(AgentContext ctx) : ApiModule(ctx)
{
    private WorldState W => Ctx.World;
    private int RoleOfSlot(int s) => Ctx.Knowledge.NpcRole(W.Entities.MobId(s)) | ((W.Entities.Kind(s) & EntityKind.Gate) != 0 ? (int)NpcRole.Gate : 0);
    /// <summary>NPCs in view with any of the roles (0 = all NPCs).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter Find(int roleMask = 0)
    {
        var s = Ctx.Scratch(1024); var (x, y) = W.Self.Pos;
        var n = W.Grid.Query(x, y, 4000, EntityKind.Npc, W, Ctx.Now, s); var m = 0;
        for (var i = 0; i < n; i++) if (roleMask == 0 || (RoleOfSlot(s[i]) & roleMask) != 0) s[m++] = W.Entities.HandleOf(s[i]);
        return ApiScope.Iter(s[..m]);
    }
    /// <summary>Nearest NPC in view with any of the roles (0 = any), -1 none.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok)]
    public int Nearest(int roleMask = 0)
    {
        var s = Ctx.Scratch(1024); var (x, y) = W.Self.Pos;
        var n = W.Grid.Query(x, y, 4000, EntityKind.Npc, W, Ctx.Now, s);
        for (var i = 0; i < n; i++) if (roleMask == 0 || (RoleOfSlot(s[i]) & roleMask) != 0) return W.Entities.HandleOf(s[i]);
        return -1;
    }
    /// <summary>A known NPC with the role on a map (knowledge store): npc mob id, x, y; -1 none.</summary>
    [LuaFn] public (int NpcId, double X, double Y) Known(int roleMask, int mapId) => Ctx.Knowledge.KnownNpc(roleMask, mapId) is { } k ? (k.NpcId, k.X, k.Y) : (-1, -1, -1);
    /// <summary>Role mask of an NPC in view.</summary>
    [LuaFn] public int Role(int h) { var s = W.Entities.SlotOf(h); return s < 0 ? 0 : RoleOfSlot(s); }
    /// <summary>NPC mob id.</summary>
    [LuaFn] public int NpcId(int h) { var s = W.Entities.SlotOf(h); return s < 0 ? -1 : W.Entities.MobId(s); }
    /// <summary>Open an NPC's shop (procedure; resolves when the list arrived).</summary>
    /// <summary>Enhance a bag item at a smith (h = the smith in view), as the client does: click, leave its menu open,
    /// ITEM_UPGRADE_REQ, the answer, end of trade. Safety stones by bag slot, -1 none. items.lastUpgrade() is the code.</summary>
    [LuaFn] public int UpgradeAt(int h, int itemSlot, int stoneSlot, int left = -1, int right = -1, int middle = -1)
    {
        static byte Sl(int s) => s < 0 ? (byte)0xFF : (byte)s;
        return Ctx.Procedures.Start(new UpgradeAtNpcProcedure(h, Sl(itemSlot), Sl(stoneSlot), Sl(left), Sl(right), Sl(middle)), ActionKind.UpgradeAtNpc, CallingLayer, Ctx).Value;
    }
    [LuaFn] public int OpenShop(int h, int reply = 1) => Ctx.Procedures.Start(new OpenShopProcedure(h, reply), ActionKind.OpenShop, CallingLayer, Ctx).Value;
    /// <summary>The open shop sells the item.</summary>
    /// <summary>Item ids the open shop sells.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.Ok, Alloc = Alloc.Iterator)]
    public LuaIter ShopItems() { var s = Ctx.Scratch(256); var n = 0; foreach (var (_, it) in W.Npc.ShopItems) if (n < s.Length) s[n++] = it; return ApiScope.Iter(s[..n]); }
    [LuaFn] public bool ShopHas(int itemId) { foreach (var (_, it) in W.Npc.ShopItems) if (it == itemId) return true; return false; }
    /// <summary>Buy from the open shop.</summary>
    [LuaFn] public int Buy(int itemId, int lot = 1) { _one[0] = IntentEncoder.Buy((ushort)itemId, (uint)lot, -1); return Ctx.Actions.Issue(ActionKind.Buy, CallingLayer, itemId, lot, _one).Value; }
    private readonly Intent[] _one = new Intent[1];
    /// <summary>Sell a bag slot to the open shop.</summary>
    [LuaFn] public int Sell(int slot, int lot = 1) { _one[0] = IntentEncoder.Sell((byte)slot, (uint)lot, -1); return Ctx.Actions.Issue(ActionKind.Sell, CallingLayer, slot, lot, _one).Value; }
    /// <summary>Quest dialogue with an NPC (accept or hand in), procedure.</summary>
    [LuaFn] public int Talk(int h, int questId = -1, int reward = -1) => Ctx.Procedures.Start(new QuestDialogueProcedure(h, questId, reward), ActionKind.Talk, CallingLayer, Ctx).Value;
    /// <summary>Open a storage keeper's storage (procedure: click, menu answer, the storage window; fails when the NPC
    /// opens a shop instead or the window is refused).</summary>
    [LuaFn] public int Storage(int h, int reply = 1) => Ctx.Procedures.Start(new StorageProcedure(h, reply), ActionKind.Storage, CallingLayer, Ctx).Value;
    /// <summary>Close whatever NPC window is open (END_OF_TRADE, as the client does when a window is closed; an open
    /// window makes the game refuse trade proposals). Resolved when sent: the game answers nothing.</summary>
    [LuaFn]
    public int Close()
    {
        W.Bag.StorageOpenAt = -1;
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpEndOfTrade, default, -1);
        return Ctx.Actions.Issue(ActionKind.Raw, CallingLayer, IntentEncoder.OpEndOfTrade, 0, _one).Value;
    }
    /// <summary>Is the storage window open now (opened, and no other NPC window or map change since)?</summary>
    [LuaFn] public bool StorageOpen() => W.Bag.StorageOpenAt >= 0;
    /// <summary>Occupied storage cells as of the last window and the moves since.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter StorageCells()
    {
        var s = Ctx.Scratch(Inventory.StoreCells); var n = 0;
        for (var c = 0; c < Inventory.StoreCells; c++) if (W.Bag.StoreAt(c).ItemId >= 0) s[n++] = c;
        return ApiScope.Iter(s[..n]);
    }
    /// <summary>Item id and lot in a storage cell ((-1, 0) empty).</summary>
    [LuaFn] public (int ItemId, int Lot) StorageAt(int cell) => W.Bag.StoreAt(cell);
    /// <summary>The first empty storage cell at or after a cell (-1 none). Capacity is the server's: a move into a cell
    /// beyond it is refused, and the caller moves on.</summary>
    [LuaFn] public int StorageFree(int from = 0) { for (var c = Math.Max(0, from); c < Inventory.StoreCells; c++) if (W.Bag.StoreAt(c).ItemId < 0) return c; return -1; }
    /// <summary>Storage pages (0 = never opened) and money held in storage, from the last window.</summary>
    [LuaFn] public (int Pages, long Cen) StorageInfo() => (W.Bag.StoragePages, W.Bag.StorageCen);
    /// <summary>Move a bag slot into a storage cell (the storage window must be open); acked when the bag cell changes,
    /// refused with the server's RELOC code.</summary>
    [LuaFn] public int Deposit(int bagSlot, int cell) => Move(ItemDecoder.BagBox, bagSlot, ItemDecoder.StorageBox, cell);
    /// <summary>Move a storage cell into a bag slot.</summary>
    [LuaFn] public int Withdraw(int cell, int bagSlot) => Move(ItemDecoder.StorageBox, cell, ItemDecoder.BagBox, bagSlot);
    private int Move(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        if (W.Bag.StorageOpenAt < 0) return Ctx.Actions.Refuse(ActionKind.StorageMove, CallingLayer, LocalRefusal.NotReady).Value;
        var from = (ushort)((fromBox << 10) | (fromSlot & 0x3FF)); var to = (ushort)((toBox << 10) | (toSlot & 0x3FF));
        _one[0] = IntentEncoder.Reloc(from, to, -1);
        return Ctx.Actions.Issue(ActionKind.StorageMove, CallingLayer, from, to, _one).Value;
    }
}

/// <summary>Lua module `social`: party invitations, friend requests, player trade, chat reads.</summary>
[LuaModule("social")]
public sealed partial class SocialModule(AgentContext ctx) : ApiModule(ctx)
{
    private int No(ActionKind k) => Ctx.Actions.Refuse(k, CallingLayer, LocalRefusal.UnknownSkill).Value;
    /// <summary>Invite a player (not wired).</summary>
    [LuaFn] public int PartyInvite(string name) => No(ActionKind.PartyInvite);
    /// <summary>Accept an invite (not wired).</summary>
    private readonly Intent[] _one = new Intent[1];
    /// <summary>Accept the pending party invitation (JOINPROPOSE_ALLOW_ACK with the master's name, to the world manager).</summary>
    [LuaFn]
    public int PartyAccept()
    {
        var (master, _) = Ctx.World.Party.Invite;
        if (master.Length == 0) return Ctx.Actions.Refuse(ActionKind.PartyAccept, CallingLayer, LocalRefusal.InvalidTarget).Value;
        Span<byte> b = stackalloc byte[20]; Ascii.Put(b, master);
        Ctx.World.Party.Invite = ("", -1);
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpPartyAllow, b, -1);
        return Ctx.Actions.Issue(ActionKind.PartyAccept, CallingLayer, 0, 0, _one).Value;
    }
    /// <summary>The pending party invitation's master ("" none).</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string PendingInvite() => Ctx.World.Party.Invite.Master;
    /// <summary>The pending friend request's sender ("" none).</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string PendingFriend() => Ctx.World.Party.FriendRequest.CharId;
    /// <summary>Answer the pending friend request (FRIEND_SET_CONFIRM_ACK, to the world manager).</summary>
    [LuaFn]
    public int FriendAnswer(bool accept)
    {
        var (a, f, _) = Ctx.World.Party.FriendRequest;
        if (a.Length == 0) return Ctx.Actions.Refuse(ActionKind.Raw, CallingLayer, LocalRefusal.InvalidTarget).Value;
        Span<byte> b = stackalloc byte[41]; Ascii.Put(b[..20], a); Ascii.Put(b.Slice(20, 20), f); b[40] = (byte)(accept ? 1 : 0);
        Ctx.World.Party.FriendRequest = ("", "", -1);
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpFriendConfirm, b, -1);
        return Ctx.Actions.Issue(ActionKind.Raw, CallingLayer, IntentEncoder.OpFriendConfirm, 0, _one).Value;
    }
    /// <summary>Leave (not wired).</summary>
    [LuaFn] public int PartyLeave() => No(ActionKind.PartyLeave);
    /// <summary>Party members in view.</summary>
    [LuaFn(Alloc = Alloc.Iterator)] public LuaIter PartyMembers() { var s = Ctx.Scratch(16); var n = Ctx.World.Party.Members(s); return ApiScope.Iter(s[..n]); }
    /// <summary>Party size.</summary>
    [LuaFn] public int PartySize() => Ctx.World.Party.Size;
    /// <summary>Propose a trade to a player in view (TRADE_PROPOSE_REQ); acked when the window opens (START), refused
    /// with the PROPOSENO code.</summary>
    [LuaFn] public int TradePropose(int h) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)h); return T(ActionKind.TradePropose, 1, b, h); }
    /// <summary>Answer a player's trade proposal: yes (acked when the window opens) or no.</summary>
    [LuaFn] public int TradeAnswer(bool yes) => Ctx.World.Trade.Phase != TradePhase.Asked ? Ctx.Actions.Refuse(ActionKind.TradeAnswer, CallingLayer, LocalRefusal.InvalidTarget).Value
        : yes ? T(ActionKind.TradeAnswer, 6, default, 1) : Declined(T(ActionKind.Raw, 3, default, 0));
    private int Declined(int a) { Ctx.World.Trade.Forget(Ctx.Now); return a; }
    /// <summary>Trade phase: none, asked, proposed, open, done, failed, cancelled.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string TradeState() => Ctx.World.Trade.Phase.ToString().ToLowerInvariant();
    /// <summary>The trade partner's handle (-1 none) and our / their money on the board.</summary>
    [LuaFn] public (int Partner, long MyCen, long TheirCen) TradeInfo() => (Ctx.World.Trade.Partner, Ctx.World.Trade.MyCen, Ctx.World.Trade.TheirCen);
    /// <summary>Lock / decide flags: mine locked, theirs locked, mine decided, theirs decided.</summary>
    [LuaFn] public (bool MyLock, bool TheirLock, bool MyDecide, bool TheirDecide) TradeFlags() { var t = Ctx.World.Trade; return (t.MyLocked, t.TheirLocked, t.MyDecided, t.TheirDecided); }
    /// <summary>Their item on a board slot: item id, lot ((-1, 0) none).</summary>
    [LuaFn] public (int ItemId, int Lot) TradeTheirs(int board) => Ctx.World.Trade.TheirsAt(board);
    /// <summary>The bag slot we put on a board slot (-1 none).</summary>
    [LuaFn] public int TradeMine(int board) => Ctx.World.Trade.MineAt(board);
    /// <summary>Is a bag slot on our side of the board?</summary>
    [LuaFn] public bool TradeOnBoard(int bagSlot) => Ctx.World.Trade.OnBoard(bagSlot);
    /// <summary>Put a bag slot on the board (UPBOARD; the whole stack: the game boards a slot, not a lot).</summary>
    [LuaFn] public int TradeOfferItem(int slot) { Span<byte> b = [(byte)slot]; return T(ActionKind.TradeOfferItem, 13, b, slot); }
    /// <summary>Take a board slot back (DOWNBOARD).</summary>
    [LuaFn] public int TradeTakeBack(int board) { Span<byte> b = [(byte)board]; return T(ActionKind.TradeTakeBack, 17, b, board); }
    /// <summary>Put money on the board (CENBOARDING, the total offered).</summary>
    [LuaFn] public int TradeOfferMoney(long cen) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, cen); return T(ActionKind.TradeOfferMoney, 21, b, cen); }
    /// <summary>Lock our board (BOARDLOCK).</summary>
    [LuaFn] public int TradeLock() => T(ActionKind.TradeLock, 25, default, 0);
    /// <summary>Confirm the trade (DECIDE; both sides locked first). The trade completes when both decided.</summary>
    [LuaFn] public int TradeConfirm() => T(ActionKind.TradeConfirm, 31, default, 0);
    /// <summary>Cancel the trade (CANCEL_REQ).</summary>
    [LuaFn] public int TradeCancel() => T(ActionKind.TradeCancel, 10, default, 0);
    private int T(ActionKind k, int cmd, ReadOnlySpan<byte> payload, long arg)
    {
        _one[0] = IntentEncoder.Trade(cmd, payload, -1);
        return Ctx.Actions.Issue(k, CallingLayer, arg, cmd, _one).Value;
    }
    /// <summary>Last chat line: speaker handle (as text), text.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public (string Name, string Text) ChatLast() => Ctx.World.Npc.LastChat is { } c ? (c.Speaker.ToString(), c.Text) : ("", "");
    /// <summary>Last whisper: name, text.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public (string Name, string Text) WhisperLast() => Ctx.World.Npc.LastWhisper is { } w ? (w.Name, w.Text) : ("", "");
}

/// <summary>Lua module `know`: learned knowledge (estimate, low, high, samples; -1,-1,-1,0 unknown).</summary>
[LuaModule("know")]
public sealed partial class KnowModule(AgentContext ctx) : ApiModule(ctx)
{
    private (double, double, double, int) E(int mob, MobFact f) { var e = Ctx.Knowledge.Mob(mob, f); return (e.Value, e.Low, e.High, e.Samples); }
    /// <summary>Detect radius.</summary>
    [LuaFn] public (double V, double Lo, double Hi, int N) Detect(int mobId) => E(mobId, MobFact.DetectRadius);
    /// <summary>Chase distance.</summary>
    [LuaFn] public (double V, double Lo, double Hi, int N) Leash(int mobId) => E(mobId, MobFact.ChaseDistance);
    /// <summary>Attack reach.</summary>
    [LuaFn] public (double V, double Lo, double Hi, int N) Reach(int mobId) => E(mobId, MobFact.Reach);
    /// <summary>Swing interval (ms).</summary>
    [LuaFn] public (double V, double Lo, double Hi, int N) Interval(int mobId) => E(mobId, MobFact.AttackInterval);
    /// <summary>Family assist radius.</summary>
    [LuaFn] public (double V, double Lo, double Hi, int N) Assist(int familyId) { var e = Ctx.Knowledge.Assist(familyId); return (e.Value, e.Low, e.High, e.Samples); }
    /// <summary>Hits on us: average, max, samples.</summary>
    [LuaFn] public (double Avg, double Max, int N) Hit(int mobId) { var e = Ctx.Knowledge.Mob(mobId, MobFact.HitAvg); return (e.Value, e.High, e.Samples); }
    /// <summary>Armour estimate (not fitted yet).</summary>
    [LuaFn] public (double Ac, double Mr, double Conf) Armour(int mobId) => (-1, -1, 0);
    /// <summary>Dungeon ratio (not learned yet).</summary>
    [LuaFn] public double DungeonRatio(int mapId) => -1;
    /// <summary>NPC role mask.</summary>
    [LuaFn] public int NpcRole(int npcId) => Ctx.Knowledge.NpcRole(npcId);
}

/// <summary>Lua module `time`.</summary>
[LuaModule("time")]
public sealed partial class TimeModule(AgentContext ctx) : ApiModule(ctx)
{
    private readonly Dictionary<long, long> _every = new();
    /// <summary>Tick-start clock as Unix milliseconds. Brains keep times in bb.bot (skip deadlines, the last death...),
    /// which outlives the host process: a process-relative clock started over at every restart, so a stored time lay
    /// hours in the future (a grind wait that never ended, quests skipped far too long).</summary>
    [LuaFn] public long Now() => SystemClock.UnixMs(Ctx.Now);
    /// <summary>Ms since t (a time.now() value).</summary>
    [LuaFn] public long Since(long t) => SystemClock.UnixMs(Ctx.Now) - t;
    /// <summary>Tick counter.</summary>
    [LuaFn] public long Tick() => Ctx.Tick;
    /// <summary>True at most once per ms per key (per layer).</summary>
    [LuaFn]
    public bool Every(int key, int ms)
    {
        var k = ((long)CallingLayer << 32) | (uint)key;
        if (_every.TryGetValue(k, out var t) && Ctx.Now - t < ms) return false;
        _every[k] = Ctx.Now;
        return true;
    }
}

/// <summary>Lua module `ev`.</summary>
[LuaModule("ev")]
public sealed partial class EvModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Did an event of the kind fire this tick?</summary>
    [LuaFn] public bool Fired(int kind) => Ctx.Events.Fired((EventKind)kind);
    /// <summary>Occurrences in the last ms.</summary>
    [LuaFn] public int Count(int kind, int ms) => Ctx.Events.Count((EventKind)kind, ms, Ctx.Now);
    /// <summary>Most recent: tick, arg1, arg2 (-1 none).</summary>
    [LuaFn] public (long Tick, long A1, long A2) Last(int kind) => Ctx.Events.Last((EventKind)kind) is { } e ? (e.Tick, e.Arg1, e.Arg2) : (-1, -1, -1);
    /// <summary>The latest chat: speaker handle as text, text.</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public (string Name, string Text) ChatText() => Ctx.World.Npc.LastChat is { } c ? (c.Speaker.ToString(), c.Text) : ("", "");
}

/// <summary>Lua module `world`.</summary>
[LuaModule("world")]
public sealed partial class WorldModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Version of a component (world.SELF, ENTS, BAG, SKILLS, QUESTS, PARTY, MAP).</summary>
    [LuaFn] public int Version(int component) => Ctx.World.Version((WorldComponent)component);
}

/// <summary>What `dbg` writes to (the brain's debug tap).</summary>
public interface IDbgSink
{
    /// <summary>Watch a value.</summary>
    void Watch(string name, double value);
    /// <summary>Profiler mark.</summary>
    void Mark(string section);
    /// <summary>Script line budget: returns false when the layer exhausted it.</summary>
    bool TakeLine();
}

/// <summary>Lua module `dbg`: script logging (category script, gated and budgeted) and watches.</summary>
[LuaModule("dbg")]
public sealed partial class DbgModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>The brain's sink (set by the runtime).</summary>
    public IDbgSink? Sink { get; set; }

    /// <summary>A wall-clock microsecond counter for measuring what a piece of brain code costs (differences only; not
    /// game time - use time.now() for that).</summary>
    [LuaFn] public double Us() => System.Diagnostics.Stopwatch.GetTimestamp() * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    private void Line(LogLevel level, string? fmt, LuaAny args)
    {
        if (!Ctx.Log.IsEnabled(LogCategory.Script, level)) return;
        if (Sink is { } s && level != LogLevel.Crit && !s.TakeLine()) return;
        var text = Format(fmt ?? "", args);
        if (text.Length > 512) text = text[..512] + "...";
        Ctx.Log.Text(LogCategory.Script, level, text, CallingLayer);
    }
    /// <summary>string.format-style formatting in C# (%s %d %i %f %.Nf %x %%).</summary>
    public static string Format(string fmt, LuaAny args)
    {
        var sb = new System.Text.StringBuilder(fmt.Length + 16);
        var ai = 0;
        for (var i = 0; i < fmt.Length; i++)
        {
            var ch = fmt[i];
            if (ch != '%' || i + 1 >= fmt.Length) { sb.Append(ch); continue; }
            var j = i + 1; var prec = -1;
            if (fmt[j] == '%') { sb.Append('%'); i = j; continue; }
            while (j < fmt.Length && (char.IsDigit(fmt[j]) || fmt[j] is '.' or '-' or '+')) { if (fmt[j] == '.') { var k = j + 1; var p = 0; while (k < fmt.Length && char.IsDigit(fmt[k])) p = p * 10 + (fmt[k++] - '0'); prec = p; } j++; }
            if (j >= fmt.Length) { sb.Append(fmt[i..]); break; }
            var conv = fmt[j];
            var arg = ai < args.Count ? ai++ : -1;
            switch (conv)
            {
                case 'd' or 'i': sb.Append(arg < 0 ? "nil" : double.IsNaN(args.Number(arg)) ? args.Text(arg) : ((long)args.Number(arg)).ToString()); break;
                case 'f' or 'g': sb.Append(arg < 0 ? "nil" : args.Number(arg).ToString(prec >= 0 ? "F" + prec : "0.######", System.Globalization.CultureInfo.InvariantCulture)); break;
                case 'x' or 'X': sb.Append(arg < 0 ? "nil" : ((long)args.Number(arg)).ToString(conv == 'x' ? "x" : "X")); break;
                default: sb.Append(arg < 0 ? "nil" : args.Text(arg)); break;
            }
            i = j;
        }
        for (; ai < args.Count; ai++) sb.Append(' ').Append(args.Text(ai));
        return sb.ToString();
    }
    /// <summary>Note line (formatted in C# only when enabled).</summary>
    [LuaFn] public void Log(string fmt, LuaAny args) => Line(LogLevel.Note, fmt, args);
    /// <summary>Info line.</summary>
    [LuaFn] public void Logi(string fmt, LuaAny args) => Line(LogLevel.Info, fmt, args);
    /// <summary>Debug line.</summary>
    [LuaFn] public void Logv(string fmt, LuaAny args) => Line(LogLevel.Debug, fmt, args);
    /// <summary>Crit line (never dropped).</summary>
    [LuaFn] public void Crit(string fmt, LuaAny args) => Line(LogLevel.Crit, fmt, args);
    /// <summary>Is the level on for script lines (dbg.NOTE/INFO/DEBUG/TRACE)?</summary>
    [LuaFn] public bool Enabled(int level) => Ctx.Log.IsEnabled(LogCategory.Script, (LogLevel)level);
    /// <summary>Watch a value in the debugger.</summary>
    [LuaFn] public void Watch(string name, double value) => Sink?.Watch(name ?? "?", value);
    /// <summary>Profiler section boundary.</summary>
    [LuaFn] public void Mark(string section) => Sink?.Mark(section ?? "");
}
