# Fiesta damage formulas — read out of the server binary

**Status of every claim in this file is labelled.** `[BIN]` = disassembled from `Z:/ServerSource/Zone00/Zone.exe`
via `Zone.pdb`. `[TBL]` = read from a server data table. `[WIRE]` = measured from a packet capture.
`[OPEN]` = not established — do not build on it.

Reproduce anything here with:

```bash
python tools/pdb_disasm.py --exe Z:/ServerSource/Zone00/Zone.exe --pdb Z:/ServerSource/Zone00/Zone.pdb \
    --find roe_                      # list every RulesOfEngagement symbol + address
python tools/pdb_disasm.py ... --sym "?roe_Damage@RulesOfEngagement@@MAENPAUEngageArgument@@NN@Z"
python tools/roe_dump.py             # arithmetic of ALL roe_ methods, calls + constants resolved
python tools/damage_fit.py --pcap Z:/Damage.pcapng    # the wire side
```

> **Why this file exists.** Damage was previously *fitted* from captures, which produced
> `damage = K/(DEF - 141)` — a 2.65% residual and a constant that means nothing. The formula is not in any
> data table: it is code, in `RulesOfEngagement`. Reading it is both easier and correct. The fitted version
> was wrong (see [Where the fit went wrong](#where-the-fit-went-wrong)).

---

## 1. The engine: `RulesOfEngagement` [BIN]

One base class plus a subclass per attack kind. The subclass decides *how* attack power, defence power, hit,
crit and block are computed; the base decides how they combine.

| Class | Used for |
|---|---|
| `RulesOfEngagementNormalPY` | normal **physical** attack (a mob's auto-attack, a melee swing) |
| `RulesOfEngagementNormalMA` | normal **magical** attack |
| `RulesOfEngagementPhisycalSkill` | physical **skill** |
| `RulesOfEngagementMagicalSkill` | magical **skill** |
| `RuleOfEngagementHealAttack` (note: `Rule`, not `Rules`) | heal-as-damage (undead) — overrides `roe_CalcDamage` |
| `RulesOfEngagementCureSkill` | cures — overrides hit rate only |
| `RulesOfEngagementAlwaysHit` | overrides `roe_HitRate` |
| `RulesOfEngagementAlwaysCritical` | overrides `roe_CriticalRate` / `roe_CriticalStunRate` |

Singletons: `roe_normalPY`, `roe_normalMA`, `roe_physical`, `roe_magical`, `roe_alwaysHealAttack`,
`roe_cure`, `roe_always`, `roe_alwaysCritical`.

Virtual surface (all take `EngageArgument*`):

```
roe_AttackPower   roe_DefendPower   roe_Damage(arg, attack, defend)
roe_HitRate       roe_CriticalRate  roe_CriticalStunRate   roe_ShieldBlock
roe_AC  roe_MR  roe_TH  roe_TB  roe_MinWC  roe_MaxWC  roe_MinMA  roe_MaxMA
roe_LevelGapDamageRevision   roe_IsDamageImmune   roe_IsDamageSkill
roe_FreeStateAttackPower  roe_FreeStateDefendPower  roe_FreeStatHitRate  roe_FreeStatCriRate
roe_CalcDamage   roe_AttackPowerCalcDamage   roe_GetAttackPower   roe_CriticalStun
roe_CalcHealPower   roe_CalcHealPower_NoCri
```

`EngageArgument` is the packet of context; `+0x00` is the attacker object, `+0x04` the defender,
`+0x28` an int rate (see below). Members named in the PDB include `attack`, `defend`, `iscritical`.

---

## 2. The core: `RulesOfEngagement::roe_Damage` [BIN]

Signature (demangled): `protected: virtual double RulesOfEngagement::roe_Damage(EngageArgument*, double attack, double defend)`

Disassembled at VA `0x500510`, arithmetic path only (the first ~140 instructions are a debug-log block gated
on a global flag and jumped over in production):

```
00500860  fild  dword ptr [edi + 0x28]     ; arg->rate            (int)
00500863  fmul  qword ptr [ebp - 0x218]    ; * attack
00500869  fdiv  CONST(1000)                ; / 1000.0
0050086F  fst   qword ptr [ebp - 0x218]
00500875  fldz / fcompp                    ; if (v <= 0)
00500880  fld1 / fstp [ebp-0x218]          ;     v = 1.0
00500888  call  <attacker vtable + 0x4D8>  ; -> X   (returns a BYTE)
005008A3  fadd  CONST(1)                   ; X + 1
005008A9  fmul  qword ptr [ebp - 0x218]    ; * v
005008AF  fdiv  qword ptr [ebp - 0x228]    ; / defend
```

which is exactly:

```c
double v = (arg->rate * attack) / 1000.0;
if (v <= 0.0) v = 1.0;
damage = ((X + 1.0) * v) / defend;
```

**The only literals in the whole function are `1000.0` and `1.0`.** There is no additive defence offset, no
tuning constant.

`RulesOfEngagementNormalPY::roe_Damage` and `...NormalMA::roe_Damage` both just call this base and then log.

- `arg->rate` is a **permille** — the `/1000` normalises it. It carries the level-gap `DamageRate`
  (§5), which is why every one of those tables stores `1000` for "no change".
- **`X` is the attacker's LEVEL** [BIN, proven]. Vtable slot `+0x4D8` was resolved by reading the function
  pointer out of every `??_7Shine*` vftable in `.rdata`: for `ShineMob`, `ShinePlayer`, `ShineNPC`,
  `ShinePet`, `ShineBandit`, `ShineMover` and `ShineServant` it is `so_GetLevel` (mangled return type `E` =
  `unsigned char`, matching the `movzx eax, al` at the call site).

So the complete normal-attack damage formula is:

```
damage = (attackerLevel + 1) * AttackPower * (levelGapRate / 1000) / DefendPower
```

then multiplied by the **angle** rate (§5) and adjusted by crit / block.

---

## 3. Attack power and defence power [BIN]

### `RulesOfEngagementNormalPY::roe_AttackPower` (VA `0x506660`)

```
call RulesOfEngagement::roe_MinWC   -> lo
call RulesOfEngagement::roe_MaxWC   -> hi
; then, if the attacker object exists:
edi = attacker->so_MaxHP()               ; vtable +0x4F0
ecx = attacker->so_GetHP()               ; vtable +0x4E8
eax = ((MaxHP - HP) * 1000) / MaxHP      ; permille of MISSING HP (so_MaxHP / so_GetHP)
obj = attacker->so_parameter()           ; vtable +0x430 -> Parameter::Container
lo += cbcp_GetValue(obj->PassiveHPDownRateWCMin, eax)   ; +0x0CE0 -- name confirmed by offset
hi += cbcp_GetValue(obj->PassiveHPDownRateWCMax, eax)   ; +0x0CFC
```

So both bounds get a bonus indexed by *how much HP is missing* — the "stronger as it gets hurt"
mechanic (`PassiveHPDownRateWCMin` / `PassiveHPDownRateWCMax` are members of the container, §3b). Then a roll is taken between `lo` and `hi`
(RNG = `cWell512Random::well512_GetRandom`, WELL512).

### `RulesOfEngagementNormalPY::roe_DefendPower` (VA `0x501FA0`)

```
call RulesOfEngagement::roe_AC      -> base
; plus further terms via defender->vtable[0x4F0] ...
```

### `roe_MinWC` / `roe_MaxWC` / `roe_AC` are accumulators, not lookups

`roe_MinWC` (VA `0x4FDBE0`) reads ~12 different offsets off the attacker and folds them together:

```
v  = field[0xCC] + field[0x00]
v *= field[0x990]                  ; multiplicative slots
v *= field[0x7F8]
v *= field[0x330]
v *= field[0xB28] / CONST(...)
v += field[0x3FC] + field[0x594] + field[0x72C] + field[0x8C4] + field[0xA5C]   ; additive slots
```

**This is the single most important consequence for anyone comparing against data files:**
`MobWeapon.MinWC` / `MaxWC` is the *base input*, **not** the final roll range. Buffs, abnormal states,
gear and the missing-resource bonus all layer on top. A measured hit will **not** fall inside the raw
`MinWC..MaxWC` from the table, and it is not supposed to.

### 3a. Every stat accessor is the SAME function shape [BIN]

`roe_AC`, `roe_MR`, `roe_TH`, `roe_TB`, `roe_MinWC`, `roe_MaxWC` are all one pattern — a base plus a
change, four permille multipliers, then a run of additive bonus slots:

```c
stat = (base + change)
     * mul1 * mul2 * mul3 * mul4 / 1000000000000.0     // 1e12 == 1000^4, four permille factors
     + add1 + add2 + add3 + add4 + add5 ...            // flat bonus slots
```

The `1e12` divisor (`CONST @0x6CFE28`) is the tell: **four multiplicative permille modifiers**, exactly
like every other rate in this engine. The slots are the buff / abnormal-state / gear / socket layers.

Field offsets read off each accessor (all fetched via a virtual getter, so the object differs per fetch):

| accessor | base | change | the four multipliers | additive slots |
|---|---|---|---|---|
| `roe_AC` | `+0xD0` | `+0x04` | `0x994, 0x7FC, 0x334, 0xB2C` | … |
| `roe_TH` | `+0xD4` | `+0x08` | `0x998, 0x800, 0x338, 0xB30` | `0x404, 0x59C, 0x734, 0x8CC, 0xA64`, then `+0xEC` |
| `roe_TB` | `+0xD4` | `+0x08` | `0x998, 0x800, 0x338, 0xB30` | `0x404, 0x59C, 0x734, 0x8CC, 0xA64`, then `+0xF0` |
| `roe_MR` | `+0xDC` | `+0x10` | `0x9A0, 0x808, 0x340, 0xB38` | … |
| `roe_MinWC` | `+0xCC` | `+0x00` | `0x990, 0x7F8, 0x330, 0xB28` | `0x3FC, 0x594, 0x72C, 0x8C4, 0xA5C` |

`roe_TH` and `roe_TB` are **byte-identical** through the whole chain and diverge only at the very last
field (`+0xEC` vs `+0xF0`) — aim and block share one accumulator and differ by a single term.

### 3b. What those offsets are reading: `Parameter::Container` [BIN]

Every `call edx` in the accessors is the **same** virtual, re-called per field (the compiler does not cache
it). Resolved by reading the pointer out of each `??_7Shine*` vftable:

| vtable slot | function | returns |
|---|---|---|
| `+0x430` | `ShineMobileObject::so_parameter` / `ShineObject::so_parameter` | **`Parameter::Container const*`** |
| `+0x4E8` | `so_GetHP` (`ShineMob` / `ShinePlayer` / `ShineMover`) | current HP |
| `+0x4F0` | `so_MaxHP` (`ShineMob` / `ShinePlayer` / `ShineMover` / `ShinePet`) | max HP |
| `+0x4D8` | `so_GetLevel` | level |

So the `((max - cur) * 1000) / max` in `roe_AttackPower` (§3) is **permille of MISSING HP** — confirmed,
not a generic resource. And `arg+0x00` / `arg+0x04` are attacker / defender: `roe_AC` reads `[esi+4]`,
the **defender**.

`Parameter::Container` is not a flat stat block — it holds **parallel blocks of the same shape**, one per
source of modification, named in the PDB:

```
PureCharParam   Item   ItemPowerRate   Upgrade   WeaponTitle
PassiveSkill    AbnormalState   LastTune   Total
```

plus loose members: `DotDamagePlus, SPRate, RangeEvasion, flag, MissPercentFix, DamageReflection,
ChangeAbilityInfo, HealRate, PassiveBuffKeepTimeUPRate, PassiveHealRate, PassiveCriDamageRatePlus,
PassiveHPDownRate{WCMin,WCMax,MAMin,MAMax,AC,MR}, PassiveMovingTBPlus, PhysicalImmuneRate,
MagicalImmuneRate, RangeOver, DMGMinusRate` and the methods `c_clear, c_StoreMob, c_Storepure,
c_MakeTotal, c_TotalPram_MinusCheck, c_StoreMover, IsNoAttack, IsNoAttacOrNoMove`.

**One block's field order** (from the PDB member list, in order):

```
Str  Con  Dex  Int  Men
WCmin  WCmax  MAmin  MAmax
AbsoluteAttack  AbsoluteDefend  AbsoluteHit  AbsoluteBlock
MoveSpeed  HPRecover  SPRecover  CastingTime  Critical
PhisycalWeaponMastery  MagicalWeaponMastery  ShieldAC
HitRate  EvaRate  MACri  CriDam  MagCriDam  CriDamRate  MagCriDamRate
AttSpeed  MaxHP  MaxHP_2  MaxSP
HPAbsorption_Hitted  SPAbsorption_Hitted  HPAbsorption_Hit  SPAbsorption_Hit
CriticalTB  RegistNone  ResistPoison  ResistDeaseas  ResistCurse
ResistMoveSpdDown  ResistGTI  MaxLP  LPRecover
```

That is 45 names. **The observed block stride is `0xCC` = 204 bytes = 51 ints**, so six fields are not in
the visible name run — and the six the accessors demonstrably use but the list omits are exactly
**AC, TH, TB, MR, MH, MB**.

The offsets in §3a decompose as `blockIndex * 0xCC + fieldOffset`. The blocks touched by `roe_AC`/`roe_TH`/
`roe_TB`/`roe_MR`/`roe_MinWC` are indices `0, 1, 4, 5, 7, 9, 10, 11, 12, 13, 14`, in the roles the chain
implies: **two blocks summed** (base + one source), **four blocks multiplied** as permille rates, **five
blocks added** as flat bonuses. `HitRate` and `EvaRate` are adjacent in the member list, which matches
`roe_TH` / `roe_TB` diverging by exactly one adjacent field (`+0xEC` vs `+0xF0`).

### 3c. The exact layout, from the PDB type stream [BIN, field-exact]

Recovered with `tools/pdb_members.py`, which scans CodeView `LF_MEMBER` records (`0x150D`:
`leaf, attr, typeIndex, <numeric leaf> offset, name`) — the same linear-scan trick as `S_PUB32`, so no MSF
container parsing and **no live process** is needed. These are the compiler's real offsets, not inferences.

**One parameter block — 51 ints, `0xCC` bytes** (exactly the stride measured in §3a):

| off | field | off | field | off | field |
|---|---|---|---|---|---|
| `+0x00` | **Str** | `+0x28` | MAmin | `+0x88` | AttSpeed |
| `+0x04` | **Con** | `+0x2C` | MAmax | `+0x8C` | MaxHP |
| `+0x08` | **Dex** | `+0x30` | **MR** | `+0x90` | MaxHP_2 |
| `+0x0C` | **Int** | `+0x34` | MH | `+0x94` | MaxSP |
| `+0x10` | **Men** | `+0x38` | MB | `+0x98..0xA4` | HP/SP Absorption ×4 |
| `+0x14` | **WCmin** | `+0x3C..0x48` | Absolute Attack/Defend/Hit/Block | `+0xA8` | CriticalTB |
| `+0x18` | **WCmax** | `+0x4C..0x5C` | MoveSpeed, HPRecover, SPRecover, CastingTime, Critical | `+0xAC..0xC0` | RegistNone, Resist×5 |
| `+0x1C` | **AC** | `+0x60..0x68` | Phisycal/MagicalWeaponMastery, ShieldAC | `+0xC4` | MaxLP |
| `+0x20` | **TH** | `+0x6C` | HitRate | `+0xC8` | LPRecover |
| `+0x24` | **TB** | `+0x70..0x84` | EvaRate, MACri, CriDam, MagCriDam, CriDamRate, MagCriDamRate | | |

**`Parameter::Container` — the blocks:**

| off | member | size | role |
|---|---|---|---|
| `+0x0000` | `PureCharParam` | `0xCC` | the character's own stats |
| `+0x00CC` | `Item` | `0x198` | **a PAIR of blocks**: `+0x00` plus, `+0xCC` rate |
| `+0x0264` | `ItemPowerRate` | `0x198` | pair |
| `+0x03FC` | `Upgrade` | `0x198` | pair |
| `+0x0594` | `WeaponTitle` | `0x198` | pair |
| `+0x072C` | `PassiveSkill` | `0x198` | pair |
| `+0x08C4` | `AbnormalState` | `0x198` | pair |
| `+0x0A5C` | `LastTune` | `0x198` | pair |
| `+0x0BF4` | `Total` | `0xCC` | the cached result (`c_MakeTotal`) |
| `+0x0CC0` | `DotDamagePlus` … | | loose members |
| `+0x0CE0` | `PassiveHPDownRateWCMin` | | ← `roe_AttackPower`'s low-bound bonus |
| `+0x0CFC` | `PassiveHPDownRateWCMax` | | ← its high-bound bonus |
| `+0x0D18..0x0D6C` | `PassiveHPDownRate` MAMin/MAMax/AC/MR | | the same mechanic for magic and defence |

The `0x198` sections are **two 0xCC blocks**: a *plus* half at `+0x00` and a *rate* half at `+0xCC`. That is
what §3a's shape actually is — every offset now decomposes exactly:

```
roe_<stat> =
    ( PureCharParam.<stat>            @ 0x0000            // base
    + Item.plus.<stat>                @ 0x00CC )          // gear
  * ItemPowerRate.rate.<stat>         @ 0x0330            // 0x0264 + 0xCC
  * PassiveSkill.rate.<stat>          @ 0x07F8            // 0x072C + 0xCC
  * AbnormalState.rate.<stat>         @ 0x0990            // 0x08C4 + 0xCC
  * LastTune.rate.<stat>              @ 0x0B28            // 0x0A5C + 0xCC
  / 1e12                                                  // == 1000^4
  + Upgrade.plus.<stat>               @ 0x03FC
  + WeaponTitle.plus.<stat>           @ 0x0594
  + PassiveSkill.plus.<stat>          @ 0x072C
  + AbnormalState.plus.<stat>         @ 0x08C4
  + LastTune.plus.<stat>              @ 0x0A5C
```

**Each accessor runs that chain twice** — once on the governing CORE STAT, once on the named stat itself:

| accessor | core-stat field | own field | classic mapping |
|---|---|---|---|
| `roe_MinWC` / `roe_MaxWC` | `+0x00` **Str** | `+0x14` / `+0x18` WCmin/WCmax | STR → weapon damage |
| `roe_AC` | `+0x04` **Con** | `+0x1C` AC | END → defence |
| `roe_TH` | `+0x08` **Dex** | `+0x20` TH | DEX → accuracy |
| `roe_TB` | `+0x08` **Dex** | `+0x24` TB | DEX → evasion |
| `roe_MR` | `+0x10` **Men** | `+0x30` MR | SPR → magic resist |

This resolves the two loose ends from §3a exactly:
- `roe_TH` vs `roe_TB` diverge at `0xEC` vs `0xF0` = `Item.TH` (`0xCC+0x20`) vs `Item.TB` (`0xCC+0x24`).
- `roe_MinMA`/`roe_MaxMA` starting at `0x790` is **not** an anomaly: `0x790 = 0x72C + 0x64`, i.e.
  `PassiveSkill.plus.MagicalWeaponMastery` — the magic accessors run the same chain over the MA fields
  (`+0x28`/`+0x2C`) and the *magical* mastery, where the physical ones use `+0x60`.

**No live memory dump was required** — the type stream is authoritative and reading it cannot perturb the
running game. The offsets above are the compiler's own.

---

## 4. Hit, block, critical [BIN]

| Function | What the code shows |
|---|---|
| `roe_HitRate` | computes a rate, draws `cWell512Random::well512_GetRandom`, compares. Uses `roe_TH` (attacker) vs `roe_TB` (defender). |
| `RulesOfEngagementNormalPY::roe_ShieldBlock` | `v = (fieldA + fieldB) * fieldC / 1000.0`, clamped at 0, then a further additive term. Again permille. |
| `roe_CriticalRate` | per-subclass; NormalPY folds four attacker fields (`+0x218`, `+0x6E0`, `+0xA10`, `+0xA38`). |
| `RulesOfEngagement::roe_CriticalStunRate` | **`fld CONST(200); ret`** — a flat 200 (permille ⇒ 20%). `AlwaysCritical` overrides it. |
| `roe_LevelGapDamageRevision` | fetches both levels, calls `LevelGap_*::GetLevelCapRate`, then `imul` by the damage and adds the rounding term — i.e. `damage = damage * rate / 1000`. |

`RulesOfEngagementAlwaysHit` / `AlwaysCritical` / `CureSkill` exist purely to override these.

### 4a. Full per-subclass override table [BIN]

Which subclass overrides what. Everything not listed falls through to `RulesOfEngagement`'s implementation,
so a blank cell means "base behaviour", not "absent".

| | AttackPower | DefendPower | Damage | HitRate | CriticalRate | ShieldBlock | IsDamageImmune | FreeState A/D |
|---|---|---|---|---|---|---|---|---|
| `NormalPY` (physical auto) | ✓ MinWC/MaxWC | ✓ AC | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ / ✓ |
| `NormalMA` (magic auto) | ✓ MinMA/MaxMA | ✓ MR | ✓ | ✓ | ✓ | — | ✓ | ✓ / ✓ |
| `PhisycalSkill` | ✓ | ✓ | — | ✓ | ✓ | — | — | — |
| `MagicalSkill` | ✓ | ✓ | — | ✓ | ✓ | — | — | — |
| `HealAttack` | — | — | — | — | — | — | — | — (overrides `roe_CalcDamage` instead) |
| `CureSkill` | — | — | — | ✓ | — | — | — | — |
| `AlwaysHit` | — | — | — | ✓ | — | — | — | — |
| `AlwaysCritical` | — | — | — | — | ✓ + `CriticalStunRate` | — | — | — |

Notes (all call targets below were resolved from the binary, not assumed):
- **Physical vs magical is a class swap, not a branch.** Verified by resolving each subclass's calls:
  - `NormalPY::roe_AttackPower` → `roe_MinWC` + `roe_MaxWC`; `NormalPY::roe_DefendPower` → `roe_AC`
  - `NormalMA::roe_AttackPower` → `roe_MinMA` + `roe_MaxMA`; `NormalMA::roe_DefendPower` → `roe_MR`
  - `MagicalSkill::roe_AttackPower` → `roe_MinMA` + `roe_MaxMA` (same pair as `NormalMA`)
  - `PhisycalSkill::roe_DefendPower` → **calls `NormalPY::roe_DefendPower` directly** — physical skills
    are defended by exactly the same AC path as auto-attacks.

  Identical structure, different accessors — which is why "magic damage appears unchanged" while armour came
  off is exactly the expected behaviour: armour moves `AC` (0x08), not `MR` (0x0D).
- **Only the two `Normal*` classes override `roe_Damage`**, and both merely call the base and then log —
  the arithmetic in §2 is shared by *every* attack type in the game.
- **Only `NormalPY` has a shield block.** Magic and skills cannot be shield-blocked.
- `roe_CriticalStunRate` is a flat `200` on the base and is only overridden by `AlwaysCritical`.
- Every `*ByGlobalAction` variant (`roe_HitRateByGlobalAction`, `roe_CriticalRateByGlobalAction`,
  `roe_ShieldBlockByGlobalAction`) exists per subclass as a parallel path for globally-scripted actions.
- `roe_FreeStat*` (`FreeStateAttackPower`, `FreeStateDefendPower`, `FreeStatHitRate`, `FreeStatCriRate`)
  are the free-stat-point contribution, split out so the "free stat" bonus can be applied independently —
  the debug log in `roe_Damage` prints `    After FreeStatBonus = ` right after applying it.

---

## 5. Multiplier tables [TBL]

All are **permille**: `1000` = ×1.000.

### `DamageByAngle` — the angle table

Two identical copies, `DamageByAngle_Chr` (hitting a character) and `DamageByAngle_Mob` (hitting a mob):

| DamagedAngle | DamageRate | ×
|---|---|---|
| 0 (front) | 1000 | 1.000 |
| 45 | 1040 | 1.040 |
| 90 (flank) | 1100 | 1.100 |
| 135 | 1120 | 1.120 |
| 170 | 1140 | 1.140 |
| 180 (behind) | 1200 | **1.200** |

**Being hit from behind costs 20% more.** Both attacker and defender facing therefore change damage —
which matters for the bot twice over: attack from behind, and do not get flanked.
(Columns: `DamagedAngle, DamageRate, CheckSum`.)

### `DamageLvGapEVP` — Monster → Player

**Flat `1000` (×1.000) at every gap from −150 to +150.** Level gap does **nothing** to incoming mob damage.
Useful negative result: level is eliminated as a confound when analysing damage the bot *takes*.

### `DamageLvGapPVE` — Player → Monster

Not flat. Attacking something *above* your level is rewarded:

| gap (my level − target) | ×    | gap | × |
|---|---|---|---|
| −150 … −5 | 1.500 | 0 | 1.000 |
| −4 | 1.400 | +1 … +150 | 1.000 |
| −3 | 1.300 | | |
| −2 | 1.200 | | |
| −1 | 1.100 | | |

So the bot deals up to **1.5×** against higher-level mobs, and gains nothing for over-levelling.

### `DamageLvGapPVP` — Player → Player

A full 151 × 151 matrix (`MyLv` × `TargetLv1..TargetLv150`), not a single curve.

### Other permille-style tables found by shape-scan

Scanned every table in `serversource-data` for a "value column clustering near 1000" shape rather than by name:

| Table | Column | Rows | Range |
|---|---|---|---|
| `DamageByAngle_Chr` / `_Mob` | `DamageRate` | 6 | 1000..1200 |
| `DamageLvGapPVE` | `DamageRate` | 24 | 1000..1500 |
| `ExpRecalculation_StandardDeviation` | `HandicapRate` | 6 | 0..1000 |
| `FriendPointReward` | `FPR_Rate` | 97 | 200..700 |
| `RandomOptionCount` | `LimitDropRate` | 5928 | 100..500 |
| `RareMoverRate` / `RareMoverSubRate` | `RMR_Rate` | 19 / 6 | 100..1000 |
| `WeaponAttrib` | `HitRate` | 14 | 1..500 |

`WeaponAttrib` (`WeaponType, UsableDegree, IsUsableInMoving, HitRate, Undefined0, Undefined1`) is the
per-weapon-type hit modifier and also carries the **UsableDegree** the cast-facing check uses.

Related tables not permille-shaped but part of combat: `MobWeapon` (MinWC/MaxWC/MinMA/MaxMA/TH/MH/AtkSpd/
BlastRate/Range/StaName), `MobInfoServer` (AC/TB/MR/MB/Str/Dex/Con/Int/Men + resists), `MobResist`,
`ActionRangeFactor`, `DamageLvGap*`.

---

## 6. The stat vocabulary [BIN] + the wire param ids [WIRE]

`CHAR_PARAMETER_DATA` — attack/defence inputs on a character:

```
Strength  Constitute  Dexterity  Intelligence  Wizdom  MentalPower
WClow WChigh    AC  TH  TB    MAlow MAhigh    MR  MH  MB
MaxHp MaxSp MaxLp MaxAp   MaxHPStone MaxSPStone  PwrStone GrdStone
PainRes RestraintRes CurseRes ShockRes
```

`NC_CHAR_CHANGEPARAMCHANGE_CMD` (0x1035) is `[changenum u8][(paramId u8, value u32) × n]`
(PDB: `CHAR_PARAMCHANGE_CMD { flag u8; value u32; }`, SizeOf 5). **The `flag` byte has no enum in the PDB** —
it is a bare `unsigned char`. The ids are the field order above:

| id | stat | id | stat | id | stat |
|---|---|---|---|---|---|
| 0x00 | **STR** | 0x08 | **AC** (phys def) | 0x10 | MaxHp |
| 0x01 | **END** (Constitute) | 0x09 | **TH** (aim) | 0x11 | MaxSp |
| 0x02 | **DEX** | 0x0A | **TB** (block/evade) | 0x12 | MaxLp |
| 0x03 | **INT** | 0x0B | MAlow | 0x13 | MaxAp |
| 0x04 | *Wizdom — never sent* | 0x0C | MAhigh | 0x14 | MaxHPStone |
| 0x05 | **SPR** (MentalPower) | 0x0D | **MR** (magic def) | 0x15 | MaxSPStone |
| 0x06 | WClow | 0x0E | *MH — never sent* | 0x16 | PwrStone |
| 0x07 | WChigh | 0x0F | *MB — never sent* | 0x17 | GrdStone |
| | | | | 0x18..0x1B | PainRes, RestraintRes, CurseRes, ShockRes |

**Six named stat slots, five real ones.** Across `Z:/Damage.pcapng` and `Z:/QuestsLowLevel.pcapng`,
`Wizdom (0x04)` is **never sent once** — nor are `MH (0x0E)` or `MB (0x0F)`. The live core stats are
**STR, END, DEX, INT, SPR**; `Wizdom` is a vestigial slot in the struct.

⚠️ The *server's own* `CHAR_PARAMETER_DATA` in `Zone.pdb` is a **shorter, different list** with no
AC/TH/TB/MR/MH/MB at all. The wire matches **FiestaLib's** layout, not the server struct's. Use FiestaLib's.

---

## 7. Measured wire data [WIRE]

`Z:/Damage.pcapng` — the operator raised a stat, then stripped armour piece by piece while standing still
and taking hits from **mob 84 "Orc"** (level 61, `MinWC 747 / MaxWC 1137 / TH 267 / Str 822 / AC 102 / TB 179`).
Only normal hits (flag `0x0000`); `ismissed` and `ismissed+isshieldblock` frames carry 0 damage and are
excluded. **No critical hits occur anywhere in the capture.**

| AC (0x08) | n | damage | mean | damage × AC |
|---|---|---|---|---|
| 1023 | 62 | 73..104 | 84.9 | 86,853 |
| 713 | 36 | 120..149 | 131.7 | 93,902 |
| 535 | 56 | 116..213 | 169.9 | 90,897 |

`damage × AC` is constant to **±8%**, consistent with `damage ∝ attack / defend` and no additive offset.
The residual is expected: `DefendPower ≠ AC` exactly (§3), and the **angle table (§5) alone spans 20%**.

---

## 8. What is NOT established [OPEN]

1. ~~`X` is not proven to be attacker level.~~ **RESOLVED** — vtable `+0x4D8` is `so_GetLevel` on every
   combat class (§2). Method: read the pointer out of each `??_7Shine*` vftable rather than trying to infer
   it from the wire, where `X` and `attack` are multiplicatively confounded and can never be separated.
2. **`DefendPower`'s non-AC terms** are unidentified (`defender->vtable[0x4F0]` and following).
3. ~~The missing-resource bonus tables are not decoded.~~ **PARTLY RESOLVED** — the lookup is
   `ChangeByConditionParam::cbcp_GetValue`, i.e. the ChangeByCondition system (cf.
   `PROTO_NC_CHAR_CHANGEBYCONDITION_PARAM_CMD { nSkillID, nChangeRate, nParamNum, aParam[] }`). The two
   instances at `obj+0xCE0` / `obj+0xCFC` are its low-bound and high-bound parameter sets. The contents of
   those parameter sets are still not dumped.
4. **`arg->rate` is assumed to be the level-gap DamageRate.** It is an int divided by 1000 in a function
   whose sibling `roe_LevelGapDamageRevision` does exactly that, but it was not traced to its writer.
5. The **angle** of each recorded hit is not in the capture analysis, so the 20% angle band is currently
   folded into the ±8% residual rather than removed from it.
6. ~~Which object each `call edx` returns / the field-exact offsets.~~ **FULLY RESOLVED** (§3b, §3c) —
   `so_parameter()` returns `Parameter::Container const*`, and every member offset is now read from the
   PDB type stream (`tools/pdb_members.py`). Every offset in §3a decomposes exactly, including the two
   that looked anomalous (`roe_TH`/`roe_TB` at `0xEC`/`0xF0`, and `roe_MinMA` at `0x790`).

## Where the fit went wrong

The earlier `damage = K/(DEF − 141)` fitted three cell *means* with two free parameters and reported a 2.65%
residual. It failed the moment it was tested against `MobWeapon`: implied weapon damage came out 594..1193
against a table range of 747..1137, overflowing both ends. Three compounding mistakes:

- **A fitted constant was mistaken for a game constant.** The real function contains only `1000.0` and `1.0`.
- **The raw table range was treated as the roll range.** `roe_MinWC`/`roe_MaxWC` layer ~12 modifier slots on
  top of it (§3), so the comparison was never valid.
- **Most stats were ignored.** Only AC entered the fit; STR, the attacker's stats, the angle table and the
  level-gap tables were all absent.

Lesson: **the formula is code. Read the code.** `tools/pdb_disasm.py` makes that a one-liner.

---

## Appendix A — Structures, in full [BIN, field-exact]

All recovered with `tools/pdb_members.py` (CodeView `LF_MEMBER` scan). These are the compiler's offsets.

### `EngageArgument` — the context passed to every `roe_*`

| off | field | | off | field |
|---|---|---|---|---|
| `+0x00` | `att` — **attacker** object | | `+0x13` | `isshieldblock` |
| `+0x04` | `def` — **defender** object | | `+0x14` | `isresist` |
| `+0x08` | `sklinfo` | | `+0x15` | `isDamege2Heal` |
| `+0x0C` | `empower` | | `+0x16` | `isImmune` |
| `+0x0E` | `actionnumber` | | `+0x18` | `attackloc` |
| `+0x0F` | `attackcode` | | `+0x1C` | `damagerate` |
| `+0x10` | `iscritical` | | `+0x20` | `crirateadd` |
| `+0x11` | `ismiss` | | `+0x24` | `pMultiHitArg` |
| `+0x12` | `isdead` | | `+0x28` | **`nBMPDamageRate`** ← the rate in `roe_Damage` |

⚠️ `roe_Damage` divides by 1000 the field at `+0x28`, which is **`nBMPDamageRate`** — *not* `damagerate`
(`+0x1C`). The earlier assumption that `+0x28` carried the level-gap `DamageRate` is **wrong**; level gap is
applied separately by `roe_LevelGapDamageRevision`.

### `Parameter::Container`

| off | member | size | | off | member |
|---|---|---|---|---|---|
| `+0x0000` | `PureCharParam` | `0xCC` | | `+0x0CC0` | `DotDamagePlus` |
| `+0x00CC` | `Item` | `0x198` (pair) | | `+0x0CCA` | `SPRate` |
| `+0x0264` | `ItemPowerRate` | `0x198` (pair) | | `+0x0CCC` | `RangeEvasion` |
| `+0x03FC` | `Upgrade` | `0x198` (pair) | | `+0x0CCE` | `flag` |
| `+0x0594` | `WeaponTitle` | `0x198` (pair) | | `+0x0CD0` | `MissPercentFix` |
| `+0x072C` | `PassiveSkill` | `0x198` (pair) | | `+0x0CD2` | `DamageReflection` |
| `+0x08C4` | `AbnormalState` | `0x198` (pair) | | `+0x0CD4` | `ChangeAbilityInfo` |
| `+0x0A5C` | `LastTune` | `0x198` (pair) | | `+0x0CD6` | `HealRate` |
| `+0x0BF4` | `Total` | `0xCC` | | `+0x0CD8` | `PassiveBuffKeepTimeUPRate` |
| | | | | `+0x0CDA` | `PassiveHealRate` |
| | | | | `+0x0CDC` | `PassiveCriDamageRatePlus` |
| | | | | `+0x0CE0` | `PassiveHPDownRateWCMin` |
| | | | | `+0x0CFC` | `PassiveHPDownRateWCMax` |
| | | | | `+0x0D18` | `PassiveHPDownRateMAMin` |
| | | | | `+0x0D34` | `PassiveHPDownRateMAMax` |
| | | | | `+0x0D50` | `PassiveHPDownRateAC` |
| | | | | `+0x0D6C` | `PassiveHPDownRateMR` |
| | | | | `+0x0D88` | `PassiveMovingTBPlus` |
| | | | | `+0x0DA4` | `PhysicalImmuneRate` |
| | | | | `+0x0DA6` | `MagicalImmuneRate` |
| | | | | `+0x0DA8` | `RangeOver` |

Methods: `c_clear, c_clearplus, c_clearrate, c_compare, c_compareelement, c_WC, c_MA, c_StoreMob,
c_Storepure, c_StoreMover, c_MakeTotal, c_TotalPram_MinusCheck, IsNoAttack, IsNoAttacOrNoMove`.

Each `0x198` section is **two `0xCC` blocks**: *plus* at `+0x00`, *rate* at `+0xCC`.

### One parameter block — 51 ints, `0xCC` bytes

| off | field | off | field | off | field |
|---|---|---|---|---|---|
| `+0x00` | Str | `+0x38` | MB | `+0x84` | MagCriDamRate |
| `+0x04` | Con | `+0x3C` | AbsoluteAttack | `+0x88` | AttSpeed |
| `+0x08` | Dex | `+0x40` | AbsoluteDefend | `+0x8C` | MaxHP |
| `+0x0C` | Int | `+0x44` | AbsoluteHit | `+0x90` | MaxHP_2 |
| `+0x10` | Men | `+0x48` | AbsoluteBlock | `+0x94` | MaxSP |
| `+0x14` | WCmin | `+0x4C` | MoveSpeed | `+0x98` | HPAbsorption_Hitted |
| `+0x18` | WCmax | `+0x50` | HPRecover | `+0x9C` | SPAbsorption_Hitted |
| `+0x1C` | AC | `+0x54` | SPRecover | `+0xA0` | HPAbsorption_Hit |
| `+0x20` | TH | `+0x58` | CastingTime | `+0xA4` | SPAbsorption_Hit |
| `+0x24` | TB | `+0x5C` | Critical | `+0xA8` | CriticalTB |
| `+0x28` | MAmin | `+0x60` | PhisycalWeaponMastery | `+0xAC` | RegistNone |
| `+0x2C` | MAmax | `+0x64` | MagicalWeaponMastery | `+0xB0` | ResistPoison |
| `+0x30` | MR | `+0x68` | ShieldAC | `+0xB4` | ResistDeaseas |
| `+0x34` | MH | `+0x6C` | HitRate | `+0xB8` | ResistCurse |
| | | `+0x70` | EvaRate | `+0xBC` | ResistMoveSpdDown |
| | | `+0x74` | MACri | `+0xC0` | ResistGTI |
| | | `+0x78` | CriDam | `+0xC4` | MaxLP |
| | | `+0x7C` | MagCriDam | `+0xC8` | LPRecover |
| | | `+0x80` | CriDamRate | | |

### `CHAR_PARAMCHANGE_CMD` / `PROTO_NC_CHAR_BASEPARAMCHANGE_CMD` (wire)

```c
struct CHAR_PARAMCHANGE_CMD { uint8 flag; uint32 value; };            // SizeOf 5
struct PROTO_NC_CHAR_BASEPARAMCHANGE_CMD { uint8 changenum; CHAR_PARAMCHANGE_CMD param[]; };
```

### `ChangeByConditionParam`

`cbcp_nID, cbcp_nCondition, cbcp_nChange, cbcp_nChangeParam, cbcp_nCharged, cbcp_nMaxValueNum, cbcp_pValue`
with `cbcp_Clear, cbcp_SetCondition, cbcp_MakeParam, cbcp_MakeParam_Plus, cbcp_SendBuffer, cbcp_GetValue,
cbcp_GetValue_Index`. Wire form: `PROTO_NC_CHAR_CHANGEBYCONDITION_PARAM_CMD { nSkillID u16, nChangeRate u16,
nParamNum u16, aParam[] }`.

### `MobWeapon` / `MobInfoServer` (the mob's `PureCharParam` inputs) [TBL]

`MobWeapon`: `ID, InxName, Skill, AtkSpd, BlastRate, AtkDly, SwingTime, HitTime, AtkType, MinWC, MaxWC, TH,
MinMA, MaxMA, MH, Range, MopAttackTarget, HitType, StaName, StaStrength, StaRate, AggroInitialize`

`MobInfoServer`: `ID, InxName, Visible, AC, TB, MR, MB, EnemyDetectType, MobKillInx, MonEXP, EXPRange,
DetectCha, ResetInterval, CutInterval, CutNonAT, FollowCha, PceHPRcvDly, PceHPRcv, AtkHPRcvDly, AtkHPRcv,
Str, Dex, Con, Int, Men, MobRaceType, Rank, FamilyArea, …, MaxSP, BroadAtDead, TurnSpeed, WalkChase,
AllCanLoot, DmgByHealMin, DmgByHealMax, RegenInterval`

Note the mob tables carry **exactly** the parameter-block fields: `Str/Dex/Con/Int/Men`, `AC/TB/MR/MB`,
`MinWC/MaxWC/TH`, `MinMA/MaxMA/MH` — i.e. a mob's `PureCharParam` is these two rows.

---

## Appendix B — Simulating the captured stream against the formula [WIRE + BIN]

`tools/damage_sim.py` replays `Z:/Damage.pcapng` hit by hit. It does **not** fit anything: it inverts the
formula read from the binary and asks whether the results agree.

```
impliedAttack = damage * AC / (attackerLevel + 1)          // level+1 = 62 for the Orc
```

For a plain field mob every `plus` block is 0 and every `rate` block neutral, so `AttackPower` must be a
roll over **one fixed band** — every hit from the same mob type must map into the same band regardless of
what the character's defence was at the time. Result over 164 normal hits from mob 84 (69 missed, 22
blocked, **0 critical**):

| AC | n | damage | impliedAttack | band width |
|---|---|---|---|---|
| 1230 | 22 | 55..84 | 1091..1666 | **×1.527** |
| 1215 | 121 | 71..107 | 1391..2097 | **×1.507** |
| 1212 | 2 | 84..88 | 1642..1720 | ×1.048 (n=2) |
| 535 | 19 | 168..213 | 1450..1838 | ×1.268 |

### What matches — the shape, to within 0.5%

`MaxWC / MinWC = 1137 / 747 = ` **×1.522**. The two well-sampled cells give **×1.527** (n=22) and
**×1.507** (n=121). So within a single defence value the damage spread reproduces the weapon roll ratio
almost exactly. That confirms, independently of any fitting:

- `AttackPower` really is a **roll between MinWC and MaxWC**, and
- damage is **linear** in that roll — no exponent, no additive offset inside the roll.

### What does not match — the absolute scale

The effective band is `1391..2097` against a raw table band of `747..1137` — a consistent
**×1.86 / ×1.84** on both ends. And the cells do not align with each other: `AC=1230` implies a band
centred on ~1379 while `AC=1215` implies ~1744, a 26% disagreement between two nearly identical defence
values. **So the answer to "does it match the ranges exactly" is: the shape does, the absolute values do
not.**

Both discrepancies have the same, already-documented cause and neither is a contradiction of §2:

1. **`roe_MinWC`/`roe_MaxWC` are accumulators, not the table value** (§3a/§3c). They run the chain over
   `Str` *and* `WCmin` *and* `PhisycalWeaponMastery`. The raw `MobWeapon` row is only `PureCharParam.WCmin`
   — one input of several — so a ×1.85 gap between raw and effective is expected, not anomalous.
2. **`DefendPower` is not the wire `AC`.** `NormalPY::roe_DefendPower` calls `roe_AC` and then adds further
   terms (§3). Using the wire `AC` as the divisor is what makes cells with near-identical AC disagree.

### What would close it

- Decode `roe_MinWC`'s full expression (~200 instructions, three interleaved chains) to get the exact
  Str/WCmin/Mastery combination, and `roe_DefendPower`'s non-AC terms. Both are pure disassembly work.
- Or capture **one mob type against a character whose AC is changed while nothing else moves, with the
  facing held fixed**, and enough landed hits per state (30+) to pin each band's endpoints. That isolates
  `DefendPower` empirically without needing the full expression.

Until one of those lands, the usable, verified result is the **relative** law:

```
damage ∝ (attackerLevel + 1) x roll(MinWC..MaxWC) / DefendPower x angleRate
```

with the roll ratio confirmed to 0.5% and the level term proven from `so_GetLevel`.
