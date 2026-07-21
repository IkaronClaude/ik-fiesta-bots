namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// A map's <c>.bdt</c> collision data — a <b>sparse region quadtree of walkable cells</b> at
/// 50-world-unit (one Shine block) resolution, from <c>9Data/Shine/BlockInfo/&lt;Map&gt;.bdt</c>
/// (BYO). Only terrain/hill maps ship a <c>.bdt</c> (76 of 158); flat maps have none.
///
/// <para>Format (reverse-engineered 2026-07-21, see <c>docs/BDT_FORMAT.md</c>): no header, a flat
/// array of <b>9-byte nodes</b> — <c>x1:u16, x2:u16, y1:u16, y2:u16, flag:u8</c> (all-X-then-all-Y;
/// the node's box is <c>[min(x1,x2),max]×[min(y1,y2),max]</c>). <c>depth = flag/16</c>, node size =
/// <c>worldExtent / 2^depth</c>; <b>flag==128 = a depth-8 LEAF = a 50×50 WALKABLE cell</b>. Larger
/// nodes are internal structure; blocked regions are pruned (absent). So a point is walkable iff it
/// falls inside a leaf box.</para>
///
/// <para>This is a candidate for the <b>server's movement collision</b> — coarser than the 6.25-unit
/// <see cref="BlockGrid"/> (<c>.shbd</c>). Whether the server actually enforces this (vs the shbd) is
/// validated by the "measuring stick": correlating live MOVEFAIL points against both grids.</para>
/// </summary>
public sealed class BdtGrid
{
    /// <summary>World units per bdt leaf cell (one Shine block; .ini OneBlockWidth).</summary>
    public const int BlockWorld = 50;

    // Set of walkable blocks, key = ((long)blockY << 32) | (uint)blockX.
    private readonly HashSet<long> _walkBlocks;

    public int LeafCount => _walkBlocks.Count;
    /// <summary>Max block coordinate seen (world extent ≈ (this+1)*50), for diagnostics.</summary>
    public int MaxBlock { get; }

    private BdtGrid(HashSet<long> walkBlocks, int maxBlock)
    {
        _walkBlocks = walkBlocks;
        MaxBlock = maxBlock;
    }

    private static long Key(int bx, int by) => ((long)by << 32) | (uint)bx;

    /// <summary>Load a <c>.bdt</c>. Returns null if the file is missing (flat map — no bdt).</summary>
    public static BdtGrid? Load(string bdtPath)
    {
        if (!File.Exists(bdtPath)) return null;
        var b = File.ReadAllBytes(bdtPath);
        if (b.Length == 0 || b.Length % 9 != 0)
            throw new InvalidDataException($"{bdtPath}: length {b.Length} is not a multiple of 9 (not a .bdt node array)");
        var walk = new HashSet<long>();
        int maxBlock = 0;
        int n = b.Length / 9;
        for (int i = 0; i < n; i++)
        {
            int o = i * 9;
            if (b[o + 8] != 128) continue; // only depth-8 leaves are walkable cells
            int x1 = b[o] | (b[o + 1] << 8);
            int x2 = b[o + 2] | (b[o + 3] << 8);
            int y1 = b[o + 4] | (b[o + 5] << 8);
            int y2 = b[o + 6] | (b[o + 7] << 8);
            int xa = Math.Min(x1, x2), ya = Math.Min(y1, y2);
            int bx = xa / BlockWorld, by = ya / BlockWorld;
            walk.Add(Key(bx, by));
            if (bx > maxBlock) maxBlock = bx;
            if (by > maxBlock) maxBlock = by;
        }
        return new BdtGrid(walk, maxBlock);
    }

    /// <summary>Is world (x,y) inside a walkable bdt leaf cell (server-collision candidate)?</summary>
    public bool IsWalkableWorld(uint worldX, uint worldY)
        => _walkBlocks.Contains(Key((int)(worldX / BlockWorld), (int)(worldY / BlockWorld)));
}
