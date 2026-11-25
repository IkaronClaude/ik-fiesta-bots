using System.IO.Compression;

namespace Fiesta.Bot.Services;

/// <summary>Decodes the client's own images (DDS atlases, minimap DDS / TGA) to PNG for the game view. The images are
/// read from the configured client directory at runtime; nothing is shipped.</summary>
public static class ClientImages
{
    /// <summary>Icon atlases are an 8 x 8 grid of cells whatever their pixel size.</summary>
    public const int AtlasColumns = 8;

    /// <summary>One cell of a DDS atlas as PNG, or null.</summary>
    public static byte[]? AtlasCellPng(string ddsPath, int index)
    {
        if (!File.Exists(ddsPath)) return null;
        var surface = DecodeDds(File.ReadAllBytes(ddsPath), out var w, out var h);
        if (surface is null) return null;
        var size = Math.Max(1, w / AtlasColumns);
        var cells = AtlasColumns * Math.Max(1, h / size);
        if (index < 0 || index >= cells) return null;
        int cx = index % AtlasColumns * size, cy = index / AtlasColumns * size;
        var tile = new byte[size * size * 4];
        for (var y = 0; y < size && cy + y < h; y++)
            Buffer.BlockCopy(surface, ((cy + y) * w + cx) * 4, tile, y * size * 4, Math.Min(size, w - cx) * 4);
        return Png(tile, size, size);
    }

    /// <summary>A whole DDS or TGA image as PNG, or null.</summary>
    public static byte[]? ImagePng(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        var rgba = b.Length >= 4 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S' && b[3] == ' ' ? DecodeDds(b, out var w, out var h) : DecodeTga(b, out w, out h);
        return rgba is null ? null : Png(rgba, w, h);
    }

    /// <summary>DDS to RGBA (DXT1 / DXT3 / DXT5 and uncompressed 32-bit), null otherwise.</summary>
    public static byte[]? DecodeDds(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S' || b[3] != ' ') return null;
        height = BitConverter.ToInt32(b, 12); width = BitConverter.ToInt32(b, 16);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
        var flags = BitConverter.ToUInt32(b, 80);
        var four = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        const int data = 128;
        var o = new byte[width * height * 4];
        if ((flags & 0x4) != 0)
        {
            if (four is not ("DXT1" or "DXT3" or "DXT5")) return null;
            var bb = four == "DXT1" ? 8 : 16;
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            if (b.Length < data + bx * by * bb) return null;
            for (var j = 0; j < by; j++)
                for (var i = 0; i < bx; i++) Block(b, data + (j * bx + i) * bb, four, o, width, height, i * 4, j * 4);
            return o;
        }
        if ((flags & 0x40) != 0 && BitConverter.ToInt32(b, 88) == 32 && b.Length >= data + width * height * 4)
        {
            for (var i = 0; i < width * height; i++)
            {
                o[i * 4] = b[data + i * 4 + 2]; o[i * 4 + 1] = b[data + i * 4 + 1]; o[i * 4 + 2] = b[data + i * 4]; o[i * 4 + 3] = b[data + i * 4 + 3];
            }
            return o;
        }
        return null;
    }

    private static void Block(byte[] b, int off, string four, byte[] o, int w, int h, int px, int py)
    {
        Span<byte> alpha = stackalloc byte[16];
        var co = off;
        if (four == "DXT3")
        {
            for (var i = 0; i < 16; i += 2) { var v = b[off + i / 2]; alpha[i] = (byte)((v & 0xF) * 17); alpha[i + 1] = (byte)((v >> 4) * 17); }
            co = off + 8;
        }
        else if (four == "DXT5")
        {
            int a0 = b[off], a1 = b[off + 1];
            ulong bits = 0;
            for (var i = 0; i < 6; i++) bits |= (ulong)b[off + 2 + i] << (8 * i);
            for (var i = 0; i < 16; i++)
            {
                var c = (int)((bits >> (3 * i)) & 7);
                alpha[i] = (byte)(c == 0 ? a0 : c == 1 ? a1 : a0 > a1 ? ((8 - c) * a0 + (c - 1) * a1) / 7
                    : c == 6 ? 0 : c == 7 ? 255 : ((6 - c) * a0 + (c - 1) * a1) / 5);
            }
            co = off + 8;
        }
        else alpha.Fill(255);
        var c0 = BitConverter.ToUInt16(b, co); var c1 = BitConverter.ToUInt16(b, co + 2); var idx = BitConverter.ToUInt32(b, co + 4);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4];
        (r[0], g[0], bl[0]) = Rgb565(c0); (r[1], g[1], bl[1]) = Rgb565(c1);
        var punch = four == "DXT1" && c0 <= c1;
        if (!punch)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
        }
        else { r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2; r[3] = g[3] = bl[3] = 0; }
        for (var i = 0; i < 16; i++)
        {
            int x = px + i % 4, y = py + i / 4;
            if (x >= w || y >= h) continue;
            var c = (int)((idx >> (2 * i)) & 3);
            var p = (y * w + x) * 4;
            o[p] = (byte)r[c]; o[p + 1] = (byte)g[c]; o[p + 2] = (byte)bl[c]; o[p + 3] = punch && c == 3 ? (byte)0 : alpha[i];
        }
    }

    private static (int, int, int) Rgb565(ushort c) => ((c >> 11 & 0x1F) * 255 / 31, (c >> 5 & 0x3F) * 255 / 63, (c & 0x1F) * 255 / 31);

    /// <summary>Uncompressed or RLE true-colour TGA to RGBA (opaque), null otherwise.</summary>
    public static byte[]? DecodeTga(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 18) return null;
        int idLen = b[0], type = b[2], w = b[12] | b[13] << 8, h = b[14] | b[15] << 8, bpp = b[16], desc = b[17];
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192 || type is not (2 or 10)) return null;
        var stride = bpp / 8;
        if (stride is not (2 or 3 or 4)) return null;
        width = w; height = h;
        var px = new byte[w * h * 4];
        var top = (desc & 0x20) != 0;
        var src = 18 + idLen;
        void Put(int i, int at)
        {
            int row = i / w, col = i % w, y = top ? row : h - 1 - row, o = (y * w + col) * 4;
            if (stride == 2) { var v = b[at] | b[at + 1] << 8; px[o] = (byte)((v >> 10 & 0x1F) * 255 / 31); px[o + 1] = (byte)((v >> 5 & 0x1F) * 255 / 31); px[o + 2] = (byte)((v & 0x1F) * 255 / 31); }
            else { px[o] = b[at + 2]; px[o + 1] = b[at + 1]; px[o + 2] = b[at]; }
            px[o + 3] = 255;
        }
        int n = 0, total = w * h;
        if (type == 2)
        {
            for (; n < total && src + stride <= b.Length; n++, src += stride) Put(n, src);
            return px;
        }
        while (n < total && src < b.Length)
        {
            int head = b[src++], count = (head & 0x7F) + 1;
            if ((head & 0x80) != 0)
            {
                if (src + stride > b.Length) break;
                for (var i = 0; i < count && n < total; i++, n++) Put(n, src);
                src += stride;
            }
            else
                for (var i = 0; i < count && n < total && src + stride <= b.Length; i++, n++, src += stride) Put(n, src);
        }
        return px;
    }

    /// <summary>Encode RGBA as PNG.</summary>
    public static byte[] Png(byte[] rgba, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
        using var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr); Chunk(png, "IDAT", z.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var x in t) crc = Crc[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in data) crc = Crc[(crc ^ x) & 0xFF] ^ (crc >> 8);
        var c = new byte[4]; Be(c, 0, unchecked((int)(crc ^ 0xFFFFFFFF))); s.Write(c);
    }

    private static readonly uint[] Crc = MakeCrc();
    private static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
}
