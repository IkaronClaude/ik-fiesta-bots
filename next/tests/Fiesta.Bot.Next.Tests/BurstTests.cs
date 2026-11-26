using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Client data with two instant damage skills (data damage 15 and 25, cooldowns 1 s and 3 s, 5 SP each).</summary>
public sealed class BurstData : IClientData
{
    private readonly TestData _t = new();
    public static readonly ActiveSkillRow S1 = new(1, "TestBolt01", "Test Bolt", 1, 300, 0, 1, 0, 3000, 0, 0, 5, 0, false, 0, 0, 0, 0, 0, 0, 0, 20, 30, "TestBolt", "-", "-", "-", "-");
    public MobInfoRow? Mob(int id) => _t.Mob(id);
    public ActiveSkillRow? Skill(int id) => id == 1 ? S1 : _t.Skill(id);
    public ItemInfoRow? Item(int id) => _t.Item(id);
    public MapInfoRow? Map(int id) => _t.Map(id);
    public MapInfoRow? Map(string mapName) => _t.Map(mapName);
    public QuestDef? Quest(int id) => _t.Quest(id);
    public string? Dialog(int id) => null;
    public int ClassFamily(int classId) => 1;
    public string ClassName(int classId) => "";
    public IReadOnlyCollection<QuestDef> Quests => [];
    public string Dir => "";
}

/// <summary>The burst decision: time to kill from the soonest-available cast queue against time to die from what is on
/// us, both on the pessimistic side of their measured spread.</summary>
public class BurstTests
{
    private static TestBot Fight(int mobHp, int[] attackers)
    {
        var t = new TestBot(new BurstData());
        t.Ev(InboundKind.SkillLearned, a: 1);
        t.Tick(2);
        var h = 2000;
        foreach (var a in attackers)
        {
            t.Ev(InboundKind.EntityAppeared, h: a, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
            t.Ev(InboundKind.EntityHp, h: a, a: a == h ? mobHp : 500, b: 500);
        }
        t.Tick();
        // the mob type is measured: 20 a hit every 1 s (every attacker is this type)
        for (var i = 0; i < 4; i++) { t.Ctx.Knowledge.Sample(1, MobFact.HitAvg, 20); t.Ctx.Knowledge.Sample(1, MobFact.AttackInterval, 1000); }
        return t;
    }

    [Fact]
    public void A_two_hit_mob_with_both_skills_ready_commits_in_a_fraction_of_a_second()
    {
        using var t = Fight(mobHp: 35, attackers: [2000]);   // 15 + 25 = 40 >= 35: two casts, both instant and ready
        var p = Burst.Decide(t.Ctx, 2000, swings: false);
        Assert.True(p.Commit, p.Why);
        Assert.True(p.TtkS < 0.5, $"ttk {p.TtkS}");
        Assert.Equal(2, p.Steps);
        Assert.True(p.TtdS > p.TtkS);
    }

    [Fact]
    public void The_same_mob_with_skills_on_cooldown_and_low_hp_backs_off()
    {
        using var t = Fight(mobHp: 35, attackers: [2000]);
        // both skills just used: 1 s and 3 s to wait; 15 HP left against 20 a second
        t.Ev(InboundKind.SkillCastStart, a: 0, b: 1); t.Ev(InboundKind.SkillCastStart, a: 1, b: 2);
        t.Ev(InboundKind.SelfHp, a: 15);
        t.Tick();
        var p = Burst.Decide(t.Ctx, 2000, swings: false);
        Assert.False(p.Commit, $"{p.Why} ttk {p.TtkS} ttd {p.TtdS}");
        Assert.True(p.TtdS < 1.0);
    }

    [Fact]
    public void Every_attacker_on_us_shortens_the_time_to_die()
    {
        using var one = Fight(mobHp: 90, attackers: [2000]);
        using var three = Fight(mobHp: 90, attackers: [2000, 2001, 2002]);
        foreach (var t in new[] { three })
            foreach (var h in new[] { 2001, 2002 })
                t.Ev(InboundKind.Damage, h: h, h2: t.Ctx.World.Self.Handle, a: 20, b: (long)t.Ctx.World.Self.Hp - 20);
        three.Tick();
        var p1 = Burst.Decide(one.Ctx, 2000, swings: false);
        var p3 = Burst.Decide(three.Ctx, 2000, swings: false);
        Assert.True(p3.TtdS < p1.TtdS / 2, $"one {p1.TtdS} three {p3.TtdS}");
        // 90 HP takes the queue ~3 s (15 every 1 s + 25 every 3 s) against 5 s to die under one attacker, ~1.7 s under three
        Assert.True(p1.Commit, $"one: {p1.Why} ttk {p1.TtkS} ttd {p1.TtdS}");
        Assert.False(p3.Commit, $"three: {p3.Why} ttk {p3.TtkS} ttd {p3.TtdS}");
    }

    [Fact]
    public void Measured_damage_replaces_the_data_prior_and_its_spread_makes_the_plan_pessimistic()
    {
        using var t = Fight(mobHp: 35, attackers: [2000]);
        // our Test Hit measured at 8 / 12 (mean 10, sd ~2.8): the plan counts ~7 a hit, so 35 HP needs more casts
        t.Ev(InboundKind.SkillCastStart, a: 0, b: 7); t.Ev(InboundKind.SkillDamage, h: t.Ctx.World.Self.Handle, h2: 2000, a: 8, b: 27, c: 7);
        t.Ev(InboundKind.SkillCastStart, a: 0, b: 8); t.Ev(InboundKind.SkillDamage, h: t.Ctx.World.Self.Handle, h2: 2000, a: 12, b: 15, c: 8);
        t.Ev(InboundKind.EntityHp, h: 2000, a: 35, b: 500);
        t.Tick();
        var (mean, sd, n) = t.Ctx.World.Fight.Skill(0);
        Assert.Equal((10.0, 2), (mean, n));
        Assert.True(sd > 2);
        var p = Burst.Decide(t.Ctx, 2000, swings: false);
        Assert.True(p.Steps > 2);
    }
}

/// <summary>Test data with two given skills (ids 0 and 1) over the plain test data.</summary>
public sealed class GroupData(ActiveSkillRow a, ActiveSkillRow b) : IClientData
{
    private readonly TestData _t = new();
    public MobInfoRow? Mob(int id) => _t.Mob(id);
    public ActiveSkillRow? Skill(int id) => id == 0 ? a : id == 1 ? b : null;
    public ItemInfoRow? Item(int id) => _t.Item(id);
    public MapInfoRow? Map(int id) => _t.Map(id);
    public MapInfoRow? Map(string mapName) => _t.Map(mapName);
    public QuestDef? Quest(int id) => _t.Quest(id);
    public string? Dialog(int id) => null;
    public int ClassFamily(int classId) => 1;
    public string ClassName(int classId) => "";
    public IReadOnlyCollection<QuestDef> Quests => [];
    public string Dir => "";
}
