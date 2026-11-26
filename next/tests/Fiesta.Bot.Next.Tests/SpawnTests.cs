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
}
