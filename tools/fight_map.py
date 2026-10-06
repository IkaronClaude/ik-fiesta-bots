"""fight_map.py - draw what happened to a bot on the map, from its own log window + the game data.

    python tools/fight_map.py --bot NewJoker --from 07:26:30 --to 07:27:56 [--out report.png]

Pulls the bot's log window (verbose) from the host, and from it:
  * the bot's PATH ([lq] status lines carry the position every ~2 s), ENGAGE points, the first OUTMATCHED,
    the DEATH point ([death] SELF: pos=...), SHED/KITE decisions;
  * the AGGRESSORS (mob ids from "mob N (h=..) running at us - AGGRO" and the [death] AGGRO rows);
  * the CAST BURST: casts per 2 s window vs the 5+ a human bursts, and refusals;
  * damage taken per tracked-aggressor count.
Game data (client, the bot's side of the boundary): the minimap (resmenu/minimap/<Map>.DDS; the world->pixel
mapping is the one minimap_fit.py verified: px = worldX / (shbdTiles * 6.25) * imgW), MobCoordinate.shn spawn
regions (dumped once with the collab CLI into tools/.cache), MobInfo.shn names/levels/HP.

Triggers (operator 2026-10-06): the bot's own structural alarms - the first OUTMATCHED of a fight, a death, a
HARD-WEDGE - and on demand. Never per tick.
"""
import argparse
import json
import os
import re
import struct
import subprocess
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from dds_to_png import decode_dds  # noqa: E402

try:
    from PIL import Image, ImageDraw
except ImportError:  # pragma: no cover
    raise SystemExit("pip install pillow")

HOST = os.environ.get("BOT_API", "https://bots.ikaron.uk")
TOKEN = os.environ.get("BOT_API_TOKEN", "secret-bot-token-123")
RESSYSTEM = os.environ.get("RESSYSTEM", "Z:/ClientProd2/ressystem")
MINIMAP_DIR = os.environ.get("MINIMAP_DIR", "Z:/ClientProd2/resmenu/minimap")
SHBD_DIR = os.environ.get("BLOCKINFO_DIR", "Z:/ServerSource/9Data/Shine/BlockInfo")
COLLAB_CLI = os.environ.get("COLLAB_CLI", "C:/Projects/ik-fiesta-collab/src/Fiesta.Collab.Cli")
CACHE = os.path.join(os.path.dirname(__file__), ".cache")
WORLD_PER_TILE = 6.25
HUMAN_BURST = 5          # casts a human lands in ~2 s (operator)

# ---------------------------------------------------------------- game data (cached SHN dumps) ------------------
def _dump(shn, head=20000):
    os.makedirs(CACHE, exist_ok=True)
    out = os.path.join(CACHE, os.path.basename(shn) + ".txt")
    if not os.path.exists(out) or os.path.getmtime(out) < os.path.getmtime(shn):
        txt = subprocess.run(["dotnet", "run", "-c", "Release", "--project", COLLAB_CLI, "--", "shn", shn,
                              "--head", str(head)], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
        open(out, "w", encoding="utf-8").write(txt)
    return open(out, encoding="utf-8", errors="replace").read()


def mob_coords(map_name):
    """[(mobId, cx, cy, w, h)] for every spawn REGION on the map (points are markers, not spawns)."""
    rows = []
    for l in _dump(os.path.join(RESSYSTEM, "MobCoordinate.shn")).split("\n"):
        if "│" not in l:
            continue
        f = [x.strip() for x in l.split("│")]
        if len(f) >= 8 and f[3].lower() == map_name.lower():
            try:
                rows.append((int(f[2]), int(f[4]), int(f[5]), int(f[6]), int(f[7])))
            except ValueError:
                pass
    return rows


def mob_info():
    """{id: (name, level, maxhp, grade)} from the vertical 'Row N / key = value' dump."""
    info, cur = {}, {}
    for l in _dump(os.path.join(RESSYSTEM, "MobInfo.shn")).split("\n"):
        if "─── Row" in l:
            if "ID" in cur:
                info[int(cur["ID"])] = (cur.get("Name", "?"), int(cur.get("Level", 0) or 0),
                                        int(cur.get("MaxHP", 0) or 0), int(cur.get("GradeType", 0) or 0))
            cur = {}
        elif "=" in l:
            k, _, v = l.partition("=")
            cur[k.strip()] = v.strip()
    if "ID" in cur:
        info[int(cur["ID"])] = (cur.get("Name", "?"), int(cur.get("Level", 0) or 0),
                                int(cur.get("MaxHP", 0) or 0), int(cur.get("GradeType", 0) or 0))
    return info


def find_ci(directory, *names):
    low = {e.lower(): e for e in os.listdir(directory)}
    for n in names:
        if n.lower() in low:
            return os.path.join(directory, low[n.lower()])
    return None


def world_size(map_name):
    p = find_ci(SHBD_DIR, map_name + ".shbd")
    if not p:
        return None
    bpr, h = struct.unpack("<ii", open(p, "rb").read(8))
    return bpr * 8 * WORLD_PER_TILE, h * WORLD_PER_TILE


# ---------------------------------------------------------------- the log window ---------------------------------
def fetch_log(bot, t_from, t_to):
    url = f"{HOST}/api/bots/{bot}/log?level=verbose&from={t_from}&to={t_to}&max=100000"
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {TOKEN}"})
    return urllib.request.urlopen(req, timeout=120).read().decode("utf-8", "replace").split("\n")


def tsec(line):
    m = re.match(r"(\d\d):(\d\d):(\d\d)\.(\d\d\d)", line)
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3)) + int(m.group(4)) / 1000 if m else None


def parse(lines):
    ev = {"path": [], "engage": [], "outmatched": [], "death": None, "deathpos": None, "map": None,
          "casts": [], "refusals": [], "damage": [], "aggro_mobs": {}, "shed": [], "level": None}
    for l in lines:
        t = tsec(l)
        if t is None:
            continue
        m = re.search(r"\[lq\] lvl(\d+) (\w+) \((\d+),(\d+)\) hp(-?\d+)%", l)
        if m:
            ev["level"] = int(m.group(1)); ev["map"] = ev["map"] or m.group(2)
            ev["path"].append((t, int(m.group(3)), int(m.group(4)), int(m.group(5))))
            continue
        m = re.search(r"\[death\] SELF: map=(\w+) pos=\((\d+),(\d+)\)", l)
        if m:
            ev["map"] = m.group(1); ev["deathpos"] = (int(m.group(2)), int(m.group(3))); ev["death"] = t
            continue
        if "DIED (death menu)" in l:
            ev["death"] = ev["death"] or t
        m = re.search(r"ENGAGE (.+?) \(Id (\d+)\) h=(\d+)", l)
        if m:
            ev["engage"].append((t, m.group(1), int(m.group(2))))
        if "OUTMATCHED:" in l:
            m = re.search(r"OUTMATCHED: (\d+) dmg/s vs (\d+) HP/s sustainable at (\d+) hp", l)
            ev["outmatched"].append((t, m.groups() if m else ()))
        if re.search(r"\buse skill ", l):
            ev["casts"].append(t)
        if "[castfail] 0x" in l and "cannot" in l:
            ev["refusals"].append(t)
        m = re.search(r"\[damage\] took (\d+) \(hp (\d+)->(\d+)/(\d+)\) from (\d+) tracked aggressor", l)
        if m:
            ev["damage"].append((t, int(m.group(1)), int(m.group(3)), int(m.group(4)), int(m.group(5))))
        m = re.search(r"mob (\d+) \(h=(\d+)\) running at us", l)
        if m:
            ev["aggro_mobs"][int(m.group(1))] = ev["aggro_mobs"].get(int(m.group(1)), 0) + 1
        m = re.search(r"\[death\]\s+AGGRO .*?\(Id (\d+)\)", l)
        if m:
            ev["aggro_mobs"][int(m.group(1))] = ev["aggro_mobs"].get(int(m.group(1)), 0) + 1
        if "SHED" in l or "KITE" in l:
            ev["shed"].append((t, l.split("] ", 2)[-1][:90]))
    return ev


# ---------------------------------------------------------------- the picture -------------------------------------
def render(ev, out, window):
    map_name = ev["map"]
    if not map_name:
        raise SystemExit("no map in the window (no status line / death line)")
    dds = find_ci(MINIMAP_DIR, map_name + ".dds")
    ws = world_size(map_name)
    if not dds or not ws:
        raise SystemExit(f"minimap or shbd missing for {map_name}")
    w, h, rgba = decode_dds(open(dds, "rb").read())
    img = Image.frombytes("RGBA", (w, h), bytes(rgba)).convert("RGB")
    scale = 2
    img = img.resize((w * scale, h * scale))
    W, H = img.size
    sx, sy = W / ws[0], H / ws[1]
    d = ImageDraw.Draw(img, "RGBA")
    P = lambda x, y: (x * sx, y * sy)
    info = mob_info()
    # spawn regions of the aggressors' mobs (the groups the bot pulled)
    regions = mob_coords(map_name)
    palette = [(255, 80, 80), (255, 170, 0), (200, 80, 255), (80, 200, 255), (255, 255, 80), (80, 255, 120)]
    legend = []
    for i, (mob, n) in enumerate(sorted(ev["aggro_mobs"].items(), key=lambda kv: -kv[1])):
        col = palette[i % len(palette)]
        name, lvl, hp, grade = info.get(mob, ("?", 0, 0, 0))
        legend.append(f"mob{mob} {name} L{lvl} {hp}HP{' grade'+str(grade) if grade else ''} x{n}")
        for (mid, cx, cy, rw, rh) in regions:
            if mid == mob and rw * rh > 0:
                d.rectangle([P(cx - rw / 2, cy - rh / 2), P(cx + rw / 2, cy + rh / 2)], outline=col + (255,), fill=col + (40,), width=2)
    # the path
    pts = [P(x, y) for (_, x, y, _) in ev["path"]]
    if len(pts) > 1:
        d.line(pts, fill=(255, 255, 255, 220), width=3)
    for (t, x, y, hp) in ev["path"]:
        c = (0, 255, 0) if hp >= 60 else (255, 200, 0) if hp >= 30 else (255, 0, 0)
        d.ellipse([x * sx - 3, y * sy - 3, x * sx + 3, y * sy + 3], fill=c + (255,))
    for (t, name, mob) in ev["engage"]:
        p = nearest_pos(ev, t)
        if p:
            d.ellipse([p[0] * sx - 7, p[1] * sy - 7, p[0] * sx + 7, p[1] * sy + 7], outline=(0, 200, 255, 255), width=3)
    for (t, _) in ev["outmatched"][:1]:
        p = nearest_pos(ev, t)
        if p:
            d.ellipse([p[0] * sx - 10, p[1] * sy - 10, p[0] * sx + 10, p[1] * sy + 10], outline=(255, 160, 0, 255), width=4)
    if ev["deathpos"]:
        x, y = ev["deathpos"]
        d.line([x * sx - 10, y * sy - 10, x * sx + 10, y * sy + 10], fill=(255, 0, 0, 255), width=5)
        d.line([x * sx - 10, y * sy + 10, x * sx + 10, y * sy - 10], fill=(255, 0, 0, 255), width=5)
    # text panel
    lines = summary(ev, window)
    y0 = 6
    for i, s in enumerate(lines[:14] + legend[:8]):
        d.rectangle([4, y0 - 2, 4 + 7 * len(s) + 6, y0 + 12], fill=(0, 0, 0, 170))
        d.text((8, y0), s, fill=(255, 255, 255, 255))
        y0 += 15
    img.save(out)
    return lines + legend


def nearest_pos(ev, t):
    best = None
    for (pt, x, y, _) in ev["path"]:
        if best is None or abs(pt - t) < abs(best[0] - t):
            best = (pt, x, y)
    return (best[1], best[2]) if best else None


def summary(ev, window):
    out = [f"{window[0]} bot {window[1]}  {ev['map']} lvl{ev['level']}  window {window[2]}-{window[3]} UTC"]
    if ev["death"]:
        out.append(f"DIED at {fmt(ev['death'])}  (X)")
    if ev["outmatched"]:
        t, g = ev["outmatched"][0]
        out.append(f"first OUTMATCHED at {fmt(t)}: {' / '.join(g) if g else ''}  (orange ring)")
    # cast burst: best 2 s window and the 2 s before the first outmatched / death
    casts = sorted(ev["casts"])
    best = max((sum(1 for c in casts if t <= c < t + 2) for t in casts), default=0)
    ref = len(ev["refusals"])
    anchor = (ev["outmatched"][0][0] if ev["outmatched"] else ev["death"])
    near = sum(1 for c in casts if anchor and anchor - 6 <= c <= anchor) if anchor else 0
    out.append(f"casts: {len(casts)} in window, best 2 s burst {best} (human {HUMAN_BURST}+), {near} in the 6 s before the alarm, {ref} refused")
    if ev["damage"]:
        tot = sum(x[1] for x in ev["damage"]); span = max(0.001, ev["damage"][-1][0] - ev["damage"][0][0])
        peak = max(x[4] for x in ev["damage"])
        out.append(f"damage taken: {tot} over {span:.0f} s ({tot/span:.0f}/s), attackers peaked at {peak}, maxHP {ev['damage'][0][3]}")
    for (t, name, mob) in ev["engage"][-4:]:
        out.append(f"ENGAGE {fmt(t)} {name} (mob{mob})  (blue ring)")
    for (t, s) in ev["shed"][:3]:
        out.append(f"{fmt(t)} {s}")
    return out


def fmt(t):
    return f"{int(t)//3600:02d}:{int(t)%3600//60:02d}:{int(t)%60:02d}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bot", required=True)
    ap.add_argument("--from", dest="t_from", required=True, help="UTC HH:MM:SS")
    ap.add_argument("--to", dest="t_to", required=True)
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    lines = fetch_log(a.bot, a.t_from, a.t_to)
    ev = parse(lines)
    out = a.out or os.path.join(os.path.dirname(__file__), ".cache", f"fight_{a.bot}_{a.t_from.replace(':', '')}.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    txt = render(ev, out, ("fight_map", a.bot, a.t_from, a.t_to))
    print("\n".join(txt))
    print("->", out)


if __name__ == "__main__":
    main()
