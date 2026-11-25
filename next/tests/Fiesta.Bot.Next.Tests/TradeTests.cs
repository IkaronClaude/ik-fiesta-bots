using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Player trade: decoding, state and step actions resolved by their acks / fails.</summary>
public class TradeTests
{
    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    private static InboundEvent One(ushort op, params byte[] p)
    {
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016)).Dispatch(op, p, 0, sink);
        return Assert.Single(sink.E);
    }

    [Fact]
    public void Trade_frames_are_decoded_by_command()
    {
        var ask = One(0x4C02, 0x34, 0x12);
        Assert.Equal((InboundKind.TradeEvent, 2L, 0x1234L), (ask.Kind, ask.A, ask.B));
        var up = One(0x4C0F, 7, 2);
        Assert.Equal((15L, 7L, 2L), (up.A, up.B, up.C));
        var theirs = One(0x4C10, 1, 0xBC, 0x0D, 5);   // board 1, item 3516, lot 5
        Assert.Equal((16L, 1L, 3516L, 5u), (theirs.A, theirs.B, theirs.C, theirs.X));
        var cen = One(0x4C18, 0x10, 0x27, 0, 0, 0, 0, 0, 0);
        Assert.Equal((24L, 10000L), (cen.A, cen.C));
        var no = One(0x4C04, 0xC8, 0x06);
        Assert.Equal((4L, 0x06C8L), (no.A, no.B));
        Assert.Equal((ushort)0x4C01, IntentEncoder.Trade(1, [1, 2], -1).Opcode);
    }

    private static void Trade(TestBot t, int cmd, long b = 0, long c = 0, uint x = 0)
    {
        var e = InboundEvent.Of(InboundKind.TradeEvent, (ushort)(0x4C00 | cmd), t.Clock.NowMs); e.A = cmd; e.B = b; e.C = c; e.X = x;
        t.In.TryWrite(e);
        t.Tick();
    }

    private static ActionId Step(TestBot t, ActionKind k, int cmd, long arg = 0, params byte[] p)
    {
        var a = t.Ctx.Actions.Issue(k, 1, arg, cmd, [IntentEncoder.Trade(cmd, p, -1)]);
        t.Tick();
        return a;
    }

    [Fact]
    public void A_trade_runs_propose_board_lock_decide_complete()
    {
        using var t = new TestBot();
        t.Tick(2);
        var p = Step(t, ActionKind.TradePropose, 1, 2001, 0xD1, 0x07);
        Assert.Contains((ushort)0x4C01, t.Sent);
        Trade(t, 9, b: 2001);
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(p).State);
        Assert.Equal((TradePhase.Open, 2001), (t.Ctx.World.Trade.Phase, t.Ctx.World.Trade.Partner));
        Assert.True(t.Ctx.Events.Fired(EventKind.TradeStarted) || t.Ctx.Events.Count(EventKind.TradeStarted, 10_000, t.Clock.NowMs) > 0);
        var item = Step(t, ActionKind.TradeOfferItem, 13, 4, 4);
        Trade(t, 15, b: 4, c: 0);
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(item).State);
        Assert.True(t.Ctx.World.Trade.OnBoard(4));
        Trade(t, 16, b: 0, c: 3516, x: 5);
        Assert.Equal((3516, 5), t.Ctx.World.Trade.TheirsAt(0));
        var money = Step(t, ActionKind.TradeOfferMoney, 21, 100);
        Trade(t, 22, b: 0x1234);   // refused
        Assert.Equal((ActionState.Refused, (ushort)0x1234), (t.Ctx.Actions.Get(money).State, t.Ctx.Actions.Get(money).Code));
        var lk = Step(t, ActionKind.TradeLock, 25);
        Trade(t, 27);
        Trade(t, 28);
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(lk).State);
        Assert.True(t.Ctx.World.Trade.MyLocked && t.Ctx.World.Trade.TheirLocked);
        var dc = Step(t, ActionKind.TradeConfirm, 31);
        Trade(t, 33);
        Trade(t, 34);
        Trade(t, 36);
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(dc).State);
        Assert.Equal(TradePhase.Done, t.Ctx.World.Trade.Phase);
        Assert.True(t.Ctx.Events.Count(EventKind.TradeEnded, 10_000, t.Clock.NowMs) > 0);
    }

    [Fact]
    public void A_confirm_is_acked_by_the_completion_when_it_comes_first()
    {
        using var t = new TestBot();
        t.Tick(2);
        Trade(t, 9, b: 2001);
        var dc = Step(t, ActionKind.TradeConfirm, 31);
        Trade(t, 36);   // live: OPPOSIT_DECIDE, then COMPLETE right after our DECIDE, no DECIDE_ACK
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(dc).State);
    }

    [Fact]
    public void A_partner_cancel_refuses_the_pending_step()
    {
        using var t = new TestBot();
        t.Tick(2);
        Trade(t, 2, b: 2001);
        Assert.Equal(TradePhase.Asked, t.Ctx.World.Trade.Phase);
        Trade(t, 9, b: 2001);
        var lk = Step(t, ActionKind.TradeLock, 25);
        Trade(t, 12);
        Assert.Equal((ActionState.Refused, (ushort)0x4C0C), (t.Ctx.Actions.Get(lk).State, t.Ctx.Actions.Get(lk).Code));
        Assert.Equal(TradePhase.Cancelled, t.Ctx.World.Trade.Phase);
    }

    [Fact]
    public void Team_asks_are_claimed_by_one_helper_and_expire()
    {
        var board = new TeamBoard { TtlMs = 1000 };
        board.Ask("Mage", "sp_potions", 6, "Rou", 10, 20, 0);
        Assert.Null(board.Next("Mage", "sp_potions", 100, 0));             // never our own ask
        Assert.Equal("Mage", board.Next("Fighter", "sp_potions", 100, 0)!.Name);
        Assert.Null(board.Next("Fighter", "cen", 100, 0));
        Assert.Null(board.Next("Fighter", "sp_potions", 5, 0));            // more than we can spare
        Assert.True(board.Claim("Mage", "Fighter"));
        Assert.False(board.Claim("Mage", "Archer"));
        Assert.Null(board.Next("Archer", "sp_potions", 100, 0));
        board.Ask("Mage", "sp_potions", 4, "Rou", 30, 20, 500);              // a refresh keeps the claim
        Assert.Equal(("Fighter", 30.0), (board.Get("Mage", 500)!.Claimer, board.Get("Mage", 500)!.X));
        board.Release("Mage", "Archer");                                     // only the claimer can give it back
        Assert.Equal("Fighter", board.Get("Mage", 500)!.Claimer);
        board.Release("Mage", "Fighter");
        Assert.True(board.Claim("Mage", "Archer"));
        Assert.Null(board.Get("Mage", 2000));                                // expired
    }

    [Fact]
    public void A_party_battle_zone_is_learned_and_routed_around()
    {
        Assert.Equal((InboundKind.PvpZone, 1L, 1L), (One(0x1818).Kind, One(0x1818).A, One(0x1818).B));   // captured: LINKOTHER to map 67, then 0x1818 {}
        Assert.Equal((0L, 2L), (One(0x1813).A, One(0x1813).B));
        using var t = new TestBot();
        t.Ctx.Learners.Add(new AtlasLearner());
        t.Ev(InboundKind.SelfBase, a: 10, b: 0, c: 100, text: "TestMap");
        t.Tick(2);
        t.Ev(InboundKind.PvpZone, a: 1, b: 1);
        t.Tick();
        Assert.True(t.Ctx.Refused(7));
    }
}
