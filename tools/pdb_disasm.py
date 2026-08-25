#!/usr/bin/env python
"""Resolve a mangled symbol in a PDB to its address and disassemble it out of the matching EXE.

    python tools/pdb_disasm.py --exe Z:/ServerSource/Zone00/Zone.exe --pdb Z:/ServerSource/Zone00/Zone.pdb \
        --sym "?roe_Damage@RulesOfEngagement@@MAENPAUEngageArgument@@NN@Z"
    python tools/pdb_disasm.py ... --find roe_          # list matching symbols and their addresses

WHY THIS EXISTS. The damage formula is not in any data table -- it is code, in
RulesOfEngagement::roe_Damage(EngageArgument*, double attack, double defend). Curve-fitting the wire gave a
constant of 141 that means nothing; reading the function gives the actual arithmetic. `strings` is not installed
on this box and no disassembler is on PATH, but capstone and pefile are importable, so this is self-contained.

HOW THE ADDRESS IS FOUND, without a full MSF/PDB parser: public symbols are stored as S_PUB32 records

    struct PUBSYM32 { uint16 reclen; uint16 rectyp /*0x110E*/; uint32 flags; uint32 off; uint16 seg; char name[]; }

so the 14 bytes immediately BEFORE a mangled name are that record's header. Scanning the raw PDB for the name
and validating `rectyp == 0x110E` at name-12 is enough to recover (seg, off) without decoding the MSF container
at all. seg/off then map through the PE section table to an RVA and a file offset.
"""
import argparse, re, struct, sys

S_PUB32 = 0x110E


def sections(exe):
    import pefile
    pe = pefile.PE(exe, fast_load=True)
    out = []
    for s in pe.sections:
        out.append((s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData,
                    s.Name.rstrip(b"\x00").decode("latin-1")))
    return out, pe.OPTIONAL_HEADER.ImageBase


def publics(pdb_bytes, needle=None):
    """Yield (name, seg, off) for every S_PUB32 record whose name matches."""
    pat = re.escape(needle.encode()) if needle else rb"[?_][ -~]{4,180}"
    for m in re.finditer(pat, pdb_bytes):
        i = m.start()
        if i < 14:
            continue
        rectyp = struct.unpack_from("<H", pdb_bytes, i - 12)[0]
        if rectyp != S_PUB32:
            continue
        off, seg = struct.unpack_from("<IH", pdb_bytes, i - 6)
        end = pdb_bytes.find(b"\x00", i)
        yield pdb_bytes[i:end].decode("latin-1"), seg, off


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True)
    ap.add_argument("--pdb", required=True)
    ap.add_argument("--sym")
    ap.add_argument("--find")
    ap.add_argument("--count", type=int, default=180, help="instructions to disassemble")
    a = ap.parse_args()

    pdb = open(a.pdb, "rb").read()
    secs, base = sections(a.exe)
    exe = open(a.exe, "rb").read()

    if a.find:
        seen = set()
        for name, seg, off in publics(pdb):
            if a.find in name and name not in seen:
                seen.add(name)
                print("  seg%-3d off=0x%06X  %s" % (seg, off, name))
        return

    hits = [h for h in publics(pdb, a.sym)]
    if not hits:
        print("symbol not found as S_PUB32:", a.sym)
        return
    name, seg, off = hits[0]
    va, vsz, praw, rsz, sname = secs[seg - 1]
    rva = va + off
    fo = praw + off
    print("%s\n  seg %d (%s)  off 0x%X  RVA 0x%X  VA 0x%X  file 0x%X\n" % (name, seg, sname, off, rva, base + rva, fo))

    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    md.detail = False
    n = 0
    for ins in md.disasm(exe[fo:fo + 4096], base + rva):
        print("  %08X  %-24s %s" % (ins.address, ins.mnemonic, ins.op_str))
        n += 1
        if ins.mnemonic == "ret" or n >= a.count:
            break


if __name__ == "__main__":
    main()
