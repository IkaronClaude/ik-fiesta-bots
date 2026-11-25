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
        a.LearnGate(cos2, 17607, 13887, rou);
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
}
