namespace Fiesta.Bot.Net;

/// <summary>
/// The item records the zone now sends in their 2026 shape (bridge26 batch 6) back to 2016 - the exact inverse of the zone's
/// bridge26_items.h / the proxy's ItemAttr. A record is datasize u8 | location u16 | itemid u16 | attributes, and its
/// 2026 width depends on the item's CLASS (the 2026 client's ItemInfo.Class - here the bot's own client data, set by the
/// host as <see cref="ClassOf"/>):
///   classes 4/5/6/7/8/38 (enchantable): 2026 inserted ONE byte - before the count byte, the amulet (4) at attribute 8;
///   class 39 (tickets / keys): the zone sends 16 attribute bytes where the 2016 record has its 1 lot byte;
///   every other class: the same width on both wires.
/// </summary>
public static class Wire2026Items
{
    /// <summary>The item's 2026 attribute class, -1 = unknown (then the packet passes as it came).</summary>
    public static Func<int, int> ClassOf { get; set; } = _ => -1;

    private const int Head = 5;

    private static int EnchantableFixed(int cls) => cls switch { 4 => 39, 5 => 66, 6 or 7 or 8 or 38 => 14, _ => -1 };

    /// <summary>One 2026 record (datasize included) as the 2016 one; null = unknown class (left as it is).</summary>
    public static byte[]? Record(ReadOnlySpan<byte> r)
    {
        if (r.Length < Head) return null;
        var item = r[3] | (r[4] << 8);
        if (item == 0xFFFF) return r.ToArray();
        var cls = ClassOf(item);
        if (cls < 0) return null;
        var fx = EnchantableFixed(cls);
        if (fx > 0)
        {
            var ins = cls == 4 ? 8 : fx - 2;                 // where the zone put the 2026 byte, in 2016 attribute terms
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
    public static byte[]? ClientItem(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6) return null;
        var o = new List<byte>(p.Length) { p[0], p[4], p[5] };
        var at = 6;
        while (at < p.Length)
        {
            var len = p[at] + 1;
            if (len < Head || at + len > p.Length) return null;
            var rec = Record(p.Slice(at, len));
            if (rec is null) return null;
            o.AddRange(rec);
            at += len;
        }
        return o.ToArray();
    }

    /// <summary>header | count u32 | records | 4 zero  ->  header | count u8 | records | 1 zero.</summary>
    public static byte[]? RecordList(ReadOnlySpan<byte> p, int countAt, int itemAt)
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
            var rec = LongRecord(p.Slice(at, len), itemAt);
            if (rec is null) return null;
            o.AddRange(rec);
            at += len;
        }
        if (p.Length - at != 4) return null;
        o.Add(0);
        return o.ToArray();
    }

    private static byte[]? LongRecord(ReadOnlySpan<byte> r, int itemAt)
    {
        if (itemAt == Head - 2) return Record(r);
        var extra = itemAt - (Head - 2);
        var inner = new byte[r.Length - extra];
        r[itemAt..].CopyTo(inner.AsSpan(Head - 2));
        inner[0] = (byte)(inner.Length - 1);
        var t = Record(inner);
        if (t is null) return null;
        var o = new byte[t.Length + extra];
        r[..itemAt].CopyTo(o);
        t.AsSpan(Head - 2).CopyTo(o.AsSpan(itemAt));
        o[0] = (byte)(o.Length - 1);
        return o;
    }

    /// <summary>A packet whose last field is one item {itemid u16, attributes} at <paramref name="at"/>.</summary>
    public static byte[]? TrailingItem(ReadOnlySpan<byte> p, int at)
    {
        var body = p.Length - at;
        if (body < 2) return null;
        var rec = new byte[body + Head - 2];
        rec[0] = (byte)(rec.Length - 1);
        p[at..].CopyTo(rec.AsSpan(Head - 2));
        var t = Record(rec);
        if (t is null) return null;
        var o = new byte[at + t.Length - (Head - 2)];
        p[..at].CopyTo(o);
        t.AsSpan(Head - 2).CopyTo(o.AsSpan(at));
        return o;
    }

    /// <summary>An item at <paramref name="at"/>, possibly padded to the full struct (then the packet keeps its size).</summary>
    public static byte[]? LeadingItem(ReadOnlySpan<byte> p, int at)
    {
        if (p.Length < at + 2) return null;
        var id = p[at] | (p[at + 1] << 8);
        if (id == 0xFFFF) return p.ToArray();
        var cls = ClassOf(id);
        var fx = EnchantableFixed(cls);
        if (cls < 0) return null;
        if (fx < 0 && cls != 39) return p.ToArray();             // the same width on both wires
        int width26 = fx > 0 ? fx + (p.Length >= at + 2 + fx ? p[at + 2 + fx - 1] >> 1 : 0) * 3 : 16;
        var used = Math.Min(p.Length, at + 2 + width26);
        var t = TrailingItem(p[..used], at);
        if (t is null) return null;
        if (used == p.Length) return t;                           // trimmed: the zone grew the packet
        var o = new byte[p.Length];                              // padded: keep the size
        t.CopyTo(o, 0);
        p[used..].CopyTo(o.AsSpan(t.Length));
        return o;
    }
}
