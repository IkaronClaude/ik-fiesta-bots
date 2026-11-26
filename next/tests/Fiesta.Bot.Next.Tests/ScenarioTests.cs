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

    // one door "Door02" over tiles x 8..23, y 4..5: state 0 blocks every tile, state 1 blocks nothing
    private static byte[] SbiFile()
    {
        var b = new List<byte>(BitConverter.GetBytes(1u));
        var name = new byte[32]; "Door02"u8.ToArray().CopyTo(name, 0); b.AddRange(name);
        foreach (var v in new uint[] { 8, 4, 23, 5, 4, 0 }) b.AddRange(BitConverter.GetBytes(v));
        b.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });   // state 0: 2 bytes x 2 rows
        b.AddRange(new byte[] { 0, 0, 0, 0 });               // state 1
        return b.ToArray();
    }

    [Fact]
    public void Door_files_are_read_and_a_closed_door_blocks_its_tiles_in_the_paths()
    {
        var doors = MapDoorFile.Parse(SbiFile());
        var d = Assert.Single(doors);
        Assert.Equal(("Door02", 8, 4, 23, 5, 2), (d.Name, d.X0, d.Y0, d.X1, d.Y1, d.RowBytes));
        Assert.True(d.Blocked(0, 15, 5));
        Assert.False(d.Blocked(1, 15, 5));
        Assert.False(d.Blocked(0, 24, 5));
        var dir = Path.Combine(Path.GetTempPath(), "resmap", "field", "TestMap"); Directory.CreateDirectory(dir);   // the client layout: resmap/<kind>/<folder>/<folder>.sbi
            var file = Path.Combine(dir, "TestMap.sbi");
        File.WriteAllBytes(file, SbiFile());
        try
        {
            using var t = new TestBot();
            t.Ctx.Learners.Add(new DoorLearner());
            t.Ev(InboundKind.SelfBase, a: 10, b: 0, c: 100, text: "TestMap");
            t.Tick(2);
            void Door(long a, long state, string? block)
            {
                var e = InboundEvent.Of(InboundKind.Scenario, 0, t.Clock.NowMs); e.A = a; e.Handle = 0x509F; e.B = state; e.Text = block;
                t.In.TryWrite(e);
                t.Tick();
            }
            Door(1000, 0, "Door02");   // BUILDDOOR, closed
            Assert.True(t.Ctx.Overlay.Blocked(10, 4, t.Clock.NowMs));
            Assert.Equal(32, t.Ctx.Overlay.Count);
            Door(9, 1, null);          // DOORSTATE open
            Assert.False(t.Ctx.Overlay.Blocked(10, 4, t.Clock.NowMs));
            Assert.Equal(0, t.Ctx.Overlay.Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_path_goes_around_a_closed_door_and_through_it_when_open()
    {
        // a wall at x = 30 with two gaps: a near one (y 10..13) and a far one (y 50..63); the door closes the near one
        var layer = BlockLayer.FromBits("t", 64, 64, (x, y) => x == 30 && !(y is >= 10 and <= 13) && y < 50);
        var mesh = NavMesh.Build(layer);
        bool Door(int x, int y) => x is >= 29 and <= 31 && y is >= 10 and <= 13;
        var open = PathSearch.Find(mesh, 10 * 6.25, 11 * 6.25, 50 * 6.25, 11 * 6.25);
        var closed = PathSearch.Find(mesh, 10 * 6.25, 11 * 6.25, 50 * 6.25, 11 * 6.25, Door);
        Assert.Equal(PathStatus.Ready, open.Status);
        Assert.DoesNotContain(open.Waypoints, p => p.Y / 6.25 >= 49);    // straight through the near gap
        Assert.Equal(PathStatus.Ready, closed.Status);
        Assert.Contains(closed.Waypoints, p => p.Y / 6.25 >= 49);        // around by the far gap
    }
}
