namespace Fiesta.Bot.Pathfinding;

/// <summary>
/// A map's walkability grid, loaded from a server <c>.shbd</c> (Shine Block Data)
/// file under <c>9Data/Shine/BlockInfo/&lt;Map&gt;.shbd</c> (BYO at runtime).
///
/// Format (recovered + calibrated live 2026-06-11): 8-byte header = two LE u32
/// [bytesPerRow, height]; then <c>height</c> rows of <c>bytesPerRow</c> bytes,
/// <b>1 bit per tile</b>, LSB-first within a byte. <b>bit 0 = walkable, bit 1 =
/// blocked/void.</b> The playable map is a small region (~7% of tiles) inside a
/// large void of bit-1 padding.
///
/// <para>World↔tile is a plain linear scale with <b>no offset and no Y-flip</b>:
/// a Shine map-unit is 50 world units and the grid stores 8 tiles per map-unit, so
/// each tile spans 50/8 = <see cref="WorldPerTile"/> = 6.25 world units. The grid
/// therefore covers world [0, tiles·6.25] (Eld: 4096·6.25 = 25600 = 512 map-units ·
/// 50). Verified exactly against Eld live — every gate and landmark lines up.
///   tileX = worldX / 6.25
///   tileY = worldY / 6.25
///   walkable = ((row[tileX >> 3] >> (tileX &amp; 7)) &amp; 1) == 0
/// </para>
/// </summary>
public sealed class BlockGrid
{
    /// <summary>World units per tile (50 world per map-unit ÷ 8 tiles per map-unit).</summary>
    public const double WorldPerTile = 6.25;

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
        => IsWalkableTile((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));

    public bool IsWalkableTile(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        var bytePos = 8 + ty * _bytesPerRow + (tx >> 3);
        return ((_data[bytePos] >> (tx & 7)) & 1) == 0; // bit 0 = walkable
    }

    /// <summary>World coordinate of a tile's centre (for issuing move packets).</summary>
    public (uint X, uint Y) TileToWorld(int tx, int ty)
        => ((uint)((tx + 0.5) * WorldPerTile), (uint)((ty + 0.5) * WorldPerTile));

    public (int X, int Y) WorldToTile(uint worldX, uint worldY)
        => ((int)(worldX / WorldPerTile), (int)(worldY / WorldPerTile));
}
