"""Verify the minimap->world mapping by content, not by assumption.

Hypothesis (from minimap_fit.py): the minimap image covers the FULL square .shbd grid, i.e.
    px = worldX / (WidthTiles * 6.25) * imgW ,  py = worldY / (HeightTiles * 6.25) * imgH

Test: minimaps draw the playable land and leave the surrounding void transparent/black. So the
bounding box of DRAWN pixels should coincide with the .shbd walkable bounding box once both are
expressed as a FRACTION of the map extent. Agreement across many maps confirms the mapping;
systematic disagreement falsifies it.

Usage: python tools/minimap_align.py <Map> [<Map> ...]
"""
import os
import struct
import sys

from minimap_fit import find_ci, shbd_bounds, SHBD_DIR, MINIMAP_DIR


def decode_alpha_mask(path):
    """Return (w, h, mask) where mask[y][x] is True if the pixel is 'drawn'.

    Only enough DDS/TGA decoding to answer 'is there content here': for DXT3/DXT5 the per-block
    alpha lives in the first 8 bytes of each 16-byte block; for DXT1 we use colour != black; for
    16-bit TGA the high bit is alpha and the rest is RGB555.
    """
    with open(path, "rb") as fh:
        b = fh.read()

    if b[:4] == b"DDS ":
        h, w = struct.unpack_from("<ii", b, 12)
        four_cc = b[84:88].decode("ascii", "replace")
        data = b[128:]
        mask = [[False] * w for _ in range(h)]
        block_bytes = 8 if four_cc == "DXT1" else 16
        bx_count = (w + 3) // 4
        by_count = (h + 3) // 4
        for by in range(by_count):
            for bx in range(bx_count):
                off = (by * bx_count + bx) * block_bytes
                if off + block_bytes > len(data):
                    continue
                blk = data[off:off + block_bytes]
                for i in range(16):
                    x, y = bx * 4 + (i % 4), by * 4 + (i // 4)
                    if x >= w or y >= h:
                        continue
                    if four_cc == "DXT3":
                        a = (blk[i // 2] >> (4 * (i % 2))) & 0x0F
                        mask[y][x] = a > 2
                    elif four_cc == "DXT5":
                        a0, a1 = blk[0], blk[1]
                        bits = int.from_bytes(blk[2:8], "little")
                        code = (bits >> (3 * i)) & 7
                        a = a0 if code == 0 else a1 if code == 1 else 128
                        if a0 <= a1 and code == 6:
                            a = 0
                        mask[y][x] = a > 8
                    else:                                  # DXT1: black == void
                        c0, c1 = struct.unpack_from("<HH", blk, 0)
                        idx = struct.unpack_from("<I", blk, 4)[0]
                        code = (idx >> (2 * i)) & 3
                        c = c0 if code == 0 else c1 if code == 1 else (c0 | c1)
                        mask[y][x] = c != 0
        return w, h, mask

    # TGA (uncompressed truecolour)
    idlen, _cmaptype, imgtype = b[0], b[1], b[2]
    w, h = struct.unpack_from("<HH", b, 12)
    bpp = b[16]
    descriptor = b[17]
    off = 18 + idlen
    mask = [[False] * w for _ in range(h)]
    if imgtype != 2:
        return w, h, mask
    stride = bpp // 8
    for row in range(h):
        y = row if (descriptor & 0x20) else (h - 1 - row)
        for x in range(w):
            p = off + (row * w + x) * stride
            if p + stride > len(b):
                break
            if bpp == 16:
                v = struct.unpack_from("<H", b, p)[0]
                mask[y][x] = (v & 0x7FFF) != 0
            else:
                mask[y][x] = b[p] != 0 or b[p + 1] != 0 or b[p + 2] != 0
    return w, h, mask


def content_bbox(w, h, mask):
    x0, y0, x1, y1 = w, h, -1, -1
    for y in range(h):
        rowm = mask[y]
        for x in range(w):
            if rowm[x]:
                if x < x0: x0 = x
                if x > x1: x1 = x
                if y < y0: y0 = y
                if y > y1: y1 = y
    return x0, y0, x1, y1


print(f"{'map':<14}{'source':<10}{'  walk bbox (fraction of grid)':<34}{'  image content bbox (fraction)':<34} verdict")
for map_name in sys.argv[1:]:
    shbd = find_ci(SHBD_DIR, map_name + ".shbd")
    img = find_ci(MINIMAP_DIR, map_name + ".tga", map_name + ".dds")
    if not shbd or not img:
        print(f"{map_name:<14}missing shbd or minimap")
        continue
    gw, gh, (sx0, sy0, sx1, sy1), _ = shbd_bounds(shbd)
    iw, ih, mask = decode_alpha_mask(img)
    ix0, iy0, ix1, iy1 = content_bbox(iw, ih, mask)
    if ix1 < 0:
        print(f"{map_name:<14}{os.path.basename(img):<10}  image fully empty by this test")
        continue
    sf = (sx0 / gw, sy0 / gh, (sx1 + 1) / gw, (sy1 + 1) / gh)
    imf = (ix0 / iw, iy0 / ih, (ix1 + 1) / iw, (iy1 + 1) / ih)
    err = max(abs(a - b) for a, b in zip(sf, imf))
    verdict = "MATCH" if err < 0.05 else ("close" if err < 0.12 else "MISMATCH")
    print(f"{map_name:<14}{os.path.basename(img):<10}"
          f"  [{sf[0]:.3f},{sf[1]:.3f}..{sf[2]:.3f},{sf[3]:.3f}]"
          f"  [{imf[0]:.3f},{imf[1]:.3f}..{imf[2]:.3f},{imf[3]:.3f}]"
          f"  maxerr={err:.3f} {verdict}")
