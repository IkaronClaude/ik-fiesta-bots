using System.Collections.Concurrent;
using System.Diagnostics;
using Fiesta.Bot.Pathfinding;

// PER-MAP A* EXPANSION BUDGET CALIBRATOR
//
// RouteAndWalk searches with a fixed expansion budget. Too low and a legitimate route is abandoned; too high and a
// route that cannot be found grinds for minutes -- which is exactly what cost a bot 357 seconds on a 511-unit walk
// (EldGbl02, 2026-08-20). One global number cannot serve both a 400x400 instance cell and a 2048x2048 field map,
// so measure what each map actually needs.
//
// Method: sample N walkable points per map, pathfind every pair against every other, and escalate the budget until
// the search succeeds. The map's budget is the worst case over all pairs -- the smallest number that ALWAYS works.
//
// THE TRAP A NAIVE VERSION FALLS INTO: a map's walkable area is not one connected blob. Islands, sealed rooms and
// instance cells exist, and a pair spanning two of them can never be pathed at ANY budget. Escalation would climb
// to the cap on every such pair and report a meaningless number. So components are labelled first (one BFS per
// unlabelled sample, each tile visited at most once) and only genuinely connected pairs are timed.

var blockInfo = args.Length > 0 ? args[0] : "Z:/ServerSource/9Data/Shine/BlockInfo";
var samples = args.Length > 1 ? int.Parse(args[1]) : 100;
var outPath = args.Length > 2 ? args[2] : "path-budgets.csv";
const int BudgetStart = 25_000;
const int BudgetCap = 8_000_000;

var files = Directory.GetFiles(blockInfo, "*.shbd").OrderBy(f => f).ToArray();
Console.Error.WriteLine($"calibrating {files.Length} map(s), {samples} samples each, {Environment.ProcessorCount} cores");

var rows = new ConcurrentBag<string>();
var done = 0;
var swAll = Stopwatch.StartNew();

Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
{
    var name = Path.GetFileNameWithoutExtension(f);
    var swMap = Stopwatch.StartNew();
    BlockGrid g;
    try { g = BlockGrid.Load(f); }
    catch (Exception e) { rows.Add($"{name},LOAD_FAILED,,,,,{e.GetType().Name}"); return; }

    // Deterministic sampling, via a STABLE hash of the map name.
    // string.GetHashCode() is NOT stable: .NET randomises it per process, so seeding with it made every run sample
    // different points and report different budgets -- the comment that used to sit here claimed reproducibility
    // it did not have. Caught by running the component pass twice and getting two different sets of maps.
    static int StableSeed(string s2)
    {
        unchecked { uint h = 2166136261; foreach (var ch in s2) { h ^= ch; h *= 16777619; } return (int)(h & 0x7fffffff); }
    }
    var rnd = new Random(StableSeed(name));
    var pts = new List<(int tx, int ty)>();
    for (int t = 0; t < 500_000 && pts.Count < samples; t++)
    {
        int tx = rnd.Next(g.WidthTiles), ty = rnd.Next(g.HeightTiles);
        if (g.IsWalkableTile(tx, ty)) pts.Add((tx, ty));
    }
    if (pts.Count < 2)
    {
        rows.Add($"{name},{g.WidthTiles}x{g.HeightTiles},0,0,0,0,{swMap.ElapsedMilliseconds},no-walkable-samples");
        Interlocked.Increment(ref done);
        return;
    }

    // Connected components, so we never time an impossible pair.
    var comp = new int[g.WidthTiles * g.HeightTiles];
    int nComp = 0;
    var q = new Queue<int>();
    ReadOnlySpan<int> dx = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
    ReadOnlySpan<int> dy = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };
    foreach (var (px, py) in pts)
    {
        int seed = py * g.WidthTiles + px;
        if (comp[seed] != 0) continue;
        nComp++;
        comp[seed] = nComp; q.Enqueue(seed);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            int cx = cur % g.WidthTiles, cy = cur / g.WidthTiles;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if ((uint)nx >= (uint)g.WidthTiles || (uint)ny >= (uint)g.HeightTiles) continue;
                int ni = ny * g.WidthTiles + nx;
                if (comp[ni] != 0 || !g.IsWalkableTile(nx, ny)) continue;
                comp[ni] = nComp; q.Enqueue(ni);
            }
        }
    }

    int maxBudget = 0, pairs = 0, unsolved = 0;
    long worstMs = 0;
    for (int i = 0; i < pts.Count; i++)
    for (int j = i + 1; j < pts.Count; j++)
    {
        var (ax, ay) = pts[i];
        var (bx, by) = pts[j];
        if (comp[ay * g.WidthTiles + ax] != comp[by * g.WidthTiles + bx]) continue;
        var wa = g.TileToWorld(ax, ay);
        var wb = g.TileToWorld(bx, by);
        pairs++;
        var sw = Stopwatch.StartNew();
        bool solved = false;
        // START AT THE RUNNING HIGH-WATER MARK, not at BudgetStart.
        // The budget is a CAP on expansions, not work performed: a search that finds its path in 3,000 expansions
        // costs the same whether the cap was 25,000 or 8,000,000. Only FAILURES cost the full budget, because a
        // failure is what expanding everything and finding nothing means.
        // We are looking for a MAXIMUM, so the only question each pair has to answer is "do you need more than the
        // worst we have seen so far?" -- and the cheapest way to ask is to try that number first. Pairs at or below
        // it (almost all of them) cost one quick success and escalate nothing. Escalating from 25,000 every time
        // instead made every hard pair pay 25k + 50k + ... in failed searches before the one that worked.
        for (int budget = Math.Max(BudgetStart, maxBudget); budget <= BudgetCap; budget *= 2)
        {
            if (PathFinder.FindPath(g, wa.X, wa.Y, wb.X, wb.Y, budget).Count > 0)
            {
                if (budget > maxBudget) maxBudget = budget;
                solved = true;
                break;
            }
        }
        sw.Stop();
        // Connected by flood fill but unsolved at the cap: the pathfinder disagrees with 8-way connectivity
        // (clearance/margin rules are stricter than "tile is walkable"). Counted, not folded into the budget.
        if (!solved) unsolved++;
        if (sw.ElapsedMilliseconds > worstMs) worstMs = sw.ElapsedMilliseconds;
    }

    rows.Add($"{name},{g.WidthTiles}x{g.HeightTiles},{nComp},{pairs},{maxBudget},{worstMs},{swMap.ElapsedMilliseconds},{(unsolved > 0 ? $"unsolved={unsolved}" : "")}");
    var n = Interlocked.Increment(ref done);
    Console.Error.WriteLine($"[{n}/{files.Length}] {name} budget={maxBudget} pairs={pairs} {swMap.ElapsedMilliseconds}ms");
});

var sorted = rows.OrderBy(r => r.Split(',')[0], StringComparer.OrdinalIgnoreCase).ToList();
File.WriteAllLines(outPath, new[] { "map,tiles,components,pairsTested,maxBudget,worstPairMs,mapMs,notes" }.Concat(sorted));
Console.Error.WriteLine($"wrote {outPath} in {swAll.Elapsed.TotalMinutes:F1} min");
