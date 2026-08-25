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
import argparse, json, os, random, subprocess, sys

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
CSX = r'''
#r "{dll}"
using System;
using System.Text.Json;
using Fiesta.Bot.Combat;

static readonly string[] SectPlus = {{"PureCharParam","Item.plus","ItemPowerRate.plus","Upgrade.plus",
    "WeaponTitle.plus","PassiveSkill.plus","AbnormalState.plus","LastTune.plus"}};

static ParamBlock Pick(ParamContainer c, string s) => s switch {{
    "PureCharParam" => c.PureCharParam,
    "Item.plus" => c.ItemPlus, "Item.rate" => c.ItemRate,
    "ItemPowerRate.plus" => c.ItemPowerRatePlus, "ItemPowerRate.rate" => c.ItemPowerRateRate,
    "Upgrade.plus" => c.UpgradePlus, "Upgrade.rate" => c.UpgradeRate,
    "WeaponTitle.plus" => c.WeaponTitlePlus, "WeaponTitle.rate" => c.WeaponTitleRate,
    "PassiveSkill.plus" => c.PassiveSkillPlus, "PassiveSkill.rate" => c.PassiveSkillRate,
    "AbnormalState.plus" => c.AbnormalStatePlus, "AbnormalState.rate" => c.AbnormalStateRate,
    "LastTune.plus" => c.LastTunePlus, "LastTune.rate" => c.LastTuneRate,
    "Total" => c.Total, _ => throw new Exception("section " + s)
}};

static ParamContainer Build(JsonElement e) {{
    var c = ParamContainer.Neutral();
    if (e.ValueKind != JsonValueKind.Object) return c;
    foreach (var sect in e.EnumerateObject()) {{
        var b = Pick(c, sect.Name);
        foreach (var f in sect.Value.EnumerateObject())
            b[Enum.Parse<ParamField>(f.Name)] = f.Value.GetInt32();
    }}
    return c;
}}

string line;
while ((line = Console.ReadLine()) != null) {{
    if (line.Length == 0) continue;
    try {{
        var c = JsonDocument.Parse(line).RootElement;
        var att = Build(c.GetProperty("att"));
        var def = Build(c.GetProperty("def"));
        int lvl = c.TryGetProperty("level", out var l) ? l.GetInt32() : 61;
        string fn = c.GetProperty("fn").GetString();
        double v;
        switch (fn) {{
            case "roe_MinWC": v = DamageFormula.MinWc(att); break;
            case "roe_MaxWC": v = DamageFormula.MaxWc(att); break;
            case "roe_AC":    v = DamageFormula.Ac(def);    break;
            case "roe_TH":    v = DamageFormula.Th(att);    break;
            case "roe_TB":    v = DamageFormula.Tb(def);    break;
            case "roe_MR":    v = DamageFormula.Mr(def);    break;
            case "AttackPower": v = DamageFormula.AttackPower(att, c.GetProperty("roll").GetInt32()); break;
            case "DefendPower": v = DamageFormula.DefendPower(def); break;
            case "CalcDamage":
                v = DamageFormula.CalcDamage(att, def, lvl,
                        c.GetProperty("roll").GetInt32(), c.GetProperty("crit").GetBoolean(),
                        c.TryGetProperty("damagerate", out var dr) ? dr.GetInt32() : 1000);
                break;
            default: throw new Exception("fn " + fn);
        }}
        Console.WriteLine(JsonSerializer.Serialize(new {{ ok = true, v }}));
    }} catch (Exception ex) {{
        Console.WriteLine(JsonSerializer.Serialize(new {{ ok = false, err = ex.Message }}));
    }}
    Console.Out.Flush();
}}
'''


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

    csx = os.path.join(HERE, "_fuzz_cs.csx")
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
