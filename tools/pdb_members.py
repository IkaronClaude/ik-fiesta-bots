#!/usr/bin/env python
"""Recover real struct member offsets from a PDB by scanning LF_MEMBER records.

    python tools/pdb_members.py --pdb Z:/ServerSource/Zone00/Zone.pdb --anchor Str,Con,Dex,Int,Men
    python tools/pdb_members.py --pdb ... --anchor PureCharParam,Item,ItemPowerRate

Why not a full MSF/PDB parser: the CodeView type records are self-describing and dense, so a linear scan for
the LF_MEMBER leaf recovers (name, offset) pairs without decoding the container at all — the same trick that
made pdb_disasm.py work off S_PUB32 records. A run of members is then identified by matching an anchor
sequence of names the caller already knows.

LF_MEMBER (0x150D):
    uint16 leaf; uint16 attr; uint32 typeIndex; <numeric leaf> offset; char name[];
The numeric leaf is the value itself when < 0x8000, otherwise a tagged 1/2/4-byte value.
"""
import argparse, re, struct

LF_MEMBER = 0x150D
NUMERIC = {0x8000: ("<b", 1), 0x8001: ("<h", 2), 0x8002: ("<H", 2), 0x8003: ("<i", 4), 0x8004: ("<I", 4)}


def numeric(buf, i):
    """Return (value, bytes_consumed) for a CodeView numeric leaf at buf[i:]."""
    v = struct.unpack_from("<H", buf, i)[0]
    if v < 0x8000:
        return v, 2
    if v in NUMERIC:
        fmt, n = NUMERIC[v]
        return struct.unpack_from(fmt, buf, i + 2)[0], 2 + n
    return None, None


def members(pdb):
    """Yield (file_pos, name, offset) for every LF_MEMBER-looking record."""
    for m in re.finditer(struct.pack("<H", LF_MEMBER), pdb):
        i = m.start()
        if i + 10 > len(pdb):
            continue
        off, n = numeric(pdb, i + 8)
        if off is None:
            continue
        j = i + 8 + n
        e = pdb.find(b"\x00", j)
        if e < 0 or e - j == 0 or e - j > 64:
            continue
        name = pdb[j:e]
        if not re.fullmatch(rb"[A-Za-z_][A-Za-z0-9_]*", name):
            continue
        yield i, name.decode("latin-1"), off


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pdb", required=True)
    ap.add_argument("--anchor", required=True, help="comma-separated names that start the run")
    ap.add_argument("--count", type=int, default=60, help="members to print from the anchor")
    a = ap.parse_args()
    pdb = open(a.pdb, "rb").read()
    anchor = [x.strip() for x in a.anchor.split(",") if x.strip()]

    recs = sorted(members(pdb))
    print("LF_MEMBER records found: %d" % len(recs))
    names = [r[1] for r in recs]
    hits = 0
    for k in range(len(names) - len(anchor)):
        if names[k:k + len(anchor)] == anchor:
            hits += 1
            print("\n=== run at record %d (file 0x%X) ===" % (k, recs[k][0]))
            prev = None
            for pos, nm, off in recs[k:k + a.count]:
                delta = "" if prev is None else "  (+%d)" % (off - prev)
                print("   +0x%04X  %-28s %s" % (off, nm, delta))
                prev = off
            if hits >= 2:
                break
    if not hits:
        print("anchor sequence not found:", anchor)


if __name__ == "__main__":
    main()
