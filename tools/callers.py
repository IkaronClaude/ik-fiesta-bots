#!/usr/bin/env python
"""Call / jump sites of a function: every `E8 rel32` / `E9 rel32` in an executable section that lands on the
target VA, each printed with the public symbol that contains the site (from the PDB, if one is given).

    python tools/callers.py --exe Z:/ClientSource/Fiesta.bin --pdb Z:/ClientSource/Fiesta.pdb --va 0x6AEA70 [--va ...]
    python tools/callers.py --exe Z:/ClientOfficialUS/Fiesta.exe --va 0x5C5550          # no PDB: raw site VAs only

WHY: pdb_disasm.py answers "what does this function do"; this answers "who runs it" - the question that tells
whether a client path (quest accept, map open, a packet handler) reaches a given helper at all.
"""
import argparse
import bisect
import struct

from pdb_disasm import publics, sections


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', required=True)
    ap.add_argument('--pdb')
    ap.add_argument('--va', action='append', required=True, help='target VA (hex), repeatable')
    a = ap.parse_args()
    data = open(a.exe, 'rb').read()
    secs, base = sections(a.exe)
    names = {}
    if a.pdb:
        for name, seg, off in publics(open(a.pdb, 'rb').read()):
            if 1 <= seg <= len(secs):
                names[base + secs[seg - 1][0] + off] = name
    keys = sorted(names)
    targets = {int(v, 16) for v in a.va}
    for va0, vs, raw0, rawsize, sname in secs:
        if not sname.startswith('.text'):
            continue
        raw = data[raw0:raw0 + rawsize]
        start = base + va0
        n = len(raw)
        for i in range(n - 4):
            op = raw[i]
            if op != 0xE8 and op != 0xE9:
                continue
            dst = (start + i + 5 + struct.unpack_from('<i', raw, i + 1)[0]) & 0xFFFFFFFF
            if dst in targets:
                va = start + i
                k = bisect.bisect_right(keys, va) - 1
                owner = names[keys[k]] if k >= 0 else '?'
                print('%s 0x%08X -> 0x%08X  in %s' % ('call' if op == 0xE8 else 'jmp ', va, dst, owner))


if __name__ == '__main__':
    main()
