# HANDOVER — JCQ shadow-clone combat: why the casters die

**Scope of the next session: ONE issue — the level-20 job-change instance (`Job1_Dn01`, quest q60012)
kills both casters at the shadow-clone fight.** Everything else is out of scope; the board is in
`tickets.md`.

**FIRST TASK, before any diagnosis (operator): build the TARGET VIEW** — see §6.

---

## 1. The two failures (they are DIFFERENT bugs)

### MageFresh — loses the damage race
- **183 clones spawned, 0 beaten.** (ClericFresh: 41 spawned, 20 beaten, ~49%.)
- Level 20, **maxHp 407**, class 16 Mage. No auto-attack worth using — it must cast.
- Operator watched it live: *"Stone on cd, taking big damage — not running away until stone is off cd.
  The kite is not firing or if it is, it's not good enough."*
- So the mechanism is now a **clear combat failure**, not a targeting/visibility one: it fights, lands
  swings, uses stones — and still loses. Squishy is expected; losing every time is not.
- **What to look at:** does it kite at all in the instance? The instance branch has its own combat path
  (`INSTANCE WAVE COMBAT` in `level_quest.lua`) that is deliberately NOT the field `grindStep` — check
  whether the kite/flee logic is reachable from it at all. Also: is it opening with its best-damage
  skill, and is the stone being used BEFORE it is needed rather than after.

### ClericFresh — should never die here at all
- Operator: *"The cleric should NEVER die to the clone, as it heals itself, so it can tank a tooooon of
  damage. Separate failure."*
- It is a **near-unkillable physical melee** (operator), not a squishy caster — do not reason about it as
  a caster. maxHp 462 at level 20, and it **self-heals**.
- It beats ~half its clones, so the fight is winnable; the deaths are the anomaly.
- **What to look at:** is it actually casting its heal on ITSELF during the clone fight? A previous fix
  made heals self-targeted (`ef1a687`), but verify it fires in the INSTANCE path. If it is healing and
  still dying, measure incoming DPS vs heal throughput over the fight.
- Operator's prescribed fix for the *end* of the run (clone + stone goblin adds):
  **when a goblin spawns, clear that add first, then resume the clone.**

---

## 2. What is ESTABLISHED (evidence, not theory)

| Fact | Evidence |
|---|---|
| The clone is a **copy of our own character** spawned as a **player entity** | `player appeared: MageFresh (h=17534 class=16 lvl=20 mode=1 type=4)`, then it taunts in chat |
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
