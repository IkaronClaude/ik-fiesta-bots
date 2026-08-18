// Generate a pathfinding CORRECTNESS CORPUS: random walkable tile pairs per map, solved with the
// full 8M-expansion reference search. Any faster implementation must reproduce these results.
//
// Operator heuristic (2026-08-18): ASSUME ALL PATHS ARE POSSIBLE. Between two walkable tiles a route
// is presumed to exist, so a reference MISS is a limitation of the search, not proof of disconnection.
// Misses are recorded (found=false) but a candidate finding a path there is an IMPROVEMENT, not a failure.
//
//   dotnet script tools/path_corpus.csx -- <blockinfo-dir> <out.json> [pairsPerMap] [maps...]
//
// Output is DERIVED FROM GAME DATA -- keep it local, never commit it.
#r "C:/Projects/ik-fiesta-bots/src/Fiesta.Bot/bin/Release/net10.0/Fiesta.Bot.dll"
using Fiesta.Bot.Pathfinding;
using System.Diagnostics;
using System.Text.Json;

var argv = Args.ToArray();
string dir   = argv.Length > 0 ? argv[0] : "Z:/ServerSource/9Data/Shine/BlockInfo";
string outp  = argv.Length > 1 ? argv[1] : "pathcorpus.json";
int pairs    = argv.Length > 2 ? int.Parse(argv[2]) : 40;
string[] maps = argv.Length > 3 ? argv[3..] : new[]{ "RouVal02", "RouN", "Eld" };

var rows = new List<Dictionary<string, object>>();
foreach (var map in maps)
{
    var path = Path.Combine(dir, map + ".shbd");
    if (!File.Exists(path)) { Console.Error.WriteLine($"skip {map}: no .shbd"); continue; }
    var g = BlockGrid.Load(path);
    g.IsPathable(0, 0, 2);                       // build clearance outside the timings
    var rnd = new Random(map.GetHashCode() & 0x7fffffff);   // per-map deterministic

    var walkable = new List<(int tx, int ty)>();
    for (int i = 0; i < 4_000_000 && walkable.Count < pairs * 40; i++)
    {
        int tx = rnd.Next(g.WidthTiles), ty = rnd.Next(g.HeightTiles);
        if (g.IsPathable(tx, ty, 2)) walkable.Add((tx, ty));
    }
    Console.Error.WriteLine($"{map}: {g.WidthTiles}x{g.HeightTiles}, {walkable.Count} walkable samples");
    if (walkable.Count < 2) continue;

    for (int n = 0; n < pairs; n++)
    {
        var a = walkable[rnd.Next(walkable.Count)];
        var b = walkable[rnd.Next(walkable.Count)];
        if (a == b) continue;
        var (sx, sy) = g.TileToWorld(a.tx, a.ty);
        var (gx, gy) = g.TileToWorld(b.tx, b.ty);
        var sw = Stopwatch.StartNew();
        var p = PathFinder.FindPath(g, sx, sy, gx, gy);      // reference: default 8M
        sw.Stop();
        rows.Add(new Dictionary<string, object> {
            ["map"] = map, ["sx"] = sx, ["sy"] = sy, ["gx"] = gx, ["gy"] = gy,
            ["found"] = p.Count > 0, ["steps"] = p.Count,
            ["worldLen"] = Math.Round(PathLen(p), 1), ["refMs"] = sw.ElapsedMilliseconds,
        });
        Console.Error.WriteLine($"  {map} {n+1}/{pairs}: ({sx},{sy})->({gx},{gy}) found={p.Count>0} steps={p.Count} {sw.ElapsedMilliseconds}ms");
    }
}
File.WriteAllText(outp, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
var hit = rows.Count(r => (bool)r["found"]);
Console.Error.WriteLine($"\nwrote {rows.Count} cases to {outp} ({hit} solved, {rows.Count-hit} reference MISSES -- treated as 'path assumed to exist')");
Console.Error.WriteLine($"reference total {rows.Sum(r => (long)(int)(long)Convert.ToInt64(r["refMs"]))}ms");

static double PathLen(IReadOnlyList<(uint X, uint Y)> p)
{
    double d = 0;
    for (int i = 1; i < p.Count; i++) d += Math.Sqrt(Math.Pow((double)p[i].X - p[i-1].X, 2) + Math.Pow((double)p[i].Y - p[i-1].Y, 2));
    return d;
}
