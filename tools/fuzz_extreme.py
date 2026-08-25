#!/usr/bin/env python
"""Extreme differential fuzz: N independent SETS, each with its own truly-random seed and its own regime.

    python tools/fuzz_extreme.py --sets 100 --cases 100

Where `fuzz_damage.py` samples a deliberately tame space (10 stat fields, sparse containers, a handful of
"interesting" magnitudes), this one is built to break things:

  * **all 51 fields**, not the 10 the normal generator touches. `PhisycalWeaponMastery` in particular feeds
    the MinWC/MaxWC chain and had never once been fuzzed.
  * **all 15 blocks** including the ones the normal generator leaves neutral.
  * **full int32 magnitudes**, both signs, and the exact boundary values.
  * **dense** containers (every field set) as well as sparse ones -- dense is where the rate products get
    large enough to expose ordering and overflow differences.
  * per-set regimes, so the 100 sets span realistic -> absurd rather than all sampling the same shape.

Seeds come from os.urandom, so a run is not reproducible by design -- a failing set prints its own seed and
its own minimal case, and `--seed-list` replays specific ones.
"""
import argparse, json, math, os, random, struct, subprocess, sys, tempfile, shutil, atexit, hashlib, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import fuzz_damage as FZ
from roe_oracle import Oracle, FIELDS

BLOCKS_PLUS = ["PureCharParam", "Item.plus", "ItemPowerRate.plus", "Upgrade.plus", "WeaponTitle.plus",
               "PassiveSkill.plus", "AbnormalState.plus", "LastTune.plus"]
BLOCKS_RATE = ["Item.rate", "ItemPowerRate.rate", "Upgrade.rate", "WeaponTitle.rate",
               "PassiveSkill.rate", "AbnormalState.rate", "LastTune.rate"]
ONLY_FN = None
FNS = ["roe_MinWC", "roe_MaxWC", "roe_MinMA", "roe_MaxMA", "roe_AC", "roe_TH", "roe_TB", "roe_MR",
       "AttackPower", "DefendPower", "CalcDamage"]
# Only the rules the C# side models end-to-end. cureSkill / alwaysHit / healAttack change hit
# handling rather than the damage maths and are not ported yet.
FUZZ_RULES = ["normalPY", "physicalSkill", "normalMA", "magicalSkill", "alwaysCritical"]

I32_MIN, I32_MAX = -2147483648, 2147483647
EDGE = [0, 1, -1, 2, -2, 1000, 999, 1001, -1000, 32767, -32768, 65535, 65536,
        I32_MAX, I32_MIN, I32_MAX - 1, I32_MIN + 1, 1 << 20, -(1 << 20), 1 << 30, -(1 << 30)]

# Per-set regimes. `mag` bounds the random magnitude, `density` is P(field is set), `edge` is P(an edge
# value is used instead of a random one).
REGIMES = [
    ("realistic",  3000,        0.30, 0.10),
    ("dense",      5000,        1.00, 0.10),
    ("wide",       1 << 20,     0.60, 0.25),
    ("huge",       I32_MAX,     0.40, 0.30),
    ("edge-heavy", 1 << 16,     0.70, 0.85),
    ("dense-huge", I32_MAX,     1.00, 0.50),
]


def gen_value(rng, mag, edge_p):
    if rng.random() < edge_p:
        return rng.choice(EDGE)
    v = rng.randint(-mag, mag)
    return max(I32_MIN, min(I32_MAX, v))


def gen_container(rng, mag, density, edge_p):
    c = {}
    for sect in BLOCKS_PLUS + BLOCKS_RATE:
        if rng.random() > 0.85 and density < 1.0:
            continue
        fields = {f: gen_value(rng, mag, edge_p) for f in FIELDS if rng.random() < density}
        if fields:
            c[sect] = fields
    return c


def gen_case(rng, mag, density, edge_p):
    fn = ONLY_FN or rng.choice(FNS)
    case = {"fn": fn, "rule": rng.choice(FUZZ_RULES),
            "att": gen_container(rng, mag, density, edge_p),
            "def": gen_container(rng, mag, density, edge_p),
            "level": rng.choice([0, 1, 2, 61, 100, 200, 255, rng.randint(0, 255)]),
            "deflevel": rng.choice([0, 1, 61, 255, rng.randint(0, 255)]),
            # Player attacking a monster, so roe_LevelGapDamageRevision actually reaches
            # LevelGap_Player_to_Monster. Any other pairing leaves the damage untouched, which is what a
            # rate of 1000 means on the C# side.
            "atttype": 2, "deftype": 5,
            "hp": rng.choice([1, 1000, 32767]), "maxhp": rng.choice([1, 1000, 32767])}
    if fn in ("CalcDamage", "AttackPower"):
        case["roll"] = rng.choice([0, 1, 250, 500, 750, 999, 1000, rng.randint(0, 1000)])
    if fn == "CalcDamage":
        case["crit"] = rng.random() < 0.5
        case["damagerate"] = rng.choice([0, 1, 500, 1000, 1000, 2000, 10000])
        # Large rates on purpose: roe_LevelGapDamageRevision multiplies rate by damage with a 32-BIT imul,
        # so the product WRAPS. Without values big enough to overflow, an integer model and a double model
        # of that step agree, and the fuzz has no power to tell them apart -- confirmed by sabotage.
        case["levelgaprate"] = rng.choice([1000, 1000, 0, 1, 500, 1500, 2000, 10000, -1000,
                                           70000, 100000, 200000, 1000000, -100000,
                                           rng.randint(-5000, 5000), rng.randint(-2**31, 2**31 - 1)])
    return case


def same(a, b, exact=True):
    """Agreement. EXACT by default.

    A 1e-9 relative tolerance is far too loose here: at 1e17 it hides eight whole ULPs, and one ULP in
    MaxWC propagated into a 256-point difference in the integer damage. x87 at 53-bit precision and SSE
    doubles agree bit-for-bit when the operation ORDER matches, so exact equality is the right bar.

    BOTH SIDES ARE COERCED TO float FIRST, and that is not cosmetic. System.Text.Json writes a large
    double with no decimal point (4.028110698197033e+16 -> "40281106981970330"), Python's json then
    parses it as an int, and `int == float` in Python compares EXACTLY -- so a bit-identical result was
    reported as a mismatch. That artifact accounted for most of the "residual" WC failures.
    """
    if a is None or b is None:
        return False
    a = float(a); b = float(b)
    if math.isnan(a) and math.isnan(b):
        return True
    if math.isinf(a) or math.isinf(b):
        return a == b
    if exact:
        return a == b
    return abs(a - b) <= 1e-9 * max(1.0, abs(a), abs(b))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sets", type=int, default=100)
    ap.add_argument("--cases", type=int, default=100)
    ap.add_argument("--regime", default=None, help="restrict every set to one named regime")
    ap.add_argument("--only-fn", default=None, help="restrict every case to one function")
    ap.add_argument("--dump", default=None, help="write the first failing case to this file")
    ap.add_argument("--seed-list", default=None, help="comma-separated seeds to replay instead of random")
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "src", "Fiesta.Bot", "bin", "Release",
                                                  "net10.0", "Fiesta.Bot.dll"))
    a = ap.parse_args()

    tmpdir = tempfile.mkdtemp(prefix="fiesta_xfuzz_")
    atexit.register(lambda: shutil.rmtree(tmpdir, ignore_errors=True))
    tag = hashlib.md5(("%s%s" % (time.time(), os.path.getmtime(a.dll))).encode()).hexdigest()[:10]
    csx = os.path.join(tmpdir, "_xfuzz_%s.csx" % tag)
    with open(csx, "w", encoding="utf-8") as fh:
        fh.write(FZ.CSX.format(dll=os.path.abspath(a.dll).replace("\\", "/")))
    proc = subprocess.Popen(["dotnet-script", csx], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, bufsize=1)
    global ONLY_FN
    ONLY_FN = a.only_fn
    o = Oracle()
    o.set_angle_table()

    if a.seed_list:
        seeds = [int(x) for x in a.seed_list.split(",")]
    else:
        seeds = [struct.unpack("<I", os.urandom(4))[0] for _ in range(a.sets)]

    sets_ok = sets_bad = 0
    tot_agree = tot_bad = tot_err = 0
    failures = []
    per_fn = {}
    t0 = time.time()
    for si, seed in enumerate(seeds):
        rng = random.Random(seed)
        # Regime is derived from the SEED, not the set index, so `--seed-list <seed>` reproduces the exact
        # set. Keying it on the index meant a replay silently ran a DIFFERENT regime from the run being
        # investigated -- a reproduction harness that does not reproduce.
        if a.regime:
            name, mag, density, edge_p = next(r for r in REGIMES if r[0] == a.regime)
        else:
            name, mag, density, edge_p = REGIMES[seed % len(REGIMES)]
        agree = bad = err = 0
        first_bad = None
        for _ in range(a.cases):
            case = gen_case(rng, mag, density, edge_p)
            try:
                go = o.call(case)
            except Exception:
                err += 1
                continue
            proc.stdin.write(json.dumps(case) + "\n")
            proc.stdin.flush()
            try:
                r = json.loads(proc.stdout.readline())
            except Exception:
                err += 1
                continue
            if not r.get("ok"):
                err += 1
                continue
            if same(r["v"], go):
                agree += 1
            else:
                bad += 1
                per_fn[case["fn"]] = per_fn.get(case["fn"], 0) + 1
                if first_bad is None:
                    first_bad = (case, go, r["v"])
        tot_agree += agree; tot_bad += bad; tot_err += err
        if bad == 0:
            sets_ok += 1
            print("  set %3d/%d seed=%-11d %-11s OK   %d/%d%s"
                  % (si + 1, len(seeds), seed, name, agree, a.cases,
                     ("  (%d oracle-refused)" % err) if err else ""))
        else:
            sets_bad += 1
            failures.append((seed, name, first_bad))
            print("  set %3d/%d seed=%-11d %-11s FAIL %d/%d  (%d mismatch)"
                  % (si + 1, len(seeds), seed, name, agree, a.cases, bad))
    proc.stdin.close()

    print("\n%s" % ("=" * 78))
    print("SETS   %d/%d clean" % (sets_ok, len(seeds)))
    print("CASES  agree=%d  mismatch=%d  oracle-refused=%d  (of %d)  in %.0fs"
          % (tot_agree, tot_bad, tot_err, tot_agree + tot_bad + tot_err, time.time() - t0))
    if per_fn:
        print("MISMATCHES BY FUNCTION: " + ", ".join(
              "%s=%d" % kv for kv in sorted(per_fn.items(), key=lambda kv: -kv[1])))
    for seed, name, (case, go, gc) in failures[:5]:
        print("\nFIRST MISMATCH in set seed=%d (%s):\n  oracle=%r csharp=%r\n  %s"
              % (seed, name, go, gc, json.dumps(case)))
    if failures and a.dump:
        with open(a.dump, "w", encoding="utf-8") as fh:
            json.dump(failures[0][2][0], fh)
        print("")
        print("first failing case written to %s (feed it to tools/minimise_case.py)" % a.dump)
    return 1 if tot_bad else 0


if __name__ == "__main__":
    sys.exit(main())
