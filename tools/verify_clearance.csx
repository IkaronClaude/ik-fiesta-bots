#r "C:/Projects/ik-fiesta-bots/src/Fiesta.Bot/bin/Release/net10.0/Fiesta.Bot.dll"
using Fiesta.Bot.Pathfinding;

// Reference: the ORIGINAL byte-per-tile transform, capped at 3 (the packed map's cap).
// If the packed version is correct, every tile must agree exactly.
static byte[] Reference(BlockGrid g, byte cap)
{
    int W = g.WidthTiles, H = g.HeightTiles;
    var d = new byte[W * H];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) d[y*W+x] = g.IsWalkableTile(x,y) ? cap : (byte)0;
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
        int i = y*W+x; if (d[i]==0) continue; int b = d[i];
        if (x==0||y==0||x==W-1) b = Math.Min(b,1);
        if (x>0) b = Math.Min(b, d[i-1]+1);
        if (y>0) b = Math.Min(b, d[i-W]+1);
        if (x>0&&y>0) b = Math.Min(b, d[i-W-1]+1);
        if (x<W-1&&y>0) b = Math.Min(b, d[i-W+1]+1);
        d[i]=(byte)b;
    }
    for (int y = H-1; y >= 0; y--) for (int x = W-1; x >= 0; x--) {
        int i = y*W+x; if (d[i]==0) continue; int b = d[i];
        if (x==W-1||y==H-1||x==0) b = Math.Min(b,1);
        if (x<W-1) b = Math.Min(b, d[i+1]+1);
        if (y<H-1) b = Math.Min(b, d[i+W]+1);
        if (x<W-1&&y<H-1) b = Math.Min(b, d[i+W+1]+1);
        if (x>0&&y<H-1) b = Math.Min(b, d[i+W-1]+1);
        d[i]=(byte)b;
    }
    return d;
}

foreach (var map in new[]{"RouN","RouVal02","Eld"})
{
    var path = $"Z:/ServerSource/9Data/Shine/BlockInfo/{map}.shbd";
    if (!File.Exists(path)) { Console.WriteLine($"{map}: missing"); continue; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var g = BlockGrid.Load(path);
    var reference = Reference(g, 3);
    long mism = 0; int W = g.WidthTiles, H = g.HeightTiles;
    var hist = new long[4];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
        int got = g.ClearanceAt(x,y), want = reference[y*W+x];
        hist[Math.Min(got,3)]++;
        if (got != want) mism++;
    }
    Console.WriteLine($"{map,-10} {W}x{H} tiles={(long)W*H:N0}  MISMATCHES={mism}  " +
        $"clearance histogram 0/1/2/3+ = {hist[0]:N0}/{hist[1]:N0}/{hist[2]:N0}/{hist[3]:N0}  ({sw.ElapsedMilliseconds}ms)");
}
