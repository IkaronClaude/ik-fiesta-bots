#!/usr/bin/env python
"""Reconstruct a fight frame-by-frame from a packet capture — and diff BOT combat against a real player's.

    python tools/combat_timeline.py --pcap Z:/LongCaptureNoDc.pcapng --fight 305
    python tools/combat_timeline.py --pcap Z:/BotFight.pcapng --auto          # biggest fight in the capture
    python tools/combat_timeline.py --decoded dump.txt --conv 7 --group 0x0014,0x0015,0x0016
    python tools/combat_timeline.py --pcap A.pcapng --auto --summary-only     # one line per fight

Emits every swing, skill hit (damage + named flags), cease-fire, target change, stone use, movement and
death, per mob handle, plus a damage dealt/taken summary per participant.

⛔ THREE THINGS THAT WILL BITE YOU IF YOU REWRITE THIS (each cost real time on 2026-08-11):

 1. `@` OFFSETS ARE PER-DIRECTION, NOT A CLOCK. `S<- @19389` and the `C-> @484` that ANSWERS it are on
    separate baselines. pcap_decode.py interleaves by timestamp BY DEFAULT, so FILE ORDER is the true
    chronology. Windowing on `@` silently drops every client frame and yields the false conclusion
    "the client sent nothing during this fight" — which is how a fight full of Slice and Dice casts came
    out as "server-driven damage with no client input". ORDERING IS THE PACKET INDEX IN THE INTERLEAVED
    STREAM; `@` is printed in parentheses as the raw per-direction value and never used to order or
    window anything.

 2. THE PDB GIVES THE STRUCT; THE WIRE GIVES THE SERIALISED FORM. Fiesta.pdb lists kSkillID and pTarget
    between `targetnum` and the SkillDamage array — they are NOT serialised. Every frame is 19 bytes
    with targetnum=1, and 5 + 14 = 19, so the array starts at @5. Trusting the PDB's field order shifts
    everything 4 bytes and reports zero skill damage for entire fights.

 3. NC_BRIEFINFO_BRIEFINFODELETE_CMD IS NOT DEATH (it also fires on leaving AoI) and its field is `hnd`,
    not `handle`. Death is `resthp == 0`, or the isDead flag.

VALIDATION: total damage dealt to each mob must equal its MobInfo MaxHP. If it does not reconcile, the
decode is wrong — that check caught both errors above.
"""
import argparse, os, re, struct, subprocess, sys, tempfile
from collections import defaultdict

PCAP_DECODE = r"C:/Projects/fiesta-proxy/tools/pcap_decode.py"
XOR_TABLE   = r"C:/Projects/ik-fiesta-bots/xor-table.hex"

# SkillDamage.flag bitfield, straight from Fiesta.pdb
FLAG_B0 = ["isdamage","iscritical","ismissed","isshieldblock","isheal","isenchant","isresist","IsCostumWeapon"]
FLAG_B1 = ["isDead","isImmune","IsCostumShield"]

SHOW = {"NC_BAT_SWING_START_CMD","NC_BAT_SWING_DAMAGE_CMD","NC_BAT_CEASE_FIRE_CMD",
        "NC_BAT_SKILLBASH_HIT_DAMAGE_CMD","NC_BAT_SKILLBASH_OBJ_CAST_REQ","NC_BAT_SKILLBASH_HIT_BLAST_CMD",
        "NC_BAT_BASHSTART_CMD","NC_BAT_TARGETTING_REQ","NC_BAT_HPCHANGE_CMD",
        "NC_SOULSTONE_HP_USE_REQ","NC_SOULSTONE_SP_USE_REQ","NC_ACT_STOP_REQ","NC_ACT_MOVERUN_CMD",
        "NC_ACT_SOMEONEMOVEWALK_CMD","NC_ACT_SOMEONEMOVERUN_CMD","NC_ACT_SOMEONESTOP_CMD",
        "NC_BRIEFINFO_BRIEFINFODELETE_CMD","NC_ITEM_PICK_REQ"}


def flagnames(fl):
    out  = [FLAG_B0[i] for i in range(8) if fl & (1 << i)]
    out += [FLAG_B1[i] for i in range(3) if (fl >> 8) & (1 << i)]
    rest = ((fl >> 8) & ~0x07) & 0xFF
    if rest: out.append(f"hi:0x{rest:02X}")
    return ",".join(out) or "-"


def decode_pcap(path):
    """Run pcap_decode.py (interleaved = default) and return the text."""
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE)   # without this, C->S decodes to garbage
    r = subprocess.run([sys.executable, PCAP_DECODE, path],
                       capture_output=True, text=True, env=env, errors="replace")
    if not r.stdout.strip():
        sys.exit(f"pcap_decode produced nothing for {path}\n{r.stderr[:400]}")
    return r.stdout


def parse(text):
    """Frames in FILE ORDER (= chronological; see note 1)."""
    frames, conv, cur, hexb = [], -1, None, []
    for line in text.split("\n"):
        if line.startswith("==== server"):
            conv += 1
        m = re.match(r'\s+([CS])(?:->|<-) +@ *(\d+) +\[(0x[0-9A-Fa-f]{4})\]\s*(\S*)', line)
        if m:
            if cur: cur["hex"] = "".join(hexb); frames.append(cur)
            cur = {"seq": len(frames), "conv": conv, "dir": m.group(1), "at": int(m.group(2)),
                   "op": m.group(3), "name": m.group(4), "f": {}}
            hexb = []
            continue
        if cur is None: continue
        if line.lstrip().startswith("@"):
            t = line.split()
            for i, x in enumerate(t):
                if i >= 2 and re.fullmatch(r'-?\d+', x) and re.fullmatch(r'[A-Za-z_]\w*', t[i-1]):
                    cur["f"].setdefault(t[i-1], int(x))
        hm = re.match(r'\s+[0-9a-f]{4}\s+((?:[0-9a-f]{2} )+)', line)
        if hm: hexb.append(hm.group(1).replace(" ", ""))
    if cur: cur["hex"] = "".join(hexb); frames.append(cur)
    # ⛔ NO SYNTHETIC CLOCK. The ordering IS the packet index in the interleaved stream (`seq`), which is
    # the only sound cross-direction ordering available: pcap_decode interleaves by timestamp, but the `@`
    # values it prints are PER-DIRECTION offsets. Carrying the last server `@` forward "to have seconds"
    # invents a time for every client frame and puts replies before their requests. `@` is shown in
    # parentheses as the raw per-direction value it is, and never used to order or window anything.
    return frames


def skillhits(f):
    """SkillDamage[] — array starts at @5 on the WIRE (note 2)."""
    try: b = bytes.fromhex(f.get("hex", ""))
    except ValueError: return []
    if len(b) < 5: return []
    out = []
    for i in range(b[4]):
        o = 5 + i * 14
        if o + 14 > len(b): break
        out.append((b[o] | (b[o+1] << 8), b[o+2] | (b[o+3] << 8),
                    struct.unpack('<I', b[o+4:o+8])[0], struct.unpack('<I', b[o+8:o+12])[0]))
    return out


def build(frames):
    mob = defaultdict(dict)
    for f in frames:
        if f["name"] == "NC_BRIEFINFO_REGENMOB_CMD" and {"handle","mobid"} <= f["f"].keys():
            mob[f["conv"]][f["f"]["handle"]] = f["f"]["mobid"]
    me = {}
    for f in frames:
        if f["name"] == "NC_BAT_SWING_DAMAGE_CMD" and f["f"].get("defender") in mob[f["conv"]]:
            me.setdefault(f["conv"], f["f"]["attacker"])
    return mob, me


def find_fights(frames, mob, me, want_mobid=None):
    """Contiguous engagements: a mob joins while the anchor lives; death = resthp 0."""
    fights = []
    for c, handles in mob.items():
        if c not in me: continue
        dmg = defaultdict(list)
        for f in frames:
            if f["conv"] != c: continue
            if f["name"] == "NC_BAT_SWING_DAMAGE_CMD" and f["f"].get("attacker") == me[c]:
                d = f["f"].get("defender")
                if d in handles: dmg[d].append((f["seq"], f["f"].get("resthp")))
            elif f["name"] == "NC_BAT_SKILLBASH_HIT_DAMAGE_CMD":
                for h, _fl, _d, rest in skillhits(f):
                    if h in handles: dmg[h].append((f["seq"], rest))
        for h, hits in dmg.items():
            if want_mobid is not None and handles[h] != want_mobid: continue
            if len(hits) < 3: continue
            fights.append({"conv": c, "anchor": h, "mobid": handles[h],
                           "start": hits[0][0], "hits": len(hits)})
    fights.sort(key=lambda x: -x["hits"])
    return fights


def render(frames, mob, me, conv, group, out, mobnames=None):
    c, g = conv, set(group)
    my = me.get(c)
    ev = [f for f in frames if f["conv"] == c]
    mn = lambda h: (f"{(mobnames or {}).get(mob[c].get(h), 'mob%s' % mob[c].get(h))}/{h:04X}"
                    if h in mob[c] else f"0x{h:04X}")
    first = None; deaths = {}
    for f in ev:
        hit_g = (f["f"].get("attacker") in g or f["f"].get("defender") in g
                 or any(h in g for h, _, _, _ in (skillhits(f) if f["name"].endswith("HIT_DAMAGE_CMD") else [])))
        if first is None and hit_g and f["name"] in ("NC_BAT_SWING_DAMAGE_CMD","NC_BAT_SKILLBASH_HIT_DAMAGE_CMD"):
            first = f["seq"]
        if first is None: continue
        if f["name"] == "NC_BAT_SWING_DAMAGE_CMD" and f["f"].get("defender") in g and f["f"].get("resthp") == 0:
            deaths.setdefault(f["f"]["defender"], f["seq"])
        elif f["name"] == "NC_BAT_SKILLBASH_HIT_DAMAGE_CMD":
            for h, _fl, _d, rest in skillhits(f):
                if h in g and rest == 0: deaths.setdefault(h, f["seq"])
    if first is None: return None
    last = max((f["seq"] for f in ev if f["seq"] >= first and
                (f["f"].get("attacker") in g or f["f"].get("defender") in g)), default=first)
    end = max(deaths.values()) if len(deaths) == len(g) else last


    dealt, taken = defaultdict(int), defaultdict(int)
    W = out.write
    W("=" * 112 + "\n")
    W(f"### conv#{c}  us=0x{my:04X}  group: " + ", ".join(mn(h) for h in sorted(g)) + "\n")
    W(f"    {end - first + 1} packets in the interleaved stream" +
      ("   deaths: " + ", ".join(f"{mn(h)}" for h, _ in sorted(deaths.items(), key=lambda kv: kv[1])) if deaths else "") + "\n")
    W("=" * 112 + "\n")
    W(f"{'t(s)':>7}  dir {'event':22} {'who':34} detail\n" + "-" * 112 + "\n")
    target_now = None
    for f in ev:
        if not (first <= f["seq"] <= end) or f["name"] not in SHOW: continue
        idx = f["seq"] - first
        fl = f["f"]
        tag = (f["name"].replace("NC_BAT_","").replace("NC_ACT_","").replace("NC_BRIEFINFO_","")
               .replace("NC_SOULSTONE_","STONE_").replace("NC_ITEM_","").replace("_CMD","").replace("_REQ",""))
        if f["name"] == "NC_BAT_TARGETTING_REQ":
            try:
                hb = bytes.fromhex(f.get("hex",""))
                if len(hb) >= 2: target_now = hb[0] | (hb[1] << 8)
            except ValueError: pass
            W(f"{idx:6}  C-> {'TARGET':22} {(mn(target_now) if target_now is not None else '?'):34} held until next change\n")
            continue
        if f["name"] == "NC_BAT_SKILLBASH_OBJ_CAST_REQ":
            W(f"{idx:6}  C-> {'SKILL_CAST':22} {('US -> ' + (mn(target_now) if target_now is not None else 'no target')):34} via tracked target\n")
            continue
        if f["name"] == "NC_BAT_SKILLBASH_HIT_DAMAGE_CMD":
            for h, flg, dm, rest in skillhits(f):
                if h not in g: continue
                dealt[h] += dm
                W(f"{idx:6}  S<- {'SKILL_HIT':22} {('US -> ' + mn(h)):34} dmg={dm} tgtHP={rest} [{flagnames(flg)}]\n")
            continue
        rel = {fl.get(k) for k in ("attacker","defender","hnd","handle","target")}
        if not (f["dir"] == "C" or my in rel or rel & g): continue
        who = ""
        if fl.get("attacker") is not None:
            a, d = fl["attacker"], fl.get("defender")
            who = f"{'US' if a==my else mn(a)} -> {'US' if d==my else mn(d)}"
            if f["name"] == "NC_BAT_SWING_DAMAGE_CMD":
                if a == my and d in g: dealt[d] += fl.get("damage", 0)
                elif d == my and a in g: taken[a] += fl.get("damage", 0)
        elif fl.get("hnd") is not None: who = mn(fl["hnd"])
        elif fl.get("handle") is not None: who = "US" if fl["handle"] == my else mn(fl["handle"])
        det = ""
        if "damage" in fl: det += f"dmg={fl['damage']} "
        if "resthp" in fl: det += (f"ourHP={fl['resthp']} " if fl.get("defender") == my else f"tgtHP={fl['resthp']} ")
        W("%6d  %s %-22s %-34s %s(Stream %d: @%d)\n" % (idx, "C->" if f["dir"]=="C" else "S<-", tag, who, det, f["conv"], f["at"]))
    W(f"\n    dealt: " + ", ".join(f"{mn(h)}={dealt.get(h,0)}" for h in sorted(g)) + "\n")
    W(f"    taken: " + ", ".join(f"{mn(h)}={taken.get(h,0)}" for h in sorted(g)) +
      f"   TOTAL {sum(taken.values())}\n\n")
    return {"packets": end - first + 1, "dealt": dict(dealt), "taken": sum(taken.values()), "deaths": len(deaths)}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--pcap", help="a .pcapng (real player OR a capture armed on the bot)")
    src.add_argument("--decoded", help="an already-decoded pcap_decode.py dump")
    ap.add_argument("--conv", type=int, help="conversation index")
    ap.add_argument("--group", help="comma-separated mob handles, e.g. 0x0014,0x0015")
    ap.add_argument("--fight", type=int, help="pick the biggest fight against this mobid")
    ap.add_argument("--auto", action="store_true", help="pick the biggest fight in the capture")
    ap.add_argument("--top", type=int, default=1, help="how many fights to render with --auto/--fight")
    ap.add_argument("--summary-only", action="store_true")
    ap.add_argument("-o", "--out", help="write here instead of stdout")
    a = ap.parse_args()

    text = open(a.decoded, encoding="utf-8", errors="replace").read() if a.decoded else decode_pcap(a.pcap)
    frames = parse(text)
    mob, me = build(frames)
    out = open(a.out, "w", encoding="utf-8") if a.out else sys.stdout

    if a.conv is not None and a.group:
        render(frames, mob, me, a.conv, [int(x, 0) for x in a.group.split(",")], out)
    else:
        fights = find_fights(frames, mob, me, a.fight)
        if not fights: sys.exit("no fights found (is XOR_TABLE_PATH right? C->S must decode)")
        for fx in fights[:a.top]:
            c, anchor = fx["conv"], fx["anchor"]
            # group = whatever also traded damage with us while the anchor was alive
            grp = {anchor}
            for f in frames:
                if f["conv"] != c or f["seq"] < fx["start"]: continue
                if f["name"] == "NC_BAT_SWING_DAMAGE_CMD":
                    at, df = f["f"].get("attacker"), f["f"].get("defender")
                    if at == me[c] and df in mob[c]: grp.add(df)
                    elif df == me[c] and at in mob[c]: grp.add(at)
                    if df == anchor and f["f"].get("resthp") == 0: break
            r = render(frames, mob, me, c, grp, out)
            if a.summary_only and r:
                print(f"conv{c} mob{fx['mobid']} {r['packets']}pkts dealt={sum(r['dealt'].values())} taken={r['taken']}")
    if a.out: out.close(); print(f"written to {a.out}")


if __name__ == "__main__":
    main()
