using System.Diagnostics;
using Fiesta.Bot.Pathfinding;

// Decompose every map into convex (rectangle) regions and cache the mesh.
// Local pathing inside a convex region is a straight line, so only the region graph needs searching -- on Sand Hill
// that is 7,854 nodes instead of 1,200,362 tiles.
var blockInfo = args.Length > 0 ? args[0] : "Z:/ServerSource/9Data/Shine/BlockInfo";
var outDir = args.Length > 1 ? args[1] : "navmeshes";
double margin = args.Length > 2 ? double.Parse(args[2]) : 2;
Directory.CreateDirectory(outDir);
var files = Directory.GetFiles(blockInfo, "*.shbd").OrderBy(f => f).ToArray();
Console.Error.WriteLine($"decomposing {files.Length} map(s) at margin {margin} -> {outDir}");
int done = 0; long totRects = 0, totEdges = 0;
var swAll = Stopwatch.StartNew();
object gate = new();
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
{
    var name = Path.GetFileNameWithoutExtension(f);
    BlockGrid g;
    try { g = BlockGrid.Load(f); } catch (Exception e) { Console.Error.WriteLine($"{name}: LOAD FAILED {e.GetType().Name}"); return; }
    var sw = Stopwatch.StartNew();
    // Attach the map's .sbi so door boxes become their own regions and get tagged -- without this the cache would
    // be door-blind and a closed door would be invisible to routing.
    try { g.AttachDoors(DoorCollision.Load(Path.Combine(blockInfo, name + ".sbi"))); } catch { }
    var mesh = g.Mesh(margin);
    sw.Stop();
    var path = Path.Combine(outDir, name + ".navmesh");
    mesh.Save(path);
    int edges = 0;
    for (int i = 0; i < mesh.Portals.Count; i++) foreach (var p in mesh.Portals[i]) if (p.To > i) edges++;
    lock (gate) { totRects += mesh.Rects.Count; totEdges += edges; }
    var n = Interlocked.Increment(ref done);
    Console.Error.WriteLine($"[{n}/{files.Length}] {name,-18} {g.WidthTiles}x{g.HeightTiles} rects={mesh.Rects.Count,7:N0} edges={edges,7:N0} {sw.ElapsedMilliseconds,5}ms {new FileInfo(path).Length/1024,6}KB");
});
Console.Error.WriteLine($"done in {swAll.Elapsed.TotalSeconds:F1}s; {totRects:N0} regions, {totEdges:N0} edges total");
