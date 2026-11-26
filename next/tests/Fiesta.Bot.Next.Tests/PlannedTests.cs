using System.Buffers.Binary;
using System.Diagnostics;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Fleet;
using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>A small in-memory world for agent / brain tests.</summary>
public sealed class TestBot : IDisposable
{
    public readonly ManualClock Clock = new() { NowMs = 10_000 };
    public readonly InboundRing In = new();
    public readonly OutboundRing Out = new() { LinkUp = true };
    public readonly BotAgent Agent;
    public readonly AgentContext Ctx;
    public readonly JobScheduler Jobs = new(1);
    public readonly List<ushort> Sent = new();
    public BrainInstance? Brain;

    public TestBot(IClientData? data = null)
    {
        data ??= new TestData();
        var log = new BotLogger("t", new LogRing(1024), null, Clock);
        Ctx = new AgentContext
        {
            BotId = "t", Data = data, Nav = new NavService(Path.GetTempPath(), Jobs), Jobs = Jobs, Atlas = WorldAtlas.CreateEmpty(), Knowledge = new KnowledgeStore(), Log = log,
            Inbound = In, Actions = new ActionSystem(Out), World = new WorldState { Data = data },
        };
        Agent = new BotAgent(Ctx, In, Out, new ControlQueue(), null, Clock);
        Ev(InboundKind.LinkChanged, a: (long)LinkState.Ready);
        Ev(InboundKind.SelfBase, a: 10, b: 0, c: 100);
        Ev(InboundKind.SelfHp, a: 100);
        Ev(InboundKind.SelfSp, a: 50);
        Ev(InboundKind.MapLoginAck, h: 1000, x: 5000, y: 5000, a: 100, b: 50);
        Ev(InboundKind.SkillLearned, a: 0);
    }

    public void Ev(InboundKind k, int h = -1, int h2 = -1, long a = 0, long b = 0, long c = 0, uint x = 0, uint y = 0, string? text = null)
    {
        var e = InboundEvent.Of(k, 0, Clock.NowMs); e.Handle = h; e.Handle2 = h2; e.A = a; e.B = b; e.C = c; e.X = x; e.Y = y; e.Text = text;
        In.TryWrite(e);
    }

    public void Tick(int n = 1, int ms = 50)
    {
        for (var i = 0; i < n; i++)
        {
            Clock.NowMs += ms;
            Agent.Tick();
            while (Out.TryDequeue(out var op, out _)) Sent.Add(op);
        }
    }

    public BrainInstance Load(string yaml, Dictionary<string, string>? modules = null)
    {
        var c = BrainCompiler.Compile(BrainLoader.Parse(yaml), modules ?? new());
        Assert.True(c.Ok, string.Join("; ", c.Lint.Where(l => l.Severity == LintSeverity.Error).Select(l => $"{l.Code} {l.Message} {l.Layer}/{l.State}")));
        Brain = new BrainInstance(c, Ctx);
        Assert.Empty(Brain.LoadErrors);
        Agent.Brain = Brain;
        return Brain;
    }

    public void Dispose() { Brain?.Dispose(); Jobs.Dispose(); }
}

/// <summary>Client data stand-in: one mob (body 50), one damage skill (range 100, 1 s cooldown, 5 SP).</summary>
public sealed class TestData : IClientData
{
    public static readonly MobInfoRow M = new(1, "TestMob", "Test Mob", 10, 500, 30, 100, false, 1000, 1, 0, 1, false);
    public static readonly ActiveSkillRow S = new(0, "TestHit01", "Test Hit", 1, 100, 0, 1, 0, 1000, 0, 0, 5, 0, false, 0, 0, 0, 0, 0, 10, 20, 0, 0, "TestHit", "-", "-", "-", "-");
    public MobInfoRow? Mob(int id) => id == 1 ? M : null;
    public ActiveSkillRow? Skill(int id) => id == 0 ? S : null;
    public readonly Dictionary<int, ItemInfoRow> Items = new();
    public ItemInfoRow? Item(int id) => Items.GetValueOrDefault(id);
    public static readonly MapInfoRow MapRow = new(7, "TestMap", "Test Map", false, 5000, 5000);
    public static readonly MapInfoRow DnRow = new(8, "TestDn", "Test Dungeon", true, 1000, 1000);
    public MapInfoRow? Map(int id) => id == 7 ? MapRow : id == 8 ? DnRow : null;
    public MapInfoRow? Map(string mapName) => mapName == "TestMap" ? MapRow : mapName == "TestDn" ? DnRow : null;
    public static readonly QuestDef Q8 = new(8, "Master Zach", 2, 10, false, 88,
        [new QuestObjectiveDef(QuestObjectiveKind.Talk, 88, -1, 1), new QuestObjectiveDef(QuestObjectiveKind.Kill, 1, -1, 5)], 30, 0, 0, 0, "")
    { Rewards = [(1, 2, 2505), (2, 2, 12943), (2, 2, 12947), (1, 0, 30)] };
    public QuestDef? Quest(int id) => id == 8 ? Q8 : null;
    public string? Dialog(int id) => null;
    public int ClassFamily(int classId) => 1;
    public string ClassName(int classId) => "";
    public IReadOnlyCollection<QuestDef> Quests => [];
    public string Dir => "";
}

/// <summary>Transport: framing and the stream cipher.</summary>
public class TransportTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    public void Frame_round_trips(int len)
    {
        var payload = Enumerable.Range(0, len).Select(i => (byte)i).ToArray();
        var buf = new byte[FrameCodec.Size(len)];
        var n = FrameCodec.Write(buf, 0x2019, payload);
        Assert.Equal(buf.Length, n);
        Assert.Equal(n, FrameCodec.TryRead(buf, out var op, out var p));
        Assert.Equal(0x2019, op);
        Assert.True(p.SequenceEqual(payload));
        Assert.Equal(0, FrameCodec.TryRead(buf.AsSpan(0, n - 1), out _, out _));
    }

    [Fact]
    public void Cipher_is_its_own_inverse_from_the_same_seed()
    {
        var table = Enumerable.Range(0, 499).Select(i => (byte)(i * 7)).ToArray();
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var copy = (byte[])data.Clone();
        new XorStreamCipher(table, 300).Apply(copy);
        Assert.False(copy.SequenceEqual(data));
        new XorStreamCipher(table, 300).Apply(copy);
        Assert.True(copy.SequenceEqual(data));
    }
}

/// <summary>Wire profiles: what differs between the 2016 and 2026 packet sets.</summary>
public class WireTests
{
    private sealed class Sink : IEventSink { public readonly List<InboundEvent> E = new(); public bool TryWrite(in InboundEvent evt) { E.Add(evt); return true; } }

    [Fact]
    public void Checksum_lists_have_the_client_counts()
    {
        Assert.Equal(49, Wire2016Profile.Files.Length);
        Assert.Equal(53, Wire2026Profile.Files.Length);
        Assert.DoesNotContain("MapLinkPoint", Wire2026Profile.Files);
        var sums = WireProfile.For(WireKind.Wire2016).ComputeChecksums(Path.GetTempPath());   // no tables there: '0' slots
        Assert.All(sums, s => Assert.Equal(new string('0', 32), s));
        var body = WireProfile.For(WireKind.Wire2026).BuildMapLogin(1500, "Bot", WireProfile.For(WireKind.Wire2026).ComputeChecksums(Path.GetTempPath()));
        Assert.Equal(22 + 53 * 32, body.Length);
    }

    [Fact]
    public void Swing_damage_is_read_on_both_wires()
    {
        foreach (var kind in new[] { WireKind.Wire2016, WireKind.Wire2026 })
        {
            var w = WireProfile.For(kind);
            var (len, dmgBytes) = w.SwingDamage;
            var p = new byte[len];
            BinaryPrimitives.WriteUInt16LittleEndian(p, 7); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 9);
            if (dmgBytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(6), 70000); else BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 700);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(6 + dmgBytes), 1234);
            var sink = new Sink();
            DecoderTable.CreateDefault(w).Dispatch(0x2448, p, 0, sink);
            var e = Assert.Single(sink.E);
            Assert.Equal(InboundKind.Damage, e.Kind);
            Assert.Equal(7, e.Handle); Assert.Equal(9, e.Handle2);
            Assert.Equal(dmgBytes == 4 ? 70000 : 700, e.A);
            Assert.Equal(1234, e.B);
        }
    }

    [Fact]
    public void Soul_stone_window_carries_prices_and_shop_kinds_name_roles()
    {
        // NextA's SHOPOPENSOULSTONE (15:50:06): hp {339, max 58, price 28}, sp {179, max 20, price 12}; 6 HP stones then
        // cost 168 and 2 SP stones 24 (CENCHANGE): the unit price is the third field
        var p = Convert.FromHexString("530100003A0000001C000000B3000000140000000C000000");
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016)).Dispatch(0x3C05, p, 0, sink);
        var m = Assert.Single(sink.E, e => e.Kind == InboundKind.StoneShop);
        Assert.Equal(28, m.A); Assert.Equal(58, m.B); Assert.Equal(12, m.C); Assert.Equal(20u, m.X);
        Assert.Contains(sink.E, e => e.Kind == InboundKind.ShopList);
        Assert.Equal(2, OpenShopProcedure.RoleOfShop(0x3C05));
        Assert.Equal(4, OpenShopProcedure.RoleOfShop(0x3C0A));
        Assert.Equal(8, OpenShopProcedure.RoleOfShop(0x3C03));
        Assert.Equal(1, OpenShopProcedure.RoleOfShop(0x3C0B));
    }

    [Fact]
    public void Someone_swing_carries_rest_hp_not_damage()
    {
        // PDB layout: attacker u16, defender u16, flag u8, resthp u32 @5, order u16, index u8, sequence u8 (13 B);
        // the 2026 frame is 20 B (7 trailing bytes)
        foreach (var len in new[] { 13, 20 })
        {
            var p = new byte[len];
            BinaryPrimitives.WriteUInt16LittleEndian(p, 7); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 1000);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(5), 33);
            var sink = new Sink();
            DecoderTable.CreateDefault(WireProfile.For(len == 13 ? WireKind.Wire2016 : WireKind.Wire2026)).Dispatch(0x2449, p, 0, sink);
            var e = Assert.Single(sink.E);
            Assert.Equal(33, e.B); Assert.Equal(-1, e.A);
        }
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.Damage, h: 7, h2: 1000, a: -1, b: 60);
        t.Tick();
        Assert.Equal(60, t.Ctx.World.Self.Hp);
        Assert.Equal(40, t.Ctx.Events.Last(EventKind.Damaged)!.Value.Arg2);
    }

    [Fact]
    public void Wide_2026_char_base_is_canonicalised()
    {
        var p = new byte[362];
        p[25] = 33;                                                   // level
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(26), 5555);  // exp
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(59), 777);   // cen (one byte after the 2016 offset 58)
        "Rou"u8.CopyTo(p.AsSpan(67));
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2026)).Dispatch(0x1038, p, 0, sink);
        var b = sink.E.First(x => x.Kind == InboundKind.SelfBase);
        Assert.Equal(33, b.A); Assert.Equal(5555, b.B); Assert.Equal(777, b.C); Assert.Equal("Rou", b.Text);
    }

    [Fact]
    public void Wrong_width_is_reported_not_misread()
    {
        var w = new Wire2026Profile();
        byte[]? s = null;
        var p = new byte[100];
        var o = w.Normalize(0x1038, p, ref s);
        Assert.Equal(100, o.Length);
        Assert.Equal(1, w.Mismatches[0x1038]);
    }

    [Fact]
    public void Regen_mob_2026_row_keeps_the_head()
    {
        var p = new byte[188];
        BinaryPrimitives.WriteUInt16LittleEndian(p, 4242); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(5), 100); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), 200);
        var sink = new Sink();
        DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2026)).Dispatch(0x1C08, p, 0, sink);
        var e = Assert.Single(sink.E);
        Assert.Equal(4242, e.Handle); Assert.Equal(1, e.B); Assert.Equal(100u, e.X); Assert.Equal(200u, e.Y);
    }

    /// <summary>The 2016 path against a real 2016 capture (skipped when the capture or the decoder tool is absent):
    /// the zone's server-to-client frames of Z:/Full.pcapng decode into a map login, a character base and entities.</summary>
    [Fact]
    public void Decodes_a_2016_capture()
    {
        if (!File.Exists("Z:/Full.pcapng") || !File.Exists("C:/Projects/fiesta-proxy/tools/pcap_decode.py")) return;
        var frames = Pcap.ServerFrames("Z:/Full.pcapng", 9016);
        Assert.NotNull(frames);
        var sink = new Sink();
        var table = DecoderTable.CreateDefault(WireProfile.For(WireKind.Wire2016));
        foreach (var (op, p) in frames) table.Dispatch(op, p, 0, sink);
        Assert.Contains(sink.E, e => e.Kind == InboundKind.MapLoginAck && e.Handle > 0 && e.X > 0);
        var b = sink.E.First(e => e.Kind == InboundKind.SelfBase);
        Assert.InRange(b.A, 1, 150);
        Assert.Contains(sink.E, e => e.Kind == InboundKind.EntityAppeared);
        Assert.Contains(sink.E, e => e.Kind == InboundKind.ItemCell && e.A == 9);
        Assert.Contains(sink.E, e => e.Kind == InboundKind.SkillLearned);
    }
}

/// <summary>Server-to-client frames of a capture through fiesta-proxy's decoder (text output parsed).</summary>
public static class Pcap
{
    public static List<(ushort Op, byte[] P)>? ServerFrames(string pcap, int port)
    {
        const string tool = "C:/Projects/fiesta-proxy/tools/pcap_decode.py";
        if (!File.Exists(pcap) || !File.Exists(tool)) return null;
        var psi = new ProcessStartInfo("python", $"\"{tool}\" \"{pcap}\" --port {port} --no-struct --hex-limit 0 --hide-movement")
        { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(tool)! };
        psi.Environment["XOR_TABLE_PATH"] = "C:/Projects/ik-fiesta-bots/xor-table.hex";
        Process? pr;
        try { pr = Process.Start(psi); } catch (System.ComponentModel.Win32Exception) { return null; }
        if (pr is null) return null;
        var o = new List<(ushort, byte[])>();
        ushort op = 0; List<byte>? cur = null;
        string? line;
        while ((line = pr.StandardOutput.ReadLine()) is not null)
        {
            var t = line.TrimStart();
            if (t.StartsWith("S<- ") || t.StartsWith("C-> "))
            {
                if (cur is not null) o.Add((op, cur.ToArray()));
                cur = null;
                if (!t.StartsWith("S<- ")) continue;
                var i = t.IndexOf("[0x", StringComparison.Ordinal);
                op = Convert.ToUInt16(t.Substring(i + 3, 4), 16);
                cur = new List<byte>();
            }
            else if (cur is not null && t.Length > 6 && t[4] == ' ' && t.Length >= 6 && Uri.IsHexDigit(t[0]))
            {
                var bar = t.IndexOf('|');
                var hex = (bar > 0 ? t[6..bar] : t[6..]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var h in hex) if (h.Length == 2) cur.Add(Convert.ToByte(h, 16));
            }
        }
        if (cur is not null) o.Add((op, cur.ToArray()));
        pr.WaitForExit(60_000);
        return o.Count == 0 ? null : o;
    }
}

/// <summary>Navigation port (corridor A*, funnel, nudge, goal snap).</summary>
public class NavTests
{
    // 64 x 64 tiles, a wall at x = 30 from y = 0 to 50 (a gap at the bottom)
    private static BlockLayer Map() => BlockLayer.FromBits("t", 64, 64, (x, y) => x == 30 && y < 50);

    [Fact]
    public void Path_goes_around_the_wall_and_every_leg_is_walkable()
    {
        var layer = Map();
        var mesh = NavMesh.Build(layer);
        var r = PathSearch.Find(mesh, 10 * 6.25, 10 * 6.25, 50 * 6.25, 10 * 6.25);
        Assert.Equal(PathStatus.Ready, r.Status);
        var pts = new List<(double, double)> { (10 * 6.25, 10 * 6.25) };
        pts.AddRange(r.Waypoints.Select(p => ((double)p.X, (double)p.Y)));
        for (var i = 1; i < pts.Count; i++)
            Assert.True(GameRules.MoveSegmentAccepted(layer, pts[i - 1].Item1, pts[i - 1].Item2, pts[i].Item1, pts[i].Item2), $"leg {i} crosses the wall");
        Assert.Contains(r.Waypoints, p => p.Y / 6.25 >= 49);   // it used the gap
        Assert.True(r.Length < 2.2 * (40 + 2 * 40) * 6.25);
    }

    [Fact]
    public void Blocked_goal_is_snapped()
    {
        var mesh = NavMesh.Build(Map());
        var r = PathSearch.Find(mesh, 10 * 6.25, 10 * 6.25, 30 * 6.25 + 1, 20 * 6.25);
        Assert.Equal(PathStatus.Ready, r.Status);
        Assert.True(r.Partial);
    }

    [Fact]
    public void Reachability_follows_components()
    {
        var layer = BlockLayer.FromBits("t", 64, 64, (x, y) => x == 30);
        var mesh = NavMesh.Build(layer);
        Assert.False(PathSearch.Reachable(mesh, 10, 10, 50, 10));
        Assert.True(PathSearch.Reachable(mesh, 10, 10, 20, 40));
    }
}

/// <summary>GameRules (formulas as measured; REVIEW section 5).</summary>
public class GameRulesTests
{
    [Fact]
    public void Reach_formulas()
    {
        // the game client's rules: a mob's attackable distance is its body (+ 450 with a bow); a swing starts within that
        // + 50 - 10 and keeps within + 50; a skill on a mob reaches Range + body and is cast while that + 50 exceeds the
        // distance; on a character Range + 50; with no target Range
        Assert.Equal(50, GameRules.MobBody(TestData.M));
        Assert.Equal(50, GameRules.Attackable(50, WeaponClass.Melee));
        Assert.Equal(500, GameRules.Attackable(50, WeaponClass.Bow));
        Assert.Equal(50 + 40, GameRules.BashStartReach(50, WeaponClass.Melee));
        Assert.Equal(50 + 50, GameRules.BashReach(50, WeaponClass.Melee));
        Assert.Equal(100 + 50, GameRules.Skillable(TestData.S, 50, targetIsMob: true, hasTarget: true));
        Assert.Equal(100 + 50, GameRules.Skillable(TestData.S, 0, targetIsMob: false, hasTarget: true));
        Assert.Equal(100, GameRules.Skillable(TestData.S, 0, targetIsMob: false, hasTarget: false));
        Assert.Equal(50, GameRules.Skillable(null, 0, true, true));
        Assert.Equal(100 + 50 + 50, GameRules.SkillReach(TestData.S, 50));
    }

    [Fact]
    public void Move_fail_classification()
    {
        Assert.Equal(MoveFailKind.Desync, MoveController.Classify((1000, 1000), (1300, 1000)));
        Assert.Equal(MoveFailKind.Blocked, MoveController.Classify((1000, 1000), (1050, 1000)));
    }
}

/// <summary>Actions: result matching, refusals, timeouts, claims.</summary>
public class ActionTests
{
    [Fact]
    public void Cast_resolves_from_the_cast_start_and_from_a_refusal()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Tick();
        var a = t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        Assert.True(a.Value > 0, t.Ctx.Actions.LastRejection.ToString());
        Assert.Contains(IntentEncoder.OpCastObj, Drain(t));
        t.Ev(InboundKind.SkillCastStart, h2: 2000, a: 0, b: 1);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
        Assert.True(t.Ctx.World.Skills.ReadyInMs(0, 0, t.Clock.NowMs) > 0);   // cooldown from the server's start
        t.Tick(30);
        var b = t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        t.Ev(InboundKind.CastFail, a: 0x0FCA);
        t.Tick();
        Assert.Equal(ActionState.Refused, t.Ctx.Actions.Get(b).State);
        Assert.Equal(0x0FCA, t.Ctx.Actions.Get(b).Code);
    }

    [Fact]
    public void Out_of_range_is_a_local_refusal_with_a_handle()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 9000, y: 5000);
        t.Tick();
        var a = t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        Assert.Equal(ActionState.Refused, t.Ctx.Actions.Get(a).State);
        Assert.Equal((ushort)LocalRefusal.OutOfRange, t.Ctx.Actions.Get(a).Code);
    }

    [Fact]
    public void Unanswered_action_times_out_and_link_loss_fails_it()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Tick();
        var a = t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        t.Tick(40);
        Assert.Equal(ActionState.TimedOut, t.Ctx.Actions.Get(a).State);
        var b = t.Ctx.Combat.Bash(2000, 1, t.Ctx);
        t.Ev(InboundKind.LinkChanged, a: (long)LinkState.Disconnected);
        t.Tick();
        Assert.Equal(ActionState.Failed, t.Ctx.Actions.Get(b).State);
    }

    [Fact]
    public void Higher_layer_claims_preempt_lower_layers()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ctx.Actions.SetClaims(1, Resource.Movement);
        var a = t.Ctx.Move.Order(new MoveOrder(6000, 5000, false, 10, ActionId.None), t.Ctx, 2);
        Assert.Equal(-1, a.Value);
        Assert.Equal(LocalRefusal.Preempted, t.Ctx.Actions.LastRejection);
    }

    [Fact]
    public void Reissuing_the_same_move_keeps_the_running_order()
    {
        using var t = new TestBot();
        t.Tick(30);   // spawn settle
        var a = t.Ctx.Move.Order(new MoveOrder(5600, 5000, false, 10, ActionId.None), t.Ctx, 1);
        t.Tick();
        var b = t.Ctx.Move.Order(new MoveOrder(5605, 5000, false, 10, ActionId.None), t.Ctx, 1);
        Assert.Equal(a, b);
        t.Tick(200);
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
        Assert.True(Math.Abs(t.Ctx.World.Self.Pos.X - 5600) < 2);
    }

    [Fact]
    public void Refused_swing_starts_are_not_rearmed_forever()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5160, y: 5000);
        t.Tick();
        t.Ctx.Combat.Bash(2000, 1, t.Ctx);
        t.Tick();
        for (var i = 0; i < 5; i++) { t.Ev(InboundKind.BashStop, h: 1000); t.Tick(); }
        Assert.False(t.Ctx.Combat.Bashing);
        Assert.Equal(2000, t.Ctx.Combat.RefusedTarget);
        Assert.True(t.Sent.Count(o => o == IntentEncoder.OpBashStart) <= 2);
        Assert.Equal(GameRules.BashStartReach(50, WeaponClass.Melee) + 10, GameRules.BashReach(50, WeaponClass.Melee));
    }

    [Fact]
    public void One_cease_fire_with_nothing_landed_is_retried_then_a_second_stops_the_bash()
    {
        // NextA, 02:48:22: one CEASE_FIRE right after the start, then 24 s "bashing" without a packet
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5060, y: 5000);
        t.Tick();
        t.Ctx.Combat.Bash(2000, 1, t.Ctx);
        t.Tick();
        var starts = t.Sent.Count(o => o == IntentEncoder.OpBashStart);
        t.Ev(InboundKind.BashStop, h: 1000); t.Tick(3);
        Assert.True(t.Sent.Count(o => o == IntentEncoder.OpBashStart) > starts);   // retried
        Assert.True(t.Ctx.Combat.Bashing);
        t.Ev(InboundKind.BashStop, h: 1000); t.Tick(3);
        Assert.False(t.Ctx.Combat.Bashing);                                         // the second refusal ends it
    }

    [Fact]
    public void A_swing_stream_that_landed_is_rearmed()
    {
        using var t = new TestBot();
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5060, y: 5000);
        t.Tick();
        t.Ctx.Combat.Bash(2000, 1, t.Ctx);
        t.Tick();
        for (var i = 0; i < 4; i++) { t.Ev(InboundKind.Damage, h: 1000, h2: 2000, a: 5, b: 400); t.Ev(InboundKind.BashStop, h: 1000); t.Tick(); }
        Assert.True(t.Ctx.Combat.Bashing);
        Assert.True(t.Sent.Count(o => o == IntentEncoder.OpBashStart) >= 4);
    }

    [Fact]
    public void Gate_hop_succeeds_through_a_cross_server_handoff()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)(EntityKind.Gate | EntityKind.Npc), b: 1, x: 5050, y: 5000, text: "Other");
        t.Tick();
        var a = t.Ctx.Procedures.Start(new GateHopProcedure(3000, "Other"), ActionKind.Gate, 1, t.Ctx, 30000);
        t.Tick();
        Assert.Contains(IntentEncoder.OpNpcClick, t.Sent);
        var menu = InboundEvent.Of(InboundKind.ServerMenu, 0x3C01, t.Clock.NowMs); menu.Handle = 3000; menu.Options = [(0, "Yes"), (1, "No")]; menu.Text = "move?";
        t.In.TryWrite(menu);
        t.Tick();
        // LINKOTHER, then the link goes down for the reconnect, in one tick
        t.Ev(InboundKind.MapLink, a: 2, b: 1, x: 100, y: 100);
        t.Ev(InboundKind.LinkChanged, a: (long)LinkState.Connecting);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
    }

    [Fact]
    public void A_gate_click_from_a_pitched_tent_folds_it_first_and_clicks_after_the_fold_ack()
    {
        // NextM camped at 02:24:35 and clicked three gates over five minutes: the server answers no camped click
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.Tent, a: 1, b: 0x0A81);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)(EntityKind.Gate | EntityKind.Npc), b: 1, x: 5050, y: 5000, text: "Other");
        t.Tick();
        Assert.True(t.Ctx.World.Self.TentPitched);
        t.Sent.Clear();
        t.Ctx.Procedures.Start(new GateHopProcedure(3000, "Other"), ActionKind.Gate, 1, t.Ctx, 30000);
        t.Tick(3);
        Assert.Contains(IntentEncoder.OpFoldTent, t.Sent);
        Assert.DoesNotContain(IntentEncoder.OpNpcClick, t.Sent);
        t.Ev(InboundKind.Tent, a: 0, b: 0x0A81);
        t.Tick(2);
        Assert.Contains(IntentEncoder.OpNpcClick, t.Sent);
    }

    [Fact]
    public void A_move_from_a_pitched_tent_folds_first_and_walks_after_the_fold_ack()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.Tent, a: 1, b: 0x0A81);
        t.Tick();
        t.Sent.Clear();
        t.Ctx.Move.Order(new MoveOrder(5400, 5000, false, 10, ActionId.None), t.Ctx, 1);
        t.Tick(3);
        Assert.Contains(IntentEncoder.OpFoldTent, t.Sent);
        Assert.DoesNotContain(IntentEncoder.OpMoveRun, t.Sent);
        t.Ev(InboundKind.Tent, a: 0, b: 0x0A81);
        t.Tick(2);
        Assert.Contains(IntentEncoder.OpMoveRun, t.Sent);
    }

    [Fact]
    public void An_unanswered_gate_click_is_logged_and_retried_never_a_map_avoid()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)(EntityKind.Gate | EntityKind.Npc), b: 1, x: 5050, y: 5000, text: "Other");
        t.Tick();
        var a = t.Ctx.Procedures.Start(new GateHopProcedure(3000, "Other"), ActionKind.Gate, 1, t.Ctx, 30000);
        t.Tick();
        Assert.Contains(IntentEncoder.OpNpcClick, t.Sent);
        t.Tick(150);                                   // no menu for 7.5 s
        Assert.Equal(ActionState.Failed, t.Ctx.Actions.Get(a).State);
        Assert.Empty(t.Ctx.RefusedMaps);               // a gate is never banned: the fault is ours
    }

    [Fact]
    public void A_gate_click_waits_for_the_game_held_position_to_be_in_reach()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)(EntityKind.Gate | EntityKind.Npc), b: 1, x: 5050, y: 5000, text: "Other");
        t.Tick();
        // the game still holds us 1600 away (e.g. moves it did not take): no click from here
        t.Ctx.World.Self.GetType().GetProperty("ServerPos")!.SetValue(t.Ctx.World.Self, ((uint)6650, (uint)5000));
        t.Sent.Clear();
        t.Ctx.Procedures.Start(new GateHopProcedure(3000, "Other"), ActionKind.Gate, 1, t.Ctx, 30000);
        t.Tick(2);
        Assert.DoesNotContain(IntentEncoder.OpNpcClick, t.Sent);
        // our belief is re-synced to where the game has us, so the walk in starts from the true spot (the test world
        // has no walkability map, so the walk itself is not run here)
        Assert.True(t.Ctx.World.Self.Pos.X > 6000);
    }

    [Fact]
    public void The_click_answer_timeout_and_reach_are_learned_from_answered_clicks()
    {
        using var t = new TestBot();
        t.Tick(30);
        Assert.Equal(4321, Camp.AnswerTimeoutMs(t.Ctx, 4321));            // nothing measured: the caller's prior
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)EntityKind.Npc, b: 28, x: 5100, y: 5000);
        t.Tick();
        for (var i = 0; i < Camp.MinSamples; i++)
        {
            Camp.Click(t.Ctx, 3000, -1);
            t.Clock.NowMs += 40 + i;                                       // answers 40-47 ms later
            t.Ev(InboundKind.NpcMenu, a: 28);
            t.Tick(1, 0);
        }
        var timeout = Camp.AnswerTimeoutMs(t.Ctx, 4321);
        Assert.InRange(timeout, 47 * 2, 47 * 4 + 10);                     // a high percentile x4, at least the slowest x2
        Assert.Equal(100, Camp.AnsweredReach(t.Ctx), 0);                  // answered from 100 away (game-held position)
    }

    [Fact]
    public void A_refused_leg_blocks_its_tiles_on_that_map_and_they_survive_a_visit_elsewhere()
    {
        var o = new NavOverlay();
        o.OnMap("Eld");
        for (var tx = 10; tx <= 20; tx++) o.LearnBlocked(tx, 5, long.MaxValue);
        Assert.Equal(11, o.SnapshotBlocked(0).Count);
        o.OnMap("RouCos03");
        Assert.Empty(o.SnapshotBlocked(0));                 // another map: its own (none yet)
        o.Clear();                                          // a map change forgets doors only
        o.OnMap("Eld");
        Assert.Equal(11, o.SnapshotBlocked(0).Count);       // back on Eld: what the game taught us is still there
    }

    [Fact]
    public void A_move_the_game_refuses_teaches_every_tile_of_the_leg()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ctx.Move.Order(new MoveOrder(5100, 5000, false, 1, ActionId.None), t.Ctx, 1);
        t.Tick(2);
        t.Ev(InboundKind.MoveFail, x: 5000, y: 5000);         // refused where we stand
        t.Tick();
        var blocked = t.Ctx.Overlay.SnapshotBlocked(t.Clock.NowMs);
        Assert.True(blocked.Count >= 15, $"{blocked.Count} tiles");   // 100 units = 16 tiles, the one we stand on excluded
        Assert.DoesNotContain(((long)800 << 32) | 800u, blocked);
    }

    [Fact]
    public void An_out_of_range_refusal_re_syncs_our_position_and_learns_no_range()
    {
        using var t = new TestBot();
        t.Tick(30);
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5080, y: 5000);
        t.Tick();
        Assert.Equal(CanUseReason.Ok, GameRules.CanUse(t.Ctx, 0, 2000));      // 80 < Range 100 + body 50 + 50
        t.Ctx.World.Self.GetType().GetProperty("ServerPos")!.SetValue(t.Ctx.World.Self, ((uint)4700, (uint)5000));
        t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        t.Tick();
        t.Ev(InboundKind.CastFail, a: 0x0FCA);                                 // the game had us elsewhere
        t.Tick();
        Assert.Equal(4700, t.Ctx.World.Self.Pos.X, 0);                       // re-synced to the game's position
        t.Clock.NowMs += 2000; t.Tick();
        t.Ctx.World.Self.GetType().GetProperty("Pos")!.SetValue(t.Ctx.World.Self, (5000.0, 5000.0));
        Assert.Equal(CanUseReason.Ok, GameRules.CanUse(t.Ctx, 0, 2000));      // the skill's range is the data's, untouched
    }

    [Fact]
    public void A_mob_spawning_in_view_takes_skills_only_after_the_learned_guard()
    {
        using var t = new TestBot();
        t.Tick(30);
        const long spawning = (long)EntityKind.Mob | EntityDecoder.KSpawning;
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: spawning, b: 1, x: 5080, y: 5000);
        t.Tick();
        Assert.Equal(CanUseReason.Ok, GameRules.CanUse(t.Ctx, 0, 2000));         // nothing learned yet: try
        t.Ctx.Combat.Cast(0, 2000, 1, t.Ctx);
        t.Clock.NowMs += 500;
        t.Ev(InboundKind.CastFail, a: 0x0FCD);                                    // refused 500+ ms after its spawn
        t.Tick();
        Assert.Equal(1, t.Ctx.Combat.CastsRefused);
        t.Ev(InboundKind.EntityAppeared, h: 2001, a: spawning, b: 1, x: 5080, y: 5010);
        t.Tick();
        Assert.Equal(CanUseReason.Spawning, GameRules.CanUse(t.Ctx, 0, 2001));   // a fresh spawn waits the window out
        t.Ev(InboundKind.EntityAppeared, h: 2002, a: (long)EntityKind.Mob, b: 1, x: 5080, y: 5020);
        t.Tick();
        Assert.Equal(CanUseReason.Ok, GameRules.CanUse(t.Ctx, 0, 2002));         // came into view already spawned: no wait
        t.Clock.NowMs += 600; t.Tick();
        Assert.Equal(CanUseReason.Ok, GameRules.CanUse(t.Ctx, 0, 2001));         // past the window
        t.Ctx.Combat.Cast(0, 2001, 1, t.Ctx);
        t.Ev(InboundKind.SkillCastStart, a: 0, h2: 2001);                         // taken 600 ms after: an upper bound only
        t.Tick();
        t.Ev(InboundKind.EntityAppeared, h: 2003, a: spawning, b: 1, x: 5080, y: 5030);
        t.Tick();
        t.Clock.NowMs += 501; t.Tick();
        Assert.Equal(0, GameRules.SpawnGuardLeftMs(t.Ctx, t.Ctx.World.Entities.SlotOf(2003)));   // the wait is the refused bound, not the taken one
    }

    [Fact]
    public void The_burst_plan_holds_every_rank_of_a_skill_on_the_group_cooldown()
    {
        var d = new TestData();
        var sk = d.Skill(0)!;
        var data = new GroupData(sk with { Id = 0, DelayGroup = 10, DelayGroupMs = 5000 }, sk with { Id = 1, Step = 2, MinWc = sk.MinWc * 2, MaxWc = sk.MaxWc * 2, DelayGroup = 10, DelayGroupMs = 5000 });
        using var t = new TestBot(data);
        t.Ev(InboundKind.SkillLearned, a: 0); t.Ev(InboundKind.SkillLearned, a: 1);
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Ev(InboundKind.EntityHp, h: 2000, a: 100000, b: 100000);
        t.Tick(2);
        var sb = new System.Text.StringBuilder();
        var p = Burst.Decide(t.Ctx, 2000, swings: false, sb);
        Assert.Equal(1, p.FirstSkill);                                            // the higher rank first
        var at = System.Text.RegularExpressions.Regex.Matches(sb.ToString(), @"@([0-9.]+)").Take(2).Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(at[1] - at[0] >= 4.9, sb.ToString());                         // the next cast of the group waits its 5 s
    }

    [Fact]
    public void A_cancelled_move_stops_walking()
    {
        using var t = new TestBot();
        t.Tick(30);
        var a = t.Ctx.Move.Order(new MoveOrder(7000, 5000, false, 10, ActionId.None), t.Ctx, 1);
        t.Tick(5);
        Assert.True(t.Ctx.Move.Moving);
        t.Ctx.Actions.Cancel(a);
        t.Tick();
        Assert.False(t.Ctx.Move.Moving);
        Assert.Equal(IntentEncoder.OpStop, t.Sent[^1]);
    }

    [Fact]
    public void A_quest_accepted_in_dialogue_becomes_active()
    {
        using var t = new TestBot();
        t.Tick();
        Assert.False(t.Ctx.World.Quests.IsActive(956));
        t.Ev(InboundKind.QuestDialog, a: 956, b: 6);   // QUEST_SCRIPT_CMD ACCEPT, the only notice the server sends
        t.Tick();
        Assert.True(t.Ctx.World.Quests.IsActive(956));
        t.Ev(InboundKind.QuestDialog, a: 956, b: 10);  // DONE
        t.Tick();
        Assert.False(t.Ctx.World.Quests.IsActive(956));
        Assert.True(t.Ctx.World.Quests.IsDone(956));
    }

    [Fact]
    public void A_trip_survives_the_link_change_of_a_zone_handoff()
    {
        using var t = new TestBot();
        t.Tick(5);
        var trip = t.Ctx.Procedures.Start(new TravelProcedure("Nowhere"), ActionKind.Travel, 1, t.Ctx, 60000);
        var move = t.Ctx.Move.Order(new MoveOrder(6000, 5000, false, 10, ActionId.None), t.Ctx, 1);
        t.Ev(InboundKind.LinkChanged, a: (long)LinkState.Connecting);
        t.Tick();
        Assert.Equal(ActionState.Failed, t.Ctx.Actions.Get(move).State);          // an ordinary action fails on link loss
        Assert.NotEqual(LocalRefusal.LinkDown, (LocalRefusal)t.Ctx.Actions.Get(trip).Code);   // the trip decides itself
    }

    [Fact]
    public void A_trip_whose_walk_another_layer_takes_over_waits_instead_of_failing()
    {
        using var t = new TestBot();
        t.Ev(InboundKind.SelfBase, a: 10, b: 0, c: 100, text: "TestMap");
        t.Tick(5);
        var trip = t.Ctx.Procedures.Start(new TravelProcedure("TestMap", 9000, 5000, 50), ActionKind.Travel, 3, t.Ctx, 60000);
        t.Tick(2);
        Assert.Equal("walkFinal", t.Ctx.Procedures.Active[0].Step);
        // the combat layer walks us to a mob: the trip's own walk is cancelled
        t.Ctx.Move.Order(new MoveOrder(5100, 5000, true, 10, ActionId.None), t.Ctx, 1);
        t.Tick(5);
        Assert.True(t.Ctx.Actions.Get(trip).Pending, t.Ctx.Procedures.Recent.LastOrDefault().P?.FailReason);   // was: Failed "could not reach: Cancelled"
    }

    [Fact]
    public void A_shop_behind_a_quest_page_is_opened_with_the_menu_answer()
    {
        using var t = new TestBot();
        t.Tick(5);
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)EntityKind.Npc, b: 1, x: 5020, y: 5000);
        t.Tick();
        var a = t.Ctx.Procedures.Start(new OpenShopProcedure(3000), ActionKind.OpenShop, 1, t.Ctx);
        t.Tick();
        t.Sent.Clear();
        t.Ev(InboundKind.QuestDialog, a: 466, b: 2);   // the healer's quest page instead of its menu
        t.Tick();
        Assert.Contains(IntentEncoder.OpNpcMenuAck, t.Sent);
        t.Ev(InboundKind.ShopList, h: -1, a: 0x3C05);
        t.Tick();
        Assert.Equal(ActionState.Acked, t.Ctx.Actions.Get(a).State);
    }

    [Fact]
    public void Quest_counters_belong_to_the_kill_objectives_only()
    {
        // "Master Zach": {talk to Zach, kill 5 slimes}; after five kills the server's counter 1 is 5
        var zach = new QuestDef(8, "Master Zach", 2, 10, false, 88,
            [new QuestObjectiveDef(QuestObjectiveKind.Talk, 88, -1, 1), new QuestObjectiveDef(QuestObjectiveKind.Kill, 0, -1, 5)], 100, 0, 0, 0, "");
        Assert.Equal(-1, zach.CounterOf(0));
        Assert.Equal(1, zach.CounterOf(1));
        var three = new QuestDef(421, "Kingdom", 1, 10, false, 92,
            [new QuestObjectiveDef(QuestObjectiveKind.Talk, 92, -1, 1), new QuestObjectiveDef(QuestObjectiveKind.Kill, 1030, -1, 5),
             new QuestObjectiveDef(QuestObjectiveKind.Collect, 3, 3079, 8), new QuestObjectiveDef(QuestObjectiveKind.Kill, 1034, -1, 2)], 100, 0, 0, 0, "");
        Assert.Equal(1, three.CounterOf(1));
        Assert.Equal(-1, three.CounterOf(2));
        Assert.Equal(2, three.CounterOf(3));
    }

    [Fact]
    public void A_hand_in_with_a_reward_choice_names_the_pick_before_the_ack()
    {
        using var t = new TestBot();
        t.Tick(5);
        t.Ev(InboundKind.QuestDialog, a: 8, b: 6);   // accepted earlier
        t.Ev(InboundKind.EntityAppeared, h: 3000, a: (long)EntityKind.Npc, b: 1, x: 5020, y: 5000);
        t.Tick();
        t.Ctx.Procedures.Start(new QuestDialogueProcedure(3000, 8, -1), ActionKind.Talk, 1, t.Ctx);
        t.Tick();
        t.Sent.Clear();
        var page = InboundEvent.Of(InboundKind.QuestDialog, 0x4401, t.Clock.NowMs); page.A = 8; page.B = 2; page.C = 714; page.Handle2 = 88;
        t.In.TryWrite(page);
        t.Tick();
        var i = t.Sent.IndexOf(IntentEncoder.OpQuestRewardSelect);
        Assert.True(i >= 0 && i < t.Sent.IndexOf(IntentEncoder.OpQuestScriptAck), string.Join(",", t.Sent.Select(x => x.ToString("X4"))));
    }

    private static List<ushort> Drain(TestBot t) { t.Tick(); return t.Sent; }
}

/// <summary>Brain format, linter and runtime semantics (API_SPEC 5).</summary>
public class BrainTests
{
    private const string Two = """
brain: t
api: 3
modules: [brain/t/m]
blackboard:
  fields: [A, B]
layers:
  - name: survival
    priority: 90
    machine:
      initial: ok
      states:
        ok:
          transitions:
            - { to: low, value: self.hpPct, op: "<", than: 35, hold: 200 }
        low:
          tick: m.low
          budget: 1000
          progress: self.hpPct
          transitions:
            - { to: ok, value: self.hpPct, op: ">=", than: 70 }
            - { to: ok, stuck: true }
  - name: work
    priority: 30
    machine:
      initial: a
      states:
        a:
          tick: m.count
          transitions:
            - { to: b, when: "bb.state.n ~= nil and bb.state.n >= 3" }
            - { to: b, after: 60000 }
        b:
          terminal: true
          transitions:
            - { to: a, after: 100000 }
""";
    private static readonly Dictionary<string, string> Mods = new()
    {
        ["brain/t/m"] = """
local M = {}
function M.count() bb.state.n = (bb.state.n or 0) + 1 end
function M.low() bb.bot.lowTicks = (bb.bot.lowTicks or 0) + 1 end
function M.spin() while true do end end
function M.bad() return act.cast("x") end
return M
""",
    };

    [Fact]
    public void Parses_yaml_and_json_to_the_same_model()
    {
        var y = BrainLoader.Parse(Two);
        var j = BrainLoader.FromJson(System.Text.Json.JsonSerializer.Serialize(new
        {
            brain = "t", api = 3, layers = new[] { new { name = "l", priority = 1, machine = new { initial = "s", states = new { s = new { terminal = true } } } } },
        }));
        Assert.Equal(2, y.Layers.Count);
        Assert.Equal("t", j.Brain);
        Assert.Equal(ConditionKind.Value, y.Layers[0].Machine.States["ok"].Transitions[0].Condition.Kind);
    }

    [Fact]
    public void Linter_rejects_states_without_exits_unknown_targets_and_sensors()
    {
        var bad = BrainLoader.Parse("""
brain: x
api: 3
layers:
  - name: l
    priority: 1
    machine:
      initial: s
      states:
        s:
          transitions:
            - { to: nowhere, value: self.noSuchSensor, op: "<", than: 1 }
        t: {}
""");
        var lint = BrainLinter.Lint(bad, new Dictionary<string, string>(), SensorRegistry.StaticPaths());
        Assert.Contains(lint, l => l.Code == "TARGET_UNKNOWN");
        Assert.Contains(lint, l => l.Code == "SENSOR");
        Assert.Contains(lint, l => l.Code == "NO_EXIT" && l.State == "t");
        var unknownApi = BrainLinter.Lint(BrainLoader.Parse(Two), new Dictionary<string, string> { ["brain/t/m"] = "local M = {} function M.f() return self.noSuchFn() end return M" });
        Assert.Contains(unknownApi, l => l.Code == "API_NAME");
        var shadow = BrainLinter.Lint(BrainLoader.Parse(Two), new Dictionary<string, string> { ["brain/t/social"] = "local M = {} return M" });
        Assert.Contains(shadow, l => l.Code == "MODULE_SHADOWS_API");
    }

    [Fact]
    public void Value_hold_progress_and_stuck()
    {
        using var t = new TestBot();
        var b = t.Load(Two, Mods);
        t.Tick(3);
        Assert.Equal("ok", b.Layers[0].ActivePath[0].Name);
        t.Ev(InboundKind.SelfHp, a: 20);
        t.Tick(2);
        Assert.Equal("ok", b.Layers[0].ActivePath[0].Name);   // hold 200 ms
        t.Tick(5);
        Assert.Equal("low", b.Layers[0].ActivePath[0].Name);
        t.Tick(22);                                             // no progress for 1 s -> stuck -> ok (and back after the hold)
        Assert.Contains(b.Debug.Transitions(), x => x.Kind == "stuck");
        Assert.True(t.Ctx.Events.Total(EventKind.Stuck) > 0);
    }

    [Fact]
    public void Lua_condition_state_scope_and_layers()
    {
        using var t = new TestBot();
        var b = t.Load(Two, Mods);
        t.Tick(6);
        Assert.Equal("b", b.Layers[1].ActivePath[0].Name);
        var bb = b.BlackboardJson();
        Assert.Equal(2, bb["bot"]!["fields"]!.AsObject().Count);   // the definition's blackboard
    }

    [Fact]
    public void Hot_swap_keeps_bb_bot_and_states_and_seeds_new_defaults()
    {
        using var t = new TestBot();
        var b = t.Load(Two, Mods);
        t.Ev(InboundKind.SelfHp, a: 20);
        t.Tick(8);
        Assert.Equal("low", b.Layers[0].ActivePath[0].Name);
        var v2 = Two.Replace("fields: [A, B]", "fields: [A, B]\n  extra: 5").Replace("budget: 1000", "budget: 5000");
        var c2 = BrainCompiler.Compile(BrainLoader.Parse(v2), Mods);
        var before = b.BlackboardJson()["bot"]!["lowTicks"]!.GetValue<long>();
        b.Swap(c2, keepState: true);
        Assert.Equal("low", b.Layers[0].ActivePath[0].Name);
        var bot = b.BlackboardJson()["bot"]!;
        Assert.True(bot["lowTicks"]!.GetValue<long>() >= before);
        Assert.Equal(5, bot["extra"]!.GetValue<long>());
    }

    [Fact]
    public void Budget_is_enforced_three_overruns_restart_the_layer_and_repeated_restarts_halt_it()
    {
        using var t = new TestBot();
        var yaml = Two.Replace("tick: m.count", "tick: m.spin");
        var b = t.Load(yaml, Mods);
        var sw = Stopwatch.StartNew();
        // the first 30 s warm the caches: overruns are logged, not counted
        t.Tick(3);
        Assert.False(b.Suspended);
        t.Tick(1, 30_000);
        t.Tick(3);
        Assert.False(b.Suspended);                 // three overruns restart the layer, the brain runs on
        t.Tick(6);
        Assert.True(sw.ElapsedMilliseconds < 4000, "the spinning state was not stopped by the budget");
        Assert.True(b.Suspended);                  // a layer restarted three times within the window halts
        Assert.Contains(b.Debug.Errors(), e => e.Error.Contains("E_BUDGET"));
    }

    [Fact]
    public void Argument_errors_are_reported_without_lua_error()
    {
        using var t = new TestBot();
        var b = t.Load(Two.Replace("tick: m.count", "tick: m.bad"), Mods);
        t.Tick(2);
        Assert.Contains(b.Debug.Errors(), e => e.Error.Contains("E_ARG"));
    }

    [Fact]
    public void Operator_force_state()
    {
        using var t = new TestBot();
        var b = t.Load(Two, Mods);
        t.Tick();
        var r = (TransitionRecord)b.Control(new ControlCommand(ControlKind.ForceState, Layer: "work", State: "b"), t.Ctx)!;
        Assert.Equal("operator", r.Kind);
        Assert.Equal("b", b.Layers[1].ActivePath[0].Name);
        Assert.Throws<ArgumentException>(() => b.Control(new ControlCommand(ControlKind.ForceState, Layer: "work", State: "zz"), t.Ctx));
    }
}

/// <summary>The Lua API: zero allocation for the common crossings, iterators, constants.</summary>
public class ApiTests
{
    [Fact]
    public void Iterators_and_queries_work_and_do_not_allocate_per_tick()
    {
        using var t = new TestBot();
        for (var i = 0; i < 20; i++) t.Ev(InboundKind.EntityAppeared, h: 2000 + i, a: (long)EntityKind.Mob, b: 1, x: (uint)(5000 + 30 * i), y: 5000);
        var b = t.Load("""
brain: a
api: 3
modules: [brain/a/m]
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
""", new() { ["brain/a/m"] = """
local M = {}
function M.t()
  local n = 0
  for h in ents.near(2000, ents.MOB) do n = n + 1 end
  for h in ents.near(100, ents.MOB) do
    for h2 in ents.near(100, ents.MOB) do n = n + 0 end
  end
  bb.state.n = n
  bb.state.c = ents.count(2000, ents.MOB)
  bb.state.near = ents.nearest(ents.MOB)
  local x, y = self.pos()
  bb.state.ok = canUse(0, ents.nearest(ents.MOB))
  dbg.watch("hp", self.hpPct())
end
return M
""" });
        t.Tick(20);
        var s = b.BlackboardJson()["state"]!["l"]!;
        Assert.Equal(20, s["n"]!.GetValue<long>());
        Assert.Equal(20, s["c"]!.GetValue<long>());
        Assert.Equal(2000, s["near"]!.GetValue<long>());
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        t.Tick(100);
        var per = (GC.GetAllocatedBytesForCurrentThread() - a0) / 100.0;
        Assert.True(per < 256, $"{per} B per tick");
    }

    [Fact]
    public void Manifest_and_constants()
    {
        Assert.True(GeneratedApi.FunctionCount > 200);
        Assert.Contains("skills.canUse", ApiManifest.Names());
        Assert.Contains("self.hpPct", ApiManifest.ZeroArgNumeric());
        Assert.Equal((long)EntityKind.Huntable, LuaConstants.Ents["HUNTABLE"]);
    }
}

/// <summary>Logging: zero cost when off, collapsing.</summary>
public class LoggingTests
{
    [Fact]
    public void Filtered_call_does_not_allocate()
    {
        var log = new BotLogger("x", new LogRing(64), null, new ManualClock());
        var v = 42;
        log.Log(LogCategory.Nav, LogLevel.Debug, $"warm {v}");
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) log.Log(LogCategory.Nav, LogLevel.Debug, $"path {i} to {v}");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - a0);
    }

    [Fact]
    public void Repeats_collapse()
    {
        var clock = new ManualClock { NowMs = 1 };
        var log = new BotLogger("x", new LogRing(64), null, clock);
        for (var i = 0; i < 10; i++) log.Text(LogCategory.Nav, LogLevel.Note, $"walk {i} unreachable");
        clock.NowMs += 11_000;
        log.Text(LogCategory.Nav, LogLevel.Note, "other");
        var buf = new LogEntry[64];
        var n = log.Ring.Read(LogLevel.Trace, null, 0, long.MaxValue, buf);
        Assert.Equal(3, n);
        Assert.Equal(9, buf[1].Repeat);
    }
}

/// <summary>Deterministic replay of recorded sessions (REDESIGN 12.1).</summary>
public class ReplayTests
{
    [Fact]
    public void A_recorded_session_replays_to_the_same_intents()
    {
        var file = Path.Combine(Path.GetTempPath(), $"t-{Guid.NewGuid():N}.botrec");
        List<ushort> live;
        const string brain = """
brain: r
api: 3
modules: [brain/r/m]
layers:
  - name: c
    priority: 1
    machine:
      initial: s
      states:
        s:
          tick: m.t
          terminal: true
          transitions: [ { to: s, after: 99999999 } ]
""";
        var mods = new Dictionary<string, string> { ["brain/r/m"] = "local M = {} function M.t() local h = ents.nearest(ents.MOB) if h >= 0 and not combat.bashing() then act.bash(h) end end return M" };
        using (var t = new TestBot())
        {
            var rec = new SessionRecorder(file, "t", "x", "2016");
            t.Agent.Recorder = rec.Record;
            t.Load(brain, mods);
            t.Tick(2);
            t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)EntityKind.Mob, b: 1, x: 5050, y: 5000);
            t.Tick(5);
            t.Ev(InboundKind.EntityDied, h: 2000, h2: 1000);
            t.Tick(5);
            rec.Dispose();
            live = t.Sent.ToList();
        }
        using var r = new TestBot();
        while (r.In.TryRead(out _)) { }
        r.Load(brain, mods);
        var result = new SessionReplayer(file).Run(r.Agent, r.Clock);
        Assert.Equal(live, result.Intents.Select(x => x.Opcode).ToList());
        Assert.Contains(IntentEncoder.OpBashStart, live);
    }
}
