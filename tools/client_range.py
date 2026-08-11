#!/usr/bin/env python
"""Measure the range at which the REAL game client actually initiates attacks.

Answers a question no data file does: melee skills declare ActiveSkill.Range = 0 (contact), so the
only way to know what "contact" means in world units is to watch a real player cast and measure the
distance to the target at that instant.

Reconstructs positions from a pcap_decode.py dump (run WITHOUT --hide-movement: movement broadcasts
ARE the position source) and, for every C->S cast/target request, reports the distance to the target's
last known position. File order within a conversation is the chronology -- the `@` offsets are NOT.

    python pcap_decode.py <cap>.pcapng --no-hex > cap.txt
    python tools/client_range.py cap.txt [--skill N]
"""
import argparse, re, struct, sys, statistics, collections

FRAME = re.compile(r"^\s*(S<-|C->) @\s*\d+\s+\[(0x[0-9A-Fa-f]{4})\]\s+(\S+)")
HEXROW = re.compile(r"^\s*[0-9a-f]{4}\s+((?:[0-9a-f]{2} )+)")
# NOTE: the type can contain spaces ("unsigned short"), so split on the dump's COLUMN gaps
# (2+ spaces), not on single whitespace -- doing the latter parses the field name as "short".
FIELD = re.compile(r"^\s*@\s*(\d+)\s{2,}(.+?)\s{2,}(\w+)\s{2,}(.*)$")
XY    = re.compile(r"SHINE_XY_TYPE:\s*((?:[0-9a-f]{2} ?)+)")

def xy(val):
    m = XY.search(val)
    if not m: return None
    b = bytes.fromhex(m.group(1).replace(" ", ""))
    if len(b) < 8: return None
    return struct.unpack("<II", b[:8])

def num(val):
    m = re.match(r"^(-?\d+)", val.strip())
    return int(m.group(1)) if m else None

def conversations(text):
    """Split into per-conversation blocks; positions only make sense inside one."""
    parts = re.split(r"^==== server (\S+) <-> client (\S+) ====$", text, flags=re.M)
    for i in range(1, len(parts), 3):
        yield parts[i], parts[i+2]

def analyse(block):
    pos = {}          # handle -> (x, y)   last known, from movement broadcasts + briefinfo
    me = None         # my own last position, from my own C->S move requests
    target = None     # my last TARGETTING_REQ target handle
    events = []
    cur = None        # (dir, opcode, name)
    fields = {}
    raw = bytearray()
    def flush():
        nonlocal me, target
        if not cur: return
        d, op, name = cur
        # Frames with no PDB struct (my own MOVERUN/STOP/TARGETTING) carry everything in the hex.
        if d == "C->" and op in ("0x2019", "0x2018") and len(raw) >= 16:
            me = struct.unpack("<II", bytes(raw[8:16]))          # 16b = from(x,y) + to(x,y)
        if d == "C->" and op == "0x2012" and len(raw) >= 8:
            me = struct.unpack("<II", bytes(raw[0:8]))           # STOP_REQ = where I stopped
        if d == "C->" and op == "0x2401" and len(raw) >= 2:
            target = raw[0] | (raw[1] << 8)
        if d == "C->" and op == "0x2440" and len(raw) >= 4 and "skill" not in fields:
            fields["skill"]  = raw[0] | (raw[1] << 8)
            fields["target"] = raw[2] | (raw[3] << 8)
        # --- position sources -------------------------------------------------------------
        h = fields.get("handle")
        to = fields.get("to") or fields.get("coord") or fields.get("pos")
        if d == "S<-" and h is not None and to:
            pos[h] = to
        if d == "C->" and to and ("MOVE" in name or "RUN" in name or "WALK" in name):
            me = to                      # my own movement request carries MY position
        # briefinfo-style spawn rows also carry a position for a handle
        if d == "S<-" and h is not None and fields.get("from") and h not in pos:
            pos[h] = fields["from"]
        # --- the events we are measuring ---------------------------------------------------
        if d == "C->" and op == "0x2401" and isinstance(fields.get("target"), int):
            target = fields["target"]
        if d == "C->" and op in ("0x2440", "0x242B"):
            t = fields.get("target") if op == "0x2440" else target
            skill = fields.get("skill") if op == "0x2440" else None
            if isinstance(t, int) and me and t in pos:
                dx, dy = me[0] - pos[t][0], me[1] - pos[t][1]
                events.append((op, skill, (dx*dx + dy*dy) ** 0.5))
    for line in block.splitlines():
        m = FRAME.match(line)
        if m:
            flush(); cur = (m.group(1), m.group(2), m.group(3)); fields = {}; raw = bytearray(); continue
        hr = HEXROW.match(line)
        if hr and cur:
            raw += bytes.fromhex(hr.group(1).replace(" ", "")); continue
        f = FIELD.match(line)
        if f and cur:
            name, val = f.group(3), f.group(4)
            p = xy(val)
            fields[name] = p if p else num(val)
    flush()
    return events

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dump"); ap.add_argument("--skill", type=int)
    a = ap.parse_args()
    text = open(a.dump, encoding="utf-8", errors="replace").read()
    allev = []
    for label, block in conversations(text):
        ev = analyse(block)
        if ev: print(f"  {label}: {len(ev)} measurable attack events")
        allev += ev
    if not allev: sys.exit("no measurable events -- was the dump made WITHOUT --hide-movement?")
    for op, tag in (("0x2440", "SKILL CAST (0x2440)"), ("0x242B", "AUTO-ATTACK bash (0x242B)")):
        ds = [d for o, s, d in allev if o == op and (a.skill is None or s == a.skill)]
        if not ds: continue
        ds.sort()
        print(f"\n=== {tag}: n={len(ds)}")
        print(f"    min={ds[0]:.0f}u  median={statistics.median(ds):.0f}u  max={ds[-1]:.0f}u")
        qs = [f"p{int(q*100)}={ds[min(len(ds)-1,int(len(ds)*q))]:.0f}" for q in (.5,.75,.9,.95,.99)]
        print("    " + "  ".join(qs))
        hist = collections.Counter(int(d // 25) * 25 for d in ds)
        for b in sorted(hist): print(f"      {b:4d}-{b+24:<4d}u  {'#' * min(60, hist[b])} {hist[b]}")
    by = collections.defaultdict(list)
    for o, s, d in allev:
        if o == "0x2440" and s is not None: by[s].append(d)
    if by:
        print("\n=== per-skill max distance the client cast at (vs ActiveSkill.Range)")
        for s in sorted(by, key=lambda k: -len(by[k]))[:12]:
            v = sorted(by[s])
            print(f"    skill {s:5d}  n={len(v):4d}  median={statistics.median(v):6.0f}u  max={v[-1]:6.0f}u")

if __name__ == "__main__":
    main()
