"""Search for the world window a minimap actually covers, instead of assuming one.

Two hypotheses have now been tested and the second falsified: the image is NOT the walkable
bounding box (loses on every map). The full-grid mapping wins overall but leaves RouN visibly
wrong ("Sea of Greed scaling looks pretty damn spot on, RouN is really off").

So stop proposing rules and FIT one: scan candidate square world windows (origin + size) and score
each by correlating the .shbd walkability inside that window against the minimap's painted ground.
If the best window for the maps that already look right is the full grid, and RouN's is something
else, the printout says what RouN actually needs -- and whether that value looks like a rule
(a power-of-two sub-grid, a fixed map-unit count) rather than a one-off fudge.

Usage: python tools/minimap_search.py <Map> [<Map> ...]
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from minimap_fit import find_ci, SHBD_DIR, MINIMAP_DIR       # noqa: E402
from dds_to_png import decode_dds                             # noqa: E402
from minimap_orient import bright_grid, corr                  # noqa: E402

N = 64                      # coarse: we are locating a window, not pixels
WORLD_PER_TILE = 6.25


def load_walk(path):
    with open(path, "rb") as fh:
        b = fh.read()
    bpr, height = struct.unpack_from("<ii", b, 0)
    return b[8:], bpr, bpr * 8, height


def walk_window(data, bpr, n, tx0, ty0, size):
    acc = [[0] * n for _ in range(n)]
    cnt = [[0] * n for _ in range(n)]
    for ty in range(ty0, ty0 + size):
        if ty < 0:
            continue
        gy = (ty - ty0) * n // size
        row = data[ty * bpr:(ty + 1) * bpr]
        if not row:
            break
        arow, crow = acc[gy], cnt[gy]
        for tx in range(tx0, tx0 + size):
            if tx < 0 or (tx >> 3) >= len(row):
                continue
            gx = (tx - tx0) * n // size
            crow[gx] += 1
            if not ((row[tx >> 3] >> (tx & 7)) & 1):
                arow[gx] += 1
    return [[acc[y][x] / cnt[y][x] if cnt[y][x] else 0.0 for x in range(n)] for y in range(n)]


for map_name in sys.argv[1:]:
    shbd = find_ci(SHBD_DIR, map_name + ".shbd")
    dds = find_ci(MINIMAP_DIR, map_name + ".dds")
    if not shbd or not dds:
        print(f"{map_name}: missing shbd or minimap")
        continue
    with open(dds, "rb") as fh:
        w, h, rgba = decode_dds(fh.read())
    bg = bright_grid(w, h, rgba, N)[::-1]                 # flipY, established earlier
    data, bpr, gw, gh = load_walk(shbd)

    best = []
    for frac in (1.0, 0.75, 0.625, 0.5, 0.375, 0.25):
        size = int(gw * frac)
        step = max(1, size // 8)
        for ty0 in range(0, gh - size + 1, step):
            for tx0 in range(0, gw - size + 1, step):
                c = corr(walk_window(data, bpr, N, tx0, ty0, size), bg)
                best.append((c, tx0, ty0, size))
    best.sort(reverse=True)
    c, tx0, ty0, size = best[0]
    full = [b for b in best if b[3] == gw][0][0]
    print(f"{map_name:<12} grid {gw}x{gh}  BEST corr={c:.3f} at tiles ({tx0},{ty0}) size {size} "
          f"= world x[{tx0*WORLD_PER_TILE:.0f}..{(tx0+size)*WORLD_PER_TILE:.0f}] "
          f"y[{ty0*WORLD_PER_TILE:.0f}..{(ty0+size)*WORLD_PER_TILE:.0f}]   (full-grid corr={full:.3f})")
