using System.Buffers.Binary;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Personal storage (window, moves) and potions (item use effects).</summary>
public class StorageAndPotionTests
{
    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    // canonical MENU_OPENSTORAGE_CMD with two records {datasize, location, itemid, lot}
    private static byte[] StorageFrame()
    {
        var p = new List<byte>();
        var head = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(head, 1234); head[8] = 2; head[9] = 0; head[10] = 0; head[11] = 2;
        p.AddRange(head);
        void Rec(int cell, int item, byte lot)
        {
            var loc = (6 << 10) | cell;
            p.Add(5); p.Add((byte)loc); p.Add((byte)(loc >> 8)); p.Add((byte)item); p.Add((byte)(item >> 8)); p.Add(lot);
        }
        Rec(0, 3005, 7); Rec(37, 250, 1);
        return p.ToArray();
    }

    [Fact]
    public void Storage_window_and_move_answers_are_decoded()
    {
        var sink = new Sink();
        var t = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        t.Dispatch(0x3C08, StorageFrame(), 0, sink);
        var head = Assert.Single(sink.E, e => e.Kind == InboundKind.StorageList);
        Assert.Equal(1234, head.A); Assert.Equal(2, head.B); Assert.Equal(2u, head.X);
        var cells = sink.E.Where(e => e.Kind == InboundKind.ItemCell).ToList();
        // (the box is cleared once per window by WorldState, not per page frame)
        Assert.Equal((6L, 0L, 3005L, 7u), (cells[0].A, cells[0].B, cells[0].C, cells[0].X));
        Assert.Equal((6L, 37L, 250L), (cells[1].A, cells[1].B, cells[1].C));
        t.Dispatch(0x300C, new byte[] { 0x4A, 0x02 }, 0, sink);
        Assert.Equal(0x024A, Assert.Single(sink.E, e => e.Kind == InboundKind.RelocAck).A);
        t.Dispatch(0x3C07, new byte[] { 0x01, 0x02 }, 0, sink);
        Assert.Equal(0x0201, Assert.Single(sink.E, e => e.Kind == InboundKind.StorageFail).A);
        Assert.Equal(16, OpenShopProcedure.RoleOfShop(0x3C08));
    }

    private static int Keeper(TestBot t)
    {
        t.Tick(5);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)EntityKind.Npc, b: 1, x: 5020, y: 5000);
        t.Tick();
        return t.Ctx.Procedures.Start(new StorageProcedure(3000), ActionKind.Storage, 1, t.Ctx).Value;
    }

    [Fact]
    public void Storage_opens_through_the_keepers_menu_and_a_move_resolves_on_the_bag_cell()
    {
        using var t = new TestBot();
        var a = Keeper(t);
        t.Ev(InboundKind.NpcMenu, a: 1);
        t.Tick();
        Assert.Contains(IntentEncoder.OpNpcMenuAck, t.Sent);
        t.Ev(InboundKind.StorageList, a: 50, b: 2, x: 0);
        t.Ev(InboundKind.ItemCell, a: 6, b: -1, c: -1);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(new ActionId(a)).State);
        Assert.True(t.Ctx.World.Bag.StorageOpenAt >= 0);
        Assert.Equal(16, t.Ctx.Knowledge.NpcRole(1));
        // deposit bag 4 -> cell 0: done when the bag cell changes
        t.Ev(InboundKind.ItemCell, a: 9, b: 4, c: 250, x: 1);
        t.Tick();
        var from = (9 << 10) | 4; var to = 6 << 10;
        var mv = t.Ctx.Actions.Issue(ActionKind.StorageMove, 1, from, to, [IntentEncoder.Reloc((ushort)from, (ushort)to, -1)]);
        t.Tick();
        Assert.Contains(IntentEncoder.OpReloc, t.Sent);
        t.Ev(InboundKind.RelocAck, a: 0x0241);
        t.Ev(InboundKind.ItemCell, a: 9, b: 4, c: -1);
        t.Ev(InboundKind.ItemCell, a: 6, b: 0, c: 250, x: 1);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(mv).State);
        Assert.Equal(250, t.Ctx.World.Bag.StoreAt(0).ItemId);
        Assert.Equal(-1, t.Ctx.World.Bag.At(4).ItemId);
        // a refused move resolves with the server's code
        t.Ev(InboundKind.ItemCell, a: 9, b: 5, c: 251, x: 1);
        t.Tick();
        var from2 = (9 << 10) | 5;
        var mv2 = t.Ctx.Actions.Issue(ActionKind.StorageMove, 1, from2, to, [IntentEncoder.Reloc((ushort)from2, (ushort)to, -1)]);
        t.Tick();
        t.Ev(InboundKind.RelocAck, a: 0x024A);
        t.Tick();
        Assert.Equal((ActionState.Refused, (ushort)0x024A), (t.Ctx.Actions.Get(mv2).State, t.Ctx.Actions.Get(mv2).Code));
        // another NPC window closes the storage
        t.Ctx.Procedures.Start(new OpenShopProcedure(3000), ActionKind.OpenShop, 1, t.Ctx);
        Assert.True(t.Ctx.World.Bag.StorageOpenAt < 0);
    }

    [Fact]
    public void A_keeper_that_opens_a_shop_fails_the_storage_procedure()
    {
        using var t = new TestBot();
        var a = Keeper(t);
        t.Ev(InboundKind.ShopList, h: 3000, a: 0x3C06);
        t.Tick();
        Assert.Equal(ActionState.Failed, t.Ctx.Actions.Get(new ActionId(a)).State);
    }

    private static ItemInfoRow Potion(int id, string inx, int effect, int value, int level = 1) =>
        new(id, inx, inx, 1, 0, 0, level, 0, 5, 100, 50, 0, "UsePotion") { UseEffect = effect, UseValue = value };

    private const string ProbeBrain = """
brain: p
api: 3
modules: [brain/p/m]
layers:
  - name: l
    priority: 1
    machine:
      initial: s
      states:
        s:
          tick: m.t
          terminal: true
          transitions: [ { to: s, after: 99999999 } ]
""";

    private const string ProbeModule = """
local M = {}
function M.t()
  bb.state.small = items.bestPotion(200, 0)
  bb.state.big = items.bestPotion(5000, 0)
  bb.state.sp = items.bestPotion(10, 1)
  bb.state.hpN = items.potions(0)
  bb.state.spN = items.potions(1)
  local k, v = items.useEffect(3505)
  bb.state.k, bb.state.v = k, v
  bb.state.scroll = items.isPotion(3710, 0)
end
return M
""";

    [Fact]
    public void Potions_are_chosen_by_their_use_effect()
    {
        var data = new TestData();
        data.Items[3500] = Potion(3500, "BestLowHpPotion", 0, 250);
        data.Items[3501] = Potion(3501, "LowHpPotion", 0, 650);
        data.Items[3505] = Potion(3505, "BestLowSpPotion", 1, 230);
        data.Items[3509] = Potion(3509, "HighHpPotion", 0, 2400, level: 60);     // above our level
        data.Items[3710] = new ItemInfoRow(3710, "BestLowMRScroll", "scroll", 1, 0, 0, 1, 0, 1, 1, 50, 0, "UseScroll");
        using var t = new TestBot(data);
        t.Ev(InboundKind.ItemCell, a: 9, b: 0, c: 3500, x: 3);
        t.Ev(InboundKind.ItemCell, a: 9, b: 1, c: 3501, x: 2);
        t.Ev(InboundKind.ItemCell, a: 9, b: 2, c: 3505, x: 4);
        t.Ev(InboundKind.ItemCell, a: 9, b: 3, c: 3509, x: 9);
        t.Ev(InboundKind.ItemCell, a: 9, b: 4, c: 3710, x: 9);
        var b = t.Load(ProbeBrain, new() { ["brain/p/m"] = ProbeModule });
        t.Tick(3);
        var s = b.BlackboardJson()["state"]!["l"]!;
        Assert.Equal(0, s["small"]!.GetValue<long>());   // 250 covers 200: the smallest that covers
        Assert.Equal(1, s["big"]!.GetValue<long>());     // nothing covers 5000: the largest we may drink (not the level-60 one)
        Assert.Equal(2, s["sp"]!.GetValue<long>());
        Assert.Equal(5, s["hpN"]!.GetValue<long>());
        Assert.Equal(4, s["spN"]!.GetValue<long>());
        Assert.Equal((1L, 230L), (s["k"]!.GetValue<long>(), s["v"]!.GetValue<long>()));
        Assert.False(s["scroll"]!.GetValue<bool>());
    }

    [Fact]
    public void A_split_resolves_on_its_ack_after_the_two_cell_changes()
    {
        // captured: SPLIT_REQ {0x2402, 0x2415, 15} -> CELLCHANGE slot 2 lot 10, CELLCHANGE slot 21 lot 15 -> SPLIT_ACK 0x0441
        var enc = IntentEncoder.Split(0x2402, 0x2415, 15, -1);
        Assert.Equal(IntentEncoder.OpSplit, enc.Opcode);
        Assert.Equal(new byte[] { 0x02, 0x24, 0x15, 0x24, 0x0F, 0, 0, 0 }, enc.Inline[..enc.Length].ToArray());
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016)).Dispatch(0x300E, new byte[] { 0x41, 0x04 }, 0, sink);
        Assert.Equal((InboundKind.SplitAck, 0x0441L), (sink.E[0].Kind, sink.E[0].A));
        using var t = new TestBot();
        t.Ev(InboundKind.ItemCell, a: 9, b: 2, c: 3005, x: 25);
        t.Tick(2);
        var a = t.Ctx.Actions.Issue(ActionKind.Split, 1, 2, 21, [enc]);
        t.Tick();
        t.Ev(InboundKind.ItemCell, a: 9, b: 2, c: 3005, x: 10);
        t.Ev(InboundKind.ItemCell, a: 9, b: 21, c: 3005, x: 15);
        t.Ev(InboundKind.SplitAck, a: 0x0441);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
        Assert.Equal((3005, 15), t.Ctx.World.Bag.At(21));
        Assert.Equal(25, t.Ctx.World.Bag.Count(3005));
    }

    [Fact]
    public void A_storage_window_sent_page_by_page_keeps_every_page()
    {
        // Z:/Storage.pcapng: the window opens with one OPENSTORAGE frame per page, pages 3, 0, 1, 2 back to back. Page 0
        // here carries two stored items (cells 0 and 1); the empty pages after it must not wipe them (NextA, 21:58: the
        // bot saw occupied cells as free and its deposits swapped stored items back into bag slot 22, seven times)
        byte[] Page(byte page, params byte[] records)
        {
            var count = (byte)(records.Length / 6);
            return new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0x10, page, 0, count }.Concat(records).ToArray();
        }
        var sink = new Sink();
        var table = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        table.Dispatch(0x3C08, Page(3), 0, sink);
        table.Dispatch(0x3C08, Page(0, 5, 0x00, 0x18, 0x10, 0xA4, 1, 5, 0x01, 0x18, 0x11, 0xA4, 1), 0, sink);
        table.Dispatch(0x3C08, Page(1), 0, sink);
        table.Dispatch(0x3C08, Page(2), 0, sink);
        using var t = new TestBot();
        t.Tick(2);
        foreach (var e in sink.E) t.In.TryWrite(e);
        t.Tick();
        Assert.Equal(42000, t.Ctx.World.Bag.StoreAt(0).ItemId);
        Assert.Equal(42001, t.Ctx.World.Bag.StoreAt(1).ItemId);
        Assert.Equal(2, new Fiesta.Bot.Api.NpcModule(t.Ctx).StorageFree(0));
        // a window opened again later starts from a clean box
        t.Tick(60);
        t.In.TryWrite(sink.E[0]);
        t.Tick();
        Assert.Equal(-1, t.Ctx.World.Bag.StoreAt(0).ItemId);
    }

    [Fact]
    public void A_2026_storage_page_lists_its_items()
    {
        // live 00:09:56, NextA at the Eld keeper: page 0 of 16 with nine stored items in cells 0-8 (the 2026 page carries
        // its count as a u32 at 12, records from 16, 3 trailing bytes); our view read it as empty and the next deposit
        // swapped a Long Sword out of cell 0
        var frame = Convert.FromHexString("00000000000000001000001609000000460018FB0000000000000000FFFF00000000FFFF00000000FFFF00000000FFFF000000000000000000000000000000000000000000FFFFFFFFFFFFFFFFFF0000000000000000012E011810A400000000000000000000000001000000000000000000000000000000000000000000000000000303010012021802000000000000000000000000000001120318C8000000000000000000000000000001460418FA0000000000000000FFFF00000000FFFF00000000FFFF00000000FFFF000000000000000000000000000000000000000000FFFFFFFFFFFFFFFFFF00000000000000000012051803000000000000000000000000000001490618BC3400000000000000FFFF00000000FFFF00000000FFFF000000000000000000000000000000000000000000000000000000FFFFFFFFFFFFFFFFFF0100000000000000030203003107183408000000000000000000000000010000000000000000000000000000000000000000000000000005040100020100050818204E00000000");
        var wire = WireProfile.For(WireKind.Wire2026);
        const string Res = "Z:/Client2026patched_10_6_6/ressystem";
        if (!Directory.Exists(Res)) return;   // the item classes come from the client's tables (BYO)
        var data = new Fiesta.Bot.Services.ClientData(Res);
        wire.ItemClassOf = data.ItemClass;
        var sink = new Sink();
        DecoderTable.CreateDefault(wire).Dispatch(0x3C08, frame, 0, sink);   // the table brings the 2026 shape to the canonical one
        var cells = sink.E.Where(e => e.Kind == InboundKind.ItemCell).Select(e => (e.A, e.B, e.C)).ToList();
        Assert.Equal(9, cells.Count);
        Assert.Equal((6L, 0L, 251L), cells[0]);
        Assert.Equal((6L, 1L, 42000L), cells[1]);
        Assert.Equal((6L, 8L, 20000L), cells[8]);
    }
}
