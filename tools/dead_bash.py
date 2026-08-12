#!/usr/bin/env python
"""Watch a DEAD BASH frame-by-frame, movement included.

A "dead bash" is a C->S BASHSTART (0x242B) that produced no SWING_DAMAGE from us. It is the dominant
combat failure (90%+ of bashes) and it is invisible in aggregates, so this replays the seconds around
one with MOVES KEPT IN -- our MOVERUN/STOP, the target's moves, our facing and the target's position.

Tests the standing theory: if we overshoot the mob (or meet its stand-off circle on the wrong side) the
target ends up BEHIND us, which kills the swing and the cast together.

    python tools/dead_bash.py <packets-BOT.log> [--n 3] [--window 6]
"""
import argparse, re, struct
from collections import defaultdict

HDR = re.compile(r"^\[(\d\d:\d\d:\d\d\.\d+)\] ([CS])->([SC]) 0x([0-9A-Fa-f]{4}) d=\d+ c=\d+ len=(\d+)(?: (\S+))?")
SELF = re.compile(r"^==== self handle (\d+)")
HEX = re.compile(r"^\s{2}[0-9a-f]{4}\s+((?:[0-9A-Fa-f]{2} )+)")

def parse(path):
    out, cur = [], None
    for line in open(path, encoding="utf-8", errors="replace"):
        m = HDR.match(line)
        if m:
            if cur: out.append(cur)
            cur = dict(ts=m.group(1), dir=m.group(2), op=int(m.group(4), 16),
                       name=m.group(6) or "", raw=bytearray())
            continue
        sm = SELF.match(line)
        if sm:
            # The host stamps OUR handle at every zone-enter. Handles are PER MAP (operator), so a log
            # spanning a map change carries several and the LAST one before a frame is the one in force.
            if cur: out.append(cur); cur = None
            out.append(dict(ts=None, dir="=", op=-1, name="SELF", raw=bytearray(), self=int(sm.group(1))))
            continue
        h = HEX.match(line)
        if h and cur: cur["raw"] += bytes.fromhex(h.group(1).replace(" ", ""))
    if cur: out.append(cur)
    return out

def secs(ts):
    h, m, s = ts.split(":"); return int(h)*3600 + int(m)*60 + float(s)

def u16(b, o): return b[o] | (b[o+1] << 8) if o+2 <= len(b) else None
def xy(b, o):  return struct.unpack("<II", bytes(b[o:o+8])) if o+8 <= len(b) else None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log"); ap.add_argument("--n", type=int, default=3); ap.add_argument("--window", type=float, default=6.0)
    a = ap.parse_args()
    fr = parse(a.log)
    # Timestamps are TIME-OF-DAY, so a session spanning midnight jumps 86399 -> 0 and every window
    # comparison inverts (this silently printed empty windows on the first run: the log opened 23:59
    # and the bashes were at 00:05). Make the clock MONOTONIC by carrying a day offset.
    day = 0.0; prev = None; last_t = 0.0
    for i, f in enumerate(fr):
        if f["ts"] is None:                       # self-handle marker: no clock of its own
            f["t"] = last_t; f["i"] = i; continue
        t = secs(f["ts"])
        if prev is not None and t < prev - 1: day += 86400.0
        prev = t
        f["t"] = t + day; f["i"] = i; last_t = f["t"]

    # ⛔ READ OUR HANDLE, NEVER INFER IT. The host stamps "==== self handle N ====" at every
    # zone-enter (PacketLog.NoteSelfHandle). Earlier versions of this tool guessed -- "attacker of the
    # first swing after our bash", then "whoever damages what we targeted" -- and both were wrong,
    # because SWING_* are BROADCASTS about every nearby entity and this server runs five bots plus real
    # players on the same mobs. A wrong handle makes our own swings invisible, which is how a "90% dead
    # bash" statistic got produced for a bot that was swinging normally.
    bashes = [f for f in fr if f["dir"] == "C" and f["op"] == 0x242B]
    stamps = [f for f in fr if f["dir"] == "="]
    if not stamps:
        print("no self-handle marker in this log - it predates the fix. REFUSING to guess: "
              "swing stats would be meaningless. Re-capture with a current host.")
        return
    def self_at(i):
        """Our handle in force at frame i — the last stamp at or before it (handles are per map)."""
        cur = None
        for st in stamps:
            if st["i"] <= i: cur = st["self"]
            else: break
        return cur
    me = stamps[-1]["self"]
    print(f"self handle(s) from the log: {[st['self'] for st in stamps]}")

    dead = []
    for b in bashes:
        mine = self_at(b["i"])
        hit = any(f["op"] == 0x2448 and f["dir"] == "S" and len(f["raw"]) >= 4 and u16(f["raw"], 0) == mine
                  for f in fr[b["i"]+1:] if f["t"] - b["t"] <= 4.0)
        if not hit: dead.append(b)
    print(f"dead bashes (no SWING_DAMAGE from us within 4s): {len(dead)}/{len(bashes)} "
          f"= {100*len(dead)/max(1,len(bashes)):.0f}%\n")

    NAMES = {0x2019:"MOVERUN(C)", 0x2018:"MOVEWALK(C)", 0x2012:"STOP(C)", 0x2401:"TARGET(C)",
             0x242B:"BASHSTART(C)", 0x2440:"CAST(C)", 0x2434:"CASTFAIL(S)", 0x2447:"SWING_START(S)",
             0x2448:"SWING_DMG(S)", 0x243D:"CEASE_FIRE(S)", 0x201A:"someone_run(S)", 0x2017:"someone_walk(S)",
             0x2002:"MOVE_ACK?(S)", 0x2013:"STOP_ACK(S)"}
    for b in dead[:a.n]:
        print(f"===== DEAD BASH at {b['ts']} " + "="*50)
        mypos = None
        for f in fr:
            if f["t"] < b["t"] - a.window: continue
            if f["t"] > b["t"] + a.window: break
            tag = NAMES.get(f["op"], f["name"][:26] or hex(f["op"]))
            extra = ""
            if f["dir"] == "C" and f["op"] in (0x2019, 0x2018) and len(f["raw"]) >= 16:
                frm, to = xy(f["raw"], 0), xy(f["raw"], 8)
                mypos = to
                dx, dy = to[0]-frm[0], to[1]-frm[1]
                extra = f"  me {frm} -> {to}  step=({dx:+d},{dy:+d})"
            elif f["dir"] == "C" and f["op"] == 0x2012 and len(f["raw"]) >= 8:
                mypos = xy(f["raw"], 0); extra = f"  me STOP at {mypos}"
            elif f["dir"] == "C" and f["op"] == 0x2401 and len(f["raw"]) >= 2:
                extra = f"  target=h{u16(f['raw'],0)}"
            elif f["dir"] == "S" and f["op"] in (0x201A, 0x2017) and len(f["raw"]) >= 18:
                h = u16(f["raw"], 0); to = xy(f["raw"], 10)
                d = f"  dist={((to[0]-mypos[0])**2+(to[1]-mypos[1])**2)**.5:.0f}u" if mypos and to else ""
                extra = f"  h{h} -> {to}{d}"
            elif f["dir"] == "S" and f["op"] in (0x2447, 0x2448) and len(f["raw"]) >= 4:
                extra = f"  atk=h{u16(f['raw'],0)} def=h{u16(f['raw'],2)}"
            elif f["dir"] == "S" and f["op"] == 0x2434 and len(f["raw"]) >= 2:
                extra = f"  err=0x{u16(f['raw'],0):04X}"
            mark = " <<<< BASH" if f["i"] == b["i"] else ""
            print(f"  {f['ts']} {f['dir']}  {tag:<22}{extra}{mark}")
        print()

if __name__ == "__main__":
    main()
