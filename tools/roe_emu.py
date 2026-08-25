#!/usr/bin/env python
"""Execute the REAL RulesOfEngagement code out of Zone.exe under Unicorn, with a synthetic character.

    python tools/roe_emu.py --mob 84
    python tools/roe_emu.py --mob 84 --ac 1215 --level 61

This is the only way to claim a 1:1 reproduction honestly: rather than transcribe ~200 instructions of FPU
arithmetic by hand and hope, it maps Zone.exe into an emulator, builds a `Parameter::Container` from a mob's
MobInfoServer + MobWeapon row, and calls `roe_MinWC` / `roe_MaxWC` / `roe_AC` / `roe_AttackPower` /
`roe_DefendPower` / `roe_Damage` for real. Whatever comes back IS what the server computes.

Layout of the synthetic objects follows docs/DAMAGE_FORMULA.md Appendix A:
  EngageArgument  +0x00 att, +0x04 def, +0x28 nBMPDamageRate
  object          +0x00 vtable
  vtable          +0x430 so_parameter, +0x4D8 so_GetLevel, +0x4E8 so_GetHP, +0x4F0 so_MaxHP

Unmapped pages are auto-mapped zero-filled, which covers every global the code touches (log flags read as 0,
so the debug-logging blocks are skipped exactly as in production).
"""
import argparse, json, os, re, struct, sys

IMAGE = 0x400000
STACK = 0x00200000
HEAP = 0x30000000
STUB = 0x70000000          # stub function addresses, hooked in Python
BLOCK = 0xCC
SECTIONS = [("PureCharParam", 0x0000, False), ("Item", 0x00CC, True), ("ItemPowerRate", 0x0264, True),
            ("Upgrade", 0x03FC, True), ("WeaponTitle", 0x0594, True), ("PassiveSkill", 0x072C, True),
            ("AbnormalState", 0x08C4, True), ("LastTune", 0x0A5C, True), ("Total", 0x0BF4, False)]
FIELDS = ["Str", "Con", "Dex", "Int", "Men", "WCmin", "WCmax", "AC", "TH", "TB", "MAmin", "MAmax",
          "MR", "MH", "MB", "AbsoluteAttack", "AbsoluteDefend", "AbsoluteHit", "AbsoluteBlock",
          "MoveSpeed", "HPRecover", "SPRecover", "CastingTime", "Critical", "PhisycalWeaponMastery",
          "MagicalWeaponMastery", "ShieldAC", "HitRate", "EvaRate", "MACri", "CriDam", "MagCriDam",
          "CriDamRate", "MagCriDamRate", "AttSpeed", "MaxHP", "MaxHP_2", "MaxSP", "HPAbsorption_Hitted",
          "SPAbsorption_Hitted", "HPAbsorption_Hit", "SPAbsorption_Hit", "CriticalTB", "RegistNone",
          "ResistPoison", "ResistDeaseas", "ResistCurse", "ResistMoveSpdDown", "ResistGTI",
          "MaxLP", "LPRecover"]
FIDX = {n: i * 4 for i, n in enumerate(FIELDS)}
CONTAINER_SIZE = 0x0E00


def publics(pdb, segrva):
    """name -> VA for every S_PUB32 record.

    `segrva` maps a 1-based segment index to its section RVA, so this covers `.rdata` — where the vftables
    live — and not only `.text`. Looking up `??_7ShineMob@ShineObjectClass@@6B@` needs that; restricting to
    segment 1 silently loses every vftable."""
    out = {}
    for m in re.finditer(rb"[?_][ -~]{4,220}", pdb):
        i = m.start()
        if i < 14 or struct.unpack_from("<H", pdb, i - 12)[0] != 0x110E:
            continue
        off, seg = struct.unpack_from("<IH", pdb, i - 6)
        if seg in segrva:
            out.setdefault(pdb[i:pdb.find(b"\x00", i)].decode("latin-1"), IMAGE + segrva[seg] + off)
    return out


class Emu:
    def __init__(self, exe, pdb):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_MEM_UNMAPPED
        import pefile
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        pe = pefile.PE(exe, fast_load=True)
        data = open(exe, "rb").read()
        # map the whole image, then copy each section in
        size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF
        self.uc.mem_map(IMAGE, size)
        self.uc.mem_write(IMAGE, data[:pe.OPTIONAL_HEADER.SizeOfHeaders])
        for s in pe.sections:
            raw = data[s.PointerToRawData:s.PointerToRawData + s.SizeOfRawData]
            if raw:
                self.uc.mem_write(IMAGE + s.VirtualAddress, raw)
        self.uc.mem_map(STACK, 0x200000)
        self.uc.mem_map(HEAP, 0x100000)
        self.uc.mem_map(STUB & ~0xFFF, 0x1000)
        self.segrva = {i + 1: sec.VirtualAddress for i, sec in enumerate(pe.sections)}
        self.syms = publics(open(pdb, "rb").read(), self.segrva)
        self.stubs = {}
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, self._unmapped)
        # NEUTRALISE THE INSTRUMENTATION. Every roe_ function opens with FunctionProfiler::pr_Entrance and
        # closes with pr_Exit. They are pure profiling, they touch a pile of global state we do not have, and
        # left alone they ran 1.5M instructions and then walked off into the stack. Both are
        # `__thiscall void f(char*)` -- one stack argument, callee-cleaned -- so `ret 4` is the exact
        # signature-preserving no-op. This changes nothing about the arithmetic under test.
        for name, code in ((r"?pr_Entrance@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z", b"\xC2\x04\x00"),
                           (r"?pr_Exit@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z", b"\xC2\x04\x00")):
            va = self.syms.get(name)
            if va:
                self.uc.mem_write(va, code)

    def _unmapped(self, uc, access, addr, size, value, user):
        try:
            uc.mem_map(addr & ~0xFFF, 0x1000)
        except Exception:
            pass
        return True

    def _code(self, uc, addr, size, user):
        from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_EIP, UC_X86_REG_EAX
        fn = self.stubs.get(addr)
        if fn is None:
            return
        esp = uc.reg_read(UC_X86_REG_ESP)
        ret = struct.unpack("<I", uc.mem_read(esp, 4))[0]
        val, argbytes = fn()
        uc.reg_write(UC_X86_REG_EAX, val & 0xFFFFFFFF)
        uc.reg_write(UC_X86_REG_ESP, esp + 4 + argbytes)
        uc.reg_write(UC_X86_REG_EIP, ret)

    def add_stub(self, name, value):
        """Emit REAL machine code `mov eax, imm32; ret` instead of hooking.

        Every getter we fake returns a constant (a Container pointer, a level, an HP), so a two-instruction
        thunk is exact. Setting EIP from inside a UC_HOOK_CODE callback proved unreliable here -- it
        returned into the stack and faulted at 0x2FFFF9 -- and emitting real code sidesteps that entirely.
        Plain `ret`, not `ret 4`: these are __thiscall getters with no stack arguments."""
        addr = STUB + len(self.stubs) * 0x10
        self.stubs[addr] = value
        self.uc.mem_write(addr, b"\xB8" + struct.pack("<I", value & 0xFFFFFFFF) + b"\xC3")
        return addr


def build_container(uc, addr, stats):
    """Zero the container, write PureCharParam from `stats`, set every `rate` half to 1000 (neutral)."""
    uc.mem_write(addr, b"\x00" * CONTAINER_SIZE)
    for name, base, paired in SECTIONS:
        if paired:                      # rate half must be neutral permille, not 0
            for i in range(len(FIELDS)):
                uc.mem_write(addr + base + BLOCK + i * 4, struct.pack("<i", 1000))
    for k, v in stats.items():
        if k in FIDX:
            uc.mem_write(addr + FIDX[k], struct.pack("<i", int(v)))
            uc.mem_write(addr + 0x0BF4 + FIDX[k], struct.pack("<i", int(v)))   # Total mirrors it


def call_double(emu, sym, argp, ecx=0):
    """__thiscall/cdecl returning a double in st(0)."""
    from unicorn.x86_const import (UC_X86_REG_ESP, UC_X86_REG_ECX, UC_X86_REG_EIP)
    uc = emu.uc
    va = emu.syms.get(sym)
    if va is None:
        raise KeyError(sym)
    # Return into a thunk that stores st(0) to memory, instead of decoding the 80-bit FP0 register by hand
    # (which silently produced 0.0). `fstp qword ptr [RESULT]` is 6 bytes; we stop one instruction later.
    result = STUB + 0xE80
    ret_magic = STUB + 0xE00
    uc.mem_write(ret_magic, b"\xDD\x1D" + struct.pack("<I", result))
    uc.mem_write(result, b"\x00" * 8)
    stop_at = ret_magic + 6
    esp = STACK + 0x100000
    uc.mem_write(esp - 8, struct.pack("<II", ret_magic, argp))
    uc.reg_write(UC_X86_REG_ESP, esp - 8)
    uc.reg_write(UC_X86_REG_ECX, ecx)
    try:
        uc.emu_start(va, stop_at, count=5_000_000)
    except Exception:
        from unicorn.x86_const import UC_X86_REG_EIP
        eip = uc.reg_read(UC_X86_REG_EIP)
        from capstone import Cs, CS_ARCH_X86, CS_MODE_32
        md = Cs(CS_ARCH_X86, CS_MODE_32)
        try:
            code = bytes(uc.mem_read(eip, 16))
            dis = "; ".join("%s %s" % (i.mnemonic, i.op_str) for i in md.disasm(code, eip))
        except Exception:
            dis = "(unreadable)"
        raise RuntimeError("fault at EIP=0x%X : %s | bytes=%s" % (eip, dis, code.hex()[:32]))
    return struct.unpack("<d", bytes(uc.mem_read(result, 8)))[0]


def call_damage(emu, argp, attack, defend, ecx=0):
    """roe_Damage(EngageArgument*, double attack, double defend) -> double.

    __thiscall: `this` in ECX, then the arg pointer and two 8-byte doubles pushed right-to-left."""
    from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_ECX
    uc = emu.uc
    va = emu.syms["?roe_Damage@RulesOfEngagement@@MAENPAUEngageArgument@@NN@Z"]
    result = STUB + 0xE80
    ret_magic = STUB + 0xE00
    uc.mem_write(ret_magic, b"\xDD\x1D" + struct.pack("<I", result))
    uc.mem_write(result, b"\x00" * 8)
    esp = STACK + 0x100000 - 0x40
    frame = struct.pack("<I", ret_magic) + struct.pack("<I", argp) + \
        struct.pack("<d", attack) + struct.pack("<d", defend)
    uc.mem_write(esp, frame)
    uc.reg_write(UC_X86_REG_ESP, esp)
    uc.reg_write(UC_X86_REG_ECX, ecx)
    uc.emu_start(va, ret_magic + 6, count=5_000_000)
    return struct.unpack("<d", bytes(uc.mem_read(result, 8)))[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=r"Z:/ServerSource/Zone00/Zone.exe")
    ap.add_argument("--pdb", default=r"Z:/ServerSource/Zone00/Zone.pdb")
    ap.add_argument("--mob", type=int, default=84)
    ap.add_argument("--ac", type=int, help="override the DEFENDER's AC")
    a = ap.parse_args()

    proj = "C:/Projects/serversource-data"
    def row(tbl, mid):
        d = json.load(open(os.path.join(proj, "data/shn", tbl + ".json"), encoding="utf-8"))
        cols = [c["name"] if isinstance(c, dict) else c for c in d["columns"]]
        for r in d["data"]:
            rr = dict(zip(cols, r)) if isinstance(r, list) else r
            if rr.get("ID") == mid:
                return rr
        return {}
    mi, ms, mw = row("MobInfo", a.mob), row("MobInfoServer", a.mob), row("MobWeapon", a.mob)
    atk_stats = {"Str": ms.get("Str", 0), "Con": ms.get("Con", 0), "Dex": ms.get("Dex", 0),
                 "Int": ms.get("Int", 0), "Men": ms.get("Men", 0),
                 "WCmin": mw.get("MinWC", 0), "WCmax": mw.get("MaxWC", 0),
                 "MAmin": mw.get("MinMA", 0), "MAmax": mw.get("MaxMA", 0),
                 "AC": ms.get("AC", 0), "TB": ms.get("TB", 0), "MR": ms.get("MR", 0),
                 "TH": mw.get("TH", 0), "MH": mw.get("MH", 0), "MB": ms.get("MB", 0)}
    print("attacker mob %d '%s' level %d" % (a.mob, mi.get("Name"), mi.get("Level")))
    print("  PureCharParam:", {k: v for k, v in atk_stats.items() if v})

    emu = Emu(a.exe, a.pdb)
    uc = emu.uc
    att_c, def_c = HEAP + 0x40000, HEAP + 0x42000
    build_container(uc, att_c, atk_stats)
    dstats = dict(atk_stats)
    if a.ac:
        dstats["AC"] = a.ac
    build_container(uc, def_c, dstats)

    lvl = int(mi.get("Level", 1))
    s_par_a = emu.add_stub("att.so_parameter", att_c)
    s_par_d = emu.add_stub("def.so_parameter", def_c)
    s_lvl = emu.add_stub("so_GetLevel", lvl)
    s_hp = emu.add_stub("so_GetHP", int(mi.get("MaxHP", 1)))
    s_mhp = emu.add_stub("so_MaxHP", int(mi.get("MaxHP", 1)))

    # Use the REAL ShineMob vtable, copied out of .rdata, and patch only the four slots we control.
    # A synthetic zero-filled vtable is not safe: roe_MinWC also calls slot +0x938 (so_IsInWeapon and
    # friends), and a zero there is a call to address 0. Copying the real table means every other virtual
    # lands on real code instead of nowhere.
    real_vt = emu.syms.get("??_7ShineMob@ShineObjectClass@@6B@")
    if real_vt is None:
        raise SystemExit("ShineMob vftable not found")
    vt_bytes = bytes(uc.mem_read(real_vt, 0xA00))

    def make_obj(objaddr, vtaddr, parstub):
        uc.mem_write(objaddr, struct.pack("<I", vtaddr))
        uc.mem_write(objaddr + 4, b"\x00" * 0x2000)
        uc.mem_write(vtaddr, vt_bytes)
        for slot, target in ((0x430, parstub), (0x4D8, s_lvl), (0x4E8, s_hp), (0x4F0, s_mhp)):
            uc.mem_write(vtaddr + slot, struct.pack("<I", target))
    att_o, def_o = HEAP + 0x10000, HEAP + 0x14000
    make_obj(att_o, HEAP + 0x20000, s_par_a)
    make_obj(def_o, HEAP + 0x22000, s_par_d)

    arg = HEAP + 0x30000
    uc.mem_write(arg, b"\x00" * 0x40)
    uc.mem_write(arg + 0x00, struct.pack("<I", att_o))
    uc.mem_write(arg + 0x04, struct.pack("<I", def_o))
    uc.mem_write(arg + 0x28, struct.pack("<i", 1000))    # nBMPDamageRate, neutral

    for sym, label in (("?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_MinWC"),
                       ("?roe_MaxWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_MaxWC"),
                       ("?roe_AC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_AC"),
                       ("?roe_MR@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_MR"),
                       ("?roe_TH@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_TH"),
                       ("?roe_TB@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", "roe_TB")):
        try:
            print("  %-14s = %s" % (label, call_double(emu, sym, arg)))
        except Exception as e:
            print("  %-14s FAILED: %s %s" % (label, type(e).__name__, e))

    # The per-subclass virtuals are members of the `roe_normalPY` singleton, so they need a real `this`.
    py = emu.syms.get("?roe_normalPY@@3VRulesOfEngagementNormalPY@@A")
    print("\n  roe_normalPY singleton @ %s" % (hex(py) if py else "NOT FOUND"))
    atkp = defp = None
    if py:
        for sym, label in (("?roe_AttackPower@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z", "AttackPower"),
                           ("?roe_DefendPower@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z", "DefendPower")):
            try:
                v = call_double(emu, sym, arg, ecx=py)
                print("  %-14s = %s" % (label, v))
                if label == "AttackPower":
                    atkp = v
                else:
                    defp = v
            except Exception as e:
                print("  %-14s FAILED: %s %s" % (label, type(e).__name__, e))

    # roe_Damage(arg, double attack, double defend) -- two doubles pushed after the arg pointer.
    if atkp is not None and defp is not None:
        try:
            d = call_damage(emu, arg, atkp, defp, ecx=py)
            print("\n  roe_Damage(attack=%.1f, defend=%.1f) = %s" % (atkp, defp, d))
            print("  cross-check (level+1)*attack/defend  = %.4f" % ((lvl + 1) * atkp / defp))
        except Exception as e:
            print("  roe_Damage FAILED: %s %s" % (type(e).__name__, e))


if __name__ == "__main__":
    main()
