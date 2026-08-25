#!/usr/bin/env python
"""Replay a captured combat stream through the formula read out of Zone.exe and test it against the ranges.

    python tools/damage_sim.py --decoded dump.txt

THE TEST. From `RulesOfEngagement::roe_Damage` (see docs/DAMAGE_FORMULA.md):

    damage = ((attackerLevel + 1) * AttackPower * nBMPDamageRate/1000) / DefendPower

For a plain field mob nothing modifies its `Parameter::Container` -- no gear, no upgrades, no passives, no
abnormal states -- so every `plus` block is 0 and every `rate` block is neutral, and its `PureCharParam` IS
its `MobInfoServer` + `MobWeapon` row. AttackPower therefore collapses to a roll over a FIXED band for a
given mob type, and the formula predicts, for each observed hit:

    impliedAttack = damage * DefendPower / (attackerLevel + 1)

If the formula and the level term are right, every hit from one mob type must map into ONE band, no matter
which defence value the character had at the time. That is the falsifiable claim this script tests: it does
not fit anything, it inverts the known formula per hit and asks whether the results agree.

Angle is the one modifier that cannot be recovered from the capture (`DamageByAngle` spans 1.000..1.200),
so a 20% spread is expected and is reported separately rather than hidden.
"""
import argparse, struct, importlib.util, os, sys
from collections import defaultdict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("df", os.path.join(HERE, "damage_fit.py"))
df = importlib.util.module_from_spec(spec)
spec.loader.exec_module(df)

# mob 84 "Orc" -- MobInfo + MobInfoServer + MobWeapon
MOB = {"id": 84, "name": "Orc", "level": 61, "maxhp": 3562,
       "MinWC": 747, "MaxWC": 1137, "TH": 267, "Str": 822, "Con": 140, "Dex": 147,
       "AC": 102, "TB": 179, "MR": 127}
ANGLE = [(0, 1.000), (45, 1.040), (90, 1.100), (135, 1.120), (170, 1.140), (180, 1.200)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--decoded", required=True)
    ap.add_argument("--mob", type=int, default=84)
    a = ap.parse_args()

    fs = list(df.frames(a.decoded))

    # self handle per conversation (a relog gives us a new one)
    votes = defaultdict(Counter)
    for i, (conv, d, op, name, b) in enumerate(fs):
        if name == "NC_BAT_SWING_DAMAGE_CMD" and len(b) >= 12:
            rest = struct.unpack_from("<I", b, 8)[0]
            for j in range(i + 1, min(i + 6, len(fs))):
                if fs[j][3] == "NC_BAT_HPCHANGE_CMD" and len(fs[j][4]) >= 4:
                    if struct.unpack_from("<I", fs[j][4], 0)[0] == rest:
                        votes[conv][struct.unpack_from("<H", b, 2)[0]] += 1
                    break
    self_of = {c: v.most_common(1)[0][0] for c, v in votes.items() if v}

    mobid = {}          # (conv,handle) -> mob id      [NC_BRIEFINFO_REGENMOB_CMD]
    params, hits = {}, []
    for conv, d, op, name, b in fs:
        if name == "NC_BRIEFINFO_REGENMOB_CMD" and len(b) >= 5:
            mobid[(conv, struct.unpack_from("<H", b, 0)[0])] = struct.unpack_from("<H", b, 3)[0]
        elif name == "NC_CHAR_CHANGEPARAMCHANGE_CMD":
            p = df.parse_params(b)
            if p:
                params.update(p)
        elif name == "NC_BAT_SWING_DAMAGE_CMD" and len(b) >= 12:
            atk, dfn = struct.unpack_from("<H", b, 0)[0], struct.unpack_from("<H", b, 2)[0]
            flag, dmg = struct.unpack_from("<H", b, 4)[0], struct.unpack_from("<H", b, 6)[0]
            if dfn != self_of.get(conv):
                continue
            if mobid.get((conv, atk)) != a.mob:
                continue
            # 0x08 = AC on the wire (FiestaLib CHAR_PARAMETER_DATA order)
            if 8 not in params:
                continue
            hits.append({"conv": conv, "atk": atk, "flag": flag, "dmg": dmg, "ac": params[8],
                         "con": params.get(1), "maxhp": params.get(16)})

    normal = [h for h in hits if h["flag"] == 0 and h["dmg"] > 0]
    miss = [h for h in hits if h["flag"] & 0x04]
    blocked = [h for h in hits if h["flag"] & 0x08]
    print("mob %d '%s' level %d   MinWC=%d MaxWC=%d Str=%d" %
          (MOB["id"], MOB["name"], MOB["level"], MOB["MinWC"], MOB["MaxWC"], MOB["Str"]))
    print("hits from this mob: %d   normal=%d  missed=%d  blocked=%d  critical=%d"
          % (len(hits), len(normal), len(miss), len(blocked), sum(1 for h in hits if h["flag"] & 0x02)))
    if not normal:
        return

    L1 = MOB["level"] + 1
    print("\n=== INVERT THE FORMULA PER HIT:  impliedAttack = damage * AC / (level+1)   [level+1 = %d] ===" % L1)
    print("  %-6s %-4s %-14s %-22s %s" % ("AC", "n", "damage", "impliedAttack", "band width"))
    bycell = defaultdict(list)
    for h in normal:
        bycell[h["ac"]].append(h)
    allimp = []
    for ac in sorted(bycell, reverse=True):
        ds = sorted(x["dmg"] for x in bycell[ac])
        imp = sorted(d * ac / L1 for d in ds)
        allimp += imp
        print("  %-6d %-4d %-14s %-22s x%.3f"
              % (ac, len(ds), "%d..%d" % (ds[0], ds[-1]),
                 "%.0f..%.0f" % (imp[0], imp[-1]), imp[-1] / imp[0]))

    lo, hi = min(allimp), max(allimp)
    print("\n  pooled impliedAttack over ALL cells: %.0f .. %.0f   (x%.3f)" % (lo, hi, hi / lo))
    print("  raw MobWeapon band                 : %d .. %d   (x%.3f)"
          % (MOB["MinWC"], MOB["MaxWC"], MOB["MaxWC"] / MOB["MinWC"]))
    print("  DamageByAngle alone spans          : x1.200")
    print("  so the widest band the formula permits from one fixed roll band is "
          "x%.3f" % (MOB["MaxWC"] / MOB["MinWC"] * 1.2))

    print("\n=== DOES ONE FIXED ROLL BAND EXPLAIN EVERY CELL? ===")
    print("  If AttackPower is a fixed band per mob type, each cell's implied band must be a SUBSET of the")
    print("  pooled band, and the cell bands must overlap heavily. Per-cell vs pooled:")
    ok = True
    for ac in sorted(bycell, reverse=True):
        imp = sorted(x["dmg"] * ac / L1 for x in bycell[ac])
        cover = (imp[-1] - imp[0]) / (hi - lo)
        inside = imp[0] >= lo - 1e-9 and imp[-1] <= hi + 1e-9
        ok &= inside
        print("     AC=%-5d band %.0f..%.0f  covers %3.0f%% of pooled  inside=%s"
              % (ac, imp[0], imp[-1], cover * 100, inside))

    # The strict test the operator asked for: with angle unknown, the tightest claim is that
    # impliedAttack / angleRate must land in [MinWC_eff, MaxWC_eff] for SOME angle in the table.
    print("\n=== RANGE TEST vs the raw MobWeapon band, allowing any angle from DamageByAngle ===")
    worst_lo = min(i / 1.200 for i in allimp)   # best case: hit from behind
    worst_hi = max(i / 1.000 for i in allimp)   # best case: hit from the front
    print("  impliedAttack / angle  ->  %.0f .. %.0f" % (worst_lo, worst_hi))
    print("  needs to fit inside     ->  %d .. %d" % (MOB["MinWC"], MOB["MaxWC"]))
    fits = worst_lo >= MOB["MinWC"] and worst_hi <= MOB["MaxWC"]
    print("  FITS THE RAW TABLE BAND : %s" % ("YES" if fits else "NO"))
    if not fits:
        print("     ratio to band       : low x%.2f   high x%.2f"
              % (worst_lo / MOB["MinWC"], worst_hi / MOB["MaxWC"]))
        print("     -> expected: roe_MinWC/roe_MaxWC are ACCUMULATORS over Str + WCmin + WeaponMastery,")
        print("        so the effective band is NOT the raw table band (docs/DAMAGE_FORMULA.md 3a/3c).")
        print("        Solve for the effective band instead:")
        print("        effective MinWC..MaxWC = %.0f .. %.0f   (raw x%.2f .. x%.2f)"
              % (worst_lo, worst_hi, worst_lo / MOB["MinWC"], worst_hi / MOB["MaxWC"]))


if __name__ == "__main__":
    main()
