using System.Buffers.Binary;

namespace Fiesta.Bot.Protocol;

/// <summary>The 2026 shapes of zone packets that are wider than the canonical (2016) layout the decoders read, and
/// their exact inverse. Each inverse first checks that the frame is exactly the 2026 shape; a frame of any other size
/// is reported (and passed on as it came) instead of being misread. All of these are low-frequency packets (lists,
/// briefinfo, items, quests); the per-swing combat frames are read width-aware by the decoders without a copy.</summary>
public static class Wire2026Shapes
{
    private const int UsExtra = Wire2026Profile.UsExtra;

    /// <summary>The canonical layout of a 2026-shaped packet; null when the opcode is not translated
    /// (<paramref name="translated"/> false) or when the frame is not the expected 2026 shape (true).</summary>
    public static byte[]? ToLegacy(ushort op, ReadOnlySpan<byte> p, Func<int, int> itemClass, out bool translated)
    {
        translated = true;
        switch (op)
        {
            case 0x2449 or 0x243C: return p.Length == 20 ? p[..13].ToArray() : null;      // SOMEONESWING_DAMAGE / DOTDAMAGE
            case 0x2452: return SkillHit(p);                                                // SKILLBASH_HIT_DAMAGE
            case 0x2402: return TargetInfo(p);
            case 0x244E: return Tail4(p, 6);                                                // HIT_OBJ_START
            case 0x2450: return Tail4(p, 12);                                               // HIT_FLD_START
            case 0x244F: return Tail4(p, 8);                                                // SOMEONE_HIT_OBJ_START
            case 0x2451: return Tail4(p, 14);                                               // SOMEONE_HIT_FLD_START
            case 0x103A: return QuestList(p, p.Length >= 6 ? p[5] : -1);                    // CLIENT_QUEST_DOING
            case 0x10D7: return QuestList(p, p.Length >= 6 ? p[4] | (p[5] << 8) : -1);     // CLIENT_QUEST_REPEAT
            case 0x1038: return ClientBase(p);                                              // CHAR_CLIENT_BASE
            case 0x104A: return ChargedBuff(p);
            case 0x9003: return p.Length == 22 ? p[..14].ToArray() : null;
            case 0x9004: return p.Length == 5 ? p[..4].ToArray() : null;
            case 0x3C03 or 0x3C04 or 0x3C06 or 0x3C09 or 0x3C0A or 0x3C0B: return ShopTable(p);
            case 0x1C08: return p.Length == 187 + UsExtra ? RegenMobRow(p) : null;
            case 0x1C09: return Rows(p, 187 + UsExtra, 149, RegenMobRow);
            case 0x1C1A: return RegenMover(p);
            case 0x1C06: return p.Length == 304 + UsExtra ? LoginCharacterRow(p) : null;
            case 0x1C07: return Rows(p, 304 + UsExtra, 235, LoginCharacterRow);
            case 0x3001: return Items.TrailingItem(p, 4, itemClass);                       // ITEM_CELLCHANGE
            case 0x3002: return Items.TrailingItem(p, 3, itemClass);                       // ITEM_EQUIPCHANGE
            case 0x1047: return Items.ClientItem(p, itemClass);                            // CHAR_CLIENT_ITEM
            case 0x305B: return Items.RecordList(p, 0, 3, itemClass);
            case 0x7492: return Items.RecordList(p, 18, 3, itemClass);
            case 0x6814: return Items.RecordList(p, 2, 15, itemClass);
            case 0x986E: return Items.RecordList(p, 10, 3, itemClass);
            case 0x302D: return Items.RecordList(p, 0, 3, itemClass);
            case 0x3C08: return Items.RecordList(p, 11, 3, itemClass);
            case 0x305C: return Items.LeadingItem(p, 2, itemClass);
            case 0x4C10: return Items.LeadingItem(p, 1, itemClass);
            case 0xC407: return Items.LeadingItem(p, 3, itemClass);
            default: translated = false; return null;
        }
    }

    private static byte[]? TargetInfo(ReadOnlySpan<byte> p)
    {
        if (p.Length != 41) return null;
        var o = new byte[30];
        p[..28].CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(28), (ushort)BinaryPrimitives.ReadUInt32LittleEndian(p[28..]));
        return o;
    }

    private static byte[]? Tail4(ReadOnlySpan<byte> p, int size2016) => p.Length == size2016 + 4 ? p[..size2016].ToArray() : null;

    private static byte[]? QuestList(ReadOnlySpan<byte> p, int n)
    {
        if (n < 0 || p.Length != 6 + 37 * n) return null;
        var o = new byte[6 + 32 * n];
        p[..6].CopyTo(o);
        for (var i = 0; i < n; i++) p.Slice(6 + 37 * i, 32).CopyTo(o.AsSpan(6 + 32 * i));
        return o;
    }

    // the US 362-byte CHAR_CLIENT_BASE: one byte inserted at 54, zero padding after the 2016 fields
    private static byte[]? ClientBase(ReadOnlySpan<byte> p)
    {
        if (p.Length != 362) return null;
        var o = new byte[105];
        p[..54].CopyTo(o);
        p.Slice(55, 105 - 54).CopyTo(o.AsSpan(54));
        return o;
    }

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

    // {itemnum u16, npc u16} + n x {slot u32, item u16} -> n x {slot u8, item u16}
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

    private static byte[] RegenMobRow(ReadOnlySpan<byte> p)
    {
        var o = new byte[149];
        p[..114].CopyTo(o);
        p.Slice(151 + UsExtra, 35).CopyTo(o.AsSpan(114));
        return o;
    }

    private static byte[]? RegenMover(ReadOnlySpan<byte> p)
    {
        if (p.Length != 176 + UsExtra) return null;
        var o = new byte[139];
        p[..118].CopyTo(o);
        p.Slice(155 + UsExtra, 21).CopyTo(o.AsSpan(118));
        return o;
    }

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

    /// <summary>Item records. A record is datasize u8 | location u16 | itemid u16 | attributes; its 2026 width depends
    /// on the item's class (client ItemInfo.Class): enchantable classes 4/5/6/7/8/38 carry one inserted byte, class 39
    /// carries 16 attribute bytes where the canonical record has its 1 lot byte, every other class is unchanged.</summary>
    public static class Items
    {
        private const int Head = 5;

        private static int EnchantableFixed(int cls) => cls switch { 4 => 39, 5 => 66, 6 or 7 or 8 or 38 => 14, _ => -1 };

        /// <summary>One record (datasize included); null = unknown class.</summary>
        public static byte[]? Record(ReadOnlySpan<byte> r, Func<int, int> classOf)
        {
            if (r.Length < Head) return null;
            var item = r[3] | (r[4] << 8);
            if (item == 0xFFFF) return r.ToArray();
            var cls = classOf(item);
            if (cls < 0) return null;
            var fx = EnchantableFixed(cls);
            if (fx > 0)
            {
                var ins = cls == 4 ? 8 : fx - 2;
                if (r.Length < Head + ins + 1) return null;
                var o = new byte[r.Length - 1];
                r[..(Head + ins)].CopyTo(o);
                r[(Head + ins + 1)..].CopyTo(o.AsSpan(Head + ins));
                o[0] = (byte)(o.Length - 1);
                return o;
            }
            if (cls == 39 && r.Length == Head + 16)
            {
                var o = r[..(Head + 1)].ToArray();
                o[0] = (byte)(o.Length - 1);
                return o;
            }
            return r.ToArray();
        }

        /// <summary>CHAR_CLIENT_ITEM {count u32, box, flag, records} -> {count u8, box, flag, records}.</summary>
        public static byte[]? ClientItem(ReadOnlySpan<byte> p, Func<int, int> classOf)
        {
            if (p.Length < 6) return null;
            var o = new List<byte>(p.Length) { p[0], p[4], p[5] };
            var at = 6;
            while (at < p.Length)
            {
                var len = p[at] + 1;
                if (len < Head || at + len > p.Length) return null;
                var rec = Record(p.Slice(at, len), classOf);
                if (rec is null) return null;
                o.AddRange(rec);
                at += len;
            }
            return o.ToArray();
        }

        /// <summary>header | count u32 | records | 4 zero -> header | count u8 | records | 1 zero.</summary>
        public static byte[]? RecordList(ReadOnlySpan<byte> p, int countAt, int itemAt, Func<int, int> classOf)
        {
            if (p.Length < countAt + 4) return null;
            int count = p[countAt];
            var o = new List<byte>(p.Length);
            o.AddRange(p[..countAt].ToArray());
            o.Add((byte)count);
            var at = countAt + 4;
            for (var k = 0; k < count; k++)
            {
                if (at >= p.Length) return null;
                var len = p[at] + 1;
                if (len < itemAt + 2 || at + len > p.Length) return null;
                var rec = LongRecord(p.Slice(at, len), itemAt, classOf);
                if (rec is null) return null;
                o.AddRange(rec);
                at += len;
            }
            if (p.Length - at != 4) return null;
            o.Add(0);
            return o.ToArray();
        }

        private static byte[]? LongRecord(ReadOnlySpan<byte> r, int itemAt, Func<int, int> classOf)
        {
            if (itemAt == Head - 2) return Record(r, classOf);
            var extra = itemAt - (Head - 2);
            var inner = new byte[r.Length - extra];
            r[itemAt..].CopyTo(inner.AsSpan(Head - 2));
            inner[0] = (byte)(inner.Length - 1);
            var t = Record(inner, classOf);
            if (t is null) return null;
            var o = new byte[t.Length + extra];
            r[..itemAt].CopyTo(o);
            t.AsSpan(Head - 2).CopyTo(o.AsSpan(itemAt));
            o[0] = (byte)(o.Length - 1);
            return o;
        }

        /// <summary>A packet whose last field is one item {itemid u16, attributes} at <paramref name="at"/>.</summary>
        public static byte[]? TrailingItem(ReadOnlySpan<byte> p, int at, Func<int, int> classOf)
        {
            var body = p.Length - at;
            if (body < 2) return null;
            var rec = new byte[body + Head - 2];
            rec[0] = (byte)(rec.Length - 1);
            p[at..].CopyTo(rec.AsSpan(Head - 2));
            var t = Record(rec, classOf);
            if (t is null) return null;
            var o = new byte[at + t.Length - (Head - 2)];
            p[..at].CopyTo(o);
            t.AsSpan(Head - 2).CopyTo(o.AsSpan(at));
            return o;
        }

        /// <summary>An item at <paramref name="at"/>, possibly padded to the full struct (then the packet keeps its size).</summary>
        public static byte[]? LeadingItem(ReadOnlySpan<byte> p, int at, Func<int, int> classOf)
        {
            if (p.Length < at + 2) return null;
            var id = p[at] | (p[at + 1] << 8);
            if (id == 0xFFFF) return p.ToArray();
            var cls = classOf(id);
            var fx = EnchantableFixed(cls);
            if (cls < 0) return null;
            if (fx < 0 && cls != 39) return p.ToArray();
            int width26 = fx > 0 ? fx + (p.Length >= at + 2 + fx ? p[at + 2 + fx - 1] >> 1 : 0) * 3 : 16;
            var used = Math.Min(p.Length, at + 2 + width26);
            var t = TrailingItem(p[..used], at, classOf);
            if (t is null) return null;
            if (used == p.Length) return t;
            var o = new byte[p.Length];
            t.CopyTo(o, 0);
            p[used..].CopyTo(o.AsSpan(t.Length));
            return o;
        }
    }
}
