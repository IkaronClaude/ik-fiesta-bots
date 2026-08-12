#!/usr/bin/env python
"""How many SWINGS does each BASHSTART actually buy?

One BASHSTART starts a swing stream the server keeps running until the target dies or something
cancels it, so "swings per bash" is the single number that says whether auto-attack is working. The
real-player baseline from docs/COMBAT_BIBLE.md is 5.96; a bot at 0.20 is re-bashing into its own
windup and dealing almost no damage.

    python tools/swings_per_bash.py packets-ClericFresh.log [--gap 8]

⛔ READS OUR HANDLE, NEVER INFERS IT. The host stamps "==== self handle N ====" at every zone-enter
and handles are PER MAP, so the last stamp at or before a frame is the one in force. Both earlier
attempts at swing statistics in this repo guessed the handle — "attacker of the first swing after our
bash", then "whoever damages what we targeted" — and both were wrong, because SWING_* frames are
BROADCASTS about every nearby entity and this server runs several bots plus real players on the same
mobs. A wrong handle makes our own swings invisible, which is how a "90% dead bash" figure got
published for a bot that was swinging normally, and then retracted.

Counts 0x2448 SWING_DAMAGE (the auto-attack swing) only — NOT 0x2452 HIT_DAMAGE, which is a skill
landing. Mixing them would credit the rotation's damage to the bash.
"""
import argparse, re, statistics
from collections import Counter

HDR = re.compile(r"^\[(\d\d:\d\d:\d\d\.\d+)\] ([CS])->([SC]) 0x([0-9A-Fa-f]{4}) d=\d+ c=\d+ len=(\d+)(?: (\S+))?")
SELF = re.compile(r"^==== self handle (\d+)")
HEX = re.compile(r"^\s{2}[0-9a-f]{4}\s+((?:[0-9A-Fa-f]{2} )+)")

BASHSTART, BASHSTOP = 0x242B, 0x2432
SWING_START, SWING_DAMAGE, CEASE_FIRE = 0x2447, 0x2448, 0x243D


def parse(path):
    """Frames in order, with self-handle stamps interleaved so per-map handles stay correct."""
    out, cur = [], None
    for line in open(path, encoding="utf-8", errors="replace"):
        m = HDR.match(line)
        if m:
            if cur: out.append(cur)
            cur = dict(ts=m.group(1), dir=m.group(2), op=int(m.group(4), 16), raw=bytearray())
            continue
        sm = SELF.match(line)
        if sm:
            if cur: out.append(cur); cur = None
            out.append(dict(ts=None, dir="=", op=-1, raw=bytearray(), self=int(sm.group(1))))
            continue
        h = HEX.match(line)
        if h and cur: cur["raw"] += bytes.fromhex(h.group(1).replace(" ", ""))
    if cur: out.append(cur)
    return out


def secs(ts):
    h, m, s = ts.split(":"); return int(h) * 3600 + int(m) * 60 + float(s)


def u16(b, o): return b[o] | (b[o + 1] << 8) if o + 2 <= len(b) else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--gap", type=float, default=8.0,
                    help="seconds after a BASHSTART to keep crediting swings to it (default 8)")
    a = ap.parse_args()
    fr = parse(a.log)

    # Monotonic clock: timestamps are time-of-day, so a capture spanning midnight would invert every
    # comparison and silently report zero of everything.
    day, prev, last_t = 0.0, None, 0.0
    for i, f in enumerate(fr):
        f["i"] = i
        if f["ts"] is None: f["t"] = last_t; continue
        t = secs(f["ts"])
        if prev is not None and t < prev - 1: day += 86400.0
        prev = t
        f["t"] = t + day; last_t = f["t"]

    stamps = [f for f in fr if f["dir"] == "="]
    if not stamps:
        print("no '==== self handle N ====' marker in this log. REFUSING to guess — swing stats "
              "computed against an inferred handle are how the last two attempts got retracted.")
        return
    print(f"self handle(s) stamped in this log: {[s['self'] for s in stamps]}")

    def self_at(i):
        cur = None
        for st in stamps:
            if st["i"] <= i: cur = st["self"]
            else: break
        return cur

    bashes = [f for f in fr if f["dir"] == "C" and f["op"] == BASHSTART]
    if not bashes:
        print("no BASHSTART (0x242B) sent in this capture — the bot never auto-attacked. "
              "For a caster that is correct; for a melee class it is the bug.")
        return

    # Credit each of OUR swings to the most recent preceding BASHSTART, within --gap.
    swings, starts = [], []
    for f in fr:
        if f["dir"] != "S" or len(f["raw"]) < 4: continue
        if u16(f["raw"], 0) != self_at(f["i"]): continue        # a broadcast about somebody else
        if f["op"] == SWING_DAMAGE: swings.append(f)
        elif f["op"] == SWING_START: starts.append(f)

    per, first_delay = [], []
    for n, b in enumerate(bashes):
        nxt = bashes[n + 1]["t"] if n + 1 < len(bashes) else float("inf")
        window = min(nxt, b["t"] + a.gap)
        mine = [s for s in swings if b["t"] < s["t"] <= window]
        per.append(len(mine))
        if mine: first_delay.append((mine[0]["t"] - b["t"]) * 1000)

    ceases = [f for f in fr if f["dir"] == "S" and f["op"] == CEASE_FIRE
              and len(f["raw"]) >= 2 and u16(f["raw"], 0) == self_at(f["i"])]
    span = fr[-1]["t"] - fr[0]["t"]

    print(f"\ncapture span            {span/60:.1f} min")
    print(f"BASHSTART sent          {len(bashes)}")
    print(f"our SWING_DAMAGE        {len(swings)}   (SWING_START {len(starts)})")
    print(f"our CEASE_FIRE          {len(ceases)}")
    print(f"\nSWINGS PER BASH         mean {statistics.mean(per):.2f}"
          f"   median {statistics.median(per):.1f}   max {max(per)}")
    print(f"  real-player baseline  5.96   (docs/COMBAT_BIBLE.md)")
    dead = sum(1 for p in per if p == 0)
    print(f"  DEAD bashes (0 swings) {dead}/{len(per)} = {100*dead/len(per):.0f}%")
    print(f"  distribution           {sorted(Counter(per).items())}")
    if first_delay:
        print(f"  first swing after bash median {statistics.median(first_delay):.0f}ms")
    if len(bashes) > 1:
        gaps = [bashes[i+1]["t"] - bashes[i]["t"] for i in range(len(bashes)-1)]
        print(f"  re-bash interval       median {statistics.median(gaps)*1000:.0f}ms"
              f"  (< 418ms windup = re-bashing into our own windup)")


if __name__ == "__main__":
    main()
