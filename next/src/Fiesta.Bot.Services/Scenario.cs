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
        var p = BlockLayer.FindFile(clientRoot, mapName, folder ?? mapName, ".aid");
        if (p is null) return [];
        try { return Parse(File.ReadAllBytes(p)); } catch (IOException) { return []; }
    }
}
