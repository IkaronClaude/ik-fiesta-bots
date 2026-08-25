#!/usr/bin/env python
"""Differential fuzz: the C# DamageFormula against the Unicorn oracle running the REAL Zone.exe code.

    python tools/fuzz_damage.py --n 400
    python tools/fuzz_damage.py --n 2000 --seed 7 --fn AttackPower

Any disagreement is a bug in the C# port, by construction: the oracle *is* the server. Every mismatch is
printed with the exact input that produced it, so it can be replayed.

The generator deliberately includes the awkward cases as well as random ones -- all-zero containers (which
exercise the `<= 0 -> 1` clamps), single-field spikes, rate halves at 0 and at extremes, negative values,
and roll/crit at both ends -- because the integer boundary is where a "close enough" port stops matching.
"""
import argparse, json, os, random, subprocess, sys, tempfile, shutil, atexit

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

SECTIONS_PLUS = ["PureCharParam", "Item.plus", "ItemPowerRate.plus", "Upgrade.plus", "WeaponTitle.plus",
                 "PassiveSkill.plus", "AbnormalState.plus", "LastTune.plus"]
SECTIONS_RATE = ["Item.rate", "ItemPowerRate.rate", "Upgrade.rate", "WeaponTitle.rate",
                 "PassiveSkill.rate", "AbnormalState.rate", "LastTune.rate"]
CORE = ["Str", "Con", "Dex", "Men"]
OWN = ["WCmin", "WCmax", "AC", "TH", "TB", "MR"]
INTERESTING = [0, 1, -1, 2, 1000, 999, 1001, 32767, -32768, 100000]


def gen_container(rng, style):
    c = {}
    if style == "zero":
        return c
    if style == "neutral":
        return c
    fields = CORE + OWN
    if style == "spike":
        sect = rng.choice(SECTIONS_PLUS + SECTIONS_RATE)
        c[sect] = {rng.choice(fields): rng.choice(INTERESTING)}
        return c
    for sect in SECTIONS_PLUS:
        if rng.random() < 0.5:
            c[sect] = {f: rng.choice(INTERESTING) if rng.random() < 0.3 else rng.randint(-50, 3000)
                       for f in fields if rng.random() < 0.5}
    for sect in SECTIONS_RATE:
        if rng.random() < 0.4:
            c[sect] = {f: rng.choice([0, 1, 500, 1000, 1000, 2000, 5000])
                       for f in fields if rng.random() < 0.5}
    return c


def gen_case(rng, fn):
    style = rng.choice(["random", "random", "random", "spike", "zero", "neutral"])
    case = {"fn": fn,
            "att": gen_container(rng, style),
            "def": gen_container(rng, rng.choice(["random", "spike", "neutral"])),
            "level": rng.choice([1, 2, 61, 100, 200, 255]),
            "hp": 1000, "maxhp": 1000}
    if fn == "CalcDamage":
        case["crit"] = rng.choice([True, False])
        case["roll"] = rng.choice([0, 1, 250, 500, 750, 999, 1000])
        case["damagerate"] = rng.choice([1000, 1000, 500, 2000])
    elif fn == "AttackPower":
        case["roll"] = rng.choice([0, 1, 250, 500, 750, 999, 1000])
    return case


# ---- the C# side ------------------------------------------------------------------------------------
# This is a THIN ADAPTER over the library's public API and nothing more: it maps the oracle's wire names
# ("Item.plus", "roe_MinWC") onto Fiesta.Bot.Combat and prints the result. The library knows nothing about
# it, so the whole fuzzing layer can be deleted without touching production code.
CSX = r"""
#r "{dll}"
using System;
using System.Text.Json;
using Fiesta.Bot.Combat;

// The oracle names blocks the way the server's PDB does; the library names them by intent.
static StatBlock Block(CombatStats stats, string wireName) {{
    if (wireName == "PureCharParam") return stats.Base;
    var dot = wireName.IndexOf('.');
    if (dot < 0) throw new Exception("section " + wireName);
    var source = Enum.Parse<StatModifier>(wireName.Substring(0, dot));
    var half = wireName.Substring(dot + 1);
    return half == "plus" ? stats.Plus(source)
         : half == "rate" ? stats.Rate(source)
         : throw new Exception("half " + half);
}}

static ICombatant Build(JsonElement spec, int level) {{
    var stats = CombatStats.Unmodified();
    if (spec.ValueKind == JsonValueKind.Object)
        foreach (var section in spec.EnumerateObject()) {{
            var block = Block(stats, section.Name);
            foreach (var field in section.Value.EnumerateObject())
                block[Enum.Parse<Stat>(field.Name)] = field.Value.GetInt32();
        }}
    return new Combatant(level, stats);
}}

string line;
while ((line = Console.ReadLine()) != null) {{
    if (line.Length == 0) continue;
    try {{
        var c = JsonDocument.Parse(line).RootElement;
        int level = c.TryGetProperty("level", out var l) ? l.GetInt32() : 61;
        int defLevel = c.TryGetProperty("deflevel", out var dl) ? dl.GetInt32() : level;
        var attacker = Build(c.GetProperty("att"), level);
        var defender = Build(c.GetProperty("def"), defLevel);
        string fn = c.GetProperty("fn").GetString();
        double v;
        switch (fn) {{
            case "roe_MinWC":   v = DamageCalculator.MinWeaponDamage(attacker); break;
            case "roe_MaxWC":   v = DamageCalculator.MaxWeaponDamage(attacker); break;
            case "roe_AC":      v = DamageCalculator.ArmourClass(defender);     break;
            case "roe_TH":      v = DamageCalculator.ToHitRating(attacker);     break;
            case "roe_TB":      v = DamageCalculator.ToBlockRating(defender);   break;
            case "roe_MR":      v = DamageCalculator.MagicResistance(defender); break;
            case "DefendPower": v = DamageCalculator.DefendPower(defender);     break;
            case "AttackPower":
                v = DamageCalculator.AttackPower(attacker, c.GetProperty("roll").GetInt32());
                break;
            case "CalcDamage":
                v = DamageCalculator.ResolveDamage(attacker, defender, new AttackModifiers {{
                        RollPermille = c.GetProperty("roll").GetInt32(),
                        ForceCritical = c.GetProperty("crit").GetBoolean(),
                        DamageRatePermille = c.TryGetProperty("damagerate", out var dr) ? dr.GetInt32() : 1000,
                        LevelGapRatePermille = c.TryGetProperty("levelgaprate", out var lg) ? lg.GetInt32() : 1000,
                    }});
                break;
            default: throw new Exception("fn " + fn);
        }}
        Console.WriteLine(JsonSerializer.Serialize(new {{ ok = true, v }}));
    }} catch (Exception ex) {{
        Console.WriteLine(JsonSerializer.Serialize(new {{ ok = false, err = ex.Message }}));
    }}
    Console.Out.Flush();
}}
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--n", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--fn", default=None, help="restrict to one function")
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "src", "Fiesta.Bot", "bin", "Release",
                                                  "net10.0", "Fiesta.Bot.dll"))
    a = ap.parse_args()
    rng = random.Random(a.seed)

    fns = [a.fn] if a.fn else ["roe_MinWC", "roe_MaxWC", "roe_AC", "roe_TH", "roe_TB", "roe_MR",
                               "AttackPower", "DefendPower", "CalcDamage"]
    cases = [gen_case(rng, rng.choice(fns)) for _ in range(a.n)]

    from roe_oracle import Oracle
    o = Oracle()
    o.set_angle_table()

    import hashlib, time
    # UNIQUE FILENAME PER RUN, in a throwaway directory. dotnet-script caches compiled scripts BY
    # FILENAME, so reusing one name silently keeps running the PREVIOUS build -- a corrected
    # DamageFormula.cs appeared to change nothing at all until this was found, and three real fixes
    # were wrongly recorded as ineffective. The name is derived from the DLL mtime so a rebuild always
    # gets a fresh compile.
    tmpdir = tempfile.mkdtemp(prefix="fiesta_dmg_")
    atexit.register(lambda: shutil.rmtree(tmpdir, ignore_errors=True))
    tag = hashlib.md5(("%s%s" % (time.time(), os.path.getmtime(a.dll))).encode()).hexdigest()[:10]
    csx = os.path.join(tmpdir, "_fuzz_cs_%s.csx" % tag)
    with open(csx, "w", encoding="utf-8") as fh:
        fh.write(CSX.format(dll=os.path.abspath(a.dll).replace("\\", "/")))
    proc = subprocess.Popen([r"dotnet-script", csx], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, bufsize=1)

    bad = same = err = 0
    for case in cases:
        try:
            got_o = o.call(case)
        except Exception as e:
            err += 1
            continue
        proc.stdin.write(json.dumps(case) + "\n")
        proc.stdin.flush()
        out = proc.stdout.readline()
        try:
            r = json.loads(out)
        except Exception:
            err += 1
            continue
        if not r.get("ok"):
            err += 1
            continue
        got_c = r["v"]
        if abs(got_c - got_o) <= 1e-9 * max(1.0, abs(got_o)):
            same += 1
        else:
            bad += 1
            if bad <= 12:
                print("MISMATCH %-12s oracle=%-20r csharp=%-20r" % (case["fn"], got_o, got_c))
                print("   case: %s" % json.dumps(case))
    proc.stdin.close()
    print("\nagree=%d  mismatch=%d  errors=%d  (of %d)" % (same, bad, err, len(cases)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
