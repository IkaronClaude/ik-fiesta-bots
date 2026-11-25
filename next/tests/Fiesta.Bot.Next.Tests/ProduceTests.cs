using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Production: the frames of a captured Elrue [1] production and the cast's result.</summary>
public class ProduceTests
{
    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    [Fact]
    public void Production_frames_from_a_capture_are_decoded()
    {
        var t = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        var sink = new Sink();
        t.Dispatch(0x4823, new byte[] { 0xAD, 0x71, 0x01, 0x0B }, 0, sink);         // field 29101 chosen
        t.Dispatch(0x481D, new byte[] { 0x20, 0x4E, 0x02, 0, 0, 0 }, 0, sink);      // recipe 20000 mastery 2
        t.Dispatch(0x203B, new byte[] { 0x40, 0x1F, 0x01, 0x0B }, 0, sink);         // made item 8000
        Assert.Equal((InboundKind.ProduceField, 29101L, 0x0B01L), (sink.E[0].Kind, sink.E[0].A, sink.E[0].B));
        Assert.Equal((InboundKind.SkillExp, 20000L, 2L), (sink.E[1].Kind, sink.E[1].A, sink.E[1].B));
        Assert.Equal((InboundKind.ProduceMade, 8000L, 0x0B01L), (sink.E[2].Kind, sink.E[2].A, sink.E[2].B));
    }

    [Fact]
    public void A_production_cast_resolves_on_the_make_and_records_mastery()
    {
        using var t = new TestBot();
        t.Tick(2);
        var a = t.Ctx.Actions.Issue(ActionKind.Produce, 1, 20000, 0, [IntentEncoder.Raw(IntentEncoder.OpProduceCast, [0x20, 0x4E], -1)]);
        t.Tick();
        Assert.Contains(IntentEncoder.OpProduceCast, t.Sent);
        t.Ev(InboundKind.SkillExp, a: 20000, b: 4);
        t.Ev(InboundKind.ProduceMade, a: 8000, b: 0x0B01);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
        Assert.Equal(4, t.Ctx.World.Skills.Mastery[20000]);
        var b = t.Ctx.Actions.Issue(ActionKind.Produce, 1, 20000, 0, [IntentEncoder.Raw(IntentEncoder.OpProduceCast, [0x20, 0x4E], -1)]);
        t.Tick();
        t.Ev(InboundKind.ProduceFail, a: 0x0B05);
        t.Tick();
        Assert.Equal((ActionState.Refused, (ushort)0x0B05), (t.Ctx.Actions.Get(b).State, t.Ctx.Actions.Get(b).Code));
    }

    [Fact]
    public void A_call_to_a_missing_function_of_any_api_module_fails_the_lint()
    {
        var mods = typeof(Fiesta.Bot.Api.ProduceModule).Assembly.GetTypes()
            .Select(t => t.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "LuaModuleAttribute"))
            .Where(a => a is not null).Select(a => (string)a!.GetType().GetProperties().First(p => p.PropertyType == typeof(string)).GetValue(a)!).ToList();
        Assert.Contains("produce", mods);
        var src = "local M = {}\nfunction M.t()\n" + string.Concat(mods.Select(m => $"  {m}.zzNope()\n")) + "end\nreturn M\n";
        const string yaml = """
brain: x
api: 3
modules: [brain/x/m]
layers:
  - name: l
    priority: 1
    machine:
      initial: s
      states:
        s:
          tick: m.t
          terminal: true
""";
        var lint = Fiesta.Bot.Brain.BrainLinter.Lint(Fiesta.Bot.Brain.BrainLoader.Parse(yaml), new Dictionary<string, string> { ["brain/x/m"] = src });
        foreach (var m in mods) Assert.Contains(lint, l => l.Code == "API_NAME" && l.Message.Contains($"{m}.zzNope"));
    }

    [Fact]
    public void A_removed_agent_is_not_ticked_once_RemoveAndWait_returns()
    {
        using var t = new TestBot();
        var ticks = new Fiesta.Bot.Fleet.TickScheduler(2, TimeSpan.FromMilliseconds(5));
        ticks.Start();
        try
        {
            ticks.Add(t.Agent);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (t.Ctx.Tick < 3 && DateTime.UtcNow < until) Thread.Sleep(5);
            Assert.True(t.Ctx.Tick >= 3);
            Assert.True(ticks.RemoveAndWait(t.Agent, TimeSpan.FromSeconds(5)));
            var after = t.Ctx.Tick;
            Thread.Sleep(100);
            Assert.Equal(after, t.Ctx.Tick);
        }
        finally { ticks.Dispose(); }
    }

    [Fact]
    public void A_skill_learned_from_a_book_is_in_the_skill_book_at_once()
    {
        // captured: ITEM_USE_ACK 0x0700 for Fire Bolt [02], then SKILL_LEARNSUC {0x1799, 0}
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016)).Dispatch(0x4804, new byte[] { 0x99, 0x17, 0x00 }, 0, sink);
        Assert.Equal((InboundKind.SkillLearned, 6041L), (sink.E[0].Kind, sink.E[0].A));
        using var t = new TestBot();
        t.Tick(2);
        t.In.TryWrite(sink.E[0]);
        t.Tick();
        Assert.True(t.Ctx.World.Skills.Has(6041));
    }

    [Fact]
    public void A_quest_the_npc_will_not_start_fails_the_talk_with_the_servers_code()
    {
        // captured at Nina (Eld): QUEST_SELECT_START_REQ {27, 20028} -> QUEST_SELECT_START_ACK {27, 20028, 0x0B47}
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016)).Dispatch(0x4410, new byte[] { 0x1B, 0x00, 0x3C, 0x4E, 0x47, 0x0B }, 0, sink);
        Assert.Equal((InboundKind.QuestAck, 0x4410L, 0x0B47L, 20028L), (sink.E[0].Kind, sink.E[0].A, sink.E[0].B, sink.E[0].C));
        using var t = new TestBot();
        t.Tick(2);
        t.Ev(InboundKind.EntityAppeared, h: 21624, a: (long)EntityKind.Npc, b: 27, x: 5020, y: 5000);
        t.Tick();
        var a = t.Ctx.Procedures.Start(new QuestDialogueProcedure(21624, 20028, -1), ActionKind.Talk, 1, t.Ctx);
        t.Ev(InboundKind.NpcMenu, a: 27);
        t.Tick();
        Assert.Contains(IntentEncoder.OpQuestSelectStart, t.Sent);
        t.In.TryWrite(sink.E[0]);
        t.Tick(100);   // 5 s: no page follows
        var r = t.Ctx.Procedures.Recent.Last();
        Assert.Equal(ActionState.Failed, t.Ctx.Actions.Get(a).State);
        Assert.Contains("not offered by the NPC", r.P.FailReason);
        Assert.Contains("0x0B47", r.P.FailReason);
    }

    [Fact]
    public void The_ledger_learns_what_a_mob_type_costs_and_pays()
    {
        using var t = new TestBot();
        t.Tick(2);
        for (var k = 0; k < 2; k++)
        {
            var h = 2000 + k;
            t.Ev(InboundKind.EntityAppeared, h: h, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
            t.Tick();
            t.Ev(InboundKind.Damage, h: 1000, h2: h, a: 50, b: 450);     // our first hit
            t.Ev(InboundKind.Damage, h: h, h2: 1000, a: 10, b: 80);      // it hits back: in combat
            t.Tick(20);                                                   // 1 s
            t.Ev(InboundKind.StoneUse, a: 0);                             // a stone in the fight
            t.Tick(20);                                                   // 1 s
            t.Ev(InboundKind.EntityDied, h: h, h2: 1000);
            t.Ev(InboundKind.SelfExp, h: h, a: 40);                        // EXPGAIN names the mob
            t.Tick();
        }
        t.Ev(InboundKind.SelfExp, h: -1, a: 300);                         // a quest reward: no mob
        t.Tick();
        var c = t.Ctx.World.Economy.Mob(1);
        Assert.Equal(2, c.Kills);
        Assert.Equal(80, c.Exp);
        Assert.Equal(2, c.Stones);
        Assert.InRange(c.FightMs / c.Kills, 1500, 3000);
        Assert.Equal(300, t.Ctx.World.Economy.QuestExp);
    }

    [Fact]
    public void A_production_field_we_work_is_known_from_the_login_skill_list()
    {
        // live 21:31: NextM knows Stone Production (29101) from the login skill list but asked to choose it again every
        // minute (refused 0x0B13): fields were only learned from a choice ack
        using var t = new TestBot();
        t.Tick(2);
        var p = new Fiesta.Bot.Api.ProduceModule(t.Ctx);
        Assert.False(p.HasField(29101));
        t.Ev(InboundKind.SkillLearned, a: 29101);
        t.Tick();
        Assert.True(p.HasField(29101));
    }

    [Fact]
    public void The_rest_tent_is_pitched_and_folded_with_its_acks()
    {
        // PITCHTENT_REQ 0x2027 -> PITCHTENT_ACK {0x0A81}; FOLDTENT_REQ 0x202A -> FOLDTENT_ACK {0x0A81}; a refusal carries why
        var sink = new Sink();
        var table = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        table.Dispatch(0x2028, new byte[] { 0x81, 0x0A }, 0, sink);
        table.Dispatch(0x2028, new byte[] { 0x83, 0x0A }, 0, sink);
        table.Dispatch(0x202B, new byte[] { 0x81, 0x0A }, 0, sink);
        Assert.Equal((InboundKind.Tent, 1L, 0x0A81L), (sink.E[0].Kind, sink.E[0].A, sink.E[0].B));
        Assert.Equal(0x0A83L, sink.E[1].B);
        Assert.Equal(0L, sink.E[2].A);
        using var t = new TestBot();
        t.Tick(2);
        var act = new Fiesta.Bot.Api.ActModule(t.Ctx);
        var refused = act.PitchTent();
        t.Tick();
        Assert.Contains(IntentEncoder.OpPitchTent, t.Sent);
        t.In.TryWrite(sink.E[1]);
        t.Tick();
        Assert.Equal(ActionState.Refused, t.Ctx.Actions.Get(new ActionId(refused)).State);
        var a = act.PitchTent();
        t.Tick();
        t.In.TryWrite(sink.E[0]);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(new ActionId(a)).State);
        Assert.True(t.Ctx.World.Self.TentPitched);
        act.FoldTent();
        t.Tick();
        t.In.TryWrite(sink.E[2]);
        t.Tick();
        Assert.False(t.Ctx.World.Self.TentPitched);
    }
}
