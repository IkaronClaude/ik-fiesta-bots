namespace Fiesta.Bot.GameData;

/// <summary>
/// Cuts a single item icon out of a client icon ATLAS and hands it back as a PNG.
///
/// <para>Item art lives in <c>&lt;client&gt;/resmenu/Icon/&lt;IconFile&gt;.dds</c> — a 256x256 DXT-compressed
/// sheet holding an 8x8 grid of 32px icons — and <c>ItemViewInfo.shn</c> says which sheet and which cell
/// each item uses (<c>IconFile</c> + <c>IconIndex</c>). Nothing here is baked: the sheets are BYO client
/// data supplied at runtime, exactly like <c>ressystem</c> and the BlockInfo grids.</para>
///
/// <para>Decoding is done by hand rather than with an image library because the host runs on Linux
/// containers where System.Drawing is unavailable, and pulling in a full imaging dependency to crop a
/// 32x32 tile would be a poor trade. BC1/BC2/BC3 and plain 32-bit surfaces cover every sheet in the
/// client; anything else is reported rather than guessed at.</para>
/// </summary>
public static class IconAtlas
{
    /// <summary>Atlases are an 8x8 grid of cells, whatever their pixel size.</summary>
    public const int Columns = 8;

    /// <summary>Decode one cell of a DDS atlas to a PNG. Returns null if the file is missing, the
    /// format is one we do not decode, or the index is outside the sheet — the caller renders a
    /// placeholder rather than a broken image.
    ///
    /// <para>⛔ CELL SIZE IS DERIVED, NOT 32. It used to be a hard 32px constant, which is right only for
    /// the 256x256 item and skill sheets. The <b>abstate</b> sheets are 128x128, so a 32px cell made them
    /// a 4x4 grid of 16 cells and every icon index above 15 fell off the end and returned null — which is
    /// why a live buff rendered as a grey box with a number (operator 2026-08-13) even though its row
    /// plainly said <c>icon=16, iconFile=AbState03</c>. Every family is an 8x8 grid of 64 cells: items and
    /// skills 256/8 = 32px, abstates 128/8 = 16px. So take the cell size from the sheet.</para></summary>
    public static byte[]? IconPng(string ddsPath, int iconIndex)
    {
        if (!File.Exists(ddsPath)) return null;
        byte[] dds;
        try { dds = File.ReadAllBytes(ddsPath); } catch { return null; }
        var surface = Decode(dds, out var w, out var h);
        if (surface is null || w <= 0 || h <= 0) return null;

        var size = Math.Max(1, w / Columns);          // 256 -> 32, 128 -> 16
        var perRow = Columns;
        var cells = perRow * Math.Max(1, h / size);
        if (iconIndex < 0 || iconIndex >= cells) return null;
        var cx = (iconIndex % perRow) * size;
        var cy = (iconIndex / perRow) * size;

        var tile = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var sy = cy + y;
            if (sy >= h) break;
            Buffer.BlockCopy(surface, (sy * w + cx) * 4, tile, y * size * 4, Math.Min(size, w - cx) * 4);
        }
        return Png.Encode(tile, size, size);
    }

    /// <summary>DDS → RGBA8888. Supports DXT1/3/5 and uncompressed 32-bit; null otherwise.
    /// <para>Internal rather than private because the minimap loader decodes whole surfaces from the
    /// same client art (see <see cref="MinimapImage"/>) — one DDS decoder, two callers.</para></summary>
    internal static byte[]? Decode(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S' || b[3] != ' ') return null;
        height = BitConverter.ToInt32(b, 12);
        width = BitConverter.ToInt32(b, 16);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
        var pfFlags = BitConverter.ToUInt32(b, 80);
        var fourCc = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        const int data = 128;
        var outp = new byte[width * height * 4];

        if ((pfFlags & 0x4) != 0)   // DDPF_FOURCC — a block-compressed surface
        {
            var blockBytes = fourCc == "DXT1" ? 8 : 16;
            if (fourCc is not ("DXT1" or "DXT3" or "DXT5")) return null;
            var bx = (width + 3) / 4; var by = (height + 3) / 4;
            if (b.Length < data + bx * by * blockBytes) return null;
            for (var byi = 0; byi < by; byi++)
                for (var bxi = 0; bxi < bx; bxi++)
                    DecodeBlock(b, data + (byi * bx + bxi) * blockBytes, fourCc, outp, width, height, bxi * 4, byi * 4);
            return outp;
        }
        if ((pfFlags & 0x40) != 0 && BitConverter.ToInt32(b, 88) == 32)   // DDPF_RGB, 32bpp (BGRA)
        {
            if (b.Length < data + width * height * 4) return null;
            for (var i = 0; i < width * height; i++)
            {
                outp[i * 4 + 0] = b[data + i * 4 + 2];
                outp[i * 4 + 1] = b[data + i * 4 + 1];
                outp[i * 4 + 2] = b[data + i * 4 + 0];
                outp[i * 4 + 3] = b[data + i * 4 + 3];
            }
            return outp;
        }
        return null;
    }

    /// <summary>One 4x4 block. BC2 carries 4-bit explicit alpha, BC3 an interpolated alpha ramp, BC1 a
    /// 1-bit punch-through keyed on the colour ordering — the transparency item icons actually use.</summary>
    private static void DecodeBlock(byte[] b, int off, string fourCc, byte[] outp, int w, int h, int px, int py)
    {
        var alpha = new byte[16];
        var colorOff = off;
        if (fourCc == "DXT3")
        {
            for (var i = 0; i < 16; i += 2)
            {
                var v = b[off + i / 2];
                alpha[i] = (byte)((v & 0x0F) * 17);          // 4-bit -> 8-bit
                alpha[i + 1] = (byte)((v >> 4) * 17);
            }
            colorOff = off + 8;
        }
        else if (fourCc == "DXT5")
        {
            var a0 = b[off]; var a1 = b[off + 1];
            var bits = 0UL;
            for (var i = 0; i < 6; i++) bits |= (ulong)b[off + 2 + i] << (8 * i);
            for (var i = 0; i < 16; i++)
            {
                var code = (int)((bits >> (3 * i)) & 7);
                alpha[i] = (byte)(code switch
                {
                    0 => a0,
                    1 => a1,
                    _ => a0 > a1
                        ? (byte)(((8 - code) * a0 + (code - 1) * a1) / 7)
                        : code == 6 ? 0 : code == 7 ? 255 : (byte)(((6 - code) * a0 + (code - 1) * a1) / 5),
                });
            }
            colorOff = off + 8;
        }
        else for (var i = 0; i < 16; i++) alpha[i] = 255;    // DXT1: opaque unless punch-through below

        var c0 = BitConverter.ToUInt16(b, colorOff);
        var c1 = BitConverter.ToUInt16(b, colorOff + 2);
        var idx = BitConverter.ToUInt32(b, colorOff + 4);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4];
        (r[0], g[0], bl[0]) = Rgb565(c0);
        (r[1], g[1], bl[1]) = Rgb565(c1);
        var punch = fourCc == "DXT1" && c0 <= c1;
        if (!punch)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2;
            r[3] = g[3] = bl[3] = 0;                          // index 3 = fully transparent
        }
        for (var i = 0; i < 16; i++)
        {
            var x = px + (i % 4); var y = py + (i / 4);
            if (x >= w || y >= h) continue;
            var code = (int)((idx >> (2 * i)) & 3);
            var o = (y * w + x) * 4;
            outp[o] = (byte)r[code]; outp[o + 1] = (byte)g[code]; outp[o + 2] = (byte)bl[code];
            outp[o + 3] = punch && code == 3 ? (byte)0 : alpha[i];
        }
    }

    private static (int R, int G, int B) Rgb565(ushort c)
        => (((c >> 11) & 0x1F) * 255 / 31, ((c >> 5) & 0x3F) * 255 / 63, (c & 0x1F) * 255 / 31);
}

/// <summary>A minimal PNG writer: enough to emit one RGBA image, with no imaging dependency.
/// (Deflate comes from the BCL; PNG wants a zlib wrapper around it, which is a 2-byte header plus an
/// Adler-32 trailer.)</summary>
internal static class Png
{
    public static byte[] Encode(byte[] rgba, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];                  // each scanline is prefixed with a filter byte
        for (var y = 0; y < h; y++)
        {
            raw[y * (w * 4 + 1)] = 0;                          // filter 0 = None
            Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
        }
        using var deflated = new MemoryStream();
        deflated.WriteByte(0x78); deflated.WriteByte(0x01);    // zlib header, no preset dict
        using (var z = new System.IO.Compression.DeflateStream(deflated, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(raw, 0, raw.Length);
        var adler = Adler32(raw);
        deflated.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BeInt(ihdr, 0, w); BeInt(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6;                              // 8-bit, truecolour + alpha
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", deflated.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void BeInt(byte[] b, int o, int v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BeInt(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = Crc32(t, data);
        var c = new byte[4]; BeInt(c, 0, unchecked((int)crc)); s.Write(c);
    }

    private static uint Adler32(byte[] d)
    {
        uint a = 1, b = 0;
        foreach (var x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFF;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
