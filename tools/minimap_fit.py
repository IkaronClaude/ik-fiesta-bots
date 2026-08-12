"""Work out how a client minimap DDS maps onto world coordinates.

The .shbd grid nominally covers world [0, tiles*6.25], but its playable region is a small
island inside a large blocked-bit void, so the minimap almost certainly spans the PLAYABLE
region rather than the padded grid. This measures both and prints the candidate mappings so
the choice is made from numbers instead of assumption.

Usage: python tools/minimap_fit.py <Map> [<Map> ...]
"""
import os
import struct
import sys

SHBD_DIR = os.environ.get("BLOCKINFO_DIR", "Z:/ServerSource/9Data/Shine/BlockInfo")
MINIMAP_DIR = os.environ.get("MINIMAP_DIR", "Z:/ClientProd2/resmenu/minimap")
WORLD_PER_TILE = 6.25


def find_ci(directory, *names):
    """Case-insensitive lookup — client files mix .dds/.DDS and map-name casing."""
    try:
        entries = os.listdir(directory)
    except OSError:
        return None
    low = {e.lower(): e for e in entries}
    for n in names:
        if n.lower() in low:
            return os.path.join(directory, low[n.lower()])
    return None


def shbd_bounds(path):
    with open(path, "rb") as fh:
        b = fh.read()
    bytes_per_row, height = struct.unpack_from("<ii", b, 0)
    width = bytes_per_row * 8
    data = b[8:]
    min_x, min_y, max_x, max_y, walk = width, height, -1, -1, 0
    for ty in range(height):
        row = data[ty * bytes_per_row:(ty + 1) * bytes_per_row]
        if not row:
            break
        for bx, byte in enumerate(row):
            if byte == 0xFF:      # all 8 tiles blocked — the common void case
                continue
            for bit in range(8):
                if (byte >> bit) & 1:
                    continue      # bit 1 = blocked
                tx = bx * 8 + bit
                walk += 1
                if tx < min_x: min_x = tx
                if tx > max_x: max_x = tx
                if ty < min_y: min_y = ty
                if ty > max_y: max_y = ty
    return width, height, (min_x, min_y, max_x, max_y), walk


def image_size(path):
    """DDS or TGA. A few maps (LinkField01/02) ship BOTH, at different sizes and aspects —
    operator: 'LinkField02 is tga not dds' — so the loader must handle each."""
    with open(path, "rb") as fh:
        head = fh.read(128)
    if head[:4] == b"DDS ":
        h, w = struct.unpack_from("<ii", head, 12)
        return w, h, "dds"
    w, h = struct.unpack_from("<HH", head, 12)      # TGA: width @12, height @14
    return w, h, f"tga{head[16]}bpp"


for map_name in sys.argv[1:]:
    shbd = find_ci(SHBD_DIR, map_name + ".shbd")
    dds = find_ci(MINIMAP_DIR, map_name + ".tga", map_name + ".dds")
    print(f"===== {map_name}")
    if not shbd:
        print(f"  no .shbd in {SHBD_DIR}")
        continue
    if not dds:
        print(f"  no minimap in {MINIMAP_DIR}")
        continue
    w, h, (x0, y0, x1, y1), walk = shbd_bounds(shbd)
    iw, ih, kind = image_size(dds)
    total = w * h
    print(f"  shbd        {w} x {h} tiles  (world {w * WORLD_PER_TILE:.0f} x {h * WORLD_PER_TILE:.0f})")
    print(f"  walkable    {walk} tiles = {100.0 * walk / total:.1f}% of grid")
    print(f"  walk bbox   tiles x[{x0}..{x1}] y[{y0}..{y1}]"
          f"  => world x[{x0 * WORLD_PER_TILE:.0f}..{(x1 + 1) * WORLD_PER_TILE:.0f}]"
          f" y[{y0 * WORLD_PER_TILE:.0f}..{(y1 + 1) * WORLD_PER_TILE:.0f}]")
    print(f"  bbox aspect {(x1 - x0 + 1) / (y1 - y0 + 1):.4f}")
    print(f"  minimap     {os.path.basename(dds)} [{kind}]  {iw} x {ih}   aspect {iw / ih:.4f}")
    print(f"  full-grid aspect {w / h:.4f}")
