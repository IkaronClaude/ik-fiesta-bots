#!/usr/bin/env python
"""Ground-truth oracle: run the REAL RulesOfEngagement code from Zone.exe on arbitrary inputs.

Batch protocol (one JSON object per line on stdin, one result per line on stdout) so a driver in any
language can fuzz against it without paying process startup per case:

    echo '{"fn":"roe_MinWC","att":{"PureCharParam":{"Str":822,"WCmin":747}},"def":{}}' \
        | python tools/roe_oracle.py --serve

    python tools/roe_oracle.py --probe          # derive each accessor's field dependencies empirically

A case is:
    {"fn": "<roe_MinWC|roe_MaxWC|roe_AC|roe_MR|roe_TH|roe_TB|AttackPower|DefendPower|Damage>",
     "att": {"<Section>": {"<Field>": int, ...}, ...},     # attacker Container, sparse
     "def": {...},                                          # defender Container, sparse
     "level": 61, "hp": 3562, "maxhp": 3562, "rate": 1000, "damagerate": 1000,
     "crirateadd": 0, "empower": 0, "attackloc": 0,
     "attack": <double>, "defend": <double>}                # Damage only
Section is "PureCharParam" | "Item.plus" | "Item.rate" | ... | "Total".
Unspecified `rate` halves default to 1000 (neutral); everything else defaults to 0.

Why an oracle rather than a transcription: the accessors are ~200 instructions of interleaved FPU chains.
Reading them is how you form a hypothesis; running them is how you know. See docs/DAMAGE_FORMULA.md App. C.
"""
import argparse, json, struct, sys, os

IMAGE = 0x400000
STACK = 0x00200000
HEAP = 0x30000000
STUB = 0x70000000
# Data slots for the getter stubs. MUST live outside the stub CODE pages: Unicorn caches
# translated blocks, so a value baked into an instruction can never be changed again.
CONST_SLOTS = 0x78000000
# Data slot for the forced GetLevelCapRate return value (see set_level_gap_rate).
LEVELGAP_SLOT = 0x78001000
BLOCK = 0xCC
CONTAINER_SIZE = 0x0E00

SECTION_BASE = {"PureCharParam": 0x0000, "Item": 0x00CC, "ItemPowerRate": 0x0264, "Upgrade": 0x03FC,
                "WeaponTitle": 0x0594, "PassiveSkill": 0x072C, "AbnormalState": 0x08C4,
                "LastTune": 0x0A5C, "Total": 0x0BF4}
PAIRED = {"Item", "ItemPowerRate", "Upgrade", "WeaponTitle", "PassiveSkill", "AbnormalState", "LastTune"}
FIELDS = ["Str", "Con", "Dex", "Int", "Men", "WCmin", "WCmax", "AC", "TH", "TB", "MAmin", "MAmax",
          "MR", "MH", "MB", "AbsoluteAttack", "AbsoluteDefend", "AbsoluteHit", "AbsoluteBlock",
          "MoveSpeed", "HPRecover", "SPRecover", "CastingTime", "Critical", "PhisycalWeaponMastery",
          "MagicalWeaponMastery", "ShieldAC", "HitRate", "EvaRate", "MACri", "CriDam", "MagCriDam",
          "CriDamRate", "MagCriDamRate", "AttSpeed", "MaxHP", "MaxHP_2", "MaxSP", "HPAbsorption_Hitted",
          "SPAbsorption_Hitted", "HPAbsorption_Hit", "SPAbsorption_Hit", "CriticalTB", "RegistNone",
          "ResistPoison", "ResistDeaseas", "ResistCurse", "ResistMoveSpdDown", "ResistGTI",
          "MaxLP", "LPRecover"]
FIDX = {n: i * 4 for i, n in enumerate(FIELDS)}

SYM = {
    "roe_MinWC": ("?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_MaxWC": ("?roe_MaxWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_MinMA": ("?roe_MinMA@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_MaxMA": ("?roe_MaxMA@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_AC": ("?roe_AC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_MR": ("?roe_MR@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_TH": ("?roe_TH@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "roe_TB": ("?roe_TB@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", None),
    "AttackPower": ("?roe_AttackPower@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z", "normalPY"),
    "DefendPower": ("?roe_DefendPower@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z", "normalPY"),
    "Damage": ("?roe_Damage@RulesOfEngagement@@MAENPAUEngageArgument@@NN@Z", "normalPY"),
    # The TOP-LEVEL entry: applies AttackPower/DefendPower/roe_Damage, then the DamageByAngle multiplier,
    # the level gap, crit/block/miss -- and returns the final INTEGER damage. This is the whole pipeline.
    "CalcDamage": ("?roe_CalcDamage@RulesOfEngagement@@UAEHPAUEngageArgument@@@Z", "normalPY"),
    "AttackPowerCalcDamage": ("?roe_AttackPowerCalcDamage@RulesOfEngagement@@QAEHPAUEngageArgument@@HE@Z", "normalPY"),
}
INT_RETURNING = {"CalcDamage", "AttackPowerCalcDamage"}

# DamageByAngle::DamageTable is uint16[91]. The argument is a DIRECTION-UNIT delta, NOT degrees: one unit
# is 2 degrees (ddt_Initialize builds its table with atan(...) * 90 / PI, where degrees would be * 180 / PI),
# so a full turn is 180 units and the 0..90 index spans 0..180 degrees.
#     index(units) = abs(((abs(units) % 180 + 90) % 180) - 90)
# Confirmed by running the real operator[] against an identity table:
#     0 units =    0 deg -> index  0   attacked from the FRONT
#    45 units =   90 deg -> index 45   from the SIDE
#    90 units =  180 deg -> index 90   from BEHIND, the largest multiplier
# Reading the argument as degrees folds 180 to index 0 and makes a backstab read as a frontal hit.
ANGLE_ENTRIES = 91


def angle_index(angle):
    return abs(((abs(int(angle)) + 90) % 180) - 90)


def publics(pdb, segrva):
    out = {}
    import re
    for m in re.finditer(rb"[?_][ -~]{4,220}", pdb):
        i = m.start()
        if i < 14 or struct.unpack_from("<H", pdb, i - 12)[0] != 0x110E:
            continue
        off, seg = struct.unpack_from("<IH", pdb, i - 6)
        if seg in segrva:
            out.setdefault(pdb[i:pdb.find(b"\x00", i)].decode("latin-1"), IMAGE + segrva[seg] + off)
    return out


class Oracle:
    def __init__(self, exe=r"Z:/ServerSource/Zone00/Zone.exe", pdb=r"Z:/ServerSource/Zone00/Zone.pdb"):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_MEM_UNMAPPED
        import pefile
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        pe = pefile.PE(exe, fast_load=True)
        # fast_load skips the data directories, so DIRECTORY_ENTRY_IMPORT would be absent and the IAT
        # stubbing below would silently no-op (the failure mode that sent CalcDamage to 0x34B69A).
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT']])
        data = open(exe, "rb").read()
        self.uc.mem_map(IMAGE, (pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF)
        self.uc.mem_write(IMAGE, data[:pe.OPTIONAL_HEADER.SizeOfHeaders])
        for s in pe.sections:
            raw = data[s.PointerToRawData:s.PointerToRawData + s.SizeOfRawData]
            if raw:
                self.uc.mem_write(IMAGE + s.VirtualAddress, raw)
        self.uc.mem_map(STACK, 0x200000)
        self.uc.mem_map(HEAP, 0x100000)
        self.uc.mem_map(STUB, 0x8000)
        segrva = {i + 1: sec.VirtualAddress for i, sec in enumerate(pe.sections)}
        self.syms = publics(open(pdb, "rb").read(), segrva)
        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED, lambda uc, a, addr, sz, v, u: (self._map(addr), True)[1])
        # x87 PRECISION CONTROL. Unicorn boots the FPU with FPCW = 0x0000, whose PC field is 00 =
        # 24-bit SINGLE precision, so every fmul/fdiv here was rounding to float32. A real MSVC process
        # runs 0x027F (all exceptions masked, PC = 10 = 53-bit double). The difference is ~1e-7 relative --
        # small enough to look like a harmless last-digit wobble, and it was the ONLY thing separating the
        # C# port from the oracle on 4 of 120 fuzz cases. The accessors contain no `fstp dword` (no float
        # temporaries), which is what proved the rounding came from the emulator and not from the game.
        try:
            from unicorn.x86_const import UC_X86_REG_FPCW
            self.uc.reg_write(UC_X86_REG_FPCW, 0x027F)
        except Exception:
            pass
        for n in (r"?pr_Entrance@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z",
                  r"?pr_Exit@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z"):
            if n in self.syms:
                self.uc.mem_write(self.syms[n], b"\xC2\x04\x00")
        # CRT per-thread data. __getptd_noexit walks TlsGetValue -> DecodePointer -> `call eax` on an
        # ENCODED function pointer held in a global. That global is zero here, so the decode yields 0 and the
        # call lands on null. There is no per-thread state worth emulating for a pure arithmetic function, so
        # return a zeroed _ptiddata block directly. The auto-mapper covers whatever field the caller reads.
        self.ptd = HEAP + 0x70000
        self.uc.mem_write(self.ptd, b"\x00" * 0x800)
        for n in ("__getptd_noexit", "__getptd"):
            if n in self.syms:
                self.uc.mem_write(self.syms[n], b"\xB8" + struct.pack("<I", self.ptd) + b"\xC3")
        # roe_CriticalStun APPLIES a stun abnormal-state (so_AbnormalState_Set -> asl_AbstateSet). It is a
        # `void` SIDE EFFECT on world state, not part of the damage number, and our synthetic object is not a
        # registered world object -- so it asserts, and the CRT assert path runs malloc/MessageBoxW/ExitProcess
        # and takes the emulator with it. Neutralised (`__thiscall void f(arg)` -> `ret 4`). The returned
        # damage is unaffected; what is lost is only whether a stun would have been inflicted.
        for n in (r"?roe_CriticalStun@RulesOfEngagement@@QAEXPAUEngageArgument@@@Z",):
            if n in self.syms:
                self.uc.mem_write(self.syms[n], b"\xC2\x04\x00")
        # LevelGap_*::GetLevelCapRate reads the ITableBase `ms_pkTable` singleton, which is null here because
        # nothing loaded the SHN. It therefore returned 0, roe_LevelGapDamageRevision multiplied the damage by
        # 0/1000, and CalcDamage clamped the result to 1 -- for EVERY input, which is exactly the degenerate
        # behaviour observed. The tables are DATA (DamageLvGapEVP/PVE/PVP), so like the angle table they are an
        # oracle INPUT rather than something to emulate. `SAHHH` = static, __cdecl, caller-cleaned -> plain ret.
        self._levelgap_syms = [n for n in (
            r"?GetLevelCapRate@LevelGap_Monster_to_Player@@SAHHH@Z",
            r"?GetLevelCapRate@LevelGap_Player_to_Monster@@SAHHH@Z",
            r"?GetLevelCapRate@LevelGap_Player_to_Player@@SAHHH@Z") if n in self.syms]
        self.set_level_gap_rate(1000)
        self.normalPY = self.syms.get("?roe_normalPY@@3VRulesOfEngagementNormalPY@@A", 0)
        self.att_c, self.def_c = HEAP + 0x40000, HEAP + 0x42000
        self.att_o, self.def_o = HEAP + 0x10000, HEAP + 0x14000
        self.arg = HEAP + 0x30000
        vt = bytes(self.uc.mem_read(self.syms["??_7ShineMob@ShineObjectClass@@6B@"], 0xA00))
        self.stub_n = 0
        self._saved = {}
        self.raw_n = 0
        self._const_slot = {}
        self._stub_imports(pe)
        self.s_par_a = self._stub(self.att_c)
        self.s_par_d = self._stub(self.def_c)
        # SEPARATE level stubs per object. Each combatant gets its own vtable copy below, so attacker and
        # defender levels can differ -- which they must, or roe_LevelGapDamageRevision always sees a gap of
        # zero and the level-gap path can never be fuzzed.
        self.s_lvl = self._stub(61)          # attacker
        self.s_lvl_def = self._stub(61)      # defender
        # so_GetObjectType, overridable per object. roe_LevelGapDamageRevision DISPATCHES ON IT:
        # attacker 2 (player) + defender 5 (monster) selects LevelGap_Player_to_Monster, and any other
        # combination returns the damage untouched. Both objects use the ShineMob vtable, so without this
        # the level-gap path was unreachable and the rate silently did nothing.
        self.s_type_a = self._stub(5)
        self.s_type_d = self._stub(5)
        self.s_hp = self._stub(1)
        self.s_mhp = self._stub(1)
        # vtable +0xD34 = so_ply_JobChangeDamageUp(ShineObject* attacker, int damage) -> int.
        # It is a damage MODIFIER hook (a job-change bonus), not the damage application, and it is a
        # player concept -- for a mob defender it is identity. `mov eax,[esp+8]; ret 8` returns the
        # damage argument unchanged (__thiscall: this in ecx, then attacker and damage on the stack).
        self.s_jobdmg = self._stub_raw(bytes([0x8B, 0x44, 0x24, 0x08, 0xC2, 0x08, 0x00]))
        for o, v, par, lvl, oty in ((self.att_o, HEAP + 0x20000, self.s_par_a, self.s_lvl, self.s_type_a),
                               (self.def_o, HEAP + 0x22000, self.s_par_d, self.s_lvl_def, self.s_type_d)):
            self.uc.mem_write(o, struct.pack("<I", v))
            self.uc.mem_write(o + 4, b"\x00" * 0x1000)
            self.uc.mem_write(v, vt)
            for slot, t in ((0x430, par), (0x4D8, lvl), (0x4D0, oty), (0x4E8, self.s_hp), (0x4F0, self.s_mhp),
                            (0xD2C, self.s_jobdmg), (0xD34, self.s_jobdmg)):
                self.uc.mem_write(v + slot, struct.pack("<I", t))

    def _stub_imports(self, pe):
        """Point every IAT entry at a stub, so a Win32 call cannot jump into unresolved garbage.

        roe_CalcDamage reaches KERNEL32!GetSystemTimeAsFileTime (clock, for RNG seeding). Without an import
        table the thunk holds the on-disk value and control lands at 0x34B69A. Each stub is stdcall-correct:
        `xor eax,eax; ret <argbytes>`, with argbytes from a small table and 0 assumed otherwise. APIs that
        write through an out-pointer get a bespoke stub that zeroes the buffer first."""
        ARGS = {"GetSystemTimeAsFileTime": 4, "QueryPerformanceCounter": 4, "QueryPerformanceFrequency": 4,
                "GetTickCount": 0, "GetCurrentThreadId": 0, "GetCurrentProcessId": 0,
                "InitializeCriticalSection": 4, "EnterCriticalSection": 4, "LeaveCriticalSection": 4,
                "DeleteCriticalSection": 4, "GetLastError": 0, "SetLastError": 4,
                # These four are the ones roe_CalcDamage actually reaches. Getting an arg count wrong here
                # does not fault at the call -- it silently leaks stack, and a LATER `ret` pops the wrong
                # word. That is what returned to address 0 with the crit-stun path half finished.
                "TlsGetValue": 4, "TlsSetValue": 8, "DecodePointer": 4, "EncodePointer": 4}
        OUTPTR = {"GetSystemTimeAsFileTime": 8, "QueryPerformanceCounter": 8, "QueryPerformanceFrequency": 8}
        # DecodePointer/EncodePointer must be IDENTITY, not 0: the CRT round-trips real function pointers
        # through them, and returning 0 turns the next indirect call into a jump to null.
        IDENTITY = {"DecodePointer", "EncodePointer"}
        RETVAL = {"TlsSetValue": 1}
        self.import_names = {}
        try:
            dirs = pe.DIRECTORY_ENTRY_IMPORT
        except AttributeError:
            return
        for d in dirs:
            for imp in d.imports:
                nm = (imp.name or b"").decode("latin-1") or ("ord%d" % imp.ordinal)
                self.import_names[imp.address] = nm
                argb = ARGS.get(nm, 0)
                if nm in IDENTITY:
                    code = b"\x8B\x44\x24\x04" + b"\xC2" + struct.pack("<H", argb)   # mov eax,[esp+4]; ret n
                elif nm in OUTPTR:
                    n = OUTPTR[nm]
                    code = b"\x8B\x44\x24\x04"                      # mov eax,[esp+4]  (the out pointer)
                    for k in range(0, n, 4):
                        code += b"\xC7\x40" + bytes([k]) + b"\x00\x00\x00\x00"   # mov [eax+k],0
                    code += b"\x31\xC0"                             # xor eax,eax
                    code += b"\xC2" + struct.pack("<H", argb)
                else:
                    rv = RETVAL.get(nm, 0)
                    code = (b"\xB8" + struct.pack("<I", rv)) + (b"\xC2" + struct.pack("<H", argb) if argb else b"\xC3")
                addr = self._stub_raw(code)
                self.uc.mem_write(imp.address, struct.pack("<I", addr))

    def _stub_raw(self, code):
        addr = STUB + 0x1000 + self.raw_n * 0x20
        self.raw_n += 1
        self.uc.mem_write(addr, code)
        return addr

    def _map(self, addr):
        try:
            self.uc.mem_map(addr & ~0xFFF, 0x1000)
        except Exception:
            pass

    def _stub(self, value):
        """A getter stub that reads its value from a DATA slot: `mov eax,[slot]; ret`.

        WARNING: the first version emitted `mov eax,<imm32>; ret` and `_set_const` rewrote the immediate.
        That is the Unicorn code-cache trap -- once a block has been translated, later writes to its BYTES
        are invisible -- and it silently pinned the character level for the whole process. Every
        `CalcDamage` case came back computed at level 1 no matter what the caller asked for, which showed
        up in the fuzz as C# being too large by exactly (level+1)/2 and read convincingly like a C# bug.
        The same trap had already been found and documented for the `rb_largerandom` roll patch; it was
        not generalised to here. Keep the CODE fixed and vary DATA -- for every stub, without exception."""
        addr = STUB + self.stub_n * 0x10
        slot = CONST_SLOTS + self.stub_n * 4
        self.stub_n += 1
        self._map(slot)
        self.uc.mem_write(slot, struct.pack("<I", value & 0xFFFFFFFF))
        self.uc.mem_write(addr, bytes([0xA1]) + struct.pack("<I", slot) + bytes([0xC3]))
        self._const_slot[addr] = slot
        return addr

    def _set_const(self, stub_addr, value):
        self.uc.mem_write(self._const_slot[stub_addr], struct.pack("<I", value & 0xFFFFFFFF))

    def _container(self, addr, spec):
        self.uc.mem_write(addr, b"\x00" * CONTAINER_SIZE)
        for name, base in SECTION_BASE.items():           # neutral permille in every rate half
            if name in PAIRED:
                self.uc.mem_write(addr + base + BLOCK, struct.pack("<i", 1000) * len(FIELDS))
        for sect, fields in (spec or {}).items():
            if "." in sect:
                nm, half = sect.split(".", 1)
            else:
                nm, half = sect, "plus"
            base = SECTION_BASE[nm] + (BLOCK if (nm in PAIRED and half == "rate") else 0)
            for f, v in fields.items():
                self.uc.mem_write(addr + base + FIDX[f], struct.pack("<i", int(v)))

    def call(self, case):
        from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_ECX
        uc = self.uc
        fn = case["fn"]
        sym, this = SYM[fn]
        va = self.syms[sym]
        self._container(self.att_c, case.get("att"))
        self._container(self.def_c, case.get("def"))
        self._set_const(self.s_lvl, int(case.get("level", 61)))
        self._set_const(self.s_lvl_def, int(case.get("deflevel", case.get("level", 61))))
        self._set_const(self.s_type_a, int(case.get("atttype", 5)))
        self._set_const(self.s_type_d, int(case.get("deftype", 5)))
        if "levelgaprate" in case:
            self.set_level_gap_rate(case["levelgaprate"])
        self._set_const(self.s_hp, int(case.get("hp", 1)))
        self._set_const(self.s_mhp, int(case.get("maxhp", 1)))
        uc.mem_write(self.arg, b"\x00" * 0x40)
        uc.mem_write(self.arg + 0x00, struct.pack("<I", self.att_o))
        uc.mem_write(self.arg + 0x04, struct.pack("<I", self.def_o))
        uc.mem_write(self.arg + 0x28, struct.pack("<i", int(case.get("rate", 1000))))
        # ⚠️ damagerate (+0x1C) DEFAULTS TO 1000, NOT 0. Leaving it zero makes roe_CalcDamage compute a raw
        # damage of 0, which the `test eax,eax / jg` at 0x5061CB then clamps to 1 -- for every input, which is
        # exactly the degenerate all-1s behaviour that made CalcDamage look broken. It is a permille.
        uc.mem_write(self.arg + 0x1C, struct.pack("<i", int(case.get("damagerate", 1000))))
        uc.mem_write(self.arg + 0x20, struct.pack("<i", int(case.get("crirateadd", 0))))
        uc.mem_write(self.arg + 0x0C, struct.pack("<h", int(case.get("empower", 0))))
        uc.mem_write(self.arg + 0x18, struct.pack("<i", int(case.get("attackloc", 0))))
        for k in ("crit", "hit", "block", "critstun"):
            if k in case:
                self.set_override(k, case[k])
        if "immune" in case:
            self.set_immune(case["immune"])
        if "roll" in case:
            self.set_roll_permille(case["roll"])
        result, ret = STUB + 0xE80, STUB + 0xE00
        uc.mem_write(ret, b"\xDD\x1D" + struct.pack("<I", result))
        uc.mem_write(result, b"\x00" * 8)
        esp = STACK + 0x100000 - 0x80
        frame = struct.pack("<I", ret) + struct.pack("<I", self.arg)
        if fn == "Damage":
            frame += struct.pack("<d", float(case["attack"])) + struct.pack("<d", float(case["defend"]))
        uc.mem_write(esp, frame)
        uc.reg_write(UC_X86_REG_ESP, esp)
        uc.reg_write(UC_X86_REG_ECX, self.normalPY if this else 0)
        if fn in INT_RETURNING:
            # Returns int in EAX, so no fstp thunk -- stop at a bare `ret` landing pad instead.
            from unicorn.x86_const import UC_X86_REG_EAX
            pad = STUB + 0xD00
            uc.mem_write(pad, b"\xC3")
            uc.mem_write(esp, struct.pack("<I", pad) + frame[4:])
            uc.emu_start(va, pad, count=5_000_000)
            v = uc.reg_read(UC_X86_REG_EAX)
            return v - (1 << 32) if v >= (1 << 31) else v
        uc.emu_start(va, ret + 6, count=5_000_000)
        return struct.unpack("<d", bytes(uc.mem_read(result, 8)))[0]

    # ---- deterministic overrides -------------------------------------------------------------------
    # The engine is random by nature: the caller draws from WELL512 and compares against a RATE returned by
    # roe_CriticalRate / roe_HitRate / roe_ShieldBlock. So forcing the *rate* forces the decision without
    # touching the RNG -- a huge rate always wins the comparison, a zero rate always loses. Each override is
    # tri-state: True = always, False = never, None = leave the real code alone and let the RNG decide.
    # The attack roll is separate: it comes from well512_GetRandom(n), so `roll_permille` rewrites that to a
    # fixed fraction of its range (0 = MinWC, 1000 = MaxWC), leaving every other draw irrelevant because the
    # rates above already decided those branches.
    RATE_SYMS = {
        "crit": r"?roe_CriticalRate@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z",
        "hit": r"?roe_HitRate@RulesOfEngagementNormalPY@@UAENPAUEngageArgument@@@Z",
        "block": r"?roe_ShieldBlock@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z",
        "critstun": r"?roe_CriticalStunRate@RulesOfEngagement@@MAENPAUEngageArgument@@@Z",
    }
    IMMUNE_SYM = r"?roe_IsDamageImmune@RulesOfEngagementNormalPY@@MAEEPAUEngageArgument@@@Z"
    # The attack roll is NOT any well512 overload. NormalPY::roe_AttackPower computes (MaxWC - MinWC),
    # converts it with __ftol2_sse, calls `RandomBox::rb_largerandom(int)` at 0x63CCC0, and adds MinWC back.
    # Patching the well512 overloads changed nothing for exactly this reason -- they feed rb_largerandom.
    ROLL_SYM = r"?rb_largerandom@RandomBox@@QAEHH@Z"

    def _orig(self, sym, n=16):
        if sym not in self._saved:
            self._saved[sym] = bytes(self.uc.mem_read(self.syms[sym], n))
        return self._saved[sym]

    def _restore(self, sym):
        if sym in self._saved:
            self.uc.mem_write(self.syms[sym], self._saved[sym])

    def set_override(self, what, value):
        """what in {crit, hit, block, critstun}; value True=always, False=never, None=random."""
        sym = self.RATE_SYMS[what]
        if sym not in self.syms:
            return False
        self._orig(sym)
        if value is None:
            self._restore(sym)
            return True
        # `fld qword ptr [const]; ret 4` -- a double-returning __thiscall with one stack arg.
        slot = HEAP + 0x7A000 + (list(self.RATE_SYMS).index(what) * 8)
        self.uc.mem_write(slot, struct.pack("<d", 1e18 if value else 0.0))
        self.uc.mem_write(self.syms[sym], b"\xDD\x05" + struct.pack("<I", slot) + b"\xC2\x04\x00")
        return True

    def set_immune(self, value):
        """True = always immune (damage suppressed), False = never, None = real code."""
        sym = self.IMMUNE_SYM
        if sym not in self.syms:
            return False
        self._orig(sym)
        if value is None:
            self._restore(sym)
        else:
            self.uc.mem_write(self.syms[sym], b"\xB8" + struct.pack("<I", 1 if value else 0) + b"\xC2\x04\x00")
        return True

    def set_roll_permille(self, permille):
        """Force the attack roll to a fixed fraction of its range. None restores the real RNG draw.

        well512_GetRandom(unsigned n) returns 0..n; rewriting it to `n * permille / 1000` makes AttackPower
        land on MinWC at 0 and MaxWC at 1000, deterministically and without disturbing the generator."""
        sym = self.ROLL_SYM
        if sym not in self.syms:
            return False
        self._orig(sym, 24)
        if permille is None:
            self._restore(sym)
            return True
        # ⚠️ THE PERMILLE MUST LIVE IN DATA, NOT AS AN IMMEDIATE IN THE CODE.
        # Unicorn caches translated blocks. Once rb_largerandom has been executed, rewriting its bytes has
        # no effect -- the stale translation keeps running. That is why an earlier version "worked" only on
        # the very first permille of a process and then returned MinWC forever, while the crit override
        # (which rewrites a *double slot* and leaves the code identical) worked every time. Writing the code
        # once and varying only the operands sidesteps the cache entirely.
        slot = HEAP + 0x7C000
        self.uc.mem_write(slot, struct.pack("<II", int(permille) & 0xFFFFFFFF, 1000))
        # 64-BIT signed multiply, then signed divide. Two earlier versions of this patch were wrong and
        # each cost a round of investigation, because a broken patch looks exactly like a broken port:
        #   * xor edx,edx + div (UNSIGNED) turned a negative range into ~4.3e6.
        #   * imul eax,[slot] (two-operand, 32-BIT result) silently TRUNCATED the product. With
        #     MaxWC < MinWC the range reached -1.4e10, the 32-bit product wrapped, and the oracle
        #     reported an attack power ~275 million short of the port. The PORT was right.
        # One-operand imul puts the full 64-bit product in edx:eax, which idiv then consumes -- so no
        # intermediate is ever truncated. This matches the C# side, which does the same in `long`.
        code = (bytes([0x8B, 0x44, 0x24, 0x04])                  # mov eax,[esp+4]   ; n = MaxWC-MinWC
                + bytes([0xF7, 0x2D]) + struct.pack("<I", slot)      # imul dword [slot]   ; edx:eax = n*pm
                + bytes([0xF7, 0x3D]) + struct.pack("<I", slot + 4)  # idiv dword [slot+4] ; eax = /1000
                + bytes([0xC2, 0x04, 0x00]))                         # ret 4
        if bytes(self.uc.mem_read(self.syms[sym], len(code))) != code:
            self.uc.mem_write(self.syms[sym], code)
            try:
                self.uc.ctl_remove_cache(self.syms[sym], self.syms[sym] + len(code))
            except Exception:
                pass
        return True

    def set_seed(self, seed):
        """Seed the server's own WELL512 generator, so crit/hit/roll outcomes are controllable and repeatable.

        `cWell512Random::well512_InitState(unsigned int* state)` takes a 16-word seed array (WELL512's STATE),
        and the singleton is `rb_well512random`. Without this the state is all zeroes, which makes the harness
        deterministic in a *degenerate* way: crit fires on every call and the attack roll always lands exactly
        on MinWC. Seeding is what lets a fuzzer reach the other branches."""
        init = self.syms.get(r"?well512_InitState@cWell512Random@@QAEXPAI@Z")
        glob = self.syms.get(r"?rb_well512random@@3VcWell512Random@@A")
        if init is None or glob is None:
            return False
        from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_ECX
        buf = HEAP + 0x78000
        x = seed & 0xFFFFFFFF
        words = []
        for _ in range(16):                       # xorshift expansion; any spread of bits will do
            x ^= (x << 13) & 0xFFFFFFFF
            x ^= x >> 17
            x ^= (x << 5) & 0xFFFFFFFF
            words.append(x or 0x9E3779B9)
        self.uc.mem_write(buf, b"".join(struct.pack("<I", w) for w in words))
        pad = STUB + 0xD00
        self.uc.mem_write(pad, b"\xC3")
        esp = STACK + 0x100000 - 0x100
        self.uc.mem_write(esp, struct.pack("<II", pad, buf))
        self.uc.reg_write(UC_X86_REG_ESP, esp)
        self.uc.reg_write(UC_X86_REG_ECX, glob)
        self.uc.emu_start(init, pad, count=200000)
        return True

    def set_level_gap_rate(self, rate):
        """Force GetLevelCapRate to a constant permille (1000 = no change).

        For Monster -> Player this is exactly right for every gap: DamageLvGapEVP is flat 1000 from -150 to
        +150. For Player -> Monster it is only right at gap >= 0; use the DamageLvGapPVE value (up to 1500)
        when modelling the bot's outgoing damage."""
        # Read from a DATA SLOT. The first version baked the rate into a `mov eax,<imm32>` and rewrote the
        # immediate per call -- the Unicorn code-cache trap, for the third time in this file: once the
        # function has executed, later writes to its BYTES do nothing, so every case after the first would
        # silently have used the first case's rate.
        self.level_gap_rate = int(rate)
        self._map(LEVELGAP_SLOT)
        self.uc.mem_write(LEVELGAP_SLOT, struct.pack('<i', int(rate)))
        code = bytes([0xA1]) + struct.pack('<I', LEVELGAP_SLOT) + bytes([0xC3])   # mov eax,[slot] ; ret
        for n in self._levelgap_syms:
            if bytes(self.uc.mem_read(self.syms[n], len(code))) != code:
                self.uc.mem_write(self.syms[n], code)

    def set_angle_table(self, rates_mob=None, rates_ply=None):
        """Fill DamageByAngle's uint16[91] globals. Index is angle_index(angle), NOT the raw angle.

        These are loaded from a shinetable at server start by dt_Load (file I/O we do not emulate), so the
        oracle takes them as an INPUT -- exactly like a MobWeapon row. Default is neutral 1000 everywhere."""
        for sym, rates in ((r"?damagebyangle_Mob@@3VDamageTable@DamageByAngle@@A", rates_mob),
                           (r"?damagebyangle_Ply@@3VDamageTable@DamageByAngle@@A", rates_ply)):
            addr = self.syms.get(sym)
            if addr is None:
                continue
            vals = rates if rates else [1000] * ANGLE_ENTRIES
            self.uc.mem_write(addr, b"".join(struct.pack("<H", int(v) & 0xFFFF) for v in vals[:ANGLE_ENTRIES]))


def probe(o):
    """Empirically derive which container fields each accessor depends on, and with what weight."""
    print("Deriving field dependencies by single-field perturbation (value 1000, baseline all-zero).")
    for fn in ("roe_MinWC", "roe_MaxWC", "roe_AC", "roe_MR", "roe_TH", "roe_TB", "roe_MinMA", "roe_MaxMA"):
        base = o.call({"fn": fn, "att": {}, "def": {}})
        deps = []
        for sect in SECTION_BASE:
            halves = ["plus", "rate"] if sect in PAIRED else ["plus"]
            for half in halves:
                for f in FIELDS:
                    key = "%s.%s" % (sect, half) if sect in PAIRED else sect
                    v = o.call({"fn": fn, "att": {key: {f: 1000}}, "def": {key: {f: 1000}}})
                    if abs(v - base) > 1e-9:
                        deps.append(("%s.%s" % (key, f), v - base, v))
        print("\n%s  baseline=%g" % (fn, base))
        for name, delta, val in deps:
            print("   %-42s delta=%+12.4f  value=%g" % (name, delta, val))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--serve", action="store_true")
    ap.add_argument("--probe", action="store_true")
    a = ap.parse_args()
    o = Oracle()
    if a.probe:
        probe(o)
        return
    if a.serve:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                print(json.dumps({"ok": True, "v": o.call(json.loads(line))}), flush=True)
            except Exception as e:
                print(json.dumps({"ok": False, "err": "%s: %s" % (type(e).__name__, e)}), flush=True)
        return
    print(o.call({"fn": "roe_MinWC", "att": {"PureCharParam": {"Str": 822, "WCmin": 747}}}))


if __name__ == "__main__":
    main()
