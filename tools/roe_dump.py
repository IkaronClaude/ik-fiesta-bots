#!/usr/bin/env python
"""Dump the arithmetic of every RulesOfEngagement method out of Zone.exe.

    python tools/roe_dump.py > docs/roe_raw.txt

Prints, per symbol: the FPU/integer arithmetic only (control flow and the debug-logging blocks are dropped),
with every `call` resolved to its public symbol and every `fld/fmul/fdiv/fadd qword ptr [abs]` resolved to the
actual double constant. That turns a wall of x86 into something you can read the formula off.

The logging blocks are ~80% of each function and are gated on a global flag, so they are filtered by skipping
runs of instructions between a `cmp byte ptr [<log flag>], ...` and the join point. Cheap heuristic: drop any
instruction that touches the known logging globals or calls into the logging helpers.
"""
import re, struct, sys

EXE = r"Z:/ServerSource/Zone00/Zone.exe"
PDB = r"Z:/ServerSource/Zone00/Zone.pdb"
S_PUB32 = 0x110E
ARITH = {"fld", "fild", "fmul", "fdiv", "fadd", "fsub", "fdivr", "fsubr", "fstp", "fst", "fchs", "fabs",
         "fld1", "fldz", "fmulp", "faddp", "fsubp", "fdivp", "imul", "idiv", "div", "mul", "sub", "add",
         "movzx", "movsx", "call", "ret", "fcomp", "fcompp", "fucompp", "fistp", "frndint", "fsqrt"}
# logging plumbing seen in every roe_ function
LOG_CALLS = {0x4682D0, 0x6569EA, 0x4191B0, 0x657E94, 0x657D88, 0x658180, 0x658124, 0x656AF9, 0x657200}


def load():
    import pefile
    pe = pefile.PE(EXE, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    secs = [(s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData) for s in pe.sections]
    data = open(EXE, "rb").read()
    pdb = open(PDB, "rb").read()
    return pe, base, secs, data, pdb


def fileoff(secs, base, va):
    rva = va - base
    for v, vs, pr in secs:
        if v <= rva < v + max(vs, 1):
            return pr + (rva - v)
    return None


def publics(pdb):
    byoff, byname = {}, {}
    for m in re.finditer(rb"[?_][ -~]{4,180}", pdb):
        i = m.start()
        if i < 14 or struct.unpack_from("<H", pdb, i - 12)[0] != S_PUB32:
            continue
        off, seg = struct.unpack_from("<IH", pdb, i - 6)
        name = pdb[i:pdb.find(b"\x00", i)].decode("latin-1")
        if seg == 1:
            byoff.setdefault(off, name)
            byname.setdefault(name, off)
    return byoff, byname


def demangle(n):
    """Enough of MSVC mangling to read: ?name@Class@@... -> Class::name"""
    m = re.match(r"\?([^@]+)@([^@]*)@", n)
    return "%s::%s" % (m.group(2), m.group(1)) if m and m.group(2) else n


def main():
    pe, base, secs, data, pdb = load()
    byoff, byname = publics(pdb)
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    md = Cs(CS_ARCH_X86, CS_MODE_32)

    def sym(va):
        return byoff.get(va - 0x401000)

    def dbl(va):
        o = fileoff(secs, base, va)
        if o is None or o + 8 > len(data):
            return None
        try:
            return struct.unpack_from("<d", data, o)[0]
        except Exception:
            return None

    targets = sorted(n for n in byname if "roe_" in n and n.startswith("?"))
    for name in targets:
        va = 0x401000 + byname[name]
        o = fileoff(secs, base, va)
        if o is None:
            continue
        print("=" * 100)
        print("%s        [VA 0x%X]" % (demangle(name), va))
        print("=" * 100)
        n = 0
        for ins in md.disasm(data[o:o + 6000], va):
            n += 1
            if n > 700 or ins.mnemonic == "ret":
                if ins.mnemonic == "ret":
                    print("   ret")
                break
            if ins.mnemonic not in ARITH:
                continue
            ops = ins.op_str
            if ins.mnemonic == "call":
                t = None
                if ops.startswith("0x"):
                    t = int(ops, 16)
                if t in LOG_CALLS:
                    continue
                s = sym(t) if t else None
                print("   call     %s" % (demangle(s) if s else ops))
                continue
            m = re.search(r"qword ptr \[(0x[0-9a-f]+)\]", ops)
            if m:
                v = dbl(int(m.group(1), 16))
                if v is not None:
                    ops = ops.replace(m.group(0), "CONST(%g)" % v)
            print("   %-8s %s" % (ins.mnemonic, ops))
        print()


if __name__ == "__main__":
    main()
