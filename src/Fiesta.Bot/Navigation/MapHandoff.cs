using System.Buffers.Binary;
using System.Text;

namespace Fiesta.Bot.Navigation;

/// <summary>A map transition the zone server initiated, decoded from a MAP link command</summary>
public sealed record MapHandoff(
    ushort MapId, uint X, uint Y, bool IsCrossServer,
    string? Ip = null, int Port = 0, ushort WmHandle = 0)
{
    /// <summary>Parse a LINKSAME (0x1809) payload: [mapId u16][x u32][y u32]</summary>
    public static MapHandoff? ParseLinkSame(ReadOnlySpan<byte> p)
    {
        if (p.Length < 10) return null;
        var mapId = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var x = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        var y = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        return new MapHandoff(mapId, x, y, IsCrossServer: false);
    }

    /// <summary>Parse a LINKOTHER (0x180A) payload: [mapId u16][x u32][y u32][ip char[16]][port u16][wmHandle u16]</summary>
    public static MapHandoff? ParseLinkOther(ReadOnlySpan<byte> p)
    {
        if (p.Length < 30) return null;
        var mapId = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var x = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        var y = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        var ip = ReadCString(p.Slice(10, 16));
        var port = BinaryPrimitives.ReadUInt16LittleEndian(p[26..]);
        var wmHandle = BinaryPrimitives.ReadUInt16LittleEndian(p[28..]);
        return new MapHandoff(mapId, x, y, IsCrossServer: true, ip, port, wmHandle);
    }

    private static string ReadCString(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        if (end < 0) end = b.Length;
        return Encoding.ASCII.GetString(b[..end]);
    }
}
