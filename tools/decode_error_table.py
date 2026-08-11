#!/usr/bin/env python
"""Decode a server ERROR CODE into the exact string the real client shows the player.

The client turns an error code into a message with a SWITCH: `cmp eax, N` / `jmp [eax*4 + TABLE]`,
and each case does `push <eTextID>` before calling the message box. So the mapping we want is
    error code -> (code - base) -> jump-table entry -> `push <textid>` -> TextData*.shn string
and it lives in the client binary, not in either PDB. Neither Fiesta.pdb nor Zone.pdb names these
codes at all — they are bare immediates in the server's code — which is why grepping for constants
finds nothing and this is the route that works.

    # decode a known table (see docs/jumptables/)
    python tools/decode_error_table.py --table 0x4AA9D0 --base 0x0FC0 --count 25

    # find candidate switch tables near a string you can see in-game
    python tools/decode_error_table.py --find "out of casting range"

Requires: Z:/ClientProd2/Fiesta.bin and tools/text-ids.json (regenerate with the csx in the runbook).
"""
import argparse, json, os, struct, sys

CLIENT = r"Z:/ClientProd2/Fiesta.bin"
TEXTIDS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "text-ids.json")


def load_pe(path):
    data = open(path, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe+4] != b"PE\0\0": sys.exit("not a PE file")
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    optsz = struct.unpack_from("<H", data, pe + 20)[0]
    imagebase = struct.unpack_from("<I", data, pe + 24 + 28)[0]
    secs, off = [], pe + 24 + optsz
    for _ in range(nsec):
        vsz, va, rsz, roff = struct.unpack_from("<IIII", data, off + 8)
        secs.append((va, vsz, roff, rsz)); off += 40
    def va2off(va):
        rva = va - imagebase
        for sva, vsz, roff, rsz in secs:
            if sva <= rva < sva + max(vsz, rsz): return roff + (rva - sva)
        return None
    return data, imagebase, va2off


def decode(table_va, base, count):
    data, _, va2off = load_pe(CLIENT)
    texts = json.load(open(TEXTIDS, encoding="utf-8")) if os.path.exists(TEXTIDS) else {}
    t = va2off(table_va)
    if t is None: sys.exit(f"table VA 0x{table_va:X} is not in any section")
    out = {}
    for i in range(count):
        ep = struct.unpack_from("<I", data, t + 4 * i)[0]
        fo = va2off(ep)
        code = base + i
        if fo is None: out[f"0x{code:04X}"] = None; continue
        seg = data[fo:fo+24]
        tid = next((struct.unpack_from("<I", seg, k+1)[0] for k in range(12) if seg[k] == 0x68), None)
        out[f"0x{code:04X}"] = texts.get(str(tid)) if tid else None
        print(f"  0x{code:04X}  " + (out[f"0x{code:04X}"] or f"(textid {tid:#010x} not in TextData)" if tid else "(no push found)"))
    return out


def find(needle):
    """Locate the textid of a string, then every `push <that id>` in the binary — the case bodies."""
    texts = json.load(open(TEXTIDS, encoding="utf-8"))
    hits = [(int(k), v) for k, v in texts.items() if needle.lower() in v.lower()]
    if not hits: sys.exit(f"no client string contains {needle!r}")
    data, _, _ = load_pe(CLIENT)
    for tid, s in hits[:5]:
        print(f"\ntextid 0x{tid:08X}  {s!r}")
        pat = struct.pack("<I", tid); off = data.find(pat)
        while off != -1:
            # a case body looks like: 68 <id>   (push). Show a little context so the switch is visible.
            if off >= 1 and data[off-1] == 0x68:
                print(f"   push @file 0x{off-1:X}   context: " +
                      " ".join(f"{b:02x}" for b in data[max(0, off-24):off+8]))
            off = data.find(pat, off + 1)
    print("\nLook backwards from a case body for `ff 24 85 <table VA LE>` (jmp [eax*4+TABLE]) and the\n"
          "`83 f8 <N>` (cmp eax, N) just above it — that gives you --table and --count.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--table", type=lambda x: int(x, 0), help="switch table VA, e.g. 0x4AA9D0")
    ap.add_argument("--base", type=lambda x: int(x, 0), default=0, help="error code of index 0")
    ap.add_argument("--count", type=int, default=25, help="number of cases (cmp eax, N -> N+1)")
    ap.add_argument("--find", help="locate the switch for a string you saw in game")
    ap.add_argument("--json", help="write the decoded table here")
    a = ap.parse_args()
    if a.find: find(a.find)
    elif a.table:
        res = decode(a.table, a.base, a.count)
        if a.json:
            json.dump(res, open(a.json, "w", encoding="utf-8"), indent=1)
            print(f"\nwritten to {a.json}")
    else: ap.print_help()
