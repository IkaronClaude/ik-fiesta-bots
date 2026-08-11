# COMBAT BIBLE — what the wire says about fighting in Fiesta

Everything here is **measured**, from `Z:/LongCaptureNoDc.pcapng` (1 hour of a real Fighter) and from the
bots' own packet logs. Reproduce any of it with:

```
python tools/combat_timeline.py --pcap Z:/LongCaptureNoDc.pcapng --fight 305
python tools/combat_timeline.py --botlog packets-JcqFresh.log --auto
```

Claims are labelled **MEASURED** (from data), **PDB** (from `Z:/ClientSource/Fiesta.pdb`) or
**THEORY** (plausible, not yet proven). Do not promote a THEORY without evidence.

---

## 1. The packets

| opcode | dir | name | layout |
|---|---|---|---|
| `0x2401` | C→ | `NC_BAT_TARGETTING_REQ` | `handle u16` |
| `0x242B` | C→ | `NC_BAT_BASHSTART_CMD` | empty — starts the auto-attack stream |
| `0x2440` | C→ | `NC_BAT_SKILLBASH_OBJ_CAST_REQ` | **PDB** `skill u16 @0, target u16 @2` |
| `0x2434` | S← | cast FAILED | `err u16` — `0xFC8` on cooldown · `0xFCA` precondition (moving / no STOP) · `0xFCD`, `0xFC6` |
| `0x244E` | S← | `NC_BAT_SKILLBASH_HIT_OBJ_START_CMD` | **PDB** `skill u16 @0, targetobj u16 @2, index u16 @4` — the cast STARTED, and names which skill |
| `0x2047` | S← | `NC_ACT_CREATECASTBAR` | `millisec u16` — only for skills that HAVE a cast time |
| `0x2435` | S← | (unnamed, dept 9 cmd 53, len 0) | fires ~once per SUCCESSFUL cast — **THEORY: cast finished** |
| `0x2447` | S← | `NC_BAT_SWING_START_CMD` | `attacker u16, defender u16` |
| `0x2448` | S← | `NC_BAT_SWING_DAMAGE_CMD` | see below |
| `0x2452` | S← | `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` | see below |
| `0x243D` | S← | `NC_BAT_CEASE_FIRE_CMD` | the swing stream was interrupted |

### `NC_BAT_SWING_DAMAGE_CMD` (0x2448, 16B) — **PDB**
`attacker u16 @0 · defender u16 @2 · flag u16 @4 · damage u16 @6 · resthp u32 @8 ·
hpchangeorder u16 @12 · damageindex u8 @14 · attacksequence u8 @15`

flag bits, byte0: `iscritical · isresist · ismissed · isshieldblock · isCostumCharged · isDead ·
isDamege2Heal · isImmune`; byte1: `isCostumShieldCharged`.

### `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` (0x2452)
Header **on the wire**: `index u16 @0 · caster u16 @2 · targetnum u8 @4`, then `SkillDamage[]` **at @5**.

⛔ The PDB lists `kSkillID` and `pTarget` between `targetnum` and the array — **they are NOT serialised**.
Every frame is 19 bytes with `targetnum=1`, and `5 + 14 = 19`. Following the PDB's field order shifts
everything 4 bytes and silently reports **zero skill damage for entire fights**.

`SkillDamage` (14B): `handle u16 @0 · flag u16 @2 · hpchange u32 @4 · resthp u32 @8 · hpchangeorder u16 @12`
flag bits, byte0: `isdamage · iscritical · ismissed · isshieldblock · isheal · isenchant · isresist ·
IsCostumWeapon`; byte1: `isDead · isImmune · IsCostumShield`.

⚠️ **The two flag bitfields are DIFFERENT ORDERS.** Only `ismissed` (bit2) and `isshieldblock` (bit3)
coincide — enough coincidence to make a wrong decoder look right, while a crit reads as "isdamage" and a
killing blow as "isenchant".

### Traps that cost a full session
1. **`@` offsets in a pcap decode are PER-DIRECTION, not a clock.** `S<- @19389` and the `C-> @484` that
   ANSWERS it are on different baselines. `pcap_decode.py` interleaves by timestamp by default → **file
   order is the chronology**. Windowing on `@` drops every client frame and yields "the client sent
   nothing", for a fight containing 38 client frames.
2. **`NC_BRIEFINFO_BRIEFINFODELETE_CMD` is not death** (also fires on leaving AoI) and its field is
   **`hnd`**, not `handle`. Death is **`resthp == 0`** (or the `isDead` flag).
3. **Validation that catches all of it:** damage dealt to a mob must equal its `MobInfo.MaxHP`.
   Blue Crab 140/140 · Gang Imp 123/123 · Mutant Wolf 1462/1462 · Hungry Wolf 171/171 · Ratman 272/272.

---

## 2. What a real player's combat looks like — **MEASURED**, 1 hour, a Fighter (all physical)

| metric | value |
|---|---|
| skill casts sent | 200 |
| **cast FAILURES** | **1** (and it is `0xFC6`, not a cooldown) |
| BASHSTART sent | 53 |
| SWING_START | 770 → **~14.5 swings per engage** |
| **landed swings per BASHSTART** | **5.96 (median 4, max 18)** |
| bashes producing ZERO damage | **4 %** |
| CEASE_FIRE | 516 — **routine, not failure** |
| swings resume after cease-fire **with no new bash** | **46 %** |
| HP stone uses | 51 (~1 per 70 s), **0 during the fights sampled** |
| loot picks | 208 |
| crit rate / miss rate | 10 crits (avg 40.1 dmg vs 21.9 normal) · 16.2 % misses |

**The client never sends a doomed cast.** Zero `0xFC8` in an hour ⇒ the client gates cooldowns locally
from `ActiveSkill.DelayTime` and simply does not transmit. Pressing a key on cooldown does nothing.

Fight shapes (group-aware, first hit → all dead):
* Blue Crab (lvl8 140hp) — **solo, 1.3 s**, we take 87. Crabs do not group.
* Gang Imp (lvl7) + 2 Bored Imp (lvl5 90hp) — **4.8 s**, we take 371.
* Mutant Wolf (lvl15 1462hp) + 3 Hungry Wolf (lvl10 171hp) — **14.5 s**, we take 2997.

Two behavioural facts that contradict how the driver reasons:
* **Multi-aggro is normal** — six attackers on the *easy* mob.
* **The adds do most of the damage.** Imp group: 147 from the named imp, **224 from the two adds**, which
  the player hit 9 times total. Attack what is hitting you, not only the quest target.

---

## 3. What the bot does wrong — **MEASURED** (JcqFresh, lvl 26)

| metric | bot | player |
|---|---|---|
| skill casts | 199 | 200 |
| **cast failures** | **104 (52 %)** — 57×`0xFC8`, 44×`0xFCA` | 1 |
| BASHSTART | 150 | 53 |
| **landed swings per bash** | **0.20** (median 0) | 5.96 |
| **bashes producing ZERO** | **81 %** | 4 % |
| swings landed / taken | 30 / 451 | 370 / 270 |
| resume after cease-fire without re-bash | 12 % | 46 % |
| **re-bashed before the stream could resume** | **80 %**, median **355 ms** | 33 % |

### The three self-inflicted causes — **MEASURED** from a packet-level diff

A good player bash vs a dead bot bash, same tool, same window:

```
PLAYER (41 swings followed)          BOT (0 swings)
+0 C-> BASHSTART                       +0ms C-> BASHSTART
+1 S<- SWING_START US   ← swings first  +4ms C-> TARGET + STOP + SKILL_CAST
+2 C-> STOP                            +11ms C-> TARGET + STOP + SKILL_CAST   ← a SECOND bracket
+3 C-> SKILL_CAST       ← one cast     +16ms S<- CEASE_FIRE
+5 S<- CEASE_FIRE       ← still fine   +49ms S<- CEASE_FIRE
```

1. **We cast ~4 ms after BASHSTART.** First damage lands a median **418 ms** after a bash (the swing
   windup), so casting immediately guarantees the swing never happens. The player casts *after*
   `SWING_START` is acknowledged.
2. **We re-send `TARGET` before every cast.** The player does not (`STOP` → `SKILL_CAST`).
3. **We re-bash at a median 355 ms**, inside the windup — interrupting a stream that, for the player,
   recovers by itself 46 % of the time. The RE-BASH guard waits 1200 ms, so the early bash is coming from
   the ordinary engage path, not from RE-BASH.

⚠️ **NOT established:** that a *failed* cast breaks the bash. The player has no failed casts, so the
capture cannot say. Do not assert it.

---

## 4. The cooldown model to implement

```
C-> 0x2440 CAST_REQ (skill, target)
S←  0x2434 err 0xFC8   → the skill was NOT ready; nothing started
S←  0x244E HIT_OBJ_START (skill, targetobj, index)   ← the cast STARTED; names the skill
S←  0x2047 CREATECASTBAR (millisec)                  ← only if it has a cast time
S←  0x2435 (len 0)                                   ← THEORY: cast FINISHED
S←  0x2452 SKILLBASH_HIT_DAMAGE (index ties to 0x244E)
```

**Cooldown starts when the cast FINISHES, not when it is requested** — for a cast-time skill, after the
cast time elapses (operator). Evidence for `0x2435` being that finish event: it fires **199 times for the
player's 199 successful casts** and **92 times for the bot's ~95**. ⚠️ Still **THEORY** — the operator does
not fully trust it yet; the count correlation is strong but the payload is empty, so it is inferred from
timing and pairing with the preceding `0x244E`, not from a decoded field.

Consequences: a failed cast produces no `0x244E`/`0x2435`, so it must never start a timer. Gate casts on
`ActiveSkill.DelayTime` measured from the finish event, exactly as the real client does.

---

## 5. Expected residual noise

Small blips are expected and are NOT regressions (operator): a stun/root **abstate** or being briefly out
of range will interrupt a stream. These barely appear in the player capture because it was taken at a
level/zone where those mobs do not apply them. Judge a fix on **swings per bash** and **cast-failure
rate**, not on the absence of every cease-fire.

---

## 6. Targets to hit

| metric | now | target |
|---|---|---|
| landed swings per BASHSTART | 0.20 | **≥ 3** (player 5.96) |
| bashes producing zero damage | 81 % | **< 25 %** (player 4 %) |
| cast failures | 52 % | **< 5 %** (player 0.5 %) |
| swings landed : taken | 1 : 15 | **≥ 1 : 2** (player 1.4 : 1) |
