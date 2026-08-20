using System.Diagnostics;
using System.IO.Compression;
using Fiesta.Bot.Pathfinding;

// PRECOMPUTED CONNECTIVITY ("ISLAND") MAPS
//
// Reachability is the expensive question, and A* is the worst possible tool for it: proving NO path exists forces
// it to expand everything, and with a weighted heuristic it re-expands closed nodes on top. Measured on EldGbl02
// (2048x2048): A* took 14,455ms to conclude "unreachable" where a plain BFS answered in 290ms, and the live cost
// of that shape was a 357-SECOND stall on a 511-unit walk.
//
// So precompute it. One flood fill per map, cached to disk, and the question becomes an integer compare.
//
// THE PROPERTY THAT MAKES THIS SOUND, and the reason it is worth caching from the BASE .shbd:
// runtime MOVEFAIL blocks and closed .sbi doors can only ever REMOVE connectivity, never add it. So
//     island[a] != island[b]  =>  DEFINITELY unreachable, return immediately, no search
//     island[a] == island[b]  =>  maybe reachable, still needs the search
// A one-way test -- and the direction it gives away for free is exactly the expensive one.
//
// LAYOUT: 3 bits per tile, same resolution as the .shbd.
//   0..6  the seven largest islands of >= MinIslandTiles, 0 = largest
//   7     UNKNOWN: a blocked tile, or an island too small / too far down the ranking to track
// Three bits gives eight slots and the operator asked for the eight largest. One is spent on UNKNOWN instead,
// because without it an untracked tile has to borrow a real island's index -- which keeps the test safe (it only
// ever costs a search that was avoidable) but makes the DATA claim a tile is somewhere it is not. An explicit
// unknown is also why 0 stays a real island index rather than a sentinel.

var blockInfo = args.Length > 0 ? args[0] : "Z:/ServerSource/9Data/Shine/BlockInfo";
var outDir = args.Length > 1 ? args[1] : "island-maps";
const int MinIslandTiles = 100;
const int Tracked = 7;              // 0..6; 7 is UNKNOWN
const byte Unknown = 7;

Directory.CreateDirectory(outDir);
var files = Directory.GetFiles(blockInfo, "*.shbd").OrderBy(f => f).ToArray();
Console.Error.WriteLine($"building island maps for {files.Length} map(s) -> {outDir}");

int wantedMore = 0;
var swAll = Stopwatch.StartNew();
object gate = new();

Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
{
    var name = Path.GetFileNameWithoutExtension(f);
    BlockGrid g;
    try { g = BlockGrid.Load(f); }
    catch (Exception e) { Console.Error.WriteLine($"{name}: LOAD FAILED {e.GetType().Name}"); return; }
    int W = g.WidthTiles, H = g.HeightTiles;
    var sw = Stopwatch.StartNew();

    // --- label every component ---
    var comp = new int[W * H];
    var sizes = new List<int> { 0 };                       // 1-based; index 0 unused
    var boxes = new List<(int minX, int minY, int maxX, int maxY)> { (0, 0, 0, 0) };
    var q = new Queue<int>();
    ReadOnlySpan<int> dx = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
    ReadOnlySpan<int> dy = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };
    int n = 0;
    for (int sy = 0; sy < H; sy++)
    for (int sx = 0; sx < W; sx++)
    {
        int si = sy * W + sx;
        if (comp[si] != 0 || !g.IsWalkableTile(sx, sy)) continue;
        n++; comp[si] = n; q.Enqueue(si);
        int size = 0, mnx = sx, mny = sy, mxx = sx, mxy = sy;
        while (q.Count > 0)
        {
            int cur = q.Dequeue(); size++;
            int cx = cur % W, cy = cur / W;
            if (cx < mnx) mnx = cx; if (cx > mxx) mxx = cx;
            if (cy < mny) mny = cy; if (cy > mxy) mxy = cy;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                int ni = ny * W + nx;
                if (comp[ni] != 0 || !g.IsWalkableTile(nx, ny)) continue;
                comp[ni] = n; q.Enqueue(ni);
            }
        }
        sizes.Add(size);
        boxes.Add((mnx, mny, mxx, mxy));
    }

    // --- rank, keep the biggest that clear the floor ---
    var kept = Enumerable.Range(1, n)
                         .Where(c => sizes[c] >= MinIslandTiles)
                         .OrderByDescending(c => sizes[c])
                         .ToList();
    if (kept.Count > Tracked) { lock (gate) wantedMore++; }
    var slot = new byte[n + 1];
    Array.Fill(slot, Unknown);
    for (int i = 0; i < kept.Count && i < Tracked; i++) slot[kept[i]] = (byte)i;
    int stored = Math.Min(kept.Count, Tracked);

    // --- pack 3bpp ---
    long bits = (long)W * H * 3;
    var packed = new byte[(bits + 7) / 8];
    for (int i = 0; i < W * H; i++)
    {
        byte v = comp[i] == 0 ? Unknown : slot[comp[i]];
        long bit = (long)i * 3;
        int by = (int)(bit >> 3), sh = (int)(bit & 7);
        packed[by] |= (byte)(v << sh);
        if (sh > 5) packed[by + 1] |= (byte)(v >> (8 - sh));   // straddles the byte boundary
    }

    using var fs = File.Create(Path.Combine(outDir, name + ".islands"));
    using var bw = new BinaryWriter(fs);
    bw.Write(new[] { (byte)'F', (byte)'I', (byte)'S', (byte)'L' });
    bw.Write((ushort)1);                 // version
    bw.Write((ushort)W); bw.Write((ushort)H);
    bw.Write((byte)stored);
    bw.Write((byte)0);                   // reserved
    for (int i = 0; i < stored; i++)
    {
        var b = boxes[kept[i]];
        bw.Write((ushort)b.minX); bw.Write((ushort)b.minY);
        bw.Write((ushort)b.maxX); bw.Write((ushort)b.maxY);
        bw.Write((uint)sizes[kept[i]]);
    }
    // DEFLATE the plane. Uncompressed, 158 maps come to 404MB -- a 4096x4096 map is 6.3MB of 3bpp on its own --
    // and the data is mostly long runs of one value, which is exactly what deflate is for. The bounding boxes and
    // header stay raw so a reader can size and locate an island without inflating anything.
    bw.Write((uint)packed.Length);            // inflated length, so the reader can allocate once
    using (var z = new DeflateStream(fs, CompressionLevel.Optimal, true)) z.Write(packed, 0, packed.Length);
    bw.Flush();

    Console.Error.WriteLine($"{name,-18} {W}x{H} islands={n,4} kept={stored} " +
        $"(>= {MinIslandTiles}: {kept.Count}) sizes={string.Join(",", kept.Take(stored).Select(c => sizes[c]))} {sw.ElapsedMilliseconds}ms");
});

Console.Error.WriteLine($"done in {swAll.Elapsed.TotalSeconds:F1}s; {wantedMore} map(s) had MORE than {Tracked} islands >= {MinIslandTiles} tiles");
