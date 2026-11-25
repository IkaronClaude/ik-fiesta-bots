using System.Diagnostics;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;
using LogLevel = Fiesta.Bot.Services.Logging.LogLevel;

namespace Fiesta.Bot.Server;

/// <summary>`bench [--bots N] [--ticks T] [--data-dir ressystem]`: N agents with a layered test brain against a synthetic
/// world (50 mobs moving, periodic swings) on one thread; reports tick p50/p99/max, allocation per tick and GC
/// (REDESIGN 2.6 / 12.3 macro benchmark shape, without the network).</summary>
public static class BenchCli
{
    /// <summary>The test brain (a benchmark fixture: three layers, value / event / Lua conditions, iterators, canUse).</summary>
    public const string Brain = """
brain: bench
api: 3
modules: [brain/bench/logic]
layers:
  - name: survival
    priority: 90
    machine:
      initial: ok
      states:
        ok:
          transitions:
            - { to: recover, value: self.hpPct, op: "<", than: 35, hold: 200 }
        recover:
          tick: logic.recover
          budget: 15000
          progress: self.hpPct
          transitions:
            - { to: ok, value: self.hpPct, op: ">=", than: 70 }
            - { to: ok, stuck: true }
  - name: combat
    priority: 60
    machine:
      initial: idle
      states:
        idle:
          tick: logic.pick
          transitions:
            - { to: engage, when: "bb.state.target ~= nil and bb.state.target >= 0" }
            - { to: idle, after: 30000 }
        engage:
          tick: logic.fight
          budget: 45000
          transitions:
            - { to: idle, on: targetDied }
            - { to: idle, stuck: true }
  - name: errand
    priority: 30
    rate: 500
    machine:
      initial: choose
      states:
        choose:
          tick: logic.choose
          terminal: true
          transitions:
            - { to: choose, every: 5000 }
""";

    /// <summary>The test brain's module.</summary>
    public const string Logic = """
local M = {}
local SK = 0
function M.recover()
  if canUse(SK, self.handle()) then act.cast(SK, self.handle()) end
end
function M.pick()
  local best, bestCost = -1, 1e9
  for h in ents.near(800, ents.HUNTABLE) do
    local c = threat.pullCost(h)
    if c < bestCost then best, bestCost = h, c end
  end
  bb.state.target = best
end
function M.fight()
  local t = bb.layer.target or bb.state.target
  if t == nil or t < 0 or not ents.exists(t) then return end
  if combat.inBashReach(t) then
    if not combat.bashing() then act.bash(t) end
  else
    local x, y = ents.pos(t)
    act.moveTo(x, y, combat.bashReach(t) * 0.8)
  end
  for s in skills.list(skills.DAMAGE) do
    if canUse(s, t) then act.cast(s, t) break end
  end
  dbg.watch("hp", self.hpPct())
end
function M.choose()
  local n = ents.count(1500, ents.MOB)
  bb.layer.mobs = n
  if time.every(1, 2000) then dbg.logi("mobs around: %d", n) end
end
return M
""";

    /// <summary>Run.</summary>
    public static Task<int> RunAsync(string[] args)
    {
        string Arg(string k, string d) { var i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var bots = int.Parse(Arg("--bots", "100")); var ticks = int.Parse(Arg("--ticks", "2000"));
        var dataDir = Arg("--data-dir", Environment.GetEnvironmentVariable("CLIENT_DATA_DIR") ?? "Z:/Client2026patched_10_6_6/ressystem");
        var r = Run(bots, ticks, Directory.Exists(dataDir) ? new ClientData(dataDir) : null);
        Console.WriteLine(r);
        return Task.FromResult(0);
    }

    /// <summary>Result.</summary>
    public sealed record Result(int Bots, int Ticks, double P50Us, double P99Us, double MaxUs, double BrainP99Us, double AllocMeanB, double AllocP95B,
        double AllocMaxB, double PublishAllocMeanB, int Gen0, int Gen1, int Gen2, double GcPauseMs, long LuaInstrPerTick, int Transitions, string Errors)
    {
        /// <inheritdoc/>
        public override string ToString() =>
            $"bots {Bots} x ticks {Ticks}: tick p50 {P50Us:0.0} us, p99 {P99Us:0.0} us, max {MaxUs:0} us; brain p99 {BrainP99Us:0.0} us; " +
            $"alloc/tick mean {AllocMeanB:0} B, p95 {AllocP95B:0} B, max {AllocMaxB:0} B (publish step mean {PublishAllocMeanB:0} B); " +
            $"GC gen0 {Gen0} gen1 {Gen1} gen2 {Gen2}, pause {GcPauseMs:0} ms; ~{LuaInstrPerTick} Lua instructions/tick; {Transitions} transitions; errors: {Errors}";
    }

    /// <summary>Run the benchmark in-process.</summary>
    public static Result Run(int nBots, int nTicks, IClientData? data)
    {
        var clock = new ManualClock { NowMs = 1000 };
        data ??= new EmptyData();
        using var jobs = new JobScheduler(1);
        var nav = new NavService(Path.GetTempPath(), jobs);
        var knowledge = new KnowledgeStore();
        var modules = new Dictionary<string, string> { ["brain/bench/logic"] = Logic };
        var def = BrainLoader.Parse(Brain);
        var compiled = BrainCompiler.Compile(def, modules);
        if (!compiled.Ok) throw new InvalidOperationException(string.Join("; ", compiled.Lint.Where(l => l.Severity == LintSeverity.Error).Select(l => l.Message)));
        var agents = new List<(BotAgent A, BrainInstance B, Random R)>();
        var mobId = data is ClientData cd ? cd.Quests.Count >= 0 ? 1 : 1 : 1;
        for (var i = 0; i < nBots; i++)
        {
            var inbound = new InboundRing(); var outbound = new OutboundRing { LinkUp = true };
            var log = new BotLogger($"bench{i}", new LogRing(2048), null, clock);
            var ctx = new AgentContext
            {
                BotIndex = i, BotId = $"bench{i}", Data = data, Nav = nav, Jobs = jobs, Knowledge = knowledge, Log = log, Inbound = inbound,
                Actions = new ActionSystem(outbound), World = new WorldState { Data = data, Knowledge = knowledge },
            };
            var agent = new BotAgent(ctx, inbound, outbound, new ControlQueue(), null, clock);
            var brain = new BrainInstance(compiled, ctx);
            agent.Brain = brain;
            agents.Add((agent, brain, new Random(i)));
            // the world: zone entry, 50 mobs, one learned skill
            Ev(inbound, InboundKind.LinkChanged, a: (long)Fiesta.Bot.Net.LinkState.Ready);
            Ev(inbound, InboundKind.SelfBase, a: 20, b: 1000, c: 5000);
            Ev(inbound, InboundKind.SelfHp, a: 800);
            Ev(inbound, InboundKind.MapLoginAck, h: 1000, x: 5000, y: 5000, a: 1000, b: 300);
            Ev(inbound, InboundKind.SkillLearned, a: 0, b: 0);
            for (var m = 0; m < 50; m++)
            {
                var r = agents[^1].R;
                Ev(inbound, InboundKind.EntityAppeared, h: 2000 + m, a: (long)EntityKind.Mob, b: mobId, c: 20, x: (uint)(4000 + r.Next(2000)), y: (uint)(4000 + r.Next(2000)));
            }
        }
        // warm-up
        for (var t = 0; t < 50; t++) { clock.NowMs += 50; foreach (var (a, _, _) in agents) { a.Tick(); Drain(a); } }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2); var pause = GC.GetTotalPauseDuration();
        var total = new List<double>(nBots * nTicks); var brainUs = new List<double>(nBots * nTicks);
        var alloc = new List<double>(nBots * nTicks); var pubAlloc = 0.0; long instr = 0;
        Span<TickSample> last = stackalloc TickSample[1];
        for (var t = 0; t < nTicks; t++)
        {
            clock.NowMs += 50;
            foreach (var (a, _, r) in agents)
            {
                var inbound = a.Inbound;
                // per tick: a few mobs move, sometimes one swings at us
                for (var k = 0; k < 3; k++)
                {
                    var h = 2000 + r.Next(50);
                    Ev(inbound, InboundKind.EntityMoved, h: h, a: 5000, b: 5000, x: (uint)(4000 + r.Next(2000)), y: (uint)(4000 + r.Next(2000)), c: 100);
                }
                if (r.Next(10) == 0) Ev(inbound, InboundKind.Damage, h: 2000 + r.Next(50), h2: 1000, a: 20, b: 300 + r.Next(500));
                if (r.Next(40) == 0) Ev(inbound, InboundKind.SelfHp, a: 800);
                a.Tick();
                Drain(a);
                a.Stats.Last(last);
                total.Add(last[0].TotalUs); brainUs.Add(last[0].BrainUs); alloc.Add(last[0].AllocBytes); pubAlloc += last[0].PublishAllocBytes;
                instr += last[0].LuaInstructions;
            }
        }
        var res = new Result(nBots, nTicks, Q(total, 0.5), Q(total, 0.99), total.Max(), Q(brainUs, 0.99), alloc.Average(), Q(alloc, 0.95), alloc.Max(),
            pubAlloc / alloc.Count, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
            (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, instr / alloc.Count, agents.Sum(x => (int)x.B.Debug.TransitionCount),
            string.Join(" | ", agents[0].B.Debug.Errors().Select(e => e.Error).Concat(agents[0].B.LoadErrors).Distinct().Take(3)));
        foreach (var (_, b, _) in agents) b.Dispose();
        return res;
    }

    private static void Drain(BotAgent a) { while (a.Outbound.TryDequeue(out _, out _)) { } }

    private static double Q(List<double> v, double q) { var a = v.ToArray(); Array.Sort(a); return a[Math.Min(a.Length - 1, (int)(q * a.Length))]; }

    private static void Ev(InboundRing r, InboundKind k, int h = -1, int h2 = -1, long a = 0, long b = 0, long c = 0, uint x = 0, uint y = 0)
    {
        var e = InboundEvent.Of(k, 0, 0); e.Handle = h; e.Handle2 = h2; e.A = a; e.B = b; e.C = c; e.X = x; e.Y = y;
        r.TryWrite(e);
    }

    /// <summary>Client data stand-in when no ressystem is available (one damage skill, one mob).</summary>
    public sealed class EmptyData : IClientData
    {
        private static readonly MobInfoRow M = new(1, "BenchMob", "Bench Mob", 20, 500, 30, 100, false, 1000, 1, 0, 1, false);
        private static readonly ActiveSkillRow S = new(0, "BenchHit01", "Bench Hit", 1, 0, 0, 1, 0, 1000, 0, 0, 5, 0, false, 0, 0, 0, 0, 0, 10, 20, 0, 0, "BenchHit", "-", "-", "-", "-");
        /// <inheritdoc/>
        public MobInfoRow? Mob(int id) => id == 1 ? M : null;
        /// <inheritdoc/>
        public ActiveSkillRow? Skill(int id) => id == 0 ? S : null;
        /// <inheritdoc/>
        public ItemInfoRow? Item(int id) => null;
        /// <inheritdoc/>
        public MapInfoRow? Map(int id) => null;
        /// <inheritdoc/>
        public MapInfoRow? Map(string mapName) => null;
        /// <inheritdoc/>
        public QuestDef? Quest(int id) => null;
        /// <inheritdoc/>
        public string? Dialog(int id) => null;
        /// <inheritdoc/>
        public int ClassFamily(int classId) => 1;
        /// <inheritdoc/>
        public string ClassName(int classId) => "";
        /// <inheritdoc/>
        public IReadOnlyCollection<QuestDef> Quests => [];
        /// <inheritdoc/>
        public string Dir => "";
    }
}
