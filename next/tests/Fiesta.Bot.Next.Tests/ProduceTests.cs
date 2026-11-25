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
}
