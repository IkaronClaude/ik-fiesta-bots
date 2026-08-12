#!/usr/bin/env python
"""Extract ONE 1v1 fight and list every packet involving us or that mob, in order.

    python tools/one_fight.py --decoded player.txt            # a pcap_decode.py dump (real player)
    python tools/one_fight.py --botlog packets-ClericFresh.log  # a bot-side PacketLog
    python tools/one_fight.py --decoded p.txt --mobid 305     # pick the fight against a given mob

Prints the frames as a single chronological narrative, which is the only view that shows
request->response pairing — the golden rule of pcap reading in CLAUDE.md.

⛔ HOW WE KNOW WHICH HANDLE IS US — READ IT FROM THE LOGIN BURST (operator 2026-08-12).
 · botlog: the host stamps "==== self handle N ====" at every zone-enter.
 · pcap:   NC_MAP_LOGIN_ACK (0x1802) is PROTO_NC_CHAR_MAPLOGIN_ACK, whose FIRST field is
   `charhandle u16 @0` — the server telling us our own handle. Take the LATEST such burst BEFORE the
   fight, because handles are assigned per zone-entry and a capture spanning several logins carries
   several. Not an inference at all.
 ⚠️ Why this matters, measured on CombatExtensive.pcapng: it holds THREE login bursts with charhandles
   8104, 8115 and 8106 — and 8104/8106/8115 ALL appear as cease-fire targets, because they are all us
   at different times. Deriving one handle for the whole capture (say, "the handle that attacks mobs
   most often") silently blends three sessions. Inferring the self handle is what produced, and then
   forced the retraction of, a "90% dead bash" statistic; SWING_* frames are BROADCASTS about every
   nearby entity and several bots plus real players share the same mobs on this server.
"""
import argparse, re, sys
from collections import Counter, defaultdict

HDRP = re.compile(r"^\s*([SC])(?:<-|->)\s*@\s*(\d+)\s+\[0x([0-9A-Fa-f]{4})\]\s+(\S+)")
HDRB = re.compile(r"^\[(\d\d:\d\d:\d\d\.\d+)\] ([CS])->([SC]) 0x([0-9A-Fa-f]{4}) d=\d+ c=\d+ len=(\d+)(?: (\S+))?")
SELFB = re.compile(r"^==== self handle (\d+)")


def parse_decoded(text):
    """pcap_decode.py output -> frames in FILE ORDER (which is chronological: it interleaves)."""
    frames, cur = [], None
    for line in text.split("\n"):
        m = HDRP.match(line)
        if m:
            if cur: frames.append(cur)
            cur = dict(dir=m.group(1), at=int(m.group(2)), op=int(m.group(3), 16), name=m.group(4), body=[])
            continue
        if cur is not None: cur["body"].append(line)
    if cur: frames.append(cur)
    for f in frames: f["text"] = "\n".join(f["body"])
    return frames


def parse_botlog(text):
    frames, cur, me = [], None, None
    for line in text.split("\n"):
        s = SELFB.match(line)
        if s: me = int(s.group(1)); continue
        m = HDRB.match(line)
        if m:
            if cur: frames.append(cur)
            cur = dict(dir=m.group(2), at=m.group(1), op=int(m.group(4), 16),
                       name=m.group(6) or f"0x{m.group(4)}", body=[], self=me)
            continue
        if cur is not None: cur["body"].append(line)
    if cur: frames.append(cur)
    for f in frames: f["text"] = "\n".join(f["body"])
    return frames, me


def num(f, name):
    m = re.search(r"\b" + name + r"\s+(\d+)", f["text"])
    return int(m.group(1)) if m else None


def hexnums(f):
    """Every u16 that appears in the frame's hex, for the botlog case where fields are not decoded."""
    out = []
    for line in f["body"]:
        # pcap_decode indents hex by 8, the botlog by 2 — accept either.
        m = re.match(r"^\s+[0-9a-f]{4}\s+((?:[0-9A-Fa-f]{2} )+)", line)
        if m: out += list(bytes.fromhex(m.group(1).replace(" ", "")))
    return out


def main():
    ap = argparse.ArgumentParser()
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--decoded"); src.add_argument("--botlog")
    ap.add_argument("--mobid", type=int, help="pick the fight against this mob id")
    ap.add_argument("--pad", type=int, default=2, help="seconds/frames of lead-in to include")
    a = ap.parse_args()

    if a.botlog:
        frames, me = parse_botlog(open(a.botlog, encoding="utf-8", errors="replace").read())
        if me is None: sys.exit("no self-handle stamp in this botlog — refusing to guess.")
        print(f"self handle {me}  (from the host's own stamp)")
        getb = lambda f, o: (hexnums(f)[o] | (hexnums(f)[o + 1] << 8)) if len(hexnums(f)) > o + 1 else None
        atk = lambda f: getb(f, 0); dfd = lambda f: getb(f, 2)
        isop = lambda f, op: f["op"] == op
        SWING, CEASE = 0x2448, 0x243D
    else:
        frames = parse_decoded(open(a.decoded, encoding="utf-8", errors="replace").read())
        atk = lambda f: num(f, "attacker"); dfd = lambda f: num(f, "defender")
        isop = lambda f, op: f["op"] == op
        SWING, CEASE = 0x2448, 0x243D
        # THE LOGIN BURST NAMES US. NC_MAP_LOGIN_ACK = PROTO_NC_CHAR_MAPLOGIN_ACK {charhandle u16 @0}.
        # pcap_decode does not expand this struct's fields, so read the first u16 out of its hex.
        logins = []   # (frame index, charhandle)
        for i, f in enumerate(frames):
            if f["op"] != 0x1802: continue
            b = hexnums(f)
            if len(b) >= 2: logins.append((i, b[0] | (b[1] << 8)))
        if not logins:
            sys.exit("no NC_MAP_LOGIN_ACK in this capture — the login burst is where our handle is "
                     "stated, and REFUSING to infer it from combat frames (see the docstring).")
        print(f"login bursts in this capture: {[f'@{i}->h{h}' for i, h in logins]}")
        # EVERY charhandle in the capture is us, at some point. Use the whole set to FIND the fight,
        # then narrow to the one handle in force for that fight (the latest burst before it starts).
        # Seeding with just the last burst finds nothing when the fight belongs to an earlier session.
        me_all = {h for _, h in logins}
        me = logins[-1][1]

    # mob handle -> mobid, from REGENMOB
    mobid = {}
    for f in frames:
        if "REGENMOB" in f["name"]:
            h, mid = num(f, "handle"), num(f, "mobid")
            if h is not None and mid is not None: mobid[h] = mid

    # Who did we hit most? That is the fight.
    who_is_me = me_all if not a.botlog else {me}
    hit = Counter()
    for f in frames:
        if isop(f, SWING) and atk(f) in who_is_me and dfd(f) is not None:
            if a.mobid is None or mobid.get(dfd(f)) == a.mobid: hit[dfd(f)] += 1
    if not hit: sys.exit("we never damaged anything in this capture" + (f" with mobid {a.mobid}" if a.mobid else ""))
    tgt, n = hit.most_common(1)[0]
    print(f"fight target h{tgt} (mob {mobid.get(tgt, '?')}), {n} swings landed on it\n")

    idx = [i for i, f in enumerate(frames)
           if atk(f) == tgt or dfd(f) == tgt or num(f, "handle") == tgt
           or (atk(f) == me or dfd(f) == me or num(f, "handle") == me)]
    if not idx: sys.exit("no frames matched")
    lo, hi = max(0, min(idx) - a.pad), min(len(frames), max(idx) + a.pad + 1)
    # Trim to the window in which THIS target was actually involved.
    tidx = [i for i in idx if atk(frames[i]) == tgt or dfd(frames[i]) == tgt or num(frames[i], "handle") == tgt]
    lo, hi = max(0, min(tidx) - 8), min(len(frames), max(tidx) + 8)

    for i in range(lo, hi):
        f = frames[i]
        if i not in idx: continue
        who = []
        for lbl, v in (("atk", atk(f)), ("def", dfd(f)), ("h", num(f, "handle"))):
            if v is not None: who.append(f"{lbl}={'ME' if v == me else ('MOB' if v == tgt else v)}")
        extra = ""
        for k in ("damage", "resthp", "skill", "index", "target", "targetobj"):
            v = num(f, k)
            if v is not None: extra += f" {k}={v}"
        at = f["at"]
        print(f"  {at}  {'C->' if f['dir']=='C' else 'S<-'} {f['name'][:44]:44} {' '.join(who):28}{extra}")


if __name__ == "__main__":
    main()
