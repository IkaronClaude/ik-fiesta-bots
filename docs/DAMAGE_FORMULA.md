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

---

## Appendix C — 1:1 reproduction: executing the real code [BIN, EXACT]

`tools/roe_emu.py` maps `Zone.exe` into a Unicorn x86 emulator, builds a `Parameter::Container` from a mob's
`MobInfoServer` + `MobWeapon` row, and **calls the real `RulesOfEngagement` functions**. Nothing is
transcribed by hand, so there is nothing to get wrong.

### Harness

- Whole image mapped at `0x400000`; unmapped pages auto-map zero-filled, so every global reads 0 and the
  debug-log blocks are skipped exactly as in production.
- `FunctionProfiler::pr_Entrance` / `pr_Exit` patched to `ret 4` (`__thiscall void f(char*)`). They are pure
  instrumentation; left running they executed 1.5M instructions and walked into the stack.
- The **real `ShineMob` vftable** is copied out of `.rdata` and only four slots are replaced
  (`so_parameter`, `so_GetLevel`, `so_GetHP`, `so_MaxHP`). A synthetic zero vtable is not safe —
  `roe_MinWC` also calls slot `+0x938`, and a zero there is a call to address 0.
- Fakes are emitted as real machine code (`mov eax, imm32; ret`), not Unicorn hooks — writing EIP from
  inside a `UC_HOOK_CODE` callback returned into the stack.
- The result is captured by returning into a `fstp qword ptr [mem]` thunk rather than decoding the 80-bit
  `FP0` register, which silently yielded 0.0.

### Result — mob 84 "Orc" (level 61), neutral modifiers

```
PureCharParam: Str 822  Con 140  Dex 147  Int 135  Men 112
               WCmin 747  WCmax 1137  AC 102  TB 179  MR 127  TH 267
```

| call | returned | equals |
|---|---|---|
| `roe_MinWC` | **1569.0** | `822 Str + 747 WCmin` |
| `roe_MaxWC` | **1959.0** | `822 Str + 1137 WCmax` |
| `roe_AC` | **242.0** | `140 Con + 102 AC` |
| `roe_MR` | **239.0** | `112 Men + 127 MR` |
| `roe_TH` | **414.0** | `147 Dex + 267 TH` |
| `roe_TB` | **326.0** | `147 Dex + 179 TB` |
| `NormalPY::roe_AttackPower` | **1569.0** | `= roe_MinWC` (the WELL512 state is zeroed, so the roll lands on its floor) |
| `NormalPY::roe_DefendPower` | **242.0** | `= roe_AC` exactly |

So with neutral modifiers **every accessor is exactly `coreStat + ownStat`**, confirming §3c's
"runs the chain twice" reading with real numbers rather than inference.

### The formula, proven

```
roe_Damage(attack=1569.0, defend=242.0) = 401.9752197265625      <- the real server code
(61 + 1) * 1569 / 242                   = 401.9752197265625      <- our closed form
```

**Identical.** The damage law is therefore exactly:

```
damage = (attackerLevel + 1) * AttackPower * (nBMPDamageRate / 1000) / DefendPower
AttackPower = roll(roe_MinWC .. roe_MaxWC)        // WELL512
DefendPower = roe_AC                              // for a normal physical hit
roe_<stat>  = (coreStat chain) + (ownStat chain)  // each chain per 3a/3c
```

then the **angle** rate (§5), then crit / block / miss.

### Reproducing

```bash
pip install unicorn
python tools/roe_emu.py --mob 84            # any mob id; reads its real MobInfoServer + MobWeapon row
```

### Remaining gap against the capture, and what it is

Predicted attack band for the Orc is `1569..1959`. The capture's implied band (§ Appendix B, using
`defend = wire AC + Con`) is `~1726..2601` — the floor is close, the ceiling overshoots. Two known, named
effects are still unmodelled per hit, and both push the ceiling up:

1. **Angle** — up to ×1.200 (§5), not recoverable from the capture.
2. **The missing-HP attack bonus** — `PassiveHPDownRateWCMin/WCMax` through `cbcp_GetValue`, indexed by
   permille of missing HP (§3). The operator was *killing* these mobs, so their HP fell throughout, and this
   term grows as it does. It is reconstructible: our outgoing `SWING_DAMAGE` carries `resthp` per mob handle,
   so each incoming hit can be dated against that mob's HP at that instant.

Neither is a discrepancy in the formula — both are inputs the capture does not directly carry.

### Why this matters for the bot (future goal)

With the forward function exactly executable, the **inverse** becomes tractable: observe damage, angle,
level and defence on the wire, and solve for a mob's `MobWeapon` / `MobInfoServer` row. The bot can then
learn `MinWC/MaxWC/Str/AC/TB/MR` for any mob it fights, rather than needing the server tables — which is
exactly the "fit MobInfoServer + MobWeapon from combat data" goal. `roe_emu.py` is the oracle that makes
each candidate fit checkable against ground truth.

---

## Appendix D — Closing the oracle's gaps (in progress)

`tools/roe_oracle.py` is the reusable oracle: arbitrary `Parameter::Container` fields, a batch JSON protocol
on stdin/stdout (so a fuzz driver in any language can call it), and a `--probe` mode.

### Closed

**1. Where the angle multiplier is applied [BIN].** Scanning `.text` for calls to
`DamageByAngle::DamageTable::operator[]` (`0x45C9A0`) finds exactly **two** call sites:

```
0x504994  inside  RulesOfEngagement::roe_AttackPowerCalcDamage
0x506161  inside  RulesOfEngagement::roe_CalcDamage
```

So **`roe_CalcDamage` is the true top-level** — it applies AttackPower/DefendPower/roe_Damage, then the angle
rate, the level gap, and crit/block/miss, and returns the final **integer** damage. Appendix C's use of
`roe_Damage` was one layer too low.

**2. The angle table's real shape and index [BIN, exact].** `dt_Load` zero-fills `rep stosd (0x2D)` + `stosw`
= 182 bytes = **`uint16[91]`**, and `operator[]` is:

```c
if (i < 0) i = -i;
if (i > 90) i = i - 180 - ((i - 91) / 180) * 180;      // fold
if (i < 0) i = -i;
if (i > 90) { log error; return 1000; }
return table[i];
```

Derived empirically by running `operator[]` against an identity table over 0..360 and both signs:

```
index(angle) = abs(((abs(angle) + 90) % 180) - 90)
```
verified at 0→0, 45→45, 90→90, 91→89, 100→80, 135→45, 170→10, 179→1, 180→0, 181→1, 225→45, 270→90,
315→45, 359→1, 360→0, −45→45, −135→45.

⚠️ **This does not map onto `DamageByAngle_Mob`'s `DamagedAngle` column directly** — index 0 is reached by
both 0° and 180°, yet the table lists 0→1000 and 180→1200. `dt_Load` therefore transforms the rows on load,
and **how it does so is not yet established.** The oracle takes the 91-entry array as an *input*
(`set_angle_table`, default neutral 1000) rather than guessing the mapping.

**3. The missing-HP attack bonus is NOT the capture gap [BIN, measured].** Appendix C listed it as a likely
cause. Testing it directly in the oracle — same mob at full HP vs 10% HP:

```
AttackPower @ 3562/3562 HP = 1569.0
AttackPower @  356/3562 HP = 1569.0     delta 0.0
```

It contributes **nothing**, because the bonus comes from `PassiveHPDownRateWCMin/WCMax` in the container and a
plain field mob's are zero — `MobInfoServer`/`MobWeapon` have no such column. **That hypothesis is dead**; the
remaining capture discrepancy must be angle and/or `DefendPower`, not this.

**4. `--probe` derives dependencies empirically.** Perturbing one container field at a time confirms the
accessor structure from the outside, and surfaced the clamp directly: with an all-zero container every
accessor returns **1**, not 0 — that is `if (v <= 0) v = 1` (§2) firing in the accessors too.
It also confirms the asymmetries, e.g. `roe_MinWC` depends on `Upgrade.plus.WCmax` and
`AbnormalState.plus.WCmax` (WCmax, not WCmin).

### Still open

**`roe_CalcDamage` NOW WORKS.** Seven blockers, each of which failed *silently somewhere other than its
cause* — which is the whole reason each is written down:

| blocker | symptom | fix |
|---|---|---|
| IAT never populated | jumped to `0x34B69A` | stub all 182 imports. ⚠️ `pefile.PE(fast_load=True)` omits the import directory, so the stubbing silently no-ops unless you force `parse_data_directories` |
| wrong stdcall arg counts | a **later** `ret` popped 0 | `DecodePointer`/`EncodePointer` are 1 arg **and must be IDENTITY** (returning 0 makes the next indirect call jump to null); `TlsSetValue` is 2 args |
| CRT per-thread data | `__getptd_noexit` → `TlsGetValue` → `DecodePointer` → `call eax` on null | stub it to return a zeroed `_ptiddata` |
| `roe_CriticalStun` | applies a stun abnormal-state on a non-registered object → assert → `malloc`/`MessageBoxW`/`ExitProcess` took the emulator with it | `ret 4`; it is a `void` side effect, damage unaffected |
| vtable `+0xD2C` / `+0xD34` | `call edx` into world-state code | `so_ply_JobChangeDamageUp(attacker, damage)` is a damage MODIFIER hook, identity for a mob defender: `mov eax,[esp+8]; ret 8` |
| `GetLevelCapRate` | read the null `ITableBase::ms_pkTable`, returned 0, so the level-gap step multiplied damage by 0/1000 | patched to a settable constant (`set_level_gap_rate`, default 1000). Exactly right for Monster→Player, whose table is flat 1000 |
| **`EngageArgument.damagerate` (+0x1C) left 0** | **every** input returned 1 | it is a **permille and must default to 1000**. At 0 the raw damage is 0 and `test eax,eax / jg` at `0x5061CB` clamps to 1 — the degenerate all-1s behaviour |

### Verified against the closed form

`CalcDamage` executes the full sequence — `roe_IsDamageImmune`, `roe_CriticalRate`, `roe_FreeStatCriRate`,
`roe_CriticalStun`, `roe_AttackPower` (`roe_MinWC`/`roe_MaxWC`), `roe_DefendPower` (`roe_AC`), `roe_Damage`,
`roe_LevelGapDamageRevision` — and lands exactly where the closed form predicts:

| defender | AttackPower | DefendPower | `(L+1)·A/D` | `CalcDamage` | ratio |
|---|---|---|---|---|---|
| AC 1215, Con 292 | 1569 | 1507 | 64.55 | **129** | 1.998 |
| AC 1023, Con 292 | 1569 | 1315 | 73.98 | **147** | 1.987 |
| AC 713, Con 262 | 1569 | 975 | 99.77 | **199** | 1.995 |
| AC 535, Con 262 | 1569 | 797 | 122.06 | **244** | 1.999 |

A constant **×2.0** across a 2× spread of defence, with the residual being integer rounding — that is the
**critical hit** the call sequence shows firing (the WELL512 state is deterministic in the harness, so every
call crits). `DefendPower = Con + AC` is confirmed directly (292+1215 = 1507).

So the closed form is now validated *through the top-level function*, not just `roe_Damage`:

```
CalcDamage = round( (attackerLevel+1) * AttackPower / DefendPower * critMultiplier
                    * damagerate/1000 * angleRate/1000 * levelGapRate/1000 )     , min 1
```

### Still open

- **Angle is not yet exercised.** Setting the whole table to 1200 gives the same 129 as 1000, because the
  angle is derived from `attackloc` (+0x18) and the two objects' positions, which are still zero. The table
  and the index fold are known (§Appendix D); wiring the geometry is what remains.
- **The RNG is deterministic** (zeroed WELL512 state), so crit fires every call and the attack roll always
  lands on `MinWC`. Seeding it is needed to sample the distribution.
- **`dt_Load`'s SHN → `uint16[91]` mapping** is still not established (see above).

---

## Appendix E — Deterministic overrides, and the exact integer law

The engine is stochastic: the caller draws from WELL512 and compares against a **rate** returned by
`roe_CriticalRate` / `roe_HitRate` / `roe_ShieldBlock`. So the oracle does not seed the RNG — it forces the
*rate*, which forces the decision without perturbing the generator. Each override is tri-state:
**True = always, False = never, None = leave the real code and let the RNG decide.** Restoring `None` puts
the original bytes back, so a run can mix forced and free branches.

```python
o.call({"fn": "CalcDamage", "att": ..., "def": ..., "crit": False})   # never crit
o.call({... , "crit": True})                                          # always crit
o.set_override("hit", False); o.set_immune(True)                      # or set them directly
```

### The exact law

With `crit` forced both ways against four defence values (mob 84 Orc, level 61, `AttackPower = 1569`):

| defender | DefendPower | `crit=False` | `crit=True` | `(L+1)·A/D` |
|---|---|---|---|---|
| AC 1215, Con 292 | 1507 | **64** | **129** | 64.55 |
| AC 1023, Con 292 | 1315 | **73** | **147** | 73.98 |
| AC 713, Con 262 | 975 | **99** | **199** | 99.77 |
| AC 535, Con 262 | 797 | **122** | **244** | 122.06 |

Every non-crit value is **`floor()` of the closed form** — 64.55→64, 73.98→73, 99.77→99, 122.06→122 — and
every crit value is `floor(2 × closed)`: 129.1→129, 147.96→147, 199.5→199, 244.1→244. So:

```
damage = floor( (attackerLevel + 1) * AttackPower / DefendPower
                * (crit ? 2 : 1) * damagerate/1000 * angleRate/1000 * levelGapRate/1000 ),  min 1
AttackPower = roll(roe_MinWC .. roe_MaxWC)
DefendPower = roe_AC                       = coreStat(Con) chain + AC chain
```

`DefendPower = Con + AC` is confirmed directly (292 + 1215 = 1507; 262 + 535 = 797).

### The roll override — and a Unicorn trap worth knowing

The attack roll is **not** any `well512` overload. `NormalPY::roe_AttackPower` computes `MaxWC - MinWC`,
converts it with `__ftol2_sse`, calls **`RandomBox::rb_largerandom(int)`** (`0x63CCC0`), and adds `MinWC`
back. Patching the `well512` overloads changed nothing precisely because they only feed `rb_largerandom`.

⚠️ **Unicorn caches translated blocks.** Once `rb_largerandom` has executed, rewriting its bytes has no
effect — the stale translation keeps running. That is why an early version appeared to work on the first
permille of a process and then returned `MinWC` forever, while the crit override worked every time: crit
rewrites a *double slot* and leaves the code identical, so there is nothing to re-translate. The fix is the
same discipline — **write the code once, vary only the operands in a data slot** (`imul eax,[slot]` /
`mov ecx,[slot+4]`), with `ctl_remove_cache` as a belt-and-braces.

With that, every value is exact (mob 84 Orc, level 61, defender AC 1215 + Con 292 → `DefendPower` 1507):

| roll | AttackPower | expected `MinWC + (MaxWC-MinWC)·roll/1000` | `crit=False` | `floor(62·A/D)` | `crit=True` | `floor(2·62·A/D)` |
|---|---|---|---|---|---|---|
| 0 | 1569 | 1569 | **64** | 64 | **129** | 129 |
| 100 | 1608 | 1608 | **66** | 66 | **132** | 132 |
| 250 | 1666 | 1666 | **68** | 68 | **137** | 137 |
| 500 | 1764 | 1764 | **72** | 72 | **145** | 145 |
| 750 | 1861 | 1861 | **76** | 76 | **153** | 153 |
| 900 | 1920 | 1920 | **78** | 78 | **157** | 157 |
| 1000 | 1959 | 1959 | **80** | 80 | **161** | 161 |

Every cell matches — the roll, the floor, and the crit doubling. **The oracle is deterministic and
exhaustively controllable over `roll x crit x stats`, which is what makes differential fuzzing possible.**

### What the overrides do NOT yet cover


- **`hit` / `block` / `immune` have no observable effect on this path** — and the call trace says why:
  `roe_CalcDamage` invokes `roe_IsDamageImmune`, `roe_CriticalRate`, `roe_FreeStatCriRate`,
  `roe_CriticalStun`, `roe_AttackPower`, `roe_DefendPower`, `roe_Damage`, `roe_LevelGapDamageRevision` —
  and **never `roe_HitRate` or `roe_ShieldBlock`**. Those are evaluated by a layer *above* `CalcDamage`
  (`roe_AttackPowerCalcDamage` is the other angle-table caller and the likely home). So the overrides are
  wired but untested until that layer is driven.

---

## Appendix F — The C# port and the differential fuzz workflow

- `src/Fiesta.Bot/Combat/DamageFormula.cs` — the port (`ParamField`, `ParamBlock`, `ParamContainer`,
  `DamageFormula`).
- `tools/fuzz_damage.py` — drives the C# and the Unicorn oracle on identical random inputs and requires
  exact agreement. Any mismatch is a bug in the port *by construction*: the oracle is the server.
- `docs/roe_field_dependencies.txt`, `docs/roe_rate_targets.txt` — the machine-derived structure (below).

The generator deliberately includes all-zero containers (to exercise the clamps), single-field spikes,
rate halves at 0/1/500/2000/5000, negatives, `int16` extremes, and roll/crit at both ends.

### What the fuzz caught immediately

First run: **5/60 agreement.** Two systematic bugs, neither of which a code review would have found:

1. **Each accessor reads a FIXED side.** From the binary (`[esi]`/`[edi]` = attacker, `[esi+4]` = defender):
   **attacker** supplies `MinWC`/`MaxWC`/`TH`; **defender** supplies `AC`/`TB`/`MR`. Semantically exactly
   right — your weapon and accuracy, their armour, block and resist.
2. **The clamp applies to the CORE chain only.** Clamping both halves makes an all-zero container return 2;
   the real accessors return 1.

Fixing both took it to **42/60**.

### The remaining structure, derived not guessed

Perturbing one field at a time against the oracle (`--probe`, then a second pass with the own-field
non-zero to tell "rate multiplies the sum" from "rate multiplies a zero own-part"):

| accessor | core chain | own base | own pluses | rate targets |
|---|---|---|---|---|
| `roe_MinWC` | Str | `PCP.WCmin + Item.plus.WCmin` | `Upgrade.plus.`**`WCmax`**, `AbnormalState.plus.`**`WCmax`**, `PassiveSkill.plus.PhisycalWeaponMastery` | ItemPowerRate/PassiveSkill `.WCmin` + AbnormalState `.WCmax` multiply the **SUM** |
| `roe_MaxWC` | Str | `PCP.WCmax + Item.plus.WCmax` | same, all `WCmax` | same three, all `WCmax`, on the **SUM** |
| `roe_TH` / `roe_TB` | Dex | `PCP.X + Item.plus.X` | all five `.plus.X` | all four rates multiply the **own part only** |
| `roe_AC` / `roe_MR` | Con / Men | `PCP.X + Item.plus.X` | all five `.plus.X` | PassiveSkill/LastTune hit **own**; ItemPowerRate/AbnormalState show a larger gain — **unresolved** |

⚠️ **`roe_MinWC` genuinely reads `WCmax` for its Upgrade and AbnormalState plus-terms and for one rate.**
That is asymmetric and looks like a copy-paste slip in the original server, but it is the behaviour, so the
port must reproduce it. This is exactly the sort of thing that is invisible to reading and obvious to fuzzing.

### Open

`roe_AC` / `roe_MR`: with core=100 and own=1000, `ItemPowerRate.rate` and `AbnormalState.rate` on the own
field give **4200** rather than the 2100 (own doubled) or 2200 (sum doubled) that every other accessor shows.
Something in those two is counted twice. `roe_AC` is `DefendPower`, so this must be resolved before the port
can be trusted for damage. The next step is a third perturbation pass isolating that term, or reading
`roe_AC`'s tail with `tools/roe_trace.py` (which names every offset as `Block.half.Field`).

---

## Appendix G — Fuzz results, and a trap that invalidated three earlier conclusions

Current agreement, 40 random cases per accessor (`tools/fuzz_damage.py --fn <x> --seed 5`), and 120 mixed:

| accessor | agree |
|---|---|
| `roe_TH` | **40/40** |
| `roe_MinWC` | 39/40 |
| `roe_MaxWC` | 39/40 |
| `roe_AC` / `DefendPower` | 39/40 |
| `roe_MR` | 39/40 |
| `roe_TB` | 38/40 |
| **mixed, all functions** | **104/120** |

### ⚠️ `dotnet-script` caches compiled scripts BY FILENAME

This invalidated three conclusions before it was spotted. The harness reused one `_fuzz_cs.csx`, so after
every `DamageFormula.cs` fix the fuzz **re-ran the previous build** and reported no change. On that basis I
had recorded that the clamp fix "didn't help", that the MinWC/MaxWC transcription "didn't help", and that
`roe_MinWC` was stuck at 22/40. All three were measuring a stale binary. With a unique filename per run the
same code jumps to 39/40, 39/40 and 39/40.

**A measurement harness that silently serves cached results is worse than no harness**, because it produces
confident negative findings. The fuzzer now derives its script name from the DLL's mtime.

### Defects the fuzz found (all confirmed against the binary, not fitted)

1. **Each accessor reads a fixed side** — attacker gives `MinWC`/`MaxWC`/`TH`, defender gives `AC`/`TB`/`MR`.
2. **The clamp is on the CORE chain**, not both halves (all-zero returns 1, not 2).
3. **The trailing rates are applied twice** — inside the own chain and again on the sum (`roe_AC`: two,
   `roe_MR`: one, `roe_TH`/`roe_TB`: none).
4. **The intermediate is truncated to an integer between rate multiplies** in `roe_AC` (`fistp`/`fild`).
5. **The result floor is `&lt; 1`, not `&lt;= 0`.** Minimised case: sum 600 with `AbnormalState.rate.WCmax = 1`
   gives 0.6 and the server returns **1.0**; at rate 2 it gives 1.2 and the server returns **1.2**. The core
   chain's clamp and `roe_Damage`'s really are `&lt;= 0` (they compare against zero via `fldz`); this one
   compares against one via `fld1`.
6. **`Item.plus.WCmin` is scaled, not added raw** — by `WeaponTitle.rate.WCmin` and
   `PassiveSkill.rate.PhisycalWeaponMastery`, per `roe_MinWC`'s tail.

### Rejected by (fair) measurement

Truncating inside `Chain`, after the rate product: **104/120 with and without**, so it is not evidenced and
was removed. Recorded so it is not re-litigated.

### Remaining ~13%

The visible failures are extreme-value cases — e.g. `AttackPower` with `MaxWC < MinWC`, where the roll range
is negative. That is at least partly an **oracle artefact**: the `rb_largerandom` override divides unsigned,
so a negative range wraps to ~4.3e6, which the real function would not do. Constrain the generator to sane
WC ordering, or make the override use `cdq`/`idiv`, before reading anything into those.

---|---|---|
| `roe_TH` | **40/40** | exact |
| `roe_MR` | 39/40 | |
| `roe_TB` | 37/40 | |
| `roe_AC` / `DefendPower` | 35/40 | |
| `roe_MaxWC` | 27/40 | own-half model wrong |
| `roe_MinWC` | 22/40 | own-half model wrong |

### Three defects the fuzz found and fixed

1. **The trailing rates are applied TWICE.** `roe_AC`'s tail is
   `fld own; fadd core; fmul AbnormalState.rate.AC; fdiv 1000; …; fmul ItemPowerRate.rate.AC` — those two
   rates already appear *inside* the own chain and are then applied again to the sum. That is exactly why
   `ItemPowerRate.rate.AC = 2000` yields 4200 rather than 2100: the own half doubles to 2000, then
   `(100 + 2000) × 2`. `roe_MR` re-applies only `ItemPowerRate.rate.MR`; `roe_TH`/`roe_TB` re-apply nothing.
2. **The intermediate is truncated to an integer between rate multiplies** (`fistp`/`fild` round-trip). The
   port returned 3317.497 where the server returns 3317 — a half-unit error that only surfaces at the final
   `floor()`.
3. **Every accessor floors its RESULT at 1**, not just its core chain: `AbnormalState.plus.TB = -32768`
   returns 1 from the server, not a negative number.

### Rejected by measurement

Truncating inside `Chain` (after the rate product, before the flat bonuses) was tried and **reverted** — it
left `roe_TH` at 40/40 but did not move `roe_MinWC`/`roe_MaxWC` either, so it is not where the truncation
lives. Recorded so it is not tried again.

### Remaining

`roe_MinWC`/`roe_MaxWC` are the irregular pair and their own-half model is still wrong. Their tail is genuinely
different from the other four — from `tools/roe_trace.py` it builds an intermediate
`(WeaponTitle.rate.WCmin × Item.plus.WCmin / 1000) × PassiveSkill.rate.PhisycalWeaponMastery`, adds
`Upgrade.plus.WCmax`, `AbnormalState.plus.WCmax`, `PureCharParam.WCmin` and
`PassiveSkill.plus.PhisycalWeaponMastery`, and only then applies the sum rates. Transcribing that tail
term-by-term (rather than fitting it) is the next step; the fuzz will confirm or refute it immediately.
