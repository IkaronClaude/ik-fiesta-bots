using Fiesta.Bot.Brain;
using Fiesta.Bot.Protocol;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>The leveller brain is operator-private (scripts/ is not in git); these tests run when it is present.</summary>
public class LevelerBrainTests
{
    private static (string Yaml, Dictionary<string, string> Modules)? Load()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scripts", "brains", "leveler"))) d = d.Parent;
        if (d is null) return null;
        var dir = Path.Combine(d.FullName, "scripts", "brains", "leveler");
        var mods = Directory.GetFiles(dir, "*.lua").ToDictionary(f => "brain/leveler/" + Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        return (File.ReadAllText(Path.Combine(dir, "brain.yaml")), mods);
    }

    [Fact]
    public void Lints_clean_and_runs_without_script_errors()
    {
        if (Load() is not var (yaml, mods)) return;
        Assert.DoesNotContain(BrainLinter.Lint(BrainLoader.Parse(yaml), mods), l => l.Severity == LintSeverity.Error);
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(40);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
        // no quest data, no atlas: the quest layer falls back to grinding and keeps re-planning, never wedges
        Assert.Contains(b.Layers.First(l => l.Name == "quest").ActivePath[0].Name, new[] { "plan", "grind" });
    }

    [Fact]
    public void Heals_when_hp_drops_and_fights_what_attacks_it()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.SelfHp, a: 20);
        t.Tick(20);
        // out of a fight: rest (HP comes back by itself, no stones spent)
        Assert.Equal("rest", b.Layers.First(l => l.Name == "survival").ActivePath[0].Name);
        // a mob hits us: the combat layer takes it on
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Ev(InboundKind.Damage, h: 2000, h2: 1000, a: 5, b: 15);
        t.Tick(5);
        Assert.Equal("fight", b.Layers.First(l => l.Name == "combat").ActivePath[0].Name);
        // the first call of a C# helper can pay its JIT inside a 2 ms condition budget on a loaded test machine
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void Flees_when_hurt_with_nothing_to_heal_and_recovers_once_clear()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Ev(InboundKind.Damage, h: 2000, h2: 1000, a: 5, b: 15);   // 15 of 100 hp, no stones, no heal skill
        t.Tick(40);
        Assert.Equal("flee", b.Layers.First(l => l.Name == "survival").ActivePath[0].Name);
        Assert.True(t.Sent.Contains(IntentEncoder.OpMoveRun), string.Join(",", t.Sent.Select(x => x.ToString("X4"))) + " | " + string.Join(" / ", t.Ctx.Actions.Recent(10).Select(r => $"{r.Kind} {r.State} {r.Code}")));
        t.Ev(InboundKind.EntityLeft, h: 2000);
        t.Tick(120);   // 6 s with nothing on us
        Assert.NotEqual("flee", b.Layers.First(l => l.Name == "survival").ActivePath[0].Name);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
    }

    [Fact]
    public void Two_deaths_on_a_map_within_five_minutes_avoid_it()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Ev(InboundKind.SelfBase, a: 10, b: 0, c: 100, text: "TestMap");
        t.Tick(5);
        Assert.Equal("TestMap", t.Ctx.World.Map.MapName);
        for (var i = 0; i < 2; i++)
        {
            t.Ev(InboundKind.DeadMenu, a: 180);
            t.Tick(3);
            t.Ev(InboundKind.Revive, a: t.Ctx.World.Map.MapId, b: 1, x: 5000, y: 5000);
            t.Tick(3);
        }
        Assert.True(t.Ctx.Refused(t.Ctx.World.Map.MapId));
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
    }

    [Fact]
    public void Spends_free_stat_points()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.StatPoints, a: 2);
        t.Tick(30);
        Assert.Contains(IntentEncoder.OpStatPoint, t.Sent);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
    }

    [Fact]
    public void Accepts_a_party_invitation()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        var e = InboundEvent.Of(InboundKind.PartyProposal, 0x3803, t.Clock.NowMs); e.Text = "Leader";
        t.In.TryWrite(e);
        t.Tick(30);
        Assert.Contains(IntentEncoder.OpPartyAllow, t.Sent);
        Assert.Equal("", t.Ctx.World.Party.Invite.Master);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
    }

    [Fact]
    public void A_pick_that_does_not_finish_never_replays_the_previous_task()
    {
        // the live cycle (2026-11-25): plan.pick ran over the Lua budget, bb.bot.task still held the hand-in it had just
        // given up, and the transitions sent the quest layer back to the same hand-in every 30 s
        if (Load() is not var (yaml, mods)) return;
        // the first pick works (grind, which fails at once here: no spawn data), every later pick dies half-way
        mods["brain/leveler/plan"] = mods["brain/leveler/plan"]
            .Replace("  bb.layer.pkBest = nil", "  bb.layer.pkBest = nil M.calls = (M.calls or 0) + 1 if M.calls > 1 then error(\"pick did not finish\") end")
            .Replace("  local pk = bb.layer.pk", "  if M.calls == 1 and bb.layer.pk ~= nil then finish(score(\"grind\", -1, -1, -1, 1, 1)) return end local pk = bb.layer.pk");
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(200);
        Assert.True(b.Debug.Transitions().Count(r => r.Layer == "quest" && r.To == "grind") == 1, string.Join(" | ", b.Debug.Errors().Select(e => e.Error)) + string.Join(" ; ", b.Debug.Transitions().Where(r => r.Layer == "quest").Select(r => $"{r.Tick}:{r.From}->{r.To}({r.Reason})")));
    }

    [Fact]
    public void A_grind_that_cannot_start_is_not_retried_every_tick()
    {
        // live 2026-11-25: with no reachable grind spot the quest layer went grind -> give_up -> plan -> grind 36 times a
        // minute (caught by the transition-cycle alarm)
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(400);   // 20 s
        Assert.True(b.Debug.Transitions().Count(r => r.Layer == "quest" && r.To == "grind") <= 1);
    }

    [Fact]
    public void A_refused_stat_point_moves_on_to_the_next_stat()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.StatPoints, a: 2);
        t.Tick(20);
        var first = t.Ctx.Actions.Recent(20).First(r => r.Kind == Fiesta.Bot.Agent.ActionKind.StatPoint);
        t.Ev(InboundKind.StatPointRefused, a: 0x0AC2);   // live: INCPOINTFAIL 0x0AC2 for End on a level-7 fighter
        t.Tick(20);
        var next = t.Ctx.Actions.Recent(20).First(r => r.Kind == Fiesta.Bot.Agent.ActionKind.StatPoint);
        Assert.NotEqual(first.Arg0, next.Arg0);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));   // wall-clock Lua budgets depend on machine load
    }

    private static (string Yaml, Dictionary<string, string> Modules)? LoadSupport()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scripts", "brains", "support"))) d = d.Parent;
        if (d is null) return null;
        var dir = Path.Combine(d.FullName, "scripts", "brains", "support");
        return (File.ReadAllText(Path.Combine(dir, "brain.yaml")),
            Directory.GetFiles(dir, "*.lua").ToDictionary(f => "brain/support/" + Path.GetFileNameWithoutExtension(f), File.ReadAllText));
    }

    [Fact]
    public void Support_brain_runs_and_answers_a_buff_request_once()
    {
        if (LoadSupport() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.EntityAppeared, h: 2500, a: (long)Fiesta.Bot.Agent.EntityKind.Player, b: 1, x: 5050, y: 5000, text: "Friend");
        var chat = InboundEvent.Of(InboundKind.Chat, 0x2002, t.Clock.NowMs); chat.Handle = 2500; chat.Text = "buff please";
        t.In.TryWrite(chat);
        t.Tick(40);
        Assert.Single(b.Debug.Transitions(), r => r.Layer == "buffs" && r.To == "casting");
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void A_scenario_area_the_server_asks_about_is_confirmed()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        var e = InboundEvent.Of(InboundKind.Scenario, 0x6C05, t.Clock.NowMs); e.A = 5; e.Text = "Zone_Mob01";
        t.In.TryWrite(e);
        t.Tick(10);
        Assert.Contains(IntentEncoder.OpAreaEntryAck, t.Sent);
        Assert.True(t.Ctx.World.Scenario.Confirmed.Contains("Zone_Mob01"));
        // the room's wave: two mobs appear; the instance layer holds the room until both are gone
        t.Ev(InboundKind.EntityAppeared, h: 2100, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5100, y: 5000);
        t.Ev(InboundKind.EntityAppeared, h: 2101, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5150, y: 5000);
        t.Tick(10);
        Assert.Equal(2, t.Ctx.World.Scenario.Wave.Count);
        Assert.Equal("wave", b.Layers.First(l => l.Name == "instance").ActivePath[0].Name);
        t.Ev(InboundKind.EntityLeft, h: 2100);
        t.Ev(InboundKind.EntityLeft, h: 2101);
        t.Tick(10);
        Assert.Equal("idle", b.Layers.First(l => l.Name == "instance").ActivePath[0].Name);
        Assert.Empty(b.Debug.Errors().Where(x => !x.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void The_producer_brain_lints_clean_and_runs()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scripts", "brains", "producer"))) d = d.Parent;
        if (d is null) return;
        var dir = Path.Combine(d.FullName, "scripts", "brains", "producer");
        var mods = Directory.GetFiles(dir, "*.lua").ToDictionary(f => "brain/producer/" + Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        var yaml = File.ReadAllText(Path.Combine(dir, "brain.yaml"));
        Assert.DoesNotContain(BrainLinter.Lint(BrainLoader.Parse(yaml), mods), l => l.Severity == LintSeverity.Error);
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(20);
        // a commission in chat for something we do not make is answered, nothing is ordered
        t.Ev(InboundKind.Chat, h: 2001, text: "need 5 x hp potion");
        t.Tick(5);
        Assert.Contains(IntentEncoder.OpChat, t.Sent);
        Assert.Empty(b.Debug.Errors().Where(e => !e.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void The_job_change_instance_is_played_room_by_room_as_the_capture_shows()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        // linked into the instance (Job1_Dn01 in the capture; an instance map here)
        t.Ev(InboundKind.MapLink, a: 8, x: 1000, y: 1000);
        t.Tick(5);
        // an indoor map (InSide) like any cave: an instance only once the server runs a scenario there
        Assert.False(t.Ctx.World.Map.IsInstance);
        // the scripted copy of us appears as a character and is turned into a mob (OBJTYPECHANGE {0x43BC, 5})
        t.Ev(InboundKind.EntityAppeared, h: 0x43BC, a: (long)Fiesta.Bot.Agent.EntityKind.Player, b: -1, x: 1100, y: 1000, text: "Shadow");
        var obj = InboundEvent.Of(InboundKind.Scenario, 0x6C0B, t.Clock.NowMs); obj.A = 11; obj.Handle = 0x43BC; obj.B = 5;
        t.In.TryWrite(obj);
        t.Tick(2);
        Assert.True((t.Ctx.World.KindOf(t.Ctx.World.Entities.SlotOf(0x43BC), t.Clock.NowMs) & Fiesta.Bot.Agent.EntityKind.Huntable) != 0);
        Assert.True(t.Ctx.World.Map.IsInstance);
        // the server asks for Zone_Mob01: the confirm is repeated until the room's wave appears
        var req = InboundEvent.Of(InboundKind.Scenario, 0x6C05, t.Clock.NowMs); req.A = 5; req.Text = "Zone_Mob01";
        t.In.TryWrite(req);
        t.Sent.Clear();
        t.Tick(40);   // 2 s
        Assert.True(t.Sent.Count(o => o == IntentEncoder.OpAreaEntryAck) >= 3, string.Join(",", t.Sent.Select(o => o.ToString("X4"))));
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 1200, y: 1000);
        t.Ev(InboundKind.EntityAppeared, h: 3001, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 1250, y: 1000);
        t.Tick(10);
        t.Sent.Clear();
        t.Tick(20);
        Assert.DoesNotContain(IntentEncoder.OpAreaEntryAck, t.Sent);    // the wave came: no more confirms
        Assert.Equal("wave", b.Layers.First(l => l.Name == "instance").ActivePath[0].Name);
        // the wave and the copy go (it turns back into an NPC, then vanishes)
        t.Ev(InboundKind.EntityLeft, h: 3000);
        t.Ev(InboundKind.EntityLeft, h: 3001);
        var back = InboundEvent.Of(InboundKind.Scenario, 0x6C0B, t.Clock.NowMs); back.A = 11; back.Handle = 0x43BC; back.B = 4;
        t.In.TryWrite(back);
        t.Tick(10);
        Assert.Equal("idle", b.Layers.First(l => l.Name == "instance").ActivePath[0].Name);
        Assert.Equal(0, (int)(t.Ctx.World.KindOf(t.Ctx.World.Entities.SlotOf(0x43BC), t.Clock.NowMs) & Fiesta.Bot.Agent.EntityKind.Mob));
        // the quest layer waits inside the instance; the promotion is applied ({02} in the capture)
        Assert.Contains(b.Layers.First(l => l.Name == "quest").ActivePath[0].Name, new[] { "plan" });
        t.Ev(InboundKind.Promoted, a: 2);
        t.Tick(2);
        Assert.Equal(2, t.Ctx.World.Self.ClassId);
        Assert.Empty(b.Debug.Errors().Where(x => !x.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void A_scripted_death_spiral_is_reversed_without_lowering_the_fight_level()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Ev(InboundKind.SelfBase, a: 10, b: 0, c: 400);   // logged in with 400
        t.Tick(5);
        // die under two mobs, revive, buy stones, die again ... the purse drains
        for (var k = 0; k < 3; k++)
        {
            t.Ev(InboundKind.EntityAppeared, h: 2000 + 2 * k, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5050, y: 5000);
            t.Ev(InboundKind.EntityAppeared, h: 2001 + 2 * k, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 4950, y: 5000);
            t.Ev(InboundKind.Damage, h: 2000 + 2 * k, h2: 1000, a: 30, b: 40);
            t.Ev(InboundKind.Damage, h: 2001 + 2 * k, h2: 1000, a: 30, b: 10);
            t.Tick(2);
            t.Ev(InboundKind.DeadMenu, a: 30);
            t.Tick(5);
            t.Ev(InboundKind.Revive, a: 0, x: 5000, y: 5000);
            t.Tick(5);
            t.Ev(InboundKind.SelfMoney, a: 400 - 100 * (k + 1));
            t.Ev(InboundKind.StoneReserve, a: 0, b: 10, c: 1);
            t.Tick(5);
        }
        t.Tick(240, 50);   // 12 s: the guard checks
        var bot = b.BlackboardJson()["bot"]!;
        var now = Fiesta.Bot.Services.SystemClock.UnixMs(t.Clock.NowMs);
        Assert.True(bot["pullOneUntil"]!.GetValue<long>() > now, Diag(t));   // the cause (several mobs on us) is fixed
        Assert.True(bot["restFullUntil"]!.GetValue<long>() > now);
        Assert.True(bot["avoidMob"]?[ "1" ] is not null);                     // the same mob type killed us again: left alone
        Assert.True(bot["econSafe"]!.GetValue<bool>());                      // money 100 and falling: efficient earning
        Assert.Null(bot["caution"]);                                         // no lowering of the fight level
        Assert.Contains("DEATH DIAGNOSIS", Diag(t));
        var (_, moneyH, deathsH, stonesH) = t.Ctx.World.Economy.Rates(30, t.Clock.NowMs);
        Assert.True(moneyH < 0 && deathsH > 0 && stonesH > 0, $"{moneyH} {deathsH} {stonesH}");
        // reversal: the purse grows again (loot sold, quests paid): efficient earning ends
        for (var k = 1; k <= 10; k++) { t.Ev(InboundKind.SelfMoney, a: 100 + 200 * k); t.Tick(60, 1000); }
        bot = b.BlackboardJson()["bot"]!;
        Assert.False(bot["econSafe"]!.GetValue<bool>());
        Assert.Empty(b.Debug.Errors().Where(x => !x.Error.Contains("E_BUDGET")));
    }

    [Fact]
    public void The_planner_values_a_mob_by_what_the_bot_measured_against_it()
    {
        // efficiency: kill time / exp / stones per kill come from the ledger once a mob type has been fought
        if (Load() is not var (yaml, mods)) return;
        mods["brain/leveler/plan"] = mods["brain/leveler/plan"].Replace("  closeMaps()", "  bb.bot.k1s, bb.bot.k1x = value.kill(1) closeMaps()");
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(20);
        var bot = b.BlackboardJson()["bot"]!;
        Assert.Equal(18.0, double.Parse(bot["k1s"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));   // never fought: the judge's default 15 s + 3 s rest
        for (var k = 0; k < 3; k++)
        {
            var h = 2000 + k;
            t.Ev(InboundKind.EntityAppeared, h: h, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5050, y: 5000);
            t.Tick();
            t.Ev(InboundKind.Damage, h: 1000, h2: h, a: 50, b: 450);
            t.Tick(40);   // 2 s
            t.Ev(InboundKind.EntityDied, h: h, h2: 1000);
            t.Ev(InboundKind.SelfExp, h: h, a: 60);
            t.Tick();
        }
        t.Tick(200);      // the next pick
        bot = b.BlackboardJson()["bot"]!;
        Assert.InRange(double.Parse(bot["k1s"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture), 4.5, 6.5);   // measured ~2 s + 3 s rest
        Assert.Equal(60.0, double.Parse(bot["k1x"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));           // measured exp per kill, no stones used
    }

    private static string Diag(TestBot t)
    {
        var buf = new Fiesta.Bot.Services.Logging.LogEntry[400];
        var n = t.Ctx.Log.Ring.Read(Fiesta.Bot.Services.Logging.LogLevel.Trace, null, 0, long.MaxValue, buf);
        return string.Join(" / ", buf.Take(n).Select(e => Fiesta.Bot.Services.Logging.LogRenderer.Render(e)).Where(x => x.Contains("ECON") || x.Contains("DEATH") || x.Contains("econ")));
    }
}
