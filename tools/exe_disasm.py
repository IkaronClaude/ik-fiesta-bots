#!/usr/bin/env python
"""Disassemble a PDB-less x86 exe (the 2026 client) from a VA, or find the instructions carrying an absolute operand.

    python tools/exe_disasm.py --exe Z:/ClientOfficialUS/Fiesta.exe --va 0x7EEE00 --count 200 [--through]
    python tools/exe_disasm.py --exe ... --imm 0xC49FC4 [--imm ...]     # every instruction whose imm32/disp32 is one of these
    python tools/exe_disasm.py --exe ... --bytes 83C70883FB057C            # raw byte pattern (hex, ?? = wildcard)

Each --imm hit prints the site VA, the bytes and the mnemonic; pair with callers.py to climb to the owning function.
"""
import argparse
import re
import struct

import pefile
from capstone import CS_ARCH_X86, CS_MODE_32, Cs


def load(exe):
    pe = pefile.PE(exe, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
    return base, base + text.VirtualAddress, text.get_data()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', required=True)
    ap.add_argument('--va')
    ap.add_argument('--count', type=int, default=120)
    ap.add_argument('--through', action='store_true')
    ap.add_argument('--imm', action='append')
    ap.add_argument('--bytes')
    a = ap.parse_args()
    base, t0, raw = load(a.exe)
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    if a.va:
        va = int(a.va, 16)
        n = 0
        for ins in md.disasm(raw[va - t0:va - t0 + 16 * a.count], va):
            print('  %08X  %-8s %s' % (ins.address, ins.mnemonic, ins.op_str))
            n += 1
            if n >= a.count or (ins.mnemonic == 'ret' and not a.through):
                break
    if a.imm:
        for v in a.imm:
            pat = struct.pack('<I', int(v, 16))
            for m in re.finditer(re.escape(pat), raw):
                i = m.start()
                # the instruction holding the operand starts a few bytes before; try starts 1..7 back
                for back in range(1, 8):
                    s = i - back
                    if s < 0:
                        continue
                    ins = next(md.disasm(raw[s:s + 16], t0 + s), None)
                    if ins and s + ins.size > i + 3 and ins.size >= back + 4:
                        print('  %s @ %08X  %-24s %-6s %s' % (v, t0 + s, raw[s:s + ins.size].hex(), ins.mnemonic, ins.op_str))
                        break
    if a.bytes:
        pat = b''.join(b'.' if a.bytes[k:k + 2] == '??' else re.escape(bytes.fromhex(a.bytes[k:k + 2])) for k in range(0, len(a.bytes), 2))
        for m in re.finditer(pat, raw, re.S):
            print('  bytes @ %08X' % (t0 + m.start()))


if __name__ == '__main__':
    main()
