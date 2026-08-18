namespace Fiesta.Bot.Pathfinding;

/// <summary>A map's .bdt collision data — a sparse region quadtree of walkable cells at 50-world-unit (one Shine block) re…</summary>
public sealed class BdtGrid
{
    /// <summary>World units per bdt leaf cell (one Shine block; .ini OneBlockWidth)</summary>
    public const int BlockWorld = 50;

    // Set of walkable blocks, key = ((long)blockY << 32) | (uint)blockX
    private readonly HashSet<long> _walkBlocks;

    public int LeafCount => _walkBlocks.Count;
    /// <summary>Max block coordinate seen (world extent ≈ (this+1)*50), for diagnostics</summary>
    public int MaxBlock { get; }

    private BdtGrid(HashSet<long> walkBlocks, int maxBlock)
    {
        _walkBlocks = walkBlocks;
        MaxBlock = maxBlock;
    }

    private static long Key(int bx, int by) => ((long)by << 32) | (uint)bx;

    /// <summary>Load a .bdt . Returns null if the file is missing (flat map — no bdt)</summary>
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
