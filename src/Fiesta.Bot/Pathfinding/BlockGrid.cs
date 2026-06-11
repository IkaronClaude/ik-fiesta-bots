namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// A map's walkability grid, loaded from a server <c>.shbd</c> (Shine Block Data)
/// file under <c>9Data/Shine/BlockInfo/&lt;Map&gt;.shbd</c> (BYO at runtime).
///
/// Format (recovered 2026-06-11): 8-byte header = two LE u32 [bytesPerRow, height];
/// then <c>height</c> rows of <c>bytesPerRow</c> bytes, **1 bit per tile**. Tiles
/// are 8 world units wide. The grid is Y-flipped vs world Y, and the bit order
/// within a byte is LSB-first. **bit 1 = walkable, bit 0 = blocked** — verified
/// live (2026-06-11): a character standing in Eld reads bit=1 with a fully-open
/// neighborhood, and open plazas across maps are the ~92% bit=1 majority.
///   tileX = worldX >> 3
///   tileY = (height-1) - (worldY >> 3)
///   walkable = (row[tileX >> 3] >> (tileX &amp; 7)) &amp; 1
/// </summary>
public sealed class BlockGrid
{
    public const int TileSize = 8;

    private readonly byte[] _data;
    private readonly int _bytesPerRow;

    public int WidthTiles { get; }
    public int HeightTiles { get; }

    private BlockGrid(byte[] data, int bytesPerRow, int height)
    {
        _data = data;
        _bytesPerRow = bytesPerRow;
        HeightTiles = height;
        WidthTiles = bytesPerRow * 8;
    }

    public static BlockGrid Load(string shbdPath)
    {
        var b = File.ReadAllBytes(shbdPath);
        if (b.Length < 8) throw new InvalidDataException($"{shbdPath}: too short for a .shbd header");
        var bytesPerRow = BitConverter.ToInt32(b, 0);
        var height = BitConverter.ToInt32(b, 4);
        var need = 8L + (long)bytesPerRow * height;
        if (bytesPerRow <= 0 || height <= 0 || b.Length < need)
            throw new InvalidDataException($"{shbdPath}: bad .shbd dims {bytesPerRow}x{height} for {b.Length} bytes");
        return new BlockGrid(b, bytesPerRow, height);
    }

    /// <summary>Is the tile at world (x,y) walkable? Out-of-bounds = blocked.</summary>
    public bool IsWalkableWorld(uint worldX, uint worldY)
        => IsWalkableTile((int)(worldX >> 3), HeightTiles - 1 - (int)(worldY >> 3));

    public bool IsWalkableTile(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        var bytePos = 8 + ty * _bytesPerRow + (tx >> 3);
        return ((_data[bytePos] >> (tx & 7)) & 1) == 1;
    }

    /// <summary>World coordinate of a tile's centre (for issuing move packets).</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)(tx * TileSize + TileSize / 2),
            (uint)((HeightTiles - 1 - ty) * TileSize + TileSize / 2));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX >> 3), HeightTiles - 1 - (int)(worldY >> 3));
}
