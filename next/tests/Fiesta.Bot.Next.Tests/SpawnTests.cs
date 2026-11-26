using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Spawn areas from the client's MobCoordinate (runs when the client data is on this machine).</summary>
public class SpawnTests
{
    [Fact]
    public void Circular_spawn_rows_are_spawn_areas_not_npc_points()
    {
        const string Res = "Z:/Client2026patched_10_6_6/ressystem";
        if (!File.Exists(Path.Combine(Res, "MobCoordinate.shn"))) return;
        var c = new ClientData(Res);
        var atlas = new WorldAtlas(Res, n => c.Map(n)?.Id ?? -1, c.Maps.Select(m => (m.Id, (double)m.RegenX, (double)m.RegenY)));
        // the Angry Wolf (quest 18) spawns only in three circles of radius 50 on RouCos01 (no width, no height)
        var wolf = atlas.SpawnAreas(2003);
        Assert.Equal(3, wolf.Count);
        Assert.All(wolf, a => Assert.Equal(50, a.HalfW));
    }

    [Fact]
    public void Survey_respawn_gaps_come_from_spawns_and_drops_name_their_mob()
    {
        var sv = new SurveyStore(null);
        sv.MobDied("M", 10, 1000, 1000, 10_000);
        sv.SawMob("M", 10, 5, 1050, 1000, 20_000);                     // came into view near it: not a respawn
        Assert.Empty(sv.Of("M").Respawns);
        sv.SawMob("M", 10, 5, 1100, 1000, 31_000, spawned: true);      // spawned near the death 21 s later
        Assert.Equal(21_000, Assert.Single(sv.Of("M").Respawns[10]));
        Assert.Equal(1100, sv.ObservedArea("M", 10)!.Value.X);         // the spawn point, not the passing sighting
        sv.SawDrop("M", 4242, byMob: 10);
        var d = Assert.Single(sv.DroppersOf(4242));
        Assert.Equal((10, 1, 1), (d.MobId, d.Drops, d.Deaths));
    }
}
