"""Find `if (...)` statements with NO braces whose *apparent* body spans more than one line.

Why this exists: in ZoneView the "took N with NO tracked attacker" CRITICAL log sat directly
under an unbraced `if (Aggressors.Count == 0)`, indented as though it were guarded by it. It
was not. The line fired on every hit taken instead of only unattributed ones (1295 of them on
one bot), and a CRITICAL that fires unconditionally destroys the signal it exists to give —
`grep CRITICAL` is the first thing read on any investigation.

The pattern is invisible in review precisely because the indentation lies, so scan for it:
an unbraced if, a single-statement body, and a following line at the SAME indentation as that
body (i.e. it reads as part of the body but executes unconditionally).

Usage:  python tools/unbraced_if.py [root=src]
Exit code 1 if any suspect is found, so it can gate CI.
"""
import re
import sys
import pathlib

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "src")
hits = []

for f in root.rglob("*.cs"):
    sp = f.as_posix()
    if "/obj/" in sp or "/bin/" in sp:
        continue
    lines = f.read_text(encoding="utf-8", errors="replace").split("\n")
    for i, line in enumerate(lines[:-2]):
        s = line.strip()
        if not re.match(r"^(if|else if)\s*\(", s):
            continue
        # A brace or a same-line body is fine; we only care about the dangling form.
        if s.endswith("{") or s.endswith(";"):
            continue
        ind = len(line) - len(line.lstrip())
        a, b = lines[i + 1], lines[i + 2]
        if not a.strip() or not b.strip():
            continue
        ia = len(a) - len(a.lstrip())
        ib = len(b) - len(b.lstrip())
        # body deeper than the if, and the next line at the body's depth => outside the if
        if ia > ind and ib == ia and a.strip().endswith(";") \
                and not b.strip().startswith(("else", "//", "}", "?", ":", ".", "&&", "||")):
            hits.append(
                f"{sp}:{i + 1}\n"
                f"    IF   {s[:95]}\n"
                f"    body {a.strip()[:95]}\n"
                f"    ALSO {b.strip()[:95]}   <-- runs unconditionally"
            )

print(f"{len(hits)} suspect unbraced-if bodies\n")
print("\n\n".join(hits))
sys.exit(1 if hits else 0)
