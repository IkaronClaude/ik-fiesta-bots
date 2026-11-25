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
}
