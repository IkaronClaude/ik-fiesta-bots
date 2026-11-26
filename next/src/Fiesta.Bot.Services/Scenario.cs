using System.Buffers.Binary;
using System.Text;

namespace Fiesta.Bot.Services;

/// <summary>A named trigger area of an instance map (the client's .aid file next to the map's block files): centre and
/// half extents in world units (a circle has both extents = its radius).</summary>
public sealed record ScenarioArea(string Name, double X, double Y, double HalfX, double HalfY)
{
    /// <summary>Is a point inside the area's box?</summary>
    public bool Contains(double x, double y) => Math.Abs(x - X) <= HalfX && Math.Abs(y - Y) <= HalfY;
}

/// <summary>A door of an instance map (the client's .sbi): its tile box and one walkability bitmap per door state
/// (the state the server sends selects the bitmap; set bit = blocked, rows of width / 8 bytes, low bit first).</summary>
public sealed record MapDoor(string Name, int X0, int Y0, int X1, int Y1, int RowBytes, byte[][] States)
{
    /// <summary>Is tile (tx, ty) blocked in a door state? False outside the box or for an unknown state.</summary>
    public bool Blocked(int state, int tx, int ty)
    {
        if ((uint)state >= (uint)States.Length || tx < X0 || tx > X1 || ty < Y0 || ty > Y1) return false;
        int lx = tx - X0, i = (ty - Y0) * RowBytes + (lx >> 3);
        return i < States[state].Length && ((States[state][i] >> (lx & 7)) & 1) != 0;
    }
}

/// <summary>Reads .sbi files: u32 count, count x {name[32], x0, y0, x1, y1 (tiles, inclusive), size, offset (u32)}, then
/// per door at headers end + offset two bitmaps of size bytes (door state 0, then 1). Checked on client maps: the state 1
/// bitmap equals the map's .shbd, and the server opens a room's door by switching it from 0 to 1.</summary>
public static class MapDoorFile
{
    /// <summary>Parse (empty when malformed).</summary>
    public static IReadOnlyList<MapDoor> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<MapDoor>();
        if (b.Length < 4) return list;
        var n = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b), 64);
        var headEnd = 4 + n * 56;
        if (headEnd > b.Length) return list;
        for (var i = 0; i < n; i++)
        {
            var h = b.Slice(4 + i * 56, 56);
            var z = h[..32].IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(z < 0 ? h[..32] : h[..z]);
            int x0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[32..]), y0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[36..]);
            int x1 = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[40..]), y1 = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[44..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[48..]), at = headEnd + (int)BinaryPrimitives.ReadUInt32LittleEndian(h[52..]);
            if (x1 < x0 || y1 < y0 || size <= 0 || at + 2L * size > b.Length) continue;
            list.Add(new MapDoor(name, x0, y0, x1, y1, (x1 - x0 + 1) / 8, [b.Slice(at, size).ToArray(), b.Slice(at + size, size).ToArray()]));
        }
        return list;
    }

    /// <summary>Load a map's doors from a client root (empty when the map has none).</summary>
    public static IReadOnlyList<MapDoor> Load(string clientRoot, string mapName, string? folder = null)
    {
        var p = BlockLayer.FindFile(clientRoot, mapName, folder, ".sbi");
        if (p is null) return [];
        try { return Parse(File.ReadAllBytes(p)); } catch (IOException) { return []; }
    }
}

/// <summary>Reads .aid files: u32 count, then per area {name[32], shape u32 (0 circle {x, y, r}, else box {x, y, halfU,
/// halfV, angle}), all f32}.</summary>
public static class ScenarioAreaFile
{
    /// <summary>Parse the bytes of a .aid file (empty when malformed).</summary>
    public static IReadOnlyList<ScenarioArea> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<ScenarioArea>();
        if (b.Length < 4) return list;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(b);
        var off = 4;
        for (var i = 0; i < n && off + 32 + 4 + 12 <= b.Length; i++)
        {
            var name = b.Slice(off, 32); var z = name.IndexOf((byte)0);
            var s = Encoding.ASCII.GetString(z < 0 ? name : name[..z]);
            off += 32;
            var shape = BinaryPrimitives.ReadUInt32LittleEndian(b[off..]); off += 4;
            double x = F(b, ref off), y = F(b, ref off);
            if (shape == 0) { var r = F(b, ref off); list.Add(new ScenarioArea(s, x, y, r, r)); }
            else
            {
                if (off + 12 > b.Length) break;
                double u = F(b, ref off), v = F(b, ref off); F(b, ref off);
                list.Add(new ScenarioArea(s, x, y, u, v));
            }
        }
        return list;
    }

    private static float F(ReadOnlySpan<byte> b, ref int off) { var v = BinaryPrimitives.ReadSingleLittleEndian(b[off..]); off += 4; return v; }

    /// <summary>Load a map's areas from a client root (empty when the map has no .aid).</summary>
    public static IReadOnlyList<ScenarioArea> Load(string clientRoot, string mapName, string? folder = null)
    {
        var p = BlockLayer.FindFile(clientRoot, mapName, folder, ".aid");
        if (p is null) return [];
        try { return Parse(File.ReadAllBytes(p)); } catch (IOException) { return []; }
    }
}
