using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

public class AtlasTests
{
    private const string Res = "Z:/Client2026patched_10_6_6/ressystem";

    private static (WorldAtlas A, ClientData D)? Load()
    {
        if (!Directory.Exists(Res)) return null;   // client data is BYO
        var d = new ClientData(Res);
        return (new WorldAtlas(Res, n => d.Map(n)?.Id ?? -1, d.Maps.Select(m => (m.Id, (double)m.RegenX, (double)m.RegenY))), d);
    }

    [Fact]
    public void Routes_cross_maps_through_the_client_gate_graph_and_learned_gates()
    {
        if (Load() is not var (a, d)) return;
        Assert.True(a.Links > 100);
        int rou = d.Map("Rou")!.Id, cos1 = d.Map("RouCos01")!.Id, val1 = d.Map("RouVal01")!.Id, cos2 = d.Map("RouCos02")!.Id;
        // the client graph links the new Roumen, not the old one the server runs: Roumen's own gates are learned when seen
        // (the client data puts a town portal on old Roumen; one missing in the world is dropped - here, to test the gates)
        // the portal NPC type is found from where NPCs stand beside the portal spots
        Assert.Equal("Gate_Town", d.Mob(a.PortalNpc)?.InxName);
        var viaPortal = a.Route(rou, 9456, 2408, val1)!;
        Assert.True(viaPortal.Hops[0].Portal >= 0);
        Assert.True(a.PortalMissing(rou));
        Assert.Null(a.Route(rou, 9456, 2408, val1));
        Assert.NotEmpty(a.GatePoints(rou));
        Assert.True(a.LearnGate(rou, 4021, 2879, cos1));
        Assert.False(a.LearnGate(rou, 4030, 2890, cos1));   // the same gate again
        var r = a.Route(rou, 9456, 2408, val1)!;
        Assert.NotNull(r);
        Assert.Equal(rou, r.Hops[0].From);
        Assert.Equal(cos1, r.Hops[0].To);
        Assert.Equal(val1, r.Hops[^1].To);
        for (var i = 1; i < r.Hops.Count; i++) Assert.Equal(r.Hops[i - 1].To, r.Hops[i].From);
        // routing around a map: RouCos01 avoided leaves no way from Roumen to RouVal01 (its only learned gate leads there)
        Assert.Null(a.Route(rou, 9456, 2408, val1, avoid: m => m == cos1));
        Assert.True(a.EasiestMobLevel(cos1, id => d.Mob(id) is { IsNpc: false, MaxHp: > 0 } mi ? mi.Level : -1) > 0);
        a.LearnGate(cos2, 17607, 13887, rou);
        // standing in a dangerous map, the way out is still found (the danger test skips the map we are on)
        Assert.NotNull(a.Route(cos2, 16000, 13000, rou, dangerous: _ => true));
        var back = a.Route(cos2, 16000, 13000, rou)!;
        Assert.Single(back.Hops);
        Assert.True(Math.Abs(back.Hops[0].X - 17607) < 400 && Math.Abs(back.Hops[0].Y - 13887) < 400, $"{back.Hops[0]}");
    }

    [Fact]
    public void Npc_spots_and_spawn_areas_come_from_the_client()
    {
        if (Load() is not var (a, d)) return;
        var pey = Assert.Single(a.NpcSpots(93));   // the item merchant in Roumen
        Assert.Equal(d.Map("Rou")!.Id, pey.MapId);
        Assert.NotEmpty(a.SpawnAreas(1));           // MushRoom
    }

    [Fact]
    public void Mount_items_are_the_ones_the_client_mount_tables_name()
    {
        if (Load() is not var (_, d)) return;
        Assert.True(d.IsMountItem(38043));    // Wooden Horse (Pey's shop): MoverItem names WoodenHorse01
        Assert.False(d.IsMountItem(2522));    // Floatation Stone
        Assert.False(d.IsMountItem(4720));    // a skill book
    }

    [Fact]
    public void One_search_gives_the_same_route_cost_to_every_map_as_one_search_per_map()
    {
        if (Load() is not var (a, d)) return;
        var from = d.Map("RouN")!.Id;
        var costs = new Dictionary<int, double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a.RouteCosts(from, d.Map("RouN")!.RegenX, d.Map("RouN")!.RegenY, costs);
        var oneMs = sw.Elapsed.TotalMilliseconds;
        var checkedN = 0;
        foreach (var m in d.Maps)
        {
            if (m.Id == from) continue;
            var r = a.Route(from, d.Map("RouN")!.RegenX, d.Map("RouN")!.RegenY, m.Id);
            if (r is null) { Assert.False(costs.ContainsKey(m.Id), m.Name); continue; }
            Assert.True(costs.TryGetValue(m.Id, out var c), m.Name);
            Assert.Equal(r.Cost, c, 3);
            checkedN++;
        }
        Assert.True(checkedN > 20, $"{checkedN} maps reachable");
        Assert.True(oneMs < 50, $"{oneMs} ms");
    }

    [Fact]
    public void Walk_danger_by_level_gives_the_same_costs_as_the_danger_test()
    {
        if (Load() is not var (a, d)) return;
        int Hostile(int id) => d.Mob(id) is { IsNpc: false, IsPlayerSide: false, MaxHp: > 0 } m ? m.Level : -1;
        var from = d.Map("RouN")!;
        foreach (var lv in new[] { 5, 15, 30 })
        {
            var byTest = new Dictionary<int, double>(); var byLevel = new Dictionary<int, double>();
            a.RouteCosts(from.Id, from.RegenX, from.RegenY, byTest, null, s => Hostile(s.MobId) > lv);
            a.RouteCostsBelow(from.Id, from.RegenX, from.RegenY, byLevel, null, lv, Hostile);
            Assert.Equal(byTest.Count, byLevel.Count);
            foreach (var (m, c) in byTest) Assert.Equal(c, byLevel[m], 3);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a.RouteCostsBelow(from.Id, from.RegenX, from.RegenY, new Dictionary<int, double>(), null, 20, Hostile);
        Assert.True(sw.Elapsed.TotalMilliseconds < 20, $"{sw.Elapsed.TotalMilliseconds} ms with the walk levels known");
    }

    [Fact]
    public void A_gate_seen_in_the_world_overrides_a_wrong_table_link_from_its_spot()
    {
        // live 23:45: RouCos03's gate at (7494,15671) leads to Rou (its menu and its name say so), the link table sends
        // it to RouN, and routes to RouN kept walking to a gate that never leads there
        if (Load() is not var (a, d)) return;
        int cos = d.Map("RouCos03")!.Id, rou = d.Map("Rou")!.Id, roun = d.Map("RouN")!.Id;
        var corrected = 0;
        a.GateCorrected += (_, _, _, _, n) => corrected += n;
        a.LearnGate(cos, 7494, 15671, rou);
        Assert.True(corrected > 0, "no table link from that spot to another map");
        var costs = new Dictionary<int, double>();
        a.RouteCosts(cos, 7494, 15600, costs);
        Assert.True(costs.ContainsKey(rou));
        // RouN, if reachable at all, is no longer one hop through that gate
        var route = a.Route(cos, 7494, 15600, roun);
        Assert.True(route is null || route.Hops.Count == 0 || !(route.Hops[0].From == cos && Math.Abs(route.Hops[0].X - 7494) < 300 && Math.Abs(route.Hops[0].Y - 15671) < 300 && route.Hops[0].To == roun),
            "still routed to RouN through the Rou gate");
    }
}
