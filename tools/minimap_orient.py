"""Decide the minimap<->world orientation by CORRELATION, not by eye.

Hypothesis under test: the minimap image spans the full square .shbd grid, world [0, tiles*6.25]
on both axes, with image pixel (px,py) = (worldX/extent*W, worldY/extent*H).

The unknown is orientation: the art could be stored flipped in either axis relative to the grid.
So score all four candidates. Signal: walkable tiles are roads/open ground, which are PAINTED
(bright, non-water) on the minimap, while blocked tiles are buildings, water and the void border
(dark or blue). Correlating the walkability mask against per-pixel brightness separates them.

A clear winner across several maps confirms both the mapping and the orientation. If all four
score alike, the test is dead and the answer must come from a human eyeballing a landmark --
say so rather than picking the top number.

Usage: python tools/minimap_orient.py <Map> [<Map> ...]
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from minimap_fit import find_ci, SHBD_DIR, MINIMAP_DIR          # noqa: E402
from dds_to_png import decode_dds                                # noqa: E402

N = 128          # comparison grid; coarse on purpose -- we want structure, not pixels


def walk_grid(path, n):
    """Fraction of walkable tiles per cell of an n x n grid over the whole .shbd."""
    with open(path, "rb") as fh:
        b = fh.read()
    bytes_per_row, height = struct.unpack_from("<ii", b, 0)
    width = bytes_per_row * 8
    data = b[8:]
    acc = [[0] * n for _ in range(n)]
    cnt = [[0] * n for _ in range(n)]
    for ty in range(height):
        gy = ty * n // height
        row = data[ty * bytes_per_row:(ty + 1) * bytes_per_row]
        if not row:
            break
        arow, crow = acc[gy], cnt[gy]
        for tx in range(width):
            gx = tx * n // width
            crow[gx] += 1
            if not ((row[tx >> 3] >> (tx & 7)) & 1):
                arow[gx] += 1
    return [[acc[y][x] / cnt[y][x] if cnt[y][x] else 0.0 for x in range(n)] for y in range(n)]


def bright_grid(w, h, rgba, n):
    """Mean 'is painted ground' per cell: bright and not water-blue."""
    acc = [[0.0] * n for _ in range(n)]
    cnt = [[0] * n for _ in range(n)]
    for y in range(h):
        gy = y * n // h
        for x in range(w):
            o = (y * w + x) * 4
            r, g, b = rgba[o], rgba[o + 1], rgba[o + 2]
            lum = (r + g + b) / 3.0
            watery = b > r + 20 and b > g + 10
            acc[gy][x * n // w] += 0.0 if watery else lum / 255.0
            cnt[gy][x * n // w] += 1
    return [[acc[y][x] / cnt[y][x] if cnt[y][x] else 0.0 for x in range(n)] for y in range(n)]


def corr(a, b):
    xs = [a[y][x] for y in range(N) for x in range(N)]
    ys = [b[y][x] for y in range(N) for x in range(N)]
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = sum((x - mx) ** 2 for x in xs) ** 0.5
    dy = sum((y - my) ** 2 for y in ys) ** 0.5
    return num / (dx * dy) if dx and dy else 0.0


print(f"{'map':<12} {'identity':>9} {'flipY':>9} {'flipX':>9} {'flipXY':>9}   winner")
for map_name in sys.argv[1:]:
    shbd = find_ci(SHBD_DIR, map_name + ".shbd")
    dds = find_ci(MINIMAP_DIR, map_name + ".dds")      # .dds only: the .tga files are LOADING SCREENS
    if not shbd or not dds:
        print(f"{map_name:<12} missing shbd or minimap .dds")
        continue
    with open(dds, "rb") as fh:
        w, h, rgba = decode_dds(fh.read())
    wg = walk_grid(shbd, N)
    bg = bright_grid(w, h, rgba, N)
    cands = {
        "identity": bg,
        "flipY": bg[::-1],
        "flipX": [row[::-1] for row in bg],
        "flipXY": [row[::-1] for row in bg[::-1]],
    }
    scores = {k: corr(wg, v) for k, v in cands.items()}
    best = max(scores, key=scores.get)
    spread = scores[best] - sorted(scores.values())[-2]
    verdict = best if spread > 0.05 else f"INCONCLUSIVE (best {best}, margin {spread:.3f})"
    print(f"{map_name:<12} " + " ".join(f"{scores[k]:9.3f}" for k in
          ("identity", "flipY", "flipX", "flipXY")) + f"   {verdict}")
