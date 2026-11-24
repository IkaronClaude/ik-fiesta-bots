using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Fiesta.Bot.LuaBench;

// Modes:
//   micro            per-scenario ns and managed bytes per unit, every engine (Stopwatch + allocation counter)
//   fleet [N] [T]    N bots x T ticks of the decision function per engine: tick p50/p99/max, GC counts, pauses
//   bdn              BenchmarkDotNet run of the key scenarios (in-process, MemoryDiagnoser)
var mode = args.Length > 0 ? args[0] : "micro";
switch (mode)
{
    case "micro": Micro.Run(); break;
    case "fleet":
        Fleet.Run(args.Length > 1 ? int.Parse(args[1]) : 100, args.Length > 2 ? int.Parse(args[2]) : 2000);
        break;
    case "bdn":
        BenchmarkRunner.Run<CrossingBench>(DefaultConfig.Instance
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(10))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator));
        break;
    default: Console.WriteLine("modes: micro | fleet [N] [T] | bdn"); break;
}

static class Micro
{
    // (function, n, units per call, unit name)
    private static readonly (string Fn, int N, int Units, string Unit)[] Cases =
    [
        ("calls_scalar", 1000, 1000, "crossing"),
        ("calls_arg", 1000, 1000, "crossing"),
        ("iter_mobs", 20, 20 * 101, "crossing"),
        ("table_mobs", 20, 20, "50-mob table"),
        ("strings", 200, 200, "map()+log()"),
        ("bb_access", 1000, 1000, "iteration"),
        ("decide", 100, 100, "tick"),
        ("decide_table", 100, 100, "tick"),
    ];

    public static void Run()
    {
        Console.WriteLine($"{"engine",-20} {"scenario",-14} {"ns/unit",10} {"B/unit",10}  unit");
        foreach (var name in Engines.Names)
        {
            using var e = Engines.Create(name, new World(1));
            foreach (var (fn, n, units, unit) in Cases)
            {
                for (int i = 0; i < 50; i++) e.Call(fn, n);          // warm up (JIT, tables, Lua GC)
                const int reps = 300;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++) e.Call(fn, n);
                sw.Stop();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - a0;
                double totalUnits = (double)reps * units;
                Console.WriteLine($"{name,-20} {fn,-14} {sw.Elapsed.TotalNanoseconds / totalUnits,10:F1} {bytes / totalUnits,10:F1}  {unit}");
            }
            // hot reload: compile + run a 300-function (~1,500 line) module
            for (int i = 0; i < 3; i++) e.Reload(Scenario.ReloadModule);
            GC.Collect();
            var r0 = GC.GetAllocatedBytesForCurrentThread();
            var rsw = Stopwatch.StartNew();
            const int reloads = 20;
            for (int i = 0; i < reloads; i++) e.Reload(Scenario.ReloadModule);
            rsw.Stop();
            Console.WriteLine($"{name,-20} {"reload",-14} {rsw.Elapsed.TotalMicroseconds / reloads,10:F0}us {(GC.GetAllocatedBytesForCurrentThread() - r0) / reloads / 1024.0,8:F0}KB  per 1,500-line module");
            Console.WriteLine();
        }
    }
}

static class Fleet
{
    public static void Run(int bots, int ticks)
    {
        Console.WriteLine($"fleet: {bots} bots x {ticks} ticks, one thread, GC server={System.Runtime.GCSettings.IsServerGC} " +
                          $"latency={System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"{"engine",-20} {"fn",-13} {"p50 us",8} {"p99 us",8} {"max us",8} {"KB/tick",8} {"gen0",6} {"gen1",6} {"gen2",6} {"pause ms",9} {"lua heap MB",11}");
        foreach (var name in Engines.Names)
            foreach (var fn in new[] { "decide", "decide_table" })
                RunOne(name, fn, bots, ticks);
    }

    private static void RunOne(string name, string fn, int bots, int ticks)
    {
        var engines = new IEngine[bots];
        for (int i = 0; i < bots; i++) engines[i] = Engines.Create(name, new World(i));
        for (int t = 0; t < 20; t++) foreach (var e in engines) e.Call(fn, 1);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new double[bots * ticks];
        int k = 0;
        var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
        var p0 = GC.GetTotalPauseDuration();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        long ts0;
        for (int t = 0; t < ticks; t++)
            foreach (var e in engines)
            {
                ts0 = Stopwatch.GetTimestamp();
                e.Call(fn, 1);
                samples[k++] = Stopwatch.GetElapsedTime(ts0).TotalMicroseconds;
            }
        var alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        var pause = (GC.GetTotalPauseDuration() - p0).TotalMilliseconds;
        Array.Sort(samples);
        double P(double q) => samples[(int)Math.Min(samples.Length - 1, q * samples.Length)];
        long luaHeap = 0; foreach (var e in engines) luaHeap += e.LuaHeapBytes();
        Console.WriteLine($"{name,-20} {fn,-13} {P(0.5),8:F1} {P(0.99),8:F1} {samples[^1],8:F0} {alloc / 1024.0 / samples.Length,8:F2} " +
                          $"{GC.CollectionCount(0) - g0,6} {GC.CollectionCount(1) - g1,6} {GC.CollectionCount(2) - g2,6} {pause,9:F1} {luaHeap / 1048576.0,11:F1}");
        foreach (var e in engines) e.Dispose();
    }
}

[MemoryDiagnoser]
public class CrossingBench
{
    [Params("moonsharp-userdata", "moonsharp-callback", "keralua", "raw-fnptr")]
    public string Engine { get; set; } = "";
    private IEngine _e = null!;

    [GlobalSetup] public void Setup() => _e = Engines.Create(Engine, new World(1));
    [GlobalCleanup] public void Cleanup() => _e.Dispose();

    /// <summary>1,000 scalar crossings per op</summary>
    [Benchmark(OperationsPerInvoke = 1000)] public double ScalarCall() => _e.Call("calls_scalar", 1000);
    /// <summary>one decision tick (~110 crossings, iterator, no tables)</summary>
    [Benchmark] public double DecideTick() => _e.Call("decide", 1);
    /// <summary>one decision tick reading mobs as a table of tables (today's shape)</summary>
    [Benchmark] public double DecideTableTick() => _e.Call("decide_table", 1);
}
