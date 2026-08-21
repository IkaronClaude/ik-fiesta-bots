#!/usr/bin/env python
"""Derive the INCOMING damage formula from a controlled stat capture.

    python tools/damage_fit.py --pcap Z:/Damage.pcapng
    python tools/damage_fit.py --decoded dump.txt          # reuse an existing decode
    python tools/damage_fit.py --pcap Z:/Damage.pcapng --params   # just the param-id timeline

Z:/Damage.pcapng is an EXPERIMENT, not a fight: the operator raised END (+3 -> +20 -> 50), then stripped
armour piece by piece, standing still and taking hits, narrating each step in chat. That design is what
makes a formula recoverable at all -- damage is sampled against one stat being moved deliberately while
everything else is held still. Read the chat first (--params prints it); it is the legend.

HOW IT READS THE CAPTURE
  NC_CHAR_CHANGEPARAMCHANGE_CMD (0x1035) is [changenum u8][(paramId u8, value u32) x changenum]. Verified
  by arithmetic on the wire, not assumed: a 131-byte payload is exactly 1 + 26*5, and the first pair
  decodes as id 0x10 = 2453, the same number the NC_BAT_HPCHANGE_CMD two frames later reports as hp.
  There is no param-id enum in the PDB extract (all-enums.json holds opcodes only), so ids are named
  EMPIRICALLY -- by which id moves when the operator says he moved something.

  NC_BAT_SWING_DAMAGE_CMD (0x2448) is attacker u16 @0, defender u16 @2, damage u16 @6, resthp u32 @8,
  taken from combat_timeline.py, which reconciles total damage against MobInfo MaxHP.

WHICH HANDLE IS "ME" IS DERIVED, NOT GUESSED. CHANGEPARAMCHANGE carries no handle (it is self by
definition), so self is found by pairing: a SWING_DAMAGE whose resthp equals the hp of the NEXT
HPCHANGE frame was a hit on US. Assuming "the most-hit handle is me" breaks as soon as the player
out-damages the mobs, which is most of any real capture.

ORDERING: `@` offsets are per-direction baselines, NOT a clock (see combat_timeline.py). pcap_decode
interleaves by timestamp by default, so FILE ORDER is the chronology. Everything here is indexed by
position in the interleaved stream.
"""
import argparse, os, re, struct, subprocess, sys, tempfile
from collections import defaultdict, Counter

PCAP_DECODE = r"C:/Projects/fiesta-proxy/tools/pcap_decode.py"
XOR_TABLE   = r"C:/Projects/ik-fiesta-bots/xor-table.hex"

HEXLINE = re.compile(r"^\s{4,}[0-9a-f]{4}\s+((?:[0-9a-f]{2} )+)")
FRAME   = re.compile(r"^\s*([CS])(?:->|<-)\s+@\s*(\d+)\s+\[(0x[0-9A-Fa-f]{4})\]\s+(\S+)")


def decode(pcap):
    out = os.path.join(tempfile.gettempdir(), "damage_fit_decode.txt")
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE)
    with open(out, "w", encoding="utf-8") as fh:
        subprocess.run([sys.executable, PCAP_DECODE, pcap, "--hide-movement"],
                       stdout=fh, stderr=subprocess.DEVNULL, env=env, check=True)
    return out


def frames(path):
    """Yield (dir, opcode, name, payload) in FILE order, which is timestamp order."""
    cur, hexb = None, []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = FRAME.match(line)
            if m:
                if cur:
                    yield cur + (bytes(hexb),)
                cur, hexb = (m.group(1), int(m.group(3), 16), m.group(4)), []
                continue
            h = HEXLINE.match(line)
            if h and cur:
                hexb.extend(int(x, 16) for x in h.group(1).split())
    if cur:
        yield cur + (bytes(hexb),)


def parse_params(b):
    """[changenum u8][(id u8, value u32) x n]. Length-checked, so a wrong guess cannot pass silently."""
    if not b:
        return None
    n = b[0]
    if len(b) < 1 + 5 * n:
        return None
    return {b[1 + i * 5]: struct.unpack_from("<I", b, 2 + i * 5)[0] for i in range(n)}


def chat_text(b):
    """[itemLinkDataCount u8][len u8][text]. The struct field misreads chat; the bytes do not."""
    if len(b) < 2:
        return ""
    return b[2:2 + b[1]].decode("latin-1", "replace").strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pcap")
    ap.add_argument("--decoded")
    ap.add_argument("--params", action="store_true", help="only show the param timeline + annotations")
    a = ap.parse_args()
    path = a.decoded or decode(a.pcap or "Z:/Damage.pcapng")

    fs = list(frames(path))

    # self-identification: SWING_DAMAGE.resthp == hp of the following HPCHANGE
    me = Counter()
    for i, (d, op, name, b) in enumerate(fs):
        if name == "NC_BAT_SWING_DAMAGE_CMD" and len(b) >= 12:
            resthp = struct.unpack_from("<I", b, 8)[0]
            for j in range(i + 1, min(i + 6, len(fs))):
                if fs[j][2] == "NC_BAT_HPCHANGE_CMD" and len(fs[j][3]) >= 4:
                    if struct.unpack_from("<I", fs[j][3], 0)[0] == resthp:
                        me[struct.unpack_from("<H", b, 2)[0]] += 1
                    break
    self_h = me.most_common(1)[0][0] if me else None
    print("self handle = 0x%04X (paired %d SWING_DAMAGE resthp -> HPCHANGE hp)" % (self_h, me[self_h])
          if self_h is not None else "could not identify self handle")

    params, hits, events = {}, [], []
    changed = defaultdict(list)
    for idx, (d, op, name, b) in enumerate(fs):
        if name == "NC_CHAR_CHANGEPARAMCHANGE_CMD":
            p = parse_params(b)
            if p:
                for k, v in p.items():
                    if params.get(k) != v:
                        changed[k].append((idx, params.get(k), v))
                params.update(p)
        elif name == "NC_ACT_CHAT_REQ" and d == "C":
            t = chat_text(b)
            if t:
                events.append((idx, "CHAT", t))
        elif name == "NC_BRIEFINFO_UNEQUIP_CMD":
            events.append((idx, "UNEQUIP", ""))
        elif name == "NC_BAT_SWING_DAMAGE_CMD" and len(b) >= 12 and self_h is not None:
            atk = struct.unpack_from("<H", b, 0)[0]
            dfn = struct.unpack_from("<H", b, 2)[0]
            if dfn == self_h:
                hits.append({"i": idx, "atk": atk,
                             "dmg": struct.unpack_from("<H", b, 6)[0], "params": dict(params)})

    print("frames=%d  hits taken=%d  param updates=%d"
          % (len(fs), len(hits), sum(len(v) for v in changed.values())))

    print("\n=== PARAM IDS THAT MOVED ===")
    for k in sorted(changed, key=lambda k: -len(changed[k])):
        vals = sorted({v for _, _, v in changed[k]})
        print("  0x%02X (%3d): %3d changes  range %d..%d  distinct=%d"
              % (k, k, len(changed[k]), vals[0], vals[-1], len(vals)))

    if a.params:
        print("\n=== ANNOTATIONS (chronological) ===")
        for i, kind, t in events:
            if kind == "CHAT":
                print("  @%6d  %s" % (i, t))
        return

    print("\n=== DAMAGE TAKEN, BY ATTACKER ===")
    by_atk = defaultdict(list)
    for h in hits:
        by_atk[h["atk"]].append(h)
    for atk, hs in sorted(by_atk.items(), key=lambda kv: -len(kv[1])):
        d = [h["dmg"] for h in hs]
        print("  attacker 0x%04X: n=%4d  min=%4d max=%4d mean=%7.2f  distinct=%d"
              % (atk, len(d), min(d), max(d), sum(d) / len(d), len(set(d))))

    # CONTROL FOR THE ATTACKER. Different mobs hit for different amounts, so comparing a pooled mean
    # before a stat change against a pooled mean after it mostly measures WHICH MOB was hitting you.
    # Only a mob observed at two or more values of the same param says anything about that param.
    # Misses are counted separately, never averaged in: a 0 is a miss/block, not mitigation, and folding
    # them into the mean makes any change in hit rate look like a change in damage.
    print("\n=== SAME ATTACKER, DIFFERENT PARAM VALUE (the only comparison that controls for the mob) ===")
    for k in sorted(changed, key=lambda k: -len(changed[k])):
        cell, miss, seen = defaultdict(list), defaultdict(int), defaultdict(set)
        for h in hits:
            v = h["params"].get(k)
            if v is None:
                continue
            seen[h["atk"]].add(v)
            if h["dmg"] == 0:
                miss[(h["atk"], v)] += 1
            else:
                cell[(h["atk"], v)].append(h["dmg"])
        multi = [a for a in seen if len(seen[a]) >= 2 and sum(len(cell[(a, v)]) for v in seen[a]) >= 8]
        if not multi:
            continue
        print("\n  param 0x%02X:" % k)
        for atk in multi:
            parts = []
            for v in sorted(seen[atk]):
                d = cell[(atk, v)]
                if not d:
                    continue
                parts.append("%d: n=%d mean=%.2f [%d..%d] miss=%d"
                             % (v, len(d), sum(d) / len(d), min(d), max(d), miss[(atk, v)]))
            if len(parts) >= 2:
                print("    mob 0x%04X  %s" % (atk, "  |  ".join(parts)))

    # A param that MITIGATES must move damage when it moves, with the attacker held fixed.
    print("\n=== CANDIDATE MITIGATION PARAMS (damage vs param value, per attacker) ===")
    movers = [k for k in changed if len(changed[k]) >= 2]
    for atk, hs in sorted(by_atk.items(), key=lambda kv: -len(kv[1]))[:4]:
        if len(hs) < 12:
            continue
        print("\n  -- attacker 0x%04X (n=%d) --" % (atk, len(hs)))
        for k in movers:
            buckets = defaultdict(list)
            for h in hs:
                if k in h["params"]:
                    buckets[h["params"][k]].append(h["dmg"])
            buckets = {v: d for v, d in buckets.items() if len(d) >= 3}
            if len(buckets) < 2:
                continue
            pts = sorted((v, sum(d) / len(d), len(d)) for v, d in buckets.items())
            spread = max(p[1] for p in pts) - min(p[1] for p in pts)
            if spread < 0.5:
                continue
            print("     param 0x%02X: %s   [spread %.1f]"
                  % (k, "  ".join("%d->%.1f(n=%d)" % (v, m, n) for v, m, n in pts), spread))


if __name__ == "__main__":
    main()
