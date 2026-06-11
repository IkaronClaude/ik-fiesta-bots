using System.Buffers.Binary;
using System.Text;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// A map transition the zone server initiated, decoded from a MAP link command.
/// The server sends one whenever the bot uses a gate (target+NPCClick) or a town
/// portal: first a <c>NC_MAP_LOGOUT_CMD</c> (0x1805), then one of —
/// <list type="bullet">
/// <item><b>LINKSAME</b> (0x1809, <see cref="IsCrossServer"/> = false): the new map
///   is hosted by the <i>same</i> zone process, so the TCP connection is reused.
///   Payload is just <c>[mapId u16][x u32][y u32]</c> (10 bytes). The bot only has
///   to re-seed its position and switch its block grid.</item>
/// <item><b>LINKOTHER</b> (0x180A, <see cref="IsCrossServer"/> = true): the new map
///   lives on a <i>different</i> zone server, so the bot must open a fresh
///   connection to <see cref="Ip"/>:<see cref="Port"/> and re-send MAP_LOGIN_REQ —
///   using <see cref="WmHandle"/> (the trailing u16) as the world-manager handle
///   the new zone validates against. The WM link stays open across the handoff.
///   Payload <c>[mapId u16][x u32][y u32][ip char[16]][port u16][wmHandle u16]</c>
///   (30 bytes).</item>
/// </list>
/// Decoded from Portals.pcapng (2026-06-11): RouN→Eld was a LINKOTHER to
/// 62.171.171.24:9019 spawning at (11802,10466) with wmHandle 0x7B0C; Eld→EldPri01
/// (and back) were LINKSAME on the same 9019 connection.
/// </summary>
public sealed record MapHandoff(
    ushort MapId, uint X, uint Y, bool IsCrossServer,
    string? Ip = null, int Port = 0, ushort WmHandle = 0)
{
    /// <summary>Parse a LINKSAME (0x1809) payload: <c>[mapId u16][x u32][y u32]</c>.</summary>
    public static MapHandoff? ParseLinkSame(ReadOnlySpan<byte> p)
    {
        if (p.Length < 10) return null;
        var mapId = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var x = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        var y = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        return new MapHandoff(mapId, x, y, IsCrossServer: false);
    }

    /// <summary>Parse a LINKOTHER (0x180A) payload:
    /// <c>[mapId u16][x u32][y u32][ip char[16]][port u16][wmHandle u16]</c>.</summary>
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
