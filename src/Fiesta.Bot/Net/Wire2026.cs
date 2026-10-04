using System.Buffers.Binary;
using System.Collections.Concurrent;
using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>
/// The bot as a 2026-shape client, for testing the zone's own 2026 translation (ik-fiesta-patch-recipes
/// zone/plugins/bridge26, hooks\bridge26.ini send=2026; operator 2026-10-04: "test it with the bots project directly,
/// so I don't have to log in 20x over").
///
/// FIESTA_WIRE=2026 (host environment) turns it on: every inbound packet the zone now sends in its 2026 shape is turned
/// back into the 2016 layout the bot's parsers read, AFTER checking it is exactly the 2026 shape - so a zone that sends
/// a wrong width shows up here (logged once per opcode, then the packet passes as it came) instead of as a quiet
/// misparse. The bot still SENDS 2016 shapes; the zone accepts either.
///
/// Mirrors the zone plugin's batches; each inverse is the exact inverse of the proxy's T.* translator.
///   batch 1: 0x2448 SWING_DAMAGE, 0x2449 SOMEONESWING_DAMAGE, 0x243C DOTDAMAGE, 0x2452 SKILLBASH_HIT_DAMAGE, 0x2402 TARGETINFO
/// </summary>
public static class Wire2026
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("FIESTA_WIRE") == "2026";

    private static readonly ConcurrentDictionary<ushort, int> Mismatches = new();

    /// <summary>The 2016-layout packet for a 2026-shape one; the packet itself when this opcode is not translated.</summary>
    public static FiestaPacket ToLegacy(FiestaPacket pkt, Action<string>? log = null)
    {
        if (!Enabled) return pkt;
        var p = pkt.Payload.Span;
        byte[]? legacy = pkt.Opcode switch
        {
            0x2448 => Swing(p),
            0x2449 or 0x243C => Tail7(p),
            0x2452 => SkillHit(p),
            0x2402 => TargetInfo(p),
            _ => Array.Empty<byte>(),
        };
        if (legacy is { Length: 0 } && pkt.Opcode is not (0x2448 or 0x2449 or 0x243C or 0x2452 or 0x2402))
            return pkt;                                          // not a translated opcode
        if (legacy is null)
        {
            if (Mismatches.AddOrUpdate(pkt.Opcode, 1, (_, n) => n + 1) <= 3)
                log?.Invoke($"[wire2026] 0x{pkt.Opcode:X4} {p.Length} B is NOT the 2026 shape - passed through (zone send=2016, or a wrong width)");
            return pkt;
        }
        return new FiestaPacket(pkt.Opcode, legacy);
    }

    // 25 -> 16: attacker, defender, flag (6); damage u32 -> u16; resthp, order, index, sequence (8); 7 trailing bytes dropped
    private static byte[]? Swing(ReadOnlySpan<byte> p)
    {
        if (p.Length != 25) return null;
        var o = new byte[16];
        p[..6].CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(6), (ushort)BinaryPrimitives.ReadUInt32LittleEndian(p[6..]));
        p.Slice(10, 8).CopyTo(o.AsSpan(8));
        return o;
    }

    // 41 -> 30: the head, hpchangeorder u32 -> u16, nine trailing bytes dropped
    private static byte[]? TargetInfo(ReadOnlySpan<byte> p)
    {
        if (p.Length != 41) return null;
        var o = new byte[30];
        p[..28].CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(28), (ushort)BinaryPrimitives.ReadUInt32LittleEndian(p[28..]));
        return o;
    }

    // 20 -> 13: the 2016 layout, 7 trailing bytes dropped
    private static byte[]? Tail7(ReadOnlySpan<byte> p) => p.Length == 20 ? p[..13].ToArray() : null;

    // head 5 + skill u16 + 0xFFFF + n x 21 -> head 5 + n x 14
    private static byte[]? SkillHit(ReadOnlySpan<byte> p)
    {
        if (p.Length < 9) return null;
        int n = p[4];
        if (p.Length != 9 + 21 * n) return null;
        var o = new byte[5 + 14 * n];
        p[..5].CopyTo(o);
        for (var i = 0; i < n; i++) p.Slice(9 + 21 * i, 14).CopyTo(o.AsSpan(5 + 14 * i));
        return o;
    }
}
