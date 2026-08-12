"""Convert a client TGA to PNG with alpha FORCED OPAQUE.

The minimap TGAs (LinkField01/02) are 16bpp ARGB1555 with the alpha bit clear on every pixel, so
any viewer that honours alpha shows an empty image even though the colour bits hold a real map.
Forcing alpha to 255 makes them viewable. Same rule the C# MinimapImage loader applies.

Usage: python tools/tga_to_png.py <in.tga> [out.png]
"""
import struct
import sys
import zlib


def decode_tga(b):
    id_len, _cmap, img_type = b[0], b[1], b[2]
    w, h = struct.unpack_from("<HH", b, 12)
    bpp, descriptor = b[16], b[17]
    stride = bpp // 8
    src = 18 + id_len
    top_down = bool(descriptor & 0x20)
    out = bytearray(w * h * 4)

    def put(i, px):
        row, col = divmod(i, w)
        y = row if top_down else h - 1 - row
        o = (y * w + col) * 4
        if stride == 2:
            v = px[0] | (px[1] << 8)
            out[o] = ((v >> 10) & 0x1F) * 255 // 31
            out[o + 1] = ((v >> 5) & 0x1F) * 255 // 31
            out[o + 2] = (v & 0x1F) * 255 // 31
        else:
            out[o], out[o + 1], out[o + 2] = px[2], px[1], px[0]
        out[o + 3] = 255                      # <- forced opaque

    if img_type == 2:
        for i in range(w * h):
            p = src + i * stride
            if p + stride > len(b):
                break
            put(i, b[p:p + stride])
    elif img_type == 10:
        i = 0
        while i < w * h and src < len(b):
            head = b[src]; src += 1
            count = (head & 0x7F) + 1
            if head & 0x80:
                px = b[src:src + stride]; src += stride
                for _ in range(count):
                    if i >= w * h: break
                    put(i, px); i += 1
            else:
                for _ in range(count):
                    if i >= w * h or src + stride > len(b): break
                    put(i, b[src:src + stride]); src += stride; i += 1
    else:
        raise SystemExit(f"unsupported TGA image type {img_type}")
    return w, h, bytes(out)


def write_png(path, w, h, rgba):
    raw = b"".join(b"\x00" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)


src_path = sys.argv[1]
dst_path = sys.argv[2] if len(sys.argv) > 2 else src_path.rsplit(".", 1)[0] + ".png"
with open(src_path, "rb") as fh:
    data = fh.read()
width, height, pixels = decode_tga(data)
write_png(dst_path, width, height, pixels)
print(f"{src_path} -> {dst_path}  ({width}x{height}, alpha forced opaque)")
