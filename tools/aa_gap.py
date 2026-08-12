#!/usr/bin/env python
"""Is the auto-attack actually swinging during a fight? Two metrics, both the operator's design.

    python tools/aa_gap.py packets-BOT.log

METRIC 1 — SWING RATE OVER A FIGHT.
Rate of landed auto-attack swings between the start of a fight and its end (the enemy dies, we die,
or contact breaks). Expected roughly 0.5/s: about a 1.3s weapon windup, minus some cast interrupts,
which are fine and expected. Sends are counted too — BASHSTART/s and swings/s are different questions
and reporting one as the other is how "0.25 swings per bash" gets misread as a cadence problem.

METRIC 2 — THE KILLER STAT (operator 2026-08-12).
Inside a fight, find a gap between skill casts LONGER than one auto-attack interval. If no swing lands
in that gap, AND neither we nor the target moved during it, AND we had already connected with a melee
skill in this fight (so range and facing are proven), then nothing external can explain the silence:
  · not cooldowns    — the gap is between casts, by construction
  · not range/facing — a melee skill already connected against this same target
  · not movement     — neither side moved
  · not interrupts   — no cast was issued inside the gap
That leaves our own combat logic. Every such gap is a defect, and the tool prints them with the
frames around them so the failure is readable rather than inferred.

⛔ Reads OUR handle from the log's "==== self handle N ====" stamps, never infers it. Handles are per
map, so the last stamp at or before a frame is the one in force. Both earlier swing statistics in this
repo guessed the handle, got it wrong, and had to be retracted.
"""
import argparse, statistics, struct, sys
from collections import defaultdict

sys.path.insert(0, __file__.rsplit("/", 1)[0] if "/" in __file__ else ".")
from swings_per_bash import parse, secs, u16   # same parser; one source of truth for the log format

# C->S
BASHSTART, CAST, MOVERUN, MOVEWALK, STOP = 0x242B, 0x2440, 0x2019, 0x2018, 0x2012
# S->C
SWING_START, SWING_DAMAGE, CEASE_FIRE, HIT_DAMAGE = 0x2447, 0x2448, 0x243D, 0x2452


def load(path):
    fr = parse(path)
    day, prev, last = 0.0, None, 0.0
    for i, f in enumerate(fr):
        f["i"] = i
        if f["ts"] is None: f["t"] = last; continue
        t = secs(f["ts"])
        if prev is not None and t < prev - 1: day += 86400.0
        prev = t
        f["t"] = t + day; last = f["t"]
    return fr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--idle", type=float, default=6.0,
                    help="seconds of no damage in either direction that ends a fight (default 6)")
    ap.add_argument("--show", type=int, default=3, help="how many offending gaps to print in full")
    a = ap.parse_args()
    fr = load(a.log)

    stamps = [f for f in fr if f["dir"] == "="]
    if not stamps:
        print("no '==== self handle N ====' stamp — REFUSING to guess the handle.")
        return

    def me_at(i):
        cur = None
        for st in stamps:
            if st["i"] <= i: cur = st["self"]
            else: break
        return cur

    # ⛔ THE TWO DAMAGE PACKETS DO NOT SHARE A LAYOUT. Read from the PDB extract
    # (lib/FiestaLib-Reloaded/docs/extracted/merged/all-structs.json), not from a guess:
    #   PROTO_NC_BAT_SWING_DAMAGE_CMD           attacker u16 @0, defender u16 @2, ...
    #   PROTO_NC_BAT_SKILLBASH_HIT_DAMAGE_CMD   index u16 @0, CASTER u16 @2, targetnum u8 @4,
    #                                           then a SkillDamage[] whose first entry starts @5
    #                                           with the target handle. (The rest of that entry is
    #                                           not in the extract and is left undecoded rather
    #                                           than invented.)
    # The first version of this tool read the skill packet as if it were the swing packet, so
    # "attacker" came out of `index` — a monotonically increasing sequence number that matches no
    # handle. Result: ZERO skill hits were ever attributed to us, every fight failed the "we already
    # connected with a melee skill" precondition, and metric 2 reported a confident 0 offending gaps.
    def attacker_of(f):
        return u16(f["raw"], 0) if f["op"] == SWING_DAMAGE else u16(f["raw"], 2)

    def defender_of(f):
        return u16(f["raw"], 2) if f["op"] == SWING_DAMAGE else (u16(f["raw"], 5) if len(f["raw"]) >= 7 else None)

    dmg = [f for f in fr if f["dir"] == "S" and f["op"] in (SWING_DAMAGE, HIT_DAMAGE) and len(f["raw"]) >= 7]
    ours = [f for f in dmg if attacker_of(f) == me_at(f["i"])]
    on_us = [f for f in dmg if defender_of(f) == me_at(f["i"])]
    if not dmg:
        print("no damage frames at all — nothing was fighting in this capture.")
        return

    # FIGHTS = runs of damage activity (either direction) separated by --idle seconds of silence.
    events = sorted(ours + on_us, key=lambda f: f["t"])
    fights, cur = [], [events[0]]
    for f in events[1:]:
        if f["t"] - cur[-1]["t"] > a.idle: fights.append(cur); cur = [f]
        else: cur.append(f)
    fights.append(cur)
    fights = [g for g in fights if g[-1]["t"] - g[0]["t"] >= 2.0]     # a 2s floor: not a fight

    # AA INTERVAL, measured rather than assumed: the median gap between consecutive landed swings of
    # ours inside a fight. If too few swings land to measure one, fall back to the operator's ~1.3s
    # and SAY SO, because the whole point is not to invent numbers.
    swing_ts = [f["t"] for f in ours if f["op"] == SWING_DAMAGE]
    gaps = [b - a2 for a2, b in zip(swing_ts, swing_ts[1:]) if 0 < b - a2 < 6]
    if len(gaps) >= 8:
        aa_interval = statistics.median(gaps); src = f"measured, median of {len(gaps)} inter-swing gaps"
    else:
        aa_interval = 1.3; src = f"ASSUMED 1.3s — only {len(gaps)} inter-swing gaps to measure from"

    print(f"fights (>=2s, split by {a.idle}s of no damage): {len(fights)}")
    print(f"auto-attack interval: {aa_interval:.2f}s   ({src})\n")

    # ── METRIC 1 ────────────────────────────────────────────────────────────────────────────────
    tot_dur = tot_sw = tot_bash = 0.0
    rows = []
    for g in fights:
        t0, t1 = g[0]["t"], g[-1]["t"]
        sw = sum(1 for f in ours if f["op"] == SWING_DAMAGE and t0 <= f["t"] <= t1)
        bs = sum(1 for f in fr if f["dir"] == "C" and f["op"] == BASHSTART and t0 <= f["t"] <= t1)
        rows.append((t1 - t0, sw, bs))
        tot_dur += t1 - t0; tot_sw += sw; tot_bash += bs
    print("METRIC 1 — over time actually spent in a fight")
    print(f"  fight time          {tot_dur:.0f}s")
    print(f"  landed AA swings    {tot_sw:.0f}   ->  {tot_sw/max(1,tot_dur):.2f} swings/sec   (expected ~0.5)")
    print(f"  BASHSTART sent      {tot_bash:.0f}   ->  {tot_bash/max(1,tot_dur):.2f} sends/sec")
    med = statistics.median([s / max(0.001, d) for d, s, _ in rows]) if rows else 0
    print(f"  per-fight median    {med:.2f} swings/sec\n")

    # ── METRIC 2 — THE KILLER STAT ──────────────────────────────────────────────────────────────
    casts = [f for f in fr if f["dir"] == "C" and f["op"] == CAST]
    my_moves = [f for f in fr if f["dir"] == "C" and f["op"] in (MOVERUN, MOVEWALK)]
    # ⛔ MOVEMENT MUST BE THE TARGET'S, NOT ANYBODY'S. The first version rejected a gap if ANY nearby
    # entity broadcast a move, and in a room full of mobs something is always moving — so it discarded
    # every candidate and printed a confident "0 offending gaps" that meant "my filter ate everything",
    # not "nothing is wrong". That is the read-nothing-as-an-answer failure this repo keeps hitting, so
    # the rejection reasons are counted and printed below: a zero has to be explainable.
    # Movement broadcasts carry {handle u16 @0}; our own TARGET requests (0x2401) carry the handle we
    # selected, so the target in force at any moment is the last one we asked for.
    targets = [(f["t"], u16(f["raw"], 0)) for f in fr
               if f["dir"] == "C" and f["op"] == 0x2401 and len(f["raw"]) >= 2]

    def target_at(t):
        cur = None
        for tt, h in targets:
            if tt <= t: cur = h
            else: break
        return cur

    moves_by_handle = defaultdict(list)
    for f in fr:
        if f["dir"] == "S" and f["op"] in (0x201A, 0x2017) and len(f["raw"]) >= 2:
            moves_by_handle[u16(f["raw"], 0)].append(f["t"])

    def any_in(lst, t0, t1): return any(t0 < f["t"] < t1 for f in lst)

    offenders = []
    rej = defaultdict(int)
    for g in fights:
        t0, t1 = g[0]["t"], g[-1]["t"]
        # Proof that range and facing were fine in THIS fight: a skill of ours connected.
        first_skill_hit = next((f["t"] for f in ours if f["op"] == HIT_DAMAGE and t0 <= f["t"] <= t1), None)
        if first_skill_hit is None: rej["fight had no connecting melee skill (reach unproven)"] += 1; continue
        fc = [f["t"] for f in casts if t0 <= f["t"] <= t1]
        bounds = [t0] + fc + [t1]
        for x, y in zip(bounds, bounds[1:]):
            if y - x <= aa_interval: rej["gap shorter than one AA interval"] += 1; continue
            if x < first_skill_hit: rej["before the fight's first connecting skill"] += 1; continue
            if any(x < f["t"] < y for f in ours if f["op"] == SWING_DAMAGE):
                rej["a swing DID land (working as intended)"] += 1; continue
            if any_in(my_moves, x, y): rej["we moved"] += 1; continue
            tgt = target_at(x)
            if tgt is not None and any(x < mt < y for mt in moves_by_handle.get(tgt, ())):
                rej["the TARGET moved"] += 1; continue
            offenders.append((x, y, y - x, tgt))

    total_gapless = sum(d for _, _, d, _ in offenders)
    print("METRIC 2 — silent windows that nothing external explains")
    print("  (longer than one AA interval, between casts, no swing, nobody moved,")
    print("   and a melee skill had already connected in that same fight)")
    print(f"  offending gaps      {len(offenders)}   totalling {total_gapless:.0f}s"
          f"  ({100*total_gapless/max(1,tot_dur):.0f}% of all fight time)")
    print("  candidate windows rejected, and why (a zero above must be EXPLAINABLE, not assumed clean):")
    for k, v in sorted(rej.items(), key=lambda kv: -kv[1]):
        print(f"      {v:5}  {k}")
    if offenders:
        longest = sorted(offenders, key=lambda o: -o[2])
        print("\n  longest             " + ", ".join(f"{d:.1f}s" for _, _, d, _ in longest[:6]))
        print("\n  => Nothing external explains these. Not cooldowns (they sit between casts), not")
        print("     range or facing (a melee skill connected in this same fight), not movement")
        print("     (neither side moved), not interrupts (no cast was issued inside them).")
        print("     THIS IS OUR COMBAT LOGIC.\n")
        NAMES = {BASHSTART: "BASHSTART(C)", CAST: "CAST(C)", STOP: "STOP(C)", MOVERUN: "MOVERUN(C)",
                 MOVEWALK: "MOVEWALK(C)", 0x2401: "TARGET(C)", SWING_START: "SWING_START(S)",
                 SWING_DAMAGE: "SWING_DMG(S)", CEASE_FIRE: "CEASE_FIRE(S)", HIT_DAMAGE: "HIT_DMG(S)"}
        for x, y, d, tgt in longest[:a.show]:
            print(f"  ===== SILENT {d:.1f}s  (target h{tgt}) =====")
            for f in fr:
                if f["t"] < x - 0.5: continue
                if f["t"] > y + 0.5: break
                if f["op"] not in NAMES: continue
                who = "" if f["dir"] == "C" else f"  h{u16(f['raw'],0)}" if len(f["raw"]) >= 2 else ""
                print(f"    {f['ts']}  {NAMES[f['op']]:16}{who}")
    else:
        print("  none — every silent window is explained by a cast, a move, or a swing landing.")


if __name__ == "__main__":
    main()
