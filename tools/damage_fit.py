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
    """Yield (conv, dir, opcode, name, payload).

    ⛔ THE FILE IS NOT ONE STREAM. pcap_decode emits each TCP conversation separately, under its own
    `==== server IP:PORT <-> client IP:PORT ====` header, and a relog mid-capture opens NEW conversations
    (each with its own 0x0807 handshake). Z:/Damage.pcapng has TWELVE.

    This matters more than it looks, because YOUR OWN HANDLE CHANGES ON RELOG. Reading the file as one
    linear stream and deriving a single self handle silently drops every hit in every later conversation:
    on this capture that discarded the entire armour-swap phase and produced the confident, wrong
    conclusion "the armour phase has zero incoming hits, so the capture cannot answer the question". The
    capture was fine. The parser was not. Self is therefore resolved PER CONVERSATION.
    """
    cur, hexb, conv = None, [], -1
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if line.startswith("==== server"):
                if cur:
                    yield (conv,) + cur + (bytes(hexb),)
                cur, hexb = None, []
                conv += 1
                continue
            m = FRAME.match(line)
            if m:
                if cur:
                    yield (conv,) + cur + (bytes(hexb),)
                cur, hexb = (m.group(1), int(m.group(3), 16), m.group(4)), []
                continue
            h = HEXLINE.match(line)
            if h and cur:
                hexb.extend(int(x, 16) for x in h.group(1).split())
    if cur:
        yield (conv,) + cur + (bytes(hexb),)


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

    # Self-identification, PER CONVERSATION: a SWING_DAMAGE whose resthp equals the hp of the next
    # HPCHANGE landed on us. Done per conversation because a relog gives us a new handle (see frames()).
    votes = defaultdict(Counter)
    for i, (conv, d, op, name, b) in enumerate(fs):
        if name == "NC_BAT_SWING_DAMAGE_CMD" and len(b) >= 12:
            resthp = struct.unpack_from("<I", b, 8)[0]
            for j in range(i + 1, min(i + 6, len(fs))):
                if fs[j][3] == "NC_BAT_HPCHANGE_CMD" and len(fs[j][4]) >= 4:
                    if struct.unpack_from("<I", fs[j][4], 0)[0] == resthp:
                        votes[conv][struct.unpack_from("<H", b, 2)[0]] += 1
                    break
    self_of = {c: v.most_common(1)[0][0] for c, v in votes.items() if v}
    nconv = 1 + max((f[0] for f in fs), default=-1)
    print("conversations=%d, self handle resolved in %d of them: %s"
          % (nconv, len(self_of),
             ", ".join("conv%d=0x%04X(%d)" % (c, h, votes[c][h]) for c, h in sorted(self_of.items()))))

    params, hits, events = {}, [], []
    changed = defaultdict(list)
    for idx, (conv, d, op, name, b) in enumerate(fs):
        self_h = self_of.get(conv)
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

    # THE FIT. Run it on the whole set and again with END pinned, so the difference between an
    # uncontrolled fit and a controlled one is visible side by side rather than taken on trust.
    print("\n=== FIT: damage vs defense (param 0x08) ===")
    print("  [uncontrolled — every cell, including ones where other stats moved too]")
    fit_defense(hits)
    ends = sorted({h["params"].get(0x19) for h in hits if h["params"].get(0x19) is not None})
    for e in ends:
        n = sum(1 for h in hits if h["params"].get(0x19) == e and h["dmg"] > 0)
        if n >= 20:
            print("  [controlled — END (0x19) pinned at %d, so defense is the only moving input]" % e)
            fit_defense(hits, pin={0x19: e})

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


def fit_defense(hits, pin=None):
    """Fit damage against defense (param 0x08), optionally pinning another param to hold it still.

    RESULT ON Z:/Damage.pcapng (pin={0x19: 50}, i.e. END held at 50 so armour is the only moving input):

        damage = K_mob / (DEF - 141)        worst residual 2.65% over 8 cells, 3 mobs,
                                            DEF 535..1230, damage 69..188 (a 2.7x range)

    The form is confirmed by something the fit does not get to choose: the recovered per-mob constants
    come out 74228 / 73430 / 74984 for three INDEPENDENT mob handles -- within 2% of each other -- so K
    behaves exactly like a per-mob-type attack value. Linear mitigation (a - b*DEF) cannot do better than
    12.8% and is ruled out. A power law K/DEF^1.245 fits comparably (2.18%); three defense values cannot
    separate the two, and both say the same practical thing: mitigation is roughly INVERSE in defense,
    not flat subtraction, so each point of defense is worth less than the last.

    ⛔ PIN SOMETHING, OR THE FIT IS MEANINGLESS. Fitting the whole capture at once gives C=289 and 18.7%
    residuals, because the END 20->50 cells move 0x08 by only 15 points while damage moves 14-28% -- no
    function of DEF can do that. Those cells are confounded (0x13/0x19/0x1A all moved with END) and tiny
    (n=5,6). Restricting to cells where ONE input moved is the whole difference between a 2.65% fit and
    a meaningless one.
    """
    cells = defaultdict(list)
    for h in hits:
        p = h["params"]
        if h["dmg"] <= 0 or 0x08 not in p:
            continue
        if pin and any(p.get(k) != v for k, v in pin.items()):
            continue
        cells[(h["atk"], p[0x08])].append(h["dmg"])
    bymob = defaultdict(dict)
    for (m, dv), d in cells.items():
        if len(d) >= 5:
            bymob[m][dv] = (sum(d) / len(d), len(d))
    multi = {m: d for m, d in bymob.items() if len(d) >= 2}
    if not multi:
        print("  not enough controlled cells to fit (need a mob seen at 2+ defense values, n>=5 each)")
        return

    def score(c):
        worst, ks = 0.0, {}
        for m, d in multi.items():
            k = sum(mn * (dv - c) for dv, (mn, _) in d.items()) / len(d)
            ks[m] = k
            for dv, (mn, _) in d.items():
                worst = max(worst, abs(k / (dv - c) - mn) / mn)
        return worst, ks

    err, c = min(((score(x / 4)[0], x / 4) for x in range(-800, 2000)), key=lambda t: t[0])
    _, ks = score(c)
    print("  dmg = K_mob / (DEF - %.1f)   worst residual %.2f%%" % (c, err * 100))
    for m, d in sorted(multi.items()):
        for dv, (mn, n) in sorted(d.items()):
            pr = ks[m] / (dv - c)
            print("    mob 0x%04X DEF=%4d n=%2d  actual=%7.2f pred=%7.2f  %+5.1f%%"
                  % (m, dv, n, mn, pr, (pr - mn) / mn * 100))
    print("  per-mob K: " + "  ".join("0x%04X=%.0f" % (m, k) for m, k in sorted(ks.items()))
          + "   (should agree if they are the same mob type)")


if __name__ == "__main__":
    main()
