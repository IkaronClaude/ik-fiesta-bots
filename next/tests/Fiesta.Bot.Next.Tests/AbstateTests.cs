using System.Text.Json;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Server;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Abnormal states on us and on the entities in view, and the aggro marks a death clears.</summary>
public class AbstateTests
{
    private const int Mob = 2000;

    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    private static (TestBot Bot, GameStream Stream, List<JsonElement> Got) Setup()
    {
        var t = new TestBot();
        var tap = new GameTap();
        t.Agent.Game = tap;
        var got = new List<JsonElement>();
        var s = new GameStream("t", tap);
        s.Add(new GameViewer(m => { got.Add(JsonDocument.Parse(m).RootElement.Clone()); return true; }));
        return (t, s, got);
    }

    private static IEnumerable<JsonElement> Ops(List<JsonElement> got) =>
        got.Where(m => m.GetProperty("t").GetString() == "d").SelectMany(m => m.GetProperty("ops").EnumerateArray());

    [Fact]
    public void Decodes_the_states_an_entity_brings_into_view_and_our_handleless_inform()
    {
        var d = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        var sink = new Sink();
        // NextA 09:44:03: 0x1C19 6219 01 23010000 88130000 01000000 = handle 0x1962, state 291 for 5000 ms
        d.Dispatch(0x1C19, Convert.FromHexString("621901230100008813000001000000"), 0, sink);
        var e = Assert.Single(sink.E);
        Assert.Equal(InboundKind.SelfAbstate, e.Kind);
        Assert.Equal((0x1962, 291L, 1L, 5000L), (e.Handle, e.A, e.B, e.C));

        // NextA 09:44:40: 0x242A 6F000000 B0240300 = state 111 on us for 206 s, no handle
        sink.E.Clear();
        d.Dispatch(0x242A, Convert.FromHexString("6F000000B0240300"), 0, sink);
        e = Assert.Single(sink.E);
        Assert.Equal((-1, 111L, 1L, 206000L), (e.Handle, e.A, e.B, e.C));
    }

    [Fact]
    public void Entity_states_reach_the_game_stream_and_leave_it()
    {
        var (t, s, got) = Setup();
        t.Ev(InboundKind.EntityAppeared, h: Mob, a: (long)EntityKind.Mob, b: 1, x: 5100, y: 5000);
        t.Tick(3); s.Pump(0, null);

        t.Ev(InboundKind.SelfAbstate, h: Mob, a: 291, b: 1, c: 5000);
        t.Tick(3); s.Pump(0, null);
        var ea = Ops(got).Last(o => o[0].GetString() == "ea");
        Assert.Equal(Mob, ea[1].GetInt32());
        Assert.Equal(291, ea[2][0][0].GetInt32());
        Assert.True(ea[2][0][1].GetInt64() > 0);   // an end time

        t.Ev(InboundKind.SelfAbstate, h: Mob, a: 291, b: 0);
        t.Tick(3); s.Pump(0, null);
        ea = Ops(got).Last(o => o[0].GetString() == "ea");
        Assert.Equal(0, ea[2].GetArrayLength());
        Assert.False(t.Ctx.World.Self.HasAbstate(291, t.Clock.NowMs));   // never put on us
    }

    [Fact]
    public void A_corpse_shows_no_states()
    {
        var (t, s, got) = Setup();
        t.Ev(InboundKind.EntityAppeared, h: Mob, a: (long)EntityKind.Mob, b: 1, x: 5100, y: 5000);
        t.Ev(InboundKind.SelfAbstate, h: Mob, a: 307, b: 1, c: 5000);
        t.Tick(3); s.Pump(0, null);

        t.Ev(InboundKind.EntityDied, h: Mob);
        t.Tick(3); s.Pump(0, null);
        Assert.Equal(0, Ops(got).Last(o => o[0].GetString() == "ea")[2].GetArrayLength());

        // the server keeps sending states for the corpse (NextA: mob 6541 got states 1 and 0 after it died): ignored
        t.Ev(InboundKind.SelfAbstate, h: Mob, a: 1, b: 1, c: 5000);
        t.Tick(3); s.Pump(0, null);
        Assert.Equal(0, Ops(got).Last(o => o[0].GetString() == "ea")[2].GetArrayLength());
    }

    [Fact]
    public void A_handleless_state_is_ours()
    {
        var (t, s, got) = Setup();
        t.Tick(3); s.Pump(0, null);
        t.Ev(InboundKind.SelfAbstate, a: 111, b: 1, c: 206000);
        t.Tick(3); s.Pump(0, null);
        Assert.Contains(Ops(got), o => o[0].GetString() == "ab" && o[1].GetInt32() == 111);
    }

    [Fact]
    public void Dying_clears_who_is_on_us()
    {
        using var t = new TestBot();
        t.Ev(InboundKind.EntityAppeared, h: Mob, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Ev(InboundKind.Damage, h: Mob, h2: t.Ctx.World.Self.Handle >= 0 ? t.Ctx.World.Self.Handle : 1000, a: 5, b: 50);
        t.Tick(2);
        var w = t.Ctx.World;
        var slot = w.Entities.SlotOf(Mob);
        Assert.True((w.KindOf(slot, t.Clock.NowMs) & EntityKind.Aggro) != 0);

        t.Ev(InboundKind.DeadMenu);
        t.Tick(2);
        Assert.Equal(0, (int)(w.KindOf(slot, t.Clock.NowMs) & EntityKind.Aggro));
    }
}
