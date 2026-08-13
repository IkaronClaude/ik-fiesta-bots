"""Preview the wall-hugging kite loop for a map, and render it over the .shbd.

Mirrors Navigation/KiteLoop.cs: clearance BFS -> drop cells narrower than a corridor -> component
containing the bot -> inset -> outer contour. Lets us see the loop the bot will actually run
(size, shape, whether corridors were skipped) before trusting it in a fight.

Usage: python tools/kite_loop_probe.py <Map> <x> <y> [corridorMinWidth] [out.png]
"""
import os
import struct
import sys
import zlib
from collections import deque

SHBD_DIR = os.environ.get("BLOCKINFO_DIR", "Z:/ServerSource/9Data/Shine/BlockInfo")
WORLD_PER_TILE = 6.25
SHIFT = 1


def load(map_name):
    for f in os.listdir(SHBD_DIR):
        if f.lower() == (map_name + ".shbd").lower():
            with open(os.path.join(SHBD_DIR, f), "rb") as fh:
                b = fh.read()
            bpr, h = struct.unpack_from("<ii", b, 0)
            return b[8:], bpr, bpr * 8, h
    raise SystemExit(f"no .shbd for {map_name}")


def walk_tile(data, bpr, w, h, tx, ty):
    if tx < 0 or ty < 0 or tx >= w or ty >= h:
        return False
    off = ty * bpr + (tx >> 3)
    return off < len(data) and ((data[off] >> (tx & 7)) & 1) == 0


def build(map_name, px, py, corridor_w, span_world=5000, margin=40, max_points=48):
    data, bpr, W, H = load(map_name)
    btx, bty = int(px / WORLD_PER_TILE) + SHIFT, int(py / WORLD_PER_TILE) + SHIFT
    span = int(span_world / WORLD_PER_TILE / 2)
    x0, y0 = max(0, btx - span), max(0, bty - span)
    x1, y1 = min(W - 1, btx + span), min(H - 1, bty + span)
    w, h = x1 - x0 + 1, y1 - y0 + 1

    INF = 1 << 30
    clear = [INF] * (w * h)
    q = deque()
    for i in range(w * h):
        lx, ly = i % w, i // w
        edge = lx == 0 or ly == 0 or lx == w - 1 or ly == h - 1
        if edge or not walk_tile(data, bpr, W, H, x0 + lx, y0 + ly):
            clear[i] = 0
            q.append(i)
    while q:
        i = q.popleft()
        cx, cy = i % w, i // w
        for nx, ny in ((cx+1, cy), (cx-1, cy), (cx, cy+1), (cx, cy-1)):
            if 0 <= nx < w and 0 <= ny < h:
                ni = ny * w + nx
                if clear[ni] > clear[i] + 1:
                    clear[ni] = clear[i] + 1
                    q.append(ni)

    min_clear = max(2, round(corridor_w / WORLD_PER_TILE / 2))
    keep = [c >= min_clear for c in clear]

    sx, sy = btx - x0, bty - y0
    start = -1
    if 0 <= sx < w and 0 <= sy < h and keep[sy * w + sx]:
        start = sy * w + sx
    else:
        for r in range(1, max(w, h)):
            for dy in range(-r, r + 1):
                for dx in range(-r, r + 1):
                    if abs(dx) != r and abs(dy) != r:
                        continue
                    nx, ny = sx + dx, sy + dy
                    if 0 <= nx < w and 0 <= ny < h and keep[ny * w + nx]:
                        start = ny * w + nx
                        break
                if start >= 0: break
            if start >= 0: break
    if start < 0:
        return None, None, None, (w, h)

    comp = [False] * (w * h)
    st = [start]; comp[start] = True; n = 1
    while st:
        i = st.pop()
        cx, cy = i % w, i // w
        for nx, ny in ((cx+1, cy), (cx-1, cy), (cx, cy+1), (cx, cy-1)):
            if 0 <= nx < w and 0 <= ny < h:
                ni = ny * w + nx
                if keep[ni] and not comp[ni]:
                    comp[ni] = True; n += 1; st.append(ni)
    if n < 64:
        return None, None, None, (w, h)

    inset = max(1, round(margin / WORLD_PER_TILE))
    body = [comp[i] and clear[i] >= min_clear + inset for i in range(w * h)]
    if not any(body):
        body = comp
    # The inset can split the room into fragments; re-take the blob we are standing in, or the trace
    # starts on a stray fragment (measured: a 5-waypoint 6x6u "loop").
    bs = -1
    if 0 <= sx < w and 0 <= sy < h and body[sy * w + sx]:
        bs = sy * w + sx
    else:
        for r in range(1, max(w, h)):
            for dy in range(-r, r + 1):
                for dx in range(-r, r + 1):
                    if abs(dx) != r and abs(dy) != r:
                        continue
                    nx, ny = sx + dx, sy + dy
                    if 0 <= nx < w and 0 <= ny < h and body[ny * w + nx]:
                        bs = ny * w + nx
                        break
                if bs >= 0: break
            if bs >= 0: break
    if bs < 0:
        return None, None, None, (w, h)
    keep2 = [False] * (w * h)
    st = [bs]; keep2[bs] = True
    while st:
        i = st.pop()
        cx, cy = i % w, i // w
        for nx, ny in ((cx+1, cy), (cx-1, cy), (cx, cy+1), (cx, cy-1)):
            if 0 <= nx < w and 0 <= ny < h:
                ni = ny * w + nx
                if body[ni] and not keep2[ni]:
                    keep2[ni] = True; st.append(ni)
    body = keep2

    # Moore contour trace
    startIdx = next((i for i, b in enumerate(body) if b), -1)
    dxs = [-1, -1, 0, 1, 1, 1, 0, -1]
    dys = [0, -1, -1, -1, 0, 1, 1, 1]
    cx, cy = startIdx % w, startIdx // w
    sxx, syy = cx, cy
    # Start so the first backtrack probe is WEST of a topmost-leftmost cell (guaranteed outside the
    # shape). With d=0 the probe starts north-west and the trace closes after a handful of cells.
    d = 3
    ring = []
    for _ in range(w * h * 4):
        ring.append((cx, cy))
        back = (d + 5) % 8
        found = False
        for k in range(8):
            nd = (back + k) % 8
            nx, ny = cx + dxs[nd], cy + dys[nd]
            if 0 <= nx < w and 0 <= ny < h and body[ny * w + nx]:
                cx, cy, d, found = nx, ny, nd, True
                break
        if not found or (cx == sxx and cy == syy):
            break

    step = max(1, len(ring) // max(8, max_points))
    pts = [((x0 + lx - SHIFT + 0.5) * WORLD_PER_TILE, (y0 + ly - SHIFT + 0.5) * WORLD_PER_TILE)
           for lx, ly in ring[::step]]
    return pts, ring, (body, comp, w, h, x0, y0, data, bpr, W, H), (w, h)


def png(path, w, h, rgb):
    raw = b"".join(b"\x00" + bytes(rgb[y * w * 3:(y + 1) * w * 3]) for y in range(h))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n"
                 + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


map_name, px, py = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
corridor = float(sys.argv[4]) if len(sys.argv) > 4 else 260
out = sys.argv[5] if len(sys.argv) > 5 else None

pts, ring, dbg, dims = build(map_name, px, py, corridor)
if not pts:
    print(f"{map_name}: NO loop (window {dims[0]}x{dims[1]} tiles, corridorMinWidth={corridor})")
    raise SystemExit(1)

xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
per = sum(((pts[i][0]-pts[i-1][0])**2 + (pts[i][1]-pts[i-1][1])**2) ** 0.5 for i in range(1, len(pts)))
print(f"{map_name}: loop of {len(pts)} waypoints, contour {len(ring)} tiles")
print(f"  extent world x[{min(xs):.0f}..{max(xs):.0f}] y[{min(ys):.0f}..{max(ys):.0f}]")
print(f"  span {max(xs)-min(xs):.0f} x {max(ys)-min(ys):.0f}u, perimeter ~{per:.0f}u")
print(f"  vs the best inscribed circle measured earlier (r=524u -> circumference ~3293u)")

if out:
    body, comp, w, h, x0, y0, data, bpr, W, H = dbg
    img = bytearray(w * h * 3)
    for i in range(w * h):
        lx, ly = i % w, i // w
        walk = walk_tile(data, bpr, W, H, x0 + lx, y0 + ly)
        c = (24, 26, 32) if not walk else ((60, 70, 85) if not comp[i] else (90, 110, 130))
        img[i*3:i*3+3] = bytes(c)
    for lx, ly in ring:
        i = ly * w + lx
        img[i*3:i*3+3] = bytes((80, 220, 120))
    png(out, w, h, img)
    print(f"  wrote {out}")
