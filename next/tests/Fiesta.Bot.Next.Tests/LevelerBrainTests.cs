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
        t.Tick(10);
        Assert.Equal("recover", b.Layers.First(l => l.Name == "survival").ActivePath[0].Name);
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
        mods["brain/leveler/plan"] = mods["brain/leveler/plan"].Replace("  local q, npc = readyHandin()",
            "  M.calls = (M.calls or 0) + 1 if M.calls > 1 then error(\"pick did not finish\") end local q, npc = readyHandin()");
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(200);
        Assert.Single(b.Debug.Transitions(), r => r.Layer == "quest" && r.To == "grind");
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
        Assert.Empty(b.Debug.Errors().Where(x => !x.Error.Contains("E_BUDGET")));
    }
}
