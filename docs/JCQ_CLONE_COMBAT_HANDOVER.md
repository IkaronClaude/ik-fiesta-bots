# HANDOVER — JCQ shadow-clone combat: why the Mage and Cleric die

**Scope of the next session: ONE issue — the level-20 job-change instance (`Job1_Dn01`, quest q60012)
kills both the Mage and the Cleric at the shadow-clone fight.** (The Cleric is a PHYSICAL MELEE class,
not a caster — see below.) Everything else is out of scope; the board is in
`tickets.md`.

**FIRST TASK, before any diagnosis (operator): build the TARGET VIEW** — see §6.

---

## 1. The two failures (they are DIFFERENT bugs)

### MageFresh — loses the fight; several combat levers are still wrong

- **183 clones spawned, 0 beaten.** (ClericFresh: 41 spawned, 20 beaten, ~49%.)
- Level 20, **maxHp 407**, class 16 Mage. Ranged: it must cast, not melee.
- ⚠️ **Treat what follows as LEVERS, not a diagnosis.** Several things about this fight are known to be
  wrong; none has been shown to be *the* cause, and the fight may only come good once more than one is
  fixed. Do not go looking for a single root cause to close this out.

- **Lever 1 — engagement distance against a RANGED enemy** (operator 2026-08-12): *"When a mage fights
  another ranged enemy (e.g. the clone) IT MUST NOT do melee attacks but instead stay at the maximum
  range. Otherwise when it flees, the ranged attacks still hit for 3+ seconds until it walks out of
  ranged range."*
  The reason this matters is escape distance: from max range the first step of a retreat breaks contact,
  whereas from melee the enemy keeps landing ranged hits for ~3 seconds while you walk out — the same
  window the operator watched it die in. Note the clone is a copy of OUR character, so a Mage's clone is
  itself ranged; engagement distance should consider the ENEMY's range, not only our weapon type.
- **Lever 2 — the kite** ("not firing, or if it is, it's not good enough"). Related to lever 1: a kite
  started from melee cannot create space fast enough against a ranged attacker.
- **Lever 3 — stone timing.** Operator watched it take heavy damage with the stone on cooldown and not
  retreat until it came up. Using it before it is needed, rather than after, is a different behaviour.
- **Lever 4 — skill selection/order.** Not yet examined at all: whether it opens with its best damage.
- **Where to look for lever 1:** `engageRange()` / `autoAttackAllowed()` in `level_quest.lua` already distinguish
  ranged from melee for OUR weapon, but (a) verify they are reachable from the INSTANCE combat branch —
  `INSTANCE WAVE COMBAT` deliberately bypasses the field `grindStep` and calls `bot.autoAttack()`
  directly, which CLOSES TO MELEE by design ("auto-attack closes to melee", combat notes); and (b) they
  do not consider whether the TARGET is ranged. Both need to change for this fight.


### ClericFresh — should never die here at all

- **⛔ THE CLERIC IS NOT A CASTER.** Operator, twice: it is a **near-unkillable PHYSICAL MELEE** class
  that **heals itself**, so it can tank a great deal. Do not reason about it as a squishy caster, and do
  not group it with the mage — these are two unrelated failures that happen to share a room.
- It beats ~half its clones, so the fight is winnable; the deaths are the anomaly.
- **What to look at:** is the self-heal actually firing during the clone fight? Heals were made
  self-targeted in `ef1a687`; verify that reaches the INSTANCE path. If it heals and still dies, measure
  incoming DPS against heal throughput over the fight.
- Operator's prescribed fix for the END of the run (clone + stone goblin adds):
  **when a goblin spawns, clear that add first, then resume the clone.**

## 1b. Done 2026-08-12 (this session) — and what it turned up

- **The TARGET VIEW is built** (`bfa30d2`): `GET /api/bots/{id}/target`, the same object on `/entities`,
  and rendered in `watch.html`. Handle, name, level, cur/max HP, distance, bearing, and the ANGLE off our
  facing. `asserted` (not a non-zero handle) says whether a selection exists; HP stays null until the
  entity has been hit; an unresolvable handle renders **NOT IN VIEW**. `tools/target_sample.py` (`a3b8094`)
  samples it to CSV so a whole fight can be measured rather than watched.
- **⛔ THE `change2mob` REGISTRATION ITSELF HAD TWO BUGS** (`3b9c481`), both introduced by the `b632dba` /
  `8dbbdb7` fix listed in §3 below. Neither was a theory — the bot's own log named the first one:
  > `DIED ... Killed by: Slime (Id 0)(max?). 44 dmg/s incoming vs 34 HP/s sustainable`
  1. **The clone was given `MobId = 0`, and 0 is a REAL mob — "Slime".** A level-20 clone was therefore
     drawn and reasoned about with a level-1 Slime's Level and MaxHp, its 80-damage hits were filed under
     Slime in the danger model, its chase limit came from Slime's leash, and the post-mortem told the
     operator a Slime had killed the character. The golden zero-as-sentinel rule, broken inside the fight
     we were investigating.
  2. **The clone's position never updated.** It is a player entity that `change2mob` ALSO copied into the
     mob list, and the movement handler is an `if/else if` over the two lists — player first. So every
     `SOMEONE_MOVE` updated the player record and returned, and the mob copy sat at the SPAWN POINT for the
     entire fight. Nearest-target, distance, the CHASE/MELEE thresholds and the kite were all measured
     against where the clone appeared. **This is the most likely reason lever 2 (the kite) "wasn't firing
     or wasn't good enough".**
     Fixed by not storing it: `change2mob` now only marks the handle and `NearbyNpcs` projects a live view
     out of the player list on every read. A projection cannot go stale; there is no second copy.
- **Lever 1 is implemented** (lua `eaba56c`): the instance wave branch now calls `engageRange()` /
  `autoAttackAllowed()` like the field path — it never called either, and `bot.autoAttack()` closes to
  melee by design. A caster with a rotation stops auto-attacking, backs off to its engage range when well
  inside it (unless something is already in melee on us), and the stalled-follow nudge closes to engage
  range instead of walking onto the mob.

**Not yet done:** levers 3 (stone timing) and 4 (skill order); whether the cleric's self-heal reaches the
instance path; the operator's end-of-run rule (goblin add first, then resume the clone).

### New MEASURED facts the target view produced immediately

Sampled live from ClericFresh in `Job1_Dn01`, 2026-08-12 17:21:46–17:21:53 (`tools/target_sample.py`):

| Fact | Number | Why it matters |
|---|---|---|
| The clone resolves correctly now | `kind=clone name=ClericFresh level=20 mobId=null` | It used to read as a level-1 Slime. This is the fix, live. |
| **The clone's HP is ~4900** | RestHp `4932 → 4883 → 4836 → 4761` over 7s | ⚠️ **The clone is NOT stat-identical to us** — our maxHp is 462, so it carries roughly TEN TIMES our HP. Do not reason about it as a mirror of our character's numbers, only of its class and range. |
| Cleric damage against it | ~24 dmg/s (171 over 7s) | |
| The two position copies now agree | `posDisagreeU = 0` on every sample | By construction — there is one source again. |

### ⭐ THE MAGE FIGHT, MEASURED END TO END (two full attempts, 2026-08-12)

Both attempts are in `tools/target_sample.py` output at 1 Hz. They are near-identical:

| | MageFresh (2 attempts) | ClericFresh (winning attempt) |
|---|---|---|
| Clone HP at spawn | 4460 | 5208 |
| Our damage out | **~36 dmg/s** (4460→4068 in 11s) | ~47 dmg/s (5208→3998 in 26s) |
| Damage in | **~30 dmg/s** | ~34 dmg/s |
| Our pool | 407 | 462 |
| Heals landed | 3 stones, ~+150 avg | 3 heals, ~+195 avg (~23 HP/s sustained) |
| Net HP drain | ~30 − ~14 = **~16 HP/s** | ~34 − 23 = **~11 HP/s** |
| Survived | **11 s** | 26 s and still going |
| Damage needed to the ~80% flee threshold | **~890** | ~1040 |
| Damage actually dealt before dying | **392 (44%)** | ~1210 — threshold CROSSED |

**The mage's problem is not damage — it is sustain.** Its DPS is within 25% of the cleric's; it dies in
under half the time. 30 dmg/s against a 407 pool is ~13 s of life, and the fight needs ~25.

**⛔ AND THE MAGE NEVER MOVES.** `dist` reads **113u on all 22 samples across both fights**, constant to the
unit, and `angleOffDeg` reads 0 throughout — while our HP falls 248 → 51. There is no kite, no retreat, no
reposition: it stands at one spot and trades until it dies. Lever 2 was right, and the reason is structural:
the flee/kite logic lives in the field `grindStep`, which the instance branch deliberately bypasses.

**⭐ AND THE CLONE DOES NOT REGENERATE.** Within every fight its HP is monotonically decreasing — 4460 →
4068 with no recovery, and the cleric's 5208 → 3998 likewise. It resets only because a death spawns a fresh
clone. **That makes break-contact-heal-return a WINNING strategy rather than a stalling one**: damage is
banked permanently, so the mage does not have to survive 25 continuous seconds — only to accumulate ~890
damage across as many passes as it likes. That is the single most promising lead on this ticket and it is
now measured rather than argued.

**This reframes the mage's task and should be checked before any more lever work.** The clone is not meant
to be killed: at ~80% HP it teleport-jumps away and despawns, which is what `change2npc` (type 4) reports
and what "beaten" means here. So the bar is **removing ~1000 HP**, not ~4900 — about 42 seconds at the
cleric's rate. The question for MageFresh is therefore precise and answerable: what is its damage per second
against the clone, and does 1000 HP of it fit inside the time its own 407 HP survives? Measure both with
`tools/target_sample.py` (target HP falling vs `selfHp` falling) before touching levers 3 and 4.

**First thing to check next session:** with the clone's position now live, re-measure a clone fight with
`tools/target_sample.py` and read `dist` and `angleOffDeg` over the fight. An early sample of the cleric vs
a Shadow Skeleton already showed the target at **180° off our facing** on roughly half the ticks — i.e.
directly behind us, where a 45° `UsableDegree` cast is refused. That is a lead, not yet a finding: our
facing comes from the last committed MOVE, so a retreat legitimately puts the target behind us. Establish
whether we are CASTING during those ticks before drawing any conclusion.

## 2. What is ESTABLISHED (evidence, not theory)

| Fact | Evidence |
|---|---|
| The clone is a **copy of our own character** spawned as a **player entity** — so a Mage's clone is RANGED | `player appeared: MageFresh (h=17534 class=16 lvl=20 mode=1 type=4)`, then it taunts in chat |
| **type=4 = scripted clone, type=2 = real player** | the cleric's log shows the real MageFresh in town as `type=2` |
| `change2mob` (0x6C0B type 5) = the script declaring it **fightable**; type 4 = `change2npc` = **clone beaten / leaves** | JobChange1.ps + observed 5→4 on every cleric win |
| You may only auto-attack / cast at the entity the **server currently has targeted** | operator; `BASHSTART (0x242B)` carries **no target — payload 0 bytes** |
| You cannot reach the Kebings / stone goblins **without beating the clone first** | operator |
| The mage now targets, bashes, casts and lands swings | packet log 16:46:06 — `0x2401 4F 45` → `0x242B` → `0x2440` → `0x2447`×4, `0x2448`×3 |

## 3. Fixed today (deployed) — do not re-litigate

- `b632dba` + `8dbbdb7` — **register a `change2mob` clone and force it huntable BY HANDLE.** The clone
  is a player entity with no MobId, so MobInfo could never classify it; before this the mage had
  `nearbyMobs=0` and never cast. **Casting began within a minute of this deploying** — that is the
  proof it was the blocker.
- `7f176f0` — **seed melee range from the MEAN, not the max.** Every character was pinned at 135–150u by
  one persisted position-desync outlier, making `tooClose = 0.40 × range ≈ 60u`, so melee bots **retreated
  out of melee** (`TOO CLOSE (17u) — stepping BACK to ~104u`). Mean is ~50u across all bots and matches
  the real-client capture (auto-attack median 49u).
- `fae3715` — **an unexplained HP drop counts as combat.** `InCombat` came only from `SWING_DAMAGE`
  naming us defender, so scripted/DOT damage left the bot at 64/407 with `inCombat=false`, never healing
  or fleeing. **This is directly relevant to the kite not firing — check it is now reaching the flee path.**
- `f484dcb` — re-assert TARGETTING when the server may have dropped the selection. **Narrow** (handle
  reuse only); not a general fix.

## 4. Tooling built today — USE IT, it exists because of this bug

- **`GET /api/bots/{id}/events`** — typed events + per-kind counts and gap p50/p90. Kinds wired: `relog`,
  `disconnect`, `zone-enter`, `phase`, `castfail`, `damage-unattributed`.
- **`GET /api/bots/{id}/phases`** — RAW array of every phase change (`phase`, `startedUtc`, `seconds`).
  Derive metrics with a script. A cumulative rollup CANNOT tell one long stall from hundreds of retries.
- **Packet logs carry our own handle**: `==== self handle N (Char) ====` per zone-enter. Never infer it.
- **`[castfail]` at Note level** carries `dist@cast` / `dist@fail` / `mobMoved` / `weMoved` / `after=Nms`.
- **Bot logs persist** to `/app/bot-knowledge/roster/logs/<id>.log{,.1..9}` and are restored into the ring
  on spawn — history survives restarts and stopped bots.
- `tools/client_range.py`, `tools/dead_bash.py`, `tools/combat_timeline.py`.

## 5. ⛔ Process rules, learned expensively today

1. **Read the log CHRONOLOGICALLY around the moment, unfiltered.** Every wrong call came from grepping
   for a pattern and generalising. The one time 60 lines were read in order, the answer was obvious.
2. **Check deploy time against observation time before rewriting a conclusion.** "It's casting now" was
   read as "my earlier count was stale" when in fact the fix had just landed. A correct diagnosis was
   retracted for want of one timestamp comparison.
3. **Hold a conclusion the evidence supports; ask for confirmation instead of folding.** "Storage is
   full" was correct, abandoned under pushback, then confirmed by the operator's own UI.
4. **Never infer identity that the bot already knows** (self handle, current target, current map).
5. **Statistics need their denominator checked** — a "90% dead bash" rate was computed against the wrong
   entity handle and had to be fully retracted.

## 6. FIRST TASK — build the TARGET VIEW (operator)

Surface, for the currently-targeted entity: **handle, name, level, current/max HP, distance, and the
angle between our facing and the bearing to it.** A partial version exists (`watch.html` target row +
`Self.Target`/`Self.Facing` in `EntityPanel`) — finish it properly:

- **level and HP** are the ones that decide the damage race, and neither is shown yet. Mob HP is `null`
  until the entity has been hit — absent must render as *unknown*, never as full.
- **angle** matters because a cast is refused outside `UsableDegree` (45° for most melee skills), and it
  is the one geometric fact never surfaced anywhere.
- Show it even when the entity is **not in view** — "targeting a handle we cannot see" is itself the
  diagnosis.
- The same numbers should be queryable, not just drawn, so a script can measure a whole clone fight.
