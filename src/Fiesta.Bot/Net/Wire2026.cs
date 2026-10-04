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
///   batch 2: 0x244E / 0x2450 / 0x244F / 0x2451 the SKILLBASH *_START frames (+u32)
/// </summary>
public static class Wire2026
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("FIESTA_WIRE") == "2026";

    private static readonly ConcurrentDictionary<ushort, int> Mismatches = new();

    /// <summary>The opcodes the zone sends in a 2026 shape (bridge26 batches 1-3)</summary>
    private static readonly HashSet<ushort> Translated =
        [0x2448, 0x2449, 0x243C, 0x2452, 0x2402, 0x244E, 0x2450, 0x244F, 0x2451, 0x103A, 0x10D7,
         0x1038, 0x104A, 0x9003, 0x9004, 0x3C03, 0x3C04, 0x3C06, 0x3C09, 0x3C0A, 0x3C0B,
         0x1C06, 0x1C07, 0x1C08, 0x1C09, 0x1C1A,
         0x3001, 0x3002, 0x1047, 0x305B, 0x7492, 0x6814, 0x986E, 0x302D, 0x3C08, 0x305C, 0x4C10, 0xC407];

    /// <summary>The US build's briefinfo records are one byte longer than the German's (the zone's bridge26.ini build=us)</summary>
    private const int UsExtra = 1;

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
            0x244E => Tail4(p, 6),                               // HIT_OBJ_START
            0x2450 => Tail4(p, 12),                              // HIT_FLD_START
            0x244F => Tail4(p, 8),                               // SOMEONE_HIT_OBJ_START
            0x2451 => Tail4(p, 14),                              // SOMEONE_HIT_FLD_START
            0x103A => QuestList(p, p.Length >= 6 ? p[5] : -1),   // CLIENT_QUEST_DOING {chr u32, clear u8, count u8}
            0x10D7 => QuestList(p, p.Length >= 6 ? p[4] | (p[5] << 8) : -1),   // CLIENT_QUEST_REPEAT {chr u32, count u16}
            0x1038 => ClientBase(p),                             // CHAR_CLIENT_BASE (US 362 B)
            0x104A => ChargedBuff(p),
            0x9003 => p.Length == 22 ? p[..14].ToArray() : null,     // CHARGED_BUFFSTART
            0x9004 => p.Length == 5 ? p[..4].ToArray() : null,       // CHARGED_BUFFTERMINATE
            0x3C03 or 0x3C04 or 0x3C06 or 0x3C09 or 0x3C0A or 0x3C0B => ShopTable(p),
            0x1C08 => p.Length == 187 + UsExtra ? RegenMobRow(p) : null,           // BRIEFINFO_REGENMOB
            0x1C09 => Rows(p, 187 + UsExtra, 149, RegenMobRow),                   // BRIEFINFO_MOB {count u8} + rows
            0x1C1A => RegenMover(p),                                              // BRIEFINFO_REGENMOVER
            0x1C06 => p.Length == 304 + UsExtra ? LoginCharacterRow(p) : null,    // BRIEFINFO_LOGINCHARACTER
            0x1C07 => Rows(p, 304 + UsExtra, 235, LoginCharacterRow),             // BRIEFINFO_CHARACTER {count u8} + rows
            // batch 6: items (Wire2026Items - needs the item classes, set by the host from its client data)
            0x3001 => Wire2026Items.TrailingItem(p, 4),          // ITEM_CELLCHANGE
            0x3002 => Wire2026Items.TrailingItem(p, 3),          // ITEM_EQUIPCHANGE
            0x1047 => Wire2026Items.ClientItem(p),               // CHAR_CLIENT_ITEM
            0x305B => Wire2026Items.RecordList(p, 0, 3),
            0x7492 => Wire2026Items.RecordList(p, 18, 3),
            0x6814 => Wire2026Items.RecordList(p, 2, 15),
            0x986E => Wire2026Items.RecordList(p, 10, 3),
            0x302D => Wire2026Items.RecordList(p, 0, 3),
            0x3C08 => Wire2026Items.RecordList(p, 11, 3),
            0x305C => Wire2026Items.LeadingItem(p, 2),
            0x4C10 => Wire2026Items.LeadingItem(p, 1),
            0xC407 => Wire2026Items.LeadingItem(p, 3),
            _ => Array.Empty<byte>(),
        };
        if (legacy is { Length: 0 } && !Translated.Contains(pkt.Opcode))
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

    // the four SKILLBASH *_START frames: the 2016 struct + a trailing u32 (the zone sends 1)
    private static byte[]? Tail4(ReadOnlySpan<byte> p, int size2016) => p.Length == size2016 + 4 ? p[..size2016].ToArray() : null;

    // the quest lists: head 6, then n x 37 -> n x 32 (PLAYER_QUEST_INFO; the 5 added bytes dropped). A quest the zone moved
    // counters for (quest-counter-rows.txt) keeps its 2026 row order - the bot reads the status and id, not those rows.
    private static byte[]? QuestList(ReadOnlySpan<byte> p, int n)
    {
        if (n < 0 || p.Length != 6 + 37 * n) return null;
        var o = new byte[6 + 32 * n];
        p[..6].CopyTo(o);
        for (var i = 0; i < n; i++) p.Slice(6 + 37 * i, 32).CopyTo(o.AsSpan(6 + 32 * i));
        return o;
    }

    // CHAR_CLIENT_BASE the US 362 -> 105: the byte the 2026 build inserted at 54 removed, the zero padding dropped
    private static byte[]? ClientBase(ReadOnlySpan<byte> p)
    {
        if (p.Length != 362) return null;
        var o = new byte[105];
        p[..54].CopyTo(o);
        p.Slice(55, 105 - 54).CopyTo(o.AsSpan(54));
        return o;
    }

    // CHARGEDBUFF {u32 0, count u16} + n x 22 -> {count u16} + n x 14
    private static byte[]? ChargedBuff(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6) return null;
        int n = p[4] | (p[5] << 8);
        if (p.Length != 6 + 22 * n) return null;
        var o = new byte[2 + 14 * n];
        o[0] = p[4]; o[1] = p[5];
        for (var i = 0; i < n; i++) p.Slice(6 + 22 * i, 14).CopyTo(o.AsSpan(2 + 14 * i));
        return o;
    }

    // SHOPOPEN tables {itemnum u16, npc u16} + n x {slot u32, item u16} -> n x {slot u8, item u16}
    private static byte[]? ShopTable(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) return null;
        int n = p[0] | (p[1] << 8);
        if (p.Length != 4 + 6 * n) return null;
        var o = new byte[4 + 3 * n];
        p[..4].CopyTo(o);
        for (var i = 0; i < n; i++)
        {
            o[4 + 3 * i] = p[4 + 6 * i];
            o[5 + 3 * i] = p[8 + 6 * i];
            o[6 + 3 * i] = p[9 + 6 * i];
        }
        return o;
    }

    // REGENMOB row 187 + extra -> 149: the 37 + extra bytes after the abstate array at 114 and the last byte dropped
    private static byte[] RegenMobRow(ReadOnlySpan<byte> p)
    {
        var o = new byte[149];
        p[..114].CopyTo(o);
        p.Slice(151 + UsExtra, 35).CopyTo(o.AsSpan(114));
        return o;
    }

    // REGENMOVER 176 + extra -> 139
    private static byte[]? RegenMover(ReadOnlySpan<byte> p)
    {
        if (p.Length != 176 + UsExtra) return null;
        var o = new byte[139];
        p[..118].CopyTo(o);
        p.Slice(155 + UsExtra, 21).CopyTo(o.AsSpan(118));
        return o;
    }

    // LOGINCHARACTER 304 + extra -> 235 (the inverse of the zone's layout; the 2016 record's last byte is not carried: 0)
    private static byte[] LoginCharacterRow(ReadOnlySpan<byte> p)
    {
        var o = new byte[235];
        p[..82].CopyTo(o);
        p.Slice(113, 9).CopyTo(o.AsSpan(82));
        p.Slice(123, 99).CopyTo(o.AsSpan(91));
        p.Slice(258 + UsExtra, 44).CopyTo(o.AsSpan(190));
        return o;
    }

    private delegate byte[] RowFn(ReadOnlySpan<byte> row);

    // {count u8} + count rows of `width` -> {count u8} + rows of `legacy`
    private static byte[]? Rows(ReadOnlySpan<byte> p, int width, int legacy, RowFn fn)
    {
        if (p.Length < 1) return null;
        int n = p[0];
        if (p.Length != 1 + width * n) return null;
        var o = new byte[1 + legacy * n];
        o[0] = (byte)n;
        for (var i = 0; i < n; i++) fn(p.Slice(1 + width * i, width)).CopyTo(o, 1 + legacy * i);
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
