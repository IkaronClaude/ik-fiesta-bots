namespace Fiesta.Bot.GameData;

/// <summary>
/// Loads a map's MINIMAP — the same picture the real client draws — from BYO client data at
/// <c>&lt;client root&gt;/resmenu/minimap/&lt;MapName&gt;.{tga,dds}</c>, and hands it back as a PNG.
///
/// <para><b>Client-data traps, all settled by measurement:</b></para>
/// <list type="number">
/// <item><b>Mixed case.</b> The extensions are spelled <c>.dds</c> and <c>.DDS</c> in one directory, so
/// the lookup is case-insensitive — the same trap that silently cost the item icons their art twice.</item>
/// <item><b>⛔ The <c>.tga</c> files are NOT minimaps — they are LOADING SCREENS.</b> Only two exist
/// (LinkField01/02) and both are 1024x768 (a 4:3 screen, not a square map); decoding LinkField02.TGA
/// renders the "Dark Passage II — Now Loading" splash art. An earlier version of this loader preferred
/// TGA over DDS on the reasoning that it was higher-resolution; that was wrong, and would have drawn a
/// picture of four chibi adventurers where the map belongs. <b>DDS only.</b></item>
/// <item><b>Those TGAs also look blank in a viewer</b> — 16bpp ARGB1555 with the alpha bit clear on
/// every pixel, so anything honouring alpha shows nothing. The TGA decoder below forces opaque and is
/// kept for <c>tools/tga_to_png.py</c>-style inspection, but it is not used for minimaps.</item>
/// </list>
///
/// <para><b>World↔image mapping (MEASURED — tools/minimap_orient.py).</b> The image spans the FULL square
/// <c>.shbd</c> grid, world <c>[0, tiles*6.25]</c> on both axes, with <b>Y FLIPPED</b>:
/// <c>px = worldX/extent*W</c>, <c>py = (1 - worldY/extent)*H</c>. Established by correlating each map's
/// walkability mask against its painted ground across all four flip candidates: flipY scored highest on
/// every map that discriminates (EldGbl02 0.880 vs 0.415 identity, RouVal02 0.635 vs 0.395, Urg 0.494 vs
/// 0.286) and never lost. Towns (RouN, Eld) come out inconclusive because they are mostly rooftops, so
/// they neither confirm nor contradict. An earlier attempt to fit by drawn-pixel bounding box was a dead
/// test — minimaps are fully painted, so every image's content bbox is the whole image.</para>
/// </summary>
public static class MinimapImage
{
    /// <summary>Decode a map's minimap to PNG bytes, or null when the client has no art for it
    /// (instances and a few link fields have none — the caller draws a plain grid instead).</summary>
    public static byte[]? Png(string minimapDir, string mapName)
    {
        var path = Resolve(minimapDir, mapName);
        if (path is null) return null;
        byte[] raw;
        try { raw = File.ReadAllBytes(path); } catch { return null; }

        var rgba = raw.Length >= 4 && raw[0] == 'D' && raw[1] == 'D' && raw[2] == 'S' && raw[3] == ' '
            ? IconAtlas.Decode(raw, out var w, out var h)
            : DecodeTga(raw, out w, out h);
        return rgba is null || w <= 0 || h <= 0 ? null : GameData.Png.Encode(rgba, w, h);
    }

    /// <summary>True when the client ships art for this map.</summary>
    public static bool Exists(string minimapDir, string mapName) => Resolve(minimapDir, mapName) is not null;

    // Index the directory once, case-insensitively, keyed by "<name>.<ext>" so the TGA/DDS preference
    // is expressible — the item-icon index keys on the bare name and could not tell the two apart.
    private static readonly Dictionary<string, Dictionary<string, string>> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    private static string? Resolve(string dir, string mapName)
    {
        Dictionary<string, string> index;
        lock (_gate)
        {
            if (!_dirs.TryGetValue(dir, out index!))
            {
                index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        index[Path.GetFileName(f)] = f;
                }
                catch { /* no minimap dir — caller falls back to a plain grid */ }
                _dirs[dir] = index;
            }
        }
        // DDS ONLY — the .tga siblings are loading screens (see the type remarks), so a map that ships
        // both must still resolve to its .dds.
        return index.TryGetValue(mapName + ".dds", out var p) ? p : null;
    }

    /// <summary>Uncompressed or RLE truecolour TGA → RGBA8888, forced opaque (see the type remarks:
    /// these files carry a cleared alpha bit throughout and are not actually transparent).</summary>
    private static byte[]? DecodeTga(byte[] b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 18) return null;
        int idLen = b[0], imageType = b[2];
        // Locals, not the out params: the Put local function below captures them, and C# forbids
        // capturing an out parameter.
        int w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8);
        width = w; height = h;
        int bpp = b[16], descriptor = b[17];
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
        if (imageType is not (2 or 10)) return null;               // 2 = raw truecolour, 10 = RLE
        var stride = bpp / 8;
        if (stride is not (2 or 3 or 4)) return null;

        var px = new byte[w * h * 4];
        var src = 18 + idLen;
        var topDown = (descriptor & 0x20) != 0;

        void Put(int row, int col, ReadOnlySpan<byte> p)
        {
            var y = topDown ? row : h - 1 - row;
            var o = (y * w + col) * 4;
            if (stride == 2)
            {
                int v = p[0] | (p[1] << 8);                          // ARGB1555, alpha bit ignored
                px[o] = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                px[o + 1] = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                px[o + 2] = (byte)((v & 0x1F) * 255 / 31);
            }
            else
            {
                px[o] = p[2]; px[o + 1] = p[1]; px[o + 2] = p[0];    // BGR(A) on the wire
            }
            px[o + 3] = 255;
        }

        if (imageType == 2)
        {
            for (var row = 0; row < h; row++)
                for (var col = 0; col < w; col++)
                {
                    var p = src + (row * w + col) * stride;
                    if (p + stride > b.Length) return px;
                    Put(row, col, b.AsSpan(p, stride));
                }
            return px;
        }

        // RLE: packets of [header][pixel(s)]; high bit set = run of one repeated pixel.
        int idx = 0, total = w * h;
        while (idx < total && src < b.Length)
        {
            int header = b[src++];
            int count = (header & 0x7F) + 1;
            if ((header & 0x80) != 0)
            {
                if (src + stride > b.Length) break;
                var pixel = b.AsSpan(src, stride);
                src += stride;
                for (var i = 0; i < count && idx < total; i++, idx++)
                    Put(idx / w, idx % w, pixel);
            }
            else
            {
                for (var i = 0; i < count && idx < total; i++, idx++, src += stride)
                {
                    if (src + stride > b.Length) return px;
                    Put(idx / w, idx % w, b.AsSpan(src, stride));
                }
            }
        }
        return px;
    }
}
