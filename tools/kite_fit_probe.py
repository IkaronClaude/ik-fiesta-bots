"""What size kite circle actually fits on a map, near a point?

The kite loop must have a diameter of at least 4x the chaser's range or it achieves nothing
(operator: the enemy "just needs to take small adjustment steps to attack"). This probe answers
whether such a circle EXISTS on a given map before we rely on it — measured off the same .shbd
the bot navigates with, using the same rim-walkability test as Navigation/KiteCircle.cs.

Usage: python tools/kite_fit_probe.py <Map> <x> <y> [maxDiameter]
"""
import math
import os
import struct
import sys

SHBD_DIR = os.environ.get("BLOCKINFO_DIR", "Z:/ServerSource/9Data/Shine/BlockInfo")
WORLD_PER_TILE = 6.25
SHIFT = 1                      # BlockGrid.ShbdTileShift
SAMPLES = 32


def load(map_name):
    for f in os.listdir(SHBD_DIR):
        if f.lower() == (map_name + ".shbd").lower():
            with open(os.path.join(SHBD_DIR, f), "rb") as fh:
                b = fh.read()
            bpr, h = struct.unpack_from("<ii", b, 0)
            return b[8:], bpr, bpr * 8, h
    raise SystemExit(f"no .shbd for {map_name} in {SHBD_DIR}")


def walkable_world(data, bpr, w, h, x, y):
    tx = int(x / WORLD_PER_TILE) + SHIFT
    ty = int(y / WORLD_PER_TILE) + SHIFT
    if tx < 0 or ty < 0 or tx >= w or ty >= h:
        return False
    off = ty * bpr + (tx >> 3)
    if off >= len(data):
        return False
    return ((data[off] >> (tx & 7)) & 1) == 0


def rim_ok(data, bpr, w, h, cx, cy, r):
    px = py = None
    for i in range(SAMPLES + 1):
        a = i * (2 * math.pi / SAMPLES)
        x, y = cx + math.cos(a) * r, cy + math.sin(a) * r
        if x < 0 or y < 0 or not walkable_world(data, bpr, w, h, x, y):
            return False
        if px is not None and not walkable_world(data, bpr, w, h, (x + px) / 2, (y + py) / 2):
            return False
        px, py = x, y
    return True


def centres(px, py, r, leeway=100):
    yield px, py
    for frac in (0.34, 0.66, 1.0):
        for i in range(8):
            a = i * (math.pi / 4)
            d = min(r * frac, r + leeway)
            yield px + math.cos(a) * d, py + math.sin(a) * d


map_name, bx, by = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
max_d = float(sys.argv[4]) if len(sys.argv) > 4 else 5000
data, bpr, w, h = load(map_name)
print(f"{map_name}: grid {w}x{h} tiles, probing around ({bx:.0f},{by:.0f})")
print(f"bot's own tile walkable: {walkable_world(data, bpr, w, h, bx, by)}")

best = None
r = max_d / 2
while r >= 50:
    for cx, cy in centres(bx, by, r):
        if rim_ok(data, bpr, w, h, cx, cy, r):
            best = (cx, cy, r)
            break
    if best:
        break
    r *= 0.8

if best:
    print(f"LARGEST FITTING CIRCLE: r={best[2]:.0f}u (diameter {best[2]*2:.0f}u) at ({best[0]:.0f},{best[1]:.0f})")
    print(f"  -> supports an enemy range up to {best[2]/2:.0f}u at the operator's 4x-diameter rule")
else:
    print("NO circle fits at any radius down to 50u")
