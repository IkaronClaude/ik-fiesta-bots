#!/usr/bin/env python
"""Disassemble a roe_* accessor and name every Parameter::Container offset as `Block.half.Field`.

    python tools/roe_trace.py --sym "?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"

The accessors are long only because they walk every modifier block. Once each `[eax + off]` is resolved to
`AbnormalState.rate.WCmin` instead of `+0x9A8`, the expression reads straight off the FPU op sequence, which
is what a 1:1 reimplementation needs. Layout comes from docs/DAMAGE_FORMULA.md Appendix A (PDB type stream).
"""
import argparse, re, struct

BLOCK = 0xCC
SECTIONS = [(0x0000, "PureCharParam", False), (0x00CC, "Item", True), (0x0264, "ItemPowerRate", True),
            (0x03FC, "Upgrade", True), (0x0594, "WeaponTitle", True), (0x072C, "PassiveSkill", True),
            (0x08C4, "AbnormalState", True), (0x0A5C, "LastTune", True), (0x0BF4, "Total", False)]
FIELDS = ["Str", "Con", "Dex", "Int", "Men", "WCmin", "WCmax", "AC", "TH", "TB", "MAmin", "MAmax",
          "MR", "MH", "MB", "AbsoluteAttack", "AbsoluteDefend", "AbsoluteHit", "AbsoluteBlock",
          "MoveSpeed", "HPRecover", "SPRecover", "CastingTime", "Critical", "PhisycalWeaponMastery",
          "MagicalWeaponMastery", "ShieldAC", "HitRate", "EvaRate", "MACri", "CriDam", "MagCriDam",
          "CriDamRate", "MagCriDamRate", "AttSpeed", "MaxHP", "MaxHP_2", "MaxSP", "HPAbsorption_Hitted",
          "SPAbsorption_Hitted", "HPAbsorption_Hit", "SPAbsorption_Hit", "CriticalTB", "RegistNone",
          "ResistPoison", "ResistDeaseas", "ResistCurse", "ResistMoveSpdDown", "ResistGTI",
          "MaxLP", "LPRecover"]
LOOSE = {0x0CC0: "DotDamagePlus", 0x0CCA: "SPRate", 0x0CCC: "RangeEvasion", 0x0CCE: "flag",
         0x0CD0: "MissPercentFix", 0x0CD2: "DamageReflection", 0x0CD4: "ChangeAbilityInfo",
         0x0CD6: "HealRate", 0x0CD8: "PassiveBuffKeepTimeUPRate", 0x0CDA: "PassiveHealRate",
         0x0CDC: "PassiveCriDamageRatePlus", 0x0CE0: "PassiveHPDownRateWCMin",
         0x0CFC: "PassiveHPDownRateWCMax", 0x0D18: "PassiveHPDownRateMAMin",
         0x0D34: "PassiveHPDownRateMAMax", 0x0D50: "PassiveHPDownRateAC",
         0x0D6C: "PassiveHPDownRateMR", 0x0D88: "PassiveMovingTBPlus",
         0x0DA4: "PhysicalImmuneRate", 0x0DA6: "MagicalImmuneRate", 0x0DA8: "RangeOver"}


def name_off(off):
    if off in LOOSE:
        return LOOSE[off]
    for base, nm, paired in reversed(SECTIONS):
        if off >= base:
            rel = off - base
            span = BLOCK * (2 if paired else 1)
            if rel >= span:
                break
            half, fo = ("plus", rel) if rel < BLOCK else ("rate", rel - BLOCK)
            if fo % 4 or fo // 4 >= len(FIELDS):
                return "%s+0x%X" % (nm, rel)
            f = FIELDS[fo // 4]
            return "%s.%s.%s" % (nm, half, f) if paired else "%s.%s" % (nm, f)
    return "?+0x%X" % off


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=r"Z:/ServerSource/Zone00/Zone.exe")
    ap.add_argument("--pdb", default=r"Z:/ServerSource/Zone00/Zone.pdb")
    ap.add_argument("--sym", required=True)
    ap.add_argument("--count", type=int, default=900)
    ap.add_argument("--all", action="store_true", help="show non-FPU instructions too")
    a = ap.parse_args()

    import pefile
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    pe = pefile.PE(a.exe, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    secs = [(s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData) for s in pe.sections]
    data = open(a.exe, "rb").read()
    pdb = open(a.pdb, "rb").read()

    def fo(va):
        rva = va - base
        for v, vs, pr in secs:
            if v <= rva < v + max(vs, 1):
                return pr + (rva - v)

    off = None
    for m in re.finditer(re.escape(a.sym.encode()), pdb):
        i = m.start()
        if i >= 14 and struct.unpack_from("<H", pdb, i - 12)[0] == 0x110E:
            off, seg = struct.unpack_from("<IH", pdb, i - 6)
            break
    if off is None:
        print("symbol not found"); return
    va = 0x401000 + off
    print("%s\n  VA 0x%X\n" % (a.sym, va))

    md = Cs(CS_ARCH_X86, CS_MODE_32)
    FPU = ("fld", "fild", "fmul", "fdiv", "fadd", "fsub", "fstp", "fst", "fld1", "fldz",
           "fmulp", "faddp", "fsubp", "fdivp", "fdivr", "fsubr", "fcomp", "fcompp", "fxch", "fistp")
    n = 0
    for ins in md.disasm(data[fo(va):fo(va) + 9000], va):
        n += 1
        if n > a.count or ins.mnemonic == "ret":
            print("  ret"); break
        if not a.all and ins.mnemonic not in FPU:
            continue
        ops = ins.op_str
        m2 = re.search(r"\[eax \+ (0x[0-9a-f]+)\]", ops)
        if m2:
            ops = "%-22s ; %s" % (ops, name_off(int(m2.group(1), 16)))
        elif ops == "dword ptr [eax]":
            ops = "%-22s ; %s" % (ops, name_off(0))
        print("  %08X  %-7s %s" % (ins.address, ins.mnemonic, ops))


if __name__ == "__main__":
    main()
