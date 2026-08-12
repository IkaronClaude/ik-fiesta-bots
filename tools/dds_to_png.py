"""Convert a client DDS (DXT1/3/5 or uncompressed 32bpp) to PNG, alpha forced opaque.

Companion to tga_to_png.py, for eyeballing minimap art outside the bot host.

Usage: python tools/dds_to_png.py <in.dds> [out.png]
"""
import struct
import sys
import zlib


def rgb565(c):
    return (((c >> 11) & 0x1F) * 255 // 31, ((c >> 5) & 0x3F) * 255 // 63, (c & 0x1F) * 255 // 31)


def decode_dds(b):
    h, w = struct.unpack_from("<ii", b, 12)
    pf_flags = struct.unpack_from("<I", b, 80)[0]
    four_cc = b[84:88].decode("ascii", "replace")
    out = bytearray(w * h * 4)
    data = b[128:]

    if pf_flags & 0x4:
        block = 8 if four_cc == "DXT1" else 16
        bxc, byc = (w + 3) // 4, (h + 3) // 4
        for by in range(byc):
            for bx in range(bxc):
                off = (by * bxc + bx) * block
                if off + block > len(data):
                    continue
                blk = data[off:off + block]
                colour_off = 0 if four_cc == "DXT1" else 8
                c0, c1 = struct.unpack_from("<HH", blk, colour_off)
                idx = struct.unpack_from("<I", blk, colour_off + 4)[0]
                cols = [rgb565(c0), rgb565(c1)]
                if four_cc != "DXT1" or c0 > c1:
                    cols.append(tuple((2 * cols[0][i] + cols[1][i]) // 3 for i in range(3)))
                    cols.append(tuple((cols[0][i] + 2 * cols[1][i]) // 3 for i in range(3)))
                else:
                    cols.append(tuple((cols[0][i] + cols[1][i]) // 2 for i in range(3)))
                    cols.append((0, 0, 0))
                for i in range(16):
                    x, y = bx * 4 + (i % 4), by * 4 + (i // 4)
                    if x >= w or y >= h:
                        continue
                    r, g, bl = cols[(idx >> (2 * i)) & 3]
                    o = (y * w + x) * 4
                    out[o], out[o + 1], out[o + 2], out[o + 3] = r, g, bl, 255
    elif pf_flags & 0x40:
        for i in range(w * h):
            p = 128 + i * 4
            if p + 4 > len(b):
                break
            o = i * 4
            out[o], out[o + 1], out[o + 2], out[o + 3] = b[p + 2], b[p + 1], b[p], 255
    else:
        raise SystemExit(f"unsupported DDS pixel format flags=0x{pf_flags:x} fourCC={four_cc}")
    return w, h, bytes(out)


def write_png(path, w, h, rgba):
    raw = b"".join(b"\x00" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n"
                 + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9))
                 + chunk(b"IEND", b""))


if __name__ == "__main__":      # importable: minimap_orient.py reuses decode_dds
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src.rsplit(".", 1)[0] + ".png"
    with open(src, "rb") as fh:
        data = fh.read()
    W, H, PX = decode_dds(data)
    write_png(dst, W, H, PX)
    print(f"{src} -> {dst}  ({W}x{H})")
