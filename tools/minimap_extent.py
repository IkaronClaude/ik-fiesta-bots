"""Does a minimap span the FULL .shbd grid, or just the map's PLAYABLE region?

Operator 2026-08-13: "Sea of Greed scaling looks pretty damn spot on, RouN is really off."
That is the exact split the first orientation test could not see. RouN's walkable area covers only
~34% x ~50% of its grid, so "image = full grid" and "image = playable region" place it completely
differently; on maps whose walkable area nearly fills the grid the two hypotheses almost coincide,
which is why they correlated fine and looked correct.

So compare the two mappings directly, both with the established Y flip, by correlating the
walkability mask against the minimap's painted ground (bright, non-water). Higher wins.

Usage: python tools/minimap_extent.py <Map> [<Map> ...]
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from minimap_fit import find_ci, SHBD_DIR, MINIMAP_DIR      # noqa: E402
from dds_to_png import decode_dds                            # noqa: E402
from minimap_orient import bright_grid, corr, N              # noqa: E402


def walk_grid_window(path, n, win=None):
    """Walkable fraction per cell of an n x n grid over a TILE WINDOW of the .shbd.
    win = (tx0, ty0, tx1, ty1) inclusive; None = the whole grid."""
    with open(path, "rb") as fh:
        b = fh.read()
    bytes_per_row, height = struct.unpack_from("<ii", b, 0)
    width = bytes_per_row * 8
    data = b[8:]
    tx0, ty0, tx1, ty1 = win if win else (0, 0, width - 1, height - 1)
    ww, hh = tx1 - tx0 + 1, ty1 - ty0 + 1
    acc = [[0] * n for _ in range(n)]
    cnt = [[0] * n for _ in range(n)]
    for ty in range(ty0, ty1 + 1):
        gy = (ty - ty0) * n // hh
        row = data[ty * bytes_per_row:(ty + 1) * bytes_per_row]
        if not row:
            break
        arow, crow = acc[gy], cnt[gy]
        for tx in range(tx0, tx1 + 1):
            gx = (tx - tx0) * n // ww
            crow[gx] += 1
            if not ((row[tx >> 3] >> (tx & 7)) & 1):
                arow[gx] += 1
    return [[acc[y][x] / cnt[y][x] if cnt[y][x] else 0.0 for x in range(n)] for y in range(n)]


def walk_bbox(path):
    with open(path, "rb") as fh:
        b = fh.read()
    bytes_per_row, height = struct.unpack_from("<ii", b, 0)
    width = bytes_per_row * 8
    data = b[8:]
    x0, y0, x1, y1 = width, height, -1, -1
    for ty in range(height):
        row = data[ty * bytes_per_row:(ty + 1) * bytes_per_row]
        if not row:
            break
        for bx, byte in enumerate(row):
            if byte == 0xFF:
                continue
            for bit in range(8):
                if (byte >> bit) & 1:
                    continue
                tx = bx * 8 + bit
                if tx < x0: x0 = tx
                if tx > x1: x1 = tx
                if ty < y0: y0 = ty
                if ty > y1: y1 = ty
    return x0, y0, x1, y1, width, height


print(f"{'map':<12} {'fullgrid':>9} {'walkbbox':>9}   winner        walkable bbox as % of grid")
for map_name in sys.argv[1:]:
    shbd = find_ci(SHBD_DIR, map_name + ".shbd")
    dds = find_ci(MINIMAP_DIR, map_name + ".dds")
    if not shbd or not dds:
        print(f"{map_name:<12} missing shbd or minimap")
        continue
    with open(dds, "rb") as fh:
        w, h, rgba = decode_dds(fh.read())
    bg = bright_grid(w, h, rgba, N)[::-1]          # flipY, established
    x0, y0, x1, y1, gw, gh = walk_bbox(shbd)
    c_full = corr(walk_grid_window(shbd, N, None), bg)
    c_bbox = corr(walk_grid_window(shbd, N, (x0, y0, x1, y1)), bg)
    win = "walkbbox" if c_bbox > c_full + 0.02 else ("fullgrid" if c_full > c_bbox + 0.02 else "tie")
    pct = f"x {100*(x1-x0+1)/gw:.0f}%  y {100*(y1-y0+1)/gh:.0f}%"
    print(f"{map_name:<12} {c_full:9.3f} {c_bbox:9.3f}   {win:<13} {pct}")
