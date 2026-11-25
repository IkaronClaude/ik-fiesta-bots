using System.Text.Json;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Server;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

public class GameViewTests
{
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

    [Fact]
    public void Snapshot_then_small_deltas()
    {
        var (t, s, got) = Setup();
        t.Tick(3);
        s.Pump(0, null);
        var snap = Assert.Single(got);
        Assert.Equal("snap", snap.GetProperty("t").GetString());
        Assert.Equal(1, snap.GetProperty("seq").GetInt64());
        Assert.Equal(100, snap.GetProperty("data").GetProperty("self").GetProperty("hp").GetInt64());

        // nothing changed: nothing sent
        t.Tick(3); s.Pump(0, null);
        Assert.Single(got);

        // HP drop and a mob appearing: one delta with just those ops
        t.Ev(InboundKind.SelfHp, a: 60);
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5100, y: 5000);
        t.Tick(3); s.Pump(0, null);
        var d = got[^1];
        Assert.Equal("d", d.GetProperty("t").GetString());
        Assert.Equal(2, d.GetProperty("seq").GetInt64());
        var ops = d.GetProperty("ops").EnumerateArray().Select(o => o[0].GetString()).ToList();
        Assert.Contains("self", ops); Assert.Contains("e+", ops);
        var self = d.GetProperty("ops").EnumerateArray().First(o => o[0].GetString() == "self")[1];
        Assert.Equal(60, self.GetProperty("hp").GetInt64());
        Assert.False(self.TryGetProperty("maxHp", out _));

        // the mob leaves
        t.Ev(InboundKind.EntityLeft, h: 2000);
        t.Tick(3); s.Pump(0, null);
        Assert.Contains(got[^1].GetProperty("ops").EnumerateArray(), o => o[0].GetString() == "e-" && o[1].GetInt32() == 2000);
    }

    [Fact]
    public void Capture_does_not_allocate_on_the_tick()
    {
        var (t, s, _) = Setup();
        for (var i = 0; i < 20; i++) t.Ev(InboundKind.EntityAppeared, h: 2000 + i, a: (long)EntityKind.Mob, b: 1, x: (uint)(5100 + i), y: 5000);
        t.Tick(10); s.Pump(0, null);
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) t.Agent.Game!.MaybeCapture(t.Ctx);   // interval-gated; force fills below
        var tap = t.Agent.Game!; tap.IntervalMs = 0;
        for (var i = 0; i < 20; i++) tap.MaybeCapture(t.Ctx);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - a0);
        // cost of one capture with 100 entities in view
        for (var i = 20; i < 100; i++) t.Ev(InboundKind.EntityAppeared, h: 2000 + i, a: (long)EntityKind.Mob, b: 1, x: (uint)(5100 + i), y: 5000);
        t.Tick();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++) tap.MaybeCapture(t.Ctx);
        var us = sw.Elapsed.TotalMicroseconds / 2000;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gameview-capture-us.txt"), us.ToString("0.0"));
        Assert.True(us < 100, $"capture {us:0.0} us");
    }

    [Fact]
    public void A_viewer_joining_later_gets_a_snapshot()
    {
        var (t, s, got) = Setup();
        t.Tick(3); s.Pump(0, null);
        var late = new List<string>();
        s.Add(new GameViewer(m => { late.Add(m); return true; }));
        s.Pump(0, null);
        Assert.Single(late);
        Assert.Contains("\"t\":\"snap\"", late[0]);
        Assert.Single(got);   // the first viewer got nothing new
    }

    [Fact]
    public void Off_is_sent_only_when_the_bot_really_leaves_the_world()
    {
        var (t, s, got) = Setup();
        // subscribed before the first capture while the bot is in the world: no "off" flash, just the snapshot
        s.Pump(0, null);
        Assert.Empty(got);
        t.Tick(3); s.Pump(0, null);
        Assert.Equal(["snap"], got.Select(m => m.GetProperty("t").GetString()));

        // a zone handoff (link down while the supervisor still counts the bot in the world): no "off"
        t.Ev(InboundKind.LinkChanged, a: (long)Fiesta.Bot.Net.LinkState.Connecting);
        t.Tick(3); s.Pump(0, null);
        Assert.DoesNotContain(got, m => m.GetProperty("t").GetString() == "off");

        // disconnected: exactly one "off" with the reason, not repeated on later pumps
        s.Pump(0, "disconnected"); s.Pump(0, "disconnected"); s.Pump(0, "disconnected");
        var offs = got.Where(m => m.GetProperty("t").GetString() == "off").ToList();
        Assert.Single(offs);
        Assert.Equal("disconnected", offs[0].GetProperty("reason").GetString());

        // back in the world: a fresh snapshot, seq still contiguous
        t.Ev(InboundKind.LinkChanged, a: (long)Fiesta.Bot.Net.LinkState.Ready);
        t.Ev(InboundKind.MapLoginAck, h: 1000, x: 5000, y: 5000, a: 100, b: 50);
        t.Tick(3); s.Pump(0, null);
        Assert.Equal("snap", got[^1].GetProperty("t").GetString());
        var seqs = got.Select(m => m.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(x => (long)x), seqs);

        // the host stopping
        s.HostStopping(0);
        Assert.Equal("host-stopping", got[^1].GetProperty("reason").GetString());
    }
}
