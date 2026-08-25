#!/usr/bin/env python
"""Shrink a failing fuzz case to the smallest input that still disagrees.

A 40-field disagreement tells you nothing; a 2-field one names the bug. Greedily drops sections, then
fields, then walks each surviving value down toward zero, keeping any change that preserves the mismatch.
"""
import json, os, subprocess, sys, hashlib, time, tempfile, shutil, atexit
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import fuzz_damage as FZ
from roe_oracle import Oracle


def main():
    case = json.loads(sys.argv[1] if len(sys.argv) > 1 else sys.stdin.read())
    dll = os.path.join(HERE, "..", "src", "Fiesta.Bot", "bin", "Release", "net10.0", "Fiesta.Bot.dll")
    # UNIQUE FILENAME PER RUN, in a throwaway directory. dotnet-script caches compiled scripts BY
    # FILENAME, so reusing one name silently keeps running the PREVIOUS build -- a corrected
    # DamageFormula.cs appeared to change nothing at all until this was found, and three real fixes
    # were wrongly recorded as ineffective. The name is derived from the DLL mtime so a rebuild always
    # gets a fresh compile.
    tmpdir = tempfile.mkdtemp(prefix="fiesta_dmg_")
    atexit.register(lambda: shutil.rmtree(tmpdir, ignore_errors=True))
    tag = hashlib.md5(("%s%s" % (time.time(), os.path.getmtime(dll))).encode()).hexdigest()[:10]
    csx = os.path.join(tmpdir, "_min_cs_%s.csx" % tag)
    open(csx, "w", encoding="utf-8").write(FZ.CSX.format(dll=os.path.abspath(dll).replace("\\", "/")))
    proc = subprocess.Popen(["dotnet-script", csx], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, bufsize=1)
    o = Oracle(); o.set_angle_table()

    def bad(c):
        try:
            go = o.call(c)
        except Exception:
            return False
        proc.stdin.write(json.dumps(c) + "\n"); proc.stdin.flush()
        try:
            r = json.loads(proc.stdout.readline())
        except Exception:
            return False
        return bool(r.get("ok")) and abs(r["v"] - go) > 1e-9 * max(1.0, abs(go))

    assert bad(case), "case does not reproduce"
    # 1. drop whole sections, 2. drop single fields, 3. shrink values
    for side in ("att", "def"):
        for sect in list(case.get(side, {})):
            t = json.loads(json.dumps(case)); t[side].pop(sect)
            if bad(t): case = t
    for side in ("att", "def"):
        for sect in list(case.get(side, {})):
            for f in list(case[side].get(sect, {})):
                t = json.loads(json.dumps(case)); t[side][sect].pop(f)
                if not t[side][sect]: t[side].pop(sect)
                if bad(t): case = t
    for side in ("att", "def"):
        for sect in list(case.get(side, {})):
            for f in list(case[side][sect]):
                for cand in (0, 1, 1000, case[side][sect][f] // 2):
                    t = json.loads(json.dumps(case)); t[side][sect][f] = cand
                    if bad(t): case = t; break
    proc.stdin.close()
    print(json.dumps(case, indent=1))
    print("oracle=%r" % o.call(case))


if __name__ == "__main__":
    main()
