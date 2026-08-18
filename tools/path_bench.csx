// Validate + benchmark a candidate pathfinder against the reference corpus from path_corpus.csx.
//
// Correctness rule (operator, 2026-08-18): ASSUME ALL PATHS POSSIBLE. Failing to find a path the reference
// found is a REGRESSION. Finding one the reference missed is an improvement. A longer path is a quality
// warning, not a failure -- but it is reported, because a corridor that pushes routes sideways is a bug.
//
//   dotnet script tools/path_bench.csx -- <corpus.json> <blockinfo-dir>
#r "C:/Projects/ik-fiesta-bots/src/Fiesta.Bot/bin/Release/net10.0/Fiesta.Bot.dll"
using Fiesta.Bot.Pathfinding;
using System.Diagnostics;
using System.Text.Json;

var argv = Args.ToArray();
string corpusPath = argv.Length > 0 ? argv[0] : "pathcorpus.json";
string dir        = argv.Length > 1 ? argv[1] : "Z:/ServerSource/9Data/Shine/BlockInfo";

var doc = JsonDocument.Parse(File.ReadAllText(corpusPath));
var grids = new Dictionary<string, BlockGrid>();
BlockGrid G(string m)
{
    if (grids.TryGetValue(m, out var g)) return g;
    g = BlockGrid.Load(Path.Combine(dir, m + ".shbd"));
    g.IsPathable(0, 0, 2);
    return grids[m] = g;
}

// Warm the coarse levels so the one-off build is not charged to the first case
var sww = Stopwatch.StartNew();
foreach (var m in doc.RootElement.EnumerateArray().Select(e => e.GetProperty("map").GetString()!).Distinct())
    foreach (var mar in new[]{2.0,1.5,1.0,0.5,0.0}) CoarsePathFinder.Route(G(m), 0, 0, 0, 0, mar);
sww.Stop();
Console.WriteLine($"coarse build (all maps x all margins): {sww.ElapsedMilliseconds}ms one-off\n");

string lastTrace = "";
PathFinder.OnTrace = t => lastTrace = t;
PathFinder.OnFallback = _ => {};
var winStats = new Dictionary<string,int>();
var winMs = new Dictionary<string,long>();
int regress = 0, gained = 0, longer = 0, n = 0;
long tRef = 0, tNew = 0; var times = new List<long>();
foreach (var e in doc.RootElement.EnumerateArray())
{
    string map = e.GetProperty("map").GetString()!;
    uint sx = e.GetProperty("sx").GetUInt32(), sy = e.GetProperty("sy").GetUInt32();
    uint gx = e.GetProperty("gx").GetUInt32(), gy = e.GetProperty("gy").GetUInt32();
    bool refFound = e.GetProperty("found").GetBoolean();
    double refLen = e.GetProperty("worldLen").GetDouble();
    tRef += e.GetProperty("refMs").GetInt64();

    var g = G(map);
    var sw = Stopwatch.StartNew();
    var p = PathFinder.FindPathFast(g, sx, sy, gx, gy);
    sw.Stop();
    tNew += sw.ElapsedMilliseconds; times.Add(sw.ElapsedMilliseconds); n++;

    var key = lastTrace.StartsWith("win") ? string.Join(" ", lastTrace.Split(' ')[..2]) : (lastTrace.StartsWith("FALLBACK") ? "FALLBACK" : "short-route");
    if (key == "FALLBACK") Console.WriteLine($"  FB {map} ({sx},{sy})->({gx},{gy}) refMs={e.GetProperty("refMs").GetInt64()} candMs={sw.ElapsedMilliseconds} refSteps={e.GetProperty("steps").GetInt32()}");
    winStats[key] = winStats.GetValueOrDefault(key) + 1;
    winMs[key] = winMs.GetValueOrDefault(key) + sw.ElapsedMilliseconds;
    lastTrace = "";
    bool found = p.Count > 0;
    double len = 0; for (int i = 1; i < p.Count; i++) len += Math.Sqrt(Math.Pow((double)p[i].X-p[i-1].X,2)+Math.Pow((double)p[i].Y-p[i-1].Y,2));
    if (refFound && !found) { regress++; Console.WriteLine($"  REGRESSION {map} ({sx},{sy})->({gx},{gy}): reference found, candidate did NOT"); }
    else if (!refFound && found) gained++;
    else if (refFound && found && len > refLen * 1.25)
    { longer++; Console.WriteLine($"  longer  {map} ({sx},{sy})->({gx},{gy}): {len:F0} vs ref {refLen:F0} (+{(len/refLen-1)*100:F0}%)"); }
}
times.Sort();
Console.WriteLine($"\ncases={n}  REGRESSIONS={regress}  gained={gained}  >25% longer={longer}");
Console.WriteLine($"reference total {tRef}ms  ->  candidate total {tNew}ms  ({(tRef==0?0:(double)tRef/Math.Max(1,tNew)):F1}x)");
Console.WriteLine("");
Console.WriteLine("where time goes (outcome -> cases, total ms):");
foreach (var kv in winMs.OrderByDescending(kv => kv.Value)) Console.WriteLine($"   {kv.Key,-22} {winStats[kv.Key],4} cases  {kv.Value,7}ms");
Console.WriteLine($"candidate ms: median={times[times.Count/2]} p90={times[(int)(times.Count*0.9)]} max={times[^1]}");
