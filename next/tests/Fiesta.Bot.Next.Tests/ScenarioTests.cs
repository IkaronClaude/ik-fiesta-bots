using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Instance scenarios: frames from the job-change capture, the client's .aid areas.</summary>
public class ScenarioTests
{
    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    [Fact]
    public void Scenario_frames_from_a_capture_are_decoded_into_the_map_state()
    {
        var t = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        var sink = new Sink();
        t.Dispatch(0x1C0F, Hex("9f50 3704 32080000 db0c0000 87 00 446f6f72303200000000000000000000000000000000000000000000000000 e803"), 0, sink);
        t.Dispatch(0x6C09, Hex("9f50 01"), 0, sink);
        t.Dispatch(0x6C05, Hex("5a6f6e655f4d6f62303100000000000000000000000000000000000000000000"), 0, sink);
        var door = sink.E[0];
        Assert.Equal((1000L, 0x509F, 0x0437L, 2098u, 3291u, 0L, "Door02"), (door.A, door.Handle, door.C, door.X, door.Y, door.B, door.Text));
        Assert.Equal((9L, 0x509F, 1L), (sink.E[1].A, sink.E[1].Handle, sink.E[1].B));
        Assert.Equal((5L, "Zone_Mob01"), (sink.E[2].A, sink.E[2].Text));
        using var bot = new TestBot();
        bot.Tick(2);
        foreach (var e in sink.E) bot.In.TryWrite(e);
        bot.Tick();
        var s = bot.Ctx.World.Scenario;
        Assert.True(s.Active);
        Assert.Equal((1, "Door02"), (s.Doors[0x509F].State, s.Doors[0x509F].Block));
        Assert.Equal("Zone_Mob01", s.Asked[0].Area);
    }

    [Fact]
    public void Aid_areas_are_read_from_the_client_format()
    {
        // two areas: a circle {x 8751, y 9883, r 857} and a box {x 3404, y 6403, 4679 x 3142}
        var b = new List<byte>(BitConverter.GetBytes(2u));
        void Name(string n) { var a = new byte[32]; System.Text.Encoding.ASCII.GetBytes(n).CopyTo(a, 0); b.AddRange(a); }
        Name("GateTimeArea"); b.AddRange(BitConverter.GetBytes(0u)); foreach (var f in new[] { 8751f, 9883f, 857f }) b.AddRange(BitConverter.GetBytes(f));
        Name("FirstRoom"); b.AddRange(BitConverter.GetBytes(1u)); foreach (var f in new[] { 3404f, 6403f, 4679f, 3142f, 0f }) b.AddRange(BitConverter.GetBytes(f));
        var areas = ScenarioAreaFile.Parse(b.ToArray());
        Assert.Equal(2, areas.Count);
        Assert.Equal(new ScenarioArea("GateTimeArea", 8751, 9883, 857, 857), areas[0]);
        Assert.True(areas[1].Contains(3404 + 4000, 6403 - 3000));
        Assert.False(areas[1].Contains(3404 + 4700, 6403));
    }
}
